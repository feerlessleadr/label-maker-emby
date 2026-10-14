using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using LabelMaker.Core.Matching;
using LabelMaker.Core.Models;
using LabelMaker.Core.MusicBrainz;
using Xunit;

namespace LabelMaker.Tests
{
    /// <summary>
    /// The search cascade: the exact "album and artist" search often finds nothing, so the matcher tries looser searches, other spellings
    /// of the album name, and checks the artist itself. Found by calibration on a real library, where 14 of 287 albums matched nothing.
    /// </summary>
    public class SearchCascadeTests
    {
        private static ReleaseInfo Insomniac() =>
            MusicBrainzClient.ParseRelease(File.ReadAllText(TestFiles.Fixture("mb-release-insomniac-2021.json")));

        private static ReleaseGroupHit Group(string id, string title, string artist, int score = 100) =>
            new ReleaseGroupHit { Id = id, Title = title, ArtistCredit = artist, PrimaryType = "Album", Score = score };

        private static AlbumFile Brat(string album = "Insomniac", string artist = "Green Day") =>
            new AlbumFile("02 Brat.flac", new TrackTags { Title = "Brat", Artist = artist, AlbumArtist = artist, Album = album, TrackNumber = 2, DiscNumber = 1, TotalDiscs = 2 });

        [Fact]
        public async Task WhenTheExactSearchFindsNothingTheAlbumOnlySearchIsTried()
        {
            var mb = new FakeMusicBrainz { Browse = new[] { Insomniac() } };
            mb.GroupsByShape[SearchShape.QuotedAlbumAndArtist] = Array.Empty<ReleaseGroupHit>();
            mb.GroupsByShape[SearchShape.QuotedAlbumOnly] = new[] { Group("g1", "Insomniac", "Green Day") };

            var result = await new AlbumMatcher(mb).MatchAsync(new[] { Brat() }, null);

            Assert.Equal(MatchOutcome.Auto, result.Outcome);
            Assert.Equal(2, mb.SearchCalls);
            Assert.StartsWith("QuotedAlbumAndArtist|", mb.SearchLog[0]);
            Assert.StartsWith("QuotedAlbumOnly|", mb.SearchLog[1]);
            Assert.Contains(result.Notes, n => n.Contains("exact album, any artist"));
        }

        [Fact]
        public async Task AlbumsByOtherArtistsWithTheSameNameAreNotChecked()
        {
            var mb = new FakeMusicBrainz { Browse = new[] { Insomniac() } };
            mb.GroupsByShape[SearchShape.QuotedAlbumAndArtist] = Array.Empty<ReleaseGroupHit>();
            mb.GroupsByShape[SearchShape.QuotedAlbumOnly] = new[]
            {
                Group("other", "Insomniac", "Some Other Band"),
                Group("g1", "Insomniac", "Green Day"),
            };

            await new AlbumMatcher(mb).MatchAsync(new[] { Brat() }, null);

            Assert.Equal(1, mb.BrowseCalls); // only the Green Day album was browsed
        }

        [Fact]
        public async Task TheSecondNameWithoutATrailingParenthesisIsTried()
        {
            var mb = new FakeMusicBrainz { Browse = new[] { Insomniac() } };
            mb.GroupsByShape[SearchShape.QuotedAlbumAndArtist] = Array.Empty<ReleaseGroupHit>();
            mb.GroupsByShape[SearchShape.QuotedAlbumOnly] = Array.Empty<ReleaseGroupHit>();
            mb.GroupsByShape[SearchShape.PlainWords] = Array.Empty<ReleaseGroupHit>();
            // Only a search for the shortened name finds the album.
            var picky = new PickyMusicBrainz(mb, "Insomniac");

            var result = await new AlbumMatcher(picky).MatchAsync(new[] { Brat(album: "Insomniac (Green)") }, null);

            Assert.NotNull(result.Best);
            Assert.Contains(mb.SearchLog, line => line.Contains("|Insomniac (Green)|"));
            Assert.Contains(mb.SearchLog, line => line.Contains("|Insomniac|") );
        }

        [Fact]
        public async Task NothingFoundAnywhereStopsAfterAFewSearchesAndSaysWhatWasTried()
        {
            var mb = new FakeMusicBrainz();
            var result = await new AlbumMatcher(mb).MatchAsync(new[] { Brat(album: "Nothing Like This (Green)") }, null);

            Assert.Equal(MatchOutcome.NoMatch, result.Outcome);
            Assert.InRange(mb.SearchCalls, 3, 12);
            Assert.True(result.Notes.Count >= 3, "the notes should list each search");
        }

        private static ReleaseInfo SmallRelease(string id, string groupId, string title, string artist, params string[] trackTitles)
        {
            var release = new ReleaseInfo { Id = id, ReleaseGroupId = groupId, Title = title, ArtistCredit = artist, Status = "Official", Country = "US", Date = "1999-01-01" };
            var medium = new MediumInfo { Position = 1, Format = "CD" };
            for (int i = 0; i < trackTitles.Length; i++) medium.Tracks.Add(new RecordingInfo { Position = i + 1, Number = (i + 1).ToString(), Title = trackTitles[i], RecordingId = id + "-r" + (i + 1) });
            release.Media.Add(medium);
            return release;
        }

        private static AlbumFile Track(string title, int number, string album, string artist) =>
            new AlbumFile(title + ".flac", new TrackTags { Title = title, Artist = artist, AlbumArtist = artist, Album = album, TrackNumber = number, DiscNumber = 1, TotalDiscs = 1 });

        [Fact]
        public async Task ARenamedAlbumByTheSameArtistIsFoundButOnlyForReview()
        {
            // The tags say "Greatest Hits - Chapter One"; MusicBrainz lists the album as "The Hits - Chapter One".
            var mb = new FakeMusicBrainz { Browse = new[] { SmallRelease("rel-1", "grp-1", "The Hits - Chapter One", "Backstreet Boys", "Larger Than Life", "I Want It That Way", "Everybody") } };
            mb.GroupsByShape[SearchShape.PlainWords] = new[] { Group("grp-1", "The Hits - Chapter One", "Backstreet Boys") };

            var files = new[] { Track("Larger Than Life", 1, "Greatest Hits - Chapter One", "Backstreet Boys"), Track("I Want It That Way", 2, "Greatest Hits - Chapter One", "Backstreet Boys") };
            var result = await new AlbumMatcher(mb).MatchAsync(files, null);

            Assert.Equal(MatchOutcome.NeedsReview, result.Outcome);
            Assert.Equal("rel-1", result.Best!.Release.Id);
            Assert.Contains("different name or artist", result.Reason);
        }

        [Fact]
        public async Task AnAlbumCreditedUnderAnotherArtistNameIsFoundButOnlyForReview()
        {
            // "mgk" on the files, "Machine Gun Kelly" on MusicBrainz.
            var mb = new FakeMusicBrainz { Browse = new[] { SmallRelease("rel-2", "grp-2", "mainstream sellout", "Machine Gun Kelly", "title one", "title two") } };
            mb.GroupsByShape[SearchShape.QuotedAlbumOnly] = new[] { Group("grp-2", "mainstream sellout", "Machine Gun Kelly") };

            var files = new[] { Track("title one", 1, "mainstream sellout (sfw)", "mgk"), Track("title two", 2, "mainstream sellout (sfw)", "mgk") };
            var result = await new AlbumMatcher(mb).MatchAsync(files, null);

            Assert.NotEqual(MatchOutcome.Auto, result.Outcome);
            Assert.NotEqual(MatchOutcome.NoMatch, result.Outcome);
            Assert.Equal("rel-2", result.Best!.Release.Id);
        }

        [Fact]
        public async Task AnAlbumWhoseNameAndArtistAgreeCanStillBeAppliedAutomatically()
        {
            var mb = new FakeMusicBrainz { Browse = new[] { SmallRelease("rel-3", "grp-3", "Siamese Dream", "The Smashing Pumpkins", "Cherub Rock", "Quiet") } };
            mb.GroupsByShape[SearchShape.QuotedAlbumOnly] = new[] { Group("grp-3", "Siamese Dream", "The Smashing Pumpkins") };

            var files = new[] { Track("Cherub Rock", 1, "Siamese Dream", "The Smashing Pumpkins"), Track("Quiet", 2, "Siamese Dream", "The Smashing Pumpkins") };
            var result = await new AlbumMatcher(mb).MatchAsync(files, null);

            Assert.Equal(MatchOutcome.Auto, result.Outcome);
        }

        [Fact]
        public async Task AllAlbumsOfThatNameAreCheckedWhenOneBandHasSeveral()
        {
            // A band with a single, two albums and a live record all called the same: the right one is the fourth in the list.
            var wrongRelease = SmallRelease("wrong", "g1", "High Voltage", "AC/DC", "Some Other Song");
            var rightRelease = SmallRelease("right", "g4", "High Voltage", "AC/DC", "T.N.T.");
            var mb = new GroupBrowseFake(new Dictionary<string, ReleaseInfo>
            {
                ["g1"] = wrongRelease, ["g2"] = wrongRelease, ["g3"] = wrongRelease, ["g4"] = rightRelease,
            });
            mb.GroupsByShape[SearchShape.QuotedAlbumAndArtist] = new[]
            {
                Group("g1", "High Voltage", "AC/DC"), Group("g2", "High Voltage", "AC/DC"), Group("g3", "High Voltage", "AC/DC"), Group("g4", "High Voltage", "AC/DC"),
            };

            var result = await new AlbumMatcher(mb).MatchAsync(new[] { Track("T.N.T.", 1, "High Voltage", "AC/DC") }, null);

            Assert.Equal("right", result.Best!.Release.Id);
        }

        [Theory]
        [InlineData("The Little Mermaid", "The Little Mermaid: Original Motion Picture Soundtrack", true)]
        [InlineData("Greatest Hits", "Greatest Hits Volume 2", true)]
        [InlineData("Home", "Home Alone", false)]
        [InlineData("Siamese Dream", "Mellon Collie and the Infinite Sadness", false)]
        public void ASubtitleOnTheSameAlbumNameCountsAsTheSameAlbum(string a, string b, bool expected)
        {
            Assert.Equal(expected, TitleNormaliser.AlbumSimilarity(a, b) >= 0.85);
        }
        [Theory]
        [InlineData("Alan Menken & Howard Ashman", "Alan Menken", true)]
        [InlineData("KPop Demon Hunters Cast, HUNTR/X & Saja Boys", "KPop Demon Hunters", true)]
        [InlineData("Various Artists", "Michael Giacchino", true)]
        [InlineData("Daft Punk", "Various Artists", true)]
        [InlineData("The Beatles", "Beatles", true)]
        [InlineData("", "Anyone", true)]
        [InlineData("Green Day", "Some Other Band", false)]
        [InlineData("Queen", "Queens of the Stone Age", false)]
        public void ArtistsThatAreProbablyTheSameAreAccepted(string fileArtist, string credit, bool expected)
        {
            Assert.Equal(expected, AlbumMatcher.ArtistsAgree(fileArtist, credit));
        }

        [Theory]
        [InlineData("Frozen II", "Frozen 2")]
        [InlineData("Rocky IV", "Rocky 4")]
        [InlineData("Part III", "Part 3")]
        public void RomanNumeralsAndDigitsCompareAsTheSame(string a, string b)
        {
            Assert.Equal(1.0, TitleNormaliser.Similarity(a, b));
        }

        [Fact]
        public void ALetterSpacedTitleMatchesTheNormalOne()
        {
            Assert.True(TitleNormaliser.Similarity("G I R L", "Girl") >= 0.95);
        }

        [Theory]
        [InlineData("Frozen 2", "Frozen II")]
        [InlineData("Frozen II", "Frozen 2")]
        [InlineData("Rocky IV", "Rocky 4")]
        [InlineData("Dark Side of the Moon", "Dark Side of the Moon")]
        public void NumeralsCanBeSwappedForASearch(string input, string expected)
        {
            Assert.Equal(expected, TitleNormaliser.SwapNumerals(input));
        }

        [Fact]
        public void TheNameVariantsRunFromTheCleanNameToTheLooserOnes()
        {
            var variants = AlbumMatcher.AlbumNameVariants("Frozen 2 (Original Soundtrack) [FLAC]");
            Assert.Equal("Frozen 2 (Original Soundtrack)", variants[0]);
            Assert.Contains("Frozen 2", variants);
            Assert.Contains("Frozen II", variants);
        }

        [Theory]
        [InlineData("mainstream sellout (sfw)", "mainstream sellout")]
        [InlineData("your name forever (nsfw)", "your name forever")]
        public void SafeForWorkMarkersAreNoise(string input, string expected)
        {
            Assert.Equal(expected, AlbumNameCleaner.ForSearch(input));
        }

        [Theory]
        [InlineData("Take Off Your Pants and Jacket (Green)", "Take Off Your Pants and Jacket")]
        [InlineData("KPop Demon Hunters (Soundtrack from the Netflix Film / Deluxe Version)", "KPop Demon Hunters")]
        [InlineData("(What's the Story) Morning Glory?", "(What's the Story) Morning Glory?")]
        public void TheLooseNameDropsAnyTrailingParenthesis(string input, string expected)
        {
            Assert.Equal(expected, AlbumNameCleaner.ForSearchLoose(input));
        }

        [Fact]
        public void UnicodeDashesAndQuotesBecomePlainOnesInASearch()
        {
            Assert.Equal("blink-182", MusicBrainzClient.CleanForQuery("blink‐182"));
            Assert.Equal("Greatest Hits - Chapter One", MusicBrainzClient.CleanForQuery("Greatest Hits – Chapter One"));
            Assert.Equal("Don't Stop", MusicBrainzClient.CleanForQuery("Don’t   Stop"));
        }

        [Fact]
        public async Task EachSearchShapeAsksMusicBrainzTheRightWay()
        {
            var handler = new ScriptedHandler();
            for (int i = 0; i < 3; i++) handler.Then(HttpStatusCode.OK, "{\"release-groups\":[]}");
            var client = new MusicBrainzClient(handler, "LabelMaker-tests/0.0 (feerlessleadr@users.noreply.github.com)") { MinInterval = TimeSpan.Zero };

            await client.SearchReleaseGroupsAsync("Matchbox Twenty", "Yourself or Someone Like You", 5, SearchShape.QuotedAlbumAndArtist);
            await client.SearchReleaseGroupsAsync("Matchbox Twenty", "Yourself or Someone Like You", 25, SearchShape.QuotedAlbumOnly);
            await client.SearchReleaseGroupsAsync("Matchbox Twenty", "TRON: Legacy (Original Soundtrack)", 25, SearchShape.PlainWords);

            var queries = handler.Urls.Select(u => Uri.UnescapeDataString(new Uri(u).Query)).ToList();
            Assert.Contains("releasegroup:\"Yourself or Someone Like You\" AND artist:\"Matchbox Twenty\"", queries[0]);
            Assert.Contains("limit=5", queries[0]);
            Assert.Contains("query=releasegroup:\"Yourself or Someone Like You\"&", queries[1]);
            Assert.DoesNotContain("artist:", queries[1]);
            Assert.Contains("limit=25", queries[1]);
            Assert.Contains("query=TRON Legacy Original Soundtrack Matchbox Twenty&", queries[2]); // words only, no punctuation
        }
    }

    /// <summary>Answers a search only when the album name asked for is exactly <paramref name="onlyFor"/>; every other name finds nothing.</summary>
    internal sealed class PickyMusicBrainz : LabelMaker.Core.MusicBrainz.IMusicBrainz
    {
        private readonly FakeMusicBrainz _inner;
        private readonly string _onlyFor;

        public PickyMusicBrainz(FakeMusicBrainz inner, string onlyFor)
        {
            _inner = inner;
            _onlyFor = onlyFor;
        }

        public System.Threading.Tasks.Task<ReleaseInfo?> LookupReleaseAsync(string releaseId, System.Threading.CancellationToken cancellation = default) =>
            _inner.LookupReleaseAsync(releaseId, cancellation);

        public async System.Threading.Tasks.Task<IReadOnlyList<ReleaseGroupHit>> SearchReleaseGroupsAsync(string artist, string album, int limit, SearchShape shape, System.Threading.CancellationToken cancellation = default)
        {
            await _inner.SearchReleaseGroupsAsync(artist, album, limit, shape, cancellation); // records the call
            return string.Equals(album, _onlyFor, StringComparison.OrdinalIgnoreCase)
                ? new[] { new ReleaseGroupHit { Id = "g1", Title = "Insomniac", ArtistCredit = artist, PrimaryType = "Album", Score = 100 } }
                : Array.Empty<ReleaseGroupHit>();
        }

        public System.Threading.Tasks.Task<IReadOnlyList<ReleaseInfo>> BrowseOfficialReleasesAsync(string releaseGroupId, System.Threading.CancellationToken cancellation = default) =>
            _inner.BrowseOfficialReleasesAsync(releaseGroupId, cancellation);
    }

    /// <summary>A fake whose browse answer depends on which release group is asked for.</summary>
    internal sealed class GroupBrowseFake : IMusicBrainz
    {
        private readonly Dictionary<string, ReleaseInfo> _byGroup;

        public GroupBrowseFake(Dictionary<string, ReleaseInfo> byGroup) { _byGroup = byGroup; }

        public Dictionary<SearchShape, IReadOnlyList<ReleaseGroupHit>> GroupsByShape { get; } = new Dictionary<SearchShape, IReadOnlyList<ReleaseGroupHit>>();

        public Task<ReleaseInfo?> LookupReleaseAsync(string releaseId, System.Threading.CancellationToken cancellation = default) => Task.FromResult<ReleaseInfo?>(null);

        public Task<IReadOnlyList<ReleaseGroupHit>> SearchReleaseGroupsAsync(string artist, string album, int limit, SearchShape shape, System.Threading.CancellationToken cancellation = default) =>
            Task.FromResult(GroupsByShape.TryGetValue(shape, out var found) ? found : (IReadOnlyList<ReleaseGroupHit>)Array.Empty<ReleaseGroupHit>());

        public Task<IReadOnlyList<ReleaseInfo>> BrowseOfficialReleasesAsync(string releaseGroupId, System.Threading.CancellationToken cancellation = default)
        {
            var release = _byGroup[releaseGroupId];
            var copy = new ReleaseInfo { Id = release.Id, ReleaseGroupId = releaseGroupId, Title = release.Title, ArtistCredit = release.ArtistCredit, Status = release.Status, Country = release.Country, Date = release.Date };
            foreach (var m in release.Media) copy.Media.Add(m);
            return Task.FromResult<IReadOnlyList<ReleaseInfo>>(new[] { copy });
        }
    }
}