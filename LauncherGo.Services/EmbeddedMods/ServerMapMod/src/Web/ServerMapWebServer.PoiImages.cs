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
        if ((principal?.IsAdmin != true && (!announcements.Current.PoiImagesEnabled || Management.Layer("pois").Forbidden)) || !PoiImageStore.ValidKey(key)
            || !int.TryParse(context.Request.QueryString["size"], out var size) || size is not (0 or 480 or 1280)
            || !pois.All.Any(p => p.Id == id && p.ImageKey == key && PoiVisible(principal, p)))
        { NotFound(context); return; }
        context.Response.Headers["Vary"] = "Cookie";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        // Recheck visibility and the administrator switch on every request.
        var display = poiImages.DisplayPath(key!);
        var original = size != 480 && File.Exists(display);
        var file = original ? display : poiImages.FilePath(key!, size == 0 ? 1280 : size);
        var contentType = Path.GetExtension(file) switch { ".png" => "image/png", ".gif" => "image/gif", _ => "image/webp" };
        ServeFile(context, file, contentType, true);
    }
}
