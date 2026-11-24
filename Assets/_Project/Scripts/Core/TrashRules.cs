using System.Collections.Generic;
using System.Linq;

namespace Safehouse.Core
{
    /// <summary>
    /// The bin. An item dropped in it is destroyed rather than dropped on the ground, and that is the
    /// whole rule: what leaves a grid is simply gone, and what leaves an expedition floor is gone
    /// without a word in the trip's <c>discarded</c> list.
    ///
    /// That last part is the point of having a separate rule. <see cref="Expedition.Discarded"/> is what
    /// the "left behind, no room" line and the carry-limit note read: it records what the player walked
    /// away from. A destroyed item was not left anywhere, so writing it there would make the trip say
    /// the party abandoned something they deliberately threw out. The log gets one plain line instead.
    /// </summary>
    public static class TrashRules
    {
        /// <summary>
        /// The grid without that one instance. The same grid comes back when it did not hold the item,
        /// so a caller can tell "there was nothing to destroy" from "it is gone" by reference.
        /// </summary>
        public static StashGrid Without(StashGrid grid, string instanceId)
        {
            if (grid == null || instanceId == null)
            {
                return grid;
            }

            var kept = grid.Stash.Where(item => item.InstanceId != instanceId).ToList();
            return kept.Count == grid.Stash.Count ? grid : grid.With(kept);
        }

        /// <summary>
        /// One character's floor without a single find of that type: the last matching entry goes, so
        /// two identical finds on the floor leave one behind and the other characters' finds are
        /// untouched. Null when they had nothing of the kind down there.
        /// </summary>
        public static Dictionary<string, List<string>> WithoutFind(
            IReadOnlyDictionary<string, List<string>> loot, string profileId, string itemId)
        {
            if (loot == null || profileId == null || itemId == null
                || !loot.TryGetValue(profileId, out var found))
            {
                return null;
            }

            var index = found.LastIndexOf(itemId);
            if (index < 0)
            {
                return null;
            }

            var copy = loot.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
            copy[profileId].RemoveAt(index);
            if (copy[profileId].Count == 0)
            {
                copy.Remove(profileId); // an empty list would still draw an empty floor row
            }

            return copy;
        }
    }
}
