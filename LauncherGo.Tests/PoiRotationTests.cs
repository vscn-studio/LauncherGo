using System.Text.Json;
using ServerMap.Web;
using Xunit;

namespace LauncherGo.Tests;

public sealed class PoiRotationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "poi-rotation-tests-" + Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(root, "pois.json");
    private static PoiStore.Poi Point(double angle, string id = "legacy") => new(id, "text", "Name", "Description", "#123456", angle, 10, 20, null, null, "alice", DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData(-60, -60)] [InlineData(60, 60)] [InlineData(-30.5, -30.5)]
    [InlineData(-60.01, 0)] [InlineData(60.01, 0)] [InlineData(360, 0)] [InlineData(-180, 0)]
    public void LegacyRotationIsNormalizedAndPersisted(double original, double expected)
    {
        Directory.CreateDirectory(root);
        var before = Point(original);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(new[] { before }));
        var store = new PoiStore(StorePath);
        Assert.Equal(before with { Rotation = expected }, Assert.Single(store.All));
        Assert.Equal(expected, Assert.Single(JsonSerializer.Deserialize<PoiStore.Poi[]>(File.ReadAllText(StorePath))!).Rotation);
        Assert.Equal(expected, Assert.Single(new PoiStore(StorePath).All).Rotation);
    }

    [Fact]
    public void EditorSaveChecksOwnershipAndRetainsRotation()
    {
        var store = new PoiStore(StorePath);
        store.TrySave(Point(0), "alice", 10, false, out var created);
        Assert.Equal(PoiStore.SaveResult.Forbidden, store.TrySave(created! with { Rotation = 30 }, "bob", 10, false, out _));
        Assert.Equal(PoiStore.SaveResult.Saved, store.TrySave(created! with { Rotation = -60 }, "alice", 10, false, out var updated));
        Assert.Equal(created with { Rotation = -60, UpdatedAt = updated!.UpdatedAt }, updated);
        Assert.Equal(-60, Assert.Single(new PoiStore(StorePath).All).Rotation);
    }

    [Fact]
    public void GeneralSaveCannotPersistOutOfRangeAngles()
    {
        var store = new PoiStore(StorePath);
        foreach (var invalid in new[] { 61d, -61d, 390d, double.NaN, double.NegativeInfinity })
        {
            Assert.Equal(PoiStore.SaveResult.Saved, store.TrySave(Point(invalid), "alice", 10, false, out var saved));
            Assert.Equal(0, saved!.Rotation);
        }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
