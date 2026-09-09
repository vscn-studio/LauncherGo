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
            var canonical = JsonSerializer.Serialize(new Dictionary<string, uint[]> { ["ignored:block"] = new uint[30], ["game:test"] = Enumerable.Repeat(0xff125634u, 30).ToArray() }, new JsonSerializerOptions { WriteIndented = true });
            palette.ApplyClientColormap(canonical, 5, out _);
            Require(palette.ClientColormapVersion == old.Version, "Equivalent effective palette contents invalidated tiles");
            palette.ApplyClientColormap(canonical, 6, out _);
            Require(palette.ClientColormapVersion != old.Version, "A new month did not invalidate colored tiles");
            palette.ApplyClientColormap(json, 5, out _);
            // Recolor a serialized surface with reassigned runtime block IDs.
            // A null world reader makes accidental world IO fail this fixture.
            var surface = new SurfaceRegion { Generation = 1 };
            for (var i = 0; i < surface.Heights.Length; i++) { surface.Codes[i] = "game:test"; surface.Valid[i] = true; surface.Heights[i] = (ushort)(50 + i % 13); surface.SepiaKeys[i] = "land"; }
            var regionKey = new ChunkKey(12, 0, 34);
            var renderer = new MapRenderer(null!, root, 256, palette);
            Require(renderer.RenderSurface(regionKey, surface), "Direct surface coloring failed");
            var expectedPng = File.ReadAllBytes(Path.Combine(root, "2d", "basic", "0", "12_34.png"));
            var surfacePath = SurfaceRegion.PathFor(root, 12, 34); surface.Save(surfacePath);
            var reassignedEntries = new MapPalette.Entry?[] { entries[0], null, entries[1]! with { Id = 2 } };
            var reassigned = (MapPalette)Activator.CreateInstance(typeof(MapPalette), BindingFlags.Instance | BindingFlags.NonPublic, null, [reassignedEntries], null)!;
            reassigned.ApplyClientColormap(json, 5, out _);
            var cachedRenderer = new MapRenderer(null!, root, 256, reassigned);
            Require(cachedRenderer.RenderSurface(regionKey, SurfaceRegion.Load(surfacePath)!), "Cached coloring failed");
            Require(expectedPng.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "2d", "basic", "0", "12_34.png"))), "Cache coloring changed after block ID reassignment");
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
            Require(!builder.BuildParentsBatch("basic", [(0, 0), (1, 0), (0, 1), (1, 1)], CancellationToken.None).Any(), "Restart rebuilt unchanged parents");
            var parent = PngEncoder.Decode(File.ReadAllBytes(Path.Combine(root, "2d", "basic", "1", "0_0.png")));
            Require(parent[0] == 40 && parent[256 * 4] == 80 && parent[256 * 512 * 4] == 120 && parent[(256 * 512 + 256) * 4] == 160, "Parent retained stale quadrant colors");
            var parentPath = Path.Combine(root, "2d", "basic", "1", "0_0.png");
            var damaged = File.ReadAllBytes(parentPath); damaged[0] ^= 1; File.WriteAllBytes(parentPath, damaged);
            Require(!builder.IsCurrent("basic", 1, 0, 0), "Corrupt parent accepted as current");
            Require(builder.BuildParents("basic", new ChunkKey(0, 0, 0)).Any(), "Corrupt parent was not repaired");
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
        // A running job accepts identical target versions without rerunning.
        entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); release.Reset();
        var versionCalls = 0;
        var versionQueue = new RenderQueue(1, key => { Interlocked.Increment(ref versionCalls); entered.TrySetResult(); Require(release.Wait(TimeSpan.FromSeconds(5)), "Version queue blocked"); return RenderQueueOutcome.Completed; });
        try
        {
            versionQueue.Enqueue(new(1, 0, 0), version: 5); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 0; i < 100; i++) versionQueue.Enqueue(new(1, 0, 0), version: 5);
            release.Set();
            while (versionQueue.PendingCount > 0) await Task.Delay(10);
            Require(versionCalls == 1, "Identical in-flight versions reran");
        }
        finally { release.Set(); await versionQueue.StopAsync(); }
        // Yielding extraction units admit one background unit after eight changes.
        entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); release.Reset();
        var order = new ConcurrentQueue<int>(); var units = 0;
        var fairQueue = new RenderQueue(1, key => { order.Enqueue(key.X); if (key.X == 0) { entered.TrySetResult(); Require(release.Wait(TimeSpan.FromSeconds(5)), "Fairness queue blocked"); return ++units < 10 ? RenderQueueOutcome.Yield : RenderQueueOutcome.Completed; } return RenderQueueOutcome.Completed; });
        try
        {
            fairQueue.Enqueue(new(0, 0, 0), priority: true, version: 1); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fairQueue.Enqueue(new(1, 0, 0), version: 1, seasonal: true); release.Set();
            while (fairQueue.PendingCount > 0) await Task.Delay(10);
            Require(Array.IndexOf(order.ToArray(), 1) == 8, "Background coloring starved behind saved changes");
        }
        finally { release.Set(); await fairQueue.StopAsync(); }
        await SaveAdapterChecks.Run();
        Console.WriteLine("PASS saved translocator decoding, palette restore/snapshot, parent reuse, stable surface recoloring, version deduplication and scheduling fairness");
    }
}

[ProtoContract]
sealed class ChunkFixture
{
    [ProtoMember(1)] public byte[] Unrelated { get; set; } = [];
    [ProtoMember(8)] public List<byte[]> Objects { get; set; } = [];
}

static class SaveAdapterChecks
{
    public static Task Run()
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        static void Set(object value, string name, object field) => value.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(value, field);
        var serverType = typeof(Vintagestory.Server.ServerMain);
        var server = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(serverType);
        var threadType = serverType.Assembly.GetType("Vintagestory.Server.ChunkServerThread")!;
        var thread = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(threadType);
        var systemType = serverType.Assembly.GetType("Vintagestory.Server.ServerSystemLoadAndSaveGame")!;
        var system = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(systemType);
        Set(server, "chunkThread", thread); Set(thread, "loadsavegame", system); Set(system, "savingLock", new object()); Set(system, "chunkthread", thread);
        var api = DispatchProxy.Create<Vintagestory.API.Server.ICoreServerAPI, SaveApiProxy>();
        ((SaveApiProxy)(object)api).World = server;
        var begun = 0; var confirmed = 0; var failures = new List<string>();
        using var adapter = new SaveCompletionAdapter(api, () => { begun++; return () => confirmed++; }, failures.Add);
        DeferredGenerationChecks.Run(adapter);
        var type = typeof(SaveCompletionAdapter);
        object? Invoke(string name, params object?[] arguments) => type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments);
        // Invoke the actually patched idle worker once: no completed save exists.
        systemType.GetMethod("OnSeparateThreadTick")!.Invoke(system, null); adapter.Tick(); Check(confirmed == 0, "Idle worker acknowledged a save");
        object?[] begin = [null]; Invoke("Begin", begin); Set(thread, "runOffThreadSaveNow", true);
        adapter.Tick(); Check(begun == 1 && confirmed == 0, "Slow save acknowledged early");
        try { SaveCompletionAdapter.CaptureReadFence(); throw new Exception("Surface extraction ran during save"); } catch (IOException) { }
        object?[] worker = [null]; Invoke("BeginWorker", worker);
        var error = new IOException("synthetic disk failure");
        Check(ReferenceEquals(error, Invoke("FinishWorker", worker[0], error)), "Save exception was swallowed");
        adapter.Tick(); Check(confirmed == 0 && failures.Count == 1, "Failed save was acknowledged");
        Invoke("BeginWorker", worker); Set(thread, "runOffThreadSaveNow", false); Invoke("FinishWorker", worker[0], null);
        Check(confirmed == 0, "Save completion was delivered off the game tick");
        adapter.Tick(); Check(confirmed == 1, "Successful asynchronous save was not acknowledged");
        var fence = SaveCompletionAdapter.CaptureReadFence(); Invoke("Begin", begin);
        try { SaveCompletionAdapter.ValidateReadFence(fence); throw new Exception("Changed save did not invalidate the read fence"); } catch (IOException) { }
        return Task.CompletedTask;
    }
}
public class SaveApiProxy : DispatchProxy
{
    public object? World;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name == "get_World" ? World : throw new NotSupportedException(method?.Name);
}
