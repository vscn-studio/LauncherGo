using System.Text.Json;
using ServerMap.Util;

namespace ServerMap.Web;

// Reservations are persisted before moving a player; failed moves release them.
public sealed class DailyTeleportQuota
{
    public sealed record State(DateOnly Date, Dictionary<string, int> Counts);
    private readonly string path;
    private readonly object gate = new();
    private State state = new(DateOnly.FromDateTime(DateTime.Now), new());
    public DailyTeleportQuota(string path)
    {
        this.path = path;
        if (File.Exists(path)) state = JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid teleport quota ledger");
    }
    private void Today() { var date = DateOnly.FromDateTime(DateTime.Now); if (state.Date != date) state = new(date, new()); }
    private void Persist() => AtomicFile.Replace(path, tmp => File.WriteAllText(tmp, JsonSerializer.Serialize(state)));
    public bool Available(string uid, int limit) { lock (gate) { Today(); return limit == 0 || state.Counts.GetValueOrDefault(uid) < limit; } }
    public IDisposable Reserve(IEnumerable<string> players, int limit, out Action commit)
    {
        lock (gate)
        {
            Today(); var ids = players.Distinct().ToArray(); var date = state.Date;
            if (ids.Length == 0) { commit = () => { }; return new Reservation(() => { }); }
            if (limit > 0 && ids.Any(id => state.Counts.GetValueOrDefault(id) >= limit)) throw new InvalidOperationException("teleport_daily_quota");
            var previous = new Dictionary<string, int>(state.Counts);
            foreach (var id in ids) state.Counts[id] = state.Counts.GetValueOrDefault(id) + 1;
            try { Persist(); } catch { state = state with { Counts = previous }; throw; }
            var completed = false;
            commit = () => completed = true;
            return new Reservation(() =>
            {
                lock (gate)
                {
                    if (completed || state.Date != date) return;
                    foreach (var id in ids) state.Counts[id] = Math.Max(0, state.Counts.GetValueOrDefault(id) - 1);
                    Persist();
                }
            });
        }
    }
    private sealed class Reservation(Action rollback) : IDisposable { public void Dispose() => rollback(); }
}
