using System.Windows.Threading;

namespace Wihomo.ViewModels;

/// <summary>
/// 把后台线程的回调投递回 UI 线程。抽象出来是为了让 ViewModel 能在无 WPF 消息泵的测试里运行。
/// </summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action) => dispatcher.BeginInvoke(action);
}

/// <summary>测试用：原地执行，等于假设已经在 UI 线程上。</summary>
public sealed class DirectUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>下拉选项：显示文本与落盘值分离，取代按 ComboBoxItem.Content 字符串匹配或位置索引。</summary>
public sealed record LabeledOption(string Display, string Value)
{
    public override string ToString() => Display;
}
