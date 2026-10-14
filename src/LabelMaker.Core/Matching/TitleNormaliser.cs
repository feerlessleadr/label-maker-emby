using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LabelMaker.Core.Matching
{
    /// <summary>
    /// Compares titles the way a person would: ignores case, accents, punctuation, curly versus straight apostrophes and "&amp;"
    /// versus "and", and gives a high (not perfect) score when only a trailing "(Remastered)" or "[Live]" differs.
    /// </summary>
    public static class TitleNormaliser
    {
        /// <summary>A comparison key: lower case, no accents, apostrophes dropped, other punctuation as one space.</summary>
        public static string Normalise(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var decomposed = text.Normalize(NormalizationForm.FormKD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (var ch in decomposed)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (category == UnicodeCategory.NonSpacingMark) continue; // accents
                if (ch == '\'' || ch == '’' || ch == '‘' || ch == '´' || ch == '`') continue; // Don't = Dont
                if (ch == '&') { sb.Append(" and "); continue; }
                sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
            }
            return MapNumerals(CollapseSpaces(sb.ToString()));
        }

        private static readonly Dictionary<string, string> RomanToDigit = new Dictionary<string, string>
        {
            // "i", "v" and "x" are left out: they are as likely to be a word or a letter as a number.
            ["ii"] = "2", ["iii"] = "3", ["iv"] = "4", ["vi"] = "6", ["vii"] = "7", ["viii"] = "8", ["ix"] = "9",
        };

        /// <summary>Whole-word Roman numerals II to IX become digits, so "Frozen II" and "Frozen 2" compare as the same.</summary>
        private static string MapNumerals(string normalised)
        {
            var parts = normalised.Split(' ');
            for (int i = 0; i < parts.Length; i++)
            {
                if (RomanToDigit.TryGetValue(parts[i], out var digit)) parts[i] = digit;
            }
            return string.Join(" ", parts);
        }

        /// <summary>
        /// The same text with each whole-word digit 2 to 9 written as a Roman numeral and each Roman numeral II to IX written as a digit.
        /// Used to try the other spelling of a title in a search. Returns the text unchanged when it has neither.
        /// </summary>
        public static string SwapNumerals(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text ?? string.Empty;
            var toRoman = new Dictionary<string, string> { ["2"] = "II", ["3"] = "III", ["4"] = "IV", ["5"] = "V", ["6"] = "VI", ["7"] = "VII", ["8"] = "VIII", ["9"] = "IX" };
            var parts = text.Split(' ');
            for (int i = 0; i < parts.Length; i++)
            {
                var bare = parts[i].Trim('(', ')', ',', '.', ':');
                if (toRoman.TryGetValue(bare, out var roman)) parts[i] = parts[i].Replace(bare, roman);
                else if (RomanToDigit.TryGetValue(bare.ToLowerInvariant(), out var digit)) parts[i] = parts[i].Replace(bare, digit);
            }
            return string.Join(" ", parts);
        }

        /// <summary>The title without a trailing "(...)", "[...]" or " - ..." part, as a comparison key.</summary>
        public static string Core(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var trimmed = text.Trim();
            bool changed = true;
            while (changed && trimmed.Length > 0)
            {
                changed = false;
                char last = trimmed[trimmed.Length - 1];
                if (last == ')' || last == ']')
                {
                    char open = last == ')' ? '(' : '[';
                    int at = trimmed.LastIndexOf(open);
                    if (at > 0) { trimmed = trimmed.Substring(0, at).TrimEnd(); changed = true; }
                }
            }
            int dash = trimmed.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0) trimmed = trimmed.Substring(0, dash);
            return Normalise(trimmed);
        }

        /// <summary>
        /// Like <see cref="Similarity"/> for album names, but an album name that is the other one plus a subtitle counts as 0.9
        /// ("The Little Mermaid" and "The Little Mermaid: Original Motion Picture Soundtrack"). The shorter name must be at least two words or
        /// ten letters, so a one-word name such as "Home" is not taken for a prefix of every longer title.
        /// </summary>
        public static double AlbumSimilarity(string a, string b)
        {
            double plain = Similarity(a, b);
            if (plain >= 0.9) return plain;
            var na = Normalise(a);
            var nb = Normalise(b);
            if (na.Length == 0 || nb.Length == 0) return plain;
            var shorter = na.Length <= nb.Length ? na : nb;
            var longer = na.Length <= nb.Length ? nb : na;
            bool prefix = longer.StartsWith(shorter + " ", StringComparison.Ordinal);
            bool enough = shorter.Split(' ').Length >= 2 || shorter.Length >= 10;
            return prefix && enough ? Math.Max(plain, 0.9) : plain;
        }

        /// <summary>0 to 1. 1 means the same title once normalised; 0.9 or less when only a trailing remark differs.</summary>
        public static double Similarity(string a, string b)
        {
            var na = Normalise(a);
            var nb = Normalise(b);
            if (na.Length == 0 || nb.Length == 0) return 0;
            if (na == nb) return 1;
            if (na.Replace(" ", string.Empty) == nb.Replace(" ", string.Empty)) return 0.95; // "G I R L" and "Girl"

            double full = Ratio(na, nb);
            var ca = Core(a);
            var cb = Core(b);
            double core = ca.Length > 0 && ca == cb ? 0.9 : 0;
            return Math.Max(full, core);
        }

        private static string CollapseSpaces(string text)
        {
            var sb = new StringBuilder(text.Length);
            bool space = false;
            foreach (var ch in text)
            {
                if (ch == ' ') { space = true; continue; }
                if (space && sb.Length > 0) sb.Append(' ');
                space = false;
                sb.Append(ch);
            }
            return sb.ToString();
        }

        /// <summary>1 minus the edit distance as a share of the longer text.</summary>
        private static double Ratio(string a, string b)
        {
            int distance = Levenshtein(a, b);
            int longest = Math.Max(a.Length, b.Length);
            return longest == 0 ? 1 : 1.0 - (double)distance / longest;
        }

        private static int Levenshtein(string a, string b)
        {
            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) previous[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }
                var swap = previous; previous = current; current = swap;
            }
            return previous[b.Length];
        }
    }
}
