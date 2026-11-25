using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Safehouse.Core;

namespace Safehouse.Data
{
    /// <summary>
    /// The quest chain the game runs on: the shipped `quests.json`, with the GM's own file laid over it. The GM file
    /// is a whole chain like the shipped one - a quest with the same id replaces the shipped one field by field,
    /// a new id is appended - so a scene's editor page can change a quest's title, text, reward or the scene it
    /// finishes without anyone hand-editing StreamingAssets, which is read-only in a build.
    /// </summary>
    public static class QuestStore
    {
        private const string FileName = "gm-quests.json";

        public static string Folder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "..", "LocalLow",
            "DefaultCompany", "Safehouse", "Safehouse", "quests");

        public static string OverridePath => Path.Combine(Folder, FileName);

        public static IReadOnlyList<QuestDefinition> Load()
        {
            var shipped = QuestLoader.Load();
            if (!File.Exists(OverridePath))
            {
                return shipped;
            }

            try
            {
                var catalog = CatalogLoader.Load();
                var root = JObject.Parse(File.ReadAllText(OverridePath));
                var overrides = QuestLoader.Parse(root, catalog);
                var byId = overrides.ToDictionary(quest => quest.QuestId, StringComparer.Ordinal);

                var merged = shipped.Where(quest => !byId.ContainsKey(quest.QuestId)).ToList();
                merged.AddRange(overrides);
                return merged;
            }
            catch (Exception error)
            {
                UnityEngine.Debug.LogWarning("gm-quests.json ignored: " + error.Message);
                return shipped;
            }
        }

        /// <summary>Writes the whole chain to the GM file; the shipped copy is never touched.</summary>
        public static void Save(IEnumerable<QuestDefinition> quests)
        {
            Directory.CreateDirectory(Folder);
            var rows = new JArray();
            foreach (var quest in quests)
            {
                rows.Add(new JObject
                {
                    ["quest_id"] = quest.QuestId,
                    ["trader_id"] = quest.TraderId,
                    ["title"] = quest.Title,
                    ["text"] = quest.Text,
                    ["requires"] = new JArray(quest.Requires),
                    ["objective"] = new JObject
                    {
                        ["kind"] = quest.Objective.Kind,
                        ["count"] = quest.Objective.Count,
                        ["map"] = quest.Objective.MapId,
                        ["item"] = quest.Objective.ItemId,
                        ["category"] = quest.Objective.Category,
                    },
                    ["reward"] = new JObject
                    {
                        ["roubles"] = quest.Reward.Roubles,
                        ["standing"] = quest.Reward.Standing,
                        ["items"] = new JArray(quest.Reward.Items.Select(item => new JObject
                        {
                            ["item"] = item.ItemId,
                            ["count"] = item.Count,
                        })),
                    },
                });
            }

            File.WriteAllText(OverridePath, new JObject { ["quests"] = rows }.ToString());
        }

        public static void Clear()
        {
            if (File.Exists(OverridePath))
            {
                File.Delete(OverridePath);
            }
        }
    }

    /// <summary>Which scene a quest finishes: the objective's own item id, and a small file for rebinding.</summary>
    public static class QuestBindings
    {
        private const string FileName = "gm-bindings.json";

        public static string Folder => QuestStore.Folder;

        public static string Path_ => System.IO.Path.Combine(Folder, FileName);

        public static Dictionary<string, string> Load()
        {
            try
            {
                if (!File.Exists(Path_))
                {
                    return new Dictionary<string, string>();
                }

                var root = JObject.Parse(File.ReadAllText(Path_));
                return root.Properties().ToDictionary(pair => pair.Name, pair => pair.Value.Value<string>());
            }
            catch (Exception error)
            {
                UnityEngine.Debug.LogWarning("gm-bindings.json ignored: " + error.Message);
                return new Dictionary<string, string>();
            }
        }

        public static void Save(IReadOnlyDictionary<string, string> bindings)
        {
            Directory.CreateDirectory(Folder);
            var root = new JObject();
            foreach (var pair in bindings.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                root[pair.Key] = pair.Value;
            }

            File.WriteAllText(Path_, root.ToString());
        }

        /// <summary>The chain with both the GM's edits and the older binding file applied.</summary>
        public static IReadOnlyList<QuestDefinition> LoadQuests()
        {
            var chain = QuestStore.Load();
            var bindings = Load();
            if (bindings.Count == 0)
            {
                return chain;
            }

            var result = new List<QuestDefinition>();
            foreach (var quest in chain)
            {
                if (bindings.TryGetValue(quest.QuestId, out var sceneId)
                    && quest.Objective.Kind == QuestObjectiveKinds.Event
                    && !string.Equals(quest.Objective.ItemId, sceneId, StringComparison.Ordinal))
                {
                    var objective = new QuestObjective(quest.Objective.Kind, quest.Objective.Count,
                        quest.Objective.MapId, sceneId, quest.Objective.Category);
                    result.Add(Rebind(quest, objective));
                }
                else
                {
                    result.Add(quest);
                }
            }

            return result;
        }

        public static IEnumerable<QuestDefinition> ForScene(IEnumerable<QuestDefinition> quests, string sceneId) =>
            quests.Where(quest => quest.Objective.Kind == QuestObjectiveKinds.Event
                                  && string.Equals(quest.Objective.ItemId, sceneId, StringComparison.Ordinal));

        /// <summary>Points a quest at a scene - or, with a null scene, takes the binding off it.</summary>
        public static void Bind(string questId, string sceneId)
        {
            var chain = QuestStore.Load().ToList();
            var quest = chain.FirstOrDefault(entry => entry.QuestId == questId);
            if (quest == null)
            {
                return;
            }

            var objective = new QuestObjective(quest.Objective.Kind, quest.Objective.Count,
                quest.Objective.MapId, string.IsNullOrWhiteSpace(sceneId) ? null : sceneId,
                quest.Objective.Category);
            var at = chain.IndexOf(quest);
            chain[at] = Rebind(quest, objective);
            QuestStore.Save(chain);

            var bindings = Load();
            if (bindings.Remove(questId))
            {
                Save(bindings);
            }
        }

        private static QuestDefinition Rebind(QuestDefinition quest, QuestObjective objective) =>
            new QuestDefinition(quest.QuestId, quest.TraderId, quest.Title, quest.Text, quest.Requires,
                objective, quest.Reward);
    }
}
