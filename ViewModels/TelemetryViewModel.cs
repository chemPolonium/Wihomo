using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using Wihomo.Services;
using Wihomo.Services.Realtime;

namespace Wihomo.ViewModels;

/// <summary>连接表行。列名沿用现有 DataGrid 绑定，格式化规则与迁移前保持一致。</summary>
public sealed partial class ConnectionRowViewModel : ObservableObject
{
    [ObservableProperty] private string _source = "-";
    [ObservableProperty] private string _destination = "-";
    [ObservableProperty] private string _type = "-";
    [ObservableProperty] private string _usedProxy = "-";
    [ObservableProperty] private string _rule = "-";
    [ObservableProperty] private string _speed = "-";

    public required string Id { get; init; }

    internal long UploadBytes { get; set; }

    internal long DownloadBytes { get; set; }

    internal DateTimeOffset SampledAt { get; set; }
}

/// <summary>
/// 概览页统计与连接表。速率直接取自 /traffic，不再由客户端差分累计值。
/// 每条连接的速率仍需差分，因为内核只在 /connections 里给出该连接的累计字节。
/// </summary>
public sealed partial class TelemetryViewModel : ObservableObject
{
    private const string NoConnections = "暂无当前连接";
    private const string CoreNotRunning = "内核未运行";

    [ObservableProperty] private string _versionText = "内核版本: -";
    [ObservableProperty] private string _connectionsText = "当前连接数: -";
    [ObservableProperty] private string _downloadTotalText = "累计下载: -";
    [ObservableProperty] private string _uploadTotalText = "累计上传: -";
    [ObservableProperty] private string _downloadRateText = "下载速率: -";
    [ObservableProperty] private string _uploadRateText = "上传速率: -";
    [ObservableProperty] private string _systemProxyStatusText = "系统代理: 未启用";
    [ObservableProperty] private string _tunStatusText = "TUN: 未启用";
    [ObservableProperty] private bool _isCoreRunning;

    public ObservableCollection<ConnectionRowViewModel> Connections { get; } = [];

    public void Update(TrafficFrame frame)
    {
        DownloadTotalText = $"累计下载: {SettingsParsing.FormatBytes(frame.DownTotal)}";
        UploadTotalText = $"累计上传: {SettingsParsing.FormatBytes(frame.UpTotal)}";
        DownloadRateText = $"下载速率: {SettingsParsing.FormatBytes(frame.Down)}/s";
        UploadRateText = $"上传速率: {SettingsParsing.FormatBytes(frame.Up)}/s";
    }

    public void Update(ConnectionsFrame frame)
    {
        ConnectionsText = $"当前连接数: {frame.ActiveCount}";
        ApplyConnections(frame.Connections);
    }

    public void ShowCoreStopped()
    {
        ConnectionsText = "当前连接数: -";
        DownloadRateText = "下载速率: -";
        UploadRateText = "上传速率: -";
        ReplacePlaceholder(CoreNotRunning);
    }

    /// <summary>内核重启后累计值归零，残留的行间快照会让速率算出巨大的假值。</summary>
    public void ResetRates()
    {
        Connections.Clear();
    }

    public void UpdateRuntimeIndicators(bool systemProxyEnabled, int mixedPort, bool tunEnabled, string tunStack)
    {
        SystemProxyStatusText = systemProxyEnabled
            ? $"系统代理: 已配置为启动时启用 ({mixedPort})"
            : "系统代理: 未启用";
        TunStatusText = tunEnabled ? $"TUN: 已启用 ({tunStack})" : "TUN: 未启用";
    }

    private void ApplyConnections(CoreConnection[]? incoming)
    {
        if (incoming is null || incoming.Length == 0)
        {
            ReplacePlaceholder(NoConnections);
            return;
        }

        var sampledAt = DateTimeOffset.UtcNow;
        var alive = new HashSet<string>(StringComparer.Ordinal);

        foreach (var connection in incoming)
        {
            var key = ConnectionKey(connection);
            alive.Add(key);

            var row = Connections.FirstOrDefault(x => x.Id == key);
            if (row is null)
            {
                row = new ConnectionRowViewModel { Id = key };
                Connections.Add(row);
                row.Source = Dash(FormatEndpoint(connection.Metadata.SourceIp, connection.Metadata.SourcePort));
                row.Destination = Dash(FormatDestination(connection.Metadata));
                row.Type = Dash(string.IsNullOrWhiteSpace(connection.Metadata.Type)
                    ? connection.Metadata.Network
                    : connection.Metadata.Type);
                row.UsedProxy = Dash(connection.UsedProxy);
                row.Rule = Dash(connection.Rule);
                row.SampledAt = sampledAt;
                row.Speed = $"↑ {SettingsParsing.FormatBytes(0)}/s ↓ {SettingsParsing.FormatBytes(0)}/s";
            }
            else
            {
                var seconds = Math.Max((sampledAt - row.SampledAt).TotalSeconds, 0.001d);
                var uploadRate = Math.Max(0d, (connection.Upload - row.UploadBytes) / seconds);
                var downloadRate = Math.Max(0d, (connection.Download - row.DownloadBytes) / seconds);
                row.Speed = $"↑ {SettingsParsing.FormatBytes((long)uploadRate)}/s ↓ {SettingsParsing.FormatBytes((long)downloadRate)}/s";
            }

            row.UploadBytes = connection.Upload;
            row.DownloadBytes = connection.Download;
            row.SampledAt = sampledAt;
        }

        for (var index = Connections.Count - 1; index >= 0; index--)
        {
            if (!alive.Contains(Connections[index].Id))
            {
                Connections.RemoveAt(index);
            }
        }
    }

    private void ReplacePlaceholder(string message)
    {
        if (Connections.Count == 1 && Connections[0].Source == message)
        {
            return;
        }

        Connections.Clear();
        Connections.Add(new ConnectionRowViewModel { Id = message, Source = message });
    }

    private static string ConnectionKey(CoreConnection connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.Id))
        {
            return connection.Id;
        }

        var metadata = connection.Metadata;
        return $"{metadata.SourceIp}|{metadata.SourcePort}|{metadata.Host}|{metadata.DestinationIp}|{metadata.DestinationPort}";
    }

    private static string FormatEndpoint(string address, string port)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(port) ? address : $"{address}:{port}";
    }

    private static string FormatDestination(ConnectionMetadata metadata)
    {
        var endpoint = FormatEndpoint(metadata.DestinationIp, metadata.DestinationPort);
        var host = metadata.Host;

        if (string.IsNullOrWhiteSpace(host))
        {
            return endpoint;
        }

        return string.IsNullOrWhiteSpace(endpoint) ? host : $"{host} ({endpoint})";
    }

    private static string Dash(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;
}
