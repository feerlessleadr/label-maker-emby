using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LabelMaker.Core.Inbox;
using LabelMaker.Core.Matching;
using LabelMaker.Core.Models;
using LabelMaker.Core.Naming;
using LabelMaker.Core.Pipeline;
using LabelMaker.Core.Tags;
using Xunit;

namespace LabelMaker.Tests
{
    /// <summary>Cases taken from real files the owner supplied on 2026-10-14 (tag values only; no paths, no audio).</summary>
    public class RealSampleTests
    {
        [Fact]
        public void PicardNamesACompilationTrackWithItsArtistAndNoDiscPrefixOnOneDisc()
        {
            // A "Various Artists" album Picard wrote: "04 American Hi-Fi - Vertigo".
            var tags = new TrackTags { Title = "Vertigo", Artist = "American Hi-Fi", AlbumArtist = "Various Artists", Album = "American Pie 2", TrackNumber = 4, TotalTracks = 15, DiscNumber = 1, TotalDiscs = 1, Year = 2001 };

            var rendered = PicardScript.Render(PlannerOptions.PicardDefaultScript, NamingVariables.ForTrack(tags));

            Assert.Equal("Various Artists/American Pie 2/04 American Hi-Fi - Vertigo", rendered);
        }

        [Fact]
        public void PicardNamesACompilationTrackOfAManyDiscSetWithADiscPrefix()
        {
            // "1-09 Barbara Mandrell - The Battle Hymn of the Republic" from a 4-disc compilation.
            var tags = new TrackTags { Title = "The Battle Hymn of the Republic", Artist = "Barbara Mandrell", AlbumArtist = "Various Artists", Album = "From Sea to Shining Sea", TrackNumber = 9, TotalTracks = 19, DiscNumber = 1, TotalDiscs = 4, Year = 2004 };

            var rendered = PicardScript.Render(PlannerOptions.PicardDefaultScript, NamingVariables.ForTrack(tags));

            Assert.Equal("Various Artists/From Sea to Shining Sea/1-09 Barbara Mandrell - The Battle Hymn of the Republic", rendered);
        }

        [Fact]
        public async Task AMusicGrabberSingleIsAnAlbumFolderWithOneTrackAndNoAlbuminfo()
        {
            // MusicGrabber puts a single download in Albums/<artist>/<album>/<title>.<ext> (a lyrics file beside it, no .albuminfo, no track number in the name).
            using var s = new InboxSandbox();
            var track = s.AddTrack(@"Albums\Green Day\Insomniac", "Brat.mp3", "Brat", 2);
            File.WriteAllText(Path.ChangeExtension(track, ".lrc"), "[00:01.00] words");

            var units = new InboxScanner().Scan(s.Lane, new RealFileOps());
            var unit = Assert.Single(units);
            Assert.Equal(UnitKind.Album, unit.Kind);
            Assert.Single(unit.Files);

            var mb = new FakeMusicBrainz
            {
                Groups = new[] { new LabelMaker.Core.MusicBrainz.ReleaseGroupHit { Id = "g1", Title = "Insomniac", ArtistCredit = "Green Day", PrimaryType = "Album", Score = 100 } },
                Browse = new[] { LabelMaker.Core.MusicBrainz.MusicBrainzClient.ParseRelease(File.ReadAllText(TestFiles.Fixture("mb-release-insomniac-2021.json"))) },
            };
            var processor = new AlbumProcessor(mb, new RealFileOps(), new JournalStore(s.Journals), new MatchSettings(), new PlannerOptions(), new TagWriteOptions());

            var outcome = await processor.ProcessAsync(s.Lane, unit, dryRun: false, null, CancellationToken.None);

            Assert.Equal(QueueState.Done, outcome.State);
            var moved = Directory.EnumerateFiles(s.Library, "*", SearchOption.AllDirectories).Select(Path.GetFileName).OrderBy(n => n).ToList();
            Assert.Equal(new[] { "1-02 Brat.lrc", "1-02 Brat.mp3" }, moved);
            Assert.Empty(Directory.EnumerateFiles(s.Inbox, "*", SearchOption.AllDirectories));
        }
    }
}
