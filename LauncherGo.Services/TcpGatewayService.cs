using System.Diagnostics;
using System.Text.Json;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

/// <summary>
///     管理独立 GatewayHost 进程并读取其状态快照。
/// </summary>
public sealed class TcpGatewayService : ITcpGatewayService
{
    private const string GatewayHostExecutableName = "LauncherGo.GatewayHost.exe";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _statusGate = new();
    private TcpGatewayRuntimeStatus _status = new();
    private Process? _process;

    public event EventHandler<TcpGatewayRuntimeStatus>? StatusChanged;

    public TcpGatewayRuntimeStatus GetCurrentStatus()
    {
        lock (_statusGate)
        {
            return CloneStatus(_status);
        }
    }

    public async Task<TcpGatewayRuntimeStatus> RefreshStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = await Task.Run(ReadStatus, cancellationToken).ConfigureAwait(false);
        SetStatus(status);
        return status;
    }

    public async Task StartAsync(TcpGatewaySettings settings, CancellationToken cancellationToken = default)
    {
        TcpGatewayHostRunner.ValidateSettings(settings);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var control = await BackgroundHostFiles.AcquireControlAsync(WorkspacePathHelper.GatewayRoot, cancellationToken);
            var current = await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
            if (current.IsRunning)
            {
                throw new InvalidOperationException("TCP gateway is already running.");
            }

            WorkspacePathHelper.EnsureWorkspace();
            using (BackgroundHostFiles.AcquireHost(WorkspacePathHelper.GatewayRoot)) { }
            DotNetRuntimeRequirement.EnsureForHost(ResolveGatewayHostPath());
            TryDeleteFile(WorkspacePathHelper.GatewayStopSignalPath);
            TryDeleteFile(WorkspacePathHelper.GatewayReloadSignalPath);
            TryDeleteFile(WorkspacePathHelper.GatewayStatePath);
            await WriteSettingsAsync(settings, cancellationToken).ConfigureAwait(false);

            using var prepared = await Task.Run(() => ServerHostRuntimeStager.Prepare(
                ResolveGatewayHostPath(),
                WorkspacePathHelper.GatewayHostRuntimeRoot, cancellationToken), cancellationToken).ConfigureAwait(false);
            var hostPath = prepared.ExecutablePath;
            if (!File.Exists(hostPath))
            {
                throw new FileNotFoundException("GatewayHost executable was not found.", hostPath);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = hostPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(hostPath) ?? AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add("--config");
            startInfo.ArgumentList.Add(WorkspacePathHelper.GatewayConfigPath);
            startInfo.ArgumentList.Add("--state");
            startInfo.ArgumentList.Add(WorkspacePathHelper.GatewayStatePath);
            startInfo.ArgumentList.Add("--stop-signal");
            startInfo.ArgumentList.Add(WorkspacePathHelper.GatewayStopSignalPath);
            startInfo.ArgumentList.Add("--reload-signal");
            startInfo.ArgumentList.Add(WorkspacePathHelper.GatewayReloadSignalPath);

            _process?.Dispose();
            _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start GatewayHost.");
            try
            {
                var started = await WaitForStartedAsync(_process, cancellationToken).ConfigureAwait(false);
                SetStatus(started);
            }
            catch
            {
                if (!_process.HasExited) { _process.Kill(true); await _process.WaitForExitAsync(); }
                _process.Dispose();
                _process = null;
                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<TcpGatewayRuntimeStatus> ReloadAsync(
        TcpGatewaySettings settings,
        CancellationToken cancellationToken = default)
    {
        TcpGatewayHostRunner.ValidateSettings(settings);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var control = await BackgroundHostFiles.AcquireControlAsync(WorkspacePathHelper.GatewayRoot, cancellationToken);
            var current = await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
            if (!current.IsRunning)
            {
                throw new InvalidOperationException("TCP gateway is not running.");
            }

            WorkspacePathHelper.EnsureWorkspace();
            await WriteSettingsAsync(settings, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                    WorkspacePathHelper.GatewayReloadSignalPath,
                    Guid.NewGuid().ToString("N"),
                    cancellationToken)
                .ConfigureAwait(false);

            // GatewayHost checks control signals every 300 ms; wait for its next status snapshot.
            await Task.Delay(TimeSpan.FromMilliseconds(450), cancellationToken).ConfigureAwait(false);
            return await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task RecordRoutingHistoryAsync(
        TcpGatewayRoutingHistoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        WorkspacePathHelper.EnsureWorkspace();
        var item = new TcpGatewayRoutingHistoryEntry
        {
            OccurredAtUtc = entry.OccurredAtUtc == default ? DateTimeOffset.UtcNow : entry.OccurredAtUtc,
            Action = entry.Action?.Trim() ?? string.Empty,
            SourceServerId = entry.SourceServerId?.Trim() ?? string.Empty,
            TargetServerId = entry.TargetServerId?.Trim() ?? string.Empty,
            Details = entry.Details?.Trim() ?? string.Empty
        };
        await File.AppendAllTextAsync(
                WorkspacePathHelper.GatewayRoutingHistoryPath,
                JsonSerializer.Serialize(item, JsonOptions) + Environment.NewLine,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TcpGatewayRoutingHistoryEntry>> GetRoutingHistoryAsync(
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(WorkspacePathHelper.GatewayRoutingHistoryPath))
        {
            return [];
        }

        var items = new List<TcpGatewayRoutingHistoryEntry>();
        using var stream = new FileStream(
            WorkspacePathHelper.GatewayRoutingHistoryPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<TcpGatewayRoutingHistoryEntry>(line, JsonOptions);
                if (entry is not null) items.Add(entry);
            }
            catch (JsonException)
            {
                // A concurrent writer may have left an incomplete final line. It will be read on the next refresh.
            }
        }

        return items
            .OrderByDescending(static item => item.OccurredAtUtc)
            .Take(Math.Clamp(take, 1, 1000))
            .ToList();
    }

    public async Task StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var control = await BackgroundHostFiles.AcquireControlAsync(WorkspacePathHelper.GatewayRoot, cancellationToken);
            var status = await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
            using var process = BackgroundHostFiles.ResolveProcess(status.ProcessId, status.ProcessStartTimeUtcTicks, status.ExecutablePath);
            if (process is null || process.HasExited)
            {
                SetStatus(new TcpGatewayRuntimeStatus
                {
                    LastError = status.LastError,
                    Backends = status.Backends
                });
                return;
            }

            Directory.CreateDirectory(WorkspacePathHelper.GatewayRoot);
            await File.WriteAllTextAsync(WorkspacePathHelper.GatewayStopSignalPath, "stop", cancellationToken)
                .ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(gracefulTimeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(10) : gracefulTimeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            var stopped = await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
            stopped.IsRunning = false;
            stopped.IsListening = false;
            stopped.ProcessId = null;
            SetStatus(stopped);
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            CacheMaintenance.Request(WorkspacePathHelper.GatewayHostRuntimeRoot, CacheKind.Host);
            _operationGate.Release();
        }
    }

    private static async Task WriteSettingsAsync(TcpGatewaySettings settings, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(WorkspacePathHelper.GatewayConfigPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(WorkspacePathHelper.GatewayConfigPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, WorkspacePathHelper.GatewayConfigPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private async Task<TcpGatewayRuntimeStatus> WaitForStartedAsync(Process process, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                var failed = ReadStatus();
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(failed.LastError)
                    ? "GatewayHost exited before it started listening."
                    : failed.LastError);
            }

            var status = ReadStatus();
            if (status.IsListening && status.ProcessId == process.Id)
            {
                return status;
            }

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("Timed out while waiting for GatewayHost to listen.");
    }

    private TcpGatewayRuntimeStatus ReadStatus()
    {
        TcpGatewayRuntimeStatus status;
        try
        {
            status = File.Exists(WorkspacePathHelper.GatewayStatePath)
                ? JsonSerializer.Deserialize<TcpGatewayRuntimeStatus>(File.ReadAllText(WorkspacePathHelper.GatewayStatePath), JsonOptions) ?? new TcpGatewayRuntimeStatus()
                : new TcpGatewayRuntimeStatus();
        }
        catch
        {
            status = GetCurrentStatus();
        }

        using var process = BackgroundHostFiles.ResolveProcess(status.ProcessId, status.ProcessStartTimeUtcTicks, status.ExecutablePath);
        status.RoutingHistoryLogPath = WorkspacePathHelper.GatewayRoutingHistoryPath;
        status.IsRunning = process is not null;
        if (status.IsRunning && (!BackgroundHostFiles.IsFresh(status.HeartbeatUtc) ||
            !System.Net.IPEndPoint.TryParse(status.ListenAddress, out var endpoint) ||
            !BackgroundHostFiles.IsListening(endpoint.Address.ToString(), endpoint.Port)))
        {
            status.IsListening = false;
            status.LastError = "GatewayHost listener is unavailable or heartbeat timed out; stop the host before restarting.";
        }
        if (!status.IsRunning)
        {
            status.IsListening = false;
            status.ProcessId = null;
            foreach (var backend in status.Backends)
            {
                backend.IsHealthy = false;
            }
        }

        return status;
    }

    private void SetStatus(TcpGatewayRuntimeStatus status)
    {
        var snapshot = CloneStatus(status);
        lock (_statusGate)
        {
            _status = snapshot;
        }

        StatusChanged?.Invoke(this, CloneStatus(snapshot));
    }

    private static TcpGatewayRuntimeStatus CloneStatus(TcpGatewayRuntimeStatus source)
    {
        return new TcpGatewayRuntimeStatus
        {
            IsRunning = source.IsRunning,
            IsListening = source.IsListening,
            RequiresRestart = source.RequiresRestart,
            PendingRestartReason = source.PendingRestartReason,
            ProcessId = source.ProcessId,
            ProcessStartTimeUtcTicks = source.ProcessStartTimeUtcTicks,
            ExecutablePath = source.ExecutablePath,
            HeartbeatUtc = source.HeartbeatUtc,
            StartedAtUtc = source.StartedAtUtc,
            ListenAddress = source.ListenAddress,
            ActiveConnections = source.ActiveConnections,
            AcceptedConnections = source.AcceptedConnections,
            RejectedConnections = source.RejectedConnections,
            FailedConnections = source.FailedConnections,
            ClientToBackendBytes = source.ClientToBackendBytes,
            BackendToClientBytes = source.BackendToClientBytes,
            LastError = source.LastError,
            RoutingHistoryLogPath = source.RoutingHistoryLogPath,
            RoutingHistory = source.RoutingHistory.Select(item => new TcpGatewayRoutingHistoryEntry
            {
                OccurredAtUtc = item.OccurredAtUtc,
                Action = item.Action,
                SourceServerId = item.SourceServerId,
                TargetServerId = item.TargetServerId,
                Details = item.Details
            }).ToList(),
            Backends = source.Backends.Select(backend => new TcpGatewayBackendRuntimeStatus
            {
                Id = backend.Id,
                Name = backend.Name,
                Address = backend.Address,
                Enabled = backend.Enabled,
                RoutingState = backend.RoutingState,
                Weight = backend.Weight,
                ProfileId = backend.ProfileId,
                IsHealthy = backend.IsHealthy,
                ActiveConnections = backend.ActiveConnections,
                LastError = backend.LastError,
                Statistics = CloneStatistics(backend.Statistics)
            }).ToList()
        };
    }

    private static TcpGatewayBackendStatistics CloneStatistics(TcpGatewayBackendStatistics? source)
    {
        source ??= new TcpGatewayBackendStatistics();
        return new TcpGatewayBackendStatistics
        {
            StartedAtUtc = source.StartedAtUtc,
            ClientToBackendBytes = source.ClientToBackendBytes,
            BackendToClientBytes = source.BackendToClientBytes,
            CurrentClientToBackendMbps = source.CurrentClientToBackendMbps,
            CurrentBackendToClientMbps = source.CurrentBackendToClientMbps,
            PeakClientToBackendMbps = source.PeakClientToBackendMbps,
            PeakBackendToClientMbps = source.PeakBackendToClientMbps,
            AverageClientToBackendMbps = source.AverageClientToBackendMbps,
            AverageBackendToClientMbps = source.AverageBackendToClientMbps,
            CurrentConnections = source.CurrentConnections,
            PeakConnections = source.PeakConnections,
            EstablishedConnections = source.EstablishedConnections,
            FailedConnections = source.FailedConnections,
            ConnectionEstablishRatePerMinute = source.ConnectionEstablishRatePerMinute,
            ConnectionFailureRate = source.ConnectionFailureRate,
            AverageBackendConnectLatencyMilliseconds = source.AverageBackendConnectLatencyMilliseconds,
            LastDisconnectReason = source.LastDisconnectReason,
            LastDisconnectAtUtc = source.LastDisconnectAtUtc,
            RecentDisconnects = source.RecentDisconnects.Select(record => new TcpGatewayDisconnectRecord
            {
                OccurredAtUtc = record.OccurredAtUtc,
                Type = record.Type,
                Details = record.Details
            }).ToList()
        };
    }

    private static string ResolveGatewayHostPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, GatewayHostExecutableName),
            Path.Combine(AppContext.BaseDirectory, "GatewayHost", GatewayHostExecutableName),
            Path.Combine(Environment.CurrentDirectory, GatewayHostExecutableName)
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A stale runtime file will be replaced when possible.
        }
    }
}
