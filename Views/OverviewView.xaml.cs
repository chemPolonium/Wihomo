using Microsoft.UI.Xaml.Controls;

namespace Wihomo.Views;

public sealed partial class OverviewView : UserControl
{
    public MainViewModel ViewModel { get; }

    public string AppVersionText { get; } =
        $"程序版本: {typeof(App).Assembly.GetName().Version?.ToString(3) ?? "未知"}";

    public OverviewView(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }
}
