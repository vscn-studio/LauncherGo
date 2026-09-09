using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ProtoBuf;
using ServerMap.Render;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

// Read-only integration check: real saved trees and the mod's own
// FromTreeAttributes, with collectible IDs resolved from this save's registry.
internal static class SavedRoofChecks
{
    [ProtoContract] private sealed class Save
    {
        [ProtoMember(11)] public Dictionary<string, byte[]> ModData { get; set; } = [];
    }
    [ProtoContract] private sealed class Chunk
    {
        [ProtoMember(8)] public List<byte[]> BlockEntities { get; set; } = [];
    }
    private static T Decode<T>(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        return Serializer.Deserialize<T>(stream);
    }

    internal static void Run(string roofingRoot, string savePath, string colormapPath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = Path.GetFullPath(savePath), Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT data FROM gamedata LIMIT 1";
        var save = Decode<Save>((byte[])command.ExecuteScalar()!);
        var blocks = Decode<Dictionary<int, string>>(save.ModData["BlockIDs"])
            .ToDictionary(p => p.Key, p => new Block { BlockId = p.Key, Code = new AssetLocation(p.Value) });
        var items = Decode<Dictionary<int, string>>(save.ModData["ItemIDs"])
            .ToDictionary(p => p.Key, p => new Item { ItemId = p.Key, Code = new AssetLocation(p.Value) });
        var assembly = Assembly.LoadFrom(Path.Combine(roofingRoot, "vsroofing.dll"));
        var roofType = assembly.GetType("VSRoofing.RoofBlock", true)!;
        var roofBlock = (Block)Activator.CreateInstance(roofType)!;
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(roofingRoot, "assets/vsroofing/blocktypes/roof.json")),
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        roofBlock.Attributes = JsonObject.FromJson(json.RootElement.GetProperty("attributes").GetRawText());
        foreach (var name in new[] { "LoadRoofs", "LoadFrames" })
            roofType.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(roofBlock, null);
        var adapter = new RoofingColors(roofBlock);
        var colors = JsonSerializer.Deserialize<Dictionary<string, uint[]>>(File.ReadAllText(colormapPath))!;
        var blockList = Enumerable.Range(0, blocks.Keys.Max() + 1).Select(id => blocks.GetValueOrDefault(id)!).ToList();
        var world = FixtureProxy.Make<IWorldAccessor>((method, args) => method switch {
            "get_Side" => EnumAppSide.Server,
            "get_Blocks" => blockList,
            "GetBlock" => args[0] is int blockId ? blocks.GetValueOrDefault(blockId)
                : blocks.Values.FirstOrDefault(b => b.Code.Equals(args[0])),
            "GetItem" => args[0] is int itemId ? items.GetValueOrDefault(itemId)
                : items.Values.FirstOrDefault(i => i.Code.Equals(args[0])),
            _ => null
        });
        command.CommandText = "SELECT data FROM chunk";
        using var rows = command.ExecuteReader();
        var counts = new Dictionary<string, int>();
        var total = 0;
        while (rows.Read()) foreach (var bytes in Decode<Chunk>((byte[])rows[0]).BlockEntities)
        {
            using var stream = new MemoryStream(bytes, false);
            using var reader = new BinaryReader(stream);
            var className = reader.ReadString();
            if (!className.Contains("AutoRoof", StringComparison.Ordinal)) continue;
            var tree = new TreeAttribute();
            tree.FromBytes(reader);
            var entity = (BlockEntity)Activator.CreateInstance(assembly.GetType("VSRoofing.AutoRoofEntity", true)!)!;
            entity.CreateBehaviors(roofBlock, world);
            entity.FromTreeAttributes(tree, world);
            var key = adapter.Resolve(entity, out var infill);
            if (key == null && infill > 0) key = blocks[infill].Code.ToString();
            if (key == null || !colors.TryGetValue(key, out var values) || values.Length != 30 || values.All(v => v == 0))
                throw new Exception($"Saved roof at {entity.Pos}: no color for {key ?? "<unresolved>"}");
            counts[key] = counts.GetValueOrDefault(key) + 1;
            total++;
        }
        if (total == 0) throw new Exception("No saved VS Roofing entities found; test did not exercise the reported world");
        foreach (var pair in counts) Console.WriteLine($"  {pair.Value} saved roofs -> {pair.Key}");
        Console.WriteLine($"PASS read-only save: {total}/{total} real deserialized roofs match nonblack client colors");
    }
}
