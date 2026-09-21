using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

public interface IVoiceWebService
{
    VoiceSettings LoadSettings(InstanceProfile profile);
    void SaveSettings(InstanceProfile profile, VoiceSettings settings);
    Task<VoiceRuntimeStatus> StartAsync(InstanceProfile profile, CancellationToken cancellationToken = default);
    Task StopAsync(InstanceProfile profile, CancellationToken cancellationToken = default);
    VoiceRuntimeStatus GetStatus(InstanceProfile profile);
    string GetUrl(VoiceSettings settings);
}
