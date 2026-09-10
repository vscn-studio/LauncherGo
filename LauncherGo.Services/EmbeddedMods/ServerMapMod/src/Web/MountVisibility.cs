namespace ServerMap.Web;

public static class MountVisibility
{
    public static (double MinX, double MinZ, double MaxX, double MaxZ) Bounds(MountSnapshotStore.Mount mount, MountSnapshotStore.Image image)
    {
        // The fixed-camera atlas shares one projected rectangle for ALL headings.
        // Check the whole atlas footprint, not only the current frame, before exposing the image.
        var scale = MountSnapshotStore.DisplayScale(image.Footprint);
        var x = mount.X + image.CenterX * scale;
        var z = mount.Z + image.CenterZ * scale;
        var radius = image.WorldSize / 2 * scale;
        return (x - radius, z - radius, x + radius, z + radius);
    }

    public static bool Visible(MountSnapshotStore.Mount mount, MountSnapshotStore.Image image, bool playersEnabled, string? uid, bool admin, IReadOnlyList<MapNotebookStore.Region> regions)
    {
        if (!playersEnabled || uid == null || !double.IsFinite(mount.X + mount.Y + mount.Z + mount.Yaw)) return false;
        if (!mount.Riders.Any(r => (admin || r.Uid == uid) && (admin || MapVisibility.Visible(regions, r.X, r.Z)))) return false;
        var b = Bounds(mount, image);
        return admin || MapVisibility.Visible(regions, mount.X, mount.Z) && !regions.Any(r => MapVisibility.Intersects(r, b.MinX, b.MinZ, b.MaxX, b.MaxZ));
    }
}
