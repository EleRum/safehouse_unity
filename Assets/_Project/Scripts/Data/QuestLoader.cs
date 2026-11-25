using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Safehouse.Core;

namespace Safehouse.Data
{
    /// <summary>
    /// Turns quests.json into quest definitions. Same division as the rest of Data: this owns the JSON
    /// shape and the cross-references between quests, the rules in Safehouse.Core own the behaviour.
    /// </summary>
    public static class QuestLoader
    {
        public static IReadOnlyList<QuestDefinition> Parse(JObject document,
            IReadOnlyDictionary<string, ItemDefinition> catalog, ICollection<string> notices = null)
        {
            if (!(document["quests"] is JArray rows) || rows.Count == 0)
            {
                throw new GameDataException("The quest chain must contain a quests list.");
            }

            var quests = new List<QuestDefinition>(rows.Count);
            var byId = new Dictionary<string, QuestDefinition>();

            foreach (var row in rows)
            {
                var definition = Read(row, catalog);
                if (byId.ContainsKey(definition.QuestId))
                {
                    throw new GameDataException($"Quest id {definition.QuestId} appears twice.");
                }

                byId[definition.QuestId] = definition;
                quests.Add(definition);
            }

            foreach (var quest in quests)
            {
                foreach (var required in quest.Requires)
                {
                    if (!byId.ContainsKey(required))
                    {
                        throw new GameDataException(
                            $"Quest {quest.QuestId} requires {required}, which does not exist.");
                    }

                    if (required == quest.QuestId)
                    {
                        throw new GameDataException($"Quest {quest.QuestId} requires itself.");
                    }
                }
            }

            RefuseCycles(quests, byId);
            notices?.Add($"Loaded {quests.Count} quests.");
            return quests;
        }

        public static IReadOnlyList<QuestDefinition> Load(ICollection<string> notices = null) =>
            Parse(GameDataLoader.Load(GameDataFile.Quests), CatalogLoader.Load(), notices);

        private static QuestDefinition Read(JToken row, IReadOnlyDictionary<string, ItemDefinition> catalog)
        {
            var questId = row["quest_id"]?.Value<string>();
            var objectiveRow = row["objective"];
            if (objectiveRow == null)
            {
                throw new GameDataException($"Quest {questId ?? "(no id)"} has no objective.");
            }

            var kind = objectiveRow["kind"]?.Value<string>();
            var objective = new QuestObjective(
                kind,
                objectiveRow["count"]?.Value<int>() ?? 1,
                objectiveRow["map"]?.Value<string>(),
                objectiveRow["item"]?.Value<string>(),
                objectiveRow["category"]?.Value<string>());

            // An event objective carries an event id, which is not in the item catalogue - only items are checked.
        if (kind != QuestObjectiveKinds.Event)
        {
            CheckItem(objective.ItemId, catalog, questId, "objective");
        }

            var rewardRow = row["reward"];
            var items = new List<QuestRewardItem>();
            if (rewardRow?["items"] is JArray rewardItems)
            {
                foreach (var item in rewardItems)
                {
                    var itemId = item["item"]?.Value<string>();
                    CheckItem(itemId, catalog, questId, "reward");
                    items.Add(new QuestRewardItem(itemId, item["count"]?.Value<int>() ?? 1));
                }
            }

            var reward = new QuestReward(
                rewardRow?["roubles"]?.Value<int>() ?? 0,
                rewardRow?["standing"]?.Value<int>() ?? 0,
                items);

            var requires = new List<string>();
            if (row["requires"] is JArray requiresRows)
            {
                requires.AddRange(requiresRows.Select(token => token.Value<string>()));
            }

            return new QuestDefinition(
                questId,
                row["trader_id"]?.Value<string>(),
                row["title"]?.Value<string>(),
                row["text"]?.Value<string>(),
                requires,
                objective,
                reward);
        }

        private static void CheckItem(string itemId, IReadOnlyDictionary<string, ItemDefinition> catalog,
            string questId, string where)
        {
            if (itemId != null && !catalog.ContainsKey(itemId))
            {
                throw new GameDataException(
                    $"Quest {questId} names {itemId} as a {where} item, but the catalog has no such item.");
            }
        }

        /// <summary>A chain that requires itself in a loop can never be opened, so it is refused at load.</summary>
        private static void RefuseCycles(IReadOnlyList<QuestDefinition> quests,
            IReadOnlyDictionary<string, QuestDefinition> byId)
        {
            var state = new Dictionary<string, int>(System.StringComparer.Ordinal); // 1 = on the path, 2 = done

            void Visit(QuestDefinition quest)
            {
                state[quest.QuestId] = 1;
                foreach (var required in quest.Requires)
                {
                    var next = byId[required];
                    if (!state.TryGetValue(required, out var seen))
                    {
                        Visit(next);
                        continue;
                    }

                    if (seen == 1)
                    {
                        throw new GameDataException(
                            $"The quest chain loops: {quest.QuestId} requires {required}, which leads back to it.");
                    }
                }

                state[quest.QuestId] = 2;
            }

            foreach (var quest in quests)
            {
                if (!state.ContainsKey(quest.QuestId))
                {
                    Visit(quest);
                }
            }
        }
    }
}
