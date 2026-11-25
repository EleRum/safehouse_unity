using System;
using System.Collections.Generic;
using System.Linq;

namespace Safehouse.Core
{
    /// <summary>
    /// One enemy the party is currently facing, with enough of its numbers copied in that the fight can be
    /// resumed from a save file: a raid that is interrupted mid-firefight has to come back the same fight.
    /// The old flow resolved an encounter in one silent calculation; this is the state that lets the player
    /// answer it round by round instead.
    /// </summary>
    public sealed class Encounter
    {
        /// <summary>
        /// How much of a second of firefight one round is worth. Everything else - firepowers, swings - is
        /// the same arithmetic the automatic resolution used, so the numbers stay comparable and one
        /// constant tunes the pace of the whole thing.
        /// </summary>
        public const double RoundSeconds = 2.0;

        /// <summary>How often the enemy is behind something at the start of a round: our damage halves.</summary>
        public const int EnemyCoverPercent = 30;

        /// <summary>Incoming damage while the player is in cover.</summary>
        public const double PlayerCoverFactor = 0.35;

        /// <summary>Fleeing: base chance, plus agility, capped.</summary>
        public const int FleeBasePercent = 35;
        public const int FleeAgilityPercent = 5;
        public const int FleeMaxPercent = 75;

        /// <summary>How far a contact starts, in metres, when nothing else says otherwise.</summary>
        public const int DefaultDistance = 20;

        /// <summary>Metres covered by one round of crawling closer, and the closest a fight gets.</summary>
        public const int CrawlMetres = 8;
        public const int MinDistance = 3;

        /// <summary>Where the fight starts depending on what started it: a node's option is closer than a patrol,
        /// a boss holds the far end of the room.</summary>
        public const int ChoiceDistance = 15;
        public const int PatrolDistance = 20;
        public const int BossDistance = 30;

        /// <summary>Distance bands, matching the engagement ranges the firepower tables are keyed by.</summary>
        public const int CloseMetres = 12;
        public const int MediumMetres = 45;

        public Encounter(string enemyName, int health, int healthMax, int armorClass, int penetration,
            int damage, int fireRate, bool boss, string source, string nodeId, int choiceIndex, int rounds = 0,
            int distance = DefaultDistance)
        {
            EnemyName = Validate.Text(enemyName, "Enemy name", 80);
            Health = Validate.Integer(health, "Enemy health", 0, 10_000_000);
            HealthMax = Validate.Integer(healthMax, "Enemy health max", 1, 10_000_000);
            ArmorClass = Validate.Integer(armorClass, "Enemy armor class", 0, 10);
            Penetration = Validate.Integer(penetration, "Enemy penetration", 0, 1000);
            Damage = Validate.Integer(damage, "Enemy damage", 0, 10_000);
            FireRate = Validate.Integer(fireRate, "Enemy fire rate", 0, 10_000);
            Boss = boss;
            Source = Validate.Text(source, "Encounter source", 20);
            NodeId = nodeId;
            ChoiceIndex = choiceIndex;
            Rounds = Validate.Integer(rounds, "Encounter rounds", 0, 10_000);
            Distance = Validate.Integer(distance, "Encounter distance", MinDistance, 1000);
        }

        public string EnemyName { get; }
        public int Health { get; }
        public int HealthMax { get; }
        public int ArmorClass { get; }
        public int Penetration { get; }
        public int Damage { get; }
        public int FireRate { get; }
        public bool Boss { get; }

        /// <summary>"choice" when a node's option started it, "patrol" when walking into one did.</summary>
        public string Source { get; }

        /// <summary>The node the fight belongs to; empty for a patrol.</summary>
        public string NodeId { get; }

        /// <summary>Which option of that node to finish once the fight is over; -1 when there is none.</summary>
        public int ChoiceIndex { get; }

        public int Rounds { get; }

        /// <summary>Metres between the party and the enemy. Crawling closes it; the range band it falls in picks
        /// the firepower and hit-location tables, so getting closer changes what a round costs both ways.</summary>
        public int Distance { get; }

        /// <summary>"close", "medium" or "long" - the band this distance falls in.</summary>
        public string Range => RangeName(Distance);

        /// <summary>The band a distance falls in. Same thresholds the firepower and hit-location tables use.</summary>
        public static string RangeName(int distance) =>
            distance <= CloseMetres ? "close" : distance <= MediumMetres ? "medium" : "long";

        public bool Alive => Health > 0;

        public Encounter With(int? health = null, int? rounds = null, int? distance = null) =>
            new Encounter(EnemyName, health ?? Health, HealthMax, ArmorClass, Penetration, Damage, FireRate, Boss,
                Source, NodeId, ChoiceIndex, rounds ?? Rounds, distance ?? Distance);

        /// <summary>The same enemy as the combat numbers expect. Rebuilt from the stored numbers, not looked up,
        /// so an interrupted fight does not depend on the data files still saying the same thing.</summary>
        public Enemy ToEnemy() => new Enemy(EnemyName, HealthMax, ArmorClass, Penetration, Damage, FireRate, Boss);

        public static Encounter From(Enemy enemy, string source, string nodeId, int choiceIndex) =>
            new Encounter(enemy.Name, enemy.Health, enemy.Health, enemy.ArmorClass, enemy.Penetration, enemy.Damage,
                enemy.FireRate, enemy.Boss, source, nodeId, choiceIndex, 0,
                enemy.Boss ? BossDistance : source == "patrol" ? PatrolDistance : ChoiceDistance);
    }

    /// <summary>Where the player aimed this round: the trade is damage against the chance of landing it.</summary>
    public enum Aim
    {
        /// <summary>Centre mass: every round lands, ordinary damage.</summary>
        Body,

        /// <summary>The head: less than half the rounds land, and they hurt.</summary>
        Head,

        /// <summary>The legs: most rounds land, softer, and a hit slows the enemy's next burst.</summary>
        Legs,
    }

    /// <summary>What one round came to. "ongoing" is the ordinary case; the other three end the fight.</summary>
    public static class EncounterOutcomeKind
    {
        public const string Ongoing = "ongoing";
        public const string Won = "won";
        public const string Fled = "fled";
        public const string Lost = "lost";
    }

    /// <summary>
    /// A round of firefight the player takes part in. Every action does three things: it spends one
    /// <see cref="Encounter.RoundSeconds"/> of exchange, it changes the fighter (a new Fighter comes back,
    /// never a mutated one), and it says whether the fight is over and how.
    ///
    /// The damage arithmetic is deliberately the same functions the automatic resolution uses
    /// (<see cref="CombatRules.Firepower"/>, <see cref="CombatRules.EnemyFirepower"/>, the swing, the hit
    /// location weights, the injury rolls), so this is the same fight with a steering wheel on it.
    /// </summary>
    public static class EncounterRules
    {
        /// <summary>
        /// The parts a fracture can happen to. The injury tables only carry these four - looking up "stomach"
        /// in the fracture table throws, which is exactly the crash this set prevents.
        /// </summary>
        private static readonly HashSet<string> Limbs = new HashSet<string>
        {
            "left_arm", "right_arm", "left_leg", "right_leg",
        };

        /// <summary>Aim: chance to land, damage multiplier, and whether a hit slows them.</summary>
        public static (int HitPercent, double Damage, bool Slows) Profile(Aim aim)
        {
            switch (aim)
            {
                case Aim.Head: return (45, 2.2, false);
                case Aim.Legs: return (90, 0.85, true);
                default: return (100, 1.0, false);
            }
        }

        /// <summary>The player shoots. Returns the enemy's answer in the same round.</summary>
        public static (Encounter Encounter, Fighter Fighter, List<string> Log, string Outcome) Attack(
            Encounter encounter, Fighter fighter, Aim aim, PythonRandom random, CombatNumbers numbers)
        {
            var log = new List<string>();
            var (hitPercent, damageFactor, slows) = Profile(aim);

            // The enemy takes cover at the start of the round, so our burst either meets it or does not.
            var covered = encounter.Alive && random.RandRange(100) < Encounter.EnemyCoverPercent;
            var dealt = 0;
            if (encounter.Alive && random.RandRange(100) < hitPercent)
            {
                var ours = CombatRules.Firepower(fighter, encounter.Range, encounter.ArmorClass, numbers)
                           * Encounter.RoundSeconds;
                ours *= random.RandInt(numbers.SwingLow, numbers.SwingHigh) / (double)CombatRules.Percent;
                ours *= damageFactor;
                if (covered)
                {
                    ours *= 0.5;
                }

                dealt = Math.Max(1, (int)Math.Round(ours, MidpointRounding.ToEven));
            }

            var remaining = Math.Max(0, encounter.Health - dealt);
            log.Add(dealt > 0
                ? $"You hit the {encounter.EnemyName} for {dealt}{(covered ? " through its cover" : "")} ({remaining} left)."
                : $"You miss the {encounter.EnemyName} - it is behind cover.");

            var next = encounter.With(health: remaining, rounds: encounter.Rounds + 1);
            if (remaining <= 0)
            {
                log.Add($"The {encounter.EnemyName} is down.");
                return (next, fighter, log, EncounterOutcomeKind.Won);
            }

            // Its turn: one burst at one body part, resolved exactly as the automatic fight does it.
            var (hurt, injuryLog, slowedHit) = TakeFire(next, fighter, random, numbers,
                encounterCovered: false, slowedByLegs: false);
            log.AddRange(injuryLog);
            var outcome = hurt.Standing ? EncounterOutcomeKind.Ongoing : EncounterOutcomeKind.Lost;
            return (next, hurt, log, outcome);
        }

        /// <summary>
        /// Face down and crawling: no shot this round, a third of the burst comes back (a prone target is a poor
        /// one), and the party ends the round closer. The reason to close is the tables: a shotgun at three metres
        /// is not a shotgun at forty, and a pistol at forty is nearly useless.
        /// </summary>
        public static (Encounter Encounter, Fighter Fighter, List<string> Log, string Outcome) Crawl(
            Encounter encounter, Fighter fighter, PythonRandom random, CombatNumbers numbers)
        {
            var closer = Math.Max(Encounter.MinDistance, encounter.Distance - Encounter.CrawlMetres);
            var log = new List<string>
            {
                $"You crawl closer: {encounter.Distance} m to {closer} m ({Encounter.RangeName(closer)}).",
            };
            var next = encounter.With(rounds: encounter.Rounds + 1, distance: closer);
            var (hurt, injuryLog, _) = TakeFire(next, fighter, random, numbers,
                encounterCovered: true, slowedByLegs: false);
            log.AddRange(injuryLog);
            var outcome = hurt.Standing ? EncounterOutcomeKind.Ongoing : EncounterOutcomeKind.Lost;
            return (next, hurt, log, outcome);
        }

        /// <summary>
        /// The enemy's turn with nobody shooting back: what a round spent on a bandage costs, and what a failed
        /// attempt to break contact costs too. One burst, at the same odds and the same hit locations.
        /// </summary>
        public static (Encounter Encounter, Fighter Fighter, List<string> Log, string Outcome) EnemyTurn(
            Encounter encounter, Fighter fighter, PythonRandom random, CombatNumbers numbers)
        {
            var next = encounter.With(rounds: encounter.Rounds + 1);
            var (hurt, injuryLog, _) = TakeFire(next, fighter, random, numbers,
                encounterCovered: false, slowedByLegs: false);
            var outcome = hurt.Standing ? EncounterOutcomeKind.Ongoing : EncounterOutcomeKind.Lost;
            return (next, hurt, injuryLog, outcome);
        }

        /// <summary>
        /// Breaking contact. Agility is what gets you out; a failure costs a full burst. A clean escape ends
        /// the fight with nothing to show for the node - the loot stays where it was.
        /// </summary>
        public static (Encounter Encounter, Fighter Fighter, List<string> Log, string Outcome) Flee(
            Encounter encounter, Fighter fighter, PythonRandom random, CombatNumbers numbers, int agility)
        {
            var chance = Math.Min(Encounter.FleeMaxPercent,
                Encounter.FleeBasePercent + Encounter.FleeAgilityPercent * Math.Max(0, agility));
            var next = encounter.With(rounds: encounter.Rounds + 1);
            if (random.RandRange(100) < chance)
            {
                return (next, fighter, new List<string> { $"You break contact with the {encounter.EnemyName}." },
                    EncounterOutcomeKind.Fled);
            }

            var log = new List<string> { $"You cannot get away from the {encounter.EnemyName}." };
            var (hurt, injuryLog, _) = TakeFire(next, fighter, random, numbers,
                encounterCovered: false, slowedByLegs: false);
            log.AddRange(injuryLog);
            var outcome = hurt.Standing ? EncounterOutcomeKind.Ongoing : EncounterOutcomeKind.Lost;
            return (next, hurt, log, outcome);
        }

        /// <summary>
        /// One enemy burst at one body part of this fighter. The same rules the automatic fight uses: the
        /// part comes from the engagement range's hit-location weights, the damage is cut by that part's
        /// armour, the swing is rolled, and a wound can bring a bleed or a fracture with it.
        /// </summary>
        private static (Fighter Fighter, List<string> Log, bool Slowed) TakeFire(Encounter encounter,
            Fighter fighter, PythonRandom random, CombatNumbers numbers, bool encounterCovered, bool slowedByLegs)
        {
            var log = new List<string>();
            var enemy = encounter.ToEnemy();
            var weights = numbers.HitLocation[encounter.Range];
            var parts = CharacterSheet.BodyParts.ToList();
            var part = random.Choices(parts, parts.Select(name => (double)weights[name]).ToList());
            var armorClass = fighter.ArmorClasses.TryGetValue(part, out var worn) ? worn : 0;

            var incoming = CombatRules.EnemyFirepower(enemy, armorClass, numbers) * Encounter.RoundSeconds;
            incoming *= random.RandInt(numbers.SwingLow, numbers.SwingHigh) / (double)CombatRules.Percent;
            if (encounterCovered)
            {
                incoming *= Encounter.PlayerCoverFactor;
            }

            var dealt = Math.Max(0, (int)Math.Round(incoming, MidpointRounding.ToEven));
            var partHealth = fighter.BodyParts[part];
            var hurt = Math.Max(0, partHealth - dealt);
            var lost = partHealth - hurt;

            var bodyParts = new Dictionary<string, int>(fighter.BodyParts) { [part] = hurt };
            var conditions = fighter.Conditions.ToDictionary(pair => pair.Key, pair => (string[])pair.Value.Clone());
            var partConditions = new HashSet<string>(conditions.TryGetValue(part, out var existing)
                ? existing
                : new string[0]);
            var gained = new List<string>();
            if (lost > 0)
            {
                var injury = numbers.InjuryChances;
                if (random.RandRange(CombatRules.Percent) < injury["heavy_bleed_chance"][part]
                    && !partConditions.Contains("heavy_bleed"))
                {
                    partConditions.Remove("light_bleed");
                    partConditions.Add("heavy_bleed");
                    gained.Add("heavy_bleed");
                }
                else if (random.RandRange(CombatRules.Percent) < injury["light_bleed_chance"][part]
                         && !partConditions.Contains("light_bleed") && !partConditions.Contains("heavy_bleed"))
                {
                    partConditions.Add("light_bleed");
                    gained.Add("light_bleed");
                }

                if (Limbs.Contains(part)
                    && random.RandRange(CombatRules.Percent) < injury["fracture_chance"][part]
                    && !partConditions.Contains("fracture"))
                {
                    partConditions.Add("fracture");
                    gained.Add("fracture");
                }
            }

            if (partConditions.Count == 0)
            {
                conditions.Remove(part);
            }
            else
            {
                conditions[part] = partConditions.ToArray();
            }

            // Armour wears out where it gets hit: the vest loses life when the torso takes a round, the helmet
            // when the head does. Nothing else wears it, so a quiet run costs nothing but a bad one costs plate.
            var wear = new Dictionary<string, int>(fighter.ArmorDurability);
            if (fighter.ArmorClasses.TryGetValue(part, out var covering) && covering > 0
                && wear.TryGetValue(part, out var left) && left > 0)
            {
                var points = 2 + dealt / 6 + enemy.Penetration / 12;
                var after2 = Math.Max(0, left - Math.Max(1, points));
                wear[part] = after2;
                if (after2 <= 0)
                {
                    log.Add($"Your {(part == "head" ? "helmet" : "body armour")} is finished.");
                }
            }

            var after = new Fighter(fighter.CharacterId, fighter.Name, bodyParts, fighter.MaxBodyParts, conditions,
                fighter.Weapon, fighter.Ammo, fighter.ArmorClasses, fighter.HealPool, fighter.StopsLightBleed,
                fighter.StopsHeavyBleed, fighter.TreatsFracture, wear);

            if (lost > 0)
            {
                log.Add($"The {encounter.EnemyName} hits your {part.Replace('_', ' ')} for {lost} ({hurt} left)"
                        + (gained.Count > 0 ? " - " + string.Join(", ", gained.Select(name => name.Replace('_', ' '))) : "")
                        + ".");
            }
            else
            {
                log.Add($"The {encounter.EnemyName}'s burst misses you.");
            }

            if (!after.Standing)
            {
                log.Add(fighter.Name + " goes down.");
            }

            return (after, log, slowedByLegs);
        }
    }
}
