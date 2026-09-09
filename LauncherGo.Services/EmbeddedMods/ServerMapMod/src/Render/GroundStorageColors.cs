using Vintagestory.API.Common;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ServerMap.Render;

internal sealed class GroundStorageColors(IWorldAccessor world)
{
    internal const string Prefix = "servermap:groundstorage-color/";
    internal const string CompleteKey = Prefix + "version-2";
    internal static bool IsStorage(Block block) => block is BlockGroundStorage or BlockIngotPile or BlockPlatePile
        or BlockMetalPartPile or BlockPlankPile or BlockCoalPile or BlockFirewoodPile or BlockPeatPile;
    internal static string Key(CollectibleObject collectible) => Prefix + (collectible is Block ? "block/" : "item/") + collectible.Code;
    internal static uint[] SampleColors(ICoreClientAPI client, CollectibleObject collectible)
    {
        var stack = collectible is Block block ? new ItemStack(block) : new ItemStack((Item)collectible);
        // A baked texture's numeric sub-id is atlas-local. Resolve ordinary
        // item textures by name in the item atlas, rather than trusting an id
        // that can refer to another texture after atlas insertion/reloading.
        // Keep custom collectible overrides (and the block-stack overload).
        int[] samples;
        if (collectible is Item item && collectible.GetType()
            .GetMethod(nameof(CollectibleObject.GetRandomColor), [typeof(ICoreClientAPI), typeof(ItemStack)])?.DeclaringType == typeof(Item))
        {
            var texture = item.ParticlesTextureCode is { } code
                ? item.Textures?.GetValueOrDefault(code) : item.Textures?.FirstOrDefault().Value;
            if (texture?.Baked?.BakedName is not { } name)
                throw new InvalidDataException($"Ground-storage item texture is not ready: {item.Code}");
            var atlas = client.ItemTextureAtlas;
            var position = atlas[name];
            if (position == null || ReferenceEquals(position, atlas.UnknownTexturePosition))
                throw new InvalidDataException($"Ground-storage item texture is unavailable: {item.Code}, {name}");
            samples = Enumerable.Range(0, 30).Select(index => atlas.GetRandomColor(position, index)).ToArray();
        }
        else samples = Enumerable.Range(0, 30).Select(_ => collectible.GetRandomColor(client, stack)).ToArray();

        // The atlas can retain transparent samples after its bounded retries.
        // Stripping alpha first would turn their hidden RGB into opaque pixels.
        // Preserve legitimate greens; reject transparent pixels, not hues.
        var visible = samples.Where(color => ((uint)color >> 24) > 5).Select(color => (uint)color & 0xFFFFFF).ToArray();
        if (visible.Length == 0) throw new InvalidDataException($"No visible ground-storage texture samples: {collectible.Code}");
        return Enumerable.Range(0, 30).Select(index => visible[index % visible.Length]).ToArray();
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
