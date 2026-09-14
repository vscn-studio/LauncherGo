namespace ServerMap.Web;

// Pure road geometry shared by the scanner and regression tests.
public sealed partial class RoadIndex
{
    public sealed record Segment(string Id, double[][] Coordinates, string Code, double SpeedMultiplier, string Color, string GroupId);
    internal sealed record Cell(int X, int Z, int Y, string Code, double SpeedMultiplier);
    private sealed record CrossSection(Cell[] Cells, bool Horizontal, double X, double Z);
    private sealed record CentreNode(double[] Coordinates, Cell Material);
    private sealed record CentreEdge(int A, int B, Cell Material);

    internal static IReadOnlyList<Segment> BuildSegments(IEnumerable<Cell> source,
        (double MinX, double MinZ, double MaxX, double MaxZ)? bounds, double minSpeed, double maxSpeed,
        Func<double, double, bool>? visible = null)
    {
        // Visibility is applied before connectivity, but materials are not:
        // a change of pavement must not disconnect the road at its boundary.
        var byPosition = source.Where(cell => visible == null || visible(cell.X + .5, cell.Z + .5))
            .OrderBy(cell => cell.X).ThenBy(cell => cell.Z).ToDictionary(cell => (cell.X, cell.Z));
        var remaining = byPosition.Keys.ToHashSet();
        var result = new List<Segment>();
        foreach (var first in byPosition.Keys)
        {
            if (!remaining.Remove(first)) continue;
            var component = new List<Cell>();
            var queue = new Queue<(int X, int Z)>(); queue.Enqueue(first);
            while (queue.TryDequeue(out var key))
            {
                component.Add(byPosition[key]);
                foreach (var next in Neighbours(key)) if (remaining.Remove(next)) queue.Enqueue(next);
            }

            var (nodes, edges) = CentreGraph(component);
            var id = StableId(component);
            var groupId = "road-group-" + id;
            var branchIndex = 0;
            foreach (var (coordinates, material) in CentreLines(nodes, edges))
            {
                if (bounds != null && !coordinates.Any(point => point[0] >= bounds.Value.MinX && point[0] <= bounds.Value.MaxX
                    && point[1] >= bounds.Value.MinZ && point[1] <= bounds.Value.MaxZ)) continue;
                result.Add(new Segment($"road-{id}-{branchIndex++}", coordinates, material.Code, material.SpeedMultiplier,
                    SpeedColor(material.SpeedMultiplier, minSpeed, maxSpeed), groupId));
            }
        }
        return result;
    }

    private static IEnumerable<(int X, int Z)> Neighbours((int X, int Z) key)
    {
        yield return (key.X - 1, key.Z); yield return (key.X + 1, key.Z);
        yield return (key.X, key.Z - 1); yield return (key.X, key.Z + 1);
    }

    private static (List<CentreNode> Nodes, List<CentreEdge> Edges) CentreGraph(IReadOnlyList<Cell> component)
    {
        var byPosition = component.ToDictionary(cell => (cell.X, cell.Z));
        var horizontalRun = new Dictionary<(int X, int Z), int>();
        var verticalRun = new Dictionary<(int X, int Z), int>();
        foreach (var row in component.GroupBy(cell => cell.Z))
            AssignRuns(row.Select(cell => cell.X).OrderBy(value => value).ToArray(), row.Key, horizontalRun, true);
        foreach (var column in component.GroupBy(cell => cell.X))
            AssignRuns(column.Select(cell => cell.Z).OrderBy(value => value).ToArray(), column.Key, verticalRun, false);

        var horizontal = component.ToDictionary(cell => (cell.X, cell.Z),
            cell => horizontalRun[(cell.X, cell.Z)] >= verticalRun[(cell.X, cell.Z)]);
        var sections = new List<CrossSection>();
        var sectionByCell = new Dictionary<(int X, int Z), int>();
        AddSections(true); AddSections(false);

        // Each real block belongs to exactly one cross-section. Derive edges
        // from touching blocks, not a distance/alignment guess between their
        // averages. This preserves bends, varying widths and every branch.
        var sectionEdges = new HashSet<(int A, int B)>();
        foreach (var (position, section) in sectionByCell)
            foreach (var next in Neighbours(position))
                if (sectionByCell.TryGetValue(next, out var other) && section != other)
                    sectionEdges.Add(EdgeKey(section, other));

        // The perpendicular sections touching at a wide turn/junction form
        // one shared node, instead of parallel connectors across its width.
        var parents = Enumerable.Range(0, sections.Count).ToArray();
        var boxes = sections.Select(section => (MinX: section.Cells.Min(cell => cell.X), MinZ: section.Cells.Min(cell => cell.Z),
            MaxX: section.Cells.Max(cell => cell.X), MaxZ: section.Cells.Max(cell => cell.Z))).ToArray();
        var adjacent = Enumerable.Range(0, sections.Count).Select(_ => new HashSet<int>()).ToArray();
        foreach (var (a, b) in sectionEdges)
        { adjacent[a].Add(b); adjacent[b].Add(a); }
        var continues = Enumerable.Range(0, sections.Count)
            .Select(index => adjacent[index].Any(other => sections[index].Horizontal == sections[other].Horizontal)).ToArray();
        foreach (var (a, b) in sectionEdges)
            if (sections[a].Horizontal != sections[b].Horizontal && continues[a] && continues[b]) Join(a, b);

        // A one-section arm is a real endpoint, not part of the junction.
        // Contract only its touching supports on each side, so a wide short
        // branch has one connector without swallowing its final block.
        for (var index = 0; index < sections.Count; index++)
            if (!continues[index])
            {
                var section = sections[index];
                foreach (var direction in new[] { -1, 1 })
                {
                    var supports = new HashSet<int>();
                    foreach (var cell in section.Cells)
                    {
                        var next = section.Horizontal ? (cell.X + direction, cell.Z) : (cell.X, cell.Z + direction);
                        if (sectionByCell.TryGetValue(next, out var other) && continues[other]
                            && section.Horizontal != sections[other].Horizontal) supports.Add(other);
                    }
                    foreach (var support in supports)
                        foreach (var other in adjacent[support])
                            if (supports.Contains(other)) Join(support, other);
                }
            }
        var nodes = new List<CentreNode>();
        var nodeBySection = new int[sections.Count];
        foreach (var group in Enumerable.Range(0, sections.Count).GroupBy(Find))
        {
            var members = group.Select(index => sections[index]).ToArray();
            var cells = members.SelectMany(section => section.Cells).ToArray();
            var horizontalSections = members.Where(section => section.Horizontal).ToArray();
            var verticalSections = members.Where(section => !section.Horizontal).ToArray();
            var x = verticalSections.Length > 0 ? verticalSections.Average(section => section.X) : members.Average(section => section.X);
            var z = horizontalSections.Length > 0 ? horizontalSections.Average(section => section.Z) : members.Average(section => section.Z);
            var material = cells.OrderBy(cell => Math.Pow(cell.X + .5 - x, 2) + Math.Pow(cell.Z + .5 - z, 2))
                .ThenByDescending(cell => cell.SpeedMultiplier).ThenBy(cell => cell.X).ThenBy(cell => cell.Z).First();
            if (!cells.Any(cell => x >= cell.X && x <= cell.X + 1 && z >= cell.Z && z <= cell.Z + 1))
            { x = material.X + .5; z = material.Z + .5; }
            foreach (var section in group) nodeBySection[section] = nodes.Count;
            nodes.Add(new CentreNode([x, z, material.Y], material));
        }

        var contacts = new Dictionary<(int A, int B), List<(Cell A, Cell B)>>();
        foreach (var cell in component)
            foreach (var next in Neighbours((cell.X, cell.Z)))
            {
                if (!sectionByCell.TryGetValue(next, out var otherSection)) continue;
                var a = nodeBySection[sectionByCell[(cell.X, cell.Z)]]; var b = nodeBySection[otherSection];
                if (a >= b) continue;
                if (!contacts.TryGetValue((a, b), out var pairs)) contacts[(a, b)] = pairs = [];
                pairs.Add((cell, byPosition[next]));
            }
        var edges = new List<CentreEdge>();
        foreach (var ((a, b), pairs) in contacts)
        {
            var from = nodes[a]; var to = nodes[b];
            List<double[]> path = [from.Coordinates, to.Coordinates];
            if (Spans(from.Coordinates, to.Coordinates).Any(span => Pavement(span.Midpoint) == null))
            {
                var contact = pairs.MinBy(pair => Distance(from.Coordinates, pair.A) + Distance(to.Coordinates, pair.B));
                path = [from.Coordinates];
                path.AddRange(InsideNode(a, from.Material, contact.A));
                path.AddRange(InsideNode(b, to.Material, contact.B).Reverse());
                path.Add(to.Coordinates);
            }
            path = path.Where((point, index) => index == 0 || !point.SequenceEqual(path[index - 1])).ToList();
            var previous = a;
            for (var part = 1; part < path.Count; part++)
                foreach (var span in Spans(path[part - 1], path[part]))
                {
                    var material = Pavement(span.Midpoint) ?? throw new InvalidOperationException("Road connection leaves its pavement.");
                    var last = part == path.Count - 1 && span.To.SequenceEqual(to.Coordinates);
                    var next = last ? b : nodes.Count;
                    if (!last) nodes.Add(new CentreNode(span.To, material));
                    edges.Add(new CentreEdge(previous, next, material));
                    previous = next;
                }
        }
        return (nodes, edges);

        Cell? Pavement(double[] point)
        {
            Cell? best = null;
            foreach (var dx in new[] { -.000001, .000001 }) foreach (var dz in new[] { -.000001, .000001 })
                if (byPosition.TryGetValue(((int)Math.Floor(point[0] + dx), (int)Math.Floor(point[1] + dz)), out var cell)
                    && (best == null || cell.SpeedMultiplier > best.SpeedMultiplier)) best = cell;
            return best;
        }

        static double Distance(double[] point, Cell cell) => Math.Abs(point[0] - cell.X - .5) + Math.Abs(point[1] - cell.Z - .5);

        IEnumerable<double[]> InsideNode(int node, Cell start, Cell end)
        {
            var previous = new Dictionary<(int X, int Z), (int X, int Z)> { [(start.X, start.Z)] = (start.X, start.Z) };
            var queue = new Queue<(int X, int Z)>(); queue.Enqueue((start.X, start.Z));
            while (queue.TryDequeue(out var current))
            {
                if (current == (end.X, end.Z)) break;
                foreach (var next in Neighbours(current))
                    if (sectionByCell.TryGetValue(next, out var section) && nodeBySection[section] == node && previous.TryAdd(next, current)) queue.Enqueue(next);
            }
            var path = new List<double[]>(); var position = (end.X, end.Z);
            while (true)
            {
                var cell = byPosition[position]; path.Add([cell.X + .5, cell.Z + .5, cell.Y]);
                if (position == (start.X, start.Z)) break;
                position = previous[position];
            }
            path.Reverse(); return path;
        }

        int Find(int index)
        {
            while (parents[index] != index) { parents[index] = parents[parents[index]]; index = parents[index]; }
            return index;
        }

        void Join(int a, int b)
        {
            var rootA = Find(a); var rootB = Find(b);
            if (rootA == rootB) return;
            var boxA = boxes[rootA]; var boxB = boxes[rootB];
            var box = (MinX: Math.Min(boxA.MinX, boxB.MinX), MinZ: Math.Min(boxA.MinZ, boxB.MinZ),
                MaxX: Math.Max(boxA.MaxX, boxB.MaxX), MaxZ: Math.Max(boxA.MaxZ, boxB.MaxZ));
            // Do not contract a chain of bends or a ring across unpaved
            // space. Only a solid paved junction can become one node.
            for (var x = box.MinX; x <= box.MaxX; x++) for (var z = box.MinZ; z <= box.MaxZ; z++)
                if (!byPosition.ContainsKey((x, z))) return;
            var root = Math.Min(rootA, rootB);
            parents[Math.Max(rootA, rootB)] = root;
            boxes[root] = box;
        }

        void AddSections(bool alongX)
        {
            foreach (var group in component.Where(cell => horizontal[(cell.X, cell.Z)] == alongX)
                .GroupBy(cell => alongX ? cell.X : cell.Z).OrderBy(group => group.Key))
            {
                var ordered = group.OrderBy(cell => alongX ? cell.Z : cell.X).ToArray();
                for (var start = 0; start < ordered.Length;)
                {
                    var end = start + 1;
                    while (end < ordered.Length && (alongX ? ordered[end].Z : ordered[end].X)
                        == (alongX ? ordered[end - 1].Z : ordered[end - 1].X) + 1) end++;
                    var cells = ordered[start..end];
                    foreach (var cell in cells) sectionByCell[(cell.X, cell.Z)] = sections.Count;
                    sections.Add(new CrossSection(cells, alongX, cells.Average(cell => cell.X) + .5, cells.Average(cell => cell.Z) + .5));
                    start = end;
                }
            }
        }
    }

    // Split at exact block boundaries. Midpoints select the actual pavement
    // material; adjacent colours share the same endpoint (including height).
    private static IEnumerable<(double[] To, double[] Midpoint)> Spans(double[] from, double[] to)
    {
        if (from.SequenceEqual(to)) yield break;
        var breaks = new SortedSet<double> { 0, 1 };
        for (var axis = 0; axis < 2; axis++)
        {
            if (from[axis] == to[axis]) continue;
            for (var border = Math.Floor(Math.Min(from[axis], to[axis])) + 1; border < Math.Max(from[axis], to[axis]); border++)
                breaks.Add((border - from[axis]) / (to[axis] - from[axis]));
        }
        double[] At(double t) => t == 0 ? from : t == 1 ? to :
            [from[0] + (to[0] - from[0]) * t, from[1] + (to[1] - from[1]) * t, from[2] + (to[2] - from[2]) * t];
        var previous = 0d;
        foreach (var end in breaks.Skip(1))
        {
            yield return (At(end), At((previous + end) / 2));
            previous = end;
        }
    }

    private static IEnumerable<(double[][] Coordinates, Cell Material)> CentreLines(List<CentreNode> nodes, List<CentreEdge> edges)
    {
        if (edges.Count == 0)
        {
            var node = nodes[0]; var p = node.Coordinates;
            yield return (new[] { new[] { p[0] - .3, p[1], p[2] }, new[] { p[0] + .3, p[1], p[2] } }, node.Material);
            yield break;
        }
        var incident = Enumerable.Range(0, nodes.Count).Select(_ => new List<int>()).ToArray();
        for (var index = 0; index < edges.Count; index++)
        { incident[edges[index].A].Add(index); incident[edges[index].B].Add(index); }
        var visited = new HashSet<int>();
        for (var start = 0; start < nodes.Count; start++)
        {
            if (incident[start].Count == 2 && SameMaterial(edges[incident[start][0]].Material, edges[incident[start][1]].Material)) continue;
            foreach (var edge in incident[start]) if (!visited.Contains(edge)) yield return Walk(start, edge);
        }
        // Components that are closed rings have no endpoint or junction.
        for (var edge = 0; edge < edges.Count; edge++) if (!visited.Contains(edge)) yield return Walk(edges[edge].A, edge);

        (double[][] Coordinates, Cell Material) Walk(int start, int edge)
        {
            var path = new List<double[]> { nodes[start].Coordinates };
            var material = edges[edge].Material;
            var current = start;
            while (visited.Add(edge))
            {
                current = edges[edge].A == current ? edges[edge].B : edges[edge].A;
                path.Add(nodes[current].Coordinates);
                if (incident[current].Count != 2) break;
                var next = incident[current][0] == edge ? incident[current][1] : incident[current][0];
                if (!SameMaterial(material, edges[next].Material)) break;
                edge = next;
            }
            return (path.ToArray(), material);
        }
    }

    private static bool SameMaterial(Cell a, Cell b) => string.Equals(a.Code, b.Code, StringComparison.OrdinalIgnoreCase)
        && a.SpeedMultiplier == b.SpeedMultiplier;
    private static (int A, int B) EdgeKey(int a, int b) => a < b ? (a, b) : (b, a);

    private static void AssignRuns(int[] values, int fixedAxis, Dictionary<(int X, int Z), int> output, bool horizontal)
    {
        for (var start = 0; start < values.Length;)
        {
            var end = start + 1;
            while (end < values.Length && values[end] == values[end - 1] + 1) end++;
            for (var index = start; index < end; index++)
                output[horizontal ? (values[index], fixedAxis) : (fixedAxis, values[index])] = end - start;
            start = end;
        }
    }

    private static string StableId(IEnumerable<Cell> cells)
    {
        var ordered = cells.OrderBy(cell => cell.X).ThenBy(cell => cell.Z).ToArray();
        var first = ordered[0]; var last = ordered[^1];
        // Extents/count alone collide for disconnected components of the same shape.
        ulong hash = 14695981039346656037UL;
        foreach (var cell in ordered)
        {
            hash ^= unchecked((uint)cell.X); hash *= 1099511628211UL;
            hash ^= unchecked((uint)cell.Z); hash *= 1099511628211UL;
        }
        return FormattableString.Invariant($"{first.X}_{first.Z}_{last.X}_{last.Z}_{ordered.Length}_{hash:X16}");
    }

    private static string SpeedColor(double multiplier, double minSpeed, double maxSpeed)
    {
        var t = maxSpeed > minSpeed + 0.0001 ? Math.Clamp((multiplier - minSpeed) / (maxSpeed - minSpeed), 0, 1) : 0;
        var blue = (int)Math.Round(255 * (1 - t));
        return $"#FFFF{blue:X2}";
    }
}
