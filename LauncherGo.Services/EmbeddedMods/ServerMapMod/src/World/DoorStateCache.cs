using System.Collections.Concurrent;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace ServerMap.World;

/// <summary>
/// Captures the live state of door block entities on the server thread.
/// Mesh rendering reads SQLite from worker threads, but a door interaction
/// marks its chunk dirty before the next asynchronous world save completes.
/// Keeping this small immutable overlay makes an explicit render reflect the
/// current door pose without accessing the live world from a worker.
/// </summary>
public sealed class DoorStateCache
{
    public readonly record struct State(float RotateYRad, bool Opened, bool InvertHandles);
    private readonly record struct Position(int X, int Y, int Z);

    private readonly ConcurrentDictionary<Position, State> states = new();

    public int CaptureLoadedChunks(IReadOnlyDictionary<long, IServerChunk> chunks)
    {
        var count = 0;
        foreach (var entry in chunks)
        {
            ChunkKey key;
            try { key = ChunkKey.From(entry.Key); }
            catch { continue; }
            count += CaptureChunk(new Vec3i(key.X, key.Y, key.Z), entry.Value);
        }
        return count;
    }

    public int CaptureChunk(Vec3i chunkPosition, IWorldChunk chunk)
    {
        // A block replacement can remove a door entity, so discard the old
        // overlay for this chunk before taking its newest entity snapshot.
        foreach (var entry in states)
        {
            if (FloorDiv(entry.Key.X, 32) == chunkPosition.X
                && (entry.Key.Y >> 5) == chunkPosition.Y
                && FloorDiv(entry.Key.Z, 32) == chunkPosition.Z)
            {
                states.TryRemove(entry.Key, out _);
            }
        }

        var captured = 0;
        foreach (var entry in chunk.BlockEntities)
        {
            var door = entry.Value.GetBehavior<BEBehaviorDoor>();
            if (door == null) continue;
            states[new Position(entry.Key.X, entry.Key.Y, entry.Key.Z)] =
                new State(door.RotateYRad, door.Opened, door.InvertHandles);
            captured++;
        }
        return captured;
    }

    public bool TryGet(int x, int y, int z, out State state) =>
        states.TryGetValue(new Position(x, y, z), out state);

    private static int FloorDiv(int value, int divisor) =>
        value >= 0 ? value / divisor : (value - divisor + 1) / divisor;
}
