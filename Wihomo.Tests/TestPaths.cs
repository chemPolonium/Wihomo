using System.IO;

namespace Wihomo.Tests;

/// <summary>
/// vstest 18+ 的 testhost 以 Low 完整性级别运行，无法写入用户的 %TEMP%。
/// 构建阶段会在输出目录创建并标记为 Low 的 TestTemp，测试统一落在那里。
/// </summary>
internal static class TestPaths
{
    public static string TempRoot { get; } = EnsureTempRoot();

    private static string EnsureTempRoot()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "TestTemp");
        Directory.CreateDirectory(root);

        // 目录存在不代表可写：若构建目标没有把它标成 Low，写入会在这里直接失败，
        // 比每个测试各自报 UnauthorizedAccessException 更容易定位。
        var probe = Path.Combine(root, $".probe-{Guid.NewGuid():N}");
        File.WriteAllText(probe, "probe");
        File.Delete(probe);

        return root;
    }
}
