using System.ComponentModel;
using System.IO;
using System.Net.Http;
using YamlDotNet.Core;

namespace Wihomo.Services;

/// <summary>
/// 把异常翻译成状态栏文本。迁移前这段分类散落在代码后置的 ExecuteUiActionAsync 里，
/// 抽出后视图模型可以独立于 WPF 复现同样的提示。
/// </summary>
public static class UiErrorFormatter
{
    public static string Describe(Exception exception) => exception switch
    {
        InvalidOperationException invalid => invalid.Message,
        YamlException yaml => $"YAML 覆写格式错误: {yaml.Message}",
        FileNotFoundException notFound => $"文件不存在: {notFound.FileName}",
        DirectoryNotFoundException directory => $"目录不存在: {directory.Message}",
        UnauthorizedAccessException unauthorized => $"权限不足: {unauthorized.Message}",
        HttpRequestException request => $"API 请求失败: {request.Message}",
        TaskCanceledException => "请求超时。",
        Win32Exception win32 => $"系统调用失败: {win32.Message}",
        IOException io => $"I/O 错误: {io.Message}",
        _ => $"操作失败: {exception.Message}",
    };
}
