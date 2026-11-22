using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using Safehouse.Core;

namespace Safehouse.Data
{
    /// <summary>
    /// One file per character: <c>quests/&lt;profile id&gt;.json</c>. Quest state lives here rather than
    /// inside the profile so turning the chain on needed no save-schema migration - the same reasoning
    /// the expedition files follow. A missing file is a character who has not touched a quest yet, which
    /// is not an error.
    /// </summary>
    public sealed class QuestRepository
    {
        private readonly string _folder;

        public QuestRepository(string dataFolder)
        {
            if (string.IsNullOrWhiteSpace(dataFolder))
            {
                throw new StorageException("A quest folder is required.");
            }

            _folder = Path.Combine(dataFolder, "quests");
        }

        public string DataFolder => _folder;

        public string PathFor(string profileId)
        {
            if (string.IsNullOrWhiteSpace(profileId))
            {
                throw new StorageException("A character id is required.");
            }

            return Path.Combine(_folder, profileId + ".json");
        }

        /// <summary>Never throws for a file that is missing or unreadable: a damaged log is moved aside
        /// and the character simply starts the chain again, which loses progress but never blocks play.</summary>
        public QuestLog Load(string profileId, ICollection<string> notices = null)
        {
            var path = PathFor(profileId);
            if (!File.Exists(path))
            {
                return QuestLog.Empty;
            }

            try
            {
                return QuestSerializer.FromJson(JObject.Parse(File.ReadAllText(path)), profileId);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                                          || error is Newtonsoft.Json.JsonException || error is StorageException
                                          || error is ValidationException)
            {
                var kept = path + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss");
                try
                {
                    File.Move(path, kept);
                    notices?.Add($"Quest log was unreadable and was moved to {Path.GetFileName(kept)}: {error.Message}");
                }
                catch (Exception moveError) when (moveError is IOException || moveError is UnauthorizedAccessException)
                {
                    notices?.Add($"Quest log was unreadable: {error.Message}");
                }

                return QuestLog.Empty;
            }
        }

        public void Save(string profileId, QuestLog log)
        {
            var path = PathFor(profileId);
            try
            {
                Directory.CreateDirectory(_folder);
                AtomicFile.Write(path, QuestSerializer.Serialize(profileId, log ?? QuestLog.Empty));
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                throw new StorageException(
                    $"Could not save the quest log: {error.Message}\nThe requested change was not applied.", error);
            }
        }

        public void Delete(string profileId)
        {
            try
            {
                File.Delete(PathFor(profileId));
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                throw new StorageException($"Could not delete the quest log: {error.Message}", error);
            }
        }
    }
}
