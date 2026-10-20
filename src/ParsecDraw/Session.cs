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
    public const int DefaultPort = 8765;

    public StrokeStore Store { get; } = new();

    public bool IsHost { get; private set; }
    public bool Active { get; private set; }

    /// <summary>本端 id，笔迹 id 前缀，用于撤销归属校验。</summary>
    public string ClientId { get; } = Guid.NewGuid().ToString("N")[..4];

    public string DisplayName { get; set; } = "观众";
    public string ColorHex { get; set; } = "#FF4757";
    public double BaseWidth { get; set; } = 6;
    public ToolKind Tool { get; set; } = ToolKind.Pen;

    /// <summary>观众端渲染模式：false=只画自己的（默认，避免与 Parsec 流里重复）；true=渲染所有人。</summary>
    public bool ShowAllStrokes { get; set; }

    private DrawServer? _server;
    private DrawClient? _client;
    private Dispatcher _dispatcher = null!;

    private int _seq;
    private Stroke? _active;
    private readonly List<double[]> _pending = new();
    private long _lastFlush;
    private const int FlushIntervalMs = 33;

    public event Action<string>? Status;
    public event Action<int>? ViewerCount;
    public event Action<bool>? ConnectedChanged;

    /// <summary>观众端收到主机屏幕分辨率（物理 px），用于对齐/黑边补偿。</summary>
    public event Action<double, double>? HostInfo;

    /// <summary>最近一次收到的主机分辨率（0 = 未知）。晚订阅者（如重连后重建的覆盖层）从这里补拉，不错过单播。</summary>
    public (double W, double H) LastHostInfo { get; private set; }

    public bool IsConnected => !Active || IsHost || _client?.State == ConnState.Connected;
    public bool HasActiveStroke => _active is not null;

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
    }

    public void StrokeMove(Point normalized)
    {
        if (_active is null) return;
        _pending.Add(new[] { normalized.X, normalized.Y });
        var now = Environment.TickCount64;
        if (now - _lastFlush >= FlushIntervalMs)
            FlushPoints(now);
    }

    public void StrokeEnd(bool pin = false)
    {
        if (_active is null) return;
        FlushPoints(Environment.TickCount64);
        var m = new Msg { T = MsgTypes.End, Id = _active.Id, Pin = pin };
        Store.Apply(m);
        Send(m);
        _active = null;
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

    /// <summary>钉住当前画面上所有已结束的笔迹。</summary>
    public void PinAll()
    {
        if (!Active) return;
        var m = new Msg { T = MsgTypes.Pin };
        Store.Apply(m);
        Send(m);
        Status?.Invoke("已钉住当前笔迹");
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
