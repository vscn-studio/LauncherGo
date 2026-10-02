namespace LauncherGo.Domains.Models;

/// <summary>
///     模组项
/// </summary>
public class ModEntry
{
    public required string Name { get; init; }

    public required string ModId { get; init; }

    public required string Version { get; init; }

    public required string Side { get; init; }

    public required string FilePath { get; init; }

    public string ConfigPath { get; init; } = "-";

    public required string Status { get; init; }

    public bool IsDisabled { get; init; }

    /// <summary>同一 modid 在当前 Mods 目录中存在多个安装项。</summary>
    public bool IsDuplicate { get; init; }

    /// <summary>同一 modid 存在多个版本。</summary>
    public bool IsVersionConflict { get; init; }

    public IReadOnlyList<ModDependency> Dependencies { get; init; } = [];

    public IReadOnlyList<string> DependencyIssues { get; init; } = [];

    public string DependenciesText => Dependencies.Count == 0
        ? "-"
        : string.Join(", ", Dependencies.Select(static x =>
            string.IsNullOrWhiteSpace(x.Version) ? x.ModId : $"{x.ModId}@{x.Version}"));

    public string IssuesText => DependencyIssues.Count == 0 ? "-" : string.Join("; ", DependencyIssues);
}

