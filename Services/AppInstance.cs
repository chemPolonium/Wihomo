using System.IO;
using Wihomo.Models;

namespace Wihomo.Services;

/// <summary>
/// 当前进程所属的应用实例。
///
/// 默认实例沿用旧版 WPF 的单实例名、设置目录、工作目录与端口，升级后用户配置可以继续沿用；
/// 设置 <c>WIHOMO_INSTANCE_SUFFIX</c> 时会派生出一个完全独立的并行实例：
/// 独立互斥体/激活管道、独立设置文件、独立内核工作目录、独立端口，并且不接管开机自启。
/// 这样新旧两版可以同时运行，调试时不会互相激活、覆盖配置或抢占端口。
/// </summary>
public static class AppInstance
{
    public const string SuffixEnvironmentVariable = "WIHOMO_INSTANCE_SUFFIX";

    private const int MaxSuffixLength = 12;

    public static string Suffix { get; } = Normalize(Environment.GetEnvironmentVariable(SuffixEnvironmentVariable));

    public static bool IsIsolated => Suffix.Length > 0;

    /// <summary>窗口标题、托盘提示与错误框里显示的名字；并行实例带后缀便于辨认。</summary>
    public static string DisplayName => IsIsolated ? $"Wihomo ({Suffix})" : "Wihomo";

    /// <summary>旧版设置仍在 %AppData%\Wihomo，默认实例必须继续读取它。</summary>
    public static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        IsIsolated ? $"Wihomo-{Suffix}" : "Wihomo");

    /// <summary>与旧版共用同一个 runtime 目录会覆盖 active.yaml / config.yaml，因此并行实例单独一份。</summary>
    public static string RuntimeDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        IsIsolated ? $"Wihomo-{Suffix}" : "Wihomo",
        "runtime");

    public static string SingleInstanceMutexName => $@"Local\Wihomo.SingleInstance{NameSuffix}";

    public static string ActivationPipeName => $"Wihomo.Activation{NameSuffix}";

    // 并行实例使用与旧版 7890/7891/7892/9090 不同的一组默认端口。
    public static int DefaultMixedPort => IsIsolated ? 8090 : 7890;

    public static int DefaultSocksPort => IsIsolated ? 8091 : 7891;

    public static int DefaultHttpPort => IsIsolated ? 8092 : 7892;

    public static int DefaultExternalControllerPort => IsIsolated ? 9095 : 9090;

    /// <summary>新建设置文件时的默认值；并行实例的端口与目录都与旧版错开。</summary>
    public static AppSettings CreateDefaultSettings() => new()
    {
        Core = new CoreRuntimeSettings
        {
            WorkingDirectory = RuntimeDirectory,
            ExternalControllerPort = DefaultExternalControllerPort,
            MixedPort = DefaultMixedPort,
            SocksPort = DefaultSocksPort,
            HttpPort = DefaultHttpPort,
        },
    };

    private static string NameSuffix => IsIsolated ? $".{Suffix}" : string.Empty;

    private static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(MaxSuffixLength);
        foreach (var ch in raw.Trim())
        {
            if (builder.Length == MaxSuffixLength)
            {
                break;
            }

            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }
}
