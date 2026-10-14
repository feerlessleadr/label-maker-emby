using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LabelMaker.Core.Inbox;
using LabelMaker.Core.Pipeline;
using Xunit;

namespace LabelMaker.Tests
{
    internal sealed class ScriptedOps : IFileOps
    {
        private readonly RealFileOps _real = new RealFileOps();
        public string? DenyWritesUnder { get; set; }
        public long Free { get; set; } = 500L * 1024 * 1024 * 1024;
        public bool Same { get; set; } = true;

        private void Check(string path)
        {
            if (DenyWritesUnder != null && path.StartsWith(DenyWritesUnder, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("denied");
        }

        public bool FileExists(string path) => _real.FileExists(path);
        public bool DirectoryExists(string path) => _real.DirectoryExists(path);
        public void CreateDirectory(string path) { Check(path); _real.CreateDirectory(path); }
        public void Move(string source, string destination) { Check(destination); _real.Move(source, destination); }
        public void Copy(string source, string destination) { Check(destination); _real.Copy(source, destination); }
        public void DeleteFile(string path) { Check(path); _real.DeleteFile(path); }
        public bool DeleteDirectoryIfEmpty(string path) => _real.DeleteDirectoryIfEmpty(path);
        public IEnumerable<string> EnumerateFiles(string directory) => _real.EnumerateFiles(directory);
        public IEnumerable<string> EnumerateDirectories(string directory) => _real.EnumerateDirectories(directory);
        public long Length(string path) => _real.Length(path);
        public string Sha256(string path) => _real.Sha256(path);
        public byte[] ReadAllBytes(string path) => _real.ReadAllBytes(path);
        public void WriteAllBytes(string path, byte[] bytes) { Check(path); _real.WriteAllBytes(path, bytes); }
        public bool SameVolume(string a, string b) => Same;
        public DateTime LastWriteUtc(string path) => _real.LastWriteUtc(path);
        public bool CanOpenForRead(string path) => _real.CanOpenForRead(path);
        public long FreeBytes(string path) => Free;
    }

    public class LaneValidatorTests
    {
        private static Lane L(string id, string inbox, string library, string? name = null) =>
            new Lane { Id = id, Name = name ?? id, InboxPath = inbox, LibraryPath = library };

        private static List<string> Codes(params Lane[] lanes) => LaneValidator.Validate(lanes).Select(i => i.LaneId + ":" + i.Code).OrderBy(c => c).ToList();

        [Fact]
        public void AFourLaneLayoutIsAccepted()
        {
            var issues = Codes(
                L("rock", @"D:\Inbox\Rock", @"\\nas\rock"),
                L("jazz", @"D:\Inbox\Jazz", @"\\nas\jazz"),
                L("folk", @"D:\Inbox\Folk", @"\\nas\folk"),
                L("blues", @"D:\Inbox\Blues", @"\\nas\blues"));
            Assert.Empty(issues);
        }

        [Fact]
        public void TwoLanesMayNotShareAnInboxEvenWhenSpelledDifferently()
        {
            var issues = Codes(L("a", @"D:\Inbox\Rock", @"\\s\a"), L("b", @"d:/inbox/rock/", @"\\s\b"));
            Assert.Equal(new[] { "a:SharedInbox", "b:SharedInbox" }, issues);
        }

        [Fact]
        public void AnInboxInsideAnotherInboxIsRefused()
        {
            var issues = Codes(L("a", @"D:\Inbox", @"\\s\a"), L("b", @"D:\Inbox\Jazz", @"\\s\b"));
            Assert.Equal(new[] { "b:InboxNesting" }, issues);
        }

        [Fact]
        public void AnInboxInsideOrHoldingALibraryIsRefused()
        {
            Assert.Contains("a:InboxIsLibrary", Codes(L("a", @"E:\Music\Inbox", @"E:\Music")));
            Assert.Contains("a:InboxIsLibrary", Codes(L("a", @"E:\Music", @"E:\Music\Library")));
            Assert.Contains("a:InboxIsLibrary", Codes(L("a", @"\\s\share", @"\\s\share")));
            // Another lane's library inside this lane's inbox would be filed into and then scanned again.
            Assert.Contains("a:LibraryInsideInbox", Codes(L("a", @"D:\Inbox", @"\\s\a"), L("b", @"D:\Other", @"D:\Inbox\Library")));
        }

        [Fact]
        public void MissingOrRelativeOrBadlyNamedThingsAreExplained()
        {
            var issues = LaneValidator.Validate(new[]
            {
                L("", "", "", name: ""),
                L("ok", @"Downloads\Music", @"\\s\ok"),
                L("bad id!", @"E:\a", @"E:\b"),
                L("dup", @"E:\c", @"E:\d"), L("DUP", @"E:\e", @"E:\f"),
            });
            var codes = issues.Select(i => i.Code).ToList();
            Assert.Contains("BadId", codes);
            Assert.Contains("NoName", codes);
            Assert.Contains("NoInbox", codes);
            Assert.Contains("NoLibrary", codes);
            Assert.Contains("NotAbsolute", codes);
            Assert.Equal(2, codes.Count(c => c == "DuplicateId"));
            Assert.Contains(issues, i => i.Code == "NotAbsolute" && i.Message.Contains("drive letter"));
            Assert.All(issues, i => Assert.False(i.Message.TrimEnd('.').EndsWith(" is") || i.Message.TrimEnd('.').EndsWith(" are")));
        }

        [Fact]
        public void ALaneBusyWithAnAlbumCannotBeRemoved()
        {
            var queue = new[]
            {
                new QueueItem { Id = "1", LaneId = "main", State = QueueState.NeedsReview },
                new QueueItem { Id = "2", LaneId = "kids", State = QueueState.Applying },
            };
            Assert.Null(LaneValidator.RemovalBlockedReason("main", queue));
            Assert.Contains("1 album", LaneValidator.RemovalBlockedReason("kids", queue));
        }
    }

    public class PreflightTests
    {
        private static IEnumerable<string> Everything(string root) => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories);

        [Fact]
        public void AHealthyLaneCanRunAndLeavesNoProbeBehind()
        {
            using var s = new InboxSandbox();

            var result = Preflight.Run(s.Lane, new ScriptedOps(), new PreflightOptions { AccountName = "emby-user" });

            Assert.True(result.CanRun, string.Join(" | ", result.Checks.Where(c => c.Level == PreflightLevel.Error).Select(c => c.Message)));
            Assert.All(result.Checks.Where(c => c.Name != "Same disk"), c => Assert.Equal(PreflightLevel.Ok, c.Level));
            Assert.Empty(Everything(s.Inbox));
            Assert.Empty(Everything(s.Library));
        }

        [Fact]
        public void AMissingInboxOrLibraryIsAnErrorThatNamesTheAccount()
        {
            using var s = new InboxSandbox();
            var lane = new Lane { Id = "x", Name = "X", InboxPath = Path.Combine(s.Root, "nope-in"), LibraryPath = Path.Combine(s.Root, "nope-lib") };

            var result = Preflight.Run(lane, new ScriptedOps(), new PreflightOptions { AccountName = "emby-user" });

            Assert.False(result.CanRun);
            Assert.Contains(result.Checks, c => c.Name == "Inbox found" && c.Level == PreflightLevel.Error && c.Message.Contains("emby-user"));
            Assert.Contains(result.Checks, c => c.Name == "Library found" && c.Level == PreflightLevel.Error);
        }

        [Fact]
        public void ALibraryThatCannotBeWrittenIsAnErrorWithTheFix()
        {
            using var s = new InboxSandbox();

            var result = Preflight.Run(s.Lane, new ScriptedOps { DenyWritesUnder = s.Library }, new PreflightOptions { AccountName = "emby-user" });

            Assert.False(result.CanRun);
            var check = Assert.Single(result.Checks, c => c.Name == "Library changes");
            Assert.Equal(PreflightLevel.Error, check.Level);
            Assert.Contains("access was denied", check.Message);
            Assert.Contains("Modify permission", check.Message);
            Assert.Contains("emby-user", check.Message);
            Assert.Contains(result.Checks, c => c.Name == "Inbox changes" && c.Level == PreflightLevel.Ok);
            Assert.Empty(Everything(s.Library));
        }

        [Fact]
        public void AnInboxThatCannotBeChangedIsAnError()
        {
            using var s = new InboxSandbox();

            var result = Preflight.Run(s.Lane, new ScriptedOps { DenyWritesUnder = s.Inbox }, new PreflightOptions());

            Assert.Contains(result.Checks, c => c.Name == "Inbox changes" && c.Level == PreflightLevel.Error && c.Message.Contains("moves albums out of the inbox"));
            Assert.Empty(Everything(s.Inbox));
        }

        [Theory]
        [InlineData(50L * 1024 * 1024, PreflightLevel.Error)]
        [InlineData(500L * 1024 * 1024, PreflightLevel.Warning)]
        [InlineData(-1L, PreflightLevel.Info)]
        [InlineData(100L * 1024 * 1024 * 1024, PreflightLevel.Ok)]
        public void FreeSpaceIsJudgedByTheOptions(long free, PreflightLevel expected)
        {
            using var s = new InboxSandbox();
            var result = Preflight.Run(s.Lane, new ScriptedOps { Free = free }, new PreflightOptions());
            Assert.Equal(expected, Assert.Single(result.Checks, c => c.Name == "Free space").Level);
        }

        [Fact]
        public void ALongLibraryPathIsAWarningAndADifferentDiskIsGoodToKnow()
        {
            using var s = new InboxSandbox();

            var result = Preflight.Run(s.Lane, new ScriptedOps { Same = false }, new PreflightOptions { MaxPathLength = s.Library.Length + 50 });

            Assert.True(result.CanRun);
            Assert.Equal(PreflightLevel.Warning, Assert.Single(result.Checks, c => c.Name == "Path length").Level);
            var disk = Assert.Single(result.Checks, c => c.Name == "Same disk");
            Assert.Equal(PreflightLevel.Info, disk.Level);
            Assert.Contains("copied, checked and then deleted", disk.Message);
        }
    }
}
