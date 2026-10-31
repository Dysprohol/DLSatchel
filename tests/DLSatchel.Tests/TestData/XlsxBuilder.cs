using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace DDV.SaveEditor.Tests
{
    /// <summary>
    /// Writes minimal .xlsx workbooks for tests. Values are inline strings, so
    /// no shared-string table is needed and the file stays human-readable.
    /// </summary>
    internal static class XlsxBuilder
    {
        public static void Create(string path, params (string Name, string?[][] Rows)[] sheets)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            var sheetsElement = workbookPart.Workbook.AppendChild(new Sheets());

            uint sheetId = 1;
            foreach (var (name, rows) in sheets)
            {
                var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                var sheetData = new SheetData();

                for (int r = 0; r < rows.Length; r++)
                {
                    var row = new Row { RowIndex = (uint)(r + 1) };
                    var values = rows[r];

                    for (int c = 0; c < values.Length; c++)
                    {
                        string? value = values[c];
                        if (string.IsNullOrEmpty(value)) continue;

                        row.Append(new Cell
                        {
                            CellReference = ColumnName(c) + (r + 1),
                            DataType = CellValues.InlineString,
                            InlineString = new InlineString(new Text(value))
                        });
                    }

                    sheetData.Append(row);
                }

                worksheetPart.Worksheet = new Worksheet(sheetData);
                sheetsElement.Append(new Sheet
                {
                    Id = workbookPart.GetIdOfPart(worksheetPart),
                    SheetId = sheetId++,
                    Name = name
                });
            }

            workbookPart.Workbook.Save();
        }

        public static string ColumnName(int zeroBased)
        {
            string name = "";
            int value = zeroBased + 1;
            while (value > 0)
            {
                int remainder = (value - 1) % 26;
                name = (char)('A' + remainder) + name;
                value = (value - 1) / 26;
            }
            return name;
        }

        /// <summary>Creates a unique temp folder for one test and returns it.</summary>
        public static string NewTempDirectory()
        {
            string path = Path.Combine(
                Path.GetTempPath(), "ddv-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        public static string WriteCsv(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }
    }
}
