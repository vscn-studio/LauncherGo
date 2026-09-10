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
    [Theory] [InlineData(3)] [InlineData(5)] [InlineData(6)] [InlineData(8)] [InlineData(11)] [InlineData(13)] [InlineData(14)]
    public void OtherBandsAreRejected(int min) => Assert.Throws<ArgumentException>(() => Store().Save(0, null, "Name", "#abcdef", min, [R(0, 0, 1, 1)]));
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
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
