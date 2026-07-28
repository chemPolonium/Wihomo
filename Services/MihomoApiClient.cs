using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using System.Text.Json.Serialization;
using Wihomo.Models;

namespace Wihomo.Services;

public sealed class MihomoApiClient
{
    private HttpClient _httpClient = new();
    private readonly Dictionary<string, ConnectionTrafficSnapshot> _connectionTrafficSnapshots = new(StringComparer.Ordinal);

    public void Configure(string host, int port, string secret)
    {
        _httpClient.Dispose();
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri($"http://{host}:{port}/")
        };

        if (!string.IsNullOrWhiteSpace(secret))
        {
            var authValue = "Bearer " + secret;
            _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
        }
    }

    public async Task<string> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        var result = await _httpClient.GetFromJsonAsync<MihomoVersionResponse>("version", cancellationToken);
        return result?.Version ?? "unknown";
    }

    public async Task<ConnectionStats> GetConnectionStatsAsync(
        ConnectionStats? previous,
        DateTimeOffset? previousTimestamp,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetFromJsonAsync<MihomoConnectionsResponse>("connections", cancellationToken)
            ?? throw new InvalidOperationException("Failed to parse /connections response.");

        var now = DateTimeOffset.UtcNow;
        var stats = new ConnectionStats
        {
            DownloadTotal = response.DownloadTotal,
            UploadTotal = response.UploadTotal,
            ActiveConnections = response.Connections?.Count ?? 0
        };

        if (previous is not null && previousTimestamp is not null)
        {
            var seconds = Math.Max((now - previousTimestamp.Value).TotalSeconds, 0.001d);
            stats.DownloadBytesPerSecond = Math.Max(0d, (stats.DownloadTotal - previous.DownloadTotal) / seconds);
            stats.UploadBytesPerSecond = Math.Max(0d, (stats.UploadTotal - previous.UploadTotal) / seconds);
        }

        return stats;
    }

    public async Task<List<string>> GetRulesAsync(CancellationToken cancellationToken = default)
    {
        using var stream = await _httpClient.GetStreamAsync("rules", cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = document.RootElement;
        var rulesElement = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("rules", out var property) && property.ValueKind == JsonValueKind.Array
                ? property
                : default;

        if (rulesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var rules = new List<string>();
        foreach (var item in rulesElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    rules.Add(text);
                }
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var payload = item.TryGetProperty("payload", out var payloadElement) && payloadElement.ValueKind == JsonValueKind.String
                ? payloadElement.GetString() ?? string.Empty
                : string.Empty;
            var proxy = item.TryGetProperty("proxy", out var proxyElement) && proxyElement.ValueKind == JsonValueKind.String
                ? proxyElement.GetString() ?? string.Empty
                : string.Empty;
            var type = item.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(payload) && string.IsNullOrWhiteSpace(proxy) && string.IsNullOrWhiteSpace(type))
            {
                continue;
            }

            var summary = string.Join(" | ", new[]
            {
                string.IsNullOrWhiteSpace(type) ? null : type,
                string.IsNullOrWhiteSpace(payload) ? null : payload,
                string.IsNullOrWhiteSpace(proxy) ? null : proxy
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

            rules.Add(summary);
        }

        return rules;
    }

    public async Task<List<ConnectionInfo>> GetConnectionsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("connections", cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        var connectionsElement = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("connections", out var property) && property.ValueKind == JsonValueKind.Array
                ? property
                : (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array ? data : default);

        if (connectionsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var capturedAt = DateTimeOffset.UtcNow;
        var activeConnectionIds = new HashSet<string>(StringComparer.Ordinal);
        var connections = new List<ConnectionInfo>();
        foreach (var item in connectionsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = GetString(item, "id");
            var metadata = item.TryGetProperty("metadata", out var metadataElement) && metadataElement.ValueKind == JsonValueKind.Object
                ? metadataElement
                : default;

            var source = GetString(metadata, "sourceIP");
            var sourcePort = GetStringOrNumber(metadata, "sourcePort");
            var destination = GetString(metadata, "destinationIP");
            var host = GetString(metadata, "host");
            var destinationPort = GetStringOrNumber(metadata, "destinationPort");
            var network = GetString(metadata, "network");
            var type = GetString(metadata, "type");
            var rule = GetString(item, "rule");
            var usedProxy = item.TryGetProperty("chains", out var chainsElement) && chainsElement.ValueKind == JsonValueKind.Array
                ? chainsElement.EnumerateArray()
                    .Select(x => x.GetString())
                    .LastOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty
                : string.Empty;
            var upload = TryGetLong(item, "upload");
            var download = TryGetLong(item, "download");

            var connectionKey = string.IsNullOrWhiteSpace(id)
                ? $"{source}|{sourcePort}|{host}|{destination}|{destinationPort}"
                : id;
            activeConnectionIds.Add(connectionKey);

            var uploadRate = 0d;
            var downloadRate = 0d;
            if (_connectionTrafficSnapshots.TryGetValue(connectionKey, out var previous))
            {
                var seconds = Math.Max((capturedAt - previous.CapturedAt).TotalSeconds, 0.001d);
                uploadRate = Math.Max(0d, (upload - previous.Upload) / seconds);
                downloadRate = Math.Max(0d, (download - previous.Download) / seconds);
            }

            _connectionTrafficSnapshots[connectionKey] = new ConnectionTrafficSnapshot(upload, download, capturedAt);

            var sourceText = FormatEndpoint(source, sourcePort);
            var destinationEndpoint = FormatEndpoint(destination, destinationPort);
            var destinationText = string.IsNullOrWhiteSpace(host)
                ? destinationEndpoint
                : string.IsNullOrWhiteSpace(destinationEndpoint)
                    ? host
                    : $"{host} ({destinationEndpoint})";
            var requestType = !string.IsNullOrWhiteSpace(type) ? type : network;

            connections.Add(new ConnectionInfo
            {
                Source = DisplayOrDash(sourceText),
                Destination = DisplayOrDash(destinationText),
                Type = DisplayOrDash(requestType),
                UsedProxy = DisplayOrDash(usedProxy),
                Rule = DisplayOrDash(rule),
                Speed = $"↑ {FormatBytes((long)uploadRate)}/s ↓ {FormatBytes((long)downloadRate)}/s"
            });
        }

        _connectionTrafficSnapshots.Keys
            .Where(key => !activeConnectionIds.Contains(key))
            .ToList()
            .ForEach(key => _connectionTrafficSnapshots.Remove(key));

        return connections;
    }

    private static string FormatEndpoint(string address, string port)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(port) ? address : $"{address}:{port}";
    }

    private static string DisplayOrDash(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private static long TryGetLong(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var property)
            && property.ValueKind is JsonValueKind.Number
            ? property.GetInt64()
            : 0L;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0d, bytes);
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string GetStringOrNumber(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            _ => string.Empty
        };
    }

    public async Task UpdateProxyProviderAsync(string providerName, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"providers/proxies/{Uri.EscapeDataString(providerName)}");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<List<ProxyGroupInfo>> GetProxyGroupsAsync(CancellationToken cancellationToken = default)
    {
        using var stream = await _httpClient.GetStreamAsync("proxies", cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("proxies", out var proxiesElement))
        {
            throw new InvalidOperationException("Failed to parse /proxies response.");
        }

        var groups = new List<ProxyGroupInfo>();
        foreach (var property in proxiesElement.EnumerateObject())
        {
            if (!property.Value.TryGetProperty("all", out var allElement) || allElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var options = allElement.EnumerateArray()
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>()
                .ToList();

            if (options.Count == 0)
            {
                continue;
            }

            var hidden = property.Value.TryGetProperty("hidden", out var hiddenElement)
                && hiddenElement.ValueKind == JsonValueKind.True;
            if (hidden)
            {
                continue;
            }

            groups.Add(new ProxyGroupInfo
            {
                Name = property.Name,
                Type = property.Value.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? string.Empty : string.Empty,
                Current = property.Value.TryGetProperty("now", out var nowElement) ? nowElement.GetString() ?? string.Empty : string.Empty,
                Options = options
            });
        }

        return groups;
    }

    public async Task SelectProxyAsync(string groupName, string proxyName, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutAsJsonAsync(
            $"proxies/{Uri.EscapeDataString(groupName)}",
            new ProxySelectionRequest { Name = proxyName },
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<int?> TestProxyDelayAsync(
        string proxyName,
        string url,
        int timeoutMilliseconds,
        CancellationToken cancellationToken = default)
    {
        var path =
            $"proxies/{Uri.EscapeDataString(proxyName)}/delay?url={Uri.EscapeDataString(url)}&timeout={timeoutMilliseconds}";
        var response = await _httpClient.GetFromJsonAsync<MihomoDelayResponse>(path, cancellationToken);
        return response?.Delay;
    }

    public async Task<List<string>> GetTestableProxyNamesAsync(CancellationToken cancellationToken = default)
    {
        using var stream = await _httpClient.GetStreamAsync("proxies", cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("proxies", out var proxiesElement)
            || proxiesElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Failed to parse /proxies response.");
        }

        var excludedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Direct",
            "Reject",
            "Compatible",
            "Pass"
        };
        var nodes = new List<string>();
        foreach (var property in proxiesElement.EnumerateObject())
        {
            var value = property.Value;
            var isGroup = value.TryGetProperty("all", out var allElement)
                && allElement.ValueKind == JsonValueKind.Array;
            var type = value.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString() ?? string.Empty
                : string.Empty;

            if (!isGroup && !excludedTypes.Contains(type))
            {
                nodes.Add(property.Name);
            }
        }

        return nodes;
    }

    public async Task<SubscriptionDownloadResult> DownloadSubscriptionAsync(string url, CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient();
        using var response = await client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var userInfo = response.Headers.TryGetValues("Subscription-Userinfo", out var values)
            ? values.FirstOrDefault()
            : response.Content.Headers.TryGetValues("Subscription-Userinfo", out var contentValues)
                ? contentValues.FirstOrDefault()
                : null;
        return new SubscriptionDownloadResult(content, userInfo);
    }

    private sealed class MihomoVersionResponse
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;
    }

    private sealed class MihomoConnectionsResponse
    {
        [JsonPropertyName("downloadTotal")]
        public long DownloadTotal { get; set; }

        [JsonPropertyName("uploadTotal")]
        public long UploadTotal { get; set; }

        [JsonPropertyName("connections")]
        public List<object>? Connections { get; set; }
    }

    private sealed class ProxySelectionRequest
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    private sealed class MihomoDelayResponse
    {
        [JsonPropertyName("delay")]
        public int Delay { get; set; }
    }

    private sealed record ConnectionTrafficSnapshot(long Upload, long Download, DateTimeOffset CapturedAt);
}

public sealed record SubscriptionDownloadResult(string Content, string? UserInfo);
