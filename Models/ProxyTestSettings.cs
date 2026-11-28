namespace Wihomo.Models;

/// <summary>代理测速偏好。属于客户端行为，不写入 mihomo 配置。</summary>
public sealed class ProxyTestSettings
{
    public string Url { get; set; } = "http://cp.cloudflare.com/generate_204";

    public int TimeoutMilliseconds { get; set; } = 5000;
}
