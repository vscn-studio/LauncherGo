using System.Text.Json;
using ServerMap.Util;

namespace ServerMap.Web;

public sealed class PlayerTrackStore
{
    public sealed record MountPoint(string Name, double X, double Y, double Z, float Yaw)
    {
        public string? ImageKey { get; init; }
        public double WorldSize { get; init; }
        public double CenterX { get; init; }
        public double CenterZ { get; init; }
        public double DisplayScale { get; init; } = 1;
    }
    public sealed record Sample(DateTimeOffset Time, double X, double Y, double Z, float Yaw, MountPoint? Mount);
    public sealed record Track(string Id, string PlayerUid, string PlayerName, string AdminUid, DateTimeOffset Started, DateTimeOffset Deadline)
    {
        public DateTimeOffset? Ended { get; set; }
        public string? EndReason { get; set; }
        public List<Sample> Samples { get; init; } = [];
    }
    private readonly string root;
    private readonly object gate = new();
    private readonly Dictionary<string, Track> tracks = new();
    public PlayerTrackStore(string root)
    {
        this.root = root;
        Directory.CreateDirectory(root);
        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
        {
            try
            {
                var track = JsonSerializer.Deserialize<Track>(File.ReadAllText(path));
                if (track == null || !ValidId(track.Id)) continue;
                tracks[track.Id] = track;
                if (track.Ended == null) { track.Ended = track.Samples.LastOrDefault()?.Time ?? track.Started; track.EndReason = "server-restarted"; Save(track); }
            }
            catch (JsonException) { }
        }
    }
    private static bool ValidId(string id) => id.Length == 32 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private void Save(Track track) => AtomicFile.Replace(Path.Combine(root, track.Id + ".json"), tmp => File.WriteAllText(tmp, JsonSerializer.Serialize(track)));
    public void SaveMountImage(string key, byte[] png)
    {
        if (!ValidImageKey(key)) throw new ArgumentException("Invalid image key");
        lock (gate) { var path = Path.Combine(root, key + ".png"); if (!File.Exists(path)) AtomicFile.Replace(path, tmp => File.WriteAllBytes(tmp, png)); }
    }
    private static bool ValidImageKey(string? key) => key is { Length: 64 } && key.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public string? MountImage(string trackId, string? key)
    {
        lock (gate) return ValidImageKey(key) && tracks.TryGetValue(trackId, out var track) && track.Samples.Any(s => s.Mount?.ImageKey == key) ? Path.Combine(root, key + ".png") : null;
    }
    private static Track Copy(Track track) => track with { Samples = track.Samples.ToList() };
    public Track[] Active { get { lock (gate) return tracks.Values.Where(t => t.Ended == null).Select(t => t with { Samples = [] }).ToArray(); } }
    public Track[] List(string? search) { lock (gate) return tracks.Values.Where(t => string.IsNullOrWhiteSpace(search) || t.PlayerName.Contains(search, StringComparison.OrdinalIgnoreCase) || t.Started.ToString("O").Contains(search, StringComparison.OrdinalIgnoreCase)).OrderByDescending(t => t.Started).Select(t => t with { Samples = [] }).ToArray(); }
    public Track? Get(string id, DateTimeOffset? after = null) { lock (gate) return tracks.TryGetValue(id, out var track) ? track with { Samples = track.Samples.Where(s => after == null || s.Time > after).ToList() } : null; }
    public Track Start(string uid, string name, string admin, int seconds)
    {
        if (seconds is < 1 or > 86400) throw new ArgumentException("Tracking duration must be 1–86400 seconds");
        lock (gate)
        {
            if (tracks.Values.Count(t => t.Ended == null) >= 16 || tracks.Values.Any(t => t.Ended == null && t.PlayerUid == uid)) throw new InvalidOperationException("Player is already tracked or tracking limit reached");
            var now = DateTimeOffset.UtcNow; var track = new Track(Guid.NewGuid().ToString("N"), uid, name, admin, now, now.AddSeconds(seconds));
            Save(track); tracks[track.Id] = track; return Copy(track);
        }
    }
    public bool Remove(string id)
    {
        lock (gate)
        {
            if (!ValidId(id) || !tracks.ContainsKey(id)) return false;
            // Remove the persisted record first; failed IO must not report successful deletion.
            File.Delete(Path.Combine(root, id + ".json"));
            return tracks.Remove(id);
        }
    }
    public void Stop(string id, string reason)
    {
        lock (gate) if (tracks.TryGetValue(id, out var track) && track.Ended == null)
        { track.Ended = DateTimeOffset.UtcNow; track.EndReason = reason; Save(track); }
    }
    public void Append(string id, Sample sample)
    {
        lock (gate)
        {
            if (!tracks.TryGetValue(id, out var track) || track.Ended != null) return;
            if (sample.Time > track.Deadline || track.Samples.Count >= 86401) { Stop(id, "duration"); return; }
            if (!double.IsFinite(sample.X + sample.Y + sample.Z + sample.Yaw)) return;
            track.Samples.Add(sample);
            if (track.Samples.Count % 10 == 0) Save(track);
        }
    }
}
