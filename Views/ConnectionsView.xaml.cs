using Microsoft.UI.Xaml.Controls;

namespace Wihomo.Views;

public sealed partial class ConnectionsView : UserControl
{
    public MainViewModel ViewModel { get; }

    public ConnectionsView(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }
}
