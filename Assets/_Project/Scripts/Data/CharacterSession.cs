using System;
using System.Collections.Generic;
using System.Linq;
using Safehouse.Core;

namespace Safehouse.Data
{
    /// <summary>
    /// The character the player has open, and the one way it changes: build the new version, <see cref="Commit"/>
    /// it (which saves it), and only then does it become the current one. A save that fails throws and leaves
    /// <see cref="Profile"/> as it was, so the screen and the file cannot disagree - the same guarantee the
    /// Python InventorySession gives. Also holds the data-folder lock for as long as the game has it open.
    /// The shared account (hideout + character order) is loaded alongside the character.
    /// </summary>
    public sealed class CharacterSession : IDisposable
    {
        private DataDirectoryLock _lock;

        private CharacterSession(ProfileRepository repository, AccountRepository accountRepository, Account account,
            Profile profile, DataDirectoryLock folderLock)
        {
            Repository = repository;
            AccountRepository = accountRepository;
            Account = account;
            Profile = profile;
            _lock = folderLock;
        }

        /// <summary>Null for an <see cref="Unsaved"/> session.</summary>
        public ProfileRepository Repository { get; }

        /// <summary>Null for an <see cref="Unsaved"/> session.</summary>
        public AccountRepository AccountRepository { get; }

        public Account Account { get; private set; }
        public Profile Profile { get; private set; }

        /// <summary>False when nothing is being written: the changes exist only until the game closes.</summary>
        public bool Saves => Repository != null;

        /// <summary>
        /// Opens the data folder (locking it against a second copy of the game), the account, and a character:
        /// the one with <paramref name="preferredId"/> if it exists, otherwise the first by name. A folder with
        /// no account gets a default one; a folder with no characters gets <paramref name="firstCharacter"/>, saved.
        /// Problems with individual files, and any restore from a backup, are added to <paramref name="notices"/>;
        /// throws <see cref="StorageException"/> if the folder is in use or the first character cannot be saved.
        /// </summary>
        public static CharacterSession Open(IReadOnlyDictionary<string, ItemDefinition> catalog, string folder,
            Func<Profile> firstCharacter, string preferredId, ICollection<string> notices,
            Func<double> clock = null)
        {
            clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var folderLock = DataDirectoryLock.Acquire(folder);
            try
            {
                var accountRepository = new AccountRepository(folder);
                var account = accountRepository.Exists
                    ? accountRepository.Load(notices)
                    : accountRepository.CreateDefault();

                var now = clock();
                account = TickFuel(account, now, accountRepository);

                var repository = new ProfileRepository(folder, catalog);
                var (profiles, problems) = repository.List(notices);
                foreach (var problem in problems)
                {
                    notices?.Add(problem);
                }

                var chosen = profiles.FirstOrDefault(profile => profile.ProfileId == preferredId) ?? profiles.FirstOrDefault();
                if (chosen == null)
                {
                    chosen = firstCharacter();
                    chosen = HideoutRules.EnsureRoomStashSize(HideoutLoader.Load(), chosen);
                    repository.Save(chosen);

                    if (!account.CharacterOrder.Contains(chosen.ProfileId))
                    {
                        account = account.With(characterOrder: account.CharacterOrder.Concat(new[] { chosen.ProfileId }));
                        accountRepository.Save(account);
                    }
                }
                else
                {
                    var resized = HideoutRules.EnsureRoomStashSize(HideoutLoader.Load(), chosen);
                    if (resized != chosen)
                    {
                        repository.Save(resized);
                        chosen = resized;
                    }
                }

                return new CharacterSession(repository, accountRepository, account, chosen, folderLock);
            }
            catch
            {
                folderLock.Dispose();
                throw;
            }
        }

        private static Account TickFuel(Account account, double now, AccountRepository accountRepository)
        {
            var definition = HideoutLoader.Load();
            var elapsedHours = account.LastFuelTick == 0.0
                ? 0.0
                : (now - account.LastFuelTick) / 3600.0;
            var ticked = HideoutRules.TickFuel(definition, account.Hideout, account.Population, elapsedHours);
            var updated = account.With(
                hideout: ticked,
                lastFuelTick: now);

            if (updated.Hideout.Fuel != account.Hideout.Fuel || updated.LastFuelTick != account.LastFuelTick)
            {
                accountRepository?.Save(updated);
            }

            return updated;
        }

        /// <summary>A session that never touches the disk, for when the real one cannot be opened.</summary>
        public static CharacterSession Unsaved(Profile profile, Account account = null) =>
            new CharacterSession(null, null, account ?? Account.CreateNew(), profile, null);

        /// <summary>Saves the candidate and makes it the current profile. Throws, changing nothing, if it cannot be saved.</summary>
        public void Commit(Profile candidate)
        {
            if (candidate.ProfileId != Profile.ProfileId)
            {
                throw new StorageException("A change can only be committed to the character that is open.");
            }

            Repository?.Save(candidate);
            Profile = candidate;
        }

        /// <summary>Saves the candidate and makes it the current account. Throws, changing nothing, if it cannot be saved.</summary>
        public void Commit(Account candidate)
        {
            AccountRepository?.Save(candidate);
            Account = candidate;
        }

        /// <summary>Reads the open character and the account back from disk after another writer, such as an expedition, changed them.</summary>
        public void Reload(ICollection<string> notices = null)
        {
            if (Repository == null)
            {
                return;
            }

            Profile = Repository.Load(Profile.ProfileId, notices);
            if (AccountRepository != null)
            {
                Account = AccountRepository.Load(notices);
            }
        }

        /// <summary>
        /// Saves a new character with the given Gundog sheet, adds them to the account, and opens them.
        /// The current character is left as it was if the save fails.
        /// </summary>
        public Profile CreateCharacter(string displayName, IReadOnlyDictionary<string, string> bio, GundogSheet sheet)
        {
            if (Repository == null)
            {
                throw new StorageException("A character cannot be created while saving is unavailable.");
            }

            PruneOrder();

            if (Repository.CharacterIds().Count >= CharacterSheet.MaxCharacters)
            {
                throw new StorageException($"At most {CharacterSheet.MaxCharacters} characters can be kept.");
            }

            var profile = HideoutRules.EnsureRoomStashSize(HideoutLoader.Load(),
                Profile.CreateNew(Guid.NewGuid().ToString("N"), displayName, bio: bio, gundog: sheet));
            Repository.Save(profile);
            if (!Account.CharacterOrder.Contains(profile.ProfileId))
            {
                var account = Account.With(characterOrder: Account.CharacterOrder.Concat(new[] { profile.ProfileId }));
                AccountRepository.Save(account);
                Account = account;
            }

            Profile = profile;
            return profile;
        }

        /// <summary>
        /// Drops a character's file and forgets it in the account's order. The order is what counts
        /// against <see cref="Account.MaxCharacterOrder"/> and what the hideout reads as its
        /// population, so a deleted character that stayed listed would eventually refuse new ones.
        /// </summary>
        public void DeleteCharacter(string profileId)
        {
            if (Repository == null)
            {
                throw new StorageException("A character cannot be deleted while saving is unavailable.");
            }

            Repository.Delete(profileId);
            var kept = Account.CharacterOrder.Where(id => id != profileId).ToList();
            if (kept.Count != Account.CharacterOrder.Count)
            {
                var account = Account.With(characterOrder: kept);
                AccountRepository.Save(account);
                Account = account;
            }
        }

        /// <summary>Forgets ids in the order that no longer have a save file.</summary>
        private void PruneOrder()
        {
            var present = Repository.CharacterIds();
            var kept = Account.CharacterOrder.Where(present.Contains).ToList();
            if (kept.Count == Account.CharacterOrder.Count)
            {
                return;
            }

            var account = Account.With(characterOrder: kept);
            AccountRepository.Save(account);
            Account = account;
        }

        /// <summary>Opens another character. The current one is untouched if that fails.</summary>
        public void Switch(string profileId, ICollection<string> notices = null)
        {
            if (Repository == null)
            {
                throw new StorageException("Characters cannot be switched while saving is unavailable.");
            }

            Profile = Repository.Load(profileId, notices);
        }

        /// <summary>The characters that can be opened, by name.</summary>
        public IReadOnlyList<Profile> Characters(ICollection<string> notices = null) =>
            Repository == null ? new[] { Profile } : Repository.List(notices).Profiles;

        public void Dispose()
        {
            _lock?.Dispose();
            _lock = null;
        }
    }
}
