using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
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
        BtnPin.Content = _session.PinMode ? "钉住:开" : "钉住:关";
        BtnPin.BorderThickness = new Thickness(2);
        BtnPin.BorderBrush = (Brush)FindResourceInternal(_session.PinMode ? "TbAccent" : "TbBorder");
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
        try { DragMove(); } catch { /* 非按下态拖动 */ }
    }

    private void OnPen(object sender, RoutedEventArgs e)
    {
        _session.Tool = ToolKind.Pen;
        BtnPen.BorderBrush = (Brush)FindResourceInternal("TbAccent");
        BtnLaser.BorderBrush = (Brush)FindResourceInternal("TbBorder");
    }

    private void OnLaser(object sender, RoutedEventArgs e)
    {
        _session.Tool = ToolKind.Laser;
        BtnLaser.BorderBrush = (Brush)FindResourceInternal("TbAccent");
        BtnPen.BorderBrush = (Brush)FindResourceInternal("TbBorder");
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
