using System.Text;
using Wihomo.Models;

namespace Wihomo.Services;

public sealed class MihomoConfigBuilder
{
    public string Build(AppSettings settings, HashSet<string>? localProviders = null)
    {
        var enabledProviders = settings.Subscriptions
            .Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Url))
            .Select(x => new
            {
                ProviderKey = NormalizeName(x.Name),
                Url = x.Url.Trim(),
                IntervalSeconds = x.IntervalSeconds
            })
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"mixed-port: {Math.Max(settings.Core.MixedPort, 1)}");
        sb.AppendLine($"socks-port: {Math.Max(settings.Core.SocksPort, 1)}");
        sb.AppendLine($"port: {Math.Max(settings.Core.HttpPort, 1)}");
        sb.AppendLine("allow-lan: false");
        sb.AppendLine("mode: rule");
        sb.AppendLine("log-level: info");
        sb.AppendLine("ipv6: true");
        sb.AppendLine($"external-controller: {settings.Core.ExternalControllerHost}:{settings.Core.ExternalControllerPort}");
        sb.AppendLine($"secret: {EscapeYamlString(settings.Core.Secret)}");
        sb.AppendLine("profile:");
        sb.AppendLine("  store-selected: true");
        sb.AppendLine("  store-fake-ip: true");
        sb.AppendLine();
        sb.AppendLine("tun:");
        sb.AppendLine($"  enable: {settings.Core.EnableTun.ToString().ToLowerInvariant()}");
        sb.AppendLine($"  stack: {NormalizeTunStack(settings.Core.TunStack)}");
        sb.AppendLine("  auto-route: true");
        sb.AppendLine("  auto-detect-interface: true");
        sb.AppendLine("  strict-route: true");
        sb.AppendLine();

        if (settings.Dns.Enable)
        {
            sb.AppendLine("dns:");
            sb.AppendLine($"  enable: true");
            sb.AppendLine($"  listen: \"{settings.Dns.Listen}\"");
            sb.AppendLine($"  ipv6: {settings.Dns.Ipv6.ToString().ToLowerInvariant()}");
            sb.AppendLine($"  enhanced-mode: {settings.Dns.EnhancedMode}");
            sb.AppendLine($"  fake-ip-range: {settings.Dns.FakeIpRange}");
            sb.AppendLine("  respect-rules: true");
            sb.AppendLine("  proxy-server-nameserver:");
            sb.AppendLine("    - 1.1.1.1");
            sb.AppendLine("    - 8.8.8.8");

            if (settings.Dns.FakeIpFilter.Count > 0)
            {
                sb.AppendLine("  fake-ip-filter:");
                foreach (var filter in settings.Dns.FakeIpFilter)
                {
                    sb.AppendLine($"    - \"{filter}\"");
                }
            }

            if (settings.Dns.DefaultNameserver.Count > 0)
            {
                sb.AppendLine("  default-nameserver:");
                foreach (var ns in settings.Dns.DefaultNameserver)
                {
                    sb.AppendLine($"    - {ns}");
                }
            }

            if (settings.Dns.Nameserver.Count > 0)
            {
                sb.AppendLine("  nameserver:");
                foreach (var ns in settings.Dns.Nameserver)
                {
                    sb.AppendLine($"    - \"{ns}\"");
                }
            }

            if (settings.Dns.Fallback.Count > 0)
            {
                sb.AppendLine("  fallback:");
                foreach (var ns in settings.Dns.Fallback)
                {
                    sb.AppendLine($"    - \"{ns}\"");
                }
            }

            if (settings.Dns.FallbackFilterGeoIp || settings.Dns.FallbackFilterIpCidr.Count > 0)
            {
                sb.AppendLine("  fallback-filter:");
                sb.AppendLine($"    geoip: {settings.Dns.FallbackFilterGeoIp.ToString().ToLowerInvariant()}");
                sb.AppendLine($"    geoip-code: {settings.Dns.FallbackFilterGeoIpCode}");
                if (settings.Dns.FallbackFilterIpCidr.Count > 0)
                {
                    sb.AppendLine("    ipcidr:");
                    foreach (var cidr in settings.Dns.FallbackFilterIpCidr)
                    {
                        sb.AppendLine($"      - {cidr}");
                    }
                }
            }
            sb.AppendLine();
        }

        sb.AppendLine($"geodata-mode: {settings.GeoDataMode.ToString().ToLowerInvariant()}");
        sb.AppendLine($"geo-auto-update: {settings.GeoAutoUpdate.ToString().ToLowerInvariant()}");
        sb.AppendLine($"geo-update-interval: {Math.Max(settings.GeoUpdateIntervalHours, 1)}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(settings.GeoxUrls.GeoIp)
            || !string.IsNullOrWhiteSpace(settings.GeoxUrls.GeoSite)
            || !string.IsNullOrWhiteSpace(settings.GeoxUrls.Mmdb)
            || !string.IsNullOrWhiteSpace(settings.GeoxUrls.Asn))
        {
            sb.AppendLine("geox-url:");
            if (!string.IsNullOrWhiteSpace(settings.GeoxUrls.GeoIp))
            {
                sb.AppendLine($"  geoip: {EscapeYamlString(settings.GeoxUrls.GeoIp)}");
            }
            if (!string.IsNullOrWhiteSpace(settings.GeoxUrls.GeoSite))
            {
                sb.AppendLine($"  geosite: {EscapeYamlString(settings.GeoxUrls.GeoSite)}");
            }
            if (!string.IsNullOrWhiteSpace(settings.GeoxUrls.Mmdb))
            {
                sb.AppendLine($"  mmdb: {EscapeYamlString(settings.GeoxUrls.Mmdb)}");
            }
            if (!string.IsNullOrWhiteSpace(settings.GeoxUrls.Asn))
            {
                sb.AppendLine($"  asn: {EscapeYamlString(settings.GeoxUrls.Asn)}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("proxy-providers:");
        if (enabledProviders.Count == 0)
        {
            sb.AppendLine("  default:");
            sb.AppendLine("    type: file");
            sb.AppendLine("    path: ./proxy_providers/default.yaml");
            sb.AppendLine("    interval: 3600");
            sb.AppendLine("    health-check:");
            sb.AppendLine("      enable: true");
            sb.AppendLine("      url: https://www.gstatic.com/generate_204");
            sb.AppendLine("      interval: 300");
        }
        else
        {
            foreach (var provider in enabledProviders)
            {
                sb.AppendLine($"  {provider.ProviderKey}:");
                if (localProviders?.Contains(provider.ProviderKey) == true)
                {
                    sb.AppendLine("    type: file");
                }
                else
                {
                    sb.AppendLine("    type: http");
                    sb.AppendLine($"    url: {EscapeYamlString(provider.Url)}");
                }

                sb.AppendLine($"    path: ./proxy_providers/{provider.ProviderKey}.yaml");
                sb.AppendLine($"    interval: {Math.Max(provider.IntervalSeconds, 300)}");
                sb.AppendLine("    health-check:");
                sb.AppendLine("      enable: true");
                sb.AppendLine("      url: https://www.gstatic.com/generate_204");
                sb.AppendLine("      interval: 300");
            }
        }

        sb.AppendLine();
        sb.AppendLine("proxy-groups:");
        if (enabledProviders.Count > 0)
        {
            var names = string.Join(", ", enabledProviders.Select(x => x.ProviderKey).Select(EscapeYamlString));
            sb.AppendLine("  - name: AUTO");
            sb.AppendLine("    type: url-test");
            sb.AppendLine($"    use: [{names}]");
            sb.AppendLine("    url: https://www.gstatic.com/generate_204");
            sb.AppendLine("    interval: 300");
            sb.AppendLine("  - name: PROXY");
            sb.AppendLine("    type: select");
            sb.AppendLine("    proxies:");
            sb.AppendLine("      - AUTO");
            sb.AppendLine("      - DIRECT");
            sb.AppendLine("  - name: SELECT");
            sb.AppendLine("    type: select");
            sb.AppendLine("    proxies:");
            sb.AppendLine("      - PROXY");
            sb.AppendLine("      - DIRECT");
        }
        else
        {
            sb.AppendLine("  - name: PROXY");
            sb.AppendLine("    type: select");
            sb.AppendLine("    proxies:");
            sb.AppendLine("      - DIRECT");
            sb.AppendLine("  - name: SELECT");
            sb.AppendLine("    type: select");
            sb.AppendLine("    proxies:");
            sb.AppendLine("      - PROXY");
            sb.AppendLine("      - DIRECT");
        }

        var additionalPolicyGroups = ExtractRulePolicyNames(settings.SubscriptionRules)
            .Where(x => !string.Equals(x, "DIRECT", StringComparison.OrdinalIgnoreCase))
            .Where(x => !string.Equals(x, "REJECT", StringComparison.OrdinalIgnoreCase))
            .Where(x => !string.Equals(x, "REJECT-DROP", StringComparison.OrdinalIgnoreCase))
            .Where(x => !string.Equals(x, "PASS", StringComparison.OrdinalIgnoreCase))
            .Where(x => !string.Equals(x, "PROXY", StringComparison.OrdinalIgnoreCase))
            .Where(x => !string.Equals(x, "AUTO", StringComparison.OrdinalIgnoreCase))
            .Where(x => !string.Equals(x, "SELECT", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var policyName in additionalPolicyGroups)
        {
            sb.AppendLine($"  - name: {EscapeYamlString(policyName)}");
            sb.AppendLine("    type: select");
            sb.AppendLine("    proxies:");
            sb.AppendLine("      - PROXY");
            sb.AppendLine("      - DIRECT");
        }

        sb.AppendLine();
        sb.AppendLine("rules:");
        var subscriptionRules = settings.SubscriptionRules
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Where(x => !x.StartsWith('#'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var line in subscriptionRules)
        {
            sb.AppendLine($"  - {line}");
        }

        if (!subscriptionRules.Any(x => x.StartsWith("MATCH", StringComparison.OrdinalIgnoreCase)
            || x.StartsWith("FINAL", StringComparison.OrdinalIgnoreCase)))
        {
            sb.AppendLine("  - MATCH,PROXY");
        }

        return sb.ToString();
    }

    public static string NormalizeName(string raw)
    {
        var normalized = new string(raw.Trim().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "provider" : normalized;
    }

    private static string EscapeYamlString(string value)
    {
        return $"'{value.Replace("'", "''")}'";
    }

    private static string NormalizeTunStack(string value)
    {
        return value switch
        {
            "system" => "system",
            "gvisor" => "gvisor",
            _ => "mixed"
        };
    }

    private static IEnumerable<string> ExtractRulePolicyNames(IEnumerable<string> rules)
    {
        foreach (var line in rules)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                trimmed = trimmed[2..].Trim();
            }

            var parts = trimmed.Split(',', StringSplitOptions.None)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();

            if (parts.Length < 2)
            {
                continue;
            }

            var keyword = parts[0].ToUpperInvariant();
            if (keyword is "MATCH" or "FINAL")
            {
                yield return parts[1];
                continue;
            }

            if (parts.Length >= 3)
            {
                yield return parts[2];
                continue;
            }

            yield return parts[1];
        }
    }
}
