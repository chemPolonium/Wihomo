using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Wihomo.Services;

public sealed class SystemProxyService
{
    private const string InternetSettingsPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    public void Enable(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("系统代理主机不能为空。");
        }

        if (port <= 0)
        {
            throw new InvalidOperationException("系统代理端口必须是正整数。");
        }

        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: true)
            ?? throw new InvalidOperationException("无法打开 Windows Internet Settings 注册表。");
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", $"{host}:{port}", RegistryValueKind.String);
        key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
        RefreshSystemProxy();
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: true)
            ?? throw new InvalidOperationException("无法打开 Windows Internet Settings 注册表。");
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        RefreshSystemProxy();
    }

    private static void RefreshSystemProxy()
    {
        if (!InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "刷新系统代理设置失败。");
        }

        if (!InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "通知系统代理刷新失败。");
        }
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
