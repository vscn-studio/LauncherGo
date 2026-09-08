using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class CacheMaintenanceTests
{
    [Fact]
    public async Task Cleanup_SkipsAnotherCollector()
    {
        var root = Path.Combine(Path.GetTempPath(), "launchergo-cache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var collector = CacheDirectoryLease.TryAcquireForCleanup(root, ".cleanup-lock");
            Assert.NotNull(collector);
            Assert.Equal(CacheCleanupResult.RetryLater, await CacheMaintenance.CleanAsync(root, CacheKind.Host, default));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LargeCache_IsDeletedInBoundedPassesAndSurvivesCancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), "launchergo-cache-test-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "large");
        Directory.CreateDirectory(directory);
        try
        {
            using (CacheDirectoryLease.Acquire(directory)) { }
            for (var index = 0; index < 900; index++) File.WriteAllText(Path.Combine(directory, index + ".dll"), "cache");
            Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddMinutes(-10));
            Assert.Equal(CacheCleanupResult.MoreWork, await CacheMaintenance.CleanAsync(root, CacheKind.Host, default));
            Assert.False(Directory.Exists(directory));
            Assert.Single(Directory.EnumerateDirectories(root));
            using (var cancel = new CancellationTokenSource(80))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CacheMaintenance.CleanAsync(root, CacheKind.Host, cancel.Token));
            }
            for (var pass = 0; pass < 5 && Directory.EnumerateDirectories(root).Any(); pass++)
                await CacheMaintenance.CleanAsync(root, CacheKind.Host, default);
            Assert.Empty(Directory.EnumerateDirectories(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Cleanup_SkipsBusyRootWithoutWaitingForStaging()
    {
        var root = Path.Combine(Path.GetTempPath(), "launchergo-cache-test-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "busy");
        Directory.CreateDirectory(directory);
        using (CacheDirectoryLease.Acquire(directory)) { }
        Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddMinutes(-10));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staging = Task.Run(() =>
        {
            using var gate = CacheDirectoryLease.EnterRoot(root, wait: true);
            entered.SetResult();
            release.Wait();
        });
        try
        {
            await entered.Task;
            Assert.Equal(CacheCleanupResult.RetryLater,
                await Task.Run(() => CacheMaintenance.CleanAsync(root, CacheKind.Host, default)).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(Directory.Exists(directory));
        }
        finally { release.Set(); await staging; Directory.Delete(root, true); }
    }

    [Fact]
    public async Task GatewayProcess_ProtectsRuntimeAfterLauncherLeaseEndsAndReleasesOnCrash()
    {
        var projectOutput = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "LauncherGo.GatewayHost", "bin", "Release", "net10.0"));
        var root = Path.Combine(Path.GetTempPath(), "launchergo-cache-test-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(root);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var backendPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        Process? process = null;
        try
        {
            var config = Path.Combine(root, "config.json");
            var state = Path.Combine(root, "state.json");
            await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new TcpGatewaySettings
            {
                ListenHost = "127.0.0.1", ListenPort = port,
                Backends = [new TcpGatewayBackend { Id = "test", Host = "127.0.0.1", Port = backendPort }]
            }));
            using var prepared = ServerHostRuntimeStager.Prepare(Path.Combine(projectOutput, "LauncherGo.GatewayHost.exe"), cache);
            var start = new ProcessStartInfo(prepared.ExecutablePath) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "--config", config, "--state", state, "--stop-signal", Path.Combine(root, "stop"), "--reload-signal", Path.Combine(root, "reload") })
                start.ArgumentList.Add(arg);
            process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            while (!File.Exists(state) || JsonSerializer.Deserialize<TcpGatewayRuntimeStatus>(await File.ReadAllTextAsync(state), options)?.IsListening != true)
            {
                Assert.False(process.HasExited);
                await Task.Delay(50, timeout.Token);
            }
            prepared.Dispose();
            File.SetLastWriteTimeUtc(Path.Combine(Path.GetDirectoryName(prepared.ExecutablePath)!, ".complete"), DateTime.UtcNow.AddMinutes(-10));
            await CacheMaintenance.CleanAsync(cache, CacheKind.Host, default);
            Assert.True(File.Exists(prepared.ExecutablePath));
            Assert.False(process.HasExited);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await CacheMaintenance.CleanAsync(cache, CacheKind.Host, default);
            Assert.Empty(Directory.EnumerateDirectories(cache));
        }
        finally
        {
            if (process is { HasExited: false }) { process.Kill(true); await process.WaitForExitAsync(); }
            process?.Dispose();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task UpdaterScript_HoldsLeaseWhileWaitingForLauncherExit()
    {
        var root = Path.Combine(Path.GetTempPath(), "launchergo-cache-test-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "update");
        Directory.CreateDirectory(directory);
        Process? process = null;
        try
        {
            using var download = CacheDirectoryLease.Acquire(directory);
            var script = Path.Combine(directory, "apply-update.ps1");
            await File.WriteAllTextAsync(script, LauncherUpdateService.BuildUpdateScript());
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-ParentProcessId", Environment.ProcessId.ToString() })
                start.ArgumentList.Add(arg);
            process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(Path.Combine(directory, ".updater-ready")))
            {
                Assert.False(process.HasExited);
                await Task.Delay(50, timeout.Token);
            }
            download.Dispose();
            Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddHours(-1));
            await CacheMaintenance.CleanAsync(root, CacheKind.Update, default);
            Assert.True(File.Exists(script));
            process.Kill(true);
            await process.WaitForExitAsync();
            await CacheMaintenance.CleanAsync(root, CacheKind.Update, default);
            Assert.Empty(Directory.EnumerateDirectories(root));
        }
        finally
        {
            if (process is { HasExited: false }) { process.Kill(true); await process.WaitForExitAsync(); }
            process?.Dispose();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SharedLeases_ProtectEveryFileUntilLastUserExits()
    {
        var root = Path.Combine(Path.GetTempPath(), "launchergo-cache-test-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "shared");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "dependency.dll"), "runtime");
            using var first = CacheDirectoryLease.Acquire(directory);
            using var second = CacheDirectoryLease.Acquire(directory);
            Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddMinutes(-10));
            await CacheMaintenance.CleanAsync(root, CacheKind.Host, default);
            first.Dispose();
            await CacheMaintenance.CleanAsync(root, CacheKind.Host, default);
            Assert.True(File.Exists(Path.Combine(directory, "dependency.dll")));
            second.Dispose();
            Assert.Equal(CacheCleanupResult.Done, await CacheMaintenance.CleanAsync(root, CacheKind.Host, default));
            Assert.Empty(Directory.EnumerateDirectories(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Cleanup_CollectsAbandonedStageAndResumesIsolatedDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "launchergo-cache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var name in new[] { "unfinished.tmp", ".retired-" + Guid.NewGuid().ToString("N") })
            {
                var directory = Path.Combine(root, name);
                Directory.CreateDirectory(Path.Combine(directory, "nested"));
                File.WriteAllText(Path.Combine(directory, "nested", "part.dll"), "incomplete");
                using (CacheDirectoryLease.Acquire(directory)) { }
                Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddMinutes(-10));
            }
            await CacheMaintenance.CleanAsync(root, CacheKind.Host, default);
            Assert.Empty(Directory.EnumerateDirectories(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Prepare_ConcurrentCallsReuseVersionAndSurviveCleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), "launchergo-cache-test-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "LauncherGo.ServerHost.exe"), "host");
        ServerHostRuntimeStager.PreparedHost[] prepared = [];
        try
        {
            prepared = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
                ServerHostRuntimeStager.Prepare(Path.Combine(source, "LauncherGo.ServerHost.exe"), cache))));
            Assert.Single(prepared.Select(host => host.ExecutablePath).Distinct());
            File.SetLastWriteTimeUtc(Path.Combine(Path.GetDirectoryName(prepared[0].ExecutablePath)!, ".complete"), DateTime.UtcNow.AddHours(-1));
            await CacheMaintenance.CleanAsync(cache, CacheKind.Host, default);
            Assert.True(File.Exists(prepared[0].ExecutablePath));
        }
        finally
        {
            foreach (var host in prepared) host.Dispose();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Queue_CoalescesRequestsWithoutRunningCleanupOnCaller()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var queue = new CacheCleanupQueue(async (_, _, token) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return CacheCleanupResult.Done;
        }, TimeSpan.FromMilliseconds(100));
        for (var index = 0; index < 1000; index++) queue.Request("test-root", CacheKind.Host);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);
        release.SetResult();
        await queue.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Queue_RetriesFailureAndNeverRunsTwoCleanupsTogether()
    {
        var calls = 0;
        var active = 0;
        using var queue = new CacheCleanupQueue(async (_, _, token) =>
        {
            Assert.Equal(1, Interlocked.Increment(ref active));
            await Task.Delay(10, token);
            Interlocked.Decrement(ref active);
            if (Interlocked.Increment(ref calls) == 1) throw new IOException("busy");
            return CacheCleanupResult.Done;
        }, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(10));
        queue.Request("one", CacheKind.Host);
        queue.Request("two", CacheKind.Update);
        await queue.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, calls);
    }
}
