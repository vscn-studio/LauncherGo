using System.Text.Json.Nodes;
using System.Reflection;
using System.Collections;
using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class ServerBridgePlayerSnapshotTests
{
    [Fact]
    public void MissingBridgePreservesLogStatusAndDashboardRoster()
    {
        var service = new ServerProcessService();
        var local = LogStatus("one", ["Alice", "Bob"]);
        Publish(service, local);
        Assert.Same(local, ServerProcessService.MergeBridgePlayers(local, null));
        Assert.Equal(2, service.GetCurrentStatus("one").OnlinePlayers);
        var players = service.GetOnlinePlayers();
        Assert.Equal(new[] { "Alice", "Bob" }, players.Select(p => p.PlayerName));
        Assert.All(players, p => { Assert.Equal("one", p.ProfileId); Assert.Null(p.PingMilliseconds); Assert.Null(p.JoinedAtUtc); Assert.False(p.HasExtendedInfo); });
        Publish(service, LogStatus("one", ["Bob"]));
        Assert.Equal("Bob", Assert.Single(service.GetOnlinePlayers()).PlayerName);
        Publish(service, new ServerRuntimeStatus { ProfileId = "one", IsRunning = false });
        Assert.Empty(service.GetOnlinePlayers());
    }

    [Fact]
    public void EachProfileUsesItsOwnSourceAndEmptyBridgeRosterIsAuthoritative()
    {
        var service = new ServerProcessService();
        SetSnapshot(service, "bridge", [], DateTimeOffset.UtcNow);
        Publish(service, LogStatus("bridge", ["StaleLogPlayer"]));
        Publish(service, LogStatus("logs", ["Alice"]));
        Assert.Equal(0, service.GetCurrentStatus("bridge").OnlinePlayers);
        Assert.Equal("logs", Assert.Single(service.GetOnlinePlayers()).ProfileId);
        SetSnapshot(service, "bridge", [new() { ProfileId = "bridge", PlayerName = "BridgePlayer", PingMilliseconds = 42 }], DateTimeOffset.UtcNow);
        Publish(service, LogStatus("bridge", ["StaleLogPlayer"]));
        Assert.Equal(2, service.GetOnlinePlayers().Count);
        Assert.Equal(42, service.GetOnlinePlayers().Single(p => p.ProfileId == "bridge").PingMilliseconds);
        Assert.Equal(new[] { "BridgePlayer" }, service.GetOnlinePlayerNames("bridge"));
    }

    [Fact]
    public void ExpiredBridgeSnapshotRestoresLatestLogRosterRatherThanMergedRoster()
    {
        var service = new ServerProcessService();
        var bridge = new ServerOnlinePlayerInfo[] { new() { PlayerName = "BridgePlayer", ProfileId = "one" } };
        SetSnapshot(service, "one", bridge, DateTimeOffset.UtcNow);
        Publish(service, LogStatus("one", ["Alice", "Bob"]));
        Assert.Equal("BridgePlayer", Assert.Single(service.GetOnlinePlayers()).PlayerName);
        SetSnapshot(service, "one", bridge, DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal(2, Assert.Single(service.GetCachedStatuses()).OnlinePlayers);
        Assert.Equal(new[] { "Alice", "Bob" }, service.GetOnlinePlayerNames());
        Assert.Equal(2, service.GetCurrentStatus("one").OnlinePlayers);
    }

    [Fact]
    public void LogAbsoluteCountCanExceedKnownNames()
    {
        var service = new ServerProcessService();
        Publish(service, new() { ProfileId = "one", IsRunning = true, OnlinePlayers = 5, OnlinePlayerNames = ["Alice"] });
        Assert.Equal(5, service.GetCurrentStatus("one").OnlinePlayers);
        Assert.Single(service.GetOnlinePlayers());
    }

    private static ServerRuntimeStatus LogStatus(string id, string[] names) => new()
    {
        ProfileId = id, IsRunning = true, OnlinePlayerNames = names, OnlinePlayers = names.Length
    };
    private static void Publish(ServerProcessService service, ServerRuntimeStatus status) =>
        typeof(ServerProcessService).GetMethod("OnControllerStatusChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [new InstanceProfile { Id = status.ProfileId!, Name = status.ProfileId! }, status]);
    private static void SetSnapshot(ServerProcessService service, string id, IReadOnlyList<ServerOnlinePlayerInfo> players, DateTimeOffset at)
    {
        var type = typeof(ServerProcessService);
        var snapshot = Activator.CreateInstance(type.GetNestedType("BridgePlayerSnapshot", BindingFlags.NonPublic)!, at, players);
        ((IDictionary)type.GetField("_bridgePlayers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!)[id] = snapshot;
    }

    [Fact]
    public void ParseBridgePlayers_ReadsBaseAndExtendedFieldsAndSkipsOfflinePlayers()
    {
        var profile = new InstanceProfile { Id = "profile-1", Name = "生存服" };
        var data = new JsonObject
        {
            ["players"] = new JsonArray
            {
                new JsonObject
                {
                    ["uid"] = "uid-alice",
                    ["name"] = "Alice",
                    ["online"] = true,
                    ["connectionState"] = "Playing",
                    ["pingMs"] = 42,
                    ["joinedAtUtc"] = "2026-09-05T01:02:03Z",
                    ["lastActivityUtc"] = "2026-09-05T01:04:05Z",
                    ["gameMode"] = "Survival",
                    ["role"] = "suplayer",
                    ["dimension"] = 0,
                    ["x"] = 12.5,
                    ["y"] = 110.0,
                    ["z"] = -8.25
                },
                new JsonObject
                {
                    ["uid"] = "uid-bob",
                    ["name"] = "Bob",
                    ["online"] = false
                },
                new JsonObject
                {
                    ["uid"] = "uid-charlie",
                    ["name"] = "Charlie",
                    ["connectionState"] = "Offline"
                }
            }
        };

        var player = Assert.Single(ServerProcessService.ParseBridgePlayers(profile, data));

        Assert.Equal("uid-alice", player.PlayerUid);
        Assert.Equal("Alice", player.PlayerName);
        Assert.Equal("profile-1", player.ProfileId);
        Assert.Equal("生存服", player.ProfileName);
        Assert.Equal(42, player.PingMilliseconds);
        Assert.Equal("Playing", player.ConnectionState);
        Assert.Equal(DateTimeOffset.Parse("2026-09-05T01:02:03Z"), player.JoinedAtUtc);
        Assert.Equal("Survival", player.GameMode);
        Assert.Equal("suplayer", player.Role);
        Assert.Equal(0, player.Dimension);
        Assert.Equal(12.5, player.X);
        Assert.True(player.HasExtendedInfo);
    }

    [Fact]
    public void MergeBridgePlayers_DoesNotUseLogDerivedPlayerState()
    {
        var logDerivedStatus = new ServerRuntimeStatus
        {
            IsRunning = true,
            ProfileId = "profile-1",
            OnlinePlayers = 7,
            OnlinePlayerNames = ["FromLog"],
            PeakOnlinePlayers = 12
        };

        var merged = ServerProcessService.MergeBridgePlayers(logDerivedStatus, []);

        Assert.Equal(0, merged.OnlinePlayers);
        Assert.Empty(merged.OnlinePlayerNames);
        Assert.Equal(0, merged.PeakOnlinePlayers);
    }
}
