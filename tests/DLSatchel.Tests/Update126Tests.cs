using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DDV.SaveEditor.Tests.TestData;
using DDV_SaveEditor_G.Data;
using DDV_SaveEditor_G.Modules;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DDV.SaveEditor.Tests
{
    /// <summary>Game update 1.26 (mermaid form, ship), "Add all missing" and list clean-up.</summary>
    public class Update126Tests
    {
        private static async Task LoadCatalogAsync()
        {
            string directory = XlsxBuilder.NewTempDirectory();
            string dump = XlsxBuilder.WriteCsv(Path.Combine(directory, "dump.csv"),
                "ID,NAME,LOCALIZED_DISPLAY_NAME,TYPE\n" +
                "50000001,CloTop/99_NonIP!Set01!Shirt01!color01,Plain Shirt,Clothing\n" +
                "50000002,CloTop/52_Winnie!Update24!VLG_Eeyo_020_Shirt!color01,Quest Shirt,Clothing\n" +
                "50000003,CloTop/99_NonIP!Set02!Shirt02!color01,Shop Shirt,Clothing\n" +
                "50000004,CloTop/99_NonIP!Set03!Shirt03!color01,Event Shirt,Clothing\n" +
                "50000005,CloTop/99_NonIP!Set04!Shirt04!color01,Broken Shirt,Clothing\n" +
                "50000006,CloTop/99_NonIP!Set05!Shirt05!color01,Owned Shirt,Clothing\n" +
                "50000007,CloFeet/Feet_Test,Test Shoes,Clothing\n" +
                "50000008,CloTop/99_NonIP!Set06!Shirt06!color01,Buggy Event Shirt,Clothing\n" +
                "50000009,CloTop/Event!Halloween!Shirt07,Event Path Shirt,Clothing\n" +
                "50000010,CloTop/99_NonIP!Set07!Shirt08!color01,Expansion Shirt,Clothing\n" +
                "50000011,CloTop/99_NonIP!Set08!Shirt09!color01,Premium Path Shirt,Clothing\n" +
                "51900000,MerTorso/99_NonIP!Update26!Pirate01,Bold Buccaneer,Clothing\n" +
                "51800000,BodEar/99_NonIP!Update26!BasicMermaidEars01,Merfolk Ears,Clothing\n" +
                "170500000,Boat/PirateShip01,Enchanted Pirate Ship,Skin\n" +
                "40000001,Furniture/Chair,Chair,Furniture\n");
            string rubyelf = XlsxBuilder.WriteCsv(Path.Combine(directory, "rubyelf.csv"),
                "Hex,Item ID,Name,Category,Notes,Catalogue Category\n" +
                "0,50000003,Shop Shirt,Clothes Top,,Premium\n" +
                "0,50000004,Event Shirt,Clothes Top,Star Path,\n" +
                "0,50000005,Broken Shirt,Clothes Top,DONT USE,\n" +
                "0,50000008,Buggy Event Shirt,Clothes Top,Star Path - can bug the event,\n" +
                "0,50000010,Expansion Shirt,Clothes Top,A Rift in Time DLC,\n" +
                "0,50000011,Premium Path Shirt,Clothes Top,Premium Star Path,\n");
            var (catalog, _) = await CatalogHarness.LoadAsync((dump, 10), (rubyelf, 30));
            ItemDatabase.SetCatalog(catalog);
        }

        private static JObject Save(bool mermaid = false, bool ship = false) => JObject.Parse($$"""
            { "Player": {
                "FemaleMermaidAvatar": {{(mermaid ? "{ \"Id\": 0, \"Clothes\": {} }" : "null")}},
                "MaleMermaidAvatar": null,
                "ListInventories": {
                  "0": { "ID": 0, "CompatibleItemType": "ItemType_Furniture", "Inventory": {} },
                  "1": { "ID": 1, "CompatibleItemType": "ItemType_Clothing", "Inventory": { "50000006": { "Amount": 1, "Marker": "ItemMarker_None" } } },
                  "7": { "ID": 7, "CompatibleItemType": "ItemType_Skin", "Inventory": {} } } },
              "World": { "BoatData": {{(ship ? "{ }" : "null")}} } }
            """);

        [Fact]
        public async Task MermaidPartsAndShipSkins_GoToWardrobeAndSkins_WithAWarningUntilUnlocked()
        {
            await LoadCatalogAsync();

            var locked = Save();
            var tail = ItemPlacement.Resolve(locked, "51900000");
            Assert.Equal((PlacementKind.ListInventory, "1"), (tail.Kind, tail.ListKey));
            Assert.Contains("mermaid form", tail.Warning);
            Assert.Contains("mermaid form", ItemPlacement.Resolve(locked, "51800000").Warning);
            var ship = ItemPlacement.Resolve(locked, "170500000");
            Assert.Equal((PlacementKind.ListInventory, "7"), (ship.Kind, ship.ListKey));
            Assert.Contains("ship", ship.Warning);
            Assert.Null(ItemPlacement.Resolve(locked, "50000001").Warning);

            var unlocked = Save(mermaid: true, ship: true);
            Assert.Null(ItemPlacement.Resolve(unlocked, "51900000").Warning);
            Assert.Null(ItemPlacement.Resolve(unlocked, "170500000").Warning);

            Assert.Equal(SafetyState.Caution, ItemDatabase.FindRecord(51900000)!.Safety);
            Assert.Equal(SafetyState.Caution, ItemDatabase.FindRecord(170500000)!.Safety);
        }

        [Fact]
        public async Task AddAllMissing_GroupsTheWardrobeAndSkipsOwnedUnsafeAndTestItems()
        {
            await LoadCatalogAsync();
            var save = Save();

            var missing = MissingItems.Find(save, "1", new HashSet<long> { 50000006 })
                .ToDictionary(m => m.Id, m => m.Group);

            Assert.Equal(MissingGroup.Ordinary, missing[50000001]);
            Assert.Equal(MissingGroup.Quest, missing[50000002]);
            Assert.Equal(MissingGroup.Shop, missing[50000003]);
            Assert.Equal(MissingGroup.Event, missing[50000004]);
            Assert.Equal(MissingGroup.Unlock, missing[51900000]);
            Assert.Equal(MissingGroup.Unlock, missing[51800000]);
            Assert.False(missing.ContainsKey(50000005));   // DONT USE → unsafe
            Assert.Equal(MissingGroup.Other, missing[50000008]);   // "can bug" wins over Star Path
            Assert.Equal(MissingGroup.Other, missing[50000009]);   // Event! progression item, as a single add warns
            Assert.Equal(MissingGroup.Dlc, missing[50000010]);
            Assert.Equal(MissingGroup.Shop, missing[50000011]);    // a premium Star Path reward is not a free event item
            Assert.False(missing.ContainsKey(50000006));   // owned
            Assert.False(missing.ContainsKey(50000007));   // test item
            Assert.False(missing.ContainsKey(40000001));   // other collection
            Assert.False(missing.ContainsKey(170500000));  // skins, not wardrobe

            Assert.False(MissingItems.CheckedByDefault(MissingGroup.Quest));
            Assert.False(MissingItems.CheckedByDefault(MissingGroup.Unlock));
            Assert.False(MissingItems.CheckedByDefault(MissingGroup.Shop));
            Assert.False(MissingItems.CheckedByDefault(MissingGroup.Dlc));
        }

        [Fact]
        public void ItemsPlacedInTheValley_CountAsOwned()
        {
            var save = JObject.Parse("""
                { "World": { "GridCollection": {
                    "Grids": { "1": { "Objects": { "0": { "ID": 0, "ItemID": 40000001, "X": 3, "Y": 4, "State": {} } } } },
                    "DiffGrids": { "16": { "ModifiedObjects": [ { "ID": 2, "ItemID": 20000022, "X": 1, "Y": 1 } ] } } } } }
                """);

            Assert.Equal(new long[] { 20000022, 40000001 }, SaveItemIds.PlacedInWorld(save).OrderBy(i => i).ToArray());
        }

        [Fact]
        public async Task CatalogueCategory_SurvivesTheCache_SoGroupsStayTheSame()
        {
            string directory = XlsxBuilder.NewTempDirectory();
            string rubyelf = XlsxBuilder.WriteCsv(Path.Combine(directory, "rubyelf.csv"),
                "Hex,Item ID,Name,Category,Notes,Catalogue Category\n0,50000003,Shop Shirt,Clothes Top,,Premium\n");
            var config = new SourcesConfig { Sources = { new CatalogSourceConfig { Path = rubyelf, Priority = 30, Enabled = true } } };
            var options = new CatalogLoadOptions
            {
                SourcesPath = Path.Combine(directory, "sources.json"),
                CacheDirectory = Path.Combine(directory, "cache"),
                UseCache = true
            };

            var built = await ItemCatalog.LoadFromConfigAsync(config, options);
            var cached = await ItemCatalog.LoadFromConfigAsync(config, options);

            Assert.False(built.Diagnostics.FromCache);
            Assert.True(cached.Diagnostics.FromCache);
            Assert.Equal("Premium", cached.Get(50000003)!.CatalogueCategory);
        }

        [Fact]
        public void CleanUp_RemovesOffAndMissingLists_ButDeletesOnlyTheEditorsOwnFiles()
        {
            string root = XlsxBuilder.NewTempDirectory();
            string editor = Path.Combine(root, "DDVSaveEditor");
            string backups = Path.Combine(editor, "lists", "previous");
            string elsewhere = Path.Combine(root, "MyLists");
            Directory.CreateDirectory(backups);
            Directory.CreateDirectory(elsewhere);
            Directory.CreateDirectory(Path.Combine(editor, "Catalog"));

            string used = Touch(Path.Combine(elsewhere, "KOSDDV - DDV ITEM DUMP.xlsx"));
            string oldCopy = Touch(Path.Combine(editor, "Catalog", "KOSDDV - DDV ITEM DUMP.xlsx"));
            string offElsewhere = Touch(Path.Combine(elsewhere, "companies.xlsx"));
            string backup = Touch(Path.Combine(backups, "Rubyelf.xlsx"));
            var config = new SourcesConfig
            {
                Sources =
                {
                    new CatalogSourceConfig { Path = used, Enabled = true },
                    new CatalogSourceConfig { Path = oldCopy, Enabled = false },
                    new CatalogSourceConfig { Path = offElsewhere, Enabled = false },
                    new CatalogSourceConfig { Path = Path.Combine(elsewhere, "gone.xlsx"), Enabled = true }
                }
            };

            var plan = SourceCleanup.Plan(config, editor, backups);

            Assert.Equal(new[] { oldCopy, offElsewhere, Path.Combine(elsewhere, "gone.xlsx") },
                         plan.Remove.Select(s => s.Path).ToArray());
            Assert.Equal(new[] { oldCopy, backup }.OrderBy(p => p), plan.DeleteFiles.OrderBy(p => p));
        }

        private static string Touch(string path)
        {
            File.WriteAllText(path, "x");
            return path;
        }
    }
}
