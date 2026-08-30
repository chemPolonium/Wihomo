using Wihomo.Services;
using Wihomo.Tests.Golden;

namespace Wihomo.Tests;

public class MihomoConfigBuilderGoldenTests
{
    [Fact]
    public void Build_DefaultsNoSubscription_MatchesGolden()
    {
        var actual = new MihomoConfigBuilder().Build(Fixtures.DefaultsNoSubscription());
        GoldenMaster.Verify(nameof(Build_DefaultsNoSubscription_MatchesGolden), actual);
    }

    [Fact]
    public void Build_TwoProvidersCustomRulesTun_MatchesGolden()
    {
        var actual = new MihomoConfigBuilder()
            .Build(Fixtures.TwoProvidersCustomRulesTun(), Fixtures.LocalProviderKeys());
        GoldenMaster.Verify(nameof(Build_TwoProvidersCustomRulesTun_MatchesGolden), actual);
    }

    [Fact]
    public void Build_DnsOffBlankGeoxPortsClamped_MatchesGolden()
    {
        var actual = new MihomoConfigBuilder().Build(Fixtures.DnsOffBlankGeoxPortsClamped());
        GoldenMaster.Verify(nameof(Build_DnsOffBlankGeoxPortsClamped_MatchesGolden), actual);
    }

    [Fact]
    public void Build_EscapingEdgeCases_MatchesGolden()
    {
        var actual = new MihomoConfigBuilder().Build(Fixtures.EscapingEdgeCases());
        GoldenMaster.Verify(nameof(Build_EscapingEdgeCases_MatchesGolden), actual);
    }
}

public class SubscriptionConfigComposerGoldenTests
{
    [Fact]
    public void Compose_Basic_MatchesGolden()
    {
        var actual = new SubscriptionConfigComposer().Compose(Fixtures.SubscriptionYaml, Fixtures.ComposeBase());
        GoldenMaster.Verify(nameof(Compose_Basic_MatchesGolden), actual);
    }

    [Fact]
    public void Compose_AllOverrideOperators_MatchesGolden()
    {
        var actual = new SubscriptionConfigComposer()
            .Compose(Fixtures.SubscriptionYaml, Fixtures.ComposeWithOverrides());
        GoldenMaster.Verify(nameof(Compose_AllOverrideOperators_MatchesGolden), actual);
    }
}
