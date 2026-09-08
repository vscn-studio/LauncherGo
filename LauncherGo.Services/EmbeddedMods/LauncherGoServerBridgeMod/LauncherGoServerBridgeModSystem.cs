using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using LauncherGo.Services;
using BoolRef = Vintagestory.API.Datastructures.BoolRef;
using ITreeAttribute = Vintagestory.API.Datastructures.ITreeAttribute;
using Vintagestory.API.Config;

namespace LauncherGoServerBridge;

/// <summary>
///     A local, authenticated console bridge. Commands are injected on the Vintage Story server thread.
/// </summary>
public sealed class LauncherGoServerBridgeModSystem : ModSystem
{
    private const string ConfigurationFileName = "launchergoserverbridge.json";
    private const string BridgeVersion = "2.1.0";
    private const int MaximumRequestBytes = 32768;
    private const int MaximumEventHistory = 500;
    private const int MaximumSubscriptions = 16;
    private const int MaximumExtensionResultBytes = 1024 * 1024;
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private ICoreServerAPI? _serverApi;
    private TcpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    private Task? _acceptLoop;
    private FileSystemWatcher? _configurationWatcher;
    private Timer? _configurationReloadTimer;
    private ServerBridgeConfiguration _configuration = new();
    private readonly object _configurationLock = new();
    private readonly object _eventLock = new();
    private readonly Queue<ServerBridgeEventRecord> _eventHistory = new();
    private readonly ConcurrentDictionary<Guid, BridgeSubscriber> _subscribers = new();
    private readonly ConcurrentDictionary<string, long> _recentPlayerEvents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _recentExactDeathLogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _playerJoinedAtUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _playerLastActivityUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IServerBridgeExtensionProvider> _extensionProviders = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _extensionLastQueryTicks = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _querySlots = new(8, 8);
    private readonly object _performanceLock = new();
    private long _performanceListenerId;
    private long _performanceWindowStartedMs;
    private int _performanceTickCount;
    private double _measuredTps;
    private double _measuredTickTimeMs;
    private long _eventSequence;

    public override void StartServerSide(ICoreServerAPI api)
    {
        _serverApi = api;
        api.Event.PlayerNowPlaying += OnPlayerNowPlaying;
        api.Event.PlayerDisconnect += OnPlayerDisconnect;
        api.Event.PlayerChat += OnPlayerChat;
        api.Logger.EntryAdded += OnLoggerEntryAdded;
        _performanceListenerId = api.Event.RegisterGameTickListener(OnPerformanceTick, 0, 0);
        _performanceWindowStartedMs = Environment.TickCount64;
        _configuration = LoadConfiguration(api);
        StartConfigurationWatcher(api);
        ApplyConfiguration(api, _configuration, restartListener: true);
    }

    public override void Dispose()
    {
        var api = _serverApi;
        if (api is not null)
        {
            api.Event.PlayerNowPlaying -= OnPlayerNowPlaying;
            api.Event.PlayerDisconnect -= OnPlayerDisconnect;
            api.Event.PlayerChat -= OnPlayerChat;
            api.Logger.EntryAdded -= OnLoggerEntryAdded;
            if (_performanceListenerId != 0)
                api.Event.UnregisterGameTickListener(_performanceListenerId);
        }
        foreach (var subscriber in _subscribers.Values) subscriber.Channel.Writer.TryComplete();
        _subscribers.Clear();
        _recentExactDeathLogs.Clear();
        _playerJoinedAtUtc.Clear();
        _playerLastActivityUtc.Clear();
        _configurationReloadTimer?.Dispose();
        _configurationReloadTimer = null;
        _configurationWatcher?.Dispose();
        _configurationWatcher = null;
        StopListener();
        _serverApi = null;
        base.Dispose();
    }

    public void RegisterExtensionProvider(IServerBridgeExtensionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(provider.ProviderId)) throw new ArgumentException("ProviderId is required.", nameof(provider));
        _extensionProviders[provider.ProviderId.Trim()] = provider;
    }

    public bool UnregisterExtensionProvider(string providerId) =>
        !string.IsNullOrWhiteSpace(providerId) && _extensionProviders.TryRemove(providerId.Trim(), out _);

    private void StartConfigurationWatcher(ICoreServerAPI api)
    {
        try
        {
            Directory.CreateDirectory(GamePaths.ModConfig);
            _configurationWatcher = new FileSystemWatcher(GamePaths.ModConfig, ConfigurationFileName)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _configurationWatcher.Changed += (_, _) => ScheduleConfigurationReload(api);
            _configurationWatcher.Created += (_, _) => ScheduleConfigurationReload(api);
            _configurationWatcher.Renamed += (_, _) => ScheduleConfigurationReload(api);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("LauncherGo Server Bridge could not watch configuration: {0}", ex.Message);
        }
    }

    private void ScheduleConfigurationReload(ICoreServerAPI api)
    {
        try
        {
            _configurationReloadTimer ??= new Timer(_ => ReloadConfiguration(api), null, Timeout.Infinite, Timeout.Infinite);
            _configurationReloadTimer.Change(250, Timeout.Infinite);
        }
        catch (ObjectDisposedException) { }
    }

    private void ReloadConfiguration(ICoreServerAPI api)
    {
        try
        {
            var next = LoadConfiguration(api);
            ServerBridgeConfiguration previous;
            lock (_configurationLock) previous = _configuration;
            var restartListener = previous.Enabled != next.Enabled || previous.Port != next.Port;
            ApplyConfiguration(api, next, restartListener);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("LauncherGo Server Bridge configuration reload failed: {0}", ex.Message);
        }
    }

    private void ApplyConfiguration(ICoreServerAPI api, ServerBridgeConfiguration configuration, bool restartListener)
    {
        lock (_configurationLock) _configuration = configuration;
        if (!restartListener) return;

        StopListener();
        if (!configuration.Enabled) return;
        try
        {
            var listenerCts = new CancellationTokenSource();
            var listener = new TcpListener(IPAddress.Loopback, configuration.Port);
            listener.Start();
            _listenerCts = listenerCts;
            _listener = listener;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listenerCts.Token));
            api.Logger.Notification("LauncherGo Server Bridge is listening on 127.0.0.1:{0}.", configuration.Port);
        }
        catch (Exception ex)
        {
            api.Logger.Error("LauncherGo Server Bridge failed to start: {0}", ex.Message);
            StopListener();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        var listener = _listener;
        if (listener is null)
            return;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), CancellationToken.None);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                try
                {
                    await Task.Delay(200, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverCancellationToken)
    {
        using (client)
        await using (var stream = client.GetStream())
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true })
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
        {
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken);
            requestCts.CancelAfter(TimeSpan.FromMilliseconds(_configuration.QueryTimeoutMilliseconds));
            ServerBridgeResponse response;
            ServerBridgeRequest? parsedRequest = null;
            try
            {
                var requestJson = await reader.ReadLineAsync(requestCts.Token);
                if (string.IsNullOrWhiteSpace(requestJson))
                {
                    response = Failure("Empty server bridge request.", "invalid-request");
                }
                else if (Encoding.UTF8.GetByteCount(requestJson) > MaximumRequestBytes)
                {
                    response = Failure("Server bridge request is too large.", "request-too-large");
                }
                else
                {
                    parsedRequest = JsonSerializer.Deserialize<ServerBridgeRequest>(requestJson, JsonOptions);
                    response = await ProcessRequestAsync(parsedRequest, requestCts.Token);
                }
            }
            catch (OperationCanceledException) when (serverCancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                response = Failure("Server bridge request timed out.", "timeout");
            }
            catch (Exception ex)
            {
                response = Failure("Invalid server bridge request: " + ex.Message, "invalid-request");
            }

            try
            {
                var responseJson = JsonSerializer.Serialize(response, JsonOptions);
                if (Encoding.UTF8.GetByteCount(responseJson) > MaximumResponseBytes)
                    responseJson = JsonSerializer.Serialize(Failure("Response is too large.", "result-too-large"), JsonOptions);
                await writer.WriteLineAsync(responseJson.AsMemory(), serverCancellationToken);
                if (parsedRequest?.Type.Equals("subscribe", StringComparison.OrdinalIgnoreCase) == true && response.Success)
                    await RunSubscriptionAsync(parsedRequest, writer, serverCancellationToken);
            }
            catch
            {
                // A disconnected local LauncherGo client does not affect the server.
            }
        }
    }

    private async Task<ServerBridgeResponse> ProcessRequestAsync(
        ServerBridgeRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return Failure("Server bridge request could not be parsed.");
        if (request.Version != 2)
            return Failure("Unsupported protocol version.", "unsupported-version");
        if (!FixedTimeEquals(request.Token, _configuration.AccessToken))
            return Failure("Server bridge authentication failed.", "permission-denied");
        if (request.Type.Equals("ping", StringComparison.OrdinalIgnoreCase))
        {
            return new ServerBridgeResponse { Version = 2, Id = request.Id, Success = true, BridgeVersion = BridgeVersion };
        }

        if (request.Type.Equals("heartbeat", StringComparison.OrdinalIgnoreCase))
            return new ServerBridgeResponse { Version = 2, Id = request.Id, Type = "heartbeat", Success = true, BridgeVersion = BridgeVersion };

        if (request.Type.Equals("query", StringComparison.OrdinalIgnoreCase))
            return await ProcessQueryAsync(request, cancellationToken);
        if (request.Type.Equals("subscribe", StringComparison.OrdinalIgnoreCase) && _subscribers.Count >= MaximumSubscriptions)
            return Failure("Subscription limit reached.", "subscription-limit");
        if (request.Type.Equals("subscribe", StringComparison.OrdinalIgnoreCase))
            return new ServerBridgeResponse { Version = 2, Success = true, Id = request.Id, Data = BuildSubscriptionState(), BridgeVersion = BridgeVersion };
        if (request.Type.Equals("unsubscribe", StringComparison.OrdinalIgnoreCase))
            return new ServerBridgeResponse { Version = 2, Success = true, Id = request.Id, BridgeVersion = BridgeVersion };

        if (request.Type.Equals("rotate-token", StringComparison.OrdinalIgnoreCase))
        {
            var replacementToken = request.NewToken?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!IsValidToken(replacementToken))
                return Failure("Replacement access token is invalid.");

            lock (_configurationLock)
            {
                // Re-check after locking so concurrent rotation requests cannot overwrite each other.
                if (!FixedTimeEquals(request.Token, _configuration.AccessToken))
                    return Failure("Server bridge authentication failed.", "permission-denied");
                _configuration.AccessToken = replacementToken;
            }

            return new ServerBridgeResponse { Version = 2, Id = request.Id, Success = true, BridgeVersion = BridgeVersion };
        }

        if (!request.Type.Equals("command", StringComparison.OrdinalIgnoreCase))
            return Failure("Unsupported server bridge request.", "unsupported-capability");

        var command = request.Command?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(command))
            return Failure("Command is empty.");
        if (command.Length > _configuration.MaxCommandLength)
            return Failure("Command exceeds the configured maximum length.");
        if (!command.StartsWith('/'))
            command = "/" + command;

        var api = _serverApi;
        if (api is null)
            return Failure("Server API is unavailable.", "server-not-ready");

        var completion = new TaskCompletionSource<ServerBridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            api.Event.EnqueueMainThreadTask(() =>
            {
                try
                {
                    api.InjectConsole(command);
                    completion.TrySetResult(new ServerBridgeResponse
                    {
                        Version = 2,
                        Id = request.Id,
                        Success = true,
                        BridgeVersion = BridgeVersion
                    });
                }
                catch (Exception ex)
                {
                    completion.TrySetResult(Failure("Server rejected command: " + ex.Message));
                }
            }, "launchergoserverbridge-command");
        }
        catch (Exception ex)
        {
            return Failure("Could not queue command on the server thread: " + ex.Message);
        }

        try
        {
            return await completion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Failure("Command was not accepted by the server thread before the timeout.");
        }
    }

    private async Task RunSubscriptionAsync(ServerBridgeRequest request, StreamWriter writer, CancellationToken cancellationToken)
    {
        if (_subscribers.Count >= MaximumSubscriptions) return;
        var capacity = Math.Clamp(request.MaxQueueSize <= 0 ? 256 : request.MaxQueueSize, 1, 4096);
        var channel = Channel.CreateBounded<ServerBridgeEventRecord>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            // TryWrite returns false when full so queue loss can be reported explicitly.
            FullMode = BoundedChannelFullMode.Wait
        });
        var subscriber = new BridgeSubscriber(channel, request.Events.ToHashSet(StringComparer.OrdinalIgnoreCase));
        var key = Guid.NewGuid();
        if (!_subscribers.TryAdd(key, subscriber)) return;
        var extensionSubscriptions = new List<IAsyncDisposable>();
        try
        {
            foreach (var provider in _extensionProviders.Values)
            {
                var prefix = $"ext.{provider.ProviderId}.";
                var requested = request.Events.Where(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (requested.Length == 0) continue;
                var extensionSubscription = await provider.SubscribeAsync(requested, value =>
                {
                    if (!string.IsNullOrWhiteSpace(value.Event))
                    {
                        var eventName = value.Event.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                            ? value.Event
                            : prefix + value.Event.TrimStart('.');
                        var record = CreateEventRecord(eventName, value.Data, value.TimestampUtc);
                        if (!channel.Writer.TryWrite(record)) subscriber.RecordDropped();
                    }
                    return ValueTask.CompletedTask;
                }, cancellationToken);
                if (extensionSubscription is not null) extensionSubscriptions.Add(extensionSubscription);
            }
            ServerBridgeEventRecord[] replay;
            lock (_eventLock) replay = _eventHistory.Where(x => x.Sequence > request.Since && subscriber.Accepts(x.Event)).ToArray();
            foreach (var value in replay)
                if (!channel.Writer.TryWrite(value)) subscriber.RecordDropped();

            var readTask = channel.Reader.ReadAsync(cancellationToken).AsTask();
            while (!cancellationToken.IsCancellationRequested)
            {
                var heartbeatTask = Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
                var completed = await Task.WhenAny(readTask, heartbeatTask);
                if (completed == readTask)
                {
                    var value = await readTask;
                    await WriteOverflowNoticeAsync(subscriber, writer, cancellationToken);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(value, JsonOptions).AsMemory(), cancellationToken);
                    readTask = channel.Reader.ReadAsync(cancellationToken).AsTask();
                }
                else
                {
                    await WriteOverflowNoticeAsync(subscriber, writer, cancellationToken);
                    await writer.WriteLineAsync("{\"version\":2,\"type\":\"heartbeat\"}".AsMemory(), cancellationToken);
                }
            }
        }
        finally
        {
            foreach (var extensionSubscription in extensionSubscriptions)
                try { await extensionSubscription.DisposeAsync(); } catch { }
            _subscribers.TryRemove(key, out _);
            channel.Writer.TryComplete();
        }
    }

    private async Task WriteOverflowNoticeAsync(
        BridgeSubscriber subscriber,
        StreamWriter writer,
        CancellationToken cancellationToken)
    {
        var dropped = subscriber.TakeDroppedCount();
        if (dropped <= 0) return;
        var notice = CreateEventRecord("bridge.overflow", new JsonObject
        {
            ["droppedCount"] = dropped,
            ["message"] = "订阅发送队列已溢出，请使用 since 序号恢复状态。"
        }, DateTimeOffset.UtcNow);
        await writer.WriteLineAsync(JsonSerializer.Serialize(notice, JsonOptions).AsMemory(), cancellationToken);
    }

    private JsonObject BuildSubscriptionState()
    {
        lock (_eventLock)
        {
            return new JsonObject
            {
                ["currentSequence"] = _eventSequence,
                ["oldestSequence"] = _eventHistory.Count == 0 ? _eventSequence + 1 : _eventHistory.Peek().Sequence
            };
        }
    }

    private void EmitEvent(string eventName, JsonObject data)
    {
        if (_configuration.EventTypes.Length > 0 && !_configuration.EventTypes.Contains(eventName, StringComparer.OrdinalIgnoreCase)) return;
        var value = CreateEventRecord(eventName, data, DateTimeOffset.UtcNow);
        lock (_eventLock)
        {
            _eventHistory.Enqueue(value);
            while (_eventHistory.Count > MaximumEventHistory) _eventHistory.Dequeue();
        }
        foreach (var subscriber in _subscribers.Values)
            if (subscriber.Accepts(eventName) && !subscriber.Channel.Writer.TryWrite(value))
                subscriber.RecordDropped();
    }

    private ServerBridgeEventRecord CreateEventRecord(string eventName, JsonObject data, DateTimeOffset timestampUtc) => new()
    {
        Version = 2,
        Type = "event",
        Sequence = Interlocked.Increment(ref _eventSequence),
        Event = eventName,
        TimestampUtc = timestampUtc,
        Data = data
    };

    private void OnPlayerNowPlaying(IServerPlayer player)
    {
        var now = DateTimeOffset.UtcNow;
        _playerJoinedAtUtc[player.PlayerUID] = now;
        _playerLastActivityUtc[player.PlayerUID] = now;
        EmitPlayerEvent("player.joined", player);
    }

    private void OnPlayerDisconnect(IServerPlayer player)
    {
        EmitPlayerEvent("player.left", player);
        _playerJoinedAtUtc.TryRemove(player.PlayerUID, out _);
        _playerLastActivityUtc.TryRemove(player.PlayerUID, out _);
    }
    private void OnPlayerChat(IServerPlayer player, int channelId, ref string message, ref string data, BoolRef consumed)
    {
        if (consumed.value || string.IsNullOrWhiteSpace(message) || message.StartsWith('/')) return;
        _playerLastActivityUtc[player.PlayerUID] = DateTimeOffset.UtcNow;
        var payload = PlayerData(player);
        payload["message"] = message;
        payload["channelId"] = channelId;
        EmitEvent("chat", payload);
    }

    private void OnLoggerEntryAdded(EnumLogType type, string message, params object[] args)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        string formatted;
        try { formatted = args.Length == 0 ? message : string.Format(message, args); }
        catch (FormatException) { formatted = message; }
        if (TryEmitExactDeathLog(type, formatted)) return;
        if (type != EnumLogType.Notification) return;
        if (ServerBroadcastLogParser.TryParse(formatted, out var content))
            EmitEvent("server.notification", new JsonObject { ["message"] = content });
    }

    private bool TryEmitExactDeathLog(EnumLogType type, string formatted)
    {
        if (type != EnumLogType.Audit || !ServerDeathLogParser.TryParse(formatted, out var playerName, out var deathMessage))
            return false;

        var now = Environment.TickCount64;
        if (_recentExactDeathLogs.TryGetValue(playerName, out var previousAt) && now - previousAt < 2000)
            return true;
        _recentExactDeathLogs[playerName] = now;
        foreach (var item in _recentExactDeathLogs)
            if (now - item.Value > 10000) _recentExactDeathLogs.TryRemove(item.Key, out _);
        var data = new JsonObject { ["name"] = playerName };
        data["deathMessage"] = deathMessage;
        data["reason"] = deathMessage;
        data["reasonSource"] = "server-log";
        EmitEvent("player.died", data);
        return true;
    }

    private void EmitPlayerEvent(string eventName, IServerPlayer player)
    {
        if (!ShouldEmitPlayerEvent(eventName, player)) return;
        EmitEvent(eventName, PlayerData(player));
    }

    private bool ShouldEmitPlayerEvent(string eventName, IServerPlayer player)
    {
        var key = $"{eventName}\n{player.PlayerUID}";
        var now = Environment.TickCount64;
        if (_recentPlayerEvents.TryGetValue(key, out var previous) && now - previous < 2000)
            return false;
        _recentPlayerEvents[key] = now;
        foreach (var item in _recentPlayerEvents)
            if (now - item.Value > 10000) _recentPlayerEvents.TryRemove(item.Key, out _);
        return true;
    }

    private void OnPerformanceTick(float elapsedSeconds)
    {
        lock (_performanceLock)
        {
            _performanceTickCount++;
            var now = Environment.TickCount64;
            var elapsedMs = now - _performanceWindowStartedMs;
            if (elapsedMs < 1000) return;
            _measuredTps = _performanceTickCount * 1000d / elapsedMs;
            _measuredTickTimeMs = _measuredTps > 0 ? 1000d / _measuredTps : 0;
            _performanceTickCount = 0;
            _performanceWindowStartedMs = now;
        }
    }

    private JsonObject PlayerData(IServerPlayer player)
    {
        var joinedAt = _playerJoinedAtUtc.GetOrAdd(player.PlayerUID, static _ => DateTimeOffset.UtcNow);
        var lastActivity = _playerLastActivityUtc.GetOrAdd(player.PlayerUID, joinedAt);
        return new JsonObject
        {
            ["uid"] = player.PlayerUID,
            ["name"] = player.PlayerName,
            ["connectionState"] = player.ConnectionState.ToString(),
            ["joinedAtUtc"] = joinedAt,
            ["lastActivityUtc"] = lastActivity
        };
    }

    private async Task<ServerBridgeResponse> ProcessQueryAsync(ServerBridgeRequest request, CancellationToken cancellationToken)
    {
        if (!await _querySlots.WaitAsync(TimeSpan.Zero, cancellationToken))
            return Failure("Concurrent query limit reached.", "server-busy");
        try
        {
            var api = _serverApi;
            if (api is null) return Failure("Server API is unavailable.", "server-not-ready");
            var completion = new TaskCompletionSource<ServerBridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            api.Event.EnqueueMainThreadTask(() =>
            {
                try
                {
                    if (TryResolveExtensionProvider(request.Method, out var provider))
                    {
                        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
                        var previousTicks = _extensionLastQueryTicks.GetOrAdd(request.Method, 0);
                        if (previousTicks > 0 && TimeSpan.FromTicks(nowTicks - previousTicks) < TimeSpan.FromMilliseconds(100))
                        {
                            completion.TrySetResult(Failure("Extension query rate limit exceeded.", "rate-limited"));
                            return;
                        }
                        _extensionLastQueryTicks[request.Method] = nowTicks;
                        _ = CompleteExtensionQueryAsync(provider, request, completion, cancellationToken);
                        return;
                    }
                    if (string.Equals(request.Method, "player.info", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!_configuration.IncludeExtendedPlayerInfo)
                        {
                            completion.TrySetResult(Failure("Extended player information is not enabled.", "unsupported-capability"));
                            return;
                        }
                        var playerInfo = BuildPlayerInfo(api, request.Arguments);
                        completion.TrySetResult(playerInfo is null
                            ? Failure("The requested player is not online.", "player-not-online")
                            : new ServerBridgeResponse { Version = 2, Success = true, Id = request.Id, Data = playerInfo, BridgeVersion = BridgeVersion });
                        return;
                    }
                    if (string.Equals(request.Method, "player.consume", StringComparison.OrdinalIgnoreCase))
                    {
                        completion.TrySetResult(TryConsumePlayerItem(api, request.Arguments));
                        return;
                    }
                    JsonObject? data = request.Method switch
                    {
                        "server.status" => BuildServerStatus(api),
                        "players.list" => BuildPlayers(api),
                        "server.capabilities" => BuildCapabilities(),
                        "world.status" when _configuration.IncludeWorldDetails => BuildWorldStatus(api),
                        _ => null
                    };
                    completion.TrySetResult(data is null ? Failure("Query capability is not available.", "unsupported-capability") : new ServerBridgeResponse { Version = 2, Success = true, Id = request.Id, Data = data, BridgeVersion = BridgeVersion });
                }
                catch (Exception ex) { completion.TrySetResult(Failure(ex.Message, "query-failed")); }
            }, "launchergoserverbridge-query");
            try { return await completion.Task.WaitAsync(cancellationToken); } catch (OperationCanceledException) { return Failure("Query timed out.", "timeout"); }
        }
        finally
        {
            _querySlots.Release();
        }
    }

    private bool TryResolveExtensionProvider(string method, out IServerBridgeExtensionProvider provider)
    {
        provider = null!;
        if (!method.StartsWith("ext.", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = method.Split('.', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 3 && _extensionProviders.TryGetValue(parts[1], out provider!);
    }

    private static async Task CompleteExtensionQueryAsync(
        IServerBridgeExtensionProvider provider,
        ServerBridgeRequest request,
        TaskCompletionSource<ServerBridgeResponse> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            var prefix = $"ext.{provider.ProviderId}.";
            var shortMethod = request.Method.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? request.Method[prefix.Length..]
                : request.Method;
            if (!provider.Capabilities.Contains(request.Method, StringComparer.OrdinalIgnoreCase) &&
                !provider.Capabilities.Contains(shortMethod, StringComparer.OrdinalIgnoreCase))
            {
                completion.TrySetResult(Failure("Extension capability is not available.", "unsupported-capability"));
                return;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var data = await provider.QueryAsync(shortMethod, request.Arguments, timeout.Token);
            if (data is null)
            {
                completion.TrySetResult(Failure("Extension returned no data.", "unsupported-capability"));
                return;
            }
            if (Encoding.UTF8.GetByteCount(data.ToJsonString()) > MaximumExtensionResultBytes)
            {
                completion.TrySetResult(Failure("Extension result is too large.", "result-too-large"));
                return;
            }
            completion.TrySetResult(new ServerBridgeResponse { Version = 2, Id = request.Id, Success = true, Data = data, BridgeVersion = BridgeVersion });
        }
        catch (OperationCanceledException) { completion.TrySetResult(Failure("Extension query timed out.", "timeout")); }
        catch (UnauthorizedAccessException ex) { completion.TrySetResult(Failure(ex.Message, "permission-denied")); }
        catch (Exception ex) { completion.TrySetResult(Failure(ex.Message, "extension-error")); }
    }

    private JsonObject BuildPlayers(ICoreServerAPI api)
    {
        var players = new JsonArray();
        foreach (var value in api.World.AllOnlinePlayers)
        {
            if (value is not IServerPlayer player) continue;
            players.Add(BuildPlayerInfoData(api, player, includeInventory: false));
        }
        return new JsonObject
        {
            ["players"] = players,
            ["count"] = players.Count,
            ["maxPlayers"] = api.Server.Config.MaxClients
        };
    }

    private JsonObject? BuildPlayerInfo(ICoreServerAPI api, JsonObject? arguments)
    {
        var uid = arguments?["uid"] is JsonValue uidValue && uidValue.TryGetValue<string>(out var uidText)
            ? uidText.Trim()
            : string.Empty;
        var name = arguments?["name"] is JsonValue nameValue && nameValue.TryGetValue<string>(out var nameText)
            ? nameText.Trim()
            : string.Empty;
        if (string.IsNullOrWhiteSpace(uid) && string.IsNullOrWhiteSpace(name)) return null;
        var player = api.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault(candidate =>
            (!string.IsNullOrWhiteSpace(uid) && string.Equals(candidate.PlayerUID, uid, StringComparison.Ordinal)) ||
            (!string.IsNullOrWhiteSpace(name) && string.Equals(candidate.PlayerName, name, StringComparison.OrdinalIgnoreCase)));
        return player is null ? null : BuildPlayerInfoData(api, player, includeInventory: true);
    }

    private static ServerBridgeResponse TryConsumePlayerItem(ICoreServerAPI api, JsonObject? arguments)
    {
        var uid = arguments?["uid"]?.ToString()?.Trim() ?? string.Empty;
        var name = arguments?["name"]?.ToString()?.Trim() ?? string.Empty;
        var requestedCode = arguments?["code"]?.ToString()?.Trim() ?? string.Empty;
        var requestedId = arguments?["id"] is JsonValue idValue && idValue.TryGetValue<int>(out var id) ? id : 0;
        var requestedQuantity = arguments?["quantity"] is JsonValue quantityValue && quantityValue.TryGetValue<int>(out var quantity) ? quantity : 0;
        if (string.IsNullOrWhiteSpace(uid) && string.IsNullOrWhiteSpace(name))
            return Failure("A player UID or name is required.", "invalid-arguments");
        if (!string.Equals(requestedCode, "game:gear-temporal", StringComparison.OrdinalIgnoreCase) || requestedId != 1899 || requestedQuantity != 1)
            return Failure("Only one game:gear-temporal (item ID 1899) may be consumed.", "invalid-arguments");

        var player = api.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault(candidate =>
            (!string.IsNullOrWhiteSpace(uid) && string.Equals(candidate.PlayerUID, uid, StringComparison.Ordinal)) ||
            (!string.IsNullOrWhiteSpace(name) && string.Equals(candidate.PlayerName, name, StringComparison.OrdinalIgnoreCase)));
        if (player is null || player.ConnectionState != EnumClientState.Playing)
            return Failure("The requested player is not online.", "player-not-online");

        foreach (var inventory in player.InventoryManager.Inventories.Values)
        {
            if (!IsPlayerCarriedInventory(inventory.ClassName)) continue;
            foreach (var slot in inventory)
            {
                var stack = slot.Itemstack;
                var code = stack?.Collectible?.Code?.ToString() ?? string.Empty;
                if (stack is null || stack.StackSize < 1 || stack.Collectible?.Id != requestedId || !string.Equals(code, requestedCode, StringComparison.OrdinalIgnoreCase))
                    continue;

                slot.TakeOut(1);
                slot.MarkDirty();
                return new ServerBridgeResponse
                {
                    Version = 2,
                    Success = true,
                    Data = new JsonObject { ["consumed"] = true, ["code"] = requestedCode, ["id"] = requestedId, ["quantity"] = 1 },
                    BridgeVersion = BridgeVersion
                };
            }
        }

        return Failure("The player does not have the required item.", "item-not-found");
    }

    private JsonObject BuildPlayerInfoData(ICoreServerAPI api, IServerPlayer player, bool includeInventory)
    {
        var ping = float.IsFinite(player.Ping) && player.Ping >= 0 ? (int?)Math.Round(player.Ping * 1000f) : null;
        var joinedAt = _playerJoinedAtUtc.GetOrAdd(player.PlayerUID, static _ => DateTimeOffset.UtcNow);
        var lastActivity = _playerLastActivityUtc.GetOrAdd(player.PlayerUID, joinedAt);
        var item = new JsonObject
        {
            ["uid"] = player.PlayerUID,
            ["name"] = player.PlayerName,
            ["online"] = player.ConnectionState != EnumClientState.Offline,
            ["playing"] = player.ConnectionState == EnumClientState.Playing,
            ["connectionState"] = player.ConnectionState.ToString(),
            ["pingMs"] = ping,
            ["joinedAtUtc"] = joinedAt,
            ["lastActivityUtc"] = lastActivity
        };
        if (!_configuration.IncludeExtendedPlayerInfo) return item;

        item["gameMode"] = player.WorldData.CurrentGameMode.ToString();
        item["role"] = player.Role?.Code;
        var entity = player.Entity;
        if (entity is not null)
        {
            item["dimension"] = entity.Pos.Dimension;
            var origin = api.World.DefaultSpawnPosition;
            item["x"] = Math.Round(entity.Pos.X - origin.X, 2);
            item["y"] = entity.Pos.Y;
            item["z"] = Math.Round(entity.Pos.Z - origin.Z, 2);
            item["coordinateSystem"] = "spawn-relative";
            AddPlayerVitals(item, entity);
        }
        if (includeInventory) AddPlayerInventory(item, player);
        return item;
    }

    private static void AddPlayerVitals(JsonObject item, EntityPlayer entity)
    {
        item["alive"] = entity.Alive;
        var health = entity.WatchedAttributes.GetTreeAttribute("health");
        AddTreeFloat(item, "health", health, "currenthealth");
        AddTreeFloat(item, "maxHealth", health, "maxhealth");
        var hunger = entity.WatchedAttributes.GetTreeAttribute("hunger");
        AddTreeFloat(item, "hunger", hunger, "currentsaturation");
        AddTreeFloat(item, "maxHunger", hunger, "maxsaturation");
    }

    private static void AddTreeFloat(JsonObject target, string outputName, ITreeAttribute? tree, string attributeName)
    {
        if (tree is null || !tree.HasAttribute(attributeName)) return;
        var value = tree.GetFloat(attributeName);
        if (float.IsFinite(value)) target[outputName] = Math.Round(value, 2);
    }

    private static void AddPlayerInventory(JsonObject item, IServerPlayer player)
    {
        var totals = new Dictionary<string, InventorySummaryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var inventory in player.InventoryManager.Inventories.Values)
        {
            if (!IsPlayerCarriedInventory(inventory.ClassName)) continue;
            foreach (var slot in inventory)
            {
                var stack = slot.Itemstack;
                if (stack is null || stack.StackSize <= 0) continue;
                var code = stack.Collectible?.Code?.ToString() ?? $"{stack.Class}:{stack.Id}";
                string name;
                try { name = stack.GetName(); }
                catch { name = code; }
                if (string.IsNullOrWhiteSpace(name)) name = code;
                if (totals.TryGetValue(code, out var existing))
                    totals[code] = existing with { Quantity = existing.Quantity + stack.StackSize };
                else
                    totals[code] = new InventorySummaryEntry(code, name.Trim(), stack.StackSize);
            }
        }

        var inventoryItems = new JsonArray();
        foreach (var entry in totals.Values
                     .OrderBy(static value => value.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static value => value.Code, StringComparer.OrdinalIgnoreCase))
        {
            inventoryItems.Add(new JsonObject
            {
                ["code"] = entry.Code,
                ["name"] = entry.Name,
                ["quantity"] = entry.Quantity
            });
        }
        item["inventory"] = inventoryItems;
        item["inventoryItemKinds"] = inventoryItems.Count;
        item["inventoryTotalItems"] = totals.Values.Sum(static value => value.Quantity);
    }

    private static bool IsPlayerCarriedInventory(string? className) =>
        string.Equals(className, GlobalConstants.hotBarInvClassName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(className, GlobalConstants.backpackInvClassName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(className, GlobalConstants.characterInvClassName, StringComparison.OrdinalIgnoreCase);

    private sealed record InventorySummaryEntry(string Code, string Name, int Quantity);

    private JsonObject BuildServerStatus(ICoreServerAPI api)
    {
        var config = api.Server.Config;
        var whitelist = config.WhitelistMode == EnumWhitelistMode.On ||
                        (config.WhitelistMode == EnumWhitelistMode.Default && api.Server.IsDedicated);
        var status = new JsonObject
        {
            ["name"] = config.ServerName,
            ["status"] = api.Server.IsShuttingDown ? "shutting-down" : api.Server.CurrentRunPhase.ToString(),
            ["version"] = GameVersion.LongGameVersion,
            ["apiVersion"] = GameVersion.APIVersion,
            ["onlinePlayers"] = api.World.AllOnlinePlayers.OfType<IServerPlayer>().Count(x => x.ConnectionState == EnumClientState.Playing),
            ["maxPlayers"] = config.MaxClients,
            ["worldName"] = api.World.WorldName,
            ["address"] = $"{api.Server.ServerIp}:{config.Port}",
            ["description"] = ReadStringProperty(config, "Description"),
            ["welcomeMessage"] = config.WelcomeMessage,
            ["whitelistEnabled"] = whitelist,
            ["passwordProtected"] = !string.IsNullOrWhiteSpace(config.Password),
            ["uptimeSeconds"] = api.Server.ServerUptimeSeconds
        };
        if (_configuration.IncludePerformanceInfo)
        {
            var performance = new JsonObject();
            lock (_performanceLock)
            {
                if (_measuredTps > 0) performance["tps"] = Math.Round(_measuredTps, 2);
                if (_measuredTickTimeMs > 0) performance["averageTickTimeMs"] = Math.Round(_measuredTickTimeMs, 2);
            }
            AddNumericProperty(performance, "tps", api.Server, "Tps", "TPS", "TicksPerSecond");
            AddNumericProperty(performance, "averageTickTimeMs", api.Server, "AverageTickTimeMs", "AvgTickTimeMs");
            AddNumericProperty(performance, "loadedChunks", api.World, "LoadedChunkCount", "LoadedChunks");
            AddNumericProperty(performance, "entityCount", api.World, "EntityCount");
            if (performance.Count > 0) status["performance"] = performance;
        }
        if (_configuration.IncludeWorldDetails)
        {
            var world = BuildWorldStatus(api);
            status["worldTime"] = world["worldTime"]?.DeepClone();
            status["season"] = world["season"]?.DeepClone();
            status["year"] = world["year"]?.DeepClone();
            status["month"] = world["month"]?.DeepClone();
            status["day"] = world["day"]?.DeepClone();
        }
        return status;
    }

    private JsonObject BuildCapabilities()
    {
        var queries = new JsonArray("server.status", "players.list", "server.capabilities");
        if (_configuration.IncludeExtendedPlayerInfo) queries.Add("player.info");
        if (_configuration.IncludeWorldDetails) queries.Add("world.status");
        foreach (var provider in _extensionProviders.Values)
        {
            var prefix = $"ext.{provider.ProviderId}.";
            foreach (var capability in provider.Capabilities
                         .Where(x => !string.IsNullOrWhiteSpace(x))
                         .Select(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? x : prefix + x.TrimStart('.'))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                queries.Add(capability);
        }
        return new JsonObject
        {
            ["protocolVersion"] = 2,
            ["queries"] = queries,
            ["events"] = new JsonArray(_configuration.EventTypes.Select(x => (JsonNode?)x).ToArray()),
            ["extendedPlayerInfo"] = _configuration.IncludeExtendedPlayerInfo,
            ["worldDetails"] = _configuration.IncludeWorldDetails,
            ["performanceInfo"] = _configuration.IncludePerformanceInfo,
            ["sensitiveFields"] = _configuration.IncludeSensitiveFields
        };
    }

    private static JsonObject BuildWorldStatus(ICoreServerAPI api)
    {
        var calendar = api.World.Calendar;
        var daysPerMonth = Math.Max(1, calendar.DaysPerMonth);
        var dayOfYear = Math.Max(0, calendar.DayOfYear);
        var month = dayOfYear / daysPerMonth + 1;
        var day = dayOfYear % daysPerMonth + 1;
        var totalSeconds = Math.Max(0, calendar.HourOfDay) * 3600d;
        var hour = (int)(totalSeconds / 3600d) % 24;
        var minute = (int)(totalSeconds / 60d) % 60;
        var second = (int)totalSeconds % 60;
        var season = calendar.GetSeason(api.World.DefaultSpawnPosition.AsBlockPos).ToString();
        var worldTime = $"第{calendar.Year}年 {month}月 {day}日 {hour:00}:{minute:00}:{second:00}";
        return new JsonObject
        {
            ["worldName"] = api.World.WorldName,
            ["calendarTotalHours"] = calendar.TotalHours,
            ["calendarSpeedOfTime"] = calendar.SpeedOfTime,
            ["year"] = calendar.Year,
            ["month"] = month,
            ["day"] = day,
            ["time"] = $"{hour:00}:{minute:00}:{second:00}",
            ["worldTime"] = worldTime,
            ["season"] = season,
            ["dimension"] = 0
        };
    }

    private static void AddNumericProperty(JsonObject target, string key, object source, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = source.GetType().GetProperty(propertyName)?.GetValue(source);
            if (value is null) continue;
            try
            {
                target[key] = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                return;
            }
            catch (FormatException) { }
            catch (InvalidCastException) { }
        }
    }

    private static string ReadStringProperty(object source, string name) =>
        source.GetType().GetProperty(name)?.GetValue(source)?.ToString() ?? string.Empty;

    private static ServerBridgeConfiguration LoadConfiguration(ICoreServerAPI api)
    {
        try
        {
            var configuration = api.LoadModConfig<ServerBridgeConfiguration>(ConfigurationFileName)
                                ?? new ServerBridgeConfiguration();
            return NormalizeConfiguration(configuration);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("LauncherGo Server Bridge could not load configuration: {0}", ex.Message);
            return new ServerBridgeConfiguration();
        }
    }

    private static ServerBridgeConfiguration NormalizeConfiguration(ServerBridgeConfiguration configuration) => new()
    {
        Enabled = configuration.Enabled,
        Port = configuration.Port is >= 1024 and <= 65535 ? configuration.Port : 19090,
        AccessToken = configuration.AccessToken?.Trim() ?? string.Empty,
        MaxCommandLength = Math.Clamp(configuration.MaxCommandLength <= 0 ? 4096 : configuration.MaxCommandLength, 256, 16384),
        QueryTimeoutMilliseconds = Math.Clamp(configuration.QueryTimeoutMilliseconds <= 0 ? 5000 : configuration.QueryTimeoutMilliseconds, 500, 30000),
        IncludeExtendedPlayerInfo = configuration.IncludeExtendedPlayerInfo,
        IncludeWorldDetails = configuration.IncludeWorldDetails,
        IncludePerformanceInfo = configuration.IncludePerformanceInfo,
        IncludeSensitiveFields = configuration.IncludeSensitiveFields,
        EventTypes = (configuration.EventTypes ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToArray()
    };

    private void StopListener()
    {
        try
        {
            _listenerCts?.Cancel();
        }
        catch
        {
            // Nothing to recover during server shutdown.
        }

        try
        {
            _listener?.Stop();
        }
        catch
        {
            // Nothing to recover during server shutdown.
        }

        _listenerCts?.Dispose();
        _listenerCts = null;
        _listener = null;
        _acceptLoop = null;
    }

    private static bool FixedTimeEquals(string? receivedToken, string expectedToken)
    {
        // Decode into fixed-size buffers and always compare the same number of bytes.
        // This avoids making token length or hex validity observable through timing.
        Span<byte> left = stackalloc byte[32];
        Span<byte> right = stackalloc byte[32];
        var leftText = receivedToken?.Trim() ?? string.Empty;
        var rightText = expectedToken?.Trim() ?? string.Empty;
        var leftValid = TryDecodeToken(leftText, left);
        var rightValid = TryDecodeToken(rightText, right);
        var equal = CryptographicOperations.FixedTimeEquals(left, right);
        return leftValid && rightValid && equal;
    }

    private static bool TryDecodeToken(string value, Span<byte> destination)
    {
        destination.Clear();
        if (value.Length != destination.Length * 2) return false;
        try
        {
            var bytes = Convert.FromHexString(value);
            bytes.AsSpan().CopyTo(destination);
            return bytes.Length == destination.Length;
        }
        catch (FormatException)
        {
            destination.Clear();
            return false;
        }
    }

    private static bool IsValidToken(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static ServerBridgeResponse Failure(string error, string code = "bridge-error") => new()
    {
        Version = 2,
        Success = false,
        Error = error,
        ErrorCode = code,
        BridgeVersion = BridgeVersion
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public sealed class ServerBridgeConfiguration
    {
        public bool Enabled { get; set; }
        public int Port { get; set; } = 19090;
        public string AccessToken { get; set; } = string.Empty;
        public int MaxCommandLength { get; set; } = 4096;
        public int QueryTimeoutMilliseconds { get; set; } = 5000;
        public bool IncludeExtendedPlayerInfo { get; set; }
        public bool IncludeWorldDetails { get; set; }
        public bool IncludePerformanceInfo { get; set; }
        public bool IncludeSensitiveFields { get; set; }
        public string[] EventTypes { get; set; } = ["player.joined", "player.left", "player.died", "chat", "server.notification"];
    }

    private sealed class ServerBridgeRequest
    {
        public int Version { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public string NewToken { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public string Method { get; set; } = string.Empty;
        public JsonObject? Arguments { get; set; }
        public string[] Events { get; set; } = [];
        public long Since { get; set; }
        public int MaxQueueSize { get; set; } = 256;
    }

    private sealed class ServerBridgeResponse
    {
        public int Version { get; set; }
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string? BridgeVersion { get; set; }
        public string? Id { get; set; }
        public string? ErrorCode { get; set; }
        public JsonObject? Data { get; set; }
        public string? Type { get; set; }
    }

    private sealed class ServerBridgeEventRecord
    {
        public int Version { get; init; }
        public string Type { get; init; } = "event";
        public long Sequence { get; init; }
        public string Event { get; init; } = string.Empty;
        public DateTimeOffset TimestampUtc { get; init; }
        public JsonObject Data { get; init; } = new();
    }

    private sealed class BridgeSubscriber
    {
        private long _droppedCount;

        public BridgeSubscriber(Channel<ServerBridgeEventRecord> channel, HashSet<string> events)
        {
            Channel = channel;
            Events = events;
        }

        public Channel<ServerBridgeEventRecord> Channel { get; }
        private HashSet<string> Events { get; }

        public bool Accepts(string eventName) => Events.Count == 0 || Events.Contains(eventName) || Events.Contains("*");

        public void RecordDropped() => Interlocked.Increment(ref _droppedCount);

        public long TakeDroppedCount() => Interlocked.Exchange(ref _droppedCount, 0);
    }
}
