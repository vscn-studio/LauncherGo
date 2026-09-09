using System.Text.Json;
using ServerMap.Render;

namespace ServerMap.Web;

public static class MapVisibility
{
    // Preview only adds restrictions. A query parameter can never unmask a guest's tiles.
    public static bool ShouldMaskTiles(bool isAdmin, string? preview) => !isAdmin || preview == "1";
    public static bool Intersects(MapNotebookStore.Region r, double minX, double minZ, double maxX, double maxZ) =>
        minX <= r.MaxX && maxX >= r.MinX && minZ <= r.MaxZ && maxZ >= r.MinZ;
    public static bool Visible(IEnumerable<MapNotebookStore.Region> regions, double x, double z) => !regions.Any(r => Intersects(r, x, z, x, z));
    // Keep a link when at least one endpoint is visible so the visible endpoint
    // can still be shown with an unknown destination. The line itself is only
    // allowed when both endpoints are visible.
    public static bool TranslocatorVisible(IEnumerable<MapNotebookStore.Region> regions, double x, double z, double targetX, double targetZ) =>
        Visible(regions, x, z) || Visible(regions, targetX, targetZ);
    public static bool TranslocatorLineVisible(IEnumerable<MapNotebookStore.Region> regions, double x, double z, double targetX, double targetZ) =>
        Visible(regions, x, z) && Visible(regions, targetX, targetZ);
    public static bool FeatureVisible(IEnumerable<MapNotebookStore.Region> regions, JsonElement feature)
    {
        var geometry = feature.GetProperty("geometry");
        var coordinates = geometry.GetProperty("coordinates");
        // Endpoint visibility is carried in the feature properties so a link
        // can cross fog without exposing a translocator inside it.
        if (geometry.GetProperty("type").GetString() == "LineString" && coordinates.GetArrayLength() == 2
            && feature.TryGetProperty("properties", out var properties)
            && properties.TryGetProperty("kind", out var kind) && kind.GetString() == "translocator")
            return true;
        return GeometryVisible(regions, coordinates);
    }
    public static bool GeometryVisible(IEnumerable<MapNotebookStore.Region> regions, JsonElement coordinates)
    {
        var points = new List<(double X, double Z)>();
        void Walk(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Array) return;
            if (node.GetArrayLength() >= 2 && node[0].ValueKind == JsonValueKind.Number && node[1].ValueKind == JsonValueKind.Number) points.Add((node[0].GetDouble(), node[1].GetDouble()));
            else foreach (var child in node.EnumerateArray()) Walk(child);
        }
        Walk(coordinates);
        // Conservatively omit whole crossing features; never disclose their hidden endpoints.
        return points.Count == 0 || !regions.Any(r => Intersects(r, points.Min(p => p.X), points.Min(p => p.Z), points.Max(p => p.X), points.Max(p => p.Z)));
    }
    public static bool RouteVisible(IEnumerable<MapNotebookStore.Region> regions, double[][] points) =>
        !regions.Any(r => Intersects(r, points.Min(p => p[0]), points.Min(p => p[1]), points.Max(p => p[0]), points.Max(p => p[1])));
    public static byte[] MaskTile(byte[] png, int zoom, int x, int z, IReadOnlyList<MapNotebookStore.Region> regions)
    {
        var resolution = Math.Pow(2, zoom); var span = 512 * resolution; var originX = x * span; var originZ = z * span;
        var overlaps = regions.Where(r => Intersects(r, originX, originZ, originX + span, originZ + span)).ToArray();
        if (overlaps.Length == 0) return png;
        var pixels = PngEncoder.Decode(png);
        foreach (var r in overlaps)
        {
            // Round outward so no downsampled pixel retains hidden terrain.
            // Erase RGB as well as alpha: transparency alone would leak the
            // original data, while opaque white creates a bright block below fog.
            var left = (int)Math.Clamp(Math.Floor((r.MinX - originX) / resolution), 0, 511);
            var top = (int)Math.Clamp(Math.Floor((r.MinZ - originZ) / resolution), 0, 511);
            var right = (int)Math.Clamp(Math.Floor((r.MaxX - originX) / resolution) + 1, 0, 512);
            var bottom = (int)Math.Clamp(Math.Floor((r.MaxZ - originZ) / resolution) + 1, 0, 512);
            for (var row = top; row < bottom; row++) for (var col = left; col < right; col++) pixels.AsSpan((row * 512 + col) * 4, 4).Clear();
        }
        return PngEncoder.Encode(512, 512, pixels);
    }
}
