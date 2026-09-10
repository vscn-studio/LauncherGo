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
            var snapshot = areaMarkers.Read(); var fog = notebook.Regions;
            var visible = principal?.IsAdmin == true ? snapshot.Markers : snapshot.Markers.Where(m => !m.Rects.Any(r => fog.Any(f => MapVisibility.Intersects(f, r.MinX, r.MinZ, r.MaxX, r.MaxZ))));
            Json(context, new { revision = snapshot.Revision, markers = visible.Select(m => new { id = m.Id, name = m.Name, color = m.Color, minZoom = m.MinZoom, maxZoom = AreaMarkerStore.MaxZoom(m.MinZoom), rects = m.Rects.Select(r => new[] { r.MinX, r.MinZ, r.MaxX, r.MaxZ }) }) }, true);
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
                if (!value.TryGetProperty("rects", out var rectangles) || rectangles.ValueKind != JsonValueKind.Array || rectangles.GetArrayLength() is < 1 or > AreaMarkerStore.MaxRects
                    || !value.TryGetProperty("minZoom", out var zoom) || zoom.ValueKind != JsonValueKind.Number || !zoom.TryGetInt32(out var minZoom)) throw new ArgumentException();
                var rects = rectangles.EnumerateArray().Select(r =>
                {
                    if (r.ValueKind != JsonValueKind.Array || r.GetArrayLength() != 4 || r.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out _))) throw new ArgumentException();
                    return new AreaMarkerStore.Rect(r[0].GetInt32(), r[1].GetInt32(), r[2].GetInt32(), r[3].GetInt32());
                }).ToArray();
                areaMarkers.Save(revision, id, S("name", true)!, S("color", true)!, minZoom, rects);
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
