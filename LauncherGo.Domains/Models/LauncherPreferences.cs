using System.Globalization;
using LauncherGo.Domains.Enums;

using System.Text.Json.Serialization;

namespace LauncherGo.Domains.Models;

public class LauncherPreferences
{
    public bool IsOnboardingCompleted { get; set; }

    public string Language { get; set; } = CultureInfo.CurrentUICulture.Name;

    public ThemeMode ThemeMode { get; set; } = ThemeMode.System;

    public string WorkspaceRoot { get; set; } = string.Empty;

    [JsonIgnore]
    public string ServerDirectory { get; set; } = string.Empty;

    [JsonIgnore]
    public string ProfileDirectory { get; set; } = string.Empty;

    [JsonIgnore]
    public string SaveDirectory { get; set; } = string.Empty;

    [JsonIgnore]
    public string QqBotDirectory { get; set; } = string.Empty;

    public string ServerDownloadCatalogUrl { get; set; } = string.Empty;

    public bool EnableChunkedDownloads { get; set; }

    public int DownloadChunkCount { get; set; } = 4;

    public int DownloadThreadCount { get; set; } = 4;

    public GitHubProxyKind GitHubProxy { get; set; } = GitHubProxyKind.Direct;

    public bool AutoCheckUpdates { get; set; } = true;

    public string DefaultLaunchProfileId { get; set; } = string.Empty;

    public string DefaultLaunchSaveFile { get; set; } = string.Empty;

    public List<string> DefaultLaunchProfileIds { get; set; } = [];

    public List<string> QuickCommands { get; set; } = [];

    public List<ConsoleLogFilterRule> ConsoleLogFilters { get; set; } = [];

    public Dictionary<string, ModUpdateCheckCacheEntry> ModUpdateChecks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool StartWithWindows { get; set; }

    public bool CloseToTrayOnExit { get; set; }

    public bool StartHiddenOnLaunch { get; set; }

    public bool AutoStartServerOnLaunch { get; set; }

    public bool AutoRestartServerAfterCrash { get; set; }

    public string AutoStartServerProfileId { get; set; } = string.Empty;

    public List<string> AutoStartServerProfileIds { get; set; } = [];

    public bool AutoStartRobotOnLaunch { get; set; }

    public bool AutoStartFrpOnLaunch { get; set; }

    public bool AutoStartThirdPartyFrpcOnLaunch { get; set; }

    public bool AutoStartEasyTierOnLaunch { get; set; }

    public bool AutoStartGatewayOnLaunch { get; set; }

    public RobotIntegrationSettings Robot { get; set; } = new();

    public FrpIntegrationSettings Frp { get; set; } = new();

    public EasyTierIntegrationSettings EasyTier { get; set; } = new();

    public TcpGatewaySettings TcpGateway { get; set; } = new();

    public SaveCompressionSettings SaveCompression { get; set; } = new();
}
