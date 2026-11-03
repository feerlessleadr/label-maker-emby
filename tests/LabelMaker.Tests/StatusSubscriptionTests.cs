using System.Collections.Generic;
using LabelMaker.Core.Notifications;
using Xunit;

namespace LabelMaker.Tests
{
    public class StatusSubscriptionTests
    {
        private sealed class Source
        {
            public long Version = 5;
            public int Built;

            public Dictionary<string, object?> Status() { Built++; return new Dictionary<string, object?> { ["Version"] = Version, ["Counts"] = new Dictionary<string, int> { ["NeedsReview"] = 2 } }; }
        }

        [Fact]
        public void TheFirstCallSendsTheStatusRightAway()
        {
            var source = new Source();
            var sub = new StatusSubscription(0);

            var body = sub.Next(() => source.Version, source.Status, isAdministrator: true, currentTick: 0);

            Assert.NotNull(body);
            Assert.Equal(5L, body!["Version"]);
            Assert.Equal(1, source.Built);
        }

        [Fact]
        public void NothingIsSentWhileTheVersionAndTheHeartbeatStayTheSameAndNoStatusIsBuilt()
        {
            var source = new Source();
            var sub = new StatusSubscription(0);
            sub.Next(() => source.Version, source.Status, true, 0);

            for (int i = 0; i < 5; i++) Assert.Null(sub.Next(() => source.Version, source.Status, true, 0));

            Assert.Equal(1, source.Built); // the cheap version check is all that ran
        }

        [Fact]
        public void AChangedVersionSendsTheNewStatusOnce()
        {
            var source = new Source();
            var sub = new StatusSubscription(0);
            sub.Next(() => source.Version, source.Status, true, 0);

            source.Version = 6;
            var changed = sub.Next(() => source.Version, source.Status, true, 0);
            var again = sub.Next(() => source.Version, source.Status, true, 0);

            Assert.Equal(6L, changed!["Version"]);
            Assert.Null(again);
        }

        [Fact]
        public void EachHeartbeatTickSendsTheStatusEvenWhenNothingChanged()
        {
            var source = new Source();
            var sub = new StatusSubscription(0);
            sub.Next(() => source.Version, source.Status, true, 0);

            Assert.NotNull(sub.Next(() => source.Version, source.Status, true, 1));
            Assert.Null(sub.Next(() => source.Version, source.Status, true, 1));
            Assert.NotNull(sub.Next(() => source.Version, source.Status, true, 2));
        }

        [Fact]
        public void ANonAdministratorGetsTheErrorOnceAndNeverTheStatus()
        {
            var source = new Source();
            var sub = new StatusSubscription(0);

            var first = sub.Next(() => source.Version, source.Status, isAdministrator: false, currentTick: 0);
            var second = sub.Next(() => source.Version, source.Status, isAdministrator: false, currentTick: 1);

            Assert.Equal("Administrator login required.", first!["Error"]);
            Assert.False(first.ContainsKey("Counts"));
            Assert.Null(second);
            Assert.Equal(0, source.Built);
        }

        [Theory]
        [InlineData("abc123", "abc123")]
        [InlineData("  abc123  ", "abc123")]
        [InlineData("", null)]
        [InlineData(" ", null)]
        public void TheTokenIsTheFirstValueAfterEmbysOwnTwo(string value, string? expected)
        {
            Assert.Equal(expected, StatusSubscription.ParseStart(new[] { value }));
            Assert.Null(StatusSubscription.ParseStart(new string[0]));
        }
    }
}
