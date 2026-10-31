using System;
using System.Globalization;
using System.Text;

namespace DDV_SaveEditor_G.Data
{
    /// <summary>
    /// Text helpers used by header detection, search and safety matching.
    /// Everything is culture-invariant: the data is a mix of English, Polish
    /// and localized dump sheets, so casing rules from tr-TR or similar must
    /// never leak in.
    /// </summary>
    public static class TextNormalizer
    {
        /// <summary>
        /// Header key: no diacritics, no punctuation, no spaces, upper case.
        /// "Item ID" and "ItemID" therefore collapse to the same key ("ITEMID").
        /// </summary>
        public static string NormalizeKey(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";

            string decomposed = value.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (char ch in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                    continue;
                if (char.IsLetterOrDigit(ch))
                    sb.Append(char.ToUpperInvariant(ch));
            }
            return sb.ToString();
        }

        /// <summary>Case- and diacritic-insensitive text used by catalog search.</summary>
        public static string FoldForSearch(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";

            string decomposed = value.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            bool lastWasSpace = false;
            foreach (char ch in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                    continue;

                // Treat full-width and non-breaking spaces as a plain space.
                char c = ch is '\u00A0' or '\u3000' ? ' ' : ch;
                if (char.IsWhiteSpace(c))
                {
                    if (!lastWasSpace && sb.Length > 0) sb.Append(' ');
                    lastWasSpace = true;
                    continue;
                }
                lastWasSpace = false;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString().Trim();
        }

        /// <summary>Removes NBSP, zero-width and typographic quotes; trims.</summary>
        public static string CleanCell(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";

            // Fast path: the overwhelming majority of cells are plain text.
            bool needsRewrite = false;
            foreach (char ch in value)
            {
                if (ch is '\u00A0' or '\u3000' or '\u200B' or '\uFEFF'
                    or '\u2018' or '\u2019' or '\u201C' or '\u201D')
                {
                    needsRewrite = true;
                    break;
                }
            }
            if (!needsRewrite) return value.Trim();

            var sb = new StringBuilder(value.Length);
            foreach (char ch in value)
            {
                if (ch is '\u00A0' or '\u3000' or '\u200B' or '\uFEFF') continue;
                if (ch is '\u2018' or '\u2019') { sb.Append('\''); continue; }
                if (ch is '\u201C' or '\u201D') { sb.Append('"'); continue; }
                sb.Append(ch);
            }
            return sb.ToString().Trim();
        }

        /// <summary>0-based column index → spreadsheet letters (7 → "H").</summary>
        public static string ColumnName(int zeroBasedIndex)
        {
            if (zeroBasedIndex < 0) return "";
            int n = zeroBasedIndex + 1;
            var sb = new StringBuilder(3);
            while (n > 0)
            {
                int rem = (n - 1) % 26;
                sb.Insert(0, (char)('A' + rem));
                n = (n - 1) / 26;
            }
            return sb.ToString();
        }

        /// <summary>"H" / "h" → 7. Returns -1 for invalid references.</summary>
        public static int ColumnIndex(string? cellReference)
        {
            if (string.IsNullOrEmpty(cellReference)) return -1;
            int n = 0, seen = 0;
            foreach (char ch in cellReference)
            {
                if (!char.IsLetter(ch)) break;
                n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
                seen++;
            }
            return seen == 0 ? -1 : n - 1;
        }
    }
}
