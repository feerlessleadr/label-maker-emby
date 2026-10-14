using System;
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
            return CollapseSpaces(sb.ToString());
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

        /// <summary>0 to 1. 1 means the same title once normalised; 0.9 or less when only a trailing remark differs.</summary>
        public static double Similarity(string a, string b)
        {
            var na = Normalise(a);
            var nb = Normalise(b);
            if (na.Length == 0 || nb.Length == 0) return 0;
            if (na == nb) return 1;

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
