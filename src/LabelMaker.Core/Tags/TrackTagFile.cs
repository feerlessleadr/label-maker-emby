using System;
using System.Linq;
using LabelMaker.Core.Models;
using TagFile = TagLib.File;

namespace LabelMaker.Core.Tags
{
    /// <summary>How tags are written. Defaults match the owner's Picard.ini (ID3v2.4, UTF-8, ID3v1 kept).</summary>
    public sealed class TagWriteOptions
    {
        /// <summary>ID3v2 version for MP3 files: 3 or 4. Picard writes 4 unless its "write ID3v2.3" setting is on.</summary>
        public byte Id3Version { get; set; } = 4;

        /// <summary>Keep an ID3v1 footer on MP3 files (Picard's "write ID3v1" setting, on by default).</summary>
        public bool WriteId3v1 { get; set; } = true;
    }

    /// <summary>
    /// Reads and writes the tags Label Maker owns through TagLibSharp. Frames and fields it does not own are not touched, so
    /// lyrics, ratings, ReplayGain and MusicGrabber's custom tags survive a write.
    /// </summary>
    public static class TrackTagFile
    {
        private static readonly object WriteLock = new object();

        public static TrackTags Read(string path)
        {
            using var file = TagFile.Create(path);
            var tag = file.Tag;

            // ID3v2.3 separates several artists with "/", so TagLib splits the single name "AC/DC" into "AC" and "DC".
            // Putting the pieces back with "/" restores the name as written; ID3v2.4 and the other formats keep the first value.
            var id3 = file.GetTag(TagLib.TagTypes.Id3v2, false) as TagLib.Id3v2.Tag;
            bool slashSplit = id3 != null && id3.Version < 4;
            string Name(string[] names) => names.Length == 0 ? string.Empty : slashSplit ? string.Join("/", names) : names[0];

            return new TrackTags
            {
                Title = tag.Title ?? string.Empty,
                Artist = Name(tag.Performers),
                AlbumArtist = Name(tag.AlbumArtists),
                Album = tag.Album ?? string.Empty,
                TrackNumber = Positive(tag.Track),
                TotalTracks = Positive(tag.TrackCount),
                DiscNumber = Positive(tag.Disc),
                TotalDiscs = Positive(tag.DiscCount),
                Year = Positive(tag.Year),
                ReleaseMbid = tag.MusicBrainzReleaseId ?? string.Empty,
                DurationMs = file.Properties != null && file.Properties.Duration.TotalMilliseconds > 0 ? (int)file.Properties.Duration.TotalMilliseconds : (int?)null,
            };
        }

        /// <summary>
        /// Writes <paramref name="tags"/> into the file. Every field of <paramref name="tags"/> replaces the file's value (an empty
        /// string or zero clears it). Throws the usual I/O exceptions; a read-only file throws <see cref="UnauthorizedAccessException"/>.
        /// </summary>
        public static void Write(string path, TrackTags tags, TagWriteOptions options)
        {
            if (tags == null) throw new ArgumentNullException(nameof(tags));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.Id3Version != 3 && options.Id3Version != 4)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "The ID3v2 version must be 3 or 4.");
            }

            // TagLibSharp keeps the ID3 version and text encoding in static (process-wide) settings that it reads while saving, so two writes at once
            // with different settings could corrupt each other. One write at a time (the pipeline writes one album at a time anyway).
            lock (WriteLock)
            {
                // Static settings of TagLibSharp: used for every MP3 saved from here on.
                TagLib.Id3v2.Tag.DefaultVersion = options.Id3Version;
                TagLib.Id3v2.Tag.ForceDefaultVersion = true;
                TagLib.Id3v2.Tag.DefaultEncoding = TagLib.StringType.UTF8;

                using var file = TagFile.Create(path);
                var tag = file.Tag;
                tag.Title = Text(tags.Title);
                tag.Performers = Names(tags.Artist);
                tag.AlbumArtists = Names(tags.AlbumArtist);
                tag.Album = Text(tags.Album);
                tag.Track = Unsigned(tags.TrackNumber);
                tag.TrackCount = Unsigned(tags.TotalTracks);
                tag.Disc = Unsigned(tags.DiscNumber);
                tag.DiscCount = Unsigned(tags.TotalDiscs);
                tag.Year = Unsigned(tags.Year);
                tag.MusicBrainzReleaseId = string.IsNullOrWhiteSpace(tags.ReleaseMbid) ? null : tags.ReleaseMbid.Trim();

                if (!options.WriteId3v1)
                {
                    file.RemoveTags(TagLib.TagTypes.Id3v1);
                }
                file.Save();
            }
        }

        private static int? Positive(uint value) => value > 0 && value <= int.MaxValue ? (int)value : (int?)null;

        private static uint Unsigned(int? value) => value.HasValue && value.Value > 0 ? (uint)value.Value : 0u;

        private static string? Text(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static string[] Names(string value) => string.IsNullOrWhiteSpace(value) ? Array.Empty<string>() : new[] { value.Trim() };
    }
}
