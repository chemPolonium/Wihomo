using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Wihomo.Models;
using Wihomo.Services;

namespace Wihomo.ViewModels;

/// <summary>
/// 设置草稿。所有设置页共用同一份编辑状态：字段变化只标记 dirty，
/// 真正落盘与热重载由 MainViewModel 统一处理。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
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

    private static readonly string[] PreferenceKeys =
    [
        nameof(StartWithWindows),
        nameof(StartCoreOnProgramStart),
        nameof(StatsRefreshText),
        nameof(ProxyTestUrl),
        nameof(ProxyTestTimeoutText),
    ];

    private readonly Dictionary<string, string> _savedState = new(StringComparer.Ordinal);
    private bool _isLoading;

    // WinUI XAML 不支持 x:Static，ComboBox 统一绑定实例属性。
    public IReadOnlyList<LabeledOption> DnsEnhancedModeOptionList => DnsEnhancedModeOptions;

    public IReadOnlyList<LabeledOption> GeoDataModeOptionList => GeoDataModeOptions;

    /// <summary>并行调试实例不接管系统启动项，设置页据此禁用开关。</summary>
    public bool CanStartWithWindows => !AppInstance.IsIsolated;

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
    [ObservableProperty] private bool _bypassLocalNetworks;
    [ObservableProperty] private bool _startCoreOnProgramStart;
    [ObservableProperty] private bool _startWithWindows;
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
    [ObservableProperty] private string _proxyTestUrl = "http://cp.cloudflare.com/generate_204";
    [ObservableProperty] private string _proxyTestTimeoutText = "5000";
    [ObservableProperty] private bool _isDirty;

    public SettingsViewModel()
    {
        _selectedDnsEnhancedMode = DnsEnhancedModeOptions[0];
        _selectedGeoDataMode = GeoDataModeOptions[0];

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IsDirty) or nameof(HasPreferenceChanges) or nameof(HasConfigChanges))
            {
                return;
            }

            RefreshDirty();
        };
    }

    public bool IsLoading => _isLoading;

    public bool HasPreferenceChanges => PreferenceKeys.Any(key =>
        _savedState.TryGetValue(key, out var saved) && !string.Equals(saved, StateValue(key), StringComparison.Ordinal));

    public bool HasConfigChanges => _savedState.Any(pair =>
        !PreferenceKeys.Contains(pair.Key, StringComparer.Ordinal)
        && !string.Equals(pair.Value, StateValue(pair.Key), StringComparison.Ordinal));

    public static bool IsPreferenceProperty(string? propertyName) =>
        propertyName is not null && PreferenceKeys.Contains(propertyName, StringComparer.Ordinal);

    public void LoadFrom(AppSettings settings)
    {
        _isLoading = true;
        try
        {
            CorePath = settings.Core.CoreExecutablePath;
            CoreWorkDir = settings.Core.WorkingDirectory;
            ControllerHost = settings.Core.ExternalControllerHost;
            ControllerPortText = settings.Core.ExternalControllerPort.ToString(CultureInfo.InvariantCulture);
            MixedPortText = settings.Core.MixedPort.ToString(CultureInfo.InvariantCulture);
            SocksPortText = settings.Core.SocksPort.ToString(CultureInfo.InvariantCulture);
            HttpPortText = settings.Core.HttpPort.ToString(CultureInfo.InvariantCulture);
            Secret = settings.Core.Secret;
            StatsRefreshText = settings.StatsRefreshSeconds.ToString(CultureInfo.InvariantCulture);
            EnableSystemProxy = settings.Core.EnableSystemProxy;
            BypassLocalNetworks = settings.Core.BypassLocalNetworks;
            StartCoreOnProgramStart = settings.StartCoreOnProgramStart;
            StartWithWindows = settings.StartWithWindows;

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
            GeoUpdateIntervalText = settings.GeoUpdateIntervalHours.ToString(CultureInfo.InvariantCulture);

            RuleOverrides = settings.RuleOverrides;
            ProxyTestUrl = settings.ProxyTest.Url;
            ProxyTestTimeoutText = settings.ProxyTest.TimeoutMilliseconds.ToString(CultureInfo.InvariantCulture);

            CaptureSavedState();
        }
        finally
        {
            _isLoading = false;
        }

        IsDirty = false;
        OnPropertyChanged(nameof(HasPreferenceChanges));
        OnPropertyChanged(nameof(HasConfigChanges));
    }

    public void MarkSaved()
    {
        CaptureSavedState();
        IsDirty = false;
        OnPropertyChanged(nameof(HasPreferenceChanges));
        OnPropertyChanged(nameof(HasConfigChanges));
    }

    /// <summary>自动保存偏好字段后，只更新这些字段的基线，避免把配置类修改也标成已保存。</summary>
    public void MarkPreferencesSaved()
    {
        foreach (var key in PreferenceKeys)
        {
            _savedState[key] = StateValue(key);
        }

        IsDirty = HasPreferenceChanges || HasConfigChanges;
        OnPropertyChanged(nameof(HasPreferenceChanges));
        OnPropertyChanged(nameof(HasConfigChanges));
    }

    public void MarkDirty()
    {
        IsDirty = true;
        OnPropertyChanged(nameof(HasPreferenceChanges));
        OnPropertyChanged(nameof(HasConfigChanges));
    }

    /// <summary>
    /// 校验并写回设置对象。只校验本次真正修改过的字段：未修改的配置即使历史上不合法，
    /// 也不应阻塞其它设置保存。未在界面暴露的字段原样保留。
    /// </summary>
    public void ApplyTo(AppSettings settings)
    {
        // 先全部解析再写入：任一校验失败时不得留下半新半旧的设置。
        var corePath = RequireIfChanged(CorePath, settings.Core.CoreExecutablePath, "内核路径不能为空。");
        var coreWorkDir = RequireIfChanged(CoreWorkDir, settings.Core.WorkingDirectory, "工作目录不能为空。");
        var controllerHost = RequireIfChanged(ControllerHost, settings.Core.ExternalControllerHost, "External Controller Host 不能为空。");
        var controllerPort = ParsePositiveIntIfChanged(ControllerPortText, settings.Core.ExternalControllerPort, "External Controller Port");
        var mixedPort = ParsePositiveIntIfChanged(MixedPortText, settings.Core.MixedPort, "Mixed Port");
        var socksPort = ParsePositiveIntIfChanged(SocksPortText, settings.Core.SocksPort, "SOCKS Port");
        var httpPort = ParsePositiveIntIfChanged(HttpPortText, settings.Core.HttpPort, "HTTP Port");
        var statsRefresh = ParsePositiveIntIfChanged(StatsRefreshText, settings.StatsRefreshSeconds, "统计刷新间隔(秒)");
        var geoUpdateInterval = ParsePositiveIntIfChanged(GeoUpdateIntervalText, settings.GeoUpdateIntervalHours, "GEO 更新间隔（小时）");
        var geoIp = ParseUrlIfChanged(GeoIpUrl, settings.GeoxUrls.GeoIp, "GeoIP 数据库 URL");
        var geoSite = ParseUrlIfChanged(GeoSiteUrl, settings.GeoxUrls.GeoSite, "GeoSite 数据库 URL");
        var mmdb = ParseUrlIfChanged(MmdbUrl, settings.GeoxUrls.Mmdb, "MMDB 数据库 URL");
        var asn = ParseUrlIfChanged(AsnUrl, settings.GeoxUrls.Asn, "ASN 数据库 URL");
        var proxyTestUrl = ParseUrlIfChanged(ProxyTestUrl, settings.ProxyTest.Url, "测速 URL");
        var proxyTestTimeout = ParsePositiveIntIfChanged(
            ProxyTestTimeoutText,
            settings.ProxyTest.TimeoutMilliseconds,
            "测速超时(ms)");

        settings.Core.CoreExecutablePath = corePath;
        settings.Core.WorkingDirectory = coreWorkDir;
        settings.Core.ExternalControllerHost = controllerHost;
        settings.Core.ExternalControllerPort = controllerPort;
        settings.Core.MixedPort = mixedPort;
        settings.Core.SocksPort = socksPort;
        settings.Core.HttpPort = httpPort;
        settings.Core.Secret = Secret;
        settings.Core.EnableSystemProxy = EnableSystemProxy;
        settings.Core.BypassLocalNetworks = BypassLocalNetworks;

        settings.StatsRefreshSeconds = statsRefresh;
        settings.StartCoreOnProgramStart = StartCoreOnProgramStart;
        settings.StartWithWindows = StartWithWindows;
        settings.GeoAutoUpdate = GeoAutoUpdate;
        settings.GeoUpdateIntervalHours = geoUpdateInterval;
        settings.GeoDataMode = SelectedGeoDataMode.Value == "dat";
        settings.RuleOverrides = RuleOverrides;
        settings.ProxyTest.Url = proxyTestUrl;
        settings.ProxyTest.TimeoutMilliseconds = proxyTestTimeout;

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
    }

    /// <summary>自动保存只写偏好字段，不把未保存的内核/DNS 编辑一并落盘。</summary>
    public void ApplyPreferencesTo(AppSettings settings)
    {
        settings.StatsRefreshSeconds = ParsePositiveIntIfChanged(
            StatsRefreshText,
            settings.StatsRefreshSeconds,
            "统计刷新间隔(秒)");
        settings.StartCoreOnProgramStart = StartCoreOnProgramStart;
        settings.StartWithWindows = StartWithWindows;
        settings.ProxyTest.Url = ParseUrlIfChanged(ProxyTestUrl, settings.ProxyTest.Url, "测速 URL");
        settings.ProxyTest.TimeoutMilliseconds = ParsePositiveIntIfChanged(
            ProxyTestTimeoutText,
            settings.ProxyTest.TimeoutMilliseconds,
            "测速超时(ms)");
    }

    private void RefreshDirty()
    {
        if (_isLoading)
        {
            return;
        }

        var dirty = HasPreferenceChanges || HasConfigChanges;
        if (IsDirty != dirty)
        {
            IsDirty = dirty;
        }

        OnPropertyChanged(nameof(HasPreferenceChanges));
        OnPropertyChanged(nameof(HasConfigChanges));
    }

    private void CaptureSavedState()
    {
        _savedState.Clear();
        foreach (var key in StateKeys)
        {
            _savedState[key] = StateValue(key);
        }
    }

    private string StateValue(string key) => key switch
    {
        nameof(CorePath) => CorePath,
        nameof(CoreWorkDir) => CoreWorkDir,
        nameof(ControllerHost) => ControllerHost,
        nameof(ControllerPortText) => ControllerPortText,
        nameof(MixedPortText) => MixedPortText,
        nameof(SocksPortText) => SocksPortText,
        nameof(HttpPortText) => HttpPortText,
        nameof(Secret) => Secret,
        nameof(StatsRefreshText) => StatsRefreshText,
        nameof(EnableSystemProxy) => BoolValue(EnableSystemProxy),
        nameof(BypassLocalNetworks) => BoolValue(BypassLocalNetworks),
        nameof(StartCoreOnProgramStart) => BoolValue(StartCoreOnProgramStart),
        nameof(StartWithWindows) => BoolValue(StartWithWindows),
        nameof(EnableDns) => BoolValue(EnableDns),
        nameof(DnsListen) => DnsListen,
        nameof(SelectedDnsEnhancedMode) => SelectedDnsEnhancedMode.Value,
        nameof(DefaultNameserverText) => DefaultNameserverText,
        nameof(NameserverText) => NameserverText,
        nameof(FallbackText) => FallbackText,
        nameof(GeoIpUrl) => GeoIpUrl,
        nameof(GeoSiteUrl) => GeoSiteUrl,
        nameof(MmdbUrl) => MmdbUrl,
        nameof(AsnUrl) => AsnUrl,
        nameof(SelectedGeoDataMode) => SelectedGeoDataMode.Value,
        nameof(GeoAutoUpdate) => BoolValue(GeoAutoUpdate),
        nameof(GeoUpdateIntervalText) => GeoUpdateIntervalText,
        nameof(RuleOverrides) => RuleOverrides,
        nameof(ProxyTestUrl) => ProxyTestUrl,
        nameof(ProxyTestTimeoutText) => ProxyTestTimeoutText,
        _ => string.Empty,
    };

    private static readonly string[] StateKeys =
    [
        nameof(CorePath), nameof(CoreWorkDir), nameof(ControllerHost), nameof(ControllerPortText),
        nameof(MixedPortText), nameof(SocksPortText), nameof(HttpPortText), nameof(Secret),
        nameof(StatsRefreshText), nameof(EnableSystemProxy), nameof(BypassLocalNetworks),
        nameof(StartCoreOnProgramStart), nameof(StartWithWindows), nameof(EnableDns), nameof(DnsListen),
        nameof(SelectedDnsEnhancedMode), nameof(DefaultNameserverText), nameof(NameserverText),
        nameof(FallbackText), nameof(GeoIpUrl), nameof(GeoSiteUrl), nameof(MmdbUrl), nameof(AsnUrl),
        nameof(SelectedGeoDataMode), nameof(GeoAutoUpdate), nameof(GeoUpdateIntervalText),
        nameof(RuleOverrides), nameof(ProxyTestUrl), nameof(ProxyTestTimeoutText),
    ];

    private static string BoolValue(bool value) => value ? "1" : "0";

    private static string RequireIfChanged(string text, string current, string error)
    {
        var value = text.Trim();
        if (string.Equals(value, current, StringComparison.Ordinal))
        {
            return current;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(error);
        }

        return value;
    }

    private static int ParsePositiveIntIfChanged(string text, int current, string fieldName)
    {
        if (string.Equals(
            text.Trim(),
            current.ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal))
        {
            return current;
        }

        return SettingsParsing.ParsePositiveInt(text, fieldName);
    }

    private static string ParseUrlIfChanged(string text, string current, string fieldName)
    {
        var value = text.Trim();
        if (string.Equals(value, current, StringComparison.Ordinal))
        {
            return current;
        }

        return SettingsParsing.ParseAbsoluteUrl(text, fieldName);
    }

    private static LabeledOption FindOption(IReadOnlyList<LabeledOption> options, string value)
    {
        return options.FirstOrDefault(x => string.Equals(x.Value, value, StringComparison.Ordinal)) ?? options[0];
    }

    private static string JoinLines(IEnumerable<string> lines) => string.Join(Environment.NewLine, lines);
}
