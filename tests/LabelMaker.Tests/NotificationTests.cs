using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LabelMaker.Core.Notifications;
using LabelMaker.Core.Pipeline;
using Xunit;

namespace LabelMaker.Tests
{
    public class EventLogTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "LabelMaker-events-" + Guid.NewGuid().ToString("N"));
        private DateTime _now = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

        public void Dispose()
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }

        private string LogPath => Path.Combine(_folder, "events.json");
        private EventLog Open() => new EventLog(LogPath, () => _now);
        private static PendingEvent Pending(string name = EventNames.NeedsReview) => new PendingEvent(name, new Dictionary<string, object?> { ["Count"] = 3 });

        [Fact]
        public void EveryEventGetsTheNextNumberAndTheCommonFields()
        {
            var log = Open();

            var first = log.Append(Pending());
            var second = log.Append(Pending(EventNames.AlbumFailed));

            Assert.Equal(1, first.Seq);
            Assert.Equal(2, second.Seq);
            Assert.Equal("LabelMaker.NeedsReview", first.Name);
            Assert.Equal(1, first.Data.GetProperty("ApiLevel").GetInt32());
            Assert.Equal(1, first.Data.GetProperty("Seq").GetInt64());
            Assert.Equal("NeedsReview", first.Data.GetProperty("Event").GetString());
            Assert.Equal("2026-10-14T12:00:00Z", first.Data.GetProperty("ServerTimeUtc").GetString());
            Assert.True(Guid.TryParse(first.Data.GetProperty("EventId").GetString(), out _));
            Assert.Equal(3, first.Data.GetProperty("Count").GetInt32());
        }

        [Fact]
        public void NumbersAndEventsSurviveARestart()
        {
            var log = Open();
            log.Append(Pending());
            log.Append(Pending());

            var reopened = Open();
            var third = reopened.Append(Pending());

            Assert.Equal(3, third.Seq);
            var all = reopened.After(0, 100, out var truncated);
            Assert.Equal(new long[] { 1, 2, 3 }, all.Select(e => e.Seq));
            Assert.False(truncated);
        }

        [Fact]
        public void AfterReturnsOnlyWhatWasMissedOldestFirstAndHonoursTheLimit()
        {
            var log = Open();
            for (int i = 0; i < 10; i++) log.Append(Pending());

            Assert.Equal(new long[] { 8, 9, 10 }, log.After(7, 100, out _).Select(e => e.Seq));
            Assert.Equal(new long[] { 1, 2 }, log.After(0, 2, out _).Select(e => e.Seq));
            Assert.Empty(log.After(10, 100, out _));
        }

        [Fact]
        public void OnlyTheLast500AreKeepAndAnAppAskingForOlderOnesIsToldItIsTruncated()
        {
            var log = Open();
            for (int i = 0; i < 520; i++) log.Append(Pending());

            var kept = log.After(0, 500, out var truncated);

            Assert.True(truncated);
            Assert.Equal(500, kept.Count);
            Assert.Equal(21, kept[0].Seq);
            log.After(300, 10, out var recent);
            Assert.False(recent, "an app that is only a little behind is not told to start over");
            Assert.Equal(520, log.LatestSeq);
        }

        [Fact]
        public void ADamagedLogNeverMakesNumbersGoBack()
        {
            var log = Open();
            for (int i = 0; i < 5; i++) log.Append(Pending());
            File.WriteAllText(LogPath, "{ not json");

            var reopened = Open();
            var next = reopened.Append(Pending());

            Assert.Equal(6, next.Seq);
            Assert.True(File.Exists(LogPath + ".corrupt"));

            // Both files lost: the number starts from the clock, higher than any an app can remember.
            File.WriteAllText(LogPath, "{ not json");
            File.Delete(LogPath + ".seq");
            var lost = Open();
            Assert.True(lost.Append(Pending()).Seq > 1_000_000_000);
        }
    }

    public class EventAggregatorTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        private static readonly Dictionary<string, string> Lanes = new Dictionary<string, string> { ["main"] = "Main", ["kids"] = "Kids" };

        private static QueueItem Item(string id, string state, string lane = "main", string artist = "Green Day", string album = "Insomniac", string reason = "", int minutes = 0) =>
            new QueueItem { Id = id, LaneId = lane, State = state, Artist = artist, Album = album, Reason = reason, FileCount = 3, FirstSeenUtc = T0.AddMinutes(minutes), RelativeFolder = @"Albums\" + artist + @"\" + album };

        private static AggregatorInput Input(DateTime now, params QueueItem[] items) => new AggregatorInput { Now = now, Items = items, LaneNames = Lanes };

        private static List<string> Names(IEnumerable<PendingEvent> events) => events.Select(e => e.Event).ToList();

        [Fact]
        public void AFirstBigPassSendsOneSummaryNotOnePerAlbum()
        {
            var agg = new EventAggregator(new AggregatorState());
            var items = Enumerable.Range(1, 120).Select(i => Item("a" + i, i % 10 == 0 ? QueueState.NoMatch : QueueState.NeedsReview, i % 2 == 0 ? "main" : "kids", album: "Album " + i, minutes: i)).ToArray();

            var events = agg.Evaluate(Input(T0, items));

            var single = Assert.Single(events);
            Assert.Equal(EventNames.NeedsReview, single.Event);
            Assert.Equal(120, single.Fields["Count"]);
            Assert.Equal(12, single.Fields["NoMatchCount"]);
            Assert.Equal(120, single.Fields["New"]);
            var albums = (List<object>)single.Fields["Albums"]!;
            Assert.Equal(5, albums.Count);
            Assert.Equal("Album 1", ((Dictionary<string, object?>)albums[0])["Album"]);
            var lanes = (List<object>)single.Fields["Lanes"]!;
            Assert.Equal(new[] { "Kids", "Main" }, lanes.Select(l => (string)((Dictionary<string, object?>)l)["LaneName"]!));
        }

        [Fact]
        public void AnAlbumAlreadyCountedIsNotAnnouncedAgainButNewOnesAreAfterAMinute()
        {
            var agg = new EventAggregator(new AggregatorState());
            Assert.Single(agg.Evaluate(Input(T0, Item("a", QueueState.NeedsReview))));

            Assert.Empty(agg.Evaluate(Input(T0.AddMinutes(5), Item("a", QueueState.NeedsReview))));

            // A second album arrives 10 seconds after an event: held back until a minute has passed since it.
            var both = new[] { Item("a", QueueState.NeedsReview), Item("b", QueueState.NoMatch, album: "Dookie") };
            var quick = new EventAggregator(new AggregatorState());
            Assert.Single(quick.Evaluate(Input(T0, Item("a", QueueState.NeedsReview))));
            Assert.Empty(quick.Evaluate(Input(T0.AddSeconds(10), both)));
            var later = Assert.Single(quick.Evaluate(Input(T0.AddSeconds(70), both)));
            Assert.Equal(2, later.Fields["Count"]);
            Assert.Equal(1, later.Fields["New"]);
        }

        [Fact]
        public void AnAlbumThatLeavesReviewAndComesBackIsNewAgain()
        {
            var agg = new EventAggregator(new AggregatorState());
            agg.Evaluate(Input(T0, Item("a", QueueState.NeedsReview)));
            Assert.Empty(agg.Evaluate(Input(T0.AddMinutes(2), Item("a", QueueState.Done))));
            var again = Assert.Single(agg.Evaluate(Input(T0.AddMinutes(4), Item("a", QueueState.NeedsReview))));
            Assert.Equal(EventNames.NeedsReview, again.Event);
        }

        [Fact]
        public void DryRunReadyAlbumsAreAnnouncedOnceAsWouldApplyAndAppliedOnesAsApplied()
        {
            var agg = new EventAggregator(new AggregatorState());
            var ready = Item("r1", QueueState.Ready);
            var settings = new NotificationSettings { AutoApplied = true };
            var input = Input(T0, ready, Item("r2", QueueState.Ready, album: "Dookie"));
            input.Settings = settings;

            var first = Assert.Single(agg.Evaluate(input));
            Assert.Equal("WouldApply", first.Fields["Mode"]);
            Assert.Equal(2, first.Fields["Count"]);
            Assert.Empty(agg.Evaluate(input));

            var done = Input(T0.AddMinutes(10), Item("r1", QueueState.Done), Item("r2", QueueState.Done, album: "Dookie"), Item("p", QueueState.Done, album: "Chosen"));
            done.Items.Single(i => i.Id == "p").Match = new MatchSummary { Outcome = "ChosenByPerson" };
            done.Settings = settings;
            var applied = Assert.Single(agg.Evaluate(done));
            Assert.Equal("Applied", applied.Fields["Mode"]);
            Assert.Equal(2, applied.Fields["Count"]);
        }

        [Fact]
        public void AutoAppliedIsOffByDefaultButStillCountedSoSwitchingItOnDoesNotFlood()
        {
            var agg = new EventAggregator(new AggregatorState());
            var input = Input(T0, Item("r1", QueueState.Ready));
            Assert.Empty(agg.Evaluate(input));

            input.Settings = new NotificationSettings { AutoApplied = true };
            Assert.Empty(agg.Evaluate(input));
        }

        [Fact]
        public void FailuresAreSentOnePerAlbumAFewAtATime()
        {
            var agg = new EventAggregator(new AggregatorState());
            var items = Enumerable.Range(1, 5).Select(i => Item("f" + i, QueueState.Failed, album: "Bad " + i, reason: "1 file(s) could not be read (x.mp3). Fix or remove them and the album is checked again.", minutes: i)).ToArray();

            var first = agg.Evaluate(Input(T0, items));
            Assert.Equal(EventAggregator.MaxFailuresPerRun, first.Count);
            Assert.All(first, e => Assert.Equal(true, e.Fields["Retryable"]));
            var second = agg.Evaluate(Input(T0.AddMinutes(1), items));
            Assert.Equal(2, second.Count);
            Assert.Empty(agg.Evaluate(Input(T0.AddMinutes(2), items)));
        }

        [Fact]
        public void LaneProblemsAreSentOnceRepeatDailyAndEndWithARecovery()
        {
            var agg = new EventAggregator(new AggregatorState());
            var problem = new LaneProblemInfo { LaneId = "kids", LaneName = "Kids", Kind = LaneProblemKinds.LibraryUnreachable, Message = "The library folder cannot be reached." };
            AggregatorInput With(DateTime now, params LaneProblemInfo[] problems) { var i = Input(now); i.LaneProblems = problems; return i; }

            var first = Assert.Single(agg.Evaluate(With(T0, problem)));
            Assert.Equal(EventNames.LaneProblem, first.Event);
            Assert.Equal("LibraryUnreachable", first.Fields["Kind"]);
            Assert.Empty(agg.Evaluate(With(T0.AddHours(3), problem)));
            Assert.Single(agg.Evaluate(With(T0.AddHours(25), problem)));

            var recovered = Assert.Single(agg.Evaluate(With(T0.AddHours(26))));
            Assert.Equal(EventNames.LaneRecovered, recovered.Event);
            Assert.Equal("Kids", recovered.Fields["LaneName"]);
            Assert.Empty(agg.Evaluate(With(T0.AddHours(27))));
        }

        [Fact]
        public void WhilePausedOnlyLaneProblemsGoOutAndNothingIsAnnouncedLater()
        {
            var agg = new EventAggregator(new AggregatorState());
            var input = Input(T0, Item("a", QueueState.NeedsReview), Item("f", QueueState.Failed));
            input.Paused = true;
            input.LaneProblems = new[] { new LaneProblemInfo { LaneId = "main", LaneName = "Main", Kind = LaneProblemKinds.NotWritable, Message = "Cannot write." } };

            var paused = agg.Evaluate(input);
            Assert.Equal(new[] { EventNames.LaneProblem }, Names(paused));

            input.Paused = false;
            input.LaneProblems = Array.Empty<LaneProblemInfo>();
            Assert.Equal(new[] { EventNames.LaneRecovered }, Names(agg.Evaluate(input)));
        }

        [Fact]
        public void MusicBrainzTroubleIsReportedAfterFifteenMinutesWithAlbumsWaitingAndEndsWithARecovery()
        {
            var agg = new EventAggregator(new AggregatorState());
            var settings = new NotificationSettings { ServiceTrouble = true };
            AggregatorInput Make(DateTime now, DateTime? since, bool waiting)
            {
                var i = Input(now, waiting ? Item("w", QueueState.Waiting) : Item("d", QueueState.Done));
                i.ServiceTroubleSince = since; i.Settings = settings;
                return i;
            }

            Assert.Empty(agg.Evaluate(Make(T0.AddMinutes(10), T0, waiting: true)));
            Assert.Empty(agg.Evaluate(Make(T0.AddMinutes(20), T0, waiting: false)));
            var trouble = Assert.Single(agg.Evaluate(Make(T0.AddMinutes(20), T0, waiting: true)));
            Assert.Equal(EventNames.ServiceTrouble, trouble.Event);
            Assert.Equal(1, trouble.Fields["WaitingCount"]);
            Assert.Equal("2026-10-14T12:00:00Z", trouble.Fields["SinceUtc"]);
            Assert.Empty(agg.Evaluate(Make(T0.AddMinutes(30), T0, waiting: true)));
            Assert.Equal(new[] { EventNames.ServiceRecovered }, Names(agg.Evaluate(Make(T0.AddMinutes(40), null, waiting: false))));
        }

        [Fact]
        public void APassTheOwnerStartedEndsWithASummaryAndATimedPassSendsNothing()
        {
            var agg = new EventAggregator(new AggregatorState());
            var input = Input(T0, Item("a", QueueState.Done), Item("f", QueueState.Failed));
            input.Settings = new NotificationSettings { AutoApplied = false };
            input.ManualPass = new PassResult { Found = 14, Queued = 3, Processed = 5 };

            var events = agg.Evaluate(input);

            var finished = Assert.Single(events, e => e.Event == EventNames.PassFinished);
            Assert.Equal(14, finished.Fields["Found"]);
            Assert.Equal(0, finished.Fields["NeedsReview"]);
            Assert.Equal(1, finished.Fields["Failed"]);
            input.ManualPass = null;
            Assert.DoesNotContain(agg.Evaluate(input), e => e.Event == EventNames.PassFinished);
        }

        [Fact]
        public void TheMasterSwitchAndEachSwitchStopAnEventFromBeingSent()
        {
            var item = Item("a", QueueState.NeedsReview);
            var off = new EventAggregator(new AggregatorState());
            var input = Input(T0, item);
            input.Settings = new NotificationSettings { SendPhoneAlerts = false };
            Assert.Empty(off.Evaluate(input));

            var one = new EventAggregator(new AggregatorState());
            input.Settings = new NotificationSettings { NeedsReview = false };
            Assert.Empty(one.Evaluate(input));
        }

        [Fact]
        public void NoEventEverCarriesAFilePathOrShareName()
        {
            var agg = new EventAggregator(new AggregatorState());
            var item = Item("a", QueueState.Failed, reason: @"Could not write D:\Spool\Rock\Green Day\x.mp3 or \\nas\jazz\folder, sorry.");
            item.RelativeFolder = @"Albums\Green Day\Insomniac";
            item.Album = string.Empty; item.Artist = string.Empty;
            var input = Input(T0, item, Item("n", QueueState.NoMatch, reason: @"See \\nas\rock\x"));
            input.LaneProblems = new[] { new LaneProblemInfo { LaneId = "main", LaneName = "Main", Kind = LaneProblemKinds.InboxUnreachable, Message = @"Cannot reach F:\spool\music." } };

            var json = JsonSerializer.Serialize(agg.Evaluate(input).Select(e => e.Fields));

            Assert.DoesNotContain(@"D:\\", json);
            Assert.DoesNotContain("nas", json);
            Assert.DoesNotContain(@"F:\\", json);
            Assert.DoesNotContain("Spool", json);
            Assert.Contains("(a folder)", json);
            Assert.Contains("Insomniac", json); // the album name alone is fine
        }

        [Fact]
        public void TheStateSurvivesBeingSavedAndLoadedSoARestartDoesNotRepeatAlerts()
        {
            var state = new AggregatorState();
            var agg = new EventAggregator(state);
            agg.Evaluate(Input(T0, Item("a", QueueState.NeedsReview), Item("r", QueueState.Ready)));

            var copy = JsonSerializer.Deserialize<AggregatorState>(JsonSerializer.Serialize(state))!;
            var restarted = new EventAggregator(copy);
            var input = Input(T0.AddHours(1), Item("a", QueueState.NeedsReview), Item("r", QueueState.Ready));
            input.Settings = new NotificationSettings { AutoApplied = true };

            Assert.Empty(restarted.Evaluate(input));
        }
    }

    public class NotificationCatalogTests
    {
        [Fact]
        public void TheSampleFileAndTheRealCatalogDescribeTheSameEvents()
        {
            using var sample = JsonDocument.Parse(File.ReadAllText(TestFiles.RepoFile("docs", "reference", "notification-samples.json")));
            var real = JsonSerializer.SerializeToElement(NotificationCatalog.Build());
            var fromSample = sample.RootElement.GetProperty("catalog");

            Assert.Equal(fromSample.GetProperty("ApiLevel").GetInt32(), real.GetProperty("ApiLevel").GetInt32());
            var a = fromSample.GetProperty("Events").EnumerateArray().Select(Describe).ToList();
            var b = real.GetProperty("Events").EnumerateArray().Select(Describe).ToList();
            Assert.Equal(a, b);
        }

        private static string Describe(JsonElement e) =>
            e.GetProperty("Id").GetString() + "|" +
            string.Join(",", e.GetProperty("Params").EnumerateArray().Select(p => p.GetProperty("Name").GetString() + ":" + p.GetProperty("Min").GetInt32() + "-" + p.GetProperty("Max").GetInt32() + "=" + p.GetProperty("Default").GetInt32())) + "|" +
            e.GetProperty("DefaultRule").GetProperty("Enabled").GetBoolean() + "|" + e.GetProperty("OpensTab").GetString();

        [Fact]
        public void EveryPushedEventInTheSamplesIsOneTheAggregatorCanSend()
        {
            using var sample = JsonDocument.Parse(File.ReadAllText(TestFiles.RepoFile("docs", "reference", "notification-samples.json")));
            var known = new[] { "NeedsReview", "AutoApplied", "AlbumFailed", "LaneProblem", "LaneRecovered", "ServiceTrouble", "ServiceRecovered", "PassFinished" };
            foreach (var pushed in sample.RootElement.GetProperty("pushed").EnumerateObject())
            {
                Assert.Contains(pushed.Value.GetProperty("Event").GetString(), known);
                Assert.StartsWith("LabelMaker." + pushed.Value.GetProperty("Event").GetString(), pushed.Name);
            }
        }
    }
}
