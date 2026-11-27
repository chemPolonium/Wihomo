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
}
