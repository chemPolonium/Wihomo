using Wihomo.Models;
using Wihomo.ViewModels;

namespace Wihomo.Tests;

public class SettingsViewModelTests
{
    [Fact]
    public void LoadFrom_Then_ApplyTo_保留全部已暴露字段()
    {
        var source = NewPopulatedSettings();

        var vm = new SettingsViewModel();
        vm.LoadFrom(source);

        var target = new AppSettings();
        vm.ApplyTo(target);

        Assert.Equal("C:\\core\\mihomo.exe", target.Core.CoreExecutablePath);
        Assert.Equal("C:\\work", target.Core.WorkingDirectory);
        Assert.Equal("127.0.0.1", target.Core.ExternalControllerHost);
        Assert.Equal(19090, target.Core.ExternalControllerPort);
        Assert.Equal(17890, target.Core.MixedPort);
        Assert.Equal(17891, target.Core.SocksPort);
        Assert.Equal(17892, target.Core.HttpPort);
        Assert.Equal("sekret", target.Core.Secret);
        Assert.True(target.Core.EnableSystemProxy);
        Assert.True(target.Core.EnableTun);
        Assert.Equal("gvisor", target.Core.TunStack);
        Assert.Equal(3, target.StatsRefreshSeconds);
        Assert.False(target.StartCoreOnProgramStart);
        Assert.True(target.StartWithWindows);
        Assert.Equal("https://geo/geoip.dat", target.GeoxUrls.GeoIp);
        Assert.Equal("https://geo/geosite.dat", target.GeoxUrls.GeoSite);
        Assert.Equal("https://geo/country.mmdb", target.GeoxUrls.Mmdb);
        Assert.Equal("https://geo/asn.mmdb", target.GeoxUrls.Asn);
        Assert.True(target.GeoDataMode);
        Assert.Equal(12, target.GeoUpdateIntervalHours);
        Assert.Equal("rules:\n  - MATCH,DIRECT", target.RuleOverrides);
        Assert.True(target.Dns.Enable);
        Assert.Equal("0.0.0.0:53", target.Dns.Listen);
        Assert.Equal("redir-host", target.Dns.EnhancedMode);
        Assert.Equal(["223.5.5.5", "119.29.29.29"], target.Dns.DefaultNameserver);
        Assert.Equal(["https://dns.alidns.com/dns-query"], target.Dns.Nameserver);
        Assert.Equal(["https://dns.google/dns-query", "8.8.8.8"], target.Dns.Fallback);
    }

    [Fact]
    public void ApplyTo_逐项写回文本框与设置的映射()
    {
        var vm = new SettingsViewModel();
        vm.LoadFrom(NewPopulatedSettings());

        Assert.Equal("19090", vm.ControllerPortText);
        Assert.Equal("gvisor", vm.SelectedTunStack.Value);
        Assert.Equal("DAT", vm.SelectedGeoDataMode.Display);
        Assert.Equal("redir-host", vm.SelectedDnsEnhancedMode.Display);
        Assert.Equal(string.Join(Environment.NewLine, ["223.5.5.5", "119.29.29.29"]), vm.DefaultNameserverText);
    }

    [Fact]
    public void ApplyTo_校验失败时不留下半新半旧的设置()
    {
        var settings = new AppSettings();
        settings.Core.CoreExecutablePath = "C:\\原\\mihomo.exe";
        var vm = new SettingsViewModel();
        vm.LoadFrom(settings);

        vm.CorePath = "D:\\其它\\内核.exe";
        vm.MixedPortText = "不是数字";

        Assert.Throws<InvalidOperationException>(() => vm.ApplyTo(settings));

        Assert.Equal("C:\\原\\mihomo.exe", settings.Core.CoreExecutablePath);
        Assert.Equal(7890, settings.Core.MixedPort);
    }

    [Theory]
    [InlineData("core", "内核路径不能为空。")]
    [InlineData("workdir", "工作目录不能为空。")]
    [InlineData("host", "External Controller Host 不能为空。")]
    public void ApplyTo_必填项为空时给出可定位的错误(string field, string expectedMessage)
    {
        var settings = new AppSettings();
        settings.Core.CoreExecutablePath = "C:\\原\\mihomo.exe";
        settings.Core.WorkingDirectory = "C:\\原\\工作目录";
        settings.Core.ExternalControllerHost = "127.0.0.1";
        var vm = new SettingsViewModel();
        vm.LoadFrom(settings);

        switch (field)
        {
            case "core":
                vm.CorePath = string.Empty;
                break;
            case "workdir":
                vm.CoreWorkDir = "   ";
                break;
            default:
                vm.ControllerHost = " ";
                break;
        }

        var error = Assert.Throws<InvalidOperationException>(() => vm.ApplyTo(settings));
        Assert.Equal(expectedMessage, error.Message);
    }

    [Fact]
    public void ApplyTo_保留未在界面暴露的DNS字段()
    {
        var settings = NewPopulatedSettings();
        settings.Dns.FakeIpFilter = ["*.lan"];
        settings.Dns.Ipv6 = false;
        settings.Dns.FakeIpRange = "198.20.0.1/16";
        settings.Dns.FallbackFilterGeoIp = false;
        settings.Dns.FallbackFilterGeoIpCode = "HK";

        var vm = new SettingsViewModel();
        vm.LoadFrom(settings);
        vm.ApplyTo(settings);

        Assert.Equal(["*.lan"], settings.Dns.FakeIpFilter);
        Assert.False(settings.Dns.Ipv6);
        Assert.Equal("198.20.0.1/16", settings.Dns.FakeIpRange);
        Assert.False(settings.Dns.FallbackFilterGeoIp);
        Assert.Equal("HK", settings.Dns.FallbackFilterGeoIpCode);
    }

    [Fact]
    public void 加载不脏_编辑变脏_保存后重新干净()
    {
        var settings = NewPopulatedSettings();
        var vm = new SettingsViewModel();

        vm.LoadFrom(settings);
        Assert.False(vm.IsDirty);

        vm.MixedPortText = "7899";
        Assert.True(vm.IsDirty);

        vm.ApplyTo(settings);
        Assert.False(vm.IsDirty);
        Assert.Equal(7899, settings.Core.MixedPort);
    }

    [Fact]
    public void 未知的TunStack_回落为mixed()
    {
        // 记录在案的既有缺陷：内核接受大小写混合的 stack 名，界面却按 Ordinal 精确匹配。
        // 迁移前 SelectTunStack 的 switch 同样把无法识别的值归为 mixed，这里锁定行为未被改坏。
        var settings = new AppSettings();
        settings.Core.TunStack = "System";

        var vm = new SettingsViewModel();
        vm.LoadFrom(settings);

        Assert.Equal("mixed", vm.SelectedTunStack.Value);
        Assert.Equal("mixed", vm.SelectedTunStack.Display);
    }

    [Fact]
    public void 端口为负数时拒绝写回()
    {
        var settings = NewPopulatedSettings();
        var vm = new SettingsViewModel();
        vm.LoadFrom(settings);
        vm.SocksPortText = "-1";

        Assert.Throws<InvalidOperationException>(() => vm.ApplyTo(settings));
        Assert.Equal(17891, settings.Core.SocksPort);
    }

    private static AppSettings NewPopulatedSettings()
    {
        var settings = new AppSettings
        {
            StatsRefreshSeconds = 3,
            StartCoreOnProgramStart = false,
            StartWithWindows = true,
            GeoDataMode = true,
            GeoAutoUpdate = true,
            GeoUpdateIntervalHours = 12,
            RuleOverrides = "rules:\n  - MATCH,DIRECT",
        };

        settings.Core.CoreExecutablePath = "C:\\core\\mihomo.exe";
        settings.Core.WorkingDirectory = "C:\\work";
        settings.Core.ExternalControllerHost = "127.0.0.1";
        settings.Core.ExternalControllerPort = 19090;
        settings.Core.MixedPort = 17890;
        settings.Core.SocksPort = 17891;
        settings.Core.HttpPort = 17892;
        settings.Core.Secret = "sekret";
        settings.Core.EnableSystemProxy = true;
        settings.Core.EnableTun = true;
        settings.Core.TunStack = "gvisor";

        settings.GeoxUrls.GeoIp = "https://geo/geoip.dat";
        settings.GeoxUrls.GeoSite = "https://geo/geosite.dat";
        settings.GeoxUrls.Mmdb = "https://geo/country.mmdb";
        settings.GeoxUrls.Asn = "https://geo/asn.mmdb";

        settings.Dns.Enable = true;
        settings.Dns.Listen = "0.0.0.0:53";
        settings.Dns.EnhancedMode = "redir-host";
        settings.Dns.DefaultNameserver = ["223.5.5.5", "119.29.29.29"];
        settings.Dns.Nameserver = ["https://dns.alidns.com/dns-query"];
        settings.Dns.Fallback = ["https://dns.google/dns-query", "8.8.8.8"];

        return settings;
    }
}
