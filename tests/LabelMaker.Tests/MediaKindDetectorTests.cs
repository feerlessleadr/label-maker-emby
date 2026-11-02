using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LabelMaker.Core.Inbox;
using LabelMaker.Core.Matching;
using LabelMaker.Core.Models;
using LabelMaker.Core.Pipeline;
using LabelMaker.Core.Tags;
using Xunit;

namespace LabelMaker.Tests
{
    public class MediaKindDetectorTests
    {
        private static AlbumFile F(string title, string genre = "", string album = "Some Album", int minutes = 4, int track = 1) =>
            new AlbumFile($"{track:00} {title}.mp3", new TrackTags { Title = title, Artist = "Someone", Album = album, Genre = genre, TrackNumber = track, DurationMs = minutes * 60 * 1000 });

        private static List<AlbumFile> Album(params AlbumFile[] files) => files.ToList();

        [Fact]
        public void AClassicalGenreOnMostFilesIsClassical()
        {
            var found = MediaKindDetector.Detect(Album(F("A", "Classical"), F("B", "Classical"), F("C", "Classical"), F("D", "Rock")));
            Assert.Equal(MediaKind.Classical, found!.Kind);
            Assert.Contains("classical music", found.Sentence);
        }

        [Fact]
        public void OneClassicalGenreAmongManyOrdinaryFilesIsNotEnough()
        {
            Assert.Null(MediaKindDetector.Detect(Album(F("A", "Classical"), F("B", "Rock"), F("C", "Rock"), F("D", "Pop"))));
        }

        [Theory]
        [InlineData("Audiobook")]
        [InlineData("Spoken Word")]
        [InlineData("Speech")]
        public void ASpokenWordGenreIsAnAudiobook(string genre)
        {
            Assert.Equal(MediaKind.Audiobook, MediaKindDetector.Detect(Album(F("One", genre), F("Two", genre)))!.Kind);
        }

        [Fact]
        public void APodcastGenreOrAlbumNameIsAPodcast()
        {
            Assert.Equal(MediaKind.Podcast, MediaKindDetector.Detect(Album(F("Episode 1", "Podcast")))!.Kind);
            Assert.Equal(MediaKind.Podcast, MediaKindDetector.Detect(Album(F("Episode 1", album: "The Daily Podcast"), F("Episode 2", album: "The Daily Podcast")))!.Kind);
        }

        [Fact]
        public void ChapterTitlesOnThreeOrMoreFilesAreAnAudiobook()
        {
            var chapters = Album(F("Chapter 1"), F("Chapter 2"), F("Chapter 3"), F("Epilogue"));
            Assert.Equal(MediaKind.Audiobook, MediaKindDetector.Detect(chapters)!.Kind);
            Assert.Null(MediaKindDetector.Detect(Album(F("Part 1"), F("Part 2")))); // two files are not enough to say
        }

        [Fact]
        public void AFileLongerThanNinetyMinutesIsAnAudiobookButEightyNineIsNot()
        {
            Assert.Equal(MediaKind.Audiobook, MediaKindDetector.Detect(Album(F("Long", minutes: 90)))!.Kind);
            Assert.Null(MediaKindDetector.Detect(Album(F("Long mix", minutes: 89))));
        }

        [Fact]
        public void MovementTitlesOnMostFilesAreClassical()
        {
            var symphony = Album(F("Symphony No. 5 in C minor, Op. 67: I. Allegro con brio", track: 1), F("Symphony No. 5 in C minor, Op. 67: II. Andante con moto", track: 2));
            Assert.Equal(MediaKind.Classical, MediaKindDetector.Detect(symphony)!.Kind);
            Assert.Null(MediaKindDetector.Detect(Album(F("Piano Sonata No. 14")))); // one file with only a title is not enough
        }

        [Fact]
        public void OrdinaryMusicAndALoneComposerTagAreLeftAlone()
        {
            var pop = Album(F("Armatage Shanks"), F("Brat"), F("86"), F("Geek Stink Breath"));
            Assert.Null(MediaKindDetector.Detect(pop));
            foreach (var f in pop) f.Tags.Composer = "Billie Joe Armstrong";
            Assert.Null(MediaKindDetector.Detect(pop));
            Assert.Null(MediaKindDetector.Detect(new List<AlbumFile>()));
        }

        [Fact]
        public void GenreAndComposerAreReadFromTheFileAndNeverWritten()
        {
            var path = Path.Combine(Path.GetTempPath(), "LabelMaker-genre-" + Guid.NewGuid().ToString("N") + ".mp3");
            try
            {
                File.Copy(TestFiles.Fixture("silence.mp3"), path);
                using (var tl = TagLib.File.Create(path))
                {
                    tl.Tag.Genres = new[] { "Classical", "Opera" };
                    tl.Tag.Composers = new[] { "Mozart" };
                    tl.Save();
                }

                var read = TrackTagFile.Read(path);
                Assert.Equal("Classical; Opera", read.Genre);
                Assert.Equal("Mozart", read.Composer);

                TrackTagFile.Write(path, new TrackTags { Title = "T", Artist = "A", AlbumArtist = "A", Album = "B", TrackNumber = 1 }, new TagWriteOptions());
                var after = TrackTagFile.Read(path);
                Assert.Equal("Classical; Opera", after.Genre); // Label Maker's own writes leave the genre and composer as they were
                Assert.Equal("Mozart", after.Composer);
            }
            finally { try { File.Delete(path); } catch (IOException) { } }
        }

        [Fact]
        public async Task ACertainMatchThatLooksClassicalGoesToReviewAndNothingMoves()
        {
            using var s = new InboxSandbox();
            s.AddTrack(@"Albums\Green Day\Insomniac", "02 - Brat.mp3", "Brat", 2);
            s.AddTrack(@"Albums\Green Day\Insomniac", "07 - 86.mp3", "86", 7);
            foreach (var f in Directory.EnumerateFiles(Path.Combine(s.Inbox, "Albums"), "*.mp3", SearchOption.AllDirectories))
            {
                using var tl = TagLib.File.Create(f);
                tl.Tag.Genres = new[] { "Classical" };
                tl.Save();
            }
            var mb = new FakeMusicBrainz
            {
                Groups = new[] { new LabelMaker.Core.MusicBrainz.ReleaseGroupHit { Id = "g1", Title = "Insomniac", ArtistCredit = "Green Day", PrimaryType = "Album", Score = 100 } },
                Browse = new[] { LabelMaker.Core.MusicBrainz.MusicBrainzClient.ParseRelease(File.ReadAllText(TestFiles.Fixture("mb-release-insomniac-2021.json"))) },
            };
            var ops = new RealFileOps();
            var processor = new AlbumProcessor(mb, ops, new JournalStore(s.Journals), new MatchSettings(), new PlannerOptions(), new TagWriteOptions());
            var unit = Assert.Single(new InboxScanner().Scan(s.Lane, ops));

            var outcome = await processor.ProcessAsync(s.Lane, unit, dryRun: false, null, CancellationToken.None);

            Assert.Equal(QueueState.NeedsReview, outcome.State);
            Assert.StartsWith("This looks like classical music", outcome.Reason);
            Assert.Empty(Directory.EnumerateFiles(s.Library, "*", SearchOption.AllDirectories));
            Assert.Equal(2, Directory.EnumerateFiles(s.Inbox, "*.mp3", SearchOption.AllDirectories).Count());
            Assert.Contains(outcome.Match!.Notes, n => n.StartsWith("recognized as classical"));
        }
    }
}
