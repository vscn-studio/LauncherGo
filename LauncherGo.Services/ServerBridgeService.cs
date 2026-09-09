using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

/// <summary>
///     Local-only transport for the in-server server bridge. The protocol deliberately has no remote endpoint.
/// </summary>
public sealed class ServerBridgeService : IServerBridgeService
{
    private const string ModId = "launchergoserverbridge";
    private const string ModVersion = "2.1.0";
    private const string ModFolderName = "launchergoserverbridge";
    private const string ModDllName = "serverbridge.dll";
    private const string SettingsRelativePath = "ModConfig/launchergoserverbridge.json";
    private const int DefaultPort = 19090;
    private const int MinimumPort = 1024;
    private const int MaximumPort = 65535;
    private const int MinimumTimeoutMilliseconds = 500;
    private const int MaximumTimeoutMilliseconds = 30000;
    private const int MinimumCommandLength = 256;
    private const int MaximumCommandLength = 16384;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    // TCP uses one JSON document per line; persisted configuration remains indented for operators.
    private static readonly JsonSerializerOptions WireJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IInstanceServerConfigService _serverConfigService;
    private readonly ServerBridgeStateStore? _stateStore;

    public ServerBridgeService(IInstanceServerConfigService serverConfigService, ServerBridgeStateStore? stateStore = null)
    {
        _serverConfigService = serverConfigService;
        _stateStore = stateStore;
    }

    public async Task<ServerBridgeSettings> LoadSettingsAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetSettingsPath(profile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (!File.Exists(path))
        {
            var defaults = NormalizeSettings(profile, new ServerBridgeSettings());
            await SaveSettingsAsync(profile, defaults, cancellationToken);
            return defaults;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var settings = JsonSerializer.Deserialize<ServerBridgeSettings>(json, JsonOptions)
                           ?? new ServerBridgeSettings();
            var normalized = NormalizeSettings(profile, settings);
            if (!string.Equals(settings.AccessToken, normalized.AccessToken, StringComparison.Ordinal) ||
                settings.Port != normalized.Port ||
                settings.QueryTimeoutMilliseconds != normalized.QueryTimeoutMilliseconds ||
                settings.MaxCommandLength != normalized.MaxCommandLength ||
                settings.IncludeExtendedPlayerInfo != normalized.IncludeExtendedPlayerInfo ||
                settings.IncludeWorldDetails != normalized.IncludeWorldDetails ||
                settings.IncludePerformanceInfo != normalized.IncludePerformanceInfo ||
                settings.IncludeSensitiveFields != normalized.IncludeSensitiveFields ||
                !(settings.EventTypes ?? []).SequenceEqual(normalized.EventTypes, StringComparer.OrdinalIgnoreCase))
            {
                await SaveSettingsAsync(profile, normalized, cancellationToken);
            }

            return normalized;
        }
        catch
        {
            var defaults = NormalizeSettings(profile, new ServerBridgeSettings());
            await SaveSettingsAsync(profile, defaults, cancellationToken);
            return defaults;
        }
    }

    public async Task SaveSettingsAsync(
        InstanceProfile profile,
        ServerBridgeSettings settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetSettingsPath(profile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(NormalizeSettings(profile, settings), JsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    public async Task EnsureServerBridgeModDeployedAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default,
        bool enableMod = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var modsPath = WorkspacePathHelper.GetProfileModsPath(profile.DirectoryPath);
        Directory.CreateDirectory(modsPath);
        var destination = Path.Combine(modsPath, ModFolderName);
        SyncDirectory(ResolveEmbeddedSourceRoot(), destination);
        await SetServerBridgeModEnabledAsync(profile, enableMod, cancellationToken);
    }

    public async Task<bool> GetServerBridgeModEnabledAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(Path.Combine(WorkspacePathHelper.GetProfileModsPath(profile.DirectoryPath), ModFolderName)))
            return false;

        return !await IsModDisabledAsync(profile, cancellationToken);
    }

    public async Task SetServerBridgeModEnabledAsync(
        InstanceProfile profile,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rawJson = await _serverConfigService.LoadRawJsonAsync(profile, cancellationToken);
        var root = JsonNode.Parse(rawJson) as JsonObject
                   ?? throw new InvalidOperationException("配置格式错误。");
        var disabledMods = GetOrCreateDisabledModsArray(root);
        var remain = disabledMods
            .Where(static item => item is not null)
            .Select(static item => item!.GetValue<string>())
            .Where(static item => !IsModDisabledKey(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!enabled)
            remain.Add($"{ModId}@{ModVersion}");

        disabledMods.Clear();
        foreach (var item in remain.Distinct(StringComparer.OrdinalIgnoreCase))
            disabledMods.Add(item);

        await _serverConfigService.SaveRawJsonAsync(profile, root.ToJsonString(JsonOptions), cancellationToken);
    }

    public async Task<ServerBridgeRuntimeStatus> GetRuntimeStatusAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(profile, cancellationToken);
        if (!settings.Enabled)
        {
            return new ServerBridgeRuntimeStatus
            {
                State = ServerBridgeRuntimeState.Disabled,
                Message = "服务器桥接未启用。",
                Port = settings.Port
            };
        }

        if (!await GetServerBridgeModEnabledAsync(profile, cancellationToken))
        {
            return new ServerBridgeRuntimeStatus
            {
                State = ServerBridgeRuntimeState.NotDeployed,
                Message = "服务器桥接模组未部署或已禁用。",
                Port = settings.Port
            };
        }

        try
        {
            var response = await SendRequestAsync(settings, new ServerBridgeRequest
            {
                Version = 2,
                Type = "ping",
                Token = settings.AccessToken
            }, cancellationToken);
            return response.Success
                ? new ServerBridgeRuntimeStatus
                {
                    State = ServerBridgeRuntimeState.Ready,
                    Message = "服务器桥接已就绪。",
                    Port = settings.Port,
                    Version = response.BridgeVersion ?? string.Empty
                }
                : new ServerBridgeRuntimeStatus
                {
                    State = ServerBridgeRuntimeState.Unavailable,
                    Message = response.Error ?? "服务器桥接拒绝了连接。",
                    Port = settings.Port
                };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or OperationCanceledException)
        {
            return new ServerBridgeRuntimeStatus
            {
                State = ServerBridgeRuntimeState.Unavailable,
                Message = "服务器桥接当前不可达：" + ex.Message,
                Port = settings.Port
            };
        }
    }

    public async Task SendCommandAsync(
        InstanceProfile profile,
        string command,
        CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(profile, cancellationToken);
        if (!settings.Enabled)
            throw new InvalidOperationException("服务器桥接未启用。");

        var normalized = NormalizeCommand(command, settings.MaxCommandLength);
        var response = await SendRequestAsync(settings, new ServerBridgeRequest
        {
            Version = 2,
            Type = "command",
            Token = settings.AccessToken,
            Id = Guid.NewGuid().ToString("N"),
            Command = normalized
        }, cancellationToken);
        if (!response.Success)
            throw new InvalidOperationException(response.Error ?? "服务器桥接未接受命令。");
    }

    public async Task ExecuteCommandAsync(InstanceProfile profile, string command, CancellationToken cancellationToken = default)
        => await SendCommandAsync(profile, command, cancellationToken).ConfigureAwait(false);

    public async Task<ServerBridgeQueryResult> QueryAsync(
        InstanceProfile profile,
        string method,
        JsonObject? arguments = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(method))
            throw new ArgumentException("查询方法不能为空。", nameof(method));
        var settings = await LoadSettingsAsync(profile, cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
            return new ServerBridgeQueryResult { Success = false, ErrorCode = "bridge-disabled", Error = "服务器桥接未启用。" };
        var response = await SendRequestAsync(settings, new ServerBridgeRequest
        {
            Version = 2,
            Type = "query",
            Id = Guid.NewGuid().ToString("N"),
            Token = settings.AccessToken,
            Method = method.Trim(),
            Arguments = arguments
        }, cancellationToken).ConfigureAwait(false);
        if (response.Success && response.Data is not null)
            _stateStore?.SetState(profile.Id, method, response.Data);
        return new ServerBridgeQueryResult
        {
            Success = response.Success,
            Data = response.Data,
            ErrorCode = response.ErrorCode,
            Error = response.Error,
            RequestId = response.Id,
            BridgeVersion = response.BridgeVersion
        };
    }

    public async Task<ServerBridgeSubscription> SubscribeAsync(
        InstanceProfile profile,
        ServerBridgeSubscriptionOptions options,
        Func<ServerBridgeEvent, Task> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (options is null) throw new ArgumentNullException(nameof(options));
        if ((options.Events?.Count ?? 0) > 64) throw new ArgumentException("订阅事件数量不能超过 64。", nameof(options));
        if (options.MaxQueueSize is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(options.MaxQueueSize));
        var settings = await LoadSettingsAsync(profile, cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
            throw new InvalidOperationException("服务器桥接未启用。");
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastSequence = options.Since;
        var initializeCursor = options.StartFromLatest;
        var runTask = Task.Run(async () =>
        {
            while (!linked.IsCancellationRequested)
            {
                try
                {
                    using var client = new TcpClient(AddressFamily.InterNetwork);
                    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                    connectCts.CancelAfter(TimeSpan.FromMilliseconds(settings.QueryTimeoutMilliseconds));
                    await client.ConnectAsync(IPAddress.Loopback, settings.Port, connectCts.Token).ConfigureAwait(false);
                    await using var stream = client.GetStream();
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new ServerBridgeRequest
                    {
                        Version = 2, Type = "subscribe", Id = Guid.NewGuid().ToString("N"), Token = settings.AccessToken,
                        Events = options.Events?.ToArray() ?? [], Since = initializeCursor ? long.MaxValue : lastSequence, MaxQueueSize = options.MaxQueueSize
                    }, WireJsonOptions).AsMemory(), linked.Token).ConfigureAwait(false);
                    var ack = JsonSerializer.Deserialize<ServerBridgeResponse>(await reader.ReadLineAsync(linked.Token) ?? string.Empty, WireJsonOptions);
                    if (ack is null || !ack.Success) throw new InvalidOperationException(ack?.Error ?? "服务器桥接拒绝订阅。");
                    var currentSequence = ack.Data?["currentSequence"]?.GetValue<long?>();
                    var oldestSequence = ack.Data?["oldestSequence"]?.GetValue<long?>();
                    if (options.StartFromLatest && (currentSequence is null || currentSequence < 0))
                        throw new InvalidOperationException("服务器桥接未返回有效事件序号，已暂停实时转发以避免重复发送历史消息。");
                    if (initializeCursor)
                    {
                        lastSequence = currentSequence!.Value;
                        initializeCursor = false;
                    }
                    else if (currentSequence is not null && currentSequence < lastSequence)
                    {
                        lastSequence = options.StartFromLatest ? currentSequence.Value : 0;
                        await QueryAsync(profile, "server.status", cancellationToken: linked.Token).ConfigureAwait(false);
                        await QueryAsync(profile, "players.list", cancellationToken: linked.Token).ConfigureAwait(false);
                    }
                    else if (oldestSequence is not null && lastSequence > 0 && oldestSequence > lastSequence + 1)
                    {
                        await QueryAsync(profile, "server.status", cancellationToken: linked.Token).ConfigureAwait(false);
                        await QueryAsync(profile, "players.list", cancellationToken: linked.Token).ConfigureAwait(false);
                    }
                    ready.TrySetResult();
                    while (!linked.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync(linked.Token).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(line)) throw new IOException("服务器桥接订阅已断开。");
                        var evt = JsonSerializer.Deserialize<WireServerBridgeEvent>(line, WireJsonOptions);
                        if (evt is null || !string.Equals(evt.Type, "event", StringComparison.OrdinalIgnoreCase)) continue;
                        if (lastSequence > 0 && evt.Sequence > lastSequence + 1)
                        {
                            await QueryAsync(profile, "server.status", cancellationToken: linked.Token).ConfigureAwait(false);
                            await QueryAsync(profile, "players.list", cancellationToken: linked.Token).ConfigureAwait(false);
                        }
                        if (evt.Sequence <= lastSequence) continue;
                        lastSequence = evt.Sequence;
                        var bridgeEvent = new ServerBridgeEvent { Sequence = evt.Sequence, Event = evt.Event, TimestampUtc = evt.TimestampUtc, Data = evt.Data ?? new() };
                        _stateStore?.AddEvent(profile.Id, bridgeEvent);
                        linked.Token.ThrowIfCancellationRequested();
                        await handler(bridgeEvent).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    if (!ready.Task.IsCompleted) ready.TrySetException(ex);
                    try { await Task.Delay(TimeSpan.FromSeconds(2), linked.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }, CancellationToken.None);

        try
        {
            await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            linked.Cancel();
            try { await runTask.ConfigureAwait(false); } catch { }
            linked.Dispose();
            throw;
        }

        return new ServerBridgeSubscription(async () =>
        {
            linked.Cancel();
            try { await runTask.ConfigureAwait(false); } catch { }
            linked.Dispose();
        });
    }

    public async Task RotateAccessTokenAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(profile, cancellationToken);
        if (!settings.Enabled)
            throw new InvalidOperationException("服务器桥接未启用，无法热轮换访问令牌。");

        var replacementToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var replacementSettings = new ServerBridgeSettings
        {
            Enabled = settings.Enabled,
            Port = settings.Port,
            AccessToken = replacementToken,
            QueryTimeoutMilliseconds = settings.QueryTimeoutMilliseconds,
            MaxCommandLength = settings.MaxCommandLength,
            AllowRelayFallback = settings.AllowRelayFallback,
            IncludeExtendedPlayerInfo = settings.IncludeExtendedPlayerInfo,
            IncludeWorldDetails = settings.IncludeWorldDetails,
            IncludePerformanceInfo = settings.IncludePerformanceInfo,
            IncludeSensitiveFields = settings.IncludeSensitiveFields,
            EventTypes = settings.EventTypes
        };

        var response = await SendRequestAsync(settings, new ServerBridgeRequest
        {
            Version = 2,
            Type = "rotate-token",
            Token = settings.AccessToken,
            NewToken = replacementToken
        }, cancellationToken);
        if (!response.Success)
        {
            if (response.Error?.Contains("Unsupported server bridge request", StringComparison.OrdinalIgnoreCase) == true)
            {
                throw new InvalidOperationException(
                    "当前运行中的服务器桥接不支持令牌热轮换。请部署新版服务器桥接并重启服务端一次；之后轮换无需重启。");
            }

            throw new InvalidOperationException(
                response.Error ?? "运行中的服务器桥接未接受访问令牌热轮换。未修改本地令牌。");
        }

        try
        {
            await SaveSettingsAsync(profile, replacementSettings, cancellationToken);
        }
        catch (Exception saveException)
        {
            try
            {
                await SendRequestAsync(replacementSettings, new ServerBridgeRequest
                {
                    Version = 2,
                    Type = "rotate-token",
                    Token = replacementToken,
                    NewToken = settings.AccessToken
                }, CancellationToken.None);
            }
            catch
            {
                // The primary failure below explains that manual recovery may be required.
            }

            throw new InvalidOperationException(
                "访问令牌热轮换后无法保存本地配置；已尝试恢复服务端令牌。请测试连接后再继续操作。",
                saveException);
        }
    }

    private static async Task<ServerBridgeResponse> SendRequestAsync(
        ServerBridgeSettings settings,
        ServerBridgeRequest request,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(settings.QueryTimeoutMilliseconds));
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, settings.Port, timeoutCts.Token);
        await using var stream = client.GetStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, WireJsonOptions).AsMemory(), timeoutCts.Token);
        var responseJson = await reader.ReadLineAsync(timeoutCts.Token);
        if (string.IsNullOrWhiteSpace(responseJson))
            throw new IOException("服务器桥接未返回响应。");
        return JsonSerializer.Deserialize<ServerBridgeResponse>(responseJson, WireJsonOptions)
               ?? throw new IOException("服务器桥接返回了无效响应。");
    }

    private static ServerBridgeSettings NormalizeSettings(InstanceProfile profile, ServerBridgeSettings settings)
    {
        return new ServerBridgeSettings
        {
            Enabled = settings.Enabled,
            Port = settings.Port is >= MinimumPort and <= MaximumPort
                ? settings.Port
                : GetDefaultPort(profile.Id),
            AccessToken = IsValidToken(settings.AccessToken)
                ? settings.AccessToken.Trim().ToLowerInvariant()
                : Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            QueryTimeoutMilliseconds = Math.Clamp(
                settings.QueryTimeoutMilliseconds <= 0 ? 5000 : settings.QueryTimeoutMilliseconds,
                MinimumTimeoutMilliseconds,
                MaximumTimeoutMilliseconds),
            MaxCommandLength = Math.Clamp(
                settings.MaxCommandLength <= 0 ? 4096 : settings.MaxCommandLength,
                MinimumCommandLength,
                MaximumCommandLength),
            AllowRelayFallback = settings.AllowRelayFallback,
            IncludeExtendedPlayerInfo = settings.IncludeExtendedPlayerInfo,
            IncludeWorldDetails = settings.IncludeWorldDetails,
            IncludePerformanceInfo = settings.IncludePerformanceInfo,
            IncludeSensitiveFields = settings.IncludeSensitiveFields,
            EventTypes = (settings.EventTypes ?? []).Where(static x => !string.IsNullOrWhiteSpace(x)).Select(static x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToArray()
        };
    }

    private static bool IsValidToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length != 64)
            return false;
        return value.Trim().All(Uri.IsHexDigit);
    }

    private static int GetDefaultPort(string profileId)
    {
        var source = string.IsNullOrWhiteSpace(profileId) ? Guid.NewGuid().ToString("N") : profileId.Trim();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        var offset = (hash[0] << 8 | hash[1]) % 20000;
        return Math.Clamp(DefaultPort + offset, MinimumPort, MaximumPort);
    }

    private static string NormalizeCommand(string? command, int maxLength)
    {
        var normalized = command?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("命令不能为空。");
        if (normalized.Length > maxLength)
            throw new InvalidOperationException($"命令长度不能超过 {maxLength} 个字符。");
        return normalized.StartsWith('/') ? normalized : "/" + normalized;
    }

    private static void SyncDirectory(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);
        var sourceFiles = Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(sourcePath, path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The bridge directory is owned by LauncherGo. Remove stale files from
        // older packages so Vintage Story does not reject multiple assemblies.
        foreach (var destinationFile in Directory.EnumerateFiles(destinationPath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(destinationPath, destinationFile);
            if (sourceFiles.Contains(relative)) continue;
            try { File.Delete(destinationFile); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        foreach (var directory in Directory.EnumerateDirectories(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourcePath, directory);
            Directory.CreateDirectory(Path.Combine(destinationPath, relative));
        }

        foreach (var sourceFile in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourcePath, sourceFile);
            var target = Path.Combine(destinationPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try
            {
                File.Copy(sourceFile, target, true);
            }
            catch (UnauthorizedAccessException) when (IsBridgeDll(target))
            {
                // The running server holds the loaded bridge dll. Its replacement is applied on next start.
            }
            catch (IOException) when (IsBridgeDll(target))
            {
                // The running server holds the loaded bridge dll. Its replacement is applied on next start.
            }
        }
    }

    private static string ResolveEmbeddedSourceRoot()
    {
        var primary = Path.Combine(AppContext.BaseDirectory, "EmbeddedMods", ModFolderName);
        if (Directory.Exists(primary))
            return primary;

        var fallback = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "LauncherGo.Services",
            "EmbeddedMods",
            ModFolderName));
        if (Directory.Exists(fallback))
            return fallback;

        throw new InvalidOperationException($"未找到内置服务器桥接模组文件：{primary}；{fallback}");
    }

    private async Task<bool> IsModDisabledAsync(InstanceProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            var rawJson = await _serverConfigService.LoadRawJsonAsync(profile, cancellationToken);
            var root = JsonNode.Parse(rawJson) as JsonObject;
            if (root?["WorldConfig"] is not JsonObject worldConfig ||
                worldConfig["DisabledMods"] is not JsonArray disabledMods)
            {
                return false;
            }

            return disabledMods
                .Where(static item => item is not null)
                .Select(static item => item!.GetValue<string>())
                .Any(static item => IsModDisabledKey(item));
        }
        catch
        {
            return false;
        }
    }

    private static JsonArray GetOrCreateDisabledModsArray(JsonObject root)
    {
        if (root["WorldConfig"] is not JsonObject worldConfig)
        {
            worldConfig = new JsonObject();
            root["WorldConfig"] = worldConfig;
        }

        if (worldConfig["DisabledMods"] is JsonArray disabledMods)
            return disabledMods;
        disabledMods = new JsonArray();
        worldConfig["DisabledMods"] = disabledMods;
        return disabledMods;
    }

    private static bool IsModDisabledKey(string item) =>
        item.Equals(ModId, StringComparison.OrdinalIgnoreCase) ||
        item.StartsWith(ModId + "@", StringComparison.OrdinalIgnoreCase);

    private static bool IsBridgeDll(string path) =>
        Path.GetFileName(path).Equals(ModDllName, StringComparison.OrdinalIgnoreCase);

    private static string GetSettingsPath(InstanceProfile profile) =>
        Path.Combine(profile.DirectoryPath, SettingsRelativePath);

    private sealed class ServerBridgeRequest
    {
        public int Version { get; init; }
        public string Type { get; init; } = string.Empty;
        public string Token { get; init; } = string.Empty;
        public string NewToken { get; init; } = string.Empty;
        public string Id { get; init; } = string.Empty;
        public string Command { get; init; } = string.Empty;
        public string Method { get; init; } = string.Empty;
        public JsonObject? Arguments { get; init; }
        public string[] Events { get; init; } = [];
        public long Since { get; init; }
        public int MaxQueueSize { get; init; }
    }

    private sealed class ServerBridgeResponse
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public string? BridgeVersion { get; init; }
        public int Version { get; init; }
        public string? Id { get; init; }
        public string? ErrorCode { get; init; }
        public JsonObject? Data { get; init; }
    }

    private sealed class WireServerBridgeEvent
    {
        public int Version { get; init; }
        public string Type { get; init; } = string.Empty;
        public long Sequence { get; init; }
        public string Event { get; init; } = string.Empty;
        public DateTimeOffset TimestampUtc { get; init; }
        public JsonObject? Data { get; init; }
    }
}
