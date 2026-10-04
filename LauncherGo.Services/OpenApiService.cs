using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LauncherGo.Services;

public sealed class OpenApiService(
    IServerProcessService serverProcessService,
    IInstanceServerConfigService serverConfigService,
    IInstanceModService modService,
    ILogger<OpenApiService>? logger = null) : IOpenApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient ModDbClient = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly ConcurrentDictionary<string, string> ModUrlCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, WebApplication> _applications = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _profileGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<OpenApiService>? _logger = logger;

    public bool IsRunning(string profileId) => _applications.ContainsKey(profileId);

    public int GetPort(InstanceProfile profile) => LoadSettings(profile).Port;

    public void SavePort(InstanceProfile profile, int port)
    {
        ValidatePort(port);
        SaveSettings(profile, LoadSettings(profile) with { Port = port });
    }

    public string? GetCoverPath(InstanceProfile profile)
    {
        var settings = LoadSettings(profile);
        var path = string.IsNullOrWhiteSpace(settings.CoverFileName)
            ? string.Empty
            : Path.Combine(profile.DirectoryPath, settings.CoverFileName);
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
    }

    public void SaveCover(InstanceProfile profile, string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("封面图片不存在。", sourcePath);
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        var contentType = extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => string.Empty
        };
        if (string.IsNullOrWhiteSpace(contentType))
            throw new InvalidOperationException("封面仅支持 JPG、PNG、GIF 或 WebP 图片。");

        Directory.CreateDirectory(profile.DirectoryPath);
        var fileName = $"openapi-cover{extension}";
        var destination = Path.Combine(profile.DirectoryPath, fileName);
        File.Copy(sourcePath, destination, overwrite: true);
        SaveSettings(profile, LoadSettings(profile) with { CoverFileName = fileName, CoverContentType = contentType });
    }

    public void ClearCover(InstanceProfile profile)
    {
        var settings = LoadSettings(profile);
        if (!string.IsNullOrWhiteSpace(settings.CoverFileName))
        {
            var path = Path.Combine(profile.DirectoryPath, settings.CoverFileName);
            if (File.Exists(path)) File.Delete(path);
        }
        SaveSettings(profile, settings with { CoverFileName = string.Empty, CoverContentType = string.Empty });
    }

    public async Task StartAsync(InstanceProfile profile, int port, CancellationToken cancellationToken = default)
    {
        ValidatePort(port);
        var gate = _profileGates.GetOrAdd(profile.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (_applications.ContainsKey(profile.Id))
                return;

            SaveSettings(profile, LoadSettings(profile) with { Port = port });
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Any, port));
            builder.Services.AddCors();
            builder.Services.Configure<JsonOptions>(options => options.SerializerOptions.PropertyNamingPolicy = JsonOptions.PropertyNamingPolicy);
            var app = builder.Build();
            app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
            app.MapGet("/", () => Results.Json(new
            {
                name = "LauncherGo Open API",
                endpoints = new[] { "/api", "/api/server", "/api/mods" }
            }, JsonOptions));
            app.MapGet("/api", async (CancellationToken token) => Results.Json(await BuildServerInfoAsync(profile, token), JsonOptions));
            app.MapGet("/api/server", async (CancellationToken token) => Results.Json(await BuildServerInfoAsync(profile, token), JsonOptions));
            app.MapGet("/api/mods", async (CancellationToken token) => Results.Json(await BuildModsAsync(profile, token), JsonOptions));
            app.MapGet("/api/cover", () =>
            {
                var coverPath = GetCoverPath(profile);
                return coverPath is null
                    ? Results.NotFound(new { error = "No server cover configured." })
                    : Results.File(coverPath, LoadSettings(profile).CoverContentType);
            });
            app.MapFallback(() => Results.NotFound(new { error = "Endpoint not found." }));

            try
            {
                await app.StartAsync(cancellationToken);
                _applications[profile.Id] = app;
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StopAsync(string profileId, CancellationToken cancellationToken = default)
    {
        if (!_applications.TryRemove(profileId, out var app))
            return;
        await app.StopAsync(cancellationToken);
        await app.DisposeAsync();
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var profileId in _applications.Keys)
            await StopAsync(profileId, cancellationToken);
    }

    private async Task<OpenApiServerInfo> BuildServerInfoAsync(InstanceProfile profile, CancellationToken cancellationToken)
    {
        var status = serverProcessService.GetCurrentStatus(profile.Id);
        var settings = await serverConfigService.LoadServerSettingsAsync(profile, cancellationToken);
        return new OpenApiServerInfo
        {
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            ServerName = settings.ServerName,
            Description = settings.ServerDescription ?? string.Empty,
            Version = profile.Version,
            IsRunning = status.IsRunning,
            OnlinePlayers = status.OnlinePlayers,
            StartedAtUtc = status.StartedAtUtc,
            UptimeSeconds = status.IsRunning && status.StartedAtUtc is { } startedAt
                ? Math.Max(0, (long)(DateTimeOffset.UtcNow - startedAt).TotalSeconds)
                : 0,
            CoverUrl = GetCoverPath(profile) is null ? string.Empty : "/api/cover",
            Mods = await BuildModsAsync(profile, cancellationToken)
        };
    }

    private async Task<IReadOnlyList<OpenApiModInfo>> BuildModsAsync(InstanceProfile profile, CancellationToken cancellationToken)
    {
        var mods = await modService.GetModsAsync(profile, cancellationToken);
        return await Task.WhenAll(mods.Where(static mod => !mod.IsDisabled)
            .OrderBy(static mod => mod.ModId, StringComparer.OrdinalIgnoreCase)
            .Select(async mod => new OpenApiModInfo
            {
                Name = mod.Name,
                ModId = mod.ModId,
                Url = await ResolveModUrlAsync(mod.ModId, cancellationToken)
            }));
    }

    private static async Task<string> ResolveModUrlAsync(string modId, CancellationToken cancellationToken)
    {
        modId = modId.Trim();
        if (string.IsNullOrWhiteSpace(modId))
            return string.Empty;
        if (ModUrlCache.TryGetValue(modId, out var cached))
            return cached;

        var fallback = $"https://mods.vintagestory.at/{Uri.EscapeDataString(modId)}";
        try
        {
            using var response = await ModDbClient.GetAsync($"https://mods.vintagestory.at/api/mod/{Uri.EscapeDataString(modId)}", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var root = document.RootElement;
                if (IsApiSuccess(root) && root.TryGetProperty("mod", out var mod))
                {
                    var alias = mod.TryGetProperty("urlalias", out var aliasValue) && aliasValue.ValueKind == JsonValueKind.String
                        ? aliasValue.GetString()?.Trim().Trim('/')
                        : null;
                    if (!string.IsNullOrWhiteSpace(alias))
                        return ModUrlCache.GetOrAdd(modId, $"https://mods.vintagestory.at/{Uri.EscapeDataString(alias)}");
                    if (mod.TryGetProperty("modid", out var numericId) && numericId.TryGetInt32(out var id) && id > 0)
                        return ModUrlCache.GetOrAdd(modId, $"https://mods.vintagestory.at/show/mod/{id}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
        }
        return ModUrlCache.GetOrAdd(modId, fallback);
    }

    private static bool IsApiSuccess(JsonElement root) => root.TryGetProperty("statuscode", out var status) &&
        ((status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var code) && code == 200) ||
         (status.ValueKind == JsonValueKind.String && status.GetString() == "200"));

    private static OpenApiSettings LoadSettings(InstanceProfile profile)
    {
        var path = GetSettingsPath(profile);
        try
        {
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<OpenApiSettings>(File.ReadAllText(path));
                return settings is { Port: >= 1 and <= 65535 } ? settings : new OpenApiSettings();
            }
        }
        catch (JsonException)
        {
        }
        return new OpenApiSettings();
    }

    private static void SaveSettings(InstanceProfile profile, OpenApiSettings settings)
    {
        Directory.CreateDirectory(profile.DirectoryPath);
        File.WriteAllText(GetSettingsPath(profile), JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string GetSettingsPath(InstanceProfile profile) => Path.Combine(profile.DirectoryPath, "openapi-settings.json");

    private static void ValidatePort(int port)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "端口必须在 1 到 65535 之间。");
    }

    private sealed record OpenApiSettings
    {
        public int Port { get; init; } = 8085;
        public string CoverFileName { get; init; } = string.Empty;
        public string CoverContentType { get; init; } = string.Empty;
    }
}

public sealed class OpenApiServerInfo
{
    public string ProfileId { get; init; } = string.Empty;
    public string ProfileName { get; init; } = string.Empty;
    public string ServerName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public bool IsRunning { get; init; }
    public int OnlinePlayers { get; init; }
    public DateTimeOffset? StartedAtUtc { get; init; }
    public long UptimeSeconds { get; init; }
    public string CoverUrl { get; init; } = string.Empty;
    public IReadOnlyList<OpenApiModInfo> Mods { get; init; } = [];
}

public sealed class OpenApiModInfo
{
    public string Name { get; init; } = string.Empty;
    public string ModId { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
}
