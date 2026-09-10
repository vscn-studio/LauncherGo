using ServerMap.Web;
using Xunit;

namespace LauncherGo.Tests;

public sealed class PoiImageMetadataTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "poi-image-metadata-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public void SwitchDefaultsOffPersistsAndSurvivesLegacySaves()
    {
        var path = Path.Combine(root, "announcement.json"); var store = new AnnouncementStore(path);
        Assert.False(store.Current.PoiImagesEnabled);
        store.Save("news", "https://example.com", "admin", poiImagesEnabled: true);
        store = new AnnouncementStore(path); Assert.True(store.Current.PoiImagesEnabled);
        store.Save("legacy", "https://example.com", "admin");
        Assert.True(new AnnouncementStore(path).Current.PoiImagesEnabled);
        store.Save("off", "https://example.com", "admin", poiImagesEnabled: false);
        Assert.False(new AnnouncementStore(path).Current.PoiImagesEnabled);
    }
    [Fact]
    public void TextAndRotationEditsPreserveImageAndExplicitRemovalPersists()
    {
        var path = Path.Combine(root, "pois.json"); var store = new PoiStore(path);
        var input = new PoiStore.Poi("", "text", "Name", "Text", "#123456", 0, 0, 0, null, null, "alice", DateTimeOffset.UtcNow) { ImageKey = new string('a', 32) };
        store.TrySave(input, "alice", 10, false, out var saved, true);
        Assert.Equal(input.ImageKey, saved!.ImageKey);
        store.TrySave(saved with { ImageKey = "forged", Name = "Edited" }, "alice", 10, false, out saved);
        Assert.Equal(input.ImageKey, saved!.ImageKey);
        store.TrySave(saved with { Rotation = 60 }, "alice", 10, false, out saved); Assert.Equal(input.ImageKey, saved!.ImageKey);
        Assert.Equal(input.ImageKey, Assert.Single(new PoiStore(path).All).ImageKey);
        store.TrySave(saved with { ImageKey = null }, "alice", 10, false, out saved, true);
        Assert.Null(Assert.Single(new PoiStore(path).All).ImageKey);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
