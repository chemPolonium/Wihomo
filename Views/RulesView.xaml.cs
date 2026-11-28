using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Wihomo.Views;

public sealed partial class RulesView : UserControl
{
    public MainViewModel ViewModel { get; }

    public RulesView(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    private void RulesSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (RulesPanel is null || OverridesPanel is null || RefreshRulesButton is null)
        {
            return;
        }

        var showOverrides = sender.SelectedItem == OverridesSelectorItem;
        RulesPanel.Visibility = showOverrides ? Visibility.Collapsed : Visibility.Visible;
        OverridesPanel.Visibility = showOverrides ? Visibility.Visible : Visibility.Collapsed;
        RefreshRulesButton.Visibility = showOverrides ? Visibility.Collapsed : Visibility.Visible;
    }

    // WinUI TextBox 没有 AcceptsTab：手动插入制表符，保持 YAML 编辑体验。
    private void OverridesTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Tab)
        {
            return;
        }

        var textBox = OverridesTextBox;
        var start = textBox.SelectionStart;
        textBox.Text = textBox.Text.Insert(start, "\t");
        textBox.SelectionStart = start + 1;
        e.Handled = true;
    }
}
