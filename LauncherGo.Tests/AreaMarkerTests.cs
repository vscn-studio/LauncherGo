using ServerMap.Web;
using Xunit;

namespace LauncherGo.Tests;

public sealed class AreaMarkerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "area-marker-tests-" + Guid.NewGuid().ToString("N"));
    private AreaMarkerStore Store() => new(Path.Combine(root, "areas.json"));
    private static AreaMarkerStore.Rect R(int x1, int z1, int x2, int z2) => new(x1, z1, x2, z2);
    private static long Area(AreaMarkerStore.Marker marker) => marker.Rects.Sum(r => (long)(r.MaxX - r.MinX) * (r.MaxZ - r.MinZ));
    [Fact] public void AdjacentMarkersDoNotOverlapAndExistingGeometryIsUnchanged()
    {
        var store = Store(); var first = store.Save(0, null, "First", "#abcdef", 4, [R(0, 0, 10, 10)]);
        var second = store.Save(1, null, "Next", "#123456", 4, [R(5, 0, 20, 10)]);
        Assert.Equal(100, Area(first)); Assert.Equal(100, Area(second));
        Assert.Equal(first.Rects, store.Read().Markers.First(m => m.Id == first.Id).Rects);
        Assert.DoesNotContain(second.Rects, r => first.Rects.Any(f => AreaMarkerStore.Intersects(r, f)));
        var nested = store.Save(2, null, "Other band", "#abcdef", 7, [R(0, 0, 10, 10)]);
        Assert.Equal(100, Area(nested));
        var reload = Store().Read(); Assert.Equal(3, reload.Revision); Assert.Equal(3, reload.Markers.Length);
    }
    [Fact] public void UnionDoesNotDoublePaintAndSupportsHolesAndNegativePixels()
    {
        var store = Store(); var m = store.Save(0, null, "Union", "#abcdef", 10, [R(-10, -10, 0, 0), R(-5, -5, 5, 5)]);
        Assert.Equal(175, Area(m));
        for (var i = 0; i < m.Rects.Length; i++) for (var j = i + 1; j < m.Rects.Length; j++) Assert.False(AreaMarkerStore.Intersects(m.Rects[i], m.Rects[j]));
        var hole = store.Save(1, m.Id, "Hole", "#abcdef", 10, [R(0, 0, 10, 3), R(0, 7, 10, 10), R(0, 3, 3, 7), R(7, 3, 10, 7)]);
        Assert.Equal(84, Area(hole)); Assert.DoesNotContain(hole.Rects, r => AreaMarkerStore.Intersects(r, R(3, 3, 7, 7)));
    }
    [Fact] public void ConflictsAndEmptySelectionsDoNotChangeStoredState()
    {
        var store = Store(); var m = store.Save(0, null, "First", "#abcdef", 12, [R(0, 0, 10, 10)]);
        Assert.Throws<InvalidOperationException>(() => store.Save(0, m.Id, "stale", "#abcdef", 12, [R(5, 5, 6, 6)]));
        Assert.Throws<InvalidOperationException>(() => store.Remove(0, m.Id));
        Assert.Throws<ArgumentException>(() => store.Save(1, null, "occupied", "#abcdef", 12, [R(5, 5, 6, 6)]));
        Assert.Equal(1, store.Read().Revision); Assert.Equal(100, Area(store.Read().Markers.Single()));
        Assert.True(store.Remove(1, m.Id)); Assert.Empty(Store().Read().Markers);
    }
    [Theory] [InlineData(4,6)] [InlineData(7,9)] [InlineData(10,11)] [InlineData(12,13)]
    public void FourBandsHaveExactLimits(int min, int max) => Assert.Equal(max, AreaMarkerStore.MaxZoom(min));
    [Theory] [InlineData(3)] [InlineData(16)]
    public void OutOfRangeLevelsAreRejected(int min) => Assert.Throws<ArgumentException>(() => Store().Save(0, null, "Name", "#abcdef", min, [R(0, 0, 1, 1)]));
    [Fact] public void CustomIntervalsExcludeEveryNeighbourSharingAnyVisibleLevel()
    {
        var store=Store();var first=store.Save(0,null,"First","#abcdef",7,[R(0,0,10,10)],maxZoom:10);
        var next=store.Save(1,null,"Next","#abcdef",10,[R(5,0,20,10)],maxZoom:13);
        Assert.Equal(100,Area(next));Assert.All(next.Rects,r=>Assert.True(r.MinX>=10));
        var separate=store.Save(2,null,"Separate","#abcdef",11,[R(0,0,10,10)],maxZoom:15);
        Assert.Equal(100,Area(separate));
        Assert.Throws<ArgumentException>(()=>store.Save(3,null,"Bad","#abcdef",8,[R(50,50,60,60)],maxZoom:7));
        Assert.Throws<ArgumentException>(()=>store.Save(3,null,"Bad","#abcdef",4,[R(50,50,60,60)],maxZoom:16));
        var single=store.Save(3,null,"Single","#abcdef",5,[R(0,0,10,10)],maxZoom:5);
        Assert.Equal(5,Store().Read().Markers.Single(m=>m.Id==single.Id).MaxZoom);
        store.Save(4,first.Id,"Old client edit","#abcdef",7,first.Rects);
        Assert.Equal(10,Store().Read().Markers.Single(m=>m.Id==first.Id).MaxZoom);
        Assert.Throws<ArgumentException>(()=>store.Merge(5,[first.Id,separate.Id],"Bad parent","#abcdef",4,maxZoom:7));
        var merged=store.Merge(5,[first.Id,separate.Id],"Parent","#abcdef",6,maxZoom:6);
        Assert.Equal(6,merged.MaxZoom);Assert.Equal(100,Area(merged));
    }
    [Theory] [InlineData(4,6)] [InlineData(7,9)] [InlineData(10,11)] [InlineData(12,13)]
    public void LegacySerializedRangesArePreserved(int min,int max)
    {
        Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"areas.json"),System.Text.Json.JsonSerializer.Serialize(new{Revision=0,Markers=new[]{new{Id="old",Name="Old",Color="#abcdef",MinZoom=min,Rects=new[]{R(0,0,1,1)}}}}));
        var store=Store();Assert.Equal(max,store.Read().Markers.Single().MaxZoom);
        store.Save(0,"old","Rename","#abcdef",min,[R(0,0,1,1)]);
        Assert.Equal(max,Store().Read().Markers.Single().MaxZoom);
    }
    [Fact] public void InputsAndGeometryAreBoundedAndSnapshotsCannotMutateStore()
    {
        var store = Store();
        Assert.Throws<ArgumentException>(() => store.Save(0, null, " ", "#abcdef", 4, [R(0,0,1,1)]));
        Assert.Throws<ArgumentException>(() => store.Save(0, null, "Name", "red", 4, [R(0,0,1,1)]));
        Assert.Throws<ArgumentException>(() => store.Save(0, null, "Name", "#abcdef", 4, [R(0,0,0,1)]));
        Assert.Throws<ArgumentException>(() => store.Save(0, null, "Name", "#abcdef", 4, [R(-32000001,0,1,1)]));
        Assert.Throws<ArgumentException>(() => store.Save(0, null, "Name", "#abcdef", 4, Enumerable.Repeat(R(0,0,1,1),1025).ToArray()));
        var rects = new[] { R(0,0,1,1) }; var saved = store.Save(0,null,"  Pixel  ","#ABCDEF",4,rects);
        rects[0]=R(10,10,20,20); saved.Rects[0]=R(10,10,20,20); store.Read().Markers[0].Rects[0]=R(10,10,20,20);
        Assert.Equal(1,Area(store.Read().Markers.Single())); Assert.Equal("Pixel",store.Read().Markers.Single().Name);
        Assert.Equal("#abcdef",store.Read().Markers.Single().Color);
    }
    [Fact] public void HugeWorldAreaUsesOnlySelectedRectangles()
    {
        var marker=Store().Save(0,null,"Large region","#abcdef",4,[R(-1000000,-1000000,1000000,1000000)]);
        Assert.Single(marker.Rects); Assert.Equal(4_000_000_000_000L,Area(marker));
    }
    [Fact] public void MergeMixedBandsOnlyCreatesHigherLevelsAndPreservesOriginals()
    {
        var store=Store();var a=store.Save(0,null,"Town","#abcdef",12,[R(0,0,10,10)]);
        var b=store.Save(1,null,"District","#abcdef",10,[R(5,5,15,15)]);
        var style=new AreaMarkerStore.Appearance(.4,.2,.7);
        foreach(var invalid in new[]{10,12}) Assert.Throws<ArgumentException>(()=>store.Merge(2,[a.Id,b.Id],"Invalid","#abcdef",invalid));
        Assert.Throws<ArgumentException>(()=>store.Merge(2,[a.Id,a.Id],"Duplicate","#abcdef",7));
        Assert.Throws<KeyNotFoundException>(()=>store.Merge(2,[a.Id,"missing"],"Missing","#abcdef",7));
        Assert.Throws<InvalidOperationException>(()=>store.Merge(0,[a.Id,b.Id],"Stale","#abcdef",7));
        var merged=store.Merge(2,[a.Id,b.Id],"Province","#abcdef",7,style);
        Assert.Equal(175,Area(merged));Assert.Equal(style,Store().Read().Markers.Single(m=>m.Id==merged.Id).Style);
        Assert.Equal(a.Rects,store.Read().Markers.Single(m=>m.Id==a.Id).Rects);Assert.Equal(b.Rects,store.Read().Markers.Single(m=>m.Id==b.Id).Rects);
        var top=store.Merge(3,[a.Id,b.Id,merged.Id],"Country","#abcdef",4);
        Assert.Equal(175,Area(top));Assert.Throws<ArgumentException>(()=>store.Merge(4,[a.Id,top.Id],"No parent","#abcdef",4));
    }
    [Fact] public void OpacityDefaultsSurviveLegacyDataAndInvalidSettingsAreRejected()
    {
        Directory.CreateDirectory(root);var path=Path.Combine(root,"areas.json");
        File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(new{Revision=1,Markers=new[]{new{Id="legacy",Name="Old",Color="#abcdef",MinZoom=12,Rects=new[]{R(0,0,1,1)}}}}));
        var store=Store();Assert.Equal(new AreaMarkerStore.Appearance(.25,.08,.58),store.Read().Markers.Single().Style);
        var style=new AreaMarkerStore.Appearance(0,1,.45);store.Save(1,"legacy","Old","#abcdef",12,[R(0,0,1,1)],style);
        store.Save(2,"legacy","Name only","#abcdef",12,[R(0,0,1,1)]);Assert.Equal(style,Store().Read().Markers.Single().Style);
        foreach(var bad in new[]{new AreaMarkerStore.Appearance(-.1),new AreaMarkerStore.Appearance(1.1),new AreaMarkerStore.Appearance(.2,double.NaN),new AreaMarkerStore.Appearance(.2,.3,double.PositiveInfinity)})
            Assert.Throws<ArgumentException>(()=>store.Save(3,"legacy","Invalid","#abcdef",12,[R(0,0,1,1)],bad));
        Assert.Equal(3,store.Read().Revision);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
