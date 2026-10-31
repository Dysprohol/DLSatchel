using DDV_SaveEditor_G.Modules;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DDV.SaveEditor.Tests
{
    public class SaveDiffTests
    {
        [Fact]
        public void CompareReturnsChangedPathsWithoutValuesAndTreatsArraysAsOneArea()
        {
            var before = JObject.Parse("""
            { "Player": { "Level": 4, "CurrencyAmounts": { "80000000": 10 }, "Pets": [1, 2] } }
            """);
            var after = JObject.Parse("""
            { "Player": { "Level": 4, "CurrencyAmounts": { "80000000": 12 }, "Pets": [1, 2, 3] } }
            """);

            var diff = SaveDiffModule.Compare(before, after);

            Assert.Equal(new[] { "$.Player.CurrencyAmounts.80000000", "$.Player.Pets" }, diff.Paths);
            Assert.True(diff.HasChanges);
        }

        [Fact]
        public void CompareCapsLongReportsAndCountsOmittedPaths()
        {
            var before = JObject.Parse("{\"a\":0,\"b\":0,\"c\":0}");
            var after = JObject.Parse("{\"a\":1,\"b\":1,\"c\":1}");

            var diff = SaveDiffModule.Compare(before, after, maxPaths: 2);

            Assert.Equal(2, diff.Paths.Count);
            Assert.Equal(1, diff.OmittedPathCount);
        }

        [Fact]
        public void ChangedPaths_AreShownWithTheEditorsNames()
        {
            var save = JObject.Parse("""
            { "Player": { "ListInventories": {
                "0": { "CompatibleItemType": "ItemType_Furniture" },
                "4": { "CompatibleItemType": "ItemType_Trimming" },
                "1": { "CompatibleItemType": "ItemType_Clothing" } } } }
            """);

            var names = SaveDiffModule.ReadableSections(save, new[]
            {
                "$.Player.ContainerInventories.0.Inventory",
                "$.Player.ContainerInventories.0.Size",
                "$.Player.ListInventories.0.Inventory.40000047",
                "$.Player.ListInventories.4.Inventory.160000005",
                "$.Player.ListInventories.1.Inventory.50000320",
                "$.Player.CurrencyAmounts.80000000",
                "$.Player.CurrencyAmounts.99999999",
                "$.Player.Pets",
                "$.Player.Xp",
                "$.Player.SomethingNew"
            });

            Assert.Equal(new[]
            {
                "Backpack", "Furniture", "Trimmings", "Wardrobe", "Star Coins", "Currencies",
                "Companions", "XP", "Player › SomethingNew"
            }, names);
        }

        [Fact]
        public void CompareIdenticalSavesHasNoChanges()
        {
            var save = JObject.Parse("{\"Player\":{\"Level\":4}}");
            Assert.False(SaveDiffModule.Compare(save, save.DeepClone()).HasChanges);
        }
    }
}
