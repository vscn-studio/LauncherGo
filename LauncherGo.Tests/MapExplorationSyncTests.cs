using ServerMap.Network;
using Xunit;

namespace LauncherGo.Tests;

public sealed class MapExplorationSyncTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private static long Cell(int x, int z = 0) => MapExplorationProtocol.Cell(x, z);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(42, 78)]
    [InlineData(-1, -17)]
    [InlineData(int.MinValue, int.MaxValue)]
    public void CoordinatesRoundTripWithoutMixingDimensionsOrSigns(int x, int z)
    {
        Assert.Equal((x, z), MapExplorationProtocol.Coordinates(Cell(x, z)));
    }

    [Fact]
    public void WorldBoundsAreHalfOpenAndCheckedBeforeConsultingDeliveryRecords()
    {
        var session = new MapExplorationSession(); var checkedCells = new List<long>(); var saved = new List<long>();
        long[] submitted = [Cell(-1), Cell(0, -1), Cell(10), Cell(0, 20), Cell(int.MaxValue), Cell(9, 19), Cell(0)];
        var ack = session.Receive(session.Id, 1, 0, submitted, 1000, 10, 20, cell => { checkedCells.Add(cell); return true; }, cells => saved.AddRange(cells));
        Assert.Equal(new[] { Cell(9, 19), Cell(0) }, saved);
        Assert.Equal(saved, checkedCells); Assert.Equal(saved, ack!.Accepted);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(1024)]
    public void EveryVerticalChunkMustHaveBeenSent(int height)
    {
        Assert.True(MapExplorationProtocol.CompleteColumn(height, _ => true));
        for (var missing = 0; missing < height; missing++)
            Assert.False(MapExplorationProtocol.CompleteColumn(height, y => y != missing));
        Assert.False(MapExplorationProtocol.CompleteColumn(0, _ => true));
        Assert.False(MapExplorationProtocol.CompleteColumn(1025, _ => true));
    }

    [Fact]
    public void GenerationBeforeHelloIsRetainedWithoutRevealingNeighbouringChunks()
    {
        var queue = new GeneratedMapChunks(); queue.Record(Cell(500, 600));
        Assert.Null(queue.Next(0)); queue.Connect("invalid"); Assert.False(queue.Connected);
        queue.Connect(Token); var batch = queue.Next(1)!;
        Assert.Equal(Token, batch.Session); Assert.Equal(1, batch.Sequence); Assert.Equal(new[] { Cell(500, 600) }, batch.Cells);
    }

    [Fact]
    public void NativeRedrawsAreDeduplicatedAndBatchesAreBounded()
    {
        var queue = new GeneratedMapChunks(); queue.Connect(Token);
        for (var i = 0; i < 600; i++) { Assert.True(queue.Record(Cell(i))); Assert.True(queue.Record(Cell(i))); }
        var batch = queue.Next(0)!; Assert.Equal(MapExplorationProtocol.MaxBatch, batch.Cells.Length);
        queue.Acknowledge(new(Token, batch.Sequence, batch.Cells));
        foreach (var cell in batch.Cells) queue.Record(cell);
        Assert.Equal(600 - MapExplorationProtocol.MaxBatch, queue.Count);
        Assert.Null(queue.Next(MapExplorationProtocol.BatchIntervalMs - 1));
        var next = queue.Next(MapExplorationProtocol.BatchIntervalMs)!;
        Assert.Equal(2, next.Sequence); Assert.DoesNotContain(next.Cells[0], batch.Cells);
    }

    [Fact]
    public void LostAckRetriesTheSameImmutableBatchWithoutAdvancingOrFlooding()
    {
        var queue = new GeneratedMapChunks(); queue.Connect(Token); queue.Record(Cell(1));
        var batch = queue.Next(1000)!; batch.Cells[0] = Cell(99); queue.Record(Cell(2));
        queue.Connect(Token); Assert.Null(queue.Next(2999));
        var retry = queue.Next(3000)!;
        Assert.Equal(1, retry.Sequence); Assert.Equal(new[] { Cell(1) }, retry.Cells);
        queue.Acknowledge(new(Token, retry.Sequence, retry.Cells));
        Assert.Null(queue.Next(3001));
        var next = queue.Next(3500)!; Assert.Equal(2, next.Sequence); Assert.Equal(new[] { Cell(2) }, next.Cells);
    }

    [Fact]
    public void StaleForeignAndUnrequestedAcknowledgementsCannotConsumePendingWork()
    {
        var queue = new GeneratedMapChunks(); queue.Connect(Token); queue.Record(Cell(1)); queue.Next(0);
        queue.Acknowledge(new(new string('a', 32), 1, [Cell(1)]));
        queue.Acknowledge(new(Token, 2, [Cell(1)]));
        queue.Acknowledge(new(Token, 1, [Cell(999)]));
        queue.Acknowledge(new(Token, 1, [Cell(1), Cell(1)]));
        Assert.Equal(1, queue.Count); Assert.Equal(1, queue.Next(2000)!.Sequence);
        queue.Acknowledge(new(Token, 1, [])); Assert.Equal(0, queue.Count);
        // A rejection may be retried after the native game genuinely generates this cell again.
        Assert.True(queue.Record(Cell(1))); Assert.Null(queue.Next(2001)); Assert.Equal(2, queue.Next(2500)!.Sequence);
    }

    [Fact]
    public void ResetNeverLeaksOneWorldsPendingCellsOrAcksIntoAnotherWorld()
    {
        var queue = new GeneratedMapChunks(); queue.Connect(Token); queue.Record(Cell(1)); var old = queue.Next(0)!;
        queue.Reset(); queue.Record(Cell(2)); var nextToken = new string('b', 32); queue.Connect(nextToken);
        queue.Acknowledge(new(old.Session, old.Sequence, old.Cells));
        Assert.Equal(new[] { Cell(2) }, queue.Next(1)!.Cells);
    }

    [Fact]
    public void QueueIsThreadSafeAndBacklogHasAnExplicitLimit()
    {
        var queue = new GeneratedMapChunks();
        Parallel.For(0, 10_000, i => Assert.True(queue.Record(Cell(i % 1000))));
        Assert.Equal(1000, queue.Count);
        for (var i = 1000; i < MapExplorationProtocol.MaxBufferedChunks; i++) Assert.True(queue.Record(Cell(i)));
        Assert.False(queue.Record(Cell(MapExplorationProtocol.MaxBufferedChunks)));
        queue.Connect(Token); Assert.Equal(MapExplorationProtocol.MaxBatch, queue.Next(0)!.Cells.Length);
    }

    [Fact]
    public void OnlyThisPlayersDeliveredCellsAreSavedWithoutFillingGapsOrRectangles()
    {
        var session = new MapExplorationSession(); var delivered = new HashSet<long> { Cell(1, 2), Cell(200, 300) }; long[] saved = [];
        var ack = session.Receive(session.Id, 1, 0, [Cell(1, 2), Cell(1, 3), Cell(200, 300), Cell(1, 2)], 1000, 1000, 1000,
            delivered.Contains, cells => saved = cells.ToArray())!;
        Assert.Equal(new[] { Cell(1, 2), Cell(200, 300) }, saved); Assert.Equal(saved, ack.Accepted);
        var stranger = new MapExplorationSession();
        Assert.Empty(stranger.Receive(stranger.Id, 1, 0, saved, 1000, 1000, 1000, _ => false, _ => { })!.Accepted);
    }

    [Fact]
    public void ReplayedCommittedBatchIsIdempotentAndAckCannotMutateStoredResult()
    {
        var session = new MapExplorationSession(); var writes = 0; long[] submitted = [Cell(1)];
        var first = session.Receive(session.Id, 1, 0, submitted, 1000, 10, 10, _ => true, _ => writes++)!;
        first.Accepted[0] = Cell(9);
        var replay = session.Receive(session.Id, 1, 0, submitted, 3000, 10, 10, _ => throw new Exception("Must not revalidate a committed replay"), _ => writes++);
        Assert.Equal(new[] { Cell(1) }, replay!.Accepted); Assert.Equal(1, writes);
        Assert.Null(session.Receive(session.Id, 1, 0, [Cell(2)], 5000, 10, 10, _ => true, _ => writes++));
        Assert.Equal(1, writes);
    }

    [Fact]
    public void InvalidProtocolDimensionsAndSessionReplaysAreRejectedBeforePersistence()
    {
        var session = new MapExplorationSession(); var calls = 0;
        MapExplorationReceipt? Send(string token, long seq, int dim, long[]? cells) => session.Receive(token, seq, dim, cells, 1000,
            10, 10, _ => { calls++; return true; }, _ => calls++);
        Assert.Null(Send(Token, 1, 0, [Cell(1)])); Assert.Null(Send(session.Id, 0, 0, [Cell(1)]));
        Assert.Null(Send(session.Id, 1, 1, [Cell(1)])); Assert.Null(Send(session.Id, 1, -1, [Cell(1)]));
        Assert.Null(Send(session.Id, 1, 0, null)); Assert.Null(Send(session.Id, 1, 0, []));
        Assert.Null(Send(session.Id, 1, 0, new long[MapExplorationProtocol.MaxBatch + 1]));
        Assert.Null(Send(session.Id, 2, 0, [Cell(1)])); Assert.Equal(0, calls);
    }

    [Fact]
    public void RateLimitStillAllowsNormalBatchesAndRetriesAfterPersistenceFailure()
    {
        var session = new MapExplorationSession(); var saves = 0; long[] cells = [Cell(1)];
        Assert.Throws<IOException>(() => session.Receive(session.Id, 1, 0, cells, 0, 10, 10, _ => true, _ => throw new IOException("Disk full")));
        Assert.Null(session.Receive(session.Id, 1, 0, cells, 1, 10, 10, _ => true, _ => saves++));
        var ack = session.Receive(session.Id, 1, 0, cells, 2000, 10, 10, _ => true, _ => saves++);
        Assert.NotNull(ack); Assert.Equal(1, saves);
        Assert.Null(session.Receive(session.Id, 2, 0, cells, 2001, 10, 10, _ => true, _ => saves++));
        Assert.NotNull(session.Receive(session.Id, 2, 0, cells, 2500, 10, 10, _ => true, _ => saves++)); Assert.Equal(2, saves);
    }

    [Fact]
    public void RecentDeliveryHistoryIsBoundedAndDuplicateSendsDoNotGrowIt()
    {
        var history = new RecentMapChunks(2);
        history.Add(Cell(1)); history.Add(Cell(1)); history.Add(Cell(2));
        Assert.True(history.Contains(Cell(1))); history.Add(Cell(3));
        Assert.False(history.Contains(Cell(1))); Assert.True(history.Contains(Cell(2))); Assert.True(history.Contains(Cell(3)));
        history.Clear(); Assert.False(history.Contains(Cell(2)));
    }
}
