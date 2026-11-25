using System.IO;
using System.Threading.Tasks;
using DDV.SaveEditor.Tests.TestData;
using DDV_SaveEditor_G.Data;
using Xunit;

namespace DDV.SaveEditor.Tests
{
    /// <summary>A list of items players tested in the game ("Tested" column) on top of the usual lists.</summary>
    public class TestedListTests
    {
        [Fact]
        public async Task TestResults_SetTheLabel_AndTheTestersNotesShowAsWarnings()
        {
            string directory = XlsxBuilder.NewTempDirectory();
            string dump = XlsxBuilder.WriteCsv(Path.Combine(directory, "dump.csv"),
                "ID,NAME,LOCALIZED_DISPLAY_NAME,TYPE\n" +
                "40000001,Furniture/Chair,Chair,Furniture\n" +
                "40000002,Furniture/Lamp,Lamp,Furniture\n" +
                "40000003,Furniture/Table,Table,Furniture\n" +
                "40000004,Furniture/Sofa,Sofa,Furniture\n" +
                "40000005,Furniture/Bed,Bed,Furniture\n");
            string rubyelf = XlsxBuilder.WriteCsv(Path.Combine(directory, "rubyelf.csv"),
                "Hex,Item ID,Name,Category,Notes,Catalogue Category\n" +
                "0,40000001,Chair,Furniture,,\n" +
                "0,40000002,Lamp,Furniture,,\n" +
                "0,40000003,Table,Furniture,DONT USE,\n");
            // What a tester's sheet looks like: an ID, any name column, the result, when, notes.
            string tested = XlsxBuilder.WriteCsv(Path.Combine(directory, "tested.csv"),
                "Item ID,Item,Tested,Tested on,Tester notes\n" +
                "40000001,Chair,yes,2026-11-25,\n" +
                "40000002,Lamp,crash,1.26,game freezes when placed\n" +
                "40000003,Table,ok,1.26,\n" +
                "40000004,Sofa,careful,1.26,only after the quest\n" +
                "40000005,Bed,maybe,,\n");

            var (catalog, _) = await CatalogHarness.LoadAsync((dump, 10), (rubyelf, 30), (tested, 5));

            Assert.Equal(SafetyState.Tested, catalog.Get(40000001)!.Safety);
            Assert.Equal("TESTED", catalog.Get(40000001)!.SafetyText);
            Assert.Equal("2026-11-25", catalog.Get(40000001)!.TestedOn);

            Assert.Equal(SafetyState.Unsafe, catalog.Get(40000002)!.Safety);
            Assert.Contains(catalog.Get(40000002)!.Warnings, w => w.Contains("does not work") && w.Contains("freezes"));

            // A tester's "works" beats an old community warning: it is what happened in the game.
            Assert.Equal(SafetyState.Tested, catalog.Get(40000003)!.Safety);

            Assert.Equal(SafetyState.Caution, catalog.Get(40000004)!.Safety);
            Assert.Contains(catalog.Get(40000004)!.Warnings, w => w.Contains("only after the quest"));

            // "maybe" is not a test result: no promise is made.
            Assert.Equal("", catalog.Get(40000005)!.Tested);
            Assert.NotEqual(SafetyState.Tested, catalog.Get(40000005)!.Safety);

            // Add all missing: tested items get their own group, ticked at first; broken ones are never offered.
            DDV_SaveEditor_G.Modules.ItemDatabase.SetCatalog(catalog);
            var save = Newtonsoft.Json.Linq.JObject.Parse(
                "{\"Player\":{\"ListInventories\":{\"0\":{\"ID\":0,\"CompatibleItemType\":\"ItemType_Furniture\",\"Inventory\":{}}}}}");
            var missing = DDV_SaveEditor_G.Modules.MissingItems.Find(save, "0", new System.Collections.Generic.HashSet<long>());
            var chair = Assert.Single(missing, m => m.Id == 40000001);
            Assert.Equal(DDV_SaveEditor_G.Modules.MissingGroup.Tested, chair.Group);
            Assert.True(DDV_SaveEditor_G.Modules.MissingItems.CheckedByDefault(chair.Group));
            Assert.DoesNotContain(missing, m => m.Id == 40000002);
        }
    }
}
