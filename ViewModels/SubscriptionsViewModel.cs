using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using Wihomo.Models;
using Wihomo.Services;

namespace Wihomo.ViewModels;

public sealed class SubscriptionRowViewModel(SubscriptionItem item)
{
    public SubscriptionItem Item { get; } = item;

    public string Status => Item.Enabled ? "★ 活动" : "停用";

    public string Name => Item.Name;

    public string Used => SettingsParsing.FormatBytes(Item.UploadBytes + Item.DownloadBytes);

    public string Total => Item.TotalBytes > 0 ? SettingsParsing.FormatBytes(Item.TotalBytes) : "-";

    public string Expires => Item.ExpireAt?.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture) ?? "-";
}

/// <summary>
/// 订阅列表与编辑区。选中项按对象身份而非列表索引回查，
/// 因此刷新导致的重排或改名不会再让选中项错位。
/// </summary>
public sealed partial class SubscriptionsViewModel : ObservableObject
{
    [ObservableProperty] private SubscriptionRowViewModel? _selectedSubscription;
    [ObservableProperty] private string _editName = string.Empty;
    [ObservableProperty] private string _editUrl = string.Empty;
    [ObservableProperty] private string _editIntervalText = "3600";

    public ObservableCollection<SubscriptionRowViewModel> Items { get; } = [];

    private bool _isRestoringSelection;

    public void Load(AppSettings settings)
    {
        var selectedName = SelectedSubscription?.Name;
        var isEditing = IsEditingExisting(selectedName);

        RestoreRows(settings.Subscriptions, selectedName);

        if (!isEditing)
        {
            EditName = string.Empty;
            EditUrl = string.Empty;
        }
    }

    /// <summary>行的显示值是从 SubscriptionItem 投影出来的只读快照，值变化后按行重建以刷新 DataGrid。</summary>
    public void RefreshCells()
    {
        var selectedName = SelectedSubscription?.Name;
        RestoreRows(Items.Select(x => x.Item).ToList(), selectedName);
    }

    private void RestoreRows(IEnumerable<SubscriptionItem> subscriptions, string? selectedName)
    {
        _isRestoringSelection = true;
        try
        {
            Items.Clear();
            foreach (var subscription in subscriptions)
            {
                Items.Add(new SubscriptionRowViewModel(subscription));
            }

            SelectedSubscription = FindByName(selectedName);
        }
        finally
        {
            _isRestoringSelection = false;
        }
    }

    public void AddNew(SubscriptionItem subscription)
    {
        Items.Add(new SubscriptionRowViewModel(subscription));
    }

    public SubscriptionRowViewModel? FindByName(string? name)
    {
        if (name is null)
        {
            return null;
        }

        return Items.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsEditingExisting(string? selectedName)
    {
        return selectedName is not null
            && string.Equals(EditName, selectedName, StringComparison.Ordinal);
    }

    partial void OnSelectedSubscriptionChanged(SubscriptionRowViewModel? value)
    {
        if (value is null || _isRestoringSelection)
        {
            return;
        }

        EditName = value.Item.Name;
        EditUrl = value.Item.Url;
        EditIntervalText = value.Item.IntervalSeconds.ToString(CultureInfo.InvariantCulture);
    }
}
