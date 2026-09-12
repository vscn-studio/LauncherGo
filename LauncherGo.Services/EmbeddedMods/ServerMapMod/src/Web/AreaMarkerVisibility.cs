using Rect = ServerMap.Web.AreaMarkerStore.Rect;

namespace ServerMap.Web;

/// <summary>Display-only geometry. Never save a fog-clipped shape back to the marker store.</summary>
public static class AreaMarkerVisibility
{
    public sealed record Edge(int X1, int Z1, int X2, int Z2);
    public sealed record Shape(Rect[] Rects, Edge[] Borders);

    public static Shape Clip(Rect[] source, Rect[] explored)
    {
        var pieces = new List<Rect>();
        var ordered = explored.OrderBy(r => r.MinX).ToArray();
        foreach (var r in source)
            foreach (var cell in ordered)
            {
                if (cell.MinX >= r.MaxX) break;
                if (AreaMarkerStore.Intersects(r, cell))
                    pieces.Add(new(Math.Max(r.MinX, cell.MinX), Math.Max(r.MinZ, cell.MinZ), Math.Min(r.MaxX, cell.MaxX), Math.Min(r.MaxZ, cell.MaxZ)));
            }
        var rects = Compact(pieces);
        if (rects.Length == 0) return new([], []);
        // Intersect the two boundaries, not just the fills: the fog frontier
        // must not invent an area border where none existed in the original.
        var original = Boundary(source).ToLookup(Line);
        var borders = new List<Edge>();
        foreach (var edge in Boundary(rects))
            foreach (var old in original[Line(edge)])
            {
                var x1 = Math.Max(edge.X1, old.X1); var z1 = Math.Max(edge.Z1, old.Z1);
                var x2 = Math.Min(edge.X2, old.X2); var z2 = Math.Min(edge.Z2, old.Z2);
                if (edge.Z1 == edge.Z2 ? x1 < x2 : z1 < z2) borders.Add(new(x1, z1, x2, z2));
            }
        return new(rects, borders.ToArray());
    }

    private static (bool Horizontal, int Position) Line(Edge edge) => edge.Z1 == edge.Z2 ? (true, edge.Z1) : (false, edge.X1);

    public static Edge[] Boundary(Rect[] rects)
    {
        var lines = new Dictionary<(bool Horizontal, int Position), SortedDictionary<int, int>>();
        void Edge(bool horizontal, int position, int start, int end, int sign)
        {
            if (!lines.TryGetValue((horizontal, position), out var events)) lines[(horizontal, position)] = events = new();
            events[start] = events.GetValueOrDefault(start) + sign;
            events[end] = events.GetValueOrDefault(end) - sign;
        }
        foreach (var r in rects)
        {
            Edge(true, r.MinZ, r.MinX, r.MaxX, 1); Edge(true, r.MaxZ, r.MinX, r.MaxX, -1);
            Edge(false, r.MinX, r.MinZ, r.MaxZ, 1); Edge(false, r.MaxX, r.MinZ, r.MaxZ, -1);
        }
        var result = new List<Edge>();
        foreach (var (line, events) in lines)
        {
            int coverage = 0, previous = 0;
            foreach (var (position, delta) in events)
            {
                if (coverage != 0 && position > previous) result.Add(line.Horizontal
                    ? new(previous, line.Position, position, line.Position) : new(line.Position, previous, line.Position, position));
                coverage += delta; previous = position;
            }
        }
        return result.ToArray();
    }

    // Input is a disjoint union. Compact complete shared edges without any
    // work proportional to the selected world area (or quadratic label grids).
    public static Rect[] Compact(IEnumerable<Rect> input)
    {
        var result = input.Distinct().ToArray();
        for (var pass = 0; pass < 4; pass++)
        {
            var count = result.Length;
            foreach (var horizontal in new[] { true, false })
            {
                var sorted = result.OrderBy(r => horizontal ? r.MinZ : r.MinX)
                    .ThenBy(r => horizontal ? r.MaxZ : r.MaxX).ThenBy(r => horizontal ? r.MinX : r.MinZ);
                var merged = new List<Rect>();
                foreach (var r in sorted)
                {
                    var last = merged.LastOrDefault();
                    if (horizontal && last != null && last.MinZ == r.MinZ && last.MaxZ == r.MaxZ && last.MaxX == r.MinX)
                        merged[^1] = last with { MaxX = r.MaxX };
                    else if (!horizontal && last != null && last.MinX == r.MinX && last.MaxX == r.MaxX && last.MaxZ == r.MinZ)
                        merged[^1] = last with { MaxZ = r.MaxZ };
                    else merged.Add(r);
                }
                result = merged.ToArray();
            }
            if (result.Length == count) break;
        }
        return result;
    }
}
