// Colormap sampling portions adapted from VS-LiveMap-Revival (MIT).
// Copyright (c) 2024 William Blake Galbreath. See VS-LiveMap-Revival-LICENSE.txt.
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ServerMap.Network;
using ServerMap.Render;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.GameContent;

namespace ServerMap.Client;

/// <summary>
/// Generates the same 30-entry colormap as LiveMap inside the real client,
/// where texture atlas averages and climate/season color maps are available.
///
/// Block and calendar APIs are client-main-thread objects. Generation is a
/// small main-thread state machine; only JSON compression runs on a worker
/// thread after all colors have been collected.
/// </summary>
public sealed class ClientColormapSystem : ModSystem
{
    private const string ChannelName = "servermap-colormap";
    private const int ChunkBytes = 60 * 1024;
    private const int BlocksPerBatch = 64;
    private const int ChunksPerBatch = 2;

    private ICoreClientAPI? api;
    private IClientNetworkChannel? channel;
    private long tickListenerId;
    private int requestedMonth;
    private int sentMonth;
    private int generating;
    private bool wasConnected;
    private Queue<ClientWaypointIconPacket>? waypointIcons;
    private bool iconsSent;
    private bool disposed;
    private CancellationTokenSource stop = new();

    private List<Block>? generationBlocks;
    private Dictionary<string, uint[]>? generationColors;
    private BlockPos? generationPosition;
    private int generationMonth;
    private int generationIndex;
    private IEnumerator<RoofingColors.Sample>? roofingSamples;
    private Block? roofingBlock;
    private IEnumerator<CollectibleObject>? groundSamples;
    private byte[]? transferData;
    private string? transferId;
    private int transferIndex;
    private int transferTotal;
    private readonly HashSet<string> sampleLogs = new(StringComparer.Ordinal);

    public override void StartClientSide(ICoreClientAPI api)
    {
        this.api = api;
        channel = api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<ServerColormapRequestPacket>()
            .RegisterMessageType<ClientColormapChunkPacket>()
            .RegisterMessageType<ClientWaypointIconPacket>()
            .SetMessageHandler<ServerColormapRequestPacket>(OnColormapRequested);
        tickListenerId = api.Event.RegisterGameTickListener(_ => CheckForGeneration(), 1000);
        api.Logger.Notification("ServerMap client colormap channel ready.");
    }

    private void CheckForGeneration()
    {
        if (api == null || disposed || stop.IsCancellationRequested) return;
        var connected = channel is { Connected: true };
        if (!connected)
        {
            iconsSent = false; waypointIcons = null;
            // A server restart creates a new channel state. Do not keep the
            // previous server's sent-month marker.
            if (wasConnected)
            {
                sentMonth = 0;
                requestedMonth = 0;
                AbortGeneration(null);
            }
            wasConnected = false;
            return;
        }
        if (!wasConnected)
        {
            wasConnected = true;
            sentMonth = 0;
            requestedMonth = 0;
        }
        if (api.World.Player?.Entity == null) return;
        if (!api.World.Player.HasPrivilege(Privilege.root)) return;

        // Dedicated servers omit these client assets. Send the actual game/mod
        // icons in small batches, once per connection; the server caches them.
        if (!iconsSent)
        {
            waypointIcons ??= new Queue<ClientWaypointIconPacket>(api.Assets.GetMany("textures/icons/worldmap/")
                .Where(a => a.Name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) && a.Data.Length <= ServerMap.Web.WaypointIconStore.MaxBytes)
                .Take(256).Select(a => new ClientWaypointIconPacket {
                    Name = System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(a.Name), "\\d+\\-", ""), Data = a.Data.ToArray() }));
            for (var i = 0; i < 2 && waypointIcons.Count > 0; i++) channel!.SendPacket(waypointIcons.Dequeue());
            if (waypointIcons.Count == 0) { iconsSent = true; api.Logger.Notification("ServerMap original waypoint icons sent."); }
        }

        var month = requestedMonth;
        if (month is < 1 or > 12 || month == sentMonth) return;
        if (Interlocked.CompareExchange(ref generating, 1, 0) != 0) return;
        api.Event.EnqueueMainThreadTask(() => StartGeneration(month), "servermap-colormap-start");
    }

    private void OnColormapRequested(ServerColormapRequestPacket packet)
    {
        if (api == null || packet.Month is < 1 or > 12) return;
        if (packet.Month == sentMonth)
        {
            api.Logger.Notification("ServerMap ignored duplicate client colormap request for month {0}.", packet.Month);
            return;
        }
        requestedMonth = packet.Month;
        api.Logger.Notification("ServerMap received client colormap request for month {0}.", packet.Month);
        CheckForGeneration();
    }

    private void StartGeneration(int month)
    {
        try
        {
            if (api == null || disposed || stop.IsCancellationRequested || channel is not { Connected: true })
            {
                AbortGeneration(null);
                return;
            }
            var player = api.World.Player?.Entity;
            if (player == null)
            {
                AbortGeneration(null);
                return;
            }

#pragma warning disable CS0618 // LiveMap samples the player's sided position.
            generationPosition = player.SidedPos.AsBlockPos;
#pragma warning restore CS0618
            generationBlocks = api.World.Blocks.Where(block => block?.Code is not null).ToList();
            roofingBlock = generationBlocks.FirstOrDefault(RoofingColors.IsRoof);
            roofingSamples = roofingBlock == null ? null : new RoofingColors(roofingBlock).Samples(api.World).GetEnumerator();
            groundSamples = generationBlocks.Any(GroundStorageColors.IsStorage)
                ? generationBlocks.Cast<CollectibleObject>().Concat(api.World.Items).Where(c => c?.Code != null && !c.IsMissing).GetEnumerator() : null;
            generationColors = new Dictionary<string, uint[]>(StringComparer.Ordinal);
            generationMonth = month;
            generationIndex = 0;
            sampleLogs.Clear();
            ProcessGenerationBatch();
        }
        catch (Exception ex)
        {
            AbortGeneration(ex);
        }
    }

    private void ProcessGenerationBatch()
    {
        try
        {
            if (api == null || disposed || generationBlocks == null || generationColors == null || generationPosition == null)
            {
                AbortGeneration(null);
                return;
            }
            // LiveMap generates the middle of the selected month, not the
            // current day within that month.
            var end = Math.Min(generationBlocks.Count, generationIndex + BlocksPerBatch);
            for (; generationIndex < end; generationIndex++)
            {
                var block = generationBlocks[generationIndex];
                try
                {
                    generationColors[block.Code!.ToString()] = GenerateBlockColors(block, generationPosition);
                }
                catch (Exception ex)
                {
                    api.Logger.Warning("ServerMap skipped client colormap block {0}: {1}", block.Code, ex.Message);
                }
            }

            if (generationIndex < generationBlocks.Count)
            {
                api.Event.EnqueueMainThreadTask(ProcessGenerationBatch, "servermap-colormap-batch");
                return;
            }
            // RoofBlock.GetColor(playerPosition) has no roof entity to inspect.
            // Sample the mod's material textures independently, including items
            // (shingles, plates, planks) which have no block-id colormap entry.
            if (roofingSamples != null)
            {
                for (var i = 0; i < BlocksPerBatch; i++)
                {
                    if (!roofingSamples.MoveNext())
                    {
                        roofingSamples.Dispose(); roofingSamples = null;
                        api.Logger.Notification("ServerMap sampled {0} VS Roofing material colors.", generationColors.Keys.Count(k => k.StartsWith(RoofingColors.Prefix, StringComparison.Ordinal)));
                        break;
                    }
                    var sample = roofingSamples.Current;
                    try { generationColors[sample.Key] = GenerateRoofColors(sample, generationPosition); }
                    catch (Exception ex) { api.Logger.Warning("ServerMap skipped roofing colormap {0}: {1}", sample.Key, ex.Message); }
                }
                if (roofingSamples != null)
                {
                    api.Event.EnqueueMainThreadTask(ProcessGenerationBatch, "servermap-roofing-colormap-batch");
                    return;
                }
            }
            if (groundSamples != null)
            {
                for (var i = 0; i < BlocksPerBatch; i++)
                {
                    if (!groundSamples.MoveNext())
                    {
                        groundSamples.Dispose(); groundSamples = null;
                        generationColors[GroundStorageColors.CompleteKey] = Enumerable.Repeat(1u, 30).ToArray();
                        api.Logger.Notification("ServerMap sampled {0} ground-storage collectible colors.", generationColors.Keys.Count(k => k.StartsWith(GroundStorageColors.Prefix, StringComparison.Ordinal)) - 1);
                        break;
                    }
                    var collectible = groundSamples.Current;
                    try { generationColors[GroundStorageColors.Key(collectible)] = GroundStorageColors.SampleColors(api, collectible); }
                    catch (Exception ex) { api.Logger.Warning("ServerMap skipped ground-storage colormap {0}: {1}", collectible.Code, ex.Message); }
                }
                if (groundSamples != null)
                {
                    api.Event.EnqueueMainThreadTask(ProcessGenerationBatch, "servermap-groundstorage-colormap-batch");
                    return;
                }
            }
            var json = JsonSerializer.Serialize(generationColors);
            var month = generationMonth;
            var clientApi = api;
            _ = Task.Run(() => Compress(Encoding.UTF8.GetBytes(json)), stop.Token)
                .ContinueWith(task => clientApi?.Event.EnqueueMainThreadTask(
                    () => BeginTransfer(task, month), "servermap-colormap-transfer"),
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            AbortGeneration(ex);
        }
    }

    private void BeginTransfer(Task<byte[]> compressionTask, int month)
    {
        try
        {
            if (api == null || disposed || compressionTask.IsCanceled)
            {
                AbortGeneration(null);
                return;
            }
            if (compressionTask.IsFaulted)
                throw compressionTask.Exception?.GetBaseException() ?? new InvalidDataException("Colormap compression failed.");

            transferData = compressionTask.Result;
            transferId = Guid.NewGuid().ToString("N");
            transferIndex = 0;
            transferTotal = (transferData.Length + ChunkBytes - 1) / ChunkBytes;
            if (transferTotal is <= 0 or > 1024) throw new InvalidDataException("Generated colormap is too large.");
            SendTransferBatch(month);
        }
        catch (Exception ex)
        {
            AbortGeneration(ex);
        }
    }

    private void SendTransferBatch(int month)
    {
        try
        {
            if (api == null || disposed || channel is not { Connected: true } || transferData == null || transferId == null)
            {
                AbortGeneration(null);
                return;
            }

            var end = Math.Min(transferTotal, transferIndex + ChunksPerBatch);
            for (; transferIndex < end; transferIndex++)
            {
                var offset = transferIndex * ChunkBytes;
                var length = Math.Min(ChunkBytes, transferData.Length - offset);
                var data = new byte[length];
                Buffer.BlockCopy(transferData, offset, data, 0, length);
                channel.SendPacket(new ClientColormapChunkPacket
                {
                    ProtocolVersion = 2,
                    TransferId = transferId,
                    ChunkIndex = transferIndex,
                    TotalChunks = transferTotal,
                    Data = data,
                    Month = month
                });
            }

            if (transferIndex < transferTotal)
            {
                api.Event.EnqueueMainThreadTask(() => SendTransferBatch(month), "servermap-colormap-send-batch");
                return;
            }

            sentMonth = month;
            api.Logger.Notification("ServerMap sent client colormap: month {0}, {1} blocks, {2} chunks.", month, generationColors?.Count ?? 0, transferTotal);
            FinishGeneration();
        }
        catch (Exception ex)
        {
            AbortGeneration(ex);
        }
    }

    private uint[] GenerateBlockColors(Block block, BlockPos position)
    {
        if (api == null) throw new InvalidOperationException("Client API is unavailable.");

        // GetColor returns the atlas-native BGRA packed value. LiveMap flips
        // only this base value; GetRandomColor returns the atlas RGBA value
        // already consumed by Color.Blend, so it must not be flipped again.
        var baseColor = GetBaseColor(block, position);
        var variants = new uint[30];
        for (var i = 0; i < variants.Length; i++)
        {
            var randomColor = (uint)block.GetRandomColor(api, position, BlockFacing.UP, i);
            variants[i] = Blend(baseColor, randomColor, .4f) & 0xFFFFFF;
            if (i == 0) LogSample(block, baseColor, randomColor, variants[i]);
        }
        return variants;
    }

    private uint[] GenerateRoofColors(RoofingColors.Sample sample, BlockPos position)
    {
        var client = api ?? throw new InvalidOperationException("Client API is unavailable.");
        sample.Texture.Bake(client.Assets);
        var baked = sample.Texture.Baked;
        if (sample.Texture.Alternates != null && baked.BakedVariants is { Length: > 0 })
            baked = baked.BakedVariants[GameMath.MurmurHash3Mod(position.X, position.Y, position.Z, baked.BakedVariants.Length)];
        if (!client.BlockTextureAtlas.GetOrInsertTexture(baked.BakedName, out _, out var atlasPosition)
            || atlasPosition == null || atlasPosition == client.BlockTextureAtlas.UnknownTexturePosition)
            throw new InvalidDataException($"Roof texture is unavailable: {baked.BakedName}");
        var baseColor = BgraToRgb((uint)atlasPosition.AvgColor);
        var variants = new uint[30];
        for (var i = 0; i < variants.Length; i++)
        {
            var randomColor = client.BlockTextureAtlas.GetRandomColor(atlasPosition, i);
            if (sample.TintGrass && roofingBlock != null && client.World.GetColorMapData(roofingBlock, position.X, position.Y, position.Z) is { } climate)
                randomColor = client.World.ApplyColorMapOnRgba("climatePlantTint", "seasonalGrass", randomColor, climate.Rainfall, climate.Temperature, true);
            variants[i] = Blend(baseColor, (uint)randomColor, .4f) & 0xFFFFFF;
        }
        return variants;
    }

    private void LogSample(Block block, uint baseColor, uint randomColor, uint blended)
    {
        if (api == null || block.Code == null) return;
        var code = block.Code.ToString();
        if (code is not ("game:water-still-7" or "game:soil-low-normal" or "game:tallgrass-mediumshort-free") || !sampleLogs.Add(code)) return;
        api.Logger.Notification("ServerMap colormap sample {0}: base={1} random={2} blended={3}", code, Describe(baseColor), Describe(randomColor), Describe(blended));
    }

    private static string Describe(uint color) =>
        $"({(color >> 16) & 0xff},{(color >> 8) & 0xff},{color & 0xff}) 0x{color:X8}";

    private uint GetBaseColor(Block block, BlockPos position)
    {
        if (api == null) return 0;
        if (block is BlockRequireSolidGround)
            return BgraToRgb((uint)api.BlockTextureAtlas.GetAverageColor(block.TextureSubIdForBlockColor));
        if (block is BlockPlant)
        {
            var grass = api.World.GetBlock(new AssetLocation("game:tallgrass-tall-free"));
            if (grass != null) return BgraToRgb((uint)grass.GetColor(api, position));
        }
        return BgraToRgb((uint)block.GetColor(api, position));
    }

    private void FinishGeneration()
    {
        roofingSamples?.Dispose(); roofingSamples = null;
        groundSamples?.Dispose(); groundSamples = null;
        roofingBlock = null;
        generationBlocks = null;
        generationColors = null;
        generationPosition = null;
        transferData = null;
        transferId = null;
        transferIndex = 0;
        transferTotal = 0;
        requestedMonth = 0;
        Interlocked.Exchange(ref generating, 0);
    }

    private void AbortGeneration(Exception? exception)
    {
        if (exception != null) api?.Logger.Warning("ServerMap client colormap generation failed: {0}", exception);
        FinishGeneration();
    }

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(bytes, 0, bytes.Length);
        return output.ToArray();
    }

    private static uint BgraToRgb(uint color)
    {
        var red = color & 0xFF;
        var green = (color >> 8) & 0xFF;
        var blue = (color >> 16) & 0xFF;
        return (red << 16) | (green << 8) | blue;
    }

    private static uint Blend(uint color0, uint color1, float ratio)
    {
        var inverse = 1 - ratio;
        return (color0 & 0xFF000000)
            | ((uint)(((color0 >> 16) & 0xFF) * ratio + ((color1 >> 16) & 0xFF) * inverse) << 16)
            | ((uint)(((color0 >> 8) & 0xFF) * ratio + ((color1 >> 8) & 0xFF) * inverse) << 8)
            | (uint)(((color0 & 0xFF) * ratio) + ((color1 & 0xFF) * inverse));
    }

    public override void Dispose()
    {
        disposed = true;
        stop.Cancel();
        AbortGeneration(null);
        if (api != null && tickListenerId != 0) api.Event.UnregisterGameTickListener(tickListenerId);
        channel = null;
        api = null;
        stop.Dispose();
        base.Dispose();
    }
}
