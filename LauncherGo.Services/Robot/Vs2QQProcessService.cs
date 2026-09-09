using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Enums;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

public sealed class Vs2QQProcessService
{
    private const int TemporalGearId = 1899;
    private const string TemporalGearCode = "game:gear-temporal";
    private static int _encodingProviderRegistered;

    private const int MaxServerStatusQueryCount = 10;
    private const int MaxOneBotMessageLength = 1800;
    private static readonly TimeSpan RecentRelaySignatureWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan GroupMemberDisplayNameCacheDuration = TimeSpan.FromMinutes(10);
    private static readonly Regex CqImageRegex = new(@"\[CQ:image,[^\]]+\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CqAtRegex = new(@"\[CQ:at,(?<params>[^\]]+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CqAtQqParameterRegex = new(@"(?:^|,)qq=(?<qq>[^,]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CqCodeRegex = new(@"\[CQ:[^\]]+\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MultiWhitespaceRegex = new(@"\s{2,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex QqIdentifierRegex = new(@"^[1-9]\d{4,11}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex QqNumberMentionRegex = new(@"(?<![\p{L}\p{N}_])@[1-9]\d{4,11}(?!\d)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TimePartRegex = new(@"(?<time>\d{2}:\d{2}:\d{2})", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GroupRelayEchoRegex = new(@"^\[(?:群聊|group)\s+\d{1,2}:\d{2}:\d{2}\]", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ServerRelayEchoRegex = new(@"^\[(?:服务器|server)\s+\d{1,2}:\d{2}:\d{2}\]", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly SemaphoreSlim _runtimeGate = new(1, 1);
    private readonly IServerProcessService _serverProcessService;
    private readonly IServerTransport _serverTransport;
    private readonly IInstanceProfileService _instanceProfileService;
    private readonly IInstanceSaveService _instanceSaveService;
    private readonly IInstanceServerConfigService _instanceServerConfigService;
    private readonly IInstanceModService _instanceModService;
    private readonly IModFileArchiveService _modFileArchiveService;
    private readonly IModListExportService _modListExportService;
    private readonly ILauncherPreferencesService _launcherPreferencesService;
    private readonly IServerBridgeService? _serverBridgeService;
    private readonly ServerBridgeStateStore? _serverBridgeStateStore;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private Vs2QQRuntimeContext? _runtime;

    public event EventHandler<string>? OutputReceived;

    public event EventHandler<RobotRuntimeStatus>? StatusChanged;

    public RobotRuntimeStatus CurrentStatus { get; private set; } = new();

    public Vs2QQProcessService(
        IServerProcessService serverProcessService,
        IServerTransport serverTransport,
        IInstanceProfileService instanceProfileService,
        IInstanceSaveService instanceSaveService,
        IInstanceServerConfigService instanceServerConfigService,
        IInstanceModService instanceModService,
        IModFileArchiveService modFileArchiveService,
        IModListExportService modListExportService,
        ILauncherPreferencesService launcherPreferencesService,
        IServerBridgeService? serverBridgeService = null,
        ServerBridgeStateStore? serverBridgeStateStore = null)
    {
        _serverProcessService = serverProcessService;
        _serverTransport = serverTransport;
        _instanceProfileService = instanceProfileService;
        _instanceSaveService = instanceSaveService;
        _instanceServerConfigService = instanceServerConfigService;
        _instanceModService = instanceModService;
        _modFileArchiveService = modFileArchiveService;
        _modListExportService = modListExportService;
        _launcherPreferencesService = launcherPreferencesService;
        _serverBridgeService = serverBridgeService;
        _serverBridgeStateStore = serverBridgeStateStore;
        if (Interlocked.Exchange(ref _encodingProviderRegistered, 1) == 0)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
    }

    public async Task<OperationResult> StartAsync(RobotSettings settings, CancellationToken cancellationToken = default)
    {
        await _runtimeGate.WaitAsync(cancellationToken);
        try
        {
            if (_runtime is not null && CurrentStatus.IsRunning)
            {
                return OperationResult.Failed("VS2QQ 已在运行中。");
            }

            var normalizeResult = NormalizeLaunchSettings(settings);
            if (!normalizeResult.IsSuccess || normalizeResult.Value is null)
            {
                return OperationResult.Failed(normalizeResult.Message ?? "VS2QQ 配置无效。");
            }

            var normalized = normalizeResult.Value;
            var storage = new Vs2QQStorage(normalized.DatabasePath);
            Vs2QQRuntimeContext runtime = new(normalized, storage);
            var oneBot = new Vs2QQOneBotClient(
                normalized.OneBotWsUrl,
                normalized.AccessToken,
                normalized.ReconnectIntervalSec,
                EmitOutput,
                (eventPayload, token) => HandleOneBotEventAsync(runtime, eventPayload, token));
            runtime.OneBot = oneBot;
            _runCts = new CancellationTokenSource();
            var runToken = _runCts.Token;
            if (_serverBridgeService is not null)
            {
                foreach (var profileId in normalized.ProfileBindings.Select(x => x.ProfileId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var profile = _instanceProfileService.GetProfileById(profileId);
                    if (profile is null) continue;
                    runtime.BridgeConnectionTasks.Add(Task.Run(
                        () => MaintainBridgeSubscriptionAsync(runtime, profile, runToken),
                        CancellationToken.None));
                }
            }

            _runtime = runtime;
            _runTask = Task.Run(() => RunRuntimeAsync(runtime, runToken), CancellationToken.None);

            CurrentStatus = new RobotRuntimeStatus
            {
                IsRunning = true,
                ProcessId = Environment.ProcessId,
                StartedAtUtc = DateTimeOffset.UtcNow,
                OneBotWsUrl = normalized.OneBotWsUrl
            };
            StatusChanged?.Invoke(this, CurrentStatus);
            EmitOutput($"[system] VS2QQ 已启动。OneBot={normalized.OneBotWsUrl}");
            EmitOutput($"[system] VS2QQ 数据库：{normalized.DatabasePath}");

            return OperationResult.Success("VS2QQ 已启动。");
        }
        catch (Exception ex)
        {
            return OperationResult.Failed("启动 VS2QQ 失败。", ex);
        }
        finally
        {
            _runtimeGate.Release();
        }
    }

    public async Task<OperationResult> StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
    {
        Task? runTask;
        Vs2QQRuntimeContext? runtime;

        await _runtimeGate.WaitAsync(cancellationToken);
        try
        {
            if (_runtime is null || _runTask is null || !CurrentStatus.IsRunning)
            {
                return OperationResult.Success("VS2QQ 未运行。");
            }

            runTask = _runTask;
            runtime = _runtime;
            _runCts?.Cancel();
        }
        finally
        {
            _runtimeGate.Release();
        }

        try
        {
            var timeoutTask = Task.Delay(gracefulTimeout, cancellationToken);
            var completed = await Task.WhenAny(runTask!, timeoutTask);
            cancellationToken.ThrowIfCancellationRequested();

            if (!ReferenceEquals(completed, runTask))
            {
                return OperationResult.Failed("停止 VS2QQ 超时。");
            }

            await runTask!;
            return OperationResult.Success("VS2QQ 已停止。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return OperationResult.Failed("停止 VS2QQ 失败。", ex);
        }
    }

    private async Task RunRuntimeAsync(Vs2QQRuntimeContext runtime, CancellationToken cancellationToken)
    {
        try
        {
            await runtime.OneBot.RunForeverAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal cancellation.
        }
        catch (Exception ex)
        {
            EmitOutput($"[system] VS2QQ 运行异常: {ex.Message}");
        }
        finally
        {
            await FinalizeRuntimeAsync(runtime);
        }
    }

    private async Task FinalizeRuntimeAsync(Vs2QQRuntimeContext runtime)
    {
        bool shouldNotifyStopped = false;
        string? wsUrl = null;
        CancellationTokenSource? ctsToDispose = null;

        await _runtimeGate.WaitAsync();
        try
        {
            if (!ReferenceEquals(_runtime, runtime))
            {
                return;
            }

            wsUrl = runtime.Settings.OneBotWsUrl;
            ctsToDispose = _runCts;
            _runCts = null;
            _runTask = null;
            _runtime = null;
            shouldNotifyStopped = CurrentStatus.IsRunning;

            CurrentStatus = new RobotRuntimeStatus
            {
                IsRunning = false,
                ProcessId = null,
                StartedAtUtc = null,
                OneBotWsUrl = wsUrl
            };
        }
        finally
        {
            _runtimeGate.Release();
        }

        ctsToDispose?.Cancel();
        try { await Task.WhenAll(runtime.BridgeConnectionTasks).ConfigureAwait(false); } catch { }
        foreach (var subscription in runtime.BridgeSubscriptions)
        {
            try { await subscription.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        runtime.BridgeSubscriptions.Clear();
        ctsToDispose?.Dispose();
        await runtime.DisposeAsync();

        if (shouldNotifyStopped)
        {
            StatusChanged?.Invoke(this, CurrentStatus);
            EmitOutput("[system] VS2QQ 已停止。");
        }
    }

    private async Task HandleOneBotEventAsync(Vs2QQRuntimeContext runtime, JsonObject eventPayload, CancellationToken cancellationToken)
    {
        if (!string.Equals(GetString(eventPayload, "post_type"), "message", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var userId = GetInt64(eventPayload, "user_id");
        var selfId = GetInt64(eventPayload, "self_id", -1);
        if (userId > 0 && userId == selfId)
        {
            return;
        }

        var rawMessage = (await ExtractPlainTextAsync(runtime, eventPayload, cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            return;
        }

        if (rawMessage.StartsWith('/'))
        {
            try
            {
                await HandleCommandAsync(runtime, eventPayload, rawMessage, cancellationToken);
            }
            catch (Exception ex)
            {
                EmitOutput($"[warn] 命令处理异常: {ex.Message}");
                try
                {
                    await ReplyAsync(runtime, eventPayload, $"命令执行异常：{ex.Message}", cancellationToken);
                }
                catch (Exception replyEx)
                {
                    EmitOutput($"[warn] 命令异常回包失败: {replyEx.Message}");
                }
            }
            return;
        }

        if (TryBuildOutboundGroupMessage(runtime, eventPayload, rawMessage, out var outboundMessage))
        {
            var groupId = GetInt64(eventPayload, "group_id");
            if (groupId > 0)
            {
                await SendToGameServerAsync(runtime, groupId, outboundMessage, cancellationToken);
            }
            return;
        }
    }

    private async Task HandleCommandAsync(
        Vs2QQRuntimeContext runtime,
        JsonObject eventPayload,
        string rawCommand,
        CancellationToken cancellationToken)
    {
        var firstSpace = rawCommand.IndexOf(' ');
        var command = (firstSpace >= 0 ? rawCommand[..firstSpace] : rawCommand).Trim().ToLowerInvariant();
        var args = firstSpace >= 0 ? rawCommand[(firstSpace + 1)..].Trim() : string.Empty;

        switch (command)
        {
            case "/help":
                await ReplyAsync(runtime, eventPayload, BuildHelpText(runtime), cancellationToken);
                return;
            case "/send":
                await HandleSendCommandAsync(runtime, eventPayload, args, cancellationToken);
                return;
            case "/server":
                await HandleServerCommandAsync(runtime, eventPayload, args, cancellationToken);
                return;
            case "/bind":
                await HandleBindCommandAsync(runtime, eventPayload, args, cancellationToken);
                return;
            case "/myinfo":
                await HandleMyInfoCommandAsync(runtime, eventPayload, args, cancellationToken);
                return;
            case "/tp":
                await HandleTeleportCommandAsync(runtime, eventPayload, args, cancellationToken);
                return;
            case "/modslist":
                await HandleModsListCommandAsync(runtime, eventPayload, args, cancellationToken);
                return;
            case "/modfile":
                await HandleModFileCommandAsync(
                    runtime,
                    eventPayload,
                    args,
                    ModFileArchiveScope.UniversalOnly,
                    cancellationToken);
                return;
            case "/modfileall":
                await HandleModFileCommandAsync(
                    runtime,
                    eventPayload,
                    args,
                    ModFileArchiveScope.All,
                    cancellationToken);
                return;
            default:
                if (runtime.CustomCommands.TryGetValue(command, out var customCommand))
                {
                    await ReplyAsync(
                        runtime,
                        eventPayload,
                        RobotOneBotMessageBuilder.BuildCustomMessage(customCommand),
                        cancellationToken);
                    return;
                }

                await ReplyAsync(runtime, eventPayload, "Unknown command. Use /help.", cancellationToken);
                return;
        }
    }

    private async Task HandleSendCommandAsync(
        Vs2QQRuntimeContext runtime,
        JsonObject eventPayload,
        string args,
        CancellationToken cancellationToken)
    {
        if (!HasAdminPermission(runtime, eventPayload))
        {
            await ReplyAsync(runtime, eventPayload, "Permission denied. Super admin only.", cancellationToken);
            return;
        }

        var commandText = NormalizeServerCommand(args);
        if (string.IsNullOrWhiteSpace(commandText))
        {
            await ReplyAsync(runtime, eventPayload, "Usage: /send <server_command>", cancellationToken);
            return;
        }

        var targetResolution = ResolveServerCommandProfile(runtime, eventPayload, string.Empty);
        if (!string.IsNullOrWhiteSpace(targetResolution.ErrorMessage) || targetResolution.Profile is null)
        {
            await ReplyAsync(runtime, eventPayload, targetResolution.ErrorMessage, cancellationToken);
            return;
        }

        await _serverProcessService.SendCommandAsync(
            targetResolution.Profile.Id,
            commandText,
            cancellationToken);
        await ReplyAsync(runtime, eventPayload, $"已发送服务端指令：{commandText}", cancellationToken);
    }

    private async Task HandleBindCommandAsync(
        Vs2QQRuntimeContext runtime,
        JsonObject eventPayload,
        string playerName,
        CancellationToken cancellationToken)
    {
        if (!IsGroupMessage(eventPayload))
        {
            await ReplyAsync(runtime, eventPayload, "请在已绑定服务器的 QQ 群中使用 /bind <游戏玩家名>。", cancellationToken);
            return;
        }

        var groupId = GetInt64(eventPayload, "group_id");
        var qqUserId = GetInt64(eventPayload, "user_id");
        var normalizedName = NormalizeDisplayText(playerName);
        if (groupId <= 0 || !runtime.BoundGroupIds.Contains(groupId))
        {
            await ReplyAsync(runtime, eventPayload, "当前群未绑定服务器档案。", cancellationToken);
            return;
        }
        if (qqUserId <= 0)
        {
            await ReplyAsync(runtime, eventPayload, "无法识别当前 QQ 用户。", cancellationToken);
            return;
        }
        if (string.IsNullOrWhiteSpace(normalizedName) || normalizedName.Length > 64 || normalizedName.Any(char.IsWhiteSpace))
        {
            await ReplyAsync(runtime, eventPayload, "用法：/bind <游戏玩家名>", cancellationToken);
            return;
        }

        var profileId = runtime.GetPrimaryProfileIdForGroup(groupId);
        if (string.IsNullOrWhiteSpace(profileId))
        {
            await ReplyAsync(runtime, eventPayload, "当前群绑定了多个服务器档案，无法确定玩家所在服务器。", cancellationToken);
            return;
        }
        var profile = _instanceProfileService.GetProfileById(profileId);
        if (profile is null)
        {
            await ReplyAsync(runtime, eventPayload, "绑定的服务器档案不存在。", cancellationToken);
            return;
        }

        var result = await QueryBridgeForProfileAsync(profile, "players.list", cancellationToken);
        if (result?.Success != true || result.Data is null)
        {
            await ReplyAsync(runtime, eventPayload, "服务器桥接不可用，暂时无法发起绑定验证。", cancellationToken);
            return;
        }
        var player = result.Data["players"] is JsonArray players
            ? players.OfType<JsonObject>().FirstOrDefault(item =>
                string.Equals(ReadString(item, "name"), normalizedName, StringComparison.OrdinalIgnoreCase) &&
                (ReadBool(item, "playing") ?? ReadBool(item, "online") is not false))
            : null;
        if (player is null)
        {
            await ReplyAsync(runtime, eventPayload, $"未在服务器 {profile.Name} 找到在线玩家：{normalizedName}", cancellationToken);
            return;
        }

        var canonicalName = Safe(ReadString(player, "name"));
        runtime.PlayerBindings.CreatePending(
            qqUserId,
            groupId,
            profile.Id,
            ReadString(player, "uid"),
            canonicalName);
        await ReplyAsync(
            runtime,
            eventPayload,
            $"绑定验证已开始：请使用游戏玩家 {canonicalName} 在服务器聊天中发送 QQ 号 {qqUserId}。验证在 10 分钟内有效。",
            cancellationToken);
    }

    private async Task HandleMyInfoCommandAsync(
        Vs2QQRuntimeContext runtime,
        JsonObject eventPayload,
        string args,
        CancellationToken cancellationToken)
    {
        if (IsGroupMessage(eventPayload))
        {
            await ReplyAsync(runtime, eventPayload, "该指令包含个人信息，请私聊机器人使用 /myinfo。", cancellationToken);
            return;
        }
        if (!string.IsNullOrWhiteSpace(args))
        {
            await ReplyAsync(runtime, eventPayload, "用法：/myinfo", cancellationToken);
            return;
        }

        var qqUserId = GetInt64(eventPayload, "user_id");
        var binding = qqUserId > 0 ? runtime.PlayerBindings.GetBinding(qqUserId) : null;
        if (binding is null)
        {
            await ReplyAsync(runtime, eventPayload, "尚未绑定游戏玩家，请先在服务器绑定群使用 /bind <游戏玩家名>。", cancellationToken);
            return;
        }
        var profile = _instanceProfileService.GetProfileById(binding.ProfileId);
        if (profile is null)
        {
            await ReplyAsync(runtime, eventPayload, "绑定的服务器档案已不存在，请重新绑定。", cancellationToken);
            return;
        }

        var result = await QueryBridgeForProfileAsync(
            profile,
            "player.info",
            cancellationToken,
            new JsonObject { ["uid"] = binding.PlayerUid, ["name"] = binding.PlayerName });
        var player = result?.Success == true ? result.Data : null;
        if (player is null)
        {
            var message = result is null
                ? "服务器桥接不可用，暂时无法读取玩家信息。"
                : string.Equals(result.ErrorCode, "unsupported-capability", StringComparison.OrdinalIgnoreCase)
                    ? "服务器桥接未启用扩展玩家信息，请由管理员启用后重试。"
                    : string.Equals(result.ErrorCode, "player-not-online", StringComparison.OrdinalIgnoreCase)
                        ? $"绑定玩家 {binding.PlayerName} 当前不在线，无法读取实时信息。"
                        : $"玩家信息查询失败：{Safe(result.Error)}";
            await ReplyAsync(runtime, eventPayload, message, cancellationToken);
            return;
        }

        foreach (var message in SplitOneBotMessages(BuildMyInfoLines(profile.Name, player)))
            await ReplyAsync(runtime, eventPayload, message, cancellationToken);
    }

    private async Task HandleTeleportCommandAsync(
        Vs2QQRuntimeContext runtime,
        JsonObject eventPayload,
        string pointName,
        CancellationToken cancellationToken)
    {
        if (!IsGroupMessage(eventPayload))
        {
            await ReplyAsync(runtime, eventPayload, "请在已绑定服务器的 QQ 群中使用 /tp <设置点名称>。", cancellationToken);
            return;
        }

        var groupId = GetInt64(eventPayload, "group_id");
        var qqUserId = GetInt64(eventPayload, "user_id");
        var boundProfileIds = runtime.CommandScope.GetProfileIdsForGroup(groupId);
        if (groupId <= 0 || boundProfileIds.Count == 0)
        {
            await ReplyAsync(runtime, eventPayload, "当前群未绑定服务器档案。", cancellationToken);
            return;
        }

        var binding = qqUserId > 0 ? runtime.PlayerBindings.GetBinding(qqUserId) : null;
        if (binding is null)
        {
            await ReplyAsync(runtime, eventPayload, "尚未绑定游戏玩家，请先使用 /bind <游戏玩家名> 完成绑定。", cancellationToken);
            return;
        }

        if (!IsTeleportProfileBoundToGroup(binding.ProfileId, boundProfileIds))
        {
            await ReplyAsync(runtime, eventPayload, "当前群未绑定你所绑定玩家所在的服务器档案。", cancellationToken);
            return;
        }

        var requestedName = pointName.Trim();
        if (string.IsNullOrWhiteSpace(requestedName))
        {
            await ReplyAsync(runtime, eventPayload, BuildTeleportPointUsage(runtime), cancellationToken);
            return;
        }

        if (!runtime.TeleportPoints.TryGetValue(requestedName, out var point))
        {
            await ReplyAsync(runtime, eventPayload, $"未找到设置点：{requestedName}\n{BuildTeleportPointUsage(runtime)}", cancellationToken);
            return;
        }

        if (!TryBuildTeleportServerCommand(binding.PlayerName, point, out var command))
        {
            await ReplyAsync(runtime, eventPayload, "玩家绑定或设置点坐标无效，请重新绑定或联系管理员检查配置。", cancellationToken);
            return;
        }

        var profile = _instanceProfileService.GetProfileById(binding.ProfileId);
        if (profile is null)
        {
            await ReplyAsync(runtime, eventPayload, "绑定的服务器档案不存在。", cancellationToken);
            return;
        }

        var consumeResult = await QueryBridgeForProfileAsync(
            profile,
            "player.consume",
            cancellationToken,
            new JsonObject
            {
                ["uid"] = binding.PlayerUid,
                ["name"] = binding.PlayerName,
                ["code"] = TemporalGearCode,
                ["id"] = TemporalGearId,
                ["quantity"] = 1
            });
        if (consumeResult?.Success != true || consumeResult.Data?["consumed"]?.GetValue<bool>() != true)
        {
            var message = consumeResult?.ErrorCode switch
            {
                "unsupported-capability" => "服务器桥接未启用扩展玩家信息，暂时无法验证时空齿轮。",
                "player-not-online" => $"绑定玩家 {binding.PlayerName} 当前不在线。",
                _ => "传送需要消耗 1 个时空齿轮，你的背包中没有该物品。"
            };
            await ReplyAsync(runtime, eventPayload, message, cancellationToken);
            return;
        }

        await _serverProcessService.SendCommandAsync(binding.ProfileId, command, cancellationToken);
        await ReplyAsync(
            runtime,
            eventPayload,
            $"已将 {binding.PlayerName} 传送到设置点 {point.Name}（{FormatTeleportCoordinate(point.X)}, {FormatTeleportCoordinate(point.Y)}, {FormatTeleportCoordinate(point.Z)}）。",
            cancellationToken);
    }

    private static string BuildTeleportPointUsage(Vs2QQRuntimeContext runtime)
    {
        var names = runtime.TeleportPoints.Values
            .Select(static point => point.Name)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return names.Count == 0
            ? "用法：/tp <设置点名称>。管理员尚未配置设置点。"
            : LimitText($"用法：/tp <设置点名称>\n可用设置点：{string.Join("、", names)}", MaxOneBotMessageLength);
    }

    internal static bool TryBuildTeleportServerCommand(
        string? playerName,
        RobotTeleportPoint? point,
        out string command)
    {
        command = string.Empty;
        var normalizedPlayerName = playerName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedPlayerName) ||
            normalizedPlayerName.Length > 64 ||
            normalizedPlayerName.Any(char.IsWhiteSpace) ||
            normalizedPlayerName.Any(char.IsControl) ||
            !RobotTeleportPointRules.TryNormalize(point, out var normalizedPoint))
        {
            return false;
        }

        command = $"/tp {normalizedPlayerName} {FormatTeleportCoordinate(normalizedPoint.X)} {FormatTeleportCoordinate(normalizedPoint.Y)} {FormatTeleportCoordinate(normalizedPoint.Z)}";
        return true;
    }

    internal static bool IsTeleportProfileBoundToGroup(
        string? playerProfileId,
        IEnumerable<string>? groupProfileIds)
    {
        var profileId = playerProfileId?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(profileId) &&
               (groupProfileIds ?? []).Any(id => string.Equals(id?.Trim(), profileId, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatTeleportCoordinate(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    private async Task HandleModsListCommandAsync(
        Vs2QQRuntimeContext runtime,
        JsonObject eventPayload,
        string args,
        CancellationToken cancellationToken)
    {
        if (!HasAdminPermission(runtime, eventPayload))
        {
            await ReplyAsync(runtime, eventPayload, "Permission denied. Super admin only.", cancellationToken);
            return;
        }

        if (!TryParseModListExportFormat(args, out var format))
        {
            await ReplyAsync(runtime, eventPayload, "Usage: /modslist txt|pdf|md|xlsx|csv", cancellationToken);
            return;
        }

        var targetResolution = ResolveServerCommandProfile(runtime, eventPayload, string.Empty);
        if (!string.IsNullOrWhiteSpace(targetResolution.ErrorMessage) || targetResolution.Profile is null)
        {
            await ReplyAsync(runtime, eventPayload, targetResolution.ErrorMessage, cancellationToken);
            return;
        }

        var profile = targetResolution.Profile;
        var mods = await _instanceModService.GetModsAsync(profile, cancellationToken);
        if (format == ModListExportFormat.Txt)
        {
            var lines = mods
                .OrderBy(static mod => mod.ModId, StringComparer.OrdinalIgnoreCase)
                .Select(static mod => $"{(string.IsNullOrWhiteSpace(mod.Name) ? mod.ModId : mod.Name).Trim()} {mod.Version.Trim()}")
                .ToList();
            foreach (var message in SplitOneBotMessages(lines))
            {
                await ReplyAsync(runtime, eventPayload, message, cancellationToken);
            }

            return;
        }

        var exportDirectory = Path.Combine(WorkspacePathHelper.RobotRoot, "exports");
        Directory.CreateDirectory(exportDirectory);
        var extension = _modListExportService.GetFileExtension(format);
        var fileName = $"mods-{SanitizeFileName(profile.Name)}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.{extension}";
        var outputPath = Path.Combine(exportDirectory, fileName);

        await using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            await _modListExportService.ExportAsync(profile, mods, format, output, cancellationToken);
        }

        try
        {
            if (IsGroupMessage(eventPayload))
            {
                var groupId = GetInt64(eventPayload, "group_id");
                if (groupId <= 0)
                    throw new InvalidOperationException("Unable to identify the group.");
                await runtime.OneBot.UploadGroupFileAsync(groupId, outputPath, fileName, cancellationToken);
            }
            else
            {
                var userId = GetInt64(eventPayload, "user_id");
                if (userId <= 0)
                    throw new InvalidOperationException("Unable to identify the user.");
                await runtime.OneBot.UploadPrivateFileAsync(userId, outputPath, fileName, cancellationToken);
            }

            await ReplyAsync(runtime, eventPayload, $"已输出模组清单：{fileName}", cancellationToken);
        }
        finally
        {
            TryDeleteFile(outputPath);
        }
    }

    private async Task HandleModFileCommandAsync(
        Vs2QQRuntimeContext runtime,
        JsonObject eventPayload,
        string args,
        ModFileArchiveScope scope,
        CancellationToken cancellationToken)
    {
        var commandName = scope == ModFileArchiveScope.All ? "/modfileall" : "/modfile";
        if (!HasAdminPermission(runtime, eventPayload))
        {
            await ReplyAsync(runtime, eventPayload, "Permission denied. Super admin only.", cancellationToken);
            return;
        }

        if (!IsGroupMessage(eventPayload))
        {
            await ReplyAsync(runtime, eventPayload, $"Use {commandName} in a group chat.", cancellationToken);
            return;
        }

        if (!string.IsNullOrWhiteSpace(args))
        {
            await ReplyAsync(runtime, eventPayload, $"Usage: {commandName}", cancellationToken);
            return;
        }

        var groupId = GetInt64(eventPayload, "group_id");
        if (groupId <= 0)
        {
            await ReplyAsync(runtime, eventPayload, "Unable to identify the group.", cancellationToken);
            return;
        }

        var targetResolution = ResolveServerCommandProfile(runtime, eventPayload, string.Empty);
        if (!string.IsNullOrWhiteSpace(targetResolution.ErrorMessage) || targetResolution.Profile is null)
        {
            await ReplyAsync(runtime, eventPayload, targetResolution.ErrorMessage, cancellationToken);
            return;
        }

        var profile = targetResolution.Profile;
        var mods = await _instanceModService.GetModsAsync(profile, cancellationToken);
        var includedCount = mods.Count(mod => ModFileArchiveService.ShouldInclude(mod, scope));
        if (includedCount == 0)
        {
            await ReplyAsync(
                runtime,
                eventPayload,
                scope == ModFileArchiveScope.All
                    ? "当前档案没有可发送的模组。"
                    : "当前档案没有可发送的 Universal 模组。",
                cancellationToken);
            return;
        }

        var exportDirectory = Path.Combine(WorkspacePathHelper.RobotRoot, "exports");
        Directory.CreateDirectory(exportDirectory);
        var scopeToken = scope == ModFileArchiveScope.All ? "all" : "universal";
        var fileName = $"mods-{scopeToken}-{SanitizeFileName(profile.Name)}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip";
        var outputPath = Path.Combine(exportDirectory, fileName);
        try
        {
            await using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                await _modFileArchiveService.CreateModArchiveAsync(profile, mods, scope, output, cancellationToken);
            }

            await runtime.OneBot.UploadGroupFileAsync(groupId, outputPath, fileName, cancellationToken);
            var scopeText = scope == ModFileArchiveScope.All ? "全部模组" : "Universal 模组";
            await ReplyAsync(runtime, eventPayload, $"已发送模组压缩包：{fileName}（{includedCount} 个{scopeText}）", cancellationToken);
        }
        finally
        {
            TryDeleteFile(outputPath);
        }
    }

    private static bool TryParseModListExportFormat(string args, out ModListExportFormat format)
    {
        format = default;
        var value = (args ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Contains(' ') || value.Contains('\t'))
            return false;

        switch (value)
        {
            case "txt": format = ModListExportFormat.Txt; return true;
            case "pdf": format = ModListExportFormat.Pdf; return true;
            case "md":
            case "markdown": format = ModListExportFormat.Markdown; return true;
            case "xlsx": format = ModListExportFormat.Xlsx; return true;
            case "csv": format = ModListExportFormat.Csv; return true;
            default: return false;
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string((value ?? string.Empty).Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "profile" : sanitized;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Upload completion is authoritative; failed cleanup is harmless and will be retried by later exports.
        }
    }

    private async Task HandleServerCommandAsync(Vs2QQRuntimeContext runtime, JsonObject eventPayload, string args, CancellationToken cancellationToken)
    {
        var parts = args.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            await ReplyAsync(runtime, eventPayload, "Usage: /server status [n] | /server players [n] | /server start [档案名或ID] | /server stop [档案名或ID] | /server password get | /server password set <new_password>", cancellationToken);
            return;
        }

        var subCommand = parts[0].ToLowerInvariant();
        if (subCommand == "status")
        {
            await HandleServerStatusCommandAsync(runtime, eventPayload, parts, cancellationToken);
            return;
        }

        if (subCommand == "players")
        {
            await HandleServerPlayersCommandAsync(runtime, eventPayload, parts, cancellationToken);
            return;
        }

        if (subCommand == "password")
        {
            await HandleServerPasswordCommandAsync(runtime, eventPayload, parts, cancellationToken);
            return;
        }

        if (subCommand == "start")
        {
            await HandleServerStartCommandAsync(
                runtime,
                eventPayload,
                string.Join(' ', parts.Skip(1)),
                cancellationToken);
            return;
        }

        if (subCommand == "stop")
        {
            await HandleServerStopCommandAsync(
                runtime,
                eventPayload,
                string.Join(' ', parts.Skip(1)),
                cancellationToken);
            return;
        }

        await ReplyAsync(runtime, eventPayload, "Only /server status [n], /server players [n], /server start [档案名或ID], /server stop [档案名或ID], and /server password get|set are supported.", cancellationToken);
    }

    private async Task HandleServerStatusCommandAsync(
        Vs2QQRuntimeContext runtime,
        JsonObject eventPayload,
        IReadOnlyList<string> parts,
        CancellationToken cancellationToken)
    {
        var index = 1;
        if (parts.Count > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            index = parsed;
        }

        var groupId = GetInt64(eventPayload, "group_id");
        if (groupId <= 0)
        {
            await ReplyAsync(runtime, eventPayload, "Use in group chat.", cancellationToken);
            return;
        }

        if (!runtime.BoundGroupIds.Contains(groupId))
        {
            await ReplyAsync(runtime, eventPayload, "This group is not bound in robot settings.", cancellationToken);
            return;
        }

        var bridgeStatus = await QueryBridgeForGroupAsync(runtime, groupId, "server.status", cancellationToken);
        if (bridgeStatus?.Success == true && bridgeStatus.Data is not null)
        {
            var bridgePlayers = await QueryBridgeForGroupAsync(runtime, groupId, "players.list", cancellationToken);
            var profileId = runtime.GetPrimaryProfileIdForGroup(groupId);
            var events = !string.IsNullOrWhiteSpace(profileId)
                ? _serverBridgeStateStore?.GetEvents(profileId)
                    .ToList()
                : null;
            await ReplyAsync(runtime, eventPayload, BuildBridgeStatusMessage(bridgeStatus.Data, bridgePlayers?.Data, events), cancellationToken);
            return;
        }

        await ReplyAsync(runtime, eventPayload, "服务器桥接不可用，无法读取服务器状态。", cancellationToken);
    }

    private async Task HandleServerPasswordCommandAsync(
        Vs2QQRuntimeContext runtime,
        JsonObject eventPayload,
        IReadOnlyList<string> parts,
        CancellationToken cancellationToken)
    {
        if (parts.Count < 2)
        {
            await ReplyAsync(runtime, eventPayload, "Usage: /server password get | /server password set <new_password>", cancellationToken);
            return;
        }

        var action = parts[1].ToLowerInvariant();
        var isGet = action == "get";
        var isSet = action == "set";
        if (!isGet && !isSet)
        {
            await ReplyAsync(runtime, eventPayload, "Usage: /server password get | /server password set <new_password>", cancellationToken);
            return;
        }

        if (!HasAdminPermission(runtime, eventPayload))
        {
            await ReplyAsync(runtime, eventPayload, "Permission denied. Super admin only.", cancellationToken);
            return;
        }

        var targetResolution = ResolveServerCommandProfile(runtime, eventPayload, string.Empty);
        if (!string.IsNullOrWhiteSpace(targetResolution.ErrorMessage) || targetResolution.Profile is null)
        {
            await ReplyAsync(runtime, eventPayload, targetResolution.ErrorMessage, cancellationToken);
            return;
        }

        var status = _serverProcessService.GetCurrentStatus(targetResolution.Profile.Id);
        if (!status.IsRunning)
        {
            await ReplyAsync(runtime, eventPayload, "No local running profile. Password command only supports local bound server.", cancellationToken);
            return;
        }

        var profile = _instanceProfileService.GetProfileById(targetResolution.Profile.Id);
        if (profile is null)
        {
            await ReplyAsync(runtime, eventPayload, "Cannot resolve local profile for password operation.", cancellationToken);
            return;
        }

        var serverSettings = await _instanceServerConfigService.LoadServerSettingsAsync(profile, cancellationToken);
        var worldSettings = await _instanceServerConfigService.LoadWorldSettingsAsync(profile, cancellationToken);
        var worldRules = await _instanceServerConfigService.LoadWorldRulesAsync(profile, cancellationToken);

        if (isGet)
        {
            var passwordText = string.IsNullOrWhiteSpace(serverSettings.Password) ? "(empty)" : serverSettings.Password.Trim();
            var userId = GetInt64(eventPayload, "user_id");
            if (userId > 0)
            {
                await runtime.OneBot.SendPrivateMsgAsync(userId, $"服务器加入密码：{passwordText}", cancellationToken);
                if (IsGroupMessage(eventPayload))
                {
                    await ReplyAsync(runtime, eventPayload, "密码已通过私聊发送。", cancellationToken);
                }
            }
            else
            {
                await ReplyAsync(runtime, eventPayload, "无法识别用户，不能发送密码。", cancellationToken);
            }
            return;
        }

        if (parts.Count < 3)
        {
            await ReplyAsync(runtime, eventPayload, "Usage: /server password set <new_password>", cancellationToken);
            return;
        }

        var newPassword = string.Join(' ', parts.Skip(2)).Trim();
        if (newPassword.Length > 128)
        {
            await ReplyAsync(runtime, eventPayload, "Password too long. Maximum 128 characters.", cancellationToken);
            return;
        }

        serverSettings.Password = string.Equals(newPassword, "-", StringComparison.Ordinal)
            ? null
            : newPassword;
        await _instanceServerConfigService.SaveSettingsAsync(profile, serverSettings, worldSettings, worldRules, cancellationToken);

        await ReplyAsync(runtime, eventPayload, string.IsNullOrWhiteSpace(serverSettings.Password) ? "密码已清空。" : "密码已更新。", cancellationToken);
    }

    private async Task HandleServerPlayersCommandAsync(
        Vs2QQRuntimeContext runtime,
        JsonObject eventPayload,
        IReadOnlyList<string> parts,
        CancellationToken cancellationToken)
    {
        var index = 1;
        if (parts.Count > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            index = parsed;
        }

        var groupId = GetInt64(eventPayload, "group_id");
        if (groupId <= 0)
        {
            await ReplyAsync(runtime, eventPayload, "Use in group chat.", cancellationToken);
            return;
        }

        if (!runtime.BoundGroupIds.Contains(groupId))
        {
            await ReplyAsync(runtime, eventPayload, "This group is not bound in robot settings.", cancellationToken);
            return;
        }

        var bridgePlayers = await QueryBridgeForGroupAsync(runtime, groupId, "players.list", cancellationToken);
        if (bridgePlayers?.Success == true && bridgePlayers.Data is not null)
        {
            await ReplyAsync(runtime, eventPayload, BuildBridgePlayersMessage(bridgePlayers.Data), cancellationToken);
            return;
        }

        await ReplyAsync(runtime, eventPayload, "服务器桥接不可用，无法读取在线玩家。", cancellationToken);
    }

    private async Task HandleServerStartCommandAsync(
        Vs2QQRuntimeContext runtime,
        JsonObject eventPayload,
        string targetSelector,
        CancellationToken cancellationToken)
    {
        if (!HasAdminPermission(runtime, eventPayload))
        {
            await ReplyAsync(runtime, eventPayload, "Permission denied. Super admin only.", cancellationToken);
            return;
        }

        var targetResolution = ResolveServerCommandProfile(runtime, eventPayload, targetSelector);
        if (!string.IsNullOrWhiteSpace(targetResolution.ErrorMessage))
        {
            await ReplyAsync(runtime, eventPayload, targetResolution.ErrorMessage, cancellationToken);
            return;
        }

        var profile = targetResolution.Profile;
        if (profile is null)
        {
            await ReplyAsync(runtime, eventPayload, "未解析到可启动的服务器档案。", cancellationToken);
            return;
        }

        if (_serverProcessService.GetCurrentStatus(profile.Id).IsRunning)
        {
            await ReplyAsync(runtime, eventPayload, $"服务器已在运行：{profile.Name}", cancellationToken);
            return;
        }

        var launchableProfile = await EnsureLaunchableProfileAsync(profile, profile.ActiveSaveFile, cancellationToken);
        await _serverProcessService.StartAsync(launchableProfile, cancellationToken);
        await ReplyAsync(runtime, eventPayload, $"已启动服务器：{launchableProfile.Name}", cancellationToken);
    }

    private async Task HandleServerStopCommandAsync(
        Vs2QQRuntimeContext runtime,
        JsonObject eventPayload,
        string targetSelector,
        CancellationToken cancellationToken)
    {
        if (!HasAdminPermission(runtime, eventPayload))
        {
            await ReplyAsync(runtime, eventPayload, "Permission denied. Super admin only.", cancellationToken);
            return;
        }

        var targetResolution = ResolveServerCommandProfile(runtime, eventPayload, targetSelector);
        if (!string.IsNullOrWhiteSpace(targetResolution.ErrorMessage))
        {
            await ReplyAsync(runtime, eventPayload, targetResolution.ErrorMessage, cancellationToken);
            return;
        }

        if (targetResolution.Profile is null)
        {
            await ReplyAsync(runtime, eventPayload, "未解析到可停止的服务器档案。", cancellationToken);
            return;
        }

        var profile = targetResolution.Profile;
        if (!_serverProcessService.GetCurrentStatus(profile.Id).IsRunning)
        {
            await ReplyAsync(runtime, eventPayload, $"服务器档案未运行：{profile.Name}", cancellationToken);
            return;
        }

        await _serverProcessService.StopAsync(profile.Id, TimeSpan.FromSeconds(15), cancellationToken);
        await ReplyAsync(runtime, eventPayload, $"已停止服务器：{profile.Name}", cancellationToken);
    }

    private RobotServerCommandTargetResolution ResolveServerCommandProfile(
        Vs2QQRuntimeContext runtime,
        JsonObject eventPayload,
        string targetSelector)
    {
        var preferences = _launcherPreferencesService.Load();
        var fallbackProfileIds = SplitProfileIds(
            preferences.AutoStartServerProfileIds,
            preferences.AutoStartServerProfileId);
        if (fallbackProfileIds.Count == 0)
        {
            fallbackProfileIds = SplitProfileIds(
                preferences.DefaultLaunchProfileIds,
                preferences.DefaultLaunchProfileId);
        }

        var groupId = IsGroupMessage(eventPayload)
            ? GetInt64(eventPayload, "group_id")
            : 0;
        return RobotServerCommandTargetResolver.Resolve(
            _instanceProfileService.GetProfiles(),
            fallbackProfileIds,
            runtime.CommandScope,
            GetInt64(eventPayload, "user_id"),
            groupId > 0 ? groupId : null,
            targetSelector);
    }

    private static HashSet<string> SplitProfileIds(IEnumerable<string>? values, string legacyValue = "")
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values ?? [])
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                result.Add(value.Trim());
            }
        }

        foreach (var value in (legacyValue ?? string.Empty).Split(
                     [';', ',', '|'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                result.Add(value.Trim());
            }
        }

        return result;
    }

    private async Task HandleBridgeEventAsync(Vs2QQRuntimeContext runtime, string profileId, ServerBridgeEvent evt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = NormalizeDisplayText(evt.Data["name"]?.GetValue<string>());
        var content = NormalizeInboundServerText(name, evt.Data["message"]?.GetValue<string>());
        if (evt.Event == "chat")
        {
            var completed = runtime.PlayerBindings.TryComplete(
                profileId,
                ReadString(evt.Data, "uid"),
                name,
                content);
            if (completed is not null)
            {
                var profileName = _instanceProfileService.GetProfileById(profileId)?.Name ?? profileId;
                var confirmation = $"绑定成功：QQ {completed.QqUserId} 已绑定玩家 {completed.PlayerName}（{profileName}）。";
                try { await runtime.OneBot.SendGroupMsgAsync(completed.GroupId, confirmation, cancellationToken).ConfigureAwait(false); } catch { }
                cancellationToken.ThrowIfCancellationRequested();
                try { await runtime.OneBot.SendPrivateMsgAsync(completed.QqUserId, confirmation, cancellationToken).ConfigureAwait(false); } catch { }
            }
        }
        var deathReason = evt.Event == "player.died" ? FormatDeathReason(evt.Data) : string.Empty;
        var deathNotification = evt.Event == "player.died" ? FormatDeathNotification(evt.Data, name) : string.Empty;
        // The server may echo the bridge's own /announce payload back through
        // its notification logger. Do not send that group relay back to QQ.
        if (IsServerRelayEchoText(content) || GroupRelayEchoRegex.IsMatch(content)) return;
        if (ServerLogPrivacyFilter.ShouldSuppressRelayParts(name, content)) return;
        var time = evt.TimestampUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var message = evt.Event switch
        {
            "player.joined" => $"[服务器 {time}]{name} 进入服务器",
            "player.left" => $"[服务器 {time}]{name} 离开服务器",
            "player.died" => string.IsNullOrWhiteSpace(deathReason)
                ? $"[服务器 {time}]{name} 死亡"
                : $"[服务器 {time}]{deathNotification}",
            "chat" => $"[服务器 {time}]{name}：{content}",
            "server.notification" => $"[服务器 {time}]{content}",
            _ => string.Empty
        };
        if (string.IsNullOrWhiteSpace(message)) return;
        foreach (var groupId in runtime.ProfileBindings
                     .Where(x => string.Equals(x.ProfileId, profileId, StringComparison.OrdinalIgnoreCase) && x.GroupId > 0)
                     .Select(x => x.GroupId).Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await runtime.OneBot.SendGroupMsgAsync(groupId, message, cancellationToken).ConfigureAwait(false); } catch { }
        }
    }

    private async Task MaintainBridgeSubscriptionAsync(
        Vs2QQRuntimeContext runtime,
        InstanceProfile profile,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _serverBridgeService is not null)
        {
            try
            {
                var subscription = await _serverBridgeService.SubscribeAsync(profile,
                    new ServerBridgeSubscriptionOptions { Events = ["player.joined", "player.left", "player.died", "chat", "server.notification"], StartFromLatest = true },
                    evt => HandleBridgeEventAsync(runtime, profile.Id, evt, cancellationToken), cancellationToken).ConfigureAwait(false);
                runtime.BridgeSubscriptions.Add(subscription);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                EmitOutput($"[warn] server bridge subscription failed ({profile.Id}): {ex.Message}");
                try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task<ServerBridgeQueryResult?> QueryBridgeForGroupAsync(
        Vs2QQRuntimeContext runtime,
        long groupId,
        string method,
        CancellationToken cancellationToken)
    {
        if (_serverBridgeService is null) return null;
        var profileId = runtime.GetPrimaryProfileIdForGroup(groupId);
        var profile = !string.IsNullOrWhiteSpace(profileId)
            ? _instanceProfileService.GetProfileById(profileId)
            : _serverProcessService.GetCurrentStatuses().Where(x => x.IsRunning).Select(x => x.ProfileId).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => _instanceProfileService.GetProfileById(x!)).FirstOrDefault(x => x is not null);
        if (profile is null) return null;
        return await QueryBridgeForProfileAsync(profile, method, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ServerBridgeQueryResult?> QueryBridgeForProfileAsync(
        InstanceProfile profile,
        string method,
        CancellationToken cancellationToken,
        JsonObject? arguments = null)
    {
        if (_serverBridgeService is null) return null;
        try { return await _serverBridgeService.QueryAsync(profile, method, arguments, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex)
        {
            EmitOutput($"[warn] server bridge query failed ({method}): {ex.Message}");
            return null;
        }
    }

    private static string BuildBridgeStatusMessage(
        JsonObject data,
        JsonObject? playersData = null,
        IReadOnlyList<ServerBridgeEvent>? recentEvents = null)
    {
        var onlinePlayers = ReadInt(data, "onlinePlayers");
        var maxPlayers = ReadInt(data, "maxPlayers");
        var address = Safe(ReadString(data, "address"));
        var lines = new List<string>
        {
            $"服务器：{Safe(ReadString(data, "name"))}",
            $"状态：{FormatBridgeServerStatus(ReadString(data, "status"))}",
            $"版本：{Safe(ReadString(data, "version"))}",
            $"API版本：{Safe(ReadString(data, "apiVersion"))}",
            $"人数：{(onlinePlayers?.ToString(CultureInfo.InvariantCulture) ?? "-")}/{(maxPlayers?.ToString(CultureInfo.InvariantCulture) ?? "-")}",
            $"世界：{Safe(ReadString(data, "worldName"))}",
            $"世界时间：{Safe(ReadString(data, "worldTime"))}",
            $"季节：{FormatSeason(ReadString(data, "season"))}",
            $"地址：{address}",
            $"描述：{Safe(ReadString(data, "description"))}",
            $"欢迎语：{Safe(ReadString(data, "welcomeMessage"))}",
            $"白名单：{FormatBoolean(data, "whitelistEnabled")}",
            $"密码：{FormatBoolean(data, "passwordProtected")}",
            $"运行时间：{FormatDuration(ReadDouble(data, "uptimeSeconds"))}"
        };

        if (data["performance"] is JsonObject performance && performance.Count > 0)
        {
            var metrics = performance.Select(item => $"{item.Key}={item.Value}").ToList();
            lines.Add("性能：" + string.Join(", ", metrics));
        }

        if (playersData?["players"] is JsonArray players)
        {
            var names = players.OfType<JsonObject>()
                .Select(player => Safe(ReadString(player, "name")))
                .Where(name => name != "-")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList();
            if (names.Count > 0)
                lines.Add("玩家：" + string.Join("、", names) + (onlinePlayers > names.Count ? $" 等 {onlinePlayers} 人" : string.Empty));
        }

        if (recentEvents is { Count: > 0 })
        {
            var eventSummary = recentEvents
                .Where(evt => evt.Event is "player.joined" or "player.left" or "player.died")
                .TakeLast(3)
                .Select(evt =>
                {
                    var eventName = evt.Event switch
                    {
                        "player.joined" => "join",
                        "player.left" => "leave",
                        "player.died" => "death",
                        _ => evt.Event
                    };
                    var playerName = Safe(ReadString(evt.Data, "name"));
                    var connectionState = Safe(ReadString(evt.Data, "connectionState"));
                    var reason = evt.Event == "player.died"
                        ? FormatDeathReason(evt.Data)
                        : string.Empty;
                    var entry = connectionState == "-"
                        ? $"{playerName}-{eventName}"
                        : $"{playerName}-{eventName}-{connectionState}";
                    return string.IsNullOrWhiteSpace(reason) || reason == "-" ? entry : $"{entry}：{reason}";
                })
                .ToList();
            if (eventSummary.Count > 0)
                lines.Add("连接事件：" + string.Join("；", eventSummary));

            var chatSummary = recentEvents
                .Where(evt => evt.Event == "chat")
                .TakeLast(3)
                .Select(evt =>
                {
                    var name = Safe(ReadString(evt.Data, "name"));
                    var message = NormalizeInboundServerText(name, ReadString(evt.Data, "message"));
                    return ServerLogPrivacyFilter.ShouldSuppressRelayParts(name, message) || string.IsNullOrWhiteSpace(message)
                        ? string.Empty
                        : $"{name}: {message}";
                })
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToList();
            if (chatSummary.Count > 0)
                lines.Add("聊天：" + string.Join(" | ", chatSummary));
        }

        var body = LimitText(string.Join('\n', lines), MaxOneBotMessageLength - 32);
        return LimitText($"[服务器状态 {DateTime.Now:HH:mm:ss}]\n{body}", MaxOneBotMessageLength);
    }

    private static string BuildBridgePlayersMessage(JsonObject data)
    {
        var players = data["players"] as JsonArray;
        var online = players?.OfType<JsonObject>()
            .Where(player => ReadBool(player, "playing") ?? ReadBool(player, "online") == true)
            .OrderBy(player => ReadString(player, "name"), StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        var time = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var maxPlayers = ReadInt(data, "maxPlayers");
        var header = $"[在线玩家 {time}] {online.Count}/{(maxPlayers?.ToString(CultureInfo.InvariantCulture) ?? "?")}";
        if (online.Count == 0) return $"{header}\n当前无在线玩家。";

        var lines = new List<string> { header };
        foreach (var player in online)
        {
            var state = Safe(ReadString(player, "connectionState"));
            var ping = ReadInt(player, "pingMs");
            var latency = ping is null ? "-" : $"{ping.Value.ToString(CultureInfo.InvariantCulture)}ms";
            lines.Add($"- {Safe(ReadString(player, "name"))} ({state}, {latency})");
        }
        return LimitText(string.Join('\n', lines), MaxOneBotMessageLength);
    }

    internal static IReadOnlyList<string> BuildMyInfoLines(string profileName, JsonObject player)
    {
        var lines = new List<string>
        {
            $"[我的玩家信息 {DateTime.Now:HH:mm:ss}]",
            $"服务器：{Safe(profileName)}",
            $"玩家：{Safe(ReadString(player, "name"))}",
            $"状态：{Safe(ReadString(player, "connectionState"))}",
            $"延迟：{FormatNumber(ReadInt(player, "pingMs"), "ms")}",
            $"坐标：{FormatPlayerPosition(player)}",
            $"维度：{FormatNumber(ReadInt(player, "dimension"))}",
            $"游戏模式：{Safe(ReadString(player, "gameMode"))}",
            $"生命值：{FormatRange(ReadDouble(player, "health"), ReadDouble(player, "maxHealth"))}",
            $"饱食度：{FormatRange(ReadDouble(player, "hunger"), ReadDouble(player, "maxHunger"))}",
            $"加入时间：{FormatPlayerTimestamp(ReadString(player, "joinedAtUtc"))}",
            $"最近活动：{FormatPlayerTimestamp(ReadString(player, "lastActivityUtc"))}"
        };

        if (player["inventory"] is not JsonArray inventory)
        {
            lines.Add("背包：未启用扩展玩家信息或当前版本不支持");
            return lines;
        }
        if (inventory.Count == 0)
        {
            lines.Add("背包：空");
            return lines;
        }

        lines.Add($"背包物品（{inventory.Count} 类）：");
        foreach (var item in inventory.OfType<JsonObject>())
        {
            var quantity = ReadInt(item, "quantity") ?? 0;
            lines.Add($"- {Safe(ReadString(item, "name"))} x{quantity.ToString(CultureInfo.InvariantCulture)}");
        }
        return lines;
    }

    internal static string FormatDeathReason(JsonObject data)
    {
        var exactMessage = NormalizeDisplayText(ReadString(data, "deathMessage"));
        if (!string.IsNullOrWhiteSpace(exactMessage))
        {
            var playerName = NormalizeDisplayText(ReadString(data, "name"));
            return !string.IsNullOrWhiteSpace(playerName) && exactMessage.StartsWith(playerName, StringComparison.OrdinalIgnoreCase)
                ? exactMessage[playerName.Length..].TrimStart()
                : exactMessage;
        }
        return string.Empty;
    }

    internal static string FormatDeathNotification(JsonObject data, string playerName)
    {
        var exactMessage = NormalizeDisplayText(ReadString(data, "deathMessage"));
        if (!string.IsNullOrWhiteSpace(exactMessage))
            return exactMessage.StartsWith(playerName, StringComparison.OrdinalIgnoreCase)
                ? exactMessage
                : $"{playerName}{exactMessage}";
        return $"{playerName} 死亡";
    }

    private static string FormatPlayerPosition(JsonObject player)
    {
        var x = ReadDouble(player, "x");
        var y = ReadDouble(player, "y");
        var z = ReadDouble(player, "z");
        if (x is null || y is null || z is null) return "-";
        return $"X={x.Value:F1}, Y={y.Value:F1}, Z={z.Value:F1}（相对出生点）";
    }

    private static string FormatRange(double? current, double? maximum) =>
        current is null ? "-" : maximum is null ? $"{current.Value:F1}" : $"{current.Value:F1}/{maximum.Value:F1}";

    private static string FormatNumber(int? value, string suffix = "") =>
        value is null ? "-" : $"{value.Value.ToString(CultureInfo.InvariantCulture)}{suffix}";

    private static string FormatPlayerTimestamp(string rawTimestamp)
    {
        return DateTimeOffset.TryParse(rawTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp)
            ? timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "-";
    }

    private static string ReadString(JsonObject data, string name) =>
        data[name]?.GetValue<string>() ?? string.Empty;

    private static int? ReadInt(JsonObject data, string name)
    {
        if (data[name] is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var integer)) return integer;
        if (value.TryGetValue<long>(out var longInteger) && longInteger is >= int.MinValue and <= int.MaxValue)
            return (int)longInteger;
        if (value.TryGetValue<double>(out var number) && double.IsFinite(number) && number is >= int.MinValue and <= int.MaxValue)
            return (int)Math.Round(number);
        return null;
    }

    private static double? ReadDouble(JsonObject data, string name)
    {
        if (data[name] is not JsonValue value) return null;
        if (value.TryGetValue<double>(out var number) && double.IsFinite(number)) return number;
        if (value.TryGetValue<int>(out var integer)) return integer;
        if (value.TryGetValue<long>(out var longInteger)) return longInteger;
        return null;
    }

    private static bool? ReadBool(JsonObject data, string name)
    {
        try { return data[name]?.GetValue<bool?>(); }
        catch (InvalidOperationException) { return null; }
        catch (FormatException) { return null; }
    }

    private static string FormatBoolean(JsonObject data, string name) =>
        ReadBool(data, name) switch
        {
            true => "是",
            false => "否",
            _ => "-"
        };

    private static string FormatBridgeServerStatus(string? status) =>
        Safe(status).ToLowerInvariant() switch
        {
            "rungame" or "running" or "run" => "运行中",
            "standby" => "待机",
            "starting" => "启动中",
            "stopping" => "停止中",
            "stopped" => "已停止",
            "offline" => "离线",
            var value => value
        };

    private static string FormatSeason(string? season) =>
        Safe(season).ToLowerInvariant() switch
        {
            "spring" => "春季",
            "summer" => "夏季",
            "autumn" or "fall" => "秋季",
            "winter" => "冬季",
            var value => value
        };

    private static string FormatDuration(double? seconds)
    {
        if (seconds is null || seconds < 0) return "-";
        var span = TimeSpan.FromSeconds(seconds.Value);
        return span.TotalDays >= 1
            ? $"{(int)span.TotalDays}天{span.Hours:00}时{span.Minutes:00}分"
            : $"{span.Hours:00}时{span.Minutes:00}分{span.Seconds:00}秒";
    }

    private static string LimitText(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
        return value[..Math.Max(0, maxLength - 3)] + "...";
    }

    private async Task SendToGameServerAsync(Vs2QQRuntimeContext runtime, long groupId, string message, CancellationToken cancellationToken)
    {
        if (!runtime.BoundGroupIds.Contains(groupId))
        {
            return;
        }

        var outbound = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (string.IsNullOrWhiteSpace(outbound))
        {
            return;
        }

        var boundProfileIds = runtime.CommandScope.GetProfileIdsForGroup(groupId);
        if (boundProfileIds.Count > 1)
        {
            EmitOutput($"[warn] 群绑定了多个服务器档案，已拒绝转发群消息 group={groupId}");
            return;
        }

        if (boundProfileIds.Count == 0 && runtime.HasProfileBindings)
        {
            return;
        }

        var boundProfileId = boundProfileIds.Count == 1 ? boundProfileIds[0] : string.Empty;

        Exception? lastError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (string.IsNullOrWhiteSpace(boundProfileId))
                {
                    _serverProcessService.GetCurrentStatus();
                    await _serverTransport.SendGroupMessageToServerAsync(groupId, outbound, cancellationToken);
                }
                else
                {
                    await _serverProcessService.SendCommandAsync(boundProfileId, $"/announce {outbound}", cancellationToken);
                }
                if (attempt > 1)
                {
                    EmitOutput($"[vs2qq] 群消息补发成功 group={groupId}");
                }

                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                EmitOutput($"[warn] 群消息转发到服务器失败 group={groupId} attempt={attempt}: {ex.Message}");
                if (attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                }
            }
        }

        throw new InvalidOperationException($"群消息转发到服务器失败 group={groupId}", lastError);
    }

    private static bool TryBuildOutboundGroupMessage(Vs2QQRuntimeContext runtime, JsonObject eventPayload, string rawMessage, out string outboundMessage)
    {
        outboundMessage = string.Empty;
        if (!IsGroupMessage(eventPayload))
        {
            return false;
        }

        var groupId = GetInt64(eventPayload, "group_id");
        if (groupId <= 0)
        {
            return false;
        }

        if (!runtime.BoundGroupIds.Contains(groupId))
        {
            return false;
        }

        var senderName = GetSenderDisplayName(eventPayload);
        var plain = NormalizeOutboundText(rawMessage);
        if (string.IsNullOrWhiteSpace(plain))
        {
            return false;
        }

        if (IsServerRelayEchoText(plain))
        {
            return false;
        }

        var timeLabel = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        outboundMessage = $"[群聊 {timeLabel}]{Safe(senderName)}：{plain}";
        return true;
    }

    private static string NormalizeOutboundText(string rawMessage)
    {
        var text = NormalizeDisplayText(rawMessage);
        text = CqImageRegex.Replace(text, "[图片]");
        text = CqCodeRegex.Replace(text, "[消息]");
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        text = MultiWhitespaceRegex.Replace(text, " ");
        text = SanitizeOutboundMentionText(text);
        return text;
    }

    private static string GetSenderDisplayName(JsonObject eventPayload)
    {
        if (eventPayload["sender"] is JsonObject senderObject)
        {
            var card = GetString(senderObject, "card");
            if (!string.IsNullOrWhiteSpace(card))
            {
                return card;
            }

            var nickname = GetString(senderObject, "nickname");
            if (!string.IsNullOrWhiteSpace(nickname))
            {
                return nickname;
            }
        }

        var name = GetString(eventPayload, "sender_name");
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        return GetString(eventPayload, "nickname");
    }

    private async Task<string> ExtractPlainTextAsync(
        Vs2QQRuntimeContext runtime,
        JsonObject eventPayload,
        CancellationToken cancellationToken)
    {
        if (eventPayload.TryGetPropertyValue("message", out var messageNode) && messageNode is not null)
        {
            var segmentText = await ExtractOneBotMessageNodeTextAsync(runtime, eventPayload, messageNode, cancellationToken);
            if (!string.IsNullOrWhiteSpace(segmentText))
            {
                return NormalizeOutboundText(segmentText);
            }
        }

        var message = GetString(eventPayload, "raw_message");
        if (!string.IsNullOrWhiteSpace(message))
        {
            var expandedMessage = await ExpandCqAtSegmentsAsync(runtime, eventPayload, message, cancellationToken);
            return NormalizeOutboundText(expandedMessage);
        }

        var fallbackMessage = await ExpandCqAtSegmentsAsync(
            runtime,
            eventPayload,
            GetString(eventPayload, "message"),
            cancellationToken);
        return NormalizeOutboundText(fallbackMessage);
    }

    private async Task<string> ExtractOneBotMessageNodeTextAsync(
        Vs2QQRuntimeContext runtime,
        JsonObject eventPayload,
        JsonNode messageNode,
        CancellationToken cancellationToken)
    {
        if (messageNode is JsonValue valueNode && valueNode.TryGetValue<string>(out var textValue))
        {
            return await ExpandCqAtSegmentsAsync(runtime, eventPayload, textValue, cancellationToken);
        }

        if (messageNode is not JsonArray segments)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var segment in segments.OfType<JsonObject>())
        {
            var type = GetString(segment, "type").Trim().ToLowerInvariant();
            var data = segment["data"] as JsonObject;
            switch (type)
            {
                case "text":
                    parts.Add(data is null ? string.Empty : GetString(data, "text"));
                    break;
                case "image":
                case "mface":
                case "face":
                case "marketface":
                    parts.Add("[图片]");
                    break;
                case "at":
                    parts.Add(await FormatAtSegmentTextAsync(runtime, eventPayload, data, cancellationToken));
                    break;
                case "record":
                    parts.Add("[语音]");
                    break;
                case "video":
                    parts.Add("[视频]");
                    break;
                case "file":
                    parts.Add("[文件]");
                    break;
                case "reply":
                    break;
                default:
                    if (!string.IsNullOrWhiteSpace(type))
                    {
                        parts.Add("[消息]");
                    }
                    break;
            }
        }

        return string.Concat(parts);
    }

    private async Task<string> ExpandCqAtSegmentsAsync(
        Vs2QQRuntimeContext runtime,
        JsonObject eventPayload,
        string text,
        CancellationToken cancellationToken)
    {
        var source = text ?? string.Empty;
        var matches = CqAtRegex.Matches(source);
        if (matches.Count == 0)
        {
            return source;
        }

        var result = new StringBuilder(source.Length);
        var position = 0;
        foreach (Match match in matches)
        {
            result.Append(source, position, match.Index - position);
            var qqMatch = CqAtQqParameterRegex.Match(match.Groups["params"].Value);
            if (!qqMatch.Success)
            {
                result.Append("@用户");
            }
            else
            {
                var data = new JsonObject
                {
                    ["qq"] = WebUtility.HtmlDecode(qqMatch.Groups["qq"].Value)
                };
                result.Append(await FormatAtSegmentTextAsync(runtime, eventPayload, data, cancellationToken));
            }

            position = match.Index + match.Length;
        }

        result.Append(source, position, source.Length - position);
        return result.ToString();
    }

    private async Task<string> FormatAtSegmentTextAsync(
        Vs2QQRuntimeContext runtime,
        JsonObject eventPayload,
        JsonObject? data,
        CancellationToken cancellationToken)
    {
        if (data is null)
        {
            return "@用户";
        }

        var qq = GetString(data, "qq");
        if (string.Equals(qq, "all", StringComparison.OrdinalIgnoreCase))
        {
            return "@全体成员";
        }

        var displayName = GetSafeAtDisplayName(data, qq);
        if (string.IsNullOrWhiteSpace(displayName)
            && long.TryParse(qq, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId)
            && userId > 0)
        {
            var groupId = GetInt64(eventPayload, "group_id");
            if (groupId > 0)
            {
                try
                {
                    displayName = await runtime.OneBot.GetGroupMemberDisplayNameAsync(
                        groupId,
                        userId,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    EmitOutput($"[warn] OneBot 提及昵称查询失败 group={groupId}: {ex.Message}");
                }
            }
        }

        return string.IsNullOrWhiteSpace(displayName)
            ? "@用户"
            : "@" + Safe(displayName);
    }

    private static string GetSafeAtDisplayName(JsonObject data, string qq)
    {
        var displayName = NormalizeSafeAtDisplayName(GetString(data, "card"), qq);
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName;
        }

        displayName = NormalizeSafeAtDisplayName(GetString(data, "name"), qq);
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName;
        }

        return NormalizeSafeAtDisplayName(GetString(data, "nickname"), qq);
    }

    private static string NormalizeSafeAtDisplayName(string candidate, string qq)
    {
        var displayName = NormalizeDisplayText(candidate).Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return string.Empty;
        }

        if (IsQqIdentifierText(displayName))
        {
            return string.Empty;
        }

        var normalizedCandidate = displayName.TrimStart('@').Trim();
        if (string.IsNullOrWhiteSpace(normalizedCandidate))
        {
            return string.Empty;
        }

        var normalizedQq = (qq ?? string.Empty).Trim().TrimStart('@').Trim();
        return !string.IsNullOrWhiteSpace(normalizedQq) &&
               string.Equals(normalizedCandidate, normalizedQq, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : normalizedCandidate;
    }

    private static bool IsQqIdentifierText(string value)
    {
        var text = (value ?? string.Empty).Trim().TrimStart('@').Trim();
        return QqIdentifierRegex.IsMatch(text);
    }

    private static string SanitizeOutboundMentionText(string text)
    {
        return QqNumberMentionRegex.Replace(text ?? string.Empty, "@用户");
    }

    private async Task ReplyAsync(Vs2QQRuntimeContext runtime, JsonObject eventPayload, string message, CancellationToken cancellationToken)
    {
        if (IsGroupMessage(eventPayload))
        {
            var groupId = GetInt64(eventPayload, "group_id");
            if (groupId > 0)
            {
                await runtime.OneBot.SendGroupMsgAsync(groupId, message, cancellationToken);
                return;
            }
        }

        var userId = GetInt64(eventPayload, "user_id");
        if (userId > 0)
        {
            await runtime.OneBot.SendPrivateMsgAsync(userId, message, cancellationToken);
        }
    }

    private async Task ReplyAsync(Vs2QQRuntimeContext runtime, JsonObject eventPayload, JsonNode message, CancellationToken cancellationToken)
    {
        if (IsGroupMessage(eventPayload))
        {
            var groupId = GetInt64(eventPayload, "group_id");
            if (groupId > 0)
            {
                await runtime.OneBot.SendGroupMsgAsync(groupId, message, cancellationToken);
                return;
            }
        }

        var userId = GetInt64(eventPayload, "user_id");
        if (userId > 0)
        {
            await runtime.OneBot.SendPrivateMsgAsync(userId, message, cancellationToken);
        }
    }

    private static bool IsGroupMessage(JsonObject eventPayload)
    {
        return string.Equals(GetString(eventPayload, "message_type"), "group", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAdminPermission(Vs2QQRuntimeContext runtime, JsonObject eventPayload)
    {
        return runtime.CommandScope.IsAdmin(GetInt64(eventPayload, "user_id"));
    }

    private static string BuildHelpText(Vs2QQRuntimeContext runtime)
    {
        var builtInCommands = """
            VS2QQ Commands
            /help - 帮助
            /send <server_command> - 发送服务端指令（仅超级管理员）
            /modslist txt|pdf|md|xlsx|csv - 输出已绑定服务器档案的模组清单（仅超级管理员）
            /modfile - 打包并发送已绑定服务器档案的 Universal 模组（仅群聊超级管理员）
            /modfileall - 打包并发送已绑定服务器档案的全部模组（仅群聊超级管理员）
            /server status [n] - 获取最近第 n 次服务器状态（默认1）
            /server players [n] - 获取最近第 n 次在线玩家列表（默认1）
            /server start [档案名或ID] - 启动指定或唯一绑定的服务器档案（仅超级管理员）
            /server stop [档案名或ID] - 停止指定或唯一绑定的服务器档案（仅超级管理员）
            /server password get - 获取服务器密码
            /server password set <new_password> - 修改服务器密码（- 表示清空，仅超级管理员）
            /bind <游戏玩家名> - 在群聊发起 QQ 与游戏玩家绑定
            /myinfo - 私聊查看已绑定玩家的实时信息
            /tp <设置点名称> - 将已绑定玩家传送到管理员配置的设置点（仅绑定群）
            """;

        if (runtime.CustomCommands.Count == 0)
        {
            return builtInCommands;
        }

        var customCommands = runtime.CustomCommands.Values
            .OrderBy(static item => item.Command, StringComparer.OrdinalIgnoreCase)
            .Select(static item => $"{item.Command} - 自定义{FormatCustomMessageType(item.MessageType)}消息");
        return builtInCommands + Environment.NewLine + "自定义指令" + Environment.NewLine + string.Join(Environment.NewLine, customCommands);
    }

    private static string FormatCustomMessageType(RobotCustomMessageType messageType)
    {
        return messageType switch
        {
            RobotCustomMessageType.Text => "文本",
            RobotCustomMessageType.Image => "图片",
            _ => string.Empty
        };
    }

    private async Task<InstanceProfile> EnsureLaunchableProfileAsync(
        InstanceProfile profile,
        string preferredSavePath,
        CancellationToken cancellationToken)
    {
        var normalizedPreferredSavePath = NormalizeFullPath(preferredSavePath);
        if (!string.IsNullOrWhiteSpace(normalizedPreferredSavePath))
        {
            var saves = await _instanceSaveService.GetSavesAsync(profile, cancellationToken);
            var preferredSave = saves.FirstOrDefault(save =>
                NormalizeFullPath(save.FullPath).Equals(normalizedPreferredSavePath, StringComparison.OrdinalIgnoreCase));
            if (preferredSave is not null)
            {
                await PrepareProfileSaveForLaunchAsync(profile, preferredSave.FullPath, cancellationToken);
                return _instanceProfileService.GetProfileById(profile.Id) ?? profile;
            }
        }

        var currentSavePath = NormalizeFullPath(profile.ActiveSaveFile);
        if (string.IsNullOrWhiteSpace(currentSavePath))
        {
            currentSavePath = NormalizeFullPath(_instanceProfileService.GetDefaultSaveFilePath(profile.Id));
        }

        if (!string.IsNullOrWhiteSpace(currentSavePath))
        {
            await PrepareProfileSaveForLaunchAsync(profile, currentSavePath, cancellationToken);
        }

        return _instanceProfileService.GetProfileById(profile.Id) ?? profile;
    }

    private async Task PrepareProfileSaveForLaunchAsync(
        InstanceProfile profile,
        string savePath,
        CancellationToken cancellationToken)
    {
        var normalizedSavePath = NormalizeFullPath(savePath);
        if (string.IsNullOrWhiteSpace(normalizedSavePath))
        {
            return;
        }

        if (File.Exists(normalizedSavePath))
        {
            var fileInfo = new FileInfo(normalizedSavePath);
            if (fileInfo.Length == 0)
            {
                File.Delete(normalizedSavePath);
            }
        }

        await _instanceSaveService.SetActiveSaveAsync(profile, normalizedSavePath, cancellationToken);
    }

    private static string NormalizeFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeServerCommand(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        text = string.Join(' ', text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
        return text.StartsWith('/') ? text : "/" + text;
    }

    private static string GetString(JsonObject obj, string key)
    {
        return obj.TryGetPropertyValue(key, out var node) && node is not null
            ? node.ToString()
            : string.Empty;
    }

    private static long GetInt64(JsonObject obj, string key, long fallback = 0)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return fallback;
        }

        if (node is JsonValue valueNode)
        {
            if (valueNode.TryGetValue<long>(out var longValue))
            {
                return longValue;
            }

            if (valueNode.TryGetValue<int>(out var intValue))
            {
                return intValue;
            }
        }

        return long.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private void EmitOutput(string message)
    {
        OutputReceived?.Invoke(this, message);
    }

    private static IReadOnlyList<string> SplitOneBotMessages(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return [];
        }

        var result = new List<string>();
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            var safeLine = line ?? string.Empty;
            if (builder.Length > 0 &&
                builder.Length + Environment.NewLine.Length + safeLine.Length > MaxOneBotMessageLength)
            {
                result.Add(builder.ToString());
                builder.Clear();
            }

            if (safeLine.Length > MaxOneBotMessageLength)
            {
                if (builder.Length > 0)
                {
                    result.Add(builder.ToString());
                    builder.Clear();
                }

                for (var offset = 0; offset < safeLine.Length; offset += MaxOneBotMessageLength)
                {
                    result.Add(safeLine.Substring(offset, Math.Min(MaxOneBotMessageLength, safeLine.Length - offset)));
                }
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }
            builder.Append(safeLine);
        }

        if (builder.Length > 0)
        {
            result.Add(builder.ToString());
        }

        return result;
    }

    private static bool IsServerRelayEchoText(string? text)
    {
        var normalized = NormalizeDisplayText(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return ServerRelayEchoRegex.IsMatch(normalized) || GroupRelayEchoRegex.IsMatch(normalized);
    }

    private static string NormalizeInboundServerText(string? senderName, string? rawText)
    {
        var text = NormalizeDisplayText(rawText);

        if (!string.IsNullOrWhiteSpace(senderName) && !string.IsNullOrWhiteSpace(text))
        {
            var escapedSender = Regex.Escape(senderName.Trim());
            text = Regex.Replace(
                text,
                $"^{escapedSender}\\s*[:：]\\s*",
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            text = text.Trim();
        }

        return text;
    }

    private static string NormalizeDisplayText(string? rawText)
    {
        var text = WebUtility.HtmlDecode(rawText ?? string.Empty);
        text = HtmlTagRegex.Replace(text, string.Empty);
        text = CqImageRegex.Replace(text, "[图片]");
        text = CqCodeRegex.Replace(text, "[消息]");
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        text = MultiWhitespaceRegex.Replace(text, " ");
        return text;
    }

    private static string FormatDisplayTime(string? rawTimestamp)
    {
        if (!string.IsNullOrWhiteSpace(rawTimestamp))
        {
            var value = rawTimestamp.Trim();

            if (HasExplicitTimeZone(value) &&
                DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                    out var offsetParsed))
            {
                return offsetParsed.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            }

            if (DateTime.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                    out var parsed))
            {
                return parsed.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            }

            var match = TimePartRegex.Match(value);
            if (match.Success)
            {
                return match.Groups["time"].Value;
            }
        }

        return DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static bool HasExplicitTimeZone(string value)
    {
        var trimmed = value.Trim();
        return trimmed.EndsWith('Z')
               || trimmed.EndsWith('z')
               || Regex.IsMatch(trimmed, @"[+-]\d{2}:?\d{2}$", RegexOptions.CultureInvariant);
    }

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
    }

    private static OperationResult<RobotSettings> NormalizeLaunchSettings(RobotSettings settings)
    {
        var wsUrl = (settings.OneBotWsUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(wsUrl))
        {
            return OperationResult<RobotSettings>.Failed("缺少 OneBot WebSocket 地址。");
        }

        if (!Uri.TryCreate(wsUrl, UriKind.Absolute, out var wsUri)
            || (wsUri.Scheme != "ws" && wsUri.Scheme != "wss"))
        {
            return OperationResult<RobotSettings>.Failed("OneBot WebSocket 地址格式无效，必须是 ws:// 或 wss://。");
        }

        var dbPath = string.IsNullOrWhiteSpace(settings.DatabasePath)
            ? Path.Combine(WorkspacePathHelper.WorkspaceRoot, "vs2qq", "vs2qq.db")
            : settings.DatabasePath.Trim();
        if (!Path.IsPathRooted(dbPath))
        {
            dbPath = Path.Combine(WorkspacePathHelper.WorkspaceRoot, dbPath);
        }
        dbPath = Path.GetFullPath(dbPath);

        var reconnectInterval = settings.ReconnectIntervalSec <= 0 ? 5 : settings.ReconnectIntervalSec;
        var defaultEncoding = string.IsNullOrWhiteSpace(settings.DefaultEncoding) ? "utf-8" : settings.DefaultEncoding.Trim();
        var fallbackEncoding = string.IsNullOrWhiteSpace(settings.FallbackEncoding) ? "gbk" : settings.FallbackEncoding.Trim();        var normalizedSuperUsers = (settings.SuperUsers ?? [])
            .Where(x => x > 0)
            .Distinct()
            .ToList();
        var normalizedBoundGroupIds = (settings.BoundGroupIds ?? [])
            .Where(x => x > 0)
            .Distinct()
            .ToList();
        var normalizedProfileBindings = NormalizeProfileBindings(settings.ProfileBindings);
        var normalizedCustomCommands = RobotCustomCommandRules.NormalizeMany(settings.CustomCommands);
        var normalizedTeleportPoints = RobotTeleportPointRules.NormalizeMany(settings.TeleportPoints);
        foreach (var groupId in normalizedProfileBindings
                     .Select(static binding => ParsePositiveInt64(binding.GroupId))
                     .Where(static id => id > 0))
        {
            if (!normalizedBoundGroupIds.Contains(groupId))
            {
                normalizedBoundGroupIds.Add(groupId);
            }
        }

        foreach (var superUserId in normalizedProfileBindings
                     .Select(static binding => ParsePositiveInt64(binding.SuperUserId))
                     .Where(static id => id > 0))
        {
            if (!normalizedSuperUsers.Contains(superUserId))
            {
                normalizedSuperUsers.Add(superUserId);
            }
        }

        return OperationResult<RobotSettings>.Success(new RobotSettings
        {
            OneBotWsUrl = wsUrl,
            AccessToken = string.IsNullOrWhiteSpace(settings.AccessToken) ? null : settings.AccessToken.Trim(),
            BoundGroupIds = normalizedBoundGroupIds,
            ProfileBindings = normalizedProfileBindings,
            CustomCommands = normalizedCustomCommands,
            TeleportPoints = normalizedTeleportPoints,
            ReconnectIntervalSec = reconnectInterval,
            DatabasePath = dbPath,
            DefaultEncoding = defaultEncoding,
            FallbackEncoding = fallbackEncoding,
            SuperUsers = normalizedSuperUsers
        });
    }

    private static List<RobotProfileBinding> NormalizeProfileBindings(IEnumerable<RobotProfileBinding>? bindings)
    {
        var result = new List<RobotProfileBinding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in bindings ?? [])
        {
            var profileId = binding.ProfileId?.Trim() ?? string.Empty;
            var groupId = ParsePositiveInt64(binding.GroupId);
            var superUserId = ParsePositiveInt64(binding.SuperUserId);
            if (string.IsNullOrWhiteSpace(profileId) && groupId <= 0 && superUserId <= 0)
            {
                continue;
            }

            var normalizedGroupId = groupId > 0 ? groupId.ToString(CultureInfo.InvariantCulture) : string.Empty;
            var normalizedSuperUserId = superUserId > 0 ? superUserId.ToString(CultureInfo.InvariantCulture) : string.Empty;
            var key = $"{profileId}|{normalizedGroupId}|{normalizedSuperUserId}";
            if (!seen.Add(key))
            {
                continue;
            }

            result.Add(new RobotProfileBinding
            {
                ProfileId = profileId,
                GroupId = normalizedGroupId,
                SuperUserId = normalizedSuperUserId
            });
        }

        return result;
    }

    private static long ParsePositiveInt64(string? value)
    {
        return long.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : 0;
    }

    private static string NormalizeListenPrefix(string? value)
    {
        var raw = string.IsNullOrWhiteSpace(value)
            ? "http://127.0.0.1:18089/"
            : value.Trim();

        if (IsWildcardListenPrefix(raw))
        {
            return NormalizeWildcardListenPrefix(raw);
        }

        if (!raw.EndsWith('/'))
        {
            raw += "/";
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return "http://127.0.0.1:18089/";
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return "http://127.0.0.1:18089/";
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            return "http://127.0.0.1:18089/";
        }

        var prefix = uri.GetLeftPart(UriPartial.Path);
        if (!prefix.EndsWith('/'))
        {
            prefix += "/";
        }

        return prefix;
    }

    private static bool IsWildcardListenPrefix(string value)
    {
        return value.StartsWith("http://+:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("http://*:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("http://0.0.0.0:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://+:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://*:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://0.0.0.0:", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWildcardListenPrefix(string value)
    {
        var prefix = value.Trim();
        if (!prefix.EndsWith('/'))
        {
            prefix += "/";
        }

        if (prefix.StartsWith("http://0.0.0.0:", StringComparison.OrdinalIgnoreCase))
        {
            return "http://+:" + prefix["http://0.0.0.0:".Length..];
        }

        if (prefix.StartsWith("https://0.0.0.0:", StringComparison.OrdinalIgnoreCase))
        {
            return "https://+:" + prefix["https://0.0.0.0:".Length..];
        }

        return prefix;
    }

    private sealed class Vs2QQRuntimeContext : IAsyncDisposable
    {
        private int _disposedFlag;

        public Vs2QQRuntimeContext(
            RobotSettings settings,
            Vs2QQStorage storage)
        {
            Settings = settings;
            Storage = storage;
            BoundGroupIds = settings.BoundGroupIds?.Where(id => id > 0).ToHashSet() ?? [];
            CommandScope = new RobotCommandScope(settings.SuperUsers, BoundGroupIds, settings.ProfileBindings);
            CustomCommands = RobotCustomCommandRules.NormalizeMany(settings.CustomCommands)
                .ToDictionary(static command => command.Command, StringComparer.OrdinalIgnoreCase);
            TeleportPoints = RobotTeleportPointRules.NormalizeMany(settings.TeleportPoints)
                .ToDictionary(static point => point.Name, StringComparer.OrdinalIgnoreCase);
            ProfileBindings = BuildRuntimeProfileBindings(settings.ProfileBindings);
            GroupsByProfileId = BuildGroupsByProfileId(ProfileBindings);
            PlayerBindings = new RobotPlayerBindingStore(settings.DatabasePath);
        }

        public RobotSettings Settings { get; }

        public HashSet<long> BoundGroupIds { get; }

        public RobotCommandScope CommandScope { get; }

        public IReadOnlyDictionary<string, RobotCustomCommand> CustomCommands { get; }

        public IReadOnlyDictionary<string, RobotTeleportPoint> TeleportPoints { get; }

        public IReadOnlyList<Vs2QQProfileBinding> ProfileBindings { get; }

        public bool HasProfileBindings => ProfileBindings.Any(static binding => binding.GroupId > 0);

        private IReadOnlyDictionary<string, HashSet<long>> GroupsByProfileId { get; }

        public Vs2QQStorage Storage { get; }

        public RobotPlayerBindingStore PlayerBindings { get; }

        public Vs2QQOneBotClient OneBot { get; set; } = null!;

        public List<ServerBridgeSubscription> BridgeSubscriptions { get; } = [];
        public List<Task> BridgeConnectionTasks { get; } = [];

        public string GetPrimaryProfileIdForGroup(long groupId)
        {
            var profiles = CommandScope.GetProfileIdsForGroup(groupId);
            return profiles.Count == 1 ? profiles[0] : string.Empty;
        }

        private IReadOnlyList<long> GetBoundGroupIdsForProfile(string profileId)
        {
            return GroupsByProfileId.TryGetValue(profileId, out var groups)
                ? groups.ToList()
                : [];
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposedFlag, 1) == 1)
            {
                return;
            }

            await OneBot.DisposeAsync();
            PlayerBindings.Dispose();
            Storage.Dispose();
        }

        private static IReadOnlyList<Vs2QQProfileBinding> BuildRuntimeProfileBindings(IEnumerable<RobotProfileBinding>? bindings)
        {
            var result = new List<Vs2QQProfileBinding>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var binding in bindings ?? [])
            {
                var profileId = binding.ProfileId?.Trim() ?? string.Empty;
                var groupId = ParsePositiveInt64(binding.GroupId);
                var superUserId = ParsePositiveInt64(binding.SuperUserId);
                if (string.IsNullOrWhiteSpace(profileId) ||
                    (groupId <= 0 && superUserId <= 0))
                {
                    continue;
                }

                var key = $"{profileId}|{groupId}|{superUserId}";
                if (!seen.Add(key))
                {
                    continue;
                }

                result.Add(new Vs2QQProfileBinding(profileId, groupId, superUserId));
            }

            return result;
        }

        private static IReadOnlyDictionary<string, HashSet<long>> BuildGroupsByProfileId(IEnumerable<Vs2QQProfileBinding> bindings)
        {
            var result = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
            foreach (var binding in bindings)
            {
                if (binding.GroupId <= 0)
                {
                    continue;
                }

                if (!result.TryGetValue(binding.ProfileId, out var groups))
                {
                    groups = [];
                    result[binding.ProfileId] = groups;
                }

                groups.Add(binding.GroupId);
            }

            return result;
        }
    }

    private readonly record struct Vs2QQProfileBinding(string ProfileId, long GroupId, long SuperUserId);

    private readonly record struct GroupMemberDisplayNameCacheEntry(string DisplayName, DateTimeOffset ExpiresAtUtc);

    private sealed class Vs2QQOneBotClient : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = false
        };

        private readonly Uri _wsUri;
        private readonly string? _accessToken;
        private readonly int _reconnectIntervalSec;
        private readonly Action<string> _log;
        private readonly Func<JsonObject, CancellationToken, Task> _eventHandler;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _echoWaiters = new();
        private readonly ConcurrentDictionary<(long GroupId, long UserId), GroupMemberDisplayNameCacheEntry> _groupMemberDisplayNames = new();
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private readonly object _socketGate = new();
        private ClientWebSocket? _socket;

        public Vs2QQOneBotClient(
            string wsUrl,
            string? accessToken,
            int reconnectIntervalSec,
            Action<string> log,
            Func<JsonObject, CancellationToken, Task> eventHandler)
        {
            _wsUri = new Uri(wsUrl, UriKind.Absolute);
            _accessToken = accessToken;
            _reconnectIntervalSec = reconnectIntervalSec;
            _log = log;
            _eventHandler = eventHandler;
        }

        public async Task RunForeverAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var socket = new ClientWebSocket();
                if (!string.IsNullOrWhiteSpace(_accessToken))
                {
                    socket.Options.SetRequestHeader("Authorization", $"Bearer {_accessToken}");
                }

                try
                {
                    _log($"[onebot] Connecting {_wsUri} ...");
                    await socket.ConnectAsync(_wsUri, cancellationToken);
                    SetSocket(socket);
                    _log("[onebot] Connected.");
                    await ConsumeMessagesAsync(socket, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log($"[onebot] Disconnected: {ex.Message}");
                }
                finally
                {
                    SetSocket(null);
                    FailPendingWaiters(new InvalidOperationException("OneBot connection closed."));
                }

                await Task.Delay(TimeSpan.FromSeconds(_reconnectIntervalSec), cancellationToken);
            }
        }

        public async Task SendGroupMsgAsync(long groupId, string message, CancellationToken cancellationToken)
        {
            await SendGroupMsgAsync(groupId, JsonValue.Create(message), cancellationToken);
        }

        public async Task SendGroupMsgAsync(long groupId, JsonNode message, CancellationToken cancellationToken)
        {
            var parameters = new JsonObject
            {
                ["group_id"] = groupId,
                ["message"] = message.DeepClone()
            };

            await CallActionAsync("send_group_msg", parameters, TimeSpan.FromSeconds(20), cancellationToken);
        }

        public async Task SendPrivateMsgAsync(long userId, string message, CancellationToken cancellationToken)
        {
            await SendPrivateMsgAsync(userId, JsonValue.Create(message), cancellationToken);
        }

        public async Task SendPrivateMsgAsync(long userId, JsonNode message, CancellationToken cancellationToken)
        {
            var parameters = new JsonObject
            {
                ["user_id"] = userId,
                ["message"] = message.DeepClone()
            };

            await CallActionAsync("send_private_msg", parameters, TimeSpan.FromSeconds(20), cancellationToken);
        }

        public async Task UploadGroupFileAsync(long groupId, string path, string name, CancellationToken cancellationToken)
        {
            var parameters = new JsonObject
            {
                ["group_id"] = groupId,
                ["file"] = path,
                ["name"] = name
            };
            await CallActionAsync("upload_group_file", parameters, TimeSpan.FromSeconds(30), cancellationToken);
        }

        public async Task UploadPrivateFileAsync(long userId, string path, string name, CancellationToken cancellationToken)
        {
            var parameters = new JsonObject
            {
                ["user_id"] = userId,
                ["file"] = path,
                ["name"] = name
            };
            await CallActionAsync("upload_private_file", parameters, TimeSpan.FromSeconds(30), cancellationToken);
        }

        public async Task<string> GetGroupMemberDisplayNameAsync(
            long groupId,
            long userId,
            CancellationToken cancellationToken)
        {
            var cacheKey = (groupId, userId);
            if (_groupMemberDisplayNames.TryGetValue(cacheKey, out var cached))
            {
                if (cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
                {
                    return cached.DisplayName;
                }

                _groupMemberDisplayNames.TryRemove(cacheKey, out _);
            }

            var parameters = new JsonObject
            {
                ["group_id"] = groupId,
                ["user_id"] = userId,
                ["no_cache"] = false
            };
            var data = await CallActionAsync(
                "get_group_member_info",
                parameters,
                TimeSpan.FromSeconds(5),
                cancellationToken);
            if (data is not JsonObject memberInfo)
            {
                return string.Empty;
            }

            var displayName = GetSafeAtDisplayName(
                memberInfo,
                userId.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                _groupMemberDisplayNames[cacheKey] = new GroupMemberDisplayNameCacheEntry(
                    displayName,
                    DateTimeOffset.UtcNow.Add(GroupMemberDisplayNameCacheDuration));
            }

            return displayName;
        }

        public async Task<JsonNode?> CallActionAsync(
            string action,
            JsonObject parameters,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var echo = Guid.NewGuid().ToString("N");
            var waiter = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_echoWaiters.TryAdd(echo, waiter))
            {
                throw new InvalidOperationException("Cannot create action waiter.");
            }

            try
            {
                var payload = new JsonObject
                {
                    ["action"] = action,
                    ["params"] = parameters,
                    ["echo"] = echo
                };

                await SendTextAsync(payload.ToJsonString(JsonOptions), cancellationToken);

                var delayTask = Task.Delay(timeout, cancellationToken);
                var completed = await Task.WhenAny(waiter.Task, delayTask);
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(completed, waiter.Task))
                {
                    throw new TimeoutException(
                        $"OneBot action timeout: {action}. " +
                        "未收到动作回包，请检查 OneBot WS 地址/AccessToken/协议版本是否匹配。");
                }

                var response = await waiter.Task;
                var status = response["status"]?.ToString();
                if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    var retCode = response["retcode"]?.ToString();
                    var msg = response["msg"]?.ToString();
                    throw new InvalidOperationException($"OneBot action failed: action={action}, retcode={retCode}, msg={msg}");
                }

                return response["data"];
            }
            finally
            {
                _echoWaiters.TryRemove(echo, out _);
            }
        }

        public async ValueTask DisposeAsync()
        {
            SetSocket(null);
            FailPendingWaiters(new OperationCanceledException("OneBot client disposed."));

            ClientWebSocket? snapshot;
            lock (_socketGate)
            {
                snapshot = _socket;
                _socket = null;
            }

            if (snapshot is not null)
            {
                try
                {
                    if (snapshot.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await snapshot.CloseAsync(WebSocketCloseStatus.NormalClosure, "dispose", cts.Token);
                    }
                }
                catch
                {
                    // Ignore shutdown errors.
                }
                finally
                {
                    snapshot.Dispose();
                }
            }
        }

        private void SetSocket(ClientWebSocket? socket)
        {
            lock (_socketGate)
            {
                _socket = socket;
            }
        }

        private ClientWebSocket? GetSocket()
        {
            lock (_socketGate)
            {
                return _socket;
            }
        }

        private async Task SendTextAsync(string text, CancellationToken cancellationToken)
        {
            var socket = GetSocket();
            if (socket is null || socket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("OneBot is not connected.");
            }

            await _sendGate.WaitAsync(cancellationToken);
            try
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                await socket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken);
            }
            finally
            {
                _sendGate.Release();
            }
        }

        private async Task ConsumeMessagesAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var text = await ReceiveTextAsync(socket, cancellationToken);
                if (text is null)
                {
                    break;
                }

                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(text);
                }
                catch
                {
                    continue;
                }

                if (node is not JsonObject payload)
                {
                    continue;
                }

                var echoValue = payload["echo"]?.ToString();
                if (!string.IsNullOrWhiteSpace(echoValue)
                    && _echoWaiters.TryGetValue(echoValue, out var waiter))
                {
                    waiter.TrySetResult(payload);
                    continue;
                }

                if (payload["post_type"] is not null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _eventHandler(payload, cancellationToken);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            // Normal shutdown.
                        }
                        catch (Exception ex)
                        {
                            _log($"[warn] OneBot 事件处理异常: {ex.Message}");
                        }
                    }, cancellationToken);
                }
            }
        }

        private static async Task<string?> ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            var buffer = new byte[8 * 1024];
            using var stream = new MemoryStream();

            while (true)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    try
                    {
                        if (socket.State == WebSocketState.CloseReceived)
                        {
                            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "close-received", cancellationToken);
                        }
                    }
                    catch
                    {
                        // Ignore close errors.
                    }

                    return null;
                }

                if (result.Count > 0)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
                }

                if (result.EndOfMessage)
                {
                    break;
                }
            }

            if (stream.Length == 0)
            {
                return string.Empty;
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private void FailPendingWaiters(Exception exception)
        {
            foreach (var item in _echoWaiters.Values)
            {
                item.TrySetException(exception);
            }

            _echoWaiters.Clear();
        }
    }

    private sealed class Vs2QQStorage : IDisposable
    {
        private readonly object _sync = new();
        private readonly SqliteConnection _connection;
        private bool _disposed;
        public Vs2QQStorage(string dbPath)
        {
            var directory = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _connection = new SqliteConnection($"Data Source={dbPath}");
            _connection.Open();
            using (var pragma = _connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys = ON;";
                pragma.ExecuteNonQuery();
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _connection.Dispose();
            }
        }

    }
}
