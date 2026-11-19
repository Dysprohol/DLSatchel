using System;
using System.IO;
using System.Linq;
using DDV_SaveEditor_G.Data;
using Xunit;
using Xunit.Abstractions;

namespace DDV.SaveEditor.Tests
{
    public class ReaderSmokeTests
    {
        private readonly ITestOutputHelper _output;

        public ReaderSmokeTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void ReadsFirstRowsOfRealDump()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "KOSDDV - DDV ITEM DUMP.xlsx");
            if (!File.Exists(path))
                path = Path.Combine(AppContext.BaseDirectory, "pliki_testowe", "KOSDDV - DDV ITEM DUMP.xlsx");

            using var source = new XlsxWorkbookSource(path);
            _output.WriteLine($"sheets: {string.Join(", ", source.SheetNames)}");

            var sheet = source.ReadSheet("English", 10);
            _output.WriteLine($"rows read (max 10): {sheet.RowCount}");

            for (int i = 0; i < Math.Min(3, sheet.RowCount); i++)
                _output.WriteLine($"row {i + 1}: {string.Join(" | ", sheet.Rows[i].Select(v => v ?? "<null>"))}");

            Assert.True(sheet.RowCount > 0, "reader returned no rows");
            Assert.Contains("ID", sheet.Rows[0].Select(v => v ?? ""), StringComparer.OrdinalIgnoreCase);
        }
    }
}
