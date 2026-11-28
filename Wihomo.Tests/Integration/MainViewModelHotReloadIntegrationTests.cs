using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Wihomo.Models;
using Wihomo.Services;
using Wihomo.Services.Realtime;
using Wihomo.ViewModels;

namespace Wihomo.Tests.Integration;

/// <summary>
/// 真内核验证“保存设置即生效”：配置可热重载时不重启进程，端口类改动也能落到监听器。
/// 与其它集成测试一样，可通过 WIHOMO_SKIP_INTEGRATION=1 跳过。
/// </summary>
public sealed class MainViewModelHotReloadIntegrationTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _directory;
    private readonly int _mixedPort;
    private readonly int _controllerPort;
    private readonly string _secret;
    private readonly MainViewModel _viewModel;

    public MainViewModelHotReloadIntegrationTests()
    {
        _directory = Path.Combine(TestPaths.TempRoot, "wihomo-reload-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_directory);
        _mixedPort = GetFreeLoopbackPort();
        _controllerPort = GetFreeLoopbackPort();
        _secret = "reload-test";

        var settings = new AppSettings
        {
            Core = new CoreRuntimeSettings
            {
                CoreExecutablePath = ResolveCorePath(),
                WorkingDirectory = _directory,
                ExternalControllerHost = "127.0.0.1",
                ExternalControllerPort = _controllerPort,
                Secret = _secret,
                MixedPort = _mixedPort,
                SocksPort = GetFreeLoopbackPort(),
                HttpPort = GetFreeLoopbackPort(),
                EnableSystemProxy = false,
            },
            Subscriptions =
            [
                new SubscriptionItem
                {
                    Name = "测试订阅",
                    Url = "https://example.com/sub",
                    IntervalSeconds = 3600,
                    Enabled = true,
                },
            ],
            ActiveSubscriptionName = "测试订阅",
            StartCoreOnProgramStart = false,
            StartWithWindows = false,
        };

        var subscriptionDirectory = Path.Combine(_directory, "subscriptions");
        Directory.CreateDirectory(subscriptionDirectory);
        File.WriteAllText(Path.Combine(subscriptionDirectory, "active.yaml"), Fixtures.SubscriptionYaml);

        var settingsPath = Path.Combine(_directory, "settings.json");
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, JsonOptions));

        _viewModel = new MainViewModel(
            new SettingsService(settingsPath),
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
    public async Task 保存设置优先热重载并应用新端口()
    {
        if (Environment.GetEnvironmentVariable("WIHOMO_SKIP_INTEGRATION") == "1")
        {
            return;
        }

        await _viewModel.InitializeAsync();
        await _viewModel.StartCoreCommand.ExecuteAsync(null);
        Assert.True(
            _viewModel.IsCoreRunning,
            _viewModel.Message + Environment.NewLine + string.Join(Environment.NewLine, _viewModel.Logs.Lines));

        using var api = new MihomoApiClient();
        api.Configure("127.0.0.1", _controllerPort, _secret);
        await api.SelectProxyAsync("节点选择", "JP-02");
        Assert.Equal("JP-02", await GetCurrentAsync(api, "节点选择"));

        var newMixedPort = GetFreeLoopbackPort();
        _viewModel.Settings.MixedPortText = newMixedPort.ToString();

        await _viewModel.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Contains("热重载", _viewModel.Message);
        Assert.True(_viewModel.IsCoreRunning);
        Assert.True(
            await WaitForPortAsync(newMixedPort, TimeSpan.FromSeconds(5)),
            $"热重载后 mixed-port {newMixedPort} 未开始监听。");
        Assert.False(IsPortOpen(_mixedPort), $"热重载后旧 mixed-port {_mixedPort} 仍在监听。");

        // 热重载会重建代理组，store-selected 必须把用户选中的节点带回来。
        Assert.Equal("JP-02", await GetCurrentAsync(api, "节点选择"));
    }

    private static async Task<string> GetCurrentAsync(MihomoApiClient api, string groupName)
    {
        var groups = await api.GetProxyGroupsAsync();
        return groups.Single(x => x.Name == groupName).Current;
    }

    [Fact]
    public async Task 控制端口变化时自动重启内核()
    {
        if (Environment.GetEnvironmentVariable("WIHOMO_SKIP_INTEGRATION") == "1")
        {
            return;
        }

        await _viewModel.InitializeAsync();
        await _viewModel.StartCoreCommand.ExecuteAsync(null);
        Assert.True(_viewModel.IsCoreRunning, _viewModel.Message);

        var newControllerPort = GetFreeLoopbackPort();
        _viewModel.Settings.ControllerPortText = newControllerPort.ToString();

        await _viewModel.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Contains("已重启", _viewModel.Message);
        Assert.True(_viewModel.IsCoreRunning);
        Assert.True(
            await WaitForPortAsync(newControllerPort, TimeSpan.FromSeconds(5)),
            $"重启后控制端口 {newControllerPort} 未开始监听。");
        Assert.False(IsPortOpen(_controllerPort), $"重启后旧控制端口 {_controllerPort} 仍在监听。");
    }

    public void Dispose()
    {
        _viewModel.PrepareForExit();
        _viewModel.Dispose();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task<bool> WaitForPortAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (IsPortOpen(port))
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    private static bool IsPortOpen(int port)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string ResolveCorePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wihomo.csproj")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName
            ?? throw new InvalidOperationException("无法定位仓库根目录。");
        return Path.Combine(root, "assets", "core", "mihomo-windows-amd64-v3.exe");
    }
}
