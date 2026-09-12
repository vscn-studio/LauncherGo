using HarmonyLib;
using ServerMap.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ServerMap.Client;

/// <summary>Observe successful native generation and synchronize the active world's complete historical map cache.</summary>
public sealed class ClientNativeExploration : IDisposable
{
    private static volatile ClientNativeExploration? current;
    private readonly ICoreClientAPI api;
    private readonly Harmony harmony = new("servermap-native-exploration-client");
    private readonly GeneratedMapChunks generated = new();
    private bool warned;
    private volatile bool disposed;
    private MapHistoryUpload? history;
    private string historySession = "", historyWorld = "";
    private long nextHistoryAttempt;
    private static readonly System.Reflection.FieldInfo LayerApi = AccessTools.Field(typeof(MapLayer), "api");
    private static readonly System.Reflection.FieldInfo LayerDatabase = AccessTools.Field(typeof(ChunkMapLayer), "mapdb");
    private static readonly System.Reflection.FieldInfo DatabaseFile = AccessTools.Field(typeof(SQLiteDBConnection), "databaseFileName");

    public bool Connected => generated.Connected;
    public string ClientSession { get; private set; } = Guid.NewGuid().ToString("N");

    public ClientNativeExploration(ICoreClientAPI api)
    {
        this.api = api;
        try
        {
            harmony.Patch(AccessTools.Method(typeof(ChunkMapLayer), nameof(ChunkMapLayer.GenerateChunkImage)),
                postfix: new HarmonyMethod(typeof(ClientNativeExploration), nameof(AfterGenerate)));
            current = this;
        }
        catch { harmony.UnpatchAll(harmony.Id); throw; }
    }

    private static void AfterGenerate(FastVec2i chunkPos, int[]? __result, ICoreAPI ___api)
    {
        var owner = current;
        // GenerateChunkImage returns null until the entire column is LoadedFromServer.
        // The API identity also excludes another world/layer instance during shutdown.
        if (owner == null || owner.disposed || !ReferenceEquals(___api, owner.api) || __result is not { Length: 1024 }
            || chunkPos.X < 0 || chunkPos.Y < 0) return;
        if (!owner.generated.Record(MapExplorationProtocol.Cell(chunkPos.X, chunkPos.Y)) && !owner.warned)
        {
            owner.warned = true;
            owner.api.Logger.Warning("ServerMap native exploration queue is full; waiting for server acknowledgements. Unsent pieces will need a native redraw.");
        }
    }

    public void Receive(ServerMapExplorationAckPacket packet)
    {
        if (disposed || packet.ClientSession != ClientSession) return;
        if (packet.Sequence == 0)
        {
            // A fresh client/world accepts its hello once; stale hello packets cannot replace an active session.
            if (!generated.Connected)
            {
                generated.Connect(packet.Session);
                if (generated.Connected && packet.HistoryProtocol == MapHistoryProtocol.Version
                    && MapHistoryProtocol.ValidWorld(packet.WorldId) && packet.WorldId == api.World.SavegameIdentifier)
                { historySession = packet.Session; historyWorld = packet.WorldId; }
                else if (generated.Connected)
                    api.Logger.Warning("ServerMap full map history sync is unavailable: update both client and server, and verify the world identity. Native incremental sync remains active.");
            }
        }
        else generated.Acknowledge(new(packet.Session, packet.Sequence, packet.Accepted));
    }

    public void Send(IClientNetworkChannel channel, long now)
    {
        if (disposed) return;
        if (generated.Next(now) is { } batch)
            channel.SendPacket(new ClientMapExplorationPacket { Session = batch.Session, Sequence = batch.Sequence, Cells = batch.Cells });
        if (historySession.Length == 0 || api.World.SavegameIdentifier != historyWorld || api.World.Player?.Entity?.Pos.Dimension != 0) return;
        if (history?.Failure is { } error)
        {
            api.Logger.Warning("ServerMap historical map scan failed; the previous exploration is unchanged. Retrying: {0}", error.Message);
            history.Dispose(); history = null; nextHistoryAttempt = now + MapHistoryProtocol.RestartIntervalMs;
        }
        if (history == null && now >= nextHistoryAttempt)
        {
            nextHistoryAttempt = now + 1000;
            try
            {
                // Read the already-open native layer's exact database, never enumerate Maps/*.db or guess a server file.
                var manager = api.ModLoader.GetModSystem<WorldMapManager>();
                var layer = manager?.MapLayers.OfType<ChunkMapLayer>().FirstOrDefault(l => ReferenceEquals(LayerApi.GetValue(l), api));
                if (layer != null && LayerDatabase.GetValue(layer) is MapDB db && DatabaseFile.GetValue(db) is string file
                    && Path.GetFileName(file) == historyWorld + ".db" && File.Exists(file))
                {
                    var chunksX = api.World.BlockAccessor.MapSizeX / MapExplorationProtocol.ChunkSize;
                    var chunksZ = api.World.BlockAccessor.MapSizeZ / MapExplorationProtocol.ChunkSize;
                    if (chunksX <= 0 || chunksZ <= 0) return;
                    history = new(historySession, historyWorld, cancellation => NativeMapCache.Read(file, chunksX, chunksZ, cancellation));
                    api.Logger.Notification("ServerMap historical map synchronization started for the current world.");
                }
            }
            catch (Exception ex)
            {
                nextHistoryAttempt = now + MapHistoryProtocol.RestartIntervalMs;
                api.Logger.Warning("ServerMap cannot read the active native map cache; previous exploration is unchanged: {0}", ex.Message);
            }
        }
        if (history?.Next(now) is { } page)
            channel.SendPacket(new ClientMapHistoryPacket { Session = page.Session, WorldId = page.World, Snapshot = page.Snapshot,
                Sequence = page.Sequence, Cells = page.Cells, Complete = page.Complete });
    }

    public void ReceiveHistory(ServerMapHistoryAckPacket packet)
    {
        if (disposed || packet.ClientSession != ClientSession || history == null) return;
        var wasComplete = history.Complete;
        history.Acknowledge(new(packet.Session, packet.Snapshot, packet.Sequence, packet.Complete));
        if (!wasComplete && history.Complete)
            api.Logger.Notification("ServerMap historical map synchronization completed: {0} cached chunks; exploration replaced successfully.", history.UploadedChunks);
    }

    public void Reset()
    {
        history?.Dispose(); history = null; historySession = historyWorld = ""; nextHistoryAttempt = 0;
        generated.Reset(); ClientSession = Guid.NewGuid().ToString("N"); warned = false;
    }
    public void Dispose()
    {
        disposed = true;
        if (current == this) current = null;
        harmony.UnpatchAll(harmony.Id); Reset();
    }
}
