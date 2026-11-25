using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Safehouse.Core;

namespace Safehouse.Data
{
    /// <summary>
    /// One file per expedition, under expeditions/. A completed expedition is not "the character's current
    /// trip": once it is done, a downed member is read from their status.
    /// </summary>
    public sealed class ExpeditionRepository
    {
        private static readonly Regex IdPattern = new Regex("^[0-9a-f]{32}$", RegexOptions.Compiled);
        private readonly string _folder;

        public ExpeditionRepository(string dataFolder)
        {
            _folder = Path.Combine(dataFolder, "expeditions");
            DataFolder = dataFolder;
        }

        public string DataFolder { get; }

        public string PathFor(string expeditionId)
        {
            if (expeditionId == null || !IdPattern.IsMatch(expeditionId))
            {
                throw new StorageException($"Invalid expedition id: '{expeditionId}'.");
            }

            return Path.Combine(_folder, expeditionId + ".json");
        }

        public IReadOnlyList<string> ListIds()
        {
            if (!Directory.Exists(_folder))
            {
                return new string[0];
            }

            return Directory.GetFiles(_folder, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(id => IdPattern.IsMatch(id))
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
        }

        public Expedition Load(string expeditionId)
        {
            var path = PathFor(expeditionId);
            try
            {
                var text = File.ReadAllText(path);
                return ExpeditionSerializer.FromJson(JObject.Parse(text));
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                                          || error is Newtonsoft.Json.JsonException || error is ValidationException)
            {
                throw new StorageException($"Cannot load expedition {Path.GetFileName(path)}: {error.Message}\nThe file was left unchanged.", error);
            }
        }

        public void Save(Expedition expedition)
        {
            ExpeditionSerializer.FromJson(ExpeditionSerializer.ToJson(expedition));
            Directory.CreateDirectory(_folder);
            try
            {
                AtomicFile.Write(PathFor(expedition.ExpeditionId), ExpeditionSerializer.Serialize(expedition));
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                throw new StorageException($"Could not save the expedition: {error.Message}\nThe requested change was not applied.", error);
            }
        }

        public Expedition FindFor(string profileId)
        {
            foreach (var expeditionId in ListIds())
            {
                var expedition = Load(expeditionId);
                if (expedition.Party.Contains(profileId) && expedition.Status != ExpeditionStatus.Completed)
                {
                    return expedition;
                }
            }

            return null;
        }

        /// <summary>Drops a trip's file - what rolling a character back to its snapshot does, so the
        /// trip it was on cannot be picked up again afterwards.</summary>
        public void Delete(string expeditionId)
        {
            try
            {
                File.Delete(PathFor(expeditionId));
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                throw new StorageException($"Could not drop expedition {expeditionId}: {error.Message}", error);
            }
        }
    }

    /// <summary>
    /// Launch, catch up and resolve an expedition. The expedition file and every changed character are
    /// written together: if a later write fails, the files already written are put back.
    /// </summary>
    public sealed class ExpeditionSession
    {
        private readonly ExpeditionRepository _expeditions;
        private readonly ProfileRepository _profiles;
        private readonly AccountRepository _accounts;
        private readonly IReadOnlyDictionary<string, MapDefinition> _maps;
        private readonly ContainerRules _containers;
        private readonly GearData _gear;
        private readonly CombatNumbers _combat;
        private readonly RaidRules _raids;
        private readonly IReadOnlyDictionary<string, ItemDefinition> _catalog;
        private EventBook _events;

        /// <summary>Five in a hundred per node walked into: a scene is a surprise, not a schedule.</summary>
        private const int RaidEventPercent = 5;

        public ExpeditionSession(ExpeditionRepository expeditions, ProfileRepository profiles, AccountRepository accounts,
            IReadOnlyDictionary<string, MapDefinition> maps, ContainerRules containers, GearData gear,
            CombatNumbers combat, RaidRules raids, IReadOnlyDictionary<string, ItemDefinition> catalog)
        {
            _expeditions = expeditions;
            _profiles = profiles;
            _accounts = accounts;
            _maps = maps;
            _containers = containers;
            _gear = gear;
            _combat = combat;
            _raids = raids;
            _catalog = catalog;
        }

        public Expedition FindFor(string profileId) => _expeditions.FindFor(profileId);

        public Expedition Launch(string mapId, bool night, IReadOnlyList<Profile> party, double now, string mode)
        {
            if (!_maps.ContainsKey(mapId))
            {
                throw new ValidationException($"Unknown map: {mapId}.");
            }

            foreach (var profile in party)
            {
                if (profile.Status != CharacterSheet.Active)
                {
                    throw new ValidationException($"{profile.DisplayName} is not available for an expedition.");
                }
            }

            var map = _maps[mapId];
            var random = PythonRandom.Create();
            Route route = null;
            string zoneId = null;
            string locationId = null;
            if (mode == ExpeditionModes.Direct)
            {
                if (map.Locations != null)
                {
                    var entry = map.Locations.Get(map.Locations.EntryLocationId);
                    zoneId = entry.ZoneId;
                    locationId = entry.LocationId;
                }
                else
                {
                    zoneId = MapZones.Ids[0];
                }

                route = RaidMaps.Generate(map.Zone(zoneId, night), night, _raids, _gear, _combat, random);
            }

            var expedition = ExpeditionRules.Create(mapId, night, party.Select(profile => profile.ProfileId).ToList(),
                now, mode, route, random, zoneId, locationId);
            // The green mark is placed before the party takes a single step, so the first screen of the trip
            // already says where the story is waiting.
            expedition = MaybeRaiseEvent(expedition);
            var updated = party.ToDictionary(profile => profile.ProfileId,
                profile => profile.With(status: CharacterSheet.OnExpedition));
            Commit(expedition, updated, null);
            return expedition;
        }

        public Expedition Advance(Expedition expedition, double now)
        {
            if (!ExpeditionRules.Due(expedition, now))
            {
                return expedition;
            }

            var current = LoadParty(expedition);
            var fighters = Fighters(current);
            var limits = Limits(current);
            var resolved = ExpeditionRules.ResolveZone(expedition, _maps[expedition.MapId], _catalog,
                new LootTables(_catalog, _containers), _gear, _combat, fighters, limits);
            var changed = ExpeditionRules.SyncProfiles(current, resolved.Fighters);
            var recorded = ExpeditionRules.NoteDeaths(resolved.Expedition, current, changed);
            Commit(recorded, changed, null);
            return recorded;
        }

        /// <summary>
        /// Anything still on the floor is gone once the party walks on. Only what was dragged into the
        /// rig or backpack comes with them.
        /// </summary>
        private Expedition AbandonFloor(Expedition expedition)
        {
            if (expedition == null || expedition.Loot.Count == 0)
            {
                return expedition;
            }

            var names = expedition.Loot.Values.SelectMany(items => items)
                .Select(id => _catalog.TryGetValue(id, out var item) ? item.Name : id)
                .ToList();

            var discarded = expedition.Discarded.ToList();
            discarded.AddRange(names.Count == 0 ? new List<string>() : expedition.Loot.Values.SelectMany(items => items));

            var log = expedition.Log.ToList();
            log.Add("Left behind: " + string.Join(", ", names) + ".");

            var updated = expedition.With(loot: new Dictionary<string, List<string>>(),
                discarded: discarded, log: log);
            _expeditions.Save(updated);
            return updated;
        }

        public Expedition Move(Expedition expedition, string nodeId)
        {
            var current = LoadParty(expedition);
            expedition = AbandonFloor(expedition);
            var resolved = ExpeditionRules.Move(expedition, nodeId, _maps[expedition.MapId], _combat, _raids, Fighters(current));
            var changed = ExpeditionRules.SyncProfiles(current, resolved.Fighters);
            var recorded = ExpeditionRules.NoteDeaths(resolved.Expedition, current, changed);
            recorded = MaybeRaiseEvent(recorded);
            Commit(recorded, changed, null);
            return recorded;
        }

        /// <summary>
        /// A scene out here, rolled the way a patrol is rolled: walking into a node is when the street can happen
        /// to you. Five in a hundred, and only when nothing else is already on screen - a fight or another scene
        /// keeps the floor until it is answered.
        /// </summary>
        private Expedition MaybeRaiseEvent(Expedition expedition)
        {
            if (expedition.PendingEventId != null || expedition.Encounter != null
                || expedition.Status == ExpeditionStatus.Completed || expedition.Route == null)
            {
                return expedition;
            }

            var random = expedition.RngState.Restore();
            var log = new QuestRepository(_profiles.DataFolder).Load(expedition.Party[0]);
            // The party leader's history is the one the scenes are written against.
            var leader = _profiles.Load(expedition.Party[0]);
            var flags = leader.Flags;
            var abilities = leader.Abilities;

            // A main-line scene does not ambush the party at every corner: it picks a door, marks it green on the
            // map, and waits there. Walking into that node opens it.
            var mainLine = EventRules.NextMainLine(Book, log, EventPhases.Raid, flags, abilities);
            if (mainLine != null && !OnThisMap(mainLine, expedition))
            {
                // A scene pinned to another map waits: it does not mark a door here.
                mainLine = null;
            }
            if (mainLine != null)
            {
                if (expedition.EventNodeId == null)
                {
                    var target = PickEventNode(expedition, random, mainLine);
                    if (target == null)
                    {
                        return expedition;
                    }

                    var marked = expedition.Log.ToList();
                    marked.Add("There is somewhere you should be going.");
                    return expedition.With(eventNode: target,
                        rngState: state_of(random));
                }

                if (expedition.Route.CurrentNodeId == expedition.EventNodeId)
                {
                    var lines = expedition.Log.ToList();
                    lines.Add("Something happens here.");
                    UnityEngine.Debug.Log($"[events] raised '{mainLine.EventId}' at {expedition.EventNodeId}.");
                    return expedition.With(pendingEvent: mainLine.EventId, keepEventNode: false, log: lines,
                        rngState: state_of(random));
                }

                return expedition;
            }

            if (random.RandRange(100) >= RaidEventPercent)
            {
                return expedition;
            }

            var candidate = EventRules.NextRandom(Book, log, EventPhases.Raid, random, flags, abilities);
            if (candidate == null)
            {
                return expedition;
            }

            var extra = expedition.Log.ToList();
            extra.Add("Something happens here.");
            UnityEngine.Debug.Log($"[events] random '{candidate.EventId}' at {expedition.Route.CurrentNodeId}.");
            return expedition.With(pendingEvent: candidate.EventId, log: extra, rngState: state_of(random));

            RngState state_of(PythonRandom r)
            {
                var state = r.GetState();
                return new RngState(state.Version, state.Words, state.Index, state.GaussNext);
            }
        }

        /// <summary>Whether the scene belongs on the map this trip is walking.</summary>
        private static bool OnThisMap(StoryEvent scene, Expedition expedition) =>
            scene.Place?.MapId == null
            || string.Equals(scene.Place.MapId, expedition.MapId, StringComparison.Ordinal);

        /// <summary>
        /// Which door the story is behind. The scene says where it wants to happen - a kind of node, or nothing -
        /// and this honours that: a scene written for an exit is marked on an exit, a scene with no preference
        /// still prefers a container, because a scene belongs at something rather than in an empty corridor.
        /// </summary>
        private static string PickEventNode(Expedition expedition, PythonRandom random, StoryEvent scene)
        {
            var current = expedition.Route.CurrentNodeId;
            var wanted = scene?.Place?.NodeKind;
            var nodes = expedition.Route.Nodes.Values
                .Where(node => !node.Cleared && node.NodeId != current)
                .ToList();

            var candidates = !string.IsNullOrEmpty(wanted)
                ? nodes.Where(node => string.Equals(node.Kind, wanted, StringComparison.Ordinal)).ToList()
                : nodes.Where(node => node.Kind != "exit").ToList();

            if (candidates.Count == 0)
            {
                return null;
            }

            if (string.IsNullOrEmpty(wanted))
            {
                var containers = candidates.Where(node => node.Kind == "container").ToList();
                if (containers.Count > 0)
                {
                    candidates = containers;
                }
            }

            return candidates[random.RandRange(candidates.Count)].NodeId;
        }

        /// <summary>
        /// Answers the scene waiting in the raid: the choice lands on the character, the quest it finishes is
        /// advanced and saved, and the trip is free to move again. Returns why it was refused, or null.
        /// </summary>
        public string ResolveEvent(Expedition expedition, string profileId, int choiceIndex)
        {
            if (expedition == null || expedition.PendingEventId == null)
            {
                return "Nothing is happening here.";
            }

            var scene = Book.Find(expedition.PendingEventId);
            if (scene == null)
            {
                return "That scene is not in the book any more.";
            }

            var profile = _profiles.Load(profileId);
            Profile updated;
            string line;
            string advanced;
            try
            {
                (updated, line, advanced) = EventRules.Apply(scene, choiceIndex, profile, _catalog,
                    Context(profile, expedition));
            }
            catch (ValidationException error)
            {
                return error.Message;
            }

            // The scene has recorded itself on the character (EventRules.SeenFlag), and that record is what an
            // event quest is judged by now. The old counter is still written for non-event objectives, because a
            // quest that advanced on a trip keeps advancing the way it always did.
            if (advanced != null)
            {
                var definition = QuestBindings.LoadQuests().FirstOrDefault(quest => quest.QuestId == advanced);
                if (definition != null && definition.Objective.Kind != QuestObjectiveKinds.Event)
                {
                    var repository = new QuestRepository(_profiles.DataFolder);
                    repository.Save(profileId, QuestRules.Advance(repository.Load(profileId), definition, 1));
                }
            }

            var lines = expedition.Log.ToList();
            if (!string.IsNullOrEmpty(line))
            {
                lines.Add(line);
            }

            Commit(expedition.With(keepPendingEvent: false, log: lines),
                new Dictionary<string, Profile> { [profileId] = updated }, null);
            return null;
        }

        /// <summary>The values a scene condition can read: the sheet, the purse, what is carried, what happened
        /// before. Carried means the rig and backpack, not the stash at home.</summary>
        private static ExpressionContext Context(Profile profile, Expedition expedition)
        {
            var carried = new HashSet<string>(profile.Rig.Stash.Concat(profile.Backpack.Stash)
                .Select(item => item.ItemId));
            return new ExpressionContext(profile.Abilities, profile.Money, expedition.Party.Count,
                id => carried.Contains(id),
                // seen("x") asks whether that scene has been played, and the record of that lives on the
                // character. Passing a constant false made every such check take the failure branch silently.
                id => profile.Flags.Contains(EventRules.SeenFlag(id)),
                expedition.RngState.Restore(),
                flag => profile.Flags.Contains(flag));
        }

        /// <summary>The scene book, read once per session.</summary>
        /// <summary>
        /// Always the current book. <see cref="EventStore"/> caches on the files' stamps, so this is cheap - and a
        /// scene the GM saved a minute ago is what the next node rolls against, not whatever was loaded at launch.
        /// </summary>
        private EventBook Book => EventStore.Load(_catalog);

        /// <summary>
        /// Drops one of a character's finds on purpose, so a full bag can be made room in. The item goes
        /// to the same list the carry limit uses, so the log remembers it was left behind.
        /// </summary>
        public Expedition DiscardLoot(Expedition expedition, string profileId, string itemId)
        {
            if (expedition == null || profileId == null || itemId == null
                || !expedition.Loot.TryGetValue(profileId, out var carried))
            {
                return expedition;
            }

            var index = carried.LastIndexOf(itemId);
            if (index < 0)
            {
                return expedition;
            }

            var loot = expedition.Loot.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
            loot[profileId].RemoveAt(index);

            var discarded = expedition.Discarded.ToList();
            discarded.Add(itemId);

            var log = expedition.Log.ToList();
            log.Add("Dropped " + (_catalog.TryGetValue(itemId, out var item) ? item.Name : itemId) + ".");

            var updated = expedition.With(loot: loot, discarded: discarded, log: log);
            _expeditions.Save(updated);
            return updated;
        }

        /// <summary>
        /// Destroys one find off the floor - the bin, not the drop. It does not go into
        /// <see cref="Expedition.Discarded"/>, because that list is what the "left behind, no room"
        /// line and the carry-limit note read, and an item thrown away on purpose was not left
        /// behind. The trip log still says what happened.
        /// </summary>
        public Expedition DestroyLoot(Expedition expedition, string profileId, string itemId)
        {
            if (expedition == null || profileId == null || itemId == null)
            {
                return expedition;
            }

            var loot = TrashRules.WithoutFind(expedition.Loot, profileId, itemId);
            if (loot == null)
            {
                return expedition;
            }

            var log = expedition.Log.ToList();
            log.Add("Threw away " + (_catalog.TryGetValue(itemId, out var item) ? item.Name : itemId) + ".");

            var updated = expedition.With(loot: loot, log: log);
            _expeditions.Save(updated);
            return updated;
        }

        /// <summary>
        /// The bin for what a character is carrying mid-raid: the rig and the backpack are what fills up
        /// out there, so they need a way to empty a cell without walking to an exit. Like the floor's bin
        /// this destroys the item - nothing goes into <see cref="Expedition.Discarded"/>, which is the list
        /// of things walked away from - and the trip log gets one line. Returns null, or why it was refused.
        /// </summary>
        public string DestroyCarried(Expedition expedition, string profileId, string instanceId)
        {
            if (expedition == null || profileId == null || instanceId == null)
            {
                return "Nothing is being carried.";
            }

            if (!expedition.Party.Contains(profileId))
            {
                return "That character is not on this trip.";
            }

            var profile = _profiles.Load(profileId);
            var rig = TrashRules.Without(profile.Rig, instanceId);
            var backpack = TrashRules.Without(profile.Backpack, instanceId);
            if (ReferenceEquals(rig, profile.Rig) && ReferenceEquals(backpack, profile.Backpack))
            {
                return "That item is not carried.";
            }

            var itemId = profile.Rig.Stash.Concat(profile.Backpack.Stash)
                .FirstOrDefault(item => item.InstanceId == instanceId)?.ItemId;
            var log = expedition.Log.ToList();
            log.Add("Threw away " + (itemId != null && _catalog.TryGetValue(itemId, out var definition)
                ? definition.Name
                : itemId ?? "an item") + ".");

            // One commit, so the log and the emptied bag cannot disagree about what happened.
            Commit(expedition.With(log: log),
                new Dictionary<string, Profile> { [profileId] = profile.With(rig: rig, backpack: backpack) }, null);
            return null;
        }

        /// <summary>
        /// Carries the wear from a round back onto the profile: the piece covering a part that took the round is
        /// the piece that loses life. A vest covers thorax and stomach, so it takes the worse of the two.
        /// </summary>
        private static Profile ApplyArmourWear(Profile profile, IReadOnlyDictionary<string, int> wear)
        {
            if (wear == null || wear.Count == 0)
            {
                return profile;
            }

            var loadout = profile.Loadout;
            foreach (var piece in profile.Loadout.Items)
            {
                var parts = piece.Slot == LoadoutSlots.Helmet
                    ? new[] { "head" }
                    : piece.Slot == LoadoutSlots.Armor
                        ? new[] { "thorax", "stomach" }
                        : new string[0];
                var covered = parts.Where(wear.ContainsKey).Select(part => wear[part]).ToList();
                if (covered.Count == 0)
                {
                    continue;
                }

                var lowest = covered.Min();
                if (lowest != piece.Durability)
                {
                    loadout = loadout.With(piece.WithDurability(lowest));
                }
            }

            return profile.With(loadout: loadout);
        }

        /// <summary>
        /// fighting; if it ends the fight, the action that started it is finished in the same commit - the node
        /// is searched (or deliberately not, after a clean escape) or the walk to the next node happens.
        ///
        /// <paramref name="action"/> is "attack", "cover" or "flee"; <paramref name="aim"/> only matters for an
        /// attack. Returns why it was refused, or null, plus the lines to show the player.
        /// </summary>
        public (string Error, List<string> Log, string Outcome) Fight(Expedition expedition, string profileId,
            string action, Aim aim = Aim.Body, string instanceId = null)
        {
            if (expedition == null || expedition.Encounter == null)
            {
                return ("There is nobody to fight.", new List<string>(), null);
            }

            if (!expedition.Party.Contains(profileId))
            {
                return ("That character is not on this trip.", new List<string>(), null);
            }

            var profile = _profiles.Load(profileId);
            var fighter = ExpeditionRules.BuildFighter(profile, _gear);
            var zone = _maps[expedition.MapId].Zone(expedition.ZoneId, expedition.Night);
            var random = expedition.RngState.Restore();
            var applied = profile; // healing changes the profile the rest of the round is worked out from
            var firstLines = new List<string>();

            (Encounter Encounter, Fighter Fighter, List<string> Log, string Outcome) round;
            if (action == "cover" || action == "crawl")
            {
                // Crawling replaces the old "take cover": head down AND closer, which is what makes the range
                // band matter - a shotgun at three metres is not a shotgun at forty.
                round = EncounterRules.Crawl(expedition.Encounter, fighter, random, _combat);
            }
            else if (action == "flee")
            {
                var agility = profile.Abilities.TryGetValue("agility", out var score)
                    ? score
                    : CharacterSheet.AbilityBase;
                round = EncounterRules.Flee(expedition.Encounter, fighter, random, _combat, agility);
            }
            else if (action == "heal")
            {
                // A bandage costs the round: the enemy gets its burst while the character is busy with it.
                if (instanceId == null)
                {
                    return ("No medkit was picked.", new List<string>(), null);
                }

                var carried = profile.Rig.Stash.Concat(profile.Backpack.Stash)
                    .FirstOrDefault(item => item.InstanceId == instanceId);
                if (carried == null)
                {
                    return ("That item is not carried.", new List<string>(), null);
                }

                var name = _catalog.TryGetValue(carried.ItemId, out var definition) ? definition.Name : carried.ItemId;
                if (!_gear.Meds.TryGetValue(carried.ItemId, out var med))
                {
                    return ($"{name} is not something you can use.", new List<string>(), null);
                }

                if (!HealingRules.WouldHelp(profile, med))
                {
                    return ($"{name} would not change anything right now.", new List<string>(), null);
                }

                Profile treated;
                int healed;
                IReadOnlyList<string> clearedConditions;
                try
                {
                    (treated, healed, clearedConditions) = HealingRules.UseCarried(profile, instanceId, med);
                }
                catch (ValidationException error)
                {
                    return (error.Message, new List<string>(), null);
                }

                applied = treated;
                firstLines.Add($"{name}: {healed} HP, "
                               + (clearedConditions.Count > 0 ? string.Join(", ", clearedConditions) : "nothing cleared") + ".");
                round = EncounterRules.EnemyTurn(expedition.Encounter, ExpeditionRules.BuildFighter(applied, _gear),
                    random, _combat);
            }
            else if (action == "attack")
            {
                round = EncounterRules.Attack(expedition.Encounter, fighter, aim, random, _combat);
            }
            else
            {
                return ($"Unknown action '{action}'.", new List<string>(), null);
            }

            var state = random.GetState();
            var log = expedition.Log.ToList();
            log.AddRange(firstLines);
            log.AddRange(round.Log);

            // The wounds go into the profile before anything else is decided: the resumed action reads the
            // party straight back out of the save files.
            var status = round.Fighter.Standing
                ? applied.Status
                : (ExpeditionRules.KilledBy(round.Fighter) ? CharacterSheet.Dead : CharacterSheet.Downed);
            var conditions = round.Fighter.Conditions.ToDictionary(
                pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.ToList());
            var wounded = applied.With(status: status, bodyParts: round.Fighter.BodyParts, conditions: conditions);
            wounded = ApplyArmourWear(wounded, round.Fighter.ArmorDurability);

            var advanced = expedition.With(encounter: round.Encounter, log: log,
                rngState: new RngState(state.Version, state.Words, state.Index, state.GaussNext));

            if (round.Outcome == EncounterOutcomeKind.Ongoing)
            {
                Commit(advanced, new Dictionary<string, Profile> { [profileId] = wounded }, null);
                return (null, round.Log, round.Outcome);
            }

            // The fight is over: drop it and finish what it interrupted.
            var source = advanced.Encounter.Source;
            var nodeId = advanced.Encounter.NodeId;
            var choiceIndex = advanced.Encounter.ChoiceIndex;
            var cleared = advanced.With(keepEncounter: false, status: ExpeditionStatus.Active);

            var party = LoadParty(cleared);
            party[profileId] = wounded;
            Expedition settled;
            Dictionary<string, Fighter> after;
            if (source == "patrol")
            {
                (settled, after) = ExpeditionRules.Move(cleared, nodeId, _maps[cleared.MapId], _combat, _raids,
                    Fighters(party), round.Outcome);
            }
            else
            {
                (settled, after) = ExpeditionRules.Choose(cleared, choiceIndex, _maps[cleared.MapId], _catalog,
                    new LootTables(_catalog, _containers), _raids, _gear, _combat, Fighters(party), Limits(party),
                    round.Outcome);
            }

            var changed = ExpeditionRules.SyncProfiles(party, after);
            changed[profileId] = wounded;
            Commit(settled, changed, null);
            return (null, round.Log, round.Outcome);
        }

        /// <summary>
        /// Uses a medkit the character is carrying, mid-raid. The stash is back home, so the kit has to be
        /// one of the instances in the rig or the backpack - and a character bleeding out at a cache is not
        /// going to walk home for it. The treated parts and the freed cell are written with the log line in
        /// one commit. Returns why it was refused, or null, and the line to show the player.
        /// </summary>
        public (string Error, string Message) UseCarriedMed(Expedition expedition, string profileId, string instanceId)
        {
            if (expedition == null || profileId == null || instanceId == null)
            {
                return ("Nothing is being used.", null);
            }

            if (!expedition.Party.Contains(profileId))
            {
                return ("That character is not on this trip.", null);
            }

            var profile = _profiles.Load(profileId);
            var carried = profile.Rig.Stash.Concat(profile.Backpack.Stash)
                .FirstOrDefault(item => item.InstanceId == instanceId);
            if (carried == null)
            {
                return ("That item is not carried.", null);
            }

            var name = _catalog.TryGetValue(carried.ItemId, out var definition) ? definition.Name : carried.ItemId;
            if (!_gear.Meds.TryGetValue(carried.ItemId, out var med))
            {
                return ($"{name} is not something you can use.", null);
            }

            if (!HealingRules.WouldHelp(profile, med))
            {
                return ($"{name} would not change anything: it heals "
                    + (med.Heal > 0 ? med.Heal + " HP" : "no HP") + " and clears nothing you have.", null);
            }

            Profile treated;
            int healed;
            IReadOnlyList<string> cleared;
            try
            {
                (treated, healed, cleared) = HealingRules.UseCarried(profile, instanceId, med);
            }
            catch (ValidationException error)
            {
                return (error.Message, null);
            }

            var log = expedition.Log.ToList();
            log.Add("Used " + name + ".");
            Commit(expedition.With(log: log), new Dictionary<string, Profile> { [profileId] = treated }, null);

            var parts = healed > 0 ? healed + " HP healed" : "no HP healed";
            var conditions = cleared.Count > 0 ? string.Join(", ", cleared) : "nothing cleared";
            return (null, name + ": " + parts + ", " + conditions + ".");
        }

        public Expedition Choose(Expedition expedition, int choiceIndex)
        {
            var current = LoadParty(expedition);
            var resolved = ExpeditionRules.Choose(expedition, choiceIndex, _maps[expedition.MapId], _catalog,
                new LootTables(_catalog, _containers), _raids, _gear, _combat, Fighters(current), Limits(current));
            var changed = ExpeditionRules.SyncProfiles(current, resolved.Fighters);
            var done = ExpeditionRules.NoteDeaths(resolved.Expedition, current, changed);

            // Finds stay on the floor until the player drags them into the rig or backpack.
            done = MaybeRaiseEvent(done);
            Commit(done, changed, null);
            return done;
        }

        public Expedition LeaveZone(Expedition expedition, string action)
        {
            var current = LoadParty(expedition);
            var resolved = ExpeditionRules.LeaveZone(expedition, action, _maps[expedition.MapId], _raids, _gear, _combat,
                Fighters(current));
            var changed = ExpeditionRules.SyncProfiles(current, resolved.Fighters);
            var recorded = ExpeditionRules.NoteDeaths(resolved.Expedition, current, changed);
            Commit(recorded, changed, null);
            return recorded.Status == ExpeditionStatus.AwaitingChoice
                ? ChooseExtract(recorded)
                : recorded;
        }

        public Expedition ChooseContinue(Expedition expedition, double now)
        {
            var advanced = ExpeditionRules.Continue(expedition, now);
            _expeditions.Save(advanced);
            return advanced;
        }

        public Expedition ChooseExtract(Expedition expedition)
        {
            var done = ExpeditionRules.Extract(expedition);
            var current = LoadParty(expedition);
            var deliverable = new Dictionary<string, List<string>>();
            foreach (var pair in done.Loot)
            {
                // Whoever is not on their feet brings nothing out: a downed character is carried, and a
                // dead one is left where they fell.
                if (ExpeditionRules.OnTheirFeet(current[pair.Key]))
                {
                    deliverable[pair.Key] = pair.Value;
                }
            }

            var delivery = Deliver(deliverable, current);
            var changed = new Dictionary<string, Profile>();
            foreach (var profileId in expedition.Party)
            {
                if (!ExpeditionRules.OnTheirFeet(current[profileId]))
                {
                    continue;
                }

                var profile = delivery.Profiles.ContainsKey(profileId) ? delivery.Profiles[profileId] : current[profileId];
                changed[profileId] = profile.With(status: CharacterSheet.Active);
            }

            var kept = new Dictionary<string, List<string>>();
            foreach (var pair in done.Loot)
            {
                if (!deliverable.ContainsKey(pair.Key))
                {
                    kept[pair.Key] = pair.Value.ToList();
                }
            }

            foreach (var pair in delivery.Leftover)
            {
                kept[pair.Key] = pair.Value;
            }

            done = done.With(loot: kept);
            Commit(done, changed, null);
            return done;
        }

        private Dictionary<string, Profile> LoadParty(Expedition expedition) =>
            expedition.Party.ToDictionary(id => id, id => _profiles.Load(id));

        private Dictionary<string, Fighter> Fighters(Dictionary<string, Profile> party) =>
            party.ToDictionary(pair => pair.Key, pair => ExpeditionRules.BuildFighter(pair.Value, _gear));

        private Dictionary<string, int> Limits(Dictionary<string, Profile> party) =>
            party.ToDictionary(pair => pair.Key, pair => ExpeditionRules.CarryLimit(pair.Value, _gear));

        /// <summary>
        /// Puts finds into each character's own stash. This build has no separate shared hideout stash
        /// (the personal room is the character stash), so anything that does not fit stays on the expedition.
        /// </summary>
        private (Dictionary<string, Profile> Profiles, Dictionary<string, List<string>> Leftover) Deliver(
            IReadOnlyDictionary<string, List<string>> loot, IReadOnlyDictionary<string, Profile> profiles)
        {
            var changed = new Dictionary<string, Profile>();
            var leftover = new Dictionary<string, List<string>>();
            foreach (var pair in loot)
            {
                var profile = profiles[pair.Key];
                var stash = profile.Stash;
                var rig = profile.Rig;
                var backpack = profile.Backpack;
                var kept = new List<string>();
                foreach (var itemId in pair.Value)
                {
                    // Rig first, then backpack, then the stash: the two grids are what the carry limit
                    // already measures, and they are what the player sees on the way out.
                    var instance = ItemInstance.Create(Guid.NewGuid().ToString("N"), itemId, 0, 0);
                    var spot = PlacementRules.FirstFit(rig, _catalog, itemId);
                    if (spot != null)
                    {
                        rig = rig.With(rig.Stash.Concat(new[]
                        {
                            ItemInstance.Create(instance.InstanceId, itemId, spot.Value.X, spot.Value.Y, spot.Value.Rotation),
                        }));
                        continue;
                    }

                    spot = PlacementRules.FirstFit(backpack, _catalog, itemId);
                    if (spot != null)
                    {
                        backpack = backpack.With(backpack.Stash.Concat(new[]
                        {
                            ItemInstance.Create(instance.InstanceId, itemId, spot.Value.X, spot.Value.Y, spot.Value.Rotation),
                        }));
                        continue;
                    }

                    spot = PlacementRules.FirstFit(stash, _catalog, itemId);
                    if (spot == null)
                    {
                        kept.Add(itemId);
                        continue;
                    }

                    stash = stash.With(stash.Stash.Concat(new[]
                    {
                        ItemInstance.Create(instance.InstanceId, itemId, spot.Value.X, spot.Value.Y, spot.Value.Rotation),
                    }));
                }

                if (kept.Count > 0)
                {
                    leftover[pair.Key] = kept;
                }

                if (rig != profile.Rig || backpack != profile.Backpack || stash != profile.Stash)
                {
                    changed[pair.Key] = profile.With(stash: stash, rig: rig, backpack: backpack);
                }
            }

            return (changed, leftover);
        }

        /// <summary>
        /// Takes one find off the floor and puts it in a chosen cell of the rig or backpack. Refused with a
        /// reason when it does not fit, and the find stays on the floor.
        /// </summary>
        public string StowLoot(Expedition expedition, string profileId, string instanceId, bool toBackpack,
            int x, int y, int rotation)
        {
            if (expedition == null || !expedition.Loot.TryGetValue(profileId, out var floor))
            {
                return "Nothing is on the floor.";
            }

            var index = floor.ToList().FindIndex(id => id == instanceId);
            if (index < 0)
            {
                return "That is not on the floor.";
            }

            var profile = _profiles.Load(profileId);
            var target = toBackpack ? profile.Backpack : profile.Rig;
            var error = PlacementRules.PlacementError(target, _catalog, itemId: instanceId, x: x, y: y,
                rotation: rotation, ignoreInstanceId: null);
            if (error != null)
            {
                return error;
            }

            var placed = target.With(target.Stash.Concat(new[]
            {
                ItemInstance.Create(Guid.NewGuid().ToString("N"), instanceId, x, y, rotation),
            }));

            var updatedProfile = toBackpack
                ? profile.With(backpack: placed)
                : profile.With(rig: placed);
            _profiles.Save(updatedProfile);

            var loot = expedition.Loot.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
            loot[profileId].RemoveAt(index);
            if (loot[profileId].Count == 0)
            {
                loot.Remove(profileId);
            }

            var updated = expedition.With(loot: loot);
            _expeditions.Save(updated);
            return null;
        }

        /// <summary>
        /// Moves a carried item to a cell in either grid, or between them. Returns why it was refused, or
        /// null when it moved. Placement is the same rule the stash uses.
        /// </summary>
        public string MoveCarried(string profileId, string instanceId, bool toBackpack, int x, int y, int rotation)
        {
            var profile = _profiles.Load(profileId);
            var inRig = profile.Rig.Stash.Any(item => item.InstanceId == instanceId);
            var inBackpack = profile.Backpack.Stash.Any(item => item.InstanceId == instanceId);
            if (!inRig && !inBackpack)
            {
                return "That item is not carried.";
            }

            var rig = profile.Rig;
            var backpack = profile.Backpack;
            try
            {
                if (inRig == toBackpack)
                {
                    // same grid: a move inside it
                    if (inRig)
                    {
                        rig = PlacementRules.Move(rig, _catalog, instanceId, x, y, rotation);
                    }
                    else
                    {
                        backpack = PlacementRules.Move(backpack, _catalog, instanceId, x, y, rotation);
                    }
                }
                else
                {
                    var moved = PlacementRules.Transfer(inRig ? rig : backpack, inRig ? backpack : rig,
                        _catalog, instanceId, x, y, rotation);
                    if (inRig)
                    {
                        rig = moved.From;
                        backpack = moved.To;
                    }
                    else
                    {
                        backpack = moved.From;
                        rig = moved.To;
                    }
                }
            }
            catch (ValidationException error)
            {
                return error.Message;
            }

            _profiles.Save(profile.With(rig: rig, backpack: backpack));
            return null;
        }

        /// <summary>
        /// Throws one carried item away mid-raid, freeing its cells. It goes to the same discarded list
        /// the log reads, so the record matches what happened.
        /// </summary>
        public Expedition DropCarried(Expedition expedition, string profileId, string instanceId)
        {
            if (expedition == null || profileId == null || instanceId == null
                || !expedition.Party.Contains(profileId))
            {
                return expedition;
            }

            var profile = _profiles.Load(profileId);
            var inRig = profile.Rig.Stash.FirstOrDefault(item => item.InstanceId == instanceId);
            var inBackpack = profile.Backpack.Stash.FirstOrDefault(item => item.InstanceId == instanceId);
            var dropped = inRig ?? inBackpack;
            if (dropped == null)
            {
                return expedition;
            }

            var rig = inRig == null
                ? profile.Rig
                : profile.Rig.With(profile.Rig.Stash.Where(item => item.InstanceId != instanceId));
            var backpack = inBackpack == null
                ? profile.Backpack
                : profile.Backpack.With(profile.Backpack.Stash.Where(item => item.InstanceId != instanceId));

            _profiles.Save(profile.With(rig: rig, backpack: backpack));

            var discarded = expedition.Discarded.ToList();
            discarded.Add(dropped.ItemId);

            var log = expedition.Log.ToList();
            log.Add("Dropped " + (_catalog.TryGetValue(dropped.ItemId, out var item)
                ? item.Name
                : dropped.ItemId) + ".");

            var updated = expedition.With(discarded: discarded, log: log);
            _expeditions.Save(updated);
            return updated;
        }

        /// <summary>
        /// Puts a character's finds straight into their rig and backpack, first fit, and returns what
        /// would not go in. The grids are the carrying space, so they decide - not a separate budget.
        /// </summary>
        private (Profile Profile, IReadOnlyList<string> Left) PlaceLoot(Profile profile, IEnumerable<string> itemIds)
        {
            var rig = profile.Rig;
            var backpack = profile.Backpack;
            var left = new List<string>();
            foreach (var itemId in itemIds ?? Enumerable.Empty<string>())
            {
                var id = Guid.NewGuid().ToString("N");
                var spot = PlacementRules.FirstFit(rig, _catalog, itemId);
                if (spot != null)
                {
                    rig = rig.With(rig.Stash.Concat(new[]
                    {
                        ItemInstance.Create(id, itemId, spot.Value.X, spot.Value.Y, spot.Value.Rotation),
                    }));
                    continue;
                }

                spot = PlacementRules.FirstFit(backpack, _catalog, itemId);
                if (spot != null)
                {
                    backpack = backpack.With(backpack.Stash.Concat(new[]
                    {
                        ItemInstance.Create(id, itemId, spot.Value.X, spot.Value.Y, spot.Value.Rotation),
                    }));
                    continue;
                }

                left.Add(itemId);
            }

            return (profile.With(rig: rig, backpack: backpack), left);
        }

        private void Commit(Expedition expedition, Dictionary<string, Profile> profiles, Account account)
        {
            var snapshots = new List<(string Path, byte[] Bytes)>();
            snapshots.Add(( _expeditions.PathFor(expedition.ExpeditionId), ReadIfExists(_expeditions.PathFor(expedition.ExpeditionId)) ));
            foreach (var profileId in profiles.Keys)
            {
                var path = _profiles.PathFor(profileId);
                snapshots.Add((path, ReadIfExists(path)));
            }

            if (account != null)
            {
                var path = Path.Combine(_accounts.DataFolder, "account.json");
                snapshots.Add((path, ReadIfExists(path)));
            }

            try
            {
                _expeditions.Save(expedition);
                foreach (var profile in profiles.Values)
                {
                    _profiles.Save(profile);
                }

                if (account != null)
                {
                    _accounts.Save(account);
                }
            }
            catch (Exception error)
            {
                for (var i = snapshots.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        if (snapshots[i].Bytes == null)
                        {
                            if (File.Exists(snapshots[i].Path))
                            {
                                File.Delete(snapshots[i].Path);
                            }
                        }
                        else
                        {
                            AtomicFile.Write(snapshots[i].Path, snapshots[i].Bytes);
                        }
                    }
                    catch (IOException)
                    {
                        // The error below still says the save failed.
                    }
                }

                throw new StorageException($"Could not save the expedition: {error.Message}\nNothing was changed.", error);
            }
        }

        private static byte[] ReadIfExists(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;
    }
}
