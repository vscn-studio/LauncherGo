using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

/// <summary>
///     独立 GatewayHost 的入口和 TCP 转发实现。
/// </summary>
public static class TcpGatewayHostRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions RoutingHistoryJsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(string[] args)
    {
        GatewayHostOptions? options = null;
        try
        {
            options = GatewayHostOptions.Parse(args);
            var settings = LoadSettings(options.ConfigPath);
            ValidateSettings(settings);

            var host = new TcpGatewayHost(
                settings,
                options.ConfigPath,
                options.StatePath,
                options.StopSignalPath,
                options.ReloadSignalPath);
            await host.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            // The launcher reads this file when the host exits before it begins listening.
            if (options is not null)
            {
                try
                {
                    await WriteStateAsync(options.StatePath, new TcpGatewayRuntimeStatus
                    {
                        LastError = ex.Message
                    }).ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the original startup failure if the runtime directory is unavailable.
                }
            }

            throw;
        }
    }

    public static void ValidateSettings(TcpGatewaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.ListenHost))
        {
            throw new InvalidOperationException("Gateway listen host is required.");
        }

        if (settings.ListenPort is < 1 or > ushort.MaxValue)
        {
            throw new InvalidOperationException("Gateway listen port must be between 1 and 65535.");
        }

        if (settings.MaxConnections < 1 || settings.MaxConnectionsPerIp < 1)
        {
            throw new InvalidOperationException("Gateway connection limits must be greater than zero.");
        }

        if (settings.MaxConnectionsPerIp > settings.MaxConnections)
        {
            throw new InvalidOperationException("Per-IP connection limit cannot exceed the total limit.");
        }

        var configuredBackends = settings.Backends?.ToList() ?? [];
        if (configuredBackends.Count == 0)
        {
            throw new InvalidOperationException("At least one gateway backend is required.");
        }

        var backendIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var backend in settings.Backends ?? [])
        {
            if (string.IsNullOrWhiteSpace(backend.Id) || !backendIds.Add(backend.Id.Trim()))
            {
                throw new InvalidOperationException("Every gateway backend requires a unique ID.");
            }

            if (backend.RoutingState == TcpGatewayBackendRoutingState.Offline)
            {
                throw new InvalidOperationException("Offline is a runtime-only gateway backend state.");
            }

            if (string.IsNullOrWhiteSpace(backend.Host) || backend.Port is < 1 or > ushort.MaxValue)
            {
                throw new InvalidOperationException("Every gateway backend requires a host and port.");
            }

            if (backend.Weight is < 0 or > 100)
            {
                throw new InvalidOperationException("Gateway backend weight must be between 0 and 100.");
            }

        }

        _ = IpRule.ParseMany(settings.AllowListText);
        _ = IpRule.ParseMany(settings.BlockListText);
    }

    internal static TcpGatewaySettings LoadSettings(string configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            throw new FileNotFoundException("Gateway configuration file was not found.", configPath);
        }

        var settings = JsonSerializer.Deserialize<TcpGatewaySettings>(File.ReadAllText(configPath), JsonOptions);
        return settings ?? throw new InvalidOperationException("Gateway configuration file is invalid.");
    }

    internal static async Task WriteStateAsync(string statePath, TcpGatewayRuntimeStatus status)
    {
        var directory = Path.GetDirectoryName(statePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Gateway state directory is unavailable.");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(statePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(status, JsonOptions)).ConfigureAwait(false);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temporaryPath, statePath, overwrite: true);
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 4)
                {
                    // Readers may temporarily hold the previous snapshot without delete sharing on Windows.
                    await Task.Delay(TimeSpan.FromMilliseconds(25 * (attempt + 1))).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // The next write uses a different temporary path.
                }
            }
        }
    }

    private sealed class TcpGatewayHost
    {
        private const int RelayBufferSize = 16 * 1024;
        private readonly string _configPath;
        private readonly string _statePath;
        private readonly string _stopSignalPath;
        private readonly string _reloadSignalPath;
        private readonly string _listenHost;
        private readonly int _listenPort;
        private readonly string _routingHistoryPath;
        private readonly object _stateGate = new();
        private readonly SemaphoreSlim _stateWriteGate = new(1, 1);
        private readonly Dictionary<string, int> _connectionsByIp = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BackendState> _backendStates = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<long, Task> _sessions = new();
        private readonly ConcurrentDictionary<string, DateTimeOffset> _consumedTransferTickets = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource _shutdownCts = new();
        private GatewayRuntimeConfiguration _configuration;
        private TcpListener? _listener;
        private DateTimeOffset _startedAtUtc;
        private string _listenAddress = string.Empty;
        private string _lastError = string.Empty;
        private string _pendingRestartReason = string.Empty;
        private bool _isListening;
        private bool _requiresRestart;
        private int _activeConnections;
        private long _acceptedConnections;
        private long _rejectedConnections;
        private long _failedConnections;
        private long _clientToBackendBytes;
        private long _backendToClientBytes;
        private long _sessionId;
        private long _selectionCursor;

        public TcpGatewayHost(
            TcpGatewaySettings settings,
            string configPath,
            string statePath,
            string stopSignalPath,
            string reloadSignalPath)
        {
            _configPath = configPath;
            _statePath = statePath;
            _stopSignalPath = stopSignalPath;
            _reloadSignalPath = reloadSignalPath;
            _listenHost = settings.ListenHost.Trim();
            _listenPort = settings.ListenPort;
            _routingHistoryPath = Path.Combine(
                Path.GetDirectoryName(statePath) ?? throw new InvalidOperationException("Gateway state directory is unavailable."),
                "routing-history.jsonl");
            _configuration = CreateConfiguration(settings);
        }

        public async Task RunAsync()
        {
            using var stopWatcherCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
            var stopWatcher = WatchControlSignalsAsync(stopWatcherCts.Token);
            try
            {
                _startedAtUtc = DateTimeOffset.UtcNow;
                await RefreshBackendHealthAsync(_shutdownCts.Token).ConfigureAwait(false);

                var startupConfiguration = GetConfiguration();
                var listenAddress = await ResolveListenAddressAsync(_listenHost, _shutdownCts.Token).ConfigureAwait(false);
                _listener = new TcpListener(listenAddress, _listenPort);
                _listener.Start(Math.Clamp(startupConfiguration.Settings.MaxConnections * 2, 16, 10_000));
                _listenAddress = FormatEndpoint(listenAddress, _listenPort);
                _isListening = true;
                await WriteHostStateAsync().ConfigureAwait(false);

                var healthTask = RunHealthLoopAsync(_shutdownCts.Token);
                var stateTask = RunStateLoopAsync(_shutdownCts.Token);
                try
                {
                    await AcceptConnectionsAsync(_shutdownCts.Token).ConfigureAwait(false);
                }
                finally
                {
                    _shutdownCts.Cancel();
                    await IgnoreCancellationAsync(healthTask).ConfigureAwait(false);
                    await IgnoreCancellationAsync(stateTask).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
            {
                // Normal stop request.
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                throw;
            }
            finally
            {
                _isListening = false;
                _listener?.Stop();
                _shutdownCts.Cancel();
                await AwaitSessionsAsync().ConfigureAwait(false);
                await WriteHostStateAsync().ConfigureAwait(false);
                stopWatcherCts.Cancel();
                await IgnoreCancellationAsync(stopWatcher).ConfigureAwait(false);
                _shutdownCts.Dispose();
            }
        }

        private async Task AcceptConnectionsAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                client.NoDelay = true;
                var sessionId = Interlocked.Increment(ref _sessionId);
                var task = HandleClientAsync(client, cancellationToken);
                _sessions[sessionId] = task;
                _ = task.ContinueWith(
                    static (completedTask, state) =>
                    {
                        var (sessions, id) = ((ConcurrentDictionary<long, Task> Sessions, long Id))state!;
                        sessions.TryRemove(id, out Task? removedTask);
                    },
                    (_sessions, sessionId),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        private async Task HandleClientAsync(
            TcpClient client,
            CancellationToken shutdownToken)
        {
            using (client)
            {
                var remoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                var configuration = GetConfiguration();
                if (remoteAddress is null ||
                    !IsClientAllowed(remoteAddress, configuration) ||
                    !TryReserveConnection(remoteAddress, configuration))
                {
                    Interlocked.Increment(ref _rejectedConnections);
                    return;
                }

                BackendState? selectedBackend = null;
                GatewayTransferTicket? transferTicket = null;
                try
                {
                    Interlocked.Increment(ref _acceptedConnections);
                    var preamble = await GatewayTransferProtocol.ReadPreambleAsync(client.GetStream(), shutdownToken)
                        .ConfigureAwait(false);
                    if (!preamble.IsValid)
                    {
                        Interlocked.Increment(ref _rejectedConnections);
                        return;
                    }

                    IReadOnlyList<BackendState> candidates;
                    if (preamble.HasTransferPreamble)
                    {
                        if (!TryConsumeTransferTicket(preamble.Ticket, configuration, out transferTicket, out var transferBackend))
                        {
                            Interlocked.Increment(ref _rejectedConnections);
                            return;
                        }

                        candidates = [transferBackend];
                    }
                    else
                    {
                        candidates = GetBackendCandidates(configuration);
                    }

                    BackendState? failedBackend = null;
                    foreach (var backend in candidates)
                    {
                        var target = new TcpClient { NoDelay = true };
                        var connectStartedTimestamp = 0L;
                        try
                        {
                            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
                            connectCts.CancelAfter(TimeSpan.FromSeconds(configuration.Settings.ConnectTimeoutSec));
                            connectStartedTimestamp = Stopwatch.GetTimestamp();
                            await target.ConnectAsync(backend.Definition.Host, backend.Definition.Port, connectCts.Token)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
                        {
                            target.Dispose();
                            return;
                        }
                        catch (Exception ex)
                        {
                            backend.RecordFailedConnection(ex.Message);
                            failedBackend = backend;
                            target.Dispose();
                            continue;
                        }

                        selectedBackend = backend;
                        backend.RecordConnectionEstablished(Stopwatch.GetElapsedTime(connectStartedTimestamp));
                        if (transferTicket is not null)
                        {
                            await RecordRoutingHistoryAsync(
                                    "TicketRedirect",
                                    transferTicket.SourceServerId,
                                    backend.Definition.Id,
                                    "A one-time transfer ticket selected the target backend.")
                                .ConfigureAwait(false);
                        }
                        if (failedBackend is not null && !ReferenceEquals(failedBackend, backend))
                        {
                            await RecordRoutingHistoryAsync(
                                    "Failover",
                                    failedBackend.Definition.Id,
                                    backend.Definition.Id,
                                    "A backend connection failed and the gateway selected another healthy backend.")
                                .ConfigureAwait(false);
                        }
                        await RelayAsync(client, target, backend, preamble.InitialBytes, shutdownToken).ConfigureAwait(false);
                        return;
                    }

                    Interlocked.Increment(ref _failedConnections);
                }
                catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
                {
                    // Host shutdown cancels active sessions.
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failedConnections);
                    _lastError = ex.Message;
                }
                finally
                {
                    if (selectedBackend is null && transferTicket is not null)
                    {
                        _consumedTransferTickets.TryRemove(transferTicket.Nonce, out _);
                    }
                    selectedBackend?.DecrementConnections();
                    ReleaseConnection(remoteAddress);
                }
            }
        }

        private async Task RelayAsync(
            TcpClient client,
            TcpClient backend,
            BackendState backendState,
            ReadOnlyMemory<byte> initialClientBytes,
            CancellationToken shutdownToken)
        {
            using (backend)
            using (var relayCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken))
            {
                if (!initialClientBytes.IsEmpty)
                {
                    await backend.GetStream().WriteAsync(initialClientBytes, relayCts.Token).ConfigureAwait(false);
                    Interlocked.Add(ref _clientToBackendBytes, initialClientBytes.Length);
                    backendState.AddClientToBackendBytes(initialClientBytes.Length);
                }

                var clientToBackend = ForwardAsync(
                    client.GetStream(),
                    backend.GetStream(),
                    backend.Client,
                    bytes =>
                    {
                        Interlocked.Add(ref _clientToBackendBytes, bytes);
                        backendState.AddClientToBackendBytes(bytes);
                    },
                    relayCts.Token);
                var backendToClient = ForwardAsync(
                    backend.GetStream(),
                    client.GetStream(),
                    client.Client,
                    bytes =>
                    {
                        Interlocked.Add(ref _backendToClientBytes, bytes);
                        backendState.AddBackendToClientBytes(bytes);
                    },
                    relayCts.Token);

                var first = await Task.WhenAny(clientToBackend, backendToClient).ConfigureAwait(false);
                if (first.IsFaulted || first.IsCanceled)
                {
                    relayCts.Cancel();
                }

                try
                {
                    await Task.WhenAll(clientToBackend, backendToClient).ConfigureAwait(false);
                    backendState.RecordDisconnect(
                        ReferenceEquals(first, clientToBackend) ? "ClientClosed" : "BackendClosed",
                        string.Empty);
                }
                catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
                {
                    backendState.RecordDisconnect("GatewayStopped", string.Empty);
                    throw;
                }
                catch (Exception ex)
                {
                    backendState.RecordDisconnect("RelayError", ex.Message);
                    throw;
                }
            }
        }

        private static async Task ForwardAsync(
            NetworkStream source,
            NetworkStream destination,
            Socket destinationSocket,
            Action<int> countBytes,
            CancellationToken cancellationToken)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(RelayBufferSize);
            try
            {
                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, RelayBufferSize), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    countBytes(read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                try
                {
                    destinationSocket.Shutdown(SocketShutdown.Send);
                }
                catch (SocketException)
                {
                    // The peer may have already disconnected.
                }
                catch (ObjectDisposedException)
                {
                    // The other relay direction may already have closed the socket.
                }
            }
        }

        private async Task RunHealthLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var configuration = GetConfiguration();
                await Task.Delay(TimeSpan.FromSeconds(configuration.Settings.HealthCheckIntervalSec), cancellationToken).ConfigureAwait(false);
                await RefreshBackendHealthAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task RefreshBackendHealthAsync(CancellationToken cancellationToken)
        {
            var configuration = GetConfiguration();
            var checks = configuration.Backends.Values.Select(backend => CheckBackendAsync(backend, configuration.Settings.ConnectTimeoutSec, cancellationToken));
            await Task.WhenAll(checks).ConfigureAwait(false);
        }

        private async Task CheckBackendAsync(BackendState backend, int connectTimeoutSec, CancellationToken cancellationToken)
        {
            if (backend.Definition.RoutingState == TcpGatewayBackendRoutingState.Disabled)
            {
                if (backend.SetHealth(false, string.Empty))
                {
                    await RecordRoutingHistoryAsync(
                            "HealthChanged",
                            backend.Definition.Id,
                            string.Empty,
                            "Backend is disabled.")
                        .ConfigureAwait(false);
                }
                return;
            }

            using var client = new TcpClient { NoDelay = true };
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(connectTimeoutSec));
            try
            {
                await client.ConnectAsync(backend.Definition.Host, backend.Definition.Port, timeoutCts.Token)
                    .ConfigureAwait(false);
                if (backend.SetHealth(true, string.Empty))
                {
                    await RecordRoutingHistoryAsync(
                            "HealthChanged",
                            backend.Definition.Id,
                            string.Empty,
                            "Backend TCP health check is reachable.")
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (backend.SetHealth(false, ex.Message))
                {
                    await RecordRoutingHistoryAsync(
                            "HealthChanged",
                            backend.Definition.Id,
                            string.Empty,
                            $"Backend TCP health check failed: {ex.Message}")
                        .ConfigureAwait(false);
                }
            }
        }

        private async Task RunStateLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await WriteHostStateAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // A transient file lock or I/O failure must not stop the state loop.
                    // TCP forwarding continues independently and the next iteration retries the snapshot.
                }

                // Publish snapshots frequently enough for the launcher UI to observe
                // short-lived traffic bursts. Bandwidth itself is calculated from a
                // sliding window in BackendState.
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task WatchControlSignalsAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (File.Exists(_stopSignalPath))
                {
                    _shutdownCts.Cancel();
                    return;
                }

                await TryReloadConfigurationAsync(cancellationToken).ConfigureAwait(false);

                await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task TryReloadConfigurationAsync(CancellationToken cancellationToken)
        {
            if (!File.Exists(_reloadSignalPath))
            {
                return;
            }

            string signal;
            try
            {
                signal = await File.ReadAllTextAsync(_reloadSignalPath, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return;
            }

            try
            {
                var settings = LoadSettings(_configPath);
                ValidateSettings(settings);
                var previousRoutingStates = GetConfiguration().Backends.Values
                    .Select(static item => item.Definition)
                    .ToDictionary(item => item.Id, item => item.RoutingState, StringComparer.OrdinalIgnoreCase);
                Volatile.Write(ref _configuration, CreateConfiguration(settings));

                foreach (var backend in settings.Backends ?? [])
                {
                    if (!previousRoutingStates.TryGetValue(backend.Id, out var previousState) ||
                        previousState == backend.RoutingState)
                    {
                        continue;
                    }

                    await RecordRoutingHistoryAsync(
                            "RoutingStateChanged",
                            backend.Id,
                            backend.MaintenanceTargetServerId,
                            $"Routing state changed from {previousState} to {backend.RoutingState}.")
                        .ConfigureAwait(false);
                }

                if (!settings.ListenHost.Trim().Equals(_listenHost, StringComparison.OrdinalIgnoreCase) ||
                    settings.ListenPort != _listenPort)
                {
                    _requiresRestart = true;
                    _pendingRestartReason = "Gateway listener changed; restart is required.";
                }
                else
                {
                    _requiresRestart = false;
                    _pendingRestartReason = string.Empty;
                }

                _lastError = string.Empty;
                await WriteHostStateAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _lastError = ex.Message;
                await WriteHostStateAsync().ConfigureAwait(false);
            }

            try
            {
                if (File.Exists(_reloadSignalPath) &&
                    string.Equals(await File.ReadAllTextAsync(_reloadSignalPath, cancellationToken).ConfigureAwait(false), signal, StringComparison.Ordinal))
                {
                    File.Delete(_reloadSignalPath);
                }
            }
            catch (IOException)
            {
                // A newer reload signal may be replacing this one. The next poll will process it.
            }
        }

        private bool IsClientAllowed(IPAddress remoteAddress, GatewayRuntimeConfiguration configuration)
        {
            var normalized = NormalizeAddress(remoteAddress);
            return !configuration.BlockRules.Any(rule => rule.Matches(normalized)) &&
                   (configuration.AllowRules.Count == 0 || configuration.AllowRules.Any(rule => rule.Matches(normalized)));
        }

        private bool TryReserveConnection(IPAddress remoteAddress, GatewayRuntimeConfiguration configuration)
        {
            var key = NormalizeAddress(remoteAddress).ToString();
            lock (_stateGate)
            {
                if (_activeConnections >= configuration.Settings.MaxConnections ||
                    _connectionsByIp.TryGetValue(key, out var current) && current >= configuration.Settings.MaxConnectionsPerIp)
                {
                    return false;
                }

                _connectionsByIp[key] = current + 1;
                _activeConnections++;
                return true;
            }
        }

        private void ReleaseConnection(IPAddress remoteAddress)
        {
            var key = NormalizeAddress(remoteAddress).ToString();
            lock (_stateGate)
            {
                if (_connectionsByIp.TryGetValue(key, out var current))
                {
                    if (current <= 1)
                    {
                        _connectionsByIp.Remove(key);
                    }
                    else
                    {
                        _connectionsByIp[key] = current - 1;
                    }
                }

                _activeConnections = Math.Max(0, _activeConnections - 1);
            }
        }

        private bool TryConsumeTransferTicket(
            string ticket,
            GatewayRuntimeConfiguration configuration,
            out GatewayTransferTicket transfer,
            out BackendState backend)
        {
            transfer = default!;
            backend = default!;
            RemoveExpiredTransferTickets();
            if (!GatewayTransferProtocol.TryValidateTicket(ticket, configuration.Settings.RedirectTicketSecret, out var parsed))
            {
                return false;
            }

            if (!configuration.Backends.ContainsKey(parsed.SourceServerId) ||
                !configuration.Backends.TryGetValue(parsed.TargetServerId, out var target) ||
                !target.CanAcceptRedirect() || !target.IsHealthy)
            {
                return false;
            }

            if (!_consumedTransferTickets.TryAdd(parsed.Nonce, parsed.ExpiresAtUtc))
            {
                return false;
            }

            transfer = parsed;
            backend = target;
            return true;
        }

        private void RemoveExpiredTransferTickets()
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var pair in _consumedTransferTickets)
            {
                if (pair.Value <= now)
                {
                    _consumedTransferTickets.TryRemove(pair.Key, out _);
                }
            }
        }

        private IReadOnlyList<BackendState> GetBackendCandidates(GatewayRuntimeConfiguration configuration)
        {
            lock (_stateGate)
            {
                var online = configuration.Backends.Values
                    .Where(static backend => backend.Definition.RoutingState == TcpGatewayBackendRoutingState.Online)
                    .Where(static backend => backend.Definition.Weight > 0)
                    .ToList();
                var preferred = online
                    .Where(static backend => backend.IsHealthy)
                    .ToList();
                var candidates = preferred.Count == 0 ? online : preferred;
                var weighted = candidates
                    .SelectMany(backend => Enumerable.Repeat(backend, Math.Clamp(backend.Definition.Weight, 0, 100)))
                    .ToList();
                if (weighted.Count == 0)
                {
                    return [];
                }

                var selectedIndex = (int)(Math.Abs(Interlocked.Increment(ref _selectionCursor)) % weighted.Count);
                var selected = weighted[selectedIndex];
                var ordered = new List<BackendState>(
                    [selected, .. weighted.Where(backend => !ReferenceEquals(backend, selected)).Distinct()]);
                ordered.AddRange(online.Where(backend => !ordered.Contains(backend)));
                return ordered;
            }
        }

        private TcpGatewayRuntimeStatus CreateStatus()
        {
            var configuration = GetConfiguration();
            lock (_stateGate)
            {
                var backends = configuration.Backends.Values
                    .OrderBy(static backend => backend.Definition.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static backend => backend.Definition.Host, StringComparer.OrdinalIgnoreCase)
                    .Select(backend => backend.ToRuntimeStatus(DateTimeOffset.UtcNow))
                    .ToList();
                return new TcpGatewayRuntimeStatus
                {
                    IsRunning = _isListening,
                    IsListening = _isListening,
                    RequiresRestart = _requiresRestart,
                    PendingRestartReason = _pendingRestartReason,
                    ProcessId = Environment.ProcessId,
                    StartedAtUtc = _startedAtUtc,
                    ListenAddress = _listenAddress,
                    ActiveConnections = _activeConnections,
                    AcceptedConnections = Interlocked.Read(ref _acceptedConnections),
                    RejectedConnections = Interlocked.Read(ref _rejectedConnections),
                    FailedConnections = Interlocked.Read(ref _failedConnections),
                    ClientToBackendBytes = Interlocked.Read(ref _clientToBackendBytes),
                    BackendToClientBytes = Interlocked.Read(ref _backendToClientBytes),
                    LastError = _lastError,
                    RoutingHistoryLogPath = _routingHistoryPath,
                    Backends = backends
                };
            }
        }

        private async Task WriteHostStateAsync()
        {
            await _stateWriteGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var status = CreateStatus();
                await WriteStateAsync(_statePath, status).ConfigureAwait(false);
            }
            finally
            {
                _stateWriteGate.Release();
            }
        }

        private GatewayRuntimeConfiguration GetConfiguration() => Volatile.Read(ref _configuration);

        private GatewayRuntimeConfiguration CreateConfiguration(TcpGatewaySettings settings)
        {
            return new GatewayRuntimeConfiguration(
                settings,
                (settings.Backends ?? [])
                    .Select(GetOrCreateBackendState)
                    .ToDictionary(backend => backend.Definition.Id, StringComparer.OrdinalIgnoreCase),
                IpRule.ParseMany(settings.AllowListText),
                IpRule.ParseMany(settings.BlockListText));
        }

        private async Task RecordRoutingHistoryAsync(
            string action,
            string sourceServerId,
            string targetServerId,
            string details)
        {
            try
            {
                var entry = new TcpGatewayRoutingHistoryEntry
                {
                    OccurredAtUtc = DateTimeOffset.UtcNow,
                    Action = action,
                    SourceServerId = sourceServerId,
                    TargetServerId = targetServerId,
                    Details = details
                };
                await File.AppendAllTextAsync(
                        _routingHistoryPath,
                        JsonSerializer.Serialize(entry, RoutingHistoryJsonOptions) + Environment.NewLine)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Routing audit logging must never interrupt game traffic.
            }
        }

        private BackendState GetOrCreateBackendState(TcpGatewayBackend backend)
        {
            var id = backend.Id.Trim();
            if (_backendStates.TryGetValue(id, out var existing))
            {
                existing.UpdateDefinition(backend);
                return existing;
            }

            var created = new BackendState(backend);
            _backendStates.Add(id, created);
            return created;
        }

        private async Task AwaitSessionsAsync()
        {
            var sessions = _sessions.Values.ToArray();
            if (sessions.Length == 0)
            {
                return;
            }

            try
            {
                await Task.WhenAll(sessions).ConfigureAwait(false);
            }
            catch
            {
                // Individual connection failures are tracked in runtime status.
            }
        }

        private static async Task IgnoreCancellationAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when the host stops.
            }
        }

        private static async Task<IPAddress> ResolveListenAddressAsync(string host, CancellationToken cancellationToken)
        {
            var normalized = host.Trim();
            if (normalized is "*" or "0.0.0.0")
            {
                return IPAddress.Any;
            }

            if (normalized is "::" or "[::]")
            {
                return IPAddress.IPv6Any;
            }

            if (IPAddress.TryParse(normalized.Trim('[', ']'), out var address))
            {
                return address;
            }

            var addresses = await Dns.GetHostAddressesAsync(normalized, cancellationToken).ConfigureAwait(false);
            return addresses.FirstOrDefault(static address => address.AddressFamily == AddressFamily.InterNetwork)
                   ?? addresses.FirstOrDefault()
                   ?? throw new InvalidOperationException("Gateway listen host could not be resolved.");
        }

        private static IPAddress NormalizeAddress(IPAddress address) =>
            address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        private static string FormatEndpoint(IPAddress address, int port) =>
            address.AddressFamily == AddressFamily.InterNetworkV6
                ? $"[{address}]:{port}"
                : $"{address}:{port}";
    }

    private sealed class GatewayRuntimeConfiguration(
        TcpGatewaySettings settings,
        Dictionary<string, BackendState> backends,
        IReadOnlyList<IpRule> allowRules,
        IReadOnlyList<IpRule> blockRules)
    {
        public TcpGatewaySettings Settings { get; } = settings;

        public Dictionary<string, BackendState> Backends { get; } = backends;

        public IReadOnlyList<IpRule> AllowRules { get; } = allowRules;

        public IReadOnlyList<IpRule> BlockRules { get; } = blockRules;
    }

    private sealed class BackendState
    {
        private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(2);
        private readonly object _gate = new();
        private TcpGatewayBackend _definition;
        private bool _isHealthy;
        private string _lastError = string.Empty;
        private string _lastDisconnectReason = string.Empty;
        private DateTimeOffset? _lastDisconnectAtUtc;
        private readonly Queue<TcpGatewayDisconnectRecord> _recentDisconnects = [];
        private readonly DateTimeOffset _statisticsStartedAtUtc = DateTimeOffset.UtcNow;
        private int _activeConnections;
        private int _peakConnections;
        private long _clientToBackendBytes;
        private long _backendToClientBytes;
        private readonly Queue<BandwidthSample> _clientToBackendRateSamples = [];
        private readonly Queue<BandwidthSample> _backendToClientRateSamples = [];
        private long _clientToBackendRateBytes;
        private long _backendToClientRateBytes;
        private long _establishedConnections;
        private long _failedConnections;
        private long _backendConnectLatencyTicks;
        private long _backendConnectLatencySamples;
        private double _currentClientToBackendMbps;
        private double _currentBackendToClientMbps;
        private double _peakClientToBackendMbps;
        private double _peakBackendToClientMbps;

        public BackendState(TcpGatewayBackend definition)
        {
            _definition = CloneDefinition(definition);
        }

        public TcpGatewayBackend Definition
        {
            get
            {
                lock (_gate)
                {
                    return _definition;
                }
            }
        }

        public void UpdateDefinition(TcpGatewayBackend definition)
        {
            lock (_gate)
            {
                _definition = CloneDefinition(definition);
            }
        }

        public bool IsHealthy
        {
            get
            {
                lock (_gate)
                {
                    return _isHealthy;
                }
            }
        }

        public bool SetHealth(bool isHealthy, string error)
        {
            lock (_gate)
            {
                var changed = _isHealthy != isHealthy;
                _isHealthy = isHealthy;
                _lastError = error;
                return changed;
            }
        }

        public bool CanAcceptRedirect()
        {
            lock (_gate)
            {
                return _definition.RoutingState is TcpGatewayBackendRoutingState.Online or TcpGatewayBackendRoutingState.Draining;
            }
        }

        public void RecordConnectionEstablished(TimeSpan connectDuration)
        {
            lock (_gate)
            {
                _activeConnections++;
                _peakConnections = Math.Max(_peakConnections, _activeConnections);
                _establishedConnections++;
                _backendConnectLatencyTicks += Math.Max(0, connectDuration.Ticks);
                _backendConnectLatencySamples++;
            }
        }

        public void RecordFailedConnection(string error)
        {
            lock (_gate)
            {
                _failedConnections++;
                _isHealthy = false;
                _lastError = error;
            }
        }

        public void AddClientToBackendBytes(int bytes)
        {
            if (bytes <= 0) return;
            Interlocked.Add(ref _clientToBackendBytes, bytes);
            lock (_gate)
            {
                AppendRateSample(_clientToBackendRateSamples, ref _clientToBackendRateBytes, bytes, DateTimeOffset.UtcNow);
            }
        }

        public void AddBackendToClientBytes(int bytes)
        {
            if (bytes <= 0) return;
            Interlocked.Add(ref _backendToClientBytes, bytes);
            lock (_gate)
            {
                AppendRateSample(_backendToClientRateSamples, ref _backendToClientRateBytes, bytes, DateTimeOffset.UtcNow);
            }
        }

        public void RecordDisconnect(string type, string details)
        {
            lock (_gate)
            {
                var occurredAtUtc = DateTimeOffset.UtcNow;
                _lastDisconnectReason = string.IsNullOrWhiteSpace(details) ? type : details;
                _lastDisconnectAtUtc = occurredAtUtc;
                _recentDisconnects.Enqueue(new TcpGatewayDisconnectRecord
                {
                    OccurredAtUtc = occurredAtUtc,
                    Type = type,
                    Details = details
                });
                while (_recentDisconnects.Count > 10)
                {
                    _recentDisconnects.Dequeue();
                }
            }
        }

        public void DecrementConnections()
        {
            lock (_gate)
            {
                _activeConnections = Math.Max(0, _activeConnections - 1);
            }
        }

        public TcpGatewayBackendRuntimeStatus ToRuntimeStatus(DateTimeOffset now)
        {
            lock (_gate)
            {
                var clientToBackendBytes = Interlocked.Read(ref _clientToBackendBytes);
                var backendToClientBytes = Interlocked.Read(ref _backendToClientBytes);
                _currentClientToBackendMbps = GetCurrentRateMbps(
                    _clientToBackendRateSamples,
                    ref _clientToBackendRateBytes,
                    now);
                _currentBackendToClientMbps = GetCurrentRateMbps(
                    _backendToClientRateSamples,
                    ref _backendToClientRateBytes,
                    now);
                _peakClientToBackendMbps = Math.Max(_peakClientToBackendMbps, _currentClientToBackendMbps);
                _peakBackendToClientMbps = Math.Max(_peakBackendToClientMbps, _currentBackendToClientMbps);

                var statisticsDuration = now - _statisticsStartedAtUtc;
                var durationSeconds = Math.Max(statisticsDuration.TotalSeconds, 0.001);
                var attempts = _establishedConnections + _failedConnections;
                return new TcpGatewayBackendRuntimeStatus
                {
                    Id = _definition.Id,
                    Name = _definition.Name,
                    Address = $"{_definition.Host}:{_definition.Port}",
                    Enabled = _definition.RoutingState != TcpGatewayBackendRoutingState.Disabled,
                    RoutingState = ResolveRuntimeRoutingState(),
                    Weight = _definition.Weight,
                    ProfileId = _definition.ProfileId,
                    IsHealthy = _isHealthy,
                    ActiveConnections = _activeConnections,
                    LastError = _lastError,
                    Statistics = new TcpGatewayBackendStatistics
                    {
                        StartedAtUtc = _statisticsStartedAtUtc,
                        ClientToBackendBytes = clientToBackendBytes,
                        BackendToClientBytes = backendToClientBytes,
                        CurrentClientToBackendMbps = _currentClientToBackendMbps,
                        CurrentBackendToClientMbps = _currentBackendToClientMbps,
                        PeakClientToBackendMbps = _peakClientToBackendMbps,
                        PeakBackendToClientMbps = _peakBackendToClientMbps,
                        AverageClientToBackendMbps = ToMbps(clientToBackendBytes, statisticsDuration),
                        AverageBackendToClientMbps = ToMbps(backendToClientBytes, statisticsDuration),
                        CurrentConnections = _activeConnections,
                        PeakConnections = _peakConnections,
                        EstablishedConnections = _establishedConnections,
                        FailedConnections = _failedConnections,
                        ConnectionEstablishRatePerMinute = _establishedConnections / durationSeconds * 60,
                        ConnectionFailureRate = attempts == 0 ? 0 : _failedConnections * 100d / attempts,
                        AverageBackendConnectLatencyMilliseconds = _backendConnectLatencySamples == 0
                            ? 0
                            : TimeSpan.FromTicks(_backendConnectLatencyTicks / _backendConnectLatencySamples).TotalMilliseconds,
                        LastDisconnectReason = _lastDisconnectReason,
                        LastDisconnectAtUtc = _lastDisconnectAtUtc,
                        RecentDisconnects = _recentDisconnects
                            .Reverse()
                            .Select(static record => new TcpGatewayDisconnectRecord
                            {
                                OccurredAtUtc = record.OccurredAtUtc,
                                Type = record.Type,
                                Details = record.Details
                            })
                            .ToList()
                    }
                };
            }
        }

        private TcpGatewayBackendRoutingState ResolveRuntimeRoutingState()
        {
            if (_definition.RoutingState == TcpGatewayBackendRoutingState.Disabled)
            {
                return TcpGatewayBackendRoutingState.Disabled;
            }

            return _isHealthy ? _definition.RoutingState : TcpGatewayBackendRoutingState.Offline;
        }

        private static double ToMbps(long bytes, TimeSpan duration) =>
            duration <= TimeSpan.Zero ? 0 : bytes * 8d / duration.TotalSeconds / 1_000_000d;

        private static void AppendRateSample(
            Queue<BandwidthSample> samples,
            ref long totalBytes,
            int bytes,
            DateTimeOffset timestamp)
        {
            samples.Enqueue(new BandwidthSample(timestamp, bytes));
            totalBytes += bytes;
            TrimRateSamples(samples, ref totalBytes, timestamp);
        }

        private static double GetCurrentRateMbps(
            Queue<BandwidthSample> samples,
            ref long totalBytes,
            DateTimeOffset now)
        {
            TrimRateSamples(samples, ref totalBytes, now);
            return ToMbps(totalBytes, RateWindow);
        }

        private static void TrimRateSamples(
            Queue<BandwidthSample> samples,
            ref long totalBytes,
            DateTimeOffset now)
        {
            var cutoff = now - RateWindow;
            while (samples.Count > 0 && samples.Peek().Timestamp < cutoff)
            {
                totalBytes -= samples.Dequeue().Bytes;
            }

            if (totalBytes < 0)
            {
                totalBytes = 0;
            }
        }

        private readonly record struct BandwidthSample(DateTimeOffset Timestamp, int Bytes);

        private static TcpGatewayBackend CloneDefinition(TcpGatewayBackend source) => new()
        {
            Id = source.Id.Trim(),
            Name = source.Name?.Trim() ?? string.Empty,
            Host = source.Host?.Trim() ?? string.Empty,
            Port = source.Port,
            Weight = source.Weight,
            RoutingState = source.RoutingState == TcpGatewayBackendRoutingState.Offline
                ? TcpGatewayBackendRoutingState.Disabled
                : source.RoutingState,
            MaintenanceTargetServerId = source.MaintenanceTargetServerId?.Trim() ?? string.Empty,
            ProfileId = source.ProfileId?.Trim() ?? string.Empty
        };
    }

    private sealed class IpRule
    {
        private readonly byte[] _networkBytes;

        private IpRule(IPAddress networkAddress, int prefixLength)
        {
            NetworkAddress = networkAddress;
            PrefixLength = prefixLength;
            _networkBytes = networkAddress.GetAddressBytes();
        }

        private IPAddress NetworkAddress { get; }

        private int PrefixLength { get; }

        public static IReadOnlyList<IpRule> ParseMany(string? rawRules)
        {
            var result = new List<IpRule>();
            foreach (var rawRule in (rawRules ?? string.Empty)
                         .Split(['\r', '\n', ';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                result.Add(Parse(rawRule));
            }

            return result;
        }

        public bool Matches(IPAddress candidate)
        {
            candidate = candidate.IsIPv4MappedToIPv6 ? candidate.MapToIPv4() : candidate;
            if (candidate.AddressFamily != NetworkAddress.AddressFamily)
            {
                return false;
            }

            var candidateBytes = candidate.GetAddressBytes();
            var fullBytes = PrefixLength / 8;
            for (var index = 0; index < fullBytes; index++)
            {
                if (_networkBytes[index] != candidateBytes[index])
                {
                    return false;
                }
            }

            var remainingBits = PrefixLength % 8;
            if (remainingBits == 0)
            {
                return true;
            }

            var mask = (byte)(0xFF << (8 - remainingBits));
            return (_networkBytes[fullBytes] & mask) == (candidateBytes[fullBytes] & mask);
        }

        private static IpRule Parse(string rawRule)
        {
            var parts = rawRule.Split('/', StringSplitOptions.TrimEntries);
            if (parts.Length is < 1 or > 2 || !IPAddress.TryParse(parts[0], out var address))
            {
                throw new InvalidOperationException($"Invalid IP rule: {rawRule}");
            }

            address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            var maximumPrefixLength = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            var prefixLength = maximumPrefixLength;
            if (parts.Length == 2 && (!int.TryParse(parts[1], out prefixLength) || prefixLength is < 0 or > 128 || prefixLength > maximumPrefixLength))
            {
                throw new InvalidOperationException($"Invalid IP rule: {rawRule}");
            }

            return new IpRule(address, prefixLength);
        }
    }

    private sealed class GatewayHostOptions
    {
        public required string ConfigPath { get; init; }

        public required string StatePath { get; init; }

        public required string StopSignalPath { get; init; }

        public required string ReloadSignalPath { get; init; }

        public static GatewayHostOptions Parse(IReadOnlyList<string> args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index + 1 < args.Count; index += 2)
            {
                values[args[index]] = args[index + 1];
            }

            return new GatewayHostOptions
            {
                ConfigPath = GetRequiredArgument(values, "--config"),
                StatePath = GetRequiredArgument(values, "--state"),
                StopSignalPath = GetRequiredArgument(values, "--stop-signal"),
                ReloadSignalPath = GetRequiredArgument(values, "--reload-signal")
            };
        }

        private static string GetRequiredArgument(IReadOnlyDictionary<string, string> values, string name)
        {
            return values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                ? Path.GetFullPath(value)
                : throw new InvalidOperationException($"Missing required argument: {name}");
        }
    }
}
