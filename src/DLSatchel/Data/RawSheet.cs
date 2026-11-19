using System;
using System.Collections.Generic;
using System.Linq;

namespace DDV_SaveEditor_G.Data
{
    /// <summary>A sheet read into memory as raw text cells (1-based row numbers).</summary>
    public sealed class RawSheet
    {
        public RawSheet(string filePath, string sheetName, List<string?[]> rows)
        {
            FilePath = filePath;
            SheetName = sheetName;
            Rows = rows;
        }

        public string FilePath { get; }

        public string SheetName { get; }

        /// <summary>All rows including the header; row 1 is Rows[0].</summary>
        public List<string?[]> Rows { get; }

        public int RowCount => Rows.Count;

        public int MaxColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Length);

        /// <summary>Number of rows that contain at least one non-empty cell.</summary>
        public int NonEmptyRowCount => Rows.Count(r => !IsEmpty(r));

        public static bool IsEmpty(string?[]? row)
        {
            if (row == null) return true;
            foreach (string? cell in row)
                if (!string.IsNullOrWhiteSpace(cell))
                    return false;
            return true;
        }

        /// <summary>1-based row lookup; returns an empty array when out of range.</summary>
        public string?[] GetRow(int oneBasedRow)
        {
            int index = oneBasedRow - 1;
            return index >= 0 && index < Rows.Count ? Rows[index] : Array.Empty<string?>();
        }

        public string GetCell(int oneBasedRow, int zeroBasedColumn)
        {
            var row = GetRow(oneBasedRow);
            if (zeroBasedColumn < 0 || zeroBasedColumn >= row.Length) return "";
            return TextNormalizer.CleanCell(row[zeroBasedColumn]);
        }
    }
}
