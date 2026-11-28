using System.IO;
using System.Text.Json;
using Wihomo.Models;

namespace Wihomo.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    /// <param name="settingsPath">
    /// 缺省落到 <see cref="AppInstance.SettingsDirectory"/> 下的 settings.json；测试注入临时路径以免触碰真实设置。
    /// </param>
    public SettingsService(string? settingsPath = null)
    {
        // 调试实例可通过环境变量指向一份配置副本，避免读写正式设置。
        var overridePath = Environment.GetEnvironmentVariable("WIHOMO_SETTINGS_PATH");
        _settingsPath = settingsPath
            ?? (!string.IsNullOrWhiteSpace(overridePath)
                ? Path.GetFullPath(overridePath)
                : Path.Combine(AppInstance.SettingsDirectory, "settings.json"));
    }

    public string SettingsPath => _settingsPath;

    public async Task<AppSettings> LoadAsync()
    {
        if (!File.Exists(_settingsPath))
        {
            return AppInstance.CreateDefaultSettings();
        }

        await using var stream = File.OpenRead(_settingsPath);
        var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions);
        return settings ?? new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("Invalid settings path.");
        Directory.CreateDirectory(directory);

        // 先写同目录临时文件，再原子替换；失败时旧 settings.json 保持完整，
        // 不会出现 File.Create 已经截断、序列化却中途失败的半截文件。
        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_settingsPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_settingsPath))
            {
                File.Replace(tempPath, _settingsPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, _settingsPath);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
