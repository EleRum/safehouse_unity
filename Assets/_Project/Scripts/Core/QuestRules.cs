using System;
using System.Collections.Generic;
using System.Linq;

namespace Safehouse.Core
{
    /// <summary>
    /// The quest chain's rules. A quest is not stored with a status: <see cref="StatusOf"/> derives it
    /// from the log plus the quest's own requirements, so editing a chain in quests.json can never leave
    /// a character holding a status the chain no longer agrees with.
    ///
    /// Progress comes from two places only - a finished expedition (extract and loot) and the stash at
    /// claim time (deliver) - which keeps every objective provable from data the game already saves.
    /// </summary>
    public static class QuestRules
    {
        /// <summary>Every quest this one needs has been claimed.</summary>
        public static bool RequiresMet(QuestDefinition definition, QuestLog log) =>
            definition.Requires.All(id => log.Claimed(id));

        public static string StatusOf(QuestDefinition definition, QuestLog log,
            IReadOnlyCollection<string> flags = null, Profile profile = null)
        {
            var entry = log.Get(definition.QuestId);
            if (entry.Claimed)
            {
                return QuestStatus.Claimed;
            }

            if (!RequiresMet(definition, log))
            {
                return QuestStatus.Locked;
            }

            // An event quest is finished when the scene it names has been played, and that is recorded once, on
            // the character, by the scene itself - not by a counter some screen remembered to bump.
            if (definition.Objective.Kind == QuestObjectiveKinds.Event
                && definition.Objective.ItemId != null)
            {
                var seen = flags != null && flags.Contains(EventRules.SeenFlag(definition.Objective.ItemId));
                return seen ? QuestStatus.Done : QuestStatus.Open;
            }

            // A delivery quest is finished when the stash holds what it asked for. Nothing advances a delivery:
            // ProgressFrom returns 0 for it by design, so judging it by the counter left it Open forever and the
            // claim screen could never be reached.
            if (definition.Objective.Kind == QuestObjectiveKinds.Deliver)
            {
                if (profile == null || definition.Objective.ItemId == null)
                {
                    return QuestStatus.Open;
                }

                var have = profile.Stash.Stash.Count(instance =>
                    string.Equals(instance.ItemId, definition.Objective.ItemId, StringComparison.Ordinal));
                return have >= definition.Objective.Count ? QuestStatus.Done : QuestStatus.Open;
            }

            return entry.Progress >= definition.Objective.Count ? QuestStatus.Done : QuestStatus.Open;
        }

        /// <summary>The quests a screen should offer, in data order, with their derived status.</summary>
        public static IReadOnlyList<QuestDefinition> Visible(IEnumerable<QuestDefinition> definitions,
            QuestLog log, IReadOnlyCollection<string> flags = null, Profile profile = null) =>
            definitions.Where(definition => StatusOf(definition, log, flags, profile) != QuestStatus.Locked).ToList();

        /// <summary>
        /// How much one finished trip adds. Only an extraction counts: loot that was lost on the way out
        /// never reached the stash, and a quest that paid for it would be paying for nothing.
        /// </summary>
        public static int ProgressFrom(QuestDefinition definition, Expedition trip, string profileId,
            IReadOnlyDictionary<string, ItemDefinition> catalog)
        {
            if (trip == null || trip.Outcome != "extracted")
            {
                return 0;
            }

            switch (definition.Objective.Kind)
            {
                case QuestObjectiveKinds.Extract:
                    if (definition.Objective.MapId != null
                        && !string.Equals(definition.Objective.MapId, trip.MapId, StringComparison.Ordinal))
                    {
                        return 0;
                    }

                    return 1;

                case QuestObjectiveKinds.Loot:
                    if (trip.Loot == null || !trip.Loot.TryGetValue(profileId, out var brought)
                        || brought == null)
                    {
                        return 0;
                    }

                    return brought.Count(itemId => Matches(definition.Objective, itemId, catalog));

                default:
                    return 0; // deliver is handed over when the reward is claimed
            }
        }

        public static bool Matches(QuestObjective objective, string itemId,
            IReadOnlyDictionary<string, ItemDefinition> catalog)
        {
            if (objective.ItemId != null)
            {
                return string.Equals(objective.ItemId, itemId, StringComparison.Ordinal);
            }

            if (objective.Category != null && catalog.TryGetValue(itemId, out var item))
            {
                return string.Equals(item.Category, objective.Category, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        /// <summary>Adds progress to one quest, clamped, and only while it is open.</summary>
        public static QuestLog Advance(QuestLog log, QuestDefinition definition, int amount)
        {
            if (amount <= 0 || definition.Objective.Kind == QuestObjectiveKinds.Event
                || StatusOf(definition, log) != QuestStatus.Open)
            {
                return log;
            }

            var entry = log.Get(definition.QuestId);
            var next = Math.Min(definition.Objective.Count, entry.Progress + amount);
            return log.With(definition.QuestId, entry.With(next, false));
        }

        /// <summary>Adds one finished trip's progress to every quest in the chain.</summary>
        public static QuestLog Advance(QuestLog log, IEnumerable<QuestDefinition> definitions,
            Expedition trip, string profileId, IReadOnlyDictionary<string, ItemDefinition> catalog)
        {
            var current = log ?? QuestLog.Empty;
            foreach (var definition in definitions ?? Enumerable.Empty<QuestDefinition>())
            {
                current = Advance(current, definition,
                    ProgressFrom(definition, trip, profileId, catalog));
            }

            return current;
        }

        public static string ClaimError(Profile profile, QuestDefinition definition, QuestLog log,
            IReadOnlyDictionary<string, ItemDefinition> catalog)
        {
            var status = StatusOf(definition, log, profile.Flags, profile);
            if (status == QuestStatus.Claimed)
            {
                return "That reward was already taken.";
            }

            if (status != QuestStatus.Done)
            {
                return "That quest is not finished yet.";
            }

            if (definition.Objective.Kind == QuestObjectiveKinds.Deliver)
            {
                var have = profile.Stash.Stash.Count(instance =>
                    string.Equals(instance.ItemId, definition.Objective.ItemId, StringComparison.Ordinal));
                if (have < definition.Objective.Count)
                {
                    return $"That quest wants {definition.Objective.Count} in the stash; there are {have}.";
                }
            }

            return null;
        }

        /// <summary>
        /// Takes the reward. Delivery quests consume their items from the stash first, then the reward is
        /// added, so a claim that cannot be built throws and leaves the profile untouched - the caller
        /// commits the result, exactly as every other change in this game is committed.
        /// </summary>
        public static (Profile Profile, QuestLog Log) Claim(Profile profile, QuestDefinition definition,
            QuestLog log, IReadOnlyDictionary<string, ItemDefinition> catalog)
        {
            var error = ClaimError(profile, definition, log, catalog);
            if (error != null)
            {
                throw new ValidationException(error);
            }

            var stash = profile.Stash;
            if (definition.Objective.Kind == QuestObjectiveKinds.Deliver)
            {
                stash = InventoryRules.RemoveItems(stash, catalog, definition.Objective.ItemId,
                    definition.Objective.Count);
            }

            foreach (var item in definition.Reward.Items)
            {
                stash = InventoryRules.AddItems(stash, catalog, item.ItemId, item.Count);
            }

            var traders = profile.Traders.ToList();
            if (definition.Reward.Standing != 0)
            {
                var index = traders.FindIndex(state =>
                    string.Equals(state.TraderId, definition.TraderId, StringComparison.Ordinal));
                if (index >= 0)
                {
                    var state = traders[index];
                    var standing = Math.Max(-CharacterSheet.MaxStanding,
                        Math.Min(CharacterSheet.MaxStanding, state.Standing + definition.Reward.Standing));
                    traders[index] = TradingRules.WithStanding(state, standing);
                }
            }

            var updated = profile.With(
                stash: stash,
                money: profile.Money + definition.Reward.Roubles,
                traders: traders);
            var claimed = log.Get(definition.QuestId).With(definition.Objective.Count, true);
            return (updated, log.With(definition.QuestId, claimed));
        }
    }
}
