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
    /// Creates a character and stores the Gundog Revised sheet. The numbers are not used by expeditions.
    /// </summary>
    [UnityEngine.RequireComponent(typeof(UIDocument))]
    public sealed class CharacterScreenController : UnityEngine.MonoBehaviour
    {
        private VisualElement _root;
        private ScrollView _scroll;
        private Label _error;
        // The profile's own abilities - the ones an expedition builds a fighter from. The Gundog
        // sheet's ability list is a different vocabulary and nothing reads it.
        private readonly Dictionary<string, int> _abilities =
            CharacterSheet.Abilities.ToDictionary(ability => ability, ability => CharacterSheet.AbilityBase);
        private readonly Dictionary<string, string> _identity = new Dictionary<string, string>();
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
        private Label _rosterStatus;
        private Label _abilityPool;
        private int _startingKit;

        /// <summary>What a new character walks out of the hideout with. Ids are from items.json.</summary>
        private static readonly (string Name, string Blurb, (string Item, int Count)[] Items)[] Kits =
        {
            ("RIFLEMAN", "AK-74N, two boxes of BT, two bandages",
                new[] { ("kalashnikov-ak-74n-545x39-assault-rifle", 1), ("545x39mm-bt-gs", 2), ("army-bandage", 2) }),
            ("SCOUT", "PP-91 Kedr, two boxes of 9x18, one bandage",
                new[] { ("pp-91-kedr-9x18pm-submachine-gun", 1), ("9x18mm-pm-bzht-gzh", 2), ("army-bandage", 1) }),
            ("BREACHER", "MP-133, two boxes of buckshot, two bandages",
                new[] { ("mp-133-12ga-pump-action-shotgun", 1), ("1270-7mm-buckshot", 2), ("army-bandage", 2) }),
            ("MARKSMAN", "SKS carbine, two boxes of PS, one AI-2 kit",
                new[] { ("toz-simonov-sks-762x39-carbine", 1), ("762x39mm-ps-gzh", 2), ("ai-2-medkit", 1) }),
        };

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

            // The game opens here, on the list of characters, rather than on a stash.
            Show();
        }

        private void OnDisable() => ScreenNavigator.Unregister("character");

        private void Show()
        {
            _root.style.display = DisplayStyle.Flex;
            _creating = false;
            Build();

            // The GEAR screen is what opens the data folder. If this screen is built before it got
            // that far, the roster has nothing to list - so look again once it has.
            if (GearScreenController.Current?.Session == null)
            {
                _root.schedule.Execute(() =>
                {
                    if (GearScreenController.Current?.Session != null)
                    {
                        Build();
                    }
                }).StartingIn(150);
            }
        }

        private void Hide() => _root.style.display = DisplayStyle.None;

        private bool _creating;
        private int _newAvatarIndex = -1;

        /// <summary>A new character starts from the defaults, not from whatever was typed last time.</summary>
        private void ResetForm()
        {
            foreach (var ability in CharacterSheet.Abilities)
            {
                _abilities[ability] = CharacterSheet.AbilityBase;
            }

            _identity.Clear();
            _career.Clear();
            _arts.Clear();
            _main = "assault";
            _sub = "scout";
            _reward = 0;
            _movement = 9;
            _durability = 48;
            _startingKit = 0;
            _error.text = "";
        }

        private void Build()
        {
            ClosePresets();
            _scroll.Clear();
            _error.text = "";
            _identity.Clear();
            _career.Clear();

            _scroll.Add(_creating ? CreatePage() : RosterPage());
        }

        /// <summary>The saves, with the button that starts a new one above them.</summary>
        private VisualElement RosterPage()
        {
            var column = new VisualElement();
            column.Add(Section("CHARACTERS"));

            var newCharacter = new Button(() =>
            {
                ResetForm();
                _creating = true;
                _newAvatarIndex = -1;
                Build();
            })
            {
                text = "NEW CHARACTER",
            };
            newCharacter.AddToClassList("btn");
            newCharacter.AddToClassList("btn-primary");
            newCharacter.style.width = 220;
            newCharacter.style.marginBottom = 16;
            column.Add(newCharacter);

            column.Add(Roster());
            return column;
        }

        /// <summary>Creating a character is its own page, and it starts with the portrait.</summary>
        private VisualElement CreatePage()
        {
            var column = new VisualElement();
            column.Add(Section("NEW CHARACTER"));

            var back = new Button(() =>
            {
                _creating = false;
                Build();
            })
            {
                text = "BACK TO CHARACTERS",
            };
            back.AddToClassList("btn");
            back.AddToClassList("btn-outline");
            back.style.width = 220;
            back.style.marginBottom = 16;
            column.Add(back);

            column.Add(Section("PORTRAIT"));
            column.Add(PortraitPicker());

            column.Add(Section("NAME"));
            _name = Field("NAME", wide: true);
            column.Add(_name);

            column.Add(Section("IDENTITY"));
            var identity = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };
            AddIdentity(identity, "nationality", "NATIONALITY", new[]
            {
                "China", "Hong Kong", "Taiwan", "Russia", "Ukraine", "Poland", "Germany",
                "France", "United Kingdom", "United States", "Japan", "Korea", "Australia",
            });
            AddIdentity(identity, "gender", "GENDER", new[] { "Male", "Female", "Other" });
            AddNumber(identity, "age", "AGE", 18, 65, 1);
            AddIdentity(identity, "blood_type", "BLOOD TYPE",
                new[] { "A+", "A-", "B+", "B-", "AB+", "AB-", "O+", "O-" });
            AddNumber(identity, "height", "HEIGHT", 150, 200, 5);
            AddNumber(identity, "weight", "WEIGHT", 45, 120, 5);
            AddIdentity(identity, "hair_color", "HAIR",
                new[] { "Black", "Dark brown", "Brown", "Auburn", "Blond", "Red", "Grey", "White", "Shaved" });
            AddIdentity(identity, "eye_color", "EYES",
                new[] { "Brown", "Hazel", "Green", "Grey", "Blue", "Amber" });
            AddIdentity(identity, "skin_color", "SKIN",
                new[] { "Fair", "Light", "Olive", "Tan", "Brown", "Dark" });
            AddIdentity(identity, "rank", "RANK",
                new[] { "Private", "Corporal", "Sergeant", "Lieutenant", "Captain", "Major" });
            AddIdentity(identity, "language", "LANGUAGE",
                new[] { "English", "Russian", "Mandarin", "Cantonese", "Polish", "Ukrainian", "Korean", "Japanese" });
            AddIdentity(identity, "occupation", "OCCUPATION",
                new[] { "Rifleman", "Scout", "Medic", "Engineer", "Machine gunner", "Sniper", "Driver", "Signaller" });
            AddIdentity(identity, "era", "ERA", new[] { "1990s", "2000s", "2010s", "Present" });

            column.Add(identity);

            column.Add(Section("ABILITY"));
            _abilityPool = new Label("");
            _abilityPool.AddToClassList("text-mono");
            _abilityPool.style.fontSize = 12;
            _abilityPool.style.marginBottom = 8;
            column.Add(_abilityPool);
            foreach (var ability in CharacterSheet.Abilities)
            {
                column.Add(AbilityRow(ability));
            }

            RefreshAbilityPool();

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

            column.Add(Section("STARTING KIT"));
            column.Add(KitPicker());

            var create = new Button(Create) { text = "CREATE" };
            create.AddToClassList("btn");
            create.AddToClassList("btn-primary");
            create.style.marginTop = 12;
            create.style.width = 160;
            column.Add(create);
            RefreshClasses();
            return column;
        }

        /// <summary>The kits, one clickable card each; the chosen one is outlined.</summary>
        private VisualElement KitPicker()
        {
            var row = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginBottom = 14 },
            };

            for (var i = 0; i < Kits.Length; i++)
            {
                var index = i;
                var card = new Button(() =>
                {
                    _startingKit = index;
                    Build();
                });
                card.AddToClassList("card");
                card.style.flexDirection = FlexDirection.Column;
                card.style.alignItems = Align.FlexStart;
                card.style.width = 260;
                card.style.marginRight = 10;
                card.style.marginBottom = 8;
                card.style.paddingLeft = 12;
                card.style.paddingRight = 12;
                card.style.paddingTop = 10;
                card.style.paddingBottom = 10;
                if (i == _startingKit)
                {
                    card.AddToClassList("sheet-art-on");
                }

                var title = new Label(Kits[i].Name);
                title.style.fontSize = 14;
                card.Add(title);

                var blurb = new Label(Kits[i].Blurb);
                blurb.AddToClassList("text-muted");
                blurb.style.fontSize = 11;
                blurb.style.whiteSpace = WhiteSpace.Normal;
                blurb.style.marginTop = 4;
                card.Add(blurb);

                row.Add(card);
            }

            return row;
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
                        bio[pair.Key] = pair.Value.Trim();
                    }
                }

                var sheet = new GundogSheet(Gundog.Abilities.ToDictionary(stat => stat.StatId, stat => Gundog.AbilityDefault),
                    _main, _sub, _arts.OrderBy(id => id, StringComparer.Ordinal),
                    _career.Select(field => field.value.Trim()).ToList(), _reward, _movement, _durability,
                    Text("rank"), Text("language"), Text("occupation"), Text("era"));
                var created = session.CreateCharacter(_name.value, bio, sheet);
                if (created.Abilities.Any(pair => pair.Value != _abilities[pair.Key]))
                {
                    created = created.With(abilities: _abilities);
                    session.Commit(created);
                }

                // the starting kit goes into the stash, one AddItems per line so a full stash refuses
                // the whole thing rather than leaving half of it behind
                var stash = created.Stash;
                foreach (var (item, count) in Kits[_startingKit].Items)
                {
                    stash = InventoryRules.AddItems(stash, CatalogLoader.Load(), item, count);
                }

                created = created.With(stash: stash);
                session.Commit(created);
                if (_newAvatarIndex >= 0)
                {
                    PlayerPrefs.SetInt("Safehouse.Avatar." + created.ProfileId, _newAvatarIndex);
                    PlayerPrefs.Save();
                }

                _creating = false;
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
                        paddingLeft = 14,
                        paddingRight = 14,
                        paddingTop = 12,
                        paddingBottom = 12,
                        marginBottom = 12,
                    },
                };
                row.AddToClassList("card");
                row.style.flexDirection = FlexDirection.Row; // .card sets a column; this row is one line
                if (profile.ProfileId == session.Profile.ProfileId)
                {
                    row.style.borderTopColor = row.style.borderBottomColor =
                        row.style.borderLeftColor = row.style.borderRightColor =
                        new UnityEngine.Color(217f / 255f, 164f / 255f, 65f / 255f);
                }

                var open = profile.ProfileId == session.Profile.ProfileId;
                row.Add(Avatar(profile.ProfileId));

                var info = new VisualElement
                {
                    style = { flexDirection = FlexDirection.Column, flexGrow = 1 },
                };

                var nameLabel = new Label(profile.DisplayName);
                nameLabel.style.fontSize = 17;
                nameLabel.AddToClassList(open ? "text-accent" : "");
                info.Add(nameLabel);

                var dead = profile.Status == CharacterSheet.Dead;
                var meta = new Label((dead ? "KILLED IN ACTION" : profile.Status.Replace('_', ' ').ToUpperInvariant())
                    + "     " + profile.Money + " R");
                meta.style.fontSize = 12;
                meta.style.marginTop = 2;
                meta.AddToClassList(dead ? "text-danger" : "text-muted");
                info.Add(meta);

                row.Add(info);

                var id = profile.ProfileId;
                var name = profile.DisplayName;

                // Neither button is ever dead: pressing one always does something or says why not.
                var openButton = new Button(() =>
                {
                    if (open)
                    {
                        Say("That character is already open.");
                        return;
                    }

                    if (dead)
                    {
                        Say(name + " was killed in action, so there is nobody left to open. DELETE removes the file.");
                        return;
                    }

                    Open(id);
                })
                {
                    text = open ? "CURRENT" : dead ? "KIA" : "OPEN",
                    style = { marginRight = 8, height = 30, fontSize = 11, paddingLeft = 14, paddingRight = 14 },
                };
                openButton.AddToClassList("btn");
                openButton.AddToClassList(open ? "btn-outline" : dead ? "btn-disabled" : "btn-primary");
                row.Add(openButton);

                var deleteButton = new Button { text = "DELETE" };
                deleteButton.AddToClassList("btn");
                deleteButton.AddToClassList("btn-danger");
                deleteButton.style.height = 30;
                deleteButton.style.fontSize = 11;
                deleteButton.style.paddingLeft = 14;
                deleteButton.style.paddingRight = 14;
                var confirming = false;
                deleteButton.clicked += () =>
                {
                    if (!confirming)
                    {
                        confirming = true;
                        deleteButton.text = "SURE?";
                        Say(open
                            ? "This is the character you are playing. Deleting it moves you to another one."
                            : "Press DELETE again to remove " + name + ".");
                        return;
                    }

                    Delete(id, name, open, profiles);
                };
                row.Add(deleteButton);
                list.Add(row);
            }

            _rosterStatus = new Label("");
            _rosterStatus.AddToClassList("text-accent");
            _rosterStatus.style.whiteSpace = WhiteSpace.Normal;
            _rosterStatus.style.maxWidth = 640;
            _rosterStatus.style.marginBottom = 6;
            list.Insert(0, _rosterStatus);

            var hint = new Label("OPEN switches to that character and goes to the stash. NEW CHARACTER "
                + "below opens the sheet for a new one.");
            hint.AddToClassList("text-muted");
            hint.style.whiteSpace = WhiteSpace.Normal;
            hint.style.maxWidth = 560;
            hint.style.marginTop = 6;
            list.Add(hint);
            return list;
        }

        /// <summary>The portraits on disk, one clickable plate each; the chosen one is outlined.</summary>
        private VisualElement PortraitPicker()
        {
            var row = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginBottom = 14 },
            };

            var iconLibrary = new IconLibrary();
            var ids = iconLibrary.AvatarIds;
            if (ids.Count == 0)
            {
                row.Add(new Label("No portraits in icon_cache/avatars, so this one starts without a face."));
                return row;
            }

            if (_newAvatarIndex < 0 || _newAvatarIndex >= ids.Count)
            {
                _newAvatarIndex = 0;
            }

            for (var i = 0; i < ids.Count; i++)
            {
                var index = i;
                var plate = new Button(() =>
                {
                    _newAvatarIndex = index;
                    Build();
                });
                plate.AddToClassList("card");
                plate.style.width = 56;
                plate.style.height = 56;
                plate.style.marginRight = 8;
                plate.style.paddingLeft = 0;
                plate.style.paddingRight = 0;
                plate.style.paddingTop = 0;
                plate.style.paddingBottom = 0;

                var texture = iconLibrary.GetAvatar(ids[i]);
                if (texture != null)
                {
                    var image = new Image
                    {
                        image = texture,
                        scaleMode = ScaleMode.ScaleAndCrop,
                        pickingMode = PickingMode.Ignore,
                    };
                    image.style.position = Position.Absolute;
                    image.style.left = 1;
                    image.style.top = 1;
                    image.style.right = 1;
                    image.style.bottom = 1;
                    plate.Add(image);
                }

                if (i == _newAvatarIndex)
                {
                    plate.AddToClassList("cell-selected");
                }

                row.Add(plate);
            }

            return row;
        }

        /// <summary>
        /// The character's portrait. Clicking it walks to the next portrait on disk; the choice is kept
        /// in PlayerPrefs, so it costs no save-schema change. Without any portraits on disk the cell is
        /// simply an empty plate.
        /// </summary>
        private VisualElement Avatar(string profileId)
        {
            var iconLibrary = new IconLibrary();
            var ids = iconLibrary.AvatarIds;
            var plate = new VisualElement();
            plate.style.width = 72;
            plate.style.height = 72;
            plate.style.marginRight = 10;
            plate.style.flexShrink = 0;
            plate.AddToClassList("card");

            if (ids.Count == 0)
            {
                return plate;
            }

            var key = "Safehouse.Avatar." + profileId;
            var index = PlayerPrefs.GetInt(key, -1);
            if (index < 0)
            {
                index = Mathf.Abs(profileId.GetHashCode()) % ids.Count;
            }

            index = ((index % ids.Count) + ids.Count) % ids.Count;
            var texture = iconLibrary.GetAvatar(ids[index]);
            if (texture != null)
            {
                var image = new Image
                {
                    image = texture,
                    scaleMode = ScaleMode.ScaleAndCrop,
                    pickingMode = PickingMode.Ignore,
                };
                image.style.position = Position.Absolute;
                image.style.left = 1;
                image.style.top = 1;
                image.style.right = 1;
                image.style.bottom = 1;
                plate.Add(image);
            }

            var next = index;
            var button = new Button(() =>
            {
                PlayerPrefs.SetInt(key, (next + 1) % ids.Count);
                PlayerPrefs.Save();
                Build();
            });
            button.style.position = Position.Absolute;
            button.style.left = 0;
            button.style.top = 0;
            button.style.right = 0;
            button.style.bottom = 0;
            button.style.backgroundColor = new UnityEngine.Color(0, 0, 0, 0);
            button.style.borderTopWidth = button.style.borderBottomWidth =
                button.style.borderLeftWidth = button.style.borderRightWidth = 0;
            button.tooltip = "Click to change the portrait";
            plate.Add(button);
            return plate;
        }

        private void Say(string text)
        {
            if (_rosterStatus != null)
            {
                _rosterStatus.text = text;
            }
        }

        /// <summary>Switches to a character and goes to the stash, or says why it could not.</summary>
        private void Open(string profileId)
        {
            var gear = GearScreenController.Current;
            if (gear == null)
            {
                Say("The stash screen is not ready yet.");
                return;
            }

            var error = gear.OpenCharacter(profileId);
            if (error != null)
            {
                Say(error);
                return;
            }

            ScreenNavigator.Go("gear");
        }

        /// <summary>
        /// Removes a character. Deleting the open one first switches to another, because the session
        /// would otherwise keep writing the character that was just deleted back to disk.
        /// </summary>
        private void Delete(string profileId, string name, bool isOpen, IReadOnlyList<Profile> profiles)
        {
            var gear = GearScreenController.Current;
            var session = gear?.Session;
            if (session == null || !session.Saves)
            {
                Say("No data folder is open.");
                return;
            }

            if (isOpen)
            {
                var next = profiles.FirstOrDefault(candidate => candidate.ProfileId != profileId);
                if (next == null)
                {
                    Say("That is the only character in this folder. Create another one first.");
                    return;
                }

                var switchError = gear.OpenCharacter(next.ProfileId);
                if (switchError != null)
                {
                    Say(switchError);
                    return;
                }
            }

            try
            {
                session.DeleteCharacter(profileId);
            }
            catch (Exception error)
            {
                Say(error.Message);
                return;
            }

            Build();
            Say("Deleted " + name + ".");
        }

        private string Text(string key) => _identity.ContainsKey(key) ? _identity[key].Trim() : "";

        private void RefreshClasses()
        {
            FillClassRow(_mainRow, true);
            FillClassRow(_subRow, false);
            _skills.Clear();
            var sheet = new GundogSheet(Gundog.Abilities.ToDictionary(stat => stat.StatId, stat => Gundog.AbilityDefault),
                _main, _sub, null,
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

        private VisualElement AbilityRow(string ability)
        {
            var row = new VisualElement();
            row.AddToClassList("sheet-row");
            var name = new Label(ability.ToUpperInvariant());
            name.AddToClassList("sheet-stat");
            var value = new Label(_abilities[ability].ToString());
            value.AddToClassList("sheet-value");
            value.AddToClassList("text-mono");
            row.Add(name);
            row.Add(Step("-", () => ChangeAbility(ability, -1, value)));
            row.Add(value);
            row.Add(Step("+", () => ChangeAbility(ability, 1, value)));
            return row;
        }

        /// <summary>Abilities go up and down one point at a time, between 1 and 10.</summary>
        private void ChangeAbility(string ability, int delta, Label value)
        {
            _abilities[ability] = Math.Max(CharacterSheet.AbilityMin,
                Math.Min(CharacterSheet.AbilityMax, _abilities[ability] + delta));
            value.text = _abilities[ability].ToString();
            RefreshAbilityPool();
        }

        /// <summary>The bonus points the chosen tiers spend, out of the pool the profile allows.</summary>
        private void RefreshAbilityPool()
        {
            if (_abilityPool == null)
            {
                return;
            }

            var spent = _abilities.Values.Sum(CharacterSheet.AbilityCost);
            _abilityPool.text = "SPENT " + spent + " / " + CharacterSheet.AbilityBonusPool;
            _abilityPool.EnableInClassList("text-danger", spent > CharacterSheet.AbilityBonusPool);
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

        /// <summary>Caption plus control, in a fixed-width block, so the identity grid lines up.</summary>
        private static VisualElement Block(string label)
        {
            var block = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    width = 316,
                    marginRight = 10,
                    marginBottom = 10,
                },
            };

            var caption = new Label(label);
            caption.AddToClassList("text-label");
            caption.style.width = 96;
            caption.style.minWidth = 96;
            caption.style.fontSize = 12;
            caption.style.marginRight = 8;
            caption.style.unityTextAlign = TextAnchor.MiddleRight;
            block.Add(caption);
            return block;
        }

        /// <summary>
        /// One identity field: a text box you can type anything into, plus a ▾ that offers the presets.
        /// The menu is drawn by this screen rather than by Unity's DropdownField, whose popup is attached
        /// outside the document's tree where this document's stylesheet cannot reach it.
        /// </summary>
        private void AddIdentity(VisualElement row, string key, string label, IReadOnlyList<string> options)
        {
            _identity[key] = options[0];
            var block = Block(label);

            var field = new TextField { maxLength = Gundog.MaxLineLength };
            field.AddToClassList("sheet-field");
            field.AddToClassList("sheet-inline");
            field.value = options[0];
            field.RegisterValueChangedCallback(changed => _identity[key] = changed.newValue ?? "");
            block.Add(field);

            var presets = new Button(() => OpenPresets(field, options, value =>
            {
                field.value = value;
                _identity[key] = value;
            }))
            {
                text = "\u25be",
            };
            presets.AddToClassList("btn");
            presets.AddToClassList("btn-outline");
            presets.style.width = 28;
            presets.style.height = 32;
            presets.style.paddingLeft = 0;
            presets.style.paddingRight = 0;
            presets.style.marginLeft = 4;
            presets.tooltip = "Presets";
            block.Add(presets);

            row.Add(block);
        }

        /// <summary>A number the player nudges rather than a chip for every value.</summary>
        private void AddNumber(VisualElement row, string key, string label, int from, int to, int step)
        {
            _identity[key] = from.ToString();
            var block = Block(label);

            var value = new Label(_identity[key]);
            value.AddToClassList("sheet-value");
            value.AddToClassList("text-mono");
            value.style.width = 44;
            value.style.unityTextAlign = TextAnchor.MiddleCenter;

            block.Add(Step("-", () =>
            {
                var next = Math.Max(from, int.Parse(_identity[key]) - step);
                _identity[key] = next.ToString();
                value.text = _identity[key];
            }));
            block.Add(value);
            block.Add(Step("+", () =>
            {
                var next = Math.Min(to, int.Parse(_identity[key]) + step);
                _identity[key] = next.ToString();
                value.text = _identity[key];
            }));

            row.Add(block);
        }

        private VisualElement _presetCatcher;

        /// <summary>The preset list, drawn under the field. Any click outside it closes it.</summary>
        private void OpenPresets(VisualElement anchor, IReadOnlyList<string> options, Action<string> choose)
        {
            ClosePresets();

            _presetCatcher = new VisualElement();
            _presetCatcher.style.position = Position.Absolute;
            _presetCatcher.style.left = 0;
            _presetCatcher.style.top = 0;
            _presetCatcher.style.right = 0;
            _presetCatcher.style.bottom = 0;
            _presetCatcher.RegisterCallback<PointerDownEvent>(_ => ClosePresets());
            _root.Add(_presetCatcher);

            var menu = new VisualElement { pickingMode = PickingMode.Position };
            menu.AddToClassList("card");
            menu.style.position = Position.Absolute;
            var corner = _root.WorldToLocal(anchor.worldBound.position);
            menu.style.left = corner.x;
            menu.style.top = corner.y + anchor.worldBound.height + 2;
            menu.style.minWidth = Math.Max(180, anchor.worldBound.width);
            menu.style.maxWidth = 420;

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.maxHeight = 300;
            foreach (var option in options)
            {
                var value = option;
                var item = new Button(() =>
                {
                    choose(value);
                    ClosePresets();
                })
                {
                    text = value,
                };
                item.AddToClassList("menu-item");
                item.style.unityTextAlign = TextAnchor.MiddleLeft;
                scroll.Add(item);
            }

            menu.Add(scroll);
            _presetCatcher.Add(menu);
        }

        private void ClosePresets()
        {
            if (_presetCatcher == null)
            {
                return;
            }

            _presetCatcher.RemoveFromHierarchy();
            _presetCatcher = null;
        }

        private static string[] Range(int from, int to, int step)
        {
            var values = new List<string>();
            for (var value = from; value <= to; value += step)
            {
                values.Add(value.ToString());
            }

            return values.ToArray();
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
