using System.Text.Json;

namespace Wihomo.Services.Realtime;

/// <summary>
/// mihomo 内核推送流的聚合入口。路径与鉴权方式来自 v1.19.29 实测：
/// 顶层 /traffic、/connections、/logs，仅接受 Authorization: Bearer，?secret= 会 401。
/// 事件在后台线程触发，订阅方负责切回 UI 线程。
/// </summary>
public sealed class MihomoTelemetry : IDisposable
{
    public const string TrafficPath = "/traffic";
    public const string ConnectionsPath = "/connections";
    public const string LogsPath = "/logs";

    private readonly object _gate = new();
    private MihomoWebSocketStream<TrafficFrame>? _traffic;
    private MihomoWebSocketStream<ConnectionsFrame>? _connections;
    private MihomoWebSocketStream<LogFrame>? _logs;
    private bool _disposed;

    public event Action<TrafficFrame>? TrafficReceived;

    public event Action<ConnectionsFrame>? ConnectionsReceived;

    /// <summary>内核日志帧不带时间戳，时间由本类在接收侧打点。</summary>
    public event Action<DateTimeOffset, LogFrame>? LogReceived;

    public event Action<string, StreamState, string?>? StreamStateChanged;

    /// <summary>
    /// 数值推送链路是否可用。/logs 单独断开不算失效，因为空闲内核本来就不产生日志。
    /// </summary>
    public bool IsHealthy
    {
        get
        {
            lock (_gate)
            {
                return _traffic?.State == StreamState.Connected
                    && _connections?.State == StreamState.Connected;
            }
        }
    }

    public int TrafficParseFailures
    {
        get
        {
            lock (_gate)
            {
                return _traffic?.ParseFailures ?? 0;
            }
        }
    }

    public void Start(string host, int port, string? secret)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(MihomoTelemetry));
            }

            StopCore();

            _traffic = CreateStream(TrafficPath, host, port, secret, Deserialize<TrafficFrame>, TrafficReceived);
            _connections = CreateStream(ConnectionsPath, host, port, secret, Deserialize<ConnectionsFrame>, ConnectionsReceived);
            _logs = CreateStream(LogsPath, host, port, secret, Deserialize<LogFrame>, frame => LogReceived?.Invoke(DateTimeOffset.Now, frame));

            _traffic.Start();
            _connections.Start();
            _logs.Start();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopCore();
        }
    }

    private MihomoWebSocketStream<T> CreateStream<T>(
        string path,
        string host,
        int port,
        string? secret,
        Func<string, T?> parse,
        Action<T> handler)
        where T : class
    {
        var uri = new UriBuilder(Uri.UriSchemeWs, host, port, path).Uri;
        return new MihomoWebSocketStream<T>(
            uri,
            secret,
            parse,
            handler,
            (state, detail) => StreamStateChanged?.Invoke(path, state, detail));
    }

    private static T? Deserialize<T>(string frame) where T : class
    {
        return string.IsNullOrWhiteSpace(frame)
            ? null
            : JsonSerializer.Deserialize<T>(frame, CoreJson.Options);
    }

    private void StopCore()
    {
        _traffic?.Stop();
        _connections?.Stop();
        _logs?.Stop();
        _traffic = null;
        _connections = null;
        _logs = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopCore();
        }
    }
}
