// Rendering/shadow portions adapted from VS-LiveMap-Revival (MIT).
// Copyright (c) 2024 William Blake Galbreath. See VS-LiveMap-Revival-LICENSE.txt.
using ServerMap.World;
using ServerMap.Util;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.GameContent;
using Vintagestory.Server;

namespace ServerMap.Render;

public sealed class MapRenderer
{
    private readonly WorldDatabaseReader reader;
    private readonly string root;
    private readonly int mapSizeY;
    private readonly MapPalette materials;
    public MapRenderer(WorldDatabaseReader reader, string root, int mapSizeY, MapPalette materials) { this.reader = reader; this.root = root; this.mapSizeY = mapSizeY; this.materials = materials; }
    public sealed class ColumnGenerationIncompleteException : IOException { }
    public long ExtractedColumns, ReusedColumns;
    public SurfaceRegion Extract(ChunkKey key, SurfaceRegion? previous = null, ISet<int>? columns = null, bool verify = false, bool singleColumn = false)
    {
        var size = singleColumn ? 32 : 512;
        var surface = previous ?? new SurfaceRegion(size);
        var heights = surface.Heights; var hasData = surface.Valid;
        var ids = surface.Codes.Select(materials.ResolveCode).ToArray();
        var entityKeys = surface.EntityKeys;


        var stableColumns = 0;
        for (var chunkOffsetX = 0; chunkOffsetX < 16; chunkOffsetX++) for (var chunkOffsetZ = 0; chunkOffsetZ < 16; chunkOffsetZ++)
        {
            var cx = key.X * 16 + chunkOffsetX;
            var cz = key.Z * 16 + chunkOffsetZ;
            var columnIndex = chunkOffsetX * 16 + chunkOffsetZ;
            if (columns != null && !columns.Contains(columnIndex)) continue;
            using var snapshot = reader.BeginSnapshot();
            var fingerprint = reader.ColumnFingerprint(cx, cz);
            if (verify && surface.Fingerprints[columnIndex] == fingerprint) { Interlocked.Increment(ref ReusedColumns); continue; }
            for (var px = 0; px < 32; px++) for (var pz = 0; pz < 32; pz++)
            {
                var pi = ((singleColumn ? 0 : chunkOffsetZ) * 32 + pz) * size + (singleColumn ? 0 : chunkOffsetX) * 32 + px;
                hasData[pi] = false; ids[pi] = 0; heights[pi] = 0; entityKeys[pi] = "";
            }
            surface.Columns[columnIndex] = false;
            surface.Fingerprints[columnIndex] = fingerprint;
            var map = reader.ReadSavedMapChunk(new ChunkKey(cx, 0, cz));
            if (map == null) continue;
            // Match LiveMap's rule: an unfinished mapchunk is skipped, while
            // other completed columns in the same region remain renderable.
            // Aborting the whole 512x512 tile here meant one actively
            // generating column prevented every 2D tile from ever being
            // written in partially explored worlds.
            if (map.CurrentIncompletePass < EnumWorldGenPass.Done) throw new ColumnGenerationIncompleteException();
            surface.Columns[columnIndex] = true;
            stableColumns++; Interlocked.Increment(ref ExtractedColumns);
            // LiveMap samples the rain height map, then reads the engine's
            // default block layer (solid, falling back to fluid).  Loading a
            // broad terrain/scan-above range selected different blocks and
            // was the source of many apparent colour mismatches.
            var rainValues = map.RainHeightMap;
            var terrainValues = map.WorldGenTerrainHeightMap;
            const int minTop = 0;
            var maxTop = Math.Min(mapSizeY - 1, rainValues?.Max(value => (int)value) ?? terrainValues?.Max(value => (int)value) ?? 0);
            var chunks = new Dictionary<int, ServerChunk?>();
            foreach (var chunkY in reader.ChunkYs(cx, cz))
            {
                if (chunkY * 32 > maxTop || (chunkY + 1) * 32 - 1 < minTop) continue;
                chunks[chunkY] = reader.LoadChunk(new ChunkKey(cx, chunkY, cz));
            }
            try
            {
                for (var x = 0; x < 32; x++) for (var z = 0; z < 32; z++)
                {
                    var index = z * 32 + x;
                    var y = Math.Clamp((int)(rainValues?[index] ?? terrainValues?[index] ?? 0), 0, mapSizeY - 1);
                    var solidId = ReadMapBlock(chunks, cx, cz, x, y, z);
                    var fluidId = ReadFluid(chunks, x, y, z);
                    // A column's default layer is solid.  Vintage Story stores
                    // water and lava separately, however, so an air/default
                    // block must fall back to the fluid layer at the same cell.
                    var id = solidId;
                    if (id == 0 || materials.IsEmpty(id) || materials.IsPlaceholder(id)) id = fluidId;
                    // LiveMap's BlocksToIgnore contains snow overlays.  It
                    // selects the block immediately underneath and lowers the
                    // reported height by one, rather than repeatedly scanning
                    // down until an arbitrary non-snow block is found.
                    if (id != MapPalette.MissingBlockId && materials.IsSurfaceCover(id))
                    {
                        y = Math.Max(0, y - 1);
                        solidId = ReadMapBlock(chunks, cx, cz, x, y, z);
                        fluidId = ReadFluid(chunks, x, y, z);
                        id = solidId == 0 || materials.IsEmpty(solidId) || materials.IsPlaceholder(solidId) ? fluidId : solidId;
                    }
                    if (id == 0 || materials.IsEmpty(id) || materials.IsPlaceholder(id))
                    {
                        // RainHeightMap may point at an air cell above a cave
                        // entrance. Search the complete saved column, not an
                        // arbitrary eight-block window, so the visible cave
                        // floor still receives a map pixel.
                        for (var sampleY = y - 1; sampleY >= 0; sampleY--)
                        {
                            solidId = ReadMapBlock(chunks, cx, cz, x, sampleY, z);
                            fluidId = ReadFluid(chunks, x, sampleY, z);
                            var candidate = solidId == 0 || materials.IsEmpty(solidId) || materials.IsPlaceholder(solidId) ? fluidId : solidId;
                            if (candidate == MapPalette.MissingBlockId) break;
                            if (candidate == 0 || materials.IsEmpty(candidate) || materials.IsPlaceholder(candidate)) continue;
                            id = candidate; y = sampleY; break;
                        }
                    }
                    var pixelX = (singleColumn ? 0 : chunkOffsetX) * 32 + x;
                    var pixelZ = (singleColumn ? 0 : chunkOffsetZ) * 32 + z;
                    var pixel = pixelZ * size + pixelX;
                    if (materials.Get(id).IsRoof && chunks.TryGetValue(y >> 5, out var roofChunk) && roofChunk != null
                        && roofChunk.BlockEntities.TryGetValue(new BlockPos(cx * 32 + x, y, cz * 32 + z), out var roofEntity)
                        && materials.Roofing is { } roofing)
                    {
                        var roofKey = roofing.Resolve(roofEntity, out var infillId);
                        if (entityKeys != null) entityKeys[pixel] = roofKey ?? "";
                        if (infillId > 0) id = reader.ResolveMetaBlockLayer(cx * 32 + x, y, cz * 32 + z, infillId) ?? MapPalette.MissingBlockId;
                    }
                    if (materials.Get(id).IsGroundStorage && materials.GroundStorage is { } storage
                        && chunks.TryGetValue(y >> 5, out var storageChunk) && storageChunk != null
                        && storageChunk.BlockEntities.TryGetValue(new BlockPos(cx * 32 + x, y, cz * 32 + z), out var storageEntity))
                        entityKeys![pixel] = storage.Resolve(storageEntity, cx * 32 + x, y, cz * 32 + z) ?? "";
                    hasData[pixel] = true;
                    heights[pixel] = (ushort)y;
                    if (id == MapPalette.MissingBlockId)
                    {
                        ids[pixel] = id;
                        continue;
                    }
                    if (id == 0 || materials.IsEmpty(id))
                    {

                        continue;
                    }
                    ids[pixel] = id;
                }
            }
            finally { foreach (var chunk in chunks.Values) chunk?.Dispose(); }
        }


        for (var i = 0; i < ids.Length; i++)
        {
            surface.Codes[i] = materials.Get(ids[i]).Code;
            surface.SepiaKeys[i] = materials.Get(ids[i]).MapColorCode;
            surface.Water[i] = materials.IsMapWaterBlock(ids[i]);
        }
        return surface;
    }
    public bool Render2D(ChunkKey key, string renderer = "basic") => RenderSurface(key, Extract(key), renderer);
    public bool RenderSurface(ChunkKey key, SurfaceRegion surface, string renderer = "basic")
    {
        var colored = renderer.Equals("basic", StringComparison.OrdinalIgnoreCase);
        var colors = materials.CaptureColors();
        if (colored && colors == null) return false;
        const int size = 512;
        var pixels = new byte[size * size * 4]; var heights = surface.Heights; var hasData = surface.Valid;
        var ids = surface.Codes.Select(materials.ResolveCode).ToArray(); var entityKeys = surface.EntityKeys;
        var roofPixels = 0;
        var missingRoofColors = 0;
        var storagePixels = 0;
        var missingStorageColors = 0;
        for (var pixelZ = 0; pixelZ < size; pixelZ++) for (var pixelX = 0; pixelX < size; pixelX++)
        {
            var pixel = pixelZ * size + pixelX;
            var id = ids[pixel];
            if (id == 0) continue;
            if (id == MapPalette.MissingBlockId)
            {
                // A missing palette entry is an incomplete world column, not
                // black terrain. Leave it transparent so the map background
                // remains visible rather than producing opaque black blocks.
                continue;
            }
            // LiveMap keeps the selected top block even when the client
            // colormap has no entry for it. Its BasicRenderer leaves that
            // pixel transparent instead of falling through to a lower block.
            // LiveMap uses its explicit water-edge colour where a water/ice
            // column touches a non-water column.  Empty cells outside a region
            // count as water, matching BlockData.Get's null behaviour.
            (byte R, byte G, byte B) entityColor = default;
            var entityColored = colored && (colors!.TryRoofColor(entityKeys![pixel], pixelX, heights[pixel], pixelZ, out entityColor)
                || colors.TryGroundColor(entityKeys![pixel], pixelX, heights[pixel], pixelZ, out entityColor));
            if (colored && materials.Get(id).IsRoof)
            {
                roofPixels++;
                if (!entityColored) missingRoofColors++;
            }
            if (colored && materials.Get(id).IsGroundStorage)
            {
                storagePixels++;
                if (!entityColored) missingStorageColors++;
            }
            if (colored && !entityColored && (materials.Get(id).IsRoof || materials.Get(id).IsGroundStorage || !colors!.HasColor(id))) continue;
            var mapColor = !colored && surface.Water[pixel] && IsWaterEdge(surface.Water, hasData, pixelX, pixelZ, size)
                ? (R: (byte)72, G: (byte)48, B: (byte)24)
                : colored
                    // LiveMap hashes colour variants in region-local coordinates.
                    // Using absolute world coordinates changes the selected one
                    // of the 30 client colours and makes the same block look wrong.
                    ? entityColored ? entityColor : colors!.Color(id, pixelX, heights[pixel], pixelZ)
                    : MapPalette.ColorFor(surface.SepiaKeys[pixel]);
            var offset = pixel * 4;
            pixels[offset] = mapColor.R; pixels[offset + 1] = mapColor.G; pixels[offset + 2] = mapColor.B; pixels[offset + 3] = 255;
        }
        ApplyLiveMapShading(pixels, heights, hasData, size);

        var path = Path.Combine(root, "2d", colored ? "basic" : "sepia", "0", $"{key.X}_{key.Z}.png");
        var png = PngEncoder.Encode(size, size, pixels);
        if (colored) TileColorStamp.Invalidate(path);
        TileIntegrity.Write(path, png);
        if (colored) TileColorStamp.Complete(path, colors!.Version);
        return true;
    }

    private bool IsWaterEdge(bool[] water, bool[] hasData, int x, int z, int size)
    {
        foreach (var (dx, dz) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
        {
            var nx = x + dx; var nz = z + dz;
            if ((uint)nx >= (uint)size || (uint)nz >= (uint)size) continue;
            var index = nz * size + nx;
            // SepiaRenderer treats a null BlockData entry as water. A present
            // entry whose top block is air is still a non-water column.
            if (!hasData[index]) continue;
            if (!water[index]) return true;
        }
        return false;
    }
    private static int ReadDefault(IReadOnlyDictionary<int, ServerChunk?> chunks, int x, int y, int z)
    {
        if (y < 0) return 0;
        var chunkY = y >> 5;
        if (!chunks.TryGetValue(chunkY, out var chunk) || chunk == null) return 0;
        var index = x + z * 32 + (y & 31) * 1024;
        return chunk.Data.GetBlockId(index, 0);
    }
    private static int ReadFluid(IReadOnlyDictionary<int, ServerChunk?> chunks, int x, int y, int z)
    {
        if (y < 0) return 0;
        var chunkY = y >> 5;
        if (!chunks.TryGetValue(chunkY, out var chunk) || chunk == null) return 0;
        var index = x + z * 32 + (y & 31) * 1024;
        return chunk.Data.GetFluid(index);
    }

    private int ReadMapBlock(IReadOnlyDictionary<int, ServerChunk?> chunks, int chunkX, int chunkZ, int x, int y, int z)
    {
        var id = ReadDefault(chunks, x, y, z);
        if (id <= 0) return id;
        var block = materials.Get(id);
        if (block.Id != id) return MapPalette.MissingBlockId;
        if (!block.IsMicroBlock) return id;

        // Microblock material is stored in BlockEntityMicroBlock rather than
        // in the block id. The entity dictionary uses absolute world
        // coordinates (the same lookup used by LiveMap's RenderTask).
        if (y >= 0 && chunks.TryGetValue(y >> 5, out var chunk) && chunk != null)
        {
            var position = new BlockPos(chunkX * 32 + x, y, chunkZ * 32 + z);
            if (chunk.BlockEntities.TryGetValue(position, out var entity)
                && entity is BlockEntityMicroBlock micro
                && micro.BlockIds is { Length: > 0 } blockIds
                && blockIds[0] > 0
                && blockIds[0] < materials.BlockCount)
            {
                // BlockMicroBlock.GetColor on the client uses the first
                // material id directly.  A schematic can still retain the
                // 1.22.x temporary meta-blocklayer id, however; resolve it
                // with the same worldgen rule used by the server before
                // asking the client colormap for its colour.
                var sourceId = blockIds[0];
                var resolved = reader.ResolveMetaBlockLayer(position.X, position.Y, position.Z, sourceId);
                return resolved is > 0 ? resolved.Value : MapPalette.MissingBlockId;
            }
        }

        // A missing/empty entity is how old saves and partially written
        // chunks appear. Keep it visible as the dedicated black audit pixel;
        // substituting soil hides whether client material data was lost.
        return MapPalette.MissingBlockId;
    }

    private static void ApplyLiveMapShading(byte[] pixels, ushort[] heights, bool[] hasData, int size)
    {
        var shadowMap = Enumerable.Repeat((byte)128, size * size).ToArray();
        for (var x = 0; x < size; x++) for (var z = 0; z < size; z++)
        {
            var index = z * size + x;
            if (!hasData[index]) continue;
            var y = heights[index];
            var northwest = x > 0 && z > 0 && hasData[index - size - 1] ? heights[index - size - 1] : y;
            var north = z > 0 && hasData[index - size] ? heights[index - size] : y;
            var west = x > 0 && hasData[index - 1] ? heights[index - 1] : y;
            var direction = Math.Sign(y - northwest) + Math.Sign(y - north) + Math.Sign(y - west);
            var steepness = Math.Max(Math.Max(Math.Abs(y - northwest), Math.Abs(y - north)), Math.Abs(y - west));
            var slopeFactor = Math.Min(.5f, steepness / 10f) / 1.25f;
            var slope = direction > 0 ? 1.08f + slopeFactor : direction < 0 ? .92f - slopeFactor : 1f;
            shadowMap[index] = (byte)Math.Clamp(128f * slope, 0, 255);
        }

        var unblurred = shadowMap.ToArray();
        BoxBlurRangeOne(shadowMap, size);
        for (var index = 0; index < shadowMap.Length; index++)
        {
            if (pixels[index * 4 + 3] == 0) continue;
            var shadow = (int)(((shadowMap[index] / 128f) - 1f) * 5f) / 5f;
            shadow += ((((unblurred[index] / 128f) - 1f) * 5f) % 1f) / 5f;
            var light = shadow * 1.4f + 1f;
            var offset = index * 4;
            pixels[offset] = (byte)Math.Clamp((int)(pixels[offset] * light), 0, 255);
            pixels[offset + 1] = (byte)Math.Clamp((int)(pixels[offset + 1] * light), 0, 255);
            pixels[offset + 2] = (byte)Math.Clamp((int)(pixels[offset + 2] * light), 0, 255);
        }
    }

    private static void BoxBlurRangeOne(byte[] values, int size)
    {
        var output = new byte[values.Length];
        for (var z = 0; z < size; z++) for (var x = 0; x < size; x++)
        {
            var sum = 0; var count = 0;
            for (var sampleX = Math.Max(0, x - 1); sampleX <= Math.Min(size - 1, x + 1); sampleX++)
            {
                sum += values[z * size + sampleX];
                count++;
            }
            output[z * size + x] = (byte)(sum / count);
        }
        Array.Copy(output, values, values.Length);

        for (var z = 0; z < size; z++) for (var x = 0; x < size; x++)
        {
            var sum = 0; var count = 0;
            for (var sampleZ = Math.Max(0, z - 1); sampleZ <= Math.Min(size - 1, z + 1); sampleZ++)
            {
                sum += values[sampleZ * size + x];
                count++;
            }
            output[z * size + x] = (byte)(sum / count);
        }
        Array.Copy(output, values, values.Length);
    }
}
