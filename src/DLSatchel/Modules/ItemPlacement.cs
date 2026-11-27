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
                return Unsupported("This ID is not in your item lists. Open the tab it belongs to and try again.");

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
                    : Unsupported("This save has no preset list yet, so presets can't be added.");

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
                                // The game fills in bond and favourite treats itself (checked in the game, 1.26).
                                // A save without any mount gets a warning from RequirementWarnings.
                                Warning = null
                            }
                            : Unsupported("This save has no mounts yet, so mounts can't be added.");

                    // Critter/... (1201xxxxx) are the wild animals walking around the valley. Game-written saves never
                    // hold them in Player.Pets (checked in 7 saves, 219-221 companions); players report that adding
                    // them there breaks the Companions list. The companion of the same animal is a Pet/... item.
                    if (prefix == "Critter")
                        return Unsupported(CritterProblem);

                    return prefix is "Pet"
                        ? new Placement
                        {
                            Kind = PlacementKind.Pet,
                            Label = "Companions"
                            // New companions get the full record the game itself starts with (PetModule.NewPetRecord).
                        }
                        : Unsupported("This kind of companion can't be added by the editor.");

                case "Currency":
                    return new Placement { Kind = PlacementKind.Currency, Label = "Currency" };

                case "Tool":
                    return new Placement { Kind = PlacementKind.Tool, Label = "Tools & Accessories" };

                case "ActivityData" when prefix == "CookingRecipe":
                    return new Placement { Kind = PlacementKind.CookingRecipe, Label = "Cookbook" };

                case "ActivityData" when prefix == "CraftingRecipe":
                    return new Placement { Kind = PlacementKind.CraftingRecipe, Label = "Crafting Recipes" };

                case "MemoryShard":
                    // Player.MemoryShards = { ID: maska zebranych kawałków } (wartości 1, 2, 4 … 938) – liczby kawałków nie znamy
                    return Unsupported("Memories can't be added by the editor.");

                case "Character":
                    return Unsupported("This is a character. Characters join your valley through the story; the editor can't add them.");

                case "Upgrade":
                    return Unsupported("Tool upgrades can't be added by the editor.");
            }

            // A save may contain a reserved ListInventory even for a game-object
            // type that the catalog explicitly marks as non-inventory (for example
            // Environment). A matching empty container alone must not make an ID addable.
            if (string.Equals(ItemDatabase.Get(row, "PLACEMENT", "None"), "None", StringComparison.OrdinalIgnoreCase))
                return Unsupported($"The item lists say this is not something you can own ({type}), so it can't be added.");

            // magazyny ListInventories wg CompatibleItemType
            var matchingLists = (root["Player"]?["ListInventories"] as JObject)?.Properties()
                .Where(p => p.Value["CompatibleItemType"]?.ToString() == ListInventoryModule.ItemTypePrefix + type)
                .ToList() ?? new List<JProperty>();
            if (matchingLists.Count > 1)
                return Unsupported($"Your save has more than one place for {type} items, so the editor can't tell which to use.");

            var li = matchingLists.FirstOrDefault();
            if (li != null)
            {
                if (!ListInventoryModule.IsEditableType(type))
                    return Unsupported($"The editor can't add {ListInventoryModule.DisplayName(type)} items yet.");

                string label = ListInventoryModule.DisplayName(type);
                string? problem = CheckSubtype(li.Value, "CompatibleFurnitureTypes", "FurnitureItemType_", prefix, name)
                               ?? CheckSubtype(li.Value, "CompatibleBuildingTypes", "BuildingItemType_", prefix, name);
                bool restricted = li.Value["CompatibleFurnitureTypes"] != null || li.Value["CompatibleBuildingTypes"] != null;
                return new Placement
                {
                    Kind = PlacementKind.ListInventory, ListKey = li.Name, Label = label, Problem = problem,
                    Warning = type == "Furniture" && prefix == "Landscaping"
                        ? "Landscaping items haven't been tested in the game yet. Keep a backup save."
                        : restricted && name.Length == 0
                            ? "Only Rubyelf's ID list has this item, so the editor can't check that it fits here."
                            : UnlockWarning(root, type, prefix)
                };
            }

            return Unsupported(type.Length == 0
                ? "The item lists don't say what this item is, so the editor doesn't know where it goes."
                : $"This is not an item you can own ({type}), so it can't be added.");
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
                return "The editor can't read which items fit here, so it won't add this one.";

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
                : $"This item ({prefix}) doesn't fit here; the game won't accept it.";
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
                if (doNotCraft) reasons.Add("the item lists say \"do not craft\"");
                if (characterHouse) reasons.Add("it belongs to a character's house");
                if (progression) reasons.Add("it is part of a quest, event or character story");
                // Forum reports (2026): quests stuck after their item was added early (Vanellope, picnic preset),
                // saves that needed a restart. Not tested by the editor.
                return $"{Capitalize(string.Join("; ", reasons))}. Players report stuck quests and broken saves from items like this.";
            }
            return null;
        }

        /// <summary>Clothing slots of the mermaid form added in game update 1.26.</summary>
        internal static readonly string[] MermaidPrefixes =
            { "MerFin", "MerTail", "MerTailPattern", "MerTorso", "MerHand", "BodEar" };

        /// <summary>
        /// Mermaid parts and ship skins (game update 1.26) are ordinary wardrobe and skin items,
        /// but they are used by a mermaid form (Player.*MermaidAvatar) and a ship (World.BoatData)
        /// that the game unlocks in its story. Until the save has those, adding them is unverified.
        /// </summary>
        internal static string? UnlockWarning(JObject root, string type, string prefix)
        {
            if (type == "Clothing" && MermaidPrefixes.Contains(prefix, StringComparer.OrdinalIgnoreCase))
            {
                var player = root["Player"];
                bool unlocked = player?["FemaleMermaidAvatar"]?.Type is JTokenType.Object ||
                                player?["MaleMermaidAvatar"]?.Type is JTokenType.Object;
                return unlocked
                    ? null
                    : "Mermaid part: worn in the mermaid form, which this save doesn't have yet. " +
                      "It should wait in your wardrobe, but this isn't tested. Keep a backup.";
            }

            if (type == "Skin" && prefix.Equals("Boat", StringComparison.OrdinalIgnoreCase))
            {
                bool unlocked = root["World"]?["BoatData"]?.Type is JTokenType.Object;
                return unlocked
                    ? null
                    : "Ship skin: used by the ship, which this save doesn't have yet. " +
                      "It should wait with your skins, but this isn't tested. Keep a backup.";
            }

            return null;
        }

        /// <summary>
        /// What the item needs that this save may not have: an expansion it doesn't own (the save
        /// lists the ones bought, as "Expansion01"… / "WinnieDLC"), or, for a mount, the stable and
        /// quests that come first. Without them the game's reaction is unknown, so the player is told.
        /// </summary>
        public static List<string> RequirementWarnings(JObject root, DataRow? row)
        {
            var warnings = new List<string>();
            if (row == null) return warnings;
            string name = ItemDatabase.Get(row, "NAME", "");

            if (MissingExpansion(root, row) is string tag)
                warnings.Add($"From an expansion not owned where this save was last played ({tag}). A player reported losing that expansion's area after adding such items.");

            if (name.StartsWith("Mount/", StringComparison.OrdinalIgnoreCase) && ItemDatabase.Get(row, "TYPE", "") == "Companion" &&
                root["Player"]?["Mounts"] is JArray { Count: 0 })
                warnings.Add("No mounts unlocked in this save yet. A player reported a white screen after adding a horse this early.");
            return warnings;
        }

        /// <summary>
        /// The expansion an item comes with ("Expansion02", "WinnieDLC") when the save says the platform
        /// it was last played on doesn't own it. The save's list follows that platform: playing it
        /// elsewhere changes it, so this is about where the save is played, not who bought what.
        /// Null when the item needs no expansion, or the save doesn't say (older saves).
        /// </summary>
        public static string? MissingExpansion(JObject root, DataRow? row)
        {
            if (row == null) return null;
            var tag = System.Text.RegularExpressions.Regex.Match(ItemDatabase.Get(row, "NAME", ""), @"Expansion0\d|WinnieDLC");
            if (!tag.Success || root["LastKnownBoughtExpansionsAnyPlatform"] is not JArray owned) return null;
            return owned.Any(t => string.Equals(t.ToString(), tag.Value, StringComparison.OrdinalIgnoreCase)) ? null : tag.Value;
        }

        private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

        internal const string CritterProblem =
            "This is a wild critter that walks around your valley, not a companion. Players report that adding one as a " +
            "companion breaks the Companions list. The companion version has its own ID - search for the same name.";

        /// <summary>A wild critter (Critter/..., IDs 1201xxxxx) rather than a companion.</summary>
        public static bool IsWildCritter(long id)
        {
            var row = ItemDatabase.Find(id);
            string name = ItemDatabase.Get(row, "NAME", "");
            return name.Length > 0
                ? name.StartsWith("Critter/", StringComparison.OrdinalIgnoreCase)
                : id is >= 120100000 and < 120200000;
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
