namespace ServerMap.Network;

/// <summary>Version 1 reports main-world, 32x32 native terrain map pieces, never player radii.</summary>
public static class MapExplorationProtocol
{
    public const int Version = 1;
    public const int ChunkSize = 32;
    public const int MaxBatch = 256;
    public const int MaxBufferedChunks = 65_536;
    public const int BatchIntervalMs = 500;
    public const int RetryIntervalMs = 2000;

    public static long Cell(int x, int z) => ((long)(uint)x << 32) | (uint)z;
    public static (int X, int Z) Coordinates(long cell) => ((int)(cell >> 32), (int)cell);
    public static bool InWorld(long cell, int chunksX, int chunksZ)
    {
        var (x, z) = Coordinates(cell);
        return x >= 0 && z >= 0 && x < chunksX && z < chunksZ;
    }
    public static bool ValidSession(string? session) => session is { Length: 32 } && session.All(Uri.IsHexDigit);
    public static bool CompleteColumn(int height, Func<int, bool> sentChunk)
    {
        if (height <= 0 || height > 1024) return false;
        for (var y = 0; y < height; y++) if (!sentChunk(y)) return false;
        return true;
    }
}

public sealed record MapExplorationBatch(string Session, long Sequence, long[] Cells);
public sealed record MapExplorationReceipt(string Session, long Sequence, long[] Accepted);

/// <summary>Bounded recent history; evicted cells can be verified again against the game's live sent set.</summary>
public sealed class RecentMapChunks(int capacity = MapExplorationProtocol.MaxBufferedChunks)
{
    private readonly HashSet<long> cells = new();
    private readonly Queue<long> order = new();
    public bool Contains(long cell) => cells.Contains(cell);
    public void Add(long cell)
    {
        if (!cells.Add(cell)) return;
        order.Enqueue(cell);
        while (order.Count > Math.Max(1, capacity)) cells.Remove(order.Dequeue());
    }
    public void Clear() { cells.Clear(); order.Clear(); }
}

/// <summary>Native map generation runs off-thread. Networking only drains this queue on the game thread.</summary>
public sealed class GeneratedMapChunks
{
    private readonly object gate = new();
    private readonly Queue<long> pending = new();
    private readonly HashSet<long> queued = new();
    private readonly RecentMapChunks confirmed = new();
    private string session = "";
    private long sequence = 1, nextBatchAt, retryAt;
    private long[]? inFlight;

    public bool Connected { get { lock (gate) return session.Length != 0; } }
    public int Count { get { lock (gate) return queued.Count; } }

    public bool Record(long cell)
    {
        lock (gate)
        {
            if (confirmed.Contains(cell) || queued.Contains(cell)) return true;
            if (queued.Count >= MapExplorationProtocol.MaxBufferedChunks) return false;
            queued.Add(cell); pending.Enqueue(cell);
            return true;
        }
    }

    public void Connect(string token)
    {
        if (!MapExplorationProtocol.ValidSession(token)) return;
        lock (gate)
        {
            if (session == token) return; // Repeated ready/hello packets must not reset an in-flight batch.
            if (inFlight != null)
            {
                var rest = pending.ToArray(); pending.Clear();
                foreach (var cell in inFlight) pending.Enqueue(cell);
                foreach (var cell in rest) pending.Enqueue(cell);
            }
            session = token; sequence = 1; nextBatchAt = retryAt = 0; inFlight = null; confirmed.Clear();
        }
    }

    public MapExplorationBatch? Next(long now)
    {
        lock (gate)
        {
            if (session.Length == 0) return null;
            if (inFlight != null)
            {
                if (now < retryAt) return null;
                retryAt = now + MapExplorationProtocol.RetryIntervalMs;
                nextBatchAt = now + MapExplorationProtocol.BatchIntervalMs;
                return new(session, sequence, inFlight.ToArray());
            }
            if (pending.Count == 0 || now < nextBatchAt) return null;
            inFlight = new long[Math.Min(MapExplorationProtocol.MaxBatch, pending.Count)];
            for (var i = 0; i < inFlight.Length; i++) inFlight[i] = pending.Dequeue();
            nextBatchAt = now + MapExplorationProtocol.BatchIntervalMs;
            retryAt = now + MapExplorationProtocol.RetryIntervalMs;
            return new(session, sequence, inFlight.ToArray());
        }
    }

    public void Acknowledge(MapExplorationReceipt receipt)
    {
        lock (gate)
        {
            if (session != receipt.Session || sequence != receipt.Sequence || inFlight == null
                || receipt.Accepted == null || receipt.Accepted.Length > inFlight.Length) return;
            var batch = inFlight.ToHashSet();
            if (receipt.Accepted.Any(cell => !batch.Contains(cell))) return;
            foreach (var cell in inFlight) queued.Remove(cell);
            foreach (var cell in receipt.Accepted) confirmed.Add(cell);
            // Rejected cells are not remembered as confirmed: a later successful native redraw can retry them.
            inFlight = null; sequence++;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            session = ""; sequence = 1; nextBatchAt = retryAt = 0; inFlight = null;
            pending.Clear(); queued.Clear(); confirmed.Clear();
        }
    }
}

/// <summary>Connection-bound, ordered, rate-limited reports. An ACK is issued only after durable persistence.</summary>
public sealed class MapExplorationSession
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    private long lastSequence, nextReportAt;
    private long[] lastCells = [], lastAccepted = [];

    public MapExplorationReceipt? Receive(string token, long sequence, int dimension, long[]? cells, long now,
        int chunksX, int chunksZ, Func<long, bool> wasSent, Action<long[]> persist)
    {
        if (token != Id || sequence <= 0 || dimension != 0 || cells is not { Length: > 0 and <= MapExplorationProtocol.MaxBatch }
            || now < nextReportAt) return null;
        nextReportAt = now + MapExplorationProtocol.BatchIntervalMs / 2;
        if (sequence == lastSequence)
            return cells.AsSpan().SequenceEqual(lastCells) ? new(Id, sequence, lastAccepted.ToArray()) : null;
        if (sequence != lastSequence + 1) return null;
        var accepted = cells.Distinct().Where(cell => MapExplorationProtocol.InWorld(cell, chunksX, chunksZ) && wasSent(cell)).ToArray();
        persist(accepted); // If saving fails, keep the sequence uncommitted so the client can retry.
        lastSequence = sequence; lastCells = cells.ToArray(); lastAccepted = accepted;
        return new(Id, sequence, accepted.ToArray());
    }
}
