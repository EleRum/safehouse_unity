using System;
using System.Collections.Generic;
using System.Linq;

namespace Safehouse.Core
{
    /// <summary>The checks on a profile that need the item catalog. The C# side of Python validate_profile.</summary>
    public static class ProfileRules
    {
        /// <summary>
        /// A part at or below this share of its own maximum is about to be destroyed - one more hit takes
        /// the limb, and for the head or the thorax that is death. Five parts in six of it gone is the line
        /// the raid screen draws for the heartbeat.
        /// </summary>
        public const double CriticalShare = 0.2;

        /// <summary>
        /// The parts one hit from gone, worst first. A part already at zero counts: it is destroyed, and a
        /// character walking on one is in the same trouble as one about to lose it.
        /// </summary>
        public static IReadOnlyList<string> CriticalParts(Profile profile)
        {
            if (profile == null)
            {
                return new string[0];
            }

            return CharacterSheet.BodyPartMaxHealth
                .Where(pair => profile.BodyParts[pair.Key] <= pair.Value * CriticalShare)
                .OrderBy(pair => profile.BodyParts[pair.Key] / (double)Math.Max(1, pair.Value))
                .Select(pair => pair.Key)
                .ToList();
        }
        /// <summary>
        /// Why this character's stash and carried grids cannot be rearranged right now, or null when they can.
        /// Someone away on an expedition or downed is not there to have their things moved (Python require_active);
        /// a dead character is not there at all.
        /// </summary>
        public static string StashEditError(Profile profile)
        {
            switch (profile.Status)
            {
                case CharacterSheet.OnExpedition:
                    return $"{profile.DisplayName} is away on an expedition.";
                case CharacterSheet.Downed:
                    return $"{profile.DisplayName} is downed.";
                case CharacterSheet.Dead:
                    return $"{profile.DisplayName} is dead.";
                default:
                    return null;
            }
        }

        /// <summary>
        /// Why this character's gear cannot be changed right now, or null when it can. Unlike the stash, gear can be
        /// changed while a character is away - it is read fresh when their next zone starts - but not while downed
        /// (Python require_not_downed) or dead.
        /// </summary>
        public static string GearEditError(Profile profile)
        {
            switch (profile.Status)
            {
                case CharacterSheet.Downed:
                    return $"{profile.DisplayName} is downed and cannot change gear.";
                case CharacterSheet.Dead:
                    return $"{profile.DisplayName} is dead and cannot change gear.";
                default:
                    return null;
            }
        }

        /// <summary>
        /// Every item must exist in the catalog, fit inside its grid without overlapping, and every instance id
        /// must be unique across the stash, the rig, the backpack and everything worn - one item, one place.
        /// Throws <see cref="ValidationException"/> naming the first problem.
        /// </summary>
        public static void ValidateAgainstCatalog(Profile profile, IReadOnlyDictionary<string, ItemDefinition> catalog)
        {
            var taken = new HashSet<string>();
            foreach (var worn in profile.Loadout.Items)
            {
                if (!catalog.ContainsKey(worn.ItemId))
                {
                    throw new ValidationException(
                        $"{worn.Slot}: Unknown item type: {worn.ItemId}. Restore its item database entry.");
                }

                if (!taken.Add(worn.InstanceId))
                {
                    throw new ValidationException($"Duplicate instance ID: {worn.InstanceId}.");
                }
            }

            PlacementRules.ValidateGrid(profile.Stash, catalog, taken);
            PlacementRules.ValidateGrid(profile.Rig, catalog, taken);
            PlacementRules.ValidateGrid(profile.Backpack, catalog, taken);
        }
    }
}
