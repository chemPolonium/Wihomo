using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wihomo.Views;

public sealed partial class ProxyGroupsView : UserControl
{
    public MainViewModel ViewModel { get; }

    public ProxyGroupsView(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    private async void DelaySettings_Click(object sender, RoutedEventArgs e)
    {
        DelaySettingsDialog.XamlRoot = Root.XamlRoot;
        await DelaySettingsDialog.ShowAsync();
    }
}
