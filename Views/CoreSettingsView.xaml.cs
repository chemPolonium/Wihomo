using Microsoft.UI.Xaml.Controls;

namespace Wihomo.Views;

public sealed partial class CoreSettingsView : UserControl
{
    public MainViewModel ViewModel { get; }

    public CoreSettingsView(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }
}
