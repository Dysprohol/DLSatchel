using DDV_SaveEditor_G.Modules;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DDV.SaveEditor.Tests
{
    public class InventoryModuleTests
    {
        [Fact]
        public void AmountEditPreservesUnrecognizedFieldsOnSameSlotItem()
        {
            var save = JObject.Parse("""
            {
              "Player": { "ContainerInventories": { "0": { "Size": 1, "Inventory": [
                { "ItemID": 50000001, "Amount": 1, "State": { "Custom": 4 }, "FutureSlotFlag": true }
              ] } } }
            }
            """);
            var table = InventoryModule.MapInventory(save)!;
            table.Rows[0]["Amount"] = 3;

            InventoryModule.UpdateJsonFromTable(save, table);

            var slot = (JObject)save["Player"]!["ContainerInventories"]!["0"]!["Inventory"]![0]!;
            Assert.Equal(3, slot["Amount"]?.Value<int>());
            Assert.Equal(4, slot["State"]?["Custom"]?.Value<int>());
            Assert.True(slot["FutureSlotFlag"]?.Value<bool>());
        }

        [Fact]
        public void ReplacingSlotItemDoesNotCopyOldItemSpecificFields()
        {
            var save = JObject.Parse("""
            {
              "Player": { "ContainerInventories": { "0": { "Size": 1, "Inventory": [
                { "ItemID": 50000001, "Amount": 1, "State": { "Custom": 4 }, "OldItemOnly": true }
              ] } } }
            }
            """);
            var table = InventoryModule.MapInventory(save)!;
            table.Rows[0]["Item ID"] = "50000002";
            table.Rows[0]["Amount"] = 1;

            InventoryModule.UpdateJsonFromTable(save, table);

            var slot = (JObject)save["Player"]!["ContainerInventories"]!["0"]!["Inventory"]![0]!;
            Assert.Equal(50000002, slot["ItemID"]?.Value<long>());
            Assert.Null(slot["OldItemOnly"]);
            Assert.Equal(JTokenType.Null, slot["State"]?.Type);
        }
    }
}
