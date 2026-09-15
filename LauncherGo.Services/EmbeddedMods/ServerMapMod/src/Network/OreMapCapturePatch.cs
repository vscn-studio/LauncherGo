using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.API.MathTools;
using System.Collections.Generic;
using ServerMap.World;

namespace ServerMap.Network;

[HarmonyPatch(typeof(ModSystemOreMap), nameof(ModSystemOreMap.DidProbe))]
internal static class OreMapCapturePatch
{
    [HarmonyPostfix]
    private static void After(ICoreAPI ___api, PropickReading results, IServerPlayer splr)
    {
        // The API belongs to the patched instance, so integrated-server and client instances
        // cannot accidentally share a static store. Nothing registers a client upload handler.
        if (___api?.Side != EnumAppSide.Server || splr == null || results == null) return;
        try { ___api.ModLoader.GetModSystem<ServerMapModSystem>()?.WebServer?.RecordProspecting(results, splr); }
        catch (Exception ex) { ___api.Logger.Warning("ServerMap could not record completed probe: {0}", ex.Message); }
    }
}

[HarmonyPatch(typeof(ItemProspectingPick), "ProbeBlockNodeMode")]
internal static class OreNodeCapturePatch
{
    [HarmonyPrefix]
    private static void Before(IWorldAccessor world, Entity byEntity, BlockSelection blockSel, int radius, out bool __state)
    {
        __state = false;
        if (world?.Api?.Side != EnumAppSide.Server || radius is < 1 or > OreHeatmapStore.MaxNodeRadius
            || byEntity?.Pos.Dimension != 0 || blockSel?.Position == null) return;
        try { __state = world.BlockAccessor.GetBlock(blockSel.Position)?.Attributes?["propickable"].AsBool(false) == true; }
        catch (Exception ex) { world.Logger.Warning("ServerMap could not validate node probe: {0}", ex.Message); }
    }

    [HarmonyPostfix]
    private static void After(IWorldAccessor world, Entity byEntity, BlockSelection blockSel, int radius, bool __state)
    {
        if (!__state || world?.Api?.Side != EnumAppSide.Server || byEntity is not EntityPlayer entity || blockSel?.Position == null) return;
        if (world.PlayerByUid(entity.PlayerUID) is not IServerPlayer player) return;
        try
        {
            var found = new Dictionary<string, int>(StringComparer.Ordinal);
            var min = blockSel.Position.AddCopy(-radius, -radius, -radius);
            var max = blockSel.Position.AddCopy(radius, radius, radius);
            world.BlockAccessor.WalkBlocks(max, min, (block, x, y, z) =>
            {
                if (block.BlockMaterial != EnumBlockMaterial.Ore || block.Variant == null || !block.Variant.ContainsKey("type")) return;
                var code = block.Variant["type"];
                found[code] = found.GetValueOrDefault(code) + 1;
            }, false);
            var web = world.Api.ModLoader.GetModSystem<ServerMap.ServerMapModSystem>()?.WebServer;
            web?.RecordNodeProspecting(blockSel.Position.X, blockSel.Position.Y, blockSel.Position.Z, radius,
                found.Select(pair => new OreHeatmapStore.NodeValue(pair.Key, pair.Value)), player);
        }
        catch (Exception ex) { world.Logger.Warning("ServerMap could not record node prospecting: {0}", ex.Message); }
    }
}
