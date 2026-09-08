using Microsoft.Data.Sqlite;
using ServerMap.Util;
using ServerMap.World;
using Xunit;

namespace LauncherGo.Tests;

public sealed class ServerMapBackgroundWorkTests
{
    [Fact]
    public async Task ResetDoesNotWaitForBlockedReaderOrPublishItsStaleResult()
    {
        var cache = new ResettableCache<int, string>(4);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var oldRead = Task.Run(() => cache.GetOrAdd(1, _ =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return "old";
        }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(cache.Reset).WaitAsync(TimeSpan.FromSeconds(2));
            var fresh = await Task.Run(() => cache.GetOrAdd(1, _ => "new")).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("new", fresh);
        }
        finally { release.Set(); }
        Assert.Equal("old", await oldRead);
        Assert.Equal("new", cache.GetOrAdd(1, _ => throw new InvalidOperationException("Stale reader replaced the new generation.")));
    }

    [Fact]
    public void CacheIsBoundedAndSaveResetRefreshesColumns()
    {
        var cache = new ResettableCache<int, int[]>(2);
        Assert.Equal(new[] { 0, 3 }, cache.GetOrAdd(1, _ => [0, 3]));
        Assert.Equal(new[] { 0, 3 }, cache.GetOrAdd(1, _ => [0, 3, 7]));
        cache.Reset();
        Assert.Equal(new[] { 0, 3, 7 }, cache.GetOrAdd(1, _ => [0, 3, 7]));
        cache.GetOrAdd(2, _ => [2]);
        cache.GetOrAdd(3, _ => [3]);
        Assert.Equal(new[] { 9 }, cache.GetOrAdd(1, _ => [9]));
    }

    [Fact]
    public async Task StartupJobsDoNotRunBeforeRunGameAndDuplicatesAreCoalesced()
    {
        var calls = 0;
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new BackgroundMapWork((_, _) => { });
        for (var i = 0; i < 100; i++)
            work.Enqueue("startup", _ => { Interlocked.Increment(ref calls); ran.TrySetResult(); return Task.CompletedTask; });
        Assert.Equal(0, calls);
        Assert.False(ran.Task.IsCompleted);
        work.Start(TimeSpan.Zero);
        work.Start(TimeSpan.Zero);
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await work.StopAsync();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SaveEventEnqueueDoesNotWaitForAnActiveScan()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new BackgroundMapWork((_, _) => { });
        work.Enqueue("scan", async token => { entered.SetResult(); await release.Task.WaitAsync(token); });
        work.Start(TimeSpan.Zero);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(() => work.Enqueue("save", _ => { saved.SetResult(); return Task.CompletedTask; }))
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(saved.Task.IsCompleted);
            release.SetResult();
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await work.StopAsync(); }
    }

    [Fact]
    public async Task ShutdownCancelsScanAndNeverStartsPendingWork()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingRan = false;
        var errors = 0;
        var work = new BackgroundMapWork((_, _) => Interlocked.Increment(ref errors));
        work.Enqueue("scan", async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); });
        work.Enqueue("pending", _ => { pendingRan = true; return Task.CompletedTask; });
        work.Start(TimeSpan.Zero);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await work.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(pendingRan);
        Assert.Equal(0, errors);
    }

    [Fact]
    public async Task ShutdownBeforeRunGamePreventsStartup()
    {
        var ran = false;
        var work = new BackgroundMapWork((_, _) => { });
        work.Enqueue("startup", _ => { ran = true; return Task.CompletedTask; });
        await work.StopAsync();
        work.Start(TimeSpan.Zero);
        await work.StopAsync();
        Assert.False(ran);
    }

    [Fact]
    public async Task FailedScanIsReportedWithoutStoppingLaterJobs()
    {
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new BackgroundMapWork((key, _) => failed.SetResult(key));
        work.Enqueue("failed", _ => throw new IOException("Simulated busy world database"));
        work.Enqueue("next", _ => { next.SetResult(); return Task.CompletedTask; });
        work.Start(TimeSpan.Zero);
        try
        {
            await next.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("failed", await failed.Task);
        }
        finally { await work.StopAsync(); }
    }

    [Fact]
    public void MapPagesCoverSignedKeysWithoutDuplicatesOrOverflow()
    {
        using var connection = OpenDatabase();
        long[] expected = [long.MinValue, -10, -1, 0, 1, 10, long.MaxValue];
        foreach (var key in expected) Insert(connection, "mapchunk", key);
        var actual = new List<long>();
        long? after = null;
        for (var i = 0; i < 10; i++)
        {
            var page = SavedWorldQueries.MapPage(connection, after, 2);
            if (page.Length == 0) break;
            Assert.InRange(page.Length, 1, 2);
            actual.AddRange(page);
            after = page[^1];
        }
        Assert.Equal(expected, actual);
        // The page has disposed its reader, so another statement can run immediately.
        Insert(connection, "mapchunk", 20);
    }

    [Fact]
    public void ColumnQueryOnlyReturnsRequestedSparseSlices()
    {
        using var connection = OpenDatabase();
        foreach (var key in new long[] { -100, 0, 1, 2, 3, 1000, long.MaxValue }) Insert(connection, "chunk", key);
        Assert.Equal(new long[] { -100, 2, long.MaxValue }, SavedWorldQueries.ColumnPositions(connection, [-100, 2, 999, long.MaxValue]).Order());
        Assert.Empty(SavedWorldQueries.ColumnPositions(connection, []));
        Insert(connection, "chunk", 999);
        Assert.Equal(new long[] { 2, 999 }, SavedWorldQueries.ColumnPositions(connection, [2, 999]).Order());
    }

    [Fact]
    public void QueriesLeaveWorldDataUnchanged()
    {
        using var connection = OpenDatabase();
        Insert(connection, "mapchunk", 42);
        Insert(connection, "chunk", 42);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA query_only=ON";
        command.ExecuteNonQuery();
        Assert.Equal(new long[] { 42 }, SavedWorldQueries.MapPage(connection, null));
        Assert.Equal(new long[] { 42 }, SavedWorldQueries.ColumnPositions(connection, [42]));
    }

    private static SqliteConnection OpenDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE mapchunk(position INTEGER PRIMARY KEY, data BLOB); CREATE TABLE chunk(position INTEGER PRIMARY KEY, data BLOB);";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void Insert(SqliteConnection connection, string table, long position)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO {table}(position) VALUES (@position)";
        command.Parameters.AddWithValue("@position", position);
        command.ExecuteNonQuery();
    }
}
