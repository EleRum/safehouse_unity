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
            Commit(recorded, changed, null);
            return recorded;
        }

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

        public Expedition Choose(Expedition expedition, int choiceIndex)
        {
            var current = LoadParty(expedition);
            var resolved = ExpeditionRules.Choose(expedition, choiceIndex, _maps[expedition.MapId], _catalog,
                new LootTables(_catalog, _containers), _raids, _gear, _combat, Fighters(current), Limits(current));
            var changed = ExpeditionRules.SyncProfiles(current, resolved.Fighters);
            var done = ExpeditionRules.NoteDeaths(resolved.Expedition, current, changed);

            // Finds stay on the floor until the player drags them into the rig or backpack.
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
