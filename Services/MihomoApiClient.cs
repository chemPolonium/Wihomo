using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using System.Text.Json.Serialization;
using Wihomo.Models;
using Wihomo.Services.Realtime;

namespace Wihomo.Services;

public sealed class MihomoApiClient : IDisposable
{
    private HttpClient _httpClient = CreateNoProxyClient();
    private string? _configuredEndpoint;

    private static HttpClient CreateNoProxyClient()
    {
        var handler = new SocketsHttpHandler { UseProxy = false, Proxy = null };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>
    /// 每个刷新 tick 都会被调用，因此只在端点真正变化时重建客户端，避免 socket churn。
    /// </summary>
    public void Configure(string host, int port, string secret)
    {
        var endpoint = $"{host}|{port}|{secret}";
        if (string.Equals(endpoint, _configuredEndpoint, StringComparison.Ordinal))
        {
            return;
        }

        var previous = _httpClient;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            Proxy = null
        };
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://{host}:{port}/"),
            Timeout = TimeSpan.FromSeconds(10)
        };

        if (!string.IsNullOrWhiteSpace(secret))
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
        }

        _httpClient = client;
        _configuredEndpoint = endpoint;
        previous.Dispose();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    public async Task<string> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        var result = await _httpClient.GetFromJsonAsync<MihomoVersionResponse>("version", cancellationToken);
        return result?.Version ?? "unknown";
    }

    public async Task ReloadConfigsAsync(string configPath, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new { path = configPath });
        var request = new HttpRequestMessage(HttpMethod.Put, "configs?force=true")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// 与 /connections WebSocket 推送同构的一次性快照，供推送断开时降级轮询复用同一条渲染路径。
    /// </summary>
    public async Task<ConnectionsFrame> GetConnectionsFrameAsync(CancellationToken cancellationToken = default)
    {
        return await _httpClient.GetFromJsonAsync<ConnectionsFrame>(
                   "connections", Realtime.CoreJson.Options, cancellationToken)
            ?? throw new InvalidOperationException("Failed to parse /connections response.");
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
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            Proxy = null
        };
        using var client = new HttpClient(handler);
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

}

public sealed record SubscriptionDownloadResult(string Content, string? UserInfo);
