using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace ServerMap.Render;

public sealed record RegionWork(long Revision, string Reason, bool ExtractAll, bool Verify, bool ColorOnly, Dictionary<int, long> Columns, string ColorVersion)
{
    public bool Rebuild { get; init; }
    public bool ForceImages { get; init; }
    public Dictionary<int, int[]> ObjectYs { get; init; } = new();
}

// All SQLite mutations have one owner. Event handlers only update the in-memory
// journal and enqueue immutable records; shutdown drains this writer, not renders.
public sealed class MapCacheState : IDisposable
{
    private readonly object gate = new();
    private readonly SqliteConnection connection;
    private readonly Channel<(string Table, string Key, string? Value)> writes = Channel.CreateUnbounded<(string, string, string?)>(new() { SingleReader = true });
    private readonly Task writer;
    private readonly Dictionary<string, string> metadata = new();
    private readonly Dictionary<string, RegionWork> work = new();
    private readonly Dictionary<string, long> dirty = new();
    private readonly HashSet<string> regions = new();
    private long revision;
    private bool closed;
    public bool RecoveryRequired { get; }
    public string Epoch { get; }
    public string? Error { get; private set; }
    public string? RecoveryNotice { get; private set; }
    public long Merged { get; private set; }
    public MapCacheState(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SqliteConnection Open()
        {
            var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            try
            {
                c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA quick_check";
                if ((string?)cmd.ExecuteScalar() != "ok") throw new InvalidDataException("Invalid cache state database");
                cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS metadata (key TEXT PRIMARY KEY,value TEXT NOT NULL); CREATE TABLE IF NOT EXISTS regions (key TEXT PRIMARY KEY,value TEXT NOT NULL); CREATE TABLE IF NOT EXISTS work (key TEXT PRIMARY KEY,value TEXT NOT NULL); CREATE TABLE IF NOT EXISTS dirty (key TEXT PRIMARY KEY,value TEXT NOT NULL);"; cmd.ExecuteNonQuery(); return c;
            }
            catch { c.Dispose(); throw; }
        }
        try { connection = Open(); Load(); }
        catch (Exception ex) when (ex is SqliteException { SqliteErrorCode: 11 or 26 } or JsonException or InvalidDataException or FormatException or OverflowException)
        {
            connection?.Dispose(); var suffix = ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            foreach (var extension in new[] { "", "-wal", "-shm" }) if (File.Exists(path + extension)) File.Move(path + extension, path + suffix + extension);
            metadata.Clear(); work.Clear(); dirty.Clear(); regions.Clear(); connection = Open();
            RecoveryNotice = "Cache state recovered: " + ex.Message;
        }
        RecoveryRequired = Get("clean") != "yes";
        revision = Math.Max(long.TryParse(Get("revision"), out var storedRevision) ? storedRevision : 0, Math.Max(work.Values.Select(w => w.Revision).DefaultIfEmpty().Max(), dirty.Values.DefaultIfEmpty().Max()));
        Epoch = Get("epoch") ?? Guid.NewGuid().ToString("N");
        metadata["epoch"] = Epoch;
        using (var cmd = connection.CreateCommand()) { cmd.CommandText = "INSERT INTO metadata(key,value) VALUES('epoch',$epoch) ON CONFLICT(key) DO UPDATE SET value=$epoch"; cmd.Parameters.AddWithValue("$epoch", Epoch); cmd.ExecuteNonQuery(); }
        metadata["clean"] = "no";
        using (var cmd = connection.CreateCommand()) { cmd.CommandText = "INSERT INTO metadata(key,value) VALUES('clean','no') ON CONFLICT(key) DO UPDATE SET value='no'"; cmd.ExecuteNonQuery(); }
        writer = Task.Run(WriteLoop);
    }
    private void Load()
    {
        foreach (var table in new[] { "metadata", "regions", "work", "dirty" })
        {
            using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT key,value FROM " + table; using var r = cmd.ExecuteReader();
            while (r.Read()) { var key = r.GetString(0); var value = r.GetString(1);
                if (table != "metadata")
                {
                    var pieces = key.Split('_'); var parent = table == "work" && pieces[0] == "p";
                    var expected = parent ? 4 : table == "dirty" ? 3 : 2;
                    if (pieces.Length != expected || pieces.Skip(parent ? 1 : 0).Any(p => !int.TryParse(p, out _))) throw new InvalidDataException("Invalid cache coordinate");
                }
                if (table == "metadata") metadata[key] = value;
                else if (table == "regions") regions.Add(key);
                else if (table == "work")
                {
                    var task = JsonSerializer.Deserialize<RegionWork>(value) ?? throw new InvalidDataException("Invalid task");
                    if (task.Revision < 1 || string.IsNullOrEmpty(task.Reason) || task.Columns == null || task.ObjectYs == null || task.Columns.Keys.Any(i => i < 0 || i >= 256)) throw new InvalidDataException("Invalid task state");
                    work[key] = task;
                }
                else dirty[key] = long.Parse(value);
            }
        }
    }
    private void Write(string table, string key, string? value) { if (!closed) writes.Writer.TryWrite((table, key, value)); }
    private long NextRevision() { var value = ++revision; Set("revision", value.ToString()); return value; }
    public string? Get(string key) { lock (gate) return metadata.GetValueOrDefault(key); }
    public void Set(string key, string value) { lock (gate) { if (metadata.GetValueOrDefault(key) != value) { metadata[key] = value; Write("metadata", key, value); } } }
    public string[] Regions { get { lock (gate) return regions.ToArray(); } }
    public void NoteRegion(string key) { lock (gate) if (regions.Add(key)) Write("regions", key, "1"); }
    public void RemoveRegion(string key) { lock (gate) { regions.Remove(key); Write("regions", key, null); } }
    public Dictionary<string, RegionWork> Pending { get { lock (gate) return new(work); } }
    public int PendingCount { get { lock (gate) return work.Count; } }
    public bool HasExtractionWork { get { lock (gate) return work.Values.Any(w => w.Columns.Count > 0); } }
    public bool HasRebuildWork { get { lock (gate) return work.Values.Any(w => w.Rebuild); } }
    // Dirty events identify vertical chunks, while extraction works on X/Z
    // columns. Report these units separately instead of counting every Y slice.
    public int AwaitingSave { get { lock (gate) return dirty.Keys.Select(ColumnOfDirty).Distinct().Count(); } }
    public int AwaitingSaveChunks { get { lock (gate) return dirty.Count; } }
    private static string ColumnOfDirty(string key) { var parts = key.Split('_'); return parts[0] + "_" + parts[2]; }
    public int DeferredGeneration { get { lock (gate) return metadata.Count(p => p.Key.StartsWith("generation:", StringComparison.Ordinal) && p.Value == "pending"); } }
    public void SetGenerationPending(string column, bool pending)
    {
        lock (gate)
        {
            var key = "generation:" + column;
            if (pending) Set(key, "pending");
            else if (metadata.Remove(key)) Write("metadata", key, null);
        }
    }
    public bool NeedsGeneratedColumn(string column)
    {
        lock (gate) return metadata.GetValueOrDefault("generation:" + column) == "pending" || metadata.GetValueOrDefault("column:" + column) != "yes";
    }
    public long MarkDirty(string key) { lock (gate) { var version = NextRevision(); dirty[key] = version; Write("dirty", key, version.ToString()); return version; } }
    public Dictionary<string, long> Freeze() { lock (gate) return new(dirty); }
    public void ConfirmSaved(string key, long version) { lock (gate) if (dirty.GetValueOrDefault(key) == version) { dirty.Remove(key); Write("dirty", key, null); } }
    public RegionWork Request(string key, string reason, bool extractAll = false, bool verify = false, bool colorOnly = false, Dictionary<int, long>? columns = null, string colorVersion = "", Dictionary<int, int[]>? objectYs = null)
    {
        lock (gate)
        {
            NoteRegion(key);
            var rebuild = reason == "rebuild";
            var forceImages = reason == "render";
            var incomingExtraction = extractAll || columns?.Count > 0;
            columns = columns == null ? new() : new(columns);
            objectYs = objectYs == null ? new() : new(objectYs);
            work.TryGetValue(key, out var old);
            if (extractAll && (old == null || !old.ExtractAll || reason == "rebuild" && old.Reason != "rebuild"))
                foreach (var index in Enumerable.Range(0, 256)) { columns[index] = revision + 1; objectYs.Remove(index); }
            if (old != null)
            {
                Merged++; rebuild |= old.Rebuild; forceImages |= old.ForceImages;
                foreach (var pair in old.Columns)
                {
                    if (!columns.ContainsKey(pair.Key)) { columns[pair.Key] = pair.Value; if (old.ObjectYs.TryGetValue(pair.Key, out var previousYs)) objectYs[pair.Key] = previousYs; }
                    else if (objectYs.TryGetValue(pair.Key, out var ys) && old.ObjectYs.TryGetValue(pair.Key, out var previousYs)) objectYs[pair.Key] = ys.Concat(previousYs).Distinct().ToArray();
                    else objectYs.Remove(pair.Key);
                }
                extractAll |= old.ExtractAll; verify = incomingExtraction ? verify && old.Verify : old.Verify; colorOnly &= old.ColorOnly;
                reason = MergeReason(old.Reason, reason);
            }
            if (old != null && old.Reason == reason && old.Rebuild == rebuild && old.ForceImages == forceImages && old.ExtractAll == extractAll && old.Verify == verify && old.ColorOnly == colorOnly && old.ColorVersion == colorVersion
                && old.Columns.Count == columns.Count && old.Columns.All(p => columns.GetValueOrDefault(p.Key) == p.Value)
                && old.ObjectYs.Count == objectYs.Count && old.ObjectYs.All(p => objectYs.TryGetValue(p.Key, out var ys) && p.Value.Order().SequenceEqual(ys.Order()))) return old;
            var next = new RegionWork(NextRevision(), reason, extractAll, verify, colorOnly, columns, colorVersion) { ObjectYs = objectYs, Rebuild = rebuild, ForceImages = forceImages };
            work[key] = next; Write("work", key, JsonSerializer.Serialize(next)); return next;
        }
    }
    public RegionWork RequestParent(string key, string reason, string dependency, bool rebuild = false)
    {
        lock (gate)
        {
            if (work.TryGetValue(key, out var old))
            {
                Merged++;
                reason = MergeReason(old.Reason, reason); rebuild |= old.Rebuild;
                if (old.ColorVersion == dependency && old.Reason == reason && old.Rebuild == rebuild) return old;
            }
            var next = new RegionWork(NextRevision(), reason, false, false, false, new(), dependency) { Rebuild = rebuild || reason == "rebuild" };
            work[key] = next; Write("work", key, JsonSerializer.Serialize(next)); return next;
        }
    }
    private static string MergeReason(string first, string second)
    {
        static int Rank(string reason) => reason == "changes" ? 0 : reason == "rebuild" ? 1 : reason == "season" ? 3 : 2;
        return Rank(first) <= Rank(second) ? first : second;
    }
    public void CompleteColumn(string key, int index, long target, long revision = 0)
    {
        lock (gate)
        {
            if (!work.TryGetValue(key, out var current) || revision != 0 && current.Revision != revision || !current.Columns.TryGetValue(index, out var expected) || expected != target) return;
            var columns = new Dictionary<int, long>(current.Columns); columns.Remove(index);
            var ys = new Dictionary<int, int[]>(current.ObjectYs); ys.Remove(index);
            var next = current with { Columns = columns, ObjectYs = ys }; work[key] = next; Write("work", key, JsonSerializer.Serialize(next));
        }
    }
    public RegionWork? Find(string key) { lock (gate) return work.GetValueOrDefault(key); }
    public bool Complete(string key, long version) { lock (gate) { if (!work.TryGetValue(key, out var current) || current.Revision != version) return false; work.Remove(key); Write("work", key, null); return true; } }
    private async Task WriteLoop()
    {
        try
        {
            while (await writes.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                var batch = new Dictionary<(string Table, string Key), string?>(); var count = 0;
                while (count++ < 512 && writes.Reader.TryRead(out var item)) batch[(item.Table, item.Key)] = item.Value;
                using var tx = connection.BeginTransaction();
                foreach (var (key, value) in batch)
                {
                    var item = (Table: key.Table, Key: key.Key, Value: value);
                    using var cmd = connection.CreateCommand(); cmd.Transaction = tx;
                    cmd.CommandText = item.Value == null ? $"DELETE FROM {item.Table} WHERE key=$key" : $"INSERT INTO {item.Table}(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
                    cmd.Parameters.AddWithValue("$key", item.Key); if (item.Value != null) cmd.Parameters.AddWithValue("$value", item.Value); cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }
        catch (Exception ex) { Error = ex.Message; writes.Writer.TryComplete(ex); }
    }
    public void Close(bool clean)
    {
        lock (gate) { if (closed) return; if (clean && Error == null) Set("clean", "yes"); closed = true; writes.Writer.TryComplete(); }
        writer.GetAwaiter().GetResult(); connection.Dispose();
    }
    public void Dispose() => Close(false);
}
