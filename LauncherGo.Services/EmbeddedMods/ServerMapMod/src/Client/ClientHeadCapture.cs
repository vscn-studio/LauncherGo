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
    private static ICoreClientAPI? cacheApi;
    private static MeshData? cachedMesh;
    private static string? cachedAppearance;
    private static Entity? cachedEntity;
    private sealed class CaptureSession : IDisposable
    {
        private readonly Harmony harmony = new("servermap-avatar-shape-snapshot");
        public CaptureSession(ICoreClientAPI api)
        {
            cacheApi = api; cachedMesh = null; cachedAppearance = null; cachedEntity = null;
            harmony.Patch(AccessTools.Method(typeof(EntityShapeRenderer), nameof(EntityShapeRenderer.TesselateShape), [typeof(Action<MeshData>), typeof(string[])]),
                prefix: new HarmonyMethod(typeof(ClientHeadCapture), nameof(BeforeTesselate)));
        }
        public void Dispose() { harmony.UnpatchAll(harmony.Id); cacheApi = null; cachedMesh = null; cachedAppearance = null; cachedEntity = null; }
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
                var head = entity.AnimManager?.Animator?.GetPosebyName("Head") ?? entity.AnimManager?.Animator?.GetPosebyName("head");
                if (head == null) throw new InvalidOperationException("Player animator has no head pose");
                var joints = new HashSet<int>();
                void Collect(ElementPose pose)
                {
                    joints.Add(pose.ForElement.JointId);
                    foreach (var child in pose.ChildElementPoses ?? []) Collect(child);
                }
                Collect(head);
                cachedMesh = ExtractHeadMesh(mesh, joints); cachedAppearance = appearance; cachedEntity = entity;
                api.Logger.Notification("ServerMap head mesh snapshot ready: {0} vertices, {1} faces.", cachedMesh.VerticesCount, cachedMesh.IndicesCount / 6);
            }
            catch (Exception ex) { cachedMesh = null; cachedAppearance = null; api?.Logger.Warning("ServerMap head snapshot unavailable: {0}", ex.Message); }
            finally { original(mesh); } // Never disrupt native mesh upload or live character animation.
        };
    }
    public static MeshData ExtractHeadMesh(MeshData mesh, ISet<int> joints)
    {
        if (mesh.IndicesPerFace != 6 || mesh.VerticesPerFace != 4 || mesh.CustomInts == null || mesh.CustomInts.Count < mesh.VerticesCount ||
            (mesh.CustomInts.InterleaveStride == 0 ? mesh.CustomInts.InterleaveSizes?.FirstOrDefault() : mesh.CustomInts.InterleaveStride) != 1)
            throw new InvalidDataException("Player mesh has no per-vertex joints");
        var head = mesh.EmptyClone();
        head.AddMeshData(mesh, face => joints.Contains(mesh.CustomInts.Values[face * mesh.VerticesPerFace]));
        if (head.IndicesCount < 3 || head.IndicesCount > AvatarScene.MaxVertices) throw new InvalidDataException("Player head mesh exceeds limits or is empty");
        return head;
    }
    public static bool Ready(ICoreClientAPI api) => cachedMesh != null && cachedAppearance != null && api.World.Player is { } player && cachedEntity == player.Entity && cachedAppearance == PlayerMapSyncSystem.Appearance(player);
    /// <summary>Called on the graphics/main thread. Only the local player's head is exported.</summary>
    public static AvatarScene Capture(ICoreClientAPI api)
    {
        if (!Ready(api)) throw new InvalidOperationException("Player head mesh snapshot not ready");
        var scene = CaptureMesh(cachedMesh!, api.EntityTextureAtlas.AtlasTextures.Concat(api.ItemTextureAtlas.AtlasTextures).Concat(api.BlockTextureAtlas.AtlasTextures).ToArray());
        api.Logger.Notification("ServerMap head textures packed: {0} faces into {1} pages, {2} pixels.", cachedMesh!.IndicesCount / 6, scene.Textures.Length, scene.Textures.Sum(t => t.Width * t.Height));
        return scene;
    }
    public static AvatarScene CaptureMesh(MeshData mesh, IReadOnlyList<LoadedTexture> atlases)
    {
        var textures = new List<AvatarScene.Texture>(); var vertices = new List<AvatarScene.Vertex>();
        var crops = new Dictionary<(int Id, int X, int Y, int W, int H), int>(); var pixelCount = 0;
        for (var face = 0; face < mesh.IndicesCount / 6; face++)
        {
            var textureId = mesh.TextureIds[mesh.TextureIndices[face]];
            // Worn headgear can reference item/block atlases, not just the skin atlas.
            var atlas = atlases.FirstOrDefault(t => t.TextureId == textureId) ?? throw new InvalidDataException($"Head texture atlas unavailable (texture {textureId})");
            var indices = mesh.Indices.Skip(face * 6).Take(6).ToArray();
            var x = Math.Clamp((int)Math.Floor(indices.Min(i => mesh.Uv[i * 2]) * atlas.Width), 0, atlas.Width - 1);
            var y = Math.Clamp((int)Math.Floor(indices.Min(i => mesh.Uv[i * 2 + 1]) * atlas.Height), 0, atlas.Height - 1);
            var right = Math.Clamp((int)Math.Ceiling(indices.Max(i => mesh.Uv[i * 2]) * atlas.Width), x + 1, atlas.Width);
            var bottom = Math.Clamp((int)Math.Ceiling(indices.Max(i => mesh.Uv[i * 2 + 1]) * atlas.Height), y + 1, atlas.Height);
            var width = right - x; var height = bottom - y; var crop = (textureId, x, y, width, height);
            if (!crops.TryGetValue(crop, out var textureIndex))
            {
                if (width > 512 || height > 512) throw new InvalidDataException($"Head texture rectangle exceeds 512 pixels: {width}x{height}");
                if ((pixelCount += width * height) > AvatarScene.MaxPixels) throw new InvalidDataException($"Head texture pixel budget exceeded: {pixelCount}/{AvatarScene.MaxPixels}");
                textureIndex = textures.Count;
                textures.Add(new(width, height, TextureReadback.Read(textureId, x, y, width, height))); crops.Add(crop, textureIndex);
            }
            foreach (var index in indices) vertices.Add(new(mesh.xyz[index * 3], mesh.xyz[index * 3 + 1], mesh.xyz[index * 3 + 2],
                Math.Clamp((mesh.Uv[index * 2] * atlas.Width - x) / width, 0, 1), Math.Clamp((mesh.Uv[index * 2 + 1] * atlas.Height - y) / height, 0, 1), textureIndex));
        }
        return AvatarTexturePacking.Pack(textures, vertices);
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
