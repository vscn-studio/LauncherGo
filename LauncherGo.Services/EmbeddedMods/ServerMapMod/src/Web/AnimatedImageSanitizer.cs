using System.Buffers.Binary;
using System.Text;
using SkiaSharp;

namespace ServerMap.Web;

// Preserve animation control/image data, not arbitrary source extensions or metadata.
internal static class AnimatedImageSanitizer
{
    private static InvalidDataException Invalid() => new("poi_image_format");
    public static bool HasAnimation(byte[] bytes, SKEncodedImageFormat format)
    {
        if (format == SKEncodedImageFormat.Gif) return true;
        if (format is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp)) return false;
        var png = format == SKEncodedImageFormat.Png;
        foreach (var (offset, length, tag) in Chunks(bytes, png))
            if (tag is "acTL" or "ANIM" or "ANMF") return true;
        return false;
    }
    // All offsets and lengths are bounded before indexing or copying.
    private static IEnumerable<(int Offset, int Length, string Tag)> Chunks(byte[] bytes, bool png)
    {
        var offset = png ? 8 : 12;
        var limit = bytes.Length;
        if (!png)
        {
            if (limit < 12) throw Invalid();
            var declared = 8L + BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
            if (declared > limit || declared < 12) throw Invalid();
            limit = (int)declared;
        }
        while (offset < limit)
        {
            var overhead = png ? 12 : 8;
            if (limit - offset < overhead) throw Invalid();
            var length = png ? BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4)) : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            var total = overhead + (long)length + (png ? 0 : length & 1);
            if (total > limit - offset) throw Invalid();
            var tag = Encoding.ASCII.GetString(bytes, offset + (png ? 4 : 0), 4);
            yield return (offset, (int)total, tag);
            offset += (int)total;
            if (png && tag == "IEND") yield break; // Discard trailing payloads.
        }
    }
    public static byte[] Sanitize(byte[] bytes, SKEncodedImageFormat format)
    {
        if (format == SKEncodedImageFormat.Gif) return Gif(bytes);
        var png = format == SKEncodedImageFormat.Png;
        if (!png && format != SKEncodedImageFormat.Webp) throw Invalid();
        using var output = new MemoryStream();
        output.Write(bytes, 0, png ? 8 : 12);
        foreach (var (offset, length, tag) in Chunks(bytes, png))
        {
            if (png)
            {
                if (tag is "IHDR" or "PLTE" or "IDAT" or "IEND" or "tRNS" or "acTL" or "fcTL" or "fdAT" or "gAMA" or "sRGB" or "cHRM")
                    output.Write(bytes, offset, length);
            }
            else if (tag == "VP8X")
            {
                if (length != 18) throw Invalid();
                var chunk = bytes.AsSpan(offset, length).ToArray();
                chunk[8] &= 0xD3; // Clear ICC, EXIF, XMP flags; retain alpha and animation.
                output.Write(chunk);
            }
            else if (tag == "ANMF")
            {
                var payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
                if (payloadSize < 16) throw Invalid();
                using var frame = new MemoryStream();
                frame.Write(bytes, offset + 8, 16);
                var pos = offset + 24; var end = offset + 8 + (int)payloadSize;
                while (pos < end)
                {
                    if (end - pos < 8) throw Invalid();
                    var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 4, 4));
                    var total = 8L + count + (count & 1);
                    if (total > end - pos) throw Invalid();
                    var kind = Encoding.ASCII.GetString(bytes, pos, 4);
                    if (kind is "ALPH" or "VP8 " or "VP8L") frame.Write(bytes, pos, (int)total);
                    pos += (int)total;
                }
                output.Write("ANMF"u8);
                var size = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)frame.Length); output.Write(size);
                frame.Position = 0; frame.CopyTo(output); if ((frame.Length & 1) != 0) output.WriteByte(0);
            }
            else if (tag is "VP8 " or "VP8L" or "ALPH" or "ANIM") output.Write(bytes, offset, length);
        }
        var result = output.ToArray();
        if (!png) BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), (uint)result.Length - 8);
        return result;
    }
    private static byte[] Gif(byte[] bytes)
    {
        if (bytes.Length < 13) throw Invalid();
        var pos = 13 + ((bytes[10] & 128) != 0 ? 3 * (1 << ((bytes[10] & 7) + 1)) : 0);
        if (pos > bytes.Length) throw Invalid();
        using var output = new MemoryStream(); output.Write(bytes, 0, pos);
        int EndBlocks(int start)
        {
            while (start < bytes.Length)
            {
                var size = bytes[start++];
                if (size == 0) return start;
                if (size > bytes.Length - start) throw Invalid();
                start += size;
            }
            throw Invalid();
        }
        while (pos < bytes.Length)
        {
            var start = pos;
            switch (bytes[pos++])
            {
                case 0x3B: output.WriteByte(0x3B); return output.ToArray();
                case 0x21:
                    if (pos >= bytes.Length) throw Invalid();
                    var kind = bytes[pos++]; var blocks = pos; pos = EndBlocks(pos);
                    if (kind == 0xF9)
                    {
                        if (pos - blocks != 6 || bytes[blocks] != 4) throw Invalid();
                        output.Write(bytes, start, pos - start);
                    }
                    // Retain only the canonical loop count, never arbitrary application payloads.
                    else if (kind == 0xFF && pos - blocks >= 17 && bytes[blocks] == 11
                        && Encoding.ASCII.GetString(bytes, blocks + 1, 11) is "NETSCAPE2.0" or "ANIMEXTS1.0"
                        && bytes[blocks + 12] == 3 && bytes[blocks + 13] == 1)
                    {
                        output.Write(new byte[] { 0x21, 0xFF, 11 }); output.Write("NETSCAPE2.0"u8);
                        output.Write(new byte[] { 3, 1, bytes[blocks + 14], bytes[blocks + 15], 0 });
                    }
                    break;
                case 0x2C:
                    if (bytes.Length - pos < 9) throw Invalid();
                    var packed = bytes[pos + 8]; pos += 9;
                    if ((packed & 128) != 0) pos += 3 * (1 << ((packed & 7) + 1));
                    if (pos >= bytes.Length) throw Invalid();
                    pos = EndBlocks(pos + 1); // LZW minimum code size followed by image sub-blocks.
                    output.Write(bytes, start, pos - start);
                    break;
                default: throw Invalid();
            }
        }
        throw Invalid();
    }
}
