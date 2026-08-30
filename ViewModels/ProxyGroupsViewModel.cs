using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using Wihomo.Models;

namespace Wihomo.ViewModels;

public sealed partial class ProxyMemberViewModel(string nodeName) : ObservableObject
{
    public string NodeName { get; } = nodeName;

    [ObservableProperty] private string _delay = "未测试";
}

public sealed class ProxyGroupViewModel(ProxyGroupInfo info, bool showCurrentInHeader)
{
    public ProxyGroupInfo Info { get; } = info;

    public string Name => Info.Name;

    public string Type => Info.Type;

    public string Current => Info.Current;

    public ObservableCollection<ProxyMemberViewModel> Members { get; } = [];

    /// <summary>内核在线时展示当前节点，离线读订阅缓存时没有可信的当前值。</summary>
    public string Display => showCurrentInHeader
        ? $"{Name} [{Type}] -> {Current}"
        : $"{Name} [{Type}]";

    public ProxyMemberViewModel? LoadMembers(IEnumerable<string> options, Func<string, string> delayOf, string? preferred)
    {
        Members.Clear();
        foreach (var option in options)
        {
            Members.Add(new ProxyMemberViewModel(option) { Delay = delayOf(option) });
        }

        var target = preferred ?? Current;
        return Members.FirstOrDefault(x => string.Equals(x.NodeName, target, StringComparison.Ordinal));
    }
}

/// <summary>
/// 代理组与成员。选择状态按组对象身份保存，刷新导致的重排不会再丢失用户选中项。
/// </summary>
public sealed partial class ProxyGroupsViewModel : ObservableObject
{
    [ObservableProperty] private ProxyGroupViewModel? _selectedGroup;
    [ObservableProperty] private ProxyMemberViewModel? _selectedMember;
    [ObservableProperty] private string _selectedGroupStatusText = "当前组: -";
    [ObservableProperty] private string _currentSelectionText = "当前选择: -";
    [ObservableProperty] private string _allDelayStatusText = "等待测速";
    [ObservableProperty] private string _delayTestUrl = "http://cp.cloudflare.com/generate_204";
    [ObservableProperty] private string _delayTestTimeoutText = "5000";

    private Func<string, string> _delayOf = _ => "未测试";

    public ObservableCollection<ProxyGroupViewModel> Groups { get; } = [];

    public void Replace(IReadOnlyList<ProxyGroupInfo> groups, Func<string, string> delayOf, bool showCurrentInHeader)
    {
        var selectedName = SelectedGroup?.Name;
        _delayOf = delayOf;

        Groups.Clear();
        foreach (var group in groups)
        {
            Groups.Add(new ProxyGroupViewModel(group, showCurrentInHeader));
        }

        SelectedGroup = Groups.FirstOrDefault(x => string.Equals(x.Name, selectedName, StringComparison.Ordinal))
            ?? Groups.FirstOrDefault();
    }

    public void Clear()
    {
        Groups.Clear();
        _delayOf = _ => "未测试";
        SelectedGroup = null;
        SelectedMember = null;
        SelectedGroupStatusText = "当前组: -";
        CurrentSelectionText = "当前选择: -";
    }

    /// <summary>测速结果落地后刷新当前组的延迟列，并保持所选节点不变。</summary>
    public void RefreshDelays(Func<string, string> delayOf)
    {
        _delayOf = delayOf;
        if (SelectedGroup is null)
        {
            return;
        }

        SelectedMember = SelectedGroup.LoadMembers(SelectedGroup.Info.Options, delayOf, SelectedMember?.NodeName);
    }

    partial void OnSelectedGroupChanged(ProxyGroupViewModel? value)
    {
        if (value is null)
        {
            SelectedMember = null;
            SelectedGroupStatusText = "当前组: -";
            CurrentSelectionText = "当前选择: -";
            return;
        }

        SelectedGroupStatusText = $"当前组: {value.Name} ({value.Type})";
        CurrentSelectionText = $"当前选择: {value.Current}";
        SelectedMember = value.LoadMembers(value.Info.Options, _delayOf, preferred: null);
    }
}
