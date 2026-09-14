using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using ServerMap;
using ServerMap.Render;
using ServerMap.Web;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

static class RoadChecks
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static void Field(object instance, string name, object value) => instance.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

    public static void Run()
    {
        var root = Directory.CreateTempSubdirectory("LauncherGo-road-save-").FullName;
        try
        {
            var blocks = new Dictionary<string, Block>
            {
                ["game:stonepath-free"] = new() { WalkSpeedMultiplier = 1.3f },
                ["chiseltools:pathedchiseledblock"] = new() { WalkSpeedMultiplier = 2.5f }
            };
            var world = Proxy.Make<IServerWorldAccessor>((method, args) => method.Name switch
            {
                "GetBlock" when args?[0] is AssetLocation code => blocks[code.ToString()],
                "get_AllOnlinePlayers" => Array.Empty<IPlayer>(),
                _ => throw new InvalidOperationException("Road recognition unexpectedly needs " + method.Name)
            });
            var api = Proxy.Make<ICoreServerAPI>((method, _) => method.Name == "get_World" ? world : Proxy.Default(method.ReturnType));
            var surface = new SurfaceRegion();
            for (var x = 0; x < 6; x++)
            {
                surface.Valid[x] = true; surface.Heights[x] = 64;
                surface.Codes[x] = x < 3 ? "game:stonepath-free" : "chiseltools:pathedchiseledblock";
            }
            surface.Save(SurfaceRegion.PathFor(root, 0, 0));
            var roads = new RoadIndex(api, root, null!, null!);
            var settings = new MapManagementSettings.RoadSettings { DeepScan = false };
            IReadOnlyList<RoadIndex.Segment> Query() => roads.Query(null, settings, [(0, 0)]);
            var initial = Query();
            Require(initial.Count == 2 && initial.Any(line => Math.Abs(line.SpeedMultiplier - 1.3) < .00001 && line.Color == "#FFFFFF")
                && initial.Any(line => line.SpeedMultiplier == 2.5 && line.Color == "#FFFF00"), "Road query needs a player/colormap or lost native material speeds");

            using var cache = new MapCacheState(Path.Combine(root, "cache-state.db"));
            using var events = new LiveEventHub();
            var web = (ServerMapWebServer)RuntimeHelpers.GetUninitializedObject(typeof(ServerMapWebServer));
            var versions = new ConcurrentDictionary<string, long>(); versions["roads"] = 1;
            Field(web, "roads", roads); Field(web, "events", events); Field(web, "layerVersions", versions);
            Field(web, "knownRegions", new ConcurrentDictionary<(int X, int Z), byte>());
            var mod = new ServerMapModSystem(); Field(mod, "cache", cache); Field(mod, "web", web);
            var tileField = typeof(RoadIndex).GetField("tiles", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var beforeSave = (IDictionary)tileField.GetValue(roads)!;
            var oldEntries = beforeSave.GetEnumerator();
            Require(oldEntries.MoveNext(), "Initial road query did not populate its cache");
            var oldEntry = oldEntries.Entry;
            var dirty = typeof(ServerMapModSystem).GetMethod("OnChunkDirty", BindingFlags.Instance | BindingFlags.NonPublic)!;
            dirty.Invoke(mod, [new Vec3i(0, 1, 0), null, EnumChunkDirtyReason.MarkedDirty]);
            Require(ReferenceEquals(beforeSave, tileField.GetValue(roads)) && versions["roads"] == 1,
                "An unsaved change invalidated roads before the saved block data is available");
            var saved = typeof(ServerMapModSystem).GetMethod("QueueSaved", BindingFlags.Instance | BindingFlags.NonPublic)!;
            saved.Invoke(mod, [cache.Freeze()]);
            Require(!ReferenceEquals(beforeSave, tileField.GetValue(roads)) && versions["roads"] == 2,
                "Saved underground changes must invalidate and publish roads even when the surface is unchanged");
            Require(Query().Count == 2, "Saved underground changes damaged the unchanged surface roads");
            saved.Invoke(mod, [new Dictionary<string, long>()]);
            Require(versions["roads"] == 2, "An empty save unnecessarily refreshed the road layer");

            surface.Valid[2] = false; surface.Valid[3] = false;
            surface.Save(SurfaceRegion.PathFor(root, 0, 0));
            web.InvalidateRoads(publish: true);
            beforeSave.Clear(); beforeSave[oldEntry.Key] = oldEntry.Value;
            var updated = Query();
            Require(updated.Select(line => line.GroupId).Distinct().Count() == 2 && versions["roads"] == 3,
                "An old scan refilled the live cache or a saved removal stayed connected");
            Console.WriteLine("PASS roads: no online player/colormap, native speeds, post-save underground invalidation, empty saves, stale cache isolation and saved removals");
        }
        finally { Directory.Delete(root, true); }
    }
}
