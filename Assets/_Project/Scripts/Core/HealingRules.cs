using System;
using System.Collections.Generic;
using System.Linq;

namespace Safehouse.Core
{
    /// <summary>
    /// Using a medkit. The numbers are the same ones the automatic fight uses mid-raid (gear.json's meds
    /// table): a heal pool that closes the worst wounds first, and the three conditions a kit can clear.
    /// Without this a character who comes back hurt stays hurt forever, because nothing outside a fight
    /// ever wrote to their body parts.
    ///
    /// Two places a kit can be: the stash at home (<see cref="Use"/>), and the rig or the backpack out on
    /// a raid (<see cref="UseCarried"/>). The rules are identical; only the grid the kit leaves differs.
    /// </summary>
    public static class HealingRules
    {
        public static bool IsMed(GearData gear, string itemId) =>
            gear != null && itemId != null && gear.Meds.ContainsKey(itemId);

        /// <summary>"3 of 4 parts still hurt" is a nicer thing to read than a list of names.</summary>
        public static IReadOnlyList<string> InjuredParts(Profile profile) =>
            profile.BodyParts
                .Where(pair => pair.Value < CharacterSheet.MaxHealthOf(pair.Key))
                .Select(pair => pair.Key)
                .ToList();

        /// <summary>
        /// Whether this kit would change anything at all. A bandage on a broken leg heals nothing and
        /// clears nothing, and burning one of its uses to say so is worse than refusing it.
        /// </summary>
        public static bool WouldHelp(Profile profile, MedStats med)
        {
            if (profile == null || med == null)
            {
                return false;
            }

            if (med.Heal > 0 && profile.BodyParts.Any(pair => pair.Value < CharacterSheet.MaxHealthOf(pair.Key)))
            {
                return true;
            }

            foreach (var conditions in profile.Conditions.Values)
            {
                foreach (var condition in conditions)
                {
                    if (condition == "light_bleed" && med.StopsLightBleed)
                    {
                        return true;
                    }

                    if (condition == "heavy_bleed" && med.StopsHeavyBleed)
                    {
                        return true;
                    }

                    if (condition == "fracture" && med.TreatsFracture)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Applies one use of a medkit from the stash: heals up to its pool across the worst parts first,
        /// clears the conditions it treats, and consumes one of the item. Whatever it cannot help with is
        /// left alone, so using a bandage on a fracture does not quietly do nothing - the caller says so.
        /// </summary>
        public static (Profile Profile, int Healed, IReadOnlyList<string> Cleared) Use(Profile profile,
            MedStats med, IReadOnlyDictionary<string, ItemDefinition> catalog)
        {
            if (profile == null || med == null)
            {
                throw new ValidationException("There is nothing to use.");
            }

            RefuseIfDead(profile);

            if (profile.Stash.Stash.All(instance =>
                    !string.Equals(instance.ItemId, med.ItemId, StringComparison.Ordinal)))
            {
                throw new ValidationException("That medkit is not in the stash.");
            }

            var treated = Treat(profile, med);
            var stash = InventoryRules.RemoveItems(profile.Stash, catalog, med.ItemId, 1);
            return (profile.With(stash: stash, bodyParts: treated.Parts, conditions: treated.Conditions),
                treated.Healed, treated.Cleared);
        }

        /// <summary>
        /// The same use, for a kit the character is carrying: out on a raid the stash is back home, so the
        /// only medkits there are the ones in the rig and the backpack - and it is the instance that says
        /// which cell to free, since the same type can be in either grid.
        /// </summary>
        public static (Profile Profile, int Healed, IReadOnlyList<string> Cleared) UseCarried(Profile profile,
            string instanceId, MedStats med)
        {
            if (profile == null || med == null || instanceId == null)
            {
                throw new ValidationException("There is nothing to use.");
            }

            RefuseIfDead(profile);

            var inRig = profile.Rig.Stash.FirstOrDefault(item => item.InstanceId == instanceId);
            var inBackpack = profile.Backpack.Stash.FirstOrDefault(item => item.InstanceId == instanceId);
            if (inRig == null && inBackpack == null)
            {
                throw new ValidationException("That medkit is not carried.");
            }

            if (!string.Equals((inRig ?? inBackpack).ItemId, med.ItemId, StringComparison.Ordinal))
            {
                throw new ValidationException("That is not the medkit that was picked.");
            }

            var treated = Treat(profile, med);
            var rig = inRig == null
                ? profile.Rig
                : profile.Rig.With(profile.Rig.Stash.Where(item => item.InstanceId != instanceId));
            var backpack = inBackpack == null
                ? profile.Backpack
                : profile.Backpack.With(profile.Backpack.Stash.Where(item => item.InstanceId != instanceId));
            return (profile.With(rig: rig, backpack: backpack, bodyParts: treated.Parts,
                conditions: treated.Conditions), treated.Healed, treated.Cleared);
        }

        private static void RefuseIfDead(Profile profile)
        {
            if (profile.Status == CharacterSheet.Dead)
            {
                throw new ValidationException($"{profile.DisplayName} is dead. A medkit will not change that.");
            }
        }

        /// <summary>The healing itself: worst wound first, then whatever conditions the kit treats.</summary>
        private static (Dictionary<string, int> Parts, int Healed, IReadOnlyList<string> Cleared,
            Dictionary<string, IReadOnlyList<string>> Conditions) Treat(Profile profile, MedStats med)
        {
            var parts = new Dictionary<string, int>(profile.BodyParts);
            var pool = Math.Max(0, med.Heal);
            var healed = 0;
            while (pool > 0)
            {
                string worst = null;
                var deficit = 0;
                foreach (var part in parts.Keys)
                {
                    var missing = CharacterSheet.MaxHealthOf(part) - parts[part];
                    if (missing > deficit)
                    {
                        deficit = missing;
                        worst = part;
                    }
                }

                if (worst == null)
                {
                    break;
                }

                var give = Math.Min(pool, deficit);
                parts[worst] += give;
                pool -= give;
                healed += give;
            }

            var clearedNames = new List<string>();
            if (med.StopsLightBleed) clearedNames.Add("light_bleed");
            if (med.StopsHeavyBleed) clearedNames.Add("heavy_bleed");
            if (med.TreatsFracture) clearedNames.Add("fracture");

            var cleared = new List<string>();
            var conditions = new Dictionary<string, IReadOnlyList<string>>();
            foreach (var pair in profile.Conditions)
            {
                var kept = pair.Value.Where(condition =>
                {
                    if (!clearedNames.Contains(condition))
                    {
                        return true;
                    }

                    cleared.Add(pair.Key + ": " + condition.Replace('_', ' '));
                    return false;
                }).ToList();

                // A part with no conditions left has no entry at all; an empty list is rejected by
                // Profile.CheckConditions, so keeping the key would make the whole save fail.
                if (kept.Count > 0)
                {
                    conditions[pair.Key] = kept;
                }
            }

            return (parts, healed, cleared, conditions);
        }
    }
}
