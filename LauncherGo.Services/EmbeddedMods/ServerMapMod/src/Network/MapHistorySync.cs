using System.Threading.Channels;

namespace ServerMap.Network;

/// <summary>Explicitly negotiated, client-trusted history; native delivery reports keep their stricter protocol.</summary>
public static class MapHistoryProtocol
{
    public const int Version = 1;
    public const int MaxChunks = 2_000_000;
    public const int RestartIntervalMs = 10_000;
    public static bool ValidWorld(string? world) => !string.IsNullOrWhiteSpace(world) && world.Length <= 256;
}

public sealed record MapHistoryBatch(string Session, string World, string Snapshot, long Sequence, long[] Cells, bool Complete);
public sealed record MapHistoryReceipt(string Session, string Snapshot, long Sequence, bool Complete);

/// <summary>Read-only disk work uses a bounded producer; only the game thread sends packets and handles receipts.</summary>
public sealed class MapHistoryUpload : IDisposable
{
    private readonly Channel<long[]> pages = Channel.CreateBounded<long[]>(new BoundedChannelOptions(2)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource stop = new();
    private readonly string session, world;
    private Exception? failure;
    private MapHistoryBatch? inFlight;
    private long sequence = 1, nextBatchAt, retryAt;
    private int disposed;
    public string Snapshot { get; } = Guid.NewGuid().ToString("N");
    public bool Complete { get; private set; }
    public long UploadedChunks { get; private set; }
    public Exception? Failure => Volatile.Read(ref failure);
    public Task Reading { get; }

    public MapHistoryUpload(string session, string world, Func<CancellationToken, IEnumerable<long[]>> read)
    {
        this.session = session; this.world = world;
        var token = stop.Token;
        Reading = Task.Run(async () =>
        {
            try
            {
                long count = 0;
                foreach (var page in read(token))
                {
                    token.ThrowIfCancellationRequested();
                    if (page.Length is <= 0 or > MapExplorationProtocol.MaxBatch) throw new InvalidDataException("Invalid map history page.");
                    count += page.Length;
                    if (count > MapHistoryProtocol.MaxChunks) throw new InvalidDataException("Map history exceeds the safe synchronization limit; no partial snapshot will be applied.");
                    await pages.Writer.WriteAsync(page.ToArray(), token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                pages.Writer.TryComplete();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { pages.Writer.TryComplete(); }
            catch (Exception ex) { Volatile.Write(ref failure, ex); pages.Writer.TryComplete(); }
        });
    }

    public MapHistoryBatch? Next(long now)
    {
        if (stop.IsCancellationRequested || Complete || Failure != null) return null;
        if (inFlight != null)
        {
            if (now < retryAt) return null;
            retryAt = now + MapExplorationProtocol.RetryIntervalMs;
            nextBatchAt = now + MapExplorationProtocol.BatchIntervalMs;
            return inFlight with { Cells = inFlight.Cells.ToArray() };
        }
        if (now < nextBatchAt) return null;
        if (pages.Reader.TryRead(out var cells)) inFlight = new(session, world, Snapshot, sequence, cells, false);
        else if (Reading.IsCompletedSuccessfully && pages.Reader.Completion.IsCompletedSuccessfully && Failure == null)
            inFlight = new(session, world, Snapshot, sequence, [], true);
        else return null;
        retryAt = now + MapExplorationProtocol.RetryIntervalMs;
        nextBatchAt = now + MapExplorationProtocol.BatchIntervalMs;
        return inFlight with { Cells = inFlight.Cells.ToArray() };
    }

    public void Acknowledge(MapHistoryReceipt receipt)
    {
        if (stop.IsCancellationRequested || inFlight == null || receipt.Session != session || receipt.Snapshot != Snapshot
            || receipt.Sequence != sequence || receipt.Complete != inFlight.Complete) return;
        UploadedChunks += inFlight.Cells.Length;
        Complete = inFlight.Complete; inFlight = null; sequence++;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        // The reader owns any open SQLite handle and closes it on cancellation, without blocking the game thread.
        _ = Reading.ContinueWith(_ => stop.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}

/// <summary>Intermediate receipts only acknowledge buffering. A complete snapshot becomes visible atomically after persistence.</summary>
public sealed class MapHistorySession(string session, string world)
{
    private readonly HashSet<long> cells = new(), nativeDuringSync = new();
    private string snapshot = "";
    private long lastSequence, nextReportAt, nextRestartAt;
    private long[] lastCells = [];
    private bool lastComplete, complete, overflow;

    public void ObserveNative(IEnumerable<long> generated)
    {
        if (complete || overflow) return;
        foreach (var cell in generated)
        {
            nativeDuringSync.Add(cell);
            if (nativeDuringSync.Count > MapHistoryProtocol.MaxChunks) { overflow = true; nativeDuringSync.Clear(); return; }
        }
    }

    public MapHistoryReceipt? Receive(MapHistoryBatch batch, int dimension, long now, int chunksX, int chunksZ, Action<long[]> persist)
    {
        if (overflow || batch.Session != session || batch.World != world || !MapHistoryProtocol.ValidWorld(world)
            || !MapExplorationProtocol.ValidSession(batch.Snapshot) || batch.Sequence <= 0 || dimension != 0
            || batch.Cells == null || batch.Cells.Length > MapExplorationProtocol.MaxBatch
            || (batch.Complete ? batch.Cells.Length != 0 : batch.Cells.Length == 0) || now < nextReportAt
            || batch.Cells.Any(cell => !MapExplorationProtocol.InWorld(cell, chunksX, chunksZ))) return null;
        nextReportAt = now + MapExplorationProtocol.BatchIntervalMs / 2;
        if (batch.Snapshot != snapshot)
        {
            if (complete || batch.Sequence != 1 || now < nextRestartAt) return null;
            snapshot = batch.Snapshot; lastSequence = 0; lastCells = []; lastComplete = false; cells.Clear();
            nextRestartAt = now + MapHistoryProtocol.RestartIntervalMs;
        }
        if (batch.Sequence == lastSequence)
            return batch.Complete == lastComplete && batch.Cells.AsSpan().SequenceEqual(lastCells)
                ? new(session, snapshot, lastSequence, lastComplete) : null;
        if (complete || batch.Sequence != lastSequence + 1) return null;
        if (batch.Complete)
        {
            var result = new HashSet<long>(cells); result.UnionWith(nativeDuringSync);
            if (result.Count > MapHistoryProtocol.MaxChunks) return null;
            persist(result.ToArray()); // Failure leaves the old exploration and this sequence intact for retry.
            complete = true; cells.Clear(); nativeDuringSync.Clear();
        }
        else
        {
            var added = batch.Cells.Distinct().Where(cell => !cells.Contains(cell)).ToArray();
            if (cells.Count + added.Length > MapHistoryProtocol.MaxChunks) return null;
            cells.UnionWith(added);
        }
        lastSequence = batch.Sequence; lastCells = batch.Cells.ToArray(); lastComplete = batch.Complete;
        return new(session, snapshot, lastSequence, lastComplete);
    }
}
