using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DDV.SaveEditor.Tests.TestData;
using DDV_SaveEditor_G.Data;
using DDV_SaveEditor_G.Modules;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DDV.SaveEditor.Tests
{
    public class ItemPlacementTests
    {
        [Fact]
        public async Task EveryListInventoryTypeUsesTheKeyDeclaredByTheSave()
        {
            var types = new[]
            {
                "Furniture", "Clothing", "Makeup", "Trimming", "Building",
                "TouchOfMagic", "Skin", "BoardGame", "AvatarFeature", "PhotoMode", "MountGear"
            };
            var csv = new StringBuilder("ID,NAME,LOCALIZED_DISPLAY_NAME,TYPE\n");
            for (int i = 0; i < types.Length; i++)
                csv.AppendLine($"{41000000 + i},Default/Test{i},Test {i},{types[i]}");

            string directory = XlsxBuilder.NewTempDirectory();
            string source = XlsxBuilder.WriteCsv(Path.Combine(directory, "items.csv"), csv.ToString());
            var (catalog, _) = await CatalogHarness.LoadAsync((source, 10));
            ItemDatabase.SetCatalog(catalog);

            var inventories = new JObject();
            for (int i = 0; i < types.Length; i++)
                inventories[i.ToString()] = new JObject
                {
                    ["CompatibleItemType"] = $"ItemType_{types[i]}",
                    ["Inventory"] = new JObject()
                };
            var save = new JObject { ["Player"] = new JObject { ["ListInventories"] = inventories } };

            for (int i = 0; i < types.Length; i++)
            {
                var placement = ItemPlacement.Resolve(save, (41000000 + i).ToString());
                Assert.Equal(PlacementKind.ListInventory, placement.Kind);
                Assert.Equal(i.ToString(), placement.ListKey);
                Assert.Null(placement.Problem);
            }

            Assert.Equal(PlacementHint.Storage, catalog.Get(41000008)!.Placement);
        }

        [Fact]
        public async Task UnsupportedKnownTypesAreMarkedUnknownAndNotRoutedIntoInventory()
        {
            string directory = XlsxBuilder.NewTempDirectory();
            string source = XlsxBuilder.WriteCsv(
                Path.Combine(directory, "items.csv"),
                "ID,NAME,LOCALIZED_DISPLAY_NAME,TYPE\n" +
                "42000001,MemoryShard/Memory01,Memory shard,MemoryShard\n" +
                "42000002,Upgrade/Tool01,Tool upgrade,Upgrade\n" +
                "42000004,MiningData/Mining01,Mining data,ActivityData\n" +
                "42000005,Character/Mickey,Character,Character\n");
            var (catalog, _) = await CatalogHarness.LoadAsync((source, 10));
            ItemDatabase.SetCatalog(catalog);

            var save = JObject.Parse("""
            {
              "Player": { "ListInventories": {} }
            }
            """);

            foreach (long id in new[] { 42000001L, 42000002L, 42000004L, 42000005L })
            {
                Assert.Equal(id == 42000005L ? SafetyState.Caution : SafetyState.Unknown, catalog.Get(id)!.Safety);
                Assert.Equal(PlacementKind.Unsupported, ItemPlacement.Resolve(save, id.ToString()).Kind);
            }
        }

        [Theory]
        [InlineData("Mission!Reward", 42000011L)]
        [InlineData("Event!Reward", 42000012L)]
        [InlineData("Character/Mickey", 42000013L)]
        public async Task MissionEventAndCharacterRecordsWarnButKeepAnOverridePath(string name, long id)
        {
            string directory = XlsxBuilder.NewTempDirectory();
            string type = name.StartsWith("Character/", StringComparison.OrdinalIgnoreCase) ? "Character" : "ActivityItem";
            string source = XlsxBuilder.WriteCsv(Path.Combine(directory, "items.csv"),
                "ID,NAME,LOCALIZED_DISPLAY_NAME,TYPE\n" +
                $"{id},{name},Progress reward,{type}\n");
            var (catalog, _) = await CatalogHarness.LoadAsync((source, 10));
            ItemDatabase.SetCatalog(catalog);

            var save = JObject.Parse("""
            { "Player": { "ContainerInventories": { "0": { "Inventory": [] } } } }
            """);
            Assert.Equal(SafetyState.Caution, catalog.Get(id)!.Safety);
            Assert.NotNull(ItemPlacement.GetRiskWarning(ItemDatabase.Find(id)));
            if (type == "ActivityItem")
            {
                Assert.Equal(PlacementKind.Backpack, ItemPlacement.Resolve(save, id.ToString()).Kind);
                Assert.Contains("Mission/Event/Character", ItemPlacement.GetRiskWarning(ItemDatabase.Find(id)));
            }
            else
            {
                Assert.Equal(PlacementKind.Unsupported, ItemPlacement.Resolve(save, id.ToString()).Kind);
                Assert.Contains("Character", ItemPlacement.GetRiskWarning(ItemDatabase.Find(id)));
            }
        }

        [Fact]
        public async Task MountAdditionUsesMinimalRecordAndProtectsExistingMountData()
        {
            string directory = XlsxBuilder.NewTempDirectory();
            string source = XlsxBuilder.WriteCsv(Path.Combine(directory, "items.csv"),
                "ID,NAME,LOCALIZED_DISPLAY_NAME,TYPE\n" +
                "42000003,Mount/TestHorse,Test Mount,Companion\n");
            var (catalog, _) = await CatalogHarness.LoadAsync((source, 10));
            ItemDatabase.SetCatalog(catalog);

            var save = JObject.Parse("""
            {
              "Player": {
                "Mounts": [ { "MountItemID": 42000004, "BondingLevel": 5, "CustomField": "keep" } ],
                "CollectionSets": [ { "GroupData": [ { "GroupsCollectionItems": { "42000003": false } } ] } ],
                "ContainerInventories": { "0": { "Inventory": [] }, "9": { "CharacterData": true } }
              }
            }
            """);

            Placement placement = ItemPlacement.Resolve(save, "42000003");
            Assert.Equal(PlacementKind.Mount, placement.Kind);
            Assert.Contains("EXPERIMENTAL", placement.Warning);
            Assert.True(MountModule.AddMount(save, 42000003));
            Assert.False(MountModule.AddMount(save, 42000003));
            Assert.Equal(42000003, save["Player"]?["Mounts"]?[1]?["MountItemID"]?.Value<long>());
            Assert.Single((JObject)save["Player"]!["Mounts"]![1]!);
            Assert.Equal(5, save["Player"]?["Mounts"]?[0]?["BondingLevel"]?.Value<int>());
            Assert.Equal("keep", save["Player"]?["Mounts"]?[0]?["CustomField"]?.Value<string>());
            Assert.Equal(1, ItemPlacement.MarkCollected(save, 42000003));
            Assert.True(save["Player"]?["CollectionSets"]?[0]?["GroupData"]?[0]?["GroupsCollectionItems"]?["42000003"]?.Value<bool>());
            Assert.True((bool)save["Player"]?["ContainerInventories"]?["9"]?["CharacterData"]!);

            var issues = SaveValidationModule.Analyze(save);
            Assert.Contains(issues, issue => issue.Level == SaveIssueLevel.Caution && issue.Message.Contains("only MountItemID"));
        }

        [Fact]
        public async Task PlumeriaFlowerPath_GoesToFurnitureGroundAlterationInventory()
        {
            string directory = XlsxBuilder.NewTempDirectory();
            string source = XlsxBuilder.WriteCsv(
                Path.Combine(directory, "items.csv"),
                "ID,NAME,LOCALIZED_DISPLAY_NAME,TYPE,CATEGORY\n" +
                "40200507,Landscaping/Plant!MoanaPlumeriaFlowerPath01,Plumeria Flower Path,Furniture,Landscaping Path\n");

            var (catalog, _) = await CatalogHarness.LoadAsync((source, 10));
            ItemDatabase.SetCatalog(catalog);
            Assert.Equal("Caution", catalog.Get(40200507)!.Safety.ToString());

            var save = JObject.Parse("""
            {
              "Player": {
                "ContainerInventories": {
                  "0": { "Size": 21, "Inventory": [] }
                },
                "ListInventories": {
                  "0": {
                    "CompatibleItemType": "ItemType_Furniture",
                    "CompatibleFurnitureTypes": {
                      "List": ["FurnitureItemType_GroundAlteration"]
                    },
                    "Inventory": {}
                  }
                }
              }
            }
            """);

            Placement placement = ItemPlacement.Resolve(save, "40200507");

            Assert.Equal(PlacementKind.ListInventory, placement.Kind);
            Assert.Equal("0", placement.ListKey);
            Assert.Null(placement.Problem);
            Assert.Contains("not yet been confirmed", placement.Warning);

            var furniture = ListInventoryModule.Map(save, placement.ListKey!);
            furniture.Rows.Add(40200507L, "?", "?", 1, "?");
            ListInventoryModule.Save(save, placement.ListKey!, furniture);

            Assert.Equal(1, save["Player"]?["ListInventories"]?["0"]?["Inventory"]?["40200507"]?["Amount"]?.Value<int>());
            Assert.Equal("ItemMarker_None", save["Player"]?["ListInventories"]?["0"]?["Inventory"]?["40200507"]?["Marker"]?.Value<string>());
            Assert.Empty((JArray)save["Player"]!["ContainerInventories"]!["0"]!["Inventory"]!);
        }

        [Fact]
        public async Task RubyelfStyleCatalogFillsCategoryWithoutTypeColumn()
        {
            // Rubyelf has only a display Name and a human Category: no internal NAME, no TYPE.
            string directory = XlsxBuilder.NewTempDirectory();
            string source = XlsxBuilder.WriteCsv(
                Path.Combine(directory, "rubyelf.csv"),
                "Hex,Item ID,Name,Category,Notes\n" +
                "2625A00,40000000,Wooden Chair,Furniture,\n");
            var (catalog, _) = await CatalogHarness.LoadAsync((source, 30));
            ItemDatabase.SetCatalog(catalog);

            var save = JObject.Parse("""
            {
              "Player": {
                "ListInventories": {
                  "0": {
                    "CompatibleItemType": "ItemType_Furniture",
                    "Inventory": {
                      "40000000": { "Amount": 1, "Marker": "ItemMarker_None" },
                      "40009999": { "Amount": 2, "Marker": "ItemMarker_None" }
                    }
                  }
                }
              }
            }
            """);

            var table = ListInventoryModule.Map(save, "0");
            var known = table.AsEnumerable().Single(r => r.Field<long>("Item ID") == 40000000);
            var missing = table.AsEnumerable().Single(r => r.Field<long>("Item ID") == 40009999);

            Assert.Equal("Wooden Chair", known["Localized Name"]);
            Assert.Equal("—", known["Name"]);
            Assert.Equal("Furniture", known["Category"]);
            Assert.Equal("?", missing["Name"]);
            Assert.Equal("(not in catalog)", missing["Localized Name"]);
            Assert.Equal("Furniture", missing["Category"]);
        }

        [Fact]
        public async Task PixelDustIsEditableAndMarkedWithCaution()
        {
            string directory = XlsxBuilder.NewTempDirectory();
            string source = XlsxBuilder.WriteCsv(
                Path.Combine(directory, "items.csv"),
                "ID,NAME,LOCALIZED_DISPLAY_NAME,TYPE\n" +
                "80200002,Currency/PixelDust,Pixel Dust,Currency\n");
            var (catalog, _) = await CatalogHarness.LoadAsync((source, 10));
            ItemDatabase.SetCatalog(catalog);
            Assert.Equal(SafetyState.Caution, catalog.Get(80200002)!.Safety);
            var save = JObject.Parse("""
            { "Player": { "CurrencyAmounts": { "80200002": 17 } } }
            """);

            var placement = ItemPlacement.Resolve(save, "80200002");
            Assert.Equal(PlacementKind.Currency, placement.Kind);
            Assert.Equal(117, ItemPlacement.AddCurrency(save, 80200002, 100));
            Assert.Equal(117, save["Player"]?["CurrencyAmounts"]?["80200002"]?.Value<int>());

            PlayerModule.SavePlayerData(save, new Dictionary<string, string> { ["PixelDust"] = "999999" });
            Assert.Equal(999999, save["Player"]?["CurrencyAmounts"]?["80200002"]?.Value<int>());
        }
    }
}
