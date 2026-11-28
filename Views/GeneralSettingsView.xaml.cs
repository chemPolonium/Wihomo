using Microsoft.UI.Xaml.Controls;

namespace Wihomo.Views;

public sealed partial class GeneralSettingsView : UserControl
{
    public MainViewModel ViewModel { get; }

    public GeneralSettingsView(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }
}
