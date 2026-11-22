using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Safehouse.Core;

namespace Safehouse.Data
{
    /// <summary>
    /// The quest log document. Deliberately small: one number and one flag per quest, plus the id of
    /// the character it belongs to so a file dropped in the wrong folder is caught rather than loaded.
    /// </summary>
    public static class QuestSerializer
    {
        public const int SchemaVersion = 1;

        public static JObject ToJson(string profileId, QuestLog log)
        {
            var quests = new JObject();
            foreach (var pair in log?.Entries ?? new Dictionary<string, QuestEntry>())
            {
                quests[pair.Key] = new JObject
                {
                    ["progress"] = pair.Value.Progress,
                    ["claimed"] = pair.Value.Claimed,
                };
            }

            return new JObject
            {
                ["schema_version"] = SchemaVersion,
                ["profile_id"] = profileId,
                ["quests"] = quests,
            };
        }

        public static byte[] Serialize(string profileId, QuestLog log) =>
            System.Text.Encoding.UTF8.GetBytes(ToJson(profileId, log).ToString());

        public static QuestLog FromJson(JToken document, string expectedProfileId)
        {
            if (!(document is JObject root))
            {
                throw new StorageException("A quest log must be an object.");
            }

            var version = root["schema_version"]?.Value<int>() ?? 0;
            if (version != SchemaVersion)
            {
                throw new StorageException(
                    $"Quest log schema {version} is not supported (this build reads {SchemaVersion}).");
            }

            var profileId = root["profile_id"]?.Value<string>();
            if (expectedProfileId != null && profileId != null && profileId != expectedProfileId)
            {
                throw new StorageException(
                    $"That quest log belongs to character {profileId}, not {expectedProfileId}.");
            }

            var entries = new List<KeyValuePair<string, QuestEntry>>();
            if (root["quests"] is JObject quests)
            {
                foreach (var property in quests.Properties())
                {
                    entries.Add(new KeyValuePair<string, QuestEntry>(property.Name, new QuestEntry(
                        property.Value["progress"]?.Value<int>() ?? 0,
                        property.Value["claimed"]?.Value<bool>() ?? false)));
                }
            }

            return new QuestLog(entries);
        }
    }
}
