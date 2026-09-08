using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class ServerMapServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "launchergo-webroot-tests-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(root, "builtin");
    private string Target => Path.Combine(root, "custom");
    private InstanceProfile Profile => new() { Id = "map-test", DirectoryPath = Path.Combine(root, "profile") };

    public ServerMapServiceTests()
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Target);
        File.WriteAllText(Path.Combine(Source, "index.html"), "new homepage");
        Directory.CreateDirectory(Path.Combine(Source, "vendor", "leaflet"));
        File.WriteAllText(Path.Combine(Source, "vendor", "leaflet", "leaflet.js"), "new library");
        File.WriteAllText(Path.Combine(Source, "LICENSE.txt"), "license");
    }

    [Fact]
    public async Task CopyWebRoot_ReplacesBundledFilesAndKeepsCustomFiles()
    {
        File.WriteAllText(Path.Combine(Target, "index.html"), "old homepage");
        File.WriteAllText(Path.Combine(Target, "custom.css"), "custom stylesheet");
        File.WriteAllText(Path.Combine(Target, "pois.json"), "map data");
        var count = await ServerMapService.CopyWebRootAsync(Source, Target);
        Assert.Equal(3, count);
        Assert.Equal("new homepage", File.ReadAllText(Path.Combine(Target, "index.html")));
        Assert.Equal("new library", File.ReadAllText(Path.Combine(Target, "vendor", "leaflet", "leaflet.js")));
        Assert.Equal("license", File.ReadAllText(Path.Combine(Target, "LICENSE.txt")));
        Assert.Equal("custom stylesheet", File.ReadAllText(Path.Combine(Target, "custom.css")));
        Assert.Equal("map data", File.ReadAllText(Path.Combine(Target, "pois.json")));
        Assert.Empty(Directory.EnumerateFiles(Target, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task UpdateWebRoot_PersistsRelativePathAsAbsoluteAndKeepsSettings()
    {
        var service = new ServerMapService(Source);
        var settings = new ServerMapSettings { WebRoot = "www", BackendToken = "existing-token", BackendPort = 15080, ListenPort = 18081 };
        await service.UpdateWebRootAsync(Profile, settings);
        var loaded = await service.LoadSettingsAsync(Profile);
        Assert.Equal(Path.Combine(Profile.DirectoryPath, "ServerMap", "www"), loaded.WebRoot);
        Assert.True(File.Exists(Path.Combine(loaded.WebRoot, "index.html")));
        Assert.Equal(settings.BackendToken, loaded.BackendToken);
        Assert.Equal(settings.ListenPort, loaded.ListenPort);
        Assert.Equal(settings.BackendPort, loaded.BackendPort);
    }

    [Fact]
    public async Task SaveSettings_EmptyWebRootKeepsBuiltInDefault()
    {
        var service = new ServerMapService(Source);
        await service.SaveSettingsAsync(Profile, new ServerMapSettings { WebRoot = "   " });
        Assert.Equal(string.Empty, (await service.LoadSettingsAsync(Profile)).WebRoot);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateWebRootAsync(Profile, new ServerMapSettings()));
    }

    [Fact]
    public async Task CopyWebRoot_RejectsOverlappingSourceAndDestination()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ServerMapService.CopyWebRootAsync(Source, Source));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ServerMapService.CopyWebRootAsync(Source, Path.Combine(Source, "nested")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ServerMapService.CopyWebRootAsync(Source, root));
        Assert.Equal("new homepage", File.ReadAllText(Path.Combine(Source, "index.html")));
    }

    [Fact]
    public async Task UpdateWebRoot_MissingBundleDoesNotChangeSavedSettings()
    {
        var service = new ServerMapService(Path.Combine(root, "missing"));
        await service.SaveSettingsAsync(Profile, new ServerMapSettings { WebRoot = Target });
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.UpdateWebRootAsync(Profile, new ServerMapSettings { WebRoot = "different" }));
        Assert.Equal(Target, (await service.LoadSettingsAsync(Profile)).WebRoot);
    }

    [Fact]
    public async Task CopyWebRoot_LockedFileKeepsPreviousCompleteContents()
    {
        var file = Path.Combine(Target, "index.html");
        File.WriteAllText(file, "old homepage");
        using (var reader = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => ServerMapService.CopyWebRootAsync(Source, Target));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal("old homepage", File.ReadAllText(file));
        Assert.Empty(Directory.EnumerateFiles(Target, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CopyWebRoot_CancelledOperationLeavesTargetUntouched()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ServerMapService.CopyWebRootAsync(Source, Target, cancel.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Target));
    }

    public void Dispose() => Directory.Delete(root, true);
}
