using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

public sealed partial class ServerMapService
{
    public Task<ServerMapModDeployment> InspectMapModAsync(InstanceProfile profile, CancellationToken cancellationToken = default) =>
        Task.Run(() => InspectMapMod(builtInMapMod,
            Path.Combine(WorkspacePathHelper.GetProfileModsPath(profile.DirectoryPath), "servermap.zip"), cancellationToken), cancellationToken);

    internal static ServerMapModDeployment InspectMapMod(string source, string target, CancellationToken cancellationToken = default)
    {
        source = Path.GetFullPath(source);
        target = Path.GetFullPath(target);
        RejectLinkedPath(target);
        var conflicts = new List<string>();
        var mods = Path.GetDirectoryName(target)!;
        if (Directory.Exists(mods))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(mods))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    conflicts.Add(path); // Do not follow links into another profile.
                    continue;
                }
                var (id, _) = ReadModMetadata(path);
                var canonical = path.Equals(target, StringComparison.OrdinalIgnoreCase);
                if ((id.Equals("servermap", StringComparison.OrdinalIgnoreCase) && !canonical) ||
                    (canonical && (Directory.Exists(path) || (id.Length > 0 && !id.Equals("servermap", StringComparison.OrdinalIgnoreCase)))))
                    conflicts.Add(path);
            }
        }
        return new(source, target, File.Exists(source) ? HashFile(source) : "", File.Exists(target) ? HashFile(target) : null,
            ReadModMetadata(source).Version, ReadModMetadata(target).Version, conflicts);
    }

    private static (string Id, string Version) ReadModMetadata(string path)
    {
        try
        {
            using var archive = File.Exists(path) && Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase)
                ? ZipFile.OpenRead(path) : null;
            using var input = archive is not null
                ? archive.Entries.FirstOrDefault(e => e.FullName.Equals("modinfo.json", StringComparison.OrdinalIgnoreCase))?.Open()
                : Directory.Exists(path) && File.Exists(Path.Combine(path, "modinfo.json"))
                    ? File.OpenRead(Path.Combine(path, "modinfo.json")) : null;
            if (input is null) return ("", "unknown");
            using var json = JsonDocument.Parse(input, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (json.RootElement.ValueKind != JsonValueKind.Object) return ("", "unknown");
            string Read(string key) => json.RootElement.EnumerateObject()
                .FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.String } value
                ? value.GetString() ?? "" : "";
            return (Read("modid"), Read("version"));
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { return ("", "unknown"); }
    }

    private static string HashFile(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    public async Task<ServerMapMaintenanceResult> DeployMapModAsync(InstanceProfile profile, ServerMapModDeployment confirmed,
        CancellationToken cancellationToken = default)
    {
        using var control = await BackgroundHostFiles.AcquireControlAsync(Path.Combine(GetProfileDirectory(profile), ".deployment"), cancellationToken);
        return await Task.Run(() => DeployConfirmedMapMod(builtInMapMod,
            Path.Combine(WorkspacePathHelper.GetProfileModsPath(profile.DirectoryPath), "servermap.zip"),
            Path.Combine(GetProfileDirectory(profile), "Backups"), confirmed, cancellationToken), cancellationToken);
    }

    internal static ServerMapMaintenanceResult DeployConfirmedMapMod(string source, string target, string backupRoot,
        ServerMapModDeployment confirmed, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = InspectMapMod(source, target, cancellationToken);
        if (current.Conflicts.Count > 0)
            throw new InvalidOperationException("请先处理模组冲突 / Resolve mod conflicts first:\n" + string.Join("\n", current.Conflicts));
        if (current.SourcePath != confirmed.SourcePath || current.TargetPath != confirmed.TargetPath ||
            current.SourceHash != confirmed.SourceHash || current.TargetHash != confirmed.TargetHash)
            throw new IOException("模组文件已变化，请重新确认部署。 / Mod files changed; confirm deployment again.");
        ValidateMapModPackage(source);
        if (!ReadModMetadata(source).Id.Equals("servermap", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("内置模组包 modid 必须为 servermap。 / Expected modid servermap.");
        if (current.IsIdentical) return new(0);
        RejectLinkedPath(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + $".{Guid.NewGuid():N}.tmp";
        string? backup = null;
        try
        {
            File.Copy(source, temporary);
            if (HashFile(temporary) != current.SourceHash || HashFile(source) != current.SourceHash)
                throw new IOException("模组包在复制期间变化，请重试。 / Mod package changed during copy; retry.");
            if (current.TargetHash is not null)
            {
                backup = NewMaintenanceBackup(backupRoot, "mod", Path.GetDirectoryName(target)!);
                var backupFile = Path.Combine(backup, "servermap.zip");
                File.Copy(target, backupFile);
                if (HashFile(backupFile) != current.TargetHash)
                    throw new IOException("已安装模组在备份期间变化，请重试。 / Installed mod changed during backup; retry.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            var latest = InspectMapMod(source, target, cancellationToken);
            if (latest.SourceHash != current.SourceHash || latest.TargetHash != current.TargetHash || latest.Conflicts.Count > 0)
                throw new IOException("模组目录已变化，请重新确认。 / Mod directory changed; confirm again.");
            File.Move(temporary, target, overwrite: current.TargetHash is not null);
            return new(1, backup);
        }
        catch (Exception ex) when (backup is not null && ex is not OperationCanceledException)
        {
            throw new IOException($"{ex.Message}\n备份 / Backup: {backup}", ex);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<ServerMapWebReset> InspectWebResetAsync(InstanceProfile profile, CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(profile, cancellationToken);
        var runningDefault = ResolveRunningDefaultWebRoot(profile);
        var target = string.IsNullOrWhiteSpace(settings.WebRoot) ? runningDefault : settings.WebRoot;
        var requiresRestart = false;
        if (runningDefault is not null)
        {
            // host.json is the saved launch snapshot, not the editable settings.
            var active = BackgroundHostFiles.Read<ServerMapSettings>(Path.Combine(RuntimeDirectory(profile.Id), "host.json"));
            requiresRestart = active is null || !string.Equals(active.WebRoot, settings.WebRoot, StringComparison.OrdinalIgnoreCase);
        }
        if (!File.Exists(Path.Combine(builtInWebRoot, "index.html")))
            throw new FileNotFoundException("内置地图网页不存在，请重新安装 LauncherGo。 / Bundled map page is missing.");
        return new(Path.GetFullPath(builtInWebRoot), target, requiresRestart);
    }

    public async Task<ServerMapMaintenanceResult> ResetWebRootAsync(InstanceProfile profile, ServerMapWebReset confirmed,
        CancellationToken cancellationToken = default)
    {
        await webUpdateGate.WaitAsync(cancellationToken);
        try
        {
            using var control = await BackgroundHostFiles.AcquireControlAsync(RuntimeDirectory(profile.Id), cancellationToken);
            var current = await InspectWebResetAsync(profile, cancellationToken);
            if (current != confirmed)
                throw new IOException("网页目录或运行状态已变化，请重新确认。 / Web directory or runtime changed; confirm again.");
            if (current.TargetPath is null) return new(0, AppliesOnNextStart: true);
            return await Task.Run(async () =>
            {
                ValidateWebDirectories(current.SourcePath, current.TargetPath);
                var files = Directory.GetFiles(current.SourcePath, "*", SearchOption.AllDirectories);
                // Check every path before backing up or replacing anything.
                foreach (var file in files)
                {
                    RejectLinkedPath(file);
                    RejectLinkedPath(Path.Combine(current.TargetPath, Path.GetRelativePath(current.SourcePath, file)));
                }
                var backup = NewMaintenanceBackup(Path.Combine(GetProfileDirectory(profile), "Backups"), "web", current.TargetPath);
                try
                {
                    foreach (var file in files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var relative = Path.GetRelativePath(current.SourcePath, file);
                        var destination = Path.Combine(current.TargetPath, relative);
                        if (!File.Exists(destination)) continue;
                        var saved = Path.Combine(backup, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                        File.Copy(destination, saved);
                    }
                    var count = await CopyWebRootAsync(current.SourcePath, current.TargetPath, cancellationToken);
                    return new ServerMapMaintenanceResult(count, backup, RequiresHostRestart: current.RequiresHostRestart);
                }
                catch (Exception ex)
                {
                    throw new IOException($"网页重置未完成，可重试。 / Web reset incomplete; retry.\n{ex.Message}\n备份 / Backup: {backup}", ex);
                }
            }, cancellationToken);
        }
        finally { webUpdateGate.Release(); }
    }

    private static string NewMaintenanceBackup(string root, string kind, string excludedRoot)
    {
        var path = Path.GetFullPath(Path.Combine(root, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{kind}-{Guid.NewGuid():N}"));
        if (IsWithin(path, excludedRoot))
            throw new IOException("备份目录不能位于网页或 Mods 目录内。 / Backup must be outside the web/Mods directory.");
        RejectLinkedPath(path);
        Directory.CreateDirectory(path);
        return path;
    }

    private static bool IsWithin(string path, string root)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectLinkedPath(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"不支持链接路径 / Linked paths are not supported: {current}");
    }

    private static void ValidateWebDirectories(string source, string target)
    {
        source = Path.GetFullPath(source);
        target = Path.GetFullPath(target);
        if (target.Equals(Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase) || IsWithin(source, target) || IsWithin(target, source))
            throw new InvalidOperationException("自定义 WebRoot 不能是根目录或与内置网页目录重叠。 / WebRoot cannot be a root or overlap bundled files.");
        RejectLinkedPath(source);
        RejectLinkedPath(target);
    }
}
