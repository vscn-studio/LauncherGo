using ServerMap.Client;
using ServerMap.Network;
using ServerMap.Render;
using ServerMap.Web;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace ServerMap;

public sealed class MountMapSyncSystem : ModSystem
{
    private const string Channel = "servermap-mounted-model-v2";
    private ICoreServerAPI? server; private ICoreClientAPI? client;
    private IServerNetworkChannel? serverChannel; private IClientNetworkChannel? clientChannel;
    private readonly Dictionary<string, long> ready = new();
    private ClientMountCapture? capture; private ServerMountRequestPacket? request;
    private Queue<ClientMountChunkPacket>? outgoing; private bool rendering;
    private long tick, nextHello, deadline; private readonly CancellationTokenSource stop = new();
    public static Entity? MountedEntity(IPlayer player)
    {
        var seat = player.Entity?.MountedOn;
        var entity = seat?.MountSupplier?.OnEntity ?? seat?.Entity;
        // Beds and decorative seats are not moving mounts. Include passenger seats
        // when the same supplier has a driver seat (boats and modded airships).
        return entity?.Alive == true && (seat!.CanControl || seat.MountSupplier?.Seats?.Any(s => s.CanControl) == true) ? entity : null;
    }
    public override void StartServerSide(ICoreServerAPI api)
    {
        server = api;
        serverChannel = api.Network.RegisterChannel(Channel).RegisterMessageType<ClientMountReadyPacket>().RegisterMessageType<ServerMountRequestPacket>().RegisterMessageType<ClientMountChunkPacket>()
            .SetMessageHandler<ClientMountReadyPacket>((p, _) => ready[p.PlayerUID] = Environment.TickCount64)
            .SetMessageHandler<ClientMountChunkPacket>((p, packet) =>
            {
                var store = api.ModLoader.GetModSystem<ServerMapModSystem>()?.WebServer?.Mounts;
                if (MountedEntity(p)?.EntityId != packet.EntityId) { store?.Fail(p.PlayerUID, packet.EntityId, packet.Token); return; }
                if (!string.IsNullOrEmpty(packet.Error)) store?.Fail(p.PlayerUID, packet.EntityId, packet.Token);
                else store?.Receive(p.PlayerUID, packet.EntityId, packet.Token, packet.Index, packet.Total, packet.Data, packet.WorldSize, packet.CenterX, packet.CenterZ, Environment.TickCount64, packet.Footprint);
            });
        tick = api.Event.RegisterGameTickListener(_ => ServerTick(), 1000);
    }
    private void ServerTick()
    {
        if (server == null || serverChannel == null || stop.IsCancellationRequested) return;
        var store = server.ModLoader.GetModSystem<ServerMapModSystem>()?.WebServer?.Mounts; if (store == null) return;
        var players = server.World.AllOnlinePlayers.OfType<IServerPlayer>().ToArray(); var now = Environment.TickCount64;
        foreach (var uid in ready.Keys.Where(uid => !players.Any(p => p.PlayerUID == uid)).ToArray()) ready.Remove(uid);
        var occupied = players.Select(p => (Player: p, Entity: MountedEntity(p))).Where(p => p.Entity != null).GroupBy(p => p.Entity!.EntityId).ToArray();
        store.Replace(occupied.Select(group =>
        {
            var entity = group.First().Entity!; var pos = entity.Pos;
            return new MountSnapshotStore.Mount(entity.EntityId, entity.Code.ToString(), entity.GetName(), pos.X, pos.Y, pos.Z, pos.Yaw,
                group.Select(p => new MountSnapshotStore.Rider(p.Player.PlayerUID, p.Player.PlayerName, p.Player.Entity.Pos.X, p.Player.Entity.Pos.Z)).ToArray());
        }), now);
        foreach (var group in occupied)
        {
            var rider = group.Select(p => p.Player).FirstOrDefault(p => ready.ContainsKey(p.PlayerUID)); if (rider == null) continue;
            var token = store.Request(rider.PlayerUID, group.Key, now);
            if (token != null) serverChannel.SendPacket(new ServerMountRequestPacket { Token = token, EntityId = group.Key }, rider);
        }
    }
    public override void StartClientSide(ICoreClientAPI api)
    {
        client = api;
        clientChannel = api.Network.RegisterChannel(Channel).RegisterMessageType<ClientMountReadyPacket>().RegisterMessageType<ServerMountRequestPacket>().RegisterMessageType<ClientMountChunkPacket>()
            .SetMessageHandler<ServerMountRequestPacket>(packet => api.Event.EnqueueMainThreadTask(() =>
            {
                if (stop.IsCancellationRequested || request != null || rendering || packet.Token?.Length != 32 || MountedEntity(api.World.Player) is not { } entity || entity.EntityId != packet.EntityId) return;
                request = packet; deadline = Environment.TickCount64 + 30_000; capture?.Request(entity);
            }, "servermap-mount-request"));
        capture = new ClientMountCapture(api); tick = api.Event.RegisterGameTickListener(_ => ClientTick(), 200);
    }
    private void ClientTick()
    {
        if (stop.IsCancellationRequested || client == null) return;
        if (clientChannel is not { Connected: true } || client.World.Player?.Entity == null) { request = null; outgoing = null; capture?.Clear(); nextHello = 0; return; }
        if (Environment.TickCount64 >= nextHello) { clientChannel.SendPacket(new ClientMountReadyPacket()); nextHello = Environment.TickCount64 + 15_000; }
        var mount = MountedEntity(client.World.Player);
        if (request is { } pending)
        {
            if (mount?.EntityId != pending.EntityId || Environment.TickCount64 > deadline) { Fail(pending); request = null; capture?.Clear(); }
            else if (!rendering && capture?.Ready(mount) == true)
            {
                request = null; rendering = true;
                try
                {
                    var scene = capture.Capture(mount); capture.Clear();
                    _ = Task.Run(() => TopDownMountRenderer.Render(scene, stop.Token), stop.Token).ContinueWith(task => client.Event.EnqueueMainThreadTask(() =>
                    {
                        rendering = false;
                        if (stop.IsCancellationRequested || clientChannel is not { Connected: true }) return;
                        if (!task.IsCompletedSuccessfully) { if (task.Exception != null) client.Logger.Warning("ServerMap mount render failed: {0}", task.Exception.GetBaseException().Message); Fail(pending); return; }
                        if (MountedEntity(client.World.Player)?.EntityId != pending.EntityId) { Fail(pending); return; }
                        var image = task.Result; if (image.Png.Length > MountSnapshotStore.MaxBytes) { Fail(pending); return; }
                        var count = (image.Png.Length + MountSnapshotStore.ChunkSize - 1) / MountSnapshotStore.ChunkSize;
                        outgoing = new Queue<ClientMountChunkPacket>(Enumerable.Range(0, count).Select(i => new ClientMountChunkPacket { Token = pending.Token, EntityId = pending.EntityId, Index = i, Total = count, Data = image.Png.Skip(i * MountSnapshotStore.ChunkSize).Take(MountSnapshotStore.ChunkSize).ToArray(), WorldSize = image.WorldSize, CenterX = image.CenterX, CenterZ = image.CenterZ, Footprint = image.Footprint }));
                    }, "servermap-mount-rendered"), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                }
                catch (Exception ex) { rendering = false; capture?.Clear(); Fail(pending); client.Logger.Warning("ServerMap mount capture failed: {0}", ex.Message); }
            }
        }
        for (var i = 0; i < 2 && outgoing is { Count: > 0 }; i++)
        {
            if (outgoing.Peek().EntityId != mount?.EntityId) { outgoing = null; break; }
            clientChannel.SendPacket(outgoing.Dequeue());
        }
    }
    private void Fail(ServerMountRequestPacket packet) { if (!stop.IsCancellationRequested && clientChannel is { Connected: true }) clientChannel.SendPacket(new ClientMountChunkPacket { Token = packet.Token, EntityId = packet.EntityId, Error = "capture-unavailable" }); }
    public override void Dispose() { stop.Cancel(); if (tick != 0) { server?.Event.UnregisterGameTickListener(tick); client?.Event.UnregisterGameTickListener(tick); } capture?.Dispose(); request = null; outgoing = null; }
}
