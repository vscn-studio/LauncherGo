using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using HarmonyLib;
using ServerMap;
using ServerMap.Web;
using ServerMap.World;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

internal static class Program
{
    static async Task Main(string[] args)
    {
        var game = Path.GetFullPath(args[0]);
        AssemblyLoadContext.Default.Resolving += (_, name) => {
            foreach (var directory in new[] { game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods") })
            {
                var path = Path.Combine(directory, name.Name + ".dll");
                if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            }
            return null;
        };
        await Run();
    }
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Field(object value, string name, object content) => value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(value, content);
    static bool SkipVanilla() => false;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static async Task Run()
    {
        var directory = Directory.CreateTempSubdirectory("servermap-ore-tests-").FullName;
        var path = Path.Combine(directory, "ore.json");
        var logs = new List<string>();
        var store = new OreHeatmapStore(path, GlobalConstants.ChunkSize, logs.Add);
        var now = DateTimeOffset.UtcNow;
        OreHeatmapStore.OreValue[] copper = [new("copper", .3, 3.14), new("cassiterite", .01, .01)];
        Check(store.Record(-.1, 31.9, copper, now), "valid record rejected");
        var all = store.Query((-100, -100, 100, 100), null);
        var cell = all.Cells.Single();
        Check(GlobalConstants.ChunkSize == 32 && cell.MinX == -32 && cell.MinZ == 0 && cell.MaxX == 0 && cell.MaxZ == 32, "wrong chunk size or negative coordinate rounding");
        Check(cell.Density == 4 && cell.Ores.Single(o => o.Code == "copper").PartsPerThousand == 3.14, "density / ppt lost");
        Check(store.Query((-100, -100, 100, 100), "cassiterite").Cells.Single().Density == 1, "trace filter failed");
        Check(store.Query((200, 200, 300, 300), null).Cells.Length == 0, "bbox filtering failed");
        Check(store.Query((200, 200, 300, 300), "copper").OreCodes.Length == 0, "catalog leaked outside bbox");
        Check(!store.Record(double.NaN, 0, copper, now), "NaN coordinate accepted");
        Check(!store.Record(0, 0, [new("copper", double.PositiveInfinity, 1)], now), "infinite factor accepted");
        Check(!store.Record(0, 0, [new("copper", 1, -1)], now), "negative ppt accepted");
        store.Record(-1, 0, [new("copper", .002, 0)], now.AddSeconds(1));
        Check(store.Query((-100, -100, 100, 100), null).Cells.Single().Density == 0, "latest empty reading must replace earlier heat");
        store.Record(-1, 0, copper, now);
        store.Save();
        var restored = new OreHeatmapStore(path, GlobalConstants.ChunkSize, logs.Add);
        Check(restored.Query((-100, -100, 100, 100), null).Cells.Single().SampledAt == now, "persist/reload failed");
        Check(store.RecordNode(16, 48, 16, 6, [new OreHeatmapStore.NodeValue("copper", 34)], now.AddSeconds(2)), "node record rejected");
        var node = store.Query((-100, -100, 100, 100), "copper", mode: "node").Cells.Single();
        Check(node.Mode == "node" && node.SampleY == 48 && node.Radius == 6 && node.Nodes.Single().Blocks == 34, "node sample lost");
        Check(store.Query((-100, -100, 100, 100), null, mode: "density").Cells.All(cell => cell.Mode == "density"), "mode filter leaked node records");
        Check(logs.Count == 0, "save failed");
        var corruptPath = Path.Combine(directory, "corrupt.json");
        File.WriteAllText(corruptPath, "not json");
        try { _ = new OreHeatmapStore(corruptPath, 32, logs.Add); throw new Exception("corrupt store accepted"); }
        catch (JsonException) { }
        Check(File.ReadAllText(corruptPath) == "not json", "corrupt file overwritten");
        try { _ = new OreHeatmapStore(path, 64, logs.Add); throw new Exception("incompatible chunk size accepted"); }
        catch (InvalidDataException) { }
        var otherWorld = new OreHeatmapStore(Path.Combine(directory, "other-world.json"), 32, logs.Add);
        Check(otherWorld.Query((-100, -100, 100, 100), null).Cells.Length == 0, "world data leaked");
        for (var i = 0; i <= OreHeatmapStore.MaxQueryCells; i++) otherWorld.Record(i * 32, 0, copper, now);
        var limited = otherWorld.Query((-1, -1, 1e6, 100), null);
        Check(limited.Truncated && limited.Cells.Length == OreHeatmapStore.MaxQueryCells, "response cap failed");

        // Run the actual Harmony postfix against the installed game's DidProbe signature.
        // Only the vanilla body is skipped, as this harness has no live world/map manager.
        var web = (ServerMapWebServer)RuntimeHelpers.GetUninitializedObject(typeof(ServerMapWebServer));
        Field(web, "oreHeatmap", store);
        using var stop = new CancellationTokenSource();
        using var events = new LiveEventHub();
        Field(web, "stop", stop); Field(web, "events", events);
        var announcements = new AnnouncementStore(Path.Combine(directory, "announcement.json"));
        var notebook = new MapNotebookStore(Path.Combine(directory, "notebook.json"));
        using var exploration = new ExplorationStore(directory);
        using var alliances = new AllianceStore(directory);
        Field(web, "announcements", announcements); Field(web, "notebook", notebook);
        Field(web, "exploration", exploration); Field(web, "alliances", alliances);
        Field(web, "config", new ServerMap.Configuration.ServerMapConfig());
        Field(web, "auth", new MapAuthStore(Path.Combine(directory, "auth.json")));
        void Policy(MapManagementSettings settings) => announcements.Save("", "", "test", management: settings);
        Policy(new() { FogEnabled = false });
        var world = Stub.Create(typeof(ICoreServerAPI).GetProperty("World")!.PropertyType, (method, _) => method.Name == "get_AllOnlinePlayers" ? Array.Empty<IPlayer>() : null);
        var mod = new ServerMapModSystem(); Field(mod, "web", web);
        var loaderType = typeof(ICoreAPI).GetProperty("ModLoader")!.PropertyType;
        var loader = Stub.Create(loaderType, (method, _) => method.Name == "GetModSystem" ? mod : null);
        var logger = Stub.Create(typeof(ILogger), (_, args) => { Console.WriteLine(string.Join(" ", args?.Select(value => value is object[] nested ? string.Join(" ", nested.Select(v => v?.ToString())) : value?.ToString()) ?? [])); return null; });
        var serverApi = (ICoreServerAPI)Stub.Create(typeof(ICoreServerAPI), (method, _) => method.Name switch { "get_Side" => EnumAppSide.Server, "get_ModLoader" => loader, "get_Logger" => logger, "get_World" => world, _ => null });
        Field(mod, "sapi", serverApi); Field(web, "api", serverApi);
        var oreMap = new ModSystemOreMap(); Field(oreMap, "api", serverApi);
        var entity = new EntityPlayer();
        var player = (IServerPlayer)Stub.Create(typeof(IServerPlayer), (method, _) => method.Name switch { "get_Entity" => entity, "get_PlayerUID" => "player", _ => null });
        var patchType = typeof(ServerMapModSystem).Assembly.GetType("ServerMap.Network.OreMapCapturePatch", true)!;
        var target = AccessTools.Method(typeof(ModSystemOreMap), "DidProbe");
        var harmony = new Harmony("servermap.tests.ore");
        try
        {
            harmony.CreateClassProcessor(patchType).Patch();
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(SkipVanilla), BindingFlags.NonPublic | BindingFlags.Static)!));
            var reading = new PropickReading { Position = new(64, 50, 64), OreReadings = new() { ["copper"] = new OreReading { TotalFactor = .7, PartsPerThousand = 4.2 } } };
            oreMap.DidProbe(reading, player);
            Check(store.Query((65, 65, 66, 66), null).Cells.Single().Density == 7, "server postfix did not record");
            var before = store.Query((-100, -100, 100, 100), null).Version;
            entity.Pos.Dimension = 1;
            oreMap.DidProbe(reading, player);
            Check(store.Query((-100, -100, 100, 100), null).Version == before, "other dimension captured");
            entity.Pos.Dimension = 0;
            Field(oreMap, "api", (ICoreAPI)Stub.Create(typeof(ICoreAPI), (method, _) => method.Name == "get_Side" ? EnumAppSide.Client : throw new Exception("Client must never resolve the store")));
            oreMap.DidProbe(reading, player);
            Check(store.Query((-100, -100, 100, 100), null).Version == before, "client API reached store");
        }
        finally { harmony.UnpatchAll(harmony.Id); }

        // Exercise production HTTP routing without constructing the unrelated terrain renderer.

        var versions = new ConcurrentDictionary<string,long>();
        foreach (var layer in (string[])typeof(ServerMapWebServer).GetField("Layers", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!) versions[layer] = 1;
        Field(web, "layerVersions", versions);
        var handle = typeof(ServerMapWebServer).GetMethod("Handle", BindingFlags.NonPublic | BindingFlags.Instance)!;
        using var port = new TcpListener(IPAddress.Loopback, 0); port.Start(); var number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{number}/"); listener.Start();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{number}/"), Timeout = TimeSpan.FromSeconds(10) };
        async Task<HttpResponseMessage> Request(HttpMethod method, string route)
        {
            var pending = listener.GetContextAsync(); var response = client.SendAsync(new HttpRequestMessage(method, route));
            handle.Invoke(web, [await pending]); return await response;
        }
        var countBefore = store.Query((-100, -100, 100, 100), null).Version;
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
            Check((await Request(method, "api/v1/layers/mineral-heatmap")).StatusCode == HttpStatusCode.MethodNotAllowed, "write request not rejected");
        Check((await Request(HttpMethod.Get, "api/v1/layers/mineral-heatmap?bbox=NaN,0,1,1")).StatusCode == HttpStatusCode.BadRequest, "NaN bbox accepted");
        Check((await Request(HttpMethod.Get, "api/v1/layers/mineral-heatmap")).StatusCode == HttpStatusCode.BadRequest, "missing bbox accepted");
        var response = await Request(HttpMethod.Get, "servermap/api/v1/layers/mineral-heatmap?bbox=-100,-100,100,100&ore=copper");
        Check(response.IsSuccessStatusCode && response.Headers.CacheControl?.NoStore == true, "heatmap GET/cache policy failed");
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Check(json.RootElement.GetProperty("features").GetArrayLength() == 3, "heatmap HTTP response missing samples");
        Check(json.RootElement.GetProperty("features")[0].GetProperty("geometry").GetProperty("type").GetString() == "Polygon", "heatmap geometry is not polygon");
        var manifest = JsonDocument.Parse(await (await Request(HttpMethod.Get, "api/v1/layers/manifest")).Content.ReadAsStringAsync());
        Check(manifest.RootElement.GetProperty("layers").EnumerateArray().Any(layer => layer.GetProperty("id").GetString() == "mineral-heatmap" && !layer.GetProperty("visible").GetBoolean()), "manifest missing optional heatmap");
        Check(store.Query((-100, -100, 100, 100), null).Version == countBefore, "HTTP changed server records");
        var query = typeof(ServerMapWebServer).GetMethod("OreHeatmap", BindingFlags.NonPublic | BindingFlags.Instance)!;
        JsonElement Query(MapAuthStore.Principal? principal) => JsonSerializer.SerializeToElement(query.Invoke(web, [(-100d, -100d, 100d, 100d), null, null, principal, null, null]));
        void Empty(JsonElement result, string reason) => Check(result.GetProperty("features").GetArrayLength() == 0 && result.GetProperty("ores").GetArrayLength() == 0 && !result.GetProperty("truncated").GetBoolean(), reason);
        var ordinary = new MapAuthStore.Principal("player", "Player", false);
        var admin = new MapAuthStore.Principal("admin", "Admin", true);
        Policy(new() { FogEnabled = true });
        Empty(Query(null), "anonymous fog leak");
        Empty(Query(ordinary), "unexplored fog leak");
        Check(Query(admin).GetProperty("features").GetArrayLength() == 3, "admin bypass failed");
        Policy(new() { FogEnabled = true, AdminsBypassFog = false });
        Empty(Query(admin), "disabled admin bypass leaked");
        exploration.RecordNativeChunks(player, [(long)2 << 32 | 2u]);
        Check(Query(ordinary).GetProperty("features").GetArrayLength() == 1, "explored cell unavailable");
        var region = notebook.SaveRegion(null, "inner hidden area", 70, 70, 71, 71);
        Empty(Query(ordinary), "hidden area inside cell leaked geometry/catalog");
        Policy(new() { FogEnabled = false });
        Check(Query(null).GetProperty("features").GetArrayLength() == 2, "hidden region ignored with fog off");
        Policy(new() { FogEnabled = false, Layers = new() { ["mineral-heatmap"] = new(Forbidden: true) } });
        Empty(Query(admin), "forbidden layer bypassed");
        var hidden = otherWorld.Query((-1, -1, 1e6, 100), null, (x, z, xx, zz) => x == 320000);
        Check(hidden.Cells.Length == 1 && !hidden.Truncated, "invisible cells consumed query limit");
        Check(otherWorld.Query((-1, -1, 1e6, 100), null, (_, _, _, _) => false).OreCodes.Length == 0, "hidden catalog leaked");
        Console.WriteLine("PASS: privacy policies, fog, hidden-area intersection, dimension gating, persistence, chunk boundaries, density/filtering, invalid data, world isolation, response cap, real Harmony server/client gating, HTTP write rejection and GeoJSON.");
        Console.WriteLine("Test artifacts: " + directory);
    }
}

public class Stub : DispatchProxy
{
    private System.Func<MethodInfo, object?[]?, object?> call = null!;
    public static object Create(Type type, System.Func<MethodInfo, object?[]?, object?> call)
    {
        var result = DispatchProxy.Create(type, typeof(Stub)); ((Stub)result).call = call; return result;
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args) => call(method!, args);
}
