using System.Collections.Generic;
using LabelMaker.Core.Models;

namespace LabelMaker.Core.Naming
{
    /// <summary>The variables a naming script sees for one track, named as in Picard.</summary>
    public static class NamingVariables
    {
        /// <summary>
        /// Builds the variables for a track. Values have "/" and "\" replaced by "_" so they never create folders (AC/DC becomes
        /// AC_DC). "_multiartist" is "1" when the track's artist differs from the album artist (Picard's rule for compilations;
        /// an approximation until checked against a real Picard compilation).
        /// </summary>
        public static IReadOnlyDictionary<string, string> ForTrack(TrackTags track)
        {
            var vars = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["albumartist"] = Value(track.AlbumArtist),
                ["artist"] = Value(track.Artist),
                ["album"] = Value(track.Album),
                ["title"] = Value(track.Title),
                ["tracknumber"] = Number(track.TrackNumber),
                ["totaltracks"] = Number(track.TotalTracks),
                ["discnumber"] = Number(track.DiscNumber),
                ["totaldiscs"] = Number(track.TotalDiscs),
                ["year"] = Number(track.Year),
                ["_multiartist"] = IsMultiArtist(track) ? "1" : string.Empty,
            };
            return vars;
        }

        private static bool IsMultiArtist(TrackTags track) =>
            track.AlbumArtist.Length > 0 && track.Artist.Length > 0 &&
            !string.Equals(track.AlbumArtist, track.Artist, System.StringComparison.OrdinalIgnoreCase);

        private static string Value(string text) => (text ?? string.Empty).Replace('/', '_').Replace('\\', '_').Trim();

        private static string Number(int? value) => value.HasValue && value.Value > 0 ? value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
    }
}
