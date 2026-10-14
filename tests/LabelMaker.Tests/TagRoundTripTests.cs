using System;
using System.IO;
using System.Linq;
using LabelMaker.Core.Models;
using LabelMaker.Core.Tags;
using TagLib;
using Xunit;
using TagFile = TagLib.File;

namespace LabelMaker.Tests
{
    /// <summary>Writes the tags Label Maker owns into copies of tiny generated files and reads them back.</summary>
    public class TagRoundTripTests : IDisposable
    {
        private readonly TestFiles _files = new TestFiles();

        public void Dispose() => _files.Dispose();

        private static TrackTags Sample() => new TrackTags
        {
            Title = "Armatage Shanks",
            Artist = "Green Day",
            AlbumArtist = "Green Day",
            Album = "Insomniac",
            TrackNumber = 1,
            TotalTracks = 14,
            DiscNumber = 2,
            TotalDiscs = 2,
            Year = 2021,
            ReleaseMbid = "46b2741f-10f2-4c3e-9f3d-2c55abfa6f51",
        };

        private static void AssertSample(TrackTags actual)
        {
            var expected = Sample();
            Assert.Equal(expected.Title, actual.Title);
            Assert.Equal(expected.Artist, actual.Artist);
            Assert.Equal(expected.AlbumArtist, actual.AlbumArtist);
            Assert.Equal(expected.Album, actual.Album);
            Assert.Equal(expected.TrackNumber, actual.TrackNumber);
            Assert.Equal(expected.TotalTracks, actual.TotalTracks);
            Assert.Equal(expected.DiscNumber, actual.DiscNumber);
            Assert.Equal(expected.TotalDiscs, actual.TotalDiscs);
            Assert.Equal(expected.Year, actual.Year);
            Assert.Equal(expected.ReleaseMbid, actual.ReleaseMbid);
        }

        [Theory]
        [InlineData("silence.mp3", "t.mp3")]
        [InlineData("silence.flac", "t.flac")]
        public void TagsSurviveAWriteAndARead(string fixture, string name)
        {
            var path = _files.Copy(fixture, name);
            TrackTagFile.Write(path, Sample(), new TagWriteOptions());
            AssertSample(TrackTagFile.Read(path));
        }

        [Fact]
        public void Mp3IsWrittenAsId3v24ByDefault()
        {
            var path = _files.Copy("silence.mp3", "v24.mp3");
            TrackTagFile.Write(path, Sample(), new TagWriteOptions());
            var bytes = System.IO.File.ReadAllBytes(path);
            Assert.Equal("ID3", System.Text.Encoding.ASCII.GetString(bytes, 0, 3));
            Assert.Equal(4, bytes[3]);
        }

        [Fact]
        public void Mp3CanBeWrittenAsId3v23()
        {
            var path = _files.Copy("silence.mp3", "v23.mp3");
            TrackTagFile.Write(path, Sample(), new TagWriteOptions { Id3Version = 3 });
            var bytes = System.IO.File.ReadAllBytes(path);
            Assert.Equal(3, bytes[3]);
            AssertSample(TrackTagFile.Read(path));
        }

        [Fact]
        public void Id3v1FooterIsKeptByDefaultAsPicardDoes()
        {
            var path = _files.Copy("silence.mp3", "v1on.mp3");
            TrackTagFile.Write(path, Sample(), new TagWriteOptions());
            Assert.Equal("TAG", System.Text.Encoding.ASCII.GetString(System.IO.File.ReadAllBytes(path), System.IO.File.ReadAllBytes(path).Length - 128, 3));
        }

        [Fact]
        public void Id3v1FooterCanBeTurnedOff()
        {
            var path = _files.Copy("silence.mp3", "v1off.mp3");
            TrackTagFile.Write(path, Sample(), new TagWriteOptions { WriteId3v1 = false });
            var bytes = System.IO.File.ReadAllBytes(path);
            Assert.NotEqual("TAG", System.Text.Encoding.ASCII.GetString(bytes, bytes.Length - 128, 3));
        }

        [Fact]
        public void CustomId3FramesFromMusicGrabberSurviveAWrite()
        {
            var path = _files.Copy("silence.mp3", "custom.mp3");
            using (var file = TagFile.Create(path))
            {
                var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, create: true);
                TagLib.Id3v2.UserTextInformationFrame.Get(id3, "MUSICGRABBER_FILE_ID", create: true).Text = new[] { "abc-123" };
                TagLib.Id3v2.UserTextInformationFrame.Get(id3, "SOURCE", create: true).Text = new[] { "monochrome" };
                file.Save();
            }

            TrackTagFile.Write(path, Sample(), new TagWriteOptions());

            using var after = TagFile.Create(path);
            var id3After = (TagLib.Id3v2.Tag)after.GetTag(TagTypes.Id3v2);
            Assert.Equal("abc-123", TagLib.Id3v2.UserTextInformationFrame.Get(id3After, "MUSICGRABBER_FILE_ID", create: false).Text.Single());
            Assert.Equal("monochrome", TagLib.Id3v2.UserTextInformationFrame.Get(id3After, "SOURCE", create: false).Text.Single());
        }

        [Fact]
        public void CustomVorbisCommentsSurviveAWriteToFlac()
        {
            var path = _files.Copy("silence.flac", "custom.flac");
            using (var file = TagFile.Create(path))
            {
                var xiph = (TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph, create: true);
                xiph.SetField("SOURCE", "monochrome");
                xiph.SetField("MUSICGRABBER_FILE_ID", "xyz-9");
                file.Save();
            }

            TrackTagFile.Write(path, Sample(), new TagWriteOptions());

            using var after = TagFile.Create(path);
            var xiphAfter = (TagLib.Ogg.XiphComment)after.GetTag(TagTypes.Xiph);
            Assert.Equal("monochrome", xiphAfter.GetFirstField("SOURCE"));
            Assert.Equal("xyz-9", xiphAfter.GetFirstField("MUSICGRABBER_FILE_ID"));
        }

        [Fact]
        public void ReadOnlyFileRaisesAPlainPermissionError()
        {
            var path = _files.Copy("silence.mp3", "locked.mp3");
            System.IO.File.SetAttributes(path, FileAttributes.ReadOnly);
            Assert.Throws<UnauthorizedAccessException>(() => TrackTagFile.Write(path, Sample(), new TagWriteOptions()));
        }

        [Fact]
        public void AnUnsupportedId3VersionIsRefused()
        {
            var path = _files.Copy("silence.mp3", "bad.mp3");
            Assert.Throws<ArgumentOutOfRangeException>(() => TrackTagFile.Write(path, Sample(), new TagWriteOptions { Id3Version = 2 }));
        }
    }
}
