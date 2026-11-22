using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Safehouse.Core;

namespace Safehouse.Data
{
    /// <summary>One kept copy of a character, as listed by <see cref="SnapshotRepository.List"/>.</summary>
    public sealed class SnapshotInfo
    {
        public SnapshotInfo(string snapshotId, string profileId, long savedAt, string reason, long bytes)
        {
            SnapshotId = snapshotId;
            ProfileId = profileId;
            SavedAt = savedAt;
            Reason = reason;
            Bytes = bytes;
        }

        public string SnapshotId { get; }
        public string ProfileId { get; }

        /// <summary>Unix seconds, taken from the name so listing never opens a file.</summary>
        public long SavedAt { get; }

        /// <summary>Free-form tag from the name: "before-launch", "manual", ...</summary>
        public string Reason { get; }

        public long Bytes { get; }

        public DateTime SavedUtc => DateTimeOffset.FromUnixTimeSeconds(SavedAt).UtcDateTime;
    }

    /// <summary>
    /// A small ring of "just before this" copies of a character, one per expedition launch, so a trip
    /// that goes wrong can be rolled back instead of the character being stuck in it. The files are
    /// ordinary profile documents written by <see cref="ProfileSerializer"/>, under
    /// <c>snapshots/&lt;character id&gt;/&lt;unix seconds&gt;-&lt;reason&gt;.json</c>, so they can be
    /// read by hand and moved between machines. Nothing here is authoritative: the character file in
    /// <c>saves/</c> stays the one the game plays, a snapshot only ever replaces it when the player
    /// asks for that.
    ///
    /// The account (shared hideout) is deliberately not snapshotted - it belongs to every character
    /// at once, and rolling one character back must not undo another one's work.
    /// </summary>
    public sealed class SnapshotRepository
    {
        /// <summary>Older snapshots are dropped beyond this; six is a few hours of trips.</summary>
        public const int MaxPerCharacter = 6;

        public const int MaxReasonLength = 12;

        private readonly string _root;
        private readonly IReadOnlyDictionary<string, ItemDefinition> _catalog;

        public SnapshotRepository(string dataFolder, IReadOnlyDictionary<string, ItemDefinition> catalog)
        {
            if (string.IsNullOrWhiteSpace(dataFolder))
            {
                throw new StorageException("A snapshot folder is required.");
            }

            _root = Path.Combine(dataFolder, "snapshots");
            _catalog = catalog ?? throw new StorageException("The item catalog is required.");
        }

        /// <summary>The snapshots/ folder itself.</summary>
        public string DataFolder => _root;

        public string FolderFor(string profileId) => Path.Combine(_root, RequireId(profileId, "character"));

        /// <summary>Snapshots of one character, newest first. Never throws for a missing folder.</summary>
        public IReadOnlyList<SnapshotInfo> List(string profileId)
        {
            var folder = FolderFor(profileId);
            if (!Directory.Exists(folder))
            {
                return new SnapshotInfo[0];
            }

            var found = new List<SnapshotInfo>();
            foreach (var path in Directory.GetFiles(folder, "*.json"))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (!TrySplit(id, out var savedAt, out var reason))
                {
                    continue;
                }

                long bytes;
                try
                {
                    bytes = new FileInfo(path).Length;
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    bytes = 0;
                }

                found.Add(new SnapshotInfo(id, profileId, savedAt, reason, bytes));
            }

            return found.OrderByDescending(snapshot => snapshot.SavedAt)
                .ThenByDescending(snapshot => snapshot.SnapshotId, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>Keeps a copy of the character as it is now. Returns what was written.</summary>
        public SnapshotInfo Save(Profile profile, double now, string reason)
        {
            if (profile == null)
            {
                throw new StorageException("There is no character to snapshot.");
            }

            var folder = FolderFor(profile.ProfileId);
            var savedAt = ExpeditionRules.Timestamp(now);
            var id = Name(savedAt, reason, folder);
            try
            {
                Directory.CreateDirectory(folder);
                AtomicFile.Write(Path.Combine(folder, id + ".json"), ProfileSerializer.Serialize(profile));
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                throw new StorageException($"Could not keep a snapshot of {profile.DisplayName}: {error.Message}", error);
            }

            Prune(profile.ProfileId);
            return new SnapshotInfo(id, profile.ProfileId, savedAt, CleanReason(reason), 0);
        }

        /// <summary>The newest snapshot of a character, or null when there is none.</summary>
        public SnapshotInfo Latest(string profileId) => List(profileId).FirstOrDefault();

        /// <summary>Reads one back. Throws <see cref="StorageException"/> if it cannot be used as a character.</summary>
        public Profile Load(string profileId, string snapshotId)
        {
            var path = Path.Combine(FolderFor(profileId), RequireId(snapshotId, "snapshot") + ".json");
            byte[] raw;
            try
            {
                raw = File.ReadAllBytes(path);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                throw new StorageException($"Could not read that snapshot: {error.Message}", error);
            }

            return Decode(raw, profileId, snapshotId);
        }

        public Profile LoadLatest(string profileId)
        {
            var latest = Latest(profileId);
            return latest == null ? null : Load(profileId, latest.SnapshotId);
        }

        public void Delete(string profileId, string snapshotId)
        {
            var path = Path.Combine(FolderFor(profileId), RequireId(snapshotId, "snapshot") + ".json");
            try
            {
                File.Delete(path);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                throw new StorageException($"Could not delete that snapshot: {error.Message}", error);
            }
        }

        /// <summary>Decodes bytes the same way the character file is decoded, so a snapshot that
        /// cannot be a character is refused here rather than half-loaded later.</summary>
        public Profile Decode(byte[] raw, string profileId, string where)
        {
            Profile profile;
            try
            {
                profile = ProfileSerializer.FromJson(ProfileSerializer.Parse(
                    System.Text.Encoding.UTF8.GetString(raw)));
            }
            catch (Exception error)
            {
                throw new StorageException($"The snapshot {where} is damaged ({error.Message}).", error);
            }

            if (profile.ProfileId != profileId)
            {
                throw new StorageException(
                    $"The snapshot {where} belongs to {profile.ProfileId}, not to {profileId}.");
            }

            try
            {
                ProfileRules.ValidateAgainstCatalog(profile, _catalog);
            }
            catch (ValidationException error)
            {
                throw new StorageException($"The snapshot {where} cannot be used: {error.Message}", error);
            }

            return profile;
        }

        private void Prune(string profileId)
        {
            var all = List(profileId);
            foreach (var stale in all.Skip(MaxPerCharacter))
            {
                try
                {
                    File.Delete(Path.Combine(FolderFor(profileId), stale.SnapshotId + ".json"));
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    // A snapshot nobody deleted is not a reason to fail the save that just succeeded.
                }
            }
        }

        private static string Name(long savedAt, string reason, string folder)
        {
            var stem = savedAt + "-" + CleanReason(reason);
            var candidate = stem;
            var counter = 2;
            while (File.Exists(Path.Combine(folder, candidate + ".json")) && counter < 100)
            {
                candidate = stem + "-" + counter.ToString(System.Globalization.CultureInfo.InvariantCulture);
                counter++;
            }

            return candidate;
        }

        private static string CleanReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return "manual";
            }

            var cleaned = new string(reason.Trim().ToLowerInvariant()
                .Select(c => char.IsLetterOrDigit(c) ? c : '-')
                .ToArray());
            while (cleaned.Contains("--"))
            {
                cleaned = cleaned.Replace("--", "-");
            }

            cleaned = cleaned.Trim('-');
            if (cleaned.Length == 0)
            {
                return "manual";
            }

            return cleaned.Length <= MaxReasonLength ? cleaned : cleaned.Substring(0, MaxReasonLength);
        }

        private static bool TrySplit(string id, out long savedAt, out string reason)
        {
            savedAt = 0;
            reason = "?";
            var dash = id.IndexOf('-');
            if (dash <= 0 || !long.TryParse(id.Substring(0, dash),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out savedAt))
            {
                return false;
            }

            reason = id.Substring(dash + 1);
            return true;
        }

        private static string RequireId(string value, string what)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 64
                || !value.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'))
            {
                throw new StorageException($"That {what} id cannot be part of a file name: '{value}'.");
            }

            return value;
        }
    }
}
