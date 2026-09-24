namespace Wihomo.Services;

/// <summary>「本地与局域网绕过代理」开关对应的 mihomo 配置片段，两条配置生成路径共用。</summary>
internal static class LocalNetworkBypass
{
    /// <summary>直连规则，须排在订阅规则之前；fake-ip 段 198.18.0.0/16 刻意不在其中。</summary>
    public static IReadOnlyList<string> Rules { get; } =
    [
        "DOMAIN-SUFFIX,lan,DIRECT",
        "DOMAIN-SUFFIX,local,DIRECT",
        "DOMAIN-SUFFIX,localhost,DIRECT",
        "DOMAIN-SUFFIX,home.arpa,DIRECT",
        "DOMAIN-SUFFIX,internal,DIRECT",
        "IP-CIDR,0.0.0.0/8,DIRECT",
        "IP-CIDR,10.0.0.0/8,DIRECT",
        "IP-CIDR,100.64.0.0/10,DIRECT",
        "IP-CIDR,127.0.0.0/8,DIRECT",
        "IP-CIDR,169.254.0.0/16,DIRECT",
        "IP-CIDR,172.16.0.0/12,DIRECT",
        "IP-CIDR,192.0.0.0/24,DIRECT",
        "IP-CIDR,192.168.0.0/16,DIRECT",
        "IP-CIDR,224.0.0.0/4,DIRECT",
        "IP-CIDR,240.0.0.0/4,DIRECT",
        "IP-CIDR,::1/128,DIRECT",
        "IP-CIDR,fc00::/7,DIRECT",
        "IP-CIDR,fe80::/10,DIRECT",
        "IP-CIDR,ff00::/8,DIRECT",
    ];

    /// <summary>让内网域名拿到真实地址而非 fake-ip，否则上一条的 IP-CIDR 永远匹配不上。</summary>
    public static IReadOnlyList<string> FakeIpFilter { get; } =
    [
        "*.home.arpa",
        "*.internal",
        "*.in-addr.arpa",
        "*.ip6.arpa",
        "*.msftconnecttest.com",
        "*.msftncsi.com",
    ];

    /// <summary>内网域名交给系统（路由器）解析；nameserver 全是公网 DoH，问不出局域网地址。</summary>
    public static IReadOnlyDictionary<string, string> NameserverPolicy { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["*.lan"] = "system:",
            ["*.local"] = "system:",
            ["*.localhost"] = "system:",
            ["*.home.arpa"] = "system:",
            ["*.internal"] = "system:",
            ["*.in-addr.arpa"] = "system:",
            ["*.ip6.arpa"] = "system:",
        };
}
