using System;
using System.Collections.Generic;
using System.Linq;
using Safehouse.Core;
using Safehouse.Data;
using UnityEngine;
using UnityEngine.UIElements;

namespace Safehouse.UI
{
    /// <summary>
    /// The QUESTS screen: the task chain, one card per quest, with the objective in the game's own
    /// terms, progress from the character's quest log, and a CLAIM button whose reward is exactly what
    /// <see cref="QuestRules.Claim"/> applies. The chain is data (quests.json); this screen only reads it.
    ///
    /// Progress itself is added where trips finish (see ScavengeScreenController), not here, so opening
    /// and closing this screen can never move a quest along.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class QuestsScreenController : MonoBehaviour
    {
        private static IReadOnlyList<QuestDefinition> _definitions;
        private static Dictionary<string, ItemDefinition> _catalog;

        private VisualElement _root;
        private ScrollView _list;
        private Label _title;
        private Label _trader;
        private Label _text;
        private Label _objective;
        private Label _progress;
        private Label _reward;
        private Label _status;
        private Button _claim;
        private string _selectedId;

        private static IReadOnlyList<QuestDefinition> Definitions =>
            _definitions ?? (_definitions = QuestBindings.LoadQuests());

        private static Dictionary<string, ItemDefinition> Catalog =>
            _catalog ?? (_catalog = CatalogLoader.Load());

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            _root.style.alignItems = Align.Center;
            _root.style.justifyContent = Justify.Center;

            _list = _root.Q<ScrollView>("quest-list");
            _title = _root.Q<Label>("quest-title");
            _trader = _root.Q<Label>("quest-trader");
            _text = _root.Q<Label>("quest-text");
            _objective = _root.Q<Label>("quest-objective");
            _progress = _root.Q<Label>("quest-progress");
            _reward = _root.Q<Label>("quest-reward");
            _status = _root.Q<Label>("quest-status");
            _claim = _root.Q<Button>("quest-claim");
            _claim.clicked += OnClaimClicked;

            _root.Q<Button>("navtab-character").clicked += () => ScreenNavigator.Go("character");
            _root.Q<Button>("navtab-gear").clicked += () => ScreenNavigator.Go("gear");
            _root.Q<Button>("navtab-hideout").clicked += () => ScreenNavigator.Go("hideout");
            _root.Q<Button>("navtab-traders").clicked += () => ScreenNavigator.Go("traders");
            _root.Q<Button>("navtab-scavenge").clicked += () => ScreenNavigator.Go("scavenge");
            var gmTab = _root.Q<Button>("navtab-gm");
            if (gmTab != null)
            {
                gmTab.clicked += () => ScreenNavigator.Go("gm");
            }
            _root.Q<Button>("navtab-settings").clicked += () => ScreenNavigator.Go("settings");

            ScreenNavigator.Register("quests", Show, Hide);
            Hide();
        }

        private void OnDisable() => ScreenNavigator.Unregister("quests");

        private void Show()
        {
            _root.style.display = DisplayStyle.Flex;
            Refresh();
        }

        private void Hide() => _root.style.display = DisplayStyle.None;

        private CharacterSession Session => GearScreenController.Current?.Session;

        private QuestLog LoadLog()
        {
            var session = Session;
            return session == null || !session.Saves
                ? QuestLog.Empty
                : new QuestRepository(session.Repository.DataFolder).Load(session.Profile.ProfileId);
        }

        private void Refresh()
        {
            _list.Clear();
            var session = Session;
            if (session == null || !session.Saves)
            {
                _status.text = "Open a saved character first.";
                _claim.SetEnabled(false);
                return;
            }

            var log = LoadLog();
            var visible = QuestRules.Visible(Definitions, log, Session?.Profile?.Flags, Session?.Profile);
            if (visible.Count == 0)
            {
                _status.text = "No tasks are open yet.";
            }

            foreach (var quest in visible)
            {
                _list.Add(Card(quest, log));
            }

            var selected = visible.FirstOrDefault(quest => quest.QuestId == _selectedId)
                           ?? visible.FirstOrDefault();
            _selectedId = selected?.QuestId;
            Draw(selected, log);
        }

        private VisualElement Card(QuestDefinition quest, QuestLog log)
        {
            var status = QuestRules.StatusOf(quest, log, Session?.Profile?.Flags, Session?.Profile);
            var card = new Button(() =>
            {
                _selectedId = quest.QuestId;
                Refresh();
            });
            card.AddToClassList("card");
            card.style.flexDirection = FlexDirection.Column;
            card.style.alignItems = Align.FlexStart;
            card.style.paddingLeft = 10;
            card.style.paddingRight = 10;
            card.style.paddingTop = 8;
            card.style.paddingBottom = 8;
            card.style.marginBottom = 6;
            if (quest.QuestId == _selectedId)
            {
                card.AddToClassList("cell-selected");
            }

            var head = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            var title = new Label(quest.Title) { style = { flexGrow = 1, fontSize = 13 } };
            title.AddToClassList(status == QuestStatus.Claimed ? "text-muted" : "text-accent");
            head.Add(title);
            var badge = new Label(Badge(status)) { style = { fontSize = 10 } };
            badge.AddToClassList(StatusClass(status));
            head.Add(badge);
            card.Add(head);

            var detail = new Label(quest.Objective.Describe(Catalog) + "   "
                + Math.Min(quest.Objective.Count, log.ProgressOf(quest.QuestId)) + " / " + quest.Objective.Count)
            {
                style = { fontSize = 11, marginTop = 3, whiteSpace = WhiteSpace.Normal },
            };
            detail.AddToClassList("text-muted");
            card.Add(detail);

            // What finishing it pays, on the card itself: the player should not have to open a task to find out
            // whether it is worth the trip. Locked tasks never reach this code - they are filtered out before the
            // list is built - so nothing here is a spoiler for a scene that has not been earned yet.
            var reward = new Label("REWARD  " + quest.Reward.Describe(Catalog))
            {
                style = { fontSize = 11, marginTop = 2, whiteSpace = WhiteSpace.Normal },
            };
            reward.AddToClassList("text-accent");
            card.Add(reward);
            return card;
        }

        private static string Badge(string status)
        {
            switch (status)
            {
                case QuestStatus.Done: return "READY";
                case QuestStatus.Claimed: return "DONE";
                default: return "OPEN";
            }
        }

        private static string StatusClass(string status)
        {
            switch (status)
            {
                case QuestStatus.Done: return "text-good";
                case QuestStatus.Claimed: return "text-muted";
                default: return "text-muted";
            }
        }

        private void Draw(QuestDefinition quest, QuestLog log)
        {
            if (quest == null)
            {
                _title.text = "Select a task";
                _trader.text = "";
                _text.text = "";
                _objective.text = "";
                _progress.text = "";
                _reward.text = "";
                _claim.SetEnabled(false);
                return;
            }

            var status = QuestRules.StatusOf(quest, log, Session?.Profile?.Flags, Session?.Profile);
            _title.text = quest.Title.ToUpperInvariant();
            _trader.text = quest.TraderId.ToUpperInvariant();
            _text.text = quest.Text;
            _objective.text = quest.Objective.Describe(Catalog);
            _progress.text = Math.Min(quest.Objective.Count, log.ProgressOf(quest.QuestId))
                + " / " + quest.Objective.Count;
            _reward.text = quest.Reward.Describe(Catalog);
            _claim.SetEnabled(status == QuestStatus.Done);
            _claim.text = status == QuestStatus.Claimed ? "CLAIMED"
                : status == QuestStatus.Done ? "CLAIM"
                : "NOT FINISHED";
            _status.text = status == QuestStatus.Done
                ? "Ready to hand in."
                : status == QuestStatus.Claimed ? "Reward already taken."
                : "";
        }

        private void OnClaimClicked()
        {
            var session = Session;
            var quest = Definitions.FirstOrDefault(entry => entry.QuestId == _selectedId);
            if (session == null || !session.Saves || quest == null)
            {
                return;
            }

            var repository = new QuestRepository(session.Repository.DataFolder);
            var log = repository.Load(session.Profile.ProfileId);
            Profile paid;
            QuestLog after;
            try
            {
                (paid, after) = QuestRules.Claim(session.Profile, quest, log, Catalog);
                session.Commit(paid);
            }
            catch (Exception error)
            {
                _status.text = error.Message;
                return;
            }

            try
            {
                repository.Save(session.Profile.ProfileId, after);
            }
            catch (StorageException error)
            {
                _status.text = "Reward paid, but the quest log was not saved: " + error.Message;
                return;
            }

            _status.text = "Claimed " + quest.Title + ".";
            _selectedId = null;
            Refresh();
        }
    }
}
