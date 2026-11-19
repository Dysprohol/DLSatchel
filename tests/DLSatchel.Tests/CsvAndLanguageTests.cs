using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DDV_SaveEditor_G.Data;
using DDV.SaveEditor.Tests.TestData;
using Xunit;

namespace DDV.SaveEditor.Tests
{
    public class CsvAndLanguageTests
    {
        [Fact]
        public async Task SemicolonCsv_WithPolishCharacters_IsParsed()
        {
            string dir = XlsxBuilder.NewTempDirectory();
            string file = XlsxBuilder.WriteCsv(Path.Combine(dir, "pl.csv"),
                "Item ID;Name;Category;Notes\n" +
                "10000002;Łóżko z bajki;Furniture;Opis\n");

            var (catalog, _) = await CatalogHarness.LoadAsync((file, 10));

            var record = Assert.Single(catalog.All);
            Assert.Equal("Łóżko z bajki", record.DisplayName);
            Assert.Equal("Furniture", record.Category);
            Assert.Equal("Opis", record.Notes);
        }

        [Fact]
        public async Task CsvWithQuotedFieldsAndNewlineInside_IsParsed()
        {
            string dir = XlsxBuilder.NewTempDirectory();
            string file = XlsxBuilder.WriteCsv(Path.Combine(dir, "quoted.csv"),
                "ID;Name;Notes\n" +
                "10000002;\"Bed; big\";\"line one\nline two\"\n");

            var (catalog, _) = await CatalogHarness.LoadAsync((file, 10));

            var record = Assert.Single(catalog.All);
            Assert.Equal("Bed; big", record.DisplayName);
            Assert.Contains("line two", record.Notes);
        }

        [Fact]
        public async Task LanguageSheetWithoutEnglish_FillsLocalizedNames()
        {
            string dir = XlsxBuilder.NewTempDirectory();
            string file = Path.Combine(dir, "french-first.xlsx");

            XlsxBuilder.Create(file,
                ("French", new[]
                {
                    new[] { "ID", "NAME", "LOCALIZED_DISPLAY_NAME", "TYPE" },
                    new[] { "10000002", "Meal/Soup", "Soupe", "ActivityItem" }
                }),
                ("German", new[]
                {
                    new[] { "ID", "NAME", "LOCALIZED_DISPLAY_NAME", "TYPE" },
                    new[] { "10000002", "Meal/Soup", "Suppe", "ActivityItem" }
                }));

            var (catalog, _) = await CatalogHarness.LoadAsync((file, 10));

            var record = Assert.Single(catalog.All);
            Assert.Equal("Soupe", record.DisplayName);
            Assert.Equal("fr", record.DisplayNameLanguage);
            Assert.Equal("Suppe", record.LocalizedNames["de"]);
        }

        [Fact]
        public async Task LanguageSheet_DoesNotCreateRecordsThePrimaryDoesNotHave()
        {
            string dir = XlsxBuilder.NewTempDirectory();
            string file = Path.Combine(dir, "dump.xlsx");

            XlsxBuilder.Create(file,
                ("English", new[]
                {
                    new[] { "ID", "NAME", "LOCALIZED_DISPLAY_NAME", "TYPE" },
                    new[] { "10000002", "Furniture/Bed", "Bed", "Furniture" }
                }),
                ("French", new[]
                {
                    new[] { "ID", "NAME", "LOCALIZED_DISPLAY_NAME", "TYPE" },
                    new[] { "10000002", "Furniture/Bed", "Lit", "Furniture" },
                    new[] { "99999999", "Furniture/Ghost", "Fantôme", "Furniture" }
                }));

            var (catalog, _) = await CatalogHarness.LoadAsync((file, 10));

            Assert.Single(catalog.All);
            Assert.Null(catalog.Get(99999999));
            Assert.Equal("Lit", catalog.Get(10000002)!.LocalizedNames["fr"]);
        }

        [Fact]
        public async Task HistorySheet_OnlyFillsGaps_AndDoesNotResurrectRemovedIds()
        {
            string dir = XlsxBuilder.NewTempDirectory();
            string file = Path.Combine(dir, "history.xlsx");

            XlsxBuilder.Create(file,
                ("LATEST", new[]
                {
                    new[] { "ID", "NAME", "TYPE" },
                    new[] { "10000002", "", "Furniture" }
                }),
                ("Before Update - BACKUP", new[]
                {
                    new[] { "ID", "NAME", "TYPE" },
                    new[] { "10000002", "Furniture/OldBed", "Clothing" },
                    new[] { "10000003", "Furniture/OldChair", "Furniture" }
                }));

            var (catalog, _) = await CatalogHarness.LoadAsync((file, 10));

            // The backup may fill the missing NAME, but must not override TYPE.
            Assert.Equal("Furniture/OldBed", catalog.Get(10000002)!.Name);
            Assert.Equal("Furniture", catalog.Get(10000002)!.RawType);

            // Rows that exist only in the backup are gone from the game – not added.
            Assert.Single(catalog.All);
            Assert.Null(catalog.Get(10000003));
        }
    }
}
