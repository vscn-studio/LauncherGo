using System.IO.Compression;
using System.Buffers.Binary;

namespace ServerMap.Render;

internal static class PngEncoder
{
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
