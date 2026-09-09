using ProtoBuf;
using ServerMap.Web;
using Vintagestory.API.Datastructures;

namespace ServerMap.World;

internal static class SavedTranslocators
{
    // Read only the saved block-entity field, without creating entities,
    // initializing mod behaviors or decompressing terrain/light arrays.
    [ProtoContract]
    private sealed class ChunkObjects
    {
        [ProtoMember(8)] public List<byte[]> BlockEntities { get; set; } = [];
    }

    public static TranslocatorPoint[] Read(byte[] data)
    {
        using var stream = new MemoryStream(data, false);
        var result = new List<TranslocatorPoint>();
        foreach (var bytes in Serializer.Deserialize<ChunkObjects>(stream).BlockEntities)
        {
            using var entityStream = new MemoryStream(bytes, false);
            using var reader = new BinaryReader(entityStream);
            if (!reader.ReadString().Equals("StaticTranslocator", StringComparison.Ordinal)) continue;
            var tree = new TreeAttribute();
            tree.FromBytes(reader);
            if (!tree.GetBool("canTele")) continue;
            var x = tree.GetInt("teleX"); var y = tree.GetInt("teleY"); var z = tree.GetInt("teleZ");
            if (x == 0 && z == 0 || tree.GetBool("tpLocationIsOffset")) continue;
            result.Add(new(tree.GetInt("posx"), tree.GetInt("posy"), tree.GetInt("posz"), x, y, z));
        }
        return result.ToArray();
    }
}
