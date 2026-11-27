using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Wihomo.Views;

public sealed partial class YamlOverridesView : UserControl
{
    public MainViewModel ViewModel { get; }

    public YamlOverridesView(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
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
