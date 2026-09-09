using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace ServerMap.Web;

public static class TeleportEffects
{
    public static string? Check(PlayerTeleportSettings settings, bool stability, bool hunger, float? health)
    {
        if (!settings.EffectsEnabled) return null;
        if (settings.StabilityLossPercent > 0 && !stability || settings.HungerLoss > 0 && !hunger || settings.HealthLoss > 0 && health == null)
            return "teleport_effects_unavailable";
        return settings.HealthLoss > 0 && (!float.IsFinite(health!.Value) || health <= settings.HealthLoss) ? "teleport_health" : null;
    }

    // Prepare and validate before taking items; invoke only after a successful
    // teleport. Numeric setters synchronize the game's native watched attributes.
    public static (string? Error, Action Apply) Prepare(Entity entity, PlayerTeleportSettings settings)
    {
        if (!settings.EffectsEnabled) return (null, () => { });
        var stability = entity.GetBehavior<EntityBehaviorTemporalStabilityAffected>();
        var hunger = entity.GetBehavior<EntityBehaviorHunger>();
        var health = entity.GetBehavior<EntityBehaviorHealth>();
        var error = Check(settings, stability != null, hunger != null, health?.Health);
        return (error, () => {
            if (error != null) throw new InvalidOperationException(error);
            if (settings.StabilityLossPercent > 0) stability!.OwnStability = Math.Max(0, stability.OwnStability - settings.StabilityLossPercent / 100);
            if (settings.HungerLoss > 0) hunger!.Saturation = Math.Max(0, hunger.Saturation - settings.HungerLoss);
            if (settings.HealthLoss > 0) health!.Health = Math.Max(float.Epsilon, health.Health - settings.HealthLoss);
        });
    }
}
