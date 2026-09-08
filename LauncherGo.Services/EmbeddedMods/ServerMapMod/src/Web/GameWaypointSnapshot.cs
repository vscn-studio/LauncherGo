namespace ServerMap.Web;

/// <summary>Immutable copies only: HTTP workers never enumerate the game's mutable list.</summary>
public sealed class GameWaypointSnapshot
{
    public sealed record Marker(string Id, string OwnerUid, string Name, string Text, string Icon, string Color, double X, double Y, double Z, bool Pinned);
    private Marker[] markers = [];
    public void Replace(IEnumerable<Marker> values) => Volatile.Write(ref markers, values.ToArray());
    public Marker[] ForOwner(string uid) => Volatile.Read(ref markers).Where(m => m.OwnerUid == uid).ToArray();
}
