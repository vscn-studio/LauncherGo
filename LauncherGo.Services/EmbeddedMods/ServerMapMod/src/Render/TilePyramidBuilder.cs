using System.Buffers.Binary;
using System.IO.Compression;
using ServerMap.Util;
using ServerMap.World;

namespace ServerMap.Render;

/// <summary>Builds the persistent CRS.Simple tile pyramid from ServerMap's
/// 512-pixel base regions. A parent is written atomically only after reading
/// its four children, so browsers never receive a partial PNG.</summary>
public sealed class TilePyramidBuilder
{
    public const int TileSize = 512;
    public const int MaxZoom = 12;
    private readonly string root;

    public TilePyramidBuilder(string root) => this.root = root;

    public IEnumerable<(int Zoom, int X, int Z)> BuildParents(string renderer, ChunkKey baseRegion)
    {
        var x = baseRegion.X;
        var z = baseRegion.Z;
        for (var zoom = 1; zoom <= MaxZoom; zoom++)
        {
            x = FloorDiv(x, 2);
            z = FloorDiv(z, 2);
            BuildParent(renderer, zoom, x, z);
            yield return (zoom, x, z);
        }
    }

    /// <summary>Backfills a pyramid after an upgrade. Old cache directories
    /// only contain zoom zero tiles, so doing this in the background prevents
    /// a zoom-out request from resolving to a transparent placeholder.</summary>
    public void BuildAllParents(string renderer, IEnumerable<(int X, int Z)> regions)
    {
        var current = new HashSet<(int X, int Z)>(regions);
        for (var zoom = 1; zoom <= MaxZoom && current.Count > 0; zoom++)
        {
            var parents = new HashSet<(int X, int Z)>();
            foreach (var tile in current) parents.Add((FloorDiv(tile.X, 2), FloorDiv(tile.Z, 2)));
            foreach (var parent in parents) BuildParent(renderer, zoom, parent.X, parent.Z);
            current = parents;
        }
    }

    private void BuildParent(string renderer, int zoom, int x, int z)
    {
        var children = new byte[]?[]
        {
            Read(renderer, zoom - 1, x * 2, z * 2), Read(renderer, zoom - 1, x * 2 + 1, z * 2),
            Read(renderer, zoom - 1, x * 2, z * 2 + 1), Read(renderer, zoom - 1, x * 2 + 1, z * 2 + 1)
        };
        if (children.All(child => child == null)) return;

        var output = new byte[TileSize * TileSize * 4];
        for (var py = 0; py < TileSize; py++) for (var px = 0; px < TileSize; px++)
        {
            var childX = px >> 8;
            var childY = py >> 8;
            var child = children[childY * 2 + childX];
            if (child == null) continue;
            var sourceX = (px & 255) * 2;
            var sourceY = (py & 255) * 2;
            var red = 0; var green = 0; var blue = 0; var alpha = 0; var count = 0;
            for (var oy = 0; oy < 2; oy++) for (var ox = 0; ox < 2; ox++)
            {
                var source = ((sourceY + oy) * TileSize + sourceX + ox) * 4;
                if (child[source + 3] == 0) continue;
                red += child[source]; green += child[source + 1]; blue += child[source + 2]; alpha += child[source + 3]; count++;
            }
            if (count == 0) continue;
            var target = (py * TileSize + px) * 4;
            output[target] = (byte)(red / count); output[target + 1] = (byte)(green / count);
            output[target + 2] = (byte)(blue / count); output[target + 3] = (byte)(alpha / count);
        }
        var path = Path.Combine(root, "2d", renderer, zoom.ToString(), $"{x}_{z}.png");
        AtomicFile.Replace(path, temp => File.WriteAllBytes(temp, PngEncoder.Encode(TileSize, TileSize, output)));
    }

    private byte[]? Read(string renderer, int zoom, int x, int z)
    {
        var path = Path.Combine(root, "2d", renderer, zoom.ToString(), $"{x}_{z}.png");
        if (!File.Exists(path)) return null;
        try { return Decode(File.ReadAllBytes(path)); }
        catch { return null; }
    }

    // ServerMap writes filter type 0 PNGs. Keeping this reader narrow avoids a
    // graphics dependency in a server-side mod and makes parent construction deterministic.
    private static byte[] Decode(byte[] png)
    {
        if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw new InvalidDataException();
        var offset = 8; var width = 0; var height = 0;
        using var compressed = new MemoryStream();
        while (offset + 12 <= png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            if (length < 0 || offset + 12 + length > png.Length) throw new InvalidDataException();
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.AsSpan(offset + 8, length);
            if (type == "IHDR") { width = BinaryPrimitives.ReadInt32BigEndian(data); height = BinaryPrimitives.ReadInt32BigEndian(data[4..]); }
            if (type == "IDAT") compressed.Write(data);
            offset += length + 12;
            if (type == "IEND") break;
        }
        if (width != TileSize || height != TileSize) throw new InvalidDataException();
        compressed.Position = 0;
        var raw = new byte[height * (width * 4 + 1)];
        using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress)) zlib.ReadExactly(raw);
        var output = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var row = y * (width * 4 + 1);
            if (raw[row] != 0) throw new InvalidDataException("Unsupported PNG filter");
            raw.AsSpan(row + 1, width * 4).CopyTo(output.AsSpan(y * width * 4));
        }
        return output;
    }

    public static int FloorDiv(int value, int divisor) => value >= 0 ? value / divisor : (value - divisor + 1) / divisor;
}
