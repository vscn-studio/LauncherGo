using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using ServerMap.Render;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ServerMap.Client;

public static class ClientHeadCapture
{
    // Fraction of the base torso height retained above the crop line.  Keep
    // this independent from Render() scaling: increasing it moves the line
    // downward and reveals more torso without zooming the portrait.
    private const float VisibleUpperBodyFraction = 1f / 2f;
    private static ICoreClientAPI? cacheApi;
    private static MeshData? cachedMesh;
    private static string? cachedAppearance;
    private static Entity? cachedEntity;
    private static float? cachedBottomCutProjection;
    private sealed class CaptureSession : IDisposable
    {
        private readonly Harmony harmony = new("servermap-avatar-shape-snapshot");
        public CaptureSession(ICoreClientAPI api)
        {
            cacheApi = api; cachedMesh = null; cachedAppearance = null; cachedEntity = null; cachedBottomCutProjection = null;
            harmony.Patch(AccessTools.Method(typeof(EntityShapeRenderer), nameof(EntityShapeRenderer.TesselateShape), [typeof(Action<MeshData>), typeof(string[])]),
                prefix: new HarmonyMethod(typeof(ClientHeadCapture), nameof(BeforeTesselate)));
        }
        public void Dispose() { harmony.UnpatchAll(harmony.Id); cacheApi = null; cachedMesh = null; cachedAppearance = null; cachedEntity = null; cachedBottomCutProjection = null; }
    }
    public static IDisposable Start(ICoreClientAPI api) => new CaptureSession(api);
    private static void BeforeTesselate(EntityShapeRenderer __instance, ref Action<MeshData> onMeshDataReady)
    {
        var player = cacheApi?.World?.Player;
        if (player?.Entity?.Properties?.Client?.Renderer != __instance || player == null) return;
        var api = cacheApi;
        var entity = player.Entity;
        var appearance = PlayerMapSyncSystem.Appearance(player);
        var original = onMeshDataReady;
        // The native callback runs on the main/graphics thread and still contains
        // the full third-person mesh, before the first-person renderer removes the head.
        onMeshDataReady = mesh =>
        {
            try
            {
                if (cacheApi != api || api?.World.Player?.Entity != entity || appearance == null || appearance != PlayerMapSyncSystem.Appearance(player)) return;
                var animator = entity.AnimManager?.Animator;
                var head = animator?.GetPosebyName("Head") ?? animator?.GetPosebyName("head");
                if (head == null) throw new InvalidOperationException("Player animator has no head pose");
                var joints = new HashSet<int>();
                // The map avatar follows the reference renderer: head, neck,
                // torso and both arm branches, with legs removed below the
                // fixed upper-body crop.  Clothing and hair are attached to
                // these poses in the native shape tree, so walking their
                // children retains the selected appearance layers.
                var torso = animator?.GetPosebyName("LowerTorso") ?? animator?.GetPosebyName("lowertorso");
                void Collect(ElementPose pose)
                {
                    var name = pose.ForElement.Name ?? string.Empty;
                    if (ExcludedFromTorso(name, pose.ForElement.StepParentName)) return;
                    joints.Add(pose.ForElement.JointId);
                    foreach (var child in pose.ChildElementPoses ?? []) Collect(child);
                }
                if (torso != null) Collect(torso);
                else
                {
                    // Keep compatibility with alternate player shapes that do
                    // not expose the LowerTorso root by name.
                    foreach (var name in new[] { "UpperTorso", "Neck", "Head" })
                    {
                        var pose = animator?.GetPosebyName(name) ?? animator?.GetPosebyName(name.ToLowerInvariant());
                        if (pose != null) Collect(pose);
                    }
                }
                if (joints.Count == 0) throw new InvalidOperationException("Player animator has no torso/head poses");
                cachedMesh = ExtractHeadMesh(mesh, joints); cachedBottomCutProjection = ComputeBottomCutProjection(mesh, torso);
                cachedAppearance = appearance; cachedEntity = entity;
                api.Logger.Notification("ServerMap avatar mesh snapshot ready: {0} vertices, {1} faces.", cachedMesh.VerticesCount, cachedMesh.IndicesCount / 6);
            }
            catch (Exception ex) { cachedMesh = null; cachedAppearance = null; cachedBottomCutProjection = null; api?.Logger.Warning("ServerMap avatar snapshot unavailable: {0}", ex.Message); }
            finally { original(mesh); } // Never disrupt native mesh upload or live character animation.
        };
    }
    private static bool ExcludedFromTorso(string name, string? stepParent)
    {
        static string Compact(string value) => new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var compact = Compact(name); var parent = Compact(stepParent ?? string.Empty);
        // Legs/feet are outside the portrait.  Keep both arm branches; their
        // lower portions are clipped by the fixed anatomical crop line below.
        if (compact is "upperfootr" or "upperfootl" or "lowerfootr" or "lowerfootl" ||
            parent is "upperfootr" or "upperfootl" or "lowerfootr" or "lowerfootl") return true;
        // Held-item anchors have no useful portrait pixels and may otherwise
        // pull a weapon into the avatar or alter its bounds.
        if (compact.StartsWith("itemanchor", StringComparison.Ordinal) || parent.StartsWith("itemanchor", StringComparison.Ordinal)) return true;
        // Lower-body wearable roots are remote-parented to LowerTorso.  Keep
        // the native UpperTorso child, while preventing pants/skirts from
        // extending the portrait below the requested upper-body crop.
        return (compact.StartsWith("lowertorso", StringComparison.Ordinal) && compact != "lowertorso" &&
                !compact.StartsWith("lowertorsoorigin", StringComparison.Ordinal)) ||
            (parent == "lowertorso" && compact is not ("uppertorso" or "uppertorsoorigin") &&
             !compact.StartsWith("lowertorsoorigin", StringComparison.Ordinal));
    }

    private static float ProjectY(float x, float y, float z)
    {
        const float yaw = 12f * MathF.PI / 180f, pitch = 18f * MathF.PI / 180f;
        return MathF.Cos(yaw) * MathF.Sin(pitch) * x + MathF.Cos(pitch) * y - MathF.Sin(yaw) * MathF.Sin(pitch) * z;
    }

    /// <summary>
    /// Finds the bottom of the visible upper-body slice in projected
    /// space.  Only the base LowerTorso and UpperTorso joints are considered,
    /// never clothing or arms, so the value remains stable across outfits.
    /// </summary>
    private static float? ComputeBottomCutProjection(MeshData mesh, ElementPose? torso)
    {
        // Kept as a separate helper so the capture callback stays failure-safe.
        // Search the pose tree rather than relying on a particular shape's
        // direct-child layout.
        ElementPose? FindUpper(ElementPose pose)
        {
            if (string.Equals(pose.ForElement.Name, "UpperTorso", StringComparison.OrdinalIgnoreCase)) return pose;
            foreach (var child in pose.ChildElementPoses ?? []) if (FindUpper(child) is { } found) return found;
            return null;
        }
        var upper = torso == null ? null : FindUpper(torso);
        var joints = new HashSet<int>();
        if (torso != null) joints.Add(torso.ForElement.JointId);
        if (upper != null) joints.Add(upper.ForElement.JointId);
        var values = mesh.CustomInts?.Values;
        if (joints.Count == 0 || values == null || mesh.CustomInts!.Count < mesh.VerticesCount) return null;
        var projected = new List<float>();
        var faces = mesh.IndicesCount / mesh.IndicesPerFace;
        for (var face = 0; face < faces; face++)
        {
            var customIndex = face * mesh.VerticesPerFace;
            if (customIndex >= mesh.CustomInts.Count || !joints.Contains(values[customIndex])) continue;
            var indexOffset = face * mesh.IndicesPerFace;
            for (var i = 0; i < mesh.IndicesPerFace; i++)
            {
                var vertex = mesh.Indices[indexOffset + i];
                projected.Add(ProjectY(mesh.xyz[vertex * 3], mesh.xyz[vertex * 3 + 1], mesh.xyz[vertex * 3 + 2]));
            }
        }
        if (projected.Count < 3) return null;
        var min = projected.Min(); var max = projected.Max(); var height = max - min;
        return height > .00001f ? max - height * VisibleUpperBodyFraction : null;
    }

    public static MeshData ExtractHeadMesh(MeshData mesh, ISet<int> joints)
    {
        if (mesh.IndicesPerFace != 6 || mesh.VerticesPerFace != 4 || mesh.CustomInts == null || mesh.CustomInts.Count < mesh.VerticesCount ||
            (mesh.CustomInts.InterleaveStride == 0 ? mesh.CustomInts.InterleaveSizes?.FirstOrDefault() : mesh.CustomInts.InterleaveStride) != 1)
            throw new InvalidDataException("Player mesh has no per-vertex joints");
        var selected = mesh.EmptyClone();
        selected.AddMeshData(mesh, face => joints.Contains(mesh.CustomInts.Values[face * mesh.VerticesPerFace]));
        if (selected.IndicesCount < 3 || selected.IndicesCount > AvatarScene.MaxVertices) throw new InvalidDataException("Player avatar mesh exceeds limits or is empty");
        return selected;
    }
    public static bool Ready(ICoreClientAPI api) => cachedMesh != null && cachedAppearance != null && api.World.Player is { } player && cachedEntity == player.Entity && cachedAppearance == PlayerMapSyncSystem.Appearance(player);
    /// <summary>Called on the graphics/main thread. Only the local player's avatar is exported.</summary>
    public static AvatarScene Capture(ICoreClientAPI api)
    {
        if (!Ready(api)) throw new InvalidOperationException("Player avatar mesh snapshot not ready");
        var scene = CaptureMesh(cachedMesh!, api.EntityTextureAtlas.AtlasTextures.Concat(api.ItemTextureAtlas.AtlasTextures).Concat(api.BlockTextureAtlas.AtlasTextures).ToArray(),
            AvatarScene.MaxVertices, AvatarScene.MaxPixels, cachedBottomCutProjection ?? float.NaN);
        api.Logger.Notification("ServerMap avatar textures packed: {0} faces into {1} pages, {2} pixels.", cachedMesh!.IndicesCount / 6, scene.Textures.Length, scene.Textures.Sum(t => t.Width * t.Height));
        return scene;
    }
    public static AvatarScene CaptureMesh(MeshData mesh, IReadOnlyList<LoadedTexture> atlases, int maxVertices = AvatarScene.MaxVertices,
        int maxPixels = AvatarScene.MaxPixels, float bottomCutProjection = float.NaN)
    {
        if (mesh.IndicesPerFace != 6 || mesh.VerticesPerFace != 4 || mesh.IndicesCount < 6 || mesh.IndicesCount > maxVertices || mesh.IndicesCount % 6 != 0) throw new InvalidDataException("Unsupported capture mesh");
        var textures = new List<AvatarScene.Texture>(); var vertices = new List<AvatarScene.Vertex>();
        var crops = new Dictionary<(int Id, int X, int Y, int W, int H), int>(); var pixelCount = 0;
        for (var face = 0; face < mesh.IndicesCount / 6; face++)
        {
            var textureId = mesh.TextureIds[mesh.TextureIndices[face]];
            // Worn gear can reference item/block atlases, not just the skin atlas.
            var atlas = atlases.FirstOrDefault(t => t.TextureId == textureId) ?? throw new InvalidDataException($"Avatar texture atlas unavailable (texture {textureId})");
            var indices = mesh.Indices.Skip(face * 6).Take(6).ToArray();
            var x = Math.Clamp((int)Math.Floor(indices.Min(i => mesh.Uv[i * 2]) * atlas.Width), 0, atlas.Width - 1);
            var y = Math.Clamp((int)Math.Floor(indices.Min(i => mesh.Uv[i * 2 + 1]) * atlas.Height), 0, atlas.Height - 1);
            var right = Math.Clamp((int)Math.Ceiling(indices.Max(i => mesh.Uv[i * 2]) * atlas.Width), x + 1, atlas.Width);
            var bottom = Math.Clamp((int)Math.Ceiling(indices.Max(i => mesh.Uv[i * 2 + 1]) * atlas.Height), y + 1, atlas.Height);
            var width = right - x; var height = bottom - y; var crop = (textureId, x, y, width, height);
            if (!crops.TryGetValue(crop, out var textureIndex))
            {
                if (width > 512 || height > 512) throw new InvalidDataException($"Avatar texture rectangle exceeds 512 pixels: {width}x{height}");
                if ((pixelCount += width * height) > maxPixels) throw new InvalidDataException($"Avatar texture pixel budget exceeded: {pixelCount}/{AvatarScene.MaxPixels}");
                textureIndex = textures.Count;
                textures.Add(new(width, height, TextureReadback.Read(textureId, x, y, width, height))); crops.Add(crop, textureIndex);
            }
            foreach (var index in indices) vertices.Add(new(mesh.xyz[index * 3], mesh.xyz[index * 3 + 1], mesh.xyz[index * 3 + 2],
                Math.Clamp((mesh.Uv[index * 2] * atlas.Width - x) / width, 0, 1), Math.Clamp((mesh.Uv[index * 2 + 1] * atlas.Height - y) / height, 0, 1), textureIndex));
        }
        return AvatarTexturePacking.Pack(textures, vertices, maxVertices, maxPixels, bottomCutProjection);
    }

    /// <summary>Read just the used atlas rectangle. GL bindings/packing are restored in finally.</summary>
    private static class TextureReadback
    {
        // Graphics is intentionally resolved only on a client; dedicated servers do not ship it.
        private static readonly Type Gl = Type.GetType("OpenTK.Graphics.OpenGL.GL, OpenTK.Graphics", true)!;
        private static readonly Dictionary<string, MethodInfo> methods = new();
        private static object? Call(string name, params object[] args)
        {
            var key = name + "/" + args.Length;
            if (!methods.TryGetValue(key, out var method))
            {
                method = Gl.GetMethods(BindingFlags.Public | BindingFlags.Static).First(m => m.Name == name && !m.IsGenericMethod && m.GetParameters().Length == args.Length && m.GetParameters().Select((p, i) => p.ParameterType == args[i].GetType() || p.ParameterType.IsEnum && args[i] is int).All(b => b));
                methods[key] = method;
            }
            var parameters = method.GetParameters();
            return method.Invoke(null, args.Select((a, i) => parameters[i].ParameterType.IsEnum ? Enum.ToObject(parameters[i].ParameterType, a) : a).ToArray());
        }
        private static int Integer(int name) => (int)Call("GetInteger", name)!;
        public static byte[] Read(int texture, int x, int y, int width, int height)
        {
            var oldFramebuffer = Integer(36010); var oldPackBuffer = Integer(35053);
            int[] packNames = [3333, 3330, 3331, 3332]; var packValues = packNames.Select(Integer).ToArray();
            var framebuffer = (int)Call("GenFramebuffer")!;
            var bytes = new byte[width * height * 4]; var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                Call("BindFramebuffer", 36008, framebuffer);
                Call("FramebufferTexture2D", 36008, 36064, 3553, texture, 0);
                if (Convert.ToInt32(Call("CheckFramebufferStatus", 36008)) != 36053) throw new InvalidOperationException("Head atlas read framebuffer incomplete");
                Call("ReadBuffer", 36064); Call("BindBuffer", 35051, 0);
                for (var i = 0; i < packNames.Length; i++) Call("PixelStore", packNames[i], i == 0 ? 1 : 0);
                Call("ReadPixels", x, y, width, height, 6408, 5121, pinned.AddrOfPinnedObject());
                return bytes;
            }
            finally
            {
                pinned.Free();
                Call("BindFramebuffer", 36008, oldFramebuffer); Call("BindBuffer", 35051, oldPackBuffer);
                for (var i = 0; i < packNames.Length; i++) Call("PixelStore", packNames[i], packValues[i]);
                Call("DeleteFramebuffer", framebuffer);
            }
        }
    }
}
