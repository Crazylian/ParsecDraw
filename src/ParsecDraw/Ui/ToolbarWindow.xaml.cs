using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ParsecDraw.Draw;
using ParsecDraw.Protocol;

namespace ParsecDraw.Ui;

/// <summary>
/// 悬浮迷你工具栏：置顶、可拖动、永不点击穿透（z 序压在覆盖层之上）。
/// </summary>
public partial class ToolbarWindow : Window
{
    private static readonly string[] ColorChoices = { "#FFFFFF", "#FF4757", "#FFD32A", "#2ED573", "#4DA3FF" };
    private static readonly double[] WidthChoices = { 3, 6, 12 };

    private readonly Session _session;
    private readonly OverlayWindow _overlay;
    private readonly bool _isHost;

    private Button? _selectedColor;
    private Button? _selectedWidth;
    private bool _paused;

    // ---- 靠边自动缩入 ----
    private const double DockZone = 14;      // 距顶边多近算贴边
    private const double DockPeek = 12;      // 缩入后露出多少像素
    private bool _docked;
    private bool _dragging;
    private bool _animating;                 // 滑动动画进行中：忽略合成的 Enter/Leave
    private DispatcherTimer? _retractTimer;

    public ToolbarWindow(Session session, OverlayWindow overlay, bool isHost)
    {
        InitializeComponent();
        _session = session;
        _overlay = overlay;
        _isHost = isHost;

        // 屏幕顶部中央
        Loaded += (_, _) =>
        {
            Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
            Top = 12;
        };

        if (isHost)
        {
            // 主机不能画：隐藏绘制工具，显示状态
            ModeText.Text = "主机";
            DrawTools.Visibility = Visibility.Collapsed;
            BtnExitDraw.Visibility = Visibility.Collapsed;
            HintText.Text = "覆盖层已点击穿透，不影响游戏。Ctrl+Alt+D 暂停/显示笔迹，Ctrl+Alt+P 钉住模式，Ctrl+Alt+C 清空";
            _overlay.Visibility = Visibility.Visible;
        }
        else
        {
            BtnToggle.Visibility = Visibility.Collapsed;
            BuildColorButtons();
            BuildWidthButtons();
            SetTool(_session.Tool);
            _overlay.DrawModeChanged += OnDrawModeChanged;
            _session.PinModeChanged += OnPinModeChanged;
            UpdateModeUi(_overlay.DrawMode);
            UpdatePinUi();
        }
    }

    // ---- 钉住模式（开关：开启后新笔迹持久存在） ----

    private void OnPinModeChanged() => Dispatcher.BeginInvoke(UpdatePinUi);

    private void UpdatePinUi()
    {
        BtnPin.Content = _session.PinMode ? "◉ 钉住" : "◎ 钉住";
        SelectVisual(BtnPin, _session.PinMode); // 与工具选中一致的开关样式
    }

    // ---- 构建 ----

    private void BuildColorButtons()
    {
        foreach (var hex in ColorChoices)
        {
            var dot = new Ellipse
            {
                Width = 14,
                Height = 14,
                Fill = BrushCache.Get(hex),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var btn = new Button
            {
                Content = dot,
                Style = (Style)FindResource("TbButton"),
                Tag = hex,
                Margin = new Thickness(0, 0, 4, 0),
            };
            btn.Click += (_, _) => SelectColor(btn, hex);
            ColorsPanel.Children.Add(btn);
            if (hex == _session.ColorHex)
            {
                _selectedColor = btn;
                Highlight(btn);
            }
        }
        if (_selectedColor is null)
        {
            _selectedColor = (Button)ColorsPanel.Children[0];
            SelectColor(_selectedColor, ColorChoices[0]);
        }
    }

    private void BuildWidthButtons()
    {
        for (var i = 0; i < WidthChoices.Length; i++)
        {
            var w = WidthChoices[i];
            var dot = new Ellipse
            {
                Width = 5 + i * 4,
                Height = 5 + i * 4,
                Fill = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var btn = new Button
            {
                Content = dot,
                Style = (Style)FindResource("TbButton"),
                Tag = w,
                Margin = new Thickness(0, 0, 4, 0),
            };
            btn.Click += (_, _) => SelectWidth(btn, w);
            WidthsPanel.Children.Add(btn);
            if (Math.Abs(w - _session.BaseWidth) < 0.1)
            {
                _selectedWidth = btn;
                Highlight(btn);
            }
        }
        _selectedWidth ??= (Button)WidthsPanel.Children[1];
        if (_selectedWidth.BorderThickness != new Thickness(2))
            Highlight(_selectedWidth);
    }

    private void SelectColor(Button btn, string hex)
    {
        _session.ColorHex = hex;
        _selectedColor = btn;
        foreach (Button b in ColorsPanel.Children) Reset(b);
        Highlight(btn);
    }

    private void SelectWidth(Button btn, double w)
    {
        _session.BaseWidth = w;
        _selectedWidth = btn;
        foreach (Button b in WidthsPanel.Children) Reset(b);
        Highlight(btn);
    }

    private static void Reset(Button b)
    {
        b.BorderThickness = new Thickness(1);
        b.BorderBrush = (Brush)FindResourceInternal("TbBorder");
    }

    private static void Highlight(Button b)
    {
        b.BorderThickness = new Thickness(2);
        b.BorderBrush = (Brush)FindResourceInternal("TbAccent");
    }

    // 资源在 App 级字典，FindResource 走元素树即可
    private static object FindResourceInternal(string key) => Application.Current.TryFindResource(key) is { } v ? v : Brushes.Gray;

    // ---- 状态 ----

    private void OnDrawModeChanged(bool drawing) => Dispatcher.BeginInvoke(() => UpdateModeUi(drawing));

    private void UpdateModeUi(bool drawing)
    {
        ModeText.Text = drawing ? "绘画中" : "观看中";
        ModeBadge.Background = drawing
            ? new SolidColorBrush(Color.FromArgb(0x44, 0x2E, 0xD5, 0x73))
            : new SolidColorBrush(Color.FromArgb(0x33, 0x4D, 0xA3, 0xFF));
        ModeText.Text += drawing ? " · 右键退出" : "";
        // 切换按钮两个态都在：观看态显示"开始绘画"（此前被藏掉，提示文案却让人点它）
        BtnExitDraw.Visibility = Visibility.Visible;
        BtnExitDraw.Content = drawing ? "完成" : "开始绘画";
        HintText.Text = drawing
            ? "Ctrl+Alt+D / 右键 = 退出绘画态（穿透回 Parsec）；Ctrl+Alt+P 钉住模式（开=新笔迹保留），Ctrl+Alt+C 清空，Ctrl+Z 撤销"
            : "当前穿透：鼠标直接操作 Parsec 窗口。Ctrl+Alt+D 或点\"开始绘画\"进入绘画态";
    }

    public void ReassertAboveOverlay()
    {
        var overlayHwnd = new WindowInteropHelper(_overlay).Handle;
        var myHwnd = new WindowInteropHelper(this).Handle;
        if (overlayHwnd != IntPtr.Zero && myHwnd != IntPtr.Zero)
            Win32.WinApi.PlaceAbove(myHwnd, overlayHwnd);
    }

    // ---- 按钮事件 ----

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _retractTimer?.Stop();
        BeginAnimation(TopProperty, null); // 释放动画对位置的占用
        try { DragMove(); } catch { /* 非按下态拖动 */ }
        _dragging = false;
        ConsiderDock();
    }

    // ---- 靠边自动缩入 ----

    /// <summary>拖动结束后：贴近视频上边缘 → 缩入，只露一条把手。</summary>
    private void ConsiderDock()
    {
        if (Top > DockZone)
        {
            _docked = false;
            return;
        }
        _docked = true;
        SlideTo(-(ActualHeight - DockPeek));
    }

    private void OnToolbarMouseEnter(object sender, MouseEventArgs e)
    {
        _retractTimer?.Stop();
        // 动画期间的合成事件不处理；绘画中（左键按住）不弹出——
        // 否则在屏幕顶部画线来回扫过把手，工具栏会不停弹出-缩入抖动
        if (_animating || !_docked || Mouse.LeftButton == MouseButtonState.Pressed) return;
        // 光标必须真的在把手条区域内：折叠态窗口大半矩形在屏外，
        // 光标远离时的幽灵 Enter（z 序拨动等来源）不得触发弹出
        if (Mouse.GetPosition(this).Y < ActualHeight - DockPeek - 10) return;
        _docked = false;
        SlideTo(4);
    }

    private void OnToolbarMouseLeave(object sender, MouseEventArgs e)
    {
        if (_animating || _dragging || Top > DockZone) return;
        // 离开 1.2s 后再缩（避免来回抖）；绘画中不缩（笔划过工具栏下方别塌）
        _retractTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1.2), DispatcherPriority.Background, (_, _) =>
        {
            _retractTimer!.Stop();
            if (_dragging || _animating || IsMouseOver || Mouse.LeftButton == MouseButtonState.Pressed) return;
            _docked = true;
            SlideTo(-(ActualHeight - DockPeek));
        }, Dispatcher);
        _retractTimer.Stop();
        _retractTimer.Start();
    }

    private void SlideTo(double targetTop)
    {
        _animating = true;
        var anim = new System.Windows.Media.Animation.DoubleAnimation(targetTop, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        };
        anim.Completed += (_, _) => _animating = false;
        BeginAnimation(TopProperty, anim);
    }

    // ---- 冒烟测试观测口 ----

    /// <summary>折叠测试：贴边并缩入（模拟拖拽靠边后的状态）。</summary>
    internal void SmokeDock()
    {
        Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
        Top = 0;
        _docked = true;
        SlideTo(-(ActualHeight - DockPeek));
    }

    /// <summary>当前 Top（抖动监测：折叠态应为负值）。</summary>
    internal double TopForSmoke => Top;

    // ---- 工具三选一 ----

    private void OnPen(object sender, RoutedEventArgs e) => SetTool(ToolKind.Pen);

    private void OnLaser(object sender, RoutedEventArgs e) => SetTool(ToolKind.Laser);

    private void OnEraser(object sender, RoutedEventArgs e) => SetTool(ToolKind.Eraser);

    private void SetTool(ToolKind kind)
    {
        _session.Tool = kind;
        SelectVisual(BtnPen, kind == ToolKind.Pen);
        SelectVisual(BtnLaser, kind == ToolKind.Laser);
        SelectVisual(BtnEraser, kind == ToolKind.Eraser);
        _overlay.UpdateToolCursor();
    }

    /// <summary>工具按钮选中态：选中=蓝色描边+淡蓝填充，未选=透明。</summary>
    private static void SelectVisual(Button b, bool on)
    {
        b.Background = on ? new SolidColorBrush(Color.FromArgb(0x3A, 0x4D, 0xA3, 0xFF)) : Brushes.Transparent;
        b.BorderBrush = on ? new SolidColorBrush(Color.FromRgb(0x4D, 0xA3, 0xFF)) : new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        b.Foreground = on ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xD6, 0xD6, 0xE4));
    }

    private void OnUndo(object sender, RoutedEventArgs e) => _session.UndoMine();

    private void OnPin(object sender, RoutedEventArgs e) => _session.TogglePinMode();

    private void OnClear(object sender, RoutedEventArgs e) => _session.ClearAll();

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        // 主机端：暂停/恢复显示笔迹
        _overlay.ToggleVisibility();
        _paused = !_paused;
        BtnToggle.Content = _paused ? "显示" : "暂停";
    }

    private void OnExitDraw(object sender, RoutedEventArgs e)
    {
        if (_overlay.DrawMode)
            _overlay.SetDrawMode(false);
        else
            _overlay.SetDrawMode(true);
    }
}
