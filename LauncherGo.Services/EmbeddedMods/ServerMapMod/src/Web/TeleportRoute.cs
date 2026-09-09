// Routing adapted from WebCartographer (MIT).
// Copyright (c) 2023 Th3Dilli. See WebCartographer-LICENSE.txt.
namespace ServerMap.Web;

// Same walk distance and entry penalty as webcartographer-route.js / the map's
// measurement tool. The server computes this; clients never choose the price.
public static class TeleportRoute
{
    public readonly record struct Point(double X, double Y, double Z);
    public static int Jumps(IEnumerable<TranslocatorPoint> links, Point start, Point end)
    {
        var points = new List<Point>();
        var seen = new HashSet<(Point, Point)>();
        foreach (var link in links)
        {
            var a = new Point(link.X, link.Y, link.Z);
            var b = new Point(link.TargetX, link.TargetY, link.TargetZ);
            if (seen.Contains((b, a)) || !seen.Add((a, b))) continue;
            points.Add(a); points.Add(b);
        }
        var endpointCount = points.Count;
        points.Add(start); points.Add(end);
        var count = points.Count;
        var distances = Enumerable.Repeat(double.PositiveInfinity, count).ToArray();
        var jumps = new int[count];
        var visited = new bool[count];
        var queue = new PriorityQueue<int, double>();
        distances[endpointCount] = 0;
        queue.Enqueue(endpointCount, 0);
        var bound = Distance(start, end);
        var cellSize = Math.Max(1, Math.Max(points.Max(p => p.X) - points.Min(p => p.X), points.Max(p => p.Z) - points.Min(p => p.Z)) / Math.Max(1, Math.Sqrt(count)));
        var cells = points.Select((p, i) => (Cell: Cell(p), Index: i)).GroupBy(p => p.Cell).ToDictionary(g => g.Key, g => g.Select(p => p.Index).ToArray());
        (int X, int Z) Cell(Point p) => ((int)Math.Floor(p.X / cellSize), (int)Math.Floor(p.Z / cellSize));
        while (queue.TryDequeue(out var u, out var cost))
        {
            if (visited[u]) continue;
            if (u == count - 1) return jumps[u];
            if (cost > bound) break;
            visited[u] = true;
            if (u < endpointCount) Relax(u ^ 1, cost + (points[u].Y < 64 ? 320 : 200), true);
            var remaining = bound - cost;
            var min = Cell(new(points[u].X - remaining, 0, points[u].Z - remaining));
            var max = Cell(new(points[u].X + remaining, 0, points[u].Z + remaining));
            // Sparse worlds can span millions of blocks. Never iterate empty
            // cells across that whole extent when scanning occupied ones is cheaper.
            if (((long)max.X - min.X + 1) * ((long)max.Z - min.Z + 1) > cells.Count)
            {
                foreach (var cell in cells)
                    if (cell.Key.X >= min.X && cell.Key.X <= max.X && cell.Key.Z >= min.Z && cell.Key.Z <= max.Z)
                        foreach (var v in cell.Value) Walk(v);
            }
            else
                for (var x = min.X; x <= max.X; x++) for (var z = min.Z; z <= max.Z; z++)
                    if (cells.TryGetValue((x, z), out var bucket)) foreach (var v in bucket) Walk(v);
            void Walk(int v)
            {
                if (v != u && !visited[v]) Relax(v, cost + Distance(points[u], points[v]), false);
            }
            void Relax(int v, double next, bool jump)
            {
                if (next >= distances[v] || next > bound) return;
                distances[v] = next; jumps[v] = jumps[u] + (jump ? 1 : 0);
                if (v == count - 1) bound = next;
                queue.Enqueue(v, next);
            }
        }
        return 0;
    }
    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
}
