using System.Text;
using Wihomo.Models;
using Wihomo.Services;

namespace Wihomo.Tests;

public class SubscriptionParserTests
{
    private const string RulesYaml = """
        port: 7890
        proxies:
          - name: a
            type: ss
        rules:
          - DOMAIN-SUFFIX,example.com,PROXY
          - DOMAIN-SUFFIX,example.com,PROXY
          - GEOIP,CN,DIRECT

          # 注释应被跳过
          - MATCH,PROXY
        """;

    [Fact]
    public void Parse_PlainYaml_ExtractsDedupedRules()
    {
        var result = SubscriptionParser.Parse(RulesYaml);

        Assert.Equal(
            ["DOMAIN-SUFFIX,example.com,PROXY", "GEOIP,CN,DIRECT", "MATCH,PROXY"],
            result.Rules);
    }

    [Fact]
    public void Parse_StandardBase64_DecodesAndExtractsSameRules()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(RulesYaml));

        var result = SubscriptionParser.Parse(encoded);

        Assert.Equal(
            ["DOMAIN-SUFFIX,example.com,PROXY", "GEOIP,CN,DIRECT", "MATCH,PROXY"],
            result.Rules);
    }

    // 同一份订阅 YAML（规则里含中文策略名）的两种 base64 形态。
    // 标准字母表形态含 '+'；URL-safe 形态把该位置换成 '-'。
    // 纯 ASCII 载荷的 base64 在数学上不会出现 '+' 或 '/'，所以这个差异只在载荷含
    // 非 ASCII（中文节点名/策略名，正是本应用的常见情况）时才暴露。
    private const string StandardBase64WithChinese =
        "cnVsZXM6CiAgLSBET01BSU4tU1VGRklYLGV4YW1wbGUuY29tLOiKgueCuemAieaLqQogIC0gR0VPSVAsQ04s55u06L+eCg==";

    private const string UrlSafeBase64WithChinese =
        "cnVsZXM6CiAgLSBET01BSU4tU1VGRklYLGV4YW1wbGUuY29tLOiKgueCuemAieaLqQogIC0gR0VPSVAsQ04s55u06L-eCg==";

    [Fact]
    public void Parse_UrlSafeBase64_IsNotDecoded_CurrentBehavior()
    {
        // LooksLikeBase64 只接受标准字母表 [A-Za-z0-9+/=]，URL-safe 的 '-'/'_' 会被判为非 base64，
        // 于是整串原样返回、解析不出任何规则。SubscriptionFormatConverter 那条路径是显式处理 URL-safe 的，
        // 两者不一致；此处锁定本路径现状，是否在 Stage 3 收口另行决定。
        Assert.Contains('+', StandardBase64WithChinese);
        Assert.Contains('-', UrlSafeBase64WithChinese);
        Assert.NotEqual(StandardBase64WithChinese, UrlSafeBase64WithChinese);

        var ok = SubscriptionParser.Parse(StandardBase64WithChinese).Rules;
        Assert.Equal(["DOMAIN-SUFFIX,example.com,节点选择", "GEOIP,CN,直连"], ok);

        Assert.Empty(SubscriptionParser.Parse(UrlSafeBase64WithChinese).Rules);
        Assert.Equal(UrlSafeBase64WithChinese, SubscriptionParser.DecodeSubscriptionText(UrlSafeBase64WithChinese));
    }

    [Fact]
    public void Parse_ProxyUriListWithoutRules_ReturnsEmpty()
    {
        var content = "ss://YWVzLTI1Ni1nY206cGFzcw==@1.2.3.4:8388#HK\nvmess://abcdef";

        Assert.Empty(SubscriptionParser.Parse(content).Rules);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_BlankContent_ReturnsEmpty(string content)
    {
        Assert.Empty(SubscriptionParser.Parse(content).Rules);
    }

    [Fact]
    public void Parse_RulesSectionEndsAtNextTopLevelKey()
    {
        var content = """
            rules:
              - DOMAIN-SUFFIX,a.com,PROXY
            proxy-groups:
              - name: AUTO
                proxies:
                  - DIRECT
            """;

        Assert.Equal(["DOMAIN-SUFFIX,a.com,PROXY"], SubscriptionParser.Parse(content).Rules);
    }

    [Theory]
    [InlineData("DOMAIN-SUFFIX,a.com,PROXY", true)]
    [InlineData("MATCH,PROXY", true)]
    [InlineData("FINAL,PROXY", true)]
    [InlineData("GEOIP,CN,DIRECT", true)]
    [InlineData("GEOSITE,cn,PROXY", true)]
    [InlineData("IP-CIDR,1.0.0.0/8,DIRECT", true)]
    [InlineData("SRC-IP-CIDR,10.0.0.0/8,PROXY", true)]
    [InlineData("PROCESS-NAME,chrome,PROXY", true)]
    [InlineData("URL-REGEX,^https,PROXY", true)]
    [InlineData("RULE-SET,ads,REJECT", true)]
    [InlineData("AND,DOMAIN,a.com,PROXY", true)]
    [InlineData("NOT,DOMAIN,a.com,PROXY", true)]
    [InlineData("OR,DOMAIN,a.com,PROXY", true)]
    [InlineData("DOMAIN", true)]
    [InlineData("geoip", false)]
    [InlineData("name: AUTO", false)]
    [InlineData("", false)]
    public void IsRuleCandidate_MatchesExpected(string text, bool expected)
    {
        Assert.Equal(expected, SubscriptionParser.IsRuleCandidate(text));
    }

    [Fact]
    public void Decode_MissingPadding_IsTolerated()
    {
        var bytes = Encoding.UTF8.GetBytes("rules:\n  - MATCH,PROXY");
        var unpadded = Convert.ToBase64String(bytes).TrimEnd('=');

        Assert.StartsWith("rules:", SubscriptionParser.DecodeSubscriptionText(unpadded));
    }

    [Fact]
    public void Decode_DecodesToBinaryKeepsOriginal()
    {
        // 含 NUL 的解码结果说明这不是文本订阅，应原样返回而不是产出乱码。
        var binary = new byte[] { 0, 1, 2, 255, 254, 0 };
        var encoded = Convert.ToBase64String(binary);

        Assert.Equal(encoded, SubscriptionParser.DecodeSubscriptionText(encoded));
    }

    [Fact]
    public void Decode_ShortBase64ishText_IsLeftAlone()
    {
        const string text = "YWJjZA==";

        Assert.Equal(text, SubscriptionParser.DecodeSubscriptionText(text));
    }
}

public class SubscriptionUsageTests
{
    [Fact]
    public void Apply_FullUserInfo_SetsAllFields()
    {
        var subscription = new SubscriptionItem();

        SubscriptionUsage.Apply(subscription, "upload=1000;download=2000;total=9000;expire=1700000000");

        Assert.Equal(1000, subscription.UploadBytes);
        Assert.Equal(2000, subscription.DownloadBytes);
        Assert.Equal(9000, subscription.TotalBytes);
        Assert.NotNull(subscription.ExpireAt);

        // ExpireAt 经 ToLocalTime() 转换，断言 Unix 秒值才与时区无关。
        Assert.Equal(1700000000, subscription.ExpireAt!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public void Apply_PartialUserInfo_LeavesOtherFieldsUntouched()
    {
        var subscription = new SubscriptionItem { TotalBytes = 4242, ExpireAt = null };

        SubscriptionUsage.Apply(subscription, "upload=7;garbage;=");

        Assert.Equal(7, subscription.UploadBytes);
        Assert.Equal(4242, subscription.TotalBytes);
        Assert.Null(subscription.ExpireAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Apply_EmptyUserInfo_IsNoOp(string? userInfo)
    {
        var subscription = new SubscriptionItem { UploadBytes = 1, DownloadBytes = 2, TotalBytes = 3 };

        SubscriptionUsage.Apply(subscription, userInfo);

        Assert.Equal(1, subscription.UploadBytes);
        Assert.Equal(2, subscription.DownloadBytes);
        Assert.Equal(3, subscription.TotalBytes);
    }

    [Theory]
    [InlineData("expire=-1")]
    [InlineData("expire=253402300800")]
    [InlineData("expire=abc")]
    public void Apply_ExpireOutOfRangeOrMalformed_NotSet(string userInfo)
    {
        var subscription = new SubscriptionItem();

        SubscriptionUsage.Apply(subscription, userInfo);

        Assert.Null(subscription.ExpireAt);
    }

    [Fact]
    public void Apply_ExpireAtUpperBound_IsAccepted()
    {
        var subscription = new SubscriptionItem();

        SubscriptionUsage.Apply(subscription, "expire=253402300799");

        Assert.NotNull(subscription.ExpireAt);
    }

    [Fact]
    public void EnsureSingleActive_KeepsNamedActiveAndDisablesRest()
    {
        var settings = new AppSettings
        {
            ActiveSubscriptionName = "b",
            Subscriptions =
            [
                new SubscriptionItem { Name = "a", Enabled = true },
                new SubscriptionItem { Name = "b", Enabled = true },
                new SubscriptionItem { Name = "c", Enabled = false },
            ],
        };

        SubscriptionUsage.EnsureSingleActive(settings);

        Assert.Equal([false, true, false], settings.Subscriptions.Select(x => x.Enabled));
        Assert.Equal("b", settings.ActiveSubscriptionName);
    }

    [Fact]
    public void EnsureSingleActive_WithoutMatchingName_KeepsFirstEnabled()
    {
        var settings = new AppSettings
        {
            ActiveSubscriptionName = "不存在",
            Subscriptions =
            [
                new SubscriptionItem { Name = "a", Enabled = false },
                new SubscriptionItem { Name = "b", Enabled = true },
                new SubscriptionItem { Name = "c", Enabled = true },
            ],
        };

        SubscriptionUsage.EnsureSingleActive(settings);

        Assert.Equal([false, true, false], settings.Subscriptions.Select(x => x.Enabled));
        Assert.Equal("b", settings.ActiveSubscriptionName);
    }

    [Fact]
    public void EnsureSingleActive_SingleEnabled_DoesNotRenameActive()
    {
        var settings = new AppSettings
        {
            ActiveSubscriptionName = "其它",
            Subscriptions = [new SubscriptionItem { Name = "a", Enabled = true }],
        };

        SubscriptionUsage.EnsureSingleActive(settings);

        Assert.True(settings.Subscriptions[0].Enabled);
        Assert.Equal("其它", settings.ActiveSubscriptionName);
    }

    [Fact]
    public void DisableOthers_MatchesNameCaseInsensitively()
    {
        var settings = new AppSettings
        {
            Subscriptions =
            [
                new SubscriptionItem { Name = "Alpha", Enabled = true },
                new SubscriptionItem { Name = "BETA", Enabled = true },
            ],
        };

        SubscriptionUsage.DisableOthers(settings, "alpha");

        Assert.True(settings.Subscriptions[0].Enabled);
        Assert.False(settings.Subscriptions[1].Enabled);
    }
}
