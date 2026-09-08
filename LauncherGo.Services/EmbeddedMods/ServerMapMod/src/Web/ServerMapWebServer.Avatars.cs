using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace ServerMap.Web;

public sealed partial class ServerMapWebServer
{
    private LocalAvatarCache? avatars;
    public ClientAvatarStore? ClientAvatars { get; private set; }

    private void InitializeAvatars()
    {
        ClientAvatars = new ClientAvatarStore(Path.Combine(root, "client-avatars"), message => api.Logger.Notification("ServerMap {0}", message));
        api.Logger.Notification("ServerMap client-model avatars enabled; head mesh and cropped textures will be requested from connected map-mod clients.");
        try
        {
            var path = string.IsNullOrWhiteSpace(config.AvatarAssetsPath) ? Path.Combine(root, "avatar-layers") : Path.GetFullPath(config.AvatarAssetsPath);
            if (Directory.Exists(Path.Combine(path, "public", "v2"))) path = Path.Combine(path, "public", "v2");
            else if (Directory.Exists(Path.Combine(path, "v2"))) path = Path.Combine(path, "v2");
            if (!Directory.Exists(Path.Combine(path, "baseskin")))
            {
                api.Logger.Notification("ServerMap optional avatar layers not configured; using client-model avatars.");
                return;
            }
            var files = Directory.EnumerateFiles(path, "*.png", SearchOption.AllDirectories).Take(10001).Order(StringComparer.Ordinal).ToArray();
            if (files.Length > 10000) throw new InvalidDataException("Too many avatar assets");
            var stamp = string.Join('\n', files.Select(file => { var info = new FileInfo(file); return $"{file}:{info.Length}:{info.LastWriteTimeUtc.Ticks}"; }));
            var revision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stamp)));
            avatars = new LocalAvatarCache(new LocalAvatarRenderer(path, DecodeAvatarLayer), Path.Combine(root, "avatars"), revision, message => api.Logger.Warning("ServerMap {0}", message));
            api.Logger.Notification("ServerMap local avatars enabled: {0} PNG layers at {1}.", files.Length, path);
        }
        catch (Exception ex) { api.Logger.Warning("ServerMap local avatar setup failed: {0}", ex.Message); }
    }

    internal static byte[] DecodeAvatarLayer(byte[] bytes)
    {
        if (bytes.Length < 33 || bytes.Length > LocalAvatarRenderer.MaxImageBytes || !bytes.AsSpan(0,8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})
            || BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16,4)) != LocalAvatarRenderer.Size || BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20,4)) != LocalAvatarRenderer.Size)
            throw new InvalidDataException("Avatar layer must be a 256 x 256 PNG");
        using var bitmap = SKBitmap.Decode(bytes);
        if (bitmap == null || bitmap.Width != 256 || bitmap.Height != 256) throw new InvalidDataException("Invalid avatar PNG");
        var rgba = new byte[256 * 256 * 4];
        for (var y = 0; y < 256; y++) for (var x = 0; x < 256; x++)
        {
            var color = bitmap.GetPixel(x, y); var i = (y * 256 + x) * 4;
            rgba[i] = color.Red; rgba[i+1] = color.Green; rgba[i+2] = color.Blue; rgba[i+3] = color.Alpha;
        }
        return rgba;
    }
}
