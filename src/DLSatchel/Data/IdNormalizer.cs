using System;
using System.Globalization;
using System.Text;

namespace DDV_SaveEditor_G.Data
{
    /// <summary>
    /// Turns the many shapes of an item id found in community spreadsheets into
    /// one canonical long. Rules:
    ///  • trim, drop NBSP / full-width spaces / quotes / '#',
    ///  • "10000002.0" → 10000002, "1.0000002E7" → 10000002,
    ///  • "7C92DFE6" / "0x7C92DFE6" → decimal,
    ///  • anything with a comma ("1,0000") is not an id,
    ///  • valid range is 1 .. 9_999_999_999.
    /// </summary>
    public static class IdNormalizer
    {
        public const long MaxId = 9_999_999_999L;

        /// <summary>Normalizes an id cell. A pure-digit value is always decimal.</summary>
        public static bool TryNormalize(string? raw, out long id, out string rawId, out string? error)
            => TryNormalizeCore(raw, forceHex: false, out id, out rawId, out error);

        /// <summary>
        /// Normalizes the dedicated "Hex" column. There a value like "989682" is
        /// hexadecimal (10000002), so digits must not be read as decimal.
        /// </summary>
        public static bool TryNormalizeHex(string? raw, out long id, out string rawId, out string? error)
            => TryNormalizeCore(raw, forceHex: true, out id, out rawId, out error);

        private static bool TryNormalizeCore(string? raw, bool forceHex, out long id, out string rawId, out string? error)
        {
            id = 0;
            rawId = raw ?? "";
            error = null;

            string cleaned = StripNoise(raw);
            if (cleaned.Length == 0)
            {
                error = "empty";
                return false;
            }

            if (cleaned.Contains(','))
            {
                // "1,0000" is a localized number, not an item id.
                error = "comma";
                return false;
            }

            bool hex = forceHex;
            if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                hex = true;
                cleaned = cleaned[2..];
            }
            else if (!forceHex && LooksLikeHex(cleaned))
            {
                hex = true;
            }

            bool parsed = hex
                ? long.TryParse(cleaned, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id)
                : TryParseDecimal(cleaned, out id);

            if (!parsed)
            {
                id = 0;
                error = hex ? "invalid-hex" : "invalid-number";
                return false;
            }

            if (id <= 0)
            {
                error = "zero-or-negative";
                id = 0;
                return false;
            }
            if (id > MaxId)
            {
                error = "out-of-range";
                id = 0;
                return false;
            }
            return true;
        }

        private static bool TryParseDecimal(string value, out long id)
        {
            id = 0;

            // Plain integers first (fast path, exact).
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                return true;

            // "10000002.0", "1.0000002E7" and similar numeric cell artefacts.
            if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d))
                return false;

            if (d != decimal.Truncate(d))
                return false;      // 12.5 is not an item id

            if (d < 0 || d > MaxId)
                return false;

            id = (long)d;
            return true;
        }

        private static bool LooksLikeHex(string value)
        {
            if (value.Length < 6 || value.Length > 10) return false;

            bool hasLetter = false, allHex = true;
            foreach (char ch in value)
            {
                if (Uri.IsHexDigit(ch))
                {
                    if (ch is >= 'A' and <= 'F' or >= 'a' and <= 'f') hasLetter = true;
                }
                else
                {
                    allHex = false;
                    break;
                }
            }
            return allHex && hasLetter;
        }

        private static string StripNoise(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";

            var sb = new StringBuilder(raw.Length);
            foreach (char ch in raw)
            {
                if (ch is '\u00A0' or '\u3000' or '\u200B' or '\uFEFF') continue;
                if (ch is '#' or '"' or '\'' or '\u2018' or '\u2019' or '\u201C' or '\u201D') continue;
                if (char.IsWhiteSpace(ch)) continue;
                sb.Append(ch);
            }
            return sb.ToString();
        }
    }
}
