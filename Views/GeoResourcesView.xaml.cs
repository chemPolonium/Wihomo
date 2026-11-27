using Microsoft.UI.Xaml.Controls;

namespace Wihomo.Views;

public sealed partial class GeoResourcesView : UserControl
{
    public MainViewModel ViewModel { get; }

    public GeoResourcesView(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }
}
