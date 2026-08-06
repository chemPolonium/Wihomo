namespace Wihomo.Models;

public sealed class DnsSettings
{
    public bool Enable { get; set; } = true;
    public string Listen { get; set; } = "0.0.0.0:53";
    public bool Ipv6 { get; set; } = true;
    public string EnhancedMode { get; set; } = "fake-ip";
    public string FakeIpRange { get; set; } = "198.18.0.1/16";
    public List<string> FakeIpFilter { get; set; } =
    [
        "*.lan",
        "*.local",
        "*.localhost",
        "dns.msftncsi.com",
        "www.msftncsi.com",
        "www.msftconnecttest.com"
    ];
    public List<string> DefaultNameserver { get; set; } =
    [
        "223.5.5.5",
        "119.29.29.29"
    ];
    public List<string> Nameserver { get; set; } =
    [
        "https://dns.alidns.com/dns-query",
        "https://doh.pub/dns-query"
    ];
    public List<string> Fallback { get; set; } =
    [
        "https://dns.cloudflare.com/dns-query",
        "https://dns.google/dns-query"
    ];
    public bool FallbackFilterGeoIp { get; set; } = true;
    public string FallbackFilterGeoIpCode { get; set; } = "CN";
    public List<string> FallbackFilterIpCidr { get; set; } =
    [
        "240.0.0.0/4"
    ];
}
