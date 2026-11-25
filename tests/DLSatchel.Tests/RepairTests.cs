using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DDV.SaveEditor.Tests.TestData;
using DDV_SaveEditor_G.Modules;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DDV.SaveEditor.Tests
{
    /// <summary>Restore Backup and the "flagged items added since the first backup" part of Check Save.</summary>
    public class RepairTests
    {
        private static async Task LoadCatalogAsync()
        {
            string directory = XlsxBuilder.NewTempDirectory();
            string dump = XlsxBuilder.WriteCsv(Path.Combine(directory, "dump.csv"),
                "ID,NAME,LOCALIZED_DISPLAY_NAME,TYPE\n" +
                "40000001,Furniture/Chair,Chair,Furniture\n" +
                "40000002,Blocker/InvisibleWall01,Invisible Wall,Furniture\n" +
                "40000003,Request/Story!Quest01!Box,Quest Box,Furniture\n" +
                "40000004,GroundAlteration/Road01,Old Road,Furniture\n" +
                "40000005,Furniture/BrandNewLamp,Brand New Lamp,Furniture\n" +
                "40000006,Furniture/Table,Table,Furniture\n");
            string rubyelf = XlsxBuilder.WriteCsv(Path.Combine(directory, "rubyelf.csv"),
                "Hex,Item ID,Name,Category,Notes,Catalogue Category\n" +
                "0,40000001,Chair,Furniture,,\n" +
                "0,40000002,Invisible Wall,Furniture,,\n" +
                "0,40000003,Quest Box,Furniture,,\n" +
                "0,40000004,Old Road,Furniture,,\n" +
                "0,40000006,Table,Furniture,,\n");
            var (catalog, _) = await CatalogHarness.LoadAsync((dump, 10), (rubyelf, 30));
            ItemDatabase.SetCatalog(catalog);
        }

        private static JObject Save(params long[] ids)
        {
            var inventory = new JObject();
            foreach (long id in ids) inventory[id.ToString()] = new JObject { ["Amount"] = 1, ["Marker"] = "ItemMarker_None" };
            return new JObject
            {
                ["Player"] = new JObject
                {
                    ["ListInventories"] = new JObject
                    {
                        ["0"] = new JObject { ["ID"] = 0, ["CompatibleItemType"] = "ItemType_Furniture", ["Inventory"] = inventory }
                    }
                },
                ["World"] = new JObject
                {
                    ["GridCollection"] = new JObject
                    {
                        ["Grids"] = new JObject
                        {
                            ["1"] = new JObject { ["Objects"] = new JObject { ["0"] = new JObject { ["ID"] = 0, ["ItemID"] = 40000006, ["X"] = 1, ["Y"] = 1 } } }
                        }
                    }
                }
            };
        }

        [Fact]
        public async Task OnlyFlaggedItemsAddedSinceTheBackup_AreOffered()
        {
            await LoadCatalogAsync();
            // The game itself had a quest box in storage: it must never be offered for removal.
            var before = Save(40000001, 40000003);
            // Since then: a blocker and a brand-new unchecked lamp (flagged), a road and the placed table
            // (not flagged), a chair amount change (already there).
            var now = Save(40000001, 40000003, 40000002, 40000005, 40000004, 40000006);

            var risky = RiskyAdditions.Find(now, before);

            Assert.Equal(new[] { 40000002L, 40000005L }, risky.Select(r => r.Id).OrderBy(i => i));
            Assert.Equal("game object, not a normal item", risky.Single(r => r.Id == 40000002).Reason);
            Assert.Equal("not checked by the community list", risky.Single(r => r.Id == 40000005).Reason);
            Assert.All(risky, r => Assert.Equal("0", r.ListKey));
            Assert.Empty(RiskyAdditions.Find(before, before));
        }

        [Fact]
        public void Backups_AreListedNewestFirst_AndRestoreKeepsTheCurrentSave()
        {
            string dir = XlsxBuilder.NewTempDirectory();
            string save = Path.Combine(dir, "profile.json");
            File.WriteAllText(save, "{\"Player\":{\"Level\":3}}");
            File.WriteAllText(save + ".bak", "{\"Player\":{\"Level\":1}}");
            File.WriteAllText(save + ".backup-20261101-100000-000", "{\"Player\":{\"Level\":2}}");
            File.WriteAllText(save + ".backup-20261120-090000-000", "{\"Player\":{\"Level\":2}}");
            File.WriteAllText(save + ".backup-20261120-090000-000-1", "not a save");
            File.WriteAllText(Path.Combine(dir, "other.json.backup-20261121-090000-000"), "{}");

            var backups = SaveBackups.Find(save);

            Assert.Equal(4, backups.Count);
            Assert.True(backups[0].Time >= backups[1].Time && backups[1].Time >= backups[2].Time);
            Assert.True(backups[^1].BeforeFirstEdit);
            Assert.Contains("first edit", backups[^1].Label);

            string kept = SaveBackups.Restore(save, backups[^1]);
            Assert.Equal("{\"Player\":{\"Level\":1}}", File.ReadAllText(save));
            Assert.Equal("{\"Player\":{\"Level\":3}}", File.ReadAllText(kept));
            Assert.Contains(SaveBackups.Find(save), b => b.Path == kept);

            var broken = SaveBackups.Find(save).Single(b => b.Path.EndsWith("-1", StringComparison.Ordinal));
            Assert.Throws<InvalidDataException>(() => SaveBackups.Restore(save, broken));
            Assert.Equal("{\"Player\":{\"Level\":1}}", File.ReadAllText(save));
        }

        [Fact]
        public async Task CharactersAreRefused_WithAPlainReason()
        {
            string directory = XlsxBuilder.NewTempDirectory();
            string dump = XlsxBuilder.WriteCsv(Path.Combine(directory, "dump.csv"),
                "ID,NAME,LOCALIZED_DISPLAY_NAME,TYPE\n10000209,Character/Dory,Dory,Character\n");
            var (catalog, _) = await CatalogHarness.LoadAsync((dump, 10));
            ItemDatabase.SetCatalog(catalog);

            var placement = ItemPlacement.Resolve(Save(), "10000209");

            Assert.Equal(PlacementKind.Unsupported, placement.Kind);
            Assert.Contains("character", placement.Problem, StringComparison.OrdinalIgnoreCase);
        }
    }
}
