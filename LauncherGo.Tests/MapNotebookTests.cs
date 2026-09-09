using System.Text.Json;
using ServerMap.Render;
using ServerMap.Web;
using Xunit;

namespace LauncherGo.Tests;

public sealed class MapNotebookTests : IDisposable
{
    [Theory]
    [InlineData(false, null, true)]
    [InlineData(false, "0", true)]
    [InlineData(false, "1", true)]
    [InlineData(true, null, false)]
    [InlineData(true, "0", false)]
    [InlineData(true, "1", true)]
    public void AdminPreviewAddsTileMaskingWithoutAllowingGuestBypass(bool admin, string? preview, bool expected) =>
        Assert.Equal(expected, MapVisibility.ShouldMaskTiles(admin, preview));
    private readonly string root = Path.Combine(Path.GetTempPath(), "launchergo-notebook-" + Guid.NewGuid().ToString("N"));
    private MapNotebookStore Store() => new(Path.Combine(root, "notebook.json"));
    private static double[][] Points => [[10, 20], [30, 40], [50, 60]];
    [Fact]
    public void WaypointSharesAreImmutableOwnerCheckedPersistedAndRevokedOnSourceDeletion()
    {
        var path=Path.Combine(root,"waypoint-shares.json");var store=new WaypointShareStore(path);
        var marker=new GameWaypointSnapshot.Marker("a","alice","Copper","ore","pick","#aabbcc",10,90,20,true);
        Assert.Throws<UnauthorizedAccessException>(()=>store.Create("bob",marker));
        var id=store.Create("alice",marker);Assert.Equal(id,store.Create("alice",marker));
        Assert.Equal(marker,new WaypointShareStore(path).Get(id));
        var edited=marker with {Name="Changed"};Assert.NotEqual(id,store.Create("alice",edited));
        store.Prune([edited]);Assert.Equal("Copper",store.Get(id)!.Name);
        store.Prune([marker with {OwnerUid="bob"}]);Assert.Null(store.Get(id));Assert.Null(new WaypointShareStore(path).Get(id));
    }
    [Fact]
    public async Task TimedOutGameThreadCallsCannotMutateLaterAndStartedCallsReportActualResult()
    {
        var count=0;var queued=new GameThreadCall<int>(()=>++count);
        Assert.True(queued.CancelPending());queued.Run();Assert.Equal(0,count);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await queued.Task);
        var running=new GameThreadCall<int>(()=>++count);running.Run();running.Run();
        Assert.False(running.CancelPending());Assert.Equal(1,await running.Task);Assert.Equal(1,count);
    }
    [Fact]
    public void SearchIncludesOwnMarkerTextIconsRoutesAndAdminRegionsWithoutLeakingOtherOwners()
    {
        var store = Store(); var snapshot = new GameWaypointSnapshot();
        snapshot.Replace([new("a", "alice", "果树", "apple orchard", "apple", "#ffffff", 10, 90, 20, false),
            new("b", "bob", "果树", "apple orchard", "apple", "#ffffff", 10, 90, 20, false)]);
        var route = store.Save("alice", null, "APPLE route", "#ffffff", Points);
        store.Save("bob", null, "APPLE route", "#ffffff", Points);
        Assert.Equal(new[] {"a", route.Id}, NotebookSearch.Find("apple", "alice", false, snapshot, store).Select(r => r.id));
        Assert.Empty(NotebookSearch.Find("apple", null, false, snapshot, store));
        store.SaveRegion(null, "apple secret", 20, 25, 40, 45); // Segment crosses fog; starting vertex is outside.
        Assert.Single(NotebookSearch.Find("apple", "alice", false, snapshot, store));
        var admin = NotebookSearch.Find("apple", "alice", true, snapshot, store).ToArray();
        Assert.Equal(new[] {"waypoint", "route", "hidden-region"}, admin.Select(r => r.kind));
        Assert.DoesNotContain(admin, r => r.id == "b");
        Assert.Empty(NotebookSearch.Find("   ", "alice", true, snapshot, store));
    }
    [Fact]
    public void OriginalIconGeometryIsPersistedWithoutExecutableContent()
    {
        var path = Path.Combine(root, "icons"); var store = new WaypointIconStore(path);
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 20 20\" onload=\"alert(1)\"><script>alert(1)</script><path d=\"M0 0 L20 20\"/><image href=\"https://example.com/x\"/><use href=\"#shape\"/><use href=\"https://example.com/s.svg#shape\"/></svg>";
        store.Put("pick", System.Text.Encoding.UTF8.GetBytes(svg));
        var clean = System.Text.Encoding.UTF8.GetString(new WaypointIconStore(path).Get("pick")!);
        Assert.Contains("M0 0 L20 20", clean); Assert.Contains("viewBox=\"0 0 20 20\"", clean);
        Assert.Contains("href=\"#shape\"", clean); Assert.DoesNotContain("script", clean); Assert.DoesNotContain("onload", clean); Assert.DoesNotContain("example.com", clean);
        var copy = store.Get("pick")!; copy[0] = 0; Assert.Equal((byte)'<', store.Get("pick")![0]);
    }
    [Fact]
    public void IconNamesSizeAndExternalEntitiesAreRejected()
    {
        var store = new WaypointIconStore(Path.Combine(root, "icons"));
        Assert.Throws<ArgumentException>(() => store.Put("../pick", [1]));
        Assert.Throws<ArgumentException>(() => store.Put("pick", new byte[WaypointIconStore.MaxBytes + 1]));
        Assert.Throws<System.Xml.XmlException>(() => store.Put("pick", System.Text.Encoding.UTF8.GetBytes("<!DOCTYPE svg [<!ENTITY x SYSTEM 'file:///forbidden'>]><svg xmlns='http://www.w3.org/2000/svg'>&x;</svg>")));
    }
    [Fact]
    public void RoutesArePrivatePersistedAndCannotBeOverwrittenByOtherPlayers()
    {
        var store = Store(); var route = store.Save("alice", null, "Home", "#112233", Points);
        Assert.Empty(store.ForOwner("bob")); Assert.Single(Store().ForOwner("alice"));
        Assert.Throws<UnauthorizedAccessException>(() => store.Save("bob", route.Id, "Stolen", "#ffffff", Points));
        Assert.Throws<UnauthorizedAccessException>(() => store.ShareRoute("bob", route.Id));
        Assert.False(store.Remove("bob", route.Id));
    }
    [Fact]
    public void SharingUsesSnapshotAndImportCreatesIndependentOwnedCopy()
    {
        var store = Store(); var original = store.Save("alice", null, "Original", "#112233", Points);
        var id = store.ShareRoute("alice", original.Id); Assert.Equal(id, store.ShareRoute("alice", original.Id));
        store.Save("alice", original.Id, "Edited", "#445566", [[0, 0], [1, 1]]);
        var snapshot = Store().Shared(id)!; Assert.Equal("Original", snapshot.Name); Assert.Equal(3, snapshot.Points.Length);
        var imported = store.Save("bob", null, snapshot.Name, snapshot.Color, snapshot.Points);
        Assert.NotEqual(original.Id, imported.Id); Assert.Equal("bob", imported.OwnerUid);
        Assert.True(store.Remove("alice", original.Id)); Assert.Null(store.Shared(id)); Assert.Single(store.ForOwner("bob"));
    }
    [Fact]
    public void StoreDoesNotExposeMutableCoordinateArrays()
    {
        var store = Store(); var input = Points; var result = store.Save("alice", null, "Route", "bad", input);
        input[0][0] = 999; result.Points[0][0] = 888; store.ForOwner("alice")[0].Points[0][0] = 777;
        Assert.Equal(10, store.ForOwner("alice")[0].Points[0][0]); Assert.Equal("#ffd000", result.Color);
    }
    [Fact]
    public void CoordinatesAndQuotasAreBounded()
    {
        var store = Store();
        Assert.Throws<ArgumentException>(() => store.Save("a", null, "R", "#fff", [[double.NaN, 0], [0, 0]]));
        Assert.Throws<ArgumentException>(() => store.Save("a", null, "R", "#fff", [[0, 0]]));
        Assert.Throws<ArgumentException>(() => store.Save("a", null, "R", "#fff", Enumerable.Range(0, 513).Select(_ => new double[] {0, 0}).ToArray()));
        for (var i = 0; i < 100; i++) store.Save("a", null, "R", "#ffffff", Points);
        Assert.Throws<InvalidOperationException>(() => store.Save("a", null, "R", "#ffffff", Points));
        Assert.Single(store.ForOwner("b").Append(store.Save("b", null, "R", "#ffffff", Points)));
    }
    [Fact]
    public void HiddenRegionsNormalizePersistEditAndRemove()
    {
        var store = Store(); var region = store.SaveRegion(null, "Fog", 100, 100, -10, -20);
        Assert.Equal(-10, region.MinX); Assert.Single(Store().Regions);
        store.SaveRegion(region.Id, "Changed", 10, 20, 30, 40); Assert.Equal("Changed", Store().Regions[0].Name);
        Assert.True(store.RemoveRegion(region.Id)); Assert.Empty(Store().Regions);
        Assert.Throws<ArgumentException>(() => store.SaveRegion(null, "Bad", 0, 0, double.PositiveInfinity, 1));
        Assert.Throws<ArgumentException>(() => store.SaveRegion(null, "Bad", 0, 0, 0, 1));
    }
    [Fact]
    public void BrokenFogConfigurationFailsClosed()
    {
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root,"notebook.json"), "broken");
        Assert.Throws<JsonException>(() => Store());
    }
    [Fact]
    public void WaypointSnapshotsReturnOnlyOwnersExactData()
    {
        var snapshot = new GameWaypointSnapshot();
        var marker = new GameWaypointSnapshot.Marker("id", "alice", "Copper", "ore", "pick", "#a1b2c3", 100.25, 90, -123.75, true);
        snapshot.Replace([marker, marker with {OwnerUid="bob", Id="other"}]);
        Assert.Equal(marker, Assert.Single(snapshot.ForOwner("alice"))); Assert.Empty(snapshot.ForOwner("unknown"));
        snapshot.Replace([]); Assert.Empty(snapshot.ForOwner("alice"));
    }
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, -1, -1)]
    [InlineData(8, 0, 0)]
    [InlineData(12, -1, 0)]
    public void FogMasksUnderlyingPngAtEveryZoomIncludingNegativeTiles(int zoom, int tileX, int tileZ)
    {
        var pixels = new byte[512*512*4]; for(var i=0;i<pixels.Length;i+=4){pixels[i]=30;pixels[i+1]=60;pixels[i+2]=90;pixels[i+3]=255;}
        var scale = Math.Pow(2, zoom); var x=tileX*512*scale; var z=tileZ*512*scale;
        var region = new MapNotebookStore.Region("fog","",x+10.5*scale,z+20.5*scale,x+12.5*scale,z+22.5*scale);
        var decoded=PngEncoder.Decode(MapVisibility.MaskTile(PngEncoder.Encode(512,512,pixels),zoom,tileX,tileZ,[region]));
        Assert.Equal(new byte[]{0,0,0,0},decoded.AsSpan((20*512+10)*4,4).ToArray());
        Assert.Equal(new byte[]{0,0,0,0},decoded.AsSpan((22*512+12)*4,4).ToArray());
        Assert.Equal(new byte[]{30,60,90,255},decoded.AsSpan((5*512+5)*4,4).ToArray());
    }
    [Fact]
    public void CrossingRoutesAndFeaturesDoNotLeakHiddenEndpoints()
    {
        var regions=new[]{new MapNotebookStore.Region("fog","",10,10,20,20)};
        Assert.False(MapVisibility.Visible(regions,10,10)); Assert.True(MapVisibility.Visible(regions,0,0));
        Assert.False(MapVisibility.RouteVisible(regions,[[0,15],[30,15]]));
        Assert.False(MapVisibility.GeometryVisible(regions,JsonSerializer.SerializeToElement(new[]{new double[]{0,15},new double[]{30,15}})));
        Assert.True(MapVisibility.RouteVisible(regions,[[0,0],[5,5]]));
    }
    [Theory]
    [InlineData(512556, 512432, 513609, 507194)]
    [InlineData(512053, 511711, 512981, 512923)]
    [InlineData(512800, 512400, 513000, 512400)]
    [InlineData(512900, 512400, 513000, 512400)]
    public void TranslocatorLinksIgnoreHiddenRegions(double x, double z, double targetX, double targetZ)
    {
        var regions = new[] { new MapNotebookStore.Region("hidden", "", 512831, 512346, 512985, 512521) };
        var feature = TranslocatorFeature(x, z, targetX, targetZ);
        Assert.False(MapVisibility.GeometryVisible(regions, feature.GetProperty("geometry").GetProperty("coordinates")));
        Assert.True(MapVisibility.FeatureVisible(regions, feature));
        Assert.True(MapVisibility.TranslocatorVisible(regions, x, z, targetX, targetZ));
        Assert.True(MapVisibility.FeatureVisible(regions, TranslocatorFeature(targetX, targetZ, x, z)));
    }

    [Fact]
    public void TranslocatorExceptionDoesNotRelaxOtherFeaturePrivacy()
    {
        var regions = new[] { new MapNotebookStore.Region("hidden", "", 10, 10, 20, 20) };
        var feature = JsonSerializer.SerializeToElement(new
        {
            geometry = new { type = "LineString", coordinates = new[] { new[] { 0, 15 }, new[] { 30, 15 } } },
            properties = new { kind = "route" }
        });
        Assert.False(MapVisibility.FeatureVisible(regions, feature));
        Assert.True(MapVisibility.FeatureVisible([], TranslocatorFeature(0, 15, 30, 15)));
    }

    [Fact]
    public void TranslocatorLinksHideCrossRegionLinesAndKeepVisibleEndpoint()
    {
        var regions = new[] { new MapNotebookStore.Region("hidden", "", 10, 10, 20, 20) };
        Assert.True(MapVisibility.TranslocatorVisible(regions, 15, 15, 30, 15));
        Assert.False(MapVisibility.TranslocatorLineVisible(regions, 15, 15, 30, 15));
        Assert.False(MapVisibility.TranslocatorVisible(regions, 12, 12, 18, 18));
        Assert.False(MapVisibility.TranslocatorLineVisible(regions, 12, 12, 18, 18));
        Assert.False(MapVisibility.Visible(regions, 12, 12));
        Assert.True(MapVisibility.Visible(regions, 30, 15));
        Assert.True(MapVisibility.TranslocatorLineVisible(regions, 0, 0, 30, 30));
    }

    private static JsonElement TranslocatorFeature(double x, double z, double targetX, double targetZ) =>
        JsonSerializer.SerializeToElement(new
        {
            geometry = new { type = "LineString", coordinates = new[] { new[] { x, z }, new[] { targetX, targetZ } } },
            properties = new { kind = "translocator" }
        });

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root,true); }
}
