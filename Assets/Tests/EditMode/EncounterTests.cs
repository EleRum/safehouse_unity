using System.Linq;
using NUnit.Framework;
using Safehouse.Core;
using Safehouse.Data;

namespace Safehouse.Tests
{
    /// <summary>
    /// The interactive firefight: an enemy with hit points, rounds the player answers, and the three ways a
    /// fight can end. These tests stay in Core - no scene, no save files - because that is where the rules are.
    /// </summary>
    public sealed class EncounterTests
    {
        private static readonly System.Collections.Generic.Dictionary<string, ItemDefinition> Catalog = CatalogLoader.Load();
        private static readonly GearData Gear = GearLoader.Load(Catalog);
        private static readonly CombatNumbers Numbers = CombatLoader.Load();

        [Test]
        public void AnAttackTakesHealthOffTheEnemy()
        {
            var encounter = ScavFight();
            var fighter = Fighter();
            var random = PythonRandom.Seed(7);

            var total = 0;
            for (var round = 0; round < 3 && encounter.Alive; round++)
            {
                var result = EncounterRules.Attack(encounter, fighter, Aim.Body, random, Numbers);
                total += encounter.Health - result.Encounter.Health;
                encounter = result.Encounter;
                fighter = result.Fighter;
            }

            Assert.Greater(total, 0, "three aimed bursts should hurt a scav");
            Assert.AreEqual(3, encounter.Rounds);
        }

        [Test]
        public void TheEnemyGoesDownWhenItsHealthRunsOut()
        {
            var encounter = ScavFight();
            var fighter = Fighter();
            var random = PythonRandom.Seed(3);
            var outcome = EncounterOutcomeKind.Ongoing;

            for (var round = 0; round < 40 && outcome == EncounterOutcomeKind.Ongoing; round++)
            {
                var result = EncounterRules.Attack(encounter, fighter, Aim.Body, random, Numbers);
                encounter = result.Encounter;
                fighter = result.Fighter;
                outcome = result.Outcome;
            }

            Assert.AreEqual(EncounterOutcomeKind.Won, outcome);
            Assert.AreEqual(0, encounter.Health);
        }

        [Test]
        public void CoveringCostsTheRoundButNotTheShot()
        {
            // Cover deals nothing: the enemy's health is untouched, and the round was still spent.
            var encounter = ScavFight();
            var result = EncounterRules.Crawl(encounter, Fighter(), PythonRandom.Seed(11), Numbers);

            Assert.AreEqual(encounter.Health, result.Encounter.Health);
            Assert.AreEqual(1, result.Encounter.Rounds);
            Assert.Less(result.Encounter.Distance, encounter.Distance, "crawling closes the distance");
            Assert.IsTrue(result.Log[0].Contains("crawl"));
        }

        [Test]
        public void AnyBodyPartCanBeHitWithoutThrowing()
        {
            // The fracture table only carries the four limbs, so a stomach hit must not ask it about a fracture:
            // that lookup is what broke a live fight with KeyNotFoundException 'stomach'.
            var hits = 0;
            for (var seed = 1; seed <= 25; seed++)
            {
                var encounter = ScavFight();
                var fighter = Fighter();
                var random = PythonRandom.Seed(seed);
                for (var round = 0; round < 15 && encounter.Alive && fighter.Standing; round++)
                {
                    var result = EncounterRules.Attack(encounter, fighter, Aim.Body, random, Numbers);
                    encounter = result.Encounter;
                    fighter = result.Fighter;
                    hits += result.Log.Count;
                }
            }

            Assert.Greater(hits, 0);
        }

        [Test]
        public void CrawlingStopsAtTheClosestDistance()
        {
            var encounter = ScavFight();
            var random = PythonRandom.Seed(2);
            for (var round = 0; round < 10 && encounter.Distance > Encounter.MinDistance; round++)
            {
                encounter = EncounterRules.Crawl(encounter, Fighter(), random, Numbers).Encounter;
            }

            Assert.AreEqual(Encounter.MinDistance, encounter.Distance);
            Assert.AreEqual("close", encounter.Range);
        }

        [Test]
        public void DistancePicksTheRangeBand()
        {
            Assert.AreEqual("close", Encounter.RangeName(5));
            Assert.AreEqual("close", Encounter.RangeName(Encounter.CloseMetres));
            Assert.AreEqual("medium", Encounter.RangeName(Encounter.CloseMetres + 1));
            Assert.AreEqual("medium", Encounter.RangeName(Encounter.MediumMetres));
            Assert.AreEqual("long", Encounter.RangeName(Encounter.MediumMetres + 1));
        }

        [Test]
        public void HealingCostsTheRoundAndTheEnemyStillShoots()
        {
            // What a bandage mid-firefight gives up: the enemy's burst lands while the character is busy,
            // and nothing happens to the enemy's health.
            var encounter = ScavFight();
            var fighter = Fighter();
            var result = EncounterRules.EnemyTurn(encounter, fighter, PythonRandom.Seed(13), Numbers);

            Assert.AreEqual(encounter.Health, result.Encounter.Health, "the enemy was not shot at");
            Assert.AreEqual(1, result.Encounter.Rounds, "the round was spent");
            Assert.IsNotEmpty(result.Log, "and the burst was described");
        }

        [Test]
        public void FleeingWithHighAgilityCanBreakContact()
        {
            var broken = false;
            for (var seed = 1; seed <= 40 && !broken; seed++)
            {
                var result = EncounterRules.Flee(ScavFight(), Fighter(), PythonRandom.Seed(seed), Numbers,
                    agility: 10);
                broken = result.Outcome == EncounterOutcomeKind.Fled;
            }

            Assert.IsTrue(broken, "75 % for forty seeds in a row cannot all fail");
        }

        [Test]
        public void AFightThatKeepsGoingEventuallyLoses()
        {
            // Standing in the open against a boss for round after round ends with the character down, and the
            // outcome says so - that is what turns into a wipe in the trip.
            var boss = new Encounter("Black Div.", 790, 790, 6, 65, 70, 1100, true, "patrol", "n1", -1);
            var fighter = Fighter();
            var random = PythonRandom.Seed(5);
            var outcome = EncounterOutcomeKind.Ongoing;

            for (var round = 0; round < 60 && outcome == EncounterOutcomeKind.Ongoing; round++)
            {
                var result = EncounterRules.Attack(boss, fighter, Aim.Legs, random, Numbers);
                boss = result.Encounter;
                fighter = result.Fighter;
                outcome = result.Outcome;
            }

            Assert.AreEqual(EncounterOutcomeKind.Lost, outcome);
            Assert.IsFalse(fighter.Standing);
        }

        [Test]
        public void AnEncounterSurvivesTheSaveFile()
        {
            var trip = ExpeditionRules.Create("customs", false, new[] { Id() }, 1_000, ExpeditionModes.Simulation,
                null, PythonRandom.Seed(1));
            var withFight = trip.With(status: ExpeditionStatus.InEncounter,
                encounter: new Encounter("Scav", 180, 300, 2, 20, 50, 550, false, "choice", "n3", 1, 4));

            var back = ExpeditionSerializer.FromJson(ExpeditionSerializer.ToJson(withFight));

            Assert.IsNotNull(back.Encounter);
            Assert.AreEqual("Scav", back.Encounter.EnemyName);
            Assert.AreEqual(180, back.Encounter.Health);
            Assert.AreEqual(300, back.Encounter.HealthMax);
            Assert.AreEqual(1, back.Encounter.ChoiceIndex);
            Assert.AreEqual(4, back.Encounter.Rounds);
            Assert.AreEqual(ExpeditionStatus.InEncounter, back.Status);
        }

        private static Encounter ScavFight() => Encounter.From(Enemy.Scav(Numbers), "choice", "n2", 0);

        private static Fighter Fighter() =>
            ExpeditionRules.BuildFighter(Profile.CreateNew(Id(), "Ana"), Gear);

        private static int _next = 1;

        private static string Id() => (_next++).ToString("x32");
    }
}
