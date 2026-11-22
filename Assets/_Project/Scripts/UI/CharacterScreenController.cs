using System;
using System.Collections.Generic;
using System.Linq;
using Safehouse.Core;
using Safehouse.Data;
using UnityEngine.UIElements;

namespace Safehouse.UI
{
    /// <summary>
    /// Creates a character and stores the Gundog Revised sheet. The numbers are not used by expeditions.
    /// </summary>
    [UnityEngine.RequireComponent(typeof(UIDocument))]
    public sealed class CharacterScreenController : UnityEngine.MonoBehaviour
    {
        private VisualElement _root;
        private ScrollView _scroll;
        private Label _error;
        private readonly Dictionary<string, int> _abilities =
            Gundog.Abilities.ToDictionary(stat => stat.StatId, stat => Gundog.AbilityDefault);
        private readonly Dictionary<string, TextField> _identity = new Dictionary<string, TextField>();
        private readonly List<TextField> _career = new List<TextField>();
        private readonly HashSet<string> _arts = new HashSet<string>();
        private TextField _name;
        private string _main = "assault";
        private string _sub = "scout";
        private VisualElement _skills;
        private VisualElement _artRow;
        private VisualElement _mainRow;
        private VisualElement _subRow;
        private int _reward;
        private int _movement = 9;
        private int _durability = 48;
        private Label _rewardLabel;
        private Label _movementLabel;
        private Label _durabilityLabel;

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;

            // The panel scales the 1440x900 design to the window but keeps its proportions, so a
            // window of another aspect leaves a strip of empty space on one axis. Centre the design
            // in it, exactly like the other screens - this one was missing it and sat in the corner.
            _root.style.alignItems = Align.Center;
            _root.style.justifyContent = Justify.Center;

            _scroll = _root.Q<ScrollView>("character-scroll");
            _error = _root.Q<Label>("character-error");
            _root.Q<Button>("navtab-gear").clicked += () => ScreenNavigator.Go("gear");
            _root.Q<Button>("navtab-hideout").clicked += () => ScreenNavigator.Go("hideout");
            _root.Q<Button>("navtab-traders").clicked += () => ScreenNavigator.Go("traders");
            _root.Q<Button>("navtab-scavenge").clicked += () => ScreenNavigator.Go("scavenge");
            _root.Q<Button>("navtab-quests").clicked += () => ScreenNavigator.Go("quests");
            _root.Q<Button>("navtab-settings").clicked += () => ScreenNavigator.Go("settings");
            ScreenNavigator.Register("character", Show, Hide);
            Hide();
        }

        private void OnDisable() => ScreenNavigator.Unregister("character");

        private void Show()
        {
            _root.style.display = DisplayStyle.Flex;
            Build();
        }

        private void Hide() => _root.style.display = DisplayStyle.None;

        private void Build()
        {
            _scroll.Clear();
            _error.text = "";
            _identity.Clear();
            _career.Clear();
            var column = new VisualElement();
            column.Add(Section("ROSTER"));
            column.Add(Roster());
            column.Add(Section("NAME"));
            _name = Field("NAME", wide: true);
            column.Add(_name);

            column.Add(Section("IDENTITY"));
            var identity = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };
            AddIdentity(identity, "nationality", "NATIONALITY");
            AddIdentity(identity, "gender", "GENDER");
            AddIdentity(identity, "age", "AGE");
            AddIdentity(identity, "blood_type", "BLOOD TYPE");
            AddIdentity(identity, "height", "HEIGHT");
            AddIdentity(identity, "weight", "WEIGHT");
            AddIdentity(identity, "hair_color", "HAIR");
            AddIdentity(identity, "eye_color", "EYES");
            AddIdentity(identity, "skin_color", "SKIN");
            AddIdentity(identity, "rank", "RANK");
            AddIdentity(identity, "language", "LANGUAGE");
            AddIdentity(identity, "occupation", "OCCUPATION");
            AddIdentity(identity, "era", "ERA");
            column.Add(identity);

            column.Add(Section("ABILITY"));
            foreach (var stat in Gundog.Abilities)
            {
                column.Add(AbilityRow(stat));
            }

            column.Add(Section("CLASS"));
            column.Add(new Label("MAIN") { style = { marginBottom = 4 } });
            _mainRow = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };
            column.Add(_mainRow);
            column.Add(new Label("SUB") { style = { marginTop = 4, marginBottom = 4 } });
            _subRow = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };
            column.Add(_subRow);

            column.Add(Section("SKILL"));
            var note = new Label("Class modifiers. Not applied to expeditions.");
            note.AddToClassList("text-muted");
            note.style.marginBottom = 6;
            column.Add(note);
            _skills = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };
            column.Add(_skills);

            column.Add(Section("CLASS ARTS"));
            _artRow = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };
            column.Add(_artRow);

            column.Add(Section("CAREER"));
            for (var index = 0; index < Gundog.CareerLines; index++)
            {
                var line = Field("CAREER " + (index + 1), wide: true);
                _career.Add(line);
                column.Add(line);
            }

            column.Add(Section("SHEET"));
            _rewardLabel = new Label();
            _movementLabel = new Label();
            _durabilityLabel = new Label();
            column.Add(Stepper("REWARD", () => _reward, value => _reward = value, 0, Gundog.MaxReward, _rewardLabel));
            column.Add(Stepper("MOVEMENT", () => _movement, value => _movement = value, 0, Gundog.MaxMovement, _movementLabel));
            column.Add(Stepper("DURABILITY", () => _durability, value => _durability = value, 0, Gundog.MaxDurability, _durabilityLabel));

            var create = new Button(Create) { text = "CREATE" };
            create.AddToClassList("btn");
            create.AddToClassList("btn-primary");
            create.style.marginTop = 12;
            create.style.width = 160;
            column.Add(create);
            _scroll.Add(column);
            RefreshClasses();
        }

        private void Create()
        {
            var session = GearScreenController.Current?.Session;
            if (session == null || !session.Saves)
            {
                _error.text = "Open a save folder on the GEAR screen first.";
                return;
            }

            try
            {
                var bio = CharacterSheet.BioFields.ToDictionary(field => field, field => "");
                foreach (var pair in _identity)
                {
                    if (bio.ContainsKey(pair.Key))
                    {
                        bio[pair.Key] = pair.Value.value.Trim();
                    }
                }

                var sheet = new GundogSheet(_abilities, _main, _sub, _arts.OrderBy(id => id, StringComparer.Ordinal),
                    _career.Select(field => field.value.Trim()).ToList(), _reward, _movement, _durability,
                    Text("rank"), Text("language"), Text("occupation"), Text("era"));
                session.CreateCharacter(_name.value, bio, sheet);
                _error.text = "";
                ScreenNavigator.Go("gear");
            }
            catch (Exception error)
            {
                _error.text = error.Message;
            }
        }

        /// <summary>
        /// The characters in the data folder. There was no way to see them or get rid of one - the
        /// repository could list and delete all along; this is the screen. The form below still creates
        /// a character (with its Gundog sheet), so creating is not repeated here.
        /// </summary>
        private VisualElement Roster()
        {
            var list = new VisualElement { style = { marginBottom = 16 } };
            var session = GearScreenController.Current?.Session;
            if (session == null || !session.Saves)
            {
                list.Add(new Label("No data folder is open, so there are no characters to list."));
                return list;
            }

            IReadOnlyList<Profile> profiles;
            IReadOnlyList<string> problems;
            try
            {
                (profiles, problems) = session.Repository.List();
            }
            catch (Exception error)
            {
                list.Add(new Label("Could not read the save folder: " + error.Message));
                return list;
            }

            foreach (var problem in problems)
            {
                var note = new Label(problem);
                note.AddToClassList("text-danger");
                list.Add(note);
            }

            foreach (var profile in profiles)
            {
                var row = new VisualElement
                {
                    style =
                    {
                        flexDirection = FlexDirection.Row,
                        alignItems = Align.Center,
                        marginBottom = 6,
                    },
                };

                var open = profile.ProfileId == session.Profile.ProfileId;
                var label = new Label(profile.DisplayName
                    + "     " + profile.Status.Replace('_', ' ').ToUpperInvariant()
                    + "     " + profile.Money + " R")
                {
                    style = { flexGrow = 1 },
                };
                label.AddToClassList(open ? "text-accent" : "text-muted");
                row.Add(label);

                var id = profile.ProfileId;
                var openButton = new Button(() =>
                {
                    var gear = GearScreenController.Current;
                    if (gear == null || gear.OpenCharacter(id) == null)
                    {
                        ScreenNavigator.Go("gear");
                    }
                })
                {
                    text = "OPEN",
                    style = { marginRight = 6, height = 26, fontSize = 10, paddingLeft = 12, paddingRight = 12 },
                };
                openButton.AddToClassList("btn");
                openButton.AddToClassList("btn-outline");
                openButton.SetEnabled(!open);
                row.Add(openButton);

                var deleteButton = new Button { text = "DELETE" };
                deleteButton.AddToClassList("btn");
                deleteButton.AddToClassList("btn-danger");
                deleteButton.style.height = 26;
                deleteButton.style.fontSize = 10;
                deleteButton.style.paddingLeft = 12;
                deleteButton.style.paddingRight = 12;
                deleteButton.SetEnabled(!open);
                var confirming = false;
                deleteButton.clicked += () =>
                {
                    if (!confirming)
                    {
                        confirming = true;
                        deleteButton.text = "SURE?";
                        return;
                    }

                    try
                    {
                        session.Repository.Delete(id);
                        _error.text = "";
                    }
                    catch (Exception error)
                    {
                        _error.text = error.Message;
                    }

                    Build();
                };
                row.Add(deleteButton);
                list.Add(row);
            }

            var hint = new Label("The form below adds a new character. A character that is open cannot be "
                + "deleted - open another one first.");
            hint.AddToClassList("text-muted");
            hint.style.whiteSpace = WhiteSpace.Normal;
            hint.style.maxWidth = 560;
            hint.style.marginTop = 6;
            list.Add(hint);
            return list;
        }

        private string Text(string key) => _identity.ContainsKey(key) ? _identity[key].value.Trim() : "";

        private void RefreshClasses()
        {
            FillClassRow(_mainRow, true);
            FillClassRow(_subRow, false);
            _skills.Clear();
            var sheet = new GundogSheet(_abilities, _main, _sub, null,
                Enumerable.Repeat("", Gundog.CareerLines).ToList(), 0, 0, 0, "", "", "", "");
            var modifiers = sheet.SkillModifiers();
            foreach (var skill in Gundog.Skills)
            {
                var label = new Label(skill.Name + "  " + modifiers[skill.StatId]);
                label.style.marginRight = 14;
                label.AddToClassList("text-accent");
                _skills.Add(label);
            }

            var chosenClasses = new[] { Gundog.FindClass(_main), Gundog.FindClass(_sub) }
                .GroupBy(entry => entry.ClassId)
                .Select(group => group.First())
                .ToList();
            var allowed = new HashSet<string>();
            foreach (var entry in chosenClasses)
            {
                foreach (var art in entry.Arts)
                {
                    allowed.Add(art.ArtId);
                }
            }

            _arts.RemoveWhere(id => !allowed.Contains(id));
            _artRow.Clear();
            foreach (var entry in chosenClasses)
            {
                foreach (var art in entry.Arts)
                {
                    var id = art.ArtId;
                    var button = new Button(() =>
                    {
                        if (!_arts.Add(id))
                        {
                            _arts.Remove(id);
                        }

                        RefreshClasses();
                    })
                    { text = art.Name };
                    button.AddToClassList("btn");
                    button.AddToClassList("btn-outline");
                    button.AddToClassList("sheet-art");
                    if (_arts.Contains(id))
                    {
                        button.AddToClassList("sheet-art-on");
                    }

                    _artRow.Add(button);
                }
            }
        }

        private void FillClassRow(VisualElement row, bool main)
        {
            row.Clear();
            foreach (var entry in Gundog.Classes)
            {
                var id = entry.ClassId;
                var button = new Button(() =>
                {
                    if (main)
                    {
                        _main = id;
                    }
                    else
                    {
                        _sub = id;
                    }

                    RefreshClasses();
                })
                { text = entry.Name };
                button.AddToClassList("btn");
                button.AddToClassList((main ? _main : _sub) == id ? "btn-primary" : "btn-outline");
                button.AddToClassList("sheet-class");
                row.Add(button);
            }
        }

        private VisualElement AbilityRow(GundogStat stat)
        {
            var row = new VisualElement();
            row.AddToClassList("sheet-row");
            var name = new Label(stat.Name);
            name.AddToClassList("sheet-stat");
            var value = new Label(_abilities[stat.StatId].ToString());
            value.AddToClassList("sheet-value");
            value.AddToClassList("text-mono");
            row.Add(name);
            row.Add(Step("-", () => ChangeAbility(stat.StatId, -1, value)));
            row.Add(value);
            row.Add(Step("+", () => ChangeAbility(stat.StatId, 1, value)));
            return row;
        }

        private void ChangeAbility(string statId, int delta, Label value)
        {
            var next = _abilities[statId] + delta;
            if (next < Gundog.AbilityMin || next > Gundog.AbilityMax)
            {
                return;
            }

            _abilities[statId] = next;
            value.text = next.ToString();
        }

        private VisualElement Stepper(string caption, Func<int> read, Action<int> write, int low, int high, Label value)
        {
            var row = new VisualElement();
            row.AddToClassList("sheet-row");
            var name = new Label(caption);
            name.AddToClassList("sheet-stat");
            value.text = read().ToString();
            value.AddToClassList("sheet-value");
            value.AddToClassList("text-mono");
            row.Add(name);
            row.Add(Step("-", () =>
            {
                if (read() > low)
                {
                    write(read() - 1);
                    value.text = read().ToString();
                }
            }));
            row.Add(value);
            row.Add(Step("+", () =>
            {
                if (read() < high)
                {
                    write(read() + 1);
                    value.text = read().ToString();
                }
            }));
            return row;
        }

        private static Button Step(string text, Action action)
        {
            var button = new Button(action) { text = text };
            button.AddToClassList("btn");
            button.AddToClassList("btn-outline");
            button.AddToClassList("sheet-step");
            return button;
        }

        private void AddIdentity(VisualElement row, string key, string label)
        {
            var field = Field(label);
            _identity[key] = field;
            row.Add(field);
        }

        private static TextField Field(string label, bool wide = false)
        {
            var field = new TextField(label);
            field.AddToClassList("sheet-field");
            if (wide)
            {
                field.AddToClassList("sheet-field-wide");
            }

            field.maxLength = wide ? CharacterSheet.MaxNameLength : Gundog.MaxLineLength;
            var caption = field.labelElement;
            caption.style.minWidth = 0;
            caption.style.width = StyleKeyword.Auto;
            caption.style.marginRight = 6;
            caption.style.paddingLeft = 0;
            caption.style.paddingRight = 0;
            caption.style.flexGrow = 0;
            caption.style.unityTextAlign = UnityEngine.TextAnchor.MiddleRight;
            field.style.width = wide ? new StyleLength(new Length(100, LengthUnit.Percent)) : StyleKeyword.Auto;
            field.style.flexGrow = wide ? 1 : 0;
            var input = field.Q(className: "unity-base-field__input");
            if (input != null)
            {
                // Inline styles beat the stylesheet, so this has to match Theme.uss - a white box here
                // would stay white no matter what the USS says.
                input.style.backgroundColor = new UnityEngine.Color(20f / 255f, 23f / 255f, 27f / 255f);
                input.style.color = new UnityEngine.Color(230f / 255f, 233f / 255f, 236f / 255f);
            }

            return field;
        }

        private static Label Section(string text)
        {
            var label = new Label(text);
            label.AddToClassList("text-section");
            label.AddToClassList("sheet-section");
            label.style.marginTop = 8;
            return label;
        }
    }
}
