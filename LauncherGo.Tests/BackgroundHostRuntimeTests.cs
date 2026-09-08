using System.Diagnostics;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class BackgroundHostRuntimeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "launchergo-host-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void FrameworkDependentConfigReadsBothRequirements()
    {
        var requirements = DotNetRuntimeRequirement.ReadRequirements("""
            {"runtimeOptions":{"frameworks":[
              {"name":"Microsoft.NETCore.App","version":"10.0.8"},
              {"name":"Microsoft.AspNetCore.App","version":"10.0.8"}]}}
            """);
        Assert.Equal(2, requirements.Count);
        Assert.Equal(new Version(10, 0, 8), requirements["Microsoft.AspNetCore.App"]);
    }

    [Fact]
    public void PortableIncludedFrameworksDoNotRequireSystemRuntime()
    {
        var requirements = DotNetRuntimeRequirement.ReadRequirements("""
            {"runtimeOptions":{"includedFrameworks":[
              {"name":"Microsoft.NETCore.App","version":"10.0.11"},
              {"name":"Microsoft.AspNetCore.App","version":"10.0.11"}]}}
            """);
        Assert.Empty(requirements);
    }

    [Fact]
    public void NewerAspNetPatchRequiresMatchingCorePatch()
    {
        InstallFramework("Microsoft.NETCore.App", "10.0.8");
        InstallFramework("Microsoft.AspNetCore.App", "10.0.11");
        var requirements = new Dictionary<string, Version>
        {
            ["Microsoft.NETCore.App"] = new(10, 0, 8),
            ["Microsoft.AspNetCore.App"] = new(10, 0, 8)
        };
        DotNetRuntimeRequirement.IncludeAspNetCoreDependency(root, requirements);
        Assert.Equal(new Version(10, 0, 11), requirements["Microsoft.NETCore.App"]);
        Assert.False(DotNetRuntimeRequirement.HasCompatibleFramework(root, "Microsoft.NETCore.App", requirements["Microsoft.NETCore.App"]));
        InstallFramework("Microsoft.NETCore.App", "10.0.12");
        Assert.True(DotNetRuntimeRequirement.HasCompatibleFramework(root, "Microsoft.NETCore.App", requirements["Microsoft.NETCore.App"]));
    }

    [Fact]
    public void DifferentMajorAndIncompleteRuntimeDoNotSatisfyRequirement()
    {
        InstallFramework("Microsoft.NETCore.App", "11.0.0");
        Directory.CreateDirectory(Path.Combine(root, "shared", "Microsoft.NETCore.App", "10.0.11"));
        Assert.False(DotNetRuntimeRequirement.HasCompatibleFramework(root, "Microsoft.NETCore.App", new(10, 0, 8)));
    }

    [Fact]
    public void ProcessIdentityRejectsReusedPidAndWrongExecutable()
    {
        using var current = Process.GetCurrentProcess();
        var ticks = current.StartTime.ToUniversalTime().Ticks;
        using var resolved = BackgroundHostFiles.ResolveProcess(current.Id, ticks, Environment.ProcessPath!);
        Assert.NotNull(resolved);
        Assert.Null(BackgroundHostFiles.ResolveProcess(current.Id, ticks + 1, Environment.ProcessPath!));
        Assert.Null(BackgroundHostFiles.ResolveProcess(current.Id, ticks, "wrong.exe"));
    }

    [Fact]
    public void HostLockPreventsDuplicateOwnerAndReleasesOnDispose()
    {
        using (BackgroundHostFiles.AcquireHost(root))
            Assert.Throws<IOException>(() => BackgroundHostFiles.AcquireHost(root));
        using var next = BackgroundHostFiles.AcquireHost(root);
    }

    [Fact]
    public async Task ControlLockWaitCanBeCancelled()
    {
        using var first = await BackgroundHostFiles.AcquireControlAsync(root, CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using var second = await BackgroundHostFiles.AcquireControlAsync(root, cancellation.Token);
        });
    }

    [Fact]
    public async Task StateRoundTripsAndFinalSnapshotMarksStopped()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "host.state.json");
        var state = new BackgroundHostState { ProcessId = 123, IsRunning = true, HeartbeatUtc = DateTimeOffset.UtcNow };
        await BackgroundHostFiles.WriteAsync(path, state);
        Assert.True(BackgroundHostFiles.Read<BackgroundHostState>(path)!.IsRunning);
        state.IsRunning = false;
        await BackgroundHostFiles.WriteAsync(path, state);
        Assert.False(BackgroundHostFiles.Read<BackgroundHostState>(path)!.IsRunning);
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        Assert.True(BackgroundHostFiles.IsFresh(state.HeartbeatUtc));
        Assert.False(BackgroundHostFiles.IsFresh(DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.False(BackgroundHostFiles.IsFresh(DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    private void InstallFramework(string name, string version)
    {
        var path = Path.Combine(root, "shared", name, version);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, name + ".deps.json"), "{}");
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
