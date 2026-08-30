using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wihomo.ViewModels;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Wihomo;

/// <summary>
/// 窗口外壳：托盘宿主、关闭即隐藏、ESC 隐藏，以及日志控件的增量追加。
/// 所有业务状态与操作都在 MainViewModel 里，这里不承载逻辑。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>日志控件行数超过该阈值时按视图模型的环形缓冲重建，避免控件无限增长。</summary>
    private const int LogTrimLineCount = 600;

    private readonly MainViewModel _viewModel;
    private readonly Drawing.Icon _applicationIcon;
    private readonly Forms.NotifyIcon _notifyIcon;
    private bool _isExiting;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel(new WpfUiDispatcher(Dispatcher));
        DataContext = _viewModel;

        _applicationIcon = LoadApplicationIcon();
        Icon = LoadWindowIconSource();
        _notifyIcon = CreateNotifyIcon(_applicationIcon);

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;

        _viewModel.Logs.LineAppended += AppendLogLine;
        _viewModel.Logs.Cleared += ResetLogText;
    }

    private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
        ResetLogText();
        _viewModel.Logs.ReplayTo(AppendLogLine);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isExiting)
        {
            e.Cancel = true;
            HideToNotificationArea();
            return;
        }

        _notifyIcon.Dispose();
        _applicationIcon.Dispose();
        _viewModel.PrepareForExit();
        _viewModel.Dispose();
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            HideToNotificationArea();
            e.Handled = true;
        }
    }

    internal void RestoreAndActivate()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CoreStatusText))
        {
            _notifyIcon.Text = _viewModel.TrayText;
        }
    }

    private static string ApplicationIconPath => Path.Combine(AppContext.BaseDirectory, "Wihomo.ico");

    private static Drawing.Icon LoadApplicationIcon() => new(ApplicationIconPath);

    private static ImageSource? LoadWindowIconSource()
    {
        if (!File.Exists(ApplicationIconPath))
        {
            return null;
        }

        return BitmapFrame.Create(new Uri(ApplicationIconPath, UriKind.Absolute));
    }

    private Forms.NotifyIcon CreateNotifyIcon(Drawing.Icon applicationIcon)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => ShowMainWindow());
        menu.Items.Add("启动/重启内核", null, (_, _) => InvokeOnUi(() => _viewModel.StartCoreCommand.Execute(null)));
        menu.Items.Add("停止内核", null, (_, _) => InvokeOnUi(() => _viewModel.StopCoreCommand.Execute(null)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => InvokeOnUi(ExitApplication));

        var notifyIcon = new Forms.NotifyIcon
        {
            Icon = applicationIcon,
            Text = _viewModel.TrayText,
            ContextMenuStrip = menu,
            Visible = true
        };
        notifyIcon.DoubleClick += (_, _) => ShowMainWindow();
        return notifyIcon;
    }

    private void HideToNotificationArea()
    {
        ShowInTaskbar = false;
        Hide();
    }

    private void ShowMainWindow()
    {
        Dispatcher.InvokeAsync(RestoreAndActivate);
    }

    private void InvokeOnUi(Action action)
    {
        Dispatcher.InvokeAsync(action);
    }

    private void ExitApplication()
    {
        _isExiting = true;
        Close();
    }

    private void AppendLogLine(string line)
    {
        LogsTextBox.AppendText(line + Environment.NewLine);
        if (LogsTextBox.LineCount > LogTrimLineCount)
        {
            RebuildLogText();
        }

        LogsTextBox.ScrollToEnd();
    }

    private void ResetLogText()
    {
        LogsTextBox.Clear();
    }

    private void RebuildLogText()
    {
        LogsTextBox.Text = string.Join(Environment.NewLine, _viewModel.Logs.Lines);
        LogsTextBox.CaretIndex = LogsTextBox.Text.Length;
    }
}
