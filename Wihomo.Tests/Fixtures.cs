using Wihomo.Models;

namespace Wihomo.Tests;

/// <summary>
/// 金标准测试的输入。刻意覆盖分支而非追求真实配置：
/// 端口钳位、名称归一化、TUN 栈回退、GEO 间隔钳位、规则策略名去重、YAML 转义边界。
/// </summary>
internal static class Fixtures
{
    public static AppSettings DefaultsNoSubscription() => new();

    public static AppSettings TwoProvidersCustomRulesTun() => new()
    {
        Core = new CoreRuntimeSettings
        {
            ExternalControllerHost = "127.0.0.1",
            ExternalControllerPort = 9090,
            Secret = "wihomo",
            MixedPort = 1080,
            SocksPort = 7891,
            HttpPort = 7892,
            EnableSystemProxy = true,
            EnableTun = true,
            TunStack = "System",
        },
        Subscriptions =
        [
            new SubscriptionItem
            {
                Name = "My 机场 A",
                Url = "https://example.com/sub?a=1&b=2",
                IntervalSeconds = 60,
                Enabled = true,
            },
            new SubscriptionItem
            {
                Name = "B·Node",
                Url = "https://b.example/",
                IntervalSeconds = 7200,
                Enabled = true,
            },
        ],
        ActiveSubscriptionName = "My 机场 A",
        SubscriptionRules =
        [
            "DOMAIN-SUFFIX,example.com,我的落地",
            "DOMAIN,1.1.1.1,DIRECT",
            "GEOIP,CN,REJECT",
            "SRC-IP-CIDR,10.0.0.0/8,PASS",
            "DOMAIN,proxy.com,PROXY",
            "DOMAIN,auto.com,AUTO",
            "DOMAIN,select.com,SELECT",
            "GEOSITE,cn,我的落地",
            "# 注释行应被忽略",
            "   ",
            "MATCH,PROXY",
        ],
        GeoDataMode = true,
        GeoAutoUpdate = false,
        GeoUpdateIntervalHours = 0,
    };

    public static HashSet<string> LocalProviderKeys() => ["My_机场_A"];

    public static AppSettings DnsOffBlankGeoxPortsClamped() => new()
    {
        Core = new CoreRuntimeSettings
        {
            ExternalControllerHost = "0.0.0.0",
            ExternalControllerPort = -5,
            Secret = "s e c r e t",
            MixedPort = 0,
            SocksPort = -1,
            HttpPort = 1,
            EnableSystemProxy = false,
            EnableTun = false,
            TunStack = "bogus-stack",
        },
        Dns = new DnsSettings { Enable = false },
        GeoxUrls = new GeoxUrlSettings { GeoIp = "", GeoSite = "   ", Mmdb = "", Asn = "" },
        Subscriptions =
        [
            new SubscriptionItem { Name = "停用", Url = "https://x/", IntervalSeconds = 300, Enabled = false },
            new SubscriptionItem { Name = "", Url = "https://x/", IntervalSeconds = 300, Enabled = true },
            new SubscriptionItem { Name = "无URL", Url = "", IntervalSeconds = 300, Enabled = true },
        ],
        SubscriptionRules = [],
        StatsRefreshSeconds = 5,
    };

    public static AppSettings EscapingEdgeCases() => new()
    {
        Core = new CoreRuntimeSettings
        {
            ExternalControllerHost = "127.0.0.1",
            ExternalControllerPort = 9090,
            Secret = "he said \"hi\": C:\\path",
            MixedPort = 7890,
            SocksPort = 7891,
            HttpPort = 7892,
            TunStack = "gvisor",
        },
        Subscriptions =
        [
            new SubscriptionItem
            {
                Name = "quote\"colon:",
                Url = "https://u.example/x?token=a:b&c=\"d\"",
                IntervalSeconds = 3600,
                Enabled = true,
            },
        ],
        SubscriptionRules = ["DOMAIN-SUFFIX,a.com,含\"引号\"的策略"],
        Dns = new DnsSettings
        {
            Enable = true,
            Listen = "0.0.0.0:53",
            EnhancedMode = "redir-host",
            DefaultNameserver = [],
            Nameserver = ["223.5.5.5"],
            Fallback = [],
            FallbackFilterGeoIp = false,
            FallbackFilterIpCidr = [],
            FakeIpFilter = ["*.lan"],
        },
    };

    public const string SubscriptionYaml = """
proxies:
  - name: HK-01
    type: ss
    server: 1.2.3.4
    port: 8388
    cipher: aes-256-gcm
    password: "p@ss"
  - name: JP-02
    type: trojan
    server: 5.6.7.8
    port: 443
    password: trojanpass
    sni: example.com

proxy-groups:
  - name: 节点选择
    type: select
    proxies:
      - HK-01
      - JP-02
      - DIRECT
  - name: 自动选择
    type: url-test
    url: http://www.gstatic.com/generate_204
    interval: 300
    proxies:
      - HK-01
      - JP-02

dns:
  enable: true
  listen: 127.0.0.1:53
  nameserver:
    - 114.114.114.114

rules:
  - DOMAIN-SUFFIX,local,DIRECT
  - MATCH,节点选择

""";

    public static AppSettings ComposeBase() => new()
    {
        Core = new CoreRuntimeSettings { TunStack = "system", EnableTun = true },
    };

    public static AppSettings ComposeWithOverrides() => new()
    {
        Core = new CoreRuntimeSettings { TunStack = "system", EnableTun = true, MixedPort = 7890 },
        RuleOverrides = """
<mixed-port>: 1
+proxies:
  - name: US-03
    type: ss
    server: 9.9.9.9
    port: 8388
    cipher: aes-256-gcm
    password: added-front
proxies+:
  - name: SG-04
    type: ss
    server: 8.8.8.8
    port: 8388
    cipher: aes-256-gcm
    password: added-back
rules+:
  - DOMAIN-SUFFIX,override.example,节点选择
dns:
  ipv6: false
  fake-ip-range: 198.20.0.1/16
proxy-groups!:
  - name: 覆写组
    type: select
    proxies:
      - DIRECT

""",
    };
}
