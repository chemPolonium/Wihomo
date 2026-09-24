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

    private AppSettings _settings = new();

    private CancellationTokenSource? _fallbackCancellation;
    private ConnectionsFrame? _previousPoll;
    private DateTimeOffset _previousPollAt;
    private bool _pushHealthy;

    [ObservableProperty] private string _message = "就绪";
    [ObservableProperty] private string _coreStatusText = "内核状态: 未启动";
    [ObservableProperty] private bool _isCoreRunning;
    [ObservableProperty] private bool _hasUnsavedChanges;

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
        Settings.PropertyChanged += (_, _) => HasUnsavedChanges = Settings.IsDirty;
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
            _windowsStartupService.SetEnabled(true);
        }

        if (settings.StartCoreOnProgramStart)
        {
            await StartCoreAsync();
        }
    }

    /// <summary>托盘与窗口共享同一份状态文本，避免同一个概念散落在三处写入。</summary>
    public string TrayText => $"Wihomo - {CoreStatusText}";

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

        if (_settings.Core.EnableSystemProxy)
        {
            _systemProxyService.Disable();
            if (!quiet)
            {
                Logs.Append("系统代理已关闭。");
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
            _settings.Core.MixedPort,
            _settings.Core.EnableTun,
            Settings.SelectedTunStack.Value);
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
    private Task SaveSettingsAsync() => RunSafelyAsync(() => SaveInternalAsync(generateConfig: false));

    [RelayCommand]
    private Task SaveAllAsync() => RunSafelyAsync(() => SaveInternalAsync(generateConfig: true));

    private async Task SaveInternalAsync(bool generateConfig)
    {
        var candidate = CloneSettings();
        Settings.ApplyTo(candidate);
        candidate.Subscriptions = Subscriptions.Items.Select(x => x.Item).ToList();
        await _settingsService.SaveAsync(candidate);

        _settings = candidate;
        _windowsStartupService.SetEnabled(candidate.StartWithWindows);
        RefreshRuntimeIndicators();

        if (!generateConfig)
        {
            Message = "设置已保存。";
            return;
        }

        WriteMihomoConfigFile(candidate);
        Message = "设置已保存，并已生成 mihomo 配置文件。";
    }

    /// <summary>复制当前已保存设置，作为界面编辑的落点；订阅列表沿用同一批对象。</summary>
    private AppSettings CloneSettings()
    {
        return new AppSettings
        {
            Core = new CoreRuntimeSettings
            {
                CoreExecutablePath = _settings.Core.CoreExecutablePath,
                WorkingDirectory = _settings.Core.WorkingDirectory,
                ExternalControllerHost = _settings.Core.ExternalControllerHost,
                ExternalControllerPort = _settings.Core.ExternalControllerPort,
                Secret = _settings.Core.Secret,
                MixedPort = _settings.Core.MixedPort,
                SocksPort = _settings.Core.SocksPort,
                HttpPort = _settings.Core.HttpPort,
                EnableSystemProxy = _settings.Core.EnableSystemProxy,
                EnableTun = _settings.Core.EnableTun,
                TunStack = _settings.Core.TunStack,
                BypassLocalNetworks = _settings.Core.BypassLocalNetworks,
            },
            Subscriptions = _settings.Subscriptions,
            ActiveSubscriptionName = _settings.ActiveSubscriptionName,
            SubscriptionRules = _settings.SubscriptionRules,
            GeoxUrls = _settings.GeoxUrls,
            Dns = _settings.Dns,
            RuleOverrides = _settings.RuleOverrides,
            StatsRefreshSeconds = _settings.StatsRefreshSeconds,
            StartCoreOnProgramStart = _settings.StartCoreOnProgramStart,
            StartWithWindows = _settings.StartWithWindows,
            GeoDataMode = _settings.GeoDataMode,
            GeoAutoUpdate = _settings.GeoAutoUpdate,
            GeoUpdateIntervalHours = _settings.GeoUpdateIntervalHours,
        };
    }

    private string WriteMihomoConfigFile(AppSettings settings, HashSet<string>? localProviders = null)
    {
        Directory.CreateDirectory(settings.Core.WorkingDirectory);
        var path = Path.Combine(settings.Core.WorkingDirectory, "config.yaml");
        File.WriteAllText(path, BuildEffectiveConfig(settings, localProviders), new UTF8Encoding(false));
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

        await _settingsService.SaveAsync(_settings);
        Subscriptions.Load(_settings);

        var subscription = _settings.Subscriptions.First(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        await DownloadAndSaveSubscriptionAsync(subscription);

        if (!isActive)
        {
            return;
        }

        File.Copy(GetSubscriptionFilePath(name), GetActiveSubscriptionPath(_settings), overwrite: true);
        if (_processManager.IsRunning)
        {
            StopCoreCore(quiet: false);
            Logs.Append("内核已停止，准备使用新订阅文件重启。");
        }

        await StartInternalAsync(new HashSet<string> { MihomoConfigBuilder.NormalizeName(name) });
        Message = "已下载订阅文件并已重载内核。";
    }

    [RelayCommand]
    private Task RemoveSubscriptionAsync() => RunSafelyAsync(RemoveSubscriptionAsyncInternal);

    private async Task RemoveSubscriptionAsyncInternal()
    {
        var selected = Subscriptions.SelectedSubscription
            ?? throw new InvalidOperationException("请先选择要删除的订阅。");

        _settings.Subscriptions.Remove(selected.Item);
        if (string.Equals(_settings.ActiveSubscriptionName, selected.Name, StringComparison.OrdinalIgnoreCase))
        {
            _settings.ActiveSubscriptionName = string.Empty;
        }

        await _settingsService.SaveAsync(_settings);
        Subscriptions.Load(_settings);
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
        selected.Item.Enabled = true;
        _settings.ActiveSubscriptionName = selected.Name;
        await _settingsService.SaveAsync(_settings);
        Subscriptions.Load(_settings);
        Logs.Append($"已激活订阅: {selected.Name}");

        File.Copy(subscriptionFile, GetActiveSubscriptionPath(_settings), overwrite: true);

        var content = ReadText(subscriptionFile);
        _settings.SubscriptionRules = SubscriptionParser.Parse(content).Rules;
        Rules.SetSubscriptionRules(_settings.SubscriptionRules);

        if (_processManager.IsRunning)
        {
            StopCoreCore(quiet: false);
            Logs.Append("内核已停止，准备使用新订阅重启。");
        }

        await StartInternalAsync(new HashSet<string> { MihomoConfigBuilder.NormalizeName(selected.Name) });
        Message = $"已激活订阅: {selected.Name}";
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

    [RelayCommand]
    private Task ApplyProxyAsync() => RunSafelyAsync(ApplyProxyAsyncInternal);

    private async Task ApplyProxyAsyncInternal()
    {
        EnsureCoreRunning();
        var group = ProxyGroups.SelectedGroup ?? throw new InvalidOperationException("请先选择代理组。");
        var proxyName = ProxyGroups.SelectedMember?.NodeName;

        if (string.IsNullOrWhiteSpace(proxyName))
        {
            throw new InvalidOperationException("请先选择节点。");
        }

        ConfigureApiClient();
        await _apiClient.SelectProxyAsync(group.Name, proxyName);
        Logs.Append($"代理组 {group.Name} 已切换到 {proxyName}");
        await RefreshProxyGroupsCoreAsync();
        Message = "代理组已切换。";
    }

    [RelayCommand]
    private Task TestDelayAsync() => RunSafelyAsync(TestDelayAsyncInternal);

    private async Task TestDelayAsyncInternal()
    {
        EnsureCoreRunning();
        var proxyName = ProxyGroups.SelectedMember?.NodeName
            ?? throw new InvalidOperationException("请先选择要测试的节点。");

        var (url, timeout) = ReadDelayTestArguments();
        ConfigureApiClient();
        var delay = await _apiClient.TestProxyDelayAsync(proxyName, url, timeout);
        _delayResults[proxyName] = delay.HasValue ? $"{delay.Value} ms" : "失败";
        ProxyGroups.RefreshDelays(NameOfDelay);

        var message = delay.HasValue ? $"{proxyName} 延迟: {delay.Value} ms" : $"{proxyName} 延迟测试失败";
        Logs.Append(message);
        Message = message;
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

        ProxyGroups.AllDelayStatusText = $"正在测试 {proxyNames.Count} 个节点...";

        using var concurrency = new SemaphoreSlim(4);
        var results = await Task.WhenAll(proxyNames.Select(async name =>
        {
            await concurrency.WaitAsync();
            try
            {
                var delay = await _apiClient.TestProxyDelayAsync(name, url, timeout);
                return (name, delay);
            }
            catch (HttpRequestException)
            {
                return (name, (int?)null);
            }
            catch (TaskCanceledException)
            {
                return (name, (int?)null);
            }
            finally
            {
                concurrency.Release();
            }
        }));

        foreach (var (name, delay) in results)
        {
            _delayResults[name] = delay.HasValue ? $"{delay.Value} ms" : "失败";
        }

        ProxyGroups.RefreshDelays(NameOfDelay);
        var usable = results.Count(x => x.Item2.HasValue);
        ProxyGroups.AllDelayStatusText = $"测速完成: {usable}/{results.Length} 个节点可用";
        Message = $"全部节点测速完成: {usable}/{results.Length} 个节点可用。";
    }

    private (string Url, int Timeout) ReadDelayTestArguments()
    {
        var url = ProxyGroups.DelayTestUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("测试 URL 无效。");
        }

        return (url, SettingsParsing.ParsePositiveInt(ProxyGroups.DelayTestTimeoutText, "超时(ms)"));
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
            settings.Core.WorkingDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wihomo", "runtime");
        }

        if (settings.Core.MixedPort <= 0)
        {
            settings.Core.MixedPort = 8090;
        }

        if (settings.Core.SocksPort <= 0)
        {
            settings.Core.SocksPort = 8091;
        }

        if (settings.Core.HttpPort <= 0)
        {
            settings.Core.HttpPort = 8092;
        }

        if (string.IsNullOrWhiteSpace(settings.Core.TunStack))
        {
            settings.Core.TunStack = "mixed";
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
            if (File.Exists(sourcePath) && !File.Exists(destinationPath))
            {
                File.Copy(sourcePath, destinationPath, overwrite: false);
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

        if (_settings.Core.EnableSystemProxy)
        {
            try
            {
                _systemProxyService.Disable();
            }
            catch (Exception ex)
            {
                Logs.Append($"关闭系统代理失败: {ex.Message}");
            }
        }

        _processManager.Stop();
    }

    public void Dispose()
    {
        StopFallbackPolling();
        _telemetry.Dispose();
        _apiClient.Dispose();
    }
}
