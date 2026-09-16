using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LauncherGo.Services;

public sealed partial class ServerMapService : IServerMapService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly object gate = new();
    private readonly Dictionary<string, Process> processes = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim webUpdateGate = new(1, 1);
    private readonly string builtInWebRoot;
    private readonly string builtInMapMod;
    private readonly string? mapRuntimeRoot;
    private string MapRuntimeRoot => mapRuntimeRoot ?? WorkspacePathHelper.RuntimeRoot;
    private readonly string hostExecutable;
    private readonly ILogger<ServerMapService> logger;
    private readonly HttpClient progressClient;

    public ServerMapService(ILogger<ServerMapService>? logger = null) : this(Path.Combine(AppContext.BaseDirectory, "WebRoot"), logger) { }
    internal ServerMapService(string builtInWebRoot, ILogger<ServerMapService>? logger = null, HttpClient? progressClient = null,
        string? mapModPackage = null, string? runtimeRoot = null, string? hostPath = null)
    {
        this.builtInWebRoot = builtInWebRoot;
        builtInMapMod = mapModPackage ?? Path.Combine(AppContext.BaseDirectory, "EmbeddedMods", "servermap", "servermap.zip");
        mapRuntimeRoot = runtimeRoot;
        hostExecutable = hostPath ?? Path.Combine(AppContext.BaseDirectory, "LauncherGo.ServerMapHost.exe");
        this.progressClient = progressClient ?? ProgressClient;
        this.logger = logger ?? NullLogger<ServerMapService>.Instance;
    }

    private static readonly HttpClient ProgressClient = new() { Timeout = TimeSpan.FromSeconds(3) };
    public async Task<ServerMapRenderProgress?> GetRenderProgressAsync(InstanceProfile profile, CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(profile, cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{settings.BackendPort}/api/v1/render-progress");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.BackendToken);
        using var response = await progressClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<ServerMapRenderProgress>(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), JsonOptions);
    }

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
    internal static void ValidateMapModPackage(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        foreach (var name in new[] { "LICENSE.txt", "THIRD_PARTY_NOTICES.txt", "VS-LiveMap-Revival-LICENSE.txt" })
            if (archive.GetEntry(name) is not { Length: > 0 })
                throw new InvalidDataException($"内置 ServerMap 模组包缺少版权文件 {name}，请重新构建或更新 LauncherGo。");
    }
    private string? ResolveRunningDefaultWebRoot(InstanceProfile profile)
    {
        var statePath = Path.Combine(RuntimeDirectory(profile.Id), "host.state.json");
        var state = BackgroundHostFiles.Read<BackgroundHostState>(statePath);
        if (state is null)
            return null;

        using var process = BackgroundHostFiles.ResolveProcess(
            state.ProcessId, state.ProcessStartTimeUtcTicks, state.ExecutablePath);
        if (process is null || string.IsNullOrWhiteSpace(state.ExecutablePath))
            return null;

        var hostDirectory = Path.GetDirectoryName(Path.GetFullPath(state.ExecutablePath));
        if (string.IsNullOrWhiteSpace(hostDirectory))
            return null;
        var webRoot = Path.Combine(hostDirectory, "WebRoot");
        // A missing homepage is a reason to reset, not evidence that the Host is stopped.
        return webRoot;
    }

    internal static Task<int> CopyWebRootAsync(string sourceDirectory, string targetDirectory, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
            ValidateWebDirectories(source, target);
            if (!File.Exists(Path.Combine(source, "index.html")))
                throw new FileNotFoundException("内置地图网页不存在，请重新安装 LauncherGo。 ", Path.Combine(source, "index.html"));

            var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories)
                .OrderBy(file => Path.GetRelativePath(source, file).Equals("index.html", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ToArray();
            foreach (var file in files)
            {
                RejectLinkedPath(file);
                RejectLinkedPath(Path.Combine(target, Path.GetRelativePath(source, file)));
            }
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.Combine(target, Path.GetRelativePath(source, file));
                var directory = Path.GetDirectoryName(destination)!;
                Directory.CreateDirectory(directory);
                var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
                try
                {
                    if (Path.GetRelativePath(source, file).Equals("index.html", StringComparison.OrdinalIgnoreCase))
                    {
                        var html = await File.ReadAllTextAsync(file, cancellationToken);
                        await File.WriteAllTextAsync(temporary, ServerMapWebAssets.VersionHtml(html, source), cancellationToken);
                    }
                    else
                    {
                        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
                        await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
                        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    }
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

    private string RuntimeDirectory(string profileId) =>
        Path.Combine(MapRuntimeRoot, "server-map", WorkspacePathHelper.SanitizeFileName(profileId));

    public Task<ServerMapRuntimeStatus> StartAsync(InstanceProfile profile, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var stages = new MapLifecycleLog(logger, profile.Id, "start");
            try
            {
                var result = await StartCoreAsync(profile, stages, cancellationToken).ConfigureAwait(false);
                stages.Complete();
                return result;
            }
            catch (Exception error) { stages.Fail(error); throw; }
        }, cancellationToken);

    private async Task<ServerMapRuntimeStatus> StartCoreAsync(InstanceProfile profile, MapLifecycleLog stages, CancellationToken cancellationToken)
    {
        stages.Stage("wait-control-lock");
        var runtime = RuntimeDirectory(profile.Id);
        using var control = await BackgroundHostFiles.AcquireControlAsync(runtime, cancellationToken);
        stages.Stage("check-existing-host");
        var existing = GetStatus(profile);
        if (existing.IsRunning) return existing;

        // Refuse to replace config/state if a Host is starting or its state is temporarily unavailable.
        using (BackgroundHostFiles.AcquireHost(runtime)) { }
        stages.Stage("load-settings-and-certificate");
        var settings = await LoadSettingsAsync(profile, cancellationToken);
        if (!settings.Enabled) throw new InvalidOperationException("请先启用服务器地图。");
        if (!string.IsNullOrWhiteSpace(settings.WebRoot) && !File.Exists(Path.Combine(settings.WebRoot, "index.html")))
            throw new InvalidOperationException("自定义 WebRoot 缺少 index.html，请先重置网页。 / Custom WebRoot has no index.html; reset web files first.");
        if (settings.UseHttps && !await ValidateCertificateAsync(profile, settings, cancellationToken))
            throw new InvalidOperationException("HTTPS 证书或私钥无效、已过期，或两者不匹配。");
        var sourceHost = hostExecutable;
        stages.Stage("check-dotnet-runtime");
        DotNetRuntimeRequirement.EnsureForHost(sourceHost);
        stages.Stage("write-host-config");
        var runtimeConfig = Path.Combine(runtime, "host.json");
        var stop = Path.Combine(runtime, "host.stop");
        var statePath = Path.Combine(runtime, "host.state.json");
        if (File.Exists(stop)) File.Delete(stop);
        if (File.Exists(statePath)) File.Delete(statePath);
        // Resolve certificates relative to the profile before writing the detached Host configuration.
        var snapshot = JsonSerializer.SerializeToNode(settings, JsonOptions)!;
        if (settings.UseHttps)
        {
            snapshot["CertificatePath"] = ResolveProfilePath(profile, settings.CertificatePath);
            snapshot["PrivateKeyPath"] = ResolveProfilePath(profile, settings.PrivateKeyPath);
        }
        await File.WriteAllTextAsync(runtimeConfig, snapshot.ToJsonString(), cancellationToken);
        stages.Stage("enumerate-web-files");
        var webFiles = Directory.Exists(builtInWebRoot)
            ? Directory.GetFiles(builtInWebRoot, "*", SearchOption.AllDirectories) : [];
        using var prepared = await Task.Run(() => ServerHostRuntimeStager.Prepare(
            sourceHost, Path.Combine(MapRuntimeRoot, "server-map-host"), cancellationToken, webFiles,
            (step, _) => stages.Stage("prepare-host/" + step)), cancellationToken);
        stages.Stage("launch-host");
        cancellationToken.ThrowIfCancellationRequested();
        var hostPath = prepared.ExecutablePath;
        if (!File.Exists(hostPath)) throw new FileNotFoundException("LauncherGo.ServerMapHost 未部署。", hostPath);
        var start = new ProcessStartInfo(hostPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(hostPath)!
        };
        start.ArgumentList.Add("--config"); start.ArgumentList.Add(runtimeConfig);
        start.ArgumentList.Add("--stop"); start.ArgumentList.Add(stop);
        start.ArgumentList.Add("--state"); start.ArgumentList.Add(statePath);
        var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 ServerMap Host。");
        try
        {
            stages.Stage("wait-listening");
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                    throw new InvalidOperationException($"ServerMap Host 启动失败，退出码 {process.ExitCode}。 " +
                        BackgroundHostFiles.Read<BackgroundHostState>(statePath)?.Error);
                var status = GetStatus(profile);
                if (status.IsRunning && status.ProcessId == process.Id && string.IsNullOrEmpty(status.Error))
                {
                    lock (gate)
                    {
                        if (processes.Remove(profile.Id, out var previous)) previous.Dispose();
                        processes[profile.Id] = process;
                    }
                    return status;
                }
                await Task.Delay(100, cancellationToken);
            }
            throw new TimeoutException("等待 ServerMap Host 监听端口超时。");
        }
        catch
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            process.Dispose();
            throw;
        }
    }

    public Task StopAsync(InstanceProfile profile, CancellationToken cancellationToken = default) =>
        StopProfileAsync(profile.Id, cancellationToken);

    private Task StopProfileAsync(string profileId, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            var stages = new MapLifecycleLog(logger, profileId, "stop");
            try
            {
                await StopCoreAsync(profileId, stages, cancellationToken).ConfigureAwait(false);
                stages.Complete();
            }
            catch (Exception error) { stages.Fail(error); throw; }
        }, cancellationToken);

    private async Task StopCoreAsync(string profileId, MapLifecycleLog stages, CancellationToken cancellationToken)
    {
        stages.Stage("wait-control-lock");
        var runtime = RuntimeDirectory(profileId);
        using var control = await BackgroundHostFiles.AcquireControlAsync(runtime, cancellationToken);
        stages.Stage("resolve-host-process");
        var state = BackgroundHostFiles.Read<BackgroundHostState>(Path.Combine(runtime, "host.state.json"));
        using var process = state is null ? null : BackgroundHostFiles.ResolveProcess(
            state.ProcessId, state.ProcessStartTimeUtcTicks, state.ExecutablePath);
        if (process is null) return;
        stages.Stage("send-stop-signal");
        await File.WriteAllTextAsync(Path.Combine(runtime, "host.stop"), "stop", cancellationToken);
        stages.Stage("wait-graceful-exit");
        try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
        catch (TimeoutException)
        {
            logger.LogWarning("Map Host graceful stop exceeded 5 seconds; forcing exit. ProfileId={ProfileId}.", profileId);
            stages.Stage("force-exit");
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(cancellationToken);
        }
        lock (gate) { if (processes.Remove(profileId, out var previous)) previous.Dispose(); }
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(MapRuntimeRoot, "server-map");
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.GetDirectories(root))
            await StopProfileAsync(Path.GetFileName(directory), cancellationToken);
    }

    public ServerMapRuntimeStatus GetStatus(InstanceProfile profile)
    {
        var state = BackgroundHostFiles.Read<BackgroundHostState>(Path.Combine(RuntimeDirectory(profile.Id), "host.state.json"));
        using var process = state is null ? null : BackgroundHostFiles.ResolveProcess(
            state.ProcessId, state.ProcessStartTimeUtcTicks, state.ExecutablePath);
        if (process is not null)
        {
            var healthy = state!.IsRunning && BackgroundHostFiles.IsFresh(state.HeartbeatUtc) &&
                BackgroundHostFiles.IsListening(state.ListenAddress, state.ListenPort);
            return new ServerMapRuntimeStatus
            {
                ProfileId = profile.Id, IsRunning = true, ProcessId = process.Id, Url = state.Url,
                Error = healthy ? "" : "地图 Host 尚未就绪或心跳超时，请检查后停止并重新启动。"
            };
        }
        var settings = BackgroundHostFiles.Read<ServerMapSettings>(GetSettingsPath(profile)) ?? new();
        return new ServerMapRuntimeStatus { ProfileId = profile.Id, Url = BuildUrl(settings), Error = state?.Error ?? "" };
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
