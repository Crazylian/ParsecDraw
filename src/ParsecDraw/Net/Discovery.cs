using System.Text.Json.Serialization;

namespace ParsecDraw.Net;

/// <summary>
/// 局域网发现协议常量。发现通道（UDP）与笔迹通道（WS）分端口：
/// 任一被占互不影响——发现失效时观众仍可手动输地址连接。
/// </summary>
internal static class Discovery
{
    /// <summary>响应器监听的 UDP 端口 = Session.DefaultPort + 1，同段好记。</summary>
    public const int Port = 38766;

    /// <summary>探测包负载：定长 ASCII，逐字节比较即可过滤打到本端口的无关流量；后缀 1 留协议演进余地。</summary>
    public const string ProbeMagic = "PDPROBE1";

    /// <summary>应答 JSON 的 magic 字段值：过滤别的程序恰好占用端口时的杂散回包。</summary>
    public const string AnswerMagic = "PDANSWER1";

    /// <summary>报文长度上限：正常应答远小于此，超限直接丢（防畸形包）。</summary>
    public const int MaxDatagram = 512;
}

/// <summary>
/// 发现应答负载（UDP 单播 JSON，走 AppJsonContext source-gen）。
/// Port 是主机 WS 端口，观众拼 ws://来源IP:Port 直连。
/// </summary>
public sealed record DiscoveryAnswer
{
    /// <summary>协议闸门：<see cref="Discovery.AnswerMagic"/>，不符的应答扫描端不上报。</summary>
    [JsonPropertyName("magic")] public string Magic { get; init; } = "";

    /// <summary>主机名（主机面板可改，默认电脑名）。</summary>
    [JsonPropertyName("name")] public string Name { get; init; } = "";

    /// <summary>主机 WS 端口。</summary>
    [JsonPropertyName("port")] public int Port { get; init; }
}
