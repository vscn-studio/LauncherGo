using HarmonyLib;
using ServerMap.Render;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ServerMap.Client;

/// <summary>Snapshot only the local player's current mount; never mutate native meshes or animator state.</summary>
public sealed class ClientMountCapture : IDisposable
{
    private static ClientMountCapture? active;
    private readonly ICoreClientAPI api;
    private readonly Harmony harmony = new("servermap-mount-shape-snapshot");
    private Entity? target;
    private MeshData? mesh;
    private IAnimator? meshAnimator;
    public ClientMountCapture(ICoreClientAPI api)
    {
        this.api = api; active = this;
        harmony.Patch(AccessTools.Method(typeof(EntityShapeRenderer), nameof(EntityShapeRenderer.TesselateShape), [typeof(Action<MeshData>), typeof(string[])]),
            prefix: new HarmonyMethod(typeof(ClientMountCapture), nameof(BeforeTesselate)));
    }
    public void Request(Entity entity) { target = entity; mesh = null; meshAnimator = null; entity.MarkShapeModified(); }
    public void Clear() { target = null; mesh = null; meshAnimator = null; }
    public bool Ready(Entity entity) => target == entity && mesh != null;
    private static void BeforeTesselate(EntityShapeRenderer __instance, ref Action<MeshData> onMeshDataReady)
    {
        var self = active; var entity = self?.target;
        if (self == null || entity == null || entity.Properties.Client.Renderer != __instance) return;
        var original = onMeshDataReady;
        onMeshDataReady = data =>
        {
            try
            {
                if (active == self && self.target == entity && self.api.World.Player is { } rider && MountMapSyncSystem.MountedEntity(rider) == entity
                    && data.IndicesCount > 0 && data.IndicesCount <= TopDownMountRenderer.MaxVertices && data.VerticesCount <= TopDownMountRenderer.MaxVertices)
                { self.mesh = data.Clone(); self.meshAnimator = entity.AnimManager?.Animator; }
            }
            catch (Exception ex) { self.api.Logger.Warning("ServerMap mount snapshot failed: {0}", ex.Message); }
            finally { original(data); }
        };
    }
    public AvatarScene Capture(Entity entity)
    {
        if (!Ready(entity) || MountMapSyncSystem.MountedEntity(api.World.Player) != entity) throw new InvalidOperationException("Mount changed during capture");
        if (!ReferenceEquals(meshAnimator, entity.AnimManager?.Animator)) throw new InvalidOperationException("Mount skeleton changed during capture");
        var snapshot = mesh!.Clone();
        var custom = snapshot.CustomInts;
        if (custom != null)
        {
            if (custom.Count < snapshot.VerticesCount || (custom.InterleaveStride == 0 ? custom.InterleaveSizes?.FirstOrDefault() : custom.InterleaveStride) != 1)
                throw new InvalidDataException("Mount mesh has unsupported joint layout");
            var maxJoint = custom.Values.Take(snapshot.VerticesCount).DefaultIfEmpty().Max();
            var idle = entity.Properties.Client.AnimationsByMetaCode?.GetValueOrDefault("idle")?.Animation;
            var joints = MountIdlePose.Matrices(entity is EntityAgent, meshAnimator, maxJoint, idle);
            for (var i = 0; i < snapshot.VerticesCount; i++)
            {
                var joint = custom.Values[i];
                if (joint < 0 || joint > maxJoint) throw new InvalidDataException("Invalid mount animation joint");
                var offset = joint * 16;
                Transform(snapshot.xyz, i * 3, joints, offset);
            }
        }
        // Native model transform at yaw=0, without water bobbing or walking sway.
        // Composite offsets/rotations already applied during tessellation are deliberately
        // followed by the renderer's additional shape transform, matching the game.
        var shape = entity.Properties.Client.Shape; var size = entity.Properties.Client.Size;
        var matrix = Mat4f.Create(); var pivot = entity.SelectionBox.Y2 / 2;
        Mat4f.Translate(matrix, matrix, 0, pivot, 0);
        Mat4f.RotateX(matrix, matrix, (shape?.rotateX ?? 0) * GameMath.DEG2RAD);
        Mat4f.RotateY(matrix, matrix, ((shape?.rotateY ?? 0) + 90) * GameMath.DEG2RAD);
        Mat4f.RotateZ(matrix, matrix, (shape?.rotateZ ?? 0) * GameMath.DEG2RAD);
        Mat4f.Translate(matrix, matrix, 0, -pivot, 0);
        Mat4f.Scale(matrix, matrix, new[] { size, size, size }); Mat4f.Translate(matrix, matrix, -.5f, 0, -.5f);
        for (var i = 0; i < snapshot.VerticesCount; i++) Transform(snapshot.xyz, i * 3, matrix, 0);
        return ClientHeadCapture.CaptureMesh(snapshot, api.EntityTextureAtlas.AtlasTextures.Concat(api.ItemTextureAtlas.AtlasTextures).Concat(api.BlockTextureAtlas.AtlasTextures).ToArray(), TopDownMountRenderer.MaxVertices, TopDownMountRenderer.MaxPixels);
    }
    private static void Transform(float[] xyz, int i, float[] m, int o)
    {
        var x = xyz[i]; var y = xyz[i + 1]; var z = xyz[i + 2];
        xyz[i] = m[o] * x + m[o + 4] * y + m[o + 8] * z + m[o + 12];
        xyz[i + 1] = m[o + 1] * x + m[o + 5] * y + m[o + 9] * z + m[o + 13];
        xyz[i + 2] = m[o + 2] * x + m[o + 6] * y + m[o + 10] * z + m[o + 14];
    }
    public void Dispose() { Clear(); if (active == this) active = null; harmony.UnpatchAll(harmony.Id); }
}
