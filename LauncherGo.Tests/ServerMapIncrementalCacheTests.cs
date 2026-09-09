using ServerMap.Render;
using Xunit;

namespace LauncherGo.Tests;

public sealed class ServerMapIncrementalCacheTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("map-incremental-").FullName;
    private string Database => Path.Combine(root, "cache-state.db");
    [Fact]
    public void RestartRestoresWorkAndCleanMarkerWithoutDiscoveringRegionsAgain()
    {
        long revision;
        using (var state = new MapCacheState(Database))
        {
            state.Set("initialized", "yes"); state.Set("scan", "done");
            var task = state.Request("10_20", "build", true); revision = task.Revision;
            state.CompleteColumn("10_20", 0, task.Columns[0]);
            state.Close(true);
        }
        using (var state = new MapCacheState(Database))
        {
            Assert.False(state.RecoveryRequired); Assert.Equal("yes", state.Get("initialized"));
            Assert.Equal(new[] { "10_20" }, state.Regions);
            var task = state.Pending["10_20"]; Assert.Equal(revision, task.Revision);
            Assert.Equal(255, task.Columns.Count); Assert.DoesNotContain(0, task.Columns.Keys);
            state.Complete("10_20", task.Revision); state.Close(true);
        }
        using var next = new MapCacheState(Database);
        Assert.Empty(next.Pending); Assert.False(next.RecoveryRequired);
        Assert.True(next.MarkDirty("160_1_320") > revision);
    }
    [Fact]
    public void SaveProgressCountsColumnsWithoutDiscardingVerticalDirtyGenerations()
    {
        using var state = new MapCacheState(Database);
        state.MarkDirty("1_0_3"); state.MarkDirty("1_4_3"); state.MarkDirty("2_4_3");
        Assert.Equal(2, state.AwaitingSave); Assert.Equal(3, state.AwaitingSaveChunks);
        Assert.Equal(3, state.Freeze().Count);
    }
    [Fact]
    public void DeferredWorldGenerationSurvivesRestartWithoutCreatingSaveOrRenderWork()
    {
        using (var state = new MapCacheState(Database))
        {
            state.Set("column:1_3", "yes");
            Assert.False(state.NeedsGeneratedColumn("1_3"));
            state.SetGenerationPending("1_3", true);
            Assert.True(state.NeedsGeneratedColumn("1_3"));
            Assert.Empty(state.Freeze()); Assert.Empty(state.Pending);
            state.Close(true);
        }
        using var restored = new MapCacheState(Database);
        Assert.Equal(1, restored.DeferredGeneration); Assert.Empty(restored.Freeze());
        Assert.True(restored.NeedsGeneratedColumn("1_3"));
        var generated = restored.MarkDirty("1_0_3");
        var frozen = restored.Freeze(); var changedAgain = restored.MarkDirty("1_0_3");
        restored.ConfirmSaved("1_0_3", frozen["1_0_3"]);
        restored.SetGenerationPending("1_3", false);
        Assert.Equal(0, restored.DeferredGeneration); Assert.False(restored.NeedsGeneratedColumn("1_3"));
        Assert.Equal(changedAgain, restored.Freeze()["1_0_3"]);
        Assert.True(changedAgain > generated);
    }
    [Fact]
    public void RepeatedDirtiesAndNewSaveDuringExtractionDoNotLoseChanges()
    {
        using var state = new MapCacheState(Database);
        var first = state.MarkDirty("1_2_3"); var frozen = state.Freeze();
        var second = state.MarkDirty("1_2_3");
        var task = state.Request("0_0", "changes", columns: new() { [19] = first });
        state.ConfirmSaved("1_2_3", frozen["1_2_3"]);
        Assert.Equal(second, state.Freeze()["1_2_3"]);
        var next = state.Request("0_0", "changes", columns: new() { [19] = second });
        state.CompleteColumn("0_0", 19, first);
        Assert.Equal(second, state.Pending["0_0"].Columns[19]);
        Assert.False(state.Complete("0_0", task.Revision));
        Assert.True(state.Complete("0_0", next.Revision));
    }
    [Fact]
    public void SameColorRequestCoalescesAndNewColorRetainsUnfinishedExtraction()
    {
        using var state = new MapCacheState(Database);
        var first = state.Request("0_0", "season", colorOnly: true, colorVersion: "a");
        var duplicate = state.Request("0_0", "season", colorOnly: true, colorVersion: "a");
        Assert.Equal(first.Revision, duplicate.Revision);
        var dirty = state.Request("0_0", "changes", columns: new() { [12] = 100 }, colorVersion: "a");
        var season = state.Request("0_0", "season", colorOnly: true, colorVersion: "b");
        Assert.False(season.ColorOnly); Assert.Equal(100, season.Columns[12]); Assert.Equal("changes", season.Reason);
        Assert.False(state.Complete("0_0", dirty.Revision));
    }
    [Fact]
    public void SavedChangesPromoteRebuildWithoutLosingItsCompletionMembership()
    {
        using var state = new MapCacheState(Database);
        var rebuild = state.Request("0_0", "rebuild", true);
        var changed = state.Request("0_0", "changes", columns: new() { [12] = 77 });
        Assert.Equal("changes", changed.Reason); Assert.True(changed.Rebuild);
        state.CompleteColumn("0_0", 12, rebuild.Columns[12], rebuild.Revision);
        Assert.Equal(77, state.Find("0_0")!.Columns[12]);
        Assert.True(state.HasRebuildWork);
    }
    [Fact]
    public void AdditionalObjectSlicesInvalidateTheRunningColumnAcknowledgement()
    {
        using var state = new MapCacheState(Database);
        var first = state.Request("0_0", "changes", columns: new() { [12] = 77 }, objectYs: new() { [12] = [1] });
        var next = state.Request("0_0", "changes", columns: new() { [12] = 77 }, objectYs: new() { [12] = [2] });
        Assert.NotEqual(first.Revision, next.Revision);
        state.CompleteColumn("0_0", 12, 77, first.Revision);
        Assert.Equal(new[] { 1, 2 }, state.Find("0_0")!.ObjectYs[12].Order());
        state.CompleteColumn("0_0", 12, 77, next.Revision);
        Assert.Empty(state.Find("0_0")!.Columns);
    }
    [Fact]
    public void ParentRequestsArePersistentAndDoNotPolluteWorldRegionIndex()
    {
        using (var state = new MapCacheState(Database))
        {
            var first = state.RequestParent("p_0_1_0", "changes", "one");
            Assert.Equal(first.Revision, state.RequestParent("p_0_1_0", "changes", "one").Revision);
            var next = state.RequestParent("p_0_1_0", "changes", "two");
            Assert.False(state.Complete("p_0_1_0", first.Revision)); Assert.Empty(state.Regions);
            state.Close(true);
        }
        using var restored = new MapCacheState(Database);
        Assert.Equal("two", restored.Pending.Single().Value.ColorVersion); Assert.Empty(restored.Regions);
    }
    [Fact]
    public void AbnormalCloseAndCorruptIndexRequireRecoveryAndRetainImages()
    {
        var image = Path.Combine(root, "existing.png"); File.WriteAllText(image, "existing image");
        using (var state = new MapCacheState(Database)) state.MarkDirty("1_2_3");
        using (var state = new MapCacheState(Database)) { Assert.True(state.RecoveryRequired); Assert.Single(state.Freeze()); state.Close(true); }
        File.WriteAllText(Database, "damaged state");
        using var recovered = new MapCacheState(Database);
        Assert.True(recovered.RecoveryRequired); Assert.NotNull(recovered.RecoveryNotice);
        Assert.Equal("existing image", File.ReadAllText(image));
    }
    [Fact]
    public void ColumnCacheRoundtripPreservesStableCodesAndOnlyMergesOneColumn()
    {
        var region = new SurfaceRegion { Generation = 42 };
        var index = 16 * 3 + 7; var pixel = (7 * 32 + 11) * 512 + 3 * 32 + 9;
        region.Valid[pixel] = true; region.Heights[pixel] = 103; region.Water[pixel] = true;
        region.Codes[pixel] = "game:rock-granite"; region.SepiaKeys[pixel] = "stone";
        region.EntityKeys[pixel] = "roof/material/straw"; region.Columns[index] = true; region.Fingerprints[index] = "snapshot";
        var path = SurfaceRegion.PathFor(root, 1, 2); region.Save(path);
        var restored = Assert.IsType<SurfaceRegion>(SurfaceRegion.Load(path));
        Assert.Equal(region.Codes, restored.Codes); Assert.Equal(region.Heights, restored.Heights);
        var column = restored.Column(index); column.Codes[11 * 32 + 9] = "game:firewood";
        var part = Path.Combine(root, "part.br"); column.Save(part);
        restored.MergeColumn(index, Assert.IsType<SurfaceRegion>(SurfaceRegion.Load(part)));
        Assert.Equal("game:firewood", restored.Codes[pixel]); Assert.Equal("game:air", restored.Codes[pixel + 32]);
        Assert.Equal("roof/material/straw", restored.EntityKeys[pixel]); Assert.True(restored.Water[pixel]);
        var bytes = File.ReadAllBytes(path); bytes[0] ^= 1; File.WriteAllBytes(path, bytes);
        Assert.Null(SurfaceRegion.Load(path)); Assert.NotNull(SurfaceRegion.Load(part));
    }
    public void Dispose() => Directory.Delete(root, true);
}
