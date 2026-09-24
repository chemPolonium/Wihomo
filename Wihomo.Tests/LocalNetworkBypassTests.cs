using System.IO;
using Wihomo.Services;
using YamlDotNet.RepresentationModel;

namespace Wihomo.Tests;

/// <summary>
/// 「本地与局域网绕过代理」的行为约束：直连规则必须排在订阅规则之前，且 DNS 侧要同步放行，
/// 否则 fake-ip 会让内网连接拿到 198.18.x.x 而匹配不上 IP-CIDR 规则。
/// </summary>
public class LocalNetworkBypassTests
{
    [Fact]
    public void Build_BypassOff_EmitsNeitherRulesNorNameserverPolicy()
    {
        var config = new MihomoConfigBuilder().Build(Fixtures.DefaultsNoSubscription());

        Assert.DoesNotContain("IP-CIDR,192.168.0.0/16", config);
        Assert.DoesNotContain("nameserver-policy", config);
    }

    [Fact]
    public void Build_BypassOn_PutsLocalRulesBeforeSubscriptionRules()
    {
        var rules = Rules(new MihomoConfigBuilder().Build(Fixtures.LocalBypass()));

        Assert.Equal("DOMAIN-SUFFIX,lan,DIRECT", rules[0]);
        Assert.True(rules.IndexOf("IP-CIDR,192.168.0.0/16,DIRECT") < rules.IndexOf("GEOSITE,cn,PROXY"));
        Assert.Equal("MATCH,PROXY", rules[^1]);
    }

    [Fact]
    public void Build_BypassOn_DoesNotRepeatRuleFromSubscription()
    {
        var rules = Rules(new MihomoConfigBuilder().Build(Fixtures.LocalBypass()));

        Assert.Equal(1, rules.Count(x => x == "DOMAIN-SUFFIX,local,DIRECT"));
    }

    [Fact]
    public void Build_BypassOn_LeavesFakeIpRangeProxied()
    {
        var config = new MihomoConfigBuilder().Build(Fixtures.LocalBypass());

        Assert.Contains("fake-ip-range: 198.18.0.1/16", config);
        Assert.DoesNotContain("198.18", string.Join('\n', Rules(config)));
    }

    [Fact]
    public void Build_BypassOn_ResolvesLocalNamesWithSystemDns()
    {
        var config = new MihomoConfigBuilder().Build(Fixtures.LocalBypass());

        Assert.Contains("\"*.lan\": \"system:\"", config);
        Assert.Contains("    - \"*.home.arpa\"", config);
    }

    [Fact]
    public void Compose_BypassOn_PrependsRulesAndKeepsSubscriptionTail()
    {
        var rules = Rules(new SubscriptionConfigComposer()
            .Compose(Fixtures.SubscriptionYaml, Fixtures.ComposeLocalBypass()));

        Assert.Equal("DOMAIN-SUFFIX,lan,DIRECT", rules[0]);
        Assert.Equal("MATCH,节点选择", rules[^1]);
        Assert.Equal(1, rules.Count(x => x == "DOMAIN-SUFFIX,local,DIRECT"));
    }

    [Fact]
    public void Compose_BypassOn_KeepsNameserverPolicyWrittenBySubscription()
    {
        var config = new SubscriptionConfigComposer()
            .Compose(Fixtures.SubscriptionYamlWithNameserverPolicy, Fixtures.ComposeLocalBypass());

        Assert.Equal("192.168.1.1", NameserverPolicy(config)["*.lan"]);
        Assert.Equal("system:", NameserverPolicy(config)["*.local"]);
    }

    [Fact]
    public void Compose_BypassOff_LeavesSubscriptionRulesAlone()
    {
        var rules = Rules(new SubscriptionConfigComposer()
            .Compose(Fixtures.SubscriptionYaml, Fixtures.ComposeBase()));

        Assert.Equal(["DOMAIN-SUFFIX,local,DIRECT", "MATCH,节点选择"], rules);
    }

    private static List<string> Rules(string config)
    {
        var root = Root(config);
        var node = Children(root, "rules");
        return ((YamlSequenceNode)node).Children.OfType<YamlScalarNode>().Select(x => x.Value!).ToList();
    }

    private static Dictionary<string, string> NameserverPolicy(string config)
    {
        var dns = (YamlMappingNode)Children(Root(config), "dns");
        var policy = (YamlMappingNode)Children(dns, "nameserver-policy");
        return policy.Children.ToDictionary(
            x => ((YamlScalarNode)x.Key).Value!,
            x => ((YamlScalarNode)x.Value).Value!);
    }

    private static YamlMappingNode Root(string config)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(config);
        stream.Load(reader);
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static YamlNode Children(YamlMappingNode node, string key)
    {
        Assert.True(node.Children.TryGetValue(new YamlScalarNode(key), out var value), $"配置缺少 {key}");
        return value;
    }
}
