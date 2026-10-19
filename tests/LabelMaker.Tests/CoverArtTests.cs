using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LabelMaker.Core.CoverArt;
using LabelMaker.Core.Inbox;
using LabelMaker.Core.Matching;
using LabelMaker.Core.Models;
using LabelMaker.Core.MusicBrainz;
using LabelMaker.Core.Pipeline;
using LabelMaker.Core.Tags;
using Xunit;

namespace LabelMaker.Tests
{
    public class CoverArtTests
    {
        private const string ReleaseId = "0e2f1a2c-8a3b-4d55-9d7e-1f0b6c5a4d3e";

        private static byte[] Jpeg(int length = 4096)
        {
            var bytes = new byte[length];
            bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF; bytes[3] = 0xE0;
            return bytes;
        }

        private sealed class FakeHandler : HttpMessageHandler
        {
            public HttpStatusCode Status = HttpStatusCode.OK;
            public byte[] Body = Jpeg();
            public string? LastUrl;
            public int Calls;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
                LastUrl = request.RequestUri!.ToString();
                return Task.FromResult(new HttpResponseMessage(Status) { Content = new ByteArrayContent(Body) });
            }
        }

        private sealed class FakeCoverArt : ICoverArt
        {
            public CoverImage? Image = new CoverImage { Bytes = Jpeg(), Extension = ".jpg" };
            public int Calls;
            public string? LastRelease;

            public Task<CoverImage?> GetFrontAsync(string releaseId, CancellationToken cancellation = default)
            {
                Calls++;
                LastRelease = releaseId;
                return Task.FromResult(Image);
            }
        }

        [Fact]
        public async Task TheClientAsksForTheFrontCoverAt500AndReturnsAJpeg()
        {
            var handler = new FakeHandler();
            var image = await new CoverArtClient(handler, "LabelMaker-Emby/1.0 (https://example.org)").GetFrontAsync(ReleaseId);

            Assert.NotNull(image);
            Assert.Equal(".jpg", image!.Extension);
            Assert.Equal($"https://coverartarchive.org/release/{ReleaseId}/front-500", handler.LastUrl);
        }

        [Fact]
        public async Task ANotFoundAnImageThatIsNotAPictureATinyAnswerAndABadIdGiveNothing()
        {
            var client404 = new CoverArtClient(new FakeHandler { Status = HttpStatusCode.NotFound }, "x/1");
            Assert.Null(await client404.GetFrontAsync(ReleaseId));

            var html = new FakeHandler { Body = System.Text.Encoding.ASCII.GetBytes(new string('<', 5000)) };
            Assert.Null(await new CoverArtClient(html, "x/1").GetFrontAsync(ReleaseId));

            Assert.Null(await new CoverArtClient(new FakeHandler { Body = Jpeg(100) }, "x/1").GetFrontAsync(ReleaseId));

            var untouched = new FakeHandler();
            Assert.Null(await new CoverArtClient(untouched, "x/1").GetFrontAsync("not-an-id"));
            Assert.Equal(0, untouched.Calls);
        }

        private static FakeMusicBrainzAdapter Mb() => new FakeMusicBrainzAdapter();

        // The processor tests reuse the Insomniac fixture through a small adapter so this file stays independent of WatcherTests' private helpers.
        private sealed class FakeMusicBrainzAdapter : IMusicBrainz
        {
            private readonly ReleaseInfo _release = MusicBrainzClient.ParseRelease(File.ReadAllText(TestFiles.Fixture("mb-release-insomniac-2021.json")));

            public Task<ReleaseInfo?> LookupReleaseAsync(string releaseId, CancellationToken cancellation = default) =>
                Task.FromResult<ReleaseInfo?>(string.Equals(releaseId, _release.Id, StringComparison.OrdinalIgnoreCase) ? _release : null);

            public Task<System.Collections.Generic.IReadOnlyList<ReleaseGroupHit>> SearchReleaseGroupsAsync(string artist, string album, int limit, SearchShape shape, CancellationToken cancellation = default) =>
                Task.FromResult<System.Collections.Generic.IReadOnlyList<ReleaseGroupHit>>(new[] { new ReleaseGroupHit { Id = "g1", Title = "Insomniac", ArtistCredit = "Green Day", PrimaryType = "Album", Score = 100 } });

            public Task<System.Collections.Generic.IReadOnlyList<ReleaseInfo>> BrowseOfficialReleasesAsync(string releaseGroupId, CancellationToken cancellation = default) =>
                Task.FromResult<System.Collections.Generic.IReadOnlyList<ReleaseInfo>>(new[] { _release });
        }

        private static async Task<(ProcessOutcome Outcome, InboxSandbox Sandbox, AlbumUnit Unit)> FileAlbum(ICoverArt? cover, Action<InboxSandbox>? before = null)
        {
            var s = new InboxSandbox();
            s.AddTrack(@"Albums\Green Day\Insomniac", "02 - Brat.mp3", "Brat", 2);
            s.AddTrack(@"Albums\Green Day\Insomniac", "07 - 86.mp3", "86", 7);
            before?.Invoke(s);
            var unit = Assert.Single(new LabelMaker.Core.Inbox.InboxScanner().Scan(s.Lane, new RealFileOps()));
            var processor = new AlbumProcessor(Mb(), new RealFileOps(), new JournalStore(s.Journals), new MatchSettings(), new PlannerOptions(), new TagWriteOptions(), cover);
            var outcome = await processor.ProcessAsync(s.Lane, unit, dryRun: false, null, CancellationToken.None);
            return (outcome, s, unit);
        }

        [Fact]
        public async Task AFiledAlbumWithoutACoverGetsTheReleasesFrontCover()
        {
            var cover = new FakeCoverArt();
            var (outcome, s, _) = await FileAlbum(cover);
            using (s)
            {
                Assert.Equal(QueueState.Done, outcome.State);
                Assert.Contains("Cover art added.", outcome.Reason);
                Assert.Equal(1, cover.Calls);
                Assert.True(File.Exists(Path.Combine(s.Library, "Green Day", "Insomniac", "cover.jpg")));
            }
        }

        [Fact]
        public async Task ACoverThatCameWithTheDownloadIsKeptAndNothingIsDownloaded()
        {
            var cover = new FakeCoverArt();
            var (outcome, s, _) = await FileAlbum(cover, sb => File.WriteAllBytes(Path.Combine(sb.Inbox, "Albums", "Green Day", "Insomniac", "folder.jpg"), Jpeg(2000)));
            using (s)
            {
                Assert.Equal(QueueState.Done, outcome.State);
                Assert.Equal(0, cover.Calls);
                Assert.DoesNotContain("Cover art added.", outcome.Reason);
                Assert.Single(Directory.EnumerateFiles(Path.Combine(s.Library, "Green Day", "Insomniac"), "cover.*"));
            }
        }

        [Fact]
        public async Task WithoutACoverReaderOrWhenTheReleaseHasNoCoverNothingIsAdded()
        {
            var (off, s1, _) = await FileAlbum(null);
            using (s1)
            {
                Assert.Equal(QueueState.Done, off.State);
                Assert.Empty(Directory.EnumerateFiles(Path.Combine(s1.Library, "Green Day", "Insomniac"), "*.jpg"));
            }

            var none = new FakeCoverArt { Image = null };
            var (missing, s2, _) = await FileAlbum(none);
            using (s2)
            {
                Assert.Equal(QueueState.Done, missing.State);
                Assert.Equal(1, none.Calls);
                Assert.Empty(Directory.EnumerateFiles(Path.Combine(s2.Library, "Green Day", "Insomniac"), "*.jpg"));
            }
        }

        [Fact]
        public async Task UndoRemovesTheCoverLabelMakerAddedAndTheEmptyLibraryFolder()
        {
            var (outcome, s, unit) = await FileAlbum(new FakeCoverArt());
            using (s)
            {
                Assert.Contains("Cover art added.", outcome.Reason);
                var undone = new Applier(new RealFileOps(), new JournalStore(s.Journals), new TagWriteOptions()).Undo(unit.Id);

                Assert.True(undone.Success, undone.Message);
                Assert.Empty(Directory.EnumerateFiles(s.Library, "*", SearchOption.AllDirectories));
                Assert.Equal(2, Directory.EnumerateFiles(s.Inbox, "*.mp3", SearchOption.AllDirectories).Count());
            }
        }
    }
}
