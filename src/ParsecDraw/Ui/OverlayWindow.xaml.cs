using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ParsecDraw.Protocol;
using ParsecDraw.Win32;

namespace ParsecDraw.Ui;

/// <summary>
/// 全屏透明覆盖层。
/// 主机端：恒定点击穿透（画笔纯覆盖层，绝不向游戏注入输入），Ctrl+Alt+D 切换显示/隐藏（暂停笔迹画面）。
/// 观众端：绘画态（捕获鼠标画线）⇄ 观看态（穿透，输入落给底下 Parsec 窗口），Ctrl+Alt+D / 右键切换。
/// 坐标经映射矩形归一化：观众端 = Parsec 视频画面区域（自动黑边补偿，见 <see cref="AlignmentTracker"/>），
/// 主机端 = 整屏恒等。
/// </summary>
public partial class OverlayWindow : Window
{
    private const int HotkeyIdToggle = 1;
    private const int HotkeyIdClear = 2;
    private const int HotkeyIdPin = 3;

    /// <summary>绘画态画笔光标（tools/make_cursor.py 生成，热点=笔尖）。</summary>
    private static readonly Cursor PenCursor = new(
        Application.GetResourceStream(new Uri("pack://application:,,,/Assets/pen.cur"))!.Stream);

    /// <summary>橡皮光标（tools/make_cursor.py --eraser 生成）。</summary>
    private static readonly Cursor EraserCursor = new(
        Application.GetResourceStream(new Uri("pack://application:,,,/Assets/eraser.cur"))!.Stream);

    private readonly Session _session;
    private readonly bool _isHost;
    private IntPtr _hwnd;
    private bool _drawMode; // 观众端：当前是否处于绘画态
    private bool _erasing;  // 橡皮拖动擦除中

    private Rect _map = Rect.Empty;                 // [0,1] 归一化坐标 ⇄ 屏幕区域的映射矩形（DIP）
    private AlignmentTracker? _tracker;             // 观众端对齐追踪
    private DispatcherTimer? _guideFadeTimer;
    private KeyboardHook? _keys;                    // 低级键盘钩子（Parsec 焦点下也能收到热键）

    /// <summary>绘画/观看态变化（工具栏指示灯用）。</summary>
    public event Action<bool>? DrawModeChanged;

    public OverlayWindow(Session session, bool isHost)
    {
        InitializeComponent();
        _session = session;
        _isHost = isHost;

        // 覆盖主显示器全屏（DIP 坐标系）
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;

        Loaded += OnLoaded;
        Closed += (_, _) => { _keys?.Dispose(); _tracker?.Dispose(); };
        // 鼠标输入不走 WPF 事件，直接在 WndProc 处理原始消息：
        // 透明覆盖层的命中测试怪癖全部绕开，且可用 PostMessage 无头自测。
    }

    public bool DrawMode => _drawMode;

    /// <summary>当前映射矩形（冒烟测试观测用）。</summary>
    internal Rect MapRectForSmoke => _map;

    /// <summary>对齐追踪实际执行过的窗口重扫次数（冒烟测试观测用）。</summary>
    internal int AlignmentRescansForSmoke => _tracker?.RescansForSmoke ?? -1;

    /// <summary>覆盖层窗口句柄（冒烟测试合成输入用）。</summary>
    internal IntPtr HwndForSmoke => _hwnd;

    /// <summary>低级键盘钩子安装成功（冒烟测试观测用）。</summary>
    internal bool HotkeyHookActiveForSmoke => _keys?.Active ?? false;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        Canvas.Init(_session.Store);
        ApplyMap(new Rect(0, 0, ActualWidth, ActualHeight)); // 初始：整屏（v0.1 行为）

        if (!_isHost)
        {
            // 观众端：收到主机分辨率 → 追踪 Parsec 窗口算视频区域。
            // 先补拉缓存：分辨率单播可能在覆盖层创建前就已到达（快速连接/重连场景）
            _tracker = new AlignmentTracker(new Size(ActualWidth, ActualHeight));
            _tracker.MapChanged += r => ApplyMap(r);
            _session.HostInfo += OnHostInfo;
            var cached = _session.LastHostInfo;
            if (cached.W > 0 && cached.H > 0)
                _tracker.SetHostSize(cached.W, cached.H);
            _tracker.Start();
        }

        // 主机：恒穿透；观众：初始即绘画态（连上就想画）
        _drawMode = !_isHost;
        ApplyClickThrough();
        UpdateToolCursor();
        // 工具栏在 SourceInitialized（早于 Loaded）时创建，读到的是默认 false——
        // 初始化完补发一次事件，让它的"绘画中/观看中"与实际状态同步（老 bug）
        DrawModeChanged?.Invoke(_drawMode);

        RegisterHotkeys();
    }

    /// <summary>低级键盘钩子接管热键（RegisterHotKey 在 Parsec 捕获键盘时收不到）。</summary>
    private void RegisterHotkeys()
    {
        _keys = new KeyboardHook();
        if (!_keys.Active) return; // 极端失败：工具栏按钮仍可用
        _keys.Hotkey += HandleHotkey;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
    }

    private void HandleHotkey(uint vk)
    {
        switch (vk)
        {
            case 0x44: // D
                if (_isHost) ToggleVisibility();
                else ToggleDrawMode();
                break;
            case 0x43: // C
                _session.ClearAll();
                break;
            case 0x50: // P
                _session.TogglePinMode(); // 钉住模式开关：开启后新画的笔迹持久存在
                break;
        }
    }

    private void OnHostInfo(double w, double h)
        => Dispatcher.BeginInvoke(() => _tracker?.SetHostSize(w, h));

    /// <summary>更新映射矩形（渲染+输入一起走它），并闪现参考框。</summary>
    private void ApplyMap(Rect r)
    {
        _map = r;
        _session.Store.SetMap(r);
        MapGuide.Margin = new Thickness(r.Left, r.Top, Math.Max(0, ActualWidth - r.Right), Math.Max(0, ActualHeight - r.Bottom));
        FlashGuide();
    }

    private void FlashGuide()
    {
        MapGuide.Opacity = 0.85;
        _guideFadeTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1.6), DispatcherPriority.Render, (_, _) =>
        {
            MapGuide.Opacity = 0;
            _guideFadeTimer!.Stop();
        }, Dispatcher);
        _guideFadeTimer.Stop();
        _guideFadeTimer.Start();
    }

    private void ApplyClickThrough()
        => WinApi.SetClickThrough(_hwnd, clickThrough: _isHost || !_drawMode, noActivate: true);

    public void SetDrawMode(bool on)
    {
        if (_isHost || _drawMode == on) return;
        _drawMode = on;
        ApplyClickThrough();
        // 绘画态用画笔/橡皮造型光标，让"现在能画"一眼可见（观看态穿透，光标归下层窗口）
        UpdateToolCursor();
        if (on) FlashGuide(); // 进绘画态时提示可画区域
        DrawModeChanged?.Invoke(on);
    }

    public void ToggleDrawMode() => SetDrawMode(!_drawMode);

    /// <summary>主机端暂停/恢复显示笔迹（隐藏覆盖层 = 观众屏上看不到）。</summary>
    public void ToggleVisibility()
    {
        Visibility = Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
    }

    // ---- 输入（原始消息，WndProc 分发） ----

    private void HandleMouseMessage(uint msg, IntPtr lParam)
    {
        // lParam 为客户区物理像素坐标，转 DIP 后再做归一化
        var px = new System.Windows.Point(
            (short)(lParam.ToInt64() & 0xFFFF),
            (short)((lParam.ToInt64() >> 16) & 0xFFFF));
        var dip = PresentationSource.FromVisual(this)?.CompositionTarget is { } ct
            ? ct.TransformFromDevice.Transform(px)
            : px;

        switch (msg)
        {
            case 0x0201: // WM_LBUTTONDOWN
                if (!_isHost && _drawMode)
                {
                    if (_session.Tool == ToolKind.Eraser)
                    {
                        _erasing = true;
                        _session.EraseAt(dip);
                    }
                    else
                    {
                        _session.StrokeBegin(ToNormalized(dip));
                    }
                }
                break;
            case 0x0200: // WM_MOUSEMOVE
                if (_erasing)
                    _session.EraseAt(dip);
                else if (_session.HasActiveStroke)
                    _session.StrokeMove(ToNormalized(dip));
                break;
            case 0x0202: // WM_LBUTTONUP
                _erasing = false;
                if (_session.HasActiveStroke)
                    _session.StrokeEnd();
                break;
            case 0x0205: // WM_RBUTTONUP
                if (!_isHost) SetDrawMode(false); // 右键快速退出绘画态
                break;
        }
    }

    /// <summary>当前工具对应的绘画态光标（画笔/橡皮）。</summary>
    public void UpdateToolCursor()
        => Cursor = _drawMode
            ? (_session.Tool == ToolKind.Eraser ? EraserCursor : PenCursor)
            : Cursors.Arrow;

    /// <summary>屏幕 DIP → [0,1] 归一化（按映射矩形；越界钳到边界）。</summary>
    private Point ToNormalized(Point p)
    {
        var w = _map.Width > 0 ? _map.Width : Math.Max(ActualWidth, 1);
        var h = _map.Height > 0 ? _map.Height : Math.Max(ActualHeight, 1);
        return new Point(
            Math.Clamp((p.X - _map.X) / w, 0, 1),
            Math.Clamp((p.Y - _map.Y) / h, 0, 1));
    }

    // ---- 全局热键 ----

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WinApi.WM_HOTKEY)
        {
            // 冒烟测试投递路径（id → vk），与真实钩子路径汇合
            HandleHotkey(wParam.ToInt32() switch
            {
                HotkeyIdToggle => 0x44,
                HotkeyIdClear => 0x43,
                HotkeyIdPin => 0x50,
                _ => 0,
            });
            handled = true;
        }
        else if (!_isHost && msg is 0x0200 or 0x0201 or 0x0202 or 0x0205)
        {
            HandleMouseMessage((uint)msg, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }
}
