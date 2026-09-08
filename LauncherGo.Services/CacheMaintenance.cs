using System.Diagnostics;
using System.Management;

namespace LauncherGo.Services;

internal enum CacheKind { Host, Update }
internal enum CacheCleanupResult { Done, RetryLater, MoreWork }

internal static class CacheMaintenance
{
    private static readonly CacheCleanupQueue Queue = new(CleanAsync);

    internal static void Request(string root, CacheKind kind) => Queue.Request(root, kind);

    internal static void RegisterWorkspace(string root)
    {
        Request(Path.Combine(root, ".runtime", "server-host"), CacheKind.Host);
        Request(Path.Combine(root, ".runtime", "gateway-host"), CacheKind.Host);
    }

    internal static async Task<CacheCleanupResult> CleanAsync(string root, CacheKind kind, CancellationToken token)
    {
        if (!Directory.Exists(root) || IsLink(root)) return CacheCleanupResult.Done;
        using var passLease = CacheDirectoryLease.TryAcquireForCleanup(root, ".cleanup-lock");
        if (passLease is null) return CacheCleanupResult.RetryLater;
        var budget = new CleanupBudget();
        var remaining = false;
        HashSet<string>? legacyHosts = null;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            token.ThrowIfCancellationRequested();
            if (budget.Expired) return CacheCleanupResult.MoreWork;
            if (IsLink(directory)) continue;
            var retired = Path.GetFileName(directory).StartsWith(".retired-", StringComparison.Ordinal);
            var leased = File.Exists(Path.Combine(directory, CacheDirectoryLease.ProtocolMarker));
            if (!retired && kind == CacheKind.Host && !leased &&
                !File.Exists(Path.Combine(directory, ".complete")) &&
                !directory.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            if (!retired && !leased && kind == CacheKind.Host)
                legacyHosts ??= GetLegacyHostDirectories();
            if (!retired && !leased && kind == CacheKind.Update && LegacyUpdateIsRunning(directory))
            {
                remaining = true;
                continue;
            }

            string? isolated = null;
            FileStream? deletionLease = null;
            // The mutex is thread-affine: never await while holding it.
            using (var gate = CacheDirectoryLease.EnterRoot(root, wait: false))
            {
                if (gate is null) return CacheCleanupResult.RetryLater;
                if (!Directory.Exists(directory)) continue;
                if (!retired)
                {
                    var marker = Path.Combine(directory, ".complete");
                    var changed = File.Exists(marker) ? File.GetLastWriteTimeUtc(marker) : Directory.GetLastWriteTimeUtc(directory);
                    if (DateTime.UtcNow - changed < TimeSpan.FromMinutes(2))
                    {
                        remaining = true;
                        continue;
                    }
                    if (!leased)
                    {
                        if (kind == CacheKind.Host)
                        {
                            if (legacyHosts!.Contains("*") || legacyHosts.Contains(directory))
                            {
                                remaining = true;
                                continue;
                            }
                        }
                        else if (DateTime.UtcNow - changed < TimeSpan.FromDays(1))
                        {
                            // Old updater scripts do not participate in the lease protocol.
                            remaining = true;
                            continue;
                        }
                    }
                }
                deletionLease = CacheDirectoryLease.TryAcquireForCleanup(directory);
                if (deletionLease is null) { remaining = true; continue; }
                try
                {
                    isolated = retired ? directory : Path.Combine(root, ".retired-" + Guid.NewGuid().ToString("N"));
                    if (!retired)
                    {
                        // Windows cannot rename a directory with an open child handle.
                        // New lease acquisitions share this root mutex, so closing here is safe.
                        deletionLease.Dispose();
                        Directory.Move(directory, isolated);
                        deletionLease = CacheDirectoryLease.TryAcquireForCleanup(isolated);
                        if (deletionLease is null) { remaining = true; continue; }
                    }
                }
                catch (IOException) { deletionLease?.Dispose(); remaining = true; continue; }
                catch (UnauthorizedAccessException) { deletionLease?.Dispose(); remaining = true; continue; }
            }
            using (deletionLease)
            {
                try
                {
                    if (!await DeleteBatchAsync(isolated!, budget, token, keepRoot: true).ConfigureAwait(false))
                        return CacheCleanupResult.MoreWork;
                    using var gate = CacheDirectoryLease.EnterRoot(root, wait: false);
                    if (gate is null) { remaining = true; continue; }
                    deletionLease!.Dispose();
                    File.Delete(Path.Combine(isolated!, CacheDirectoryLease.FileName));
                    Directory.Delete(isolated!);
                }
                catch (IOException) { remaining = true; }
                catch (UnauthorizedAccessException) { remaining = true; }
            }
        }
        return remaining ? CacheCleanupResult.RetryLater : CacheCleanupResult.Done;
    }

    private static async Task<bool> DeleteBatchAsync(string directory, CleanupBudget budget, CancellationToken token, bool keepRoot = false)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            token.ThrowIfCancellationRequested();
            if (Path.GetFileName(file) == CacheDirectoryLease.FileName) continue;
            if (budget.Expired) return false;
            var attributes = File.GetAttributes(file);
            if ((attributes & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) == FileAttributes.ReadOnly)
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            File.Delete(file);
            await budget.YieldAsync(token).ConfigureAwait(false);
        }
        // Do not follow links, even inside a cache that was created by an older version.
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            token.ThrowIfCancellationRequested();
            if (budget.Expired) return false;
            if (IsLink(child)) Directory.Delete(child);
            else if (!await DeleteBatchAsync(child, budget, token).ConfigureAwait(false)) return false;
        }
        if (!keepRoot)
        {
            File.Delete(Path.Combine(directory, CacheDirectoryLease.FileName));
            Directory.Delete(directory);
            await budget.YieldAsync(token).ConfigureAwait(false);
        }
        return true;
    }

    private sealed class CleanupBudget
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _operations;
        public bool Expired => _clock.Elapsed > TimeSpan.FromSeconds(2);
        public Task YieldAsync(CancellationToken token) =>
            ++_operations % 16 == 0 ? Task.Delay(50, token) : Task.CompletedTask;
    }

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static HashSet<string> GetLegacyHostDirectories()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "LauncherGo.ServerHost", "LauncherGo.GatewayHost" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            if (process.MainModule?.FileName is { } path) result.Add(Path.GetDirectoryName(path)!);
                            else result.Add("*");
                        }
                    }
                    catch { result.Add("*"); }
                }
            }
        }
        return result;
    }

    private static bool LegacyUpdateIsRunning(string directory)
    {
        if (!OperatingSystem.IsWindows()) return true;
        var names = Directory.EnumerateFiles(directory, "*.exe").Select(Path.GetFileNameWithoutExtension)
            .Concat(["powershell", "pwsh"]).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            foreach (var process in Process.GetProcessesByName(name!))
            {
                using (process)
                {
                    try
                    {
                        if (process.HasExited) continue;
                        if (name is not ("powershell" or "pwsh")) return true;
                        using var query = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId={process.Id}");
                        using var rows = query.Get();
                        foreach (ManagementObject row in rows)
                        {
                            using (row)
                            {
                                if (row["CommandLine"] is not string command || command.Contains(directory, StringComparison.OrdinalIgnoreCase))
                                    return true;
                            }
                        }
                    }
                    catch { return true; }
                }
            }
        }
        return false;
    }
}

internal sealed class CacheCleanupQueue(
    Func<string, CacheKind, CancellationToken, Task<CacheCleanupResult>> clean,
    TimeSpan? initialDelay = null,
    TimeSpan? retryDelay = null) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheKind> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private Task? _worker;

    internal void Request(string root, CacheKind kind)
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) return;
            _pending[root] = kind;
            _worker ??= Task.Run(RunAsync);
            if (_wake.CurrentCount == 0) _wake.Release();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await Task.Delay(initialDelay ?? TimeSpan.FromSeconds(10), _stop.Token).ConfigureAwait(false);
            while (true)
            {
                _wake.Wait(0);
                KeyValuePair<string, CacheKind>[] work;
                lock (_gate) { work = _pending.ToArray(); _pending.Clear(); }
                var moreWork = false;
                foreach (var item in work)
                {
                    var result = CacheCleanupResult.RetryLater;
                    try { result = await clean(item.Key, item.Value, _stop.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                    catch (Exception ex) { Trace.TraceWarning("Cache cleanup deferred: {0}", ex.Message); }
                    moreWork |= result == CacheCleanupResult.MoreWork;
                    if (result != CacheCleanupResult.Done) lock (_gate) _pending.TryAdd(item.Key, item.Value);
                }
                lock (_gate)
                {
                    if (_pending.Count == 0) { _worker = null; return; }
                }
                await _wake.WaitAsync(retryDelay ?? (moreWork ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(5)), _stop.Token).ConfigureAwait(false);
                await Task.Delay(initialDelay ?? TimeSpan.FromSeconds(10), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public void Dispose() => _stop.Cancel();

    internal Task Completion { get { lock (_gate) return _worker ?? Task.CompletedTask; } }
}
