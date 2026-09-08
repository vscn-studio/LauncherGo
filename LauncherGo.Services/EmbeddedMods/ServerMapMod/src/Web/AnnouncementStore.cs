using System.Text.Json;
using ServerMap.Util;

namespace ServerMap.Web;

public sealed class AnnouncementStore
{
    public sealed record Announcement(string Html, string ServerWebsite, string UpdatedBy, DateTimeOffset UpdatedAt)
    {
        public WebPageMetadata Site { get; init; } = new();
    }
    private readonly string path;
    private readonly object gate = new();
    private Announcement current;

    public AnnouncementStore(string path)
    {
        this.path = path;
        current = new Announcement("<h3>服务器公告</h3><p>欢迎来到服务器。</p>", "https://vintagestory.at", "server", DateTimeOffset.UtcNow);
        try { if (File.Exists(path)) current = JsonSerializer.Deserialize<Announcement>(File.ReadAllText(path)) ?? current; }
        catch { }
    }

    public Announcement Current { get { lock (gate) return current; } }

    public Announcement Save(string html, string serverWebsite, string updatedBy, WebPageMetadata? site = null)
    {
        if (html.Length > 50_000) html = html[..50_000];
        if (!Uri.TryCreate(serverWebsite, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) serverWebsite = "https://vintagestory.at";
        else serverWebsite = uri.ToString();
        lock (gate)
        {
            var next = new Announcement(html, serverWebsite, updatedBy, DateTimeOffset.UtcNow) { Site = (site ?? current.Site ?? new()).Normalize() };
            AtomicFile.Replace(path, temp => File.WriteAllText(temp, JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true })));
            current = next;
            return current;
        }
    }
}
