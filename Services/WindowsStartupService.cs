using Windows.ApplicationModel;

namespace Wihomo.Services;

/// <summary>
/// MSIX 打包应用不能依赖注册表 Run 项，改用清单里的 startupTask 扩展。
/// </summary>
public sealed class WindowsStartupService
{
    private const string TaskId = "WihomoStartup";

    /// <summary>返回系统最终生效的启用状态；被策略或任务管理器禁用时可能仍为 false。</summary>
    public async Task<bool> SetEnabledAsync(bool enabled)
    {
        // 并行调试实例绝不动系统启动项：旧版仍在用同一个 startupTask 语义，
        // 关掉它会让用户以为自己安装的正式版失效。
        if (AppInstance.IsIsolated)
        {
            return false;
        }

        StartupTask task;
        try
        {
            task = await StartupTask.GetAsync(TaskId);
        }
        catch (Exception) when (!HasPackageIdentity())
        {
            return false;
        }

        if (!enabled)
        {
            task.Disable();
            return false;
        }

        var state = await task.RequestEnableAsync();
        return state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
    }

    private static bool HasPackageIdentity()
    {
        try
        {
            _ = Package.Current;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
