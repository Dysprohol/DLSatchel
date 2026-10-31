using System.Collections.Generic;
using DDV_SaveEditor_G.Data;
using Xunit;

namespace DDV.SaveEditor.Tests
{
    public class HeaderDetectorTests
    {
        private static RawSheet Sheet(params string?[][] rows)
            => new("test.xlsx", "Sheet1", new List<string?[]>(rows));

        [Fact]
        public void DetectsDumpLayout()
        {
            var sheet = Sheet(
                new[] { "ID", "NAME", "LOCALIZED_DISPLAY_NAME", "TYPE", "CATEGORY", "NOTES", "PATCH" },
                new[] { "10000002.0", "Furniture/Bed", "Łóżko", "Furniture", "Furniture", "", "1.0" });

            var header = HeaderDetector.Detect(sheet);

            Assert.NotNull(header);
            Assert.Equal(1, header!.HeaderRow);
            Assert.Equal(0, header.Columns[CatalogField.Id]);
            Assert.Equal(1, header.Columns[CatalogField.Name]);
            Assert.Equal(2, header.Columns[CatalogField.Display]);
            Assert.Equal(3, header.Columns[CatalogField.Type]);
            Assert.False(header.NameIsDisplay);
            Assert.Equal("C", header.Mapping[CatalogField.Display]);
            Assert.False(header.NeedsIdConfirmation);
        }

        [Fact]
        public void DetectsRubyelfLayout_AndPrefersItemIdOverHex()
        {
            var sheet = Sheet(
                new[] { "Hex", "Item ID", "Name", "Category", "Notes", "Catalogue Category", "Image", "Patch Added" },
                new[] { "989682", "10000002", "Bed", "Furniture", "", "Furniture", "", "1.0" });

            var header = HeaderDetector.Detect(sheet);

            Assert.NotNull(header);
            Assert.Equal(1, header!.HeaderRow);
            Assert.Equal(1, header.Columns[CatalogField.Id]);      // "Item ID", not "Hex"
            Assert.Equal("B", header.Mapping[CatalogField.Id]);
            Assert.Equal(2, header.Columns[CatalogField.Display]);
            Assert.True(header.NameIsDisplay);
            Assert.Equal(0, header.Columns[CatalogField.Hex]);
        }

        [Fact]
        public void FindsHeaderBelowJunkRows()
        {
            var sheet = Sheet(
                new[] { "This list is maintained by the community" },
                new string?[] { },
                new[] { "Item ID", "Name", "Type", "Notes" },
                new[] { "10000002", "Bed", "Furniture", "" });

            var header = HeaderDetector.Detect(sheet);

            Assert.NotNull(header);
            Assert.Equal(3, header!.HeaderRow);
            Assert.Equal(1, header.Columns[CatalogField.Display]);
        }

        [Fact]
        public void DuplicateIdHeaders_NeedConfirmation()
        {
            var sheet = Sheet(
                new[] { "Item ID", "Item ID", "Name" },
                new[] { "10000002", "10000003", "Bed" });

            var header = HeaderDetector.Detect(sheet);

            Assert.NotNull(header);
            Assert.True(header!.NeedsIdConfirmation);
            Assert.Contains(header.Notes, note => note.Contains("confirm", System.StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void ManualOverride_ForcesIdColumn()
        {
            var sheet = Sheet(
                new[] { "Hex", "Item ID", "Name" },
                new[] { "989682", "10000002", "Bed" });

            var mapping = new SheetMappingOverride { IdColumn = "Hex" };
            var header = HeaderDetector.Detect(sheet, mapping);

            Assert.NotNull(header);
            Assert.Equal(0, header!.Columns[CatalogField.Id]);
            Assert.Equal("A", header.Mapping[CatalogField.Id]);
        }

        [Fact]
        public void SheetWithoutId_IsNotAnItemList()
        {
            var sheet = Sheet(new[] { "Quest", "Description" }, new[] { "A", "B" });
            Assert.Null(HeaderDetector.Detect(sheet));
        }
    }
}
