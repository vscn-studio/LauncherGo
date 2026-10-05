using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    IServerBridgeService? serverBridgeService = null,
    ILogger<OpenApiService>? logger = null) : IOpenApiService
{
    private const int PlayerCountHistoryHours = 24 * 7;
    private static readonly TimeSpan BridgeStatusCacheTtl = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BridgeHistoryCacheTtl = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient ModDbClient = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly ConcurrentDictionary<string, string> ModUrlCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, WebApplication> _applications = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _profileGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _playerHistoryCancellations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _playerHistoryTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, object> _playerHistoryGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedBridgeJson> _bridgeStatusCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedBridgeJson> _bridgeHistoryCache = new(StringComparer.OrdinalIgnoreCase);
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
                endpoints = new[] { "/api", "/api/server", "/api/mods", "/api/players/history", "/api/cover" }
            }, JsonOptions));
            app.MapGet("/api", async (CancellationToken token) => Results.Json(await BuildServerInfoAsync(profile, token), JsonOptions));
            app.MapGet("/api/server", async (CancellationToken token) => Results.Json(await BuildServerInfoAsync(profile, token), JsonOptions));
            app.MapGet("/api/mods", async (CancellationToken token) => Results.Json(await BuildModsAsync(profile, token), JsonOptions));
            app.MapGet("/api/players/history", async (CancellationToken token) =>
            {
                await RefreshPlayerHistoryFromBridgeAsync(profile, token);
                return Results.Json(GetPlayerCountHistory(profile), JsonOptions);
            });
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
                StartPlayerCountTracking(profile);
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
        await StopPlayerCountTrackingAsync(profileId);
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
        var localStatus = serverProcessService.GetCurrentStatus(profile.Id);
        var settings = await serverConfigService.LoadServerSettingsAsync(profile, cancellationToken);
        var bridgeStatus = await GetBridgeStatusAsync(profile, cancellationToken);
        var isRunning = bridgeStatus is not null
            ? !string.Equals(bridgeStatus["status"]?.GetValue<string>(), "shutting-down", StringComparison.OrdinalIgnoreCase)
            : localStatus.IsRunning;
        var uptimeSeconds = ReadLong(bridgeStatus, "uptimeSeconds") ?? (localStatus.IsRunning && localStatus.StartedAtUtc is { } startedAt
            ? Math.Max(0, (long)(DateTimeOffset.UtcNow - startedAt).TotalSeconds)
            : 0);
        var startedAtUtc = bridgeStatus is not null && uptimeSeconds > 0
            ? DateTimeOffset.UtcNow.AddSeconds(-uptimeSeconds)
            : localStatus.StartedAtUtc;
        await RefreshPlayerHistoryFromBridgeAsync(profile, cancellationToken);
        return new OpenApiServerInfo
        {
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            ServerName = ReadString(bridgeStatus, "name") ?? settings.ServerName,
            Description = ReadString(bridgeStatus, "description") ?? settings.ServerDescription ?? string.Empty,
            Version = ReadString(bridgeStatus, "version") ?? profile.Version,
            ServerStatus = ReadString(bridgeStatus, "status") ?? string.Empty,
            WorldName = ReadString(bridgeStatus, "worldName") ?? string.Empty,
            Address = ReadString(bridgeStatus, "address") ?? string.Empty,
            MaxPlayers = ReadInt(bridgeStatus, "maxPlayers") ?? 0,
            IsRunning = isRunning,
            OnlinePlayers = ReadInt(bridgeStatus, "onlinePlayers") ?? localStatus.OnlinePlayers,
            StartedAtUtc = startedAtUtc,
            UptimeSeconds = uptimeSeconds,
            CoverUrl = GetCoverPath(profile) is null ? string.Empty : "/api/cover",
            PlayerCountHistoryHours = PlayerCountHistoryHours,
            PlayerCountHistory = GetPlayerCountHistory(profile),
            Mods = await BuildModsAsync(profile, cancellationToken)
        };
    }

    private void StartPlayerCountTracking(InstanceProfile profile)
    {
        var cancellation = new CancellationTokenSource();
        if (!_playerHistoryCancellations.TryAdd(profile.Id, cancellation))
        {
            cancellation.Dispose();
            return;
        }
        var task = TrackPlayerCountAsync(profile, cancellation.Token);
        _playerHistoryTasks[profile.Id] = task;
    }

    private async Task StopPlayerCountTrackingAsync(string profileId)
    {
        if (_playerHistoryCancellations.TryRemove(profileId, out var cancellation))
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        if (_playerHistoryTasks.TryRemove(profileId, out var task))
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task TrackPlayerCountAsync(InstanceProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            if (serverBridgeService is null) throw new InvalidOperationException("服务器桥接服务不可用。");
            await RefreshPlayerHistoryFromBridgeAsync(profile, cancellationToken).ConfigureAwait(false);

            var current = await serverBridgeService.QueryAsync(profile, "server.status", cancellationToken: cancellationToken).ConfigureAwait(false);
            if (current.Success && ReadInt(current.Data, "onlinePlayers") is { } currentCount)
                RecordPlayerCount(profile, currentCount, DateTimeOffset.UtcNow);

            await using var subscription = await serverBridgeService.SubscribeAsync(
                profile,
                new ServerBridgeSubscriptionOptions { Events = ["player.count-changed"], StartFromLatest = true },
                value =>
                {
                    if (ReadInt(value.Data, "onlinePlayers") is { } count)
                        RecordPlayerCount(profile, count, value.TimestampUtc);
                    return Task.CompletedTask;
                }, cancellationToken).ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger?.LogDebug(exception, "桥接玩家数量跟踪不可用，使用当前状态作为兼容基线。ProfileId={ProfileId}", profile.Id);
            RecordPlayerCount(profile, Math.Max(0, serverProcessService.GetCurrentStatus(profile.Id).OnlinePlayers), DateTimeOffset.UtcNow);
        }
    }

    private async Task RefreshPlayerHistoryFromBridgeAsync(InstanceProfile profile, CancellationToken cancellationToken)
    {
        if (serverBridgeService is null) return;
        if (_bridgeHistoryCache.TryGetValue(profile.Id, out var cached) &&
            DateTimeOffset.UtcNow - cached.CreatedAtUtc < BridgeHistoryCacheTtl)
            return;
        try
        {
            var history = await serverBridgeService.QueryAsync(profile, "players.history", cancellationToken: cancellationToken).ConfigureAwait(false);
            if (history.Success && history.Data?["history"] is JsonArray values)
            {
                foreach (var value in values.OfType<JsonObject>())
                    if (ReadInt(value, "onlinePlayers") is { } count && ReadDateTime(value, "timestampUtc") is { } timestamp)
                        RecordPlayerCount(profile, count, timestamp);
                _bridgeHistoryCache[profile.Id] = new CachedBridgeJson(DateTimeOffset.UtcNow, history.Data.DeepClone().AsObject());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or SocketException or TimeoutException or InvalidOperationException)
        {
            _logger?.LogDebug(exception, "读取桥接玩家历史失败。ProfileId={ProfileId}", profile.Id);
        }
    }

    private async Task<JsonObject?> GetBridgeStatusAsync(InstanceProfile profile, CancellationToken cancellationToken)
    {
        if (serverBridgeService is null) return null;
        if (_bridgeStatusCache.TryGetValue(profile.Id, out var cached) &&
            DateTimeOffset.UtcNow - cached.CreatedAtUtc < BridgeStatusCacheTtl)
            return cached.Data.DeepClone().AsObject();
        try
        {
            var result = await serverBridgeService.QueryAsync(profile, "server.status", cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.Success && result.Data is not null)
            {
                _bridgeStatusCache[profile.Id] = new CachedBridgeJson(DateTimeOffset.UtcNow, result.Data.DeepClone().AsObject());
                return result.Data;
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or TimeoutException or InvalidOperationException)
        {
            _logger?.LogDebug(exception, "读取服务器桥接状态失败。ProfileId={ProfileId}", profile.Id);
        }
        return null;
    }

    private void RecordPlayerCount(InstanceProfile profile, int onlinePlayers, DateTimeOffset timestamp)
    {
        try
        {
            var gate = _playerHistoryGates.GetOrAdd(profile.Id, static _ => new object());
            lock (gate)
            {
                var records = LoadPlayerCountHistory(profile);
                if (records.LastOrDefault()?.OnlinePlayers == onlinePlayers)
                    return;

                records.Add(new OpenApiPlayerCountRecord
                {
                    TimestampUtc = timestamp,
                    OnlinePlayers = Math.Max(0, onlinePlayers)
                });

                var retained = records
                    .OrderBy(record => record.TimestampUtc)
                    .Where(record => record.TimestampUtc >= DateTimeOffset.UtcNow.AddHours(-PlayerCountHistoryHours))
                    .ToList();
                SavePlayerCountHistory(profile, retained);
            }
        }
        catch (Exception exception)
        {
            _logger?.LogDebug(exception, "记录开放 API 玩家数量失败。ProfileId={ProfileId}", profile.Id);
        }
    }

    private IReadOnlyList<OpenApiPlayerCountRecord> GetPlayerCountHistory(InstanceProfile profile)
    {
        var gate = _playerHistoryGates.GetOrAdd(profile.Id, static _ => new object());
        lock (gate)
        {
            return LoadPlayerCountHistory(profile)
                .OrderBy(record => record.TimestampUtc)
                .Where(record => record.TimestampUtc >= DateTimeOffset.UtcNow.AddHours(-PlayerCountHistoryHours))
                .ToArray();
        }
    }

    private static List<OpenApiPlayerCountRecord> LoadPlayerCountHistory(InstanceProfile profile)
    {
        var path = GetPlayerCountHistoryPath(profile);
        try
        {
            if (!File.Exists(path))
                return [];

            var records = JsonSerializer.Deserialize<List<OpenApiPlayerCountRecord>>(File.ReadAllText(path), JsonOptions);
            return records?
                .Where(record => record.TimestampUtc != default && record.OnlinePlayers >= 0)
                .GroupBy(record => record.TimestampUtc)
                .Select(group => group.Last())
                .OrderBy(record => record.TimestampUtc)
                .Where(record => record.TimestampUtc >= DateTimeOffset.UtcNow.AddHours(-PlayerCountHistoryHours))
                .ToList() ?? [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static void SavePlayerCountHistory(InstanceProfile profile, IReadOnlyList<OpenApiPlayerCountRecord> records)
    {
        Directory.CreateDirectory(profile.DirectoryPath);
        var path = GetPlayerCountHistoryPath(profile);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(records, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
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

    private static string GetPlayerCountHistoryPath(InstanceProfile profile) =>
        Path.Combine(profile.DirectoryPath, "openapi-player-history.json");

    private static int? ReadInt(JsonObject? data, string name) =>
        data?[name] is JsonValue value && value.TryGetValue<int>(out var result) ? result : null;

    private static long? ReadLong(JsonObject? data, string name) =>
        data?[name] is JsonValue value && value.TryGetValue<long>(out var result) ? result : null;

    private static string? ReadString(JsonObject? data, string name) =>
        data?[name] is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;

    private static DateTimeOffset? ReadDateTime(JsonObject data, string name) =>
        data[name] is JsonValue value && value.TryGetValue<DateTimeOffset>(out var result) ? result : null;

    private sealed record CachedBridgeJson(DateTimeOffset CreatedAtUtc, JsonObject Data);

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
    public string ServerStatus { get; init; } = string.Empty;
    public string WorldName { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public int MaxPlayers { get; init; }
    public bool IsRunning { get; init; }
    public int OnlinePlayers { get; init; }
    public DateTimeOffset? StartedAtUtc { get; init; }
    public long UptimeSeconds { get; init; }
    public string CoverUrl { get; init; } = string.Empty;
    public string PlayerCountHistoryMode { get; init; } = "on-change";
    public int PlayerCountHistoryHours { get; init; }
    public IReadOnlyList<OpenApiPlayerCountRecord> PlayerCountHistory { get; init; } = [];
    public IReadOnlyList<OpenApiModInfo> Mods { get; init; } = [];
}

public sealed class OpenApiPlayerCountRecord
{
    public DateTimeOffset TimestampUtc { get; init; }
    public int OnlinePlayers { get; init; }
}

public sealed class OpenApiModInfo
{
    public string Name { get; init; } = string.Empty;
    public string ModId { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
}
