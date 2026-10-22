using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ParsecDraw.Protocol;

namespace ParsecDraw.Net;

/// <summary>
/// 观众端主机扫描器：一轮 = 向全部广播目标各发一次探测，收集窗口内收应答、
/// 按 ip:port 去重逐条上报，窗口到点报 Completed。无周期信标（主机空闲零流量）。
/// 生命周期跟观众面板（进面板扫、连上即取消），不进 Session——与响应器（随主机会话）刻意不对称。
/// 再 Scan 自动顶替在飞的那一轮（"重新扫描"直用，旧轮不发 Completed）。
/// </summary>
public sealed class DiscoveryScanner : IDisposable
{
    /// <summary>默认收集窗口：局域网 RTT 远小于此，留足裕量；防火墙首弹窗期丢包由"重新扫描"兜底。</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMilliseconds(1200);

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;

    /// <summary>发现一台主机（IO 线程回调，UI 需自行调度，对齐 DrawServer/DrawClient 风格）。</summary>
    public event Action<DiscoveryAnswer, IPEndPoint>? HostFound;

    /// <summary>本轮窗口结束（IO 线程回调；空结果也触发）。被新一轮顶替的旧轮不发。</summary>
    public event Action? Completed;

    /// <summary>广播扫描：对每个本地网段的定向广播地址 + 受限广播各发一份探测。</summary>
    public void Scan() => Scan(null, null);

    /// <summary>定向扫描：target 非空时单播探测该 endpoint（自测连 127.0.0.1 用）。</summary>
    public void Scan(IPEndPoint? target, TimeSpan? window)
    {
        Cancel();
        var udp = new UdpClient(0); // 源端口随机：绝不与发现端口相撞（含自测场景）
        try
        {
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        }
        catch (SocketException)
        {
            // 开不了广播（极罕见）：单播目标仍可用
        }
        var cts = new CancellationTokenSource();
        _udp = udp;
        _cts = cts;
        // 窗口到点：cancel + close 双保险唤醒阻塞中的 ReceiveAsync
        cts.Token.Register(() => { try { udp.Close(); } catch { /* 已关闭 */ } });
        cts.CancelAfter(window ?? DefaultWindow);
        _ = ProbeAsync(udp, target, cts.Token);
        _ = CollectAsync(udp, cts);
    }

    public void Cancel()
    {
        _cts?.Cancel();
        try { _udp?.Close(); } catch { /* 已关闭 */ }
        _udp = null;
        _cts = null;
    }

    public void Dispose() => Cancel();

    private static async Task ProbeAsync(UdpClient udp, IPEndPoint? target, CancellationToken ct)
    {
        var probe = Encoding.ASCII.GetBytes(Discovery.ProbeMagic);
        var targets = target is not null
            ? new[] { target }
            : BroadcastTargets().Select(ip => new IPEndPoint(ip, Discovery.Port));
        foreach (var t in targets)
        {
            try
            {
                await udp.SendAsync(probe, t).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested) { return; } // 窗口已结束
            catch (SocketException) { /* 该网卡不可用/已拔：其余目标照发 */ }
            catch (ObjectDisposedException) { return; }
        }
    }

    private async Task CollectAsync(UdpClient udp, CancellationTokenSource cts)
    {
        var seen = new HashSet<string>(); // 本轮去重：来源 IP + 应答里的 WS 端口（同主机多网卡 = 多条可达路径）
        while (true)
        {
            UdpReceiveResult r;
            try
            {
                r = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception) when (cts.IsCancellationRequested) { break; } // 窗口到点/被顶替/取消
            catch (SocketException) { continue; } // 10054 回射：窗口内继续收
            if (r.Buffer.Length is 0 or > Discovery.MaxDatagram) continue;
            DiscoveryAnswer? a;
            try
            {
                a = JsonSerializer.Deserialize(Encoding.UTF8.GetString(r.Buffer), AppJsonContext.Default.DiscoveryAnswer);
            }
            catch (JsonException) { continue; }
            if (a is null || a.Magic != Discovery.AnswerMagic || a.Port is < 1 or > 65535) continue;
            if (!seen.Add($"{r.RemoteEndPoint.Address}:{a.Port}")) continue;
            HostFound?.Invoke(a, r.RemoteEndPoint);
        }
        if (ReferenceEquals(_cts, cts)) // 代际守卫：被新一轮顶替的旧轮不发 Completed
            Completed?.Invoke();
    }

    /// <summary>
    /// 广播目标：每个活动 IPv4 网段的定向广播地址（IP | ~掩码）+ 受限广播兜底。
    /// Windows 发 255.255.255.255 只走默认路由网卡，多网卡/VPN 下定向广播才覆盖全；
    /// /32 掩码（常见 VPN）算出"广播=本卡 IP"，退化为无害的自发单播。
    /// </summary>
    private static List<IPAddress> BroadcastTargets()
    {
        var set = new HashSet<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = addr.Address.GetAddressBytes();
                    var mask = addr.IPv4Mask.GetAddressBytes();
                    if (mask.Length != 4) continue;
                    var bc = new byte[4];
                    for (var i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | (byte)~mask[i]);
                    set.Add(new IPAddress(bc)); // /0 掩码会得出 255.255.255.255，set 自动去重
                }
            }
        }
        catch
        {
            // 网卡枚举失败：只剩受限广播兜底
        }
        set.Add(IPAddress.Broadcast);
        return set.ToList();
    }
}
