using System.IO;
using System.Threading.Tasks;
using DDV.SaveEditor.Tests.TestData;
using DDV_SaveEditor_G.Data;
using DDV_SaveEditor_G.Modules;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DDV.SaveEditor.Tests
{
    public class SaveValidationTests
    {
        [Fact]
        public async Task AnalyzeFindsMisplacedAndUnknownItemsWithoutMutatingSave()
        {
            string dir = XlsxBuilder.NewTempDirectory();
            string file = XlsxBuilder.WriteCsv(Path.Combine(dir, "items.csv"),
                "ID,NAME,LOCALIZED_DISPLAY_NAME,TYPE\n" +
                "41000001,Default/Furniture,Furniture,Furniture\n" +
                "41000002,Currency/Coin,Coin,Currency\n" +
                "41000003,Mount/TestHorse,Test Mount,Companion\n");
            var (catalog, _) = await CatalogHarness.LoadAsync((file, 1));
            ItemDatabase.SetCatalog(catalog);

            var save = JObject.Parse("""
            {
              "Player": {
                "ContainerInventories": { "0": { "Inventory": [ { "ItemID": 41000002, "Amount": 1, "State": null } ] } },
                "Mounts": [ { "MountItemID": 41000003, "BondingLevel": 2, "Unknown": "keep" } ],
                "ListInventories": {
                  "4": { "CompatibleItemType": "ItemType_Furniture", "Inventory": { "41000001": { "Amount": 1 } } },
                  "8": { "CompatibleItemType": "ItemType_BoardGame", "Inventory": { "49999999": { "Amount": 1 } } }
                }
              }
            }
            """);
            var before = save.DeepClone();

            var issues = SaveValidationModule.Analyze(save);

            Assert.Contains(issues, issue => issue.Level == SaveIssueLevel.Error && issue.Path.Contains("ContainerInventories"));
            // Items the lists don't know are listed once, by SaveItemIds.NotInCatalog, not as a note per item.
            Assert.Contains(SaveItemIds.NotInCatalog(save), u => u.Id == 49999999);
            Assert.DoesNotContain(issues, issue => issue.Message.Contains("49999999"));
            // An ordinary mount with its full record is nothing to act on, so Check Save stays quiet about it.
            Assert.DoesNotContain(issues, issue => issue.Path == "Player.Mounts[0]");
            Assert.True(JToken.DeepEquals(before, save));
        }
    }
}
