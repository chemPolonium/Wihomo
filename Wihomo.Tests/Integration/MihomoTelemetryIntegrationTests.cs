using System.Net.Http;
using System.Text;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Wihomo.Services;
using Wihomo.Services.Realtime;

namespace Wihomo.Tests.Integration;

/// <summary>
/// 用仓库内捆绑的真实内核验证 WS 接入链路：握手鉴权、代理绕过、帧解析、断线重连。
/// DTO 单测只能证明解析，证明不了 C# 客户端能否真的连上内核。
/// 需要本机可执行 47MB 内核并绑定 loopback 端口，因此可通过 WIHOMO_SKIP_INTEGRATION=1 跳过。
/// </summary>
public sealed class MihomoTelemetryIntegrationTests : IAsyncLifetime
{
    private const string Secret = "integration-secret-ab";
    private static readonly string CoreExecutable = ResolveCorePath();

    private string _workingDirectory = string.Empty;
    private int _controllerPort;
    private int _mixedPort;
    private Process? _core;

    public bool Skipped => Environment.GetEnvironmentVariable("WIHOMO_SKIP_INTEGRATION") == "1";

    private static string ResolveCorePath()
    {
        // 测试输出目录位于 <repo>/Wihomo.Tests/bin/<cfg>/net9.0-windows
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wihomo.csproj")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName
            ?? throw new InvalidOperationException("无法定位仓库根目录。");
        return Path.Combine(root, "assets", "core", "mihomo-windows-amd64-v3.exe");
    }

    public Task InitializeAsync()
    {
        _workingDirectory = Path.Combine(Path.GetTempPath(), "wihomo-it-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_workingDirectory);
        _controllerPort = GetFreeLoopbackPort();
        _mixedPort = GetFreeLoopbackPort();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await StopCoreAsync();
        TryDeleteDirectory(_workingDirectory);
    }

    [Fact]
    public async Task Streams_DeliverParsedFrames_AndReconnectAfterCoreRestart()
    {
        if (Skipped)
        {
            return;
        }

        StartCore();

        // 顺带验证 REST 客户端在新生命周期管理下可用。
        using var api = new MihomoApiClient();
        api.Configure("127.0.0.1", _controllerPort, Secret);
        Assert.Equal("v1.19.29", await WaitUntilReadyAsync(api));

        var telemetry = new MihomoTelemetry();
        var traffic = new FrameCollector<TrafficFrame>();
        var connections = new FrameCollector<ConnectionsFrame>();
        var logs = new FrameCollector<LogFrame>();
        var reconnecting = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        telemetry.TrafficReceived += traffic.Add;
        telemetry.ConnectionsReceived += connections.Add;
        telemetry.LogReceived += (_, frame) => logs.Add(frame);
        telemetry.StreamStateChanged += (path, state, _) =>
        {
            if (state == StreamState.Reconnecting && path == MihomoTelemetry.TrafficPath)
            {
                reconnecting.TrySetResult(path);
            }
        };

        try
        {
            telemetry.Start("127.0.0.1", _controllerPort, Secret);

            // 制造一条会被内核记录的连接，用于同时验证 /logs 与 /connections 的真实帧。
            await GenerateTrafficAsync();

            // 内核每秒推一帧且首帧可能早于流量，因此一律按谓词等待，不取首帧。
            var trafficFrame = await traffic.FirstAsync(
                frame => frame.DownTotal > 0, TimeSpan.FromSeconds(20), "/traffic 非零下行累计");
            Assert.True(trafficFrame.UpTotal > 0, "/traffic 应同时报告非零上行累计");

            var connectionsFrame = await connections.FirstAsync(
                frame => frame.DownloadTotal > 0, TimeSpan.FromSeconds(20), "/connections 非零下行累计");
            Assert.True(connectionsFrame.UploadTotal > 0, "/connections 应同时报告非零上行累计");

            var logFrame = await logs.FirstAsync(
                frame => !string.IsNullOrWhiteSpace(frame.Type), TimeSpan.FromSeconds(20), "/logs 帧");
            Assert.Contains("DIRECT", logFrame.Payload, StringComparison.Ordinal);

            Assert.True(telemetry.IsHealthy, "两条数值流都应处于已连接状态");
            Assert.Equal(0, telemetry.TrafficParseFailures);

            // 断线检测：杀掉内核后必须转入重连状态，而不是静默停在 Connected。
            await StopCoreAsync();
            await WithTimeout(reconnecting.Task, TimeSpan.FromSeconds(20), "转入 Reconnecting");

            await WaitForControllerDownAsync();
            Assert.False(telemetry.IsHealthy, "内核停止后不应仍报告健康");

            // 重连：重启内核后必须自动恢复并继续收到帧。
            traffic.Clear();
            StartCore();
            Assert.Equal("v1.19.29", await WaitUntilReadyAsync(api));
            await GenerateTrafficAsync();

            var recovered = await traffic.FirstAsync(
                frame => frame.DownTotal > 0, TimeSpan.FromSeconds(45), "重连后的 /traffic 帧");
            Assert.True(recovered.DownTotal > 0);
        }
        finally
        {
            telemetry.Dispose();
        }
    }

    /// <summary>
    /// 收集推送帧以支持谓词等待。内核对 /traffic 与 /connections 固定每秒推一帧，
    /// 「取首帧」与「等条件成立」在这些断言上并不等价。
    /// </summary>
    private sealed class FrameCollector<T>
        where T : class
    {
        private readonly List<T> _frames = [];
        private readonly object _gate = new();

        public void Add(T frame)
        {
            lock (_gate)
            {
                _frames.Add(frame);
            }
        }

        /// <summary>
        /// 丢弃已收集的帧。验证重连时必须先清空，否则谓词会立刻命中断线前的旧帧。
        /// </summary>
        public void Clear()
        {
            lock (_gate)
            {
                _frames.Clear();
            }
        }

        public async Task<T> FirstAsync(Func<T, bool> predicate, TimeSpan timeout, string what)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                T? hit;
                lock (_gate)
                {
                    hit = _frames.FirstOrDefault(predicate);
                }

                if (hit is not null)
                {
                    return hit;
                }

                await Task.Delay(150);
            }

            int count;
            lock (_gate)
            {
                count = _frames.Count;
            }

            throw new TimeoutException($"{what} 未在 {timeout.TotalSeconds} 秒内出现（共收到 {count} 帧）。");
        }
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout, string what)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task)
        {
            throw new TimeoutException($"{what} 在 {timeout.TotalSeconds} 秒内未发生。");
        }

        return await task;
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private void StartCore()
    {
        File.WriteAllText(Path.Combine(_workingDirectory, "config.yaml"), BuildConfig());

        _core = Process.Start(new ProcessStartInfo
        {
            FileName = CoreExecutable,
            Arguments = $"-d \"{_workingDirectory}\" -f config.yaml",
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("内核启动失败。");

        // 持续排空输出，避免内核因管道缓冲区写满而阻塞。
        _core.OutputDataReceived += (_, _) => { };
        _core.ErrorDataReceived += (_, _) => { };
        _core.BeginOutputReadLine();
        _core.BeginErrorReadLine();
    }

    private string BuildConfig() =>
        "mixed-port: " + _mixedPort + "\n"
        + "allow-lan: false\n"
        + "mode: rule\n"
        + "log-level: info\n"
        + "ipv6: false\n"
        + "external-controller: 127.0.0.1:" + _controllerPort + "\n"
        + "secret: " + Secret + "\n"
        + "tun:\n  enable: false\n"
        + "rules:\n  - MATCH,DIRECT\n";

    private async Task<string> WaitUntilReadyAsync(MihomoApiClient api)
    {
        string? version = null;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                version = await api.GetVersionAsync();
                if (version != "unknown")
                {
                    return version;
                }
            }
            catch
            {
                // 内核尚未监听，继续等待。
            }

            await Task.Delay(250);
        }

        return version ?? "unknown";
    }

    private async Task GenerateTrafficAsync()
    {
        // 裸 TcpListener 充当源站：HttpListener 在 Windows 上需要 urlacl 或管理员权限。
        var originPort = GetFreeLoopbackPort();
        using var origin = new TcpListener(IPAddress.Loopback, originPort);
        origin.Start();

        var serving = Task.Run(async () =>
        {
            using var client = await origin.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var request = new byte[4096];
            await stream.ReadAtLeastAsync(request, 1);
            var body = new byte[8192];
            var header = $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
            await stream.WriteAsync(body);
            await stream.FlushAsync();
        });

        using var handler = new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = new WebProxy($"http://127.0.0.1:{_mixedPort}"),
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        var response = await client.GetAsync($"http://127.0.0.1:{originPort}/probe");
        var received = await response.Content.ReadAsByteArrayAsync();
        await serving;

        // 若没经代理走通，内核侧的字节断言就失去了意义，因此先确认这条链路本身成立。
        Assert.Equal(8192, received.Length);
    }

    private async Task WaitForControllerDownAsync()
    {
        using var handler = new SocketsHttpHandler { UseProxy = false, Proxy = null };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(1) };
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                await client.GetAsync($"http://127.0.0.1:{_controllerPort}/version");
            }
            catch
            {
                return;
            }

            await Task.Delay(250);
        }
    }

    private async Task StopCoreAsync()
    {
        var core = _core;
        _core = null;
        if (core is null)
        {
            return;
        }

        try
        {
            if (!core.HasExited)
            {
                core.Kill(entireProcessTree: true);
                await core.WaitForExitAsync();
            }
        }
        catch
        {
            // 清理路径。
        }
        finally
        {
            core.Dispose();
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 临时目录残留无害。
        }
    }
}
