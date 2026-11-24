using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Safehouse.Core;
using Safehouse.Data;

namespace Safehouse.Tests
{
    /// <summary>
    /// A medkit out on a raid. The stash is back home, so the kit has to be one the character is carrying,
    /// and using it has to free that cell - otherwise a full rig means a character bleeds out with a
    /// bandage in their pocket.
    /// </summary>
    public sealed class RaidHealTests
    {
        private const string Bandage = "ai-2-medkit";

        [Test]
        public void ACarriedKitHealsAndFreesItsCell()
        {
            var catalog = CatalogLoader.Load();
            var gear = GearLoader.Load(catalog);
            var med = gear.Meds[Bandage];
            var profile = Hurt(Bandage, out var instanceId);

            var (after, healed, cleared) = HealingRules.UseCarried(profile, instanceId, med);

            Assert.Greater(healed, 0, "it heals the worst wound it can");
            Assert.IsNotEmpty(cleared, "and clears the light bleed it was put there for");
            Assert.IsEmpty(after.Rig.Stash, "the cell comes free");
            Assert.AreEqual(profile.Health + healed, after.Health);
            Assert.IsFalse(after.Conditions.ContainsKey("thorax"), "the treated bleed is gone from the save");
        }

        [Test]
        public void AKitThatIsNotCarriedIsRefused()
        {
            var catalog = CatalogLoader.Load();
            var gear = GearLoader.Load(catalog);
            var profile = Hurt(Bandage, out var instanceId);
            var elsewhere = ItemInstance.Create(new string('c', 32), Bandage, 0, 0);

            var error = Assert.Throws<ValidationException>(
                () => HealingRules.UseCarried(profile, elsewhere.InstanceId, gear.Meds[Bandage]));

            StringAssert.Contains("not carried", error.Message);
            Assert.IsNotEmpty(profile.Rig.Stash, "nothing was used");
            Assert.IsNotNull(instanceId);
        }

        [Test]
        public void UsingAKitMidRaidWritesTheProfileAndTheLogTogether()
        {
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "safehouse-raidmed-" + System.IO.Path.GetRandomFileName());
            System.IO.Directory.CreateDirectory(folder);
            try
            {
                var catalog = CatalogLoader.Load();
                var gear = GearLoader.Load(catalog);
                var profiles = new ProfileRepository(folder, catalog);
                var accounts = new AccountRepository(folder);
                accounts.Save(Account.CreateNew());
                var character = profiles.Create("Raider");

                var hurt = Hurt(Bandage, out var instanceId, profile: profiles.Load(character.ProfileId));
                profiles.Save(hurt);

                var session = new ExpeditionSession(new ExpeditionRepository(folder), profiles, accounts,
                    MapLoader.LoadMaps(catalog), MapLoader.LoadContainers(catalog), gear,
                    CombatLoader.Load(), RaidLoader.Load(), catalog);
                var trip = session.Launch("factory", false, new[] { character }, 5_000, ExpeditionModes.Simulation);

                var (error, message) = session.UseCarriedMed(trip, character.ProfileId, instanceId);

                Assert.IsNull(error);
                StringAssert.Contains("HP healed", message);
                Assert.IsEmpty(profiles.Load(character.ProfileId).Rig.Stash, "saved: the cell is free");
                var saved = new ExpeditionRepository(folder).Load(trip.ExpeditionId);
                StringAssert.Contains("Used", saved.Log.Last());

                var again = session.UseCarriedMed(trip, character.ProfileId, instanceId);
                Assert.IsNotNull(again.Error, "one use per kit");
            }
            finally
            {
                System.IO.Directory.Delete(folder, true);
            }
        }

        /// <summary>A character with one bandage in the rig, a light bleed on the thorax and a hurt leg.</summary>
        private static Profile Hurt(string itemId, out string instanceId, Profile profile = null)
        {
            profile = profile ?? Profile.CreateNew(Guid.NewGuid().ToString("N"), "Raider");
            var med = ItemInstance.Create(Guid.NewGuid().ToString("N"), itemId, 0, 0);
            instanceId = med.InstanceId;
            var parts = profile.BodyParts.ToDictionary(pair => pair.Key, pair => pair.Value);
            parts["left_leg"] = 20;
            return profile.With(
                rig: profile.Rig.With(new[] { med }),
                bodyParts: parts,
                conditions: new Dictionary<string, IReadOnlyList<string>>
                {
                    ["thorax"] = new[] { "light_bleed" },
                });
        }
    }
}
