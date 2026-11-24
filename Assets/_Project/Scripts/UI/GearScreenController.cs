using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Safehouse.Core;
using Safehouse.Data;
using Safehouse.UI.Sample;
using UnityEngine;
using UnityEngine.UIElements;

namespace Safehouse.UI
{
    /// <summary>
    /// Wires the GEAR screen's UXML to real game data: the STASH, the RIG / BACKPACK grids in CARRIED
    /// and the seven LOADOUT slots are real catalog items under the real placement and equip rules.
    /// Items drag between the three grids and onto (or off) the slot cards.
    ///
    /// It shows the open character (<see cref="CharacterSession"/>): every change is committed - which saves
    /// it - before the screen shows it, so a change that cannot be saved is refused and the screen keeps
    /// matching the file. The health / abilities panels are still hand-written (see the Unity project README).
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class GearScreenController : MonoBehaviour
    {
        public const string StashGridName = "stash";
        public const string RigGridName = "rig";
        public const string BackpackGridName = "backpack";

        private const float Pitch = 48f;
        private const float DragThreshold = 4f;
        private const float AutoScrollEdge = 48f;      // pointer this close to the viewport edge starts scrolling
        private const float AutoScrollSpeed = 300f;    // pixels per second while held at the edge

        private const string BinHint = "Drag an item from any grid onto the bin, then click the bin to destroy it.";
        private static readonly Color BinIdleColor = new Color(0.471f, 0.518f, 0.561f);   // rgb(120, 132, 143)
        private static readonly Color BinArmedColor = new Color(0.851f, 0.643f, 0.255f);  // rgb(217, 164, 65)

        /// <summary>One on-screen grid: the element cells live in, and the rules-side grid behind it.</summary>
        private sealed class GridView
        {
            public string Name;
            public VisualElement Element;
            public VisualElement Viewport; // what clips the grid on screen when it scrolls; null when nothing does
            public StashGrid Grid;
        }

        /// <summary>One LOADOUT slot card and the labels that show what is in it.</summary>
        private sealed class SlotView
        {
            public string Slot;
            public VisualElement Card;
            public Label Stat;
            public Label Icon;   // null for the primary-weapon row, which has no monogram
            public Label Name;
            public Label Erg;    // primary weapon only
            public Label Rcl;
            public VisualElement Art;   // the item's icon filling the card, when it has one; else null
        }

        private const string LastCharacterKey = "Safehouse.LastCharacter";

        /// <summary>Where characters are saved instead of the game's own folder. For tests.</summary>
        public static string DataFolderOverride { get; set; }

        private Dictionary<string, ItemDefinition> _catalog;
        private CharacterSession _session;
        private VisualElement _toast;
        private Label _toastText;
        private IVisualElementScheduledItem _toastTimer;
        private string _persistentMessage;
        private Label _labelCharacter;
        private Label _labelRoubles;
        private GearData _gear;
        private Loadout _loadout;
        private IconLibrary _icons;
        private IReadOnlyList<CaseDefinition> _cases;
        private LootTables _lootTables;
        private Button _openButton;
        private Button _useButton;
        private VisualElement _root;
        private readonly List<GridView> _views = new List<GridView>();
        private readonly List<SlotView> _slots = new List<SlotView>();
        private readonly Dictionary<string, Button> _cellsByInstanceId = new Dictionary<string, Button>();
        private string _selectedInstanceId;

        private Label _labelStashCells;
        private Label _labelStashValue;
        private Label _labelCarried;
        private VisualElement _trashBin;
        private Label _trashIcon;
        private Label _trashLabel;
        private string _trashInstanceId;
        private VisualElement _deathBanner;
        private Label _deathBannerText;
        private Button _equipButton;
        private VisualElement _swatchSelected;
        private Label _labelSelectedIcon;
        private Label _labelSelectedName;
        private Label _labelSelectedRarity;
        private Label _labelSelectedSub;
        private Label _labelSelectedValue;
        private Label _labelSelectedWeight;
        private Label _labelSelectedPerCell;

        private GridView StashView => _views[0];

        private void OnEnable()
        {
            _catalog = CatalogLoader.Load();
            _gear = GearLoader.Load(_catalog);
            var containers = MapLoader.LoadContainers(_catalog);
            _cases = CaseOpeningLoader.Load(_catalog, containers);
            _lootTables = new LootTables(_catalog, containers);
            _icons = new IconLibrary();

            _root = GetComponent<UIDocument>().rootVisualElement;

            // The panel scales the 1440x900 design to fit the window but keeps its proportions, so
            // a window of a different aspect ratio leaves a strip of empty space on one axis.
            // Centre the design in it rather than letting the whole strip pile up on one side.
            _root.style.alignItems = Align.Center;
            _root.style.justifyContent = Justify.Center;

            _views.Clear();
            _views.Add(new GridView
            {
                Name = StashGridName, Element = _root.Q<VisualElement>("stash-grid"),
                Viewport = _root.Q<ScrollView>("stash-scroll")?.contentViewport,
            });
            _views.Add(new GridView { Name = RigGridName, Element = _root.Q<VisualElement>("rig-grid") });
            _views.Add(new GridView { Name = BackpackGridName, Element = _root.Q<VisualElement>("backpack-grid") });

            _slots.Clear();
            foreach (var slot in LoadoutSlots.All)
            {
                var view = new SlotView
                {
                    Slot = slot,
                    Card = _root.Q<VisualElement>("slot-" + slot),
                    Stat = _root.Q<Label>("slot-" + slot + "-stat"),
                    Icon = _root.Q<Label>("slot-" + slot + "-icon"),
                    Name = _root.Q<Label>("slot-" + slot + "-name"),
                    Erg = _root.Q<Label>("slot-" + slot + "-erg"),
                    Rcl = _root.Q<Label>("slot-" + slot + "-rcl"),
                };
                _slots.Add(view);
                RegisterDrag(view.Card, () => _loadout.Get(view.Slot)?.InstanceId);
            }

            _labelStashCells = _root.Q<Label>("label-stash-cells");
            _labelStashValue = _root.Q<Label>("label-stash-value");
            _labelCarried = _root.Q<Label>("label-carried");
            _swatchSelected = _root.Q<VisualElement>("swatch-selected");
            _labelSelectedIcon = _root.Q<Label>("label-selected-icon");
            _labelSelectedName = _root.Q<Label>("label-selected-name");
            _labelSelectedRarity = _root.Q<Label>("label-selected-rarity");
            _labelSelectedSub = _root.Q<Label>("label-selected-sub");
            _labelSelectedValue = _root.Q<Label>("label-selected-value");
            _labelSelectedWeight = _root.Q<Label>("label-selected-weight");
            _labelSelectedPerCell = _root.Q<Label>("label-selected-percell");

            _labelCharacter = _root.Q<Label>("label-character");
            _labelRoubles = _root.Q<Label>("label-roubles");
            _root.Q<Button>("character-prev").clicked += () => CycleCharacter(-1);
            _root.Q<Button>("character-next").clicked += () => CycleCharacter(1);

            _trashBin = _root.Q<VisualElement>("stash-bin");
            _trashIcon = _root.Q<Label>("stash-bin-icon");
            _trashLabel = _root.Q<Label>("stash-bin-label");
            if (_trashBin != null)
            {
                _trashBin.tooltip = BinHint;
                _trashBin.RegisterCallback<PointerDownEvent>(_ => OnBinClicked());
            }

            _deathBanner = _root.Q<VisualElement>("death-banner");
            _deathBannerText = _root.Q<Label>("death-banner-text");

            CreateToast();
            OpenSession();
            ApplyProfile();

            // The screens are separate documents; the top bar's tabs move between them.
            Current = this;
            ScreenNavigator.Register("gear", ShowScreen, HideScreen);
            _root.Q<Button>("navtab-character").clicked += () => ScreenNavigator.Go("character");
            _root.Q<Button>("navtab-traders").clicked += () => ScreenNavigator.Go("traders");
            _root.Q<Button>("navtab-hideout").clicked += () => ScreenNavigator.Go("hideout");
            _root.Q<Button>("navtab-settings").clicked += () => ScreenNavigator.Go("settings");
            _root.Q<Button>("navtab-scavenge").clicked += () => ScreenNavigator.Go("scavenge");
            _root.Q<Button>("navtab-quests").clicked += () => ScreenNavigator.Go("quests");

            // The toolbar and the bottom bar were drawn for the design but never wired. They now do
            // what they say: DEPLOY opens the expedition screen, TO HIDEOUT the hideout, EQUIP wears
            // the selected item in its own slot, SORT repacks the stash, the chips filter it, and
            // SAVE PRESET exports the current loadout and stash as JSON beside the save folder.
            _root.Q<Button>("btn-deploy").clicked += () => ScreenNavigator.Go("scavenge");
            _root.Q<Button>("btn-to-hideout").clicked += () => ScreenNavigator.Go("hideout");
            _useButton = _root.Q<Button>("btn-use");
            _useButton.clicked += UseSelectedMed;
            _openButton = _root.Q<Button>("btn-open");
            _openButton.clicked += OpenSelectedCase;
            _equipButton = _root.Q<Button>("btn-equip");
            _equipButton.clicked += EquipSelected;
            _root.Q<Button>("btn-sort").clicked += SortStash;
            _root.Q<Button>("btn-save-preset").clicked += SavePreset;
            _filterAll = _root.Q<Button>("filter-all");
            _filterMeds = _root.Q<Button>("filter-meds");
            _filterBarter = _root.Q<Button>("filter-barter");
            _filterAll.clicked += () => SetStashFilter("ALL");
            _filterMeds.clicked += () => SetStashFilter("MEDS");
            _filterBarter.clicked += () => SetStashFilter("BARTER");

            // The game opens on the character list, not on the stash: this screen is what opens the
            // data folder, so it starts hidden and hands over once every screen has registered.
            HideScreen();
            _root.schedule.Execute(() => ScreenNavigator.Go("character")).StartingIn(0);
        }

        private void OnDisable()
        {
            if (Current == this)
            {
                Current = null;
            }

            ScreenNavigator.Unregister("gear");
            _session?.Dispose(); // lets the next copy of the game (or the next test) use the data folder
            _session = null;
        }

        /// <summary>The GEAR screen that is open, for the other screens to reach the character it holds.</summary>
        public static GearScreenController Current { get; private set; }

        /// <summary>The open character and how it is saved. Shared with the TRADERS screen, which trades for the same character.</summary>
        public CharacterSession Session => _session;

        private void ShowScreen()
        {
            _root.style.display = DisplayStyle.Flex;
            if (_session != null)
            {
                ApplyProfile(); // the character may have traded on the other screen
            }
        }

        private void HideScreen()
        {
            _root.style.display = DisplayStyle.None;
        }

        // ---- the open character ----

        private void OpenSession()
        {
            var notices = new List<string>();
            var folder = DataFolderOverride ?? ProfileRepository.DefaultFolder;
            var preferred = DataFolderOverride == null ? PlayerPrefs.GetString(LastCharacterKey, null) : null;
            try
            {
                _session = CharacterSession.Open(_catalog, folder, () => SampleCharacter.Build(_catalog), preferred, notices);
            }
            catch (StorageException error)
            {
                // A screen that works but cannot save, with a warning that stays up, beats a blank one.
                _session = CharacterSession.Unsaved(SampleCharacter.Build(_catalog));
                _persistentMessage = "Saving is unavailable - changes will NOT be kept.\n" + error.Message;
                ShowMessage(_persistentMessage, persistent: true);
                return;
            }

            if (notices.Count > 0)
            {
                ShowMessage(string.Join("\n", notices));
            }
        }

        /// <summary>
        /// What the two carried grids are. The grids themselves always exist on the profile, so an
        /// empty rig slot still shows a 6x4 box - the header is what tells the player nothing is worn,
        /// and the name comes from the item actually equipped rather than from the design mock-up.
        /// </summary>
        private void ApplyCarried(Profile profile)
        {
            var rig = _loadout.Get(LoadoutSlots.Rig);
            var backpack = _loadout.Get(LoadoutSlots.Backpack);

            var rigLabel = _root.Q<Label>("label-rig-grid");
            if (rigLabel != null)
            {
                rigLabel.text = rig == null ? "RIG — NOTHING WORN" : "RIG — " + NameOf(rig.ItemId);
                rigLabel.EnableInClassList("text-muted", rig == null);
            }

            var backpackLabel = _root.Q<Label>("label-backpack-grid");
            if (backpackLabel != null)
            {
                backpackLabel.text = backpack == null
                    ? "BACKPACK — NOTHING WORN"
                    : "BACKPACK — " + NameOf(backpack.ItemId);
                backpackLabel.EnableInClassList("text-muted", backpack == null);
            }

            // The lattice is an element with a size of its own; without this it keeps the size the UXML
            // declared, so a resized grid showed the same squares with a different number above it.
            var rigLattice = _root.Q<VisualElement>("rig-grid");
            if (rigLattice != null)
            {
                rigLattice.style.width = profile.Rig.StashWidth * Pitch;
                rigLattice.style.height = profile.Rig.StashHeight * Pitch;
            }

            var backpackLattice = _root.Q<VisualElement>("backpack-grid");
            if (backpackLattice != null)
            {
                backpackLattice.style.width = profile.Backpack.StashWidth * Pitch;
                backpackLattice.style.height = profile.Backpack.StashHeight * Pitch;
            }

            var rigSize = _root.Q<Label>("label-rig-size");
            if (rigSize != null)
            {
                rigSize.text = profile.Rig.StashWidth + " × " + profile.Rig.StashHeight;
            }

            var backpackSize = _root.Q<Label>("label-backpack-size");
            if (backpackSize != null)
            {
                backpackSize.text = profile.Backpack.StashWidth + " × " + profile.Backpack.StashHeight;
            }
        }

        private string NameOf(string itemId) =>
            itemId != null && _catalog.TryGetValue(itemId, out var item) ? item.Name.ToUpperInvariant() : "?";

        /// <summary>The seven body parts, the abilities and the bottom bar, from the open character.</summary>
        private void ApplyVitals(Profile profile)
        {
            (string Part, string Element)[] parts =
            {
                ("head", "bp-head"), ("thorax", "bp-thorax"), ("stomach", "bp-stomach"),
                ("left_arm", "bp-arm-l"), ("right_arm", "bp-arm-r"),
                ("left_leg", "bp-leg-l"), ("right_leg", "bp-leg-r"),
            };

            var injured = new List<string>();
            foreach (var (part, element) in parts)
            {
                var label = _root.Q<Label>(element);
                if (label == null)
                {
                    continue;
                }

                var have = profile.BodyParts.TryGetValue(part, out var current) ? current : 0;
                var max = CharacterSheet.MaxHealthOf(part);
                label.text = have + " / " + max;

                var ratio = max <= 0 ? 0f : have / (float)max;
                label.EnableInClassList("text-good", ratio >= 0.999f);
                label.EnableInClassList("text-warn", ratio < 0.999f && ratio > 0f);
                label.EnableInClassList("text-danger", ratio <= 0f);
                if (ratio < 0.999f)
                {
                    injured.Add(part.Replace('_', ' ').ToUpperInvariant());
                }
            }

            var total = _root.Q<Label>("bp-total");
            if (total != null)
            {
                total.text = profile.Health.ToString();
            }

            // The panel shows the profile's own six; CharacterSheet.Abilities has eight and the
            // vocabulary is not the Gundog sheet's (that one is the class sheet, shown on CHARACTER).
            (string Ability, string Element)[] abilities =
            {
                ("strength", "ab-strength"), ("dexterity", "ab-dexterity"), ("agility", "ab-agility"),
                ("intelligence", "ab-intelligence"), ("charisma", "ab-charisma"), ("sense", "ab-sense"),
            };

            foreach (var (ability, element) in abilities)
            {
                var label = _root.Q<Label>(element);
                if (label == null)
                {
                    continue;
                }

                var value = profile.Abilities.TryGetValue(ability, out var number)
                    ? number
                    : CharacterSheet.AbilityBase;
                label.text = value.ToString();
                label.EnableInClassList("text-good", value >= 7);
                label.EnableInClassList("text-danger", value <= 3);
            }

            var range = _root.Q<Label>("ab-range");
            if (range != null)
            {
                range.text = CharacterSheet.AbilityMin + " – " + CharacterSheet.AbilityMax;
            }

            var weight = 0.0;
            foreach (var grid in new[] { profile.Stash, profile.Rig, profile.Backpack })
            {
                PlacementRules.StashTotals(grid, _catalog, out _, out var grams, out _);
                weight += grams;
            }

            var weightLabel = _root.Q<Label>("load-weight");
            if (weightLabel != null)
            {
                weightLabel.text = weight.ToString("0.0");
            }

            var weightMax = _root.Q<Label>("load-weight-max");
            if (weightMax != null)
            {
                weightMax.text = "/ " + ExpeditionRules.CarryLimit(profile, _gear) + " KG";
            }

            var hp = _root.Q<Label>("load-hp");
            if (hp != null)
            {
                hp.text = profile.Health.ToString();
                hp.EnableInClassList("text-good", profile.Health >= CharacterSheet.MaxHealth);
                hp.EnableInClassList("text-danger", profile.Health < CharacterSheet.MaxHealth / 3);
            }

            var hpMax = _root.Q<Label>("load-hp-max");
            if (hpMax != null)
            {
                hpMax.text = "/ " + CharacterSheet.MaxHealth + " HP";
            }

            // Bleeding and fractures are separate from missing HP: a character can be at full health and
            // still be bleeding out, which is exactly the state this line used to call "no injuries".
            var flags = new List<string>();
            foreach (var pair in profile.Conditions)
            {
                foreach (var name in pair.Value)
                {
                    var part = pair.Key.Replace('_', ' ').ToUpperInvariant();
                    var label = name.Replace('_', ' ').ToUpperInvariant();
                    flags.Add(part + " " + label);
                    if (!injured.Contains(part))
                    {
                        injured.Add(part);
                    }
                }
            }

            var condition = _root.Q<Label>("load-condition");
            if (condition != null)
            {
                var words = flags.Count > 0 ? flags : injured;
                condition.text = words.Count == 0 ? "NO INJURIES" : string.Join("  ·  ", words);
                condition.EnableInClassList("text-danger", words.Count > 0);
                condition.EnableInClassList("text-muted", words.Count == 0);
            }
        }

        /// <summary>Loads the open character into the screen: its grids (sized to them), its gear, its name.</summary>
        private void ApplyProfile()
        {
            var profile = _session.Profile;
            _views[0].Grid = profile.Stash;
            _views[1].Grid = profile.Rig;
            _views[2].Grid = profile.Backpack;
            _loadout = profile.Loadout;
            _labelRoubles.text = Spaced(profile.Money);
            foreach (var view in _views)
            {
                view.Element.style.width = view.Grid.StashWidth * Pitch;
                view.Element.style.height = view.Grid.StashHeight * Pitch;
                view.Element.MarkDirtyRepaint();
            }

            _selectedInstanceId = null;
            CancelTrash(); // another character's item must never be left waiting in the bin
            RefreshAll();
            ClearSelection();

            var highestValueId = StashView.Grid.Stash
                .OrderByDescending(instance => _catalog[instance.ItemId].BaseValue)
                .Select(instance => instance.InstanceId)
                .FirstOrDefault();
            if (highestValueId != null)
            {
                SelectItem(highestValueId);
            }

            ApplyCarried(profile);

            ApplyVitals(profile);

            ApplyDeathState(profile);

            _labelCharacter.text = profile.DisplayName.ToUpperInvariant()
                + (profile.Status == CharacterSheet.Active ? "" : " · " + profile.Status.Replace('_', ' ').ToUpperInvariant());
            if (_session.Saves && DataFolderOverride == null)
            {
                PlayerPrefs.SetString(LastCharacterKey, profile.ProfileId); // reopen this one next time
            }
        }

        private void ClearSelection()
        {
            _labelSelectedIcon.text = "?";
            _labelSelectedName.text = "Select an item";
            _labelSelectedRarity.text = "";
            _labelSelectedSub.text = "";
            _labelSelectedValue.text = "—";
            _labelSelectedWeight.text = "—";
            _labelSelectedPerCell.text = "—";
            _swatchSelected.style.backgroundImage = StyleKeyword.None;
            RefreshActions();
        }

        /// <summary>
        /// USE and OPEN are only on screen while the selected item can use them, so the row is not
        /// cluttered with buttons that would only answer "that is not a medkit". Neither shows for a dead
        /// character: there is nothing left to use on them.
        /// </summary>
        private void RefreshActions()
        {
            var dead = _session != null && _session.Profile.Status == CharacterSheet.Dead;
            var itemId = _selectedInstanceId == null ? null : ItemIdOf(_selectedInstanceId);
            var isMed = itemId != null && HealingRules.IsMed(_gear, itemId);
            var isCase = itemId != null && CaseRules.Find(_cases, itemId) != null;

            if (_useButton != null)
            {
                _useButton.style.display = isMed && !dead ? DisplayStyle.Flex : DisplayStyle.None;
            }

            if (_openButton != null)
            {
                _openButton.style.display = isCase && !dead ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        /// <summary>
        /// Dead is dead. The screen still shows what the character had - the stash is worth looking at,
        /// and the roster still lists them - but nothing here can change them again: no medkit brings a
        /// head or thorax back, so USE, OPEN, EQUIP and the bin are hidden, and a strip at the top says
        /// why. DELETE on the character screen is the only way out.
        /// </summary>
        private void ApplyDeathState(Profile profile)
        {
            var dead = profile.Status == CharacterSheet.Dead;

            if (_deathBanner != null)
            {
                _deathBanner.style.display = dead ? DisplayStyle.Flex : DisplayStyle.None;
            }

            if (dead && _deathBannerText != null)
            {
                _deathBannerText.text = profile.DisplayName + " is dead. A head or thorax at zero is not a "
                    + "knockdown and nothing treats it, so this screen cannot change them: no healing, no gear, "
                    + "no bin, and no sending out. DELETE on the character screen removes them for good.";
            }

            if (_trashBin != null)
            {
                _trashBin.style.display = dead ? DisplayStyle.None : DisplayStyle.Flex;
            }

            if (_equipButton != null)
            {
                _equipButton.style.display = dead ? DisplayStyle.None : DisplayStyle.Flex;
            }

            RefreshActions();
        }

        /// <summary>The open character's name.</summary>
        public string CharacterName => _session.Profile.DisplayName;

        /// <summary>The open character's id.</summary>
        public string CharacterId => _session.Profile.ProfileId;

        /// <summary>Opens another saved character. Returns why it could not, or null.</summary>
        public string OpenCharacter(string profileId)
        {
            var notices = new List<string>();
            try
            {
                _session.Switch(profileId, notices);
            }
            catch (StorageException error)
            {
                ShowMessage(error.Message);
                return error.Message;
            }

            ApplyProfile();
            if (notices.Count > 0)
            {
                ShowMessage(string.Join("\n", notices));
            }

            return null;
        }

        private void CycleCharacter(int step)
        {
            var characters = _session.Characters();
            if (characters.Count < 2)
            {
                return;
            }

            var index = characters.ToList().FindIndex(profile => profile.ProfileId == _session.Profile.ProfileId);
            OpenCharacter(characters[(index + step + characters.Count) % characters.Count].ProfileId);
        }

        // ---- messages ----

        private void CreateToast()
        {
            _toast = new VisualElement { pickingMode = PickingMode.Ignore };
            _toast.AddToClassList("toast");
            _toast.style.display = DisplayStyle.None;
            _toastText = new Label { pickingMode = PickingMode.Ignore };
            _toastText.AddToClassList("toast-label");
            _toast.Add(_toastText);
            (_root.Q(className: "gear-root") ?? _root).Add(_toast);
        }

        /// <summary>Shows a message over the bottom of the screen for a few seconds (or until replaced, when
        /// <paramref name="persistent"/>). A standing "not saving" warning comes back after any other message.</summary>
        private void ShowMessage(string text, bool persistent = false)
        {
            _toastText.text = text;
            _toast.style.display = DisplayStyle.Flex;
            _toastTimer?.Pause();
            if (!persistent)
            {
                _toastTimer = _toast.schedule.Execute(() =>
                {
                    if (_persistentMessage != null)
                    {
                        _toastText.text = _persistentMessage;
                    }
                    else
                    {
                        _toast.style.display = DisplayStyle.None;
                    }
                }).StartingIn(6000);
            }
        }

        /// <summary>Whether a message is showing, and what it says. For tests.</summary>
        public bool MessageVisible => _toast != null && _toast.style.display == DisplayStyle.Flex;

        public string MessageText => _toastText?.text;

        /// <summary>Whether changes are being written to disk.</summary>
        public bool Saving => _session != null && _session.Saves;

        /// <summary>
        /// Saves the profile with these grids (and gear, when given) in place of the current ones and only then
        /// makes it current. Returns why it could not be saved, having shown that to the player; the caller must
        /// then change nothing.
        /// </summary>
        private string Persist(IDictionary<GridView, StashGrid> changes, Loadout loadout = null)
        {
            StashGrid Pick(GridView view) => changes.TryGetValue(view, out var grid) ? grid : view.Grid;
            try
            {
                _session.Commit(_session.Profile.With(
                    stash: Pick(_views[0]), rig: Pick(_views[1]), backpack: Pick(_views[2]), loadout: loadout));
                return null;
            }
            catch (StorageException error)
            {
                ShowMessage(error.Message);
                return error.Message;
            }
        }

        // ---- building the screen from the current state ----

        private void BuildCells()
        {
            _cellsByInstanceId.Clear();

            foreach (var view in _views)
            {
                view.Element.Clear();
                foreach (var instance in view.Grid.Stash)
                {
                    var cell = CreateCell(instance);
                    view.Element.Add(cell);
                    _cellsByInstanceId[instance.InstanceId] = cell;
                }
            }
        }

        private Button CreateCell(ItemInstance instance)
        {
            var item = _catalog[instance.ItemId];
            PlacementRules.Footprint(item, instance.Rotation, out var width, out var height);

            var cell = new Button { name = "cell-" + instance.InstanceId };
            cell.AddToClassList("cell");
            PositionCell(cell, instance.X, instance.Y, width, height);
            cell.style.borderTopColor = cell.style.borderBottomColor =
                cell.style.borderLeftColor = cell.style.borderRightColor = RarityPalette.For(item.Rarity);

            var icon = _icons.Get(item.ItemId);
            if (icon != null)
            {
                // The art stands in for the placeholder monogram and the name, as in the Python build.
                cell.Add(IconImage(item, icon, instance.Rotation));
            }
            else
            {
                cell.Add(MonogramLabel(item));

                if (width >= 2 || height >= 2)
                {
                    var label = new Label(item.Name.ToUpperInvariant());
                    label.AddToClassList("cell-label");
                    cell.Add(label);
                }
            }

            var instanceId = instance.InstanceId;
            cell.clicked += () => SelectItem(instanceId);
            RegisterDrag(cell, () => instanceId);
            return cell;
        }

        internal static Label MonogramLabel(ItemDefinition item) =>
            new Label(Monogram(item.Category))
            {
                style =
                {
                    alignSelf = Align.Center,
                    color = RarityPalette.For(item.Rarity),
                    unityFontStyleAndWeight = FontStyle.Bold,
                },
            };

        /// <summary>
        /// The item's art filling a cell. Icons are drawn upright at the item's own size, so a turned item
        /// gets the same image spun 90 degrees about the middle of its (swapped) cell rather than a
        /// second, pre-rotated file. Sized to the area inside the cell's 1px border.
        /// </summary>
        internal static Image IconImage(ItemDefinition item, Texture2D icon, int rotation)
        {
            PlacementRules.Footprint(item, rotation, out var cellsWide, out var cellsHigh);
            float imageWidth = item.Width * Pitch - 4;
            float imageHeight = item.Height * Pitch - 4;
            float innerWidth = cellsWide * Pitch - 4;
            float innerHeight = cellsHigh * Pitch - 4;

            var image = new Image { image = icon, scaleMode = ScaleMode.StretchToFill, pickingMode = PickingMode.Ignore };
            image.style.position = Position.Absolute;
            image.style.width = imageWidth;
            image.style.height = imageHeight;
            image.style.left = (innerWidth - imageWidth) / 2f;
            image.style.top = (innerHeight - imageHeight) / 2f;
            if (rotation == 90)
            {
                image.style.rotate = new Rotate(new Angle(90, AngleUnit.Degree));
            }

            return image;
        }

        /// <summary>
        /// A slot card's item shown the way the inventory shows it - rarity border, dark fill, the picture - but
        /// filling the whole card instead of a grid cell. Nothing is ever cropped.
        ///
        /// Text-free art (<paramref name="textFree"/>) is scaled to fit whole at its own proportions: it has no
        /// background box, so the card's dark fill around it looks like part of the slot, not like empty bars,
        /// and every item gets the same margin. The fallback, the game's inventory icon, has its short name
        /// baked in and a box of its own, so it is stretched to the card instead (the wide primary-weapon row,
        /// <paramref name="stretchAlways"/> false, is fitted whole when the shapes differ a lot; see
        /// <see cref="IconLibrary.FitInto"/>).
        /// </summary>
        private static VisualElement SlotArt(bool textFree, bool stretchAlways, ItemDefinition item, Texture2D picture)
        {
            var art = new VisualElement { pickingMode = PickingMode.Ignore };
            art.AddToClassList("cell");
            art.AddToClassList("slot-art-image");
            art.style.borderTopColor = art.style.borderBottomColor =
                art.style.borderLeftColor = art.style.borderRightColor = RarityPalette.For(item.Rarity);
            var image = new Image
            {
                image = picture,
                scaleMode = !textFree && stretchAlways ? ScaleMode.StretchToFill : ScaleMode.ScaleToFit,
                pickingMode = PickingMode.Ignore,
                style = { flexGrow = 1 },
            };
            art.Add(image);

            if (textFree)
            {
                art.style.paddingTop = art.style.paddingBottom = 5;
                art.style.paddingLeft = art.style.paddingRight = 5;
            }
            else if (!stretchAlways)
            {
                // That row's size is only known after layout, so choose the fit then (and if it resizes).
                art.RegisterCallback<GeometryChangedEvent>(_ =>
                {
                    if (art.layout.width > 0 && art.layout.height > 0)
                    {
                        image.scaleMode = IconLibrary.FitInto(
                            (float)picture.width / picture.height, art.layout.width / art.layout.height);
                    }
                });
            }

            return art;
        }

        internal static void PositionCell(VisualElement cell, int x, int y, int width, int height)
        {
            cell.style.left = x * Pitch + 2;
            cell.style.top = y * Pitch + 2;
            cell.style.width = width * Pitch - 2;
            cell.style.height = height * Pitch - 2;
        }

        /// <summary>Fills each slot card from the loadout: the item's name and the one number that matters for it.</summary>
        private void RenderSlots()
        {
            foreach (var view in _slots)
            {
                view.Card.RemoveFromClassList("slot-selected");
                view.Card.RemoveFromClassList("slot-art");
                view.Art?.RemoveFromHierarchy();
                view.Art = null;
                var worn = _loadout.Get(view.Slot);
                if (worn == null)
                {
                    view.Name.text = "Empty";
                    view.Stat.text = "";
                    if (view.Icon != null) { view.Icon.text = ""; }
                    if (view.Erg != null) { view.Erg.text = ""; }
                    if (view.Rcl != null) { view.Rcl.text = ""; }
                    continue;
                }

                var item = _catalog[worn.ItemId];
                view.Name.text = item.Name;
                if (view.Icon != null)
                {
                    view.Icon.text = Monogram(item.Category);
                    view.Icon.style.color = RarityPalette.For(item.Rarity);
                }

                // With art the card shows only that (the text underneath is hidden by USS); the name
                // and stats are on the detail panel once it is selected.
                var textFree = _icons.GetArt(worn.ItemId);
                var icon = textFree ?? _icons.Get(worn.ItemId);
                if (icon != null)
                {
                    view.Art = SlotArt(textFree != null, view.Slot != LoadoutSlots.Primary, item, icon);
                    view.Card.Add(view.Art);
                    view.Card.AddToClassList("slot-art");
                }

                view.Stat.text = SlotStat(view.Slot, worn.ItemId);
                if (view.Erg != null)
                {
                    _gear.Weapons.TryGetValue(worn.ItemId, out var weapon);
                    view.Erg.text = weapon == null ? "" : $"ERG {weapon.Ergonomics}";
                    view.Rcl.text = weapon == null ? "" : $"RCL {weapon.Recoil}";
                }
            }

            if (_selectedInstanceId != null && _loadout.FindByInstance(_selectedInstanceId) != null)
            {
                MarkSlotSelected(SlotViewFor(_loadout.FindByInstance(_selectedInstanceId).Slot));
            }
        }

        private string SlotStat(string slot, string itemId)
        {
            switch (slot)
            {
                case LoadoutSlots.Primary:
                    return _gear.Weapons.TryGetValue(itemId, out var weapon)
                        ? weapon.Caliber.Replace("Caliber", "") : "";
                case LoadoutSlots.Ammo:
                    return _gear.Ammo.TryGetValue(itemId, out var round) ? round.Damage.ToString(CultureInfo.InvariantCulture) : "";
                case LoadoutSlots.Meds:
                    return _gear.Meds.TryGetValue(itemId, out var med) ? $"{med.Uses} USES" : "";
                case LoadoutSlots.Rig:
                case LoadoutSlots.Backpack:
                    return _gear.Equipment.TryGetValue(itemId, out var carrier)
                        ? carrier.Capacity.ToString(CultureInfo.InvariantCulture) : "";
                default: // helmet, armor
                    return _gear.Equipment.TryGetValue(itemId, out var armor)
                        ? armor.ArmorClass.ToString(CultureInfo.InvariantCulture) : "";
            }
        }

        private static void MarkSlotSelected(SlotView view)
        {
            view.Card.AddToClassList("slot-selected");
            view.Art?.AddToClassList("cell-selected"); // same thicker border a selected stash cell gets
        }

        private SlotView SlotViewFor(string slot) => _slots.First(view => view.Slot == slot);

        /// <summary>Redraws everything that depends on which items are where, keeping the selection.</summary>
        private void RefreshAll()
        {
            ApplyCarried(_session.Profile); // the carried headers follow the grids
            BuildCells();
            ApplyStashFilter();
            RenderSlots();
            UpdateHeaders();
            if (_selectedInstanceId != null && ItemIdOf(_selectedInstanceId) != null)
            {
                SelectItem(_selectedInstanceId);
            }
        }

        private void UpdateHeaders()
        {
            PlacementRules.StashTotals(StashView.Grid, _catalog, out var cells, out _, out var value);
            _labelStashCells.text = $"{cells} / {StashView.Grid.StashWidth * StashView.Grid.StashHeight} CELLS";
            _labelStashValue.text = Spaced(value);

            var carried = 0;
            var capacity = 0;
            foreach (var view in _views.Skip(1))
            {
                PlacementRules.StashTotals(view.Grid, _catalog, out var used, out _, out _);
                carried += used;
                capacity += view.Grid.StashWidth * view.Grid.StashHeight;
            }

            _labelCarried.text = $"{carried} / {capacity}";
        }

        // ---- looking items up: an item is in exactly one grid, or worn ----

        private GridView TryViewOf(string instanceId) =>
            _views.FirstOrDefault(view => view.Grid.Stash.Any(candidate => candidate.InstanceId == instanceId));

        private GridView ViewOf(string instanceId) =>
            TryViewOf(instanceId) ?? throw new ValidationException($"{instanceId} is not in a grid.");

        /// <summary>The item type of an instance wherever it is, or null if there is no such instance.</summary>
        private string ItemIdOf(string instanceId)
        {
            var view = TryViewOf(instanceId);
            if (view != null)
            {
                return view.Grid.Stash.First(candidate => candidate.InstanceId == instanceId).ItemId;
            }

            return _loadout.FindByInstance(instanceId)?.ItemId;
        }

        /// <summary>How an item in a grid is turned; a worn item has no orientation, so 0.</summary>
        private int RotationOf(string instanceId) =>
            TryViewOf(instanceId)?.Grid.Stash.First(candidate => candidate.InstanceId == instanceId).Rotation ?? 0;

        /// <summary>Which grid ("stash", "rig" or "backpack") holds this item; null if it is worn.</summary>
        public string GridOf(string instanceId) => TryViewOf(instanceId)?.Name;

        /// <summary>Which loadout slot this item is worn in; null if it is in a grid.</summary>
        public string SlotOf(string instanceId) => _loadout.FindByInstance(instanceId)?.Slot;

        /// <summary>The item type worn in a slot, or null when the slot is empty.</summary>
        public string EquippedItemIn(string slot) => _loadout.Get(slot)?.ItemId;

        /// <summary>The instance id of what is worn in a slot, or null when it is empty.</summary>
        public string EquippedInstanceIn(string slot) => _loadout.Get(slot)?.InstanceId;

        /// <summary>The instance id of the first item of this type sitting in any grid, or null. For callers
        /// (tests, mostly) that know what they want by item type rather than by instance.</summary>
        public string FirstInstanceOf(string itemId) =>
            _views.SelectMany(view => view.Grid.Stash).FirstOrDefault(instance => instance.ItemId == itemId)?.InstanceId;

        // ---- changing things ----

        /// <summary>
        /// Moves (and optionally turns) an item within the grid it is already in. Returns why the move
        /// was refused, or null on success - the same contract as <see cref="PlacementRules.PlacementError"/>.
        /// Public so tests can drive a move without simulating a pointer drag.
        /// </summary>
        public string TryMoveItem(string instanceId, int x, int y, int rotation)
        {
            var view = TryViewOf(instanceId);
            return view == null ? "That item is worn, not in a grid." : TryTransfer(instanceId, view, x, y, rotation);
        }

        /// <summary>Moves an item into the named grid ("stash", "rig" or "backpack") at x,y. Same
        /// contract as <see cref="TryMoveItem"/>; nothing changes when it is refused.</summary>
        public string TryTransferItem(string instanceId, string gridName, int x, int y, int rotation)
        {
            var target = _views.FirstOrDefault(view => view.Name == gridName);
            if (target == null)
            {
                return $"Unknown grid: {gridName}.";
            }

            return TryViewOf(instanceId) == null
                ? "That item is worn: take it off first."
                : TryTransfer(instanceId, target, x, y, rotation);
        }

        private string TryTransfer(string instanceId, GridView target, int x, int y, int rotation)
        {
            var blocked = ProfileRules.StashEditError(_session.Profile);
            if (blocked != null)
            {
                ShowMessage(blocked);
                return blocked;
            }

            var source = ViewOf(instanceId);
            StashGrid newSource;
            StashGrid newTarget;
            try
            {
                (newSource, newTarget) = PlacementRules.Transfer(
                    source.Grid, target.Grid, _catalog, instanceId, x, y, rotation);
            }
            catch (ValidationException error)
            {
                return error.Message;
            }

            var saveError = Persist(new Dictionary<GridView, StashGrid> { [source] = newSource, [target] = newTarget });
            if (saveError != null)
            {
                return saveError;
            }

            source.Grid = newSource;
            target.Grid = newTarget;

            var moved = target.Grid.Stash.First(candidate => candidate.InstanceId == instanceId);
            PlacementRules.Footprint(_catalog[moved.ItemId], moved.Rotation, out var width, out var height);
            var cell = _cellsByInstanceId[instanceId];
            if (source != target)
            {
                target.Element.Add(cell); // the new list order puts it last, which is also drawn on top
            }

            PositionCell(cell, moved.X, moved.Y, width, height);
            UpdateHeaders();
            if (_selectedInstanceId == instanceId)
            {
                SelectItem(instanceId); // the detail line shows the footprint, which a turn changes
            }

            AudioDirector.Play(Sound.Move);
            return null;
        }

        /// <summary>Turns an item 90 degrees where it sits. Refused (returns the reason) if the turned
        /// shape would not fit there; a square item, or a worn one, has nothing to turn.</summary>
        public string TryRotateItem(string instanceId)
        {
            var view = TryViewOf(instanceId);
            if (view == null)
            {
                return null;
            }

            var instance = view.Grid.Stash.First(candidate => candidate.InstanceId == instanceId);
            var item = _catalog[instance.ItemId];
            if (item.Width == item.Height)
            {
                return null;
            }

            return TryMoveItem(instanceId, instance.X, instance.Y, instance.Rotation == 0 ? 90 : 0);
        }

        /// <summary>Why this item cannot be worn in this slot, or null when it can (or is already there).</summary>
        private string SlotDropError(string instanceId, string slot)
        {
            var worn = _loadout.FindByInstance(instanceId);
            if (worn != null)
            {
                return worn.Slot == slot ? null : $"That is worn in the {worn.Slot} slot already.";
            }

            var itemId = ItemIdOf(instanceId);
            var expected = _gear.SlotOf(itemId);
            if (expected == null)
            {
                return $"{_catalog[itemId].Name} cannot be equipped.";
            }

            if (expected != slot)
            {
                return $"{_catalog[itemId].Name} goes in the {expected} slot.";
            }

            var view = ViewOf(instanceId);
            return LoadoutRules.EquipError(_gear, _catalog, _loadout, view.Grid, StashView.Grid,
                _views[1].Grid, _views[2].Grid, instanceId);
        }

        /// <summary>
        /// Wears an item from whichever grid holds it. What the slot held goes back to the stash. Returns
        /// why it was refused, or null; nothing changes when it is refused.
        /// </summary>
        public string TryEquipItem(string instanceId, string slot)
        {
            var blocked = ProfileRules.GearEditError(_session.Profile);
            if (blocked != null)
            {
                ShowMessage(blocked);
                return blocked;
            }

            var error = SlotDropError(instanceId, slot);
            if (error != null)
            {
                return error;
            }

            if (_loadout.FindByInstance(instanceId) != null)
            {
                return null; // already worn in that slot
            }

            var source = ViewOf(instanceId);
            var result = LoadoutRules.Equip(_gear, _catalog, _loadout, source.Grid, StashView.Grid,
                _views[1].Grid, _views[2].Grid, instanceId);
            var saveError = Persist(new Dictionary<GridView, StashGrid>
            {
                [source] = result.Source,
                [StashView] = result.Stash,
                [_views[1]] = result.Rig,
                [_views[2]] = result.Backpack,
            }, result.Loadout);
            if (saveError != null)
            {
                return saveError;
            }

            _loadout = result.Loadout;
            source.Grid = result.Source;
            StashView.Grid = result.Stash;
            _views[1].Grid = result.Rig;
            _views[2].Grid = result.Backpack;
            RefreshAll();
            AudioDirector.Play(Sound.Equip);
            return null;
        }

        /// <summary>Takes a worn item off into the named grid at x,y. Taking off a weapon also unloads
        /// its ammunition into the same grid. Returns why it was refused, or null.</summary>
        public string TryUnequipItem(string instanceId, string gridName, int x, int y, int rotation)
        {
            var blocked = ProfileRules.GearEditError(_session.Profile);
            if (blocked != null)
            {
                ShowMessage(blocked);
                return blocked;
            }

            var worn = _loadout.FindByInstance(instanceId);
            if (worn == null)
            {
                return "That item is not worn.";
            }

            var target = _views.FirstOrDefault(view => view.Name == gridName);
            if (target == null)
            {
                return $"Unknown grid: {gridName}.";
            }

            (Loadout Loadout, StashGrid Target, StashGrid Rig, StashGrid Backpack) result;
            try
            {
                result = LoadoutRules.Unequip(_catalog, _loadout, worn.Slot, target.Grid,
                    _views[1].Grid, _views[2].Grid, x, y, rotation);
            }
            catch (ValidationException error)
            {
                return error.Message;
            }

            var saveError = Persist(new Dictionary<GridView, StashGrid>
            {
                [target] = result.Target,
                [_views[1]] = result.Rig,
                [_views[2]] = result.Backpack,
            }, result.Loadout);
            if (saveError != null)
            {
                return saveError;
            }

            _loadout = result.Loadout;
            target.Grid = result.Target;
            _views[1].Grid = result.Rig;
            _views[2].Grid = result.Backpack;
            RefreshAll();
            AudioDirector.Play(Sound.Equip);
            return null;
        }

        // ---- dragging ----

        private sealed class Drag
        {
            public string InstanceId;
            public string SourceSlot;      // the slot the item is worn in, or null when it is in a grid
            public int PointerId;
            public bool Active;
            public int Rotation;
            public Vector2 StartPointer;   // panel coordinates
            public Vector2 Pointer;        // panel coordinates, latest
            public Vector2 Grab;           // pointer minus the dragged element's top-left, panel coordinates
            public float MarginLeft;       // the element's margin, so the drop lands where the ghost shows
            public float MarginTop;
            public GridView TargetView;    // the grid under the pointer, if any
            public SlotView TargetSlot;    // the slot card under the pointer, if any
            public bool OverTrash;         // the bin is under the pointer
            public int TargetX;
            public int TargetY;
        }

        private Drag _drag;
        private VisualElement _ghost;
        private VisualElement _proxy;

        private ScrollView _autoScrollView;
        private float _autoScrollPixelsPerSecond;

        /// <summary>Makes a grid cell or a slot card draggable. <paramref name="instanceIdOf"/> says which
        /// item it stands for right now - a slot card's item changes as gear is swapped, and it may be empty.</summary>
        private void RegisterDrag(VisualElement element, Func<string> instanceIdOf)
        {
            // TrickleDown: a Button's built-in Clickable stops pointer events from reaching later
            // handlers on the same element, so a normal (bubble-phase) callback would never run.
            element.RegisterCallback<PointerDownEvent>(evt => OnPointerDown(element, instanceIdOf, evt), TrickleDown.TrickleDown);
            element.RegisterCallback<PointerMoveEvent>(evt => OnPointerMove(element, evt), TrickleDown.TrickleDown);
            element.RegisterCallback<PointerUpEvent>(evt => OnPointerUp(element, evt), TrickleDown.TrickleDown);
            element.RegisterCallback<PointerCaptureOutEvent>(_ => CancelDrag(element));
            element.RegisterCallback<KeyDownEvent>(evt => OnKeyDown(element, instanceIdOf, evt), TrickleDown.TrickleDown);
        }

        private void OnPointerDown(VisualElement element, Func<string> instanceIdOf, PointerDownEvent evt)
        {
            // Touching anything else is the "no, keep it" answer to a bin that is waiting.
            CancelTrash();

            var instanceId = instanceIdOf();
            if (evt.button != 0 || instanceId == null)
            {
                return; // an empty slot has nothing to pick up
            }

            SelectItem(instanceId);
            element.Focus(); // so R / Esc reach this element

            _drag = new Drag
            {
                InstanceId = instanceId,
                SourceSlot = SlotOf(instanceId),
                PointerId = evt.pointerId,
                Rotation = RotationOf(instanceId),
                StartPointer = evt.position,
                Pointer = evt.position,
                Grab = (Vector2)evt.position - element.worldBound.min,
                MarginLeft = element.resolvedStyle.marginLeft,
                MarginTop = element.resolvedStyle.marginTop,
            };
            element.CapturePointer(evt.pointerId);
        }

        private void OnPointerMove(VisualElement element, PointerMoveEvent evt)
        {
            if (_drag == null || _drag.PointerId != evt.pointerId)
            {
                return;
            }

            _drag.Pointer = evt.position;
            if (!_drag.Active)
            {
                if ((_drag.Pointer - _drag.StartPointer).magnitude < DragThreshold)
                {
                    return; // still a click, not a drag
                }

                BeginDrag(element);
            }

            RefreshDrag();
        }

        private void BeginDrag(VisualElement element)
        {
            _drag.Active = true;

            // The element itself stays put (dimmed) and a copy follows the pointer. The copy lives at the
            // top of the screen's tree, so it draws over every panel - a cell dragged inside its own
            // grid would be hidden behind the next panel the moment it crossed the border.
            var item = _catalog[ItemIdOf(_drag.InstanceId)];
            _proxy = new VisualElement { pickingMode = PickingMode.Ignore };
            _proxy.AddToClassList("cell");
            _proxy.AddToClassList("cell-proxy");
            _proxy.style.borderTopColor = _proxy.style.borderBottomColor =
                _proxy.style.borderLeftColor = _proxy.style.borderRightColor = RarityPalette.For(item.Rarity);
            _proxy.Add(MonogramLabel(item));
            _root.Add(_proxy);

            _ghost = new VisualElement { pickingMode = PickingMode.Ignore };
            _ghost.AddToClassList("cell-ghost");

            if (_drag.SourceSlot != null)
            {
                // A slot card is not the size of the item, so the point grabbed on it means nothing on
                // the copy: hold the copy by its middle instead.
                _drag.Grab = FootprintPixels(item, _drag.Rotation) / 2f;
            }

            element.AddToClassList("cell-dragging");
        }

        private static Vector2 FootprintPixels(ItemDefinition item, int rotation)
        {
            PlacementRules.Footprint(item, rotation, out var width, out var height);
            return new Vector2(width * Pitch - 2, height * Pitch - 2);
        }

        /// <summary>Moves the copy with the pointer, and shows where a drop would land in green or red.</summary>
        private void RefreshDrag()
        {
            var drag = _drag;
            var itemId = ItemIdOf(drag.InstanceId);
            var item = _catalog[itemId];
            PlacementRules.Footprint(item, drag.Rotation, out var width, out var height);

            var topLeft = drag.Pointer - drag.Grab;
            var local = _root.WorldToLocal(topLeft);
            _proxy.style.left = local.x;
            _proxy.style.top = local.y;
            _proxy.style.width = width * Pitch - 2;
            _proxy.style.height = height * Pitch - 2;

            ClearSlotHighlights();

            // The bin is neither a slot nor a grid: it only needs to know whether the pointer is over
            // it, and the copy can be shown with no landing preview under it.
            drag.OverTrash = _trashBin != null && _trashBin.worldBound.Contains(drag.Pointer);
            if (_trashBin != null)
            {
                _trashBin.EnableInClassList("trash-bin-over", drag.OverTrash && TrashError(drag.InstanceId) == null);
            }

            if (drag.OverTrash)
            {
                drag.TargetSlot = null;
                drag.TargetView = null;
                _ghost.RemoveFromHierarchy();
                return;
            }

            drag.TargetSlot = _slots.FirstOrDefault(view => view.Card.worldBound.Contains(drag.Pointer));
            if (drag.TargetSlot != null)
            {
                drag.TargetView = null;
                _ghost.RemoveFromHierarchy();
                var hint = SlotDropError(drag.InstanceId, drag.TargetSlot.Slot) == null ? "slot-drop-ok" : "slot-drop-bad";
                drag.TargetSlot.Card.AddToClassList(hint);
                drag.TargetSlot.Art?.AddToClassList(hint); // the art covers the card, so it has to show the hint too
                return;
            }

            // A grid that scrolls is only under the pointer where its viewport is, not the whole tall grid.
            drag.TargetView = _views.FirstOrDefault(view => view.Element.worldBound.Contains(drag.Pointer)
                                                            && (view.Viewport ?? view.Element).worldBound.Contains(drag.Pointer));
            if (drag.TargetView == null)
            {
                _ghost.RemoveFromHierarchy();
                return;
            }

            var inGrid = drag.TargetView.Element.WorldToLocal(topLeft);
            drag.TargetX = Mathf.RoundToInt((inGrid.x - drag.MarginLeft - 2) / Pitch);
            drag.TargetY = Mathf.RoundToInt((inGrid.y - drag.MarginTop - 2) / Pitch);

            if (_ghost.parent != drag.TargetView.Element)
            {
                drag.TargetView.Element.Add(_ghost);
            }

            PositionCell(_ghost, drag.TargetX, drag.TargetY, width, height);
            var fits = PlacementRules.CanPlace(drag.TargetView.Grid, _catalog, itemId,
                drag.TargetX, drag.TargetY, drag.Rotation, drag.InstanceId);
            _ghost.EnableInClassList("cell-ghost-bad", !fits);

            UpdateAutoScroll(drag);
        }

        private void UpdateAutoScroll(Drag drag)
        {
            if (drag.TargetView == null || drag.TargetView.Viewport == null)
            {
                _autoScrollView = null;
                _autoScrollPixelsPerSecond = 0f;
                return;
            }

            var viewport = drag.TargetView.Viewport.worldBound;
            var distanceFromTop = drag.Pointer.y - viewport.y;
            var distanceFromBottom = viewport.y + viewport.height - drag.Pointer.y;

            if (distanceFromTop < AutoScrollEdge && distanceFromTop < distanceFromBottom)
            {
                _autoScrollView = drag.TargetView.Viewport.parent as ScrollView;
                _autoScrollPixelsPerSecond = -AutoScrollSpeed;
            }
            else if (distanceFromBottom < AutoScrollEdge)
            {
                _autoScrollView = drag.TargetView.Viewport.parent as ScrollView;
                _autoScrollPixelsPerSecond = AutoScrollSpeed;
            }
            else
            {
                _autoScrollView = null;
                _autoScrollPixelsPerSecond = 0f;
            }
        }

        private void Update()
        {
            if (_autoScrollView == null || _drag == null || !_drag.Active)
            {
                return;
            }

            var offset = _autoScrollView.scrollOffset;
            var maxOffset = _autoScrollView.contentContainer.layout.height - _autoScrollView.layout.height;
            offset.y = Mathf.Clamp(offset.y + _autoScrollPixelsPerSecond * Time.deltaTime, 0f, Mathf.Max(0f, maxOffset));
            _autoScrollView.scrollOffset = offset;

            RefreshDrag();
        }

        private void ClearSlotHighlights()
        {
            foreach (var view in _slots)
            {
                foreach (var element in new[] { view.Card, view.Art })
                {
                    element?.RemoveFromClassList("slot-drop-ok");
                    element?.RemoveFromClassList("slot-drop-bad");
                }
            }
        }

        private void OnPointerUp(VisualElement element, PointerUpEvent evt)
        {
            var drag = _drag;
            if (drag == null || drag.PointerId != evt.pointerId)
            {
                return;
            }

            // Clear first: releasing the capture raises PointerCaptureOut, which must not see a live drag.
            _drag = null;
            if (element.HasPointerCapture(evt.pointerId))
            {
                element.ReleasePointer(evt.pointerId);
            }

            if (!drag.Active)
            {
                return;
            }

            EndDrag(element);

            if (drag.OverTrash)
            {
                // Into the bin: it holds the item until the click that confirms, so a drop that lands
                // here by accident costs nothing but a message.
                var armError = ArmTrash(drag.InstanceId);
                if (armError != null)
                {
                    ShowMessage(armError);
                    Reject(element);
                }

                return;
            }

            string error;
            if (drag.TargetSlot != null)
            {
                error = TryEquipItem(drag.InstanceId, drag.TargetSlot.Slot);
            }
            else if (drag.TargetView == null)
            {
                return; // dropped on empty screen: the item simply stays where it was
            }
            else if (drag.SourceSlot != null)
            {
                error = TryUnequipItem(drag.InstanceId, drag.TargetView.Name, drag.TargetX, drag.TargetY, drag.Rotation);
            }
            else
            {
                error = TryTransfer(drag.InstanceId, drag.TargetView, drag.TargetX, drag.TargetY, drag.Rotation);
            }

            if (error != null)
            {
                Reject(element);
            }
        }

        private void CancelDrag(VisualElement element)
        {
            var drag = _drag;
            _drag = null;
            if (drag != null && drag.Active)
            {
                EndDrag(element);
            }
        }

        /// <summary>Removes the drag visuals; the element was never moved, so there is nothing to snap back.</summary>
        private void EndDrag(VisualElement element)
        {
            _proxy?.RemoveFromHierarchy();
            _proxy = null;
            _ghost?.RemoveFromHierarchy();
            _ghost = null;
            ClearSlotHighlights();
            _trashBin?.RemoveFromClassList("trash-bin-over");
            element.RemoveFromClassList("cell-dragging");
            _autoScrollView = null;
            _autoScrollPixelsPerSecond = 0f;
        }

        private static void Reject(VisualElement element)
        {
            AudioDirector.Play(Sound.Error);
            element.AddToClassList("cell-rejected");
            element.schedule.Execute(() => element.RemoveFromClassList("cell-rejected")).StartingIn(350);
        }

        private void OnKeyDown(VisualElement element, Func<string> instanceIdOf, KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Escape && _trashInstanceId != null)
            {
                CancelTrash();
                evt.StopPropagation();
                return;
            }

            var instanceId = instanceIdOf();
            if (instanceId == null)
            {
                return;
            }

            if (evt.keyCode == KeyCode.R)
            {
                if (_drag != null && _drag.Active)
                {
                    var item = _catalog[ItemIdOf(instanceId)];
                    if (item.Width != item.Height)
                    {
                        _drag.Rotation = _drag.Rotation == 0 ? 90 : 0;
                        // Centre the copy on the pointer: the old grab point may now be off the shape.
                        _drag.Grab = FootprintPixels(item, _drag.Rotation) / 2f;
                        RefreshDrag();
                    }
                }
                else if (TryRotateItem(instanceId) != null)
                {
                    Reject(element);
                }

                evt.StopPropagation();
            }
            else if (evt.keyCode == KeyCode.Escape && _drag != null && _drag.Active)
            {
                var pointerId = _drag.PointerId;
                CancelDrag(element);
                if (element.HasPointerCapture(pointerId))
                {
                    element.ReleasePointer(pointerId);
                }

                evt.StopPropagation();
            }
        }

        // ---- selection and the detail panel ----

        /// <summary>Selects an item by instance id, wherever it is - a grid or a slot. Public so tests can
        /// drive it directly instead of simulating a pointer click on a specific screen position.</summary>
        public void SelectItem(string instanceId)
        {
            if (_selectedInstanceId != null && _cellsByInstanceId.TryGetValue(_selectedInstanceId, out var previous))
            {
                previous.RemoveFromClassList("cell-selected");
            }

            foreach (var view in _slots)
            {
                view.Card.RemoveFromClassList("slot-selected");
                view.Art?.RemoveFromClassList("cell-selected");
            }

            _selectedInstanceId = instanceId;
            if (_cellsByInstanceId.TryGetValue(instanceId, out var current))
            {
                current.AddToClassList("cell-selected");
            }

            var worn = _loadout.FindByInstance(instanceId);
            if (worn != null)
            {
                MarkSlotSelected(SlotViewFor(worn.Slot));
            }

            RefreshActions();

            var item = _catalog[ItemIdOf(instanceId)];
            PlacementRules.Footprint(item, RotationOf(instanceId), out var width, out var height);
            var color = RarityPalette.For(item.Rarity);

            _swatchSelected.style.borderTopColor = _swatchSelected.style.borderBottomColor =
                _swatchSelected.style.borderLeftColor = _swatchSelected.style.borderRightColor = color;
            var art = _icons.GetArt(item.ItemId) ?? _icons.Get(item.ItemId);
            _labelSelectedIcon.text = art == null ? Monogram(item.Category) : "";
            _labelSelectedIcon.style.color = color;
            if (art != null)
            {
                _swatchSelected.style.backgroundImage = Background.FromTexture2D(art);
                _swatchSelected.style.unityBackgroundScaleMode = ScaleMode.ScaleToFit;
            }
            else
            {
                _swatchSelected.style.backgroundImage = StyleKeyword.None;
            }

            _labelSelectedName.text = item.Name;
            _labelSelectedRarity.text = item.Rarity.ToUpperInvariant();
            _labelSelectedRarity.style.color = color;
            _labelSelectedSub.text = $"{item.Category} · {width} × {height} cells";
            _labelSelectedValue.text = Spaced(item.BaseValue);
            _labelSelectedWeight.text = $"{item.Weight:0.00} kg";
            _labelSelectedPerCell.text = Spaced(item.BaseValue / (width * height));
        }

        // ---- the bin ----

        /// <summary>What is waiting in the bin for its confirming click, or null. For tests.</summary>
        public string ArmedForTrash => _trashInstanceId;

        /// <summary>The item is in a grid, not worn. Returns why it cannot be thrown away, or null.</summary>
        private string TrashError(string instanceId)
        {
            if (instanceId == null)
            {
                return "Nothing is being dragged.";
            }

            if (_loadout.FindByInstance(instanceId) != null)
            {
                return "That is worn - take it off before throwing it away.";
            }

            return TryViewOf(instanceId) == null ? "That item is not in a grid." : null;
        }

        /// <summary>
        /// Puts an item in the bin, where it waits. This is what dropping on the bin does: nothing is
        /// destroyed yet, because a drop that lands an inch wide would otherwise cost the item. Returns
        /// why it could not, or null.
        /// </summary>
        public string ArmTrash(string instanceId)
        {
            var error = TrashError(instanceId);
            if (error != null)
            {
                return error;
            }

            var item = _catalog[ItemIdOf(instanceId)];
            _trashInstanceId = instanceId;
            if (_trashIcon != null)
            {
                _trashIcon.style.color = BinArmedColor;
            }

            if (_trashLabel != null)
            {
                _trashLabel.text = "SURE?";
            }

            if (_trashBin != null)
            {
                _trashBin.AddToClassList("trash-bin-armed");
                _trashBin.tooltip = "Click to destroy " + item.Name;
            }

            ShowMessage(item.Name + " is in the bin. Click the bin to destroy it; click anything else to keep it.");
            return null;
        }

        /// <summary>Takes the item back out of the bin, destroying nothing.</summary>
        public void CancelTrash()
        {
            _trashInstanceId = null;
            if (_trashIcon != null)
            {
                _trashIcon.style.color = BinIdleColor;
            }

            if (_trashLabel != null)
            {
                _trashLabel.text = "BIN";
            }

            if (_trashBin != null)
            {
                _trashBin.RemoveFromClassList("trash-bin-armed");
                _trashBin.RemoveFromClassList("trash-bin-over");
                _trashBin.tooltip = BinHint;
            }
        }

        /// <summary>
        /// The confirming click: what is in the bin is gone. Returns why it could not be destroyed, or
        /// null. Nothing is recorded anywhere else - a thrown-away item is not a "left behind" one.
        /// </summary>
        public string ConfirmTrash()
        {
            var instanceId = _trashInstanceId;
            if (instanceId == null)
            {
                return null;
            }

            CancelTrash(); // whatever happens next, a second click must not find it still in there

            var blocked = ProfileRules.StashEditError(_session.Profile);
            if (blocked != null)
            {
                ShowMessage(blocked);
                return blocked;
            }

            var view = TryViewOf(instanceId);
            if (view == null)
            {
                return "That item is not in a grid any more.";
            }

            var itemId = ItemIdOf(instanceId);
            var trimmed = TrashRules.Without(view.Grid, instanceId);
            var saveError = Persist(new Dictionary<GridView, StashGrid> { [view] = trimmed });
            if (saveError != null)
            {
                return saveError;
            }

            view.Grid = trimmed;
            _selectedInstanceId = null;
            RefreshAll();
            ClearSelection();
            AudioDirector.Play(Sound.Trash);
            ShowMessage(_catalog[itemId].Name + " destroyed.");
            return null;
        }

        private void OnBinClicked()
        {
            if (_trashInstanceId == null)
            {
                ShowMessage(BinHint);
                return;
            }

            ConfirmTrash();
        }

        // ---- the toolbar and the bottom bar ----

        private static readonly string[] MedCategories = { "Meds", "Medical supplies" };

        private static readonly string[] BarterCategories =
        {
            "Valuables", "Electronics", "Household goods", "Building materials", "Tools",
            "Flammable materials",
        };

        private Button _filterAll;
        private Button _filterMeds;
        private Button _filterBarter;
        private string _stashFilter = "ALL";

        /// <summary>
        /// Uses one medkit from the stash on the open character: heals its pool across the worst parts
        /// first and clears what it treats. Before this there was no way to recover outside a fight.
        /// </summary>
        private void UseSelectedMed()
        {
            if (_selectedInstanceId == null)
            {
                ShowMessage("Select a medkit in the stash first.");
                return;
            }

            var itemId = ItemIdOf(_selectedInstanceId);
            if (!HealingRules.IsMed(_gear, itemId))
            {
                ShowMessage($"{_catalog[itemId].Name} is not a medkit.");
                return;
            }

            if (!HealingRules.WouldHelp(_session.Profile, _gear.Meds[itemId]))
            {
                ShowMessage(_catalog[itemId].Name + " would not change anything: it heals "
                    + (_gear.Meds[itemId].Heal > 0 ? _gear.Meds[itemId].Heal + " HP" : "no HP")
                    + " and clears nothing you have. Nothing was used.");
                return;
            }

            Profile treated;
            int healed;
            IReadOnlyList<string> cleared;
            try
            {
                (treated, healed, cleared) = HealingRules.Use(_session.Profile, _gear.Meds[itemId], _catalog);
            }
            catch (ValidationException error)
            {
                ShowMessage(error.Message);
                return;
            }

            // Not Persist(): that rebuilds the profile from the grids alone, which would throw away the
            // body parts and conditions this medkit just changed.
            try
            {
                _session.Commit(treated);
            }
            catch (StorageException error)
            {
                ShowMessage(error.Message);
                return;
            }

            StashView.Grid = treated.Stash;
            _selectedInstanceId = null;
            RefreshAll();
            ClearSelection();
            ApplyVitals(_session.Profile);
            AudioDirector.Play(Sound.Heal);

            var parts = new List<string>();
            if (healed > 0)
            {
                parts.Add(healed + " HP healed");
            }

            parts.AddRange(cleared);
            ShowMessage(parts.Count == 0
                ? _catalog[itemId].Name + " had nothing to treat."
                : _catalog[itemId].Name + ": " + string.Join(", ", parts));
        }

        /// <summary>
        /// Opens a stashed case: the case is consumed and what it holds lands in the stash. A locked
        /// case says what it needs instead of just failing, and one that will not fit changes nothing.
        /// </summary>
        private void OpenSelectedCase()
        {
            if (_selectedInstanceId == null)
            {
                ShowMessage("Select a case in the stash first.");
                return;
            }

            var itemId = ItemIdOf(_selectedInstanceId);
            var definition = CaseRules.Find(_cases, itemId);
            if (definition == null)
            {
                ShowMessage($"{_catalog[itemId].Name} does not open.");
                return;
            }

            var blocked = ProfileRules.StashEditError(_session.Profile);
            if (blocked != null)
            {
                ShowMessage(blocked);
                return;
            }

            Profile opened;
            IReadOnlyList<string> found;
            try
            {
                (opened, found) = CaseRules.Open(_session.Profile, definition, _lootTables,
                    PythonRandom.Create(), _catalog);
            }
            catch (ValidationException error)
            {
                ShowMessage(error.Message);
                return;
            }

            var saveError = Persist(new Dictionary<GridView, StashGrid> { [StashView] = opened.Stash });
            if (saveError != null)
            {
                return;
            }

            StashView.Grid = opened.Stash;
            _selectedInstanceId = null;
            RefreshAll();
            ClearSelection();
            ApplyVitals(_session.Profile);
            AudioDirector.Play(Sound.Case);

            var names = found
                .Select(id => _catalog.TryGetValue(id, out var item) ? item.Name : id)
                .ToList();
            ShowMessage(names.Count == 0
                ? "The case was empty."
                : "Opened: " + string.Join(", ", names));
        }

        /// <summary>Wears the selected item in the slot its own type belongs to.</summary>
        private void EquipSelected()
        {
            if (_selectedInstanceId == null)
            {
                ShowMessage("Select an item in the stash first.");
                return;
            }

            var itemId = ItemIdOf(_selectedInstanceId);
            if (itemId == null)
            {
                ClearSelection();
                ShowMessage("That item is no longer in a grid.");
                return;
            }

            var slot = _loadout.FindByInstance(_selectedInstanceId)?.Slot ?? _gear.SlotOf(itemId);
            if (slot == null)
            {
                ShowMessage($"{_catalog[itemId].Name} cannot be equipped.");
                return;
            }

            var error = TryEquipItem(_selectedInstanceId, slot);
            if (error != null)
            {
                ShowMessage(error);
            }
        }

        /// <summary>
        /// Repacks the stash with the same items: category first, then the largest piece first so the
        /// bins pack tightly, laid out from the top-left by the same placement rule a drop uses.
        /// All-or-nothing - if anything will not fit, the stash is left exactly as it was.
        /// </summary>
        private void SortStash()
        {
            var blocked = ProfileRules.StashEditError(_session.Profile);
            if (blocked != null)
            {
                ShowMessage(blocked); // repacking is still a change to a character who cannot make one
                return;
            }

            var ordered = StashView.Grid.Stash
                .OrderBy(instance => _catalog[instance.ItemId].Category, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(instance => _catalog[instance.ItemId].Width * _catalog[instance.ItemId].Height)
                .ThenBy(instance => _catalog[instance.ItemId].Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(instance => instance.InstanceId, StringComparer.Ordinal)
                .ToList();

            var packed = new StashGrid(StashView.Grid.StashWidth, StashView.Grid.StashHeight,
                new List<ItemInstance>());
            foreach (var instance in ordered)
            {
                var spot = PlacementRules.FirstFit(packed, _catalog, instance.ItemId);
                if (!spot.HasValue)
                {
                    ShowMessage("Nowhere to put everything - nothing was moved.");
                    return;
                }

                var place = spot.Value;
                packed = packed.With(packed.Stash.Concat(new[]
                {
                    instance.MovedTo(place.X, place.Y, place.Rotation),
                }));
            }

            var saveError = Persist(new Dictionary<GridView, StashGrid> { [StashView] = packed });
            if (saveError != null)
            {
                return;
            }

            StashView.Grid = packed;
            RefreshAll();
            AudioDirector.Play(Sound.Move);
            ShowMessage("Stash sorted by category.");
        }

        private void SetStashFilter(string filter)
        {
            _stashFilter = filter;
            PaintChip(_filterAll, filter == "ALL");
            PaintChip(_filterMeds, filter == "MEDS");
            PaintChip(_filterBarter, filter == "BARTER");
            ApplyStashFilter();
        }

        private static void PaintChip(Button chip, bool active)
        {
            var accent = new Color(0.851f, 0.643f, 0.255f);
            var muted = new Color(0.604f, 0.647f, 0.694f, 1f);
            chip.style.borderTopWidth = chip.style.borderBottomWidth =
                chip.style.borderLeftWidth = chip.style.borderRightWidth = 1;
            chip.style.borderTopColor = chip.style.borderBottomColor =
                chip.style.borderLeftColor = chip.style.borderRightColor = active ? accent : Color.clear;
            chip.style.color = active ? accent : muted;
        }

        /// <summary>Hides the cells that the current chip excludes. Placement is untouched - a hidden
        /// item keeps its cells, it just is not drawn.</summary>
        private void ApplyStashFilter()
        {
            string[] categories = _stashFilter == "MEDS" ? MedCategories
                : _stashFilter == "BARTER" ? BarterCategories
                : null;

            foreach (var pair in _cellsByInstanceId)
            {
                var itemId = ItemIdOf(pair.Key);
                var show = categories == null
                           || (itemId != null && categories.Contains(_catalog[itemId].Category));
                pair.Value.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        [Serializable]
        private sealed class PresetSlot
        {
            public string slot;
            public string item;
            public string instanceId;
        }

        [Serializable]
        private sealed class PresetItem
        {
            public string item;
            public string instanceId;
            public int x;
            public int y;
            public int rotation;
        }

        [Serializable]
        private sealed class LoadoutPreset
        {
            public string character;
            public string savedUtc;
            public List<PresetSlot> loadout;
            public List<PresetItem> stash;
        }

        /// <summary>Writes the worn loadout and the stash to presets/<character id>.json.</summary>
        private void SavePreset()
        {
            var folder = Path.Combine(DataFolderOverride ?? ProfileRepository.DefaultFolder, "presets");
            try
            {
                Directory.CreateDirectory(folder);
                var preset = new LoadoutPreset
                {
                    character = _session.Profile.DisplayName,
                    savedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    loadout = LoadoutSlots.All.Select(slot => new PresetSlot
                    {
                        slot = slot,
                        item = _loadout.Get(slot)?.ItemId,
                        instanceId = _loadout.Get(slot)?.InstanceId,
                    }).ToList(),
                    stash = StashView.Grid.Stash.Select(instance => new PresetItem
                    {
                        item = instance.ItemId,
                        instanceId = instance.InstanceId,
                        x = instance.X,
                        y = instance.Y,
                        rotation = instance.Rotation,
                    }).ToList(),
                };

                var file = Path.Combine(folder, _session.Profile.ProfileId + ".json");
                File.WriteAllText(file, JsonUtility.ToJson(preset, true));
                ShowMessage("Saved preset to " + file, persistent: true);
            }
            catch (Exception error)
            {
                ShowMessage("Could not save the preset: " + error.Message);
            }
        }

        internal static string Monogram(string category)
        {
            var letters = category.Where(char.IsLetter).Take(3).ToArray();
            return new string(letters).ToUpperInvariant();
        }

        public static string Spaced(double value) =>
            Math.Round(value).ToString("N0", CultureInfo.InvariantCulture).Replace(",", " ");
    }
}
