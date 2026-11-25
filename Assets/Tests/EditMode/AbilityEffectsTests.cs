using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Safehouse.Core;

namespace Safehouse.Tests
{
    /// <summary>
    /// The attribute curves themselves. They are pure arithmetic, so they are tested as arithmetic: change a
    /// constant in AbilityEffects and these numbers are what has to be updated, nowhere else.
    /// </summary>
    public sealed class AbilityEffectsTests
    {
        [Test]
        public void FinesseShortensCraftsAndNeverFailsThem()
        {
            // Following Tarkov: the crafting skill only moves the clock. Ten finishes in 75 % of the listed
            // time, five is the design point, one takes 20 % longer.
            Assert.AreEqual(1.00, Speed(5), 0.0001);
            Assert.AreEqual(0.75, Speed(10), 0.0001);
            Assert.AreEqual(1.20, Speed(1), 0.0001);
        }

        [Test]
        public void TheSpeedBonusIsClampedAtBothEnds()
        {
            // The clamp only bites if someone pushes the table past its range; it must not run away.
            var profile = Profile.CreateNew(Id(), "Ana").With(abilities: Sheet(20));
            Assert.AreEqual(AbilityEffects.CraftSpeedFloor, AbilityEffects.CraftSpeed(profile), 0.0001);
        }

        [Test]
        public void OnlySteadyHandsGetAnExtraUnit()
        {
            Assert.AreEqual(0, Bonus(7), "below the threshold nothing extra drops");
            Assert.AreEqual(AbilityEffects.CraftBonusPercent, Bonus(8));
            Assert.AreEqual(AbilityEffects.CraftBonusPercent, Bonus(10));
        }

        private static double Speed(int finesse) =>
            AbilityEffects.CraftSpeed(Profile.CreateNew(Id(), "Ana").With(abilities: Sheet(finesse)));

        private static int Bonus(int finesse) =>
            AbilityEffects.CraftBonus(Profile.CreateNew(Id(), "Ana").With(abilities: Sheet(finesse)));

        private static Dictionary<string, int> Sheet(int finesse) =>
            CharacterSheet.Abilities.ToDictionary(
                ability => ability,
                ability => ability == "finesse" ? finesse : CharacterSheet.AbilityBase);

        private static int _next = 90;

        private static string Id() => (_next++).ToString("x32");
    }
}
