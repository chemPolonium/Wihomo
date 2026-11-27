using System.IO;
using System.Reflection;
using System.Text.Json;
using Wihomo.Models;
using Wihomo.Services;
using Wihomo.Services.Realtime;
using Wihomo.ViewModels;

namespace Wihomo.Tests;

/// <summary>
/// 删除订阅的持久化行为。设置文件落在临时目录，全程不触碰真实 %AppData%，
/// 也不会启动内核（StartCoreOnProgramStart 固定为 false）。
/// </summary>
public class MainViewModelSubscriptionTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _directory;
    private readonly string _settingsPath;
    private readonly MainViewModel _viewModel;

    public MainViewModelSubscriptionTests()
    {
        _directory = Path.Combine(TestPaths.TempRoot, $"wihomo-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _settingsPath = Path.Combine(_directory, "settings.json");
        WriteSettings(TwoSubscriptionSettings(_directory));

        _viewModel = new MainViewModel(
            new SettingsService(_settingsPath),
            new MihomoConfigBuilder(),
            new SubscriptionConfigComposer(),
            new SubscriptionFormatConverter(),
            new MihomoProcessManager(),
            new MihomoApiClient(),
            new SystemProxyService(),
            new WindowsStartupService(),
            new MihomoTelemetry(),
            new DirectUiDispatcher());
    }

    [Fact]
    public async Task 删除活动订阅后立即落盘并清除活动标记()
    {
        await _viewModel.InitializeAsync();
        _viewModel.Subscriptions.SelectedSubscription = _viewModel.Subscriptions.FindByName("主订阅");

        await _viewModel.RemoveSubscriptionCommand.ExecuteAsync(null);

        var saved = ReadSettings();
        Assert.Null(saved.Subscriptions.FirstOrDefault(x => x.Name == "主订阅"));
        Assert.NotNull(saved.Subscriptions.FirstOrDefault(x => x.Name == "备用订阅"));
        Assert.Equal(string.Empty, saved.ActiveSubscriptionName);
        Assert.Equal("订阅已删除。", _viewModel.Message);
    }

    [Fact]
    public async Task 删除非活动订阅保留活动标记()
    {
        await _viewModel.InitializeAsync();
        _viewModel.Subscriptions.SelectedSubscription = _viewModel.Subscriptions.FindByName("备用订阅");

        await _viewModel.RemoveSubscriptionCommand.ExecuteAsync(null);

        var saved = ReadSettings();
        Assert.Null(saved.Subscriptions.FirstOrDefault(x => x.Name == "备用订阅"));
        Assert.Equal("主订阅", saved.ActiveSubscriptionName);
        Assert.True(saved.Subscriptions.Single(x => x.Name == "主订阅").Enabled);
    }

    [Fact]
    public async Task 未选中订阅时报错且不改写设置文件()
    {
        await _viewModel.InitializeAsync();
        var before = File.ReadAllText(_settingsPath);

        await _viewModel.RemoveSubscriptionCommand.ExecuteAsync(null);

        Assert.Contains("请先选择要删除的订阅", _viewModel.Message);
        Assert.Equal(before, File.ReadAllText(_settingsPath));
    }

    [Fact]
    public async Task CloneSettings_深拷贝所有嵌套对象()
    {
        await _viewModel.InitializeAsync();

        var cloneMethod = typeof(MainViewModel).GetMethod(
            "CloneSettings",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("CloneSettings 方法不存在。");
        var settingsField = typeof(MainViewModel).GetField(
            "_settings",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_settings 字段不存在。");

        var original = (AppSettings)settingsField.GetValue(_viewModel)!;
        var clone = (AppSettings)cloneMethod.Invoke(_viewModel, null)!;

        Assert.NotSame(original, clone);
        Assert.NotSame(original.Core, clone.Core);
        Assert.NotSame(original.Dns, clone.Dns);
        Assert.NotSame(original.Dns.FakeIpFilter, clone.Dns.FakeIpFilter);
        Assert.NotSame(original.Dns.DefaultNameserver, clone.Dns.DefaultNameserver);
        Assert.NotSame(original.Dns.Nameserver, clone.Dns.Nameserver);
        Assert.NotSame(original.Dns.Fallback, clone.Dns.Fallback);
        Assert.NotSame(original.Dns.FallbackFilterIpCidr, clone.Dns.FallbackFilterIpCidr);
        Assert.NotSame(original.GeoxUrls, clone.GeoxUrls);
        Assert.NotSame(original.Subscriptions, clone.Subscriptions);
        Assert.NotSame(original.Subscriptions[0], clone.Subscriptions[0]);
        Assert.NotSame(original.SubscriptionRules, clone.SubscriptionRules);

        clone.Dns.Nameserver.Add("9.9.9.9");
        clone.Subscriptions[0].Name = "已修改";
        clone.SubscriptionRules.Add("MATCH,DIRECT");

        Assert.DoesNotContain("9.9.9.9", original.Dns.Nameserver);
        Assert.NotEqual("已修改", original.Subscriptions[0].Name);
        Assert.Empty(original.SubscriptionRules);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static AppSettings TwoSubscriptionSettings(string workingDirectory) => new()
    {
        Core = new CoreRuntimeSettings
        {
            // 显式给一个临时工作目录，避免 ApplyDefaults 把空值指到真实的 %LocalAppData%。
            WorkingDirectory = workingDirectory,
        },
        Subscriptions =
        [
            new SubscriptionItem { Name = "主订阅", Url = "https://a/sub", IntervalSeconds = 3600, Enabled = true },
            new SubscriptionItem { Name = "备用订阅", Url = "https://b/sub", IntervalSeconds = 3600, Enabled = false },
        ],
        ActiveSubscriptionName = "主订阅",
        StartCoreOnProgramStart = false,
        StartWithWindows = false,
    };

    private void WriteSettings(AppSettings settings)
    {
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    private AppSettings ReadSettings()
    {
        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), JsonOptions)
            ?? throw new InvalidOperationException("设置文件反序列化失败。");
    }
}
