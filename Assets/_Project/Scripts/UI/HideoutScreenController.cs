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
    /// The HIDEOUT screen: shows every shared facility, its level, requirements, cost and effects, and lets the
    /// player upgrade them. The shared account and the open character are committed separately, so a save failure
    /// leaves the last good version on disk.
    /// </summary>
    /// <summary>
    /// A scene while the editor holds it: strings everywhere, converted on save or validate, so a half-typed
    [RequireComponent(typeof(UIDocument))]
    public sealed class HideoutScreenController : MonoBehaviour

    {
        private HideoutDefinition _definition;
        private RecipeBook _recipes;
        private IReadOnlyDictionary<string, ItemDefinition> _catalog;
        private VisualElement _root;
        private VisualElement _facilityList;
        private VisualElement _craftingList;
        private VisualElement _facilityScroll;
        private VisualElement _craftingScroll;
        private Button _tabFacilities;
        private Button _tabCrafting;
        private bool _showCrafting;
        private bool _showGm;

        /// <summary>PlayerPrefs key for the last random-event roll, and how often one is allowed.</summary>
        private const string EventRollKey = "Safehouse.EventRoll.v1";

        /// <summary>Random scenes are meant to be a surprise, not a schedule: five in a hundred visits.</summary>
        private const int RandomEventPercent = 5;

        /// <summary>Only there to stop one visit rolling twice; the 5 % is what keeps them rare.</summary>
        private const double RandomEventCooldownSeconds = 60.0;

        private EventBook _events;
        private VisualElement _eventSlot;
        private Label _eventError;
        private Label _labelMoney;
        private Label _labelPopulation;
        private Label _labelFuel;
        private Label _labelGeneratorSummary;
        private Label _labelRoomLevel;
        private Label _labelRoomSize;
        private Button _buttonUpgradeRoom;
        private Label _labelFacilityCount;
        private Label _labelStatus;

        /// <summary>Seconds since Unix epoch. Replaced in tests.</summary>
        public Func<double> Clock { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        private CharacterSession Session => GearScreenController.Current?.Session;
        private Profile Profile => Session?.Profile;
        private Account Account => Session?.Account;
        private double Now => Clock();

        private bool _wired;
        private bool _warned;

        private void OnEnable() => Wire();

        private void Start() => Wire();

        /// <summary>
        /// Wires the screen once. Guarded because a UIDocument builds its tree during its own OnEnable and the
        /// order of components on one GameObject is not something a screen should bet on: a null root threw from
        /// the first tab lookup and took the whole screen down with it.
        /// </summary>
        private void Wire()
        {
            if (_wired)
            {
                return;
            }

            var document = GetComponent<UIDocument>();
            var root = document == null ? null : document.rootVisualElement;
            if (root == null)
            {
                return;
            }

            _wired = true;
            _definition = HideoutLoader.Load();
            _recipes = RecipeLoader.LoadForCatalog(CatalogLoader.Load());
            _catalog = CatalogLoader.Load();
            _events = EventStore.Load(_catalog);

            _root = root;
            _root.style.alignItems = Align.Center;
            _root.style.justifyContent = Justify.Center;

            _facilityList = _root.Q<VisualElement>("facility-list");
            _craftingList = _root.Q<VisualElement>("crafting-list");
            _eventSlot = _root.Q<VisualElement>("event-slot");
            
            _facilityScroll = _root.Q<VisualElement>("facility-scroll");
            _craftingScroll = _root.Q<VisualElement>("crafting-scroll");
            _tabFacilities = _root.Q<Button>("tab-facilities");
            _tabCrafting = _root.Q<Button>("tab-crafting");
            _labelMoney = _root.Q<Label>("label-roubles");
            _labelPopulation = _root.Q<Label>("label-population");
            _labelFuel = _root.Q<Label>("label-fuel");
            _labelGeneratorSummary = _root.Q<Label>("label-generator-summary");
            _labelRoomLevel = _root.Q<Label>("label-room-level");
            _labelRoomSize = _root.Q<Label>("label-room-size");
            _buttonUpgradeRoom = _root.Q<Button>("button-upgrade-room");
            _labelFacilityCount = _root.Q<Label>("label-facility-count");
            _labelStatus = _root.Q<Label>("label-status");

            _root.Q<Button>("navtab-character").clicked += () => ScreenNavigator.Go("character");
            _root.Q<Button>("navtab-gear").clicked += () => ScreenNavigator.Go("gear");
            _root.Q<Button>("navtab-traders").clicked += () => ScreenNavigator.Go("traders");
            _root.Q<Button>("navtab-settings").clicked += () => ScreenNavigator.Go("settings");
            var gmTab = _root.Q<Button>("navtab-gm");
            if (gmTab != null)
            {
                gmTab.clicked += () => ScreenNavigator.Go("gm");
            }
            _root.Q<Button>("navtab-scavenge").clicked += () => ScreenNavigator.Go("scavenge");
            
            _root.Q<Button>("navtab-quests").clicked += () => ScreenNavigator.Go("quests");
            _buttonUpgradeRoom.clicked += TryUpgradeRoom;
            _tabFacilities.clicked += () => ShowCrafting(false);
            _tabCrafting.clicked += () => ShowCrafting(true);

            ScreenNavigator.Register("hideout", Show, Hide);
            Hide();
        }

        private void OnDisable()
        {
            ScreenNavigator.Unregister("hideout");
        }

        public void Show()
        {
            _root.style.display = DisplayStyle.Flex;
            Refresh();
            // Scenes are rolled out in the raid now (SCAVENGE), like encounters; the hideout does not raise them.
        }

        /// <summary>
        /// If a scene is due, put it on screen. Two different rules by design: a main-line scene plays the moment
        /// its gate quest is claimed, while a random one only gets a roll every
        /// <see cref="RandomEventCooldownSeconds"/> - otherwise walking in and out of the hideout would deal the
        /// city's little dramas like a card trick.
        /// </summary>
        private void MaybeShowEvent()
        {
            var profile = Profile;
            var session = Session;
            if (profile == null || session == null || !session.Saves
                || (_eventSlot != null && _eventSlot.childCount > 0))
            {
                return;
            }

            _events = EventStore.Load(_catalog);
            var log = new QuestRepository(session.Repository.DataFolder).Load(profile.ProfileId);
            var storyEvent = EventRules.NextMainLine(_events, log, EventPhases.Hideout, profile.Flags, profile.Abilities);
            if (storyEvent == null)
            {
                // Stored as a string: a PlayerPrefs float cannot hold a Unix timestamp to the minute.
                var stamp = PlayerPrefs.GetString(EventRollKey, "");
                // Parsed invariant: on a machine whose decimal separator is a comma, the stored "1730...0"
                // read back as 0 and the cooldown reset itself every time.
                var last = double.TryParse(stamp, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : 0.0;
                if (last > 0.0 && Now - last < RandomEventCooldownSeconds)
                {
                    return;
                }

                PlayerPrefs.SetString(EventRollKey, Now.ToString("F0", System.Globalization.CultureInfo.InvariantCulture));
                PlayerPrefs.Save();
                var random = PythonRandom.Seed(unchecked((int)DateTime.UtcNow.Ticks));

                // Five in a hundred. Everything else about the roll - cooldown, weighting - only decides when the
                // dice are even picked up.
                if (random.RandRange(100) >= RandomEventPercent)
                {
                    return;
                }

                storyEvent = EventRules.NextRandom(_events, log, EventPhases.Hideout, random, profile.Flags, profile.Abilities);
            }

            if (storyEvent != null)
            {
                ShowEvent(storyEvent, log);
            }
        }

        /// <summary>
        /// Draws the scene at the top of the right-hand column - the same shape as the CONTACT panel a fight
        /// uses, because it is the same kind of thing: something is happening to you and here are your options.
        /// It sits in the page rather than over it, so the facilities stay readable behind it.
        /// </summary>
        private void ShowEvent(StoryEvent storyEvent, QuestLog log)
        {
            if (_eventSlot == null)
            {
                return;
            }

            _eventSlot.Clear();
            var panel = new VisualElement();
            panel.AddToClassList("kia-panel");
            panel.style.marginBottom = 14;

            var kicker = new Label(storyEvent.MainLine
                ? (storyEvent.Act > 0 ? $"ACT {storyEvent.Act}   ·   MAIN LINE" : "MAIN LINE")
                : "THE CITY   ·   SOMETHING HAPPENS");
            kicker.AddToClassList("text-label");
            panel.Add(kicker);

            var title = new Label(storyEvent.Title.ToUpperInvariant());
            title.AddToClassList("kia-title");
            panel.Add(title);

            var text = new Label(storyEvent.Text);
            text.AddToClassList("kia-body");
            text.style.whiteSpace = WhiteSpace.Normal;
            text.style.maxWidth = Length.Percent(100);
            panel.Add(text);

            var errorLabel = new Label("");
            errorLabel.AddToClassList("text-danger");
            errorLabel.style.whiteSpace = WhiteSpace.Normal;
            errorLabel.style.fontSize = 12;
            errorLabel.style.marginBottom = 8;
            errorLabel.style.display = DisplayStyle.None;
            panel.Add(errorLabel);
            _eventError = errorLabel;

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;

            var carried = new HashSet<string>(Profile.Rig.Stash.Concat(Profile.Backpack.Stash)
                .Select(item => item.ItemId));
            for (var index = 0; index < storyEvent.Choices.Count; index++)
            {
                var choice = storyEvent.Choices[index];
                var choiceIndex = index;

                // Not offered is not the same as not affordable: an option that needs a thing you do not have is
                // simply not on the page.
                if (!EventRules.ChoiceOffered(choice, Profile.Flags, id => carried.Contains(id)))
                {
                    continue;
                }

                var error = EventRules.CostError(choice, Profile);
                var button = new Button(() => PickChoice(storyEvent, choiceIndex, log))
                {
                    text = choice.Label + CostSuffix(choice),
                };
                button.AddToClassList("btn");
                button.AddToClassList(error == null ? "btn-primary" : "btn-outline");
                button.AddToClassList("crafting-row-btn");
                button.style.marginTop = 8;
                button.style.marginRight = 8;
                button.SetEnabled(error == null);
                button.tooltip = error ?? "";
                row.Add(button);
            }

            panel.Add(row);
            _eventSlot.Add(panel);

            AudioDirector.Ambience(true);
        }

        private string CostSuffix(EventChoice choice)
        {
            var parts = new List<string>();
            if (choice.Cost.Roubles > 0)
            {
                parts.Add(TradingRules.Roubles(choice.Cost.Roubles));
            }

            foreach (var itemId in choice.Cost.Items)
            {
                parts.Add(NameOf(itemId));
            }

            return parts.Count == 0 ? "" : "   (" + string.Join(" + ", parts) + ")";
        }

        /// <summary>
        /// Takes the choice: the effects land on the profile, the quest the event finishes is advanced and
        /// written, and the scene closes. One commit for the character, one file write for the quest log.
        /// </summary>
        private void PickChoice(StoryEvent storyEvent, int choiceIndex, QuestLog log)
        {
            var profile = Profile;
            var session = Session;
            if (profile == null || session == null || !session.Saves)
            {
                return;
            }

            try
            {
                var (updated, line, advanced) = EventRules.Apply(storyEvent, choiceIndex, profile, _catalog);
                if (Account != null && !CommitAll(updated, Account))
                {
                    return;
                }

                if (advanced != null)
                {
                    var definition = QuestLoader.Load().FirstOrDefault(quest => quest.QuestId == advanced);
                    if (definition != null)
                    {
                        var after = QuestRules.Advance(log, definition, 1);
                        new QuestRepository(session.Repository.DataFolder).Save(profile.ProfileId, after);
                    }
                }

                CloseEventModal();
                SetStatus(string.IsNullOrEmpty(line) ? "It is done." : line);
                Refresh();
            }
            catch (ValidationException error)
            {
                ShowEventError(error.Message);
            }
            catch (StorageException error)
            {
                // The scene played, but the quest file would not write: close the modal anyway so the player is
                // never trapped behind it, and say what happened.
                Debug.LogWarning(error.Message);
                CloseEventModal();
                SetStatus("The scene played, but the quest could not be saved: " + error.Message, error: true);
                Refresh();
            }
            catch (Exception error)
            {
                // Never let a scene become an unclickable wall: log it and say it in the modal.
                Debug.LogException(error);
                ShowEventError(error.Message);
            }
        }

        private void CloseEventModal()
        {
            _eventSlot?.Clear();
            _eventError = null;
            AudioDirector.Ambience(false);
        }

        private void ShowEventError(string message)
        {
            if (_eventError == null)
            {
                return;
            }

            _eventError.text = message;
            _eventError.style.display = DisplayStyle.Flex;
        }

        public void Hide()
        {
            _root.style.display = DisplayStyle.None;
            AudioDirector.Ambience(false); // leaving the page stops the scene's radio, not the music
        }

        private void Refresh()
        {
            var profile = Profile;
            var account = Account;

            _labelMoney.text = profile == null ? "—" : GearScreenController.Spaced(profile.Money);
            _labelPopulation.text = account == null ? "—" : account.Population.ToString();
            _labelFuel.text = account == null || profile == null
                ? "—"
                : HideoutRules.GeneratorFuelPerHour(_definition, account.Hideout, account.Population).ToString();

            if (profile == null || account == null)
            {
                _labelStatus.text = "Open a character on the GEAR screen first.";
                _facilityList.Clear();
                return;
            }

            _labelStatus.text = "";
            RefreshGeneratorSummary(account.Hideout);
            RefreshRoomSummary(profile.Room);
            BuildFacilityCards(account.Hideout, profile);
            ShowPage(_showCrafting ? 1 : 0);
        }

        /// <summary>Switches the right-hand panel between the facility cards and the crafting table.</summary>
        private void ShowCrafting(bool crafting) => ShowPage(crafting ? 1 : 0);

        /// <summary>Which of the three right-hand pages is showing: facilities, crafting, or the scene editor.</summary>
        private void ShowPage(int page)
        {
            _showCrafting = page == 1;
            _showGm = page == 2;
            if (_facilityScroll != null)
            {
                _facilityScroll.style.display = page == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            }

            if (_craftingScroll != null)
            {
                _craftingScroll.style.display = page == 1 ? DisplayStyle.Flex : DisplayStyle.None;
            }

            SetTab(_tabFacilities, page == 0);
            SetTab(_tabCrafting, page == 1);

            if (page == 1)
            {
                BuildCraftingTable();
            }
        }

        /// <summary>
        /// One tab on, one off. Both classes have to be moved, not just added: `Theme.uss` declares
        /// `.btn-outline` after `.btn-primary`, so a button carrying both keeps the outline look whatever the
        /// code says - which is exactly why the open tab was not gold with black text.
        /// </summary>
        private static void SetTab(Button tab, bool active)
        {
            if (tab == null)
            {
                return;
            }

            tab.EnableInClassList("btn-primary", active);
            tab.EnableInClassList("btn-outline", !active);
        }

        /// <summary>
        /// Every recipe in the game on one page: what it takes (and how much of that is in the stash), what it
        /// gives, how long it runs at this character's finesse, and the one button that matters - START, the
        /// running percentage, or COLLECT. The facility cards remain the place to build and upgrade.
        /// </summary>
        private void BuildCraftingTable()
        {
            if (_craftingList == null)
            {
                return;
            }

            _craftingList.Clear();
            var profile = Profile;
            var account = Account;
            if (profile == null || account == null)
            {
                _craftingList.Add(new Label("Open a character on the GEAR screen first."));
                return;
            }

            var have = profile.Stash.Stash
                .GroupBy(item => item.ItemId)
                .ToDictionary(group => group.Key, group => group.Count());
            var speed = AbilityEffects.CraftSpeed(profile);
            var running = account.ProductionJobs.ToDictionary(job => job.RecipeId, job => job);

            var header = new Label($"CRAFTING · finesse {profile.Abilities.GetValueOrDefault("finesse", CharacterSheet.AbilityBase)} "
                                   + $"→ {speed * 100:0}% craft time");
            header.AddToClassList("text-label");
            _craftingList.Add(header);

            foreach (var recipe in _recipes.Recipes.OrderBy(r => r.FacilityId).ThenBy(r => r.FacilityLevel))
            {
                var row = new VisualElement();
                row.AddToClassList("crafting-row");

                var left = new VisualElement();
                left.AddToClassList("crafting-row-left");
                var name = new Label(recipe.Name);
                name.AddToClassList("crafting-row-name");
                left.Add(name);

                // Two lines, not one: the facility and the flow on one, the clock on its own, so the eye can find
                // "what does it cost me" and "how long" without parsing a single long sentence.
                var facilityLevel = account.Hideout.LevelOf(recipe.FacilityId);
                var meta = new Label(
                    $"{_definition.Facility(recipe.FacilityId).Name} L{recipe.FacilityLevel}"
                    + "     " + InputsText(recipe, have)
                    + "   →   " + OutputsText(recipe));
                meta.AddToClassList("crafting-row-meta");
                meta.enableRichText = true;
                left.Add(meta);

                var time = new Label(recipe.Category == RecipeCategory.Repeating
                    ? $"{recipe.DurationHours * speed:0.##} h per cycle"
                    : $"{recipe.DurationHours * speed:0.##} h");
                time.AddToClassList("crafting-row-time");
                left.Add(time);
                row.Add(left);

                var action = new Button();
                action.AddToClassList("btn");
                action.AddToClassList("crafting-row-btn");
                if (running.TryGetValue(recipe.RecipeId, out var job))
                {
                    var ready = ProductionRules.IsReady(recipe, job, _definition, account.Hideout, Now, speed);
                    var progress = ProductionRules.Progress(recipe, job, _definition, account.Hideout, Now, speed);
                    action.text = ready ? "COLLECT" : $"{progress * 100f:0}%";
                    action.AddToClassList(ready ? "btn-primary" : "btn-outline");
                    action.SetEnabled(ready);
                    action.clicked += () => TryCollectRecipe(recipe, job);
                }
                else if (facilityLevel < recipe.FacilityLevel)
                {
                    action.text = $"NEEDS {_definition.Facility(recipe.FacilityId).Name.ToUpperInvariant()} L{recipe.FacilityLevel}";
                    action.AddToClassList("btn-outline");
                    action.SetEnabled(false);
                }
                else
                {
                    var error = ProductionRules.CanStartError(recipe, profile, account, _definition, _recipes, _catalog,
                        recipe.Category == RecipeCategory.Repeating ? 1 : 0);
                    action.text = recipe.Category == RecipeCategory.Repeating ? "INSERT GPUs" : "START";
                    action.AddToClassList(error == null ? "btn-primary" : "btn-outline");
                    var blocked = error != null;
                    action.SetEnabled(!blocked);
                    action.tooltip = error ?? "";
                    if (!blocked)
                    {
                        action.clicked += () => TryStartRecipe(recipe);
                    }
                }

                row.Add(action);
                _craftingList.Add(row);
            }
        }

        private string NameOf(string itemId) =>
            _catalog.TryGetValue(itemId, out var item) ? item.Name : itemId;

        /// <summary>
        /// What a recipe eats, with what the stash holds beside it. A stack the stash cannot cover is drawn in
        /// red as a whole - name and count together - because "×1 (0)" in the same grey as everything else reads
        /// as fine until START refuses for no visible reason.
        /// </summary>
        private string InputsText(RecipeDefinition recipe, IReadOnlyDictionary<string, int> have)
        {
            if (recipe.Inputs.Count == 0)
            {
                return "IN: nothing";
            }

            var parts = recipe.Inputs.Select(stack =>
            {
                var count = have.TryGetValue(stack.ItemId, out var held) ? held : 0;
                var text = $"{NameOf(stack.ItemId)} ×{stack.Count} ({count})";
                return count >= stack.Count
                    ? $"<color=#6F9E5C>{text}</color>"
                    : $"<color=#C8503C>{text}</color>";
            });
            return "IN: " + string.Join(" + ", parts);
        }

        private string OutputsText(RecipeDefinition recipe) =>
            recipe.Outputs.Count == 0
                ? "OUT: random loot"
                : "OUT: " + string.Join(", ", recipe.Outputs.Select(stack => $"{NameOf(stack.ItemId)} ×{stack.Count}"));

        private void RefreshGeneratorSummary(Hideout hideout)
        {
            var generatorLevel = hideout.LevelOf("generator");
            var perPerson = HideoutRules.IntEffect(_definition, hideout, "generator", "fuel_per_character");
            var running = Account != null && HideoutRules.IsGeneratorRunning(_definition, hideout, Account.Population);
            _labelGeneratorSummary.text = $"LEVEL {generatorLevel} · {perPerson}/person/h · FUEL {Math.Floor(hideout.Fuel)} · {(running ? "RUNNING" : "OFF")}";
        }

        private void RefreshRoomSummary(PersonalRoom room)
        {
            var (width, height) = HideoutRules.RoomStashSize(_definition, room.Level);
            _labelRoomLevel.text = $"LEVEL {room.Level}";
            _labelRoomSize.text = $"{width}×{height} cells";

            var next = room.Level + 1;
            var canUpgrade = next <= _definition.PersonalRoom.MaxLevel
                && Profile.Money >= _definition.PersonalRoom.Level(next).Cost.Money;
            _buttonUpgradeRoom.SetEnabled(canUpgrade);
        }

        private void BuildFacilityCards(Hideout hideout, Profile profile)
        {
            _facilityList.Clear();
            var builtCount = hideout.Facilities.Count(f => f.Level > 0);
            _labelFacilityCount.text = $"{builtCount} / {Hideout.SharedFacilityIds.Count} BUILT";

            foreach (var facilityId in Hideout.SharedFacilityIds)
            {
                var definition = _definition.Facility(facilityId);
                var currentLevel = hideout.LevelOf(facilityId);
                var card = MakeFacilityCard(definition, currentLevel, profile, hideout);
                _facilityList.Add(card);
            }
        }

        private VisualElement MakeFacilityCard(FacilityDefinition definition, int currentLevel, Profile profile, Hideout hideout)
        {
            var card = new VisualElement();
            card.AddToClassList("facility-card");

            var header = new VisualElement();
            header.AddToClassList("facility-card-header");

            var name = new Label(definition.Name.ToUpperInvariant());
            name.AddToClassList("facility-card-name");
            header.Add(name);

            var level = new Label($"LV{currentLevel}/{definition.MaxLevel}");
            level.AddToClassList("facility-card-level");
            header.Add(level);

            var nextLevel = currentLevel + 1;
            var canUpgrade = nextLevel <= definition.MaxLevel;
            var blocked = canUpgrade
                ? HideoutRules.UpgradeError(_definition, hideout, profile, definition.FacilityId, nextLevel)
                : null;
            var button = new Button { text = canUpgrade ? "UPGRADE" : "MAX" };
            button.AddToClassList("btn");
            button.AddToClassList(canUpgrade ? "btn-primary" : "btn-outline");
            button.AddToClassList("facility-card-btn");
            button.SetEnabled(canUpgrade && blocked == null);

            if (canUpgrade)
            {
                var id = definition.FacilityId;
                var toLevel = nextLevel;
                button.clicked += () => TryUpgradeFacility(id, toLevel);
            }

            header.Add(button);
            card.Add(header);

            if (!string.IsNullOrEmpty(definition.Description))
            {
                var descLabel = new Label(definition.Description);
                descLabel.AddToClassList("facility-card-desc");
                card.Add(descLabel);
            }

            if (definition.Decorative)
            {
                var decorativeLabel = new Label("Decorative — does not affect character stats.");
                decorativeLabel.AddToClassList("facility-card-decorative");
                card.Add(decorativeLabel);
            }

            if (canUpgrade)
            {
                var levelDef = definition.Level(nextLevel);
                if (levelDef.Requirements.Count > 0)
                {
                    var reqText = "Requires: " + string.Join(", ", levelDef.Requirements.Select(r =>
                        $"{_definition.Facility(r.FacilityId).Name} L{r.Level}"));
                    var reqLabel = new Label(reqText);
                    reqLabel.AddToClassList("facility-card-requirements");
                    card.Add(reqLabel);
                }

                var costText = levelDef.Cost.Items.Count > 0
                    ? $"Cost: {TradingRules.Roubles(levelDef.Cost.Money)} + items"
                    : $"Cost: {TradingRules.Roubles(levelDef.Cost.Money)}";
                var costLabel = new Label(costText);
                costLabel.AddToClassList("facility-card-cost");
                card.Add(costLabel);

                // A disabled button with no explanation is a dead end: say why it cannot be pressed.
                if (blocked != null)
                {
                    var whyLabel = new Label(blocked);
                    whyLabel.AddToClassList("facility-card-requirements");
                    card.Add(whyLabel);
                }
            }

            if (currentLevel > 0)
            {
                var effects = FormatEffects(definition.Level(currentLevel).Effects);
                if (!string.IsNullOrEmpty(effects))
                {
                    var effectsLabel = new Label(effects);
                    effectsLabel.AddToClassList("facility-card-effects");
                    card.Add(effectsLabel);
                }
            }

            AddProductionSection(card, definition.FacilityId, currentLevel);

            if (definition.FacilityId == "generator" && currentLevel > 0)
            {
                AddRefuelRow(card, profile);
            }

            return card;
        }

        /// <summary>
        /// One button per fuel can in the stash. Without this the generator only ever ran down: no recipe could
        /// start once the hundred units it begins with were burnt, and nothing on the screen said why.
        /// </summary>
        private void AddRefuelRow(VisualElement card, Profile profile)
        {
            if (profile == null || Account == null)
            {
                return;
            }

            var cans = profile.Stash.Stash
                .Where(item => HideoutRules.FuelUnits.ContainsKey(item.ItemId))
                .GroupBy(item => item.ItemId)
                .Select(group => (ItemId: group.Key, InstanceId: group.First().InstanceId, Count: group.Count()))
                .ToList();
            if (cans.Count == 0)
            {
                return;
            }

            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };
            foreach (var can in cans)
            {
                var units = HideoutRules.FuelUnits[can.ItemId];
                var instanceId = can.InstanceId;
                var button = new Button(() => TryRefuel(instanceId))
                {
                    text = $"FUEL {can.ItemId.Replace('-', ' ').ToUpperInvariant()}  +{units}  ({can.Count})",
                };
                button.AddToClassList("btn");
                button.AddToClassList("btn-outline");
                button.style.marginRight = 6;
                button.style.marginTop = 6;
                row.Add(button);
            }

            card.Add(row);
        }

        private void TryRefuel(string instanceId)
        {
            var profile = Profile;
            var account = Account;
            if (profile == null || account == null)
            {
                SetStatus("Open a character first.");
                return;
            }

            try
            {
                var (paid, fuelled, units) = HideoutRules.Refuel(account, profile, instanceId, _catalog);
                if (CommitAll(paid, fuelled))
                {
                    SetStatus($"+{units} fuel.");
                }
            }
            catch (ValidationException error)
            {
                SetStatus(error.Message);
            }
        }

        private void AddProductionSection(VisualElement card, string facilityId, int facilityLevel)
        {
            var recipes = _recipes.ForFacility(facilityId).Where(r => r.FacilityLevel <= facilityLevel).ToList();
            if (recipes.Count == 0)
            {
                return;
            }

            var section = new VisualElement();
            section.AddToClassList("facility-card-production");

            foreach (var recipe in recipes)
            {
                var row = new VisualElement();
                row.AddToClassList("facility-recipe-row");

                var name = new Label(recipe.Name);
                name.AddToClassList("facility-recipe-name");
                row.Add(name);

                var running = Account?.ProductionJobs.FirstOrDefault(j => j.RecipeId == recipe.RecipeId);
                if (running != null)
                {
                    // The character's finesse is part of the duration, so the bar and the READY state have to be
                    // computed with the same multiplier the collection uses.
                    var speed = Profile == null ? 1.0 : AbilityEffects.CraftSpeed(Profile);
                    var progress = ProductionRules.Progress(recipe, running, _definition, Account.Hideout, Now, speed);
                    var ready = ProductionRules.IsReady(recipe, running, _definition, Account.Hideout, Now, speed);

                    var track = new VisualElement();
                    track.AddToClassList("facility-recipe-progress");
                    var bar = new VisualElement();
                    bar.AddToClassList("facility-recipe-progress-bar");
                    bar.style.width = new Length((float)(progress * 100.0), LengthUnit.Percent);
                    track.Add(bar);
                    row.Add(track);

                    var button = new Button { text = ready ? "COLLECT" : $"{progress * 100f:0}%" };
                    button.AddToClassList("btn");
                    button.AddToClassList(ready ? "btn-primary" : "btn-outline");
                    button.AddToClassList("facility-recipe-btn");
                    button.SetEnabled(ready);
                    var job = running;
                    button.clicked += () => TryCollectRecipe(recipe, job);
                    row.Add(button);
                }
                else
                {
                    var error = Profile != null && Account != null
                        ? ProductionRules.CanStartError(recipe, Profile, Account, _definition, _recipes, _catalog,
                            GpuCountFor(recipe))
                        : "Open a character first.";

                    var button = new Button
                    {
                        text = recipe.Category == RecipeCategory.Repeating ? "INSERT GPUs" : "START"
                    };
                    button.AddToClassList("btn");
                    button.AddToClassList("btn-primary");
                    button.AddToClassList("facility-recipe-btn");
                    button.SetEnabled(error == null);
                    if (error != null)
                    {
                        button.tooltip = error;
                    }

                    var r = recipe;
                    button.clicked += () => TryStartRecipe(r);
                    row.Add(button);
                }

                section.Add(row);
            }

            card.Add(section);
        }

        private int GpuCountFor(RecipeDefinition recipe)
        {
            if (recipe.Category != RecipeCategory.Repeating || Profile == null || Account == null)
            {
                return 0;
            }

            var maxSlots = HideoutRules.IntEffect(_definition, Account.Hideout, "bitcoin_farm", "bitcoin_slots");
            var available = Profile.Stash.Stash.Count(i => i.ItemId == "graphics-card");
            return Math.Min(maxSlots, available);
        }

        private void TryStartRecipe(RecipeDefinition recipe)
        {
            var profile = Profile;
            var account = Account;
            if (profile == null || account == null)
            {
                SetStatus("Open a character on the GEAR screen first.", error: true);
                return;
            }

            var gpuCount = recipe.Category == RecipeCategory.Repeating ? GpuCountFor(recipe) : 0;
            try
            {
                var (paidProfile, withJob) = ProductionRules.Start(recipe, profile, account, _definition, _recipes,
                    Now, _catalog, gpuCount);
                if (!CommitAll(paidProfile, withJob))
                {
                    return;
                }

                SetStatus($"Started {recipe.Name}.");
                Refresh();
            }
            catch (ValidationException error)
            {
                SetStatus(error.Message, error: true);
            }
        }

        private void TryCollectRecipe(RecipeDefinition recipe, ProductionJob job)
        {
            var profile = Profile;
            var account = Account;
            if (profile == null || account == null)
            {
                SetStatus("Open a character on the GEAR screen first.", error: true);
                return;
            }

            try
            {
                var (withOutput, collected) = ProductionRules.Collect(recipe, job, profile, account, _definition,
                    _recipes, _catalog, Now, new System.Random());
                if (!CommitAll(withOutput, collected))
                {
                    return;
                }

                SetStatus($"Collected {recipe.Name}.");
                Refresh();
            }
            catch (ValidationException error)
            {
                SetStatus(error.Message, error: true);
            }
        }

        private static string FormatEffects(IReadOnlyDictionary<string, object> effects)
        {
            if (effects == null || effects.Count == 0)
            {
                return null;
            }

            var parts = new List<string>();
            foreach (var pair in effects)
            {
                if (pair.Value is List<object> list)
                {
                    parts.Add($"{pair.Key}: {string.Join(", ", list.OfType<string>())}");
                }
                else
                {
                    parts.Add($"{pair.Key}: {pair.Value}");
                }
            }

            return string.Join(" · ", parts);
        }

        private void TryUpgradeFacility(string facilityId, int toLevel)
        {
            var profile = Profile;
            var account = Account;
            if (profile == null || account == null)
            {
                SetStatus("Open a character on the GEAR screen first.", error: true);
                return;
            }

            var error = HideoutRules.UpgradeError(_definition, account.Hideout, profile, facilityId, toLevel);
            if (error != null)
            {
                SetStatus(error, error: true);
                return;
            }

            var cost = HideoutRules.UpgradeCost(_definition, account.Hideout, facilityId, toLevel);
            var upgradedHideout = account.Hideout.WithFacility(new Facility(facilityId, toLevel));
            var paidProfile = profile.With(money: profile.Money - cost.Money);

            if (!CommitAll(paidProfile, account.With(hideout: upgradedHideout)))
            {
                return;
            }

            SetStatus($"Upgraded {_definition.Facility(facilityId).Name} to level {toLevel}.");
            Refresh();
        }

        private void TryUpgradeRoom()
        {
            var profile = Profile;
            var account = Account;
            if (profile == null || account == null)
            {
                SetStatus("Open a character on the GEAR screen first.", error: true);
                return;
            }

            var nextLevel = profile.Room.Level + 1;
            if (nextLevel > _definition.PersonalRoom.MaxLevel)
            {
                SetStatus("Personal room is already at maximum level.", error: true);
                return;
            }

            var cost = _definition.PersonalRoom.Level(nextLevel).Cost;
            if (profile.Money < cost.Money)
            {
                SetStatus($"Not enough money: {TradingRules.Roubles(cost.Money)} needed.", error: true);
                return;
            }

            var paidProfile = profile.With(money: profile.Money - cost.Money, room: new PersonalRoom(nextLevel));
            paidProfile = HideoutRules.EnsureRoomStashSize(_definition, paidProfile);
            if (!CommitAll(paidProfile, account))
            {
                return;
            }

            SetStatus($"Upgraded personal room to level {nextLevel}. Stash expanded to {paidProfile.Stash.StashWidth}×{paidProfile.Stash.StashHeight}.");
            Refresh();
        }

        /// <summary>
        /// Saves the profile and account. Returns false if either save fails; the first successful save is not rolled
        /// back, so this is best-effort for now.
        /// </summary>
        private bool CommitAll(Profile profile, Account account)

        {
            try
            {
                Session.Commit(profile);
                Session.Commit(account);
                return true;
            }
            catch (StorageException error)
            {
                SetStatus(error.Message, error: true);
                return false;
            }
        }

        private void SetStatus(string text, bool error = false)
        {
            _labelStatus.text = text;
            _labelStatus.EnableInClassList("text-danger", error);
            _labelStatus.EnableInClassList("text-muted", !error);
        }
    }
}