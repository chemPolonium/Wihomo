using System.Globalization;
using Wihomo.Models;

namespace Wihomo.Services;

/// <summary>
/// 订阅流量/到期信息与「仅一个订阅生效」不变量的维护。
/// </summary>
public static class SubscriptionUsage
{
    /// <summary>
    /// 解析订阅响应的 Subscription-Userinfo 头（形如 upload=1;download=2;total=3;expire=1700000000）。
    /// 缺失或非法的字段保持原值不变。
    /// </summary>
    public static void Apply(SubscriptionItem subscription, string? userInfo)
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

    /// <summary>
    /// 多个订阅同时启用时收敛为一个：优先保留 ActiveSubscriptionName 指向的那个，否则取第一个。
    /// </summary>
    public static void EnsureSingleActive(AppSettings settings)
    {
        var enabledSubs = settings.Subscriptions.Where(x => x.Enabled).ToList();
        if (enabledSubs.Count <= 1)
        {
            return;
        }

        var keep = enabledSubs.FirstOrDefault(x =>
            string.Equals(x.Name, settings.ActiveSubscriptionName, StringComparison.OrdinalIgnoreCase))
            ?? enabledSubs[0];

        foreach (var sub in settings.Subscriptions)
        {
            sub.Enabled = string.Equals(sub.Name, keep.Name, StringComparison.OrdinalIgnoreCase);
        }

        settings.ActiveSubscriptionName = keep.Name;
    }

    public static void DisableOthers(AppSettings settings, string activeName)
    {
        foreach (var sub in settings.Subscriptions)
        {
            if (!string.Equals(sub.Name, activeName, StringComparison.OrdinalIgnoreCase))
            {
                sub.Enabled = false;
            }
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
}
