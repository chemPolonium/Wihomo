using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Wihomo.ViewModels;

namespace Wihomo.Views;

public sealed partial class LogsView : UserControl
{
    private readonly ObservableCollection<string> _lines = [];

    public MainViewModel ViewModel { get; }

    public LogsView(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        LogsList.ItemsSource = _lines;
        viewModel.Logs.LineAppended += AppendLine;
        viewModel.Logs.Cleared += ClearLines;
        viewModel.Logs.ReplayTo(AppendLine);
    }

    private void AppendLine(string line)
    {
        _lines.Add(line);
        while (_lines.Count > LogsViewModel.MaxLines)
        {
            _lines.RemoveAt(0);
        }

        if (_lines.Count > 0)
        {
            LogsList.ScrollIntoView(_lines[^1]);
        }
    }

    private void ClearLines()
    {
        _lines.Clear();
    }

    private void CopyLogs_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, _lines));
        Clipboard.SetContent(package);
    }
}
