using System.Buffers.Binary;
using System.IO.Compression;
using ServerMap.Util;
using ServerMap.World;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.GameContent;
using Vintagestory.Server;

namespace ServerMap.Render;

public sealed class MeshTile
{
    private readonly HashSet<string> dynamicDiagnostics = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> microDiagnostics = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> microSourceDiagnostics = new(StringComparer.OrdinalIgnoreCase);
    // RGB is native colored block light; Sky is the saved sunlight level.
    // Keeping them separate lets the browser add directional sunlight and
    // receive shadows without multiplying a pre-lit texture a second time.
    private readonly record struct LightSample(float R, float G, float B, float Sky);

    private sealed class MeshBuffer
    {
        public readonly List<float> Vertices = [];
        public readonly List<byte> Lights = [];
        public readonly List<float> Uvs = [];
        public readonly List<int> Indices = [];
        public bool Empty => Vertices.Count == 0 || Indices.Count == 0;
    }

    // Client BlockEntityMicroBlock uses BlockFacing order
    // north, east, south, west, up, down. MeshTile stores faces as
    // west, east, down, up, north, south.
    private static readonly int[] ServerToClientMicroFace = [3, 1, 5, 4, 0, 2];
    private static readonly int[] ClientToServerMicroFace = [4, 1, 5, 0, 3, 2];
    private static readonly int[] ClientCubeUvCoords =
    [
        1, 0, 1, 1, 0, 1, 0, 0,
        1, 0, 1, 1, 0, 1, 0, 0,
        1, 0, 1, 1, 0, 1, 0, 0,
        1, 0, 1, 1, 0, 1, 0, 0,
        1, 0, 1, 1, 0, 1, 0, 0,
        1, 0, 1, 1, 0, 1, 0, 0
    ];

    private readonly record struct MicroUvBounds(float X0, float Y0, float Z0, float X1, float Y1, float Z1);
    private readonly record struct MicroCuboid(
        int X0, int Y0, int Z0, int X1, int Y1, int Z1,
        int MaterialId, BlockMaterialInfo Material, bool SkipRender);

    private sealed record DynamicShapeState(
        DynamicShapeMaterial? Material,
        ShapeTemplate[]? Shapes,
        bool Missing,
        float RotateX,
        float RotateY,
        float RotateZ,
        float ScaleY,
        float OffsetX,
        float OffsetY,
        float OffsetZ,
        bool DoorOpened = false,
        bool DoorInvertHandles = false,
        float DoorOpenedRotateX = 0,
        float DoorOpenedRotateY = 0,
        float DoorOpenedRotateZ = 0,
        float DoorOpenedOriginX = .5f,
        float DoorOpenedOriginY = .5f,
        float DoorOpenedOriginZ = .5f);

    private readonly WorldDatabaseReader reader;
    private readonly string root;
    private readonly int mapSizeY;
    private readonly MaterialCatalog materials;
    private readonly int scanAboveTerrain;
    private readonly DoorStateCache doorStates;

    // MeshTile uses its own face order so it can keep the captured material
    // arrays compact: west, east, down, up, north, south.  The bit values in
    // Block.EmitSideAo remain in the engine's N/E/S/W/U/D order and are
    // translated by EngineFaceBit below.
    private static readonly (int X, int Y, int Z)[] FaceNormals =
    [
        (-1, 0, 0), (1, 0, 0), (0, -1, 0),
        (0, 1, 0), (0, 0, -1), (0, 0, 1)
    ];
    private static readonly int[] OppositeFace = [1, 0, 3, 2, 5, 4];

    public MeshTile(WorldDatabaseReader reader, string root, int mapSizeY, MaterialCatalog materials, int scanAboveTerrain,
        DoorStateCache doorStates)
    {
        this.reader = reader;
        this.root = root;
        this.mapSizeY = mapSizeY;
        this.materials = materials;
        this.scanAboveTerrain = scanAboveTerrain;
        this.doorStates = doorStates;
    }

    public bool Render(ChunkKey tile, int tileSize, int lod, bool surfaceOnly, int minY)
    {
        // ServerMap exports the real voxel grid.  Coarser values skip source
        // blocks and stretch the survivors over multiple cells, which loses
        // geometry and cannot be repaired by browser-side filtering.
        lod = 1;
        MeshBuffer[] layers = [new(), new(), new(), new()];
        var minWorldX = tile.X * tileSize;
        var minWorldZ = tile.Z * tileSize;
        var maxWorldX = (tile.X + 1) * tileSize - lod;
        var maxWorldZ = (tile.Z + 1) * tileSize - lod;
        var tileMinChunkX = FloorDiv(minWorldX, 32);
        var tileMaxChunkX = FloorDiv(maxWorldX, 32);
        var tileMinChunkZ = FloorDiv(minWorldZ, 32);
        var tileMaxChunkZ = FloorDiv(maxWorldZ, 32);
        var minChunkX = FloorDiv(minWorldX - lod, 32);
        var maxChunkX = FloorDiv(maxWorldX + lod, 32);
        var minChunkZ = FloorDiv(minWorldZ - lod, 32);
        var maxChunkZ = FloorDiv(maxWorldZ + lod, 32);
        var mapChunks = new Dictionary<(int X, int Z), ServerMapChunk?>();
        var snapshotMinY = mapSizeY - 1;
        var snapshotMaxY = 0;

        for (var cx = minChunkX; cx <= maxChunkX; cx++)
        for (var cz = minChunkZ; cz <= maxChunkZ; cz++)
        {
            var map = reader.GetMapChunk(new ChunkKey(cx, 0, cz));
            mapChunks[(cx, cz)] = map;
            if (map == null) continue;
            // Never replace a complete tile with geometry from a column the
            // engine is still generating.  The extra mapchunk border is only
            // consulted for face culling, so only an incomplete interior
            // column blocks this tile.
            if (cx >= tileMinChunkX && cx <= tileMaxChunkX
                && cz >= tileMinChunkZ && cz <= tileMaxChunkZ
                && map.CurrentIncompletePass < EnumWorldGenPass.Done) return false;
            var terrainMin = map.WorldGenTerrainHeightMap?.Min(value => (int)value) ?? 0;
            var terrainMax = map.WorldGenTerrainHeightMap?.Max(value => (int)value) ?? 0;
            var rainMax = map.RainHeightMap?.Max(value => (int)value) ?? terrainMax;
            snapshotMinY = Math.Min(snapshotMinY, surfaceOnly ? Math.Max(minY, terrainMin - 2) : minY);
            // A non-surface render is a complete voxel snapshot.  Limiting it
            // to terrain + ScanAboveTerrain silently drops towers, bridges and
            // floating structures above the height map.
            snapshotMaxY = Math.Max(snapshotMaxY, surfaceOnly
                ? Math.Min(mapSizeY - 1, Math.Max(terrainMax, rainMax) + scanAboveTerrain)
                : mapSizeY - 1);
        }

        // A region can contain unexplored mapchunk coordinates at its edges.
        // Do not ask the server to generate those columns: an empty tile is
        // not useful and the peek callback can block a render worker for 45s.
        if (!mapChunks.Values.Any(map => map != null)) return false;

        snapshotMinY = Math.Clamp(snapshotMinY, 0, mapSizeY - 1);
        snapshotMaxY = Math.Clamp(Math.Max(snapshotMinY, snapshotMaxY), 0, mapSizeY - 1);
        var minChunkY = snapshotMinY >> 5;
        var maxChunkY = snapshotMaxY >> 5;
        var chunks = new Dictionary<(int X, int Y, int Z), ServerChunk?>();
        for (var cx = minChunkX; cx <= maxChunkX; cx++)
        for (var cz = minChunkZ; cz <= maxChunkZ; cz++)
        {
            // Chunk rows are sparse in the save database.  Only load rows
            // that actually exist; probing every possible Y slice made a
            // single mesh tile perform hundreds of useless SQLite queries.
            foreach (var cy in reader.ChunkYs(cx, cz))
            {
                if (cy < minChunkY || cy > maxChunkY) continue;
                // Rendering is a snapshot of saved world data.  Missing rows
                // are treated as empty until the save event queues the tile.
                chunks[(cx, cy, cz)] = reader.LoadChunk(new ChunkKey(cx, cy, cz));
            }
        }

        try
        {
            for (var wx = minWorldX; wx < (tile.X + 1) * tileSize; wx += lod)
            for (var wz = minWorldZ; wz < (tile.Z + 1) * tileSize; wz += lod)
            {
                var cx = FloorDiv(wx, 32); var lx = wx - cx * 32;
                var cz = FloorDiv(wz, 32); var lz = wz - cz * 32;
                mapChunks.TryGetValue((cx, cz), out var map);
                if (map == null) continue;
                var columnIndex = lz * 32 + lx;
                var terrain = Math.Clamp(map.WorldGenTerrainHeightMap?[columnIndex] ?? 0, 0, mapSizeY - 1);
                var rain = Math.Clamp(map.RainHeightMap?[columnIndex] ?? terrain, 0, mapSizeY - 1);
                var floor = surfaceOnly ? Math.Max(minY, terrain - 2) : minY;
                var ceiling = surfaceOnly
                    ? Math.Min(mapSizeY - 1, Math.Max(terrain, rain) + scanAboveTerrain)
                    : mapSizeY - 1;
                if (ceiling < floor) continue;

                for (var y = floor; y <= ceiling; y++)
                for (var sourceLayer = 0; sourceLayer < 2; sourceLayer++)
                {
                    var id = sourceLayer == 0 ? Read(chunks, cx, cz, lx, y, lz) : ReadFluid(chunks, cx, cz, lx, y, lz);
                    if (id == 0) continue;
                    var info = materials.Get(id);
                    if (info.Geometry == BlockGeometryKind.Empty) continue;
                    var mesh = layers[(int)info.Layer];
                    var topSoilOverlayMesh = info.HasTopSoilOverlay
                        ? layers[(int)MeshMaterialLayer.Cutout]
                        : null;
                    var micro = info.IsMicroBlock ? ResolveMicroBlockEntity(chunks, wx, y, wz) : null;

                    if (info.Geometry == BlockGeometryKind.Cross)
                    {
                        AddCross(mesh, wx, y, wz, lod, info, SampleBlockLight(chunks, wx, y, wz, lod));
                        continue;
                    }

                    var west = Neighbor(chunks, wx - lod, y, wz, sourceLayer);
                    var east = Neighbor(chunks, wx + lod, y, wz, sourceLayer);
                    var bottom = Neighbor(chunks, wx, y - 1, wz, sourceLayer);
                    var top = Neighbor(chunks, wx, y + 1, wz, sourceLayer);
                    var north = Neighbor(chunks, wx, y, wz - lod, sourceLayer);
                    var south = Neighbor(chunks, wx, y, wz + lod, sourceLayer);

                    // ShapeFromAttributes blocks (clutter, ruins and several
                    // survival props) keep their actual mesh in a block
                    // entity. Resolve that type before the generic cube path;
                    // the static Block.Shape is only a placeholder.
                    var dynamicShape = ResolveDynamicShapeEntity(chunks, wx, y, wz, info);
                    if (dynamicShape != null)
                    {
                        if (dynamicShape.Missing || dynamicShape.Shapes is not { Length: > 0 } shapes)
                        {
                            // A dynamic entity is not a normal block cube. If
                            // its type/variant/shape cannot be captured, keep
                            // the failure visible instead of rendering the
                            // placeholder block and hiding the missing asset.
                            AddCuboid(mesh, wx, y, wz, wx + lod, y + 1, wz + lod,
                                materials.MissingMaterial, west, east, bottom, top,
                                north, south, chunks, wx, y, wz, lod);
                        }
                        else
                        {
                            AddShape(mesh, wx, y, wz, lod, info,
                                [west, east, bottom, top, north, south], chunks,
                                SampleBlockLight(chunks, wx, y, wz, lod),
                                shapeOverride: shapes, dynamicState: dynamicShape, useRandomVariant: false,
                                layerMeshes: layers);
                        }
                        continue;
                    }

                    // Support beams are generated from BEBehaviorSupportBeam's
                    // persisted start/end points. The registry shape is only
                    // the one-block preview and cannot represent diagonal,
                    // vertical or chained beams, so render the entity mesh
                    // before falling back to the static block shape.
                    if (TryRenderSupportBeams(layers, info, chunks, wx, y, wz)) continue;

                    if (info.Geometry == BlockGeometryKind.Fluid)
                    {
                        AddFluid(mesh, chunks, wx, y, wz, lod, info, west, east, bottom, top, north, south);
                    }
                    else if (info.IsMicroBlock)
                    {
                        // The chiseled/micro block JSON only describes a
                        // generic cube. Its saved entity contains the actual
                        // voxel cuboids and the material index for every
                        // cuboid, so use that data instead of rendering the
                        // outer placeholder shape.
                        if (micro != null && AddMicroBlock(mesh, micro, wx, y, wz, lod,
                                [west, east, bottom, top, north, south], chunks, topSoilOverlayMesh, layers)) continue;

                        // Old or partially written saves may not have an
                        // entity payload. Keep the failure visible with the
                        // dedicated black audit material; never substitute a
                        // real terrain block and hide the missing payload.
                        var fallback = materials.MissingMaterial;
                        AddCuboid(mesh, wx, y, wz, wx + lod, y + 1, wz + lod, fallback,
                            west, east, bottom, top, north, south, chunks, wx, y, wz, lod);
                    }
                    else if (info.Geometry == BlockGeometryKind.Shape)
                    {
                        var doorState = ResolveDoorState(chunks, wx, y, wz, info);
                        AddShape(mesh, wx, y, wz, lod, info, [west, east, bottom, top, north, south], chunks,
                            SampleBlockLight(chunks, wx, y, wz, lod),
                            dynamicState: doorState, layerMeshes: layers);
                    }
                    else if (info.Geometry == BlockGeometryKind.Boxes && info.Boxes.Length > 0)
                    {
                        foreach (var box in info.Boxes)
                        {
                            AddCuboid(mesh, wx + box.X1 * lod, y + box.Y1, wz + box.Z1 * lod,
                                wx + box.X2 * lod, y + box.Y2, wz + box.Z2 * lod, info,
                                box.X1 <= .001f ? west : 0, box.X2 >= .999f ? east : 0,
                                box.Y1 <= .001f ? bottom : 0, box.Y2 >= .999f ? top : 0,
                                box.Z1 <= .001f ? north : 0, box.Z2 >= .999f ? south : 0,
                                chunks, wx, y, wz, lod,
                                overlayMesh: topSoilOverlayMesh, overlayFaceTiles: info.TopSoilOverlayTiles);
                        }
                    }
                    else
                    {
                        AddCuboid(mesh, wx, y, wz, wx + lod, y + 1, wz + lod, info,
                            west, east, bottom, top, north, south, chunks, wx, y, wz, lod,
                            overlayMesh: topSoilOverlayMesh, overlayFaceTiles: info.TopSoilOverlayTiles);
                    }
                }
            }

            // A partially available column can legitimately produce no
            // geometry (for example while its saved chunk rows are still
            // missing). Do not replace a previously valid tile with an empty
            // SMESH file; on the first render simply leave the tile absent so
            // the web client keeps its existing neighbours and retries later.
            if (layers.All(layer => layer.Empty)) return false;

            var path = Path.Combine(root, "3d", lod.ToString(), $"{tile.X}_{tile.Z}.smesh");
            AtomicFile.Replace(path, temp =>
            {
                using var stream = File.Create(temp);
                stream.Write("SMESH6"u8);
                using var compressed = new GZipStream(stream, CompressionLevel.SmallestSize, leaveOpen: true);
                compressed.WriteByte((byte)layers.Length);
                for (var layer = 0; layer < layers.Length; layer++)
                {
                    compressed.WriteByte((byte)layer);
                    WriteLayer(compressed, layers[layer]);
                }
            });
            return true;
        }
        finally
        {
            foreach (var chunk in chunks.Values) if (chunk != null) reader.ReleaseChunk(chunk);
            reader.ClearTransientColumns();
        }
    }

    private int Read(IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks, int cx, int cz, int x, int y, int z)
    {
        if (y < 0 || y >= mapSizeY) return 0;
        if (x < 0) { cx--; x += 32; } else if (x >= 32) { cx++; x -= 32; }
        if (z < 0) { cz--; z += 32; } else if (z >= 32) { cz++; z -= 32; }
        chunks.TryGetValue((cx, y >> 5, cz), out var chunk);
        // ChunkData's indexer falls back to the fluid layer when no solid is
        // present. This renderer visits both layers explicitly, so using the
        // indexer emitted every exposed liquid twice.
        return chunk == null ? 0 : chunk.Data.GetBlockId(x + z * 32 + (y & 31) * 1024, 1);
    }

    private int ReadFluid(IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks, int cx, int cz, int x, int y, int z)
    {
        if (y < 0 || y >= mapSizeY) return 0;
        if (x < 0) { cx--; x += 32; } else if (x >= 32) { cx++; x -= 32; }
        if (z < 0) { cz--; z += 32; } else if (z >= 32) { cz++; z -= 32; }
        chunks.TryGetValue((cx, y >> 5, cz), out var chunk);
        return chunk == null ? 0 : chunk.Data.GetFluid(x + z * 32 + (y & 31) * 1024);
    }

    private int ReadWorld(IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks, int x, int y, int z)
    {
        var cx = FloorDiv(x, 32); var cz = FloorDiv(z, 32);
        return Read(chunks, cx, cz, x - cx * 32, y, z - cz * 32);
    }

    private int ReadFluidWorld(IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks, int x, int y, int z)
    {
        var cx = FloorDiv(x, 32); var cz = FloorDiv(z, 32);
        return ReadFluid(chunks, cx, cz, x - cx * 32, y, z - cz * 32);
    }

    private int Neighbor(IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks, int x, int y, int z, int sourceLayer)
    {
        var solid = ReadWorld(chunks, x, y, z);
        if (sourceLayer == 0) return solid;
        var fluid = ReadFluidWorld(chunks, x, y, z);
        return fluid != 0 ? fluid : solid;
    }

    private BlockEntityMicroBlock? ResolveMicroBlockEntity(
        IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks,
        int x, int y, int z)
    {
        if (y < 0 || y >= mapSizeY) return null;

        var cx = FloorDiv(x, 32);
        var cz = FloorDiv(z, 32);
        if (!chunks.TryGetValue((cx, y >> 5, cz), out var chunk) || chunk == null) return null;

        var position = new BlockPos(x, y, z);
        return chunk.BlockEntities.TryGetValue(position, out var entity)
            ? entity as BlockEntityMicroBlock
            : null;
    }

    private DynamicShapeState? ResolveDynamicShapeEntity(
        IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks,
        int x, int y, int z, BlockMaterialInfo info)
    {
        if (!info.IsDynamicShape) return null;
        if (y < 0 || y >= mapSizeY) return null;
        var cx = FloorDiv(x, 32);
        var cz = FloorDiv(z, 32);
        if (!chunks.TryGetValue((cx, y >> 5, cz), out var chunk) || chunk == null)
            return new DynamicShapeState(null, null, true, 0, 0, 0, 1, 0, 0, 0);
        if (!chunk.BlockEntities.TryGetValue(new BlockPos(x, y, z), out var entity))
            return new DynamicShapeState(null, null, true, 0, 0, 0, 1, 0, 0, 0);

        // BlockCrate is meshed from its BlockEntity state on the 1.22.x
        // client.  Do this before the generic ShapeFromAttributes behavior:
        // the registry block's shape is only a placeholder and contains the
        // wrong (baldcypress) default texture for every crate type.
        if (entity is BlockEntityCrate crate)
        {
            var crateType = string.IsNullOrWhiteSpace(crate.type) ? "wood-aged" : crate.type;
            var lidState = string.Equals(crate.LidState, "opened", StringComparison.OrdinalIgnoreCase) ? "opened" : "closed";
            var key = crateType + "-" + lidState;
            var crateMaterial = materials.GetDynamicShape(info.Id, key);
            if (crateMaterial == null)
            {
                DiagnoseDynamic(info, x, y, z, $"crate type '{crateType}' state '{lidState}' not captured");
                return new DynamicShapeState(null, null, true, 0, 0, 0, 1, 0, 0, 0);
            }
            if (!crateMaterial.TryGetShapes(null, out var crateShapes) || crateShapes.Length == 0)
                return new DynamicShapeState(crateMaterial, null, true, 0, 0, 0, 1, 0, 0, 0);
            // BECrate stores MeshAngle in radians and rotates the completed
            // mesh around the block center after tesselation.
            return new DynamicShapeState(crateMaterial, crateShapes, false,
                0, crate.MeshAngle, 0, 1, 0, 0, 0);
        }
        var behavior = entity.GetBehavior<BEBehaviorShapeFromAttributes>();
        // Saved chunks normally hydrate BEBehaviorShapeFromAttributes before
        // reaching this renderer.  Some old/generated chunks only retain the
        // block-entity tree, however, so read the same persisted keys as a
        // fallback instead of treating a valid clutter object as missing.
        TreeAttribute? persisted = null;
        if (behavior == null || string.IsNullOrWhiteSpace(behavior.Type))
        {
            persisted = new TreeAttribute();
            try { entity.ToTreeAttributes(persisted); }
            catch { persisted = null; }
        }
        var type = !string.IsNullOrWhiteSpace(behavior?.Type)
            ? behavior.Type
            : persisted?.GetString("type", null);
        // BlockClutter's 1.22.x BEBehaviorShapeFromAttributes applies the
        // same remap used by BlockClutter.LoadTypes before storing Type. A
        // few imported/older entities retain only blockName, so preserve that
        // official value as a compatibility fallback without inventing a
        // material when both fields are absent.
        type = RemapDynamicType(type);
        if (string.IsNullOrWhiteSpace(type))
            type = RemapDynamicType(persisted?.GetString("blockName", null));
        var overrideTextureCode = behavior?.overrideTextureCode
            ?? persisted?.GetString("overrideTextureCode", null);
        var rotateX = behavior?.rotateX ?? persisted?.GetFloat("rotateX", 0f) ?? 0f;
        var rotateY = behavior?.rotateY ?? persisted?.GetFloat("meshAngle", 0f) ?? 0f;
        var rotateZ = behavior?.rotateZ ?? persisted?.GetFloat("rotateZ", 0f) ?? 0f;
        var offsetX = behavior?.offsetX ?? persisted?.GetFloat("offsetX", 0f) ?? 0f;
        var offsetY = behavior?.offsetY ?? persisted?.GetFloat("offsetY", 0f) ?? 0f;
        var offsetZ = behavior?.offsetZ ?? persisted?.GetFloat("offsetZ", 0f) ?? 0f;
        if (string.IsNullOrWhiteSpace(type))
        {
            DiagnoseDynamic(info, x, y, z, "missing type/blockName");
            return new DynamicShapeState(null, null, true, 0, 0, 0, 1, offsetX, offsetY, offsetZ);
        }
        var baseMaterial = materials.GetDynamicShape(info.Id, type);
        if (baseMaterial == null)
        {
            DiagnoseDynamic(info, x, y, z, $"type '{type}' not captured");
            return new DynamicShapeState(null, null, true, 0, 0, 0, 1, offsetX, offsetY, offsetZ);
        }

        var material = materials.GetDynamicShape(info.Id, type, overrideTextureCode);
        var shapes = material?.TryGetShapes(overrideTextureCode, out var selected) == true ? selected : null;
        if (shapes is not { Length: > 0 })
        {
            DiagnoseDynamic(info, x, y, z, $"type '{type}' has no shape faces");
            return new DynamicShapeState(baseMaterial, null, true,
                0, 0, 0, 1,
                offsetX, offsetY, offsetZ);
        }
        var selectedMaterial = material ?? baseMaterial;

        // ClutterTypeProps.Rotation is stored in degrees; the block entity's
        // wrench rotation is stored in radians by the game.
        return new DynamicShapeState(
            selectedMaterial,
            shapes,
            false,
            selectedMaterial.TypeRotationX * GameMath.DEG2RAD + rotateX,
            selectedMaterial.TypeRotationY * GameMath.DEG2RAD + rotateY,
            selectedMaterial.TypeRotationZ * GameMath.DEG2RAD + rotateZ,
            selectedMaterial.RandomizeYSize
                ? .98f + GameMath.MurmurHash3Mod(x, y, z, 1000) / 1000f * .04f
                : 1f,
            offsetX, offsetY, offsetZ);
    }

    private DynamicShapeState? ResolveDoorState(
        IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks,
        int x, int y, int z, BlockMaterialInfo info)
    {
        if (y < 0 || y >= mapSizeY) return null;
        var entityPosition = new BlockPos(x, y, z);
        if (doorStates.TryGet(x, y, z, out var captured))
            return CreateDoorState(captured, info);

        var cx = FloorDiv(x, 32);
        var cz = FloorDiv(z, 32);
        if (!chunks.TryGetValue((cx, y >> 5, cz), out var chunk) || chunk == null) return null;

        BEBehaviorDoor? door = null;
        if (chunk.BlockEntities.TryGetValue(entityPosition, out var entity))
            door = entity.GetBehavior<BEBehaviorDoor>();

        // Door upper halves and width-expanded parts are BlockMultiblock
        // placeholders. Vanilla resolves these back to the controller BE.
        if (door == null)
        {
            if (info.SourceBlock is IMultiblockOffset multiblock)
            {
                var rootPosition = entityPosition.Copy();
                multiblock.GetControlBlockPos(rootPosition);
                if (doorStates.TryGet(rootPosition.X, rootPosition.Y, rootPosition.Z, out captured))
                    return CreateDoorState(captured, info);
                var rootCx = FloorDiv(rootPosition.X, 32);
                var rootCz = FloorDiv(rootPosition.Z, 32);
                if (chunks.TryGetValue((rootCx, rootPosition.Y >> 5, rootCz), out var rootChunk)
                    && rootChunk?.BlockEntities.TryGetValue(rootPosition, out var rootEntity) == true)
                {
                    door = rootEntity.GetBehavior<BEBehaviorDoor>();
                }
            }
        }

        if (door == null) return null;
        return CreateDoorState(new DoorStateCache.State(door.RotateYRad, door.Opened, door.InvertHandles), info);
    }

    private static DynamicShapeState? CreateDoorState(DoorStateCache.State door, BlockMaterialInfo info)
    {
        var shape = info.Shapes.FirstOrDefault();
        if (shape == null) return null;
        return new DynamicShapeState(
            null, info.Shapes, false,
            0, door.RotateYRad, 0, 1, 0, 0, 0,
            door.Opened, door.InvertHandles,
            shape.OpenedRotateX, shape.OpenedRotateY, shape.OpenedRotateZ,
            shape.OpenedOriginX, shape.OpenedOriginY, shape.OpenedOriginZ);
    }

    private bool TryRenderSupportBeams(MeshBuffer[] layerMeshes, BlockMaterialInfo blockInfo,
        IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks, int x, int y, int z)
    {
        if (!blockInfo.Code.Contains("supportbeam", StringComparison.OrdinalIgnoreCase)) return false;
        var cx = FloorDiv(x, 32);
        var cz = FloorDiv(z, 32);
        if (!chunks.TryGetValue((cx, y >> 5, cz), out var chunk) || chunk == null
            || !chunk.BlockEntities.TryGetValue(new BlockPos(x, y, z), out var entity)) return false;

        var behavior = entity.GetBehavior<BEBehaviorSupportBeam>();
        if (behavior?.Beams is not { Length: > 0 } beams) return false;

        var emitted = false;
        foreach (var beam in beams)
        {
            var material = beam.BlockId > 0 && beam.BlockId < materials.BlockCount
                ? materials.Get(beam.BlockId)
                : blockInfo;
            if (material.Id <= 0 || material.Geometry == BlockGeometryKind.Empty) continue;
            var target = layerMeshes[Math.Clamp((int)material.Layer, 0, layerMeshes.Length - 1)];
            emitted |= EmitSupportBeam(target, material, chunks, x, y, z,
                beam.Start, beam.End, beam.SlumpPerMeter);
        }

        return emitted;
    }

    private bool EmitSupportBeam(MeshBuffer mesh, BlockMaterialInfo material,
        IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks,
        int blockX, int blockY, int blockZ, Vec3f start, Vec3f end, float slumpPerMeter)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var dz = end.Z - start.Z;
        var length = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (length < .0001f) return false;

        var direction = (dx / length, dy / length, dz / length);
        var reference = MathF.Abs(direction.Item2) > .92f
            ? (0f, 0f, 1f)
            : (0f, 1f, 0f);
        var side = Normalize(Cross(direction, reference));
        var up = Normalize(Cross(side, direction));
        const float halfWidth = .125f;
        var segmentCount = Math.Max(1, (int)MathF.Ceiling(length));
        var emitted = false;

        for (var segment = 0; segment < segmentCount; segment++)
        {
            var t0 = segment / (float)segmentCount;
            var t1 = (segment + 1) / (float)segmentCount;
            var a = BeamPoint(start, end, t0, length, slumpPerMeter);
            var b = BeamPoint(start, end, t1, length, slumpPerMeter);
            var sideOffset = Scale(side, halfWidth);
            var upOffset = Scale(up, halfWidth);

            // Build the eight corners of the square beam section.  Combining
            // the offsets is essential: using each offset independently makes
            // crossed quads, which show up as holes and texture spill from
            // different camera angles.
            var aTopSide = Add(Add(a, upOffset), sideOffset);
            var bTopSide = Add(Add(b, upOffset), sideOffset);
            var aTopOther = Add(Subtract(a, sideOffset), upOffset);
            var bTopOther = Add(Subtract(b, sideOffset), upOffset);
            var aBottomSide = Add(Subtract(a, upOffset), sideOffset);
            var bBottomSide = Add(Subtract(b, upOffset), sideOffset);
            var aBottomOther = Subtract(Subtract(a, upOffset), sideOffset);
            var bBottomOther = Subtract(Subtract(b, upOffset), sideOffset);

            // The source supportbeam shape is a 4x4 cross-section with its
            // length on X. Keep each generated section at one block or less
            // so the atlas texture repeats with the same scale as vanilla.
            EmitBeamQuad(mesh, material, chunks, blockX, blockY, blockZ,
                aBottomSide, bBottomSide, bTopSide, aTopSide,
                DominantFace(Cross(Subtract(bBottomSide, aBottomSide),
                    Subtract(aTopSide, aBottomSide))),
                t0, t1, 0, 1);
            EmitBeamQuad(mesh, material, chunks, blockX, blockY, blockZ,
                aTopOther, bTopOther, bBottomOther, aBottomOther,
                DominantFace(Cross(Subtract(bTopOther, aTopOther),
                    Subtract(aBottomOther, aTopOther))),
                t0, t1, 0, 1);
            EmitBeamQuad(mesh, material, chunks, blockX, blockY, blockZ,
                aTopSide, bTopSide, bTopOther, aTopOther,
                DominantFace(Cross(Subtract(bTopSide, aTopSide),
                    Subtract(aTopOther, aTopSide))),
                t0, t1, 0, 1);
            EmitBeamQuad(mesh, material, chunks, blockX, blockY, blockZ,
                aBottomOther, bBottomOther, bBottomSide, aBottomSide,
                DominantFace(Cross(Subtract(bBottomOther, aBottomOther),
                    Subtract(aBottomSide, aBottomOther))),
                t0, t1, 0, 1);
            EmitBeamQuad(mesh, material, chunks, blockX, blockY, blockZ,
                aTopSide, aTopOther, aBottomOther, aBottomSide,
                DominantFace(Scale(direction, -1)), 0, 1, 0, 1);
            EmitBeamQuad(mesh, material, chunks, blockX, blockY, blockZ,
                bTopSide, bBottomSide, bBottomOther, bTopOther,
                DominantFace(direction), 0, 1, 0, 1);
            emitted = true;
        }
        return emitted;
    }

    private void EmitBeamQuad(MeshBuffer mesh, BlockMaterialInfo material,
        IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks,
        int blockX, int blockY, int blockZ,
        (float X, float Y, float Z) p0, (float X, float Y, float Z) p1,
        (float X, float Y, float Z) p2, (float X, float Y, float Z) p3,
        int face, float u0, float u1, float v0, float v1)
    {
        var tile = material.FaceTiles[Math.Clamp(face, 0, material.FaceTiles.Length - 1)];
        var first = mesh.Vertices.Count / 3;
        var points = new[] { p0, p1, p2, p3 };
        var uv = new[] { (u0, v0), (u1, v0), (u1, v1), (u0, v1) };
        foreach (var point in points)
        {
            mesh.Vertices.Add(blockX + point.X);
            mesh.Vertices.Add(blockY + point.Y);
            mesh.Vertices.Add(blockZ + point.Z);
            AddLight(mesh, SampleCornerLight(chunks, blockX, blockY, blockZ, 1, material, face,
                blockX + point.X, blockY + point.Y, blockZ + point.Z));
        }
        for (var index = 0; index < uv.Length; index++)
        {
            var mapped = materials.Uv(tile, uv[index].Item1, uv[index].Item2);
            mesh.Uvs.Add(mapped.U); mesh.Uvs.Add(mapped.V);
        }
        mesh.Indices.Add(first); mesh.Indices.Add(first + 1); mesh.Indices.Add(first + 2);
        mesh.Indices.Add(first); mesh.Indices.Add(first + 2); mesh.Indices.Add(first + 3);
    }

    private static (float X, float Y, float Z) BeamPoint(Vec3f start, Vec3f end, float t,
        float length, float slumpPerMeter)
    {
        var distance = (t - .5f) * length;
        var slump = MathF.Sin(distance * slumpPerMeter);
        return (start.X + (end.X - start.X) * t,
            start.Y + (end.Y - start.Y) * t + slump,
            start.Z + (end.Z - start.Z) * t);
    }

    private static (float X, float Y, float Z) Cross((float X, float Y, float Z) left,
        (float X, float Y, float Z) right) =>
        (left.Y * right.Z - left.Z * right.Y,
            left.Z * right.X - left.X * right.Z,
            left.X * right.Y - left.Y * right.X);

    private static (float X, float Y, float Z) Normalize((float X, float Y, float Z) value)
    {
        var length = MathF.Sqrt(value.X * value.X + value.Y * value.Y + value.Z * value.Z);
        return length < .0001f ? (1, 0, 0) : (value.X / length, value.Y / length, value.Z / length);
    }

    private static (float X, float Y, float Z) Add((float X, float Y, float Z) left,
        (float X, float Y, float Z) right) => (left.X + right.X, left.Y + right.Y, left.Z + right.Z);

    private static (float X, float Y, float Z) Subtract((float X, float Y, float Z) left,
        (float X, float Y, float Z) right) => (left.X - right.X, left.Y - right.Y, left.Z - right.Z);

    private static (float X, float Y, float Z) Scale((float X, float Y, float Z) value, float scale) =>
        (value.X * scale, value.Y * scale, value.Z * scale);

    private void DiagnoseDynamic(BlockMaterialInfo info, int x, int y, int z, string reason)
    {
        var key = $"{info.Id}:{reason}";
        if (!dynamicDiagnostics.Add(key)) return;
        reader.LogNotification("ServerMap dynamic shape unresolved at {0},{1},{2}: {3} ({4})", x, y, z, reason, info.Code);
    }

    private static string? RemapDynamicType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        type = type.Trim();
        // Official BlockClutter.Remap in 1.22.3/1.22.7.
        return type.StartsWith("pipes/", StringComparison.OrdinalIgnoreCase)
            ? "pipe-veryrusted-" + type[6..]
            : type;
    }

    private bool AddMicroBlock(MeshBuffer mesh, BlockEntityMicroBlock micro, int x, int y, int z, int size,
        int[] neighbors, IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks,
        MeshBuffer? overlayMesh, MeshBuffer[] layerMeshes)
    {
        if (micro.BlockIds is not { Length: > 0 } blockIds
            || micro.VoxelCuboids is not { Count: > 0 } cuboids) return false;

        // Structure imports in 1.22.x can leave the selectable-collider
        // helper block next to the real dynamic clutter/banner block. It is
        // selection geometry, not a renderable microblock material. Rendering
        // it creates the extra white/black object seen over ruin props. Keep
        // ordinary chiseled blocks (which use real source materials) intact.
        if (ShouldSuppressRuinSelectableCollider(micro, blockIds, x, y, z, chunks))
            return true;

        // Trader schematics in 1.22.x may store a dynamic banner as a
        // selectable-collider microblock.  The collider id has no banner
        // texture; restore the original BlockShapeFromAttributes type from
        // the persisted display name and use the captured vanilla shape.
        if (TryAddRecoveredHansaBanner(mesh, micro.BlockName, cuboids, x, y, z, size,
                neighbors, chunks, layerMeshes)) return true;

        // BlockEntityMicroBlock keeps the uncut source cuboids separately.
        // Their envelope is the client's test for an outside face; faces of
        // a chisel cut that do not touch this envelope use inside-* textures.
        var originalX0 = 16; var originalY0 = 16; var originalZ0 = 16;
        var originalX1 = 0; var originalY1 = 0; var originalZ1 = 0;
        if (micro.OriginalVoxelCuboids is { Length: > 0 } originalCuboids)
        {
            // The native CreateMesh path intentionally reads element zero
            // only, because originalBounds describes the source cuboid whose
            // faces are being tested. Match that exact rule for collision-box
            // based microblocks instead of taking an envelope over all boxes.
            var originalPacked = originalCuboids[0];
            originalX0 = (int)(originalPacked & 0xF);
            originalY0 = (int)((originalPacked >> 4) & 0xF);
            originalZ0 = (int)((originalPacked >> 8) & 0xF);
            originalX1 = (int)(((originalPacked >> 12) & 0xF) + 1);
            originalY1 = (int)(((originalPacked >> 16) & 0xF) + 1);
            originalZ1 = (int)(((originalPacked >> 20) & 0xF) + 1);
        }
        if (originalX1 <= originalX0 || originalY1 <= originalY0 || originalZ1 <= originalZ0)
        {
            originalX0 = originalY0 = originalZ0 = 0;
            originalX1 = originalY1 = originalZ1 = 16;
        }

        // BlockEntityMicroBlock.GenRotatedMaterialIds creates a temporary
        // rotated material array for GenMesh; it does not write those ids back
        // to BlockIds.  Decode the saved source ids and apply the same mapping
        // before reading their client texture sets.
        var materialRotation = ReadMicroRotation(micro);
        var decoded = DecodeMicroCuboids(blockIds, cuboids, materialRotation, x, y, z, micro.BlockName, chunks);

        // Keep attached decorations independent from the voxel materials,
        // matching BlockEntityMicroBlock.CreateMesh/loadDecor.
        AddAttachedMicroDecors(layerMeshes, micro, x, y, z, size, cuboids, chunks, materialRotation);
        if (decoded.Count == 0) return true;

        // The native client fills its 18x18x18 voxel edge table with the
        // boundary faces of neighboring microblock entities.  Build the same
        // projected cuboids here so a face is hidden only when every exposed
        // voxel cell is actually covered by the adjacent microblock.
        var neighboringFaces = new IReadOnlyList<MicroCuboid>?[6];
        for (var face = 0; face < neighboringFaces.Length; face++)
        {
            var normal = FaceNormals[face];
            var neighbor = ResolveMicroBlockEntity(chunks, x + normal.X, y + normal.Y, z + normal.Z);
            if (neighbor?.BlockIds is not { Length: > 0 } neighborIds
                || neighbor.VoxelCuboids is not { Count: > 0 } neighborCuboids) continue;

            // FetchNeighborVoxels passes the neighbour's raw BlockIds. Its
            // rotation is deliberately not applied; only the current entity
            // uses GenRotatedMaterialIds() in the native 1.22.x mesher.
            var neighborDecoded = DecodeMicroCuboids(neighborIds, neighborCuboids, 0,
                x + normal.X, y + normal.Y, z + normal.Z, null, chunks);
            neighboringFaces[face] = ProjectNeighborFace(neighborDecoded, face);
        }

        // The client does not emit one quad per stored cuboid.  It first
        // writes all cuboid boundary cells into an 18^3 voxel table, then
        // greedily merges visible cells on each plane.  Reproduce that
        // behaviour with integer 1/16 coordinates; this also preserves
        // partial visibility where a neighbouring cuboid covers only part
        // of a face.
        var emitted = false;
        for (var cuboidIndex = 0; cuboidIndex < decoded.Count; cuboidIndex++)
        {
            var cuboid = decoded[cuboidIndex];
            // Only an unresolved temporary meta-blocklayer is omitted. Other
            // Meta-pass materials can carry real client textures and must be
            // retained for the microblock mesh.
            if (cuboid.SkipRender) continue;
            for (var face = 0; face < 6; face++)
            {
                var visible = BuildMicroFaceVisibility(decoded, cuboidIndex, face, neighboringFaces[face]);
                if (visible == null) continue;
                foreach (var rect in MergeMicroFaceCells(visible, cuboid, face))
                {
                    var rx0 = rect.U0;
                    var rx1 = rect.U1;
                    var ry0 = rect.V0;
                    var ry1 = rect.V1;
                    var x0 = cuboid.X0;
                    var x1 = cuboid.X1;
                    var y0 = cuboid.Y0;
                    var y1 = cuboid.Y1;
                    var z0 = cuboid.Z0;
                    var z1 = cuboid.Z1;
                    switch (face)
                    {
                        case 0 or 1:
                            // BuildMicroFaceVisibility uses U=Y and V=Z for
                            // west/east faces, exactly like the client's
                            // X-plane mesher.  Keep that coordinate order
                            // when materializing the merged rectangle.  A
                            // previous Y/Z swap moved partial faces onto the
                            // wrong voxels and made their UVs look rotated or
                            // offset.
                            y0 = rx0; y1 = rx1; z0 = ry0; z1 = ry1; break;
                        case 2 or 3:
                            x0 = rx0; x1 = rx1; z0 = ry0; z1 = ry1; break;
                        default:
                            x0 = rx0; x1 = rx1; y0 = ry0; y1 = ry1; break;
                    }

                    var minX = x + x0 / 16f * size;
                    var minY = y + y0 / 16f;
                    var minZ = z + z0 / 16f * size;
                    var maxX = x + x1 / 16f * size;
                    var maxY = y + y1 / 16f;
                    var maxZ = z + z1 / 16f * size;
                    var outside = face switch
                    {
                        0 => x0 == originalX0,
                        1 => x1 == originalX1,
                        2 => y0 == originalY0,
                        3 => y1 == originalY1,
                        4 => z0 == originalZ0,
                        5 => z1 == originalZ1,
                        _ => true
                    };
                    var tile = outside ? cuboid.Material.FaceTiles[face] : cuboid.Material.InsideFaceTiles[face];
                    // A slanted roofing block is a JSON shape, not a cube.
                    // VoxelMaterial.FromBlock() falls back to its first
                    // texture for every face, so recover the source shape
                    // face for each exposed voxel face. Matching the face
                    // plane and position prevents plank supports from being
                    // painted with shingle textures.
                    if (outside && IsNamedRoof(cuboid.Material))
                    {
                        // Resolve against the actual merged rectangle.  A
                        // large source cuboid can expose several different
                        // shape faces, so its overall centre is not a valid
                        // representative for every emitted rectangle.
                        var exposed = new MicroCuboid(x0, y0, z0, x1, y1, z1,
                            cuboid.MaterialId, cuboid.Material, false);
                        tile = ResolveMicroShapeTile(cuboid.Material, exposed, face, tile);
                    }
                    var target = layerMeshes[Math.Clamp((int)cuboid.Material.Layer, 0, layerMeshes.Length - 1)];
                    var bounds = new MicroUvBounds(x0 / 16f, y0 / 16f, z0 / 16f, x1 / 16f, y1 / 16f, z1 / 16f);
                    AddMicroFaceRect(target, cuboid.Material, chunks, x, y, z, size, face, tile,
                        minX, minY, minZ, maxX, maxY, maxZ, bounds,
                        overlayMesh, cuboid.Material.TopSoilOverlayTiles, emitted);
                    AddMicroDecorRect(layerMeshes, micro, cuboid.Material, chunks, x, y, z, size, face,
                        bounds, minX, minY, minZ, maxX, maxY, maxZ, materialRotation);
                    emitted = true;
                }
            }
        }
        return emitted;
    }

    private bool ShouldSuppressRuinSelectableCollider(BlockEntityMicroBlock micro, int[] blockIds,
        int x, int y, int z, IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks)
    {
        if (!blockIds.All(IsSelectableColliderId)) return false;

        var name = NormalizeRuinDisplayName(micro.BlockName);
        if (IsKnownRuinDynamicName(name))
        {
            LogSuppressedRuinMicroblock(x, y, z, micro.BlockName!, "named-dynamic-overlay");
            return true;
        }

        // Some schematic entries only retain the name on the first collider
        // piece. Suppress unnamed pieces immediately adjacent to that named
        // piece so multi-block flags/props do not leave a partial duplicate.
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
        {
            if (dx == 0 && dy == 0 && dz == 0) continue;
            var neighbor = ResolveMicroBlockEntity(chunks, x + dx, y + dy, z + dz);
            if (neighbor?.BlockIds is not { Length: > 0 } neighborIds
                || !neighborIds.All(IsSelectableColliderId)) continue;
            if (!IsKnownRuinDynamicName(NormalizeRuinDisplayName(neighbor.BlockName))) continue;

            LogSuppressedRuinMicroblock(x, y, z, micro.BlockName!, "adjacent-named-collider");
            return true;
        }

        return false;
    }

    private bool IsSelectableColliderId(int id)
    {
        return id > 0 && id < materials.BlockCount
            && materials.Get(id).Code.EndsWith(":meta-selectablecollider", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRuinDisplayName(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();

    private static bool IsKnownRuinDynamicName(string normalized)
    {
        if (normalized.Length == 0) return false;
        return normalized.Contains("trader merchandise", StringComparison.Ordinal)
            || normalized.Contains("hansa banner", StringComparison.Ordinal);
    }

    private void LogSuppressedRuinMicroblock(int x, int y, int z, string name, string reason)
    {
        var key = $"suppressed:{x},{y},{z}:{reason}";
        if (!microSourceDiagnostics.Add(key)) return;
        reader.LogNotification(
            "ServerMap suppressed duplicate ruin microblock: pos={0},{1},{2}; name={3}; reason={4}; source=game:meta-selectablecollider",
            x, y, z, name ?? "", reason);
    }

    private sealed record MicroFaceGrid(bool[,] Cells, int U0, int U1, int V0, int V1);

    private MicroFaceGrid? BuildMicroFaceVisibility(IReadOnlyList<MicroCuboid> cuboids, int currentIndex,
        int face, IReadOnlyList<MicroCuboid>? neighboringCuboids)
    {
        var current = cuboids[currentIndex];
        var (u0, u1, v0, v1) = face switch
        {
            0 or 1 => (current.Y0, current.Y1, current.Z0, current.Z1),
            2 or 3 => (current.X0, current.X1, current.Z0, current.Z1),
            4 or 5 => (current.X0, current.X1, current.Y0, current.Y1),
            _ => (0, 0, 0, 0)
        };
        if (u1 <= u0 || v1 <= v0) return null;

        var cells = new bool[16, 16];
        for (var u = u0; u < u1; u++)
        for (var v = v0; v < v1; v++)
        {
            var covered = false;
            for (var index = 0; index < cuboids.Count && !covered; index++)
            {
                if (index == currentIndex) continue;
                var candidate = cuboids[index];
                if (!MicroFaceTouches(candidate, current, face)
                    || !MicroFaceContains(candidate, face, u, v)) continue;
                covered = materials.CanMergeMicroMaterials(current.MaterialId, candidate.MaterialId);
            }
            if (!covered && neighboringCuboids is { Count: > 0 })
            {
                foreach (var candidate in neighboringCuboids)
                {
                    if (MicroFaceContains(candidate, face, u, v)
                        && materials.CanMergeMicroMaterials(current.MaterialId, candidate.MaterialId))
                    {
                        covered = true;
                        break;
                    }
                }
            }
            cells[u, v] = !covered;
        }
        return new MicroFaceGrid(cells, u0, u1, v0, v1);
    }

    private static bool MicroFaceTouches(MicroCuboid candidate, MicroCuboid current, int face) => face switch
    {
        0 => candidate.X1 == current.X0,
        1 => candidate.X0 == current.X1,
        2 => candidate.Y1 == current.Y0,
        3 => candidate.Y0 == current.Y1,
        4 => candidate.Z1 == current.Z0,
        5 => candidate.Z0 == current.Z1,
        _ => false
    };

    private static bool MicroFaceContains(MicroCuboid candidate, int face, int u, int v) => face switch
    {
        0 or 1 => u >= candidate.Y0 && u < candidate.Y1 && v >= candidate.Z0 && v < candidate.Z1,
        2 or 3 => u >= candidate.X0 && u < candidate.X1 && v >= candidate.Z0 && v < candidate.Z1,
        4 or 5 => u >= candidate.X0 && u < candidate.X1 && v >= candidate.Y0 && v < candidate.Y1,
        _ => false
    };

    private static IEnumerable<(int U0, int V0, int U1, int V1)> MergeMicroFaceCells(MicroFaceGrid grid,
        MicroCuboid cuboid, int face)
    {
        var used = new bool[16, 16];
        for (var v = grid.V0; v < grid.V1; v++)
        for (var u = grid.U0; u < grid.U1; u++)
        {
            if (!grid.Cells[u, v] || used[u, v]) continue;
            var width = 1;
            while (u + width < grid.U1 && grid.Cells[u + width, v] && !used[u + width, v]) width++;
            var length = 1;
            while (v + length < grid.V1)
            {
                var rowVisible = true;
                for (var test = 0; test < width; test++)
                {
                    if (!grid.Cells[u + test, v + length] || used[u + test, v + length])
                    {
                        rowVisible = false;
                        break;
                    }
                }
                if (!rowVisible) break;
                length++;
            }
            for (var yy = 0; yy < length; yy++)
            for (var xx = 0; xx < width; xx++) used[u + xx, v + yy] = true;
            yield return (u, v, u + width, v + length);
        }
    }

    private void AddMicroFaceRect(MeshBuffer target, BlockMaterialInfo material,
        IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks,
        int blockX, int blockY, int blockZ, int size, int face, int tile,
        float minX, float minY, float minZ, float maxX, float maxY, float maxZ,
        MicroUvBounds bounds, MeshBuffer? overlayMesh, int[] overlayTiles, bool emitted)
    {
        var points = new float[]
        {
            minX, minY, minZ, maxX, minY, minZ, maxX, maxY, minZ, minX, maxY, minZ,
            minX, minY, maxZ, maxX, minY, maxZ, maxX, maxY, maxZ, minX, maxY, maxZ
        };
        var corners = face switch
        {
            0 => new[] { 4, 7, 3, 0 },
            1 => new[] { 1, 2, 6, 5 },
            2 => new[] { 4, 0, 1, 5 },
            3 => new[] { 6, 2, 3, 7 },
            4 => new[] { 0, 3, 2, 1 },
            _ => new[] { 5, 6, 7, 4 }
        };
        Face(target, points, material, chunks, blockX, blockY, blockZ, size, face, tile, bounds, corners);

        if (overlayMesh == null || (uint)face >= (uint)overlayTiles.Length || overlayTiles[face] < 0) return;
        var overlayPoints = (float[])points.Clone();
        var normal = FaceNormals[face];
        var epsilon = .0015f * Math.Max(1, size);
        for (var vertex = 0; vertex < 8; vertex++)
        {
            overlayPoints[vertex * 3] += normal.X * epsilon;
            overlayPoints[vertex * 3 + 1] += normal.Y * epsilon;
            overlayPoints[vertex * 3 + 2] += normal.Z * epsilon;
        }
        FaceTopSoilOverlay(overlayMesh, overlayPoints, material, chunks, blockX, blockY, blockZ, size,
            face, overlayTiles[face], face == 3 ? GameMath.MurmurHash3Mod(blockX, blockY, blockZ, 4) : 0,
            bounds, corners);
    }

    private void AddMicroDecorRect(MeshBuffer[] layerMeshes, BlockEntityMicroBlock micro, BlockMaterialInfo baseMaterial,
        IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks,
        int blockX, int blockY, int blockZ, int size, int face, MicroUvBounds bounds,
        float minX, float minY, float minZ, float maxX, float maxY, float maxZ, int blockRotation)
    {
        if (micro.DecorIds is not { Length: >= 6 } decorIds) return;
        var clientFace = ServerToClientMicroFace[face];
        var sourceFace = GetRotatedDecorSourceFace(clientFace, blockRotation);
        var decorId = NormalizeRegistryId(decorIds[sourceFace]);
        if (decorId <= 0) return;
        var decor = materials.Get(decorId);
        if (decor.Id <= 0 || decor.Geometry == BlockGeometryKind.Empty
            || decor.AttachAs3d || decor.RenderPass == EnumChunkRenderPass.Meta) return;

        var target = layerMeshes[Math.Clamp((int)decor.Layer, 0, layerMeshes.Length - 1)];
        var rotation = (micro.DecorRotations >> (clientFace * 3)) & 7;
        var points = new float[]
        {
            minX, minY, minZ, maxX, minY, minZ, maxX, maxY, minZ, minX, maxY, minZ,
            minX, minY, maxZ, maxX, minY, maxZ, maxX, maxY, maxZ, minX, maxY, maxZ
        };
        var normal = FaceNormals[face];
        var epsilon = .0015f * Math.Max(1, size);
        for (var vertex = 0; vertex < 8; vertex++)
        {
            points[vertex * 3] += normal.X * epsilon;
            points[vertex * 3 + 1] += normal.Y * epsilon;
            points[vertex * 3 + 2] += normal.Z * epsilon;
        }
        var corners = face switch
        {
            0 => new[] { 4, 7, 3, 0 },
            1 => new[] { 1, 2, 6, 5 },
            2 => new[] { 4, 0, 1, 5 },
            3 => new[] { 6, 2, 3, 7 },
            4 => new[] { 0, 3, 2, 1 },
            _ => new[] { 5, 6, 7, 4 }
        };
        EmitMicroDecorFace(target, points, decor, chunks, blockX, blockY, blockZ, size, face,
            decor.FaceTiles[face], bounds, rotation, corners);
    }

    private int NormalizeRegistryId(int id)
    {
        if (id > 0 && id < materials.BlockCount) return id;
        // BlockEntityMicroBlock stores direct registry ids.  Masking an
        // invalid value can silently turn corrupt entity data into an
        // unrelated valid block, which is much harder to audit than the
        // dedicated black fallback material.
        return MaterialCatalog.MissingBlockId;
    }

    private static bool IsNamedRoof(BlockMaterialInfo material) =>
        // Only the slanted-roof block uses the shape face-role matcher. An
        // Aged/Veryaged roofing microblock is deliberately remapped to the
        // real supportbeam material; sending that material back through the
        // roofing shape matcher reintroduces the old acacia/plank UV binding.
        material.Code.StartsWith("game:slantedroofing-", StringComparison.OrdinalIgnoreCase)
        && material.Shapes.Any(shape => shape.Faces.Length > 0);

    private static int ResolveMicroShapeTile(BlockMaterialInfo material, MicroCuboid cuboid, int face, int fallback)
    {
        if ((uint)face >= (uint)FaceNormals.Length || material.Shapes.Length == 0) return fallback;

        var normal = FaceNormals[face];
        var center = MicroFaceCenter(cuboid, face);
        var bestTile = fallback;
        var bestScore = float.NegativeInfinity;

        foreach (var shape in material.Shapes)
        foreach (var candidate in shape.Faces)
        {
            if (candidate.Tile < 0 || candidate.Vertices is not { Length: >= 12 }
                || candidate.Normal is not { Length: >= 3 }) continue;

            var dot = normal.X * candidate.Normal[0]
                + normal.Y * candidate.Normal[1]
                + normal.Z * candidate.Normal[2];
            // A voxel face can approximate a slanted source face, but a
            // back-facing source plane must never win the match.
            if (dot < .2f) continue;

            var minX = float.PositiveInfinity; var minY = float.PositiveInfinity; var minZ = float.PositiveInfinity;
            var maxX = float.NegativeInfinity; var maxY = float.NegativeInfinity; var maxZ = float.NegativeInfinity;
            for (var vertex = 0; vertex < 4; vertex++)
            {
                var offset = vertex * 3;
                minX = Math.Min(minX, candidate.Vertices[offset]);
                minY = Math.Min(minY, candidate.Vertices[offset + 1]);
                minZ = Math.Min(minZ, candidate.Vertices[offset + 2]);
                maxX = Math.Max(maxX, candidate.Vertices[offset]);
                maxY = Math.Max(maxY, candidate.Vertices[offset + 1]);
                maxZ = Math.Max(maxZ, candidate.Vertices[offset + 2]);
            }

            // The voxel face is allowed to be slightly outside a rotated
            // source polygon because the client first voxelizes the shape.
            const float margin = .06f;
            var inBounds = center.X >= minX - margin && center.X <= maxX + margin
                && center.Y >= minY - margin && center.Y <= maxY + margin
                && center.Z >= minZ - margin && center.Z <= maxZ + margin;
            if (!inBounds) continue;

            // Compare the source polygon's projected footprint with the
            // emitted voxel rectangle.  The centre alone is ambiguous at a
            // roof support/rafter intersection and can select a neighbouring
            // face that only touches the voxel at one point.
            var (u0, u1, v0, v1) = face switch
            {
                0 or 1 => (cuboid.Y0 / 16f, cuboid.Y1 / 16f, cuboid.Z0 / 16f, cuboid.Z1 / 16f),
                2 or 3 => (cuboid.X0 / 16f, cuboid.X1 / 16f, cuboid.Z0 / 16f, cuboid.Z1 / 16f),
                _ => (cuboid.X0 / 16f, cuboid.X1 / 16f, cuboid.Y0 / 16f, cuboid.Y1 / 16f)
            };
            var (candidateU0, candidateU1, candidateV0, candidateV1) = face switch
            {
                0 or 1 => (minY, maxY, minZ, maxZ),
                2 or 3 => (minX, maxX, minZ, maxZ),
                _ => (minX, maxX, minY, maxY)
            };
            var overlapU = Math.Max(0, Math.Min(u1, candidateU1) - Math.Max(u0, candidateU0));
            var overlapV = Math.Max(0, Math.Min(v1, candidateV1) - Math.Max(v0, candidateV0));
            var voxelArea = Math.Max(.0001f, (u1 - u0) * (v1 - v0));
            var overlap = overlapU * overlapV / voxelArea;
            if (overlap <= .01f) continue;

            var p0x = candidate.Vertices[0];
            var p0y = candidate.Vertices[1];
            var p0z = candidate.Vertices[2];
            var planeDistance = Math.Abs(candidate.Normal[0] * (center.X - p0x)
                + candidate.Normal[1] * (center.Y - p0y)
                + candidate.Normal[2] * (center.Z - p0z));

            // Vanilla roofing binds the same shape to different atlas
            // textures by face role.  The voxel material's six-face fallback
            // cannot retain that information, so use the binding as a
            // semantic tie-breaker after the geometric test.  In particular,
            // an underside support (acacia1) must not replace the deliberate
            // acacia-top texture on the sloped roof plane.
            var role = RoofTextureRole(candidate.TextureKey);
            // The vanilla roofing shape contract is strict: top/bottom voxel
            // faces use the shingle-top binding, while the four vertical
            // faces use shingle-side or plank bindings.  Do not let a sloped
            // side AABB win a horizontal face merely because its normal has
            // a positive Y component.
            if (face is 2 or 3 && role == RoofTextureRoleKind.Side) continue;
            if (face is 0 or 1 or 4 or 5 && role == RoofTextureRoleKind.Top) continue;
            var roleScore = role switch
            {
                RoofTextureRoleKind.Top when face == 3 && candidate.Normal[1] > .45f => 12f,
                RoofTextureRoleKind.Top when face == 2 && candidate.Normal[1] < -.45f => 12f,
                RoofTextureRoleKind.Top when face is 2 or 3 => -8f,
                RoofTextureRoleKind.Side when face is 0 or 1 or 4 or 5 => 5f,
                RoofTextureRoleKind.Side => -5f,
                _ => 0f
            };

            // Sloped source faces have no axis-aligned boundary.  Prefer one
            // when its footprint covers the voxel, while keeping an exact
            // support plane competitive where the sloped element does not
            // actually occupy the emitted rectangle.
            var score = roleScore + dot * 7f + overlap * 8f
                + (candidate.BoundaryFace < 0 ? 4f : 0f)
                - planeDistance * 12f + (inBounds ? 2f : 0f);
            if (score <= bestScore) continue;
            bestScore = score;
            bestTile = candidate.Tile;
        }
        return bestTile;
    }

    private enum RoofTextureRoleKind : byte
    {
        Plank,
        Side,
        Top
    }

    private static RoofTextureRoleKind RoofTextureRole(string? textureKey)
    {
        var key = textureKey?.ToLowerInvariant() ?? "";
        if (key.Contains("top", StringComparison.Ordinal)) return RoofTextureRoleKind.Top;
        if (key.Contains("side", StringComparison.Ordinal)) return RoofTextureRoleKind.Side;
        return RoofTextureRoleKind.Plank;
    }

    private static (float X, float Y, float Z) MicroFaceCenter(MicroCuboid cuboid, int face)
    {
        var x = (cuboid.X0 + cuboid.X1) / 32f;
        var y = (cuboid.Y0 + cuboid.Y1) / 32f;
        var z = (cuboid.Z0 + cuboid.Z1) / 32f;
        return face switch
        {
            0 => (cuboid.X0 / 16f, y, z),
            1 => (cuboid.X1 / 16f, y, z),
            2 => (x, cuboid.Y0 / 16f, z),
            3 => (x, cuboid.Y1 / 16f, z),
            4 => (x, y, cuboid.Z0 / 16f),
            5 => (x, y, cuboid.Z1 / 16f),
            _ => (x, y, z)
        };
    }

    private bool TryAddRecoveredHansaBanner(MeshBuffer mesh, string? blockName, IReadOnlyList<uint> cuboids,
        int x, int y, int z, int size, int[] neighbors,
        IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks, MeshBuffer[] layerMeshes)
    {
        if (string.IsNullOrWhiteSpace(blockName)) return false;
        var normalized = blockName.Trim().ToLowerInvariant();
        if (!normalized.Contains("hansa", StringComparison.Ordinal)
            || !normalized.Contains("banner", StringComparison.Ordinal)
            || !normalized.Contains("large fish", StringComparison.Ordinal)
                && !normalized.Contains("largefish", StringComparison.Ordinal)) return false;

        var bannerInfo = materials.FindByCode("game:banner-banner");
        if (bannerInfo == null) return false;
        var type = InferHansaBannerType(normalized, cuboids);
        var orientationY = InferHansaBannerOrientationY(cuboids);
        var dynamic = materials.GetDynamicShape(bannerInfo.Id, type);
        if (dynamic == null || !dynamic.TryGetShapes(null, out var shapes) || shapes.Length == 0)
        {
            DiagnoseDynamic(bannerInfo, x, y, z, $"recovered banner type '{type}' not captured");
            return false;
        }

        var state = new DynamicShapeState(dynamic, shapes, false,
            dynamic.TypeRotationX * GameMath.DEG2RAD,
            dynamic.TypeRotationY * GameMath.DEG2RAD + orientationY,
            dynamic.TypeRotationZ * GameMath.DEG2RAD,
            1, 0, 0, 0);
        AddShape(mesh, x, y, z, size, bannerInfo, neighbors, chunks,
            SampleBlockLight(chunks, x, y, z, size), shapeOverride: shapes,
            dynamicState: state, useRandomVariant: false, layerMeshes: layerMeshes);
        if (microSourceDiagnostics.Add($"banner:{x},{y},{z}:{type}"))
        {
            reader.LogNotification("ServerMap dynamic banner restore: pos={0},{1},{2}; name={3}; type={4}; block={5}; faces={6}; textures=largefish-top,largefish-bottom,budgreen",
                x, y, z, blockName, type, bannerInfo.Code, shapes.Sum(shape => shape.Faces.Length));
        }
        return true;
    }

    private static string InferHansaBannerType(string normalizedName, IReadOnlyList<uint> cuboids)
    {
        // A wall banner is a thin sheet (the vanilla wall selection box is
        // 1/16 block deep). The ground variant has a pole and occupies the
        // block in both horizontal axes. Use the actual voxel envelope so
        // rotated schematic entries are classified by geometry, not storage
        // order.
        var minX = 16; var minY = 16; var minZ = 16;
        var maxX = 0; var maxY = 0; var maxZ = 0;
        foreach (var packed in cuboids)
        {
            minX = Math.Min(minX, (int)(packed & 0xF));
            minY = Math.Min(minY, (int)((packed >> 4) & 0xF));
            minZ = Math.Min(minZ, (int)((packed >> 8) & 0xF));
            maxX = Math.Max(maxX, (int)(((packed >> 12) & 0xF) + 1));
            maxY = Math.Max(maxY, (int)(((packed >> 16) & 0xF) + 1));
            maxZ = Math.Max(maxZ, (int)(((packed >> 20) & 0xF) + 1));
        }
        var spanX = maxX - minX;
        var spanY = maxY - minY;
        var spanZ = maxZ - minZ;
        var thin = Math.Min(spanX, spanZ) <= 2 && spanY >= 8;
        return thin ? "hansa-wall-largefish" : "hansa-ground-largefish";
    }

    private static float InferHansaBannerOrientationY(IReadOnlyList<uint> cuboids)
    {
        var minX = 16; var minZ = 16; var maxX = 0; var maxZ = 0;
        foreach (var packed in cuboids)
        {
            minX = Math.Min(minX, (int)(packed & 0xF));
            minZ = Math.Min(minZ, (int)((packed >> 8) & 0xF));
            maxX = Math.Max(maxX, (int)(((packed >> 12) & 0xF) + 1));
            maxZ = Math.Max(maxZ, (int)(((packed >> 20) & 0xF) + 1));
        }
        // wall.json is authored in the north/south (thin-Z) orientation.
        // A thin-X voxel envelope is the 90-degree horizontal variant.
        return maxX - minX <= 2 && maxZ - minZ > 2 ? MathF.PI / 2 : 0;
    }

    private List<MicroCuboid> DecodeMicroCuboids(int[] blockIds, IReadOnlyList<uint> cuboids, int materialRotation,
        int worldX, int worldY, int worldZ, string? blockName,
        IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?>? chunks = null)
    {
        var decoded = new List<MicroCuboid>(cuboids.Count);
        var roofOrientation = InferNamedRoofOrientation(blockName, cuboids);
        var roofVariant = roofOrientation == null ? null : ResolveNamedRoofVariant(chunks, worldX, worldY, worldZ, blockName);
        var roofMaterial = roofOrientation == null || roofVariant == null ? null : FindNamedRoofMaterial(roofVariant, roofOrientation);
        if (roofMaterial != null && microSourceDiagnostics.Add($"roof:{blockName}|{roofMaterial.Code}|{worldX},{worldY},{worldZ}"))
        {
            reader.LogNotification(
                "ServerMap roofing microblock restore: pos={0},{1},{2}; name={3}; variant={4}; orientation={5}; material={6}; rotation={7}; cuboids={8}",
                worldX, worldY, worldZ, blockName ?? "", roofVariant ?? "", roofOrientation ?? "", roofMaterial.Code, materialRotation, cuboids.Count);
        }
        foreach (var packed in cuboids)
        {
            var x0 = (int)(packed & 0xF);
            var y0 = (int)((packed >> 4) & 0xF);
            var z0 = (int)((packed >> 8) & 0xF);
            var x1 = (int)(((packed >> 12) & 0xF) + 1);
            var y1 = (int)(((packed >> 16) & 0xF) + 1);
            var z1 = (int)(((packed >> 20) & 0xF) + 1);
            var materialIndex = (int)((packed >> 24) & 0xFF);
            if (x1 <= x0 || y1 <= y0 || z1 <= z0 || x0 >= 16 || y0 >= 16 || z0 >= 16) continue;

            var rawMaterialId = materialIndex < blockIds.Length ? blockIds[materialIndex] : 0;
            var materialId = materialIndex < blockIds.Length
                ? NormalizeRegistryId(rawMaterialId)
                : MaterialCatalog.MissingBlockId;
            var sourceCode = rawMaterialId > 0 && rawMaterialId < materials.BlockCount
                ? materials.Get(rawMaterialId).Code
                : "<invalid>";
            var skipRender = false;
            var resolvedMetaId = 0;
            var resolution = "";
            if (roofMaterial != null
                && sourceCode.EndsWith(":meta-selectablecollider", StringComparison.OrdinalIgnoreCase))
            {
                // Structures produced by the 1.22.x schematic importer can
                // retain this invisible collider in BlockIds even though the
                // entity's name still identifies the original roof block.
                // Restore only that explicit, named roof case; other
                // selectable colliders remain untouched and auditable.
                resolvedMetaId = roofMaterial.Id;
                materialId = roofMaterial.Id;
                resolution = "named-roof";
            }
            if (materialId > 0 && materials.IsMetaBlockLayer(materialId))
            {
                var resolved = reader.ResolveMetaBlockLayer(worldX, worldY, worldZ, materialId);
                if (resolved is > 0)
                {
                    resolvedMetaId = resolved.Value;
                    materialId = NormalizeRegistryId(resolved.Value);
                    resolution = "meta-blocklayer";
                }
                else
                {
                    // This id is a world-generation marker, not a texture.
                    // Do not turn a failed resolution into a diagnostic quad.
                    materialId = MaterialCatalog.MissingBlockId;
                    skipRender = true;
                }
            }
            var material = materialId > 0 && materialId < materials.BlockCount
                ? materials.Rotate(materials.Get(materialId), materialRotation)
                : materials.MissingMaterial;
            var sourceKey = $"{blockName}|{sourceCode}|{resolvedMetaId}|{materialRotation}";
            if (microSourceDiagnostics.Add(sourceKey))
            {
                var ids = string.Join(",", blockIds.Select(id => id > 0 && id < materials.BlockCount
                    ? $"{id}:{materials.Get(id).Code}" : $"{id}:<invalid>"));
                var faceTiles = material.FaceTiles == null ? "<none>" : string.Join(",", material.FaceTiles);
                var shapeTiles = IsNamedRoof(material)
                    ? string.Join(",", Enumerable.Range(0, 6).Select(face =>
                        ResolveMicroShapeTile(material, new MicroCuboid(x0, y0, z0, x1, y1, z1,
                            material.Id, material, false), face, material.FaceTiles[face])))
                    : "<none>";
                reader.LogNotification(
                    "ServerMap microblock material: pos={0},{1},{2}; name={3}; index={4}; source={5}; resolved={6}; material={7}; resolution={8}; bounds={9},{10},{11}-{12},{13},{14}; rotation={15}; faces={16}; shapeFaces={17}; blockIds=[{18}]",
                    worldX, worldY, worldZ, blockName ?? "", materialIndex, sourceCode,
                    resolvedMetaId > 0 ? $"{resolvedMetaId}:{materials.Get(resolvedMetaId).Code}" : "<none>",
                    material.Id > 0 ? $"{material.Id}:{material.Code}" : "<missing>", resolution,
                    x0, y0, z0, x1, y1, z1, materialRotation, faceTiles, shapeTiles, ids);
            }
            if (materialId > 0 && (material.Id == MaterialCatalog.MissingBlockId || material.FaceTiles.All(tile => tile == MaterialCatalog.MissingBlockId)))
            {
                var key = $"{materialId}:{materials.Get(materialId).Code}";
                if (microDiagnostics.Add(key))
                    reader.LogNotification("ServerMap microblock material unresolved: id={0}, code={1}, pos={2},{3},{4}", materialId, materials.Get(materialId).Code, worldX, worldY, worldZ);
            }
            // BlockEntityMicroBlock stores the real source block ids in
            // BlockIds. The client deliberately builds a VoxelMaterial for
            // every valid registry id, including Meta blocks and nested
            // micro/chiseled blocks whose normal block geometry is empty.
            // Keep those materials so their resolved face textures are still
            // available; ResolveTile has already assigned the black audit
            // tile when the client texture itself is unavailable.
            if (material.Id <= 0)
                material = materials.MissingMaterial;

            decoded.Add(new MicroCuboid(x0, y0, z0, x1, y1, z1, material.Id, material, skipRender));
        }
        return decoded;
    }

    private BlockMaterialInfo? FindNamedRoofMaterial(string? blockName, string orientation)
    {
        var material = RoofMaterialVariant(blockName) ?? NormalizeRoofVariant(blockName);
        if (material == null) return null;

        // In 1.22.x the trader/ruin "Aged roofing" microblock is authored
        // from the same rotten support-beam material as the surrounding
        // frame.  Resolve the normalized schematic labels back to their
        // actual support-beam codes: "aged" here represents rotten, and
        // "veryaged" represents veryrotten. The slanted-roof shape is a
        // shingle roof and gives the wrong plank/top/side bindings (and
        // therefore wrong UV offsets) when the schematic only retains
        // meta-selectablecollider.
        if (material.Equals("aged", StringComparison.OrdinalIgnoreCase))
            return materials.FindByCode("game:supportbeam-rotten");
        if (material.Equals("veryaged", StringComparison.OrdinalIgnoreCase))
            return materials.FindByCode("game:supportbeam-veryrotten");

        // Other named roofing variants retain their concrete vanilla shape.
        return materials.FindByCode($"game:slantedroofing-{material}-{orientation}-free");
    }

    private string? ResolveNamedRoofVariant(
        IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?>? chunks,
        int x, int y, int z, string? blockName)
    {
        // The schematic entity name is only "Aged roofing".  The source
        // material is encoded by the nearby 1.22.x trader blocks, commonly
        // supportbeam-aged/veryaged or planks-veryaged. Select the strongest
        // local evidence instead of assigning every ruin the aged atlas.
        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (chunks != null)
        {
            for (var dx = -3; dx <= 3; dx++)
            for (var dy = -3; dy <= 3; dy++)
            for (var dz = -3; dz <= 3; dz++)
            {
                var id = ReadWorld(chunks, x + dx, y + dy, z + dz);
                if (id <= 0 || id >= materials.BlockCount) continue;
                var code = materials.Get(id).Code;
                var variant = RoofVariantFromSourceCode(code);
                if (variant == null) continue;
                var distance = Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz);
                var weight = Math.Max(1, 16 - distance);
                if (code.Contains("supportbeam", StringComparison.OrdinalIgnoreCase)) weight += 18;
                else if (code.Contains("planks", StringComparison.OrdinalIgnoreCase)) weight += 8;
                else if (code.Contains("slantedroofing", StringComparison.OrdinalIgnoreCase)) weight += 12;
                scores[variant] = scores.TryGetValue(variant, out var score) ? score + weight : weight;
            }
        }

        var local = scores.OrderByDescending(pair => pair.Value).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(local.Key)) return local.Key;
        return RoofMaterialVariant(blockName);
    }

    private static string? RoofVariantFromSourceCode(string code)
    {
        var normalized = code.ToLowerInvariant();
        if (normalized.Contains("veryaged", StringComparison.Ordinal)
            || normalized.Contains("veryrotten", StringComparison.Ordinal)) return "veryaged";
        if (normalized.Contains("aged", StringComparison.Ordinal)
            || normalized.Contains("rotten", StringComparison.Ordinal)) return "aged";
        return normalized.Contains("slantedroofing-", StringComparison.Ordinal)
            ? normalized.Split('-').Skip(1).FirstOrDefault()
            : null;
    }

    private static string? NormalizeRoofVariant(string? variant)
    {
        if (string.IsNullOrWhiteSpace(variant)) return null;
        variant = variant.Trim().ToLowerInvariant();
        return variant is "copper" or "slate" or "thatch" or "agedthatch" or "blackclay"
            or "brownclay" or "creamclay" or "fireclay" or "grayclay" or "orangeclay"
            or "redclay" or "tanclay" or "bamboo" or "sod" or "acacia" or "aged" or "veryaged"
            or "baldcypress" or "birch" or "ebony" or "kapok" or "larch" or "maple"
            or "oak" or "pine" or "purpleheart" or "redwood" or "walnut"
            ? variant : null;
    }

    private static string? RoofMaterialVariant(string? blockName)
    {
        if (string.IsNullOrWhiteSpace(blockName)) return null;
        var name = blockName.Trim();
        var line = name.IndexOf('\n');
        if (line >= 0) name = name[..line];
        name = name.ToLowerInvariant();
        if (!name.Contains("roof", StringComparison.Ordinal)
            && !name.Contains("roofing", StringComparison.Ordinal)) return null;

        if (name.Contains("copper", StringComparison.Ordinal)) return "copper";
        if (name.Contains("slate", StringComparison.Ordinal)) return "slate";
        if (name.Contains("bamboo", StringComparison.Ordinal)) return "bamboo";
        if (name.Contains("sod", StringComparison.Ordinal)) return "sod";
        if (name.Contains("very aged", StringComparison.Ordinal)
            || name.Contains("veryaged", StringComparison.Ordinal)) return "veryaged";
        if (name.Contains("aged thatch", StringComparison.Ordinal)
            || name.Contains("agedthatch", StringComparison.Ordinal)) return "agedthatch";
        if (name.Contains("thatch", StringComparison.Ordinal)) return "thatch";
        if (name.Contains("clay", StringComparison.Ordinal))
        {
            foreach (var clay in new[] { "black", "brown", "cream", "fire", "gray", "orange", "red", "tan" })
                if (name.Contains(clay + " clay", StringComparison.Ordinal)
                    || name.Contains(clay + "clay", StringComparison.Ordinal)) return clay + "clay";
        }

        // The vanilla display name for the affected structure is exactly
        // "Aged roofing".  Do not infer wood from arbitrary names: only an
        // explicit aged roof is safe to restore this way.
        return name is "aged roofing" or "aged roof" ? "aged" : null;
    }

    private static string? InferNamedRoofOrientation(string? blockName, IReadOnlyList<uint> cuboids)
    {
        if (RoofMaterialVariant(blockName) == null || cuboids.Count == 0) return null;

        var xStarts = new Dictionary<int, List<float>>();
        var zStarts = new Dictionary<int, List<float>>();
        foreach (var packed in cuboids)
        {
            var x0 = (int)(packed & 0xF);
            var y0 = (int)((packed >> 4) & 0xF);
            var z0 = (int)((packed >> 8) & 0xF);
            var x1 = (int)(((packed >> 12) & 0xF) + 1);
            var y1 = (int)(((packed >> 16) & 0xF) + 1);
            var z1 = (int)(((packed >> 20) & 0xF) + 1);
            if (x1 <= x0 || y1 <= y0 || z1 <= z0) continue;
            Add(xStarts, x0, (y0 + y1) * .5f);
            Add(zStarts, z0, (y0 + y1) * .5f);
        }

        var xVariable = xStarts.Count > 1;
        var zVariable = zStarts.Count > 1;
        if (!xVariable && !zVariable) return "east";
        var useX = xVariable && (!zVariable || Span(xStarts) >= Span(zStarts));
        var groups = useX ? xStarts : zStarts;
        var ordered = groups.OrderBy(pair => pair.Key).Select(pair => pair.Value.Average()).ToArray();
        var trend = 0;
        for (var i = 1; i < ordered.Length; i++)
        {
            var delta = ordered[i] - ordered[i - 1];
            while (delta > 8) delta -= 16;
            while (delta < -8) delta += 16;
            trend += Math.Sign(delta);
        }
        if (trend == 0) trend = 1;
        if (useX) return trend > 0 ? "east" : "west";
        return trend > 0 ? "south" : "north";

        static void Add(Dictionary<int, List<float>> groups, int key, float value)
        {
            if (!groups.TryGetValue(key, out var values)) groups[key] = values = [];
            values.Add(value);
        }

        static int Span(Dictionary<int, List<float>> groups) => groups.Count == 0
            ? 0
            : groups.Keys.Max() - groups.Keys.Min();
    }

    private static List<MicroCuboid> ProjectNeighborFace(IReadOnlyList<MicroCuboid> neighbor, int face)
    {
        var projected = new List<MicroCuboid>();
        foreach (var cuboid in neighbor)
        {
            var touchesBoundary = face switch
            {
                0 => cuboid.X1 == 16,
                1 => cuboid.X0 == 0,
                2 => cuboid.Y1 == 16,
                3 => cuboid.Y0 == 0,
                4 => cuboid.Z1 == 16,
                5 => cuboid.Z0 == 0,
                _ => false
            };
            if (touchesBoundary) projected.Add(cuboid);
        }
        return projected;
    }

    private void AddAttachedMicroDecors(MeshBuffer[] layerMeshes, BlockEntityMicroBlock micro,
        int x, int y, int z, int size, IReadOnlyList<uint> voxelCuboids,
        IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks, int blockRotation)
    {
        if (micro.DecorIds is not { Length: >= 6 } decorIds) return;

        for (var clientFace = 0; clientFace < 6; clientFace++)
        {
            var sourceFace = GetRotatedDecorSourceFace(clientFace, blockRotation);
            var decorId = NormalizeRegistryId(decorIds[sourceFace]);
            if (decorId <= 0) continue;

            var decor = materials.Get(decorId);
            if (decor.Id <= 0 || !decor.AttachAs3d || decor.Geometry == BlockGeometryKind.Empty) continue;

            var target = layerMeshes[Math.Clamp((int)decor.Layer, 0, layerMeshes.Length - 1)];
            var local = new MeshBuffer();
            AddAttachedDecorGeometry(local, decor, x, y, z, size, chunks);
            if (local.Empty) continue;

            // loadDecor uses only the lower two bits for the physical block
            // rotation. The upper bit is a texture mirror used by decals.
            var rotation = (micro.DecorRotations >> (clientFace * 3)) & 7;
            var quarterTurns = rotation % 4;
            var radians = quarterTurns * (MathF.PI / 2f);
            var sin = MathF.Sin(radians);
            var cos = MathF.Cos(radians);
            var centerX = x + .5f * size;
            var centerZ = z + .5f * size;
            var normal = FaceNormals[ClientToServerMicroFace[clientFace]];
            var distance = OutermostVoxelDistanceToCenter(voxelCuboids, clientFace);
            var offsetX = normal.X * distance * size;
            var offsetY = normal.Y * distance;
            var offsetZ = normal.Z * distance * size;

            for (var vertex = 0; vertex < local.Vertices.Count / 3; vertex++)
            {
                var index = vertex * 3;
                var dx = local.Vertices[index] - centerX;
                var dz = local.Vertices[index + 2] - centerZ;
                local.Vertices[index] = centerX + cos * dx + sin * dz + offsetX;
                local.Vertices[index + 1] += offsetY;
                local.Vertices[index + 2] = centerZ - sin * dx + cos * dz + offsetZ;
            }

            var indexOffset = target.Vertices.Count / 3;
            target.Vertices.AddRange(local.Vertices);
            target.Lights.AddRange(local.Lights);
            target.Uvs.AddRange(local.Uvs);
            foreach (var index in local.Indices) target.Indices.Add(index + indexOffset);
        }
    }

    private void AddAttachedDecorGeometry(MeshBuffer mesh, BlockMaterialInfo decor, int x, int y, int z,
        int size, IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks)
    {
        var noNeighbors = new int[6];
        switch (decor.Geometry)
        {
            case BlockGeometryKind.Cross:
                AddCross(mesh, x, y, z, size, decor, SampleBlockLight(chunks, x, y, z, size));
                return;
            case BlockGeometryKind.Shape:
                AddShape(mesh, x, y, z, size, decor, noNeighbors, chunks,
                    SampleBlockLight(chunks, x, y, z, size), useRandomVariant: false);
                return;
            case BlockGeometryKind.Boxes when decor.Boxes.Length > 0:
                foreach (var box in decor.Boxes)
                {
                    AddCuboid(mesh, x + box.X1 * size, y + box.Y1, z + box.Z1 * size,
                        x + box.X2 * size, y + box.Y2, z + box.Z2 * size, decor,
                        0, 0, 0, 0, 0, 0, chunks, x, y, z, size);
                }
                return;
            case BlockGeometryKind.Empty:
                return;
            default:
                AddCuboid(mesh, x, y, z, x + size, y + 1, z + size, decor,
                    0, 0, 0, 0, 0, 0, chunks, x, y, z, size);
                return;
        }
    }

    private static float OutermostVoxelDistanceToCenter(IReadOnlyList<uint> voxelCuboids, int faceIndex)
    {
        var edge = faceIndex switch
        {
            0 => voxelCuboids.Count == 0 ? 16 : voxelCuboids.Min(value => (int)((value >> 8) & 0xF)),
            1 => voxelCuboids.Count == 0 ? 0 : voxelCuboids.Max(value => (int)(((value >> 12) & 0xF) + 1)),
            2 => voxelCuboids.Count == 0 ? 0 : voxelCuboids.Max(value => (int)(((value >> 20) & 0xF) + 1)),
            3 => voxelCuboids.Count == 0 ? 16 : voxelCuboids.Min(value => (int)(value & 0xF)),
            4 => voxelCuboids.Count == 0 ? 0 : voxelCuboids.Max(value => (int)(((value >> 16) & 0xF) + 1)),
            5 => voxelCuboids.Count == 0 ? 16 : voxelCuboids.Min(value => (int)((value >> 4) & 0xF)),
            _ => 0
        };
        var reference = faceIndex is 0 or 3 or 5 ? 16 : 0;
        return Math.Abs(reference - edge) / 16f;
    }

    private void AddMicroDecor(MeshBuffer[] layerMeshes, BlockEntityMicroBlock micro, int x, int y, int z, int size,
        BlockMaterialInfo baseMaterial, int[] faceNeighbors, MicroUvBounds uvBounds,
        IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks, int blockRotation,
        float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
    {
        if (micro.DecorIds is not { Length: >= 6 } decorIds) return;
        for (var face = 0; face < 6; face++)
        {
            if (faceNeighbors[face] != 0) continue;
            var clientFace = ServerToClientMicroFace[face];
            var sourceFace = GetRotatedDecorSourceFace(clientFace, blockRotation);
            if ((uint)sourceFace >= (uint)decorIds.Length) continue;
            var decorId = NormalizeRegistryId(decorIds[sourceFace]);
            if (decorId <= 0) continue;
            var decor = materials.Get(decorId);
            if (decor.Id <= 0 || decor.Geometry == BlockGeometryKind.Empty) continue;
            if (decor.AttachAs3d) continue;

            var targetMesh = layerMeshes[Math.Clamp((int)decor.Layer, 0, layerMeshes.Length - 1)];

            var rotation = (micro.DecorRotations >> (clientFace * 3)) & 7;
            var points = new float[]
            {
                minX, minY, minZ, maxX, minY, minZ, maxX, maxY, minZ, minX, maxY, minZ,
                minX, minY, maxZ, maxX, minY, maxZ, maxX, maxY, maxZ, minX, maxY, maxZ
            };
            var normal = FaceNormals[face];
            var epsilon = .0015f * Math.Max(1, size);
            for (var vertex = 0; vertex < 8; vertex++)
            {
                points[vertex * 3] += normal.X * epsilon;
                points[vertex * 3 + 1] += normal.Y * epsilon;
                points[vertex * 3 + 2] += normal.Z * epsilon;
            }
            var corners = face switch
            {
                0 => new[] { 4, 7, 3, 0 },
                1 => new[] { 1, 2, 6, 5 },
                2 => new[] { 4, 0, 1, 5 },
                3 => new[] { 6, 2, 3, 7 },
                4 => new[] { 0, 3, 2, 1 },
                _ => new[] { 5, 6, 7, 4 }
            };
            EmitMicroDecorFace(targetMesh, points, decor, chunks, x, y, z, size, face,
                decor.FaceTiles[face], uvBounds, rotation, corners);
        }
    }

    private void EmitMicroDecorFace(MeshBuffer mesh, float[] points, BlockMaterialInfo decor,
        IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks,
        int blockX, int blockY, int blockZ, int size, int face, int tile,
        MicroUvBounds bounds, int rotation, int[] corners)
    {
        var first = mesh.Vertices.Count / 3;
        var clientFace = ServerToClientMicroFace[face];
        for (var index = 0; index < 4; index++)
        {
            var corner = corners[index];
            var pointX = points[corner * 3];
            var pointY = points[corner * 3 + 1];
            var pointZ = points[corner * 3 + 2];
            mesh.Vertices.Add(pointX); mesh.Vertices.Add(pointY); mesh.Vertices.Add(pointZ);
            AddLight(mesh, SampleCornerLight(chunks, blockX, blockY, blockZ, size, decor, face,
                pointX, pointY, pointZ));
            var uvIndex = MicroUvIndex(clientFace, index);
            var cubeU = ClientCubeUvCoords[uvIndex];
            var cubeV = ClientCubeUvCoords[uvIndex + 1];
            var (localU, localV) = RelativeMicroUv(bounds, clientFace, cubeU, cubeV);
            (localU, localV) = RotateDecorUv(localU, localV, rotation);
            var mapped = materials.Uv(tile, localU, localV);
            mesh.Uvs.Add(mapped.U); mesh.Uvs.Add(mapped.V);
        }
        mesh.Indices.Add(first); mesh.Indices.Add(first + 1); mesh.Indices.Add(first + 2);
        mesh.Indices.Add(first); mesh.Indices.Add(first + 2); mesh.Indices.Add(first + 3);
    }

    private static (float U, float V) RotateDecorUv(float u, float v, int rotation)
    {
        if ((rotation & 4) == 0) u = 1f - u;
        switch (rotation % 8)
        {
            case 3:
            case 5:
                (u, v) = (v, 1f - u);
                break;
            case 2:
            case 6:
                u = 1f - u;
                v = 1f - v;
                break;
            case 1:
            case 7:
                (u, v) = (1f - v, u);
                break;
        }
        return (u, v);
    }

    private static int Mod(int value, int divisor) => ((value % divisor) + divisor) % divisor;

    // BlockEntityMicroBlock rotates only the four vertical decor slots around
    // Y. The up/down slots retain their source face. Keep this conversion in
    // one place so every decor rendering path uses the same face order.
    private static int GetRotatedDecorSourceFace(int clientFace, int blockRotation)
    {
        return clientFace < 4
            ? Mod(clientFace + blockRotation / 90, 4)
            : clientFace;
    }

    private int FindMicroFaceCoverMaterial(IReadOnlyList<MicroCuboid> cuboids, int currentIndex, int face,
        IReadOnlyList<MicroCuboid>? neighboringCuboids)
    {
        var current = cuboids[currentIndex];
        var a0 = face is 0 or 1 ? current.Y0 : face is 2 or 3 ? current.X0 : current.X0;
        var a1 = face is 0 or 1 ? current.Y1 : face is 2 or 3 ? current.X1 : current.X1;
        var b0 = face is 0 or 1 ? current.Z0 : face is 2 or 3 ? current.Z0 : current.Y0;
        var b1 = face is 0 or 1 ? current.Z1 : face is 2 or 3 ? current.Z1 : current.Y1;
        var firstCoverMaterialId = 0;

        // Voxel coordinates are integer cells in [0, 16]. Testing every cell
        // is small, exact, and handles several adjacent cuboids whose union
        // covers a face even though no single cuboid contains it completely.
        for (var a = a0; a < a1; a++)
        for (var b = b0; b < b1; b++)
        {
            var covered = false;
            var coverMaterialId = 0;
            for (var index = 0; index < cuboids.Count; index++)
            {
                if (index == currentIndex) continue;
                var candidate = cuboids[index];
                var touches = face switch
                {
                    0 => candidate.X1 == current.X0,
                    1 => candidate.X0 == current.X1,
                    2 => candidate.Y1 == current.Y0,
                    3 => candidate.Y0 == current.Y1,
                    4 => candidate.Z1 == current.Z0,
                    5 => candidate.Z0 == current.Z1,
                    _ => false
                };
                if (!touches) continue;

                var contains = face is 0 or 1
                    ? a >= candidate.Y0 && a < candidate.Y1 && b >= candidate.Z0 && b < candidate.Z1
                    : face is 2 or 3
                        ? a >= candidate.X0 && a < candidate.X1 && b >= candidate.Z0 && b < candidate.Z1
                        : a >= candidate.X0 && a < candidate.X1 && b >= candidate.Y0 && b < candidate.Y1;
                if (contains && candidate.MaterialId > 0
                    && materials.CanMergeMicroMaterials(current.MaterialId, candidate.MaterialId))
                {
                    covered = true;
                    coverMaterialId = candidate.MaterialId;
                    break;
                }
            }
            if (!covered && neighboringCuboids is { Count: > 0 })
            {
                foreach (var candidate in neighboringCuboids)
                {
                    var contains = face is 0 or 1
                        ? a >= candidate.Y0 && a < candidate.Y1 && b >= candidate.Z0 && b < candidate.Z1
                        : face is 2 or 3
                            ? a >= candidate.X0 && a < candidate.X1 && b >= candidate.Z0 && b < candidate.Z1
                            : a >= candidate.X0 && a < candidate.X1 && b >= candidate.Y0 && b < candidate.Y1;
                    if (contains && candidate.MaterialId > 0
                        && materials.CanMergeMicroMaterials(current.MaterialId, candidate.MaterialId))
                    {
                        covered = true;
                        coverMaterialId = candidate.MaterialId;
                        break;
                    }
                }
            }
            if (!covered) return 0;
            if (a == a0 && b == b0) firstCoverMaterialId = coverMaterialId;
        }
        return a1 > a0 && b1 > b0 ? firstCoverMaterialId : 0;
    }

    private static int ReadMicroRotation(BlockEntityMicroBlock micro)
    {
        // BlockEntityMicroBlock stores rotationY in the packed tree attribute
        // (rotation = (rotationY + 360) << 10); no public accessor exists.
        // Serializing the already-loaded entity is side-effect free and uses
        // the same field the client reads in FromTreeAttributes.
        try
        {
            var tree = new TreeAttribute();
            micro.ToTreeAttributes(tree);
            var packed = tree.GetInt("rotation", 0);
            if (packed == 0) return 0;
            return ((packed >> 10) & 0x3FF) - 360;
        }
        catch
        {
            return 0;
        }
    }

    private LightSample SampleLight(IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks, int x, int y, int z)
    {
        if (y < 0 || y >= mapSizeY) return new LightSample(0, 0, 0, 1);
        var cx = FloorDiv(x, 32); var cz = FloorDiv(z, 32);
        var lx = x - cx * 32; var lz = z - cz * 32;
        if (!chunks.TryGetValue((cx, y >> 5, cz), out var chunk) || chunk == null)
            return new LightSample(0, 0, 0, NativeDaylight());
        // Older saves and freshly generated rows can contain block data while
        // omitting the entire lighting layer.  ReadLight returns zero for
        // both that case and a genuinely dark cave, so inspect the native
        // ChunkData layer before reading the packed value.  Only the missing
        // layer gets a daylight fallback; a present layer with zero light
        // remains black as recorded by the game.
        if (chunk.Data is ChunkData data && data.lightLayer == null)
            return new LightSample(0, 0, 0, NativeDaylight());
        var index = lx + lz * 32 + (y & 31) * 1024;
        var packed = chunk.Unpack_AndReadLight(index, out var lightSat);
        var sunLevel = packed & 0x1f;
        var blockLevel = (packed >> 5) & 0x1f;
        var hueIndex = packed >> 10;
        var world = reader.WorldMap;
        var sunLevels = world.SunLightLevels;
        var blockLevels = world.BlockLightLevels;
        var hues = world.hueLevels;
        var saturations = world.satLevels;
        // The packed value stores colored HSV block light and sunlight as
        // independent signals. The web shader consumes them independently:
        // block light is an emissive floor, while sunlight controls sky
        // visibility for its directional light and real-time shadow map.
        var sun = sunLevels is { Length: > 0 }
            ? Math.Clamp(sunLevels[Math.Clamp(sunLevel, 0, sunLevels.Length - 1)], 0, 1)
            : sunLevel / 31f;
        var block = blockLevels is { Length: > 0 }
            ? Math.Clamp(blockLevels[Math.Clamp(blockLevel, 0, blockLevels.Length - 1)], 0, 1)
            : blockLevel / 31f;
        var hue = hues is { Length: > 0 }
            ? hues[Math.Clamp(hueIndex, 0, hues.Length - 1)]
            : (byte)Math.Clamp(hueIndex * 4, 0, 255);
        var saturation = saturations is { Length: > 0 }
            ? saturations[Math.Clamp(lightSat, 0, saturations.Length - 1)]
            : (byte)Math.Clamp(lightSat * 32, 0, 255);
        var rgb = ColorUtil.HsvToRgb(hue, saturation, Math.Clamp((int)(block * 255f), 0, 255));
        var blockR = ((rgb >> 16) & 0xff) / 255f;
        var blockG = ((rgb >> 8) & 0xff) / 255f;
        var blockB = (rgb & 0xff) / 255f;
        return new LightSample(blockR, blockG, blockB, sun);
    }

    private float NativeDaylight()
    {
        var world = reader.WorldMap;
        var levels = world.SunLightLevels;
        if (levels is { Length: > 0 })
            return Math.Clamp(levels[Math.Clamp(world.SunBrightness, 0, levels.Length - 1)], 0, 1);
        return .82f;
    }

    private LightSample SampleBlockLight(IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks, int x, int y, int z, int size)
    {
        var result = SampleLight(chunks, x, y, z);
        result = MaxLight(result, SampleLight(chunks, x - size, y, z));
        result = MaxLight(result, SampleLight(chunks, x + size, y, z));
        result = MaxLight(result, SampleLight(chunks, x, y - 1, z));
        result = MaxLight(result, SampleLight(chunks, x, y + 1, z));
        result = MaxLight(result, SampleLight(chunks, x, y, z - size));
        return MaxLight(result, SampleLight(chunks, x, y, z + size));
    }

    private LightSample SampleFaceLight(IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks, int x, int y, int z, int size, int face)
    {
        // The native renderer samples the block itself as well as the block
        // outside the face (and then applies corner AO).  Keeping the current
        // sample here is important for tile borders where the neighbouring
        // chunk row may not be present in the save yet; otherwise a valid
        // face becomes black until a later dirty event rebuilds the tile.
        var current = SampleLight(chunks, x, y, z);
        var adjacent = face switch
        {
            0 => SampleLight(chunks, x - size, y, z),
            1 => SampleLight(chunks, x + size, y, z),
            2 => SampleLight(chunks, x, y - 1, z),
            3 => SampleLight(chunks, x, y + 1, z),
            4 => SampleLight(chunks, x, y, z - size),
            5 => SampleLight(chunks, x, y, z + size),
            _ => current
        };
        return MaxLight(current, adjacent);
    }

    /// <summary>
    /// Samples the four-corner light used by the native smooth-shadow path.
    /// The server does not have the client's tesselator cache, so this keeps
    /// the same inputs (face neighbour, two tangent neighbours and the
    /// diagonal) and the same characteristic 0.67 ambient-occlusion factor.
    /// </summary>
    private LightSample SampleCornerLight(
        IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks,
        int x, int y, int z, int size, BlockMaterialInfo block, int face,
        float pointX, float pointY, float pointZ)
    {
        // Blocks that opt out of side AO still receive the native face light;
        // this matters for glass, thin JSON faces and fluids.
        var baseLight = SampleFaceLight(chunks, x, y, z, size, face);
        if ((uint)face >= (uint)block.SideAo.Length || !block.SideAo[face]) return baseLight;

        var normal = FaceNormals[face];
        var tangentA = face switch
        {
            0 or 1 => (X: 0, Y: 0, Z: 1),
            2 or 3 => (X: 1, Y: 0, Z: 0),
            _ => (X: 1, Y: 0, Z: 0)
        };
        var tangentB = face switch
        {
            0 or 1 or 4 or 5 => (X: 0, Y: 1, Z: 0),
            _ => (X: 0, Y: 0, Z: 1)
        };

        var centerX = x + size * .5f;
        var centerY = y + .5f;
        var centerZ = z + size * .5f;
        var signA = Sign(pointX, pointY, pointZ, centerX, centerY, centerZ, tangentA);
        var signB = Sign(pointX, pointY, pointZ, centerX, centerY, centerZ, tangentB);

        var faceX = x + normal.X * size;
        var faceY = y + normal.Y;
        var faceZ = z + normal.Z * size;
        var sideAX = faceX + tangentA.X * signA * size;
        var sideAY = faceY + tangentA.Y * signA;
        var sideAZ = faceZ + tangentA.Z * signA * size;
        var sideBX = faceX + tangentB.X * signB * size;
        var sideBY = faceY + tangentB.Y * signB;
        var sideBZ = faceZ + tangentB.Z * signB * size;
        var diagonalX = sideAX + tangentB.X * signB * size;
        var diagonalY = sideAY + tangentB.Y * signB;
        var diagonalZ = sideAZ + tangentB.Z * signB * size;

        var hasA = signA != 0;
        var hasB = signB != 0;
        var blockedA = hasA && OccludesAo(chunks, sideAX, sideAY, sideAZ, face);
        var blockedB = hasB && OccludesAo(chunks, sideBX, sideBY, sideBZ, face);
        var blockedDiagonal = hasA && hasB && OccludesAo(chunks, diagonalX, diagonalY, diagonalZ, face);

        var sum = baseLight;
        var count = 1;
        if (hasA && !blockedA)
        {
            sum = Add(sum, SampleAt(chunks, sideAX, sideAY, sideAZ));
            count++;
        }
        if (hasB && !blockedB)
        {
            sum = Add(sum, SampleAt(chunks, sideBX, sideBY, sideBZ));
            count++;
        }
        if (hasA && hasB && !blockedDiagonal && !(blockedA && blockedB))
        {
            sum = Add(sum, SampleAt(chunks, diagonalX, diagonalY, diagonalZ));
            count++;
        }

        var deeplyOccluded = blockedA && blockedB;
        var ao = deeplyOccluded
            ? .67f
            : Math.Max(.67f, 1f - .165f * ((blockedA ? 1 : 0) + (blockedB ? 1 : 0) + (blockedDiagonal ? 1 : 0)));
        // Native CornerAoRGB also limits the darkest corner by the current
        // block's absorption.  This small term preserves that behaviour for
        // non-cube shapes without making a fully opaque block black.
        if (deeplyOccluded)
            ao = Math.Min(ao, 1f - .0196875f * Math.Clamp(block.LightAbsorption, 0, 32));
        return Scale(sum, ao / count);
    }

    private static int Sign(float pointX, float pointY, float pointZ,
        float centerX, float centerY, float centerZ, (int X, int Y, int Z) axis)
    {
        var value = axis.X != 0 ? pointX - centerX
            : axis.Y != 0 ? pointY - centerY
            : pointZ - centerZ;
        if (Math.Abs(value) < .0005f) return 0;
        return value < 0 ? -1 : 1;
    }

    private LightSample SampleAt(IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks,
        float x, float y, float z) => SampleLight(chunks,
            (int)MathF.Round(x), (int)MathF.Round(y), (int)MathF.Round(z));

    private bool OccludesAo(IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks,
        float x, float y, float z, int face)
    {
        var ix = (int)MathF.Round(x);
        var iy = (int)MathF.Round(y);
        var iz = (int)MathF.Round(z);
        var fluid = ReadFluidWorld(chunks, ix, iy, iz);
        var id = fluid != 0 ? fluid : ReadWorld(chunks, ix, iy, iz);
        if (id == 0) return false;
        var block = materials.Get(id);
        if (block.Geometry == BlockGeometryKind.Empty || block.Geometry == BlockGeometryKind.Cross) return false;
        if (block.LightAbsorption > 0) return true;
        // A transparent block can still emit side AO (leaves and several JSON
        // blocks do this in the client).  The queried side is the side facing
        // the block whose corner is being shaded.
        return (block.EmitSideAo & EngineFaceBit(OppositeFace[face])) != 0;
    }

    private static byte EngineFaceBit(int internalFace) => internalFace switch
    {
        0 => 8,   // west -> engine index 3
        1 => 2,   // east -> engine index 1
        2 => 32,  // down -> engine index 5
        3 => 16,  // up -> engine index 4
        4 => 1,   // north -> engine index 0
        5 => 4,   // south -> engine index 2
        _ => 0
    };

    private static LightSample Add(LightSample left, LightSample right) => new(
        left.R + right.R, left.G + right.G, left.B + right.B, left.Sky + right.Sky);

    private static LightSample Scale(LightSample value, float factor) => new(
        Math.Clamp(value.R * factor, 0, 1), Math.Clamp(value.G * factor, 0, 1),
        Math.Clamp(value.B * factor, 0, 1), Math.Clamp(value.Sky * factor, 0, 1));

    private static LightSample MaxLight(LightSample left, LightSample right) => new(
        Math.Max(left.R, right.R), Math.Max(left.G, right.G), Math.Max(left.B, right.B), Math.Max(left.Sky, right.Sky));

    private void AddCuboid(MeshBuffer mesh, float x1, float y1, float z1, float x2, float y2, float z2,
        BlockMaterialInfo info, int west, int east, int bottom, int top, int north, int south,
        IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks, int blockX, int blockY, int blockZ, int size,
        int[]? overrideFaceTiles = null, MicroUvBounds? microUv = null,
        MeshBuffer? overlayMesh = null, int[]? overlayFaceTiles = null)
    {
        float[] points =
        [
            x1,y1,z1, x2,y1,z1, x2,y2,z1, x1,y2,z1,
            x1,y1,z2, x2,y1,z2, x2,y2,z2, x1,y2,z2
        ];
        if (!materials.HidesFace(info, 4, north, 5)) EmitFace(4, FaceTile(info, overrideFaceTiles, 4), 0, 3, 2, 1);
        if (!materials.HidesFace(info, 5, south, 4)) EmitFace(5, FaceTile(info, overrideFaceTiles, 5), 5, 6, 7, 4);
        if (!materials.HidesFace(info, 0, west, 1)) EmitFace(0, FaceTile(info, overrideFaceTiles, 0), 4, 7, 3, 0);
        if (!materials.HidesFace(info, 1, east, 0)) EmitFace(1, FaceTile(info, overrideFaceTiles, 1), 1, 2, 6, 5);
        if (!materials.HidesFace(info, 3, top, 2)) EmitFace(3, FaceTile(info, overrideFaceTiles, 3), 6, 2, 3, 7);
        if (!materials.HidesFace(info, 2, bottom, 3)) EmitFace(2, FaceTile(info, overrideFaceTiles, 2), 4, 0, 1, 5);

        void EmitFace(int face, int tile, int c0, int c1, int c2, int c3)
        {
            if (microUv is { } bounds)
            {
                Face(mesh, points, info, chunks, blockX, blockY, blockZ, size, face, tile, bounds, c0, c1, c2, c3);
            }
            else
            {
                Face(mesh, points, info, chunks, blockX, blockY, blockZ, size, face, tile, c0, c1, c2, c3);
            }

            if (overlayMesh == null || overlayFaceTiles == null
                || (uint)face >= (uint)overlayFaceTiles.Length
                || overlayFaceTiles[face] < 0) return;

            var overlayPoints = (float[])points.Clone();
            var normal = FaceNormals[face];
            var epsilon = .0015f * Math.Max(1, size);
            for (var vertex = 0; vertex < 8; vertex++)
            {
                overlayPoints[vertex * 3] += normal.X * epsilon;
                overlayPoints[vertex * 3 + 1] += normal.Y * epsilon;
                overlayPoints[vertex * 3 + 2] += normal.Z * epsilon;
            }
            FaceTopSoilOverlay(overlayMesh, overlayPoints, info, chunks, blockX, blockY, blockZ, size,
                face, overlayFaceTiles[face], face == 3 ? GameMath.MurmurHash3Mod(blockX, blockY, blockZ, 4) : 0,
                microUv, c0, c1, c2, c3);
        }
    }

    private static int FaceTile(BlockMaterialInfo info, int[]? overrideFaceTiles, int face) =>
        overrideFaceTiles != null && (uint)face < (uint)overrideFaceTiles.Length
            ? overrideFaceTiles[face]
            : info.FaceTiles[face];

    private void AddFluid(MeshBuffer mesh, IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks,
        int x, int y, int z, int size, BlockMaterialInfo info,
        int west, int east, int bottom, int top, int north, int south)
    {
        int Level(int sampleX, int sampleY, int sampleZ)
        {
            var sample = materials.Get(ReadFluidWorld(chunks, sampleX, sampleY, sampleZ));
            var sameLiquid = info.LiquidCode.Length > 0
                ? sample.LiquidCode.Equals(info.LiquidCode, StringComparison.Ordinal)
                : sample.Id == info.Id;
            return sameLiquid ? Math.Clamp(sample.LiquidLevel, 0, 7) : 0;
        }
        int Above(int sampleX, int sampleZ) => Level(sampleX, y + 1, sampleZ) > 0 ? 7 : 0;
        // Block.LiquidLevel is the game's fluid height index: level 7 fills
        // a whole block and level 1 reaches one quarter of a block.  The
        // native BlockWater top position is (level + 1) / 8, not level / 7.
        // The latter makes shallow water too low and exaggerates every join.
        float Height(params int[] values) => (values.Max() + 1) / 8f;

        var current = Math.Clamp(info.LiquidLevel, 1, 7);
        var nw = Height(current, Level(x, y, z - size), Level(x - size, y, z), Level(x - size, y, z - size),
            Above(x, z - size), Above(x - size, z), Above(x - size, z - size));
        var sw = Height(current, Level(x, y, z + size), Level(x - size, y, z), Level(x - size, y, z + size),
            Above(x, z + size), Above(x - size, z), Above(x - size, z + size));
        var ne = Height(current, Level(x, y, z - size), Level(x + size, y, z), Level(x + size, y, z - size),
            Above(x, z - size), Above(x + size, z), Above(x + size, z - size));
        var se = Height(current, Level(x, y, z + size), Level(x + size, y, z), Level(x + size, y, z + size),
            Above(x, z + size), Above(x + size, z), Above(x + size, z + size));
        float[] points =
        [
            x,y,z, x+size,y,z, x+size,y+ne,z, x,y+nw,z,
            x,y,z+size, x+size,y,z+size, x+size,y+se,z+size, x,y+sw,z+size
        ];
        // ServerMap is an above-ground map.  Emit only the exposed top of a
        // liquid column; side faces from every adjacent cell overlap in a
        // translucent pass and produce the visible grid/seam pattern.
        var topLiquid = materials.Get(top);
        var coveredByLiquid = topLiquid.LiquidCode.Length > 0
            && topLiquid.LiquidCode.Equals(info.LiquidCode, StringComparison.Ordinal);
        var coveredBySolid = top != 0 && topLiquid.LiquidCode.Length == 0
            && topLiquid.Geometry != BlockGeometryKind.Empty;
        if (!coveredByLiquid && !coveredBySolid)
            FluidTopFace(mesh, points, chunks, y, info.FaceTiles[3], 6, 2, 3, 7);
    }

    private void FluidTopFace(MeshBuffer mesh, float[] points,
        IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks, int blockY, int tile, params int[] corners)
    {
        var first = mesh.Vertices.Count / 3;
        var uv = materials.Uv(tile);
        float[] faceUvs = [uv.U1, uv.V0, uv.U1, uv.V1, uv.U0, uv.V1, uv.U0, uv.V0];
        for (var index = 0; index < 4; index++)
        {
            var corner = corners[index];
            var pointX = points[corner * 3];
            var pointY = points[corner * 3 + 1];
            var pointZ = points[corner * 3 + 2];
            mesh.Vertices.Add(pointX); mesh.Vertices.Add(pointY); mesh.Vertices.Add(pointZ);
            // Every liquid cell owns separate vertices. Sampling the source
            // cell's face light causes two geometrically identical boundary
            // vertices to differ in colour, which reads as a grid on still
            // water. Sample the four cells around the world-space corner so
            // both owners write the same light value.
            AddLight(mesh, SampleFluidSurfaceLight(chunks, (int)MathF.Round(pointX), blockY, (int)MathF.Round(pointZ)));
            mesh.Uvs.Add(faceUvs[index * 2]); mesh.Uvs.Add(faceUvs[index * 2 + 1]);
        }
        mesh.Indices.Add(first); mesh.Indices.Add(first + 1); mesh.Indices.Add(first + 2);
        mesh.Indices.Add(first); mesh.Indices.Add(first + 2); mesh.Indices.Add(first + 3);
    }

    private LightSample SampleFluidSurfaceLight(IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks,
        int worldX, int worldY, int worldZ)
    {
        var light = new LightSample(0, 0, 0, 0);
        // A water vertex lies on the border of these four horizontal cells.
        // Include both the liquid row and the air/cover row above it so the
        // result is stable at shores, tile edges and level transitions.
        for (var offsetX = -1; offsetX <= 0; offsetX++)
        for (var offsetZ = -1; offsetZ <= 0; offsetZ++)
        {
            light = MaxLight(light, SampleLight(chunks, worldX + offsetX, worldY, worldZ + offsetZ));
            light = MaxLight(light, SampleLight(chunks, worldX + offsetX, worldY + 1, worldZ + offsetZ));
        }
        return light;
    }

    private void AddCross(MeshBuffer mesh, int x, int y, int z, int size, BlockMaterialInfo info, LightSample light)
    {
        // Match CrossTesselator.DrawCross, including the side texture choice,
        // 1.41-high sprite and its distinct UV order.  Cube UVs make foliage
        // appear upside down, while omitting the native rotation matrix mirrors
        // randomly rotated plants around the Y axis.
        const float foliageHeight = 1.41f;
        var rotation = info.SelectRandomRotation(x, y, z);
        var radians = rotation * MathF.PI / 180f;
        var sin = MathF.Sin(radians);
        var cos = MathF.Cos(radians);

        (float X, float Y, float Z) Rotate(float px, float py, float pz)
        {
            var dx = px - .5f;
            var dz = pz - .5f;
            // Vintage Story's Mat4f.RotateY maps +X toward -Z for positive
            // angles. This sign convention must match the native tesselator.
            return (.5f + cos * dx + sin * dz, py, .5f - sin * dx + cos * dz);
        }

        void Quad(int diagonal, int tile)
        {
            var bottomRight = diagonal == 0 ? Rotate(1, 0, 1) : Rotate(0, 0, 1);
            var topRight = diagonal == 0 ? Rotate(1, foliageHeight, 1) : Rotate(0, foliageHeight, 1);
            var topLeft = diagonal == 0 ? Rotate(0, foliageHeight, 0) : Rotate(1, foliageHeight, 0);
            var bottomLeft = diagonal == 0 ? Rotate(0, 0, 0) : Rotate(1, 0, 0);
            var points = new float[]
            {
                x + bottomRight.X * size, y + bottomRight.Y, z + bottomRight.Z * size,
                x + topRight.X * size, y + topRight.Y, z + topRight.Z * size,
                x + topLeft.X * size, y + topLeft.Y, z + topLeft.Z * size,
                x + bottomLeft.X * size, y + bottomLeft.Y, z + bottomLeft.Z * size
            };
            FaceWithCrossUv(mesh, points, tile, light);
        }

        Quad(0, info.FaceTiles[4]);
        Quad(1, info.FaceTiles[5]);
    }

    private void FaceWithCrossUv(MeshBuffer mesh, float[] points, int tile, LightSample light)
    {
        var first = mesh.Vertices.Count / 3;
        var uv = materials.Uv(tile);
        // Cross vertices are emitted bottom-right, top-right, top-left,
        // bottom-left.  Uv() exposes V0 as the atlas bottom and V1 as top;
        // preserve that orientation so foliage is not vertically flipped.
        float[] faceUvs = [uv.U1, uv.V0, uv.U1, uv.V1, uv.U0, uv.V1, uv.U0, uv.V0];
        for (var index = 0; index < 4; index++)
        {
            var corner = index * 3;
            mesh.Vertices.Add(points[corner]); mesh.Vertices.Add(points[corner + 1]); mesh.Vertices.Add(points[corner + 2]);
            AddLight(mesh, light);
            mesh.Uvs.Add(faceUvs[index * 2]); mesh.Uvs.Add(faceUvs[index * 2 + 1]);
        }
        mesh.Indices.Add(first); mesh.Indices.Add(first + 1); mesh.Indices.Add(first + 2);
        mesh.Indices.Add(first); mesh.Indices.Add(first + 2); mesh.Indices.Add(first + 3);
    }

    private void AddShape(MeshBuffer mesh, int x, int y, int z, int size, BlockMaterialInfo info,
        int[] neighbors, IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks, LightSample light,
        int[]? overrideFaceTiles = null, bool useRandomVariant = true,
        ShapeTemplate[]? shapeOverride = null, DynamicShapeState? dynamicState = null,
        MeshBuffer[]? layerMeshes = null)
    {
        var shape = shapeOverride != null
            ? shapeOverride.FirstOrDefault()
            : useRandomVariant ? info.SelectShape(x, y, z) : info.Shapes.FirstOrDefault();
        if (shape == null) return;
        var rotation = useRandomVariant ? info.SelectShapeRotation(x, y, z) : 0;
        var radians = rotation * MathF.PI / 180f;
        var sin = MathF.Sin(radians);
        var cos = MathF.Cos(radians);
        foreach (var face in shape.Faces)
        {
            // Only a face baked exactly on the source block boundary may be
            // hidden by a neighboring block. A geometric normal is not a
            // boundary test: sloped faces, support beams and internal faces
            // can point along an axis while remaining inside the block.
            var boundaryFace = dynamicState != null || face.BoundaryFace < 0
                ? -1
                : RotatedFace(face.BoundaryFace, sin, cos);
            if (boundaryFace >= 0
                && materials.HidesFace(info, boundaryFace, neighbors[boundaryFace], OppositeFace[boundaryFace])) continue;
            var points = new float[12];
            for (var vertex = 0; vertex < 4; vertex++)
            {
                var localX = face.Vertices[vertex * 3];
                var localY = face.Vertices[vertex * 3 + 1];
                var localZ = face.Vertices[vertex * 3 + 2];
                if (rotation != 0)
                {
                    var dx = localX - .5f;
                    var dz = localZ - .5f;
                    // Match Mat4f.RotateY: positive angles rotate +X to -Z.
                    localX = .5f + cos * dx + sin * dz;
                    localZ = .5f - sin * dx + cos * dz;
                }
                if (dynamicState is { } state)
                {
                    if (state.DoorOpened)
                    {
                        // BEBehaviorDoor's client path first evaluates the
                        // "opened" root animation, then applies the saved
                        // hinge rotation and finally mirrors inverted-door
                        // handles around the block centre.
                        (localX, localY, localZ) = RotateAround(localX, localY, localZ,
                            state.DoorOpenedOriginX, state.DoorOpenedOriginY, state.DoorOpenedOriginZ,
                            state.DoorOpenedRotateX, state.DoorOpenedRotateY, state.DoorOpenedRotateZ);
                    }
                    var rotateY = state.DoorOpened || state.DoorInvertHandles
                        ? (state.DoorInvertHandles ? -state.RotateY : state.RotateY)
                        : state.RotateY;
                    (localX, localY, localZ) = RotateDynamic(localX, localY, localZ,
                        state.DoorOpened ? 0 : state.RotateX, rotateY,
                        state.DoorOpened ? 0 : state.RotateZ);
                    if (state.DoorInvertHandles)
                    {
                        localX = 1f - localX;
                    }
                    localY *= state.ScaleY;
                    localX += state.OffsetX / Math.Max(1, size);
                    localY += state.OffsetY;
                    localZ += state.OffsetZ / Math.Max(1, size);
                }
                points[vertex * 3] = x + localX * size;
                points[vertex * 3 + 1] = y + localY;
                points[vertex * 3 + 2] = z + localZ * size;
            }

            var lightFace = boundaryFace >= 0 ? boundaryFace : DominantFace(points);
            var targetMesh = layerMeshes == null
                ? mesh
                : layerMeshes[(int)ResolveShapeLayer(face.RenderPass, info.Layer)];
            var first = targetMesh.Vertices.Count / 3;
            for (var vertex = 0; vertex < 4; vertex++)
            {
                var pointX = points[vertex * 3];
                var pointY = points[vertex * 3 + 1];
                var pointZ = points[vertex * 3 + 2];
                targetMesh.Vertices.Add(pointX);
                targetMesh.Vertices.Add(pointY);
                targetMesh.Vertices.Add(pointZ);
                if (lightFace >= 0)
                {
                    AddLight(targetMesh, SampleCornerLight(chunks, x, y, z, size, info, lightFace,
                        pointX, pointY, pointZ));
                }
                else
                {
                    AddLight(targetMesh, light);
                }
                var tile = overrideFaceTiles != null
                    ? overrideFaceTiles[Math.Clamp(boundaryFace >= 0 ? boundaryFace : 3, 0, overrideFaceTiles.Length - 1)]
                    : face.Tile;
                // ShapeTesselator writes V in the game atlas's top-origin
                // space. MaterialCatalog maps browser UVs from the bottom,
                // so retain U but invert V here. Cross blocks use their own
                // native path and therefore must not share this conversion.
                var uv = materials.Uv(tile, face.Uvs[vertex * 2], 1f - face.Uvs[vertex * 2 + 1]);
                targetMesh.Uvs.Add(uv.U); targetMesh.Uvs.Add(uv.V);
            }
            targetMesh.Indices.Add(first); targetMesh.Indices.Add(first + 1); targetMesh.Indices.Add(first + 2);
            targetMesh.Indices.Add(first); targetMesh.Indices.Add(first + 2); targetMesh.Indices.Add(first + 3);
        }
    }

    private static MeshMaterialLayer ResolveShapeLayer(short renderPass, MeshMaterialLayer fallback)
    {
        if (renderPass < 0) return fallback;
        return (EnumChunkRenderPass)renderPass switch
        {
            EnumChunkRenderPass.Transparent or EnumChunkRenderPass.BlendNoCull => MeshMaterialLayer.Translucent,
            EnumChunkRenderPass.Liquid => MeshMaterialLayer.Liquid,
            EnumChunkRenderPass.OpaqueNoCull or EnumChunkRenderPass.OpaqueWaterPlant
                or EnumChunkRenderPass.TopSoil or EnumChunkRenderPass.Decor => MeshMaterialLayer.Cutout,
            _ => fallback
        };
    }

    private static (float X, float Y, float Z) RotateDynamic(float x, float y, float z,
        float rotateX, float rotateY, float rotateZ)
    {
        Span<float> matrix = stackalloc float[16];
        Mat4f.RotateXYZ(matrix, rotateX, rotateY, rotateZ);
        var px = x - .5f;
        var py = y - .5f;
        var pz = z - .5f;
        return (
            matrix[0] * px + matrix[4] * py + matrix[8] * pz + .5f,
            matrix[1] * px + matrix[5] * py + matrix[9] * pz + .5f,
            matrix[2] * px + matrix[6] * py + matrix[10] * pz + .5f);
    }

    private static (float X, float Y, float Z) RotateAround(float x, float y, float z,
        float originX, float originY, float originZ,
        float rotateX, float rotateY, float rotateZ)
    {
        Span<float> matrix = stackalloc float[16];
        Mat4f.RotateXYZ(matrix, rotateX, rotateY, rotateZ);
        var px = x - originX;
        var py = y - originY;
        var pz = z - originZ;
        return (
            matrix[0] * px + matrix[4] * py + matrix[8] * pz + originX,
            matrix[1] * px + matrix[5] * py + matrix[9] * pz + originY,
            matrix[2] * px + matrix[6] * py + matrix[10] * pz + originZ);
    }

    private static int RotatedFace(int face, float sin, float cos)
    {
        var normal = FaceNormals[face];
        return RotatedFace(normal.X, normal.Y, normal.Z, sin, cos);
    }

    private static int RotatedFace(float[] normal, float sin, float cos)
        => RotatedFace(normal[0], normal[1], normal[2], sin, cos);

    private static int RotatedFace(float normalX, float normalY, float normalZ, float sin, float cos)
    {
        var x = cos * normalX + sin * normalZ;
        var y = normalY;
        var z = -sin * normalX + cos * normalZ;
        var bestFace = -1;
        var bestDot = float.NegativeInfinity;
        for (var candidate = 0; candidate < FaceNormals.Length; candidate++)
        {
            var target = FaceNormals[candidate];
            var dot = x * target.X + y * target.Y + z * target.Z;
            if (dot <= bestDot) continue;
            bestDot = dot;
            bestFace = candidate;
        }
        return bestFace;
    }

    private static int DominantFace(float[] points)
    {
        var ax = points[3] - points[0];
        var ay = points[4] - points[1];
        var az = points[5] - points[2];
        var bx = points[6] - points[0];
        var by = points[7] - points[1];
        var bz = points[8] - points[2];
        var nx = ay * bz - az * by;
        var ny = az * bx - ax * bz;
        var nz = ax * by - ay * bx;
        var absX = Math.Abs(nx);
        var absY = Math.Abs(ny);
        var absZ = Math.Abs(nz);
        if (Math.Max(absX, Math.Max(absY, absZ)) < .000001f) return -1;
        if (absX >= absY && absX >= absZ) return nx < 0 ? 0 : 1;
        if (absY >= absZ) return ny < 0 ? 2 : 3;
        return nz < 0 ? 4 : 5;
    }

    private static int DominantFace((float X, float Y, float Z) normal)
    {
        var absX = MathF.Abs(normal.X);
        var absY = MathF.Abs(normal.Y);
        var absZ = MathF.Abs(normal.Z);
        if (absX >= absY && absX >= absZ) return normal.X < 0 ? 0 : 1;
        if (absY >= absZ) return normal.Y < 0 ? 2 : 3;
        return normal.Z < 0 ? 4 : 5;
    }

    private void Face(MeshBuffer mesh, float[] points, BlockMaterialInfo block,
        IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks,
        int blockX, int blockY, int blockZ, int size, int face, int tile, params int[] corners)
        => FaceInternal(mesh, points, block, chunks, blockX, blockY, blockZ, size, face, tile, null, corners);

    private void Face(MeshBuffer mesh, float[] points, BlockMaterialInfo block,
        IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks,
        int blockX, int blockY, int blockZ, int size, int face, int tile, MicroUvBounds microUv, params int[] corners)
        => FaceInternal(mesh, points, block, chunks, blockX, blockY, blockZ, size, face, tile, microUv, corners);

    private void FaceTopSoilOverlay(MeshBuffer mesh, float[] points, BlockMaterialInfo block,
        IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks,
        int blockX, int blockY, int blockZ, int size, int face, int tile, int rotation,
        MicroUvBounds? microUv, params int[] corners)
    {
        var first = mesh.Vertices.Count / 3;
        var uv = materials.Uv(tile);
        var faceUvs = microUv == null && face == 3
            ? TopSoilRotatedUvs(uv, rotation)
            : new[] { uv.U1, uv.V0, uv.U1, uv.V1, uv.U0, uv.V1, uv.U0, uv.V0 };
        for (var index = 0; index < 4; index++)
        {
            var corner = corners[index];
            var pointX = points[corner * 3];
            var pointY = points[corner * 3 + 1];
            var pointZ = points[corner * 3 + 2];
            mesh.Vertices.Add(pointX); mesh.Vertices.Add(pointY); mesh.Vertices.Add(pointZ);
            AddLight(mesh, SampleCornerLight(chunks, blockX, blockY, blockZ, size, block, face,
                pointX, pointY, pointZ));
            if (microUv is { } bounds)
            {
                var clientFace = ServerToClientMicroFace[face];
                var uvIndex = MicroUvIndex(clientFace, index);
                var cubeU = ClientCubeUvCoords[uvIndex];
                var cubeV = ClientCubeUvCoords[uvIndex + 1];
                var (localU, localV) = RelativeMicroUv(bounds, clientFace, cubeU, cubeV);
                var mapped = materials.Uv(tile, localU, localV);
                mesh.Uvs.Add(mapped.U); mesh.Uvs.Add(mapped.V);
            }
            else
            {
                mesh.Uvs.Add(faceUvs[index * 2]); mesh.Uvs.Add(faceUvs[index * 2 + 1]);
            }
        }
        mesh.Indices.Add(first); mesh.Indices.Add(first + 1); mesh.Indices.Add(first + 2);
        mesh.Indices.Add(first); mesh.Indices.Add(first + 2); mesh.Indices.Add(first + 3);
    }

    private static float[] TopSoilRotatedUvs((float U0, float V0, float U1, float V1) uv, int rotation)
    {
        var normalized = ((rotation % 4) + 4) % 4;
        return normalized switch
        {
            // TopsoilTesselator rotates the coverage texture in UV space;
            // the base soil face keeps its normal cube orientation.
            1 => [uv.U0, uv.V0, uv.U0, uv.V1, uv.U1, uv.V1, uv.U1, uv.V0],
            2 => [uv.U1, uv.V1, uv.U1, uv.V0, uv.U0, uv.V0, uv.U0, uv.V1],
            3 => [uv.U0, uv.V1, uv.U0, uv.V0, uv.U1, uv.V0, uv.U1, uv.V1],
            _ => [uv.U1, uv.V0, uv.U1, uv.V1, uv.U0, uv.V1, uv.U0, uv.V0]
        };
    }

    private void FaceInternal(MeshBuffer mesh, float[] points, BlockMaterialInfo block,
        IReadOnlyDictionary<(int X, int Y, int Z), ServerChunk?> chunks,
        int blockX, int blockY, int blockZ, int size, int face, int tile, MicroUvBounds? microUv, int[] corners)
    {
        var first = mesh.Vertices.Count / 3;
        var uv = materials.Uv(tile);
        // CubeMeshUtil uses one UV order for every face. The explicit corner
        // order above carries the native face orientation.
        float[] faceUvs = [uv.U1, uv.V0, uv.U1, uv.V1, uv.U0, uv.V1, uv.U0, uv.V0];
        for (var index = 0; index < 4; index++)
        {
            var corner = corners[index];
            var pointX = points[corner * 3];
            var pointY = points[corner * 3 + 1];
            var pointZ = points[corner * 3 + 2];
            mesh.Vertices.Add(pointX); mesh.Vertices.Add(pointY); mesh.Vertices.Add(pointZ);
            AddLight(mesh, SampleCornerLight(chunks, blockX, blockY, blockZ, size, block, face,
                pointX, pointY, pointZ));
            if (microUv is { } bounds)
            {
                var clientFace = ServerToClientMicroFace[face];
                var uvIndex = MicroUvIndex(clientFace, index);
                var cubeU = ClientCubeUvCoords[uvIndex];
                var cubeV = ClientCubeUvCoords[uvIndex + 1];
                var (localU, localV) = RelativeMicroUv(bounds, clientFace, cubeU, cubeV);
                var mapped = materials.Uv(tile, localU, localV);
                mesh.Uvs.Add(mapped.U); mesh.Uvs.Add(mapped.V);
            }
            else
            {
                mesh.Uvs.Add(faceUvs[index * 2]); mesh.Uvs.Add(faceUvs[index * 2 + 1]);
            }
        }
        mesh.Indices.Add(first); mesh.Indices.Add(first + 1); mesh.Indices.Add(first + 2);
        mesh.Indices.Add(first); mesh.Indices.Add(first + 2); mesh.Indices.Add(first + 3);
    }

    private static (float U, float V) RelativeMicroUv(MicroUvBounds bounds, int clientFace, float cubeU, float cubeV)
    {
        // BlockFacing.Axis is X for east/west, Y for up/down and Z for
        // north/south. These are the exact size/offset branches in the
        // client's BlockEntityMicroBlock.GenFace implementation.
        var uSize = clientFace is 1 or 3 ? bounds.Z1 - bounds.Z0 : bounds.X1 - bounds.X0;
        var vSize = clientFace is 4 or 5 ? bounds.Z1 - bounds.Z0 : bounds.Y1 - bounds.Y0;
        var uOffset = clientFace is 1 or 3 ? bounds.Z0 : bounds.X0;
        var vOffset = clientFace is 4 or 5 ? bounds.Z0 : bounds.Y0;

        return clientFace switch
        {
            0 or 1 => ((cubeU - 1f) * uSize + 1f - uOffset,
                -cubeV * vSize + 1f - vOffset),
            2 or 3 => (cubeU * uSize + uOffset,
                -cubeV * vSize + 1f - vOffset),
            4 => (-cubeU * uSize + 1f - uOffset,
                (cubeV - 1f) * vSize + 1f - vOffset),
            5 => ((cubeU - 1f) * uSize + 1f - uOffset,
                (1f - cubeV) * vSize + vOffset),
            _ => (cubeU, 1f - cubeV)
        };
    }

    private static int MicroUvIndex(int clientFace, int vertex)
    {
        return clientFace * 8 + vertex * 2;
    }

    private static void AddLight(MeshBuffer mesh, LightSample light)
    {
        mesh.Lights.Add(ToByte(light.R));
        mesh.Lights.Add(ToByte(light.G));
        mesh.Lights.Add(ToByte(light.B));
        mesh.Lights.Add(ToByte(light.Sky));
    }

    private static int FloorDiv(int value, int divisor) => value >= 0 ? value / divisor : (value - divisor + 1) / divisor;

    private static void WriteLayer(Stream target, MeshBuffer mesh)
    {
        var vertexCount = mesh.Vertices.Count / 3;
        if (mesh.Lights.Count != vertexCount * 4)
            throw new InvalidDataException($"Mesh light payload mismatch ({mesh.Lights.Count} bytes for {vertexCount} vertices).");
        Write(target, vertexCount);
        Write(target, mesh.Indices.Count);
        var useShortIndices = vertexCount <= ushort.MaxValue;
        target.WriteByte(useShortIndices ? (byte)1 : (byte)0);
        if (mesh.Empty)
        {
            for (var i = 0; i < 3; i++) Write(target, 0f);
            for (var i = 0; i < 3; i++) Write(target, 1f);
            return;
        }

        var minX = float.PositiveInfinity; var minY = float.PositiveInfinity; var minZ = float.PositiveInfinity;
        var maxX = float.NegativeInfinity; var maxY = float.NegativeInfinity; var maxZ = float.NegativeInfinity;
        for (var index = 0; index < vertexCount; index++)
        {
            var x = mesh.Vertices[index * 3]; var y = mesh.Vertices[index * 3 + 1]; var z = mesh.Vertices[index * 3 + 2];
            minX = Math.Min(minX, x); minY = Math.Min(minY, y); minZ = Math.Min(minZ, z);
            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); maxZ = Math.Max(maxZ, z);
        }
        var scaleX = Math.Max((maxX - minX) / ushort.MaxValue, 1f / 65535f);
        var scaleY = Math.Max((maxY - minY) / ushort.MaxValue, 1f / 65535f);
        var scaleZ = Math.Max((maxZ - minZ) / ushort.MaxValue, 1f / 65535f);
        Write(target, minX); Write(target, minY); Write(target, minZ);
        Write(target, scaleX); Write(target, scaleY); Write(target, scaleZ);
        for (var index = 0; index < vertexCount; index++)
        {
            Write(target, Quantize(mesh.Vertices[index * 3], minX, scaleX));
            Write(target, Quantize(mesh.Vertices[index * 3 + 1], minY, scaleY));
            Write(target, Quantize(mesh.Vertices[index * 3 + 2], minZ, scaleZ));
        }
        target.Write(mesh.Lights.ToArray());
        foreach (var uv in mesh.Uvs) Write(target, (ushort)Math.Clamp((int)Math.Round(Math.Clamp(uv, 0, 1) * ushort.MaxValue), 0, ushort.MaxValue));
        if (useShortIndices) foreach (var index in mesh.Indices) Write(target, (ushort)index);
        else foreach (var index in mesh.Indices) Write(target, index);
    }

    private static ushort Quantize(float value, float origin, float scale) =>
        (ushort)Math.Clamp((int)Math.Round((value - origin) / scale), 0, ushort.MaxValue);
    private static byte ToByte(float value) => (byte)Math.Clamp((int)Math.Round(value * 255f), 0, 255);
    private static void Write(Stream stream, int value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, value); stream.Write(bytes); }
    private static void Write(Stream stream, float value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(bytes, value); stream.Write(bytes); }
    private static void Write(Stream stream, ushort value) { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, value); stream.Write(bytes); }
}
