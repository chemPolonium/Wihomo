namespace Wihomo.Models;

public sealed class GeoxUrlSettings
{
    public const string DefaultGeoIpUrl = "https://github.com/MetaCubeX/meta-rules-dat/releases/download/latest/geoip-lite.dat";
    public const string DefaultGeoSiteUrl = "https://github.com/MetaCubeX/meta-rules-dat/releases/download/latest/geosite.dat";
    public const string DefaultMmdbUrl = "https://github.com/MetaCubeX/meta-rules-dat/releases/download/latest/geoip.metadb";
    public const string DefaultAsnUrl = "https://github.com/MetaCubeX/meta-rules-dat/releases/download/latest/GeoLite2-ASN.mmdb";

    public string GeoIp { get; set; } = DefaultGeoIpUrl;
    public string GeoSite { get; set; } = DefaultGeoSiteUrl;
    public string Mmdb { get; set; } = DefaultMmdbUrl;
    public string Asn { get; set; } = DefaultAsnUrl;
}
