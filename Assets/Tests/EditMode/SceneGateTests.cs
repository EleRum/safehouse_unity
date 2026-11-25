using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Safehouse.Core;

namespace Safehouse.Tests
{
    /// <summary>
    /// The gates a GM sets in the editor, each one proven to change the outcome. Every field in a scene's form is
    /// read by something here: a field that is saved and ignored is worse than a field that is missing, because it
    /// looks like it works.
    /// </summary>
    public sealed class SceneGateTests
    {
        private static StoryEvent Scene(string id, int chance = 100, int weight = 10, bool repeatable = true,
            string requiresQuest = null, string notDone = null) =>
            new StoryEvent(id, 1, EventPhases.Raid, id, "text",
                    new EventRequires(
                        questDone: requiresQuest == null ? null : new[] { requiresQuest },
                        notDone: notDone == null ? null : new[] { notDone }),
                    weight,
                    new[]
                    {
                        new EventChoice("GO", new EventCost(0, new string[0]),
                            new EventOutcome("done", 0, new string[0])),
                    })
                .WithTrigger(new EventTrigger(false, chance, 0))
                .WithRepeatable(repeatable);

        private static EventBook Book(params StoryEvent[] scenes) => new EventBook(scenes);

        private static List<StoryEvent> Roll(EventBook book, int times, int seed = 7)
        {
            var random = PythonRandom.Seed(seed);
            var picked = new List<StoryEvent>();
            for (var i = 0; i < times; i++)
            {
                var scene = EventRules.NextRandom(book, QuestLog.Empty, EventPhases.Raid, random);
                if (scene != null)
                {
                    picked.Add(scene);
                }
            }

            return picked;
        }

        [Test]
        public void ASceneAtZeroPercentIsNeverDrawn()
        {
            var book = Book(Scene("never", chance: 0));

            Assert.IsEmpty(Roll(book, 200));
        }

        [Test]
        public void ASceneAtFivePercentIsRare()
        {
            var book = Book(Scene("rare", chance: 5));
            var drawn = Roll(book, 2000);

            Assert.Greater(drawn.Count, 0, "at 5% in 2000 draws it has to appear at least once");
            Assert.Less(drawn.Count, 400, "at 5% it must not appear anything like one time in five");
        }

        [Test]
        public void WeightDecidesWhichOfTwoIsMoreOften()
        {
            var book = Book(Scene("common", weight: 30), Scene("rare", weight: 1));
            var drawn = Roll(book, 500);
            var common = drawn.Count(scene => scene.EventId == "common");
            var rare = drawn.Count(scene => scene.EventId == "rare");

            Assert.Greater(common, rare * 4, "30 against 1 has to come up far more often");
            Assert.Greater(rare, 0, "and the rare one still has to be reachable");
        }

        [Test]
        public void ASceneWaitingOnAQuestIsNotOfferedUntilThatQuestIsDone()
        {
            var book = Book(Scene("after", requiresQuest: "mil1"));
            var log = QuestLog.Empty;

            Assert.IsEmpty(EventRules.Available(book, log, EventPhases.Raid));
            var claimed = log.With("mil1", log.Get("mil1").With(1, true));
            Assert.AreEqual(1, EventRules.Available(book, claimed, EventPhases.Raid).Count);
        }

        [Test]
        public void ASceneThatMustNotFollowAnotherIsWithheldOnceItHas()
        {
            var book = Book(Scene("intro", notDone: "mil1"));
            var log = QuestLog.Empty;

            Assert.AreEqual(1, EventRules.Available(book, log, EventPhases.Raid).Count);
            var claimed = log.With("mil1", log.Get("mil1").With(1, true));
            Assert.IsEmpty(EventRules.Available(book, claimed, EventPhases.Raid));
        }

        [Test]
        public void AfterTripsWithholdsASceneUntilThePlayerHasBeenOutEnough()
        {
            var scene = Scene("later").WithTrigger(new EventTrigger(false, 100, 3));
            var book = Book(scene);

            Assert.IsEmpty(EventRules.Available(book, QuestLog.Empty, EventPhases.Raid, extractions: 2));
            Assert.AreEqual(1, EventRules.Available(book, QuestLog.Empty, EventPhases.Raid, extractions: 3).Count);
        }

        [Test]
        public void NightOnlyIsWithheldInDaylight()
        {
            var scene = Scene("night").WithTrigger(new EventTrigger(true, 100, 0));
            var book = Book(scene);

            Assert.IsEmpty(EventRules.Available(book, QuestLog.Empty, EventPhases.Raid, night: false));
            Assert.AreEqual(1, EventRules.Available(book, QuestLog.Empty, EventPhases.Raid, night: true).Count);
        }

        [Test]
        public void AnAttributeFloorWithholdsASceneFromAWeakCharacter()
        {
            var scene = new StoryEvent("strong", 1, EventPhases.Raid, "strong", "text",
                new EventRequires(attributes: new[] { new AbilityRequirement("muscle", 8) }), 10,
                new[]
                {
                    new EventChoice("GO", new EventCost(0, new string[0]),
                        new EventOutcome("done", 0, new string[0])),
                });
            var book = Book(scene);
            var weak = new Dictionary<string, int> { ["muscle"] = 4 };
            var strong = new Dictionary<string, int> { ["muscle"] = 9 };

            Assert.IsEmpty(EventRules.Available(book, QuestLog.Empty, EventPhases.Raid, abilities: weak));
            Assert.AreEqual(1,
                EventRules.Available(book, QuestLog.Empty, EventPhases.Raid, abilities: strong).Count);
        }

        [Test]
        public void AChoiceThatNeedsAnItemIsNotOfferedWithoutIt()
        {
            var choice = new EventChoice("USE THE TANK", new EventCost(0, new string[0]),
                new EventOutcome("done", 0, new string[0]), requiresItems: new[] { "metal-fuel-tank" });

            Assert.IsFalse(EventRules.ChoiceOffered(choice, new string[0], _ => false));
            Assert.IsTrue(EventRules.ChoiceOffered(choice, new string[0], id => id == "metal-fuel-tank"));
        }

        [Test]
        public void AChoiceThatNeedsAFlagIsNotOfferedWithoutIt()
        {
            var choice = new EventChoice("SAY THE NUMBERS", new EventCost(0, new string[0]),
                new EventOutcome("done", 0, new string[0]), requiresFlags: new[] { "mil4-case" });

            Assert.IsFalse(EventRules.ChoiceOffered(choice, new string[0], _ => true));
            Assert.IsTrue(EventRules.ChoiceOffered(choice, new[] { "mil4-case" }, _ => true));
        }
    }
}
