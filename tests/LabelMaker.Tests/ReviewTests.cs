using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LabelMaker.Core.Inbox;
using LabelMaker.Core.Matching;
using LabelMaker.Core.Models;
using LabelMaker.Core.MusicBrainz;
using LabelMaker.Core.Pipeline;
using LabelMaker.Core.Review;
using LabelMaker.Core.Tags;
using Xunit;

namespace LabelMaker.Tests
{
    public class ReleaseReferenceTests
    {
        private const string Id = "6e3e1d3c-0b1d-4a4e-9a0e-5a3b2c1d0e9f";

        [Theory]
        [InlineData("https://musicbrainz.org/release/" + Id, ReferenceKind.Release)]
        [InlineData("musicbrainz.org/release-group/" + Id + "/details", ReferenceKind.ReleaseGroup)]
        [InlineData("  " + Id + "  ", ReferenceKind.Unknown)]
        [InlineData("https://musicbrainz.org/artist/" + Id, ReferenceKind.None)]
        [InlineData("not an id", ReferenceKind.None)]
        [InlineData("", ReferenceKind.None)]
        public void PastedTextIsSortedIntoWhatItPointsAt(string text, ReferenceKind expected)
        {
            var reference = ReleaseReference.Parse(text);
            Assert.Equal(expected, reference.Kind);
            Assert.Equal(expected == ReferenceKind.None ? string.Empty : Id, reference.Id);
        }
    }

    public class ReviewServiceTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

        private static ReleaseInfo Insomniac() =>
            MusicBrainzClient.ParseRelease(File.ReadAllText(TestFiles.Fixture("mb-release-insomniac-2021.json")));

        private static FakeMusicBrainz Searchable() => new FakeMusicBrainz
        {
            Lookup = Insomniac(),
            Groups = new[] { new ReleaseGroupHit { Id = "g1", Title = "Insomniac", ArtistCredit = "Green Day", PrimaryType = "Album", Score = 100 } },
            Browse = new[] { Insomniac() },
        };

        private static ReviewService Service(InboxSandbox s, QueueStore store, IMusicBrainz mb) =>
            new ReviewService(mb, new RealFileOps(), new JournalStore(s.Journals), store, () => new[] { s.Lane },
                new MatchSettings(), new PlannerOptions(), new TagWriteOptions(), () => Now);

        private static void TwoTracks(InboxSandbox s)
        {
            s.AddTrack(@"Albums\Green Day\Insomniac", "02 - Brat.mp3", "Brat", 2);
            s.AddTrack(@"Albums\Green Day\Insomniac", "07 - 86.mp3", "86", 7);
        }

        /// <summary>Queues the one album in the inbox the way the worker would, waiting for review.</summary>
        private static QueueItem Enqueue(InboxSandbox s, QueueStore store, string state = QueueState.NeedsReview)
        {
            var unit = Assert.Single(new InboxScanner().Scan(s.Lane, new RealFileOps()));
            var item = new QueueItem
            {
                Id = unit.Id, LaneId = s.Lane.Id, Folder = unit.Folder, RelativeFolder = unit.RelativeFolder, Kind = unit.Kind,
                FileCount = unit.Files.Count, State = state, Fingerprint = unit.Fingerprint, FirstSeenUtc = Now,
            };
            store.Save(item);
            return item;
        }

        private static List<string> LibraryAudio(InboxSandbox s) => Directory.EnumerateFiles(s.Library, "*.mp3", SearchOption.AllDirectories).ToList();
        private static List<string> InboxAudio(InboxSandbox s) => Directory.EnumerateFiles(s.Inbox, "*.mp3", SearchOption.AllDirectories).Where(f => !f.Contains(ReviewService.TrashFolderName)).ToList();

        [Fact]
        public async Task APreviewShowsWhatWouldHappenAndChangesNothing()
        {
            using var s = new InboxSandbox();
            TwoTracks(s);
            var store = new QueueStore(s.QueuePath);
            var item = Enqueue(s, store);

            var preview = await Service(s, store, Searchable()).PreviewAsync(item.Id, Insomniac().Id, null);

            Assert.True(preview.Success);
            Assert.True(preview.CanApply);
            Assert.Equal("Insomniac", preview.Title);
            Assert.Equal(2, preview.Files.Count);
            Assert.All(preview.Files, f => Assert.False(f.Weak));
            Assert.Contains(preview.Files, f => f.Title == "Brat" && f.Position == 2);
            Assert.Contains("Insomniac", preview.Destination);
            Assert.Empty(LibraryAudio(s));
            Assert.Equal(2, InboxAudio(s).Count);
        }

        [Fact]
        public async Task ChoosingAnEditionTagsAndFilesTheAlbumEvenInDryRunMode()
        {
            using var s = new InboxSandbox();
            TwoTracks(s);
            var store = new QueueStore(s.QueuePath);
            var item = Enqueue(s, store);

            var result = await Service(s, store, Searchable()).ApplyAsync(item.Id, Insomniac().Id, null);

            Assert.True(result.Success, result.Message);
            Assert.Equal(QueueState.Done, result.State);
            Assert.Equal(2, LibraryAudio(s).Count);
            Assert.Empty(InboxAudio(s));
            Assert.All(LibraryAudio(s), f => Assert.Equal(Insomniac().Id, TrackTagFile.Read(f).ReleaseMbid));
            var saved = store.Get(item.Id)!;
            Assert.Equal(QueueState.Done, saved.State);
            Assert.Equal("ChosenByPerson", saved.Match!.Outcome);
            Assert.False(saved.Held);
        }

        [Fact]
        public async Task UndoPutsTheAlbumBackAndTheWorkerDoesNotRedoIt()
        {
            using var s = new InboxSandbox();
            TwoTracks(s);
            var store = new QueueStore(s.QueuePath);
            var item = Enqueue(s, store);
            var mb = Searchable();
            var service = Service(s, store, mb);
            Assert.True((await service.ApplyAsync(item.Id, Insomniac().Id, null)).Success);

            var undone = service.Undo(item.Id);

            Assert.True(undone.Success, undone.Message);
            Assert.Empty(LibraryAudio(s));
            Assert.Equal(2, InboxAudio(s).Count);
            var saved = store.Get(item.Id)!;
            Assert.Equal(QueueState.NeedsReview, saved.State);
            Assert.True(saved.Held);

            // The files changed on disk (an undo rewrites them), and a pass later the album is still waiting for a person.
            foreach (var f in InboxAudio(s)) File.SetLastWriteTimeUtc(f, Now.AddHours(1));
            var clock = new[] { Now.AddHours(2) };
            var processor = new AlbumProcessor(mb, new RealFileOps(), new JournalStore(s.Journals), new MatchSettings(), new PlannerOptions(), new TagWriteOptions());
            var worker = new InboxWorker(() => new[] { s.Lane }, new RealFileOps(), store, processor, new WorkerOptions { DryRun = false }, () => clock[0]);
            await worker.RunPassAsync(CancellationToken.None);
            clock[0] = clock[0].AddMinutes(5);
            await worker.RunPassAsync(CancellationToken.None);

            Assert.Equal(QueueState.NeedsReview, store.Get(item.Id)!.State);
            Assert.Empty(LibraryAudio(s));

            // "Check again" lifts the hold.
            Assert.True(service.Recheck(item.Id).Success);
            Assert.Equal(QueueState.Waiting, store.Get(item.Id)!.State);
            Assert.False(store.Get(item.Id)!.Held);
        }

        [Fact]
        public async Task AnUnknownReleaseOrAnUnreachableMusicBrainzIsRefusedWithAMessageAndNothingMoves()
        {
            using var s = new InboxSandbox();
            TwoTracks(s);
            var store = new QueueStore(s.QueuePath);
            var item = Enqueue(s, store);

            var missing = await Service(s, store, new FakeMusicBrainz { Lookup = null }).ApplyAsync(item.Id, Insomniac().Id, null);
            Assert.False(missing.Success);
            Assert.Contains("not found on MusicBrainz", missing.Message);

            var down = new UnavailableMusicBrainz { Down = true };
            var unreachable = await Service(s, store, down).ApplyAsync(item.Id, Insomniac().Id, null);
            Assert.False(unreachable.Success);
            Assert.Contains("cannot be reached", unreachable.Message);

            Assert.Empty(LibraryAudio(s));
            Assert.Equal(2, InboxAudio(s).Count);
            Assert.Equal(QueueState.NeedsReview, store.Get(item.Id)!.State);
        }

        [Fact]
        public async Task AFileThatDoesNotFitIsFlaggedAndAMappingFixesIt()
        {
            using var s = new InboxSandbox();
            s.AddTrack(@"Albums\Green Day\Insomniac", "01.mp3", "Brat", 2);
            s.AddTrack(@"Albums\Green Day\Insomniac", "02.mp3", "Some Unreleased Demo", 99, album: "Insomniac");
            var store = new QueueStore(s.QueuePath);
            var item = Enqueue(s, store);
            var service = Service(s, store, Searchable());

            var plain = await service.PreviewAsync(item.Id, Insomniac().Id, null);
            Assert.Contains(plain.Files, f => f.Weak && f.Source == "02.mp3");

            var mapped = await service.PreviewAsync(item.Id, Insomniac().Id, new[] { new MappingEntry { File = "02.mp3", Disc = 1, Position = 7 } });
            Assert.True(mapped.CanApply, string.Join(" ", mapped.Problems));
            var placed = mapped.Files.Single(f => f.Source == "02.mp3");
            Assert.False(placed.Weak);
            Assert.Equal("86", placed.Title);

            var result = await service.ApplyAsync(item.Id, Insomniac().Id, new[] { new MappingEntry { File = "02.mp3", Disc = 1, Position = 7 } });
            Assert.True(result.Success, result.Message);
            Assert.Contains(LibraryAudio(s), f => TrackTagFile.Read(f).Title == "86");
        }

        [Fact]
        public async Task ABadMappingIsRefusedInPlainWords()
        {
            using var s = new InboxSandbox();
            TwoTracks(s);
            var store = new QueueStore(s.QueuePath);
            var item = Enqueue(s, store);
            var service = Service(s, store, Searchable());

            var nowhere = await service.PreviewAsync(item.Id, Insomniac().Id, new[] { new MappingEntry { File = "02 - Brat.mp3", Disc = 1, Position = 400 } });
            Assert.False(nowhere.Success);
            Assert.Contains("not on this release", nowhere.Message);

            var twice = await service.PreviewAsync(item.Id, Insomniac().Id, new[]
            {
                new MappingEntry { File = "02 - Brat.mp3", Disc = 1, Position = 3 },
                new MappingEntry { File = "07 - 86.mp3", Disc = 1, Position = 3 },
            });
            Assert.False(twice.Success);
            Assert.Contains("Two files", twice.Message);

            var ghost = await service.PreviewAsync(item.Id, Insomniac().Id, new[] { new MappingEntry { File = "nope.mp3", Disc = 1, Position = 1 } });
            Assert.False(ghost.Success);
            Assert.Contains("no file called", ghost.Message);
            Assert.Empty(LibraryAudio(s));
        }

        [Fact]
        public async Task SearchingByTextListsEditionsWithHowWellTheyFit()
        {
            using var s = new InboxSandbox();
            TwoTracks(s);
            var store = new QueueStore(s.QueuePath);
            var item = Enqueue(s, store);

            var found = await Service(s, store, Searchable()).SearchAsync(item.Id, "Green Day", "Insomniac");

            Assert.True(found.Success);
            var best = Assert.Single(found.Candidates);
            Assert.Equal(Insomniac().Id, best.ReleaseId);
            Assert.True(best.Score > 0.9);

            var empty = await Service(s, store, new FakeMusicBrainz()).SearchAsync(item.Id, "Nobody", "Nothing");
            Assert.True(empty.Success);
            Assert.Empty(empty.Candidates);
            Assert.Contains("nothing under that name", empty.Message);

            var blank = await Service(s, store, Searchable()).SearchAsync(item.Id, "x", "  ");
            Assert.False(blank.Success);
        }

        [Fact]
        public async Task APastedLinkIsResolvedToCandidates()
        {
            using var s = new InboxSandbox();
            TwoTracks(s);
            var store = new QueueStore(s.QueuePath);
            var item = Enqueue(s, store);
            var service = Service(s, store, Searchable());

            var release = await service.ResolveReferenceAsync(item.Id, "https://musicbrainz.org/release/" + Insomniac().Id);
            Assert.True(release.Success);
            Assert.Equal(Insomniac().Id, Assert.Single(release.Candidates).ReleaseId);

            var group = await service.ResolveReferenceAsync(item.Id, "https://musicbrainz.org/release-group/1f0e3c9a-0b1d-4a4e-9a0e-5a3b2c1d0e9f");
            Assert.True(group.Success);
            Assert.Single(group.Candidates);

            var junk = await service.ResolveReferenceAsync(item.Id, "check out this album");
            Assert.False(junk.Success);
            Assert.Contains("does not look like a MusicBrainz link", junk.Message);

            var nothing = await Service(s, store, new FakeMusicBrainz()).ResolveReferenceAsync(item.Id, "https://musicbrainz.org/release/" + Insomniac().Id);
            Assert.False(nothing.Success);
            Assert.Contains("no release or official edition", nothing.Message);
        }

        [Fact]
        public void KeepMyTagsFilesTheAlbumWithItsTagsUntouched()
        {
            using var s = new InboxSandbox();
            s.AddTrack(@"Albums\Some Band\Home Recordings", "a.mp3", "First Song", 1, album: "Home Recordings");
            s.AddTrack(@"Albums\Some Band\Home Recordings", "b.mp3", "Second Song", 2, album: "Home Recordings");
            var store = new QueueStore(s.QueuePath);
            var item = Enqueue(s, store, QueueState.NoMatch);

            var result = Service(s, store, new FakeMusicBrainz()).KeepMyTags(item.Id);

            Assert.True(result.Success, result.Message);
            var files = LibraryAudio(s);
            Assert.Equal(2, files.Count);
            var tags = files.Select(TrackTagFile.Read).OrderBy(t => t.TrackNumber).ToList();
            Assert.Equal(new[] { "First Song", "Second Song" }, tags.Select(t => t.Title));
            Assert.All(tags, t => { Assert.Equal("Home Recordings", t.Album); Assert.Equal(14, t.TotalTracks); Assert.Equal(2021, t.Year); });
            Assert.Equal(QueueState.Done, store.Get(item.Id)!.State);
            Assert.Equal("keep-my-tags", store.Get(item.Id)!.Match!.Source);
        }

        [Fact]
        public void KeepMyTagsRefusesFilesWithoutTitleOrWithClashingTrackNumbers()
        {
            using var s = new InboxSandbox();
            s.AddTrack(@"Albums\X\Y", "a.mp3", "", 1);
            var store = new QueueStore(s.QueuePath);
            var item = Enqueue(s, store, QueueState.NoMatch);
            var noTitle = Service(s, store, new FakeMusicBrainz()).KeepMyTags(item.Id);
            Assert.False(noTitle.Success);
            Assert.Contains("no title, album or artist", noTitle.Message);

            using var t = new InboxSandbox();
            t.AddTrack(@"Albums\X\Y", "a.mp3", "One", 1);
            t.AddTrack(@"Albums\X\Y", "b.mp3", "Two", 1);
            var store2 = new QueueStore(t.QueuePath);
            var item2 = Enqueue(t, store2, QueueState.NoMatch);
            var clash = Service(t, store2, new FakeMusicBrainz()).KeepMyTags(item2.Id);
            Assert.False(clash.Success);
            Assert.Contains("Two files are track 1", clash.Message);
            Assert.Empty(LibraryAudio(t));
        }

        [Fact]
        public void SkippedAlbumsStayPutUntilCheckedAgain()
        {
            using var s = new InboxSandbox();
            TwoTracks(s);
            var store = new QueueStore(s.QueuePath);
            var item = Enqueue(s, store);
            var service = Service(s, store, new FakeMusicBrainz());

            var skipped = service.Skip(item.Id);
            Assert.True(skipped.Success);
            Assert.Equal(QueueState.Skipped, store.Get(item.Id)!.State);
            Assert.True(store.Get(item.Id)!.Held);
            Assert.Equal(2, InboxAudio(s).Count);

            Assert.True(service.Recheck(item.Id).Success);
            Assert.Equal(QueueState.Waiting, store.Get(item.Id)!.State);
        }

        [Fact]
        public void DiscardMovesTheAlbumToTheTrashAndRestoreBringsItBack()
        {
            using var s = new InboxSandbox();
            TwoTracks(s);
            s.AddFile(@"Albums\Green Day\Insomniac", "cover.jpg");
            var store = new QueueStore(s.QueuePath);
            var item = Enqueue(s, store);
            var service = Service(s, store, new FakeMusicBrainz());

            var discarded = service.Discard(item.Id);

            Assert.True(discarded.Success, discarded.Message);
            Assert.Empty(InboxAudio(s));
            Assert.False(Directory.Exists(Path.Combine(s.Inbox, "Albums")), "the emptied folders are cleaned up");
            var trash = store.Get(item.Id)!.TrashPath;
            Assert.StartsWith(Path.Combine(s.Inbox, ReviewService.TrashFolderName), trash);
            Assert.Equal(3, Directory.EnumerateFiles(trash, "*", SearchOption.AllDirectories).Count(f => !f.EndsWith("restore.json")));
            Assert.Equal(QueueState.Skipped, store.Get(item.Id)!.State);
            Assert.False(service.Recheck(item.Id).Success, "a discarded album has to be restored first");

            var restored = service.RestoreDiscarded(item.Id);

            Assert.True(restored.Success, restored.Message);
            Assert.Equal(2, InboxAudio(s).Count);
            Assert.True(File.Exists(Path.Combine(s.Inbox, "Albums", "Green Day", "Insomniac", "cover.jpg")));
            Assert.False(Directory.Exists(Path.Combine(s.Inbox, ReviewService.TrashFolderName)));
            Assert.Equal(QueueState.NeedsReview, store.Get(item.Id)!.State);
            Assert.Equal(string.Empty, store.Get(item.Id)!.TrashPath);
        }

        [Fact]
        public void RestoreRefusesWhenSomethingIsInTheWay()
        {
            using var s = new InboxSandbox();
            TwoTracks(s);
            var store = new QueueStore(s.QueuePath);
            var item = Enqueue(s, store);
            var service = Service(s, store, new FakeMusicBrainz());
            Assert.True(service.Discard(item.Id).Success);
            s.AddFile(@"Albums\Green Day\Insomniac", "02 - Brat.mp3", "a new download with the same name");

            var restored = service.RestoreDiscarded(item.Id);

            Assert.False(restored.Success);
            Assert.Contains("already exists", restored.Message);
            Assert.True(Directory.Exists(store.Get(item.Id)!.TrashPath));
        }

        [Fact]
        public void ADamagedAlbumCanStillBeDiscarded()
        {
            using var s = new InboxSandbox();
            s.AddFile(@"Albums\Broken\Album", "01.mp3", "this is not audio");
            var store = new QueueStore(s.QueuePath);
            var item = Enqueue(s, store, QueueState.Failed);

            var discarded = Service(s, store, new FakeMusicBrainz()).Discard(item.Id);

            Assert.True(discarded.Success, discarded.Message);
            Assert.Empty(InboxAudio(s));
        }

        [Fact]
        public async Task AnAlbumBeingWorkedOnOrAlreadyFiledIsRefused()
        {
            using var s = new InboxSandbox();
            TwoTracks(s);
            var store = new QueueStore(s.QueuePath);
            var item = Enqueue(s, store, QueueState.Applying);
            var service = Service(s, store, Searchable());

            Assert.Contains("being worked on", (await service.ApplyAsync(item.Id, Insomniac().Id, null)).Message);
            Assert.False(service.Skip(item.Id).Success);

            item.State = QueueState.Done;
            store.Save(item);
            Assert.Contains("already filed", (await service.ApplyAsync(item.Id, Insomniac().Id, null)).Message);
            Assert.False(service.Discard(item.Id).Success);

            Assert.False(service.Undo("no-such-album").Success);
            item.State = QueueState.NeedsReview;
            store.Save(item);
            Assert.Contains("Only an album that was filed", service.Undo(item.Id).Message);
        }
    }
}
