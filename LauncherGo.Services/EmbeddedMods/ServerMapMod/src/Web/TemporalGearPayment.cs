using Vintagestory.API.Common;

namespace ServerMap.Web;

public static class TemporalGearPayment
{
    public sealed record Charge(ItemSlot[] Slots, int Cost, string ItemCode);
    // One main-thread transaction for the entire vehicle. No participant is
    // charged until ALL inventories pass; a synchronous failure restores all.
    public static bool ExecuteGroup(IReadOnlyList<Charge> charges, Action teleport)
    {
        if (charges.Any(c => c.Cost < 0)) throw new ArgumentOutOfRangeException(nameof(charges));
        var all = charges.SelectMany(c => c.Slots.Distinct()).ToArray();
        if (all.Distinct().Count() != all.Length) throw new InvalidOperationException("Shared inventories in mounted teleport");
        if (charges.Any(c => Count(c.Slots, c.ItemCode) < c.Cost)) return false;
        var before = charges.Where(c=>c.Cost>0).SelectMany(c=>c.Slots.Where(s=>Matches(s,c.ItemCode)))
            .Select(s=>(Slot:s, Stack:s.Itemstack!.Clone())).ToArray();
        try
        {
            foreach (var charge in charges)
                if (!Execute(charge.Slots, charge.Cost, () => { }, charge.ItemCode)) throw new InvalidOperationException("Inventory changed during mounted teleport");
            teleport();
            return true;
        }
        catch
        {
            foreach (var (slot, stack) in before) slot.Itemstack = stack;
            foreach (var (slot, _) in before) slot.MarkDirty();
            throw;
        }
    }
    public const string Code = PlayerTeleportSettings.DefaultItemCode;
    // Numeric item IDs can change with world/mod mappings. Only the code is
    // authoritative; an unrelated item assigned ID 1899 must never be consumed.
    public static bool IsGear(ItemSlot slot) => Matches(slot, Code);
    public static bool Matches(ItemSlot slot, string code) => slot.Itemstack is { StackSize: > 0 } stack && stack.Collectible?.Code?.ToString() == code;
    public static int Count(IEnumerable<ItemSlot> slots, string code = Code) => (int)Math.Min(int.MaxValue, slots.Distinct().Where(slot => Matches(slot, code)).Sum(slot => (long)slot.StackSize));
    public static bool Execute(IEnumerable<ItemSlot> slots, int cost, Action teleport, string code = Code)
    {
        if (cost < 0) throw new ArgumentOutOfRangeException(nameof(cost));
        var available = slots.Distinct().Where(slot => Matches(slot, code)).ToArray();
        if (Count(available, code) < cost) return false;
        var changed = new List<(ItemSlot Slot, ItemStack Before)>();
        try
        {
            var remaining = cost;
            foreach (var slot in available)
            {
                if (remaining == 0) break;
                var take = Math.Min(remaining, slot.StackSize);
                changed.Add((slot, slot.Itemstack!.Clone()));
                if (slot.TakeOut(take)?.StackSize != take) throw new InvalidOperationException("Inventory changed during teleport");
                slot.MarkDirty(); remaining -= take;
            }
            if (remaining != 0) throw new InvalidOperationException("Inventory changed during teleport");
            teleport();
            return true;
        }
        catch
        {
            foreach (var (slot, before) in changed) slot.Itemstack = before;
            foreach (var (slot, _) in changed) slot.MarkDirty();
            throw;
        }
    }
}
