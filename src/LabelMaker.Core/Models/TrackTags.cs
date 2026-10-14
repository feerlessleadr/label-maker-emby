namespace LabelMaker.Core.Models
{
    /// <summary>
    /// The tags Label Maker reads and writes for one track. Only the fields Label Maker owns; everything else in a file
    /// (lyrics, ratings, ReplayGain, custom tags such as MusicGrabber's) is left as it is when the file is written.
    /// </summary>
    public sealed class TrackTags
    {
        public string Title { get; set; } = string.Empty;

        /// <summary>The track's own artist credit (for a compilation, the person on this track).</summary>
        public string Artist { get; set; } = string.Empty;

        /// <summary>The album's artist credit ("Various Artists" for a compilation).</summary>
        public string AlbumArtist { get; set; } = string.Empty;

        public string Album { get; set; } = string.Empty;

        public int? TrackNumber { get; set; }

        public int? TotalTracks { get; set; }

        public int? DiscNumber { get; set; }

        public int? TotalDiscs { get; set; }

        public int? Year { get; set; }

        /// <summary>The MusicBrainz release id ("MUSICBRAINZ_ALBUMID" in Vorbis, "MusicBrainz Album Id" in ID3 and MP4).</summary>
        public string ReleaseMbid { get; set; } = string.Empty;

        /// <summary>The audio length in milliseconds. Read from the file; never written.</summary>
        public int? DurationMs { get; set; }
    }
}
