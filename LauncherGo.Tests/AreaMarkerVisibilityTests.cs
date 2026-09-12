using ServerMap.Web;
using Xunit;
using Rect = ServerMap.Web.AreaMarkerStore.Rect;
using Edge = ServerMap.Web.AreaMarkerVisibility.Edge;

namespace LauncherGo.Tests;

public sealed class AreaMarkerVisibilityTests
{
    [Fact]
    public void PartialExplorationClipsOriginalBordersWithoutDrawingFogFrontier()
    {
        Rect[] source = [new(16, 64, 400, 256)];
        var shape = AreaMarkerVisibility.Clip(source, [new(32, 32, 320, 320)]);
        Assert.Equal(new Rect(32, 64, 320, 256), Assert.Single(shape.Rects));
        Assert.Equal(new HashSet<Edge> { new(32, 64, 320, 64), new(32, 256, 320, 256) }, shape.Borders.ToHashSet());
        Assert.Equal(new Rect(16, 64, 400, 256), source[0]);
    }

    [Fact]
    public void ExploringOnlyTheInteriorShowsFillButNoInventedBorder()
    {
        var shape = AreaMarkerVisibility.Clip([new(0, 0, 512, 512)], [new(32, 32, 320, 320)]);
        Assert.Single(shape.Rects); Assert.Empty(shape.Borders);
    }

    [Fact]
    public void AdjacentUnexploredCellsRevealNeitherAreaNorBorder()
    {
        Rect[] source = [new(-32, -32, 0, 0)];
        Assert.Empty(AreaMarkerVisibility.Clip(source, []).Rects);
        var shape = AreaMarkerVisibility.Clip(source, [new(0, -32, 32, 0)]);
        Assert.Empty(shape.Rects); Assert.Empty(shape.Borders);
        var full = AreaMarkerVisibility.Clip(source, [new(-32, -32, 0, 0)]);
        Assert.Equal(source, full.Rects); Assert.Equal(4, full.Borders.Length);
    }

    [Fact]
    public void SparseShapesKeepHolesAndDisconnectedIslands()
    {
        Rect[] source = [new(0, 0, 8, 2), new(0, 2, 2, 8), new(6, 2, 8, 8), new(2, 6, 6, 8), new(12, 12, 14, 14)];
        Rect[] explored = [new(1, 1, 7, 7), new(12, 12, 13, 13)];
        var shape = AreaMarkerVisibility.Clip(source, explored);
        var expected = Pixels(source); expected.IntersectWith(Pixels(explored));
        Assert.True(expected.SetEquals(Pixels(shape.Rects)));
        var expectedEdges = Edges(AreaMarkerVisibility.Boundary(source));
        expectedEdges.IntersectWith(Edges(AreaMarkerVisibility.Boundary(shape.Rects)));
        Assert.True(expectedEdges.SetEquals(Edges(shape.Borders)));
        Assert.DoesNotContain((3, 3), Pixels(shape.Rects));
        Assert.Contains((12, 12), Pixels(shape.Rects));
    }

    [Fact]
    public void LargeCoordinatesAndManyAdjacentExplorationCellsStayCompact()
    {
        Rect[] source = [new(-32_000_000, -32_000_000, 32_000_000, 32_000_000)];
        Rect[] explored = [new(-31_000_000, -31_000_000, -30_999_968, -30_999_968), new(31_000_000, 31_000_000, 31_000_032, 31_000_032)];
        var shape = AreaMarkerVisibility.Clip(source, explored);
        Assert.Equal(2, shape.Rects.Length); Assert.Empty(shape.Borders);
        var cells = Enumerable.Range(0, 30000).Select(x => new Rect(x * 32, 0, (x + 1) * 32, 32));
        Assert.Equal(new Rect(0, 0, 960000, 32), Assert.Single(AreaMarkerVisibility.Compact(cells)));
    }

    [Fact]
    public void RandomizedPixelOracleChecksClippingAndSharedEdgeCancellation()
    {
        var random = new Random(72631);
        for (var iteration = 0; iteration < 80; iteration++)
        {
            var source = new List<Rect>(); var explored = new List<Rect>();
            for (var x = -8; x < 8; x++) for (var z = -8; z < 8; z++)
            {
                if (random.Next(3) != 0) source.Add(new(x, z, x + 1, z + 1));
                if (random.Next(2) == 0) explored.Add(new(x, z, x + 1, z + 1));
            }
            var shape = AreaMarkerVisibility.Clip(AreaMarkerVisibility.Compact(source), AreaMarkerVisibility.Compact(explored));
            var expected = Pixels(source); expected.IntersectWith(Pixels(explored));
            Assert.True(expected.SetEquals(Pixels(shape.Rects)));
            Assert.Equal(expected.Count, shape.Rects.Sum(r => (r.MaxX - r.MinX) * (r.MaxZ - r.MinZ)));
            var expectedEdges = PixelBoundary(Pixels(source)); expectedEdges.IntersectWith(PixelBoundary(expected));
            Assert.True(expectedEdges.SetEquals(Edges(shape.Borders)));
        }
    }

    private static HashSet<(int X, int Z)> Pixels(IEnumerable<Rect> rects)
    {
        var result = new HashSet<(int, int)>();
        foreach (var r in rects) for (var x = r.MinX; x < r.MaxX; x++) for (var z = r.MinZ; z < r.MaxZ; z++) result.Add((x, z));
        return result;
    }
    private static HashSet<Edge> Edges(IEnumerable<Edge> edges)
    {
        var result = new HashSet<Edge>();
        foreach (var e in edges)
        {
            for (var x = e.X1; x < e.X2; x++) result.Add(new(x, e.Z1, x + 1, e.Z1));
            for (var z = e.Z1; z < e.Z2; z++) result.Add(new(e.X1, z, e.X1, z + 1));
        }
        return result;
    }
    private static HashSet<Edge> PixelBoundary(HashSet<(int X, int Z)> pixels)
    {
        var result = new HashSet<Edge>();
        foreach (var (x, z) in pixels)
        {
            if (!pixels.Contains((x, z - 1))) result.Add(new(x, z, x + 1, z));
            if (!pixels.Contains((x, z + 1))) result.Add(new(x, z + 1, x + 1, z + 1));
            if (!pixels.Contains((x - 1, z))) result.Add(new(x, z, x, z + 1));
            if (!pixels.Contains((x + 1, z))) result.Add(new(x + 1, z, x + 1, z + 1));
        }
        return result;
    }
}
