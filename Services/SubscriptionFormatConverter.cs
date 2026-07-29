using System.IO;
using System.Text;
using YamlDotNet.RepresentationModel;

namespace Wihomo.Services;

public sealed class SubscriptionFormatConverter
{
    /// <summary>
    /// Detects the format of subscription content and converts it to mihomo-compatible YAML if needed.
    /// Handles: YAML, base64-encoded YAML, base64-encoded proxy URIs (anytls, ss, trojan, etc.)
    /// </summary>
    public SubscriptionConvertResult Convert(string rawContent)
    {
        var trimmed = rawContent.Trim();

        // Already YAML (heuristic + parse check)
        if (IsYamlContent(trimmed))
        {
            return new SubscriptionConvertResult(trimmed, SubscriptionFormat.Yaml);
        }

        // Check if it's proxy URIs directly (not encoded)
        if (LooksLikeProxyUris(trimmed))
        {
            var yaml = ConvertProxyUrisToYaml(trimmed);
            if (yaml is not null)
            {
                return new SubscriptionConvertResult(yaml, SubscriptionFormat.ConvertedFromUris);
            }
        }

        // Try base64 decode (standard + URL-safe)
        var decoded = TryBase64Decode(trimmed);
        if (decoded is not null)
        {
            var result = ProcessDecodedContent(decoded);
            if (result is not null)
            {
                return result;
            }
        }

        // Try URL-safe base64 (replace - with + and _ with /)
        var urlSafeConverted = trimmed.Replace('-', '+').Replace('_', '/');
        if (!string.Equals(urlSafeConverted, trimmed, StringComparison.Ordinal))
        {
            decoded = TryBase64Decode(urlSafeConverted);
            if (decoded is not null)
            {
                var result = ProcessDecodedContent(decoded);
                if (result is not null)
                {
                    return result;
                }
            }
        }

        // Could not determine format
        return new SubscriptionConvertResult(trimmed, SubscriptionFormat.Unknown);
    }

    private static SubscriptionConvertResult? ProcessDecodedContent(string decoded)
    {
        var decodedTrimmed = decoded.Trim();

        // Decoded content is YAML
        if (IsYamlContent(decodedTrimmed))
        {
            return new SubscriptionConvertResult(decoded, SubscriptionFormat.Yaml);
        }

        // Decoded content is proxy URIs (one per line)
        if (LooksLikeProxyUris(decodedTrimmed))
        {
            var yaml = ConvertProxyUrisToYaml(decodedTrimmed);
            if (yaml is not null)
            {
                return new SubscriptionConvertResult(yaml, SubscriptionFormat.ConvertedFromUris);
            }
        }

        // Last resort: if it can be parsed as YAML at all, treat as YAML
        if (TryParseAsYaml(decodedTrimmed))
        {
            return new SubscriptionConvertResult(decoded, SubscriptionFormat.Yaml);
        }

        return null;
    }

    private static string? TryBase64Decode(string text)
    {
        if (text.Length < 20 || !LooksLikeBase64(text))
        {
            return null;
        }

        try
        {
            // Remove all whitespace before decoding
            var cleaned = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
            // Fix padding if needed
            var remainder = cleaned.Length % 4;
            if (remainder == 2)
            {
                cleaned += "==";
            }
            else if (remainder == 3)
            {
                cleaned += "=";
            }
            else if (remainder == 1)
            {
                return null; // Invalid base64 length
            }

            return Encoding.UTF8.GetString(System.Convert.FromBase64String(cleaned));
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeBase64(string text)
    {
        if (text.Length < 20)
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '+' && ch != '/' && ch != '='
                && ch != '\n' && ch != '\r' && ch != ' ' && ch != '\t')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsYamlContent(string text)
    {
        // Quick heuristic first
        if (LooksLikeYamlHeuristic(text))
        {
            return true;
        }

        // Fallback: actually try to parse as YAML
        return TryParseAsYaml(text);
    }

    private static bool TryParseAsYaml(string text)
    {
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(text);
            stream.Load(reader);
            return stream.Documents.Count > 0
                && stream.Documents[0].RootNode is YamlMappingNode;
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeYamlHeuristic(string text)
    {
        var lines = text.Split('\n');
        var nonEmptyLines = 0;
        var yamlIndicatorLines = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            nonEmptyLines++;

            // YAML indicators: key: value patterns (but not URLs like http://)
            if (line.Contains(':') && !line.StartsWith('-') && !line.Contains("://"))
            {
                yamlIndicatorLines++;
            }
        }

        // If most non-empty lines contain key: value patterns, it's likely YAML
        return nonEmptyLines >= 2 && yamlIndicatorLines >= 2;
    }

    private static bool LooksLikeProxyUris(string text)
    {
        var lines = text.Split('\n');
        var uriCount = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            if (IsProxyUri(line))
            {
                uriCount++;
            }
        }

        return uriCount > 0;
    }

    private static bool IsProxyUri(string line)
    {
        var schemeEnd = line.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            return false;
        }

        var scheme = line[..schemeEnd].Trim().ToLowerInvariant();
        return scheme is "anytls" or "ss" or "trojan" or "vmess" or "vless"
            or "tuic" or "hysteria" or "hysteria2" or "hy2" or "http"
            or "https" or "socks5" or "socks5s";
    }

    /// <summary>
    /// Converts proxy URI lines (one per line) to a mihomo-compatible YAML configuration.
    /// </summary>
    public static string? ConvertProxyUrisToYaml(string content)
    {
        var lines = content.Split('\n');
        var proxies = new List<Dictionary<string, object>>();
        var index = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            var proxy = ParseProxyUri(line, index);
            if (proxy is not null)
            {
                proxies.Add(proxy);
                index++;
            }
        }

        if (proxies.Count == 0)
        {
            return null;
        }

        return BuildMihomoYaml(proxies);
    }

    private static Dictionary<string, object>? ParseProxyUri(string uri, int index)
    {
        var schemeEnd = uri.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            return null;
        }

        var scheme = uri[..schemeEnd].Trim().ToLowerInvariant();

        return scheme switch
        {
            "anytls" => ParseAnyTlsUri(uri, schemeEnd, index),
            "ss" => ParseShadowsocksUri(uri, schemeEnd, index),
            "trojan" => ParseTrojanUri(uri, schemeEnd, index),
            _ => ParseGenericTlsUri(uri, schemeEnd, index, scheme)
        };
    }

    /// <summary>
    /// Parses anytls URI format: anytls://password@host:port[#name]
    /// or: anytls://host:port?password=xxx&amp;sni=xxx[#name]
    /// </summary>
    private static Dictionary<string, object>? ParseAnyTlsUri(string uri, int schemeEnd, int index)
    {
        var rest = uri[(schemeEnd + 3)..];
        var (beforeFragment, fragment) = SplitFragment(rest);

        string? name = null;
        if (!string.IsNullOrWhiteSpace(fragment))
        {
            name = Uri.UnescapeDataString(fragment).Trim();
        }

        var proxy = new Dictionary<string, object>
        {
            ["name"] = !string.IsNullOrWhiteSpace(name) ? name : $"AnyTLS-{index + 1}",
            ["type"] = "anytls",
            ["udp"] = true,
            ["client-fingerprint"] = "chrome"
        };

        // Check if it's the query-param style: host:port?password=xxx
        if (beforeFragment.Contains('?'))
        {
            var (hostPort, queryString) = SplitQuery(beforeFragment);

            // hostPort might still contain @ (user:pass@host:port?query)
            var atIndex = hostPort.LastIndexOf('@');
            string? userInfo = null;
            if (atIndex > 0)
            {
                userInfo = Uri.UnescapeDataString(hostPort[..atIndex]);
                hostPort = hostPort[(atIndex + 1)..];
            }

            var (host, port) = ParseHostPort(hostPort);
            if (host is null)
            {
                return null;
            }

            proxy["server"] = host;
            proxy["port"] = port;

            var queryParams = ParseQueryString(queryString);

            // Password priority: query param > userInfo
            if (queryParams.TryGetValue("password", out var pwd) && !string.IsNullOrWhiteSpace(pwd))
            {
                proxy["password"] = pwd;
            }
            else if (!string.IsNullOrWhiteSpace(userInfo))
            {
                proxy["password"] = userInfo;
            }
            else
            {
                proxy["password"] = string.Empty;
            }

            if (queryParams.TryGetValue("sni", out var sni))
            {
                proxy["sni"] = sni;
            }
            if (queryParams.TryGetValue("allowInsecure", out var allowInsecure)
                || queryParams.TryGetValue("skip-cert-verify", out allowInsecure))
            {
                proxy["skip-cert-verify"] = allowInsecure is "1" or "true";
            }
            if (queryParams.TryGetValue("alpn", out var alpn))
            {
                proxy["alpn"] = alpn.Split(',').Select(x => x.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            }
        }
        else
        {
            // password@host:port style
            var atIndex = beforeFragment.LastIndexOf('@');
            if (atIndex <= 0)
            {
                return null;
            }

            var password = Uri.UnescapeDataString(beforeFragment[..atIndex]);
            var (host, port) = ParseHostPort(beforeFragment[(atIndex + 1)..]);
            if (host is null)
            {
                return null;
            }

            proxy["server"] = host;
            proxy["port"] = port;
            proxy["password"] = password;
        }

        return proxy;
    }

    private static Dictionary<string, object>? ParseShadowsocksUri(string uri, int schemeEnd, int index)
    {
        var rest = uri[(schemeEnd + 3)..];
        var (beforeFragment, fragment) = SplitFragment(rest);
        var name = !string.IsNullOrWhiteSpace(fragment)
            ? Uri.UnescapeDataString(fragment).Trim()
            : $"SS-{index + 1}";

        // ss://base64(method:password)@host:port
        // or ss://base64(method:password@host:port)
        var atIndex = beforeFragment.IndexOf('@');

        if (atIndex > 0)
        {
            var userInfoBase64 = beforeFragment[..atIndex];
            var (host, port) = ParseHostPort(beforeFragment[(atIndex + 1)..]);
            if (host is null)
            {
                return null;
            }

            var userInfo = TryDecodeBase64(userInfoBase64);
            if (userInfo is null)
            {
                return null;
            }

            var colonIndex = userInfo.IndexOf(':');
            if (colonIndex <= 0)
            {
                return null;
            }

            var method = userInfo[..colonIndex];
            var password = userInfo[(colonIndex + 1)..];

            return new Dictionary<string, object>
            {
                ["name"] = name,
                ["type"] = "ss",
                ["server"] = host,
                ["port"] = port,
                ["cipher"] = method,
                ["password"] = password
            };
        }

        // ss://base64(method:password@host:port)
        var decoded = TryDecodeBase64(beforeFragment);
        if (decoded is null)
        {
            return null;
        }

        var innerAt = decoded.LastIndexOf('@');
        if (innerAt <= 0)
        {
            return null;
        }

        var innerUserInfo = decoded[..innerAt];
        var (innerHost, innerPort) = ParseHostPort(decoded[(innerAt + 1)..]);
        if (innerHost is null)
        {
            return null;
        }

        var innerColon = innerUserInfo.IndexOf(':');
        if (innerColon <= 0)
        {
            return null;
        }

        return new Dictionary<string, object>
        {
            ["name"] = name,
            ["type"] = "ss",
            ["server"] = innerHost,
            ["port"] = innerPort,
            ["cipher"] = innerUserInfo[..innerColon],
            ["password"] = innerUserInfo[(innerColon + 1)..]
        };
    }

    private static Dictionary<string, object>? ParseTrojanUri(string uri, int schemeEnd, int index)
    {
        return ParseGenericTlsUri(uri, schemeEnd, index, "trojan");
    }

    /// <summary>
    /// Generic parser for trojan/vless/tuic/hysteria style URIs:
    /// scheme://uuid_or_password@host:port?params[#name]
    /// </summary>
    private static Dictionary<string, object>? ParseGenericTlsUri(string uri, int schemeEnd, int index, string proxyType)
    {
        var rest = uri[(schemeEnd + 3)..];
        var (beforeFragment, fragment) = SplitFragment(rest);
        var name = !string.IsNullOrWhiteSpace(fragment)
            ? Uri.UnescapeDataString(fragment).Trim()
            : $"{proxyType.ToUpperInvariant()}-{index + 1}";

        var atIndex = beforeFragment.IndexOf('@');
        if (atIndex <= 0)
        {
            return null;
        }

        var userInfo = Uri.UnescapeDataString(beforeFragment[..atIndex]);
        var queryPart = string.Empty;
        var hostPortPart = beforeFragment[(atIndex + 1)..];

        var queryIndex = hostPortPart.IndexOf('?');
        if (queryIndex >= 0)
        {
            queryPart = hostPortPart[(queryIndex + 1)..];
            hostPortPart = hostPortPart[..queryIndex];
        }

        var (host, port) = ParseHostPort(hostPortPart);
        if (host is null)
        {
            return null;
        }

        var proxy = new Dictionary<string, object>
        {
            ["name"] = name,
            ["type"] = proxyType,
            ["server"] = host,
            ["port"] = port,
        };

        if (proxyType is "trojan" or "vless" or "tuic")
        {
            proxy["uuid"] = userInfo;
            proxy["udp"] = true;
        }
        else
        {
            proxy["password"] = userInfo;
        }

        if (!string.IsNullOrWhiteSpace(queryPart))
        {
            var queryParams = ParseQueryString(queryPart);

            if (queryParams.TryGetValue("sni", out var sni) || queryParams.TryGetValue("peer", out sni))
            {
                proxy["sni"] = sni;
            }
            if (queryParams.TryGetValue("allowInsecure", out var insecure) || queryParams.TryGetValue("skip-cert-verify", out insecure))
            {
                proxy["skip-cert-verify"] = insecure is "1" or "true";
            }
            if (queryParams.TryGetValue("alpn", out var alpn))
            {
                proxy["alpn"] = alpn.Split(',').Select(x => x.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            }
            if (queryParams.TryGetValue("type", out var transport) && !string.IsNullOrWhiteSpace(transport))
            {
                proxy["network"] = transport.ToLowerInvariant();
            }
        }

        return proxy;
    }

    private static string BuildMihomoYaml(List<Dictionary<string, object>> proxies)
    {
        var sb = new StringBuilder();

        sb.AppendLine("proxies:");
        foreach (var proxy in proxies)
        {
            var first = true;
            foreach (var (key, value) in proxy)
            {
                var prefix = first ? "  - " : "    ";
                first = false;

                if (value is List<string> list)
                {
                    sb.AppendLine($"{prefix}{key}:");
                    foreach (var item in list)
                    {
                        sb.AppendLine($"      - {EscapeYamlValue(item)}");
                    }
                }
                else if (value is bool boolValue)
                {
                    sb.AppendLine($"{prefix}{key}: {boolValue.ToString().ToLowerInvariant()}");
                }
                else
                {
                    sb.AppendLine($"{prefix}{key}: {EscapeYamlValue(value?.ToString() ?? string.Empty)}");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("proxy-groups:");

        var proxyNames = proxies.Select(p => p["name"]?.ToString() ?? string.Empty).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

        sb.AppendLine("  - name: AUTO");
        sb.AppendLine("    type: url-test");
        sb.AppendLine("    proxies:");
        foreach (var proxyName in proxyNames)
        {
            sb.AppendLine($"      - {EscapeYamlValue(proxyName)}");
        }
        sb.AppendLine("    url: https://www.gstatic.com/generate_204");
        sb.AppendLine("    interval: 300");
        sb.AppendLine("    tolerance: 50");

        sb.AppendLine("  - name: PROXY");
        sb.AppendLine("    type: select");
        sb.AppendLine("    proxies:");
        sb.AppendLine("      - AUTO");
        foreach (var proxyName in proxyNames)
        {
            sb.AppendLine($"      - {EscapeYamlValue(proxyName)}");
        }
        sb.AppendLine("      - DIRECT");

        sb.AppendLine("  - name: SELECT");
        sb.AppendLine("    type: select");
        sb.AppendLine("    proxies:");
        sb.AppendLine("      - PROXY");
        sb.AppendLine("      - DIRECT");

        sb.AppendLine();
        sb.AppendLine("rules:");
        sb.AppendLine("  - GEOIP,CN,DIRECT");
        sb.AppendLine("  - GEOSITE,CN,DIRECT");
        sb.AppendLine("  - MATCH,PROXY");

        return sb.ToString();
    }

    private static string EscapeYamlValue(string value)
    {
        // Sanitize: remove control characters and newlines
        value = new string(value.Where(c => !char.IsControl(c)).ToArray());

        // Empty or whitespace-only values must be quoted
        if (string.IsNullOrWhiteSpace(value))
        {
            return "''";
        }

        // Always quote if the value contains any potentially problematic character
        if (value.Contains(':') || value.Contains('#') || value.Contains('\'')
            || value.Contains('{') || value.Contains('}') || value.Contains('[')
            || value.Contains(']') || value.Contains(',') || value.Contains('&')
            || value.Contains('*') || value.Contains('?') || value.Contains('|')
            || value.Contains('-') || value.Contains('<') || value.Contains('>')
            || value.Contains('=') || value.Contains('!') || value.Contains('%')
            || value.Contains('@') || value.Contains('`') || value.Contains('"')
            || value.Contains('\\') || value.Contains('\t')
            || value.StartsWith(' ') || value.EndsWith(' ')
            || value.StartsWith("'") || value.StartsWith("\"")
            || IsYamlReservedWord(value))
        {
            return $"'{value.Replace("'", "''")}'";
        }

        return value;
    }

    private static bool IsYamlReservedWord(string value)
    {
        return value.ToLowerInvariant() is "null" or "true" or "false" or "yes" or "no"
            or "on" or "off" or "~";
    }

    private static (string beforeFragment, string fragment) SplitFragment(string text)
    {
        var hashIndex = text.IndexOf('#');
        if (hashIndex < 0)
        {
            return (text, string.Empty);
        }

        return (text[..hashIndex], text[(hashIndex + 1)..]);
    }

    private static (string hostPort, string query) SplitQuery(string text)
    {
        var queryIndex = text.IndexOf('?');
        if (queryIndex < 0)
        {
            return (text, string.Empty);
        }

        return (text[..queryIndex], text[(queryIndex + 1)..]);
    }

    private static (string? host, int port) ParseHostPort(string text)
    {
        text = text.Trim();

        // IPv6: [::1]:port
        if (text.StartsWith('['))
        {
            var bracketEnd = text.IndexOf(']');
            if (bracketEnd < 0)
            {
                return (null, 0);
            }

            var host = text[1..bracketEnd];
            var port = 443;

            if (bracketEnd + 1 < text.Length && text[bracketEnd + 1] == ':')
            {
                var portStr = text[(bracketEnd + 2)..];
                if (!int.TryParse(portStr, out port))
                {
                    port = 443;
                }
            }

            return (host, port);
        }

        var lastColon = text.LastIndexOf(':');
        if (lastColon <= 0)
        {
            return (text, 443);
        }

        var hostPart = text[..lastColon];
        var portStr2 = text[(lastColon + 1)..];
        if (!int.TryParse(portStr2, out var port2))
        {
            port2 = 443;
        }

        return (hostPart, port2);
    }

    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
        {
            return result;
        }

        foreach (var pair in query.Split('&'))
        {
            var eqIndex = pair.IndexOf('=');
            if (eqIndex <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..eqIndex]);
            var value = Uri.UnescapeDataString(pair[(eqIndex + 1)..]);
            result[key] = value;
        }

        return result;
    }

    private static string? TryDecodeBase64(string text)
    {
        try
        {
            var cleaned = text.Trim();
            if (cleaned.Length % 4 != 0)
            {
                var padding = (4 - cleaned.Length % 4) % 4;
                cleaned = cleaned + new string('=', padding);
            }

            return Encoding.UTF8.GetString(System.Convert.FromBase64String(cleaned));
        }
        catch
        {
            return null;
        }
    }
}

public enum SubscriptionFormat
{
    Yaml,
    ConvertedFromUris,
    Unknown
}

public sealed record SubscriptionConvertResult(string Content, SubscriptionFormat Format);
