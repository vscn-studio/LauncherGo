// Block/color selection adapted from VS-LiveMap-Revival (MIT).
// Copyright (c) 2024 William Blake Galbreath. See VS-LiveMap-Revival-LICENSE.txt.
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

    public sealed record Entry(int Id, string Code, string MapColorCode, bool IsWater, bool IsIce, bool IsLava, bool IsOverlay, bool IsSurfaceCover, bool IsMicroBlock, bool IsPlaceholder, bool IsEmpty)
    {
        public bool IsRoof { get; init; }
        public bool IsGroundStorage { get; init; }
    }

    private readonly Entry?[] entries;
    private Block? roofingBlock;
    internal RoofingColors? Roofing { get; private set; }
    internal GroundStorageColors? GroundStorage { get; private set; }
    public sealed record ColorSnapshot(int Month, string Version, uint[][] Colors)
    {
        public IReadOnlyDictionary<string, uint[]> RoofColors { get; init; } = new Dictionary<string, uint[]>();
        public IReadOnlyDictionary<string, uint[]> GroundColors { get; init; } = new Dictionary<string, uint[]>();
        public bool TryGroundColor(string? key, int x, int y, int z, out (byte R, byte G, byte B) color)
        {
            color = default;
            if (key == null || !GroundColors.TryGetValue(key, out var values)) return false;
            var value = values[GameMath.MurmurHash3Mod(x, y, z, values.Length)];
            color = ((byte)(value >> 16), (byte)(value >> 8), (byte)value);
            return true;
        }
        public bool TryRoofColor(string? key, int x, int y, int z, out (byte R, byte G, byte B) color)
        {
            color = default;
            if (key == null || !RoofColors.TryGetValue(key, out var values)) return false;
            var value = values[GameMath.MurmurHash3Mod(x, y, z, values.Length)];
            color = ((byte)(value >> 16), (byte)(value >> 8), (byte)value);
            return true;
        }
        public bool HasColor(int id) => (uint)id < (uint)Colors.Length && Colors[id] is { Length: > 0 };
        public (byte R, byte G, byte B) Color(int id, int x, int y, int z)
        {
            if (!HasColor(id)) return (0, 0, 0);
            var values = Colors[id];
            var value = values[GameMath.MurmurHash3Mod(x, y, z, values.Length)];
            return ((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        }
    }
    private ColorSnapshot? clientColors;

    private readonly Dictionary<string, int> idsByCode;
    private MapPalette(Entry?[] entries) { this.entries = entries; idsByCode = entries.Where(e => e != null).ToDictionary(e => e!.Code, e => e!.Id, StringComparer.Ordinal); }
    public int ResolveCode(string code) => idsByCode.GetValueOrDefault(code, MissingBlockId);

    public int BlockCount => entries.Length;
    public bool HasClientColormap => Volatile.Read(ref clientColors) != null;
    public bool HasRoofingColormap => (roofingBlock == null && Roofing == null) || CaptureColors()?.RoofColors.Keys.Any(key => key.StartsWith(RoofingColors.Prefix + "roof/", StringComparison.Ordinal)) == true;
    public bool HasGroundStorageColormap => GroundStorage == null || CaptureColors()?.GroundColors.ContainsKey(GroundStorageColors.CompleteKey) == true;
    public int ClientColormapMonth => CaptureColors()?.Month ?? 0;
    public string ClientColormapVersion => CaptureColors()?.Version ?? "fallback";
    public ColorSnapshot? CaptureColors() => Volatile.Read(ref clientColors);

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
            result[block.Id] = new Entry(block.Id, code, color, water, ice, lava, overlay, cover, micro, placeholder, block.Id == 0) { IsRoof = RoofingColors.IsRoof(block), IsGroundStorage = GroundStorageColors.IsStorage(block) };
        }
        result[0] ??= new Entry(0, "game:air", "land", false, false, false, false, false, false, false, true);
        api.Logger.Notification("ServerMap 2D palette captured {0} block definitions.", result.Count(entry => entry != null));
        var palette = new MapPalette(result);
        palette.roofingBlock = blocks.FirstOrDefault(block => block != null && RoofingColors.IsRoof(block));
        if (result.Any(entry => entry?.IsGroundStorage == true)) palette.GroundStorage = new GroundStorageColors(api.World);
        return palette;
    }

    // StartServerSide runs before Block.OnLoaded fills VS Roofing's static
    // definitions. Capture at GameReady, before loading colors/queueing tiles,
    // otherwise an empty dictionary is retained for the entire server session.
    internal void InitializeRoofing()
    {
        if (roofingBlock != null && Roofing == null) Roofing = new RoofingColors(roofingBlock);
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
        var roofColors = new Dictionary<string, uint[]>(StringComparer.Ordinal);
        var groundColors = new Dictionary<string, uint[]>(StringComparer.Ordinal);
        foreach (var (code, colors) in source)
        {
            if (colors is not { Length: 30 }) continue;
            if (code.StartsWith(GroundStorageColors.Prefix, StringComparison.Ordinal))
            {
                groundColors[code] = colors.Select(value => value & 0xFFFFFF).ToArray();
                continue;
            }
            if (code.StartsWith(RoofingColors.Prefix, StringComparison.Ordinal))
            {
                roofColors[code] = colors.Select(value => value & 0xFFFFFF).ToArray();
                continue;
            }
            if (!byCode.TryGetValue(code, out var id)) continue;
            next[id] = colors.Select(value => value & 0xFFFFFF).ToArray();
            resolvedCount++;
        }
        if (resolvedCount == 0) return false;
        var effective = new SortedDictionary<string, uint[]>(StringComparer.Ordinal);
        foreach (var entry in entries) if (entry != null && next[entry.Id] != null) effective[entry.Code] = next[entry.Id];
        foreach (var pair in roofColors.Concat(groundColors)) effective[pair.Key] = pair.Value;
        var version = month + ":" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(effective)));
        Volatile.Write(ref clientColors, new ColorSnapshot(month, version, next) { RoofColors = roofColors, GroundColors = groundColors });
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

    public bool HasMapColor(int id) => CaptureColors()?.HasColor(id) == true;
    public (byte R, byte G, byte B) MapColor(int id, int x, int y, int z)
    {
        return CaptureColors()?.Color(id, x, y, z) ?? (0, 0, 0);
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
    public static (byte R, byte G, byte B) ColorFor(string value) => value.ToLowerInvariant() switch
    {
        "ink" or "wateredge" => (72, 48, 24), "settlement" => (133, 104, 68), "land" => (172, 136, 88),
        "desert" => (196, 164, 104), "forest" => (152, 132, 76), "road" => (128, 80, 48),
        "plant" => (128, 134, 80), "lake" or "ocean" => (204, 200, 144), "glacier" => (224, 224, 192),
        "lava" => (224, 83, 25), _ => (172, 136, 88)
    };
}
