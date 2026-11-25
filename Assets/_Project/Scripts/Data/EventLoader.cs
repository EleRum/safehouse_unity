using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Safehouse.Core;
using UnityEngine;

namespace Safehouse.Data
{
    /// <summary>
    /// Turns <c>StreamingAssets/data/events.json</c> into a typed <see cref="EventBook"/>. Everything that makes a
    /// scene - the prose, the choices, what they cost and what they do - is in that file, so writing the scenario
    /// never means touching the compiler.
    /// </summary>
    public static class EventLoader
    {
        public const int SupportedSchemaVersion = 1;

        public static EventBook Load()
        {
            var path = Path.Combine(Application.streamingAssetsPath, "data", "events.json");
            return Parse(JObject.Parse(File.ReadAllText(path)), null);
        }

        /// <summary>
        /// Parses an events document. With a catalogue, every item an event takes or hands out must exist - the
        /// GM's own file is read at runtime, so a typo there has to fail loudly instead of silently dropping loot.
        /// </summary>
        public static EventBook Parse(JObject document, IReadOnlyDictionary<string, ItemDefinition> catalog)
        {
            var schema = document["schema_version"]?.Value<int>() ?? 0;
            if (schema != SupportedSchemaVersion)
            {
                throw new GameDataException($"Unsupported events schema: {schema}.");
            }

            var book = new EventBook(ReadEvents(document["events"]));
            if (catalog != null)
            {
                foreach (var storyEvent in book.Events)
                {
                    foreach (var choice in storyEvent.Choices)
                    {
                        var used = choice.Cost.Items
                            .Concat(choice.Outcome.Items)
                            .Concat(choice.Failure == null ? new string[0] : choice.Failure.Items);
                        foreach (var itemId in used.Where(itemId => !catalog.ContainsKey(itemId)))
                        {
                            throw new GameDataException(
                                $"Event '{storyEvent.EventId}' uses unknown item '{itemId}'.");
                        }
                    }
                }
            }

            return book;
        }

        /// <summary>Checks every item an event hands out or takes against the catalogue, so a typo fails at load.</summary>
        public static EventBook LoadForCatalog(IReadOnlyDictionary<string, ItemDefinition> catalog)
        {
            var book = Load();
            foreach (var storyEvent in book.Events)
            {
                foreach (var choice in storyEvent.Choices)
                {
                    foreach (var itemId in choice.Cost.Items.Concat(choice.Outcome.Items))
                    {
                        if (!catalog.ContainsKey(itemId))
                        {
                            throw new GameDataException(
                                $"Event '{storyEvent.EventId}' uses unknown item '{itemId}'.");
                        }
                    }
                }
            }

            return book;
        }

        private static IReadOnlyList<StoryEvent> ReadEvents(JToken token)
        {
            if (token == null || token.Type != JTokenType.Array)
            {
                throw new GameDataException("events.json must contain an events array.");
            }

            var events = new List<StoryEvent>();
            foreach (var entry in token)
            {
                var obj = entry as JObject ?? throw new GameDataException("Each event must be an object.");
                var storyEvent = new StoryEvent(
                    Str(obj["event_id"], "event_id"),
                    Int(obj["act"], "act", 0),
                    Str(obj["phase"], "phase"),
                    Str(obj["title"], "title"),
                    Str(obj["text"], "text"),
                    ReadRequires(obj["requires"]),
                    Int(obj["weight"], "weight", 0),
                    ReadChoices(obj["choices"], obj["event_id"]?.Value<string>()));

                // Optional v2 fields: a place, extra trigger gates, and whether the scene can come back.
                if (obj["place"] is JObject place)
                {
                    storyEvent.WithPlace(new EventPlace(
                        place["map"]?.Value<string>(), place["location"]?.Value<string>(),
                        place["node"]?.Value<string>()));
                }

                if (obj["trigger"] is JObject trigger)
                {
                    var nightToken = trigger["night"];
                    bool? night = nightToken == null || nightToken.Type == JTokenType.Null
                        ? (bool?)null
                        : nightToken.Value<bool>();
                    storyEvent.WithTrigger(new EventTrigger(night,
                        Int(trigger["chance"], "chance", 100),
                        Int(trigger["after_extractions"], "after_extractions", 0)));
                }

                storyEvent.WithRepeatable(obj["repeatable"]?.Value<bool>() ?? false);
                events.Add(storyEvent);
            }

            return events;
        }

        private static EventRequires ReadRequires(JToken token)
        {
            if (token == null || token.Type != JTokenType.Object)
            {
                return new EventRequires();
            }

            return new EventRequires(Strings(token["quest_done"]), Strings(token["not_done"]),
                Strings(token["flags"]), Attributes(token["attributes"]), Strings(token["not_flags"]));
        }

        private static IReadOnlyList<EventChoice> ReadChoices(JToken token, string eventId)
        {
            if (token == null || token.Type != JTokenType.Array)
            {
                throw new GameDataException($"Event '{eventId}' has no choices array.");
            }

            var choices = new List<EventChoice>();
            foreach (var entry in token)
            {
                var obj = entry as JObject ?? throw new GameDataException("Each choice must be an object.");
                var costToken = obj["cost"] as JObject;
                var outcomeToken = obj["outcome"] as JObject
                                   ?? throw new GameDataException($"Event '{eventId}' has a choice without an outcome.");

                var cost = new EventCost(Int(costToken?["roubles"], "cost roubles", 0), Strings(costToken?["items"]));
                var ability = outcomeToken["ability"] as JObject;
                var standing = Standing(outcomeToken["standing"]);

                var outcome = new EventOutcome(
                    outcomeToken["log"]?.Value<string>() ?? "",
                    Int(outcomeToken["roubles"], "reward roubles", 0),
                    Strings(outcomeToken["items"]),
                    standing,
                    ability?["id"]?.Value<string>(),
                    Int(ability?["delta"], "ability delta", 0),
                    outcomeToken["advances"]?.Value<string>());

                var failureToken = obj["failure"] as JObject;
                var failure = failureToken == null
                    ? null
                    : new EventOutcome(
                        failureToken["log"]?.Value<string>() ?? "",
                        Int(failureToken["roubles"], "failure roubles", 0),
                        Strings(failureToken["items"]),
                        Standing(failureToken["standing"]),
                        (failureToken["ability"] as JObject)?["id"]?.Value<string>(),
                        Int((failureToken["ability"] as JObject)?["delta"], "ability delta", 0),
                        failureToken["advances"]?.Value<string>());

                choices.Add(new EventChoice(Str(obj["label"], "label"), cost, outcome,
                    obj["check"]?.Value<string>(), failure,
                    // "sets" may sit on the choice or inside its outcome: a GM writing by hand puts it wherever
                    // it reads better, and both mean the same thing.
                    Strings(obj["sets"]).Concat(Strings(outcomeToken["sets"])).Distinct().ToList(),
                    Strings(obj["requires_flags"]), Strings(obj["requires_items"])));
            }

            return choices;
        }

        private static Dictionary<string, int> Standing(JToken token)
        {
            var standing = new Dictionary<string, int>();
            if (token is JObject obj)
            {
                foreach (var pair in obj.Properties())
                {
                    standing[pair.Name] = pair.Value.Value<int>();
                }
            }

            return standing;
        }

        /// <summary>Attribute floors: `[{"id":"sense","min":6}]`, each validated by the core type.</summary>
        private static IReadOnlyList<AbilityRequirement> Attributes(JToken token)
        {
            if (token == null || token.Type != JTokenType.Array)
            {
                return new List<AbilityRequirement>();
            }

            return token
                .Select(entry => new AbilityRequirement(
                    Str(entry["id"], "id"), Int(entry["min"], "min", 0)))
                .ToList();
        }

        private static string Str(JToken token, string label)
        {
            var value = token?.Value<string>();
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new GameDataException($"An event is missing '{label}'.");
            }

            return value;
        }

        private static int Int(JToken token, string label, int fallback)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return fallback;
            }

            if (token.Type != JTokenType.Integer)
            {
                throw new GameDataException($"'{label}' must be a whole number.");
            }

            return token.Value<int>();
        }

        private static IReadOnlyList<string> Strings(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return new string[0];
            }

            if (token.Type != JTokenType.Array)
            {
                throw new GameDataException("Expected a list of strings.");
            }

            return token.Select(entry => entry.Value<string>()).ToList();
        }
    }
}
