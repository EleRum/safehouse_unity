using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Safehouse.Core;

namespace Safehouse.Data
{
    /// <summary>
    /// Steps an older save document up to the current schema, in memory. The C# side of Python migrations.py
    /// (versions 1-6, ported step for step) plus 6 to 7, which adds the rig and backpack. Files are only
    /// rewritten by a later normal save, so the previous-save backup keeps the exact older bytes.
    /// </summary>
    public static class ProfileMigrations
    {
        // v0.1/v0.2 test-item ids -> imported game items. Each replacement fits inside the old footprint, so
        // converted stashes cannot overlap or cross the stash edge.
        public static readonly IReadOnlyDictionary<string, string> LegacyItemIds = new Dictionary<string, string>
        {
            ["cpu_fan"] = "cpu-fan",
            ["military_cable"] = "military-cable",
            ["circuit_board"] = "printed-circuit-board",
            ["broken_phone"] = "broken-gphone-smartphone",
            ["electric_drill"] = "electric-drill",
            ["pliers"] = "pliers",
            ["pipe_wrench"] = "pipe-grip-wrench",
            ["medical_kit"] = "grizzly-medical-kit",
            ["bandage"] = "army-bandage",
            ["antiseptic"] = "bottle-of-hydrogen-peroxide",
            ["canned_beans"] = "can-of-green-peas",
            ["water_bottle"] = "bottle-of-water-06l",
            ["ration_pack"] = "salty-dog-beef-sausage",
            ["car_battery"] = "car-battery",
            ["bolts"] = "bolts",
            ["fuel_can"] = "metal-fuel-tank",
            ["silver_watch"] = "roler-submariner-gold-wrist-watch",
            ["gold_chain"] = "golden-neck-chain",
            ["long_weapon_part"] = "ak-74-545x39-6l18-45-round-magazine",
            ["duct_tape"] = "duct-tape",
        };

        private static readonly IReadOnlyDictionary<int, Func<JObject, JObject>> Steps =
            new Dictionary<int, Func<JObject, JObject>>
            {
                [1] = V1ToV2, [2] = V2ToV3, [3] = V3ToV4, [4] = V4ToV5, [5] = V5ToV6, [6] = V6ToV7, [7] = V7ToV8,
                [8] = V8ToV9, [9] = V9ToV10, [10] = V10ToV11, [11] = V11ToV12,
            };

        /// <summary>
        /// Upgrades a copy of the document to the current schema (10). Anything this version cannot upgrade - not an object,
        /// a version it does not know, or a newer one - is returned unchanged, so reading it reports the
        /// problem instead of guessing.
        /// </summary>
        public static JToken Upgrade(JToken document)
        {
            if (!(document is JObject original))
            {
                return document;
            }

            var current = (JObject)original.DeepClone();
            while (true)
            {
                var version = current["schema_version"];
                if (version == null || version.Type != JTokenType.Integer)
                {
                    return current;
                }

                long number;
                try
                {
                    number = version.Value<long>();
                }
                catch (OverflowException)
                {
                    return current;
                }

                if (number >= Profile.SchemaVersion || number < 1 || !Steps.TryGetValue((int)number, out var step))
                {
                    return current;
                }

                current = step(current);
            }
        }

        private static JObject V1ToV2(JObject raw)
        {
            // v0.2 adds per-trader loyalty, standing and stock state.
            raw["schema_version"] = 2;
            raw["traders"] = new JArray();
            return raw;
        }

        private static JObject V2ToV3(JObject raw)
        {
            // Imported game data replaced the test items.
            if (raw["stash"] is JArray stash)
            {
                foreach (var item in stash.OfType<JObject>())
                {
                    if (item["item_id"] != null && item["item_id"].Type == JTokenType.String
                        && LegacyItemIds.TryGetValue(item.Value<string>("item_id"), out var renamed))
                    {
                        item["item_id"] = renamed;
                    }
                }
            }

            raw["schema_version"] = 3;
            return raw;
        }

        private static JObject V3ToV4(JObject raw)
        {
            // Profiles became characters of one account: everyone starts healthy, available and with nothing
            // equipped. Restock windows counted campaign days before and count real hours now, so earlier
            // purchase counts no longer apply.
            raw["schema_version"] = 4;
            raw["status"] = CharacterSheet.Active;
            raw["health"] = CharacterSheet.MaxHealth;
            raw["loadout"] = new JArray();
            if (raw["traders"] is JArray traders)
            {
                foreach (var state in traders.OfType<JObject>())
                {
                    state["purchases"] = new JArray();
                }
            }

            return raw;
        }

        private static JObject V4ToV5(JObject raw)
        {
            // Health becomes a per-body-part breakdown. An existing flat total is spread across the parts by
            // each part's own share of the maximum; where a character was hurt cannot be recovered, only how much.
            var health = raw["health"];
            var total = health != null && health.Type == JTokenType.Integer
                ? (int)Math.Max(0, Math.Min(CharacterSheet.MaxHealth, health.Value<long>()))
                : CharacterSheet.MaxHealth;

            var bodyParts = new JObject();
            var assigned = 0;
            var parts = CharacterSheet.BodyPartMaxHealth;
            for (var i = 0; i < parts.Count - 1; i++)
            {
                // Math.Round is round-half-to-even, as Python's round() is.
                var share = (int)Math.Max(0, Math.Min(parts[i].Value,
                    Math.Round((double)total * parts[i].Value / CharacterSheet.MaxHealth)));
                bodyParts[parts[i].Key] = share;
                assigned += share;
            }

            var last = parts[parts.Count - 1];
            bodyParts[last.Key] = Math.Max(0, Math.Min(last.Value, total - assigned));

            raw["schema_version"] = 5;
            raw["body_parts"] = bodyParts;
            raw["conditions"] = new JObject();
            raw.Remove("health");
            return raw;
        }

        private static JObject V5ToV6(JObject raw)
        {
            // Character-sheet flavour: blank bio fields and base ability scores, nothing spent yet.
            raw["schema_version"] = 6;
            raw["bio"] = new JObject(CharacterSheet.BioFields.Select(field => new JProperty(field, "")));
            raw["abilities"] = new JObject(CharacterSheet.Abilities.Select(name =>
                new JProperty(name, CharacterSheet.AbilityBase)));
            return raw;
        }

        private static JObject V6ToV7(JObject raw)
        {
            // The rig and backpack become real grids. The old save had none, so they start empty.
            raw["schema_version"] = 7;
            raw["carried"] = new JObject
            {
                ["rig"] = EmptyGrid(Profile.DefaultRigWidth, Profile.DefaultRigHeight),
                ["backpack"] = EmptyGrid(Profile.DefaultBackpackWidth, Profile.DefaultBackpackHeight),
            };
            return raw;
        }

        private static JObject V7ToV8(JObject raw)
        {
            // Every character gets a personal room, starting at level 1.
            raw["schema_version"] = 8;
            raw["personal_room"] = new JObject
            {
                ["level"] = PersonalRoom.DefaultLevel,
            };
            return raw;
        }

        private static JObject V8ToV9(JObject raw)
        {
            // A blank Gundog sheet. Expeditions do not read it.
            var blank = GundogSheet.Blank;
            raw["schema_version"] = 9;
            raw["gundog"] = new JObject
            {
                ["abilities"] = new JObject(Gundog.Abilities.Select(stat =>
                    new JProperty(stat.StatId, blank.Abilities[stat.StatId]))),
                ["main_class"] = "",
                ["sub_class"] = "",
                ["arts"] = new JArray(),
                ["career"] = new JArray(blank.Career),
                ["reward_points"] = 0,
                ["movement"] = 0,
                ["durability"] = 0,
                ["rank"] = "",
                ["language"] = "",
                ["occupation"] = "",
                ["era"] = "",
            };
            return raw;
        }

        /// <summary>
        /// The eight attributes become the Gundog sheet's eight, because having two vocabularies for the same
        /// idea was a trap: the profile said strength and the sheet said muscle, and nothing said which one
        /// was real. The scores come across unchanged, in the same order.
        /// </summary>
        private static JObject V9ToV10(JObject raw)
        {
            var old = raw["abilities"] as JObject;
            if (old != null)
            {
                var renamed = new JObject();
                foreach (var ability in CharacterSheet.Abilities)
                {
                    var was = V9Names.TryGetValue(ability, out var legacy) ? legacy : ability;
                    var score = old[was] ?? old[ability];
                    renamed[ability] = score != null ? score.DeepClone() : CharacterSheet.AbilityBase;
                }

                raw["abilities"] = renamed;
            }

            raw["schema_version"] = 10;
            return raw;
        }

        /// <summary>
        /// Worn gear gets durability. Old files had none because nothing could wear out: every worn piece starts
        /// whole, which is exactly what it was.
        /// </summary>
        private static JObject V10ToV11(JObject raw)
        {
            if (raw["loadout"] is JArray loadout)
            {
                foreach (var entry in loadout.OfType<JObject>())
                {
                    if (entry["durability"] == null)
                    {
                        entry["durability"] = EquippedItem.MaxDurability;
                    }
                }
            }

            raw["schema_version"] = 11;
            return raw;
        }

        /// <summary>schema 9's key for each schema 10 attribute.</summary>
        private static readonly IReadOnlyDictionary<string, string> V9Names = new Dictionary<string, string>
        {
            ["muscle"] = "strength",
            ["finesse"] = "dexterity",
            ["agility"] = "agility",
            ["sense"] = "sense",
            ["perception"] = "intelligence",
            ["charm"] = "charisma",
            ["build"] = "constitution",
            ["humanity"] = "looks",
        };

        /// <summary>
        /// Story flags. A character written before them simply has none: their history was never recorded, and
        /// pretending otherwise would let a later scene think they made a choice they never made.
        /// </summary>
        private static JObject V11ToV12(JObject raw)
        {
            raw["schema_version"] = 12;
            if (raw["flags"] == null)
            {
                raw["flags"] = new JArray();
            }

            return raw;
        }

        private static JObject EmptyGrid(int width, int height) => new JObject
        {
            ["width"] = width,
            ["height"] = height,
            ["items"] = new JArray(),
        };
    }
}
