using System.IO.Compression;
using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed partial class ServerMapServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "launchergo-webroot-tests-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(root, "builtin");
    private string Target => Path.Combine(root, "custom");
    private InstanceProfile Profile => new() { Id = "map-test", DirectoryPath = Path.Combine(root, "profile") };
    private ServerMapService CreateService(string? source = null, string? mod = null) => new(source ?? Source,
        mapModPackage: mod, runtimeRoot: Path.Combine(root, "runtime"));

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
    public async Task RenderProgressUsesTheSelectedProfilesLoopbackPortAndToken()
    {
        var requests = new List<(Uri Uri, string? Token)>();
        using var client = new HttpClient(new ProgressHandler(request => requests.Add((request.RequestUri!, request.Headers.Authorization?.Parameter))));
        var service = new ServerMapService(Source, progressClient: client);
        var other = new InstanceProfile { Id = "other-map", DirectoryPath = Path.Combine(root, "other-profile") };
        await service.SaveSettingsAsync(Profile, new ServerMapSettings { BackendPort = 17801, BackendToken = "profile-one" });
        await service.SaveSettingsAsync(other, new ServerMapSettings { BackendPort = 17802, BackendToken = "profile-two" });
        var first = await service.GetRenderProgressAsync(Profile);
        var second = await service.GetRenderProgressAsync(other);
        Assert.True(first!.Rebuilding); Assert.Equal("request-id", second!.RebuildId);
        Assert.Equal(new[] { 17801, 17802 }, requests.Select(r => r.Uri.Port));
        Assert.Equal(new[] { "profile-one", "profile-two" }, requests.Select(r => r.Token));
        Assert.All(requests, request => { Assert.Equal("127.0.0.1", request.Uri.Host); Assert.Equal("/api/v1/render-progress", request.Uri.AbsolutePath); });
    }
    private sealed class ProgressHandler(Action<HttpRequestMessage> capture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method); capture(request);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"cacheProtocol":1,"rebuilding":true,"rebuildId":"request-id","pending":12}""") });
        }
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
    public async Task ResetWebRoot_UsesSavedRelativePathWithoutSavingSettings()
    {
        var service = CreateService();
        var settings = new ServerMapSettings { WebRoot = "www", BackendToken = "existing-token", BackendPort = 15080, ListenPort = 18081 };
        await service.SaveSettingsAsync(Profile, settings);
        var settingsFiles = Directory.GetFiles(service.GetProfileDirectory(Profile), "*.json", SearchOption.AllDirectories)
            .ToDictionary(p => p, p => (Content: File.ReadAllText(p), Stamp: File.GetLastWriteTimeUtc(p)));
        await service.ResetWebRootAsync(Profile, await service.InspectWebResetAsync(Profile));
        foreach (var (path, before) in settingsFiles)
            Assert.Equal(before, (File.ReadAllText(path), File.GetLastWriteTimeUtc(path)));
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
        var service = CreateService();
        await service.SaveSettingsAsync(Profile, new ServerMapSettings { WebRoot = "   " });
        Assert.Equal(string.Empty, (await service.LoadSettingsAsync(Profile)).WebRoot);
        var result = await service.ResetWebRootAsync(Profile, await service.InspectWebResetAsync(Profile));
        Assert.Equal(0, result.FilesChanged);
        Assert.True(result.AppliesOnNextStart);
        Assert.Equal(string.Empty, (await service.LoadSettingsAsync(Profile)).WebRoot);
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
    public async Task ResetWebRoot_MissingBundleDoesNotChangeSavedSettings()
    {
        var service = CreateService(Path.Combine(root, "missing"));
        await service.SaveSettingsAsync(Profile, new ServerMapSettings { WebRoot = Target });
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.InspectWebResetAsync(Profile));
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

    [Fact]
    public void ValidateMapModPackage_AcceptsCompleteLicenses()
    {
        ServerMapService.ValidateMapModPackage(CreateModPackage());
    }


    [Theory]
    [InlineData("LICENSE.txt", false)]
    [InlineData("THIRD_PARTY_NOTICES.txt", false)]
    [InlineData("VS-LiveMap-Revival-LICENSE.txt", false)]
    [InlineData("LICENSE.txt", true)]
    [InlineData("THIRD_PARTY_NOTICES.txt", true)]
    [InlineData("VS-LiveMap-Revival-LICENSE.txt", true)]
    public void ValidateMapModPackage_RejectsMissingOrEmptyLicense(string name, bool empty)
    {
        var package = CreateModPackage(name, empty);
        var error = Assert.Throws<InvalidDataException>(() => ServerMapService.ValidateMapModPackage(package));
        Assert.Contains(name, error.Message);
    }

    [Fact]
    public void ValidateMapModPackage_RejectsInvalidArchive()
    {
        var package = Path.Combine(root, "invalid.zip");
        File.WriteAllText(package, "not a ZIP archive");
        Assert.Throws<InvalidDataException>(() => ServerMapService.ValidateMapModPackage(package));
    }

    private string CreateModPackage(string? excluded = null, bool empty = false)
    {
        var path = Path.Combine(root, "servermap.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("modinfo.json").Open()))
            writer.Write("""{"modid":"servermap","version":"0.4.2"}""");
        foreach (var name in new[] { "LICENSE.txt", "THIRD_PARTY_NOTICES.txt", "VS-LiveMap-Revival-LICENSE.txt" })
        {
            if (name == excluded && !empty) continue;
            var entry = archive.CreateEntry(name);
            if (name == excluded) continue;
            using var writer = new StreamWriter(entry.Open());
            writer.Write("license text");
        }
        return path;
    }

    public void Dispose() => Directory.Delete(root, true);
}
