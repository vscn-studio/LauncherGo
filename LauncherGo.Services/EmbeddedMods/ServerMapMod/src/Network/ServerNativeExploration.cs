using HarmonyLib;
using ServerMap.Web;
using Vintagestory.API.Server;
using Vintagestory.Server;

namespace ServerMap.Network;

/// <summary>Proves per-player, complete native chunk-column delivery before accepting map generation reports.</summary>
public sealed class ServerNativeExploration : IDisposable
{
    private sealed class Connection
    {
        public RecentMapChunks Sent { get; } = new();
        public MapExplorationSession? Session { get; set; }
        public string ClientSession { get; set; } = "";
        public MapHistorySession? History { get; set; }
        public string HistoryWorld { get; set; } = "";
    }
    private static ServerNativeExploration? current;
    private readonly ICoreServerAPI api;
    private readonly ServerMain server;
    private readonly Func<ServerMapWebServer?> web;
    private readonly Harmony harmony = new("servermap-native-exploration-server");
    private readonly Dictionary<ConnectedClient, Connection> connections = new();
    private bool disposed, warned;

    public ServerNativeExploration(ICoreServerAPI api, Func<ServerMapWebServer?> web)
    {
        this.api = api; this.web = web;
        server = api.World as ServerMain ?? throw new NotSupportedException("Native exploration requires the game's chunk delivery records.");
        try
        {
            harmony.Patch(AccessTools.Method(typeof(ConnectedClient), nameof(ConnectedClient.SetChunkSent)),
                postfix: new HarmonyMethod(typeof(ServerNativeExploration), nameof(AfterSendChunk)));
            current = this;
        }
        catch { harmony.UnpatchAll(harmony.Id); throw; }
    }

    private bool IsCurrent(ConnectedClient client) => !disposed && client.Player != null
        && server.Clients.TryGetValue(client.Id, out var live) && ReferenceEquals(live, client);
    private ConnectedClient? Client(IServerPlayer player) => server.Clients.TryGetValue(player.ClientId, out var client)
        && IsCurrent(client) && ReferenceEquals(client.Player, player) ? client : null;
    private Connection State(ConnectedClient client)
    {
        if (!connections.TryGetValue(client, out var state)) connections[client] = state = new();
        return state;
    }
    private bool InWorld(long cell) => MapExplorationProtocol.InWorld(cell, server.WorldMap.ChunkMapSizeX, server.WorldMap.ChunkMapSizeZ);
    private bool FullySent(ConnectedClient client, long cell)
    {
        if (!InWorld(cell)) return false;
        var (x, z) = MapExplorationProtocol.Coordinates(cell);
        return MapExplorationProtocol.CompleteColumn(server.WorldMap.ChunkMapSizeY,
            y => client.DidSendChunk(server.WorldMap.ChunkIndex3D(x, y, z)));
    }

    private static void AfterSendChunk(ConnectedClient __instance, long index3d)
    {
        var owner = current;
        if (owner == null || !owner.IsCurrent(__instance)) return;
        try
        {
            var pos = owner.server.WorldMap.ChunkPosFromChunkIndex3D(index3d);
            if (pos.Dimension != 0 || pos.Y < 0 || pos.Y >= owner.server.WorldMap.ChunkMapSizeY) return;
            var cell = MapExplorationProtocol.Cell(pos.X, pos.Z);
            var sent = owner.State(__instance).Sent;
            // Retain proof after unloading/teleporting, so an honest delayed report is not rejected
            // solely because the player has moved. Server-generated or other players' chunks do not count.
            if (!sent.Contains(cell) && owner.FullySent(__instance, cell)) sent.Add(cell);
        }
        catch (Exception ex)
        {
            if (!owner.warned) { owner.warned = true; owner.api.Logger.Warning("ServerMap chunk delivery observation failed: {0}", ex.Message); }
        }
    }

    public ServerMapExplorationAckPacket? Begin(IServerPlayer player, int version, string clientSession, int historyProtocol = 0, string worldId = "")
    {
        if (version != MapExplorationProtocol.Version || !MapExplorationProtocol.ValidSession(clientSession) || Client(player) is not { } client) return null;
        var state = State(client);
        if (state.Session == null || state.ClientSession != clientSession)
        { state.Session = new(); state.ClientSession = clientSession; state.History = null; state.HistoryWorld = ""; }
        if (state.History == null && historyProtocol == MapHistoryProtocol.Version && MapHistoryProtocol.ValidWorld(worldId)
            && worldId == api.World.SavegameIdentifier)
        { state.History = new(state.Session.Id, worldId); state.HistoryWorld = worldId; }
        return new() { Session = state.Session.Id, ClientSession = state.ClientSession,
            HistoryProtocol = state.History != null ? MapHistoryProtocol.Version : 0, WorldId = state.HistoryWorld };
    }

    // Vintage Story dispatches server mod channel handlers on the server game thread, alongside chunk sends.
    public ServerMapExplorationAckPacket? Receive(IServerPlayer player, ClientMapExplorationPacket packet, long now)
    {
        if (Client(player) is not { } client || !connections.TryGetValue(client, out var state)
            || state.Session == null || web() is not { } map) return null;
        var receipt = state.Session.Receive(packet.Session, packet.Sequence, packet.Dimension, packet.Cells, now,
            server.WorldMap.ChunkMapSizeX, server.WorldMap.ChunkMapSizeZ,
            cell => map.HasOwnExploration(player.PlayerUID, cell) || state.Sent.Contains(cell) || FullySent(client, cell),
            cells => { map.RecordVerifiedExploration(player, cells); state.History?.ObserveNative(cells); });
        return receipt == null ? null : new() { Session = receipt.Session, Sequence = receipt.Sequence, Accepted = receipt.Accepted, ClientSession = state.ClientSession };
    }

    public ServerMapHistoryAckPacket? ReceiveHistory(IServerPlayer player, ClientMapHistoryPacket packet, long now)
    {
        if (Client(player) is not { } client || !connections.TryGetValue(client, out var state) || state.History == null
            || player.Entity?.Pos.Dimension != 0 || state.HistoryWorld != api.World.SavegameIdentifier || web() is not { } map) return null;
        // Cached maps are intentionally client-trusted: current-session chunk delivery cannot prove historical exploration.
        var receipt = state.History.Receive(new(packet.Session, packet.WorldId, packet.Snapshot, packet.Sequence, packet.Cells, packet.Complete),
            packet.Dimension, now, server.WorldMap.ChunkMapSizeX, server.WorldMap.ChunkMapSizeZ,
            cells => map.ReplaceExplorationFromMap(player, cells));
        return receipt == null ? null : new() { Session = receipt.Session, Snapshot = receipt.Snapshot, Sequence = receipt.Sequence,
            Complete = receipt.Complete, ClientSession = state.ClientSession };
    }

    public void Forget(IServerPlayer player)
    {
        foreach (var client in connections.Keys.Where(c => ReferenceEquals(c.Player, player)).ToArray()) connections.Remove(client);
    }
    public void Prune()
    {
        foreach (var client in connections.Keys.Where(c => !IsCurrent(c)).ToArray()) connections.Remove(client);
    }
    public void Dispose()
    {
        disposed = true;
        if (current == this) current = null;
        harmony.UnpatchAll(harmony.Id); connections.Clear();
    }
}
