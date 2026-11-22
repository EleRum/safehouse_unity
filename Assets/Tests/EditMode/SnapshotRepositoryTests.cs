using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Safehouse.Core;
using Safehouse.Data;

namespace Safehouse.Tests
{
    /// <summary>The "before this trip" copies a character can be rolled back to, in a temp folder.</summary>
    public sealed class SnapshotRepositoryTests
    {
        private static Dictionary<string, ItemDefinition> _catalog;

        private static Dictionary<string, ItemDefinition> Catalog =>
            _catalog ?? (_catalog = CatalogLoader.Load());

        private string _folder;
        private ProfileRepository _profiles;
        private SnapshotRepository _snapshots;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Path.GetTempPath(), "safehouse-snapshots-" + Guid.NewGuid().ToString("N"));
            _profiles = new ProfileRepository(_folder, Catalog);
            _snapshots = new SnapshotRepository(_folder, Catalog);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }

        private static int _next;
        private static string NewId() => (++_next).ToString("x32");

        private Profile Character(string name = "Ana")
        {
            var profile = _profiles.Create(name);
            return _profiles.Load(profile.ProfileId);
        }

        [Test]
        public void ASnapshotComesBackExactlyAsSaved()
        {
            var profile = Character();
            _snapshots.Save(profile, 1000, "before-launch");

            var restored = _snapshots.Load(profile.ProfileId, _snapshots.Latest(profile.ProfileId).SnapshotId);

            Assert.AreEqual(ProfileSerializer.Serialize(profile), ProfileSerializer.Serialize(restored));
        }

        [Test]
        public void EverySnapshotKeepsTheStashItWasTakenWith()
        {
            var profile = Character();
            var withBandage = profile.With(
                stash: profile.Stash.With(new[] { ItemInstance.Create(NewId(), "army-bandage", 1, 1) }));
            _profiles.Save(withBandage);

            _snapshots.Save(profile, 1000, "before-launch");
            _profiles.Save(withBandage.With(money: 42));

            var restored = _snapshots.LoadLatest(profile.ProfileId);

            Assert.AreEqual(0, restored.Stash.Stash.Count, "the snapshot predates the bandage");
            Assert.AreEqual(profile.Money, restored.Money);
        }

        [Test]
        public void NewestIsListedFirst()
        {
            var profile = Character();
            _snapshots.Save(profile, 1000, "before-launch");
            _snapshots.Save(profile, 2000, "manual");
            _snapshots.Save(profile, 3000, "before-launch");

            var listed = _snapshots.List(profile.ProfileId);

            Assert.AreEqual(3, listed.Count);
            Assert.AreEqual(3000, listed[0].SavedAt);
            Assert.AreEqual(1000, listed[2].SavedAt);
        }

        [Test]
        public void OnlyTheNewestFewAreKept()
        {
            var profile = Character();
            for (var i = 0; i < SnapshotRepository.MaxPerCharacter + 4; i++)
            {
                _snapshots.Save(profile, 1000 + i, "before-launch");
            }

            var listed = _snapshots.List(profile.ProfileId);

            Assert.AreEqual(SnapshotRepository.MaxPerCharacter, listed.Count);
            Assert.AreEqual(1000 + SnapshotRepository.MaxPerCharacter + 3, listed[0].SavedAt,
                "the newest one survives");
        }

        [Test]
        public void TwoSnapshotsInTheSameSecondBothSurvive()
        {
            var profile = Character();
            _snapshots.Save(profile, 1000, "before-launch");
            _snapshots.Save(profile, 1000, "before-launch");

            var listed = _snapshots.List(profile.ProfileId);

            Assert.AreEqual(2, listed.Count);
            Assert.AreNotEqual(listed[0].SnapshotId, listed[1].SnapshotId);
        }

        [Test]
        public void ADifferentCharacterHasItsOwnSnapshots()
        {
            var ana = Character("Ana");
            var bo = Character("Bo");
            _snapshots.Save(ana, 1000, "before-launch");

            Assert.AreEqual(0, _snapshots.List(bo.ProfileId).Count);
            Assert.IsNull(_snapshots.LoadLatest(bo.ProfileId));
        }

        [Test]
        public void ASnapshotOfAnotherCharacterIsRefused()
        {
            var ana = Character("Ana");
            var bo = Character("Bo");
            _snapshots.Save(ana, 1000, "before-launch");
            var id = _snapshots.Latest(ana.ProfileId).SnapshotId;

            var error = Assert.Throws<StorageException>(() => _snapshots.Load(bo.ProfileId, id));

            StringAssert.Contains("belongs to", error.Message);
        }

        [Test]
        public void ADamagedSnapshotIsRefusedAndTheCharacterFileIsUntouched()
        {
            var profile = Character();
            _snapshots.Save(profile, 1000, "before-launch");
            var id = _snapshots.Latest(profile.ProfileId).SnapshotId;
            var path = Path.Combine(_snapshots.FolderFor(profile.ProfileId), id + ".json");
            File.WriteAllText(path, "{ this is not a profile");

            var before = File.ReadAllBytes(_profiles.PathFor(profile.ProfileId));
            Assert.Throws<StorageException>(() => _snapshots.Load(profile.ProfileId, id));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(_profiles.PathFor(profile.ProfileId)));
        }

        [Test]
        public void OneCanBeDeleted()
        {
            var profile = Character();
            _snapshots.Save(profile, 1000, "before-launch");
            var id = _snapshots.Latest(profile.ProfileId).SnapshotId;

            _snapshots.Delete(profile.ProfileId, id);

            Assert.AreEqual(0, _snapshots.List(profile.ProfileId).Count);
        }

        [Test]
        public void AReasonBecomesPartOfTheFileNameAndCannotEscapeTheFolder()
        {
            var profile = Character();
            var kept = _snapshots.Save(profile, 1000, "../../evil name");

            StringAssert.DoesNotContain("..", kept.SnapshotId);
            StringAssert.DoesNotContain("/", kept.SnapshotId);
            Assert.AreEqual(1, _snapshots.List(profile.ProfileId).Count);
        }

        [Test]
        public void NoSnapshotsMeansNoLatest()
        {
            var profile = Character();
            Assert.IsNull(_snapshots.Latest(profile.ProfileId));
            Assert.IsNull(_snapshots.LoadLatest(profile.ProfileId));
        }
    }
}
