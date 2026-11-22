using System.Collections.Generic;
using System.Linq;

namespace Safehouse.Core
{
    /// <summary>
    /// Gundog Revised character-sheet vocabulary. Stored on the character for the later board game.
    /// Expeditions and the automatic fight do not read it.
    /// </summary>
    public static class Gundog
    {
        public const int AbilityMin = 1;
        public const int AbilityMax = 10;
        public const int AbilityDefault = 5;
        public const int CareerLines = 5;
        public const int MaxLineLength = 40;
        public const int MaxReward = 999;
        public const int MaxMovement = 99;
        public const int MaxDurability = 999;

        public static readonly IReadOnlyList<GundogStat> Abilities = new[]
        {
            new GundogStat("muscle", "STRENGTH"),
            new GundogStat("finesse", "FINESSE"),
            new GundogStat("agility", "AGILITY"),
            new GundogStat("sense", "SENSE"),
            new GundogStat("perception", "PERCEPTION"),
            new GundogStat("charm", "CHARM"),
            new GundogStat("build", "BUILD"),
            new GundogStat("humanity", "HUMANITY"),
        };

        /// <summary>Skill families, in the same order as each class's modifier row.</summary>
        public static readonly IReadOnlyList<GundogStat> Skills = new[]
        {
            new GundogStat("shooting", "SHOOTING"),
            new GundogStat("melee", "MELEE"),
            new GundogStat("transport", "TRANSPORT"),
            new GundogStat("perception", "PERCEPTION"),
            new GundogStat("negotiation", "NEGOTIATION"),
            new GundogStat("education", "EDUCATION"),
            new GundogStat("technique", "TECHNIQUE"),
        };

        public static readonly IReadOnlyList<GundogClass> Classes = new[]
        {
            Class("assault", "ASSAULT", "ASSAULT",
                new[] { 30, 20, 15, 10, 10, 10, 10 }, new[] { 20, 15, 15, 10, 10, 10, 10 },
                Art("assault-1", "GUN ACTION"), Art("assault-2", "QUICK DRAW"),
                Art("assault-3", "COMBAT SENSE"), Art("assault-4", "COMBAT MOVE")),
            Class("operator", "OPERATOR", "OPERATOR",
                new[] { 10, 10, 10, 10, 15, 30, 20 }, new[] { 10, 10, 10, 10, 10, 20, 20 },
                Art("operator-1", "ADVICE"), Art("operator-2", "WALKING MANUAL"),
                Art("operator-3", "ELECTRONICS"), Art("operator-4", "COMPUTER BRAIN")),
            Class("commander", "COMMANDER", "COMMANDER",
                new[] { 25, 10, 10, 10, 20, 20, 10 }, new[] { 20, 10, 10, 10, 15, 15, 10 },
                Art("commander-1", "OUT GENERAL"), Art("commander-2", "ATTACK FORMATION"),
                Art("commander-3", "COVER FORMATION"), Art("commander-4", "KILLING EXPRESSION")),
            Class("scout", "SCOUT", "SCOUT",
                new[] { 10, 10, 25, 20, 10, 15, 15 }, new[] { 10, 10, 20, 20, 10, 10, 10 },
                Art("scout-1", "ACUTE SENSE"), Art("scout-2", "ACROBATIC FEET"),
                Art("scout-3", "SURVIVAL SKILL"), Art("scout-4", "SIXTH SENSE")),
            Class("sniper", "SNIPER", "SNIPER",
                new[] { 30, 10, 20, 15, 10, 10, 10 }, new[] { 20, 10, 15, 15, 10, 10, 10 },
                Art("sniper-1", "SHARP SHOOTER"), Art("sniper-2", "SERIOUS WOUNDS"),
                Art("sniper-3", "STONE COLD"), Art("sniper-4", "TRANQUILITY")),
            Class("medic", "MEDIC", "MEDIC",
                new[] { 10, 10, 10, 10, 20, 15, 30 }, new[] { 10, 10, 10, 10, 15, 15, 20 },
                Art("medic-1", "ASCLEPIOS"), Art("medic-2", "ANALYSE"),
                Art("medic-3", "COUNTER PLOT"), Art("medic-4", "SUMS UP")),
            Class("mechanic", "MECHANIC", "MECHANIC",
                new[] { 10, 10, 10, 20, 10, 15, 30 }, new[] { 10, 10, 10, 15, 10, 15, 20 },
                Art("mechanic-1", "ANTIDOTE"), Art("mechanic-2", "CUSTOMISE"),
                Art("mechanic-3", "EVASIVE ACTION"), Art("mechanic-4", "STUNT")),
            Class("grappler", "GRAPPLER", "GRAPPLER",
                new[] { 10, 30, 20, 15, 10, 10, 10 }, new[] { 10, 20, 15, 15, 10, 10, 10 },
                Art("grappler-1", "ALKANAN"), Art("grappler-2", "IAI-JUTSU"),
                Art("grappler-3", "THRUST & PARRY"), Art("grappler-4", "KNOCK DOWN")),
        };

        public static GundogClass FindClass(string classId) =>
            Classes.FirstOrDefault(entry => entry.ClassId == classId);

        public static GundogArt FindArt(string artId)
        {
            foreach (var entry in Classes)
            {
                var art = entry.Arts.FirstOrDefault(candidate => candidate.ArtId == artId);
                if (art != null)
                {
                    return art;
                }
            }

            return null;
        }

        private static GundogClass Class(string id, string name, string english, int[] main, int[] sub,
            params GundogArt[] arts) =>
            new GundogClass(id, name, english, main, sub, arts);

        private static GundogArt Art(string id, string name) => new GundogArt(id, name);
    }

    public sealed class GundogStat
    {
        public GundogStat(string statId, string name)
        {
            StatId = statId;
            Name = name;
        }

        public string StatId { get; }
        public string Name { get; }
    }

    public sealed class GundogArt
    {
        public GundogArt(string artId, string name)
        {
            ArtId = artId;
            Name = name;
        }

        public string ArtId { get; }
        public string Name { get; }
    }

    public sealed class GundogClass
    {
        public GundogClass(string classId, string name, string english, IReadOnlyList<int> mainModifiers,
            IReadOnlyList<int> subModifiers, IReadOnlyList<GundogArt> arts)
        {
            if (mainModifiers == null || mainModifiers.Count != Gundog.Skills.Count
                || subModifiers == null || subModifiers.Count != Gundog.Skills.Count)
            {
                throw new ValidationException("A class needs one modifier per skill family.");
            }

            ClassId = classId;
            Name = name;
            English = english;
            MainModifiers = mainModifiers.ToList();
            SubModifiers = subModifiers.ToList();
            Arts = arts.ToList();
        }

        public string ClassId { get; }
        public string Name { get; }
        public string English { get; }
        public IReadOnlyList<int> MainModifiers { get; }
        public IReadOnlyList<int> SubModifiers { get; }
        public IReadOnlyList<GundogArt> Arts { get; }
    }

    /// <summary>
    /// The Gundog sheet stored on a character. Class skill numbers are the main-class modifier plus the
    /// sub-class modifier. Older saves have a blank sheet: no class, every ability at 5.
    /// </summary>
    public sealed class GundogSheet
    {
        public GundogSheet(IReadOnlyDictionary<string, int> abilities, string mainClass, string subClass,
            IEnumerable<string> arts, IReadOnlyList<string> career, int rewardPoints, int movement, int durability,
            string rank, string language, string occupation, string era)
        {
            Abilities = CheckAbilities(abilities);
            MainClass = mainClass ?? "";
            SubClass = subClass ?? "";
            var chosen = (arts ?? Enumerable.Empty<string>()).ToList();
            if (chosen.Distinct().Count() != chosen.Count)
            {
                throw new ValidationException("A class art is selected twice.");
            }

            var hasClass = MainClass.Length > 0 || SubClass.Length > 0;
            if (!hasClass)
            {
                if (MainClass.Length != 0 || SubClass.Length != 0 || chosen.Count > 0)
                {
                    throw new ValidationException("Choose both a main class and a sub class.");
                }
            }
            else
            {
                var main = Gundog.FindClass(MainClass);
                var sub = Gundog.FindClass(SubClass);
                if (main == null || sub == null)
                {
                    throw new ValidationException("Choose both a main class and a sub class.");
                }

                foreach (var artId in chosen)
                {
                    if (main.Arts.All(art => art.ArtId != artId) && sub.Arts.All(art => art.ArtId != artId))
                    {
                        throw new ValidationException("That class art does not belong to the chosen classes.");
                    }
                }
            }

            Arts = chosen;
            if (career == null || career.Count != Gundog.CareerLines)
            {
                throw new ValidationException($"Career needs exactly {Gundog.CareerLines} lines.");
            }

            Career = career.Select(line => Validate.Text(line ?? "", "Career", Gundog.MaxLineLength, allowEmpty: true)).ToList();
            RewardPoints = Validate.Integer(rewardPoints, "Reward points", 0, Gundog.MaxReward);
            Movement = Validate.Integer(movement, "Movement", 0, Gundog.MaxMovement);
            Durability = Validate.Integer(durability, "Durability", 0, Gundog.MaxDurability);
            Rank = Validate.Text(rank ?? "", "Rank", Gundog.MaxLineLength, allowEmpty: true);
            Language = Validate.Text(language ?? "", "Language", Gundog.MaxLineLength, allowEmpty: true);
            Occupation = Validate.Text(occupation ?? "", "Occupation", Gundog.MaxLineLength, allowEmpty: true);
            Era = Validate.Text(era ?? "", "Era", Gundog.MaxLineLength, allowEmpty: true);
        }

        public IReadOnlyDictionary<string, int> Abilities { get; }
        public string MainClass { get; }
        public string SubClass { get; }
        public IReadOnlyList<string> Arts { get; }
        public IReadOnlyList<string> Career { get; }
        public int RewardPoints { get; }
        public int Movement { get; }
        public int Durability { get; }
        public string Rank { get; }
        public string Language { get; }
        public string Occupation { get; }
        public string Era { get; }

        public static GundogSheet Blank { get; } = new GundogSheet(
            Gundog.Abilities.ToDictionary(stat => stat.StatId, stat => Gundog.AbilityDefault),
            "", "", null, Enumerable.Repeat("", Gundog.CareerLines).ToList(), 0, 0, 0, "", "", "", "");

        /// <summary>Main-class modifier plus sub-class modifier, per skill family. Zero when the sheet has no class.</summary>
        public IReadOnlyDictionary<string, int> SkillModifiers()
        {
            var result = Gundog.Skills.ToDictionary(skill => skill.StatId, skill => 0);
            if (MainClass.Length == 0)
            {
                return result;
            }

            var main = Gundog.FindClass(MainClass);
            var sub = Gundog.FindClass(SubClass);
            for (var index = 0; index < Gundog.Skills.Count; index++)
            {
                result[Gundog.Skills[index].StatId] = main.MainModifiers[index] + sub.SubModifiers[index];
            }

            return result;
        }

        private static IReadOnlyDictionary<string, int> CheckAbilities(IReadOnlyDictionary<string, int> abilities)
        {
            var expected = Gundog.Abilities.Select(stat => stat.StatId);
            if (abilities == null || !new HashSet<string>(abilities.Keys).SetEquals(expected))
            {
                throw new ValidationException("Gundog abilities must be: " + string.Join(", ", expected) + ".");
            }

            return Gundog.Abilities.ToDictionary(stat => stat.StatId,
                stat => Validate.Integer(abilities[stat.StatId], stat.Name, Gundog.AbilityMin, Gundog.AbilityMax));
        }
    }
}
