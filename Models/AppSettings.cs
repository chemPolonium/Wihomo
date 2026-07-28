namespace Wihomo.Models;

public sealed class AppSettings
{
    public CoreRuntimeSettings Core { get; set; } = new();
    public List<SubscriptionItem> Subscriptions { get; set; } = [];
    public string ActiveSubscriptionName { get; set; } = string.Empty;
    public List<string> SubscriptionRules { get; set; } = [];
    public GeoxUrlSettings GeoxUrls { get; set; } = new();
    public string RuleOverrides { get; set; } = string.Empty;
    public int StatsRefreshSeconds { get; set; } = 2;
    public bool StartCoreOnProgramStart { get; set; }
    public bool StartWithWindows { get; set; }
    public bool GeoDataMode { get; set; }
    public bool GeoAutoUpdate { get; set; } = true;
    public int GeoUpdateIntervalHours { get; set; } = 24;
}
