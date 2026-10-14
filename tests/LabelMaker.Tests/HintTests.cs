using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LabelMaker.Core.Inbox;
using LabelMaker.Core.Matching;
using LabelMaker.Core.Models;
using LabelMaker.Core.MusicBrainz;
using Xunit;

namespace LabelMaker.Tests
{
    /// <summary>
    /// Release ids that arrive with the files: MusicGrabber's .albuminfo and the MUSICBRAINZ_ALBUMID already in Picard-tagged files.
    /// Either one is looked up and checked against the files; a wrong or stale id is ignored and the search runs.
    /// </summary>
    public class HintTests
    {
        private const string InsomniacId = "46b2741f-10f2-4c3e-9f3d-2c55abfa6f51";
        private const string OtherId = "11111111-2222-3333-4444-555555555555";

        private static ReleaseInfo Insomniac() =>
            MusicBrainzClient.ParseRelease(File.ReadAllText(TestFiles.Fixture("mb-release-insomniac-2021.json")));

        private static AlbumFile Track(string title, int number, int disc, string releaseId = "", string album = "Insomniac", string artist = "Green Day") =>
            new AlbumFile($"{disc}-{number:00} {title}.flac", new TrackTags
            {
                Title = title, Artist = artist, AlbumArtist = artist, Album = album, TrackNumber = number, DiscNumber = disc, TotalDiscs = 2, ReleaseMbid = releaseId,
            });

        [Fact]
        public async Task AReleaseIdInTheTagsThatFitsIsUsedWithoutSearching()
        {
            var mb = new FakeMusicBrainz { Lookup = Insomniac() };
            var files = new[] { Track("Brat", 2, 1, InsomniacId), Track("86", 7, 1, InsomniacId) };

            var result = await new AlbumMatcher(mb).MatchAsync(files, null);

            Assert.Equal(MatchOutcome.Auto, result.Outcome);
            Assert.Equal("tags", result.Source);
            Assert.Equal(1, mb.LookupCalls);
            Assert.Equal(0, mb.SearchCalls);
        }

        [Fact]
        public async Task AReleaseIdInTheTagsThatDoesNotFitTheFilesIsIgnoredAndTheSearchRuns()
        {
            // The tags carry the id of an album these files are not on (a stale or wrong id).
            var mb = new FakeMusicBrainz { Lookup = Insomniac() };
            var files = new[] { Track("Hallelujah", 5, 1, InsomniacId, album: "Various Positions", artist: "Leonard Cohen") };

            var result = await new AlbumMatcher(mb).MatchAsync(files, null);

            Assert.NotEqual("tags", result.Source);
            Assert.NotEqual(MatchOutcome.Auto, result.Outcome);
            Assert.True(mb.SearchCalls >= 1, "the search should have run after the id failed");
            Assert.Contains(result.Notes, n => n.Contains("in the tags points at"));
        }

        [Fact]
        public async Task AReleaseIdThatMusicBrainzDoesNotKnowIsNotedAndTheSearchRuns()
        {
            var mb = new FakeMusicBrainz { Lookup = null };
            var files = new[] { Track("Brat", 2, 1, OtherId) };

            var result = await new AlbumMatcher(mb).MatchAsync(files, null);

            Assert.True(mb.SearchCalls >= 1);
            Assert.Contains(result.Notes, n => n.Contains("was not found on MusicBrainz"));
        }

        [Fact]
        public async Task FilesThatCarryDifferentReleaseIdsAreNeverAppliedAutomatically()
        {
            var mb = new FakeMusicBrainz { Lookup = Insomniac(), Browse = new[] { Insomniac() } };
            mb.Groups = new[] { new ReleaseGroupHit { Id = "g1", Title = "Insomniac", ArtistCredit = "Green Day", PrimaryType = "Album", Score = 100 } };
            var files = new[] { Track("Brat", 2, 1, InsomniacId), Track("86", 7, 1, OtherId) };

            var result = await new AlbumMatcher(mb).MatchAsync(files, null);

            Assert.Equal(MatchOutcome.NeedsReview, result.Outcome);
            Assert.Contains("different MusicBrainz release ids", result.Reason);
        }

        [Fact]
        public async Task MusicGrabbersHintAndTheSameIdInTheTagsAreLookedUpOnce()
        {
            var mb = new FakeMusicBrainz { Lookup = Insomniac() };
            var info = new AlbumInfo { Artist = "Green Day", Album = "Insomniac", ReleaseMbid = InsomniacId, Year = "2021" };

            var result = await new AlbumMatcher(mb).MatchAsync(new[] { Track("Brat", 2, 1, InsomniacId) }, info);

            Assert.Equal("albuminfo", result.Source); // the hint comes first
            Assert.Equal(1, mb.LookupCalls);
        }

        [Fact]
        public async Task AnIdInTheTagsWrittenInCapitalsOrWithSpacesStillCounts()
        {
            var mb = new FakeMusicBrainz { Lookup = Insomniac() };
            var files = new[] { Track("Brat", 2, 1, "  " + InsomniacId.ToUpperInvariant() + " ") };

            var result = await new AlbumMatcher(mb).MatchAsync(files, null);

            Assert.Equal("tags", result.Source);
        }

        [Fact]
        public async Task TextThatIsNotAReleaseIdIsIgnored()
        {
            var mb = new FakeMusicBrainz();
            var files = new[] { Track("Brat", 2, 1, "not an id") };

            await new AlbumMatcher(mb).MatchAsync(files, null);

            Assert.Equal(0, mb.LookupCalls);
        }
    }
}
