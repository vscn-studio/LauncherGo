using SkiaSharp;
using ServerMap.Util;

namespace ServerMap.Web;

// Still pixels are saved as PNG; animation containers retain frames without private metadata.
// Legacy 1280 WebP display files remain readable.
public sealed class PoiImageStore(string root)
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public const int MaxDimension = 8192;
    public static bool ValidKey(string? key) => key is { Length: 32 } && System.Text.RegularExpressions.Regex.IsMatch(key, "^[a-f0-9]{32}$");
    public string FilePath(string key, int size)
    {
        if (!ValidKey(key) || size is not (0 or 480 or 1280)) throw new ArgumentException("Invalid image key or size");
        return Path.Combine(root, size == 0 ? $"{key}-original.png" : $"{key}-{size}.webp");
    }
    public string DisplayPath(string key)
    {
        var png = FilePath(key, 0); // Also validates the opaque key.
        foreach (var extension in new[] { "gif", "webp" })
        {
            var path = Path.ChangeExtension(png, extension);
            if (File.Exists(path)) return path;
        }
        return png;
    }
    public string Save(byte[] bytes, MapManagementSettings? settings = null)
    {
        settings = (settings ?? new()).Validate();
        if (bytes.Length == 0 || bytes.Length > settings.ImageMaxMb * 1024 * 1024) throw new InvalidDataException("poi_image_size");
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec == null)
            throw new InvalidDataException("poi_image_format");
        if (!settings.ImageTypes.Contains(codec.EncodedFormat.ToString().ToLowerInvariant())) throw new InvalidDataException("poi_image_format");
        var animated = AnimatedImageSanitizer.HasAnimation(bytes, codec.EncodedFormat);
        var info = codec.Info;
        if (info.Width < 1 || info.Height < 1 || info.Width > MaxDimension || info.Height > MaxDimension || (long)info.Width * info.Height > 32_000_000) throw new InvalidDataException("poi_image_dimensions");
        using var bitmap = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success) throw new InvalidDataException("poi_image_format");
        var key = Guid.NewGuid().ToString("N");
        try
        {
            foreach (var size in new[] { 480, 0 })
            {
                if (size == 0 && animated)
                {
                    var sanitized = AnimatedImageSanitizer.Sanitize(bytes, codec.EncodedFormat);
                    var path = Path.ChangeExtension(FilePath(key, 0), codec.EncodedFormat.ToString().ToLowerInvariant());
                    AtomicFile.Replace(path, temp => File.WriteAllBytes(temp, sanitized));
                    continue;
                }
                var swapped = (int)codec.EncodedOrigin >= 5;
                var width = swapped ? info.Height : info.Width; var height = swapped ? info.Width : info.Height;
                var ratio = size == 0 ? 1d : Math.Min(1d, (double)size / Math.Max(width, height));
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
                using var encoded = image.Encode(size == 0 ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Webp, size == 0 ? 100 : 82) ?? throw new InvalidDataException("poi_image_encode");
                AtomicFile.Replace(FilePath(key, size), temp => { using var stream = File.Create(temp); encoded.SaveTo(stream); });
            }
            return key;
        }
        catch { Delete(key); throw; }
    }
    public void Delete(string? key)
    {
        if (!ValidKey(key)) return;
        foreach (var size in new[] { 0, 480, 1280 })
            try { File.Delete(FilePath(key!, size)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        foreach (var extension in new[] { "gif", "webp" })
            try { File.Delete(Path.ChangeExtension(FilePath(key!, 0), extension)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
