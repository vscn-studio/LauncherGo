using System.Net;

namespace ServerMap.Web;

public sealed partial class ServerMapWebServer
{
    private void HandlePoiImage(HttpListenerContext context)
    {
        if (context.Request.HttpMethod != "GET") { Error(context, 405, "Method not allowed"); return; }
        var principal = Principal(context.Request);
        var id = context.Request.QueryString["id"];
        var key = context.Request.QueryString["key"];
        if (!announcements.Current.PoiImagesEnabled || !PoiImageStore.ValidKey(key)
            || !int.TryParse(context.Request.QueryString["size"], out var size) || size is not (480 or 1280)
            || !pois.All.Any(p => p.Id == id && p.ImageKey == key && PoiVisible(principal, p)))
        { NotFound(context); return; }
        context.Response.Headers["Vary"] = "Cookie";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        // Recheck visibility and the administrator switch on every request.
        ServeFile(context, poiImages.FilePath(key!, size), "image/webp", true);
    }
}
