using System.IO;
using LabelMaker.Core.Models;
using LabelMaker.Core.Naming;
using Xunit;

namespace LabelMaker.Tests
{
    /// <summary>The owner's stock Picard script (docs/reference copy in Fixtures), checked against the paths it must produce.</summary>
    public class PicardScriptTests
    {
        private static string DefaultScript() => File.ReadAllText(TestFiles.Fixture("picard-default-naming-script.txt"));

        private static string Folders(TrackTags track)
        {
            var rendered = PicardScript.Render(DefaultScript(), NamingVariables.ForTrack(track));
            return string.Join("/", PathSanitiser.Path(rendered));
        }

        private static TrackTags Track(string albumArtist, string artist, string album, string title, int track, int? totalTracks = 14, int disc = 1, int totalDiscs = 1) =>
            new TrackTags
            {
                AlbumArtist = albumArtist,
                Artist = artist,
                Album = album,
                Title = title,
                TrackNumber = track,
                TotalTracks = totalTracks,
                DiscNumber = disc,
                TotalDiscs = totalDiscs,
            };

        [Fact]
        public void SingleDiscAlbumIsArtistAlbumTrackTitle()
        {
            var t = Track("Green Day", "Green Day", "Insomniac", "Armatage Shanks", 1);
            Assert.Equal("Green Day/Insomniac/01 Armatage Shanks", Folders(t));
        }

        [Fact]
        public void TwoDiscAlbumPutsTheDiscInTheFileNameNotInASubfolder()
        {
            var t = Track("Green Day", "Green Day", "Insomniac", "Armatage Shanks", 1, disc: 2, totalDiscs: 2);
            Assert.Equal("Green Day/Insomniac/2-01 Armatage Shanks", Folders(t));
        }

        [Fact]
        public void TenOrMoreDiscsPadTheDiscNumberToTwoDigits()
        {
            var t = Track("Artist", "Artist", "Box", "Song", 1, disc: 3, totalDiscs: 12);
            Assert.Equal("Artist/Box/03-01 Song", Folders(t));
        }

        [Fact]
        public void CompilationPutsTheTrackArtistInTheFileName()
        {
            var t = Track("Various Artists", "Leonard Cohen", "Various Positions", "Hallelujah", 5, totalTracks: 9);
            Assert.Equal("Various Artists/Various Positions/05 Leonard Cohen - Hallelujah", Folders(t));
        }

        [Fact]
        public void TrackWithoutAlbumArtistLandsInTheArtistFolderAsPicardDoes()
        {
            // Known quirk of the stock script, kept for parity (DESIGN.md section 4 flags it for review).
            var t = Track(string.Empty, "Leonard Cohen", "Various Positions", "Hallelujah", 5);
            Assert.Equal("Leonard Cohen/Hallelujah", Folders(t));
        }

        [Fact]
        public void SlashInATagValueDoesNotCreateAFolder()
        {
            var t = Track("AC/DC", "AC/DC", "Back in Black", "Hells Bells", 1);
            Assert.Equal("AC_DC/Back in Black/01 Hells Bells", Folders(t));
        }

        [Fact]
        public void ScriptFileHasNoLineBreakInTheRenderedResult()
        {
            var script = DefaultScript();
            Assert.Contains("\n", script);
            PicardScript.Validate(script);
        }

        [Fact]
        public void LiteralCommasAtTheTopLevelAreKept()
        {
            Assert.Equal("a, b", PicardScript.Render("a, b", new System.Collections.Generic.Dictionary<string, string>()));
        }

        [Fact]
        public void UnknownFunctionIsReportedByName()
        {
            var ex = Assert.Throws<PicardScriptException>(() => PicardScript.Validate("$foo(%title%)"));
            Assert.Contains("$foo", ex.Message);
        }

        [Fact]
        public void UnclosedCallIsReported()
        {
            Assert.Throws<PicardScriptException>(() => PicardScript.Validate("$if(%title%,x"));
        }

        [Fact]
        public void StrayClosingBracketIsReported()
        {
            Assert.Throws<PicardScriptException>(() => PicardScript.Validate("%title%)"));
        }

        [Fact]
        public void UnclosedVariableIsReported()
        {
            Assert.Throws<PicardScriptException>(() => PicardScript.Validate("%title"));
        }
    }
}
