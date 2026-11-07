using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

namespace DDV_SaveEditor_G.Modules
{
    public enum PlacementKind
    {
        Backpack,        // Player.ContainerInventories["0"] – sloty z ItemID/Amount/State
        ListInventory,   // Player.ListInventories[klucz] – słownik { ID: { Amount, Marker } }
        Pet,             // Player.Pets[] – { PetItemID }
        Mount,           // Player.Mounts[] – experimental minimal { MountItemID }
        Currency,        // Player.CurrencyAmounts – { ID: ilość }
        Tool,            // Player.Tools[] – { ToolItemID, CurrentOfType }
        CookingRecipe,   // Player.Cookbook.Recipes[] – ID przepisu
        CraftingRecipe,  // Player.UnlockedCraftingRecipes[] – ID przepisu
        DecorationPreset, // Player.PremadeDecorationPresets.DecorationPresets[] – ID gotowego presetu
        Unsupported
    }

    /// <summary>Gdzie i jak gra trzyma dany przedmiot.</summary>
    public sealed class Placement
    {
        public PlacementKind Kind { get; init; }
        /// <summary>Dla ListInventory: klucz magazynu w sejwie.</summary>
        public string? ListKey { get; init; }
        /// <summary>Opis miejsca dla użytkownika, np. "Storage → Furniture".</summary>
        public string Label { get; init; } = "";
        /// <summary>Powód, dla którego gra nie przyjmie przedmiotu w tym miejscu (blokuje dodanie).</summary>
        public string? Problem { get; init; }
        /// <summary>Ostrzeżenie, które nie blokuje dodania (np. nie da się sprawdzić podtypu).</summary>
        public string? Warning { get; init; }
        /// <summary>False ⇒ każda sztuka w osobnym slocie z Amount 1 (paczki, posiłki z dodatków).</summary>
        public bool Stackable { get; init; } = true;
        /// <summary>Początkowe "State" slotu plecaka (JSON) albo null.</summary>
        public string? DefaultState { get; init; }
    }

    /// <summary>
    /// Reguły wyprowadzone z realnych sejwów (403 / 444 / 535) i dumpu KOSDDV:
    ///  • TYPE z dumpu decyduje o miejscu (ActivityItem → plecak, Furniture/Clothing/… → ListInventories
    ///    wg CompatibleItemType, Companion → Pets, Currency → CurrencyAmounts, Tool → Tools,
    ///    ActivityData CookingRecipe/CraftingRecipe → Cookbook / UnlockedCraftingRecipes),
    ///  • prefix NAME to podtyp gry: magazyn mebli przyjmuje tylko FurnitureItemType_* z listy
    ///    CompatibleFurnitureTypes ("Landscaping/" odpowiada "GroundAlteration"), budynków – BuildingItemType_*,
    ///  • posiłki z dodatków (Meal/Expansion…) mają State.MealData i leżą po 1 w slocie,
    ///    paczki Consummable/* też po 1 w slocie (każda ma własną zawartość),
    ///  • zdobycie przedmiotu gra odnotowuje flagą true we wszystkich CollectionSets, które go zawierają
    ///    (w rozegranym sejwie ~100% posiadanych rzeczy ma flagę; flaga zostaje po sprzedaży).
    /// </summary>
    internal static class ItemPlacement
    {
        private const string MealState = "{\"MealData\":{\"IngredientsUsed\":[],\"SceneRestriction\":null}}";

        public static Placement Resolve(JObject root, string id)
        {
            var row = ItemDatabase.Find(id);
            string type = ItemDatabase.Get(row, "TYPE", "");
            string name = ItemDatabase.Get(row, "NAME", "");
            string prefix = NamePrefix(name);

            if (row == null)
                return Unsupported("Unknown ID — its in-game location is not known. Choose a destination section manually.");

            // Held accessories (umbrellas, parasols, balloons…) live in Player.Tools like the
            // shovel or the rod. Rubyelf lists newer ones under "Accessory" before a dump gives
            // them a TYPE; tool upgrades stay excluded.
            if (type.Length == 0 && ItemDatabase.Get(row, "CATEGORY", "") == "Accessory")
                type = "Tool";

            // Gameloft's ready-made decoration presets: the game lists the unlocked ones by ID.
            // The player's own layouts (World.DecorationPresets) are a different structure.
            if (ItemDatabase.Get(row, "KIND", "") == nameof(Data.ItemKind.DecorationPreset))
                return PresetList(root) != null
                    ? new Placement { Kind = PlacementKind.DecorationPreset, Label = "Presets" }
                    : Unsupported("This save has no Player.PremadeDecorationPresets list, so a preset cannot be added safely.");

            switch (type)
            {
                case "ActivityItem":
                    bool expansionMeal = prefix == "Meal" && name.Contains("Expansion", StringComparison.OrdinalIgnoreCase);
                    return new Placement
                    {
                        Kind = PlacementKind.Backpack,
                        Label = "Backpack",
                        Stackable = !(expansionMeal || prefix == "Consummable"),
                        DefaultState = expansionMeal ? MealState : null
                    };

                case "Companion":
                    if (prefix == "Mount")
                        return root["Player"]?["Mounts"] is JArray
                            ? new Placement
                            {
                                Kind = PlacementKind.Mount,
                                Label = "Mounts",
                                Warning = "EXPERIMENTAL: this adds only MountItemID to Player.Mounts. Initial bond, upgrade, and customization defaults are unverified. Test on a separate backup copy; the mount may not unlock or may not load correctly."
                            }
                            : Unsupported("This save has no Player.Mounts array, so a mount cannot be added safely.");

                    return prefix is "Pet" or "Critter"
                        ? new Placement
                        {
                            Kind = PlacementKind.Pet,
                            Label = "Companions",
                            Warning = "Existing Player.Pets records in version 626 have additional progress and interaction fields. New entries currently contain only PetItemID; the full set of initial values has not been confirmed in-game. Add only to a backup save."
                        }
                        : Unsupported($"Companion type \"{prefix}\" (such as Snippet) is not stored in Player.Pets and is not supported here.");

                case "Currency":
                    return new Placement { Kind = PlacementKind.Currency, Label = "Currency" };

                case "Tool":
                    return new Placement { Kind = PlacementKind.Tool, Label = "Tools" };

                case "ActivityData" when prefix == "CookingRecipe":
                    return new Placement { Kind = PlacementKind.CookingRecipe, Label = "Cookbook" };

                case "ActivityData" when prefix == "CraftingRecipe":
                    return new Placement { Kind = PlacementKind.CraftingRecipe, Label = "Crafting Recipes" };

                case "MemoryShard":
                    // Player.MemoryShards = { ID: maska zebranych kawałków } (wartości 1, 2, 4 … 938) – liczby kawałków nie znamy
                    return Unsupported("MemoryShards stores collected memory fragments as a bit mask. Editing this format is not supported.");

                case "Upgrade":
                    return Unsupported("Tool upgrades are stored per tool in Player.ToolUpgrades. Editing this format is not supported.");
            }

            // A save may contain a reserved ListInventory even for a game-object
            // type that the catalog explicitly marks as non-inventory (for example
            // Environment). A matching empty container alone must not make an ID addable.
            if (string.Equals(ItemDatabase.Get(row, "PLACEMENT", "None"), "None", StringComparison.OrdinalIgnoreCase))
                return Unsupported($"Type \"{type}\" is marked as non-inventory in the catalog. A matching save container alone is not enough to route it safely.");

            // magazyny ListInventories wg CompatibleItemType
            var matchingLists = (root["Player"]?["ListInventories"] as JObject)?.Properties()
                .Where(p => p.Value["CompatibleItemType"]?.ToString() == ListInventoryModule.ItemTypePrefix + type)
                .ToList() ?? new List<JProperty>();
            if (matchingLists.Count > 1)
                return Unsupported($"Multiple collections were found for type \"{type}\". The correct destination is ambiguous.");

            var li = matchingLists.FirstOrDefault();
            if (li != null)
            {
                if (!ListInventoryModule.IsEditableType(type))
                    return Unsupported($"Collection type \"{type}\" does not have a verified editing rule in this version.");

                string label = type == "Clothing" ? "Clothes" : $"Storage → {type}";
                string? problem = CheckSubtype(li.Value, "CompatibleFurnitureTypes", "FurnitureItemType_", prefix, name)
                               ?? CheckSubtype(li.Value, "CompatibleBuildingTypes", "BuildingItemType_", prefix, name);
                bool restricted = li.Value["CompatibleFurnitureTypes"] != null || li.Value["CompatibleBuildingTypes"] != null;
                return new Placement
                {
                    Kind = PlacementKind.ListInventory, ListKey = li.Name, Label = label, Problem = problem,
                    Warning = type == "Furniture" && prefix == "Landscaping"
                        ? "This path is mapped to FurnitureItemType_GroundAlteration based on the save structure, but adding it has not yet been confirmed in-game. Keep a backup save."
                        : restricted && name.Length == 0
                            ? "This item appears only in the Rubyelf list and has no internal name, so its subtype cannot be checked against the collection rules."
                            : null
                };
            }

            return Unsupported(type.Length == 0
                ? "The catalog has no item type for this ID, so its in-game location is unknown."
                : $"Type \"{type}\" is not an inventory item; the game stores it elsewhere or does not store it as an owned item.");
        }

        /// <summary>
        /// Magazyn ma listę dozwolonych podtypów (np. FurnitureItemType_Default, _Fence…);
        /// podtyp przedmiotu to prefix jego NAME ("Fence/…" → FurnitureItemType_Fence).
        /// </summary>
        private static string? CheckSubtype(JToken container, string field, string typePrefix, string prefix, string name)
        {
            var constraint = container[field];
            if (constraint == null || name.Length == 0)
                return null;   // brak ograniczenia albo brak NAME (przedmiot tylko z listy Rubyelf)
            if (constraint["List"] is not JArray allowed)
                return $"The collection has a {field} restriction, but its allowed types could not be read.";

            // Game item names use the `Landscaping/` namespace for paths, while
            // ListInventories declares those same items as GroundAlteration.
            // For example 40200507 is `Landscaping/Plant!MoanaPlumeriaFlowerPath01`
            // and the save's furniture inventory allows FurnitureItemType_GroundAlteration.
            string subtypeName = typePrefix == "FurnitureItemType_" && prefix == "Landscaping"
                ? "GroundAlteration"
                : prefix;
            string subtype = typePrefix + subtypeName;
            return allowed.Any(a => a.ToString() == subtype)
                ? null
                : $"This collection only accepts: {string.Join(", ", allowed.Select(a => a.ToString().Replace(typePrefix, "")))}; " +
                  $"this item is type \"{prefix}\" and the game will not accept it here.";
        }

        public static string? GetRiskWarning(DataRow? row)
        {
            if (row == null) return null;
            string name = ItemDatabase.Get(row, "NAME", "");
            string type = ItemDatabase.Get(row, "TYPE", "");
            string category = ItemDatabase.Get(row, "CATEGORY", "");
            string metadata = string.Join(" ", row.ItemArray.OfType<string>());
            bool doNotCraft = metadata.Contains("DO NOT CRAFT", StringComparison.OrdinalIgnoreCase) ||
                              metadata.Contains("DON'T CRAFT", StringComparison.OrdinalIgnoreCase) ||
                              metadata.Contains("DONT CRAFT", StringComparison.OrdinalIgnoreCase);
            bool characterHouse = (type.Equals("Building", StringComparison.OrdinalIgnoreCase) ||
                                   type.Equals("Furniture", StringComparison.OrdinalIgnoreCase) ||
                                   category.Contains("House", StringComparison.OrdinalIgnoreCase)) &&
                                  (metadata.Contains("HOUSE", StringComparison.OrdinalIgnoreCase) ||
                                   metadata.Contains("HOME", StringComparison.OrdinalIgnoreCase));
            bool progression = name.Contains("Mission!", StringComparison.OrdinalIgnoreCase) ||
                               name.Contains("Event!", StringComparison.OrdinalIgnoreCase) ||
                               type.Equals("Character", StringComparison.OrdinalIgnoreCase);
            if (doNotCraft || characterHouse || progression)
            {
                var reasons = new List<string>();
                if (doNotCraft) reasons.Add("the catalog marks it Do Not Craft");
                if (characterHouse) reasons.Add("it appears to be a character home/house item");
                if (progression) reasons.Add("it is Mission/Event/Character progression content");
                return $"Caution: {string.Join("; ", reasons)}. It may be tied to quests, placement, or game state and can cause bugs or damage the save. Add only if you accept that risk and have a separate backup.";
            }
            return null;
        }

        private static Placement Unsupported(string why) =>
            new() { Kind = PlacementKind.Unsupported, Label = "—", Problem = why };

        private static string NamePrefix(string name)
        {
            int slash = name.IndexOf('/');
            if (slash > 0) return name[..slash];
            int bang = name.IndexOf('!');
            return bang > 0 ? name[..bang] : name;
        }

        /* =================================================================
         *  Zapis rzeczy, które nie mają własnej tabeli w UI
         * ================================================================= */

        /// <summary>Ustawia flagę "zdobyte" we wszystkich kolekcjach zawierających ID. Zwraca liczbę flag.</summary>
        public static int MarkCollected(JObject root, long id)
        {
            if (root["Player"]?["CollectionSets"] is not JArray sets) return 0;

            string key = id.ToString();
            int n = 0;
            foreach (var items in sets.OfType<JObject>()
                         .SelectMany(s => s["GroupData"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                         .Select(g => g["GroupsCollectionItems"] as JObject))
            {
                if (items?[key] is JValue v && v.Type == JTokenType.Boolean)
                {
                    if (!(bool)v) items[key] = true;
                    n++;
                }
            }
            return n;
        }

        /// <summary>Dodaje narzędzie (jako nieaktywne). False, jeśli już jest albo brak Player.Tools.</summary>
        public static bool AddTool(JObject root, long id)
        {
            if (root["Player"]?["Tools"] is not JArray tools) return false;
            if (tools.OfType<JObject>().Any(t => t["ToolItemID"]?.ToObject<long>() == id)) return false;

            tools.Add(new JObject { ["ToolItemID"] = id, ["CurrentOfType"] = false });
            return true;
        }

        internal static JArray? PresetList(JObject root)
            => root["Player"]?["PremadeDecorationPresets"]?["DecorationPresets"] as JArray;

        /// <summary>Unlocks a Gameloft decoration preset. False when already unlocked or the list is missing.</summary>
        public static bool AddPreset(JObject root, long id)
        {
            if (PresetList(root) is not JArray presets) return false;
            if (presets.Any(p => p.Type == JTokenType.Integer && (long)p == id)) return false;

            presets.Add(id);
            return true;
        }

        /// <summary>Dodaje ID do listy przepisów (Cookbook.Recipes / UnlockedCraftingRecipes).</summary>
        public static bool AddRecipe(JObject root, long id, bool cooking)
        {
            var list = cooking
                ? root["Player"]?["Cookbook"]?["Recipes"] as JArray
                : root["Player"]?["UnlockedCraftingRecipes"] as JArray;
            if (list == null) return false;
            if (list.Any(t => t.Type == JTokenType.Integer && (long)t == id)) return false;

            list.Add(id);
            return true;
        }

        /// <summary>Dodaje amount do waluty. Zwraca nowy stan albo null, gdy brak CurrencyAmounts.</summary>
        public static long? AddCurrency(JObject root, long id, int amount)
        {
            if (root["Player"]?["CurrencyAmounts"] is not JObject cur) return null;

            string key = id.ToString();
            long now = (cur[key]?.ToObject<long>() ?? 0) + amount;
            cur[key] = now;
            return now;
        }
    }
}
