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
    private OverlayWindow? _overlay;
    private ToolbarWindow? _toolbar;
    private DispatcherTimer? _zOrderTimer;

    private int _sessionPort = Session.DefaultPort;
    private int _onlineCount;
    private string _hostLine = "等待观众…";

    public ControlWindow()
    {
        InitializeComponent();
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

        Closing += async (_, _) =>
        {
            await TeardownSessionAsync();
            SaveSettings();
            Application.Current.Shutdown();
        };
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

        // 每 2s 重申置顶，防止其他置顶窗口压住覆盖层
        _zOrderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _zOrderTimer.Tick += (_, _) =>
        {
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
