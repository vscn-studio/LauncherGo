using LauncherGo.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LauncherGo.Services.Extensions;

public static class ServiceCollectionExtension
{
    public static IServiceCollection AddLauncherGoServices(this IServiceCollection services)
    {
        services.AddSingleton<ILauncherPreferencesService, LauncherPreferencesService>();
        services.AddSingleton<IServerPackageService, ServerPackageService>();
        services.AddSingleton<ILauncherUpdateService, LauncherUpdateService>();
        services.AddSingleton<IInstanceProfileService, InstanceProfileService>();
        services.AddSingleton<IInstanceSaveService, InstanceSaveService>();
        services.AddSingleton<IInstanceServerConfigService, InstanceServerConfigService>();
        services.AddSingleton<IServerProcessService, ServerProcessService>();
        services.AddSingleton<IServerTransport, LocalServerTransport>();
        services.AddSingleton<ILogTailService, LogTailService>();
        services.AddSingleton<IAutomationSettingsService, AutomationSettingsService>();
        services.AddSingleton<IAutomationLifecycleService, AutomationLifecycleService>();
        services.AddSingleton<IAutomationService, AutomationService>();
        services.AddSingleton<IFrpService, FrpService>();
        services.AddSingleton<IThirdPartyFrpcService, ThirdPartyFrpcService>();
        services.AddSingleton<IEasyTierService, EasyTierService>();
        services.AddSingleton<ITcpGatewayService, TcpGatewayService>();
        services.AddSingleton<IGatewayRedirectModService, GatewayRedirectModService>();
        services.AddSingleton<IInstanceModService, InstanceModService>();
        services.AddSingleton<IModFileArchiveService, ModFileArchiveService>();
        services.AddSingleton<IModListExportService, ModListExportService>();
        services.AddSingleton<IModUpdateService, ModUpdateService>();
        services.AddSingleton<IServerAuthService, ServerAuthService>();
        services.AddSingleton<IServerMapService, ServerMapService>();
        services.AddSingleton<IServerBridgeService, ServerBridgeService>();
        services.AddSingleton<ServerBridgeStateStore>();
        services.AddSingleton<IServerBridgeMigrationService, ServerBridgeMigrationService>();
        services.AddSingleton<Vs2QQProcessService>();
        services.AddSingleton<IRobotService, RobotService>();
        services.AddSingleton<IDiscordBotService, DiscordBotService>();
        return services;
    }
}
