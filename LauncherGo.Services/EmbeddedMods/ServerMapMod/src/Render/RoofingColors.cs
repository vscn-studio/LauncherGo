using System.Collections;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace ServerMap.Render;

// Optional VS Roofing integration: inspect its loaded definitions without a
// compile-time mod dependency or constructing/tessellating client entities.
internal sealed class RoofingColors
{
    internal const string Prefix = "servermap:vsroofing-color/";
    private readonly Dictionary<string, Definition> roofs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Definition> frames = new(StringComparer.Ordinal);
    internal int RoofCount => roofs.Count;
    internal int FrameCount => frames.Count;
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static object? Member(object? value, string name) => value == null ? null
        : value.GetType().GetProperty(name, Members)?.GetValue(value)
            ?? value.GetType().GetField(name, Members)?.GetValue(value);

    internal static bool IsRoof(Block block) => block.GetType().FullName == "VSRoofing.RoofBlock";

    internal RoofingColors(Block block)
    {
        Capture("AutoRoofVariants", roofs, false);
        Capture("Frames", frames, true);
        void Capture(string field, Dictionary<string, Definition> target, bool frame)
        {
            if (block.GetType().GetField(field, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is not IDictionary definitions) return;
            foreach (DictionaryEntry pair in definitions)
                if (pair.Key is string key && pair.Value != null)
                    target[key] = new Definition(key, pair.Value, frame);
        }
    }

    internal string? Resolve(BlockEntity entity, out int materialId)
    {
        materialId = 0;
        if (Member(entity, "SnowCovered") is int snow && snow > 0) return Prefix + "snow";
        var materials = (Member(entity, "MaterialsReadOnly") as IEnumerable)?.Cast<object>().ToArray() ?? [];
        if (materials.Length > 0)
        {
            if (Member(entity, "RoofVariant") is not string variant || !roofs.TryGetValue(variant, out var definition)) return null;
            var collectible = definition.Index >= 0 && definition.Index < materials.Length
                ? (Member(materials[definition.Index], "Primary") as ItemStack)?.Collectible : null;
            return definition.ColorKey(collectible);
        }
        // A roof can contain chiseled infill without any covering materials.
        if (entity is BlockEntityMicroBlock micro && micro.VoxelCuboids is { Count: > 0 } && micro.BlockIds is { Length: > 0 })
        {
            materialId = micro.BlockIds[0];
            return null;
        }
        if (Member(entity, "FrameVariant") is string frame && frames.TryGetValue(frame, out var frameDefinition))
            return frameDefinition.ColorKey((Member(entity, "FrameStack") as ItemStack)?.Collectible);
        return null;
    }

    internal IEnumerable<Sample> Samples(IWorldAccessor world) => Samples(world.Blocks.Cast<CollectibleObject>().Concat(world.Items));

    internal IEnumerable<Sample> Samples(IEnumerable<CollectibleObject> source)
    {
        yield return new(Prefix + "snow", new CompositeTexture(new AssetLocation("game:block/liquid/snow/normal1")), false);
        var collectibles = source.Where(c => c?.Code != null).ToArray();
        foreach (var definition in roofs.Values.Concat(frames.Values))
        {
            if (definition.Index < 0)
            {
                if (definition.Texture(null) is { } texture)
                    yield return new(definition.ColorKey(null)!, texture, definition.TintGrass);
                continue;
            }
            foreach (var collectible in collectibles)
            {
                if (!definition.Matches(collectible)) continue;
                if (definition.Texture(collectible) is { } texture)
                    yield return new(definition.ColorKey(collectible)!, texture, definition.TintGrass);
            }
        }
    }

    internal sealed record Sample(string Key, CompositeTexture Texture, bool TintGrass);

    private sealed class Definition
    {
        private readonly string key;
        private readonly string colorCode;
        private readonly CompositeTexture? fixedTexture;
        private readonly string? sourceTextureCode;
        private readonly (AssetLocation Pattern, CompositeTexture Texture)[] byMaterial;
        private readonly AssetLocation[] patterns;
        internal int Index { get; } = -1;
        internal bool TintGrass { get; }

        internal Definition(string name, object value, bool frame)
        {
            key = Prefix + (frame ? "frame/" : "roof/") + name;
            TintGrass = !frame && name is "sod" or "sod-bare" or "slab-sod" or "slab-sod-bare";
            colorCode = Member(value, "ColorTextureCode") as string ?? "";
            fixedTexture = (Member(value, "Textures") as IDictionary)?[colorCode] as CompositeTexture;
            byMaterial = [];
            patterns = [];
            if (fixedTexture != null) return;
            var source = (Member(value, "TexturesFromMaterial") as IDictionary)?[colorCode];
            var mapped = (Member(value, "TexturesByMaterial") as IDictionary)?[colorCode];
            if (source == null && mapped == null) return;
            Index = frame ? 0 : (Member(source ?? mapped, "Index") as int? ?? 0);
            sourceTextureCode = Member(source, "TextureCode") as string;
            if (Member(mapped, "Textures") is IDictionary textures)
                byMaterial = textures.Keys.OfType<AssetLocation>().Where(pattern => textures[pattern] is CompositeTexture)
                    .Select(pattern => (pattern, (CompositeTexture)textures[pattern]!)).ToArray();
            var materials = Member(value, "Materials") as Array;
            var material = frame ? Member(value, "Material")
                : materials != null && Index >= 0 && Index < materials.Length ? Member(materials.GetValue(Index), "Primary") : null;
            patterns = Member(material, "Code") as AssetLocation[] ?? [];
        }

        internal string? ColorKey(CollectibleObject? collectible) => Index < 0 ? key
            : collectible?.Code == null ? null : key + "/" + collectible.Code;

        internal bool Matches(CollectibleObject collectible) => patterns.Any(pattern => WildcardUtil.Match(pattern, collectible.Code));

        internal CompositeTexture? Texture(CollectibleObject? collectible)
        {
            if (fixedTexture != null) return fixedTexture.Clone();
            if (collectible == null) return null;
            if (sourceTextureCode != null)
            {
                var textures = collectible is Block block ? block.Textures : (collectible as Item)?.Textures;
                return textures != null && textures.TryGetValue(sourceTextureCode, out var texture) ? texture.Clone() : null;
            }
            foreach (var (pattern, texture) in byMaterial)
            {
                if (!WildcardUtil.Match(pattern, collectible.Code)) continue;
                var result = texture.Clone();
                foreach (var variant in collectible.Variant) result.FillPlaceholder("{" + variant.Key + "}", variant.Value);
                return result;
            }
            return null;
        }
    }
}
