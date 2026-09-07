using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using ServerMap.Util;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ServerMap.Render;

/// <summary>Minimal block metadata required by the 2D renderer. It deliberately
/// avoids texture IO and shape parsing so server startup is independent of 3D assets.</summary>
public sealed class MapPalette
{
    public const int MissingBlockId = -1;

    public sealed record Entry(int Id, string Code, string MapColorCode, bool IsWater, bool IsIce, bool IsLava, bool IsOverlay, bool IsSurfaceCover, bool IsMicroBlock, bool IsPlaceholder, bool IsEmpty);

    private readonly Entry?[] entries;
    private uint[][]? clientColors;
    private int clientColormapMonth;
    private string clientColormapVersion = "fallback";

    private MapPalette(Entry?[] entries) => this.entries = entries;

    public int BlockCount => entries.Length;
    public bool HasClientColormap => Volatile.Read(ref clientColors) != null;
    public int ClientColormapMonth => Volatile.Read(ref clientColormapMonth);
    public string ClientColormapVersion => Volatile.Read(ref clientColormapVersion);

    public static MapPalette Capture(ICoreAPI api)
    {
        var blocks = api.World.Blocks;
        var result = new Entry?[Math.Max(1, blocks.Where(block => block != null).Select(block => block.Id).DefaultIfEmpty().Max() + 1)];
        foreach (var block in blocks.Where(block => block?.Code != null))
        {
            var path = block.Code.Path;
            var code = block.Code.ToString();
            var color = block.Attributes?["mapColorCode"].AsString();
            if (string.IsNullOrWhiteSpace(color)) color = DefaultColor(block.BlockMaterial);
            var micro = path.StartsWith("chiseledblock", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("microblock", StringComparison.OrdinalIgnoreCase);
            var ice = block.BlockMaterial == EnumBlockMaterial.Ice;
            var lava = block.BlockMaterial == EnumBlockMaterial.Lava || path.StartsWith("lava-", StringComparison.OrdinalIgnoreCase) || color.Equals("lava", StringComparison.OrdinalIgnoreCase);
            var overlay = (path.EndsWith("-snow", StringComparison.OrdinalIgnoreCase) && !micro)
                || path.EndsWith("-snow2", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("-snow3", StringComparison.OrdinalIgnoreCase)
                || path.Equals("snowblock", StringComparison.OrdinalIgnoreCase)
                || path.Contains("snowlayer-", StringComparison.OrdinalIgnoreCase);
            // Match LiveMap: every liquid is water-like for shoreline testing,
            // while glacier ice remains land and ordinary ice remains water.
            var water = block.BlockMaterial == EnumBlockMaterial.Water
                || (ice && !path.Equals("glacierice", StringComparison.OrdinalIgnoreCase));
            // LiveMap only substitutes the block under snow overlays. Plants
            // remain the visible top block; lowering them can turn shallow
            // water columns into soil and creates false shorelines.
            var cover = overlay;
            var placeholder = block.BlockMaterial == EnumBlockMaterial.Meta
                || path.StartsWith("multiblock-", StringComparison.OrdinalIgnoreCase)
                || path.Equals("clutter", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("clutter-", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("rocktyped-rubble", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("banner-", StringComparison.OrdinalIgnoreCase);
            result[block.Id] = new Entry(block.Id, code, color, water, ice, lava, overlay, cover, micro, placeholder, block.Id == 0);
        }
        result[0] ??= new Entry(0, "game:air", "land", false, false, false, false, false, false, false, true);
        api.Logger.Notification("ServerMap 2D palette captured {0} block definitions.", result.Count(entry => entry != null));
        return new MapPalette(result);
    }

    public Entry Get(int id) => (uint)id < (uint)entries.Length && entries[id] != null
        ? entries[id]! : new Entry(MissingBlockId, "servermap:missing-material", "ink", false, false, false, false, false, false, false, false);
    public bool IsMapWaterBlock(int id) => Get(id).IsWater;
    public bool IsMapOverlay(int id) => Get(id).IsOverlay;
    public bool IsSurfaceCover(int id) => Get(id).IsSurfaceCover;
    public bool IsIce(int id) => Get(id).IsIce;
    public bool IsLava(int id) => Get(id).IsLava;
    public bool IsMicroBlock(int id) => Get(id).IsMicroBlock;
    public bool IsPlaceholder(int id) => Get(id).IsPlaceholder;
    public bool IsEmpty(int id) => Get(id).IsEmpty;

    public bool ApplyClientColormap(string json, int month, out int resolvedCount)
    {
        resolvedCount = 0;
        if (month is < 1 or > 12 || string.IsNullOrWhiteSpace(json)) return false;
        Dictionary<string, uint[]>? source;
        try { source = JsonSerializer.Deserialize<Dictionary<string, uint[]>>(json); }
        catch { return false; }
        if (source == null) return false;
        var byCode = entries.Where(entry => entry != null).ToDictionary(entry => entry!.Code, entry => entry!.Id, StringComparer.OrdinalIgnoreCase);
        var next = new uint[entries.Length][];
        foreach (var (code, colors) in source)
        {
            if (!byCode.TryGetValue(code, out var id) || colors is not { Length: 30 }) continue;
            next[id] = colors.Select(value => value & 0xFFFFFF).ToArray();
            resolvedCount++;
        }
        if (resolvedCount == 0) return false;
        Volatile.Write(ref clientColormapMonth, month);
        Volatile.Write(ref clientColormapVersion, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).Substring(0, 16));
        Volatile.Write(ref clientColors, next);
        return true;
    }

    public bool LoadClientColormap(string directory, int month, Action<string> log)
    {
        var exactPath = Path.Combine(directory, $"colormap-{month}.json");
        if (!File.Exists(exactPath)) return false;
        try
        {
            if (!ApplyClientColormap(File.ReadAllText(exactPath), month, out var count)) return false;
            log($"ServerMap restored cached client colormap: month {month}, {count} blocks.");
            return true;
        }
        catch (Exception ex) { log($"ServerMap skipped saved colormap {exactPath}: {ex.Message}"); return false; }
    }

    public static void SaveClientColormap(string directory, string json, int month)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"colormap-{month}.json");
        AtomicFile.Replace(path, temp => File.WriteAllText(temp, json));
    }

    public bool HasMapColor(int id) => Volatile.Read(ref clientColors) is { } colors && (uint)id < (uint)colors.Length && colors[id] is { Length: > 0 };
    public (byte R, byte G, byte B) MapColor(int id, int x, int y, int z)
    {
        var colors = Volatile.Read(ref clientColors);
        if (colors == null || (uint)id >= (uint)colors.Length || colors[id] is not { Length: > 0 } values) return (0, 0, 0);
        var value = values[GameMath.MurmurHash3Mod(x, y, z, values.Length)];
        return ((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }
    public (byte R, byte G, byte B) SepiaColor(int id) => ColorFor(Get(id).MapColorCode);
    public (byte R, byte G, byte B) BasicFallbackColor(int id)
    {
        var entry = Get(id);
        if (entry.IsLava) return (224, 83, 25);
        if (entry.IsIce) return (178, 213, 226);
        if (entry.IsWater) return (67, 139, 184);
        return ColorFor(entry.MapColorCode);
    }

    private static string DefaultColor(EnumBlockMaterial material) => material.ToString().ToLowerInvariant() switch
    {
        var value when value.Contains("liquid") || value.Contains("water") => "lake",
        var value when value.Contains("lava") => "lava",
        var value when value.Contains("ice") => "glacier",
        var value when value.Contains("plant") || value.Contains("leaves") => "plant",
        var value when value.Contains("wood") => "forest",
        _ => "land"
    };
    private static (byte R, byte G, byte B) ColorFor(string value) => value.ToLowerInvariant() switch
    {
        "ink" or "wateredge" => (72, 48, 24), "settlement" => (133, 104, 68), "land" => (172, 136, 88),
        "desert" => (196, 164, 104), "forest" => (152, 132, 76), "road" => (128, 80, 48),
        "plant" => (128, 134, 80), "lake" or "ocean" => (204, 200, 144), "glacier" => (224, 224, 192),
        "lava" => (224, 83, 25), _ => (172, 136, 88)
    };
}
