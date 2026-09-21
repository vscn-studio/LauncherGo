using System.Diagnostics;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

/// <summary>Manages only the detached voice web host. It never owns or controls a game server.</summary>
public sealed class VoiceWebService : IVoiceWebService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly string hostPath;
    private readonly string? runtimeRoot;
    private string RuntimeRoot => runtimeRoot ?? WorkspacePathHelper.RuntimeRoot;

    public VoiceWebService() : this(DefaultHostPath(), null) { }
    private static string DefaultHostPath()
    {
        var name = OperatingSystem.IsWindows() ? "LauncherGo.VoiceHost.exe" : "LauncherGo.VoiceHost";
        var published = Path.Combine(AppContext.BaseDirectory, "VoiceHost", name);
        return File.Exists(published) ? published : Path.Combine(AppContext.BaseDirectory, name);
    }
    internal VoiceWebService(string hostPath, string? runtimeRoot) { this.hostPath = hostPath; this.runtimeRoot = runtimeRoot; }
    private string RuntimeDirectory(InstanceProfile profile) => Path.Combine(RuntimeRoot, "voice-web", WorkspacePathHelper.SanitizeFileName(profile.Id));
    private static string DataPath(InstanceProfile profile) => WorkspacePathHelper.ResolveProfileDataPath(profile.DirectoryPath);
    private static string SettingsPath(InstanceProfile profile) => Path.Combine(DataPath(profile), "ModConfig", "launchergo-voice.json");

    public VoiceSettings LoadSettings(InstanceProfile profile)
    {
        var path = SettingsPath(profile);
        return File.Exists(path) ? JsonSerializer.Deserialize<VoiceSettings>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("语音配置为空。") : new VoiceSettings();
    }

    public string GetUrl(VoiceSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.PublicUrl))
        {
            if (!Uri.TryCreate(settings.PublicUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
                throw new InvalidOperationException("公开 URL 必须是 HTTP/HTTPS 地址。");
            return uri.AbsoluteUri;
        }
        var host = settings.ListenAddress is "0.0.0.0" or "::" ? "127.0.0.1" : settings.ListenAddress;
        return new UriBuilder(settings.UseHttps ? "https" : "http", host, settings.ListenPort).Uri.AbsoluteUri;
    }

    public void SaveSettings(InstanceProfile profile, VoiceSettings settings)
    {
        Validate(settings);
        _ = GetUrl(settings);
        // The mod keeps a separate loopback endpoint. Stopping/disabling the web host never disables the mod.
        var path = SettingsPath(profile);
        var modPath = Path.Combine(DataPath(profile), "ModConfig", "SimpleVoiceChat.Server.json");
        var mod = File.Exists(modPath) ? JsonNode.Parse(File.ReadAllText(modPath)) as JsonObject
            ?? throw new InvalidDataException("SimpleVoiceChat 配置格式无效。") : new JsonObject();
        mod["EnableWebMicrophone"] = true;
        mod["WebMicrophoneBindAddress"] = "127.0.0.1";
        mod["WebMicrophonePort"] = settings.BackendPort;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(modPath) && !File.Exists(modPath + ".voice-web.bak")) File.Copy(modPath, modPath + ".voice-web.bak");
        WriteAtomic(modPath, mod.ToJsonString(JsonOptions));
        WriteAtomic(path, JsonSerializer.Serialize(settings, JsonOptions));
    }

    private static void WriteAtomic(string path, string content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, content); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void Validate(VoiceSettings settings)
    {
        if (!IPAddress.TryParse(settings.ListenAddress, out _)) throw new InvalidOperationException("监听地址必须是 IPv4 或 IPv6 地址。");
        if (settings.ListenPort is < 1024 or > 65535 || settings.BackendPort is < 1024 or > 65535)
            throw new InvalidOperationException("网页和模组端口必须在 1024–65535 之间。");
        if (settings.ListenPort == settings.BackendPort) throw new InvalidOperationException("网页端口和模组接入端口不能相同。");
    }

    public Task<VoiceRuntimeStatus> StartAsync(InstanceProfile profile, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        var runtime = RuntimeDirectory(profile);
        using var control = await BackgroundHostFiles.AcquireControlAsync(runtime, cancellationToken);
        var existing = GetStatus(profile);
        if (existing.IsRunning) return existing;
        using (BackgroundHostFiles.AcquireHost(runtime)) { }
        var settings = LoadSettings(profile);
        Validate(settings);
        if (!settings.Enabled) throw new InvalidOperationException("请先启用语音网页。");
        if (!File.Exists(hostPath)) throw new FileNotFoundException("未找到独立语音网页服务，请更新 LauncherGo。", hostPath);
        if (BackgroundHostFiles.IsListening(settings.ListenAddress, settings.ListenPort))
            throw new InvalidOperationException($"网页端口 {settings.ListenPort} 已被占用。如果旧版 SimpleVoiceChat 占用此端口，请保存配置，将模组切换到 {settings.BackendPort}，然后在服务器页面重启游戏服务器一次。");
        if (settings.UseHttps)
        {
            using var cert = X509Certificate2.CreateFromPemFile(ResolvePath(profile, settings.CertificatePath), ResolvePath(profile, settings.PrivateKeyPath));
            if (!cert.HasPrivateKey || DateTime.Now < cert.NotBefore || DateTime.Now > cert.NotAfter)
                throw new InvalidOperationException("HTTPS 证书或私钥无效或已过期。");
        }
        DotNetRuntimeRequirement.EnsureForHost(hostPath);
        SaveSettings(profile, settings);
        var configPath = Path.Combine(runtime, "host.json");
        var stopPath = Path.Combine(runtime, "host.stop");
        var statePath = Path.Combine(runtime, "host.state.json");
        var snapshot = JsonSerializer.SerializeToNode(settings, JsonOptions)!;
        if (settings.UseHttps)
        {
            snapshot["CertificatePath"] = ResolvePath(profile, settings.CertificatePath);
            snapshot["PrivateKeyPath"] = ResolvePath(profile, settings.PrivateKeyPath);
        }
        await File.WriteAllTextAsync(configPath, snapshot.ToJsonString(), cancellationToken);
        if (File.Exists(stopPath)) File.Delete(stopPath);
        if (File.Exists(statePath)) File.Delete(statePath);
        using var prepared = ServerHostRuntimeStager.Prepare(hostPath, Path.Combine(RuntimeRoot, "voice-web-host"), cancellationToken);
        var start = new ProcessStartInfo(prepared.ExecutablePath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(prepared.ExecutablePath)! };
        foreach (var argument in new[] { "--config", configPath, "--stop", stopPath, "--state", statePath }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动语音网页服务。");
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException("语音网页启动失败：" + BackgroundHostFiles.Read<BackgroundHostState>(statePath)?.Error);
                var status = GetStatus(profile);
                if (status.ProcessId == process.Id && status.IsRunning && status.Error.Length == 0) return status;
                await Task.Delay(100, cancellationToken);
            }
            throw new TimeoutException("等待语音网页启动超时。");
        }
        catch
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            throw;
        }
    }, cancellationToken);

    public async Task StopAsync(InstanceProfile profile, CancellationToken cancellationToken = default)
    {
        var runtime = RuntimeDirectory(profile);
        using var control = await BackgroundHostFiles.AcquireControlAsync(runtime, cancellationToken);
        var state = BackgroundHostFiles.Read<BackgroundHostState>(Path.Combine(runtime, "host.state.json"));
        using var process = ResolveVoiceHost(state);
        if (process is null) return;
        await File.WriteAllTextAsync(Path.Combine(runtime, "host.stop"), "stop", cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
        catch (TimeoutException)
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(cancellationToken);
        }
    }

    // Only resolve our host executable, never a game process, even if a stale state file is present.
    private static Process? ResolveVoiceHost(BackgroundHostState? state) => state is null
        || !(Path.GetFileName(state.ExecutablePath).Equals("LauncherGo.VoiceHost.exe", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(state.ExecutablePath).Equals("LauncherGo.VoiceHost", StringComparison.OrdinalIgnoreCase))
        ? null : BackgroundHostFiles.ResolveProcess(state.ProcessId, state.ProcessStartTimeUtcTicks, state.ExecutablePath);

    public VoiceRuntimeStatus GetStatus(InstanceProfile profile)
    {
        var state = BackgroundHostFiles.Read<BackgroundHostState>(Path.Combine(RuntimeDirectory(profile), "host.state.json"));
        using var process = ResolveVoiceHost(state);
        if (process is not null)
            return new VoiceRuntimeStatus { ProfileId = profile.Id, ProcessId = process.Id, IsRunning = true, Url = state!.Url,
                Error = state.IsRunning && BackgroundHostFiles.IsFresh(state.HeartbeatUtc) && BackgroundHostFiles.IsListening(state.ListenAddress, state.ListenPort)
                    ? "" : "语音网页尚未就绪或心跳超时。" };
        return new VoiceRuntimeStatus { ProfileId = profile.Id, Url = GetUrl(LoadSettings(profile)), Error = state?.Error ?? "" };
    }

    private static string ResolvePath(InstanceProfile profile, string path) => string.IsNullOrWhiteSpace(path)
        ? throw new InvalidOperationException("请填写 HTTPS 证书和私钥路径。")
        : Path.GetFullPath(path, DataPath(profile));
}
