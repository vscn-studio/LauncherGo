using System.Globalization;
using LauncherGo.Domains.Enums;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services.Paths;

public static class LauncherPathHelper
{
    public const string DefaultServerDownloadCatalogUrl = "https://api.vintagestory.at/stable-unstable.json";

    public static string AppRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LauncherGo");

    public static string PreferencesFilePath => Path.Combine(AppRoot, "launcher-preferences.json");

    public static string LogDirectory => Path.Combine(AppRoot, "logs");

    public static string DefaultWorkspaceRoot => AppRoot;

    public static string DefaultServerDirectory => GetServerDirectory(DefaultWorkspaceRoot);

    public static string DefaultProfileDirectory => GetProfileDirectory(DefaultWorkspaceRoot);

    public static string DefaultSaveDirectory => GetSaveDirectory(DefaultWorkspaceRoot);

    public static string DefaultQqBotDirectory => GetQqBotDirectory(DefaultWorkspaceRoot);

    public static string DefaultSaveCompressionDirectory => GetSaveCompressionDirectory(DefaultWorkspaceRoot);

    public static string GetWorkspaceRootOrDefault(string? workspaceRoot) =>
        NormalizeDirectoryOrDefault(workspaceRoot, DefaultWorkspaceRoot);

    public static string GetServerDirectory(string workspaceRoot) =>
        Path.Combine(GetWorkspaceRootOrDefault(workspaceRoot), "servers");

    public static string GetProfileDirectory(string workspaceRoot) =>
        Path.Combine(GetWorkspaceRootOrDefault(workspaceRoot), "profiles");

    public static string GetSaveDirectory(string workspaceRoot) =>
        Path.Combine(GetWorkspaceRootOrDefault(workspaceRoot), "saves");

    public static string GetQqBotDirectory(string workspaceRoot) =>
        Path.Combine(GetWorkspaceRootOrDefault(workspaceRoot), "qqbot");

    public static string GetSaveCompressionDirectory(string workspaceRoot) =>
        Path.Combine(GetWorkspaceRootOrDefault(workspaceRoot), "compressed-saves");

    public static LauncherPreferences BuildDefaults()
    {
        return new LauncherPreferences
        {
            Language = CultureInfo.CurrentUICulture.Name,
            WorkspaceRoot = DefaultWorkspaceRoot,
            ServerDirectory = DefaultServerDirectory,
            ProfileDirectory = DefaultProfileDirectory,
            SaveDirectory = DefaultSaveDirectory,
            QqBotDirectory = DefaultQqBotDirectory,
            SaveCompression = new SaveCompressionSettings
            {
                CompressionPath = DefaultSaveCompressionDirectory
            },
            ServerDownloadCatalogUrl = DefaultServerDownloadCatalogUrl,
            EnableChunkedDownloads = false,
            DownloadChunkCount = 4,
            DownloadThreadCount = 4,
            GitHubProxy = GitHubProxyKind.Direct,
            AutoCheckUpdates = true,
            DefaultLaunchProfileId = string.Empty,
            DefaultLaunchSaveFile = string.Empty,
            QuickCommands = [],
            ConsoleLogFilters = [],
            AutoStartServerOnLaunch = false,
            AutoRestartServerAfterCrash = false,
            AutoStartServerProfileId = string.Empty,
            AutoStartGatewayOnLaunch = false,
            Robot = new RobotIntegrationSettings
            {
                DatabasePath = Path.Combine(DefaultQqBotDirectory, "vs2qq.db")
            },
            Frp = new FrpIntegrationSettings(),
            EasyTier = new EasyTierIntegrationSettings(),
            TcpGateway = new TcpGatewaySettings()
        };
    }

    public static void EnsureBaseDirectories(LauncherPreferences preferences)
    {
        Directory.CreateDirectory(AppRoot);
        Directory.CreateDirectory(preferences.ServerDirectory);
        Directory.CreateDirectory(preferences.ProfileDirectory);
        Directory.CreateDirectory(preferences.SaveDirectory);
        Directory.CreateDirectory(preferences.QqBotDirectory);
        if (!string.IsNullOrWhiteSpace(preferences.SaveCompression?.CompressionPath))
        {
            Directory.CreateDirectory(preferences.SaveCompression.CompressionPath);
        }
    }

    public static string NormalizeDirectoryOrDefault(string? path, string defaultPath)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                return Path.GetFullPath(path.Trim());
            }
            catch
            {
                // ignore and fallback to default
            }
        }

        return Path.GetFullPath(defaultPath);
    }
}
