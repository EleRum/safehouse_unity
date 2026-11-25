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
    /// The GM's scene editor, as its own screen next to CHARACTER and the rest: the table of every event on the
    /// left, the form for the selected one on the right. It reads and writes through <see cref="EventStore"/>,
    /// which lays the GM's file over the shipped scenes - so the data stays the source of truth and this screen
    /// is only a way of typing it.
    ///
    /// The screen has no GameObject in the scene: <see cref="GmScreenHost"/> builds it at start-up from
    /// Resources/GmScreen.uxml and borrows the panel settings of whichever document is already there.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class GmScreenController : MonoBehaviour
    {
        private static readonly Dictionary<string, string> Tabs = new Dictionary<string, string>
        {
            ["navtab-character"] = "character",
            ["navtab-gear"] = "gear",
            ["navtab-hideout"] = "hideout",
            ["navtab-traders"] = "traders",
            ["navtab-scavenge"] = "scavenge",
            ["navtab-quests"] = "quests",
            ["navtab-settings"] = "settings",
        };

        private VisualElement _root;
        private VisualElement _list;
        private VisualElement _form;
        private Label _path;
        private Label _status;
        private IReadOnlyDictionary<string, ItemDefinition> _catalog;
        private EventBook _book;
        private EventDraft _draft;
        private VisualElement _pickerOverlay;
        private IReadOnlyList<QuestDefinition> _quests;
        private QuestDraft _questDraft;
        private Dictionary<string, MapDefinition> _maps;
        private RaidRules _raids;
        private List<string> _mapIds;
        private List<string> _nodeKinds;

        private bool _wired;

        private void OnEnable()
        {
            // The document is built by GmScreenHost at run time, so the tree can be one frame late: wiring twice
            // is guarded, and nothing here assumes the root exists on the first call.
            Wire();
        }

        private void Start() => Wire();

        private void Wire()
        {
            if (_wired)
            {
                return;
            }

            var document = GetComponent<UIDocument>();
            var root = document == null ? null : document.rootVisualElement;
            if (root == null || root.childCount == 0)
            {
                return;
            }

            _wired = true;
            _catalog = CatalogLoader.Load();
            // The pickers read the same data the game runs on, so a GM can only choose places that exist.
            _maps = MapLoader.LoadMaps(_catalog);
            _raids = RaidLoader.Load();
            _mapIds = _maps.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList();
            _nodeKinds = _raids.Kinds.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList();
            _quests = QuestBindings.LoadQuests();
            _root = root;
            _root.style.alignItems = Align.Center;
            _root.style.justifyContent = Justify.Center;

            _list = _root.Q<VisualElement>("gm-list");
            _form = _root.Q<VisualElement>("gm-form");
            _path = _root.Q<Label>("gm-path");
            _status = _root.Q<Label>("gm-status");

            foreach (var pair in Tabs)
            {
                var name = pair.Value;
                var button = _root.Q<Button>(pair.Key);
                if (button != null)
                {
                    button.clicked += () => ScreenNavigator.Go(name);
                }
            }

            if (_path != null)
            {
                _path.text = EventStore.OverridePath;
            }

            ScreenNavigator.Register("gm", Show, Hide);
            Hide();
        }

        private void OnDisable() => ScreenNavigator.Unregister("gm");

        private void Show()
        {
            Wire();
            if (_root == null)
            {
                return;
            }

            _root.style.display = DisplayStyle.Flex;
            _book = EventStore.Load(_catalog);
            BuildList();
        }

        private void Hide()
        {
            if (_root != null)
            {
                _root.style.display = DisplayStyle.None;
            }
        }

        private void SetStatus(string text, bool error = false)
        {
            _status.text = text;
            _status.EnableInClassList("text-danger", error);
            _status.EnableInClassList("text-muted", !error);
        }

        private void BuildList()
        {
            _list.Clear();

            var bar = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };
            bar.Add(Button("NEW", () =>
            {
                _draft = EventDraft.Fresh();
                BuildList();
                BuildForm();
            }, primary: true));
            bar.Add(Button("SAVE", Save, primary: true));
            bar.Add(Button("RELOAD", () =>
            {
                _book = EventStore.Load(_catalog);
                _draft = null;
                _form.Clear();
                BuildList();
            }));
            bar.Add(Button("DELETE GM FILE", () =>
            {
                EventStore.Clear();
                _book = EventStore.Load(_catalog);
                _draft = null;
                _form.Clear();
                SetStatus("GM file deleted; the shipped scenes are back.");
                BuildList();
            }));
            _list.Add(bar);

            var count = new Label($"{_book.Events.Count} SCENES") { };
            count.AddToClassList("text-muted");
            count.style.fontSize = 13;
            count.style.marginTop = 6;
            count.style.marginBottom = 8;
            _list.Add(count);

            foreach (var scene in _book.Events)
            {
                var id = scene.EventId;
                var button = Button($"{scene.Title}   ·   {id}", () =>
                {
                    _draft = EventDraft.From(scene);
                    BuildList();
                    BuildForm();
                });
                button.style.flexGrow = 1;
                if (_draft != null && _draft.OriginalId == id)
                {
                    // The scene on the right is the scene you pressed: say so instead of making the reader check.
                    button.RemoveFromClassList("btn-outline");
                    button.AddToClassList("btn-primary");
                    button.style.unityFontStyleAndWeight = FontStyle.Bold;
                }

                _list.Add(button);
            }
        }

        private Button Button(string text, Action action, bool primary = false)
        {
            var button = new Button(action) { text = text };
            button.AddToClassList("btn");
            button.AddToClassList(primary ? "btn-primary" : "btn-outline");
            button.AddToClassList("crafting-row-btn");
            button.style.marginRight = 6;
            button.style.marginTop = 4;
            button.style.minHeight = 34;
            button.style.fontSize = 15;
            return button;
        }

        private void BuildForm()
        {
            _form.Clear();
            if (_draft == null)
            {
                return;
            }

            _form.Add(Field("id", _draft.EventId, value => _draft.EventId = value));
            _form.Add(Field("title", _draft.Title, value => _draft.Title = value));
            _form.Add(Field("text", _draft.Text, value => _draft.Text = value, multiline: true));
            _form.Add(Row(
                OptionPicker("map", _mapIds, _draft.Map, value => { _draft.Map = value; BuildForm(); }),
                OptionPicker("location", LocationsFor(_draft.Map), _draft.Location,
                    value => { _draft.Location = value; BuildForm(); }),
                OptionPicker("node", _nodeKinds, _draft.Node, value => { _draft.Node = value; BuildForm(); })));
            _form.Add(Row(
                Field("act", _draft.Act.ToString(), value => _draft.Act = Int(value, _draft.Act)),
                Field("weight", _draft.Weight.ToString(), value => _draft.Weight = Int(value, _draft.Weight)),
                Field("chance %", _draft.Chance.ToString(), value => _draft.Chance = Int(value, _draft.Chance)),
                Field("after trips", _draft.AfterExtractions.ToString(),
                    value => _draft.AfterExtractions = Int(value, _draft.AfterExtractions))));
            _form.Add(Row(
                Toggle("repeatable", _draft.Repeatable, value => _draft.Repeatable = value),
                Toggle("raid phase", _draft.Raid, value => _draft.Raid = value),
                // Three states, because the trigger has three: any time, night only, day only. A plain toggle
                // quietly turned every saved scene into a day-only one.
                OptionPicker("time of day", new List<string> { "any", "night only", "day only" },
                    _draft.Night == null ? "any" : _draft.Night.Value ? "night only" : "day only",
                    value =>
                    {
                        _draft.Night = value == "any" ? (bool?)null : value == "night only";
                        BuildForm();
                    })));

            _form.Add(QuestBlock());
            // ---- the four condition groups, each its own block, none of them a CSV string ----
            _form.Add(SceneGate("required scenes (must have been played)", _draft.RequiredScenes));
            _form.Add(SceneGate("scenes that must not have happened", _draft.ForbiddenScenes));
            _form.Add(AttributeGate());

            for (var index = 0; index < _draft.Choices.Count; index++)
            {
                _form.Add(ChoiceBox(_draft.Choices[index], index));
            }

            var buttons = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };
            buttons.Add(Button("ADD CHOICE", () =>
            {
                _draft.Choices.Add(ChoiceDraft.Fresh());
                BuildForm();
            }));
            buttons.Add(Button("VALIDATE", () =>
            {
                try
                {
                    _draft.ToStoryEvent();
                    SetStatus("This scene is valid.");
                }
                catch (ValidationException error)
                {
                    SetStatus(error.Message, error: true);
                }
            }));
            buttons.Add(Button("SAVE", Save, primary: true));
            _form.Add(buttons);
        }

        private VisualElement ChoiceBox(ChoiceDraft choice, int index)
        {
            var box = new VisualElement();
            box.style.marginTop = 12;
            box.style.paddingTop = 8;
            box.style.borderTopWidth = 1;
            box.style.borderTopColor = new Color(0.17f, 0.2f, 0.23f);

            var head = new Label($"CHOICE {index + 1}");
            head.AddToClassList("text-label");
            box.Add(head);
            box.Add(Field("label", choice.Label, value => choice.Label = value));
            box.Add(Row(
                Field("cost roubles", choice.CostRoubles.ToString(),
                    value => choice.CostRoubles = Int(value, choice.CostRoubles)),
                ItemField("cost items", choice.CostItems, value => choice.CostItems = value)));
            box.Add(CheckFormula(choice));
            box.Add(FlagEditor("flags this choice leaves", choice, sets: true));
            box.Add(FlagEditor("flags needed to offer it", choice, sets: false));
            box.Add(ItemField("items needed to offer it", choice.RequiresItems,
                value => choice.RequiresItems = value));
            box.Add(Row(
                Field("success log", choice.SuccessLog, value => choice.SuccessLog = value),
                ItemField("success items", choice.SuccessItems, value => choice.SuccessItems = value),
                Field("roubles", choice.SuccessRoubles.ToString(),
                    value => choice.SuccessRoubles = Int(value, choice.SuccessRoubles))));
            box.Add(Field("failure log", choice.FailureLog, value => choice.FailureLog = value));
            box.Add(Row(
                ItemField("failure items", choice.FailureItems, value => choice.FailureItems = value),
                Field("failure roubles", choice.FailureRoubles.ToString(),
                    value => choice.FailureRoubles = Int(value, choice.FailureRoubles))));

            if (_draft.Choices.Count > 1)
            {
                box.Add(Button("REMOVE", () =>
                {
                    _draft.Choices.RemoveAt(index);
                    BuildForm();
                }));
            }

            return box;
        }

        /// <summary>The named places of one map, for the location picker. Empty means "anywhere on the map".</summary>
        private List<string> LocationsFor(string mapId)
        {
            var list = new List<string> { "" };
            if (!string.IsNullOrEmpty(mapId) && _maps.TryGetValue(mapId, out var map) && map.Locations != null)
            {
                list.AddRange(map.Locations.Locations.Select(pair => pair.Key));
            }

            return list;
        }

        /// <summary>
        /// A dropdown with an empty first entry ("anywhere"). Only values the game actually has are offered, so a
        /// scene cannot be pointed at a place that does not exist.
        /// </summary>
        /// <summary>
        /// A list of scenes used as a condition. The GM picks from the scenes that exist - "this happened first",
        /// "this never happened" - and never types an id, because an id typed by hand is a typo waiting to happen.
        /// </summary>
        /// <summary>
        /// The quest this scene finishes. A quest of kind "event" names a scene, and that name is what makes it
        /// complete - so the binding belongs on this page, not buried in quests.json.
        /// </summary>
        private VisualElement QuestBlock()
        {
            var block = new VisualElement();
            block.style.marginTop = 12;
            var caption = new Label("QUEST THIS SCENE FINISHES");
            caption.AddToClassList("gm-caption");
            block.Add(caption);

            foreach (var quest in QuestBindings.ForScene(_quests, _draft.EventId))
            {
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 4 } };
                var label = new Label($"{quest.Title}   ·   {quest.QuestId}   ·   {quest.TraderId}");
                label.AddToClassList("text-mono");
                label.AddToClassList("gm-value");
                label.style.flexGrow = 1;
                label.style.paddingLeft = 8;
                label.style.paddingTop = 8;
                label.style.paddingBottom = 8;
                label.style.fontSize = 15;
                row.Add(label);

                var questId = quest.QuestId;
                row.Add(Button("EDIT", () =>
                {
                    _questDraft = QuestDraft.From(quest);
                    BuildForm();
                }));
                row.Add(Button("UNBIND", () =>
                {
                    QuestBindings.Bind(questId, null);
                    ReloadQuests();
                    BuildForm();
                }));
                block.Add(row);
            }

            if (!QuestBindings.ForScene(_quests, _draft.EventId).Any())
            {
                var none = new Label("no quest is bound to this scene yet");
                none.AddToClassList("text-muted");
                none.style.fontSize = 14;
                block.Add(none);
            }

            block.Add(Button("NEW QUEST FOR THIS SCENE", () =>
            {
                _questDraft = QuestDraft.Fresh(_draft.EventId);
                BuildForm();
            }, primary: true));
            block.Add(Button("BIND AN EXISTING QUEST", () =>
            {
                var options = _quests
                    .Where(quest => quest.Objective.Kind == QuestObjectiveKinds.Event)
                    .Select(quest => quest.QuestId)
                    .ToList();
                ShowPickerOverlay("which quest does this scene finish", options, null, picked =>
                {
                    QuestBindings.Bind(picked, _draft.EventId);
                    ReloadQuests();
                    BuildForm();
                });
            }));

            return block;
        }

        /// <summary>The quest form: what the player reads, and what it pays.</summary>
        private VisualElement QuestForm()
        {
            var form = new VisualElement();
            form.AddToClassList("kia-panel");
            form.style.marginTop = 10;

            var header = new Label("QUEST");
            header.AddToClassList("kia-title");
            form.Add(header);

            form.Add(Field("quest id", _questDraft.QuestId, value => _questDraft.QuestId = value));
            form.Add(Row(
                Field("trader", _questDraft.TraderId, value => _questDraft.TraderId = value),
                Field("reward roubles", _questDraft.Roubles.ToString(),
                    value => _questDraft.Roubles = Int(value, _questDraft.Roubles)),
                Field("standing", _questDraft.Standing.ToString(),
                    value => _questDraft.Standing = Int(value, _questDraft.Standing))));
            form.Add(Field("title", _questDraft.Title, value => _questDraft.Title = value));
            form.Add(Field("text", _questDraft.Text, value => _questDraft.Text = value, multiline: true));
            form.Add(ItemField("reward items", _questDraft.Items, value => _questDraft.Items = value));

            var buttons = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };
            buttons.Add(Button("SAVE QUEST", SaveQuestDraft, primary: true));
            buttons.Add(Button("DISCARD", () =>
            {
                _questDraft = null;
                BuildForm();
            }));
            form.Add(buttons);
            return form;
        }

        private void SaveQuestDraft()
        {
            try
            {
                var chain = QuestBindings.LoadQuests().ToList();
                var quest = _questDraft.ToQuest();
                var key = _questDraft.OriginalId ?? _questDraft.QuestId;
                var at = chain.FindIndex(entry => string.Equals(entry.QuestId, key, StringComparison.Ordinal));
                if (at >= 0)
                {
                    chain[at] = quest;
                }
                else
                {
                    chain.Add(quest);
                }

                QuestStore.Save(chain);
                ReloadQuests();
                _questDraft = QuestDraft.From(quest);
                SetStatus("Saved quest " + quest.QuestId + ".");
                BuildForm();
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                SetStatus(error.Message, error: true);
            }
        }
        private void ReloadQuests() => _quests = QuestBindings.LoadQuests();

        private VisualElement SceneGate(string title, List<string> scenes)
        {
            var block = new VisualElement();
            block.style.marginTop = 12;
            var caption = new Label(title.ToUpperInvariant());
            caption.AddToClassList("gm-caption");
            block.Add(caption);

            foreach (var sceneId in scenes.ToList())
            {
                var id = sceneId;
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 4 } };
                var name = new Label(TitleOf(id));
                name.AddToClassList("text-mono");
                name.AddToClassList("gm-value");
                name.style.flexGrow = 1;
                name.style.paddingLeft = 6;
                name.style.paddingTop = 4;
                name.style.paddingBottom = 4;
                row.Add(name);

                var remove = Button("REMOVE", () =>
                {
                    scenes.Remove(id);
                    BuildForm();
                });
                remove.style.marginLeft = 6;
                remove.style.marginTop = 0;
                row.Add(remove);
                block.Add(row);
            }

            block.Add(Button("ADD SCENE", () =>
            {
                var options = _book.Events.Select(scene => scene.EventId).ToList();
                ShowPickerOverlay("pick a scene", options, null, picked =>
                {
                    if (!scenes.Contains(picked))
                    {
                        scenes.Add(picked);
                    }

                    BuildForm();
                });
            }));

            return block;
        }

        /// <summary>Attribute floors: an attribute, a number, no free text.</summary>
        private VisualElement AttributeGate()
        {
            var block = new VisualElement();
            block.style.marginTop = 12;
            var caption = new Label("REQUIRED ATTRIBUTES (all of them, at or above)");
            caption.AddToClassList("gm-caption");
            block.Add(caption);

            var needs = _draft.RequiredAttributes;
            for (var index = 0; index < needs.Count; index++)
            {
                var need = needs[index];
                var at = index;
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 4 } };

                var who = new Label(need.AbilityId);
                who.AddToClassList("text-mono");
                who.AddToClassList("gm-value");
                who.style.flexGrow = 1;
                who.style.paddingLeft = 6;
                who.style.paddingTop = 4;
                who.style.paddingBottom = 4;
                row.Add(who);

                var min = new TextField { value = need.Min.ToString() };
                min.AddToClassList("text-mono");
                min.style.width = 70;
                min.style.marginLeft = 6;
                min.style.fontSize = 15;
                min.RegisterValueChangedCallback(evt =>
                {
                    needs[at] = new AbilityRequirement(need.AbilityId,
                        Int(evt.newValue, need.Min));
                });
                row.Add(min);

                var remove = Button("REMOVE", () =>
                {
                    needs.RemoveAt(at);
                    BuildForm();
                });
                remove.style.marginLeft = 6;
                remove.style.marginTop = 0;
                row.Add(remove);
                block.Add(row);
            }

            block.Add(Button("ADD ATTRIBUTE", () =>
            {
                ShowPickerOverlay("pick an attribute", AbilityIds, null, picked =>
                {
                    needs.Add(new AbilityRequirement(picked, CharacterSheet.AbilityMin + 4));
                    BuildForm();
                });
            }));

            return block;
        }

        private static readonly List<string> AbilityIds =
            CharacterSheet.Abilities.OrderBy(id => id, StringComparer.Ordinal).ToList();

        private string TitleOf(string sceneId)
        {
            var scene = _book?.Find(sceneId);
            return scene == null ? sceneId : $"{scene.Title}   ·   {sceneId}";
        }

        /// <summary>
        /// A field that shows its value and opens our own list when you press CHANGE. Unity's DropdownField draws
        /// its menu in a separate popup document, which does not load this project's stylesheet - the menu stayed
        /// white on black whatever the USS said. A panel inside the page is styled like everything else.
        /// </summary>
        private VisualElement OptionPicker(string label, IReadOnlyList<string> options, string current,
            Action<string> onChanged)
        {
            var field = new VisualElement();
            field.style.marginTop = 8;
            var caption = new Label(label.ToUpperInvariant());
            caption.AddToClassList("gm-caption");
            field.Add(caption);

            var row = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            var shown = new Label(string.IsNullOrEmpty(current) ? "(any)" : current);
            shown.AddToClassList("text-mono");
            shown.AddToClassList("gm-value");
            shown.style.fontSize = 16;
            shown.style.flexGrow = 1;
            shown.style.paddingLeft = 6;
            shown.style.paddingTop = 4;
            shown.style.paddingBottom = 4;
            row.Add(shown);

            var change = Button("CHANGE", () => ShowPickerOverlay(label, options, current, onChanged));
            change.style.marginLeft = 6;
            change.style.marginTop = 0;
            row.Add(change);
            field.Add(row);
            return field;
        }

        /// <summary>
        /// A picker drawn over the screen, not inside the form: pushing a panel into the form reflowed it and the
        /// page jumped, and an anchor that has been rebuilt by then is no anchor at all. This one floats.
        /// </summary>
        private void ShowPickerOverlay(string title, IReadOnlyList<string> options, string current,
            Action<string> onPick)
        {
            ClosePickerOverlay();

            var overlay = new VisualElement();
            overlay.AddToClassList("gm-picker-overlay");
            overlay.pickingMode = PickingMode.Position;

            var panel = new VisualElement();
            panel.AddToClassList("kia-panel");
            panel.AddToClassList("gm-picker");

            var header = new Label(title.ToUpperInvariant());
            header.AddToClassList("kia-title");
            panel.Add(header);

            var list = new ScrollView();
            list.style.maxHeight = 320;
            panel.Add(list);

            foreach (var option in options ?? new List<string>())
            {
                var value = option;
                var text = string.IsNullOrEmpty(value) ? "(any)" : value;
                if (value == current)
                {
                    text += "   ·   now";
                }

                list.Add(Button(text, () =>
                {
                    ClosePickerOverlay();
                    onPick(value);
                }));
            }

            panel.Add(Button("CLOSE", ClosePickerOverlay));
            overlay.Add(panel);
            _root.Add(overlay);
            _pickerOverlay = overlay;
        }

        private void ClosePickerOverlay()
        {
            if (_pickerOverlay != null)
            {
                _pickerOverlay.RemoveFromHierarchy();
                _pickerOverlay = null;
            }
        }
        private static VisualElement Dropdown(string label, IReadOnlyList<string> options, string current,
            Action<string> onChanged)
        {
            var field = new VisualElement();
            field.style.marginTop = 8;
            var caption = new Label(label.ToUpperInvariant());
            caption.AddToClassList("gm-caption");
            field.Add(caption);

            var list = new List<string>(options ?? new List<string>());
            var value = current ?? "";
            if (!list.Contains(value))
            {
                list.Insert(0, value);
            }

            var dropdown = new DropdownField(list, Math.Max(0, list.IndexOf(value)));
            dropdown.AddToClassList("text-mono");
            dropdown.style.fontSize = 15;
            dropdown.RegisterValueChangedCallback(evt => onChanged(evt.newValue));
            field.Add(dropdown);
            return field;
        }

        /// <summary>
        /// An item id field with a picker: typing 4832 ids by hand is not authoring, it is transcription. The
        /// picker lists the catalogue, filtered as you type, and appends what you click.
        /// </summary>
        private VisualElement ItemField(string label, string value, Action<string> onChanged)
        {
            var field = Field(label, value, onChanged);
            var pick = Button("PICK", () => ShowItemPicker(append =>
            {
                var current = string.IsNullOrWhiteSpace(value) ? "" : value.TrimEnd() + ",";
                value = current + append;
                onChanged(value);
                BuildForm();
            }));
            pick.style.marginTop = 4;
            field.Add(pick);
            return field;
        }

        /// <summary>
        /// The item picker: the same floating overlay as the option picker, with a search box because there are
        /// 4832 items and typing an id by hand is transcription, not authoring.
        /// </summary>
        private void ShowItemPicker(Action<string> append)
        {
            ClosePickerOverlay();

            var overlay = new VisualElement();
            overlay.AddToClassList("gm-picker-overlay");
            overlay.pickingMode = PickingMode.Position;

            var panel = new VisualElement();
            panel.AddToClassList("kia-panel");
            panel.AddToClassList("gm-picker");

            var header = new Label("PICK AN ITEM");
            header.AddToClassList("kia-title");
            panel.Add(header);

            var search = new TextField { value = "" };
            search.AddToClassList("text-mono");
            search.style.fontSize = 16;
            panel.Add(search);

            var list = new ScrollView();
            list.style.maxHeight = 300;
            list.style.marginTop = 6;
            panel.Add(list);

            void Fill(string filter)
            {
                list.Clear();
                var matches = _catalog.Values
                    .Where(item => string.IsNullOrWhiteSpace(filter)
                                   || item.ItemId.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                                   || item.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(40)
                    .ToList();

                foreach (var item in matches)
                {
                    var id = item.ItemId;
                    list.Add(Button($"{item.Name}   ·   {id}", () =>
                    {
                        ClosePickerOverlay();
                        append(id);
                    }));
                }

                if (matches.Count == 0)
                {
                    var none = new Label("Nothing matches.");
                    none.AddToClassList("text-muted");
                    none.style.fontSize = 14;
                    list.Add(none);
                }
            }

            search.RegisterValueChangedCallback(evt => Fill(evt.newValue));
            Fill("");
            panel.Add(Button("CLOSE", ClosePickerOverlay));
            overlay.Add(panel);
            _root.Add(overlay);
            _pickerOverlay = overlay;
        }


        /// <summary>
        /// The check, as rows. Three shapes cover what the expression language can say here: a comparison, "has this
        /// flag", "carries this item". No typing of syntax anywhere - the rows compile to the expression the runtime
        /// already reads, so nothing downstream changes.
        /// </summary>
        /// <summary>
        /// The check is a formula the GM writes, with the tokens one click away. It is not a CSV and not a form:
        /// a condition is arithmetic, and arithmetic reads better as arithmetic.
        /// </summary>
        private VisualElement CheckFormula(ChoiceDraft owner)
        {
            var block = new VisualElement();
            block.style.marginTop = 12;
            var caption = new Label("CHECK (EMPTY = ALWAYS SUCCEEDS)");
            caption.AddToClassList("gm-caption");
            block.Add(caption);

            var input = new TextField { value = owner.Check ?? "" };
            input.AddToClassList("text-mono");
            input.style.fontSize = 16;
            input.style.minHeight = 28;
            block.Add(input);

            var status = new Label("");
            status.style.fontSize = 14;
            status.style.whiteSpace = WhiteSpace.Normal;
            block.Add(status);

            void Update(string text)
            {
                owner.Check = text;
                if (string.IsNullOrWhiteSpace(text))
                {
                    status.text = "no check: this choice always succeeds";
                    status.EnableInClassList("text-muted", true);
                    status.EnableInClassList("text-danger", false);
                    return;
                }

                try
                {
                    EventExpression.Compile(text);
                    status.text = "reads as a formula";
                    status.EnableInClassList("text-muted", true);
                    status.EnableInClassList("text-danger", false);
                }
                catch (ValidationException error)
                {
                    status.text = error.Message;
                    status.EnableInClassList("text-muted", false);
                    status.EnableInClassList("text-danger", true);
                }
            }

            input.RegisterValueChangedCallback(evt => Update(evt.newValue));
            Update(input.value);

            var tokens = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };
            foreach (var token in new[]
                     {
                         "muscle", "finesse", "agility", "sense", "perception", "charm", "build", "humanity",
                         "roubles", "party", "d100", "d20", "+", "-", "*", "/", ">=", "<=", "=", "and", "or", "not",
                     })
            {
                var insert = token;
                tokens.Add(Button(insert, () =>
                {
                    var text = input.value ?? "";
                    var glued = insert == "+" || insert == "-" || insert == "*" || insert == "/"
                                || insert == ">=" || insert == "<=" || insert == "=";
                    input.value = text.Length == 0
                        ? insert + (glued ? " " : " ")
                        : (glued ? text.TrimEnd() + " " + insert + " " : text + insert + " ");
                    Update(input.value);
                }));
            }

            tokens.Add(Button("carried( )", () =>
            {
                ShowItemPicker(id =>
                {
                    input.value = (input.value ?? "") + "carried(\"" + id + "\") ";
                    Update(input.value);
                    BuildForm();
                });
            }));

            tokens.Add(Button("flag( )", () =>
            {
                ShowPickerOverlay("pick a flag", KnownFlags(), null, picked =>
                {
                    input.value = (input.value ?? "") + "flag(\"" + picked + "\") ";
                    Update(input.value);
                    BuildForm();
                });
            }));

            block.Add(tokens);
            return block;
        }
        /// </summary>
        private VisualElement FlagEditor(string title, ChoiceDraft choice, bool sets)
        {
            var block = new VisualElement();
            block.style.marginTop = 10;
            var caption = new Label(title.ToUpperInvariant());
            caption.AddToClassList("gm-caption");
            block.Add(caption);

            var current = (sets ? choice.Sets : choice.RequiresFlags) ?? "";
            var names = current.Length == 0
                ? new List<string>()
                : current.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(name => name.Trim()).Where(name => name.Length > 0).ToList();

            foreach (var name in names.ToList())
            {
                var flag = name;
                var at = names.IndexOf(flag);
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 4 } };

                // The name is editable: a flag the GM invents has to be spellable the way the GM spells it.
                var field = new TextField { value = flag };
                field.AddToClassList("text-mono");
                field.style.flexGrow = 1;
                field.style.fontSize = 15;
                field.RegisterValueChangedCallback(evt =>
                {
                    names[at] = evt.newValue.Trim();
                    if (sets) { choice.Sets = string.Join(",", names.Where(n => n.Length > 0)); }
                    else { choice.RequiresFlags = string.Join(",", names.Where(n => n.Length > 0)); }
                });
                row.Add(field);

                var remove = Button("REMOVE", () =>
                {
                    names.Remove(flag);
                    if (sets) { choice.Sets = string.Join(",", names); }
                    else { choice.RequiresFlags = string.Join(",", names); }
                    BuildForm();
                });
                remove.style.marginLeft = 6;
                remove.style.marginTop = 0;
                row.Add(remove);
                block.Add(row);
            }

            block.Add(Button(sets ? "ADD NEW FLAG" : "PICK AN EXISTING FLAG", () =>
            {
                if (sets)
                {
                    var created = names.Count == 0
                        ? choice.Label.ToLowerInvariant().Replace(' ', '-')
                        : names[names.Count - 1] + "-b";
                    names.Add(created);
                    choice.Sets = string.Join(",", names);
                    BuildForm();
                    return;
                }

                ShowPickerOverlay("pick a flag", KnownFlags(), null, picked =>
                {
                    names.Add(picked);
                    choice.RequiresFlags = string.Join(",", names.Distinct());
                    BuildForm();
                });
            }));

            return block;
        }

        /// <summary>Every flag the book mentions, so the pickers can offer what exists instead of a blank box.</summary>
        private List<string> KnownFlags()
        {
            var flags = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var scene in _book.Events)
            {
                foreach (var flag in scene.Requires.Flags.Concat(scene.Requires.Unseen))
                {
                    flags.Add(flag);
                }

                foreach (var choice in scene.Choices)
                {
                    foreach (var flag in choice.Sets.Concat(choice.RequiresFlags))
                    {
                        flags.Add(flag);
                    }
                }
            }

            return flags.ToList();
        }

        private static VisualElement Row(params VisualElement[] children)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };
            foreach (var child in children)
            {
                child.style.flexGrow = 1;
                child.style.marginRight = 6;
                row.Add(child);
            }

            return row;
        }

        private static VisualElement Field(string label, string value, Action<string> onChanged,
            bool multiline = false)
        {
            var field = new VisualElement();
            field.style.marginTop = 8;
            var caption = new Label(label.ToUpperInvariant());
            caption.AddToClassList("gm-caption");
            field.Add(caption);
            var input = multiline
                ? new TextField { multiline = true, value = value ?? "" }
                : new TextField { value = value ?? "" };
            input.AddToClassList("text-mono");
            input.style.fontSize = 16;
            input.style.minHeight = multiline ? 72 : 28;
            input.RegisterValueChangedCallback(evt => onChanged(evt.newValue));
            field.Add(input);
            return field;
        }

        private static VisualElement Toggle(string label, bool value, Action<bool> onChanged)
        {
            var toggle = new Toggle(label) { value = value };
            toggle.AddToClassList("text-label");
            toggle.style.marginTop = 10;
            toggle.style.marginRight = 10;
            toggle.style.fontSize = 14;
            toggle.RegisterValueChangedCallback(evt => onChanged(evt.newValue));
            return toggle;
        }

        private static int Int(string text, int fallback) => int.TryParse(text, out var value) ? value : fallback;

        private void Save()
        {
            if (_draft == null)
            {
                return;
            }

            try
            {
                var scenes = new List<StoryEvent>();
                foreach (var scene in _book.Events)
                {
                    scenes.Add(scene.EventId == _draft.OriginalId ? _draft.ToStoryEvent() : scene);
                }

                if (_book.Find(_draft.OriginalId) == null)
                {
                    scenes.Add(_draft.ToStoryEvent());
                }

                var book = new EventBook(scenes);
                EventStore.Save(book);
                _book = EventStore.Load(_catalog);
                _draft = EventDraft.From(book.Find(_draft.EventId) ?? _draft.ToStoryEvent());
                SetStatus($"Saved {book.Events.Count} scenes.");
                BuildList();
                BuildForm();
            }
            catch (ValidationException error)
            {
                SetStatus(error.Message, error: true);
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                SetStatus(error.Message, error: true);
            }
        }
    }

}
