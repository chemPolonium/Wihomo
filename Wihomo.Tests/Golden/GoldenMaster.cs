using System.IO;
using System.Text;

namespace Wihomo.Tests.Golden;

/// <summary>
/// 逐字节比对生成产物与已提交基线。重构期间用于证明 config.yaml 输出未发生漂移。
/// </summary>
internal static class GoldenMaster
{
    private static string VerifiedPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Golden", $"{name}.verified.txt");

    private static string ReceivedPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Golden", "received", $"{name}.received.txt");

    public static void Verify(string name, string actual)
    {
        var verifiedPath = VerifiedPath(name);
        if (!File.Exists(verifiedPath))
        {
            Save(name, actual);
            throw new InvalidOperationException(
                $"缺少金标准基线 '{name}'。已写出 {ReceivedPath(name)}；" +
                "确认该输出正确后复制为 Golden/{name}.verified.txt 再提交。");
        }

        var expected = File.ReadAllText(verifiedPath);
        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return;
        }

        Save(name, actual);
        Assert.Fail(
            $"'{name}' 的输出与金标准基线不一致（第一个差异在第 {FirstDifferenceLine(expected, actual)} 行）。" +
            $"实际输出已写入 {ReceivedPath(name)}，可逐行 diff 两个文件。");
    }

    private static void Save(string name, string actual)
    {
        var path = ReceivedPath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, actual, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static int FirstDifferenceLine(string expected, string actual)
    {
        using var a = new StringReader(expected);
        using var b = new StringReader(actual);
        var line = 1;
        while (true)
        {
            var left = a.ReadLine();
            var right = b.ReadLine();
            if (left is null || right is null)
            {
                return left == right ? -1 : line;
            }

            if (!string.Equals(left, right, StringComparison.Ordinal))
            {
                return line;
            }

            line++;
        }
    }
}
