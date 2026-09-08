using System.IO.Compression;
using System.Buffers.Binary;

namespace ServerMap.Render;

internal static class PngEncoder
{
    public static byte[] Decode(byte[] png)
    {
        if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw new InvalidDataException();
        var offset = 8; var width = 0; var height = 0;
        using var compressed = new MemoryStream();
        while (offset + 12 <= png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            if (length < 0 || (long)offset + 12 + length > png.Length) throw new InvalidDataException();
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4); var data = png.AsSpan(offset + 8, length);
            if (type == "IHDR") { if (length != 13 || data[8] != 8 || data[9] != 6) throw new InvalidDataException(); width = BinaryPrimitives.ReadInt32BigEndian(data); height = BinaryPrimitives.ReadInt32BigEndian(data[4..]); }
            if (type == "IDAT") compressed.Write(data);
            offset += length + 12; if (type == "IEND") break;
        }
        if (width != 512 || height != 512) throw new InvalidDataException();
        compressed.Position = 0; var raw = new byte[height * (width * 4 + 1)];
        using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress)) zlib.ReadExactly(raw);
        var output = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var row = y * (width * 4 + 1); if (raw[row] != 0) throw new InvalidDataException("Unsupported PNG filter");
            raw.AsSpan(row + 1, width * 4).CopyTo(output.AsSpan(y * width * 4));
        }
        return output;
    }
    public static byte[] Encode(int width, int height, ReadOnlySpan<byte> rgba)
    {
        using var output = new MemoryStream(); output.Write(new byte[] { 137,80,78,71,13,10,26,10 });
        WriteChunk(output, "IHDR", MakeHeader(width, height));
        using var raw = new MemoryStream();
        for (var y = 0; y < height; y++) { raw.WriteByte(0); raw.Write(rgba.Slice(y * width * 4, width * 4)); }
        using var compressed = new MemoryStream(); raw.Position = 0; using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, true)) raw.CopyTo(z);
        WriteChunk(output, "IDAT", compressed.ToArray()); WriteChunk(output, "IEND", Array.Empty<byte>()); return output.ToArray();
    }
    private static byte[] MakeHeader(int width, int height) { var b = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(b, width); BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(4), height); b[8]=8; b[9]=6; return b; }
    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, data.Length); s.Write(length);
        var name = System.Text.Encoding.ASCII.GetBytes(type); s.Write(name); s.Write(data);
        var crc = new Crc32(); crc.Append(name); crc.Append(data); Span<byte> value = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(value, crc.GetCurrentHashAsUInt32()); s.Write(value);
    }
    private sealed class Crc32 { private uint value = 0xffffffff; public void Append(ReadOnlySpan<byte> bytes) { foreach (var b in bytes) { value ^= b; for (var i=0;i<8;i++) value = (value >> 1) ^ (0xedb88320u & (uint)-(int)(value & 1)); } } public uint GetCurrentHashAsUInt32() => ~value; }
}
