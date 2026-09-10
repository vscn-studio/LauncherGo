using System.Collections.Concurrent;
using System.Net;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace ServerMap.Web;

public sealed partial class ServerMapWebServer
{
    private sealed record MountedRider(string Uid, string Name, long EntityId, string SeatId, bool Admin, int Multiplier, double OffsetX, double OffsetY, double OffsetZ);
    private sealed record MountedOccupant(long EntityId, double OffsetX, double OffsetY, double OffsetZ, MountedTeleportRules.Box[] Boxes, double? EyeHeight);
    private sealed record MountedState(long EntityId, MountedTeleportRules.Kind Kind, double X, double Y, double Z, float Yaw, float Pitch, float Roll,
        double RiderX, double RiderY, double RiderZ, bool OnGround, MountedTeleportRules.Box[] Boxes, MountedRider[] Riders, MountedOccupant[] Occupants, string Seats, PlayerTeleportSettings Settings);
    private sealed record MountedLanding(double X, double Y, double Z, double RiderY);
    private sealed record MountedQuote(string Id, DateTimeOffset Expires, MountedState State, MountedLanding Landing, int Jumps, string? Reason, long NetworkVersion, MapNotebookStore.Region[] Regions);
    private readonly ConcurrentDictionary<string, MountedQuote> mountedTeleportQuotes = new(StringComparer.Ordinal);

    // All entity/seat/inventory reads in this file are made on the game thread.
    private MountedState MountedSnapshot(HttpListenerContext context, string uid, double x, double z)
    {
        var principal=Principal(context.Request);
        if (principal?.PlayerUid != uid) throw new TeleportError(401,"Login required");
        var config=announcements.Current;
        if (!config.MountedTeleportEnabled) throw new TeleportError(403,"teleport_mount_disabled");
        if (!principal.IsAdmin && !config.PlayerGearTeleportEnabled) throw new TeleportError(403,"teleport_disabled");
        if (!CanView(principal,x,z)) throw new TeleportError(403,"Hidden region");
        var driver=OnlineTeleportPlayer(uid,true);
        var supplier=driver.Entity.MountedOn?.MountSupplier;
        var entity=supplier?.OnEntity;
        if (entity == null || !entity.Alive || entity.Teleporting || entity.Pos.Dimension != 0) throw new TeleportError(409,"teleport_changed");
        if (!ReferenceEquals(entity.GetInterface<IMountable>(),supplier)) throw new TeleportError(409,"teleport_mount_unsupported");
        if (!MountedTeleportRules.IsController(driver.Entity,supplier!)) throw new TeleportError(409,"teleport_driver_only");
        var kind=MountedTeleportRules.Classify(entity);
        var riders=new List<MountedRider>();
        var occupants=new List<MountedOccupant>();
        if (supplier!.Seats.Length>128) throw new TeleportError(409,"teleport_mount_unsupported");
        foreach (var seat in supplier!.Seats)
        {
            if (seat.Passenger is not { } seated) continue;
            if (!seated.Alive || seated.Teleporting || seated.Pos.Dimension!=0) throw new TeleportError(409,"teleport_changed");
            var seatedPos=seat.SeatPosition;
            var occupant=new MountedOccupant(seated.EntityId,seatedPos.X-entity.Pos.X,seatedPos.Y-entity.Pos.Y,seatedPos.Z-entity.Pos.Z,
                MountedTeleportRules.Boxes(seated),seated is EntityPlayer ? seated.LocalEyePos.Y : null);
            if (!double.IsFinite(occupant.OffsetX+occupant.OffsetY+occupant.OffsetZ) || Math.Max(Math.Abs(occupant.OffsetX),Math.Abs(occupant.OffsetZ))>64 || Math.Abs(occupant.OffsetY)>128) throw new TeleportError(409,"teleport_mount_unsupported");
            occupants.Add(occupant);
            if (seated is not EntityPlayer passenger) continue;
            var player=OnlineTeleportPlayer(passenger.PlayerUID,true);
            if (!ReferenceEquals(player.Entity,passenger) || !ReferenceEquals(passenger.MountedOn,seat)) throw new TeleportError(409,"teleport_changed");
            var admin=player.HasPrivilege("root");
            if (!admin && !config.PlayerGearTeleportEnabled) throw new TeleportError(403,"teleport_disabled");
            var position=seat.SeatPosition;
            riders.Add(new(player.PlayerUID,player.PlayerName,passenger.EntityId,seat.SeatId,admin,admin?0:player.PlayerUID==uid?2:1,
                position.X-entity.Pos.X,position.Y-entity.Pos.Y,position.Z-entity.Pos.Z));
        }
        if (riders.Count is < 1 or > 64 || riders.Select(r=>r.Uid).Distinct().Count()!=riders.Count || !riders.Any(r=>r.Uid==uid)) throw new TeleportError(409,"teleport_changed");
        var p=entity.Pos;
        if (!double.IsFinite(p.X+p.Y+p.Z+p.Yaw+p.Pitch+p.Roll+driver.Entity.Pos.Y) || riders.Any(r=>!double.IsFinite(r.OffsetX+r.OffsetY+r.OffsetZ) || Math.Max(Math.Abs(r.OffsetX),Math.Abs(r.OffsetZ))>64 || Math.Abs(r.OffsetY)>128))
            throw new TeleportError(409,"teleport_mount_unsupported");
        return new(entity.EntityId,kind,p.X,p.Y,p.Z,p.Yaw,p.Pitch,p.Roll,driver.Entity.Pos.X,driver.Entity.Pos.Y,driver.Entity.Pos.Z,entity.OnGround,MountedTeleportRules.Boxes(entity),
            riders.OrderBy(r=>r.Uid,StringComparer.Ordinal).ToArray(),occupants.ToArray(),string.Join(";",supplier.Seats.Select(s=>$"{s.SeatId}:{s.Passenger?.EntityId ?? 0}")),(config.PlayerTeleport??new()).Validate());
    }
    private static bool SameMountedState(MountedState a, MountedState b) =>
        a.EntityId==b.EntityId && a.Kind==b.Kind && a.Settings==b.Settings && a.Seats==b.Seats &&
        Math.Abs(a.X-b.X)<.15 && Math.Abs(a.Y-b.Y)<.25 && Math.Abs(a.Z-b.Z)<.15 && Math.Abs(a.RiderY-b.RiderY)<.25 &&
        Math.Abs(a.Yaw-b.Yaw)<.01 && Math.Abs(a.Pitch-b.Pitch)<.02 && Math.Abs(a.Roll-b.Roll)<.02 &&
        a.Riders.Length==b.Riders.Length && a.Riders.Zip(b.Riders).All(pair=>
            pair.First.Uid==pair.Second.Uid && pair.First.EntityId==pair.Second.EntityId && pair.First.SeatId==pair.Second.SeatId && pair.First.Multiplier==pair.Second.Multiplier) &&
        a.Boxes.Length==b.Boxes.Length;

    private static MountedTeleportRules.Box TravelBounds(MountedState state, double x, double z)
    {
        var boxes=state.Boxes.ToList();
        foreach (var r in state.Occupants)
            boxes.AddRange(r.Boxes.Select(b=>b.At(r.OffsetX,r.OffsetY,r.OffsetZ)));
        return MountedTeleportRules.Bounds(boxes).At(x,0,z);
    }
    private MountedLanding MountedDestination(MountedState state, string uid, double x, double z)
    {
        var blocks=api.World.BlockAccessor;
        var bounds=MountedTeleportRules.Bounds(state.Boxes);
        var targetColumns=MountedTeleportRules.Columns(bounds.At(x,0,z)).ToArray();
        var sources=state.Kind==MountedTeleportRules.Kind.Airship ? new[]{new MountedTeleportRules.Surface(0,false)} :
            MountedTeleportRules.Columns(bounds.At(state.X,0,state.Z)).Select(p=>MountedTeleportRules.ReadSurface(blocks,p.X,p.Z)).ToArray();
        var targets=targetColumns.Select(p=>MountedTeleportRules.ReadSurface(blocks,p.X,p.Z)).ToArray();
        var driver=state.Riders.Single(r=>r.Uid==uid);
        var y=MountedTeleportRules.ArrivalY(state.Kind,state.RiderY,driver.OffsetY,state.Y,bounds.Y1,sources,targets,state.OnGround);
        var destinationBounds=TravelBounds(state,x,z);
        // A passenger cannot be carried into an area hidden from that passenger.
        if (state.Riders.Any(r=>!r.Admin) && notebook.Regions.Any(r=>MapVisibility.Intersects(r,destinationBounds.X1,destinationBounds.Z1,destinationBounds.X2,destinationBounds.Z2)))
            throw new TeleportError(403,"Hidden region");
        var worldBoxes=state.Boxes.Select(b=>b.At(x,y,z)).ToList();
        // Check a little below the hull too, so shallow water cannot strand it.
        if (state.Kind==MountedTeleportRules.Kind.Boat)
            worldBoxes.AddRange(state.Boxes.Select(b=>new MountedTeleportRules.Box(b.X1,b.Y1-.25,b.Z1,b.X2,b.Y2,b.Z2).At(x,y,z)));
        foreach (var occupant in state.Occupants)
        {
            if (state.Kind==MountedTeleportRules.Kind.Boat && occupant.EyeHeight is {} eye && y+occupant.OffsetY+eye <= targets.Max(s=>s.Y)+.1) throw new TeleportError(409,"teleport_mount_space");
            worldBoxes.AddRange(occupant.Boxes.Select(b=>b.At(x+occupant.OffsetX,y+occupant.OffsetY,z+occupant.OffsetZ)));
        }
        MountedTeleportRules.CheckSpace(blocks,worldBoxes,state.Kind==MountedTeleportRules.Kind.Boat);
        return new(x,y,z,y+driver.OffsetY);
    }
    private string? MountedEligibility(MountedState state, int jumps)
    {
        if (jumps==0 && state.Riders.Any(r=>r.Multiplier>0)) return "teleport_zero_jumps";
        foreach (var rider in state.Riders)
        {
            var player=OnlineTeleportPlayer(rider.Uid,true);
            var settings=state.Settings.Multiply(rider.Multiplier);
            if (TemporalGearPayment.Count(TeleportSlots(player),settings.ItemCode)<settings.Cost(jumps)) return "teleport_party_gears";
            if (TeleportEffects.Prepare(player.Entity,settings).Error is { } reason)
                return reason=="teleport_health"?"teleport_party_health":"teleport_effects_unavailable";
        }
        return null;
    }
    private object MountedQuoteResponse(MountedQuote quote, string uid)
    {
        var driver=quote.State.Riders.Single(r=>r.Uid==uid);
        var participants=quote.State.Riders.Select(r=>new {
            name=r.Name, driver=r.Uid==uid, multiplier=r.Multiplier, admin=r.Admin,
            cost=quote.State.Settings.Multiply(r.Multiplier).Cost(quote.Jumps),
            settings=quote.State.Settings.Multiply(r.Multiplier)
        }).ToArray();
        return new { quoteId=quote.Id, x=quote.Landing.X, y=quote.Landing.RiderY, z=quote.Landing.Z, jumps=quote.Jumps,
            cost=quote.State.Settings.Multiply(driver.Multiplier).Cost(quote.Jumps), settings=quote.State.Settings.Multiply(driver.Multiplier),
            available=TemporalGearPayment.Count(TeleportSlots(OnlineTeleportPlayer(uid,true)),quote.State.Settings.ItemCode),
            itemCode=quote.State.Settings.ItemCode, admin=driver.Admin, mounted=true, participants, allowed=quote.Reason==null, reason=quote.Reason };
    }
    private object QuoteMountedTeleport(HttpListenerContext context, string uid, double x, double z)
    {
        var state=OnGameThread(()=>MountedSnapshot(context,uid,x,z));
        var landing=AtMountedDestination(state,x,z,()=>MountedDestination(MountedSnapshot(context,uid,x,z),uid,x,z));
        var networkVersion=layerVersions.GetValueOrDefault("translocators");
        var regions=notebook.Regions.ToArray();
        var jumps=state.Riders.All(r=>r.Admin)?0:TeleportCost(new(state.RiderX,state.RiderY,state.RiderZ),x,landing.RiderY,z);
        return OnGameThread(()=>
        {
            var current=MountedSnapshot(context,uid,x,z);
            if (!SameMountedState(state,current) || networkVersion!=layerVersions.GetValueOrDefault("translocators") || !regions.SequenceEqual(notebook.Regions)) throw new TeleportError(409,"teleport_changed");
            var finalLanding=MountedDestination(current,uid,x,z);
            if (Math.Abs(finalLanding.Y-landing.Y)>.25) throw new TeleportError(409,"teleport_changed");
            var quote=new MountedQuote(Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow.AddMinutes(1),current,finalLanding,jumps,MountedEligibility(current,jumps),networkVersion,regions);
            foreach (var pair in mountedTeleportQuotes) if (pair.Value.Expires<DateTimeOffset.UtcNow) mountedTeleportQuotes.TryRemove(pair.Key,out _);
            mountedTeleportQuotes[uid]=quote;
            return MountedQuoteResponse(quote,uid);
        });
    }
    private object ExecuteMountedTeleport(HttpListenerContext context, string uid, MountedQuote quote)
    {
        var checkedState=OnGameThread(()=>MountedSnapshot(context,uid,quote.Landing.X,quote.Landing.Z));
        if (!SameMountedState(quote.State,checkedState)) throw new TeleportError(409,"teleport_changed");
        var jumps=checkedState.Riders.All(r=>r.Admin)?0:TeleportCost(new(checkedState.RiderX,checkedState.RiderY,checkedState.RiderZ),quote.Landing.X,quote.Landing.RiderY,quote.Landing.Z);
        if (jumps!=quote.Jumps) throw new TeleportError(409,"teleport_changed");
        return AtMountedDestination(quote.State,quote.Landing.X,quote.Landing.Z,()=>
        {
            if (quote.Expires<DateTimeOffset.UtcNow) throw new TeleportError(409,"teleport_expired");
            var state=MountedSnapshot(context,uid,quote.Landing.X,quote.Landing.Z);
            if (!SameMountedState(quote.State,state) || !SameMountedState(checkedState,state) || quote.NetworkVersion!=layerVersions.GetValueOrDefault("translocators") || !quote.Regions.SequenceEqual(notebook.Regions)) throw new TeleportError(409,"teleport_changed");
            var landing=MountedDestination(state,uid,quote.Landing.X,quote.Landing.Z);
            if (Math.Abs(landing.Y-quote.Landing.Y)>.25) throw new TeleportError(409,"teleport_changed");
            if (MountedEligibility(state,quote.Jumps) is { } reason) throw new TeleportError(409,reason);
            var people=state.Riders.Select(r=>(Rider:r, Player:OnlineTeleportPlayer(r.Uid,true), Policy:state.Settings.Multiply(r.Multiplier))).ToArray();
            var effects=people.Select(p=>TeleportEffects.PrepareReversible(p.Player.Entity,p.Policy)).ToArray();
            if (effects.Any(e=>e.Error!=null)) throw new TeleportError(409,"teleport_changed");
            var mount=people.Single(p=>p.Rider.Uid==uid).Player.Entity.MountedOn!.MountSupplier.OnEntity;
            try
            {
                if (!TemporalGearPayment.ExecuteGroup(people.Select(p=>new TemporalGearPayment.Charge(TeleportSlots(p.Player).ToArray(),p.Policy.Cost(quote.Jumps),p.Policy.ItemCode)).ToArray(),()=>
                    MountedTeleportTransfer.Move(api,mount,landing.X,landing.Y,landing.Z,()=>{foreach(var effect in effects)effect.Apply();}))) throw new TeleportError(409,"teleport_party_gears");
            }
            catch
            {
                foreach(var effect in effects)effect.Restore();
                throw;
            }
            foreach (var p in people)
            {
                teleportQuotes.TryRemove(p.Rider.Uid,out _); mountedTeleportQuotes.TryRemove(p.Rider.Uid,out _);
                api.Logger.Notification("ServerMap mounted teleport: mount={0}; player={1}; multiplier={2}; consumed={3}; target={4},{5},{6}",mount.EntityId,p.Rider.Uid,p.Rider.Multiplier,p.Policy.Cost(quote.Jumps),landing.X,landing.Y,landing.Z);
            }
            return new { ok=true, x=landing.X, y=landing.RiderY, z=landing.Z, consumed=people.Single(p=>p.Rider.Uid==uid).Policy.Cost(quote.Jumps), itemCode=state.Settings.ItemCode, passengers=people.Length-1 };
        });
    }

    private T AtMountedDestination<T>(MountedState state, double x, double z, Func<T> action)
    {
        var call=new GameThreadCall<T>(()=>{stop.Token.ThrowIfCancellationRequested();return action();});
        try
        {
            OnGameThread(()=>
            {
                // Include rider overhangs and a margin for small idle-pose changes.
                var bounds=TravelBounds(state,x,z);
                if (bounds.X1<1 || bounds.Z1<1 || bounds.X2>=api.World.BlockAccessor.MapSizeX-1 || bounds.Z2>=api.World.BlockAccessor.MapSizeZ-1) throw new TeleportError(400,"teleport_coordinates");
                var chunks=MountedTeleportRules.Columns(new(bounds.X1-1,0,bounds.Z1-1,bounds.X2+1,1,bounds.Z2+1)).Select(p=>(X:p.X/32,Z:p.Z/32)).Distinct().ToArray();
                var remaining=chunks.Length;
                foreach (var chunk in chunks) api.WorldManager.LoadChunkColumnPriority(chunk.X,chunk.Z,new ChunkLoadOptions {
                    OnLoaded=()=>{if(Interlocked.Decrement(ref remaining)==0)api.Event.EnqueueMainThreadTask(call.Run,"servermap-mounted-teleport");}
                });
                return true;
            });
            return call.Task.WaitAsync(TimeSpan.FromSeconds(30),stop.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            if(call.CancelPending())throw new TimeoutException();
            return call.Task.GetAwaiter().GetResult();
        }
    }
}
