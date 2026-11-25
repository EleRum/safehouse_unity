using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Safehouse.Core;

namespace Safehouse.Tests
{
    /// <summary>
    /// The seen record: one flag per resolved scene, written by the scene itself. It decides whether the scene can
    /// be offered again and whether an event quest counts as finished - two things that used to be guessed from
    /// progress counters and could disagree.
    /// </summary>
    public sealed class SeenRecordTests
    {
        private static StoryEvent Scene(string id, bool repeatable, string flag = null) =>
            new StoryEvent(id, 1, EventPhases.Raid, id, "text",
                    new EventRequires(flags: flag == null ? null : new[] { flag }),
                    10,
                    new[]
                    {
                        new EventChoice("GO", new EventCost(0, new string[0]),
                            new EventOutcome("done", 0, new string[0])),
                    })
                .WithRepeatable(repeatable);

        private static EventBook Book(params StoryEvent[] scenes) => new EventBook(scenes);

        private static QuestDefinition EventQuest(string questId, string eventId) =>
            new QuestDefinition(questId, "prapor", "Test quest", "text",
                new string[0],
                new QuestObjective(QuestObjectiveKinds.Event, 1, itemId: eventId),
                new QuestReward(0));

        private static Profile Fresh() => Profile.CreateNew(Guid.NewGuid().ToString("N"), "Ana");

        [Test]
        public void ResolvingASceneRecordsThatItWasSeen()
        {
            var scene = Scene("seen-me", repeatable: false);
            var (profile, _, _) = EventRules.Apply(scene, 0, Fresh(),
                new Dictionary<string, ItemDefinition>());

            CollectionAssert.Contains(profile.Flags.ToList(), EventRules.SeenFlag("seen-me"));
        }

        [Test]
        public void AFailedCheckStillRecordsTheScene()
        {
            // A scene whose choice needs 20 on a d20 with no bonus: it fails, and the failure branch pays nothing.
            var scene = new StoryEvent("hard", 1, EventPhases.Raid, "hard", "text", new EventRequires(), 10,
                new[]
                {
                    new EventChoice("TRY", new EventCost(0, new string[0]),
                        new EventOutcome("won", 0, new string[0]), "d20 >= 20",
                        new EventOutcome("lost", 0, new string[0])),
                });
            var context = new ExpressionContext(new Dictionary<string, int>(), 0, 1);
            var (profile, line, _) = EventRules.Apply(scene, 0, Fresh(), new Dictionary<string, ItemDefinition>(),
                context);

            Assert.AreEqual("lost", line);
            CollectionAssert.Contains(profile.Flags.ToList(), EventRules.SeenFlag("hard"));
        }

        [Test]
        public void ASeenSceneIsNotOfferedAgainUnlessRepeatable()
        {
            var book = Book(Scene("once", repeatable: false), Scene("always", repeatable: true, flag: "unlock"));
            var log = QuestLog.Empty;
            var flags = new List<string> { EventRules.SeenFlag("once"), EventRules.SeenFlag("always"), "unlock" };

            var offered = EventRules.Available(book, log, EventPhases.Raid, flags: flags)
                .Select(scene => scene.EventId)
                .ToList();

            CollectionAssert.DoesNotContain("once", offered);
            CollectionAssert.Contains("always", offered);
        }

        [Test]
        public void AnEventQuestIsDoneWhenItsSceneHasBeenSeen()
        {
            var quest = EventQuest("q1", "mil1-stop");
            var log = QuestLog.Empty;

            Assert.AreEqual(QuestStatus.Open, QuestRules.StatusOf(quest, log, new string[0]));
            Assert.AreEqual(QuestStatus.Done,
                QuestRules.StatusOf(quest, log, new[] { EventRules.SeenFlag("mil1-stop") }));
        }

        [Test]
        public void ASeenFlagDoesNotCompleteADifferentQuest()
        {
            var quest = EventQuest("q2", "mil2-diesel");

            Assert.AreEqual(QuestStatus.Open,
                QuestRules.StatusOf(quest, QuestLog.Empty, new[] { EventRules.SeenFlag("mil1-stop") }));
        }

        [Test]
        public void AnEventQuestCannotBeAdvancedByATrip()
        {
            // Trips advance extract and loot quests only: an event quest's progress is the record, nothing else.
            var quest = EventQuest("q3", "mil1-stop");
            var advanced = QuestRules.Advance(QuestLog.Empty, quest, 1);

            Assert.AreEqual(0, advanced.Get("q3").Progress);
        }
    }
}
