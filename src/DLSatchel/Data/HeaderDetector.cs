using System;
using System.Collections.Generic;
using System.Linq;

namespace DDV_SaveEditor_G.Data
{
    /// <summary>Logical column names used across readers, merger and UI.</summary>
    public static class CatalogField
    {
        public const string Id = "ID";
        public const string Hex = "HEX";
        public const string Name = "NAME";
        public const string Display = "DISPLAY";
        public const string Type = "TYPE";
        public const string Category = "CATEGORY";
        public const string Subtype = "SUBTYPE";
        public const string Notes = "NOTES";
        public const string Patch = "PATCH";
        public const string Dlc = "DLC";
        public const string Image = "IMAGE";
        public const string CatalogueCategory = "CATALOGUE";
    }

    public sealed class HeaderResult
    {
        /// <summary>1-based row with the header.</summary>
        public int HeaderRow { get; init; }

        /// <summary>Logical field → 0-based column.</summary>
        public Dictionary<string, int> Columns { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Logical field → column letter, for the report and the Sources panel.</summary>
        public Dictionary<string, string> Mapping { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Notes { get; init; } = new();

        /// <summary>More than one plausible id column and no manual override.</summary>
        public bool NeedsIdConfirmation { get; init; }

        /// <summary>True when the "Name" column is a display name (Rubyelf), not internal NAME.</summary>
        public bool NameIsDisplay { get; init; }

        public bool DumpLike { get; init; }
    }

    /// <summary>
    /// Scans the first 25 rows of a sheet, accepts the row with the most alias
    /// hits and maps columns. Aliases are compared after stripping case,
    /// diacritics, spaces and punctuation.
    /// </summary>
    public static class HeaderDetector
    {
        public const int DefaultScanRows = 25;

        private static readonly Dictionary<string, string> AliasToField = BuildAliases();

        private static readonly Dictionary<string, int> IdRank = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ID"] = 0,
            ["ITEMID"] = 1,
            ["OBJECTID"] = 2,
            ["ITEMIDENTIFIER"] = 2,
            ["HEX"] = 5,
            ["ITEMHEX"] = 5,
            ["IDHEX"] = 5
        };

        /* ================================================================= */

        public static HeaderResult? Detect(
            RawSheet sheet,
            SheetMappingOverride? mappingOverride = null,
            int scanRows = DefaultScanRows)
        {
            int limit = Math.Min(scanRows, sheet.Rows.Count);
            int bestRow = -1, bestScore = int.MinValue, bestFieldCount = 0;

            for (int rowIndex = 0; rowIndex < limit; rowIndex++)
            {
                var row = sheet.Rows[rowIndex];
                if (RawSheet.IsEmpty(row)) continue;

                var headers = BuildHeaderMap(row);
                int fieldCount = headers.Keys
                    .SelectMany(key => AliasesFor(key))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();

                bool hasId = headers.Keys.Any(k => IsIdAlias(k));
                if (!hasId) continue;

                bool hasName = headers.Keys.Any(k => IsAlias(k, CatalogField.Name) || IsAlias(k, CatalogField.Display));
                int score = fieldCount * 10 + (hasName ? 3 : 0);

                if (score > bestScore ||
                    (score == bestScore && bestRow >= 0 && row.Length > sheet.Rows[bestRow].Length))
                {
                    bestScore = score;
                    bestRow = rowIndex;
                    bestFieldCount = fieldCount;
                }
            }

            if (bestRow < 0 || bestFieldCount == 0) return null;

            return MapColumns(sheet, bestRow + 1, mappingOverride);
        }

        private static HeaderResult MapColumns(
            RawSheet sheet, int headerRow, SheetMappingOverride? mappingOverride)
        {
            var header = BuildHeaderMap(sheet.GetRow(headerRow));
            var notes = new List<string>();
            var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            /* ---- id column(s) ------------------------------------------------ */
            var idCandidates = header
                .Where(kv => IsIdAlias(kv.Key))
                .SelectMany(kv => kv.Value.Select(index => (Key: kv.Key, Index: index, Rank: RankOf(kv.Key))))
                .OrderBy(c => c.Rank)
                .ToList();

            bool needsConfirmation = false;
            (string Key, int Index, int Rank)? chosen = null;

            if (mappingOverride?.IdColumn is { Length: > 0 } manualId)
            {
                string wanted = TextNormalizer.NormalizeKey(manualId);
                var match = idCandidates.FirstOrDefault(c => c.Key == wanted);
                if (match.Key != null)
                {
                    chosen = match;
                    notes.Add($"ID column forced by mapping override: '{manualId}'.");
                }
                else
                {
                    notes.Add($"Mapping override points at missing id column '{manualId}' – auto-detection used.");
                }
            }

            if (chosen == null && idCandidates.Count > 0)
            {
                chosen = idCandidates[0];
                int bestRank = idCandidates[0].Rank;
                var sameRank = idCandidates.Where(c => c.Rank == bestRank).ToList();
                if (sameRank.Count > 1)
                {
                    needsConfirmation = true;
                    notes.Add(
                        "More than one id column (" +
                        string.Join(", ", sameRank.Select(c => $"'{c.Key}'")) +
                        ") – please confirm the mapping in the Sources panel.");
                }
            }

            if (chosen != null) columns[CatalogField.Id] = chosen.Value.Index;

            /* ---- regular fields --------------------------------------------- */
            MapField(CatalogField.Hex, "HEX", "ITEMHEX", "IDHEX");
            MapField(CatalogField.Name, "NAME", "INTERNALNAME", "DEVNAME");
            MapField(CatalogField.Display, "LOCALIZEDDISPLAYNAME", "DISPLAYNAME", "LOCALIZEDNAME", "DISPLAY");
            MapField(CatalogField.Type, "TYPE", "ITEMTYPE");
            MapField(CatalogField.Category, "CATEGORY", "ITEMCATEGORY");
            MapField(CatalogField.Subtype, "SUBTYPE", "ITEMSUBTYPE");
            MapField(CatalogField.Notes, "NOTES", "NOTE", "COMMENT");
            MapField(CatalogField.Patch, "PATCHADDED", "PATCH");
            MapField(CatalogField.Dlc, "DLC");
            MapField(CatalogField.Image, "IMAGE", "ICON");
            MapField(CatalogField.CatalogueCategory, "CATALOGUECATEGORY");

            void MapField(string field, params string[] aliases)
            {
                if (mappingOverride?.Columns.TryGetValue(field, out string? manualHeader) == true &&
                    manualHeader is { Length: > 0 })
                {
                    string wanted = TextNormalizer.NormalizeKey(manualHeader);
                    if (header.TryGetValue(wanted, out var manualIndexes))
                    {
                        columns[field] = manualIndexes[0];
                        notes.Add($"{field} column forced by mapping override: '{manualHeader}'.");
                        return;
                    }
                    notes.Add($"Mapping override for {field} points at missing column '{manualHeader}'.");
                }

                foreach (string alias in aliases)
                    if (header.TryGetValue(alias, out var indexes))
                    {
                        columns[field] = indexes[0];
                        return;
                    }
            }

            // The id column must not be reused as another field (e.g. "Item ID" also matching "ID").
            if (columns.TryGetValue(CatalogField.Id, out int idIndex))
            {
                foreach (string field in columns.Keys.ToList())
                    if (field != CatalogField.Id && columns[field] == idIndex)
                        columns.Remove(field);
            }

            /* ---- decide whether "Name" means display or internal name -------- */
            bool hasLocalized = columns.ContainsKey(CatalogField.Display);
            bool hasType = columns.ContainsKey(CatalogField.Type);
            bool dumpLike = hasLocalized || hasType;
            bool nameIsDisplay = false;

            if (!hasLocalized && columns.TryGetValue(CatalogField.Name, out int nameIndex))
            {
                if (!hasType)
                {
                    // Rubyelf-style list: a single "Name" column is what the player sees.
                    nameIsDisplay = true;
                    columns[CatalogField.Display] = nameIndex;
                    columns.Remove(CatalogField.Name);
                    notes.Add("Single 'Name' column without TYPE/LOCALIZED_DISPLAY_NAME – treated as display name.");
                }
                else
                {
                    columns[CatalogField.Display] = nameIndex;
                    notes.Add("No localized column – display name falls back to NAME.");
                }
            }

            if (!columns.ContainsKey(CatalogField.Display) &&
                columns.TryGetValue(CatalogField.Name, out int fallbackName))
            {
                columns[CatalogField.Display] = fallbackName;
            }

            var mapping = columns.ToDictionary(
                kv => kv.Key,
                kv => TextNormalizer.ColumnName(kv.Value),
                StringComparer.OrdinalIgnoreCase);

            return new HeaderResult
            {
                HeaderRow = headerRow,
                Columns = columns,
                Mapping = mapping,
                Notes = notes,
                NeedsIdConfirmation = needsConfirmation,
                NameIsDisplay = nameIsDisplay,
                DumpLike = dumpLike
            };
        }

        /* ================================================================= */

        private static Dictionary<string, List<int>> BuildHeaderMap(string?[] row)
        {
            var map = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int column = 0; column < row.Length; column++)
            {
                string key = TextNormalizer.NormalizeKey(row[column]);
                if (key.Length == 0) continue;
                if (!map.TryGetValue(key, out var list)) map[key] = list = new List<int>(1);
                list.Add(column);
            }
            return map;
        }

        private static bool IsIdAlias(string key)
            => IdRank.ContainsKey(key) || AliasesFor(key).Contains(CatalogField.Id, StringComparer.OrdinalIgnoreCase);

        private static bool IsAlias(string key, string field)
        {
            if (field == CatalogField.Id) return IsIdAlias(key);
            return AliasesFor(key).Contains(field, StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> AliasesFor(string key)
        {
            if (AliasToField.TryGetValue(key, out string? field))
                yield return field;
        }

        private static int RankOf(string key) => IdRank.TryGetValue(key, out int rank) ? rank : 9;

        private static Dictionary<string, string> BuildAliases()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            void Add(string field, params string[] aliases)
            {
                foreach (string alias in aliases)
                    map[TextNormalizer.NormalizeKey(alias)] = field;
            }

            Add(CatalogField.Id, "ID", "Item ID", "ItemID", "Object ID", "Item Identifier");
            Add(CatalogField.Hex, "Hex", "Item Hex", "ID Hex");
            Add(CatalogField.Name, "Name", "Internal Name", "Dev Name");
            Add(CatalogField.Display, "Display Name", "Localized Display Name", "Localized Name", "Loc Name", "Display");
            Add(CatalogField.Type, "Type", "Item Type");
            Add(CatalogField.Category, "Category", "Item Category");
            Add(CatalogField.Subtype, "Subtype", "Item Subtype");
            Add(CatalogField.Notes, "Notes", "Note", "Comment");
            Add(CatalogField.Patch, "Patch Added", "Patch");
            Add(CatalogField.Dlc, "DLC");
            Add(CatalogField.Image, "Image", "Icon");
            Add(CatalogField.CatalogueCategory, "Catalogue Category");

            return map;
        }
    }
}
