using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

public interface IServerMapService
{
    Task<ServerMapSettings> LoadSettingsAsync(InstanceProfile profile, CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(InstanceProfile profile, ServerMapSettings settings, CancellationToken cancellationToken = default);
    Task EnsureMapModDeployedAsync(InstanceProfile profile, CancellationToken cancellationToken = default);
    Task<int> UpdateWebRootAsync(InstanceProfile profile, ServerMapSettings settings, CancellationToken cancellationToken = default);
    Task<ServerMapRuntimeStatus> StartAsync(InstanceProfile profile, CancellationToken cancellationToken = default);
    Task StopAsync(InstanceProfile profile, CancellationToken cancellationToken = default);
    Task StopAllAsync(CancellationToken cancellationToken = default);
    ServerMapRuntimeStatus GetStatus(InstanceProfile profile);
    Task<bool> ValidateCertificateAsync(InstanceProfile profile, ServerMapSettings settings, CancellationToken cancellationToken = default);
    string GetProfileDirectory(InstanceProfile profile);
}
