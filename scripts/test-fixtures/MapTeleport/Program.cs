using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ServerMap.Web;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

if (args.Length != 1) throw new ArgumentException("GameRoot required (run from repository root)");
var gameRoot = Path.GetFullPath(args[0]);
AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
{
    var name = new AssemblyName(request.Name).Name + ".dll";
    foreach (var folder in new[] { gameRoot, Path.Combine(gameRoot, "Lib"), Path.Combine(gameRoot, "Mods") })
    { var file = Path.Combine(folder, name); if (File.Exists(file)) return Assembly.LoadFrom(file); }
    return null;
};
Checks.Run();
static class Checks
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        var settingsRoot = Directory.CreateTempSubdirectory("LauncherGo-teleport-setting-").FullName;
        try
        {
            var path = Path.Combine(settingsRoot, "announcement.json");
            var settings = new AnnouncementStore(path);
            Require(!settings.Current.PlayerGearTeleportEnabled, "Player gear teleport must default to disabled");
            Require(settings.Current.PlayerTeleport == new PlayerTeleportSettings(), "Default teleport policy changed");
            var policy = new PlayerTeleportSettings { ItemCode = "game:gear-rusty", ItemsPerJump = 3, EffectsEnabled = true, StabilityLossPercent = 20, HungerLoss = 150, HealthLoss = 2 };
            settings.Save("test", "https://example.com", "admin", playerTeleport: policy);
            Require(new AnnouncementStore(path).Current.PlayerTeleport == policy && policy.Cost(2) == 6, "Custom teleport policy persistence or per-jump cost failed");
            settings.Save("test", "https://example.com", "admin", playerGearTeleportEnabled: true);
            Require(new AnnouncementStore(path).Current.PlayerGearTeleportEnabled, "Enabled setting did not persist");
            settings.Save("updated", "https://example.com", "admin");
            Require(settings.Current.PlayerGearTeleportEnabled, "Legacy settings save reset teleport permission");
            Require(settings.Current.PlayerTeleport == policy, "Legacy settings save reset custom policy");
            foreach (var invalid in new[] { policy with { ItemCode = "bad:*" }, policy with { ItemsPerJump = 0 }, policy with { ItemsPerJump = -1 }, policy with { StabilityLossPercent = double.NaN }, policy with { StabilityLossPercent = 101 }, policy with { HealthLoss = -1 }, policy with { HungerLoss = float.PositiveInfinity } })
            {
                try { settings.Save("test", "https://example.com", "admin", playerTeleport: invalid); throw new Exception("Invalid policy accepted"); }
                catch (ArgumentException) { Require(settings.Current.PlayerTeleport == policy, "Invalid save changed policy"); }
            }
            try { (policy with { ItemsPerJump = 100000 }).Cost(int.MaxValue); throw new Exception("Cost overflow accepted"); }
            catch (OverflowException) { }
            settings.Save("updated", "https://example.com", "admin", playerGearTeleportEnabled: false);
            Require(!new AnnouncementStore(path).Current.PlayerGearTeleportEnabled, "Disabled setting did not persist");
            File.WriteAllText(path, JsonSerializer.Serialize(new { Html = "legacy", ServerWebsite = "https://example.com", UpdatedBy = "admin", UpdatedAt = DateTimeOffset.UtcNow }));
            Require(!new AnnouncementStore(path).Current.PlayerGearTeleportEnabled, "Legacy settings must default to disabled");
            Require(new AnnouncementStore(path).Current.PlayerTeleport == new PlayerTeleportSettings(), "Legacy settings lost default policy");
        }
        finally { Directory.Delete(settingsRoot, true); }
        var start = new TeleportRoute.Point(0, 100, 0);
        Require(TeleportRoute.Jumps([], start, new(500, 100, 0)) == 0, "No-network travel must have no jumps");
        var one = new TranslocatorPoint(20, 100, 0, 9800, 100, 0);
        Require(TeleportRoute.Jumps([one], start, new(10000, 100, 0)) == 1, "Single jump was not selected");
        Require(TeleportRoute.Jumps([one], start, new(100, 100, 0)) == 0, "Nearby direct travel must not become a paid jump");
        Require(TeleportRoute.Jumps([one, new(9800, 100, 0, 20, 100, 0), new(9820, 100, 0, 20000, 100, 0)], start, new(20010, 100, 0)) == 2, "Two-hop route or reciprocal deduplication failed");
        var vectors = new List<object>(); var expected = new List<int>(); var random = new Random(5027);
        for (var test = 0; test < 100; test++)
        {
            var links = Enumerable.Range(0, random.Next(1, 40)).Select(_ => new TranslocatorPoint(random.Next(10000), random.Next(150), random.Next(10000), random.Next(10000), random.Next(150), random.Next(10000))).ToArray();
            var from = new TeleportRoute.Point(random.Next(10000), random.Next(150), random.Next(10000));
            var to = new TeleportRoute.Point(random.Next(10000), random.Next(150), random.Next(10000));
            vectors.Add(new { links, start = new[] { from.X, from.Z, from.Y }, end = new[] { to.X, to.Z, to.Y } });
            expected.Add(TeleportRoute.Jumps(links, from, to));
        }
        var info = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("scripts/test-map-teleport-route.cjs");
        using var node = Process.Start(info)!;
        node.StandardInput.Write(JsonSerializer.Serialize(vectors)); node.StandardInput.Close();
        var result = node.StandardOutput.ReadToEnd(); var error = node.StandardError.ReadToEnd(); node.WaitForExit();
        Require(node.ExitCode == 0, error);
        Require(JsonSerializer.Deserialize<int[]>(result)!.SequenceEqual(expected), "Server jump counts differ from the map measurement tool");

        ItemSlot Slot(string code, int id, int quantity) => new(null) { Itemstack = new ItemStack(new Item { Code = new AssetLocation(code), ItemId = id }, quantity) };
        var wrong = Slot("game:gear-rusty", 1899, 99);
        var a = Slot(TemporalGearPayment.Code, 777, 1); var b = Slot(TemporalGearPayment.Code, 777, 3);
        Require(TemporalGearPayment.Count([wrong, a, b, a]) == 4, "Gear identity or duplicate slot handling is incorrect");
        var calls = 0;
        Require(!TemporalGearPayment.Execute([a, b], 5, () => calls++) && a.StackSize == 1 && b.StackSize == 3 && calls == 0, "Insufficient gears changed inventory or teleported");
        Require(TemporalGearPayment.Execute([wrong, a, b], 2, () => calls++) && a.Empty && b.StackSize == 2 && wrong.StackSize == 99 && calls == 1, "Exact payment across stacks failed");
        try { TemporalGearPayment.Execute([b], 1, () => throw new IOException("test teleport failure")); throw new Exception("Failure was swallowed"); }
        catch (IOException) { Require(b.StackSize == 2, "Failed teleport did not restore payment"); }
        Require(TemporalGearPayment.Execute([], 0, () => calls++) && calls == 2, "Admin zero-cost teleport failed");
        var pending = new GameThreadCall<bool>(() => TemporalGearPayment.Execute([b], 1, () => calls++));
        Require(pending.CancelPending(), "Pending teleport could not be cancelled"); pending.Run();
        Require(b.StackSize == 2 && calls == 2, "Late chunk callback charged or teleported after timeout");
        var custom = Slot("game:gear-rusty", 42, 8);
        Require(TemporalGearPayment.Count([a,b,custom], "game:gear-rusty") == 8, "Custom item count mixed item codes");
        Require(TemporalGearPayment.Execute([b,custom], 6, () => { }, "game:gear-rusty") && custom.StackSize == 2 && b.StackSize == 2, "Custom payment consumed temporal gears");
        var effectSettings = new PlayerTeleportSettings { EffectsEnabled = true, StabilityLossPercent = 25, HungerLoss = 100, HealthLoss = 2 };
        Require(TeleportEffects.Check(effectSettings, true, true, 2) == "teleport_health", "Lethal health payment allowed");
        Require(TeleportEffects.Check(effectSettings, false, true, 10) == "teleport_effects_unavailable", "Missing behavior accepted");
        Require(TeleportEffects.Check(effectSettings with { EffectsEnabled = false }, false, false, null) == null, "Disabled effects still required attributes");
        var entity = new EffectEntity();
        entity.WatchedAttributes.SetAttribute("health", new TreeAttribute());
        var hungerTree = new TreeAttribute(); entity.WatchedAttributes.SetAttribute("hunger", hungerTree);
        var health = new EntityBehaviorHealth(entity); health.Health = 10;
        var hunger = new EntityBehaviorHunger(entity);
        typeof(EntityBehaviorHunger).GetField("hungerTree", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(hunger, hungerTree);
        hunger.Saturation = 50;
        var stability = new EntityBehaviorTemporalStabilityAffected(entity); stability.OwnStability = .2;
        entity.TestBehaviors.AddRange([health,hunger,stability]);
        var effects = TeleportEffects.Prepare(entity, effectSettings);
        Require(effects.Error == null && health.Health == 10 && hunger.Saturation == 50 && stability.OwnStability == .2, "Preparing a quote applied effects");
        effects.Apply();
        Require(health.Health == 8 && hunger.Saturation == 0 && stability.OwnStability == 0, "Native health/hunger/stability effects or clamping failed");
        TeleportEffects.Prepare(entity, effectSettings with { EffectsEnabled = false }).Apply();
        Require(health.Health == 8, "Disabled effects deducted health");
        Console.WriteLine("PASS custom item policy validation/persistence, per-jump pricing, native side effects, health protection and defaults");
        Console.WriteLine("PASS 100 routes match browser measurement; zero/one/two jumps; gear code and split-stack payment; insufficient funds, rollback, admin and late callback");
    }
}

sealed class EffectEntity : EntityAgent
{
    public List<EntityBehavior> TestBehaviors { get; } = [];
    public override T GetBehavior<T>() => TestBehaviors.OfType<T>().FirstOrDefault()!;
}
