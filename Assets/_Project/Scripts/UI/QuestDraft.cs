using System;
using System.Collections.Generic;
using System.Linq;
using Safehouse.Core;

namespace Safehouse.UI
{
    /// <summary>
    /// A quest while the editor holds it: everything the player reads and everything it pays, as strings and
    /// numbers, turned into a <see cref="QuestDefinition"/> only on save.
    /// </summary>
    internal sealed class QuestDraft
    {
        public string OriginalId;
        public string QuestId = "";
        public string TraderId = "prapor";
        public string Title = "";
        public string Text = "";
        public int Roubles;
        public int Standing;
        public string Items = "";

        /// <summary>The scene that finishes this quest: the objective's item id, and the whole point of the page.</summary>
        public string SceneId = "";

        public static QuestDraft Fresh(string sceneId)
        {
            var stamp = Guid.NewGuid().ToString("N").Substring(0, 6);
            return new QuestDraft
            {
                QuestId = "gm-quest-" + stamp,
                Title = "NEW QUEST",
                Text = "",
                SceneId = sceneId,
            };
        }

        public static QuestDraft From(QuestDefinition quest) => new QuestDraft
        {
            OriginalId = quest.QuestId,
            QuestId = quest.QuestId,
            TraderId = quest.TraderId,
            Title = quest.Title,
            Text = quest.Text ?? "",
            Roubles = quest.Reward.Roubles,
            Standing = quest.Reward.Standing,
            Items = string.Join(",", quest.Reward.Items.Select(item => item.ItemId)),
            SceneId = quest.Objective.ItemId ?? "",
        };

        public QuestDefinition ToQuest()
        {
            var objective = new QuestObjective(QuestObjectiveKinds.Event, 1, itemId: Blank(SceneId));
            var items = (Items ?? "")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(id => id.Trim())
                .Where(id => id.Length > 0)
                .Select(id => new QuestRewardItem(id, 1))
                .ToList();
            return new QuestDefinition(QuestId, TraderId, Title, Text, new string[0], objective,
                new QuestReward(Roubles, Standing, items));
        }

        private static string Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
