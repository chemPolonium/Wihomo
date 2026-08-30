using System.ComponentModel;
using System.IO;
using System.Net.Http;
using Wihomo.Services;
using YamlDotNet.Core;
using YamlException = YamlDotNet.Core.YamlException;

namespace Wihomo.Tests;

public class UiErrorFormatterTests
{
    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(FileNotFoundException))]
    [InlineData(typeof(DirectoryNotFoundException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TaskCanceledException))]
    [InlineData(typeof(Win32Exception))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(FormatException))]
    public void 任何异常都得到非空提示(Type type)
    {
        var exception = (Exception)Activator.CreateInstance(type)!;

        Assert.False(string.IsNullOrWhiteSpace(UiErrorFormatter.Describe(exception)));
    }

    [Fact]
    public void 业务校验异常原样透出文案()
    {
        Assert.Equal("请先启动内核。", UiErrorFormatter.Describe(new InvalidOperationException("请先启动内核。")));
    }

    [Fact]
    public void HttpRequest_优先于其基类_IOError_匹配()
    {
        // HttpRequestException 继承自 IOException；迁移前它被 IOException 分支吞掉，
        // 这里锁定分类顺序，确保 API 失败不再显示成泛化的 I/O 错误。
        var message = UiErrorFormatter.Describe(new HttpRequestException("响应码 404"));

        Assert.Equal("API 请求失败: 响应码 404", message);
        Assert.DoesNotContain("I/O 错误", message);
    }

    [Fact]
    public void 文件类异常使用最具体的分支()
    {
        Assert.Equal("文件不存在: D:\\missing.bin", UiErrorFormatter.Describe(new FileNotFoundException("读不了", "D:\\missing.bin")));
        Assert.Equal("目录不存在: 缺少目录", UiErrorFormatter.Describe(new DirectoryNotFoundException("缺少目录")));
        Assert.Equal("权限不足: 拒绝访问", UiErrorFormatter.Describe(new UnauthorizedAccessException("拒绝访问")));
        Assert.Equal("I/O 错误: 磁盘满", UiErrorFormatter.Describe(new IOException("磁盘满")));
    }

    [Fact]
    public void 超时与系统调用异常有专属文案()
    {
        Assert.Equal("请求超时。", UiErrorFormatter.Describe(new TaskCanceledException("取消")));
        Assert.StartsWith("系统调用失败:", UiErrorFormatter.Describe(new Win32Exception(5, "拒绝访问")));
    }

    [Fact]
    public void 未知异常保留可读前缀而不是崩溃()
    {
        Assert.Equal("操作失败: 无法解释", UiErrorFormatter.Describe(new FormatException("无法解释")));
    }

    [Fact]
    public void YAML_异常标注为覆写格式错误()
    {
        var message = UiErrorFormatter.Describe(new YamlException("映射需要结束"));

        Assert.Equal("YAML 覆写格式错误: 映射需要结束", message);
    }
}
