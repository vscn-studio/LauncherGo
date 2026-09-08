using System.Text.RegularExpressions;
using LauncherGo.Services.Paths;

namespace LauncherGo.Services;

internal static partial class WorkspacePathHelper
{
    private static string? _workspaceRoot;

    public static string WorkspaceRoot => LauncherPathHelper.GetWorkspaceRootOrDefault(_workspaceRoot);

    public static void SetWorkspaceRoot(string? workspaceRoot)
    {
        var normalized = LauncherPathHelper.GetWorkspaceRootOrDefault(workspaceRoot);
        var changed = !string.Equals(_workspaceRoot, normalized, StringComparison.OrdinalIgnoreCase);
        _workspaceRoot = normalized;
        EnsureWorkspace();
        if (changed) CacheMaintenance.RegisterWorkspace(normalized);
    }

    public static string DataRoot => LauncherPathHelper.GetProfileDirectory(WorkspaceRoot);

    public static string SavesRoot => Path.Combine(WorkspaceRoot, "saves");

    public static string ServersRoot => Path.Combine(LauncherPathHelper.GetServerDirectory(WorkspaceRoot), "installed");

    public static string PackagesRoot => Path.Combine(WorkspaceRoot, "packages");

    public static string TempRoot => Path.Combine(WorkspaceRoot, ".tmp");

    public static string RuntimeRoot => Path.Combine(WorkspaceRoot, ".runtime");

    public static string ServerRelayRoot => Path.Combine(RuntimeRoot, "server-relays");

    public static string ServerHostRuntimeRoot => Path.Combine(RuntimeRoot, "server-host");

    public static string GatewayRoot => Path.Combine(RuntimeRoot, "tcp-gateway");

    public static string GatewayHostRuntimeRoot => Path.Combine(RuntimeRoot, "gateway-host");

    public static string GatewayConfigPath => Path.Combine(GatewayRoot, "gateway-config.json");

    public static string GatewayStatePath => Path.Combine(GatewayRoot, "gateway-state.json");

    public static string GatewayStopSignalPath => Path.Combine(GatewayRoot, "gateway.stop");

    public static string GatewayReloadSignalPath => Path.Combine(GatewayRoot, "gateway.reload");

    public static string GatewayRoutingHistoryPath => Path.Combine(GatewayRoot, "routing-history.jsonl");

    public static string FrpRoot => Path.Combine(RuntimeRoot, "frp");

    public static string FrpExecutablePath => Path.Combine(FrpRoot, "frpc.exe");

    public static string FrpConfigPath => Path.Combine(FrpRoot, "frpc.toml");

    public static string ThirdPartyFrpcRoot => Path.Combine(RuntimeRoot, "third-party-frpc");

    public static string ThirdPartyFrpcExecutablePath => Path.Combine(ThirdPartyFrpcRoot, "frpc.exe");

    public static string ThirdPartyFrpcConfigPath => Path.Combine(ThirdPartyFrpcRoot, "frpc.ini");

    public static string EasyTierRoot => Path.Combine(RuntimeRoot, "easytier");

    public static string EasyTierCoreExecutablePath => Path.Combine(EasyTierRoot, "easytier-core.exe");

    public static string EasyTierCliExecutablePath => Path.Combine(EasyTierRoot, "easytier-cli.exe");

    public static string RobotRoot => Path.Combine(WorkspaceRoot, "qqbot");

    public static string RobotSettingsPath => Path.Combine(RobotRoot, "vs2qq-settings.json");

    public static string DiscordRoot => Path.Combine(RuntimeRoot, "discordbot");

    public static string DiscordSettingsPath => Path.Combine(DiscordRoot, "discord-settings.json");

    public static string DiscordPlayerBindingsPath => Path.Combine(DiscordRoot, "player-bindings.json");

    public static string DiscordLogPath => Path.Combine(DiscordRoot, "discord.log");

    public static string GetServerRelayStatePath(string profileId) =>
        Path.Combine(ServerRelayRoot, $"{SanitizeFileName(profileId)}.json");

    public static string GetProfileDataPath(string profileId) => Path.Combine(DataRoot, profileId);

    public static string GetServerInstallPath(string version) => Path.Combine(ServersRoot, version);

    public static string GetProfileSavesPath(string profileId) =>
        Path.Combine(GetProfileDataPath(profileId), "Saves");

    public static string GetDetachedProfileSavesPath(string profileId) => Path.Combine(SavesRoot, profileId);

    public static string GetProfileDefaultSaveFile(string profileId) =>
        Path.Combine(GetProfileSavesPath(profileId), "default.vcdbs");

    public static void EnsureWorkspace()
    {
        Directory.CreateDirectory(WorkspaceRoot);
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(SavesRoot);
        Directory.CreateDirectory(ServersRoot);
        Directory.CreateDirectory(PackagesRoot);
        Directory.CreateDirectory(TempRoot);
        Directory.CreateDirectory(RuntimeRoot);
        Directory.CreateDirectory(ServerRelayRoot);
        Directory.CreateDirectory(ServerHostRuntimeRoot);
        Directory.CreateDirectory(GatewayRoot);
        Directory.CreateDirectory(GatewayHostRuntimeRoot);
        Directory.CreateDirectory(FrpRoot);
        Directory.CreateDirectory(ThirdPartyFrpcRoot);
        Directory.CreateDirectory(EasyTierRoot);
        Directory.CreateDirectory(RobotRoot);
        Directory.CreateDirectory(DiscordRoot);

    }

    public static string GetProfileConfigPath(string profileDataPath) =>
        Path.Combine(ResolveProfileDataPath(profileDataPath), "serverconfig.json");

    public static string GetProfileModsPath(string profileDataPath) =>
        Path.Combine(ResolveProfileDataPath(profileDataPath), "Mods");

    public static string GetProfileLogsPath(string profileDataPath) =>
        Path.Combine(ResolveProfileDataPath(profileDataPath), "Logs");

    public static string GetServerMainLogPath(string profileDataPath) =>
        Path.Combine(GetProfileLogsPath(profileDataPath), "server-main.log");

    public static string ResolveProfileDataPath(string profileDataPath)
    {
        var fullPath = NormalizePathOrEmpty(profileDataPath);
        if (string.IsNullOrWhiteSpace(fullPath) || !Directory.Exists(fullPath))
        {
            return string.IsNullOrWhiteSpace(fullPath) ? profileDataPath : fullPath;
        }

        if (IsProfileDataDirectory(fullPath))
        {
            return fullPath;
        }

        var directoryName = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(directoryName))
        {
            var sameNameChild = Path.Combine(fullPath, directoryName);
            if (IsProfileDataDirectory(sameNameChild))
            {
                return sameNameChild;
            }
        }

        try
        {
            var candidates = Directory
                .EnumerateDirectories(fullPath, "*", SearchOption.TopDirectoryOnly)
                .Where(IsProfileDataDirectory)
                .Take(2)
                .ToList();
            if (candidates.Count == 1)
            {
                return candidates[0];
            }
        }
        catch
        {
            // Keep the registered path when probing is not possible.
        }

        return fullPath;
    }

    public static string SanitizeFileName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = string.Join('_', name.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(sanitized) ? "unnamed" : sanitized;
    }

    public static string? TryExtractVersionFromPackageName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var match = VersionPattern().Match(fileName);
        return match.Success ? match.Groups["version"].Value.Trim() : null;
    }

    private static bool IsProfileDataDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return false;
        }

        if (File.Exists(Path.Combine(path, "serverconfig.json")))
        {
            return true;
        }

        var knownDirectoryCount = 0;
        if (Directory.Exists(Path.Combine(path, "Logs"))) knownDirectoryCount++;
        if (Directory.Exists(Path.Combine(path, "Saves"))) knownDirectoryCount++;
        if (Directory.Exists(Path.Combine(path, "Mods"))) knownDirectoryCount++;
        if (Directory.Exists(Path.Combine(path, "ModConfig"))) knownDirectoryCount++;
        if (Directory.Exists(Path.Combine(path, "Playerdata"))) knownDirectoryCount++;

        return knownDirectoryCount >= 2;
    }

    private static string NormalizePathOrEmpty(string? path)
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

    [GeneratedRegex(@"^vs_server_win-x64_(?<version>.+)\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionPattern();
}
