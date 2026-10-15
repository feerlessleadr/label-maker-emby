using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LabelMaker.Core.Notifications;
using Xunit;

namespace LabelMaker.Tests
{
    public class EventSubscriptionTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "LabelMaker-subs-" + Guid.NewGuid().ToString("N"));
        private readonly EventLog _log;

        public EventSubscriptionTests()
        {
            _log = new EventLog(Path.Combine(_folder, "events.json"));
        }

        public void Dispose()
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }

        private void Add(int count, string name = EventNames.NeedsReview)
        {
            for (int i = 0; i < count; i++) _log.Append(new PendingEvent(name, new Dictionary<string, object?> { ["Count"] = i + 1 }));
        }

        private static List<long> Seqs(Dictionary<string, object?>? body) =>
            ((List<object>)body!["Events"]!).Select(e => (long)((Dictionary<string, object?>)e)["Seq"]!).ToList();

        [Fact]
        public void WithNoAfterSeqTheAnswerIsOnlyABaselineAndNothingOldIsReplayed()
        {
            Add(5);
            var sub = new EventSubscription(null, 0);

            var first = sub.Next(_log, true, 0)!;

            Assert.Equal(1, first["ApiLevel"]);
            Assert.Equal(5L, first["LatestSeq"]);
            Assert.Equal(false, first["Truncated"]);
            Assert.Empty(Seqs(first));
            Assert.Equal(5L, sub.LastSent);
        }

        [Fact]
        public void AfterSeqReplaysWhatTheAppMissedOldestFirstAndNothingBefore()
        {
            Add(5);
            var sub = new EventSubscription(2, 0);

            var first = sub.Next(_log, true, 0)!;

            Assert.Equal(new long[] { 3, 4, 5 }, Seqs(first));
            Assert.Equal(false, first["Truncated"]);
            Assert.Null(sub.Next(_log, true, 0));
        }

        [Fact]
        public void AfterSeqOlderThanTheLogIsReportedAsTruncated()
        {
            Add(EventLog.Capacity + 20);
            var sub = new EventSubscription(3, 0);

            var first = sub.Next(_log, true, 0)!;

            Assert.Equal(true, first["Truncated"]);
        }

        [Fact]
        public void ANewEventIsSentOnceAndNeverAgainOnTheSameConnection()
        {
            Add(2);
            var sub = new EventSubscription(null, 0);
            sub.Next(_log, true, 0);

            Add(1);
            var pushed = sub.Next(_log, true, 0)!;
            Assert.Equal(new long[] { 3 }, Seqs(pushed));
            Assert.Equal(3L, pushed["LatestSeq"]);

            Assert.Null(sub.Next(_log, true, 0));
            Assert.Null(sub.Next(_log, true, 0));
        }

        [Fact]
        public void AQuietConnectionGetsAnEmptyHeartbeatOncePerTick()
        {
            Add(1);
            var sub = new EventSubscription(null, 0);
            sub.Next(_log, true, 0);

            Assert.Null(sub.Next(_log, true, 0));
            var beat = sub.Next(_log, true, 1)!;
            Assert.Empty(Seqs(beat));
            Assert.Equal(1L, beat["LatestSeq"]);
            Assert.Null(sub.Next(_log, true, 1));
            Assert.NotNull(sub.Next(_log, true, 2));
        }

        [Fact]
        public void AnEventWinsOverAHeartbeatAndIsNotDelayedToTheNextTick()
        {
            var sub = new EventSubscription(null, 0);
            sub.Next(_log, true, 0);
            Add(1);

            var body = sub.Next(_log, true, 0)!;

            Assert.Equal(new long[] { 1 }, Seqs(body));
        }

        [Fact]
        public void ANonAdministratorGetsTheErrorOnceAndNothingElse()
        {
            Add(3);
            var sub = new EventSubscription(0, 0);

            var refused = sub.Next(_log, false, 0)!;

            Assert.Equal("Administrator login required.", refused["Error"]);
            Assert.False(refused.ContainsKey("Events"));
            Assert.Null(sub.Next(_log, false, 0));
            Assert.Null(sub.Next(_log, false, 5));
        }

        [Fact]
        public void ASubscriptionThatWasRefusedWorksOnceTheLoginIsRecognized()
        {
            Add(2);
            var sub = new EventSubscription(null, 0);
            Assert.NotNull(sub.Next(_log, false, 0));

            var later = sub.Next(_log, true, 0)!;

            Assert.Equal(2L, later["LatestSeq"]);
            Assert.False(later.ContainsKey("Error"));
        }

        [Fact]
        public void ABacklogLargerThanOneMessageGoesOutInBatchesWithoutGapsOrRepeats()
        {
            Add(250);
            var sub = new EventSubscription(0, 0);
            var seen = new List<long>();

            var first = sub.Next(_log, true, 0)!;
            seen.AddRange(Seqs(first));
            Assert.Equal(EventSubscription.BatchSize, seen.Count);
            Assert.True(sub.MoreWaiting);

            seen.AddRange(Seqs(sub.Next(_log, true, 0)));
            Assert.True(sub.MoreWaiting);
            seen.AddRange(Seqs(sub.Next(_log, true, 0)));
            Assert.False(sub.MoreWaiting);

            Assert.Equal(Enumerable.Range(1, 250).Select(i => (long)i), seen);
            Assert.Null(sub.Next(_log, true, 0));
        }

        [Fact]
        public void EachEventHasTheCatchUpShapeAndJsonThatDoesNotHoldAnyToken()
        {
            _log.Append(new PendingEvent(EventNames.AlbumFailed, new Dictionary<string, object?> { ["Artist"] = "Green Day", ["Reason"] = "A file could not be read." }));
            var sub = new EventSubscription(0, 0);

            var body = sub.Next(_log, true, 0)!;
            var json = JsonSerializer.SerializeToElement(body);
            var ev = json.GetProperty("Events")[0];

            Assert.Equal(1, ev.GetProperty("Seq").GetInt64());
            Assert.Equal("LabelMaker.AlbumFailed", ev.GetProperty("Name").GetString());
            Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$", ev.GetProperty("AtUtc").GetString());
            Assert.Equal("Green Day", ev.GetProperty("Data").GetProperty("Artist").GetString());
            Assert.Equal(1, ev.GetProperty("Data").GetProperty("Seq").GetInt64());
        }

        [Theory]
        [InlineData(new[] { "", "tok" }, null, "tok")]
        [InlineData(new[] { "7", "tok" }, 7L, "tok")]
        [InlineData(new[] { "0", "tok" }, 0L, "tok")]
        [InlineData(new[] { "abc", "tok" }, null, "tok")]
        [InlineData(new[] { "-3", "tok" }, null, "tok")]
        [InlineData(new[] { "5", "  " }, 5L, null)]
        [InlineData(new[] { "5" }, 5L, null)]
        [InlineData(new string[0], null, null)]
        [InlineData(new[] { "", " tok " }, null, "tok")]
        public void TheStartMessageValuesAreReadTolerantly(string[] values, long? after, string? token)
        {
            var parsed = EventSubscription.ParseStart(values);

            Assert.Equal(after, parsed.AfterSeq);
            Assert.Equal(token, parsed.Token);
        }
    }
}
