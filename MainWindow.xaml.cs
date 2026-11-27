using CommunityToolkit.Mvvm.Input;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Wihomo.Services;
using Wihomo.ViewModels;
using Wihomo.Views;
using Windows.Graphics;
using Windows.UI;

namespace Wihomo;

/// <summary>
/// 窗口外壳：导航、托盘宿主、关闭即隐藏、ESC 隐藏，以及全局状态栏。
/// 所有业务状态与操作都在 MainViewModel 里，这里不承载逻辑。
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int DefaultWidth = 1120;
    private const int DefaultHeight = 760;
    private const int MinimumWidth = 880;
    private const int MinimumHeight = 620;

    private readonly Dictionary<string, UserControl> _views = new(StringComparer.Ordinal);
    private bool _isExiting;
    private bool _uiDisposed;
    private bool _initialSizeApplied;

    public MainViewModel ViewModel { get; }

    public string DisplayName { get; } = AppInstance.DisplayName;

    public IRelayCommand ShowWindowCommand { get; }

    public IRelayCommand ExitCommand { get; }

    public MainWindow()
    {
        ShowWindowCommand = new RelayCommand(ShowMainWindow);
        ExitCommand = new RelayCommand(ExitApplication);

        ViewModel = new MainViewModel(new WinUiDispatcher(DispatcherQueue));
        InitializeComponent();

        // 并行调试实例的标题、标题栏和托盘提示都带后缀，避免和旧版混淆。
        Title = AppInstance.DisplayName;

        ConfigureWindow();
        ConfigureTitleBar();

        AppWindow.Closing += AppWindow_Closing;
        Root.Loaded += Root_Loaded;

        Navigation.SelectedItem = Navigation.MenuItems[0];
    }

    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyInitialSize();
        await ViewModel.InitializeAsync();
    }

    /// <summary>
    /// 窗口显示前 GetDpiForWindow 拿不到 DPI，因此初始尺寸推迟到 XamlRoot 可用后
    /// 按 RasterizationScale 计算，并夹到当前显示器工作区内，避免 200% 缩放下窗口超出屏幕。
    /// </summary>
    private void ApplyInitialSize()
    {
        if (_initialSizeApplied)
        {
            return;
        }

        _initialSizeApplied = true;

        var scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var margin = (int)(24 * scale);
        var maxWidth = Math.Max(320, work.Width - margin * 2);
        var maxHeight = Math.Max(240, work.Height - margin * 2);

        var width = Math.Min((int)(DefaultWidth * scale), maxWidth);
        var height = Math.Min((int)(DefaultHeight * scale), maxHeight);
        width = Math.Max(width, Math.Min((int)(MinimumWidth * scale), maxWidth));
        height = Math.Max(height, Math.Min((int)(MinimumHeight * scale), maxHeight));

        AppWindow.Resize(new SizeInt32(width, height));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Math.Min((int)(MinimumWidth * scale), maxWidth);
            presenter.PreferredMinimumHeight = Math.Min((int)(MinimumHeight * scale), maxHeight);
        }

        var x = work.X + Math.Max(0, (work.Width - width) / 2);
        var y = work.Y + Math.Max(0, (work.Height - height) / 2);
        AppWindow.Move(new PointInt32(x, y));
    }

    private void ConfigureWindow()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Branding", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
    }

    private void ConfigureTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        if (!MicaController.IsSupported())
        {
            SystemBackdrop = DesktopAcrylicController.IsSupported()
                ? new DesktopAcrylicBackdrop()
                : null;

            if (SystemBackdrop is null)
            {
                Root.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
            }
        }

        ApplyTitleBarTheme();
        Root.ActualThemeChanged += (_, _) => ApplyTitleBarTheme();
    }

    private void ApplyTitleBarTheme()
    {
        var foreground = Root.ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black;
        AppWindow.TitleBar.ButtonForegroundColor = foreground;
        AppWindow.TitleBar.ButtonHoverForegroundColor = foreground;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Root.ActualTheme == ElementTheme.Dark
            ? Color.FromArgb(24, 255, 255, 255)
            : Color.FromArgb(24, 0, 0, 0);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        Navigation.IsPaneOpen = !Navigation.IsPaneOpen;
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }

        PageHost.Content = GetView(tag);
    }

    private UserControl GetView(string tag)
    {
        if (_views.TryGetValue(tag, out var view))
        {
            return view;
        }

        view = tag switch
        {
            "overview" => new OverviewView(ViewModel),
            "subscriptions" => new SubscriptionsView(ViewModel),
            "proxy-groups" => new ProxyGroupsView(ViewModel),
            "rules" => new RulesView(ViewModel),
            "connections" => new ConnectionsView(ViewModel),
            "logs" => new LogsView(ViewModel),
            "geo-resources" => new GeoResourcesView(ViewModel),
            "yaml-overrides" => new YamlOverridesView(ViewModel),
            "core-settings" => new CoreSettingsView(ViewModel),
            "dns-settings" => new DnsSettingsView(ViewModel),
            _ => throw new ArgumentOutOfRangeException(nameof(tag), tag, "Unknown navigation tag."),
        };

        _views[tag] = view;
        return view;
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isExiting)
        {
            return;
        }

        args.Cancel = true;
        HideToNotificationArea();
    }

    private void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        HideToNotificationArea();
        args.Handled = true;
    }

    internal void RestoreAndActivate()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        Activate();
    }

    private void HideToNotificationArea()
    {
        AppWindow.Hide();
    }

    private void ShowMainWindow()
    {
        DispatcherQueue.TryEnqueue(RestoreAndActivate);
    }

    private void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        DisposeUi();
        Close();
    }

    private void DisposeUi()
    {
        if (_uiDisposed)
        {
            return;
        }

        _uiDisposed = true;
        TrayIcon.Dispose();
        ViewModel.PrepareForExit();
        ViewModel.Dispose();
    }
}
