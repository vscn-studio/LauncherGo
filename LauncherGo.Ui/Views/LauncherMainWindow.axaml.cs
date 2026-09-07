using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LauncherGo.Abstractions.Services;
using LauncherGo.Abstractions.Services.I18n;
using LauncherGo.Domains.Enums;
using LauncherGo.Domains.Models;
using LauncherGo.Ui;
using LauncherGo.Ui.Converters;
using LauncherGo.Ui.Platform;
using LauncherGo.Ui.Services;
using LauncherGo.Ui.Services.I18n;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LauncherGo.Ui.Views;

public partial class LauncherMainWindow : Window
{
    private const int RealtimeRangeSeconds = 60;
    private const int NetworkRangeCount = 144;
    private const int MaxConsoleLines = 800;
    private const double ConsoleAutoScrollThreshold = 12;
    private const int ConsoleRefreshDelayMs = 80;
    private const int ServerStartTimeoutSeconds = 30;
    private const int RunningServerLogReplayGraceSeconds = 5;
    private const int ConsoleProfileReplayLogBytes = 256 * 1024;
    private const int ConsoleProfileReplayLogLines = 220;
    private const double ChartWidth = 640;
    private const double ChartHeight = 248;
    private const double ThumbnailWidth = 76;
    private const double ThumbnailHeight = 50;
    private const string DefaultServerDownloadCatalogUrl = "https://cdn.vintagestory.top/stable-unstable.json";
    private const string GitHubContributorsApiUrl = "https://api.github.com/repos/vscn-studio/LauncherGo/contributors?per_page=100";
    private const string SponsorApiUrl = "https://vscn.studio/api/afdian/sponsors";
    private const string LaunchStartIconData =
        "M187.2 100.9C174.8 94.1 159.8 94.4 147.6 101.6C135.4 108.8 128 121.9 128 136L128 504C128 518.1 135.5 531.2 147.6 538.4C159.7 545.6 174.8 545.9 187.2 539.1L523.2 355.1C536 348.1 544 334.6 544 320C544 305.4 536 291.9 523.2 284.9L187.2 100.9z";
    private const string LaunchStopIconData =
        "M160 96L480 96C515.3 96 544 124.7 544 160L544 480C544 515.3 515.3 544 480 544L160 544C124.7 544 96 515.3 96 480L96 160C96 124.7 124.7 96 160 96z";
    private static readonly HttpClient SharedHttpClient = CreateSharedHttpClient();

    private static readonly (string Zh, string En)[] HomeSlogans =
    [
        ("Launcher Go !", "Launcher Go !"),
        ("极速启动服务，高自定义功能", "Fast startup, highly customizable"),
        ("24*7小时测试环境，追求0漏洞", "24*7 tested, aiming for zero defects"),
        ("极致开服体验，从Launcher Go开始", "Start the best server experience with Launcher Go")
    ];

    private static readonly (string Zh, string En)[] StaticUiTranslations =
    [
        ("服务器", "Server"),
        ("管理", "Manage"),
        ("连接", "Connections"),
        ("设置", "Settings"),
        ("加入时间", "Joined"),
        ("选择", "Select"),
        ("档案", "Profile"),
        ("版本", "Version"),
        ("档案目录", "Profile Directory"),
        ("当前存档", "Active Save"),
        ("操作", "Actions"),
        ("修改", "Edit"),
        ("存档", "Save file"),
        ("大小", "Size"),
        ("修改时间", "Modified"),
        ("路径", "Path"),
        ("脚本路径", "Script path"),
        ("服务器名称", "Server Name"),
        ("配置路径", "Config Path"),
        ("启用", "Enabled"),
        ("模式", "Mode"),
        ("匹配模式", "Match mode"),
        ("匹配内容", "Match pattern"),
        ("开始条件", "Start condition"),
        ("结束条件", "End condition"),
        ("开始", "Start time"),
        ("结束", "End"),
        ("动作", "Action"),
        ("时间", "Time"),
        ("周期", "Schedule"),
        ("执行设置", "Execution settings"),
        ("下次执行", "Next Run"),
        ("消息", "Message"),
        ("命令", "Command"),
        ("日志路径", "Log path"),
        ("日志文件夹", "Log folder"),
        ("查看日志", "View logs"),
        ("打开文件夹", "Open folder"),
        ("定时备份", "Scheduled Backup"),
        ("日志导出", "Log Export"),
        ("定时广播", "Scheduled Broadcast"),
        ("定时命令", "Scheduled Commands"),
        ("定时开关服", "Scheduled Start/Stop"),
        ("依赖", "Dependencies"),
        ("问题", "Issues"),
        ("文件", "File"),
        ("平台", "Platform"),
        ("打开", "Open"),
        ("服务器档案", "Server Profile"),
        ("绑定群号", "Bound Group IDs"),
        ("超级管理员 QQ", "Super Admin QQ IDs"),
        ("玩家", "Player"),
        ("注册", "Registered"),
        ("最后登录", "Last Login"),
        ("密码", "Password"),
        ("清空密码", "Clear Password"),
        ("创建", "Create"),
        ("创建存档", "Create Save"),
        ("导入", "Import"),
        ("删除", "Delete"),
        ("刷新", "Refresh"),
        ("保存", "Save"),
        ("返回", "Back"),
        ("清空", "Clear"),
        ("添加", "Add"),
        ("浏览", "Browse"),
        ("复制", "Copy"),
        ("发送", "Send"),
        ("启动", "Start"),
        ("停止", "Stop"),
        ("配置", "Config"),
        ("下载版本", "Downloads"),
        ("游戏服务端", "Game Server"),
        ("直连", "Direct"),
        ("日志", "Logs"),
        ("实例", "Instance"),
        ("模组", "Mods"),
        ("自动化", "Automation"),
        ("安全", "Security"),
        ("机器人", "Robot"),
        ("网关", "Gateway"),
        ("TCP 网关", "TCP Gateway"),
        ("监听地址", "Listen Address"),
        ("监听端口", "Listen Port"),
        ("最大连接数", "Max Connections"),
        ("单 IP 连接上限", "Per-IP Connection Limit"),
        ("后端连接超时秒数", "Backend Connect Timeout Seconds"),
        ("健康检查间隔秒数", "Health Check Interval Seconds"),
        ("IP 白名单", "IP Allow List"),
        ("IP 黑名单", "IP Block List"),
        ("每行一个 IP 或 CIDR 网段；黑名单优先。", "One IP or CIDR range per line; block list takes priority."),
        ("后端服务器", "Backend Servers"),
        ("后端名称", "Backend Name"),
        ("主机", "Host"),
        ("端口", "Port"),
        ("权重", "Weight"),
        ("路由状态", "Routing State"),
        ("本地实例", "Local Instance"),
        ("路由与重定向", "Routing & Redirect"),
        ("部署重定向模组", "Deploy Redirect Mod"),
        ("历史", "History"),
        ("后端名称/主机/ServerId", "Backend Name / Host / ServerId"),
        ("实时 / 峰值流量", "Current / Peak Traffic"),
        ("重定向", "Redirect"),
        ("维护", "Maintenance"),
        ("疏散", "Evacuate"),
        ("运行状态", "Runtime Status"),
        ("活跃连接", "Active Connections"),
        ("已接收", "Accepted"),
        ("已拒绝", "Rejected"),
        ("失败", "Failed"),
        ("上行", "Upstream"),
        ("下行", "Downstream"),
        ("状态", "Status"),
        ("错误", "Error"),
        ("统计", "Statistics"),
        ("高级", "Advanced"),
        ("关于", "About"),
        ("贡献者", "Contributors"),
        ("赞助者", "Sponsors"),
        ("启动时自动启动网关", "Auto-start Gateway on launch")
    ];

    private static IReadOnlyList<SupportedLanguageOption> AppearanceLanguageOptions => SupportedLanguages.All;

    private static readonly (ThemeMode Mode, string Zh, string En)[] AppearanceThemeOptions =
    [
        (ThemeMode.Light, "亮色主题", "Light Theme"),
        (ThemeMode.Dark, "暗色主题", "Dark Theme"),
        (ThemeMode.System, "跟随系统", "Follow System")
    ];

    private static readonly string[] ConfigServerLanguageOptions =
    [
        "en", "ar", "be", "cs", "da", "de", "es-es", "fr", "hu", "is", "it", "ja", "ko",
        "nl", "no", "pl", "pt-br", "pt-pt", "ru", "sr", "zh-cn", "zh-tw"
    ];

    private static readonly (string Value, string Zh, string En)[] ConfigPlayStyleDefinitions =
    [
        ("surviveandbuild", "标准", "Standard"),
        ("exploration", "探索", "Exploration"),
        ("wildernesssurvival", "荒野求生", "Wilderness Survival"),
        ("homosapiens", "智人", "Homo sapiens"),
        ("creativebuilding", "超平坦创造模式", "Creative Building")
    ];

    private static readonly (string Value, string Zh, string En)[] ConfigWorldTypeDefinitions =
    [
        ("standard", "标准地形", "Standard"),
        ("superflat", "超平坦", "Superflat")
    ];

    private static readonly (int Value, string Zh, string En)[] ConfigWhitelistModeDefinitions =
    [
        (0, "默认（专用服务器启用白名单）", "Default (on for dedicated servers)"),
        (1, "关闭", "Off"),
        (2, "开启", "On")
    ];

    private static readonly (string Value, string Zh, string En)[] ConfigRoleDefinitions =
    [
        ("suplayer", "生存玩家", "Survival Player"),
        ("sumod", "生存管理员", "Survival Moderator"),
        ("suadmin", "生存服主", "Survival Admin"),
        ("crplayer", "创造玩家", "Creative Player"),
        ("crmod", "创造管理员", "Creative Moderator"),
        ("cradmin", "创造服主", "Creative Admin")
    ];

    private static readonly HashSet<string> ConfigOnlyDuringWorldCreateRuleKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "startingClimate",
        "graceTimer",
        "worldClimate",
        "landcover",
        "oceanscale",
        "upheavelCommonness",
        "geologicActivity",
        "landformScale",
        "worldWidth",
        "worldLength",
        "polarEquatorDistance",
        "storyStructuresDistScaling",
        "globalTemperature",
        "globalPrecipitation",
        "globalForestation"
    };

    private readonly ILauncherPreferencesService _preferencesService;
    private readonly IServerPackageService _serverPackageService;
    private readonly IInstanceProfileService _profileService;
    private readonly IInstanceSaveService _saveService;
    private readonly IInstanceServerConfigService _instanceServerConfigService;
    private readonly IServerProcessService _serverProcessService;
    private readonly IRobotService _robotService;
    private readonly IDiscordBotService _discordBotService;
    private readonly ILogTailService _logTailService;
    private readonly IAutomationService _automationService;
    private readonly IAutomationSettingsService _automationSettingsService;
    private readonly IFrpService _frpService;
    private readonly IThirdPartyFrpcService _thirdPartyFrpcService;
    private readonly IEasyTierService _easyTierService;
    private readonly ITcpGatewayService _tcpGatewayService;
    private readonly IGatewayRedirectModService _gatewayRedirectModService;
    private readonly IInstanceModService _instanceModService;
    private readonly IModListExportService _modListExportService;
    private readonly IModUpdateService _modUpdateService;
    private readonly IServerAuthService _serverAuthService;
    private readonly IServerMapService _serverMapService;
    private readonly IServerBridgeService _serverBridgeService;
    private readonly ILauncherUpdateService _launcherUpdateService;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger<LauncherMainWindow> _logger;
    private readonly DispatcherTimer _dataTimer;
    private readonly DispatcherTimer _tickerTimer;
    private readonly DispatcherTimer _homeSloganTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly DateTimeOffset _windowStartedAtUtc = DateTimeOffset.UtcNow;

    private readonly List<double> _serverCpuSamples = [];
    private readonly List<double> _serverMemoryMbSamples = [];
    private readonly List<double> _robotCpuSamples = [];
    private readonly List<double> _robotMemoryMbSamples = [];
    private readonly List<double> _playersSamples = [];
    private readonly List<double> _networkLatencySamples = [];
    private readonly List<string> _playerEvents = [];

    private readonly List<string> _consoleLines = [];
    private readonly ObservableCollection<ProfileListItem> _profileItems = [];
    private readonly ObservableCollection<SaveListItem> _saveItems = [];
    private readonly ObservableCollection<DownloadVersionListItem> _downloadVersionItems = [];
    private readonly ObservableCollection<ConfigChoiceOption> _configWhitelistModeOptions = [];
    private readonly ObservableCollection<ConfigChoiceOption> _configDefaultRoleOptions = [];
    private readonly ObservableCollection<ConfigChoiceOption> _configPlayStyleOptions = [];
    private readonly ObservableCollection<ConfigChoiceOption> _configWorldTypeOptions = [];
    private readonly ObservableCollection<ConfigSaveFileItem> _configSaveItems = [];
    private readonly ObservableCollection<ConfigWorldRuleItem> _configWorldRuleItems = [];
    private readonly ObservableCollection<ConfigChoiceOption> _thirdPartyFrpcModeOptions = [];
    private readonly ObservableCollection<ConfigChoiceOption> _saveCompressionUpdateModeOptions = [];
    private readonly ObservableCollection<ConfigChoiceOption> _gitHubProxyOptions = [];
    private readonly ObservableCollection<SettingsContributorItem> _settingsContributorItems = [];
    private readonly ObservableCollection<SettingsSponsorItem> _settingsSponsorItems = [];
    private readonly ObservableCollection<InstanceProfile> _automationProfileItems = [];
    private readonly ObservableCollection<ProfileConfigListItem> _automationConfigItems = [];
    private readonly ObservableCollection<AutomationActionWindowItem> _automationActionWindowItems = [];
    private readonly ObservableCollection<AutomationBackupScheduleItem> _automationBackupScheduleItems = [];
    private readonly ObservableCollection<ScheduledBroadcastItem> _automationBroadcastItems = [];
    private readonly ObservableCollection<ScheduledCommandItem> _automationCommandItems = [];
    private readonly ObservableCollection<AutomationTimeItem> _automationExportTimeItems = [];
    private readonly ObservableCollection<AutomationScriptItem> _automationScriptItems = [];
    private readonly ObservableCollection<ProfileLogListItem> _logItems = [];
    private readonly ObservableCollection<InstanceProfile> _modProfileItems = [];
    private readonly ObservableCollection<ModListItem> _modItems = [];
    private readonly ObservableCollection<InstanceProfile> _authProfileItems = [];
    private readonly ObservableCollection<ProfileConfigListItem> _authConfigItems = [];
    private readonly ObservableCollection<InstanceProfile> _serverBridgeProfileItems = [];
    private readonly ObservableCollection<ProfileConfigListItem> _serverBridgeConfigItems = [];
    private readonly List<AuthPlayerListItem> _authPlayerSourceItems = [];
    private readonly ObservableCollection<AuthPlayerListItem> _authPlayerItems = [];
    private readonly ObservableCollection<RobotProfileBindingItem> _robotBindingItems = [];
    private readonly ObservableCollection<RobotTeleportPointItem> _robotTeleportPointItems = [];
    private readonly ObservableCollection<RobotCustomCommandItem> _robotCustomCommandItems = [];
    private readonly ObservableCollection<InstanceProfile> _robotProfileItems = [];
    private readonly ObservableCollection<DiscordProfileBindingItem> _discordBindingItems = [];
    private readonly ObservableCollection<DiscordCustomCommandItem> _discordCustomCommandItems = [];
    private readonly ObservableCollection<TcpGatewayBackend> _gatewayBackendItems = [];
    private readonly ObservableCollection<GatewayBackendRuntimeItem> _gatewayBackendRuntimeItems = [];
    private readonly ObservableCollection<InstanceProfile> _serverMapProfileItems = [];
    private readonly ObservableCollection<ProfileConfigListItem> _serverMapConfigItems = [];
    private readonly HashSet<GatewayBackendStatisticsWindow> _gatewayStatisticsWindows = [];
    private readonly ObservableCollection<DashboardServerItem> _dashboardServerItems = [];
    private readonly ObservableCollection<DashboardPlayerItem> _dashboardOnlinePlayerItems = [];
    private readonly ObservableCollection<DashboardUptimeItem> _dashboardUptimeItems = [];
    private readonly ObservableCollection<LaunchTargetItem> _launchTargetItems = [];
    private readonly ObservableCollection<InstanceProfile> _launchAddProfileItems = [];
    private readonly ObservableCollection<LaunchTargetItem> _settingsAutoStartTargetItems = [];
    private readonly ObservableCollection<InstanceProfile> _settingsAutoStartAddProfileItems = [];
    private readonly ObservableCollection<ConsoleLogFilterRuleItem> _consoleLogFilterRuleItems = [];
    private readonly ObservableCollection<ConsoleLogFilterRuleItem> _visibleConsoleLogFilterRuleItems = [];
    private readonly ObservableCollection<ConsoleServerItem> _consoleServerItems = [];
    private readonly List<ServerDownloadEntry> _catalogEntries = [];
    private readonly Dictionary<string, string> _configGameLanguageZh = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _consoleLinesByProfile = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ConsoleLogFilterRule> _consoleLogFilterRules = [];
    private readonly HashSet<string> _consoleReplayLoadedProfileIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _tailedProfileIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _replayedLogProfileIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ServerCommonSettings> _dashboardSettingsByProfile = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dashboardSettingsLoadingProfileIds = new(StringComparer.OrdinalIgnoreCase);

    private MainTab _selectedTab = MainTab.Monitor;
    private HomeMetric _selectedMetric = HomeMetric.Server;
    private InstanceManageTab _selectedInstanceManageTab = InstanceManageTab.Profiles;
    private SettingsTab _selectedSettingsTab = SettingsTab.Server;
    private ConnectionTab _selectedConnectionTab = ConnectionTab.Frp;
    private bool _logsNavSelected;
    private int _tickerIndex;
    private int _homeSloganIndex;
    private bool _tickerAnimating;
    private bool _homeSloganVisible = true;
    private bool _isChinese;
    private bool _isApplyingAppearanceSettings;
    private bool _downloadCatalogLoaded;
    private bool _isStoppingOrStarting;
    private bool _isRefreshingSaves;
    private bool _isRefreshingConfigProfiles;
    private bool _isLoadingConfig;
    private bool _isConfigLoaded;
    private bool _isApplyingServerSettings;
    private bool _isApplyingNetworkSettings;
    private bool _isApplyingConnectionSettings;
    private bool _aboutIntroductionLoaded;
    private bool _contributorsLoaded;
    private bool _sponsorsLoaded;
    private bool _consoleAutoScroll = true;
    private bool _consoleRefreshQueued;
    private bool _isFrpRunning;
    private bool _isThirdPartyFrpcRunning;
    private bool _isEasyTierRunning;
    private bool _isTogglingFrp;
    private bool _isTogglingThirdPartyFrpc;
    private bool _isTogglingEasyTier;
    private bool _isTogglingGateway;
    private bool _isRefreshingGateway;
    private bool _isTogglingRobot;
    private bool _isTogglingDiscord;
    private bool _isExitRequested;
    private bool _isExitConfirmationOpen;
    private bool _staticUiTranslationQueued;
    private bool _languageRefreshQueued;
    private bool _isApplyingLocalizedOptions;
    private bool _isRefreshingAutomation;
    private bool _isRefreshingMods;
    private bool _isCheckingModUpdates;
    private bool _isUpdatingModSelectAll;
    private readonly List<string> _modImportPaths = [];
    private bool _isRefreshingAuth;
    private bool _isRefreshingServerBridge;
    private bool _isRefreshingServerMap;
    private string _editingServerMapProfileId = string.Empty;
    private bool _toastPointerOver;
    private string _editingConfigProfileId = string.Empty;
    private string _pendingConfigLoadProfileId = string.Empty;
    private string _loadedConfigProfileId = string.Empty;
    private string _selectedConsoleProfileId = string.Empty;
    private string _editingAutomationProfileId = string.Empty;
    private string _editingAuthProfileId = string.Empty;
    private string _editingServerBridgeProfileId = string.Empty;
    private long _configLoadVersion;
    private long _dashboardSettingsVersion;
    private TimeSpan _robotLastProcessorTime;
    private DateTimeOffset _robotLastCpuSampleUtc = DateTimeOffset.UtcNow;
    private double _robotLastCpuPercent;
    private string _configGameLanguageZhPath = string.Empty;
    private string _configSaveFileLocation = string.Empty;
    public LauncherMainWindow()
        : this(
            ServiceLocator.GetRequiredService<ILauncherPreferencesService>(),
            ServiceLocator.GetRequiredService<IServerPackageService>(),
            ServiceLocator.GetRequiredService<IInstanceProfileService>(),
            ServiceLocator.GetRequiredService<IInstanceSaveService>(),
            ServiceLocator.GetRequiredService<IInstanceServerConfigService>(),
            ServiceLocator.GetRequiredService<IServerProcessService>(),
            ServiceLocator.GetRequiredService<IRobotService>(),
            ServiceLocator.GetRequiredService<ILogTailService>(),
            ServiceLocator.GetRequiredService<IAutomationService>(),
            ServiceLocator.GetRequiredService<IAutomationSettingsService>(),
            ServiceLocator.GetRequiredService<IFrpService>(),
            ServiceLocator.GetRequiredService<IThirdPartyFrpcService>(),
            ServiceLocator.GetRequiredService<IEasyTierService>(),
            ServiceLocator.GetRequiredService<ITcpGatewayService>(),
            ServiceLocator.GetRequiredService<IGatewayRedirectModService>(),
            ServiceLocator.GetRequiredService<IInstanceModService>(),
            ServiceLocator.GetRequiredService<IModListExportService>(),
            ServiceLocator.GetRequiredService<IModUpdateService>(),
            ServiceLocator.GetRequiredService<IServerAuthService>(),
            ServiceLocator.GetRequiredService<IServerMapService>(),
            ServiceLocator.GetRequiredService<IServerBridgeService>(),
            ServiceLocator.GetRequiredService<ILauncherUpdateService>(),
            ServiceLocator.GetRequiredService<ILogger<LauncherMainWindow>>(),
            ServiceLocator.GetRequiredService<ILocalizationService>(),
            ServiceLocator.GetRequiredService<IDiscordBotService>())
    {
    }

    public LauncherMainWindow(
        ILauncherPreferencesService preferencesService,
        IServerPackageService serverPackageService,
        IInstanceProfileService profileService,
        IInstanceSaveService saveService,
        IInstanceServerConfigService instanceServerConfigService,
        IServerProcessService serverProcessService,
        IRobotService robotService,
        ILogTailService logTailService,
        IAutomationService automationService,
        IAutomationSettingsService automationSettingsService,
        IFrpService frpService,
        IThirdPartyFrpcService thirdPartyFrpcService,
        IEasyTierService easyTierService,
        ITcpGatewayService tcpGatewayService,
        IGatewayRedirectModService gatewayRedirectModService,
        IInstanceModService instanceModService,
        IModListExportService modListExportService,
        IModUpdateService modUpdateService,
        IServerAuthService serverAuthService,
        IServerMapService serverMapService,
        IServerBridgeService serverBridgeService,
        ILauncherUpdateService launcherUpdateService,
        ILogger<LauncherMainWindow>? logger = null,
        ILocalizationService? localizationService = null,
        IDiscordBotService? discordBotService = null)
    {
        _preferencesService = preferencesService;
        _serverPackageService = serverPackageService;
        _profileService = profileService;
        _saveService = saveService;
        _instanceServerConfigService = instanceServerConfigService;
        _serverProcessService = serverProcessService;
        _robotService = robotService;
        _discordBotService = discordBotService ?? ServiceLocator.GetRequiredService<IDiscordBotService>();
        _logTailService = logTailService;
        _automationService = automationService;
        _automationSettingsService = automationSettingsService;
        _frpService = frpService;
        _thirdPartyFrpcService = thirdPartyFrpcService;
        _easyTierService = easyTierService;
        _tcpGatewayService = tcpGatewayService;
        _gatewayRedirectModService = gatewayRedirectModService;
        _instanceModService = instanceModService;
        _modListExportService = modListExportService;
        _modUpdateService = modUpdateService;
        _serverAuthService = serverAuthService;
        _serverMapService = serverMapService;
        _serverBridgeService = serverBridgeService;
        _launcherUpdateService = launcherUpdateService;
        _localizationService = localizationService ?? new LocalizationService();
        _logger = logger ?? NullLogger<LauncherMainWindow>.Instance;

        InitializeComponent();
        AddHandler(InputElement.PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _localizationService.LanguageChanged += OnLanguageChanged;

        var launcherPreferences = _preferencesService.Load();
        _localizationService.SetLanguage(launcherPreferences.Language);
        _isChinese = _localizationService.CurrentCulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        _dataTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _dataTimer.Tick += OnDataTimerTick;

        _tickerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.3) };
        _tickerTimer.Tick += OnTickerTimerTick;

        _homeSloganTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.1) };
        _homeSloganTimer.Tick += OnHomeSloganTimerTick;

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _toastTimer.Tick += OnToastTimerTick;

        _serverProcessService.OutputReceived += OnServerOutputReceived;
        _serverProcessService.ProfileOutputReceived += OnServerProfileOutputReceived;
        _serverProcessService.StatusChanged += OnServerStatusChanged;
        _logTailService.LogLineReceived += OnLogTailLineReceived;
        _logTailService.ProfileLogLineReceived += OnProfileLogTailLineReceived;
        _frpService.StatusChanged += OnFrpStatusChanged;
        _thirdPartyFrpcService.StatusChanged += OnThirdPartyFrpcStatusChanged;
        _easyTierService.StatusChanged += OnEasyTierStatusChanged;
        _tcpGatewayService.StatusChanged += OnTcpGatewayStatusChanged;
        _discordBotService.StatusChanged += OnDiscordStatusChanged;
        _discordBotService.OutputReceived += OnDiscordOutputReceived;

        InitializeStaticTexts();
        RefreshAppearanceSettingsEditor();
        InitializeCollections();
        InitializeSeries();
        RegisterAutoSaveHandlers();
        RefreshProfiles();
        _ = RefreshSavesAsync();
        _ = RefreshDownloadVersionsAsync(forceReload: false);

        SelectMetric(HomeMetric.Server);
        SelectInstanceManageTab(InstanceManageTab.Profiles);
        SelectSettingsTab(SettingsTab.Server);
        SelectConnectionTab(ConnectionTab.Frp);
        SelectTab(MainTab.Monitor);

        _dataTimer.Start();
        _tickerTimer.Start();
        _homeSloganTimer.Start();

        Opened += OnWindowOpened;
        Closing += OnWindowClosing;

        Closed += (_, _) =>
        {
            _dataTimer.Stop();
            _tickerTimer.Stop();
            _homeSloganTimer.Stop();
            _toastTimer.Stop();
            _serverProcessService.OutputReceived -= OnServerOutputReceived;
            _serverProcessService.ProfileOutputReceived -= OnServerProfileOutputReceived;
            _serverProcessService.StatusChanged -= OnServerStatusChanged;
            _logTailService.LogLineReceived -= OnLogTailLineReceived;
            _logTailService.ProfileLogLineReceived -= OnProfileLogTailLineReceived;
            _frpService.StatusChanged -= OnFrpStatusChanged;
            _thirdPartyFrpcService.StatusChanged -= OnThirdPartyFrpcStatusChanged;
            _easyTierService.StatusChanged -= OnEasyTierStatusChanged;
            _tcpGatewayService.StatusChanged -= OnTcpGatewayStatusChanged;
            _discordBotService.StatusChanged -= OnDiscordStatusChanged;
            _discordBotService.OutputReceived -= OnDiscordOutputReceived;
            _localizationService.LanguageChanged -= OnLanguageChanged;
            _ = _logTailService.StopAsync();
            _ = _robotService.StopAsync(TimeSpan.FromSeconds(2));
            _ = _discordBotService.StopAsync(TimeSpan.FromSeconds(2));
            _ = _frpService.StopAsync(TimeSpan.FromSeconds(2));
            _ = _thirdPartyFrpcService.StopAsync(TimeSpan.FromSeconds(2));
            _ = _easyTierService.StopAsync(TimeSpan.FromSeconds(2));
            _ = _serverMapService.StopAllAsync();
        };
    }

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        Opened -= OnWindowOpened;
        WindowsDwmWindowEffects.Apply(this);

        var preferences = _preferencesService.Load();
        if (preferences.StartHiddenOnLaunch)
        {
            ShowInTaskbar = false;
            Hide();
        }

        try
        {
            using var recoveryCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await _serverProcessService.RefreshStatusesAsync(recoveryCts.Token);
        }
        catch (OperationCanceledException)
        {
            AppendConsoleLine(T(
                "[system] 恢复后台服务器状态超时，界面将继续启动。",
                "[system] Timed out while restoring background server status; startup will continue."));
        }
        catch (Exception ex)
        {
            var errorMessage = GetExceptionMessage(ex);
            AppendConsoleLine(T(
                $"[system] 恢复后台服务器状态失败：{errorMessage}",
                $"[system] Failed to restore background server status: {errorMessage}"));
        }

        await StartConfiguredConnectionServicesAsync(preferences);
        if (preferences.AutoCheckUpdates)
        {
            _ = CheckLauncherUpdatesAsync(onlyShowWhenAvailable: true, includePrerelease: false);
        }
    }

    public void RequestExit()
    {
        _isExitRequested = true;
        Close();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isExitRequested || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
        {
            return;
        }

        var preferences = _preferencesService.Load();
        e.Cancel = true;
        if (preferences.CloseToTrayOnExit)
        {
            HideToTray();
            return;
        }

        ScheduleExitConfirmation();
    }

    private void RequestWindowClose()
    {
        var preferences = _preferencesService.Load();
        if (preferences.CloseToTrayOnExit)
        {
            HideToTray();
            return;
        }

        ScheduleExitConfirmation();
    }

    private void ScheduleExitConfirmation()
    {
        if (_isExitConfirmationOpen)
        {
            return;
        }

        _isExitConfirmationOpen = true;
        Dispatcher.UIThread.Post(async () => await ShowExitConfirmationAsync());
    }

    private async Task ShowExitConfirmationAsync()
    {
        try
        {
            var preferences = _preferencesService.Load();
            var dialog = new LauncherExitConfirmationWindow(_isChinese, preferences.CloseToTrayOnExit);
            var result = await dialog.ShowDialog<LauncherExitConfirmationResult?>(this);
            if (result is null)
            {
                return;
            }

            if (preferences.CloseToTrayOnExit != result.CloseToTrayOnExit)
            {
                preferences.CloseToTrayOnExit = result.CloseToTrayOnExit;
                _preferencesService.Save(preferences);
                SettingsCloseToTrayCheckBox.IsChecked = result.CloseToTrayOnExit;
            }

            if (result.ExitApplication)
            {
                RequestExit();
            }
            else
            {
                HideToTray();
            }
        }
        finally
        {
            _isExitConfirmationOpen = false;
        }
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
    }

    private async Task StartConfiguredConnectionServicesAsync(LauncherPreferences preferences)
    {
        if (preferences.AutoStartServerOnLaunch)
        {
            await StartConfiguredServerAsync(preferences);
        }

        // ServerBridge is hosted by each local server profile; there is no launcher-level HTTP listener.

        if (preferences.AutoStartRobotOnLaunch)
        {
            try
            {
                await _robotService.StartAsync(ToRobotSettings(preferences.Robot));
            }
            catch (Exception ex)
            {
                SetConnectionStatus(T($"QQ机器人自启动失败：{ex.Message}", $"QQ robot auto-start failed: {ex.Message}"));
            }
        }

        if (preferences.AutoStartDiscordOnLaunch)
        {
            try { await _discordBotService.StartAsync(preferences.Discord); }
            catch (Exception ex) { SetConnectionStatus(T($"Discord 自启动失败：{ex.Message}", $"Discord auto-start failed: {ex.Message}")); }
        }

        if (preferences.AutoStartFrpOnLaunch)
        {
            await StartConnectionProcessAsync(ConnectionProcessKind.Frp);
        }

        if (preferences.AutoStartThirdPartyFrpcOnLaunch)
        {
            await StartConnectionProcessAsync(ConnectionProcessKind.ThirdPartyFrpc);
        }

        if (preferences.AutoStartEasyTierOnLaunch)
        {
            await StartEasyTierAsync();
        }

        if (preferences.AutoStartGatewayOnLaunch)
        {
            try
            {
                if (!_tcpGatewayService.GetCurrentStatus().IsRunning)
                {
                    await _tcpGatewayService.StartAsync(preferences.TcpGateway);
                }
            }
            catch (Exception ex)
            {
                SetConnectionStatus(T(
                    $"TCP 网关自启动失败：{GetExceptionMessage(ex)}",
                    $"TCP gateway auto-start failed: {GetExceptionMessage(ex)}"));
            }
        }

        RefreshConnectionRuntimeStatus();
    }

    private async Task StartConfiguredServerAsync(LauncherPreferences preferences)
    {
        try
        {
            var profileIds = SplitProfileIds(preferences.AutoStartServerProfileIds, preferences.AutoStartServerProfileId);
            if (profileIds.Count == 0)
            {
                profileIds = SplitProfileIds(preferences.DefaultLaunchProfileIds, preferences.DefaultLaunchProfileId);
            }

            if (profileIds.Count == 0)
            {
                LaunchSelectionSummaryTextBlock.Text = T("未设置自启动服务器档案", "No auto-start server profile configured");
                return;
            }

            var profilesToStart = new List<InstanceProfile>();
            foreach (var profileId in profileIds)
            {
                var profile = _profileService.GetProfileById(profileId.Trim());
                if (profile is null)
                    continue;

                try
                {
                    using var recoveryCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var status = await _serverProcessService.RefreshStatusAsync(profile.Id, recoveryCts.Token);
                    if (!status.IsRunning)
                        profilesToStart.Add(profile);
                }
                catch (OperationCanceledException)
                {
                    AppendConsoleLine(T(
                        $"[system] 恢复服务器 {profile.Name} 状态超时，已跳过自启动以避免重复进程。",
                        $"[system] Timed out restoring server {profile.Name}; auto-start was skipped to avoid a duplicate process."));
                }
            }

            if (profilesToStart.Count == 0)
                return;

            SetLaunchOperationBusy(T("启动中...", "Starting..."));
            try
            {
                foreach (var profile in profilesToStart)
                {
                    var savePath = NormalizeFullPath(profile.ActiveSaveFile);

                    var reloadedProfile = await EnsureLaunchableProfileSaveAsync(profile, savePath);
                    await StartServerProfileWithTimeoutAsync(reloadedProfile);
                }
            }
            finally
            {
                ClearLaunchOperationBusy();
            }
        }
        catch (Exception ex)
        {
            var errorMessage = GetExceptionMessage(ex);
            AppendConsoleLine(T($"[system] 自启动服务器失败：{errorMessage}", $"[system] Auto-start server failed: {errorMessage}"));
        }
    }

    private void InitializeStaticTexts()
    {
        ServerNavSectionTitleTextBlock.Text = T("服务器", "Server");
        ManageNavSectionTitleTextBlock.Text = T("管理", "Manage");
        ConnectionNavSectionTitleTextBlock.Text = T("连接", "Connections");
        SettingsNavSectionTitleTextBlock.Text = T("设置", "Settings");
        MonitorNavButton.Content = T("仪表盘", "Dashboard");
        ConsoleNavButton.Content = T("控制台", "Console");
        LogsNavButton.Content = T("日志", "Logs");
        HomeSloganTextBlock.Text = T(HomeSlogans[0].Zh, HomeSlogans[0].En);

        LaunchActionTextBlock.Text = T("启动服务器", "Start Server");
        LaunchActionIconPath.Data = Geometry.Parse(LaunchStartIconData);
        CommandTextBox.PlaceholderText = T("输入服务器命令，回车发送", "Enter server command, press Enter to send");
        QuickCommandComboBox.PlaceholderText = T("快捷命令", "Quick command");
        SendCommandButton.Content = T("发送", "Send");

        DashboardPlayersTitleText.Text = T("在线玩家", "Online Players");
        DashboardPlayersHintText.Text = T("玩家名称", "Player Name");
        DashboardPlayersServerHeaderText.Text = T("服务器", "Server");
        DashboardPlayersLatencyHeaderText.Text = T("延迟", "Latency");
        DashboardPlayersJoinedHeaderText.Text = T("加入时间", "Joined");
        DashboardServerLineLegendText.Text = T("服务器", "Server");
        DashboardRobotLineLegendText.Text = T("QQ机器人", "QQ Robot");
        DashboardUptimeTitleText.Text = T("运行时间", "Uptime");

        ProfilesTabButton.Content = T("实例", "Instance");
        ConfigTabButton.Content = T("配置", "Config");
        SavesTabButton.Content = T("存档", "Saves");
        AutomationTabButton.Content = T("自动化", "Automation");
        ModsTabButton.Content = T("模组", "Mods");
        DownloadVersionsTabButton.Content = T("下载版本", "Downloads");
        DownloadVersionsNavButton.Content = T("下载版本", "Downloads");
        ProfileNameTextBox.PlaceholderText = T("档案名称", "Profile name");
        CreateProfileButton.Content = T("创建", "Create");
        ImportProfileButton.Content = T("导入", "Import");
        DeleteProfileButton.Content = T("删除", "Delete");
        RefreshProfilesButton.Content = T("刷新", "Refresh");
        NewSaveNameTextBox.PlaceholderText = T("新存档名称", "New save name");
        CreateSaveButton.Content = T("创建存档", "Create Save");
        ImportSaveButton.Content = T("导入", "Import");
        DeleteSaveButton.Content = T("删除", "Delete");
        RefreshSavesButton.Content = T("刷新", "Refresh");
        InitializeAutomationStaticTexts();
        InitializeModStaticTexts();
        DownloadVersionSearchTextBox.PlaceholderText = T("搜索版本号", "Search version");
        ImportServerPackageButton.Content = T("导入", "Import");
        RefreshDownloadVersionsButton.Content = T("刷新", "Refresh");
        InitializeConfigStaticTexts();

        ServerSettingsTabButton.Content = T("服务器设置", "Server");
        AppearanceSettingsTabButton.Content = T("外观", "Appearance");
        NetworkSettingsTabButton.Content = T("网络", "Network");
        AdvancedSettingsTabButton.Content = T("高级", "Advanced");
        AboutSettingsTabButton.Content = T("关于", "About");
        SponsorsSettingsTabButton.Content = T("赞助者", "Sponsors");
        ContributorsSettingsTabButton.Content = T("贡献者", "Contributors");
        SettingsLanguageLabelTextBlock.Text = T("语言", "Language");
        SettingsThemeLabelTextBlock.Text = T("主题", "Theme");
        InitializeServerSettingsStaticTexts();
        InitializeNetworkSettingsStaticTexts();
        InitializeAdvancedSettingsStaticTexts();
        InitializeAboutSettingsStaticTexts();
        InitializeSponsorSettingsStaticTexts();
        InitializeContributorSettingsStaticTexts();
        InitializeConnectionStaticTexts();

        Title = T("LauncherGo 主窗口", "LauncherGo Main Window");
        ToolTip.SetTip(RepositoryButton, T("仓库", "Repository"));
        ToolTip.SetTip(FeedbackButton, T("反馈", "Feedback"));
        ToolTip.SetTip(SponsorButton, T("赞助", "Sponsor"));
        ToolTip.SetTip(MinimizeButton, T("最小化", "Minimize"));
        UpdateMaximizeButton();
        ToolTip.SetTip(CloseButton, T("关闭", "Close"));
        LaunchAddProfileComboBox.PlaceholderText = T("添加服务器", "Add server");
        SettingsServerSaveButton.Content = T("保存", "Save");
        SettingsServerRefreshButton.Content = T("刷新", "Refresh");
        ConnectionFrpSaveButton.Content = T("保存", "Save");
        ConnectionFrpRefreshButton.Content = T("刷新", "Refresh");
        RobotSaveButton.Content = T("保存", "Save");
        ApplyStaticUiTranslations();
    }

    private void ApplyStaticUiTranslations()
    {
        foreach (var visual in this.GetVisualDescendants())
        {
            switch (visual)
            {
                case TextBlock textBlock when IsStaticTextBlock(textBlock):
                    textBlock.Text = TranslateStaticUiText(textBlock.Text);
                    break;
                case Button button when button.Content is string content:
                    button.Content = TranslateStaticUiText(content);
                    break;
                case TextBox textBox:
                    textBox.PlaceholderText = TranslateStaticUiText(textBox.PlaceholderText);
                    break;
                case ComboBox comboBox:
                    comboBox.PlaceholderText = TranslateStaticUiText(comboBox.PlaceholderText);
                    break;
                case ComboBoxItem item when item.Content is string content:
                    item.Content = TranslateStaticUiText(content);
                    break;
            }
        }
    }

    private void RequestStaticUiTranslations()
    {
        // ItemsControl templates are realized after a tab becomes visible. Apply once
        // immediately and once before the next render so their default text never lingers.
        ApplyStaticUiTranslations();
        QueueStaticUiTranslations();
    }

    private void QueueStaticUiTranslations()
    {
        if (_staticUiTranslationQueued)
        {
            return;
        }

        _staticUiTranslationQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _staticUiTranslationQueued = false;
            ApplyStaticUiTranslations();
        }, DispatcherPriority.Render);
    }

    private static bool IsStaticTextBlock(TextBlock textBlock)
    {
        return textBlock.Classes.Contains("SidebarSectionTitle") ||
               textBlock.Classes.Contains("DashboardLabel") ||
               textBlock.Classes.Contains("TableHeaderText") ||
               textBlock.Classes.Contains("ConfigSectionTitle") ||
               textBlock.Classes.Contains("ConfigFieldLabel") ||
               textBlock.Classes.Contains("ConfigHintText");
    }

    private string TranslateStaticUiText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value ?? string.Empty;
        }

        foreach (var (zh, en) in StaticUiTranslations)
        {
            if (value.Equals(zh, StringComparison.Ordinal) ||
                value.Equals(en, StringComparison.Ordinal))
            {
                return _localizationService.Resolve(zh, en);
            }
        }

        return value;
    }

    private void InitializeAutomationStaticTexts()
    {
        AutomationSaveButton.Content = T("保存", "Save");
        AutomationRefreshButton.Content = T("刷新", "Refresh");
        AutomationListRefreshButton.Content = T("刷新", "Refresh");
        AutomationClearButton.Content = T("清空", "Clear");
        AutomationBackButton.Content = T("返回", "Back");
        AutomationRestartEnabledLabelTextBlock.Text = T("启用定时开关服", "Enable scheduled start/stop");
        AutomationBackupEnabledLabelTextBlock.Text = T("启用定时备份", "Enable scheduled backup");
        AutomationBackupBeforeShutdownLabelTextBlock.Text = T("关服前备份", "Backup before shutdown");
        AutomationBroadcastEnabledLabelTextBlock.Text = T("启用定时广播", "Enable scheduled broadcast");
        AutomationCommandEnabledLabelTextBlock.Text = T("启用定时命令", "Enable scheduled commands");
        AutomationExportEnabledLabelTextBlock.Text = T("启用日志导出", "Enable log export");
        AutomationExportBeforeShutdownLabelTextBlock.Text = T("关服前导出日志", "Export before shutdown");
        AutomationExportIncludeChatLabelTextBlock.Text = T("导出聊天", "Export chat");
        AutomationExportIncludeServerLabelTextBlock.Text = T("导出服务端信息", "Export server info");
        AutomationClearCacheBeforeStartLabelTextBlock.Text = T("开服前清理缓存", "Clear cache before start");
        AutomationScriptsEnabledLabelTextBlock.Text = T("启用自动化脚本", "Enable automation scripts");
        AutomationScriptsTitleTextBlock.Text = T("自动化脚本", "Automation scripts");
        AutomationAddScriptButton.Content = T("添加", "Add");
        AutomationActionTitleTextBlock.Text = T("定时开关服", "Scheduled Start/Stop");
        AutomationAddActionButton.Content = T("添加", "Add");
        AutomationAddBackupScheduleButton.Content = T("添加", "Add");
        AutomationBackupRetentionLabelTextBlock.Text = T("保留备份数", "Backup retention");
        AutomationBackupRetentionHintTextBlock.Text = T(
            "0 表示不限制；原始文件和 ZSTD 压缩文件按一份计算",
            "0 means unlimited; source and ZSTD files count as one backup");
        AutomationAddExportTimeButton.Content = T("添加", "Add");
        AutomationAddBroadcastButton.Content = T("添加", "Add");
        AutomationAddCommandButton.Content = T("添加", "Add");
        foreach (var item in _automationActionWindowItems)
        {
            item.SetLanguage(_isChinese);
        }
        foreach (var item in _automationBackupScheduleItems)
        {
            item.SetLanguage(_isChinese);
        }
        foreach (var item in _automationScriptItems)
        {
            item.SetLanguage(_isChinese);
        }
    }

    private void InitializeModStaticTexts()
    {
        ModZipPathTextBox.PlaceholderText = T("Mod ZIP", "Mod ZIP");
        BrowseModZipButton.Content = T("浏览", "Browse");
        ToolTip.SetTip(ModSelectAllCheckBox, T("全选/取消全选模组", "Select or clear all mods"));
        ImportModZipButton.Content = T("导入", "Import");
        DeleteSelectedModsButton.Content = T("删除", "Delete");
        RefreshModsButton.Content = T("刷新", "Refresh");
        ExportModsButton.Content = T("导出", "Export");
        UpdateCheckModButtonText();
        ModNameHeaderTextBlock.Text = T("名称", "Name");
        ModSideHeaderTextBlock.Text = T("端", "Side");
        ModEditConfigHeaderTextBlock.Text = T("编辑配置", "Edit Config");
        ModUpdateHeaderTextBlock.Text = T("更新", "Update");
        foreach (var item in _modItems)
        {
            item.SetLanguage(_isChinese);
        }
    }

    private void InitializeConfigStaticTexts()
    {
        ConfigBackButton.Content = T("返回", "Back");
        ConfigRefreshButton.Content = T("刷新", "Refresh");
        ConfigImportButton.Content = T("导入", "Import");
        ConfigSaveButton.Content = T("保存", "Save");
        ConfigBasicInfoTitleTextBlock.Text = T("基础信息", "Basic Info");
        ConfigServerNameLabelTextBlock.Text = T("服务器名称", "Server Name");
        ConfigServerLanguageLabelTextBlock.Text = T("服务器语言", "Server Language");
        ConfigDefaultRoleCodeLabelTextBlock.Text = T("默认角色代码", "Default Role Code");
        ConfigServerDescriptionLabelTextBlock.Text = T("服务器描述", "Server Description");
        ConfigWelcomeMessageLabelTextBlock.Text = T("进服提示", "Welcome Message");
        ConfigNetworkTitleTextBlock.Text = T("网络与公开", "Network & Listing");
        ConfigIpLabelTextBlock.Text = T("IP", "IP");
        ConfigPortLabelTextBlock.Text = T("端口", "Port");
        ConfigMaxClientsLabelTextBlock.Text = T("最大玩家数", "Max Players");
        ConfigMaxClientsInQueueLabelTextBlock.Text = T("排队人数上限", "Queue Limit");
        ConfigServerUrlLabelTextBlock.Text = T("服务器网址", "Server URL");
        ConfigAdvertiseServerToggleLabelTextBlock.Text = T("公开到服务器列表", "List on Public Server Browser");
        ConfigUpnpToggleLabelTextBlock.Text = T("启用 UPnP 自动端口映射", "Enable UPnP Port Mapping");
        ConfigSecurityTitleTextBlock.Text = T("安全与维护", "Security & Maintenance");
        ConfigPasswordLabelTextBlock.Text = T("加入密码", "Join Password");
        ConfigPasswordHintTextBlock.Text = T("留空表示不设置密码。", "Leave empty to disable password.");
        ConfigWhitelistModeLabelTextBlock.Text = T("白名单模式", "Whitelist Mode");
        ConfigWarnAfkSecondsLabelTextBlock.Text = T("AFK 警告秒数", "AFK Warning Seconds");
        ConfigKickAfkSecondsLabelTextBlock.Text = T("AFK 踢出秒数", "AFK Kick Seconds");
        ConfigClientConnectionTimeoutLabelTextBlock.Text = T("连接超时秒数", "Connection Timeout Seconds");
        ConfigMaxChunkRadiusLabelTextBlock.Text = T("最大区块视距半径", "Max Chunk View Radius");
        ConfigDieBelowDiskSpaceMbLabelTextBlock.Text = T("低于磁盘空间时关闭（MB）", "Shutdown Below Disk Space (MB)");
        ConfigVerifyPlayerAuthToggleLabelTextBlock.Text = T("启用官方账号验证", "Enable Official Auth");
        ConfigAllowPvPToggleLabelTextBlock.Text = T("允许PvP", "Allow PvP");
        ConfigAllowFireSpreadToggleLabelTextBlock.Text = T("允许火势蔓延", "Allow Fire Spread");
        ConfigAllowFallingBlocksToggleLabelTextBlock.Text = T("允许方块掉落", "Allow Falling Blocks");
        ConfigPassTimeWhenEmptyToggleLabelTextBlock.Text = T("无人在线时继续流逝时间", "Pass Time When Empty");
        ConfigCorruptionProtectionToggleLabelTextBlock.Text = T("启用存档损坏保护", "Enable Corruption Protection");
        ConfigRegenerateCorruptChunksToggleLabelTextBlock.Text = T("重新生成损坏区块", "Regenerate Corrupt Chunks");
        ConfigStartupCommandsLabelTextBlock.Text = T("启动后执行命令", "Startup Commands");
        ConfigWorldTitleTextBlock.Text = T("世界", "World");
        ConfigSeedLabelTextBlock.Text = T("种子", "Seed");
        ConfigWorldNameLabelTextBlock.Text = T("世界名称", "World Name");
        ConfigSaveFileLabelTextBlock.Text = T("存档文件", "Save File");
        ConfigPlayStyleLabelTextBlock.Text = T("游玩风格", "Play Style");
        ConfigWorldTypeLabelTextBlock.Text = T("世界类型", "World Type");
        ConfigWorldHeightLabelTextBlock.Text = T("世界高度", "World Height");
        ConfigWorldGeneratedNoticeTextBlock.Text = T(
            "当前存档已生成世界：种子、游玩风格、世界类型、世界高度，以及仅限建档阶段的世界规则（如世界宽度/长度）已锁定。",
            "This save already has a generated world: seed, play style, world type, world height, and world-creation-only rules are locked.");
        ConfigWorldRulesTitleTextBlock.Text = T("世界规则", "World Rules");
        ConfigNoProfileTextBlock.Text = T("暂无档案，请先创建档案。", "No profile found. Create a profile first.");
        RebuildConfigChoiceOptions();
        RefreshConfigWorldRuleLabels();
    }

    private void InitializeServerSettingsStaticTexts()
    {
        SettingsServerDirectoryTitleTextBlock.Text = T("目录路径", "Directory Path");
        SettingsWorkspaceDirectoryLabelTextBlock.Text = T("工作目录", "Workspace");
        SettingsBrowseWorkspaceDirectoryButton.Content = T("浏览", "Browse");
        SettingsSaveCompressionTitleTextBlock.Text = T("存档压缩", "Save Compression");
        SettingsSaveCompressionEnabledLabelTextBlock.Text = T("启用存档压缩", "Enable save compression");
        SettingsSaveCompressionLevelLabelTextBlock.Text = T("压缩等级", "Compression level");
        SettingsSaveCompressionPathLabelTextBlock.Text = T("压缩路径", "Compression path");
        SettingsBrowseSaveCompressionPathButton.Content = T("浏览", "Browse");
        SettingsSaveCompressionUpdateModeLabelTextBlock.Text = T("更新方式", "Update mode");
        SettingsSaveCompressionDeleteSourceLabelTextBlock.Text = T("压缩后删除原始文件", "Delete source after compression");
        SettingsSaveCompressionHintTextBlock.Text = T(
            "仅处理备份产生的 .vcdbs；活动存档仍保留原始格式。更新并添加会跳过目标中更新的文件，添加并替换会始终覆盖目标。",
            "Only backup .vcdbs files are compressed; the active save remains in its native format. Update and add skips targets that are newer, while add and replace always overwrites the target.");
        SettingsCompressExistingBackupsButton.Content = T("立即压缩已有备份", "Compress existing backups");
        RebuildSaveCompressionUpdateModeOptions();
        SettingsQuickCommandsTitleTextBlock.Text = T("快捷命令", "Quick Commands");
        SettingsConsoleLogFiltersTitleTextBlock.Text = T("控制台日志过滤", "Console Log Filters");
        SettingsConsoleLogFiltersSearchTextBox.PlaceholderText = T("搜索过滤规则", "Search filters");
        SettingsConsoleLogFiltersHintTextBlock.Text = T(
            "默认过滤规则始终生效；启用下方规则后，匹配的日志不会显示在控制台。",
            "Built-in filters always remain active. Enabled rules below hide matching lines from the console.");
        SettingsAddConsoleLogFilterButton.Content = T("添加", "Add");
        SettingsConsoleLogFiltersEnabledHeaderTextBlock.Text = T("启用", "Enabled");
        SettingsConsoleLogFiltersModeHeaderTextBlock.Text = T("匹配模式", "Match mode");
        SettingsConsoleLogFiltersPatternHeaderTextBlock.Text = T("匹配内容", "Match pattern");
        SettingsConsoleLogFiltersActionsHeaderTextBlock.Text = T("操作", "Actions");
        foreach (var item in _consoleLogFilterRuleItems)
        {
            item.SetLanguage(_isChinese, T);
        }
        SettingsServerAutomationTitleTextBlock.Text = T("启动与托盘", "Startup & Tray");
        SettingsStartWithWindowsLabelTextBlock.Text = T("开机启动启动器", "Start launcher with Windows");
        SettingsCloseToTrayLabelTextBlock.Text = T("关闭时隐藏到托盘，不直接退出", "Hide to tray on close instead of exiting");
        SettingsStartHiddenLabelTextBlock.Text = T("启动时隐藏到托盘", "Start hidden to tray");
        SettingsAutoStartServerLabelTextBlock.Text = T("启动时自动启动服务器", "Auto-start server on launch");
        SettingsAutoRestartServerAfterCrashLabelTextBlock.Text = T(
            "服务端异常退出后自动重启",
            "Restart server after an unexpected exit");
        SettingsAutoStartServerProfileLabelTextBlock.Text = T("自启动服务器档案", "Auto-start server profile");
        SettingsAutoStartAddProfileComboBox.PlaceholderText = T("添加自启动服务器", "Add auto-start server");
        SettingsAutoStartRobotLabelTextBlock.Text = T("启动时自动启动QQ机器人", "Auto-start QQ robot on launch");
        SettingsAutoStartDiscordLabelTextBlock.Text = T("启动时自动启动 Discord 机器人", "Auto-start Discord bot on launch");
        SettingsAutoStartFrpLabelTextBlock.Text = T("启动时自动开启内网穿透（常规）", "Auto-start FRP (regular) on launch");
        SettingsAutoStartThirdPartyFrpcLabelTextBlock.Text = T("启动时自动开启第三方内网穿透", "Auto-start third-party FRPC on launch");
        SettingsAutoStartEasyTierLabelTextBlock.Text = T("启动时自动开启 EasyTier", "Auto-start EasyTier on launch");
        SettingsAutoStartGatewayLabelTextBlock.Text = T("启动时自动启动网关", "Auto-start Gateway on launch");
    }

    private void InitializeNetworkSettingsStaticTexts()
    {
        SettingsNetworkDownloadTitleTextBlock.Text = T("下载网络", "Download Network");
        SettingsThirdPartyServerLabelTextBlock.Text = T("游戏服务端", "Game Server");
        SettingsUpdateTitleTextBlock.Text = T("LauncherGo 更新", "LauncherGo Updates");
        SettingsGitHubProxyLabelTextBlock.Text = T("GitHub 代理", "GitHub Proxy");
        SettingsAutoCheckUpdatesLabelTextBlock.Text = T("启动时自动检查", "Check on startup");
        SettingsChunkedDownloadsTitleTextBlock.Text = T("多线程分片", "Multithreaded chunking");
        SettingsChunkedDownloadLabelTextBlock.Text = T("启用", "Enabled");
        SettingsDownloadChunkCountLabelTextBlock.Text = T("分片数量", "Chunk count");
        SettingsDownloadThreadCountLabelTextBlock.Text = T("下载线程数", "Download threads");
        SettingsCheckUpdatesButton.Content = T("检查更新（含预发布）", "Check updates (including prereleases)");
        EnsureGitHubProxyOptions();
    }

    private void InitializeAdvancedSettingsStaticTexts()
    {
        SettingsAdvancedActionsTitleTextBlock.Text = T("维护", "Maintenance");
        SettingsOpenLogButton.Content = T("打开软件日志", "Open App Logs");
        SettingsResetAllButton.Content = T("重置所有设置", "Reset All Settings");
        SettingsClearDownloadCacheButton.Content = T("清空下载缓存", "Clear Download Cache");
    }

    private void InitializeAboutSettingsStaticTexts()
    {
        AboutVersionTextBlock.Text = T(
            $"版本 {_launcherUpdateService.CurrentVersion}",
            $"Version {_launcherUpdateService.CurrentVersion}");
        AboutPackageKindTextBlock.Text = T(
            $"安装类型：{GetPackageKindDisplayName(_launcherUpdateService.PackageKind)}",
            $"Package: {GetPackageKindDisplayName(_launcherUpdateService.PackageKind)}");
        AboutCopyrightTextBlock.Text = "Copyright (C) 2026 HansJack, LauncherGo project owner (VSCN-Studio team)";
        AboutLicenseTextBlock.Text = T("许可证：MIT", "License: MIT");
        AboutRepositoryButton.Content = T("源码仓库", "Source Repository");
        AboutLicenseButton.Content = T("MIT 许可证", "MIT License");
        AboutNoticeButton.Content = T("版权声明", "Copyright Notice");
        AboutThirdPartyButton.Content = T("第三方声明", "Third-Party Notices");
        AboutActionStatusTextBlock.IsVisible = false;
        SetAboutFallbackText(T("正在加载项目介绍 ...", "Loading project introduction ..."));
    }

    private string GetPackageKindDisplayName(LauncherPackageKind packageKind)
    {
        return packageKind switch
        {
            LauncherPackageKind.Installer => T("安装版", "Installer"),
            LauncherPackageKind.SmallInstaller => T("精简安装版", "Small Installer"),
            LauncherPackageKind.Portable => T("便携版", "Portable"),
            LauncherPackageKind.SmallPackage => T("精简包", "Small Package"),
            _ => T("本地构建", "Local Build")
        };
    }

    private void InitializeSponsorSettingsStaticTexts()
    {
    }

    private void InitializeContributorSettingsStaticTexts()
    {
    }

    private void InitializeConnectionStaticTexts()
    {
        ConfigureGatewayRoutingStateDisplay();
        ConnectionFrpTabButton.Content = T("FRP", "FRP");
        ConnectionEasyTierTabButton.Content = T("EasyTier", "EasyTier");
        ConnectionRobotTabButton.Content = "OneBot";
        ConnectionDiscordTabButton.Content = "Discord";
        DiscordSaveButton.Content = T("保存", "Save");
        DiscordToggleButton.Content = T("启动", "Start");
        DiscordRedeployButton.Content = T("重新部署命令", "Redeploy Commands");
        DiscordClearButton.Content = T("清空", "Clear");
        DiscordRefreshButton.Content = T("刷新", "Refresh");
        DiscordBindingAddButton.Content = T("添加", "Add");
        DiscordCustomCommandAddButton.Content = T("添加", "Add");
        DiscordConfigTitleTextBlock.Text = T("Discord 机器人配置", "Discord Bot Configuration");
        DiscordTokenLabelTextBlock.Text = T("Bot Token", "Bot Token");
        DiscordReconnectLabelTextBlock.Text = T("重连间隔秒数", "Reconnect Interval Seconds");
        DiscordAdminUsersLabelTextBlock.Text = T("管理员用户 ID", "Administrator User IDs");
        DiscordAdminRolesLabelTextBlock.Text = T("管理员角色 ID", "Administrator Role IDs");
        DiscordBindingTitleTextBlock.Text = T("Profile / Guild / Channel 绑定", "Profile / Guild / Channel Bindings");
        DiscordBindingProfileHeaderTextBlock.Text = T("服务器档案", "Server Profile");
        DiscordBindingGuildHeaderTextBlock.Text = "Guild ID";
        DiscordBindingChannelHeaderTextBlock.Text = "Channel ID";
        DiscordBindingActionHeaderTextBlock.Text = T("操作", "Actions");
        DiscordCustomCommandsTitleTextBlock.Text = T("自定义指令", "Custom Commands");
        DiscordCustomCommandNameHeaderTextBlock.Text = T("指令", "Command");
        DiscordCustomCommandTypeHeaderTextBlock.Text = T("类型", "Type");
        DiscordCustomCommandContentHeaderTextBlock.Text = T("内容", "Content");
        DiscordCustomCommandActionHeaderTextBlock.Text = T("操作", "Actions");
        DiscordCustomCommandHintTextBlock.Text = T(
            "指令名称仅允许 Discord 支持的 a-z、0-9、_、-；非法名称会提示并阻止保存。",
            "Command names may contain only Discord-supported a-z, 0-9, _ and -; invalid names block saving.");
        foreach (var item in _discordCustomCommandItems) item.SetLanguage(_isChinese);
        ConnectionAuthTabButton.Content = T("认证", "Authentication");
        ConnectionGatewayTabButton.Content = T("网关", "Gateway");

        GatewaySaveButton.Content = T("保存", "Save");
        GatewayRefreshButton.Content = T("刷新", "Refresh");
        GatewayAddBackendButton.Content = T("添加", "Add");
        GatewayConfigTitleTextBlock.Text = T("TCP 网关", "TCP Gateway");
        GatewayListenHostLabelTextBlock.Text = T("监听地址", "Listen Address");
        GatewayListenPortLabelTextBlock.Text = T("监听端口", "Listen Port");
        GatewayMaxConnectionsLabelTextBlock.Text = T("最大连接数", "Max Connections");
        GatewayMaxConnectionsPerIpLabelTextBlock.Text = T("单 IP 连接上限", "Per-IP Connection Limit");
        GatewayConnectTimeoutLabelTextBlock.Text = T("后端连接超时秒数", "Backend Connect Timeout Seconds");
        GatewayHealthCheckIntervalLabelTextBlock.Text = T("健康检查间隔秒数", "Health Check Interval Seconds");
        GatewayAllowListLabelTextBlock.Text = T("IP 白名单", "IP Allow List");
        GatewayBlockListLabelTextBlock.Text = T("IP 黑名单", "IP Block List");
        GatewayIpRulesHintTextBlock.Text = T(
            "每行一个 IP 或 CIDR 网段；黑名单优先。",
            "One IP or CIDR range per line; block list takes priority.");
        GatewayBackendsTitleTextBlock.Text = T("后端服务器", "Backend Servers");
        GatewayRuntimeTitleTextBlock.Text = T("路由与重定向", "Routing & Redirect");
        GatewayDeployRedirectModButton.Content = T("部署重定向模组", "Deploy Redirect Mod");
        GatewayRoutingHistoryButton.Content = T("历史", "History");
        GatewayBackendStatisticsHeaderTextBlock.Text = T("操作", "Actions");
        UpdateGatewayToggleButtonText();

        ConnectionFrpImportButton.Content = T("导入frpc", "Import frpc");
        ConnectionThirdPartyFrpcImportButton.Content = T("导入第三方frpc", "Import third-party frpc");
        ConnectionFrpEditTomlButton.Content = T("编辑常规TOML", "Edit Regular TOML");
        ConnectionThirdPartyFrpcEditTomlButton.Content = T("编辑第三方TOML", "Edit Third-party TOML");
        UpdateConnectionFrpActionButtons();
        ConnectionFrpTitleTextBlock.Text = T("内网穿透配置", "FRP Configuration");
        ConnectionFrpCommandLabelTextBlock.Text = T("常规启动命令", "Regular Launch Command");
        ConnectionThirdPartyFrpcModeLabelTextBlock.Text = T("第三方模式", "Third-party Mode");
        ConnectionThirdPartyFrpcCommandLabelTextBlock.Text = T("第三方启动命令", "Third-party Launch Command");

        EasyTierSaveButton.Content = T("保存", "Save");
        EasyTierRefreshButton.Content = T("刷新", "Refresh");
        EasyTierImportCoreButton.Content = T("导入 Core", "Import Core");
        EasyTierImportCliButton.Content = T("导入 CLI", "Import CLI");
        EasyTierCopyRoomCodeButton.Content = T("复制", "Copy");
        EasyTierCopyGameAddressButton.Content = T("复制", "Copy");
        EasyTierRoomPrefixLabelTextBlock.Text = T("房间前缀", "Room Prefix");
        EasyTierGamePortLabelTextBlock.Text = T("游戏端口", "Game Port");
        EasyTierPeerNodesLabelTextBlock.Text = T("引导/中继节点（支持 JSON 订阅链接）", "Bootstrap / Relay Nodes (JSON subscriptions supported)");
        EasyTierNetworkNameLabelTextBlock.Text = T("自定义网络名称", "Custom Network Name");
        EasyTierNetworkSecretLabelTextBlock.Text = T("自定义网络密钥", "Custom Network Secret");
        EasyTierUdpLabelTextBlock.Text = T("UDP 游戏端口", "UDP game port");
        EasyTierLatencyFirstLabelTextBlock.Text = T("低延迟优先", "Latency First");
        EasyTierCompressionLabelTextBlock.Text = T("Zstd 压缩", "Zstd Compression");
        EasyTierKcpLabelTextBlock.Text = T("KCP 代理", "KCP Proxy");
        EasyTierRoomCodeLabelTextBlock.Text = T("MVL 分享码", "MVL Room Code");
        EasyTierGameAddressLabelTextBlock.Text = T("ET 游戏地址", "ET Game Address");
        UpdateEasyTierActionButtons();


        UpdateRobotToggleButtonText();
        DiscordToggleButton.Content = _discordBotService.GetCurrentStatus().IsRunning ? T("停止", "Stop") : T("启动", "Start");
        RobotConfigTitleTextBlock.Text = T("QQ机器人配置", "QQ Robot Configuration");
        RobotOneBotLabelTextBlock.Text = T("OneBot WebSocket", "OneBot WebSocket");
        RobotAccessTokenLabelTextBlock.Text = T("访问令牌", "Access Token");
        RobotBoundGroupsLabelTextBlock.Text = T("绑定群号", "Bound Group IDs");
        RobotReconnectLabelTextBlock.Text = T("重连间隔秒数", "Reconnect Interval Seconds");
        RobotDatabasePathLabelTextBlock.Text = T("数据库路径", "Database Path");
        RobotDefaultEncodingLabelTextBlock.Text = T("默认编码", "Default Encoding");
        RobotFallbackEncodingLabelTextBlock.Text = T("回退编码", "Fallback Encoding");
        RobotSuperUsersLabelTextBlock.Text = T("超级管理员 QQ", "Super Admin QQ IDs");
        RobotTeleportPointsTitleTextBlock.Text = T("传送设置点", "Teleport Points");
        RobotTeleportPointNameHeaderTextBlock.Text = T("设置点名称", "Point Name");
        RobotTeleportPointAddButton.Content = T("添加", "Add");
        RobotCustomCommandsTitleTextBlock.Text = T("自定义指令", "Custom Commands");
        RobotCustomCommandNameHeaderTextBlock.Text = T("指令", "Command");
        RobotCustomCommandTypeHeaderTextBlock.Text = T("类型", "Type");
        RobotCustomCommandContentHeaderTextBlock.Text = T("内容", "Content");
        RobotCustomCommandActionHeaderTextBlock.Text = T("操作", "Action");
        RobotCustomCommandHintTextBlock.Text = T(
            "指令以 / 开头；不能使用或包含 /help、/send、/server 等内置指令前缀。文本支持换行，图片请使用按钮选择文件路径。",
            "Commands start with /. Built-in prefixes such as /help, /send, and /server are reserved. Text supports new lines; choose an image file path with the button.");
        RobotCustomCommandAddButton.Content = T("添加", "Add");
        foreach (var item in _robotCustomCommandItems)
        {
            item.SetLanguage(_isChinese);
        }
        RobotBridgeSourceHintTextBlock.Text = T(
            "服务器桥接来源由“服务器桥接”页面统一接收，机器人不再单独监听端口。",
            "Server Bridge source is received by Server Bridge; the robot does not listen on its own port.");
        RobotClearButton.Content = T("清空", "Clear");
        RobotRefreshButton.Content = T("刷新", "Refresh");
        RobotBindingAddButton.Content = T("添加", "Add");
        AuthSaveButton.Content = T("保存", "Save");
        AuthRefreshButton.Content = T("刷新", "Refresh");
        AuthClearButton.Content = T("清空", "Clear");
        AuthBackButton.Content = T("返回", "Back");
        AuthDeployButton.Content = T("部署认证模组", "Deploy Auth Mod");
        AuthEnabledLabelTextBlock.Text = T("启用认证", "Enable Auth");
        AuthLoginTimeoutLabelTextBlock.Text = T("登录超时秒数", "Login Timeout Seconds");
        AuthRememberSessionLabelTextBlock.Text = T("会话记住分钟", "Remember Session Minutes");
        AuthDiscourseEnabledLabelTextBlock.Text = T("启用 Discourse 登录", "Enable Discourse Login");
        AuthDiscourseBaseUrlLabelTextBlock.Text = T("Discourse 地址", "Discourse URL");
        AuthDiscourseSecretLabelTextBlock.Text = T("共享密钥", "Shared Secret");
        AuthDiscoursePublicCallbackLabelTextBlock.Text = T("公开回调地址", "Public Callback URL");
        AuthDiscourseListenPrefixLabelTextBlock.Text = T("本地监听地址", "Local Listen Prefix");
        AuthOAuth2EnabledLabelTextBlock.Text = T("启用 OAuth2/OIDC 登录", "Enable OAuth2/OIDC Login");
        AuthOAuth2DiscoveryUrlLabelTextBlock.Text = T("Discovery 地址（可选）", "Discovery URL (Optional)");
        AuthOAuth2AuthorizationEndpointLabelTextBlock.Text = T("授权端点", "Authorization Endpoint");
        AuthOAuth2TokenEndpointLabelTextBlock.Text = T("Token 端点", "Token Endpoint");
        AuthOAuth2UserInfoEndpointLabelTextBlock.Text = T("UserInfo 端点", "UserInfo Endpoint");
        AuthOAuth2ClientIdLabelTextBlock.Text = T("Client ID", "Client ID");
        AuthOAuth2ClientSecretLabelTextBlock.Text = T("Client Secret", "Client Secret");
        AuthOAuth2ScopeLabelTextBlock.Text = T("Scope", "Scope");
        AuthOAuth2PublicCallbackLabelTextBlock.Text = T("公开回调地址", "Public Callback URL");
        AuthOAuth2ListenPrefixLabelTextBlock.Text = T("本地监听地址", "Local Listen Prefix");
        AuthOAuth2UserIdClaimLabelTextBlock.Text = T("用户 ID claim", "User ID Claim");
        AuthOAuth2UsernameClaimLabelTextBlock.Text = T("用户名 claim", "Username Claim");
        AuthOAuth2DisplayNameClaimLabelTextBlock.Text = T("显示名 claim", "Display Name Claim");
        AuthOAuth2EmailClaimLabelTextBlock.Text = T("邮箱 claim", "Email Claim");
        AuthPlayersTitleTextBlock.Text = T("玩家认证数据", "Player Auth Data");
        AuthExternalAccountHeaderTextBlock.Text = T("外部账号", "External Account");
        AuthRefreshPlayersButton.Content = T("刷新玩家", "Refresh Players");
        AuthPlayerSearchTextBox.PlaceholderText = T("搜索玩家名、UID 或外部账号", "Search player name, UID, or external account");
        ServerBridgeTabButton.Content = T("服务器桥接", "Server Bridge");
        ServerBridgeSaveButton.Content = T("保存", "Save");
        ServerBridgeRefreshButton.Content = T("刷新", "Refresh");
        ServerBridgeClearButton.Content = T("清空", "Clear");
        ServerBridgeBackButton.Content = T("返回", "Back");
        ServerBridgeDeployButton.Content = T("部署服务器桥接模组", "Deploy Server Bridge");
        ServerBridgeTestButton.Content = T("测试连接", "Test Connection");
        ServerBridgeRegenerateTokenButton.Content = T("轮换令牌", "Rotate Token");
        ServerBridgeEnabledLabelTextBlock.Text = T("启用服务器桥接", "Enable Server Bridge");
        ServerBridgePortLabelTextBlock.Text = T("本机端口", "Local Port");
        ServerBridgeTimeoutLabelTextBlock.Text = T("查询超时毫秒", "Query Timeout ms");
        ServerBridgeMaxLengthLabelTextBlock.Text = T("最大命令长度", "Max Command Length");
        ServerBridgeFallbackLabelTextBlock.Text = T("桥接不可用时回退 Relay", "Fallback to Relay when bridge is unavailable");
        ServerBridgeTokenLabelTextBlock.Text = T("访问令牌", "Access Token");
        RebuildThirdPartyFrpcModeOptions();
    }

    private void InitializeSeries()
    {
        FillWithZero(_serverCpuSamples, RealtimeRangeSeconds);
        FillWithZero(_serverMemoryMbSamples, RealtimeRangeSeconds);
        FillWithZero(_robotCpuSamples, RealtimeRangeSeconds);
        FillWithZero(_robotMemoryMbSamples, RealtimeRangeSeconds);
        FillWithZero(_playersSamples, RealtimeRangeSeconds);
        FillWithZero(_networkLatencySamples, NetworkRangeCount);

        EventTickerCurrentText.Text = T("暂无玩家事件", "No player events");
        EventTickerNextText.Text = EventTickerCurrentText.Text;
        UpdateCardValues(_serverProcessService.GetCachedStatus());
    }

    private void InitializeCollections()
    {
        ConsoleOutputTextBlock.Text = string.Empty;
        _consoleAutoScroll = true;
        RefreshQuickCommandItems(_preferencesService.Load().QuickCommands);
        ProfilesListBox.ItemsSource = _profileItems;
        SavesListBox.ItemsSource = _saveItems;
        DownloadVersionsListBox.ItemsSource = _downloadVersionItems;
        ConfigServerLanguageComboBox.ItemsSource = ConfigServerLanguageOptions;
        ConfigWhitelistModeComboBox.ItemsSource = _configWhitelistModeOptions;
        ConfigDefaultRoleComboBox.ItemsSource = _configDefaultRoleOptions;
        ConfigPlayStyleComboBox.ItemsSource = _configPlayStyleOptions;
        ConfigWorldTypeComboBox.ItemsSource = _configWorldTypeOptions;
        ConfigSaveFileComboBox.ItemsSource = _configSaveItems;
        ConfigWorldRulesItemsControl.ItemsSource = _configWorldRuleItems;
        ConnectionThirdPartyFrpcModeComboBox.ItemsSource = _thirdPartyFrpcModeOptions;
        SettingsContributorsItemsControl.ItemsSource = _settingsContributorItems;
        SettingsSponsorsItemsControl.ItemsSource = _settingsSponsorItems;
        SettingsConsoleLogFiltersItemsControl.ItemsSource = _visibleConsoleLogFilterRuleItems;
        AutomationConfigItemsControl.ItemsSource = _automationConfigItems;
        AutomationProfileComboBox.ItemsSource = _automationProfileItems;
        AutomationActionsItemsControl.ItemsSource = _automationActionWindowItems;
        AutomationBackupSchedulesItemsControl.ItemsSource = _automationBackupScheduleItems;
        AutomationBroadcastsItemsControl.ItemsSource = _automationBroadcastItems;
        AutomationCommandsItemsControl.ItemsSource = _automationCommandItems;
        AutomationExportTimesItemsControl.ItemsSource = _automationExportTimeItems;
        AutomationScriptsItemsControl.ItemsSource = _automationScriptItems;
        LogsItemsControl.ItemsSource = _logItems;
        ModProfileComboBox.ItemsSource = _modProfileItems;
        ModsListBox.ItemsSource = _modItems;
        RobotBindingsItemsControl.ItemsSource = _robotBindingItems;
        RobotTeleportPointsItemsControl.ItemsSource = _robotTeleportPointItems;
        RobotCustomCommandsItemsControl.ItemsSource = _robotCustomCommandItems;
        DiscordBindingsItemsControl.ItemsSource = _discordBindingItems;
        DiscordCustomCommandsItemsControl.ItemsSource = _discordCustomCommandItems;
        GatewayBackendsItemsControl.ItemsSource = _gatewayBackendItems;
        GatewayBackendRuntimeItemsControl.ItemsSource = _gatewayBackendRuntimeItems;
        AuthConfigItemsControl.ItemsSource = _authConfigItems;
        AuthProfileComboBox.ItemsSource = _authProfileItems;
        AuthPlayersListBox.ItemsSource = _authPlayerItems;
        ServerBridgeConfigItemsControl.ItemsSource = _serverBridgeConfigItems;
        ServerBridgeProfileComboBox.ItemsSource = _serverBridgeProfileItems;
        DashboardServersItemsControl.ItemsSource = _dashboardServerItems;
        DashboardOnlinePlayersItemsControl.ItemsSource = _dashboardOnlinePlayerItems;
        DashboardUptimeItemsControl.ItemsSource = _dashboardUptimeItems;
        LaunchTargetsItemsControl.ItemsSource = _launchTargetItems;
        LaunchAddProfileComboBox.ItemsSource = _launchAddProfileItems;
        SettingsAutoStartTargetsItemsControl.ItemsSource = _settingsAutoStartTargetItems;
        SettingsAutoStartAddProfileComboBox.ItemsSource = _settingsAutoStartAddProfileItems;
        ConsoleServerComboBox.ItemsSource = _consoleServerItems;
        RegisterCollectionTranslationHandlers();
        RebuildConfigChoiceOptions();
        RebuildThirdPartyFrpcModeOptions();
    }

    private void RegisterCollectionTranslationHandlers()
    {
        INotifyCollectionChanged[] collections =
        [
            _profileItems,
            _saveItems,
            _downloadVersionItems,
            _configWorldRuleItems,
            _settingsContributorItems,
            _settingsSponsorItems,
            _automationConfigItems,
            _automationActionWindowItems,
            _automationBackupScheduleItems,
            _automationBroadcastItems,
            _automationCommandItems,
            _automationExportTimeItems,
            _automationScriptItems,
            _logItems,
            _modItems,
            _authConfigItems,
            _authPlayerItems,
            _serverBridgeConfigItems,
            _robotBindingItems,
            _robotCustomCommandItems,
            _discordBindingItems,
            _discordCustomCommandItems,
            _gatewayBackendItems,
            _gatewayBackendRuntimeItems,
            _dashboardServerItems,
            _dashboardOnlinePlayerItems,
            _dashboardUptimeItems,
            _launchTargetItems,
            _settingsAutoStartTargetItems
        ];

        foreach (var collection in collections)
        {
            collection.CollectionChanged += OnTranslatedCollectionChanged;
        }
    }

    private void OnTranslatedCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        QueueStaticUiTranslations();
    }

    private static void FillWithZero(List<double> target, int count)
    {
        target.Clear();
        for (var i = 0; i < count; i++)
        {
            target.Add(0);
        }
    }

    private void OnDataTimerTick(object? sender, EventArgs e)
    {
        var statuses = _serverProcessService.GetCachedStatuses();
        var status = statuses.FirstOrDefault(s => s.IsRunning) ?? _serverProcessService.GetCachedStatus();
        var robotStatus = _robotService.GetCurrentStatus();
        var robotResources = SampleRobotResources(robotStatus);
        var totalServerCpu = statuses.Where(s => s.IsRunning).Sum(s => s.CpuPercent);
        var totalServerMemoryBytes = statuses
            .Where(s => s.IsRunning)
            .Sum(s => ResolveProcessMemory(s.ProcessId) ?? s.MemoryBytes);
        PushNextSample(_serverCpuSamples, Math.Clamp(totalServerCpu, 0, 100));
        PushNextSample(_serverMemoryMbSamples, BytesToMb(totalServerMemoryBytes));
        PushNextSample(_playersSamples, statuses.Where(s => s.IsRunning).Sum(s => s.OnlinePlayers));

        PushNextSample(_robotCpuSamples, robotStatus.IsRunning ? robotResources.CpuPercent : 0);
        PushNextSample(_robotMemoryMbSamples, robotStatus.IsRunning ? BytesToMb(robotResources.MemoryBytes) : 0);
        if (DateTime.UtcNow.Second % 5 == 0)
        {
            var networkActive = _frpService.GetCurrentStatus().IsRunning ||
                                _thirdPartyFrpcService.GetCurrentStatus().IsRunning ||
                                _easyTierService.GetCurrentStatus().IsRunning ||
                                _tcpGatewayService.GetCurrentStatus().IsRunning;
            PushNextSample(_networkLatencySamples, networkActive ? 1 : 0, NetworkRangeCount);
        }

        if (_selectedTab == MainTab.Connection && _selectedConnectionTab == ConnectionTab.Gateway)
        {
            _ = RefreshGatewayStatusAsync();
        }

        UpdateCardValues(status);
        UpdateMultiServerDashboard(statuses);
        RefreshConsoleServerItems(statuses);
        ApplyStaticUiTranslations();
        if (AutomationEditorPanel.IsVisible)
        {
            foreach (var item in _automationBackupScheduleItems)
            {
                item.RefreshPreview();
            }
        }

        if (_selectedTab == MainTab.Monitor)
        {
            RenderSelectedMetricChart(status);
        }
    }

    private async void OnTickerTimerTick(object? sender, EventArgs e)
    {
        if (_selectedTab != MainTab.Monitor || _selectedMetric != HomeMetric.Players || _playerEvents.Count == 0 || _tickerAnimating)
        {
            return;
        }

        _tickerAnimating = true;
        _tickerIndex = (_tickerIndex + 1) % _playerEvents.Count;
        var nextText = _playerEvents[_tickerIndex];

        EventTickerNextText.Text = nextText;
        EventTickerCurrentText.RenderTransform = TransformOperations.Parse("translate(0px,-24px)");
        EventTickerNextText.RenderTransform = TransformOperations.Parse("translate(0px,0px)");

        await Task.Delay(260);

        EventTickerCurrentText.Text = nextText;
        EventTickerCurrentText.RenderTransform = TransformOperations.Parse("translate(0px,0px)");
        EventTickerNextText.RenderTransform = TransformOperations.Parse("translate(0px,24px)");

        _tickerAnimating = false;
    }

    private void OnHomeSloganTimerTick(object? sender, EventArgs e)
    {
        if (!HomePanel.IsVisible)
        {
            return;
        }

        if (_homeSloganVisible)
        {
            HomeSloganTextBlock.Opacity = 0;
            _homeSloganVisible = false;
            return;
        }

        _homeSloganIndex = (_homeSloganIndex + 1) % HomeSlogans.Length;
        HomeSloganTextBlock.Text = T(HomeSlogans[_homeSloganIndex].Zh, HomeSlogans[_homeSloganIndex].En);
        HomeSloganTextBlock.Opacity = 1;
        _homeSloganVisible = true;
    }

    private void ShowToast(string message, ToastKind? kind = null)
    {
        var text = NormalizeToastMessage(message);
        if (string.IsNullOrWhiteSpace(text))
            return;

        var resolvedKind = kind ?? InferToastKind(text);
        _logger.LogInformation("Toast[{Kind}]: {Message}", resolvedKind, text);
        ToastMessageTextBlock.Text = text;
        ToastAccentBorder.Background = GetToastAccentBrush(resolvedKind);
        ToastHost.IsVisible = true;
        RestartToastTimer();
    }

    private void RestartToastTimer()
    {
        _toastTimer.Stop();
        if (!_toastPointerOver && ToastHost.IsVisible)
        {
            _toastTimer.Start();
        }
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        ToastHost.IsVisible = false;
    }

    private void OnToastTimerTick(object? sender, EventArgs e)
    {
        if (_toastPointerOver)
            return;

        HideToast();
    }

    private void OnToastPointerEntered(object? sender, PointerEventArgs e)
    {
        _toastPointerOver = true;
        _toastTimer.Stop();
    }

    private void OnToastPointerExited(object? sender, PointerEventArgs e)
    {
        _toastPointerOver = false;
        RestartToastTimer();
    }

    private void OnToastCloseClick(object? sender, RoutedEventArgs e)
    {
        HideToast();
    }

    private static string NormalizeToastMessage(string message)
    {
        var text = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.StartsWith("[system]", StringComparison.OrdinalIgnoreCase))
        {
            text = text[8..].Trim();
        }

        return text;
    }

    private static ToastKind InferToastKind(string text)
    {
        if (text.Contains("失败", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("错误", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("异常", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            return ToastKind.Error;
        }

        if (text.Contains("未启动", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("未启用", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("已停止", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("跳过", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("stopped", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("disabled", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("skipped", StringComparison.OrdinalIgnoreCase))
        {
            return ToastKind.Neutral;
        }

        if (text.Contains("已", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("完成", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("成功", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("启动", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("运行中", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("saved", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("started", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("running", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("deployed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("imported", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("deleted", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("completed", StringComparison.OrdinalIgnoreCase))
        {
            return ToastKind.Success;
        }

        return ToastKind.Neutral;
    }

    private static IBrush GetToastAccentBrush(ToastKind kind)
    {
        return new SolidColorBrush(kind switch
        {
            ToastKind.Success => Color.Parse("#6B8E23"),
            ToastKind.Error => Color.Parse("#C62828"),
            _ => Color.Parse("#555555")
        });
    }

    private void UpdateCardValues(ServerRuntimeStatus status)
    {
        var serverCpu = _serverCpuSamples[^1];
        var serverMemMb = _serverMemoryMbSamples[^1];

        var robotStatus = _robotService.GetCurrentStatus();
        var robotCpu = _robotCpuSamples[^1];
        var robotMemMb = _robotMemoryMbSamples[^1];
        UpdateDashboard(status, robotStatus, serverCpu, serverMemMb, robotCpu, robotMemMb);

        var statuses = _serverProcessService.GetCachedStatuses();
        var hasRunningServer = statuses.Any(static current => current.IsRunning);
        var hasPendingLaunchTargets = HasPendingLaunchTargets(statuses);
        var stopMode = hasRunningServer && !hasPendingLaunchTargets;
        LaunchActionTextBlock.Text = stopMode ? T("停止服务器", "Stop Server") : T("启动服务器", "Start Server");
        LaunchActionIconPath.Data = Geometry.Parse(stopMode ? LaunchStopIconData : LaunchStartIconData);
        LaunchServerButton.Classes.Set("running", stopMode);
        RefreshLaunchButtonSummary();
    }

    private void UpdateDashboard(
        ServerRuntimeStatus status,
        RobotRuntimeStatus robotStatus,
        double serverCpu,
        double serverMemMb,
        double robotCpu,
        double robotMemMb)
    {
        UpdateMultiServerDashboard(_serverProcessService.GetCachedStatuses());
    }

    private void UpdateMultiServerDashboard(IReadOnlyList<ServerRuntimeStatus> statuses)
    {
        var runningStatuses = statuses.Where(static status => status.IsRunning).ToList();
        var statusByProfileId = statuses
            .Where(static status => !string.IsNullOrWhiteSpace(status.ProfileId))
            .GroupBy(status => status.ProfileId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var profiles = _profileService.GetProfiles()
            .Select(profile =>
            {
                var profileId = profile.Id.Trim();
                var isRunning = statusByProfileId.TryGetValue(profileId, out var status) && status.IsRunning;
                var displayName = string.IsNullOrWhiteSpace(profile.Name) ? profileId : profile.Name;
                return (Profile: profile, ProfileId: profileId, IsRunning: isRunning, DisplayName: displayName);
            })
            .OrderByDescending(static item => item.IsRunning)
            .ThenBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var desiredItems = new List<DashboardServerItem>();
        foreach (var (profile, profileId, _, displayName) in profiles)
        {
            EnsureDashboardSettings(profile);

            _dashboardSettingsByProfile.TryGetValue(profileId, out var settings);
            var hasStatus = statusByProfileId.TryGetValue(profileId, out var status);
            var isRunning = hasStatus && status is not null && status.IsRunning;
            var isLoading = _isStoppingOrStarting;
            var cpuPercent = isRunning ? status!.CpuPercent : 0;
            var memoryMb = isRunning ? BytesToMb(status!.MemoryBytes) : 0;
            var item = _dashboardServerItems.FirstOrDefault(existing =>
                           existing.ProfileId.Equals(profileId, StringComparison.OrdinalIgnoreCase))
                       ?? new DashboardServerItem { ProfileId = profileId };
            item.ProfileName = displayName;
            item.Version = string.IsNullOrWhiteSpace(profile.Version) ? "--" : profile.Version;
            item.IsRunning = isRunning;
            item.IsActionEnabled = !isLoading;
            item.ActionText = isRunning ? T("停止", "Stop") : T("启动", "Start");
            item.StatusText = isLoading
                ? T("加载中", "Loading")
                : isRunning ? T("正在运行", "Running") : T("已停止", "Stopped");
            item.StatusBrush = new SolidColorBrush(Color.Parse(isLoading ? "#F59E0B" : isRunning ? "#16A34A" : "#DC2626"));
            item.SummaryText = T(
                $"端口 {settings?.Port.ToString(CultureInfo.InvariantCulture) ?? "--"}  CPU {cpuPercent:F1}%  内存 {memoryMb:F0} MB",
                $"Port {settings?.Port.ToString(CultureInfo.InvariantCulture) ?? "--"}  CPU {cpuPercent:F1}%  Mem {memoryMb:F0} MB");
            desiredItems.Add(item);
        }

        if (desiredItems.Count == 0)
        {
            var emptyItem = _dashboardServerItems.FirstOrDefault(static item => string.IsNullOrWhiteSpace(item.ProfileId))
                            ?? new DashboardServerItem();
            emptyItem.ProfileName = T("暂无服务器档案", "No server profiles");
            emptyItem.Version = "--";
            emptyItem.IsRunning = false;
            emptyItem.IsActionEnabled = false;
            emptyItem.ActionText = T("启动", "Start");
            emptyItem.StatusText = T("已停止", "Stopped");
            emptyItem.StatusBrush = new SolidColorBrush(Color.Parse("#DC2626"));
            emptyItem.SummaryText = T("请先在实例页面创建档案。", "Create a profile from the instance page first.");
            desiredItems.Add(emptyItem);
        }

        SynchronizeDashboardServerItems(desiredItems);

        var players = _serverProcessService.GetOnlinePlayers();
        _dashboardOnlinePlayerItems.Clear();
        foreach (var player in players)
        {
            _dashboardOnlinePlayerItems.Add(DashboardPlayerItem.FromModel(player));
        }

        if (_dashboardOnlinePlayerItems.Count == 0)
        {
            _dashboardOnlinePlayerItems.Add(new DashboardPlayerItem
            {
                PlayerName = T("暂无在线玩家", "No online players"),
                ProfileName = "--",
                LatencyText = "--",
                JoinedAtText = "--"
            });
        }

        var maxPlayers = runningStatuses
            .Select(status =>
            {
                var id = status.ProfileId ?? string.Empty;
                return _dashboardSettingsByProfile.TryGetValue(id, out var settings) ? settings.MaxClients : 0;
            })
            .Sum();
        DashboardPlayersCountText.Text = $"{players.Count.ToString(CultureInfo.InvariantCulture)}/{(maxPlayers > 0 ? maxPlayers.ToString(CultureInfo.InvariantCulture) : "--")}";

        UpdateDashboardUptimeItems(runningStatuses);
    }

    private void SynchronizeDashboardServerItems(IReadOnlyList<DashboardServerItem> desiredItems)
    {
        for (var index = _dashboardServerItems.Count - 1; index >= 0; index--)
        {
            var existing = _dashboardServerItems[index];
            if (!desiredItems.Any(item => ReferenceEquals(item, existing)))
            {
                _dashboardServerItems.RemoveAt(index);
            }
        }

        for (var desiredIndex = 0; desiredIndex < desiredItems.Count; desiredIndex++)
        {
            var desiredItem = desiredItems[desiredIndex];
            var currentIndex = _dashboardServerItems.IndexOf(desiredItem);
            if (currentIndex < 0)
            {
                _dashboardServerItems.Insert(desiredIndex, desiredItem);
                continue;
            }

            if (currentIndex != desiredIndex)
            {
                _dashboardServerItems.Move(currentIndex, desiredIndex);
            }
        }
    }

    private void EnsureDashboardSettings(InstanceProfile profile)
    {
        var profileId = profile.Id.Trim();
        if (string.IsNullOrWhiteSpace(profileId) ||
            _dashboardSettingsByProfile.ContainsKey(profileId) ||
            _dashboardSettingsLoadingProfileIds.Contains(profileId))
        {
            return;
        }

        _dashboardSettingsLoadingProfileIds.Add(profileId);
        var requestVersion = _dashboardSettingsVersion;
        _ = RefreshDashboardSettingsAsync(profile, requestVersion);
    }

    private async Task RefreshDashboardSettingsAsync(InstanceProfile profile, long requestVersion)
    {
        var profileId = profile.Id.Trim();
        try
        {
            var settings = await _instanceServerConfigService.LoadServerSettingsAsync(profile);
            Dispatcher.UIThread.Post(() =>
            {
                _dashboardSettingsLoadingProfileIds.Remove(profileId);
                if (requestVersion == _dashboardSettingsVersion)
                {
                    _dashboardSettingsByProfile[profileId] = settings;
                }

                UpdateMultiServerDashboard(_serverProcessService.GetCachedStatuses());
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => _dashboardSettingsLoadingProfileIds.Remove(profileId));
            _logger.LogDebug(ex, "Failed to load dashboard settings for profile {ProfileId}", profile.Id);
        }
    }

    private void UpdateDashboardSettingsCache(InstanceProfile profile, ServerCommonSettings settings)
    {
        var profileId = profile.Id.Trim();
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return;
        }

        _dashboardSettingsVersion++;
        _dashboardSettingsLoadingProfileIds.Remove(profileId);
        _dashboardSettingsByProfile[profileId] = settings;
        UpdateMultiServerDashboard(_serverProcessService.GetCachedStatuses());
    }

    private void InvalidateDashboardSettingsCache(InstanceProfile profile)
    {
        var profileId = profile.Id.Trim();
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return;
        }

        _dashboardSettingsVersion++;
        _dashboardSettingsByProfile.Remove(profileId);
        _dashboardSettingsLoadingProfileIds.Remove(profileId);
        UpdateMultiServerDashboard(_serverProcessService.GetCachedStatuses());
    }

    private void UpdateDashboardUptimeItems(IReadOnlyList<ServerRuntimeStatus> runningStatuses)
    {
        _dashboardUptimeItems.Clear();
        foreach (var status in runningStatuses)
        {
            var profile = ResolveDashboardProfile(status);
            _dashboardUptimeItems.Add(new DashboardUptimeItem
            {
                Name = string.IsNullOrWhiteSpace(profile?.Name) ? status.ProfileId ?? T("服务器", "Server") : profile.Name,
                UptimeText = FormatConnectionUptime(status.StartedAtUtc)
            });
        }

        var robotStatus = _robotService.GetCurrentStatus();
        _dashboardUptimeItems.Add(new DashboardUptimeItem
        {
            Name = T("QQ机器人", "QQ Robot"),
            UptimeText = robotStatus.IsRunning ? FormatConnectionUptime(robotStatus.StartedAtUtc) : "--"
        });

        var frpStatus = _frpService.GetCurrentStatus();
        var thirdPartyStatus = _thirdPartyFrpcService.GetCurrentStatus();
        _dashboardUptimeItems.Add(new DashboardUptimeItem
        {
            Name = T("FRP", "FRP"),
            UptimeText = frpStatus.IsRunning ? FormatConnectionUptime(frpStatus.StartedAtUtc) : "--"
        });
        _dashboardUptimeItems.Add(new DashboardUptimeItem
        {
            Name = T("第三方FRP", "Third-party FRP"),
            UptimeText = thirdPartyStatus.IsRunning ? FormatConnectionUptime(thirdPartyStatus.StartedAtUtc) : "--"
        });
    }

    private void RefreshConsoleServerItems(IReadOnlyList<ServerRuntimeStatus> statuses)
    {
        var runningStatuses = statuses.Where(static status => status.IsRunning).ToList();
        var previousSelected = _selectedConsoleProfileId;
        _consoleServerItems.Clear();
        foreach (var status in runningStatuses)
        {
            var profile = ResolveDashboardProfile(status);
            var profileId = status.ProfileId ?? profile?.Id ?? string.Empty;
            if (string.IsNullOrWhiteSpace(profileId))
            {
                continue;
            }

            _consoleServerItems.Add(new ConsoleServerItem
            {
                ProfileId = profileId,
                DisplayName = string.IsNullOrWhiteSpace(profile?.Name) ? profileId : profile.Name
            });
        }

        var selected = _consoleServerItems.FirstOrDefault(item =>
                           !string.IsNullOrWhiteSpace(previousSelected) &&
                           item.ProfileId.Equals(previousSelected, StringComparison.OrdinalIgnoreCase))
                       ?? _consoleServerItems.FirstOrDefault();
        if (selected is null)
        {
            _selectedConsoleProfileId = string.Empty;
            ConsoleServerComboBox.SelectedIndex = -1;
            RefreshConsoleText();
            return;
        }

        if (!selected.ProfileId.Equals(_selectedConsoleProfileId, StringComparison.OrdinalIgnoreCase))
        {
            _selectedConsoleProfileId = selected.ProfileId;
            RefreshConsoleText();
        }

        if (!ReferenceEquals(ConsoleServerComboBox.SelectedItem, selected))
        {
            ConsoleServerComboBox.SelectedItem = selected;
        }

        var selectedProfileId = selected.ProfileId;
        _ = EnsureConsoleReplayLoadedAsync(selectedProfileId);
    }

    private InstanceProfile? ResolveDashboardProfile(ServerRuntimeStatus status)
    {
        if (!string.IsNullOrWhiteSpace(status.ProfileId))
        {
            var runningProfile = _profileService.GetProfileById(status.ProfileId.Trim());
            if (runningProfile is not null)
            {
                return runningProfile;
            }
        }

        var preferences = _preferencesService.Load();
        var defaultProfileId = SplitProfileIds(preferences.DefaultLaunchProfileIds, preferences.DefaultLaunchProfileId).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(defaultProfileId))
        {
            var defaultProfile = _profileService.GetProfileById(defaultProfileId);
            if (defaultProfile is not null)
            {
                return defaultProfile;
            }
        }

        return _profileService.GetProfiles().FirstOrDefault();
    }

    private void UpdateDashboardStatus(ServerRuntimeStatus status)
    {
        UpdateMultiServerDashboard(_serverProcessService.GetCachedStatuses());
    }

    private void UpdateDashboardUptimes(ServerRuntimeStatus status, RobotRuntimeStatus robotStatus)
    {
        UpdateDashboardUptimeItems(_serverProcessService.GetCachedStatuses().Where(static s => s.IsRunning).ToList());
    }

    private static DateTimeOffset? ParseRuntimeStartedAtUtc(string value)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var startedAt)
            ? startedAt
            : null;
    }

    private (double CpuPercent, long MemoryBytes) SampleRobotResources(RobotRuntimeStatus status)
    {
        if (!status.IsRunning || status.ProcessId is null)
        {
            return (0, 0);
        }

        try
        {
            using var process = Process.GetProcessById(status.ProcessId.Value);
            process.Refresh();
            var now = DateTimeOffset.UtcNow;
            var elapsedMs = Math.Max(1, (now - _robotLastCpuSampleUtc).TotalMilliseconds);
            if (_robotLastProcessorTime == TimeSpan.Zero)
            {
                _robotLastProcessorTime = process.TotalProcessorTime;
                _robotLastCpuSampleUtc = now;
                return (0, process.WorkingSet64);
            }

            if (elapsedMs >= 700)
            {
                var currentProcessorTime = process.TotalProcessorTime;
                var processorElapsedMs = Math.Max(0, (currentProcessorTime - _robotLastProcessorTime).TotalMilliseconds);
                _robotLastCpuPercent = Math.Clamp(
                    processorElapsedMs / (elapsedMs * Environment.ProcessorCount) * 100.0,
                    0,
                    100);
                _robotLastProcessorTime = currentProcessorTime;
                _robotLastCpuSampleUtc = now;
            }

            return (_robotLastCpuPercent, process.WorkingSet64);
        }
        catch
        {
            return (0, 0);
        }
    }

    private void RenderSelectedMetricChart(ServerRuntimeStatus? statusOverride = null)
    {
        var status = statusOverride ?? _serverProcessService.GetCachedStatus();
        RenderDashboardResourceChart(status);
    }

    private void RenderDashboardResourceChart(ServerRuntimeStatus status)
    {
        var robotStatus = _robotService.GetCurrentStatus();
        var hasRunningServer = _serverProcessService.GetCachedStatuses().Any(static current => current.IsRunning);
        var serverCpu = _serverCpuSamples[^1];
        var robotCpu = _robotCpuSamples[^1];
        var serverMemoryMb = _serverMemoryMbSamples[^1];
        var robotMemoryMb = _robotMemoryMbSamples[^1];
        var yMax = Math.Max(
            GetMemoryChartYMax(_serverMemoryMbSamples),
            GetMemoryChartYMax(_robotMemoryMbSamples));

        RenderDualLineChart(
            title: T("资源监控", "Resource Monitor"),
            topValue: T($"{serverMemoryMb:F0} MB / {robotMemoryMb:F0} MB", $"{serverMemoryMb:F0} MB / {robotMemoryMb:F0} MB"),
            summary: T("60 秒区间，蓝线为服务器总内存占用，绿线为 QQ 机器人内存占用。", "60-second range. Blue is total server memory usage; green is QQ robot memory usage."),
            primary: _serverMemoryMbSamples,
            secondary: _robotMemoryMbSamples,
            yMin: 0,
            yMax: yMax,
            yAxisFormatter: value => $"{value:F0}",
            xHint: T("60 秒", "60 seconds"),
            details:
            [
                (T("服务器总 CPU", "Total Server CPU"), hasRunningServer ? $"{serverCpu:F1}%" : "--"),
                (T("服务器总内存", "Total Server Memory"), hasRunningServer ? $"{serverMemoryMb:F0} MB" : "--"),
                (T("机器人 CPU", "Robot CPU"), robotStatus.IsRunning ? $"{robotCpu:F1}%" : "--"),
                (T("机器人内存", "Robot Memory"), robotStatus.IsRunning ? $"{robotMemoryMb:F0} MB" : "--")
            ]);
    }

    private void RenderServerChart(ServerRuntimeStatus status)
    {
        var cpu = _serverCpuSamples[^1];
        var memoryMb = _serverMemoryMbSamples[^1];
        var yMax = GetMemoryChartYMax(_serverMemoryMbSamples);
        var uptime = status.StartedAtUtc.HasValue
            ? FormatDuration(DateTimeOffset.UtcNow - status.StartedAtUtc.Value)
            : "--";

        RenderSingleLineChart(
            title: T("服务器状态", "Server Status"),
            topValue: status.IsRunning ? $"{memoryMb:F0} MB" : T("未启动", "Stopped"),
            summary: T("60 秒区间，蓝线为服务端进程内存 MB；CPU 仅在详情展示。", "60-second range. Blue is server process memory MB; CPU is shown in details only."),
            primary: _serverMemoryMbSamples,
            yMin: 0,
            yMax: yMax,
            yAxisFormatter: value => $"{value:F0}",
            xHint: T("60 秒", "60 seconds"),
            showTicker: false,
            details:
            [
                (T("CPU", "CPU"), $"{cpu:F1}%"),
                (T("内存", "Memory"), $"{memoryMb:F0} MB"),
                (T("PID", "PID"), status.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "--"),
                (T("运行时间", "Uptime"), uptime)
            ]);
    }

    private void RenderRobotChart()
    {
        var status = _robotService.GetCurrentStatus();
        var cpu = _robotCpuSamples[^1];
        var memoryMb = _robotMemoryMbSamples[^1];
        var yMax = GetMemoryChartYMax(_robotMemoryMbSamples);
        var uptime = status.StartedAtUtc.HasValue
            ? FormatDuration(DateTimeOffset.UtcNow - status.StartedAtUtc.Value)
            : "--";

        RenderSingleLineChart(
            title: T("机器人状态", "Robot Status"),
            topValue: status.IsRunning ? $"{memoryMb:F0} MB" : T("未启动", "Stopped"),
            summary: T("60 秒区间，蓝线为 QQ 机器人内存 MB；CPU 仅在详情展示。", "60-second range. Blue is QQ robot memory MB; CPU is shown in details only."),
            primary: _robotMemoryMbSamples,
            yMin: 0,
            yMax: yMax,
            yAxisFormatter: value => $"{value:F0}",
            xHint: T("60 秒", "60 seconds"),
            showTicker: false,
            details:
            [
                (T("CPU", "CPU"), $"{cpu:F1}%"),
                (T("内存", "Memory"), $"{memoryMb:F0} MB"),
                (T("状态", "Status"), status.IsRunning ? T("运行中", "Running") : T("未启动", "Stopped")),
                (T("运行时间", "Uptime"), uptime)
            ]);
    }

    private void RenderPlayersChart(ServerRuntimeStatus status)
    {
        var currentPlayers = (int)Math.Round(_playersSamples[^1]);
        var peakPlayers = Math.Max(status.PeakOnlinePlayers, (int)Math.Round(_playersSamples.Max()));
        RenderSingleLineChart(
            title: T("在线玩家", "Online Players"),
            topValue: T($"{currentPlayers} 人", $"{currentPlayers} players"),
            summary: T("60 秒区间，数据来自服务端输出解析。", "60-second range parsed from server output."),
            primary: _playersSamples,
            yMin: 0,
            yMax: NiceCeiling(Math.Max(4, _playersSamples.Max() + 1)),
            yAxisFormatter: value => $"{Math.Round(value):F0}",
            xHint: T("60 秒", "60 seconds"),
            showTicker: true,
            details:
            [
                (T("当前人数", "Current"), currentPlayers.ToString(CultureInfo.InvariantCulture)),
                (T("最高人数", "Peak"), peakPlayers.ToString(CultureInfo.InvariantCulture)),
                (T("事件数量", "Events"), _playerEvents.Count.ToString(CultureInfo.InvariantCulture)),
                (T("来源", "Source"), T("服务端输出", "Server output"))
            ]);
    }

    private void RenderNetworkChart()
    {
        RenderSingleLineChart(
            title: T("网络状态", "Network Status"),
            topValue: T("未配置", "Not configured"),
            summary: T("连接监控尚未配置，当前不展示模拟延迟。", "Connection monitor is not configured; no simulated latency is shown."),
            primary: _networkLatencySamples,
            yMin: 0,
            yMax: 100,
            yAxisFormatter: value => $"{value:F0}ms",
            xHint: T("最近 12 小时", "Last 12 hours"),
            showTicker: false,
            details:
            [
                (T("当前延迟", "Latency"), "--"),
                (T("丢包", "Packet loss"), "--"),
                (T("测试频率", "Frequency"), T("未启动", "Stopped")),
                (T("采样区间", "Range"), T("12 小时", "12 hours"))
            ]);
    }

    private void RenderDualLineChart(
        string title,
        string topValue,
        string summary,
        IReadOnlyList<double> primary,
        IReadOnlyList<double> secondary,
        double yMin,
        double yMax,
        Func<double, string> yAxisFormatter,
        string xHint,
        IReadOnlyList<(string Label, string Value)> details)
    {
        ChartTitleText.Text = title;
        ChartTopValueText.Text = topValue;
        ChartSummaryText.Text = summary;
        ChartXAxisText.Text = xHint;

        ChartLinePrimary.Points = BuildPolylinePoints(primary, yMin, yMax);
        ChartLineSecondary.Points = BuildPolylinePoints(secondary, yMin, yMax);
        ChartLineSecondary.IsVisible = true;

        SetYAxisLabels(yMin, yMax, yAxisFormatter);
        SetChartDetails(details);
        EventTickerContainer.IsVisible = false;
    }

    private void RenderSingleLineChart(
        string title,
        string topValue,
        string summary,
        IReadOnlyList<double> primary,
        double yMin,
        double yMax,
        Func<double, string> yAxisFormatter,
        string xHint,
        bool showTicker,
        IReadOnlyList<(string Label, string Value)> details)
    {
        ChartTitleText.Text = title;
        ChartTopValueText.Text = topValue;
        ChartSummaryText.Text = summary;
        ChartXAxisText.Text = xHint;

        ChartLinePrimary.Points = BuildPolylinePoints(primary, yMin, yMax);
        ChartLineSecondary.IsVisible = false;
        ChartLineSecondary.Points = [];

        SetYAxisLabels(yMin, yMax, yAxisFormatter);
        SetChartDetails(details);
        EventTickerContainer.IsVisible = showTicker;
    }

    private void SetYAxisLabels(double yMin, double yMax, Func<double, string> formatter)
    {
        var span = Math.Max(0.0001, yMax - yMin);
        var labels = new[]
        {
            yMax,
            yMin + span * 0.8,
            yMin + span * 0.6,
            yMin + span * 0.4,
            yMin + span * 0.2,
            yMin
        };

        YAxisLabelTop.Text = formatter(labels[0]);
        YAxisLabel2.Text = formatter(labels[1]);
        YAxisLabel3.Text = formatter(labels[2]);
        YAxisLabel4.Text = formatter(labels[3]);
        YAxisLabel5.Text = formatter(labels[4]);
        YAxisLabelBottom.Text = formatter(labels[5]);
    }

    private void SetChartDetails(IReadOnlyList<(string Label, string Value)> details)
    {
        var normalized = details.Take(4).ToArray();
        if (normalized.Length < 4)
        {
            normalized = normalized.Concat(Enumerable.Repeat((string.Empty, string.Empty), 4 - normalized.Length)).ToArray();
        }

        DetailOneLabelText.Text = normalized[0].Label;
        DetailOneValueText.Text = normalized[0].Value;
        DetailTwoLabelText.Text = normalized[1].Label;
        DetailTwoValueText.Text = normalized[1].Value;
        DetailThreeLabelText.Text = normalized[2].Label;
        DetailThreeValueText.Text = normalized[2].Value;
        DetailFourLabelText.Text = normalized[3].Label;
        DetailFourValueText.Text = normalized[3].Value;
    }

    private void RenderThumbnailCharts()
    {
    }

    private static double GetMemoryChartYMax(IReadOnlyList<double> memoryMbSamples)
    {
        return NiceCeiling(Math.Max(1, memoryMbSamples.Max()));
    }

    private static IList<Point> BuildPolylinePoints(
        IReadOnlyList<double> values,
        double yMin,
        double yMax,
        double width = ChartWidth,
        double height = ChartHeight)
    {
        if (values.Count <= 1)
        {
            return [new Point(0, height), new Point(width, height)];
        }

        var points = new List<Point>(values.Count);
        var denominator = Math.Max(0.0001, yMax - yMin);
        for (var i = 0; i < values.Count; i++)
        {
            var x = i * (width / (values.Count - 1));
            var normalized = Math.Clamp((values[i] - yMin) / denominator, 0, 1);
            var y = height - normalized * height;
            points.Add(new Point(x, y));
        }

        return new Points(points);
    }

    private void RefreshProfiles()
    {
        var profiles = _profileService.GetProfiles();
        _profileItems.Clear();
        foreach (var profile in profiles)
        {
            _profileItems.Add(ProfileListItem.FromProfile(profile));
        }

        var versions = _profileService.GetInstalledVersions();
        CreateVersionComboBox.ItemsSource = versions;
        if (CreateVersionComboBox.SelectedIndex < 0 && versions.Count > 0)
        {
            CreateVersionComboBox.SelectedIndex = 0;
        }

        RefreshLaunchOptions(profiles);
        RefreshLogItems(profiles);
        _ = RefreshSavesAsync();
        _ = RefreshConfigProfilesAsync();
        _ = RefreshAutomationAsync();
        _ = RefreshModsAsync();
        _ = RefreshAuthProfilesAsync();
        _ = RefreshServerBridgeProfilesAsync();
    }

    private void RefreshLogItems(IReadOnlyList<InstanceProfile>? profiles = null)
    {
        _logItems.Clear();
        foreach (var profile in (profiles ?? _profileService.GetProfiles()).OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            _logItems.Add(ProfileLogListItem.FromProfile(profile));
        }
    }

    private void RefreshLaunchOptions(IReadOnlyList<InstanceProfile>? profiles = null)
    {
        RefreshLaunchTargetItems(profiles ?? _profileService.GetProfiles());
        RefreshLaunchButtonSummary();
    }

    private void RefreshLaunchTargetItems(IReadOnlyList<InstanceProfile>? profiles = null)
    {
        var profileList = profiles ?? _profileService.GetProfiles();
        var selectedIds = LoadLaunchProfileIds();
        _launchTargetItems.Clear();
        foreach (var profile in profileList.Where(profile => selectedIds.Contains(profile.Id)))
        {
            _launchTargetItems.Add(new LaunchTargetItem
            {
                ProfileId = profile.Id,
                DisplayName = profile.Name
            });
        }

        _launchAddProfileItems.Clear();
        foreach (var profile in profileList.Where(profile => !selectedIds.Contains(profile.Id)))
        {
            _launchAddProfileItems.Add(profile);
        }
    }

    private HashSet<string> LoadLaunchProfileIds()
    {
        var preferences = _preferencesService.Load();
        var ids = SplitProfileIds(preferences.DefaultLaunchProfileIds, preferences.DefaultLaunchProfileId);
        return ids.Count > 0 ? ids : [];
    }

    private string GetPrimaryLaunchProfileId()
    {
        var preferences = _preferencesService.Load();
        return SplitProfileIds(preferences.DefaultLaunchProfileIds, preferences.DefaultLaunchProfileId).FirstOrDefault() ?? string.Empty;
    }

    private static HashSet<string> SplitProfileIds(string value)
    {
        return value
            .Split([';', ',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> SplitProfileIds(IEnumerable<string>? values, string legacyValue = "")
    {
        var result = SplitProfileIds(legacyValue);
        foreach (var value in values ?? [])
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                result.Add(value.Trim());
            }
        }

        return result;
    }

    private void SaveLaunchProfileIds(IEnumerable<string> profileIds)
    {
        var preferences = _preferencesService.Load();
        var ids = profileIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        preferences.DefaultLaunchProfileIds = ids;
        preferences.DefaultLaunchProfileId = string.Join(';', ids);
        _preferencesService.Save(preferences);
        RefreshLaunchTargetItems();
        RefreshLaunchButtonSummary();
    }

    private HashSet<string> LoadAutoStartProfileIds()
    {
        var preferences = _preferencesService.Load();
        return SplitProfileIds(preferences.AutoStartServerProfileIds, preferences.AutoStartServerProfileId);
    }

    private void SaveAutoStartProfileIds(IEnumerable<string> profileIds)
    {
        var preferences = _preferencesService.Load();
        var ids = profileIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        preferences.AutoStartServerProfileIds = ids;
        preferences.AutoStartServerProfileId = string.Join(';', ids);
        _preferencesService.Save(preferences);
        RefreshSettingsAutoStartTargetItems();
    }

    private void RefreshSettingsAutoStartTargetItems(IReadOnlyList<InstanceProfile>? profiles = null)
    {
        var profileList = profiles ?? _profileService.GetProfiles();
        var selectedIds = LoadAutoStartProfileIds();
        _settingsAutoStartTargetItems.Clear();
        foreach (var profile in profileList.Where(profile => selectedIds.Contains(profile.Id)))
        {
            _settingsAutoStartTargetItems.Add(new LaunchTargetItem
            {
                ProfileId = profile.Id,
                DisplayName = profile.Name
            });
        }

        _settingsAutoStartAddProfileItems.Clear();
        foreach (var profile in profileList.Where(profile => !selectedIds.Contains(profile.Id)))
        {
            _settingsAutoStartAddProfileItems.Add(profile);
        }
    }

    private async Task RefreshAutomationAsync()
    {
        if (_isRefreshingAutomation)
        {
            return;
        }

        _isRefreshingAutomation = true;
        try
        {
            var preferences = _preferencesService.Load();
            var profiles = _profileService.GetProfiles();
            var selectedProfileId = !string.IsNullOrWhiteSpace(_editingAutomationProfileId)
                ? _editingAutomationProfileId
                : AutomationProfileComboBox.SelectedItem is InstanceProfile selectedProfile
                    ? selectedProfile.Id
                    : string.Empty;
            _automationProfileItems.Clear();
            foreach (var profile in profiles)
            {
                _automationProfileItems.Add(profile);
            }

            RefreshAutomationConfigItems(profiles);
            AutomationProfileComboBox.ItemsSource = _automationProfileItems;
            if (_automationProfileItems.Count > 0)
            {
                var target = _automationProfileItems.FirstOrDefault(profile =>
                    !string.IsNullOrWhiteSpace(selectedProfileId) &&
                    profile.Id.Equals(selectedProfileId, StringComparison.OrdinalIgnoreCase))
                    ?? _automationProfileItems.FirstOrDefault(profile =>
                        SplitProfileIds(preferences.DefaultLaunchProfileIds, preferences.DefaultLaunchProfileId).Contains(profile.Id))
                    ?? _automationProfileItems.FirstOrDefault();
                AutomationProfileComboBox.SelectedItem = target;
            }

            if (AutomationEditorPanel.IsVisible &&
                AutomationProfileComboBox.SelectedItem is InstanceProfile editorProfile)
            {
                var settings = await _automationSettingsService.LoadAsync(editorProfile);
                ApplyAutomationSettings(settings);
            }
            SetAutomationStatus(T("自动化配置已加载。", "Automation settings loaded."), notify: false);
        }
        catch (Exception ex)
        {
            SetAutomationStatus(T($"自动化加载失败：{ex.Message}", $"Automation load failed: {ex.Message}"));
        }
        finally
        {
            _isRefreshingAutomation = false;
        }
    }

    private void ApplyAutomationSettings(AutomationSettings settings)
    {
        AutomationRestartEnabledCheckBox.IsChecked = settings.RestartSchedulerEnabled;
        AutomationBackupEnabledCheckBox.IsChecked = settings.BackupEnabled;
        AutomationBackupBeforeShutdownCheckBox.IsChecked = settings.BackupBeforeShutdown;
        SetNumericValue(AutomationBackupRetentionNumericUpDown, settings.BackupRetentionCount);
        AutomationBroadcastEnabledCheckBox.IsChecked = settings.BroadcastEnabled;
        AutomationCommandEnabledCheckBox.IsChecked = settings.CommandEnabled;
        AutomationExportEnabledCheckBox.IsChecked = settings.ExportLogEnabled;
        AutomationExportBeforeShutdownCheckBox.IsChecked = settings.ExportBeforeShutdown;
        AutomationExportIncludeChatCheckBox.IsChecked = settings.ExportIncludeChat;
        AutomationExportIncludeServerCheckBox.IsChecked = settings.ExportIncludeServerInfo;
        AutomationClearCacheBeforeStartCheckBox.IsChecked = settings.ClearCacheBeforeStart;
        AutomationScriptsEnabledCheckBox.IsChecked = settings.AutomationScriptsEnabled;

        _automationActionWindowItems.Clear();
        foreach (var window in settings.ActionWindows ?? [])
        {
            _automationActionWindowItems.Add(AutomationActionWindowItem.FromModel(window, _isChinese));
        }
        if (_automationActionWindowItems.Count == 0)
        {
            _automationActionWindowItems.Add(new AutomationActionWindowItem(_isChinese));
        }

        _automationBackupScheduleItems.Clear();
        foreach (var schedule in settings.BackupSchedules ?? [])
        {
            _automationBackupScheduleItems.Add(AutomationBackupScheduleItem.FromModel(schedule, _isChinese));
        }
        if (_automationBackupScheduleItems.Count == 0)
        {
            _automationBackupScheduleItems.Add(new AutomationBackupScheduleItem(_isChinese));
        }

        _automationBroadcastItems.Clear();
        foreach (var message in settings.BroadcastMessages ?? [])
        {
            _automationBroadcastItems.Add(ScheduledBroadcastItem.FromModel(message));
        }
        if (_automationBroadcastItems.Count == 0)
        {
            _automationBroadcastItems.Add(new ScheduledBroadcastItem());
        }

        _automationCommandItems.Clear();
        foreach (var command in settings.ScheduledCommands ?? [])
        {
            _automationCommandItems.Add(ScheduledCommandItem.FromModel(command));
        }
        if (_automationCommandItems.Count == 0)
        {
            _automationCommandItems.Add(new ScheduledCommandItem());
        }

        _automationExportTimeItems.Clear();
        foreach (var time in settings.ExportTimes ?? [])
        {
            _automationExportTimeItems.Add(new AutomationTimeItem(time));
        }
        if (_automationExportTimeItems.Count == 0)
        {
            _automationExportTimeItems.Add(new AutomationTimeItem("12:00"));
        }

        _automationScriptItems.Clear();
        foreach (var script in settings.AutomationScripts ?? [])
        {
            _automationScriptItems.Add(AutomationScriptItem.FromModel(script, _isChinese));
        }
    }

    private AutomationSettings CollectAutomationSettings()
    {
        var selectedProfile = AutomationProfileComboBox.SelectedItem as InstanceProfile;
        return new AutomationSettings
        {
            TargetProfileId = selectedProfile?.Id ?? string.Empty,
            RestartSchedulerEnabled = AutomationRestartEnabledCheckBox.IsChecked == true,
            BackupEnabled = AutomationBackupEnabledCheckBox.IsChecked == true,
            BackupBeforeShutdown = AutomationBackupBeforeShutdownCheckBox.IsChecked == true,
            BroadcastEnabled = AutomationBroadcastEnabledCheckBox.IsChecked == true,
            CommandEnabled = AutomationCommandEnabledCheckBox.IsChecked == true,
            ExportLogEnabled = AutomationExportEnabledCheckBox.IsChecked == true,
            ExportBeforeShutdown = AutomationExportBeforeShutdownCheckBox.IsChecked == true,
            ExportIncludeChat = AutomationExportIncludeChatCheckBox.IsChecked == true,
            ExportIncludeServerInfo = AutomationExportIncludeServerCheckBox.IsChecked == true,
            ClearCacheBeforeStart = AutomationClearCacheBeforeStartCheckBox.IsChecked == true,
            AutomationScriptsEnabled = AutomationScriptsEnabledCheckBox.IsChecked == true,
            ActionWindows = _automationActionWindowItems.Select(item => item.ToModel()).ToList(),
            BackupSchedules = _automationBackupScheduleItems.Select(item => item.ToModel()).ToList(),
            BackupRetentionCount = Math.Clamp(GetNumericValue(AutomationBackupRetentionNumericUpDown, 0), 0, 100_000),
            BackupTimes = [],
            BroadcastMessages = _automationBroadcastItems
                .Select(item => item.ToModel())
                .Where(item => !string.IsNullOrWhiteSpace(item.Message) || !string.IsNullOrWhiteSpace(item.Time))
                .ToList(),
            ScheduledCommands = _automationCommandItems
                .Select(item => item.ToModel())
                .Where(item => !string.IsNullOrWhiteSpace(item.Command) || !string.IsNullOrWhiteSpace(item.Time))
                .ToList(),
            ExportTimes = _automationExportTimeItems
                .Select(item => item.Time?.Trim() ?? string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            AutomationScripts = _automationScriptItems
                .Select(item => item.ToModel())
                .Where(item => !string.IsNullOrWhiteSpace(item.ScriptPath))
                .ToList()
        };
    }

    private async Task SaveAutomationAsync()
    {
        try
        {
            if (AutomationProfileComboBox.SelectedItem is not InstanceProfile profile)
            {
                SetAutomationStatus(T("请先选择档案。", "Select a profile first."));
                return;
            }

            var settings = CollectAutomationSettings();
            await _automationSettingsService.SaveAsync(profile, settings);
            await _automationService.ReloadAsync();
            RefreshAutomationConfigItems();
            SetAutomationStatus(T("自动化配置已保存。", "Automation settings saved."));
        }
        catch (Exception ex)
        {
            SetAutomationStatus(T($"自动化保存失败：{ex.Message}", $"Automation save failed: {ex.Message}"));
        }
    }

    private void SetAutomationStatus(string message, bool notify = true)
    {
        AutomationStatusTextBlock.Text = message;
        AutomationEditorStatusTextBlock.Text = message;
        if (notify)
        {
            ShowToast(message);
        }
    }

    private void SetModStatus(string message, bool notify = true)
    {
        ModStatusTextBlock.Text = message;
        if (notify)
        {
            ShowToast(message);
        }
    }

    private void SetAuthStatus(string message, bool notify = true)
    {
        AuthStatusTextBlock.Text = message;
        if (notify)
        {
            ShowToast(message);
        }
    }

    private void SetServerBridgeStatus(string message, bool notify = true)
    {
        ServerBridgeStatusTextBlock.Text = message;
        if (notify)
        {
            ShowToast(message);
        }
    }

    private void ShowAutomationList()
    {
        _editingAutomationProfileId = string.Empty;
        AutomationListPanel.IsVisible = true;
        AutomationEditorPanel.IsVisible = false;
        RefreshAutomationConfigItems();
    }

    private async Task ShowAutomationEditorAsync(InstanceProfile profile)
    {
        _editingAutomationProfileId = profile.Id;
        AutomationListPanel.IsVisible = false;
        AutomationEditorPanel.IsVisible = true;
        AutomationProfileComboBox.SelectedItem = _automationProfileItems.FirstOrDefault(item =>
            item.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase)) ?? profile;
        var settings = await _automationSettingsService.LoadAsync(profile);
        ApplyAutomationSettings(settings);
        SetAutomationStatus(T($"正在编辑自动化配置：{profile.Name}", $"Editing automation: {profile.Name}"), notify: false);
    }

    private void ShowAuthList()
    {
        _editingAuthProfileId = string.Empty;
        AuthListPanel.IsVisible = true;
        AuthEditorPanel.IsVisible = false;
        AuthClearButton.IsVisible = true;
        AuthBackButton.IsVisible = false;
        AuthSaveButton.IsVisible = false;
        AuthDeployButton.IsVisible = false;
        Grid.SetColumn(AuthRefreshButton, 1);
        RefreshAuthConfigItems();
    }

    private async Task ShowAuthEditorAsync(InstanceProfile profile)
    {
        _editingAuthProfileId = profile.Id;
        AuthListPanel.IsVisible = false;
        AuthEditorPanel.IsVisible = true;
        AuthClearButton.IsVisible = false;
        AuthBackButton.IsVisible = true;
        AuthSaveButton.IsVisible = true;
        AuthDeployButton.IsVisible = true;
        Grid.SetColumn(AuthRefreshButton, 3);
        AuthProfileComboBox.SelectedItem = _authProfileItems.FirstOrDefault(item =>
            item.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase)) ?? profile;
        await LoadAuthForProfileAsync(profile);
    }

    private void ShowServerBridgeList()
    {
        _editingServerBridgeProfileId = string.Empty;
        ServerBridgeListPanel.IsVisible = true;
        ServerBridgeEditorPanel.IsVisible = false;
        ServerBridgeClearButton.IsVisible = true;
        ServerBridgeBackButton.IsVisible = false;
        ServerBridgeSaveButton.IsVisible = false;
        ServerBridgeDeployButton.IsVisible = false;
        ServerBridgeTestButton.IsVisible = false;
        ServerBridgeRegenerateTokenButton.IsVisible = false;
        Grid.SetColumn(ServerBridgeRefreshButton, 1);
        RefreshServerBridgeConfigItems();
    }

    private async Task ShowServerBridgeEditorAsync(InstanceProfile profile)
    {
        _editingServerBridgeProfileId = profile.Id;
        ServerBridgeListPanel.IsVisible = false;
        ServerBridgeEditorPanel.IsVisible = true;
        ServerBridgeClearButton.IsVisible = false;
        ServerBridgeBackButton.IsVisible = true;
        ServerBridgeSaveButton.IsVisible = true;
        ServerBridgeDeployButton.IsVisible = true;
        ServerBridgeTestButton.IsVisible = true;
        ServerBridgeRegenerateTokenButton.IsVisible = true;
        Grid.SetColumn(ServerBridgeRefreshButton, 3);
        ServerBridgeProfileComboBox.SelectedItem = _serverBridgeProfileItems.FirstOrDefault(item =>
            item.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase)) ?? profile;
        await LoadServerBridgeForProfileAsync(profile);
    }

    private async Task RefreshServerBridgeProfilesAsync()
    {
        if (_isRefreshingServerBridge)
            return;

        _isRefreshingServerBridge = true;
        try
        {
            var profiles = _profileService.GetProfiles();
            var selectedProfileId = !string.IsNullOrWhiteSpace(_editingServerBridgeProfileId)
                ? _editingServerBridgeProfileId
                : ServerBridgeProfileComboBox.SelectedItem is InstanceProfile selectedProfile
                    ? selectedProfile.Id
                    : string.Empty;
            _serverBridgeProfileItems.Clear();
            foreach (var profile in profiles)
            {
                _serverBridgeProfileItems.Add(profile);
            }

            RefreshServerBridgeConfigItems(profiles);
            if (_serverBridgeProfileItems.Count == 0)
            {
                SetServerBridgeStatus(T("暂无档案，请先创建档案。", "No profile found. Create a profile first."), notify: false);
                return;
            }

            var target = _serverBridgeProfileItems.FirstOrDefault(profile =>
                !string.IsNullOrWhiteSpace(selectedProfileId) &&
                profile.Id.Equals(selectedProfileId, StringComparison.OrdinalIgnoreCase))
                ?? _serverBridgeProfileItems.FirstOrDefault();
            ServerBridgeProfileComboBox.SelectedItem = target;
            if (target is not null && ServerBridgeEditorPanel.IsVisible)
            {
                await LoadServerBridgeForProfileAsync(target);
            }
        }
        catch (Exception ex)
        {
            SetServerBridgeStatus(T($"服务器桥接加载失败：{ex.Message}", $"Server Bridge load failed: {ex.Message}"));
        }
        finally
        {
            _isRefreshingServerBridge = false;
        }
    }

    private async Task LoadServerBridgeForProfileAsync(InstanceProfile profile)
    {
        var settings = await _serverBridgeService.LoadSettingsAsync(profile);
        ApplyServerBridgeSettings(settings);
        var modEnabled = await _serverBridgeService.GetServerBridgeModEnabledAsync(profile);
        var runtime = await _serverBridgeService.GetRuntimeStatusAsync(profile);
        SetServerBridgeStatus(T(
            $"已加载服务器桥接配置，模组{(modEnabled ? "已启用" : "未启用或未部署")}；{runtime.Message}",
            $"Server Bridge settings loaded, mod {(modEnabled ? "enabled" : "disabled or not deployed")}; {runtime.Message}"), notify: false);
    }

    private void ApplyServerBridgeSettings(ServerBridgeSettings settings)
    {
        ServerBridgeEnabledCheckBox.IsChecked = settings.Enabled;
        ServerBridgePortNumericUpDown.Value = settings.Port;
        ServerBridgeTimeoutNumericUpDown.Value = settings.QueryTimeoutMilliseconds;
        ServerBridgeMaxLengthNumericUpDown.Value = settings.MaxCommandLength;
        ServerBridgeFallbackCheckBox.IsChecked = settings.AllowRelayFallback;
        ServerBridgeExtendedPlayersCheckBox.IsChecked = settings.IncludeExtendedPlayerInfo;
        ServerBridgeWorldDetailsCheckBox.IsChecked = settings.IncludeWorldDetails;
        ServerBridgePerformanceCheckBox.IsChecked = settings.IncludePerformanceInfo;
        ServerBridgeSensitiveFieldsCheckBox.IsChecked = settings.IncludeSensitiveFields;
        ServerBridgeEventTypesTextBox.Text = string.Join(", ", settings.EventTypes);
        ServerBridgeTokenTextBox.Text = settings.AccessToken;
    }

    private ServerBridgeSettings CollectServerBridgeSettings() => new()
    {
        Enabled = ServerBridgeEnabledCheckBox.IsChecked == true,
        Port = GetNumericValue(ServerBridgePortNumericUpDown, 19090),
        QueryTimeoutMilliseconds = GetNumericValue(ServerBridgeTimeoutNumericUpDown, 5000),
        MaxCommandLength = GetNumericValue(ServerBridgeMaxLengthNumericUpDown, 4096),
        AllowRelayFallback = ServerBridgeFallbackCheckBox.IsChecked != false,
        IncludeExtendedPlayerInfo = ServerBridgeExtendedPlayersCheckBox.IsChecked == true,
        IncludeWorldDetails = ServerBridgeWorldDetailsCheckBox.IsChecked == true,
        IncludePerformanceInfo = ServerBridgePerformanceCheckBox.IsChecked == true,
        IncludeSensitiveFields = ServerBridgeSensitiveFieldsCheckBox.IsChecked == true,
        EventTypes = (ServerBridgeEventTypesTextBox.Text ?? string.Empty).Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        AccessToken = ServerBridgeTokenTextBox.Text?.Trim() ?? string.Empty
    };

    private static ServerBridgeSettings BuildClearedServerBridgeSettings() => new()
    {
        Enabled = false,
        Port = 0,
        QueryTimeoutMilliseconds = 5000,
        MaxCommandLength = 4096,
        AllowRelayFallback = true,
        AccessToken = string.Empty
    };

    private static AutomationSettings BuildClearedAutomationSettings(string profileId)
    {
        return new AutomationSettings
        {
            TargetProfileId = profileId,
            RestartSchedulerEnabled = false,
            BackupEnabled = false,
            BroadcastEnabled = false,
            CommandEnabled = false,
            ExportLogEnabled = false,
            BackupBeforeShutdown = false,
            ExportBeforeShutdown = false,
            ExportIncludeChat = false,
            ExportIncludeServerInfo = false,
            ClearCacheBeforeStart = false,
            AutomationScriptsEnabled = false,
            AutomationScripts = [],
            ActionWindows = [],
            BackupSchedules = [],
            BackupRetentionCount = 0,
            BackupTimes = [],
            BroadcastMessages = [],
            ScheduledCommands = [],
            ExportTimes = []
        };
    }

    private static RobotIntegrationSettings BuildClearedRobotSettings()
    {
        return new RobotIntegrationSettings
        {
            OneBotWsUrl = "ws://127.0.0.1:3001/",
            AccessToken = string.Empty,
            BoundGroupIdsText = string.Empty,
            ReconnectIntervalSec = 5,
            DatabasePath = string.Empty,
            DefaultEncoding = "utf-8",
            FallbackEncoding = "gbk",
            SuperUsersText = string.Empty,
            CustomCommands = [],
            TeleportPoints = []
        };
    }

    private static ServerAuthSettings BuildClearedAuthSettings()
    {
        return new ServerAuthSettings
        {
            Enabled = false,
            LoginTimeoutSeconds = 60,
            RememberSessionMinutes = 0,
            Discourse = new ServerAuthDiscourseSettings
            {
                Enabled = false,
                BaseUrl = string.Empty,
                SharedSecret = string.Empty,
                PublicCallbackBaseUrl = "http://127.0.0.1:18092/",
                ListenPrefix = "http://127.0.0.1:18092/"
            },
            OAuth2 = new ServerAuthOAuth2Settings
            {
                Enabled = false,
                DiscoveryUrl = string.Empty,
                AuthorizationEndpoint = string.Empty,
                TokenEndpoint = string.Empty,
                UserInfoEndpoint = string.Empty,
                ClientId = string.Empty,
                ClientSecret = string.Empty,
                Scope = "openid profile email",
                PublicCallbackBaseUrl = "http://127.0.0.1:18092/",
                ListenPrefix = "http://127.0.0.1:18092/",
                UserIdClaim = "sub",
                UsernameClaim = "preferred_username",
                DisplayNameClaim = "name",
                EmailClaim = "email"
            }
        };
    }

    private async Task RefreshModsAsync()
    {
        if (_isRefreshingMods)
        {
            return;
        }

        _isRefreshingMods = true;
        try
        {
            var profiles = _profileService.GetProfiles();
            var selectedProfileId = ModProfileComboBox.SelectedItem is InstanceProfile selectedProfile
                ? selectedProfile.Id
                : string.Empty;
            _modProfileItems.Clear();
            foreach (var profile in profiles)
            {
                _modProfileItems.Add(profile);
            }

            ModProfileComboBox.ItemsSource = _modProfileItems;
            if (_modProfileItems.Count > 0)
            {
                ModProfileComboBox.SelectedItem = _modProfileItems.FirstOrDefault(profile =>
                    !string.IsNullOrWhiteSpace(selectedProfileId) &&
                    profile.Id.Equals(selectedProfileId, StringComparison.OrdinalIgnoreCase))
                    ?? _modProfileItems.FirstOrDefault();
            }

            await LoadModsForSelectedProfileAsync();
        }
        catch (Exception ex)
        {
            SetModStatus(T($"模组加载失败：{ex.Message}", $"Mod load failed: {ex.Message}"));
        }
        finally
        {
            _isRefreshingMods = false;
        }
    }

    private async Task LoadModsForSelectedProfileAsync()
    {
        if (ModProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            _modItems.Clear();
            UpdateModSelectAllState();
            UpdateCheckModButtonText();
            SetModStatus(T("暂无档案，请先创建档案。", "No profile found. Create a profile first."), notify: false);
            return;
        }

        var mods = await _instanceModService.GetModsAsync(profile);
        var cachedChecks = _preferencesService.Load().ModUpdateChecks.Values.ToList();
        _modItems.Clear();
        foreach (var mod in mods)
        {
            _modItems.Add(ModListItem.FromModel(
                mod,
                _isChinese,
                FindCachedModUpdate(cachedChecks, profile.Id, mod.ModId, mod.Version)));
        }
        UpdateModSelectAllState();
        UpdateCheckModButtonText(profile.Id);

        var enabledCount = mods.Count(static mod => !mod.IsDisabled);
        var disabledCount = mods.Count - enabledCount;
        SetModStatus(T(
            $"已加载 {mods.Count} 个模组，启用 {enabledCount} 个，关闭 {disabledCount} 个。",
            $"Loaded {mods.Count} mods, {enabledCount} enabled, {disabledCount} disabled."), notify: false);
    }

    private void UpdateCheckModButtonText(string? profileId = null)
    {
        if (CheckModUpdatesButton is null)
            return;

        profileId ??= (ModProfileComboBox.SelectedItem as InstanceProfile)?.Id;
        var lastCheckedAt = string.IsNullOrWhiteSpace(profileId)
            ? null
            : _preferencesService.Load().ModUpdateChecks.Values
                .Where(entry => entry.ProfileId.Equals(profileId, StringComparison.OrdinalIgnoreCase))
                .Select(static entry => (DateTimeOffset?)entry.CheckedAtUtc)
                .OrderByDescending(static value => value)
                .FirstOrDefault();

        if (lastCheckedAt is null || lastCheckedAt.Value == default)
        {
            CheckModUpdatesButton.Content = T("检查更新", "Check updates");
            return;
        }

        var dateText = lastCheckedAt.Value.ToLocalTime().ToString("yyyy/M/d", CultureInfo.InvariantCulture);
        CheckModUpdatesButton.Content = T(
            $"检查更新（{dateText}）",
            $"Check updates ({dateText})");
    }

    private static ModUpdateCheckCacheEntry? FindCachedModUpdate(
        IEnumerable<ModUpdateCheckCacheEntry> entries,
        string profileId,
        string modId,
        string version)
    {
        return entries
            .Where(entry =>
                entry.ProfileId.Equals(profileId, StringComparison.OrdinalIgnoreCase) &&
                entry.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase) &&
                entry.CurrentVersion.Equals(version, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static entry => entry.CheckedAtUtc)
            .FirstOrDefault();
    }

    private void PersistModUpdateChecks(
        InstanceProfile profile,
        IEnumerable<(ModListItem Item, ModUpdateCheckResult? Result)> entries,
        DateTimeOffset checkedAtUtc)
    {
        var preferences = _preferencesService.Load();
        preferences.ModUpdateChecks ??= new Dictionary<string, ModUpdateCheckCacheEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var model = ModListItem.ToModel(entry.Item);
            foreach (var key in preferences.ModUpdateChecks
                         .Where(pair =>
                             pair.Value.ProfileId.Equals(profile.Id, StringComparison.OrdinalIgnoreCase) &&
                             pair.Value.ModId.Equals(model.ModId, StringComparison.OrdinalIgnoreCase))
                         .Select(static pair => pair.Key)
                         .ToList())
            {
                preferences.ModUpdateChecks.Remove(key);
            }

            var status = entry.Result is null
                ? "Failed"
                : entry.Result.IsUpdateAvailable
                    ? "Available"
                    : "Latest";
            preferences.ModUpdateChecks[BuildModUpdateCacheKey(profile.Id, model.ModId)] = new ModUpdateCheckCacheEntry
            {
                ProfileId = profile.Id,
                ModId = model.ModId,
                CurrentVersion = model.Version,
                Status = status,
                Result = entry.Result,
                CheckedAtUtc = checkedAtUtc
            };
        }

        try
        {
            _preferencesService.Save(preferences);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist mod update check results for profile {ProfileId}", profile.Id);
        }

        UpdateCheckModButtonText(profile.Id);
    }

    private static string BuildModUpdateCacheKey(string profileId, string modId)
    {
        return $"{profileId.Trim()}|{modId.Trim()}";
    }

    private async Task RefreshAuthProfilesAsync()
    {
        if (_isRefreshingAuth)
        {
            return;
        }

        _isRefreshingAuth = true;
        try
        {
            var profiles = _profileService.GetProfiles();
            var selectedProfileId = !string.IsNullOrWhiteSpace(_editingAuthProfileId)
                ? _editingAuthProfileId
                : AuthProfileComboBox.SelectedItem is InstanceProfile selectedProfile
                    ? selectedProfile.Id
                    : string.Empty;
            _authProfileItems.Clear();
            foreach (var profile in profiles)
            {
                _authProfileItems.Add(profile);
            }

            RefreshAuthConfigItems(profiles);
            AuthProfileComboBox.ItemsSource = _authProfileItems;
            if (_authProfileItems.Count == 0)
            {
                _authPlayerSourceItems.Clear();
                _authPlayerItems.Clear();
                SetAuthStatus(T("暂无档案，请先创建档案。", "No profile found. Create a profile first."), notify: false);
                return;
            }

            var target = _authProfileItems.FirstOrDefault(profile =>
                !string.IsNullOrWhiteSpace(selectedProfileId) &&
                profile.Id.Equals(selectedProfileId, StringComparison.OrdinalIgnoreCase))
                ?? _authProfileItems.FirstOrDefault();
            AuthProfileComboBox.SelectedItem = target;
            if (target is not null && AuthEditorPanel.IsVisible)
            {
                await LoadAuthForProfileAsync(target);
            }
        }
        catch (Exception ex)
        {
            SetAuthStatus(T($"认证加载失败：{ex.Message}", $"Auth load failed: {ex.Message}"));
        }
        finally
        {
            _isRefreshingAuth = false;
        }
    }

    private async Task LoadAuthForProfileAsync(InstanceProfile profile)
    {
        var settings = await _serverAuthService.LoadSettingsAsync(profile);
        ApplyAuthSettings(settings);
        await LoadAuthPlayersAsync(profile);
        var authModEnabled = await _serverAuthService.GetAuthModEnabledAsync(profile);
        SetAuthStatus(T(
            $"已加载认证配置，认证模组{(authModEnabled ? "已启用" : "未启用或未部署")}。",
            $"Auth settings loaded, auth mod {(authModEnabled ? "enabled" : "disabled or missing")}."), notify: false);
    }

    private void ApplyAuthSettings(ServerAuthSettings settings)
    {
        AuthEnabledCheckBox.IsChecked = settings.Enabled;
        AuthLoginTimeoutNumericUpDown.Value = settings.LoginTimeoutSeconds;
        AuthRememberSessionNumericUpDown.Value = settings.RememberSessionMinutes;
        AuthDiscourseEnabledCheckBox.IsChecked = settings.Discourse.Enabled && !settings.OAuth2.Enabled;
        AuthDiscourseBaseUrlTextBox.Text = settings.Discourse.BaseUrl;
        AuthDiscourseSecretTextBox.Text = settings.Discourse.SharedSecret;
        AuthDiscoursePublicCallbackTextBox.Text = settings.Discourse.PublicCallbackBaseUrl;
        AuthDiscourseListenPrefixTextBox.Text = settings.Discourse.ListenPrefix;
        AuthOAuth2EnabledCheckBox.IsChecked = settings.OAuth2.Enabled;
        AuthOAuth2DiscoveryUrlTextBox.Text = settings.OAuth2.DiscoveryUrl;
        AuthOAuth2AuthorizationEndpointTextBox.Text = settings.OAuth2.AuthorizationEndpoint;
        AuthOAuth2TokenEndpointTextBox.Text = settings.OAuth2.TokenEndpoint;
        AuthOAuth2UserInfoEndpointTextBox.Text = settings.OAuth2.UserInfoEndpoint;
        AuthOAuth2ClientIdTextBox.Text = settings.OAuth2.ClientId;
        AuthOAuth2ClientSecretTextBox.Text = settings.OAuth2.ClientSecret;
        AuthOAuth2ScopeTextBox.Text = settings.OAuth2.Scope;
        AuthOAuth2PublicCallbackTextBox.Text = settings.OAuth2.PublicCallbackBaseUrl;
        AuthOAuth2ListenPrefixTextBox.Text = settings.OAuth2.ListenPrefix;
        AuthOAuth2UserIdClaimTextBox.Text = settings.OAuth2.UserIdClaim;
        AuthOAuth2UsernameClaimTextBox.Text = settings.OAuth2.UsernameClaim;
        AuthOAuth2DisplayNameClaimTextBox.Text = settings.OAuth2.DisplayNameClaim;
        AuthOAuth2EmailClaimTextBox.Text = settings.OAuth2.EmailClaim;
    }

    private ServerAuthSettings CollectAuthSettings()
    {
        return new ServerAuthSettings
        {
            Enabled = AuthEnabledCheckBox.IsChecked == true,
            LoginTimeoutSeconds = GetNumericValue(AuthLoginTimeoutNumericUpDown, 60),
            RememberSessionMinutes = GetNumericValue(AuthRememberSessionNumericUpDown, 30),
            Discourse = new ServerAuthDiscourseSettings
            {
                Enabled = AuthDiscourseEnabledCheckBox.IsChecked == true,
                BaseUrl = AuthDiscourseBaseUrlTextBox.Text?.Trim() ?? string.Empty,
                SharedSecret = AuthDiscourseSecretTextBox.Text?.Trim() ?? string.Empty,
                PublicCallbackBaseUrl = AuthDiscoursePublicCallbackTextBox.Text?.Trim() ?? string.Empty,
                ListenPrefix = AuthDiscourseListenPrefixTextBox.Text?.Trim() ?? string.Empty
            },
            OAuth2 = new ServerAuthOAuth2Settings
            {
                Enabled = AuthOAuth2EnabledCheckBox.IsChecked == true,
                DiscoveryUrl = AuthOAuth2DiscoveryUrlTextBox.Text?.Trim() ?? string.Empty,
                AuthorizationEndpoint = AuthOAuth2AuthorizationEndpointTextBox.Text?.Trim() ?? string.Empty,
                TokenEndpoint = AuthOAuth2TokenEndpointTextBox.Text?.Trim() ?? string.Empty,
                UserInfoEndpoint = AuthOAuth2UserInfoEndpointTextBox.Text?.Trim() ?? string.Empty,
                ClientId = AuthOAuth2ClientIdTextBox.Text?.Trim() ?? string.Empty,
                ClientSecret = AuthOAuth2ClientSecretTextBox.Text?.Trim() ?? string.Empty,
                Scope = AuthOAuth2ScopeTextBox.Text?.Trim() ?? string.Empty,
                PublicCallbackBaseUrl = AuthOAuth2PublicCallbackTextBox.Text?.Trim() ?? string.Empty,
                ListenPrefix = AuthOAuth2ListenPrefixTextBox.Text?.Trim() ?? string.Empty,
                UserIdClaim = AuthOAuth2UserIdClaimTextBox.Text?.Trim() ?? string.Empty,
                UsernameClaim = AuthOAuth2UsernameClaimTextBox.Text?.Trim() ?? string.Empty,
                DisplayNameClaim = AuthOAuth2DisplayNameClaimTextBox.Text?.Trim() ?? string.Empty,
                EmailClaim = AuthOAuth2EmailClaimTextBox.Text?.Trim() ?? string.Empty
            }
        };
    }

    private async Task LoadAuthPlayersAsync(InstanceProfile profile)
    {
        var players = await _serverAuthService.GetPlayersAsync(profile);
        _authPlayerSourceItems.Clear();
        _authPlayerSourceItems.AddRange(players.Select(player => AuthPlayerListItem.FromModel(player, _isChinese)));
        ApplyAuthPlayerSearch();
    }

    private void ApplyAuthPlayerSearch()
    {
        var keyword = AuthPlayerSearchTextBox.Text?.Trim() ?? string.Empty;
        _authPlayerItems.Clear();

        foreach (var player in _authPlayerSourceItems)
        {
            if (string.IsNullOrWhiteSpace(keyword) ||
                player.PlayerName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                player.PlayerUid.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                player.ExternalUsername.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                _authPlayerItems.Add(player);
            }
        }
    }

    private void RefreshLaunchButtonSummary()
    {
        var statuses = _serverProcessService.GetCachedStatuses();
        var runningStatuses = statuses.Where(static status => status.IsRunning).ToList();
        var runningIds = runningStatuses
            .Select(static status => status.ProfileId ?? string.Empty)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedIds = LoadLaunchProfileIds();
        var pendingCount = selectedIds.Count(id => !runningIds.Contains(id));
        if (pendingCount > 0)
        {
            LaunchSelectionSummaryTextBlock.Text = runningStatuses.Count > 0
                ? T($"运行中 {runningStatuses.Count} 个 | 待启动 {pendingCount} 个", $"{runningStatuses.Count} running | {pendingCount} pending")
                : T($"准备启动 {pendingCount} 个", $"{pendingCount} selected");
            LaunchSelectionPillHost.Classes.Set("expanded", false);
            return;
        }

        if (runningStatuses.Count > 0)
        {
            LaunchSelectionSummaryTextBlock.Text = T($"运行中 {runningStatuses.Count} 个 | 点击停止", $"{runningStatuses.Count} running | Click to stop");
            LaunchSelectionPillHost.Classes.Set("expanded", false);
            return;
        }

        if (_launchTargetItems.Count == 0)
        {
            LaunchSelectionSummaryTextBlock.Text = T("未选择服务器", "No server selected");
            LaunchSelectionPillHost.Classes.Set("expanded", false);
            return;
        }

        LaunchSelectionSummaryTextBlock.Text = T($"准备启动 {_launchTargetItems.Count} 个", $"{_launchTargetItems.Count} selected");
        LaunchSelectionPillHost.Classes.Set("expanded", false);
    }

    private bool HasPendingLaunchTargets(IReadOnlyList<ServerRuntimeStatus> statuses)
    {
        var selectedIds = LoadLaunchProfileIds();
        if (selectedIds.Count == 0)
        {
            return false;
        }

        var runningIds = statuses
            .Where(static status => status.IsRunning)
            .Select(static status => status.ProfileId ?? string.Empty)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return selectedIds.Any(id => !runningIds.Contains(id));
    }

    private void RefreshAuthConfigItems(IReadOnlyList<InstanceProfile>? profiles = null)
    {
        var profileList = profiles ?? _profileService.GetProfiles();
        _authConfigItems.Clear();
        foreach (var profile in profileList)
        {
            _authConfigItems.Add(ProfileConfigListItem.FromPath(
                profile,
                GetAuthSettingsPath(profile)));
        }
    }

    private void RefreshAutomationConfigItems(IReadOnlyList<InstanceProfile>? profiles = null)
    {
        var profileList = profiles ?? _profileService.GetProfiles();
        _automationConfigItems.Clear();
        foreach (var profile in profileList)
        {
            _automationConfigItems.Add(ProfileConfigListItem.FromPath(
                profile,
                _automationSettingsService.GetSettingsPath(profile)));
        }
    }

    private async Task RefreshSavesAsync()
    {
        if (_isRefreshingSaves)
        {
            return;
        }

        _isRefreshingSaves = true;
        try
        {
            var selectedProfile = SaveProfileComboBox.SelectedItem;
            var profiles = _profileService.GetProfiles();
            var preferences = _preferencesService.Load();
            var lockedSavePath = NormalizeFullPath(preferences.DefaultLaunchSaveFile);
            var saveProfileItems = new List<object> { T("全部档案", "All profiles") };
            saveProfileItems.AddRange(profiles);
            SaveProfileComboBox.ItemsSource = saveProfileItems;

            if (selectedProfile is InstanceProfile selectedInstance)
            {
                SaveProfileComboBox.SelectedItem = profiles.FirstOrDefault(profile => profile.Id == selectedInstance.Id) ?? saveProfileItems[0];
            }
            else if (SaveProfileComboBox.SelectedIndex < 0)
            {
                SaveProfileComboBox.SelectedIndex = 0;
            }

            InstanceProfile? filter = SaveProfileComboBox.SelectedItem as InstanceProfile;
            var saves = await _saveService.GetSavesAsync(filter);
            _saveItems.Clear();
            foreach (var save in saves)
            {
                var profileForSave = profiles.FirstOrDefault(profile => profile.Id.Equals(save.ProfileId, StringComparison.OrdinalIgnoreCase));
                var activeSavePath = NormalizeFullPath(profileForSave?.ActiveSaveFile);
                var isLocked = !string.IsNullOrWhiteSpace(activeSavePath) &&
                               NormalizeFullPath(save.FullPath).Equals(activeSavePath, StringComparison.OrdinalIgnoreCase);
                if (!isLocked && !string.IsNullOrWhiteSpace(lockedSavePath))
                {
                    var launchIds = SplitProfileIds(preferences.DefaultLaunchProfileIds, preferences.DefaultLaunchProfileId);
                    isLocked = launchIds.Contains(save.ProfileId) &&
                               NormalizeFullPath(save.FullPath).Equals(lockedSavePath, StringComparison.OrdinalIgnoreCase);
                }
                _saveItems.Add(SaveListItem.FromSave(
                    save,
                    isLocked,
                    T("默认启动", "Default"),
                    T("锁定默认", "Set default")));
            }

            RefreshLaunchButtonSummary();
        }
        finally
        {
            _isRefreshingSaves = false;
        }
    }

    private async Task RefreshDownloadVersionsAsync(bool forceReload)
    {
        if (_downloadCatalogLoaded && !forceReload)
        {
            RebuildDownloadVersionItems();
            return;
        }

        SetDownloadStatus(T("正在加载服务端版本列表...", "Loading server versions..."), notify: false);
        try
        {
            _catalogEntries.Clear();
            _catalogEntries.AddRange(await _serverPackageService.GetServerDownloadEntriesAsync());
            _downloadCatalogLoaded = true;
            RebuildDownloadVersionItems();
            SetDownloadStatus(
                T($"已加载 {_catalogEntries.Count} 个服务端版本。", $"Loaded {_catalogEntries.Count} server versions."),
                notify: forceReload);
        }
        catch (Exception ex)
        {
            _downloadCatalogLoaded = false;
            _catalogEntries.Clear();
            _downloadVersionItems.Clear();
            SetDownloadStatus(T($"加载失败：{ex.Message}", $"Load failed: {ex.Message}"));
        }
    }

    private void RebuildDownloadVersionItems()
    {
        var preferences = _preferencesService.Load();
        var installedVersions = _profileService.GetInstalledVersions().ToHashSet(StringComparer.OrdinalIgnoreCase);
        _downloadVersionItems.Clear();
        var searchKeyword = DownloadVersionSearchTextBox.Text?.Trim() ?? string.Empty;
        foreach (var entry in _catalogEntries)
        {
            if (!string.IsNullOrWhiteSpace(searchKeyword)
                && !entry.Version.Contains(searchKeyword, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isDownloaded = IsCatalogEntryDownloaded(entry, preferences.ServerDirectory, installedVersions);
            _downloadVersionItems.Add(new DownloadVersionListItem(
                entry,
                entry.Version,
                isDownloaded,
                T("已下载", "Downloaded"),
                T("下载", "Download")));
        }
    }

    private void OnDownloadVersionSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        RebuildDownloadVersionItems();
    }

    private void SetDownloadStatus(string message, bool notify = true)
    {
        DownloadStatusTextBlock.Text = message;
        if (notify)
        {
            ShowToast(message);
        }
    }

    private void SelectTab(MainTab tab)
    {
        var previousTab = _selectedTab;
        _selectedTab = tab;
        _logsNavSelected = false;

        HomePanel.IsVisible = false;
        NonHomePanelHost.IsVisible = true;
        MonitorPanel.IsVisible = tab == MainTab.Monitor;
        ConsolePanel.IsVisible = tab == MainTab.Console;
        InstanceManagePanel.IsVisible = tab == MainTab.InstanceManage;
        SettingsPanel.IsVisible = tab == MainTab.Settings;
        ConnectionPanel.IsVisible = tab == MainTab.Connection;

        RefreshSidebarSelection();

        if (tab == MainTab.Monitor)
        {
            RenderSelectedMetricChart();
        }

        if (tab == MainTab.Console)
        {
            RefreshConsoleServerItems(_serverProcessService.GetCachedStatuses());
            RefreshConsoleText();
        }

        if (tab == MainTab.Connection)
        {
            RefreshConnectionSettingsEditor();
            RefreshConnectionRuntimeStatus();
        }

        ShowNonHomePanel(previousTab == MainTab.Home);
        RequestStaticUiTranslations();
    }

    private void ShowNonHomePanel(bool animate)
    {
        if (!animate)
        {
            NonHomePanelHost.RenderTransform = TransformOperations.Parse("translate(0px,0px)");
            return;
        }

        var offset = Math.Max(MainContentHost.Bounds.Height, NonHomePanelHost.Bounds.Height);
        if (offset < 1)
        {
            offset = 480;
        }

        var offsetText = offset.ToString(CultureInfo.InvariantCulture);
        NonHomePanelHost.RenderTransform = TransformOperations.Parse($"translate(0px,{offsetText}px)");
        Dispatcher.UIThread.Post(() =>
        {
            if (_selectedTab != MainTab.Home)
            {
                NonHomePanelHost.RenderTransform = TransformOperations.Parse("translate(0px,0px)");
            }
        }, DispatcherPriority.Render);
    }

    private void SelectMetric(HomeMetric metric)
    {
        _selectedMetric = metric;
        RenderSelectedMetricChart();
    }

    private void SelectInstanceManageTab(InstanceManageTab tab)
    {
        _selectedInstanceManageTab = tab;
        ProfilesPanel.IsVisible = tab == InstanceManageTab.Profiles;
        ConfigPanel.IsVisible = tab == InstanceManageTab.Config;
        SavesPanel.IsVisible = tab == InstanceManageTab.Saves;
        AutomationPanel.IsVisible = tab == InstanceManageTab.Automation;
        ModsPanel.IsVisible = tab == InstanceManageTab.Mods;
        DownloadVersionsPanel.IsVisible = tab == InstanceManageTab.DownloadVersions;
        LogsPanel.IsVisible = tab == InstanceManageTab.Logs;
        ServerBridgePanel.IsVisible = tab == InstanceManageTab.ServerBridge;
        ServerMapPanel.IsVisible = tab == InstanceManageTab.ServerMap;
        RefreshSidebarSelection();

        if (tab == InstanceManageTab.Config)
        {
            if (string.IsNullOrWhiteSpace(_pendingConfigLoadProfileId))
            {
                _ = RefreshConfigProfilesAsync();
            }
        }
        else if (tab == InstanceManageTab.Automation)
        {
            ShowAutomationList();
            _ = RefreshAutomationAsync();
        }
        else if (tab == InstanceManageTab.Mods)
        {
            _ = RefreshModsAsync();
        }
        else if (tab == InstanceManageTab.DownloadVersions)
        {
            _ = RefreshDownloadVersionsAsync(forceReload: false);
        }
        else if (tab == InstanceManageTab.Logs)
        {
            RefreshLogItems();
        }
        else if (tab == InstanceManageTab.ServerBridge)
        {
            ShowServerBridgeList();
            _ = RefreshServerBridgeProfilesAsync();
        }
        else if (tab == InstanceManageTab.ServerMap)
        {
            ShowServerMapList();
            _ = RefreshServerMapProfilesAsync();
        }

        RequestStaticUiTranslations();
    }

    private void SelectSettingsTab(SettingsTab tab)
    {
        _selectedSettingsTab = tab;
        RefreshSidebarSelection();
        var isServer = tab == SettingsTab.Server;
        var isAppearance = tab == SettingsTab.Appearance;
        var isNetwork = tab == SettingsTab.Network;
        var isAdvanced = tab == SettingsTab.Advanced;
        var isAbout = tab == SettingsTab.About;
        var isSponsors = tab == SettingsTab.Sponsors;
        var isContributors = tab == SettingsTab.Contributors;
        SettingsServerPanel.IsVisible = isServer;
        SettingsAppearancePanel.IsVisible = isAppearance;
        SettingsNetworkPanel.IsVisible = isNetwork;
        SettingsAdvancedPanel.IsVisible = isAdvanced;
        SettingsAboutPanel.IsVisible = isAbout;
        SettingsSponsorsPanel.IsVisible = isSponsors;
        SettingsContributorsPanel.IsVisible = isContributors;
        SettingsBlankPanel.IsVisible = !isServer &&
                                       !isAppearance &&
                                       !isNetwork &&
                                       !isAdvanced &&
                                       !isAbout &&
                                       !isSponsors &&
                                       !isContributors;

        if (isServer)
        {
            RefreshServerSettingsEditor();
        }
        else if (isAppearance)
        {
            RefreshAppearanceSettingsEditor();
        }
        else if (isNetwork)
        {
            RefreshNetworkSettingsEditor();
        }
        else if (isAbout)
        {
            LoadAboutIntroduction();
        }
        else if (isSponsors)
        {
            if (!_sponsorsLoaded)
            {
                _ = RefreshSponsorsAsync();
            }
        }
        else if (isContributors && !_contributorsLoaded)
        {
            _ = RefreshContributorsAsync();
        }

        RequestStaticUiTranslations();
    }

    private void SelectConnectionTab(ConnectionTab tab)
    {
        _selectedConnectionTab = tab;
        ConnectionFrpPanel.IsVisible = tab == ConnectionTab.Frp;
        ConnectionEasyTierPanel.IsVisible = tab == ConnectionTab.EasyTier;
        ConnectionRobotPanel.IsVisible = tab == ConnectionTab.Robot;
        ConnectionDiscordPanel.IsVisible = tab == ConnectionTab.Discord;
        ConnectionGatewayPanel.IsVisible = tab == ConnectionTab.Gateway;
        ConnectionAuthPanel.IsVisible = tab == ConnectionTab.Auth;
        RefreshSidebarSelection();
        RefreshConnectionSettingsEditor();
        RefreshConnectionRuntimeStatus();
        if (tab == ConnectionTab.Discord)
        {
            ConnectionStatusTextBlock.Text = string.Empty;
            ConnectionStatusTextBlock.IsVisible = false;
        }
        if (tab == ConnectionTab.Robot)
        {
            RefreshRobotProfileItems();
        }
        if (tab == ConnectionTab.Discord) ApplyDiscordSettings(_preferencesService.Load().Discord);

        if (tab == ConnectionTab.Auth)
        {
            ShowAuthList();
            _ = RefreshAuthProfilesAsync();
        }

        if (tab == ConnectionTab.Gateway)
        {
            _ = RefreshGatewayStatusAsync();
        }

        RequestStaticUiTranslations();
    }

    private void RefreshSidebarSelection()
    {
        SetSelectedClass(MonitorNavButton, !_logsNavSelected && _selectedTab == MainTab.Monitor);
        SetSelectedClass(ConsoleNavButton, !_logsNavSelected && _selectedTab == MainTab.Console);
        SetSelectedClass(ProfilesTabButton, !_logsNavSelected && _selectedTab == MainTab.InstanceManage && _selectedInstanceManageTab == InstanceManageTab.Profiles);
        SetSelectedClass(ConfigTabButton, false);
        SetSelectedClass(SavesTabButton, !_logsNavSelected && _selectedTab == MainTab.InstanceManage && _selectedInstanceManageTab == InstanceManageTab.Saves);
        SetSelectedClass(AutomationTabButton, !_logsNavSelected && _selectedTab == MainTab.InstanceManage && _selectedInstanceManageTab == InstanceManageTab.Automation);
        SetSelectedClass(ModsTabButton, !_logsNavSelected && _selectedTab == MainTab.InstanceManage && _selectedInstanceManageTab == InstanceManageTab.Mods);
        SetSelectedClass(DownloadVersionsTabButton, false);
        SetSelectedClass(DownloadVersionsNavButton, !_logsNavSelected && _selectedTab == MainTab.InstanceManage && _selectedInstanceManageTab == InstanceManageTab.DownloadVersions);
        SetSelectedClass(ConnectionAuthTabButton, !_logsNavSelected && _selectedTab == MainTab.Connection && _selectedConnectionTab == ConnectionTab.Auth);
        SetSelectedClass(ServerBridgeTabButton, !_logsNavSelected && _selectedTab == MainTab.InstanceManage && _selectedInstanceManageTab == InstanceManageTab.ServerBridge);
        SetSelectedClass(LogsNavButton, _logsNavSelected);
        SetSelectedClass(ConnectionFrpTabButton, !_logsNavSelected && _selectedTab == MainTab.Connection && _selectedConnectionTab == ConnectionTab.Frp);
        SetSelectedClass(ConnectionEasyTierTabButton, !_logsNavSelected && _selectedTab == MainTab.Connection && _selectedConnectionTab == ConnectionTab.EasyTier);
        SetSelectedClass(ConnectionGatewayTabButton, !_logsNavSelected && _selectedTab == MainTab.Connection && _selectedConnectionTab == ConnectionTab.Gateway);
        SetSelectedClass(ConnectionRobotTabButton, !_logsNavSelected && _selectedTab == MainTab.Connection && _selectedConnectionTab == ConnectionTab.Robot);
        SetSelectedClass(ConnectionDiscordTabButton, !_logsNavSelected && _selectedTab == MainTab.Connection && _selectedConnectionTab == ConnectionTab.Discord);
        SetSelectedClass(ServerMapTabButton, !_logsNavSelected && _selectedTab == MainTab.InstanceManage && _selectedInstanceManageTab == InstanceManageTab.ServerMap);
        SetSelectedClass(ServerSettingsTabButton, !_logsNavSelected && _selectedTab == MainTab.Settings && _selectedSettingsTab == SettingsTab.Server);
        SetSelectedClass(AppearanceSettingsTabButton, !_logsNavSelected && _selectedTab == MainTab.Settings && _selectedSettingsTab == SettingsTab.Appearance);
        SetSelectedClass(NetworkSettingsTabButton, !_logsNavSelected && _selectedTab == MainTab.Settings && _selectedSettingsTab == SettingsTab.Network);
        SetSelectedClass(AdvancedSettingsTabButton, !_logsNavSelected && _selectedTab == MainTab.Settings && _selectedSettingsTab == SettingsTab.Advanced);
        SetSelectedClass(AboutSettingsTabButton, !_logsNavSelected && _selectedTab == MainTab.Settings && _selectedSettingsTab == SettingsTab.About);
        SetSelectedClass(SponsorsSettingsTabButton, !_logsNavSelected && _selectedTab == MainTab.Settings && _selectedSettingsTab == SettingsTab.Sponsors);
        SetSelectedClass(ContributorsSettingsTabButton, !_logsNavSelected && _selectedTab == MainTab.Settings && _selectedSettingsTab == SettingsTab.Contributors);
    }

    private void RegisterAutoSaveHandlers()
    {
        SettingsWorkspaceDirectoryTextBox.LostFocus += OnServerSettingsAutoSaveChanged;
        SettingsQuickCommandsTextBox.LostFocus += OnServerSettingsAutoSaveChanged;
        SettingsSaveCompressionLevelNumericUpDown.LostFocus += OnServerSettingsAutoSaveChanged;
        SettingsSaveCompressionPathTextBox.LostFocus += OnServerSettingsAutoSaveChanged;

        foreach (var check in new[]
                 {
                     SettingsStartWithWindowsCheckBox,
                     SettingsCloseToTrayCheckBox,
                     SettingsStartHiddenCheckBox,
                     SettingsAutoStartServerCheckBox,
                     SettingsAutoRestartServerAfterCrashCheckBox,
                     SettingsAutoStartRobotCheckBox,
                     SettingsAutoStartFrpCheckBox,
                     SettingsAutoStartThirdPartyFrpcCheckBox,
                     SettingsAutoStartEasyTierCheckBox,
                     SettingsAutoStartGatewayCheckBox,
                     SettingsSaveCompressionEnabledCheckBox,
                     SettingsSaveCompressionDeleteSourceCheckBox
                 })
        {
            check.IsCheckedChanged += OnServerSettingsAutoSaveChanged;
        }

        SettingsSaveCompressionUpdateModeComboBox.SelectionChanged += OnServerSettingsAutoSaveChanged;

        SettingsThirdPartyServerTextBox.LostFocus += OnNetworkSettingsAutoSaveChanged;
        SettingsDownloadChunkCountTextBox.LostFocus += OnNetworkSettingsAutoSaveChanged;
        SettingsDownloadThreadCountTextBox.LostFocus += OnNetworkSettingsAutoSaveChanged;
        SettingsChunkedDownloadToggleSwitch.IsCheckedChanged += OnNetworkSettingsAutoSaveChanged;
        SettingsAutoCheckUpdatesToggleSwitch.IsCheckedChanged += OnNetworkSettingsAutoSaveChanged;

        ConnectionFrpCommandTextBox.LostFocus += OnFrpAutoSaveChanged;
        ConnectionThirdPartyFrpcCommandTextBox.LostFocus += OnFrpAutoSaveChanged;

        EasyTierRoomPrefixTextBox.LostFocus += OnEasyTierAutoSaveChanged;
        EasyTierGamePortNumericUpDown.LostFocus += OnEasyTierAutoSaveChanged;
        EasyTierPeerNodesTextBox.LostFocus += OnEasyTierAutoSaveChanged;
        EasyTierNetworkNameTextBox.LostFocus += OnEasyTierAutoSaveChanged;
        EasyTierNetworkSecretTextBox.LostFocus += OnEasyTierAutoSaveChanged;
        foreach (var check in new[]
                 {
                     EasyTierUdpCheckBox,
                     EasyTierLatencyFirstCheckBox,
                     EasyTierCompressionCheckBox,
                     EasyTierKcpCheckBox
                 })
        {
            check.IsCheckedChanged += OnEasyTierAutoSaveChanged;
        }

        RobotOneBotTextBox.LostFocus += OnRobotAutoSaveChanged;
        RobotAccessTokenTextBox.LostFocus += OnRobotAutoSaveChanged;
        RobotBoundGroupsTextBox.LostFocus += OnRobotAutoSaveChanged;
        RobotDatabasePathTextBox.LostFocus += OnRobotAutoSaveChanged;
        RobotDefaultEncodingTextBox.LostFocus += OnRobotAutoSaveChanged;
        RobotFallbackEncodingTextBox.LostFocus += OnRobotAutoSaveChanged;
        RobotSuperUsersTextBox.LostFocus += OnRobotAutoSaveChanged;
        RobotReconnectNumericUpDown.LostFocus += OnRobotAutoSaveChanged;
    }

    private void OnServerSettingsAutoSaveChanged(object? sender, RoutedEventArgs e)
    {
        if (_isApplyingServerSettings)
        {
            return;
        }

        SaveServerSettings(refreshEditor: false);
    }

    private void OnConsoleLogFilterRulePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isApplyingServerSettings || _isApplyingLocalizedOptions)
        {
            return;
        }

        if (e.PropertyName == nameof(ConsoleLogFilterRuleItem.Pattern))
        {
            return;
        }

        SaveServerSettings(refreshEditor: false);
    }

    private void OnConsoleLogFilterPatternLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_isApplyingServerSettings || _isApplyingLocalizedOptions)
        {
            return;
        }

        SaveServerSettings(refreshEditor: false);
    }

    private void OnConsoleLogFiltersSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        RefreshVisibleConsoleLogFilterItems();
    }

    private void OnSettingsAddConsoleLogFilterClick(object? sender, RoutedEventArgs e)
    {
        if (_isApplyingServerSettings || _isApplyingLocalizedOptions)
        {
            return;
        }

        var item = new ConsoleLogFilterRuleItem(_isChinese, T);
        item.PropertyChanged += OnConsoleLogFilterRulePropertyChanged;
        _consoleLogFilterRuleItems.Add(item);
        RefreshVisibleConsoleLogFilterItems();
        SaveServerSettings(refreshEditor: false);
    }

    private void OnSettingsRemoveConsoleLogFilterClick(object? sender, RoutedEventArgs e)
    {
        if (_isApplyingServerSettings || _isApplyingLocalizedOptions)
        {
            return;
        }

        if (sender is not Button { Tag: ConsoleLogFilterRuleItem item })
        {
            return;
        }

        item.PropertyChanged -= OnConsoleLogFilterRulePropertyChanged;
        _consoleLogFilterRuleItems.Remove(item);
        RefreshVisibleConsoleLogFilterItems();
        SaveServerSettings(refreshEditor: false);
    }

    private void RefreshVisibleConsoleLogFilterItems()
    {
        var search = SettingsConsoleLogFiltersSearchTextBox?.Text?.Trim();
        _visibleConsoleLogFilterRuleItems.Clear();
        foreach (var item in _consoleLogFilterRuleItems)
        {
            if (string.IsNullOrWhiteSpace(search) ||
                item.Pattern.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                _visibleConsoleLogFilterRuleItems.Add(item);
            }
        }
    }

    private void OnNetworkSettingsAutoSaveChanged(object? sender, RoutedEventArgs e)
    {
        if (_isApplyingNetworkSettings)
        {
            return;
        }

        SaveNetworkSettings(refreshEditor: false);
    }

    private void OnFrpAutoSaveChanged(object? sender, RoutedEventArgs e)
    {
        if (_isApplyingConnectionSettings)
        {
            return;
        }

        SaveFrpSettings(updateStatus: false, refreshEditor: false);
    }

    private void OnEasyTierAutoSaveChanged(object? sender, RoutedEventArgs e)
    {
        if (_isApplyingConnectionSettings)
        {
            return;
        }

        SaveEasyTierSettings(updateStatus: false, refreshEditor: false);
    }

    private void OnRobotAutoSaveChanged(object? sender, RoutedEventArgs e)
    {
        if (_isApplyingConnectionSettings)
        {
            return;
        }

        SaveRobotSettings(updateStatus: false, refreshEditor: false);
    }

    private void RefreshQuickCommandItems(IEnumerable<string>? commands)
    {
        QuickCommandComboBox.ItemsSource = NormalizeQuickCommands(commands);
        QuickCommandComboBox.SelectedIndex = -1;
    }

    private static string FormatQuickCommands(IEnumerable<string>? commands)
    {
        return string.Join(Environment.NewLine, NormalizeQuickCommands(commands));
    }

    private static List<string> ParseQuickCommands(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return NormalizeQuickCommands(text.Split(
            ["\r\n", "\n", "\r"],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static List<string> NormalizeQuickCommands(IEnumerable<string>? commands)
    {
        var result = new List<string>();
        foreach (var command in commands ?? [])
        {
            var normalized = command?.Trim();
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    private void RefreshServerSettingsEditor()
    {
        _isApplyingServerSettings = true;
        try
        {
            var preferences = _preferencesService.Load();
            var profiles = _profileService.GetProfiles();
            SettingsWorkspaceDirectoryTextBox.Text = preferences.WorkspaceRoot;
            SettingsQuickCommandsTextBox.Text = FormatQuickCommands(preferences.QuickCommands);
            foreach (var item in _consoleLogFilterRuleItems)
            {
                item.PropertyChanged -= OnConsoleLogFilterRulePropertyChanged;
            }

            _consoleLogFilterRuleItems.Clear();
            foreach (var rule in preferences.ConsoleLogFilters ?? [])
            {
                var item = ConsoleLogFilterRuleItem.FromModel(rule, _isChinese, T);
                item.PropertyChanged += OnConsoleLogFilterRulePropertyChanged;
                _consoleLogFilterRuleItems.Add(item);
            }
            RefreshVisibleConsoleLogFilterItems();

            RefreshConsoleLogFilterSnapshot(preferences.ConsoleLogFilters);
            var saveCompression = preferences.SaveCompression ?? new SaveCompressionSettings();
            SettingsSaveCompressionEnabledCheckBox.IsChecked = saveCompression.Enabled;
            SetNumericValue(SettingsSaveCompressionLevelNumericUpDown, Math.Clamp(saveCompression.CompressionLevel, 1, 22));
            SettingsSaveCompressionPathTextBox.Text = saveCompression.CompressionPath;
            SelectConfigChoiceByValue(
                SettingsSaveCompressionUpdateModeComboBox,
                _saveCompressionUpdateModeOptions,
                saveCompression.UpdateMode.ToString());
            SettingsSaveCompressionDeleteSourceCheckBox.IsChecked = saveCompression.DeleteSourceFiles;
            SettingsStartWithWindowsCheckBox.IsChecked = preferences.StartWithWindows;
            SettingsCloseToTrayCheckBox.IsChecked = preferences.CloseToTrayOnExit;
            SettingsStartHiddenCheckBox.IsChecked = preferences.StartHiddenOnLaunch;
            SettingsAutoStartServerCheckBox.IsChecked = preferences.AutoStartServerOnLaunch;
            SettingsAutoRestartServerAfterCrashCheckBox.IsChecked = preferences.AutoRestartServerAfterCrash;
            SettingsAutoStartRobotCheckBox.IsChecked = preferences.AutoStartRobotOnLaunch;
            SettingsAutoStartDiscordCheckBox.IsChecked = preferences.AutoStartDiscordOnLaunch;
            SettingsAutoStartFrpCheckBox.IsChecked = preferences.AutoStartFrpOnLaunch;
            SettingsAutoStartThirdPartyFrpcCheckBox.IsChecked = preferences.AutoStartThirdPartyFrpcOnLaunch;
            SettingsAutoStartEasyTierCheckBox.IsChecked = preferences.AutoStartEasyTierOnLaunch;
            SettingsAutoStartGatewayCheckBox.IsChecked = preferences.AutoStartGatewayOnLaunch;
            SettingsAutoStartServerProfileComboBox.ItemsSource = profiles;
            var autoStartIds = SplitProfileIds(preferences.AutoStartServerProfileIds, preferences.AutoStartServerProfileId);
            SettingsAutoStartServerProfileComboBox.SelectedItem = profiles.FirstOrDefault(profile =>
                autoStartIds.Contains(profile.Id))
                ?? profiles.FirstOrDefault(profile =>
                    SplitProfileIds(preferences.DefaultLaunchProfileIds, preferences.DefaultLaunchProfileId).Contains(profile.Id))
                ?? profiles.FirstOrDefault();
            RefreshSettingsAutoStartTargetItems(profiles);
            SettingsServerStatusTextBlock.Text = T("已加载服务器设置。", "Server settings loaded.");
        }
        finally
        {
            _isApplyingServerSettings = false;
        }
    }

    private void SaveServerSettings(bool refreshEditor = true)
    {
        var preferences = _preferencesService.Load();
        var autoStartIds = LoadAutoStartProfileIds().ToList();
        preferences.WorkspaceRoot = SettingsWorkspaceDirectoryTextBox.Text?.Trim() ?? string.Empty;
        preferences.QuickCommands = ParseQuickCommands(SettingsQuickCommandsTextBox.Text);
        preferences.ConsoleLogFilters = ConsoleLogFilterRuleRules.NormalizeMany(
            _consoleLogFilterRuleItems.Select(item => item.ToModel()));
        preferences.SaveCompression = new SaveCompressionSettings
        {
            Enabled = SettingsSaveCompressionEnabledCheckBox.IsChecked == true,
            CompressionLevel = (int)Math.Round(SettingsSaveCompressionLevelNumericUpDown.Value ?? 3),
            CompressionPath = SettingsSaveCompressionPathTextBox.Text?.Trim() ?? string.Empty,
            UpdateMode = GetSelectedSaveCompressionUpdateMode(),
            DeleteSourceFiles = SettingsSaveCompressionDeleteSourceCheckBox.IsChecked == true
        };
        preferences.StartWithWindows = SettingsStartWithWindowsCheckBox.IsChecked == true;
        preferences.CloseToTrayOnExit = SettingsCloseToTrayCheckBox.IsChecked == true;
        preferences.StartHiddenOnLaunch = SettingsStartHiddenCheckBox.IsChecked == true;
        preferences.AutoStartServerOnLaunch = SettingsAutoStartServerCheckBox.IsChecked == true;
        preferences.AutoRestartServerAfterCrash = SettingsAutoRestartServerAfterCrashCheckBox.IsChecked == true;
        preferences.AutoStartServerProfileIds = autoStartIds;
        preferences.AutoStartServerProfileId = string.Join(';', autoStartIds);
        preferences.AutoStartRobotOnLaunch = SettingsAutoStartRobotCheckBox.IsChecked == true;
        preferences.AutoStartDiscordOnLaunch = SettingsAutoStartDiscordCheckBox.IsChecked == true;
        preferences.AutoStartFrpOnLaunch = SettingsAutoStartFrpCheckBox.IsChecked == true;
        preferences.AutoStartThirdPartyFrpcOnLaunch = SettingsAutoStartThirdPartyFrpcCheckBox.IsChecked == true;
        preferences.AutoStartEasyTierOnLaunch = SettingsAutoStartEasyTierCheckBox.IsChecked == true;
        preferences.AutoStartGatewayOnLaunch = SettingsAutoStartGatewayCheckBox.IsChecked == true;
        _preferencesService.Save(preferences);
        RefreshConsoleLogFilterSnapshot(preferences.ConsoleLogFilters);
        RefreshQuickCommandItems(preferences.QuickCommands);
        RefreshConsoleText();
        try
        {
            ApplyWindowsStartupRegistration(preferences.StartWithWindows);
        }
        catch (Exception ex)
        {
            SettingsServerStatusTextBlock.Text = T($"开机启动设置失败：{ex.Message}", $"Startup registration failed: {ex.Message}");
        }

        if (refreshEditor)
        {
            RefreshServerSettingsEditor();
        }
    }

    private void RefreshNetworkSettingsEditor()
    {
        _isApplyingNetworkSettings = true;
        try
        {
            var preferences = _preferencesService.Load();
            SettingsThirdPartyServerTextBox.Text = string.IsNullOrWhiteSpace(preferences.ServerDownloadCatalogUrl)
                ? DefaultServerDownloadCatalogUrl
                : preferences.ServerDownloadCatalogUrl;
            SettingsChunkedDownloadToggleSwitch.IsChecked = preferences.EnableChunkedDownloads;
            SettingsDownloadChunkCountTextBox.Text = Math.Clamp(preferences.DownloadChunkCount, 1, 32).ToString(CultureInfo.InvariantCulture);
            SettingsDownloadThreadCountTextBox.Text = Math.Clamp(preferences.DownloadThreadCount, 1, 32).ToString(CultureInfo.InvariantCulture);
            EnsureGitHubProxyOptions();
            SelectConfigChoiceByValue(SettingsGitHubProxyComboBox, _gitHubProxyOptions, preferences.GitHubProxy.ToString());
            SettingsAutoCheckUpdatesToggleSwitch.IsChecked = preferences.AutoCheckUpdates;
            SettingsUpdateStatusTextBlock.Text = T(
                $"当前版本：{_launcherUpdateService.CurrentVersion} · {_launcherUpdateService.PackageKind}",
                $"Current: {_launcherUpdateService.CurrentVersion} · {_launcherUpdateService.PackageKind}");
        }
        finally
        {
            _isApplyingNetworkSettings = false;
        }
    }

    private void SaveNetworkSettings(bool refreshEditor = true)
    {
        var preferences = _preferencesService.Load();
        preferences.ServerDownloadCatalogUrl = SettingsThirdPartyServerTextBox.Text?.Trim() ?? string.Empty;
        preferences.EnableChunkedDownloads = SettingsChunkedDownloadToggleSwitch.IsChecked == true;
        preferences.DownloadChunkCount = ParseClampedInt(SettingsDownloadChunkCountTextBox.Text, 4, 1, 32);
        preferences.DownloadThreadCount = ParseClampedInt(SettingsDownloadThreadCountTextBox.Text, 4, 1, 32);
        preferences.GitHubProxy = GetSelectedGitHubProxy();
        preferences.AutoCheckUpdates = SettingsAutoCheckUpdatesToggleSwitch.IsChecked == true;
        _preferencesService.Save(preferences);
        _downloadCatalogLoaded = false;

        if (refreshEditor)
        {
            RefreshNetworkSettingsEditor();
        }
    }

    private void LoadAboutIntroduction()
    {
        if (_aboutIntroductionLoaded)
        {
            return;
        }

        const string fileName = "launchergo-introduction.html";
        var path = FindBundledContentPath(fileName);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            SetAboutFallbackText(T($"未找到项目介绍文件：{fileName}。", $"Project introduction file was not found: {fileName}."));
            _aboutIntroductionLoaded = true;
            return;
        }

        try
        {
            RenderAboutHtml(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            SetAboutFallbackText(T($"读取项目介绍失败：{ex.Message}", $"Failed to read project introduction: {ex.Message}"));
        }

        _aboutIntroductionLoaded = true;
    }

    private void RenderAboutHtml(string html)
    {
        try
        {
            SettingsAboutContentHost.Content = BuildAboutHtmlView(html);
        }
        catch
        {
            SetAboutFallbackText(_isChinese
                ? "项目介绍内容无法显示。"
                : "The project introduction could not be displayed.");
        }
    }

    private bool IsCatalogEntryDownloaded(
        ServerDownloadEntry entry,
        string serverDirectory,
        IReadOnlySet<string> installedVersions)
    {
        if (installedVersions.Contains(entry.Version))
        {
            return true;
        }

        if (!File.Exists(Path.Combine(serverDirectory, entry.FileName)))
        {
            return false;
        }

        return true;
    }

    private void SetAboutFallbackText(string text)
    {
        SettingsAboutContentHost.Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = new SelectableTextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13
            }
        };
    }

    private Control BuildAboutHtmlView(string html)
    {
        var normalizedHtml = NormalizeAboutHtmlEntities(html);
        var document = XDocument.Parse(normalizedHtml, LoadOptions.PreserveWhitespace);
        var language = _isChinese ? "zh-CN" : "en";
        var article = document
            .Descendants("article")
            .FirstOrDefault(element => string.Equals(
                (string?)element.Attribute("lang"),
                language,
                StringComparison.OrdinalIgnoreCase));

        if (article is null)
        {
            throw new InvalidOperationException($"Missing introduction article for language '{language}'.");
        }

        var host = new StackPanel
        {
            Spacing = 6
        };

        foreach (var element in article.Elements())
        {
            AddAboutHtmlElement(host, element);
        }

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = host
        };
    }

    private static void AddAboutHtmlElement(StackPanel host, XElement element)
    {
        var tagName = element.Name.LocalName;
        switch (tagName)
        {
            case "h1":
            case "h2":
                AddAboutHtmlText(host, ReadAboutHtmlText(element), "AboutHeading1");
                return;
            case "h3":
            case "h4":
                AddAboutHtmlText(host, ReadAboutHtmlText(element), "AboutHeading2");
                return;
            case "p":
                AddAboutHtmlText(host, ReadAboutHtmlText(element), "AboutParagraph");
                return;
            case "pre":
                var code = System.Net.WebUtility.HtmlDecode(element.Value).Trim('\r', '\n');
                if (!string.IsNullOrWhiteSpace(code))
                {
                    AddAboutCodeBlock(host, code);
                }

                return;
            case "table":
                AddAboutHtmlTable(host, element);
                return;
            case "ul":
            case "ol":
                AddAboutHtmlList(host, element, tagName == "ol");
                return;
            case "div":
                AddAboutHtmlDiv(host, element);
                return;
            default:
                foreach (var child in element.Elements())
                {
                    AddAboutHtmlElement(host, child);
                }

                return;
        }
    }

    private static void AddAboutHtmlText(StackPanel host, string text, string className)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            host.Children.Add(CreateAboutText(text, className));
        }
    }

    private static void AddAboutHtmlDiv(StackPanel host, XElement element)
    {
        var paragraphs = element
            .Elements("p")
            .Select(ReadAboutHtmlText)
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .ToList();
        if (paragraphs.Count == 0)
        {
            foreach (var child in element.Elements())
            {
                AddAboutHtmlElement(host, child);
            }

            return;
        }

        var callout = new Border
        {
            Child = CreateAboutText(string.Join(Environment.NewLine, paragraphs), "AboutParagraph")
        };
        callout.Classes.Add("AboutCallout");
        var style = (string?)element.Attribute("style") ?? string.Empty;
        if (style.Contains("#e6a700", StringComparison.OrdinalIgnoreCase))
        {
            callout.Classes.Add("AboutCalloutWarning");
        }
        else if (style.Contains("#e03e2d", StringComparison.OrdinalIgnoreCase))
        {
            callout.Classes.Add("AboutCalloutDanger");
        }

        host.Children.Add(callout);
    }

    private static void AddAboutHtmlList(StackPanel host, XElement list, bool numbered)
    {
        var index = 1;
        foreach (var item in list.Elements("li"))
        {
            var prefix = numbered ? $"{index++}. " : "• ";
            var text = ReadAboutHtmlText(item);
            if (!string.IsNullOrWhiteSpace(text))
            {
                host.Children.Add(CreateAboutText(prefix + text, "AboutSubText"));
            }
        }
    }

    private static void AddAboutHtmlTable(StackPanel host, XElement table)
    {
        var rows = table
            .Descendants("tr")
            .Select(row => row
                .Elements()
                .Where(cell => cell.Name.LocalName is "th" or "td")
                .Select(ReadAboutHtmlText)
                .ToList())
            .Where(static row => row.Count > 0)
            .ToList();
        if (rows.Count == 0)
        {
            return;
        }

        AddAboutTable(host, rows);
    }

    private static string ReadAboutHtmlText(XElement element)
    {
        var builder = new StringBuilder();
        foreach (var node in element.DescendantNodes())
        {
            if (node is XText text)
            {
                builder.Append(text.Value);
            }
            else if (node is XElement child && child.Name.LocalName.Equals("br", StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine();
            }
        }

        var decoded = System.Net.WebUtility.HtmlDecode(builder.ToString());
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    private static string NormalizeAboutHtmlEntities(string html)
    {
        return html
            .Replace("&nbsp;", "&#160;", StringComparison.OrdinalIgnoreCase)
            .Replace("&ndash;", "&#8211;", StringComparison.OrdinalIgnoreCase)
            .Replace("&mdash;", "&#8212;", StringComparison.OrdinalIgnoreCase)
            .Replace("&hellip;", "&#8230;", StringComparison.OrdinalIgnoreCase)
            .Replace("&times;", "&#215;", StringComparison.OrdinalIgnoreCase);
    }

    private static TextBlock CreateAboutText(string text, string className)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap
        };
        textBlock.Classes.Add(className);
        return textBlock;
    }

    private static void AddAboutCodeBlock(StackPanel host, string code)
    {
        var textBlock = CreateAboutText(code, "AboutCodeText");
        var border = new Border
        {
            Child = textBlock
        };
        border.Classes.Add("AboutCodeBlock");
        host.Children.Add(border);
    }

    private static void AddAboutTable(StackPanel host, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var columnCount = rows.Max(row => row.Count);
        var grid = new Grid
        {
            ColumnSpacing = 0,
            RowSpacing = 0,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
        grid.Classes.Add("AboutTable");

        for (var column = 0; column < columnCount; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }

        for (var row = 0; row < rows.Count; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var column = 0; column < columnCount; column++)
            {
                var cellText = column < rows[row].Count ? rows[row][column] : string.Empty;
                var cell = new Border
                {
                    Child = CreateAboutText(
                        cellText,
                        row == 0 ? "AboutTableHeaderText" : "AboutTableCellText")
                };
                cell.Classes.Add(row == 0 ? "AboutTableHeaderCell" : "AboutTableCell");
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }
        }

        host.Children.Add(grid);
    }

    private async Task RefreshContributorsAsync(bool forceReload = false)
    {
        if (_contributorsLoaded && !forceReload)
        {
            return;
        }

        try
        {
            using var response = await SharedHttpClient.GetAsync(GitHubContributorsApiUrl);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);

            _settingsContributorItems.Clear();
            foreach (var contributor in document.RootElement.EnumerateArray())
            {
                var login = ReadJsonString(contributor, "login");
                if (string.IsNullOrWhiteSpace(login))
                {
                    continue;
                }

                var contributions = contributor.TryGetProperty("contributions", out var contributionsNode) &&
                                    contributionsNode.TryGetInt32(out var parsed)
                    ? parsed
                    : 0;
                _settingsContributorItems.Add(new SettingsContributorItem
                {
                    Login = login,
                    HtmlUrl = ReadJsonString(contributor, "html_url"),
                    AvatarImage = await LoadAvatarImageAsync(ReadJsonString(contributor, "avatar_url")),
                    ContributionsText = T($"贡献 {contributions} 次", $"{contributions} contributions")
                });
            }

            _contributorsLoaded = true;
        }
        catch
        {
            _settingsContributorItems.Clear();
        }
    }

    private async Task RefreshSponsorsAsync(bool forceReload = false)
    {
        if (_sponsorsLoaded && !forceReload)
        {
            return;
        }

        try
        {
            using var response = await SharedHttpClient.GetAsync(GetSponsorApiUrl());
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.TryGetProperty("ok", out var okNode) &&
                okNode.ValueKind == JsonValueKind.False)
            {
                var message = ReadJsonString(document.RootElement, "message");
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "Sponsor API failed." : message);
            }

            _settingsSponsorItems.Clear();
            if (TryGetSponsorList(document.RootElement, out var listNode))
            {
                foreach (var sponsor in listNode.EnumerateArray())
                {
                    var item = await BuildSponsorItemAsync(sponsor);
                    if (!string.IsNullOrWhiteSpace(item.Name))
                    {
                        _settingsSponsorItems.Add(item);
                    }
                }
            }

            _sponsorsLoaded = true;
        }
        catch
        {
            _settingsSponsorItems.Clear();
        }
    }

    private async Task OpenAppLogsAsync()
    {
        var logDirectory = GetAppLogDirectory();
        Directory.CreateDirectory(logDirectory);

        var latestLog = Directory.EnumerateFiles(logDirectory, "LauncherGo-*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        var target = latestLog ?? logDirectory;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            SettingsAdvancedStatusTextBlock.Text = T("已打开软件日志。", "App logs opened.");
        }
        catch (Exception ex)
        {
            SettingsAdvancedStatusTextBlock.Text = T($"打开软件日志失败：{ex.Message}", $"Failed to open app logs: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    private async Task ClearDownloadCacheAsync()
    {
        try
        {
            var preferences = _preferencesService.Load();
            var deleted = await _serverPackageService.ClearDownloadCacheAsync(preferences.ServerDirectory);
            _downloadCatalogLoaded = false;
            await RefreshDownloadVersionsAsync(forceReload: true);
            SettingsAdvancedStatusTextBlock.Text = T($"已清空下载缓存：{deleted} 个文件。", $"Download cache cleared: {deleted} files.");
        }
        catch (Exception ex)
        {
            SettingsAdvancedStatusTextBlock.Text = T($"清空下载缓存失败：{ex.Message}", $"Failed to clear download cache: {ex.Message}");
        }
    }

    private void ResetAllSettingsAndRestartToGuide()
    {
        try
        {
            _preferencesService.Save(new LauncherPreferences { IsOnboardingCompleted = false });
            var executablePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                Process.Start(new ProcessStartInfo { FileName = executablePath, UseShellExecute = true });
            }

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                _isExitRequested = true;
                desktop.Shutdown();
            }
        }
        catch (Exception ex)
        {
            SettingsAdvancedStatusTextBlock.Text = T($"重置设置失败：{ex.Message}", $"Failed to reset settings: {ex.Message}");
        }
    }

    private static void ApplyWindowsStartupRegistration(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "LauncherGo";
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(runKeyPath, writable: true);
        if (key is null)
        {
            return;
        }

        if (!enabled)
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            using var process = Process.GetCurrentProcess();
            executablePath = process.MainModule?.FileName;
        }

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        key.SetValue(valueName, $"\"{executablePath}\"", Microsoft.Win32.RegistryValueKind.String);
    }

    private void RefreshConnectionSettingsEditor()
    {
        _isApplyingConnectionSettings = true;
        try
        {
            RebuildThirdPartyFrpcModeOptions();
            RefreshRobotProfileItems();

            var preferences = _preferencesService.Load();
            ApplyFrpSettings(preferences.Frp);
            ApplyEasyTierSettings(preferences.EasyTier);
            ApplyRobotSettings(preferences.Robot);
            ApplyDiscordSettings(preferences.Discord);
            ApplyGatewaySettings(preferences.TcpGateway);
            RefreshRobotProfileItems();
            RefreshAuthConfigItems();
            RefreshServerBridgeConfigItems();
        }
        finally
        {
            _isApplyingConnectionSettings = false;
        }
    }

    private void RefreshRobotProfileItems(IReadOnlyList<InstanceProfile>? profiles = null)
    {
        var profileList = profiles ?? _profileService.GetProfiles();
        var selectedByItem = _robotBindingItems
            .Select(item => (Item: item, ProfileId: item.SelectedProfile?.Id ?? item.ProfileId))
            .ToList();

        _robotProfileItems.Clear();
        foreach (var profile in profileList)
        {
            _robotProfileItems.Add(profile);
        }

        foreach (var item in _robotBindingItems)
        {
            var selectedId = selectedByItem.FirstOrDefault(entry => ReferenceEquals(entry.Item, item)).ProfileId ?? item.ProfileId;
            item.ProfileOptions = _robotProfileItems;
            item.SelectedProfile = _robotProfileItems.FirstOrDefault(profile =>
                profile.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void ApplyFrpSettings(FrpIntegrationSettings settings)
    {
        ConnectionFrpCommandTextBox.Text = settings.FrpCommand;
        SelectConfigChoiceByValue(
            ConnectionThirdPartyFrpcModeComboBox,
            _thirdPartyFrpcModeOptions,
            settings.ThirdPartyFrpcLaunchMode.ToString());
        ConnectionThirdPartyFrpcCommandTextBox.Text = settings.ThirdPartyFrpcCommand;
    }

    private void ApplyEasyTierSettings(EasyTierIntegrationSettings settings)
    {
        EasyTierRoomPrefixTextBox.Text = settings.RoomPrefix;
        SetNumericValue(EasyTierGamePortNumericUpDown, settings.GamePort);
        EasyTierPeerNodesTextBox.Text = settings.PeerNodesText;
        EasyTierNetworkNameTextBox.Text = settings.NetworkName;
        EasyTierNetworkSecretTextBox.Text = settings.NetworkSecret;
        EasyTierUdpCheckBox.IsChecked = settings.EnableUdp;
        EasyTierLatencyFirstCheckBox.IsChecked = settings.LatencyFirst;
        EasyTierCompressionCheckBox.IsChecked = settings.Compression;
        EasyTierKcpCheckBox.IsChecked = settings.EnableKcpProxy;
        ApplyEasyTierRuntimeStatus(_easyTierService.GetCurrentStatus());
    }

    private void ApplyGatewaySettings(TcpGatewaySettings settings)
    {
        GatewayListenHostTextBox.Text = settings.ListenHost;
        SetNumericValue(GatewayListenPortNumericUpDown, settings.ListenPort);
        SetNumericValue(GatewayMaxConnectionsNumericUpDown, settings.MaxConnections);
        SetNumericValue(GatewayMaxConnectionsPerIpNumericUpDown, settings.MaxConnectionsPerIp);
        SetNumericValue(GatewayConnectTimeoutNumericUpDown, settings.ConnectTimeoutSec);
        SetNumericValue(GatewayHealthCheckIntervalNumericUpDown, settings.HealthCheckIntervalSec);
        GatewayAllowListTextBox.Text = settings.AllowListText;
        GatewayBlockListTextBox.Text = settings.BlockListText;

        _gatewayBackendItems.Clear();
        var profileOptions = _profileService.GetProfiles();
        foreach (var backend in settings.Backends ?? [])
        {
            _gatewayBackendItems.Add(new TcpGatewayBackend
            {
                Id = backend.Id,
                Name = backend.Name,
                Host = backend.Host,
                Port = backend.Port,
                Weight = backend.Weight,
                RoutingState = backend.RoutingState == TcpGatewayBackendRoutingState.Offline
                    ? TcpGatewayBackendRoutingState.Disabled
                    : backend.RoutingState,
                MaintenanceTargetServerId = backend.MaintenanceTargetServerId,
                ProfileId = backend.ProfileId,
                ProfileOptions = profileOptions
            });
        }

        ApplyGatewayRuntimeStatus(_tcpGatewayService.GetCurrentStatus());
    }

    private void ApplyRobotSettings(RobotIntegrationSettings settings)
    {
        RobotOneBotTextBox.Text = settings.OneBotWsUrl;
        RobotAccessTokenTextBox.Text = settings.AccessToken;
        RobotBoundGroupsTextBox.Text = settings.BoundGroupIdsText;
        SetNumericValue(RobotReconnectNumericUpDown, settings.ReconnectIntervalSec);
        RobotDatabasePathTextBox.Text = settings.DatabasePath;
        RobotDefaultEncodingTextBox.Text = settings.DefaultEncoding;
        RobotFallbackEncodingTextBox.Text = settings.FallbackEncoding;
        RobotSuperUsersTextBox.Text = settings.SuperUsersText;
        RebuildRobotBindingItems(settings);
        RebuildRobotTeleportPointItems(settings);
        RebuildRobotCustomCommandItems(settings);
    }

    private void RebuildRobotBindingItems(RobotIntegrationSettings settings)
    {
        RefreshRobotProfileItems();
        _robotBindingItems.Clear();

        foreach (var binding in settings.ProfileBindings ?? [])
        {
            _robotBindingItems.Add(new RobotProfileBindingItem(
                _robotProfileItems,
                binding.ProfileId,
                binding.GroupId,
                binding.SuperUserId));
        }

        if (_robotBindingItems.Count == 0)
        {
            var groups = ParseQqIds(settings.BoundGroupIdsText).Select(static id => id.ToString(CultureInfo.InvariantCulture)).ToList();
            var admins = ParseQqIds(settings.SuperUsersText).Select(static id => id.ToString(CultureInfo.InvariantCulture)).ToList();
            var count = Math.Max(groups.Count, admins.Count);
            for (var i = 0; i < count; i++)
            {
                _robotBindingItems.Add(new RobotProfileBindingItem(
                    _robotProfileItems,
                    _robotProfileItems.FirstOrDefault()?.Id ?? string.Empty,
                    i < groups.Count ? groups[i] : string.Empty,
                    i < admins.Count ? admins[i] : string.Empty));
            }
        }

        if (_robotBindingItems.Count == 0)
        {
            _robotBindingItems.Add(new RobotProfileBindingItem(
                _robotProfileItems,
                _robotProfileItems.FirstOrDefault()?.Id ?? string.Empty,
                string.Empty,
                string.Empty));
        }
    }

    private void RebuildRobotCustomCommandItems(RobotIntegrationSettings settings)
    {
        _robotCustomCommandItems.Clear();
        foreach (var command in settings.CustomCommands ?? [])
        {
            _robotCustomCommandItems.Add(new RobotCustomCommandItem(
                command.Command,
                command.MessageType,
                command.Content,
                _isChinese));
        }

        if (_robotCustomCommandItems.Count == 0)
        {
            _robotCustomCommandItems.Add(new RobotCustomCommandItem(
                string.Empty,
                RobotCustomMessageType.Text,
                string.Empty,
                _isChinese));
        }
    }

    private void RebuildRobotTeleportPointItems(RobotIntegrationSettings settings)
    {
        _robotTeleportPointItems.Clear();
        foreach (var point in settings.TeleportPoints ?? [])
        {
            _robotTeleportPointItems.Add(new RobotTeleportPointItem(point.Name, point.X, point.Y, point.Z));
        }

        if (_robotTeleportPointItems.Count == 0)
        {
            _robotTeleportPointItems.Add(new RobotTeleportPointItem(string.Empty, 0, 0, 0));
        }
    }

    private void SaveFrpSettings(bool updateStatus = true, bool refreshEditor = true)
    {
        var preferences = _preferencesService.Load();
        preferences.Frp = CollectFrpSettings();
        _preferencesService.Save(preferences);
        if (refreshEditor)
        {
            RefreshConnectionSettingsEditor();
        }

        if (updateStatus)
        {
            SetConnectionStatus(T("内网穿透配置已保存。", "FRP configuration saved."));
        }
    }

    private void SaveEasyTierSettings(bool updateStatus = true, bool refreshEditor = true)
    {
        var preferences = _preferencesService.Load();
        preferences.EasyTier = CollectEasyTierSettings();
        _preferencesService.Save(preferences);
        if (refreshEditor)
        {
            RefreshConnectionSettingsEditor();
        }

        if (updateStatus)
        {
            SetConnectionStatus(T("EasyTier 配置已保存。", "EasyTier configuration saved."));
        }
    }

    private void RefreshServerBridgeConfigItems(IReadOnlyList<InstanceProfile>? profiles = null)
    {
        var profileList = profiles ?? _profileService.GetProfiles();
        _serverBridgeConfigItems.Clear();
        foreach (var profile in profileList)
        {
            _serverBridgeConfigItems.Add(ProfileConfigListItem.FromPath(
                profile,
                GetServerBridgeSettingsPath(profile)));
        }
    }

    private void SaveGatewaySettings(bool updateStatus = true, bool refreshEditor = true)
    {
        var preferences = _preferencesService.Load();
        preferences.TcpGateway = CollectGatewaySettings();
        _preferencesService.Save(preferences);
        if (refreshEditor)
        {
            RefreshConnectionSettingsEditor();
        }

        if (updateStatus)
        {
            SetConnectionStatus(
                _tcpGatewayService.GetCurrentStatus().IsRunning
                    ? T("TCP 网关配置已保存，重启网关后生效。", "TCP gateway configuration saved. Restart the gateway to apply it.")
                    : T("TCP 网关配置已保存。", "TCP gateway configuration saved."));
        }
    }

    private bool SaveRobotSettings(bool updateStatus = true, bool refreshEditor = true)
    {
        if (!TryValidateRobotTeleportPoints(out var teleportValidationMessage))
        {
            SetConnectionStatus(teleportValidationMessage);
            return false;
        }

        if (!TryValidateRobotCustomCommands(out var validationMessage))
        {
            SetConnectionStatus(validationMessage);
            return false;
        }

        var preferences = _preferencesService.Load();
        preferences.Robot = CollectRobotSettings();
        _preferencesService.Save(preferences);
        if (refreshEditor)
        {
            RefreshConnectionSettingsEditor();
        }

        if (updateStatus)
        {
            SetConnectionStatus(T("QQ机器人配置已保存。", "QQ robot configuration saved."));
        }

        return true;
    }

    private async Task SaveRobotSettingsAndReloadIfRunningAsync(bool updateStatus = true, bool refreshEditor = true)
    {
        if (!SaveRobotSettings(updateStatus, refreshEditor))
        {
            return;
        }
        var preferences = _preferencesService.Load();
        await _robotService.SaveSettingsAsync(ToRobotSettings(preferences.Robot));

        if (!_robotService.GetCurrentStatus().IsRunning)
        {
            return;
        }

        try
        {
            await _robotService.StopAsync(TimeSpan.FromSeconds(5));
            await _robotService.StartAsync(ToRobotSettings(preferences.Robot));
            SetConnectionStatus(T("QQ机器人配置已保存，并已重新加载。", "QQ robot configuration saved and reloaded."));
        }
        catch (Exception ex)
        {
            SetConnectionStatus(T($"QQ机器人配置已保存，但重新加载失败：{ex.Message}", $"QQ robot configuration saved, but reload failed: {ex.Message}"));
        }
        finally
        {
            UpdateRobotToggleButtonText();
            UpdateCardValues(_serverProcessService.GetCachedStatus());
        }
    }

    private FrpIntegrationSettings CollectFrpSettings()
    {
        var mode = GetSelectedThirdPartyFrpcMode();
        var fallbackThirdPartyCommand = mode == ThirdPartyFrpcLaunchMode.CommandOnly
            ? FrpIntegrationSettings.DefaultThirdPartyFrpcCommand
            : FrpIntegrationSettings.DefaultThirdPartyFrpcConfigCommand;

        return new FrpIntegrationSettings
        {
            FrpCommand = string.IsNullOrWhiteSpace(ConnectionFrpCommandTextBox.Text)
                ? FrpIntegrationSettings.DefaultFrpCommand
                : ConnectionFrpCommandTextBox.Text.Trim(),
            ThirdPartyFrpcLaunchMode = mode,
            ThirdPartyFrpcCommand = string.IsNullOrWhiteSpace(ConnectionThirdPartyFrpcCommandTextBox.Text)
                ? fallbackThirdPartyCommand
                : ConnectionThirdPartyFrpcCommandTextBox.Text.Trim()
        };
    }

    private EasyTierIntegrationSettings CollectEasyTierSettings()
    {
        return new EasyTierIntegrationSettings
        {
            RoomPrefix = string.IsNullOrWhiteSpace(EasyTierRoomPrefixTextBox.Text)
                ? EasyTierIntegrationSettings.DefaultRoomPrefix
                : EasyTierRoomPrefixTextBox.Text.Trim(),
            GamePort = Math.Clamp(
                (int)Math.Round(EasyTierGamePortNumericUpDown.Value ?? EasyTierIntegrationSettings.DefaultGamePort),
                1,
                ushort.MaxValue),
            PeerNodesText = EasyTierPeerNodesTextBox.Text?.Trim() ?? string.Empty,
            NetworkName = EasyTierNetworkNameTextBox.Text?.Trim() ?? string.Empty,
            NetworkSecret = EasyTierNetworkSecretTextBox.Text?.Trim() ?? string.Empty,
            EnableUdp = EasyTierUdpCheckBox.IsChecked == true,
            LatencyFirst = EasyTierLatencyFirstCheckBox.IsChecked == true,
            Compression = EasyTierCompressionCheckBox.IsChecked == true,
            EnableKcpProxy = EasyTierKcpCheckBox.IsChecked == true,
            Hostname = "LauncherGo-vs-server",
            Ipv4Address = EasyTierIntegrationSettings.DefaultIpv4Address
        };
    }

    private TcpGatewaySettings CollectGatewaySettings()
    {
        return new TcpGatewaySettings
        {
            ListenHost = string.IsNullOrWhiteSpace(GatewayListenHostTextBox.Text)
                ? TcpGatewaySettings.DefaultListenHost
                : GatewayListenHostTextBox.Text.Trim(),
            ListenPort = GetNumericValue(GatewayListenPortNumericUpDown, TcpGatewaySettings.DefaultListenPort),
            MaxConnections = GetNumericValue(GatewayMaxConnectionsNumericUpDown, 200),
            MaxConnectionsPerIp = GetNumericValue(GatewayMaxConnectionsPerIpNumericUpDown, 4),
            ConnectTimeoutSec = GetNumericValue(GatewayConnectTimeoutNumericUpDown, 8),
            HealthCheckIntervalSec = GetNumericValue(GatewayHealthCheckIntervalNumericUpDown, 5),
            RedirectTicketSecret = _preferencesService.Load().TcpGateway.RedirectTicketSecret,
            AllowListText = GatewayAllowListTextBox.Text?.Trim() ?? string.Empty,
            BlockListText = GatewayBlockListTextBox.Text?.Trim() ?? string.Empty,
            Backends = _gatewayBackendItems.Select(backend => new TcpGatewayBackend
            {
                Id = string.IsNullOrWhiteSpace(backend.Id) ? Guid.NewGuid().ToString("N") : backend.Id,
                Name = backend.Name?.Trim() ?? string.Empty,
                Host = backend.Host?.Trim() ?? string.Empty,
                Port = backend.Port,
                Weight = backend.Weight,
                RoutingState = backend.RoutingState == TcpGatewayBackendRoutingState.Offline
                    ? TcpGatewayBackendRoutingState.Disabled
                    : backend.RoutingState,
                MaintenanceTargetServerId = backend.MaintenanceTargetServerId?.Trim() ?? string.Empty,
                ProfileId = backend.ProfileId?.Trim() ?? string.Empty
            }).ToList()
        };
    }

    private RobotIntegrationSettings CollectRobotSettings()
    {
        var bindings = CollectRobotProfileBindings();
        return new RobotIntegrationSettings
        {
            OneBotWsUrl = string.IsNullOrWhiteSpace(RobotOneBotTextBox.Text)
                ? "ws://127.0.0.1:3001/"
                : RobotOneBotTextBox.Text.Trim(),
            AccessToken = RobotAccessTokenTextBox.Text?.Trim() ?? string.Empty,
            BoundGroupIdsText = FormatQqIdText(bindings.Select(static binding => binding.GroupId)),
            ReconnectIntervalSec = GetNumericValue(RobotReconnectNumericUpDown, 5),
            DatabasePath = RobotDatabasePathTextBox.Text?.Trim() ?? string.Empty,
            DefaultEncoding = string.IsNullOrWhiteSpace(RobotDefaultEncodingTextBox.Text)
                ? "utf-8"
                : RobotDefaultEncodingTextBox.Text.Trim(),
            FallbackEncoding = string.IsNullOrWhiteSpace(RobotFallbackEncodingTextBox.Text)
                ? "gbk"
                : RobotFallbackEncodingTextBox.Text.Trim(),
            SuperUsersText = FormatQqIdText(bindings.Select(static binding => binding.SuperUserId)),
            ProfileBindings = bindings,
            CustomCommands = CollectRobotCustomCommands(),
            TeleportPoints = CollectRobotTeleportPoints()
        };
    }

    private List<RobotProfileBinding> CollectRobotProfileBindings()
    {
        var bindings = new List<RobotProfileBinding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _robotBindingItems)
        {
            var profileId = item.SelectedProfile?.Id ?? item.ProfileId;
            var groupId = NormalizeQqId(item.GroupId);
            var superUserId = NormalizeQqId(item.SuperUserId);
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

            bindings.Add(new RobotProfileBinding
            {
                ProfileId = profileId?.Trim() ?? string.Empty,
                GroupId = groupId,
                SuperUserId = superUserId
            });
        }

        return bindings;
    }

    private List<RobotCustomCommand> CollectRobotCustomCommands()
    {
        var commands = new List<RobotCustomCommand>();
        foreach (var item in _robotCustomCommandItems)
        {
            var candidate = new RobotCustomCommand
            {
                Command = item.Command,
                MessageType = item.MessageType,
                Content = item.Content
            };
            if (RobotCustomCommandRules.TryNormalize(candidate, out var normalized))
            {
                commands.Add(normalized);
            }
        }

        return RobotCustomCommandRules.NormalizeMany(commands);
    }

    private List<RobotTeleportPoint> CollectRobotTeleportPoints()
    {
        return RobotTeleportPointRules.NormalizeMany(_robotTeleportPointItems.Select(static item => new RobotTeleportPoint
        {
            Name = item.Name,
            X = (double)item.X,
            Y = (double)item.Y,
            Z = (double)item.Z
        }));
    }

    private bool TryValidateRobotTeleportPoints(out string message)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _robotTeleportPointItems)
        {
            var name = item.Name.Trim();
            if (string.IsNullOrWhiteSpace(name) && item.X == 0 && item.Y == 0 && item.Z == 0)
                continue;

            var candidate = new RobotTeleportPoint
            {
                Name = name,
                X = (double)item.X,
                Y = (double)item.Y,
                Z = (double)item.Z
            };
            if (!RobotTeleportPointRules.TryNormalize(candidate, out var normalized))
            {
                message = T(
                    $"传送设置点无效：{(string.IsNullOrWhiteSpace(name) ? "未命名" : name)}。名称不能为空或超过 64 个字符，坐标必须在有效范围内。",
                    $"Invalid teleport point: {(string.IsNullOrWhiteSpace(name) ? "unnamed" : name)}. A name of up to 64 characters and valid coordinates are required.");
                return false;
            }

            if (!names.Add(normalized.Name))
            {
                message = T($"传送设置点名称重复：{normalized.Name}。", $"Duplicate teleport point name: {normalized.Name}.");
                return false;
            }
        }

        message = string.Empty;
        return true;
    }

    private bool TryValidateRobotCustomCommands(out string message)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _robotCustomCommandItems)
        {
            var command = item.Command?.Trim() ?? string.Empty;
            var content = item.Content?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(command) && string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            var candidateCommand = command.StartsWith('/') ? command : "/" + command;
            if (!RobotCustomCommandRules.TryNormalizeCommand(command, out var normalizedCommand))
            {
                message = RobotCustomCommandRules.HasReservedPrefixConflict(candidateCommand)
                    ? T($"自定义指令 {candidateCommand} 与内置指令前缀冲突。", $"Custom command {candidateCommand} conflicts with a built-in command prefix.")
                    : T($"自定义指令无效：{command}。只能使用 /字母、数字、下划线或连字符。", $"Invalid custom command: {command}. Use / plus letters, numbers, underscores, or hyphens.");
                return false;
            }

            if (!seen.Add(normalizedCommand))
            {
                message = T($"自定义指令重复：{normalizedCommand}。", $"Duplicate custom command: {normalizedCommand}.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                message = T($"自定义指令 {normalizedCommand} 缺少内容。", $"Custom command {normalizedCommand} has no content.");
                return false;
            }
        }

        message = string.Empty;
        return true;
    }

    private static string FormatQqIdText(IEnumerable<string?> values)
    {
        var ids = values
            .Select(NormalizeQqId)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return ids.Count == 0 ? string.Empty : string.Join(Environment.NewLine, ids);
    }

    private static string NormalizeQqId(string? value)
    {
        var raw = value?.Trim() ?? string.Empty;
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private static RobotSettings ToRobotSettings(RobotIntegrationSettings settings)
    {
        return new RobotSettings
        {
            OneBotWsUrl = settings.OneBotWsUrl,
            AccessToken = settings.AccessToken,
            BoundGroupIds = ParseQqIds(settings.BoundGroupIdsText),
            ProfileBindings = settings.ProfileBindings ?? [],
            ReconnectIntervalSec = settings.ReconnectIntervalSec,
            DatabasePath = settings.DatabasePath,
            DefaultEncoding = settings.DefaultEncoding,
            FallbackEncoding = settings.FallbackEncoding,
            SuperUsers = ParseQqIds(settings.SuperUsersText),
            CustomCommands = settings.CustomCommands ?? [],
            TeleportPoints = settings.TeleportPoints ?? []
        };
    }

    private static IReadOnlyList<long> ParseQqIds(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split([',', ';', '，', '；', '\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(item => long.TryParse(item.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();
    }

    private void RebuildThirdPartyFrpcModeOptions()
    {
        var selectedValue = (ConnectionThirdPartyFrpcModeComboBox.SelectedItem as ConfigChoiceOption)?.Value;
        _thirdPartyFrpcModeOptions.Clear();
        _thirdPartyFrpcModeOptions.Add(new ConfigChoiceOption(
            ThirdPartyFrpcLaunchMode.ConfigFile.ToString(),
            T("配置文件", "Config File")));
        _thirdPartyFrpcModeOptions.Add(new ConfigChoiceOption(
            ThirdPartyFrpcLaunchMode.CommandOnly.ToString(),
            T("纯命令", "Command Only")));
        SelectConfigChoiceByValue(
            ConnectionThirdPartyFrpcModeComboBox,
            _thirdPartyFrpcModeOptions,
            selectedValue ?? ThirdPartyFrpcLaunchMode.ConfigFile.ToString());
    }

    private void RebuildSaveCompressionUpdateModeOptions()
    {
        var selectedValue = (SettingsSaveCompressionUpdateModeComboBox.SelectedItem as ConfigChoiceOption)?.Value;
        _saveCompressionUpdateModeOptions.Clear();
        _saveCompressionUpdateModeOptions.Add(new ConfigChoiceOption(
            SaveCompressionUpdateMode.UpdateAndAdd.ToString(),
            T("更新并添加文件", "Update and add files")));
        _saveCompressionUpdateModeOptions.Add(new ConfigChoiceOption(
            SaveCompressionUpdateMode.AddAndReplace.ToString(),
            T("添加并替换文件", "Add and replace files")));
        SettingsSaveCompressionUpdateModeComboBox.ItemsSource = _saveCompressionUpdateModeOptions;
        SelectConfigChoiceByValue(
            SettingsSaveCompressionUpdateModeComboBox,
            _saveCompressionUpdateModeOptions,
            selectedValue ?? SaveCompressionUpdateMode.UpdateAndAdd.ToString());
    }

    private SaveCompressionUpdateMode GetSelectedSaveCompressionUpdateMode()
    {
        var value = (SettingsSaveCompressionUpdateModeComboBox.SelectedItem as ConfigChoiceOption)?.Value;
        return Enum.TryParse<SaveCompressionUpdateMode>(value, ignoreCase: true, out var mode)
            ? mode
            : SaveCompressionUpdateMode.UpdateAndAdd;
    }

    private ThirdPartyFrpcLaunchMode GetSelectedThirdPartyFrpcMode()
    {
        var value = (ConnectionThirdPartyFrpcModeComboBox.SelectedItem as ConfigChoiceOption)?.Value;
        return Enum.TryParse<ThirdPartyFrpcLaunchMode>(value, ignoreCase: true, out var mode)
            ? mode
            : ThirdPartyFrpcLaunchMode.ConfigFile;
    }

    private void UpdateConnectionFrpActionButtons()
    {
        var frpStatus = _frpService.GetCurrentStatus();
        var thirdPartyStatus = _thirdPartyFrpcService.GetCurrentStatus();
        _isFrpRunning = frpStatus.IsRunning;
        _isThirdPartyFrpcRunning = thirdPartyStatus.IsRunning;

        if (!_isTogglingFrp)
        {
            ConnectionFrpToggleButton.Content = frpStatus.IsRunning
                ? T("停止常规", "Stop Regular")
                : T("启动常规", "Start Regular");
        }

        if (!_isTogglingThirdPartyFrpc)
        {
            ConnectionThirdPartyFrpcToggleButton.Content = thirdPartyStatus.IsRunning
                ? T("停止第三方", "Stop Third-party")
                : T("启动第三方", "Start Third-party");
        }
    }

    private void UpdateEasyTierActionButtons()
    {
        var status = _easyTierService.GetCurrentStatus();
        _isEasyTierRunning = status.IsRunning;
        if (!_isTogglingEasyTier)
        {
            EasyTierToggleButton.Content = status.IsRunning
                ? T("停止", "Stop")
                : T("启动", "Start");
        }

        ApplyEasyTierRuntimeStatus(status);
    }

    private void ApplyEasyTierRuntimeStatus(EasyTierRuntimeStatus status)
    {
        EasyTierRoomCodeTextBox.Text = status.RoomCode;
        EasyTierGameAddressTextBox.Text = status.GameAddress;
        EasyTierCopyRoomCodeButton.IsEnabled = !string.IsNullOrWhiteSpace(status.RoomCode);
        EasyTierCopyGameAddressButton.IsEnabled = !string.IsNullOrWhiteSpace(status.GameAddress);

        EasyTierRuntimeInfoTextBlock.Text = status.IsReady
            ? T(
                $"ET IP {status.LocalIpV4}  节点 {status.ConnectedPeerCount}  玩家 {status.ConnectedPlayerCount}  控制端口 {status.ControlPort}",
                $"ET IP {status.LocalIpV4}  peers {status.ConnectedPeerCount}  players {status.ConnectedPlayerCount}  control port {status.ControlPort}")
            : !string.IsNullOrWhiteSpace(status.LastError)
                ? T($"状态：{status.LastError}", $"Status: {status.LastError}")
                : status.IsRunning
                    ? T("EasyTier 正在连接。", "EasyTier is connecting.")
                    : T("EasyTier 未启动。", "EasyTier is stopped.");
    }

    private bool IsConnectionProcessToggling(ConnectionProcessKind kind)
    {
        return kind == ConnectionProcessKind.Frp ? _isTogglingFrp : _isTogglingThirdPartyFrpc;
    }

    private void SetConnectionProcessToggling(ConnectionProcessKind kind, bool toggling)
    {
        if (kind == ConnectionProcessKind.Frp)
        {
            _isTogglingFrp = toggling;
            ConnectionFrpToggleButton.IsEnabled = !toggling;
            return;
        }

        _isTogglingThirdPartyFrpc = toggling;
        ConnectionThirdPartyFrpcToggleButton.IsEnabled = !toggling;
    }

    private void SetConnectionProcessToggleText(ConnectionProcessKind kind, bool runningText)
    {
        if (kind == ConnectionProcessKind.Frp)
        {
            ConnectionFrpToggleButton.Content = runningText
                ? T("停止常规", "Stop Regular")
                : T("启动常规", "Start Regular");
            return;
        }

        ConnectionThirdPartyFrpcToggleButton.Content = runningText
            ? T("停止第三方", "Stop Third-party")
            : T("启动第三方", "Start Third-party");
    }

    private void UpdateRobotToggleButtonText()
    {
        var isRunning = _robotService.GetCurrentStatus().IsRunning;
        RobotToggleButton.Content = isRunning ? T("停止", "Stop") : T("启动", "Start");
    }

    private void RefreshConnectionRuntimeStatus()
    {
        UpdateConnectionFrpActionButtons();
        UpdateEasyTierActionButtons();
        UpdateRobotToggleButtonText();
        DiscordToggleButton.Content = _discordBotService.GetCurrentStatus().IsRunning ? T("停止", "Stop") : T("启动", "Start");
        UpdateGatewayToggleButtonText();
        var currentStatus = _selectedConnectionTab switch
        {
            ConnectionTab.Frp => BuildFrpRuntimeStatusText(),
            ConnectionTab.EasyTier => BuildEasyTierRuntimeStatusText(),
            ConnectionTab.Robot => BuildRobotRuntimeStatusText(),
            ConnectionTab.Discord => string.Empty,
            ConnectionTab.Gateway => BuildGatewayRuntimeStatusText(_tcpGatewayService.GetCurrentStatus()),
            ConnectionTab.Auth => AuthStatusTextBlock.Text ?? string.Empty,
            _ => string.Empty
        };

        if (_selectedConnectionTab != ConnectionTab.Discord)
            SetConnectionStatus(currentStatus, notify: false);
        UpdateCardValues(_serverProcessService.GetCachedStatus());
    }

    private string BuildFrpRuntimeStatusText()
    {
        var frpStatus = _frpService.GetCurrentStatus();
        var thirdPartyStatus = _thirdPartyFrpcService.GetCurrentStatus();
        _isFrpRunning = frpStatus.IsRunning;
        _isThirdPartyFrpcRunning = thirdPartyStatus.IsRunning;

        var regular = frpStatus.IsRunning
            ? T(
                $"常规内网穿透：运行中 PID={frpStatus.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "--"}  {FormatConnectionUptime(frpStatus.StartedAtUtc)}",
                $"Regular FRP: running PID={frpStatus.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "--"}  {FormatConnectionUptime(frpStatus.StartedAtUtc)}")
            : T("常规内网穿透：未启动", "Regular FRP: stopped");
        var thirdParty = thirdPartyStatus.IsRunning
            ? T(
                $"第三方内网穿透：运行中 PID={thirdPartyStatus.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "--"}  {FormatConnectionUptime(thirdPartyStatus.StartedAtUtc)}",
                $"Third-party FRPC: running PID={thirdPartyStatus.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "--"}  {FormatConnectionUptime(thirdPartyStatus.StartedAtUtc)}")
            : T("第三方内网穿透：未启动", "Third-party FRPC: stopped");
        return $"{regular}；{thirdParty}";
    }

    private string BuildEasyTierRuntimeStatusText()
    {
        var status = _easyTierService.GetCurrentStatus();
        _isEasyTierRunning = status.IsRunning;
        if (!status.IsRunning)
        {
            return string.IsNullOrWhiteSpace(status.LastError)
                ? T("EasyTier：未启动", "EasyTier: stopped")
                : T($"EasyTier：{status.LastError}", $"EasyTier: {status.LastError}");
        }

        if (!status.IsReady)
        {
            return T(
                $"EasyTier：正在连接 PID={status.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "--"}",
                $"EasyTier: connecting PID={status.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "--"}");
        }

        var gameAddress = string.IsNullOrWhiteSpace(status.GameAddress) ? status.LocalIpV4 : status.GameAddress;
        return T(
            $"EasyTier：运行中 PID={status.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "--"}  {gameAddress}  {FormatConnectionUptime(status.StartedAtUtc)}",
            $"EasyTier: running PID={status.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "--"}  {gameAddress}  {FormatConnectionUptime(status.StartedAtUtc)}");
    }

    private string BuildRobotRuntimeStatusText()
    {
        var status = _robotService.GetCurrentStatus();
        if (!status.IsRunning)
        {
            return T("QQ机器人：未启动", "QQ robot: stopped");
        }

        return T(
            $"QQ机器人：运行中 PID={status.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "--"}  {FormatConnectionUptime(status.StartedAtUtc)}",
            $"QQ robot: running PID={status.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "--"}  {FormatConnectionUptime(status.StartedAtUtc)}");
    }

    private void ConfigureGatewayRoutingStateDisplay()
    {
        if (Resources["GatewayRoutingStateDisplayConverter"] is GatewayRoutingStateDisplayConverter converter)
        {
            converter.IsChinese = _isChinese;
        }

        // Rebuild the editor rows only when the application language changes so the
        // ComboBox item template reevaluates its localized state labels.
        if (GatewayBackendsItemsControl.ItemsSource is not null)
        {
            GatewayBackendsItemsControl.ItemsSource = null;
            GatewayBackendsItemsControl.ItemsSource = _gatewayBackendItems;
        }
    }

    private string BuildGatewayRuntimeStatusText(TcpGatewayRuntimeStatus status)
    {
        if (!status.IsRunning)
        {
            var error = TranslateGatewayError(status.LastError);
            return string.IsNullOrWhiteSpace(status.LastError)
                ? T("TCP 网关：未启动", "TCP gateway: stopped")
                : T($"TCP 网关：{error}", $"TCP gateway: {error}");
        }

        if (!status.IsListening)
        {
            return T(
                $"TCP 网关：正在启动 PID={status.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "--"}",
                $"TCP gateway: starting PID={status.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "--"}");
        }

        var runningStatus = T(
            $"TCP 网关：运行中 {status.ListenAddress}  活跃 {status.ActiveConnections}  {FormatConnectionUptime(status.StartedAtUtc)}",
            $"TCP gateway: running {status.ListenAddress}  active {status.ActiveConnections}  {FormatConnectionUptime(status.StartedAtUtc)}");
        return status.RequiresRestart
            ? $"{runningStatus}{Environment.NewLine}{TranslateGatewayError(status.PendingRestartReason)}"
            : runningStatus;
    }

    private void UpdateGatewayToggleButtonText()
    {
        GatewayToggleButton.Content = _tcpGatewayService.GetCurrentStatus().IsRunning
            ? T("停止", "Stop")
            : T("启动", "Start");
    }

    private string TranslateGatewayError(string? message)
    {
        var raw = message?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        if (raw.StartsWith("Invalid IP rule: ", StringComparison.Ordinal))
        {
            var rule = raw["Invalid IP rule: ".Length..];
            return T($"无效的 IP 规则：{rule}", $"Invalid IP rule: {rule}");
        }

        if (raw.StartsWith("GatewayHost executable was not found.", StringComparison.Ordinal))
        {
            return T("未找到 GatewayHost 程序，请重新构建或重新安装 LauncherGo。", raw);
        }

        return raw switch
        {
            "Gateway listen host is required." => T("必须填写网关监听地址。", "Gateway listen host is required."),
            "Gateway listen port must be between 1 and 65535." => T("网关监听端口必须在 1 到 65535 之间。", "Gateway listen port must be between 1 and 65535."),
            "Gateway connection limits must be greater than zero." => T("网关连接上限必须大于 0。", "Gateway connection limits must be greater than zero."),
            "Per-IP connection limit cannot exceed the total limit." => T("单 IP 连接上限不能大于最大连接数。", "Per-IP connection limit cannot exceed the total limit."),
            "At least one enabled gateway backend is required." => T("请至少添加一个已启用的后端服务器。", "At least one enabled gateway backend is required."),
            "Every gateway backend requires a unique ID." => T("每个后端服务器都必须具有唯一标识。", "Every gateway backend requires a unique ID."),
            "Every enabled backend requires a host and port." => T("每个已启用的后端服务器都必须填写主机和端口。", "Every enabled backend requires a host and port."),
            "TCP gateway is already running." => T("TCP 网关已经在运行。", "TCP gateway is already running."),
            "GatewayHost exited before it started listening." => T("GatewayHost 在开始监听前已退出。", "GatewayHost exited before it started listening."),
            "Timed out while waiting for GatewayHost to listen." => T("等待 GatewayHost 开始监听超时。", "Timed out while waiting for GatewayHost to listen."),
            "TCP gateway is not running." => T("TCP 网关未在运行。", "TCP gateway is not running."),
            "Gateway listen address or port changed; restart is required." => T("监听地址或端口已变更，需重启网关后生效。", "Gateway listen address or port changed; restart is required."),
            _ => raw
        };
    }

    private async Task RefreshGatewayStatusAsync()
    {
        if (_isRefreshingGateway)
        {
            return;
        }

        _isRefreshingGateway = true;
        try
        {
            var status = await _tcpGatewayService.RefreshStatusAsync();
            ApplyGatewayRuntimeStatus(status);
            if (_selectedTab == MainTab.Connection && _selectedConnectionTab == ConnectionTab.Gateway)
            {
                SetConnectionStatus(BuildGatewayRuntimeStatusText(status), notify: false);
            }
        }
        catch (Exception ex)
        {
            var message = TranslateGatewayError(GetExceptionMessage(ex));
            SetConnectionStatus(T(
                $"刷新 TCP 网关状态失败：{message}",
                $"Failed to refresh TCP gateway status: {message}"));
        }
        finally
        {
            _isRefreshingGateway = false;
        }
    }

    private void ApplyGatewayRuntimeStatus(TcpGatewayRuntimeStatus status)
    {
        GatewayRuntimeSummaryTextBlock.Text = BuildGatewayRuntimeStatusText(status);
        GatewayActiveConnectionsTextBlock.Text = status.ActiveConnections.ToString(CultureInfo.InvariantCulture);
        GatewayAcceptedConnectionsTextBlock.Text = status.AcceptedConnections.ToString(CultureInfo.InvariantCulture);
        GatewayRejectedConnectionsTextBlock.Text = status.RejectedConnections.ToString(CultureInfo.InvariantCulture);
        GatewayFailedConnectionsTextBlock.Text = status.FailedConnections.ToString(CultureInfo.InvariantCulture);
        GatewayUpstreamTextBlock.Text = FormatDataSize(status.ClientToBackendBytes);
        GatewayDownstreamTextBlock.Text = FormatDataSize(status.BackendToClientBytes);

        var runtimeBackendsById = status.Backends
            .ToDictionary(backend => backend.Id, StringComparer.OrdinalIgnoreCase);
        var existingItemsById = _gatewayBackendRuntimeItems
            .ToDictionary(item => item.BackendId, StringComparer.OrdinalIgnoreCase);
        var activeBackendIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in _gatewayBackendItems)
        {
            runtimeBackendsById.TryGetValue(definition.Id, out var backend);
            backend ??= new TcpGatewayBackendRuntimeStatus
            {
                Id = definition.Id,
                Name = definition.Name,
                Address = $"{definition.Host}:{definition.Port}",
                Enabled = definition.RoutingState != TcpGatewayBackendRoutingState.Disabled,
                RoutingState = definition.RoutingState,
                Weight = definition.Weight,
                ProfileId = definition.ProfileId
            };
            activeBackendIds.Add(backend.Id);
            if (!existingItemsById.TryGetValue(backend.Id, out var item))
            {
                item = new GatewayBackendRuntimeItem(backend.Id);
                _gatewayBackendRuntimeItems.Add(item);
            }

            item.Update(
                string.IsNullOrWhiteSpace(backend.Name) ? backend.Id : backend.Name,
                backend.Address,
                GetGatewayBackendStatusText(status, backend),
                backend.ActiveConnections.ToString(CultureInfo.InvariantCulture),
                backend.Weight.ToString(CultureInfo.InvariantCulture),
                FormatGatewayTraffic(backend.Statistics),
                T("统计", "Statistics"),
                backend);
        }

        foreach (var item in _gatewayBackendRuntimeItems
                     .Where(item => !activeBackendIds.Contains(item.BackendId))
                     .ToArray())
        {
            _gatewayBackendRuntimeItems.Remove(item);
        }

        foreach (var window in _gatewayStatisticsWindows.ToArray())
        {
            var backend = status.Backends.FirstOrDefault(item =>
                item.Id.Equals(window.BackendId, StringComparison.OrdinalIgnoreCase));
            if (backend is not null)
            {
                window.UpdateStatus(backend);
            }
        }

        UpdateGatewayToggleButtonText();
    }

    private string GetGatewayBackendStatusText(TcpGatewayRuntimeStatus gateway, TcpGatewayBackendRuntimeStatus backend)
    {
        if (!gateway.IsRunning)
        {
            return T("网关未运行", "Gateway stopped");
        }

        if (!gateway.IsListening)
        {
            return T("正在启动", "Starting");
        }

        return backend.RoutingState switch
        {
            TcpGatewayBackendRoutingState.Online => "Online",
            TcpGatewayBackendRoutingState.Draining => "Draining",
            TcpGatewayBackendRoutingState.Disabled => "Disabled",
            TcpGatewayBackendRoutingState.Offline => "Offline",
            _ => T("未知", "Unknown")
        };
    }

    private static string FormatGatewayTraffic(TcpGatewayBackendStatistics statistics)
    {
        statistics ??= new TcpGatewayBackendStatistics();
        return $"↑ {statistics.CurrentClientToBackendMbps:F3} / {statistics.PeakClientToBackendMbps:F3} Mbps\n" +
               $"↓ {statistics.CurrentBackendToClientMbps:F3} / {statistics.PeakBackendToClientMbps:F3} Mbps";
    }

    private static string FormatConnectionUptime(DateTimeOffset? startedAtUtc)
    {
        return startedAtUtc.HasValue
            ? FormatDuration(DateTimeOffset.UtcNow - startedAtUtc.Value)
            : "--";
    }

    private void SetConnectionStatus(string message, bool notify = true)
    {
        ConnectionStatusTextBlock.Text = message;
        ConnectionStatusTextBlock.IsVisible = !string.IsNullOrWhiteSpace(message);
        if (notify)
        {
            ShowToast(message);
        }
    }

    private async Task ImportConnectionExecutableAsync(ConnectionProcessKind kind)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = kind == ConnectionProcessKind.Frp
                ? T("导入frpc可执行文件", "Import frpc executable")
                : T("导入第三方frpc可执行文件", "Import third-party frpc executable"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Executable")
                {
                    Patterns = ["*.exe"]
                }
            ]
        });

        var sourcePath = TryGetLocalPath(files.FirstOrDefault());
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        try
        {
            if (kind == ConnectionProcessKind.Frp)
            {
                await _frpService.ImportExecutableAsync(sourcePath);
            }
            else
            {
                await _thirdPartyFrpcService.ImportExecutableAsync(sourcePath);
            }

            if (kind == ConnectionProcessKind.Frp)
            {
                if (string.IsNullOrWhiteSpace(ConnectionFrpCommandTextBox.Text))
                {
                    ConnectionFrpCommandTextBox.Text = FrpIntegrationSettings.DefaultFrpCommand;
                }
            }
            else
            {
                var mode = GetSelectedThirdPartyFrpcMode();
                var defaultCommand = mode == ThirdPartyFrpcLaunchMode.CommandOnly
                    ? FrpIntegrationSettings.DefaultThirdPartyFrpcCommand
                    : FrpIntegrationSettings.DefaultThirdPartyFrpcConfigCommand;
                if (string.IsNullOrWhiteSpace(ConnectionThirdPartyFrpcCommandTextBox.Text))
                {
                    ConnectionThirdPartyFrpcCommandTextBox.Text = defaultCommand;
                }
            }

            SaveFrpSettings(updateStatus: false, refreshEditor: false);
            SetConnectionStatus(T($"已导入：{Path.GetFileName(sourcePath)}", $"Imported: {Path.GetFileName(sourcePath)}"));
        }
        catch (Exception ex)
        {
            SetConnectionStatus(T($"导入失败：{ex.Message}", $"Import failed: {ex.Message}"));
        }
    }

    private async Task ToggleConnectionProcessAsync(ConnectionProcessKind kind)
    {
        if (IsConnectionProcessToggling(kind))
            return;

        SetConnectionProcessToggling(kind, true);
        try
        {
            if (kind == ConnectionProcessKind.Frp
                    ? _frpService.GetCurrentStatus().IsRunning
                    : _thirdPartyFrpcService.GetCurrentStatus().IsRunning)
            {
                SetConnectionProcessToggleText(kind, runningText: false);
                await StopConnectionProcessAsync(kind);
                return;
            }

            SaveFrpSettings(updateStatus: false, refreshEditor: false);
            SetConnectionProcessToggleText(kind, runningText: true);
            await StartConnectionProcessAsync(kind);
        }
        finally
        {
            SetConnectionProcessToggling(kind, false);
            UpdateConnectionFrpActionButtons();
            UpdateCardValues(_serverProcessService.GetCachedStatus());
        }
    }

    private async Task EditConnectionTomlAsync(ConnectionProcessKind kind)
    {
        try
        {
            string tomlPath;
            if (kind == ConnectionProcessKind.Frp)
            {
                await _frpService.LoadConfigAsync();
                tomlPath = _frpService.ConfigPath;
            }
            else
            {
                await _thirdPartyFrpcService.LoadConfigAsync();
                tomlPath = _thirdPartyFrpcService.ConfigPath;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = tomlPath,
                UseShellExecute = true
            });

            var serviceName = kind == ConnectionProcessKind.Frp
                ? T("常规内网穿透", "Regular FRP")
                : T("第三方内网穿透", "Third-party FRPC");
            SetConnectionStatus(T($"已打开 {serviceName} 的 TOML 配置。", $"Opened TOML config for {serviceName}."));
        }
        catch (Exception ex)
        {
            SetConnectionStatus(T($"打开 TOML 配置失败：{ex.Message}", $"Failed to open TOML config: {ex.Message}"));
        }
    }

    private async Task StartConnectionProcessAsync(ConnectionProcessKind kind)
    {
        var isRunning = kind == ConnectionProcessKind.Frp
            ? _frpService.GetCurrentStatus().IsRunning
            : _thirdPartyFrpcService.GetCurrentStatus().IsRunning;
        if (isRunning)
        {
            SetConnectionStatus(kind == ConnectionProcessKind.Frp
                ? T("常规内网穿透已在运行。", "Regular FRP is already running.")
                : T("第三方内网穿透已在运行。", "Third-party FRPC is already running."));
            return;
        }

        var serviceName = kind == ConnectionProcessKind.Frp
            ? T("常规内网穿透", "Regular FRP")
            : T("第三方内网穿透", "Third-party FRPC");

        try
        {
            if (kind == ConnectionProcessKind.Frp)
            {
                await _frpService.StartAsync();
            }
            else
            {
                await _thirdPartyFrpcService.StartAsync();
            }

            var status = kind == ConnectionProcessKind.Frp
                ? _frpService.GetCurrentStatus()
                : _thirdPartyFrpcService.GetCurrentStatus();
            SetConnectionStatus(T($"{serviceName} 已启动，PID={status.ProcessId}。", $"{serviceName} started, PID={status.ProcessId}."));
        }
        catch (Exception ex)
        {
            SetConnectionStatus(T($"{serviceName} 启动失败：{ex.Message}", $"{serviceName} start failed: {ex.Message}"));
        }
    }

    private async Task StopConnectionProcessAsync(ConnectionProcessKind kind)
    {
        var serviceName = kind == ConnectionProcessKind.Frp
            ? T("常规内网穿透", "Regular FRP")
            : T("第三方内网穿透", "Third-party FRPC");

        try
        {
            if (kind == ConnectionProcessKind.Frp)
            {
                await _frpService.StopAsync(TimeSpan.FromSeconds(15));
            }
            else
            {
                await _thirdPartyFrpcService.StopAsync(TimeSpan.FromSeconds(15));
            }

            SetConnectionStatus(T($"{serviceName} 已停止。", $"{serviceName} stopped."));
        }
        catch (Exception ex)
        {
            SetConnectionStatus(T($"{serviceName} 停止失败：{ex.Message}", $"{serviceName} stop failed: {ex.Message}"));
        }
    }

    private void RefreshAppearanceSettingsEditor()
    {
        _isApplyingAppearanceSettings = true;
        try
        {
            var preferences = _preferencesService.Load();
            SettingsLanguageLabelTextBlock.Text = T("语言", "Language");
            SettingsThemeLabelTextBlock.Text = T("主题", "Theme");

            SettingsLanguageComboBox.ItemsSource = AppearanceLanguageOptions
                .Select(option => option.NativeName)
                .ToList();
            SettingsThemeComboBox.ItemsSource = AppearanceThemeOptions
                .Select(option => _isChinese ? option.Zh : option.En)
                .ToList();

            SettingsLanguageComboBox.SelectedIndex = SupportedLanguages.FindIndex(preferences.Language);

            var themeIndex = Array.FindIndex(AppearanceThemeOptions, option => option.Mode == preferences.ThemeMode);
            SettingsThemeComboBox.SelectedIndex = themeIndex >= 0 ? themeIndex : 0;
        }
        finally
        {
            _isApplyingAppearanceSettings = false;
        }
    }

    private void OnSettingsLanguageSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingAppearanceSettings)
        {
            return;
        }

        var index = SettingsLanguageComboBox.SelectedIndex;
        if (index < 0 || index >= AppearanceLanguageOptions.Count)
        {
            return;
        }

        var languageCode = AppearanceLanguageOptions[index].Code;
        var preferences = _preferencesService.Load();
        preferences.Language = languageCode;
        _preferencesService.Save(preferences);

        _localizationService.SetLanguage(languageCode);
    }

    private void OnLanguageChanged(object? sender, LanguageChangedEventArgs e)
    {
        // Coalesce changes until the render queue. A rapid selection sequence only
        // renders the final culture, so no intermediate/blank language is visible.
        if (_languageRefreshQueued)
        {
            return;
        }

        _languageRefreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _languageRefreshQueued = false;
            ApplyCommittedLanguage();
        }, DispatcherPriority.Render);
    }

    private void ApplyCommittedLanguage()
    {
        _isChinese = _localizationService.CurrentCulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        _isApplyingLocalizedOptions = true;
        try
        {
            _aboutIntroductionLoaded = false;
            InitializeStaticTexts();
            RequestStaticUiTranslations();
            _ = RefreshSavesAsync();
            _ = RefreshDownloadVersionsAsync(forceReload: false);
            if (_selectedInstanceManageTab == InstanceManageTab.Mods)
            {
                _ = LoadModsForSelectedProfileAsync();
            }

            if (_selectedConnectionTab == ConnectionTab.Auth)
            {
                _ = RefreshAuthProfilesAsync();
            }

            if (_selectedInstanceManageTab == InstanceManageTab.ServerBridge)
            {
                _ = RefreshServerBridgeProfilesAsync();
            }

            RefreshAppearanceSettingsEditor();
            if (_selectedSettingsTab == SettingsTab.About)
            {
                LoadAboutIntroduction();
            }
        }
        finally
        {
            _isApplyingLocalizedOptions = false;
        }
    }

    private void OnSettingsThemeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingAppearanceSettings)
        {
            return;
        }

        var index = SettingsThemeComboBox.SelectedIndex;
        if (index < 0 || index >= AppearanceThemeOptions.Length)
        {
            return;
        }

        var mode = AppearanceThemeOptions[index].Mode;
        var preferences = _preferencesService.Load();
        preferences.ThemeMode = mode;
        _preferencesService.Save(preferences);

        ApplyTheme(mode);
        RefreshAppearanceSettingsEditor();
    }

    private static void ApplyTheme(ThemeMode mode)
    {
        if (Application.Current is null)
        {
            return;
        }

        Application.Current.RequestedThemeVariant = mode switch
        {
            ThemeMode.Dark => ThemeVariant.Dark,
            ThemeMode.Light => ThemeVariant.Light,
            _ => ThemeVariant.Default
        };
    }

    private static void SetSelectedClass(StyledElement element, bool selected)
    {
        element.Classes.Set("selected", selected);
    }

    private void OnServerOutputReceived(object? sender, string line)
    {
        if (string.IsNullOrWhiteSpace(line)
            || IsSystemConsoleLine(line)
            || ShouldSuppressConsoleLineForUi(line))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            TrackPlayerEventText(line);
        });
    }

    private void OnServerProfileOutputReceived(object? sender, ServerOutputLine output)
    {
        if (string.IsNullOrWhiteSpace(output.Line)
            || IsSystemConsoleLine(output.Line)
            || ShouldSuppressConsoleLineForUi(output.Line))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            AppendConsoleLine(output);
            TrackPlayerEventText(output.Line);
        });
    }

    private void OnServerStatusChanged(object? sender, ServerRuntimeStatus status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpdateCardValues(status);
            _ = HandleServerLogTailAsync(status);
            if (_selectedTab == MainTab.Monitor)
            {
                RenderSelectedMetricChart(status);
            }
        });
    }

    private async Task HandleServerLogTailAsync(ServerRuntimeStatus status)
    {
        try
        {
            var profileId = status.ProfileId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(profileId))
            {
                return;
            }

            if (!status.IsRunning)
            {
                if (_tailedProfileIds.Remove(profileId))
                {
                    await _logTailService.StopAsync(profileId);
                }

                return;
            }

            if (_tailedProfileIds.Contains(profileId))
            {
                return;
            }

            var profile = _profileService.GetProfileById(profileId);
            if (profile is null)
            {
                return;
            }

            var replayExisting = ShouldReplayExistingServerLogs(status, profile);
            await _logTailService.StartAsync(profile, replayExisting);
            _tailedProfileIds.Add(profile.Id);
            if (replayExisting)
            {
                _replayedLogProfileIds.Add(profile.Id);
                _consoleReplayLoadedProfileIds.Add(profile.Id);
            }
        }
        catch
        {
            // 日志跟随失败不影响主流程。
        }
    }

    private bool ShouldReplayExistingServerLogs(ServerRuntimeStatus status, InstanceProfile profile)
    {
        if (_replayedLogProfileIds.Contains(profile.Id))
        {
            return false;
        }

        return status.StartedAtUtc.HasValue &&
               status.StartedAtUtc.Value < _windowStartedAtUtc.AddSeconds(-RunningServerLogReplayGraceSeconds);
    }

    private void OnLogTailLineReceived(object? sender, string line)
    {
        if (string.IsNullOrWhiteSpace(line)
            || IsSystemConsoleLine(line)
            || ShouldSuppressConsoleLineForUi(line))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            TrackPlayerEventText(line);
        });
    }

    private void OnProfileLogTailLineReceived(object? sender, ProfileLogLine output)
    {
        if (string.IsNullOrWhiteSpace(output.Line)
            || string.IsNullOrWhiteSpace(output.ProfileId)
            || IsSystemConsoleLine(output.Line)
            || ShouldSuppressConsoleLineForUi(output.Line))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            AppendConsoleProfileLine(
                output.ProfileId,
                string.IsNullOrWhiteSpace(output.ProfileName) ? output.ProfileId : output.ProfileName,
                $"[log] {output.Line}");
            TrackPlayerEventText(output.Line);
        });
    }

    private void OnConsoleOutputScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        _consoleAutoScroll = IsConsoleScrolledToBottom();
    }

    private void OnFrpStatusChanged(object? sender, FrpRuntimeStatus status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _isFrpRunning = status.IsRunning;
            UpdateConnectionFrpActionButtons();
            if (_selectedTab == MainTab.Connection && _selectedConnectionTab == ConnectionTab.Frp)
            {
                RefreshConnectionRuntimeStatus();
            }
        });
    }

    private void OnThirdPartyFrpcStatusChanged(object? sender, FrpRuntimeStatus status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _isThirdPartyFrpcRunning = status.IsRunning;
            UpdateConnectionFrpActionButtons();
            if (_selectedTab == MainTab.Connection && _selectedConnectionTab == ConnectionTab.Frp)
            {
                RefreshConnectionRuntimeStatus();
            }
        });
    }

    private void OnEasyTierStatusChanged(object? sender, EasyTierRuntimeStatus status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _isEasyTierRunning = status.IsRunning;
            UpdateEasyTierActionButtons();
            if (_selectedTab == MainTab.Connection && _selectedConnectionTab == ConnectionTab.EasyTier)
            {
                RefreshConnectionRuntimeStatus();
            }
        });
    }

    private void AppendConsoleLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (IsSystemConsoleLine(line))
        {
            ShowToast(line);
            return;
        }

        if (ShouldSuppressConsoleLineForUi(line))
        {
            return;
        }

        var shouldAutoScroll = _consoleAutoScroll || IsConsoleScrolledToBottom();
        _consoleLines.Add(line);
        while (_consoleLines.Count > MaxConsoleLines)
        {
            _consoleLines.RemoveAt(0);
        }

        QueueConsoleRefresh();
    }

    private void AppendConsoleLine(ServerOutputLine output)
    {
        var profileId = string.IsNullOrWhiteSpace(output.ProfileId) ? "__unknown" : output.ProfileId;
        var profileName = string.IsNullOrWhiteSpace(output.ProfileName) ? profileId : output.ProfileName;
        AppendConsoleProfileLine(profileId, profileName, output.Line);
    }

    private void AppendConsoleProfileLine(string profileId, string profileName, string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine)
            || IsSystemConsoleLine(rawLine)
            || ShouldSuppressConsoleLineForUi(rawLine))
        {
            return;
        }

        if (!_consoleLinesByProfile.TryGetValue(profileId, out var lines))
        {
            lines = [];
            _consoleLinesByProfile[profileId] = lines;
        }

        var line = $"[{profileName}] {rawLine}";
        lines.Add(line);
        while (lines.Count > MaxConsoleLines)
        {
            lines.RemoveAt(0);
        }

        if (string.IsNullOrWhiteSpace(_selectedConsoleProfileId))
        {
            _selectedConsoleProfileId = profileId;
        }

        if (_selectedConsoleProfileId.Equals(profileId, StringComparison.OrdinalIgnoreCase))
        {
            QueueConsoleRefresh();
        }
    }

    private async void QueueConsoleRefresh()
    {
        if (_consoleRefreshQueued)
        {
            return;
        }

        _consoleRefreshQueued = true;
        await Task.Delay(ConsoleRefreshDelayMs);
        _consoleRefreshQueued = false;
        RefreshConsoleText();
    }

    private void RefreshConsoleText()
    {
        if (_selectedTab != MainTab.Console)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_selectedConsoleProfileId) ||
            !_consoleLinesByProfile.TryGetValue(_selectedConsoleProfileId, out var lines))
        {
            ConsoleOutputTextBlock.Text = string.Join(
                Environment.NewLine,
                _consoleLines.Where(line => !ConsoleLogFilterRuleRules.MatchesAny(_consoleLogFilterRules, line)));
            return;
        }

        var shouldAutoScroll = _consoleAutoScroll || IsConsoleScrolledToBottom();
        ConsoleOutputTextBlock.Text = string.Join(
            Environment.NewLine,
            lines.Where(line => !ConsoleLogFilterRuleRules.MatchesAny(_consoleLogFilterRules, line)));
        if (shouldAutoScroll)
        {
            Dispatcher.UIThread.Post(() =>
            {
                ConsoleOutputScrollViewer.ScrollToEnd();
                _consoleAutoScroll = true;
            }, DispatcherPriority.Background);
        }
    }

    private async Task EnsureConsoleReplayLoadedAsync(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId) || _consoleReplayLoadedProfileIds.Contains(profileId))
        {
            return;
        }

        var profile = _profileService.GetProfileById(profileId);
        if (profile is null)
        {
            return;
        }

        IReadOnlyList<string> lines;
        try
        {
            lines = await Task.Run(() => ReadConsoleProfileReplayLines(profile));
        }
        catch
        {
            return;
        }

        if (lines.Count == 0)
        {
            return;
        }

        _consoleReplayLoadedProfileIds.Add(profileId);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var line in lines)
            {
                AppendConsoleProfileLine(profile.Id, profile.Name, $"[log] {line}");
                TrackPlayerEventText(line);
            }

            RefreshConsoleText();
        });
    }

    private IReadOnlyList<string> ReadConsoleProfileReplayLines(InstanceProfile profile)
    {
        var logsPath = Path.Combine(profile.DirectoryPath, "Logs");
        var paths = new[]
        {
            Path.Combine(logsPath, "server-main.log"),
            Path.Combine(logsPath, "server-chat.log"),
            Path.Combine(logsPath, "server-audit.log")
        };

        return paths
            .SelectMany(path => ReadTailLines(path, ConsoleProfileReplayLogBytes, ConsoleProfileReplayLogLines))
            .Where(line => !string.IsNullOrWhiteSpace(line) && !ShouldSuppressConsoleLineForUi(line))
            .TakeLast(ConsoleProfileReplayLogLines)
            .ToList();
    }

    private static IReadOnlyList<string> ReadTailLines(string path, int maxBytes, int maxLines)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return [];
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var start = Math.Max(0, stream.Length - maxBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            if (start > 0)
            {
                _ = reader.ReadLine();
            }

            var lines = new Queue<string>();
            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                if (line is null)
                {
                    break;
                }

                lines.Enqueue(line);
                while (lines.Count > maxLines)
                {
                    lines.Dequeue();
                }
            }

            return lines.ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static bool IsSystemConsoleLine(string? line)
    {
        return !string.IsNullOrWhiteSpace(line) &&
               line.TrimStart().StartsWith("[system]", StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshConsoleLogFilterSnapshot(IEnumerable<ConsoleLogFilterRule>? rules)
    {
        _consoleLogFilterRules = ConsoleLogFilterRuleRules.NormalizeMany(rules);
    }

    private bool ShouldSuppressConsoleLineForUi(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var normalized = StripLauncherConsolePrefix(line.Trim());
        if (normalized.Length == 0)
        {
            return false;
        }

        if (ConsoleLogFilterRuleRules.MatchesAny(_consoleLogFilterRules, normalized))
        {
            return true;
        }

        var lower = normalized.ToLowerInvariant();

        if (lower.Contains("[audit]", StringComparison.Ordinal) &&
            lower.Contains("rejected mount position update", StringComparison.Ordinal))
        {
            return true;
        }

        if (lower.Contains("[audit]", StringComparison.Ordinal))
        {
            if (lower.Contains("shift clicked slot", StringComparison.Ordinal)
                || lower.Contains("left clicked slot", StringComparison.Ordinal)
                || lower.Contains("right clicked slot", StringComparison.Ordinal)
                || lower.Contains("middle clicked slot", StringComparison.Ordinal)
                || lower.Contains("slot ", StringComparison.Ordinal) && lower.Contains(" in ", StringComparison.Ordinal)
                || lower.Contains("before: (", StringComparison.Ordinal)
                || lower.Contains("after: (", StringComparison.Ordinal)
                || lower.Contains("harvestablecontents-", StringComparison.Ordinal)
                || lower.Contains("backpack-", StringComparison.Ordinal)
                || lower.Contains("hotbar-", StringComparison.Ordinal)
                || lower.Contains("ground-", StringComparison.Ordinal)
                || lower.Contains("mouse-", StringComparison.Ordinal)
                || lower.Contains(" killed game:", StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (lower.Contains("[talk]", StringComparison.Ordinal)
            || lower.Contains("[chat]", StringComparison.Ordinal)
            || ConsoleChatLineRegex().IsMatch(normalized)
            || ConsoleNotificationLineRegex().IsMatch(normalized)
            || ConsoleJoinLeaveLineRegex().IsMatch(normalized)
            || ConsoleDeathLineRegex().IsMatch(normalized)
            || ConsoleAdminLineRegex().IsMatch(normalized)
            || ConsoleLifecycleLineRegex().IsMatch(normalized)
            || ConsoleSpecialEventLineRegex().IsMatch(normalized))
        {
            return false;
        }

        if (lower.Contains("[warning]", StringComparison.Ordinal)
            || lower.Contains("[error]", StringComparison.Ordinal)
            || lower.Contains("exception", StringComparison.Ordinal)
            || lower.Contains("fatal", StringComparison.Ordinal)
            || lower.Contains("unhandled", StringComparison.Ordinal)
            || lower.Contains("stack trace", StringComparison.Ordinal))
        {
            return false;
        }

        if (lower.Contains(" killed ", StringComparison.Ordinal)
            && !lower.Contains(" killed game:", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static string StripLauncherConsolePrefix(string line)
    {
        const string prefix = "[log]";
        return line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? line[prefix.Length..].TrimStart()
            : line;
    }

    private bool IsConsoleScrolledToBottom()
    {
        var scrollableHeight = Math.Max(0, ConsoleOutputScrollViewer.Extent.Height - ConsoleOutputScrollViewer.Viewport.Height);
        if (scrollableHeight <= ConsoleAutoScrollThreshold)
        {
            return true;
        }

        return scrollableHeight - ConsoleOutputScrollViewer.Offset.Y <= ConsoleAutoScrollThreshold;
    }

    private void TrackPlayerEventText(string line)
    {
        if (!PlayerEventHintRegex().IsMatch(line))
        {
            return;
        }

        var text = $"[{DateTime.Now:HH:mm:ss}] {line}";
        _playerEvents.Insert(0, text);
        if (_playerEvents.Count > 24)
        {
            _playerEvents.RemoveAt(_playerEvents.Count - 1);
        }

        EventTickerCurrentText.Text = _playerEvents[0];
        EventTickerNextText.Text = _playerEvents.Count > 1 ? _playerEvents[1] : _playerEvents[0];
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        DeactivateInputControlsOnBackgroundClick(e.Source);

        if (ShouldSkipWindowDrag(e.Source))
        {
            return;
        }

        BeginMoveDrag(e);
    }

    private void DeactivateInputControlsOnBackgroundClick(object? source)
    {
        if (ShouldKeepInputFocus(source))
        {
            return;
        }

        var closedDropDown = CloseOpenComboBoxDropDowns();
        var focusedElement = FocusManager?.GetFocusedElement();
        if (focusedElement is TextBox or ComboBox or ComboBoxItem or NumericUpDown || closedDropDown)
        {
            FocusManager?.Focus(null!, NavigationMethod.Pointer, KeyModifiers.None);
        }
    }

    private bool CloseOpenComboBoxDropDowns()
    {
        var closedAny = false;
        foreach (var comboBox in this.GetVisualDescendants().OfType<ComboBox>())
        {
            if (!comboBox.IsDropDownOpen)
            {
                continue;
            }

            comboBox.IsDropDownOpen = false;
            closedAny = true;
        }

        return closedAny;
    }

    private static bool ShouldKeepInputFocus(object? source)
    {
        var current = source as StyledElement;
        while (current is not null)
        {
            if (current is TextBox
                or ComboBox
                or ComboBoxItem
                or NumericUpDown)
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private static bool ShouldSkipWindowDrag(object? source)
    {
        var current = source as StyledElement;
        while (current is not null)
        {
            if (current is Button
                or ToggleSwitch
                or CheckBox
                or ComboBox
                or ComboBoxItem
                or TextBox
                or TextBlock
                or SelectableTextBlock
                or ListBox
                or ListBoxItem
                or ScrollViewer
                or ScrollBar
                or Thumb)
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private async void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ChartSummaryText.Text = T("无法打开链接。", "Unable to open the link.");
            });
        }
    }

    private string T(string zh, string en) => _localizationService.Resolve(zh, en);

    private string GetExceptionMessage(Exception exception)
    {
        var message = exception.Message.Trim();
        if (_isChinese || string.IsNullOrWhiteSpace(message))
        {
            return message;
        }

        var translated = TranslateExceptionMessage(message);
        if (!translated.Equals(message, StringComparison.Ordinal) || !ContainsChineseText(translated))
        {
            return translated;
        }

        _logger.LogError(exception, "A user-facing exception did not have an English translation.");
        return "The operation failed. Check the application log for details.";
    }

    private static string TranslateExceptionMessage(string message)
    {
        var exact = message switch
        {
            "档案名称不能为空。" => "Profile name is required.",
            "请先选择服务端版本。" => "Select a server version first.",
            "服务端版本不能为空。" => "Server version is required.",
            "压缩包内未找到 VintagestoryServer.exe。" => "VintagestoryServer.exe was not found in the package.",
            "无法识别服务端目录。" => "Unable to identify the server directory.",
            "生成 serverconfig 超时。" => "Timed out while generating serverconfig.",
            "服务端未生成 serverconfig.json。" => "The server did not generate serverconfig.json.",
            "服务器已在运行中。" => "The server is already running.",
            "ServerHost 正在运行，但控制通道尚未恢复，请稍后重试。" => "ServerHost is running, but its control channel has not recovered. Try again later.",
            "检测到该档案的 ServerHost 已在运行，控制通道正在恢复，请稍后重试。" => "ServerHost for this profile is already running and its control channel is recovering. Try again later.",
            "启动后台控制通道失败。" => "Failed to start the backend control channel.",
            "后台控制通道不可用。" => "The backend control channel is unavailable.",
            "ServerHost 控制通道暂时不可达，请稍后重试。" => "The ServerHost control channel is temporarily unavailable. Try again later.",
            "服务器未运行。" => "The server is not running.",
            "命令不能为空。" => "The command cannot be empty.",
            "后台控制通道已启动，但未能打开服务端进程。" => "The backend control channel started, but could not open the server process.",
            "后台控制通道返回的服务端进程身份不匹配。" => "The server process identity returned by the backend control channel does not match.",
            "后台控制通道实例身份不匹配。" => "The backend control channel instance identity does not match.",
            "等待后台控制通道就绪超时。" => "Timed out waiting for the backend control channel to become ready.",
            "当前服务端进程不是由可恢复的 ServerHost 管理，无法安全发送命令。请停止该外部或旧版进程后重新启动。" => "The current server process is not managed by a recoverable ServerHost. Stop the external or old process, then start it again.",
            "服务端正在自动重启，尚未在限定时间内恢复。Relay 会继续按重试策略处理，请稍后再查看状态。" => "The server is restarting and has not recovered within the time limit. Relay will keep retrying; check the status again later.",
            "存档路径不能为空。" => "The save path cannot be empty.",
            "无效存档路径。" => "The save path is invalid.",
            "请选择档案目录。" => "Select a profile directory.",
            "所选目录不是有效服务端档案目录，缺少 serverconfig.json。" => "The selected directory is not a valid server profile; serverconfig.json is missing.",
            "档案 ID 不能为空。" => "The profile ID cannot be empty.",
            "未找到要更新的档案。" => "The profile to update was not found.",
            _ => string.Empty
        };
        if (!string.IsNullOrEmpty(exact))
        {
            return exact;
        }

        const string missingVersionPrefix = "未找到版本 ";
        const string officialPackageSuffix = " 的官方服务端压缩包，请先下载或导入。";
        if (message.StartsWith(missingVersionPrefix, StringComparison.Ordinal) && message.EndsWith(officialPackageSuffix, StringComparison.Ordinal))
        {
            var version = message[missingVersionPrefix.Length..^officialPackageSuffix.Length];
            return $"The official server package for version {version} was not found. Download or import it first.";
        }

        const string missingServerExecutablePrefix = "未找到服务端程序：";
        if (message.StartsWith(missingServerExecutablePrefix, StringComparison.Ordinal))
        {
            return $"The server executable was not found: {message[missingServerExecutablePrefix.Length..]}";
        }

        const string missingServerHostPrefix = "未找到独立服务端控制程序 ";
        const string missingServerHostSuffix = "，请重新安装或重新发布 LauncherGo。";
        if (message.StartsWith(missingServerHostPrefix, StringComparison.Ordinal) && message.EndsWith(missingServerHostSuffix, StringComparison.Ordinal))
        {
            var programName = message[missingServerHostPrefix.Length..^missingServerHostSuffix.Length];
            return $"The standalone server control program was not found: {programName}. Reinstall or republish LauncherGo.";
        }

        const string generateConfigFailedPrefix = "生成 serverconfig 失败，退出码 ";
        if (message.StartsWith(generateConfigFailedPrefix, StringComparison.Ordinal))
        {
            var details = message[generateConfigFailedPrefix.Length..];
            var separator = details.IndexOf('。');
            if (separator >= 0)
            {
                var exitCode = details[..separator];
                var stderr = details[(separator + 1)..].Trim();
                return string.IsNullOrWhiteSpace(stderr) || ContainsChineseText(stderr)
                    ? $"Failed to generate serverconfig (exit code {exitCode})."
                    : $"Failed to generate serverconfig (exit code {exitCode}). {stderr}";
            }
        }

        const string controlChannelExitedPrefix = "后台控制通道启动后已退出，退出码：";
        if (message.StartsWith(controlChannelExitedPrefix, StringComparison.Ordinal))
        {
            return $"The backend control channel exited after startup (exit code {message[controlChannelExitedPrefix.Length..].TrimEnd('。')}).";
        }

        const string controlChannelTimeoutPrefix = "等待后台控制通道就绪超时：";
        if (message.StartsWith(controlChannelTimeoutPrefix, StringComparison.Ordinal))
        {
            var details = TranslateExceptionMessage(message[controlChannelTimeoutPrefix.Length..]);
            return ContainsChineseText(details)
                ? "Timed out waiting for the backend control channel."
                : $"Timed out waiting for the backend control channel: {details}";
        }

        const string externalProcessPrefix = "检测到该档案存在外部或旧版服务端进程（PID=";
        const string externalProcessSuffix = "），无法恢复命令输入。请先停止该进程，再由 LauncherGo 重新启动。";
        if (message.StartsWith(externalProcessPrefix, StringComparison.Ordinal) && message.EndsWith(externalProcessSuffix, StringComparison.Ordinal))
        {
            var processId = message[externalProcessPrefix.Length..^externalProcessSuffix.Length];
            return $"An external or old server process was detected for this profile (PID={processId}), so command input cannot be recovered. Stop it, then start it again through LauncherGo.";
        }

        const string missingProfileDirectoryPrefix = "档案目录不存在：";
        if (message.StartsWith(missingProfileDirectoryPrefix, StringComparison.Ordinal))
        {
            return $"The profile directory does not exist: {message[missingProfileDirectoryPrefix.Length..]}";
        }

        const string forceTerminateFailedPrefix = "强制终止服务器进程失败：";
        if (message.StartsWith(forceTerminateFailedPrefix, StringComparison.Ordinal))
        {
            var details = TranslateExceptionMessage(message[forceTerminateFailedPrefix.Length..]);
            return ContainsChineseText(details)
                ? "Failed to force terminate the server process."
                : $"Failed to force terminate the server process: {details}";
        }

        const string stopRestartingRelayFailedPrefix = "停止等待重启的后台 Relay 失败：";
        if (message.StartsWith(stopRestartingRelayFailedPrefix, StringComparison.Ordinal))
        {
            var details = TranslateExceptionMessage(message[stopRestartingRelayFailedPrefix.Length..]);
            return ContainsChineseText(details)
                ? "Failed to stop the restarting backend Relay."
                : $"Failed to stop the restarting backend Relay: {details}";
        }

        const string remainingProcessPrefix = "停服后仍检测到 ";
        const string remainingProcessSuffix = " 个同档案服务端进程残留，请稍后重试。";
        if (message.StartsWith(remainingProcessPrefix, StringComparison.Ordinal) && message.EndsWith(remainingProcessSuffix, StringComparison.Ordinal))
        {
            var count = message[remainingProcessPrefix.Length..^remainingProcessSuffix.Length];
            return $"{count} server process(es) for the same profile are still running after stop. Try again later.";
        }

        const string wrongTrackedProfilePrefix = "当前控制器跟踪的进程不属于目标档案 ";
        const string wrongTrackedProfileSuffix = "，已拒绝停止以避免误停其他服务器。";
        if (message.StartsWith(wrongTrackedProfilePrefix, StringComparison.Ordinal) && message.EndsWith(wrongTrackedProfileSuffix, StringComparison.Ordinal))
        {
            var profileName = message[wrongTrackedProfilePrefix.Length..^wrongTrackedProfileSuffix.Length];
            return $"The process tracked by the current controller does not belong to profile {profileName}. Stop was refused to avoid terminating another server.";
        }

        return message;
    }

    private static bool ContainsChineseText(string value) => value.Any(character => character is >= '\u4e00' and <= '\u9fff');

    private void OnRepositoryClick(object? sender, RoutedEventArgs e) => OpenUrl("https://github.com/vscn-studio/LauncherGo");

    private void OnFeedbackClick(object? sender, RoutedEventArgs e) => OpenUrl("https://github.com/vscn-studio/LauncherGo/issues");

    private void OnSponsorClick(object? sender, RoutedEventArgs e) => OpenUrl("https://vscn.studio/sponsors");

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnToggleMaximizeClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateMaximizeButton();
    }

    private void UpdateMaximizeButton()
    {
        var isMaximized = WindowState == WindowState.Maximized;
        MaximizeIconPath.IsVisible = !isMaximized;
        RestoreIconCanvas.IsVisible = isMaximized;
        ToolTip.SetTip(ToggleMaximizeButton, isMaximized ? T("还原", "Restore") : T("最大化", "Maximize"));
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => RequestWindowClose();

    private void OnHomeNavClick(object? sender, RoutedEventArgs e) => SelectTab(MainTab.Monitor);

    private void OnMonitorNavClick(object? sender, RoutedEventArgs e) => SelectTab(MainTab.Monitor);

    private void OnConsoleNavClick(object? sender, RoutedEventArgs e) => SelectTab(MainTab.Console);

    private void OnInstanceManageNavClick(object? sender, RoutedEventArgs e) => SelectTab(MainTab.InstanceManage);

    private void OnSettingsNavClick(object? sender, RoutedEventArgs e) => SelectTab(MainTab.Settings);

    private void OnConnectionNavClick(object? sender, RoutedEventArgs e) => SelectTab(MainTab.Connection);

    private void OnServerStatusCardClick(object? sender, RoutedEventArgs e) => SelectMetric(HomeMetric.Server);

    private void OnRobotStatusCardClick(object? sender, RoutedEventArgs e) => SelectMetric(HomeMetric.Robot);

    private void OnOnlinePlayersCardClick(object? sender, RoutedEventArgs e) => SelectMetric(HomeMetric.Players);

    private async void OnDashboardPlayerClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DashboardPlayerItem { Player: not null } item })
            return;

        var window = new ServerPlayerDetailsWindow(item.Player, _isChinese);
        await window.ShowDialog(this);
    }

    private void OnNetworkStatusCardClick(object? sender, RoutedEventArgs e) => SelectMetric(HomeMetric.Network);

    private void OnProfilesSubTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.InstanceManage);
        SelectInstanceManageTab(InstanceManageTab.Profiles);
    }

    private void OnConfigSubTabClick(object? sender, RoutedEventArgs e)
    {
        _editingConfigProfileId = string.Empty;
        SelectTab(MainTab.InstanceManage);
        SelectInstanceManageTab(InstanceManageTab.Config);
    }

    private async void OnEditProfileConfigClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProfileListItem item })
        {
            return;
        }

        await OpenProfileConfigEditorAsync(item.Id);
    }

    private void OnSavesSubTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.InstanceManage);
        SelectInstanceManageTab(InstanceManageTab.Saves);
    }

    private void OnAutomationSubTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.InstanceManage);
        SelectInstanceManageTab(InstanceManageTab.Automation);
    }

    private void OnModsSubTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.InstanceManage);
        SelectInstanceManageTab(InstanceManageTab.Mods);
    }

    private void OnDownloadVersionsSubTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.InstanceManage);
        SelectInstanceManageTab(InstanceManageTab.DownloadVersions);
    }

    private void OnServerSettingsTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.Settings);
        SelectSettingsTab(SettingsTab.Server);
    }

    private void OnAppearanceSettingsTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.Settings);
        SelectSettingsTab(SettingsTab.Appearance);
    }

    private void OnNetworkSettingsTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.Settings);
        SelectSettingsTab(SettingsTab.Network);
    }

    private void OnAdvancedSettingsTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.Settings);
        SelectSettingsTab(SettingsTab.Advanced);
    }

    private void OnAboutSettingsTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.Settings);
        SelectSettingsTab(SettingsTab.About);
    }

    private void OnSponsorsSettingsTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.Settings);
        SelectSettingsTab(SettingsTab.Sponsors);
    }

    private void OnContributorsSettingsTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.Settings);
        SelectSettingsTab(SettingsTab.Contributors);
    }

    private void OnSettingsServerSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_isApplyingServerSettings)
        {
            return;
        }

        SaveServerSettings();
    }

    private void OnSettingsServerRefreshClick(object? sender, RoutedEventArgs e)
    {
        RefreshServerSettingsEditor();
    }

    private void OnSettingsAutoStartServerProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingServerSettings)
        {
            return;
        }

        SaveServerSettings(refreshEditor: false);
    }

    private void OnSettingsAutoStartAddProfileClick(object? sender, RoutedEventArgs e)
    {
        if (SettingsAutoStartAddProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            return;
        }

        var ids = LoadAutoStartProfileIds();
        ids.Add(profile.Id);
        SaveAutoStartProfileIds(ids);
        SettingsAutoStartAddProfileComboBox.SelectedIndex = -1;
    }

    private void OnSettingsAutoStartRemoveSelectedProfileClick(object? sender, RoutedEventArgs e)
    {
        var selected = _settingsAutoStartTargetItems.FirstOrDefault(static item => item.IsSelected)
                       ?? _settingsAutoStartTargetItems.LastOrDefault();
        if (selected is null)
        {
            return;
        }

        var ids = LoadAutoStartProfileIds();
        ids.Remove(selected.ProfileId);
        SaveAutoStartProfileIds(ids);
    }

    private void OnSettingsAutoStartTargetChipClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: LaunchTargetItem item } button)
        {
            return;
        }

        foreach (var target in _settingsAutoStartTargetItems)
        {
            target.IsSelected = false;
        }

        item.IsSelected = button.IsChecked == true;
    }

    private async void OnSettingsBrowseWorkspaceDirectoryClick(object? sender, RoutedEventArgs e)
    {
        await BrowseFolderToTextBoxAsync(SettingsWorkspaceDirectoryTextBox, T("选择工作目录", "Select workspace directory"));
    }

    private async void OnSettingsBrowseSaveCompressionPathClick(object? sender, RoutedEventArgs e)
    {
        await BrowseFolderToTextBoxAsync(SettingsSaveCompressionPathTextBox, T("选择存档压缩路径", "Select save compression path"));
    }

    private async void OnSettingsCompressExistingBackupsClick(object? sender, RoutedEventArgs e)
    {
        if (_isApplyingServerSettings)
        {
            return;
        }

        try
        {
            SettingsCompressExistingBackupsButton.IsEnabled = false;
            var count = await _saveService.CompressExistingBackupsAsync();
            SettingsServerStatusTextBlock.Text = T(
                $"已压缩 {count} 个已有备份。",
                $"Compressed {count} existing backup(s).");
        }
        catch (Exception ex)
        {
            SettingsServerStatusTextBlock.Text = T(
                $"压缩已有备份失败：{ex.Message}",
                $"Failed to compress existing backups: {ex.Message}");
        }
        finally
        {
            SettingsCompressExistingBackupsButton.IsEnabled = true;
        }
    }

    private async void OnSettingsOpenLogClick(object? sender, RoutedEventArgs e)
    {
        await OpenAppLogsAsync();
    }

    private async void OnSettingsClearDownloadCacheClick(object? sender, RoutedEventArgs e)
    {
        await ClearDownloadCacheAsync();
    }

    private void OnSettingsResetAllClick(object? sender, RoutedEventArgs e)
    {
        ResetAllSettingsAndRestartToGuide();
    }

    private void OnContributorOpenClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && !string.IsNullOrWhiteSpace(url))
        {
            OpenUrl(url);
        }
    }

    private void OnAboutDocumentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string fileName } || string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        var path = FindBundledContentPath(fileName);
        if (string.IsNullOrWhiteSpace(path))
        {
            SetAboutActionStatus(T($"未找到 {fileName}。", $"{fileName} was not found."));
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo { UseShellExecute = true };
            if (OperatingSystem.IsWindows())
            {
                startInfo.FileName = "notepad.exe";
                startInfo.ArgumentList.Add(path);
            }
            else
            {
                startInfo.FileName = path;
            }

            Process.Start(startInfo);
            AboutActionStatusTextBlock.IsVisible = false;
        }
        catch (Exception ex)
        {
            SetAboutActionStatus(T($"打开 {fileName} 失败：{ex.Message}", $"Failed to open {fileName}: {ex.Message}"));
        }
    }

    private void SetAboutActionStatus(string text)
    {
        AboutActionStatusTextBlock.Text = text;
        AboutActionStatusTextBlock.IsVisible = true;
    }

    private void OnConnectionFrpTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.Connection);
        SelectConnectionTab(ConnectionTab.Frp);
    }

    private void OnConnectionEasyTierTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.Connection);
        SelectConnectionTab(ConnectionTab.EasyTier);
    }

    private void OnConnectionRobotTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.Connection);
        SelectConnectionTab(ConnectionTab.Robot);
    }

    private void OnDiscordStatusChanged(object? sender, DiscordRuntimeStatus status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!status.IsRunning && !string.IsNullOrWhiteSpace(status.LastError))
                ShowToast(T($"Discord 连接失败：{status.LastError}", $"Discord connection failed: {status.LastError}"));
            if (_selectedConnectionTab == ConnectionTab.Discord)
                RefreshConnectionRuntimeStatus();
        });
    }

    private void OnDiscordOutputReceived(object? sender, string line)
    {
        Dispatcher.UIThread.Post(() => AppendConsoleLine(line));
    }

    private void OnConnectionDiscordTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.Connection);
        SelectConnectionTab(ConnectionTab.Discord);
    }

    private void OnConnectionAuthTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.Connection);
        SelectConnectionTab(ConnectionTab.Auth);
    }

    private void OnServerBridgeSubTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.InstanceManage);
        SelectInstanceManageTab(InstanceManageTab.ServerBridge);
    }

    private void OnConnectionGatewayTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.Connection);
        SelectConnectionTab(ConnectionTab.Gateway);
    }

    private async void OnGatewaySaveClick(object? sender, RoutedEventArgs e)
    {
        SaveGatewaySettings(updateStatus: false, refreshEditor: false);
        if (!_tcpGatewayService.GetCurrentStatus().IsRunning)
        {
            SetConnectionStatus(T("TCP 网关配置已保存。", "TCP gateway configuration saved."));
            return;
        }

        try
        {
            var status = await _tcpGatewayService.ReloadAsync(_preferencesService.Load().TcpGateway);
            ApplyGatewayRuntimeStatus(status);
            SetConnectionStatus(status.RequiresRestart
                ? T("后端和规则已热重载；监听地址或端口的改动将在重启网关后生效。", "Backends and rules were reloaded; listener address or port changes apply after restarting the gateway.")
                : T("TCP 网关配置已热重载。", "TCP gateway configuration reloaded."));
        }
        catch (Exception ex)
        {
            var message = TranslateGatewayError(GetExceptionMessage(ex));
            SetConnectionStatus(T($"TCP 网关配置重载失败：{message}", $"TCP gateway configuration reload failed: {message}"));
        }
    }

    private async void OnGatewayRefreshClick(object? sender, RoutedEventArgs e)
    {
        RefreshConnectionSettingsEditor();
        await RefreshGatewayStatusAsync();
    }

    private void OnGatewayAddBackendClick(object? sender, RoutedEventArgs e)
    {
        var profiles = _profileService.GetProfiles();
        var backend = new TcpGatewayBackend
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = T("本地服务端", "Local Server"),
            Host = "127.0.0.1",
            Port = 42420,
            Weight = 1,
            RoutingState = TcpGatewayBackendRoutingState.Online,
            ProfileOptions = profiles
        };
        backend.SelectedProfile = profiles.FirstOrDefault();
        _gatewayBackendItems.Add(backend);
    }

    private void OnTcpGatewayStatusChanged(object? sender, TcpGatewayRuntimeStatus status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ApplyGatewayRuntimeStatus(status);
            if (_selectedTab == MainTab.Connection && _selectedConnectionTab == ConnectionTab.Gateway)
            {
                SetConnectionStatus(BuildGatewayRuntimeStatusText(status), notify: false);
            }
        });
    }

    private void OnGatewayRemoveBackendClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TcpGatewayBackend backend })
        {
            _gatewayBackendItems.Remove(backend);
        }
    }

    private async void OnGatewayShowStatisticsClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GatewayBackendRuntimeItem item })
        {
            return;
        }

        var window = new GatewayBackendStatisticsWindow(item.RuntimeStatus, _isChinese);
        _gatewayStatisticsWindows.Add(window);
        window.Closed += (_, _) => _gatewayStatisticsWindows.Remove(window);
        try
        {
            await window.ShowDialog(this);
        }
        catch (Exception ex)
        {
            _gatewayStatisticsWindows.Remove(window);
            SetConnectionStatus(T(
                $"打开网关统计失败：{GetExceptionMessage(ex)}",
                $"Failed to open gateway statistics: {GetExceptionMessage(ex)}"));
        }
    }

    private async void OnGatewayDeployRedirectModClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            SaveGatewaySettings(updateStatus: false, refreshEditor: false);
            var deployed = await _gatewayRedirectModService.DeployAsync(
                _preferencesService.Load().TcpGateway,
                _profileService.GetProfiles());
            SetConnectionStatus(T(
                $"已向 {deployed} 个关联实例部署 VSCN-Studio 重定向模组。",
                $"VSCN-Studio redirect mod deployed to {deployed} associated instance(s)."));
        }
        catch (Exception ex)
        {
            SetConnectionStatus(T(
                $"部署重定向模组失败：{GetExceptionMessage(ex)}",
                $"Redirect mod deployment failed: {GetExceptionMessage(ex)}"));
        }
    }

    private async void OnGatewayShowRoutingHistoryClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var history = await _tcpGatewayService.GetRoutingHistoryAsync();
            var window = new GatewayRoutingHistoryWindow(
                history,
                _tcpGatewayService.GetCurrentStatus().RoutingHistoryLogPath,
                _isChinese);
            await window.ShowDialog(this);
        }
        catch (Exception ex)
        {
            SetConnectionStatus(T(
                $"打开网关路由历史失败：{GetExceptionMessage(ex)}",
                $"Failed to open gateway routing history: {GetExceptionMessage(ex)}"));
        }
    }

    private async void OnGatewayRedirectPlayerClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GatewayBackendRuntimeItem item }) return;
        var request = await ShowGatewayRedirectDialogAsync(item, GatewayRedirectOperation.Player);
        if (request is null) return;
        await SendGatewayRedirectCommandAsync(item, request, "/launchergateway redirect");
    }

    private async void OnGatewayEvacuateClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GatewayBackendRuntimeItem item }) return;
        var request = await ShowGatewayRedirectDialogAsync(item, GatewayRedirectOperation.Evacuate);
        if (request is null) return;
        await SendGatewayRedirectCommandAsync(item, request, "/launchergateway evacuate");
    }

    private async void OnGatewayEnterMaintenanceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GatewayBackendRuntimeItem item }) return;
        var source = _gatewayBackendItems.FirstOrDefault(backend =>
            backend.Id.Equals(item.BackendId, StringComparison.OrdinalIgnoreCase));
        if (source is null) return;

        source.RoutingState = TcpGatewayBackendRoutingState.Draining;
        source.MaintenanceTargetServerId = string.Empty;
        try
        {
            await SaveAndReloadGatewaySettingsAsync();
            await _tcpGatewayService.RecordRoutingHistoryAsync(new TcpGatewayRoutingHistoryEntry
            {
                Action = "Maintenance",
                SourceServerId = source.Id,
                TargetServerId = string.Empty,
                Details = "Backend entered Draining maintenance mode."
            });
            await RefreshGatewayStatusAsync();
            SetConnectionStatus(T(
                "后端已进入 Draining 维护模式。新玩家将不再进入，当前玩家保持连接。",
                "The backend entered Draining maintenance mode. New players are blocked while current players stay connected."));
        }
        catch (Exception ex)
        {
            SetConnectionStatus(T(
                $"切换维护模式失败：{GetExceptionMessage(ex)}",
                $"Failed to enter maintenance mode: {GetExceptionMessage(ex)}"));
        }
    }

    private async Task<GatewayRedirectRequest?> ShowGatewayRedirectDialogAsync(
        GatewayBackendRuntimeItem source,
        GatewayRedirectOperation operation)
    {
        var gatewayStatus = _tcpGatewayService.GetCurrentStatus();
        if (!gatewayStatus.IsRunning || !gatewayStatus.IsListening)
        {
            SetConnectionStatus(T("请先启动 TCP 网关后再执行重定向。", "Start TCP Gateway before redirecting players."));
            return null;
        }

        if (gatewayStatus.RequiresRestart)
        {
            SetConnectionStatus(T(
                "网关监听配置已变更，请先重启 TCP 网关。",
                "Gateway listener configuration changed; restart TCP Gateway first."));
            return null;
        }

        var sourceDefinition = _gatewayBackendItems.FirstOrDefault(backend =>
            backend.Id.Equals(source.BackendId, StringComparison.OrdinalIgnoreCase));
        if (sourceDefinition is null || string.IsNullOrWhiteSpace(sourceDefinition.ProfileId))
        {
            SetConnectionStatus(T(
                "该后端未关联本地实例，无法向服务端下发重定向命令。",
                "This backend is not linked to a local instance, so LauncherGo cannot send a redirect command."));
            return null;
        }

        var targets = _gatewayBackendRuntimeItems
            .Where(item => !item.BackendId.Equals(source.BackendId, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.RuntimeStatus.RoutingState is TcpGatewayBackendRoutingState.Online or TcpGatewayBackendRoutingState.Draining)
            .Where(item => item.RuntimeStatus.IsHealthy)
            .Select(item => new GatewayRedirectTargetItem(
                item.BackendId,
                $"{item.Name} ({item.StatusText})"))
            .ToList();
        if (targets.Count == 0)
        {
            SetConnectionStatus(T(
                "没有可用的重定向目标。目标必须为 Online 或 Draining 且 TCP 可达。",
                "No redirect target is available. A target must be Online or Draining and TCP reachable."));
            return null;
        }

        var window = new GatewayRedirectWindow(operation, source.Name, targets, _isChinese);
        return await window.ShowDialog<GatewayRedirectRequest?>(this);
    }

    private async Task SendGatewayRedirectCommandAsync(
        GatewayBackendRuntimeItem source,
        GatewayRedirectRequest request,
        string commandPrefix,
        bool recordHistory = true)
    {
        var sourceDefinition = _gatewayBackendItems.FirstOrDefault(backend =>
            backend.Id.Equals(source.BackendId, StringComparison.OrdinalIgnoreCase));
        if (sourceDefinition is null || string.IsNullOrWhiteSpace(sourceDefinition.ProfileId))
        {
            throw new InvalidOperationException(T(
                "来源后端未关联本地实例。",
                "The source backend is not linked to a local instance."));
        }

        var playerToken = request.PlayerNameOrUid.Trim();
        if (request.Operation == GatewayRedirectOperation.Player &&
            (playerToken.Any(char.IsWhiteSpace) || playerToken.Contains('"')))
        {
            throw new InvalidOperationException(T(
                "玩家名称或 UID 不能包含空格或引号。",
                "Player name or UID cannot contain spaces or quotes."));
        }

        var command = request.Operation == GatewayRedirectOperation.Player
            ? $"{commandPrefix} {playerToken} {request.TargetServerId}"
            : $"{commandPrefix} {request.TargetServerId}";
        await _serverProcessService.SendCommandAsync(sourceDefinition.ProfileId, command);
        if (recordHistory)
        {
            await _tcpGatewayService.RecordRoutingHistoryAsync(new TcpGatewayRoutingHistoryEntry
            {
                Action = request.Operation == GatewayRedirectOperation.Player ? "PlayerRedirect" : "Evacuate",
                SourceServerId = sourceDefinition.Id,
                TargetServerId = request.TargetServerId,
                Details = request.Operation == GatewayRedirectOperation.Player
                    ? $"Redirect command sent for player '{playerToken}'."
                    : "Evacuation command sent to the associated local server instance."
            });
        }

        SetConnectionStatus(request.Operation == GatewayRedirectOperation.Player
            ? T("已向服务端发送玩家重定向请求。", "Player redirect request sent to the server.")
            : T("已向服务端发送整服疏散请求。", "Server evacuation request sent to the server."));
        await RefreshGatewayStatusAsync();
    }

    private async Task SaveAndReloadGatewaySettingsAsync()
    {
        SaveGatewaySettings(updateStatus: false, refreshEditor: false);
        if (!_tcpGatewayService.GetCurrentStatus().IsRunning)
        {
            ApplyGatewayRuntimeStatus(_tcpGatewayService.GetCurrentStatus());
            return;
        }

        var status = await _tcpGatewayService.ReloadAsync(_preferencesService.Load().TcpGateway);
        ApplyGatewayRuntimeStatus(status);
        if (status.RequiresRestart)
        {
            throw new InvalidOperationException(T(
                "网关监听配置已变更，请先重启 TCP 网关。",
                "Gateway listener configuration changed; restart TCP Gateway first."));
        }

        await RefreshGatewayStatusAsync();
    }

    private async void OnGatewayToggleClick(object? sender, RoutedEventArgs e)
    {
        if (_isTogglingGateway)
        {
            return;
        }

        _isTogglingGateway = true;
        GatewayToggleButton.IsEnabled = false;
        try
        {
            if (_tcpGatewayService.GetCurrentStatus().IsRunning)
            {
                await _tcpGatewayService.StopAsync(TimeSpan.FromSeconds(10));
                SetConnectionStatus(T("TCP 网关已停止。", "TCP gateway stopped."));
            }
            else
            {
                SaveGatewaySettings(updateStatus: false, refreshEditor: false);
                var settings = _preferencesService.Load().TcpGateway;
                await _tcpGatewayService.StartAsync(settings);
                SetConnectionStatus(T("TCP 网关已启动。", "TCP gateway started."));
            }
        }
        catch (Exception ex)
        {
            var message = TranslateGatewayError(GetExceptionMessage(ex));
            SetConnectionStatus(T($"TCP 网关启动/停止失败：{message}", $"TCP gateway start/stop failed: {message}"));
        }
        finally
        {
            _isTogglingGateway = false;
            GatewayToggleButton.IsEnabled = true;
            await RefreshGatewayStatusAsync();
            UpdateCardValues(_serverProcessService.GetCachedStatus());
        }
    }

    private void OnLogsNavClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.InstanceManage);
        SelectInstanceManageTab(InstanceManageTab.Logs);
        _logsNavSelected = true;
        RefreshSidebarSelection();
    }

    private void EnsureGitHubProxyOptions()
    {
        var selectedValue = (SettingsGitHubProxyComboBox.SelectedItem as ConfigChoiceOption)?.Value;
        var wasApplyingLocalizedOptions = _isApplyingLocalizedOptions;
        _isApplyingLocalizedOptions = true;
        try
        {
            _gitHubProxyOptions.Clear();
            _gitHubProxyOptions.Add(new ConfigChoiceOption(GitHubProxyKind.Direct.ToString(), T("直连", "Direct")));
            _gitHubProxyOptions.Add(new ConfigChoiceOption(GitHubProxyKind.GhProxy.ToString(), "gh-proxy.com"));
            _gitHubProxyOptions.Add(new ConfigChoiceOption(GitHubProxyKind.GhProxyV6.ToString(), "v6.gh-proxy.com"));
            _gitHubProxyOptions.Add(new ConfigChoiceOption(GitHubProxyKind.GhProxyHk.ToString(), "hk.gh-proxy.com"));
            _gitHubProxyOptions.Add(new ConfigChoiceOption(GitHubProxyKind.GhProxyCdn.ToString(), "cdn.gh-proxy.com"));
            _gitHubProxyOptions.Add(new ConfigChoiceOption(GitHubProxyKind.GhProxyEdgeOne.ToString(), "edgeone.gh-proxy.com"));
            SettingsGitHubProxyComboBox.ItemsSource = _gitHubProxyOptions;
            SelectConfigChoiceByValue(
                SettingsGitHubProxyComboBox,
                _gitHubProxyOptions,
                selectedValue ?? GitHubProxyKind.Direct.ToString());
        }
        finally
        {
            _isApplyingLocalizedOptions = wasApplyingLocalizedOptions;
        }
    }

    private GitHubProxyKind GetSelectedGitHubProxy()
    {
        var value = (SettingsGitHubProxyComboBox.SelectedItem as ConfigChoiceOption)?.Value;
        return Enum.TryParse<GitHubProxyKind>(value, out var proxy) ? proxy : GitHubProxyKind.Direct;
    }

    private void OnGitHubProxySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_isApplyingNetworkSettings && !_isApplyingLocalizedOptions)
            SaveNetworkSettings(refreshEditor: false);
    }

    private async void OnSettingsCheckUpdatesClick(object? sender, RoutedEventArgs e) =>
        await CheckLauncherUpdatesAsync(onlyShowWhenAvailable: false, includePrerelease: true);

    private async Task CheckLauncherUpdatesAsync(bool onlyShowWhenAvailable, bool includePrerelease)
    {
        SettingsCheckUpdatesButton.IsEnabled = false;
        SettingsUpdateStatusTextBlock.Text = T("正在检查更新...", "Checking for updates...");
        try
        {
            var preferences = _preferencesService.Load();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await _launcherUpdateService.CheckLatestAsync(
                preferences.GitHubProxy,
                includePrerelease,
                cts.Token);
            SettingsUpdateStatusTextBlock.Text = result.IsUpdateAvailable
                ? T($"发现新版本 {result.LatestVersion}", $"Version {result.LatestVersion} is available")
                : T($"当前已是最新版本 {result.CurrentVersion}", $"Up to date: {result.CurrentVersion}");
            if (result.IsUpdateAvailable || !onlyShowWhenAvailable)
            {
                var window = new LauncherUpdateWindow(_launcherUpdateService, result, preferences.GitHubProxy, _isChinese);
                await window.ShowDialog(this);
            }
        }
        catch (OperationCanceledException)
        {
            SettingsUpdateStatusTextBlock.Text = T("检查更新超时。", "Update check timed out.");
        }
        catch (Exception ex)
        {
            SettingsUpdateStatusTextBlock.Text = T($"检查更新失败：{ex.Message}", $"Update check failed: {ex.Message}");
        }
        finally
        {
            SettingsCheckUpdatesButton.IsEnabled = true;
        }
    }

    private void OnLogsRefreshClick(object? sender, RoutedEventArgs e)
    {
        RefreshLogItems();
    }

    private void OnViewProfileLogClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProfileLogListItem item })
        {
            return;
        }

        if (!Directory.Exists(item.LogDirectoryPath))
        {
            ShowToast(T(
                $"日志文件夹不存在：{item.LogDirectoryPath}",
                $"Log folder not found: {item.LogDirectoryPath}"), ToastKind.Error);
            return;
        }

        try
        {
            OpenLocalFile(item.LogDirectoryPath);
        }
        catch (Exception ex)
        {
            ShowToast(T(
                $"打开日志文件夹失败：{ex.Message}",
                $"Failed to open log folder: {ex.Message}"), ToastKind.Error);
        }
    }

    private async void OnConnectionFrpImportClick(object? sender, RoutedEventArgs e)
    {
        await ImportConnectionExecutableAsync(ConnectionProcessKind.Frp);
    }

    private async void OnConnectionThirdPartyFrpcImportClick(object? sender, RoutedEventArgs e)
    {
        await ImportConnectionExecutableAsync(ConnectionProcessKind.ThirdPartyFrpc);
    }

    private void OnEasyTierSaveClick(object? sender, RoutedEventArgs e) => SaveEasyTierSettings();

    private void OnEasyTierRefreshClick(object? sender, RoutedEventArgs e)
    {
        RefreshConnectionSettingsEditor();
        RefreshConnectionRuntimeStatus();
    }

    private async void OnEasyTierImportCoreClick(object? sender, RoutedEventArgs e)
    {
        await ImportEasyTierExecutableAsync(core: true);
    }

    private async void OnEasyTierImportCliClick(object? sender, RoutedEventArgs e)
    {
        await ImportEasyTierExecutableAsync(core: false);
    }

    private async void OnEasyTierToggleClick(object? sender, RoutedEventArgs e)
    {
        await ToggleEasyTierAsync();
    }

    private async void OnEasyTierCopyRoomCodeClick(object? sender, RoutedEventArgs e)
    {
        await CopyEasyTierValueAsync(EasyTierRoomCodeTextBox.Text, T("MVL 分享码", "MVL room code"));
    }

    private async void OnEasyTierCopyGameAddressClick(object? sender, RoutedEventArgs e)
    {
        await CopyEasyTierValueAsync(EasyTierGameAddressTextBox.Text, T("ET 游戏地址", "ET game address"));
    }

    private async Task ImportEasyTierExecutableAsync(bool core)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = core
                ? T("导入 EasyTier Core", "Import EasyTier Core")
                : T("导入 EasyTier CLI", "Import EasyTier CLI"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Executable")
                {
                    Patterns = ["*.exe"]
                }
            ]
        });

        var sourcePath = TryGetLocalPath(files.FirstOrDefault());
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        try
        {
            if (core)
            {
                await _easyTierService.ImportCoreExecutableAsync(sourcePath);
            }
            else
            {
                await _easyTierService.ImportCliExecutableAsync(sourcePath);
            }

            SetConnectionStatus(T($"已导入：{Path.GetFileName(sourcePath)}", $"Imported: {Path.GetFileName(sourcePath)}"));
        }
        catch (Exception ex)
        {
            SetConnectionStatus(T($"导入失败：{ex.Message}", $"Import failed: {ex.Message}"));
        }
    }

    private async Task ToggleEasyTierAsync()
    {
        if (_isTogglingEasyTier)
        {
            return;
        }

        _isTogglingEasyTier = true;
        EasyTierToggleButton.IsEnabled = false;
        try
        {
            if (_easyTierService.GetCurrentStatus().IsRunning)
            {
                await StopEasyTierAsync();
            }
            else
            {
                SaveEasyTierSettings(updateStatus: false, refreshEditor: false);
                await StartEasyTierAsync();
            }
        }
        finally
        {
            _isTogglingEasyTier = false;
            EasyTierToggleButton.IsEnabled = true;
            UpdateEasyTierActionButtons();
            UpdateCardValues(_serverProcessService.GetCachedStatus());
        }
    }

    private async Task StartEasyTierAsync()
    {
        if (_easyTierService.GetCurrentStatus().IsRunning)
        {
            SetConnectionStatus(T("EasyTier 已在运行。", "EasyTier is already running."));
            return;
        }

        try
        {
            await _easyTierService.StartAsync();
            var status = _easyTierService.GetCurrentStatus();
            SetConnectionStatus(
                T($"EasyTier 已启动，PID={status.ProcessId}。", $"EasyTier started, PID={status.ProcessId}."));
        }
        catch (Exception ex)
        {
            SetConnectionStatus(T($"EasyTier 启动失败：{ex.Message}", $"EasyTier start failed: {ex.Message}"));
        }
    }

    private async Task StopEasyTierAsync()
    {
        try
        {
            await _easyTierService.StopAsync(TimeSpan.FromSeconds(15));
            SetConnectionStatus(T("EasyTier 已停止。", "EasyTier stopped."));
        }
        catch (Exception ex)
        {
            SetConnectionStatus(T($"EasyTier 停止失败：{ex.Message}", $"EasyTier stop failed: {ex.Message}"));
        }
    }

    private async Task CopyEasyTierValueAsync(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            SetConnectionStatus(T($"没有可复制的 {label}。", $"No {label} is available to copy."));
            return;
        }

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                throw new InvalidOperationException(T("当前窗口没有可用的剪贴板。", "No clipboard is available for the current window."));
            }

            await clipboard.SetTextAsync(value.Trim());
            SetConnectionStatus(T($"已复制 {label}。", $"Copied {label}."));
        }
        catch (Exception ex)
        {
            SetConnectionStatus(T($"复制失败：{ex.Message}", $"Copy failed: {ex.Message}"));
        }
    }

    private void OnConnectionRefreshClick(object? sender, RoutedEventArgs e)
    {
        RefreshConnectionSettingsEditor();
        RefreshConnectionRuntimeStatus();
        if (_selectedConnectionTab == ConnectionTab.Robot)
        {
            RefreshRobotProfileItems();
        }

        if (_selectedConnectionTab == ConnectionTab.Auth)
        {
            _ = RefreshAuthProfilesAsync();
        }

        if (_selectedConnectionTab == ConnectionTab.Gateway)
        {
            _ = RefreshGatewayStatusAsync();
        }
    }

    private void OnConnectionFrpSaveClick(object? sender, RoutedEventArgs e) => SaveFrpSettings();

    private async void OnConnectionFrpToggleClick(object? sender, RoutedEventArgs e)
    {
        await ToggleConnectionProcessAsync(ConnectionProcessKind.Frp);
    }

    private async void OnConnectionThirdPartyFrpcToggleClick(object? sender, RoutedEventArgs e)
    {
        await ToggleConnectionProcessAsync(ConnectionProcessKind.ThirdPartyFrpc);
    }

    private async void OnConnectionFrpEditTomlClick(object? sender, RoutedEventArgs e)
    {
        await EditConnectionTomlAsync(ConnectionProcessKind.Frp);
    }

    private async void OnConnectionThirdPartyFrpcEditTomlClick(object? sender, RoutedEventArgs e)
    {
        await EditConnectionTomlAsync(ConnectionProcessKind.ThirdPartyFrpc);
    }

    private async void OnAutomationProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingAutomation)
        {
            return;
        }

        var selected = AutomationProfileComboBox.SelectedItem as InstanceProfile;
        if (selected is null)
        {
            return;
        }

        try
        {
            var settings = await _automationSettingsService.LoadAsync();
            if (string.IsNullOrWhiteSpace(settings.TargetProfileId))
            {
                settings.TargetProfileId = selected.Id;
                await _automationSettingsService.SaveAsync(settings);
            }
        }
        catch
        {
            // ignore
        }
    }

    private async void OnAutomationRefreshClick(object? sender, RoutedEventArgs e)
    {
        await RefreshAutomationAsync();
    }

    private async void OnAutomationSaveClick(object? sender, RoutedEventArgs e)
    {
        await SaveAutomationAsync();
    }

    private async void OnAutomationEditConfigClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProfileConfigListItem item })
        {
            return;
        }

        var profile = _profileService.GetProfileById(item.ProfileId);
        if (profile is not null)
        {
            await ShowAutomationEditorAsync(profile);
        }
    }

    private void OnAutomationBackClick(object? sender, RoutedEventArgs e)
    {
        ShowAutomationList();
    }

    private async void OnAutomationClearClick(object? sender, RoutedEventArgs e)
    {
        var selected = _automationConfigItems.Where(static item => item.IsSelected).ToList();
        if (selected.Count == 0)
        {
            SetAutomationStatus(T("请先选择自动化配置。", "Select automation configurations first."));
            return;
        }

        foreach (var item in selected)
        {
            var profile = _profileService.GetProfileById(item.ProfileId);
            if (profile is null)
            {
                continue;
            }

            await _automationSettingsService.SaveAsync(profile, BuildClearedAutomationSettings(profile.Id));
        }

        if (selected.Count > 0)
        {
            await _automationService.ReloadAsync();
            RefreshAutomationConfigItems();
            SetAutomationStatus(T($"已清空 {selected.Count} 个自动化配置。", $"Cleared {selected.Count} automation configs."));
        }
    }

    private void OnAutomationAddActionClick(object? sender, RoutedEventArgs e)
    {
        _automationActionWindowItems.Add(new AutomationActionWindowItem(_isChinese));
    }

    private void OnAutomationRemoveActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AutomationActionWindowItem item })
        {
            _automationActionWindowItems.Remove(item);
        }
    }

    private void OnAutomationAddScriptClick(object? sender, RoutedEventArgs e)
    {
        _automationScriptItems.Add(new AutomationScriptItem(_isChinese));
    }

    private void OnAutomationRemoveScriptClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AutomationScriptItem item })
        {
            _automationScriptItems.Remove(item);
        }
    }

    private async void OnAutomationBrowseScriptClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AutomationScriptItem item })
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("选择自动化脚本", "Select automation script"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(T("批处理脚本", "Batch scripts"))
                {
                    Patterns = ["*.bat", "*.cmd"]
                }
            ]
        });

        var path = TryGetLocalPath(files.FirstOrDefault());
        if (!string.IsNullOrWhiteSpace(path))
        {
            item.ScriptPath = path;
        }
    }

    private void OnAutomationAddBackupScheduleClick(object? sender, RoutedEventArgs e)
    {
        _automationBackupScheduleItems.Add(new AutomationBackupScheduleItem(_isChinese));
    }

    private void OnAutomationRemoveBackupScheduleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AutomationBackupScheduleItem item })
        {
            _automationBackupScheduleItems.Remove(item);
        }
    }

    private void OnAutomationPreviewBackupScheduleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is AutomationBackupScheduleItem item)
        {
            item.RefreshPreview();
            if (button.Flyout is Flyout flyout && flyout.Content is Control content)
            {
                content.DataContext = item;
            }
        }
    }

    private void OnAutomationAddBroadcastClick(object? sender, RoutedEventArgs e)
    {
        _automationBroadcastItems.Add(new ScheduledBroadcastItem());
    }

    private void OnAutomationRemoveBroadcastClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ScheduledBroadcastItem item })
        {
            _automationBroadcastItems.Remove(item);
        }
    }

    private void OnAutomationAddCommandClick(object? sender, RoutedEventArgs e)
    {
        _automationCommandItems.Add(new ScheduledCommandItem());
    }

    private void OnAutomationRemoveCommandClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ScheduledCommandItem item })
        {
            _automationCommandItems.Remove(item);
        }
    }

    private void OnAutomationAddExportTimeClick(object? sender, RoutedEventArgs e)
    {
        _automationExportTimeItems.Add(new AutomationTimeItem("12:00"));
    }

    private void OnAutomationRemoveExportTimeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AutomationTimeItem item })
        {
            _automationExportTimeItems.Remove(item);
        }
    }

    private async void OnModProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingMods)
        {
            return;
        }

        await LoadModsForSelectedProfileAsync();
    }

    private void OnModsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateModSelectAllState();
    }

    private void OnModSelectAllClick(object? sender, RoutedEventArgs e)
    {
        if (_isUpdatingModSelectAll)
            return;

        var selectedCount = ModsListBox.SelectedItems?.Count ?? 0;
        var shouldSelectAll = _modItems.Count > 0 && selectedCount < _modItems.Count;
        if (shouldSelectAll)
            ModsListBox.SelectAll();
        else
            ModsListBox.SelectedItems?.Clear();

        UpdateModSelectAllState();
    }

    private void UpdateModSelectAllState()
    {
        if (ModSelectAllCheckBox is null || ModsListBox is null)
            return;

        var total = _modItems.Count;
        var selected = ModsListBox.SelectedItems?.Count ?? 0;
        bool? state = total > 0 && selected == total
            ? true
            : selected > 0
                ? null
                : false;

        _isUpdatingModSelectAll = true;
        try
        {
            ModSelectAllCheckBox.IsChecked = state;
        }
        finally
        {
            _isUpdatingModSelectAll = false;
        }
    }

    private async void OnBrowseModZipClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("选择 Mod ZIP 文件（可多选）", "Select Mod ZIP files"),
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("ZIP")
                {
                    Patterns = ["*.zip"]
                }
            ]
        });

        var paths = files
            .Select(TryGetLocalPath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToList();
        if (paths.Count > 0)
            SetModImportPaths(paths);
    }

    private async void OnImportModZipClick(object? sender, RoutedEventArgs e)
    {
        if (ModProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            SetModStatus(T("请先选择档案。", "Select a profile first."));
            return;
        }

        var paths = GetModImportPaths();
        if (paths.Count == 0)
        {
            SetModStatus(T("请选择 Mod ZIP 文件。", "Select Mod ZIP files."));
            return;
        }

        try
        {
            var imported = await _instanceModService.ImportModsAsync(profile, paths);
            await LoadModsForSelectedProfileAsync();
            if (imported.Count == 0)
            {
                SetModStatus(T("未检测到包含 modinfo.json 的模组。", "No mods with modinfo.json were found."));
                return;
            }

            SetModStatus(T($"已导入 {imported.Count} 个模组：{string.Join(", ", imported.Select(static mod => mod.ModId))}",
                $"Imported {imported.Count} mods: {string.Join(", ", imported.Select(static mod => mod.ModId))}"));
        }
        catch (Exception ex)
        {
            SetModStatus(T($"导入失败：{ex.Message}", $"Import failed: {ex.Message}"));
        }
    }

    private void SetModImportPaths(IEnumerable<string> paths)
    {
        _modImportPaths.Clear();
        _modImportPaths.AddRange(paths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase));
        ModZipPathTextBox.Text = string.Join(" ", _modImportPaths.Select(ModImportPathParser.Quote));
    }

    private IReadOnlyList<string> GetModImportPaths()
    {
        var raw = ModZipPathTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return ModImportPathParser.Parse(raw);
    }

    private async void OnDeleteSelectedModsClick(object? sender, RoutedEventArgs e)
    {
        if (ModProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            SetModStatus(T("请先选择档案。", "Select a profile first."));
            return;
        }

        var selected = ModsListBox.SelectedItems?
            .OfType<ModListItem>()
            .Select(ModListItem.ToModel)
            .ToList() ?? [];
        if (selected.Count == 0)
        {
            SetModStatus(T("请先选择模组。", "Select mods first."));
            return;
        }

        try
        {
            var deleted = await _instanceModService.DeleteModsAsync(profile, selected);
            await LoadModsForSelectedProfileAsync();
            SetModStatus(T($"已删除 {deleted} 个模组。", $"Deleted {deleted} mods."));
        }
        catch (Exception ex)
        {
            SetModStatus(T($"删除失败：{ex.Message}", $"Delete failed: {ex.Message}"));
        }
    }

    private async void OnRefreshModsClick(object? sender, RoutedEventArgs e)
    {
        await RefreshModsAsync();
    }

    private async void OnCheckModUpdatesClick(object? sender, RoutedEventArgs e)
    {
        await CheckModUpdatesAsync();
    }

    private async void OnExportModsClick(object? sender, RoutedEventArgs e)
    {
        if (ModProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            SetModStatus(T("请先选择档案。", "Select a profile first."));
            return;
        }

        var dialogResult = await new ModListExportWindow(_isChinese).ShowDialog<ModListExportDialogResult?>(this);
        if (dialogResult is null)
            return;

        var extension = _modListExportService.GetFileExtension(dialogResult.Format);
        var selectedFile = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("导出模组清单", "Export mod list"),
            SuggestedFileName = $"mods-{SanitizeFileName(profile.Name)}-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}",
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType(extension.ToUpperInvariant()) { Patterns = [$"*.{extension}"] }
            ]
        });
        var path = TryGetLocalPath(selectedFile);
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var mods = _modItems.Select(ModListItem.ToModel).ToList();
            await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            await _modListExportService.ExportAsync(profile, mods, dialogResult.Format, output, options: dialogResult.Options);
            SetModStatus(T($"模组清单已导出：{path}", $"Mod list exported: {path}"));
        }
        catch (Exception ex)
        {
            SetModStatus(T($"导出模组清单失败：{ex.Message}", $"Failed to export mod list: {ex.Message}"));
        }
    }

    private async Task CheckModUpdatesAsync()
    {
        if (_isCheckingModUpdates)
            return;

        if (ModProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            SetModStatus(T("请先选择档案。", "Select a profile first."));
            return;
        }

        if (_modItems.Count == 0)
        {
            SetModStatus(T("当前档案没有可检查的模组。", "The selected profile has no mods to check."));
            return;
        }

        _isCheckingModUpdates = true;
        CheckModUpdatesButton.IsEnabled = false;
        RefreshModsButton.IsEnabled = false;
        ModProfileComboBox.IsEnabled = false;
        foreach (var item in _modItems)
            item.SetUpdateChecking(_isChinese);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var concurrency = new SemaphoreSlim(4);
            var tasks = _modItems.Select(async item =>
            {
                await concurrency.WaitAsync(cts.Token);
                try
                {
                    var result = await _modUpdateService.CheckAsync(ModListItem.ToModel(item), cts.Token);
                    return (Item: item, Result: result, Error: string.Empty);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return (Item: item, Result: (ModUpdateCheckResult?)null, Error: GetExceptionMessage(ex));
                }
                finally
                {
                    concurrency.Release();
                }
            });
            var checkedItems = await Task.WhenAll(tasks);

            var updateCount = 0;
            var latestCount = 0;
            var failedCount = 0;
            foreach (var checkedItem in checkedItems)
            {
                if (checkedItem.Result is null)
                {
                    checkedItem.Item.SetUpdateCheckFailed(_isChinese);
                    failedCount++;
                    continue;
                }

                checkedItem.Item.SetUpdateResult(checkedItem.Result, _isChinese);
                if (checkedItem.Result.IsUpdateAvailable)
                    updateCount++;
                else
                    latestCount++;
            }

            PersistModUpdateChecks(
                profile,
                checkedItems.Select(static checkedItem => (checkedItem.Item, checkedItem.Result)),
                DateTimeOffset.UtcNow);

            SetModStatus(T(
                $"更新检查完成：{updateCount} 个可更新，{latestCount} 个已是最新，{failedCount} 个检查失败。",
                $"Update check complete: {updateCount} available, {latestCount} up to date, {failedCount} failed."));
        }
        catch (OperationCanceledException)
        {
            foreach (var item in _modItems)
                item.SetUpdateCheckFailed(_isChinese);
            PersistModUpdateChecks(
                profile,
                _modItems.Select(static item => (item, (ModUpdateCheckResult?)null)),
                DateTimeOffset.UtcNow);
            SetModStatus(T("模组更新检查超时。", "Mod update check timed out."));
        }
        finally
        {
            _isCheckingModUpdates = false;
            CheckModUpdatesButton.IsEnabled = true;
            RefreshModsButton.IsEnabled = true;
            ModProfileComboBox.IsEnabled = true;
        }
    }

    private async void OnModUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ModListItem item } ||
            item.UpdateInfo is not ModUpdateCheckResult update ||
            ModProfileComboBox.SelectedItem is not InstanceProfile profile)
            return;

        var window = new ModUpdateWindow(
            ModListItem.ToModel(item),
            update,
            _isChinese,
            async cancellationToken =>
            {
                await _instanceModService.UpdateModAsync(
                    profile,
                    ModListItem.ToModel(item),
                    update.DownloadUrl,
                    cancellationToken);
            });
        var updated = await window.ShowDialog<bool?>(this);
        if (updated == true)
        {
            await LoadModsForSelectedProfileAsync();
            SetModStatus(T("模组已更新。", "Mod updated."));
        }
    }

    private void OnOpenModConfigPathClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path))
        {
            SetModStatus(T("配置路径无效。", "Invalid config path."));
            return;
        }

        try
        {
            var primaryPath = path.Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(primaryPath) || (!File.Exists(primaryPath) && !Directory.Exists(primaryPath)))
            {
                SetModStatus(T($"配置路径不存在：{path}", $"Config path not found: {path}"));
                return;
            }

            OpenLocalFile(primaryPath);
        }
        catch (Exception ex)
        {
            SetModStatus(T($"打开配置路径失败：{ex.Message}", $"Failed to open config path: {ex.Message}"));
        }
    }

    private void OnOpenConfigFileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path))
        {
            ShowToast(T("配置路径无效。", "Invalid config path."));
            return;
        }

        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                OpenLocalFile(path);
                return;
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                OpenLocalFile(directory);
                return;
            }

            ShowToast(T($"配置路径不存在：{path}", $"Config path not found: {path}"));
        }
        catch (Exception ex)
        {
            ShowToast(T($"打开配置失败：{ex.Message}", $"Open config failed: {ex.Message}"));
        }
    }

    private async void OnModEnabledSwitchClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { Tag: ModListItem item } toggleSwitch ||
            ModProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            return;
        }

        try
        {
            await _instanceModService.SetModEnabledAsync(profile, item.ModId, item.Version, toggleSwitch.IsChecked == true);
            await LoadModsForSelectedProfileAsync();
        }
        catch (Exception ex)
        {
            await LoadModsForSelectedProfileAsync();
            SetModStatus(T($"切换失败：{ex.Message}", $"Toggle failed: {ex.Message}"));
        }
    }

    private async void OnAuthProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingAuth)
        {
            return;
        }

        if (AuthEditorPanel.IsVisible && AuthProfileComboBox.SelectedItem is InstanceProfile profile)
        {
            await LoadAuthForProfileAsync(profile);
        }
    }

    private void OnAuthDiscourseModeClick(object? sender, RoutedEventArgs e)
    {
        if (AuthDiscourseEnabledCheckBox.IsChecked == true)
            AuthOAuth2EnabledCheckBox.IsChecked = false;
    }

    private void OnAuthOAuth2ModeClick(object? sender, RoutedEventArgs e)
    {
        if (AuthOAuth2EnabledCheckBox.IsChecked == true)
            AuthDiscourseEnabledCheckBox.IsChecked = false;
    }

    private async void OnAuthSaveClick(object? sender, RoutedEventArgs e)
    {
        if (AuthProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            SetAuthStatus(T("请先选择档案。", "Select a profile first."));
            return;
        }

        try
        {
            var settings = CollectAuthSettings();
            await _serverAuthService.SaveSettingsAsync(profile, settings);
            if (settings.Enabled)
            {
                await _serverAuthService.EnsureAuthModDeployedAsync(profile, enableMod: true);
            }
            else
            {
                await _serverAuthService.SetAuthModEnabledAsync(profile, enabled: false);
            }

            await LoadAuthForProfileAsync(profile);
            SetAuthStatus(T("认证配置已保存。", "Auth settings saved."));
        }
        catch (Exception ex)
        {
            SetAuthStatus(T($"保存失败：{ex.Message}", $"Save failed: {ex.Message}"));
        }
    }

    private async void OnAuthRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (AuthEditorPanel.IsVisible && AuthProfileComboBox.SelectedItem is InstanceProfile profile)
        {
            await LoadAuthForProfileAsync(profile);
            return;
        }

        await RefreshAuthProfilesAsync();
    }

    private async void OnAuthEditConfigClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProfileConfigListItem item })
        {
            return;
        }

        var profile = _profileService.GetProfileById(item.ProfileId);
        if (profile is not null)
        {
            await ShowAuthEditorAsync(profile);
        }
    }

    private void OnAuthBackClick(object? sender, RoutedEventArgs e)
    {
        ShowAuthList();
    }

    private async void OnAuthClearClick(object? sender, RoutedEventArgs e)
    {
        var selected = _authConfigItems.Where(static item => item.IsSelected).ToList();
        if (selected.Count == 0)
        {
            SetAuthStatus(T("请先选择认证配置。", "Select authentication configurations first."));
            return;
        }

        foreach (var item in selected)
        {
            var profile = _profileService.GetProfileById(item.ProfileId);
            if (profile is null)
            {
                continue;
            }

            await _serverAuthService.SaveSettingsAsync(profile, BuildClearedAuthSettings());
            await _serverAuthService.SetAuthModEnabledAsync(profile, enabled: false);
        }

        if (selected.Count > 0)
        {
            RefreshAuthConfigItems();
            SetAuthStatus(T($"已清空 {selected.Count} 个安全配置。", $"Cleared {selected.Count} security configs."));
        }
    }

    private async void OnAuthDeployClick(object? sender, RoutedEventArgs e)
    {
        if (AuthProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            SetAuthStatus(T("请先选择档案。", "Select a profile first."));
            return;
        }

        try
        {
            var settings = CollectAuthSettings();
            await _serverAuthService.EnsureAuthModDeployedAsync(profile, enableMod: settings.Enabled);
            await LoadAuthForProfileAsync(profile);
            SetAuthStatus(settings.Enabled
                ? T("认证模组已部署并启用。", "Auth mod deployed and enabled.")
                : T("认证模组已部署，但认证未启用，模组保持禁用。", "Auth mod deployed, but auth is disabled so the mod remains disabled."));
        }
        catch (Exception ex)
        {
            SetAuthStatus(T($"部署失败：{ex.Message}", $"Deploy failed: {ex.Message}"));
        }
    }

    private async void OnAuthRefreshPlayersClick(object? sender, RoutedEventArgs e)
    {
        if (AuthProfileComboBox.SelectedItem is InstanceProfile profile)
        {
            await LoadAuthPlayersAsync(profile);
        }
    }

    private async void OnServerBridgeProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingServerBridge)
            return;

        if (ServerBridgeEditorPanel.IsVisible &&
            ServerBridgeProfileComboBox.SelectedItem is InstanceProfile profile)
        {
            await LoadServerBridgeForProfileAsync(profile);
        }
    }

    private async void OnServerBridgeSaveClick(object? sender, RoutedEventArgs e)
    {
        if (ServerBridgeProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            SetServerBridgeStatus(T("请先选择档案。", "Select a profile first."));
            return;
        }

        try
        {
            var settings = CollectServerBridgeSettings();
            await _serverBridgeService.SaveSettingsAsync(profile, settings);
            if (settings.Enabled)
            {
                await _serverBridgeService.EnsureServerBridgeModDeployedAsync(profile, enableMod: true);
            }
            else
            {
                await _serverBridgeService.SetServerBridgeModEnabledAsync(profile, enabled: false);
            }

            await LoadServerBridgeForProfileAsync(profile);
            SetServerBridgeStatus(T(
                "服务器桥接配置已保存。配置和模组将在服务端下次启动时加载。",
                "Server Bridge settings saved. The configuration and mod load on the next server start."));
        }
        catch (Exception ex)
        {
            SetServerBridgeStatus(T($"保存失败：{ex.Message}", $"Save failed: {ex.Message}"));
        }
    }

    private async void OnServerBridgeRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (ServerBridgeEditorPanel.IsVisible &&
            ServerBridgeProfileComboBox.SelectedItem is InstanceProfile profile)
        {
            await LoadServerBridgeForProfileAsync(profile);
            return;
        }

        await RefreshServerBridgeProfilesAsync();
    }

    private async void OnServerBridgeEditConfigClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProfileConfigListItem item })
            return;

        var profile = _profileService.GetProfileById(item.ProfileId);
        if (profile is not null)
            await ShowServerBridgeEditorAsync(profile);
    }

    private void OnServerBridgeBackClick(object? sender, RoutedEventArgs e)
    {
        ShowServerBridgeList();
    }

    private async void OnServerBridgeClearClick(object? sender, RoutedEventArgs e)
    {
        var selected = _serverBridgeConfigItems.Where(static item => item.IsSelected).ToList();
        if (selected.Count == 0)
        {
            SetServerBridgeStatus(T("请先选择服务器桥接配置。", "Select server bridge configurations first."));
            return;
        }

        foreach (var item in selected)
        {
            var profile = _profileService.GetProfileById(item.ProfileId);
            if (profile is null)
                continue;

            await _serverBridgeService.SaveSettingsAsync(profile, BuildClearedServerBridgeSettings());
            await _serverBridgeService.SetServerBridgeModEnabledAsync(profile, enabled: false);
        }

        RefreshServerBridgeConfigItems();
        SetServerBridgeStatus(T($"已清空 {selected.Count} 个服务器桥接配置。", $"Cleared {selected.Count} server bridge configurations."));
    }

    private async void OnServerBridgeDeployClick(object? sender, RoutedEventArgs e)
    {
        if (ServerBridgeProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            SetServerBridgeStatus(T("请先选择档案。", "Select a profile first."));
            return;
        }

        try
        {
            var settings = CollectServerBridgeSettings();
            await _serverBridgeService.SaveSettingsAsync(profile, settings);
            await _serverBridgeService.EnsureServerBridgeModDeployedAsync(profile, enableMod: settings.Enabled);
            await LoadServerBridgeForProfileAsync(profile);
            SetServerBridgeStatus(settings.Enabled
                ? T("服务器桥接模组已部署并启用；将在服务端下次启动时监听。", "Server Bridge mod deployed and enabled; it listens on the next server start.")
                : T("服务器桥接模组已部署，但当前配置未启用。", "Server Bridge mod deployed, but the current configuration is disabled."));
        }
        catch (Exception ex)
        {
            SetServerBridgeStatus(T($"部署失败：{ex.Message}", $"Deployment failed: {ex.Message}"));
        }
    }

    private async void OnServerBridgeTestClick(object? sender, RoutedEventArgs e)
    {
        if (ServerBridgeProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            SetServerBridgeStatus(T("请先选择档案。", "Select a profile first."));
            return;
        }

        var status = await _serverBridgeService.GetRuntimeStatusAsync(profile);
        SetServerBridgeStatus(T(
            $"服务器桥接状态：{status.Message}（127.0.0.1:{status.Port}）",
            $"Server Bridge status: {status.Message} (127.0.0.1:{status.Port})"));
    }

    private async void OnServerBridgeRegenerateTokenClick(object? sender, RoutedEventArgs e)
    {
        if (ServerBridgeProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            SetServerBridgeStatus(T("请先选择档案。", "Select a profile first."));
            return;
        }

        try
        {
            ServerBridgeRegenerateTokenButton.IsEnabled = false;
            await _serverBridgeService.RotateAccessTokenAsync(profile);
            await LoadServerBridgeForProfileAsync(profile);
            SetServerBridgeStatus(T(
                "访问令牌已热轮换并自动保存，无需点击保存或重启服务端。",
                "Access token rotated live and saved automatically; no Save click or server restart is required."));
        }
        catch (Exception ex)
        {
            SetServerBridgeStatus(T(
                $"令牌热轮换失败：{ex.Message}",
                $"Live access-token rotation failed: {ex.Message}"));
        }
        finally
        {
            ServerBridgeRegenerateTokenButton.IsEnabled = true;
        }
    }

    private async void OnEditModConfigClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ModListItem item } || !item.CanEditConfig)
        {
            return;
        }

        try
        {
            var editor = new ModConfigEditorWindow(item.ConfigPath, _isChinese);
            await editor.ShowDialog(this);
        }
        catch (Exception ex)
        {
            SetModStatus(T($"打开配置编辑器失败：{ex.Message}", $"Failed to open configuration editor: {ex.Message}"));
        }
    }

    private void OnAuthPlayerSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        ApplyAuthPlayerSearch();
    }

    private async void OnAuthClearPlayerPasswordClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AuthPlayerListItem item } || AuthProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            return;
        }

        try
        {
            var changed = await _serverAuthService.ClearPasswordAsync(profile, item.PlayerUid);
            await LoadAuthPlayersAsync(profile);
            SetAuthStatus(changed
                ? T($"已清空 {item.PlayerName} 的密码。", $"Cleared password for {item.PlayerName}.")
                : T($"未找到玩家：{item.PlayerName}", $"Player not found: {item.PlayerName}"));
        }
        catch (Exception ex)
        {
            SetAuthStatus(T($"清空失败：{ex.Message}", $"Clear failed: {ex.Message}"));
        }
    }

    private void OnConnectionThirdPartyFrpcModeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingConnectionSettings || _isApplyingLocalizedOptions)
        {
            return;
        }

        var mode = GetSelectedThirdPartyFrpcMode();
        var defaultCommand = mode == ThirdPartyFrpcLaunchMode.CommandOnly
            ? FrpIntegrationSettings.DefaultThirdPartyFrpcCommand
            : FrpIntegrationSettings.DefaultThirdPartyFrpcConfigCommand;
        var current = ConnectionThirdPartyFrpcCommandTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(current) ||
            current.Equals(FrpIntegrationSettings.DefaultThirdPartyFrpcCommand, StringComparison.OrdinalIgnoreCase) ||
            current.Equals(FrpIntegrationSettings.DefaultThirdPartyFrpcConfigCommand, StringComparison.OrdinalIgnoreCase))
        {
            ConnectionThirdPartyFrpcCommandTextBox.Text = defaultCommand;
        }

        SaveFrpSettings(updateStatus: false, refreshEditor: false);
    }

    private async void OnRobotSaveClick(object? sender, RoutedEventArgs e) => await SaveRobotSettingsAndReloadIfRunningAsync();

    private void OnRobotRefreshClick(object? sender, RoutedEventArgs e)
    {
        RefreshConnectionSettingsEditor();
        RefreshConnectionRuntimeStatus();
    }

    private void OnServerMapSubTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.InstanceManage);
        SelectInstanceManageTab(InstanceManageTab.ServerMap);
    }

    private void ShowServerMapList()
    {
        _editingServerMapProfileId = string.Empty;
        ServerMapListPanel.IsVisible = true;
        ServerMapEditorPanel.IsVisible = false;
        ServerMapClearButton.IsVisible = true;
        ServerMapBackButton.IsVisible = false;
        ServerMapSaveButton.IsVisible = false;
        ServerMapDeployButton.IsVisible = false;
        ServerMapToggleButton.IsVisible = false;
        Grid.SetColumn(ServerMapRefreshButton, 1);
        RefreshServerMapConfigItems();
    }

    private async Task ShowServerMapEditorAsync(InstanceProfile profile)
    {
        _editingServerMapProfileId = profile.Id;
        ServerMapListPanel.IsVisible = false;
        ServerMapEditorPanel.IsVisible = true;
        ServerMapClearButton.IsVisible = false;
        ServerMapBackButton.IsVisible = true;
        ServerMapSaveButton.IsVisible = true;
        ServerMapDeployButton.IsVisible = true;
        ServerMapToggleButton.IsVisible = true;
        Grid.SetColumn(ServerMapRefreshButton, 3);
        ServerMapProfileComboBox.SelectedItem = _serverMapProfileItems.FirstOrDefault(p => p.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase)) ?? profile;
        await LoadServerMapForProfileAsync(profile);
    }

    private async Task RefreshServerMapProfilesAsync()
    {
        if (_isRefreshingServerMap) return;
        _isRefreshingServerMap = true;
        try
        {
            var selectedId = _editingServerMapProfileId;
            _serverMapProfileItems.Clear();
            foreach (var profile in _profileService.GetProfiles()) _serverMapProfileItems.Add(profile);
            ServerMapProfileComboBox.ItemsSource = _serverMapProfileItems;
            RefreshServerMapConfigItems(_serverMapProfileItems);
            var target = _serverMapProfileItems.FirstOrDefault(p => p.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase)) ?? _serverMapProfileItems.FirstOrDefault();
            ServerMapProfileComboBox.SelectedItem = target;
            if (target is not null && ServerMapEditorPanel.IsVisible) await LoadServerMapForProfileAsync(target);
        }
        finally { _isRefreshingServerMap = false; }
    }

    private async Task LoadServerMapForProfileAsync(InstanceProfile profile)
    {
        var settings = await _serverMapService.LoadSettingsAsync(profile);
        ServerMapEnabledCheckBox.IsChecked = settings.Enabled;
        ServerMapHttpsCheckBox.IsChecked = settings.UseHttps;
        ServerMapListenPortNumericUpDown.Value = settings.ListenPort;
        ServerMapCertificateTextBox.Text = settings.CertificatePath;
        ServerMapPrivateKeyTextBox.Text = settings.PrivateKeyPath;
        var status = _serverMapService.GetStatus(profile);
        ServerMapStatusTextBlock.Text = status.IsRunning ? $"运行中：{status.Url}" : "未启动";
        ServerMapToggleButton.Content = status.IsRunning ? "停止地图" : "启动地图";
    }

    private async void OnServerMapSaveClick(object? sender, RoutedEventArgs e)
    {
        if (ServerMapProfileComboBox.SelectedItem is not InstanceProfile profile) { SetServerMapStatus("请先选择档案。"); return; }
        var current = await _serverMapService.LoadSettingsAsync(profile);
        var settings = new ServerMapSettings
        {
            Enabled = ServerMapEnabledCheckBox.IsChecked == true,
            UseHttps = ServerMapHttpsCheckBox.IsChecked == true,
            ListenPort = (int?)ServerMapListenPortNumericUpDown.Value ?? current.ListenPort,
            CertificatePath = ServerMapCertificateTextBox.Text ?? string.Empty,
            PrivateKeyPath = ServerMapPrivateKeyTextBox.Text ?? string.Empty,
            ListenAddress = current.ListenAddress,
            BackendPort = current.BackendPort,
            BackendToken = current.BackendToken,
            WebRoot = current.WebRoot,
            PublicUrl = current.PublicUrl
        };
        await _serverMapService.SaveSettingsAsync(profile, settings);
        await LoadServerMapForProfileAsync(profile);
        SetServerMapStatus("服务器地图配置已保存。");
    }

    private async void OnServerMapDeployClick(object? sender, RoutedEventArgs e)
    {
        if (ServerMapProfileComboBox.SelectedItem is not InstanceProfile profile) { SetServerMapStatus("请先选择档案。"); return; }
        try { await _serverMapService.EnsureMapModDeployedAsync(profile); SetServerMapStatus("服务器地图模组已部署。"); }
        catch (Exception ex) { SetServerMapStatus($"部署失败：{ex.Message}"); }
    }

    private async void OnServerMapToggleClick(object? sender, RoutedEventArgs e)
    {
        if (ServerMapProfileComboBox.SelectedItem is not InstanceProfile profile) { SetServerMapStatus("请先选择档案。"); return; }
        try
        {
            if (_serverMapService.GetStatus(profile).IsRunning) await _serverMapService.StopAsync(profile);
            else await _serverMapService.StartAsync(profile);
            await LoadServerMapForProfileAsync(profile);
        }
        catch (Exception ex) { SetServerMapStatus($"操作失败：{ex.Message}"); }
    }

    private void OnServerMapOpenClick(object? sender, RoutedEventArgs e)
    {
        if (ServerMapProfileComboBox.SelectedItem is not InstanceProfile profile) return;
        var url = _serverMapService.GetStatus(profile).Url;
        if (!string.IsNullOrWhiteSpace(url)) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async void OnServerMapProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_isRefreshingServerMap && ServerMapEditorPanel.IsVisible && ServerMapProfileComboBox.SelectedItem is InstanceProfile profile)
            await LoadServerMapForProfileAsync(profile);
    }

    private async void OnServerMapRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (ServerMapEditorPanel.IsVisible && ServerMapProfileComboBox.SelectedItem is InstanceProfile profile) await LoadServerMapForProfileAsync(profile);
        else await RefreshServerMapProfilesAsync();
    }

    private async void OnServerMapEditConfigClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProfileConfigListItem item } && _profileService.GetProfileById(item.ProfileId) is { } profile)
            await ShowServerMapEditorAsync(profile);
    }

    private void OnServerMapBackClick(object? sender, RoutedEventArgs e) => ShowServerMapList();

    private async void OnServerMapClearClick(object? sender, RoutedEventArgs e)
    {
        foreach (var item in _serverMapConfigItems.Where(i => i.IsSelected))
            if (_profileService.GetProfileById(item.ProfileId) is { } profile)
                await _serverMapService.SaveSettingsAsync(profile, new ServerMapSettings { Enabled = false });
        RefreshServerMapConfigItems();
    }

    private async void OnServerMapCertificateBrowseClick(object? sender, RoutedEventArgs e) => await BrowseServerMapFileAsync(ServerMapCertificateTextBox, "证书文件", ["*.crt", "*.pem"]);
    private async void OnServerMapPrivateKeyBrowseClick(object? sender, RoutedEventArgs e) => await BrowseServerMapFileAsync(ServerMapPrivateKeyTextBox, "私钥文件", ["*.key", "*.pem"]);

    private async Task BrowseServerMapFileAsync(TextBox target, string title, IReadOnlyList<string> patterns)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = [new FilePickerFileType(title) { Patterns = patterns.ToArray() }] });
        var path = TryGetLocalPath(files.FirstOrDefault());
        if (!string.IsNullOrWhiteSpace(path)) target.Text = path;
    }

    private void SetServerMapStatus(string message) => ServerMapStatusTextBlock.Text = message;

    private void RefreshServerMapConfigItems(IReadOnlyList<InstanceProfile>? profiles = null)
    {
        _serverMapConfigItems.Clear();
        foreach (var profile in profiles ?? _profileService.GetProfiles())
            _serverMapConfigItems.Add(ProfileConfigListItem.FromPath(profile, Path.Combine(_serverMapService.GetProfileDirectory(profile), "launchergo-map.json")));
        ServerMapConfigItemsControl.ItemsSource = _serverMapConfigItems;
    }

    private async void OnDiscordSaveClick(object? sender, RoutedEventArgs e)
    {
        if (!TryCollectDiscordSettings(out var discord, out var validationMessage))
        {
            ShowDiscordValidation(validationMessage);
            return;
        }
        var preferences = _preferencesService.Load();
        preferences.Discord = discord;
        _preferencesService.Save(preferences);
        await _discordBotService.SaveSettingsAsync(preferences.Discord);
        if (_discordBotService.GetCurrentStatus().IsRunning)
        {
            try
            {
                await _discordBotService.StopAsync(TimeSpan.FromSeconds(5));
                await _discordBotService.StartAsync(preferences.Discord);
            }
            catch (Exception ex)
            {
                ShowToast(T($"Discord 配置已保存，但重载失败：{ex.Message}", $"Discord configuration saved, but reload failed: {ex.Message}"));
                return;
            }
        }
        ShowToast(T("Discord 配置已保存。", "Discord configuration saved."));
    }

    private async void OnDiscordToggleClick(object? sender, RoutedEventArgs e)
    {
        if (_isTogglingDiscord) return;
        _isTogglingDiscord = true;
        DiscordToggleButton.IsEnabled = false;
        try
        {
            if (_discordBotService.GetCurrentStatus().IsRunning)
            {
                ShowToast(T("正在停止 Discord 机器人…", "Stopping Discord bot…"));
                await _discordBotService.StopAsync(TimeSpan.FromSeconds(5));
                ShowToast(T("Discord 机器人已停止。", "Discord bot stopped."));
            }
            else
            {
                if (!TryCollectDiscordSettings(out var discord, out var validationMessage))
                {
                    ShowDiscordValidation(validationMessage);
                    return;
                }
                ShowToast(T("正在连接 Discord…", "Connecting to Discord…"));
                await _discordBotService.StartAsync(discord);
                ShowToast(T("Discord 机器人已连接。", "Discord bot connected."));
            }
            RefreshConnectionRuntimeStatus();
        }
        catch (Exception ex)
        {
            var message = _discordBotService.GetCurrentStatus().LastError;
            if (string.IsNullOrWhiteSpace(message)) message = GetExceptionMessage(ex);
            ShowToast(T($"Discord 连接失败：{message}", $"Discord connection failed: {message}"));
        }
        finally
        {
            _isTogglingDiscord = false;
            DiscordToggleButton.IsEnabled = true;
            DiscordToggleButton.Content = _discordBotService.GetCurrentStatus().IsRunning ? T("停止", "Stop") : T("启动", "Start");
        }
    }

    private async void OnDiscordRedeployClick(object? sender, RoutedEventArgs e)
    {
        DiscordRedeployButton.IsEnabled = false;
        try
        {
            if (!_discordBotService.GetCurrentStatus().IsConnected)
            {
                ShowToast(T("Discord 机器人尚未连接，请先启动。", "The Discord bot is not connected. Start it first."));
                return;
            }

            await _discordBotService.RedeployCommandsAsync();
            ShowToast(T(
                "Discord 命令已按绑定服务器语言重新部署。",
                "Discord commands were redeployed using each bound server language."));
        }
        catch (Exception ex)
        {
            ShowToast(T($"Discord 命令重新部署失败：{GetExceptionMessage(ex)}", $"Failed to redeploy Discord commands: {GetExceptionMessage(ex)}"));
        }
        finally
        {
            DiscordRedeployButton.IsEnabled = true;
        }
    }

    private async void OnDiscordClearClick(object? sender, RoutedEventArgs e)
    {
        var preferences = _preferencesService.Load();
        preferences.Discord = new DiscordIntegrationSettings();
        _preferencesService.Save(preferences);
        await _discordBotService.SaveSettingsAsync(preferences.Discord);
        if (_discordBotService.GetCurrentStatus().IsRunning)
            await _discordBotService.StopAsync(TimeSpan.FromSeconds(5));
        ApplyDiscordSettings(preferences.Discord);
        ShowToast(T("Discord 配置已清空。", "Discord configuration cleared."));
    }

    private void OnDiscordRefreshClick(object? sender, RoutedEventArgs e)
    {
        ApplyDiscordSettings(_preferencesService.Load().Discord);
        DiscordBindingValidationTextBlock.IsVisible = false;
        DiscordCustomCommandValidationTextBlock.IsVisible = false;
        RefreshConnectionRuntimeStatus();
    }

    private void OnDiscordBindingAddClick(object? sender, RoutedEventArgs e)
    {
        var profiles = _profileService.GetProfiles();
        _discordBindingItems.Add(new DiscordProfileBindingItem(profiles, profiles.FirstOrDefault()?.Id ?? string.Empty, string.Empty, string.Empty));
    }

    private void OnDiscordBindingRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DiscordProfileBindingItem item }) _discordBindingItems.Remove(item);
        if (_discordBindingItems.Count == 0) OnDiscordBindingAddClick(sender, e);
    }

    private void OnDiscordCustomCommandAddClick(object? sender, RoutedEventArgs e)
    {
        _discordCustomCommandItems.Add(new DiscordCustomCommandItem(string.Empty, RobotCustomMessageType.Text, string.Empty, _isChinese));
    }

    private void OnDiscordCustomCommandRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DiscordCustomCommandItem item }) _discordCustomCommandItems.Remove(item);
        if (_discordCustomCommandItems.Count == 0) OnDiscordCustomCommandAddClick(sender, e);
    }

    private async void OnDiscordCustomCommandImagePathClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DiscordCustomCommandItem item }) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("选择 Discord 图片", "Select Discord image"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Image") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp"] }]
        });
        var path = TryGetLocalPath(files.FirstOrDefault());
        if (!string.IsNullOrWhiteSpace(path)) item.Content = path;
    }

    private void ApplyDiscordSettings(DiscordIntegrationSettings settings)
    {
        DiscordTokenTextBox.Text = settings.BotToken;
        SetNumericValue(DiscordReconnectNumericUpDown, settings.ReconnectIntervalSec);
        DiscordAdminUsersTextBox.Text = string.Join(Environment.NewLine, settings.AdminUserIds);
        DiscordAdminRolesTextBox.Text = string.Join(Environment.NewLine, settings.AdminRoleIds);
        RebuildDiscordBindingItems(settings);
        RebuildDiscordCustomCommandItems(settings);
    }

    private void RebuildDiscordBindingItems(DiscordIntegrationSettings settings)
    {
        _discordBindingItems.Clear();
        var profiles = _profileService.GetProfiles();
        foreach (var binding in settings.ProfileBindings ?? [])
        {
            _discordBindingItems.Add(new DiscordProfileBindingItem(profiles, binding.ProfileId, binding.GuildId, binding.ChannelId));
        }

        if (_discordBindingItems.Count == 0)
            _discordBindingItems.Add(new DiscordProfileBindingItem(profiles, profiles.FirstOrDefault()?.Id ?? string.Empty, string.Empty, string.Empty));
    }

    private void RebuildDiscordCustomCommandItems(DiscordIntegrationSettings settings)
    {
        _discordCustomCommandItems.Clear();
        foreach (var command in settings.CustomCommands ?? [])
        {
            _discordCustomCommandItems.Add(new DiscordCustomCommandItem(command.Command, command.MessageType, command.Content, _isChinese));
        }

        if (_discordCustomCommandItems.Count == 0)
            _discordCustomCommandItems.Add(new DiscordCustomCommandItem(string.Empty, RobotCustomMessageType.Text, string.Empty, _isChinese));
    }

    private bool TryCollectDiscordSettings(out DiscordIntegrationSettings settings, out string message)
    {
        settings = new DiscordIntegrationSettings();
        message = string.Empty;
        var token = DiscordTokenTextBox.Text?.Trim() ?? string.Empty;
        if (!DiscordIntegrationSettingsRules.IsValidBotToken(token))
        {
            message = T("Discord Bot Token 格式无效。", "Discord Bot Token format is invalid.");
            return false;
        }

        var adminUsers = ParseDiscordIds(DiscordAdminUsersTextBox.Text);
        var adminRoles = ParseDiscordIds(DiscordAdminRolesTextBox.Text);
        if (!TryValidateDiscordIds(adminUsers) || !TryValidateDiscordIds(adminRoles))
        {
            message = T("管理员用户 ID 和角色 ID 必须是正整数 Snowflake ID。", "Administrator user and role IDs must be positive Discord Snowflake IDs.");
            return false;
        }

        var bindings = new List<DiscordProfileBinding>();
        var seenBindings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _discordBindingItems)
        {
            var profileId = item.SelectedProfile?.Id ?? item.ProfileId.Trim();
            var guildId = item.GuildId.Trim();
            var channelId = item.ChannelId.Trim();
            if (string.IsNullOrWhiteSpace(profileId) && string.IsNullOrWhiteSpace(guildId) && string.IsNullOrWhiteSpace(channelId)) continue;
            if (string.IsNullOrWhiteSpace(profileId) || _profileService.GetProfileById(profileId) is null || !DiscordIntegrationSettingsRules.TryNormalizeSnowflakeId(guildId, out var normalizedGuild) || !DiscordIntegrationSettingsRules.TryNormalizeSnowflakeId(channelId, out var normalizedChannel))
            {
                message = T("绑定表中存在无效的 Profile、Guild ID 或 Channel ID。", "The binding table contains an invalid Profile, Guild ID, or Channel ID.");
                return false;
            }
            var key = $"{profileId}|{normalizedGuild}|{normalizedChannel}";
            if (!seenBindings.Add(key)) continue;
            bindings.Add(new DiscordProfileBinding { ProfileId = profileId, GuildId = normalizedGuild, ChannelId = normalizedChannel });
        }

        var customCommands = new List<RobotCustomCommand>();
        var seenCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _discordCustomCommandItems)
        {
            if (string.IsNullOrWhiteSpace(item.Command) && string.IsNullOrWhiteSpace(item.Content)) continue;
            if (!RobotCustomCommandRules.TryNormalize(new RobotCustomCommand { Command = item.Command, MessageType = item.MessageType, Content = item.Content }, out var normalized))
            {
                message = T($"自定义指令无效：{item.Command}。名称必须符合 Discord 规则，且内容不能为空。", $"Invalid custom command: {item.Command}. Use a valid Discord command name and non-empty content.");
                return false;
            }
            if (seenCommands.Add(normalized.Command)) customCommands.Add(normalized);
        }

        settings = DiscordIntegrationSettingsRules.Normalize(new DiscordIntegrationSettings
        {
            BotToken = token,
            ReconnectIntervalSec = (int)(DiscordReconnectNumericUpDown.Value ?? 5),
            AdminUserIds = adminUsers,
            AdminRoleIds = adminRoles,
            ProfileBindings = bindings,
            CustomCommands = customCommands
        });
        return true;
    }

    private void ShowDiscordValidation(string message)
    {
        var isCustom = message.Contains("自定义", StringComparison.OrdinalIgnoreCase) || message.Contains("custom", StringComparison.OrdinalIgnoreCase);
        DiscordBindingValidationTextBlock.Text = isCustom ? string.Empty : message;
        DiscordBindingValidationTextBlock.IsVisible = !isCustom;
        DiscordCustomCommandValidationTextBlock.Text = isCustom ? message : string.Empty;
        DiscordCustomCommandValidationTextBlock.IsVisible = !string.IsNullOrWhiteSpace(DiscordCustomCommandValidationTextBlock.Text);
        ShowToast(message);
    }

    private static List<string> ParseDiscordIds(string? value) => (value ?? string.Empty)
        .Split([',', ';', '，', '；', '\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries)
        .ToList();

    private static bool TryValidateDiscordIds(IEnumerable<string> values) => values.All(value => DiscordIntegrationSettingsRules.TryNormalizeSnowflakeId(value, out _));

    private void OnRobotClearClick(object? sender, RoutedEventArgs e)
    {
        var preferences = _preferencesService.Load();
        preferences.Robot = BuildClearedRobotSettings();
        _preferencesService.Save(preferences);
        ApplyRobotSettings(preferences.Robot);
        SetConnectionStatus(T("QQ机器人配置已清空。", "QQ robot configuration cleared."));
    }

    private void OnRobotBindingAddClick(object? sender, RoutedEventArgs e)
    {
        _robotBindingItems.Add(new RobotProfileBindingItem(
            _robotProfileItems,
            _robotProfileItems.FirstOrDefault()?.Id ?? string.Empty,
            string.Empty,
            string.Empty));
    }

    private void OnRobotBindingRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RobotProfileBindingItem item })
        {
            _robotBindingItems.Remove(item);
        }

        if (_robotBindingItems.Count == 0)
        {
            OnRobotBindingAddClick(sender, e);
        }
    }

    private void OnRobotCustomCommandAddClick(object? sender, RoutedEventArgs e)
    {
        _robotCustomCommandItems.Add(new RobotCustomCommandItem(
            string.Empty,
            RobotCustomMessageType.Text,
            string.Empty,
            _isChinese));
    }

    private void OnRobotTeleportPointAddClick(object? sender, RoutedEventArgs e)
    {
        _robotTeleportPointItems.Add(new RobotTeleportPointItem(string.Empty, 0, 0, 0));
    }

    private void OnRobotTeleportPointRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RobotTeleportPointItem item })
        {
            _robotTeleportPointItems.Remove(item);
        }

        if (_robotTeleportPointItems.Count == 0)
        {
            OnRobotTeleportPointAddClick(sender, e);
        }
    }

    private void OnRobotCustomCommandRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RobotCustomCommandItem item })
        {
            _robotCustomCommandItems.Remove(item);
        }

        if (_robotCustomCommandItems.Count == 0)
        {
            OnRobotCustomCommandAddClick(sender, e);
        }
    }

    private async void OnRobotCustomCommandImagePathClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RobotCustomCommandItem item })
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("选择机器人图片", "Select robot image"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Image")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp"]
                }
            ]
        });

        var path = TryGetLocalPath(files.FirstOrDefault());
        if (!string.IsNullOrWhiteSpace(path))
        {
            item.Content = path;
        }
    }

    private async void OnRobotToggleClick(object? sender, RoutedEventArgs e)
    {
        if (_isTogglingRobot)
            return;

        _isTogglingRobot = true;
        RobotToggleButton.IsEnabled = false;
        try
        {
            if (_robotService.GetCurrentStatus().IsRunning)
            {
                RobotToggleButton.Content = T("启动", "Start");
                await StopRobotAsync();
                return;
            }

            RobotToggleButton.Content = T("停止", "Stop");
            await StartRobotAsync();
        }
        finally
        {
            _isTogglingRobot = false;
            RobotToggleButton.IsEnabled = true;
            UpdateRobotToggleButtonText();
        }
    }

    private async Task StartRobotAsync()
    {
        if (!SaveRobotSettings(updateStatus: false))
        {
            return;
        }

        try
        {
            var preferences = _preferencesService.Load();
            await _robotService.StartAsync(ToRobotSettings(preferences.Robot));
            SetConnectionStatus(BuildRobotRuntimeStatusText());
        }
        catch (Exception ex)
        {
            SetConnectionStatus(T($"QQ机器人启动失败：{ex.Message}", $"QQ robot start failed: {ex.Message}"));
        }
        finally
        {
            UpdateRobotToggleButtonText();
            UpdateCardValues(_serverProcessService.GetCachedStatus());
        }
    }

    private async Task StopRobotAsync()
    {
        try
        {
            await _robotService.StopAsync(TimeSpan.FromSeconds(5));
            SetConnectionStatus(T("QQ机器人已停止。", "QQ robot stopped."));
        }
        catch (Exception ex)
        {
            SetConnectionStatus(T($"QQ机器人停止失败：{ex.Message}", $"QQ robot stop failed: {ex.Message}"));
        }
        finally
        {
            UpdateRobotToggleButtonText();
            UpdateCardValues(_serverProcessService.GetCachedStatus());
        }
    }

    private async void OnLaunchServerClick(object? sender, RoutedEventArgs e)
    {
        if (_isStoppingOrStarting)
        {
            return;
        }

        var statuses = _serverProcessService.GetCachedStatuses();
        if (!statuses.Any(static status => status.IsRunning) || HasPendingLaunchTargets(statuses))
        {
            await StartSelectedServersAsync();
            return;
        }

        await StopServerFromLaunchButtonAsync();
    }

    private async void OnDashboardServerActionClick(object? sender, RoutedEventArgs e)
    {
        if (_isStoppingOrStarting || sender is not Button { Tag: DashboardServerItem item })
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(item.ProfileId))
        {
            return;
        }

        if (item.IsRunning)
        {
            await StopDashboardServerAsync(item.ProfileId);
            return;
        }

        await StartDashboardServerAsync(item.ProfileId);
    }

    private async Task StartDashboardServerAsync(string profileId)
    {
        var profile = _profileService.GetProfileById(profileId.Trim());
        if (profile is null)
        {
            ShowToast(T("未找到服务器档案。", "Server profile not found."));
            return;
        }

        var savePath = NormalizeFullPath(profile.ActiveSaveFile);
        if (string.IsNullOrWhiteSpace(savePath))
        {
            SelectTab(MainTab.InstanceManage);
            SelectInstanceManageTab(InstanceManageTab.Saves);
            ShowToast(T($"{profile.Name} 未绑定存档，请先绑定后启动。", $"{profile.Name} has no save bound. Bind a save before starting."));
            return;
        }

        SetLaunchOperationBusy(T("启动中...", "Starting..."));
        try
        {
            if (_serverProcessService.GetCurrentStatus(profile.Id).IsRunning)
            {
                ShowToast(T($"{profile.Name} 已在运行。", $"{profile.Name} is already running."));
                return;
            }

            var launchableProfile = await EnsureLaunchableProfileSaveAsync(profile, savePath);
            await StartServerProfileWithTimeoutAsync(launchableProfile);
        }
        catch (Exception ex)
        {
            var errorMessage = GetExceptionMessage(ex);
            AppendConsoleLine(T($"[system] 启动/停止失败：{errorMessage}", $"[system] Start/stop failed: {errorMessage}"));
        }
        finally
        {
            ClearLaunchOperationBusy();
        }
    }

    private async Task StopDashboardServerAsync(string profileId)
    {
        var profile = _profileService.GetProfileById(profileId.Trim());
        SetLaunchOperationBusy(T("停止中...", "Stopping..."));
        try
        {
            AppendConsoleLine(T(
                $"[system] 正在停止服务器：{profile?.Name ?? profileId}",
                $"[system] Stopping server: {profile?.Name ?? profileId}"));
            await _serverProcessService.StopAsync(profileId, TimeSpan.FromSeconds(20));
        }
        catch (Exception ex)
        {
            var errorMessage = GetExceptionMessage(ex);
            AppendConsoleLine(T($"[system] 启动/停止失败：{errorMessage}", $"[system] Start/stop failed: {errorMessage}"));
        }
        finally
        {
            ClearLaunchOperationBusy();
        }
    }

    private void OnLaunchServerPointerEntered(object? sender, PointerEventArgs e)
    {
        if (_serverProcessService.GetCachedStatuses().Any(static status => status.IsRunning) || _launchTargetItems.Count > 0)
        {
            LaunchSelectionPillHost.Classes.Set("expanded", false);
            return;
        }

        LaunchSelectionPillHost.Classes.Set("expanded", true);
    }

    private void OnLaunchServerPointerExited(object? sender, PointerEventArgs e)
    {
        LaunchSelectionPillHost.Classes.Set("expanded", false);
    }

    private async Task StopServerFromLaunchButtonAsync()
    {
        SetLaunchOperationBusy(T("停止中...", "Stopping..."));
        try
        {
            AppendConsoleLine(T("[system] 正在停止服务器...", "[system] Stopping server..."));
            await _serverProcessService.StopAsync(TimeSpan.FromSeconds(20));
        }
        catch (Exception ex)
        {
            var errorMessage = GetExceptionMessage(ex);
            AppendConsoleLine(T($"[system] 启动/停止失败：{errorMessage}", $"[system] Start/stop failed: {errorMessage}"));
        }
        finally
        {
            ClearLaunchOperationBusy();
        }
    }

    private async Task StartSelectedServersAsync()
    {
        if (_isStoppingOrStarting)
        {
            return;
        }

        var selectedIds = LoadLaunchProfileIds();
        if (selectedIds.Count == 0)
        {
            SelectTab(MainTab.InstanceManage);
            SelectInstanceManageTab(InstanceManageTab.Saves);
            LaunchSelectionSummaryTextBlock.Text = T("请先添加要启动的服务器", "Add servers to start first");
            return;
        }

        SetLaunchOperationBusy(T("启动中...", "Starting..."));
        try
        {
            var runningIds = _serverProcessService.GetCachedStatuses()
                .Where(static status => status.IsRunning)
                .Select(static status => status.ProfileId ?? string.Empty)
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var startedCount = 0;
            foreach (var profileId in selectedIds)
            {
                if (runningIds.Contains(profileId))
                {
                    continue;
                }

                var profile = _profileService.GetProfileById(profileId);
                if (profile is null)
                {
                    continue;
                }

                var savePath = NormalizeFullPath(profile.ActiveSaveFile);
                if (string.IsNullOrWhiteSpace(savePath))
                {
                    SelectTab(MainTab.InstanceManage);
                    SelectInstanceManageTab(InstanceManageTab.Saves);
                    ShowToast(T($"{profile.Name} 未绑定存档，请先绑定后启动。", $"{profile.Name} has no save bound. Bind a save before starting."));
                    return;
                }

                var launchableProfile = await EnsureLaunchableProfileSaveAsync(profile, savePath);
                await StartServerProfileWithTimeoutAsync(launchableProfile);
                startedCount++;
            }

            if (startedCount == 0)
            {
                ShowToast(T("选择的服务器均已运行。", "Selected servers are already running."));
            }
        }
        catch (Exception ex)
        {
            var errorMessage = GetExceptionMessage(ex);
            AppendConsoleLine(T($"[system] 启动/停止失败：{errorMessage}", $"[system] Start/stop failed: {errorMessage}"));
        }
        finally
        {
            ClearLaunchOperationBusy();
        }
    }

    private async Task<InstanceProfile> EnsureLaunchableProfileSaveAsync(InstanceProfile profile, string preferredSavePath)
    {
        var normalizedPreferredSavePath = NormalizeFullPath(preferredSavePath);
        if (!string.IsNullOrWhiteSpace(normalizedPreferredSavePath))
        {
            var saves = await _saveService.GetSavesAsync(profile);
            var preferredSave = saves.FirstOrDefault(save =>
                NormalizeFullPath(save.FullPath).Equals(normalizedPreferredSavePath, StringComparison.OrdinalIgnoreCase));
            if (preferredSave is not null)
            {
                await PrepareProfileSaveForLaunchAsync(profile, preferredSave.FullPath);
                return _profileService.GetProfileById(profile.Id) ?? profile;
            }

            await PrepareProfileSaveForLaunchAsync(profile, normalizedPreferredSavePath);
            return _profileService.GetProfileById(profile.Id) ?? profile;
        }

        var currentSavePath = NormalizeFullPath(profile.ActiveSaveFile);
        if (!string.IsNullOrWhiteSpace(currentSavePath))
        {
            await PrepareProfileSaveForLaunchAsync(profile, currentSavePath);
        }

        return _profileService.GetProfileById(profile.Id) ?? profile;
    }

    private async Task PrepareProfileSaveForLaunchAsync(InstanceProfile profile, string savePath)
    {
        var normalizedSavePath = NormalizeFullPath(savePath);
        if (string.IsNullOrWhiteSpace(normalizedSavePath))
        {
            return;
        }

        if (File.Exists(normalizedSavePath))
        {
            var fileInfo = new FileInfo(normalizedSavePath);
            if (fileInfo.Length == 0)
            {
                File.Delete(normalizedSavePath);
            }
        }

        await _saveService.SetActiveSaveAsync(profile, normalizedSavePath);
    }

    private async Task StartServerProfileWithTimeoutAsync(InstanceProfile profile)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(ServerStartTimeoutSeconds));
        try
        {
            var startTask = Task.Run(
                () => _serverProcessService.StartAsync(profile, timeoutCts.Token),
                CancellationToken.None);
            var completedTask = await Task.WhenAny(
                startTask,
                Task.Delay(TimeSpan.FromSeconds(ServerStartTimeoutSeconds)));
            if (!ReferenceEquals(completedTask, startTask))
            {
                await timeoutCts.CancelAsync();
                throw new TimeoutException(T(
                    $"启动服务器超时：{profile.Name}",
                    $"Server start timed out: {profile.Name}"));
            }

            await startTask;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException(T(
                $"启动服务器超时：{profile.Name}",
                $"Server start timed out: {profile.Name}"));
        }
    }

    private void SetLaunchOperationBusy(string text)
    {
        _isStoppingOrStarting = true;
        UpdateDashboardStatus(_serverProcessService.GetCachedStatus());
    }

    private void ClearLaunchOperationBusy()
    {
        _isStoppingOrStarting = false;
        UpdateCardValues(_serverProcessService.GetCachedStatus());
    }

    private async void OnSendCommandClick(object? sender, RoutedEventArgs e)
    {
        await SendCommandFromInputAsync();
    }

    private void OnLaunchAddProfileClick(object? sender, RoutedEventArgs e)
    {
        if (LaunchAddProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            return;
        }

        var ids = LoadLaunchProfileIds();
        ids.Add(profile.Id);
        SaveLaunchProfileIds(ids);
        LaunchAddProfileComboBox.SelectedIndex = -1;
    }

    private void OnLaunchRemoveSelectedProfileClick(object? sender, RoutedEventArgs e)
    {
        var selected = _launchTargetItems.FirstOrDefault(static item => item.IsSelected)
                       ?? _launchTargetItems.LastOrDefault();
        if (selected is null)
        {
            return;
        }

        var ids = LoadLaunchProfileIds();
        ids.Remove(selected.ProfileId);
        SaveLaunchProfileIds(ids);
    }

    private void OnLaunchTargetChipClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: LaunchTargetItem item } button)
        {
            return;
        }

        foreach (var target in _launchTargetItems)
        {
            target.IsSelected = false;
        }

        item.IsSelected = button.IsChecked == true;
    }

    private void OnConsoleServerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ConsoleServerComboBox.SelectedItem is not ConsoleServerItem item)
        {
            return;
        }

        _selectedConsoleProfileId = item.ProfileId;
        _ = EnsureConsoleReplayLoadedAsync(item.ProfileId);
        RefreshConsoleText();
    }

    private async void OnCommandTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await SendCommandFromInputAsync();
    }

    private void OnQuickCommandSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (QuickCommandComboBox.SelectedItem is not string command || string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        CommandTextBox.Text = command;
        CommandTextBox.CaretIndex = command.Length;
        CommandTextBox.Focus();
        QuickCommandComboBox.SelectedIndex = -1;
    }

    private async Task SendCommandFromInputAsync()
    {
        var command = CommandTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        CommandTextBox.Text = string.Empty;
        await SendCommandAsync(command);
    }

    private async Task SendCommandAsync(string command)
    {
        try
        {
            if (ConsoleServerComboBox.SelectedItem is ConsoleServerItem item &&
                !string.IsNullOrWhiteSpace(item.ProfileId))
            {
                await _serverProcessService.SendCommandAsync(item.ProfileId, command);
                return;
            }

            await _serverProcessService.SendCommandAsync(command);
        }
        catch (Exception ex)
        {
            AppendConsoleLine(T($"[system] 命令发送失败：{ex.Message}", $"[system] Failed to send command: {ex.Message}"));
        }
    }

    private async Task RefreshConfigProfilesAsync()
    {
        if (_isRefreshingConfigProfiles)
        {
            return;
        }

        InstanceProfile? targetProfile = null;
        _isRefreshingConfigProfiles = true;
        try
        {
            var selectedProfileId = !string.IsNullOrWhiteSpace(_editingConfigProfileId)
                ? _editingConfigProfileId
                : (ConfigProfileComboBox.SelectedItem as InstanceProfile)?.Id;
            var profiles = _profileService.GetProfiles();
            ConfigProfileComboBox.ItemsSource = profiles;
            targetProfile = profiles.FirstOrDefault(profile =>
                                !string.IsNullOrWhiteSpace(selectedProfileId) &&
                                profile.Id.Equals(selectedProfileId, StringComparison.OrdinalIgnoreCase))
                            ?? profiles.FirstOrDefault();
            ConfigProfileComboBox.SelectedItem = targetProfile;
            SetConfigHasProfiles(profiles.Count > 0);
        }
        finally
        {
            _isRefreshingConfigProfiles = false;
        }

        if (targetProfile is null)
        {
            ClearConfigForm();
            ConfigContentHost.IsEnabled = false;
            ConfigSaveButton.IsEnabled = false;
            SetConfigStatus(T("暂无档案，请先创建档案。", "No profile found. Create a profile first."));
            return;
        }

        await LoadConfigForProfileAsync(targetProfile);
    }

    private async Task OpenProfileConfigEditorAsync(string profileId)
    {
        var normalizedProfileId = profileId.Trim();
        if (string.IsNullOrWhiteSpace(normalizedProfileId))
        {
            SetConfigStatus(T("未找到要修改的档案。", "Profile to edit was not found."));
            return;
        }

        var profile = _profileService.GetProfileById(normalizedProfileId);
        if (profile is null)
        {
            SetConfigStatus(T("未找到要修改的档案。", "Profile to edit was not found."));
            return;
        }

        _editingConfigProfileId = profile.Id;
        _pendingConfigLoadProfileId = profile.Id;
        SelectInstanceManageTab(InstanceManageTab.Config);
        var profiles = _profileService.GetProfiles();
        profile = profiles.FirstOrDefault(item =>
                      item.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase))
                  ?? profile;
        ConfigProfileComboBox.ItemsSource = profiles;
        ConfigProfileComboBox.SelectedItem = profile;
        SetConfigHasProfiles(profiles.Count > 0);
        try
        {
            await LoadConfigForProfileAsync(profile);
        }
        finally
        {
            if (_pendingConfigLoadProfileId.Equals(profile.Id, StringComparison.OrdinalIgnoreCase))
            {
                _pendingConfigLoadProfileId = string.Empty;
            }
        }
    }

    private void SetConfigHasProfiles(bool hasProfiles)
    {
        ConfigScrollViewer.IsVisible = hasProfiles;
        ConfigEmptyPanel.IsVisible = !hasProfiles;
        ConfigRefreshButton.IsEnabled = true;
        ConfigImportButton.IsEnabled = hasProfiles;
        ConfigSaveButton.IsEnabled = hasProfiles && _isConfigLoaded;
        ConfigContentHost.IsEnabled = hasProfiles && _isConfigLoaded;
    }

    private async Task LoadConfigForProfileAsync(InstanceProfile selectedProfile)
    {
        var profile = _profileService.GetProfileById(selectedProfile.Id) ?? selectedProfile;
        var loadVersion = ++_configLoadVersion;
        var configPath = GetConfigPath(profile);
        _isLoadingConfig = true;
        _isConfigLoaded = false;
        _loadedConfigProfileId = string.Empty;
        ConfigSaveButton.IsEnabled = false;
        ConfigContentHost.IsEnabled = false;
        try
        {
            var rawJson = await _instanceServerConfigService.LoadRawJsonAsync(profile);
            var root = ParseConfigRootForUi(rawJson, configPath);
            var serverSettings = BuildConfigServerSettings(root);
            var worldSettings = BuildConfigWorldSettings(profile, root);
            var worldRules = BuildConfigWorldRules(root);

            if (!IsActiveConfigLoad(loadVersion, profile.Id))
            {
                return;
            }

            LoadConfigGameLanguageZh(profile);
            ApplyConfigServerSettings(serverSettings);
            if (!await LoadConfigSavesAsync(profile, worldSettings.SaveFileLocation, loadVersion))
            {
                return;
            }

            ApplyConfigWorldSettings(worldSettings);
            RebuildConfigWorldRules(worldRules);
            UpdateConfigWorldGeneratedState();
            if (!IsActiveConfigLoad(loadVersion, profile.Id))
            {
                return;
            }

            _isConfigLoaded = true;
            _loadedConfigProfileId = profile.Id;
            ConfigSaveButton.IsEnabled = true;
            ConfigContentHost.IsEnabled = true;
            ConfigStatusTextBlock.Text = string.Empty;
        }
        catch (Exception ex)
        {
            if (!IsActiveConfigLoad(loadVersion, profile.Id))
            {
                return;
            }

            ClearConfigForm();
            ConfigContentHost.IsEnabled = false;
            ConfigSaveButton.IsEnabled = false;
            SetConfigStatus(FormatConfigLoadFailure(profile, ex));
        }
        finally
        {
            if (loadVersion == _configLoadVersion)
            {
                ConfigContentHost.IsEnabled = _isConfigLoaded;
                _isLoadingConfig = false;
            }
        }
    }

    private bool IsActiveConfigLoad(long loadVersion, string profileId)
    {
        return loadVersion == _configLoadVersion &&
               (string.IsNullOrWhiteSpace(_editingConfigProfileId) ||
                _editingConfigProfileId.Equals(profileId, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyConfigServerSettings(ServerCommonSettings settings)
    {
        ConfigServerNameTextBox.Text = settings.ServerName;
        ConfigServerDescriptionTextBox.Text = settings.ServerDescription ?? string.Empty;
        ConfigServerUrlTextBox.Text = settings.ServerUrl ?? string.Empty;
        ConfigIpTextBox.Text = settings.Ip ?? string.Empty;
        SetNumericValue(ConfigPortNumericUpDown, settings.Port);
        SetNumericValue(ConfigMaxClientsNumericUpDown, settings.MaxClients);
        SetNumericValue(ConfigMaxClientsInQueueNumericUpDown, settings.MaxClientsInQueue);
        ConfigPasswordTextBox.Text = settings.Password ?? string.Empty;
        ConfigAdvertiseServerCheckBox.IsChecked = settings.AdvertiseServer;
        SelectConfigChoiceByValue(ConfigWhitelistModeComboBox, _configWhitelistModeOptions, settings.WhitelistMode.ToString(CultureInfo.InvariantCulture));
        ConfigUpnpCheckBox.IsChecked = settings.Upnp;
        ConfigAllowPvPCheckBox.IsChecked = settings.AllowPvP;
        ConfigAllowFireSpreadCheckBox.IsChecked = settings.AllowFireSpread;
        ConfigAllowFallingBlocksCheckBox.IsChecked = settings.AllowFallingBlocks;
        ConfigPassTimeWhenEmptyCheckBox.IsChecked = settings.PassTimeWhenEmpty;
        SetNumericValue(ConfigWarnAfkSecondsNumericUpDown, settings.WarnClientsAfterAfkSeconds);
        SetNumericValue(ConfigKickAfkSecondsNumericUpDown, settings.KickClientsAfterAfkSeconds);
        SetNumericValue(ConfigClientConnectionTimeoutNumericUpDown, settings.ClientConnectionTimeout);
        SetNumericValue(ConfigMaxChunkRadiusNumericUpDown, settings.MaxChunkRadius);
        SetNumericValue(ConfigDieBelowDiskSpaceMbNumericUpDown, settings.DieBelowDiskSpaceMb);
        ConfigCorruptionProtectionCheckBox.IsChecked = settings.CorruptionProtection;
        ConfigRegenerateCorruptChunksCheckBox.IsChecked = settings.RegenerateCorruptChunks;
        ConfigStartupCommandsTextBox.Text = settings.StartupCommands;
        ConfigVerifyPlayerAuthCheckBox.IsChecked = settings.VerifyPlayerAuth;
        EnsureComboItem(ConfigServerLanguageComboBox, settings.ServerLanguage);
        ConfigServerLanguageComboBox.SelectedItem = settings.ServerLanguage;
        EnsureConfigChoiceOptionExists(_configDefaultRoleOptions, settings.DefaultRoleCode);
        SelectConfigChoiceByValue(ConfigDefaultRoleComboBox, _configDefaultRoleOptions, settings.DefaultRoleCode);
        ConfigDefaultRoleCodeTextBox.Text = settings.DefaultRoleCode;
        ConfigWelcomeMessageTextBox.Text = settings.WelcomeMessage;
    }

    private async Task<bool> LoadConfigSavesAsync(
        InstanceProfile profile,
        string preferredSavePath,
        long? loadVersion = null)
    {
        var saves = await _saveService.GetSavesAsync(profile);
        if (loadVersion.HasValue && !IsActiveConfigLoad(loadVersion.Value, profile.Id))
        {
            return false;
        }

        _configSaveItems.Clear();
        foreach (var save in saves)
        {
            _configSaveItems.Add(ConfigSaveFileItem.FromSave(save));
        }

        var normalizedPreferred = NormalizeFullPath(preferredSavePath);
        if (string.IsNullOrWhiteSpace(normalizedPreferred))
        {
            normalizedPreferred = NormalizeFullPath(profile.ActiveSaveFile);
        }

        if (!string.IsNullOrWhiteSpace(normalizedPreferred) &&
            _configSaveItems.All(item => !item.FullPath.Equals(normalizedPreferred, StringComparison.OrdinalIgnoreCase)))
        {
            _configSaveItems.Insert(0, ConfigSaveFileItem.FromPath(normalizedPreferred));
        }

        ConfigSaveFileComboBox.SelectedItem =
            _configSaveItems.FirstOrDefault(item => item.FullPath.Equals(normalizedPreferred, StringComparison.OrdinalIgnoreCase))
            ?? _configSaveItems.FirstOrDefault();
        return true;
    }

    private void ApplyConfigWorldSettings(WorldSettings settings)
    {
        _configSaveFileLocation = settings.SaveFileLocation;
        ConfigSeedTextBox.Text = settings.Seed;
        ConfigWorldNameTextBox.Text = settings.WorldName;
        EnsureConfigChoiceOptionExists(_configPlayStyleOptions, settings.PlayStyle);
        EnsureConfigChoiceOptionExists(_configWorldTypeOptions, settings.WorldType);
        SelectConfigChoiceByValue(ConfigPlayStyleComboBox, _configPlayStyleOptions, settings.PlayStyle);
        SelectConfigChoiceByValue(ConfigWorldTypeComboBox, _configWorldTypeOptions, settings.WorldType);
        SetNumericValue(ConfigWorldHeightNumericUpDown, settings.WorldHeight ?? 256);
    }

    private JsonObject ParseConfigRootForUi(string rawJson, string configPath)
    {
        try
        {
            return JsonNode.Parse(rawJson) as JsonObject
                   ?? throw new InvalidDataException(T(
                       $"配置根节点必须是 JSON 对象：{configPath}",
                       $"The configuration root must be a JSON object: {configPath}"));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(T(
                $"配置文件无法解析：{configPath}",
                $"The configuration file could not be parsed: {configPath}"), ex);
        }
    }

    private ServerCommonSettings BuildConfigServerSettings(JsonObject root)
    {
        return new ServerCommonSettings
        {
            ServerName = ReadConfigString(root["ServerName"], "Vintage Story Server"),
            ServerDescription = ReadConfigNullableString(root["ServerDescription"]),
            ServerUrl = ReadConfigNullableString(root["ServerUrl"]),
            Ip = ReadConfigNullableString(root["Ip"]),
            Port = ReadConfigInt(root["Port"], 42420),
            MaxClients = ReadConfigInt(root["MaxClients"], 16),
            MaxClientsInQueue = ReadConfigInt(root["MaxClientsInQueue"], 0),
            Password = ReadConfigNullableString(root["Password"]),
            AdvertiseServer = ReadConfigBool(root["AdvertiseServer"], false),
            WhitelistMode = ReadConfigInt(root["WhitelistMode"], 0),
            Upnp = ReadConfigBool(root["Upnp"], false),
            AllowPvP = ReadConfigBool(root["AllowPvP"], true),
            AllowFireSpread = ReadConfigBool(root["AllowFireSpread"], true),
            AllowFallingBlocks = ReadConfigBool(root["AllowFallingBlocks"], true),
            PassTimeWhenEmpty = ReadConfigBool(root["PassTimeWhenEmpty"], false),
            WarnClientsAfterAfkSeconds = ReadConfigInt(root["WarnClientsAfterAfkSeconds"], 0),
            KickClientsAfterAfkSeconds = ReadConfigInt(root["KickClientsAfterAfkSeconds"], 0),
            ClientConnectionTimeout = ReadConfigInt(root["ClientConnectionTimeout"], 150),
            MaxChunkRadius = ReadConfigInt(root["MaxChunkRadius"], 12),
            DieBelowDiskSpaceMb = ReadConfigInt(root["DieBelowDiskSpaceMb"], 400),
            CorruptionProtection = ReadConfigBool(root["CorruptionProtection"], true),
            RegenerateCorruptChunks = ReadConfigBool(root["RegenerateCorruptChunks"], false),
            StartupCommands = ReadConfigString(root["StartupCommands"], string.Empty),
            VerifyPlayerAuth = ReadConfigBool(root["VerifyPlayerAuth"], true),
            ServerLanguage = ReadConfigString(root["ServerLanguage"], ResolveDefaultServerLanguage()),
            DefaultRoleCode = ReadConfigString(root["DefaultRoleCode"], "suplayer"),
            WelcomeMessage = ReadConfigString(root["WelcomeMessage"], string.Empty)
        };
    }

    private static WorldSettings BuildConfigWorldSettings(InstanceProfile profile, JsonObject root)
    {
        var worldConfig = root["WorldConfig"] as JsonObject ?? [];
        var worldRules = worldConfig["WorldConfiguration"] as JsonObject ?? [];
        var mapSizeY = ReadConfigNullableInt(worldConfig["MapSizeY"]) ?? ReadConfigNullableInt(worldRules["worldHeight"]);

        return new WorldSettings
        {
            Seed = ReadConfigString(worldConfig["Seed"], "123456789"),
            WorldName = ReadConfigString(worldConfig["WorldName"], "A new world"),
            SaveFileLocation = ReadConfigString(worldConfig["SaveFileLocation"], ResolveCurrentConfigSaveFilePath(profile)),
            PlayStyle = ReadConfigString(worldConfig["PlayStyle"], "surviveandbuild"),
            WorldType = ReadConfigString(worldConfig["WorldType"], "standard"),
            WorldHeight = mapSizeY ?? 256
        };
    }

    private static IReadOnlyList<WorldRuleValue> BuildConfigWorldRules(JsonObject root)
    {
        var worldConfig = root["WorldConfig"] as JsonObject ?? [];
        var worldRules = worldConfig["WorldConfiguration"] as JsonObject ?? [];

        return WorldRuleCatalog.DefaultRules
            .Select(rule => new WorldRuleValue
            {
                Definition = rule,
                Value = ReadConfigFlexibleString(worldRules[rule.Key])
                        ?? ReadConfigRuleFallbackValue(rule.Key, root, worldConfig)
                        ?? rule.DefaultValue
            })
            .ToList();
    }

    private void ClearConfigForm()
    {
        _isConfigLoaded = false;
        _loadedConfigProfileId = string.Empty;
        ConfigSaveButton.IsEnabled = false;
        ConfigServerNameTextBox.Text = "Vintage Story Server";
        ConfigServerDescriptionTextBox.Text = string.Empty;
        ConfigServerUrlTextBox.Text = string.Empty;
        ConfigIpTextBox.Text = string.Empty;
        SetNumericValue(ConfigPortNumericUpDown, 42420);
        SetNumericValue(ConfigMaxClientsNumericUpDown, 16);
        SetNumericValue(ConfigMaxClientsInQueueNumericUpDown, 0);
        ConfigPasswordTextBox.Text = string.Empty;
        ConfigAdvertiseServerCheckBox.IsChecked = false;
        SelectConfigChoiceByValue(ConfigWhitelistModeComboBox, _configWhitelistModeOptions, "0");
        ConfigUpnpCheckBox.IsChecked = false;
        ConfigAllowPvPCheckBox.IsChecked = true;
        ConfigAllowFireSpreadCheckBox.IsChecked = true;
        ConfigAllowFallingBlocksCheckBox.IsChecked = true;
        ConfigPassTimeWhenEmptyCheckBox.IsChecked = false;
        SetNumericValue(ConfigWarnAfkSecondsNumericUpDown, 0);
        SetNumericValue(ConfigKickAfkSecondsNumericUpDown, 0);
        SetNumericValue(ConfigClientConnectionTimeoutNumericUpDown, 150);
        SetNumericValue(ConfigMaxChunkRadiusNumericUpDown, 12);
        SetNumericValue(ConfigDieBelowDiskSpaceMbNumericUpDown, 400);
        ConfigCorruptionProtectionCheckBox.IsChecked = true;
        ConfigRegenerateCorruptChunksCheckBox.IsChecked = false;
        ConfigStartupCommandsTextBox.Text = string.Empty;
        ConfigVerifyPlayerAuthCheckBox.IsChecked = true;
        ConfigServerLanguageComboBox.SelectedItem = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-cn" : "en";
        SelectConfigChoiceByValue(ConfigDefaultRoleComboBox, _configDefaultRoleOptions, "suplayer");
        ConfigDefaultRoleCodeTextBox.Text = "suplayer";
        ConfigWelcomeMessageTextBox.Text = string.Empty;
        ConfigSeedTextBox.Text = "123456789";
        ConfigWorldNameTextBox.Text = "A new world";
        _configSaveFileLocation = string.Empty;
        _configSaveItems.Clear();
        SelectConfigChoiceByValue(ConfigPlayStyleComboBox, _configPlayStyleOptions, "surviveandbuild");
        SelectConfigChoiceByValue(ConfigWorldTypeComboBox, _configWorldTypeOptions, "standard");
        SetNumericValue(ConfigWorldHeightNumericUpDown, 256);
        _configWorldRuleItems.Clear();
        UpdateConfigWorldGeneratedState();
    }

    private async void OnConfigProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingConfigProfiles || ConfigProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            return;
        }

        await LoadConfigForProfileAsync(profile);
    }

    private async void OnConfigRefreshClick(object? sender, RoutedEventArgs e)
    {
        var profile = GetSelectedConfigProfile();
        if (profile is null)
        {
            await RefreshConfigProfilesAsync();
            return;
        }

        await LoadConfigForProfileAsync(profile);
    }

    private void OnConfigBackClick(object? sender, RoutedEventArgs e)
    {
        _editingConfigProfileId = string.Empty;
        _pendingConfigLoadProfileId = string.Empty;
        SelectInstanceManageTab(InstanceManageTab.Profiles);
    }

    private async void OnConfigImportClick(object? sender, RoutedEventArgs e)
    {
        var profile = GetSelectedConfigProfile();
        if (profile is null)
        {
            SetConfigStatus(T("请先选择档案。", "Select a profile first."));
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("导入 serverconfig.json", "Import serverconfig.json"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("JSON")
                {
                    Patterns = ["*.json"]
                }
            ]
        });

        var path = TryGetLocalPath(files.FirstOrDefault());
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            await _instanceServerConfigService.ImportRawJsonAsync(profile, path);
            InvalidateDashboardSettingsCache(profile);
            await LoadConfigForProfileAsync(profile);
            SetConfigStatus(T($"已导入配置：{Path.GetFileName(path)}", $"Configuration imported: {Path.GetFileName(path)}"));
        }
        catch (Exception ex)
        {
            SetConfigStatus(T($"导入配置失败：{ex.Message}", $"Failed to import configuration: {ex.Message}"));
        }
    }

    private async void OnConfigSaveClick(object? sender, RoutedEventArgs e)
    {
        await SaveConfigAsync();
    }

    private void OnConfigDefaultRoleSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingConfig || ConfigDefaultRoleComboBox.SelectedItem is not ConfigChoiceOption option)
        {
            return;
        }

        ConfigDefaultRoleCodeTextBox.Text = option.Value;
    }

    private void OnConfigSaveFileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingConfig)
        {
            return;
        }

        if (ConfigSaveFileComboBox.SelectedItem is ConfigSaveFileItem item)
        {
            _configSaveFileLocation = item.FullPath;
        }

        UpdateConfigWorldGeneratedState();
    }

    private async Task SaveConfigAsync()
    {
        var profile = GetSelectedConfigProfile();
        if (profile is null)
        {
            SetConfigStatus(T("请先选择档案。", "Select a profile first."));
            return;
        }

        if (!_isConfigLoaded || !_loadedConfigProfileId.Equals(profile.Id, StringComparison.OrdinalIgnoreCase))
        {
            ConfigSaveButton.IsEnabled = false;
            SetConfigStatus(
                T("配置尚未成功加载，已禁止保存以避免覆盖原文件。", "Configuration has not loaded successfully; saving is disabled to avoid overwriting the original file."));
            return;
        }

        ConfigSaveButton.IsEnabled = false;
        try
        {
            var saveFile = ResolveConfigSavePath(profile);
            var serverSettings = CollectConfigServerSettings();
            var worldSettings = CollectConfigWorldSettings(saveFile);
            var rules = _configWorldRuleItems
                .Select(item => new WorldRuleValue
                {
                    Definition = item.Definition,
                    Value = item.Value
                })
                .ToList();

            if (IsSaveWorldGenerated(saveFile))
            {
                var persistedWorldSettings = await _instanceServerConfigService.LoadWorldSettingsAsync(profile);
                var persistedRules = await _instanceServerConfigService.LoadWorldRulesAsync(profile);
                var persistedRuleValues = persistedRules.ToDictionary(
                    rule => rule.Definition.Key,
                    rule => rule.Value ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase);

                worldSettings.Seed = persistedWorldSettings.Seed;
                worldSettings.PlayStyle = persistedWorldSettings.PlayStyle;
                worldSettings.WorldType = persistedWorldSettings.WorldType;
                worldSettings.WorldHeight = persistedWorldSettings.WorldHeight ?? worldSettings.WorldHeight;

                foreach (var rule in rules)
                {
                    if (ConfigOnlyDuringWorldCreateRuleKeys.Contains(rule.Definition.Key) &&
                        persistedRuleValues.TryGetValue(rule.Definition.Key, out var persistedValue))
                    {
                        rule.Value = persistedValue;
                    }
                }
            }

            await _instanceServerConfigService.SaveSettingsAsync(profile, serverSettings, worldSettings, rules);
            UpdateDashboardSettingsCache(profile, serverSettings);

            profile.ActiveSaveFile = saveFile;
            profile.SaveDirectory = Path.GetDirectoryName(saveFile) ?? profile.SaveDirectory;
            profile.LastUpdatedUtc = DateTimeOffset.UtcNow;
            _profileService.UpdateProfile(profile);
            _configSaveFileLocation = saveFile;

            await LoadConfigSavesAsync(profile, saveFile);
            UpdateConfigWorldGeneratedState();
            await RefreshSavesAsync();
            RefreshLaunchOptions();
            RefreshProfiles();
            _isConfigLoaded = true;
            _loadedConfigProfileId = profile.Id;
            SetConfigStatus(T("配置已保存。", "Configuration saved."));
        }
        catch (Exception ex)
        {
            _isConfigLoaded = false;
            _loadedConfigProfileId = string.Empty;
            SetConfigStatus(T($"保存配置失败：{ex.Message}", $"Failed to save configuration: {ex.Message}"));
        }
        finally
        {
            ConfigSaveButton.IsEnabled = _isConfigLoaded;
            ConfigContentHost.IsEnabled = _isConfigLoaded;
        }
    }

    private ServerCommonSettings CollectConfigServerSettings()
    {
        return new ServerCommonSettings
        {
            ServerName = ConfigServerNameTextBox.Text?.Trim() ?? string.Empty,
            ServerDescription = NullIfWhiteSpace(ConfigServerDescriptionTextBox.Text),
            ServerUrl = NullIfWhiteSpace(ConfigServerUrlTextBox.Text),
            Ip = NullIfWhiteSpace(ConfigIpTextBox.Text),
            Port = GetNumericValue(ConfigPortNumericUpDown, 42420),
            MaxClients = GetNumericValue(ConfigMaxClientsNumericUpDown, 16),
            MaxClientsInQueue = GetNumericValue(ConfigMaxClientsInQueueNumericUpDown, 0),
            Password = NullIfWhiteSpace(ConfigPasswordTextBox.Text),
            AdvertiseServer = ConfigAdvertiseServerCheckBox.IsChecked == true,
            WhitelistMode = TryParseInt((ConfigWhitelistModeComboBox.SelectedItem as ConfigChoiceOption)?.Value, 0),
            Upnp = ConfigUpnpCheckBox.IsChecked == true,
            AllowPvP = ConfigAllowPvPCheckBox.IsChecked == true,
            AllowFireSpread = ConfigAllowFireSpreadCheckBox.IsChecked == true,
            AllowFallingBlocks = ConfigAllowFallingBlocksCheckBox.IsChecked == true,
            PassTimeWhenEmpty = ConfigPassTimeWhenEmptyCheckBox.IsChecked == true,
            WarnClientsAfterAfkSeconds = GetNumericValue(ConfigWarnAfkSecondsNumericUpDown, 0),
            KickClientsAfterAfkSeconds = GetNumericValue(ConfigKickAfkSecondsNumericUpDown, 0),
            ClientConnectionTimeout = GetNumericValue(ConfigClientConnectionTimeoutNumericUpDown, 150),
            MaxChunkRadius = GetNumericValue(ConfigMaxChunkRadiusNumericUpDown, 12),
            DieBelowDiskSpaceMb = GetNumericValue(ConfigDieBelowDiskSpaceMbNumericUpDown, 400),
            CorruptionProtection = ConfigCorruptionProtectionCheckBox.IsChecked == true,
            RegenerateCorruptChunks = ConfigRegenerateCorruptChunksCheckBox.IsChecked == true,
            StartupCommands = ConfigStartupCommandsTextBox.Text?.Trim() ?? string.Empty,
            VerifyPlayerAuth = ConfigVerifyPlayerAuthCheckBox.IsChecked == true,
            ServerLanguage = ConfigServerLanguageComboBox.SelectedItem?.ToString() ?? ResolveDefaultServerLanguage(),
            DefaultRoleCode = string.IsNullOrWhiteSpace(ConfigDefaultRoleCodeTextBox.Text)
                ? (ConfigDefaultRoleComboBox.SelectedItem as ConfigChoiceOption)?.Value ?? "suplayer"
                : ConfigDefaultRoleCodeTextBox.Text.Trim(),
            WelcomeMessage = ConfigWelcomeMessageTextBox.Text?.Trim() ?? string.Empty
        };
    }

    private WorldSettings CollectConfigWorldSettings(string saveFile)
    {
        return new WorldSettings
        {
            Seed = ConfigSeedTextBox.Text?.Trim() ?? string.Empty,
            WorldName = ConfigWorldNameTextBox.Text?.Trim() ?? string.Empty,
            SaveFileLocation = saveFile,
            PlayStyle = (ConfigPlayStyleComboBox.SelectedItem as ConfigChoiceOption)?.Value ?? "surviveandbuild",
            WorldType = (ConfigWorldTypeComboBox.SelectedItem as ConfigChoiceOption)?.Value ?? "standard",
            WorldHeight = GetNumericValue(ConfigWorldHeightNumericUpDown, 256)
        };
    }

    private void RebuildConfigChoiceOptions()
    {
        var selectedWhitelist = (ConfigWhitelistModeComboBox.SelectedItem as ConfigChoiceOption)?.Value;
        var selectedRole = (ConfigDefaultRoleComboBox.SelectedItem as ConfigChoiceOption)?.Value ?? ConfigDefaultRoleCodeTextBox.Text;
        var selectedPlayStyle = (ConfigPlayStyleComboBox.SelectedItem as ConfigChoiceOption)?.Value;
        var selectedWorldType = (ConfigWorldTypeComboBox.SelectedItem as ConfigChoiceOption)?.Value;

        _configWhitelistModeOptions.Clear();
        foreach (var (value, zh, en) in ConfigWhitelistModeDefinitions)
        {
            _configWhitelistModeOptions.Add(new ConfigChoiceOption(value.ToString(CultureInfo.InvariantCulture), T(zh, en)));
        }

        _configDefaultRoleOptions.Clear();
        foreach (var (value, zh, en) in ConfigRoleDefinitions)
        {
            _configDefaultRoleOptions.Add(new ConfigChoiceOption(value, T(zh, en)));
        }

        _configPlayStyleOptions.Clear();
        foreach (var (value, zh, en) in ConfigPlayStyleDefinitions)
        {
            _configPlayStyleOptions.Add(new ConfigChoiceOption(value, T(zh, en)));
        }

        _configWorldTypeOptions.Clear();
        foreach (var (value, zh, en) in ConfigWorldTypeDefinitions)
        {
            _configWorldTypeOptions.Add(new ConfigChoiceOption(value, T(zh, en)));
        }

        SelectConfigChoiceByValue(ConfigWhitelistModeComboBox, _configWhitelistModeOptions, selectedWhitelist ?? "0");
        EnsureConfigChoiceOptionExists(_configDefaultRoleOptions, selectedRole);
        SelectConfigChoiceByValue(ConfigDefaultRoleComboBox, _configDefaultRoleOptions, selectedRole ?? "suplayer");
        EnsureConfigChoiceOptionExists(_configPlayStyleOptions, selectedPlayStyle);
        SelectConfigChoiceByValue(ConfigPlayStyleComboBox, _configPlayStyleOptions, selectedPlayStyle ?? "surviveandbuild");
        EnsureConfigChoiceOptionExists(_configWorldTypeOptions, selectedWorldType);
        SelectConfigChoiceByValue(ConfigWorldTypeComboBox, _configWorldTypeOptions, selectedWorldType ?? "standard");
    }

    private void LoadConfigGameLanguageZh(InstanceProfile profile)
    {
        if (!_isChinese)
        {
            _configGameLanguageZh.Clear();
            _configGameLanguageZhPath = string.Empty;
            return;
        }

        var languagePath = ResolveConfigGameLanguageZhPath(profile) ?? string.Empty;
        if (languagePath.Equals(_configGameLanguageZhPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _configGameLanguageZh.Clear();
        _configGameLanguageZhPath = languagePath;
        if (string.IsNullOrWhiteSpace(languagePath))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(languagePath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.Name.StartsWith("worldattribute-", StringComparison.OrdinalIgnoreCase) &&
                    !property.Name.StartsWith("worldconfig-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var text = property.Value.ValueKind == JsonValueKind.String
                    ? NormalizeGameLanguageText(property.Value.GetString())
                    : string.Empty;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    _configGameLanguageZh[property.Name] = text;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load Vintage Story Chinese language file: {Path}", languagePath);
        }
    }

    private string? ResolveConfigGameLanguageZhPath(InstanceProfile profile)
    {
        var preferences = _preferencesService.Load();
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(preferences.ServerDirectory) &&
            !string.IsNullOrWhiteSpace(profile.Version))
        {
            var installedRoot = Path.Combine(preferences.ServerDirectory, "installed");
            var versionDirectory = Path.Combine(installedRoot, SanitizeConfigPathSegment(profile.Version));
            candidates.Add(Path.Combine(versionDirectory, "assets", "game", "lang", "zh-cn.json"));

            if (Directory.Exists(installedRoot))
            {
                foreach (var directory in Directory.EnumerateDirectories(installedRoot))
                {
                    if (Path.GetFileName(directory).Equals(profile.Version, StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(directory).Equals(SanitizeConfigPathSegment(profile.Version), StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add(Path.Combine(directory, "assets", "game", "lang", "zh-cn.json"));
                    }
                }
            }
        }

        var current = string.IsNullOrWhiteSpace(profile.DirectoryPath)
            ? null
            : new DirectoryInfo(profile.DirectoryPath);
        for (var depth = 0; current is not null && depth < 6; depth++, current = current.Parent)
        {
            candidates.Add(Path.Combine(current.FullName, "assets", "game", "lang", "zh-cn.json"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string SanitizeConfigPathSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = string.Join('_', value.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(sanitized) ? value.Trim() : sanitized.Trim();
    }

    private static string NormalizeGameLanguageText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text
            .Replace("<br>", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("</font>", string.Empty, StringComparison.OrdinalIgnoreCase);
        normalized = Regex.Replace(normalized, "<font[^>]*>", string.Empty, RegexOptions.IgnoreCase);
        return normalized.Trim();
    }

    private bool TryGetConfigGameLanguageText(string key, out string text)
    {
        return _configGameLanguageZh.TryGetValue(key, out text!) &&
               !string.IsNullOrWhiteSpace(text);
    }

    private string ResolveConfigRuleLabelZh(WorldRuleDefinition definition)
    {
        return TryGetConfigGameLanguageText($"worldattribute-{definition.Key}", out var label)
            ? label
            : definition.LabelZh;
    }

    private void RebuildConfigWorldRules(IReadOnlyList<WorldRuleValue> rules)
    {
        _configWorldRuleItems.Clear();
        foreach (var rule in rules)
        {
            var value = rule.Value ?? string.Empty;
            var item = new ConfigWorldRuleItem(
                rule.Definition,
                value,
                _isChinese,
                BuildConfigRuleChoiceOptions(rule.Definition, value),
                ResolveConfigRuleLabelZh(rule.Definition))
            {
                IsOnlyDuringWorldCreate = ConfigOnlyDuringWorldCreateRuleKeys.Contains(rule.Definition.Key)
            };
            _configWorldRuleItems.Add(item);
        }
    }

    private IReadOnlyList<ConfigChoiceOption> BuildConfigRuleChoiceOptions(WorldRuleDefinition definition, string currentValue)
    {
        if (definition.Choices.Count == 0)
        {
            return [];
        }

        var options = new List<ConfigChoiceOption>(definition.Choices.Count + 1);
        for (var index = 0; index < definition.Choices.Count; index++)
        {
            var value = definition.Choices[index];
            var choiceName = index < definition.ChoiceNames.Count ? definition.ChoiceNames[index] : value;
            options.Add(new ConfigChoiceOption(value, ResolveConfigRuleChoiceLabel(definition.Key, value, choiceName)));
        }

        if (!string.IsNullOrWhiteSpace(currentValue) &&
            options.All(option => !option.Value.Equals(currentValue, StringComparison.OrdinalIgnoreCase)))
        {
            options.Add(new ConfigChoiceOption(currentValue, currentValue));
        }

        return options;
    }

    private string ResolveConfigRuleChoiceLabel(string key, string value, string name)
    {
        if (!_isChinese)
        {
            return name;
        }

        if (TryGetConfigGameLanguageText($"worldconfig-{key}-{name}", out var localizedName))
        {
            return localizedName;
        }

        if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return "启用";
        }

        if (value.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return "禁用";
        }

        return key.ToLowerInvariant() switch
        {
            "bodytemperatureresistance" when double.TryParse(name, NumberStyles.Float, CultureInfo.InvariantCulture, out _) => $"{name}°C",
            "gamemode" when value.Equals("survival", StringComparison.OrdinalIgnoreCase) => "生存",
            "gamemode" when value.Equals("creative", StringComparison.OrdinalIgnoreCase) => "创造",
            "playerlives" when value == "-1" => "无限",
            "worldedge" when value.Equals("blocked", StringComparison.OrdinalIgnoreCase) => "被阻挡",
            "worldedge" when value.Equals("traversable", StringComparison.OrdinalIgnoreCase) => "可越过/可掉落",
            "deathpunishment" when value.Equals("drop", StringComparison.OrdinalIgnoreCase) => "掉落背包物品",
            "deathpunishment" when value.Equals("keep", StringComparison.OrdinalIgnoreCase) => "保留背包物品",
            "seasons" when value.Equals("enabled", StringComparison.OrdinalIgnoreCase) => "启用",
            "seasons" when value.Equals("spring", StringComparison.OrdinalIgnoreCase) => "关闭，永远春天",
            "seasons" when value.Equals("summer", StringComparison.OrdinalIgnoreCase) => "关闭，永远夏天",
            "seasons" when value.Equals("fall", StringComparison.OrdinalIgnoreCase) => "关闭，永远秋天",
            "seasons" when value.Equals("winter", StringComparison.OrdinalIgnoreCase) => "关闭，永远冬天",
            "temporalrifts" when value.Equals("off", StringComparison.OrdinalIgnoreCase) => "关闭",
            "temporalrifts" when value.Equals("invisible", StringComparison.OrdinalIgnoreCase) => "不可见",
            "temporalrifts" when value.Equals("visible", StringComparison.OrdinalIgnoreCase) => "可见",
            _ => ResolveCommonConfigChoiceName(name)
        };
    }

    private static string ResolveCommonConfigChoiceName(string name)
    {
        var normalized = name.Trim();
        if (TryResolveCommonConfigChoicePattern(normalized, out var patterned))
        {
            return patterned;
        }

        return name switch
        {
            "Enabled" => "启用",
            "Disabled" => "禁用",
            "Allowed" => "允许",
            "Disallowed" => "不允许",
            "Off" => "关",
            "Normal" => "正常",
            "Fast" => "快",
            "Slightly faster" => "稍快",
            "Slightly slower" => "稍慢",
            "Slower" => "缓",
            "Much slower" => "很慢",
            "Very common" => "非常常见",
            "Common" => "常见",
            "Uncommon" => "不常见",
            "Rare" => "稀有",
            "Very Rare" => "非常稀有",
            "Extremly rare" => "极其稀有",
            "Never" => "不存在",
            "None" => "无",
            "Survival" => "生存",
            "Creative" => "创造",
            "Aggressive" => "主动",
            "Passive" => "被动",
            "Never hostile" => "友好",
            "Hot (28-32°C)" => "炎热 (28~32°C)",
            "Warm (19-23 °C)" => "温暖 (19~23°C)",
            "Temperate (6-14 °C)" => "温和 (6~14°C)",
            "Cool (-5 to 1 °C)" => "寒冷 (-5~1°C)",
            "Icy (-15 to -10°C)" => "严寒 (-15~-10°C)",
            "Sand and gravel" => "沙子和砂砾",
            "Sand, gravel and soil with sideways instability" => "沙子、砂砾和边缘不稳定泥土",
            "Stone and Wood" => "石头、木头和石砖",
            "Most cubic blocks" => "大部分方形方块",
            "ifrepaired" => "只有先用胶水修补时可获取",
            "yes" => "可以，拆除即可获取",
            "no" => "否，拆除总会碎掉",
            "Realistic" => "真实",
            "Patchy" => "片状",
            "Blocked" => "被阻挡",
            "Traversable (Can fall down)" => "可越过/可掉落",
            "Scorching hot" => "灼热",
            "Very hot" => "炎热",
            "Hot" => "热",
            "Cold" => "冷",
            "Very Cold" => "很冷",
            "Snowball earth" => "雪球地球",
            "Super humid" => "潮湿",
            "Very humid" => "湿润",
            "Humid" => "湿",
            "Semi-Arid" => "半干旱",
            "Arid" => "干旱",
            "Hyperarid" => "干燥",
            "Forest World (+100%)" => "森林世界/+100%",
            "Extremely forested (+90%)" => "极多树木/+90%",
            "Very highly forested (+75%)" => "很多树木/+75%",
            "Highly forested (+50%)" => "较多树木/+50%",
            "Somewhat more forest (+25%)" => "略多树木/+25%",
            "Somewhat less forest (-25%)" => "略少树木/-25%",
            "Significantly less forested (-50%)" => "较少树木/-50%",
            "Much less forested (-75%)" => "很少树木/-75%",
            "Near Tree-less (-90%)" => "极少树木/-90%",
            "Tree-less World (-100%)" => "无树世界/-100%",
            _ => name
        };
    }

    private static bool TryResolveCommonConfigChoicePattern(string name, out string label)
    {
        label = string.Empty;
        var blocksMatch = Regex.Match(name, @"^(?<value>[0-9.]+)\s*(?<unit>k|mil)? blocks$", RegexOptions.IgnoreCase);
        if (blocksMatch.Success)
        {
            var value = blocksMatch.Groups["value"].Value;
            var unit = blocksMatch.Groups["unit"].Value.ToLowerInvariant();
            label = unit switch
            {
                "mil" => $"{value}百万个方块",
                "k" => $"{value}千个方块",
                _ => $"{value}个方块"
            };
            return true;
        }

        var hpMatch = Regex.Match(name, @"^(?<value>[0-9.]+) hp$", RegexOptions.IgnoreCase);
        if (hpMatch.Success)
        {
            label = $"{hpMatch.Groups["value"].Value}hp";
            return true;
        }

        var secondsMatch = Regex.Match(name, @"^(?<value>[0-9.]+) seconds?$", RegexOptions.IgnoreCase);
        if (secondsMatch.Success)
        {
            label = $"{secondsMatch.Groups["value"].Value} 秒";
            return true;
        }

        var minutesMatch = Regex.Match(name, @"^(?<value>[0-9.]+) minutes?$", RegexOptions.IgnoreCase);
        if (minutesMatch.Success)
        {
            label = $"{minutesMatch.Groups["value"].Value} 分钟";
            return true;
        }

        if (name.Equals("1 hour", StringComparison.OrdinalIgnoreCase))
        {
            label = "1 小时";
            return true;
        }

        var timesMatch = Regex.Match(name, @"^(?<value>[0-9]+) times?$", RegexOptions.IgnoreCase);
        if (timesMatch.Success)
        {
            label = $"{timesMatch.Groups["value"].Value}次";
            return true;
        }

        if (name.Equals("One time", StringComparison.OrdinalIgnoreCase))
        {
            label = "1次";
            return true;
        }

        if (name.Equals("Infinite", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("infinite", StringComparison.OrdinalIgnoreCase))
        {
            label = "无限";
            return true;
        }

        var speedMatch = Regex.Match(name, @"^(?<label>Very fast|Fast|Slightly faster|Normal|Slightly slower|Slower|Much slower|Deadly|Very Strong|Strong|Weak|Very weak|Much longer|Longer|Slightly longer|Slightly shorter|Shorter|Much Shorter)\s*\((?<value>[^)]+)\)$", RegexOptions.IgnoreCase);
        if (speedMatch.Success)
        {
            var zh = speedMatch.Groups["label"].Value switch
            {
                "Very fast" => "很快",
                "Fast" => "较快",
                "Slightly faster" => "稍快",
                "Normal" => "正常",
                "Slightly slower" => "稍慢",
                "Slower" => "较慢",
                "Much slower" => "很慢",
                "Deadly" => "致命",
                "Very Strong" => "很强",
                "Strong" => "强力",
                "Weak" => "弱小",
                "Very weak" => "很弱",
                "Much longer" => "很长",
                "Longer" => "较长",
                "Slightly longer" => "稍长",
                "Slightly shorter" => "稍短",
                "Shorter" => "较短",
                "Much Shorter" => "很短",
                _ => speedMatch.Groups["label"].Value
            };
            label = $"{zh}（{speedMatch.Groups["value"].Value}）";
            return true;
        }

        return false;
    }

    private void RefreshConfigWorldRuleLabels()
    {
        foreach (var item in _configWorldRuleItems)
        {
            item.SetLanguage(
                _isChinese,
                BuildConfigRuleChoiceOptions(item.Definition, item.Value),
                ResolveConfigRuleLabelZh(item.Definition));
        }
    }

    private void UpdateConfigWorldGeneratedState()
    {
        var savePath = (ConfigSaveFileComboBox.SelectedItem as ConfigSaveFileItem)?.FullPath;
        if (string.IsNullOrWhiteSpace(savePath))
        {
            savePath = _configSaveFileLocation;
        }

        var generated = IsSaveWorldGenerated(savePath);
        ConfigWorldGeneratedNoticeTextBlock.IsVisible = generated;
        ConfigSeedTextBox.IsEnabled = !generated;
        ConfigPlayStyleComboBox.IsEnabled = !generated;
        ConfigWorldTypeComboBox.IsEnabled = !generated;
        ConfigWorldHeightNumericUpDown.IsEnabled = !generated;

        foreach (var rule in _configWorldRuleItems)
        {
            rule.CanEdit = !(generated && rule.IsOnlyDuringWorldCreate);
        }
    }

    private InstanceProfile? GetSelectedConfigProfile()
    {
        if (ConfigProfileComboBox.SelectedItem is not InstanceProfile selectedProfile)
        {
            return null;
        }

        return _profileService.GetProfileById(selectedProfile.Id) ?? selectedProfile;
    }

    private string ResolveConfigSavePath(InstanceProfile profile)
    {
        var savePath = (ConfigSaveFileComboBox.SelectedItem as ConfigSaveFileItem)?.FullPath;
        if (string.IsNullOrWhiteSpace(savePath))
        {
            savePath = _configSaveFileLocation;
        }

        if (string.IsNullOrWhiteSpace(savePath))
        {
            savePath = profile.ActiveSaveFile;
        }

        var saveRoot = profile.SaveDirectory;
        if (string.IsNullOrWhiteSpace(saveRoot))
        {
            saveRoot = Path.GetDirectoryName(_profileService.GetDefaultSaveFilePath(profile.Id)) ?? profile.DirectoryPath;
        }

        saveRoot = Path.GetFullPath(saveRoot);
        Directory.CreateDirectory(saveRoot);
        if (string.IsNullOrWhiteSpace(savePath))
        {
            savePath = Path.Combine(saveRoot, "default.vcdbs");
        }

        var fullPath = Path.GetFullPath(savePath.Trim());
        if (!IsSameOrChildPath(Path.GetDirectoryName(fullPath), saveRoot))
        {
            fullPath = Path.Combine(saveRoot, Path.GetFileName(fullPath));
        }

        return fullPath;
    }

    private void OpenLocalFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetConfigStatus(T($"打开文件失败：{ex.Message}", $"Failed to open file: {ex.Message}"));
        }
    }

    private static string GetConfigPath(InstanceProfile profile)
    {
        var configPath = Path.Combine(profile.DirectoryPath, "serverconfig.json");
        try
        {
            return Path.GetFullPath(configPath);
        }
        catch
        {
            return configPath;
        }
    }

    private string GetRobotSettingsPath()
    {
        return Path.Combine(GetWorkspaceRootForUi(), "qqbot", "vs2qq-settings.json");
    }

    private static string GetAuthSettingsPath(InstanceProfile profile)
    {
        var configPath = Path.Combine(profile.DirectoryPath, "ModConfig", "serverauth.json");
        try
        {
            return Path.GetFullPath(configPath);
        }
        catch
        {
            return configPath;
        }
    }

    private static string GetServerBridgeSettingsPath(InstanceProfile profile)
    {
        var configPath = Path.Combine(profile.DirectoryPath, "ModConfig", "launchergoserverbridge.json");
        try
        {
            return Path.GetFullPath(configPath);
        }
        catch
        {
            return configPath;
        }
    }

    private string GetWorkspaceRootForUi()
    {
        var root = _preferencesService.Load().WorkspaceRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Environment.GetEnvironmentVariable("LAUNCHERGO_WORKSPACE");
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LauncherGo");
        }

        try
        {
            return Path.GetFullPath(root);
        }
        catch
        {
            return root;
        }
    }

    private string FormatConfigLoadFailure(InstanceProfile profile, Exception exception)
    {
        var status = exception switch
        {
            FileNotFoundException => T("配置状态：缺失", "Config status: missing"),
            InvalidDataException => T("配置状态：解析失败", "Config status: parse failed"),
            JsonException => T("配置状态：解析失败", "Config status: parse failed"),
            IOException => T("配置状态：读取失败", "Config status: read failed"),
            _ => T("配置状态：加载失败", "Config status: load failed")
        };

        return status + Environment.NewLine +
               T($"原因：{exception.Message}", $"Reason: {exception.Message}");
    }

    private void SetConfigStatus(string message, bool notify = true)
    {
        ConfigStatusTextBlock.Text = message;
        if (notify)
        {
            ShowToast(message);
        }
    }

    private static string ReadConfigString(JsonNode? node, string defaultValue)
    {
        return ReadConfigFlexibleString(node) ?? defaultValue;
    }

    private static string? ReadConfigNullableString(JsonNode? node)
    {
        return node is null ? null : ReadConfigFlexibleString(node);
    }

    private static int ReadConfigInt(JsonNode? node, int defaultValue)
    {
        if (ReadConfigNullableInt(node) is { } value)
        {
            return value;
        }

        return defaultValue;
    }

    private static int? ReadConfigNullableInt(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node.GetValueKind() == JsonValueKind.Number &&
            node is JsonValue numericValue &&
            numericValue.TryGetValue<int>(out var numeric))
        {
            return numeric;
        }

        if (node.GetValueKind() == JsonValueKind.String &&
            int.TryParse(node.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
        {
            return numeric;
        }

        return null;
    }

    private static bool ReadConfigBool(JsonNode? node, bool defaultValue)
    {
        if (node is null)
        {
            return defaultValue;
        }

        if (node.GetValueKind() == JsonValueKind.True || node.GetValueKind() == JsonValueKind.False)
        {
            return node.GetValue<bool>();
        }

        if (node.GetValueKind() == JsonValueKind.String &&
            bool.TryParse(node.GetValue<string>(), out var parsed))
        {
            return parsed;
        }

        return defaultValue;
    }

    private static string? ReadConfigFlexibleString(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.String => node.GetValue<string>(),
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            JsonValueKind.Number => node.ToString(),
            _ => node.ToJsonString()
        };
    }

    private static string? ReadConfigRuleFallbackValue(string key, JsonObject root, JsonObject worldConfig)
    {
        return key switch
        {
            "worldWidth" => ReadConfigFlexibleString(root["MapSizeX"]) ?? ReadConfigFlexibleString(worldConfig["MapSizeX"]),
            "worldLength" => ReadConfigFlexibleString(root["MapSizeZ"]) ?? ReadConfigFlexibleString(worldConfig["MapSizeZ"]),
            "colorAccurateWorldmap" => ReadConfigFlexibleString(worldConfig["colorAccurateWorldmap"]),
            _ => null
        };
    }

    private static string ResolveCurrentConfigSaveFilePath(InstanceProfile profile)
    {
        var activeSaveFile = NormalizeFullPath(profile.ActiveSaveFile);
        var saveRoot = NormalizeFullPath(profile.SaveDirectory);
        if (!string.IsNullOrWhiteSpace(activeSaveFile) &&
            IsSameOrChildPath(activeSaveFile, saveRoot))
        {
            return activeSaveFile;
        }

        if (!string.IsNullOrWhiteSpace(saveRoot))
        {
            return Path.Combine(saveRoot, "default.vcdbs");
        }

        return Path.Combine(profile.DirectoryPath, "Saves", "default.vcdbs");
    }

    private static void SetNumericValue(NumericUpDown control, int value)
    {
        control.Value = value;
    }

    private static void SetNumericValue(NumericUpDown control, double value)
    {
        control.Value = (decimal)value;
    }

    private static int GetNumericValue(NumericUpDown control, int fallback)
    {
        return control.Value.HasValue
            ? decimal.ToInt32(control.Value.Value)
            : fallback;
    }

    private static int TryParseInt(string? value, int fallback)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static int ParseClampedInt(string? value, int fallback, int min, int max)
    {
        return Math.Clamp(TryParseInt(value, fallback), min, max);
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string ResolveDefaultServerLanguage()
    {
        return CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-cn" : "en";
    }

    private static string NormalizeFullPath(string? path)
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

    private static bool IsSameOrChildPath(string? candidatePath, string? rootPath)
    {
        var candidate = NormalizeFullPath(candidatePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = NormalizeFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSaveWorldGenerated(string? savePath)
    {
        if (string.IsNullOrWhiteSpace(savePath))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(savePath.Trim());
            return File.Exists(fullPath) && new FileInfo(fullPath).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private void EnsureComboItem(ComboBox comboBox, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (comboBox.ItemsSource is IEnumerable<string> items &&
            items.Any(item => item.Equals(value, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var values = ConfigServerLanguageOptions
            .Append(value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        comboBox.ItemsSource = values;
    }

    private static void SelectConfigChoiceByValue(
        ComboBox comboBox,
        IEnumerable<ConfigChoiceOption> options,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            comboBox.SelectedIndex = -1;
            return;
        }

        comboBox.SelectedItem = options.FirstOrDefault(option =>
            option.Value.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private void EnsureConfigChoiceOptionExists(ObservableCollection<ConfigChoiceOption> options, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalized = value.Trim();
        if (options.Any(option => option.Value.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        options.Add(new ConfigChoiceOption(normalized, T($"自定义：{normalized}", $"Custom: {normalized}")));
    }

    private async void OnCreateProfileClick(object? sender, RoutedEventArgs e)
    {
        var version = CreateVersionComboBox.SelectedItem?.ToString() ?? string.Empty;
        var name = ProfileNameTextBox.Text?.Trim() ?? string.Empty;
        try
        {
            await Task.Run(() => _profileService.CreateProfile(name, version));
            ProfileNameTextBox.Text = string.Empty;
            RefreshProfiles();
            AppendConsoleLine(T($"[system] 已创建档案：{name}", $"[system] Profile created: {name}"));
        }
        catch (Exception ex)
        {
            var errorMessage = GetExceptionMessage(ex);
            AppendConsoleLine(T($"[system] 创建档案失败：{errorMessage}", $"[system] Failed to create profile: {errorMessage}"));
        }
    }

    private async void OnImportProfileClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = T("选择服务端档案目录", "Select server profile directory"),
            AllowMultiple = false
        });

        var path = TryGetLocalPath(folders.FirstOrDefault());
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var profile = _profileService.ImportProfile(path);
            RefreshProfiles();
            AppendConsoleLine(T($"[system] 已导入档案：{profile.Name}", $"[system] Profile imported: {profile.Name}"));
        }
        catch (Exception ex)
        {
            AppendConsoleLine(T($"[system] 导入档案失败：{ex.Message}", $"[system] Failed to import profile: {ex.Message}"));
        }
    }

    private void OnDeleteProfilesClick(object? sender, RoutedEventArgs e)
    {
        var selectedIds = ProfilesListBox.SelectedItems?
            .OfType<ProfileListItem>()
            .Select(item => item.Id)
            .ToArray() ?? [];
        if (selectedIds.Length == 0)
        {
            ShowToast(T("请先选择档案。", "Select profiles first."));
            return;
        }

        try
        {
            var count = _profileService.DeleteProfiles(selectedIds, deleteData: true);
            RefreshProfiles();
            AppendConsoleLine(T($"[system] 已删除 {count} 个档案。", $"[system] Deleted {count} profiles."));
        }
        catch (Exception ex)
        {
            AppendConsoleLine(T($"[system] 删除档案失败：{ex.Message}", $"[system] Failed to delete profiles: {ex.Message}"));
        }
    }

    private void OnRefreshProfilesClick(object? sender, RoutedEventArgs e)
    {
        RefreshProfiles();
    }

    private async void OnSaveProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingSaves)
        {
            return;
        }

        await RefreshSavesAsync();
    }

    private async void OnImportSaveClick(object? sender, RoutedEventArgs e)
    {
        if (SaveProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            AppendConsoleLine(T("[system] 导入存档前请先选择一个档案，不能选择全部。", "[system] Select one profile before importing a save; all profiles cannot be selected."));
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("选择存档文件", "Select save file"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Vintage Story Save")
                {
                    Patterns = ["*.vcdbs", "*.vcdbs.zst", "*.zst"]
                }
            ]
        });

        var path = TryGetLocalPath(files.FirstOrDefault());
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var target = await _saveService.ImportSaveAsync(profile, path);
            await RefreshSavesAsync();
            AppendConsoleLine(T($"[system] 已导入存档：{Path.GetFileName(target)}", $"[system] Save imported: {Path.GetFileName(target)}"));
        }
        catch (Exception ex)
        {
            AppendConsoleLine(T($"[system] 导入存档失败：{ex.Message}", $"[system] Failed to import save: {ex.Message}"));
        }
    }

    private async void OnDeleteSavesClick(object? sender, RoutedEventArgs e)
    {
        var selectedPaths = SavesListBox.SelectedItems?
            .OfType<SaveListItem>()
            .Select(item => item.FullPath)
            .ToArray() ?? [];
        if (selectedPaths.Length == 0)
        {
            ShowToast(T("请先选择存档。", "Select saves first."));
            return;
        }

        try
        {
            var count = SaveProfileComboBox.SelectedItem is InstanceProfile profile
                ? await _saveService.DeleteSavesAsync(profile, selectedPaths)
                : await _saveService.DeleteSavesAsync(selectedPaths);
            await RefreshSavesAsync();
            AppendConsoleLine(T($"[system] 已删除 {count} 个存档。", $"[system] Deleted {count} saves."));
        }
        catch (Exception ex)
        {
            AppendConsoleLine(T($"[system] 删除存档失败：{ex.Message}", $"[system] Failed to delete saves: {ex.Message}"));
        }
    }

    private async void OnRefreshSavesClick(object? sender, RoutedEventArgs e)
    {
        await RefreshSavesAsync();
    }

    private async void OnCreateSaveClick(object? sender, RoutedEventArgs e)
    {
        if (SaveProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            AppendConsoleLine(T("[system] 创建存档前请先选择一个档案，不能选择全部。", "[system] Select one profile before creating a save; all profiles cannot be selected."));
            return;
        }

        var name = NewSaveNameTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            AppendConsoleLine(T("[system] 请输入新存档名称。", "[system] Enter a name for the new save."));
            return;
        }

        try
        {
            await _saveService.CreateSaveAsync(profile, name);
            await RefreshSavesAsync();
            AppendConsoleLine(T($"[system] 已创建存档：{name}", $"[system] Save created: {name}"));
        }
        catch (Exception ex)
        {
            AppendConsoleLine(T($"[system] 创建存档失败：{ex.Message}", $"[system] Failed to create save: {ex.Message}"));
        }
    }

    private void OnOpenSaveDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string directoryPath } ||
            string.IsNullOrWhiteSpace(directoryPath) ||
            !Directory.Exists(directoryPath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = directoryPath, UseShellExecute = true });
    }

    private void OnOpenProfileDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string directoryPath } || string.IsNullOrWhiteSpace(directoryPath))
        {
            AppendConsoleLine(T("[system] 档案目录无效。", "[system] Invalid profile directory."));
            return;
        }

        if (!Directory.Exists(directoryPath))
        {
            AppendConsoleLine(T($"[system] 档案目录不存在：{directoryPath}", $"[system] Profile directory not found: {directoryPath}"));
            return;
        }

        OpenLocalFile(directoryPath);
    }

    private async void OnToggleDefaultSaveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SaveListItem item })
        {
            return;
        }

        var profile = _profileService.GetProfileById(item.ProfileId);
        if (profile is null)
        {
            AppendConsoleLine(T("[system] 锁定失败：未找到对应档案。", "[system] Lock failed: profile not found."));
            return;
        }

        try
        {
            await _saveService.SetActiveSaveAsync(profile, item.FullPath);
            var preferences = _preferencesService.Load();
            var ids = SplitProfileIds(preferences.DefaultLaunchProfileIds, preferences.DefaultLaunchProfileId);
            ids.Add(profile.Id);
            preferences.DefaultLaunchProfileIds = ids.ToList();
            preferences.DefaultLaunchProfileId = string.Join(';', ids);
            preferences.DefaultLaunchSaveFile = item.FullPath;
            _preferencesService.Save(preferences);
            await RefreshSavesAsync();
            RefreshLaunchOptions();
            AppendConsoleLine(T($"[system] 已锁定默认存档：{item.FileName}", $"[system] Default save locked: {item.FileName}"));
        }
        catch (Exception ex)
        {
            AppendConsoleLine(T($"[system] 锁定默认存档失败：{ex.Message}", $"[system] Failed to lock default save: {ex.Message}"));
        }
    }

    private bool TryGetLockedLaunchTarget(out InstanceProfile profile, out string lockedSavePath)
    {
        profile = null!;
        lockedSavePath = string.Empty;

        var preferences = _preferencesService.Load();
        var targetProfileId = SplitProfileIds(preferences.DefaultLaunchProfileIds, preferences.DefaultLaunchProfileId).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(targetProfileId))
        {
            return false;
        }

        var targetProfile = _profileService.GetProfileById(targetProfileId);
        if (targetProfile is null)
        {
            return false;
        }

        var targetSavePath = NormalizeFullPath(targetProfile.ActiveSaveFile);
        if (string.IsNullOrWhiteSpace(targetSavePath))
        {
            targetSavePath = NormalizeFullPath(preferences.DefaultLaunchSaveFile);
        }
        if (string.IsNullOrWhiteSpace(targetSavePath))
        {
            return false;
        }

        profile = targetProfile;
        lockedSavePath = targetSavePath;
        return true;
    }

    private async void OnImportServerPackageClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("选择服务端压缩包", "Select server package"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("ZIP")
                {
                    Patterns = ["*.zip"]
                }
            ]
        });

        var sourcePath = TryGetLocalPath(files.FirstOrDefault());
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        try
        {
            var preferences = _preferencesService.Load();
            var importedPath = await _serverPackageService.ImportServerPackageAsync(sourcePath, preferences.ServerDirectory);
            SetDownloadStatus(T($"导入完成：{Path.GetFileName(importedPath)}", $"Imported: {Path.GetFileName(importedPath)}"));
            RefreshProfiles();
            await RefreshDownloadVersionsAsync(forceReload: true);
        }
        catch (Exception ex)
        {
            SetDownloadStatus(T($"导入失败：{ex.Message}", $"Import failed: {ex.Message}"));
        }
    }

    private async void OnRefreshDownloadVersionsClick(object? sender, RoutedEventArgs e)
    {
        await RefreshDownloadVersionsAsync(forceReload: true);
        RefreshProfiles();
    }

    private async void OnDownloadVersionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DownloadVersionListItem item } || !item.CanDownload)
        {
            return;
        }

        var preferences = _preferencesService.Load();
        try
        {
            DownloadVersionsListBox.IsEnabled = false;
            await DownloadCatalogEntryAsync(item.Entry, preferences.ServerDirectory);
            SetDownloadStatus(T($"下载完成：{item.Entry.Version}", $"Download completed: {item.Entry.Version}"));
            RefreshProfiles();
            await RefreshDownloadVersionsAsync(forceReload: false);
        }
        catch (Exception ex)
        {
            SetDownloadStatus(T($"下载失败：{ex.Message}", $"Download failed: {ex.Message}"));
        }
        finally
        {
            DownloadVersionsListBox.IsEnabled = true;
        }
    }

    private async Task DownloadCatalogEntryAsync(ServerDownloadEntry entry, string serverDirectory)
    {
        foreach (var current in new[] { entry })
        {
            var targetPath = Path.Combine(serverDirectory, current.FileName);
            if (File.Exists(targetPath))
            {
                continue;
            }

            var progress = new Progress<double>(value =>
            {
                SetDownloadStatus(
                    T($"正在下载 {current.Version} {value:P0}", $"Downloading {current.Version} {value:P0}"),
                    notify: false);
            });
            await _serverPackageService.DownloadByCdnAsync(current.CdnUrl, targetPath, progress);
        }
    }

    private static string? TryGetLocalPath(IStorageItem? item)
    {
        if (item is null)
        {
            return null;
        }

        try
        {
            return item.TryGetLocalPath();
        }
        catch
        {
            return item.Path.LocalPath;
        }
    }

    private async Task BrowseFolderToTextBoxAsync(TextBox targetTextBox, string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        var path = TryGetLocalPath(folders.FirstOrDefault());
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        targetTextBox.Text = path;
        SaveServerSettings();
    }

    private static HttpClient CreateSharedHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LauncherGo/1.0");
        return client;
    }

    private static string? FindBundledContentPath(string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(Environment.CurrentDirectory, fileName),
            Path.Combine(Environment.CurrentDirectory, "LauncherGo", fileName),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "docs", fileName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", fileName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", fileName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", fileName))
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string GetAppLogDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LauncherGo",
            "logs");
    }

    private static async Task<Bitmap?> LoadAvatarImageAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        try
        {
            await using var source = await SharedHttpClient.GetStreamAsync(uri);
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer);
            buffer.Position = 0;
            return new Bitmap(buffer);
        }
        catch
        {
            return null;
        }
    }

    private static string GetSponsorApiUrl()
    {
        var overrideUrl = Environment.GetEnvironmentVariable("LAUNCHERGO_SPONSOR_API_URL");
        return string.IsNullOrWhiteSpace(overrideUrl)
            ? SponsorApiUrl
            : overrideUrl.Trim();
    }

    private static bool TryGetSponsorList(JsonElement root, out JsonElement listNode)
    {
        if (root.TryGetProperty("sponsors", out listNode) &&
            listNode.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        if (root.TryGetProperty("data", out var dataNode) &&
            dataNode.TryGetProperty("list", out listNode) &&
            listNode.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        listNode = default;
        return false;
    }

    private async Task<SettingsSponsorItem> BuildSponsorItemAsync(JsonElement sponsor)
    {
        var name = ReadFirstJsonString(sponsor, "name", "userName");
        var avatarUrl = ReadFirstJsonString(sponsor, "avatarUrl", "avatar", "avatar_url", "pic", "url");
        if (string.IsNullOrWhiteSpace(name) &&
            sponsor.TryGetProperty("user", out var userNode))
        {
            name = ReadJsonString(userNode, "name");
            avatarUrl = string.IsNullOrWhiteSpace(avatarUrl)
                ? ReadFirstJsonString(userNode, "avatarUrl", "avatar", "avatar_url", "pic", "url")
                : avatarUrl;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = ReadJsonString(sponsor, "user_id");
        }

        var amount = ReadFirstJsonString(sponsor, "amount", "all_sum_amount", "sum_amount");

        var plan = ReadJsonString(sponsor, "plan");
        if (sponsor.TryGetProperty("current_plan", out var currentPlanNode))
        {
            plan = string.IsNullOrWhiteSpace(plan)
                ? ReadJsonString(currentPlanNode, "name")
                : plan;
        }

        if (string.IsNullOrWhiteSpace(plan) &&
            sponsor.TryGetProperty("sponsor_plans", out var plansNode) &&
            plansNode.ValueKind == JsonValueKind.Array)
        {
            var firstPlan = plansNode.EnumerateArray().FirstOrDefault();
            if (firstPlan.ValueKind == JsonValueKind.Object)
            {
                plan = ReadJsonString(firstPlan, "name");
            }
        }

        return new SettingsSponsorItem
        {
            Name = string.IsNullOrWhiteSpace(name) ? T("匿名赞助者", "Anonymous Sponsor") : name,
            AvatarImage = await LoadAvatarImageAsync(avatarUrl),
            AmountText = string.IsNullOrWhiteSpace(amount)
                ? T("累计赞助金额未知", "Total sponsored amount unknown")
                : T($"累计赞助 {amount} 元", $"Total sponsored CNY {amount}"),
            PlanText = string.IsNullOrWhiteSpace(plan)
                ? T("未识别赞助方案", "Plan not available")
                : plan
        };
    }

    private static string ReadFirstJsonString(JsonElement node, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = ReadJsonString(node, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string ReadJsonString(JsonElement node, string propertyName)
    {
        if (!node.TryGetProperty(propertyName, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => string.Empty
        };
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays}d {duration:hh\\:mm\\:ss}";
        }

        return duration.ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture);
    }

    private static void PushNextSample(List<double> samples, double value, int maxCount = RealtimeRangeSeconds)
    {
        if (samples.Count >= maxCount)
        {
            samples.RemoveAt(0);
        }

        samples.Add(value);
    }

    private static double BytesToMb(long bytes)
    {
        return bytes <= 0 ? 0 : bytes / 1024.0 / 1024.0;
    }

    private static string FormatDataSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024d / 1024d:F2} GB";
        }

        if (bytes >= 1024L * 1024)
        {
            return $"{bytes / 1024d / 1024d:F1} MB";
        }

        if (bytes >= 1024)
        {
            return $"{bytes / 1024d:F1} KB";
        }

        return $"{bytes} B";
    }

    private static long? ResolveProcessMemory(int? processId)
    {
        if (!processId.HasValue || processId.Value <= 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(processId.Value);
            return process.WorkingSet64;
        }
        catch
        {
            return null;
        }
    }

    private static double NiceCeiling(double value)
    {
        if (value <= 0)
        {
            return 1;
        }

        var exponent = Math.Floor(Math.Log10(value));
        var magnitude = Math.Pow(10, exponent);
        var normalized = value / magnitude;
        var nice = normalized switch
        {
            <= 1 => 1,
            <= 2 => 2,
            <= 5 => 5,
            _ => 10
        };

        return nice * magnitude;
    }

    [GeneratedRegex(@"\[(?:Talk|Chat)\]|<[^>]+>\s*.+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConsoleChatLineRegex();

    [GeneratedRegex(@"\[(?:Server\s+)?Notification\]|服务器通知|message to all in group", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConsoleNotificationLineRegex();

    [GeneratedRegex(@"joins\.|joined\.|left\.|leaves\.|加入了服务器|离开了服务器|进入服务器|离开服务器|加入游戏|离开游戏", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConsoleJoinLeaveLineRegex();

    [GeneratedRegex(@"died|has died|death message|death reason|fell from a high place|fell to (?:his|her|their) death|plummeted|已死亡|死亡消息|死因|摔死|从高处坠落而亡|坠落身亡", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConsoleDeathLineRegex();

    [GeneratedRegex(@"kick(?:ed|ing)?|ban(?:ned|ning)?|whitelist|auth(?:entication)?.*(?:failed|failure|required|denied)|login.*failed|rejected|denied|白名单|认证失败|登录失败|踢出|封禁", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConsoleAdminLineRegex();

    [GeneratedRegex(@"start(?:ing|ed)?|stop(?:ping|ped)?|shut(?:ting)?\s*down|shutdown|crash(?:ed)?|sav(?:e|ed|ing)|backup|正在保存|保存完成|备份完成|备份失败", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConsoleLifecycleLineRegex();

    [GeneratedRegex(@"temporal|rift|storm|boss|特殊事件|时空|裂隙|风暴|首领", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConsoleSpecialEventLineRegex();

    [GeneratedRegex(@"joins\.|left\.|leaves\.|died|死亡|摔死|killed|离开|进入|加入|玩家", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlayerEventHintRegex();

    private enum MainTab
    {
        Home,
        Monitor,
        Console,
        InstanceManage,
        Settings,
        Connection
    }

    private enum HomeMetric
    {
        Server,
        Robot,
        Players,
        Network
    }

    private enum InstanceManageTab
    {
        Profiles,
        Config,
        Saves,
        Automation,
        Logs,
        Mods,
        DownloadVersions,
        ServerBridge,
        ServerMap
    }

    private enum SettingsTab
    {
        Server,
        Appearance,
        Network,
        Advanced,
        About,
        Sponsors,
        Contributors
    }

    private enum ConnectionTab
    {
        Frp,
        EasyTier,
        Robot,
        Discord,
        Gateway,
        Auth,
        ServerMap
    }

    private enum ConnectionProcessKind
    {
        Frp,
        ThirdPartyFrpc
    }

    private enum ToastKind
    {
        Neutral,
        Success,
        Error
    }

    public sealed class GatewayBackendRuntimeItem(string backendId) : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _address = string.Empty;
        private string _statusText = string.Empty;
        private string _activeConnectionsText = "0";
        private string _weightText = "0";
        private string _trafficText = string.Empty;
        private string _statisticsText = string.Empty;
        private TcpGatewayBackendRuntimeStatus _runtimeStatus = new();

        public string BackendId { get; } = backendId;

        public string Name
        {
            get => _name;
            private set => SetField(ref _name, value);
        }

        public string Address
        {
            get => _address;
            private set => SetField(ref _address, value);
        }

        public string StatusText
        {
            get => _statusText;
            private set => SetField(ref _statusText, value);
        }

        public string ActiveConnectionsText
        {
            get => _activeConnectionsText;
            private set => SetField(ref _activeConnectionsText, value);
        }

        public string WeightText
        {
            get => _weightText;
            private set => SetField(ref _weightText, value);
        }

        public string TrafficText
        {
            get => _trafficText;
            private set => SetField(ref _trafficText, value);
        }

        public string StatisticsText
        {
            get => _statisticsText;
            private set => SetField(ref _statisticsText, value);
        }

        public TcpGatewayBackendRuntimeStatus RuntimeStatus
        {
            get => _runtimeStatus;
            private set => SetField(ref _runtimeStatus, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Update(
            string name,
            string address,
            string statusText,
            string activeConnectionsText,
            string weightText,
            string trafficText,
            string statisticsText,
            TcpGatewayBackendRuntimeStatus runtimeStatus)
        {
            Name = name;
            Address = address;
            StatusText = statusText;
            ActiveConnectionsText = activeConnectionsText;
            WeightText = weightText;
            TrafficText = trafficText;
            StatisticsText = statisticsText;
            RuntimeStatus = runtimeStatus;
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
    }

    public sealed class ProfileListItem
    {
        public required string Id { get; init; }

        public required string Name { get; init; }

        public required string Version { get; init; }

        public required string DirectoryPath { get; init; }

        public required string ActiveSaveFile { get; init; }

        public static ProfileListItem FromProfile(InstanceProfile profile)
        {
            return new ProfileListItem
            {
                Id = profile.Id,
                Name = profile.Name,
                Version = profile.Version,
                DirectoryPath = profile.DirectoryPath,
                ActiveSaveFile = profile.ActiveSaveFile
            };
        }
    }

    public sealed class ProfileLogListItem
    {
        public required string ProfileId { get; init; }

        public required string ProfileName { get; init; }

        public required string LogDirectoryPath { get; init; }

        public static ProfileLogListItem FromProfile(InstanceProfile profile)
        {
            return new ProfileLogListItem
            {
                ProfileId = profile.Id,
                ProfileName = string.IsNullOrWhiteSpace(profile.Name) ? profile.Id : profile.Name,
                LogDirectoryPath = Path.Combine(profile.DirectoryPath, "Logs")
            };
        }
    }

    public sealed class SaveListItem
    {
        private const string UnlockedIconPath =
            "M528 320C528 205.1 434.9 112 320 112C205.1 112 112 205.1 112 320C112 434.9 205.1 528 320 528C434.9 528 528 434.9 528 320zM64 320C64 178.6 178.6 64 320 64C461.4 64 576 178.6 576 320C576 461.4 461.4 576 320 576C178.6 576 64 461.4 64 320z";
        private const string LockedIconPath =
            "M320 576C178.6 576 64 461.4 64 320C64 178.6 178.6 64 320 64C461.4 64 576 178.6 576 320C576 461.4 461.4 576 320 576zM438 209.7C427.3 201.9 412.3 204.3 404.5 215L285.1 379.2L233 327.1C223.6 317.7 208.4 317.7 199.1 327.1C189.8 336.5 189.7 351.7 199.1 361L271.1 433C276.1 438 282.9 440.5 289.9 440C296.9 439.5 303.3 435.9 307.4 430.2L443.3 243.2C451.1 232.5 448.7 217.5 438 209.7z";

        public required string ProfileId { get; init; }

        public required string FullPath { get; init; }

        public required string FileName { get; init; }

        public required string ProfileName { get; init; }

        public required string Description { get; init; }

        public required string DirectoryPath { get; init; }

        public required string SizeText { get; init; }

        public required string LastWriteText { get; init; }

        public bool IsLocked { get; init; }

        public string LockActionText { get; init; } = string.Empty;

        public string LockIconData => IsLocked ? LockedIconPath : UnlockedIconPath;

        public string LockIconBrush => IsLocked ? "#6B8E23" : "#8A8A8A";

        public static SaveListItem FromSave(
            SaveFileEntry save,
            bool isLocked,
            string lockedActionText,
            string unlockedActionText)
        {
            var directoryPath = Path.GetDirectoryName(save.FullPath) ?? string.Empty;
            var sizeText = FormatFileSize(save.SizeBytes);
            var lastWriteText = save.LastWriteTimeUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            return new SaveListItem
            {
                ProfileId = save.ProfileId,
                FullPath = save.FullPath,
                FileName = save.FileName,
                ProfileName = save.ProfileName,
                Description = $"{sizeText}  {lastWriteText}  {save.FullPath}",
                DirectoryPath = directoryPath,
                SizeText = sizeText,
                LastWriteText = lastWriteText,
                IsLocked = isLocked,
                LockActionText = isLocked ? lockedActionText : unlockedActionText
            };
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024)
            {
                return $"{bytes / 1024d / 1024d / 1024d:F2} GB";
            }

            if (bytes >= 1024L * 1024)
            {
                return $"{bytes / 1024d / 1024d:F1} MB";
            }

            if (bytes >= 1024)
            {
                return $"{bytes / 1024d:F1} KB";
            }

            return $"{bytes} B";
        }
    }

    public sealed class DownloadVersionListItem(
        ServerDownloadEntry entry,
        string displayText,
        bool isDownloaded,
        string downloadedText,
        string actionText)
    {
        public ServerDownloadEntry Entry { get; } = entry;

        public string DisplayText { get; } = displayText;

        public bool IsDownloaded { get; } = isDownloaded;

        public bool CanDownload => !IsDownloaded;

        public string DownloadedText { get; } = downloadedText;

        public string ActionText { get; } = actionText;
    }

    public sealed class ProfileConfigListItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public string ProfileId { get; init; } = string.Empty;

        public string ProfileName { get; init; } = string.Empty;

        public string ConfigPath { get; init; } = string.Empty;

        public string ModifiedText { get; init; } = string.Empty;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public static ProfileConfigListItem FromPath(InstanceProfile profile, string path)
        {
            var modifiedText = "-";
            try
            {
                if (File.Exists(path))
                {
                    modifiedText = File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                }
            }
            catch
            {
                modifiedText = "-";
            }

            return new ProfileConfigListItem
            {
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                ConfigPath = path,
                ModifiedText = modifiedText
            };
        }
    }

    public sealed class RobotTeleportPointItem : INotifyPropertyChanged
    {
        private string _name;
        private decimal _x;
        private decimal _y;
        private decimal _z;

        public RobotTeleportPointItem(string name, double x, double y, double z)
        {
            _name = name ?? string.Empty;
            _x = (decimal)x;
            _y = (decimal)y;
            _z = (decimal)z;
        }

        public string Name
        {
            get => _name;
            set
            {
                if (_name == value) return;
                _name = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        public decimal X
        {
            get => _x;
            set
            {
                if (_x == value) return;
                _x = value;
                OnPropertyChanged();
            }
        }

        public decimal Y
        {
            get => _y;
            set
            {
                if (_y == value) return;
                _y = value;
                OnPropertyChanged();
            }
        }

        public decimal Z
        {
            get => _z;
            set
            {
                if (_z == value) return;
                _z = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed class RobotCustomCommandItem : INotifyPropertyChanged
    {
        private string _command;
        private string _content;
        private RobotCustomMessageType _messageType;
        private ConfigChoiceOption? _selectedType;
        private bool _isChinese;

        public RobotCustomCommandItem(
            string command,
            RobotCustomMessageType messageType,
            string content,
            bool isChinese)
        {
            _command = command;
            _messageType = messageType;
            _content = content;
            SetLanguage(isChinese);
        }

        public ObservableCollection<ConfigChoiceOption> TypeOptions { get; } = [];

        public string Command
        {
            get => _command;
            set
            {
                if (_command == value)
                {
                    return;
                }

                _command = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        public string Content
        {
            get => _content;
            set
            {
                if (_content == value)
                {
                    return;
                }

                _content = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ImagePathButtonText));
            }
        }

        public bool IsText => _messageType == RobotCustomMessageType.Text;

        public bool IsImage => _messageType == RobotCustomMessageType.Image;

        public string ImagePathButtonText => string.IsNullOrWhiteSpace(_content)
            ? (_isChinese ? "选择图片路径" : "Select image path")
            : _content;

        public ConfigChoiceOption? SelectedType
        {
            get => _selectedType;
            set
            {
                if (ReferenceEquals(_selectedType, value))
                {
                    return;
                }

                _selectedType = value;
                if (value is not null && Enum.TryParse<RobotCustomMessageType>(value.Value, out var parsed))
                {
                    _messageType = parsed;
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(MessageType));
                OnPropertyChanged(nameof(IsText));
                OnPropertyChanged(nameof(IsImage));
                OnPropertyChanged(nameof(ImagePathButtonText));
            }
        }

        public RobotCustomMessageType MessageType => _messageType;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void SetLanguage(bool isChinese)
        {
            _isChinese = isChinese;
            var selectedValue = _selectedType?.Value ?? _messageType.ToString();
            TypeOptions.Clear();
            TypeOptions.Add(new ConfigChoiceOption(RobotCustomMessageType.Text.ToString(), isChinese ? "文本" : "Text"));
            TypeOptions.Add(new ConfigChoiceOption(RobotCustomMessageType.Image.ToString(), isChinese ? "图片" : "Image"));
            _selectedType = TypeOptions.FirstOrDefault(option =>
                option.Value.Equals(selectedValue, StringComparison.OrdinalIgnoreCase)) ?? TypeOptions[0];
            if (Enum.TryParse<RobotCustomMessageType>(_selectedType.Value, out var parsed))
            {
                _messageType = parsed;
            }

            OnPropertyChanged(nameof(TypeOptions));
            OnPropertyChanged(nameof(SelectedType));
            OnPropertyChanged(nameof(MessageType));
            OnPropertyChanged(nameof(IsText));
            OnPropertyChanged(nameof(IsImage));
            OnPropertyChanged(nameof(ImagePathButtonText));
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed class RobotProfileBindingItem : INotifyPropertyChanged
    {
        private string _groupId = string.Empty;
        private string _superUserId = string.Empty;
        private InstanceProfile? _selectedProfile;
        private ObservableCollection<InstanceProfile> _profileOptions;

        public RobotProfileBindingItem(
            ObservableCollection<InstanceProfile> profileOptions,
            string profileId,
            string groupId,
            string superUserId)
        {
            _profileOptions = profileOptions;
            ProfileId = profileId;
            _groupId = groupId;
            _superUserId = superUserId;
            _selectedProfile = profileOptions.FirstOrDefault(profile =>
                profile.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
        }

        public string ProfileId { get; private set; }

        public ObservableCollection<InstanceProfile> ProfileOptions
        {
            get => _profileOptions;
            set
            {
                if (ReferenceEquals(_profileOptions, value))
                {
                    return;
                }

                _profileOptions = value;
                OnPropertyChanged();
            }
        }

        public InstanceProfile? SelectedProfile
        {
            get => _selectedProfile;
            set
            {
                if (ReferenceEquals(_selectedProfile, value))
                {
                    return;
                }

                _selectedProfile = value;
                ProfileId = value?.Id ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProfileId));
            }
        }

        public string GroupId
        {
            get => _groupId;
            set
            {
                if (_groupId == value)
                {
                    return;
                }

                _groupId = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        public string SuperUserId
        {
            get => _superUserId;
            set
            {
                if (_superUserId == value)
                {
                    return;
                }

                _superUserId = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed class DiscordProfileBindingItem : INotifyPropertyChanged
    {
        private string _profileId;
        private string _guildId;
        private string _channelId;
        private InstanceProfile? _selectedProfile;

        public DiscordProfileBindingItem(
            IReadOnlyList<InstanceProfile> profileOptions,
            string profileId,
            string guildId,
            string channelId)
        {
            ProfileOptions = new ObservableCollection<InstanceProfile>(profileOptions);
            _profileId = profileId ?? string.Empty;
            _guildId = guildId ?? string.Empty;
            _channelId = channelId ?? string.Empty;
            _selectedProfile = ProfileOptions.FirstOrDefault(profile => profile.Id.Equals(_profileId, StringComparison.OrdinalIgnoreCase));
        }

        public ObservableCollection<InstanceProfile> ProfileOptions { get; }

        public string ProfileId => _profileId;

        public InstanceProfile? SelectedProfile
        {
            get => _selectedProfile;
            set
            {
                if (ReferenceEquals(_selectedProfile, value)) return;
                _selectedProfile = value;
                _profileId = value?.Id ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProfileId));
            }
        }

        public string GuildId
        {
            get => _guildId;
            set { if (_guildId == value) return; _guildId = value ?? string.Empty; OnPropertyChanged(); }
        }

        public string ChannelId
        {
            get => _channelId;
            set { if (_channelId == value) return; _channelId = value ?? string.Empty; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class DiscordCustomCommandItem : INotifyPropertyChanged
    {
        private string _command;
        private string _content;
        private RobotCustomMessageType _messageType;
        private ConfigChoiceOption? _selectedType;
        private bool _isChinese;

        public DiscordCustomCommandItem(string command, RobotCustomMessageType messageType, string content, bool isChinese)
        {
            _command = command ?? string.Empty;
            _messageType = messageType;
            _content = content ?? string.Empty;
            SetLanguage(isChinese);
        }

        public ObservableCollection<ConfigChoiceOption> TypeOptions { get; } = [];
        public string Command { get => _command; set { if (_command == value) return; _command = value ?? string.Empty; OnPropertyChanged(); } }
        public string Content { get => _content; set { if (_content == value) return; _content = value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(ImagePathButtonText)); } }
        public bool IsText => _messageType == RobotCustomMessageType.Text;
        public bool IsImage => _messageType == RobotCustomMessageType.Image;
        public string ImagePathButtonText => string.IsNullOrWhiteSpace(_content) ? (_isChinese ? "选择图片路径" : "Select image path") : _content;
        public RobotCustomMessageType MessageType => _messageType;

        public ConfigChoiceOption? SelectedType
        {
            get => _selectedType;
            set
            {
                if (ReferenceEquals(_selectedType, value)) return;
                _selectedType = value;
                if (value is not null && Enum.TryParse<RobotCustomMessageType>(value.Value, out var parsed)) _messageType = parsed;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MessageType));
                OnPropertyChanged(nameof(IsText));
                OnPropertyChanged(nameof(IsImage));
                OnPropertyChanged(nameof(ImagePathButtonText));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void SetLanguage(bool isChinese)
        {
            _isChinese = isChinese;
            TypeOptions.Clear();
            TypeOptions.Add(new ConfigChoiceOption(RobotCustomMessageType.Text.ToString(), _isChinese ? "文本" : "Text"));
            TypeOptions.Add(new ConfigChoiceOption(RobotCustomMessageType.Image.ToString(), _isChinese ? "图片" : "Image"));
            _selectedType = TypeOptions.FirstOrDefault(option => option.Value.Equals(_messageType.ToString(), StringComparison.OrdinalIgnoreCase)) ?? TypeOptions[0];
            OnPropertyChanged(nameof(TypeOptions));
            OnPropertyChanged(nameof(SelectedType));
            OnPropertyChanged(nameof(MessageType));
            OnPropertyChanged(nameof(IsText));
            OnPropertyChanged(nameof(IsImage));
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class SettingsContributorItem
    {
        public required string Login { get; init; }

        public required string HtmlUrl { get; init; }

        public Bitmap? AvatarImage { get; init; }

        public bool HasAvatar => AvatarImage is not null;

        public bool HasNoAvatar => AvatarImage is null;

        public string Initial => string.IsNullOrWhiteSpace(Login) ? "?" : Login.Trim()[..1].ToUpperInvariant();

        public required string ContributionsText { get; init; }
    }

    public sealed class SettingsSponsorItem
    {
        public required string Name { get; init; }

        public Bitmap? AvatarImage { get; init; }

        public bool HasAvatar => AvatarImage is not null;

        public bool HasNoAvatar => AvatarImage is null;

        public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[..1].ToUpperInvariant();

        public required string AmountText { get; init; }

        public required string PlanText { get; init; }
    }

    public sealed class ConfigChoiceOption(string value, string label)
    {
        public string Value { get; } = value;

        public string Label { get; } = label;

        public override string ToString() => Label;
    }

    public sealed class AutomationActionWindowItem : INotifyPropertyChanged
    {
        private AutomationScheduleMode _scheduleMode = AutomationScheduleMode.Weekly;
        private AutomationActionType _action = AutomationActionType.Start;
        private string _startDayOfWeek = "1";
        private string _endDayOfWeek = "7";
        private string _startDate = string.Empty;
        private string _endDate = string.Empty;
        private string _startTime = "08:00";
        private string _endTime = "23:00";
        private bool _enabled = true;

        public AutomationActionWindowItem(bool isChinese = true)
        {
            ScheduleModeOptions = new ObservableCollection<ConfigChoiceOption>
            {
                new(AutomationScheduleMode.Weekly.ToString(), isChinese ? "每周" : "Weekly"),
                new(AutomationScheduleMode.DateRange.ToString(), isChinese ? "日期范围" : "Date range")
            };
            RebuildDayOfWeekOptions(isChinese);
            ActionOptions = new ObservableCollection<ConfigChoiceOption>
            {
                new(AutomationActionType.Start.ToString(), isChinese ? "启动" : "Start"),
                new(AutomationActionType.Stop.ToString(), isChinese ? "停止" : "Stop")
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ObservableCollection<ConfigChoiceOption> ScheduleModeOptions { get; }

        public ObservableCollection<ConfigChoiceOption> DayOfWeekOptions { get; } = [];

        public ObservableCollection<ConfigChoiceOption> ActionOptions { get; }

        public void SetLanguage(bool isChinese)
        {
            var selectedScheduleMode = _scheduleMode;
            var selectedAction = _action;
            ScheduleModeOptions.Clear();
            ScheduleModeOptions.Add(new ConfigChoiceOption(
                AutomationScheduleMode.Weekly.ToString(),
                isChinese ? "每周" : "Weekly"));
            ScheduleModeOptions.Add(new ConfigChoiceOption(
                AutomationScheduleMode.DateRange.ToString(),
                isChinese ? "日期范围" : "Date range"));
            RebuildDayOfWeekOptions(isChinese);
            ActionOptions.Clear();
            ActionOptions.Add(new ConfigChoiceOption(
                AutomationActionType.Start.ToString(),
                isChinese ? "启动" : "Start"));
            ActionOptions.Add(new ConfigChoiceOption(
                AutomationActionType.Stop.ToString(),
                isChinese ? "停止" : "Stop"));
            _scheduleMode = selectedScheduleMode;
            _action = selectedAction;
            OnPropertyChanged(nameof(ScheduleModeOptions));
            OnPropertyChanged(nameof(DayOfWeekOptions));
            OnPropertyChanged(nameof(ActionOptions));
            OnPropertyChanged(nameof(SelectedScheduleMode));
            OnPropertyChanged(nameof(SelectedStartDayOfWeek));
            OnPropertyChanged(nameof(SelectedEndDayOfWeek));
            OnPropertyChanged(nameof(SelectedAction));
        }

        public bool Enabled
        {
            get => _enabled;
            set => SetField(ref _enabled, value);
        }

        public string StartDayOfWeek
        {
            get => _startDayOfWeek;
            set => SetField(ref _startDayOfWeek, value);
        }

        public string EndDayOfWeek
        {
            get => _endDayOfWeek;
            set => SetField(ref _endDayOfWeek, value);
        }

        public string StartDate
        {
            get => _startDate;
            set
            {
                if (SetField(ref _startDate, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(StartDateValue));
                }
            }
        }

        public string EndDate
        {
            get => _endDate;
            set
            {
                if (SetField(ref _endDate, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(EndDateValue));
                }
            }
        }

        public DateTime? StartDateValue
        {
            get => TryParseDateValue(_startDate);
            set => SetDateValue(ref _startDate, value, nameof(StartDateValue), nameof(StartDate));
        }

        public DateTime? EndDateValue
        {
            get => TryParseDateValue(_endDate);
            set => SetDateValue(ref _endDate, value, nameof(EndDateValue), nameof(EndDate));
        }

        public bool IsWeekly => _scheduleMode == AutomationScheduleMode.Weekly;

        public bool IsDateRange => _scheduleMode == AutomationScheduleMode.DateRange;

        public string StartTime
        {
            get => _startTime;
            set => SetField(ref _startTime, value);
        }

        public string EndTime
        {
            get => _endTime;
            set => SetField(ref _endTime, value);
        }

        public ConfigChoiceOption SelectedScheduleMode
        {
            get => ScheduleModeOptions.First(option => option.Value.Equals(_scheduleMode.ToString(), StringComparison.OrdinalIgnoreCase));
            set
            {
                if (value is null) return;
                if (Enum.TryParse(value.Value, true, out AutomationScheduleMode mode))
                {
                    if (_scheduleMode == mode)
                    {
                        return;
                    }

                    _scheduleMode = mode;
                    OnPropertyChanged(nameof(SelectedScheduleMode));
                    OnPropertyChanged(nameof(IsWeekly));
                    OnPropertyChanged(nameof(IsDateRange));
                }
            }
        }

        public ConfigChoiceOption SelectedStartDayOfWeek
        {
            get => DayOfWeekOptions.First(option => option.Value == NormalizeWeekDayValue(_startDayOfWeek, 1));
            set => SetDayOfWeek(ref _startDayOfWeek, value, nameof(SelectedStartDayOfWeek), 1);
        }

        public ConfigChoiceOption SelectedEndDayOfWeek
        {
            get => DayOfWeekOptions.First(option => option.Value == NormalizeWeekDayValue(_endDayOfWeek, 7));
            set => SetDayOfWeek(ref _endDayOfWeek, value, nameof(SelectedEndDayOfWeek), 7);
        }

        public ConfigChoiceOption SelectedAction
        {
            get => ActionOptions.First(option => option.Value.Equals(_action.ToString(), StringComparison.OrdinalIgnoreCase));
            set
            {
                if (value is null) return;
                if (Enum.TryParse(value.Value, true, out AutomationActionType action))
                {
                    _action = action;
                    OnPropertyChanged(nameof(SelectedAction));
                }
            }
        }

        public AutomationActionWindow ToModel()
        {
            return new AutomationActionWindow
            {
                ScheduleMode = _scheduleMode,
                StartDayOfWeek = TryParseInt(_startDayOfWeek, 1),
                EndDayOfWeek = TryParseInt(_endDayOfWeek, 7),
                StartDate = _startDate?.Trim() ?? string.Empty,
                EndDate = _endDate?.Trim() ?? string.Empty,
                StartTime = _startTime?.Trim() ?? string.Empty,
                EndTime = _endTime?.Trim() ?? string.Empty,
                Action = _action,
                Enabled = _enabled
            };
        }

        public static AutomationActionWindowItem FromModel(AutomationActionWindow model, bool isChinese)
        {
            return new AutomationActionWindowItem(isChinese)
            {
                Enabled = model.Enabled,
                StartDayOfWeek = model.StartDayOfWeek.ToString(CultureInfo.InvariantCulture),
                EndDayOfWeek = model.EndDayOfWeek.ToString(CultureInfo.InvariantCulture),
                StartDate = model.StartDate,
                EndDate = model.EndDate,
                StartTime = model.StartTime,
                EndTime = model.EndTime,
                _scheduleMode = model.ScheduleMode,
                _action = model.Action
            };
        }

        private void RebuildDayOfWeekOptions(bool isChinese)
        {
            DayOfWeekOptions.Clear();
            var labels = isChinese
                ? new[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" }
                : new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
            for (var day = 1; day <= 7; day++)
            {
                DayOfWeekOptions.Add(new ConfigChoiceOption(day.ToString(CultureInfo.InvariantCulture), labels[day - 1]));
            }
        }

        private void SetDayOfWeek(
            ref string field,
            ConfigChoiceOption? option,
            string propertyName,
            int fallback)
        {
            if (option is null)
            {
                return;
            }

            var next = NormalizeWeekDayValue(option.Value, fallback);
            if (field == next)
            {
                return;
            }

            field = next;
            OnPropertyChanged(propertyName);
        }

        private static string NormalizeWeekDayValue(string? value, int fallback)
        {
            var parsed = TryParseInt(value, fallback);
            return Math.Clamp(parsed, 1, 7).ToString(CultureInfo.InvariantCulture);
        }

        private static DateTime? TryParseDateValue(string? value)
        {
            var text = value?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) &&
                !DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out date) &&
                !DateOnly.TryParse(text, out date))
            {
                return null;
            }

            return new DateTime(date.Year, date.Month, date.Day);
        }

        private bool SetDateValue(ref string field, DateTime? value, string datePropertyName, string textPropertyName)
        {
            var next = value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
            if (EqualityComparer<string>.Default.Equals(field, next))
            {
                return false;
            }

            field = next;
            OnPropertyChanged(datePropertyName);
            OnPropertyChanged(textPropertyName);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    public sealed class AutomationBackupScheduleItem : INotifyPropertyChanged
    {
        private string _id = Guid.NewGuid().ToString("N");
        private bool _isChinese;
        private bool _enabled = true;
        private BackupScheduleType _type = BackupScheduleType.Daily;
        private string _dayOfMonth = "1";
        private int _dayOfWeek = 1;
        private string _time = "03:00";
        private string _minuteOfHour = "0";
        private string _interval = "1";
        private string _anchorDate = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        public AutomationBackupScheduleItem(bool isChinese = true)
        {
            _isChinese = isChinese;
            RebuildOptions();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ObservableCollection<ConfigChoiceOption> ScheduleTypeOptions { get; } = [];

        public ObservableCollection<ConfigChoiceOption> DayOfWeekOptions { get; } = [];

        public ObservableCollection<string> PreviewExecutionItems { get; } = [];

        public bool Enabled
        {
            get => _enabled;
            set => SetField(ref _enabled, value);
        }

        public string DayOfMonth
        {
            get => _dayOfMonth;
            set => SetField(ref _dayOfMonth, value ?? string.Empty);
        }

        public string Time
        {
            get => _time;
            set => SetField(ref _time, value ?? string.Empty);
        }

        public string MinuteOfHour
        {
            get => _minuteOfHour;
            set => SetField(ref _minuteOfHour, value ?? string.Empty);
        }

        public string Interval
        {
            get => _interval;
            set => SetField(ref _interval, value ?? string.Empty);
        }

        public ConfigChoiceOption SelectedScheduleType
        {
            get => ScheduleTypeOptions.First(option =>
                option.Value.Equals(_type.ToString(), StringComparison.OrdinalIgnoreCase));
            set
            {
                if (value is null || !Enum.TryParse(value.Value, true, out BackupScheduleType type) || _type == type)
                    return;

                _type = type;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsMonthly));
                OnPropertyChanged(nameof(IsWeekly));
                OnPropertyChanged(nameof(IsHourly));
                OnPropertyChanged(nameof(IsInterval));
                OnPropertyChanged(nameof(ShowsTime));
                OnPropertyChanged(nameof(AtLabel));
                OnPropertyChanged(nameof(IntervalUnitLabel));
                RefreshPreview();
            }
        }

        public ConfigChoiceOption SelectedDayOfWeek
        {
            get => DayOfWeekOptions.First(option => option.Value == _dayOfWeek.ToString(CultureInfo.InvariantCulture));
            set
            {
                if (value is null)
                    return;

                var next = Math.Clamp(TryParseInt(value.Value, 1), 1, 7);
                if (_dayOfWeek == next)
                    return;

                _dayOfWeek = next;
                OnPropertyChanged();
                RefreshPreview();
            }
        }

        public bool IsMonthly => _type == BackupScheduleType.Monthly;

        public bool IsWeekly => _type == BackupScheduleType.Weekly;

        public bool IsHourly => _type == BackupScheduleType.Hourly;

        public bool IsInterval => _type is
            BackupScheduleType.EveryNDays or
            BackupScheduleType.EveryNHours or
            BackupScheduleType.EveryNMinutes;

        public bool ShowsTime => !IsHourly;

        public string DayLabel => _isChinese ? "日期" : "Day";

        public string EveryLabel => _isChinese ? "每隔" : "Every";

        public string AtLabel => IsInterval
            ? (_isChinese ? "开始时间" : "Start time")
            : (_isChinese ? "执行时间" : "Run at");

        public string MinuteLabel => _isChinese ? "第" : "At minute";

        public string MinuteUnitLabel => _isChinese ? "分钟执行" : "of each hour";

        public string IntervalUnitLabel => _type switch
        {
            BackupScheduleType.EveryNDays => _isChinese ? "天" : "day(s)",
            BackupScheduleType.EveryNHours => _isChinese ? "小时" : "hour(s)",
            BackupScheduleType.EveryNMinutes => _isChinese ? "分钟" : "minute(s)",
            _ => string.Empty
        };

        public string PreviewButtonText => _isChinese ? "预览" : "Preview";

        public string PreviewTitle => _isChinese ? "未来 5 次执行时间" : "Next 5 execution times";

        public void SetLanguage(bool isChinese)
        {
            _isChinese = isChinese;
            RebuildOptions();
            OnPropertyChanged(nameof(ScheduleTypeOptions));
            OnPropertyChanged(nameof(DayOfWeekOptions));
            OnPropertyChanged(nameof(SelectedScheduleType));
            OnPropertyChanged(nameof(SelectedDayOfWeek));
            OnPropertyChanged(nameof(DayLabel));
            OnPropertyChanged(nameof(EveryLabel));
            OnPropertyChanged(nameof(AtLabel));
            OnPropertyChanged(nameof(MinuteLabel));
            OnPropertyChanged(nameof(MinuteUnitLabel));
            OnPropertyChanged(nameof(IntervalUnitLabel));
            OnPropertyChanged(nameof(PreviewButtonText));
            OnPropertyChanged(nameof(PreviewTitle));
            RefreshPreview();
        }

        public void RefreshPreview()
        {
            PreviewExecutionItems.Clear();
            var occurrences = BackupScheduleCalculator.GetNextOccurrences(ToModel(), DateTime.Now, 5);
            if (occurrences.Count == 0)
            {
                PreviewExecutionItems.Add(_isChinese ? "未启用或周期无效" : "Disabled or invalid schedule");
                return;
            }

            foreach (var occurrence in occurrences)
            {
                PreviewExecutionItems.Add(occurrence.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture));
            }
        }

        public BackupSchedule ToModel()
        {
            return BackupScheduleCalculator.Normalize(
                new BackupSchedule
                {
                    Id = _id,
                    Enabled = _enabled,
                    Type = _type,
                    DayOfMonth = TryParseInt(_dayOfMonth, 1),
                    DayOfWeek = _dayOfWeek,
                    Time = _time?.Trim() ?? string.Empty,
                    MinuteOfHour = TryParseInt(_minuteOfHour, 0),
                    Interval = TryParseInt(_interval, 1),
                    AnchorDate = _anchorDate
                },
                DateTime.Now);
        }

        public static AutomationBackupScheduleItem FromModel(BackupSchedule model, bool isChinese)
        {
            var normalized = BackupScheduleCalculator.Normalize(model, DateTime.Now);
            var item = new AutomationBackupScheduleItem(isChinese)
            {
                _id = normalized.Id,
                _enabled = normalized.Enabled,
                _type = normalized.Type,
                _dayOfMonth = normalized.DayOfMonth.ToString(CultureInfo.InvariantCulture),
                _dayOfWeek = normalized.DayOfWeek,
                _time = normalized.Time,
                _minuteOfHour = normalized.MinuteOfHour.ToString(CultureInfo.InvariantCulture),
                _interval = normalized.Interval.ToString(CultureInfo.InvariantCulture),
                _anchorDate = normalized.AnchorDate
            };
            item.RefreshPreview();
            return item;
        }

        private void RebuildOptions()
        {
            ScheduleTypeOptions.Clear();
            ScheduleTypeOptions.Add(new ConfigChoiceOption(BackupScheduleType.Monthly.ToString(), _isChinese ? "每月" : "Monthly"));
            ScheduleTypeOptions.Add(new ConfigChoiceOption(BackupScheduleType.Weekly.ToString(), _isChinese ? "每周" : "Weekly"));
            ScheduleTypeOptions.Add(new ConfigChoiceOption(BackupScheduleType.Daily.ToString(), _isChinese ? "每日" : "Daily"));
            ScheduleTypeOptions.Add(new ConfigChoiceOption(BackupScheduleType.Hourly.ToString(), _isChinese ? "每小时" : "Hourly"));
            ScheduleTypeOptions.Add(new ConfigChoiceOption(BackupScheduleType.EveryNDays.ToString(), _isChinese ? "每隔 N 天" : "Every N days"));
            ScheduleTypeOptions.Add(new ConfigChoiceOption(BackupScheduleType.EveryNHours.ToString(), _isChinese ? "每隔 N 小时" : "Every N hours"));
            ScheduleTypeOptions.Add(new ConfigChoiceOption(BackupScheduleType.EveryNMinutes.ToString(), _isChinese ? "每隔 N 分钟" : "Every N minutes"));

            DayOfWeekOptions.Clear();
            var labels = _isChinese
                ? new[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" }
                : new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
            for (var day = 1; day <= 7; day++)
            {
                DayOfWeekOptions.Add(new ConfigChoiceOption(day.ToString(CultureInfo.InvariantCulture), labels[day - 1]));
            }
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;

            field = value;
            OnPropertyChanged(propertyName);
            RefreshPreview();
            return true;
        }
    }

    public sealed class AutomationScriptItem : INotifyPropertyChanged
    {
        private bool _isChinese;
        private bool _enabled = true;
        private AutomationScriptTrigger _trigger = AutomationScriptTrigger.BeforeStart;
        private string _scriptPath = string.Empty;

        public AutomationScriptItem(bool isChinese = true)
        {
            _isChinese = isChinese;
            RebuildTriggerOptions();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ObservableCollection<ConfigChoiceOption> TriggerOptions { get; } = [];

        public bool Enabled
        {
            get => _enabled;
            set => SetField(ref _enabled, value);
        }

        public string ScriptPath
        {
            get => _scriptPath;
            set => SetField(ref _scriptPath, value ?? string.Empty);
        }

        public ConfigChoiceOption SelectedTrigger
        {
            get => TriggerOptions.First(option =>
                option.Value.Equals(_trigger.ToString(), StringComparison.OrdinalIgnoreCase));
            set
            {
                if (value is null ||
                    !Enum.TryParse(value.Value, true, out AutomationScriptTrigger trigger) ||
                    !Enum.IsDefined(trigger) ||
                    _trigger == trigger)
                {
                    return;
                }

                _trigger = trigger;
                OnPropertyChanged();
            }
        }

        public void SetLanguage(bool isChinese)
        {
            _isChinese = isChinese;
            RebuildTriggerOptions();
            OnPropertyChanged(nameof(TriggerOptions));
            OnPropertyChanged(nameof(SelectedTrigger));
        }

        public AutomationScript ToModel() => new()
        {
            Enabled = _enabled,
            Trigger = _trigger,
            ScriptPath = _scriptPath.Trim()
        };

        public static AutomationScriptItem FromModel(AutomationScript model, bool isChinese)
        {
            var trigger = Enum.IsDefined(model.Trigger)
                ? model.Trigger
                : AutomationScriptTrigger.BeforeStart;
            return new AutomationScriptItem(isChinese)
            {
                _enabled = model.Enabled,
                _trigger = trigger,
                _scriptPath = model.ScriptPath?.Trim() ?? string.Empty
            };
        }

        private void RebuildTriggerOptions()
        {
            TriggerOptions.Clear();
            TriggerOptions.Add(new ConfigChoiceOption(
                AutomationScriptTrigger.BeforeStart.ToString(),
                _isChinese ? "实例启动前" : "Before instance start"));
            TriggerOptions.Add(new ConfigChoiceOption(
                AutomationScriptTrigger.AfterStart.ToString(),
                _isChinese ? "实例启动后" : "After instance start"));
            TriggerOptions.Add(new ConfigChoiceOption(
                AutomationScriptTrigger.BeforeStop.ToString(),
                _isChinese ? "关服前" : "Before server stop"));
            TriggerOptions.Add(new ConfigChoiceOption(
                AutomationScriptTrigger.AfterStop.ToString(),
                _isChinese ? "关服后" : "After server stop"));
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string((value ?? string.Empty)
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim();
        return string.IsNullOrWhiteSpace(result) ? "profile" : result;
    }

    public sealed class AutomationTimeItem : INotifyPropertyChanged
    {
        private string _time;

        public AutomationTimeItem(string time = "03:00")
        {
            _time = time;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Time
        {
            get => _time;
            set => SetField(ref _time, value);
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    public sealed class ScheduledBroadcastItem : INotifyPropertyChanged
    {
        private string _time = "12:00";
        private string _message = string.Empty;
        private bool _enabled = true;

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool Enabled
        {
            get => _enabled;
            set => SetField(ref _enabled, value);
        }

        public string Time
        {
            get => _time;
            set => SetField(ref _time, value);
        }

        public string Message
        {
            get => _message;
            set => SetField(ref _message, value);
        }

        public ScheduledBroadcastMessage ToModel()
        {
            return new ScheduledBroadcastMessage
            {
                Enabled = _enabled,
                Time = _time?.Trim() ?? string.Empty,
                Message = _message?.Trim() ?? string.Empty
            };
        }

        public static ScheduledBroadcastItem FromModel(ScheduledBroadcastMessage model)
        {
            return new ScheduledBroadcastItem
            {
                Enabled = model.Enabled,
                Time = model.Time,
                Message = model.Message
            };
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    public sealed class ScheduledCommandItem : INotifyPropertyChanged
    {
        private string _time = "12:00";
        private string _command = string.Empty;
        private bool _enabled = true;

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool Enabled
        {
            get => _enabled;
            set => SetField(ref _enabled, value);
        }

        public string Time
        {
            get => _time;
            set => SetField(ref _time, value);
        }

        public string Command
        {
            get => _command;
            set => SetField(ref _command, value);
        }

        public ScheduledServerCommand ToModel()
        {
            return new ScheduledServerCommand
            {
                Enabled = _enabled,
                Time = _time?.Trim() ?? string.Empty,
                Command = _command?.Trim() ?? string.Empty
            };
        }

        public static ScheduledCommandItem FromModel(ScheduledServerCommand model)
        {
            return new ScheduledCommandItem
            {
                Enabled = model.Enabled,
                Time = model.Time,
                Command = model.Command
            };
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    public sealed class ModListItem : INotifyPropertyChanged
    {
        private static readonly IBrush UpdateAvailableBrush = new SolidColorBrush(Color.Parse("#2196F3"));
        private static readonly IBrush UpdateLatestBrush = new SolidColorBrush(Color.Parse("#4CAF50"));
        private static readonly IBrush UpdateFailedBrush = new SolidColorBrush(Color.Parse("#F44336"));
        private static readonly IBrush UpdateNeutralBrush = new SolidColorBrush(Color.Parse("#8A8A8A"));
        private bool _isSelected;
        private bool _isChinese;
        private ModUpdateState _updateState;

        public required string Name { get; init; }

        public required string ModId { get; init; }

        public required string Version { get; init; }

        public required string Side { get; init; }

        public required string FilePath { get; init; }

        public required string ConfigPath { get; init; }

        public required string EditConfigText { get; init; }

        public bool CanEditConfig => IsEditableConfigFile(ConfigPath);

        public bool IsDisabled { get; init; }

        public bool ModEnabled => !IsDisabled;

        public required string DependenciesText { get; init; }

        public required string IssuesText { get; init; }

        public ModUpdateCheckResult? UpdateInfo { get; private set; }

        public string UpdateButtonText { get; private set; } = "更新";

        public string UpdateStatusText { get; private set; } = "未检查";

        public IBrush UpdateStatusBrush { get; private set; } = UpdateNeutralBrush;

        public bool IsUpdateButtonVisible => _updateState == ModUpdateState.Available;

        public bool IsUpdateStatusVisible => _updateState != ModUpdateState.Available;

        public bool IsSelected
        {
            get => _isSelected;
            set => SetField(ref _isSelected, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public static ModListItem FromModel(
            ModEntry model,
            bool isChinese,
            ModUpdateCheckCacheEntry? cachedUpdate = null)
        {
            var item = new ModListItem
            {
                Name = model.Name,
                ModId = model.ModId,
                Version = model.Version,
                Side = model.Side,
                FilePath = model.FilePath,
                ConfigPath = model.ConfigPath,
                EditConfigText = isChinese ? "编辑" : "Edit",
                IsDisabled = model.IsDisabled,
                DependenciesText = model.DependenciesText,
                IssuesText = BuildModIssuesText(model, isChinese)
            };
            item.SetLanguage(isChinese);
            item.ApplyCachedUpdate(cachedUpdate, isChinese);
            return item;
        }

        private void ApplyCachedUpdate(ModUpdateCheckCacheEntry? cachedUpdate, bool isChinese)
        {
            if (cachedUpdate is null ||
                !cachedUpdate.CurrentVersion.Equals(Version, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (cachedUpdate.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase))
            {
                SetUpdateCheckFailed(isChinese);
            }
            else if (cachedUpdate.Result is not null &&
                     cachedUpdate.Status.Equals(
                         cachedUpdate.Result.IsUpdateAvailable ? "Available" : "Latest",
                         StringComparison.OrdinalIgnoreCase))
            {
                SetUpdateResult(cachedUpdate.Result, isChinese);
            }
        }

        public void SetLanguage(bool isChinese)
        {
            _isChinese = isChinese;
            UpdateDisplayText();
        }

        public void SetUpdateChecking(bool isChinese)
        {
            _updateState = ModUpdateState.Checking;
            UpdateInfo = null;
            SetLanguage(isChinese);
        }

        public void SetUpdateCheckFailed(bool isChinese)
        {
            _updateState = ModUpdateState.Failed;
            UpdateInfo = null;
            SetLanguage(isChinese);
        }

        public void SetUpdateResult(ModUpdateCheckResult result, bool isChinese)
        {
            UpdateInfo = result;
            _updateState = result.IsUpdateAvailable ? ModUpdateState.Available : ModUpdateState.Latest;
            SetLanguage(isChinese);
        }

        private void UpdateDisplayText()
        {
            UpdateButtonText = _isChinese ? "更新" : "Update";
            UpdateStatusText = _updateState switch
            {
                ModUpdateState.Checking => _isChinese ? "检查中..." : "Checking...",
                ModUpdateState.Failed => _isChinese ? "检查失败" : "Check failed",
                ModUpdateState.Latest => _isChinese ? "最新" : "Latest",
                _ => _isChinese ? "未检查" : "Not checked"
            };
            UpdateStatusBrush = _updateState switch
            {
                ModUpdateState.Available => UpdateAvailableBrush,
                ModUpdateState.Latest => UpdateLatestBrush,
                ModUpdateState.Failed => UpdateFailedBrush,
                _ => UpdateNeutralBrush
            };
            OnPropertyChanged(nameof(UpdateButtonText));
            OnPropertyChanged(nameof(UpdateStatusText));
            OnPropertyChanged(nameof(UpdateStatusBrush));
            OnPropertyChanged(nameof(IsUpdateButtonVisible));
            OnPropertyChanged(nameof(IsUpdateStatusVisible));
        }

        public static ModEntry ToModel(ModListItem item)
        {
            return new ModEntry
            {
                Name = item.Name,
                ModId = item.ModId,
                Version = item.Version,
                Side = item.Side,
                FilePath = item.FilePath,
                ConfigPath = item.ConfigPath,
                Status = item.IsDisabled ? "Disabled" : "OK",
                IsDisabled = item.IsDisabled,
                Dependencies = [],
                DependencyIssues = []
            };
        }

        private static bool IsEditableConfigFile(string? path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   !path.Contains(" | ", StringComparison.Ordinal) &&
                   File.Exists(path);
        }

        private static string BuildModIssuesText(ModEntry model, bool isChinese)
        {
            var issues = model.DependencyIssues
                .Select(issue => LocalizeModIssue(issue, isChinese))
                .ToList();
            if (!model.Status.Equals("OK", StringComparison.OrdinalIgnoreCase) &&
                !model.Status.Equals("MissingDependency", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(model.Status.Equals("InvalidMetadata", StringComparison.OrdinalIgnoreCase)
                    ? (isChinese ? "元数据无效" : "Invalid metadata")
                    : model.Status);
            }

            return issues.Count == 0 ? "-" : string.Join("; ", issues);
        }

        private static string LocalizeModIssue(string issue, bool isChinese)
        {
            const string chinesePrefix = "缺少依赖:";
            const string englishPrefix = "Missing dependency:";
            var value = issue.Trim();

            if (value.StartsWith(chinesePrefix, StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith(englishPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var dependency = value[(value.IndexOf(':') + 1)..].Trim();
                return isChinese
                    ? $"缺少依赖: {dependency}"
                    : $"Missing dependency: {dependency}";
            }

            return issue;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private enum ModUpdateState
        {
            NotChecked,
            Checking,
            Available,
            Latest,
            Failed
        }
    }

    public sealed class AuthPlayerListItem
    {
        public required string PlayerUid { get; init; }

        public required string PlayerName { get; init; }

        public required string RegisteredAtText { get; init; }

        public required string RegisteredIp { get; init; }

        public required string LastLoginAtText { get; init; }

        public required string LastIp { get; init; }

        public required string PasswordStateText { get; init; }

        public required string ExternalUsername { get; init; }

        public static AuthPlayerListItem FromModel(ServerAuthPlayerSummary model, bool isChinese)
        {
            return new AuthPlayerListItem
            {
                PlayerUid = model.PlayerUid,
                PlayerName = model.PlayerName,
                RegisteredAtText = model.RegisteredAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                RegisteredIp = model.RegisteredIp,
                LastLoginAtText = model.LastLoginAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "-",
                LastIp = model.LastIp,
                PasswordStateText = model.PasswordResetRequired
                    ? (isChinese ? "重置待处理" : "Reset required")
                    : model.HasPassword
                        ? (isChinese ? "已设置" : "Set")
                        : (isChinese ? "未设置" : "Not set"),
                ExternalUsername = !string.IsNullOrWhiteSpace(model.OAuth2Username) ||
                                   !string.IsNullOrWhiteSpace(model.OAuth2DisplayName)
                    ? "OAuth2: " + (string.IsNullOrWhiteSpace(model.OAuth2Username)
                        ? model.OAuth2DisplayName
                        : model.OAuth2Username)
                    : !string.IsNullOrWhiteSpace(model.DiscourseUsername)
                        ? "Discourse: " + model.DiscourseUsername
                        : "-"
            };
        }
    }

    public sealed class DashboardServerItem : INotifyPropertyChanged
    {
        private string _profileName = string.Empty;
        private string _version = string.Empty;
        private bool _isRunning;
        private string _statusText = string.Empty;
        private IBrush _statusBrush = Brushes.Gray;
        private string _summaryText = string.Empty;
        private string _actionText = string.Empty;
        private bool _isActionEnabled = true;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string ProfileId { get; init; } = string.Empty;

        public string ProfileName
        {
            get => _profileName;
            set => SetField(ref _profileName, value);
        }

        public string Version
        {
            get => _version;
            set => SetField(ref _version, value);
        }

        public bool IsRunning
        {
            get => _isRunning;
            set => SetField(ref _isRunning, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => SetField(ref _statusText, value);
        }

        public IBrush StatusBrush
        {
            get => _statusBrush;
            set => SetField(ref _statusBrush, value);
        }

        public string SummaryText
        {
            get => _summaryText;
            set => SetField(ref _summaryText, value);
        }

        public string ActionText
        {
            get => _actionText;
            set => SetField(ref _actionText, value);
        }

        public bool IsActionEnabled
        {
            get => _isActionEnabled;
            set => SetField(ref _isActionEnabled, value);
        }

        private void SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed class DashboardPlayerItem
    {
        public ServerOnlinePlayerInfo? Player { get; init; }

        public string PlayerName { get; init; } = string.Empty;

        public string ProfileName { get; init; } = string.Empty;

        public string LatencyText { get; init; } = string.Empty;

        public string JoinedAtText { get; init; } = string.Empty;

        public bool CanOpenDetails => Player is not null;

        public static DashboardPlayerItem FromModel(ServerOnlinePlayerInfo player)
        {
            return new DashboardPlayerItem
            {
                Player = player,
                PlayerName = player.PlayerName,
                ProfileName = player.ProfileName,
                LatencyText = player.PingMilliseconds.HasValue
                    ? $"{player.PingMilliseconds.Value.ToString(CultureInfo.InvariantCulture)} ms"
                    : "--",
                JoinedAtText = player.JoinedAtUtc.HasValue
                    ? player.JoinedAtUtc.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                    : "--"
            };
        }
    }

    public sealed class DashboardUptimeItem
    {
        public string Name { get; init; } = string.Empty;

        public string UptimeText { get; init; } = string.Empty;
    }

    public sealed class ConsoleLogFilterRuleItem : INotifyPropertyChanged
    {
        private bool _enabled = true;
        private ConsoleLogFilterMode _mode = ConsoleLogFilterMode.Contains;
        private string _pattern = string.Empty;

        public ConsoleLogFilterRuleItem(bool isChinese = true, Func<string, string, string>? translate = null)
        {
            SetLanguage(isChinese, translate);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ObservableCollection<ConfigChoiceOption> ModeOptions { get; } = [];

        public string DeleteLabel { get; private set; } = "删除";

        public bool Enabled
        {
            get => _enabled;
            set => SetField(ref _enabled, value);
        }

        public string Pattern
        {
            get => _pattern;
            set => SetField(ref _pattern, value ?? string.Empty);
        }

        public ConfigChoiceOption SelectedMode
        {
            get => ModeOptions.First(option => option.Value.Equals(_mode.ToString(), StringComparison.OrdinalIgnoreCase));
            set
            {
                if (value is null ||
                    !Enum.TryParse(value.Value, true, out ConsoleLogFilterMode mode) ||
                    !Enum.IsDefined(mode) ||
                    _mode == mode)
                {
                    return;
                }

                _mode = mode;
                OnPropertyChanged();
            }
        }

        public void SetLanguage(bool isChinese, Func<string, string, string>? translate = null)
        {
            string Translate(string zh, string en) => translate?.Invoke(zh, en) ?? (isChinese ? zh : en);

            ModeOptions.Clear();
            ModeOptions.Add(new ConfigChoiceOption(
                ConsoleLogFilterMode.Contains.ToString(),
                Translate("包含", "Contains")));
            ModeOptions.Add(new ConfigChoiceOption(
                ConsoleLogFilterMode.Exact.ToString(),
                Translate("完全匹配", "Exact")));
            ModeOptions.Add(new ConfigChoiceOption(
                ConsoleLogFilterMode.Regex.ToString(),
                Translate("正则表达式", "Regex")));
            DeleteLabel = Translate("删除", "Delete");
            OnPropertyChanged(nameof(ModeOptions));
            OnPropertyChanged(nameof(SelectedMode));
            OnPropertyChanged(nameof(DeleteLabel));
        }

        public ConsoleLogFilterRule ToModel()
        {
            return new ConsoleLogFilterRule
            {
                Enabled = _enabled,
                Mode = _mode,
                Pattern = _pattern
            };
        }

        public static ConsoleLogFilterRuleItem FromModel(
            ConsoleLogFilterRule model,
            bool isChinese,
            Func<string, string, string>? translate = null)
        {
            var item = new ConsoleLogFilterRuleItem(isChinese, translate)
            {
                _enabled = model.Enabled,
                _mode = Enum.IsDefined(model.Mode) ? model.Mode : ConsoleLogFilterMode.Contains,
                _pattern = model.Pattern ?? string.Empty
            };
            item.OnPropertyChanged(nameof(item.SelectedMode));
            return item;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    public sealed class ConsoleServerItem
    {
        public string ProfileId { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;
    }

    public sealed class LaunchTargetItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string ProfileId { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    public sealed class ConfigSaveFileItem
    {
        public required string FullPath { get; init; }

        public required string FileName { get; init; }

        public static ConfigSaveFileItem FromSave(SaveFileEntry save)
        {
            return new ConfigSaveFileItem
            {
                FullPath = save.FullPath,
                FileName = save.FileName
            };
        }

        public static ConfigSaveFileItem FromPath(string path)
        {
            return new ConfigSaveFileItem
            {
                FullPath = path,
                FileName = string.IsNullOrWhiteSpace(Path.GetFileName(path)) ? path : Path.GetFileName(path)
            };
        }
    }

    public sealed class ConfigWorldRuleItem : INotifyPropertyChanged
    {
        private string _value;
        private ConfigChoiceOption? _selectedChoiceOption;
        private bool _canEdit = true;

        public ConfigWorldRuleItem(
            WorldRuleDefinition definition,
            string value,
            bool isChinese,
            IReadOnlyList<ConfigChoiceOption> choiceOptions,
            string? labelZhOverride = null)
        {
            Definition = definition;
            Key = definition.Key;
            Type = definition.Type;
            ChoiceOptions = choiceOptions;
            _value = value;
            SetLanguage(isChinese, choiceOptions, labelZhOverride);
            _selectedChoiceOption = ChoiceOptions.FirstOrDefault(option =>
                option.Value.Equals(value, StringComparison.OrdinalIgnoreCase));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public WorldRuleDefinition Definition { get; }

        public string Key { get; }

        public WorldRuleType Type { get; }

        public string Label { get; private set; } = string.Empty;

        public string Description { get; private set; } = string.Empty;

        public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

        public string BooleanLabel { get; private set; } = string.Empty;

        public IReadOnlyList<ConfigChoiceOption> ChoiceOptions { get; private set; }

        public bool IsOnlyDuringWorldCreate { get; init; }

        public bool IsBoolean => Type == WorldRuleType.Boolean;

        public bool IsChoice => Type == WorldRuleType.Choice;

        public bool IsText => Type is WorldRuleType.Text or WorldRuleType.Number;

        public bool CanEdit
        {
            get => _canEdit;
            set => SetField(ref _canEdit, value);
        }

        public string Value
        {
            get => _value;
            set
            {
                if (!SetField(ref _value, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(BoolValue));
            }
        }

        public bool BoolValue
        {
            get => bool.TryParse(Value, out var parsed) && parsed;
            set => Value = value ? bool.TrueString.ToLowerInvariant() : bool.FalseString.ToLowerInvariant();
        }

        public ConfigChoiceOption? SelectedChoiceOption
        {
            get => _selectedChoiceOption;
            set
            {
                if (!SetField(ref _selectedChoiceOption, value) || value is null)
                {
                    return;
                }

                Value = value.Value;
            }
        }

        public void SetLanguage(bool isChinese, IReadOnlyList<ConfigChoiceOption> choiceOptions, string? labelZhOverride = null)
        {
            var selectedValue = SelectedChoiceOption?.Value ?? Value;
            ChoiceOptions = choiceOptions;
            Label = isChinese ? labelZhOverride ?? Definition.LabelZh : Definition.LabelEn;
            Description = isChinese ? Definition.DescriptionZh ?? string.Empty : Definition.DescriptionEn ?? string.Empty;
            BooleanLabel = isChinese ? "启用" : "Enabled";
            _selectedChoiceOption = ChoiceOptions.FirstOrDefault(option =>
                option.Value.Equals(selectedValue, StringComparison.OrdinalIgnoreCase));
            OnPropertyChanged(nameof(ChoiceOptions));
            OnPropertyChanged(nameof(Label));
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(HasDescription));
            OnPropertyChanged(nameof(BooleanLabel));
            OnPropertyChanged(nameof(SelectedChoiceOption));
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
