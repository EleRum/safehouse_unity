using System.Collections.Generic;
using System.Linq;

namespace Safehouse.Core
{
    /// <summary>
    /// An item worn or wielded in one loadout slot. Worn items are not placed in any grid; they keep
    /// the instance id they had, so wearing and un-wearing never mints a second copy.
    ///
    /// Durability is what armour is bought for: a vest at 100 % protects at its full armour class, a vest worn
    /// down to nothing protects not at all. Only the pieces that take hits wear - a helmet on the head, a vest
    /// on the torso - and getting hit on a covered part is what wears them.
    /// </summary>
    public sealed class EquippedItem
    {
        public const int MinDurability = 0;
        public const int MaxDurability = 100;

        private EquippedItem(string slot, string instanceId, string itemId, int durability)
        {
            Slot = slot;
            InstanceId = instanceId;
            ItemId = itemId;
            Durability = durability;
        }

        public string Slot { get; }
        public string InstanceId { get; }
        public string ItemId { get; }
        public int Durability { get; }

        /// <summary>Durability as a fraction, 0 to 1 - what the armour class is scaled by.</summary>
        public double Condition => Durability / (double)MaxDurability;

        public static EquippedItem Create(string slot, string instanceId, string itemId,
            int durability = MaxDurability)
        {
            if (!LoadoutSlots.IsSlot(slot))
            {
                throw new ValidationException($"Unknown loadout slot: '{slot}'.");
            }

            return new EquippedItem(slot,
                Validate.Identifier(instanceId, "instance_id", instance: true),
                Validate.Identifier(itemId, "item_id"),
                Validate.Integer(durability, "durability", MinDurability, MaxDurability));
        }

        public EquippedItem WithDurability(int durability) =>
            Create(Slot, InstanceId, ItemId, durability);

        /// <summary>Takes wear, never past zero.</summary>
        public EquippedItem Worn(int points) =>
            WithDurability(System.Math.Max(MinDurability, Durability - System.Math.Max(0, points)));

        public override string ToString() => $"{Slot}: {ItemId} ({Durability}%)";
    }

    /// <summary>What a character has on: at most one item per slot. Immutable, like the grids.</summary>
    public sealed class Loadout
    {
        public Loadout(IEnumerable<EquippedItem> items = null)
        {
            Items = (items ?? Enumerable.Empty<EquippedItem>()).ToList();
            if (Items.Select(item => item.Slot).Distinct().Count() != Items.Count)
            {
                throw new ValidationException("Loadout uses a slot more than once.");
            }
        }

        public static Loadout Empty { get; } = new Loadout();

        public IReadOnlyList<EquippedItem> Items { get; }

        /// <summary>What is in this slot, or null.</summary>
        public EquippedItem Get(string slot) => Items.FirstOrDefault(item => item.Slot == slot);

        public EquippedItem FindByInstance(string instanceId) =>
            Items.FirstOrDefault(item => item.InstanceId == instanceId);

        /// <summary>This loadout with one already-worn item's slot set to the item passed in.</summary>
        public Loadout With(EquippedItem item) =>
            new Loadout(Items.Where(existing => existing.Slot != item.Slot).Append(item));
    }
}
