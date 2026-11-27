using Microsoft.UI.Xaml.Controls;

namespace Wihomo.Views;

public sealed partial class RulesView : UserControl
{
    public MainViewModel ViewModel { get; }

    public RulesView(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }
}
