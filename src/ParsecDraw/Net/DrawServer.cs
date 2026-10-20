using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ParsecDraw.Protocol;

namespace ParsecDraw.Net;

/// <summary>
/// 主机端 WebSocket 中继服务器。
/// 用 TcpListener + 手写 RFC6455 握手 + WebSocket.CreateFromStream：
/// 避免 HttpListener 绑定局域网地址需要管理员配 URL ACL 的问题，零依赖、免提权。
/// 广播 = 序列化一次循环发送；同一客户端的并发发送用信号量串行化。
/// </summary>
public sealed class DrawServer : IAsyncDisposable
{
    private sealed class Client
    {
        public required string Id { get; init; }
        public required WebSocket Socket { get; init; }
        public required SemaphoreSlim SendLock { get; init; }
        /// <summary>该客户端 begun 过的笔迹 id（undo 权限校验）。</summary>
        public HashSet<string> OwnStrokes { get; } = new(StringComparer.Ordinal);
    }

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly List<Client> _clients = new();
    private readonly object _gate = new();
    private int _nextId;

    public int BoundPort { get; private set; }
    public bool Running { get; private set; }

    /// <summary>任何客户端发来的消息（已通过权限校验）。IO 线程回调，订阅方自行调度。</summary>
    public event Action<Msg>? Received;
    public event Action<string>? ClientJoined;
    public event Action<string>? ClientLeft;
    public event Action<int>? ClientCountChanged;

    public void Start(int port)
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Running = true;
        _ = AcceptLoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (!Running) return;
        Running = false;
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { /* 已关闭 */ }
        List<Client> snapshot;
        lock (_gate) snapshot = new(_clients);
        foreach (var c in snapshot)
        {
            try { c.Socket.Abort(); } catch { /* 连接已断 */ }
        }
        lock (_gate) _clients.Clear();
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>主机本地产生的消息（主播自己的笔迹/钉住/清空）广播给所有客户端。</summary>
    public void BroadcastLocal(Msg m) => Broadcast(m, except: null);

    public int ClientCount
    {
        get { lock (_gate) return _clients.Count; }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            _ = HandleClientAsync(tcp, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient tcp, CancellationToken ct)
    {
        Client? client = null;
        try
        {
            tcp.NoDelay = true;
            using var tcpOwner = tcp;
            var stream = tcp.GetStream();

            if (!await PerformHandshakeAsync(stream, ct))
                return;

            client = new Client
            {
                Id = "c" + Interlocked.Increment(ref _nextId),
                Socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30)),
                SendLock = new SemaphoreSlim(1, 1),
            };
            lock (_gate) _clients.Add(client);
            ClientJoined?.Invoke(client.Id);
            ClientCountChanged?.Invoke(ClientCount);

            var buffer = new byte[64 * 1024];
            while (!ct.IsCancellationRequested && client.Socket.State == WebSocketState.Open)
            {
                var result = await client.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                if (result.Count == 0)
                    continue;

                Msg? msg;
                try
                {
                    msg = JsonSerializer.Deserialize(Encoding.UTF8.GetString(buffer, 0, result.Count), AppJsonContext.Default.Msg);
                }
                catch (JsonException)
                {
                    continue; // 坏消息直接丢
                }
                if (msg is null || msg.T.Length == 0)
                    continue;

                // 权限校验：只能撤销自己 begun 的笔迹
                switch (msg.T)
                {
                    case MsgTypes.Begin when msg.Id is not null:
                        client.OwnStrokes.Add(msg.Id);
                        break;
                    case MsgTypes.Undo:
                        if (msg.Id is null || !client.OwnStrokes.Remove(msg.Id))
                            continue; // 不是你的笔迹，丢弃
                        break;
                }

                Broadcast(msg, except: client);
                Received?.Invoke(msg);
            }
        }
        catch (Exception)
        {
            // 断连是常态（观众关窗口/网络波动），静默处理
        }
        finally
        {
            if (client is not null)
            {
                lock (_gate) _clients.Remove(client);
                try { client.Socket.Dispose(); } catch { /* 已释放 */ }
                client.SendLock.Dispose();
                ClientLeft?.Invoke(client.Id);
                ClientCountChanged?.Invoke(ClientCount);
            }
        }
    }

    private void Broadcast(Msg m, Client? except)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(m, AppJsonContext.Default.Msg));
        List<Client> snapshot;
        lock (_gate) snapshot = new(_clients);
        foreach (var c in snapshot)
        {
            if (ReferenceEquals(c, except)) continue;
            _ = SendAsync(c, bytes);
        }
    }

    private static async Task SendAsync(Client c, byte[] bytes)
    {
        await c.SendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (c.Socket.State == WebSocketState.Open)
                await c.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // 发送失败 → 下次 Receive 循环会感知断连并清理
        }
        finally
        {
            c.SendLock.Release();
        }
    }

    /// <summary>读 HTTP 升级请求并回 101。非 WebSocket 请求回一个状态页（方便浏览器确认端口可达）。</summary>
    private static async Task<bool> PerformHandshakeAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        var total = 0;
        var headEnd = -1;
        while (true)
        {
            if (total >= buf.Length) return false;
            var n = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), ct).ConfigureAwait(false);
            if (n <= 0) return false;
            total += n;
            headEnd = IndexOfHeaderEnd(buf, total);
            if (headEnd >= 0) break;
        }
        var head = Encoding.ASCII.GetString(buf, 0, headEnd);

        if (!head.Contains("Upgrade", StringComparison.OrdinalIgnoreCase) ||
            !head.Contains("websocket", StringComparison.OrdinalIgnoreCase))
        {
            var body = Encoding.UTF8.GetBytes("ParsecDraw WebSocket server is running.\r\n");
            var resp = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(resp, ct).ConfigureAwait(false);
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
            return false;
        }

        var key = GetHeader(head, "Sec-WebSocket-Key");
        if (string.IsNullOrEmpty(key)) return false;

        var accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var upgrade = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
        await stream.WriteAsync(upgrade, ct).ConfigureAwait(false);
        return true;
    }

    private static int IndexOfHeaderEnd(byte[] buf, int len)
    {
        for (var i = 0; i + 3 < len; i++)
        {
            if (buf[i] == (byte)'\r' && buf[i + 1] == (byte)'\n' && buf[i + 2] == (byte)'\r' && buf[i + 3] == (byte)'\n')
                return i; // header 不含终结符
        }
        return -1;
    }

    private static string GetHeader(string head, string name)
    {
        foreach (var line in head.Split("\r\n"))
        {
            var idx = line.IndexOf(':');
            if (idx > 0 && string.Equals(line[..idx].Trim(), name, StringComparison.OrdinalIgnoreCase))
                return line[(idx + 1)..].Trim();
        }
        return "";
    }
}
