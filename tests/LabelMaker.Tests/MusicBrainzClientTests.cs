using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LabelMaker.Core.MusicBrainz;
using Xunit;

namespace LabelMaker.Tests
{
    /// <summary>Answers requests from a queue and records the URLs asked for. No network.</summary>
    internal sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _answers = new Queue<Func<HttpResponseMessage>>();
        public List<string> Urls { get; } = new List<string>();
        public List<string> UserAgents { get; } = new List<string>();

        public ScriptedHandler Then(HttpStatusCode status, string body = "", string? retryAfterSeconds = null)
        {
            _answers.Enqueue(() =>
            {
                var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
                if (retryAfterSeconds != null) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(double.Parse(retryAfterSeconds)));
                return response;
            });
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            UserAgents.Add(request.Headers.UserAgent.ToString());
            var answer = _answers.Count > 0 ? _answers.Dequeue()() : new HttpResponseMessage(HttpStatusCode.InternalServerError);
            return Task.FromResult(answer);
        }
    }

    public class MusicBrainzClientTests
    {
        private const string Ua = "LabelMaker-tests/0.0 (feerlessleadr@users.noreply.github.com)";
        private const string Insomniac = "46b2741f-10f2-4c3e-9f3d-2c55abfa6f51";

        private static string RecordedRelease() => System.IO.File.ReadAllText(TestFiles.Fixture("mb-release-insomniac-2021.json"));

        private static MusicBrainzClient Client(ScriptedHandler handler, int maxRetries = 4) =>
            new MusicBrainzClient(handler, Ua) { MinInterval = TimeSpan.Zero, MaxRetries = maxRetries };

        [Fact]
        public void TheRecordedInsomniacReleaseParsesIntoDiscsAndTracks()
        {
            var release = MusicBrainzClient.ParseRelease(RecordedRelease());
            Assert.Equal(Insomniac, release.Id);
            Assert.Equal("Insomniac", release.Title);
            Assert.Equal("2021-03-19", release.Date);
            Assert.Equal("Green Day", release.ArtistCredit);
            Assert.Equal(2, release.Media.Count);
            Assert.Equal(14, release.Media[0].Tracks.Count);
            Assert.Equal(8, release.Media[1].Tracks.Count);
            Assert.Equal(1, release.Media[0].Tracks[0].Position);
            Assert.False(string.IsNullOrEmpty(release.Media[0].Tracks[0].Title));
            Assert.False(string.IsNullOrEmpty(release.Media[0].Tracks[0].RecordingId));
        }

        [Fact]
        public async Task LookupAsksForRecordingsAndSendsTheUserAgent()
        {
            var handler = new ScriptedHandler().Then(HttpStatusCode.OK, RecordedRelease());
            var release = await Client(handler).LookupReleaseAsync(Insomniac.ToUpperInvariant());
            Assert.NotNull(release);
            Assert.Equal(Insomniac, release!.Id);
            Assert.Single(handler.Urls);
            Assert.Contains("/release/" + Insomniac + "?inc=recordings+artist-credits+media+release-groups&fmt=json", handler.Urls[0]);
            Assert.Contains("feerlessleadr", handler.UserAgents[0]);
        }

        [Fact]
        public async Task AnInvalidIdIsRefusedWithoutAskingTheServer()
        {
            var handler = new ScriptedHandler();
            Assert.Null(await Client(handler).LookupReleaseAsync("not-a-release"));
            Assert.Empty(handler.Urls);
        }

        [Fact]
        public async Task AMissingReleaseGivesNull()
        {
            var handler = new ScriptedHandler().Then(HttpStatusCode.NotFound);
            Assert.Null(await Client(handler).LookupReleaseAsync(Insomniac));
        }

        [Fact]
        public async Task A503IsRetriedAfterTheServersRetryAfter()
        {
            var handler = new ScriptedHandler()
                .Then((HttpStatusCode)503, "busy", retryAfterSeconds: "0")
                .Then(HttpStatusCode.OK, RecordedRelease());
            var release = await Client(handler).LookupReleaseAsync(Insomniac);
            Assert.NotNull(release);
            Assert.Equal(2, handler.Urls.Count);
        }

        [Fact]
        public async Task AServerThatStaysBusyEndsWithAPlainError()
        {
            var handler = new ScriptedHandler()
                .Then((HttpStatusCode)503, "busy", "0")
                .Then((HttpStatusCode)503, "busy", "0")
                .Then((HttpStatusCode)503, "busy", "0");
            await Assert.ThrowsAsync<MusicBrainzUnavailableException>(() => Client(handler, maxRetries: 2).LookupReleaseAsync(Insomniac));
            Assert.Equal(3, handler.Urls.Count);
        }

        [Fact]
        public async Task OtherErrorsAreNotRetried()
        {
            var handler = new ScriptedHandler().Then(HttpStatusCode.BadRequest, "bad");
            await Assert.ThrowsAsync<MusicBrainzUnavailableException>(() => Client(handler).LookupReleaseAsync(Insomniac));
            Assert.Single(handler.Urls);
        }

        [Fact]
        public async Task RequestsAreSpacedByTheMinimumInterval()
        {
            var handler = new ScriptedHandler().Then(HttpStatusCode.OK, RecordedRelease()).Then(HttpStatusCode.OK, RecordedRelease());
            var client = new MusicBrainzClient(handler, Ua) { MinInterval = TimeSpan.FromMilliseconds(300) };
            var clock = Stopwatch.StartNew();
            await client.LookupReleaseAsync(Insomniac);
            await client.LookupReleaseAsync(Insomniac);
            clock.Stop();
            Assert.True(clock.ElapsedMilliseconds >= 280, $"two requests took {clock.ElapsedMilliseconds} ms");
        }

        [Theory]
        [InlineData("LabelMaker")]
        [InlineData("LabelMaker/1.0")]
        public void AUserAgentWithoutAContactIsRefused(string userAgent)
        {
            Assert.Throws<ArgumentException>(() => new MusicBrainzClient(new ScriptedHandler(), userAgent));
        }

        [Fact]
        public void AUserAgentWithAContactIsAccepted()
        {
            var client = new MusicBrainzClient(new ScriptedHandler(), "LabelMaker/1.0 (feerlessleadr@users.noreply.github.com)");
            Assert.NotNull(client);
        }
    }
}

namespace LabelMaker.Tests
{
    internal sealed class ThrowingHandler : System.Net.Http.HttpMessageHandler
    {
        protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            => throw new System.Net.Http.HttpRequestException("no network");
    }

    public class MusicBrainzOfflineTests
    {
        [Fact]
        public async System.Threading.Tasks.Task AnOfflineNetworkGivesTheRetryLaterError()
        {
            var client = new LabelMaker.Core.MusicBrainz.MusicBrainzClient(new ThrowingHandler(), "LabelMaker-tests/0.0 (feerlessleadr@users.noreply.github.com)") { MinInterval = System.TimeSpan.Zero };
            await Assert.ThrowsAsync<LabelMaker.Core.MusicBrainz.MusicBrainzUnavailableException>(() => client.LookupReleaseAsync("46b2741f-10f2-4c3e-9f3d-2c55abfa6f51"));
        }
    }
}
