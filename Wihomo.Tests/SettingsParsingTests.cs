using System.Globalization;
using Wihomo.Models;
using Wihomo.Services;

namespace Wihomo.Tests;

public class SettingsParsingTests
{
    [Theory]
    [InlineData("1", 1)]
    [InlineData("  9090  ", 9090)]
    [InlineData("2147483647", int.MaxValue)]
    public void ParsePositiveInt_AcceptsPositive(string text, int expected)
    {
        Assert.Equal(expected, SettingsParsing.ParsePositiveInt(text, "端口"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12.5")]
    [InlineData("1a")]
    public void ParsePositiveInt_RejectsNonPositive(string text)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SettingsParsing.ParsePositiveInt(text, "Mixed Port"));
        Assert.Equal("Mixed Port 必须是正整数。", ex.Message);
    }

    [Theory]
    [InlineData("http://a.example/x", "http://a.example/x")]
    [InlineData("https://a.example/x", "https://a.example/x")]
    [InlineData("  https://a.example/x  ", "https://a.example/x")]
    [InlineData("HTTPS://A.EXAMPLE/X", "HTTPS://A.EXAMPLE/X")]
    public void ParseAbsoluteUrl_TrimsButDoesNotRewrite(string text, string expected)
    {
        Assert.Equal(expected, SettingsParsing.ParseAbsoluteUrl(text, "GeoIP 数据库 URL"));
    }

    [Theory]
    [InlineData("a.example/x")]
    [InlineData("ftp://a.example/x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseAbsoluteUrl_RejectsNonHttpAbsolute(string text)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SettingsParsing.ParseAbsoluteUrl(text, "MMDB 数据库 URL"));
        Assert.Equal("MMDB 数据库 URL 无效。", ex.Message);
    }

    [Fact]
    public void ParseMultilineText_SplitsTrimsAndDropsBlanks()
    {
        var result = SettingsParsing.ParseMultilineText(" 223.5.5.5\r\n\r\n  doh.pub  \n\t\n1.1.1.1");

        Assert.Equal(["223.5.5.5", "doh.pub", "1.1.1.1"], result);
    }

    [Fact]
    public void ParseMultilineText_Empty_ReturnsEmptyList()
    {
        Assert.Empty(SettingsParsing.ParseMultilineText(""));
    }

    [Fact]
    public void EnsureDefaultExternalResourceUrls_FillsOnlyBlanks()
    {
        var geox = new GeoxUrlSettings { GeoIp = "", GeoSite = "  ", Mmdb = "https://custom/mmdb", Asn = "" };

        SettingsParsing.EnsureDefaultExternalResourceUrls(geox);

        Assert.Equal(GeoxUrlSettings.DefaultGeoIpUrl, geox.GeoIp);
        Assert.Equal(GeoxUrlSettings.DefaultGeoSiteUrl, geox.GeoSite);
        Assert.Equal("https://custom/mmdb", geox.Mmdb);
        Assert.Equal(GeoxUrlSettings.DefaultAsnUrl, geox.Asn);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1073741824, "1 GB")]
    [InlineData(1099511627776, "1 TB")]
    [InlineData(-1, "0 B")]
    [InlineData(long.MinValue, "0 B")]
    public void FormatBytes_SelectsUnitAndClamps(long bytes, string expected)
    {
        Assert.Equal(expected, SettingsParsing.FormatBytes(bytes));
    }

    [Theory]
    [InlineData(long.MaxValue, "8388608 TB")]
    [InlineData(1125899906842624, "1024 TB")]
    public void FormatBytes_CapsAtTB_WithoutGrowingUnits(long bytes, string expected)
    {
        // 单位表止于 TB，超过后不再进位，只会把数字放大。
        Assert.Equal(expected, SettingsParsing.FormatBytes(bytes));
    }

    [Fact]
    public void FormatBytes_RoundsToTwoDecimals()
    {
        // 小数分隔符随当前区域设置，这里只锁定"保留两位、去掉尾零"的取整语义。
        var expected = string.Format(CultureInfo.CurrentCulture, "{0:0.##} KB", 1.5d);

        Assert.Equal(expected, SettingsParsing.FormatBytes(1536));
    }

    [Fact]
    public void FormatBytes_DropsTrailingZeros()
    {
        var expected = string.Format(CultureInfo.CurrentCulture, "{0:0.##} KB", 2d);

        Assert.Equal(expected, SettingsParsing.FormatBytes(2048));
    }
}
