using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ServerMap.Render;

namespace ServerMap.Web;

/// <summary>Local RGBA avatar layer compositor.</summary>
public sealed class LocalAvatarRenderer
{
    public const int Size = 256;
    public const int MaxImageBytes = 1024 * 1024;
    public sealed record Appearance(string BaseSkin, string EyeColor, string HairBase, string HairExtra, string Mustache, string Beard, string HairColor)
    {
        public string[] Parts => [BaseSkin, EyeColor, HairBase, HairExtra, Mustache, Beard, HairColor];
        public bool Valid => Parts.All(p => p != null && Regex.IsMatch(p, "^[a-zA-Z0-9_-]{1,80}$"));
        public string Key(string revision) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("local-avatar-1/" + revision + "/" + string.Join('/', Parts))));
    }

    private readonly string directory;
    private readonly Func<byte[], byte[]> decode;
    public LocalAvatarRenderer(string directory, Func<byte[], byte[]> decode) { this.directory = Path.GetFullPath(directory); this.decode = decode; }

    public byte[] Render(Appearance appearance)
    {
        if (!appearance.Valid) throw new ArgumentException("Invalid avatar appearance");
        var pixels = Read("baseskin", appearance.BaseSkin + ".png");
        Add("eyecolor", appearance.EyeColor + ".png");
        AddPart("hairbase", appearance.HairBase);
        AddPart("hairextra", appearance.HairExtra);
        AddPart("mustache", appearance.Mustache);
        AddPart("beard", appearance.Beard);
        return PngEncoder.Encode(Size, Size, pixels);

        void AddPart(string category, string variant)
        {
            if (variant == "none") return;
            Add(Path.Combine(category, variant), appearance.HairColor + ".png");
        }
        void Add(string folder, string filename)
        {
            var layer = Read(folder, filename);
            var maskPath = Path.Combine(directory, folder, "mask.png");
            // An explicit mask supports opaque rendered layers; ordinary transparent
            // PNG artwork can omit it. Missing requested layers fail instead of showing
            // a misleading, partially composed player appearance.
            var mask = File.Exists(maskPath) ? Read(folder, "mask.png") : null;
            Composite(pixels, layer, mask);
        }
    }

    private byte[] Read(string folder, string file)
    {
        var path = Path.Combine(directory, folder, file);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Avatar layer missing", path);
        if (info.Length > MaxImageBytes) throw new InvalidDataException("Avatar layer too large");
        var pixels = decode(File.ReadAllBytes(path));
        if (pixels.Length != Size * Size * 4) throw new InvalidDataException("Avatar layer must be 256 x 256 RGBA");
        return pixels;
    }

    /// <summary>Straight-alpha source-over, with optional grayscale/alpha coverage mask.</summary>
    public static void Composite(byte[] destination, byte[] source, byte[]? mask)
    {
        if (destination.Length != source.Length || destination.Length % 4 != 0 || mask != null && mask.Length != source.Length) throw new ArgumentException("Mismatched RGBA layers");
        for (var i = 0; i < source.Length; i += 4)
        {
            var coverage = mask == null ? 1.0 : Math.Max(mask[i], Math.Max(mask[i + 1], mask[i + 2])) / 255.0 * mask[i + 3] / 255.0;
            var alpha = source[i + 3] / 255.0 * coverage;
            if (alpha == 0) continue;
            var background = destination[i + 3] / 255.0 * (1 - alpha);
            var total = alpha + background;
            for (var c = 0; c < 3; c++) destination[i + c] = (byte)Math.Round((source[i + c] * alpha + destination[i + c] * background) / total);
            destination[i + 3] = (byte)Math.Round(total * 255);
        }
    }
}
