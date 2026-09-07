using Vintagestory.Common.Database;

namespace ServerMap.World;

public readonly record struct ChunkKey(int X, int Y, int Z)
{
    // Savegame v2 packs chunks as x:27, z:27 and y:10 bits.  This must stay
    // byte-for-byte compatible with ChunkPos, otherwise every DB lookup hits
    // a different column and produces an apparently empty map.
    public long ToIndex() => unchecked((long)ChunkPos.ToChunkIndex(X, Y, Z));

    public static ChunkKey From(long index)
    {
        var position = ChunkPos.FromChunkIndex_saveGamev2(unchecked((ulong)index));
        return new ChunkKey(position.X, position.Y, position.Z);
    }
}
