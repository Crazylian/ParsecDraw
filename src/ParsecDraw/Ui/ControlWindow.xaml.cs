using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ParsecDraw.Protocol;
using ParsecDraw.Win32;

namespace ParsecDraw.Ui;

/// <summary>
/// 控制窗：选角色、主机展示共享地址、观众填地址连接；持有会话与窗口生命周期。
/// </summary>
public partial class ControlWindow : Window
{
    private readonly Session _session = new();
    private readonly Settings _settings = SettingsStore.Load();
    private readonly bool _minimizeToTray;
    private OverlayWindow? _overlay;
    private ToolbarWindow? _toolbar;
    private DispatcherTimer? _zOrderTimer;
    private H.NotifyIcon.TaskbarIcon? _tray;

    private int _sessionPort = Session.DefaultPort;
    private int _onlineCount;
    private string _hostLine = "等待观众…";
    private bool _reallyExit;   // 托盘菜单"退出"置位：之后的 Close 是真退出
    private bool _exitHandled;  // 收尾流程已启动，Shutdown 触发的二次 Close 直接放行
    private bool _balloonShown; // 首次进托盘的气泡只弹一次

    public ControlWindow(bool minimizeToTray = true)
    {
        InitializeComponent();
        _minimizeToTray = minimizeToTray;
        UrlBox.Text = _settings.Url;
        NameBox.Text = _settings.Name;
        _session.ColorHex = _settings.Color;
        _session.BaseWidth = _settings.Width;
        _session.ShowAllStrokes = _settings.ShowAll;

        _session.ViewerCount += n => Dispatcher.BeginInvoke(() =>
        {
            _onlineCount = n;
            RefreshHostStatus();
        });
        _session.Status += s => Dispatcher.BeginInvoke(() =>
        {
            if (_session.IsHost)
            {
                _hostLine = s;
                RefreshHostStatus();
            }
            else
            {
                ViewerStatus.Text = s;
            }
        });
        _session.ConnectedChanged += connected => Dispatcher.BeginInvoke(() =>
        {
            ConnDot.Fill = connected ? Brushes.LimeGreen : Brushes.IndianRed;
            ViewerStatus.Text = connected ? "已连接 · 覆盖层就绪，直接画即可" : "重连中…";
        });

        Closing += OnClosing;
        StateChanged += (_, _) =>
        {
            if (_minimizeToTray && !_reallyExit && WindowState == WindowState.Minimized)
                HideToTray(showBalloon: false); // 最小化也进托盘，不留任务栏
        };

        if (_minimizeToTray)
        {
            _tray = new H.NotifyIcon.TaskbarIcon
            {
                ToolTipText = "ParsecDraw — 观战画笔",
                IconSource = System.Windows.Media.Imaging.BitmapFrame.Create(
                    new Uri("pack://application:,,,/Assets/app.ico")),
                ContextMenu = BuildTrayMenu(),
            };
            _tray.TrayLeftMouseUp += (_, _) => RestoreFromTray();
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private System.Windows.Controls.ContextMenu BuildTrayMenu()
    {
        var open = new System.Windows.Controls.MenuItem { Header = "打开主面板" };
        open.Click += (_, _) => RestoreFromTray();
        var exit = new System.Windows.Controls.MenuItem { Header = "退出" };
        exit.Click += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _reallyExit = true;
            Close();
        });
        return new System.Windows.Controls.ContextMenu { Items = { open, exit } };
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exitHandled)
            return; // 放行：让这次 Close 真正关掉窗口

        if (_minimizeToTray && !_reallyExit)
        {
            e.Cancel = true;
            HideToTray(showBalloon: true);
            return;
        }

        // 真退出：同步撤托盘/存设置；会话收尾是快路径（关窗 + Abort socket），放后台跑。
        // 这里不 Cancel —— OnClosed 统一触发 Shutdown，避免和 Shutdown 流程互相等死。
        _exitHandled = true;
        _tray?.Dispose();
        _tray = null;
        SaveSettings();
        _ = TeardownSessionAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_exitHandled)
            Application.Current.Shutdown(); // ShutdownMode=OnExplicitShutdown
    }

    private void HideToTray(bool showBalloon)
    {
        Hide();
        if (showBalloon && !_balloonShown)
        {
            _balloonShown = true;
            try
            {
                _tray?.ShowNotification(title: "ParsecDraw 仍在运行",
                    message: "已最小化到托盘：左键点图标恢复面板，右键菜单可退出。");
            }
            catch
            {
                // 通知失败不影响功能
            }
        }
    }

    private void RefreshHostStatus()
        => HostStatus.Text = _session.Active
            ? $"运行中 · 端口 {_sessionPort} · 在线观众 {_onlineCount} · {_hostLine}"
            : "未启动";

    // ---- 角色选择与会话 ----

    private void OnChooseHost(object sender, RoutedEventArgs e)
    {
        RolePanel.Visibility = Visibility.Collapsed;
        HostPanel.Visibility = Visibility.Visible;

        IpList.Items.Clear();
        foreach (var ip in WinApi.GetLocalIPv4Addresses())
            IpList.Items.Add($"ws://{ip}:{Session.DefaultPort}");

        _session.StartHost(Dispatcher, Session.DefaultPort);
        _hostLine = "等待观众…";
        ShowSessionWindows(isHost: true);
    }

    /// <summary>冒烟测试用：等价于点下"我是主机"。</summary>
    internal void SmokeStartHost() => OnChooseHost(this, new RoutedEventArgs());

    private void OnChooseViewer(object sender, RoutedEventArgs e)
    {
        RolePanel.Visibility = Visibility.Collapsed;
        ViewerPanel.Visibility = Visibility.Visible;
        if (string.IsNullOrWhiteSpace(UrlBox.Text))
            UrlBox.Text = $"ws://192.168.1.10:{Session.DefaultPort}";
    }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        if (_session.Active) return;
        var url = UrlBox.Text.Trim();
        if (url.Length == 0) return;
        if (!url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            url = "ws://" + url;

        _session.DisplayName = string.IsNullOrWhiteSpace(NameBox.Text) ? "观众" : NameBox.Text.Trim();
        SaveSettings();

        ViewerStatus.Text = "连接中…";
        _session.StartViewer(Dispatcher, url);
        ShowSessionWindows(isHost: false);
    }

    private async void OnStopSession(object sender, RoutedEventArgs e)
    {
        await TeardownSessionAsync();
        RolePanel.Visibility = Visibility.Visible;
        HostPanel.Visibility = Visibility.Collapsed;
        ViewerPanel.Visibility = Visibility.Collapsed;
        HostStatus.Text = "未启动";
        ViewerStatus.Text = "未连接";
        ConnDot.Fill = Brushes.Gray;
    }

    private async Task TeardownSessionAsync()
    {
        _zOrderTimer?.Stop();
        _zOrderTimer = null;
        _toolbar?.Close();
        _toolbar = null;
        _overlay?.Close();
        _overlay = null;
        _onlineCount = 0;
        await _session.StopAsync();
    }

    private void ShowSessionWindows(bool isHost)
    {
        _overlay = new OverlayWindow(_session, isHost);
        _overlay.SourceInitialized += (_, _) =>
        {
            _toolbar = new ToolbarWindow(_session, _overlay, isHost);
            _toolbar.Show();
            _toolbar.ReassertAboveOverlay();
        };
        _overlay.Show();

        // 每 2s 重申置顶，防止其他置顶窗口压住覆盖层；主机顺带探测分辨率变化（游戏内切分辨率）
        _zOrderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _zOrderTimer.Tick += (_, _) =>
        {
            if (isHost)
                _session.AnnounceHostScreen();
            if (_overlay is null || _toolbar is null) return;
            if (_overlay.IsLoaded)
            {
                var hwnd = new WindowInteropHelper(_overlay).Handle;
                if (hwnd != IntPtr.Zero) WinApi.AssertTopmost(hwnd);
            }
            _toolbar.ReassertAboveOverlay();
        };
        _zOrderTimer.Start();
    }

    // ---- 杂项 ----

    private void OnIpSelected(object sender, SelectionChangedEventArgs e)
    {
        if (IpList.SelectedItem is string s)
        {
            try { Clipboard.SetText(s); } catch { /* 剪贴板被占用 */ }
            _hostLine = $"已复制 {s}，发给观众即可";
            RefreshHostStatus();
        }
    }

    private void OnSettingChanged(object sender, TextChangedEventArgs e) => SaveSettings();

    private void SaveSettings()
    {
        var name = NameBox.Text.Trim();
        SettingsStore.Save(new Settings
        {
            Url = UrlBox.Text.Trim(),
            Name = name.Length > 0 ? name : "观众",
            Color = _session.ColorHex,
            Width = _session.BaseWidth,
            ShowAll = _session.ShowAllStrokes,
        });
    }
}
