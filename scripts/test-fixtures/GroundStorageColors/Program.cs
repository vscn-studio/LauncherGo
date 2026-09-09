using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ProtoBuf;
using ServerMap.Render;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

if (args.Length is < 1 or > 2) throw new ArgumentException("GameRoot [Save.vcdbs] required");
var gameRoot = Path.GetFullPath(args[0]);
AppDomain.CurrentDomain.AssemblyResolve += (_, request) => {
    var name = new AssemblyName(request.Name).Name + ".dll";
    foreach (var folder in new[] { gameRoot, Path.Combine(gameRoot, "Lib"), Path.Combine(gameRoot, "Mods") })
    { var file = Path.Combine(folder, name); if (File.Exists(file)) return Assembly.LoadFrom(file); }
    return null;
};
Checks.Run(args.Length == 2 ? args[1] : null);

static class Checks
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    internal static void Run(string? savePath)
    {
        var wood = new SampleItem { Code = new("game:plank-oak"), ItemId = 10, Color = 0xFF946A31 };
        var iron = new SampleItem { Code = new("game:ingot-iron"), ItemId = 11, Color = 0xFF434951 };
        var block = new SampleBlock { Code = wood.Code, BlockId = 42, Color = 0xFF112233 };
        var blocks = new Dictionary<int, Block> { [42] = block };
        var items = new Dictionary<int, Item> { [10] = wood, [11] = iron };
        var world = World(blocks, items);
        var colors = new GroundStorageColors(world);
        Require(GroundStorageColors.IsStorage(new BlockGroundStorage()) && GroundStorageColors.IsStorage(new BlockIngotPile()) && !GroundStorageColors.IsStorage(new Block()), "Pile detection failed");
        var storage = new BlockEntityGroundStorage();
        storage.Inventory[0].Itemstack = new ItemStack(wood, 64);
        string? Key(BlockEntity entity) => colors.Resolve(entity, 512001, 120, 512002);
        Require(Key(storage) == GroundStorageColors.Key(wood), "Ground storage lost material identity");
        storage.Inventory[0].Itemstack = new ItemStack(iron);
        Require(Key(storage) == GroundStorageColors.Key(iron), "Same groundstorage block ID collapsed different items");
        storage.Inventory[2].Itemstack = new ItemStack(wood);
        var mixed = Key(storage);
        Require(Enumerable.Range(0, 100).All(_ => Key(storage) == mixed), "Mixed storage colors flicker");
        var seen = Enumerable.Range(0, 100).Select(x => colors.Resolve(storage, x, 120, 1)).Distinct().Count();
        Require(seen == 2, "Mixed storage did not select both occupied slots");
        Require(GroundStorageColors.Key(block) != GroundStorageColors.Key(wood), "Item and block codes collided");
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) new ItemStack(iron, 8).ToBytes(writer);
        stream.Position = 0;
        var unresolved = new ItemStack(new BinaryReader(stream));
        var pile = new BlockEntityIngotPile(); pile.inventory[0].Itemstack = unresolved;
        Require(unresolved.Collectible == null && Key(pile) == GroundStorageColors.Key(iron) && unresolved.Collectible == null, "Offline pile IDs not resolved read-only");
        Require(Key(new BlockEntityGroundStorage()) == null, "Empty inventory produced white placeholder color");
        Require(GroundStorageColors.SampleColors(null!, wood).All(v => v == 0x946A31) && wood.Calls == 30, "Item color override, count or RGBA channel order failed");
        Require(GroundStorageColors.SampleColors(null!, block).All(v => v == 0x112233), "Block stack sampling used wrong overload");
        var texturePosition = new TextureAtlasPosition();
        var unknownPosition = new TextureAtlasPosition();
        var itemAtlas = Proxy.Make<IItemTextureAtlasAPI>((method,args) => method switch {
            "get_Item" => ((AssetLocation)args[0]!).Path == "wood" ? texturePosition : unknownPosition,
            "get_UnknownTexturePosition" => unknownPosition,
            // Simulate a stale sub-id pointing at a green, unrelated texture.
            "GetRandomColor" when args[0] is int => unchecked((int)0xFF00FF00),
            "GetRandomColor" when ReferenceEquals(args[0], texturePosition) => (int)args[1]! % 2 == 0 ? unchecked((int)0xFF956B32) : 0x0000FF00,
            _ => throw new Exception("Wrong item atlas request")
        });
        var blockAtlas = Proxy.Make<IBlockTextureAtlasAPI>((method,args) => method == "GetRandomColor" && (int)args[0]! == 7 ? unchecked((int)0xFF223344) : throw new Exception("Wrong block atlas request"));
        var client = Proxy.Make<ICoreClientAPI>((method,_) => method == "get_ItemTextureAtlas" ? itemAtlas : method == "get_BlockTextureAtlas" ? blockAtlas : null);
        var nativeItem = new Item { Code = new("game:firewood"), Textures = new() { ["wood"] = new CompositeTexture { Baked = new BakedCompositeTexture { TextureSubId = 9, BakedName = new("game:wood") } } } };
        Require(nativeItem.GetRandomColor(client,new ItemStack(nativeItem)) == unchecked((int)0xFF00FF00), "Fixture must reproduce stale atlas id returning green");
        Require(GroundStorageColors.SampleColors(client,nativeItem).All(v => v == 0x956B32), "Wrong atlas id or transparent green leaked into storage colors");
        Require(nativeItem.Textures["wood"].Baked.TextureSubId == 9, "Sampling mutated the game's texture registration");
        foreach (Item item in new Item[] { new ItemStone(), new ItemIngot() })
        {
            item.Code = new("game:test-pile"); item.Textures = nativeItem.Textures;
            Require(GroundStorageColors.SampleColors(client,item).All(v => v == 0x956B32), "Native stone/ingot subclass used stale atlas id");
        }
        nativeItem.Textures["wood"].Baked.BakedName = new("game:missing");
        try { GroundStorageColors.SampleColors(client,nativeItem); throw new Exception("Unknown atlas texture was accepted"); }
        catch (InvalidDataException) { }
        Require(GroundStorageColors.SampleColors(null!, new SampleItem { Color = 0xFF00FF00 }).All(v => v == 0x00FF00), "Legitimate opaque green was removed");
        try { GroundStorageColors.SampleColors(null!, new SampleItem { Color = 0x0000FF00 }); throw new Exception("Fully transparent texture was accepted"); }
        catch (InvalidDataException) { }
        Require(GroundStorageColors.SampleColors(client,new Block { Code = new("game:stone"), TextureSubIdForBlockColor = 7 }).All(v => v == 0x223344), "Native Block did not sample block atlas");
        var looseRock = new BlockLooseStones { Code = new("game:loosestones-granite-free"), TextureSubIdForBlockColor = 7 };
        var generator = new ServerMap.Client.ClientColormapSystem();
        typeof(ServerMap.Client.ClientColormapSystem).GetField("api",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(generator,client);
        var generate = typeof(ServerMap.Client.ClientColormapSystem).GetMethod("GenerateBlockColors",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var looseColors = (uint[])generate.Invoke(generator,[looseRock,new BlockPos(0,119,0)])!;
        Require(looseColors.All(v=>v==0x223344),"Loose rocks sampled the player's underlying block instead of their own material");

        var entries = new MapPalette.Entry?[] { new(0,"game:air","land",false,false,false,false,false,false,false,true), new(1,"game:groundstorage","land",false,false,false,false,false,false,false,false) { IsGroundStorage = true }, new(2,"game:looseboulders-granite-free","land",false,false,false,false,false,false,false,false) { IsLooseRock = true } };
        var palette = (MapPalette)Activator.CreateInstance(typeof(MapPalette), BindingFlags.NonPublic | BindingFlags.Instance, null, [entries], null)!;
        var property = typeof(MapPalette).GetProperty("GroundStorage", BindingFlags.NonPublic | BindingFlags.Instance)!;
        property.SetValue(palette, Activator.CreateInstance(property.PropertyType, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, null, [world], null));
        Require(!palette.HasGroundStorageColormap, "Old cache incorrectly satisfies ground storage request");
        var samples = new Dictionary<string,uint[]> { ["game:groundstorage"] = Enumerable.Repeat(0xFFFFFFu,30).ToArray(), [GroundStorageColors.Key(wood)] = GroundStorageColors.SampleColors(null!,wood), [GroundStorageColors.CompleteKey] = Enumerable.Repeat(1u,30).ToArray(), [GroundStorageColors.Prefix+"invalid"] = [1] };
        Require(palette.ApplyClientColormap(JsonSerializer.Serialize(samples),6,out _), "Extended palette rejected");
        var snapshot = palette.CaptureColors()!;
        Require(palette.HasGroundStorageColormap && snapshot.TryGroundColor(GroundStorageColors.Key(wood),0,0,0,out var rgb) && rgb == ((byte)148,(byte)106,(byte)49), "Storage color stayed generic white");
        Require(!snapshot.TryGroundColor(GroundStorageColors.Prefix+"invalid",0,0,0,out _), "Invalid samples accepted");
        samples.Remove(GroundStorageColors.CompleteKey);
        samples[GroundStorageColors.Prefix+"version-1"] = Enumerable.Repeat(1u,30).ToArray();
        Require(palette.ApplyClientColormap(JsonSerializer.Serialize(samples),6,out _) && !palette.HasGroundStorageColormap
            && !palette.CaptureColors()!.TryGroundColor(GroundStorageColors.Key(wood),0,0,0,out _), "Old green storage cache survived version upgrade");
        palette.ApplyClientColormap(JsonSerializer.Serialize(new Dictionary<string,uint[]> { ["game:groundstorage"] = new uint[30] }),6,out _);
        Require(!palette.HasGroundStorageColormap && snapshot.TryGroundColor(GroundStorageColors.Key(wood),0,0,0,out _), "Palette replacement mutated in-flight samples or hid legacy cache");
        var temporary = Directory.CreateTempSubdirectory("LauncherGo-groundstorage-colors-").FullName;
        try
        {
            var tile=Path.Combine(temporary,"tile.png"); File.WriteAllBytes(tile,[]);
            File.WriteAllText(tile+".colors","client-colors-4:"+snapshot.Version);
            Require(!TileColorStamp.IsCurrent(tile,snapshot.Version),"Old white storage tile was not invalidated");
            TileColorStamp.Complete(tile,snapshot.Version);
            Require(TileColorStamp.IsCurrent(tile,snapshot.Version),"New storage tile stamp rejected");
            CheckRenderedStorage(temporary, palette, wood, iron);
        }
        finally { Directory.Delete(temporary,true); }
        Console.WriteLine("PASS ground storage: native item/block sampling, RGBA, per-material pixels, mixed slots, offline pile IDs, empty storage, cache upgrade and snapshots");
        if (savePath != null) CheckSave(savePath);
    }

    // Verify the final PNG, including the persisted surface path. Testing only
    // TryGroundColor would miss a renderer falling back to groundstorage's
    // generic white block color or a surface cache losing its material key.
    static void CheckRenderedStorage(string root, MapPalette palette, SampleItem wood, SampleItem iron)
    {
        var white = new SampleItem { Code = new("game:legitimate-white-material"), Color = 0xFFFFFFFF };
        var samples = new Dictionary<string,uint[]> {
            ["game:groundstorage"] = Enumerable.Repeat(0xFFFFFFu,30).ToArray(),
            ["game:looseboulders-granite-free"] = Enumerable.Repeat(0xFFFFFFu,30).ToArray(),
            [GroundStorageColors.Prefix+"block/game:looseboulders-granite-free"] = Enumerable.Repeat(0x8B8678u,30).ToArray(),
            [GroundStorageColors.CompleteKey] = Enumerable.Repeat(1u,30).ToArray(),
            [GroundStorageColors.Key(wood)] = GroundStorageColors.SampleColors(null!,wood),
            [GroundStorageColors.Key(iron)] = GroundStorageColors.SampleColors(null!,iron),
            [GroundStorageColors.Key(white)] = GroundStorageColors.SampleColors(null!,white)
        };
        Require(palette.ApplyClientColormap(JsonSerializer.Serialize(samples),6,out _), "Render palette rejected");
        Require(palette.CaptureColors()!.Color(2,0,0,0)==((byte)139,(byte)134,(byte)120),"Cached loose-rock palette retained the player's white texture");
        var surface = new SurfaceRegion();
        var keys = new[] { GroundStorageColors.Key(wood), GroundStorageColors.Key(iron), GroundStorageColors.Prefix+"item/game:missing", "", GroundStorageColors.Key(white) };
        for(var x=0;x<keys.Length;x++)
        {
            var i=10*512+10+x; surface.Valid[i]=true; surface.Heights[i]=119;
            surface.Codes[i]="game:groundstorage"; surface.EntityKeys[i]=keys[x]; surface.SepiaKeys[i]="land";
        }
        var loosePixel=10*512+20; surface.Valid[loosePixel]=true; surface.Heights[loosePixel]=119;
        surface.Codes[loosePixel]="game:looseboulders-granite-free"; surface.SepiaKeys[loosePixel]="land";
        var cached = Path.Combine(root,"surface.br"); surface.Save(cached);
        surface=SurfaceRegion.Load(cached) ?? throw new Exception("Surface cache roundtrip failed");
        var renderer=new MapRenderer(null!,root,256,palette); var region=new ServerMap.World.ChunkKey(0,0,0);
        Require(renderer.RenderSurface(region,surface),"Storage PNG was not rendered");
        var decode=typeof(MapRenderer).Assembly.GetType("ServerMap.Render.PngEncoder")!.GetMethod("Decode")!;
        byte[] Read(string name)=>(byte[])decode.Invoke(null,[File.ReadAllBytes(Path.Combine(root,"2d",name,"0","0_0.png"))])!;
        var pixels=Read("basic");
        byte[] Pixel(int x)=>pixels.AsSpan((10*512+10+x)*4,4).ToArray();
        Require(Pixel(0).SequenceEqual(new byte[]{148,106,49,255}) && Pixel(1).SequenceEqual(new byte[]{67,73,81,255}), "Persisted material keys rendered as generic white instead of wood/iron");
        Require(Pixel(2)[3]==0 && Pixel(3)[3]==0, "Missing storage material rendered the generic white block texture");
        Require(Pixel(4).SequenceEqual(new byte[]{255,255,255,255}), "Legitimate white material was incorrectly filtered");
        Require(pixels.AsSpan(loosePixel*4,4).SequenceEqual(new byte[]{139,134,120,255}),"Loose boulder PNG used the old white sample instead of the cached material");
        Require(renderer.RenderSurface(region,surface,"sepia"),"Sepia storage PNG was not rendered");
        pixels=Read("sepia");
        Require(Pixel(0).SequenceEqual(new byte[]{172,136,88,255}),"Storage material colors changed the sepia layer");
        Console.WriteLine("PASS storage PNG: surface roundtrip, wood/iron material pixels, unresolved materials, legitimate white, cached loose-rock repair and sepia");
    }

    static IWorldAccessor World(Dictionary<int,Block> blocks, Dictionary<int,Item> items) => Proxy.Make<IWorldAccessor>((method,args) => method switch {
        "get_Side" => EnumAppSide.Server,
        "GetItem" => args[0] is int id ? items.GetValueOrDefault(id) : items.Values.FirstOrDefault(i => i.Code.Equals(args[0])),
        "GetBlock" => args[0] is int id ? blocks.GetValueOrDefault(id) : blocks.Values.FirstOrDefault(b => b.Code.Equals(args[0])),
        _ => null
    });
    static T Decode<T>(byte[] bytes) { using var stream = new MemoryStream(bytes,false); return Serializer.Deserialize<T>(stream); }
    static void CheckSave(string path)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(path), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open(); using var transaction = db.BeginTransaction(deferred:true); using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT data FROM gamedata LIMIT 1";
        var save = Decode<Save>((byte[])command.ExecuteScalar()!);
        var blocks = Decode<Dictionary<int,string>>(save.ModData["BlockIDs"]).ToDictionary(p=>p.Key,p=>new Block { BlockId=p.Key, Code=new(p.Value) });
        var items = Decode<Dictionary<int,string>>(save.ModData["ItemIDs"]).ToDictionary(p=>p.Key,p=>new Item { ItemId=p.Key, Code=new(p.Value) });
        var world = World(blocks,items); var colors = new GroundStorageColors(world);
        var types = new[] { "GroundStorage", "IngotPile", "PlatePile", "PlankPile", "CoalPile", "FirewoodPile", "PeatPile" }
            .ToDictionary(name => name, name => typeof(BlockEntityGroundStorage).Assembly.GetType("Vintagestory.GameContent.BlockEntity" + name, true)!);
        command.CommandText = "SELECT data FROM chunk"; using var rows = command.ExecuteReader(); var counts = new Dictionary<string,int>();
        while(rows.Read()) foreach(var bytes in Decode<Chunk>((byte[])rows[0]).Entities)
        {
            using var stream=new MemoryStream(bytes,false); using var reader=new BinaryReader(stream);
            if(!types.TryGetValue(reader.ReadString(),out var type)) continue;
            var tree=new TreeAttribute(); tree.FromBytes(reader);
            var entity=(BlockEntity)Activator.CreateInstance(type)!;
            entity.CreateBehaviors(new Block(),world); entity.FromTreeAttributes(tree,world);
            var key=colors.Resolve(entity,entity.Pos.X,entity.Pos.Y,entity.Pos.Z);
            Require(key != null,$"Unresolved saved pile at {entity.Pos}");
            counts[key!]=counts.GetValueOrDefault(key!)+1;
        }
        Require(counts.Count>0,"No saved storage was checked");
        foreach(var pair in counts) Console.WriteLine($"  {pair.Value} saved piles -> {pair.Key}");
        Console.WriteLine($"PASS read-only save: {counts.Values.Sum()} real deserialized ground storage/piles resolve to collectible keys (client texture upload still required)");
    }
    [ProtoContract] private sealed class Save { [ProtoMember(11)] public Dictionary<string,byte[]> ModData {get;set;}=[]; }
    [ProtoContract] private sealed class Chunk { [ProtoMember(8)] public List<byte[]> Entities {get;set;}=[]; }
}
sealed class SampleItem : Item
{
    public uint Color; public int Calls;
    public override int GetRandomColor(ICoreClientAPI api,ItemStack stack) { Calls++; return unchecked((int)Color); }
}
sealed class SampleBlock : Block
{
    public uint Color;
    public override int GetRandomColor(ICoreClientAPI api,ItemStack stack) => unchecked((int)Color);
}
public class Proxy : DispatchProxy
{
    private System.Func<string,object?[],object?> handler=null!;
    public static T Make<T>(System.Func<string,object?[],object?> handler) where T:class { var proxy=Create<T,Proxy>(); ((Proxy)(object)proxy).handler=handler; return proxy; }
    protected override object? Invoke(MethodInfo? method,object?[]? args) => handler(method!.Name,args??[]) ?? (method.ReturnType!=typeof(void)&&method.ReturnType.IsValueType?Activator.CreateInstance(method.ReturnType):null);
}
