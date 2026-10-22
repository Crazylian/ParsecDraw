using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ParsecDraw.Protocol;

namespace ParsecDraw.Net;

/// <summary>
/// 主机端发现响应器：监听 UDP 探测，单播应答（主机名 + WS 端口）。
/// 生命周期严格等于主机运行期间（Session.StartHost/StopAsync 挂卸）。
/// 接收循环纯阻塞 ReceiveAsync——无周期信标、不轮询，空闲 CPU 为 0（性能红线）。
/// 端口被占时 Start 返回 false 而非抛出：发现是增强功能，不得拖垮手动连接路径。
/// </summary>
public sealed class DiscoveryResponder : IDisposable
{
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Func<DiscoveryAnswer>? _answerProvider;

    public bool Running { get; private set; }

    /// <summary>实际绑定端口（Start 传 0 时为随机分配值，自测用，对齐 DrawServer.BoundPort）。</summary>
    public int BoundPort { get; private set; }

    /// <summary>收到合法探测（IO 线程回调，订阅方自行调度）。</summary>
    public event Action<IPEndPoint>? Probed;

    /// <summary>开始响应。应答内容每次探测现取（answerProvider）——主机名改动下一包即生效，免重启。</summary>
    public bool Start(int port, Func<DiscoveryAnswer> answerProvider)
    {
        if (Running) return true;
        try
        {
            _udp = new UdpClient(port, AddressFamily.InterNetwork);
        }
        catch (SocketException)
        {
            // 端口被占是预期内降级场景（双开实例/其他软件占用），交调用方提示，不拖垮主机
            _udp = null;
            return false;
        }
        _answerProvider = answerProvider;
        _cts = new CancellationTokenSource();
        BoundPort = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        Running = true;
        _ = ReceiveLoopAsync(_udp, answerProvider, _cts.Token);
        return true;
    }

    public void Stop()
    {
        if (!Running) return;
        Running = false;
        _cts?.Cancel();
        try { _udp?.Close(); } catch { /* 已关闭 */ }
        _udp = null;
        _cts = null;
        _answerProvider = null;
    }

    public void Dispose() => Stop();

    private async Task ReceiveLoopAsync(UdpClient udp, Func<DiscoveryAnswer> provider, CancellationToken ct)
    {
        var probe = Encoding.ASCII.GetBytes(Discovery.ProbeMagic);
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try
            {
                r = await udp.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; } // 10054 回射（对无监听主机的广播引出 ICMP）：继续等，不退出
            if (r.Buffer.Length != probe.Length || !r.Buffer.AsSpan().SequenceEqual(probe))
                continue; // 非本应用流量，静默丢弃
            Probed?.Invoke(r.RemoteEndPoint);
            var payload = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(provider(), AppJsonContext.Default.DiscoveryAnswer));
            if (payload.Length > Discovery.MaxDatagram) continue; // 主机名超长的异常保护
            try { await udp.SendAsync(payload, r.RemoteEndPoint).ConfigureAwait(false); }
            catch (SocketException) { /* 对端瞬断：下一个探测照常 */ }
        }
    }
}
