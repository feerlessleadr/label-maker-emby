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
    /// <summary>A MusicBrainz that answers the song search from a table, and records what was asked.</summary>
    internal sealed class SongSearchMusicBrainz : IMusicBrainz
    {
        public Dictionary<string, IReadOnlyList<RecordingHit>> BySong { get; } = new Dictionary<string, IReadOnlyList<RecordingHit>>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<ReleaseGroupHit> Groups { get; set; } = Array.Empty<ReleaseGroupHit>();
        public IReadOnlyList<ReleaseInfo> Browse { get; set; } = Array.Empty<ReleaseInfo>();
        public List<string> SongLog { get; } = new List<string>();

        public Task<ReleaseInfo?> LookupReleaseAsync(string releaseId, CancellationToken cancellation = default) => Task.FromResult<ReleaseInfo?>(null);

        public Task<IReadOnlyList<ReleaseGroupHit>> SearchReleaseGroupsAsync(string artist, string album, int limit, SearchShape shape, CancellationToken cancellation = default) =>
            Task.FromResult(Groups);

        public Task<IReadOnlyList<ReleaseInfo>> BrowseOfficialReleasesAsync(string releaseGroupId, CancellationToken cancellation = default) => Task.FromResult(Browse);

        public Task<IReadOnlyList<RecordingHit>> SearchRecordingsAsync(string artist, string title, int limit, CancellationToken cancellation = default)
        {
            SongLog.Add(title + "|" + artist);
            return Task.FromResult(BySong.TryGetValue(title, out var hits) ? hits : (IReadOnlyList<RecordingHit>)Array.Empty<RecordingHit>());
        }
    }

    public class TrackTitleSearchTests
    {
        private const string GroupId = "46b2741f-aaaa-4c3e-9f3d-2c55abfa6f51";

        private static ReleaseInfo Insomniac()
        {
            var release = MusicBrainzClient.ParseRelease(File.ReadAllText(TestFiles.Fixture("mb-release-insomniac-2021.json")));
            release.ReleaseGroupId = GroupId;
            return release;
        }

        private static RecordingHit Song(string title, string artist = "Green Day", int score = 100)
        {
            var hit = new RecordingHit { Id = Guid.NewGuid().ToString(), Title = title, ArtistCredit = artist, Score = score };
            hit.Releases.Add(new RecordingRelease { ReleaseId = "r1", ReleaseTitle = "Insomniac", ReleaseGroupId = GroupId, ReleaseGroupTitle = "Insomniac", PrimaryType = "Album" });
            return hit;
        }

        private static AlbumFile File_(string title, int track, string album = "Unknown Album", string artist = "Green Day") =>
            new AlbumFile($"{track:00} {title}.mp3", new TrackTags { Title = title, Artist = artist, AlbumArtist = artist, Album = album, TrackNumber = track, DiscNumber = 1, TotalDiscs = 2 });

        private static SongSearchMusicBrainz Songs() => new SongSearchMusicBrainz
        {
            Browse = new[] { Insomniac() },
            BySong = { ["Brat"] = new[] { Song("Brat") }, ["86"] = new[] { Song("86") }, ["Armatage Shanks"] = new[] { Song("Armatage Shanks") } },
        };

        [Fact]
        public void ARecordingSearchAnswerIsReadWithItsReleasesAndAlbumTypes()
        {
            var hits = MusicBrainzClient.ParseRecordingSearch(File.ReadAllText(TestFiles.Fixture("mb-recording-search.json")));

            Assert.Equal(2, hits.Count);
            Assert.Equal("Brat", hits[0].Title);
            Assert.Equal("Green Day", hits[0].ArtistCredit);
            Assert.Equal(100, hits[0].Score);
            Assert.Equal(2, hits[0].Releases.Count); // the release with no release group is left out
            Assert.Equal("Insomniac", hits[0].Releases[0].ReleaseGroupTitle);
            Assert.Equal("Album", hits[0].Releases[0].PrimaryType);
            Assert.Equal(new[] { "Compilation" }, hits[0].Releases[1].SecondaryTypes);
            Assert.Empty(hits[1].Releases);
        }

        [Fact]
        public void ARealRecordedRecordingSearchAnswerIsRead()
        {
            // Recorded from musicbrainz.org on 2026-11-02: recording "Brat" by Green Day, official releases only.
            var hits = MusicBrainzClient.ParseRecordingSearch(File.ReadAllText(TestFiles.Fixture("mb-recording-search-real.json")));

            Assert.Equal(3, hits.Count);
            Assert.All(hits, h => Assert.Equal("Brat", h.Title));
            Assert.All(hits, h => Assert.Equal("Green Day", h.ArtistCredit));
            var release = Assert.Single(hits[0].Releases);
            Assert.Equal("Insomniac", release.ReleaseGroupTitle);
            Assert.Equal("061f572e-7660-3c6b-b4cd-cf66d46da93c", release.ReleaseGroupId);
            Assert.Equal("Album", release.PrimaryType);
            Assert.All(hits.SelectMany(h => h.Releases), r => Assert.NotEmpty(r.ReleaseGroupId));
        }

        [Fact]
        public async Task AWrongAlbumNameFallsBackToTheSongTitlesAndTheResultGoesToReview()
        {
            var mb = Songs();
            var files = new List<AlbumFile> { File_("Armatage Shanks", 1), File_("Brat", 2), File_("86", 8) };

            var result = await new AlbumMatcher(mb).MatchAsync(files, null);

            Assert.Equal(MatchOutcome.NeedsReview, result.Outcome); // never automatic
            Assert.Equal("titles", result.Source);
            Assert.StartsWith("Found by searching for the song titles", result.Reason);
            Assert.Equal("Insomniac", result.Best!.Release.Title);
            Assert.Equal(3, mb.SongLog.Count);
            Assert.Contains(result.Notes, n => n.StartsWith("song search for \"Brat\""));
        }

        [Fact]
        public async Task AMissingAlbumNameSearchesTheSongTitles()
        {
            var mb = Songs();
            var files = new List<AlbumFile> { File_("Brat", 2, album: ""), File_("86", 8, album: "") };

            var result = await new AlbumMatcher(mb).MatchAsync(files, null);

            Assert.Equal(MatchOutcome.NeedsReview, result.Outcome);
            Assert.Equal("Insomniac", result.Best!.Release.Title);
        }

        [Fact]
        public async Task WhenTheAlbumNameFindsTheAlbumTheSongsAreNotSearched()
        {
            var mb = Songs();
            mb.Groups = new[] { new ReleaseGroupHit { Id = GroupId, Title = "Insomniac", ArtistCredit = "Green Day", PrimaryType = "Album", Score = 100 } };
            var files = new List<AlbumFile> { File_("Brat", 2, album: "Insomniac"), File_("86", 8, album: "Insomniac") };

            var result = await new AlbumMatcher(mb).MatchAsync(files, null);

            Assert.Empty(mb.SongLog);
            Assert.NotEqual("titles", result.Source);
        }

        [Fact]
        public async Task AFewSongsAreSearchedNotEveryFile()
        {
            var mb = Songs();
            var files = Enumerable.Range(1, 12).Select(n => File_("Song " + n, n)).ToList();

            await new AlbumMatcher(mb).MatchAsync(files, null);

            Assert.Equal(TrackTitleSearch.MaxFilesSearched, mb.SongLog.Count);
        }

        [Fact]
        public async Task ALoneTitleWithNoArtistIsTooVagueToSearch()
        {
            var mb = Songs();
            var files = new List<AlbumFile> { File_("Brat", 2, artist: "") };

            var result = await new AlbumMatcher(mb).MatchAsync(files, null);

            Assert.Empty(mb.SongLog);
            Assert.Equal(MatchOutcome.NoMatch, result.Outcome);
        }

        [Fact]
        public async Task WeakAndWrongArtistSongHitsAreIgnored()
        {
            var mb = Songs();
            mb.BySong["Brat"] = new[] { Song("Brat", score: 60), Song("Brat", artist: "Somebody Else Entirely") };
            mb.BySong["86"] = new[] { Song("86", artist: "Another Band") };
            var files = new List<AlbumFile> { File_("Brat", 2), File_("86", 8) };

            var result = await new AlbumMatcher(mb).MatchAsync(files, null);

            Assert.Equal(MatchOutcome.NoMatch, result.Outcome);
        }
    }
}
