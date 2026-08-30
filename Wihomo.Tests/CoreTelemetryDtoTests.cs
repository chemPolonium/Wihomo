using System.IO;
using System.Text.Json;
using Wihomo.Services.Realtime;

namespace Wihomo.Tests;

/// <summary>
/// 用 v1.19.29 原始抓包帧验证 DTO。字段名或类型一旦与实测不符，这里就会红。
/// </summary>
public class CoreTelemetryDtoTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private sealed class Capture
    {
        public ObservedObserved Observed { get; set; } = new();
        public string[] Traffic { get; set; } = [];
        public string[] Memory { get; set; } = [];
        public string[] Logs { get; set; } = [];
        public string[] Connections { get; set; } = [];
    }

    private sealed class ObservedObserved
    {
        public string CoreVersion { get; set; } = string.Empty;
        public string Authentication { get; set; } = string.Empty;
        public string[] WebSocketPaths { get; set; } = [];
    }

    private static readonly Capture Fixture = Load();

    private static Capture Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Protocol", "mihomo-1.19.29-ws-frames.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Capture>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidOperationException("无法读取抓包夹具。");
    }

    [Fact]
    public void Fixture_DescribesMeasuredCore()
    {
        Assert.Equal("v1.19.29", Fixture.Observed.CoreVersion);
        Assert.Equal(["/traffic", "/connections", "/logs", "/memory"], Fixture.Observed.WebSocketPaths);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0)]
    [InlineData(1, 308, 264, 308, 264)]
    [InlineData(2, 308, 16777960, 616, 16778224)]
    public void TrafficFrame_MapsUpAndDownAsRates(int index, long up, long down, long upTotal, long downTotal)
    {
        var frame = JsonSerializer.Deserialize<TrafficFrame>(Fixture.Traffic[index], Options);

        Assert.NotNull(frame);
        Assert.Equal(up, frame.Up);
        Assert.Equal(down, frame.Down);
        Assert.Equal(upTotal, frame.UpTotal);
        Assert.Equal(downTotal, frame.DownTotal);
    }

    [Fact]
    public void TrafficAndConnectionsUseDifferentCumulativeKeyNames()
    {
        // 实测：/traffic 用 upTotal/downTotal，/connections 用 uploadTotal/downloadTotal。
        // 交叉反序列化必须得到全零，否则说明两个流的键名被混用了。
        var trafficAsConnections = JsonSerializer.Deserialize<ConnectionsFrame>(Fixture.Traffic[2], Options)!;
        Assert.Equal(0, trafficAsConnections.DownloadTotal);
        Assert.Equal(0, trafficAsConnections.UploadTotal);
        Assert.Null(trafficAsConnections.Connections);

        var connectionsAsTraffic = JsonSerializer.Deserialize<TrafficFrame>(Fixture.Connections[2], Options)!;
        Assert.Equal(0, connectionsAsTraffic.UpTotal);
        Assert.Equal(0, connectionsAsTraffic.DownTotal);

        var connections = JsonSerializer.Deserialize<ConnectionsFrame>(Fixture.Connections[2], Options)!;
        Assert.Equal(770, connections.UploadTotal);
        Assert.Equal(17188184, connections.DownloadTotal);
    }

    [Fact]
    public void ConnectionsFrame_NullConnectionsMeansZeroActive()
    {
        // 内核实测在无活跃连接时发 null 而非 []。
        var frame = JsonSerializer.Deserialize<ConnectionsFrame>(Fixture.Connections[0], Options)!;

        Assert.Null(frame.Connections);
        Assert.Equal(0, frame.ActiveCount);
    }

    [Fact]
    public void ConnectionsFrame_ActiveSnapshotMapsEveryObservedField()
    {
        var frame = JsonSerializer.Deserialize<ConnectionsFrame>(Fixture.Connections[2], Options)!;

        Assert.Equal(2, frame.ActiveCount);
        Assert.Equal(26157056, frame.Memory);

        var first = frame.Connections![0];
        Assert.Equal("0a22d296-b59f-494e-becf-380db2fa421a", first.Id);
        Assert.Equal(77, first.Upload);
        Assert.Equal(204980, first.Download);
        Assert.Equal("Match", first.Rule);
        Assert.Equal("", first.RulePayload);
        Assert.Equal(["DIRECT"], first.Chains);
        Assert.Equal("DIRECT", first.UsedProxy);
        Assert.Equal(8, first.Start.Offset.Hours);
        Assert.Equal(2026, first.Start.Year);
        Assert.True(first.Start > DateTimeOffset.MinValue);
    }

    [Fact]
    public void ConnectionMetadata_PortsStayStringsBecauseCoreSendsStrings()
    {
        var frame = JsonSerializer.Deserialize<ConnectionsFrame>(Fixture.Connections[2], Options)!;
        var metadata = frame.Connections![0].Metadata;

        // 若把这些字段声明成 int，System.Text.Json 会在这三处直接抛异常。
        Assert.Equal("57734", metadata.SourcePort);
        Assert.Equal("18081", metadata.DestinationPort);
        Assert.Equal("17890", metadata.InboundPort);
        Assert.Equal("tcp", metadata.Network);
        Assert.Equal("HTTP", metadata.Type);
        Assert.Equal("DEFAULT-MIXED", metadata.InboundName);
        Assert.Equal("127.0.0.1", metadata.SourceIp);
        Assert.Equal("", metadata.Host);
        Assert.Equal(0, metadata.Dscp);
    }

    [Theory]
    [InlineData(new[] { "DIRECT" }, "DIRECT")]
    [InlineData(new[] { "SELECT", "PROXY", "HK-01" }, "HK-01")]
    [InlineData(new string[0], null)]
    public void UsedProxy_IsLastChainEntry(string[] chains, string? expected)
    {
        // 真实抓包里 chains 恒为 ["DIRECT"]，多跳与空链在此直接构造以验证取末位的语义。
        var connection = new CoreConnection { Chains = chains };

        Assert.Equal(expected, connection.UsedProxy);
    }

    [Fact]
    public void LogFrame_TypeAndPayloadOnly_WithNoTimestampField()
    {
        var frame = JsonSerializer.Deserialize<LogFrame>(Fixture.Logs[1], Options)!;

        Assert.Equal("info", frame.Type);
        Assert.Equal("[TCP] 127.0.0.1:57679 --> 127.0.0.1:18080 match Match using DIRECT", frame.Payload);
        Assert.DoesNotContain("\"time\"", Fixture.Logs[1], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("timestamp", Fixture.Logs[1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MalformedFrame_ThrowsRatherThanSilentlyReturningNull()
    {
        // 解析异常由 MihomoWebSocketStream 计数并保留连接，这里确认异常确实会发生。
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<TrafficFrame>("{not json", Options));
    }
}
