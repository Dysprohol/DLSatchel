using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace DDV_SaveEditor_G.Modules
{
    /// <summary>
    /// Every item ID the player owns or has unlocked, with the part of the save it is in.
    /// Used to highlight owned items and to list items the item lists do not know yet.
    /// </summary>
    public static class SaveItemIds
    {
        public static Dictionary<long, string> Collect(JObject? root)
        {
            var ids = new Dictionary<long, string>();
            if (root?["Player"] is not JObject player) return ids;

            if (player["ListInventories"] is JObject inventories)
                foreach (var inventory in inventories.Properties())
                {
                    string type = inventory.Value["CompatibleItemType"]?.ToString() ?? "";
                    type = type.StartsWith(ListInventoryModule.ItemTypePrefix) ? type[ListInventoryModule.ItemTypePrefix.Length..] : type;
                    string section = type.Length > 0 ? "Storage: " + type : "Storage";
                    if (inventory.Value["Inventory"] is JObject items)
                        foreach (var item in items.Properties()) Add(ids, item.Name, section);
                }

            // Only the player's backpack, never other ContainerInventories (character data).
            if (player["ContainerInventories"]?["0"]?["Inventory"] is JArray backpack)
                foreach (var item in backpack) Add(ids, item?["ItemID"]?.ToString(), "Backpack");
            AddArray(ids, player["Pets"] as JArray, "PetItemID", "Companions");
            AddArray(ids, player["Mounts"] as JArray, "MountItemID", "Mounts");
            AddArray(ids, player["Tools"] as JArray, "ToolItemID", "Tools & accessories");
            AddArray(ids, player["PremadeDecorationPresets"]?["DecorationPresets"] as JArray, "PresetID", "Presets");
            AddArray(ids, player["Cookbook"]?["Recipes"] as JArray, "RecipeID", "Recipes");
            AddArray(ids, player["UnlockedCraftingRecipes"] as JArray, "RecipeID", "Crafting recipes");
            if (player["CurrencyAmounts"] is JObject currencies)
                foreach (var currency in currencies.Properties()) Add(ids, currency.Name, "Currencies");

            // CollectionSets are the game's explicit collected/unlocked flags; read only.
            if (player["CollectionSets"] is JArray sets)
                foreach (var items in sets.OfType<JObject>()
                             .SelectMany(set => set["GroupData"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                             .Select(group => group["GroupsCollectionItems"] as JObject))
                    if (items != null)
                        foreach (var item in items.Properties())
                            if (item.Value.Type == JTokenType.Boolean && item.Value.Value<bool>()) Add(ids, item.Name, "Collection");
            return ids;
        }

        /// <summary>Items in the save that the loaded item lists do not know, grouped by section.</summary>
        public static List<(long Id, string Section)> NotInCatalog(JObject? root) =>
            Collect(root)
                .Where(kv => ItemDatabase.Find(kv.Key) == null)
                .Select(kv => (kv.Key, kv.Value))
                .OrderBy(x => x.Value).ThenBy(x => x.Key)
                .ToList();

        private static void AddArray(Dictionary<long, string> ids, JArray? values, string objectKey, string section)
        {
            if (values == null) return;
            foreach (var value in values)
            {
                if (value is JObject obj)
                {
                    Add(ids, obj[objectKey]?.ToString(), section);
                    if (obj[objectKey] == null && obj["ItemID"] != null) Add(ids, obj["ItemID"]!.ToString(), section);
                }
                else Add(ids, value.ToString(), section);
            }
        }

        private static void Add(Dictionary<long, string> ids, string? value, string section)
        {
            if (long.TryParse(value, out long id) && id > 0) ids.TryAdd(id, section);
        }
    }
}
