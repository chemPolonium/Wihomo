using Microsoft.UI.Xaml.Controls;

namespace Wihomo.Views;

public sealed partial class SubscriptionsView : UserControl
{
    public MainViewModel ViewModel { get; }

    public SubscriptionsView(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }
}
