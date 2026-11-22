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

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;

            // Same as the other screens: the panel scales the 1440x900 design to the window but
            // keeps its proportions, so a window of another aspect leaves a strip of empty space on
            // one axis. Centre the design in it instead of letting it pile up on the right.
            _root.style.alignItems = Align.Center;
            _root.style.justifyContent = Justify.Center;

            _main = _root.Q("scavenge-main");
            _detail = _root.Q("scavenge-detail");
            _log = _root.Q<Label>("scavenge-log");
            _error = _root.Q<Label>("scavenge-error");
            _root.Q<Button>("navtab-character").clicked += () => ScreenNavigator.Go("character");
            _root.Q<Button>("navtab-gear").clicked += () => ScreenNavigator.Go("gear");
            _root.Q<Button>("navtab-hideout").clicked += () => ScreenNavigator.Go("hideout");
            _root.Q<Button>("navtab-traders").clicked += () => ScreenNavigator.Go("traders");
            _root.Q<Button>("navtab-settings").clicked += () => ScreenNavigator.Go("settings");
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

        private void Hide() => _root.style.display = DisplayStyle.None;

        private void Refresh()
        {
            _main.Clear();
            _detail.Clear();
            _error.text = "";
            var session = GearScreenController.Current?.Session;
            if (session == null || !session.Saves)
            {
                _main.Add(new Label("Open a saved character before sending anyone out."));
                return;
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
            _log.text = _notice ?? "Choose a map, a time of day, and whether the trip runs on the clock or on the node map.";
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
            foreach (var profileId in trip.Party)
            {
                party.Add(MemberCard(session.Repository.Load(profileId)));
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

        private static VisualElement MemberCard(Profile profile)
        {
            var card = new VisualElement { style = { marginTop = 8 } };
            var header = new VisualElement { style = { flexDirection = FlexDirection.Row, justifyContent = Justify.SpaceBetween } };
            header.Add(new Label(profile.DisplayName));
            var state = profile.Status == CharacterSheet.Downed ? "DOWN" : "IN THE FIELD";
            var stateLabel = new Label(state);
            stateLabel.AddToClassList(profile.Status == CharacterSheet.Downed ? "text-danger" : "text-muted");
            header.Add(stateLabel);
            card.Add(header);

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
            }
            catch (Exception error)
            {
                _error.text = error.Message;
            }

            Refresh();
        }

        /// <summary>Keeps a copy of the character first, so the trip it is about to start can be undone.</summary>
        private void Launch(CharacterSession session)
        {
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
