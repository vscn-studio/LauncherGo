using ServerMap.Web;
using Xunit;

namespace LauncherGo.Tests;

public sealed class RoadIndexTests
{
    private static RoadIndex.Cell Cell(int x, int z, string code = "game:stonepath-free", double speed = 1.3) =>
        new(x, z, 64, code, speed);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void TurnsAndBranchesStayConnected(int width)
    {
        var cells = new Dictionary<(int, int), RoadIndex.Cell>();
        for (var x = 0; x < 24; x++) for (var z = 0; z < width; z++) cells[(x, z)] = Cell(x, z);
        for (var z = 0; z < 24; z++) for (var x = 0; x < width; x++) cells[(x, z)] = Cell(x, z);
        for (var z = -16; z < 0; z++) for (var x = 12; x < 12 + width; x++) cells[(x, z)] = Cell(x, z);
        var segments = RoadIndex.BuildSegments(cells.Values, null, 1.3, 1.3);
        var edges = new Dictionary<(double X, double Z), HashSet<(double X, double Z)>>();
        foreach (var segment in segments)
            for (var i = 1; i < segment.Coordinates.Length; i++)
            {
                var a = segment.Coordinates[i - 1]; var b = segment.Coordinates[i];
                var from = (a[0], a[1]); var to = (b[0], b[1]);
                if (!edges.ContainsKey(from)) edges[from] = [];
                if (!edges.ContainsKey(to)) edges[to] = [];
                edges[from].Add(to); edges[to].Add(from);
                Assert.Equal(64d, a[2]); Assert.Equal(64d, b[2]);
            }
        Assert.NotEmpty(edges);
        var seen = new HashSet<(double X, double Z)>();
        var queue = new Queue<(double X, double Z)>(); queue.Enqueue(edges.Keys.First());
        while (queue.TryDequeue(out var point))
        {
            if (!seen.Add(point)) continue;
            foreach (var next in edges[point]) queue.Enqueue(next);
        }
        Assert.Equal(edges.Count, seen.Count);
        Assert.Contains(seen, point => point.X >= 23);
        Assert.Contains(seen, point => point.Z >= 23);
        Assert.Contains(seen, point => point.Z <= -15);
    }

    [Fact]
    public void FullNetworkKeepsVisibleRoadsWithoutBridgingHiddenCells()
    {
        var cells = Enumerable.Range(0, 20).Select(x => Cell(x, 0)).ToArray();
        var segments = RoadIndex.BuildSegments(cells, null, 1.3, 1.3, (x, z) => x < 5 || x >= 8 && x < 12);
        Assert.Equal(2, segments.Count);
        Assert.Contains(segments, segment => segment.Coordinates.All(point => point[0] < 5));
        Assert.Contains(segments, segment => segment.Coordinates.All(point => point[0] >= 8 && point[0] < 12));
        Assert.Empty(RoadIndex.BuildSegments(cells, null, 1.3, 1.3, (_, _) => false));
    }

    [Fact]
    public void EvenWidthTurnSharesAnExactCornerNode()
    {
        var cells = new List<RoadIndex.Cell>();
        for (var x = 0; x <= 8; x++) for (var z = 0; z <= 1; z++) cells.Add(Cell(x, z));
        for (var z = 2; z <= 8; z++) for (var x = 0; x <= 1; x++) cells.Add(Cell(x, z));

        var segments = RoadIndex.BuildSegments(cells, null, 1.3, 1.3);

        var segment = Assert.Single(segments);
        Assert.Contains(segment.Coordinates, point => point[0] == 1d && point[1] == 1d);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThreeBlockTurnKeepsBothEnds(bool transpose)
    {
        var cells = new[] { Cell(0, 0), Cell(1, 0), Cell(1, 1) }
            .Select(cell => transpose ? cell with { X = cell.Z, Z = cell.X } : cell).ToArray();
        var segment = Assert.Single(RoadIndex.BuildSegments(cells, null, 1.3, 1.3));
        Assert.Contains(segment.Coordinates, point => point[0] == .5 && point[1] == .5);
        Assert.Contains(segment.Coordinates, point => point[0] == 1.5 && point[1] == 1.5);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void SingleBlockLongBranchIsNotSwallowedByWideJunction(int width)
    {
        foreach (var transpose in new[] { false, true })
        {
            var cells = new List<RoadIndex.Cell>();
            for (var x = -24; x < 24; x++) for (var z = 0; z < width; z++) cells.Add(Cell(x, z));
            for (var x = 0; x < width; x++) cells.Add(Cell(x, -1));
            if (transpose) cells = cells.Select(cell => cell with { X = cell.Z, Z = cell.X }).ToList();
            var segments = RoadIndex.BuildSegments(cells, null, 1.3, 1.3);
            Assert.Equal(3, segments.Count);
            AssertConnected(segments);
            AssertOnRoad(segments, cells);
            Assert.Contains(segments.SelectMany(segment => segment.Coordinates), point =>
                point[transpose ? 0 : 1] == -.5 && point[transpose ? 1 : 0] == width / 2d);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void UniformWideTurnsAndJunctionsHaveOnlyRealBranches(int width)
    {
        foreach (var arms in new[] { 2, 3, 4 })
        {
            var cells = new Dictionary<(int, int), RoadIndex.Cell>();
            for (var x = arms >= 3 ? -24 : 0; x < 24; x++) for (var z = 0; z < width; z++) cells[(x, z)] = Cell(x, z);
            for (var z = arms == 4 ? -24 : 0; z < 24; z++) for (var x = 0; x < width; x++) cells[(x, z)] = Cell(x, z);
            var segments = RoadIndex.BuildSegments(cells.Values, null, 1.3, 1.3);
            Assert.Equal(arms == 2 ? 1 : arms, segments.Count);
            AssertConnected(segments);
            AssertOnRoad(segments, cells.Values);
        }
    }

    [Fact]
    public void AdjacentCodesRemainSeparateWithOwnSpeedAndColour()
    {
        var cells = new List<RoadIndex.Cell>();
        for (var x = 0; x < 5; x++) cells.Add(Cell(x, 0, "game:stonepath-free", 1.3));
        for (var z = 1; z < 4; z++) cells.Add(Cell(4, z, "chiseltools:pathedchiseledblock", 2.5));

        var segments = RoadIndex.BuildSegments(cells, null, 1.3, 2.5);

        Assert.Equal(2, segments.Count);
        Assert.Contains(segments, segment => segment.Code == "game:stonepath-free" && segment.SpeedMultiplier == 1.3 && segment.Color == "#FFFFFF");
        Assert.Contains(segments, segment => segment.Code == "chiseltools:pathedchiseledblock" && segment.SpeedMultiplier == 2.5 && segment.Color == "#FFFF00");
        AssertConnected(segments);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(8)]
    public void MixedMaterialJunctionsShareNodes(int width)
    {
        var cells = new Dictionary<(int, int), RoadIndex.Cell>();
        for (var x = -24; x < 24; x++) for (var z = 0; z < width; z++)
            cells[(x, z)] = Cell(x, z);
        for (var z = -24; z < 24; z++) for (var x = 0; x < width; x++)
            cells[(x, z)] = Cell(x, z, "chiseltools:pathedchiseledblock", 2.5);

        var segments = RoadIndex.BuildSegments(cells.Values, null, 1.3, 2.5);

        AssertConnected(segments);
        Assert.Contains(segments, segment => segment.Color == "#FFFFFF" && segment.SpeedMultiplier == 1.3);
        Assert.Contains(segments, segment => segment.Color == "#FFFF00" && segment.SpeedMultiplier == 2.5);
        var points = segments.SelectMany(segment => segment.Coordinates).ToArray();
        Assert.Contains(points, point => point[0] < -23);
        Assert.Contains(points, point => point[0] > 23);
        Assert.Contains(points, point => point[1] < -23);
        Assert.Contains(points, point => point[1] > 23);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void StairSteppedAndChangingWidthRoadsStayConnected(int width)
    {
        var cells = new Dictionary<(int, int), RoadIndex.Cell>();
        for (var x = 0; x < 40; x++)
            for (var z = x / 3; z <= x / 3 + width + (x / 7 % 2); z++)
                cells[(x, z)] = Cell(x, z) with { Y = 64 + x / 4 };

        var segments = RoadIndex.BuildSegments(cells.Values, null, 1.3, 1.3);
        AssertConnected(segments);
        Assert.Contains(segments.SelectMany(segment => segment.Coordinates), point => point[0] <= width / 2d + .5);
        Assert.Contains(segments.SelectMany(segment => segment.Coordinates), point => point[0] >= 39 - width / 2d);
        AssertOnRoad(segments, cells.Values);
    }

    [Fact]
    public void AlternatingRoadMaterialsTouchAtTheirBoundaries()
    {
        var cells = Enumerable.Range(0, 32).Select(x => x % 2 == 0
            ? Cell(x, 0) : Cell(x, 0, "chiseltools:pathedchiseledblock", 2.5));

        var segments = RoadIndex.BuildSegments(cells, null, 1.3, 2.5);

        AssertConnected(segments);
        Assert.All(segments, segment => Assert.Equal(segment.Code == "game:stonepath-free" ? 1.3 : 2.5, segment.SpeedMultiplier));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void RingAndShortBranchesAreNotCollapsed(int width)
    {
        var cells = new Dictionary<(int, int), RoadIndex.Cell>();
        for (var x = 0; x < 48; x++) for (var z = 0; z < 48; z++)
            if (x < width || z < width || x >= 48 - width || z >= 48 - width) cells[(x, z)] = Cell(x, z);

        var segments = RoadIndex.BuildSegments(cells.Values, null, 1.3, 1.3);

        var ring = Assert.Single(segments);
        Assert.Equal(ring.Coordinates[0], ring.Coordinates[^1]);
        AssertOnRoad(segments, cells.Values);

        for (var x = 16; x < 16 + width; x++) for (var z = -3; z < 0; z++) cells[(x, z)] = Cell(x, z);
        segments = RoadIndex.BuildSegments(cells.Values, null, 1.3, 1.3);
        AssertConnected(segments);
        Assert.Contains(segments.SelectMany(segment => segment.Coordinates), point => point[1] <= -2);
        AssertOnRoad(segments, cells.Values);
    }

    [Fact]
    public void SeparateParallelRoadsDoNotAcquireConnectors()
    {
        var cells = Enumerable.Range(0, 24).SelectMany(x => new[] { Cell(x, 0), Cell(x, 2) }).ToArray();
        var segments = RoadIndex.BuildSegments(cells, null, 1.3, 1.3);
        Assert.Equal(2, segments.Count);
        Assert.Equal(2, segments.Select(segment => segment.GroupId).Distinct().Count());
        Assert.All(segments, segment => Assert.Single(segment.Coordinates.Select(point => point[1]).Distinct()));
    }

    [Fact]
    public void AbruptlyNarrowingRoadKeepsItsGeometryOnPavement()
    {
        var cells = new List<RoadIndex.Cell>();
        for (var x = 0; x < 48; x++) for (var z = x < 24 ? 0 : 7; z < 8; z++) cells.Add(Cell(x, z));
        var segments = RoadIndex.BuildSegments(cells, null, 1.3, 1.3);
        AssertConnected(segments);
        AssertOnRoad(segments, cells);
    }

    [Fact]
    public void RandomBranchingRoadsPreserveConnectivityAndPavement()
    {
        var random = new Random(3809);
        for (var trial = 0; trial < 100; trial++)
        {
            var cells = new Dictionary<(int, int), RoadIndex.Cell> { [(0, 0)] = Cell(0, 0) };
            for (var branch = 0; branch < 8; branch++)
            {
                var start = cells.Values.ElementAt(random.Next(cells.Count)); var x = start.X; var z = start.Z;
                for (var step = 0; step < 24; step++)
                {
                    switch (random.Next(4)) { case 0: x++; break; case 1: x--; break; case 2: z++; break; default: z--; break; }
                    cells[(x, z)] = random.Next(2) == 0 ? Cell(x, z) : Cell(x, z, "chiseltools:pathedchiseledblock", 2.5);
                }
            }
            var segments = RoadIndex.BuildSegments(cells.Values, null, 1.3, 2.5);
            AssertConnected(segments);
            AssertOnRoad(segments, cells.Values);
        }
    }

    private static void AssertOnRoad(IReadOnlyList<RoadIndex.Segment> segments, IEnumerable<RoadIndex.Cell> cells)
    {
        var occupied = cells.Select(cell => (cell.X, cell.Z)).ToHashSet();
        foreach (var segment in segments)
            for (var i = 1; i < segment.Coordinates.Length; i++)
            {
                var a = segment.Coordinates[i - 1]; var b = segment.Coordinates[i];
                var steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(b[0] - a[0]), Math.Abs(b[1] - a[1])) * 4));
                for (var step = 0; step <= steps; step++)
                {
                    var x = a[0] + (b[0] - a[0]) * step / steps; var z = a[1] + (b[1] - a[1]) * step / steps;
                    var onRoad = false;
                    foreach (var dx in new[] { -.00001, .00001 }) foreach (var dz in new[] { -.00001, .00001 })
                        onRoad |= occupied.Contains(((int)Math.Floor(x + dx), (int)Math.Floor(z + dz)));
                    Assert.True(onRoad, $"Centreline leaves the road at ({x}, {z}).");
                }
            }
    }

    private static void AssertConnected(IReadOnlyList<RoadIndex.Segment> segments)
    {
        var edges = new Dictionary<(double X, double Z, double Y), HashSet<(double X, double Z, double Y)>>();
        foreach (var segment in segments)
            for (var i = 1; i < segment.Coordinates.Length; i++)
            {
                var a = segment.Coordinates[i - 1]; var b = segment.Coordinates[i];
                var from = (a[0], a[1], a[2]); var to = (b[0], b[1], b[2]);
                if (!edges.ContainsKey(from)) edges[from] = [];
                if (!edges.ContainsKey(to)) edges[to] = [];
                edges[from].Add(to); edges[to].Add(from);
            }
        Assert.NotEmpty(edges);
        var seen = new HashSet<(double X, double Z, double Y)>();
        var queue = new Queue<(double X, double Z, double Y)>(); queue.Enqueue(edges.Keys.First());
        while (queue.TryDequeue(out var point))
        {
            if (!seen.Add(point)) continue;
            foreach (var next in edges[point]) queue.Enqueue(next);
        }
        Assert.True(edges.Count == seen.Count, $"Only {seen.Count} of {edges.Count} road nodes are connected.");
    }
}
