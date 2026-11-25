using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Safehouse.Core;
using UnityEngine;

namespace Safehouse.Data
{
    /// <summary>
    /// The scene book the game actually runs: what shipped in <c>StreamingAssets/data/events.json</c>, with the
    /// GM's own file laid over the top (same event id wins). StreamingAssets is read-only in a build, so the
    /// editor writes to <c>persistentDataPath/Safehouse/events/</c> and this class decides who wins.
    /// </summary>
    public static class EventStore
    {
        private const string Folder = "events";
        private const string FileName = "gm-events.json";

        public static string OverrideFolder =>
            Path.Combine(Application.persistentDataPath, "Safehouse", Folder);

        public static string OverridePath => Path.Combine(OverrideFolder, FileName);

        private static EventBook _cached;
        private static string _cachedStamp;

        /// <summary>
        /// The stamp of both files. A build never rewrites them, but the editor does - and a book cached from
        /// before the save is why an edited scene looked saved while the map still ran the old one.
        /// </summary>
        private static string Stamp()
        {
            var shipped = Path.Combine(Application.streamingAssetsPath, "data", "events.json");
            var a = File.Exists(shipped) ? File.GetLastWriteTimeUtc(shipped).Ticks.ToString() : "-";
            var b = File.Exists(OverridePath) ? File.GetLastWriteTimeUtc(OverridePath).Ticks.ToString() : "-";
            return a + ":" + b;
        }

        /// <summary>Shipped scenes, overridden by the GM's ones wherever an id appears in both.</summary>
        public static EventBook Load(IReadOnlyDictionary<string, ItemDefinition> catalog)
        {
            var stamp = Stamp();
            if (_cached != null && _cachedStamp == stamp)
            {
                return _cached;
            }

            var book = LoadFresh(catalog);
            _cached = book;
            _cachedStamp = stamp;
            return book;
        }

        private static EventBook LoadFresh(IReadOnlyDictionary<string, ItemDefinition> catalog)
        {
            var shipped = EventLoader.LoadForCatalog(catalog);
            var own = LoadOverrides(catalog);
            if (own.Count == 0)
            {
                return shipped;
            }

            var merged = new List<StoryEvent>();
            foreach (var scene in shipped.Events)
            {
                merged.Add(own.TryGetValue(scene.EventId, out var edited) ? edited : scene);
            }

            // GM-only scenes that never existed in the shipped file.
            merged.AddRange(own.Values.Where(scene => shipped.Find(scene.EventId) == null));
            return new EventBook(merged);
        }

        private static Dictionary<string, StoryEvent> LoadOverrides(
            IReadOnlyDictionary<string, ItemDefinition> catalog)
        {
            var result = new Dictionary<string, StoryEvent>();
            if (!File.Exists(OverridePath))
            {
                return result;
            }

            try
            {
                var book = EventLoader.Parse(JObject.Parse(File.ReadAllText(OverridePath)), catalog);
                foreach (var scene in book.Events)
                {
                    result[scene.EventId] = scene;
                }
            }
            catch (System.Exception error)
            {
                // A broken GM file must not take the game down with it: log it and run the shipped scenes.
                Debug.LogWarning($"GM events file ignored: {error.Message}");
            }

            return result;
        }

        /// <summary>Writes every scene in the book to the override file. The editor saves the whole table.</summary>
        public static void Save(EventBook book)
        {
            Directory.CreateDirectory(OverrideFolder);
            _cached = null;
            _cachedStamp = null;
            var document = new JObject
            {
                ["schema_version"] = EventLoader.SupportedSchemaVersion,
                ["note"] = "Edited in the GM screen. Overrides the shipped scenes with the same event id.",
                ["events"] = new JArray(book.Events.Select(ToJson)),
            };
            File.WriteAllText(OverridePath, document.ToString());
        }

        /// <summary>Deletes the GM file, so the shipped scenes take over again.</summary>
        public static void Clear()
        {
            if (File.Exists(OverridePath))
            {
                File.Delete(OverridePath);
            }
        }

        private static JObject ToJson(StoryEvent scene)
        {
            var choices = new JArray();
            foreach (var choice in scene.Choices)
            {
                var entry = new JObject
                {
                    ["label"] = choice.Label,
                    ["cost"] = new JObject
                    {
                        ["roubles"] = choice.Cost.Roubles,
                        ["items"] = new JArray(choice.Cost.Items),
                    },
                    ["outcome"] = Outcome(choice.Outcome),
                };
                if (choice.Check != null)
                {
                    entry["check"] = choice.Check;
                }

                if (choice.Sets.Count > 0)
                {
                    entry["sets"] = new JArray(choice.Sets);
                }

                if (choice.RequiresFlags.Count > 0)
                {
                    entry["requires_flags"] = new JArray(choice.RequiresFlags);
                }

                if (choice.RequiresItems.Count > 0)
                {
                    entry["requires_items"] = new JArray(choice.RequiresItems);
                }

                if (choice.Failure != null)
                {
                    entry["failure"] = Outcome(choice.Failure);
                }

                choices.Add(entry);
            }

            var document = new JObject
            {
                ["event_id"] = scene.EventId,
                ["act"] = scene.Act,
                ["phase"] = scene.Phase,
                ["title"] = scene.Title,
                ["text"] = scene.Text,
                ["requires"] = new JObject
                {
                    ["quest_done"] = new JArray(scene.Requires.QuestDone),
                    ["not_done"] = new JArray(scene.Requires.NotDone),
                    ["flags"] = new JArray(scene.Requires.Flags),
                    ["not_flags"] = new JArray(scene.Requires.Unseen),
                    ["attributes"] = new JArray(scene.Requires.Attributes.Select(need => new JObject
                    {
                        ["id"] = need.AbilityId,
                        ["min"] = need.Min,
                    })),
                },
                ["weight"] = scene.Weight,
                ["repeatable"] = scene.Repeatable,
                ["place"] = new JObject
                {
                    ["map"] = scene.Place.MapId,
                    ["location"] = scene.Place.LocationId,
                    ["node"] = scene.Place.NodeKind,
                },
                ["trigger"] = new JObject
                {
                    ["night"] = scene.Trigger.Night.HasValue ? new JValue(scene.Trigger.Night.Value) : JValue.CreateNull(),
                    ["chance"] = scene.Trigger.ChancePercent,
                    ["after_extractions"] = scene.Trigger.AfterExtractions,
                },
                ["choices"] = choices,
            };
            return document;
        }

        private static JObject Outcome(EventOutcome outcome) => new JObject
        {
            ["log"] = outcome.Log,
            ["roubles"] = outcome.Roubles,
            ["items"] = new JArray(outcome.Items),
            ["standing"] = new JObject(outcome.Standing.Select(pair => new JProperty(pair.Key, pair.Value))),
            ["ability"] = outcome.AbilityId == null
                ? JValue.CreateNull()
                : new JObject { ["id"] = outcome.AbilityId, ["delta"] = outcome.AbilityDelta },
            ["advances"] = outcome.Advances == null ? JValue.CreateNull() : new JValue(outcome.Advances),
        };
    }
}
