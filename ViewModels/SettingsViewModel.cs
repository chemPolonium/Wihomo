using CommunityToolkit.Mvvm.ComponentModel;
using Wihomo.Models;
using Wihomo.Services;

namespace Wihomo.ViewModels;

/// <summary>
/// 静态设置的视图模型，取代原来的 BindSettingsToUi / CollectSettingsFromUi 这一对控件搬运。
/// 内核设置、DNS、外部资源与 YAML 覆写共用一次保存，与现状一致。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public static readonly IReadOnlyList<LabeledOption> TunStackOptions =
    [
        new("mixed", "mixed"),
        new("system", "system"),
        new("gvisor", "gvisor"),
    ];

    public static readonly IReadOnlyList<LabeledOption> DnsEnhancedModeOptions =
    [
        new("fake-ip", "fake-ip"),
        new("redir-host", "redir-host"),
        new("normal", "normal"),
    ];

    public static readonly IReadOnlyList<LabeledOption> GeoDataModeOptions =
    [
        new("MMDB（默认）", "mmdb"),
        new("DAT", "dat"),
    ];

    [ObservableProperty] private string _corePath = string.Empty;
    [ObservableProperty] private string _coreWorkDir = string.Empty;
    [ObservableProperty] private string _controllerHost = string.Empty;
    [ObservableProperty] private string _controllerPortText = string.Empty;
    [ObservableProperty] private string _mixedPortText = string.Empty;
    [ObservableProperty] private string _socksPortText = string.Empty;
    [ObservableProperty] private string _httpPortText = string.Empty;
    [ObservableProperty] private string _secret = string.Empty;
    [ObservableProperty] private string _statsRefreshText = string.Empty;
    [ObservableProperty] private bool _enableSystemProxy;
    [ObservableProperty] private bool _enableTun;
    [ObservableProperty] private bool _startCoreOnProgramStart;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private LabeledOption _selectedTunStack;
    [ObservableProperty] private bool _enableDns;
    [ObservableProperty] private string _dnsListen = string.Empty;
    [ObservableProperty] private LabeledOption _selectedDnsEnhancedMode;
    [ObservableProperty] private string _defaultNameserverText = string.Empty;
    [ObservableProperty] private string _nameserverText = string.Empty;
    [ObservableProperty] private string _fallbackText = string.Empty;
    [ObservableProperty] private string _geoIpUrl = string.Empty;
    [ObservableProperty] private string _geoSiteUrl = string.Empty;
    [ObservableProperty] private string _mmdbUrl = string.Empty;
    [ObservableProperty] private string _asnUrl = string.Empty;
    [ObservableProperty] private LabeledOption _selectedGeoDataMode;
    [ObservableProperty] private bool _geoAutoUpdate;
    [ObservableProperty] private string _geoUpdateIntervalText = string.Empty;
    [ObservableProperty] private string _ruleOverrides = string.Empty;
    [ObservableProperty] private bool _isDirty;

    public SettingsViewModel()
    {
        _selectedTunStack = TunStackOptions[0];
        _selectedDnsEnhancedMode = DnsEnhancedModeOptions[0];
        _selectedGeoDataMode = GeoDataModeOptions[0];

        // 任何非 IsDirty 自身的属性变化都算未保存改动，避免逐属性手写标脏。
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(IsDirty))
            {
                IsDirty = true;
            }
        };
    }

    public void LoadFrom(AppSettings settings)
    {
        var wasDirty = IsDirty;
        IsDirty = false;

        CorePath = settings.Core.CoreExecutablePath;
        CoreWorkDir = settings.Core.WorkingDirectory;
        ControllerHost = settings.Core.ExternalControllerHost;
        ControllerPortText = settings.Core.ExternalControllerPort.ToString();
        MixedPortText = settings.Core.MixedPort.ToString();
        SocksPortText = settings.Core.SocksPort.ToString();
        HttpPortText = settings.Core.HttpPort.ToString();
        Secret = settings.Core.Secret;
        StatsRefreshText = settings.StatsRefreshSeconds.ToString();
        EnableSystemProxy = settings.Core.EnableSystemProxy;
        EnableTun = settings.Core.EnableTun;
        StartCoreOnProgramStart = settings.StartCoreOnProgramStart;
        StartWithWindows = settings.StartWithWindows;
        SelectedTunStack = FindOption(TunStackOptions, settings.Core.TunStack);

        EnableDns = settings.Dns.Enable;
        DnsListen = settings.Dns.Listen;
        SelectedDnsEnhancedMode = FindOption(DnsEnhancedModeOptions, settings.Dns.EnhancedMode);
        DefaultNameserverText = JoinLines(settings.Dns.DefaultNameserver);
        NameserverText = JoinLines(settings.Dns.Nameserver);
        FallbackText = JoinLines(settings.Dns.Fallback);

        GeoIpUrl = settings.GeoxUrls.GeoIp;
        GeoSiteUrl = settings.GeoxUrls.GeoSite;
        MmdbUrl = settings.GeoxUrls.Mmdb;
        AsnUrl = settings.GeoxUrls.Asn;
        SelectedGeoDataMode = GeoDataModeOptions[settings.GeoDataMode ? 1 : 0];
        GeoAutoUpdate = settings.GeoAutoUpdate;
        GeoUpdateIntervalText = settings.GeoUpdateIntervalHours.ToString();

        RuleOverrides = settings.RuleOverrides;

        IsDirty = wasDirty;
    }

    /// <summary>
    /// 校验并写回设置对象。未在本视图暴露的字段（fake-ip-filter、fallback-filter 等）原样保留。
    /// </summary>
    public void ApplyTo(AppSettings settings)
    {
        // 先全部解析再写入：任一校验失败时不得留下半新半旧的设置。
        if (string.IsNullOrWhiteSpace(CorePath))
        {
            throw new InvalidOperationException("内核路径不能为空。");
        }

        if (string.IsNullOrWhiteSpace(CoreWorkDir))
        {
            throw new InvalidOperationException("工作目录不能为空。");
        }

        if (string.IsNullOrWhiteSpace(ControllerHost))
        {
            throw new InvalidOperationException("External Controller Host 不能为空。");
        }

        var controllerPort = SettingsParsing.ParsePositiveInt(ControllerPortText, "External Controller Port");
        var mixedPort = SettingsParsing.ParsePositiveInt(MixedPortText, "Mixed Port");
        var socksPort = SettingsParsing.ParsePositiveInt(SocksPortText, "SOCKS Port");
        var httpPort = SettingsParsing.ParsePositiveInt(HttpPortText, "HTTP Port");
        var statsRefresh = SettingsParsing.ParsePositiveInt(StatsRefreshText, "统计刷新间隔(秒)");
        var geoUpdateInterval = SettingsParsing.ParsePositiveInt(GeoUpdateIntervalText, "GEO 更新间隔（小时）");
        var geoIp = SettingsParsing.ParseAbsoluteUrl(GeoIpUrl, "GeoIP 数据库 URL");
        var geoSite = SettingsParsing.ParseAbsoluteUrl(GeoSiteUrl, "GeoSite 数据库 URL");
        var mmdb = SettingsParsing.ParseAbsoluteUrl(MmdbUrl, "MMDB 数据库 URL");
        var asn = SettingsParsing.ParseAbsoluteUrl(AsnUrl, "ASN 数据库 URL");

        settings.Core.CoreExecutablePath = CorePath.Trim();
        settings.Core.WorkingDirectory = CoreWorkDir.Trim();
        settings.Core.ExternalControllerHost = ControllerHost.Trim();
        settings.Core.ExternalControllerPort = controllerPort;
        settings.Core.MixedPort = mixedPort;
        settings.Core.SocksPort = socksPort;
        settings.Core.HttpPort = httpPort;
        settings.Core.Secret = Secret;
        settings.Core.EnableSystemProxy = EnableSystemProxy;
        settings.Core.EnableTun = EnableTun;
        settings.Core.TunStack = SelectedTunStack.Value;

        settings.StatsRefreshSeconds = statsRefresh;
        settings.StartCoreOnProgramStart = StartCoreOnProgramStart;
        settings.StartWithWindows = StartWithWindows;
        settings.GeoAutoUpdate = GeoAutoUpdate;
        settings.GeoUpdateIntervalHours = geoUpdateInterval;
        settings.GeoDataMode = SelectedGeoDataMode.Value == "dat";
        settings.RuleOverrides = RuleOverrides;

        settings.Dns.Enable = EnableDns;
        settings.Dns.Listen = DnsListen.Trim();
        settings.Dns.EnhancedMode = SelectedDnsEnhancedMode.Value;
        settings.Dns.DefaultNameserver = SettingsParsing.ParseMultilineText(DefaultNameserverText);
        settings.Dns.Nameserver = SettingsParsing.ParseMultilineText(NameserverText);
        settings.Dns.Fallback = SettingsParsing.ParseMultilineText(FallbackText);

        settings.GeoxUrls.GeoIp = geoIp;
        settings.GeoxUrls.GeoSite = geoSite;
        settings.GeoxUrls.Mmdb = mmdb;
        settings.GeoxUrls.Asn = asn;

        IsDirty = false;
    }

    private static LabeledOption FindOption(IReadOnlyList<LabeledOption> options, string value)
    {
        return options.FirstOrDefault(x => string.Equals(x.Value, value, StringComparison.Ordinal)) ?? options[0];
    }

    private static string JoinLines(IEnumerable<string> lines) => string.Join(Environment.NewLine, lines);
}
