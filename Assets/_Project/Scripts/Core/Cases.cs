using System;
using System.Collections.Generic;
using System.Linq;

namespace Safehouse.Core
{
    /// <summary>One stashed case that can be opened: what it rolls from, and what it wants first.</summary>
    public sealed class CaseDefinition
    {
        public CaseDefinition(string itemId, string kind, IEnumerable<string> keys)
        {
            ItemId = Validate.Identifier(itemId, "case item");
            Kind = Validate.Identifier(kind, "case container kind");
            Keys = (keys ?? Enumerable.Empty<string>())
                .Select(key => Validate.Identifier(key, "case key"))
                .Distinct()
                .ToList();
        }

        public string ItemId { get; }

        /// <summary>The containers.json entry this case rolls from.</summary>
        public string Kind { get; }

        /// <summary>At least one of these has to be in the stash. Empty means the case opens by hand.</summary>
        public IReadOnlyList<string> Keys { get; }
    }

    /// <summary>
    /// Opening a case that was carried home. Deliberately one-way: the case is consumed and its
    /// contents land in the stash, because the item model is flat - an item cannot hold items.
    /// The loot comes from the same containers.json table the raid nodes search, so a weapon case
    /// gives back what a weapon box gives back.
    /// </summary>
    public static class CaseRules
    {
        /// <summary>Which zone's rarity curve to roll on; cases are opened at home, not in a zone.</summary>
        public const string HomeZone = "outer";

        public static CaseDefinition Find(IEnumerable<CaseDefinition> cases, string itemId) =>
            cases?.FirstOrDefault(definition =>
                string.Equals(definition.ItemId, itemId, StringComparison.Ordinal));

        /// <summary>Why this case cannot be opened, or null when it can.</summary>
        public static string OpenError(Profile profile, CaseDefinition definition,
            IReadOnlyDictionary<string, ItemDefinition> catalog)
        {
            if (profile == null || definition == null)
            {
                return "There is no case selected.";
            }

            if (profile.Stash.Stash.All(instance =>
                    !string.Equals(instance.ItemId, definition.ItemId, StringComparison.Ordinal)))
            {
                return "That case is not in the stash.";
            }

            if (definition.Keys.Count == 0)
            {
                return null;
            }

            var held = profile.Stash.Stash
                .Select(instance => instance.ItemId)
                .Where(id => definition.Keys.Contains(id))
                .Distinct()
                .ToList();
            if (held.Count == 0)
            {
                var names = definition.Keys
                    .Select(id => catalog.TryGetValue(id, out var key) ? key.Name : id);
                return "Locked. It needs " + string.Join(" or ", names) + ".";
            }

            return null;
        }

        /// <summary>
        /// Consumes one case and returns what was inside. Anything the stash cannot hold throws and
        /// changes nothing, because every step works on a copy.
        /// </summary>
        public static (Profile Profile, IReadOnlyList<string> Found) Open(Profile profile,
            CaseDefinition definition, LootTables tables, PythonRandom random,
            IReadOnlyDictionary<string, ItemDefinition> catalog, string zoneId = HomeZone)
        {
            var error = OpenError(profile, definition, catalog);
            if (error != null)
            {
                throw new ValidationException(error);
            }

            var stash = InventoryRules.RemoveItems(profile.Stash, catalog, definition.ItemId, 1);
            var found = LootRolls.SearchContainer(definition.Kind, tables, random, zoneId);
            foreach (var itemId in found)
            {
                stash = InventoryRules.AddItems(stash, catalog, itemId, 1);
            }

            return (profile.With(stash: stash), found);
        }
    }
}
