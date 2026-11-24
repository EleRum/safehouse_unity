using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Safehouse.Core;
using Safehouse.Data;

namespace Safehouse.Tests
{
    /// <summary>
    /// The bin. Two things have to hold: taking an item out of a grid leaves every other item exactly
    /// where it was, and a find destroyed on an expedition floor never reaches the trip's discarded
    /// list - that list is "what was left behind", which the carry-limit note and the log read.
    /// </summary>
    public sealed class TrashRulesTests
    {
        // Instance ids are bare uuid4 hex everywhere else in the project; these are the shortest legal ones.
        private static readonly string First = new string('a', 32);
        private static readonly string Second = new string('b', 32);
        private static readonly string Third = new string('c', 32);

        [Test]
        public void WithoutTakesOutOnlyThatInstance()
        {
            var grid = new StashGrid(4, 2, new[]
            {
                ItemInstance.Create(First, "matches", 0, 0),
                ItemInstance.Create(Second, "matches", 1, 0),
            });

            var trimmed = TrashRules.Without(grid, First);

            Assert.AreEqual(1, trimmed.Stash.Count);
            Assert.AreEqual(Second, trimmed.Stash[0].InstanceId, "the twin stays");
            Assert.AreEqual(1, trimmed.Stash[0].X, "and stays in its own cell");
            Assert.AreEqual(4, trimmed.StashWidth);
            Assert.AreEqual(2, trimmed.StashHeight);
        }

        [Test]
        public void WithoutOnSomethingItDoesNotHoldChangesNothing()
        {
            var grid = new StashGrid(4, 2, new[] { ItemInstance.Create(First, "matches", 0, 0) });

            Assert.AreSame(grid, TrashRules.Without(grid, Third), "no match comes back as the same grid");
        }

        [Test]
        public void WithoutFindTakesOneOfTwoIdenticalFinds()
        {
            var loot = new Dictionary<string, List<string>>
            {
                ["p1"] = new List<string> { "matches", "matches" },
                ["p2"] = new List<string> { "matches" },
            };

            var trimmed = TrashRules.WithoutFind(loot, "p1", "matches");

            Assert.IsNotNull(trimmed);
            Assert.AreEqual(1, trimmed["p1"].Count, "one of the two goes");
            Assert.AreEqual(1, trimmed["p2"].Count, "the other character's floor is not touched");
            Assert.AreEqual(2, loot["p1"].Count, "the input is left alone");
        }

        [Test]
        public void WithoutFindDropsAnEmptiedFloorAndRefusesWhatIsNotThere()
        {
            var loot = new Dictionary<string, List<string>> { ["p1"] = new List<string> { "matches" } };

            Assert.IsFalse(TrashRules.WithoutFind(loot, "p1", "matches").ContainsKey("p1"),
                "an empty list would still draw an empty floor row");
            Assert.IsNull(TrashRules.WithoutFind(loot, "p1", "bandage"), "not on the floor");
            Assert.IsNull(TrashRules.WithoutFind(loot, "p2", "matches"), "not that character's floor");
        }

        [Test]
        public void TheRaidBinAlsoTakesWhatIsCarried()
        {
            var folder = Path.Combine(Path.GetTempPath(), "safehouse-bin-" + Path.GetRandomFileName());
            Directory.CreateDirectory(folder);
            try
            {
                var catalog = CatalogLoader.Load();
                var profiles = new ProfileRepository(folder, catalog);
                var accounts = new AccountRepository(folder);
                accounts.Save(Account.CreateNew());
                var character = profiles.Create("Raider");

                // Something in the rig: the grids are what fills up out there, and what a raid bin has to empty.
                var small = catalog.Values.First(item => item.Width == 1 && item.Height == 1);
                var carried = ItemInstance.Create(System.Guid.NewGuid().ToString("N"), small.ItemId, 0, 0);
                var loaded = profiles.Load(character.ProfileId);
                profiles.Save(loaded.With(rig: loaded.Rig.With(new[] { carried })));

                var session = new ExpeditionSession(new ExpeditionRepository(folder), profiles, accounts,
                    MapLoader.LoadMaps(catalog), MapLoader.LoadContainers(catalog), GearLoader.Load(catalog),
                    CombatLoader.Load(), RaidLoader.Load(), catalog);
                var trip = session.Launch("factory", false, new[] { character }, 5_000, ExpeditionModes.Simulation);

                Assert.IsNull(session.DestroyCarried(trip, character.ProfileId, carried.InstanceId));

                Assert.IsEmpty(profiles.Load(character.ProfileId).Rig.Stash, "the cell comes free");
                var saved = new ExpeditionRepository(folder).Load(trip.ExpeditionId);
                CollectionAssert.IsEmpty(saved.Discarded, "thrown away is not left behind");
                StringAssert.StartsWith("Threw away", saved.Log.Last());
                StringAssert.Contains("not carried",
                    session.DestroyCarried(trip, character.ProfileId, carried.InstanceId), "it is gone already");
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Test]
        public void DestroyingAFloorFindIsNotRecordedAsLeftBehind()
        {
            var folder = Path.Combine(Path.GetTempPath(), "safehouse-trash-" + Path.GetRandomFileName());
            Directory.CreateDirectory(folder);
            try
            {
                var catalog = CatalogLoader.Load();
                var profiles = new ProfileRepository(folder, catalog);
                var accounts = new AccountRepository(folder);
                accounts.Save(Account.CreateNew());
                var character = profiles.Create("Raider");
                var session = new ExpeditionSession(new ExpeditionRepository(folder), profiles, accounts,
                    MapLoader.LoadMaps(catalog), MapLoader.LoadContainers(catalog), GearLoader.Load(catalog),
                    CombatLoader.Load(), RaidLoader.Load(), catalog);
                var launched = session.Launch("factory", false, new[] { character }, 5_000, ExpeditionModes.Simulation);
                var itemId = catalog.Keys.First();
                var withLoot = launched.With(loot: new Dictionary<string, List<string>>
                {
                    [character.ProfileId] = new List<string> { itemId, itemId },
                });

                var afterOne = session.DestroyLoot(withLoot, character.ProfileId, itemId);

                Assert.AreEqual(1, afterOne.Loot[character.ProfileId].Count, "two finds, one thrown away");
                CollectionAssert.IsEmpty(afterOne.Discarded, "a destroyed find was not left behind");
                StringAssert.StartsWith("Threw away", afterOne.Log.Last());

                var afterBoth = session.DestroyLoot(afterOne, character.ProfileId, itemId);

                Assert.IsFalse(afterBoth.Loot.ContainsKey(character.ProfileId), "the emptied floor is not listed");
                var saved = new ExpeditionRepository(folder).Load(launched.ExpeditionId);
                CollectionAssert.IsEmpty(saved.Discarded, "and that is what reached the file");
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }
    }
}
