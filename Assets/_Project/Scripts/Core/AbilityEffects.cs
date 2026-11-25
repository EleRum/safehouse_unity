using System;

namespace Safehouse.Core
{
    /// <summary>
    /// What an attribute actually does. Every number in here is one of the eight on the sheet (schema 10) turned
    /// into a multiplier or a percentage, so the curve lives in exactly one file and the formulas from the design
    /// note can be tuned without hunting through the rules.
    ///
    /// The convention: <c>step = value - 5</c>, five is the balance point, one to ten the range.
    /// </summary>
    public static class AbilityEffects
    {
        /// <summary>Percent of a craft's time removed per point of finesse above five (added below it).</summary>
        public const int CraftSpeedPerFinesse = 5;

        /// <summary>A craft never drops below this fraction of its listed time, however nimble the hands.</summary>
        public const double CraftSpeedFloor = 0.55;

        public const double CraftSpeedCeiling = 1.35;

        /// <summary>From this finesse on, a finished craft can yield one extra unit.</summary>
        public const int CraftBonusFinesse = 8;

        public const int CraftBonusPercent = 15;

        /// <summary>How far this attribute sits from the balance point (never null: unknown means five).</summary>
        public static int Step(Profile profile, string ability) =>
            (profile.Abilities.TryGetValue(ability, out var value) ? value : CharacterSheet.AbilityBase)
            - CharacterSheet.AbilityBase;

        /// <summary>
        /// Multiplier on a craft's duration, following the Tarkov model rather than a dice roll: finesse never
        /// makes a craft fail, it makes it finish sooner. Ten finishes in 75 % of the listed time, one takes 120 %.
        /// The Bitcoin farm is left alone (production applies this only to ordinary recipes), which is also what
        /// the Crafting skill in Tarkov does.
        /// </summary>
        public static double CraftSpeed(Profile profile)
        {
            var factor = 1.0 - CraftSpeedPerFinesse * Step(profile, "finesse") / 100.0;
            return Math.Max(CraftSpeedFloor, Math.Min(CraftSpeedCeiling, factor));
        }

        /// <summary>Chance, in percent, of one extra unit out of a finished craft; zero below the threshold.</summary>
        public static int CraftBonus(Profile profile) =>
            (profile.Abilities.TryGetValue("finesse", out var finesse) ? finesse : CharacterSheet.AbilityBase)
            >= CraftBonusFinesse
                ? CraftBonusPercent
                : 0;
    }
}
