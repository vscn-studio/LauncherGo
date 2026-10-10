using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Http.Headers;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.API.Util;
using VsslAuth.Network;

namespace VsslAuth.Server;

public sealed class VsslAuthServerSystem : ModSystem
{
    private const string SettingsFileName = "serverauth.json";
    private const string StoreRelativePath = "ServerAuth/players.json";
    private const string CharacterSelectionChannelName = "charselection";
    private const string DeferredCharacterSelectionModDataKey = "serverauth.deferCharacterSelection";
    private const int MinPasswordLength = 6;
    private const int MaxPasswordLength = 128;
    private const int DiscourseChallengeMinutes = 10;
    private const EnumChatType SystemChatType = (EnumChatType)4;

    private static readonly HttpClient OAuth2HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _storeLock = new();
    private readonly object _authLock = new();
    private readonly Dictionary<string, PendingAuthState> _pendingByUid = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DiscourseChallengeState> _discourseByNonce = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OAuth2ChallengeState> _oauth2ByState = new(StringComparer.Ordinal);

    private ICoreServerAPI? _api;
    private IServerNetworkChannel? _channel;
    private volatile ServerAuthSettings _settings = ServerAuthSettings.Default();
    private PlayerStore _store = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;

    public override bool ShouldLoad(EnumAppSide forSide)
    {
        return forSide != EnumAppSide.Client;
    }

    public override double ExecuteOrder()
    {
        return 0.0011;
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        _api = api;
        _channel = api.Network.GetChannel(VsslAuthModSystem.ChannelName);
        _settings = LoadSettings();
        _store = LoadStore();

        api.ChatCommands.Create("register")
            .WithDescription("Register a ServerAuth password")
            .RequiresPrivilege(Privilege.chat)
            .RequiresPlayer()
            .WithArgs(
                api.ChatCommands.Parsers.Word("密码"),
                api.ChatCommands.Parsers.Word("再确认密码"))
            .HandleWith(CmdRegister);

        api.ChatCommands.Create("login")
            .WithDescription("Login with a ServerAuth password")
            .RequiresPrivilege(Privilege.chat)
            .RequiresPlayer()
            .WithArgs(api.ChatCommands.Parsers.Word("密码"))
            .HandleWith(CmdLogin);

        api.ChatCommands.Create("serverauth")
            .WithDescription("ServerAuth admin commands")
            .RequiresPrivilege(Privilege.controlserver)
            .WithArgs(api.ChatCommands.Parsers.OptionalAll("args"))
            .HandleWith(CmdServerAuthAdmin);

        api.Event.PlayerJoin += OnPlayerJoin;
        api.Event.PlayerNowPlaying += OnPlayerNowPlaying;
        api.Event.PlayerDisconnect += OnPlayerDisconnect;
        api.Event.PlayerChat += OnPlayerChat;
        api.Event.CanPlaceOrBreakBlock += OnCanPlaceOrBreakBlock;
        api.Event.CanUseBlock += OnCanUseBlock;
        api.Event.HandInteract += OnHandInteract;
        api.Event.OnPlayerInteractEntity += OnPlayerInteractEntity;
        api.Event.RegisterGameTickListener(OnAuthTick, 1000, 0);

        if (_settings.Enabled && IsExternalAuthEnabled())
            StartAuthListener();
    }

    public override void Dispose()
    {
        StopAuthListener();
    }

    private TextCommandResult CmdRegister(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer player)
            return TextCommandResult.Error("Only players can use this command.", "");

        if (!_settings.Enabled)
            return TextCommandResult.Success("服务器未启用 ServerAuth。", null);

        if (IsExternalAuthEnabled())
            return TextCommandResult.Error("当前服务器使用外部账号认证，请在浏览器中完成认证。", "");

        var password = (args[0] as string ?? string.Empty).Trim();
        var confirmPassword = (args[1] as string ?? string.Empty).Trim();

        if (!ValidatePasswordLength(password, out var error))
            return TextCommandResult.Error(error, "");

        if (!password.Equals(confirmPassword, StringComparison.Ordinal))
            return TextCommandResult.Error("两次输入的密码不一致。", "");

        lock (_storeLock)
        {
            if (IsNicknameTakenByOtherUid(player))
                return TextCommandResult.Error("该昵称已经被其他 UUID 注册，请联系管理员。", "");

            var existing = FindPlayer(player.PlayerUID);
            if (existing is not null &&
                !string.IsNullOrWhiteSpace(existing.PasswordHash) &&
                !existing.PasswordResetRequired)
            {
                return TextCommandResult.Error("你已经注册过，请使用 /login <密码> 登录。", "");
            }

            var now = DateTimeOffset.UtcNow;
            var record = existing ?? new ServerAuthPlayerRecord
            {
                PlayerUid = player.PlayerUID,
                RegisteredAtUtc = now,
                RegisteredIp = player.IpAddress ?? string.Empty
            };

            if (existing is null)
                _store.Players.Add(record);

            record.PlayerName = player.PlayerName;
            record.NormalizedPlayerName = NormalizePlayerName(player.PlayerName);
            record.RegisteredIp = string.IsNullOrWhiteSpace(record.RegisteredIp)
                ? player.IpAddress ?? string.Empty
                : record.RegisteredIp;
            record.PasswordHash = PasswordHasher.Hash(password);
            record.PasswordResetRequired = false;
            record.LastIp = player.IpAddress ?? string.Empty;
            record.LastLoginAtUtc = now;

            RememberSession(record.PlayerUid, record.LastIp, now);
            SaveStoreUnsafe();
        }

        var finalMessage = Authenticate(
            player,
            "注册成功！现在可以移动了！",
            sendChatMessage: false,
            appendCharacterSelectionHint: false);
        return TextCommandResult.Success(finalMessage, null);
    }

    private TextCommandResult CmdLogin(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer player)
            return TextCommandResult.Error("Only players can use this command.", "");

        if (!_settings.Enabled)
            return TextCommandResult.Success("服务器未启用 ServerAuth。", null);

        if (IsExternalAuthEnabled())
            return TextCommandResult.Error("当前服务器使用外部账号认证，请在浏览器中完成认证。", "");

        var password = (args[0] as string ?? string.Empty).Trim();

        lock (_storeLock)
        {
            var record = FindPlayer(player.PlayerUID);
            if (record is null ||
                string.IsNullOrWhiteSpace(record.PasswordHash) ||
                record.PasswordResetRequired)
            {
                return TextCommandResult.Error("你还没有注册，请使用 /register <密码> <再确认密码>。", "");
            }

            if (!PasswordHasher.Verify(password, record.PasswordHash))
                return TextCommandResult.Error("密码错误。", "");

            var now = DateTimeOffset.UtcNow;
            record.LastIp = player.IpAddress ?? string.Empty;
            record.LastLoginAtUtc = now;
            RememberSession(record.PlayerUid, record.LastIp, now);
            SaveStoreUnsafe();
        }

        var finalMessage = Authenticate(player, "登录成功，已通过认证。", sendChatMessage: false);
        return TextCommandResult.Success(finalMessage, null);
    }

    private TextCommandResult CmdServerAuthAdmin(TextCommandCallingArgs args)
    {
        var raw = (args[0] as string ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
            raw = (args.LastArg as string ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
            raw = args.RawArgs?.PopAll() ?? string.Empty;

        var parts = raw.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 1 or 2 && parts[0].Equals("reload", StringComparison.OrdinalIgnoreCase))
        {
            var requestId = parts.Length == 2 ? parts[1] : "manual";
            if (requestId.Length > 64 || requestId.Any(c => !char.IsAsciiLetterOrDigit(c)))
                return TextCommandResult.Error("Invalid reload request ID.", "");
            try
            {
                ReloadSettings();
                _api?.Logger.Notification("{0} reload {1} OK", VsslAuthModSystem.LogPrefix, requestId);
                return TextCommandResult.Success("ServerAuth configuration reloaded and applied.", null);
            }
            catch (Exception ex)
            {
                _api?.Logger.Error("{0} reload {1} ERROR {2}", VsslAuthModSystem.LogPrefix, requestId, ex.Message);
                return TextCommandResult.Error("ServerAuth reload failed: " + ex.Message, "");
            }
        }
        if (parts.Length < 2 || !parts[0].Equals("admin", StringComparison.OrdinalIgnoreCase))
        {
            return TextCommandResult.Error(
                "Usage: /serverauth reload | admin clearpassword <player> | admin setpassword <player> <password> | admin clearsessions",
                "");
        }

        var action = parts[1].ToLowerInvariant();
        lock (_storeLock)
        {
            switch (action)
            {
                case "clearpassword":
                {
                    if (parts.Length < 3)
                        return TextCommandResult.Error("Usage: /serverauth admin clearpassword <player>", "");

                    var record = FindPlayer(parts[2]);
                    if (record is null)
                        return TextCommandResult.Error("Player not found.", "");

                    record.PasswordHash = string.Empty;
                    record.PasswordResetRequired = true;
                    RemoveSessionsForPlayer(record.PlayerUid);
                    SaveStoreUnsafe();
                    return TextCommandResult.Success("Password cleared.", null);
                }
                case "setpassword":
                {
                    if (parts.Length < 4)
                        return TextCommandResult.Error("Usage: /serverauth admin setpassword <player> <password>", "");

                    if (!ValidatePasswordLength(parts[3], out var error))
                        return TextCommandResult.Error(error, "");

                    var record = FindPlayer(parts[2]);
                    if (record is null)
                        return TextCommandResult.Error("Player not found.", "");

                    record.PasswordHash = PasswordHasher.Hash(parts[3]);
                    record.PasswordResetRequired = false;
                    RemoveSessionsForPlayer(record.PlayerUid);
                    SaveStoreUnsafe();
                    return TextCommandResult.Success("Password updated.", null);
                }
                case "clearsessions":
                    _store.Sessions.Clear();
                    SaveStoreUnsafe();
                    return TextCommandResult.Success("Remembered sessions cleared.", null);
                default:
                    return TextCommandResult.Error("Unknown ServerAuth admin command.", "");
            }
        }
    }

    private void OnPlayerJoin(IServerPlayer player)
    {
        if (!_settings.Enabled)
        {
            if (HasDeferredCharacterSelection(player))
            {
                player.RemoveModdata(DeferredCharacterSelectionModDataKey);
                player.RemoveModdata("createCharacter");
            }

            return;
        }

        if (HasCompletedCharacterSelection(player) || HasDeferredCharacterSelection(player))
            return;

        player.SetModdata("createCharacter", SerializerUtil.Serialize(true));
        player.SetModdata(DeferredCharacterSelectionModDataKey, SerializerUtil.Serialize(true));
    }

    private void OnPlayerNowPlaying(IServerPlayer player)
    {
        if (!_settings.Enabled)
            return;

        var now = DateTimeOffset.UtcNow;
        lock (_storeLock)
        {
            if (IsNicknameTakenByOtherUid(player))
            {
                player.Disconnect("该昵称已经被其他 UUID 注册，请联系管理员。");
                return;
            }

            if (HasRememberedSession(player, now))
            {
                var record = FindPlayer(player.PlayerUID);
                if (record is not null)
                {
                    record.LastIp = player.IpAddress ?? string.Empty;
                    record.LastLoginAtUtc = now;
                    SaveStoreUnsafe();
                }

                Authenticate(player, "已使用同 IP 记住会话自动认证。");
                return;
            }
        }

        BeginPending(player, now);

        SendLoginPrompt(player, now);
    }

    private void SendLoginPrompt(IServerPlayer player, DateTimeOffset now)
    {
        if (_settings.OAuth2.Enabled)
        {
            _ = SendOAuth2ChallengeAsync(player, now);
            return;
        }

        if (_settings.Discourse.Enabled)
        {
            SendDiscourseChallenge(player, now);
            return;
        }

        var prompt = HasRegisteredPassword(player)
            ? "请在聊天栏输入 /login <密码> 完成登录。"
            : "请在聊天栏输入 /register <密码> <再确认密码> 完成注册。";
        player.SendMessage(GlobalConstants.GeneralChatGroup, prompt, SystemChatType, null);
    }

    private bool IsExternalAuthEnabled()
    {
        // OAuth2 takes precedence when both switches are enabled so a malformed
        // hand-edited configuration cannot start two competing login flows.
        return _settings.OAuth2.Enabled || _settings.Discourse.Enabled;
    }

    private void OnPlayerDisconnect(IServerPlayer player)
    {
        lock (_authLock)
        {
            _pendingByUid.Remove(player.PlayerUID);
            foreach (var nonce in _discourseByNonce
                         .Where(pair => pair.Value.PlayerUid.Equals(player.PlayerUID, StringComparison.OrdinalIgnoreCase))
                         .Select(pair => pair.Key)
                         .ToList())
            {
                _discourseByNonce.Remove(nonce);
            }
            foreach (var state in _oauth2ByState
                         .Where(pair => pair.Value.PlayerUid.Equals(player.PlayerUID, StringComparison.OrdinalIgnoreCase))
                         .Select(pair => pair.Key)
                         .ToList())
            {
                _oauth2ByState.Remove(state);
            }
        }
    }

    private void OnPlayerChat(
        IServerPlayer byPlayer,
        int channelId,
        ref string message,
        ref string data,
        BoolRef consumed)
    {
        if (!_settings.Enabled || IsAuthenticated(byPlayer))
            return;

        if (message.StartsWith("/login", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("/register", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        consumed.value = true;
        byPlayer.SendMessage(GlobalConstants.GeneralChatGroup, "请先完成服务器认证。", SystemChatType, null);
    }

    private bool OnCanPlaceOrBreakBlock(IServerPlayer byPlayer, BlockSelection blockSel, out string claimant)
    {
        claimant = "custommessage-serverauth";
        return !_settings.Enabled || IsAuthenticated(byPlayer);
    }

    private bool OnCanUseBlock(IServerPlayer byPlayer, BlockSelection blockSel)
    {
        if (!_settings.Enabled || IsAuthenticated(byPlayer))
            return true;

        byPlayer.SendIngameError("serverauth", "请先完成服务器认证。");
        return false;
    }

    private void OnHandInteract(
        IServerPlayer player,
        EnumHandInteractNw handInteract,
        float secondsPassed,
        ref EnumHandling handling)
    {
        if (_settings.Enabled && !IsAuthenticated(player))
            handling = EnumHandling.PreventDefault;
    }

    private void OnPlayerInteractEntity(
        Entity entity,
        IPlayer byPlayer,
        ItemSlot slot,
        Vec3d hitPosition,
        int mode,
        ref EnumHandling handling)
    {
        if (!_settings.Enabled || byPlayer is not IServerPlayer serverPlayer || IsAuthenticated(serverPlayer))
            return;

        handling = EnumHandling.PreventDefault;
    }

    private void OnAuthTick(float dt)
    {
        if (_api is null || !_settings.Enabled)
            return;

        var expired = new List<string>();
        var now = DateTimeOffset.UtcNow;

        lock (_authLock)
        {
            foreach (var state in _pendingByUid.Values)
            {
                var player = _api.World.PlayerByUid(state.PlayerUid) as IServerPlayer;
                if (player is null)
                    continue;

                if (now >= state.DeadlineUtc)
                {
                    expired.Add(state.PlayerUid);
                    player.Disconnect("认证超时，请重新进入服务器。");
                    continue;
                }

                RestrictPlayer(player, state);
            }

            foreach (var uid in expired)
                _pendingByUid.Remove(uid);

            var expiredNonce = _discourseByNonce
                .Where(pair => pair.Value.ExpiresAtUtc <= now)
                .Select(pair => pair.Key)
                .ToList();
            foreach (var nonce in expiredNonce)
                _discourseByNonce.Remove(nonce);

            var expiredStates = _oauth2ByState
                .Where(pair => pair.Value.ExpiresAtUtc <= now)
                .Select(pair => pair.Key)
                .ToList();
            foreach (var state in expiredStates)
                _oauth2ByState.Remove(state);
        }
    }

    private void BeginPending(IServerPlayer player, DateTimeOffset now)
    {
        lock (_authLock)
        {
            var originalMoveSpeed = player.WorldData.MoveSpeedMultiplier <= 0f
                ? 1f
                : player.WorldData.MoveSpeedMultiplier;

            var shouldDeferCharacterSelection = HasDeferredCharacterSelection(player);
            _pendingByUid[player.PlayerUID] = new PendingAuthState
            {
                PlayerUid = player.PlayerUID,
                DeadlineUtc = now.AddSeconds(_settings.LoginTimeoutSeconds),
                OriginalMoveSpeed = originalMoveSpeed,
                DeferredCharacterSelection = shouldDeferCharacterSelection
            };
            RestrictPlayer(player, _pendingByUid[player.PlayerUID]);
        }
    }

    private static void RestrictPlayer(IServerPlayer player, PendingAuthState state)
    {
        player.WorldData.MoveSpeedMultiplier = 0f;
        if (player.Entity?.Controls is not null)
            player.Entity.Controls.MovespeedMultiplier = 0f;
        player.BroadcastPlayerData(false);
    }

    private string Authenticate(
        IServerPlayer player,
        string message,
        bool sendChatMessage = true,
        bool appendCharacterSelectionHint = true)
    {
        PendingAuthState? pending = null;
        lock (_authLock)
        {
            if (_pendingByUid.Remove(player.PlayerUID, out var state))
                pending = state;
        }

        if (pending is not null)
        {
            player.WorldData.MoveSpeedMultiplier = pending.OriginalMoveSpeed <= 0f ? 1f : pending.OriginalMoveSpeed;
            if (player.Entity?.Controls is not null)
                player.Entity.Controls.MovespeedMultiplier = player.WorldData.MoveSpeedMultiplier;
            player.BroadcastPlayerData(false);
        }

        var openedCharacterSelection = false;
        if (pending?.DeferredCharacterSelection == true || HasDeferredCharacterSelection(player))
            openedCharacterSelection = ReleaseDeferredCharacterSelection(player);

        var finalMessage = ComposeAuthMessage(message, openedCharacterSelection, appendCharacterSelectionHint);
        if (sendChatMessage)
            player.SendMessage(GlobalConstants.GeneralChatGroup, finalMessage, SystemChatType, null);
        _channel?.SendPacket(new AuthStatePacket
        {
            IsAuthenticated = true,
            Message = finalMessage,
            OpenCharacterSelection = openedCharacterSelection
        }, player);

        return finalMessage;
    }

    private static string ComposeAuthMessage(
        string message,
        bool openedCharacterSelection,
        bool appendCharacterSelectionHint = true)
    {
        if (!openedCharacterSelection || !appendCharacterSelectionHint)
            return message;

        return message + " 现在可以选择职业了，请完成角色创建。";
    }

    private static bool HasDeferredCharacterSelection(IServerPlayer player)
    {
        return SerializerUtil.Deserialize<bool>(player.GetModdata(DeferredCharacterSelectionModDataKey), false);
    }

    private static bool HasCompletedCharacterSelection(IServerPlayer player)
    {
        return SerializerUtil.Deserialize<bool>(player.GetModdata("createCharacter"), false);
    }

    private bool ReleaseDeferredCharacterSelection(IServerPlayer player)
    {
        var deferred = SerializerUtil.Deserialize<bool>(player.GetModdata(DeferredCharacterSelectionModDataKey), false);
        player.RemoveModdata(DeferredCharacterSelectionModDataKey);
        if (!deferred || _api is null)
            return false;

        player.RemoveModdata("createCharacter");

        var channel = _api.Network.GetChannel(CharacterSelectionChannelName);
        channel.SendPacket(new CharacterSelectedState
        {
            DidSelect = false
        }, player);

        return true;
    }

    private bool IsAuthenticated(IServerPlayer player)
    {
        lock (_authLock)
        {
            return !_pendingByUid.ContainsKey(player.PlayerUID);
        }
    }

    private void SendDiscourseChallenge(IServerPlayer player, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(_settings.Discourse.BaseUrl) ||
            string.IsNullOrWhiteSpace(_settings.Discourse.SharedSecret) ||
            string.IsNullOrWhiteSpace(_settings.Discourse.PublicCallbackBaseUrl))
        {
            player.SendMessage(GlobalConstants.GeneralChatGroup, "服务器社区认证配置不完整，请联系管理员。", SystemChatType, null);
            return;
        }

        var nonce = GenerateNonce();
        var callbackUrl = BuildCallbackUrl(_settings.Discourse.PublicCallbackBaseUrl);
        var authUrl = DiscourseConnect.BuildProviderUrl(
            _settings.Discourse.BaseUrl,
            _settings.Discourse.SharedSecret,
            callbackUrl,
            nonce);

        lock (_authLock)
        {
            _discourseByNonce[nonce] = new DiscourseChallengeState
            {
                Settings = _settings,
                Nonce = nonce,
                PlayerUid = player.PlayerUID,
                ExpiresAtUtc = now.AddMinutes(DiscourseChallengeMinutes)
            };
        }

        var message = "已打开社区认证页面，请在浏览器中完成登录。";
        player.SendMessage(GlobalConstants.GeneralChatGroup, message, SystemChatType, null);
        _channel?.SendPacket(new AuthChallengePacket
        {
            ChallengeId = nonce,
            AuthUrl = authUrl,
            Mode = "discourse",
            Message = message
        }, player);
    }

    private void StartAuthListener(string? listenPrefix = null, bool throwOnFailure = false)
    {
        listenPrefix ??= _settings.OAuth2.Enabled
            ? _settings.OAuth2.ListenPrefix
            : _settings.Discourse.ListenPrefix;
        if (string.IsNullOrWhiteSpace(listenPrefix))
            return;

        var listener = new HttpListener();
        var listenerCts = new CancellationTokenSource();
        try
        {
            listener.Prefixes.Add(NormalizeListenPrefix(listenPrefix));
            listener.Start();
            StopAuthListener();
            _listener = listener;
            _listenerCts = listenerCts;
            var token = listenerCts.Token;
            _listenerTask = Task.Run(() => ListenLoopAsync(listener, token), CancellationToken.None);

            _api?.Logger.Notification(
                "{0} auth callback listener started on {1}",
                VsslAuthModSystem.LogPrefix,
                listenPrefix);
        }
        catch (Exception ex)
        {
            listener.Close();
            listenerCts.Dispose();
            _api?.Logger.Error(
                "{0} Failed to start auth callback listener: {1}",
                VsslAuthModSystem.LogPrefix,
                ex.Message);
            if (throwOnFailure)
                throw;
        }
    }

    private void StopAuthListener()
    {
        try
        {
            _listenerCts?.Cancel();
        }
        catch
        {
            // ignore
        }

        try
        {
            _listener?.Close();
        }
        catch
        {
            // ignore
        }

        _listener = null;
        _listenerTask = null;
        _listenerCts?.Dispose();
        _listenerCts = null;
    }

    private async Task ListenLoopAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
            }

            if (context is null)
                continue;

            _ = Task.Run(() => HandleHttpCallbackAsync(context), CancellationToken.None);
        }
    }

    private async Task HandleHttpCallbackAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? string.Empty;
            var normalizedPath = path.TrimEnd('/');
            if (normalizedPath.EndsWith("/serverauth/discourse/callback", StringComparison.OrdinalIgnoreCase))
            {
                await HandleDiscourseCallbackAsync(context);
                return;
            }

            if (normalizedPath.EndsWith("/serverauth/oauth2/callback", StringComparison.OrdinalIgnoreCase))
            {
                await HandleOAuth2CallbackAsync(context);
                return;
            }

            await WriteHttpResponseAsync(context, 404, "ServerAuth callback endpoint not found.");
        }
        catch (Exception ex)
        {
            await WriteHttpResponseAsync(context, 500, "ServerAuth callback failed: " + ex.Message);
        }
    }

    private async Task HandleDiscourseCallbackAsync(HttpListenerContext context)
    {
        var sso = context.Request.QueryString["sso"] ?? string.Empty;
        var sig = context.Request.QueryString["sig"] ?? string.Empty;
        if (!DiscourseConnect.VerifySignature(sso, sig, _settings.Discourse.SharedSecret))
        {
            await WriteHttpResponseAsync(context, 403, "Invalid Discourse signature.");
            return;
        }

        var payload = DiscourseConnect.DecodePayload(sso);
        if (!payload.TryGetValue("nonce", out var nonce) || string.IsNullOrWhiteSpace(nonce))
        {
            await WriteHttpResponseAsync(context, 400, "Missing nonce.");
            return;
        }

        DiscourseChallengeState? challenge;
        lock (_authLock)
        {
            _discourseByNonce.Remove(nonce, out challenge);
        }

        if (challenge is null || !ReferenceEquals(challenge.Settings, _settings) || DateTimeOffset.UtcNow > challenge.ExpiresAtUtc)
        {
            await WriteHttpResponseAsync(context, 400, "Challenge expired. Please rejoin the server.");
            return;
        }

        if (_api is not null)
        {
            _api.Event.EnqueueMainThreadTask(
                () => CompleteDiscourseAuth(challenge, payload),
                "serverauth-discourse-complete");
        }

        await WriteAuthSuccessResponseAsync(context, "认证成功，已完成认证，请返回游戏。");
    }

    private async Task HandleOAuth2CallbackAsync(HttpListenerContext context)
    {
        var error = context.Request.QueryString["error"];
        var stateValue = context.Request.QueryString["state"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(stateValue))
        {
            await WriteHttpResponseAsync(context, 400, "Missing OAuth2 state.");
            return;
        }

        OAuth2ChallengeState? challenge;
        lock (_authLock)
        {
            _oauth2ByState.Remove(stateValue, out challenge);
        }

        if (challenge is null || !ReferenceEquals(challenge.Settings, _settings) || DateTimeOffset.UtcNow > challenge.ExpiresAtUtc)
        {
            await WriteHttpResponseAsync(context, 400, "OAuth2 challenge expired. Please rejoin the server.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            var description = context.Request.QueryString["error_description"] ?? error;
            await WriteHttpResponseAsync(context, 400, "OAuth2 login was not completed: " + description);
            return;
        }

        var code = context.Request.QueryString["code"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(code))
        {
            await WriteHttpResponseAsync(context, 400, "Missing OAuth2 authorization code.");
            return;
        }

        OAuth2Identity identity;
        try
        {
            identity = await ExchangeOAuth2CodeAsync(challenge, code);
        }
        catch
        {
            QueueAuthMessage(
                challenge.PlayerUid,
                "OAuth2 认证失败，请重新进入服务器后再试。",
                "serverauth-oauth2-callback-error");
            throw;
        }
        if (_api is not null)
        {
            _api.Event.EnqueueMainThreadTask(
                () => CompleteOAuth2Auth(challenge, identity),
                "serverauth-oauth2-complete");
        }

        await WriteAuthSuccessResponseAsync(context, "认证成功，已完成认证，请返回游戏。");
    }

    private void CompleteDiscourseAuth(DiscourseChallengeState challenge, Dictionary<string, string> payload)
    {
        if (_api is null || !ReferenceEquals(challenge.Settings, _settings) || !_settings.Enabled || IsChallengeInactive(challenge.PlayerUid))
            return;

        var player = _api.World.PlayerByUid(challenge.PlayerUid) as IServerPlayer;
        if (player is null)
            return;

        var externalId = payload.TryGetValue("external_id", out var rawExternalId) ? rawExternalId : string.Empty;
        var discourseUsername = payload.TryGetValue("username", out var rawUserName) ? rawUserName : string.Empty;
        var discourseEmail = payload.TryGetValue("email", out var rawEmail) ? rawEmail : string.Empty;

        if (string.IsNullOrWhiteSpace(externalId))
        {
            player.SendMessage(GlobalConstants.GeneralChatGroup, "社区认证缺少 external_id，请联系管理员。", SystemChatType, null);
            return;
        }

        lock (_storeLock)
        {
            var conflictByExternalId = _store.Players.FirstOrDefault(record =>
                record.DiscourseExternalId.Equals(externalId, StringComparison.OrdinalIgnoreCase) &&
                !record.PlayerUid.Equals(player.PlayerUID, StringComparison.OrdinalIgnoreCase));
            if (conflictByExternalId is not null)
            {
                player.Disconnect("该社区账号已经绑定其他玩家。");
                return;
            }

            if (IsNicknameTakenByOtherUid(player))
            {
                player.Disconnect("该昵称已经被其他 UUID 注册，请联系管理员。");
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var record = FindPlayer(player.PlayerUID) ?? new ServerAuthPlayerRecord
            {
                PlayerUid = player.PlayerUID,
                RegisteredAtUtc = now,
                RegisteredIp = player.IpAddress ?? string.Empty
            };
            if (!_store.Players.Contains(record))
                _store.Players.Add(record);

            record.PlayerName = player.PlayerName;
            record.NormalizedPlayerName = NormalizePlayerName(player.PlayerName);
            record.LastIp = player.IpAddress ?? string.Empty;
            record.LastLoginAtUtc = now;
            record.DiscourseExternalId = externalId;
            record.DiscourseUsername = discourseUsername;
            record.DiscourseEmail = discourseEmail;
            RememberSession(record.PlayerUid, record.LastIp, now);
            SaveStoreUnsafe();
        }

        Authenticate(player, "社区认证成功，已通过服务器认证。");
    }

    private void CompleteOAuth2Auth(OAuth2ChallengeState challenge, OAuth2Identity identity)
    {
        if (_api is null || !ReferenceEquals(challenge.Settings, _settings) || !_settings.Enabled || IsChallengeInactive(challenge.PlayerUid))
            return;

        var player = _api.World.PlayerByUid(challenge.PlayerUid) as IServerPlayer;
        if (player is null)
            return;

        if (string.IsNullOrWhiteSpace(identity.Subject))
        {
            player.SendMessage(GlobalConstants.GeneralChatGroup, "OAuth2 认证缺少稳定用户标识，请联系管理员。", SystemChatType, null);
            return;
        }

        lock (_storeLock)
        {
            var conflictBySubject = _store.Players.FirstOrDefault(record =>
                record.OAuth2Subject.Equals(identity.Subject, StringComparison.Ordinal) &&
                !record.PlayerUid.Equals(player.PlayerUID, StringComparison.OrdinalIgnoreCase));
            if (conflictBySubject is not null)
            {
                player.Disconnect("该 OAuth2 账号已经绑定其他玩家。");
                return;
            }

            if (IsNicknameTakenByOtherUid(player))
            {
                player.Disconnect("该昵称已经被其他 UUID 注册，请联系管理员。");
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var record = FindPlayer(player.PlayerUID) ?? new ServerAuthPlayerRecord
            {
                PlayerUid = player.PlayerUID,
                RegisteredAtUtc = now,
                RegisteredIp = player.IpAddress ?? string.Empty
            };
            if (!_store.Players.Contains(record))
                _store.Players.Add(record);

            record.PlayerName = player.PlayerName;
            record.NormalizedPlayerName = NormalizePlayerName(player.PlayerName);
            record.LastIp = player.IpAddress ?? string.Empty;
            record.LastLoginAtUtc = now;
            record.OAuth2Subject = identity.Subject;
            record.OAuth2Username = identity.Username;
            record.OAuth2DisplayName = identity.DisplayName;
            record.OAuth2Email = identity.Email;
            RememberSession(record.PlayerUid, record.LastIp, now);
            SaveStoreUnsafe();
        }

        Authenticate(player, "OAuth2 认证成功，已通过服务器认证。");
    }

    private bool IsChallengeInactive(string playerUid)
    {
        lock (_authLock)
            return !_pendingByUid.ContainsKey(playerUid);
    }

    private void ReloadSettings()
    {
        var path = Path.Combine(GamePaths.ModConfig, SettingsFileName);
        var next = ServerAuthSettings.Normalize(
            JsonSerializer.Deserialize<ServerAuthSettings>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException("Auth configuration must be a JSON object."));
        var previous = _settings;
        var previousPrefix = GetActiveListenPrefix(previous);
        var nextPrefix = GetActiveListenPrefix(next);
        var restartListener = !string.Equals(previousPrefix, nextPrefix, StringComparison.OrdinalIgnoreCase)
                              || (nextPrefix.Length > 0 && _listener?.IsListening != true);

        // Bind the replacement before publishing settings or closing the old listener.
        if (restartListener)
        {
            if (nextPrefix.Length > 0)
                StartAuthListener(nextPrefix, throwOnFailure: true);
            else
                StopAuthListener();
        }
        _settings = next;

        List<PendingAuthState> pending;
        lock (_authLock)
        {
            _discourseByNonce.Clear();
            _oauth2ByState.Clear();
            pending = _pendingByUid.Values.ToList();
        }
        if (next.RememberSessionMinutes == 0 ||
            JsonSerializer.Serialize(previous.Discourse, JsonOptions) != JsonSerializer.Serialize(next.Discourse, JsonOptions) ||
            JsonSerializer.Serialize(previous.OAuth2, JsonOptions) != JsonSerializer.Serialize(next.OAuth2, JsonOptions))
        {
            lock (_storeLock)
            {
                _store.Sessions.Clear();
                try
                {
                    SaveStoreUnsafe();
                }
                catch (Exception ex)
                {
                    _api?.Logger.Warning("{0} Failed to persist session invalidation: {1}", VsslAuthModSystem.LogPrefix, ex.Message);
                }
            }
        }
        var now = DateTimeOffset.UtcNow;
        foreach (var state in pending)
        {
            var player = _api?.World.PlayerByUid(state.PlayerUid) as IServerPlayer;
            if (player is null)
            {
                lock (_authLock)
                    _pendingByUid.Remove(state.PlayerUid);
                continue;
            }
            if (!next.Enabled)
                Authenticate(player, "服务器认证已关闭。");
            else
            {
                state.DeadlineUtc = now.AddSeconds(next.LoginTimeoutSeconds);
                SendLoginPrompt(player, now);
            }
        }
        if (!previous.Enabled && next.Enabled && _api is not null)
        {
            foreach (var player in _api.World.AllOnlinePlayers.OfType<IServerPlayer>())
            {
                if (player.ConnectionState == EnumClientState.Playing && !pending.Any(state => state.PlayerUid == player.PlayerUID))
                    OnPlayerNowPlaying(player);
            }
        }
    }

    private static string GetActiveListenPrefix(ServerAuthSettings settings)
    {
        if (!settings.Enabled || (!settings.OAuth2.Enabled && !settings.Discourse.Enabled))
            return string.Empty;
        return NormalizeListenPrefix(settings.OAuth2.Enabled ? settings.OAuth2.ListenPrefix : settings.Discourse.ListenPrefix);
    }

    private ServerAuthSettings LoadSettings()
    {
        var path = Path.Combine(GamePaths.ModConfig, SettingsFileName);
        if (!File.Exists(path))
        {
            var defaults = ServerAuthSettings.Default();
            SaveSettings(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(path);
            return ServerAuthSettings.Normalize(JsonSerializer.Deserialize<ServerAuthSettings>(json, JsonOptions));
        }
        catch
        {
            return ServerAuthSettings.Default();
        }
    }

    private static void SaveSettings(ServerAuthSettings settings)
    {
        Directory.CreateDirectory(GamePaths.ModConfig);
        var path = Path.Combine(GamePaths.ModConfig, SettingsFileName);
        var normalized = ServerAuthSettings.Normalize(settings);
        File.WriteAllText(path, JsonSerializer.Serialize(normalized, JsonOptions));
    }

    private PlayerStore LoadStore()
    {
        var path = GetStorePath();
        if (!File.Exists(path))
            return new PlayerStore();

        try
        {
            return JsonSerializer.Deserialize<PlayerStore>(File.ReadAllText(path), JsonOptions) ?? new PlayerStore();
        }
        catch
        {
            return new PlayerStore();
        }
    }

    private void SaveStoreUnsafe()
    {
        var path = GetStorePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_store, JsonOptions), Encoding.UTF8);
        File.Move(temp, path, true);
    }

    private static string GetStorePath()
    {
        return Path.Combine(GamePaths.DataPath, StoreRelativePath);
    }

    private ServerAuthPlayerRecord? FindPlayer(string playerNameOrUid)
    {
        var normalized = NormalizePlayerName(playerNameOrUid);
        return _store.Players.FirstOrDefault(player =>
            player.PlayerUid.Equals(playerNameOrUid, StringComparison.OrdinalIgnoreCase) ||
            player.PlayerName.Equals(playerNameOrUid, StringComparison.OrdinalIgnoreCase) ||
            player.NormalizedPlayerName.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private bool HasRegisteredPassword(IServerPlayer player)
    {
        lock (_storeLock)
        {
            var record = FindPlayer(player.PlayerUID);
            return record is not null &&
                   !record.PasswordResetRequired &&
                   !string.IsNullOrWhiteSpace(record.PasswordHash);
        }
    }

    private bool HasRememberedSession(IServerPlayer player, DateTimeOffset now)
    {
        if (_settings.RememberSessionMinutes <= 0)
            return false;
        var ip = player.IpAddress ?? string.Empty;
        return _store.Sessions.Any(session =>
            session.PlayerUid.Equals(player.PlayerUID, StringComparison.OrdinalIgnoreCase) &&
            session.IpAddress.Equals(ip, StringComparison.OrdinalIgnoreCase) &&
            session.ExpiresAtUtc > now);
    }

    private void RememberSession(string playerUid, string ipAddress, DateTimeOffset now)
    {
        _store.Sessions = _store.Sessions
            .Where(session => session.ExpiresAtUtc > now)
            .Where(session =>
                !session.PlayerUid.Equals(playerUid, StringComparison.OrdinalIgnoreCase) ||
                !session.IpAddress.Equals(ipAddress, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (_settings.RememberSessionMinutes <= 0 || string.IsNullOrWhiteSpace(ipAddress))
            return;

        _store.Sessions.Add(new ServerAuthSessionRecord
        {
            PlayerUid = playerUid,
            IpAddress = ipAddress,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(_settings.RememberSessionMinutes)
        });
    }

    private async Task SendOAuth2ChallengeAsync(IServerPlayer player, DateTimeOffset now)
    {
        var settings = _settings;
        var config = settings.OAuth2;
        if (string.IsNullOrWhiteSpace(config.ClientId) ||
            string.IsNullOrWhiteSpace(config.PublicCallbackBaseUrl))
        {
            QueueAuthMessage(player.PlayerUID, "OAuth2 认证配置不完整，请联系管理员。", "serverauth-oauth2-config");
            return;
        }

        try
        {
            var endpoints = await ResolveOAuth2EndpointsAsync(config).ConfigureAwait(false);
            var state = GenerateNonce();
            var verifier = GeneratePkceVerifier();
            var redirectUri = BuildOAuth2CallbackUrl(config.PublicCallbackBaseUrl);
            if (!IsHttpUrl(redirectUri))
                throw new InvalidOperationException("OAuth2 public callback URL is invalid.");
            var authUrl = BuildOAuth2AuthorizationUrl(
                endpoints.AuthorizationEndpoint,
                config.ClientId,
                redirectUri,
                config.Scope,
                state,
                verifier);
            var challenge = new OAuth2ChallengeState
            {
                Settings = settings,
                PlayerUid = player.PlayerUID,
                CodeVerifier = verifier,
                RedirectUri = redirectUri,
                Endpoints = endpoints,
                ClientId = config.ClientId,
                ClientSecret = config.ClientSecret,
                UserIdClaim = config.UserIdClaim,
                UsernameClaim = config.UsernameClaim,
                DisplayNameClaim = config.DisplayNameClaim,
                EmailClaim = config.EmailClaim,
                ExpiresAtUtc = now.AddSeconds(settings.LoginTimeoutSeconds)
            };

            lock (_authLock)
            {
                if (!ReferenceEquals(settings, _settings) || !_pendingByUid.ContainsKey(player.PlayerUID))
                    return;
                _oauth2ByState[state] = challenge;
            }

            QueueAuthChallenge(
                player.PlayerUID,
                state,
                authUrl,
                "oauth2",
                "已打开 OAuth2 登录页面，请在浏览器中完成登录。",
                "serverauth-oauth2-challenge",
                settings);
        }
        catch (Exception ex)
        {
            _api?.Logger.Error(
                "{0} Failed to build OAuth2 challenge: {1}",
                VsslAuthModSystem.LogPrefix,
                ex.Message);
            QueueAuthMessage(player.PlayerUID, "OAuth2 登录暂时不可用，请联系管理员。", "serverauth-oauth2-error");
        }
    }

    private async Task<OAuth2Endpoints> ResolveOAuth2EndpointsAsync(ServerAuthOAuth2Settings config)
    {
        var authorizationEndpoint = config.AuthorizationEndpoint.Trim();
        var tokenEndpoint = config.TokenEndpoint.Trim();
        var userInfoEndpoint = config.UserInfoEndpoint.Trim();

        if (!string.IsNullOrWhiteSpace(config.DiscoveryUrl) &&
            (string.IsNullOrWhiteSpace(authorizationEndpoint) ||
             string.IsNullOrWhiteSpace(tokenEndpoint) ||
             string.IsNullOrWhiteSpace(userInfoEndpoint)))
        {
            using var response = await OAuth2HttpClient.GetAsync(config.DiscoveryUrl).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"OAuth2 discovery request failed ({(int)response.StatusCode}).");
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            authorizationEndpoint = authorizationEndpoint.Length == 0
                ? ReadJsonProperty(root, "authorization_endpoint")
                : authorizationEndpoint;
            tokenEndpoint = tokenEndpoint.Length == 0
                ? ReadJsonProperty(root, "token_endpoint")
                : tokenEndpoint;
            userInfoEndpoint = userInfoEndpoint.Length == 0
                ? ReadJsonProperty(root, "userinfo_endpoint")
                : userInfoEndpoint;
        }

        if (!IsHttpUrl(authorizationEndpoint) ||
            !IsHttpUrl(tokenEndpoint) ||
            !IsHttpUrl(userInfoEndpoint))
        {
            throw new InvalidOperationException(
                "OAuth2 requires valid authorization, token, and UserInfo endpoints.");
        }

        return new OAuth2Endpoints
        {
            AuthorizationEndpoint = authorizationEndpoint,
            TokenEndpoint = tokenEndpoint,
            UserInfoEndpoint = userInfoEndpoint
        };
    }

    private async Task<OAuth2Identity> ExchangeOAuth2CodeAsync(
        OAuth2ChallengeState challenge,
        string code)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, challenge.Endpoints.TokenEndpoint);
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("code", code),
            new("redirect_uri", challenge.RedirectUri),
            new("code_verifier", challenge.CodeVerifier)
        };

        if (string.IsNullOrWhiteSpace(challenge.ClientSecret))
        {
            form.Add(new KeyValuePair<string, string>("client_id", challenge.ClientId));
        }
        else
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(
                    FormUrlEncode(challenge.ClientId) + ":" + FormUrlEncode(challenge.ClientSecret)));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        request.Content = new FormUrlEncodedContent(form);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var tokenResponse = await OAuth2HttpClient.SendAsync(request).ConfigureAwait(false);
        var tokenBody = await tokenResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"OAuth2 token request failed ({(int)tokenResponse.StatusCode}).");
        }

        using var tokenDocument = JsonDocument.Parse(tokenBody);
        var accessToken = ReadJsonProperty(tokenDocument.RootElement, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("OAuth2 token response did not contain access_token.");

        using var userInfoRequest = new HttpRequestMessage(HttpMethod.Get, challenge.Endpoints.UserInfoEndpoint);
        userInfoRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        userInfoRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var userInfoResponse = await OAuth2HttpClient.SendAsync(userInfoRequest).ConfigureAwait(false);
        var userInfoBody = await userInfoResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!userInfoResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"OAuth2 UserInfo request failed ({(int)userInfoResponse.StatusCode}).");
        }

        using var userInfoDocument = JsonDocument.Parse(userInfoBody);
        var root = userInfoDocument.RootElement;
        var subject = ReadJsonClaim(root, challenge.UserIdClaim, "sub");
        if (string.IsNullOrWhiteSpace(subject))
            throw new InvalidOperationException("OAuth2 UserInfo did not contain a stable user id claim.");

        var username = ReadJsonClaim(root, challenge.UsernameClaim, "preferred_username", "username");
        var displayName = ReadJsonClaim(root, challenge.DisplayNameClaim, "name", "preferred_username", "username");
        var email = ReadJsonClaim(root, challenge.EmailClaim, "email");

        return new OAuth2Identity
        {
            Subject = subject,
            Username = string.IsNullOrWhiteSpace(username) ? subject : username,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? username : displayName,
            Email = email
        };
    }

    private void QueueAuthChallenge(
        string playerUid,
        string challengeId,
        string authUrl,
        string mode,
        string message,
        string taskName,
        ServerAuthSettings settings)
    {
        if (_api is null)
            return;

        _api.Event.EnqueueMainThreadTask(
            () =>
            {
                if (!ReferenceEquals(settings, _settings) || IsChallengeInactive(playerUid))
                    return;
                if (_api.World.PlayerByUid(playerUid) is not IServerPlayer player)
                    return;

                player.SendMessage(GlobalConstants.GeneralChatGroup, message, SystemChatType, null);
                _channel?.SendPacket(new AuthChallengePacket
                {
                    ChallengeId = challengeId,
                    AuthUrl = authUrl,
                    Mode = mode,
                    Message = message
                }, player);
            },
            taskName);
    }

    private void QueueAuthMessage(string playerUid, string message, string taskName)
    {
        if (_api is null)
            return;

        _api.Event.EnqueueMainThreadTask(
            () =>
            {
                if (_api.World.PlayerByUid(playerUid) is IServerPlayer player)
                    player.SendMessage(GlobalConstants.GeneralChatGroup, message, SystemChatType, null);
            },
            taskName);
    }

    private static string BuildOAuth2AuthorizationUrl(
        string endpoint,
        string clientId,
        string redirectUri,
        string scope,
        string state,
        string codeVerifier)
    {
        var codeChallenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        var separator = endpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var parameters = new[]
        {
            ("response_type", "code"),
            ("client_id", clientId),
            ("redirect_uri", redirectUri),
            ("scope", scope),
            ("state", state),
            ("code_challenge", codeChallenge),
            ("code_challenge_method", "S256")
        };

        return endpoint + separator + string.Join(
            "&",
            parameters.Select(pair => Uri.EscapeDataString(pair.Item1) + "=" + Uri.EscapeDataString(pair.Item2)));
    }

    private static string BuildOAuth2CallbackUrl(string publicCallbackBaseUrl)
    {
        var baseUrl = publicCallbackBaseUrl.Trim();
        if (baseUrl.Contains("/serverauth/oauth2/callback", StringComparison.OrdinalIgnoreCase))
            return baseUrl;

        return baseUrl.TrimEnd('/') + "/serverauth/oauth2/callback";
    }

    private static string GeneratePkceVerifier()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string FormUrlEncode(string value)
    {
        return Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal);
    }

    private static bool IsHttpUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static string ReadJsonProperty(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return string.Empty;

        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            return property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()?.Trim() ?? string.Empty
                : property.Value.ToString();
        }

        return string.Empty;
    }

    private static string ReadJsonClaim(JsonElement root, string configuredClaim, params string[] fallbacks)
    {
        var candidates = new List<string> { configuredClaim };
        candidates.AddRange(fallbacks);
        foreach (var claim in candidates
                     .Where(static value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var exactValue = ReadJsonProperty(root, claim);
            if (!string.IsNullOrWhiteSpace(exactValue))
                return exactValue;

            var current = root;
            var found = true;
            foreach (var segment in claim.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.ValueKind != JsonValueKind.Object)
                {
                    found = false;
                    break;
                }

                var matched = false;
                foreach (var property in current.EnumerateObject())
                {
                    if (!property.Name.Equals(segment, StringComparison.OrdinalIgnoreCase))
                        continue;

                    current = property.Value;
                    matched = true;
                    break;
                }

                if (!matched)
                {
                    found = false;
                    break;
                }
            }

            if (!found)
                continue;

            var value = current.ValueKind switch
            {
                JsonValueKind.String => current.GetString()?.Trim() ?? string.Empty,
                JsonValueKind.Number => current.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => string.Empty
            };
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }

    private bool IsNicknameTakenByOtherUid(IServerPlayer player)
    {
        var normalized = NormalizePlayerName(player.PlayerName);
        return _store.Players.Any(record =>
            record.NormalizedPlayerName.Equals(normalized, StringComparison.OrdinalIgnoreCase) &&
            !record.PlayerUid.Equals(player.PlayerUID, StringComparison.OrdinalIgnoreCase));
    }

    private void RemoveSessionsForPlayer(string playerUid)
    {
        _store.Sessions = _store.Sessions
            .Where(session => !session.PlayerUid.Equals(playerUid, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static bool ValidatePasswordLength(string password, out string error)
    {
        error = string.Empty;
        if (password.Length < MinPasswordLength || password.Length > MaxPasswordLength)
        {
            error = $"密码长度必须在 {MinPasswordLength} 到 {MaxPasswordLength} 个字符之间。";
            return false;
        }

        return true;
    }

    private static string NormalizePlayerName(string? value)
    {
        return value?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    private static string GenerateNonce()
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string BuildCallbackUrl(string publicCallbackBaseUrl)
    {
        var baseUrl = publicCallbackBaseUrl.Trim();
        if (baseUrl.Contains("/serverauth/discourse/callback", StringComparison.OrdinalIgnoreCase))
            return baseUrl;

        return baseUrl.TrimEnd('/') + "/serverauth/discourse/callback";
    }

    private static string NormalizeListenPrefix(string value)
    {
        var prefix = string.IsNullOrWhiteSpace(value) ? "http://127.0.0.1:18092/" : value.Trim();
        return prefix.EndsWith('/') ? prefix : prefix + "/";
    }

    private static async Task WriteHttpResponseAsync(HttpListenerContext context, int statusCode, string message)
    {
        var html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>ServerAuth</title></head><body>"
                   + WebUtility.HtmlEncode(message)
                   + "</body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static async Task WriteAuthSuccessResponseAsync(
        HttpListenerContext context,
        string message)
    {
        var html =
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>ServerAuth</title></head><body>" +
            "<h3>" + WebUtility.HtmlEncode(message) + "</h3>" +
            "<p style=\"color:#666;\">认证已完成，请返回游戏客户端。</p>" +
            "</body></html>";

        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private sealed class PendingAuthState
    {
        public string PlayerUid { get; init; } = string.Empty;
        public DateTimeOffset DeadlineUtc { get; set; }
        public float OriginalMoveSpeed { get; init; } = 1f;
        public bool DeferredCharacterSelection { get; init; }
    }

    private sealed class DiscourseChallengeState
    {
        public ServerAuthSettings Settings { get; init; } = null!;
        public string Nonce { get; init; } = string.Empty;
        public string PlayerUid { get; init; } = string.Empty;
        public DateTimeOffset ExpiresAtUtc { get; init; }
    }

    private sealed class OAuth2ChallengeState
    {
        public ServerAuthSettings Settings { get; init; } = null!;
        public string PlayerUid { get; init; } = string.Empty;
        public string CodeVerifier { get; init; } = string.Empty;
        public string RedirectUri { get; init; } = string.Empty;
        public OAuth2Endpoints Endpoints { get; init; } = new();
        public string ClientId { get; init; } = string.Empty;
        public string ClientSecret { get; init; } = string.Empty;
        public string UserIdClaim { get; init; } = string.Empty;
        public string UsernameClaim { get; init; } = string.Empty;
        public string DisplayNameClaim { get; init; } = string.Empty;
        public string EmailClaim { get; init; } = string.Empty;
        public DateTimeOffset ExpiresAtUtc { get; init; }
    }

    private sealed class OAuth2Endpoints
    {
        public string AuthorizationEndpoint { get; init; } = string.Empty;
        public string TokenEndpoint { get; init; } = string.Empty;
        public string UserInfoEndpoint { get; init; } = string.Empty;
    }

    private sealed class OAuth2Identity
    {
        public string Subject { get; init; } = string.Empty;
        public string Username { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string Email { get; init; } = string.Empty;
    }

    private sealed class PlayerStore
    {
        public List<ServerAuthPlayerRecord> Players { get; set; } = [];
        public List<ServerAuthSessionRecord> Sessions { get; set; } = [];
    }

    private sealed class ServerAuthSettings
    {
        public bool Enabled { get; set; }
        public int LoginTimeoutSeconds { get; set; } = 60;
        public int RememberSessionMinutes { get; set; } = 30;
        public ServerAuthDiscourseSettings Discourse { get; set; } = new();
        public ServerAuthOAuth2Settings OAuth2 { get; set; } = new();

        public static ServerAuthSettings Default()
        {
            return new ServerAuthSettings
            {
                Enabled = false,
                LoginTimeoutSeconds = 60,
                RememberSessionMinutes = 30,
                Discourse = new ServerAuthDiscourseSettings(),
                OAuth2 = new ServerAuthOAuth2Settings()
            };
        }

        public static ServerAuthSettings Normalize(ServerAuthSettings? settings)
        {
            var normalized = settings ?? Default();
            normalized.LoginTimeoutSeconds = Math.Clamp(normalized.LoginTimeoutSeconds, 10, 600);
            normalized.RememberSessionMinutes = Math.Clamp(normalized.RememberSessionMinutes, 0, 1440);
            normalized.Discourse ??= new ServerAuthDiscourseSettings();
            normalized.Discourse.BaseUrl = NormalizeUrl(normalized.Discourse.BaseUrl);
            normalized.Discourse.SharedSecret = normalized.Discourse.SharedSecret?.Trim() ?? string.Empty;
            normalized.Discourse.PublicCallbackBaseUrl = NormalizeUrl(
                normalized.Discourse.PublicCallbackBaseUrl,
                "http://127.0.0.1:18092/");
            normalized.Discourse.ListenPrefix = NormalizeUrl(
                normalized.Discourse.ListenPrefix,
                "http://127.0.0.1:18092/");
            normalized.OAuth2 ??= new ServerAuthOAuth2Settings();
            normalized.OAuth2.DiscoveryUrl = NormalizeEndpoint(normalized.OAuth2.DiscoveryUrl);
            normalized.OAuth2.AuthorizationEndpoint = NormalizeEndpoint(normalized.OAuth2.AuthorizationEndpoint);
            normalized.OAuth2.TokenEndpoint = NormalizeEndpoint(normalized.OAuth2.TokenEndpoint);
            normalized.OAuth2.UserInfoEndpoint = NormalizeEndpoint(normalized.OAuth2.UserInfoEndpoint);
            normalized.OAuth2.ClientId = normalized.OAuth2.ClientId?.Trim() ?? string.Empty;
            normalized.OAuth2.ClientSecret = normalized.OAuth2.ClientSecret?.Trim() ?? string.Empty;
            normalized.OAuth2.Scope = string.IsNullOrWhiteSpace(normalized.OAuth2.Scope)
                ? "openid profile email"
                : normalized.OAuth2.Scope.Trim();
            normalized.OAuth2.PublicCallbackBaseUrl = NormalizeOAuth2CallbackUrl(
                normalized.OAuth2.PublicCallbackBaseUrl);
            normalized.OAuth2.ListenPrefix = NormalizeUrl(
                normalized.OAuth2.ListenPrefix,
                "http://127.0.0.1:18092/");
            normalized.OAuth2.UserIdClaim = NormalizeClaim(normalized.OAuth2.UserIdClaim, "sub");
            normalized.OAuth2.UsernameClaim = NormalizeClaim(normalized.OAuth2.UsernameClaim, "preferred_username");
            normalized.OAuth2.DisplayNameClaim = NormalizeClaim(normalized.OAuth2.DisplayNameClaim, "name");
            normalized.OAuth2.EmailClaim = NormalizeClaim(normalized.OAuth2.EmailClaim, "email");
            return normalized;
        }

        private static string NormalizeEndpoint(string? value)
        {
            return value?.Trim() ?? string.Empty;
        }

        private static string NormalizeClaim(string? value, string fallback)
        {
            var candidate = value?.Trim() ?? string.Empty;
            return string.IsNullOrWhiteSpace(candidate) ? fallback : candidate;
        }

        private static string NormalizeOAuth2CallbackUrl(string? value)
        {
            var candidate = value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(candidate))
                return "http://127.0.0.1:18092/";
            if (candidate.Contains("/serverauth/oauth2/callback", StringComparison.OrdinalIgnoreCase))
                return candidate.TrimEnd('/');

            return candidate.EndsWith('/') ? candidate : candidate + "/";
        }

        private static string NormalizeUrl(string? value, string fallback = "")
        {
            var candidate = value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(candidate))
                return fallback;

            return candidate.EndsWith('/') ? candidate : candidate + "/";
        }
    }

    private sealed class ServerAuthDiscourseSettings
    {
        public bool Enabled { get; set; }
        public string BaseUrl { get; set; } = string.Empty;
        public string SharedSecret { get; set; } = string.Empty;
        public string PublicCallbackBaseUrl { get; set; } = "http://127.0.0.1:18092/";
        public string ListenPrefix { get; set; } = "http://127.0.0.1:18092/";
    }

    private sealed class ServerAuthOAuth2Settings
    {
        public bool Enabled { get; set; }
        public string DiscoveryUrl { get; set; } = string.Empty;
        public string AuthorizationEndpoint { get; set; } = string.Empty;
        public string TokenEndpoint { get; set; } = string.Empty;
        public string UserInfoEndpoint { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
        public string Scope { get; set; } = "openid profile email";
        public string PublicCallbackBaseUrl { get; set; } = "http://127.0.0.1:18092/";
        public string ListenPrefix { get; set; } = "http://127.0.0.1:18092/";
        public string UserIdClaim { get; set; } = "sub";
        public string UsernameClaim { get; set; } = "preferred_username";
        public string DisplayNameClaim { get; set; } = "name";
        public string EmailClaim { get; set; } = "email";
    }

    private sealed class ServerAuthPlayerRecord
    {
        public string PlayerUid { get; set; } = string.Empty;
        public string PlayerName { get; set; } = string.Empty;
        public string NormalizedPlayerName { get; set; } = string.Empty;
        public string RegisteredIp { get; set; } = string.Empty;
        public DateTimeOffset RegisteredAtUtc { get; set; }
        public string LastIp { get; set; } = string.Empty;
        public DateTimeOffset? LastLoginAtUtc { get; set; }
        public string PasswordHash { get; set; } = string.Empty;
        public bool PasswordResetRequired { get; set; }
        public string DiscourseExternalId { get; set; } = string.Empty;
        public string DiscourseUsername { get; set; } = string.Empty;
        public string DiscourseEmail { get; set; } = string.Empty;
        public string OAuth2Subject { get; set; } = string.Empty;
        public string OAuth2Username { get; set; } = string.Empty;
        public string OAuth2DisplayName { get; set; } = string.Empty;
        public string OAuth2Email { get; set; } = string.Empty;
    }

    private sealed class ServerAuthSessionRecord
    {
        public string PlayerUid { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; set; }
    }

    private static class PasswordHasher
    {
        private const string Prefix = "pbkdf2-sha256";
        private const int Iterations = 120000;

        public static string Hash(string password)
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var derived = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                Iterations,
                HashAlgorithmName.SHA256,
                32);
            return string.Join(
                '$',
                Prefix,
                Iterations.ToString(),
                Convert.ToBase64String(salt),
                Convert.ToBase64String(derived));
        }

        public static bool Verify(string password, string encodedHash)
        {
            var parts = encodedHash.Split('$');
            if (parts.Length != 4 || !parts[0].Equals(Prefix, StringComparison.Ordinal))
                return false;
            if (!int.TryParse(parts[1], out var iterations))
                return false;

            try
            {
                var salt = Convert.FromBase64String(parts[2]);
                var expected = Convert.FromBase64String(parts[3]);
                var derived = Rfc2898DeriveBytes.Pbkdf2(
                    password,
                    salt,
                    iterations,
                    HashAlgorithmName.SHA256,
                    expected.Length);
                return CryptographicOperations.FixedTimeEquals(derived, expected);
            }
            catch
            {
                return false;
            }
        }
    }

    private static class DiscourseConnect
    {
        public static string BuildProviderUrl(string baseUrl, string secret, string callbackUrl, string nonce)
        {
            var rawPayload = "nonce=" + Uri.EscapeDataString(nonce) + "&return_sso_url=" + Uri.EscapeDataString(callbackUrl);
            var sso = Convert.ToBase64String(Encoding.UTF8.GetBytes(rawPayload));
            var sig = ComputeSignature(sso, secret);
            return $"{baseUrl.TrimEnd('/')}/session/sso_provider?sso={Uri.EscapeDataString(sso)}&sig={sig}";
        }

        public static bool VerifySignature(string sso, string sig, string secret)
        {
            if (string.IsNullOrWhiteSpace(sso) || string.IsNullOrWhiteSpace(sig) || string.IsNullOrWhiteSpace(secret))
                return false;

            try
            {
                var expected = Encoding.ASCII.GetBytes(ComputeSignature(sso, secret));
                var given = Encoding.ASCII.GetBytes(sig.Trim());
                return given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected);
            }
            catch
            {
                return false;
            }
        }

        public static Dictionary<string, string> DecodePayload(string sso)
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(sso));
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var segment in decoded.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var index = segment.IndexOf('=');
                var key = index < 0 ? segment : segment[..index];
                var value = index < 0 ? string.Empty : segment[(index + 1)..];
                result[Uri.UnescapeDataString(key.Replace('+', ' '))] =
                    Uri.UnescapeDataString(value.Replace('+', ' '));
            }

            return result;
        }

        private static string ComputeSignature(string value, string secret)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        }
    }
}
