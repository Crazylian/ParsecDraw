using System.Windows;
using System.Windows.Threading;
using ParsecDraw.Draw;
using ParsecDraw.Net;
using ParsecDraw.Protocol;
using ParsecDraw.Win32;

namespace ParsecDraw;

/// <summary>
/// 会话中枢：桥接本地绘制输入、StrokeStore（UI 线程）与网络层（IO 线程）。
/// 主机：Store 应用所有人的消息，自己的消息经 Server 广播。
/// 观众：Store 默认只渲染自己的实时笔迹（ShowAll 可改），别人的笔迹经 Parsec 流看到；
///       自己的消息 Apply 本地 + 发给服务器（服务器校验后广播）。
/// </summary>
public sealed class Session : IDisposable
{
    /// <summary>默认端口：大号段避开常见 WebSocket 工具占用，且不进 49152+ 临时端口区（监听端口会与出站源端口互抢）。含旧号 8765 后缀好记。</summary>
    public const int DefaultPort = 38765;

    public StrokeStore Store { get; } = new();

    public bool IsHost { get; private set; }
    public bool Active { get; private set; }

    /// <summary>本端 id，笔迹 id 前缀，用于撤销归属校验。</summary>
    public string ClientId { get; } = Guid.NewGuid().ToString("N")[..4];

    public string DisplayName { get; set; } = "观众";

    /// <summary>主机名：发现应答展示用（默认电脑名，面板可改），与观众昵称 <see cref="DisplayName"/> 是两个概念。响应器每次应答现取，改动即时生效。</summary>
    public string HostName { get; set; } = Environment.MachineName;
    public string ColorHex { get; set; } = "#FF4757";
    public double BaseWidth { get; set; } = 6;
    public ToolKind Tool { get; set; } = ToolKind.Pen;

    /// <summary>观众端渲染模式：false=只画自己的（默认，避免与 Parsec 流里重复）；true=渲染所有人。</summary>
    public bool ShowAllStrokes { get; set; }

    /// <summary>钉住模式：开启后新画的笔迹持久存在（不淡出），关闭后恢复自动淡出。已画的不受影响。</summary>
    public bool PinMode { get; set; }

    /// <summary>钉住模式切换（工具栏同步指示用）。</summary>
    public event Action? PinModeChanged;

    /// <summary>切换钉住模式（热键/工具栏共用入口）。</summary>
    public void TogglePinMode()
    {
        PinMode = !PinMode;
        Status?.Invoke(PinMode ? "钉住模式已开启：新画的笔迹将保留" : "钉住模式已关闭：笔迹恢复自动淡出");
        PinModeChanged?.Invoke();
    }

    private DrawServer? _server;
    private DrawClient? _client;
    private DiscoveryResponder? _discovery; // 发现响应器：生命周期严格随主机会话（观众侧扫描器在 ControlWindow，刻意不对称）
    private Dispatcher _dispatcher = null!;

    private int _seq;
    private Stroke? _active;
    private readonly List<double[]> _pending = new();
    private long _lastFlush;
    private const int FlushIntervalMs = 33;
    private DispatcherTimer? _flushTimer; // 笔画进行中的定时冲刷：停笔不动也要把尾段点上屏

    public event Action<string>? Status;
    public event Action<int>? ViewerCount;
    public event Action<bool>? ConnectedChanged;

    /// <summary>观众端收到主机屏幕分辨率（物理 px），用于对齐/黑边补偿。</summary>
    public event Action<double, double>? HostInfo;

    /// <summary>最近一次收到的主机分辨率（0 = 未知）。晚订阅者（如重连后重建的覆盖层）从这里补拉，不错过单播。</summary>
    public (double W, double H) LastHostInfo { get; private set; }

    public bool IsConnected => !Active || IsHost || _client?.State == ConnState.Connected;
    public bool HasActiveStroke => _active is not null;

    /// <summary>主机端实际绑定端口（StartHost 传 0 时为随机分配值，冒烟用）。</summary>
    public int HostPort => _server?.BoundPort ?? 0;

    // ---- 生命周期 ----

    public void StartHost(Dispatcher dispatcher, int port = DefaultPort)
    {
        if (Active) return;
        IsHost = true;
        Active = true;
        _dispatcher = dispatcher;
        _server = new DrawServer();
        _server.Received += OnNet;
        _server.ClientJoined += _ => Status?.Invoke("一位观众连接了");
        _server.ClientLeft += _ => Status?.Invoke("一位观众离开了");
        _server.ClientCountChanged += n => ViewerCount?.Invoke(n);
        var (sw, sh) = WinApi.PrimaryScreenSize;
        _server.SetHostScreen(sw, sh); // 新观众接入时单播，用于对齐补偿
        _server.Start(port);
        Status?.Invoke($"主机就绪，端口 {port}，等待观众…");
        StartDiscovery(); // 放在状态行之后：端口被占的降级提示要留在最后，不被"主机就绪"覆盖
    }

    /// <summary>挂发现响应器：绑定失败只降级提示（手动连接不受影响），绝不让主机启动失败。</summary>
    private void StartDiscovery()
    {
        var d = new DiscoveryResponder();
        // 每次应答现取：HostName 改动下一包生效；取 BoundPort 而非 port 参数——port=0 冒烟路径通告的也是真实端口
        if (!d.Start(Discovery.Port, () => new DiscoveryAnswer
        {
            Magic = Discovery.AnswerMagic,
            Name = HostName,
            Port = _server!.BoundPort,
        }))
        {
            d.Dispose(); // 绑定失败不留半死对象
            Status?.Invoke("局域网自动发现不可用（UDP 38766 被占用），观众仍可手动输入地址连接");
            return;
        }
        _discovery = d;
    }

    public void StartViewer(Dispatcher dispatcher, string url)
    {
        if (Active) return;
        IsHost = false;
        Active = true;
        _dispatcher = dispatcher;
        _client = new DrawClient();
        _client.Received += OnNet;
        _client.StateChanged += s => ConnectedChanged?.Invoke(s == ConnState.Connected);
        _client.Start(url);
        Status?.Invoke($"正在连接 {url} …");
    }

    public async Task StopAsync()
    {
        if (!Active) return;
        Active = false;
        EndStrokeIfActive();
        if (_discovery is not null)
        {
            // 先停应答再停 WS：避免向将死的 WS 端口引流
            _discovery.Stop();
            _discovery = null;
        }
        if (_server is not null)
        {
            await _server.StopAsync().ConfigureAwait(true);
            _server.Received -= OnNet;
            _server = null;
        }
        if (_client is not null)
        {
            await _client.StopAsync().ConfigureAwait(true);
            _client.Received -= OnNet;
            _client = null;
        }
        Store.ClearAll();
        Status?.Invoke("会话已结束");
    }

    public void Dispose() => _ = StopAsync();

    // ---- 本地绘制输入（观众端覆盖层调用；坐标已归一化） ----

    public void StrokeBegin(Point normalized)
    {
        if (!Active || _active is not null) return;
        var id = $"{ClientId}-{Interlocked.Increment(ref _seq)}";
        var m = new Msg
        {
            T = MsgTypes.Begin,
            Id = id,
            Name = DisplayName,
            Color = ColorHex,
            W = BaseWidth,
            K = Tool,
            P = new[] { new[] { normalized.X, normalized.Y } },
        };
        Store.Apply(m);
        Send(m);
        _active = Store.GetById(id);
        _pending.Clear();
        _lastFlush = Environment.TickCount64;
        // 33ms 定时冲刷：鼠标停住时 pending 里的点也要及时上屏/上网（触屏慢抬笔不丢尾巴）
        _flushTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(FlushIntervalMs), DispatcherPriority.Background,
            (_, _) => FlushPoints(Environment.TickCount64), _dispatcher);
        _flushTimer.Start();
    }

    public void StrokeMove(Point normalized)
    {
        if (_active is null) return;
        _pending.Add(new[] { normalized.X, normalized.Y });
        var now = Environment.TickCount64;
        if (now - _lastFlush >= FlushIntervalMs)
            FlushPoints(now);
    }

    public void StrokeEnd(bool? pin = null)
    {
        if (_active is null) return;
        FlushPoints(Environment.TickCount64);
        _flushTimer?.Stop();
        var m = new Msg { T = MsgTypes.End, Id = _active.Id, Pin = pin ?? PinMode };
        Store.Apply(m);
        Send(m);
        _active = null;
    }

    /// <summary>整笔橡皮：擦掉屏幕点附近自己画的笔迹（走 Undo 消息，服务器有归属校验）。</summary>
    public void EraseAt(Point screenPoint)
    {
        if (!Active) return;
        var s = Store.FindStrokeNear(screenPoint, 24, ClientId);
        if (s is null) return;
        var m = new Msg { T = MsgTypes.Undo, Id = s.Id };
        Store.Apply(m);
        Send(m);
    }

    /// <summary>主机端：重新通告当前屏幕分辨率（游戏内改分辨率后调用，会广播给观众）。</summary>
    public void AnnounceHostScreen()
    {
        if (!IsHost) return;
        var (sw, sh) = WinApi.PrimaryScreenSize;
        _server?.SetHostScreen(sw, sh);
    }

    /// <summary>撤销自己最后一条已结束的笔迹。</summary>
    public void UndoMine()
    {
        if (!Active) return;
        var last = Store.LastEndedStrokeOf(ClientId);
        if (last is null) return;
        var m = new Msg { T = MsgTypes.Undo, Id = last.Id };
        Store.Apply(m);
        Send(m);
    }

    public void ClearAll()
    {
        if (!Active) return;
        var m = new Msg { T = MsgTypes.Clear };
        Store.Apply(m);
        Send(m);
        Status?.Invoke("已清空");
    }

    private void FlushPoints(long now)
    {
        if (_active is null || _pending.Count == 0)
        {
            _pending.Clear();
            return;
        }
        var m = new Msg { T = MsgTypes.Point, Id = _active.Id, P = _pending.ToArray() };
        _pending.Clear();
        Store.Apply(m);
        Send(m);
        _lastFlush = now;
    }

    private void EndStrokeIfActive()
    {
        if (_active is not null)
            StrokeEnd();
    }

    // ---- 网络消息 ----

    private void Send(Msg m)
    {
        if (IsHost) _server?.BroadcastLocal(m);
        else _client?.Send(m);
    }

    private void OnNet(Msg m)
    {
        // IO 线程 → UI 线程（Store 只在 UI 线程访问）
        _dispatcher.BeginInvoke(() =>
        {
            if (!Active) return;
            if (m.T == MsgTypes.Join)
            {
                Status?.Invoke($"{m.Name ?? "观众"} 加入了观看");
                return;
            }
            if (m.T == MsgTypes.Host)
            {
                if (m.H is > 0)
                {
                    LastHostInfo = (m.W, m.H.Value);
                    HostInfo?.Invoke(m.W, m.H.Value);
                }
                return;
            }
            // 观众 LocalOnly 模式：不渲染别人的笔迹（避免与 Parsec 流重复），但协作指令照常生效
            if (!IsHost && !ShowAllStrokes && IsStrokeMsg(m.T))
                return;
            Store.Apply(m);
        });
    }

    private static bool IsStrokeMsg(string t)
        => t is MsgTypes.Begin or MsgTypes.Point or MsgTypes.End;
}
