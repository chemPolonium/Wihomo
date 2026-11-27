using Microsoft.UI.Xaml.Controls;

namespace Wihomo.Views;

public sealed partial class DnsSettingsView : UserControl
{
    public MainViewModel ViewModel { get; }

    public DnsSettingsView(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }
}
