using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

public interface IServerMapService
{
    Task<ServerMapRenderProgress?> GetRenderProgressAsync(InstanceProfile profile, CancellationToken cancellationToken = default) => Task.FromResult<ServerMapRenderProgress?>(null);
    Task<ServerMapSettings> LoadSettingsAsync(InstanceProfile profile, CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(InstanceProfile profile, ServerMapSettings settings, CancellationToken cancellationToken = default);
    Task<ServerMapModDeployment> InspectMapModAsync(InstanceProfile profile, CancellationToken cancellationToken = default);
    Task<ServerMapMaintenanceResult> DeployMapModAsync(InstanceProfile profile, ServerMapModDeployment confirmed, CancellationToken cancellationToken = default);
    Task<ServerMapWebReset> InspectWebResetAsync(InstanceProfile profile, CancellationToken cancellationToken = default);
    Task<ServerMapMaintenanceResult> ResetWebRootAsync(InstanceProfile profile, ServerMapWebReset confirmed, CancellationToken cancellationToken = default);
    Task<ServerMapRuntimeStatus> StartAsync(InstanceProfile profile, CancellationToken cancellationToken = default);
    Task StopAsync(InstanceProfile profile, CancellationToken cancellationToken = default);
    Task StopAllAsync(CancellationToken cancellationToken = default);
    ServerMapRuntimeStatus GetStatus(InstanceProfile profile);
    Task<bool> ValidateCertificateAsync(InstanceProfile profile, ServerMapSettings settings, CancellationToken cancellationToken = default);
    string GetProfileDirectory(InstanceProfile profile);
}
