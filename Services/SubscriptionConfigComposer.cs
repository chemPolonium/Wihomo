using System.IO;
using System.Text;
using Wihomo.Models;
using YamlDotNet.RepresentationModel;

namespace Wihomo.Services;

public sealed class SubscriptionConfigComposer
{
    public IReadOnlyList<ProxyGroupInfo> GetProxyGroups(string subscriptionContent)
    {
        var root = LoadRoot(subscriptionContent);
        if (!root.Children.TryGetValue(new YamlScalarNode("proxy-groups"), out var groupsNode)
            || groupsNode is not YamlSequenceNode groups)
        {
            return [];
        }

        var result = new List<ProxyGroupInfo>();
        foreach (var groupNode in groups.Children.OfType<YamlMappingNode>())
        {
            var name = GetScalar(groupNode, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var options = GetStringSequence(groupNode, "proxies");
            result.Add(new ProxyGroupInfo
            {
                Name = name,
                Type = GetScalar(groupNode, "type"),
                Current = options.FirstOrDefault() ?? string.Empty,
                Options = options
            });
        }

        return result;
    }

    public IReadOnlyList<string> GetProxyGroupOrder(string subscriptionContent)
    {
        return GetProxyGroups(subscriptionContent).Select(x => x.Name).ToList();
    }

    public string Compose(string subscriptionContent, AppSettings settings)
    {
        var root = LoadRoot(subscriptionContent);
        ApplyYamlOverride(root, settings.RuleOverrides);
        var stream = new YamlStream(new YamlDocument(root));

        SetScalar(root, "mixed-port", Math.Max(settings.Core.MixedPort, 1).ToString());
        SetScalar(root, "socks-port", Math.Max(settings.Core.SocksPort, 1).ToString());
        SetScalar(root, "port", Math.Max(settings.Core.HttpPort, 1).ToString());
        SetScalar(root, "external-controller", $"{settings.Core.ExternalControllerHost}:{settings.Core.ExternalControllerPort}");
        SetScalar(root, "secret", settings.Core.Secret ?? string.Empty);

        var profile = GetOrCreateMap(root, "profile");
        SetScalar(profile, "store-selected", "true");
        SetScalar(profile, "store-fake-ip", "true");

        var tun = GetOrCreateMap(root, "tun");
        SetScalar(tun, "enable", settings.Core.EnableTun ? "true" : "false");
        SetScalar(tun, "stack", NormalizeTunStack(settings.Core.TunStack));
        SetScalar(tun, "auto-route", "true");
        SetScalar(tun, "auto-detect-interface", "true");
        SetScalar(tun, "strict-route", "true");

        SetScalar(root, "geodata-mode", settings.GeoDataMode ? "true" : "false");
        SetScalar(root, "geo-auto-update", settings.GeoAutoUpdate ? "true" : "false");
        SetScalar(root, "geo-update-interval", Math.Max(settings.GeoUpdateIntervalHours, 1).ToString());

        if (HasAnyGeoxUrl(settings.GeoxUrls))
        {
            var geox = GetOrCreateMap(root, "geox-url");
            if (!string.IsNullOrWhiteSpace(settings.GeoxUrls.GeoIp))
            {
                SetScalar(geox, "geoip", settings.GeoxUrls.GeoIp);
            }
            if (!string.IsNullOrWhiteSpace(settings.GeoxUrls.GeoSite))
            {
                SetScalar(geox, "geosite", settings.GeoxUrls.GeoSite);
            }
            if (!string.IsNullOrWhiteSpace(settings.GeoxUrls.Mmdb))
            {
                SetScalar(geox, "mmdb", settings.GeoxUrls.Mmdb);
            }
            if (!string.IsNullOrWhiteSpace(settings.GeoxUrls.Asn))
            {
                SetScalar(geox, "asn", settings.GeoxUrls.Asn);
            }
        }

        using var writer = new StringWriter(new StringBuilder());
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    private static string DecodeSubscriptionText(string content)
    {
        var normalized = content.Trim();
        if (!LooksLikeBase64(normalized))
        {
            return content;
        }

        try
        {
            var cleaned = new string(normalized.Where(c => !char.IsWhiteSpace(c)).ToArray());
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
                return content;
            }

            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cleaned));
            return decoded.Contains('\0') ? content : decoded;
        }
        catch
        {
            return content;
        }
    }

    private static YamlMappingNode LoadRoot(string subscriptionContent)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(DecodeSubscriptionText(subscriptionContent));
        stream.Load(reader);

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidOperationException("订阅内容不是有效的 YAML 配置。");
        }

        return root;
    }

    private static void ApplyYamlOverride(YamlMappingNode target, string overrideText)
    {
        if (string.IsNullOrWhiteSpace(overrideText))
        {
            return;
        }

        var stream = new YamlStream();
        using var reader = new StringReader(overrideText);
        stream.Load(reader);

        if (stream.Documents.Count == 0)
        {
            return;
        }

        if (stream.Documents[0].RootNode is not YamlMappingNode overrideRoot)
        {
            throw new InvalidOperationException("YAML 覆写必须是一个顶级映射对象。");
        }

        DeepMerge(target, overrideRoot);
    }

    private static void DeepMerge(YamlMappingNode target, YamlMappingNode overlay)
    {
        foreach (var pair in overlay.Children)
        {
            if (pair.Key is not YamlScalarNode { Value: { } rawKey })
            {
                throw new InvalidOperationException("YAML 覆写仅支持文本键名。");
            }

            var operation = ParseOverrideKey(rawKey);
            var existingKeyNode = FindKeyNode(target, operation.Key);

            if (operation.SequenceMode != SequenceMergeMode.Replace
                && existingKeyNode is not null
                && target.Children.TryGetValue(existingKeyNode, out var existingSequenceNode)
                && existingSequenceNode is YamlSequenceNode existingSequence
                && pair.Value is YamlSequenceNode overlaySequence)
            {
                target.Children.Remove(existingKeyNode);
                target.Children[new YamlScalarNode(operation.Key)] = MergeSequences(existingSequence, overlaySequence, operation.SequenceMode);
                continue;
            }

            if (!operation.ForceReplace
                && existingKeyNode is not null
                && target.Children.TryGetValue(existingKeyNode, out var existingNode)
                && existingNode is YamlMappingNode existingMap
                && pair.Value is YamlMappingNode overlayMap)
            {
                DeepMerge(existingMap, overlayMap);
                continue;
            }

            // Remove existing key first to avoid duplicate keys
            if (existingKeyNode is not null)
            {
                target.Children.Remove(existingKeyNode);
            }

            target.Children[new YamlScalarNode(operation.Key)] = pair.Value;
        }
    }

    private static YamlSequenceNode MergeSequences(
        YamlSequenceNode existing,
        YamlSequenceNode overlay,
        SequenceMergeMode mode)
    {
        var merged = new YamlSequenceNode();
        var first = mode == SequenceMergeMode.Prepend ? overlay.Children : existing.Children;
        var second = mode == SequenceMergeMode.Prepend ? existing.Children : overlay.Children;

        foreach (var item in first)
        {
            merged.Add(item);
        }
        foreach (var item in second)
        {
            merged.Add(item);
        }

        return merged;
    }

    private static OverrideKeyOperation ParseOverrideKey(string rawKey)
    {
        if (rawKey.Length >= 2 && rawKey[0] == '<' && rawKey[^1] == '>')
        {
            return new OverrideKeyOperation(rawKey[1..^1], false, SequenceMergeMode.Replace);
        }

        var key = rawKey;
        var forceReplace = key.EndsWith('!');
        if (forceReplace)
        {
            key = key[..^1];
        }

        if (key.StartsWith('+'))
        {
            return new OverrideKeyOperation(key[1..], forceReplace, SequenceMergeMode.Prepend);
        }

        if (key.EndsWith('+'))
        {
            return new OverrideKeyOperation(key[..^1], forceReplace, SequenceMergeMode.Append);
        }

        return new OverrideKeyOperation(key, forceReplace, SequenceMergeMode.Replace);
    }

    private static string GetScalar(YamlMappingNode node, string key)
    {
        return node.Children.TryGetValue(new YamlScalarNode(key), out var value)
            && value is YamlScalarNode scalar
            ? scalar.Value ?? string.Empty
            : string.Empty;
    }

    private static List<string> GetStringSequence(YamlMappingNode node, string key)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out var value)
            || value is not YamlSequenceNode sequence)
        {
            return [];
        }

        return sequence.Children
            .OfType<YamlScalarNode>()
            .Select(x => x.Value)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToList();
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

    private static bool HasAnyGeoxUrl(GeoxUrlSettings geox)
    {
        return !string.IsNullOrWhiteSpace(geox.GeoIp)
            || !string.IsNullOrWhiteSpace(geox.GeoSite)
            || !string.IsNullOrWhiteSpace(geox.Mmdb)
            || !string.IsNullOrWhiteSpace(geox.Asn);
    }

    private static YamlMappingNode GetOrCreateMap(YamlMappingNode parent, string key)
    {
        var existingKey = FindKeyNode(parent, key);
        if (existingKey is not null
            && parent.Children.TryGetValue(existingKey, out var child)
            && child is YamlMappingNode existingMap)
        {
            return existingMap;
        }

        var keyNode = new YamlScalarNode(key);
        var created = new YamlMappingNode();
        parent.Children[keyNode] = created;
        return created;
    }

    private static void SetScalar(YamlMappingNode node, string key, string value)
    {
        // YamlMappingNode uses reference equality for keys.
        // Must find and remove existing key node first to avoid duplicate keys.
        var existingKey = FindKeyNode(node, key);
        if (existingKey is not null)
        {
            node.Children.Remove(existingKey);
        }

        node.Children[new YamlScalarNode(key)] = new YamlScalarNode(value);
    }

    private static YamlScalarNode? FindKeyNode(YamlMappingNode node, string key)
    {
        foreach (var child in node.Children)
        {
            if (child.Key is YamlScalarNode scalar
                && string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                return scalar;
            }
        }

        return null;
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

    private sealed record OverrideKeyOperation(string Key, bool ForceReplace, SequenceMergeMode SequenceMode);

    private enum SequenceMergeMode
    {
        Replace,
        Prepend,
        Append
    }
}
