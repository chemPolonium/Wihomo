using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace Wihomo.Services.Realtime;

public enum StreamState
{
    Idle,
    Connected,
    Reconnecting,
    Aborted,
}

/// <summary>
/// 单条 mihomo WebSocket 推送流：连接、读取文本帧、解析、断线重连。
/// 解析失败只计数不中断连接，避免个别异常帧让整条链路静默失效。
/// </summary>
public sealed class MihomoWebSocketStream<T> : IAsyncDisposable
    where T : class
{
    private static readonly TimeSpan MinimumBackoff = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromSeconds(10);

    private readonly Uri _uri;
    private readonly string? _secret;
    private readonly Func<string, T?> _parse;
    private readonly Action<T> _onItem;
    private readonly Action<StreamState, string?> _onState;
    private readonly TimeSpan _initialBackoff;

    private CancellationTokenSource? _lifetime;
    private Task? _runner;
    private int _parseFailures;
    private int _reconnects;

    public MihomoWebSocketStream(
        Uri uri,
        string? secret,
        Func<string, T?> parse,
        Action<T> onItem,
        Action<StreamState, string?> onState,
        TimeSpan? initialBackoff = null)
    {
        _uri = uri;
        _secret = secret;
        _parse = parse;
        _onItem = onItem;
        _onState = onState;
        _initialBackoff = initialBackoff ?? MinimumBackoff;
    }

    public string Path => _uri.AbsolutePath;

    public StreamState State { get; private set; } = StreamState.Idle;

    public int ParseFailures => _parseFailures;

    public int Reconnects => _reconnects;

    public void Start()
    {
        if (_runner is not null)
        {
            return;
        }

        _lifetime = new CancellationTokenSource();
        _runner = RunAsync(_lifetime.Token);
    }

    public void Stop()
    {
        var lifetime = _lifetime;
        var runner = _runner;
        _lifetime = null;
        _runner = null;

        if (runner is null || lifetime is null)
        {
            SetState(StreamState.Idle, null);
            return;
        }

        lifetime.Cancel();
        SetState(StreamState.Idle, null);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var backoff = _initialBackoff;

        while (!cancellationToken.IsCancellationRequested)
        {
            ClientWebSocket? socket = null;
            try
            {
                socket = new ClientWebSocket();
                // 不设 Proxy 时 ClientWebSocket 会采用环境变量/系统代理，
                // 这台机器上那会让 loopback 握手直接失败（实测表现为 502）。
                socket.Options.Proxy = NoProxy.Instance;
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                if (!string.IsNullOrWhiteSpace(_secret))
                {
                    socket.Options.SetRequestHeader("Authorization", "Bearer " + _secret);
                }

                await socket.ConnectAsync(_uri, cancellationToken);
                SetState(StreamState.Connected, null);
                backoff = _initialBackoff;

                await ReadLoopAsync(socket, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                SetState(StreamState.Reconnecting, ex.Message);
            }
            finally
            {
                socket?.Dispose();
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            _reconnects++;
            var delay = WithJitter(backoff);
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaximumBackoff.Ticks));
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (State != StreamState.Idle)
        {
            SetState(StreamState.Idle, null);
        }
    }

    private async Task ReadLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var message = new StringBuilder();

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (result.EndOfMessage)
                {
                    Emit(message.ToString());
                    message.Clear();
                }
            }
            else
            {
                // 内核实测只发文本帧；二进制帧按原样丢弃但不中断连接。
                message.Clear();
            }
        }
    }

    private void Emit(string frame)
    {
        T? item;
        try
        {
            item = _parse(frame);
        }
        catch (Exception ex)
        {
            _parseFailures++;
            _onState(StreamState.Connected, $"解析 {Path} 帧失败 {ex.Message}");
            return;
        }

        if (item is null)
        {
            _parseFailures++;
            return;
        }

        _onItem(item);
    }

    private void SetState(StreamState state, string? detail)
    {
        State = state;
        _onState(state, detail);
    }

    private static TimeSpan WithJitter(TimeSpan backoff)
    {
        // 抖动避免多条流同时断开后同步重试打在内核同一个瞬间。
        var factor = 0.75d + (Random.Shared.NextDouble() * 0.5d);
        return TimeSpan.FromTicks((long)(backoff.Ticks * factor));
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        var runner = _runner;
        if (runner is not null)
        {
            try
            {
                await runner.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // 退出路径，忽略清理期异常。
            }
        }
    }
}

/// <summary>IWebProxy 返回 null 即表示不经过代理，用于强制直连控制器。</summary>
internal sealed class NoProxy : IWebProxy
{
    public static readonly NoProxy Instance = new();

    public ICredentials? Credentials { get; set; }

    public Uri? GetProxy(Uri destination) => null;

    public bool IsBypassed(Uri uri) => true;
}
