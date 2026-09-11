using ServerMap.Web;
using Xunit;

namespace LauncherGo.Tests;

public sealed class MapManagementTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "map-management-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public void DefaultsAndLegacyAnnouncementArePreserved()
    {
        var store = new AnnouncementStore(Path.Combine(root, "announcement.json"));
        Assert.Null(store.Current.Management);
        var config = new MapManagementSettings();
        Assert.Equal(10, config.ImageMaxMb); Assert.Equal(10, config.PoiQuota); Assert.Equal(0, config.DailyTeleports);
        Assert.True(config.Layer("players").DefaultVisible);
        store.Save("news", "https://example.com", "admin", management: config with { ImageMaxMb = 20, Layers = new() { ["mounts"] = new(Forced: true, Scale: 2) } });
        store.Save("legacy save", "https://example.com", "admin");
        var loaded = new AnnouncementStore(Path.Combine(root, "announcement.json")).Current.Management!;
        Assert.Equal(20, loaded.ImageMaxMb); Assert.True(loaded.Layer("mounts").Forced); Assert.Equal(2, loaded.Layer("mounts").Scale);
    }
    [Fact]
    public void RejectsInvalidPolicies()
    {
        Assert.Throws<ArgumentException>(() => (new MapManagementSettings { ImageTypes = ["../gif"] }).Validate());
        Assert.Throws<ArgumentException>(() => (new MapManagementSettings { ImageMaxMb = 51 }).Validate());
        Assert.Throws<ArgumentException>(() => (new MapManagementSettings { Layers = new() { ["players"] = new(Forbidden:true, Forced:true) } }).Validate());
        Assert.Throws<ArgumentException>(() => (new MapManagementSettings { Layers = new() { ["players"] = new(Scale: .09) } }).Validate());
        Assert.Throws<ArgumentException>(() => (new MapManagementSettings { Layers = new() { ["players"] = new(Scale: 10.1) } }).Validate());
    }
    [Fact]
    public void ImageTypeListCanBeExtendedWithoutHardcodedFormats()
    {
        var settings = new MapManagementSettings { ImageTypes = [".JPG", "jpeg", "GIF", "apng", "avif"] };
        Assert.Equal(new[] { "jpeg", "gif", "png", "avif" }, settings.Validate().ImageTypes);
        Assert.Throws<ArgumentException>(() => (settings with { ImageTypes = [] }).Validate());
        Assert.Throws<ArgumentException>(() => (settings with { ImageTypes = ["image/gif"] }).Validate());
    }
    [Fact]
    public void QuotaPersistsAndFailedTransfersRefundWholeParty()
    {
        var path = Path.Combine(root, "quota.json"); var quota = new DailyTeleportQuota(path);
        using (quota.Reserve(["alice","bob"], 1, out _)) { Assert.False(quota.Available("alice",1)); }
        Assert.True(quota.Available("alice",1)); Assert.True(quota.Available("bob",1));
        using (quota.Reserve(["alice","alice"], 1, out var commit)) commit();
        quota = new(path); Assert.False(quota.Available("alice",1)); Assert.True(quota.Available("alice",0));
        Assert.Throws<InvalidOperationException>(() => quota.Reserve(["bob","alice"], 1, out _));
        Assert.True(quota.Available("bob",1));
    }
    [Fact]
    public void TracksPersistSearchAndCloseAfterRestartWithoutResuming()
    {
        var dir = Path.Combine(root, "tracks"); var store = new PlayerTrackStore(dir);
        var track = store.Start("alice","Alice","admin",3600);
        Assert.Throws<InvalidOperationException>(() => store.Start("alice","Alice","admin",30));
        Assert.Throws<ArgumentException>(() => store.Start("bob","Bob","admin",86401));
        for(var i=0;i<10;i++)store.Append(track.Id,new(track.Started.AddSeconds(i),i,10,20,0,new("Elk",i,10,20,0)));
        Assert.Equal(10,store.Get(track.Id)!.Samples.Count);
        Assert.Equal(4,store.Get(track.Id,track.Started.AddSeconds(5))!.Samples.Count);
        Assert.Single(store.List("alice")); Assert.Empty(store.List("unknown"));
        var loaded = new PlayerTrackStore(dir).Get(track.Id)!;
        Assert.Equal("server-restarted",loaded.EndReason); Assert.NotNull(loaded.Ended);
        Assert.Equal("Elk",loaded.Samples[0].Mount!.Name);
    }
    [Fact]
    public void HistoricalMountImagesRequireTrackReferenceAndValidKey()
    {
        var dir=Path.Combine(root,"tracks");var store=new PlayerTrackStore(dir);var track=store.Start("alice","Alice","admin",60);var key=new string('a',64);
        store.SaveMountImage(key,[1,2,3]);Assert.Null(store.MountImage(track.Id,key));
        store.Append(track.Id,new(track.Started,0,0,0,0,new("Elk",0,0,0,0){ImageKey=key}));
        Assert.NotNull(store.MountImage(track.Id,key));Assert.Null(store.MountImage("unknown",key));Assert.Null(store.MountImage(track.Id,"../private"));
    }
    [Fact]
    public void ExpiredTrackingStopsWithoutAppendingLateSample()
    {
        var store=new PlayerTrackStore(Path.Combine(root,"tracks"));var track=store.Start("alice","Alice","admin",1);
        store.Append(track.Id,new(track.Started.AddSeconds(2),0,0,0,0,null));
        Assert.Empty(store.Active);Assert.Empty(store.Get(track.Id)!.Samples);Assert.Equal("duration",store.Get(track.Id)!.EndReason);
    }
    [Fact]
    public void ImageAttributionSurvivesTextEditAndRemovalKeepsMarker()
    {
        var store = new PoiStore(Path.Combine(root,"pois.json"));
        var point = new PoiStore.Poi("","text","Test","Description","#112233",0,10,20,null,null,"owner",DateTimeOffset.UtcNow) { ImageKey = new string('a',32), ImageAddedBy = "Uploader", ImageAddedByUid = "uploader" };
        store.TrySave(point,"owner",10,false,out var saved,true);
        store.TrySave(saved! with { Name = "Edited", ImageAddedBy = "Spoofed" },"owner",10,false,out saved);
        Assert.Equal("Uploader",saved!.ImageAddedBy);
        store.TrySave(saved with { ImageKey=null,ImageAddedBy=null,ImageAddedByUid=null },"admin",10,true,out saved,true);
        Assert.Null(saved!.ImageKey); Assert.Equal("Edited",Assert.Single(store.All).Name);
    }
    [Fact]
    public void ScaleOnlyAppliesToPlayersMountsAndTranslocators()
    {
        var settings = new MapManagementSettings { Layers = MapManagementSettings.LayerIds.ToDictionary(id => id, _ => new MapManagementSettings.LayerRule(Scale: 3)) };
        foreach (var id in MapManagementSettings.LayerIds)
        {
            var expected = id is "players" or "mounts" or "translocators" ? 3 : 1;
            Assert.Equal(expected, settings.Layer(id).Scale);
            Assert.Equal(expected, settings.Validate().Layers[id].Scale);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeletedTracksNeverResumeOrReappear(bool stopFirst)
    {
        var dir = Path.Combine(root, "tracks"); var store = new PlayerTrackStore(dir);
        var track = store.Start("alice", "Alice", "admin", 60); var key = new string('a', 64);
        store.SaveMountImage(key, [1]);
        store.Append(track.Id, new(track.Started, 0, 0, 0, 0, new("Elk", 0, 0, 0, 0) { ImageKey = key }));
        if (stopFirst) store.Stop(track.Id, "manual");
        Assert.False(store.Remove("../private")); Assert.False(store.Remove(new string('b', 32)));
        Assert.True(store.Remove(track.Id)); Assert.False(store.Remove(track.Id));
        store.Append(track.Id, new(track.Started.AddSeconds(1), 1, 0, 0, 0, null));
        Assert.Null(store.Get(track.Id)); Assert.Empty(store.Active); Assert.Empty(store.List(null));
        Assert.Null(store.MountImage(track.Id, key)); Assert.False(File.Exists(Path.Combine(dir, track.Id + ".json")));
        Assert.Empty(new PlayerTrackStore(dir).List(null));
    }
    public void Dispose() { if(Directory.Exists(root))Directory.Delete(root,true); }
}
