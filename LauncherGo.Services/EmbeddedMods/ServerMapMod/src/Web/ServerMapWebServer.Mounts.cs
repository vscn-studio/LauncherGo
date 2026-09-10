using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using ServerMap.Render;
using SkiaSharp;

namespace ServerMap.Web;

public sealed partial class ServerMapWebServer
{
    public MountSnapshotStore Mounts { get; } = new(NormalizeMountImage);

    internal static byte[] NormalizeMountImage(byte[] bytes)
    {
        if (bytes.Length < 33 || bytes.Length > MountSnapshotStore.MaxBytes
            || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)
            || BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)) != TopDownMountRenderer.Size
            || BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)) != TopDownMountRenderer.Size)
            throw new InvalidDataException("Mount atlas must be a 1024 x 1024 PNG");
        using var bitmap = SKBitmap.Decode(bytes);
        if (bitmap == null || bitmap.Width != TopDownMountRenderer.Size || bitmap.Height != TopDownMountRenderer.Size) throw new InvalidDataException("Invalid mount PNG");
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray(); // Re-encode pixels; never serve client metadata or original bytes.
    }

    private bool MountVisible(MountSnapshotStore.Mount mount, MountSnapshotStore.Image image, MapAuthStore.Principal? principal) =>
        MountVisibility.Visible(mount, image, config.PublicPlayers, principal?.PlayerUid, principal?.IsAdmin == true, notebook.Regions);

    private void AddMounts(List<object> features, (double MinX, double MinZ, double MaxX, double MaxZ)? bounds, MapAuthStore.Principal? principal)
    {
        foreach (var mount in Mounts.Active)
        {
            var image = Mounts.Get(mount.Id);
            if (image == null || !MountVisible(mount, image, principal)) continue;
            var footprint = MountVisibility.Bounds(mount, image);
            if (!Intersects(footprint.MinX, footprint.MinZ, footprint.MaxX, footprint.MaxZ, bounds)) continue;
            features.Add(PointFeature(mount.Id.ToString(CultureInfo.InvariantCulture), mount.X, mount.Z,
                new { name = mount.Name, code = mount.Code, y = mount.Y, yaw = mount.Yaw, imageKey = image.Key,
                    worldSize = image.WorldSize, centerX = image.CenterX, centerZ = image.CenterZ,
                    directions = TopDownMountRenderer.Directions, displayScale = MountSnapshotStore.DisplayScale(image.Footprint), kind = "mount" }));
        }
    }

    private void HandleMountImage(HttpListenerContext context)
    {
        context.Response.Headers["Vary"] = "Cookie";
        context.Response.Headers["Cache-Control"] = "no-store";
        if (context.Request.HttpMethod != "GET") { context.Response.StatusCode = 405; context.Response.Close(); return; }
        if (!long.TryParse(context.Request.QueryString["id"], NumberStyles.None, CultureInfo.InvariantCulture, out var id)) { NotFound(context); return; }
        var mount = Mounts.Active.FirstOrDefault(m => m.Id == id); var image = Mounts.Get(id);
        if (mount == null || image == null || image.Key != context.Request.QueryString["key"] || !MountVisible(mount, image, Principal(context.Request))) { NotFound(context); return; }
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ServeBytes(context, image.Png, "image/png", "no-store");
    }
}
