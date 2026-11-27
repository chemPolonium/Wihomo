using System.IO;
using System.Text.Json;
using Wihomo.Models;
using Wihomo.Services;

namespace Wihomo.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        TestPaths.TempRoot,
        $"wihomo-settings-{Guid.NewGuid():N}");

    [Fact]
    public async Task LoadAsync_IgnoresRemovedTunFields()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(
            path,
            """{"Core":{"MixedPort":7890,"EnableTun":true,"TunStack":"system"},"StartCoreOnProgramStart":false}""");

        var settings = await new SettingsService(path).LoadAsync();

        Assert.Equal(7890, settings.Core.MixedPort);
        Assert.False(settings.StartCoreOnProgramStart);
    }

    [Fact]
    public async Task SaveAsync_WritesValidJsonAndLeavesNoTempFile()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        var service = new SettingsService(path);
        var settings = new AppSettings { RuleOverrides = "rules:\n  - MATCH,DIRECT" };

        await service.SaveAsync(settings);

        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));

        var json = await File.ReadAllTextAsync(path);
        var roundTrip = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(roundTrip);
        Assert.Equal("rules:\n  - MATCH,DIRECT", roundTrip.RuleOverrides);
    }

    [Fact]
    public async Task SaveAsync_WhenReplaceFails_KeepsExistingSettings()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        var service = new SettingsService(path);
        await service.SaveAsync(new AppSettings { RuleOverrides = "old" });

        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => service.SaveAsync(new AppSettings { RuleOverrides = "new" }));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        var json = await File.ReadAllTextAsync(path);
        var roundTrip = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(roundTrip);
        Assert.Equal("old", roundTrip.RuleOverrides);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
