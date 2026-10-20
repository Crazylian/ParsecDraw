using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ParsecDraw.Win32;

namespace ParsecDraw.Ui;

/// <summary>
/// 全屏透明覆盖层。
/// 主机端：恒定点击穿透（画笔纯覆盖层，绝不向游戏注入输入），Ctrl+Alt+D 切换显示/隐藏（暂停笔迹画面）。
/// 观众端：绘画态（捕获鼠标画线）⇄ 观看态（穿透，输入落给底下 Parsec 窗口），Ctrl+Alt+D / 右键切换。
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
        Closed += (_, _) => ReleaseHotkeys();
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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        Canvas.Init(_session.Store);

        // 主机：恒穿透；观众：初始即绘画态（连上就想画）
        _drawMode = !_isHost;
        ApplyClickThrough();

        RegisterHotkeys();
    }

    private void ApplyClickThrough()
        => WinApi.SetClickThrough(_hwnd, clickThrough: _isHost || !_drawMode, noActivate: true);

    public void SetDrawMode(bool on)
    {
        if (_isHost || _drawMode == on) return;
        _drawMode = on;
        ApplyClickThrough();
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
        _session.StrokeBegin(e.GetPosition(this));
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_session.HasActiveStroke) return;
        _session.StrokeMove(e.GetPosition(this));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_session.HasActiveStroke || e.ChangedButton != MouseButton.Left) return;
        _session.StrokeEnd();
        ReleaseMouseCapture();
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
