using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ParsecDraw.Protocol;

namespace ParsecDraw.Net;

public enum ConnState
{
    Disconnected,
    Connecting,
    Connected,
}

/// <summary>
/// 观众端 WebSocket 客户端：断线自动重连（指数退避 1s → 10s），重连期间发送静默丢弃。
/// </summary>
public sealed class DrawClient : IAsyncDisposable
{
    private ClientWebSocket? _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private string _url = "";
    private volatile bool _wantConnected;

    public ConnState State { get; private set; } = ConnState.Disconnected;

    /// <summary>IO 线程回调，订阅方自行调度。</summary>
    public event Action<ConnState>? StateChanged;
    public event Action<Msg>? Received;

    public void Start(string url)
    {
        if (_wantConnected) return;
        _url = url;
        _wantConnected = true;
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        _wantConnected = false;
        _cts?.Cancel();
        try { _ws?.Abort(); } catch { /* 已断开 */ }
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>发送消息；未连接时静默丢弃（绘制是即时性内容，不值得排队）。</summary>
    public void Send(Msg m)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(m, AppJsonContext.Default.Msg));
        _ = Task.Run(async () =>
        {
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // 断连由接收循环感知并处理
            }
            finally
            {
                _sendLock.Release();
            }
        });
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var backoffMs = 1000;
        while (_wantConnected && !ct.IsCancellationRequested)
        {
            SetState(ConnState.Connecting);
            try
            {
                var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(_url), ct).ConfigureAwait(false);
                _ws = ws;
                backoffMs = 1000;
                SetState(ConnState.Connected);

                var buffer = new byte[64 * 1024];
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close)
                        break;
                    if (r.Count == 0)
                        continue;
                    Msg? msg;
                    try
                    {
                        msg = JsonSerializer.Deserialize(Encoding.UTF8.GetString(buffer, 0, r.Count), AppJsonContext.Default.Msg);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }
                    if (msg is not null)
                        Received?.Invoke(msg);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 连接失败/中断 → 退避重连
            }

            SetState(ConnState.Disconnected);
            try { _ws?.Dispose(); } catch { /* 已释放 */ }
            _ws = null;

            if (_wantConnected && !ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(backoffMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                backoffMs = Math.Min(backoffMs * 2, 10_000);
            }
        }
    }

    private void SetState(ConnState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(s);
    }
}
