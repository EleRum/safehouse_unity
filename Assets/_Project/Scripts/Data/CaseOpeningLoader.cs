using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Safehouse.Core;

namespace Safehouse.Data
{
    /// <summary>
    /// Reads case_openings.json: which stashed case opens, which containers.json table it rolls from,
    /// and which keys it wants. Every reference is checked here, so a typo in the data is a load error
    /// rather than a case that silently opens into nothing.
    /// </summary>
    public static class CaseOpeningLoader
    {
        public static IReadOnlyList<CaseDefinition> Parse(JObject document,
            IReadOnlyDictionary<string, ItemDefinition> catalog, ContainerRules containers)
        {
            if (!(document["cases"] is JArray rows) || rows.Count == 0)
            {
                throw new GameDataException("case_openings.json must contain a cases list.");
            }

            var cases = new List<CaseDefinition>();
            foreach (var row in rows)
            {
                var itemId = row["item"]?.Value<string>();
                var kind = row["kind"]?.Value<string>();
                if (itemId == null || !catalog.ContainsKey(itemId))
                {
                    throw new GameDataException(
                        $"case_openings.json names {itemId ?? "(nothing)"}, which is not an item.");
                }

                if (kind == null || !containers.Containers.ContainsKey(kind))
                {
                    throw new GameDataException(
                        $"case_openings.json rolls {itemId} from {kind ?? "(nothing)"}, which is not a container kind.");
                }

                var keys = new List<string>();
                if (row["keys"] is JArray keyRows)
                {
                    foreach (var key in keyRows)
                    {
                        var keyId = key.Value<string>();
                        if (keyId == null || !catalog.ContainsKey(keyId))
                        {
                            throw new GameDataException($"case_openings.json names key {keyId}, which is not an item.");
                        }

                        if (catalog[keyId].Category != "Keys")
                        {
                            throw new GameDataException($"case_openings.json names {keyId} as a key, but it is a {catalog[keyId].Category}.");
                        }

                        keys.Add(keyId);
                    }
                }

                cases.Add(new CaseDefinition(itemId, kind, keys));
            }

            var duplicates = cases.GroupBy(definition => definition.ItemId).Where(group => group.Count() > 1).ToList();
            if (duplicates.Count > 0)
            {
                throw new GameDataException($"case_openings.json lists {duplicates[0].Key} twice.");
            }

            return cases;
        }

        public static IReadOnlyList<CaseDefinition> Load(
            IReadOnlyDictionary<string, ItemDefinition> catalog, ContainerRules containers) =>
            Parse(GameDataLoader.Load("case_openings"), catalog, containers);
    }
}
