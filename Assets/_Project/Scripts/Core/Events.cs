using System;
using System.Collections.Generic;
using System.Linq;

namespace Safehouse.Core
{
    /// <summary>When an event can happen: in the hideout, or out on a trip.</summary>
    public static class EventPhases
    {
        public const string Hideout = "hideout";
        public const string Raid = "raid";

        public static readonly IReadOnlyList<string> All = new[] { Hideout, Raid };
    }

    /// <summary>What an event costs to pick: roubles and/or items out of the stash.</summary>
    public sealed class EventCost
    {
        public EventCost(int roubles = 0, IReadOnlyList<string> items = null)
        {
            Roubles = Validate.Integer(roubles, "Event cost roubles", 0, CoreLimits.MaxMoney);
            Items = (items ?? new string[0]).ToList();
        }

        public int Roubles { get; }
        public IReadOnlyList<string> Items { get; }
    }

    /// <summary>
    /// What picking a choice does. Every field is optional, so a choice that only says something is legal:
    /// a log line, money, items, standing, one point of one attribute, and the quest this event finishes.
    /// </summary>
    public sealed class EventOutcome
    {
        public EventOutcome(string log, int roubles = 0, IReadOnlyList<string> items = null,
            IReadOnlyDictionary<string, int> standing = null, string abilityId = null, int abilityDelta = 0,
            string advances = null)
        {
            Log = log ?? "";
            Roubles = Validate.Integer(roubles, "Event reward roubles", 0, CoreLimits.MaxMoney);
            Items = (items ?? new string[0]).ToList();
            Standing = (standing ?? new Dictionary<string, int>())
                .ToDictionary(pair => pair.Key, pair => (int)pair.Value);
            AbilityId = string.IsNullOrWhiteSpace(abilityId) ? null : abilityId;
            AbilityDelta = abilityDelta;
            Advances = string.IsNullOrWhiteSpace(advances) ? null : advances;
        }

        public string Log { get; }
        public int Roubles { get; }
        public IReadOnlyList<string> Items { get; }
        public IReadOnlyDictionary<string, int> Standing { get; }
        public string AbilityId { get; }
        public int AbilityDelta { get; }
        public string Advances { get; }
    }

    public sealed class EventChoice
    {
        public EventChoice(string label, EventCost cost, EventOutcome outcome, string check = null,
            EventOutcome failure = null, IReadOnlyList<string> sets = null,
            IReadOnlyList<string> requiresFlags = null, IReadOnlyList<string> requiresItems = null)
        {
            Label = Validate.Text(label, "Choice label", 120);
            Cost = cost ?? new EventCost();
            Outcome = outcome ?? throw new ValidationException("A choice needs an outcome.");
            Check = string.IsNullOrWhiteSpace(check) ? null : check;
            Sets = (sets ?? new string[0]).Where(flag => !string.IsNullOrWhiteSpace(flag)).Select(f => f.Trim()).ToList();
            RequiresFlags = (requiresFlags ?? new string[0]).Where(flag => !string.IsNullOrWhiteSpace(flag)).Select(f => f.Trim()).ToList();
            RequiresItems = (requiresItems ?? new string[0]).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).ToList();
            Failure = failure;
            if (Check != null)
            {
                // Compiled at load, so a typo in a GM's expression fails when the file is read, not mid-raid.
                Compiled = EventExpression.Compile(Check);
            }
        }

        public string Label { get; }
        public EventCost Cost { get; }

        /// <summary>What happens when there is no check, or when the check passes.</summary>
        public EventOutcome Outcome { get; }

        /// <summary>A condition, in the event expression language; null means "always the outcome".</summary>
        public string Check { get; }

        /// <summary>What happens when the check fails. Falls back to <see cref="Outcome"/> when null.</summary>
        public EventOutcome Failure { get; }

        /// <summary>Flags this choice leaves on the character: the ones a later scene can test.</summary>
        public IReadOnlyList<string> Sets { get; }

        /// <summary>Flags the character must already carry for this choice to be offered at all.</summary>
        public IReadOnlyList<string> RequiresFlags { get; }

        /// <summary>
        /// Items that must be in the rig or backpack for this choice to be offered. Distinct from the cost: a cost
        /// is paid when you pick it, a requirement is only looked at.
        /// </summary>
        public IReadOnlyList<string> RequiresItems { get; }

        public Func<ExpressionContext, int> Compiled { get; }
    }

    /// <summary>Which quests this event waits for, and which quests mean it has already been played.</summary>
    /// <summary>One attribute floor: "sense at least 6".</summary>
    public sealed class AbilityRequirement
    {
        public AbilityRequirement(string abilityId, int min)
        {
            if (!CharacterSheet.Abilities.Contains(abilityId))
            {
                throw new ValidationException("Unknown ability: " + abilityId + ".");
            }

            AbilityId = abilityId;
            Min = Validate.Integer(min, "Minimum " + abilityId, CharacterSheet.AbilityMin,
                CharacterSheet.AbilityMax);
        }

        public string AbilityId { get; }
        public int Min { get; }
    }

    public sealed class EventRequires
    {
        public EventRequires(IReadOnlyList<string> questDone = null, IReadOnlyList<string> notDone = null,
            IReadOnlyList<string> flags = null, IReadOnlyList<AbilityRequirement> attributes = null,
            IReadOnlyList<string> unseen = null)
        {
            QuestDone = (questDone ?? new string[0]).ToList();
            NotDone = (notDone ?? new string[0]).ToList();
            Flags = (flags ?? new string[0]).ToList();
            Attributes = (attributes ?? new List<AbilityRequirement>()).ToList();
            Unseen = (unseen ?? new string[0]).ToList();
        }

        /// <summary>Flags the character must NOT carry: how a scene says "not this time, we already did that".</summary>
        public IReadOnlyList<string> Unseen { get; }

        /// <summary>Every one of these flags must be on the character - how a scene depends on an earlier choice.</summary>
        public IReadOnlyList<string> Flags { get; }

        /// <summary>The attributes the character must have, each at or above its minimum.</summary>
        public IReadOnlyList<AbilityRequirement> Attributes { get; }

        /// <summary>Every one of these quests has been claimed.</summary>
        public IReadOnlyList<string> QuestDone { get; }

        /// <summary>None of these quests has been claimed yet - which is how an event knows it is still pending.</summary>
        public IReadOnlyList<string> NotDone { get; }
    }

    /// <summary>
    /// One authored event: a wall of text, and the choices that follow it. This is the unit the whole scenario
    /// is written in - the loader reads it out of <c>data/events.json</c>, so writing a scene is editing that
    /// file rather than writing code.
    /// </summary>
    public sealed class StoryEvent
    {
        public StoryEvent(string eventId, int act, string phase, string title, string text, EventRequires requires,
            int weight, IReadOnlyList<EventChoice> choices)
        {
            EventId = Validate.Identifier(eventId, "event_id");
            Act = Validate.Integer(act, "act", 0, 99);
            if (!EventPhases.All.Contains(phase))
            {
                throw new ValidationException($"Unknown event phase: '{phase}'.");
            }

            Phase = phase;
            Title = Validate.Text(title, "Event title", 120);
            Text = Validate.Text(text, "Event text", 4000);
            Requires = requires ?? new EventRequires();
            Weight = Validate.Integer(weight, "weight", 0, 10_000);
            Choices = (choices ?? new EventChoice[0]).ToList();
            if (Choices.Count == 0)
            {
                throw new ValidationException($"Event '{eventId}' has no choices.");
            }
        }

        public string EventId { get; }
        public int Act { get; }

        /// <summary>"hideout" or "raid".</summary>
        public string Phase { get; }

        public string Title { get; }
        public string Text { get; }
        public EventRequires Requires { get; }

        /// <summary>Zero for a main-line event (it plays in act order); above zero for a random one.</summary>
        public int Weight { get; }

        public IReadOnlyList<EventChoice> Choices { get; }

        public bool MainLine => Weight == 0;

        /// <summary>Where it can happen. Empty means anywhere on a trip of the right phase.</summary>
        public EventPlace Place { get; private set; } = new EventPlace();

        /// <summary>Extra gates a GM sets: night only, how rare it is, how many trips it waits for.</summary>
        public EventTrigger Trigger { get; private set; } = new EventTrigger();

        /// <summary>True when the scene can happen again once it has been played.</summary>
        public bool Repeatable { get; private set; }

        public StoryEvent WithPlace(EventPlace place)
        {
            Place = place ?? new EventPlace();
            return this;
        }

        public StoryEvent WithTrigger(EventTrigger trigger)
        {
            Trigger = trigger ?? new EventTrigger();
            return this;
        }

        public StoryEvent WithRepeatable(bool repeatable)
        {
            Repeatable = repeatable;
            return this;
        }
    }

    /// <summary>
    /// Where a scene belongs. The GM narrows it down as far as they like: a map, a named location, a node kind.
    /// Everything left null means "anywhere the phase allows".
    /// </summary>
    public sealed class EventPlace
    {
        public EventPlace(string mapId = null, string locationId = null, string nodeKind = null)
        {
            MapId = string.IsNullOrWhiteSpace(mapId) ? null : mapId;
            LocationId = string.IsNullOrWhiteSpace(locationId) ? null : locationId;
            NodeKind = string.IsNullOrWhiteSpace(nodeKind) ? null : nodeKind;
        }

        public string MapId { get; }
        public string LocationId { get; }
        public string NodeKind { get; }

        public bool Matches(string mapId, string locationId, string nodeKind) =>
            (MapId == null || MapId == mapId)
            && (LocationId == null || LocationId == locationId)
            && (NodeKind == null || NodeKind == nodeKind);
    }

    /// <summary>
    /// Extra conditions a GM can set without writing an expression: the time of day, a percentage chance, and a
    /// minimum number of completed trips.
    /// </summary>
    public sealed class EventTrigger
    {
        public EventTrigger(bool? night = null, int chancePercent = 100, int afterExtractions = 0)
        {
            Night = night;
            ChancePercent = Validate.Integer(chancePercent, "chance", 0, 100);
            AfterExtractions = Validate.Integer(afterExtractions, "after_extractions", 0, 10_000);
        }

        /// <summary>Null means "either time of day".</summary>
        public bool? Night { get; }

        public int ChancePercent { get; }
        public int AfterExtractions { get; }
    }

    public sealed class EventBook
    {
        public EventBook(IEnumerable<StoryEvent> events)
        {
            Events = (events ?? new StoryEvent[0]).ToList();
            var duplicate = Events.GroupBy(e => e.EventId).FirstOrDefault(group => group.Count() > 1);
            if (duplicate != null)
            {
                throw new ValidationException($"Duplicate event id: '{duplicate.Key}'.");
            }
        }

        public IReadOnlyList<StoryEvent> Events { get; }

        public StoryEvent Find(string eventId) => Events.FirstOrDefault(e => e.EventId == eventId);
    }

    /// <summary>
    /// Which event to show, and what picking a choice does to the character. Pure: the caller saves the profile
    /// it gets back. Story progress is carried by the quest log, so an event that has been played is one whose
    /// quest has been claimed - no extra save format for events.
    public static class EventRules
    {
        /// <summary>
        /// Whether the choice is on the table at all: its flags and its items are all present. A choice that is
        /// not offered is not a failure - it does not appear, which is the whole point of a branch.
        /// </summary>
        public static bool ChoiceOffered(EventChoice choice, IReadOnlyCollection<string> flags,
            Func<string, bool> carried) => choice == null
            || (choice.RequiresFlags.All(flag => flags != null && flags.Contains(flag))
                && choice.RequiresItems.All(item => carried != null && carried(item)));

        /// <summary>What a choice costs, or null when the character can pay it.</summary>
        public static string CostError(EventChoice choice, Profile profile)
        {
            if (profile == null)
            {
                return "Open a character first.";
            }

            if (profile.Money < choice.Cost.Roubles)
            {
                return $"Needs {TradingRules.Roubles(choice.Cost.Roubles)}.";
            }

            foreach (var itemId in choice.Cost.Items)
            {
                if (!profile.Stash.Stash.Any(item => item.ItemId == itemId))
                {
                    return $"Needs {itemId.Replace('-', ' ')} in the stash.";
                }
            }

            return null;
        }

        /// <summary>
        /// Events whose requirements the log satisfies, in act order. "The previous scene is done" means its
        /// quest has been claimed **or already finished** - the story should not stop dead because the player
        /// has not walked back to the QUESTS screen to collect the money.
        ///
        /// <paramref name="where"/> narrows by place (map / location / node kind), <paramref name="night"/> by
        /// time of day and <paramref name="extractions"/> by how many trips the character has finished - the
        /// three gates a GM sets without writing an expression.
        /// </summary>
        /// <summary>
        /// The flag a resolved scene leaves behind. One convention for the whole game: the seen record, the
        /// repeat rule and the event quests all read this one id, so they can never disagree.
        /// </summary>
        public static string SeenFlag(string eventId) => "seen:" + eventId;

        private static bool Seen(IReadOnlyCollection<string> flags, string eventId) =>
            flags != null && flags.Contains(SeenFlag(eventId));

        public static IReadOnlyList<StoryEvent> Available(EventBook book, QuestLog log, string phase,
            Func<string, string, string, bool> where = null, bool? night = null, int? extractions = null,
            IReadOnlyCollection<string> flags = null,
            IReadOnlyDictionary<string, int> abilities = null) =>
            book.Events
                .Where(e => e.Phase == phase)
                // A quest counts as behind you when it is claimed, has progress, or - for the event quests - when
                // the scene it names has been played. Without the third case an event quest is unclaimable *and*
                // unfinished, so anything gated on it would wait forever.
                .Where(e => e.Requires.NotDone.All(id => !log.Claimed(id) && log.ProgressOf(id) == 0))
                .Where(e => e.Requires.QuestDone.All(id =>
                    log.Claimed(id) || log.ProgressOf(id) >= 1 || Seen(flags, id)))
                // Played once is played, unless the GM made it repeatable: any progress on the quest that this
                // event finishes means the scene has already happened.
                .Where(e => e.Repeatable
                            || e.Requires.NotDone.All(id => !log.Claimed(id) && log.ProgressOf(id) == 0))
                .Where(e => where == null || where(e.Place.MapId, e.Place.LocationId, e.Place.NodeKind))
                .Where(e => night == null || e.Trigger.Night == null || e.Trigger.Night == night)
                .Where(e => extractions == null || e.Trigger.AfterExtractions <= extractions)
                .Where(e => flags == null || e.Requires.Flags.All(flag => flags.Contains(flag)))
                .Where(e => flags == null || e.Requires.Unseen.All(flag => !flags.Contains(flag)))
                .Where(e => abilities == null || e.Requires.Attributes.All(need =>
                    abilities.TryGetValue(need.AbilityId, out var value) && value >= need.Min))
                // Played once is played: the seen record says so, unless the GM marked the scene repeatable.
                .Where(e => e.Repeatable || flags == null || !flags.Contains(SeenFlag(e.EventId)))
                .OrderBy(e => e.MainLine ? 0 : 1)
                .ThenBy(e => e.Act)
                .ToList();

        /// <summary>
        /// The next main-line scene that is due, or null. Main line ignores any cooldown: when it is time, it is
        /// time, and the player has usually just finished the previous scene's quest.
        /// </summary>
        public static StoryEvent NextMainLine(EventBook book, QuestLog log, string phase,
            IReadOnlyCollection<string> flags = null, IReadOnlyDictionary<string, int> abilities = null) =>
            Available(book, log, phase, flags: flags, abilities: abilities)
                .Where(e => e.MainLine).OrderBy(e => e.Act).FirstOrDefault();

        /// <summary>A weighted draw among the random scenes that are due, or null when none are.</summary>
        public static StoryEvent NextRandom(EventBook book, QuestLog log, string phase, PythonRandom random,
            IReadOnlyCollection<string> flags = null, IReadOnlyDictionary<string, int> abilities = null)
        {
            var pool = Available(book, log, phase, flags: flags, abilities: abilities)
                .Where(e => !e.MainLine)
                // Each scene gets its own chance first. A scene at 5% is a rare thing you may walk past, not an
                // equal candidate in a hat - without this the field in the editor changed nothing at all.
                .Where(e => e.Trigger.ChancePercent >= 100 || random.RandRange(100) < e.Trigger.ChancePercent)
                .ToList();
            if (pool.Count == 0)
            {
                return null;
            }

            var total = pool.Sum(e => e.Weight);
            var roll = random.RandRange(Math.Max(1, total));
            var running = 0;
            foreach (var candidate in pool)
            {
                running += candidate.Weight;
                if (roll < running)
                {
                    return candidate;
                }
            }

            return pool[pool.Count - 1];
        }

        /// <summary>
        /// The next thing to put in front of the player: the earliest main-line event that is due, otherwise a
        /// weighted draw among the random ones. Null when nothing is due.
        /// </summary>
        public static StoryEvent Next(EventBook book, QuestLog log, string phase, PythonRandom random,
            IReadOnlyCollection<string> flags = null) =>
            NextMainLine(book, log, phase, flags) ?? NextRandom(book, log, phase, random, flags);

        /// <summary>
        /// Applies one choice: money, items, standing, an attribute point and the quest it finishes. Returns the
        /// new profile, the line to show, and the quest id that was advanced (null when the event advances none).
        /// </summary>
        public static (Profile Profile, string Log, string Advanced) Apply(StoryEvent storyEvent, int choiceIndex,
            Profile profile, IReadOnlyDictionary<string, ItemDefinition> catalog,
            ExpressionContext context = null)
        {
            if (choiceIndex < 0 || choiceIndex >= storyEvent.Choices.Count)
            {
                throw new ValidationException("Unknown choice for this event.");
            }

            var choice = storyEvent.Choices[choiceIndex];
            var error = CostError(choice, profile);
            if (error != null)
            {
                throw new ValidationException(error);
            }

            // A choice with a check rolls once: successes take the outcome, failures the other one. Without a
            // check - or without a context to evaluate it against - the outcome is simply the outcome.
            var passed = choice.Compiled == null
                         || (context != null && choice.Compiled(context) != 0);
            var outcome = passed ? choice.Outcome : (choice.Failure ?? choice.Outcome);
            var updated = profile;

            if (choice.Cost.Roubles > 0)
            {
                updated = updated.With(money: updated.Money - choice.Cost.Roubles);
            }

            foreach (var itemId in choice.Cost.Items)
            {
                updated = updated.With(stash: InventoryRules.RemoveItems(updated.Stash, catalog, itemId, 1));
            }

            if (outcome.Roubles > 0)
            {
                updated = updated.With(money: updated.Money + outcome.Roubles);
            }

            foreach (var itemId in outcome.Items)
            {
                updated = updated.With(stash: InventoryRules.AddItems(updated.Stash, catalog, itemId, 1));
            }

            foreach (var pair in outcome.Standing)
            {
                var state = TradingRules.StateOf(updated, pair.Key);
                updated = TradingRules.WithState(updated, TradingRules.WithStanding(state, state.Standing + pair.Value));
            }

            if (passed && choice.Sets.Count > 0)
            {
                updated = updated.With(flags: updated.Flags.Concat(choice.Sets).ToList());
            }

            // Being there is what counts: a failed check still means the scene happened, so the record is written
            // whatever the outcome, and the scene will not be offered again unless it is repeatable.
            updated = updated.With(flags: updated.Flags.Concat(new[] { SeenFlag(storyEvent.EventId) }).ToList());

            if (outcome.AbilityId != null && outcome.AbilityDelta != 0)
            {
                var abilities = new Dictionary<string, int>(updated.Abilities);
                if (abilities.ContainsKey(outcome.AbilityId))
                {
                    abilities[outcome.AbilityId] = Math.Max(CharacterSheet.AbilityMin,
                        Math.Min(CharacterSheet.AbilityMax, abilities[outcome.AbilityId] + outcome.AbilityDelta));
                    updated = updated.With(abilities: abilities);
                }
            }

            return (updated, outcome.Log, outcome.Advances);
        }
    }
}
