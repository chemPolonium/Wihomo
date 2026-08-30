using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wihomo.Services.Realtime;

/// <summary>
/// 以下类型严格对应 mihomo v1.19.29 实测帧，字段名与类型以
/// Wihomo.Tests/Protocol/mihomo-1.19.29-ws-frames.json 中的原始抓包为准，
/// 不要按 Clash 通用文档「修正」。
/// </summary>
internal static class CoreJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>/traffic 帧。up/down 是最近一个推送区间（实测 1 秒）内的字节数，即速率。</summary>
public sealed record TrafficFrame
{
    [JsonPropertyName("up")]
    public long Up { get; init; }

    [JsonPropertyName("down")]
    public long Down { get; init; }

    [JsonPropertyName("upTotal")]
    public long UpTotal { get; init; }

    [JsonPropertyName("downTotal")]
    public long DownTotal { get; init; }
}

/// <summary>/logs 帧。内核不带时间戳，时间由客户端打点。</summary>
public sealed record LogFrame
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("payload")]
    public string Payload { get; init; } = string.Empty;
}

/// <summary>
/// /connections 帧。每次推送都是全量快照；无活跃连接时 connections 为 null 而非空数组。
/// 注意累计值在这里叫 downloadTotal/uploadTotal，与 /traffic 的 downTotal/upTotal 同值异名。
/// </summary>
public sealed record ConnectionsFrame
{
    [JsonPropertyName("downloadTotal")]
    public long DownloadTotal { get; init; }

    [JsonPropertyName("uploadTotal")]
    public long UploadTotal { get; init; }

    [JsonPropertyName("memory")]
    public long Memory { get; init; }

    [JsonPropertyName("connections")]
    public CoreConnection[]? Connections { get; init; }

    [JsonIgnore]
    public int ActiveCount => Connections?.Length ?? 0;
}

public sealed record CoreConnection
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("metadata")]
    public ConnectionMetadata Metadata { get; init; } = new();

    /// <summary>该连接的累计下载字节，非速率。</summary>
    [JsonPropertyName("download")]
    public long Download { get; init; }

    [JsonPropertyName("upload")]
    public long Upload { get; init; }

    [JsonPropertyName("start")]
    public DateTimeOffset Start { get; init; }

    /// <summary>代理链，末位为实际使用的节点；直连时为 ["DIRECT"]。</summary>
    [JsonPropertyName("chains")]
    public string[] Chains { get; init; } = [];

    [JsonPropertyName("rule")]
    public string Rule { get; init; } = string.Empty;

    [JsonPropertyName("rulePayload")]
    public string RulePayload { get; init; } = string.Empty;

    [JsonIgnore]
    public string? UsedProxy => Chains.Length > 0 ? Chains[^1] : null;
}

/// <summary>
/// 端口在内核侧序列化为字符串（如 "18081"），因此保持 string，不要改成数值。
/// </summary>
public sealed record ConnectionMetadata
{
    [JsonPropertyName("network")]
    public string Network { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("sourceIP")]
    public string SourceIp { get; init; } = string.Empty;

    [JsonPropertyName("destinationIP")]
    public string DestinationIp { get; init; } = string.Empty;

    [JsonPropertyName("sourcePort")]
    public string SourcePort { get; init; } = string.Empty;

    [JsonPropertyName("destinationPort")]
    public string DestinationPort { get; init; } = string.Empty;

    /// <summary>嗅探到的域名；纯 IP 访问时为空。</summary>
    [JsonPropertyName("host")]
    public string Host { get; init; } = string.Empty;

    [JsonPropertyName("sniffHost")]
    public string SniffHost { get; init; } = string.Empty;

    [JsonPropertyName("inboundName")]
    public string InboundName { get; init; } = string.Empty;

    [JsonPropertyName("inboundIP")]
    public string InboundIp { get; init; } = string.Empty;

    [JsonPropertyName("inboundPort")]
    public string InboundPort { get; init; } = string.Empty;

    [JsonPropertyName("process")]
    public string Process { get; init; } = string.Empty;

    [JsonPropertyName("processPath")]
    public string ProcessPath { get; init; } = string.Empty;

    [JsonPropertyName("sourceIPASN")]
    public string SourceIpAsn { get; init; } = string.Empty;

    [JsonPropertyName("destinationIPASN")]
    public string DestinationIpAsn { get; init; } = string.Empty;

    [JsonPropertyName("dscp")]
    public int Dscp { get; init; }
}
