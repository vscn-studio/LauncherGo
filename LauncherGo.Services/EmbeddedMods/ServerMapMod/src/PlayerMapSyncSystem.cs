using System.Security.Cryptography;
using System.Text.Json;
using ServerMap.Client;
using ServerMap.Network;
using ServerMap.Web;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace ServerMap;

public sealed class PlayerMapSyncSystem : ModSystem
{
    private const string Channel = "servermap-player-data";
    private ICoreServerAPI? server;
    private ICoreClientAPI? client;
    private IServerNetworkChannel? serverChannel;
    private IClientNetworkChannel? clientChannel;
    private ClientHiddenMap? hiddenMap;
    private IDisposable? headCapture;
    private ServerAvatarRequestPacket? pendingCapture;
    private long captureDeadline;
    private readonly HashSet<string> readyPlayers = new();
    private readonly HashSet<string> avatarChecked = new();
    private readonly Dictionary<string, string> regionVersions = new();
    private readonly Dictionary<string, long> helloAt = new();
    private Queue<ClientAvatarChunkPacket>? outgoing;
    private readonly CancellationTokenSource stop = new();
    private long tick;
    private bool connected, capturing, receivedRegions;
    private long nextCapture, nextHello;
    public override void StartServerSide(ICoreServerAPI api)
    {
        server = api;
        serverChannel = api.Network.RegisterChannel(Channel).RegisterMessageType<ClientMapReadyPacket>().RegisterMessageType<ServerHiddenMapPacket>()
            .RegisterMessageType<ServerAvatarRequestPacket>().RegisterMessageType<ClientAvatarChunkPacket>()
            .SetMessageHandler<ClientMapReadyPacket>((player, _) =>
            {
                var now = Environment.TickCount64;
                if (helloAt.GetValueOrDefault(player.PlayerUID) > now) return;
                helloAt[player.PlayerUID] = now + 5000; readyPlayers.Add(player.PlayerUID); regionVersions.Remove(player.PlayerUID); avatarChecked.Remove(player.PlayerUID);
                api.Logger.Notification("ServerMap player-data client connected: {0}.", player.PlayerUID);
                SyncPlayer(player);
            })
            .SetMessageHandler<ClientAvatarChunkPacket>((player, packet) =>
            {
                var store = api.ModLoader.GetModSystem<ServerMapModSystem>()?.WebServer?.ClientAvatars;
                if (!string.IsNullOrEmpty(packet.Error)) store?.ReportFailure(player.PlayerUID, packet.Token, packet.Error, Environment.TickCount64);
                else store?.Receive(player.PlayerUID, packet.Token, packet.Index, packet.Total, packet.Data, Environment.TickCount64);
            });
        tick = api.Event.RegisterGameTickListener(_ => SyncPlayers(), 3000);
    }
    private void SyncPlayers()
    {
        if (server == null || stop.IsCancellationRequested) return;
        var players = server.World.AllOnlinePlayers.OfType<IServerPlayer>().ToArray(); var live = players.Select(p => p.PlayerUID).ToHashSet();
        foreach (var uid in readyPlayers.Where(uid => !live.Contains(uid)).ToArray())
        {
            readyPlayers.Remove(uid); regionVersions.Remove(uid); helloAt.Remove(uid); avatarChecked.Remove(uid);
            server.ModLoader.GetModSystem<ServerMapModSystem>()?.WebServer?.ClientAvatars?.ForgetConnection(uid);
        }
        foreach (var player in players) if (readyPlayers.Contains(player.PlayerUID)) SyncPlayer(player);
    }
    private void SyncPlayer(IServerPlayer player)
    {
        var web = server?.ModLoader.GetModSystem<ServerMapModSystem>()?.WebServer;
        if (web == null || serverChannel == null) return;
        var bounds = web.HiddenRegions.Where(r => r.HideInGame).SelectMany(r => new[] { r.MinX, r.MinZ, r.MaxX, r.MaxZ }).ToArray();
        var version = JsonSerializer.Serialize(bounds);
        if (regionVersions.GetValueOrDefault(player.PlayerUID) != version)
        {
            serverChannel.SendPacket(new ServerHiddenMapPacket { Bounds = bounds }, player); regionVersions[player.PlayerUID] = version;
        }
        var appearance = Appearance(player); if (appearance == null) return;
        // Show the disk cache immediately, but refresh once per connection to pick
        // up changed client texture packs even when the selected skin codes match.
        var token = web.ClientAvatars?.Request(player.PlayerUID, appearance, Environment.TickCount64, !avatarChecked.Contains(player.PlayerUID));
        if (token != null) { avatarChecked.Add(player.PlayerUID); serverChannel.SendPacket(new ServerAvatarRequestPacket { Token = token, Appearance = appearance }, player); }
    }
    public static string? Appearance(IPlayer player)
    {
        var parts = player.Entity?.WatchedAttributes.GetTreeAttribute("skinConfig")?.GetTreeAttribute("appliedParts");
        if (parts == null) return null;
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        foreach (var part in parts.OrderBy(p => p.Key, StringComparer.Ordinal)) { writer.Write(part.Key); part.Value.ToBytes(writer); }
        var inventory = player.InventoryManager?.GetOwnInventory("character");
        if (inventory != null) foreach (var slot in inventory) writer.Write(slot.Itemstack?.Collectible?.Code?.ToString() ?? "");
        writer.Flush(); return ClientAvatarStore.AppearanceKey(player.PlayerUID, stream.ToArray());
    }
    public override void StartClientSide(ICoreClientAPI api)
    {
        client = api;
        clientChannel = api.Network.RegisterChannel(Channel).RegisterMessageType<ClientMapReadyPacket>().RegisterMessageType<ServerHiddenMapPacket>()
            .RegisterMessageType<ServerAvatarRequestPacket>().RegisterMessageType<ClientAvatarChunkPacket>()
            .SetMessageHandler<ServerHiddenMapPacket>(packet => api.Event.EnqueueMainThreadTask(() => { if (!stop.IsCancellationRequested) { hiddenMap?.Apply(packet); receivedRegions = true; } }, "servermap-hidden-sync"))
            .SetMessageHandler<ServerAvatarRequestPacket>(packet => api.Event.EnqueueMainThreadTask(() => QueueCapture(packet), "servermap-avatar-request"));
        hiddenMap = new ClientHiddenMap(api);
        headCapture = ClientHeadCapture.Start(api);
        tick = api.Event.RegisterGameTickListener(_ => ClientTick(), 200);
    }
    private void ClientTick()
    {
        if (stop.IsCancellationRequested) return;
        if (clientChannel is not { Connected: true } || client?.World.Player?.Entity == null)
        { connected = false; receivedRegions = false; outgoing = null; pendingCapture = null; hiddenMap?.Clear(); return; }
        if (!connected) { connected = true; nextHello = 0; }
        if (!receivedRegions && Environment.TickCount64 >= nextHello) { clientChannel.SendPacket(new ClientMapReadyPacket()); nextHello = Environment.TickCount64 + 6000; }
        if (pendingCapture != null)
        {
            if (Environment.TickCount64 > captureDeadline) { ReportCaptureFailure(pendingCapture, "model-timeout"); pendingCapture = null; client.Logger.Warning("ServerMap avatar request expired while waiting for the native head mesh. Renderer={0}, shapeFresh={1}.", client.World.Player.Entity.Properties.Client.Renderer?.GetType().Name, client.World.Player.Entity.ShapeFresh); }
            else if (ClientHeadCapture.Ready(client)) { var request = pendingCapture; pendingCapture = null; Capture(request); }
        }
        for (var i = 0; i < 2 && outgoing is { Count: > 0 }; i++) clientChannel.SendPacket(outgoing.Dequeue());
    }
    private void QueueCapture(ServerAvatarRequestPacket request)
    {
        if (client == null || stop.IsCancellationRequested || capturing || pendingCapture != null || Environment.TickCount64 < nextCapture || request.Token.Length != 32 || request.Appearance.Length != 64) return;
        pendingCapture = request; captureDeadline = Environment.TickCount64 + 30_000;
        client.Logger.Notification("ServerMap avatar request received; native head mesh ready={0}.", ClientHeadCapture.Ready(client));
        if (!ClientHeadCapture.Ready(client)) client.World.Player?.Entity?.MarkShapeModified();
    }
    private void Capture(ServerAvatarRequestPacket request)
    {
        if (client == null || clientChannel is not { Connected: true } || capturing || stop.IsCancellationRequested || Environment.TickCount64 < nextCapture || request.Token.Length != 32 || request.Appearance.Length != 64) return;
        capturing = true; nextCapture = Environment.TickCount64 + 60_000;
        try
        {
            var scene = ClientHeadCapture.Capture(client); var player = client.World.Player; var localAppearance = Appearance(player);
            _ = Task.Run(() => scene.Pack(), stop.Token).ContinueWith(task => client.Event.EnqueueMainThreadTask(() =>
            {
                capturing = false;
                if (task.IsCanceled || task.IsFaulted || stop.IsCancellationRequested) { if (task.Exception != null) { client.Logger.Warning("ServerMap avatar packing failed: {0}", task.Exception.GetBaseException().Message); ReportCaptureFailure(request, "packing-failed"); } return; }
                if (!connected || client.World.Player != player || Appearance(player) != localAppearance) { ReportCaptureFailure(request, "appearance-changed"); return; }
                var bytes = task.Result; var chunks = (bytes.Length + ClientAvatarStore.ChunkSize - 1) / ClientAvatarStore.ChunkSize;
                outgoing = new Queue<ClientAvatarChunkPacket>(Enumerable.Range(0, chunks).Select(i => new ClientAvatarChunkPacket { Token = request.Token, Index = i, Total = chunks, Data = bytes.Skip(i * ClientAvatarStore.ChunkSize).Take(ClientAvatarStore.ChunkSize).ToArray() }));
                client.Logger.Notification("ServerMap head model and cropped textures queued: {0} bytes, {1} chunks.", bytes.Length, chunks);
            }, "servermap-avatar-transfer"), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
        catch (Exception ex) { capturing = false; client.Logger.Warning("ServerMap avatar capture failed: {0}", ex.Message); ReportCaptureFailure(request, "capture-failed"); }
    }
    private void ReportCaptureFailure(ServerAvatarRequestPacket request, string error)
    {
        if (!stop.IsCancellationRequested && clientChannel is { Connected: true })
            clientChannel.SendPacket(new ClientAvatarChunkPacket { Token = request.Token, Error = error });
    }
    public override void Dispose()
    {
        stop.Cancel(); if (tick != 0) { server?.Event.UnregisterGameTickListener(tick); client?.Event.UnregisterGameTickListener(tick); }
        hiddenMap?.Dispose(); headCapture?.Dispose(); outgoing = null; pendingCapture = null;
    }
}
