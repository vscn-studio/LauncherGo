using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Enums;
using LauncherGo.Domains.Models;
using LauncherGo.Services.Paths;

namespace LauncherGo.Services;

public sealed class LauncherPreferencesService : ILauncherPreferencesService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public LauncherPreferences Load()
    {
        Directory.CreateDirectory(LauncherPathHelper.AppRoot);

        if (!File.Exists(LauncherPathHelper.PreferencesFilePath))
        {
            var defaults = Normalize(LauncherPathHelper.BuildDefaults());
            WorkspacePathHelper.SetWorkspaceRoot(defaults.WorkspaceRoot);
            LauncherPathHelper.EnsureBaseDirectories(defaults);
            return defaults;
        }

        try
        {
            var rawJson = File.ReadAllText(LauncherPathHelper.PreferencesFilePath);
            var parsed = JsonSerializer.Deserialize<LauncherPreferences>(rawJson, JsonOptions) ?? LauncherPathHelper.BuildDefaults();
            var normalized = Normalize(parsed);
            WorkspacePathHelper.SetWorkspaceRoot(normalized.WorkspaceRoot);
            LauncherPathHelper.EnsureBaseDirectories(normalized);
            return normalized;
        }
        catch
        {
            var fallback = Normalize(LauncherPathHelper.BuildDefaults());
            WorkspacePathHelper.SetWorkspaceRoot(fallback.WorkspaceRoot);
            LauncherPathHelper.EnsureBaseDirectories(fallback);
            return fallback;
        }
    }

    public void Save(LauncherPreferences preferences)
    {
        Directory.CreateDirectory(LauncherPathHelper.AppRoot);

        var normalized = Normalize(preferences);
        WorkspacePathHelper.SetWorkspaceRoot(normalized.WorkspaceRoot);
        LauncherPathHelper.EnsureBaseDirectories(normalized);

        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        File.WriteAllText(LauncherPathHelper.PreferencesFilePath, json);
    }

    private static LauncherPreferences Normalize(LauncherPreferences source)
    {
        var defaults = LauncherPathHelper.BuildDefaults();
        var workspaceRoot = LauncherPathHelper.GetWorkspaceRootOrDefault(source.WorkspaceRoot);
        var serverDirectory = LauncherPathHelper.GetServerDirectory(workspaceRoot);
        var profileDirectory = LauncherPathHelper.GetProfileDirectory(workspaceRoot);
        var saveDirectory = LauncherPathHelper.GetSaveDirectory(workspaceRoot);
        var qqBotDirectory = LauncherPathHelper.GetQqBotDirectory(workspaceRoot);

        var defaultLaunchProfileIds = NormalizeProfileIds(source.DefaultLaunchProfileIds, source.DefaultLaunchProfileId);
        var autoStartServerProfileIds = NormalizeProfileIds(source.AutoStartServerProfileIds, source.AutoStartServerProfileId);

        return new LauncherPreferences
        {
            IsOnboardingCompleted = source.IsOnboardingCompleted,
            Language = string.IsNullOrWhiteSpace(source.Language)
                ? CultureInfo.CurrentUICulture.Name
                : source.Language.Trim(),
            ThemeMode = Enum.IsDefined(source.ThemeMode) ? source.ThemeMode : ThemeMode.System,
            WorkspaceRoot = workspaceRoot,
            ServerDirectory = serverDirectory,
            ProfileDirectory = profileDirectory,
            SaveDirectory = saveDirectory,
            QqBotDirectory = qqBotDirectory,
            ServerDownloadCatalogUrl = NormalizeHttpUrlOrDefault(source.ServerDownloadCatalogUrl, defaults.ServerDownloadCatalogUrl),
            EnableChunkedDownloads = source.EnableChunkedDownloads,
            DownloadChunkCount = Math.Clamp(source.DownloadChunkCount <= 0 ? defaults.DownloadChunkCount : source.DownloadChunkCount, 1, 32),
            DownloadThreadCount = Math.Clamp(source.DownloadThreadCount <= 0 ? defaults.DownloadThreadCount : source.DownloadThreadCount, 1, 32),
            GitHubProxy = Enum.IsDefined(source.GitHubProxy) ? source.GitHubProxy : GitHubProxyKind.Direct,
            AutoCheckUpdates = source.AutoCheckUpdates,
            DefaultLaunchProfileId = string.Join(';', defaultLaunchProfileIds),
            DefaultLaunchProfileIds = defaultLaunchProfileIds,
            DefaultLaunchSaveFile = NormalizeFilePathOrEmpty(source.DefaultLaunchSaveFile),
            QuickCommands = NormalizeQuickCommands(source.QuickCommands),
            ConsoleLogFilters = ConsoleLogFilterRuleRules.NormalizeMany(source.ConsoleLogFilters),
            ModUpdateChecks = NormalizeModUpdateChecks(source.ModUpdateChecks),
            StartWithWindows = source.StartWithWindows,
            CloseToTrayOnExit = source.CloseToTrayOnExit,
            StartHiddenOnLaunch = source.StartHiddenOnLaunch,
            AutoStartServerOnLaunch = source.AutoStartServerOnLaunch,
            AutoRestartServerAfterCrash = source.AutoRestartServerAfterCrash,
            AutoStartServerProfileId = string.Join(';', autoStartServerProfileIds),
            AutoStartServerProfileIds = autoStartServerProfileIds,
            AutoStartRobotOnLaunch = source.AutoStartRobotOnLaunch,
            AutoStartDiscordOnLaunch = source.AutoStartDiscordOnLaunch,
            AutoStartFrpOnLaunch = source.AutoStartFrpOnLaunch,
            AutoStartThirdPartyFrpcOnLaunch = source.AutoStartThirdPartyFrpcOnLaunch,
            AutoStartEasyTierOnLaunch = source.AutoStartEasyTierOnLaunch,
            AutoStartGatewayOnLaunch = source.AutoStartGatewayOnLaunch,
            Robot = NormalizeRobot(source.Robot, qqBotDirectory),
            Discord = DiscordIntegrationSettingsRules.Normalize(source.Discord),
            Frp = NormalizeFrp(source.Frp),
            EasyTier = NormalizeEasyTier(source.EasyTier),
            TcpGateway = NormalizeTcpGateway(source.TcpGateway),
            SaveCompression = NormalizeSaveCompression(source.SaveCompression, workspaceRoot)
        };
    }

    private static SaveCompressionSettings NormalizeSaveCompression(
        SaveCompressionSettings? source,
        string workspaceRoot)
    {
        source ??= new SaveCompressionSettings();
        var defaultPath = LauncherPathHelper.GetSaveCompressionDirectory(workspaceRoot);
        var compressionPath = LauncherPathHelper.NormalizeDirectoryOrDefault(source.CompressionPath, defaultPath);

        return new SaveCompressionSettings
        {
            Enabled = source.Enabled,
            CompressionLevel = Math.Clamp(
                source.CompressionLevel <= 0 ? 3 : source.CompressionLevel,
                1,
                22),
            CompressionPath = compressionPath,
            UpdateMode = Enum.IsDefined(source.UpdateMode)
                ? source.UpdateMode
                : SaveCompressionUpdateMode.UpdateAndAdd,
            DeleteSourceFiles = source.DeleteSourceFiles
        };
    }

    private static List<string> NormalizeQuickCommands(IEnumerable<string>? source)
    {
        var commands = new List<string>();
        foreach (var command in source ?? [])
        {
            var normalized = command?.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            commands.Add(normalized);
        }

        return commands;
    }

    private static RobotIntegrationSettings NormalizeRobot(RobotIntegrationSettings? source, string defaultQqBotDirectory)
    {
        source ??= new RobotIntegrationSettings();

        var dbPath = string.IsNullOrWhiteSpace(source.DatabasePath)
            ? Path.Combine(defaultQqBotDirectory, "vs2qq.db")
            : NormalizeFilePathOrEmpty(source.DatabasePath);
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            dbPath = Path.Combine(defaultQqBotDirectory, "vs2qq.db");
        }

        var dbDirectory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(dbDirectory))
        {
            Directory.CreateDirectory(dbDirectory);
        }

        var wsUrl = string.IsNullOrWhiteSpace(source.OneBotWsUrl)
            ? "ws://127.0.0.1:3001/"
            : source.OneBotWsUrl.Trim();
        if (!wsUrl.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
            !wsUrl.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            wsUrl = "ws://127.0.0.1:3001/";
        }

        var bindings = NormalizeRobotBindings(source.ProfileBindings);
        var boundGroupsText = NormalizeQqIdText(source.BoundGroupIdsText);
        if (string.IsNullOrWhiteSpace(boundGroupsText))
        {
            boundGroupsText = NormalizeQqIdText(bindings.Select(static binding => binding.GroupId));
        }

        var superUsersText = NormalizeQqIdText(source.SuperUsersText);
        if (string.IsNullOrWhiteSpace(superUsersText))
        {
            superUsersText = NormalizeQqIdText(bindings.Select(static binding => binding.SuperUserId));
        }

        return new RobotIntegrationSettings
        {
            OneBotWsUrl = wsUrl,
            AccessToken = source.AccessToken?.Trim() ?? string.Empty,
            BoundGroupIdsText = boundGroupsText,
            ReconnectIntervalSec = Math.Clamp(source.ReconnectIntervalSec, 1, 120),
            DatabasePath = dbPath,
            DefaultEncoding = string.IsNullOrWhiteSpace(source.DefaultEncoding) ? "utf-8" : source.DefaultEncoding.Trim(),
            FallbackEncoding = string.IsNullOrWhiteSpace(source.FallbackEncoding) ? "gbk" : source.FallbackEncoding.Trim(),
            SuperUsersText = superUsersText,
            RelayChatMessages = source.RelayChatMessages,
            RelayGroupChatToServer = source.RelayGroupChatToServer,
            RelayPlayerEvents = source.RelayPlayerEvents,
            RelayServerNotifications = source.RelayServerNotifications,
            EnablePlayerBinding = source.EnablePlayerBinding,
            ProfileBindings = bindings,
            CustomCommands = RobotCustomCommandRules.NormalizeMany(source.CustomCommands),
            TeleportPoints = RobotTeleportPointRules.NormalizeMany(source.TeleportPoints)
        };
    }

    private static List<RobotProfileBinding> NormalizeRobotBindings(IEnumerable<RobotProfileBinding>? bindings)
    {
        var result = new List<RobotProfileBinding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in bindings ?? [])
        {
            var profileId = binding.ProfileId?.Trim() ?? string.Empty;
            var groupId = NormalizeQqIdValue(binding.GroupId);
            var superUserId = NormalizeQqIdValue(binding.SuperUserId);
            if (string.IsNullOrWhiteSpace(profileId) &&
                string.IsNullOrWhiteSpace(groupId) &&
                string.IsNullOrWhiteSpace(superUserId))
            {
                continue;
            }

            var key = $"{profileId}|{groupId}|{superUserId}";
            if (!seen.Add(key))
            {
                continue;
            }

            result.Add(new RobotProfileBinding
            {
                ProfileId = profileId,
                GroupId = groupId,
                SuperUserId = superUserId
            });
        }

        return result;
    }

    private static List<string> NormalizeProfileIds(IEnumerable<string>? values, string? legacyValue = null)
    {
        var result = new List<string>();
        foreach (var id in values ?? [])
        {
            AddProfileId(result, id);
        }

        if (!string.IsNullOrWhiteSpace(legacyValue))
        {
            foreach (var id in legacyValue.Split([';', ',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                AddProfileId(result, id);
            }
        }

        return result;
    }

    private static void AddProfileId(List<string> result, string? id)
    {
        var normalized = id?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized) ||
            result.Any(existing => existing.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        result.Add(normalized);
    }

    private static string NormalizeQqIdText(string? value)
    {
        return NormalizeQqIdText((value ?? string.Empty)
            .Split(['\r', '\n', '\t', ',', ';', '，', '；', ' '], StringSplitOptions.RemoveEmptyEntries));
    }

    private static string NormalizeQqIdText(IEnumerable<string?> values)
    {
        var lines = values
            .Select(NormalizeQqIdValue)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return lines.Count == 0 ? string.Empty : string.Join(Environment.NewLine, lines);
    }

    private static string NormalizeQqIdValue(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return long.TryParse(normalized, out var qq) && qq > 0
            ? qq.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private static FrpIntegrationSettings NormalizeFrp(FrpIntegrationSettings? source)
    {
        source ??= new FrpIntegrationSettings();
        var mode = Enum.IsDefined(source.ThirdPartyFrpcLaunchMode)
            ? source.ThirdPartyFrpcLaunchMode
            : ThirdPartyFrpcLaunchMode.ConfigFile;
        var thirdPartyDefault = mode == ThirdPartyFrpcLaunchMode.CommandOnly
            ? FrpIntegrationSettings.DefaultThirdPartyFrpcCommand
            : FrpIntegrationSettings.DefaultThirdPartyFrpcConfigCommand;

        return new FrpIntegrationSettings
        {
            FrpCommand = string.IsNullOrWhiteSpace(source.FrpCommand)
                ? FrpIntegrationSettings.DefaultFrpCommand
                : source.FrpCommand.Trim(),
            ThirdPartyFrpcLaunchMode = mode,
            ThirdPartyFrpcCommand = string.IsNullOrWhiteSpace(source.ThirdPartyFrpcCommand)
                ? thirdPartyDefault
                : source.ThirdPartyFrpcCommand.Trim()
        };
    }

    private static EasyTierIntegrationSettings NormalizeEasyTier(EasyTierIntegrationSettings? source)
    {
        source ??= new EasyTierIntegrationSettings();
        var roomPrefix = source.RoomPrefix?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(roomPrefix) || roomPrefix.Contains('-'))
        {
            roomPrefix = EasyTierIntegrationSettings.DefaultRoomPrefix;
        }

        var networkName = source.NetworkName?.Trim() ?? string.Empty;
        var networkSecret = source.NetworkSecret?.Trim() ?? string.Empty;

        var hostName = source.Hostname?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(hostName))
        {
            hostName = "LauncherGo-vs-server";
        }

        var ipv4Address = source.Ipv4Address?.Trim() ?? string.Empty;
        if (!System.Net.IPAddress.TryParse(ipv4Address, out var parsedAddress) ||
            parsedAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            ipv4Address = EasyTierIntegrationSettings.DefaultIpv4Address;
        }

        return new EasyTierIntegrationSettings
        {
            RoomPrefix = roomPrefix,
            PeerNodesText = NormalizeEasyTierPeerNodes(source.PeerNodesText),
            NetworkName = networkName,
            NetworkSecret = networkSecret,
            GamePort = Math.Clamp(
                source.GamePort <= 0 ? EasyTierIntegrationSettings.DefaultGamePort : source.GamePort,
                1,
                ushort.MaxValue),
            EnableUdp = source.EnableUdp,
            LatencyFirst = source.LatencyFirst,
            Compression = source.Compression,
            EnableKcpProxy = source.EnableKcpProxy,
            Hostname = hostName,
            Ipv4Address = ipv4Address
        };
    }

    private static string NormalizeEasyTierPeerNodes(string? value)
    {
        var entries = (value ?? string.Empty)
            .Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return entries.Count == 0 ? string.Empty : string.Join(Environment.NewLine, entries);
    }

    private static TcpGatewaySettings NormalizeTcpGateway(TcpGatewaySettings? source)
    {
        source ??= new TcpGatewaySettings();
        var backends = new List<TcpGatewayBackend>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var backend in source.Backends ?? [])
        {
            var host = backend.Host?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(host))
            {
                continue;
            }

            var port = Math.Clamp(backend.Port, 1, ushort.MaxValue);
            var key = $"{host}:{port}";
            if (!seen.Add(key))
            {
                continue;
            }

            var id = string.IsNullOrWhiteSpace(backend.Id) ? Guid.NewGuid().ToString("N") : backend.Id.Trim();
            while (!seenIds.Add(id))
            {
                id = Guid.NewGuid().ToString("N");
            }

            backends.Add(new TcpGatewayBackend
            {
                Id = id,
                Name = backend.Name?.Trim() ?? string.Empty,
                Host = host,
                Port = port,
                Weight = Math.Clamp(backend.Weight, 1, 100),
                RoutingState = backend.RoutingState == TcpGatewayBackendRoutingState.Offline
                    ? TcpGatewayBackendRoutingState.Disabled
                    : backend.RoutingState,
                MaintenanceTargetServerId = backend.MaintenanceTargetServerId?.Trim() ?? string.Empty,
                ProfileId = backend.ProfileId?.Trim() ?? string.Empty
            });
        }

        return new TcpGatewaySettings
        {
            ListenHost = string.IsNullOrWhiteSpace(source.ListenHost)
                ? TcpGatewaySettings.DefaultListenHost
                : source.ListenHost.Trim(),
            ListenPort = Math.Clamp(
                source.ListenPort <= 0 ? TcpGatewaySettings.DefaultListenPort : source.ListenPort,
                1,
                ushort.MaxValue),
            MaxConnections = Math.Clamp(source.MaxConnections <= 0 ? 200 : source.MaxConnections, 1, 100_000),
            MaxConnectionsPerIp = Math.Clamp(source.MaxConnectionsPerIp <= 0 ? 4 : source.MaxConnectionsPerIp, 1, 10_000),
            ConnectTimeoutSec = Math.Clamp(source.ConnectTimeoutSec <= 0 ? 8 : source.ConnectTimeoutSec, 1, 120),
            HealthCheckIntervalSec = Math.Clamp(source.HealthCheckIntervalSec <= 0 ? 5 : source.HealthCheckIntervalSec, 1, 300),
            RedirectTicketSecret = string.IsNullOrWhiteSpace(source.RedirectTicketSecret)
                ? Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
                : source.RedirectTicketSecret.Trim(),
            AllowListText = NormalizeIpRules(source.AllowListText),
            BlockListText = NormalizeIpRules(source.BlockListText),
            Backends = backends
        };
    }

    private static Dictionary<string, ModUpdateCheckCacheEntry> NormalizeModUpdateChecks(
        IDictionary<string, ModUpdateCheckCacheEntry>? source)
    {
        var result = new Dictionary<string, ModUpdateCheckCacheEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source ?? new Dictionary<string, ModUpdateCheckCacheEntry>())
        {
            var key = pair.Key?.Trim() ?? string.Empty;
            var entry = pair.Value;
            if (string.IsNullOrWhiteSpace(key) ||
                entry is null ||
                string.IsNullOrWhiteSpace(entry.ProfileId) ||
                string.IsNullOrWhiteSpace(entry.ModId) ||
                string.IsNullOrWhiteSpace(entry.CurrentVersion) ||
                string.IsNullOrWhiteSpace(entry.Status))
            {
                continue;
            }

            result[key] = new ModUpdateCheckCacheEntry
            {
                ProfileId = entry.ProfileId.Trim(),
                ModId = entry.ModId.Trim(),
                CurrentVersion = entry.CurrentVersion.Trim(),
                Status = entry.Status.Trim(),
                Result = entry.Result,
                CheckedAtUtc = entry.CheckedAtUtc
            };
        }

        return result;
    }

    private static string NormalizeIpRules(string? value)
    {
        var rules = (value ?? string.Empty)
            .Split(['\r', '\n', ';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static rule => !string.IsNullOrWhiteSpace(rule))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return rules.Count == 0 ? string.Empty : string.Join(Environment.NewLine, rules);
    }

    private static string NormalizeListenPrefix(string? value)
    {
        var raw = string.IsNullOrWhiteSpace(value)
            ? "http://127.0.0.1:18089/"
            : value.Trim();
        if (!raw.EndsWith('/'))
        {
            raw += "/";
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return "http://127.0.0.1:18089/";
        }

        var prefix = uri.GetLeftPart(UriPartial.Path);
        return prefix.EndsWith('/') ? prefix : prefix + "/";
    }

    private static string NormalizeFilePathOrEmpty(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeHttpUrlOrDefault(string? value, string defaultValue)
    {
        var raw = string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
        return Uri.TryCreate(raw, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
               !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.ToString()
            : defaultValue;
    }
}
