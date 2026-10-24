using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ParsecDraw.Net;
using ParsecDraw.Protocol;
using ParsecDraw.Win32;

namespace ParsecDraw.Ui;

/// <summary>
/// 控制窗：选角色、主机展示共享地址、观众填地址连接；持有会话与窗口生命周期。
/// </summary>
public partial class ControlWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly Session _session = new();
    private readonly Settings _settings = SettingsStore.Load();
    private readonly bool _minimizeToTray;
    private readonly DiscoveryScanner _scanner = new(); // 生命周期跟观众面板（响应器在 Session，刻意不对称）
    private bool _scanDone; // 本轮扫描窗口是否已结束（结束后"发现 N 台"文案不再刷新）
    private OverlayWindow? _overlay;
    private ToolbarWindow? _toolbar;
    private DispatcherTimer? _zOrderTimer;
    private DispatcherTimer? _saveDebounce;
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
        HostNameBox.Text = string.IsNullOrWhiteSpace(_settings.HostName)
            ? Environment.MachineName : _settings.HostName.Trim(); // TextChanged 会同步进 _session.HostName
        _session.ColorHex = _settings.Color;
        _session.BaseWidth = _settings.Width;
        _session.ShowAllStrokes = _settings.ShowAll;

        _scanner.HostFound += (a, ep) => Dispatcher.BeginInvoke(() =>
        {
            // 必须 Wpf.Ui 的 ListViewItem：字典里没有系统 ListViewItem 的隐式样式，
            // 落回系统主题默认样式的行背景不可命中——点击选不中、SelectionChanged 不触发，
            // 表现为"点发现的主机没反应"（IpList 放字符串由 ui:ListView 生成 Wpf.Ui 容器，无此问题）
            HostList.Items.Add(new Wpf.Ui.Controls.ListViewItem
            {
                Content = $"{a.Name}（{ep.Address}）", // 同名不同 IP 各成一行，IP 是区分依据
                Tag = $"ws://{ep.Address}:{a.Port}",   // 点行直连的完整地址
            });
            if (!_scanDone)
                ScanStatus.Text = $"发现 {HostList.Items.Count} 台，点击直接连接";
        });
        _scanner.Completed += () => Dispatcher.BeginInvoke(() =>
        {
            _scanDone = true;
            if (HostList.Items.Count == 0)
                ScanStatus.Text = "未发现主机：确认对方已启动且在同一网络，或手动输入地址";
        });

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
            // 关键：TaskbarIcon 是 FrameworkElement，代码创建（不进可视化树）不触发
            // Loaded，托盘图标永远不会出现——必须 ForceCreate 立即建图标，
            // 否则关窗后进程活着但托盘无图标，只能任务管理器结束进程
            _tray.ForceCreate();
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
        _scanner.Dispose();
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

    /// <summary>冒烟测试用：等价于点下"我是主机"（随机端口，避开用户可能开着的主实例）。</summary>
    internal void SmokeStartHost(int port = 0)
    {
        RolePanel.Visibility = Visibility.Collapsed;
        HostPanel.Visibility = Visibility.Visible;
        IpList.Items.Clear();
        _session.StartHost(Dispatcher, port);
        SmokeHostStarted = true;
        _hostLine = "等待观众…";
        ShowSessionWindows(isHost: true);
    }

    /// <summary>主机端实际绑定端口（冒烟用，port=0 时为随机值）。</summary>
    internal int HostPort => _session.HostPort;

    /// <summary>冒烟观测：SmokeStartHost 是否执行到位。</summary>
    internal bool SmokeHostStarted { get; private set; }

    /// <summary>冒烟观测：会话是否激活。</summary>
    internal bool SessionActiveForSmoke => _session.Active;

    private void OnChooseViewer(object sender, RoutedEventArgs e)
    {
        RolePanel.Visibility = Visibility.Collapsed;
        ViewerPanel.Visibility = Visibility.Visible;
        if (string.IsNullOrWhiteSpace(UrlBox.Text))
            UrlBox.Text = $"ws://192.168.1.10:{Session.DefaultPort}";
        StartDiscoveryScan(); // 每次进面板都重扫：上一轮结果可能过期
    }

    /// <summary>发起观众连接（手动输入与发现列表点击共用路径）。</summary>
    private void ConnectTo(string urlText)
    {
        if (_session.Active) return;
        var url = urlText.Trim();
        if (url.Length == 0) return;
        if (!url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            url = "ws://" + url;

        _scanner.Cancel(); // 连接目标已定，停掉在飞的扫描
        _session.DisplayName = string.IsNullOrWhiteSpace(NameBox.Text) ? "观众" : NameBox.Text.Trim();
        SaveSettings();

        ViewerStatus.Text = "连接中…";
        _session.StartViewer(Dispatcher, url);
        ShowSessionWindows(isHost: false);
    }

    private void OnConnect(object sender, RoutedEventArgs e) => ConnectTo(UrlBox.Text);

    // ---- 局域网发现 ----

    /// <summary>触发一轮扫描（进面板/重新扫描共用）：清列表、重置文案。Scan 自身会顶替在飞的上一轮。</summary>
    private void StartDiscoveryScan()
    {
        _scanDone = false;
        HostList.Items.Clear(); // 移除选中项会触发 SelectionChanged，OnHostPicked 的空选守卫直接放行
        ScanStatus.Text = "正在扫描局域网…";
        _scanner.Scan();
    }

    private void OnHostPicked(object sender, SelectionChangedEventArgs e)
    {
        // 点列表行 = 直接连接；守卫语义与"连接"按钮一致（会话进行中不响应）
        if (HostList.SelectedItem is ListViewItem { Tag: string url } && !_session.Active)
        {
            UrlBox.Text = url; // 回填地址框：连接对象可见、随设置保存
            ConnectTo(url);
        }
    }

    private void OnRescan(object sender, RoutedEventArgs e) => StartDiscoveryScan();

    private void OnHostNameChanged(object sender, TextChangedEventArgs e)
    {
        // 即时同步给 Session（发现应答每次现取，改名下一包生效）；防抖只管落盘
        var n = HostNameBox.Text.Trim();
        _session.HostName = n.Length > 0 ? n : Environment.MachineName;
        OnSettingChanged(sender, e);
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
        _scanner.Cancel();
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

    private void OnSettingChanged(object sender, TextChangedEventArgs e)
    {
        // 400ms 防抖：别每敲一个键就同步写盘
        _saveDebounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background,
            (_, _) => { _saveDebounce!.Stop(); SaveSettings(); }, Dispatcher);
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void SaveSettings()
    {
        var name = NameBox.Text.Trim();
        var hostName = HostNameBox.Text.Trim();
        SettingsStore.Save(new Settings
        {
            Url = UrlBox.Text.Trim(),
            Name = name.Length > 0 ? name : "观众",
            HostName = hostName.Length > 0 ? hostName : Environment.MachineName,
            Color = _session.ColorHex,
            Width = _session.BaseWidth,
            ShowAll = _session.ShowAllStrokes,
        });
    }
}
