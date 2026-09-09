using System.Collections;
using System.Reflection;
using System.Text.Json;
using ServerMap.Render;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

if (args.Length is not (2 or 4)) throw new ArgumentException("GameRoot, extracted VS Roofing directory, optionally save and colormap paths required");
var gameRoot = Path.GetFullPath(args[0]);
AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
{
    var name = new AssemblyName(request.Name).Name + ".dll";
    foreach (var folder in new[] { gameRoot, Path.Combine(gameRoot, "Lib"), Path.Combine(gameRoot, "Mods"), args[1] })
    { var file = Path.Combine(folder, name); if (File.Exists(file)) return Assembly.LoadFrom(file); }
    return null;
};
Checks.Run(args[1]);
if (args.Length == 4) SavedRoofChecks.Run(args[1], args[2], args[3]);

static class Checks
{
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run(string roofingRoot)
    {
        // Exercise the actual 1.7.2 assembly and asset definitions; no mock mod
        // classes or world mutations. Its normal SetMaterials computes variants.
        var assembly = Assembly.LoadFrom(Path.Combine(roofingRoot, "vsroofing.dll"));
        var blockType = assembly.GetType("VSRoofing.RoofBlock", true)!;
        var block = (Block)Activator.CreateInstance(blockType)!;
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(roofingRoot, "assets/vsroofing/blocktypes/roof.json")), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        block.Attributes = JsonObject.FromJson(json.RootElement.GetProperty("attributes").GetRawText());
        block.BlockId = 1;
        block.Code = new AssetLocation("vsroofing:roof-east");
        // Actual server order: palette capture precedes RoofBlock.OnLoaded.
        var world = FixtureProxy.Make<IWorldAccessor>((method, _) => method == "get_Blocks" ? new List<Block> { block } : null);
        var logger = FixtureProxy.Make<ILogger>((_, _) => null);
        var api = FixtureProxy.Make<ICoreAPI>((method, _) => method == "get_World" ? world : method == "get_Logger" ? logger : null);
        var startupPalette = MapPalette.Capture(api);
        var startupRoofing = typeof(MapPalette).GetProperty("Roofing", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Require(startupRoofing.GetValue(startupPalette) == null, "Roof definitions captured before Block.OnLoaded");
        Require(!startupPalette.HasRoofingColormap, "Uninitialized roofing was considered complete");
        foreach (var method in new[] { "LoadRoofs", "LoadFrames" }) blockType.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(block, null);
        typeof(MapPalette).GetMethod("InitializeRoofing", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(startupPalette, null);
        var initializedRoofing = startupRoofing.GetValue(startupPalette)!;
        Require((int)initializedRoofing.GetType().GetProperty("RoofCount", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(initializedRoofing)! > 0, "GameReady retained empty roofing definitions");
        var adapter = new RoofingColors(block);
        var entityType = assembly.GetType("VSRoofing.AutoRoofEntity", true)!;
        var materialType = assembly.GetType("VSRoofing.AutoRoofMaterialCollection", true)!;
        Item Item(string code, string? variant = null, string? value = null)
        {
            var item = new Item { Code = new AssetLocation(code) };
            if (variant != null) item.VariantStrict[variant] = value!;
            return item;
        }
        var grass = Item("game:drygrass");
        var oak = Item("game:plank-oak", "wood", "oak");
        var pine = Item("game:plank-pine", "wood", "pine");
        oak.Textures = new() { ["wood"] = new(new AssetLocation("game:block/wood/debarked/oak")) };
        pine.Textures = new() { ["wood"] = new(new AssetLocation("game:block/wood/debarked/pine")) };
        var copper = Item("game:metalplate-copper", "metal", "copper");
        var tin = Item("game:metalplate-tin", "metal", "tin");
        var granite = Item("game:stone-granite", "rock", "granite");
        var shingle = Item("game:shingle-burned-red");
        shingle.Textures = new() { ["material"] = new(new AssetLocation("game:block/clay/shingles/red")) };
        var samples = adapter.Samples(new CollectibleObject[] { grass, oak, pine, copper, tin, granite, shingle }).ToDictionary(s => s.Key);
        string Prefix(string key) => RoofingColors.Prefix + key;
        BlockEntity Roof(params Item[] items)
        {
            var entity = (BlockEntity)Activator.CreateInstance(entityType)!;
            entityType.GetProperty("FrameStack")!.SetValue(entity, new ItemStack(oak));
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(materialType))!;
            foreach (var item in items)
            {
                var material = Activator.CreateInstance(materialType)!;
                materialType.GetField("Primary")!.SetValue(material, new ItemStack(item));
                list.Add(material);
            }
            entityType.GetMethod("SetMaterials")!.Invoke(entity, [list]);
            return entity;
        }
        string? Key(BlockEntity entity) => adapter.Resolve(entity, out _);
        Require(Key(Roof(grass)) == Prefix("roof/straw"), "Straw variant did not resolve from real mod materials");
        Require((string?)initializedRoofing.GetType().GetMethod("Resolve", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(initializedRoofing, [Roof(grass), 0]) == Prefix("roof/straw"), "Server startup palette cannot resolve straw after late mod loading");
        Require(samples[Prefix("roof/straw")].Texture.Base.ToString() == "game:block/hay/normal-top", "Straw sampled the frame texture");
        Require(Key(Roof(oak)) == Prefix("roof/wood-basic/game:plank-oak"), "Wood material identity was lost");
        Require(Key(Roof(pine)) != Key(Roof(oak)), "Same block id collapsed different wood species");
        Require(samples[Prefix("roof/wood-basic/game:plank-pine")].Texture.Base.Path.Contains("pine"), "Wood placeholder not resolved");
        Require(samples[Prefix("roof/stone/game:stone-granite")].Texture.Base.Path.Contains("granite")
            && samples[Prefix("roof/stone/game:stone-granite")].Texture.BlendedOverlays.Length > 0, "Stone composite texture lost its overlay");
        Require(samples[Prefix("roof/metal/game:metalplate-tin")].Texture.Base.Path.EndsWith("tin1"), "Specific metal match lost precedence over wildcard");
        Require(Key(Roof(oak, shingle)) == Prefix("roof/wood-shingle/game:shingle-burned-red"), "Two-layer roof did not use material index 1");
        Require(samples[Prefix("roof/wood-shingle/game:shingle-burned-red")].Texture.Base.Path == "block/clay/shingles/red", "Item texture was not included in client samples");
        var frame = Roof();
        Require(Key(frame)?.StartsWith(Prefix("frame/")) == true && samples.ContainsKey(Key(frame)!), "Uncovered frame has no sample");
        var snowy = Roof(grass);
        entityType.GetField("SnowCovered")!.SetValue(snowy, 2);
        Require(Key(snowy) == Prefix("snow"), "Snow-covered roof did not override material");
        var infill = (BlockEntityMicroBlock)Roof();
        infill.VoxelCuboids.Add(1); infill.BlockIds = [42];
        Require(adapter.Resolve(infill, out var infillId) == null && infillId == 42, "Chiseled roof infill was lost");

        var entries = new MapPalette.Entry?[] { new(0, "game:air", "land", false, false, false, false, false, false, false, true), new(1, "vsroofing:roof-east", "land", false, false, false, false, false, false, false, false) { IsRoof = true } };
        var palette = (MapPalette)Activator.CreateInstance(typeof(MapPalette), BindingFlags.NonPublic | BindingFlags.Instance, null, [entries], null)!;
        var roofingProperty = typeof(MapPalette).GetProperty("Roofing", BindingFlags.NonPublic | BindingFlags.Instance)!;
        roofingProperty.SetValue(palette, Activator.CreateInstance(roofingProperty.PropertyType, BindingFlags.NonPublic | BindingFlags.Instance, null, [block], null));
        Require(!palette.HasRoofingColormap, "Roof palette incorrectly complete before client samples arrive");
        var colors = new Dictionary<string, uint[]> {
            ["vsroofing:roof-east"] = Enumerable.Repeat(0u, 30).ToArray(),
            [Prefix("roof/straw")] = Enumerable.Repeat(0xC9AB68u, 30).ToArray(),
            [Prefix("roof/wood-basic/game:plank-oak")] = Enumerable.Repeat(0x78542Au, 30).ToArray(),
            [Prefix("roof/invalid")] = [123u]
        };
        Require(palette.ApplyClientColormap(JsonSerializer.Serialize(colors), 5, out _), "Extended client palette rejected");
        var snapshot = palette.CaptureColors()!;
        Require(palette.HasRoofingColormap, "Received roof samples did not satisfy colormap request");
        Require(snapshot.TryRoofColor(Key(Roof(grass)), 0, 50, 0, out var strawColor) && strawColor == ((byte)201, (byte)171, (byte)104), "Per-entity straw color failed");
        Require(snapshot.TryRoofColor(Key(Roof(oak)), 0, 50, 0, out var oakColor) && oakColor != strawColor, "Per-entity colors collapsed to block color");
        Require(!snapshot.TryRoofColor(Prefix("roof/invalid"), 0, 0, 0, out _), "Malformed dynamic color accepted");
        palette.ApplyClientColormap(JsonSerializer.Serialize(new Dictionary<string, uint[]> { ["vsroofing:roof-east"] = new uint[30] }), 5, out _);
        Require(!palette.HasRoofingColormap, "Legacy monthly cache would prevent requesting roofing colors");
        Require(snapshot.TryRoofColor(Key(Roof(grass)), 0, 50, 0, out _), "Palette replacement changed in-flight roof colors");
        Require(!palette.CaptureColors()!.TryRoofColor(Key(Roof(grass)), 0, 50, 0, out _), "Old colormap retained unrelated dynamic data");
        var temporary = Directory.CreateTempSubdirectory("LauncherGo-roofing-colors-").FullName;
        try
        {
            var tile = Path.Combine(temporary, "tile.png");
            File.WriteAllBytes(tile, []);
            File.WriteAllText(tile + ".colors", "client-colors-2:" + snapshot.Version);
            Require(!TileColorStamp.IsCurrent(tile, snapshot.Version), "Old roof tiles were not invalidated");
            TileColorStamp.Complete(tile, snapshot.Version);
            Require(TileColorStamp.IsCurrent(tile, snapshot.Version), "New roof tile stamp rejected");
        }
        finally { Directory.Delete(temporary, true); }
        Console.WriteLine($"PASS VS Roofing 1.7.2: {samples.Count} material samples; real entity variants, two-layer shingles, wood/stone/metal textures, frame/snow/infill, palette snapshots and tile invalidation");
    }
}

public class FixtureProxy : DispatchProxy
{
    private System.Func<string, object?[], object?> handler = null!;
    public static T Make<T>(System.Func<string, object?[], object?> handler) where T : class
    {
        var proxy = Create<T, FixtureProxy>();
        ((FixtureProxy)(object)proxy).handler = handler;
        return proxy;
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args) => handler(method!.Name, args ?? [])
        ?? (method.ReturnType != typeof(void) && method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null);
}
