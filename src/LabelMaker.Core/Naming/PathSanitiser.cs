using System;
using System.Collections.Generic;
using System.Text;

namespace LabelMaker.Core.Naming
{
    /// <summary>
    /// Makes folder and file names safe on Windows (and Linux, so a library stays portable): replaces characters Windows does not
    /// allow, removes control characters, trims trailing dots and spaces, avoids reserved device names, normalises to Unicode NFC
    /// and caps each name's length. Only the file name is changed: tags keep the names exactly as MusicBrainz or the file has them.
    /// </summary>
    public static class PathSanitiser
    {
        /// <summary>The longest single folder or file name (without the extension) that is written.</summary>
        public const int MaxComponentLength = 120;

        private const string InvalidChars = "\\/:*?\"<>|";

        private static readonly HashSet<string> ReservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>
        /// One folder or file name. Returns an empty string when nothing usable is left; callers drop empty components.
        /// <paramref name="replacement"/> stands in for each character Windows does not allow (default "_").
        /// </summary>
        public static string Component(string value, int maxLength = MaxComponentLength, char replacement = '_')
        {
            if (value == null) return string.Empty;
            var text = value.Normalize(NormalizationForm.FormC);

            var sb = new StringBuilder(text.Length);
            foreach (var ch in text)
            {
                if (ch < 0x20 || ch == 0x7F) continue; // control characters are dropped
                sb.Append(InvalidChars.IndexOf(ch) >= 0 ? replacement : ch);
            }

            var cleaned = sb.ToString().Trim().TrimEnd('.', ' ');
            if (cleaned.Length > maxLength)
            {
                // Never cut between the two halves of a surrogate pair (an emoji or rare character): that leaves invalid text.
                int cut = char.IsHighSurrogate(cleaned[maxLength - 1]) ? maxLength - 1 : maxLength;
                cleaned = cleaned.Substring(0, cut).TrimEnd('.', ' ');
            }
            if (cleaned == "." || cleaned == "..") return string.Empty;

            // "CON.txt" is reserved too: the part before the first dot decides.
            var stem = cleaned;
            int dot = cleaned.IndexOf('.');
            if (dot >= 0) stem = cleaned.Substring(0, dot);
            if (ReservedNames.Contains(stem)) cleaned = replacement + cleaned;

            return cleaned;
        }

        /// <summary>
        /// Splits a rendered naming-script result on "/" into safe components, dropping empty ones. Values were already
        /// stripped of "/" by <see cref="PicardScript"/>, so every "/" here came from the script.
        /// </summary>
        public static IReadOnlyList<string> Path(string rendered)
        {
            var parts = new List<string>();
            foreach (var raw in (rendered ?? string.Empty).Split('/'))
            {
                var part = Component(raw);
                if (part.Length > 0) parts.Add(part);
            }
            return parts;
        }
    }
}
