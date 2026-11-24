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
    /// SCAVENGE: send the open character on a simulated (real-time) expedition or a direct node map.
    /// The rules live in Core. This screen only chooses and shows the result.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class ScavengeScreenController : MonoBehaviour
    {
        private VisualElement _root;
        private VisualElement _main;
        private VisualElement _detail;
        private Label _log;
        private Label _error;
        private ExpeditionSession _expeditions;
        private string _dataFolder;
        private Dictionary<string, MapDefinition> _maps;
        private string _notice;
        private string _mapId = "customs";
        private bool _night;
        private string _mode = ExpeditionModes.Direct;
        private float _nextTick;

        // Rolling a trip back needs two more things beside the trip itself: where the "before"
        // copies are kept, and a way to drop the trip file. Both are rebuilt when the data folder
        // changes, the same way _expeditions is.
        private string _auxFolder;
        private SnapshotRepository _snapshots;
        private ExpeditionRepository _trips;
        private IconLibrary _icons;
        private VisualElement _dragGhost;
        private ItemInstance _dragItem;
        private bool _dragFromBackpack;
        private Vector2 _dragGrab;
        private readonly List<(VisualElement Board, bool IsBackpack, StashGrid Grid, Expedition Trip, string ProfileId, Profile Profile)> _boards =
            new List<(VisualElement, bool, StashGrid, Expedition, string, Profile)>();

        // The bins: one per party member (a find and a full bag are both that member's own), and the one
        // thing waiting in one of them for the confirming click - a floor find by item type, or a carried
        // item by instance, so exactly one of the two ids is set.
        private Label _hint;
        private VisualElement _deathOverlay;
        private readonly List<(Label Bin, string ProfileId)> _bins = new List<(Label, string)>();
        private string _binnedProfileId;
        private string _binnedFloorItem;
        private string _binnedInstanceId;
        private string _binnedName;
        private Label _binnedBin;
        private const string BinHint = "Drop a find or something you are carrying here, then click it to destroy it";

        private static readonly Color BinIdleColor = new Color(0.471f, 0.518f, 0.561f);   // rgb(120, 132, 143)
        private static readonly Color BinArmedColor = new Color(0.851f, 0.643f, 0.255f);  // rgb(217, 164, 65)

        /// <summary>
        /// Pixels per grid cell out in a raid. The GEAR screen uses 48; this screen has a 400px side panel
        /// instead of a whole stash panel, so it was 22 - and at that size an icon is a smudge and the
        /// item cannot be told from the one next to it. 40 fits an 8-wide backpack (320px) in the panel.
        /// </summary>
        private const float RaidPitch = 40f;

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;

            // Same as the other screens: the panel scales the 1440x900 design to the window but
            // keeps its proportions, so a window of another aspect leaves a strip of empty space on
            // one axis. Centre the design in it instead of letting it pile up on the right.
            _root.style.alignItems = Align.Center;
            _root.style.justifyContent = Justify.Center;

            _icons = new IconLibrary();
            _main = _root.Q("scavenge-main");
            _detail = _root.Q("scavenge-detail");
            _log = _root.Q<Label>("scavenge-log");
            _error = _root.Q<Label>("scavenge-error");
            _hint = _root.Q<Label>("scavenge-hint");
            _root.Q<Button>("navtab-character").clicked += () => ScreenNavigator.Go("character");
            _root.Q<Button>("navtab-gear").clicked += () => ScreenNavigator.Go("gear");
            _root.Q<Button>("navtab-hideout").clicked += () => ScreenNavigator.Go("hideout");
            _root.Q<Button>("navtab-traders").clicked += () => ScreenNavigator.Go("traders");
            _root.Q<Button>("navtab-settings").clicked += () => ScreenNavigator.Go("settings");
            _root.Q<Button>("navtab-quests").clicked += () => ScreenNavigator.Go("quests");
            ScreenNavigator.Register("scavenge", Show, Hide);
            Hide();
        }

        private void OnDisable() => ScreenNavigator.Unregister("scavenge");

        private void Update()
        {
            if (_root == null || _root.resolvedStyle.display == DisplayStyle.None || Time.unscaledTime < _nextTick)
            {
                return;
            }

            _nextTick = Time.unscaledTime + 1f;
            var session = GearScreenController.Current?.Session;
            var trip = OpenTrip(session);
            if (trip != null && ExpeditionRules.Due(trip, Now()))
            {
                Run(() => Expeditions(session).Advance(trip, Now()), session);
            }
        }

        private void Show()
        {
            _root.style.display = DisplayStyle.Flex;
            Refresh();
        }

        private void Hide()
        {
            HideDeathNotice();
            _root.style.display = DisplayStyle.None;
        }

        private void Refresh()
        {
            CancelBin(); // the old bin elements are about to leave the tree
            _boards.Clear();
            _bins.Clear();
            CancelDrag();
            _main.Clear();
            _detail.Clear();
            _error.text = "";
            if (_hint != null)
            {
                _hint.text = "";
            }
            var session = GearScreenController.Current?.Session;
            if (session == null || !session.Saves)
            {
                HideDeathNotice();
                _main.Add(new Label("Open a saved character before sending anyone out."));
                return;
            }

            // Death is the end of the save, so it gets a window the player cannot miss or click past:
            // the only door it offers is the roster, where another character is waiting.
            if (session.Profile.Status == CharacterSheet.Dead)
            {
                ShowDeathNotice(session);
            }
            else
            {
                HideDeathNotice();
            }

            try
            {
                EnsureData(session);
            }
            catch (Exception error)
            {
                _error.text = error.Message;
                return;
            }

            var trip = OpenTrip(session);
            if (trip == null)
            {
                DrawLaunch(session);
                return;
            }

            DrawTrip(session, trip);
        }

        private void DrawLaunch(CharacterSession session)
        {
            var dead = session.Profile.Status == CharacterSheet.Dead;
            _log.text = dead
                ? "Nobody is going anywhere: this character is dead."
                : _notice ?? "Choose a map, a time of day, and whether the trip runs on the clock or on the node map.";
            var column = Column();
            column.Add(Heading("SEND OUT"));
            column.Add(new Label(session.Profile.DisplayName) { style = { marginBottom = 10 } });
            var grid = new VisualElement();
            grid.AddToClassList("map-grid");
            foreach (var map in _maps.Values.OrderBy(map => map.Name, StringComparer.Ordinal))
            {
                grid.Add(MapTile(map));
            }

            column.Add(grid);

            // A dead character cannot be sent anywhere, so the whole launch strip goes away rather than
            // sitting there failing every time somebody presses LAUNCH.
            if (dead)
            {
                column.Add(KilledPanel(new[] { session.Profile.DisplayName },
                    "Dead is dead: they cannot be sent out again, and nothing in the game brings them back. "
                    + "The roster lists them as KILLED IN ACTION, and DELETE there removes the character for good."));
            }
            else
            {
                var nightButton = Bar(_night ? "NIGHT" : "DAY", () => { _night = !_night; Refresh(); });
                nightButton.style.marginTop = 8;
                nightButton.style.marginBottom = 4;
                column.Add(nightButton);

                var modeButton = Bar(
                    _mode == ExpeditionModes.Simulation ? "MODE  SIMULATED" : "MODE  DIRECT",
                    () =>
                    {
                        _mode = _mode == ExpeditionModes.Simulation ? ExpeditionModes.Direct : ExpeditionModes.Simulation;
                        Refresh();
                    });
                modeButton.style.marginBottom = 12;
                column.Add(modeButton);

                column.Add(Bar("LAUNCH", () => Launch(session), primary: true));
            }

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            scroll.Add(column);
            _main.Add(scroll);
        }

        private Button MapTile(MapDefinition map)
        {
            var id = map.MapId;
            var tile = new Button(() => { _mapId = id; Refresh(); });
            tile.AddToClassList("map-tile");
            if (id == _mapId)
            {
                tile.AddToClassList("map-tile-on");
            }

            var texture = RaidArt.Map(id);
            if (texture != null)
            {
                tile.style.backgroundImage = new StyleBackground(texture);
            }

            var caption = new Label(map.Name.ToUpperInvariant());
            caption.AddToClassList("map-tile-name");
            tile.Add(caption);
            return tile;
        }

        private void DrawTrip(CharacterSession session, Expedition trip)
        {
            var map = _maps[trip.MapId];
            var zone = map.Zone(trip.ZoneId, trip.Night);
            var placeName = map.Locations != null && trip.LocationId != null
                ? map.Locations.Get(trip.LocationId).Name
                : zone.Name;
            var column = Column();
            column.style.flexGrow = 1;
            column.Add(Heading(map.Name.ToUpperInvariant() + "   ·   " + placeName.ToUpperInvariant()
                + (trip.Night ? "   ·   NIGHT" : "   ·   DAY")));
            DrawKilledInAction(session, trip, column);
            if (trip.Mode == ExpeditionModes.Simulation)
            {
                DrawSimulated(session, trip, column);
            }
            else
            {
                DrawDirect(session, trip, column);
            }

            var latest = Snapshots(session).Latest(session.Profile.ProfileId);
            if (latest != null)
            {
                var rollback = Bar("ROLL BACK TO BEFORE THIS TRIP", () => RollBack(session, latest));
                rollback.style.marginTop = 16;
                column.Add(rollback);
                var note = new Label("Snapshot kept "
                    + latest.SavedUtc.ToString("yyyy-MM-dd HH:mm")
                    + " UTC. Rolling back drops this trip and puts the character, gear and stash back "
                    + "the way they were before it started.");
                note.AddToClassList("text-muted");
                note.style.whiteSpace = WhiteSpace.Normal;
                note.style.maxWidth = 520;
                note.style.marginTop = 6;
                column.Add(note);
            }

            _log.text = string.Join("\n", trip.Log.TakeLast(12));
            _main.Add(column);
        }

        /// <summary>
        /// What the trip cost, at the top of the screen where it cannot be missed. Head or thorax at
        /// zero is death, not a knockdown: nothing treats those parts, the rig and backpack the character
        /// was carrying were emptied where they fell, and the roster keeps them as DEAD. A rollback to
        /// the snapshot taken before the trip is the only way back.
        /// </summary>
        private void DrawKilledInAction(CharacterSession session, Expedition trip, VisualElement column)
        {
            if (session.Repository == null)
            {
                return;
            }

            var lost = new List<string>();
            foreach (var profileId in trip.Party)
            {
                var member = session.Repository.Load(profileId);
                if (member.Status == CharacterSheet.Dead)
                {
                    lost.Add(member.DisplayName);
                }
            }

            if (lost.Count == 0)
            {
                return;
            }

            column.Add(KilledPanel(lost, "No medkit treats a head or a thorax, so there is nothing to walk back. "
                + "What they were carrying is gone. The roster lists them as DEAD, and ROLL BACK TO BEFORE THIS "
                + "TRIP is the only way to undo it."));
        }

        /// <summary>
        /// The window that ends the save: a scrim over the whole screen, what happened, and one door -
        /// the roster. Nothing on the raid screen is reachable under it, so a dead character cannot be
        /// played on by accident.
        /// </summary>
        private void ShowDeathNotice(CharacterSession session)
        {
            if (_deathOverlay != null)
            {
                return;
            }

            var overlay = new VisualElement { pickingMode = PickingMode.Position };
            overlay.AddToClassList("kia-overlay");

            var panel = new VisualElement { pickingMode = PickingMode.Position };
            panel.AddToClassList("kia-modal");
            overlay.Add(panel);

            var title = new Label("KILLED IN ACTION");
            title.AddToClassList("kia-title");
            title.style.fontSize = 16;
            panel.Add(title);

            var who = new Label(session.Profile.DisplayName.ToUpperInvariant());
            who.AddToClassList("kia-modal-name");
            panel.Add(who);

            var body = new Label("Head or thorax at zero is death, not a knockdown: no medkit treats either, and "
                + "nothing brings a character back. What they were carrying went with them.");
            body.AddToClassList("kia-modal-body");
            panel.Add(body);

            var fate = new Label("This save cannot be played again. Pick another character on the character "
                + "screen to keep playing; DELETE there removes this one for good.");
            fate.AddToClassList("kia-modal-body");
            fate.style.marginBottom = 0;
            panel.Add(fate);

            var back = new Button(() => ScreenNavigator.Go("character"))
            {
                text = "BACK TO CHARACTERS",
                style = { width = 300, height = 40, marginTop = 18 },
            };
            back.AddToClassList("btn");
            back.AddToClassList("btn-primary");
            panel.Add(back);

            _root.Add(overlay);
            _deathOverlay = overlay;
        }

        private void HideDeathNotice()
        {
            if (_deathOverlay == null)
            {
                return;
            }

            _deathOverlay.RemoveFromHierarchy();
            _deathOverlay = null;
        }

        /// <summary>The red block that says a character is dead, wherever the screen has to say it.</summary>
        private static VisualElement KilledPanel(IEnumerable<string> names, string body)
        {
            var panel = new VisualElement { style = { marginBottom = 12 } };
            panel.AddToClassList("kia-panel");

            var title = new Label("KILLED IN ACTION");
            title.AddToClassList("kia-title");
            panel.Add(title);

            var who = new Label(string.Join("   ·   ", names).ToUpperInvariant());
            who.AddToClassList("text-mono");
            who.AddToClassList("text-danger");
            who.style.fontSize = 15;
            who.style.marginBottom = 6;
            panel.Add(who);

            var text = new Label(body);
            text.AddToClassList("kia-body");
            panel.Add(text);
            return panel;
        }

        private void DrawSimulated(CharacterSession session, Expedition trip, VisualElement column)
        {
            if (trip.Status == ExpeditionStatus.Active)
            {
                var zoneSeconds = ExpeditionRules.ZoneSeconds(trip.ZoneId, trip.Night);
                var left = trip.ZoneStartedAt + zoneSeconds - (long)Now();
                var clock = left > 0 ? $"{left / 60}:{left % 60:00}" : "resolving…";
                var note = new Label(
                    "CLOCK MODE - this trip runs on the real clock: the "
                    + trip.ZoneId + " zone takes " + zoneSeconds / 60
                    + " minutes" + (trip.Night ? " (night adds 50%)" : "")
                    + ". Nothing happens on this screen until the timer runs out. "
                    + "The node map is the mode you can play right now.");
                note.AddToClassList("text-muted");
                note.style.maxWidth = 520;
                note.style.whiteSpace = WhiteSpace.Normal;
                column.Add(note);
                var countdown = new Label("Zone resolves in " + clock);
                countdown.style.fontSize = 20;
                countdown.style.marginTop = 10;
                column.Add(countdown);
            }
            else if (trip.Status == ExpeditionStatus.AwaitingChoice)
            {
                if (ExpeditionRules.CanGoDeeper(trip))
                {
                    var deeper = ChoiceButton("GO DEEPER", true,
                        () => Run(() => Expeditions(session).ChooseContinue(trip, Now()), session));
                    column.Add(deeper);
                }

                var extract = ChoiceButton("EXTRACT", false,
                    () => Run(() => Expeditions(session).ChooseExtract(trip), session));
                extract.style.marginTop = 6;
                column.Add(extract);
            }
            else
            {
                column.Add(new Label(trip.Outcome == "wiped" ? "The party is down." : "The trip is over."));
            }
        }

        private void DrawDirect(CharacterSession session, Expedition trip, VisualElement column)
        {
            if (trip.Status == ExpeditionStatus.Completed || trip.Route == null)
            {
                column.Add(new Label(trip.Outcome == "wiped" ? "The party is down." : "Extracted."));
                return;
            }

            var rules = RaidLoader.Load();
            var gear = GearLoader.Load(Catalog());
            var node = trip.Route.Nodes[trip.Route.CurrentNodeId];
            var mapView = new RouteMap();
            mapView.Show(trip.Route, trip.Status == ExpeditionStatus.Active,
                id => Run(() => Expeditions(session).Move(trip, id), session));
            column.Add(mapView);
            DrawDossier(session, trip, node, rules, gear);
        }

        private void DrawDossier(CharacterSession session, Expedition trip, RouteNode node, RaidRules rules, GearData gear)
        {
            var kind = rules.Kinds[node.Kind];
            var container = string.IsNullOrEmpty(node.ContainerType) ? "container" : node.ContainerType.Replace("-", " ");
            var boss = "boss";
            MobStats mob;
            if (!string.IsNullOrEmpty(node.BossMobId) && gear.Mobs.TryGetValue(node.BossMobId, out mob))
            {
                boss = mob.Name;
            }

            var kicker = new Label("DIRECT");
            kicker.AddToClassList("raid-kicker");
            _detail.Add(kicker);
            var title = new Label(Fill(kind.Title, container, boss).ToUpperInvariant());
            title.AddToClassList("raid-title");
            _detail.Add(title);
            var body = new Label(Fill(kind.Text, container, boss));
            body.AddToClassList("raid-body");
            _detail.Add(body);

            if (node.Kind == "exit")
            {
                var map = _maps[trip.MapId];
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };
                var extract = ChoiceButton("EXTRACT", true,
                    () => Run(() => Expeditions(session).LeaveZone(trip, "extract"), session));
                extract.style.flexGrow = 1;
                extract.style.minWidth = 100;
                row.Add(extract);

                if (map.Locations != null && trip.LocationId != null)
                {
                    var here = map.Locations.Get(trip.LocationId);
                    if (ExpeditionRules.CanGoDeeper(trip, map))
                    {
                        // One button per place this location leads to, instead of a single "go deeper": the
                        // player is choosing a destination on the map, not just descending a fixed depth ladder.
                        foreach (var neighborId in here.ConnectsTo)
                        {
                            var neighbor = map.Locations.Get(neighborId);
                            var destinationId = neighborId;
                            var go = ChoiceButton(neighbor.Name.ToUpperInvariant(), false,
                                () => Run(() => Expeditions(session).LeaveZone(trip, destinationId), session));
                            go.style.flexGrow = 1;
                            go.style.minWidth = 100;
                            go.style.marginLeft = 8;
                            row.Add(go);
                        }
                    }
                    else if (here.ConnectsTo.Count > 0)
                    {
                        // Not a dead end - the cap (ExpeditionRules.MaxNodesPerExpedition) is why only EXTRACT shows.
                        var note = new Label("The party has pushed as far as they safely can this trip.");
                        note.AddToClassList("text-muted");
                        note.style.whiteSpace = WhiteSpace.Normal;
                        note.style.marginTop = 6;
                        _detail.Add(note);
                    }
                }
                else if (ExpeditionRules.CanGoDeeper(trip))
                {
                    var deeper = ChoiceButton("GO DEEPER", false,
                        () => Run(() => Expeditions(session).LeaveZone(trip, "deeper"), session));
                    deeper.style.flexGrow = 1;
                    deeper.style.marginLeft = 8;
                    row.Add(deeper);
                }

                _detail.Add(row);
            }
            else if (node.Cleared)
            {
                _detail.Add(new Label("Already dealt with. Follow a gold line.") { style = { whiteSpace = WhiteSpace.Normal } });
                _detail[_detail.childCount - 1].AddToClassList("text-muted");
            }
            else if (kind.Choices.Count == 0)
            {
                _detail.Add(new Label("Follow a gold line. Moving can draw a patrol.") { style = { whiteSpace = WhiteSpace.Normal } });
                _detail[_detail.childCount - 1].AddToClassList("text-muted");
            }
            else
            {
                for (var index = 0; index < kind.Choices.Count; index++)
                {
                    var choice = index;
                    _detail.Add(ChoiceButton(kind.Choices[choice].Label, false,
                        () => Run(() => Expeditions(session).Choose(trip, choice), session)));
                }
            }

            var party = new VisualElement();
            party.AddToClassList("raid-party");
            party.Add(new Label("PARTY"));
            party[0].AddToClassList("text-label");

            // Out here the stash is back home, so the carried grids are the only meds and the only bin -
            // say so once rather than leaving the player to find the double click.
            var tip = new Label("Double-click a medkit in a rig or backpack to use it; drag anything onto the bin to throw it away.");
            tip.AddToClassList("text-muted");
            tip.style.whiteSpace = WhiteSpace.Normal;
            tip.style.fontSize = 10;
            tip.style.marginTop = 4;
            tip.style.marginBottom = 6;
            party.Add(tip);
            var catalogForCards = Catalog();
            var gearForCards = GearLoader.Load(catalogForCards);
            foreach (var profileId in trip.Party)
            {
                var member = session.Repository.Load(profileId);
                var haul = trip.Loot.TryGetValue(profileId, out var carried) ? carried : new List<string>();
                var used = haul.Sum(id => catalogForCards[id].Width * catalogForCards[id].Height);
                party.Add(MemberCard(member, used, ExpeditionRules.CarryLimit(member, gearForCards)));
                party.Add(Haul(member, trip));
            }

            _detail.Add(party);
        }

        /// <summary>A full-width bar in the screen's own style, not Unity's default button.</summary>
        private static Button Bar(string text, Action action, bool primary = false)
        {
            var button = new Button(action) { text = text };
            button.AddToClassList("btn");
            button.AddToClassList(primary ? "btn-primary" : "btn-outline");
            return button;
        }

        private static Button ChoiceButton(string text, bool primary, Action action)
        {
            var button = new Button(action) { text = text };
            button.AddToClassList("btn");
            button.AddToClassList(primary ? "btn-primary" : "btn-outline");
            button.AddToClassList("raid-choice");
            return button;
        }

        /// <summary>
        /// The carried grids themselves: the rig above the backpack, one box per item, placed exactly as
        /// the GEAR screen places it so the two screens agree. An item drags between the cells, or onto
        /// the bin.
        /// </summary>
        private VisualElement Haul(Profile profile, Expedition trip)
        {
            var box = new VisualElement { style = { marginTop = 6, marginBottom = 4 } };
            box.Add(FloorRow(profile, trip));
            box.Add(GridBlock("RIG", profile.Rig, profile, trip, true));
            box.Add(GridBlock("BACKPACK", profile.Backpack, profile, trip, false));
            return box;
        }

        /// <summary>The finds that are still on the floor. Drag one onto a cell to take it, or onto the
        /// bin to throw it away.</summary>
        private VisualElement FloorRow(Profile profile, Expedition trip)
        {
            var floorCatalog = Catalog();
            var box = new VisualElement { style = { marginBottom = 6 } };
            var carried = trip.Loot.TryGetValue(profile.ProfileId, out var floor) ? floor : new List<string>();

            var head = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 2 },
            };
            var title = new Label("ON THE FLOOR   " + carried.Count);
            title.AddToClassList("text-label");
            title.style.fontSize = 10;
            title.style.marginRight = 8;
            head.Add(title);
            if (AnythingToBin(profile, trip))
            {
                head.Add(FloorBin(profile.ProfileId)); // nothing to throw away means nothing to show
            }

            box.Add(head);

            if (carried.Count == 0)
            {
                var empty = new Label("nothing yet");
                empty.AddToClassList("text-muted");
                empty.style.fontSize = 10;
                box.Add(empty);
                return box;
            }

            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };
            foreach (var itemId in carried)
            {
                if (!floorCatalog.TryGetValue(itemId, out var definition))
                {
                    continue;
                }

                var tile = new VisualElement { pickingMode = PickingMode.Position };
                tile.AddToClassList("cell");
                // .cell is position:absolute for the grids; on the floor the tiles must flow, or
                // every one of them stacks in the same corner and they cover each other.
                tile.style.position = Position.Relative;
                tile.style.width = definition.Width * RaidPitch;
                tile.style.height = definition.Height * RaidPitch;
                tile.style.marginRight = 4;
                tile.style.marginBottom = 4;
                var icon = _icons.Get(itemId);
                if (icon != null)
                {
                    tile.style.backgroundImage = new StyleBackground(icon);
                    tile.style.unityBackgroundScaleMode = ScaleMode.ScaleToFit;
                }

                var id = itemId;
                var w = definition.Width;
                var h = definition.Height;
                var owner = profile.ProfileId;
                tile.RegisterCallback<PointerDownEvent>(evt => BeginFloorDrag(evt, id, w, h, owner));
                row.Add(tile);
            }

            box.Add(row);
            return box;
        }

        /// <summary>
        /// One character's bin, beside their own floor row: a find belongs to the character who spotted
        /// it and a rig belongs to whoever is wearing it, so what goes in the bin has to be that
        /// character's. Dropping on it only arms it - the click after that is what destroys the thing.
        /// </summary>
        private Label FloorBin(string profileId)
        {
            var bin = new Label("× BIN") { pickingMode = PickingMode.Position };
            bin.AddToClassList("trash-bin");
            bin.AddToClassList("text-label");
            bin.style.fontSize = 11;
            bin.style.height = 26;
            bin.style.paddingLeft = 10;
            bin.style.paddingRight = 10;
            bin.style.unityTextAlign = TextAnchor.MiddleCenter;
            bin.style.color = BinIdleColor;
            bin.tooltip = BinHint;

            var owner = profileId;
            bin.RegisterCallback<PointerDownEvent>(_ => OnBinClicked(owner));
            _bins.Add((bin, profileId));
            return bin;
        }

        private VisualElement BeginFloorGhost(PointerDownEvent down, string itemId, int width, int height)
        {
            var ghost = new VisualElement { pickingMode = PickingMode.Ignore };
            ghost.AddToClassList("cell");
            ghost.style.position = Position.Absolute;
            ghost.style.width = width * RaidPitch;
            ghost.style.height = height * RaidPitch;
            ghost.style.opacity = 0.75f;
            var icon = _icons.Get(itemId);
            if (icon != null)
            {
                ghost.style.backgroundImage = new StyleBackground(icon);
                ghost.style.unityBackgroundScaleMode = ScaleMode.ScaleToFit;
            }

            _root.Add(ghost);
            return ghost;
        }

        private void BeginFloorDrag(PointerDownEvent down, string itemId, int width, int height, string floorOwner)
        {
            CancelDrag();
            _dragFromFloor = true;
            _dragFloorItem = itemId;
            _dragFloorProfile = floorOwner;
            _dragGrab = down.localPosition;
            _dragStartPointer = down.position;
            _dragGhost = BeginFloorGhost(down, itemId, width, height);
            MoveGhost(down.position);
            _dragActive = true;
            _root.RegisterCallback<PointerMoveEvent>(OnDragMove, TrickleDown.TrickleDown);
            _root.RegisterCallback<PointerUpEvent>(OnDragUp, TrickleDown.TrickleDown);
        }

        // ---- the bin ----

        /// <summary>What is waiting in the bin for its confirming click, or null. For tests.</summary>
        public string ArmedForTrash => _binnedInstanceId ?? _binnedFloorItem;

        private void OnBinClicked(string profileId)
        {
            if (_binnedName == null || _binnedProfileId != profileId)
            {
                if (_hint != null)
                {
                    _hint.text = "Drop a find or something you are carrying on this, then click it to destroy it.";
                }

                return;
            }

            ConfirmBin();
        }

        /// <summary>
        /// Puts what was dropped in that character's bin, where it waits for one more click. Exactly one
        /// of the ids says what it was: a floor find comes by item type (there are no instances on the
        /// floor), a carried item by instance id.
        /// </summary>
        private void ArmBin(Label bin, string profileId, string name, string floorItem, string instanceId)
        {
            CancelBin();
            _binnedProfileId = profileId;
            _binnedFloorItem = floorItem;
            _binnedInstanceId = instanceId;
            _binnedName = name;
            _binnedBin = bin;
            bin.AddToClassList("trash-bin-armed");
            bin.style.color = BinArmedColor;
            bin.text = "SURE?";
            bin.tooltip = "Click to destroy " + name;
            if (_hint != null)
            {
                _hint.text = name + " is in the bin. Click the bin to destroy it; drag anything else to keep it.";
            }
        }

        /// <summary>Takes whatever is in the bin back out, destroying nothing.</summary>
        private void CancelBin()
        {
            if (_binnedBin != null)
            {
                _binnedBin.RemoveFromClassList("trash-bin-armed");
                _binnedBin.RemoveFromClassList("trash-bin-over");
                _binnedBin.text = "× BIN";
                _binnedBin.style.color = BinIdleColor;
                _binnedBin.tooltip = BinHint;
            }

            _binnedProfileId = null;
            _binnedFloorItem = null;
            _binnedInstanceId = null;
            _binnedName = null;
            _binnedBin = null;
        }

        /// <summary>
        /// The confirming click: it is gone. Nothing goes into the trip's discarded list - that list is
        /// what was left behind, and this was thrown away on purpose.
        /// </summary>
        private void ConfirmBin()
        {
            var profileId = _binnedProfileId;
            var floorItem = _binnedFloorItem;
            var instanceId = _binnedInstanceId;
            var name = _binnedName;
            CancelBin();

            var session = GearScreenController.Current?.Session;
            if (session == null || profileId == null || (floorItem == null && instanceId == null))
            {
                Refresh();
                return;
            }

            string failure = null;
            try
            {
                var trip = OpenTrip(session);
                if (trip != null)
                {
                    if (instanceId != null)
                    {
                        failure = Expeditions(session).DestroyCarried(trip, profileId, instanceId);
                    }
                    else
                    {
                        Expeditions(session).DestroyLoot(trip, profileId, floorItem);
                    }

                    session.Reload();
                }
            }
            catch (Exception error)
            {
                failure = error.Message;
            }

            Refresh();
            if (_error != null && failure != null)
            {
                _error.text = failure; // Refresh clears it, so it goes up afterwards
            }
            else if (_hint != null && name != null)
            {
                _hint.text = name + " destroyed.";
            }
        }

        /// <summary>Marks the bin the pointer is over, so a drop that will land is visible before it does.</summary>
        private void HighlightBins(Vector2 pointer)
        {
            var owner = _dragFromFloor ? _dragFloorProfile : _dragCarriedProfile;
            foreach (var (bin, profileId) in _bins)
            {
                var over = owner != null && profileId == owner && bin.worldBound.Contains(pointer);
                bin.EnableInClassList("trash-bin-over", over);
            }
        }

        private string NameOf(string itemId) =>
            Catalog().TryGetValue(itemId, out var item) ? item.Name : itemId;

        /// <summary>Whether this character has anything a bin could take: a find, or a bag that is not empty.</summary>
        private static bool AnythingToBin(Profile profile, Expedition trip) =>
            (trip.Loot.TryGetValue(profile.ProfileId, out var floor) && floor.Count > 0)
            || profile.Rig.Stash.Count > 0
            || profile.Backpack.Stash.Count > 0;

        private VisualElement GridBlock(string caption, StashGrid grid, Profile profile, Expedition trip,
            bool isBackpack)
        {
            var block = new VisualElement { style = { marginBottom = 6 } };

            var head = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, justifyContent = Justify.SpaceBetween },
            };
            var title = new Label(caption);
            title.AddToClassList("text-label");
            title.style.fontSize = 10;
            head.Add(title);
            var size = new Label(grid.StashWidth + " × " + grid.StashHeight + "   "
                + grid.Stash.Count + (grid.Stash.Count == 1 ? " item" : " items"));
            size.AddToClassList("text-mono");
            size.AddToClassList("text-muted");
            size.style.fontSize = 10;
            head.Add(size);
            block.Add(head);

            var board = new VisualElement
            {
                style =
                {
                    width = grid.StashWidth * RaidPitch,
                    height = grid.StashHeight * RaidPitch,
                    backgroundColor = new Color(0.078f, 0.09f, 0.106f),
                    borderTopWidth = 1,
                    borderBottomWidth = 1,
                    borderLeftWidth = 1,
                    borderRightWidth = 1,
                    borderTopColor = new Color(0.141f, 0.169f, 0.2f),
                    borderBottomColor = new Color(0.141f, 0.169f, 0.2f),
                    borderLeftColor = new Color(0.141f, 0.169f, 0.2f),
                    borderRightColor = new Color(0.141f, 0.169f, 0.2f),
                    marginTop = 2,
                },
            };

            for (var y = 0; y < grid.StashHeight; y++)
            {
                for (var x = 0; x < grid.StashWidth; x++)
                {
                    var line = new VisualElement
                    {
                        style =
                        {
                            position = Position.Absolute,
                            left = x * RaidPitch,
                            top = y * RaidPitch,
                            width = RaidPitch,
                            height = RaidPitch,
                            borderTopWidth = 1,
                            borderLeftWidth = 1,
                            borderTopColor = new Color(0.114f, 0.137f, 0.161f),
                            borderLeftColor = new Color(0.114f, 0.137f, 0.161f),
                        },
                    };
                    board.Add(line);
                }
            }

            var catalog = Catalog();
            foreach (var instance in grid.Stash)
            {
                if (!catalog.TryGetValue(instance.ItemId, out var definition))
                {
                    continue;
                }

                PlacementRules.Footprint(definition, instance.Rotation, out var width, out var height);
                var instanceId = instance.InstanceId;
                var carried = instance;
                var owner = profile.ProfileId;
                var usable = RaidGear.Meds.ContainsKey(instance.ItemId);
                var tile = new VisualElement { pickingMode = PickingMode.Position };
                // TrickleDown, like the GEAR screen's cells: anything a manipulator does with the press
                // happens after this, so the drag and the double click are seen first.
                tile.RegisterCallback<PointerDownEvent>(evt =>
                    OnCarriedDown(evt, tile, carried, isBackpack, width, height, definition, owner),
                    TrickleDown.TrickleDown);
                tile.AddToClassList("cell");
                tile.style.position = Position.Absolute;
                tile.style.left = instance.X * RaidPitch;
                tile.style.top = instance.Y * RaidPitch;
                tile.style.width = width * RaidPitch;
                tile.style.height = height * RaidPitch;
                tile.tooltip = definition.Name
                    + (usable ? " - double-click to use it, or drag it onto a cell" : " - drag onto a cell to move it, or onto the bin");

                var icon = _icons.Get(instance.ItemId);
                if (icon != null)
                {
                    tile.style.backgroundImage = new StyleBackground(icon);
                    tile.style.unityBackgroundScaleMode = ScaleMode.ScaleToFit;
                }

                board.Add(tile);
            }

            block.Add(board);
            _boards.Add((board, isBackpack, grid, trip, profile.ProfileId, profile));
            return block;
        }

        private bool _dragActive;
        private bool _dragFromFloor;
        private string _dragFloorItem;
        private string _dragFloorProfile; // whose floor row the dragged find came from
        private string _dragCarriedProfile; // whose rig or backpack the dragged item came from

        // A press and release in the same place is a click, not a drop: `_dragStartPointer` is where the press
        // landed, so OnDragUp can tell a drag from a click. The click timestamps are the double-click backstop.
        private Vector3 _dragStartPointer;
        private string _lastClickInstanceId;
        private float _lastClickTime;

        private GearData _raidGear;

        /// <summary>gear.json, read once: the meds table is what decides whether a cell can be double-clicked.</summary>
        private GearData RaidGear => _raidGear ?? (_raidGear = GearLoader.Load(Catalog()));

        /// <summary>
        /// A press on a carried item: the second press inside the double-click window uses it instead of
        /// picking it up, so a medkit can be spent without leaving the raid.
        /// </summary>
        private void OnCarriedDown(PointerDownEvent down, VisualElement tile, ItemInstance instance, bool fromBackpack,
            int width, int height, ItemDefinition definition, string owner)
        {
            if (down.button == 1)
            {
                // Right click does the same thing without the rhythm: out in a raid a hand wants the bandage
                // now, and it is the one press that cannot be confused with picking something up.
                CancelDrag();
                UseCarriedMed(owner, instance);
                return;
            }

            if (down.button != 0)
            {
                return;
            }

            // Two ways to recognise a double click: the runtime's own count on the pointer event, and a
            // timestamp of the last press on this same instance, in case the platform leaves the count at 1.
            var now = Time.unscaledTime;
            var twice = down.clickCount >= 2
                        || (instance.InstanceId == _lastClickInstanceId && now - _lastClickTime <= 0.4f);
            _lastClickInstanceId = instance.InstanceId;
            _lastClickTime = now;
            if (twice)
            {
                _lastClickInstanceId = null; // a third press starts over
                CancelDrag();
                UseCarriedMed(owner, instance);
                return;
            }

            // One press on a kit says what the second press would do - both a hint and the way to tell that
            // the press reached the cell at all.
            if (RaidGear.Meds.ContainsKey(instance.ItemId) && _hint != null)
            {
                _hint.text = NameOf(instance.ItemId) + ": double-click (or right-click) to use it.";
            }

            BeginDrag(down, tile, instance, fromBackpack, width, height, definition, owner);
        }

        /// <summary>
        /// Uses a medkit out of the rig or backpack and says what it did. The refusal (not a medkit, nothing
        /// to treat, dead) goes to the red line; the outcome goes to the hint, because it is not a failure.
        /// </summary>
        private void UseCarriedMed(string profileId, ItemInstance instance)
        {
            var session = GearScreenController.Current?.Session;
            if (session == null)
            {
                return;
            }

            string failure = null;
            string message = null;
            try
            {
                var trip = OpenTrip(session);
                if (trip != null)
                {
                    (failure, message) = Expeditions(session).UseCarriedMed(trip, profileId, instance.InstanceId);
                    session.Reload();
                }
            }
            catch (Exception error)
            {
                failure = error.Message;
            }

            Refresh(); // the grids and the hint are rebuilt from the file, so the outcome goes up after
            if (failure != null)
            {
                _error.text = failure;
            }
            else if (message != null && _hint != null)
            {
                _hint.text = message;
            }
        }

        /// <summary>Lifts an item into a ghost that follows the pointer, and drops it on a cell.</summary>
        private void BeginDrag(PointerDownEvent down, VisualElement tile, ItemInstance instance, bool fromBackpack,
            int width, int height, ItemDefinition definition, string owner)
        {
            CancelDrag();
            _dragItem = instance;
            _dragFromBackpack = fromBackpack;
            _dragCarriedProfile = owner;
            _dragGrab = down.localPosition;
            _dragStartPointer = down.position;

            _dragGhost = new VisualElement { pickingMode = PickingMode.Ignore };
            _dragGhost.AddToClassList("cell");
            _dragGhost.style.position = Position.Absolute;
            _dragGhost.style.width = width * RaidPitch;
            _dragGhost.style.height = height * RaidPitch;
            _dragGhost.style.opacity = 0.75f;
            var icon = _icons.Get(instance.ItemId);
            if (icon != null)
            {
                _dragGhost.style.backgroundImage = new StyleBackground(icon);
                _dragGhost.style.unityBackgroundScaleMode = ScaleMode.ScaleToFit;
            }

            _root.Add(_dragGhost);
            MoveGhost(down.position);

            // On the root, not the tile: a Button's own click manipulator competes for the pointer, and
            // with capture on the tile the move events never turned up.
            _dragActive = true;
            _root.RegisterCallback<PointerMoveEvent>(OnDragMove, TrickleDown.TrickleDown);
            _root.RegisterCallback<PointerUpEvent>(OnDragUp, TrickleDown.TrickleDown);
        }

        private void MoveGhost(Vector2 pointer)
        {
            if (_dragGhost == null)
            {
                return;
            }

            var local = _root.WorldToLocal(pointer);
            _dragGhost.style.left = local.x - _dragGrab.x;
            _dragGhost.style.top = local.y - _dragGrab.y;
        }

        private void OnDragMove(PointerMoveEvent move)
        {
            MoveGhost(move.position);
            HighlightBins(move.position);
        }

        private void OnDragUp(PointerUpEvent up)
        {
            var item = _dragItem;
            var fromFloor = _dragFromFloor;
            var floorItem = _dragFloorItem;
            var carriedOwner = _dragCarriedProfile;
            var floorOwner = _dragFloorProfile;
            var fromBackpack = _dragFromBackpack;
            var grab = _dragGrab;
            var start = _dragStartPointer;
            var moved = (up.position - start).magnitude > 4f;
            CancelDrag();

            // A press and release in the same place is a click, not a drop: it must not move the item to
            // the cell it is already in (which for a turned item is not even its own cell).
            if (!moved || (item == null && !fromFloor) || _expeditions == null)
            {
                return;
            }

            // A bin takes what its own character is holding - a find that landed on their floor, or
            // something in their rig or backpack. Another character's bin refuses it.
            foreach (var (bin, profileId) in _bins)
            {
                if (!bin.worldBound.Contains(up.position))
                {
                    continue;
                }

                var owner = fromFloor ? floorOwner : carriedOwner;
                if (profileId != owner)
                {
                    _error.text = "That bin belongs to another character - use your own.";
                    return;
                }

                if (fromFloor)
                {
                    ArmBin(bin, profileId, NameOf(floorItem), floorItem, null);
                }
                else
                {
                    ArmBin(bin, profileId, NameOf(item.ItemId), null, item.InstanceId);
                }

                return;
            }

            foreach (var board in _boards)
            {
                var box = board.Board.worldBound;
                if (!box.Contains(up.position))
                {
                    continue;
                }

                var local = board.Board.WorldToLocal(up.position) - grab;
                var x = Mathf.RoundToInt(local.x / RaidPitch);
                var y = Mathf.RoundToInt(local.y / RaidPitch);
                var error = fromFloor
                    ? _expeditions.StowLoot(board.Trip, board.ProfileId, floorItem, !board.IsBackpack, x, y, 0)
                    : _expeditions.MoveCarried(board.ProfileId, item.InstanceId, board.IsBackpack, x, y, 0);

                Refresh(); // clears the error line, so a refusal goes up after it
                if (error != null)
                {
                    _error.text = error;
                }

                return;
            }

            // Dropped outside both grids: nothing moves. The item is still where it was.
            System.Action noop = null;
            noop?.Invoke();
        }

        private void CancelDrag()
        {
            if (_dragActive)
            {
                _dragActive = false;
                _root.UnregisterCallback<PointerMoveEvent>(OnDragMove, TrickleDown.TrickleDown);
                _root.UnregisterCallback<PointerUpEvent>(OnDragUp, TrickleDown.TrickleDown);
            }

            _dragGhost?.RemoveFromHierarchy();
            _dragGhost = null;
            _dragItem = null;
            _dragFromFloor = false;
            _dragFloorItem = null;
            _dragFloorProfile = null;
            _dragCarriedProfile = null;
            foreach (var (bin, _) in _bins)
            {
                bin.RemoveFromClassList("trash-bin-over");
            }
        }

        private static VisualElement MemberCard(Profile profile, int lootCells, int carryLimit)
        {
            var card = new VisualElement { style = { marginTop = 8 } };
            var header = new VisualElement { style = { flexDirection = FlexDirection.Row, justifyContent = Justify.SpaceBetween } };
            header.Add(new Label(profile.DisplayName));
            var state = profile.Status == CharacterSheet.Downed ? "DOWN"
                : profile.Status == CharacterSheet.Dead ? "KIA"
                : "IN THE FIELD";
            var stateLabel = new Label(state);
            var hurt = profile.Status == CharacterSheet.Downed || profile.Status == CharacterSheet.Dead;
            stateLabel.AddToClassList(hurt ? "text-danger" : "text-muted");
            header.Add(stateLabel);
            card.Add(header);

            var loot = new Label("LOOT " + lootCells + " / " + carryLimit + " CELLS");
            loot.AddToClassList("text-mono");
            loot.AddToClassList(lootCells >= carryLimit ? "text-danger" : "text-muted");
            loot.style.fontSize = 11;
            loot.style.marginTop = 2;
            card.Add(loot);

            var hp = new Label(profile.Health + " / " + CharacterSheet.MaxHealth);
            hp.AddToClassList("text-mono");
            hp.style.marginTop = 2;
            hp.style.marginBottom = 6;
            card.Add(hp);

            var parts = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            foreach (var pair in CharacterSheet.BodyPartMaxHealth)
            {
                var column = new VisualElement { style = { flexGrow = 1, marginRight = 4 } };
                var caption = new Label(PartLabel(pair.Key));
                caption.AddToClassList("text-label");
                caption.style.unityTextAlign = TextAnchor.MiddleCenter;
                caption.style.fontSize = 8;
                column.Add(caption);
                var track = new VisualElement();
                track.AddToClassList("raid-part-track");
                var fill = new VisualElement();
                fill.AddToClassList("raid-part-fill");
                var ratio = pair.Value <= 0 ? 0f : Mathf.Clamp01(profile.BodyParts[pair.Key] / (float)pair.Value);
                fill.style.width = new Length(ratio <= 0f ? 100f : ratio * 100f, LengthUnit.Percent);
                if (ratio <= 0f)
                {
                    fill.AddToClassList("raid-part-down");
                }
                else if (ratio < 1f)
                {
                    fill.AddToClassList("raid-part-hurt");
                }

                track.Add(fill);
                column.Add(track);
                parts.Add(column);
            }

            card.Add(parts);
            var injuries = new Label(InjuryText(profile));
            injuries.AddToClassList("text-muted");
            injuries.style.whiteSpace = WhiteSpace.Normal;
            injuries.style.marginTop = 6;
            injuries.style.fontSize = 11;
            card.Add(injuries);
            return card;
        }

        private static string PartLabel(string part)
        {
            switch (part)
            {
                case "head": return "HD";
                case "thorax": return "TX";
                case "stomach": return "ST";
                case "left_arm": return "LA";
                case "right_arm": return "RA";
                case "left_leg": return "LL";
                case "right_leg": return "RL";
                default: return part;
            }
        }

        private static string InjuryText(Profile profile)
        {
            var lines = new List<string>();
            foreach (var part in CharacterSheet.BodyParts)
            {
                IReadOnlyList<string> conditions;
                if (!profile.Conditions.TryGetValue(part, out conditions) || conditions.Count == 0)
                {
                    continue;
                }

                var names = new List<string>();
                foreach (var condition in conditions)
                {
                    names.Add(condition.Replace('_', ' '));
                }

                lines.Add(part.Replace('_', ' ') + ": " + string.Join(", ", names));
            }

            return lines.Count == 0 ? "No injuries." : string.Join("\n", lines);
        }

        private void Run(Func<Expedition> action, CharacterSession session)
        {
            try
            {
                var done = action();
                session.Reload();
                _error.text = "";
                _notice = done.Status == ExpeditionStatus.Completed
                    ? (done.Outcome == "wiped"
                        ? "The party is down. Anything not already in the stash is lost."
                        : "Extracted. Standing members' loot is in the stash.")
                    : null;

                if (done.Status == ExpeditionStatus.Completed)
                {
                    AdvanceQuests(session, done);
                }
            }
            catch (Exception error)
            {
                _error.text = error.Message;
            }

            Refresh();
        }

        /// <summary>
        /// Hands a finished trip to the quest chain. Done here, where the trip actually ends, so a quest
        /// can only move when a trip finishes - and exactly once. The QUESTS screen never adds progress,
        /// it only reads it.
        /// </summary>
        private void AdvanceQuests(CharacterSession session, Expedition trip)
        {
            if (session == null || !session.Saves)
            {
                return;
            }

            try
            {
                var repository = new QuestRepository(session.Repository.DataFolder);
                var log = repository.Load(session.Profile.ProfileId);
                var advanced = QuestRules.Advance(log, QuestLoader.Load(), trip,
                    session.Profile.ProfileId, Catalog());
                if (!ReferenceEquals(advanced, log))
                {
                    repository.Save(session.Profile.ProfileId, advanced);
                }
            }
            catch (Exception error)
            {
                _error.text = "Quest progress was not saved: " + error.Message;
            }
        }

        /// <summary>Keeps a copy of the character first, so the trip it is about to start can be undone.</summary>
        private void Launch(CharacterSession session)        {
            try
            {
                Snapshots(session).Save(session.Profile, Now(), "before-launch");
            }
            catch (StorageException error)
            {
                _error.text = "No snapshot could be kept, so the trip was not started: " + error.Message;
                return;
            }

            Run(() => Expeditions(session).Launch(_mapId, _night, new[] { session.Profile }, Now(), _mode), session);
        }

        /// <summary>
        /// Puts the character back to a kept snapshot and drops the trip it is on, which is the only way
        /// out of a trip other than walking to an exit. The snapshot is validated and the trip file is
        /// removed before the character is committed, so a rollback that cannot finish leaves the
        /// character exactly as it was rather than half-restored.
        /// </summary>
        private void RollBack(CharacterSession session, SnapshotInfo snapshot)
        {
            try
            {
                var restored = Snapshots(session).Load(session.Profile.ProfileId, snapshot.SnapshotId);
                var trip = Expeditions(session).FindFor(session.Profile.ProfileId);
                if (trip != null)
                {
                    Trips(session).Delete(trip.ExpeditionId);
                }

                session.Commit(restored);
                _notice = "Rolled back to the snapshot taken before this trip.";
            }
            catch (Exception error)
            {
                _error.text = error.Message;
            }

            Refresh();
        }

        private SnapshotRepository Snapshots(CharacterSession session)
        {
            BuildAuxiliary(session);
            return _snapshots;
        }

        private ExpeditionRepository Trips(CharacterSession session)
        {
            BuildAuxiliary(session);
            return _trips;
        }

        private void BuildAuxiliary(CharacterSession session)
        {
            var folder = session.Repository.DataFolder;
            if (_snapshots != null && _auxFolder == folder)
            {
                return;
            }

            _auxFolder = folder;
            _snapshots = new SnapshotRepository(folder, Catalog());
            _trips = new ExpeditionRepository(folder);
        }

        private Expedition OpenTrip(CharacterSession session)
        {
            if (session == null || !session.Saves)
            {
                return null;
            }

            EnsureData(session);
            return Expeditions(session).FindFor(session.Profile.ProfileId);
        }

        private ExpeditionSession Expeditions(CharacterSession session)
        {
            var folder = session.Repository.DataFolder;
            if (_expeditions == null || _dataFolder != folder)
            {
                _dataFolder = folder;
                _expeditions = new ExpeditionSession(new ExpeditionRepository(folder), session.Repository,
                    session.AccountRepository, _maps, MapLoader.LoadContainers(Catalog()), GearLoader.Load(Catalog()),
                    CombatLoader.Load(), RaidLoader.Load(), Catalog());
            }

            return _expeditions;
        }

        private void EnsureData(CharacterSession session)
        {
            if (_maps != null)
            {
                return;
            }

            _maps = MapLoader.LoadMaps(Catalog());
            LocationLoader.LoadInto(_maps);
            if (!_maps.ContainsKey(_mapId))
            {
                _mapId = _maps.Keys.First();
            }
        }

        private static Dictionary<string, ItemDefinition> Catalog() => CatalogLoader.Load();

        private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        private static string Fill(string text, string container, string boss) =>
            (text ?? "").Replace("{container}", container).Replace("{boss}", boss);

        private static VisualElement Column() => new VisualElement { style = { flexDirection = FlexDirection.Column } };

        private static Label Heading(string text) => new Label(text)
        {
            style = { marginBottom = 10, unityFontStyleAndWeight = FontStyle.Bold },
        };
    }
}
