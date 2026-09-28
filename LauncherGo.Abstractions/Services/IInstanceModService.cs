using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

/// <summary>
///     实例模组服务
/// </summary>
public interface IInstanceModService
{
    Task<IReadOnlyList<ModEntry>> GetModsAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default);

    Task<ModEntry> ImportModZipAsync(
        InstanceProfile profile,
        string zipPath,
        CancellationToken cancellationToken = default);

    Task<ModEntry> UpdateModAsync(
        InstanceProfile profile,
        ModEntry installedMod,
        string downloadUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     导入一个或多个 Mod ZIP 文件或模组文件夹。
    /// </summary>
    Task<IReadOnlyList<ModEntry>> ImportModsAsync(
        InstanceProfile profile,
        IReadOnlyCollection<string> sourcePaths,
        CancellationToken cancellationToken = default);

    Task<int> NormalizeModNamesAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default);

    Task SetModEnabledAsync(
        InstanceProfile profile,
        string modId,
        string version,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<int> DeleteModsAsync(
        InstanceProfile profile,
        IReadOnlyCollection<ModEntry> mods,
        CancellationToken cancellationToken = default);
}

