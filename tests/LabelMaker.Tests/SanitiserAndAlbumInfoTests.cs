using System.IO;
using LabelMaker.Core.Inbox;
using LabelMaker.Core.Naming;
using Xunit;

namespace LabelMaker.Tests
{
    public class PathSanitiserTests
    {
        [Fact]
        public void InvalidWindowsCharactersBecomeUnderscores()
        {
            Assert.Equal("Who__ Live_", PathSanitiser.Component("Who?: Live*"));
            Assert.Equal("AC_DC", PathSanitiser.Component("AC/DC"));
        }

        [Fact]
        public void TrailingDotsAndSpacesAreTrimmed()
        {
            Assert.Equal("Album", PathSanitiser.Component("Album... "));
        }

        [Theory]
        [InlineData("CON", "_CON")]
        [InlineData("con.txt", "_con.txt")]
        [InlineData("COM1", "_COM1")]
        [InlineData("LPT9.flac", "_LPT9.flac")]
        public void ReservedDeviceNamesGetAPrefix(string input, string expected)
        {
            Assert.Equal(expected, PathSanitiser.Component(input));
        }

        [Fact]
        public void NamesThatOnlyLookReservedAreKept()
        {
            Assert.Equal("Console", PathSanitiser.Component("Console"));
        }

        [Fact]
        public void LongNamesAreCappedAtTheComponentLimit()
        {
            var result = PathSanitiser.Component(new string('a', 200));
            Assert.Equal(PathSanitiser.MaxComponentLength, result.Length);
        }

        [Fact]
        public void ControlCharactersAreRemoved()
        {
            Assert.Equal("abc", PathSanitiser.Component("a\tb\u0001c"));
        }

        [Fact]
        public void NamesAreNormalisedToNfc()
        {
            // "e" followed by a combining acute accent becomes the single character é.
            Assert.Equal("é", PathSanitiser.Component("é"));
        }

        [Theory]
        [InlineData(".")]
        [InlineData("..")]
        [InlineData("")]
        [InlineData("   ")]
        public void DotsAndBlankNamesBecomeEmpty(string input)
        {
            Assert.Equal(string.Empty, PathSanitiser.Component(input));
        }

        [Fact]
        public void PathDropsEmptyComponents()
        {
            Assert.Equal(new[] { "a", "b" }, PathSanitiser.Path("/a//b/"));
        }
    }

    public class AlbumInfoTests
    {
        [Fact]
        public void ReadsTheInsomniacSampleFromMusicGrabber()
        {
            var info = AlbumInfoReader.Read(TestFiles.Fixture("albuminfo-insomniac.json"));
            Assert.NotNull(info);
            Assert.Equal("Green Day", info!.Artist);
            Assert.Equal("Insomniac", info.Album);
            Assert.Equal("46b2741f-10f2-4c3e-9f3d-2c55abfa6f51", info.ReleaseMbid);
            Assert.Equal("2021", info.Year);
        }

        [Fact]
        public void AnInvalidReleaseIdIsDroppedButTheRestIsKept()
        {
            var info = AlbumInfoReader.Parse("{\"artist\":\"A\",\"album\":\"B\",\"release_mbid\":\"not-an-id\"}");
            Assert.NotNull(info);
            Assert.Equal(string.Empty, info!.ReleaseMbid);
            Assert.Equal("A", info.Artist);
        }

        [Fact]
        public void AReleaseIdInCapitalsIsLowerCased()
        {
            var info = AlbumInfoReader.Parse("{\"release_mbid\":\"46B2741F-10F2-4C3E-9F3D-2C55ABFA6F51\"}");
            Assert.Equal("46b2741f-10f2-4c3e-9f3d-2c55abfa6f51", info!.ReleaseMbid);
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("[1,2,3]")]
        [InlineData("")]
        public void NotAnObjectGivesNull(string json)
        {
            Assert.Null(AlbumInfoReader.Parse(json));
        }

        [Fact]
        public void AMissingFileGivesNull()
        {
            Assert.Null(AlbumInfoReader.Read(Path.Combine(Path.GetTempPath(), "no-such-albuminfo-" + System.Guid.NewGuid().ToString("N"))));
        }

        [Fact]
        public void AFileOverTheSizeLimitIsNotRead()
        {
            using var files = new TestFiles();
            var path = files.Copy("albuminfo-insomniac.json", "big.albuminfo");
            File.WriteAllText(path, "{\"artist\":\"" + new string('x', AlbumInfoReader.MaxBytes) + "\"}");
            Assert.Null(AlbumInfoReader.Read(path));
        }
    }
}

namespace LabelMaker.Tests
{
    public class PathSanitiserSurrogateTests
    {
        [Fact]
        public void CappingNeverSplitsASurrogatePair()
        {
            // 119 letters then an emoji (two UTF-16 chars): a cut at 120 would leave half of the pair.
            var result = LabelMaker.Core.Naming.PathSanitiser.Component(new string('a', 119) + "\U0001F3B5" + "tail");
            Assert.Equal(119, result.Length);
            Assert.False(char.IsHighSurrogate(result[result.Length - 1]));
        }
    }
}
