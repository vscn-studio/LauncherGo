using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using ProtoBuf;
using ServerMap.Render;
using ServerMap.Web;
using ServerMap.World;
using Vintagestory.API.Datastructures;

if (args.Length != 1) throw new ArgumentException("GameRoot required");
var gameRoot = Path.GetFullPath(args[0]);
AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
{
    var name = new AssemblyName(request.Name).Name + ".dll";
    foreach (var folder in new[] { gameRoot, Path.Combine(gameRoot, "Lib"), Path.Combine(gameRoot, "Mods") })
    { var file = Path.Combine(folder, name); if (File.Exists(file)) return Assembly.LoadFrom(file); }
    return null;
};
await Checks.Run();

static class Checks
{
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task Run()
    {
        // Synthetic save records use the game's TreeAttribute serializer, not
        // BlockEntity construction or any loaded player/world state.
        var tree = new TreeAttribute();
        tree.SetInt("posx", 511288); tree.SetInt("posy", 64); tree.SetInt("posz", 512121);
        tree.SetInt("teleX", 600000); tree.SetInt("teleY", 80); tree.SetInt("teleZ", 700000);
        tree.SetBool("canTele", true);
        var parser = typeof(ChunkKey).Assembly.GetType("ServerMap.World.SavedTranslocators")!.GetMethod("Read")!;
        TranslocatorPoint[] Read(TreeAttribute record, string type = "StaticTranslocator")
        {
            using var entity = new MemoryStream();
            using (var writer = new BinaryWriter(entity, System.Text.Encoding.UTF8, true)) { writer.Write(type); record.ToBytes(writer); }
            using var chunk = new MemoryStream();
            Serializer.Serialize(chunk, new ChunkFixture { Objects = [entity.ToArray()], Unrelated = [1, 2, 3] });
            return (TranslocatorPoint[])parser.Invoke(null, [chunk.ToArray()])!;
        }
        Require(Read(tree).Single() == new TranslocatorPoint(511288, 64, 512121, 600000, 80, 700000), "Saved translocator decoding failed");
        Require(Read(tree, "UnrelatedModEntity").Length == 0, "Unrelated entity was interpreted as a link");
        tree.SetBool("canTele", false); Require(Read(tree).Length == 0, "Inactive link should not be indexed");
        tree.SetBool("canTele", true); tree.SetBool("tpLocationIsOffset", true);
        Require(Read(tree).Length == 0, "Unresolved destination offset should not be mapped as an absolute location");

        var entries = new MapPalette.Entry?[] { new(0, "game:air", "land", false, false, false, false, false, false, false, true), new(1, "game:test", "land", false, false, false, false, false, false, false, false) };
        var palette = (MapPalette)Activator.CreateInstance(typeof(MapPalette), BindingFlags.Instance | BindingFlags.NonPublic, null, [entries], null)!;
        var json = JsonSerializer.Serialize(new Dictionary<string, uint[]> { ["game:test"] = Enumerable.Repeat(0x125634u, 30).ToArray() });
        Require(palette.ApplyClientColormap(json, 5, out var count) && count == 1, "Palette apply failed");
        var old = palette.CaptureColors()!;
        var nextJson = JsonSerializer.Serialize(new Dictionary<string, uint[]> { ["game:test"] = Enumerable.Repeat(0xabcdefu, 30).ToArray() });
        palette.ApplyClientColormap(nextJson, 5, out _);
        Require(old.Color(1, 0, 0, 0) == ((byte)0x12, (byte)0x56, (byte)0x34), "In-flight tile palette changed mid-render");
        Require(old.Version != palette.ClientColormapVersion && !old.HasColor(0), "Palette versions or missing colors were lost");

        var root = Directory.CreateTempSubdirectory("LauncherGo-map-recovery-").FullName;
        try
        {
            MapPalette.SaveClientColormap(root, json, 5);
            Require(palette.LoadClientColormap(root, 5, _ => { }) && palette.ClientColormapVersion == old.Version, "Cached palette did not restore its stable version");
            // Four changed children must produce each ancestor once, and all
            // their colors must reach the zoom-one parent in the same batch.
            var rgba = new byte[512 * 512 * 4];
            foreach (var (x, z, red) in new[] { (0, 0, 40), (1, 0, 80), (0, 1, 120), (1, 1, 160) })
            {
                for (var i = 0; i < rgba.Length; i += 4) { rgba[i] = (byte)red; rgba[i+3] = 255; }
                var directory = Path.Combine(root, "2d", "basic", "0"); Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, $"{x}_{z}.png"), PngEncoder.Encode(512, 512, rgba));
            }
            var builder = new TilePyramidBuilder(root);
            var updates = builder.BuildParentsBatch("basic", [(0, 0), (1, 0), (0, 1), (1, 1)], CancellationToken.None).ToArray();
            Require(updates.Length == TilePyramidBuilder.MaxZoom && updates.Distinct().Count() == updates.Length, "Duplicate parent writes in batch");
            var parent = PngEncoder.Decode(File.ReadAllBytes(Path.Combine(root, "2d", "basic", "1", "0_0.png")));
            Require(parent[0] == 40 && parent[256 * 4] == 80 && parent[256 * 512 * 4] == 120 && parent[(256 * 512 + 256) * 4] == 160, "Parent retained stale quadrant colors");
        }
        finally { Directory.Delete(root, true); }

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var calls = new ConcurrentQueue<int>();
        var queue = new RenderQueue(1, key =>
        {
            calls.Enqueue(key.X);
            if (key.X == 0) { entered.TrySetResult(); Require(release.Wait(TimeSpan.FromSeconds(10)), "Render test timed out"); }
            return RenderQueueOutcome.Completed;
        });
        try
        {
            queue.Enqueue(new(0, 0, 0)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            queue.Enqueue(new(1, 0, 0)); queue.Enqueue(new(2, 0, 0));
            for (var i = 0; i < 100; i++) { Require(queue.Promote(new(2, 0, 0)), "Visible job missing"); queue.Promote(new(0, 0, 0)); }
            release.Set();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (queue.PendingCount > 0) await Task.Delay(10, timeout.Token);
            Require(calls.SequenceEqual(new[] { 0, 2, 1 }), "Visible promotion duplicated/re-ran a job or failed to jump ahead");
        }
        finally { release.Set(); await queue.StopAsync(); }
        Console.WriteLine("PASS saved translocator decoding, palette restore/snapshot, parent pixels and queue promotion");
    }
}

[ProtoContract]
sealed class ChunkFixture
{
    [ProtoMember(1)] public byte[] Unrelated { get; set; } = [];
    [ProtoMember(8)] public List<byte[]> Objects { get; set; } = [];
}
