using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;
using LauncherGo.Services.Paths;

namespace LauncherGo.Services;

/// <summary>
///     服务器进程服务默认实现
/// </summary>
public sealed partial class ServerProcessService : IServerProcessService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SingleServerProcessController> _controllers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, InstanceProfile> _controllerProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ServerRuntimeStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BridgePlayerSnapshot> _bridgePlayers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _bridgePlayerRefreshAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _bridgePlayerRefreshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly IInstanceProfileService? _profileService;
    private readonly ILauncherPreferencesService? _preferencesService;
    private readonly IServerAuthService? _serverAuthService;
    private readonly IServerMapService? _serverMapService;
    private readonly IServerBridgeService? _serverBridgeService;
    private readonly ServerBridgeStateStore? _serverBridgeStateStore;
    private readonly IAutomationLifecycleService? _automationLifecycleService;
    private readonly ILogger<ServerProcessService> _logger;
    private string _activeProfileId = string.Empty;
    private static readonly TimeSpan BridgePlayerRefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BridgePlayerSnapshotLifetime = TimeSpan.FromSeconds(10);

    public ServerProcessService()
        : this(null, null, NullLogger<ServerProcessService>.Instance, null, null)
    {
    }

    public ServerProcessService(
        IInstanceProfileService? profileService,
        IServerAuthService? serverAuthService = null,
        ILogger<ServerProcessService>? logger = null,
        ILauncherPreferencesService? preferencesService = null,
        IServerBridgeService? serverBridgeService = null,
        IAutomationLifecycleService? automationLifecycleService = null,
        ServerBridgeStateStore? serverBridgeStateStore = null,
        IServerMapService? serverMapService = null)
    {
        _profileService = profileService;
        _preferencesService = preferencesService;
        _serverAuthService = serverAuthService;
        _serverMapService = serverMapService;
        _serverBridgeService = serverBridgeService;
        _serverBridgeStateStore = serverBridgeStateStore;
        _automationLifecycleService = automationLifecycleService;
        _logger = logger ?? NullLogger<ServerProcessService>.Instance;
    }

    public event EventHandler<string>? OutputReceived;

    public event EventHandler<ServerOutputLine>? ProfileOutputReceived;

    public event EventHandler<ServerRuntimeStatus>? StatusChanged;

    public ServerRuntimeStatus GetCurrentStatus()
    {
        var statuses = GetCachedStatuses();
        var activeStatus = statuses.FirstOrDefault(status =>
            !string.IsNullOrWhiteSpace(_activeProfileId) &&
            string.Equals(status.ProfileId, _activeProfileId, StringComparison.OrdinalIgnoreCase) &&
            status.IsRunning);
        if (activeStatus is not null)
        {
            return activeStatus;
        }

        return statuses.FirstOrDefault(status => status.IsRunning)
               ?? statuses.FirstOrDefault()
               ?? new ServerRuntimeStatus();
    }

    public ServerRuntimeStatus GetCurrentStatus(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            return new ServerRuntimeStatus();

        lock (_gate)
        {
            return _statuses.TryGetValue(profileId.Trim(), out var status)
                ? status
                : new ServerRuntimeStatus { ProfileId = profileId.Trim() };
        }
    }

    public IReadOnlyList<ServerRuntimeStatus> GetCurrentStatuses()
    {
        return GetCachedStatuses();
    }

    public async Task<ServerRuntimeStatus> RefreshStatusAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            return new ServerRuntimeStatus();

        var normalizedProfileId = profileId.Trim();
        var profile = _profileService?.GetProfileById(normalizedProfileId);
        if (profile is null)
            return GetCurrentStatus(normalizedProfileId);

        var controller = GetOrCreateController(profile);
        var status = await Task.Run(
                () => controller.RefreshStatusAsync(profile, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        return status.IsRunning
            ? await RefreshBridgeStatusAsync(profile, status, cancellationToken).ConfigureAwait(false)
            : status;
    }

    public async Task<IReadOnlyList<ServerRuntimeStatus>> RefreshStatusesAsync(
        CancellationToken cancellationToken = default)
    {
        if (_profileService is null)
            return GetCachedStatuses();

        var profiles = await Task.Run(_profileService.GetProfiles, cancellationToken)
            .ConfigureAwait(false);
        await Task.WhenAll(profiles.Select(profile => RefreshStatusAsync(profile.Id, cancellationToken)))
            .ConfigureAwait(false);
        return GetCachedStatuses();
    }

    public ServerRuntimeStatus GetCachedStatus()
    {
        lock (_gate)
        {
            return _statuses.Values.FirstOrDefault(status => status.IsRunning)
                   ?? _statuses.Values.FirstOrDefault()
                   ?? new ServerRuntimeStatus();
        }
    }

    public IReadOnlyList<ServerRuntimeStatus> GetCachedStatuses()
    {
        lock (_gate)
        {
            return _statuses.Values
                .OrderByDescending(static status => status.IsRunning)
                .ThenBy(status => ResolveStatusProfileName(status.ProfileId))
                .ToList();
        }
    }

    public IReadOnlyList<string> GetOnlinePlayerNames()
    {
        return GetOnlinePlayers()
            .Select(static player => player.PlayerName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> GetOnlinePlayerNames(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return [];
        }

        return GetCurrentStatus(profileId).OnlinePlayerNames;
    }

    private async Task<ServerRuntimeStatus> RefreshBridgeStatusAsync(
        InstanceProfile profile,
        ServerRuntimeStatus localStatus,
        CancellationToken cancellationToken)
    {
        if (_serverBridgeService is null) return localStatus;
        try
        {
            var server = _serverBridgeStateStore?.GetState(profile.Id, "server.status");
            var players = _serverBridgeStateStore?.GetState(profile.Id, "players.list");
            if (server is null)
            {
                var result = await _serverBridgeService.QueryAsync(profile, "server.status", cancellationToken: cancellationToken).ConfigureAwait(false);
                if (result.Success) server = result.Data;
            }
            if (players is null)
            {
                var result = await _serverBridgeService.QueryAsync(profile, "players.list", cancellationToken: cancellationToken).ConfigureAwait(false);
                if (result.Success) players = result.Data;
            }
            if (server is null && players is null) return localStatus;

            var parsedPlayers = players is null ? [] : ParseBridgePlayers(profile, players);
            var receivedAt = DateTimeOffset.UtcNow;
            var merged = MergeBridgePlayers(localStatus, parsedPlayers);
            lock (_gate)
            {
                if (players is not null)
                    _bridgePlayers[profile.Id] = new BridgePlayerSnapshot(receivedAt, parsedPlayers);
                _statuses[profile.Id] = merged;
            }
            StatusChanged?.Invoke(this, merged);
            return merged;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Server bridge status refresh failed. ProfileId={ProfileId}", profile.Id);
            return localStatus;
        }
    }

    public IReadOnlyList<ServerOnlinePlayerInfo> GetOnlinePlayers()
    {
        lock (_gate)
        {
            var runningProfileIds = _statuses.Values
                .Where(static status => status.IsRunning && !string.IsNullOrWhiteSpace(status.ProfileId))
                .Select(static status => status.ProfileId!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return _bridgePlayers
                .Where(pair => runningProfileIds.Contains(pair.Key) &&
                               DateTimeOffset.UtcNow - pair.Value.ReceivedAtUtc <= BridgePlayerSnapshotLifetime)
                .SelectMany(static pair => pair.Value.Players)
                .OrderBy(static player => player.ProfileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static player => player.PlayerName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public async Task StartAsync(InstanceProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile is null)
        {
            throw new ArgumentNullException(nameof(profile));
        }

        _activeProfileId = profile.Id;
        await GetOrCreateController(profile).StartAsync(profile, cancellationToken);
    }

    public async Task StopAsync(string profileId, TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return;
        }

        var normalizedProfileId = profileId.Trim();
        var controller = GetExistingController(normalizedProfileId);
        InstanceProfile? profile;
        lock (_gate)
        {
            _controllerProfiles.TryGetValue(normalizedProfileId, out profile);
        }

        if (controller is null || profile is null)
        {
            return;
        }

        await controller.StopAsync(profile, gracefulTimeout, cancellationToken);
    }

    public async Task StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
    {
        List<(InstanceProfile Profile, SingleServerProcessController Controller)> controllers;
        lock (_gate)
        {
            controllers = _controllers
                .Select(pair => (_controllerProfiles[pair.Key], pair.Value))
                .ToList();
        }

        foreach (var (profile, controller) in controllers)
        {
            if (controller.GetCachedStatus().IsRunning)
            {
                await controller.StopAsync(profile, gracefulTimeout, cancellationToken);
            }
        }
    }

    public Task SendCommandAsync(string profileId, string command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new InvalidOperationException("请先选择运行中的服务器。");
        }

        var controller = GetExistingController(profileId.Trim())
                         ?? throw new InvalidOperationException("所选服务器未运行。");
        _activeProfileId = profileId.Trim();
        return controller.SendCommandAsync(command, cancellationToken);
    }

    public Task SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        var status = GetCurrentStatus();
        if (string.IsNullOrWhiteSpace(status.ProfileId))
        {
            throw new InvalidOperationException("服务器未运行。");
        }

        return SendCommandAsync(status.ProfileId, command, cancellationToken);
    }

    private SingleServerProcessController GetOrCreateController(InstanceProfile profile)
    {
        lock (_gate)
        {
            if (_controllers.TryGetValue(profile.Id, out var existing))
            {
                _controllerProfiles[profile.Id] = profile;
                return existing;
            }

            var controller = new SingleServerProcessController(
                _profileService,
                _serverAuthService,
                _logger,
                _preferencesService,
                _serverBridgeService,
                _automationLifecycleService,
                _serverMapService);
            _controllers[profile.Id] = controller;
            _controllerProfiles[profile.Id] = profile;
            _statuses[profile.Id] = new ServerRuntimeStatus { ProfileId = profile.Id };
            controller.OutputReceived += (_, line) => OnControllerOutput(profile, line);
            controller.StatusChanged += (_, status) => OnControllerStatusChanged(profile, status);
            return controller;
        }
    }

    private SingleServerProcessController? GetExistingController(string profileId)
    {
        lock (_gate)
        {
            if (_controllers.TryGetValue(profileId, out var controller))
            {
                return controller;
            }
        }

        var profile = _profileService?.GetProfileById(profileId);
        return profile is null ? null : GetOrCreateController(profile);
    }

    private void OnControllerOutput(InstanceProfile profile, string line)
    {
        var output = new ServerOutputLine
        {
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            Line = line,
            TimestampUtc = DateTimeOffset.UtcNow
        };
        OutputReceived?.Invoke(this, line);
        ProfileOutputReceived?.Invoke(this, output);
    }

    private void OnControllerStatusChanged(InstanceProfile profile, ServerRuntimeStatus status)
    {
        var profileId = string.IsNullOrWhiteSpace(status.ProfileId) ? profile.Id : status.ProfileId!;
        ServerRuntimeStatus publishedStatus;
        var refreshBridgePlayers = false;
        lock (_gate)
        {
            if (!status.IsRunning)
            {
                _bridgePlayers.Remove(profileId);
                _bridgePlayerRefreshAttempts.Remove(profileId);
                _bridgePlayerRefreshes.Remove(profileId);
            }

            var snapshot = GetCurrentBridgePlayerSnapshotUnsafe(profileId);
            publishedStatus = MergeBridgePlayers(status, snapshot?.Players ?? []);
            _statuses[profileId] = publishedStatus;
            if (status.IsRunning)
            {
                _activeProfileId = profileId;
                var now = DateTimeOffset.UtcNow;
                if (!_bridgePlayerRefreshes.Contains(profileId) &&
                    (!_bridgePlayerRefreshAttempts.TryGetValue(profileId, out var lastAttempt) ||
                     now - lastAttempt >= BridgePlayerRefreshInterval))
                {
                    _bridgePlayerRefreshAttempts[profileId] = now;
                    _bridgePlayerRefreshes.Add(profileId);
                    refreshBridgePlayers = true;
                }
            }
        }

        StatusChanged?.Invoke(this, publishedStatus);
        if (refreshBridgePlayers)
            _ = RefreshBridgePlayersAsync(profile);
    }

    private async Task RefreshBridgePlayersAsync(InstanceProfile profile)
    {
        try
        {
            if (_serverBridgeService is null)
                return;

            var result = await _serverBridgeService.QueryAsync(profile, "players.list").ConfigureAwait(false);
            if (!result.Success || result.Data is null)
            {
                ExpireBridgePlayers(profile.Id);
                return;
            }

            var players = ParseBridgePlayers(profile, result.Data);
            ServerRuntimeStatus? publishedStatus = null;
            lock (_gate)
            {
                if (_statuses.TryGetValue(profile.Id, out var current) && current.IsRunning)
                {
                    _bridgePlayers[profile.Id] = new BridgePlayerSnapshot(DateTimeOffset.UtcNow, players);
                    publishedStatus = MergeBridgePlayers(current, players);
                    _statuses[profile.Id] = publishedStatus;
                }
            }

            if (publishedStatus is not null)
                StatusChanged?.Invoke(this, publishedStatus);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Server bridge player refresh failed. ProfileId={ProfileId}", profile.Id);
            ExpireBridgePlayers(profile.Id);
        }
        finally
        {
            lock (_gate)
            {
                _bridgePlayerRefreshes.Remove(profile.Id);
            }
        }
    }

    private void ExpireBridgePlayers(string profileId)
    {
        ServerRuntimeStatus? publishedStatus = null;
        lock (_gate)
        {
            if (_bridgePlayers.TryGetValue(profileId, out var snapshot) &&
                DateTimeOffset.UtcNow - snapshot.ReceivedAtUtc <= BridgePlayerSnapshotLifetime)
                return;

            _bridgePlayers.Remove(profileId);
            if (_statuses.TryGetValue(profileId, out var current) && current.IsRunning)
            {
                publishedStatus = MergeBridgePlayers(current, []);
                _statuses[profileId] = publishedStatus;
            }
        }

        if (publishedStatus is not null)
            StatusChanged?.Invoke(this, publishedStatus);
    }

    private BridgePlayerSnapshot? GetCurrentBridgePlayerSnapshotUnsafe(string profileId)
    {
        if (!_bridgePlayers.TryGetValue(profileId, out var snapshot))
            return null;
        if (DateTimeOffset.UtcNow - snapshot.ReceivedAtUtc <= BridgePlayerSnapshotLifetime)
            return snapshot;
        _bridgePlayers.Remove(profileId);
        return null;
    }

    internal static ServerRuntimeStatus MergeBridgePlayers(
        ServerRuntimeStatus status,
        IReadOnlyList<ServerOnlinePlayerInfo> players)
    {
        var names = players
            .Select(static player => player.PlayerName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ServerRuntimeStatus
        {
            IsRunning = status.IsRunning,
            ProcessId = status.ProcessId,
            StartedAtUtc = status.StartedAtUtc,
            ProfileId = status.ProfileId,
            CpuPercent = status.CpuPercent,
            MemoryBytes = status.MemoryBytes,
            OnlinePlayers = names.Length,
            OnlinePlayerNames = names,
            PeakOnlinePlayers = names.Length,
            CanSendCommands = status.CanSendCommands,
            ControlMode = status.ControlMode,
            Message = status.Message
        };
    }

    internal static IReadOnlyList<ServerOnlinePlayerInfo> ParseBridgePlayers(InstanceProfile profile, JsonObject data)
    {
        if (data["players"] is not JsonArray array)
            return [];

        return array
            .OfType<JsonObject>()
            .Where(static item => IsBridgePlayerOnline(item))
            .Select(item => new ServerOnlinePlayerInfo
            {
                PlayerUid = ReadString(item, "uid"),
                PlayerName = ReadString(item, "name"),
                ProfileId = profile.Id,
                ProfileName = string.IsNullOrWhiteSpace(profile.Name) ? profile.Id : profile.Name,
                JoinedAtUtc = ReadDateTimeOffset(item, "joinedAtUtc"),
                PingMilliseconds = ReadInt32(item, "pingMs"),
                ConnectionState = ReadString(item, "connectionState"),
                LastActivityUtc = ReadDateTimeOffset(item, "lastActivityUtc"),
                GameMode = ReadString(item, "gameMode"),
                Role = ReadString(item, "role"),
                Dimension = ReadInt32(item, "dimension"),
                X = ReadDouble(item, "x"),
                Y = ReadDouble(item, "y"),
                Z = ReadDouble(item, "z")
            })
            .Where(static player => !string.IsNullOrWhiteSpace(player.PlayerName))
            .GroupBy(static player => string.IsNullOrWhiteSpace(player.PlayerUid) ? player.PlayerName : player.PlayerUid,
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
    }

    private static bool IsBridgePlayerOnline(JsonObject item)
    {
        if (ReadBoolean(item, "online") is false)
            return false;
        return !string.Equals(ReadString(item, "connectionState"), "Offline", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadString(JsonObject value, string propertyName) =>
        value[propertyName] is JsonValue node && node.TryGetValue<string>(out var text) ? text : string.Empty;

    private static bool? ReadBoolean(JsonObject value, string propertyName) =>
        value[propertyName] is JsonValue node && node.TryGetValue<bool>(out var result) ? result : null;

    private static int? ReadInt32(JsonObject value, string propertyName)
    {
        if (value[propertyName] is not JsonValue node) return null;
        if (node.TryGetValue<int>(out var integer)) return integer;
        if (node.TryGetValue<double>(out var number) && double.IsFinite(number)) return (int)Math.Round(number);
        return null;
    }

    private static double? ReadDouble(JsonObject value, string propertyName)
    {
        if (value[propertyName] is not JsonValue node) return null;
        if (node.TryGetValue<double>(out var number) && double.IsFinite(number)) return number;
        if (node.TryGetValue<int>(out var integer)) return integer;
        return null;
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonObject value, string propertyName) =>
        DateTimeOffset.TryParse(ReadString(value, propertyName), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp)
            ? timestamp
            : null;

    private sealed record BridgePlayerSnapshot(
        DateTimeOffset ReceivedAtUtc,
        IReadOnlyList<ServerOnlinePlayerInfo> Players);

    private string ResolveStatusProfileName(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return string.Empty;
        }

        if (_controllerProfiles.TryGetValue(profileId, out var profile))
        {
            return profile.Name;
        }

        return _profileService?.GetProfileById(profileId)?.Name ?? profileId;
    }
}

internal sealed partial class SingleServerProcessController
{
    private const long PlayerCountBootstrapWindowBytes = 4L * 1024 * 1024;
    private const long CommandAckReadWindowBytes = 256L * 1024;
    private const int CommandAckDelayMilliseconds = 3000;
    private readonly SemaphoreSlim _processGate = new(1, 1);
    private readonly IInstanceProfileService? _profileService;
    private readonly ILauncherPreferencesService? _preferencesService;
    private readonly IServerAuthService? _serverAuthService;
    private readonly IServerMapService? _serverMapService;
    private readonly IServerBridgeService? _serverBridgeService;
    private readonly IAutomationLifecycleService? _automationLifecycleService;
    private readonly ILogger<ServerProcessService> _logger;
    private Process? _process;
    private InstanceProfile? _currentProfile;
    private ServerRelayState? _relayState;
    private bool _relayConnected;
    private bool _manualStopRequested;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private bool _canWriteStandardInput;
    private string? _playerCountLogPath;
    private long _playerCountLogPosition;

    private ServerRuntimeStatus _currentStatus = new();
    private readonly object _playerCountGate = new();
    private readonly HashSet<string> _onlinePlayerNames = new(StringComparer.OrdinalIgnoreCase);
    private int _onlinePlayers;
    private int _peakOnlinePlayers;
    private TimeSpan _lastProcessorTime;
    private DateTimeOffset _lastCpuSampleUtc = DateTimeOffset.UtcNow;
    private double _lastCpuPercent;

    public SingleServerProcessController()
        : this(null, null, NullLogger<ServerProcessService>.Instance, null, null)
    {
    }

    public SingleServerProcessController(
        IInstanceProfileService? profileService,
        IServerAuthService? serverAuthService = null,
        ILogger<ServerProcessService>? logger = null,
        ILauncherPreferencesService? preferencesService = null,
        IServerBridgeService? serverBridgeService = null,
        IAutomationLifecycleService? automationLifecycleService = null,
        IServerMapService? serverMapService = null)
    {
        _profileService = profileService;
        _preferencesService = preferencesService;
        _serverAuthService = serverAuthService;
        _serverMapService = serverMapService;
        _serverBridgeService = serverBridgeService;
        _automationLifecycleService = automationLifecycleService;
        _logger = logger ?? NullLogger<ServerProcessService>.Instance;
    }

    /// <inheritdoc />
    public event EventHandler<string>? OutputReceived;

    /// <inheritdoc />
    public event EventHandler<ServerRuntimeStatus>? StatusChanged;

    /// <inheritdoc />
    public ServerRuntimeStatus GetCurrentStatus(InstanceProfile? preferredProfile = null)
    {
        return _currentStatus;
    }

    public async Task<ServerRuntimeStatus> RefreshStatusAsync(
        InstanceProfile preferredProfile,
        CancellationToken cancellationToken = default)
    {
        await _processGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _currentProfile ??= preferredProfile;
            ClearTrackedProcessIfTerminated();

            if (_process is null)
            {
                var attachedToRelay = await TryAttachToExistingWorkspaceServerRelayAsync(
                        preferredProfile,
                        emitOutput: false,
                        cancellationToken)
                    .ConfigureAwait(false);
                var attachedToProcess = !attachedToRelay &&
                                        TryAttachToExistingWorkspaceServerProcess(
                                            preferredProfile,
                                            emitOutput: false);

                if (!attachedToRelay && !attachedToProcess &&
                    await HasLiveServerHostForProfileAsync(preferredProfile, cancellationToken)
                        .ConfigureAwait(false))
                {
                    UpdateStatus(new ServerRuntimeStatus
                    {
                        IsRunning = true,
                        ProcessId = null,
                        StartedAtUtc = _currentStatus.StartedAtUtc,
                        ProfileId = preferredProfile.Id,
                        CanSendCommands = false,
                        ControlMode = "server-host",
                        Message = "ServerHost control channel reconnecting"
                    });
                }
                else if (!attachedToRelay && !attachedToProcess)
                {
                    PublishStoppedStatusIfStale();
                }
            }

            return _currentStatus;
        }
        finally
        {
            _processGate.Release();
        }
    }

    /// <inheritdoc />
    public ServerRuntimeStatus GetCachedStatus()
    {
        return _currentStatus;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetOnlinePlayerNames()
    {
        lock (_playerCountGate)
        {
            return _onlinePlayerNames
                .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <inheritdoc />
    public async Task StartAsync(InstanceProfile profile, CancellationToken cancellationToken = default)
    {
        await _processGate.WaitAsync(cancellationToken);
        try
        {
            ClearTrackedProcessIfTerminated();
            _manualStopRequested = false;

            if (_process is { HasExited: false })
                throw new InvalidOperationException("服务器已在运行中。");

            if (await WaitForRestartingRelayAsync(profile, cancellationToken))
            {
                return;
            }

            WorkspacePathHelper.EnsureWorkspace();
            profile.DirectoryPath = WorkspacePathHelper.ResolveProfileDataPath(profile.DirectoryPath);

            if (await TryAttachToExistingWorkspaceServerRelayAsync(
                    profile,
                    emitOutput: true,
                    cancellationToken).ConfigureAwait(false) ||
                TryAttachToExistingWorkspaceServerProcess(profile, emitOutput: true))
            {
                if (_currentProfile?.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (!HasControlChannel())
                    {
                        if (_relayState is not null && IsRelayHostProcess(_relayState))
                            throw new InvalidOperationException("ServerHost 正在运行，但控制通道尚未恢复，请稍后重试。");

                        throw new InvalidOperationException(
                            $"检测到该档案存在外部或旧版服务端进程（PID={_currentStatus.ProcessId}），无法恢复命令输入。请先停止该进程，再由 LauncherGo 重新启动。");
                    }

                    return;
                }

                DetachCurrentProcessTracking();
            }

            if (await HasLiveServerHostForProfileAsync(profile, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("检测到该档案的 ServerHost 已在运行，控制通道正在恢复，请稍后重试。");

            var installPath = _profileService?.EnsureVersionInstalled(profile.Version)
                              ?? WorkspacePathHelper.GetServerInstallPath(profile.Version);
            var serverExe = LauncherWorkspacePathHelper.ResolveServerExecutablePath(installPath);
            if (!File.Exists(serverExe))
                throw new InvalidOperationException($"未找到服务端程序：{serverExe}");

            if (_automationLifecycleService is not null)
            {
                await _automationLifecycleService
                    .ExecuteAsync(profile, AutomationScriptTrigger.BeforeStart, cancellationToken)
                    .ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Starting Vintage Story server. ProfileId={ProfileId}, ProfileName={ProfileName}, Version={Version}, DataPath={DataPath}.",
                profile.Id,
                profile.Name,
                profile.Version,
                profile.DirectoryPath);

            Directory.CreateDirectory(profile.DirectoryPath);
            var logsPath = WorkspacePathHelper.GetProfileLogsPath(profile.DirectoryPath);
            Directory.CreateDirectory(logsPath);
            await EnsureBuiltInModsBeforeStartAsync(profile, cancellationToken);

            // 缺失配置时自动生成；已有配置仅做必要的非破坏性归一化。
            ServerConfigBootstrapper.EnsureGenerated(installPath, profile);
            RepairLaunchModPaths(profile);
            PrepareSaveFileForStart(profile);
            SqliteConnection.ClearAllPools();

            var relayState = await StartRelayAsync(profile, serverExe, installPath, cancellationToken);
            AttachToRelayState(relayState, profile, emitOutput: false);

            if (_automationLifecycleService is not null)
            {
                await _automationLifecycleService
                    .ExecuteAsync(profile, AutomationScriptTrigger.AfterStart, cancellationToken)
                    .ConfigureAwait(false);
            }

            OutputReceived?.Invoke(this,
                $"[system] 服务器进程已通过后台控制通道启动，PID={relayState.ServerProcessId}，Relay PID={relayState.RelayProcessId}");
            _logger.LogInformation(
                "Vintage Story server process started through relay. ProcessId={ProcessId}, RelayProcessId={RelayProcessId}.",
                relayState.ServerProcessId,
                relayState.RelayProcessId);
        }
        finally
        {
            _processGate.Release();
        }
    }

    private async Task EnsureBuiltInModsBeforeStartAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken)
    {
        if (_serverAuthService is not null && await IsAuthEnabledAsync(profile, cancellationToken))
        {
            await _serverAuthService.EnsureAuthModDeployedAsync(profile, cancellationToken);
            OutputReceived?.Invoke(this, "[system] 已在启动前检查并部署 ServerAuth 模组。");
        }

    }

    private async Task<bool> IsAuthEnabledAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_serverAuthService is null)
                return false;

            var settings = await _serverAuthService.LoadSettingsAsync(profile, cancellationToken);
            return settings.Enabled;
        }
        catch
        {
            return false;
        }
    }

    private static void RepairLaunchModPaths(InstanceProfile profile)
    {
        try
        {
            var configPath = WorkspacePathHelper.GetProfileConfigPath(profile.DirectoryPath);
            if (!File.Exists(configPath))
                return;

            var modsPath = WorkspacePathHelper.GetProfileModsPath(profile.DirectoryPath);
            Directory.CreateDirectory(modsPath);
            var normalizedModsPath = NormalizePath(modsPath);
            ServerConfigFileIO.UpdateTextFile(configPath, currentJson =>
            {
                if (string.IsNullOrWhiteSpace(currentJson) ||
                    JsonNode.Parse(currentJson) is not JsonObject root)
                {
                    return null;
                }

                if (root["ModPaths"] is JsonArray currentPaths &&
                    currentPaths.Count == 2 &&
                    string.Equals(currentPaths[0]?.GetValue<string>(), "Mods", StringComparison.Ordinal) &&
                    string.Equals(currentPaths[1]?.GetValue<string>(), normalizedModsPath, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                root["ModPaths"] = new JsonArray
                {
                    "Mods",
                    normalizedModsPath
                };

                return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            });
        }
        catch
        {
            // 启动修复失败时不阻断服务器启动，避免把路径修复变成新的故障点。
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(
        InstanceProfile? preferredProfile,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default)
    {
        await _processGate.WaitAsync(cancellationToken);
        try
        {
            ClearTrackedProcessIfTerminated();

            var process = _process;
            if (process is not null &&
                !IsProcessTerminated(process) &&
                preferredProfile is not null &&
                !IsTrackedProcessForProfile(process, preferredProfile))
            {
                throw new InvalidOperationException(
                    $"当前控制器跟踪的进程不属于目标档案 {preferredProfile.Name}，已拒绝停止以避免误停其他服务器。");
            }

            if (process is null || IsProcessTerminated(process))
            {
                if (process is null && IsRelayRestartInProgress())
                {
                    await StopRestartingRelayAsync(cancellationToken);
                    PublishStoppedStatusIfStale();
                    return;
                }

                if (!await TryAttachToExistingWorkspaceServerRelayAsync(
                        preferredProfile,
                        emitOutput: true,
                        cancellationToken).ConfigureAwait(false) &&
                    !TryAttachToExistingWorkspaceServerProcess(preferredProfile, emitOutput: true))
                {
                    PublishStoppedStatusIfStale();
                    return;
                }

                process = _process;
                if (process is null || IsProcessTerminated(process))
                {
                    PublishStoppedStatusIfStale();
                    return;
                }
            }

            var targetDataPath = ResolveStopTargetDataPath(process);
            var trackedProcessId = TryGetProcessId(process);
            var gracefulCommandSent = false;

            if (_relayState is not null)
                _manualStopRequested = true;

            if (_automationLifecycleService is not null)
            {
                await _automationLifecycleService
                    .ExecuteAsync(_currentProfile ?? preferredProfile ?? new InstanceProfile { DirectoryPath = targetDataPath }, AutomationScriptTrigger.BeforeStop, cancellationToken)
                    .ConfigureAwait(false);
            }

            try
            {
                await SendCommandInternalAsync("/stop", cancellationToken);
                gracefulCommandSent = true;
            }
            catch (Exception ex)
            {
                // stdin 写入失败时，继续走强制终止兜底，避免出现“点击停止但进程仍存活”。
                OutputReceived?.Invoke(this, $"[system] 发送停服命令失败，将尝试强制终止：{ex.Message}");
                _logger.LogWarning(ex, "Failed to send graceful stop command to server process {ProcessId}.", trackedProcessId);
            }

            if (gracefulCommandSent && !IsProcessTerminated(process))
            {
                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(gracefulTimeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : gracefulTimeout);
                    await process.WaitForExitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Graceful 停止超时，继续进入强制终止。
                }
                catch (ObjectDisposedException)
                {
                    // 进程退出事件可能已释放 Process 对象，按已退出处理。
                }
            }

            if (!IsProcessTerminated(process))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(cancellationToken);
                    OutputReceived?.Invoke(this, "[system] 服务器未在超时时间内退出，已强制终止。");
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"强制终止服务器进程失败：{ex.Message}", ex);
                }
            }

            if (!string.IsNullOrWhiteSpace(targetDataPath))
            {
                await StopOrphanWorkspaceServerProcessesAsync(
                    cancellationToken,
                    excludePid: null,
                    targetDataPath: targetDataPath);

                var remainingMatchedProcessCount = CountWorkspaceServerProcessesByDataPath(targetDataPath);
                if (remainingMatchedProcessCount > 0)
                {
                    throw new InvalidOperationException(
                        $"停服后仍检测到 {remainingMatchedProcessCount} 个同档案服务端进程残留，请稍后重试。");
                }
            }

            ClearTrackedProcessIfTerminated();
            _canWriteStandardInput = false;

            if (_automationLifecycleService is not null && _currentProfile is not null)
            {
                await _automationLifecycleService
                    .ExecuteAsync(_currentProfile, AutomationScriptTrigger.AfterStop, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _processGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        await _processGate.WaitAsync(cancellationToken);
        try
        {
            await SendCommandInternalAsync(command, cancellationToken);
        }
        finally
        {
            _processGate.Release();
        }
    }

    private async Task SendCommandInternalAsync(string command, CancellationToken cancellationToken)
    {
        var normalized = string.IsNullOrWhiteSpace(command) ? string.Empty : command.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("命令不能为空。");
        if (!normalized.StartsWith('/'))
            normalized = "/" + normalized;

        if (GetCommandName(normalized).Equals("/stop", StringComparison.OrdinalIgnoreCase))
            _manualStopRequested = true;

        var serverBridgeService = _serverBridgeService;
        if (serverBridgeService is not null && _currentProfile is { } bridgeProfile)
        {
            var bridgeSettings = await serverBridgeService
                .LoadSettingsAsync(bridgeProfile, cancellationToken)
                .ConfigureAwait(false);
            if (bridgeSettings.Enabled)
            {
                try
                {
                    await serverBridgeService
                        .ExecuteCommandAsync(bridgeProfile, normalized, cancellationToken)
                        .ConfigureAwait(false);
                    OutputReceived?.Invoke(this, $"[cmd:bridge] {normalized}");
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Server bridge failed. ProfileId={ProfileId}, AllowRelayFallback={AllowRelayFallback}.",
                        bridgeProfile.Id,
                        bridgeSettings.AllowRelayFallback);
                    if (!bridgeSettings.AllowRelayFallback)
                    {
                        throw new InvalidOperationException(
                            $"服务器桥接不可用，且已关闭 Relay 回退：{ex.Message}",
                            ex);
                    }
                }
            }

            if (!bridgeSettings.AllowRelayFallback)
            {
                throw new InvalidOperationException(
                    "服务器桥接未启用，且已关闭 Relay 回退。请启用服务器桥接后重试。");
            }
        }

        if ((_process is null || _relayState is null || !_relayConnected) &&
            await TryRecoverRelayForTrackedProcessAsync(cancellationToken))
        {
            await SendCommandInternalAsync(normalized, cancellationToken);
            return;
        }

        if (_relayState is not null)
        {
            if (!_relayConnected && !await TryRecoverRelayForTrackedProcessAsync(cancellationToken))
                throw new InvalidOperationException("ServerHost 控制通道暂时不可达，请稍后重试。");

            if (!IsRelayHostProcess(_relayState))
            {
                _relayState = null;
                _relayConnected = false;
                throw new InvalidOperationException("后台控制通道不可用。");
            }

            if (_relayState.CommandChannelAvailable == false)
            {
                throw new InvalidOperationException(
                    "Relay 仍可连接，但其 stdin 命令写入正在阻塞。请启用 LauncherGo Server Bridge，或等待当前写入恢复后重试。");
            }

            var response = await ServerRelayClient.SendCommandAsync(
                _relayState,
                normalized,
                cancellationToken);
            if (!response.Success)
            {
                _logger.LogWarning(
                    "Failed to send command through relay. ProcessId={ProcessId}, RelayProcessId={RelayProcessId}, Error={Error}.",
                    _currentStatus.ProcessId,
                    _relayState.RelayProcessId,
                    response.Error);

                if (response.State is not null)
                    _relayState = response.State;
                _relayConnected = response.State is not null;
                if (!IsRelayHostProcess(_relayState))
                    _relayState = null;

                throw new InvalidOperationException(response.Error ?? "后台控制通道不可用。");
            }

            if (response.State is not null)
                _relayState = response.State;
            _relayConnected = true;

            OutputReceived?.Invoke(this, $"[cmd] {normalized}");
            ScheduleCommandReceiptCheck(normalized);
            return;
        }

        if (_process is null || _process.HasExited)
            throw new InvalidOperationException("服务器未运行。");

        if (!_canWriteStandardInput)
        {
            if (await TryRecoverRelayForTrackedProcessAsync(cancellationToken))
            {
                await SendCommandInternalAsync(normalized, cancellationToken);
                return;
            }

            throw new InvalidOperationException(
                "当前服务端进程不是由可恢复的 ServerHost 管理，无法安全发送命令。请停止该外部或旧版进程后重新启动。");
        }

        await WriteServerConsoleCommandAsync(_process, normalized, cancellationToken);
        OutputReceived?.Invoke(this, $"[cmd] {normalized}");
        ScheduleCommandReceiptCheck(normalized);
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var process = _process;
                if (process is null || IsProcessTerminated(process))
                    break;

                if (_relayState is not null && !_relayConnected)
                    await TryRecoverRelayForTrackedProcessAsync(cancellationToken);

                RefreshPlayerCountFromLog();

                var startedAt = _currentStatus.StartedAtUtc ?? DateTimeOffset.UtcNow;
                var onlinePlayers = GetOnlinePlayerCount();
                _peakOnlinePlayers = Math.Max(_peakOnlinePlayers, onlinePlayers);
                UpdateStatus(new ServerRuntimeStatus
                {
                    IsRunning = true,
                    ProcessId = TryGetProcessId(process),
                    StartedAtUtc = startedAt,
                    ProfileId = _currentProfile?.Id,
                    CpuPercent = SampleCpuPercent(process),
                    MemoryBytes = TryGetWorkingSet64(process),
                    OnlinePlayers = onlinePlayers,
                    OnlinePlayerNames = GetOnlinePlayerNames(),
                    PeakOnlinePlayers = _peakOnlinePlayers,
                    CanSendCommands = HasControlChannel(),
                    ControlMode = GetControlMode(),
                    Message = HasControlChannel() ? "ServerHost" : "control channel reconnecting"
                });

                await Task.Delay(1000, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(1200, cancellationToken);
            }
        }
    }

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;

        var line = e.Data;
        TryUpdatePlayerCountByLine(line);
        OutputReceived?.Invoke(this, line);
    }

    private void TryUpdatePlayerCountByLine(string line)
    {
        var changed = false;
        lock (_playerCountGate)
        {
            changed = TryApplyPlayerCountLine(line, _onlinePlayerNames, ref _onlinePlayers);
        }

        if (changed)
            PublishPlayerCountOnly();
    }

    private void PublishPlayerCountOnly()
    {
        if (!_currentStatus.IsRunning) return;

        var onlinePlayers = GetOnlinePlayerCount();
        _peakOnlinePlayers = Math.Max(_peakOnlinePlayers, onlinePlayers);
        UpdateStatus(new ServerRuntimeStatus
        {
            IsRunning = true,
            ProcessId = _currentStatus.ProcessId,
            StartedAtUtc = _currentStatus.StartedAtUtc,
            ProfileId = _currentStatus.ProfileId,
            CpuPercent = _currentStatus.CpuPercent,
            MemoryBytes = _currentStatus.MemoryBytes,
            OnlinePlayers = onlinePlayers,
            OnlinePlayerNames = GetOnlinePlayerNames(),
            PeakOnlinePlayers = Math.Max(_currentStatus.PeakOnlinePlayers, onlinePlayers),
            CanSendCommands = HasControlChannel(),
            ControlMode = GetControlMode(),
            Message = _currentStatus.Message
        });
    }

    private void ResetPlayerCountLogMonitor(InstanceProfile? profile, string? dataPath = null)
    {
        var profileDataPath = !string.IsNullOrWhiteSpace(dataPath)
            ? dataPath
            : profile?.DirectoryPath;
        if (string.IsNullOrWhiteSpace(profileDataPath))
        {
            _playerCountLogPath = null;
            _playerCountLogPosition = 0;
            return;
        }

        profileDataPath = WorkspacePathHelper.ResolveProfileDataPath(profileDataPath);
        _playerCountLogPath = WorkspacePathHelper.GetServerMainLogPath(profileDataPath);
        _playerCountLogPosition = 0;

        try
        {
            if (File.Exists(_playerCountLogPath))
            {
                _playerCountLogPosition = new FileInfo(_playerCountLogPath).Length;
                BootstrapPlayerCountFromRecentLog();
            }
        }
        catch
        {
            _playerCountLogPosition = 0;
        }
    }

    private void RefreshPlayerCountFromLog()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_playerCountLogPath) || !File.Exists(_playerCountLogPath))
                return;

            using var stream = new FileStream(_playerCountLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < _playerCountLogPosition)
                _playerCountLogPosition = 0;

            stream.Seek(_playerCountLogPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    TryUpdatePlayerCountByLine(line);
            }

            _playerCountLogPosition = stream.Position;
        }
        catch
        {
            // Runtime status should not flap because a log file is rotating or temporarily locked.
        }
    }

    private void BootstrapPlayerCountFromRecentLog()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_playerCountLogPath) || !File.Exists(_playerCountLogPath))
                return;

            using var stream = new FileStream(_playerCountLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var startPosition = Math.Max(0, stream.Length - PlayerCountBootstrapWindowBytes);
            stream.Seek(startPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            // Skip a potential partial line when reading from the middle of the file.
            if (startPosition > 0)
                reader.ReadLine();

            var detected = false;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var count = 0;

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                detected |= TryApplyPlayerCountLine(line, names, ref count);
            }

            if (detected)
            {
                lock (_playerCountGate)
                {
                    _onlinePlayerNames.Clear();
                    foreach (var name in names)
                        _onlinePlayerNames.Add(name);
                    _onlinePlayers = count;
                }
            }
        }
        catch
        {
            // Keep runtime state stable if bootstrap parsing fails.
        }
    }

    private static bool TryApplyPlayerCountLine(string line, HashSet<string> onlinePlayerNames, ref int onlinePlayers)
    {
        if (ServerReadyPattern().IsMatch(line))
        {
            onlinePlayerNames.Clear();
            var changed = onlinePlayers != 0;
            onlinePlayers = 0;
            return changed;
        }

        if (TryParseAbsoluteOnlineCount(line, out var absoluteCount))
        {
            var changed = onlinePlayers != absoluteCount;
            onlinePlayers = absoluteCount;
            if (absoluteCount == 0 || onlinePlayerNames.Count > absoluteCount)
                onlinePlayerNames.Clear();
            return changed;
        }

        if (TryParseLastPlayerDisconnected(line))
        {
            var changed = onlinePlayers != 0 || onlinePlayerNames.Count > 0;
            onlinePlayers = 0;
            onlinePlayerNames.Clear();
            return changed;
        }

        if (TryParsePlayerJoin(line, out var joinedPlayerName))
            return ApplyPlayerJoin(joinedPlayerName, onlinePlayerNames, ref onlinePlayers);

        if (TryParsePlayerLeave(line, out var leftPlayerName))
            return ApplyPlayerLeave(leftPlayerName, onlinePlayerNames, ref onlinePlayers);

        return false;
    }

    private static bool ApplyPlayerJoin(string playerName, HashSet<string> onlinePlayerNames, ref int onlinePlayers)
    {
        var normalizedPlayerName = NormalizePlayerName(playerName);
        if (!string.IsNullOrWhiteSpace(normalizedPlayerName) && !onlinePlayerNames.Add(normalizedPlayerName))
            return false;

        onlinePlayers = Math.Max(0, onlinePlayers + 1);
        if (onlinePlayerNames.Count > onlinePlayers)
            onlinePlayers = onlinePlayerNames.Count;

        return true;
    }

    private static bool ApplyPlayerLeave(string playerName, HashSet<string> onlinePlayerNames, ref int onlinePlayers)
    {
        var normalizedPlayerName = NormalizePlayerName(playerName);
        if (!string.IsNullOrWhiteSpace(normalizedPlayerName))
        {
            if (onlinePlayerNames.Remove(normalizedPlayerName))
            {
                onlinePlayers = Math.Max(0, onlinePlayers - 1);
                return true;
            }

            if (onlinePlayerNames.Count > 0 && onlinePlayers <= onlinePlayerNames.Count)
                return false;
        }

        if (onlinePlayers <= 0)
            return false;

        onlinePlayers--;
        if (onlinePlayers == 0)
            onlinePlayerNames.Clear();

        return true;
    }

    private static bool TryParsePlayerJoin(string line, out string playerName)
    {
        var match = PlayerJoinPattern().Match(line);
        playerName = match.Success ? NormalizePlayerName(match.Groups["name"].Value) : string.Empty;
        return match.Success;
    }

    private static bool TryParsePlayerLeave(string line, out string playerName)
    {
        var match = PlayerLeavePattern().Match(line);
        playerName = match.Success ? NormalizePlayerName(match.Groups["name"].Value) : string.Empty;
        return match.Success;
    }

    private static bool TryParseLastPlayerDisconnected(string line)
    {
        return LastPlayerDisconnectedPattern().IsMatch(line);
    }

    private static bool TryParseAbsoluteOnlineCount(string line, out int count)
    {
        count = 0;
        var onlineMatch = OnlineCountPattern().Match(line);
        if (!onlineMatch.Success)
            return false;

        var countCapture = onlineMatch.Groups["count"].Captures;
        var rawCount = countCapture.Count > 0 ? countCapture[^1].Value : onlineMatch.Groups["count"].Value;
        if (!int.TryParse(rawCount, out var parsed))
            return false;

        count = Math.Max(0, parsed);
        return true;
    }

    private int GetOnlinePlayerCount()
    {
        lock (_playerCountGate)
        {
            return _onlinePlayers;
        }
    }

    private void ResetOnlinePlayerTracking()
    {
        lock (_playerCountGate)
        {
            _onlinePlayers = 0;
            _onlinePlayerNames.Clear();
        }
    }

    private static string NormalizePlayerName(string? playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
            return string.Empty;

        var normalized = playerName.Trim();
        const string playerPrefix = "Player ";
        return normalized.StartsWith(playerPrefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[playerPrefix.Length..].Trim()
            : normalized;
    }

    private static async Task WriteServerConsoleCommandAsync(
        Process process,
        string command,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(command + Environment.NewLine);
        await process.StandardInput.BaseStream.WriteAsync(bytes.AsMemory(0, bytes.Length), cancellationToken);
        await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
    }

    private void ScheduleCommandReceiptCheck(string normalizedCommand)
    {
        if (IsCommandReceiptCheckSkipped(normalizedCommand))
            return;

        var logPath = _playerCountLogPath;
        if (string.IsNullOrWhiteSpace(logPath))
            return;

        _ = Task.Run(() => VerifyCommandReceiptAsync(normalizedCommand, logPath));
    }

    private async Task VerifyCommandReceiptAsync(string normalizedCommand, string logPath)
    {
        try
        {
            await Task.Delay(CommandAckDelayMilliseconds).ConfigureAwait(false);
            if (!File.Exists(logPath))
                return;

            var expectedCommand = CollapseWhitespace(normalizedCommand);
            if (string.IsNullOrWhiteSpace(expectedCommand))
                return;

            if (RecentLogContainsCommandReceipt(logPath, expectedCommand))
                return;

            OutputReceived?.Invoke(this,
                $"[system] 命令已写入控制通道，但 {CommandAckDelayMilliseconds / 1000} 秒内未看到服务端接收记录：{GetCommandName(expectedCommand)}。如果所有命令都无响应，通常是 Vintage Story 服务端控制台输入线程已失效，需要重启服务端。");
        }
        catch
        {
            // Command receipt diagnostics must not affect command sending.
        }
    }

    private static bool RecentLogContainsCommandReceipt(string logPath, string expectedCommand)
    {
        try
        {
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var startPosition = Math.Max(0, stream.Length - CommandAckReadWindowBytes);
            stream.Seek(startPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = CollapseWhitespace(reader.ReadToEnd());
            return text.Contains(
                $"Handling Console Command {expectedCommand}",
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private static bool IsCommandReceiptCheckSkipped(string normalizedCommand)
    {
        var commandName = GetCommandName(normalizedCommand);
        return commandName.Equals("/stop", StringComparison.OrdinalIgnoreCase)
               || commandName.Equals("/stats", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCommandName(string command)
    {
        var compact = CollapseWhitespace(command);
        if (string.IsNullOrWhiteSpace(compact))
            return string.Empty;

        var spaceIndex = compact.IndexOf(' ', StringComparison.Ordinal);
        return spaceIndex < 0 ? compact : compact[..spaceIndex];
    }

    private static string CollapseWhitespace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return string.Join(
            ' ',
            value.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (sender is Process exitedProcess)
        {
            if (_process is null || !ReferenceEquals(_process, exitedProcess))
                return;
        }

        if (ShouldKeepRelayAfterServerExit())
        {
            TransitionToRelayRestarting();
            return;
        }

        CompleteProcessExitCleanup();
    }

    private void TransitionToRelayRestarting()
    {
        var previousProfileId = _currentProfile?.Id;
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = null;
        _monitorTask = null;
        _canWriteStandardInput = false;
        _playerCountLogPath = null;
        _playerCountLogPosition = 0;
        _peakOnlinePlayers = 0;
        _lastProcessorTime = TimeSpan.Zero;
        _lastCpuPercent = 0;
        _lastCpuSampleUtc = DateTimeOffset.UtcNow;
        ResetOnlinePlayerTracking();

        if (_relayState is not null)
        {
            _relayState.ServerProcessId = null;
            _relayState.IsRestarting = true;
            _relayState.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        var previousStartedAt = _currentStatus.StartedAtUtc;
        UpdateStatus(new ServerRuntimeStatus
        {
            IsRunning = true,
            ProcessId = null,
            StartedAtUtc = previousStartedAt,
            ProfileId = previousProfileId,
            CpuPercent = 0,
            MemoryBytes = 0,
            OnlinePlayers = 0,
            PeakOnlinePlayers = _peakOnlinePlayers,
            CanSendCommands = false,
            ControlMode = "relay",
            Message = "服务端异常退出，Relay 正在自动重启。"
        });

        OutputReceived?.Invoke(this, "[system] 服务端异常退出，后台 Relay 将自动重启服务端。");
        _logger.LogWarning("Vintage Story server process exited unexpectedly; relay recovery is in progress. ProfileId={ProfileId}.", previousProfileId);

        if (_process is not null)
        {
            _process.OutputDataReceived -= OnOutputDataReceived;
            _process.ErrorDataReceived -= OnOutputDataReceived;
            _process.Exited -= OnProcessExited;
            _process.Dispose();
            _process = null;
        }
    }

    private void CompleteProcessExitCleanup()
    {
        var previousProfileId = _currentProfile?.Id;
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = null;
        _monitorTask = null;
        _relayState = null;
        _relayConnected = false;
        _manualStopRequested = false;
        _canWriteStandardInput = false;
        _playerCountLogPath = null;
        _playerCountLogPosition = 0;
        _peakOnlinePlayers = 0;
        _lastProcessorTime = TimeSpan.Zero;
        _lastCpuPercent = 0;
        _lastCpuSampleUtc = DateTimeOffset.UtcNow;
        ResetOnlinePlayerTracking();
        UpdateStatus(new ServerRuntimeStatus
        {
            IsRunning = false,
            ProcessId = null,
            StartedAtUtc = null,
            ProfileId = previousProfileId,
            CpuPercent = 0,
            MemoryBytes = 0,
            OnlinePlayers = 0,
            PeakOnlinePlayers = _peakOnlinePlayers
        });

        OutputReceived?.Invoke(this, "[system] 服务器进程已退出。");
        _logger.LogInformation("Vintage Story server process exited. PreviousProfileId={ProfileId}.", previousProfileId);

        if (_process is not null)
        {
            _process.OutputDataReceived -= OnOutputDataReceived;
            _process.ErrorDataReceived -= OnOutputDataReceived;
            _process.Exited -= OnProcessExited;
            _process.Dispose();
            _process = null;
        }
    }

    private void StartMonitorLoop()
    {
        try
        {
            _monitorCts?.Cancel();
            _monitorCts?.Dispose();
        }
        catch
        {
            // ignore
        }

        _monitorCts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorLoopAsync(_monitorCts.Token), CancellationToken.None);
    }

    private void ClearTrackedProcessIfTerminated()
    {
        var process = _process;
        if (process is null)
        {
            if (IsRelayRestartInProgress())
                return;

            PublishStoppedStatusIfStale();
            return;
        }

        if (!IsProcessTerminated(process))
            return;

        OnProcessExited(process, EventArgs.Empty);
    }

    private void DetachCurrentProcessTracking()
    {
        var process = _process;
        try
        {
            _monitorCts?.Cancel();
            _monitorCts?.Dispose();
        }
        catch
        {
            // ignore
        }

        _monitorCts = null;
        _monitorTask = null;
        _relayState = null;
        _relayConnected = false;
        _manualStopRequested = false;
        _canWriteStandardInput = false;
        _playerCountLogPath = null;
        _playerCountLogPosition = 0;
        _peakOnlinePlayers = 0;
        _lastProcessorTime = TimeSpan.Zero;
        _lastCpuPercent = 0;
        _lastCpuSampleUtc = DateTimeOffset.UtcNow;
        ResetOnlinePlayerTracking();

        if (process is not null)
        {
            try
            {
                process.OutputDataReceived -= OnOutputDataReceived;
                process.ErrorDataReceived -= OnOutputDataReceived;
                process.Exited -= OnProcessExited;
                process.Dispose();
            }
            catch
            {
                // ignore
            }
        }

        _process = null;
        _currentProfile = null;
        _currentStatus = new ServerRuntimeStatus();
    }

    private void PublishStoppedStatusIfStale()
    {
        if (!_currentStatus.IsRunning)
            return;

        var previousProfileId = _currentStatus.ProfileId ?? _currentProfile?.Id;
        _relayState = null;
        _relayConnected = false;
        _manualStopRequested = false;
        _canWriteStandardInput = false;
        _playerCountLogPath = null;
        _playerCountLogPosition = 0;
        _peakOnlinePlayers = 0;
        _lastProcessorTime = TimeSpan.Zero;
        _lastCpuPercent = 0;
        _lastCpuSampleUtc = DateTimeOffset.UtcNow;
        ResetOnlinePlayerTracking();

        UpdateStatus(new ServerRuntimeStatus
        {
            IsRunning = false,
            ProcessId = null,
            StartedAtUtc = null,
            ProfileId = previousProfileId,
            CpuPercent = 0,
            MemoryBytes = 0,
            OnlinePlayers = 0,
            PeakOnlinePlayers = _peakOnlinePlayers
        });
    }

    private void UpdateStatus(ServerRuntimeStatus status)
    {
        _currentStatus = status;
        StatusChanged?.Invoke(this, status);
    }

    private double SampleCpuPercent(Process process)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var currentProcessorTime = process.TotalProcessorTime;
            if (_lastProcessorTime == TimeSpan.Zero)
            {
                _lastProcessorTime = currentProcessorTime;
                _lastCpuSampleUtc = now;
                return _lastCpuPercent;
            }

            var elapsedMs = Math.Max(1, (now - _lastCpuSampleUtc).TotalMilliseconds);
            var processorElapsedMs = Math.Max(0, (currentProcessorTime - _lastProcessorTime).TotalMilliseconds);
            var cpu = Math.Max(0, Math.Min(100, processorElapsedMs / (elapsedMs * Environment.ProcessorCount) * 100.0));
            _lastProcessorTime = currentProcessorTime;
            _lastCpuSampleUtc = now;
            _lastCpuPercent = cpu;
            return cpu;
        }
        catch
        {
            return _lastCpuPercent;
        }
    }

    private bool HasControlChannel()
    {
        if (_canWriteStandardInput)
            return true;

        return _relayConnected &&
               _relayState is { IsRestarting: false } &&
               IsRelayCommandChannelAvailable(_relayState) &&
               _process is not null &&
               !IsProcessTerminated(_process) &&
               IsRelayHostProcess(_relayState);
    }

    private string GetControlMode()
    {
        if (_relayConnected &&
            _relayState is { IsRestarting: false } &&
            IsRelayCommandChannelAvailable(_relayState) &&
            _process is not null &&
            !IsProcessTerminated(_process) &&
            IsRelayHostProcess(_relayState))
            return "server-host";

        return _canWriteStandardInput ? "direct" : string.Empty;
    }

    private static bool IsRelayCommandChannelAvailable(ServerRelayState state) =>
        state.CommandChannelAvailable != false;

    private bool IsAutoRestartAfterCrashEnabled()
    {
        try
        {
            return _preferencesService?.Load().AutoRestartServerAfterCrash == true;
        }
        catch
        {
            return false;
        }
    }

    private bool ShouldKeepRelayAfterServerExit()
    {
        return !_manualStopRequested &&
               _relayState is { RestartOnCrash: true } relayState &&
               IsRelayHostProcess(relayState);
    }

    private bool IsRelayRestartInProgress()
    {
        if (_manualStopRequested ||
            _relayState is not { RestartOnCrash: true } relayState ||
            !IsRelayHostProcess(relayState))
        {
            return false;
        }

        if (relayState.IsRestarting)
            return true;

        return _process is null || IsProcessTerminated(_process);
    }

    private static bool IsProcessTerminated(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsProcessIdRunning(int processId)
    {
        if (processId <= 0)
            return false;

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static int? TryGetProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch
        {
            return null;
        }
    }

    private static Process? TryOpenProcess(int? processId)
    {
        if (!processId.HasValue || processId.Value <= 0)
            return null;

        try
        {
            var process = Process.GetProcessById(processId.Value);
            if (!process.HasExited)
                return process;

            process.Dispose();
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static int TryGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return 0;
        }
    }

    private static long TryGetWorkingSet64(Process process)
    {
        try
        {
            return process.WorkingSet64;
        }
        catch
        {
            return 0;
        }
    }

    private static DateTimeOffset? TryGetStartTimeUtc(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch
        {
            return null;
        }
    }

    private async Task<ServerRelayState> StartRelayAsync(
        InstanceProfile profile,
        string serverExe,
        string installPath,
        CancellationToken cancellationToken)
    {
        var hostPath = ServerHostRuntimeStager.Prepare(ResolveServerHostPath());
        if (string.IsNullOrWhiteSpace(hostPath) || !File.Exists(hostPath))
            throw new InvalidOperationException(
                $"未找到独立服务端控制程序 {ServerRelayProtocol.ServerHostExecutableName}，请重新安装或重新发布 LauncherGo。");

        var pipeName = ServerRelayProtocol.CreatePipeName(profile.Id);
        var statePath = WorkspacePathHelper.GetServerRelayStatePath(profile.Id);
        var instanceId = Guid.NewGuid().ToString("N");
        var controlToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        TryDeleteFile(statePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = hostPath,
            WorkingDirectory = Path.GetDirectoryName(hostPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--pipe-name");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--state-path");
        startInfo.ArgumentList.Add(statePath);
        startInfo.ArgumentList.Add("--server-exe");
        startInfo.ArgumentList.Add(serverExe);
        startInfo.ArgumentList.Add("--working-dir");
        startInfo.ArgumentList.Add(installPath);
        startInfo.ArgumentList.Add("--data-path");
        startInfo.ArgumentList.Add(profile.DirectoryPath);
        startInfo.ArgumentList.Add("--profile-id");
        startInfo.ArgumentList.Add(profile.Id);
        startInfo.ArgumentList.Add("--profile-name");
        startInfo.ArgumentList.Add(profile.Name);
        startInfo.ArgumentList.Add("--version");
        startInfo.ArgumentList.Add(profile.Version);
        startInfo.ArgumentList.Add("--restart-on-crash");
        startInfo.ArgumentList.Add(IsAutoRestartAfterCrashEnabled().ToString());
        startInfo.ArgumentList.Add("--instance-id");
        startInfo.ArgumentList.Add(instanceId);
        startInfo.ArgumentList.Add("--control-token");
        startInfo.ArgumentList.Add(controlToken);

        using var relayProcess = new Process { StartInfo = startInfo };
        if (!relayProcess.Start())
            throw new InvalidOperationException("启动后台控制通道失败。");

        try
        {
            return await WaitForRelayStateAsync(
                relayProcess,
                statePath,
                pipeName,
                instanceId,
                controlToken,
                cancellationToken);
        }
        catch
        {
            TryKillProcessTree(relayProcess);
            TryDeleteFile(statePath);
            throw;
        }
    }

    private async Task<ServerRelayState> WaitForRelayStateAsync(
        Process relayProcess,
        string statePath,
        string pipeName,
        string instanceId,
        string controlToken,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        string? lastError = null;
        var expectedState = new ServerRelayState
        {
            SchemaVersion = ServerRelayProtocol.CurrentSchemaVersion,
            PipeName = pipeName,
            InstanceId = instanceId,
            ControlToken = controlToken
        };

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsProcessTerminated(relayProcess))
                throw new InvalidOperationException($"后台控制通道启动后已退出，退出码：{TryGetExitCode(relayProcess)}。");

            var cachedState = TryReadRelayState(statePath);
            var state = cachedState is not null &&
                        cachedState.SchemaVersion >= ServerRelayProtocol.CurrentSchemaVersion &&
                        cachedState.PipeName.Equals(pipeName, StringComparison.Ordinal) &&
                        cachedState.InstanceId.Equals(instanceId, StringComparison.Ordinal) &&
                        FixedTimeEquals(cachedState.ControlToken, controlToken)
                ? cachedState
                : expectedState;

            var response = await ServerRelayClient.PingAsync(state, cancellationToken);
            if (response.Success &&
                response.State is { } liveState &&
                liveState.RelayProcessId == TryGetProcessId(relayProcess) &&
                IsRelayHostProcess(liveState))
            {
                return liveState;
            }

            lastError = response.Success
                ? "后台控制通道实例身份不匹配。"
                : response.Error;

            await Task.Delay(250, cancellationToken);
        }

        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(lastError)
                ? "等待后台控制通道就绪超时。"
                : $"等待后台控制通道就绪超时：{lastError}");
    }

    private static bool IsRelayHostProcess(ServerRelayState state)
    {
        if (!IsProcessIdRunning(state.RelayProcessId))
            return false;

        using var process = TryOpenProcess(state.RelayProcessId);
        if (process is null)
            return false;

        if (state.SchemaVersion < ServerRelayProtocol.CurrentSchemaVersion)
            return IsLegacyRelayProcess(process, state.ProfileId);

        if (string.IsNullOrWhiteSpace(state.InstanceId) ||
            string.IsNullOrWhiteSpace(state.ControlToken) ||
            string.IsNullOrWhiteSpace(state.HostExecutablePath) ||
            state.RelayStartedAtUtc == default)
        {
            return false;
        }

        var startedAt = TryGetStartTimeUtc(process);
        if (state.RelayStartedAtUtc != default &&
            (!startedAt.HasValue ||
             Math.Abs((startedAt.Value - state.RelayStartedAtUtc).TotalSeconds) > 1))
        {
            return false;
        }

        try
        {
            var actualPath = NormalizePath(process.MainModule?.FileName);
            var expectedPath = NormalizePath(state.HostExecutablePath);
            return !string.IsNullOrWhiteSpace(actualPath) &&
                   !string.IsNullOrWhiteSpace(expectedPath) &&
                   actualPath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase) &&
                   Path.GetFileName(actualPath).Equals(
                       ServerRelayProtocol.ServerHostExecutableName,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLegacyRelayProcess(Process process, string profileId)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(profileId))
            return false;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {process.Id}");
            foreach (ManagementObject item in searcher.Get())
            {
                var commandLine = item["CommandLine"]?.ToString() ?? string.Empty;
                if (!commandLine.Contains(
                        ServerRelayProtocol.LauncherArgument,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var match = ProfileIdArgumentPattern().Match(commandLine);
                return match.Success &&
                       string.Equals(match.Groups["id"].Value, profileId, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // A legacy state is trusted only when its live command line proves identity.
        }

        return false;
    }

    private static bool IsServerProcessForState(Process process, ServerRelayState state)
    {
        var processId = TryGetProcessId(process);
        if (!processId.HasValue || processId != state.ServerProcessId)
            return false;

        if (state.ServerProcessStartedAtUtc is not { } expectedStartedAt)
            return true;

        var actualStartedAt = TryGetStartTimeUtc(process);
        return actualStartedAt.HasValue &&
               Math.Abs((actualStartedAt.Value - expectedStartedAt).TotalSeconds) <= 1;
    }

    private static string ResolveServerHostPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, ServerRelayProtocol.ServerHostExecutableName),
            Path.Combine(AppContext.BaseDirectory, "ServerHost", ServerRelayProtocol.ServerHostExecutableName),
            Path.Combine(
                Path.GetDirectoryName(Environment.ProcessPath) ?? string.Empty,
                ServerRelayProtocol.ServerHostExecutableName)
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left),
            Encoding.UTF8.GetBytes(right));
    }

    private async Task<bool> WaitForRestartingRelayAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken)
    {
        var state = _relayState;
        if (state is null ||
            !string.Equals(state.ProfileId, profile.Id, StringComparison.OrdinalIgnoreCase))
        {
            state = TryReadRelayState(WorkspacePathHelper.GetServerRelayStatePath(profile.Id));
        }

        if (state is not { RestartOnCrash: true } ||
            !IsRelayHostProcess(state))
        {
            return false;
        }

        var response = await ServerRelayClient.PingAsync(state, cancellationToken);
        var liveState = response.State ?? state;
        if (!string.Equals(liveState.ProfileId, profile.Id, StringComparison.OrdinalIgnoreCase))
            return false;

        if (liveState.ServerProcessId is { } serverProcessId)
        {
            var serverProcess = TryOpenProcess(serverProcessId);
            if (serverProcess is not null)
            {
                try
                {
                    if (!IsServerProcessForState(serverProcess, liveState))
                        return false;

                    AttachToRelayProcess(serverProcess, liveState, profile, emitOutput: true);
                    return true;
                }
                catch
                {
                    serverProcess.Dispose();
                    throw;
                }
            }
        }

        // A child process can disappear a few milliseconds before the relay has
        // persisted its restarting flag. Keep waiting while the supervising relay
        // is alive so a second relay is never launched for the same profile.
        liveState.IsRestarting = true;

        _relayState = liveState;
        _relayConnected = response.Success;
        _currentProfile = profile;
        OutputReceived?.Invoke(this, "[system] 检测到服务端正在由 Relay 自动重启，正在等待新进程就绪。");

        var deadline = DateTimeOffset.UtcNow.AddSeconds(25);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsRelayHostProcess(liveState))
                return false;

            response = await ServerRelayClient.PingAsync(liveState, cancellationToken);
            if (response.State is { } updatedState)
                liveState = updatedState;

            if (liveState.ServerProcessId is { } restartedProcessId)
            {
                var restartedProcess = TryOpenProcess(restartedProcessId);
                if (restartedProcess is not null)
                {
                    try
                    {
                        if (!IsServerProcessForState(restartedProcess, liveState))
                        {
                            await Task.Delay(250, cancellationToken);
                            continue;
                        }

                        AttachToRelayProcess(restartedProcess, liveState, profile, emitOutput: true);
                        return true;
                    }
                    catch
                    {
                        restartedProcess.Dispose();
                        throw;
                    }
                }
            }

            await Task.Delay(250, cancellationToken);
        }

        if (IsRelayHostProcess(liveState))
        {
            throw new InvalidOperationException(
                "服务端正在自动重启，尚未在限定时间内恢复。Relay 会继续按重试策略处理，请稍后再查看状态。");
        }

        return false;
    }

    private async Task StopRestartingRelayAsync(CancellationToken cancellationToken)
    {
        var relayState = _relayState;
        if (relayState is null)
            return;

        _manualStopRequested = true;
        var relayProcess = TryOpenProcess(relayState.RelayProcessId);
        if (relayProcess is not null)
        {
            using (relayProcess)
            {
                try
                {
                    relayProcess.Kill(entireProcessTree: true);
                    await relayProcess.WaitForExitAsync(cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException($"停止等待重启的后台 Relay 失败：{ex.Message}", ex);
                }
            }
        }

        TryDeleteFile(WorkspacePathHelper.GetServerRelayStatePath(relayState.ProfileId));
        _relayState = null;
        _relayConnected = false;
        _canWriteStandardInput = false;
        _manualStopRequested = false;
        OutputReceived?.Invoke(this, "[system] 已停止等待重启的后台 Relay。");
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!IsProcessTerminated(process))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private async Task<bool> TryAttachToExistingWorkspaceServerRelayAsync(
        InstanceProfile? preferredProfile,
        bool emitOutput,
        CancellationToken cancellationToken)
    {
        WorkspacePathHelper.EnsureWorkspace();

        var stateFiles = new List<string>();
        try
        {
            stateFiles.AddRange(Directory.GetFiles(
                WorkspacePathHelper.ServerRelayRoot,
                "*.json",
                SearchOption.TopDirectoryOnly));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to enumerate server relay state files.");
        }

        var discoveredState = preferredProfile is null
            ? null
            : await TryDiscoverRelayStateAsync(preferredProfile, cancellationToken).ConfigureAwait(false);
        if (discoveredState is not null)
            TryWriteRelayStateCache(discoveredState);

        var profiles = SafeGetProfiles();
        Process? selectedProcess = null;
        ServerRelayState? selectedState = null;
        InstanceProfile? selectedProfile = null;
        var selectedScore = int.MinValue;
        var selectedStartedAt = DateTimeOffset.MinValue;
        var restartTracked = false;

        var states = stateFiles
            .Select(TryReadRelayState)
            .Where(static state => state is not null)
            .Cast<ServerRelayState>()
            .ToList();
        if (discoveredState is not null)
        {
            states.RemoveAll(state =>
                string.Equals(
                    state.ProfileId,
                    discoveredState.ProfileId,
                    StringComparison.OrdinalIgnoreCase));
            states.Insert(0, discoveredState);
        }

        foreach (var state in states)
        {
            Process? process = null;
            var processSelected = false;

            try
            {
                if (string.IsNullOrWhiteSpace(state.PipeName))
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var response = await ServerRelayClient.PingAsync(state, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.Success)
                {
                    process = TryOpenProcess(state.ServerProcessId);
                    if (process is not null &&
                        IsServerProcessForState(process, state) &&
                        preferredProfile is not null &&
                        string.Equals(state.ProfileId, preferredProfile.Id, StringComparison.OrdinalIgnoreCase) &&
                        IsRelayHostProcess(state))
                    {
                        AttachToDisconnectedRelayProcess(process, state, preferredProfile, emitOutput);
                        processSelected = true;
                        return true;
                    }

                    if (state.RestartOnCrash &&
                        preferredProfile is not null &&
                        string.Equals(state.ProfileId, preferredProfile.Id, StringComparison.OrdinalIgnoreCase) &&
                        IsRelayHostProcess(state) &&
                        (state.IsRestarting || state.ServerProcessId is null))
                    {
                        state.IsRestarting = true;
                        TrackRestartingRelay(state, preferredProfile, emitOutput);
                        restartTracked = true;
                    }
                    else if (!IsRelayHostProcess(state))
                    {
                        TryDeleteFile(WorkspacePathHelper.GetServerRelayStatePath(state.ProfileId));
                    }

                    continue;
                }

                var liveState = response.State ?? state;
                process = TryOpenProcess(liveState.ServerProcessId);
                if (process is null ||
                    IsProcessTerminated(process) ||
                    !IsServerProcessForState(process, liveState))
                {
                    if (preferredProfile is not null &&
                        liveState.RestartOnCrash &&
                        liveState.IsRestarting &&
                        string.Equals(liveState.ProfileId, preferredProfile.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        TrackRestartingRelay(liveState, preferredProfile, emitOutput);
                        restartTracked = true;
                    }

                    continue;
                }

                InstanceProfile? profile = null;
                if (preferredProfile is not null &&
                    string.Equals(liveState.ProfileId, preferredProfile.Id, StringComparison.OrdinalIgnoreCase))
                {
                    profile = preferredProfile;
                }
                else if (!string.IsNullOrWhiteSpace(liveState.ProfileId))
                {
                    profile = profiles.FirstOrDefault(candidate =>
                        candidate.Id.Equals(liveState.ProfileId, StringComparison.OrdinalIgnoreCase));
                }

                profile ??= ResolveProfileForProcess(
                    preferredProfile,
                    profiles,
                    liveState.DataPath,
                    liveState.Version);
                if (preferredProfile is not null &&
                    profile?.Id.Equals(preferredProfile.Id, StringComparison.OrdinalIgnoreCase) != true)
                {
                    continue;
                }

                var score = ScoreProcessMatch(preferredProfile, profile, liveState.DataPath, liveState.Version);
                var startedAt = liveState.StartedAtUtc;

                if (score < selectedScore || score == selectedScore && startedAt <= selectedStartedAt)
                    continue;

                selectedProcess?.Dispose();
                selectedProcess = process;
                selectedState = liveState;
                selectedProfile = profile;
                selectedScore = score;
                selectedStartedAt = startedAt;
                processSelected = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to inspect server relay state for profile {ProfileId}.", state.ProfileId);
            }
            finally
            {
                if (!processSelected)
                    process?.Dispose();
            }
        }

        if (selectedProcess is null || selectedState is null)
            return restartTracked;

        try
        {
            AttachToRelayProcess(selectedProcess, selectedState, selectedProfile, emitOutput);
            return true;
        }
        catch (Exception ex)
        {
            selectedProcess.Dispose();
            _logger.LogDebug(ex, "Failed to attach existing Vintage Story server relay.");
            return false;
        }
    }

    private void TrackRestartingRelay(
        ServerRelayState relayState,
        InstanceProfile profile,
        bool emitOutput)
    {
        var wasAlreadyTracking = _relayState is not null &&
                                 _relayState.RelayProcessId == relayState.RelayProcessId &&
                                 _currentStatus.ProcessId is null &&
                                 _currentStatus.IsRunning;

        _relayState = relayState;
        _relayConnected = true;
        _currentProfile = profile;
        _manualStopRequested = false;
        _canWriteStandardInput = false;

        if (!wasAlreadyTracking)
        {
            ResetOnlinePlayerTracking();
            _onlinePlayers = 0;
            _peakOnlinePlayers = 0;
            _lastProcessorTime = TimeSpan.Zero;
            _lastCpuPercent = 0;
            _lastCpuSampleUtc = DateTimeOffset.UtcNow;
            UpdateStatus(new ServerRuntimeStatus
            {
                IsRunning = true,
                ProcessId = null,
                StartedAtUtc = relayState.StartedAtUtc,
                ProfileId = profile.Id,
                CpuPercent = 0,
                MemoryBytes = 0,
                OnlinePlayers = 0,
                PeakOnlinePlayers = 0,
                CanSendCommands = false,
                ControlMode = "relay",
                Message = "Relay 正在自动重启服务端"
            });
        }

        if (emitOutput && !wasAlreadyTracking)
        {
            OutputReceived?.Invoke(this, "[system] 检测到后台 Relay 正在自动重启服务端，等待新进程就绪。"
            );
        }
    }

    private bool TryAttachToExistingWorkspaceServerProcess(InstanceProfile? preferredProfile, bool emitOutput)
    {
        if (preferredProfile is not null && HasCachedLiveServerHostForProfile(preferredProfile))
        {
            _logger.LogDebug(
                "Skipped unmanaged process attachment while a ServerHost exists. ProfileId={ProfileId}.",
                preferredProfile.Id);
            return false;
        }

        var serversRoot = NormalizePath(WorkspacePathHelper.ServersRoot);
        if (string.IsNullOrWhiteSpace(serversRoot))
            return false;

        Process[] candidates;
        try
        {
            candidates = GetServerProcessCandidates();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to enumerate server processes.");
            return false;
        }

        var profiles = SafeGetProfiles();
        Process? selectedProcess = null;
        InstanceProfile? selectedProfile = null;
        var selectedScore = int.MinValue;
        var selectedStartedAt = DateTimeOffset.MinValue;

        foreach (var candidate in candidates)
        {
            var candidateSelected = false;
            try
            {
                var pid = TryGetProcessId(candidate);
                if (!pid.HasValue || IsProcessTerminated(candidate) || !IsWorkspaceServerProcess(candidate, serversRoot))
                    continue;

                var commandLine = TryReadCommandLine(pid.Value);
                var dataPath = TryExtractDataPath(commandLine);
                var version = TryResolveVersionFromExecutable(candidate, serversRoot);
                var profile = ResolveProfileForProcess(preferredProfile, profiles, dataPath, version);
                if (preferredProfile is not null &&
                    profile?.Id.Equals(preferredProfile.Id, StringComparison.OrdinalIgnoreCase) != true)
                {
                    continue;
                }

                var score = ScoreProcessMatch(preferredProfile, profile, dataPath, version);
                var startedAt = TryGetStartTimeUtc(candidate) ?? DateTimeOffset.MinValue;

                if (score < selectedScore || score == selectedScore && startedAt <= selectedStartedAt)
                    continue;

                selectedProcess?.Dispose();
                selectedProcess = candidate;
                selectedProfile = profile;
                selectedScore = score;
                selectedStartedAt = startedAt;
                candidateSelected = true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to inspect server process.");
            }
            finally
            {
                if (!candidateSelected)
                    candidate.Dispose();
            }
        }

        if (selectedProcess is null)
            return false;

        try
        {
            AttachToExistingProcess(selectedProcess, selectedProfile, emitOutput);
            return true;
        }
        catch (Exception ex)
        {
            selectedProcess.Dispose();
            _logger.LogDebug(ex, "Failed to attach existing Vintage Story server process.");
            return false;
        }
    }

    private IReadOnlyList<InstanceProfile> SafeGetProfiles()
    {
        if (_profileService is null)
            return [];

        try
        {
            return _profileService.GetProfiles();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read profiles while attaching existing server process.");
            return [];
        }
    }

    private void AttachToRelayState(ServerRelayState state, InstanceProfile? profile, bool emitOutput)
    {
        var process = TryOpenProcess(state.ServerProcessId)
                      ?? throw new InvalidOperationException("后台控制通道已启动，但未能打开服务端进程。");

        try
        {
            if (!IsServerProcessForState(process, state))
                throw new InvalidOperationException("后台控制通道返回的服务端进程身份不匹配。");

            AttachToRelayProcess(process, state, profile, emitOutput);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private void AttachToRelayProcess(Process process, ServerRelayState state, InstanceProfile? profile, bool emitOutput)
    {
        process.EnableRaisingEvents = true;
        process.Exited += OnProcessExited;

        _process = process;
        _currentProfile = profile;
        _relayState = state;
        _relayConnected = true;
        _relayState.IsRestarting = false;
        _manualStopRequested = false;
        _canWriteStandardInput = false;
        ResetOnlinePlayerTracking();
        _peakOnlinePlayers = 0;
        _lastProcessorTime = TimeSpan.Zero;
        _lastCpuPercent = 0;
        _lastCpuSampleUtc = DateTimeOffset.UtcNow;
        ResetPlayerCountLogMonitor(profile, state.DataPath);
        StartMonitorLoop();

        var processId = TryGetProcessId(process);
        UpdateStatus(new ServerRuntimeStatus
        {
            IsRunning = true,
            ProcessId = processId,
            StartedAtUtc = state.StartedAtUtc == default
                ? TryGetStartTimeUtc(process) ?? DateTimeOffset.UtcNow
                : state.StartedAtUtc,
            ProfileId = profile?.Id ?? state.ProfileId,
            CpuPercent = 0,
            MemoryBytes = TryGetWorkingSet64(process),
            OnlinePlayers = GetOnlinePlayerCount(),
            OnlinePlayerNames = GetOnlinePlayerNames(),
            PeakOnlinePlayers = _peakOnlinePlayers,
            CanSendCommands = HasControlChannel(),
            ControlMode = GetControlMode(),
            Message = "Relay"
        });

        var profileText = profile is null ? "未识别档案" : $"档案={profile.Name}";
        var message = $"[system] 已连接后台控制通道并接管服务端，PID={processId}，Relay PID={state.RelayProcessId}，{profileText}。";
        if (emitOutput)
            OutputReceived?.Invoke(this, message);

        _logger.LogInformation(
            "Attached Vintage Story server relay. ProcessId={ProcessId}, RelayProcessId={RelayProcessId}, ProfileId={ProfileId}.",
            processId,
            state.RelayProcessId,
            profile?.Id ?? state.ProfileId);
    }

    private void AttachToExistingProcess(Process process, InstanceProfile? profile, bool emitOutput)
    {
        process.EnableRaisingEvents = true;
        process.Exited += OnProcessExited;

        _process = process;
        _currentProfile = profile;
        _relayState = null;
        _relayConnected = false;
        _canWriteStandardInput = false;
        ResetOnlinePlayerTracking();
        _peakOnlinePlayers = 0;
        _lastProcessorTime = TimeSpan.Zero;
        _lastCpuPercent = 0;
        _lastCpuSampleUtc = DateTimeOffset.UtcNow;
        ResetPlayerCountLogMonitor(profile);
        StartMonitorLoop();

        var processId = TryGetProcessId(process);
        UpdateStatus(new ServerRuntimeStatus
        {
            IsRunning = true,
            ProcessId = processId,
            StartedAtUtc = TryGetStartTimeUtc(process) ?? DateTimeOffset.UtcNow,
            ProfileId = profile?.Id,
            CpuPercent = 0,
            MemoryBytes = TryGetWorkingSet64(process),
            OnlinePlayers = GetOnlinePlayerCount(),
            OnlinePlayerNames = GetOnlinePlayerNames(),
            PeakOnlinePlayers = _peakOnlinePlayers,
            CanSendCommands = false,
            ControlMode = string.Empty,
            Message = "attached without control channel"
        });

        var profileText = profile is null ? "未识别档案" : $"档案={profile.Name}";
        var message = $"[system] 检测到已在运行的服务端进程并接管状态，PID={processId}，{profileText}。该进程没有可恢复控制通道，命令发送不可用。";
        if (emitOutput)
            OutputReceived?.Invoke(this, message);

        _logger.LogInformation(
            "Attached existing Vintage Story server process. ProcessId={ProcessId}, ProfileId={ProfileId}.",
            processId,
            profile?.Id);
    }

    private InstanceProfile? ResolveProfileForProcess(
        InstanceProfile? preferredProfile,
        IReadOnlyList<InstanceProfile> profiles,
        string dataPath,
        string version)
    {
        var normalizedDataPath = NormalizePath(dataPath);
        if (!string.IsNullOrWhiteSpace(normalizedDataPath))
        {
            if (preferredProfile is not null &&
                NormalizePath(WorkspacePathHelper.ResolveProfileDataPath(preferredProfile.DirectoryPath))
                    .Equals(normalizedDataPath, StringComparison.OrdinalIgnoreCase))
            {
                return preferredProfile;
            }

            var dataPathMatch = profiles.FirstOrDefault(profile =>
                NormalizePath(WorkspacePathHelper.ResolveProfileDataPath(profile.DirectoryPath))
                    .Equals(normalizedDataPath, StringComparison.OrdinalIgnoreCase));
            if (dataPathMatch is not null)
                return dataPathMatch;
        }

        if (!string.IsNullOrWhiteSpace(version))
        {
            var versionMatches = profiles
                .Where(profile => profile.Version.Equals(version, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (versionMatches.Count == 1)
            {
                var versionMatch = versionMatches[0];
                if (preferredProfile is null ||
                    versionMatch.Id.Equals(preferredProfile.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return preferredProfile ?? versionMatch;
                }
            }
        }

        return null;
    }

    private static int ScoreProcessMatch(
        InstanceProfile? preferredProfile,
        InstanceProfile? matchedProfile,
        string dataPath,
        string version)
    {
        if (preferredProfile is not null && matchedProfile is not null &&
            preferredProfile.Id.Equals(matchedProfile.Id, StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(dataPath) ? 100 : 70;
        }

        if (matchedProfile is not null)
            return !string.IsNullOrWhiteSpace(dataPath) ? 90 : 50;

        return !string.IsNullOrWhiteSpace(version) ? 20 : 10;
    }

    private string TryReadCommandLine(int processId)
    {
        if (!OperatingSystem.IsWindows())
            return string.Empty;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId}");
            foreach (ManagementObject item in searcher.Get())
                return item["CommandLine"]?.ToString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read command line for process {ProcessId}.", processId);
        }

        return string.Empty;
    }

    private static string TryExtractDataPath(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return string.Empty;

        var match = DataPathArgumentPattern().Match(commandLine);
        return match.Success ? match.Groups["path"].Value : string.Empty;
    }

    private static string TryResolveVersionFromExecutable(Process process, string serversRoot)
    {
        try
        {
            var executablePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath))
                return string.Empty;

            var executableDirectory = NormalizePath(Path.GetDirectoryName(Path.GetFullPath(executablePath)));
            if (string.IsNullOrWhiteSpace(executableDirectory) ||
                !executableDirectory.StartsWith(serversRoot, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return Path.GetFileName(executableDirectory) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private bool IsTrackedProcessForProfile(Process process, InstanceProfile profile)
    {
        if (string.Equals(_currentProfile?.Id, profile.Id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(_relayState?.ProfileId, profile.Id, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var targetDataPath = NormalizeProfileDataPath(profile.DirectoryPath);
        if (string.IsNullOrWhiteSpace(targetDataPath))
        {
            return false;
        }

        var relayDataPath = NormalizeProfileDataPath(_relayState?.DataPath);
        if (!string.IsNullOrWhiteSpace(relayDataPath))
        {
            return relayDataPath.Equals(targetDataPath, StringComparison.OrdinalIgnoreCase);
        }

        var processId = TryGetProcessId(process);
        if (!processId.HasValue)
        {
            return false;
        }

        var processDataPath = NormalizeProfileDataPath(
            TryExtractDataPath(TryReadCommandLine(processId.Value)));
        return !string.IsNullOrWhiteSpace(processDataPath) &&
               processDataPath.Equals(targetDataPath, StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveStopTargetDataPath(Process? process)
    {
        var dataPath = _relayState?.DataPath;
        if (string.IsNullOrWhiteSpace(dataPath))
            dataPath = _currentProfile?.DirectoryPath;

        if (string.IsNullOrWhiteSpace(dataPath))
        {
            var processId = process is null ? null : TryGetProcessId(process);
            if (processId.HasValue)
            {
                var commandLine = TryReadCommandLine(processId.Value);
                dataPath = TryExtractDataPath(commandLine);
            }
        }

        return NormalizeProfileDataPath(dataPath);
    }

    private int CountWorkspaceServerProcessesByDataPath(string normalizedTargetDataPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedTargetDataPath))
            return 0;

        var serversRoot = NormalizePath(WorkspacePathHelper.ServersRoot);
        if (string.IsNullOrWhiteSpace(serversRoot))
            return 0;

        Process[] candidates;
        try
        {
            candidates = GetServerProcessCandidates();
        }
        catch
        {
            return 0;
        }

        var matchedCount = 0;
        foreach (var candidate in candidates)
        {
            using (candidate)
            {
                var pid = TryGetProcessId(candidate);
                if (!pid.HasValue)
                    continue;
                if (IsProcessTerminated(candidate))
                    continue;
                if (!IsWorkspaceServerProcess(candidate, serversRoot))
                    continue;
                if (!IsTargetDataPathMatch(pid.Value, normalizedTargetDataPath))
                    continue;

                matchedCount++;
            }
        }

        return matchedCount;
    }

    private async Task<int> StopOrphanWorkspaceServerProcessesAsync(
        CancellationToken cancellationToken,
        int? excludePid = null,
        string? targetDataPath = null)
    {
        var serversRoot = NormalizePath(WorkspacePathHelper.ServersRoot);
        if (string.IsNullOrWhiteSpace(serversRoot))
            return 0;

        var normalizedTargetDataPath = NormalizeProfileDataPath(targetDataPath);

        Process[] candidates;
        try
        {
            candidates = GetServerProcessCandidates();
        }
        catch
        {
            return 0;
        }

        var killedCount = 0;
        foreach (var candidate in candidates)
        {
            using (candidate)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var pid = TryGetProcessId(candidate);
                if (!pid.HasValue)
                    continue;
                if (excludePid.HasValue && excludePid.Value == pid.Value)
                    continue;
                if (IsProcessTerminated(candidate))
                    continue;
                if (!IsWorkspaceServerProcess(candidate, serversRoot))
                    continue;
                if (!IsTargetDataPathMatch(pid.Value, normalizedTargetDataPath))
                    continue;

                try
                {
                    candidate.Kill(entireProcessTree: true);
                    await candidate.WaitForExitAsync(cancellationToken);
                    killedCount++;
                    OutputReceived?.Invoke(this, $"[system] 已清理孤立服务端进程，PID={pid.Value}。");
                }
                catch
                {
                    // 无法访问或终止时忽略，避免阻断主流程。
                }
            }
        }

        return killedCount;
    }

    private bool IsTargetDataPathMatch(int processId, string normalizedTargetDataPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedTargetDataPath))
            return true;

        var commandLine = TryReadCommandLine(processId);
        var processDataPath = NormalizeProfileDataPath(TryExtractDataPath(commandLine));
        if (string.IsNullOrWhiteSpace(processDataPath))
            return false;

        return processDataPath.Equals(normalizedTargetDataPath, StringComparison.OrdinalIgnoreCase);
    }

    private void AttachToDisconnectedRelayProcess(
        Process process,
        ServerRelayState state,
        InstanceProfile profile,
        bool emitOutput)
    {
        AttachToRelayProcess(process, state, profile, emitOutput: false);
        _relayConnected = false;
        UpdateStatus(new ServerRuntimeStatus
        {
            IsRunning = true,
            ProcessId = _currentStatus.ProcessId,
            StartedAtUtc = _currentStatus.StartedAtUtc,
            ProfileId = profile.Id,
            CpuPercent = _currentStatus.CpuPercent,
            MemoryBytes = _currentStatus.MemoryBytes,
            OnlinePlayers = _currentStatus.OnlinePlayers,
            OnlinePlayerNames = _currentStatus.OnlinePlayerNames,
            PeakOnlinePlayers = _currentStatus.PeakOnlinePlayers,
            CanSendCommands = false,
            ControlMode = "server-host",
            Message = "ServerHost control channel reconnecting"
        });

        if (emitOutput)
            OutputReceived?.Invoke(this, "[system] 已识别 ServerHost，控制通道正在重新连接。");
    }

    private async Task<bool> TryRecoverRelayForTrackedProcessAsync(CancellationToken cancellationToken)
    {
        var profile = _currentProfile;
        if (profile is null)
            return false;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var state = await TryDiscoverRelayStateAsync(profile, cancellationToken).ConfigureAwait(false) ??
                        TryReadRelayState(WorkspacePathHelper.GetServerRelayStatePath(profile.Id));
            if (state is not null)
            {
                var response = await ServerRelayClient.PingAsync(state, cancellationToken);
                var liveState = response.State ?? state;
                if (response.Success)
                {
                    if (_process is null)
                    {
                        var discoveredProcess = TryOpenProcess(liveState.ServerProcessId);
                        if (discoveredProcess is null || !IsServerProcessForState(discoveredProcess, liveState))
                        {
                            discoveredProcess?.Dispose();
                            continue;
                        }

                        AttachToRelayProcess(discoveredProcess, liveState, profile, emitOutput: true);
                        TryWriteRelayStateCache(liveState);
                        return true;
                    }

                    if (!IsServerProcessForState(_process, liveState))
                        continue;

                    _relayState = liveState;
                    _relayConnected = true;
                    TryWriteRelayStateCache(liveState);
                    UpdateStatus(new ServerRuntimeStatus
                    {
                        IsRunning = true,
                        ProcessId = _currentStatus.ProcessId,
                        StartedAtUtc = _currentStatus.StartedAtUtc,
                        ProfileId = profile.Id,
                        CpuPercent = _currentStatus.CpuPercent,
                        MemoryBytes = _currentStatus.MemoryBytes,
                        OnlinePlayers = _currentStatus.OnlinePlayers,
                        OnlinePlayerNames = _currentStatus.OnlinePlayerNames,
                        PeakOnlinePlayers = _currentStatus.PeakOnlinePlayers,
                        CanSendCommands = IsRelayCommandChannelAvailable(liveState),
                        ControlMode = "server-host",
                        Message = IsRelayCommandChannelAvailable(liveState)
                            ? "ServerHost"
                            : "ServerHost relay command channel unavailable"
                    });
                    return true;
                }
            }

            if (attempt < 2)
                await Task.Delay(250, cancellationToken);
        }

        return false;
    }

    private static bool HasCachedLiveServerHostForProfile(InstanceProfile profile)
    {
        var state = TryReadRelayState(WorkspacePathHelper.GetServerRelayStatePath(profile.Id));
        return state is not null && IsRelayHostProcess(state) ||
               IsServerHostProcessRunningForProfile(profile.Id);
    }

    private static async Task<bool> HasLiveServerHostForProfileAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken)
    {
        var state = await TryDiscoverRelayStateAsync(profile, cancellationToken).ConfigureAwait(false) ??
                    TryReadRelayState(WorkspacePathHelper.GetServerRelayStatePath(profile.Id));
        return state is not null && IsRelayHostProcess(state) ||
               IsServerHostProcessRunningForProfile(profile.Id);
    }

    private static bool IsServerHostProcessRunningForProfile(string profileId)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(profileId))
            return false;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ExecutablePath, CommandLine FROM Win32_Process WHERE Name = '{ServerRelayProtocol.ServerHostExecutableName}'");
            foreach (ManagementObject item in searcher.Get())
            {
                var executablePath = NormalizePath(item["ExecutablePath"]?.ToString());
                if (!Path.GetFileName(executablePath).Equals(
                        ServerRelayProtocol.ServerHostExecutableName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var commandLine = item["CommandLine"]?.ToString() ?? string.Empty;
                var match = ProfileIdArgumentPattern().Match(commandLine);
                if (match.Success &&
                    string.Equals(match.Groups["id"].Value, profileId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Process discovery is a conservative guard against unmanaged fallback.
        }

        return false;
    }

    private static async Task<ServerRelayState?> TryDiscoverRelayStateAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await ServerRelayClient
                .DiscoverAsync(ServerRelayProtocol.CreatePipeName(profile.Id), cancellationToken)
                .ConfigureAwait(false);
            var state = response.Success ? response.State : null;
            return state is not null &&
                   string.Equals(state.ProfileId, profile.Id, StringComparison.OrdinalIgnoreCase) &&
                   IsRelayHostProcess(state)
                ? state
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static void TryWriteRelayStateCache(ServerRelayState state)
    {
        try
        {
            var path = WorkspacePathHelper.GetServerRelayStatePath(state.ProfileId);
            var tempPath = path + ".launcher.tmp";
            File.WriteAllText(
                tempPath,
                JsonSerializer.Serialize(
                    state,
                    new JsonSerializerOptions(ServerRelayProtocol.JsonOptions) { WriteIndented = true }),
                Encoding.UTF8);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            // The live ServerHost pipe is authoritative; this file is only a discovery cache.
        }
    }

    private static Process[] GetServerProcessCandidates()
    {
        return Process.GetProcessesByName("VintagestoryServer");
    }

    private static bool IsWorkspaceServerProcess(Process process, string serversRoot)
    {
        try
        {
            var executablePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath))
                return false;

            var executableDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
            var normalizedExecutableDirectory = NormalizePath(executableDirectory);
            if (string.IsNullOrWhiteSpace(normalizedExecutableDirectory))
                return false;

            return normalizedExecutableDirectory.StartsWith(serversRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeProfileDataPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            return NormalizePath(WorkspacePathHelper.ResolveProfileDataPath(path));
        }
        catch
        {
            return NormalizePath(path);
        }
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsSameOrChildPath(string candidatePath, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(rootPath))
            return false;

        if (candidatePath.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
            return true;

        return candidatePath.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               candidatePath.StartsWith(rootPath + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static ServerRelayState? TryReadRelayState(string statePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(statePath) || !File.Exists(statePath))
                return null;

            var json = File.ReadAllText(statePath);
            return JsonSerializer.Deserialize<ServerRelayState>(json, ServerRelayProtocol.JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path))
                File.Delete(path);
        }
        catch
        {
            // Stale runtime files are harmless and validated before use.
        }
    }

    private void PrepareSaveFileForStart(InstanceProfile profile)
    {
        var profileSaveRoot = NormalizePath(GetProfileSaveRoot(profile));
        var savePath = profile.ActiveSaveFile;
        if (string.IsNullOrWhiteSpace(savePath))
            savePath = Path.Combine(profileSaveRoot, "default.vcdbs");

        if (string.IsNullOrWhiteSpace(savePath))
            return;

        string fullSavePath;
        try
        {
            fullSavePath = Path.GetFullPath(savePath);
        }
        catch
        {
            return;
        }

        var saveDirectory = NormalizePath(Path.GetDirectoryName(fullSavePath));
        if (!IsSameOrChildPath(saveDirectory, profileSaveRoot))
        {
            var migratedFileName = Path.GetFileName(fullSavePath);
            if (string.IsNullOrWhiteSpace(migratedFileName))
                migratedFileName = "default.vcdbs";

            var migratedSavePath = Path.Combine(profileSaveRoot, migratedFileName);
            TryCopySaveFile(fullSavePath, migratedSavePath);
            fullSavePath = migratedSavePath;
            saveDirectory = NormalizePath(Path.GetDirectoryName(fullSavePath));
        }

        profile.ActiveSaveFile = fullSavePath;
        profile.SaveDirectory = string.IsNullOrWhiteSpace(saveDirectory)
            ? profile.SaveDirectory
            : saveDirectory;

        if (!string.IsNullOrWhiteSpace(saveDirectory))
            Directory.CreateDirectory(saveDirectory);

        ServerConfigBootstrapper.ApplySaveLocation(WorkspacePathHelper.GetProfileConfigPath(profile.DirectoryPath), fullSavePath);
        TryUpdateProfile(profile);

        if (!File.Exists(fullSavePath))
            return;

        var saveFileInfo = new FileInfo(fullSavePath);
        if (saveFileInfo.Length == 0)
        {
            File.Delete(fullSavePath);
            OutputReceived?.Invoke(this, $"[system] 检测到空存档文件，已删除并允许服务器重新生成：{fullSavePath}");
            return;
        }

        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = fullSavePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                Cache = SqliteCacheMode.Private
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();

            var tables = ReadTables(connection);
            var hasChunks = tables.Contains("chunks", StringComparer.OrdinalIgnoreCase);
            var hasChunk = tables.Contains("chunk", StringComparer.OrdinalIgnoreCase);

            // 兼容旧的错误迁移：曾将 chunk 表改名为 chunks，导致 VS 服务器无法写入存档。
            // 这里仅在检测到 chunks 存在、chunk 缺失时回迁；不再执行 chunk -> chunks 的迁移。
            if (hasChunks && !hasChunk)
            {
                var backupPath = $"{fullSavePath}.bak-fix-{DateTime.Now:yyyyMMddHHmmss}";
                File.Copy(fullSavePath, backupPath, overwrite: false);

                using var renameCommand = connection.CreateCommand();
                renameCommand.CommandText = "ALTER TABLE chunks RENAME TO chunk;";
                renameCommand.ExecuteNonQuery();

                OutputReceived?.Invoke(this,
                    $"[system] 已自动修复存档表名 chunks -> chunk，并创建备份：{backupPath}");
            }
        }
        catch (SqliteException ex)
        {
            OutputReceived?.Invoke(this, $"[system] 存档预检查跳过（SQLite）：{ex.Message}");
        }
        catch (Exception ex)
        {
            OutputReceived?.Invoke(this, $"[system] 存档预检查跳过：{ex.Message}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static string GetProfileSaveRoot(InstanceProfile profile)
    {
        var profileDataPath = WorkspacePathHelper.ResolveProfileDataPath(profile.DirectoryPath);
        return Path.Combine(profileDataPath, "Saves");
    }

    private static void TryCopySaveFile(string sourceSaveFile, string targetSaveFile)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourceSaveFile) ||
                string.IsNullOrWhiteSpace(targetSaveFile) ||
                !sourceSaveFile.EndsWith(".vcdbs", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(sourceSaveFile) ||
                File.Exists(targetSaveFile))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetSaveFile)!);
            File.Copy(sourceSaveFile, targetSaveFile, overwrite: false);
        }
        catch
        {
            // 复制旧路径存档失败时仍使用档案目录下的新存档路径启动。
        }
    }

    private void TryUpdateProfile(InstanceProfile profile)
    {
        if (_profileService is null)
        {
            return;
        }

        try
        {
            profile.LastUpdatedUtc = DateTimeOffset.UtcNow;
            _profileService.UpdateProfile(profile);
        }
        catch
        {
            // 启动流程以 serverconfig 为准，索引写回失败不阻断开服。
        }
    }

    private static HashSet<string> ReadTables(SqliteConnection connection)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
                tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private static string TryReadSaveFileLocation(string profileDirectoryPath)
    {
        try
        {
            var configPath = WorkspacePathHelper.GetProfileConfigPath(profileDirectoryPath);
            if (!File.Exists(configPath))
                return string.Empty;

            using var stream = File.OpenRead(configPath);
            using var json = JsonDocument.Parse(stream);

            if (!json.RootElement.TryGetProperty("WorldConfig", out var worldConfigElement) ||
                worldConfigElement.ValueKind != JsonValueKind.Object)
                return string.Empty;

            if (!worldConfigElement.TryGetProperty("SaveFileLocation", out var saveFileElement) ||
                saveFileElement.ValueKind != JsonValueKind.String)
                return string.Empty;

            return saveFileElement.GetString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    [GeneratedRegex(@"\[(?:Server\s+)?Event\]\s+(?<name>.+?)(?:\s+\[[^\]\r\n]+\]:\d+|\s+\S+:\d+)?\s+joins\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlayerJoinPattern();

    [GeneratedRegex(@"\[(?:Server\s+)?Event\]\s+(?<name>.+?)(?:\s+(?:left\.|leaves\.|got removed(?:\.|:|：))|(?:离开了游戏[。\.]?|离开了服务器[。\.]?|已被移除(?:。|\.|:|：)))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlayerLeavePattern();

    [GeneratedRegex(@"(?:\bonline\s+players?\D+(?<count>\d+))|(?:\bplayers?\s+online\D+(?<count>\d+))|(?:(?<count>\d+)\D*player(?:s|\(s\))?\D+online\b)|(?:在线(?:玩家|人数)?\D*(?<count>\d+))|(?:(?<count>\d+)\D*人\D*在线)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OnlineCountPattern();

    [GeneratedRegex(@"\[(?:Server\s+)?Notification\]\s+Last player disconnected\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LastPlayerDisconnectedPattern();

    [GeneratedRegex(@"\[(?:Server\s+)?Event\].*now running on Port\s+\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ServerReadyPattern();

    [GeneratedRegex(@"--dataPath(?:=|\s+)(?:""(?<path>[^""]+)""|(?<path>\S+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DataPathArgumentPattern();

    [GeneratedRegex(@"--profile-id(?:=|\s+)(?:""(?<id>[^""]+)""|(?<id>\S+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProfileIdArgumentPattern();
}

