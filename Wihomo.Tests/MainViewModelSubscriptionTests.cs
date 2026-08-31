using System.IO;
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
        _directory = Path.Combine(Path.GetTempPath(), $"wihomo-tests-{Guid.NewGuid():N}");
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
