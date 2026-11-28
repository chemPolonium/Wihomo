using System.IO;
using System.Net.Http;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wihomo.Models;
using Wihomo.Services;
using Wihomo.Services.Realtime;
using Wihomo.ViewModels;

namespace Wihomo;

/// <summary>
/// 主窗口的视图模型：持有全部服务与子视图模型，承载内核生命周期与订阅编排。
/// 由 View 负责把后台推送投递回 UI 线程（见 IUiDispatcher）。
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly string[] GeoDataFileNames = ["geoip.dat", "geosite.dat", "country.mmdb", "GeoLite2-ASN.mmdb"];

    private readonly SettingsService _settingsService;
    private readonly MihomoConfigBuilder _configBuilder;
    private readonly SubscriptionConfigComposer _composer;
    private readonly SubscriptionFormatConverter _formatConverter;
    private readonly MihomoProcessManager _processManager;
    private readonly MihomoApiClient _apiClient;
    private readonly SystemProxyService _systemProxyService;
    private readonly WindowsStartupService _windowsStartupService;
    private readonly MihomoTelemetry _telemetry;
    private readonly IUiDispatcher _dispatcher;
    private readonly Dictionary<string, string> _delayResults = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private AppSettings _settings = new();

    private CancellationTokenSource? _fallbackCancellation;
    private CancellationTokenSource? _preferenceSaveCancellation;
    private ConnectionsFrame? _previousPoll;
    private DateTimeOffset _previousPollAt;
    private bool _pushHealthy;
    private bool _systemProxyEngaged;
    private bool _isSwitchingProxy;
    private bool _initialized;
    private bool _suppressPreferenceAutoSave;

    [ObservableProperty] private string _message = "就绪";
    [ObservableProperty] private string _coreStatusText = "内核状态: 未启动";
    [ObservableProperty] private bool _isCoreRunning;
    [ObservableProperty] private bool _hasUnsavedChanges;
    [ObservableProperty] private bool _isSaving;

    public bool CanSave => HasUnsavedChanges && !IsSaving;

    partial void OnHasUnsavedChangesChanged(bool value) => OnPropertyChanged(nameof(CanSave));

    partial void OnIsSavingChanged(bool value) => OnPropertyChanged(nameof(CanSave));

    public MainViewModel(IUiDispatcher dispatcher)
        : this(new SettingsService(), new MihomoConfigBuilder(), new SubscriptionConfigComposer(),
               new SubscriptionFormatConverter(), new MihomoProcessManager(), new MihomoApiClient(),
               new SystemProxyService(), new WindowsStartupService(), new MihomoTelemetry(), dispatcher)
    {
    }

    public MainViewModel(
        SettingsService settingsService,
        MihomoConfigBuilder configBuilder,
        SubscriptionConfigComposer composer,
        SubscriptionFormatConverter formatConverter,
        MihomoProcessManager processManager,
        MihomoApiClient apiClient,
        SystemProxyService systemProxyService,
        WindowsStartupService windowsStartupService,
        MihomoTelemetry telemetry,
        IUiDispatcher dispatcher)
    {
        _settingsService = settingsService;
        _configBuilder = configBuilder;
        _composer = composer;
        _formatConverter = formatConverter;
        _processManager = processManager;
        _apiClient = apiClient;
        _systemProxyService = systemProxyService;
        _windowsStartupService = windowsStartupService;
        _telemetry = telemetry;
        _dispatcher = dispatcher;

        Settings = new SettingsViewModel();
        Telemetry = new TelemetryViewModel();
        Subscriptions = new SubscriptionsViewModel();
        ProxyGroups = new ProxyGroupsViewModel();
        Rules = new RulesViewModel();
        Logs = new LogsViewModel();

        _processManager.OutputReceived += line => _dispatcher.Post(() => Logs.Append(line));
        _telemetry.TrafficReceived += frame => _dispatcher.Post(() => Telemetry.Update(frame));
        _telemetry.ConnectionsReceived += frame => _dispatcher.Post(() => Telemetry.Update(frame));
        _telemetry.LogReceived += (_, frame) => _dispatcher.Post(() => Logs.Append(frame.Payload));
        _telemetry.StreamStateChanged += (_, _, _) => _dispatcher.Post(RefreshPushHealth);
        Settings.PropertyChanged += (_, e) =>
        {
            HasUnsavedChanges = Settings.IsDirty;
            if (!_suppressPreferenceAutoSave
                && _initialized
                && !Settings.IsLoading
                && SettingsViewModel.IsPreferenceProperty(e.PropertyName))
            {
                SchedulePreferenceSave();
            }
        };
    }

    public SettingsViewModel Settings { get; }

    public TelemetryViewModel Telemetry { get; }

    public SubscriptionsViewModel Subscriptions { get; }

    public ProxyGroupsViewModel ProxyGroups { get; }

    public RulesViewModel Rules { get; }

    public LogsViewModel Logs { get; }

    /// <summary>加载设置并按需自动启动内核。失败只提示到状态栏，与迁移前一致。</summary>
    public Task InitializeAsync() => RunSafelyAsync(InitializeCoreAsync);

    private async Task InitializeCoreAsync()
    {
        var settings = await _settingsService.LoadAsync();
        ApplyDefaults(settings);
        SubscriptionUsage.EnsureSingleActive(settings);
        _settings = settings;
        Settings.LoadFrom(settings);
        Subscriptions.Load(settings);
        Rules.SetSubscriptionRules(settings.SubscriptionRules);
        Telemetry.ShowCoreStopped();
        Rules.ShowCoreStopped();
        LoadCachedProxyGroups();
        RefreshRuntimeIndicators();

        if (Settings.StartWithWindows)
        {
            await _windowsStartupService.SetEnabledAsync(true);
        }
        if (settings.StartCoreOnProgramStart)
        {
            await StartCoreAsync();
        }

        _initialized = true;
    }

    /// <summary>托盘与窗口共享同一份状态文本，避免同一个概念散落在三处写入。</summary>
    public string TrayText => $"{AppInstance.DisplayName} - {CoreStatusText}";

    partial void OnCoreStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(TrayText));
    }

    // ---------------------------------------------------------------- 内核生命周期

    [RelayCommand]
    private Task StartCoreAsync() => RunSafelyAsync(StartCoreAsyncInternal);

    private async Task StartCoreAsyncInternal()
    {
        if (IsCoreRunning)
        {
            StopCoreCore(quiet: true);
            Logs.Append("正在重启 mihomo 内核...");
        }

        await StartInternalAsync(localProviders: null);
    }

    [RelayCommand]
    private void StopCore() => RunSafely(StopCoreInternal);

    private void StopCoreInternal()
    {
        StopCoreCore(quiet: false);
        SetStatus("已停止");
        Telemetry.VersionText = "内核版本: -";
        LoadCachedProxyGroups();
        Telemetry.ShowCoreStopped();
        Rules.ShowCoreStopped();
        RefreshRuntimeIndicators();
        Message = "内核已停止。";
    }

    private void StopCoreCore(bool quiet)
    {
        StopFallbackPolling();
        _telemetry.Stop();

        // 只撤销本实例自己打开的系统代理：并行实例运行时旧版可能正接管着系统代理，
        // 若仅凭设置里的 EnableSystemProxy 就关闭，会直接掐断用户正在使用的旧版。
        if (_systemProxyEngaged)
        {
            try
            {
                _systemProxyService.Disable();
                if (!quiet)
                {
                    Logs.Append("系统代理已关闭。");
                }
            }
            catch (Exception ex)
            {
                Logs.Append($"关闭系统代理失败: {ex.Message}");
            }
            finally
            {
                _systemProxyEngaged = false;
            }
        }

        _processManager.Stop();
        IsCoreRunning = false;
    }

    private async Task StartInternalAsync(HashSet<string>? localProviders)
    {
        if (_processManager.IsRunning)
        {
            throw new InvalidOperationException("内核已在运行中。");
        }

        if (Settings.IsDirty)
        {
            Message = "存在未保存更改：本次启动使用已保存的配置。";
        }

        EnsureBundledGeoDataFiles(_settings.Core.WorkingDirectory);
        var configPath = WriteMihomoConfigFile(_settings, localProviders);
        _processManager.Start(_settings.Core.CoreExecutablePath, _settings.Core.WorkingDirectory, configPath);
        ConfigureApiClient();

        SetStatus("启动中");
        Logs.Append("正在启动 mihomo 内核...");

        string version = "unknown";
        var apiReady = false;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(500);
            try
            {
                version = await _apiClient.GetVersionAsync();
                apiReady = true;
                Logs.Append($"API 已就绪，尝试次数: {attempt + 1}");
                break;
            }
            catch (Exception ex) when (attempt < 9)
            {
                Logs.Append($"等待 API 就绪 ({attempt + 1}/10): {ex.GetType().Name}");
            }
        }

        if (!apiReady)
        {
            _processManager.Stop();
            IsCoreRunning = false;
            SetStatus("已停止");
            throw new InvalidOperationException("内核启动失败: API 无响应");
        }

        IsCoreRunning = true;
        Telemetry.IsCoreRunning = true;
        Telemetry.VersionText = $"内核版本: {version}";
        SetStatus("运行中");

        if (_settings.GeoAutoUpdate)
        {
            try
            {
                Logs.Append("正在触发 GEO 数据自动更新...");
                await _apiClient.ReloadConfigsAsync(Path.Combine(_settings.Core.WorkingDirectory, "config.yaml"));
            }
            catch (Exception ex)
            {
                Logs.Append($"触发 GEO 更新失败: {ex.Message}");
            }
        }

        if (_settings.Core.EnableSystemProxy)
        {
            _systemProxyService.Enable("127.0.0.1", _settings.Core.MixedPort);
            _systemProxyEngaged = true;
            Logs.Append($"系统代理已启用: 127.0.0.1:{_settings.Core.MixedPort}");
        }

        Telemetry.ResetRates();
        _delayResults.Clear();
        _previousPoll = null;
        StartTelemetry();
        await RefreshProxyGroupsAsync();
        RefreshRuntimeIndicators();
        Message = "内核已启动。";
    }

    private void StartTelemetry()
    {
        _telemetry.Start(
            _settings.Core.ExternalControllerHost,
            _settings.Core.ExternalControllerPort,
            _settings.Core.Secret);

        _fallbackCancellation = new CancellationTokenSource();
        _ = FallbackPollLoopAsync(_fallbackCancellation.Token);
    }

    /// <summary>
    /// 推送不可用时兜底轮询 /connections，与推送共用同一套渲染路径。
    /// 推送恢复后自动闲置，只保留一个循环。
    /// </summary>
    private async Task FallbackPollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(Math.Max(_settings.StatsRefreshSeconds, 1));
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_pushHealthy || !_processManager.IsRunning)
            {
                continue;
            }

            try
            {
                ConfigureApiClient();
                var frame = await _apiClient.GetConnectionsFrameAsync(cancellationToken);
                _dispatcher.Post(() => ApplyPolledFrame(frame));
            }
            catch (Exception)
            {
                // 轮询失败保持静默，等待下一轮或推送恢复。
            }
        }
    }

    private void ApplyPolledFrame(ConnectionsFrame frame)
    {
        var now = DateTimeOffset.UtcNow;
        var seconds = _previousPoll is null ? 0d : Math.Max((now - _previousPollAt).TotalSeconds, 0.001d);
        var upRate = _previousPoll is null ? 0d : Math.Max(0d, (frame.UploadTotal - _previousPoll.UploadTotal) / seconds);
        var downRate = _previousPoll is null ? 0d : Math.Max(0d, (frame.DownloadTotal - _previousPoll.DownloadTotal) / seconds);
        _previousPoll = frame;
        _previousPollAt = now;

        Telemetry.Update(frame);
        Telemetry.Update(new TrafficFrame { Up = (long)upRate, Down = (long)downRate, UpTotal = frame.UploadTotal, DownTotal = frame.DownloadTotal });
    }

    private void StopFallbackPolling()
    {
        _fallbackCancellation?.Cancel();
        _fallbackCancellation = null;
        _pushHealthy = false;
    }

    private void RefreshPushHealth()
    {
        var healthy = _telemetry.IsHealthy;
        if (healthy == _pushHealthy)
        {
            return;
        }

        _pushHealthy = healthy;
        if (!IsCoreRunning)
        {
            return;
        }

        Message = healthy ? "实时推送已连接。" : "实时推送断开，已回退轮询，正在重连…";
    }

    private void SetStatus(string state)
    {
        CoreStatusText = $"内核状态: {state}";
    }

    private void ConfigureApiClient()
    {
        _apiClient.Configure(
            _settings.Core.ExternalControllerHost,
            _settings.Core.ExternalControllerPort,
            _settings.Core.Secret);
    }

    private void RefreshRuntimeIndicators()
    {
        Telemetry.UpdateRuntimeIndicators(
            _settings.Core.EnableSystemProxy,
            _settings.Core.MixedPort);
    }

    private void EnsureCoreRunning()
    {
        if (!_processManager.IsRunning)
        {
            throw new InvalidOperationException("请先启动内核。");
        }
    }

    // ---------------------------------------------------------------- 保存与配置

    [RelayCommand]
    private Task SaveSettingsAsync() => RunSafelyAsync(SaveInternalAsync);

    private async Task SaveInternalAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            if (IsSaving)
            {
                return;
            }

            IsSaving = true;
            _preferenceSaveCancellation?.Cancel();

            var previous = _settings;
            var candidate = CloneSettings();
            Settings.ApplyTo(candidate);
            candidate.Subscriptions = Subscriptions.Items.Select(x => CloneSubscription(x.Item)).ToList();

            var startupEnabled = await _windowsStartupService.SetEnabledAsync(candidate.StartWithWindows);
            var startupBlocked = candidate.StartWithWindows && !startupEnabled;
            if (startupBlocked)
            {
                candidate.StartWithWindows = false;
            }

            await PersistSettingsAsync(candidate);

            Subscriptions.Load(_settings);
            if (startupBlocked)
            {
                Settings.StartWithWindows = false;
            }

            RefreshRuntimeIndicators();

            string applied;
            try
            {
                var configPath = Path.Combine(candidate.Core.WorkingDirectory, "config.yaml");
                var needsConfig = HasConfigChanges(previous, candidate) || !File.Exists(configPath);

                if (!needsConfig)
                {
                    applied = "设置已保存。";
                }
                else
                {
                    configPath = WriteMihomoConfigFile(candidate);
                    applied = _processManager.IsRunning
                        ? $"设置已保存，{await ApplyToRunningCoreAsync(previous, candidate, configPath)}"
                        : "设置已保存，并已生成 mihomo 配置文件。";
                }
            }
            catch (Exception ex)
            {
                Logs.Append($"生成或应用配置失败: {ex.Message}");
                // 设置文件已经落盘，但运行时配置没应用成功：保持 dirty，方便直接重试保存。
                var failed = _processManager.IsRunning
                    ? "设置已保存，但配置应用失败，请重试。"
                    : "设置已保存，但配置生成失败，请重试。";
                Message = startupBlocked ? $"{failed.TrimEnd('。')}；{StartupBlockedReason}。" : failed;
                return;
            }

            Settings.MarkSaved();
            Message = startupBlocked ? $"{applied.TrimEnd('。')}；{StartupBlockedReason}。" : applied;
        }
        finally
        {
            IsSaving = false;
            _saveGate.Release();
        }
    }

    /// <summary>撤销本次编辑，回到最近一次成功保存的设置。</summary>
    [RelayCommand]
    private void UndoChanges()
    {
        _preferenceSaveCancellation?.Cancel();
        Settings.LoadFrom(_settings);
        Message = "已撤销未保存的修改。";
    }

    /// <summary>
    /// 重置配置类字段为默认值，但保留订阅列表与当前订阅标记；结果只进入草稿，
    /// 用户仍可通过“撤销”恢复，或点击保存后生效。
    /// </summary>
    [RelayCommand]
    private void RestoreDefaults()
    {
        _preferenceSaveCancellation?.Cancel();

        var defaults = AppInstance.CreateDefaultSettings();
        ApplyDefaults(defaults);
        defaults.Subscriptions = _settings.Subscriptions.Select(CloneSubscription).ToList();
        defaults.ActiveSubscriptionName = _settings.ActiveSubscriptionName;
        defaults.SubscriptionRules = [.. _settings.SubscriptionRules];

        Settings.LoadFrom(defaults);
        Settings.MarkDirty();
        Message = "已恢复默认设置，点击保存后生效。";
    }

    private void SchedulePreferenceSave()
    {
        _preferenceSaveCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _preferenceSaveCancellation = cancellation;
        _ = SavePreferencesAfterDelayAsync(cancellation.Token);
    }

    private async Task SavePreferencesAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(500, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _dispatcher.Post(() => _ = SavePreferencesNowAsync());
    }

    public Task SavePreferencesNowAsync() => RunSafelyAsync(SavePreferencesCoreAsync);

    private async Task SavePreferencesCoreAsync()
    {
        if (!_initialized || Settings.IsLoading)
        {
            return;
        }

        await _saveGate.WaitAsync();
        try
        {
            if (IsSaving)
            {
                return;
            }

            var previous = _settings;
            var candidate = CloneSettings();
            Settings.ApplyPreferencesTo(candidate);

            if (!PreferencesDiffer(previous, candidate))
            {
                Settings.MarkPreferencesSaved();
                return;
            }

            var startupBlocked = false;
            if (candidate.StartWithWindows != previous.StartWithWindows)
            {
                var startupEnabled = await _windowsStartupService.SetEnabledAsync(candidate.StartWithWindows);
                startupBlocked = candidate.StartWithWindows && !startupEnabled;
                if (startupBlocked)
                {
                    candidate.StartWithWindows = false;
                }
            }

            await PersistSettingsAsync(candidate);

            if (startupBlocked)
            {
                _suppressPreferenceAutoSave = true;
                try
                {
                    Settings.StartWithWindows = false;
                }
                finally
                {
                    _suppressPreferenceAutoSave = false;
                }

                Message = $"偏好已保存，但{StartupBlockedReason}。";
            }

            Settings.MarkPreferencesSaved();
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private static bool PreferencesDiffer(AppSettings previous, AppSettings next)
    {
        return previous.StatsRefreshSeconds != next.StatsRefreshSeconds
            || previous.StartCoreOnProgramStart != next.StartCoreOnProgramStart
            || previous.StartWithWindows != next.StartWithWindows
            || !string.Equals(previous.ProxyTest.Url, next.ProxyTest.Url, StringComparison.Ordinal)
            || previous.ProxyTest.TimeoutMilliseconds != next.ProxyTest.TimeoutMilliseconds;
    }

    private static bool HasConfigChanges(AppSettings previous, AppSettings next)
    {
        return !CoreEquals(previous.Core, next.Core)
            || !DnsEquals(previous.Dns, next.Dns)
            || !GeoxEquals(previous.GeoxUrls, next.GeoxUrls)
            || previous.GeoDataMode != next.GeoDataMode
            || previous.GeoAutoUpdate != next.GeoAutoUpdate
            || previous.GeoUpdateIntervalHours != next.GeoUpdateIntervalHours
            || !string.Equals(previous.RuleOverrides, next.RuleOverrides, StringComparison.Ordinal)
            || !previous.SubscriptionRules.SequenceEqual(next.SubscriptionRules, StringComparer.Ordinal);
    }

    private static bool CoreEquals(CoreRuntimeSettings previous, CoreRuntimeSettings next)
    {
        return string.Equals(previous.CoreExecutablePath, next.CoreExecutablePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(previous.WorkingDirectory, next.WorkingDirectory, StringComparison.OrdinalIgnoreCase)
            && string.Equals(previous.ExternalControllerHost, next.ExternalControllerHost, StringComparison.OrdinalIgnoreCase)
            && previous.ExternalControllerPort == next.ExternalControllerPort
            && string.Equals(previous.Secret, next.Secret, StringComparison.Ordinal)
            && previous.MixedPort == next.MixedPort
            && previous.SocksPort == next.SocksPort
            && previous.HttpPort == next.HttpPort
            && previous.EnableSystemProxy == next.EnableSystemProxy
            && previous.BypassLocalNetworks == next.BypassLocalNetworks;
    }

    private static bool DnsEquals(DnsSettings previous, DnsSettings next)
    {
        return previous.Enable == next.Enable
            && string.Equals(previous.Listen, next.Listen, StringComparison.Ordinal)
            && previous.Ipv6 == next.Ipv6
            && string.Equals(previous.EnhancedMode, next.EnhancedMode, StringComparison.Ordinal)
            && string.Equals(previous.FakeIpRange, next.FakeIpRange, StringComparison.Ordinal)
            && previous.FakeIpFilter.SequenceEqual(next.FakeIpFilter, StringComparer.Ordinal)
            && previous.DefaultNameserver.SequenceEqual(next.DefaultNameserver, StringComparer.Ordinal)
            && previous.Nameserver.SequenceEqual(next.Nameserver, StringComparer.Ordinal)
            && previous.Fallback.SequenceEqual(next.Fallback, StringComparer.Ordinal)
            && previous.FallbackFilterGeoIp == next.FallbackFilterGeoIp
            && string.Equals(previous.FallbackFilterGeoIpCode, next.FallbackFilterGeoIpCode, StringComparison.Ordinal)
            && previous.FallbackFilterIpCidr.SequenceEqual(next.FallbackFilterIpCidr, StringComparer.Ordinal);
    }

    private static bool GeoxEquals(GeoxUrlSettings previous, GeoxUrlSettings next)
    {
        return string.Equals(previous.GeoIp, next.GeoIp, StringComparison.Ordinal)
            && string.Equals(previous.GeoSite, next.GeoSite, StringComparison.Ordinal)
            && string.Equals(previous.Mmdb, next.Mmdb, StringComparison.Ordinal)
            && string.Equals(previous.Asn, next.Asn, StringComparison.Ordinal);
    }

    /// <summary>热重载不会改变 API 端点；走到这里说明设置里改了内核路径、工作目录或控制端口/密钥。</summary>
    private static bool CanReloadCore(AppSettings previous, AppSettings next)
    {
        return string.Equals(previous.Core.CoreExecutablePath, next.Core.CoreExecutablePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(previous.Core.WorkingDirectory, next.Core.WorkingDirectory, StringComparison.OrdinalIgnoreCase)
            && string.Equals(previous.Core.ExternalControllerHost, next.Core.ExternalControllerHost, StringComparison.OrdinalIgnoreCase)
            && previous.Core.ExternalControllerPort == next.Core.ExternalControllerPort
            && string.Equals(previous.Core.Secret, next.Core.Secret, StringComparison.Ordinal);
    }

    private async Task<string> ApplyToRunningCoreAsync(AppSettings previous, AppSettings next, string configPath)
    {
        if (CanReloadCore(previous, next))
        {
            var reloaded = false;
            try
            {
                ConfigureApiClient();
                await _apiClient.ReloadConfigsAsync(configPath);
                ApplySystemProxyChange(previous.Core, next.Core);
                Logs.Append("内核配置已热重载。");
                reloaded = true;
            }
            catch (Exception ex)
            {
                Logs.Append($"配置热重载失败，将重启内核: {ex.Message}");
            }

            if (reloaded)
            {
                try
                {
                    await RefreshProxyGroupsCoreAsync();
                }
                catch (Exception ex)
                {
                    Logs.Append($"刷新代理组失败: {ex.Message}");
                }

                return "内核配置已热重载。";
            }
        }

        StopCoreCore(quiet: false);
        Logs.Append("内核已停止，正在应用新设置并重启。");
        await StartInternalAsync(localProviders: null);
        return "内核已重启。";
    }

    /// <summary>热重载只能改配置，系统代理是应用侧状态，需要按新端口单独同步。</summary>
    private void ApplySystemProxyChange(CoreRuntimeSettings previous, CoreRuntimeSettings next)
    {
        var needsEnable = next.EnableSystemProxy
            && (!previous.EnableSystemProxy || previous.MixedPort != next.MixedPort || !_systemProxyEngaged);

        try
        {
            if (needsEnable)
            {
                _systemProxyService.Enable("127.0.0.1", next.MixedPort);
                _systemProxyEngaged = true;
                Logs.Append($"系统代理已启用: 127.0.0.1:{next.MixedPort}");
            }
            else if (!next.EnableSystemProxy && _systemProxyEngaged)
            {
                _systemProxyService.Disable();
                _systemProxyEngaged = false;
                Logs.Append("系统代理已关闭。");
            }
        }
        catch (Exception ex)
        {
            Logs.Append($"同步系统代理失败: {ex.Message}");
        }
    }

    private static string StartupBlockedReason => AppInstance.IsIsolated
        ? "并行调试实例不注册开机自启"
        : "开机自启被系统禁用";

    private async Task PersistSettingsAsync(AppSettings settings)
    {
        await _settingsService.SaveAsync(settings);
        _settings = settings;
    }

    /// <summary>写配置并应用到内核；订阅与设置页共用同一条路径。</summary>
    private async Task<string> ApplyCurrentConfigurationAsync(
        AppSettings previous,
        AppSettings next,
        HashSet<string>? localProviders = null)
    {
        var configPath = WriteMihomoConfigFile(next, localProviders);
        return _processManager.IsRunning
            ? await ApplyToRunningCoreAsync(previous, next, configPath)
            : "已生成 mihomo 配置文件。";
    }

    /// <summary>
    /// 复制当前已保存设置，作为界面编辑的落点。所有嵌套对象都必须深拷贝，
    /// 否则 ApplyTo 会在保存成功前改写 _settings（保存失败时内存状态已脏）。
    /// </summary>
    private AppSettings CloneSettings()
    {
        return new AppSettings
        {
            Core = CloneCore(_settings.Core),
            Subscriptions = _settings.Subscriptions.Select(CloneSubscription).ToList(),
            ActiveSubscriptionName = _settings.ActiveSubscriptionName,
            SubscriptionRules = [.. _settings.SubscriptionRules],
            GeoxUrls = CloneGeoxUrls(_settings.GeoxUrls),
            Dns = CloneDns(_settings.Dns),
            ProxyTest = CloneProxyTest(_settings.ProxyTest),
            RuleOverrides = _settings.RuleOverrides,
            StatsRefreshSeconds = _settings.StatsRefreshSeconds,
            StartCoreOnProgramStart = _settings.StartCoreOnProgramStart,
            StartWithWindows = _settings.StartWithWindows,
            GeoDataMode = _settings.GeoDataMode,
            GeoAutoUpdate = _settings.GeoAutoUpdate,
            GeoUpdateIntervalHours = _settings.GeoUpdateIntervalHours,
        };
    }

    private static CoreRuntimeSettings CloneCore(CoreRuntimeSettings core) => new()
    {
        CoreExecutablePath = core.CoreExecutablePath,
        WorkingDirectory = core.WorkingDirectory,
        ExternalControllerHost = core.ExternalControllerHost,
        ExternalControllerPort = core.ExternalControllerPort,
        Secret = core.Secret,
        MixedPort = core.MixedPort,
        SocksPort = core.SocksPort,
        HttpPort = core.HttpPort,
        EnableSystemProxy = core.EnableSystemProxy,
        BypassLocalNetworks = core.BypassLocalNetworks,
    };

    private static SubscriptionItem CloneSubscription(SubscriptionItem item) => new()
    {
        Name = item.Name,
        Url = item.Url,
        IntervalSeconds = item.IntervalSeconds,
        Enabled = item.Enabled,
        UploadBytes = item.UploadBytes,
        DownloadBytes = item.DownloadBytes,
        TotalBytes = item.TotalBytes,
        ExpireAt = item.ExpireAt,
    };

    private static GeoxUrlSettings CloneGeoxUrls(GeoxUrlSettings geox) => new()
    {
        GeoIp = geox.GeoIp,
        GeoSite = geox.GeoSite,
        Mmdb = geox.Mmdb,
        Asn = geox.Asn,
    };

    private static ProxyTestSettings CloneProxyTest(ProxyTestSettings proxyTest) => new()
    {
        Url = proxyTest.Url,
        TimeoutMilliseconds = proxyTest.TimeoutMilliseconds,
    };

    private static DnsSettings CloneDns(DnsSettings dns) => new()
    {        Enable = dns.Enable,
        Listen = dns.Listen,
        Ipv6 = dns.Ipv6,
        EnhancedMode = dns.EnhancedMode,
        FakeIpRange = dns.FakeIpRange,
        FakeIpFilter = [.. dns.FakeIpFilter],
        DefaultNameserver = [.. dns.DefaultNameserver],
        Nameserver = [.. dns.Nameserver],
        Fallback = [.. dns.Fallback],
        FallbackFilterGeoIp = dns.FallbackFilterGeoIp,
        FallbackFilterGeoIpCode = dns.FallbackFilterGeoIpCode,
        FallbackFilterIpCidr = [.. dns.FallbackFilterIpCidr],
    };

    private string WriteMihomoConfigFile(AppSettings settings, HashSet<string>? localProviders = null)
    {
        Directory.CreateDirectory(settings.Core.WorkingDirectory);
        var path = Path.Combine(settings.Core.WorkingDirectory, "config.yaml");
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, BuildEffectiveConfig(settings, localProviders), new UTF8Encoding(false));
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            TryDelete(tempPath);
        }

        return path;
    }

    private string BuildEffectiveConfig(AppSettings settings, HashSet<string>? localProviders)
    {
        var templatePath = GetSubscriptionTemplatePath(settings);
        if (!string.IsNullOrWhiteSpace(templatePath) && File.Exists(templatePath))
        {
            var activePath = GetActiveSubscriptionPath(settings);
            if (!string.Equals(templatePath, activePath, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(activePath)!);
                File.Copy(templatePath, activePath, overwrite: true);
            }

            var text = ReadText(templatePath);
            Logs.Append($"使用订阅文件: {templatePath} ({text.Length} 字符)");
            return _composer.Compose(text, settings);
        }

        if (settings.Subscriptions.Any(x => x.Enabled))
        {
            throw new InvalidOperationException("找不到已下载的订阅配置，请先选择订阅并执行”添加/更新订阅”。");
        }

        return _configBuilder.Build(settings, localProviders);
    }

    // ---------------------------------------------------------------- 订阅

    [RelayCommand]
    private Task AddOrUpdateSubscriptionAsync() => RunSafelyAsync(AddOrUpdateSubscriptionAsyncInternal);

    private async Task AddOrUpdateSubscriptionAsyncInternal()
    {
        var name = Subscriptions.EditName.Trim();
        var url = Subscriptions.EditUrl.Trim();
        var interval = SettingsParsing.ParsePositiveInt(Subscriptions.EditIntervalText, "更新间隔(秒)");

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("订阅名称不能为空。");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("订阅 URL 无效。");
        }

        var existing = _settings.Subscriptions.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        var previous = CloneSettings();
        bool isActive;
        if (existing is null)
        {
            _settings.Subscriptions.Add(new SubscriptionItem
            {
                Name = name,
                Url = url,
                IntervalSeconds = interval,
                Enabled = false,
            });
            isActive = false;
            Message = "已新增订阅。";
        }
        else
        {
            existing.Url = url;
            existing.IntervalSeconds = interval;
            isActive = existing.Enabled;
            Message = "已更新订阅。";
        }

        await PersistSettingsAsync(_settings);
        Subscriptions.Load(_settings);

        var subscription = _settings.Subscriptions.First(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        await DownloadAndSaveSubscriptionAsync(subscription);

        if (!isActive)
        {
            return;
        }

        File.Copy(GetSubscriptionFilePath(name), GetActiveSubscriptionPath(_settings), overwrite: true);
        var applied = await ApplyCurrentConfigurationAsync(previous, _settings);
        Message = $"已下载订阅文件，{applied}";
    }

    [RelayCommand]
    private Task RemoveSubscriptionAsync() => RunSafelyAsync(RemoveSubscriptionAsyncInternal);

    private async Task RemoveSubscriptionAsyncInternal()
    {
        var selected = Subscriptions.SelectedSubscription
            ?? throw new InvalidOperationException("请先选择要删除的订阅。");

        var previous = CloneSettings();
        var wasActive = string.Equals(
            _settings.ActiveSubscriptionName,
            selected.Name,
            StringComparison.OrdinalIgnoreCase);
        var activePath = GetActiveSubscriptionPath(_settings);
        var subscriptionPath = GetSubscriptionFilePath(selected.Name);

        _settings.Subscriptions.Remove(selected.Item);
        if (wasActive)
        {
            _settings.ActiveSubscriptionName = string.Empty;
        }

        // 先落盘再删文件：保存失败时不会出现设置仍引用、文件却已丢失的情况。
        await PersistSettingsAsync(_settings);

        if (wasActive)
        {
            TryDelete(activePath);
        }

        TryDelete(subscriptionPath);

        Subscriptions.Load(_settings);

        if (wasActive && _processManager.IsRunning)
        {
            try
            {
                var applied = await ApplyCurrentConfigurationAsync(previous, _settings);
                Message = $"订阅已删除，{applied}";
                return;
            }
            catch (Exception ex)
            {
                Logs.Append($"删除订阅后应用配置失败: {ex.Message}");
                Message = "订阅已删除；当前内核仍使用旧配置，重启内核后生效。";
                return;
            }
        }

        Message = "订阅已删除。";
    }

    [RelayCommand]
    private Task ActivateSubscriptionAsync() => RunSafelyAsync(ActivateSubscriptionAsyncInternal);

    private async Task ActivateSubscriptionAsyncInternal()
    {
        var selected = Subscriptions.SelectedSubscription
            ?? throw new InvalidOperationException("请先选择订阅。");

        var subscriptionFile = GetSubscriptionFilePath(selected.Name);
        if (!File.Exists(subscriptionFile))
        {
            throw new InvalidOperationException($"找不到订阅文件，请先通过「新增/更新」下载订阅: {selected.Name}");
        }

        SubscriptionUsage.DisableOthers(_settings, selected.Name);
        var previous = CloneSettings();
        selected.Item.Enabled = true;
        _settings.ActiveSubscriptionName = selected.Name;
        await PersistSettingsAsync(_settings);
        Subscriptions.Load(_settings);
        Logs.Append($"已激活订阅: {selected.Name}");

        File.Copy(subscriptionFile, GetActiveSubscriptionPath(_settings), overwrite: true);

        var content = ReadText(subscriptionFile);
        _settings.SubscriptionRules = SubscriptionParser.Parse(content).Rules;
        Rules.SetSubscriptionRules(_settings.SubscriptionRules);

        var applied = await ApplyCurrentConfigurationAsync(previous, _settings);
        Message = $"已激活订阅: {selected.Name}，{applied}";
    }

    private async Task DownloadAndSaveSubscriptionAsync(SubscriptionItem subscription)
    {
        var downloadPath = GetSubscriptionFilePath(subscription.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(downloadPath)!);

        Logs.Append($"正在下载订阅文件: {subscription.Url}");
        var download = await _apiClient.DownloadSubscriptionAsync(subscription.Url);
        var convertResult = _formatConverter.Convert(download.Content);

        switch (convertResult.Format)
        {
            case SubscriptionFormat.Yaml:
                Logs.Append("订阅格式: YAML");
                break;
            case SubscriptionFormat.ConvertedFromUris:
                Logs.Append("订阅格式: 已从代理 URI 转换为 YAML 配置");
                break;
            default:
                Logs.Append("错误: 无法识别订阅格式，内容既不是 YAML 也不是已知的代理 URI 格式");
                throw new InvalidOperationException(
                    "无法解析订阅内容。支持的格式: YAML 配置、Base64 编码的 YAML、代理 URI (anytls/ss/trojan/vless 等)。" +
                    "请检查订阅 URL 是否正确。");
        }

        File.WriteAllText(downloadPath, convertResult.Content, new UTF8Encoding(false));
        Logs.Append($"已保存订阅文件: {downloadPath}");

        SubscriptionUsage.Apply(subscription, download.UserInfo);
        Subscriptions.RefreshCells();
        _settings.SubscriptionRules = SubscriptionParser.Parse(convertResult.Content).Rules;
        Rules.SetSubscriptionRules(_settings.SubscriptionRules);
    }

    // ---------------------------------------------------------------- 代理组

    [RelayCommand]
    private Task RefreshProxyGroupsAsync() => RunSafelyAsync(RefreshProxyGroupsCoreAsync);

    private async Task RefreshProxyGroupsCoreAsync()
    {
        EnsureCoreRunning();
        ConfigureApiClient();
        var groups = OrderProxyGroupsBySubscription(await _apiClient.GetProxyGroupsAsync());
        ProxyGroups.Replace(groups, NameOfDelay, showCurrentInHeader: true);
        Message = $"已刷新 {groups.Count} 个代理组。";
    }

    /// <summary>成员列表单击即切换：点击行直接调用内核 API，不再需要单独的“切换节点”按钮。</summary>
    public Task SelectProxyMemberAsync(ProxyMemberViewModel? member)
    {
        // 连续点击多个节点时只保留第一次切换，避免两次刷新交错把选中项改回去。
        if (_isSwitchingProxy)
        {
            return Task.CompletedTask;
        }

        return RunSafelyAsync(async () =>
        {
            _isSwitchingProxy = true;
            try
            {
                await SelectProxyMemberAsyncInternal(member);
            }
            finally
            {
                _isSwitchingProxy = false;
            }
        });
    }

    private async Task SelectProxyMemberAsyncInternal(ProxyMemberViewModel? member)
    {
        EnsureCoreRunning();
        var group = ProxyGroups.SelectedGroup ?? throw new InvalidOperationException("请先选择代理组。");
        var proxyName = member?.NodeName;

        if (string.IsNullOrWhiteSpace(proxyName))
        {
            return;
        }

        // 点击当前节点时不重复调用，避免整表刷新造成闪烁。
        if (string.Equals(proxyName, group.Current, StringComparison.Ordinal))
        {
            return;
        }

        ConfigureApiClient();
        await _apiClient.SelectProxyAsync(group.Name, proxyName);
        Logs.Append($"代理组 {group.Name} 已切换到 {proxyName}");
        await RefreshProxyGroupsCoreAsync();
        Message = $"已切换到 {proxyName}。";
    }

    [RelayCommand]
    private Task TestAllDelaysAsync() => RunSafelyAsync(TestAllDelaysAsyncInternal);

    private async Task TestAllDelaysAsyncInternal()
    {
        EnsureCoreRunning();
        var (url, timeout) = ReadDelayTestArguments();
        ConfigureApiClient();
        var proxyNames = await _apiClient.GetTestableProxyNamesAsync();
        if (proxyNames.Count == 0)
        {
            throw new InvalidOperationException("未找到可测速的代理节点。");
        }

        var total = proxyNames.Count;
        var completed = 0;
        var usable = 0;
        ProxyGroups.AllDelayStatusText = $"正在测试 0/{total} 个节点...";
        ProxyGroups.IsTestingAllDelays = true;

        try
        {
            using var concurrency = new SemaphoreSlim(4);
            await Task.WhenAll(proxyNames.Select(async name =>
            {
                int? delay = null;
                await concurrency.WaitAsync();
                try
                {
                    delay = await _apiClient.TestProxyDelayAsync(name, url, timeout);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    delay = null;
                }
                finally
                {
                    concurrency.Release();
                }

                if (delay.HasValue)
                {
                    Interlocked.Increment(ref usable);
                }

                var done = Interlocked.Increment(ref completed);

                // 测完一个落一个，不等全部节点返回
                _dispatcher.Post(() =>
                {
                    var delayText = delay.HasValue ? $"{delay.Value} ms" : "失败";
                    _delayResults[name] = delayText;
                    ProxyGroups.SetDelay(name, delayText);
                    ProxyGroups.AllDelayStatusText = $"正在测试 {done}/{total} 个节点...";
                });
            }));

            ProxyGroups.AllDelayStatusText = $"测速完成: {usable}/{total} 个节点可用";
            Message = $"全部节点测速完成: {usable}/{total} 个节点可用。";
        }
        finally
        {
            ProxyGroups.IsTestingAllDelays = false;
        }
    }

    private (string Url, int Timeout) ReadDelayTestArguments()
    {
        var url = Settings.ProxyTestUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("测试 URL 无效。");
        }

        return (url, SettingsParsing.ParsePositiveInt(Settings.ProxyTestTimeoutText, "超时(ms)"));
    }

    private string NameOfDelay(string proxyName)
    {
        return _delayResults.TryGetValue(proxyName, out var delay) ? delay : "未测试";
    }

    private void LoadCachedProxyGroups()
    {
        var templatePath = GetSubscriptionTemplatePath(_settings);
        if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath))
        {
            ProxyGroups.Clear();
            return;
        }

        var text = ReadText(templatePath);
        var groups = _composer.GetProxyGroups(text).ToList();
        ProxyGroups.Replace(groups, NameOfDelay, showCurrentInHeader: false);

        if (groups.Count > 0)
        {
            ProxyGroups.SelectedGroupStatusText = $"订阅配置: {groups.Count} 个代理组";
        }
        else
        {
            ProxyGroups.SelectedGroupStatusText = "订阅中未定义代理组";
            ProxyGroups.CurrentSelectionText = "当前选择: -";
        }
    }

    private List<ProxyGroupInfo> OrderProxyGroupsBySubscription(List<ProxyGroupInfo> groups)
    {
        var templatePath = GetSubscriptionTemplatePath(_settings);
        if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath))
        {
            return groups;
        }

        var order = _composer.GetProxyGroupOrder(ReadText(templatePath));
        if (order.Count == 0)
        {
            return groups;
        }

        var positions = order
            .Select((name, index) => (name, index))
            .ToDictionary(x => x.name, x => x.index, StringComparer.Ordinal);

        return groups
            .Select((group, index) => (group, index))
            .OrderBy(x => positions.TryGetValue(x.group.Name, out var position) ? position : int.MaxValue)
            .ThenBy(x => x.index)
            .Select(x => x.group)
            .ToList();
    }

    // ---------------------------------------------------------------- 规则与外部资源

    [RelayCommand]
    private Task RefreshRulesAsync() => RunSafelyAsync(RefreshRulesAsyncInternal);

    private async Task RefreshRulesAsyncInternal()
    {
        ConfigureApiClient();
        Rules.SetSubscriptionRules(_settings.SubscriptionRules);

        try
        {
            Rules.SetActiveRules(await _apiClient.GetRulesAsync());
        }
        catch (Exception ex)
        {
            Rules.SetActiveRulesError(ex.Message);
        }

        if (_pushHealthy)
        {
            return;
        }

        try
        {
            Telemetry.Update(await _apiClient.GetConnectionsFrameAsync());
        }
        catch (HttpRequestException ex)
        {
            Logs.Append($"读取连接失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ClearLogs() => RunSafely(ClearLogsInternal);

    private void ClearLogsInternal()
    {
        Logs.Clear();
        Message = "日志已清空。";
    }

    [RelayCommand]
    private Task ManualUpdateGeoDataAsync() => RunSafelyAsync(ManualUpdateGeoDataAsyncInternal);

    private async Task ManualUpdateGeoDataAsyncInternal()
    {
        var workingDir = _settings.Core.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(workingDir))
        {
            throw new InvalidOperationException("请先在内核设置中配置工作目录。");
        }

        Directory.CreateDirectory(workingDir);
        var downloads = new (string Url, string FileName)[]
        {
            (Settings.GeoIpUrl.Trim(), "geoip.dat"),
            (Settings.GeoSiteUrl.Trim(), "geosite.dat"),
            (Settings.MmdbUrl.Trim(), "country.mmdb"),
            (Settings.AsnUrl.Trim(), "GeoLite2-ASN.mmdb"),
        };

        using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var downloaded = new List<(string TempPath, string FileName)>();
        foreach (var (url, fileName) in downloads)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                Logs.Append($"跳过 {fileName}: URL 无效");
                continue;
            }

            var tempPath = Path.Combine(workingDir, $"{fileName}.tmp");
            Logs.Append($"正在下载 {fileName}: {url}");
            try
            {
                var data = await client.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(tempPath, data);
                downloaded.Add((tempPath, fileName));
                Logs.Append($"已下载 {fileName} ({data.Length} 字节)");
            }
            catch (Exception ex)
            {
                Logs.Append($"下载 {fileName} 失败: {ex.Message}");
                TryDelete(tempPath);
            }
        }

        if (downloaded.Count == 0)
        {
            throw new InvalidOperationException("未成功下载任何 GEO 数据文件。");
        }

        var wasRunning = _processManager.IsRunning;
        if (wasRunning)
        {
            StopCoreCore(quiet: false);
            Logs.Append("内核已停止，正在替换 GEO 数据文件...");
        }

        foreach (var (tempPath, fileName) in downloaded)
        {
            File.Move(tempPath, Path.Combine(workingDir, fileName), overwrite: true);
            Logs.Append($"已替换 {fileName}");
        }

        if (wasRunning)
        {
            Logs.Append("正在重启内核...");
            await StartInternalAsync(null);
        }

        Message = $"已更新 {downloaded.Count} 个 GEO 数据文件。";
    }

    // ---------------------------------------------------------------- 路径与默认值

    private static string GetActiveSubscriptionPath(AppSettings settings) =>
        Path.Combine(settings.Core.WorkingDirectory, "subscriptions", "active.yaml");

    private string GetSubscriptionFilePath(string subscriptionName) =>
        Path.Combine(_settings.Core.WorkingDirectory, "subscriptions",
            $"{MihomoConfigBuilder.NormalizeName(subscriptionName)}.yaml");

    private static string? GetSubscriptionTemplatePath(AppSettings settings)
    {
        var activePath = GetActiveSubscriptionPath(settings);
        if (File.Exists(activePath))
        {
            return activePath;
        }

        var activeSubscription = settings.Subscriptions.FirstOrDefault(x =>
            x.Enabled && string.Equals(x.Name, settings.ActiveSubscriptionName, StringComparison.OrdinalIgnoreCase));
        var fallback = activeSubscription ?? settings.Subscriptions.FirstOrDefault(x => x.Enabled);
        if (fallback is null)
        {
            return null;
        }

        var legacyProviderPath = Path.Combine(
            settings.Core.WorkingDirectory, "proxy_providers", $"{MihomoConfigBuilder.NormalizeName(fallback.Name)}.yaml");
        return File.Exists(legacyProviderPath) ? legacyProviderPath : null;
    }

    private static string ReadText(string path) =>
        File.ReadAllText(path, new UTF8Encoding(false));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void ApplyDefaults(AppSettings settings)
    {
        var appBaseDir = AppContext.BaseDirectory;
        var repoRoot = Directory.GetParent(appBaseDir)?.Parent?.Parent?.Parent?.FullName ?? appBaseDir;
        var bundledCorePath = Path.Combine(appBaseDir, "assets", "core", "mihomo-windows-amd64-v3.exe");
        var sourceBundledCorePath = Path.Combine(repoRoot, "assets", "core", "mihomo-windows-amd64-v3.exe");
        var legacyCorePath = Path.Combine(repoRoot, "mihomo-windows-amd64-v3.exe");

        if (string.IsNullOrWhiteSpace(settings.Core.CoreExecutablePath))
        {
            settings.Core.CoreExecutablePath = File.Exists(bundledCorePath)
                ? bundledCorePath
                : File.Exists(sourceBundledCorePath)
                    ? sourceBundledCorePath
                    : legacyCorePath;
        }
        else if (!File.Exists(settings.Core.CoreExecutablePath) && File.Exists(bundledCorePath))
        {
            settings.Core.CoreExecutablePath = bundledCorePath;
        }

        if (string.IsNullOrWhiteSpace(settings.Core.WorkingDirectory))
        {
            settings.Core.WorkingDirectory = AppInstance.RuntimeDirectory;
        }

        if (settings.Core.MixedPort <= 0)
        {
            settings.Core.MixedPort = AppInstance.DefaultMixedPort;
        }

        if (settings.Core.SocksPort <= 0)
        {
            settings.Core.SocksPort = AppInstance.DefaultSocksPort;
        }

        if (settings.Core.HttpPort <= 0)
        {
            settings.Core.HttpPort = AppInstance.DefaultHttpPort;
        }

        if (settings.Core.ExternalControllerPort <= 0)
        {
            settings.Core.ExternalControllerPort = AppInstance.DefaultExternalControllerPort;
        }

        if (settings.StatsRefreshSeconds <= 0)
        {
            settings.StatsRefreshSeconds = 2;
        }

        settings.GeoxUrls ??= new GeoxUrlSettings();
        SettingsParsing.EnsureDefaultExternalResourceUrls(settings.GeoxUrls);

        if (settings.GeoUpdateIntervalHours <= 0)
        {
            settings.GeoUpdateIntervalHours = 24;
        }
    }

    private void EnsureBundledGeoDataFiles(string workingDirectory)
    {
        var sourceDir = Path.Combine(AppContext.BaseDirectory, "assets", "geodata");
        if (!Directory.Exists(sourceDir))
        {
            return;
        }

        Directory.CreateDirectory(workingDirectory);
        foreach (var fileName in GeoDataFileNames)
        {
            var sourcePath = Path.Combine(sourceDir, fileName);
            var destinationPath = Path.Combine(workingDirectory, fileName);
            if (File.Exists(sourcePath) && ShouldCopyBundledGeoFile(sourcePath, destinationPath))
            {
                File.Copy(sourcePath, destinationPath, overwrite: true);
                Logs.Append($"已更新内置 GEO 数据: {fileName}");
            }
        }

        foreach (var fileName in GeoDataFileNames)
        {
            var filePath = Path.Combine(workingDirectory, fileName);
            if (File.Exists(filePath))
            {
                Logs.Append($"GEO 数据 {fileName}: 更新于 {File.GetLastWriteTime(filePath):yyyy-MM-dd HH:mm:ss}");
            }
        }
    }

    /// <summary>
    /// 内置 GEO 数据不能只在目标缺失时复制：升级应用后包内文件可能已更新。
    /// 以“包内副本更新或大小不同”为准覆盖，同时保留用户手动下载的更新版本。
    /// </summary>
    private static bool ShouldCopyBundledGeoFile(string sourcePath, string destinationPath)
    {
        if (!File.Exists(destinationPath))
        {
            return true;
        }

        var source = new FileInfo(sourcePath);
        var destination = new FileInfo(destinationPath);
        return source.Length != destination.Length
            || source.LastWriteTimeUtc > destination.LastWriteTimeUtc;
    }

    // ---------------------------------------------------------------- 异常出口

    /// <summary>
    /// 命令的唯一异常出口。迁移前由代码后置的 ExecuteUiActionAsync 兜底，
    /// 改成 ICommand 后任务异常不会再回到 UI 线程，必须在这里显式落到状态栏。
    /// </summary>
    private async Task RunSafelyAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    private void RunSafely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    private void ReportError(Exception ex)
    {
        Message = UiErrorFormatter.Describe(ex);
    }

    // ---------------------------------------------------------------- 退出

    /// <summary>真正退出时的清理：停推送、停内核、按需关闭系统代理。</summary>
    public void PrepareForExit()
    {
        StopFallbackPolling();
        _telemetry.Stop();

        if (_systemProxyEngaged)
        {
            try
            {
                _systemProxyService.Disable();
            }
            catch (Exception ex)
            {
                Logs.Append($"关闭系统代理失败: {ex.Message}");
            }
            finally
            {
                _systemProxyEngaged = false;
            }
        }

        _processManager.Stop();
    }

    public void Dispose()
    {
        _preferenceSaveCancellation?.Cancel();
        _preferenceSaveCancellation?.Dispose();
        StopFallbackPolling();
        _telemetry.Dispose();
        _apiClient.Dispose();
    }
}
