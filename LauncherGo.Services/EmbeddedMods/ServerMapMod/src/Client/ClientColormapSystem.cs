// Colormap sampling portions adapted from VS-LiveMap-Revival (MIT).
// Copyright (c) 2024 William Blake Galbreath. See VS-LiveMap-Revival-LICENSE.txt.
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using ServerMap.Network;
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

    [ThreadStatic] private static BlockPos? overridePosition;
    [ThreadStatic] private static float? overrideMonth;
    private static readonly object PatchGate = new();

    private ICoreClientAPI? api;
    private IClientNetworkChannel? channel;
    private Harmony? harmony;
    private long tickListenerId;
    private int requestedMonth;
    private int sentMonth;
    private int generating;
    private bool wasConnected;
    private bool patched;
    private bool disposed;
    private CancellationTokenSource stop = new();

    private List<Block>? generationBlocks;
    private Dictionary<string, uint[]>? generationColors;
    private BlockPos? generationPosition;
    private int generationMonth;
    private int generationIndex;
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
            .SetMessageHandler<ServerColormapRequestPacket>(OnColormapRequested);
        harmony = new Harmony("servermap-livemap-colormap");
        tickListenerId = api.Event.RegisterGameTickListener(_ => CheckForGeneration(), 1000);
        api.Logger.Notification("ServerMap client colormap channel ready.");
    }

    private void CheckForGeneration()
    {
        if (api == null || disposed || stop.IsCancellationRequested) return;
        var connected = channel is { Connected: true };
        if (!connected)
        {
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

            EnsurePatched();
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

            overridePosition = generationPosition;
            // LiveMap generates the middle of the selected month, not the
            // current day within that month.
            overrideMonth = (generationMonth - 0.5f) / 12f;
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

            overridePosition = null;
            overrideMonth = null;
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
            return Reverse((uint)api.BlockTextureAtlas.GetAverageColor(block.TextureSubIdForBlockColor));
        if (block is BlockPlant)
        {
            var grass = api.World.GetBlock(new AssetLocation("game:tallgrass-tall-free"));
            if (grass != null) return Reverse((uint)grass.GetColor(api, position));
        }
        return Reverse((uint)block.GetColor(api, position));
    }

    private void EnsurePatched()
    {
        if (patched || harmony == null) return;
        lock (PatchGate)
        {
            if (patched || harmony == null) return;
            var getter = AccessTools.PropertyGetter(typeof(GameCalendar), "YearRel");
            if (getter == null) throw new MissingMethodException(typeof(GameCalendar).FullName, "get_YearRel");
            harmony.Patch(getter, prefix: new HarmonyMethod(typeof(ClientColormapSystem), nameof(PreYearRel)));
            patched = true;
        }
    }

    public static bool PreYearRel(ref float __result)
    {
        if (overrideMonth == null) return true;
        __result = overrideMonth.Value;
        return false;
    }

    private void FinishGeneration()
    {
        overridePosition = null;
        overrideMonth = null;
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
        overridePosition = null;
        overrideMonth = null;
        FinishGeneration();
    }

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(bytes, 0, bytes.Length);
        return output.ToArray();
    }

    // This intentionally mirrors livemap.data.Color.Reverse, including its
    // alpha placement. The generated payload drops alpha after blending, but
    // preserving this bit-level behavior keeps custom/transparent blocks
    // identical to the reference implementation.
    private static uint Reverse(uint color)
    {
        var alpha = (color >> 24) & 0xFF;
        var red = (color >> 16) & 0xFF;
        var green = (color >> 8) & 0xFF;
        var blue = color & 0xFF;
        return alpha | red | (green << 8) | (blue << 16);
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
        if (harmony != null && patched) harmony.UnpatchAll(harmony.Id);
        if (api != null && tickListenerId != 0) api.Event.UnregisterGameTickListener(tickListenerId);
        channel = null;
        api = null;
        harmony = null;
        stop.Dispose();
        base.Dispose();
    }
}
