using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace DDV_SaveEditor_G.Data
{
    /// <summary>Reads one sheet from a workbook. Implementations are not thread-safe.</summary>
    public interface IWorkbookSource : IDisposable
    {
        string FilePath { get; }

        /// <summary>"xlsx", "xlsm", "csv" or "tsv".</summary>
        string Format { get; }

        IReadOnlyList<string> SheetNames { get; }

        /// <summary>Reads a sheet. maxRows &gt; 0 stops after that many rows (header peeking).</summary>
        RawSheet ReadSheet(string sheetName, int maxRows = 0);
    }

    /// <summary>
    /// Opens .xlsx/.xlsm through DocumentFormat.OpenXml only (no Excel interop,
    /// no ClosedXML). Formula cells are read from their cached value, exactly
    /// like the previous loader did.
    /// </summary>
    public sealed class XlsxWorkbookSource : IWorkbookSource
    {
        private readonly SpreadsheetDocument _document;
        private readonly WorkbookPart _workbookPart;
        private readonly Dictionary<string, string[]> _sharedStringsCache = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Sheet> _sheetsByName;
        private readonly List<string> _sheetNames;

        public XlsxWorkbookSource(string path)
        {
            FilePath = path;
            Format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

            // FileShare.ReadWrite: the workbook may be open in Excel at the same time.
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            try
            {
                _document = SpreadsheetDocument.Open(stream, false);
            }
            catch
            {
                stream.Dispose();
                throw;
            }

            _workbookPart = _document.WorkbookPart
                            ?? throw new InvalidDataException("workbook part is missing");

            var sheets = _workbookPart.Workbook.Sheets?.Elements<Sheet>().ToList() ?? new List<Sheet>();
            if (sheets.Count == 0) throw new InvalidDataException("workbook has no sheets");

            _sheetsByName = new Dictionary<string, Sheet>(StringComparer.OrdinalIgnoreCase);
            _sheetNames = new List<string>(sheets.Count);
            foreach (var sheet in sheets)
            {
                string name = sheet.Name?.Value ?? "";
                if (name.Length == 0) continue;
                _sheetNames.Add(name);
                _sheetsByName[name] = sheet;
            }
        }

        public string FilePath { get; }

        public string Format { get; }

        public IReadOnlyList<string> SheetNames => _sheetNames;

        public RawSheet ReadSheet(string sheetName, int maxRows = 0)
        {
            if (!_sheetsByName.TryGetValue(sheetName, out var sheet))
                throw new InvalidDataException($"sheet '{sheetName}' not found");

            var worksheetPart = (WorksheetPart)_workbookPart.GetPartById(sheet.Id!.Value!);
            string[] sharedStrings = GetSharedStrings();

            var rows = new List<string?[]>(capacity: 4096);

            // Plain XmlReader instead of the OpenXml object model: for ~400k rows
            // across the real workbooks this is several times faster and allocates
            // nothing per cell except the string itself.
            using var stream = worksheetPart.GetStream(FileMode.Open, FileAccess.Read);
            using var xml = XmlReader.Create(stream, XmlSettings);

            // Invariant: every helper consumes exactly its own element and leaves
            // the cursor on that element's closing tag, so the loops below advance
            // with a single Read() and never skip a node.
            while (xml.Read())
            {
                if (xml.NodeType != XmlNodeType.Element || xml.LocalName != "row") continue;

                string?[] row = xml.IsEmptyElement
                    ? Array.Empty<string?>()
                    : ReadRow(xml, sharedStrings);

                rows.Add(row);

                if (maxRows > 0 && rows.Count >= maxRows) break;
            }

            if (maxRows <= 0) TrimTrailingEmptyRows(rows);
            return new RawSheet(FilePath, sheetName, rows);
        }

        /// <summary>
        /// Reads one &lt;row&gt;. On entry the reader is on its start tag, on exit on
        /// its end tag. Holes stay null; the returned array ends at the last
        /// non-empty cell.
        /// </summary>
        private static string?[] ReadRow(XmlReader xml, string[] sharedStrings)
        {
            var cells = new List<string?>(capacity: 8);
            int nextColumn = 0;
            int maxColumn = -1;

            while (xml.Read())
            {
                if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "row") break;
                if (xml.NodeType != XmlNodeType.Element) continue;

                if (xml.LocalName != "c")
                {
                    // Leaves the cursor on this element's closing tag, like ReadCell.
                    ReadTextContent(xml);
                    continue;
                }

                int column = xml.GetAttribute("r") is string reference
                    ? TextNormalizer.ColumnIndex(reference)
                    : nextColumn;
                if (column < 0) column = nextColumn;
                nextColumn = column + 1;

                string text = ReadCell(xml, xml.GetAttribute("t"), sharedStrings);
                if (text.Length == 0) continue;

                while (cells.Count <= column) cells.Add(null);
                cells[column] = text;
                if (column > maxColumn) maxColumn = column;
            }

            if (maxColumn < 0) return Array.Empty<string?>();

            var result = new string?[maxColumn + 1];
            for (int i = 0; i <= maxColumn && i < cells.Count; i++)
                result[i] = cells[i];
            return result;
        }

        /// <summary>
        /// Reads one &lt;c&gt;. On entry the reader is on the cell start tag or an
        /// empty cell; on exit after the cell's closing tag.
        /// </summary>
        private static string ReadCell(XmlReader xml, string? cellType, string[] sharedStrings)
        {
            if (xml.IsEmptyElement) return "";

            string stored = "";
            string inline = "";

            while (xml.Read())
            {
                if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "c") break;
                if (xml.NodeType != XmlNodeType.Element) continue;

                switch (xml.LocalName)
                {
                    case "v":
                        stored = ReadTextContent(xml);
                        break;
                    case "is":
                        inline = ReadTextContent(xml);
                        break;
                    default:
                        // <f> (formula) and any extension element: its cached <v>
                        // follows as a sibling, so only its own text is discarded.
                        ReadTextContent(xml);
                        break;
                }
            }

            return cellType switch
            {
                "s" => int.TryParse(stored, out int index) &&
                       index >= 0 && index < sharedStrings.Length
                    ? sharedStrings[index]
                    : "",
                "inlineStr" => inline,
                "b" => stored == "1" ? "TRUE" : "FALSE",
                _ => stored.Length > 0 ? stored : inline
            };
        }

        /// <summary>
        /// Concatenates every text run of the current element and leaves the reader
        /// on its closing tag. Used for &lt;v&gt; and &lt;is&gt; (rich text runs).
        /// </summary>
        private static string ReadTextContent(XmlReader xml)
        {
            if (xml.IsEmptyElement) return "";

            string localName = xml.LocalName;
            var builder = new StringBuilder();
            while (xml.Read())
            {
                if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == localName) break;
                if (xml.NodeType is XmlNodeType.Text or XmlNodeType.CDATA
                    or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace)
                {
                    builder.Append(xml.Value);
                }
            }
            return builder.ToString();
        }

        private static readonly XmlReaderSettings XmlSettings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CloseInput = false
        };

        private string[] GetSharedStrings()
        {
            var part = _workbookPart.SharedStringTablePart;
            if (part?.SharedStringTable == null) return Array.Empty<string>();

            return _sharedStringsCache.TryGetValue("sst", out var cached)
                ? cached
                : _sharedStringsCache["sst"] = part.SharedStringTable
                    .Elements<SharedStringItem>()
                    .Select(i => i.InnerText ?? "")
                    .ToArray();
        }

        private static void TrimTrailingEmptyRows(List<string?[]> rows)
        {
            int last = rows.Count - 1;
            while (last >= 0 && RawSheet.IsEmpty(rows[last])) last--;
            if (last < rows.Count - 1) rows.RemoveRange(last + 1, rows.Count - last - 1);
        }

        public void Dispose() => _document.Dispose();
    }

    /// <summary>
    /// CSV/TSV reader with delimiter detection and UTF-8 ↔ Windows-1250 fallback.
    /// Handles quoted fields, escaped quotes and newlines inside quotes.
    /// </summary>
    public sealed class DelimitedWorkbookSource : IWorkbookSource
    {
        private static readonly char[] CandidateDelimiters = { ',', ';', '\t', '|' };
        private const string SingleSheetName = "CSV";
        private readonly List<string?[]> _rows;

        static DelimitedWorkbookSource()
        {
            // needed for Windows-1250 on .NET Core
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public DelimitedWorkbookSource(string path)
        {
            FilePath = path;
            string extension = Path.GetExtension(path).ToLowerInvariant();
            Format = extension.TrimStart('.');

            byte[] bytes = File.ReadAllBytes(path);
            string text = Decode(bytes, out Encoding _);
            char delimiter = extension == ".tsv" ? '\t' : DetectDelimiter(text);
            _rows = Parse(text, delimiter);
        }

        public string FilePath { get; }

        public string Format { get; }

        public IReadOnlyList<string> SheetNames { get; } = new[] { SingleSheetName };

        public RawSheet ReadSheet(string sheetName, int maxRows = 0)
            => new(FilePath, SingleSheetName,
                maxRows > 0 && _rows.Count > maxRows ? _rows.GetRange(0, maxRows) : _rows);

        public void Dispose() { }

        private static string Decode(byte[] bytes, out Encoding encoding)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                encoding = new UTF8Encoding(false);
                return encoding.GetString(bytes, 3, bytes.Length - 3);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                encoding = Encoding.Unicode;
                return encoding.GetString(bytes, 2, bytes.Length - 2);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                encoding = Encoding.BigEndianUnicode;
                return encoding.GetString(bytes, 2, bytes.Length - 2);
            }

            try
            {
                encoding = new UTF8Encoding(false, throwOnInvalidBytes: true);
                return encoding.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                encoding = Encoding.GetEncoding(1250);
                return encoding.GetString(bytes);
            }
        }

        private static char DetectDelimiter(string text)
        {
            var counts = new Dictionary<char, int>();
            foreach (char c in CandidateDelimiters) counts[c] = 0;

            int line = 0;
            bool inQuotes = false;
            foreach (char ch in text)
            {
                if (ch == '"') { inQuotes = !inQuotes; continue; }
                if (inQuotes) continue;
                if (ch == '\n')
                {
                    if (++line >= 10) break;
                    continue;
                }
                if (counts.ContainsKey(ch)) counts[ch]++;
            }

            char best = ',';
            int bestCount = -1;
            foreach (var kv in counts)
            {
                if (kv.Value > bestCount)
                {
                    best = kv.Key;
                    bestCount = kv.Value;
                }
            }
            return bestCount <= 0 ? ',' : best;
        }

        private static List<string?[]> Parse(string text, char delimiter)
        {
            var rows = new List<string?[]>();
            var current = new List<string?>();
            var field = new StringBuilder();
            bool inQuotes = false;

            void EndField()
            {
                current.Add(field.Length == 0 ? null : field.ToString());
                field.Clear();
            }

            void EndRow()
            {
                EndField();
                rows.Add(current.ToArray());
                current.Clear();
            }

            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];

                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            field.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        field.Append(ch);
                    }
                    continue;
                }

                if (ch == '"' && field.Length == 0)
                {
                    inQuotes = true;
                    continue;
                }

                if (ch == delimiter)
                {
                    EndField();
                    continue;
                }

                if (ch == '\r') continue;

                if (ch == '\n')
                {
                    EndRow();
                    continue;
                }

                field.Append(ch);
            }

            if (field.Length > 0 || current.Count > 0) EndRow();

            while (rows.Count > 0 && RawSheet.IsEmpty(rows[^1])) rows.RemoveAt(rows.Count - 1);
            return rows;
        }
    }

    public static class WorkbookSourceFactory
    {
        public static bool IsSupported(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            return extension is ".xlsx" or ".xlsm" or ".csv" or ".tsv";
        }

        public static IWorkbookSource Open(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            return extension switch
            {
                ".xlsx" or ".xlsm" => new XlsxWorkbookSource(path),
                ".csv" or ".tsv" => new DelimitedWorkbookSource(path),
                _ => throw new NotSupportedException($"unsupported item list format: {extension}")
            };
        }
    }
}
