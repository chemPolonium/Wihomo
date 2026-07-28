using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Wihomo.Models;
using Wihomo.Services;
using YamlDotNet.Core;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Wihomo;

public partial class MainWindow : Window
{
    private const int MaxLogLines = 500;

    private readonly SettingsService _settingsService = new();
    private readonly MihomoConfigBuilder _configBuilder = new();
    private readonly SubscriptionConfigComposer _subscriptionConfigComposer = new();
    private readonly MihomoProcessManager _processManager = new();
    private readonly MihomoApiClient _apiClient = new();
    private readonly SystemProxyService _systemProxyService = new();
    private readonly WindowsStartupService _windowsStartupService = new();
    private readonly DispatcherTimer _statsTimer = new();
    private readonly DispatcherTimer _connectionsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly SemaphoreSlim _connectionRefreshLock = new(1, 1);
    private readonly List<string> _logLines = [];
    private readonly Drawing.Icon _applicationIcon;
    private readonly Forms.NotifyIcon _notifyIcon;

    private AppSettings _settings = new();
    private ConnectionStats? _previousStats;
    private DateTimeOffset? _previousStatsTimestamp;
    private List<ProxyGroupInfo> _proxyGroups = [];
    private readonly Dictionary<string, string> _proxyDelayResults = new(StringComparer.Ordinal);
    private bool _isExiting;

    public MainWindow()
    {
        InitializeComponent();
        _applicationIcon = LoadApplicationIcon();
        _notifyIcon = CreateNotifyIcon(_applicationIcon);
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;

        _statsTimer.Tick += StatsTimer_Tick;
        _connectionsTimer.Tick += ConnectionsTimer_Tick;
        _processManager.OutputReceived += ProcessManager_OutputReceived;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await ExecuteUiActionAsync(async () =>
        {
            _settings = await _settingsService.LoadAsync();
            ApplyDefaults();
            BindSettingsToUi();
            ResetStatsTimer();
            SetMessage($"设置已加载: {_settingsService.SettingsPath}");

            if (_settings.StartCoreOnProgramStart)
            {
                await StartCoreAsync();
            }
        });
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isExiting)
        {
            e.Cancel = true;
            HideToNotificationArea();
            return;
        }

        _notifyIcon.Dispose();
        _applicationIcon.Dispose();

        if (_settings.Core.EnableSystemProxy)
        {
            ExecuteUiAction(() => _systemProxyService.Disable());
        }

        _statsTimer.Stop();
        _connectionsTimer.Stop();
        _processManager.Stop();
    }

    private static Drawing.Icon LoadApplicationIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Wihomo.ico");
        return new Drawing.Icon(iconPath);
    }

    private Forms.NotifyIcon CreateNotifyIcon(Drawing.Icon applicationIcon)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => ShowMainWindow());
        menu.Items.Add("启动/重启内核", null, (_, _) => StartCoreFromNotificationArea());
        menu.Items.Add("停止内核", null, (_, _) => StopCoreFromNotificationArea());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());

        var notifyIcon = new Forms.NotifyIcon
        {
            Icon = applicationIcon,
            Text = "Wihomo - 内核状态: 已停止",
            ContextMenuStrip = menu,
            Visible = true
        };
        notifyIcon.DoubleClick += (_, _) => ShowMainWindow();
        return notifyIcon;
    }

    private void HideToNotificationArea()
    {
        ShowInTaskbar = false;
        Hide();
    }

    private void ShowMainWindow()
    {
        Dispatcher.InvokeAsync(RestoreAndActivate);
    }

    internal void RestoreAndActivate()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void StartCoreFromNotificationArea()
    {
        Dispatcher.InvokeAsync(() => ExecuteUiActionAsync(StartOrRestartCoreAsync));
    }

    private void StopCoreFromNotificationArea()
    {
        Dispatcher.InvokeAsync(() => StopCoreButton_Click(this, new RoutedEventArgs()));
    }

    private void ExitApplication()
    {
        Dispatcher.InvokeAsync(() =>
        {
            _isExiting = true;
            Close();
        });
    }

    private async void SaveSettingsOnlyButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteUiActionAsync(() => SaveSettingsAsync(generateConfig: false));
    }

    private async void SaveAllButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteUiActionAsync(() => SaveSettingsAsync(generateConfig: true));
    }

    private async Task SaveSettingsAsync(bool generateConfig)
    {
        var collected = CollectSettingsFromUi();
        _settings = collected;
        await _settingsService.SaveAsync(_settings);
        _windowsStartupService.SetEnabled(_settings.StartWithWindows);
        ResetStatsTimer();
        UpdateRuntimeStatusIndicators();

        if (generateConfig)
        {
            WriteMihomoConfigFile(_settings);
            SetMessage("设置已保存，并已生成 mihomo 配置文件。");
            return;
        }

        SetMessage("设置已保存。");
    }

    private async void StartCoreButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteUiActionAsync(async () =>
        {
            await StartOrRestartCoreAsync();
        });
    }

    private void StopCoreButton_Click(object sender, RoutedEventArgs e)
    {
        ExecuteUiAction(() =>
        {
            _statsTimer.Stop();
            _connectionsTimer.Stop();

            if (_settings.Core.EnableSystemProxy)
            {
                _systemProxyService.Disable();
                AppendLogLine("系统代理已关闭。");
            }

            _processManager.Stop();
            SetCoreStatus("已停止");
            VersionTextBlock.Text = "内核版本: -";
            LoadCachedProxyGroupsView();
            RefreshRulesAndConnectionsViewWithoutCore();
            UpdateRuntimeStatusIndicators();
            SetMessage("内核已停止。");
        });
    }

    private void AddOrUpdateSubscriptionButton_Click(object sender, RoutedEventArgs e)
    {
        ExecuteUiAction(() =>
        {
            var name = SubscriptionNameTextBox.Text.Trim();
            var url = SubscriptionUrlTextBox.Text.Trim();
            var interval = ParsePositiveInt(SubscriptionIntervalTextBox.Text, "更新间隔(秒)");
            var enabled = SubscriptionEnabledCheckBox.IsChecked ?? true;

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("订阅名称不能为空。");
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException("订阅 URL 无效。");
            }

            var existing = _settings.Subscriptions.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                _settings.Subscriptions.Add(new SubscriptionItem
                {
                    Name = name,
                    Url = url,
                    IntervalSeconds = interval,
                    Enabled = enabled
                });
                SetMessage("已新增订阅。");
            }
            else
            {
                existing.Url = url;
                existing.IntervalSeconds = interval;
                existing.Enabled = enabled;
                SetMessage("已更新订阅。");
            }

            RefreshSubscriptionsList();
        });
    }

    private void RemoveSubscriptionButton_Click(object sender, RoutedEventArgs e)
    {
        ExecuteUiAction(() =>
        {
            if (SubscriptionsListBox.SelectedIndex < 0 || SubscriptionsListBox.SelectedIndex >= _settings.Subscriptions.Count)
            {
                throw new InvalidOperationException("请先选择要删除的订阅。");
            }

            _settings.Subscriptions.RemoveAt(SubscriptionsListBox.SelectedIndex);
            RefreshSubscriptionsList();
            SetMessage("订阅已删除。");
        });
    }

    private void SubscriptionsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SubscriptionsListBox.SelectedIndex < 0 || SubscriptionsListBox.SelectedIndex >= _settings.Subscriptions.Count)
        {
            return;
        }

        var selected = _settings.Subscriptions[SubscriptionsListBox.SelectedIndex];
        SubscriptionNameTextBox.Text = selected.Name;
        SubscriptionUrlTextBox.Text = selected.Url;
        SubscriptionIntervalTextBox.Text = selected.IntervalSeconds.ToString(CultureInfo.InvariantCulture);
        SubscriptionEnabledCheckBox.IsChecked = selected.Enabled;
    }

    private async void UpdateSelectedProviderButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteUiActionAsync(UpdateSelectedSubscriptionAsync);
    }

    private async void SubscriptionsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        await ExecuteUiActionAsync(UpdateSelectedSubscriptionAsync);
    }

    private async Task UpdateSelectedSubscriptionAsync()
    {
        if (SubscriptionsListBox.SelectedIndex < 0 || SubscriptionsListBox.SelectedIndex >= _settings.Subscriptions.Count)
        {
            throw new InvalidOperationException("请先选择订阅。");
        }

        var selected = _settings.Subscriptions[SubscriptionsListBox.SelectedIndex];
        var providerKey = MihomoConfigBuilder.NormalizeName(selected.Name);
        await DownloadSubscriptionAndReloadAsync(selected, providerKey);
    }

    private async void RefreshProxyGroupsButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteUiActionAsync(RefreshProxyGroupsAsync);
    }

    private async void RefreshRulesAndConnectionsButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteUiActionAsync(RefreshRulesAndConnectionsAsync);
    }

    private void ProxyGroupsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProxyGroupsListBox.SelectedIndex < 0 || ProxyGroupsListBox.SelectedIndex >= _proxyGroups.Count)
        {
            return;
        }

        var group = _proxyGroups[ProxyGroupsListBox.SelectedIndex];
        SelectedGroupStatusTextBlock.Text = $"当前组: {group.Name} ({group.Type})";
        ProxyGroupCurrentTextBlock.Text = $"当前选择: {group.Current}";
        BindProxyGroupMembers(group);
    }

    private async void ApplySelectedProxyButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteUiActionAsync(async () =>
        {
            EnsureCoreRunning();
            var group = GetSelectedProxyGroup();
            var proxyName = (ProxyGroupMembersDataGrid.SelectedItem as ProxyMemberRow)?.NodeName;

            if (string.IsNullOrWhiteSpace(proxyName))
            {
                throw new InvalidOperationException("请先选择节点。");
            }

            ConfigureApiClient();
            await _apiClient.SelectProxyAsync(group.Name, proxyName);
            AppendLogLine($"代理组 {group.Name} 已切换到 {proxyName}");
            await RefreshProxyGroupsAsync();
            SetMessage("代理组已切换。");
        });
    }

    private async void TestProxyDelayButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteUiActionAsync(async () =>
        {
            EnsureCoreRunning();
            var proxyName = (ProxyGroupMembersDataGrid.SelectedItem as ProxyMemberRow)?.NodeName;
            if (string.IsNullOrWhiteSpace(proxyName))
            {
                throw new InvalidOperationException("请先选择要测试的节点。");
            }

            var url = DelayTestUrlTextBox.Text.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException("测试 URL 无效。");
            }

            var timeout = ParsePositiveInt(DelayTestTimeoutTextBox.Text, "超时(ms)");
            ConfigureApiClient();
            var delay = await _apiClient.TestProxyDelayAsync(proxyName, url, timeout);
            _proxyDelayResults[proxyName] = delay.HasValue ? $"{delay.Value} ms" : "失败";
            RefreshSelectedProxyGroupMembers(proxyName);
            var message = delay.HasValue ? $"{proxyName} 延迟: {delay.Value} ms" : $"{proxyName} 延迟测试失败";
            AppendLogLine(message);
            SetMessage(message);
        });
    }

    private async void TestAllProxyDelaysButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteUiActionAsync(async () =>
        {
            EnsureCoreRunning();

            var url = DelayTestUrlTextBox.Text.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException("测试 URL 无效。");
            }

            var timeout = ParsePositiveInt(DelayTestTimeoutTextBox.Text, "超时(ms)");
            ConfigureApiClient();
            var proxyNames = await _apiClient.GetTestableProxyNamesAsync();
            if (proxyNames.Count == 0)
            {
                throw new InvalidOperationException("未找到可测速的代理节点。");
            }

            AllDelayTestStatusTextBlock.Text = $"正在测试 {proxyNames.Count} 个节点...";

            using var concurrency = new SemaphoreSlim(4);
            var tests = proxyNames.Select(proxyName => TestProxyDelayAsync(proxyName, url, timeout, concurrency));
            var results = await Task.WhenAll(tests);

            foreach (var result in results)
            {
                _proxyDelayResults[result.ProxyName] = result.DelayMilliseconds.HasValue
                    ? $"{result.DelayMilliseconds.Value} ms"
                    : "失败";
            }

            RefreshSelectedProxyGroupMembers();
            var successfulCount = results.Count(x => x.DelayMilliseconds.HasValue);
            AllDelayTestStatusTextBlock.Text = $"测速完成: {successfulCount}/{results.Length} 个节点可用";
            SetMessage($"全部节点测速完成: {successfulCount}/{results.Length} 个节点可用。");
        });
    }

    private async Task<ProxyDelayTestResult> TestProxyDelayAsync(
        string proxyName,
        string url,
        int timeoutMilliseconds,
        SemaphoreSlim concurrency)
    {
        await concurrency.WaitAsync();
        try
        {
            var delay = await _apiClient.TestProxyDelayAsync(proxyName, url, timeoutMilliseconds);
            return new ProxyDelayTestResult(proxyName, delay, delay.HasValue ? null : "未返回延迟");
        }
        catch (HttpRequestException ex)
        {
            return new ProxyDelayTestResult(proxyName, null, ex.Message);
        }
        catch (TaskCanceledException)
        {
            return new ProxyDelayTestResult(proxyName, null, "请求超时");
        }
        finally
        {
            concurrency.Release();
        }
    }

    private void ClearLogsButton_Click(object sender, RoutedEventArgs e)
    {
        ExecuteUiAction(() =>
        {
            _logLines.Clear();
            LogsTextBox.Clear();
            SetMessage("日志已清空。");
        });
    }

    private async void StatsTimer_Tick(object? sender, EventArgs e)
    {
        await ExecuteUiActionAsync(async () =>
        {
            if (!_processManager.IsRunning)
            {
                RefreshRulesAndConnectionsViewWithoutCore();
                return;
            }

            await RefreshStatsAsync();
        });
    }

    private async void ConnectionsTimer_Tick(object? sender, EventArgs e)
    {
        await ExecuteUiActionAsync(async () =>
        {
            if (_processManager.IsRunning)
            {
                await RefreshConnectionsAsync();
            }
        });
    }

    private void ProcessManager_OutputReceived(string line)
    {
        Dispatcher.Invoke(() => AppendLogLine(line));
    }

    private async Task RefreshStatsAsync()
    {
        ConfigureApiClient();

        var capturedAt = DateTimeOffset.UtcNow;
        var stats = await _apiClient.GetConnectionStatsAsync(_previousStats, _previousStatsTimestamp);
        _previousStats = stats;
        _previousStatsTimestamp = capturedAt;

        ConnectionsTextBlock.Text = $"当前连接数: {stats.ActiveConnections}";
        DownloadTotalTextBlock.Text = $"累计下载: {FormatBytes(stats.DownloadTotal)}";
        UploadTotalTextBlock.Text = $"累计上传: {FormatBytes(stats.UploadTotal)}";
        DownloadRateTextBlock.Text = $"下载速率: {FormatBytes((long)stats.DownloadBytesPerSecond)}/s";
        UploadRateTextBlock.Text = $"上传速率: {FormatBytes((long)stats.UploadBytesPerSecond)}/s";
    }

    private async Task RefreshRulesAndConnectionsAsync()
    {
        ConfigureApiClient();
        RefreshSubscriptionRulesView();

        try
        {
            var rules = await _apiClient.GetRulesAsync();
            ActiveRulesListBox.ItemsSource = rules.Count > 0
                ? rules.Select(x => x).ToList()
                : new List<string> { "暂无当前规则" };
        }
        catch (Exception ex)
        {
            ActiveRulesListBox.ItemsSource = new List<string> { $"读取规则失败: {ex.Message}" };
        }

        await RefreshConnectionsAsync();
    }

    private async Task RefreshConnectionsAsync()
    {
        if (!await _connectionRefreshLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            ConfigureApiClient();
            var connections = await _apiClient.GetConnectionsAsync();
            ConnectionsDataGrid.ItemsSource = connections.Count > 0
                ? connections
                : new List<ConnectionInfo> { CreateConnectionMessage("暂无当前连接") };
        }
        catch (HttpRequestException ex)
        {
            ConnectionsDataGrid.ItemsSource = new List<ConnectionInfo> { CreateConnectionMessage($"读取连接失败: {ex.Message}") };
        }
        finally
        {
            _connectionRefreshLock.Release();
        }
    }

    private void RefreshRulesAndConnectionsViewWithoutCore()
    {
        RefreshSubscriptionRulesView();
        ActiveRulesListBox.ItemsSource = new List<string> { "内核未运行" };
        ConnectionsDataGrid.ItemsSource = new List<ConnectionInfo> { CreateConnectionMessage("内核未运行") };
    }

    private static ConnectionInfo CreateConnectionMessage(string message)
    {
        return new ConnectionInfo { Source = message };
    }

    private void RefreshSubscriptionRulesView()
    {
        var rules = _settings.SubscriptionRules
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        SubscriptionRulesListBox.ItemsSource = rules.Count > 0
            ? rules
            : new List<string> { "暂无订阅规则" };
    }

    private async Task RefreshProxyGroupsAsync()
    {
        EnsureCoreRunning();
        ConfigureApiClient();
        var selectedIndex = ProxyGroupsListBox.SelectedIndex;
        var selectedGroupName = selectedIndex >= 0 && selectedIndex < _proxyGroups.Count
            ? _proxyGroups[selectedIndex].Name
            : null;

        _proxyGroups = OrderProxyGroupsBySubscription(await _apiClient.GetProxyGroupsAsync());
        ProxyGroupsListBox.ItemsSource = null;
        ProxyGroupsListBox.ItemsSource = _proxyGroups
            .Select(x => $"{x.Name} [{x.Type}] -> {x.Current}")
            .ToList();

        if (_proxyGroups.Count > 0)
        {
            var restoredIndex = string.IsNullOrWhiteSpace(selectedGroupName)
                ? -1
                : _proxyGroups.FindIndex(x => string.Equals(x.Name, selectedGroupName, StringComparison.Ordinal));
            ProxyGroupsListBox.SelectedIndex = restoredIndex >= 0 ? restoredIndex : 0;
        }

        SetMessage($"已刷新 {_proxyGroups.Count} 个代理组。");
    }

    private List<ProxyGroupInfo> OrderProxyGroupsBySubscription(List<ProxyGroupInfo> groups)
    {
        var templatePath = GetSubscriptionTemplatePath(_settings);
        if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath))
        {
            return groups;
        }

        var subscriptionText = File.ReadAllText(templatePath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var order = _subscriptionConfigComposer.GetProxyGroupOrder(subscriptionText);
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

    private void ClearProxyGroupsView()
    {
        _proxyGroups = [];
        ProxyGroupsListBox.ItemsSource = null;
        ProxyGroupMembersDataGrid.ItemsSource = null;
        SelectedGroupStatusTextBlock.Text = "当前组: -";
        ProxyGroupCurrentTextBlock.Text = "当前选择: -";
    }

    private void BindProxyGroupMembers(ProxyGroupInfo group, string? selectedProxyName = null)
    {
        var rows = group.Options
            .Select(name => new ProxyMemberRow(
                name,
                _proxyDelayResults.TryGetValue(name, out var delay) ? delay : "未测试"))
            .ToList();

        ProxyGroupMembersDataGrid.ItemsSource = rows;
        var selection = selectedProxyName ?? group.Current;
        ProxyGroupMembersDataGrid.SelectedItem = rows.FirstOrDefault(x =>
            string.Equals(x.NodeName, selection, StringComparison.Ordinal));
    }

    private void RefreshSelectedProxyGroupMembers(string? selectedProxyName = null)
    {
        var selectedIndex = ProxyGroupsListBox.SelectedIndex;
        if (selectedIndex >= 0 && selectedIndex < _proxyGroups.Count)
        {
            BindProxyGroupMembers(_proxyGroups[selectedIndex], selectedProxyName);
        }
    }

    private void LoadCachedProxyGroupsView()
    {
        var templatePath = GetSubscriptionTemplatePath(_settings);
        if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath))
        {
            ClearProxyGroupsView();
            return;
        }

        var subscriptionText = File.ReadAllText(templatePath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _proxyGroups = _subscriptionConfigComposer.GetProxyGroups(subscriptionText).ToList();
        ProxyGroupsListBox.ItemsSource = null;
        ProxyGroupsListBox.ItemsSource = _proxyGroups
            .Select(x => $"{x.Name} [{x.Type}]")
            .ToList();

        if (_proxyGroups.Count > 0)
        {
            ProxyGroupsListBox.SelectedIndex = 0;
            SelectedGroupStatusTextBlock.Text = $"订阅配置: {_proxyGroups.Count} 个代理组";
        }
        else
        {
            ProxyGroupMembersDataGrid.ItemsSource = null;
            SelectedGroupStatusTextBlock.Text = "订阅中未定义代理组";
            ProxyGroupCurrentTextBlock.Text = "当前选择: -";
        }
    }

    private void ApplyDefaults()
    {
        var appBaseDir = AppContext.BaseDirectory;
        var repoRoot = Directory.GetParent(appBaseDir)?.Parent?.Parent?.Parent?.FullName ?? appBaseDir;
        var bundledCorePath = Path.Combine(appBaseDir, "assets", "core", "mihomo-windows-amd64-v3.exe");
        var sourceBundledCorePath = Path.Combine(repoRoot, "assets", "core", "mihomo-windows-amd64-v3.exe");
        var legacyCorePath = Path.Combine(repoRoot, "mihomo-windows-amd64-v3.exe");

        if (string.IsNullOrWhiteSpace(_settings.Core.CoreExecutablePath))
        {
            _settings.Core.CoreExecutablePath = File.Exists(bundledCorePath)
                ? bundledCorePath
                : File.Exists(sourceBundledCorePath)
                    ? sourceBundledCorePath
                    : legacyCorePath;
        }
        else if (!File.Exists(_settings.Core.CoreExecutablePath) && File.Exists(bundledCorePath))
        {
            _settings.Core.CoreExecutablePath = bundledCorePath;
        }

        if (string.IsNullOrWhiteSpace(_settings.Core.WorkingDirectory))
        {
            _settings.Core.WorkingDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Wihomo",
                "runtime");
        }

        if (_settings.Core.MixedPort <= 0)
        {
            _settings.Core.MixedPort = 8090;
        }

        if (_settings.Core.SocksPort <= 0)
        {
            _settings.Core.SocksPort = 8091;
        }

        if (_settings.Core.HttpPort <= 0)
        {
            _settings.Core.HttpPort = 8092;
        }

        if (string.IsNullOrWhiteSpace(_settings.Core.TunStack))
        {
            _settings.Core.TunStack = "mixed";
        }

        if (_settings.StatsRefreshSeconds <= 0)
        {
            _settings.StatsRefreshSeconds = 2;
        }

        _settings.GeoxUrls ??= new GeoxUrlSettings();
        EnsureDefaultExternalResourceUrls(_settings.GeoxUrls);

        if (_settings.GeoUpdateIntervalHours <= 0)
        {
            _settings.GeoUpdateIntervalHours = 24;
        }
    }

    private void BindSettingsToUi()
    {
        CorePathTextBox.Text = _settings.Core.CoreExecutablePath;
        CoreWorkDirTextBox.Text = _settings.Core.WorkingDirectory;
        ControllerHostTextBox.Text = _settings.Core.ExternalControllerHost;
        ControllerPortTextBox.Text = _settings.Core.ExternalControllerPort.ToString(CultureInfo.InvariantCulture);
        MixedPortTextBox.Text = _settings.Core.MixedPort.ToString(CultureInfo.InvariantCulture);
        SocksPortTextBox.Text = _settings.Core.SocksPort.ToString(CultureInfo.InvariantCulture);
        HttpPortTextBox.Text = _settings.Core.HttpPort.ToString(CultureInfo.InvariantCulture);
        SecretTextBox.Text = _settings.Core.Secret;
        StatsRefreshTextBox.Text = _settings.StatsRefreshSeconds.ToString(CultureInfo.InvariantCulture);
        EnableSystemProxyCheckBox.IsChecked = _settings.Core.EnableSystemProxy;
        EnableTunCheckBox.IsChecked = _settings.Core.EnableTun;
        StartCoreOnProgramStartCheckBox.IsChecked = _settings.StartCoreOnProgramStart;
        StartWithWindowsCheckBox.IsChecked = _settings.StartWithWindows;
        GeoIpUrlTextBox.Text = _settings.GeoxUrls.GeoIp;
        GeoSiteUrlTextBox.Text = _settings.GeoxUrls.GeoSite;
        MmdbUrlTextBox.Text = _settings.GeoxUrls.Mmdb;
        AsnUrlTextBox.Text = _settings.GeoxUrls.Asn;
        GeoAutoUpdateCheckBox.IsChecked = _settings.GeoAutoUpdate;
        GeoUpdateIntervalTextBox.Text = _settings.GeoUpdateIntervalHours.ToString(CultureInfo.InvariantCulture);
        SelectGeoDataMode(_settings.GeoDataMode);
        RuleOverridesTextBox.Text = _settings.RuleOverrides;
        SelectTunStack(_settings.Core.TunStack);
        RefreshSubscriptionsList();
        LoadCachedProxyGroupsView();
        RefreshSubscriptionRulesView();
        RefreshRulesAndConnectionsViewWithoutCore();
        UpdateRuntimeStatusIndicators();
    }

    private AppSettings CollectSettingsFromUi()
    {
        var corePath = CorePathTextBox.Text.Trim();
        var workDir = CoreWorkDirTextBox.Text.Trim();
        var host = ControllerHostTextBox.Text.Trim();
        var port = ParsePositiveInt(ControllerPortTextBox.Text, "External Controller Port");
        var mixedPort = ParsePositiveInt(MixedPortTextBox.Text, "Mixed Port");
        var socksPort = ParsePositiveInt(SocksPortTextBox.Text, "SOCKS Port");
        var httpPort = ParsePositiveInt(HttpPortTextBox.Text, "HTTP Port");
        var statsRefresh = ParsePositiveInt(StatsRefreshTextBox.Text, "统计刷新间隔(秒)");
        var geoUpdateInterval = ParsePositiveInt(GeoUpdateIntervalTextBox.Text, "GEO 更新间隔（小时）");

        if (string.IsNullOrWhiteSpace(corePath))
        {
            throw new InvalidOperationException("内核路径不能为空。");
        }

        if (string.IsNullOrWhiteSpace(workDir))
        {
            throw new InvalidOperationException("工作目录不能为空。");
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("External Controller Host 不能为空。");
        }

        return new AppSettings
        {
            Core = new CoreRuntimeSettings
            {
                CoreExecutablePath = corePath,
                WorkingDirectory = workDir,
                ExternalControllerHost = host,
                ExternalControllerPort = port,
                MixedPort = mixedPort,
                SocksPort = socksPort,
                HttpPort = httpPort,
                Secret = SecretTextBox.Text,
                EnableSystemProxy = EnableSystemProxyCheckBox.IsChecked ?? false,
                EnableTun = EnableTunCheckBox.IsChecked ?? false,
                TunStack = GetSelectedTunStack()
            },
            StatsRefreshSeconds = statsRefresh,
            StartCoreOnProgramStart = StartCoreOnProgramStartCheckBox.IsChecked ?? false,
            StartWithWindows = StartWithWindowsCheckBox.IsChecked ?? false,
            GeoDataMode = GetSelectedGeoDataMode(),
            GeoAutoUpdate = GeoAutoUpdateCheckBox.IsChecked ?? false,
            GeoUpdateIntervalHours = geoUpdateInterval,
            RuleOverrides = RuleOverridesTextBox.Text,
            ActiveSubscriptionName = _settings.ActiveSubscriptionName,
            SubscriptionRules = _settings.SubscriptionRules,
            GeoxUrls = new GeoxUrlSettings
            {
                GeoIp = ParseAbsoluteUrl(GeoIpUrlTextBox.Text, "GeoIP 数据库 URL"),
                GeoSite = ParseAbsoluteUrl(GeoSiteUrlTextBox.Text, "GeoSite 数据库 URL"),
                Mmdb = ParseAbsoluteUrl(MmdbUrlTextBox.Text, "MMDB 数据库 URL"),
                Asn = ParseAbsoluteUrl(AsnUrlTextBox.Text, "ASN 数据库 URL")
            },
            Subscriptions = _settings.Subscriptions
                .Select(x => new SubscriptionItem
                {
                    Name = x.Name,
                    Url = x.Url,
                    IntervalSeconds = x.IntervalSeconds,
                    Enabled = x.Enabled,
                    UploadBytes = x.UploadBytes,
                    DownloadBytes = x.DownloadBytes,
                    TotalBytes = x.TotalBytes,
                    ExpireAt = x.ExpireAt
                })
                .ToList()
        };
    }

    private async Task StartCoreAsync(HashSet<string>? localProviders = null)
    {
        if (_processManager.IsRunning)
        {
            throw new InvalidOperationException("内核已在运行中。");
        }

        await SaveSettingsAsync(generateConfig: false);
        EnsureBundledGeoDataFiles(_settings.Core.WorkingDirectory);
        var configPath = WriteMihomoConfigFile(_settings, localProviders);
        _processManager.Start(_settings.Core.CoreExecutablePath, _settings.Core.WorkingDirectory, configPath);
        ConfigureApiClient();

        SetCoreStatus("启动中");
        AppendLogLine("正在启动 mihomo 内核...");
        await Task.Delay(900);
        var version = await _apiClient.GetVersionAsync();
        VersionTextBlock.Text = $"内核版本: {version}";
        SetCoreStatus("运行中");

        if (_settings.Core.EnableSystemProxy)
        {
            _systemProxyService.Enable("127.0.0.1", _settings.Core.MixedPort);
            AppendLogLine($"系统代理已启用: 127.0.0.1:{_settings.Core.MixedPort}");
        }

        _previousStats = null;
        _previousStatsTimestamp = null;
        _statsTimer.Start();
        _connectionsTimer.Start();
        await RefreshStatsAsync();
        await RefreshRulesAndConnectionsAsync();
        await RefreshProxyGroupsAsync();
        UpdateRuntimeStatusIndicators();
        SetMessage("内核已启动。");
    }

    private async Task StartOrRestartCoreAsync()
    {
        if (_processManager.IsRunning)
        {
            _statsTimer.Stop();
            _connectionsTimer.Stop();

            if (_settings.Core.EnableSystemProxy)
            {
                _systemProxyService.Disable();
                AppendLogLine("系统代理已关闭，准备重启内核。");
            }

            _processManager.Stop();
            SetCoreStatus("已停止");
            VersionTextBlock.Text = "内核版本: -";
            AppendLogLine("正在重启 mihomo 内核...");
        }

        await StartCoreAsync();
    }

    private async Task DownloadSubscriptionAndReloadAsync(SubscriptionItem subscription, string providerKey)
    {
        var downloadPath = Path.Combine(_settings.Core.WorkingDirectory, "subscriptions", "active.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(downloadPath)!);

        AppendLogLine($"正在下载订阅文件: {subscription.Url}");
        var download = await _apiClient.DownloadSubscriptionAsync(subscription.Url);
        var content = download.Content;
        File.WriteAllText(downloadPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        AppendLogLine($"已保存订阅文件: {downloadPath}");

        ApplySubscriptionUsage(subscription, download.UserInfo);
        RefreshSubscriptionsList();
        _settings.ActiveSubscriptionName = subscription.Name;
        ParseSubscriptionRules(content);
        RefreshSubscriptionRulesView();

        var localProviders = new HashSet<string> { providerKey };
        if (_processManager.IsRunning)
        {
            _processManager.Stop();
            AppendLogLine("内核已停止，准备使用新订阅文件重启。" );
        }

        await StartCoreAsync(localProviders);
        SetMessage("已下载订阅文件并已重载内核。" );
    }

    private string WriteMihomoConfigFile(AppSettings settings, HashSet<string>? localProviders = null)
    {
        Directory.CreateDirectory(settings.Core.WorkingDirectory);
        var path = Path.Combine(settings.Core.WorkingDirectory, "config.yaml");
        var yaml = BuildEffectiveConfig(settings, localProviders);
        File.WriteAllText(path, yaml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private string BuildEffectiveConfig(AppSettings settings, HashSet<string>? localProviders)
    {
        var subscriptionTemplatePath = GetSubscriptionTemplatePath(settings);
        if (!string.IsNullOrWhiteSpace(subscriptionTemplatePath) && File.Exists(subscriptionTemplatePath))
        {
            var activeSubscriptionPath = GetActiveSubscriptionPath(settings);
            if (!string.Equals(subscriptionTemplatePath, activeSubscriptionPath, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(activeSubscriptionPath)!);
                File.Copy(subscriptionTemplatePath, activeSubscriptionPath, overwrite: true);
            }

            var subscriptionText = File.ReadAllText(subscriptionTemplatePath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return _subscriptionConfigComposer.Compose(subscriptionText, settings);
        }

        if (settings.Subscriptions.Any(x => x.Enabled))
        {
            throw new InvalidOperationException("找不到已下载的订阅配置，请先选择订阅并执行“更新选中订阅”。");
        }

        return _configBuilder.Build(settings, localProviders);
    }

    private static string GetActiveSubscriptionPath(AppSettings settings)
    {
        return Path.Combine(settings.Core.WorkingDirectory, "subscriptions", "active.yaml");
    }

    private static string? GetSubscriptionTemplatePath(AppSettings settings)
    {
        var activePath = GetActiveSubscriptionPath(settings);
        if (File.Exists(activePath))
        {
            return activePath;
        }

        var activeSubscription = settings.Subscriptions.FirstOrDefault(x =>
            x.Enabled && string.Equals(x.Name, settings.ActiveSubscriptionName, StringComparison.OrdinalIgnoreCase));
        var fallbackSubscription = activeSubscription ?? settings.Subscriptions.FirstOrDefault(x => x.Enabled);
        if (fallbackSubscription is null)
        {
            return null;
        }

        var legacyProviderPath = Path.Combine(
            settings.Core.WorkingDirectory,
            "proxy_providers",
            $"{MihomoConfigBuilder.NormalizeName(fallbackSubscription.Name)}.yaml");
        return File.Exists(legacyProviderPath) ? legacyProviderPath : null;
    }

    private void EnsureBundledGeoDataFiles(string workingDirectory)
    {
        var sourceDir = Path.Combine(AppContext.BaseDirectory, "assets", "geodata");
        if (!Directory.Exists(sourceDir))
        {
            return;
        }

        Directory.CreateDirectory(workingDirectory);
        foreach (var fileName in new[] { "geoip.dat", "geosite.dat", "country.mmdb", "GeoLite2-ASN.mmdb" })
        {
            var sourcePath = Path.Combine(sourceDir, fileName);
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            var destinationPath = Path.Combine(workingDirectory, fileName);
            if (!File.Exists(destinationPath))
            {
                File.Copy(sourcePath, destinationPath, overwrite: false);
            }
        }
    }

    private void ParseSubscriptionRules(string content)
    {
        var parsed = ParseSubscriptionContent(content);
        _settings.SubscriptionRules = parsed.Rules;
    }

    private SubscriptionContentParseResult ParseSubscriptionContent(string content)
    {
        var rules = new List<string>();
        if (string.IsNullOrWhiteSpace(content))
        {
            return new SubscriptionContentParseResult(rules);
        }

        var normalized = DecodeSubscriptionText(content);
        var lines = normalized.Replace("\r\n", "\n").Split('\n');
        var section = string.Empty;
        var sectionIndent = -1;

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            var indent = CountLeadingSpaces(rawLine);
            if (section.Length > 0 && indent <= sectionIndent && !trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                section = string.Empty;
                sectionIndent = -1;
            }

            if (string.Equals(trimmed, "rules:", StringComparison.OrdinalIgnoreCase))
            {
                section = "rules";
                sectionIndent = indent;
                continue;
            }

            if (section == "rules" && trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                var candidate = trimmed[2..].Trim();
                if (IsRuleCandidate(candidate))
                {
                    rules.Add(candidate);
                }
            }
        }

        return new SubscriptionContentParseResult(
            rules
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList());
    }

    private static bool IsRuleCandidate(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.Contains(',') || text.Contains("MATCH") || text.Contains("FINAL"))
        {
            return true;
        }

        return text.StartsWith("DOMAIN", StringComparison.Ordinal)
            || text.StartsWith("IP-CIDR", StringComparison.Ordinal)
            || text.StartsWith("SRC-IP-CIDR", StringComparison.Ordinal)
            || text.StartsWith("GEOIP", StringComparison.Ordinal)
            || text.StartsWith("GEOSITE", StringComparison.Ordinal)
            || text.StartsWith("PROCESS-NAME", StringComparison.Ordinal)
            || text.StartsWith("URL-REGEX", StringComparison.Ordinal)
            || text.StartsWith("RULE-SET", StringComparison.Ordinal)
            || text.StartsWith("AND", StringComparison.Ordinal)
            || text.StartsWith("OR", StringComparison.Ordinal)
            || text.StartsWith("NOT", StringComparison.Ordinal);
    }

    private static int CountLeadingSpaces(string text)
    {
        var index = 0;
        while (index < text.Length && text[index] == ' ')
        {
            index++;
        }

        return index;
    }

    private static string DecodeSubscriptionText(string content)
    {
        var normalized = content.Trim();
        if (LooksLikeBase64(normalized))
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            }
            catch
            {
                return content;
            }
        }

        return content;
    }

    private static bool LooksLikeBase64(string text)
    {
        if (text.Length < 20 || text.Any(ch => !char.IsLetterOrDigit(ch) && ch != '+' && ch != '/' && ch != '='))
        {
            return false;
        }

        return text.Length % 4 == 0;
    }

    private sealed record SubscriptionContentParseResult(List<string> Rules);

    private static void ApplySubscriptionUsage(SubscriptionItem subscription, string? userInfo)
    {
        if (string.IsNullOrWhiteSpace(userInfo))
        {
            return;
        }

        var values = userInfo
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);

        if (TryGetNonNegativeLong(values, "upload", out var upload))
        {
            subscription.UploadBytes = upload;
        }
        if (TryGetNonNegativeLong(values, "download", out var download))
        {
            subscription.DownloadBytes = download;
        }
        if (TryGetNonNegativeLong(values, "total", out var total))
        {
            subscription.TotalBytes = total;
        }
        if (TryGetNonNegativeLong(values, "expire", out var expire)
            && expire is >= 0 and <= 253402300799)
        {
            subscription.ExpireAt = DateTimeOffset.FromUnixTimeSeconds(expire).ToLocalTime();
        }
    }

    private static bool TryGetNonNegativeLong(
        IReadOnlyDictionary<string, string> values,
        string key,
        out long value)
    {
        value = 0;
        return values.TryGetValue(key, out var text)
            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            && value >= 0;
    }

    private void RefreshSubscriptionsList()
    {
        SubscriptionsListBox.ItemsSource = null;
        SubscriptionsListBox.ItemsSource = _settings.Subscriptions
            .Select(x => new SubscriptionRow(
                x.Enabled ? "启用" : "停用",
                x.Name,
                FormatBytes(x.UploadBytes + x.DownloadBytes),
                x.TotalBytes > 0 ? FormatBytes(x.TotalBytes) : "-",
                x.ExpireAt?.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture) ?? "-"))
            .ToList();
    }

    private void ResetStatsTimer()
    {
        _statsTimer.Stop();
        _statsTimer.Interval = TimeSpan.FromSeconds(Math.Max(_settings.StatsRefreshSeconds, 1));
    }

    private void UpdateRuntimeStatusIndicators()
    {
        SystemProxyStatusTextBlock.Text = _settings.Core.EnableSystemProxy
            ? $"系统代理: 已配置为启动时启用 ({_settings.Core.MixedPort})"
            : "系统代理: 未启用";
        TunStatusTextBlock.Text = _settings.Core.EnableTun
            ? $"TUN: 已启用 ({GetSelectedTunStackOrDefault()})"
            : "TUN: 未启用";
    }

    private void SetCoreStatus(string status)
    {
        CoreStatusTextBlock.Text = $"内核状态: {status}";
        HeaderCoreStatusTextBlock.Text = $"内核状态: {status}";
        _notifyIcon.Text = $"Wihomo - 内核状态: {status}";
    }

    private void ConfigureApiClient()
    {
        _apiClient.Configure(
            _settings.Core.ExternalControllerHost,
            _settings.Core.ExternalControllerPort,
            _settings.Core.Secret);
    }

    private ProxyGroupInfo GetSelectedProxyGroup()
    {
        if (ProxyGroupsListBox.SelectedIndex < 0 || ProxyGroupsListBox.SelectedIndex >= _proxyGroups.Count)
        {
            throw new InvalidOperationException("请先选择代理组。");
        }

        return _proxyGroups[ProxyGroupsListBox.SelectedIndex];
    }

    private void EnsureCoreRunning()
    {
        if (!_processManager.IsRunning)
        {
            throw new InvalidOperationException("请先启动内核。");
        }
    }

    private void SelectTunStack(string tunStack)
    {
        var normalized = tunStack switch
        {
            "system" => "system",
            "gvisor" => "gvisor",
            _ => "mixed"
        };

        for (var i = 0; i < TunStackComboBox.Items.Count; i++)
        {
            if (TunStackComboBox.Items[i] is ComboBoxItem item
                && string.Equals(item.Content?.ToString(), normalized, StringComparison.Ordinal))
            {
                TunStackComboBox.SelectedIndex = i;
                return;
            }
        }

        TunStackComboBox.SelectedIndex = 0;
    }

    private string GetSelectedTunStack()
    {
        return GetSelectedTunStackOrDefault();
    }

    private string GetSelectedTunStackOrDefault()
    {
        if (TunStackComboBox.SelectedItem is ComboBoxItem item && item.Content is string value && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return _settings.Core.TunStack switch
        {
            "system" => "system",
            "gvisor" => "gvisor",
            _ => "mixed"
        };
    }

    private void SelectGeoDataMode(bool useDat)
    {
        GeoDataModeComboBox.SelectedIndex = useDat ? 1 : 0;
    }

    private bool GetSelectedGeoDataMode()
    {
        return GeoDataModeComboBox.SelectedIndex == 1;
    }

    private static string ParseAbsoluteUrl(string text, string fieldName)
    {
        var value = text.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"{fieldName} 无效。");
        }

        return value;
    }

    private static void EnsureDefaultExternalResourceUrls(GeoxUrlSettings geoxUrls)
    {
        if (string.IsNullOrWhiteSpace(geoxUrls.GeoIp))
        {
            geoxUrls.GeoIp = GeoxUrlSettings.DefaultGeoIpUrl;
        }

        if (string.IsNullOrWhiteSpace(geoxUrls.GeoSite))
        {
            geoxUrls.GeoSite = GeoxUrlSettings.DefaultGeoSiteUrl;
        }

        if (string.IsNullOrWhiteSpace(geoxUrls.Mmdb))
        {
            geoxUrls.Mmdb = GeoxUrlSettings.DefaultMmdbUrl;
        }

        if (string.IsNullOrWhiteSpace(geoxUrls.Asn))
        {
            geoxUrls.Asn = GeoxUrlSettings.DefaultAsnUrl;
        }
    }

    private void AppendLogLine(string line)
    {
        var formatted = $"[{DateTime.Now:HH:mm:ss}] {line}";
        _logLines.Add(formatted);
        if (_logLines.Count > MaxLogLines)
        {
            _logLines.RemoveAt(0);
        }

        LogsTextBox.Text = string.Join(Environment.NewLine, _logLines);
        LogsTextBox.CaretIndex = LogsTextBox.Text.Length;
        LogsTextBox.ScrollToEnd();
    }

    private void SetMessage(string message)
    {
        MessageTextBlock.Text = message;
    }

    private static int ParsePositiveInt(string text, string fieldName)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw new InvalidOperationException($"{fieldName} 必须是正整数。");
        }

        return value;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0d, bytes);
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }

    private void ExecuteUiAction(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException ex)
        {
            SetMessage(ex.Message);
        }
        catch (YamlException ex)
        {
            SetMessage($"YAML 覆写格式错误: {ex.Message}");
        }
        catch (FileNotFoundException ex)
        {
            SetMessage($"文件不存在: {ex.FileName}");
        }
        catch (DirectoryNotFoundException ex)
        {
            SetMessage($"目录不存在: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            SetMessage($"权限不足: {ex.Message}");
        }
        catch (IOException ex)
        {
            SetMessage($"I/O 错误: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            SetMessage($"API 请求失败: {ex.Message}");
        }
        catch (Win32Exception ex)
        {
            SetMessage($"系统调用失败: {ex.Message}");
        }
    }

    private async Task ExecuteUiActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (InvalidOperationException ex)
        {
            SetMessage(ex.Message);
        }
        catch (YamlException ex)
        {
            SetMessage($"YAML 覆写格式错误: {ex.Message}");
        }
        catch (FileNotFoundException ex)
        {
            SetMessage($"文件不存在: {ex.FileName}");
        }
        catch (DirectoryNotFoundException ex)
        {
            SetMessage($"目录不存在: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            SetMessage($"权限不足: {ex.Message}");
        }
        catch (IOException ex)
        {
            SetMessage($"I/O 错误: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            SetMessage($"API 请求失败: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            SetMessage("请求超时。");
        }
        catch (Win32Exception ex)
        {
            SetMessage($"系统调用失败: {ex.Message}");
        }
    }

    private sealed record ProxyMemberRow(string NodeName, string Delay);
    private sealed record ProxyDelayTestResult(string ProxyName, int? DelayMilliseconds, string? ErrorMessage);
    private sealed record SubscriptionRow(string Status, string Name, string Used, string Total, string Expires);
}
