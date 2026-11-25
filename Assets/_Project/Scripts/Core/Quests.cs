using System;
using System.Collections.Generic;
using System.Linq;

namespace Safehouse.Core
{
    /// <summary>What a quest asks for. The three kinds are the ones a trip or a stash can prove.</summary>
    public static class QuestObjectiveKinds
    {
        /// <summary>Come back alive from a map (empty map = any map).</summary>
        public const string Extract = "extract";

        /// <summary>Have brought back N of an item, or of a category, from trips.</summary>
        public const string Loot = "loot";

        /// <summary>Hand N of an item over from the stash when the quest is claimed.</summary>
        public const string Deliver = "deliver";

        /// <summary>Live through an authored event. The event id rides in the item field, which is the only
        /// free string an objective has; the engine advances it directly when the choice is taken.</summary>
        public const string Event = "event";

        public static readonly IReadOnlyList<string> All = new[] { Extract, Loot, Deliver, Event };
    }

    public static class QuestStatus
    {
        /// <summary>One of the quests it requires has not been claimed yet.</summary>
        public const string Locked = "locked";

        /// <summary>Accepted, working on it.</summary>
        public const string Open = "open";

        /// <summary>Objective met, reward waiting to be claimed.</summary>
        public const string Done = "done";

        /// <summary>Reward taken. This is what the chain edge means by "requires".</summary>
        public const string Claimed = "claimed";

        public static readonly IReadOnlyList<string> All = new[] { Locked, Open, Done, Claimed };
    }

    /// <summary>One thing a quest asks for, in the terms the game can actually measure.</summary>
    public sealed class QuestObjective
    {
        public QuestObjective(string kind, int count, string mapId = null, string itemId = null,
            string category = null)
        {
            if (!QuestObjectiveKinds.All.Contains(kind))
            {
                throw new ValidationException($"Unknown quest objective kind: '{kind}'.");
            }

            Kind = kind;
            Count = Validate.Integer(count, "objective count", 1, 9999);
            MapId = string.IsNullOrWhiteSpace(mapId) ? null : mapId;
            ItemId = string.IsNullOrWhiteSpace(itemId) ? null : itemId;
            Category = string.IsNullOrWhiteSpace(category) ? null : category;

            if (kind == QuestObjectiveKinds.Extract && mapId == null)
            {
                MapId = null; // any map - allowed and meaningful
            }

            if (kind != QuestObjectiveKinds.Extract && ItemId == null && Category == null)
            {
                throw new ValidationException($"A {kind} objective needs an item or a category.");
            }
        }

        public string Kind { get; }
        public int Count { get; }
        public string MapId { get; }
        public string ItemId { get; }
        public string Category { get; }

        /// <summary>"Come back from Customs", "bring back 2 of something", for the screen.</summary>
        public string Describe(IReadOnlyDictionary<string, ItemDefinition> catalog)
        {
            var what = ItemId != null && catalog.TryGetValue(ItemId, out var item)
                ? item.Name
                : Category ?? "items";
            switch (Kind)
            {
                case QuestObjectiveKinds.Event:
                    return "See it through: " + (ItemId ?? "an event");
                case QuestObjectiveKinds.Extract:
                    return MapId == null
                        ? Count == 1 ? "Come back alive from any map" : $"Come back alive {Count} times"
                        : Count == 1 ? $"Come back alive from {MapId}" : $"Come back alive from {MapId} {Count} times";
                case QuestObjectiveKinds.Loot:
                    return Count == 1 ? $"Bring back {what}" : $"Bring back {Count} x {what}";
                default:
                    return Count == 1 ? $"Hand over {what}" : $"Hand over {Count} x {what}";
            }
        }
    }

    public sealed class QuestRewardItem
    {
        public QuestRewardItem(string itemId, int count)
        {
            ItemId = Validate.Identifier(itemId, "reward item_id");
            Count = Validate.Integer(count, "reward item count", 1, 999);
        }

        public string ItemId { get; }
        public int Count { get; }
    }

    public sealed class QuestReward
    {
        public QuestReward(int roubles = 0, int standing = 0, IEnumerable<QuestRewardItem> items = null)
        {
            Roubles = Validate.Integer(roubles, "reward roubles", 0, 2_000_000_000);
            Standing = Validate.Integer(standing, "reward standing", -CharacterSheet.MaxStanding,
                CharacterSheet.MaxStanding);
            Items = (items ?? Enumerable.Empty<QuestRewardItem>()).ToList();
        }

        public int Roubles { get; }
        public int Standing { get; }
        public IReadOnlyList<QuestRewardItem> Items { get; }

        public string Describe(IReadOnlyDictionary<string, ItemDefinition> catalog)
        {
            var parts = new List<string>();
            if (Roubles > 0)
            {
                parts.Add(Roubles.ToString("N0") + " R");
            }

            if (Standing != 0)
            {
                parts.Add((Standing > 0 ? "+" : "") + Standing + " standing");
            }

            foreach (var item in Items)
            {
                var name = catalog.TryGetValue(item.ItemId, out var definition) ? definition.Name : item.ItemId;
                parts.Add(item.Count == 1 ? name : item.Count + " x " + name);
            }

            return parts.Count == 0 ? "Nothing" : string.Join("   ", parts);
        }
    }

    /// <summary>One quest as the data file describes it. Immutable.</summary>
    public sealed class QuestDefinition
    {
        public QuestDefinition(string questId, string traderId, string title, string text,
            IEnumerable<string> requires, QuestObjective objective, QuestReward reward)
        {
            QuestId = Validate.Identifier(questId, "quest_id");
            TraderId = Validate.Identifier(traderId, "trader_id");
            Title = Validate.Text(title, "quest title", 80);
            Text = Validate.Text(text ?? "", "quest text", 600, allowEmpty: true);
            Requires = (requires ?? Enumerable.Empty<string>())
                .Select(id => Validate.Identifier(id, "required quest id"))
                .Distinct()
                .ToList();
            Objective = objective ?? throw new ValidationException($"Quest {questId} has no objective.");
            Reward = reward ?? new QuestReward();
        }

        public string QuestId { get; }
        public string TraderId { get; }
        public string Title { get; }
        public string Text { get; }
        public IReadOnlyList<string> Requires { get; }
        public QuestObjective Objective { get; }
        public QuestReward Reward { get; }
    }

    /// <summary>
    /// What one character has done with one quest. Only two things are stored - how far the objective
    /// has got, and whether the reward was taken - because a status that was stored could go stale
    /// the moment a quest's requirements changed. <see cref="QuestRules.StatusOf"/> derives the rest.
    /// </summary>
    public sealed class QuestEntry
    {
        public QuestEntry(int progress, bool claimed)
        {
            Progress = Validate.Integer(progress, "quest progress", 0, 9999);
            Claimed = claimed;
        }

        public int Progress { get; }
        public bool Claimed { get; }

        public static readonly QuestEntry Fresh = new QuestEntry(0, false);

        public QuestEntry With(int progress, bool claimed) => new QuestEntry(progress, claimed);
    }

    /// <summary>
    /// A character's quest state: one entry per quest id it has touched. Quests it has never touched
    /// are absent, which is the same as <see cref="QuestEntry.Fresh"/>. Kept in its own document
    /// rather than inside the profile, so turning quests on needed no save-schema migration.
    /// </summary>
    public sealed class QuestLog
    {
        public const int MaxEntries = 500;

        private readonly Dictionary<string, QuestEntry> _entries;

        public QuestLog(IEnumerable<KeyValuePair<string, QuestEntry>> entries = null)
        {
            _entries = new Dictionary<string, QuestEntry>(StringComparer.Ordinal);
            if (entries == null)
            {
                return;
            }

            foreach (var pair in entries)
            {
                if (_entries.Count >= MaxEntries)
                {
                    throw new ValidationException($"A quest log holds at most {MaxEntries} quests.");
                }

                _entries[Validate.Identifier(pair.Key, "quest_id")] = pair.Value ?? QuestEntry.Fresh;
            }
        }

        public static readonly QuestLog Empty = new QuestLog();

        public IReadOnlyDictionary<string, QuestEntry> Entries => _entries;

        public QuestEntry Get(string questId) =>
            _entries.TryGetValue(questId, out var entry) ? entry : QuestEntry.Fresh;

        public int ProgressOf(string questId) => Get(questId).Progress;

        public bool Claimed(string questId) => Get(questId).Claimed;

        /// <summary>This log with one quest replaced. Absent ids stay absent.</summary>
        public QuestLog With(string questId, QuestEntry entry)
        {
            var copy = new Dictionary<string, QuestEntry>(_entries, StringComparer.Ordinal);
            copy[questId] = entry;
            return new QuestLog(copy);
        }
    }
}
