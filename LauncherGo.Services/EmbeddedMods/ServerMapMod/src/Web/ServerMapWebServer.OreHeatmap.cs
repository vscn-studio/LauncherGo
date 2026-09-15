using System.Net;
using ServerMap.World;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace ServerMap.Web;

public sealed partial class ServerMapWebServer
{
    private OreHeatmapStore oreHeatmap = null!;
    private Timer? oreSaveTimer;

    private void InitializeOreHeatmap()
    {
        oreHeatmap = new OreHeatmapStore(Path.Combine(root, "ore-heatmap.json"), GlobalConstants.ChunkSize, api.Logger.Warning);
        // Persistence runs off the game thread. Records are snapshotted before file IO.
        oreSaveTimer = new Timer(_ => oreHeatmap.Save(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    internal void RecordProspecting(PropickReading reading, IServerPlayer player)
    {
        if (stop.IsCancellationRequested || player.Entity?.Pos.Dimension != 0
            || reading.Position == null || reading.OreReadings == null) return;
        var values = reading.OreReadings.Where(pair => pair.Value != null).Select(pair =>
            new OreHeatmapStore.OreValue(pair.Key, pair.Value.TotalFactor, pair.Value.PartsPerThousand));
        if (oreHeatmap.Record(reading.Position.X, reading.Position.Z, values, DateTimeOffset.UtcNow, (int)Math.Round(reading.Position.Y)))
            events.Publish("layer", new { layer = "mineral-heatmap" });
    }

    internal void RecordNodeProspecting(double x, int y, double z, int radius, IEnumerable<OreHeatmapStore.NodeValue> ores, IServerPlayer player, OreHeatmapStore.OreBlock[]? blocks = null)
    {
        if (stop.IsCancellationRequested || player.Entity?.Pos.Dimension != 0) return;
        if (oreHeatmap.RecordNode(x, y, z, radius, ores, DateTimeOffset.UtcNow, player.PlayerUID, player.PlayerName, blocks)) events.Publish("layer", new { layer = "mineral-heatmap" });
    }

    private void HandleOreHeatmap(HttpListenerContext context)
    {
        // This endpoint is read-only, including for administrators. Clients cannot assert readings.
        if (context.Request.HttpMethod != "GET") { Error(context, 405, "Method not allowed"); return; }
        var bounds = ParseBounds(context.Request.QueryString["bbox"]);
        if (bounds == null || !double.IsFinite(bounds.Value.MinX) || !double.IsFinite(bounds.Value.MinZ)
            || !double.IsFinite(bounds.Value.MaxX) || !double.IsFinite(bounds.Value.MaxZ))
        { Error(context, 400, "A finite bbox is required"); return; }
        var mode = context.Request.QueryString["mode"];
        if (mode is not (null or "" or "density" or "node")) { Error(context, 400, "Unknown prospecting mode"); return; }
        int? minY = null, maxY = null;
        if (context.Request.QueryString["minY"] is string minText) { if (!int.TryParse(minText, out var min) || min is < 0 or > 65535) { Error(context, 400, "Invalid minY"); return; } minY = min; }
        if (context.Request.QueryString["maxY"] is string maxText) { if (!int.TryParse(maxText, out var max) || max is < 0 or > 65535) { Error(context, 400, "Invalid maxY"); return; } maxY = max; }
        if (minY > maxY) { Error(context, 400, "Inverted height range"); return; }
        context.Response.Headers["Vary"] = "Cookie";
        Json(context, OreHeatmap(bounds.Value, context.Request.QueryString["ore"], mode, Principal(context.Request), minY, maxY), true);
    }

    private object OreHeatmap((double MinX, double MinZ, double MaxX, double MaxZ) bounds, string? selectedOre, string? mode, MapAuthStore.Principal? principal, int? minY = null, int? maxY = null)
    {
        if (Management.Layer("mineral-heatmap").Forbidden || principal == null && Management.FogEnabled)
            return new { type = "FeatureCollection", version = 1, features = Array.Empty<object>(), ores = Array.Empty<object>(), truncated = false };

        var result = QueryOreHeatmap(bounds, selectedOre, mode, principal, minY, maxY);
        var features = result.Cells.Select(cell => new
        {
            type = "Feature", id = cell.Id,
            geometry = cell.Mode == "node"
                ? (object)new { type = "Point", coordinates = new[] { cell.SampleX!.Value, cell.SampleZ!.Value } }
                : new { type = "Polygon", coordinates = new[] { new[] {
                    new[] { cell.MinX, cell.MinZ }, new[] { cell.MaxX, cell.MinZ },
                    new[] { cell.MaxX, cell.MaxZ }, new[] { cell.MinX, cell.MaxZ }, new[] { cell.MinX, cell.MinZ }
                } } },
            properties = OreCellView(cell, principal)
        });
        return new { type = "FeatureCollection", version = 1, features, truncated = result.Truncated,
            ores = result.OreCodes.Select(code => new { code, zh = OreName(code, "zh-cn"), en = OreName(code, "en") }) };
    }

    private OreHeatmapStore.Result QueryOreHeatmap((double MinX, double MinZ, double MaxX, double MaxZ) bounds,
        string? selectedOre, string? mode, MapAuthStore.Principal? principal, int? minY = null, int? maxY = null,
        double? pointX = null, double? pointZ = null) =>
        oreHeatmap.Query(bounds, selectedOre, (minX, minZ, maxX, maxZ) =>
        {
            // A node search can cross several chunks; corners alone miss an unexplored middle chunk.
            for (var x = Math.Floor(minX / 32) * 32; x < maxX; x += 32)
                for (var z = Math.Floor(minZ / 32) * 32; z < maxZ; z += 32)
                    if (!FogVisible(principal, Math.Max(x, minX), Math.Max(z, minZ))) return false;
            // Checking only corners would leak a cell containing a small hidden region.
            return principal?.IsAdmin == true || !notebook.Regions.Any(region => MapVisibility.Intersects(region, minX, minZ, maxX, maxZ));
        }, mode, minY, maxY, pointX, pointZ);

    private static object OreCellView(OreHeatmapStore.Cell cell, MapAuthStore.Principal? principal) => new
    {
        id = cell.Id, kind = "mineral-heatmap", mode = cell.Mode, sampleY = cell.SampleY, radius = cell.Radius,
        sampleX = cell.SampleX, sampleZ = cell.SampleZ, density = cell.Density, sampledAt = cell.SampledAt,
        ownerName = cell.OwnerName, owned = principal != null && cell.OwnerUid == principal.PlayerUid,
        adminDelete = principal?.IsAdmin == true, hasBlockPositions = cell.Blocks != null,
        ores = cell.Mode == "node" ? cell.Nodes.Select(value => (object)new { code = value.Code, amountLevel = value.AmountLevel, blocks = value.Blocks }).ToArray()
            : cell.Ores.Select(value => (object)new { code = value.Code, density = value.Density, partsPerThousand = value.PartsPerThousand }).ToArray()
    };

    private void HandleOreProbes(HttpListenerContext context)
    {
        var method = context.Request.HttpMethod;
        if (method is not ("GET" or "DELETE")) { Error(context, 405, "Method not allowed"); return; }
        var principal = Principal(context.Request);
        if (method == "DELETE" && principal == null) { Error(context, 401, "Login required"); return; }
        if (method == "DELETE" && context.Request.Headers["X-ServerMap-Request"] != "1") { Error(context, 403, "Missing request header"); return; }
        if (!int.TryParse(context.Request.QueryString["x"], out var x) || !int.TryParse(context.Request.QueryString["z"], out var z))
        { Error(context, 400, "Invalid probe coordinates"); return; }
        if (Management.Layer("mineral-heatmap").Forbidden || principal == null && Management.FogEnabled)
        { Error(context, 403, "Probe unavailable"); return; }
        if (method == "DELETE")
        {
            var removed = oreHeatmap.RemoveNodePoint(principal!.PlayerUid, x, z, principal.IsAdmin);
            if (removed == 0) { NotFound(context); return; }
            oreHeatmap.Save();
            events.Publish("layer", new { layer = "mineral-heatmap" });
            Json(context, new { removed }, true); return;
        }
        int? minY = null, maxY = null;
        if (context.Request.QueryString["minY"] is string minText) { if (!int.TryParse(minText, out var min) || min is < 0 or > 65535) { Error(context, 400, "Invalid minY"); return; } minY = min; }
        if (context.Request.QueryString["maxY"] is string maxText) { if (!int.TryParse(maxText, out var max) || max is < 0 or > 65535) { Error(context, 400, "Invalid maxY"); return; } maxY = max; }
        if (minY > maxY) { Error(context, 400, "Inverted height range"); return; }
        // Keep all ore types until newer overlapping observations have been applied.
        var result = QueryOreHeatmap((x, z, x, z), null, "node", principal, minY, maxY, pointX: x, pointZ: z);
        var cells = result.Cells.Where(cell => cell.SampleX == x && cell.SampleZ == z).ToArray();
        context.Response.Headers["Vary"] = "Cookie";
        var histogram = OreHeatmapStore.AggregateNodePoint(cells, context.Request.QueryString["ore"], minY, maxY);
        Json(context, new { minY = histogram.MinY, maxY = histogram.MaxY, step = histogram.Step,
            incomplete = histogram.Incomplete || result.Truncated, truncated = result.Truncated,
            coverage = histogram.Coverage.Select(r => new { minY = r.MinY, maxY = r.MaxY }),
            columns = histogram.Columns.Select(c => new { code = c.Code, total = c.Total,
                zh = OreName(c.Code, "zh-cn"), en = OreName(c.Code, "en"), layers = c.Layers.Select(l => new { y = l.Y, blocks = l.Blocks }) }) }, true);
    }

    private static string OreName(string code, string language)
    {
        var key = "ore-" + code;
        try { var name = Lang.GetL(language, key); return name == key ? code : name; }
        catch { return code; }
    }

    private async Task StopOreHeatmap()
    {
        if (oreSaveTimer != null) await oreSaveTimer.DisposeAsync().ConfigureAwait(false);
        await Task.Run(() => oreHeatmap.Save()).ConfigureAwait(false);
    }
}
