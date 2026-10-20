using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
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

    private readonly Session _session;
    private readonly bool _isHost;
    private IntPtr _hwnd;
    private bool _drawMode; // 观众端：当前是否处于绘画态

    private Rect _map = Rect.Empty;                 // [0,1] 归一化坐标 ⇄ 屏幕区域的映射矩形（DIP）
    private AlignmentTracker? _tracker;             // 观众端对齐追踪
    private DispatcherTimer? _guideFadeTimer;

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
        Closed += (_, _) => { ReleaseHotkeys(); _tracker?.Dispose(); };
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        LostMouseCapture += (_, _) => _session.StrokeEnd();
        MouseRightButtonUp += (_, _) =>
        {
            if (!_isHost) SetDrawMode(false); // 右键快速退出绘画态
        };
    }

    public bool DrawMode => _drawMode;

    /// <summary>当前映射矩形（冒烟测试观测用）。</summary>
    internal Rect MapRectForSmoke => _map;

    /// <summary>对齐追踪实际执行过的窗口重扫次数（冒烟测试观测用）。</summary>
    internal int AlignmentRescansForSmoke => _tracker?.RescansForSmoke ?? -1;

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

        RegisterHotkeys();
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
        if (on) FlashGuide(); // 进绘画态时提示可画区域
        DrawModeChanged?.Invoke(on);
    }

    public void ToggleDrawMode() => SetDrawMode(!_drawMode);

    /// <summary>主机端暂停/恢复显示笔迹（隐藏覆盖层 = 观众屏上看不到）。</summary>
    public void ToggleVisibility()
    {
        Visibility = Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
    }

    // ---- 输入 ----

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_isHost || !_drawMode || e.ChangedButton != MouseButton.Left) return;
        _session.StrokeBegin(ToNormalized(e.GetPosition(this)));
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_session.HasActiveStroke) return;
        _session.StrokeMove(ToNormalized(e.GetPosition(this)));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_session.HasActiveStroke || e.ChangedButton != MouseButton.Left) return;
        _session.StrokeEnd();
        ReleaseMouseCapture();
    }

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

    private void RegisterHotkeys()
    {
        WinApi.TryRegisterHotKey(_hwnd, HotkeyIdToggle, WinApi.MOD_CONTROL | WinApi.MOD_ALT, WinApi.VK_D);
        WinApi.TryRegisterHotKey(_hwnd, HotkeyIdClear, WinApi.MOD_CONTROL | WinApi.MOD_ALT, WinApi.VK_C);
        WinApi.TryRegisterHotKey(_hwnd, HotkeyIdPin, WinApi.MOD_CONTROL | WinApi.MOD_ALT, WinApi.VK_P);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
    }

    private void ReleaseHotkeys()
    {
        if (_hwnd == IntPtr.Zero) return;
        WinApi.UnregisterHotKeyId(_hwnd, HotkeyIdToggle);
        WinApi.UnregisterHotKeyId(_hwnd, HotkeyIdClear);
        WinApi.UnregisterHotKeyId(_hwnd, HotkeyIdPin);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WinApi.WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case HotkeyIdToggle:
                    if (_isHost) ToggleVisibility();
                    else ToggleDrawMode();
                    handled = true;
                    break;
                case HotkeyIdClear:
                    _session.ClearAll();
                    handled = true;
                    break;
                case HotkeyIdPin:
                    _session.PinAll();
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }
}
