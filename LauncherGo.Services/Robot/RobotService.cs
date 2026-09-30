using System.Globalization;
using System.Text.Json;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

/// <summary>
///     VS2QQ 机器人服务默认实现
/// </summary>
public class RobotService : IRobotService
{
    private const int MaxConsoleLines = 3000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly Vs2QQProcessService _processService;
    private readonly object _consoleGate = new();
    private readonly List<string> _consoleLines = [];
    private RobotRuntimeStatus _status = new();
    private RobotSettings _lastLoadedSettings = new();

    public RobotService(Vs2QQProcessService processService)
    {
        _processService = processService;
        _status = processService.CurrentStatus;
        _processService.OutputReceived += OnProcessOutputReceived;
        _processService.StatusChanged += OnProcessStatusChanged;
    }

    /// <inheritdoc />
    public async Task<RobotSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        WorkspacePathHelper.EnsureWorkspace();
        Directory.CreateDirectory(WorkspacePathHelper.RobotRoot);

        if (!File.Exists(WorkspacePathHelper.RobotSettingsPath))
        {
            var defaults = BuildDefaultSettings();
            await SaveSettingsAsync(defaults, cancellationToken);
            _lastLoadedSettings = defaults;
            return defaults;
        }

        try
        {
            var json = await File.ReadAllTextAsync(WorkspacePathHelper.RobotSettingsPath, cancellationToken);
            var settings = JsonSerializer.Deserialize<RobotSettings>(json) ?? BuildDefaultSettings();
            var normalized = NormalizeSettings(settings);
            _lastLoadedSettings = normalized;
            return normalized;
        }
        catch
        {
            var fallback = BuildDefaultSettings();
            _lastLoadedSettings = fallback;
            return fallback;
        }
    }

    /// <inheritdoc />
    public async Task SaveSettingsAsync(RobotSettings settings, CancellationToken cancellationToken = default)
    {
        WorkspacePathHelper.EnsureWorkspace();
        Directory.CreateDirectory(WorkspacePathHelper.RobotRoot);

        var normalized = NormalizeSettings(settings);
        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        await File.WriteAllTextAsync(WorkspacePathHelper.RobotSettingsPath, json, cancellationToken);
        _lastLoadedSettings = normalized;
    }

    /// <inheritdoc />
    public RobotRuntimeStatus GetCurrentStatus()
    {
        return _status;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetConsoleLines()
    {
        lock (_consoleGate)
        {
            return _consoleLines.ToList();
        }
    }

    /// <inheritdoc />
    public void ClearConsole()
    {
        lock (_consoleGate)
        {
            _consoleLines.Clear();
        }
    }

    /// <inheritdoc />
    public async Task StartAsync(RobotSettings settings, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeSettings(settings);
        await SaveSettingsAsync(normalized, cancellationToken);

        var result = await _processService.StartAsync(normalized, cancellationToken);
        if (!result.IsSuccess)
            throw new InvalidOperationException(result.Message ?? "启动 VS2QQ 失败。", result.Exception);
    }

    /// <inheritdoc />
    public async Task StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
    {
        var result = await _processService.StopAsync(gracefulTimeout, cancellationToken);
        if (!result.IsSuccess)
            throw new InvalidOperationException(result.Message ?? "停止 VS2QQ 失败。", result.Exception);
    }

    private static RobotSettings BuildDefaultSettings()
    {
        return new RobotSettings
        {
            OneBotWsUrl = "ws://127.0.0.1:3001/",
            AccessToken = string.Empty,
            BoundGroupIds = [],
            ProfileBindings = [],
            CustomCommands = [],
            TeleportPoints = [],
            ReconnectIntervalSec = 5,
            DatabasePath = Path.Combine(WorkspacePathHelper.RobotRoot, "vs2qq.db"),
            DefaultEncoding = "utf-8",
            FallbackEncoding = "gbk",
            SuperUsers = [],
            RelayChatMessages = true,
            RelayGroupChatToServer = true,
            RelayPlayerEvents = true,
            RelayServerNotifications = true,
            EnablePlayerBinding = true,
        };
    }

    private static RobotSettings NormalizeSettings(RobotSettings settings)
    {
        var wsUrl = string.IsNullOrWhiteSpace(settings.OneBotWsUrl)
            ? "ws://127.0.0.1:3001/"
            : settings.OneBotWsUrl.Trim();
        if (!wsUrl.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
            !wsUrl.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            wsUrl = "ws://127.0.0.1:3001/";
        }

        var dbPath = string.IsNullOrWhiteSpace(settings.DatabasePath)
            ? Path.Combine(WorkspacePathHelper.RobotRoot, "vs2qq.db")
            : Path.GetFullPath(settings.DatabasePath.Trim());
        var dbDirectory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(dbDirectory))
            Directory.CreateDirectory(dbDirectory);

        var reconnect = settings.ReconnectIntervalSec <= 0 ? 5 : settings.ReconnectIntervalSec;
        var defaultEncoding = string.IsNullOrWhiteSpace(settings.DefaultEncoding)
            ? "utf-8"
            : settings.DefaultEncoding.Trim();
        var fallbackEncoding = string.IsNullOrWhiteSpace(settings.FallbackEncoding)
            ? "gbk"
            : settings.FallbackEncoding.Trim();

        var superUsers = (settings.SuperUsers ?? [])
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        var boundGroupIds = (settings.BoundGroupIds ?? [])
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        var profileBindings = NormalizeProfileBindings(settings.ProfileBindings);
        var customCommands = RobotCustomCommandRules.NormalizeMany(settings.CustomCommands);
        var teleportPoints = RobotTeleportPointRules.NormalizeMany(settings.TeleportPoints);
        foreach (var groupId in profileBindings
                     .Select(static binding => ParsePositiveInt64(binding.GroupId))
                     .Where(static id => id > 0))
        {
            if (!boundGroupIds.Contains(groupId))
            {
                boundGroupIds.Add(groupId);
            }
        }

        foreach (var superUserId in profileBindings
                     .Select(static binding => ParsePositiveInt64(binding.SuperUserId))
                     .Where(static id => id > 0))
        {
            if (!superUsers.Contains(superUserId))
            {
                superUsers.Add(superUserId);
            }
        }
        return new RobotSettings
        {
            OneBotWsUrl = wsUrl,
            AccessToken = settings.AccessToken?.Trim() ?? string.Empty,
            BoundGroupIds = boundGroupIds,
            ProfileBindings = profileBindings,
            CustomCommands = customCommands,
            TeleportPoints = teleportPoints,
            ReconnectIntervalSec = reconnect,
            DatabasePath = dbPath,
            DefaultEncoding = defaultEncoding,
            FallbackEncoding = fallbackEncoding,
            SuperUsers = superUsers,
            RelayChatMessages = settings.RelayChatMessages,
            RelayGroupChatToServer = settings.RelayGroupChatToServer,
            RelayPlayerEvents = settings.RelayPlayerEvents,
            RelayServerNotifications = settings.RelayServerNotifications,
            EnablePlayerBinding = settings.EnablePlayerBinding
        };
    }

    private static List<RobotProfileBinding> NormalizeProfileBindings(IEnumerable<RobotProfileBinding>? bindings)
    {
        var result = new List<RobotProfileBinding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in bindings ?? [])
        {
            var profileId = binding.ProfileId?.Trim() ?? string.Empty;
            var groupId = ParsePositiveInt64(binding.GroupId);
            var superUserId = ParsePositiveInt64(binding.SuperUserId);
            if (string.IsNullOrWhiteSpace(profileId) && groupId <= 0 && superUserId <= 0)
            {
                continue;
            }

            var normalizedGroupId = groupId > 0 ? groupId.ToString(CultureInfo.InvariantCulture) : string.Empty;
            var normalizedSuperUserId = superUserId > 0 ? superUserId.ToString(CultureInfo.InvariantCulture) : string.Empty;
            var key = $"{profileId}|{normalizedGroupId}|{normalizedSuperUserId}";
            if (!seen.Add(key))
            {
                continue;
            }

            result.Add(new RobotProfileBinding
            {
                ProfileId = profileId,
                GroupId = normalizedGroupId,
                SuperUserId = normalizedSuperUserId
            });
        }

        return result;
    }

    private static long ParsePositiveInt64(string? value)
    {
        return long.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : 0;
    }

    private static string NormalizeListenPrefix(string? value)
    {
        var raw = string.IsNullOrWhiteSpace(value)
            ? "http://127.0.0.1:18089/"
            : value.Trim();

        if (IsWildcardListenPrefix(raw))
        {
            return NormalizeWildcardListenPrefix(raw);
        }

        if (!raw.EndsWith('/'))
        {
            raw += "/";
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return "http://127.0.0.1:18089/";
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return "http://127.0.0.1:18089/";
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            return "http://127.0.0.1:18089/";
        }

        var prefix = uri.GetLeftPart(UriPartial.Path);
        if (!prefix.EndsWith('/'))
        {
            prefix += "/";
        }

        return prefix;
    }

    private static bool IsWildcardListenPrefix(string value)
    {
        return value.StartsWith("http://+:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("http://*:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("http://0.0.0.0:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://+:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://*:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://0.0.0.0:", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWildcardListenPrefix(string value)
    {
        string prefix = value.Trim();
        if (!prefix.EndsWith('/'))
        {
            prefix += "/";
        }

        if (prefix.StartsWith("http://0.0.0.0:", StringComparison.OrdinalIgnoreCase))
        {
            return "http://+:" + prefix["http://0.0.0.0:".Length..];
        }

        if (prefix.StartsWith("https://0.0.0.0:", StringComparison.OrdinalIgnoreCase))
        {
            return "https://+:" + prefix["https://0.0.0.0:".Length..];
        }

        return prefix;
    }

    private void OnProcessOutputReceived(object? sender, string line)
    {
        lock (_consoleGate)
        {
            _consoleLines.Add(line);
            while (_consoleLines.Count > MaxConsoleLines)
                _consoleLines.RemoveAt(0);
        }
    }

    private void OnProcessStatusChanged(object? sender, RobotRuntimeStatus status)
    {
        _status = status;
        if (!status.IsRunning && !string.IsNullOrWhiteSpace(_lastLoadedSettings.OneBotWsUrl))
        {
            _status = new RobotRuntimeStatus
            {
                IsRunning = false,
                ProcessId = null,
                StartedAtUtc = null,
                OneBotWsUrl = _lastLoadedSettings.OneBotWsUrl
            };
        }
    }
}
