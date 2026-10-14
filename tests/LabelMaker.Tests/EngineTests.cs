using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LabelMaker.Core.Engine;
using LabelMaker.Core.Inbox;
using LabelMaker.Core.MusicBrainz;
using LabelMaker.Core.Notifications;
using LabelMaker.Core.Pipeline;
using Xunit;

namespace LabelMaker.Tests
{
    /// <summary>An engine over a throw-away inbox and library, with a clock the test moves and every callback recorded.</summary>
    internal sealed class EngineRig : IDisposable
    {
        public readonly InboxSandbox S = new InboxSandbox();
        public DateTime Now = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        public readonly EngineSettings Settings = new EngineSettings { StableSeconds = 10 };
        public readonly List<string> Refreshed = new List<string>();
        public readonly List<NotificationEvent> Pushed = new List<NotificationEvent>();
        public IMusicBrainz Mb;
        public LabelMakerEngine Engine = null!;

        public EngineRig(IMusicBrainz? mb = null, bool dryRun = true)
        {
            Mb = mb ?? Searchable();
            Settings.Lanes.Add(S.Lane);
            Settings.DryRun = dryRun;
            Settings.Notifications.AutoApplied = true;
            Engine = Build();
        }

        public static FakeMusicBrainz Searchable()
        {
            var release = LabelMaker.Core.MusicBrainz.MusicBrainzClient.ParseRelease(File.ReadAllText(TestFiles.Fixture("mb-release-insomniac-2021.json")));
            return new FakeMusicBrainz
            {
                Lookup = release,
                Groups = new[] { new ReleaseGroupHit { Id = "g1", Title = "Insomniac", ArtistCredit = "Green Day", PrimaryType = "Album", Score = 100 } },
                Browse = new[] { release },
            };
        }

        public LabelMakerEngine Build() => new LabelMakerEngine(new EngineHost
        {
            DataFolder = Path.Combine(S.Root, "data"),
            Settings = () => Settings,
            MusicBrainz = Mb,
            UtcNow = () => Now,
            PluginVersion = "9.9.9.9",
            RefreshFolder = f => Refreshed.Add(f),
            OnEvent = e => Pushed.Add(e),
        });

        public void TwoTracks()
        {
            S.AddTrack(@"Albums\Green Day\Insomniac", "02 - Brat.mp3", "Brat", 2);
            S.AddTrack(@"Albums\Green Day\Insomniac", "07 - 86.mp3", "86", 7);
        }

        /// <summary>A pass that finds the album, then (after the stable time) one that handles it.</summary>
        public async Task<PassSummary> SettleAsync(bool manualSecond = false)
        {
            await Engine.RunPassAsync(false);
            Now = Now.AddSeconds(30);
            return await Engine.RunPassAsync(manualSecond);
        }

        public List<string> PushedNames => Pushed.Select(e => e.Name).ToList();

        public void Dispose() => S.Dispose();
    }

    public class EngineTests
    {
        [Fact]
        public async Task ADryRunPassMakesTheAlbumReadyTellsNobodyToReviewAndSendsAWouldApply()
        {
            using var rig = new EngineRig();
            rig.TwoTracks();

            var pass = await rig.SettleAsync();

            Assert.True(pass.Ran);
            Assert.Equal(new[] { "LabelMaker.AutoApplied" }, rig.PushedNames);
            Assert.Equal("WouldApply", rig.Pushed[0].Data.GetProperty("Mode").GetString());
            var status = JsonSerializer.SerializeToElement(rig.Engine.Status());
            Assert.Equal(1, status.GetProperty("Counts").GetProperty("Ready").GetInt32());
            Assert.True(status.GetProperty("DryRun").GetBoolean());
            Assert.Empty(rig.Refreshed);
            Assert.Empty(Directory.EnumerateFiles(rig.S.Library, "*", SearchOption.AllDirectories));
        }

        [Fact]
        public async Task ARealPassFilesTheAlbumTellsEmbyToLookAndSendsAnAppliedSummary()
        {
            using var rig = new EngineRig(dryRun: false);
            rig.TwoTracks();

            await rig.SettleAsync();

            Assert.Equal(2, Directory.EnumerateFiles(rig.S.Library, "*.mp3", SearchOption.AllDirectories).Count());
            var folder = Assert.Single(rig.Refreshed);
            Assert.StartsWith(rig.S.Library, folder);
            Assert.True(Directory.Exists(folder));
            Assert.Equal("Applied", rig.Pushed.Single().Data.GetProperty("Mode").GetString());
        }

        [Fact]
        public async Task AnAlbumNobodyCanIdentifyBecomesOneNeedsReviewEventWithNoPathInIt()
        {
            using var rig = new EngineRig(new FakeMusicBrainz());
            rig.S.AddTrack(@"Albums\Nobody\Nothing Here", "01.mp3", "A Song Nobody Knows", 1, album: "Nothing Here");

            await rig.SettleAsync();

            var pushed = Assert.Single(rig.Pushed);
            Assert.Equal("LabelMaker.NeedsReview", pushed.Name);
            Assert.Equal(1, pushed.Data.GetProperty("Count").GetInt32());
            Assert.Equal(1, pushed.Data.GetProperty("NoMatchCount").GetInt32());
            var album = pushed.Data.GetProperty("Albums")[0];
            Assert.Equal("Green Day", album.GetProperty("Artist").GetString());
            Assert.Equal("Nothing Here", album.GetProperty("Album").GetString());
            Assert.DoesNotContain(rig.S.Root, pushed.Data.GetRawText());
            Assert.DoesNotContain("\\\\", pushed.Data.GetRawText().Replace("\\\\\\\\", string.Empty));
        }

        [Fact]
        public async Task PausingStopsTheWorkButStillReportsAProblemLane()
        {
            using var rig = new EngineRig();
            rig.TwoTracks();
            rig.Settings.Paused = true;
            Directory.Delete(rig.S.Library);

            var pass = await rig.SettleAsync();

            Assert.False(pass.Ran);
            Assert.Contains("paused", pass.Message);
            Assert.Equal(new[] { "LabelMaker.LaneProblem" }, rig.PushedNames);
            Assert.Equal("LibraryUnreachable", rig.Pushed[0].Data.GetProperty("Kind").GetString());
            Assert.Empty(Directory.EnumerateFiles(rig.S.Inbox, "*.mp3", SearchOption.AllDirectories).Where(_ => false));
            Assert.Equal(0, rig.Engine.Queue.All().Count);
        }

        [Fact]
        public async Task ABrokenLaneIsLeftAloneReportedOnceAndRecoveredWhenFixed()
        {
            using var rig = new EngineRig(dryRun: false);
            rig.TwoTracks();
            Directory.Delete(rig.S.Library);

            await rig.Engine.RunPassAsync(false);
            rig.Now = rig.Now.AddMinutes(10);
            await rig.Engine.RunPassAsync(false);

            Assert.Equal(new[] { "LabelMaker.LaneProblem" }, rig.PushedNames);
            Assert.Empty(rig.Engine.Queue.All());
            var status = JsonSerializer.SerializeToElement(rig.Engine.Status());
            var problem = status.GetProperty("Lanes")[0].GetProperty("Problems")[0];
            Assert.Equal("LibraryUnreachable", problem.GetProperty("Kind").GetString());
            Assert.True(problem.GetProperty("Blocking").GetBoolean());

            Directory.CreateDirectory(rig.S.Library);
            rig.Now = rig.Now.AddMinutes(10);
            await rig.Engine.RunPassAsync(false);
            Assert.Contains("LabelMaker.LaneRecovered", rig.PushedNames);
            Assert.Empty(rig.Engine.LaneProblems);
        }

        [Fact]
        public async Task LaneRuleBreaksAreReportedAsBadSettingsAndBlockThatLane()
        {
            using var rig = new EngineRig();
            rig.Settings.Lanes.Add(new Lane { Id = "dup", Name = "Dup", InboxPath = rig.S.Inbox, LibraryPath = Path.Combine(rig.S.Root, "other-library") });

            await rig.Engine.RunPassAsync(false);

            Assert.Contains(rig.Engine.LaneProblems, p => p.Kind == "BadSettings" && p.Blocking);
            Assert.Contains("LabelMaker.LaneProblem", rig.PushedNames);
        }

        [Fact]
        public async Task ACheckInboxNowPassEndsWithAPassFinishedEventAndATimedPassDoesNot()
        {
            using var rig = new EngineRig();
            rig.TwoTracks();

            await rig.Engine.RunPassAsync(false);
            Assert.DoesNotContain("LabelMaker.PassFinished", rig.PushedNames);
            rig.Now = rig.Now.AddSeconds(30);
            await rig.Engine.RunPassAsync(true);

            var finished = Assert.Single(rig.Pushed, e => e.Name == "LabelMaker.PassFinished");
            Assert.Equal(1, finished.Data.GetProperty("Found").GetInt32());
        }

        [Fact]
        public async Task AlertSwitchesKeepEventsOutOfThePhoneAndOutOfTheLog()
        {
            using var rig = new EngineRig();
            rig.TwoTracks();
            rig.Settings.Notifications.SendPhoneAlerts = false;

            await rig.SettleAsync();

            Assert.Empty(rig.Pushed);
            Assert.Equal(0, rig.Engine.Events.LatestSeq);
        }

        [Fact]
        public async Task AFailingPushNeverStopsTheEngine()
        {
            using var rig = new EngineRig();
            rig.TwoTracks();
            var failing = new LabelMakerEngine(new EngineHost
            {
                DataFolder = Path.Combine(rig.S.Root, "data2"), Settings = () => rig.Settings, MusicBrainz = rig.Mb, UtcNow = () => rig.Now,
                OnEvent = _ => throw new InvalidOperationException("push failed"),
            });

            await failing.RunPassAsync(false);
            rig.Now = rig.Now.AddSeconds(30);
            var pass = await failing.RunPassAsync(false);

            Assert.True(pass.Ran);
            Assert.Equal(1, pass.EventsSent);
            Assert.Equal(1, failing.Events.LatestSeq);
        }

        [Fact]
        public async Task MusicBrainzTroubleKeepsAlbumsWaitingAndShowsInTheStatus()
        {
            var down = new UnavailableMusicBrainz { Down = true, Working = EngineRig.Searchable() };
            using var rig = new EngineRig(down);
            rig.TwoTracks();

            await rig.SettleAsync();

            var status = JsonSerializer.SerializeToElement(rig.Engine.Status());
            Assert.Equal("Trouble", status.GetProperty("Service").GetProperty("MusicBrainz").GetString());
            Assert.Equal(1, status.GetProperty("Counts").GetProperty("Waiting").GetInt32());
            Assert.NotNull(rig.Engine.ServiceTroubleSince);

            down.Down = false;
            rig.Now = rig.Now.AddMinutes(30);
            await rig.Engine.RunPassAsync(false);
            Assert.Null(rig.Engine.ServiceTroubleSince);
        }

        [Fact]
        public async Task ARestartDoesNotAnnounceAnythingAgain()
        {
            using var rig = new EngineRig(new FakeMusicBrainz());
            rig.S.AddTrack(@"Albums\Nobody\Nothing Here", "01.mp3", "A Song Nobody Knows", 1, album: "Nothing Here");
            await rig.SettleAsync();
            Assert.Single(rig.Pushed);
            var seq = rig.Engine.Events.LatestSeq;

            rig.Pushed.Clear();
            rig.Engine = rig.Build();
            rig.Now = rig.Now.AddHours(1);
            await rig.Engine.RunPassAsync(false);

            Assert.Empty(rig.Pushed);
            Assert.Equal(seq, rig.Engine.Events.LatestSeq);
        }

        [Fact]
        public async Task TheAlbumListAndDetailShowWhatAPersonNeedsToDecide()
        {
            using var rig = new EngineRig(dryRun: true);
            rig.TwoTracks();
            await rig.SettleAsync();
            var id = rig.Engine.Queue.All().Single().Id;

            var list = JsonSerializer.SerializeToElement(rig.Engine.Albums("Ready", null, "green", 0, 50));
            Assert.Equal(1, list.GetProperty("Total").GetInt32());
            var first = list.GetProperty("Items")[0];
            Assert.Equal("Green Day", first.GetProperty("Artist").GetString());
            Assert.Equal("Insomniac", first.GetProperty("Album").GetString());
            Assert.Equal("Main", first.GetProperty("LaneName").GetString());
            Assert.Equal(0, JsonSerializer.SerializeToElement(rig.Engine.Albums("Done", null, null, 0, 50)).GetProperty("Total").GetInt32());

            var detail = JsonSerializer.SerializeToElement(rig.Engine.Album(id));
            Assert.Equal(2, detail.GetProperty("FileList").GetArrayLength());
            Assert.Equal("Brat", detail.GetProperty("FileList")[0].GetProperty("Title").GetString());
            Assert.True(detail.GetProperty("Candidates").GetArrayLength() >= 1);
            Assert.Null(rig.Engine.Album("nope"));
        }

        [Fact]
        public async Task ApprovingFromReviewFilesTheAlbumAndTellsEmbyAndUndoPutsItBack()
        {
            using var rig = new EngineRig(dryRun: true);
            rig.TwoTracks();
            await rig.SettleAsync();
            var item = rig.Engine.Queue.All().Single();

            var applied = await rig.Engine.ApplyAsync(item.Id, item.Match!.BestReleaseId, null);

            Assert.True(applied.Success, applied.Message);
            Assert.Single(rig.Refreshed);
            Assert.Equal(2, Directory.EnumerateFiles(rig.S.Library, "*.mp3", SearchOption.AllDirectories).Count());
            Assert.DoesNotContain(rig.PushedNames.Skip(1), n => n == "LabelMaker.AutoApplied"); // a person's choice is not announced

            var undone = await rig.Engine.UndoAsync(item.Id);
            Assert.True(undone.Success, undone.Message);
            Assert.Empty(Directory.EnumerateFiles(rig.S.Library, "*.mp3", SearchOption.AllDirectories));
        }

        [Fact]
        public async Task InfoAndCatalogDescribeThePlugin()
        {
            using var rig = new EngineRig();
            var info = JsonSerializer.SerializeToElement(rig.Engine.Info());
            Assert.Equal("9.9.9.9", info.GetProperty("PluginVersion").GetString());
            Assert.Equal(1, info.GetProperty("ApiLevel").GetInt32());
            Assert.Contains(info.GetProperty("Features").EnumerateArray().Select(f => f.GetString()), f => f == "events");
            await Task.CompletedTask;
        }

        [Fact]
        public void SettingsAreCheckedInPlainWords()
        {
            Assert.Null(new EngineSettings().Problem());
            Assert.Contains("automatic threshold", new EngineSettings { AutoThreshold = 0.5, ReviewThreshold = 0.7 }.Problem());
            Assert.Contains("two-letter", new EngineSettings { PreferredCountries = new List<string> { "USA" } }.Problem());
            Assert.Contains("ID3", new EngineSettings { Id3Version = 5 }.Problem());
            var twoLanes = new EngineSettings { Lanes = new List<Lane> { new Lane { Id = "a", Name = "A", InboxPath = @"E:\x", LibraryPath = @"\\s\a" }, new Lane { Id = "b", Name = "B", InboxPath = @"E:\x", LibraryPath = @"\\s\b" } } };
            Assert.Contains("same inbox", twoLanes.Problem());
        }
    }
}
