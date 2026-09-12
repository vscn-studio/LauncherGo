using HarmonyLib;
using ServerMap.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ServerMap.Client;

/// <summary>Observe successful native generation, not visible map bounds, chunk requests or cached DB images.</summary>
public sealed class ClientNativeExploration : IDisposable
{
    private static volatile ClientNativeExploration? current;
    private readonly ICoreClientAPI api;
    private readonly Harmony harmony = new("servermap-native-exploration-client");
    private readonly GeneratedMapChunks generated = new();
    private bool warned;
    private volatile bool disposed;

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
            if (!generated.Connected) generated.Connect(packet.Session);
        }
        else generated.Acknowledge(new(packet.Session, packet.Sequence, packet.Accepted));
    }

    public void Send(IClientNetworkChannel channel, long now)
    {
        if (disposed || generated.Next(now) is not { } batch) return;
        channel.SendPacket(new ClientMapExplorationPacket { Session = batch.Session, Sequence = batch.Sequence, Cells = batch.Cells });
    }

    public void Reset() { generated.Reset(); ClientSession = Guid.NewGuid().ToString("N"); warned = false; }
    public void Dispose()
    {
        disposed = true;
        if (current == this) current = null;
        harmony.UnpatchAll(harmony.Id); generated.Reset();
    }
}
