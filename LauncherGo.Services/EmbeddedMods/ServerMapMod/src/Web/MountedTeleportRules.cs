using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ServerMap.Web;

public sealed class MountedTeleportException(string code) : Exception(code);

public static class MountedTeleportRules
{
    public enum Kind { Boat, Elk, Airship }
    public sealed record Surface(double Y, bool Water)
    {
        // The map reports the supporting block's Y; standing height is its top.
        public double BlockY => Math.Ceiling(Y)-1;
    }
    public sealed record Box(double X1, double Y1, double Z1, double X2, double Y2, double Z2)
    {
        public Box At(double x, double y, double z) => new(X1+x, Y1+y, Z1+z, X2+x, Y2+y, Z2+z);
        public bool Intersects(Box b) => X1 < b.X2-1e-5 && X2 > b.X1+1e-5 && Y1 < b.Y2-1e-5 && Y2 > b.Y1+1e-5 && Z1 < b.Z2-1e-5 && Z2 > b.Z1+1e-5;
    }
    public static Kind Classify(Entity entity)
    {
        // Recognize the actual mod class, never an arbitrary entity whose code contains "airship".
        for (var type = entity.GetType(); type != null; type = type.BaseType)
            if (type.FullName == "VSAirshipmod.EntityAirship") return Kind.Airship;
        if (entity is EntityBoat) return Kind.Boat;
        if (entity is EntityAgent && entity.Code?.Domain == "game" && entity.Code.Path.StartsWith("elk-", StringComparison.Ordinal)) return Kind.Elk;
        throw new MountedTeleportException("teleport_mount_unsupported");
    }
    public static bool IsController(EntityPlayer player, IMountable supplier) =>
        ReferenceEquals(supplier.Controller, player) && player.MountedOn is { CanControl: true } seat &&
        ReferenceEquals(seat.MountSupplier, supplier) && ReferenceEquals(seat.Passenger, player) && supplier.Seats.Contains(seat);

    public static Box[] Boxes(Entity entity)
    {
        var boxes = new List<Box>();
        if (entity.CollisionBox is { } single) boxes.Add(From(single));
        // Native multi-box physics stores the current yaw-adjusted boxes privately.
        // Read copies only; never advance physics or rotate the live collision state.
        var multi = entity.GetBehavior<EntityBehaviorPassivePhysicsMultiBox>();
        if (multi != null)
        {
            var field = typeof(EntityBehaviorPassivePhysicsMultiBox).GetField("CollisionBoxes", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(multi) is not Cuboidf[] { Length: > 0 } values) throw new MountedTeleportException("teleport_mount_unsupported");
            boxes.AddRange(values.Select(From));
        }
        if (boxes.Count == 0 || boxes.Count > 128 || boxes.Any(b => !double.IsFinite(b.X1+b.Y1+b.Z1+b.X2+b.Y2+b.Z2) || b.X2 <= b.X1 || b.Y2 <= b.Y1 || b.Z2 <= b.Z1))
            throw new MountedTeleportException("teleport_mount_unsupported");
        var bounds = Bounds(boxes);
        if (bounds.X2-bounds.X1 > 64 || bounds.Z2-bounds.Z1 > 64 || bounds.Y2-bounds.Y1 > 128 || Math.Max(Math.Abs(bounds.X1), Math.Abs(bounds.X2)) > 64 || Math.Max(Math.Abs(bounds.Z1), Math.Abs(bounds.Z2)) > 64)
            throw new MountedTeleportException("teleport_mount_unsupported");
        return boxes.ToArray();
    }
    private static Box From(Cuboidf b) => new(b.X1,b.Y1,b.Z1,b.X2,b.Y2,b.Z2);
    public static Box Bounds(IEnumerable<Box> boxes) => new(boxes.Min(b=>b.X1),boxes.Min(b=>b.Y1),boxes.Min(b=>b.Z1),boxes.Max(b=>b.X2),boxes.Max(b=>b.Y2),boxes.Max(b=>b.Z2));
    public static IEnumerable<(int X, int Z)> Columns(Box box)
    {
        for (var x=(int)Math.Floor(box.X1+1e-5); x <= (int)Math.Floor(box.X2-1e-5); x++)
            for (var z=(int)Math.Floor(box.Z1+1e-5); z <= (int)Math.Floor(box.Z2-1e-5); z++) yield return (x,z);
    }
    public static Surface ReadSurface(IBlockAccessor blocks, int x, int z)
    {
        if (x < 0 || z < 0 || x >= blocks.MapSizeX || z >= blocks.MapSizeZ) throw new MountedTeleportException("teleport_coordinates");
        var top = Math.Min(blocks.MapSizeY-1, blocks.GetRainMapHeightAt(x,z)+2);
        // Skip grass and other non-colliding decorations; never silently pick an unloaded column.
        for (var y=top; y>=Math.Max(1,top-32); y--)
        {
            var p=new BlockPos(x,y,z);
            if (blocks.GetChunkAtBlockPos(p) == null) throw new MountedTeleportException("teleport_surface");
            var solid=blocks.GetBlock(p,BlockLayersAccess.Solid);
            var fluid=blocks.GetBlock(p,BlockLayersAccess.Fluid);
            if (solid.BlockMaterial == EnumBlockMaterial.Lava || fluid.BlockMaterial == EnumBlockMaterial.Lava) throw new MountedTeleportException("teleport_surface");
            var boxes=(solid.GetCollisionBoxes(blocks,p)??[]).Concat(fluid.IsLiquid()?[]:fluid.GetCollisionBoxes(blocks,p)??[]).ToArray();
            var solidTop=boxes.Length>0 ? boxes.Max(b=>b.Y2)+y : double.NegativeInfinity;
            if (fluid.IsLiquid() && y+fluid.LiquidLevel/7d > solidTop)
                {
                if (fluid.LiquidCode is not ("water" or "saltwater")) throw new MountedTeleportException("teleport_surface");
                return new(y+fluid.LiquidLevel/7d,true);
            }
            if (double.IsFinite(solidTop)) return new(solidTop,false);
            if (fluid.IsLiquid()) throw new MountedTeleportException("teleport_surface");
        }
        throw new MountedTeleportException("teleport_surface");
    }
    public static double ArrivalY(Kind kind, double riderY, double seatOffsetY, double sourceOriginY, double bottom, IReadOnlyList<Surface> source, IReadOnlyList<Surface> target, bool onGround)
    {
        if (source.Count == 0 || target.Count == 0) throw new MountedTeleportException("teleport_surface");
        if (kind == Kind.Airship)
        {
            if (riderY <= target.Max(s=>s.BlockY)) throw new MountedTeleportException("teleport_airship_height");
            return riderY+5-seatOffsetY;
        }
        if (kind == Kind.Boat)
        {
            if (source.Any(s=>!s.Water) || target.Any(s=>!s.Water) || source.Max(s=>s.Y)-source.Min(s=>s.Y) > .15 || target.Max(s=>s.Y)-target.Min(s=>s.Y) > .15)
                throw new MountedTeleportException("teleport_water_only");
            var draft=sourceOriginY-source[0].Y;
            if (Math.Abs(draft) > 2) throw new MountedTeleportException("teleport_water_only");
            return target[0].Y+draft;
        }
        if (!onGround || source.Any(s=>s.Water) || target.Any(s=>s.Water) || target.Max(s=>s.Y)-target.Min(s=>s.Y) > .5 || Math.Abs(sourceOriginY+bottom-source.Max(s=>s.Y)) > .6)
            throw new MountedTeleportException("teleport_ground_only");
        return target.Max(s=>s.Y)-bottom+.01;
    }
    public static void CheckSpace(IBlockAccessor blocks, IEnumerable<Box> boxes, bool allowWater)
    {
        var work=0;
        foreach (var box in boxes)
        {
            if (box.X1 < 0 || box.Z1 < 0 || box.X2 >= blocks.MapSizeX || box.Z2 >= blocks.MapSizeZ || box.Y1 < 1 || box.Y2 >= blocks.MapSizeY)
                throw new MountedTeleportException("teleport_mount_space");
            foreach (var (x,z) in Columns(box))
                for (var y=(int)Math.Floor(box.Y1); y <= (int)Math.Floor(box.Y2-1e-5); y++)
                {
                    if (++work > 200000) throw new MountedTeleportException("teleport_mount_space");
                    var p=new BlockPos(x,y,z);
                    if (blocks.GetChunkAtBlockPos(p) == null) throw new MountedTeleportException("teleport_surface");
                    var solid=blocks.GetBlock(p,BlockLayersAccess.Solid);
                    var fluid=blocks.GetBlock(p,BlockLayersAccess.Fluid);
                    if (solid.BlockMaterial == EnumBlockMaterial.Lava || fluid.Id != 0 && (!allowWater || fluid.LiquidCode is not ("water" or "saltwater")))
                        throw new MountedTeleportException("teleport_mount_space");
                    if ((solid.GetCollisionBoxes(blocks,p)??[]).Concat(fluid.IsLiquid()?[]:fluid.GetCollisionBoxes(blocks,p)??[]).Any(b=>box.Intersects(From(b).At(x,y,z))))
                        throw new MountedTeleportException("teleport_mount_space");
                }
        }
    }
}
