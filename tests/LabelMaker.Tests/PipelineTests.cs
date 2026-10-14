using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LabelMaker.Core.Matching;
using LabelMaker.Core.Models;
using LabelMaker.Core.MusicBrainz;
using LabelMaker.Core.Pipeline;
using LabelMaker.Core.Tags;
using Xunit;

namespace LabelMaker.Tests
{
    /// <summary>A throw-away inbox, library and journal folder on the real disk, with MusicGrabber-style album files in the inbox.</summary>
    internal sealed class Scratch : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LabelMaker-pipeline-" + Guid.NewGuid().ToString("N"));
        public string Inbox => Path.Combine(Root, "inbox");
        public string Library => Path.Combine(Root, "library");
        public string JournalFolder => Path.Combine(Root, "journals");
        public string AlbumFolder => Path.Combine(Inbox, "Albums", "Green Day", "Insomniac");

        public Scratch()
        {
            Directory.CreateDirectory(Inbox);
            Directory.CreateDirectory(Library);
        }

        /// <summary>Copies the silent MP3 in under a MusicGrabber-style name and tags it the way MusicGrabber does (no MusicBrainz ids).</summary>
        public string AddTrack(string fileName, string title, int track, int disc, bool withLrc = false)
        {
            Directory.CreateDirectory(AlbumFolder);
            var path = Path.Combine(AlbumFolder, fileName);
            File.Copy(TestFiles.Fixture("silence.mp3"), path, overwrite: true);
            TrackTagFile.Write(path, new TrackTags
            {
                Title = title,
                Artist = "Green Day",
                AlbumArtist = "Green Day",
                Album = "Insomniac",
                TrackNumber = track,
                TotalTracks = 14,
                DiscNumber = disc,
                TotalDiscs = 2,
                Year = 2021,
            }, new TagWriteOptions());
            if (withLrc) File.WriteAllText(Path.ChangeExtension(path, ".lrc"), "[00:01.00] la la");
            return path;
        }

        public void AddHousekeeping()
        {
            Directory.CreateDirectory(AlbumFolder);
            File.WriteAllBytes(Path.Combine(AlbumFolder, "cover.jpg"), new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(Path.Combine(AlbumFolder, "folder.jpg"), new byte[] { 1, 2, 3, 4 });
            File.WriteAllText(Path.Combine(AlbumFolder, ".albuminfo"), "{\"artist\":\"Green Day\",\"album\":\"Insomniac\",\"release_mbid\":\"46b2741f-10f2-4c3e-9f3d-2c55abfa6f51\",\"year\":\"2021\"}");
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

    /// <summary>Real file operations that fail on the Nth call of one kind: a stand-in for a disk full, a share dropping or a killed process.</summary>
    internal sealed class FaultyOps : IFileOps
    {
        private readonly RealFileOps _real = new RealFileOps();
        private readonly string _kind;
        private int _remaining;

        public FaultyOps(string kind, int failOnCall)
        {
            _kind = kind;
            _remaining = failOnCall;
        }

        public bool Armed { get; set; } = true;

        private void Maybe(string kind)
        {
            if (!Armed || kind != _kind) return;
            if (--_remaining == 0) throw new IOException("Injected failure during " + kind);
        }

        public bool FileExists(string path) => _real.FileExists(path);
        public bool DirectoryExists(string path) => _real.DirectoryExists(path);
        public void CreateDirectory(string path) => _real.CreateDirectory(path);
        public void Move(string source, string destination) { Maybe("move"); _real.Move(source, destination); }
        public void Copy(string source, string destination) { Maybe("copy"); _real.Copy(source, destination); }
        public void DeleteFile(string path) { Maybe("delete"); _real.DeleteFile(path); }
        public bool DeleteDirectoryIfEmpty(string path) => _real.DeleteDirectoryIfEmpty(path);
        public IEnumerable<string> EnumerateFiles(string directory) => _real.EnumerateFiles(directory);
        public IEnumerable<string> EnumerateDirectories(string directory) => _real.EnumerateDirectories(directory);
        public long Length(string path) => _real.Length(path);
        public string Sha256(string path) => _real.Sha256(path);
        public byte[] ReadAllBytes(string path) => _real.ReadAllBytes(path);
        public void WriteAllBytes(string path, byte[] bytes) => _real.WriteAllBytes(path, bytes);
        public bool SameVolume(string a, string b) => _real.SameVolume(a, b);
        public DateTime LastWriteUtc(string path) => _real.LastWriteUtc(path);
        public bool CanOpenForRead(string path) => _real.CanOpenForRead(path);
    }

    public class PipelineTests
    {
        private static ReleaseInfo Insomniac() =>
            MusicBrainzClient.ParseRelease(File.ReadAllText(TestFiles.Fixture("mb-release-insomniac-2021.json")));

        private static List<AlbumFile> ReadAlbum(Scratch s) =>
            Directory.EnumerateFiles(s.AlbumFolder, "*.mp3").OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => new AlbumFile(f, TrackTagFile.Read(f))).ToList();

        private static AlbumPlan MakePlan(Scratch s, IFileOps? ops = null, PlannerOptions? options = null)
        {
            ops ??= new RealFileOps();
            var match = ReleaseVerifier.Verify(Insomniac(), ReadAlbum(s));
            Assert.Equal(0, match.WeakFiles);
            return Planner.Plan("album-1", s.Inbox, s.AlbumFolder, s.Library, match, ops, options ?? new PlannerOptions());
        }

        private static Scratch ThreeTrackAlbum()
        {
            var s = new Scratch();
            s.AddTrack("01-01 - Armatage Shanks.mp3", "Armatage Shanks", 1, 1, withLrc: true);
            s.AddTrack("02-01 - Armatage Shanks.mp3", "Armatage Shanks", 1, 2);
            s.AddTrack("01-07 - 86.mp3", "86", 7, 1);
            s.AddHousekeeping();
            return s;
        }

        private static Applier NewApplier(Scratch s, IFileOps? ops = null, bool forceCopy = false) =>
            new Applier(ops ?? new RealFileOps(), new JournalStore(s.JournalFolder), new TagWriteOptions(), forceCopy);

        private static IEnumerable<string> RelativeFiles(string root) =>
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal);

        // ---- planning ----------------------------------------------------------------------------------------

        [Fact]
        public void ThePlanUsesTheOwnersPicardNamesWithTheDiscInTheFileName()
        {
            using var s = ThreeTrackAlbum();
            var plan = MakePlan(s);

            Assert.True(plan.CanApply, string.Join(" ", plan.Problems));
            var relative = plan.Files.Select(f => Path.GetRelativePath(s.Library, f.Destination).Replace('\\', '/')).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.Equal(new[]
            {
                "Green Day/Insomniac/1-01 Armatage Shanks.mp3",
                "Green Day/Insomniac/1-07 86.mp3",
                "Green Day/Insomniac/2-01 Armatage Shanks.mp3",
            }, relative);
            Assert.Equal("Green Day/Insomniac/cover.jpg", Path.GetRelativePath(s.Library, plan.AlbumSidecars.Single().Destination).Replace('\\', '/'));
            Assert.Equal("Green Day/Insomniac/1-01 Armatage Shanks.lrc",
                Path.GetRelativePath(s.Library, plan.Files.Single(f => f.Sidecars.Count > 0).Sidecars.Single().Destination).Replace('\\', '/'));
        }

        [Fact]
        public void NewTagsComeFromTheChosenRelease()
        {
            using var s = ThreeTrackAlbum();
            var file = MakePlan(s).Files.Single(f => f.NewTags.Title == "86");
            Assert.Equal("Green Day", file.NewTags.AlbumArtist);
            Assert.Equal("Insomniac", file.NewTags.Album);
            Assert.Equal(2, file.NewTags.TotalDiscs);
            Assert.Equal(14, file.NewTags.TotalTracks);
            Assert.Equal(1, file.NewTags.DiscNumber);
            Assert.Equal(7, file.NewTags.TrackNumber);
            Assert.Equal(2021, file.NewTags.Year);
            Assert.Equal("46b2741f-10f2-4c3e-9f3d-2c55abfa6f51", file.NewTags.ReleaseMbid);
        }

        [Fact]
        public void AnExistingFolderIsReusedEvenWhenTheCaseDiffers()
        {
            using var s = ThreeTrackAlbum();
            Directory.CreateDirectory(Path.Combine(s.Library, "green day"));
            var plan = MakePlan(s);
            Assert.All(plan.Files, f => Assert.StartsWith(Path.Combine(s.Library, "green day") + Path.DirectorySeparatorChar, f.Destination));
        }

        [Fact]
        public void AFileAlreadyInTheLibraryMakesTheAlbumADuplicate()
        {
            using var s = ThreeTrackAlbum();
            var dest = Path.Combine(s.Library, "Green Day", "Insomniac");
            Directory.CreateDirectory(dest);
            File.WriteAllText(Path.Combine(dest, "1-07 86.mp3"), "already here");
            var plan = MakePlan(s);
            Assert.True(plan.IsDuplicate);
            Assert.False(plan.CanApply);
        }

        [Fact]
        public void ATooLongPathIsReportedInsteadOfWritten()
        {
            using var s = ThreeTrackAlbum();
            var plan = MakePlan(s, options: new PlannerOptions { MaxPathLength = s.Library.Length + 10 });
            Assert.False(plan.CanApply);
            Assert.Contains(plan.Problems, p => p.Contains("characters long"));
        }

        [Fact]
        public void AJournalIdCannotPointOutsideItsFolder()
        {
            using var s = new Scratch();
            var store = new JournalStore(s.JournalFolder);
            Assert.Throws<ArgumentException>(() => store.Load("../escape"));
            Assert.Throws<ArgumentException>(() => store.Load("a/b"));
        }

        // ---- applying ----------------------------------------------------------------------------------------

        [Fact]
        public void DryRunChangesNothing()
        {
            using var s = ThreeTrackAlbum();
            var before = RelativeFiles(s.Root).ToList();
            var result = NewApplier(s).Apply(MakePlan(s), dryRun: true);

            Assert.Equal(ApplyState.DryRun, result.State);
            Assert.Equal(before, RelativeFiles(s.Root).ToList());
            Assert.Equal(string.Empty, TrackTagFile.Read(Path.Combine(s.AlbumFolder, "01-07 - 86.mp3")).ReleaseMbid);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)] // the copy, verify, delete path used between a local disk and a network share
        public void ApplyTagsMovesAndTidiesTheInbox(bool forceCopy)
        {
            using var s = ThreeTrackAlbum();
            var result = NewApplier(s, forceCopy: forceCopy).Apply(MakePlan(s), dryRun: false);

            Assert.Equal(ApplyState.Applied, result.State);
            Assert.Equal(new[]
            {
                "Green Day/Insomniac/1-01 Armatage Shanks.lrc",
                "Green Day/Insomniac/1-01 Armatage Shanks.mp3",
                "Green Day/Insomniac/1-07 86.mp3",
                "Green Day/Insomniac/2-01 Armatage Shanks.mp3",
                "Green Day/Insomniac/cover.jpg",
            }, RelativeFiles(s.Library).ToList());

            var moved = TrackTagFile.Read(Path.Combine(s.Library, "Green Day", "Insomniac", "2-01 Armatage Shanks.mp3"));
            Assert.Equal(2, moved.DiscNumber);
            Assert.Equal(2, moved.TotalDiscs);
            Assert.Equal("46b2741f-10f2-4c3e-9f3d-2c55abfa6f51", moved.ReleaseMbid);

            // The inbox album folder and its empty parents are gone; the lane's inbox itself stays.
            Assert.True(Directory.Exists(s.Inbox));
            Assert.Empty(Directory.EnumerateFileSystemEntries(s.Inbox));
            Assert.DoesNotContain(RelativeFiles(s.Root), f => f.EndsWith(SafeMover.TempSuffix));
        }

        [Fact]
        public void ApplyingTwiceDoesNothingTheSecondTime()
        {
            using var s = ThreeTrackAlbum();
            var applier = NewApplier(s);
            var plan = MakePlan(s);
            Assert.Equal(ApplyState.Applied, applier.Apply(plan, false).State);
            Assert.Equal("Already applied.", applier.Apply(plan, false).Message);
        }

        [Fact]
        public void ADuplicateIsRefusedAndTheFilesStayUntouched()
        {
            using var s = ThreeTrackAlbum();
            var dest = Path.Combine(s.Library, "Green Day", "Insomniac");
            Directory.CreateDirectory(dest);
            File.WriteAllText(Path.Combine(dest, "1-07 86.mp3"), "already here");
            var before = RelativeFiles(s.Inbox).ToList();

            var result = NewApplier(s).Apply(MakePlan(s), dryRun: false);

            Assert.Equal(ApplyState.Duplicate, result.State);
            Assert.Equal(before, RelativeFiles(s.Inbox).ToList());
            Assert.Equal("already here", File.ReadAllText(Path.Combine(dest, "1-07 86.mp3")));
            Assert.Equal(string.Empty, TrackTagFile.Read(Path.Combine(s.AlbumFolder, "01-07 - 86.mp3")).ReleaseMbid);
        }

        // ---- undo --------------------------------------------------------------------------------------------

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void UndoPutsFilesTagsAndSidecarsBack(bool forceCopy)
        {
            using var s = ThreeTrackAlbum();
            var inboxBefore = RelativeFiles(s.Inbox).ToList();
            var applier = NewApplier(s, forceCopy: forceCopy);
            Assert.Equal(ApplyState.Applied, applier.Apply(MakePlan(s), false).State);

            var undo = applier.Undo("album-1");

            Assert.True(undo.Success, undo.Message);
            Assert.Equal(inboxBefore, RelativeFiles(s.Inbox).ToList());
            Assert.Empty(Directory.EnumerateFileSystemEntries(s.Library)); // the folders created for the album were removed
            var back = TrackTagFile.Read(Path.Combine(s.AlbumFolder, "02-01 - Armatage Shanks.mp3"));
            Assert.Equal(string.Empty, back.ReleaseMbid);
            Assert.Equal(2, back.DiscNumber);
            Assert.Contains("release_mbid", File.ReadAllText(Path.Combine(s.AlbumFolder, ".albuminfo")));
        }

        [Fact]
        public void UndoRefusesPlainlyWhenAFileIsGoneFromTheLibrary()
        {
            using var s = ThreeTrackAlbum();
            var applier = NewApplier(s);
            applier.Apply(MakePlan(s), false);
            File.Delete(Path.Combine(s.Library, "Green Day", "Insomniac", "1-07 86.mp3"));

            var undo = applier.Undo("album-1");

            Assert.False(undo.Success);
            Assert.Contains("1-07 86.mp3", undo.Message);
        }

        // ---- crashes and failures ----------------------------------------------------------------------------

        [Theory]
        [InlineData("move", 1, false)]
        [InlineData("move", 3, false)]
        [InlineData("copy", 1, true)]
        [InlineData("copy", 2, true)]
        [InlineData("delete", 1, true)] // the source delete after a verified copy
        public void ACrashPartWayLosesNothingAndRunningAgainFinishes(string kind, int failOnCall, bool forceCopy)
        {
            using var s = ThreeTrackAlbum();
            var ops = new FaultyOps(kind, failOnCall);
            var applier = NewApplier(s, ops, forceCopy);

            var first = applier.Apply(MakePlan(s), false);
            Assert.Equal(ApplyState.Failed, first.State);

            // Nothing is lost: each audio file is in the inbox or in the library, never neither.
            foreach (var name in new[] { "Armatage Shanks", "86" })
            {
                var present = Directory.EnumerateFiles(s.Root, "*.mp3", SearchOption.AllDirectories).Count(f => TrackTagFile.Read(f).Title == name);
                Assert.True(present >= 1, $"'{name}' vanished after the failure");
            }

            ops.Armed = false;
            var second = applier.Apply(MakePlan(s), false);
            Assert.Equal(ApplyState.Applied, second.State);

            var mp3s = RelativeFiles(s.Library).Where(f => f.EndsWith(".mp3")).ToList();
            Assert.Equal(3, mp3s.Count);
            Assert.DoesNotContain(RelativeFiles(s.Root), f => f.EndsWith(SafeMover.TempSuffix));
            Assert.Empty(Directory.EnumerateFiles(s.Inbox, "*.mp3", SearchOption.AllDirectories));
        }

        [Fact]
        public void AReadOnlyFileFailsTheAlbumPlainlyAndCanBeRetriedAfterTheFix()
        {
            using var s = ThreeTrackAlbum();
            var locked = Path.Combine(s.AlbumFolder, "01-07 - 86.mp3");
            File.SetAttributes(locked, FileAttributes.ReadOnly);
            var applier = NewApplier(s);

            var first = applier.Apply(MakePlan(s), false);
            Assert.Equal(ApplyState.Failed, first.State);
            Assert.Contains("Access was denied", first.Message);
            Assert.Empty(RelativeFiles(s.Library)); // nothing reached the library

            File.SetAttributes(locked, FileAttributes.Normal);
            Assert.Equal(ApplyState.Applied, applier.Apply(MakePlan(s), false).State);
            Assert.Equal(4 + 1, RelativeFiles(s.Library).Count()); // 3 tracks, 1 lyrics file, 1 cover
        }

        [Fact]
        public void ResumeFinishesACutOffAlbumFromItsJournalAlone()
        {
            using var s = ThreeTrackAlbum();
            var ops = new FaultyOps("move", 2);
            var applier = NewApplier(s, ops);
            Assert.Equal(ApplyState.Failed, applier.Apply(MakePlan(s), false).State);

            ops.Armed = false;
            var resumed = applier.Resume("album-1");

            Assert.Equal(ApplyState.Applied, resumed.State);
            Assert.Equal(3, RelativeFiles(s.Library).Count(f => f.EndsWith(".mp3")));
            Assert.Empty(Directory.EnumerateFiles(s.Inbox, "*", SearchOption.AllDirectories));
            Assert.Equal(ApplyState.Failed, applier.Resume("no-such-album").State);
        }
    }
}
