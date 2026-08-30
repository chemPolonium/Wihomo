using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using Wihomo.Models;
using Wihomo.Services.Realtime;

namespace Wihomo.Tests;

/// <summary>
/// 在隐藏的 STA 线程上真实构造 MainWindow，验证 XAML 里的绑定确实解析到视图模型成员。
/// 这台机器看不到窗口，这类检查是 View 接线唯一的机器可证依据。
/// </summary>
public class ViewBindingTests
{
    [Fact]
    public void 全部绑定表达式均解析成功()
    {
        var (probed, failures) = OnUi(() =>
        {
            var window = BuildWindow();
            var problems = new List<string>();
            var count = 0;

            foreach (var element in WalkLogicalTree(window))
            {
                foreach (var (property, label) in ProbeProperties(element))
                {
                    var expression = BindingOperations.GetBindingExpression(element, property);
                    if (expression is null)
                    {
                        continue;
                    }

                    count++;
                    if (expression.Status == BindingStatus.Active)
                    {
                        continue;
                    }

                    var path = expression.ParentBinding?.Path?.Path ?? "<无路径>";
                    problems.Add($"{element.GetType().Name}.{label} ← {{{path}}}: {expression.Status}");
                }
            }

            return (count, problems);
        });

        Assert.True(probed >= 40, $"只探测到 {probed} 条绑定，说明大量控件没有真正接上视图模型");
        Assert.Empty(failures);
    }

    [Fact]
    public void 表格列绑定指向成员类型上真实存在的属性()
    {
        var (grids, lists, failures) = OnUi(() =>
        {
            var window = BuildWindow();
            var problems = new List<string>();
            var gridCount = 0;
            var listCount = 0;

            foreach (var element in WalkLogicalTree(window))
            {
                switch (element)
                {
                    case DataGrid grid:
                        gridCount++;
                        var itemType = GetItemType(grid)
                            ?? throw new InvalidOperationException("DataGrid 的 ItemsSource 未绑定到集合");
                        foreach (var binding in grid.Columns.OfType<DataGridBoundColumn>()
                                     .Select(x => x.Binding).OfType<Binding>())
                        {
                            if (binding.Path.Path.Length > 0
                                && itemType.GetProperty(binding.Path.Path, BindingFlags.Public | BindingFlags.Instance) is null)
                            {
                                problems.Add($"{itemType.Name} 上没有属性 {binding.Path.Path}");
                            }
                        }

                        break;
                    case ListBox { DisplayMemberPath.Length: > 0 } list:
                        listCount++;
                        var listType = GetItemType(list)
                            ?? throw new InvalidOperationException("ListBox 的 ItemsSource 未绑定到集合");
                        if (listType.GetProperty(list.DisplayMemberPath, BindingFlags.Public | BindingFlags.Instance) is null)
                        {
                            problems.Add($"{listType.Name} 上没有属性 {list.DisplayMemberPath}");
                        }

                        break;
                }
            }

            return (gridCount, listCount, problems);
        });

        Assert.Equal(3, grids);
        Assert.Equal(1, lists);
        Assert.Empty(failures);
    }

    [Fact]
    public void 下拉框选项来自视图模型静态列表且选中项属于该列表()
    {
        var (comboCount, optionCounts, selectedValues, selectedOptionValues) = OnUi(() =>
        {
            var window = BuildWindow();
            var combos = WalkLogicalTree(window).OfType<ComboBox>().ToList();
            var settings = ((MainViewModel)window.DataContext!).Settings;

            return (
                combos.Count,
                combos.Select(x => x.ItemsSource is { } items ? items.Cast<object>().Count() : 0).ToList(),
                combos.Select(x => x.SelectedItem?.GetType().Name ?? "<null>").ToList(),
                new[] { settings.SelectedTunStack.Value, settings.SelectedDnsEnhancedMode.Value, settings.SelectedGeoDataMode.Value });
        });

        Assert.Equal(3, comboCount);
        Assert.All(optionCounts, count => Assert.True(count >= 2, $"下拉框只有 {count} 个选项，ItemsSource 可能没接上"));
        Assert.DoesNotContain("<null>", selectedValues);
        Assert.Contains("mixed", selectedOptionValues);
        Assert.Contains("fake-ip", selectedOptionValues);
        Assert.Contains("mmdb", selectedOptionValues);
    }

    [Fact]
    public void 每个操作按钮都绑到了可用命令()
    {
        var (buttonCount, problems) = OnUi(() =>
        {
            var window = BuildWindow();
            var buttons = WalkLogicalTree(window).OfType<Button>().ToList();
            var issues = buttons
                .Where(button => button.Command is null || !button.Command.CanExecute(null))
                .Select(button => $"{button.Content}");

            return (buttons.Count, issues.ToList());
        });

        Assert.Equal(15, buttonCount);
        Assert.Empty(problems);
    }

    [Fact]
    public void 状态栏与未保存提示绑定到视图模型()
    {
        var (initialMessage, visibility) = OnUi(() =>
        {
            var window = BuildWindow();
            var viewModel = (MainViewModel)window.DataContext!;

            var statusTextBlock = WalkLogicalTree(window).OfType<TextBlock>()
                .First(x => x.Text == viewModel.Message);
            var dirtyIndicator = WalkLogicalTree(window).OfType<TextBlock>()
                .First(x => x.Text == "有未保存更改");

            viewModel.HasUnsavedChanges = true;
            window.UpdateLayout();
            return (statusTextBlock.Text, dirtyIndicator.Visibility);
        });

        Assert.Equal("就绪", initialMessage);
        Assert.Equal(Visibility.Visible, visibility);
    }

    private static MainWindow BuildWindow()
    {
        var window = new MainWindow();
        SeedSampleData((MainViewModel)window.DataContext!);
        RealizeAllTabs(window);
        return window;
    }

    /// <summary>空集合会让中间节点为 null 的路径报 PathError，也看不到表格列，先塞一份最小样本。</summary>
    private static void SeedSampleData(MainViewModel viewModel)
    {
        viewModel.ProxyGroups.Replace(
            [new ProxyGroupInfo { Name = "节点选择", Type = "select", Current = "香港-1", Options = ["香港-1", "美国-2"] }],
            _ => "12 ms",
            showCurrentInHeader: true);

        var settings = new AppSettings();
        settings.Subscriptions.Add(new SubscriptionItem { Name = "机场A", Url = "https://a/sub" });
        viewModel.Subscriptions.Load(settings);
        viewModel.Subscriptions.SelectedSubscription = viewModel.Subscriptions.Items[0];

        viewModel.Telemetry.Update(new ConnectionsFrame
        {
            Connections =
            [
                new CoreConnection
                {
                    Id = "c1",
                    Chains = ["DIRECT"],
                    Rule = "MATCH",
                    Metadata = new ConnectionMetadata { Type = "Tunnel", SourceIp = "127.0.0.1", SourcePort = "1", Host = "example.com" },
                },
            ],
        });

        viewModel.Rules.SetSubscriptionRules(["GEOSITE,cn,DIRECT"]);
        viewModel.Rules.SetActiveRules(["MATCH,DIRECT"]);
    }

    /// <summary>逐个切到每个 Tab 并强制布局，否则未选中标签的内容不会求值绑定。</summary>
    private static void RealizeAllTabs(Window window)
    {
        var tabControls = WalkLogicalTree(window).OfType<TabControl>().ToList();
        Assert.NotEmpty(tabControls);

        foreach (var tabControl in tabControls)
        {
            foreach (var tab in tabControl.Items.OfType<TabItem>().ToList())
            {
                tab.IsSelected = true;
                if (tab.Content is FrameworkElement content)
                {
                    content.Measure(new Size(960, 640));
                    content.Arrange(new Rect(0, 0, 960, 640));
                    content.UpdateLayout();
                }
            }
        }
    }

    private static Type? GetItemType(ItemsControl control)
    {
        if (control.ItemsSource is not { } source)
        {
            return null;
        }

        var type = source.GetType();
        return new[] { type }.Concat(type.GetInterfaces())
            .FirstOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
    }

    private static IEnumerable<DependencyObject> WalkLogicalTree(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in WalkLogicalTree(child))
            {
                yield return descendant;
            }
        }
    }

    private static IEnumerable<(DependencyProperty Property, string Label)> ProbeProperties(DependencyObject element)
    {
        switch (element)
        {
            case TextBlock:
                yield return (TextBlock.TextProperty, nameof(TextBlock.Text));
                yield return (UIElement.VisibilityProperty, nameof(UIElement.Visibility));
                break;
            case TextBox:
                yield return (TextBox.TextProperty, nameof(TextBox.Text));
                break;
            case CheckBox:
                yield return (ToggleButton.IsCheckedProperty, nameof(ToggleButton.IsChecked));
                break;
            case ComboBox:
            case ListBox:
            case DataGrid:
                yield return (Selector.SelectedItemProperty, nameof(Selector.SelectedItem));
                break;
        }

        if (element is ItemsControl)
        {
            yield return (ItemsControl.ItemsSourceProperty, nameof(ItemsControl.ItemsSource));
        }

        if (element is ButtonBase)
        {
            yield return (ButtonBase.CommandProperty, nameof(ButtonBase.Command));
        }
    }

    /// <summary>
    /// WPF 对象有线程亲和性，而 xUnit 每个测试跑在线程池线程上：
    /// 构造与求值全部投递到这条唯一的隐藏 STA 线程。
    /// </summary>
    private static readonly Dispatcher UiThread = StartUiThread();

    private static Dispatcher StartUiThread()
    {
        Dispatcher? dispatcher = null;
        var ready = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            _ = new Application();
            ready.Set();
            Dispatcher.Run();
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(30)), "STA UI 线程未能启动");
        return dispatcher!;
    }

    private static T OnUi<T>(Func<T> action)
    {
        var operation = UiThread.InvokeAsync(action, DispatcherPriority.Send);
        Assert.Equal(DispatcherOperationStatus.Completed, operation.Wait(TimeSpan.FromSeconds(60)));
        return operation.Result;
    }
}
