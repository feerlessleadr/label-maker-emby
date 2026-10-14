using System.IO;
using System.Threading.Tasks;
using LabelMaker.Core.Matching;
using LabelMaker.Core.Models;
using LabelMaker.Core.MusicBrainz;
using Xunit;

namespace LabelMaker.Tests
{
    /// <summary>A single track that two different MusicBrainz albums list identically (same title, other album) must be decided by a person.</summary>
    public class SameNameAlbumTests
    {
        private static ReleaseInfo Insomniac(string groupId, string title, string country)
        {
            var release = MusicBrainzClient.ParseRelease(File.ReadAllText(TestFiles.Fixture("mb-release-insomniac-2021.json")));
            release.ReleaseGroupId = groupId;
            release.Id = System.Guid.NewGuid().ToString();
            release.Title = title;
            release.Country = country;
            return release;
        }

        private static AlbumFile Single() => new AlbumFile("Brat.mp3", new TrackTags
        {
            Title = "Brat", Artist = "Green Day", AlbumArtist = "Green Day", Album = "Insomniac", TrackNumber = 2, DiscNumber = 1, TotalDiscs = 2,
        });

        private static FakeMusicBrainz Fake(params ReleaseInfo[] releases) => new FakeMusicBrainz
        {
            Groups = new[] { new ReleaseGroupHit { Id = "g1", Title = "Insomniac", ArtistCredit = "Green Day", PrimaryType = "Album", Score = 100 } },
            Browse = releases,
        };

        [Fact]
        public async Task TwoDifferentAlbumsWithTheSameTitleAndTrackSendTheSingleToReview()
        {
            var mb = Fake(Insomniac("group-soundtrack", "Insomniac", "US"), Insomniac("group-cast-recording", "Insomniac", "GB"));

            var result = await new AlbumMatcher(mb).MatchAsync(new[] { Single() }, null);

            Assert.Equal(MatchOutcome.NeedsReview, result.Outcome);
            Assert.Contains("Two different albums", result.Reason);
        }

        [Fact]
        public async Task EditionsOfOneAlbumStillCountAsTheSameAlbum()
        {
            var mb = Fake(Insomniac("group-one", "Insomniac", "US"), Insomniac("group-one", "Insomniac", "GB"));

            var result = await new AlbumMatcher(mb).MatchAsync(new[] { Single() }, null);

            Assert.Equal(MatchOutcome.Auto, result.Outcome);
            Assert.Equal(1, result.InterchangeableEditions);
        }

        [Fact]
        public async Task ADifferentlyNamedDeluxeAlbumDoesNotBlockAutomaticMatching()
        {
            var mb = Fake(Insomniac("group-standard", "Insomniac", "US"), Insomniac("group-deluxe", "Insomniac (Deluxe Edition)", "US"));

            var result = await new AlbumMatcher(mb).MatchAsync(new[] { Single() }, null);

            Assert.Equal(MatchOutcome.Auto, result.Outcome);
        }
    }
}
