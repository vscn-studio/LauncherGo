using System.Collections.Concurrent;
using System.Reflection;
using ServerMap;
using ServerMap.Render;
using ServerMap.Web;
using ServerMap.World;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Server;

// Exercise the real coordinator with an already persisted incomplete column
// patch. No world reader is supplied: save retries must not turn it into IO.
static class DeferredGenerationChecks
{
    public static void Run(SaveCompletionAdapter adapter)
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        static void Set(object value, string name, object field) => value.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(value, field);
        var root = Directory.CreateTempSubdirectory("map-deferred-generation-").FullName;
        try
        {
            using var state = new MapCacheState(Path.Combine(root, "cache-state.db"));
            state.Set("column:0_0", "yes"); state.Set("column:1_1", "yes");
            File.WriteAllText(Path.Combine(root, "translocators.json"), "[]");
            var web = (ServerMapWebServer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ServerMapWebServer));
            Set(web, "root", root);
            Set(web, "translocators", new TranslocatorIndex(Path.Combine(root, "translocators.json"), _ => { }));
            Set(web, "knownRegions", new ConcurrentDictionary<(int X, int Z), byte>());
            var system = new ServerMapModSystem();
            Set(system, "cache", state); Set(system, "web", web); Set(system, "saveAdapter", adapter);
            object? Invoke(string method, params object?[] args) => typeof(ServerMapModSystem).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(system, args);
            var work = state.Request("0_0", "changes", columns: new() { [0] = 123 }, objectYs: new() { [0] = [0] });
            var patch = new SurfaceRegion(32) { Generation = 123, ObjectVersion = state.Epoch + ":0", AwaitingSave = true };
            patch.Save(Path.Combine(root, "surface", "0_0", "0.br"));
            Check((RenderQueueOutcome)Invoke("RenderRegion", new ChunkKey(0, 0, 0))! == RenderQueueOutcome.Yield, "Incomplete column did not yield");
            Check(state.AwaitingSave == 0 && state.DeferredGeneration == 1, "Incomplete generation was incorrectly reinserted into the save journal");
            Check(state.Find("0_0")!.Columns.Count == 0, "Incomplete column blocked the rest of its region");
            state.Complete("0_0", work.Revision);
            for (var save = 0; save < 3; save++) Invoke("QueueSaved", state.Freeze());
            Check(state.PendingCount == 0 && state.AwaitingSave == 0, "Unchanged autosaves requeued the unfinished border");

            var chunk = DispatchProxy.Create<IWorldChunk, GenerationChunkProxy>();
            var map = new ServerMapChunk { CurrentIncompletePass = EnumWorldGenPass.PreDone };
            ((GenerationChunkProxy)(object)chunk).Map = map;
            Invoke("OnChunkColumnLoaded", new Vec2i(0, 0), new IWorldChunk[] { chunk });
            Check(state.AwaitingSave == 0, "A still unfinished load was queued for saving");
            map.CurrentIncompletePass = EnumWorldGenPass.Done;
            Invoke("OnChunkColumnLoaded", new Vec2i(1, 1), new IWorldChunk[] { chunk });
            Invoke("OnChunkDirty", new Vec3i(1, 0, 1), chunk, EnumChunkDirtyReason.NewlyLoaded);
            Check(state.AwaitingSave == 0, "Walking through cached terrain created map work");
            Invoke("OnChunkColumnLoaded", new Vec2i(0, 0), new IWorldChunk[] { chunk });
            var frozen = state.Freeze();
            Check(frozen.Count == 1, "Finishing deferred generation did not await its save");
            Invoke("OnChunkDirty", new Vec3i(0, 0, 0), chunk, EnumChunkDirtyReason.MarkedDirty);
            Invoke("QueueSaved", frozen);
            Check(state.AwaitingSave == 1, "Confirming a save erased a later mutation");
            Check(state.Find("0_0")!.Columns.Count == 1 && state.Find("0_0")!.ObjectYs.Count == 0, "Generated column did not request all vertical translocator slices");
            Console.WriteLine("PASS unfinished generation deferral, repeated autosaves, ordinary loads and later generation/save fences");
        }
        finally { Directory.Delete(root, true); }
    }
}

public class GenerationChunkProxy : DispatchProxy
{
    public IMapChunk? Map;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name == "get_MapChunk" ? Map : throw new NotSupportedException(method?.Name);
}
