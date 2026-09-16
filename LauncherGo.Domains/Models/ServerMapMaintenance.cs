namespace LauncherGo.Domains.Models;

// A deployment preview is also an optimistic concurrency token: confirmation
// never authorizes replacing a package that changed while the dialog was open.
public sealed record ServerMapModDeployment(
    string SourcePath, string TargetPath, string SourceHash, string? TargetHash,
    string SourceVersion, string InstalledVersion, IReadOnlyList<string> Conflicts)
{
    public bool IsIdentical => SourceHash == TargetHash;
}

public sealed record ServerMapWebReset(string SourcePath, string? TargetPath, bool RequiresHostRestart);

public sealed record ServerMapMaintenanceResult(int FilesChanged, string? BackupPath = null,
    bool AppliesOnNextStart = false, bool RequiresHostRestart = false);
