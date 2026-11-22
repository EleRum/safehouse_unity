using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Safehouse.Core;
using Safehouse.Data;

namespace Safehouse.Tests
{
    /// <summary>The quest chain: gating, progress from a finished trip, claiming, and the log file.</summary>
    public sealed class QuestRulesTests
    {
        private static Dictionary<string, ItemDefinition> _catalog;

        private static Dictionary<string, ItemDefinition> Catalog =>
            _catalog ?? (_catalog = CatalogLoader.Load());

        private static IEnumerable<QuestDefinition> Chain() => QuestLoader.Load();

        private static QuestDefinition Quest(string questId) =>
            Chain().Single(quest => quest.QuestId == questId);

        private Profile Saved(string name = "Ana") => Profile.CreateNew(
            Guid.NewGuid().ToString("N"), name);

        private static Expedition Trip(string mapId, string outcome, IEnumerable<string> loot = null)
        {
            var brought = new Dictionary<string, IReadOnlyList<string>>
            {
                ["x"] = (loot ?? Enumerable.Empty<string>()).ToList(),
            };

            return new Expedition(
                Guid.NewGuid().ToString("N"), mapId, night: false, party: new[] { "x" },
                startedAt: 1000, zoneId: "outer", zoneStartedAt: 1000, status: ExpeditionStatus.Completed,
                outcome: outcome, loot: brought, discarded: new List<string>(), log: new List<string>(),
                rngState: null, mode: ExpeditionModes.Direct, route: null);
        }

        private static Expedition TripFor(string profileId, string mapId, string outcome,
            IEnumerable<string> loot = null) =>
            Trip(mapId, outcome, loot);

        [Test]
        public void TheChainLoadsAndEveryQuestIsReferencedBySomething()
        {
            var chain = Chain().ToList();
            Assert.Greater(chain.Count, 0);
            Assert.AreEqual(chain.Count, chain.Select(quest => quest.QuestId).Distinct().Count());
        }

        [Test]
        public void AnUnfinishedRequirementLocksTheNextQuest()
        {
            var log = QuestLog.Empty;
            Assert.AreEqual(QuestStatus.Open, QuestRules.StatusOf(Quest("prapor-1-first-blood"), log));
            Assert.AreEqual(QuestStatus.Locked, QuestRules.StatusOf(Quest("prapor-2-bandages"), log));
        }

        [Test]
        public void ExtractingFromTheRightMapFinishesAnExtractQuest()
        {
            var quest = Quest("prapor-1-first-blood");
            var trip = Trip("customs", "extracted", new[] { "army-bandage" });

            var log = QuestRules.Advance(QuestLog.Empty, new[] { quest }, trip, "x", Catalog);

            Assert.AreEqual(QuestStatus.Done, QuestRules.StatusOf(quest, log));
        }

        [Test]
        public void TheWrongMapDoesNotCount()
        {
            var quest = Quest("prapor-1-first-blood");
            var trip = Trip("woods", "extracted");

            var log = QuestRules.Advance(QuestLog.Empty, new[] { quest }, trip, "x", Catalog);

            Assert.AreEqual(QuestStatus.Open, QuestRules.StatusOf(quest, log));
            Assert.AreEqual(0, log.ProgressOf(quest.QuestId));
        }

        [Test]
        public void DyingDoesNotCountEvenWithLoot()
        {
            var quest = Quest("prapor-1-first-blood");
            var trip = Trip("customs", "wiped", new[] { "army-bandage" });

            var log = QuestRules.Advance(QuestLog.Empty, new[] { quest }, trip, "x", Catalog);

            Assert.AreEqual(0, log.ProgressOf(quest.QuestId));
        }

        [Test]
        public void LootProgressCountsTheItemsBroughtBackAndClampsAtTheTarget()
        {
            var quest = Quest("prapor-4-ammo-run");
            var log = QuestLog.Empty
                .With("prapor-1-first-blood", new QuestEntry(1, true))
                .With("prapor-2-bandages", new QuestEntry(4, true))
                .With("prapor-3-factory-run", new QuestEntry(1, true));

            var trip = Trip("customs", "extracted",
                new[] { "545x39mm-bt-gs", "545x39mm-bt-gs", "545x39mm-bt-gs", "army-bandage" });
            log = QuestRules.Advance(log, new[] { quest }, trip, "x", Catalog);

            Assert.AreEqual(quest.Objective.Count, log.ProgressOf(quest.QuestId));
            Assert.AreEqual(QuestStatus.Done, QuestRules.StatusOf(quest, log));
        }

        [Test]
        public void AClaimedQuestPaysAndUnlocksTheNextOne()
        {
            var profile = Saved();
            var first = Quest("prapor-1-first-blood");
            var log = QuestRules.Advance(QuestLog.Empty, new[] { first },
                Trip("customs", "extracted"), "x", Catalog);

            var (paid, after) = QuestRules.Claim(profile, first, log, Catalog);

            Assert.AreEqual(profile.Money + first.Reward.Roubles, paid.Money);
            Assert.AreEqual(QuestStatus.Claimed, QuestRules.StatusOf(first, after));
            Assert.AreEqual(QuestStatus.Open, QuestRules.StatusOf(Quest("prapor-2-bandages"), after));
        }

        [Test]
        public void ClaimingTwiceIsRefused()
        {
            var profile = Saved();
            var first = Quest("prapor-1-first-blood");
            var log = QuestRules.Advance(QuestLog.Empty, new[] { first },
                Trip("customs", "extracted"), "x", Catalog);
            var (paid, after) = QuestRules.Claim(profile, first, log, Catalog);

            Assert.Throws<ValidationException>(() => QuestRules.Claim(paid, first, after, Catalog));
        }

        [Test]
        public void ADeliveryQuestWantsTheItemsInTheStash()
        {
            var profile = Saved();
            var deliver = Quest("prapor-2-bandages");
            var log = QuestLog.Empty
                .With("prapor-1-first-blood", new QuestEntry(1, true));

            Assert.AreEqual(QuestStatus.Done, QuestRules.StatusOf(deliver, log));
            StringAssert.Contains("there are 0", QuestRules.ClaimError(profile, deliver, log, Catalog));

            var stocked = profile.With(stash: InventoryRules.AddItems(profile.Stash, Catalog, "army-bandage", 4));
            Assert.IsNull(QuestRules.ClaimError(stocked, deliver, log, Catalog));

            var (paid, after) = QuestRules.Claim(stocked, deliver, log, Catalog);
            Assert.AreEqual(0, paid.Stash.Stash.Count(instance => instance.ItemId == "army-bandage"),
                "the handed-over items leave the stash");
            Assert.AreEqual(QuestStatus.Claimed, QuestRules.StatusOf(deliver, after));
        }

        [Test]
        public void TheRewardItemLandsInTheStash()
        {
            var profile = Saved();
            var deliver = Quest("prapor-2-bandages");
            var log = QuestLog.Empty.With("prapor-1-first-blood", new QuestEntry(1, true));
            var stocked = profile.With(stash: InventoryRules.AddItems(profile.Stash, Catalog, "army-bandage", 4));

            var (paid, _) = QuestRules.Claim(stocked, deliver, log, Catalog);

            Assert.AreEqual(1, deliver.Reward.Items.Count);
            Assert.AreEqual(deliver.Reward.Items[0].Count,
                paid.Stash.Stash.Count(instance => instance.ItemId == deliver.Reward.Items[0].ItemId));
        }

        [Test]
        public void StandingGoesToTheTraderTheQuestBelongsTo()
        {
            var profile = Saved();
            var withTraders = profile.With(traders: new[]
            {
                new TraderState("prapor", 0, 0, Array.Empty<OfferPurchase>()),
                new TraderState("therapist", 0, 0, Array.Empty<OfferPurchase>()),
            });
            var first = Quest("prapor-1-first-blood");
            var log = QuestRules.Advance(QuestLog.Empty, new[] { first },
                Trip("customs", "extracted"), "x", Catalog);

            var (paid, _) = QuestRules.Claim(withTraders, first, log, Catalog);

            Assert.AreEqual(first.Reward.Standing,
                paid.Traders.Single(state => state.TraderId == "prapor").Standing);
            Assert.AreEqual(0, paid.Traders.Single(state => state.TraderId == "therapist").Standing);
        }

        [Test]
        public void TheLogRoundTripsThroughItsFile()
        {
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "safehouse-quests-" + Guid.NewGuid().ToString("N"));
            try
            {
                var repository = new QuestRepository(folder);
                var log = new QuestLog(new[]
                {
                    new KeyValuePair<string, QuestEntry>("prapor-1-first-blood", new QuestEntry(1, true)),
                    new KeyValuePair<string, QuestEntry>("prapor-2-bandages", new QuestEntry(2, false)),
                });

                repository.Save("abc", log);
                var back = repository.Load("abc");

                Assert.AreEqual(1, back.ProgressOf("prapor-1-first-blood"));
                Assert.IsTrue(back.Claimed("prapor-1-first-blood"));
                Assert.AreEqual(2, back.ProgressOf("prapor-2-bandages"));
                Assert.IsFalse(back.Claimed("prapor-2-bandages"));
            }
            finally
            {
                if (System.IO.Directory.Exists(folder))
                {
                    System.IO.Directory.Delete(folder, recursive: true);
                }
            }
        }

        [Test]
        public void ACharacterWithNoFileStartsWithAnEmptyLog()
        {
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "safehouse-quests-" + Guid.NewGuid().ToString("N"));
            try
            {
                Assert.AreEqual(0, new QuestRepository(folder).Load("nobody").Entries.Count);
            }
            finally
            {
                if (System.IO.Directory.Exists(folder))
                {
                    System.IO.Directory.Delete(folder, recursive: true);
                }
            }
        }

        [Test]
        public void ADamagedLogIsMovedAsideInsteadOfBlockingPlay()
        {
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "safehouse-quests-" + Guid.NewGuid().ToString("N"));
            try
            {
                var repository = new QuestRepository(folder);
                repository.Save("abc", QuestLog.Empty);
                System.IO.File.WriteAllText(repository.PathFor("abc"), "{ not json");

                var notices = new List<string>();
                var log = repository.Load("abc", notices);

                Assert.AreEqual(0, log.Entries.Count);
                Assert.AreEqual(1, notices.Count);
            }
            finally
            {
                if (System.IO.Directory.Exists(folder))
                {
                    System.IO.Directory.Delete(folder, recursive: true);
                }
            }
        }
    }
}
