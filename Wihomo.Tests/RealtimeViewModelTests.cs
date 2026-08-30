using Wihomo.Models;
using Wihomo.Services;
using Wihomo.Services.Realtime;
using Wihomo.ViewModels;

namespace Wihomo.Tests;

public class TelemetryViewModelTests
{
    [Fact]
    public void 速率取自帧的区间字节而非累计值()
    {
        var vm = new TelemetryViewModel();

        vm.Update(new TrafficFrame { Up = 111, Down = 222, UpTotal = 1000, DownTotal = 2000 });

        Assert.Equal($"下载速率: {SettingsParsing.FormatBytes(222)}/s", vm.DownloadRateText);
        Assert.Equal($"上传速率: {SettingsParsing.FormatBytes(111)}/s", vm.UploadRateText);
        Assert.Equal($"累计下载: {SettingsParsing.FormatBytes(2000)}", vm.DownloadTotalText);
        Assert.Equal($"累计上传: {SettingsParsing.FormatBytes(1000)}", vm.UploadTotalText);
    }

    [Fact]
    public void 连接快照新增行并在第二帧算出速率()
    {
        var vm = new TelemetryViewModel();

        vm.Update(Frame(Connection("a", download: 1000, upload: 500)));

        var row = Assert.Single(vm.Connections);
        Assert.Equal("a", row.Id);
        var initialSpeed = row.Speed;
        Assert.StartsWith("↑ ", initialSpeed);
        Assert.Equal($"↑ {SettingsParsing.FormatBytes(0)}/s ↓ {SettingsParsing.FormatBytes(0)}/s", initialSpeed);
        Assert.Equal("127.0.0.1:54321", row.Source);
        Assert.Equal("www.example.com (1.2.3.4:443)", row.Destination);
        Assert.Equal("Tunnel", row.Type);
        Assert.Equal("节点-B", row.UsedProxy);
        Assert.Equal("MATCH", row.Rule);

        vm.Update(Frame(Connection("a", download: 3000, upload: 500)));

        row = Assert.Single(vm.Connections);
        Assert.NotEqual(initialSpeed, row.Speed);
        Assert.DoesNotContain("↓ " + SettingsParsing.FormatBytes(0) + "/s", row.Speed);
    }

    [Fact]
    public void 连接消失后行被移除且不误删其它行()
    {
        var vm = new TelemetryViewModel();

        vm.Update(Frame(Connection("a"), Connection("b")));
        Assert.Equal(2, vm.Connections.Count);

        vm.Update(Frame(Connection("b")));

        var row = Assert.Single(vm.Connections);
        Assert.Equal("b", row.Id);
        Assert.Equal("当前连接数: 1", vm.ConnectionsText);
    }

    [Fact]
    public void 无连接与内核停止时显示占位行()
    {
        var vm = new TelemetryViewModel();

        vm.Update(new ConnectionsFrame());
        Assert.Equal("暂无当前连接", Assert.Single(vm.Connections).Source);

        vm.Update(Frame(Connection("a")));
        Assert.Single(vm.Connections);

        vm.ShowCoreStopped();
        var placeholder = Assert.Single(vm.Connections);
        Assert.Equal("内核未运行", placeholder.Source);
        Assert.Equal("当前连接数: -", vm.ConnectionsText);
    }

    [Fact]
    public void 缺少id的连接按端点配对不会每帧重复建行()
    {
        var vm = new TelemetryViewModel();

        vm.Update(Frame(Connection(string.Empty, download: 10)));
        vm.Update(Frame(Connection(string.Empty, download: 20)));

        Assert.Single(vm.Connections);
    }

    [Fact]
    public void 代理链为空的连接显示破折号()
    {
        var vm = new TelemetryViewModel();

        vm.Update(Frame(new CoreConnection
        {
            Id = "x",
            Chains = [],
            Rule = string.Empty,
            Metadata = new ConnectionMetadata { Network = "tcp", Type = string.Empty, SourceIp = "127.0.0.1", SourcePort = "1" },
        }));

        var row = Assert.Single(vm.Connections);
        Assert.Equal("-", row.UsedProxy);
        Assert.Equal("-", row.Rule);
        Assert.Equal("tcp", row.Type);
        Assert.Equal("127.0.0.1:1", row.Source);
        Assert.Equal("-", row.Destination);
    }

    [Fact]
    public void 运行时指示文本与迁移前一致()
    {
        var vm = new TelemetryViewModel();

        vm.UpdateRuntimeIndicators(true, 7890, true, "system");
        Assert.Equal("系统代理: 已配置为启动时启用 (7890)", vm.SystemProxyStatusText);
        Assert.Equal("TUN: 已启用 (system)", vm.TunStatusText);

        vm.UpdateRuntimeIndicators(false, 7890, false, "mixed");
        Assert.Equal("系统代理: 未启用", vm.SystemProxyStatusText);
        Assert.Equal("TUN: 未启用", vm.TunStatusText);
    }

    private static ConnectionsFrame Frame(params CoreConnection[] connections) => new() { Connections = connections };

    private static CoreConnection Connection(
        string id,
        long download = 0,
        long upload = 0) => new()
    {
        Id = id,
        Download = download,
        Upload = upload,
        Chains = ["节点-A", "节点-B"],
        Rule = "MATCH",
        Metadata = new ConnectionMetadata
        {
            Network = "tcp",
            Type = "Tunnel",
            SourceIp = "127.0.0.1",
            SourcePort = "54321",
            Host = "www.example.com",
            DestinationIp = "1.2.3.4",
            DestinationPort = "443",
        },
    };
}

public class ProxyGroupsViewModelTests
{
    [Fact]
    public void 刷新重排后仍按名字保留选中组()
    {
        var vm = new ProxyGroupsViewModel();
        vm.Replace([Group("a"), Group("b"), Group("c")], _ => "未测试", showCurrentInHeader: true);

        vm.SelectedGroup = vm.Groups[1];
        Assert.Equal("b", vm.SelectedGroup!.Name);

        vm.Replace([Group("c"), Group("a"), Group("b")], _ => "未测试", showCurrentInHeader: true);

        Assert.Equal("b", vm.SelectedGroup!.Name);
        Assert.Equal("当前组: b (select)", vm.SelectedGroupStatusText);
    }

    [Fact]
    public void 选中组消失后回落到第一项而不是保留越界索引()
    {
        var vm = new ProxyGroupsViewModel();
        vm.Replace([Group("a"), Group("b")], _ => "未测试", showCurrentInHeader: true);
        vm.SelectedGroup = vm.Groups[1];

        vm.Replace([Group("x"), Group("a")], _ => "未测试", showCurrentInHeader: true);

        Assert.Equal("x", vm.SelectedGroup!.Name);
    }

    [Fact]
    public void 成员列表携带延迟并选中当前节点()
    {
        var vm = new ProxyGroupsViewModel();
        var info = Group("a", current: "n2", options: ["n1", "n2"]);

        vm.Replace([info], name => name == "n2" ? "88 ms" : "失败", showCurrentInHeader: true);

        var group = vm.Groups[0];
        Assert.Equal(["n1", "n2"], group.Members.Select(x => x.NodeName));
        Assert.Equal("失败", group.Members[0].Delay);
        Assert.Equal("88 ms", group.Members[1].Delay);
        Assert.Same(group.Members[1], vm.SelectedMember);
        Assert.Equal("当前选择: n2", vm.CurrentSelectionText);
        Assert.Equal("a [select] -> n2", group.Display);
    }

    [Fact]
    public void 离线缓存表头不显示当前节点()
    {
        var vm = new ProxyGroupsViewModel();

        vm.Replace([Group("a", current: "n2")], _ => "未测试", showCurrentInHeader: false);

        Assert.Equal("a [select]", vm.Groups[0].Display);
    }

    [Fact]
    public void 测速结果刷新保持所选节点()
    {
        var vm = new ProxyGroupsViewModel();
        vm.Replace([Group("a", current: "n1", options: ["n1", "n2"])], _ => "未测试", showCurrentInHeader: true);

        vm.SelectedMember = vm.Groups[0].Members[1];
        vm.RefreshDelays(name => name == "n1" ? "10 ms" : "20 ms");

        Assert.Equal("20 ms", vm.SelectedMember!.Delay);
        Assert.Equal("n2", vm.SelectedMember.NodeName);
        Assert.Equal("10 ms", vm.Groups[0].Members[0].Delay);
    }

    [Fact]
    public void 清空后不残留选中状态()
    {
        var vm = new ProxyGroupsViewModel();
        vm.Replace([Group("a")], _ => "未测试", showCurrentInHeader: true);

        vm.Clear();

        Assert.Empty(vm.Groups);
        Assert.Null(vm.SelectedGroup);
        Assert.Null(vm.SelectedMember);
        Assert.Equal("当前组: -", vm.SelectedGroupStatusText);
        Assert.Equal("当前选择: -", vm.CurrentSelectionText);
    }

    private static ProxyGroupInfo Group(string name, string current = "", params string[] options) => new()
    {
        Name = name,
        Type = "select",
        Current = string.IsNullOrEmpty(current) && options.Length > 0 ? options[0] : current,
        Options = options.Length > 0 ? [.. options] : [name + "-only"],
    };
}

public class SubscriptionsViewModelTests
{
    [Fact]
    public void 选中行按名字恢复并回填编辑框()
    {
        var settings = new AppSettings();
        settings.Subscriptions.Add(new SubscriptionItem { Name = "机场A", Url = "https://a/sub", IntervalSeconds = 100 });
        settings.Subscriptions.Add(new SubscriptionItem { Name = "机场B", Url = "https://b/sub", IntervalSeconds = 200 });

        var vm = new SubscriptionsViewModel();
        vm.Load(settings);
        vm.SelectedSubscription = vm.Items[1];

        Assert.Equal("https://b/sub", vm.EditUrl);
        Assert.Equal("机场B", vm.EditName);
        Assert.Equal("200", vm.EditIntervalText);

        // 重新 Load 会换成全新的行对象，选中项必须靠名字而不是索引找回。
        var reordered = new AppSettings();
        reordered.Subscriptions.Add(new SubscriptionItem { Name = "机场B", Url = "https://b/sub", IntervalSeconds = 200 });
        reordered.Subscriptions.Add(new SubscriptionItem { Name = "机场A", Url = "https://a/sub", IntervalSeconds = 100 });
        vm.Load(reordered);

        Assert.Equal("机场B", vm.SelectedSubscription!.Name);
        Assert.Equal(0, vm.Items.IndexOf(vm.SelectedSubscription));
    }

    [Fact]
    public void 编辑中的订阅地址在刷新后不被回填覆盖()
    {
        var settings = new AppSettings();
        settings.Subscriptions.Add(new SubscriptionItem { Name = "机场A", Url = "https://a/sub", IntervalSeconds = 100 });

        var vm = new SubscriptionsViewModel();
        vm.Load(settings);
        vm.SelectedSubscription = vm.Items[0];
        vm.EditUrl = "https://new/sub";

        vm.Load(settings);

        Assert.Equal("https://new/sub", vm.EditUrl);
        Assert.Equal("机场A", vm.EditName);
    }

    [Fact]
    public void 行文本是底层项的投影_刷新后跟随变化()
    {
        var item = new SubscriptionItem
        {
            Name = "机场A",
            Enabled = true,
            UploadBytes = 1024,
            DownloadBytes = 2048,
            TotalBytes = 0,
        };
        var settings = new AppSettings();
        settings.Subscriptions.Add(item);

        var vm = new SubscriptionsViewModel();
        vm.Load(settings);
        var row = vm.Items[0];

        Assert.Equal("★ 活动", row.Status);
        Assert.Equal(SettingsParsing.FormatBytes(3072), row.Used);
        Assert.Equal("-", row.Total);
        Assert.Equal("-", row.Expires);

        item.Enabled = false;
        item.TotalBytes = 1024 * 1024;
        item.ExpireAt = DateTimeOffset.UnixEpoch;
        vm.RefreshCells();

        var refreshed = Assert.Single(vm.Items);
        Assert.Equal("停用", refreshed.Status);
        Assert.Equal(SettingsParsing.FormatBytes(1024 * 1024), refreshed.Total);
        Assert.Equal("1970-01-01", refreshed.Expires);
    }

    [Fact]
    public void 选中项被删除后不会越界()
    {
        var settings = new AppSettings();
        settings.Subscriptions.Add(new SubscriptionItem { Name = "只此一家", Url = "https://a/sub" });

        var vm = new SubscriptionsViewModel();
        vm.Load(settings);
        vm.SelectedSubscription = vm.Items[0];

        settings.Subscriptions.Clear();
        vm.Load(settings);

        Assert.Empty(vm.Items);
        Assert.Null(vm.SelectedSubscription);
    }
}

public class RulesAndLogsViewModelTests
{
    [Fact]
    public void 规则列表在无数据时显示占位文本()
    {
        var vm = new RulesViewModel();

        vm.SetSubscriptionRules([]);
        Assert.Equal(["暂无订阅规则"], vm.SubscriptionRules);

        vm.SetSubscriptionRules(["GEOSITE,cn,DIRECT", "MATCH,PROXY"]);
        Assert.Equal(["GEOSITE,cn,DIRECT", "MATCH,PROXY"], vm.SubscriptionRules);

        vm.SetActiveRulesError("连接被拒绝");
        Assert.Equal(["读取规则失败: 连接被拒绝"], vm.ActiveRules);

        vm.ShowCoreStopped();
        Assert.Equal(["内核未运行"], vm.ActiveRules);
    }

    [Fact]
    public void 活动规则为空时显示占位而不是清空()
    {
        var vm = new RulesViewModel();

        vm.SetActiveRules([]);

        Assert.Equal(["暂无当前规则"], vm.ActiveRules);
    }

    [Fact]
    public void 日志按行增量投递并限制上限()
    {
        var vm = new LogsViewModel();
        var delivered = new List<string>();
        vm.LineAppended += line => delivered.Add(line);

        for (var i = 0; i < 505; i++)
        {
            vm.Append($"line-{i}");
        }

        Assert.Equal(500, vm.Lines.Count);
        Assert.Equal(505, delivered.Count);
        Assert.Contains("line-5", vm.Lines[0]);
        Assert.DoesNotContain("line-4", vm.Lines[0]);
        Assert.EndsWith("line-504", vm.Lines[^1]);
    }

    [Fact]
    public void 重放只补齐历史且清空后不再重放()
    {
        var vm = new LogsViewModel();
        vm.Append("第一条");
        vm.Append("第二条");

        var replayed = new List<string>();
        vm.ReplayTo(replayed.Add);
        Assert.Equal(2, replayed.Count);
        Assert.Contains("第一条", replayed[0]);

        vm.Clear();
        replayed.Clear();
        vm.ReplayTo(replayed.Add);
        Assert.Empty(replayed);

        vm.Append("清空之后");
        Assert.Single(vm.Lines);
    }
}
