using ServerMap.Network;
using Xunit;

namespace LauncherGo.Tests;

public sealed class MapHistorySyncTests
{
    private const string Session = "0123456789abcdef0123456789abcdef", World = "world-a", Snapshot = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static long Cell(int x, int z = 0) => MapExplorationProtocol.Cell(x, z);
    private static MapHistoryBatch Page(long sequence, bool complete = false, params long[] cells) => new(Session, World, Snapshot, sequence, cells, complete);

    [Fact]
    public void CompleteHistoryReplacesOnlyAfterAllPagesAndIncludesConcurrentNativeGeneration()
    {
        var server = new MapHistorySession(Session, World); long[] stored = [Cell(99)]; var saves = 0;
        void Save(long[] cells) { stored = cells; saves++; }
        server.ObserveNative([Cell(7)]); // Generated before the first history page reached the server.
        Assert.NotNull(server.Receive(Page(1, false, Cell(1), Cell(50, 70)), 0, 0, 1000, 1000, Save));
        Assert.Equal(new[] { Cell(99) }, stored); Assert.Equal(0, saves);
        Assert.NotNull(server.Receive(Page(2, false, Cell(2), Cell(1)), 0, 500, 1000, 1000, Save));
        server.ObserveNative([Cell(8)]);
        Assert.True(server.Receive(Page(3, true), 0, 1000, 1000, 1000, Save)!.Complete);
        Assert.Equal(new[] { Cell(1), Cell(2), Cell(7), Cell(8), Cell(50, 70) }.Order(), stored.Order());
        Assert.DoesNotContain(Cell(99), stored); Assert.Equal(1, saves);
        Assert.NotNull(server.Receive(Page(3, true), 0, 3000, 1000, 1000, _ => throw new Exception("Replay must not save")));
    }

    [Fact]
    public void CompletedEmptyCacheCanCorrectOldExplorationButKeepsThisConnectionsNativeMap()
    {
        var server = new MapHistorySession(Session, World); long[]? stored = null;
        Assert.NotNull(server.Receive(Page(1, true), 0, 0, 10, 10, value => stored = value)); Assert.Empty(stored!);
        server = new(Session, World); server.ObserveNative([Cell(2)]);
        Assert.NotNull(server.Receive(Page(1, true), 0, 0, 10, 10, value => stored = value)); Assert.Equal(new[] { Cell(2) }, stored);
    }

    [Fact]
    public void InvalidWorldConnectionDimensionsBoundsAndPartialFinalPacketsNeverCommit()
    {
        var server = new MapHistorySession(Session, World); var calls = 0;
        MapHistoryReceipt? Send(MapHistoryBatch batch, int dimension = 0) => server.Receive(batch, dimension, 0, 10, 10, _ => calls++);
        Assert.Null(Send(Page(1) with { World = "world-b" }));
        Assert.Null(Send(Page(1) with { Session = new string('b', 32) }));
        Assert.Null(Send(Page(1) with { Snapshot = "bad" }));
        Assert.Null(Send(Page(1, false, Cell(1)), 1));
        Assert.Null(Send(Page(1, false, Cell(-1)))); Assert.Null(Send(Page(1, false, Cell(10))));
        Assert.Null(Send(Page(1, false, Cell(0, 10)))); Assert.Null(Send(Page(1, false, Cell(1)) with { Cells = null! }));
        Assert.Null(Send(Page(1))); Assert.Null(Send(Page(1, true, Cell(1))));
        Assert.Null(Send(Page(1) with { Cells = new long[MapExplorationProtocol.MaxBatch + 1] }));
        Assert.Equal(0, calls);
        Assert.NotNull(Send(Page(1, true))); Assert.Equal(1, calls);
    }

    [Fact]
    public void InterruptedRestartedOrOutOfOrderSnapshotsCannotReplaceExistingCoverage()
    {
        var server = new MapHistorySession(Session, World); var saved = new List<long>();
        Assert.NotNull(server.Receive(Page(1, false, Cell(1)), 0, 0, 10, 10, saved.AddRange));
        Assert.Null(server.Receive(Page(3, true), 0, 500, 10, 10, saved.AddRange));
        Assert.Null(server.Receive(Page(1, false, Cell(2)), 0, 1000, 10, 10, saved.AddRange));
        var restart = Page(1, false, Cell(3)) with { Snapshot = new string('b', 32) };
        Assert.Null(server.Receive(restart, 0, 2000, 10, 10, saved.AddRange));
        Assert.NotNull(server.Receive(restart, 0, 10_000, 10, 10, saved.AddRange));
        Assert.Empty(saved);
        Assert.Null(server.Receive(Page(2, true), 0, 10_500, 10, 10, saved.AddRange));
        Assert.NotNull(server.Receive(restart with { Sequence = 2, Complete = true, Cells = [] }, 0, 11_000, 10, 10, saved.AddRange));
        Assert.Equal(new[] { Cell(3) }, saved);
        Assert.Null(server.Receive(Page(1, true), 0, 30_000, 10, 10, saved.AddRange));
    }

    [Fact]
    public void SaveFailureWithholdsFinalReceiptAndAllowsIdempotentRetry()
    {
        var server = new MapHistorySession(Session, World); var writes = 0;
        server.Receive(Page(1, false, Cell(3)), 0, 0, 10, 10, _ => writes++);
        Assert.Throws<IOException>(() => server.Receive(Page(2, true), 0, 500, 10, 10, _ => throw new IOException("Disk full")));
        Assert.Equal(0, writes);
        Assert.Null(server.Receive(Page(2, true), 0, 501, 10, 10, _ => writes++));
        Assert.True(server.Receive(Page(2, true), 0, 2500, 10, 10, values => { Assert.Equal(new[] { Cell(3) }, values); writes++; })!.Complete);
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task UploadStreamsMoreThanTheNativeQueueLimitWithoutDroppingDistantChunks()
    {
        const int total = MapExplorationProtocol.MaxBufferedChunks + 1025;
        IEnumerable<long[]> Read(CancellationToken token)
        {
            for (var i = 0; i < total; i += MapExplorationProtocol.MaxBatch)
            { token.ThrowIfCancellationRequested(); yield return Enumerable.Range(i, Math.Min(total - i, MapExplorationProtocol.MaxBatch)).Select(x => Cell(x)).ToArray(); }
        }
        using var upload = new MapHistoryUpload(Session, World, Read);
        var server = new MapHistorySession(Session, World); long[]? saved = null; long now = 0;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!upload.Complete)
        {
            Assert.True(DateTime.UtcNow < deadline, "History upload stalled"); Assert.Null(upload.Failure);
            if (upload.Next(now) is not { } batch) { await Task.Delay(1); continue; }
            var receipt = server.Receive(batch, 0, now, total + 1, 1, cells => saved = cells);
            Assert.NotNull(receipt); upload.Acknowledge(receipt); now += 500;
        }
        Assert.Equal(total, saved!.Length); Assert.Equal(total, upload.UploadedChunks);
        Assert.Contains(Cell(total - 1), saved); Assert.DoesNotContain(Cell(total), saved);
    }

    [Fact]
    public async Task UploadRetriesImmutablePagesAndRejectsForeignOrPrematureFinalAcks()
    {
        using var upload = new MapHistoryUpload(Session, World, _ => new[] { new[] { Cell(1) } });
        await upload.Reading;
        var page = upload.Next(0)!; page.Cells[0] = Cell(9);
        upload.Acknowledge(new(Session, upload.Snapshot, 1, true));
        upload.Acknowledge(new(Session, new string('b', 32), 1, false));
        upload.Acknowledge(new(new string('b', 32), upload.Snapshot, 1, false));
        Assert.False(upload.Complete); Assert.Null(upload.Next(1999));
        var retry = upload.Next(2000)!; Assert.Equal(new[] { Cell(1) }, retry.Cells);
        upload.Acknowledge(new(Session, upload.Snapshot, 1, false));
        Assert.True(upload.Next(2500)!.Complete); Assert.False(upload.Complete);
        upload.Acknowledge(new(Session, upload.Snapshot, 1, true)); Assert.False(upload.Complete);
        upload.Acknowledge(new(Session, upload.Snapshot, 2, true)); Assert.True(upload.Complete);
    }

    [Fact]
    public async Task DiskReadFailureAndCancellationNeverEmitACompleteSnapshot()
    {
        IEnumerable<long[]> Failing(CancellationToken token) { yield return [Cell(1)]; throw new IOException("Unreadable map DB"); }
        using (var upload = new MapHistoryUpload(Session, World, Failing))
        {
            await upload.Reading; Assert.IsType<IOException>(upload.Failure); Assert.Null(upload.Next(10000));
        }
        var exited = false;
        IEnumerable<long[]> Endless(CancellationToken token)
        {
            try { while (true) { token.ThrowIfCancellationRequested(); yield return [Cell(1)]; } }
            finally { exited = true; }
        }
        var cancelled = new MapHistoryUpload(Session, World, Endless);
        cancelled.Dispose(); await cancelled.Reading.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(cancelled.Next(10000)); Assert.False(cancelled.Complete);
        // Cancellation before the first MoveNext is valid too; the important invariant is that the worker has exited.
        Assert.True(exited || cancelled.Reading.IsCompletedSuccessfully);
    }
}
