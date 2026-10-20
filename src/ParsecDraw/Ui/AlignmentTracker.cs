using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using ParsecDraw.Win32;

namespace ParsecDraw.Ui;

/// <summary>letterbox 数学：把宽高比 aspect 的内容居中放进 area，返回内容实际占据的矩形。</summary>
internal static class AlignmentMath
{
    public static Rect FitInto(double areaX, double areaY, double areaW, double areaH, double aspect)
    {
        if (areaW <= 0 || areaH <= 0 || aspect <= 0 || double.IsNaN(aspect))
            return new Rect(areaX, areaY, Math.Max(areaW, 0), Math.Max(areaH, 0));
        double w, h;
        if (areaW / areaH > aspect)
        {
            h = areaH;
            w = h * aspect;
        }
        else
        {
            w = areaW;
            h = w / aspect;
        }
        return new Rect(areaX + (areaW - w) / 2, areaY + (areaH - h) / 2, w, h);
    }
}

/// <summary>
/// 观众端对齐追踪：算出"主机视频画面在本机屏幕上的实际区域"（DIP，覆盖层窗口坐标系）。
/// 路线：找到 Parsec 窗口 → 取客户区 → 按主机屏幕宽高比做 letterbox 数学分出视频区（Parsec 黑边补偿）；
/// 找不到 Parsec 窗口时按整屏 letterbox（覆盖全屏 Parsec 的场景）；
/// 主机分辨率未知（未收到 host 消息）时退化为 v0.1 的整屏恒等映射。
/// 500ms 轮询，窗口丢失后 2s 节流重扫；找到后仅查缓存的 hwnd（微秒级）。
/// </summary>
internal sealed class AlignmentTracker : IDisposable
{
    private const int PollMs = 500;
    private const int RescanThrottleMs = 2000;
    private const double MinVideoDip = 100; // 客户区小于此值视为无效（窗口正在关闭/最小化）

    private readonly DispatcherTimer _timer;
    private readonly Size _viewport;          // 覆盖层尺寸（DIP）
    private readonly int _selfPid = Environment.ProcessId;

    private double _hostW, _hostH;            // 主机屏幕（物理 px），0 = 未知
    private IntPtr _parsecHwnd;
    private long _nextRescanAt;
    private Rect _lastMap = Rect.Empty;
    private readonly Dictionary<int, string?> _pidNames = new();

    /// <summary>映射矩形变化（DIP，覆盖层局部坐标）。仅在变化时触发。</summary>
    public event Action<Rect>? MapChanged;

    public AlignmentTracker(Size viewport)
    {
        _viewport = viewport;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(PollMs), DispatcherPriority.Background, (_, _) => Recompute(), Dispatcher.CurrentDispatcher);
    }

    public void Start()
    {
        Recompute();
        _timer.Start();
    }

    /// <summary>收到主机屏幕分辨率通告。</summary>
    public void SetHostSize(double w, double h)
    {
        if (w <= 0 || h <= 0) return;
        if (w == _hostW && h == _hostH) return;
        _hostW = w;
        _hostH = h;
        Recompute();
    }

    public void Dispose() => _timer.Stop();

    private void Recompute()
    {
        var map = ComputeMap();
        if (map == _lastMap) return;
        _lastMap = map;
        MapChanged?.Invoke(map);
    }

    private Rect ComputeMap()
    {
        var aspect = _hostW > 0 && _hostH > 0 ? _hostW / _hostH : 0;
        if (aspect <= 0)
            return new Rect(0, 0, _viewport.Width, _viewport.Height); // 未知 → v0.1 恒等映射

        var area = FindParsecClientArea();
        if (area is null)
            return AlignmentMath.FitInto(0, 0, _viewport.Width, _viewport.Height, aspect); // 整屏 letterbox
        return AlignmentMath.FitInto(area.Value.X, area.Value.Y, area.Value.Width, area.Value.Height, aspect);
    }

    /// <summary>Parsec 窗口客户区（DIP，屏幕坐标 = 覆盖层局部坐标，覆盖层在主屏原点）。</summary>
    private Rect? FindParsecClientArea()
    {
        var now = Environment.TickCount64;
        if (!WinApi.IsWindowAlive(_parsecHwnd))
            _parsecHwnd = IntPtr.Zero;
        if (_parsecHwnd == IntPtr.Zero)
        {
            if (now < _nextRescanAt) return null;
            _nextRescanAt = now + RescanThrottleMs;
            _parsecHwnd = FindParsecWindow();
            if (_parsecHwnd == IntPtr.Zero) return null;
        }
        if (!WinApi.IsShown(_parsecHwnd))
            return null; // 最小化/隐藏：与找不到窗口同等对待

        if (!WinApi.TryGetClientRect(_parsecHwnd, out var rc) || rc.Width < 10 || rc.Height < 10)
            return null;

        var pt = new WinApi.POINT();
        if (!WinApi.TryClientToScreen(_parsecHwnd, ref pt))
            return null;

        var dpi = WinApi.GetWindowDpi(_parsecHwnd);
        if (dpi == 0) dpi = 96;
        var k = 96.0 / dpi;
        var x = pt.X * k;
        var y = pt.Y * k;
        var w = rc.Width * k;
        var h = rc.Height * k;

        // 与覆盖层（主屏）求交：Parsec 窗口在别的显示器上时放弃窗口跟踪
        var inter = Rect.Intersect(
            new Rect(x, y, w, h),
            new Rect(0, 0, _viewport.Width, _viewport.Height));
        if (inter.IsEmpty || inter.Width < MinVideoDip || inter.Height < MinVideoDip)
            return null;
        return inter;
    }

    /// <summary>找 Parsec 进程下客户区最大的可见顶层窗口（类名/标题无关）。</summary>
    private IntPtr FindParsecWindow()
    {
        IntPtr best = IntPtr.Zero;
        double bestArea = 0;
        foreach (var h in WinApi.EnumTopLevelWindows())
        {
            if (!WinApi.IsShown(h)) continue;
            var pid = WinApi.WindowPid(h);
            if (pid == 0 || pid == _selfPid) continue;
            if (ProcNameOf((int)pid)?.Contains("parsec", StringComparison.OrdinalIgnoreCase) != true)
                continue;
            if (!WinApi.TryGetClientRect(h, out var rc) || rc.Width < 50 || rc.Height < 50)
                continue;
            var area = rc.Width * rc.Height;
            if (area > bestArea)
            {
                bestArea = area;
                best = h;
            }
        }
        return best;
    }

    private string? ProcNameOf(int pid)
    {
        if (_pidNames.TryGetValue(pid, out var cached)) return cached;
        string? name = null;
        try { name = Process.GetProcessById(pid).ProcessName; }
        catch { /* 进程已退出或权限不足 */ }
        if (_pidNames.Count > 512) _pidNames.Clear();
        _pidNames[pid] = name;
        return name;
    }
}
