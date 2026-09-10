using Vintagestory.API.Common.Entities;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace ServerMap.Web;

public static class MountedTeleportTransfer
{
    // Synchronous equivalent of Entity.TeleportToDouble's loaded-column callback.
    // The caller loads ALL destination columns first and validates the party in
    // this same game-thread action, so payment cannot race an async teleport.
    public static void Move(ICoreServerAPI api, Entity mount, double x, double y, double z, Action? afterMove = null)
    {
        var supplier=mount.GetInterface<IMountable>() ?? throw new MountedTeleportException("teleport_mount_unsupported");
        var seats=supplier.Seats.Where(s=>s.Passenger!=null).ToArray();
        var moved=new[]{mount}.Concat(seats.Select(s=>s.Passenger!)).Distinct().ToArray();
        var before=moved.Select(e=>(Entity:e, Pos:e.Pos.Copy(), Previous:e.PreviousServerPos.Copy(), Falling:e.PositionBeforeFalling.Clone(), e.IsTeleport, e.Teleporting)).ToArray();
        var delta=new Vec3d(x-mount.Pos.X,y-mount.Pos.Y,z-mount.Pos.Z);
        var targets=seats.Select(s=>(Entity:s.Passenger!, Position:s.SeatPosition.XYZ.AddCopy(delta))).ToArray();
        try
        {
            foreach (var e in moved) e.Teleporting=true;
            Set(mount,x,y,z);
            foreach (var (entity,position) in targets)
            {
                if (entity is EntityPlayer player) player.Onplrteleported(position.X,position.Y,position.Z,null,api);
                else Set(entity,position.X,position.Y,position.Z);
            }
            afterMove?.Invoke();
        }
        catch
        {
            // Best-effort client resynchronization, then exact server-side rollback.
            // Inventory rollback belongs to ExecuteGroup around this call.
            foreach (var old in before)
            {
                try { if(old.Entity is EntityPlayer player)player.Onplrteleported(old.Pos.X,old.Pos.Y,old.Pos.Z,null,api); }
                catch { /* Preserve the original failure; restore state below. */ }
                old.Entity.Pos.SetFrom(old.Pos);old.Entity.PreviousServerPos.SetFrom(old.Previous);
                old.Entity.PositionBeforeFalling.Set(old.Falling);old.Entity.IsTeleport=old.IsTeleport;
            }
            throw;
        }
        finally { foreach(var old in before)old.Entity.Teleporting=old.Teleporting; }
    }
    private static void Set(Entity entity,double x,double y,double z)
    {
        entity.IsTeleport=true;
        entity.Pos.SetPos(x,y,z);entity.Pos.Motion.Set(0,0,0);
        entity.PreviousServerPos.SetPos(-99,-99,-99);
        entity.PositionBeforeFalling.Set(x,y,z);
    }
}
