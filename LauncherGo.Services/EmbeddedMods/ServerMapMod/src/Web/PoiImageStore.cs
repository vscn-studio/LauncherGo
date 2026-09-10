using SkiaSharp;
using ServerMap.Util;

namespace ServerMap.Web;

// Originals exist only in the bounded request buffer; only re-encoded WebP leaves memory.
public sealed class PoiImageStore(string root)
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public const int MaxDimension = 2560;
    public static bool ValidKey(string? key) => key is { Length: 32 } && System.Text.RegularExpressions.Regex.IsMatch(key, "^[a-f0-9]{32}$");
    public string FilePath(string key, int size)
    {
        if (!ValidKey(key) || size is not (480 or 1280)) throw new ArgumentException("Invalid image key or size");
        return Path.Combine(root, $"{key}-{size}.webp");
    }
    public string Save(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxBytes) throw new InvalidDataException("poi_image_size");
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec == null || codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp or SKEncodedImageFormat.Bmp))
            throw new InvalidDataException("poi_image_format");
        // Disallow animated WebP/APNG as well as GIF, including decoders that expose only frame zero.
        if (codec.FrameCount > 1 || IsAnimatedContainer(bytes, codec.EncodedFormat)) throw new InvalidDataException("poi_image_animation");
        var info = codec.Info;
        if (info.Width < 1 || info.Height < 1 || info.Width > MaxDimension || info.Height > MaxDimension) throw new InvalidDataException("poi_image_dimensions");
        using var bitmap = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success) throw new InvalidDataException("poi_image_format");
        var key = Guid.NewGuid().ToString("N");
        try
        {
            foreach (var size in new[] { 480, 1280 })
            {
                var swapped = (int)codec.EncodedOrigin >= 5;
                var width = swapped ? info.Height : info.Width; var height = swapped ? info.Width : info.Height;
                var ratio = Math.Min(1d, (double)size / Math.Max(width, height));
                using var output = new SKBitmap(Math.Max(1, (int)Math.Round(width * ratio)), Math.Max(1, (int)Math.Round(height * ratio)));
                using (var canvas = new SKCanvas(output))
                {
                    canvas.Clear(SKColors.Transparent); canvas.Scale((float)output.Width / width, (float)output.Height / height);
                    switch ((int)codec.EncodedOrigin)
                    {
                        case 2: canvas.Translate(width, 0); canvas.Scale(-1, 1); break;
                        case 3: canvas.Translate(width, height); canvas.RotateDegrees(180); break;
                        case 4: canvas.Translate(0, height); canvas.Scale(1, -1); break;
                        case 5: canvas.RotateDegrees(90); canvas.Scale(1, -1); break;
                        case 6: canvas.Translate(width, 0); canvas.RotateDegrees(90); break;
                        case 7: canvas.Translate(width, height); canvas.RotateDegrees(90); canvas.Scale(-1, 1); break;
                        case 8: canvas.Translate(0, height); canvas.RotateDegrees(-90); break;
                    }
                    using var source = SKImage.FromBitmap(bitmap);
                    canvas.DrawImage(source, new SKRect(0, 0, bitmap.Width, bitmap.Height), new SKSamplingOptions(SKCubicResampler.Mitchell));
                }
                using var image = SKImage.FromBitmap(output);
                using var encoded = image.Encode(SKEncodedImageFormat.Webp, 82) ?? throw new InvalidDataException("poi_image_encode");
                AtomicFile.Replace(FilePath(key, size), temp => { using var stream = File.Create(temp); encoded.SaveTo(stream); });
            }
            return key;
        }
        catch { Delete(key); throw; }
    }
    private static bool IsAnimatedContainer(byte[] bytes, SKEncodedImageFormat format)
    {
        if (format == SKEncodedImageFormat.Png)
        {
            for (long offset = 8; offset + 12 <= bytes.Length;)
            {
                var i = (int)offset;
                if (bytes.AsSpan(i + 4, 4).SequenceEqual("acTL"u8)) return true;
                offset += 12L + System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i, 4));
            }
        }
        if (format == SKEncodedImageFormat.Webp)
        {
            for (long offset = 12; offset + 8 <= bytes.Length;)
            {
                var i = (int)offset;
                if (bytes.AsSpan(i, 4).SequenceEqual("ANIM"u8) || bytes.AsSpan(i, 4).SequenceEqual("ANMF"u8)) return true;
                var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i + 4, 4));
                offset += 8L + length + (length & 1);
            }
        }
        return false;
    }
    public void Delete(string? key)
    {
        if (!ValidKey(key)) return;
        foreach (var size in new[] { 480, 1280 })
            try { File.Delete(FilePath(key!, size)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
