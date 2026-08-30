using System.Collections.ObjectModel;

namespace Wihomo.ViewModels;

/// <summary>
/// 规则页的两个列表。沿用「把提示当作列表项」的现有呈现方式，避免为占位文本改动布局。
/// </summary>
public sealed class RulesViewModel
{
    public ObservableCollection<string> SubscriptionRules { get; } = [];

    public ObservableCollection<string> ActiveRules { get; } = [];

    public void SetSubscriptionRules(IEnumerable<string> rules)
    {
        Replace(SubscriptionRules, rules.Any() ? rules : ["暂无订阅规则"]);
    }

    public void SetActiveRules(IReadOnlyList<string> rules)
    {
        Replace(ActiveRules, rules.Count > 0 ? rules : ["暂无当前规则"]);
    }

    public void SetActiveRulesError(string message)
    {
        Replace(ActiveRules, [$"读取规则失败: {message}"]);
    }

    public void ShowCoreStopped()
    {
        Replace(ActiveRules, ["内核未运行"]);
    }

    private static void Replace(ObservableCollection<string> target, IEnumerable<string> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}

/// <summary>
/// 内核输出与操作日志。逐条增量交付，避免每次追加都重建整段文本。
/// </summary>
public sealed class LogsViewModel
{
    private const int MaxLines = 500;
    private readonly List<string> _lines = [];

    /// <summary>新增一行时触发；由 View 负责把文本增量写入控件并滚动。</summary>
    public event Action<string>? LineAppended;

    /// <summary>缓冲区被清空时触发；View 据此清掉控件里的残留文本。</summary>
    public event Action? Cleared;

    public IReadOnlyList<string> Lines => _lines;

    public void Append(string line)
    {
        var formatted = $"[{DateTime.Now:HH:mm:ss}] {line}";
        _lines.Add(formatted);
        if (_lines.Count > MaxLines)
        {
            _lines.RemoveAt(0);
        }

        LineAppended?.Invoke(formatted);
    }

    /// <summary>View 初次订阅后补齐启动阶段已产生的日志。</summary>
    public void ReplayTo(Action<string> append)
    {
        foreach (var line in _lines)
        {
            append(line);
        }
    }

    public void Clear()
    {
        _lines.Clear();
        Cleared?.Invoke();
    }
}
