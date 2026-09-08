using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

public sealed class ServerMapService : IServerMapService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly object gate = new();
    private readonly Dictionary<string, Process> processes = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim webUpdateGate = new(1, 1);
    private readonly string builtInWebRoot;

    public ServerMapService() : this(Path.Combine(AppContext.BaseDirectory, "WebRoot")) { }
    internal ServerMapService(string builtInWebRoot) => this.builtInWebRoot = builtInWebRoot;

    public string GetProfileDirectory(InstanceProfile profile) =>
        Path.Combine(WorkspacePathHelper.ResolveProfileDataPath(profile.DirectoryPath), "ServerMap");

    public async Task<ServerMapSettings> LoadSettingsAsync(InstanceProfile profile, CancellationToken cancellationToken = default)
    {
        var path = GetSettingsPath(profile);
        if (!File.Exists(path))
        {
            var legacy = Path.Combine(WorkspacePathHelper.ResolveProfileDataPath(profile.DirectoryPath), "ServerMap", "servermap.json");
            if (File.Exists(legacy))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(legacy, path, true);
                File.Copy(legacy, legacy + ".launchergo.bak", true);
            }
        }
        if (File.Exists(path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<ServerMapSettings>(await File.ReadAllTextAsync(path, cancellationToken), JsonOptions);
                if (loaded is not null) return Normalize(profile, loaded);
            }
            catch (JsonException) { }
        }
        var settings = Normalize(profile, new ServerMapSettings());
        await SaveSettingsAsync(profile, settings, cancellationToken);
        return settings;
    }

    public async Task SaveSettingsAsync(InstanceProfile profile, ServerMapSettings settings, CancellationToken cancellationToken = default)
    {
        settings = Normalize(profile, settings);
        var directory = GetProfileDirectory(profile);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(GetSettingsPath(profile), JsonSerializer.Serialize(settings, JsonOptions), cancellationToken);
        await WriteModConfigurationAsync(profile, settings, cancellationToken);
    }

    public async Task EnsureMapModDeployedAsync(InstanceProfile profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = Path.Combine(AppContext.BaseDirectory, "EmbeddedMods", "servermap", "servermap.zip");
        if (!File.Exists(source)) throw new FileNotFoundException("内置 ServerMap 模组包不存在。", source);
        var mods = WorkspacePathHelper.GetProfileModsPath(profile.DirectoryPath);
        Directory.CreateDirectory(mods);
        var target = Path.Combine(mods, "servermap.zip");
        await using var input = File.OpenRead(source);
        await using var output = File.Create(target);
        await input.CopyToAsync(output, cancellationToken);
    }

    public async Task<int> UpdateWebRootAsync(InstanceProfile profile, ServerMapSettings settings, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.WebRoot))
            throw new InvalidOperationException("请先选择自定义 WebRoot 目录。 ");
        settings = Normalize(profile, settings);
        await webUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var count = await CopyWebRootAsync(builtInWebRoot, settings.WebRoot, cancellationToken).ConfigureAwait(false);
            await SaveSettingsAsync(profile, settings, cancellationToken).ConfigureAwait(false);
            return count;
        }
        finally { webUpdateGate.Release(); }
    }

    internal static Task<int> CopyWebRootAsync(string sourceDirectory, string targetDirectory, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
            if (target.Equals(Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase) ||
                source.Equals(target, StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                source.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("自定义 WebRoot 不能与内置网页目录重叠。 ");
            if (!File.Exists(Path.Combine(source, "index.html")))
                throw new FileNotFoundException("内置地图网页不存在，请重新安装 LauncherGo。 ", Path.Combine(source, "index.html"));

            var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories)
                .OrderBy(file => Path.GetRelativePath(source, file).Equals("index.html", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ToArray();
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.Combine(target, Path.GetRelativePath(source, file));
                var directory = Path.GetDirectoryName(destination)!;
                Directory.CreateDirectory(directory);
                var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
                try
                {
                    await using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous))
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    // Readers see either the previous complete asset or the new complete asset.
                    for (var attempt = 0; ; attempt++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try { File.Move(temporary, destination, overwrite: true); break; }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 3)
                        {
                            await Task.Delay(100 * (attempt + 1), cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
            return files.Length;
        }, cancellationToken);

    public async Task<ServerMapRuntimeStatus> StartAsync(InstanceProfile profile, CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(profile, cancellationToken);
        if (!settings.Enabled) throw new InvalidOperationException("请先启用服务器地图。 ");
        if (!string.IsNullOrWhiteSpace(settings.WebRoot) && !File.Exists(Path.Combine(settings.WebRoot, "index.html")))
            throw new InvalidOperationException("自定义 WebRoot 缺少 index.html，请先手动更新网页。 ");
        if (settings.UseHttps && !await ValidateCertificateAsync(profile, settings, cancellationToken))
            throw new InvalidOperationException("HTTPS 证书或私钥无效、已过期，或两者不匹配。 ");
        await EnsureMapModDeployedAsync(profile, cancellationToken);
        await StopAsync(profile, cancellationToken);

        var runtime = Path.Combine(WorkspacePathHelper.RuntimeRoot, "server-map", WorkspacePathHelper.SanitizeFileName(profile.Id));
        Directory.CreateDirectory(runtime);
        var runtimeConfig = Path.Combine(runtime, "host.json");
        var stop = Path.Combine(runtime, "host.stop");
        if (File.Exists(stop)) File.Delete(stop);
        await File.WriteAllTextAsync(runtimeConfig, JsonSerializer.Serialize(settings, JsonOptions), cancellationToken);

        var exe = Path.Combine(AppContext.BaseDirectory, "LauncherGo.ServerMapHost.exe");
        var dll = Path.Combine(AppContext.BaseDirectory, "LauncherGo.ServerMapHost.dll");
        var start = File.Exists(exe)
            ? new ProcessStartInfo(exe)
            : new ProcessStartInfo("dotnet", $"\"{dll}\"");
        if (!File.Exists(exe) && !File.Exists(dll)) throw new FileNotFoundException("LauncherGo.ServerMapHost 未部署。", dll);
        start.ArgumentList.Add("--config"); start.ArgumentList.Add(runtimeConfig);
        start.ArgumentList.Add("--stop"); start.ArgumentList.Add(stop);
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.WorkingDirectory = AppContext.BaseDirectory;
        var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 ServerMap Host。 ");
        lock (gate) processes[profile.Id] = process;
        await Task.Delay(350, cancellationToken);
        if (process.HasExited) throw new InvalidOperationException($"ServerMap Host 启动失败，退出码 {process.ExitCode}。 ");
        return GetStatus(profile);
    }

    public async Task StopAsync(InstanceProfile profile, CancellationToken cancellationToken = default)
    {
        Process? process;
        lock (gate) processes.Remove(profile.Id, out process);
        if (process is null || process.HasExited) return;
        var stop = Path.Combine(WorkspacePathHelper.RuntimeRoot, "server-map", WorkspacePathHelper.SanitizeFileName(profile.Id), "host.stop");
        Directory.CreateDirectory(Path.GetDirectoryName(stop)!);
        await File.WriteAllTextAsync(stop, string.Empty, cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
        catch (TimeoutException) { process.Kill(true); }
        finally { process.Dispose(); }
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        string[] ids;
        lock (gate) ids = processes.Keys.ToArray();
        foreach (var id in ids)
        {
            Process? process;
            lock (gate) processes.Remove(id, out process);
            if (process is null || process.HasExited) continue;
            var stop = Path.Combine(WorkspacePathHelper.RuntimeRoot, "server-map", WorkspacePathHelper.SanitizeFileName(id), "host.stop");
            Directory.CreateDirectory(Path.GetDirectoryName(stop)!);
            await File.WriteAllTextAsync(stop, string.Empty, cancellationToken);
            try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
            catch { if (!process.HasExited) process.Kill(true); }
            finally { process.Dispose(); }
        }
    }

    public ServerMapRuntimeStatus GetStatus(InstanceProfile profile)
    {
        Process? process;
        lock (gate) processes.TryGetValue(profile.Id, out process);
        var settings = File.Exists(GetSettingsPath(profile))
            ? JsonSerializer.Deserialize<ServerMapSettings>(File.ReadAllText(GetSettingsPath(profile)), JsonOptions) ?? new()
            : new ServerMapSettings();
        return new ServerMapRuntimeStatus
        {
            ProfileId = profile.Id,
            IsRunning = process is { HasExited: false },
            ProcessId = process is { HasExited: false } ? process.Id : 0,
            Url = BuildUrl(settings)
        };
    }

    public Task<bool> ValidateCertificateAsync(InstanceProfile profile, ServerMapSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!settings.UseHttps) return Task.FromResult(true);
        try
        {
            var certPath = ResolveProfilePath(profile, settings.CertificatePath);
            var keyPath = ResolveProfilePath(profile, settings.PrivateKeyPath);
            using var cert = X509Certificate2.CreateFromPemFile(certPath, keyPath);
            var now = DateTime.Now;
            using var key = cert.GetRSAPrivateKey();
            return Task.FromResult(cert.HasPrivateKey && key is not null && now >= cert.NotBefore && now <= cert.NotAfter);
        }
        catch { return Task.FromResult(false); }
    }

    private ServerMapSettings Normalize(InstanceProfile profile, ServerMapSettings value) => new()
    {
        Enabled = value.Enabled,
        ListenAddress = string.IsNullOrWhiteSpace(value.ListenAddress) ? "127.0.0.1" : value.ListenAddress.Trim(),
        ListenPort = value.ListenPort is > 0 and <= 65535 ? value.ListenPort : AllocatePort(profile.Id, 5081),
        UseHttps = value.UseHttps,
        CertificatePath = value.CertificatePath.Trim(),
        PrivateKeyPath = value.PrivateKeyPath.Trim(),
        WebRoot = string.IsNullOrWhiteSpace(value.WebRoot) ? string.Empty : ResolveProfilePath(profile, value.WebRoot.Trim()),
        BackendPort = value.BackendPort is > 0 and <= 65535 ? value.BackendPort : AllocatePort(profile.Id, 15080),
        BackendToken = string.IsNullOrWhiteSpace(value.BackendToken) ? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant() : value.BackendToken,
        PublicUrl = value.PublicUrl.Trim()
    };

    private async Task WriteModConfigurationAsync(InstanceProfile profile, ServerMapSettings settings, CancellationToken cancellationToken)
    {
        var path = Path.Combine(WorkspacePathHelper.ResolveProfileDataPath(profile.DirectoryPath), "ModConfig", "servermap.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && !File.Exists(path + ".launchergo.bak")) File.Copy(path, path + ".launchergo.bak");
        JsonObject root;
        try { root = File.Exists(path) ? JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken)) as JsonObject ?? new() : new(); }
        catch { root = new(); }
        root["Enabled"] = settings.Enabled;
        root["BindAddress"] = "127.0.0.1";
        root["Port"] = settings.BackendPort;
        root["Token"] = settings.BackendToken;
        await File.WriteAllTextAsync(path, root.ToJsonString(JsonOptions), cancellationToken);
    }

    private string GetSettingsPath(InstanceProfile profile) => Path.Combine(GetProfileDirectory(profile), "launchergo-map.json");
    private string ResolveProfilePath(InstanceProfile profile, string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(GetProfileDirectory(profile), path));
    private static int AllocatePort(string profileId, int start) => start + Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(profileId) % 1000);
    private static string BuildUrl(ServerMapSettings settings) => !string.IsNullOrWhiteSpace(settings.PublicUrl)
        ? settings.PublicUrl
        : $"{(settings.UseHttps ? "https" : "http")}://{(settings.ListenAddress is "0.0.0.0" or "::" ? "127.0.0.1" : settings.ListenAddress)}:{settings.ListenPort}/";
}
