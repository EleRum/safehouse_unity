using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Safehouse.Core;
using Safehouse.Data;

namespace Safehouse.Tests
{
    /// <summary>
    /// Death. Head or thorax at zero is not a knockdown: it is decided where being downed is decided
    /// (<see cref="ExpeditionRules.SyncProfiles"/>), it takes whatever the character was carrying in
    /// their rig and backpack, and the trip's log says so once.
    /// </summary>
    public sealed class DeathTests
    {
        private static Dictionary<string, ItemDefinition> Catalog => CatalogLoader.Load();
        private static GearData Gear => GearLoader.Load(Catalog);

        [Test]
        public void NoHeadKillsAndTakesWhatWasBeingCarried()
        {
            var profile = Carrying();
            var fighter = Fighter(profile);
            fighter.BodyParts["head"] = 0;

            var after = ExpeditionRules.SyncProfiles(Party(profile), Fighters(profile, fighter))[profile.ProfileId];

            Assert.AreEqual(CharacterSheet.Dead, after.Status);
            Assert.IsEmpty(after.Rig.Stash, "what the rig held is lost");
            Assert.IsEmpty(after.Backpack.Stash, "and the backpack too");
            Assert.IsNotEmpty(after.Stash.Stash, "the stash at home is not what death touches");
            Assert.AreEqual(0, after.BodyParts["head"]);
        }

        [Test]
        public void NoThoraxKillsToo()
        {
            var profile = Carrying();
            var fighter = Fighter(profile);
            fighter.BodyParts["thorax"] = 0;

            var after = ExpeditionRules.SyncProfiles(Party(profile), Fighters(profile, fighter))[profile.ProfileId];

            Assert.AreEqual(CharacterSheet.Dead, after.Status);
            Assert.IsEmpty(after.Rig.Stash);
        }

        [Test]
        public void ALostLimbIsNotDeathAndKeepsTheLoot()
        {
            var profile = Carrying();
            var fighter = Fighter(profile);
            fighter.BodyParts["left_leg"] = 0;
            fighter.BodyParts["right_arm"] = 0;

            var after = ExpeditionRules.SyncProfiles(Party(profile), Fighters(profile, fighter))[profile.ProfileId];

            Assert.AreEqual(CharacterSheet.Active, after.Status, "head and thorax intact: still on their feet");
            Assert.IsNotEmpty(after.Rig.Stash);
            Assert.IsNotEmpty(after.Backpack.Stash);
        }

        [Test]
        public void TheTripLogRecordsADeathOnce()
        {
            var profile = Carrying();
            var before = Party(profile);
            var fighter = Fighter(profile);
            fighter.BodyParts["head"] = 0;
            var changed = ExpeditionRules.SyncProfiles(before, Fighters(profile, fighter));

            var once = ExpeditionRules.NoteDeaths(Trip(profile), before, changed);
            StringAssert.Contains(profile.DisplayName + " was killed in action", once.Log.Last());

            var again = ExpeditionRules.NoteDeaths(once, changed, changed);
            Assert.AreEqual(once.Log.Count, again.Log.Count, "the same death is not written twice");
        }

        [Test]
        public void OnlyTheStandingBringTheirLootOut()
        {
            var profile = Profile.CreateNew(NewId(), "Ana");

            Assert.IsTrue(ExpeditionRules.OnTheirFeet(profile), "active");
            Assert.IsTrue(ExpeditionRules.OnTheirFeet(profile.With(status: CharacterSheet.OnExpedition)), "away");
            Assert.IsFalse(ExpeditionRules.OnTheirFeet(profile.With(status: CharacterSheet.Downed)), "carried out");
            Assert.IsFalse(ExpeditionRules.OnTheirFeet(profile.With(status: CharacterSheet.Dead)), "left behind");
        }

        /// <summary>A healthy character with one item in the rig and one in the backpack.</summary>
        private static Profile Carrying()
        {
            var catalog = Catalog;
            var small = catalog.Values.First(item => item.Width == 1 && item.Height == 1);
            var profile = Profile.CreateNew(NewId(), "Ana");
            return profile.With(
                rig: profile.Rig.With(new[] { ItemInstance.Create(NewId(), small.ItemId, 0, 0) }),
                backpack: profile.Backpack.With(new[] { ItemInstance.Create(NewId(), small.ItemId, 0, 0) }));
        }

        private static Fighter Fighter(Profile profile) => ExpeditionRules.BuildFighter(profile, Gear);

        private static Dictionary<string, Profile> Party(Profile profile) =>
            new Dictionary<string, Profile> { [profile.ProfileId] = profile };

        private static Dictionary<string, Fighter> Fighters(Profile profile, Fighter fighter) =>
            new Dictionary<string, Fighter> { [profile.ProfileId] = fighter };

        private static Expedition Trip(Profile profile) =>
            ExpeditionRules.Create("customs", false, new[] { profile.ProfileId }, 100, ExpeditionModes.Simulation,
                null, PythonRandom.Seed(1));

        private static string NewId() => Guid.NewGuid().ToString("N");
    }
}
