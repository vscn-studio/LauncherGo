using System.Net;
using System.Text.Json;

namespace ServerMap.Web;

public sealed partial class ServerMapWebServer
{
    private readonly AreaMarkerStore areaMarkers;
    private bool AreaMarkerRequest(HttpListenerContext context, string path)
    {
        if (path != "api/v1/area-markers") return false;
        var principal = Principal(context.Request);
        var method = context.Request.HttpMethod;
        if (method == "GET")
        {
            context.Response.Headers["Vary"] = "Cookie";
            var snapshot = areaMarkers.Read(); var fog = notebook.Regions;
            var management = Management;
            var bypass = principal?.IsAdmin == true && management.AdminsBypassFog;
            var restrict = management.FogEnabled && !bypass;
            var explored = !restrict || principal == null ? [] : exploration.VisibleRects(principal.PlayerUid, management.ShareExploration, uid => alliances.IsAllied(principal.PlayerUid, uid));
            static int[][] Coordinates(AreaMarkerStore.Rect[] rects) => rects.Select(r => new[] { r.MinX, r.MinZ, r.MaxX, r.MaxZ }).ToArray();
            var visible = snapshot.Markers
                // Keep the existing hidden-region privacy rule independent from
                // the exploration fog. Administrators retain their preview.
                .Where(m => principal?.IsAdmin == true || !m.Rects.Any(r => fog.Any(f => MapVisibility.Intersects(f, r.MinX, r.MinZ, r.MaxX, r.MaxZ))))
                .Select(m =>
                {
                    var shape = restrict ? AreaMarkerVisibility.Clip(m.Rects, explored) : null;
                    return new { marker = m, rects = shape?.Rects ?? m.Rects, borders = shape?.Borders };
                })
                .Where(item => item.rects.Length > 0 || principal?.IsAdmin == true)
                .Select(item => new {
                    id = item.marker.Id, name = item.marker.Name, color = item.marker.Color, minZoom = item.marker.MinZoom, maxZoom = item.marker.MaxZoom,
                    borderOpacity = item.marker.Style.BorderOpacity, fillOpacity = item.marker.Style.FillOpacity, textOpacity = item.marker.Style.TextOpacity,
                    rects = Coordinates(item.rects), borders = item.borders?.Select(e => new[] { e.X1, e.Z1, e.X2, e.Z2 }).ToArray(),
                    // Only admins receive the original for explicit editing;
                    // normal rendering and hit testing always use clipped rects.
                    editRects = principal?.IsAdmin == true && restrict ? Coordinates(item.marker.Rects) : null
                });
            Json(context, new { revision = snapshot.Revision, zoomRanges = true, markers = visible }, true);
            return true;
        }
        if (method is not ("POST" or "DELETE")) { Error(context, 405, "Method not allowed"); return true; }
        if (principal == null) { Error(context, 401, "Login required"); return true; }
        if (!principal.IsAdmin || context.Request.Headers["X-ServerMap-Request"] != "1") { Error(context, 403, "Admin login and request header required"); return true; }
        try
        {
            using var doc = ReadJson(context.Request, 128 * 1024); var value = doc.RootElement;
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("revision", out var revisionValue) || revisionValue.ValueKind != JsonValueKind.Number || !revisionValue.TryGetInt64(out var revision) || revision < 0)
                throw new ArgumentException();
            string? S(string name, bool required = false)
            {
                if (!value.TryGetProperty(name, out var p)) { if (required) throw new ArgumentException(); return null; }
                if (p.ValueKind != JsonValueKind.String) throw new ArgumentException();
                return p.GetString();
            }
            var id = S("id");
            // Reading the body may outlive a logout or game permission change.
            if (Principal(context.Request)?.IsAdmin != true) { Error(context, 403, "Admin login required"); return true; }
            if (method == "DELETE")
            {
                if (!areaMarkers.Remove(revision, id ?? "")) { NotFound(context); return true; }
            }
            else
            {
                if (!value.TryGetProperty("minZoom", out var zoom) || zoom.ValueKind != JsonValueKind.Number || !zoom.TryGetInt32(out var minZoom)) throw new ArgumentException();
                int? maxZoom = null;
                if (value.TryGetProperty("maxZoom", out var endZoom)) { if (endZoom.ValueKind != JsonValueKind.Number || !endZoom.TryGetInt32(out var end)) throw new ArgumentException(); maxZoom = end; }
                var oldStyle = areaMarkers.Read().Markers.FirstOrDefault(m => m.Id == id)?.Style ?? new();
                double Opacity(string key, double fallback)
                {
                    if (!value.TryGetProperty(key, out var p)) return fallback;
                    if (p.ValueKind != JsonValueKind.Number || !p.TryGetDouble(out var v) || !double.IsFinite(v) || v < 0 || v > 1) throw new ArgumentException();
                    return v;
                }
                var style = new AreaMarkerStore.Appearance(Opacity("borderOpacity", oldStyle.BorderOpacity), Opacity("fillOpacity", oldStyle.FillOpacity), Opacity("textOpacity", oldStyle.TextOpacity));
                if (value.TryGetProperty("sourceIds", out var sources))
                {
                    if (!string.IsNullOrEmpty(id) || sources.ValueKind != JsonValueKind.Array || sources.GetArrayLength() is < 2 or > AreaMarkerStore.MaxMarkers
                        || sources.EnumerateArray().Any(p => p.ValueKind != JsonValueKind.String)) throw new ArgumentException();
                    areaMarkers.Merge(revision, sources.EnumerateArray().Select(p => p.GetString()!).ToArray(), S("name", true)!, S("color", true)!, minZoom, style, maxZoom);
                }
                else
                {
                    if (!value.TryGetProperty("rects", out var rectangles) || rectangles.ValueKind != JsonValueKind.Array || rectangles.GetArrayLength() is < 1 or > AreaMarkerStore.MaxRects) throw new ArgumentException();
                    var rects = rectangles.EnumerateArray().Select(r =>
                    {
                        if (r.ValueKind != JsonValueKind.Array || r.GetArrayLength() != 4 || r.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out _))) throw new ArgumentException();
                        return new AreaMarkerStore.Rect(r[0].GetInt32(), r[1].GetInt32(), r[2].GetInt32(), r[3].GetInt32());
                    }).ToArray();
                    areaMarkers.Save(revision, id, S("name", true)!, S("color", true)!, minZoom, rects, style, maxZoom);
                }
            }
            events.Publish("area-markers", new { changed = true });
            Json(context, new { saved = true }, true);
        }
        catch (KeyNotFoundException) { Error(context, 400, "Missing field or area marker"); }
        catch (InvalidOperationException ex) { Error(context, 409, ex.Message); }
        catch (Exception ex) when (ex is ArgumentException or JsonException or FormatException or OverflowException) { Error(context, 400, "Invalid area selection"); }
        return true;
    }
}
