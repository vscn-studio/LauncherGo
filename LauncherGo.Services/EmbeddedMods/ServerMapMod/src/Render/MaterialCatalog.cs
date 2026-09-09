using System.Security.Cryptography;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Reflection;
using ServerMap.Util;
using SkiaSharp;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ServerMap.Render;

public enum BlockGeometryKind
{
    Empty,
    Cube,
    Boxes,
    Cross,
    Fluid,
    Shape
}

public enum MeshMaterialLayer : byte
{
    Opaque,
    Cutout,
    Translucent,
    Liquid
}

public readonly record struct MaterialBox(float X1, float Y1, float Z1, float X2, float Y2, float Z2);

public sealed class ShapeFaceTemplate
{
    public required float[] Vertices { get; init; }
    // The native JSON tesselator keeps the transformed face normal even when
    // the face is no longer on an axis-aligned 0/1 boundary (for example a
    // sign or a composite rotated element).  MeshTile uses it to map the face
    // to the nearest BlockFacing for culling and lighting.
    public required float[] Normal { get; init; }
    public required float[] Uvs { get; init; }
    public required int Tile { get; init; }
    // Preserve the shape face binding.  Slanted roofing deliberately uses
    // three different bindings (plank, shingle side and shingle top) even
    // though its voxel material exposes one six-face fallback array.
    public required string TextureKey { get; init; }
    public required int BoundaryFace { get; init; }
    // ShapeElement.RenderPass is per element (not per block).  Lamps and
    // clutter commonly mix opaque supports with transparent glass in one
    // shape, so the pass must survive the bake to MeshTile.
    public required short RenderPass { get; init; }
}

public sealed class ShapeTemplate
{
    public required ShapeFaceTemplate[] Faces { get; init; }
    // The vanilla door/trapdoor renderer applies the "opened" animation to
    // the root element at runtime. Keep its final pose alongside the baked
    // faces so server-side meshes can reproduce that state from the BE.
    public float OpenedRotateX { get; init; }
    public float OpenedRotateY { get; init; }
    public float OpenedRotateZ { get; init; }
    public float OpenedOriginX { get; init; }
    public float OpenedOriginY { get; init; }
    public float OpenedOriginZ { get; init; }
}

public sealed class DynamicShapeMaterial
{
    public required ShapeTemplate[] Shapes { get; init; }
    public Dictionary<string, ShapeTemplate[]> OverrideVariants { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public float TypeRotationX { get; init; }
    public float TypeRotationY { get; init; }
    public float TypeRotationZ { get; init; }
    public bool RandomizeYSize { get; init; }

    public bool TryGetShapes(string? overrideCode, out ShapeTemplate[] shapes)
    {
        if (string.IsNullOrWhiteSpace(overrideCode))
        {
            shapes = Shapes;
            return Shapes.Any(shape => shape.Faces.Length > 0);
        }

        // An unknown override must stay missing. Falling back to the default
        // variant would silently render the wrong banner/sign material.
        return OverrideVariants.TryGetValue(overrideCode, out shapes!) && shapes.Any(shape => shape.Faces.Length > 0);
    }
}

public sealed class BlockMaterialInfo
{
    private static readonly float[] RandomRotations = [-22.5f, 22.5f, 67.5f, 112.5f, 157.5f, 202.5f, 247.5f, 292.5f];

    public required int Id { get; init; }
    public required string Code { get; init; }
    public required int[] FaceTiles { get; init; }
    public required int[] InsideFaceTiles { get; init; }
    public required int[] TopSoilOverlayTiles { get; init; }
    public required int[] RotationIds { get; init; }
    public required bool[] OpaqueFaces { get; init; }
    public required bool[] SolidFaces { get; init; }
    public required bool[] SideAo { get; init; }
    public required byte EmitSideAo { get; init; }
    public required int LightAbsorption { get; init; }
    public required bool ForFluidsLayer { get; init; }
    public required EnumFaceCullMode FaceCullMode { get; init; }
    public required EnumBlockMaterial BlockMaterial { get; init; }
    // BlockEntityMicroBlock.VoxelMaterial keeps the native render pass and
    // cull-between-transparents bit.  They are independent from the normal
    // block face-culling policy and are required by isMergableMaterial().
    public required EnumChunkRenderPass RenderPass { get; init; }
    public required bool CullBetweenTransparents { get; init; }
    public required BlockGeometryKind Geometry { get; init; }
    public required MeshMaterialLayer Layer { get; init; }
    public required MaterialBox[] Boxes { get; init; }
    public required ShapeTemplate[] Shapes { get; init; }
    public required bool RandomizeAxesXYZ { get; init; }
    public required bool RandomizeRotations { get; init; }
    public required string LiquidCode { get; init; }
    public required string MapColorCode { get; init; }
    public required bool IsMapWater { get; init; }
    public required bool IsMicroBlock { get; init; }
    public required bool IsDynamicShape { get; init; }
    // Client BlockEntityMicroBlock.loadDecor places these blocks as a real
    // mesh translated away from the microblock face, rather than as a decal.
    public required bool AttachAs3d { get; init; }
    public required byte R { get; init; }
    public required byte G { get; init; }
    public required byte B { get; init; }
    public required byte A { get; init; }
    public required int LiquidLevel { get; init; }
    // Retain the resolved registry block for runtime-only contracts such as
    // 1.22.x BlockMultiblock control offsets. This is not serialized.
    public Block? SourceBlock { get; init; }

    public ShapeTemplate? Shape => Shapes.FirstOrDefault();
    public bool HasTopSoilOverlay => TopSoilOverlayTiles.Any(tile => tile >= 0);

    public ShapeTemplate? SelectShape(int x, int y, int z)
    {
        if (Shapes.Length == 0) return null;
        if (Shapes.Length == 1) return Shapes[0];
        return Shapes[GameMath.MurmurHash3Mod(x, RandomizeAxesXYZ ? y : 0, z, Shapes.Length)];
    }

    public float SelectRandomRotation(int x, int y, int z)
    {
        if (!RandomizeRotations) return 0;
        return RandomRotations[GameMath.MurmurHash3Mod(x, RandomizeAxesXYZ ? y : 0, z, RandomRotations.Length)];
    }

    public float SelectShapeRotation(int x, int y, int z)
    {
        if (!RandomizeRotations) return 0;
        // Match the 1.22.3 JsonTesselator.  Older engine versions negated X
        // here, but doing so now selects a different orientation from the
        // client for every randomly rotated JSON block.
        return RandomRotations[GameMath.MurmurHash3Mod(x, RandomizeAxesXYZ ? y : 0, z, RandomRotations.Length)];
    }
}

public sealed class MaterialCatalog
{
    private sealed record TextureAssetSource(IAsset? Asset, string? FilePath);

    // Keep enough source detail for foliage and grass coverage.  A 32px bake
    // visibly destroyed the detail of the 64px/128px vanilla textures when
    // the camera was close to the terrain.  Mipmap sampling uses a padded
    // atlas slot below, so this detail stays isolated from neighbouring tiles.
    public const int CellSize = 64;
    // Mipmap levels sample outside the texel addressed by the base UV.  Keep
    // a replicated border around every atlas cell so distant blocks never
    // blend with the next material in the atlas.
    private const int AtlasGutter = 4;
    private const string AtlasLayoutRevision = "mip-gutter-v1";
    public const int MissingBlockId = -1;
    private static readonly string[] FaceTextureKeys = ["west", "east", "down", "up", "north", "south"];
    private static readonly int[] FaceToEngineSide = [3, 1, 5, 4, 0, 2];

    private readonly BlockMaterialInfo[] blocks;
    private readonly List<byte[]> cells;
    // Filled atomically when a privileged client uploads the real LiveMap
    // colormap.  Until then 2D rendering is deliberately disabled instead of
    // silently falling back to a server-side texture approximation.
    private uint[][]? clientColors;
    private int clientColormapMonth;

    private readonly Dictionary<string, DynamicShapeMaterial> dynamicShapes;

    private MaterialCatalog(BlockMaterialInfo[] blocks, List<byte[]> cells, int missingTile, int resolvedTextures, int fallbackTextures, int shapeBlocks, int shapeFaces, int missingShapes,
        Dictionary<string, DynamicShapeMaterial>? dynamicShapes = null)
    {
        this.blocks = blocks;
        this.cells = cells;
        this.dynamicShapes = dynamicShapes ?? new Dictionary<string, DynamicShapeMaterial>(StringComparer.OrdinalIgnoreCase);
        ResolvedTextures = resolvedTextures;
        FallbackTextures = fallbackTextures;
        ShapeBlocks = shapeBlocks;
        ShapeFaces = shapeFaces;
        MissingShapes = missingShapes;
        AtlasSlotSize = CellSize + AtlasGutter * 2;
        AtlasColumns = NextPowerOfTwo((int)Math.Ceiling(Math.Sqrt(cells.Count)));
        AtlasRows = (int)Math.Ceiling(cells.Count / (double)AtlasColumns);
        AtlasWidth = AtlasColumns * AtlasSlotSize;
        AtlasHeight = NextPowerOfTwo(Math.Max(1, AtlasRows * AtlasSlotSize));
        MissingMaterial = new BlockMaterialInfo
        {
            Id = MissingBlockId,
            Code = "servermap:missing-material",
            FaceTiles = [missingTile, missingTile, missingTile, missingTile, missingTile, missingTile],
            InsideFaceTiles = [missingTile, missingTile, missingTile, missingTile, missingTile, missingTile],
            TopSoilOverlayTiles = [-1, -1, -1, -1, -1, -1],
            RotationIds = [MissingBlockId, MissingBlockId, MissingBlockId, MissingBlockId],
            OpaqueFaces = [true, true, true, true, true, true],
            SolidFaces = [true, true, true, true, true, true],
            SideAo = [false, false, false, false, false, false],
            EmitSideAo = 0,
            LightAbsorption = 0,
            ForFluidsLayer = false,
            FaceCullMode = EnumFaceCullMode.NeverCull,
            BlockMaterial = EnumBlockMaterial.Stone,
            RenderPass = EnumChunkRenderPass.Opaque,
            CullBetweenTransparents = false,
            Geometry = BlockGeometryKind.Cube,
            Layer = MeshMaterialLayer.Opaque,
            Boxes = [],
            Shapes = [],
            RandomizeAxesXYZ = false,
            RandomizeRotations = false,
            LiquidCode = "",
            MapColorCode = "land",
            IsMapWater = false,
            IsMicroBlock = false,
            IsDynamicShape = false,
            AttachAs3d = false,
            R = 0,
            G = 0,
            B = 0,
            A = 255,
            LiquidLevel = 0
        };
        Fingerprint = ComputeFingerprint();
    }

    public string Fingerprint { get; }
    public int AtlasSlotSize { get; }
    public int AtlasColumns { get; }
    public int AtlasRows { get; }
    public int AtlasWidth { get; }
    public int AtlasHeight { get; }
    public int ResolvedTextures { get; }
    public int FallbackTextures { get; }
    public int ShapeBlocks { get; }
    public int ShapeFaces { get; }
    public int MissingShapes { get; }
    public int BlockCount => blocks.Length;
    public BlockMaterialInfo MissingMaterial { get; }
    public bool HasClientColormap => Volatile.Read(ref clientColors) != null;
    public int ClientColormapMonth => Volatile.Read(ref clientColormapMonth);

    public BlockMaterialInfo Get(int blockId)
    {
        if ((uint)blockId < (uint)blocks.Length && blocks[blockId] != null) return blocks[blockId];
        // Unknown registry ids are deliberately black in both renderers. Do
        // not turn a missing mod/client asset into air or a real terrain tile.
        return MissingMaterial;
    }

    /// <summary>
    /// Find a captured material by its fully resolved registry code.  This is
    /// needed for old schematic microblocks whose entity only retained a
    /// display name and the temporary selectable-collider id.
    /// </summary>
    public BlockMaterialInfo? FindByCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        foreach (var block in blocks)
        {
            if (block != null && block.Code.Equals(code, StringComparison.OrdinalIgnoreCase))
                return block;
        }
        return null;
    }

    public DynamicShapeMaterial? GetDynamicShape(int blockId, string? type, string? overrideCode = null)
    {
        if (blockId <= 0 || string.IsNullOrWhiteSpace(type)) return null;
        if (!dynamicShapes.TryGetValue(DynamicShapeKey(blockId, type), out var material)
            || !material.TryGetShapes(overrideCode, out _)) return null;
        return material;
    }

    private static string DynamicShapeKey(int blockId, string type) => $"{blockId}:{type}";

    public BlockMaterialInfo Rotate(BlockMaterialInfo info, int degrees)
    {
        var quarterTurns = degrees / 90;
        if (degrees % 90 != 0 || quarterTurns == 0 || info.Id < 0) return info;
        quarterTurns = ((quarterTurns % 4) + 4) % 4;
        var rotatedId = info.RotationIds.Length > quarterTurns ? info.RotationIds[quarterTurns] : info.Id;
        return rotatedId == info.Id ? info : Get(rotatedId);
    }

    public (byte R, byte G, byte B) MapColor(int blockId, int x, int y, int z)
    {
        var colors = Volatile.Read(ref clientColors);
        if (colors == null || (uint)blockId >= (uint)colors.Length || colors[blockId] is not { Length: > 0 } values)
            return (0, 0, 0);

        var color = values[GameMath.MurmurHash3Mod(x, y, z, values.Length)] & 0xFFFFFF;
        return ((byte)((color >> 16) & 0xff), (byte)((color >> 8) & 0xff), (byte)(color & 0xff));
    }

    public bool HasMapColor(int blockId)
    {
        var colors = Volatile.Read(ref clientColors);
        return colors != null && (uint)blockId < (uint)colors.Length && colors[blockId] is { Length: > 0 };
    }

    /// <summary>Return LiveMap's stable classification colour for a block.</summary>
    public (byte R, byte G, byte B) SepiaColor(int blockId)
    {
        return infoColor(Get(blockId).MapColorCode);

        static (byte R, byte G, byte B) infoColor(string colorCode) => colorCode.ToLowerInvariant() switch
        {
            "ink" or "wateredge" => (72, 48, 24),
            "settlement" => (133, 104, 68),
            "land" => (172, 136, 88),
            "desert" => (196, 164, 104),
            "forest" => (152, 132, 76),
            "road" => (128, 80, 48),
            "plant" => (128, 134, 80),
            "lake" or "ocean" => (204, 200, 144),
            "glacier" => (224, 224, 192),
            "lava" => (224, 83, 25),
            _ => (172, 136, 88)
        };
    }

    public bool IsMapWaterBlock(int blockId) => Get(blockId).IsMapWater;

    public bool IsMetaBlockLayer(int blockId)
    {
        return IsMetaBlockLayer(Get(blockId).Code);
    }

    private static bool IsMetaBlockLayer(Block block)
    {
        return IsMetaBlockLayer(block.Code.ToString());
    }

    private static bool IsMetaBlockLayer(string code)
    {
        var path = code;
        var separator = path.IndexOf(':');
        if (separator >= 0) path = path[(separator + 1)..];
        return path.Equals("meta-blocklayer", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Apply a complete, client-generated LiveMap colormap.</summary>
    public bool ApplyClientColormap(string json, int month, out int resolvedCount)
    {
        resolvedCount = 0;
        if (month is < 1 or > 12 || string.IsNullOrWhiteSpace(json)) return false;

        Dictionary<string, uint[]>? source;
        try
        {
            source = JsonSerializer.Deserialize<Dictionary<string, uint[]>>(json);
        }
        catch
        {
            return false;
        }

        if (source == null || source.Count == 0) return false;
        var byCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in blocks)
        {
            if (block != null && !string.IsNullOrWhiteSpace(block.Code)) byCode.TryAdd(block.Code, block.Id);
        }
        var next = new uint[blocks.Length][];
        foreach (var entry in source)
        {
            if (!byCode.TryGetValue(entry.Key, out var id) || entry.Value is not { Length: 30 }) continue;
            var values = new uint[30];
            for (var i = 0; i < values.Length; i++) values[i] = entry.Value[i] & 0xFFFFFF;
            next[id] = values;
            resolvedCount++;
        }

        if (resolvedCount == 0) return false;
        Volatile.Write(ref clientColormapMonth, month);
        Volatile.Write(ref clientColors, next);
        return true;
    }

    /// <summary>Snow layers are an overlay in LiveMap, not the map's land colour.</summary>
    public bool IsMapOverlay(int blockId)
    {
        var info = Get(blockId);
        var path = info.Code;
        var separator = path.IndexOf(':');
        if (separator >= 0) path = path[(separator + 1)..];
        return (path.EndsWith("-snow", StringComparison.OrdinalIgnoreCase) && !info.IsMicroBlock)
            || path.EndsWith("-snow2", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("-snow3", StringComparison.OrdinalIgnoreCase)
            || path.Equals("snowblock", StringComparison.OrdinalIgnoreCase)
            || path.Contains("snowlayer-", StringComparison.OrdinalIgnoreCase);
    }

    public bool Occludes(int blockId, int face)
    {
        var info = Get(blockId);
        return info.Geometry is BlockGeometryKind.Cube or BlockGeometryKind.Boxes or BlockGeometryKind.Shape && info.OpaqueFaces[face];
    }

    public bool HidesFace(BlockMaterialInfo current, int currentFace, int neighborId, int neighborFace)
    {
        if (neighborId == 0) return false;
        var neighbor = Get(neighborId);
        if (neighbor.Geometry == BlockGeometryKind.Empty || neighbor.Geometry == BlockGeometryKind.Cross) return false;
        var neighborOpaque = neighbor.OpaqueFaces[neighborFace];
        var currentOpaque = current.OpaqueFaces[currentFace];
        var sameBlock = current.Id == neighbor.Id;
        var sameMaterial = current.BlockMaterial == neighbor.BlockMaterial;
        var sameLiquid = current.LiquidCode.Length > 0 && current.LiquidCode.Equals(neighbor.LiquidCode, StringComparison.Ordinal);
        var currentHorizontal = currentFace is 0 or 1 or 4 or 5;

        return current.FaceCullMode switch
        {
            EnumFaceCullMode.NeverCull => false,
            EnumFaceCullMode.Merge => sameBlock || currentOpaque && neighborOpaque,
            EnumFaceCullMode.Collapse => sameBlock ? currentFace is not (0 or 3 or 4) : currentOpaque && neighborOpaque,
            EnumFaceCullMode.MergeMaterial => current.SolidFaces[currentFace] && neighbor.SolidFaces[neighborFace]
                && (sameMaterial || currentOpaque && neighborOpaque),
            EnumFaceCullMode.CollapseMaterial => sameMaterial
                ? currentFace is not (0 or 4)
                : neighborOpaque && (!currentHorizontal || currentOpaque),
            EnumFaceCullMode.Liquid => sameLiquid || currentFace != 3 && neighbor.SolidFaces[neighborFace],
            EnumFaceCullMode.FlushExceptTop => currentFace != 3 && (neighborOpaque || sameBlock),
            EnumFaceCullMode.Stairs => currentFace != 3 && (neighborOpaque || sameBlock && !currentOpaque),
            _ => neighborOpaque && (currentOpaque || current.Geometry == BlockGeometryKind.Shape)
        };
    }

    /// <summary>
    /// Match Vintage Story 1.22.x BlockEntityMicroBlock.isMergableMaterial.
    /// This is intentionally separate from HidesFace(): the native voxel
    /// mesher compares VoxelMaterial render passes, not Block.FaceCullMode.
    /// </summary>
    public bool CanMergeMicroMaterials(int selfMaterialId, int otherMaterialId)
    {
        if (selfMaterialId < 0 || otherMaterialId < 0) return false;
        if ((uint)selfMaterialId >= (uint)blocks.Length || (uint)otherMaterialId >= (uint)blocks.Length)
            return false;
        if (selfMaterialId == otherMaterialId) return true;

        var self = blocks[selfMaterialId] ?? MissingMaterial;
        var other = blocks[otherMaterialId] ?? MissingMaterial;
        if (self.Id == other.Id) return true;
        if (other.Id == 0) return false;

        // Native 1.22.7:
        // switch (self.RenderPass - 1) { case 0,1,3: return false;
        // case 2,4,5: selfOpaque = false; }
        var selfOpaque = true;
        switch ((int)self.RenderPass - 1)
        {
            case 0:
            case 1:
            case 3:
                return false;
            case 2:
            case 4:
            case 5:
                selfOpaque = false;
                break;
        }

        // Native 1.22.7:
        // if (other.RenderPass - 2 <= 1 || other.RenderPass - 5 <= 1)
        //     otherOpaque = false;
        var otherOpaque = !((int)other.RenderPass - 2 <= 1
            || (int)other.RenderPass - 5 <= 1);
        if (selfOpaque && otherOpaque) return true;
        if (selfOpaque) return false;
        return otherOpaque || self.CullBetweenTransparents;
    }

    public (float U0, float V0, float U1, float V1) Uv(int tile)
    {
        tile = Math.Clamp(tile, 0, cells.Count - 1);
        var column = tile % AtlasColumns;
        var row = tile / AtlasColumns;
        const float inset = 0.7f;
        var x = column * AtlasSlotSize + AtlasGutter;
        var y = row * AtlasSlotSize + AtlasGutter;
        var u0 = (x + inset) / AtlasWidth;
        var u1 = (x + CellSize - inset) / AtlasWidth;
        var v0 = 1f - (y + CellSize - inset) / AtlasHeight;
        var v1 = 1f - (y + inset) / AtlasHeight;
        return (u0, v0, u1, v1);
    }

    public (float U, float V) Uv(int tile, float localU, float localV)
    {
        tile = Math.Clamp(tile, 0, cells.Count - 1);
        var column = tile % AtlasColumns;
        var row = tile / AtlasColumns;
        const float inset = 0.7f;
        var usable = CellSize - inset * 2;
        var x = column * AtlasSlotSize + AtlasGutter;
        var y = row * AtlasSlotSize + AtlasGutter;
        var u = (x + inset + Math.Clamp(localU, 0, 1) * usable) / AtlasWidth;
        var v = 1f - (y + CellSize - inset - Math.Clamp(localV, 0, 1) * usable) / AtlasHeight;
        return (u, v);
    }

    public static MaterialCatalog Capture(ICoreAPI api, string? clientAssetsPath = null)
    {
        var worldBlocks = api.World.Blocks;
        var blocksByCode = worldBlocks
            .Where(block => block?.Code != null)
            .ToDictionary(block => block!.Code.ToString(), block => block!, StringComparer.OrdinalIgnoreCase);
        var textureAssets = IndexTextureAssets(api.Assets, clientAssetsPath, out var externalRoots);
        var textureExamples = worldBlocks
            .Where(block => block?.Textures != null)
            .SelectMany(block => block.Textures.Values)
            .Where(texture => texture?.Base != null)
            .Select(texture => texture.Base.ToString())
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToArray();
        api.Logger.Notification("ServerMap indexed {0} texture assets from {1}; block texture examples: {2}", textureAssets.Count, externalRoots.Length == 0 ? "server origins only" : string.Join(", ", externalRoots), string.Join(", ", textureExamples));
        var maxId = Math.Max(1, worldBlocks.Where(block => block != null).Select(block => block.Id).DefaultIfEmpty().Max());
        var result = new BlockMaterialInfo[maxId + 1];
        var cells = new List<byte[]>();
        var tileByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        // Failed texture lookups must not allocate one black 64x64 cell per
        // shape face. Keep the audit tile shared while remembering which
        // source keys are known to be unavailable.
        var missingTextureKeys = new HashSet<string>(StringComparer.Ordinal);
        var shapeCache = new Dictionary<AssetLocation, Shape?>();
        var shapeTextureCache = new Dictionary<AssetLocation, IDictionary<string, CompositeTexture>?>();
        var colorMaps = new Dictionary<string, ColorMap>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in worldBlocks.Where(block => block != null))
        {
            if (!string.IsNullOrWhiteSpace(block.ClimateColorMap) && block.ClimateColorMapResolved != null) colorMaps[block.ClimateColorMap] = block.ClimateColorMapResolved;
            if (!string.IsNullOrWhiteSpace(block.SeasonColorMap) && block.SeasonColorMapResolved != null) colorMaps[block.SeasonColorMap] = block.SeasonColorMapResolved;
        }
        var resolved = 0;
        var fallback = 0;
        var shapeBlocks = 0;
        var shapeFaces = 0;
        var missingShapes = 0;
        var missingShapeExamples = new HashSet<string>(StringComparer.Ordinal);
        var unresolved = new HashSet<string>(StringComparer.Ordinal);
        var roofingDiagnostics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var airCell = MakeFallbackCell(28, 34, 40, 0, checker: false);
        cells.Add(airCell);
        tileByKey["air"] = 0;
        var missingTile = cells.Count;
        cells.Add(MakeFallbackCell(0, 0, 0, 255, checker: false));
        result[0] = new BlockMaterialInfo
        {
            Id = 0,
            Code = "game:air",
            FaceTiles = [0, 0, 0, 0, 0, 0],
            InsideFaceTiles = [0, 0, 0, 0, 0, 0],
            TopSoilOverlayTiles = [-1, -1, -1, -1, -1, -1],
            RotationIds = [0, 0, 0, 0],
            OpaqueFaces = [false, false, false, false, false, false],
            SolidFaces = [false, false, false, false, false, false],
            SideAo = [false, false, false, false, false, false],
            EmitSideAo = 0,
            LightAbsorption = 0,
            ForFluidsLayer = false,
            FaceCullMode = EnumFaceCullMode.NeverCull,
            BlockMaterial = EnumBlockMaterial.Air,
            RenderPass = EnumChunkRenderPass.Opaque,
            CullBetweenTransparents = false,
            Geometry = BlockGeometryKind.Empty,
            Layer = MeshMaterialLayer.Opaque,
            Boxes = [],
            Shapes = [],
            RandomizeAxesXYZ = false,
            RandomizeRotations = false,
            LiquidCode = "",
            MapColorCode = "land",
            IsMapWater = false,
            IsMicroBlock = false,
            IsDynamicShape = false,
            AttachAs3d = false,
            R = 28,
            G = 34,
            B = 40,
            A = 0,
            LiquidLevel = 0
        };

        int ResolveTile(CompositeTexture? texture, Block block, int discriminator, string? climateMap, string? seasonMap,
            bool topSoilOverlay = false, bool topSoilTop = false)
        {
            // Variant-resolved client blocks normally have already replaced
            // placeholders such as meta's {type}. Headless server captures
            // can retain the raw path, so resolve the variant from the
            // registered block code before indexing the real client PNG.
            texture = ResolveTextureVariables(texture, block);
            if (IsMetaBlock(block) && !IsMetaBlockLayer(block)
                && (texture == null || IsUnknownTexture(texture) || HasUnresolvedTextureVariables(texture)))
            {
                var face = discriminator is >= 0 and < 12
                    ? FaceTextureKeys[discriminator % 6]
                    : "all";
                texture = ResolveMetaTexture(block, face);
            }
            // TopSoil is two different client textures: the base soil is an
            // ordinary opaque texture, while specialSecondTexture is the
            // already-shaped grass coverage mask.  Applying the vegetation
            // pixel heuristic to both layers turns soil pixels green and
            // produces white speckles around the transparent coverage.
            var selectiveTint = !topSoilOverlay && IsVegetationTint(block, climateMap, seasonMap);
            // Variant rules (for example leaves-* and soil-*) are resolved by
            // the client atlas pipeline.  A headless server can leave the
            // final map names unset, so retain the vanilla defaults for those
            // grayscale vegetation masks.
            var effectiveClimate = climateMap;
            var effectiveSeason = seasonMap;
            if (selectiveTint && string.IsNullOrWhiteSpace(effectiveClimate)) effectiveClimate = "climatePlantTint";
            if (selectiveTint && string.IsNullOrWhiteSpace(effectiveSeason)) effectiveSeason = InferSeasonMap(block.Code.Path);
            var tint = !topSoilOverlay && block.DrawType == EnumDrawType.TopSoil
                ? SKColors.White
                : ResolveTint(textureAssets, colorMaps, effectiveClimate, effectiveSeason);
            // The client shader samples specialSecondTexture from two
            // adjacent halves: sides use the left half and the top uses the
            // right half. Keep those atlas cells distinct so a topsoil top
            // never receives the side strip.
            var textureKey = TextureKey(texture, tint);
            var roofingCode = block.Code?.Path ?? "";
            if (Regex.IsMatch(roofingCode, "^slantedroofing-", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                && roofingDiagnostics.Add($"{block.Code}:{texture?.Base}"))
            {
                var found = texture?.Base != null && FindTexture(textureAssets, texture.Base, block.Code.Domain) != null;
                api.Logger.Notification("ServerMap roofing texture: {0}; base={1}; found={2}; top={3}; agedstraw={4}; bamboo-top={5}",
                    block.Code, texture?.Base?.ToString() ?? "missing", found,
                    block.Textures?.ContainsKey("top") == true,
                    block.Textures?.ContainsKey("agedstraw") == true,
                    block.Textures?.ContainsKey("bamboo-top") == true);
            }
            if (texture is null || texture.Base is null)
            {
                if (missingTextureKeys.Add(textureKey)) fallback++;
                return missingTile;
            }
            // The vanilla basic cube deliberately points at game:unknown when
            // a block has no world texture.  That PNG is a white inventory
            // placeholder, not a renderable material. Keep it on the audit
            // path so chiseled/micro blocks and unresolved clutter remain
            // visibly missing instead of looking like valid white geometry.
            if (IsUnknownTexture(texture))
            {
                if (missingTextureKeys.Add(textureKey)) fallback++;
                return missingTile;
            }
            if (missingTextureKeys.Contains(textureKey)) return missingTile;

            // specialSecondTexture is a 2:1 atlas. The side and top halves
            // must remain separate even though they share the same source.
            var key = textureKey + (topSoilOverlay ? topSoilTop ? "|topsoil-top" : "|topsoil-side" : "");
            if (tileByKey.TryGetValue(key, out var existing)) return existing;

            byte[] cell;
            var textureFace = discriminator is >= 0 and < 12 ? discriminator % 6 : -1;
            if (TryBuildTextureCell(textureAssets, texture, block.Code.Domain, tint, selectiveTint,
                topSoilOverlay, topSoilTop, textureFace, out cell))
            {
                resolved++;
            }
            else
            {
                missingTextureKeys.Add(textureKey);
                if (unresolved.Count < 20) unresolved.Add($"{block.Code}: {texture.Base}");
                // A missing client/server texture must remain unmistakable.
                // Category colours hide asset lookup failures and were often
                // mistaken for incorrect terrain tinting.
                fallback++;
                return missingTile;
            }
            var tile = cells.Count;
            cells.Add(cell);
            tileByKey[key] = tile;
            return tile;
        }

        foreach (var block in worldBlocks.Where(block => block?.Code != null).OrderBy(block => block.Id))
        {
            var geometry = ResolveGeometry(block);
            var faceTiles = new int[6];
            var insideFaceTiles = new int[6];
            var topSoilOverlayTiles = Enumerable.Repeat(-1, 6).ToArray();
            var rotationIds = new[] { block.Id, block.Id, block.Id, block.Id };
            for (var quarter = 1; quarter < rotationIds.Length; quarter++)
            {
                try
                {
                    var rotatedCode = block.GetRotatedBlockCode(quarter * 90);
                    if (rotatedCode != null && blocksByCode.TryGetValue(rotatedCode.ToString(), out var rotatedBlock))
                        rotationIds[quarter] = rotatedBlock.Id;
                }
                catch { /* blocks without directional variants keep their source material */ }
            }
            var faceColors = new (byte R, byte G, byte B, byte A)[6];
            ShapeTemplate[] templates = [];

            if (geometry == BlockGeometryKind.Shape)
            {
                templates = BuildShapeTemplates(api, block, shapeCache, (texture, discriminator, climate, season) =>
                    ResolveTile(texture, block, discriminator, climate, season));
                if (templates.Length > 0)
                {
                    if (templates.All(item => item.Faces.Length == 0))
                    {
                        geometry = BlockGeometryKind.Empty;
                    }
                    else
                    {
                        shapeBlocks++;
                        shapeFaces += templates.Sum(item => item.Faces.Length);
                        var template = templates.First(item => item.Faces.Length > 0);
                        var defaultTile = template.Faces.FirstOrDefault(face => face.BoundaryFace == 3)?.Tile ?? template.Faces[0].Tile;
                        Array.Fill(faceTiles, defaultTile);
                        foreach (var face in template.Faces)
                        {
                            if (face.BoundaryFace >= 0) faceTiles[face.BoundaryFace] = face.Tile;
                        }
                        Array.Copy(faceTiles, insideFaceTiles, faceTiles.Length);
                    }
                }
                else
                {
                    missingShapes++;
                    if (missingShapeExamples.Count < 20) missingShapeExamples.Add($"{block.Code} -> {block.Shape?.Base}");
                    geometry = ResolveFallbackGeometry(block);
                }
            }

            // VoxelMaterial.FromBlock builds a six-face texture array for
            // every registered source block, even when the block's normal
            // terrain geometry is Empty (Meta blocks and nested microblocks
            // are common examples). Keep resolving those textures so a
            // valid source does not silently become an air tile in a voxel
            // cuboid. Missing texture definitions still resolve to the
            // shared black audit tile.
            if (block.Id != 0)
            {
                for (var face = 0; face < 6; face++)
                {
                    // BlockEntityMicroBlock.VoxelMaterial.FromBlock uses the
                    // client's exact face keys.  Semantic aliases such as
                    // "horizontal" are shape/texturesByType conveniences,
                    // not interchangeable voxel faces.
                    var texture = SelectVoxelTexture(block, FaceTextureKeys[face])
                        ?? SelectShapeVoxelTexture(api, block, FaceTextureKeys[face], shapeTextureCache);
                    faceTiles[face] = ResolveTile(texture, block, face, block.ClimateColorMap, block.SeasonColorMap);
                    // The client uses the selected outside face whenever an
                    // inside-face key is absent. SelectVoxelTexture follows
                    // that rule, including the first-texture fallback.
                    var insideTexture = SelectVoxelTexture(block, FaceTextureKeys[face], inside: true)
                        ?? SelectShapeVoxelTexture(api, block, FaceTextureKeys[face], shapeTextureCache);
                    insideFaceTiles[face] = insideTexture == null
                        ? faceTiles[face]
                        : ResolveTile(insideTexture, block, 6 + face, block.ClimateColorMap, block.SeasonColorMap);
                }
            }

            if (block.DrawType == EnumDrawType.TopSoil)
            {
                var overlayTexture = SelectTopSoilTexture(block);
                if (overlayTexture != null)
                {
                    for (var face = 0; face < 6; face++)
                    {
                        topSoilOverlayTiles[face] = ResolveTile(overlayTexture, block, 20 + face,
                            block.ClimateColorMap, block.SeasonColorMap, topSoilOverlay: true, topSoilTop: face == 3);
                    }
                }
            }

            if (geometry == BlockGeometryKind.Empty || geometry == BlockGeometryKind.Cross)
                Array.Copy(faceTiles, insideFaceTiles, faceTiles.Length);

            for (var face = 0; face < 6; face++) faceColors[face] = Average(cells[Math.Clamp(faceTiles[face], 0, cells.Count - 1)]);
            var average = faceColors[3].A > 5 ? faceColors[3] : faceColors.FirstOrDefault(color => color.A > 5);
            var boxes = ResolveBoxes(block, geometry);
            var layer = ResolveLayer(block, geometry, faceTiles, cells);
            var mapColorCode = block.Attributes?["mapColorCode"]?.AsString();
            var path = block.Code.Path;
            if (block.BlockMaterial == EnumBlockMaterial.Snow && path.Contains("snowblock", StringComparison.OrdinalIgnoreCase)) mapColorCode = "glacier";
            if (string.IsNullOrWhiteSpace(mapColorCode)) mapColorCode = DefaultMapColorCode(block.BlockMaterial);
            result[block.Id] = new BlockMaterialInfo
            {
                Id = block.Id,
                Code = block.Code.ToString(),
                FaceTiles = faceTiles,
                InsideFaceTiles = insideFaceTiles,
                TopSoilOverlayTiles = topSoilOverlayTiles,
                RotationIds = rotationIds,
                OpaqueFaces = FaceToEngineSide.Select(side => block.SideOpaque[side]).ToArray(),
                SolidFaces = FaceToEngineSide.Select(side => block.SideSolid[side]).ToArray(),
                SideAo = FaceToEngineSide.Select(side => block.SideAo[side]).ToArray(),
                EmitSideAo = block.EmitSideAo,
                LightAbsorption = block.LightAbsorption,
                ForFluidsLayer = block.ForFluidsLayer,
                FaceCullMode = block.FaceCullMode,
                BlockMaterial = block.BlockMaterial,
                RenderPass = block.RenderPass,
                // FromBlock(..., cullBetweenTransparents: true) is the
                // native path used by BlockEntityMicroBlock for all registry
                // materials, including transparent ones.
                CullBetweenTransparents = true,
                Geometry = geometry,
                Layer = layer,
                Boxes = boxes,
                Shapes = templates,
                RandomizeAxesXYZ = block.RandomizeAxes == EnumRandomizeAxes.XYZ,
                RandomizeRotations = block.RandomizeRotations,
                LiquidCode = block.LiquidCode ?? "",
                MapColorCode = mapColorCode,
                IsMapWater = IsLiquidMaterial(block.BlockMaterial)
                    || block.BlockMaterial == EnumBlockMaterial.Ice && !path.Equals("glacierice", StringComparison.OrdinalIgnoreCase),
                IsMicroBlock = path.StartsWith("chiseledblock", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("microblock", StringComparison.OrdinalIgnoreCase),
                // Crates are rendered from BlockEntityCrate.type/lidState in
                // the 1.22.x client, just like ShapeFromAttributes blocks.
                IsDynamicShape = block is BlockShapeFromAttributes || block is BlockCrate,
                AttachAs3d = block.Attributes?["attachas3d"]?.AsBool(false) ?? false,
                R = average.R,
                G = average.G,
                B = average.B,
                A = average.A,
                LiquidLevel = Math.Clamp(block.LiquidLevel, 0, 7),
                SourceBlock = block
            };
        }

        // BlockClutter/ShapeFromAttributes resolves its shape and texture
        // groups from the block-entity `type` at runtime.  Capture every
        // registered type here so ruins and trader structures use the same
        // concrete shape bindings as the client instead of the generic cube.
        var dynamicShapes = new Dictionary<string, DynamicShapeMaterial>(StringComparer.OrdinalIgnoreCase);
        var dynamicBlockCount = 0;
        var dynamicTypeCount = 0;
        foreach (var shapeBlock in worldBlocks.OfType<BlockShapeFromAttributes>())
        {
            try
            {
                dynamicBlockCount++;
                // LoadTypes normally populates AllTypes during asset
                // finalisation. On a headless server some content packs leave
                // it empty even though the raw `types` attribute is present.
                // Keep the serialized clutter definitions as a fallback, but
                // retain the interface so non-clutter ShapeFromAttributes
                // blocks (banners, bookshelves and typed rocks) are captured.
                // The game content loader can deserialize these attributes on
                // the headless/server side before the owning asset domain is
                // known to JsonObject.AsObject().  Reparse the raw JSON with
                // the block domain so unqualified paths such as
                // block/wood/debarked/aged resolve to survival: rather than
                // game:. This mirrors the client asset parser in 1.22.x.
                var blockDomain = string.IsNullOrWhiteSpace(shapeBlock.Code?.Domain)
                    ? "game"
                    : shapeBlock.Code.Domain;
                var rawTypes = DeserializeWithDomain(shapeBlock.Attributes?["types"], Array.Empty<ClutterTypeProps>(), blockDomain);
                var types = rawTypes is { Length: > 0 }
                    ? rawTypes.Cast<IShapeTypeProps>().ToArray()
                    : shapeBlock.AllTypes?.ToArray() ?? [];
                foreach (var type in types)
                {
                    if (string.IsNullOrWhiteSpace(type.Code)) continue;
                    dynamicTypeCount++;
                    var shapePath = ResolveDynamicShapePath(api, shapeBlock, type);
                    if (shapePath == null) continue;
                    // Reparse the owning block attributes with the domain of
                    // the resolved shape asset. Vanilla BlockClutter blocks
                    // are registered in game: but their shape/type JSON and
                    // relative textures live in survival:.
                    var captureDomain = string.IsNullOrWhiteSpace(shapePath.Domain) ? blockDomain : shapePath.Domain;
                    var domainTypes = DeserializeWithDomain(shapeBlock.Attributes?["types"], Array.Empty<ClutterTypeProps>(), captureDomain);
                    var runtimeType = domainTypes?.FirstOrDefault(candidate =>
                        string.Equals(candidate?.Code, type.Code, StringComparison.OrdinalIgnoreCase)) ?? type;
                    var serializedTextures = DeserializeWithDomain(shapeBlock.Attributes?["textures"],
                        (IDictionary<string, CompositeTexture>)new Dictionary<string, CompositeTexture>(StringComparer.OrdinalIgnoreCase), captureDomain);
                    var serializedOverrideGroups = DeserializeWithDomain(shapeBlock.Attributes?["overrideTextureGroups"],
                        new Dictionary<string, Vintagestory.API.Datastructures.OrderedDictionary<string, CompositeTexture>>(StringComparer.OrdinalIgnoreCase), captureDomain);
                    // The client tesselator merges all three sources. In a
                    // server-side capture blockTextures is commonly null, so
                    // read the serialized attributes as the authoritative
                    // fallback instead of dropping the banner's custom keys.
                    var dynamicTextures = new Dictionary<string, CompositeTexture>(StringComparer.OrdinalIgnoreCase);
                    if (serializedTextures is { Count: > 0 } attributeTextures)
                    {
                        foreach (var pair in attributeTextures) if (pair.Value != null) dynamicTextures[pair.Key] = pair.Value;
                    }
                    else if (shapeBlock.blockTextures != null)
                    {
                        foreach (var pair in shapeBlock.blockTextures) if (pair.Value != null) dynamicTextures[pair.Key] = pair.Value;
                    }
                    if (runtimeType.Textures != null)
                        foreach (var pair in runtimeType.Textures) if (pair.Value != null) dynamicTextures[pair.Key] = pair.Value;

                    var composite = new CompositeShape { Base = shapePath };
                    var shapes = BuildShapeTemplates(api, shapeBlock, shapeCache,
                        (texture, discriminator, climate, season) => ResolveTile(texture, shapeBlock, discriminator, climate, season),
                        dynamicTextures, composite);
                    if (shapes.Length == 0)
                    {
                        if (dynamicTypeCount <= 16 || runtimeType.Code.Contains("hansa-", StringComparison.OrdinalIgnoreCase))
                            api.Logger.Warning("ServerMap dynamic shape missing: {0} type {1} -> {2}", shapeBlock.Code, runtimeType.Code, shapePath);
                        continue;
                    }
                    if (dynamicTypeCount <= 16 || runtimeType.Code.Contains("hansa-", StringComparison.OrdinalIgnoreCase))
                    {
                        var textureSummary = dynamicTextures.Count == 0
                            ? "none"
                            : string.Join(", ", dynamicTextures.Select(pair => $"{pair.Key}={pair.Value?.Base}"));
                        api.Logger.Notification("ServerMap dynamic shape: {0} type {1} -> {2}; {3} faces; textures {4}",
                            shapeBlock.Code, runtimeType.Code, shapePath, shapes.Sum(shape => shape.Faces.Length), textureSummary);
                    }

                    var material = new DynamicShapeMaterial
                    {
                        Shapes = shapes,
                        TypeRotationX = runtimeType.Rotation?.X ?? 0,
                        TypeRotationY = runtimeType.Rotation?.Y ?? 0,
                        TypeRotationZ = runtimeType.Rotation?.Z ?? 0,
                        RandomizeYSize = runtimeType.RandomizeYSize && shapeBlock.AllowRandomizeDims
                    };
                    if (!string.IsNullOrWhiteSpace(runtimeType.TextureFlipCode)
                        && !string.IsNullOrWhiteSpace(runtimeType.TextureFlipGroupCode)
                        && (serializedOverrideGroups?.TryGetValue(runtimeType.TextureFlipGroupCode, out var group) == true
                            || shapeBlock.OverrideTextureGroups?.TryGetValue(runtimeType.TextureFlipGroupCode, out group) == true))
                    {
                        foreach (var pair in group)
                        {
                            if (pair.Value == null) continue;
                            var variantTextures = new Dictionary<string, CompositeTexture>(dynamicTextures, StringComparer.OrdinalIgnoreCase)
                            {
                                [runtimeType.TextureFlipCode] = pair.Value
                            };
                            var variantShapes = BuildShapeTemplates(api, shapeBlock, shapeCache,
                                (texture, discriminator, climate, season) => ResolveTile(texture, shapeBlock, discriminator, climate, season),
                                variantTextures, composite);
                            if (variantShapes.Length > 0) material.OverrideVariants[pair.Key] = variantShapes;
                        }
                    }
                    dynamicShapes[DynamicShapeKey(shapeBlock.Id, runtimeType.Code)] = material;
                    // BEBehaviorShapeFromAttributes uses BlockClutter.Remap
                    // when hydrating saved entities. Register that canonical
                    // key as well as the authored key so both fresh and old
                    // entities resolve to the same real shape/material.
                    var canonicalType = RemapDynamicType(runtimeType.Code);
                    if (!string.Equals(canonicalType, runtimeType.Code, StringComparison.OrdinalIgnoreCase))
                        dynamicShapes[DynamicShapeKey(shapeBlock.Id, canonicalType)] = material;
                }
            }
            catch (Exception ex)
            {
                api.Logger.Warning("ServerMap dynamic shape capture failed for {0}: {1}", shapeBlock.Code, ex.Message);
            }
        }

        // BlockCrate is a dynamic shape entity in 1.22.x.  The normal block
        // shape is only an inventory placeholder; the placed entity selects
        // Props[type].Shape and BlockCrate.ITexPositionSource resolves
        // `${type}-${textureCode}` before the shape's fallback key.
        var crateBlocks = worldBlocks.OfType<BlockCrate>().ToArray();
        var crateVariantCount = 0;
        api.Logger.Notification("ServerMap crate material sources: blocks={0}; runtime props={1}; types={2}; properties={3}",
            crateBlocks.Length,
            crateBlocks.Count(crate => crate.Props != null),
            crateBlocks.Sum(crate => crate.Props?.Types?.Length ?? 0),
            crateBlocks.Sum(crate => crate.Props?.Properties?.Count ?? 0));
        foreach (var crate in crateBlocks)
        {
            try
            {
                var props = crate.Props;
                if (props?.Properties == null || props.Types is not { Length: > 0 })
                {
                    // The block's Attributes object is already the patched
                    // 1.22.x `attributes` section. Reparse it directly before
                    // touching the raw asset so server-side JSON patching and
                    // mod overrides remain authoritative.
                    try
                    {
                        props = DeserializeWithDomain(crate.Attributes,
                            (CrateProperties?)null, crate.Code?.Domain ?? "game");
                    }
                    catch { /* use the vanilla asset fallback below */ }
                }
                if (props?.Properties == null || props.Types is not { Length: > 0 })
                {
                    props = LoadVanillaCrateProperties(api, crate.Code?.Domain ?? "game");
                    api.Logger.Warning("ServerMap crate runtime Props unavailable for {0}; raw 1.22.x crate.json fallback={1}.",
                        crate.Code, props != null);
                }
                if (props?.Properties == null) continue;
                // Props.Properties only contains special overrides plus "*";
                // the actual vanilla type list (including wood-maple) lives
                // in Props.Types. Expand that list exactly as BlockCrate's
                // indexer does before capturing each runtime variant.
                var crateTypes = (props.Types ?? Array.Empty<string>())
                    .Concat(props.Properties.Keys)
                    .Where(type => !string.IsNullOrWhiteSpace(type) && type != "*")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                foreach (var type in crateTypes)
                {
                    var typeProps = props[type];
                    if (typeProps?.Shape?.Base == null) continue;
                    foreach (var lidState in new[] { "closed", "opened" })
                    {
                        var shape = typeProps.Shape.Clone();
                        if (lidState == "opened") shape.Base.Path = shape.Base.Path.Replace("closed", "opened", StringComparison.OrdinalIgnoreCase);
                        var shapePath = shape.Base;
                        var captureDomain = string.IsNullOrWhiteSpace(shapePath.Domain) ? crate.Code.Domain : shapePath.Domain;
                        var serializedTextures = crate.Textures;
                        var crateTextures = new Dictionary<string, CompositeTexture>(StringComparer.OrdinalIgnoreCase);
                        if (serializedTextures != null)
                        {
                            foreach (var texture in serializedTextures)
                            {
                                if (texture.Value == null) continue;
                                crateTextures[texture.Key] = texture.Value;
                            }
                        }
                        var parsedShape = LoadShapeWithDomain(api, shapePath.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json"));
                        if (parsedShape?.Textures != null)
                        {
                            foreach (var texture in parsedShape.Textures)
                            {
                                if (texture.Value == null) continue;
                                // Official BlockCrate first probes type-key,
                                // then the bare shape key. Bake the resolved
                                // result under the bare key for ShapeTemplate.
                                if (crateTextures.TryGetValue(type + "-" + texture.Key, out var typed)) crateTextures[texture.Key] = typed;
                                else if (!crateTextures.ContainsKey(texture.Key)) crateTextures[texture.Key] = new CompositeTexture(texture.Value);
                            }
                        }
                        // BlockCrate's 1.22.x TextureSource normally exposes
                        // typed entries after client asset finalization. A
                        // dedicated server can retain only the raw definition,
                        // so provide the deterministic vanilla mapping rather
                        // than accepting the normal shape's default material.
                        if (type.StartsWith("wood-", StringComparison.OrdinalIgnoreCase))
                        {
                            var wood = type[5..];
                            var sidesPath = wood.Equals("aged", StringComparison.OrdinalIgnoreCase)
                                ? "block/wood/crate/aged-sides"
                                : $"block/wood/crate/{wood}-sides";
                            var insidePath = wood.Equals("aged", StringComparison.OrdinalIgnoreCase)
                                ? "block/wood/crate/aged-inside"
                                : $"block/wood/crate/{wood}-inside";
                            crateTextures["sides"] = new CompositeTexture { Base = new AssetLocation("survival", sidesPath) };
                            crateTextures["inside"] = new CompositeTexture { Base = new AssetLocation("survival", insidePath) };
                        }
                        api.Logger.Notification("ServerMap crate capture: {0} type {1} state {2}; sides={3}; inside={4}; frame={5}",
                            crate.Code, type, lidState,
                            crateTextures.TryGetValue("sides", out var sides) ? sides?.Base?.ToString() ?? "null" : "missing",
                            crateTextures.TryGetValue("inside", out var inside) ? inside?.Base?.ToString() ?? "null" : "missing",
                            crateTextures.TryGetValue("frame-generic", out var frame) ? frame?.Base?.ToString() ?? "null" : "missing");
                        var composite = new CompositeShape { Base = shapePath, rotateX = shape.rotateX, rotateY = shape.rotateY, rotateZ = shape.rotateZ };
                        var shapes = BuildShapeTemplates(api, crate, shapeCache,
                            (texture, discriminator, climate, season) => ResolveTile(texture, crate, discriminator, climate, season),
                            crateTextures, composite);
                        if (shapes.Length == 0) continue;
                        dynamicShapes[DynamicShapeKey(crate.Id, type + "-" + lidState)] = new DynamicShapeMaterial
                        {
                            Shapes = shapes,
                            TypeRotationX = 0,
                            TypeRotationY = 0,
                            TypeRotationZ = 0,
                            RandomizeYSize = false
                        };
                        crateVariantCount++;
                    }
                }
            }
            catch (Exception ex)
            {
                api.Logger.Warning("ServerMap crate shape capture failed for {0}: {1}", crate.Code, ex.Message);
            }
        }
        api.Logger.Notification("ServerMap inspected {0} ShapeFromAttributes blocks/{1} types; captured {2} runtime shape variants.",
            dynamicBlockCount, dynamicTypeCount, dynamicShapes.Count);
        api.Logger.Notification("ServerMap crate capture summary: blocks={0}; variants={1}; keys={2}",
            crateBlocks.Length, crateVariantCount,
            string.Join(",", dynamicShapes.Keys
                .Where(key => key.StartsWith("" + (crateBlocks.FirstOrDefault()?.Id ?? -1) + ":", StringComparison.Ordinal))
                .Select(key => key[(key.IndexOf(':') + 1)..])
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)));

        var leavesExample = result.FirstOrDefault(info => info?.Code.Contains("leaves-placed-", StringComparison.Ordinal) == true);
        if (leavesExample != null)
            api.Logger.Notification("ServerMap foliage check: {0} has {1} shape variants/{2} faces on {3} layer.",
                leavesExample.Code, leavesExample.Shapes.Length, leavesExample.Shapes.Sum(shape => shape.Faces.Length), leavesExample.Layer);
        if (unresolved.Count > 0) api.Logger.Warning("ServerMap unresolved texture examples: {0}", string.Join(", ", unresolved));
        if (missingShapeExamples.Count > 0) api.Logger.Warning("ServerMap shape fallback examples: {0}", string.Join("; ", missingShapeExamples));
        api.Logger.Notification("ServerMap parsed {0} JSON shape blocks with {1} faces; {2} shapes used fallback geometry.", shapeBlocks, shapeFaces, missingShapes);
        return new MaterialCatalog(result, cells, missingTile, resolved, fallback, shapeBlocks, shapeFaces, missingShapes, dynamicShapes);
    }

    private static string RemapDynamicType(string type)
    {
        type = type.Trim();
        return type.StartsWith("pipes/", StringComparison.OrdinalIgnoreCase)
            ? "pipe-veryrusted-" + type[6..]
            : type;
    }

    private static CrateProperties? LoadVanillaCrateProperties(ICoreAPI api, string fallbackDomain)
    {
        // BlockCrate.OnLoaded deserializes this exact attributes object. A
        // dedicated server can leave Props unset after content finalization,
        // but the source asset is still available through IAssetManager.
        foreach (var location in new[]
        {
            new AssetLocation("survival", "blocktypes/wood/woodtyped/crate.json"),
            new AssetLocation(fallbackDomain, "blocktypes/wood/woodtyped/crate.json"),
            new AssetLocation("game", "blocktypes/wood/woodtyped/crate.json")
        }.Distinct())
        {
            var asset = api.Assets.TryGet(location);
            if (asset == null) continue;
            try
            {
                var root = JsonObject.FromJson(asset.ToText());
                var attributes = root["attributes"];
                var props = DeserializeWithDomain(attributes, (CrateProperties?)null, location.Domain);
                if (props?.Properties != null && props.Types is { Length: > 0 }) return props;
            }
            catch (Exception ex)
            {
                api.Logger.Warning("ServerMap vanilla crate properties parse failed for {0}: {1}", location, ex.Message);
            }
        }
        return null;
    }

    // Keep the domain-aware JsonObject overload available without taking a
    // direct Newtonsoft.Json compile-time dependency. JsonObject itself owns
    // the official AssetLocationJsonParser, so invoking this overload still
    // uses the exact 1.22.x conversion rules for relative texture paths.
    private static T? DeserializeWithDomain<T>(JsonObject? source, T defaultValue, string domain)
    {
        if (source == null) return defaultValue;
        try
        {
            var method = typeof(JsonObject).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .First(candidate => candidate.Name == nameof(JsonObject.AsObject)
                    && candidate.IsGenericMethodDefinition
                    && candidate.GetParameters().Length == 2
                    && candidate.GetParameters()[1].ParameterType == typeof(string));
            return (T?)method.MakeGenericMethod(typeof(T)).Invoke(source, [defaultValue, domain]);
        }
        catch
        {
            return defaultValue;
        }
    }

    /// <summary>
    /// Loads a shape while preserving its JSON texture table on a dedicated
    /// server. Shape.TryGet uses the runtime cache; since 1.20.4 that cache is
    /// released by FreeRAMServer() and its Textures property is normally null.
    /// Re-reading the source JSON with the shape asset domain applies the same
    /// relative AssetLocation rules as the client tesselator.
    /// </summary>
    private static Shape? LoadShapeWithDomain(ICoreAPI api, AssetLocation assetLocation)
    {
        var asset = api.Assets.TryGet(assetLocation);
        // Shape.Base can retain the registry domain on a headless server,
        // while the vanilla 1.22.x JSON is supplied by survival. Resolve the
        // exact path across domains before falling back to Shape.TryGet.
        if (asset == null)
        {
            var locations = api.Assets.GetLocations(assetLocation.Path, null)
                .Where(location => location.Path.Equals(assetLocation.Path, StringComparison.OrdinalIgnoreCase));
            foreach (var domain in new[] { assetLocation.Domain, "survival", "game", "creative" })
            {
                var location = locations.FirstOrDefault(candidate => candidate.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase));
                if (location == null) continue;
                asset = api.Assets.TryGet(location);
                if (asset != null) { assetLocation = location; break; }
            }
        }
        if (asset == null) return null;

        try
        {
            var method = typeof(JsonUtil).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(candidate => candidate.Name == nameof(JsonUtil.ToObject)
                    && candidate.IsGenericMethodDefinition
                    && candidate.GetParameters().Length == 3
                    && candidate.GetParameters()[0].ParameterType == typeof(string)
                    && candidate.GetParameters()[1].ParameterType == typeof(string));
            return (Shape?)method.MakeGenericMethod(typeof(Shape)).Invoke(null, [asset.ToText(), assetLocation.Domain, null]);
        }
        catch
        {
            // Keep the native loader as a geometry-only fallback. Its server
            // cache may omit Textures, but it can still provide shape elements
            // when a malformed or patched asset cannot be reparsed above.
            try { return Shape.TryGet(api, assetLocation); }
            catch { return null; }
        }
    }

    public void Write(string root)
    {
        var directory = Path.Combine(root, "materials", Fingerprint);
        Directory.CreateDirectory(directory);
        var atlasPath = Path.Combine(directory, "atlas.png");
        var manifestPath = Path.Combine(directory, "materials.json");
        AtomicFile.Replace(atlasPath, path => File.WriteAllBytes(path, BuildAtlasPng()));
        AtomicFile.Replace(manifestPath, path => File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            version = 1,
            fingerprint = Fingerprint,
            atlas = new
            {
                file = "atlas.png", width = AtlasWidth, height = AtlasHeight,
                cellSize = CellSize, slotSize = AtlasSlotSize, padding = AtlasGutter, tiles = cells.Count
            },
            coverage = new
            {
                blocks = blocks.Length,
                resolvedTextures = ResolvedTextures,
                fallbackTextures = FallbackTextures,
                shapeBlocks = ShapeBlocks,
                shapeFaces = ShapeFaces,
                missingShapes = MissingShapes
            },
            blocks = blocks.Select((rawBlock, blockId) =>
            {
                var block = rawBlock ?? MissingMaterial;
                return new
                {
                    id = rawBlock?.Id ?? blockId,
                    code = block.Code,
                    faces = block.FaceTiles,
                    insideFaces = block.InsideFaceTiles,
                    topSoilOverlays = block.TopSoilOverlayTiles,
                    rotations = block.RotationIds,
                    opaque = block.OpaqueFaces,
                    sideAo = block.SideAo,
                    emitSideAo = block.EmitSideAo,
                    lightAbsorption = block.LightAbsorption,
                    renderPass = (int)block.RenderPass,
                    cullBetweenTransparents = block.CullBetweenTransparents,
                    color = new[] { block.R, block.G, block.B, block.A },
                    liquidLevel = block.LiquidLevel,
                    layer = block.Layer.ToString().ToLowerInvariant(),
                    shapeVariants = block.Shapes.Length,
                    geometry = block.Geometry.ToString().ToLowerInvariant(),
                    attachAs3d = block.AttachAs3d
                };
            })
        }, new JsonSerializerOptions { WriteIndented = true })));
    }

    private byte[] BuildAtlasPng()
    {
        using var bitmap = new SKBitmap(AtlasWidth, AtlasHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        bitmap.Erase(SKColors.Transparent);
        for (var tile = 0; tile < cells.Count; tile++)
        {
            var x = tile % AtlasColumns * AtlasSlotSize + AtlasGutter;
            var y = tile / AtlasColumns * AtlasSlotSize + AtlasGutter;
            var cell = cells[tile];
            for (var py = -AtlasGutter; py < CellSize + AtlasGutter; py++)
            for (var px = -AtlasGutter; px < CellSize + AtlasGutter; px++)
            {
                var sourceX = Math.Clamp(px, 0, CellSize - 1);
                var sourceY = Math.Clamp(py, 0, CellSize - 1);
                var offset = (sourceY * CellSize + sourceX) * 4;
                bitmap.SetPixel(x + px, y + py, new SKColor(cell[offset], cell[offset + 1], cell[offset + 2], cell[offset + 3]));
            }
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private string ComputeFingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(AtlasLayoutRevision));
        foreach (var rawBlock in blocks)
        {
            var block = rawBlock ?? MissingMaterial;
            hash.AppendData(Encoding.UTF8.GetBytes(block.Code));
            foreach (var tile in block.FaceTiles) hash.AppendData(BitConverter.GetBytes(tile));
            foreach (var tile in block.InsideFaceTiles) hash.AppendData(BitConverter.GetBytes(tile));
            foreach (var tile in block.TopSoilOverlayTiles) hash.AppendData(BitConverter.GetBytes(tile));
            foreach (var rotatedId in block.RotationIds) hash.AppendData(BitConverter.GetBytes(rotatedId));
            hash.AppendData([(byte)block.Geometry]);
            hash.AppendData([(byte)(block.AttachAs3d ? 1 : 0)]);
            hash.AppendData([(byte)block.Layer, (byte)block.FaceCullMode, (byte)block.RenderPass,
                (byte)(block.CullBetweenTransparents ? 1 : 0)]);
            foreach (var sideAo in block.SideAo) hash.AppendData([(byte)(sideAo ? 1 : 0)]);
            hash.AppendData([block.EmitSideAo]);
            hash.AppendData(BitConverter.GetBytes(block.LightAbsorption));
            hash.AppendData([(byte)(block.ForFluidsLayer ? 1 : 0)]);
            hash.AppendData(BitConverter.GetBytes(block.LiquidLevel));
            hash.AppendData(Encoding.UTF8.GetBytes(block.LiquidCode));
            foreach (var shape in block.Shapes)
            {
                hash.AppendData(BitConverter.GetBytes(shape.OpenedRotateX));
                hash.AppendData(BitConverter.GetBytes(shape.OpenedRotateY));
                hash.AppendData(BitConverter.GetBytes(shape.OpenedRotateZ));
                hash.AppendData(BitConverter.GetBytes(shape.OpenedOriginX));
                hash.AppendData(BitConverter.GetBytes(shape.OpenedOriginY));
                hash.AppendData(BitConverter.GetBytes(shape.OpenedOriginZ));
                foreach (var face in shape.Faces)
                {
                    hash.AppendData(BitConverter.GetBytes(face.Tile));
                    hash.AppendData(Encoding.UTF8.GetBytes(face.TextureKey));
                    hash.AppendData(BitConverter.GetBytes(face.BoundaryFace));
                    hash.AppendData(BitConverter.GetBytes(face.RenderPass));
                    foreach (var value in face.Vertices) hash.AppendData(BitConverter.GetBytes(value));
                    foreach (var value in face.Normal) hash.AppendData(BitConverter.GetBytes(value));
                    foreach (var value in face.Uvs) hash.AppendData(BitConverter.GetBytes(value));
                }
            }
        }
        foreach (var cell in cells) hash.AppendData(cell);
        // Dynamic entity meshes (crates and ShapeFromAttributes) do not live
        // in BlockMaterialInfo. Include their baked tile assignments and
        // transforms so a changed runtime texture mapping invalidates the
        // atlas/cache even when the underlying PNG bytes are unchanged.
        foreach (var pair in dynamicShapes.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(pair.Key));
            var dynamic = pair.Value;
            hash.AppendData(BitConverter.GetBytes(dynamic.TypeRotationX));
            hash.AppendData(BitConverter.GetBytes(dynamic.TypeRotationY));
            hash.AppendData(BitConverter.GetBytes(dynamic.TypeRotationZ));
            foreach (var shape in dynamic.Shapes)
            foreach (var face in shape.Faces)
            {
                hash.AppendData(BitConverter.GetBytes(face.Tile));
                hash.AppendData(BitConverter.GetBytes(face.RenderPass));
                foreach (var value in face.Vertices) hash.AppendData(BitConverter.GetBytes(value));
                foreach (var value in face.Uvs) hash.AppendData(BitConverter.GetBytes(value));
            }
            foreach (var variant in dynamic.OverrideVariants.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(variant.Key));
                foreach (var shape in variant.Value)
                foreach (var face in shape.Faces) hash.AppendData(BitConverter.GetBytes(face.Tile));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()[..16];
    }

    private static CompositeTexture? SelectVoxelTexture(Block block, string face, bool inside = false)
    {
        // BlockEntityMicroBlock.VoxelMaterial.FromBlock reads the block's
        // world texture source only.  TexturesInventory are GUI/item icons
        // (often white-backed) and must never become a world face fallback.
        var selected = SelectExactTexture(block.Textures, inside ? $"inside-{face}" : face);
        if (selected == null && inside)
            selected = SelectExactTexture(block.Textures, face);
        if (selected == null)
            selected = FirstTexture(block.Textures);
        // meta-blocklayer is a temporary world-generation marker. It has no
        // legitimate standalone texture; WorldDatabaseReader must resolve it
        // to the terrain block before this catalog is queried. Never expose
        // creative:block/meta/blocklayer as a white/diagnostic material.
        return IsMetaBlockLayer(block) ? selected : selected ?? ResolveMetaTexture(block, face);
    }

    private static CompositeTexture? ResolveMetaTexture(Block block, string face)
    {
        // Vintage Story 1.22.3 defines meta blocks through texturesByType in
        // game:blocktypes/meta.json. A server-side Block can retain the
        // variant code while exposing no expanded Textures dictionary, so
        // reproduce that exact asset path instead of falling back to the
        // basic cube's game:unknown texture.
        var codePath = block.Code?.Path;
        if (codePath == null || !codePath.StartsWith("meta-", StringComparison.OrdinalIgnoreCase)) return null;
        var type = codePath[5..];
        if (type.Length == 0 || type.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_'))) return null;

        var path = $"block/meta/{type}";
        var rotation = 0;
        if (type.Equals("connector", StringComparison.OrdinalIgnoreCase))
        {
            if (face is "up" or "down") path += "-up";
            else if (face.Equals("east", StringComparison.OrdinalIgnoreCase))
            {
                path += "-up";
                rotation = 270;
            }
            else if (face.Equals("west", StringComparison.OrdinalIgnoreCase))
            {
                path += "-up";
                rotation = 90;
            }
        }

        return new CompositeTexture
        {
            // In the 1.22.3/1.22.7 vanilla asset tree these PNGs are shipped
            // by the creative domain even though meta.json is in game. Use
            // that concrete domain for the fallback only; an already
            // resolved block.Textures entry remains authoritative above.
            Base = new AssetLocation("creative", path),
            Rotation = rotation
        };
    }

    private static AssetLocation? ResolveDynamicShapePath(ICoreAPI api, BlockShapeFromAttributes shapeBlock, IShapeTypeProps type)
    {
        var basePath = shapeBlock.Attributes?["shapeBasePath"]?.AsString();
        AssetLocation? requested;
        if (type.ShapePath != null)
        {
            requested = type.ShapePath.Clone();
            if (requested.Path.StartsWith('/'))
            {
                requested.WithPathPrefixOnce("shapes").WithPathAppendixOnce(".json");
            }
            else if (!requested.Path.StartsWith("shapes/", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(basePath))
            {
                requested.WithPathPrefixOnce($"shapes/{basePath.Trim('/')}/").WithPathAppendixOnce(".json");
            }
            else
            {
                requested.WithPathAppendixOnce(".json");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(basePath) || string.IsNullOrWhiteSpace(type.Code)) return null;
            var path = $"shapes/{basePath.Trim('/')}/{type.Code.Trim('/')}.json";
            requested = new AssetLocation(shapeBlock.Code.Domain, path);
        }

        // BlockClutter.LoadTypes assigns the block's registry domain to a
        // relative ShapePath.  Vanilla clutter is registered as game:clutter
        // while its JSON shapes live in survival, so checking only the
        // requested domain loses every runtime mesh. Prefer a domain where
        // the exact asset exists, keeping the declared domain first so mod
        // overrides continue to win.
        var pathLocations = api.Assets.GetLocations(requested.Path, null)
            .Where(location => location.Path.Equals(requested.Path, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (pathLocations.Length == 0) return requested;

        var preferredDomains = new[]
        {
            requested.Domain,
            shapeBlock.Code.Domain,
            "survival",
            "game"
        }.Where(domain => !string.IsNullOrWhiteSpace(domain))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var preferred in preferredDomains)
        {
            var match = pathLocations.FirstOrDefault(location =>
                location.Domain.Equals(preferred, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return pathLocations.OrderBy(location => location.Domain, StringComparer.OrdinalIgnoreCase).First();
    }

    private static CompositeTexture? SelectTopSoilTexture(Block block)
    {
        if (block.DrawType != EnumDrawType.TopSoil || block.Textures == null) return null;
        if (block.Textures.TryGetValue("specialSecondTexture", out var second))
            return ResolveTextureAlias(block.Textures, second);
        // Variant-resolved blocks normally expose the concrete key above;
        // retain this fallback for API versions that leave the ByType key in
        // the captured dictionary.
        return block.Textures.TryGetValue("specialSecondTextureByType", out var byType)
            ? ResolveTextureAlias(block.Textures, byType)
            : null;
    }

    private static CompositeShape? ResolveShapeVariables(CompositeShape? source, Block block)
    {
        if (source?.Base == null) return source;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddVariants(values, block.Variant);
        AddVariants(values, block.VariantStrict);
        AddVariant(values, "type", block.Attributes?["type"]?.AsString());
        AddVariant(values, "material", block.Attributes?["material"]?.AsString());
        var match = Regex.Match(block.Code?.Path ?? "", "^slantedroofing-(?<material>[^-]+)-(?<orientation>[^-]+)-(?<cover>[^-]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success)
        {
            AddVariant(values, "material", match.Groups["material"].Value);
            AddVariant(values, "orientation", match.Groups["orientation"].Value);
            AddVariant(values, "cover", match.Groups["cover"].Value);
        }
        string Replace(string path) => Regex.Replace(path, "\\{([^}/]+)\\}", token =>
            values.TryGetValue(token.Groups[1].Value, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value : token.Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!source.Base.Path.Contains('{')) return source;
        var resolved = source.Clone();
        resolved.Base = source.Base.Clone();
        resolved.Base.Path = Replace(resolved.Base.Path);
        return resolved;
    }

    private static ShapeTemplate[] BuildShapeTemplates(
        ICoreAPI api,
        Block block,
        Dictionary<AssetLocation, Shape?> shapeCache,
        System.Func<CompositeTexture?, int, string?, string?, int> resolveTile,
        IDictionary<string, CompositeTexture>? overrideTextures = null,
        CompositeShape? shapeOverride = null)
    {
        var sourceShape = ResolveShapeVariables(shapeOverride ?? block.Shape, block);
        if (sourceShape == null) return [];
        // ShapeTesselator applies IWithDrawnHeight before baking each JSON
        // element.  It is not simply a final Y scale: rotation origins and
        // explicit face UVs use the same height so plants and snow layers
        // retain their intended pivot and texture crop.
        var drawnHeight = block is IWithDrawnHeight { drawnHeight: > 0 } withDrawnHeight
            ? withDrawnHeight.drawnHeight / 48f
            : 1f;
        // AssetsFinalize normally populates BakedAlternates, but server-side
        // block loading can leave only Alternates populated.  Dropping those
        // transforms makes directional blocks and foliage look consistently
        // rotated the same way.  Expand both forms explicitly.
        var composites = ExpandAlternates(sourceShape);
        var templates = new List<ShapeTemplate>(composites.Length);
        var discriminator = 100;
        foreach (var source in composites)
        {
            var composite = ResolveShapeWildcard(api.Assets, source);
            if (composite?.Base == null || composite.Format != EnumShapeFormat.VintageStory) continue;
            var faces = new List<ShapeFaceTemplate>();
            BakeComposite(composite, block, api, shapeCache, faces, resolveTile, drawnHeight, ref discriminator, overrideTextures);
            if (shapeCache.TryGetValue(composite.Base, out var parsed) && parsed != null)
            {
                var opened = FindOpenedRootPose(parsed);
                templates.Add(new ShapeTemplate
                {
                    Faces = faces.ToArray(),
                    OpenedRotateX = opened.RotateX,
                    OpenedRotateY = opened.RotateY,
                    OpenedRotateZ = opened.RotateZ,
                    OpenedOriginX = opened.OriginX,
                    OpenedOriginY = opened.OriginY,
                    OpenedOriginZ = opened.OriginZ
                });
            }
        }
        return templates.ToArray();
    }

    private static (float RotateX, float RotateY, float RotateZ, float OriginX, float OriginY, float OriginZ)
        FindOpenedRootPose(Shape shape)
    {
        var root = shape.Elements?.FirstOrDefault(element =>
            string.Equals(element.Name, "origin", StringComparison.OrdinalIgnoreCase));
        var origin = root?.RotationOrigin is { Length: >= 3 } rootOrigin
            ? (OriginX: (float)rootOrigin[0] / 16f, OriginY: (float)rootOrigin[1] / 16f, OriginZ: (float)rootOrigin[2] / 16f)
            : (OriginX: .5f, OriginY: .5f, OriginZ: .5f);
        if (shape.Animations == null) return (0, 0, 0, origin.OriginX, origin.OriginY, origin.OriginZ);

        if (root == null) return (0, 0, 0, origin.OriginX, origin.OriginY, origin.OriginZ);
        var animation = shape.Animations.FirstOrDefault(candidate =>
            string.Equals(candidate.Code, "opened", StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.Name, "opened", StringComparison.OrdinalIgnoreCase));
        var rootName = root.Name ?? "origin";
        var keyframe = animation?.KeyFrames?
            .OrderBy(frame => frame.Frame)
            .LastOrDefault(frame => frame.Elements != null
                && frame.Elements.ContainsKey(rootName));
        if (keyframe == null || !keyframe.Elements.TryGetValue(rootName, out var pose))
            return (0, 0, 0, origin.OriginX, origin.OriginY, origin.OriginZ);

        return (
            (float)(pose.RotationX ?? 0) * GameMath.DEG2RAD,
            (float)(pose.RotationY ?? 0) * GameMath.DEG2RAD,
            (float)(pose.RotationZ ?? 0) * GameMath.DEG2RAD,
            (float)((pose.OriginX ?? root.RotationOrigin?[0] ?? 8) / 16.0),
            (float)((pose.OriginY ?? root.RotationOrigin?[1] ?? 8) / 16.0),
            (float)((pose.OriginZ ?? root.RotationOrigin?[2] ?? 8) / 16.0));
    }

    private static CompositeShape[] ExpandAlternates(CompositeShape shape)
    {
        if (shape.BakedAlternates is { Length: > 0 } baked) return baked;
        if (shape.Alternates is not { Length: > 0 } alternates) return [shape];

        var root = shape.CloneWithoutAlternates();
        var result = new CompositeShape[alternates.Length + 1];
        result[0] = root;
        for (var i = 0; i < alternates.Length; i++)
        {
            var alternate = alternates[i].CloneWithoutAlternates();
            if (alternate.Base == null) alternate.Base = root.Base?.Clone();
            if (!alternate.QuantityElements.HasValue) alternate.QuantityElements = root.QuantityElements;
            alternate.SelectiveElements ??= root.SelectiveElements;
            alternate.IgnoreElements ??= root.IgnoreElements;
            result[i + 1] = alternate;
        }
        return result;
    }

    private static CompositeShape? ResolveShapeWildcard(IAssetManager assets, CompositeShape? composite)
    {
        if (composite?.Base?.Path.EndsWith('*') != true) return composite;
        var prefix = "shapes/" + composite.Base.Path[..^1];
        var location = assets.GetLocations(prefix, composite.Base.Domain)
            .Where(candidate => candidate.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && candidate.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => candidate.Path, StringComparer.Ordinal)
            .FirstOrDefault();
        if (location == null) return composite;
        var resolved = composite.Clone();
        resolved.Base = new AssetLocation(location.Domain, location.Path["shapes/".Length..^5]);
        return resolved;
    }

    private static void BakeComposite(
        CompositeShape composite,
        Block block,
        ICoreAPI api,
        Dictionary<AssetLocation, Shape?> shapeCache,
        List<ShapeFaceTemplate> output,
        System.Func<CompositeTexture?, int, string?, string?, int> resolveTile,
        float drawnHeight,
        ref int discriminator,
        IDictionary<string, CompositeTexture>? overrideTextures = null)
    {
        if (composite.Base == null || composite.Format != EnumShapeFormat.VintageStory) return;
        var firstFace = output.Count;
        if (!shapeCache.TryGetValue(composite.Base, out var shape))
        {
            var assetLocation = composite.Base.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
            shape = LoadShapeWithDomain(api, assetLocation);
            shapeCache[composite.Base] = shape;
        }
        if (shape?.Elements is { Length: > 0 })
        {
            var stack = new StackMatrix4(128);
            stack.PushIdentity();
            int? quantity = composite.QuantityElements is > 0 ? composite.QuantityElements : null;
            BakeElements(shape.Elements, shape, block, stack, output, resolveTile, ref discriminator, ref quantity,
                composite.SelectiveElements, composite.IgnoreElements, drawnHeight, overrideTextures);
            ApplyCompositeTransform(output, firstFace, composite);
        }

        if (composite.Overlays == null) return;
        foreach (var overlay in composite.Overlays)
        {
            if (overlay?.Base == null) continue;
            BakeComposite(overlay, block, api, shapeCache, output, resolveTile, drawnHeight, ref discriminator, overrideTextures);
        }
    }

    private static void BakeElements(
        ShapeElement[] elements,
        Shape shape,
        Block block,
        StackMatrix4 stack,
        List<ShapeFaceTemplate> output,
        System.Func<CompositeTexture?, int, string?, string?, int> resolveTile,
        ref int discriminator,
        ref int? quantity,
        string[]? selective,
        string[]? ignored,
        float drawnHeight,
        IDictionary<string, CompositeTexture>? overrideTextures = null)
    {
        foreach (var element in elements)
        {
            if (quantity.HasValue && quantity-- <= 0) break;
            if (!SelectiveMatch(element.Name, selective, out var childSelective)) continue;
            if (ignored != null && SelectiveMatch(element.Name, ignored, out var childIgnoredMatched)) continue;
            SelectiveMatch(element.Name, ignored, out var childIgnored);
            if (element.From is not { Length: 3 } || element.To is not { Length: 3 }) continue;

            stack.Push();
            var origin = element.RotationOrigin is { Length: 3 } ? element.RotationOrigin : [0d, 0d, 0d];
            var originY = origin[1] * (double)drawnHeight;
            if (element.RotationOrigin != null) stack.Translate(origin[0] / 16, originY / 16, origin[2] / 16);
            if (element.RotationX != 0) stack.Rotate(element.RotationX * Math.PI / 180, 1, 0, 0);
            if (element.RotationY != 0) stack.Rotate(element.RotationY * Math.PI / 180, 0, 1, 0);
            if (element.RotationZ != 0) stack.Rotate(element.RotationZ * Math.PI / 180, 0, 0, 1);
            if (element.ScaleX != 1 || element.ScaleY != 1 || element.ScaleZ != 1) stack.Scale(element.ScaleX, element.ScaleY, element.ScaleZ);
            stack.Translate((element.From[0] - origin[0]) / 16, (element.From[1] - originY) / 16, (element.From[2] - origin[2]) / 16);

            var sizeX = (float)(element.To[0] - element.From[0]) / 16f;
            var sizeY = (float)(element.To[1] - element.From[1]) / 16f * drawnHeight;
            var sizeZ = (float)(element.To[2] - element.From[2]) / 16f;
            var elementFaces = element.FacesResolved;
            // JSON shapes commonly use zero-thickness planes for leaves, flowers,
            // signs and overlays. The native tesselator rejects only a point.
            if ((sizeX != 0 || sizeY != 0 || sizeZ != 0) && elementFaces != null)
            {
                for (var facing = 0; facing < Math.Min(6, elementFaces.Length); facing++)
                {
                    var face = elementFaces[facing];
                    if (face?.Enabled != true || string.IsNullOrWhiteSpace(face.Texture)) continue;
                    var texture = ResolveShapeTexture(block, shape, face.Texture, overrideTextures);
                    var climate = ColorMapName(element.ClimateColorMap, block.ClimateColorMap);
                    var season = ColorMapName(element.SeasonColorMap, block.SeasonColorMap);
                    var tile = resolveTile(texture, discriminator++, climate, season);
                    var vertices = BuildFaceVertices(stack.Top, facing, sizeX, sizeY, sizeZ);
                    var uvs = BuildFaceUvs(shape, face, facing, sizeX, sizeY, sizeZ, drawnHeight);
                    output.Add(new ShapeFaceTemplate
                    {
                        Vertices = vertices,
                        Normal = FaceNormal(vertices),
                        Uvs = uvs,
                        Tile = tile,
                        TextureKey = face.Texture.TrimStart('#'),
                        BoundaryFace = DetectBoundary(vertices),
                        RenderPass = element.RenderPass
                    });
                }
            }

            if (element.Children is { Length: > 0 })
            {
                BakeElements(element.Children, shape, block, stack, output, resolveTile, ref discriminator, ref quantity,
                    childSelective ?? selective, childIgnored ?? ignored, drawnHeight, overrideTextures);
            }
            stack.Pop();
        }
    }

    private static float[] BuildFaceVertices(double[] matrix, int facing, float sizeX, float sizeY, float sizeZ)
    {
        var result = new float[12];
        var source = CubeMeshUtil.CubeVertices;
        var offset = facing * 12;
        for (var vertex = 0; vertex < 4; vertex++)
        {
            var x = sizeX * (source[offset++] + 1) / 2f;
            var y = sizeY * (source[offset++] + 1) / 2f;
            var z = sizeZ * (source[offset++] + 1) / 2f;
            result[vertex * 3] = (float)(matrix[0] * x + matrix[4] * y + matrix[8] * z + matrix[12]);
            result[vertex * 3 + 1] = (float)(matrix[1] * x + matrix[5] * y + matrix[9] * z + matrix[13]);
            result[vertex * 3 + 2] = (float)(matrix[2] * x + matrix[6] * y + matrix[10] * z + matrix[14]);
        }
        return result;
    }

    private static float[] BuildFaceUvs(Shape shape, ShapeElementFace face, int facing, float sizeX, float sizeY, float sizeZ, float drawnHeight)
    {
        var uv = face.Uv;
        float u0 = 0, v0 = 0, u1, v1;
        if (uv is { Length: 4 })
        {
            u0 = uv[0]; v1 = uv[3]; u1 = uv[2]; v0 = v1 + (uv[1] - v1) * drawnHeight;
        }
        else if (facing is 4 or 5) { u1 = sizeX * 16; v1 = sizeZ * 16; }
        else if (facing is 1 or 3) { u1 = sizeZ * 16; v1 = sizeY * 16; }
        else { u1 = sizeX * 16; v1 = sizeY * 16; }

        var textureCode = face.Texture.StartsWith('#') ? face.Texture[1..] : face.Texture;
        var textureSize = shape.TextureSizes != null && shape.TextureSizes.TryGetValue(textureCode, out var size) && size is { Length: >= 2 }
            ? size : [Math.Max(1, shape.TextureWidth), Math.Max(1, shape.TextureHeight)];
        var result = new float[8];
        // ShapeTesselator truncates the quarter-turn count, matching the
        // client's handling of non-integral face rotations.
        var rotation = ((int)(face.Rotation / 90f) % 4 + 4) % 4;
        for (var vertex = 0; vertex < 4; vertex++)
        {
            var uvIndex = 2 * ((rotation + vertex) % 4) + facing * 8;
            result[vertex * 2] = (u0 + (u1 - u0) * CubeMeshUtil.CubeUvCoords[uvIndex]) / textureSize[0];
            result[vertex * 2 + 1] = (v1 + (v0 - v1) * CubeMeshUtil.CubeUvCoords[uvIndex + 1]) / textureSize[1];
        }
        return result;
    }

    private static CompositeTexture? ResolveShapeTexture(Block block, Shape shape, string code,
        IDictionary<string, CompositeTexture>? overrideTextures = null)
    {
        if (code.StartsWith('#')) code = code[1..];

        // ShapeTextureSource keeps the shape's own map as the final fallback,
        // while dynamic block/type maps override it by key.  Resolve aliases
        // against the merged map so values such as "#aged" can point back to
        // a shape texture.  TexturesInventory is intentionally absent: it is
        // an item icon source and often contains a white background.
        var merged = new Dictionary<string, CompositeTexture>(StringComparer.OrdinalIgnoreCase);
        if (shape.Textures != null)
        {
            foreach (var pair in shape.Textures)
                if (pair.Value != null) merged[pair.Key] = new CompositeTexture(pair.Value);
        }
        if (block.Textures != null)
        {
            foreach (var pair in block.Textures)
                if (pair.Value != null) merged[pair.Key] = pair.Value;
        }
        if (overrideTextures != null)
        {
            foreach (var pair in overrideTextures)
                if (pair.Value != null) merged[pair.Key] = pair.Value;
        }

        CompositeTexture? shapeFallback = null;
        if (shape.Textures != null && shape.Textures.TryGetValue(code, out var location))
            shapeFallback = new CompositeTexture(location);

        // Dedicated servers can expose a shape without its parsed texture
        // table.  The owning 1.22.x block still carries the final
        // `texturesByType` keys; use those only when the shape has no local
        // binding, preserving the shape's real `top/agedstraw/bamboo-top`
        // bindings whenever they are available.
        if (shapeFallback == null && block.Textures != null)
        {
            var fallbackKeys = code.ToLowerInvariant() switch
            {
                "top" => new[] { "normal-top", "bamboo-top", "top" },
                "agedstraw" => new[] { "straw1", "agedstraw" },
                "bamboo-top" => new[] { "bamboo-top", "normal-top", "top" },
                "sides" => new[] { "normal-side", "sides", "side" },
                // VS Roofing binds its slope faces through the variant
                // key `material`; on a dedicated server the shape texture
                // table may be unavailable, so resolve the block-level
                // aliases used by roof.json before falling back to missing.
                "material" => new[] { "normal-top", "normal-side", "top", "side", "all" },
                "roof" => new[] { "normal-top", "normal-side", "top", "side", "all" },
                _ => Array.Empty<string>()
            };
            foreach (var key in fallbackKeys)
            {
                if (block.Textures.TryGetValue(key, out var candidate)
                    && ResolveTextureAlias(merged, candidate) is { } resolved)
                {
                    shapeFallback = resolved;
                    break;
                }
            }
        }

        CompositeTexture? selected = null;
        var explicitOverride = overrideTextures != null && overrideTextures.ContainsKey(code);
        if (explicitOverride)
        {
            selected = ResolveTextureAlias(merged, overrideTextures![code]);
        }
        else if (overrideTextures == null && block.Textures != null)
        {
            // Normal block tesselation uses TextureSource, which has an exact
            // key followed by its conventional `all` fallback. Dynamic
            // ShapeTextureSource does not perform this fallback.
            if (block.Textures.ContainsKey(code))
                selected = ResolveTextureAlias(merged, block.Textures[code]);
            else if (block.Textures.ContainsKey("all"))
                selected = ResolveTextureAlias(merged, block.Textures["all"]);
        }
        else if (overrideTextures != null && block.Textures != null && block.Textures.ContainsKey(code))
        {
            // Keep a block-level exact key available for modded dynamic
            // blocks whose serialized `blockTextures` was not exposed by the
            // server API, but never use its unrelated `all` key.
            selected = ResolveTextureAlias(merged, block.Textures[code]);
        }

        selected ??= shapeFallback;
        if (selected?.Base == null) return shapeFallback;

        var baseLocation = selected.Base.Path.Equals("inherit", StringComparison.OrdinalIgnoreCase)
            ? shapeFallback?.Base
            : selected.Base;
        if (baseLocation == null) return null;
        return new CompositeTexture
        {
            Base = baseLocation,
            Rotation = selected.Rotation,
            Alpha = selected.Alpha,
            BlendedOverlays = selected.BlendedOverlays?.Select(overlay => new BlendedOverlayTexture
            {
                Base = overlay.Base.Path.Equals("inherit", StringComparison.OrdinalIgnoreCase) ? baseLocation : overlay.Base,
                BlendMode = overlay.BlendMode
            }).ToArray()
        };
    }

    private static string? ColorMapName(string? elementValue, string? blockValue)
    {
        var value = string.IsNullOrWhiteSpace(elementValue) ? blockValue : elementValue;
        return value?.Equals("none", StringComparison.OrdinalIgnoreCase) == true ? null : value;
    }

    private static bool SelectiveMatch(string? needle, string[]? haystack, out string[]? children)
    {
        children = null;
        if (haystack == null) return true;
        needle ??= "";
        for (var index = 0; index < haystack.Length; index++)
        {
            var value = haystack[index];
            if (string.IsNullOrEmpty(value)) continue;
            if (value == needle) { children = []; return true; }
            if (value == "*" || value == needle + "/*" || value.EndsWith('*') && needle.StartsWith(value[..^1], StringComparison.Ordinal))
            {
                children = ["*"];
                return true;
            }
            if (value.IndexOf('/') != needle.Length || !value.StartsWith(needle, StringComparison.Ordinal)) continue;
            children = haystack.Where(entry => entry.IndexOf('/') == needle.Length && entry.StartsWith(needle, StringComparison.Ordinal))
                .Select(entry => entry[(needle.Length + 1)..]).ToArray();
            return true;
        }
        return false;
    }

    private static void ApplyCompositeTransform(List<ShapeFaceTemplate> faces, int firstFace, CompositeShape composite)
    {
        for (var faceIndex = firstFace; faceIndex < faces.Count; faceIndex++)
        {
            var vertices = faces[faceIndex].Vertices;
            for (var i = 0; i < 4; i++)
            {
                var point = new Vector3(vertices[i * 3], vertices[i * 3 + 1], vertices[i * 3 + 2]);
                if (composite.Scale != 1) point = new Vector3(.5f, 0, .5f) + (point - new Vector3(.5f, 0, .5f)) * composite.Scale;
                point = RotateAround(point, new Vector3(.5f), composite.rotateX, composite.rotateY, composite.rotateZ);
                point += new Vector3(composite.offsetX, composite.offsetY, composite.offsetZ);
                vertices[i * 3] = point.X; vertices[i * 3 + 1] = point.Y; vertices[i * 3 + 2] = point.Z;
            }
            faces[faceIndex] = new ShapeFaceTemplate
            {
                Vertices = vertices,
                Normal = FaceNormal(vertices),
                Uvs = faces[faceIndex].Uvs,
                Tile = faces[faceIndex].Tile,
                TextureKey = faces[faceIndex].TextureKey,
                BoundaryFace = DetectBoundary(vertices),
                RenderPass = faces[faceIndex].RenderPass
            };
        }
    }

    private static Vector3 RotateAround(Vector3 point, Vector3 origin, float degreesX, float degreesY, float degreesZ)
    {
        if (degreesX == 0 && degreesY == 0 && degreesZ == 0) return point;
        Span<float> matrix = stackalloc float[16];
        Mat4f.RotateXYZ(matrix, degreesX * GameMath.DEG2RAD, degreesY * GameMath.DEG2RAD, degreesZ * GameMath.DEG2RAD);
        point -= origin;
        return new Vector3(
            matrix[0] * point.X + matrix[4] * point.Y + matrix[8] * point.Z,
            matrix[1] * point.X + matrix[5] * point.Y + matrix[9] * point.Z,
            matrix[2] * point.X + matrix[6] * point.Y + matrix[10] * point.Z) + origin;
    }

    private static int DetectBoundary(float[] vertices)
    {
        const float epsilon = .0005f;
        bool Plane(int axis, float value) => Enumerable.Range(0, 4).All(i => Math.Abs(vertices[i * 3 + axis] - value) <= epsilon);
        if (Plane(0, 0)) return 0;
        if (Plane(0, 1)) return 1;
        if (Plane(1, 0)) return 2;
        if (Plane(1, 1)) return 3;
        if (Plane(2, 0)) return 4;
        if (Plane(2, 1)) return 5;
        return -1;
    }

    private static float[] FaceNormal(float[] vertices)
    {
        var ax = vertices[3] - vertices[0];
        var ay = vertices[4] - vertices[1];
        var az = vertices[5] - vertices[2];
        var bx = vertices[6] - vertices[0];
        var by = vertices[7] - vertices[1];
        var bz = vertices[8] - vertices[2];
        var nx = ay * bz - az * by;
        var ny = az * bx - ax * bz;
        var nz = ax * by - ay * bx;
        var length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        if (length < .000001f) return [0, 0, 0];
        return [nx / length, ny / length, nz / length];
    }

    private static CompositeTexture? SelectTextureSet(IDictionary<string, CompositeTexture>? textures, string face, bool inside = false)
    {
        if (textures == null || textures.Count == 0) return null;
        if (inside)
        {
            var insideKeys = face switch
            {
                "up" => new[] { "inside-up", "inside-top", "inside-verticals", "inside-all", "inside-side", "inside-sides", "inside-*" },
                "down" => new[] { "inside-down", "inside-bottom", "inside-verticals", "inside-all", "inside-side", "inside-sides", "inside-*" },
                "west" or "east" => new[] { $"inside-{face}", "inside-westeast", "inside-horizontals", "inside-horizontal", "inside-side", "inside-sides", "inside-all", "inside-*" },
                "north" or "south" => new[] { $"inside-{face}", "inside-northsouth", "inside-horizontals", "inside-horizontal", "inside-side", "inside-sides", "inside-all", "inside-*" },
                _ => new[] { $"inside-{face}", "inside-horizontals", "inside-horizontal", "inside-side", "inside-sides", "inside-all", "inside-*" }
            };
            foreach (var key in insideKeys)
            {
                if (textures.TryGetValue(key, out var texture) && ResolveTextureAlias(textures, texture) is { } resolved) return resolved;
            }
            return null;
        }
        var keys = face switch
        {
            // Roofing and several typed survival blocks use the names from
            // their `texturesByType` definitions rather than cube names.
            // These aliases mirror the 1.22.x TextureSource conventions and
            // are especially important for VoxelMaterial.FromBlock().
            "up" => new[] { "up", "top", "normal-top", "bamboo-top", "verticals", "all", "side", "sides", "*" },
            "down" => new[] { "down", "bottom", "normal-bottom", "verticals", "all", "side", "sides", "*" },
            "west" or "east" => new[] { face, "westeast", "horizontals", "horizontal", "normal-side", "bamboo-side", "agedstraw", "straw1", "side", "sides", "all", "*" },
            "north" or "south" => new[] { face, "northsouth", "horizontals", "horizontal", "normal-side", "bamboo-side", "agedstraw", "straw1", "side", "sides", "all", "*" },
            _ => new[] { face, "horizontals", "horizontal", "side", "sides", "all", "*" }
        };
        foreach (var key in keys)
        {
            if (textures.TryGetValue(key, out var texture) && ResolveTextureAlias(textures, texture) is { } resolved) return resolved;
        }
        // Particle is the tesselator's preferred named fallback.
        if (textures.TryGetValue("particle", out var particle)
            && ResolveTextureAlias(textures, particle) is { } particleResolved) return particleResolved;

        // VoxelMaterial.FromBlock uses the first registered texture as the
        // final per-face fallback when the tesselator cannot resolve the
        // requested face key.  Keep that behavior for microblock materials
        // whose definitions expose only a non-standard key (and for modded
        // blocks that rely on the same client fallback).
        foreach (var candidate in textures.Values)
        {
            if (ResolveTextureAlias(textures, candidate) is { } firstResolved) return firstResolved;
        }
        return null;
    }

    private static CompositeTexture? SelectShapeVoxelTexture(ICoreAPI api, Block block, string face, Dictionary<AssetLocation, IDictionary<string, CompositeTexture>?> cache)
    {
        var location = block.Shape?.Base;
        if (location == null) return null;
        if (!cache.TryGetValue(location, out var textures))
        {
            var assetLocation = location.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
            var source = LoadShapeWithDomain(api, assetLocation)?.Textures;
            textures = source?.ToDictionary(entry => entry.Key, entry => new CompositeTexture(entry.Value), StringComparer.OrdinalIgnoreCase);
            cache[location] = textures;
        }
        var selected = SelectExactTexture(textures, face);
        return selected ?? FirstTexture(textures);
    }

    private static CompositeTexture? SelectExactTexture(IDictionary<string, CompositeTexture>? textures, string key)
    {
        if (textures == null || textures.Count == 0 || !textures.TryGetValue(key, out var texture)) return null;
        return ResolveTextureAlias(textures, texture);
    }

    private static CompositeTexture? FirstTexture(IDictionary<string, CompositeTexture>? textures)
    {
        if (textures == null || textures.Count == 0) return null;
        foreach (var texture in textures.Values)
        {
            if (ResolveTextureAlias(textures, texture) is { } resolved) return resolved;
        }
        return null;
    }

    private static CompositeTexture? ResolveTextureAlias(IDictionary<string, CompositeTexture> textures, CompositeTexture? texture)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (texture?.Base?.Path is { Length: > 1 } path && path[0] == '#')
        {
            var key = path[1..];
            if (!visited.Add(key) || !textures.TryGetValue(key, out texture)) return null;
        }
        return texture?.Base == null ? null : texture;
    }

    private static string TextureKey(CompositeTexture? texture, SKColor tint)
    {
        if (texture?.Base == null) return "fallback:missing";
        var overlays = texture.BlendedOverlays == null ? "" : string.Join(',', texture.BlendedOverlays.Select(overlay => $"{overlay.BlendMode}:{overlay.Base}"));
        return $"{texture.Base}|{overlays}|{texture.Rotation}|{texture.Alpha}|{tint.Red:x2}{tint.Green:x2}{tint.Blue:x2}";
    }

    private static Dictionary<AssetLocation, TextureAssetSource> IndexTextureAssets(IAssetManager assets, string? configuredPath, out string[] externalRoots)
    {
        var result = new Dictionary<AssetLocation, TextureAssetSource>();
        externalRoots = DiscoverAssetRoots(configuredPath).ToArray();
        // API origins can include a packed/compiled copy of a texture. The
        // actual client installation is authoritative for world rendering,
        // so load it after API assets. DiscoverAssetRoots is ordered by
        // priority (configured path, VINTAGE_STORY, application base); walk
        // it backwards so the highest-priority root wins on collisions.
        foreach (var origin in assets.Origins)
        {
            foreach (var asset in origin.GetAssets(AssetCategory.textures, false))
            {
                var source = new TextureAssetSource(asset, null);
                AddTextureAssetAliases(result, asset.Location, source);
            }
        }
        foreach (var root in externalRoots.Reverse())
        {
            foreach (var domainDirectory in Directory.EnumerateDirectories(root))
            {
                var domain = Path.GetFileName(domainDirectory).ToLowerInvariant();
                var textureRoot = Path.Combine(domainDirectory, "textures");
                if (!Directory.Exists(textureRoot)) continue;
                foreach (var file in Directory.EnumerateFiles(textureRoot, "*.png", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(domainDirectory, file).Replace('\\', '/').ToLowerInvariant();
                    AddTextureAssetAliases(result, new AssetLocation(domain, relative), new TextureAssetSource(null, file));
                }
            }
        }
        return result;
    }

    private static void AddTextureAssetAliases(
        IDictionary<AssetLocation, TextureAssetSource> result,
        AssetLocation location,
        TextureAssetSource source)
    {
        // AssetLocation values exposed by IAssetManager differ between the
        // packed 1.22.x origins and filesystem assets: one uses
        // `block/foo`, another uses `textures/block/foo`. Both identify the
        // same client texture. Keep aliases at index time so the lookup does
        // not depend on which origin supplied the authoritative PNG.
        var path = location.Path.Replace('\\', '/').TrimStart('/');
        if (path.StartsWith("textures/", StringComparison.OrdinalIgnoreCase))
        {
            result[new AssetLocation(location.Domain, path)] = source;
            result[new AssetLocation(location.Domain, path[9..])] = source;
        }
        else
        {
            result[new AssetLocation(location.Domain, path)] = source;
            result[new AssetLocation(location.Domain, "textures/" + path)] = source;
        }
    }

    private static IEnumerable<string> DiscoverAssetRoots(string? configuredPath)
    {
        var candidates = new List<string?>
        {
            configuredPath,
            Environment.GetEnvironmentVariable("VINTAGE_STORY_CLIENT"),
            Environment.GetEnvironmentVariable("VINTAGE_STORY"),
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
            // The API assembly is loaded from the actual Vintage Story
            // installation even when this mod is loaded from a LauncherGo
            // profile. Its directory is therefore a reliable client-assets
            // anchor when no explicit path is configured.
            Path.GetDirectoryName(typeof(Block).Assembly.Location)
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var probe = Path.GetFullPath(candidate!);
            // A local ServerMap checkout commonly sits beside the client
            // checkout (E:\vintagestory\ServerMap and
            // E:\vintagestory\Vintagestory). Walk a few ancestors so this
            // layout is found without requiring a machine-specific config.
            for (var depth = 0; depth < 6 && !string.IsNullOrEmpty(probe); depth++)
            {
                foreach (var rootCandidate in new[]
                {
                    probe,
                    Path.Combine(probe, "assets"),
                    Path.Combine(probe, "Vintagestory", "assets")
                })
                {
                    var root = Path.GetFullPath(rootCandidate);
                    if (!Directory.Exists(root)) continue;
                    if (!Directory.EnumerateDirectories(root).Any(directory => Directory.Exists(Path.Combine(directory, "textures")))) continue;
                    if (seen.Add(root)) yield return root;
                }

                var parent = Directory.GetParent(probe)?.FullName;
                if (string.Equals(parent, probe, StringComparison.OrdinalIgnoreCase)) break;
                probe = parent ?? string.Empty;
            }
        }
    }

    private static bool TryBuildTextureCell(IReadOnlyDictionary<AssetLocation, TextureAssetSource> assets, CompositeTexture texture,
        string fallbackDomain, SKColor tint, bool selectiveTint, bool topSoilOverlay, bool topSoilTop, int face, out byte[] cell)
    {
        cell = [];
        var layers = new List<(AssetLocation Location, SKBlendMode BlendMode, bool Overlay)>();
        if (texture.Base != null) layers.Add((texture.Base, SKBlendMode.SrcOver, false));
        if (texture.BlendedOverlays != null)
        {
            foreach (var overlay in texture.BlendedOverlays)
            {
                if (overlay?.Base == null) continue;
                layers.Add((overlay.Base, ToSkiaBlendMode(overlay.BlendMode), true));
            }
        }
        if (layers.Count == 0) return false;

        // Match the client's atlas bake. Skia's premultiplied composition is
        // important for coverage textures; reading it as straight alpha can
        // expose the transparent texel matte as white pixels.
        using var composed = new SKBitmap(CellSize, CellSize, SKColorType.Rgba8888, SKAlphaType.Premul);
        composed.Erase(SKColors.Transparent);
        using var canvas = new SKCanvas(composed);
        var loaded = false;
        for (var layerIndex = 0; layerIndex < layers.Count; layerIndex++)
        {
            var layer = layers[layerIndex];
            var source = FindTexture(assets, layer.Location, fallbackDomain);
            if (source == null) continue;
            byte[]? data;
            if (source.FilePath != null)
            {
                data = File.ReadAllBytes(source.FilePath);
            }
            else
            {
                var asset = source.Asset;
                if (asset == null || (asset.Data == null && !asset.Origin.TryLoadAsset(asset))) continue;
                data = asset.Data;
            }
            if (data == null || data.Length == 0) continue;
            using var decoded = SKBitmap.Decode(data);
            if (decoded == null) continue;
            // TopSoil's second texture is a 2:1 client coverage atlas: the
            // left half is the translucent side strip and the right half is
            // the opaque top. The chunktopsoil shader selects the half based
            // on the interpolated face normal, so bake the selected half as
            // an independent square atlas tile.
            var sourceRect = new SKRect(0, 0, decoded.Width, decoded.Height);
            if (topSoilOverlay && decoded.Width >= decoded.Height * 2)
            {
                var half = decoded.Width / 2f;
                sourceRect = topSoilTop
                    ? new SKRect(half, 0, decoded.Width, decoded.Height)
                    : new SKRect(0, 0, half, decoded.Height);
            }
            var blendMode = layer.BlendMode;
            using var paint = new SKPaint { IsAntialias = false, FilterQuality = SKFilterQuality.None, BlendMode = blendMode };
            canvas.DrawBitmap(decoded, sourceRect, new SKRect(0, 0, CellSize, CellSize), paint);
            loaded = true;
        }
        if (!loaded) return false;
        canvas.Flush();

        cell = new byte[CellSize * CellSize * 4];
        var alphaScale = Math.Clamp(texture.Alpha, 0, 255) / 255f;
        for (var y = 0; y < CellSize; y++)
        for (var x = 0; x < CellSize; x++)
        {
            var (sourceX, sourceY) = RotatePixel(x, y, texture.Rotation);
            var color = composed.GetPixel(sourceX, sourceY);
            var offset = (y * CellSize + x) * 4;
            // Climate/season maps belong to foliage, grass and leaves.  The
            // TopSoil overlay is handled as one complete coverage texture;
            // never classify individual pixels by hue.
            var applyTint = !selectiveTint || IsVegetationPixel(color);
            var alpha = (byte)Math.Clamp((int)Math.Round(color.Alpha * alphaScale), 0, 255);
            cell[offset] = applyTint ? (byte)(color.Red * tint.Red / 255) : color.Red;
            cell[offset + 1] = applyTint ? (byte)(color.Green * tint.Green / 255) : color.Green;
            cell[offset + 2] = applyTint ? (byte)(color.Blue * tint.Blue / 255) : color.Blue;
            cell[offset + 3] = alpha;
        }
        return true;
    }

    private static (int X, int Y) RotatePixel(int x, int y, int rotation)
    {
        var normalized = ((rotation % 360) + 360) % 360;
        return normalized switch
        {
            // TextureAtlasManager delegates 90-degree transforms to
            // BitmapRef.GetPixelsTransformed(). Its destination pixel (x, y)
            // reads source (width - 1 - y, x); the previous mapping used the
            // inverse transform and mirrored every rotated directional face.
            90 => (CellSize - 1 - y, x),
            180 => (CellSize - 1 - x, CellSize - 1 - y),
            270 => (y, CellSize - 1 - x),
            _ => (x, y)
        };
    }

    private static SKBlendMode ToSkiaBlendMode(EnumColorBlendMode mode) => mode switch
    {
        EnumColorBlendMode.Darken => SKBlendMode.Darken,
        EnumColorBlendMode.Lighten => SKBlendMode.Lighten,
        EnumColorBlendMode.Multiply => SKBlendMode.Multiply,
        EnumColorBlendMode.Screen => SKBlendMode.Screen,
        EnumColorBlendMode.ColorDodge => SKBlendMode.ColorDodge,
        EnumColorBlendMode.ColorBurn => SKBlendMode.ColorBurn,
        EnumColorBlendMode.Overlay => SKBlendMode.Overlay,
        EnumColorBlendMode.OverlayCutout => SKBlendMode.DstOut,
        _ => SKBlendMode.SrcOver
    };

    private static bool IsVegetationTint(Block block, string? climateName, string? seasonName)
    {
        if (block.BlockMaterial is EnumBlockMaterial.Leaves or EnumBlockMaterial.Plant) return true;
        if (block.DrawType == EnumDrawType.TopSoil) return true;
        var names = $"{climateName}|{seasonName}";
        return names.Contains("plant", StringComparison.OrdinalIgnoreCase)
            || names.Contains("grass", StringComparison.OrdinalIgnoreCase)
            || names.Contains("foliage", StringComparison.OrdinalIgnoreCase)
            || names.Contains("needle", StringComparison.OrdinalIgnoreCase)
            || names.Contains("leaf", StringComparison.OrdinalIgnoreCase);
    }

    private static string InferSeasonMap(string code)
    {
        if (code.Contains("pine", StringComparison.OrdinalIgnoreCase)
            || code.Contains("redwood", StringComparison.OrdinalIgnoreCase)
            || code.Contains("larch", StringComparison.OrdinalIgnoreCase)) return "seasonalNeedles";
        if (code.Contains("birch", StringComparison.OrdinalIgnoreCase)) return "seasonalBirch";
        if (code.Contains("maple", StringComparison.OrdinalIgnoreCase)) return "seasonalMaple";
        if (code.Contains("oak", StringComparison.OrdinalIgnoreCase)) return "seasonalOak";
        if (code.Contains("walnut", StringComparison.OrdinalIgnoreCase)) return "seasonalWalnut";
        if (code.Contains("grass", StringComparison.OrdinalIgnoreCase)
            || code.Contains("soil", StringComparison.OrdinalIgnoreCase)
            || code.Contains("forestfloor", StringComparison.OrdinalIgnoreCase)) return "seasonalGrass";
        return "seasonalFoliage";
    }

    private static bool IsVegetationPixel(SKColor color)
    {
        if (color.Alpha < 8) return false;
        // Dirt and wood are red/brown dominant.  Green/olive foliage has a
        // noticeably stronger green channel than blue and is not strongly red.
        // Vanilla leaves/grass textures also use a grayscale mask and receive
        // their visible hue entirely from the climate/season color map, so a
        // neutral pixel must be treated as vegetation too.  This keeps those
        // masks from becoming the white/gray speckles seen in the 2D map.
        var neutral = Math.Abs(color.Red - color.Green) <= 4
            && Math.Abs(color.Green - color.Blue) <= 4;
        return neutral || (color.Green >= color.Blue + 10
            && color.Green >= color.Red - 18
            && color.Red < 165);
    }

    private static CompositeTexture? ResolveTextureVariables(CompositeTexture? texture, Block block)
    {
        if (texture?.Base == null) return texture;

        // RegistryObject.Variant is the client's resolved variant map. It is
        // more authoritative than the serialized attributes and also covers
        // non-type groups such as meta/cover/material. VariantStrict is kept
        // as a compatibility fallback for API versions where Variant has not
        // been initialized yet.
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddVariants(values, block.Variant);
        AddVariants(values, block.VariantStrict);
        AddVariant(values, "type", block.Attributes?["type"]?.AsString());
        AddVariant(values, "material", block.Attributes?["material"]?.AsString());
        // 1.22.x slanted roofing is a normal variant block. On a dedicated
        // server Variant may be unset even though the registry code is already
        // concrete (slantedroofing-agedthatch-east-free).
        var roofing = Regex.Match(block.Code?.Path ?? "", "^slantedroofing-(?<material>[^-]+)-(?<orientation>[^-]+)-(?<cover>[^-]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (roofing.Success)
        {
            AddVariant(values, "material", roofing.Groups["material"].Value);
            AddVariant(values, "orientation", roofing.Groups["orientation"].Value);
            AddVariant(values, "cover", roofing.Groups["cover"].Value);
        }
        if (!values.ContainsKey("type"))
        {
            var codePath = block.Code?.Path;
            var separator = codePath?.LastIndexOf('-') ?? -1;
            if (separator >= 0 && separator + 1 < codePath!.Length)
                AddVariant(values, "type", codePath[(separator + 1)..]);
        }
        AddVariant(values, "code", block.Code?.Path);
        if (!values.ContainsKey("material"))
            AddVariant(values, "material", block.BlockMaterial.ToString().ToLowerInvariant());

        string Replace(string path) => Regex.Replace(path, "\\{([^}/]+)\\}", match =>
            values.TryGetValue(match.Groups[1].Value, out var replacement) && !string.IsNullOrWhiteSpace(replacement)
                ? replacement
                : match.Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Server-side block definitions may contain partially populated
        // CompositeTexture values. Its API Clone() assumes every optional
        // nested field is initialized and crashes for those valid definitions.
        // The map baker only consumes these four fields, so copy them
        // defensively instead of cloning client-only texture state.
        var baseLocation = texture.Base.Clone();
        baseLocation.Path = Replace(baseLocation.Path);
        var overlays = texture.BlendedOverlays?
            .Where(overlay => overlay?.Base != null)
            .Select(overlay =>
            {
                var overlayBase = overlay.Base.Clone();
                overlayBase.Path = Replace(overlayBase.Path);
                return new BlendedOverlayTexture { Base = overlayBase, BlendMode = overlay.BlendMode };
            })
            .ToArray();

        return new CompositeTexture
        {
            Base = baseLocation,
            Rotation = texture.Rotation,
            Alpha = texture.Alpha,
            BlendedOverlays = overlays
        };
    }

    private static void AddVariants(Dictionary<string, string> target,
        IEnumerable<KeyValuePair<string, string>>? variants)
    {
        if (variants == null) return;
        foreach (var pair in variants)
            AddVariant(target, pair.Key, pair.Value);
    }

    private static void AddVariant(Dictionary<string, string> target, string? key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) return;
        target.TryAdd(key, value);
    }

    private static bool IsUnknownTexture(CompositeTexture texture)
    {
        static bool Unknown(AssetLocation? location)
        {
            if (location == null) return false;

            // 1.22.x exposes the vanilla placeholder through several
            // equivalent paths depending on whether it came from a shape,
            // a block definition, or a packed asset origin (for example
            // `unknown`, `textures/unknown`, and `block/basic/unknown`).
            // Do not reject every file named `unknown`: survival also ships a
            // real wall-carving variant at block/stone/wallcarving/unknown.
            var path = location.Path.Replace('\\', '/').Trim('/');
            if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
            var separator = path.LastIndexOf('/');
            var fileName = separator >= 0 ? path[(separator + 1)..] : path;
            if (!fileName.Equals("unknown", StringComparison.OrdinalIgnoreCase)) return false;
            if (location.Domain.Equals("game", StringComparison.OrdinalIgnoreCase)) return true;
            return path.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                || path.Equals("textures/unknown", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/block/basic/unknown", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/block/unknown", StringComparison.OrdinalIgnoreCase)
                || path.Equals("block/basic/unknown", StringComparison.OrdinalIgnoreCase)
                || path.Equals("block/unknown", StringComparison.OrdinalIgnoreCase);
        }
        return Unknown(texture.Base)
            || texture.BlendedOverlays?.Any(overlay => Unknown(overlay?.Base)) == true;
    }

    private static bool HasUnresolvedTextureVariables(CompositeTexture texture)
    {
        static bool Unresolved(AssetLocation? location) => location?.Path.Contains('{') == true;
        return Unresolved(texture.Base)
            || texture.BlendedOverlays?.Any(overlay => Unresolved(overlay?.Base)) == true;
    }

    private static TextureAssetSource? FindTexture(IReadOnlyDictionary<AssetLocation, TextureAssetSource> assets, AssetLocation texture, string fallbackDomain)
    {
        var path = texture.Path.Replace('\\', '/').TrimStart('/');
        if (path.StartsWith("textures/", StringComparison.OrdinalIgnoreCase)) path = path[9..];
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) path += ".png";
        // An unresolved variant is not a wildcard. Matching it against the
        // first file in a directory produced plausible-looking but incorrect
        // white/green materials (notably meta blocks). Resolve variables at
        // capture time; if one remains, expose the missing audit material.
        if (Regex.IsMatch(path, "\\{[^}/]+\\}", RegexOptions.CultureInvariant)) return null;
        var pattern = "textures/" + path;
        foreach (var domain in new[] { texture.Domain, fallbackDomain, "survival", "game", "creative" }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (assets.TryGetValue(new AssetLocation(domain, path), out var asset)) return asset;
            if (assets.TryGetValue(new AssetLocation(domain, "textures/" + path), out asset)) return asset;
            if (pattern.IndexOfAny(['*', '?']) < 0) continue;
            TextureAssetSource? best = null;
            string? bestPath = null;
            foreach (var candidate in assets)
            {
                if (!candidate.Key.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase) || !GlobMatch(candidate.Key.Path, pattern)) continue;
                if (bestPath != null && string.CompareOrdinal(candidate.Key.Path, bestPath) >= 0) continue;
                best = candidate.Value;
                bestPath = candidate.Key.Path;
            }
            if (best != null) return best;
        }
        return null;
    }

    private static bool GlobMatch(string value, string pattern)
    {
        var valueIndex = 0;
        var patternIndex = 0;
        var star = -1;
        var retry = 0;
        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length && (pattern[patternIndex] == '?' || char.ToLowerInvariant(pattern[patternIndex]) == char.ToLowerInvariant(value[valueIndex])))
            {
                valueIndex++;
                patternIndex++;
                continue;
            }
            if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                star = patternIndex++;
                retry = valueIndex;
                continue;
            }
            if (star < 0) return false;
            patternIndex = star + 1;
            valueIndex = ++retry;
        }
        while (patternIndex < pattern.Length && pattern[patternIndex] == '*') patternIndex++;
        return patternIndex == pattern.Length;
    }

    private static SKColor ResolveTint(
        IReadOnlyDictionary<AssetLocation, TextureAssetSource> assets,
        IReadOnlyDictionary<string, ColorMap> colorMaps,
        string? climateName,
        string? seasonName)
    {
        var tint = SKColors.White;
        if (!string.IsNullOrWhiteSpace(climateName) && colorMaps.TryGetValue(climateName, out var climate) && TrySampleColorMap(assets, climate, true, out var climateColor))
        {
            tint = climateColor;
        }
        else if (!string.IsNullOrWhiteSpace(climateName) && TrySampleNamedColorMap(assets, climateName, true, out climateColor))
        {
            // ColorMapResolved is not guaranteed to be populated server-side.
            tint = climateColor;
        }
        if (!string.IsNullOrWhiteSpace(seasonName) && colorMaps.TryGetValue(seasonName, out var season) && TrySampleColorMap(assets, season, false, out var seasonColor))
        {
            const float seasonWeight = .82f;
            tint = new SKColor(
                (byte)Math.Clamp((int)Math.Round(tint.Red + (seasonColor.Red - tint.Red) * seasonWeight), 0, 255),
                (byte)Math.Clamp((int)Math.Round(tint.Green + (seasonColor.Green - tint.Green) * seasonWeight), 0, 255),
                (byte)Math.Clamp((int)Math.Round(tint.Blue + (seasonColor.Blue - tint.Blue) * seasonWeight), 0, 255));
        }
        else if (!string.IsNullOrWhiteSpace(seasonName) && TrySampleNamedColorMap(assets, seasonName, false, out seasonColor))
        {
            const float seasonWeight = .82f;
            tint = new SKColor(
                (byte)Math.Clamp((int)Math.Round(tint.Red + (seasonColor.Red - tint.Red) * seasonWeight), 0, 255),
                (byte)Math.Clamp((int)Math.Round(tint.Green + (seasonColor.Green - tint.Green) * seasonWeight), 0, 255),
                (byte)Math.Clamp((int)Math.Round(tint.Blue + (seasonColor.Blue - tint.Blue) * seasonWeight), 0, 255));
        }
        return tint;
    }

    private static bool TrySampleNamedColorMap(
        IReadOnlyDictionary<AssetLocation, TextureAssetSource> assets,
        string name,
        bool climate,
        out SKColor color)
    {
        var path = name switch
        {
            "climatePlantTint" => "environment/planttint",
            "climateWaterTint" => "environment/watertint",
            "climateCrimsonKingTint" => "environment/crimsonkingmapletint",
            "climateDarkerPlantTint" => "environment/planttintdarker",
            "climateLighterPlantTint" => "environment/planttintlight",
            "seasonalNeedles" => "environment/seasons/needletint",
            "seasonalOak" => "environment/seasons/oaktint",
            "seasonalLarch" => "environment/seasons/larchtint",
            "seasonalBirch" => "environment/seasons/birchtint",
            "seasonalMaple" => "environment/seasons/mapletint",
            "seasonalWalnut" => "environment/seasons/walnuttint",
            "seasonalCrimsonKingMaple" => "environment/seasons/crimsonkingtint",
            "seasonalDarkerNeedles" => "environment/seasons/needletintdarker",
            "seasonalFoliage" => "environment/seasons/foliagetint",
            "seasonalGrass" => "environment/seasons/grasstint",
            "seasonalBlueberry" => "environment/seasons/blueberrytint",
            "seasonalCranberry" => "environment/seasons/cranberrytint",
            "seasonalOlive" => "environment/seasons/olivetint",
            "tropicalKapok" => "environment/seasons/tropictintkapok",
            _ => null
        };
        color = SKColors.White;
        if (path == null) return false;
        var domains = climate ? new[] { "game", "survival" } : new[] { "survival", "game" };
        foreach (var domain in domains)
        {
            var source = FindTexture(assets, new AssetLocation(domain, path), domain);
            if (source != null && TrySampleBitmap(source, climate, out color)) return true;
        }
        return false;
    }

    private static bool TrySampleColorMap(IReadOnlyDictionary<AssetLocation, TextureAssetSource> assets, ColorMap map, bool climate, out SKColor color)
    {
        color = SKColors.White;
        if (map.Texture?.Base == null) return false;
        var source = FindTexture(assets, map.Texture.Base, map.Texture.Base.Domain);
        if (source == null) return false;
        return TrySampleBitmap(source, climate, out color);
    }

    private static bool TrySampleBitmap(TextureAssetSource source, bool climate, out SKColor color)
    {
        color = SKColors.White;
        byte[]? data;
        if (source.FilePath != null) data = File.ReadAllBytes(source.FilePath);
        else
        {
            var asset = source.Asset;
            if (asset == null || (asset.Data == null && !asset.Origin.TryLoadAsset(asset))) return false;
            data = asset.Data;
        }
        if (data == null || data.Length == 0) return false;
        using var bitmap = SKBitmap.Decode(data);
        if (bitmap == null || bitmap.Width == 0 || bitmap.Height == 0) return false;
        var x = climate ? (int)Math.Round(138 / 255d * (bitmap.Width - 1)) : bitmap.Width / 2;
        var y = climate ? (int)Math.Round(180 / 255d * (bitmap.Height - 1)) : bitmap.Height / 2;
        color = bitmap.GetPixel(Math.Clamp(x, 0, bitmap.Width - 1), Math.Clamp(y, 0, bitmap.Height - 1));
        return true;
    }

    private static BlockGeometryKind ResolveGeometry(Block block)
    {
        if (block.Id == 0 || block.DrawType == EnumDrawType.Empty
            || block.RenderPass == EnumChunkRenderPass.Meta && !IsMicroBlockBlock(block)) return BlockGeometryKind.Empty;
        // Chiseled/micro blocks declare the generic basic cube as a JSON
        // shape, but that shape intentionally uses the game's `unknown`
        // texture. Their real geometry and materials live in the block
        // entity's voxel data, which MeshTile handles through the Cube path.
        // Route them before JSON classification so the placeholder can never
        // bypass that decoder.
        if (IsMicroBlockBlock(block)) return BlockGeometryKind.Cube;
        if (block.ForFluidsLayer || block.DrawType == EnumDrawType.Liquid || IsLiquidMaterial(block.BlockMaterial) || block.BlockMaterial == EnumBlockMaterial.Lava) return BlockGeometryKind.Fluid;
        if (block.DrawType is EnumDrawType.Cross or EnumDrawType.CrossAndSnowlayer or EnumDrawType.CrossAndSnowlayer_2 or EnumDrawType.CrossAndSnowlayer_3 or EnumDrawType.CrossAndSnowlayer_4) return BlockGeometryKind.Cross;
        if (block.DrawType is EnumDrawType.JSON or EnumDrawType.JSONAndWater or EnumDrawType.JSONAndSnowLayer) return BlockGeometryKind.Shape;
        return ResolveFallbackGeometry(block);
    }

    private static BlockGeometryKind ResolveFallbackGeometry(Block block)
    {
        var boxes = block.SelectionBoxes ?? block.CollisionBoxes;
        if (boxes is { Length: > 0 and <= 12 } && boxes.Any(box => !FullCube(box))) return BlockGeometryKind.Boxes;
        if (block.BlockMaterial == EnumBlockMaterial.Plant && (boxes == null || boxes.Length == 0)) return BlockGeometryKind.Cross;
        return BlockGeometryKind.Cube;
    }

    private static bool IsMicroBlockBlock(Block block)
    {
        var path = block.Code?.Path;
        return path != null
            && (path.StartsWith("chiseledblock", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("microblock", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsMetaBlock(Block block) =>
        block.Code?.Path.StartsWith("meta-", StringComparison.OrdinalIgnoreCase) == true;

    private static MeshMaterialLayer ResolveLayer(Block block, BlockGeometryKind geometry, int[] faceTiles, IReadOnlyList<byte[]> textureCells)
    {
        if (geometry == BlockGeometryKind.Fluid || block.RenderPass == EnumChunkRenderPass.Liquid) return MeshMaterialLayer.Liquid;
        if (block.RenderPass is EnumChunkRenderPass.Transparent or EnumChunkRenderPass.BlendNoCull) return MeshMaterialLayer.Translucent;
        if (block.RenderPass is EnumChunkRenderPass.OpaqueNoCull or EnumChunkRenderPass.OpaqueWaterPlant
            || geometry == BlockGeometryKind.Cross || block.BlockMaterial is EnumBlockMaterial.Leaves or EnumBlockMaterial.Plant)
            return MeshMaterialLayer.Cutout;

        foreach (var tile in faceTiles.Distinct())
        {
            if ((uint)tile >= (uint)textureCells.Count) continue;
            var pixels = textureCells[tile];
            for (var offset = 3; offset < pixels.Length; offset += 4)
            {
                if (pixels[offset] < 250) return MeshMaterialLayer.Cutout;
            }
        }
        return MeshMaterialLayer.Opaque;
    }

    private static MaterialBox[] ResolveBoxes(Block block, BlockGeometryKind geometry)
    {
        if (geometry != BlockGeometryKind.Boxes) return [];
        var boxes = block.SelectionBoxes ?? block.CollisionBoxes;
        if (boxes == null) return [];
        return boxes.Where(box => box != null && !box.Empty).Take(12).Select(box => new MaterialBox(
            Math.Clamp(box.MinX, 0, 1), Math.Clamp(box.MinY, 0, 1), Math.Clamp(box.MinZ, 0, 1),
            Math.Clamp(box.MaxX, 0, 1), Math.Clamp(box.MaxY, 0, 1), Math.Clamp(box.MaxZ, 0, 1))).ToArray();
    }

    private static bool FullCube(Cuboidf box) => Math.Abs(box.MinX) < .001f && Math.Abs(box.MinY) < .001f && Math.Abs(box.MinZ) < .001f && Math.Abs(box.MaxX - 1) < .001f && Math.Abs(box.MaxY - 1) < .001f && Math.Abs(box.MaxZ - 1) < .001f;

    private static bool IsLiquidMaterial(EnumBlockMaterial material) =>
        material == EnumBlockMaterial.Water || material.ToString().Equals("Liquid", StringComparison.Ordinal);

    private static string DefaultMapColorCode(EnumBlockMaterial material)
    {
        if (IsLiquidMaterial(material)) return "lake";
        return material switch
        {
            EnumBlockMaterial.Soil or EnumBlockMaterial.Ore or EnumBlockMaterial.Stone => "land",
            EnumBlockMaterial.Sand or EnumBlockMaterial.Gravel => "desert",
            EnumBlockMaterial.Leaves or EnumBlockMaterial.Wood => "forest",
            EnumBlockMaterial.Plant => "plant",
            EnumBlockMaterial.Snow or EnumBlockMaterial.Ice => "glacier",
            EnumBlockMaterial.Lava => "lava",
            _ => "land"
        };
    }

    private static byte[] MakeFallbackCell(byte r, byte g, byte b, byte a, bool checker)
    {
        var result = new byte[CellSize * CellSize * 4];
        for (var y = 0; y < CellSize; y++)
        for (var x = 0; x < CellSize; x++)
        {
            var shade = checker && ((x >> 2) + (y >> 2)) % 2 == 0 ? .82f : 1f;
            var offset = (y * CellSize + x) * 4;
            result[offset] = (byte)(r * shade); result[offset + 1] = (byte)(g * shade); result[offset + 2] = (byte)(b * shade); result[offset + 3] = a;
        }
        return result;
    }

    private static (byte R, byte G, byte B, byte A) Average(byte[] rgba)
    {
        long r = 0, g = 0, b = 0, a = 0, weight = 0;
        for (var offset = 0; offset < rgba.Length; offset += 4)
        {
            var alpha = rgba[offset + 3];
            if (alpha <= 5) continue;
            r += rgba[offset] * alpha; g += rgba[offset + 1] * alpha; b += rgba[offset + 2] * alpha; a += alpha; weight += alpha;
        }
        if (weight == 0) return (0, 0, 0, 0);
        return ((byte)(r / weight), (byte)(g / weight), (byte)(b / weight), (byte)(a / (rgba.Length / 4)));
    }

    private static int NextPowerOfTwo(int value)
    {
        var result = 1;
        while (result < value) result <<= 1;
        return result;
    }
}
