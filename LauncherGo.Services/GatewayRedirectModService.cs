using System.Text.Json;
using System.Text.Json.Nodes;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

/// <summary>
///     将内置重定向模组同步到与网关后端关联的本地实例。
/// </summary>
public sealed class GatewayRedirectModService(IInstanceServerConfigService serverConfigService) : IGatewayRedirectModService
{
    private const string ModId = "launchergoredirect";
    private const string ModVersion = "1.2.0";
    private const string ModFolderName = "launchergoredirect";
    private const string ModDllName = "launchergoredirect.dll";
    private const string ConfigFileName = "launchergoredirect.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<int> DeployAsync(
        TcpGatewaySettings settings,
        IReadOnlyList<InstanceProfile> profiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(profiles);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(settings.RedirectTicketSecret))
        {
            throw new InvalidOperationException("网关转移凭证密钥不可用，请先保存网关配置。");
        }

        var profilesById = profiles.ToDictionary(profile => profile.Id, StringComparer.OrdinalIgnoreCase);
        var backends = (settings.Backends ?? [])
            .Where(backend => !string.IsNullOrWhiteSpace(backend.ProfileId))
            .ToList();
        if (backends.Count == 0)
        {
            throw new InvalidOperationException("请先为至少一个后端关联 LauncherGo 本地实例。");
        }

        var unboundProfiles = backends
            .Where(backend => !profilesById.ContainsKey(backend.ProfileId))
            .Select(backend => backend.Name)
            .ToList();
        if (unboundProfiles.Count > 0)
        {
            throw new InvalidOperationException("以下后端关联的本地实例不存在：" + string.Join("、", unboundProfiles));
        }

        var sourceRoot = ResolveEmbeddedSourceRoot();
        var deployed = 0;
        foreach (var backend in backends)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profile = profilesById[backend.ProfileId];
            var modsPath = WorkspacePathHelper.GetProfileModsPath(profile.DirectoryPath);
            var destination = Path.Combine(modsPath, ModFolderName);
            SyncDirectory(sourceRoot, destination);

            var configPath = Path.Combine(
                WorkspacePathHelper.ResolveProfileDataPath(profile.DirectoryPath),
                "ModConfig",
                ConfigFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            var config = new GatewayRedirectModConfiguration
            {
                ServerId = backend.Id,
                TicketSecret = settings.RedirectTicketSecret,
                Routes = (settings.Backends ?? []).Select(item => new GatewayRedirectRoute
                {
                    ServerId = item.Id,
                    Name = string.IsNullOrWhiteSpace(item.Name) ? item.Id : item.Name
                }).ToList()
            };
            await File.WriteAllTextAsync(
                    configPath,
                    JsonSerializer.Serialize(config, JsonOptions),
                    cancellationToken)
                .ConfigureAwait(false);
            await SetModEnabledAsync(profile, cancellationToken).ConfigureAwait(false);
            deployed++;
        }

        return deployed;
    }

    private async Task SetModEnabledAsync(InstanceProfile profile, CancellationToken cancellationToken)
    {
        var rawJson = await serverConfigService.LoadRawJsonAsync(profile, cancellationToken).ConfigureAwait(false);
        var root = JsonNode.Parse(rawJson) as JsonObject
                   ?? throw new InvalidOperationException("服务端配置格式错误。");
        if (root["WorldConfig"] is not JsonObject worldConfig)
        {
            worldConfig = new JsonObject();
            root["WorldConfig"] = worldConfig;
        }

        if (worldConfig["DisabledMods"] is not JsonArray disabledMods)
        {
            disabledMods = new JsonArray();
            worldConfig["DisabledMods"] = disabledMods;
        }

        var retained = disabledMods
            .Where(static item => item is not null)
            .Select(static item => item!.GetValue<string>())
            .Where(item => !item.Equals(ModId, StringComparison.OrdinalIgnoreCase) &&
                           !item.StartsWith(ModId + "@", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        disabledMods.Clear();
        foreach (var item in retained)
        {
            disabledMods.Add(item);
        }

        await serverConfigService.SaveRawJsonAsync(
                profile,
                root.ToJsonString(JsonOptions),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string ResolveEmbeddedSourceRoot()
    {
        var primary = Path.Combine(AppContext.BaseDirectory, "EmbeddedMods", ModFolderName);
        if (Directory.Exists(primary)) return primary;

        var fallback = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LauncherGo.Services", "EmbeddedMods", ModFolderName));
        if (Directory.Exists(fallback)) return fallback;

        throw new InvalidOperationException($"未找到内置重定向模组文件：{primary}；{fallback}");
    }

    private static void SyncDirectory(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);
        var sourceFiles = Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories).ToList();
        var relativePaths = sourceFiles
            .Select(path => Path.GetRelativePath(sourcePath, path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sourceFiles)
        {
            var target = Path.Combine(destinationPath, Path.GetRelativePath(sourcePath, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try
            {
                File.Copy(source, target, overwrite: true);
            }
            catch (Exception ex) when (
                (ex is IOException or UnauthorizedAccessException) &&
                Path.GetFileName(target).Equals(ModDllName, StringComparison.OrdinalIgnoreCase))
            {
                // A running server can lock the DLL. The active version remains until its next restart.
            }
        }

        foreach (var target in Directory.EnumerateFiles(destinationPath, "*", SearchOption.AllDirectories))
        {
            if (relativePaths.Contains(Path.GetRelativePath(destinationPath, target))) continue;
            try
            {
                File.Delete(target);
            }
            catch (IOException)
            {
                // Keep locked obsolete files; deployment of the remaining files still succeeds.
            }
        }
    }

    private sealed class GatewayRedirectModConfiguration
    {
        public string ServerId { get; set; } = string.Empty;

        public string TicketSecret { get; set; } = string.Empty;

        public List<GatewayRedirectRoute> Routes { get; set; } = [];
    }

    private sealed class GatewayRedirectRoute
    {
        public string ServerId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

    }
}
