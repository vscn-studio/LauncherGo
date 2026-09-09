using Vintagestory.API.Common;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ServerMap.Render;

internal sealed class GroundStorageColors(IWorldAccessor world)
{
    internal const string Prefix = "servermap:groundstorage-color/";
    internal const string CompleteKey = Prefix + "version-1";
    internal static bool IsStorage(Block block) => block is BlockGroundStorage or BlockIngotPile or BlockPlatePile
        or BlockMetalPartPile or BlockPlankPile or BlockCoalPile or BlockFirewoodPile or BlockPeatPile;
    internal static string Key(CollectibleObject collectible) => Prefix + (collectible is Block ? "block/" : "item/") + collectible.Code;
    internal static uint[] SampleColors(ICoreClientAPI client, CollectibleObject collectible)
    {
        var stack = collectible is Block block ? new ItemStack(block) : new ItemStack((Item)collectible);
        // Exactly the ItemStack overload used by BlockGroundStorage. It selects
        // the correct item/block atlas and honors collectible color overrides;
        // these values are already RGBA and must not be BGRA-swapped again.
        return Enumerable.Range(0, 30).Select(_ => (uint)collectible.GetRandomColor(client, stack) & 0xFFFFFF).ToArray();
    }

    internal string? Resolve(BlockEntity entity, int x, int y, int z)
    {
        var inventory = entity switch {
            BlockEntityGroundStorage ground => ground.Inventory,
            BlockEntityItemPile pile => pile.inventory,
            _ => null
        };
        if (inventory == null) return null;
        var keys = new List<string>();
        foreach (var slot in inventory)
        {
            if (slot.Itemstack is not { StackSize: > 0 } stack) continue;
            // Offline chunk deserialization does not Initialize every pile's
            // inventory. Resolve a copy, without mutating saved/live stacks.
            if (stack.Collectible == null)
            {
                stack = stack.Clone();
                if (!stack.ResolveBlockOrItem(world)) continue;
            }
            if (stack.Collectible is not { Code: not null, IsMissing: false } collectible) continue;
            keys.Add(Key(collectible));
        }
        // Native ground storage chooses a nonempty slot uniformly (not weighted
        // by stack size). Use a coordinate hash instead of per-render randomness.
        return keys.Count == 0 ? null : keys[GameMath.MurmurHash3Mod(x, y, z, keys.Count)];
    }
}
