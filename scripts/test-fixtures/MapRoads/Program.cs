using System.Text.Json;
using ServerMap.Render;
using ServerMap.Web;

var cells = new Dictionary<(int, int), RoadIndex.Cell>();
if (args.Length == 2 && args[0] == "--surface")
{
    var root = Path.GetFullPath(args[1]);
    using var announcement = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "announcement.json")));
    var codes = announcement.RootElement.GetProperty("Management").GetProperty("Roads").GetProperty("BlockCodes")
        .EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
    foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "surface"), "*.br"))
    {
        var region = Path.GetFileNameWithoutExtension(file).Split('_');
        if (region.Length != 2 || !int.TryParse(region[0], out var tileX) || !int.TryParse(region[1], out var tileZ)) continue;
        var surface = SurfaceRegion.Load(file) ?? throw new InvalidDataException("Invalid surface: " + file);
        for (var i = 0; i < surface.Codes.Length; i++)
        {
            if (!surface.Valid[i] || !codes.Contains(surface.Codes[i])) continue;
            var x = tileX * 512 + i % 512; var z = tileZ * 512 + i / 512;
            // Geometry replay deliberately does not invent runtime speeds.
            cells[(x, z)] = new(x, z, surface.Heights[i], surface.Codes[i], 1);
        }
    }
}
else if (args.Length == 0)
{
    for (var x = -24; x < 24; x++) for (var z = 0; z < 2; z++)
        cells[(x, z)] = new(x, z, 64, "game:stonepath-free", 1.3);
    for (var z = -24; z < 24; z++) for (var x = 0; x < 2; x++)
        cells[(x, z)] = new(x, z, 64, "chiseltools:pathedchiseledblock", 2.5);
}
else throw new ArgumentException("Usage: MapRoads [--surface ServerMapWorldRoot]");

var segments = RoadIndex.BuildSegments(cells.Values, null, 1.3, 2.5);
// Every physically connected component must remain one connected rendered
// graph. This checks real saved surfaces without using an account or token.
foreach (var group in segments.GroupBy(segment => segment.GroupId))
{
    var edges = new Dictionary<(double, double, double), HashSet<(double, double, double)>>();
    foreach (var segment in group)
        for (var i = 1; i < segment.Coordinates.Length; i++)
        {
            var a = segment.Coordinates[i - 1]; var b = segment.Coordinates[i];
            var from = (a[0], a[1], a[2]); var to = (b[0], b[1], b[2]);
            if (!edges.ContainsKey(from)) edges[from] = [];
            if (!edges.ContainsKey(to)) edges[to] = [];
            edges[from].Add(to); edges[to].Add(from);
        }
    var seen = new HashSet<(double, double, double)>(); var queue = new Queue<(double, double, double)>();
    queue.Enqueue(edges.Keys.First());
    while (queue.TryDequeue(out var point))
    {
        if (!seen.Add(point)) continue;
        foreach (var next in edges[point]) queue.Enqueue(next);
    }
    if (seen.Count != edges.Count) throw new InvalidOperationException($"Disconnected road: {seen.Count}/{edges.Count} nodes.");
}
Console.Error.WriteLine($"PASS geometry replay: {cells.Count} blocks, {segments.Select(segment => segment.GroupId).Distinct().Count()} components, {segments.Count} lines; every component connected.");
Console.WriteLine(JsonSerializer.Serialize(new { type = "FeatureCollection", features = segments.Select(segment => new
{
    type = "Feature", id = segment.Id, geometry = new { type = "LineString", coordinates = segment.Coordinates },
    properties = new { code = segment.Code, speedMultiplier = segment.SpeedMultiplier, color = segment.Color, roadGroup = segment.GroupId }
}) }));
