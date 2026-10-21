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
/// 悬浮画具托盘（横向版）：置顶、可拖动、永不点击穿透（z 序压在覆盖层之上）。
/// 纯图标单行布局，文案全部走 ToolTip；靠边缩入沿用顶部停靠。
/// </summary>
public partial class ToolbarWindow : Window
{
    private static readonly string[] ColorChoices = { "#FFFFFF", "#FF4757", "#FFD32A", "#2ED573", "#4DA3FF" };
    private static readonly string[] ColorNames = { "白色", "红色", "黄色", "绿色", "蓝色" };
    private static readonly double[] WidthChoices = { 3, 6, 12 };
    private static readonly string[] WidthNames = { "细", "中", "粗" };

    // ---- 视觉常量（冻结复用；不在交互路径上新建画刷） ----
    private static readonly Brush AccentFill = Frozen(Color.FromRgb(0x4D, 0xA3, 0xFF));
    private static readonly Brush AccentSoft = Frozen(Color.FromArgb(0x29, 0x4D, 0xA3, 0xFF));
    private static readonly Brush AccentLine = Frozen(Color.FromArgb(0x61, 0x4D, 0xA3, 0xFF));
    private static readonly Brush InkDark = Frozen(Color.FromRgb(0x0F, 0x15, 0x24));
    private static readonly Brush IconDim = Frozen(Color.FromRgb(0xC9, 0xC9, 0xDB));
    private static readonly Brush ModeGreen = Frozen(Color.FromRgb(0x2E, 0xD5, 0x73));
    private static readonly Brush ModeBlue = Frozen(Color.FromRgb(0x4D, 0xA3, 0xFF));
    private static readonly Brush RingBrush = Frozen(Color.FromRgb(0xEA, 0xEA, 0xF2));
    private static readonly Brush DotIdle = Frozen(Color.FromRgb(0xD6, 0xD6, 0xE4));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private readonly Session _session;
    private readonly OverlayWindow _overlay;

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

        // 屏幕顶部中央
        Loaded += (_, _) =>
        {
            Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
            Top = 12;
        };

        if (isHost)
        {
            // 主机不能画：只留 状态点+主机 ｜ 暂停/钉住/清空 ｜ 帮助
            ModeText.Visibility = Visibility.Visible;
            ToolsPanel.Visibility = Visibility.Collapsed;
            SepTools.Visibility = Visibility.Collapsed;
            ColorsPanel.Visibility = Visibility.Collapsed;
            SepColors.Visibility = Visibility.Collapsed;
            WidthsPanel.Visibility = Visibility.Collapsed;
            SepWidths.Visibility = Visibility.Collapsed;
            BtnUndo.Visibility = Visibility.Collapsed;
            SepActions.Visibility = Visibility.Collapsed;
            BtnExitDraw.Visibility = Visibility.Collapsed;
            BtnToggle.Visibility = Visibility.Visible;
            BtnHelp.ToolTip = "覆盖层已点击穿透，不影响游戏。\nCtrl+Alt+D 暂停/显示笔迹 · Ctrl+Alt+P 钉住模式 · Ctrl+Alt+C 清空";
            _overlay.Visibility = Visibility.Visible;
        }
        else
        {
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
        var on = _session.PinMode;
        BtnPin.Foreground = on ? AccentFill : IconDim;
        BtnPin.Background = on ? AccentSoft : Brushes.Transparent;
        BtnPin.BorderBrush = on ? AccentLine : Brushes.Transparent;
        BtnPin.ToolTip = on
            ? "钉住已开启：新笔迹持久保留（Ctrl+Alt+P 关闭）"
            : "钉住模式：开启后新笔迹持久保留（Ctrl+Alt+P）";
    }

    // ---- 构建 ----

    private void BuildColorButtons()
    {
        for (var i = 0; i < ColorChoices.Length; i++)
        {
            var hex = ColorChoices[i];
            var btn = new Button
            {
                Style = (Style)FindResource("TbDotBtn"),
                Tag = hex,
                ToolTip = ColorNames[i],
                Margin = new Thickness(0, 0, i == ColorChoices.Length - 1 ? 0 : 8, 0),
            };
            btn.Content = DotWithRing(BrushCache.Get(hex), 15, 22);
            btn.Click += (_, _) => SelectColor(btn, hex);
            ColorsPanel.Children.Add(btn);
            if (hex == _session.ColorHex)
            {
                _selectedColor = btn;
                Ring(btn, true);
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
            var btn = new Button
            {
                Style = (Style)FindResource("TbDotBtn"),
                Tag = w,
                ToolTip = WidthNames[i],
                Margin = new Thickness(0, 0, i == WidthChoices.Length - 1 ? 0 : 9, 0),
            };
            btn.Content = DotWithRing(DotIdle, 6 + i * 3, 20);
            btn.Click += (_, _) => SelectWidth(btn, w);
            WidthsPanel.Children.Add(btn);
            if (Math.Abs(w - _session.BaseWidth) < 0.1)
            {
                _selectedWidth = btn;
                SelectWidthVisual(btn, true);
            }
        }
        if (_selectedWidth is null)
        {
            _selectedWidth = (Button)WidthsPanel.Children[1];
            SelectWidth(_selectedWidth, WidthChoices[1]);
        }
    }

    /// <summary>裸点 + 选中光环；Children[0]=环（Visibility 由 Ring 切换），Children[1]=点。</summary>
    private static Grid DotWithRing(Brush dotFill, double dotSize, double ringSize)
    {
        var ring = new Ellipse
        {
            Width = ringSize,
            Height = ringSize,
            Stroke = RingBrush,
            StrokeThickness = 2,
            Visibility = Visibility.Collapsed,
        };
        var dot = new Ellipse { Width = dotSize, Height = dotSize, Fill = dotFill };
        var g = new Grid();
        g.Children.Add(ring);
        g.Children.Add(dot);
        return g;
    }

    private static void Ring(Button b, bool on)
        => ((Ellipse)((Grid)b.Content).Children[0]).Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    private void SelectColor(Button btn, string hex)
    {
        _session.ColorHex = hex;
        _selectedColor = btn;
        foreach (Button b in ColorsPanel.Children) Ring(b, b == btn);
    }

    private void SelectWidth(Button btn, double w)
    {
        _session.BaseWidth = w;
        _selectedWidth = btn;
        foreach (Button b in WidthsPanel.Children) SelectWidthVisual(b, b == btn);
    }

    private static void SelectWidthVisual(Button b, bool on)
    {
        Ring(b, on);
        // 粗细点选中时提亮为纯白，未选中为灰白
        ((Ellipse)((Grid)b.Content).Children[1]).Fill = on ? Brushes.White : DotIdle;
    }

    // ---- 状态 ----

    private void OnDrawModeChanged(bool drawing) => Dispatcher.BeginInvoke(() => UpdateModeUi(drawing));

    private void UpdateModeUi(bool drawing)
    {
        var c = drawing ? ModeGreen : ModeBlue;
        ModeDot.Fill = c;
        ModeGlow.Fill = c;
        // 完成键：绘画态=勾（完成）；观看态=三角（开始绘画，填充更醒目）
        DoneIcon.Data = (StreamGeometry)FindResource(drawing ? "GeoCheck" : "GeoPlay");
        DoneIcon.Fill = drawing ? Brushes.Transparent : InkDark;
        BtnExitDraw.ToolTip = drawing ? "完成绘画，回到观看（Ctrl+Alt+D / 右键）" : "开始绘画（Ctrl+Alt+D）";
        BtnHelp.ToolTip = drawing
            ? "绘画中：鼠标直接画线\n右键 / Ctrl+Alt+D = 退出绘画（穿透回 Parsec）\nCtrl+Alt+P 钉住 · Ctrl+Alt+Z 撤销 · Ctrl+Alt+C 清空"
            : "观看中：鼠标直通 Parsec 窗口\nCtrl+Alt+D 或点右侧蓝色按钮开始绘画";
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
        ToolVisual(BtnPen, kind == ToolKind.Pen);
        ToolVisual(BtnLaser, kind == ToolKind.Laser);
        ToolVisual(BtnEraser, kind == ToolKind.Eraser);
        _overlay.UpdateToolCursor();
    }

    /// <summary>工具选中态：选中=品牌蓝实底+深色图标，未选=透明底。</summary>
    private static void ToolVisual(Button b, bool on)
    {
        b.Background = on ? AccentFill : Brushes.Transparent;
        b.Foreground = on ? InkDark : IconDim;
    }

    private void OnUndo(object sender, RoutedEventArgs e) => _session.UndoMine();

    private void OnPin(object sender, RoutedEventArgs e) => _session.TogglePinMode();

    private void OnClear(object sender, RoutedEventArgs e) => _session.ClearAll();

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        // 主机端：暂停/恢复显示笔迹（图标随状态切换）
        _overlay.ToggleVisibility();
        _paused = !_paused;
        ToggleIcon.Data = (StreamGeometry)FindResource(_paused ? "GeoEye" : "GeoPause");
        BtnToggle.ToolTip = _paused ? "恢复显示笔迹" : "暂停显示笔迹（Ctrl+Alt+D）";
    }

    private void OnExitDraw(object sender, RoutedEventArgs e)
    {
        if (_overlay.DrawMode)
            _overlay.SetDrawMode(false);
        else
            _overlay.SetDrawMode(true);
    }
}
