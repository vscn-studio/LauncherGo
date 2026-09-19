using System.Reflection;
using System.Text.Json;
using ServerMap.Render;
using ServerMap.World;
using Vintagestory.API.Common;
using Vintagestory.Common;
using Vintagestory.Server;

// Use the real palette capture and surface selection with engine chunk storage.
// This runs before Block.OnLoaded populates notSnowCovered, as the server does.
static class SnowSurfaceChecks
{
    public static void Run()
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var blocks = new List<Block>();
        Block Add(string code, EnumBlockMaterial material, params (string Key, string Value)[] variants)
        {
            var block = new Block { BlockId = blocks.Count, Code = new AssetLocation(code), BlockMaterial = material };
            foreach (var (key, value) in variants) block.VariantStrict[key] = value;
            blocks.Add(block);
            return block;
        }
        Add("game:air", EnumBlockMaterial.Air);
        var soil = Add("game:soil-low-normal", EnumBlockMaterial.Soil);
        var layer = Add("game:snowlayer-1", EnumBlockMaterial.Snow);
        var snowBlock = Add("game:snowblock", EnumBlockMaterial.Snow);
        var water = Add("game:water-still-7", EnumBlockMaterial.Water);
        var pairs = new List<(Block Snow, Block Free)>();
        foreach (var color in new[] { "black", "brown", "cream", "fire", "gray", "orange", "red", "tan" })
        foreach (var orientation in new[] { "north", "east", "south", "west" })
        {
            Block Stair(string cover) => Add($"game:clayshinglestairs-{color}-up-{orientation}-{cover}", EnumBlockMaterial.Ceramic,
                ("variant", color), ("verticalorientation", "up"), ("horizontalorientation", orientation), ("cover", cover));
            var free = Stair("free");
            pairs.Add((Stair("snow"), free));
        }
        var slab = Add("game:plankslab-oak-down-free", EnumBlockMaterial.Wood, ("wood", "oak"), ("side", "down"), ("cover", "free"));
        foreach (var cover in new[] { "snow", "snow2", "snow3" })
            pairs.Add((Add($"game:plankslab-oak-down-{cover}", EnumBlockMaterial.Wood, ("wood", "oak"), ("side", "down"), ("cover", cover)), slab));
        var unmatched = Add("custom:stairs-snow", EnumBlockMaterial.Ceramic, ("cover", "snow"));
        var namedSnow = Add("custom:ornament-snow", EnumBlockMaterial.Wood);
        var micro = Add("game:chiseledblock-snow2", EnumBlockMaterial.Stone, ("cover", "snow2"));
        Add("game:chiseledblock-free", EnumBlockMaterial.Stone, ("cover", "free"));
        var world = SnowFixtureProxy.Make<IWorldAccessor>((name, _) => name == "get_Blocks" ? blocks : null);
        var logger = SnowFixtureProxy.Make<ILogger>((_, _) => null);
        var api = SnowFixtureProxy.Make<ICoreAPI>((name, _) => name == "get_World" ? world : name == "get_Logger" ? logger : null);
        var palette = MapPalette.Capture(api);
        Check(palette.IsSurfaceCover(layer.Id) && palette.IsSurfaceCover(snowBlock.Id), "Standalone snow no longer selects the block below");
        Check(!palette.IsSurfaceCover(micro.Id) && palette.WithoutSnowCover(micro.Id) == micro.Id, "Chiseled block bypasses its entity material");
        Check(!palette.IsSurfaceCover(namedSnow.Id) && palette.WithoutSnowCover(namedSnow.Id) == namedSnow.Id, "A suffix alone changed a non-snow block");

        var renderer = new MapRenderer(null!, "", 256, palette);
        var select = typeof(MapRenderer).GetMethod("ReadSurfaceBlock", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var pool = new ChunkDataPool(32, null!);
        (int Id, int Y) Sample(int topY, params (int Y, int Solid, int Fluid)[] cells)
        {
            var chunks = new Dictionary<int, ServerChunk?>();
            foreach (var (y, solid, fluid) in cells)
            {
                if (!chunks.TryGetValue(y >> 5, out var chunk))
                {
                    chunk = (ServerChunk)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ServerChunk));
                    typeof(WorldChunk).GetField("chunkdata", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(chunk, pool.Request());
                    chunks[y >> 5] = chunk;
                }
                var data = (ChunkData)chunk!.Data;
                data[(y & 31) * 1024] = solid;
                data.SetFluid((y & 31) * 1024, fluid);
            }
            object?[] args = [chunks, 0, 0, 0, topY, 0];
            var id = (int)select.Invoke(renderer, args)!;
            return (id, (int)args[4]!);
        }
        foreach (var (snow, free) in pairs)
        {
            Check(snow.notSnowCovered == null, "Fixture unexpectedly initialized the runtime snow mapping");
            Check(!palette.IsSurfaceCover(snow.Id), $"{snow.Code} classified as a separate snow layer");
            Check(Sample(32, (32, snow.Id, 0), (31, soil.Id, 0)) == (free.Id, 32), $"{snow.Code} lost its height/material/orientation");
            Check(Sample(32, (32, free.Id, 0), (31, soil.Id, 0)) == (free.Id, 32), "Uncovered block changed");
            Check(Sample(32, (32, layer.Id, 0), (31, snow.Id, 0), (30, soil.Id, 0)) == (free.Id, 31), "Snow layer over snowy stairs skipped both blocks");
            Check(Sample(40, (32, snow.Id, 0), (31, soil.Id, 0)) == (free.Id, 32), "Air fallback lost snowy stairs");
            Check(Sample(32, (32, snow.Id, water.Id), (31, soil.Id, 0)) == (free.Id, 32), "Fluid replaced a solid snowy stair");
        }
        Check(Sample(32, (32, unmatched.Id, 0), (31, soil.Id, 0)) == (unmatched.Id, 32), "Missing free counterpart became soil");
        Check(Sample(32, (32, layer.Id, 0), (31, soil.Id, 0)) == (soil.Id, 31), "Standalone snow layer changed behavior");
        Check(Sample(32, (32, snowBlock.Id, 0), (31, snowBlock.Id, 0), (30, soil.Id, 0)) == (snowBlock.Id, 31), "Stacked snow was scanned through repeatedly");
        Check(Sample(32, (32, layer.Id, 0), (31, 0, water.Id)) == (water.Id, 31), "Snow over water lost the fluid layer");
        Check(Sample(32, (32, 99999, 0), (31, soil.Id, 0)) == (MapPalette.MissingBlockId, 32), "Unknown material became soil");

        // Confirm the selected stair drives both persisted height and PNG color.
        var root = Directory.CreateTempSubdirectory("map-snow-surface-").FullName;
        try
        {
            var (snow, free) = pairs.First(pair => pair.Free.Code.Path == "clayshinglestairs-red-up-north-free");
            var selected = Sample(32, (32, snow.Id, 0), (31, soil.Id, 0));
            var surface = new SurfaceRegion();
            surface.Codes[0] = palette.Get(selected.Id).Code;
            surface.Heights[0] = (ushort)selected.Y; surface.Valid[0] = true;
            surface.SepiaKeys[0] = palette.Get(selected.Id).MapColorCode;
            var path = SurfaceRegion.PathFor(root, 0, 0); surface.Save(path);
            surface = SurfaceRegion.Load(path)!;
            Check(surface.Heights[0] == 32 && surface.Codes[0] == free.Code.ToString(), "Surface cache lost stair identity");
            palette.ApplyClientColormap(JsonSerializer.Serialize(new Dictionary<string, uint[]>
            {
                [free.Code.ToString()] = Enumerable.Repeat(0xcc3311u, 30).ToArray(),
                [soil.Code.ToString()] = Enumerable.Repeat(0x116633u, 30).ToArray()
            }), 1, out _);
            var pngRenderer = new MapRenderer(null!, root, 256, palette);
            Check(pngRenderer.RenderSurface(new ChunkKey(0, 0, 0), surface), "Stair PNG was not rendered");
            var png = PngEncoder.Decode(File.ReadAllBytes(Path.Combine(root, "2d", "basic", "0", "0_0.png")));
            Check(png[0] > png[1] && png[1] > png[2] && png[3] == 255, "Stair pixel is soil-colored or transparent");
            Check(pngRenderer.RenderSurface(new ChunkKey(0, 0, 0), surface, "sepia"), "Sepia stair PNG was not rendered");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {pairs.Count} snow variants: real palette, chunk selection, snow layers, height, fluids, missing variants and cached PNG color");
    }
}

public class SnowFixtureProxy : DispatchProxy
{
    private System.Func<string, object?[]?, object?> invoke = null!;
    public static T Make<T>(System.Func<string, object?[]?, object?> call) where T : class
    {
        var proxy = Create<T, SnowFixtureProxy>(); ((SnowFixtureProxy)(object)proxy).invoke = call; return proxy;
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args) => invoke(method!.Name, args);
}
