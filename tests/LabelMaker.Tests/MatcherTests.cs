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
using Xunit;

namespace LabelMaker.Tests
{
    internal sealed class FakeMusicBrainz : IMusicBrainz
    {
        public ReleaseInfo? Lookup { get; set; }
        public IReadOnlyList<ReleaseGroupHit> Groups { get; set; } = Array.Empty<ReleaseGroupHit>();
        public IReadOnlyList<ReleaseInfo> Browse { get; set; } = Array.Empty<ReleaseInfo>();
        public int LookupCalls { get; private set; }
        public int SearchCalls { get; private set; }
        public int BrowseCalls { get; private set; }

        public Task<ReleaseInfo?> LookupReleaseAsync(string releaseId, CancellationToken cancellation = default)
        {
            LookupCalls++;
            return Task.FromResult(Lookup);
        }

        public Task<IReadOnlyList<ReleaseGroupHit>> SearchReleaseGroupsAsync(string artist, string album, int limit = 5, CancellationToken cancellation = default)
        {
            SearchCalls++;
            return Task.FromResult(Groups);
        }

        public Task<IReadOnlyList<ReleaseInfo>> BrowseOfficialReleasesAsync(string releaseGroupId, CancellationToken cancellation = default)
        {
            BrowseCalls++;
            return Task.FromResult(Browse);
        }
    }

    public class MatcherTests
    {
        private readonly Xunit.Abstractions.ITestOutputHelper _out;
        public MatcherTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

        private static ReleaseInfo Insomniac() =>
            MusicBrainzClient.ParseRelease(File.ReadAllText(TestFiles.Fixture("mb-release-insomniac-2021.json")));

        private static AlbumFile File_(string title, int track, int disc, string album = "Insomniac", string artist = "Green Day", int totalDiscs = 2) =>
            new AlbumFile($"{disc}-{track:00} {title}.flac", new TrackTags
            {
                Title = title,
                Artist = artist,
                AlbumArtist = artist,
                Album = album,
                TrackNumber = track,
                DiscNumber = disc,
                TotalDiscs = totalDiscs,
            });

        private static AlbumInfo InsomniacInfo() => new AlbumInfo
        {
            Artist = "Green Day",
            Album = "Insomniac",
            ReleaseMbid = "46b2741f-10f2-4c3e-9f3d-2c55abfa6f51",
            Year = "2021",
        };

        // ---- verifying files against one release --------------------------------------------------------

        [Fact]
        public void TheSameTitleOnTwoDiscsGoesToTheRightDisc()
        {
            var files = new[]
            {
                File_("Armatage Shanks", 1, 1),
                File_("Armatage Shanks", 1, 2),
                File_("Stuck With Me", 3, 1),
                File_("Stuck With Me", 4, 2),
            };
            var v = ReleaseVerifier.Verify(Insomniac(), files);

            Assert.Equal(0, v.WeakFiles);
            Assert.True(v.Score > 0.99, $"score was {v.Score}");
            foreach (var a in v.Assignments)
            {
                Assert.NotNull(a.Track);
                Assert.Equal(a.File.Tags.DiscNumber, a.Disc);
                Assert.Equal(a.File.Tags.TrackNumber, a.Track!.Position);
            }
        }

        [Fact]
        public void TwoFilesNeverClaimOneTrack()
        {
            // Two copies of the same song on disc 1 only have one track to share; the second file ends up weak, not double-assigned.
            var files = new[] { File_("Brat", 2, 1), File_("Brat", 2, 1) };
            var v = ReleaseVerifier.Verify(Insomniac(), files);
            var tracks = v.Assignments.Where(a => a.Track != null).Select(a => a.Track!.RecordingId).ToList();
            Assert.Equal(tracks.Count, tracks.Distinct().Count());
        }

        // ---- the .albuminfo path ---------------------------------------------------------------------------

        [Fact]
        public async Task AReleaseIdFromAlbumInfoThatFitsIsUsedWithoutSearching()
        {
            var mb = new FakeMusicBrainz { Lookup = Insomniac() };
            var files = new[] { File_("Armatage Shanks", 1, 1), File_("Armatage Shanks", 1, 2), File_("86", 7, 1) };

            var result = await new AlbumMatcher(mb).MatchAsync(files, InsomniacInfo());

            Assert.Equal(MatchOutcome.Auto, result.Outcome);
            Assert.Equal("albuminfo", result.Source);
            Assert.Equal(1, mb.LookupCalls);
            Assert.Equal(0, mb.SearchCalls);
        }

        [Fact]
        public async Task AReleaseIdThatDoesNotFitFallsBackToSearchAndIsNotApplied()
        {
            // MusicGrabber's id points at a release these files are not on (here: files from another album).
            var mb = new FakeMusicBrainz { Lookup = Insomniac() };
            var files = new[] { File_("Hallelujah", 5, 1, album: "Various Positions", artist: "Leonard Cohen", totalDiscs: 1) };

            var result = await new AlbumMatcher(mb).MatchAsync(files, InsomniacInfo());

            Assert.NotEqual(MatchOutcome.Auto, result.Outcome);
            Assert.Equal(1, mb.SearchCalls);
        }

        [Fact]
        public async Task OneWrongFileInAPartialAlbumSendsItToReview()
        {
            var mb = new FakeMusicBrainz { Lookup = Insomniac() };
            var files = new[]
            {
                File_("Armatage Shanks", 1, 1),
                File_("Brat", 2, 1),
                File_("Stuck With Me", 3, 1),
                File_("A Song From Another Record", 5, 1),
            };

            var result = await new AlbumMatcher(mb).MatchAsync(files, InsomniacInfo());

            Assert.Equal(MatchOutcome.NeedsReview, result.Outcome);
            Assert.Contains("1 of 4 files", result.Reason);
        }

        [Fact]
        public async Task NothingFoundGivesNoMatchAndLeavesTheAlbumAlone()
        {
            var mb = new FakeMusicBrainz();
            var result = await new AlbumMatcher(mb).MatchAsync(new[] { File_("Some Song", 1, 1, album: "Unknown Album", artist: "Nobody", totalDiscs: 1) }, null);
            Assert.Equal(MatchOutcome.NoMatch, result.Outcome);
        }

        [Fact]
        public async Task FilesWithNoAlbumNameCannotBeSearched()
        {
            var mb = new FakeMusicBrainz();
            var result = await new AlbumMatcher(mb).MatchAsync(new[] { File_("Some Song", 1, 1, album: "", artist: "Nobody", totalDiscs: 1) }, null);
            Assert.Equal(MatchOutcome.NoMatch, result.Outcome);
            Assert.Equal(0, mb.SearchCalls);
        }

        // ---- a single track, searched, with 13 recorded editions --------------------------------------

        [Fact]
        public async Task ASingleTrackIsMatchedToItsAlbumAmongRecordedEditions()
        {
            var groups = MusicBrainzClient.ParseReleaseGroupSearch(File.ReadAllText(TestFiles.Fixture("mb-rgsearch-various-positions.json")));
            var editions = MusicBrainzClient.ParseReleaseList(File.ReadAllText(TestFiles.Fixture("mb-browse-various-positions.json")));
            var mb = new FakeMusicBrainz { Groups = groups, Browse = editions };
            var files = new[] { File_("Hallelujah", 5, 1, album: "Various Positions", artist: "Leonard Cohen", totalDiscs: 1) };

            var result = await new AlbumMatcher(mb).MatchAsync(files, null);

            _out.WriteLine($"outcome={result.Outcome} identical={result.InterchangeableEditions} best={result.Best?.Release.Id} {result.Best?.Release.Date} {result.Best?.Release.Country} score={result.Best?.Score:0.000}");
            _out.WriteLine("reason: " + result.Reason);
            foreach (var c in result.Candidates) _out.WriteLine($"  {c.Release.Id.Substring(0, 8)} {c.Release.Date,-10} {c.Release.Country,-3} score={c.Score:0.000} sig={(c.Signature == result.Best?.Signature ? "same" : "different")}");
            Assert.NotNull(result.Best);
            Assert.Equal("Various Positions", result.Best!.Release.Title);
            Assert.Equal(5, result.Best.Assignments.Single().Track!.Position);
            Assert.True(editions.Count >= 10, "the recorded browse answer should hold many editions");
            Assert.Equal("search", result.Source);
            // All 13 recorded editions carry the same recording of this song at track 5, so they are interchangeable: automatic, preferred country and earliest date win.
            Assert.Equal(MatchOutcome.Auto, result.Outcome);
            Assert.Equal(editions.Count - 1, result.InterchangeableEditions);
            Assert.Equal("US", result.Best.Release.Country);
            Assert.StartsWith("1984", result.Best.Release.Date);
        }

        // ---- editions that read the same but list different recordings (a remaster) ----------------------

        private static (ReleaseInfo Original, ReleaseInfo Remaster) TwoEditions()
        {
            var original = Insomniac();
            var remaster = Insomniac();
            remaster.Id = "11111111-2222-3333-4444-555555555555";
            remaster.Country = "GB";
            remaster.Date = "2022-01-01";
            foreach (var medium in remaster.Media)
            {
                foreach (var track in medium.Tracks) track.RecordingId = "remaster-" + track.RecordingId;
            }
            return (original, remaster);
        }

        private static FakeMusicBrainz SearchableWith(params ReleaseInfo[] editions) => new FakeMusicBrainz
        {
            Groups = new[] { new ReleaseGroupHit { Id = "g1", Title = "Insomniac", ArtistCredit = "Green Day", PrimaryType = "Album", Score = 100 } },
            Browse = editions,
        };

        [Fact]
        public async Task EditionsWithDifferentRecordingsGoToReviewUnderTheStrictRule()
        {
            var (original, remaster) = TwoEditions();
            var strict = new MatchSettings { EditionRule = EditionRule.SameRecordings };
            var result = await new AlbumMatcher(SearchableWith(original, remaster), strict).MatchAsync(new[] { File_("Brat", 2, 1), File_("86", 7, 1) }, null);
            Assert.Equal(MatchOutcome.NeedsReview, result.Outcome);
        }

        [Fact]
        public void TheTitleRuleIsTheDefault()
        {
            Assert.Equal(EditionRule.SameTitles, new MatchSettings().EditionRule);
        }

        [Fact]
        public async Task EditionsWithTheSameTitlesInTheSamePlacesAreInterchangeableUnderTheTitleRule()
        {
            var (original, remaster) = TwoEditions();
            var settings = new MatchSettings { EditionRule = EditionRule.SameTitles };
            var result = await new AlbumMatcher(SearchableWith(original, remaster), settings).MatchAsync(new[] { File_("Brat", 2, 1), File_("86", 7, 1) }, null);
            Assert.Equal(MatchOutcome.Auto, result.Outcome);
            Assert.Equal(1, result.InterchangeableEditions);
            Assert.Equal("11111111-2222-3333-4444-555555555555", result.Best!.Release.Id); // identical for the files: the preferred country (GB) beats an edition with no country
        }

        [Fact]
        public async Task TheTitleRulePicksTheEditionWhoseTitlesActuallyFitTheFiles()
        {
            var (original, remaster) = TwoEditions();
            remaster.Media[0].Tracks.First(t => t.Title == "Brat").Title = "Brat (Live)"; // a different track list for the files
            remaster.Media[0].Tracks.First(t => t.Title == "86").Title = "Eighty-Six";
            var settings = new MatchSettings { EditionRule = EditionRule.SameTitles };
            var result = await new AlbumMatcher(SearchableWith(original, remaster), settings).MatchAsync(new[] { File_("Brat", 2, 1), File_("86", 7, 1) }, null);
            // The other edition lists different titles for these files, so it is not a tie: the edition that fits wins, even under the title rule.
            Assert.Equal(MatchOutcome.Auto, result.Outcome);
            Assert.Equal(0, result.InterchangeableEditions);
            Assert.Equal("46b2741f-10f2-4c3e-9f3d-2c55abfa6f51", result.Best!.Release.Id);
        }
    }
}
