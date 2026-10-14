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
using LabelMaker.Core.Tags;
using Xunit;

namespace LabelMaker.Tests
{
    /// <summary>A throw-away inbox, library, journal folder and queue file, with a lane over them.</summary>
    internal sealed class InboxSandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LabelMaker-inbox-" + Guid.NewGuid().ToString("N"));
        public string Inbox => Path.Combine(Root, "inbox");
        public string Library => Path.Combine(Root, "library");
        public string Journals => Path.Combine(Root, "journals");
        public string QueuePath => Path.Combine(Root, "state", "queue.json");
        public Lane Lane { get; }

        public InboxSandbox()
        {
            Directory.CreateDirectory(Inbox);
            Directory.CreateDirectory(Library);
            Lane = new Lane { Id = "main", Name = "Main", InboxPath = Inbox, LibraryPath = Library };
        }

        /// <summary>A silent MP3 tagged like MusicGrabber output for the Insomniac album, at <paramref name="relativeFolder"/> under the inbox.</summary>
        public string AddTrack(string relativeFolder, string fileName, string title, int track, int disc = 1, string album = "Insomniac")
        {
            var folder = Path.Combine(Inbox, relativeFolder);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, fileName);
            File.Copy(TestFiles.Fixture("silence.mp3"), path, overwrite: true);
            TrackTagFile.Write(path, new TrackTags
            {
                Title = title, Artist = "Green Day", AlbumArtist = "Green Day", Album = album, TrackNumber = track, TotalTracks = 14,
                DiscNumber = disc, TotalDiscs = 2, Year = 2021,
            }, new TagWriteOptions());
            return path;
        }

        public string AddFile(string relativeFolder, string fileName, string content = "x")
        {
            var folder = Path.Combine(Inbox, relativeFolder);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, fileName);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch (IOException) { }
            }
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    internal sealed class UnavailableMusicBrainz : IMusicBrainz
    {
        public int Calls { get; private set; }
        public bool Down { get; set; } = true;
        public IMusicBrainz Working { get; set; } = null!;

        public Task<ReleaseInfo?> LookupReleaseAsync(string releaseId, CancellationToken cancellation = default) =>
            Down ? throw new MusicBrainzUnavailableException("MusicBrainz is busy.") : Working.LookupReleaseAsync(releaseId, cancellation);

        public Task<IReadOnlyList<ReleaseGroupHit>> SearchReleaseGroupsAsync(string artist, string album, int limit, SearchShape shape, CancellationToken cancellation = default)
        {
            Calls++;
            return Down ? throw new MusicBrainzUnavailableException("MusicBrainz is busy.") : Working.SearchReleaseGroupsAsync(artist, album, limit, shape, cancellation);
        }

        public Task<IReadOnlyList<ReleaseInfo>> BrowseOfficialReleasesAsync(string releaseGroupId, CancellationToken cancellation = default) =>
            Down ? throw new MusicBrainzUnavailableException("MusicBrainz is busy.") : Working.BrowseOfficialReleasesAsync(releaseGroupId, cancellation);
    }

    public class InboxScannerTests
    {
        private static IReadOnlyList<AlbumUnit> Scan(InboxSandbox s) => new InboxScanner().Scan(s.Lane, new RealFileOps());

        [Fact]
        public void AFolderOfAudioIsOneAlbumUnit()
        {
            using var s = new InboxSandbox();
            s.AddTrack(@"Albums\Green Day\Insomniac", "01 - Armatage Shanks.mp3", "Armatage Shanks", 1);
            s.AddTrack(@"Albums\Green Day\Insomniac", "02 - Brat.mp3", "Brat", 2);
            s.AddFile(@"Albums\Green Day\Insomniac", "cover.jpg");
            s.AddFile(@"Albums\Green Day\Insomniac", ".albuminfo", "{}");

            var unit = Assert.Single(Scan(s));

            Assert.Equal(UnitKind.Album, unit.Kind);
            Assert.Equal(2, unit.Files.Count);
            Assert.Equal(@"Albums\Green Day\Insomniac", unit.RelativeFolder);
            Assert.False(unit.HasTemporaryFiles);
        }

        [Fact]
        public void EveryTrackUnderSinglesIsItsOwnUnit()
        {
            using var s = new InboxSandbox();
            s.AddTrack(@"Singles\Green Day", "Brat.mp3", "Brat", 2);
            s.AddTrack(@"Singles\Green Day", "86.mp3", "86", 7);

            var units = Scan(s);

            Assert.Equal(2, units.Count);
            Assert.All(units, u => { Assert.Equal(UnitKind.Single, u.Kind); Assert.Single(u.Files); });
            Assert.Equal(2, units.Select(u => u.Id).Distinct().Count());
        }

        [Fact]
        public void IdsStayTheSameBetweenScansAndDifferBetweenFolders()
        {
            using var s = new InboxSandbox();
            s.AddTrack(@"Albums\A\One", "01.mp3", "Brat", 2);
            s.AddTrack(@"Albums\A\Two", "01.mp3", "Brat", 2);

            var first = Scan(s).Select(u => u.Id).OrderBy(x => x).ToList();
            var second = Scan(s).Select(u => u.Id).OrderBy(x => x).ToList();

            Assert.Equal(first, second);
            Assert.Equal(2, first.Distinct().Count());
            Assert.All(first, id => Assert.Matches("^[a-z0-9]{20}$", id)); // safe as a journal file name
        }

        [Fact]
        public void TrashPlaylistAndHiddenFoldersAreNeverLookedInto()
        {
            using var s = new InboxSandbox();
            s.AddTrack(@".trash\Old", "01.mp3", "Brat", 2);
            s.AddTrack(@"_Trash\Old", "01.mp3", "Brat", 2);
            s.AddTrack(@"Playlists", "01.mp3", "Brat", 2);
            s.AddFile(@"Albums\Nothing", "notes.txt");
            s.AddTrack(@"Albums\Real", "01.mp3", "Brat", 2);

            var unit = Assert.Single(Scan(s));
            Assert.Equal(@"Albums\Real", unit.RelativeFolder);
        }

        [Fact]
        public void DiscFoldersCountUnderTheAlbumFolderAbove()
        {
            using var s = new InboxSandbox();
            s.AddTrack(@"Albums\Band\Big Album\CD1", "01.mp3", "Brat", 2, disc: 1);
            s.AddTrack(@"Albums\Band\Big Album\CD2", "01.mp3", "86", 7, disc: 2);
            s.AddTrack(@"Albums\Band\Big Album\Disc 3", "01.mp3", "Jaded", 11, disc: 3);

            var unit = Assert.Single(Scan(s));

            Assert.Equal(@"Albums\Band\Big Album", unit.RelativeFolder);
            Assert.Equal(3, unit.Files.Count);
        }

        [Fact]
        public void ATemporaryDownloadFileMarksTheFolderUnfinishedAndIsNotMusic()
        {
            using var s = new InboxSandbox();
            s.AddTrack(@"Albums\A\B", "01.mp3", "Brat", 2);
            s.AddFile(@"Albums\A\B", "02.mp3.part");
            s.AddFile(@"Albums\A\B", "~03.mp3");
            s.AddFile(@"Albums\A\B", ".syncthing.04.mp3.tmp");

            var unit = Assert.Single(Scan(s));

            Assert.True(unit.HasTemporaryFiles);
            Assert.Single(unit.Files);
        }

        [Fact]
        public void TheFingerprintChangesWhenAFileIsAddedOrRewritten()
        {
            using var s = new InboxSandbox();
            var path = s.AddTrack(@"Albums\A\B", "01.mp3", "Brat", 2);
            var before = Assert.Single(Scan(s)).Fingerprint;
            Assert.Equal(before, Assert.Single(Scan(s)).Fingerprint);

            s.AddTrack(@"Albums\A\B", "02.mp3", "86", 7);
            var afterAdd = Assert.Single(Scan(s)).Fingerprint;
            Assert.NotEqual(before, afterAdd);

            File.AppendAllText(path, "more");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
            Assert.NotEqual(afterAdd, Assert.Single(Scan(s)).Fingerprint);
        }

        [Fact]
        public void AMissingInboxGivesNothing()
        {
            using var s = new InboxSandbox();
            s.Lane.InboxPath = Path.Combine(s.Root, "gone");
            Assert.Empty(Scan(s));
        }
    }

    public class StabilityTests
    {
        private static readonly TimeSpan Sixty = TimeSpan.FromSeconds(60);

        [Fact]
        public void AnAlbumIsFinishedOnlyAfterItsFilesStayUnchangedForTheStableTime()
        {
            using var s = new InboxSandbox();
            s.AddTrack(@"Albums\A\B", "01.mp3", "Brat", 2);
            var ops = new RealFileOps();
            var tracker = new StabilityTracker();
            var t0 = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

            Assert.False(tracker.IsFinished(Assert.Single(new InboxScanner().Scan(s.Lane, ops)), t0, Sixty, ops));
            Assert.False(tracker.IsFinished(Assert.Single(new InboxScanner().Scan(s.Lane, ops)), t0.AddSeconds(30), Sixty, ops));
            Assert.True(tracker.IsFinished(Assert.Single(new InboxScanner().Scan(s.Lane, ops)), t0.AddSeconds(61), Sixty, ops));
        }

        [Fact]
        public void AChangeStartsTheWaitAgain()
        {
            using var s = new InboxSandbox();
            s.AddTrack(@"Albums\A\B", "01.mp3", "Brat", 2);
            var ops = new RealFileOps();
            var tracker = new StabilityTracker();
            var t0 = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
            tracker.IsFinished(Assert.Single(new InboxScanner().Scan(s.Lane, ops)), t0, Sixty, ops);

            s.AddTrack(@"Albums\A\B", "02.mp3", "86", 7); // another track arrives at 50 seconds
            Assert.False(tracker.IsFinished(Assert.Single(new InboxScanner().Scan(s.Lane, ops)), t0.AddSeconds(50), Sixty, ops));
            Assert.False(tracker.IsFinished(Assert.Single(new InboxScanner().Scan(s.Lane, ops)), t0.AddSeconds(100), Sixty, ops)); // only 50 s since the change
            Assert.True(tracker.IsFinished(Assert.Single(new InboxScanner().Scan(s.Lane, ops)), t0.AddSeconds(111), Sixty, ops));
        }

        [Fact]
        public void ATemporaryFileHoldsTheAlbumBackHoweverLongItIsLeftAlone()
        {
            using var s = new InboxSandbox();
            s.AddTrack(@"Albums\A\B", "01.mp3", "Brat", 2);
            s.AddFile(@"Albums\A\B", "02.mp3.part");
            var ops = new RealFileOps();
            var tracker = new StabilityTracker();
            var t0 = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
            tracker.IsFinished(Assert.Single(new InboxScanner().Scan(s.Lane, ops)), t0, Sixty, ops);
            Assert.False(tracker.IsFinished(Assert.Single(new InboxScanner().Scan(s.Lane, ops)), t0.AddHours(1), Sixty, ops));
        }

        [Fact]
        public void AFileThatAnotherProgramHasOpenHoldsTheAlbumBack()
        {
            using var s = new InboxSandbox();
            var path = s.AddTrack(@"Albums\A\B", "01.mp3", "Brat", 2);
            var ops = new RealFileOps();
            var tracker = new StabilityTracker();
            var t0 = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
            tracker.IsFinished(Assert.Single(new InboxScanner().Scan(s.Lane, ops)), t0, Sixty, ops);

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) // another program reading or writing it
            {
                Assert.False(tracker.IsFinished(Assert.Single(new InboxScanner().Scan(s.Lane, ops)), t0.AddMinutes(5), Sixty, ops));
            }
            Assert.True(tracker.IsFinished(Assert.Single(new InboxScanner().Scan(s.Lane, ops)), t0.AddMinutes(5), Sixty, ops));
        }
    }

    public class QueueStoreTests
    {
        [Fact]
        public void ItemsSurviveARestartAndTheVersionGrowsWithEveryChange()
        {
            using var s = new InboxSandbox();
            var store = new QueueStore(s.QueuePath);
            var item = new QueueItem { Id = "abc", LaneId = "main", State = QueueState.NeedsReview, Reason = "Several editions", FirstSeenUtc = DateTime.UtcNow };
            item.Match = new MatchSummary { BestTitle = "Insomniac", Score = 0.95, Candidates = { new CandidateSummary { ReleaseId = "r1", Title = "Insomniac" } } };
            store.Save(item);
            store.Save(item);

            var reloaded = new QueueStore(s.QueuePath).Get("abc");

            Assert.NotNull(reloaded);
            Assert.Equal(2, reloaded!.Version);
            Assert.Equal("Several editions", reloaded.Reason);
            Assert.Equal("Insomniac", reloaded.Match!.Candidates.Single().Title);
        }

        [Fact]
        public void AFileThatCannotBeReadIsSetAsideAndTheQueueStartsEmpty()
        {
            using var s = new InboxSandbox();
            Directory.CreateDirectory(Path.GetDirectoryName(s.QueuePath)!);
            File.WriteAllText(s.QueuePath, "{ this is not json");

            var store = new QueueStore(s.QueuePath);

            Assert.Empty(store.All());
            Assert.True(File.Exists(s.QueuePath + ".corrupt"));
        }
    }

    public class AlbumProcessorTests
    {
        private static ReleaseInfo Insomniac() =>
            MusicBrainzClient.ParseRelease(File.ReadAllText(TestFiles.Fixture("mb-release-insomniac-2021.json")));

        private static FakeMusicBrainz Searchable() => new FakeMusicBrainz
        {
            Groups = new[] { new ReleaseGroupHit { Id = "g1", Title = "Insomniac", ArtistCredit = "Green Day", PrimaryType = "Album", Score = 100 } },
            Browse = new[] { Insomniac() },
        };

        private static AlbumProcessor Processor(InboxSandbox s, IMusicBrainz mb, IFileOps? ops = null) =>
            new AlbumProcessor(mb, ops ?? new RealFileOps(), new JournalStore(s.Journals), new MatchSettings(), new PlannerOptions(), new TagWriteOptions());

        private static AlbumUnit TheUnit(InboxSandbox s) => Assert.Single(new InboxScanner().Scan(s.Lane, new RealFileOps()));

        private static void TwoInsomniacTracks(InboxSandbox s)
        {
            s.AddTrack(@"Albums\Green Day\Insomniac", "02 - Brat.mp3", "Brat", 2);
            s.AddTrack(@"Albums\Green Day\Insomniac", "07 - 86.mp3", "86", 7);
        }

        [Fact]
        public async Task ACertainMatchInADryRunIsReadyAndNothingMoves()
        {
            using var s = new InboxSandbox();
            TwoInsomniacTracks(s);

            var outcome = await Processor(s, Searchable()).ProcessAsync(s.Lane, TheUnit(s), dryRun: true, null, CancellationToken.None);

            Assert.Equal(QueueState.Ready, outcome.State);
            Assert.Contains("would tag and move 2 file(s)", outcome.Reason);
            Assert.Equal(@"Green Day\Insomniac", outcome.Destination);
            Assert.Empty(Directory.EnumerateFiles(s.Library, "*", SearchOption.AllDirectories));
            Assert.Equal(2, Directory.EnumerateFiles(s.Inbox, "*.mp3", SearchOption.AllDirectories).Count());
        }

        [Fact]
        public async Task ACertainMatchIsTaggedAndMovedWhenDryRunIsOff()
        {
            using var s = new InboxSandbox();
            TwoInsomniacTracks(s);
            var stages = new List<string>();

            var outcome = await Processor(s, Searchable()).ProcessAsync(s.Lane, TheUnit(s), dryRun: false, stages.Add, CancellationToken.None);

            Assert.Equal(QueueState.Done, outcome.State);
            Assert.Equal(new[] { QueueState.Applying }, stages);
            Assert.True(File.Exists(Path.Combine(s.Library, "Green Day", "Insomniac", "1-02 Brat.mp3")));
            Assert.Empty(Directory.EnumerateFiles(s.Inbox, "*.mp3", SearchOption.AllDirectories));
        }

        [Fact]
        public async Task ALaneSetToAlwaysAskNeverAppliesByItself()
        {
            using var s = new InboxSandbox();
            TwoInsomniacTracks(s);
            s.Lane.AutoApply = false;

            var outcome = await Processor(s, Searchable()).ProcessAsync(s.Lane, TheUnit(s), dryRun: false, null, CancellationToken.None);

            Assert.Equal(QueueState.NeedsReview, outcome.State);
            Assert.Contains("always ask", outcome.Reason);
            Assert.Empty(Directory.EnumerateFiles(s.Library, "*", SearchOption.AllDirectories));
        }

        [Fact]
        public async Task NothingFoundIsNoMatchAndTheAlbumStaysPut()
        {
            using var s = new InboxSandbox();
            TwoInsomniacTracks(s);

            var outcome = await Processor(s, new FakeMusicBrainz()).ProcessAsync(s.Lane, TheUnit(s), dryRun: false, null, CancellationToken.None);

            Assert.Equal(QueueState.NoMatch, outcome.State);
            Assert.Equal(2, Directory.EnumerateFiles(s.Inbox, "*.mp3", SearchOption.AllDirectories).Count());
        }

        [Fact]
        public async Task MusicBrainzBeingBusyMeansWaitAndRetryNotNoMatch()
        {
            using var s = new InboxSandbox();
            TwoInsomniacTracks(s);

            var outcome = await Processor(s, new UnavailableMusicBrainz()).ProcessAsync(s.Lane, TheUnit(s), dryRun: false, null, CancellationToken.None);

            Assert.Equal(QueueState.Waiting, outcome.State);
            Assert.True(outcome.Retry);
        }

        [Fact]
        public async Task AFileThatIsNotMusicFailsTheAlbumPlainly()
        {
            using var s = new InboxSandbox();
            TwoInsomniacTracks(s);
            File.WriteAllText(Path.Combine(s.Inbox, "Albums", "Green Day", "Insomniac", "09 - Broken.mp3"), "this is not audio at all");

            var outcome = await Processor(s, Searchable()).ProcessAsync(s.Lane, TheUnit(s), dryRun: false, null, CancellationToken.None);

            Assert.Equal(QueueState.Failed, outcome.State);
            Assert.Contains("1 file(s) could not be read", outcome.Reason);
            Assert.Contains("09 - Broken.mp3", outcome.Reason);
        }

        [Fact]
        public async Task AFolderWithTwoDifferentAlbumsGoesToReview()
        {
            using var s = new InboxSandbox();
            s.AddTrack(@"Albums\Mixed", "01.mp3", "Brat", 2, album: "Insomniac");
            s.AddTrack(@"Albums\Mixed", "02.mp3", "Some Song", 1, album: "Dookie");

            var outcome = await Processor(s, Searchable()).ProcessAsync(s.Lane, TheUnit(s), dryRun: false, null, CancellationToken.None);

            Assert.Equal(QueueState.NeedsReview, outcome.State);
            Assert.Contains("2 different albums", outcome.Reason);
        }

        [Fact]
        public async Task AMissingLibraryFolderFailsPlainlyBeforeAnythingIsTouched()
        {
            using var s = new InboxSandbox();
            TwoInsomniacTracks(s);
            s.Lane.LibraryPath = Path.Combine(s.Root, "no-such-share");

            var outcome = await Processor(s, Searchable()).ProcessAsync(s.Lane, TheUnit(s), dryRun: false, null, CancellationToken.None);

            Assert.Equal(QueueState.Failed, outcome.State);
            Assert.Contains("library folder", outcome.Reason);
            Assert.Equal(2, Directory.EnumerateFiles(s.Inbox, "*.mp3", SearchOption.AllDirectories).Count());
        }

        [Fact]
        public async Task AnAlbumAlreadyInTheLibraryIsADuplicateAndNothingIsOverwritten()
        {
            using var s = new InboxSandbox();
            TwoInsomniacTracks(s);
            var dest = Path.Combine(s.Library, "Green Day", "Insomniac");
            Directory.CreateDirectory(dest);
            File.WriteAllText(Path.Combine(dest, "1-02 Brat.mp3"), "already here");

            var outcome = await Processor(s, Searchable()).ProcessAsync(s.Lane, TheUnit(s), dryRun: false, null, CancellationToken.None);

            Assert.Equal(QueueState.Duplicate, outcome.State);
            Assert.Equal("already here", File.ReadAllText(Path.Combine(dest, "1-02 Brat.mp3")));
        }

        [Fact]
        public async Task ALooseSingleIsMatchedAndItsFoldersCoverIsNotTakenAlong()
        {
            using var s = new InboxSandbox();
            s.AddTrack(@"Singles\Green Day", "Brat.mp3", "Brat", 2);
            s.AddTrack(@"Singles\Green Day", "Other Song.mp3", "Other Song", 1, album: "Another Album"); // a second single stays behind
            s.AddFile(@"Singles\Green Day", "cover.jpg", "belongs to other tracks");

            var unit = new InboxScanner().Scan(s.Lane, new RealFileOps()).Single(u => u.Files[0].EndsWith("Brat.mp3"));
            var outcome = await Processor(s, Searchable()).ProcessAsync(s.Lane, unit, dryRun: false, null, CancellationToken.None);

            Assert.Equal(UnitKind.Single, unit.Kind);
            Assert.Equal(QueueState.Done, outcome.State);
            Assert.True(File.Exists(Path.Combine(s.Library, "Green Day", "Insomniac", "1-02 Brat.mp3")));
            Assert.False(File.Exists(Path.Combine(s.Library, "Green Day", "Insomniac", "cover.jpg")));
            Assert.True(File.Exists(Path.Combine(s.Inbox, "Singles", "Green Day", "cover.jpg")));
        }
    }

    public class InboxWorkerTests
    {
        private static readonly DateTime Start = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

        private static ReleaseInfo Insomniac() =>
            MusicBrainzClient.ParseRelease(File.ReadAllText(TestFiles.Fixture("mb-release-insomniac-2021.json")));

        private static FakeMusicBrainz Searchable() => new FakeMusicBrainz
        {
            Groups = new[] { new ReleaseGroupHit { Id = "g1", Title = "Insomniac", ArtistCredit = "Green Day", PrimaryType = "Album", Score = 100 } },
            Browse = new[] { Insomniac() },
        };

        private sealed class Clock
        {
            public DateTime Now = Start;
            public DateTime Get() => Now;
        }

        private static InboxWorker NewWorker(InboxSandbox s, IMusicBrainz mb, Clock clock, bool dryRun, IFileOps? ops = null, QueueStore? store = null)
        {
            ops ??= new RealFileOps();
            store ??= new QueueStore(s.QueuePath);
            var processor = new AlbumProcessor(mb, ops, new JournalStore(s.Journals), new MatchSettings(), new PlannerOptions(), new TagWriteOptions());
            return new InboxWorker(() => new[] { s.Lane }, ops, store, processor, new WorkerOptions { DryRun = dryRun }, clock.Get);
        }

        private static void TwoInsomniacTracks(InboxSandbox s)
        {
            s.AddTrack(@"Albums\Green Day\Insomniac", "02 - Brat.mp3", "Brat", 2);
            s.AddTrack(@"Albums\Green Day\Insomniac", "07 - 86.mp3", "86", 7);
        }

        [Fact]
        public async Task AnAlbumIsQueuedAtFirstAndHandledOnlyOnceItsFilesHaveStoppedChanging()
        {
            using var s = new InboxSandbox();
            TwoInsomniacTracks(s);
            var clock = new Clock();
            var worker = NewWorker(s, Searchable(), clock, dryRun: true);

            var first = await worker.RunPassAsync(CancellationToken.None);
            Assert.Equal(1, first.Queued);
            Assert.Equal(0, first.Processed);
            Assert.Equal(QueueState.Waiting, new QueueStore(s.QueuePath).All().Single().State);

            clock.Now = Start.AddSeconds(30);
            Assert.Equal(0, (await worker.RunPassAsync(CancellationToken.None)).Processed);

            clock.Now = Start.AddSeconds(61);
            var third = await worker.RunPassAsync(CancellationToken.None);
            Assert.Equal(1, third.Processed);
            var item = new QueueStore(s.QueuePath).All().Single();
            Assert.Equal(QueueState.Ready, item.State);
            Assert.Equal("Insomniac", item.Match!.BestTitle);
            Assert.Equal(@"Green Day\Insomniac", item.Destination);
        }

        [Fact]
        public async Task WithDryRunOffTheAlbumIsAppliedOnceAndNeverHandledAgain()
        {
            using var s = new InboxSandbox();
            TwoInsomniacTracks(s);
            var clock = new Clock();
            var mb = Searchable();
            var worker = NewWorker(s, mb, clock, dryRun: false);

            await worker.RunPassAsync(CancellationToken.None);
            clock.Now = Start.AddSeconds(61);
            await worker.RunPassAsync(CancellationToken.None);

            Assert.Equal(QueueState.Done, new QueueStore(s.QueuePath).All().Single().State);
            Assert.True(File.Exists(Path.Combine(s.Library, "Green Day", "Insomniac", "1-02 Brat.mp3")));
            int searches = mb.SearchCalls;

            clock.Now = Start.AddMinutes(10);
            var again = await worker.RunPassAsync(CancellationToken.None);
            Assert.Equal(0, again.Processed);
            Assert.Equal(searches, mb.SearchCalls);
        }

        [Fact]
        public async Task NewFilesInAFolderThatWasWaitingForReviewStartItOver()
        {
            using var s = new InboxSandbox();
            s.AddTrack(@"Albums\X\Y", "01.mp3", "Totally Unknown Song", 1, album: "Unknown Album");
            var clock = new Clock();
            var worker = NewWorker(s, new FakeMusicBrainz(), clock, dryRun: true);
            await worker.RunPassAsync(CancellationToken.None);
            clock.Now = Start.AddSeconds(61);
            await worker.RunPassAsync(CancellationToken.None);
            Assert.Equal(QueueState.NoMatch, new QueueStore(s.QueuePath).All().Single().State);

            s.AddTrack(@"Albums\X\Y", "02.mp3", "Another Unknown Song", 2, album: "Unknown Album"); // a new track arrives
            clock.Now = Start.AddSeconds(70);
            await worker.RunPassAsync(CancellationToken.None);

            var item = new QueueStore(s.QueuePath).All().Single();
            Assert.Equal(QueueState.Waiting, item.State);
            Assert.Equal(2, item.FileCount);
        }

        [Fact]
        public async Task WhenMusicBrainzIsBusyTheAlbumWaitsWithGrowingDelaysAndThenGoesThrough()
        {
            using var s = new InboxSandbox();
            TwoInsomniacTracks(s);
            var clock = new Clock();
            var mb = new UnavailableMusicBrainz { Working = Searchable() };
            var worker = NewWorker(s, mb, clock, dryRun: true);

            await worker.RunPassAsync(CancellationToken.None);
            clock.Now = Start.AddSeconds(61);
            await worker.RunPassAsync(CancellationToken.None); // first try: busy
            var item = new QueueStore(s.QueuePath).All().Single();
            Assert.Equal(QueueState.Waiting, item.State);
            Assert.Equal(1, item.Attempts);
            Assert.Equal(clock.Now + TimeSpan.FromMinutes(1), item.NextAttemptUtc);

            clock.Now += TimeSpan.FromSeconds(30); // too early: not tried
            int callsBefore = mb.Calls;
            await worker.RunPassAsync(CancellationToken.None);
            Assert.Equal(callsBefore, mb.Calls);

            clock.Now += TimeSpan.FromMinutes(1); // due again: busy again, the wait grows to 5 minutes
            await worker.RunPassAsync(CancellationToken.None);
            item = new QueueStore(s.QueuePath).All().Single();
            Assert.Equal(2, item.Attempts);
            Assert.Equal(clock.Now + TimeSpan.FromMinutes(5), item.NextAttemptUtc);

            mb.Down = false; // MusicBrainz is back
            clock.Now += TimeSpan.FromMinutes(6);
            await worker.RunPassAsync(CancellationToken.None);
            item = new QueueStore(s.QueuePath).All().Single();
            Assert.Equal(QueueState.Ready, item.State);
            Assert.Equal(0, item.Attempts);
        }

        [Fact]
        public async Task AnAlbumRemovedFromTheInboxByHandIsMarkedSkipped()
        {
            using var s = new InboxSandbox();
            s.AddTrack(@"Albums\X\Y", "01.mp3", "Brat", 2);
            var clock = new Clock();
            var worker = NewWorker(s, Searchable(), clock, dryRun: true);
            await worker.RunPassAsync(CancellationToken.None);

            Directory.Delete(Path.Combine(s.Inbox, "Albums"), recursive: true);
            clock.Now = Start.AddSeconds(5);
            var pass = await worker.RunPassAsync(CancellationToken.None);

            Assert.Equal(1, pass.Gone);
            var item = new QueueStore(s.QueuePath).All().Single();
            Assert.Equal(QueueState.Skipped, item.State);
        }

        [Fact]
        public async Task AnInboxThatCannotBeReachedDoesNotLoseItsAlbums()
        {
            using var s = new InboxSandbox();
            s.AddTrack(@"Albums\X\Y", "01.mp3", "Brat", 2);
            var clock = new Clock();
            var worker = NewWorker(s, Searchable(), clock, dryRun: true);
            await worker.RunPassAsync(CancellationToken.None);

            var hidden = s.Inbox + "-offline";
            Directory.Move(s.Inbox, hidden); // the share drops
            clock.Now = Start.AddSeconds(5);
            await worker.RunPassAsync(CancellationToken.None);

            Assert.Equal(QueueState.Waiting, new QueueStore(s.QueuePath).All().Single().State);
            Directory.Move(hidden, s.Inbox);
        }

        [Fact]
        public async Task AnAlbumCutOffInTheMiddleOfMatchingStartsOver()
        {
            using var s = new InboxSandbox();
            TwoInsomniacTracks(s);
            var clock = new Clock();
            var worker = NewWorker(s, Searchable(), clock, dryRun: true);
            await worker.RunPassAsync(CancellationToken.None);
            var store = new QueueStore(s.QueuePath);
            var item = store.All().Single();
            item.State = QueueState.Matching; // the process died here
            store.Save(item);

            // A new worker has no memory of how long the files have been still, so it watches them for the stable time again before handling the album.
            var restarted = NewWorker(s, Searchable(), clock, dryRun: true);
            clock.Now = Start.AddSeconds(61);
            await restarted.RunPassAsync(CancellationToken.None);
            Assert.Equal(QueueState.Waiting, new QueueStore(s.QueuePath).All().Single().State);

            clock.Now = Start.AddSeconds(130);
            await restarted.RunPassAsync(CancellationToken.None);
            Assert.Equal(QueueState.Ready, new QueueStore(s.QueuePath).All().Single().State);
        }

        [Fact]
        public async Task AnAlbumCutOffInTheMiddleOfApplyingIsFinishedFromItsJournal()
        {
            using var s = new InboxSandbox();
            TwoInsomniacTracks(s);
            var clock = new Clock();
            var store = new QueueStore(s.QueuePath);

            // A first run that dies at the second move (the journal remembers what was done).
            var faulty = new FaultyOps("move", 2);
            var firstWorker = NewWorker(s, Searchable(), clock, dryRun: false, ops: faulty, store: store);
            await firstWorker.RunPassAsync(CancellationToken.None);
            clock.Now = Start.AddSeconds(61);
            await firstWorker.RunPassAsync(CancellationToken.None);
            var item = store.All().Single();
            Assert.Equal(QueueState.Failed, item.State);
            item.State = QueueState.Applying; // as if the process had been killed rather than failing cleanly
            store.Save(item);

            // After the restart, the same album is finished without matching again.
            var mb = Searchable();
            clock.Now = Start.AddSeconds(200);
            await NewWorker(s, mb, clock, dryRun: false, store: new QueueStore(s.QueuePath)).RunPassAsync(CancellationToken.None);

            Assert.Equal(QueueState.Done, new QueueStore(s.QueuePath).All().Single().State);
            Assert.Equal(2, Directory.EnumerateFiles(s.Library, "*.mp3", SearchOption.AllDirectories).Count());
            Assert.Empty(Directory.EnumerateFiles(s.Inbox, "*.mp3", SearchOption.AllDirectories));
            Assert.Equal(0, mb.SearchCalls);
        }

        [Fact]
        public async Task AFewAlbumsAreHandledOldestFirstUpToThePassLimit()
        {
            using var s = new InboxSandbox();
            for (int i = 1; i <= 3; i++) s.AddTrack($@"Albums\Band\Album {i}", "01.mp3", "Brat", 2, album: "Unknown Album " + i);
            var clock = new Clock();
            var store = new QueueStore(s.QueuePath);
            var processor = new AlbumProcessor(new FakeMusicBrainz(), new RealFileOps(), new JournalStore(s.Journals), new MatchSettings(), new PlannerOptions(), new TagWriteOptions());
            var worker = new InboxWorker(() => new[] { s.Lane }, new RealFileOps(), store, processor, new WorkerOptions { MaxPerPass = 2 }, clock.Get);

            await worker.RunPassAsync(CancellationToken.None);
            clock.Now = Start.AddSeconds(61);
            var pass = await worker.RunPassAsync(CancellationToken.None);

            Assert.Equal(2, pass.Processed);
            Assert.Equal(1, store.All().Count(i => i.State == QueueState.Waiting));
        }
    }
}
