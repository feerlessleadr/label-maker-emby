using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using LabelMaker.Core.MusicBrainz;
using Xunit;

namespace LabelMaker.Tests
{
    public class ResponseCacheTests : IDisposable
    {
        private const string Ua = "LabelMaker-tests/0.0 (feerlessleadr@users.noreply.github.com)";
        private const string Insomniac = "46b2741f-10f2-4c3e-9f3d-2c55abfa6f51";
        private static readonly DateTime T0 = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

        private readonly string _folder = Path.Combine(Path.GetTempPath(), "LabelMaker-cache-" + Guid.NewGuid().ToString("N"));
        private DateTime _now = T0;

        public void Dispose()
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }

        private DiskResponseCache Cache() => new DiskResponseCache(_folder, () => _now);

        private const string ReleaseUrl = "https://musicbrainz.org/ws/2/release/46b2741f-10f2-4c3e-9f3d-2c55abfa6f51?inc=recordings&fmt=json";
        private const string SearchUrl = "https://musicbrainz.org/ws/2/release-group?query=x&fmt=json&limit=25";
        private const string EditionsUrl = "https://musicbrainz.org/ws/2/release?release-group=abc&status=official&fmt=json";

        [Fact]
        public void AnAnswerComesBackAfterTheCacheIsReopened()
        {
            Cache().Put(ReleaseUrl, HttpStatusCode.OK, "{\"id\":1}\nsecond line");

            Assert.True(Cache().TryGet(ReleaseUrl, out var status, out var body));
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("{\"id\":1}\nsecond line", body);
            Assert.False(Cache().TryGet(ReleaseUrl + "&other=1", out _, out _));
        }

        [Fact]
        public void EachKindOfAnswerExpiresOnItsOwnSchedule()
        {
            var cache = Cache();
            cache.Put(ReleaseUrl, HttpStatusCode.OK, "release");
            cache.Put(SearchUrl, HttpStatusCode.OK, "search");
            cache.Put(EditionsUrl, HttpStatusCode.OK, "editions");
            cache.Put(ReleaseUrl + "&missing", HttpStatusCode.NotFound, string.Empty);

            _now = T0.AddHours(25);
            Assert.False(cache.TryGet(SearchUrl, out _, out _), "a search is trusted for a day");
            Assert.False(cache.TryGet(ReleaseUrl + "&missing", out _, out _), "'not found' is rechecked daily");
            Assert.True(cache.TryGet(EditionsUrl, out _, out _));
            Assert.True(cache.TryGet(ReleaseUrl, out _, out _));

            _now = T0.AddDays(4);
            Assert.False(cache.TryGet(EditionsUrl, out _, out _), "the editions of an album are trusted for 3 days");
            Assert.True(cache.TryGet(ReleaseUrl, out _, out _));

            _now = T0.AddDays(31);
            Assert.False(cache.TryGet(ReleaseUrl, out _, out _), "a release is trusted for 30 days");
        }

        [Fact]
        public void BusyAndErrorAnswersAreNeverStored()
        {
            var cache = Cache();
            cache.Put(ReleaseUrl, (HttpStatusCode)503, "busy");
            cache.Put(SearchUrl, HttpStatusCode.InternalServerError, "oops");
            Assert.False(cache.TryGet(ReleaseUrl, out _, out _));
            Assert.False(cache.TryGet(SearchUrl, out _, out _));
        }

        [Fact]
        public void ADamagedFileCountsAsAMissAndOldFilesAreClearedOnOpening()
        {
            var cache = Cache();
            cache.Put(ReleaseUrl, HttpStatusCode.OK, "release");
            var file = Directory.GetFiles(_folder, "*.mbc").Single();
            File.WriteAllText(file, "this is not a cache file");
            Assert.False(cache.TryGet(ReleaseUrl, out _, out _));

            File.WriteAllText(file, "200|0\nbody");
            File.SetLastWriteTimeUtc(file, T0.AddDays(-40));
            _ = Cache();
            Assert.Empty(Directory.GetFiles(_folder, "*.mbc"));
        }

        [Fact]
        public void TheOldestAnswersGoFirstWhenTheCacheIsTooBig()
        {
            var cache = Cache();
            for (int i = 0; i < 5; i++)
            {
                cache.Put($"https://musicbrainz.org/ws/2/release/{i}", HttpStatusCode.OK, "r" + i);
                var newest = Directory.GetFiles(_folder, "*.mbc").OrderBy(f => File.GetLastWriteTimeUtc(f)).Last();
                File.SetLastWriteTimeUtc(newest, T0.AddMinutes(-10 + i));
            }

            var small = new DiskResponseCache(_folder, () => _now, maxEntries: 3);

            Assert.Equal(3, Directory.GetFiles(_folder, "*.mbc").Length);
            Assert.False(small.TryGet("https://musicbrainz.org/ws/2/release/0", out _, out _));
            Assert.False(small.TryGet("https://musicbrainz.org/ws/2/release/1", out _, out _));
            Assert.True(small.TryGet("https://musicbrainz.org/ws/2/release/4", out _, out _));
        }

        [Fact]
        public async Task TheClientAsksOnceAndThenAnswersFromTheCacheWithoutWaiting()
        {
            var handler = new ScriptedHandler().Then(HttpStatusCode.OK, System.IO.File.ReadAllText(TestFiles.Fixture("mb-release-insomniac-2021.json")));
            var client = new MusicBrainzClient(handler, Ua) { MinInterval = TimeSpan.FromSeconds(30), Cache = Cache() };

            var first = await client.LookupReleaseAsync(Insomniac);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var second = await client.LookupReleaseAsync(Insomniac);
            watch.Stop();

            Assert.Equal("Insomniac", first!.Title);
            Assert.Equal("Insomniac", second!.Title);
            Assert.Single(handler.Urls);
            Assert.True(watch.ElapsedMilliseconds < 2000, "a cached answer must not wait for the request pacing");
        }

        [Fact]
        public async Task ANotFoundAnswerIsRememberedButABusyOneIsNot()
        {
            var handler = new ScriptedHandler().Then(HttpStatusCode.NotFound).Then((HttpStatusCode)503).Then(HttpStatusCode.OK, "{\"release-groups\":[]}");
            var client = new MusicBrainzClient(handler, Ua) { MinInterval = TimeSpan.Zero, MaxRetries = 0, Cache = Cache() };

            Assert.Null(await client.LookupReleaseAsync(Insomniac));
            Assert.Null(await client.LookupReleaseAsync(Insomniac));
            Assert.Single(handler.Urls);

            await Assert.ThrowsAsync<MusicBrainzUnavailableException>(() => client.SearchReleaseGroupsAsync("a", "b", 5, SearchShape.PlainWords));
            var hits = await client.SearchReleaseGroupsAsync("a", "b", 5, SearchShape.PlainWords);
            Assert.Empty(hits);
            Assert.Equal(3, handler.Urls.Count);
        }
    }
}
