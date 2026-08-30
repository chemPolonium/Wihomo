using System.Globalization;
using Wihomo.Models;

namespace Wihomo.Services;

/// <summary>
/// 设置输入解析与展示格式化。全部为纯函数，不触碰控件。
/// </summary>
public static class SettingsParsing
{
    public static List<string> ParseMultilineText(string text)
    {
        return text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    public static string ParseAbsoluteUrl(string text, string fieldName)
    {
        var value = text.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"{fieldName} 无效。");
        }

        return value;
    }

    public static void EnsureDefaultExternalResourceUrls(GeoxUrlSettings geoxUrls)
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

    public static int ParsePositiveInt(string text, string fieldName)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw new InvalidOperationException($"{fieldName} 必须是正整数。");
        }

        return value;
    }

    // 小数分隔符取自当前区域设置，与解析侧固定 InvariantCulture 不一致；见重构报告。
    public static string FormatBytes(long bytes)
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
}
