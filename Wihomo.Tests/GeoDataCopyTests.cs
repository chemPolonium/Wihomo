using System.IO;
using System.Reflection;

namespace Wihomo.Tests;

public sealed class GeoDataCopyTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        TestPaths.TempRoot,
        $"wihomo-geo-{Guid.NewGuid():N}");

    [Fact]
    public void ShouldCopyBundledGeoFile_源较新或大小不同时才复制()
    {
        Directory.CreateDirectory(_directory);
        var source = Path.Combine(_directory, "source.dat");
        var destination = Path.Combine(_directory, "destination.dat");
        File.WriteAllText(source, "new-content");
        File.WriteAllText(destination, "old");

        var method = typeof(MainViewModel).GetMethod(
            "ShouldCopyBundledGeoFile",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ShouldCopyBundledGeoFile 方法不存在。");

        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-1));
        File.SetLastWriteTimeUtc(destination, DateTime.UtcNow.AddMinutes(-2));
        Assert.True((bool)method.Invoke(null, [source, destination])!);

        File.WriteAllText(destination, "old-content-with-different-size");
        File.SetLastWriteTimeUtc(destination, DateTime.UtcNow.AddMinutes(1));
        Assert.True((bool)method.Invoke(null, [source, destination])!);

        File.WriteAllText(destination, "new-content");
        File.SetLastWriteTimeUtc(destination, DateTime.UtcNow.AddMinutes(1));
        Assert.False((bool)method.Invoke(null, [source, destination])!);

        File.Delete(destination);
        Assert.True((bool)method.Invoke(null, [source, destination])!);
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
