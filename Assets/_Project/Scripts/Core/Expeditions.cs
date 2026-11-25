using System;
using System.Collections.Generic;
using System.Linq;

namespace Safehouse.Core
{
    /// <summary>
    /// Both expedition kinds share one record. <c>simulation</c> farms on a real-time clock.
    /// <c>direct</c> is the node map and only moves when the player chooses. Skills and classes are not read.
    /// </summary>
    public static class ExpeditionModes
    {
        public const string Simulation = "simulation";
        public const string Direct = "direct";
        public static readonly IReadOnlyList<string> All = new[] { Simulation, Direct };
    }

    public static class ExpeditionStatus
    {
        public const string Active = "active";
        public const string AwaitingChoice = "awaiting_choice";
        public const string Completed = "completed";

        /// <summary>A fight the player has to answer round by round. The trip is paused until it is over:
        /// nothing else on the screen can be pressed while <see cref="Expedition.Encounter"/> is set.</summary>
        public const string InEncounter = "encounter";

        public static readonly IReadOnlyList<string> All = new[]
        {
            Active, AwaitingChoice, Completed, InEncounter,
        };
    }

    public sealed class RngState
    {
        public RngState(int version, uint[] words, int index, double? gaussNext)
        {
            Version = version;
            Words = words;
            Index = index;
            GaussNext = gaussNext;
        }

        public int Version { get; }
        public uint[] Words { get; }
        public int Index { get; }
        public double? GaussNext { get; }

        public PythonRandom Restore()
        {
            var random = PythonRandom.Seed(1);
            random.SetState(Version, Words, Index, GaussNext);
            return random;
        }
    }

    public sealed class Expedition
    {
        public const int SchemaVersion = 5;
        public const int MaxParty = 3;
        public const int MaxLogEntries = 200;
        public const int MaxLootPerCharacter = 2000;

        public Expedition(string expeditionId, string mapId, bool night, IReadOnlyList<string> party,
            long startedAt, string zoneId, long zoneStartedAt, string status, string outcome,
            IReadOnlyDictionary<string, IReadOnlyList<string>> loot, IReadOnlyList<string> discarded,
            IReadOnlyList<string> log, RngState rngState, string mode, Route route, string locationId = null,
            int nodesVisited = 0, Encounter encounter = null, string pendingEvent = null, string eventNode = null)
        {
            ExpeditionId = Validate.Identifier(expeditionId, "expedition_id", instance: true);
            MapId = Validate.Identifier(mapId, "map_id");
            Night = night;
            Party = party.ToList();
            if (Party.Count < 1 || Party.Count > MaxParty || Party.Distinct().Count() != Party.Count)
            {
                throw new ValidationException($"An expedition party must have 1 to {MaxParty} distinct members.");
            }

            StartedAt = startedAt;
            ZoneId = zoneId;
            ZoneStartedAt = zoneStartedAt;
            Status = status;
            Outcome = outcome;
            if ((outcome != null) != (status == ExpeditionStatus.Completed))
            {
                throw new ValidationException("Expedition outcome must be set exactly when status is completed.");
            }

            Loot = new Dictionary<string, List<string>>();
            foreach (var pair in loot ?? new Dictionary<string, IReadOnlyList<string>>())
            {
                if (!Party.Contains(pair.Key))
                {
                    throw new ValidationException($"Loot lists {pair.Key}, who is not in this expedition's party.");
                }

                Loot[pair.Key] = pair.Value.ToList();
            }

            Discarded = (discarded ?? new string[0]).ToList();
            Log = (log ?? new string[0]).ToList();
            RngState = rngState;
            if (mode != ExpeditionModes.Simulation && mode != ExpeditionModes.Direct)
            {
                throw new ValidationException($"mode must be one of: {string.Join(", ", ExpeditionModes.All)}.");
            }

            Mode = mode;
            Route = route;
            if (mode == ExpeditionModes.Direct && route == null)
            {
                throw new ValidationException("A direct-play expedition must have a route.");
            }

            if (mode == ExpeditionModes.Simulation && route != null)
            {
                throw new ValidationException("A simulated expedition cannot have a route.");
            }

            LocationId = locationId;
            NodesVisited = nodesVisited;
            Encounter = encounter;
            PendingEventId = string.IsNullOrWhiteSpace(pendingEvent) ? null : pendingEvent;
            EventNodeId = string.IsNullOrWhiteSpace(eventNode) ? null : eventNode;
            if (Encounter != null && status != ExpeditionStatus.InEncounter)
            {
                throw new ValidationException("An expedition can only have an encounter while its status is "
                                              + "'" + ExpeditionStatus.InEncounter + "'.");
            }
        }

        public string ExpeditionId { get; }
        public string MapId { get; }
        public bool Night { get; }
        public IReadOnlyList<string> Party { get; }
        public long StartedAt { get; }
        public string ZoneId { get; }
        public long ZoneStartedAt { get; }
        public string Status { get; }
        public string Outcome { get; }
        public Dictionary<string, List<string>> Loot { get; }
        public List<string> Discarded { get; }
        public List<string> Log { get; }
        public RngState RngState { get; }
        public string Mode { get; }
        public Route Route { get; }

        /// <summary>
        /// Which named place on the map the party is currently at, for maps with an authored
        /// <see cref="LocationGraph"/>. Null for simulated expeditions and for maps with no location graph,
        /// which still just march outer/center/deep by <see cref="ZoneId"/> alone.
        /// </summary>
        public string LocationId { get; }

        /// <summary>
        /// How many nodes the party has walked into so far this trip, across every location (never reset by a
        /// location change, only by a brand new expedition) - the start node of the first location counts as
        /// 1. 0 for a simulated expedition, which has no nodes at all. Checked against
        /// <see cref="ExpeditionRules.MaxNodesPerExpedition"/>: once reached, the party can still finish
        /// working through wherever they already are (walking the rest of a route is never blocked - every
        /// node has a path on to that location's exit, by construction), but cannot push on to a further zone
        /// or location. Extract is always available regardless.
        /// </summary>
        public int NodesVisited { get; }

        /// <summary>The fight the party is in the middle of, or null. Set exactly while the status is
        /// <see cref="ExpeditionStatus.InEncounter"/>; the trip file carries it so quitting mid-fight is safe.</summary>
        public Encounter Encounter { get; }

        /// <summary>A scene waiting to be played out here in the raid, or null. Set when walking into a node
        /// turns one up; the trip cannot be moved on until it is answered.</summary>
        public string PendingEventId { get; }

        /// <summary>
        /// The node a main-line scene is waiting at, or null. The map draws it green: the story does not ambush
        /// the party at every corner, it tells them which door to open and then waits there.
        /// </summary>
        public string EventNodeId { get; }

        public Expedition With(string zoneId = null, long? zoneStartedAt = null, string status = null,
            string outcome = null, bool keepOutcome = true, Route route = null, bool keepRoute = true,
            Dictionary<string, List<string>> loot = null, List<string> discarded = null, List<string> log = null,
            RngState rngState = null, string locationId = null, bool keepLocation = true, int? nodesVisited = null,
            Encounter encounter = null, bool keepEncounter = true, string pendingEvent = null,
            bool keepPendingEvent = true, string eventNode = null, bool keepEventNode = true)
        {
            IReadOnlyDictionary<string, IReadOnlyList<string>> lootValue = loot == null
                ? Loot.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.ToList())
                : loot.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.ToList());
            return new Expedition(ExpeditionId, MapId, Night, Party, StartedAt, zoneId ?? ZoneId,
                zoneStartedAt ?? ZoneStartedAt, status ?? Status,
                keepOutcome ? (outcome ?? Outcome) : outcome,
                lootValue, discarded ?? Discarded, log ?? Log, rngState ?? RngState, Mode,
                keepRoute ? (route ?? Route) : route,
                keepLocation ? (locationId ?? LocationId) : locationId,
                nodesVisited ?? NodesVisited,
                keepEncounter ? (encounter ?? Encounter) : encounter,
                keepPendingEvent ? (pendingEvent ?? PendingEventId) : pendingEvent,
                keepEventNode ? (eventNode ?? EventNodeId) : eventNode);
        }
    }

    public static class ExpeditionRules
    {
        /// <summary>
        /// The cells a character can haul out with nothing on their back - the same 2x3 an empty rig
        /// and backpack give at home. It used to be 260, which made the rig and backpack irrelevant: a
        /// full kit added 72 cells on top of a 260-cell base.
        /// </summary>
        public const int BaseCarryCells = 6;
        public const double NightMultiplier = 1.5;

        private static readonly Dictionary<string, int> ZoneMinutes = new Dictionary<string, int>
        {
            ["outer"] = 30, ["center"] = 45, ["deep"] = 60,
        };

        private static readonly Dictionary<string, int> SearchesPerZone = new Dictionary<string, int>
        {
            ["outer"] = 6, ["center"] = 8, ["deep"] = 10,
        };

        public static int ZoneSeconds(string zoneId, bool night)
        {
            var minutes = ZoneMinutes[zoneId] * (night ? NightMultiplier : 1);
            return (int)Math.Round(minutes * 60, MidpointRounding.ToEven);
        }

        public static long Timestamp(double now)
        {
            if (double.IsNaN(now) || double.IsInfinity(now) || now < 0 || now > 32503680000d)
            {
                throw new ValidationException("The current time must be a number of seconds since 1970.");
            }

            return (long)now;
        }

        /// <summary>At most this many nodes (across every location, the whole trip through) in a single
        /// direct-play expedition. A pacing choice, not a game rule - raise it freely. 40 is roughly a
        /// comfortable 3-4 average locations' worth (each location has 8-12 nodes, see raid_nodes.json's
        /// nodes_per_zone), without hard-coding a location count the way an earlier version of this did.</summary>
        public const int MaxNodesPerExpedition = 40;

        public static Expedition Create(string mapId, bool night, IReadOnlyList<string> party, double now,
            string mode, Route route, PythonRandom random, string zoneId = null, string locationId = null)
        {
            var started = Timestamp(now);
            var state = random.GetState();
            return new Expedition(Guid.NewGuid().ToString("N"), mapId, night, party, started, zoneId ?? MapZones.Ids[0],
                started, ExpeditionStatus.Active, null, new Dictionary<string, IReadOnlyList<string>>(), new string[0],
                new string[0], new RngState(state.Version, state.Words, state.Index, state.GaussNext), mode, route,
                locationId, mode == ExpeditionModes.Direct ? 1 : 0);
        }

        public static bool Due(Expedition expedition, double now) =>
            expedition.Mode == ExpeditionModes.Simulation
            && expedition.Status == ExpeditionStatus.Active
            && Timestamp(now) >= expedition.ZoneStartedAt + ZoneSeconds(expedition.ZoneId, expedition.Night);

        /// <summary>
        /// Whether there is somewhere further to go: not at <see cref="MaxNodesPerExpedition"/> yet (direct play
        /// only; a simulated trip has no nodes), and then, for a map with an authored <see cref="LocationGraph"/>
        /// (pass <paramref name="map"/>), whichever locations the current one connects to; otherwise simply
        /// "not at the last of outer/center/deep yet".
        /// </summary>
        public static bool CanGoDeeper(Expedition expedition, MapDefinition map = null)
        {
            if (expedition.Mode == ExpeditionModes.Direct && expedition.NodesVisited >= MaxNodesPerExpedition)
            {
                return false;
            }

            if (map?.Locations != null && expedition.LocationId != null)
            {
                return map.Locations.Get(expedition.LocationId).ConnectsTo.Count > 0;
            }

            return expedition.ZoneId != MapZones.Ids[MapZones.Ids.Count - 1];
        }

        public static Fighter BuildFighter(Profile profile, GearData gear)
        {
            var worn = profile.Loadout.Items.ToDictionary(item => item.Slot, item => item.ItemId);
            worn.TryGetValue(LoadoutSlots.Primary, out var weaponId);
            worn.TryGetValue(LoadoutSlots.Ammo, out var ammoId);
            gear.Weapons.TryGetValue(weaponId ?? "", out var weapon);
            AmmoStats ammo = null;
            if (weapon != null && ammoId != null)
            {
                gear.Ammo.TryGetValue(ammoId, out ammo);
            }

            var armorItems = profile.Loadout.Items
                .Where(item => item.Slot == LoadoutSlots.Armor || item.Slot == LoadoutSlots.Helmet)
                .ToList();
            var armorWear = new Dictionary<string, int>();
            foreach (var piece in armorItems)
            {
                foreach (var part in piece.Slot == LoadoutSlots.Helmet
                             ? new[] { "head" }
                             : new[] { "thorax", "stomach" })
                {
                    armorWear[part] = piece.Durability;
                }
            }

            worn.TryGetValue(LoadoutSlots.Meds, out var medId);
            gear.Meds.TryGetValue(medId ?? "", out var med);
            var conditions = new Dictionary<string, string[]>();
            foreach (var pair in profile.Conditions)
            {
                conditions[pair.Key] = pair.Value.ToArray();
            }

            var max = CharacterSheet.BodyPartMaxHealth.ToDictionary(pair => pair.Key, pair => pair.Value);
            return new Fighter(profile.ProfileId, profile.DisplayName, profile.BodyParts, max, conditions,
                weapon, ammo,
                gear.ArmorClasses(armorItems.Select(piece => (piece.ItemId, piece.Durability))),
                med == null ? 0 : med.Heal * med.Uses,
                med != null && med.StopsLightBleed, med != null && med.StopsHeavyBleed, med != null && med.TreatsFracture,
                armorWear);
        }

        /// <summary>
        /// How much a character can bring back: exactly the cells their rig and backpack give them, so the
        /// number in the raid matches the boxes on the GEAR screen instead of a separate budget.
        /// </summary>
        public static int CarryLimit(Profile profile, GearData gear) =>
            profile.Rig.StashWidth * profile.Rig.StashHeight
            + profile.Backpack.StashWidth * profile.Backpack.StashHeight;

        /// <summary>
        /// How many kilograms a character can hump: fifteen, plus three per point of muscle. The grids decide
        /// volume, this decides mass - which is what makes a big backpack a trade rather than a free upgrade.
        /// </summary>
        public const int BaseCarryWeight = 15;
        public const int CarryWeightPerMuscle = 3;

        public static int MaxWeight(Profile profile) =>
            BaseCarryWeight + CarryWeightPerMuscle
            * (profile.Abilities.TryGetValue("muscle", out var muscle) ? muscle : CharacterSheet.AbilityBase);

        /// <summary>
        /// What is actually on the character: everything in the rig and the backpack, plus everything worn -
        /// gear counts at 100 %, same as loot (the earlier half-weight rule for worn kit was dropped by design).
        /// The stash is at home and does not count.
        /// </summary>
        public static double CarriedWeight(Profile profile, IReadOnlyDictionary<string, ItemDefinition> catalog)
        {
            var total = 0.0;
            foreach (var grid in new[] { profile.Rig, profile.Backpack })
            {
                PlacementRules.StashTotals(grid, catalog, out _, out var weight, out _);
                total += weight;
            }

            foreach (var worn in profile.Loadout.Items)
            {
                if (catalog.TryGetValue(worn.ItemId, out var item))
                {
                    total += item.Weight;
                }
            }

            return total;
        }

        public static (Expedition Expedition, Dictionary<string, Fighter> Fighters) ResolveZone(
            Expedition expedition, MapDefinition map, IReadOnlyDictionary<string, ItemDefinition> catalog,
            LootTables tables, GearData gear, CombatNumbers numbers, IReadOnlyDictionary<string, Fighter> fighters,
            IReadOnlyDictionary<string, int> carryLimits)
        {
            var random = expedition.RngState.Restore();
            var zone = map.Zone(expedition.ZoneId, expedition.Night);
            var party = expedition.Party.Select(id => fighters[id].Copy()).ToList();
            var log = expedition.Log.ToList();
            foreach (var enemy in CombatRules.RollEncounters(zone, expedition.Night, gear, random, numbers))
            {
                if (!party.Any(fighter => fighter.Standing))
                {
                    break;
                }

                var resolved = CombatRules.ResolveEncounter(party, enemy, zone.EngagementRange, random, numbers);
                party = resolved.Party;
                log.Add(resolved.Result.Summary());
            }

            var updated = expedition.Party.Zip(party, (id, fighter) => (id, fighter))
                .ToDictionary(pair => pair.id, pair => pair.fighter);
            var ended = CombatRules.ApplyZoneEnd(updated, numbers);
            updated = ended.Fighters;
            log.AddRange(ended.Log);
            var standing = expedition.Party.Where(id => updated[id].Standing).ToList();
            var loot = CopyLoot(expedition.Loot);
            var discarded = expedition.Discarded.ToList();
            if (standing.Count > 0)
            {
                var before = discarded.Count;
                for (var index = 0; index < SearchesPerZone[zone.ZoneId]; index++)
                {
                    var found = LootRolls.SearchZone(zone, tables, random);
                    Stow(loot, discarded, standing[index % standing.Count], found, catalog, carryLimits, log);
                }
                if (discarded.Count > before)
                {
                    log.Add("Left behind, no room: " + string.Join(", ",
                        discarded.Skip(before).Select(id => catalog[id].Name)));
                }
            }
            else if (log.Count == 0 || !log[log.Count - 1].Contains("wiped"))
            {
                log.Add("The party was wiped before it could search this zone.");
            }

            var wiped = standing.Count == 0;
            var state = random.GetState();
            var resolvedExpedition = expedition.With(
                status: wiped ? ExpeditionStatus.Completed : ExpeditionStatus.AwaitingChoice,
                outcome: wiped ? "wiped" : null, keepOutcome: false,
                loot: loot, discarded: discarded, log: Trim(log),
                rngState: new RngState(state.Version, state.Words, state.Index, state.GaussNext));
            return (resolvedExpedition, updated);
        }

        public static (Expedition Expedition, Dictionary<string, Fighter> Fighters) Move(
            Expedition expedition, string nodeId, MapDefinition map, CombatNumbers numbers, RaidRules rules,
            IReadOnlyDictionary<string, Fighter> fighters, string resolvedEncounter = null)
        {
            var current = CurrentNode(expedition);
            if (!current.Neighbors.Contains(nodeId))
            {
                throw new ValidationException($"Node {nodeId} is not adjacent to the party's current node.");
            }

            var zone = map.Zone(expedition.ZoneId, expedition.Night);
            var random = expedition.RngState.Restore();
            var party = expedition.Party.Select(id => fighters[id].Copy()).ToList();
            var log = expedition.Log.ToList();

            // A patrol on the way is a fight the player answers: the move is suspended here and picked up
            // again by the session once the encounter has an outcome ("won", "fled" or "lost").
            if (resolvedEncounter == null)
            {
                var chance = NightScaled(rules.PatrolPercent[zone.ZoneId], rules.PatrolNightBonusPercent, expedition.Night);
                if (party.Any(fighter => fighter.Standing) && random.RandRange(100) < chance)
                {
                    var patrol = Enemy.Scav(numbers);
                    var encounter = Encounter.From(patrol, "patrol", nodeId, -1);
                    log.Add($"Contact: a {patrol.Name} on the way in.");
                    var pauseState = random.GetState();
                    var paused = expedition.With(status: ExpeditionStatus.InEncounter, encounter: encounter,
                        log: Trim(log),
                        rngState: new RngState(pauseState.Version, pauseState.Words, pauseState.Index, pauseState.GaussNext));
                    return (paused, Zip(expedition, party));
                }
            }

            var updated = Zip(expedition, party);
            var wiped = !updated.Values.Any(fighter => fighter.Standing);
            var state = random.GetState();
            var next = expedition.With(route: expedition.Route.With(currentNodeId: nodeId), log: Trim(log),
                rngState: new RngState(state.Version, state.Words, state.Index, state.GaussNext),
                status: wiped ? ExpeditionStatus.Completed : expedition.Status,
                outcome: wiped ? "wiped" : expedition.Outcome,
                nodesVisited: expedition.NodesVisited + 1,
                keepEncounter: false);
            return (next, updated);
        }

        public static (Expedition Expedition, Dictionary<string, Fighter> Fighters) Choose(
            Expedition expedition, int choiceIndex, MapDefinition map, IReadOnlyDictionary<string, ItemDefinition> catalog,
            LootTables tables, RaidRules rules, GearData gear, CombatNumbers numbers,
            IReadOnlyDictionary<string, Fighter> fighters, IReadOnlyDictionary<string, int> carryLimits,
            string resolvedEncounter = null)
        {
            var node = CurrentNode(expedition);
            if (node.Cleared)
            {
                throw new ValidationException("This node has already been dealt with.");
            }

            var kind = rules.Kinds[node.Kind];
            if (choiceIndex < 0 || choiceIndex >= kind.Choices.Count)
            {
                throw new ValidationException("Unknown choice for this node.");
            }

            var choice = kind.Choices[choiceIndex];
            if (choice.Action != null)
            {
                throw new ValidationException("This is an exit choice; call leave_zone instead.");
            }

            var zone = map.Zone(expedition.ZoneId, expedition.Night);
            var random = expedition.RngState.Restore();
            var party = expedition.Party.Select(id => fighters[id].Copy()).ToList();
            var log = expedition.Log.ToList();

            // Same shape as Move: the fight happens before the node is dealt with, and the session comes back
            // here with the outcome. Nothing is looted or cleared until then.
            if (resolvedEncounter == null)
            {
                var chance = NightScaled(choice.EnemyPercent, choice.NightEnemyBonusPercent, expedition.Night);
                if (party.Any(fighter => fighter.Standing) && random.RandRange(100) < chance)
                {
                    var enemy = DirectEnemies(zone, node, gear, numbers).FirstOrDefault();
                    if (enemy != null)
                    {
                        var encounter = Encounter.From(enemy, "choice", node.NodeId, choiceIndex);
                        log.Add($"Contact: a {enemy.Name}.");
                        var pauseState = random.GetState();
                        var paused = expedition.With(status: ExpeditionStatus.InEncounter, encounter: encounter,
                            log: Trim(log),
                            rngState: new RngState(pauseState.Version, pauseState.Words, pauseState.Index, pauseState.GaussNext));
                        return (paused, Zip(expedition, party));
                    }
                }
            }

            // A clean escape leaves the node as it was found: no search, no loot, but the walk on is free.
            var escaped = resolvedEncounter == EncounterOutcomeKind.Fled;
            if (escaped)
            {
                log.Add("You broke contact and left the rest of it where it was.");
            }

            var updated = Zip(expedition, party);
            var loot = CopyLoot(expedition.Loot);
            var discarded = expedition.Discarded.ToList();
            var standing = expedition.Party.Where(id => updated[id].Standing).ToList();
            var rolls = escaped ? 0 : choice.LootRolls;
            if (standing.Count > 0 && rolls > 0)
            {
                var before = discarded.Count;
                var foundItems = new List<string>();
                for (var index = 0; index < rolls; index++)
                {
                    var found = node.Kind == "container"
                        ? LootRolls.SearchContainer(node.ContainerType, tables, random, zone.ZoneId)
                        : LootRolls.SearchLoose(zone, random);
                    foundItems.AddRange(found);
                    Stow(loot, discarded, standing[index % standing.Count], found, catalog, carryLimits, log);
                }

                var kept = foundItems
                    .Where(itemId => !discarded.Skip(before).Contains(itemId))
                    .ToList();
                log.Add(kept.Count > 0
                    ? "Found: " + string.Join(", ", kept.Select(itemId => catalog[itemId].Name)) + "."
                    : "Found nothing you could carry.");
                if (discarded.Count > before)
                {
                    log.Add("Left behind, no room: " + string.Join(", ",
                        discarded.Skip(before).Select(itemId => catalog[itemId].Name)));
                }
            }
            else if (standing.Count == 0 && (log.Count == 0 || !log[log.Count - 1].Contains("wiped")))
            {
                log.Add("The party was wiped before it could deal with this node.");
            }

            var nodes = new Dictionary<string, RouteNode>(expedition.Route.Nodes);
            nodes[node.NodeId] = node.With(cleared: true);
            var wiped = standing.Count == 0;
            var state = random.GetState();
            var next = expedition.With(route: expedition.Route.With(nodes: nodes), loot: loot, discarded: discarded,
                log: Trim(log), rngState: new RngState(state.Version, state.Words, state.Index, state.GaussNext),
                status: wiped ? ExpeditionStatus.Completed : expedition.Status,
                outcome: wiped ? "wiped" : expedition.Outcome,
                keepEncounter: false);
            return (next, updated);
        }

        /// <summary>
        /// Leaving the zone's exit node: "extract" ends the trip. Anything else is where to go next - a
        /// neighboring location's id, for a map with a <see cref="LocationGraph"/> (must be one of the current
        /// location's <see cref="MapLocation.ConnectsTo"/>), or the literal "deeper" for a map with none, which
        /// still just steps to the next of outer/center/deep. Refused, changing nothing, if that destination is
        /// not actually reachable from here.
        /// </summary>
        public static (Expedition Expedition, Dictionary<string, Fighter> Fighters) LeaveZone(
            Expedition expedition, string action, MapDefinition map, RaidRules rules, GearData gear,
            CombatNumbers numbers, IReadOnlyDictionary<string, Fighter> fighters)
        {
            var node = CurrentNode(expedition);
            if (node.Kind != "exit")
            {
                throw new ValidationException("The party must be at the zone's exit node to leave it.");
            }

            string nextZoneId = null;
            string nextLocationId = null;
            if (action != "extract")
            {
                (nextZoneId, nextLocationId) = Destination(expedition, action, map);
            }

            var ended = CombatRules.ApplyZoneEnd(fighters.ToDictionary(pair => pair.Key, pair => pair.Value.Copy()), numbers);
            var log = expedition.Log.ToList();
            log.AddRange(ended.Log);
            if (!ended.Fighters.Values.Any(fighter => fighter.Standing))
            {
                return (expedition.With(status: ExpeditionStatus.Completed, outcome: "wiped", log: Trim(log)), ended.Fighters);
            }

            if (action == "extract")
            {
                return (expedition.With(status: ExpeditionStatus.AwaitingChoice, log: Trim(log)), ended.Fighters);
            }

            var random = expedition.RngState.Restore();
            var route = RaidMaps.Generate(map.Zone(nextZoneId, expedition.Night), expedition.Night, rules, gear, numbers, random);
            var state = random.GetState();
            var next = expedition.With(zoneId: nextZoneId, route: route, log: Trim(log),
                rngState: new RngState(state.Version, state.Words, state.Index, state.GaussNext),
                locationId: nextLocationId, keepLocation: nextLocationId != null,
                nodesVisited: expedition.NodesVisited + 1);
            return (next, ended.Fighters);
        }

        /// <summary>The zone and (when the map has a location graph) location that <paramref name="action"/> leads to.</summary>
        private static (string ZoneId, string LocationId) Destination(Expedition expedition, string action, MapDefinition map)
        {
            if (expedition.NodesVisited >= MaxNodesPerExpedition)
            {
                throw new ValidationException("The party has pushed as far as they safely can this trip. Extract instead.");
            }

            if (map.Locations != null && expedition.LocationId != null)
            {
                var current = map.Locations.Get(expedition.LocationId);
                if (!current.ConnectsTo.Contains(action))
                {
                    throw new ValidationException($"{action} is not reachable from {current.Name}.");
                }

                var destination = map.Locations.Get(action);
                return (destination.ZoneId, destination.LocationId);
            }

            if (action != "deeper")
            {
                throw new ValidationException("action must be 'deeper' or 'extract'.");
            }

            if (!CanGoDeeper(expedition))
            {
                throw new ValidationException("There is no deeper zone on this map. Extract instead.");
            }

            return (MapZones.Ids[MapZones.Ids.ToList().IndexOf(expedition.ZoneId) + 1], null);
        }

        public static Expedition Continue(Expedition expedition, double now)
        {
            if (expedition.Status != ExpeditionStatus.AwaitingChoice)
            {
                throw new ValidationException("This expedition is not waiting for a decision.");
            }

            if (!CanGoDeeper(expedition))
            {
                throw new ValidationException("There is no deeper zone on this map. Extract instead.");
            }

            var nextZone = MapZones.Ids[MapZones.Ids.ToList().IndexOf(expedition.ZoneId) + 1];
            return expedition.With(zoneId: nextZone, zoneStartedAt: Timestamp(now), status: ExpeditionStatus.Active);
        }

        public static Expedition Extract(Expedition expedition)
        {
            if (expedition.Status != ExpeditionStatus.AwaitingChoice)
            {
                throw new ValidationException("This expedition is not waiting for a decision.");
            }

            return expedition.With(status: ExpeditionStatus.Completed, outcome: "extracted");
        }

        /// <summary>
        /// Writes a fight's outcome into the characters: their parts, their conditions, and whether they are
        /// still on their feet. This is where being downed is decided, so this is also where death is: head
        /// or thorax at zero kills, and a character who dies that way loses whatever their rig and backpack
        /// were holding - the loot is on the ground they fell on.
        /// </summary>
        public static Dictionary<string, Profile> SyncProfiles(IReadOnlyDictionary<string, Profile> current,
            IReadOnlyDictionary<string, Fighter> updatedFighters)
        {
            var changed = new Dictionary<string, Profile>();
            foreach (var pair in updatedFighters)
            {
                var profile = current[pair.Key];
                var fighter = pair.Value;
                var status = fighter.Standing
                    ? profile.Status
                    : KilledBy(fighter) ? CharacterSheet.Dead : CharacterSheet.Downed;
                var died = status == CharacterSheet.Dead && profile.Status != CharacterSheet.Dead;
                if (!SameParts(fighter.BodyParts, profile.BodyParts) || !SameConditions(fighter.Conditions, profile.Conditions)
                    || status != profile.Status)
                {
                    var conditions = fighter.Conditions.ToDictionary(
                        entry => entry.Key, entry => (IReadOnlyList<string>)entry.Value.ToList());
                    changed[pair.Key] = profile.With(status: status, bodyParts: fighter.BodyParts, conditions: conditions,
                        rig: died ? profile.Rig.With(new ItemInstance[0]) : null,
                        backpack: died ? profile.Backpack.With(new ItemInstance[0]) : null);
                }
            }

            return changed;
        }

        /// <summary>
        /// Head or thorax at zero is death, not being down: no medkit treats either part, so there is
        /// nothing to walk it back. Everything else at zero only takes a character off their feet.
        /// </summary>
        public static bool KilledBy(Fighter fighter) =>
            fighter.BodyParts["head"] <= 0 || fighter.BodyParts["thorax"] <= 0;

        /// <summary>Still able to bring something out of a raid: neither carried out nor left behind.</summary>
        public static bool OnTheirFeet(Profile profile) =>
            profile.Status != CharacterSheet.Downed && profile.Status != CharacterSheet.Dead;

        /// <summary>
        /// The trip's own record of what the last fight cost: one line per character who was alive before
        /// it and is dead after. Death itself is decided in <see cref="SyncProfiles"/>; this only writes it
        /// down, and only once, so a log cannot claim the same death at every node.
        /// </summary>
        public static Expedition NoteDeaths(Expedition expedition, IReadOnlyDictionary<string, Profile> before,
            IReadOnlyDictionary<string, Profile> after)
        {
            var log = expedition.Log.ToList();
            foreach (var pair in after)
            {
                if (pair.Value.Status != CharacterSheet.Dead
                    || (before.TryGetValue(pair.Key, out var previous) && previous.Status == CharacterSheet.Dead))
                {
                    continue;
                }

                log.Add(pair.Value.DisplayName + " was killed in action. What they were carrying is lost.");
            }

            return log.Count == expedition.Log.Count ? expedition : expedition.With(log: Trim(log));
        }

        private static void Stow(Dictionary<string, List<string>> loot, List<string> discarded, string recipient,
            IReadOnlyList<string> found, IReadOnlyDictionary<string, ItemDefinition> catalog,
            IReadOnlyDictionary<string, int> carryLimits, List<string> log)
        {
            var limit = carryLimits.TryGetValue(recipient, out var value) ? value : 0;
            foreach (var itemId in found)
            {
                if (!loot.TryGetValue(recipient, out var carried))
                {
                    carried = new List<string>();
                    loot[recipient] = carried;
                }

                var cells = carried.Sum(id => catalog[id].Width * catalog[id].Height);
                var item = catalog[itemId];

                // The carry limit was written, shown in the raid panel, and then never applied: this branch used
                // to read `if (false)`, so a party could carry the whole map home. A limit of 0 means "no limit",
                // which is what an unset entry in the limits table means.
                if (limit > 0 && cells + item.Width * item.Height > limit)
                {
                    discarded.Add(itemId);
                }
                else
                {
                    carried.Add(itemId);
                }
            }
        }

        private static int NightScaled(int baseChance, int bonusPercent, bool night) =>
            night ? Math.Min(100, (int)Math.Round(baseChance * (100.0 + bonusPercent) / 100.0, MidpointRounding.ToEven)) : baseChance;

        private static RouteNode CurrentNode(Expedition expedition)
        {
            if (expedition.Route == null)
            {
                throw new ValidationException("This expedition is not a direct-play raid.");
            }

            return expedition.Route.Nodes[expedition.Route.CurrentNodeId];
        }

        private static List<Enemy> DirectEnemies(Zone zone, RouteNode node, GearData gear, CombatNumbers numbers)
        {
            if (node.Kind != "boss_lair" || node.BossMobId == null)
            {
                return new List<Enemy> { Enemy.Scav(numbers) };
            }

            var boss = zone.Bosses.FirstOrDefault(spawn => spawn.MobId == node.BossMobId);
            if (boss == null || !gear.Mobs.ContainsKey(boss.MobId))
            {
                return new List<Enemy> { Enemy.Scav(numbers) };
            }

            var enemies = new List<Enemy> { Enemy.FromMob(gear.Mobs[boss.MobId]) };
            foreach (var escort in boss.Escorts)
            {
                if (!gear.Mobs.ContainsKey(escort.MobId))
                {
                    continue;
                }

                for (var i = 0; i < escort.Count; i++)
                {
                    enemies.Add(Enemy.FromMob(gear.Mobs[escort.MobId]));
                }
            }

            return enemies;
        }

        private static Dictionary<string, Fighter> Zip(Expedition expedition, List<Fighter> party)
        {
            var updated = new Dictionary<string, Fighter>();
            for (var i = 0; i < expedition.Party.Count; i++)
            {
                updated[expedition.Party[i]] = party[i];
            }

            return updated;
        }

        private static Dictionary<string, List<string>> CopyLoot(Dictionary<string, List<string>> loot) =>
            loot.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());

        private static List<string> Trim(List<string> log) =>
            log.Count <= Expedition.MaxLogEntries ? log : log.Skip(log.Count - Expedition.MaxLogEntries).ToList();

        private static bool SameParts(IReadOnlyDictionary<string, int> fighter, IReadOnlyDictionary<string, int> profile)
        {
            foreach (var pair in fighter)
            {
                if (!profile.TryGetValue(pair.Key, out var value) || value != pair.Value)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool SameConditions(Dictionary<string, string[]> fighter,
            IReadOnlyDictionary<string, IReadOnlyList<string>> profile)
        {
            var fighterKeys = fighter.Keys.OrderBy(key => key, StringComparer.Ordinal).ToList();
            var profileKeys = profile.Keys.OrderBy(key => key, StringComparer.Ordinal).ToList();
            if (!fighterKeys.SequenceEqual(profileKeys))
            {
                return false;
            }

            foreach (var key in fighterKeys)
            {
                var left = fighter[key].OrderBy(name => name, StringComparer.Ordinal);
                var right = profile[key].OrderBy(name => name, StringComparer.Ordinal);
                if (!left.SequenceEqual(right))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
