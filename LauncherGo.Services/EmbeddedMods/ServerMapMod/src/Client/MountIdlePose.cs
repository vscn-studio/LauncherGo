using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ServerMap.Client;

/// <summary>Evaluate an animal's standing pose without advancing or modifying its live animator.</summary>
public static class MountIdlePose
{
    public static float[] Matrices(bool animal, IAnimator? live, int maxJointId, string? idleAnimation = null)
    {
        if (maxJointId is < 0 or > 4095) throw new InvalidDataException("Mount joint count out of bounds");
        // Vehicle animations may encode sail/propeller/attachment state. Do not reset those to animal idle.
        if (!animal && live?.Matrices is { } current) return Validate(current.ToArray(), maxJointId);
        if (!animal || live is not ClientAnimator animator || animator.RootElements == null) return Neutral(maxJointId);
        var animations = animator.Animations.Select(a => a.Animation).ToArray();
        Animation? standing = null;
        foreach (var code in new[] { idleAnimation, "idle", "stand", "standing" })
        {
            if (string.IsNullOrWhiteSpace(code)) continue;
            standing = animations.FirstOrDefault(a => string.Equals(a.Code, code, StringComparison.OrdinalIgnoreCase) || string.Equals(a.Name, code, StringComparison.OrdinalIgnoreCase));
            if (standing != null) break;
        }
        // Unknown animals use their authored bind pose, NEVER a frozen run/jump/swim frame.
        if (standing == null) return Neutral(maxJointId);
        if (standing.QuantityFrames is < 1 or > 10000 || standing.KeyFrames is not { Length: > 0 and <= 2048 })
            throw new InvalidDataException("Invalid mount idle animation");

        var roots = animator.RootElements.Select(e => e.Clone()).ToArray();
        var clones = new Dictionary<ShapeElement, ShapeElement>(); var byName = new Dictionary<string, ShapeElement>();
        void Map(ShapeElement original, ShapeElement clone)
        {
            clones.Add(original, clone); if (clone.Name != null) byName[clone.Name] = clone;
            // ShapeElement.Clone shallow-copies attachment points; they are unnecessary for bone evaluation.
            clone.AttachmentPoints = null;
            for (var i = 0; i < (original.Children?.Length ?? 0); i++) Map(original.Children![i], clone.Children![i]);
        }
        for (var i = 0; i < roots.Length; i++) Map(animator.RootElements[i], roots[i]);
        var joints = animator.jointsById.ToDictionary(pair => pair.Key, pair => new AnimationJoint { JointId = pair.Value.JointId, Element = clones[pair.Value.Element] });

        // Native Animation.Clone shares a mutable joint scratch set; KeyFrame.Clone shares its values.
        // Allocate all mutable animation data ourselves to avoid touching game caches or references.
        const string codeForSnapshot = "servermap-standing-snapshot";
        var animation = new Animation { Code = codeForSnapshot, Name = codeForSnapshot, Version = standing.Version,
            QuantityFrames = standing.QuantityFrames, OnAnimationEnd = EnumEntityAnimationEndHandling.Repeat,
            OnActivityStopped = EnumEntityActivityStoppedHandling.EaseOut,
            KeyFrames = standing.KeyFrames.Select(frame => new AnimationKeyFrame { Frame = frame.Frame,
                Elements = (frame.Elements ?? []).ToDictionary(pair => pair.Key, pair => Copy(pair.Value)) }).ToArray() };
        foreach (var frame in animation.KeyFrames)
        {
            foreach (var (name, value) in frame.Elements) value.ForElement = byName.GetValueOrDefault(name);
            frame.Resolve(byName);
        }
        // No callbacks, entity, head controller, sounds, live poses or movement-speed supplier.
        var isolated = new ClientAnimator(() => 1, [animation], roots, joints);
        var meta = new AnimationMetaData { Code = codeForSnapshot, Animation = codeForSnapshot, AnimationSpeed = 1,
            Weight = 1, BlendMode = EnumAnimationBlendMode.Average, MulWithWalkSpeed = false }.Init();
        var active = new Dictionary<string, AnimationMetaData> { [codeForSnapshot] = meta };
        isolated.OnFrame(active, 0);
        var running = isolated.GetAnimationState(codeForSnapshot);
        running.CurrentFrame = 0; running.EasingFactor = 1;
        // dt=0 fixes the first idle frame. AnimationSpeed must remain nonzero: the native
        // blender skips weight calculation entirely at speed=0, leaving the bind pose instead.
        isolated.OnFrame(active, 0);
        return Validate(isolated.Matrices.ToArray(), maxJointId);
    }

    private static float[] Neutral(int maxJointId)
    {
        var result = new float[(maxJointId + 1) * 16]; var identity = Mat4f.Create();
        for (var i = 0; i < result.Length; i += 16) identity.CopyTo(result, i);
        return result;
    }
    private static float[] Validate(float[] matrices, int maxJointId) => matrices.Length >= (maxJointId + 1) * 16 && matrices.All(float.IsFinite)
        ? matrices : throw new InvalidDataException("Mount pose matrices are unavailable or invalid");
    private static AnimationKeyFrameElement Copy(AnimationKeyFrameElement e) => new()
    {
        OffsetX = e.OffsetX, OffsetY = e.OffsetY, OffsetZ = e.OffsetZ, RotationX = e.RotationX, RotationY = e.RotationY, RotationZ = e.RotationZ,
        StretchX = e.StretchX, StretchY = e.StretchY, StretchZ = e.StretchZ, OriginX = e.OriginX, OriginY = e.OriginY, OriginZ = e.OriginZ,
        RotShortestDistanceX = e.RotShortestDistanceX, RotShortestDistanceY = e.RotShortestDistanceY, RotShortestDistanceZ = e.RotShortestDistanceZ
    };
}
