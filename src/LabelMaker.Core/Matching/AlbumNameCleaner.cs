using System;
using System.Text.RegularExpressions;

namespace LabelMaker.Core.Matching
{
    /// <summary>
    /// Removes the noise downloads put in album names before the name is searched on MusicBrainz: "[FLAC]", "(320kbps)", "(Remastered)",
    /// "(Deluxe Edition)" and web addresses. The edition words matter when choosing between editions, but they make a search miss, so
    /// they are dropped only for the search; the files' own tags are never changed by this.
    /// </summary>
    public static class AlbumNameCleaner
    {
        private static readonly Regex Brackets = new Regex(@"\s*\[[^\]]*\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex Site = new Regex(@"\s*\(?\b(?:www\.)?[a-z0-9-]+\.(?:com|net|org|ru|io)\b\)?", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex NoisyParentheses = new Regex(
            @"\s*\((?=[^)]*\b(?:flac|mp3|\d+\s*kbps|kbps|web|cdrip|cd|vinyl|24[- ]?bit|hi-?res|remaster(?:ed)?|deluxe|expanded|bonus|edition|anniversary|explicit|clean|n?sfw|reissue|digital|version)\b)[^)]*\)\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex AnyTrailingGroup = new Regex(@"\s*[\(\[][^\)\]]*[\)\]]\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// A looser version for a second try: every trailing "(...)" or "[...]" group is dropped, whatever it says ("Take Off Your Pants and
        /// Jacket (Green)" becomes "Take Off Your Pants and Jacket"). Returns the original when nothing would be left.
        /// </summary>
        public static string ForSearchLoose(string album)
        {
            var text = ForSearch(album);
            string previous;
            do
            {
                previous = text;
                text = AnyTrailingGroup.Replace(text, string.Empty);
            }
            while (text != previous);
            text = text.Trim();
            return text.Length == 0 ? ForSearch(album) : text;
        }

        /// <summary>The album name for searching. Returns the original text when cleaning would leave nothing.</summary>
        public static string ForSearch(string album)
        {
            if (string.IsNullOrWhiteSpace(album)) return string.Empty;
            var text = album.Trim();
            text = Brackets.Replace(text, string.Empty);
            text = Site.Replace(text, string.Empty);

            // Trailing noisy parentheses, possibly several ("Album (Deluxe Edition) (Remastered)").
            string previous;
            do
            {
                previous = text;
                text = NoisyParentheses.Replace(text, string.Empty);
            }
            while (text != previous);

            text = text.Trim().TrimEnd('-', ' ').Trim();
            return text.Length == 0 ? album.Trim() : text;
        }
    }
}
