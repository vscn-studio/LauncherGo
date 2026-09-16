using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed partial class ServerMapServiceTests
{
    private string ModTarget => Path.Combine(Profile.DirectoryPath, "Mods", "servermap.zip");
    private string Backups => Path.Combine(Profile.DirectoryPath, "ServerMap", "Backups");

    private ServerMapMaintenanceResult Deploy(string source, ServerMapModDeployment? confirmed = null, string? backups = null,
        CancellationToken token = default) => ServerMapService.DeployConfirmedMapMod(source, ModTarget, backups ?? Backups,
            confirmed ?? ServerMapService.InspectMapMod(source, ModTarget), token);

    [Fact]
    public async Task Deploy_ExplicitOnly_IdenticalContentsAreNotRewritten()
    {
        var source = CreateModPackage();
        var service = CreateService(mod: source);
        var info = await service.InspectMapModAsync(Profile);
        Assert.False(File.Exists(ModTarget));
        Assert.Equal("0.4.2", info.SourceVersion);
        var installed = await service.DeployMapModAsync(Profile, info);
        Assert.Equal(1, installed.FilesChanged);
        Assert.Null(installed.BackupPath);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(ModTarget));
        File.SetLastWriteTimeUtc(ModTarget, DateTime.UtcNow.AddDays(-1));
        var stamp = File.GetLastWriteTimeUtc(ModTarget);
        // No receipt is needed, and differing file timestamps do not force replacement.
        Assert.Equal(0, (await service.DeployMapModAsync(Profile, await service.InspectMapModAsync(Profile))).FilesChanged);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(ModTarget));
        Assert.False(Directory.Exists(Backups));
    }

    [Fact]
    public void Deploy_ReplacementBacksUpOldBytesAndRepairsDamagedTargetOnlyAfterConfirmation()
    {
        var source = CreateModPackage();
        Deploy(source);
        File.WriteAllText(ModTarget, "manually installed or damaged package");
        var result = Deploy(source);
        Assert.Equal("manually installed or damaged package", File.ReadAllText(Path.Combine(result.BackupPath!, "servermap.zip")));
        Assert.StartsWith(Backups, result.BackupPath);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(ModTarget));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(ModTarget)!, "*.tmp"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Deploy_RejectsChangesSinceConfirmation(bool changeSource)
    {
        var source = CreateModPackage();
        Deploy(source);
        var confirmed = ServerMapService.InspectMapMod(source, ModTarget);
        var path = changeSource ? source : ModTarget;
        var bytes = File.ReadAllBytes(path);
        var stamp = File.GetLastWriteTimeUtc(path);
        bytes[^1] ^= 1; // Same size and timestamp: content hashes must catch it.
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, stamp);
        var targetBefore = File.ReadAllBytes(ModTarget);
        Assert.Throws<IOException>(() => Deploy(source, confirmed));
        Assert.Equal(targetBefore, File.ReadAllBytes(ModTarget));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Deploy_RejectsRenamedOrDirectoryMod(bool directory)
    {
        var source = CreateModPackage();
        Deploy(source);
        var other = Path.Combine(Path.GetDirectoryName(ModTarget)!, directory ? "servermap-custom" : "servermap-new.zip");
        if (directory)
        {
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, "modinfo.json"), """{"modid":"ServerMap","version":"9.9"}""");
        }
        else File.Copy(source, other);
        var info = ServerMapService.InspectMapMod(source, ModTarget);
        Assert.Contains(other, info.Conflicts);
        Assert.Throws<InvalidOperationException>(() => Deploy(source, info));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(ModTarget));
    }

    [Fact]
    public void Deploy_CancelledLockedOrInvalidPackageKeepsOldTarget()
    {
        var source = CreateModPackage();
        Deploy(source);
        var old = File.ReadAllBytes(ModTarget);
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Update))
        using (var writer = new StreamWriter(zip.CreateEntry("changed.txt").Open())) writer.Write("new build");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => Deploy(source, token: cancel.Token));
        }
        using (File.Open(ModTarget, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => Deploy(source));
        Assert.Equal(old, File.ReadAllBytes(ModTarget));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(ModTarget)!, "*.tmp"));
        File.WriteAllText(source, "invalid ZIP");
        Assert.Throws<InvalidDataException>(() => Deploy(source));
        Assert.Equal(old, File.ReadAllBytes(ModTarget));
    }

    [Fact]
    public void Deploy_BackupFailurePreventsReplacement()
    {
        var source = CreateModPackage();
        Deploy(source);
        File.WriteAllText(ModTarget, "old mod");
        var blocked = Path.Combine(root, "not-a-directory");
        File.WriteAllText(blocked, "block backup");
        Assert.ThrowsAny<IOException>(() => Deploy(source, backups: blocked));
        Assert.Equal("old mod", File.ReadAllText(ModTarget));
        Assert.Throws<IOException>(() => Deploy(source, backups: Path.GetDirectoryName(ModTarget)));
        Assert.Equal("old mod", File.ReadAllText(ModTarget));
    }

    [Fact]
    public async Task Reset_BackupPreservesCustomFilesDataSettingsAndMod()
    {
        var source = CreateModPackage();
        Deploy(source);
        File.WriteAllText(ModTarget, "user's newer mod");
        var modTime = File.GetLastWriteTimeUtc(ModTarget);
        var service = CreateService(mod: source);
        await service.SaveSettingsAsync(Profile, new ServerMapSettings { WebRoot = Target });
        File.WriteAllText(Path.Combine(Target, "index.html"), "custom homepage");
        File.WriteAllText(Path.Combine(Target, "custom.css"), "custom stylesheet");
        var data = Path.Combine(service.GetProfileDirectory(Profile), "announcement.json");
        File.WriteAllText(data, "custom JS/CSS and announcement");
        var result = await service.ResetWebRootAsync(Profile, await service.InspectWebResetAsync(Profile));
        Assert.Equal(3, result.FilesChanged);
        Assert.Equal("custom homepage", File.ReadAllText(Path.Combine(result.BackupPath!, "index.html")));
        Assert.Equal("new homepage", File.ReadAllText(Path.Combine(Target, "index.html")));
        Assert.Equal("custom stylesheet", File.ReadAllText(Path.Combine(Target, "custom.css")));
        Assert.Equal("custom JS/CSS and announcement", File.ReadAllText(data));
        Assert.Equal("user's newer mod", File.ReadAllText(ModTarget));
        Assert.Equal(modTime, File.GetLastWriteTimeUtc(ModTarget));
    }

    [Fact]
    public async Task Reset_ConfigChangedAfterConfirmationRequiresNewConfirmation()
    {
        var service = CreateService();
        await service.SaveSettingsAsync(Profile, new ServerMapSettings { WebRoot = Target });
        var preview = await service.InspectWebResetAsync(Profile);
        var other = Path.Combine(root, "other-web");
        await service.SaveSettingsAsync(Profile, new ServerMapSettings { WebRoot = other });
        await Assert.ThrowsAsync<IOException>(() => service.ResetWebRootAsync(Profile, preview));
        Assert.Empty(Directory.GetFiles(Target));
        Assert.False(Directory.Exists(other));
    }

    [Fact]
    public async Task Reset_LockedIndexReportsBackupWithoutSuccess()
    {
        var service = CreateService();
        await service.SaveSettingsAsync(Profile, new ServerMapSettings { WebRoot = Target });
        var index = Path.Combine(Target, "index.html");
        File.WriteAllText(index, "old homepage");
        using (File.Open(index, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Assert.ThrowsAsync<IOException>(() => ResetAsync(service));
            Assert.Contains("Backup:", error.Message);
        }
        Assert.Equal("old homepage", File.ReadAllText(index));
        Assert.Equal("old homepage", File.ReadAllText(Directory.GetFiles(Backups, "index.html", SearchOption.AllDirectories).Single()));
        Assert.Empty(Directory.EnumerateFiles(Target, "*.tmp", SearchOption.AllDirectories));
        await ResetAsync(service); // lock released: retry succeeds
        Assert.Equal("new homepage", File.ReadAllText(index));
    }

    private async Task<ServerMapMaintenanceResult> ResetAsync(ServerMapService service) =>
        await service.ResetWebRootAsync(Profile, await service.InspectWebResetAsync(Profile));

    [Fact]
    public async Task Reset_BackupFailureLeavesWebUntouched()
    {
        var service = CreateService();
        await service.SaveSettingsAsync(Profile, new ServerMapSettings { WebRoot = Target });
        File.WriteAllText(Path.Combine(Target, "index.html"), "old homepage");
        File.WriteAllText(Backups, "block backup");
        await Assert.ThrowsAnyAsync<IOException>(() => ResetAsync(service));
        Assert.Equal("old homepage", File.ReadAllText(Path.Combine(Target, "index.html")));
    }

    [Fact]
    public async Task AssetVersions_ChangeWithContentAndPreserveOtherQueries()
    {
        var html = """<script src="vendor/leaflet/leaflet.js?v=old"></script><link href='https://example.test/style.css'><script>const value = '<link href="vendor/leaflet/leaflet.js">';</script><!-- <script src="vendor/leaflet/leaflet.js"></script> -->""";
        File.WriteAllText(Path.Combine(Source, "index.html"), html);
        var first = ServerMapWebAssets.VersionHtml(html, Source);
        Assert.Contains("v=old&amp;lgweb=", first);
        Assert.Contains("href='https://example.test/style.css'", first);
        Assert.Contains("const value = '<link href=\"vendor/leaflet/leaflet.js\">'", first);
        Assert.Contains("<!-- <script src=\"vendor/leaflet/leaflet.js\"></script> -->", first);
        Assert.Equal(first, ServerMapWebAssets.VersionHtml(first, Source));
        File.WriteAllText(Path.Combine(Source, "vendor", "leaflet", "leaflet.js"), "changed library");
        var second = ServerMapWebAssets.VersionHtml(first, Source);
        Assert.NotEqual(first, second);
        await ServerMapService.CopyWebRootAsync(Source, Target);
        Assert.Equal(second, File.ReadAllText(Path.Combine(Target, "index.html")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostLifecycleAndLiveWebResetNeverDeployMod(bool installed)
    {
        // Real Host, but only temporary profiles, runtime directories and loopback ports.
        // Never starts/stops the user's game server or touches its deployed packages.
        var configuration = GetType().Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var hostPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "LauncherGo.ServerMapHost", "bin", configuration, "net10.0", "LauncherGo.ServerMapHost.exe"));
        var bundle = Path.Combine(Path.GetDirectoryName(hostPath)!, "WebRoot");
        var source = CreateModPackage();
        if (installed) Deploy(source);
        var modBytes = installed ? File.ReadAllBytes(ModTarget) : null;
        var modTime = installed ? File.GetLastWriteTimeUtc(ModTarget) : default;
        if (installed) File.WriteAllText(source, "different bundled mod that must never be deployed");
        var service = new ServerMapService(bundle, mapModPackage: source, runtimeRoot: Path.Combine(root, "runtime"), hostPath: hostPath);
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        await service.SaveSettingsAsync(Profile, new ServerMapSettings { ListenPort = port, BackendPort = 1 });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            var status = await service.StartAsync(Profile, deadline.Token);
            Assert.True(status.IsRunning);
            var preview = await service.InspectWebResetAsync(Profile, deadline.Token);
            Assert.NotNull(preview.TargetPath);
            File.WriteAllText(Path.Combine(preview.TargetPath!, "index.html"), "old running web page");
            var reset = await service.ResetWebRootAsync(Profile, preview, deadline.Token);
            Assert.False(reset.RequiresHostRestart);
            Assert.Equal(status.ProcessId, service.GetStatus(Profile).ProcessId);
            Assert.Equal("old running web page", File.ReadAllText(Path.Combine(reset.BackupPath!, "index.html")));
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            var html = await client.GetStringAsync(status.Url, deadline.Token);
            Assert.Contains("lgweb=", html);
            using var asset = await client.GetAsync(new Uri(new Uri(status.Url), "mobile.css"), deadline.Token);
            Assert.True(asset.Headers.CacheControl!.NoCache);
            // A deleted homepage must be repairable without restarting the Host.
            File.Delete(Path.Combine(preview.TargetPath!, "index.html"));
            Assert.NotNull((await service.InspectWebResetAsync(Profile)).TargetPath);
            await ResetAsync(service);
            Assert.True(File.Exists(Path.Combine(preview.TargetPath!, "index.html")));
            // Changing the saved directory does not silently switch or restart the running Host.
            await service.SaveSettingsAsync(Profile, new ServerMapSettings { ListenPort = port, BackendPort = 1, WebRoot = Target });
            var switched = await service.InspectWebResetAsync(Profile, deadline.Token);
            Assert.True(switched.RequiresHostRestart);
            Assert.True((await service.ResetWebRootAsync(Profile, switched, deadline.Token)).RequiresHostRestart);
            Assert.Equal(status.ProcessId, service.GetStatus(Profile).ProcessId);
        }
        finally { await service.StopAsync(Profile); }
        Assert.False(service.GetStatus(Profile).IsRunning);
        Assert.Equal(installed, File.Exists(ModTarget));
        if (installed)
        {
            Assert.Equal(modTime, File.GetLastWriteTimeUtc(ModTarget));
            Assert.Equal(modBytes, File.ReadAllBytes(ModTarget));
        }
    }
}
