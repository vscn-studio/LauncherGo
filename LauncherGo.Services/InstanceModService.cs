using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

/// <summary>
///     实例模组服务默认实现
/// </summary>
public class InstanceModService(IInstanceServerConfigService serverConfigService) : IInstanceModService
{
    private static readonly HttpClient UpdateHttpClient = CreateUpdateHttpClient();
    private static readonly JsonDocumentOptions ModInfoJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 64
    };

    private static readonly HashSet<string> BuiltInDependencyIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "game",
        "creative",
        "survival",
        "vssurvivalmod",
        "vsessentials"
    };

    private static readonly Dictionary<string, string[]> ModConfigAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["launchergoauth"] = ["serverauth"]
    };

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModEntry>> GetModsAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        var modsPath = WorkspacePathHelper.GetProfileModsPath(profile.DirectoryPath);
        var modConfigPath = Path.Combine(WorkspacePathHelper.ResolveProfileDataPath(profile.DirectoryPath), "ModConfig");
        Directory.CreateDirectory(modsPath);

        var disabledSet = await LoadDisabledModSetAsync(profile, cancellationToken);
        var entries = new List<ModEntry>();

        foreach (var file in Directory.EnumerateFiles(modsPath, "*.zip", SearchOption.TopDirectoryOnly))
            entries.Add(ReadModFromZip(file, disabledSet, modConfigPath));

        foreach (var directory in Directory.EnumerateDirectories(modsPath, "*", SearchOption.TopDirectoryOnly))
            entries.Add(ReadModFromDirectory(directory, disabledSet, modConfigPath));

        var enabledModIds = entries
            .Where(static mod => !mod.IsDisabled)
            .Select(static mod => mod.ModId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var normalized = entries.Select(mod =>
        {
            var issues = new List<string>(mod.DependencyIssues);
            foreach (var dependency in mod.Dependencies)
            {
                if (BuiltInDependencyIds.Contains(dependency.ModId)) continue;
                if (enabledModIds.Contains(dependency.ModId)) continue;

                issues.Add(
                    $"缺少依赖: {dependency.ModId}" +
                    (string.IsNullOrWhiteSpace(dependency.Version) ? string.Empty : $"@{dependency.Version}"));
            }

            var status = issues.Count > 0 ? "MissingDependency" : mod.Status;
            return new ModEntry
            {
                Name = mod.Name,
                ModId = mod.ModId,
                Version = mod.Version,
                Side = mod.Side,
                FilePath = mod.FilePath,
                ConfigPath = mod.ConfigPath,
                Status = status,
                IsDisabled = mod.IsDisabled,
                Dependencies = mod.Dependencies,
                DependencyIssues = issues
            };
        }).OrderBy(static mod => mod.ModId, StringComparer.OrdinalIgnoreCase).ToList();

        return normalized;
    }

    /// <inheritdoc />
    public async Task<ModEntry> ImportModZipAsync(
        InstanceProfile profile,
        string zipPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
            throw new InvalidOperationException("Mod ZIP 文件不存在。");
        if (!Path.GetExtension(zipPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("仅支持导入 ZIP 格式 Mod。");

        var modsPath = WorkspacePathHelper.GetProfileModsPath(profile.DirectoryPath);
        Directory.CreateDirectory(modsPath);

        var fileName = WorkspacePathHelper.SanitizeFileName(Path.GetFileName(zipPath));
        var destinationPath = Path.Combine(modsPath, fileName);
        File.Copy(zipPath, destinationPath, overwrite: true);

        var disabledSet = await LoadDisabledModSetAsync(profile, cancellationToken);
        var modConfigPath = Path.Combine(WorkspacePathHelper.ResolveProfileDataPath(profile.DirectoryPath), "ModConfig");
        return ReadModFromZip(destinationPath, disabledSet, modConfigPath);
    }

    /// <inheritdoc />
    public async Task<ModEntry> UpdateModAsync(
        InstanceProfile profile,
        ModEntry installedMod,
        string downloadUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(installedMod);
        if (!Uri.TryCreate(downloadUrl?.Trim(), UriKind.Absolute, out var downloadUri) ||
            downloadUri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("模组下载地址无效。");
        }

        var modsPath = WorkspacePathHelper.GetProfileModsPath(profile.DirectoryPath);
        Directory.CreateDirectory(modsPath);
        var modsRoot = EnsureDirectoryPrefix(modsPath);
        var existingPath = Path.GetFullPath(installedMod.FilePath);
        if (!IsWithinDirectory(existingPath, modsRoot) ||
            (!File.Exists(existingPath) && !Directory.Exists(existingPath)))
        {
            throw new InvalidOperationException("旧模组文件不存在或路径无效。");
        }

        var modConfigPath = Path.Combine(WorkspacePathHelper.ResolveProfileDataPath(profile.DirectoryPath), "ModConfig");
        var disabledSet = await LoadDisabledModSetAsync(profile, cancellationToken);
        var tempPath = Path.Combine(modsPath, $".launchergoupdate-{Guid.NewGuid():N}.zip");
        var backupPath = existingPath + $".launchergobak-{Guid.NewGuid():N}";

        try
        {
            using var response = await UpdateHttpClient.GetAsync(downloadUri, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await source.CopyToAsync(target, cancellationToken);
            }

            var downloadedMod = ReadModFromZip(tempPath, disabledSet, modConfigPath);
            if (downloadedMod.Status.Equals("InvalidMetadata", StringComparison.OrdinalIgnoreCase) ||
                !downloadedMod.ModId.Equals(installedMod.ModId, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(downloadedMod.Version) ||
                downloadedMod.Version.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("下载的模组包与当前模组不匹配。");
            }

            var destinationPath = File.Exists(existingPath) &&
                                  Path.GetExtension(existingPath).Equals(".zip", StringComparison.OrdinalIgnoreCase)
                ? existingPath
                : Path.Combine(
                    modsPath,
                    WorkspacePathHelper.SanitizeFileName($"{downloadedMod.ModId}-{downloadedMod.Version}.zip"));
            if (!PathsEqual(destinationPath, existingPath) &&
                (File.Exists(destinationPath) || Directory.Exists(destinationPath)))
            {
                throw new InvalidOperationException("更新目标文件已存在。");
            }

            MoveToBackup(existingPath, backupPath);
            try
            {
                File.Move(tempPath, destinationPath);
                await SetModEnabledAsync(profile, downloadedMod.ModId, downloadedMod.Version, !installedMod.IsDisabled, cancellationToken);
            }
            catch
            {
                TryDeletePath(destinationPath);
                TryRestoreBackup(backupPath, existingPath);
                throw;
            }

            TryDeletePath(backupPath);
            return new ModEntry
            {
                Name = downloadedMod.Name,
                ModId = downloadedMod.ModId,
                Version = downloadedMod.Version,
                Side = downloadedMod.Side,
                FilePath = destinationPath,
                ConfigPath = downloadedMod.ConfigPath,
                Status = downloadedMod.Status,
                IsDisabled = installedMod.IsDisabled,
                Dependencies = downloadedMod.Dependencies,
                DependencyIssues = downloadedMod.DependencyIssues
            };
        }
        finally
        {
            TryDeletePath(tempPath);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModEntry>> ImportModsAsync(
        InstanceProfile profile,
        IReadOnlyCollection<string> sourcePaths,
        CancellationToken cancellationToken = default)
    {
        if (sourcePaths is null || sourcePaths.Count == 0)
            return [];

        var candidates = DiscoverModSources(sourcePaths, cancellationToken);
        if (candidates.Count == 0)
            return [];

        var modsPath = WorkspacePathHelper.GetProfileModsPath(profile.DirectoryPath);
        Directory.CreateDirectory(modsPath);
        var disabledSet = await LoadDisabledModSetAsync(profile, cancellationToken);
        var modConfigPath = Path.Combine(WorkspacePathHelper.ResolveProfileDataPath(profile.DirectoryPath), "ModConfig");
        var imported = new List<ModEntry>(candidates.Count);

        foreach (var sourcePath in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(modsPath, WorkspacePathHelper.SanitizeFileName(Path.GetFileName(sourcePath)));
            if (!PathsEqual(sourcePath, destination))
                File.Copy(sourcePath, destination, overwrite: true);
            imported.Add(ReadModFromZip(destination, disabledSet, modConfigPath));
        }

        return imported;
    }

    /// <inheritdoc />
    public async Task SetModEnabledAsync(
        InstanceProfile profile,
        string modId,
        string version,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var rawJson = await serverConfigService.LoadRawJsonAsync(profile, cancellationToken);
        var root = JsonNode.Parse(rawJson) as JsonObject
                   ?? throw new InvalidOperationException("配置格式错误。");

        var disabledArray = GetOrCreateDisabledModsArray(root);
        var modVersionKey = $"{modId}@{version}";

        var values = disabledArray
            .Where(static item => item is not null)
            .Select(static item => item!.GetValue<string>())
            .ToList();
        values.RemoveAll(value =>
            value.Equals(modId, StringComparison.OrdinalIgnoreCase) ||
            value.Equals(modVersionKey, StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith(modId + "@", StringComparison.OrdinalIgnoreCase));

        if (!enabled) values.Add(modVersionKey);

        disabledArray.Clear();
        foreach (var value in values.Distinct(StringComparer.OrdinalIgnoreCase))
            disabledArray.Add(value);

        await serverConfigService.SaveRawJsonAsync(
            profile,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> DeleteModsAsync(
        InstanceProfile profile,
        IReadOnlyCollection<ModEntry> mods,
        CancellationToken cancellationToken = default)
    {
        if (mods.Count == 0) return 0;

        var modsRoot = EnsureDirectoryPrefix(WorkspacePathHelper.GetProfileModsPath(profile.DirectoryPath));
        var deleted = 0;
        var deletedModIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deletedVersionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods.Where(static mod => mod is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(mod.FilePath);
            }
            catch
            {
                continue;
            }

            if (!IsWithinDirectory(fullPath, modsRoot))
                continue;

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
            else if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
            else
            {
                continue;
            }

            deleted++;
            deletedModIds.Add(mod.ModId);
            deletedVersionKeys.Add($"{mod.ModId}@{mod.Version}");
        }

        if (deleted > 0)
            await RemoveDeletedModsFromDisabledListAsync(
                profile,
                deletedModIds,
                deletedVersionKeys,
                cancellationToken);

        return deleted;
    }

    private static ModEntry ReadModFromZip(string zipPath, HashSet<string> disabledSet, string modConfigPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var modInfo = archive.Entries.FirstOrDefault(entry =>
                entry.FullName.EndsWith("modinfo.json", StringComparison.OrdinalIgnoreCase));
            if (modInfo is null)
                return BuildFallbackEntry(zipPath, "InvalidMetadata", disabledSet, modConfigPath);

            using var stream = modInfo.Open();
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            return BuildEntryFromModInfo(json, zipPath, disabledSet, modConfigPath);
        }
        catch
        {
            return BuildFallbackEntry(zipPath, "InvalidMetadata", disabledSet, modConfigPath);
        }
    }

    private static ModEntry ReadModFromDirectory(string directoryPath, HashSet<string> disabledSet, string modConfigPath)
    {
        try
        {
            var modInfoPath = Path.Combine(directoryPath, "modinfo.json");
            if (!File.Exists(modInfoPath))
                return BuildFallbackEntry(directoryPath, "InvalidMetadata", disabledSet, modConfigPath);

            var json = File.ReadAllText(modInfoPath);
            return BuildEntryFromModInfo(json, directoryPath, disabledSet, modConfigPath);
        }
        catch
        {
            return BuildFallbackEntry(directoryPath, "InvalidMetadata", disabledSet, modConfigPath);
        }
    }

    private static ModEntry BuildEntryFromModInfo(
        string modInfoJson,
        string filePath,
        HashSet<string> disabledSet,
        string modConfigPath)
    {
        // Vintage Story mods in the wild commonly contain a trailing comma or
        // comments. Be permissive for modinfo.json only; launcher-owned config
        // files continue to use their normal strict parsing rules.
        var node = JsonNode.Parse(modInfoJson, nodeOptions: null, documentOptions: ModInfoJsonOptions) as JsonObject;
        if (node is null)
            return BuildFallbackEntry(filePath, "InvalidMetadata", disabledSet, modConfigPath);

        var modId = ReadMetadataString(GetMetadataProperty(node, "modid")) ?? Path.GetFileNameWithoutExtension(filePath);
        var name = ReadMetadataString(GetMetadataProperty(node, "name"));
        var version = ReadMetadataString(GetMetadataProperty(node, "version")) ?? "unknown";
        var side = ReadMetadataString(GetMetadataProperty(node, "side"));
        var dependencies = ReadDependencies(GetMetadataProperty(node, "dependencies"));
        var disabled = disabledSet.Contains(modId) || disabledSet.Contains($"{modId}@{version}");

        return new ModEntry
        {
            Name = string.IsNullOrWhiteSpace(name) ? modId : name.Trim(),
            ModId = modId,
            Version = version,
            Side = NormalizeModSide(side),
            FilePath = filePath,
            ConfigPath = ResolveModConfigPath(modConfigPath, modId),
            Status = "OK",
            IsDisabled = disabled,
            Dependencies = dependencies,
            DependencyIssues = []
        };
    }

    private static ModEntry BuildFallbackEntry(
        string filePath,
        string status,
        HashSet<string> disabledSet,
        string modConfigPath)
    {
        var fallbackId = Path.GetFileNameWithoutExtension(filePath);
        return new ModEntry
        {
            Name = fallbackId,
            ModId = fallbackId,
            Version = "unknown",
            Side = "Universal",
            FilePath = filePath,
            ConfigPath = ResolveModConfigPath(modConfigPath, fallbackId),
            Status = status,
            IsDisabled = disabledSet.Contains(fallbackId),
            Dependencies = [],
            DependencyIssues = []
        };
    }

    private static IReadOnlyList<ModDependency> ReadDependencies(JsonNode? dependenciesNode)
    {
        var dependencies = new List<ModDependency>();
        switch (dependenciesNode)
        {
            case JsonObject dependencyObject:
                foreach (var pair in dependencyObject)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key)) continue;
                    dependencies.Add(new ModDependency
                    {
                        ModId = pair.Key,
                        Version = ReadMetadataString(pair.Value)
                    });
                }

                break;
            case JsonArray dependenciesArray:
                foreach (var dependencyNode in dependenciesArray)
                {
                    if (dependencyNode is not JsonObject dependencyItem) continue;
                    var modId = ReadMetadataString(GetMetadataProperty(dependencyItem, "modid"));
                    if (string.IsNullOrWhiteSpace(modId)) continue;

                    dependencies.Add(new ModDependency
                    {
                        ModId = modId,
                        Version = ReadMetadataString(GetMetadataProperty(dependencyItem, "version"))
                    });
                }

                break;
        }

        return dependencies;
    }

    private static string NormalizeModSide(string? side) => side?.Trim().ToLowerInvariant() switch
    {
        "client" => "Client",
        "server" => "Server",
        "universal" or "both" => "Universal",
        _ => "Universal"
    };

    private static List<string> DiscoverModSources(
        IEnumerable<string> sourcePaths,
        CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawPath in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(rawPath))
                continue;

            string path;
            try
            {
                path = Path.GetFullPath(rawPath.Trim());
            }
            catch
            {
                continue;
            }

            if (File.Exists(path))
            {
                if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase) && HasModInfoInZip(path))
                    AddCandidate(path, candidates, seen);
                continue;
            }

        }

        return candidates;
    }

    private static bool HasModInfoInZip(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            return archive.Entries.Any(entry =>
                entry.FullName.TrimEnd('/').EndsWith("modinfo.json", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static void AddCandidate(string path, List<string> candidates, HashSet<string> seen)
    {
        var fullPath = Path.GetFullPath(path);
        if (seen.Add(fullPath))
            candidates.Add(fullPath);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static JsonNode? GetMetadataProperty(JsonObject node, string propertyName)
    {
        foreach (var property in node)
        {
            if (property.Key.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string? ReadMetadataString(JsonNode? value)
    {
        if (value is null)
            return null;

        return value switch
        {
            JsonValue jsonValue when jsonValue.TryGetValue<string>(out var text) => text,
            JsonValue jsonValue when jsonValue.TryGetValue<int>(out var integer) => integer.ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonValue jsonValue when jsonValue.TryGetValue<long>(out var longValue) => longValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonValue jsonValue when jsonValue.TryGetValue<double>(out var number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonValue jsonValue when jsonValue.TryGetValue<bool>(out var boolean) => boolean ? "true" : "false",
            _ => null
        };
    }

    private static string ResolveModConfigPath(string modConfigPath, string modId)
    {
        try
        {
            var fullModConfigPath = Path.GetFullPath(modConfigPath);
            if (!Directory.Exists(fullModConfigPath))
            {
                return fullModConfigPath;
            }

            var candidates = Directory
                .EnumerateFileSystemEntries(fullModConfigPath, "*", SearchOption.TopDirectoryOnly)
                .Where(path => IsModConfigMatch(path, modId))
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToList();

            return candidates.Count switch
            {
                0 => fullModConfigPath,
                1 => candidates[0],
                _ => string.Join(" | ", candidates)
            };
        }
        catch
        {
            return modConfigPath;
        }
    }

    private static bool IsModConfigMatch(string candidatePath, string modId)
    {
        var candidateName = Path.GetFileNameWithoutExtension(candidatePath);
        if (string.IsNullOrWhiteSpace(candidateName)) return false;

        var normalizedCandidate = NormalizeConfigToken(candidateName);
        foreach (var token in EnumerateConfigMatchTokens(modId))
        {
            if (normalizedCandidate.Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (normalizedCandidate.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                token.Contains(normalizedCandidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeConfigToken(string value)
    {
        return new string(value
            .Where(static ch => char.IsLetterOrDigit(ch))
            .ToArray());
    }

    private static IEnumerable<string> EnumerateConfigMatchTokens(string modId)
    {
        yield return NormalizeConfigToken(modId);

        if (!ModConfigAliases.TryGetValue(modId, out var aliases))
        {
            yield break;
        }

        foreach (var alias in aliases)
        {
            if (string.IsNullOrWhiteSpace(alias))
            {
                continue;
            }

            yield return NormalizeConfigToken(alias);
        }
    }

    private async Task<HashSet<string>> LoadDisabledModSetAsync(InstanceProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            var rawJson = await serverConfigService.LoadRawJsonAsync(profile, cancellationToken);
            var root = JsonNode.Parse(rawJson) as JsonObject;
            var disabledArray = root?["WorldConfig"]?["DisabledMods"] as JsonArray;
            if (disabledArray is null) return [];

            return disabledArray
                .Where(static item => item is not null)
                .Select(static item => item!.GetValue<string>())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return [];
        }
    }

    private static JsonArray GetOrCreateDisabledModsArray(JsonObject root)
    {
        if (root["WorldConfig"] is not JsonObject worldConfig)
        {
            worldConfig = new JsonObject();
            root["WorldConfig"] = worldConfig;
        }

        if (worldConfig["DisabledMods"] is JsonArray disabledMods) return disabledMods;

        disabledMods = new JsonArray();
        worldConfig["DisabledMods"] = disabledMods;
        return disabledMods;
    }

    private async Task RemoveDeletedModsFromDisabledListAsync(
        InstanceProfile profile,
        HashSet<string> deletedModIds,
        HashSet<string> deletedVersionKeys,
        CancellationToken cancellationToken)
    {
        if (deletedModIds.Count == 0) return;

        try
        {
            var rawJson = await serverConfigService.LoadRawJsonAsync(profile, cancellationToken);
            var root = JsonNode.Parse(rawJson) as JsonObject
                       ?? throw new InvalidOperationException("配置格式错误。");

            var disabledArray = GetOrCreateDisabledModsArray(root);
            var originalValues = disabledArray
                .Where(static item => item is not null)
                .Select(static item => item!.GetValue<string>())
                .ToList();

            var cleaned = originalValues
                .Where(value =>
                {
                    if (deletedModIds.Contains(value)) return false;
                    return !deletedVersionKeys.Contains(value);
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (cleaned.Count == originalValues.Count) return;

            disabledArray.Clear();
            foreach (var value in cleaned)
                disabledArray.Add(value);

            await serverConfigService.SaveRawJsonAsync(
                profile,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
        }
        catch
        {
            // 删除模组已完成；配置清理失败时不阻断主流程。
        }
    }

    private static string EnsureDirectoryPrefix(string path)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath + Path.DirectorySeparatorChar;
    }

    private static bool IsWithinDirectory(string candidatePath, string directoryPrefix)
    {
        var fullCandidate = Path.GetFullPath(candidatePath);
        return fullCandidate.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void MoveToBackup(string sourcePath, string backupPath)
    {
        if (File.Exists(sourcePath))
        {
            File.Move(sourcePath, backupPath);
            return;
        }

        Directory.Move(sourcePath, backupPath);
    }

    private static void TryRestoreBackup(string backupPath, string destinationPath)
    {
        try
        {
            if (File.Exists(backupPath))
                File.Move(backupPath, destinationPath);
            else if (Directory.Exists(backupPath))
                Directory.Move(backupPath, destinationPath);
        }
        catch
        {
            // Preserve the original update error; a leftover backup is preferable to data loss.
        }
    }

    private static void TryDeletePath(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            else if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Cleanup is best effort and must not hide the update result.
        }
    }

    private static HttpClient CreateUpdateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LauncherGo/1.0");
        return client;
    }
}

