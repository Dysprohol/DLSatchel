using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DDV_SaveEditor_G.Data;
using DDV.SaveEditor.Tests.TestData;
using Xunit;

namespace DDV.SaveEditor.Tests
{
    public class CatalogMergeTests
    {
        [Fact]
        public async Task EmptyValue_NeverOverwritesFilledOne()
        {
            string dir = XlsxBuilder.NewTempDirectory();
            string dump = XlsxBuilder.WriteCsv(Path.Combine(dir, "dump.csv"),
                "ID,NAME,LOCALIZED_DISPLAY_NAME,TYPE,CATEGORY\n" +
                "10000002,Sword/Excalibur,Excalibur,ActivityItem,\n");

            string other = XlsxBuilder.WriteCsv(Path.Combine(dir, "rubyelf.csv"),
                "Item ID,Name,Category\n" +
                "10000002,,Weapons\n");

            var (catalog, _) = await CatalogHarness.LoadAsync((dump, 10), (other, 30));

            var record = Assert.Single(catalog.All);
            Assert.Equal("Sword/Excalibur", record.Name);
            Assert.Equal("Excalibur", record.DisplayName);
            Assert.Equal("Weapons", record.Category);
            Assert.Equal("ActivityItem", record.RawType);
        }

        [Fact]
        public async Task TwoDifferentTypes_AreReportedAsConflict_AndHigherPriorityWins()
        {
            string dir = XlsxBuilder.NewTempDirectory();
            string dump = XlsxBuilder.WriteCsv(Path.Combine(dir, "dump.csv"),
                "ID,NAME,TYPE\n10000002,Bed,Furniture\n");
            string other = XlsxBuilder.WriteCsv(Path.Combine(dir, "companies.csv"),
                "ID,NAME,TYPE\n10000002,Bed,Clothing\n");

            var (catalog, _) = await CatalogHarness.LoadAsync((dump, 10), (other, 20));

            var record = Assert.Single(catalog.All);
            Assert.Equal("Furniture", record.RawType);

            var conflict = Assert.Single(
                catalog.Diagnostics.ConflictSamples, c => c.Field == CatalogField.Type);
            Assert.Equal(10000002L, conflict.Id);
            Assert.Equal("Furniture", conflict.KeptValue);
            Assert.Equal("Clothing", conflict.DroppedValue);
            Assert.True(catalog.Diagnostics.Conflicts >= 1);
        }

        [Fact]
        public async Task DuplicateInsideOneSheet_IsCountedAndSkipped()
        {
            string dir = XlsxBuilder.NewTempDirectory();
            string file = XlsxBuilder.WriteCsv(Path.Combine(dir, "dups.csv"),
                "ID,NAME\n" +
                "10000002,First\n" +
                "10000002,Second\n" +
                "10000003,Other\n");

            var (catalog, _) = await CatalogHarness.LoadAsync((file, 10));

            Assert.Equal(2, catalog.Count);
            Assert.Equal(1, catalog.Diagnostics.DuplicateIds);
            // A single "Name" column without TYPE is the display name (Rubyelf rule).
            Assert.Equal("First", catalog.Get(10000002)!.DisplayName);
        }

        [Fact]
        public async Task RejectedRows_AreReportedWithReason()
        {
            string dir = XlsxBuilder.NewTempDirectory();
            string file = XlsxBuilder.WriteCsv(Path.Combine(dir, "bad.csv"),
                "ID,NAME\n" +
                "10000002,Good\n" +
                "not-a-number,Broken\n" +
                ",Also broken\n");

            var (catalog, _) = await CatalogHarness.LoadAsync((file, 10));

            Assert.Equal(1, catalog.Count);
            var source = Assert.Single(catalog.Diagnostics.Sources);
            Assert.Equal(2, source.RowsRejected);
            Assert.Contains(source.Rejections, r => r.RawId == "not-a-number");
        }

        [Fact]
        public async Task Taxonomy_MarksDebugItemsUnsafe_AndMissingTypesUnknownNotCaution()
        {
            string dir = XlsxBuilder.NewTempDirectory();
            string file = XlsxBuilder.WriteCsv(Path.Combine(dir, "list.csv"),
                "ID,NAME,CATEGORY\n" +
                "10000002,Debug Item,CATEGORY\n" +
                "10000003,Fine Item,Furniture\n");

            var (catalog, _) = await CatalogHarness.LoadAsync((file, 10));

            Assert.Equal(SafetyState.Unsafe, catalog.Get(10000002)!.Safety);
            // No TYPE only lowers classification confidence; it is not a gameplay risk.
            Assert.Equal(SafetyState.Unknown, catalog.Get(10000003)!.Safety);
        }

        [Fact]
        public async Task MissingFile_DoesNotBreakTheWholeLoad()
        {
            string dir = XlsxBuilder.NewTempDirectory();
            string good = XlsxBuilder.WriteCsv(Path.Combine(dir, "good.csv"), "ID,NAME\n1,Bed\n");

            var (catalog, _) = await CatalogHarness.LoadAsync(
                (Path.Combine(dir, "does-not-exist.xlsx"), 5),
                (good, 10));

            Assert.Equal(1, catalog.Count);
            Assert.Single(catalog.Errors);
            Assert.Contains("does-not-exist.xlsx", catalog.Errors[0]);
        }
    }
}
