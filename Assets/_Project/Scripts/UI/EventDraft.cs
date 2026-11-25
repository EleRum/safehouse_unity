using System;
using System.Collections.Generic;
using System.Linq;
using Safehouse.Core;

namespace Safehouse.UI
{
    /// <summary>
    /// A scene while an editor holds it: strings everywhere, converted on save or validate, so a half-typed form
    /// never has to be a half-valid event.
    /// </summary>
    internal sealed class EventDraft
    {
        public string OriginalId;
        public string EventId = "";
        public string Title = "";
        public string Text = "";
        public string Map = "";
        public string Location = "";
        public string Node = "";
        public int Act;
        public int Weight;
        public int Chance = 100;
        public int AfterExtractions;
        public bool Repeatable;
        public bool Raid = true;
        /// <summary>Null = either time of day. Collapsing this to false made every saved scene day-only.</summary>
        public bool? Night;

        /// <summary>Flags the character must already carry for this scene to be offered, comma separated.</summary>
        public string RequireFlags = "";
        public string QuestDone = "";
        public string QuestNotDone = "";

        /// <summary>Scenes that must have been played before this one.</summary>
        public readonly List<string> RequiredScenes = new List<string>();

        /// <summary>Scenes that must not have been played.</summary>
        public readonly List<string> ForbiddenScenes = new List<string>();

        /// <summary>Attribute floors, as the editor shows them.</summary>
        public readonly List<AbilityRequirement> RequiredAttributes = new List<AbilityRequirement>();

        public readonly List<ChoiceDraft> Choices = new List<ChoiceDraft>();

        public static EventDraft Fresh()
        {
            var draft = new EventDraft
            {
                EventId = "gm-scene-" + DateTime.UtcNow.Ticks.ToString("x").Substring(6),
                Title = "NEW SCENE",
                Text = "",
            };
            draft.Choices.Add(ChoiceDraft.Fresh());
            return draft;
        }

        public static EventDraft From(StoryEvent scene)
        {
            var draft = new EventDraft
            {
                OriginalId = scene.EventId,
                EventId = scene.EventId,
                Title = scene.Title,
                Text = scene.Text,
                Map = scene.Place.MapId ?? "",
                Location = scene.Place.LocationId ?? "",
                Node = scene.Place.NodeKind ?? "",
                Act = scene.Act,
                Weight = scene.Weight,
                Chance = scene.Trigger.ChancePercent,
                AfterExtractions = scene.Trigger.AfterExtractions,
                Repeatable = scene.Repeatable,
                Raid = scene.Phase == EventPhases.Raid,
                Night = scene.Trigger.Night,
                RequireFlags = string.Join(",", scene.Requires.Flags),
                QuestDone = string.Join(",", scene.Requires.QuestDone),
                QuestNotDone = string.Join(",", scene.Requires.NotDone),
            };
            draft.RequiredAttributes.AddRange(scene.Requires.Attributes);
            draft.RequiredScenes.AddRange(ScenesFrom(scene.Requires.Flags));
            draft.ForbiddenScenes.AddRange(ScenesFrom(scene.Requires.Unseen));
            foreach (var choice in scene.Choices)
            {
                draft.Choices.Add(ChoiceDraft.From(choice));
            }

            return draft;
        }

        public StoryEvent ToStoryEvent()
        {
            var choices = Choices.Select(choice => choice.ToChoice()).ToList();
            var scene = new StoryEvent(EventId, Act, Raid ? EventPhases.Raid : EventPhases.Hideout, Title, Text,
                new EventRequires(Items(QuestDone), Items(QuestNotDone),
                    SceneFlags(RequiredScenes), RequiredAttributes.ToList(), ForbiddenFlags(ForbiddenScenes)),
                Weight, choices);
            scene.WithPlace(new EventPlace(Blank(Map), Blank(Location), Blank(Node)));
            scene.WithTrigger(new EventTrigger(Night, Chance, AfterExtractions));
            scene.WithRepeatable(Repeatable);
            return scene;
        }

        private static string Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        /// <summary>"Scenes that must be played" is the seen record: one flag per scene, no special case.</summary>
        private static IReadOnlyList<string> SceneFlags(IEnumerable<string> scenes) =>
            (scenes ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(EventRules.SeenFlag)
                .ToList();

        private static IReadOnlyList<string> ForbiddenFlags(IEnumerable<string> scenes) =>
            (scenes ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(EventRules.SeenFlag)
                .ToList();

        /// <summary>Reads the seen flags back into scene ids, for the editor's lists.</summary>
        public static IEnumerable<string> ScenesFrom(IEnumerable<string> flags) =>
            (flags ?? Enumerable.Empty<string>())
                .Where(flag => flag != null && flag.StartsWith("seen:", StringComparison.Ordinal))
                .Select(flag => flag.Substring(5));

        /// <summary>Comma or space separated ids/flags, as typed in a form field.</summary>
        private static string[] Items(string csv) =>
            (csv ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(id => id.Trim())
                .Where(id => id.Length > 0)
                .ToArray();
    }


    internal sealed class ChoiceDraft
    {
        public string Label = "DO IT";
        public int CostRoubles;
        public string CostItems = "";
        public string Check = "";
        public string SuccessLog = "";
        public string SuccessItems = "";
        public int SuccessRoubles;
        public string FailureLog = "";
        public string FailureItems = "";
        public int FailureRoubles;

        /// <summary>Flags this choice leaves on the character, comma separated - what a later scene can test.</summary>
        public string Sets = "";

        /// <summary>Flags the character must already carry for this choice to be offered.</summary>
        public string RequiresFlags = "";

        /// <summary>Items that must be in the rig or backpack for this choice to be offered at all.</summary>
        public string RequiresItems = "";

        /// <summary>
        /// The outcome the scene was loaded with. The form edits the log, the roubles and the items; the trader
        /// standing, the attribute change and the quest advance are not on the page - and rebuilding the outcome
        /// from scratch wiped them the first time anyone pressed SAVE.
        /// </summary>
        public EventOutcome OriginalOutcome;
        public EventOutcome OriginalFailure;

        private static EventOutcome Merge(EventOutcome original, string log, int roubles, string[] items) =>
            original == null
                ? new EventOutcome(log, roubles, items)
                : new EventOutcome(log, roubles, items, original.Standing, original.AbilityId,
                    original.AbilityDelta, original.Advances);

        public static ChoiceDraft Fresh() => new ChoiceDraft();

        public static ChoiceDraft From(EventChoice choice) => new ChoiceDraft
        {
            Label = choice.Label,
            CostRoubles = choice.Cost.Roubles,
            CostItems = string.Join(",", choice.Cost.Items),
            Check = choice.Check ?? "",
            SuccessLog = choice.Outcome.Log,
            SuccessItems = string.Join(",", choice.Outcome.Items),
            SuccessRoubles = choice.Outcome.Roubles,
            FailureLog = choice.Failure?.Log ?? "",
            FailureItems = choice.Failure == null ? "" : string.Join(",", choice.Failure.Items),
            FailureRoubles = choice.Failure?.Roubles ?? 0,
            Sets = string.Join(",", choice.Sets),
            RequiresFlags = string.Join(",", choice.RequiresFlags),
            RequiresItems = string.Join(",", choice.RequiresItems),
            OriginalOutcome = choice.Outcome,
            OriginalFailure = choice.Failure,
        };

        public EventChoice ToChoice()
        {
            var outcome = Merge(OriginalOutcome, SuccessLog, SuccessRoubles, Items(SuccessItems));
            var failure = string.IsNullOrWhiteSpace(FailureLog) && FailureRoubles == 0
                          && string.IsNullOrWhiteSpace(FailureItems)
                ? null
                : Merge(OriginalFailure, FailureLog, FailureRoubles, Items(FailureItems));
            return new EventChoice(Label, new EventCost(CostRoubles, Items(CostItems)), outcome,
                string.IsNullOrWhiteSpace(Check) ? null : Check.Trim(), failure, Items(Sets),
                Items(RequiresFlags), Items(RequiresItems));
        }

        private static string[] Items(string csv) =>
            (csv ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(id => id.Trim())
                .Where(id => id.Length > 0)
                .ToArray();
    }

}
