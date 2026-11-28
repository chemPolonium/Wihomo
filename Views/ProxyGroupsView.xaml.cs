using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wihomo.ViewModels;

namespace Wihomo.Views;

public sealed partial class ProxyGroupsView : UserControl
{
    private bool _isDelaySettingsOpen;

    public MainViewModel ViewModel { get; }

    public ProxyGroupsView(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    private async void DelaySettings_Click(object sender, RoutedEventArgs e)
    {
        if (_isDelaySettingsOpen)
        {
            return;
        }

        _isDelaySettingsOpen = true;
        try
        {
            DelaySettingsDialog.XamlRoot = Root.XamlRoot;
            await DelaySettingsDialog.ShowAsync();
            await ViewModel.SavePreferencesNowAsync();
        }
        finally
        {
            _isDelaySettingsOpen = false;
        }
    }

    private void MemberList_ItemClick(object sender, ItemClickEventArgs e)
    {
        _ = ViewModel.SelectProxyMemberAsync(e.ClickedItem as ProxyMemberViewModel);
    }
}
