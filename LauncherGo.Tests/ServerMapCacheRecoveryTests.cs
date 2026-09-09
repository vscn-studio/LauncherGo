using ServerMap.Render;
using ServerMap.Web;
using Xunit;

namespace LauncherGo.Tests;

public sealed class ServerMapCacheRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "LauncherGo-map-recovery-" + Guid.NewGuid().ToString("N"));
    public ServerMapCacheRecoveryTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private TranslocatorIndex OpenIndex() => new(Path.Combine(root, "translocators.json"), _ => { });

    [Fact]
    public void RestartRestoresDistantTranslocatorsBeforeAnyWorldScan()
    {
        var index = OpenIndex();
        var near = new TranslocatorPoint(511288, 50, 512121, 550000, 100, 612000);
        var far = new TranslocatorPoint(-99001, 80, -85000, 511288, 50, 512121);
        index.ReplaceChunk(near.X >> 5, near.Y >> 5, near.Z >> 5, [near]);
        index.ReplaceChunk(far.X >> 5, far.Y >> 5, far.Z >> 5, [far]);
        index.Save();
        Assert.Equal(index.Values.OrderBy(p => p.Id), OpenIndex().Values.OrderBy(p => p.Id));
    }

    [Fact]
    public void PartialScanDoesNotErasePreviouslyKnownChunks()
    {
        var index = OpenIndex();
        var a = new TranslocatorPoint(1, 50, 1, 1000, 70, 1000);
        var b = new TranslocatorPoint(320, 50, 320, 2000, 70, 2000);
        index.ReplaceChunk(0, 1, 0, [a]); index.ReplaceChunk(10, 1, 10, [b]); index.Save();
        index = OpenIndex();
        Assert.False(index.ReplaceChunk(0, 1, 0, [a]));
        index.Save();
        Assert.Contains(b, OpenIndex().Values);
        // Confirmed removal in a successfully read chunk must still propagate.
        Assert.True(index.ReplaceChunk(10, 1, 10, [])); index.Save();
        Assert.Equal(new[] { a }, OpenIndex().Values);
    }

    [Fact]
    public void ChangedLinkReplacesOnlyItsOwnSavedChunk()
    {
        var index = OpenIndex();
        var a = new TranslocatorPoint(-1, 50, -1, 1000, 70, 1000);
        index.ReplaceChunk(-1, 1, -1, [a]);
        index.ReplaceChunk(-1, 1, -1, [a with { TargetX = 2000 }]); index.Save();
        Assert.Equal(2000, Assert.Single(OpenIndex().Values).TargetX);
    }

    [Fact]
    public void LegacyTileAndNewPaletteBothRequireRedraw()
    {
        var tile = Path.Combine(root, "0_0.png"); File.WriteAllBytes(tile, [1]);
        Assert.False(TileColorStamp.IsCurrent(tile, "palette1"));
        TileColorStamp.Complete(tile, "palette1");
        Assert.True(TileColorStamp.IsCurrent(tile, "palette1"));
        Assert.False(TileColorStamp.IsCurrent(tile, "palette2"));
        Assert.False(TileColorStamp.IsCurrent(tile, "fallback"));
    }

    [Fact]
    public void InterruptedTileWriteCannotRetainCurrentStamp()
    {
        var tile = Path.Combine(root, "0_0.png"); File.WriteAllBytes(tile, [1]);
        TileColorStamp.Complete(tile, "palette"); TileColorStamp.Invalidate(tile);
        File.WriteAllBytes(tile, [2]);
        Assert.False(TileColorStamp.IsCurrent(tile, "palette"));
        TileColorStamp.Complete(tile, "palette");
        Assert.True(TileColorStamp.IsCurrent(tile, "palette"));
        File.Delete(tile);
        Assert.False(TileColorStamp.IsCurrent(tile, "palette"));
    }
}
