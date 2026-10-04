using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

public interface IOpenApiService
{
    bool IsRunning(string profileId);

    int GetPort(InstanceProfile profile);

    void SavePort(InstanceProfile profile, int port);

    string? GetCoverPath(InstanceProfile profile);

    void SaveCover(InstanceProfile profile, string sourcePath);

    void ClearCover(InstanceProfile profile);

    Task StartAsync(InstanceProfile profile, int port, CancellationToken cancellationToken = default);

    Task StopAsync(string profileId, CancellationToken cancellationToken = default);

    Task StopAllAsync(CancellationToken cancellationToken = default);
}
