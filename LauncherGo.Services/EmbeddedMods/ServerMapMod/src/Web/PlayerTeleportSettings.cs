using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace ServerMap.Web;

public sealed record PlayerTeleportSettings
{
    public const string DefaultItemCode = "game:gear-temporal";
    [JsonPropertyName("itemCode")] public string ItemCode { get; init; } = DefaultItemCode;
    [JsonPropertyName("itemsPerJump")] public int ItemsPerJump { get; init; } = 1;
    [JsonPropertyName("effectsEnabled")] public bool EffectsEnabled { get; init; }
    [JsonPropertyName("stabilityLossPercent")] public double StabilityLossPercent { get; init; }
    [JsonPropertyName("hungerLoss")] public float HungerLoss { get; init; }
    [JsonPropertyName("healthLoss")] public float HealthLoss { get; init; }

    public PlayerTeleportSettings Validate()
    {
        if (ItemCode == null || ItemCode.Length > 256 || !Regex.IsMatch(ItemCode, @"\A[a-z0-9_-]+:[a-z0-9_./-]+\z")
            || ItemsPerJump is < 1 or > 100000
            || !double.IsFinite(StabilityLossPercent) || StabilityLossPercent is < 0 or > 100
            || !float.IsFinite(HungerLoss) || HungerLoss is < 0 or > 100000
            || !float.IsFinite(HealthLoss) || HealthLoss is < 0 or > 100000)
            throw new ArgumentException("Invalid player teleport settings");
        return this;
    }

    public int Cost(int jumps) => jumps < 0 ? throw new ArgumentOutOfRangeException(nameof(jumps)) : checked(jumps * ItemsPerJump);
}
