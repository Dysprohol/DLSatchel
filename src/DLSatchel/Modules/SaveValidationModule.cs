using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace DDV_SaveEditor_G.Modules
{
    public enum SaveIssueLevel { Info, Caution, Error }

    public sealed class SaveValidationIssue
    {
        public required SaveIssueLevel Level { get; init; }
        public required string Path { get; init; }
        public required string Message { get; init; }
    }

    /// <summary>Read-only inspection of item placement and complex save areas.</summary>
    public static class SaveValidationModule
    {
        public static IReadOnlyList<SaveValidationIssue> Analyze(JObject root)
        {
            var issues = new List<SaveValidationIssue>();
            if (root["Player"] is not JObject player)
            {
                issues.Add(new() { Level = SaveIssueLevel.Error, Path = "Player", Message = "This file has no player data. It may not be a save, or it is damaged." });
                return issues;
            }

            if (player["ListInventories"] is JObject inventories)
            {
                foreach (var list in inventories.Properties())
                {
                    if (list.Value["Inventory"] is not JObject items) continue;
                    foreach (var item in items.Properties())
                    {
                        if (!long.TryParse(item.Name, out long id))
                        {
                            issues.Add(new() { Level = SaveIssueLevel.Caution, Path = $"Player.ListInventories.{list.Name}.Inventory.{item.Name}", Message = "An item here has an ID that is not a number. The game may not read it." });
                            continue;
                        }
                        CheckItem(root, id, PlacementKind.ListInventory, list.Name,
                            $"Player.ListInventories.{list.Name}.Inventory.{item.Name}", issues);
                    }
                }
            }

            // The player backpack lives at ContainerInventories["0"]. Other
            // ContainerInventories entries belong to character data and are never inspected here.
            if (player["ContainerInventories"]?["0"]?["Inventory"] is JArray backpack)
            {
                for (int i = 0; i < backpack.Count; i++)
                {
                    long id = backpack[i]?["ItemID"]?.Value<long>() ?? 0;
                    if (id > 0)
                        CheckItem(root, id, PlacementKind.Backpack, null,
                            $"Player.ContainerInventories.0.Inventory[{i}]", issues);
                }
            }

            if (player["Pets"] is JArray pets)
            {
                var seenPets = new HashSet<long>();
                bool profile626 = root["GameInfo"]?["Version"]?.Value<int>() >= 626;
                for (int i = 0; i < pets.Count; i++)
                {
                    string path = $"Player.Pets[{i}]";
                    long id = ReadId(pets[i]?.Type == JTokenType.Object ? pets[i]?["PetItemID"] : pets[i]);
                    if (id <= 0)
                    {
                        issues.Add(new() { Level = SaveIssueLevel.Caution, Path = path, Message = "This companion has no valid ID. It is left as it is." });
                        continue;
                    }

                    if (!seenPets.Add(id))
                        issues.Add(new() { Level = SaveIssueLevel.Error, Path = path, Message = $"Companion {id} is in the save twice." });

                    CheckItem(root, id, PlacementKind.Pet, null, path, issues);

                    if (profile626 && pets[i] is JObject pet &&
                        new[] { "FriendshipLevel", "FriendshipXp", "GrantedInventorySlots", "CritterInteractions", "PendingHangoutRewards" }
                            .Any(field => pet[field] == null))
                    {
                        issues.Add(new()
                        {
                            Level = SaveIssueLevel.Caution,
                            Path = path,
                            Message = "This companion is missing some of its data. Check it in the game."
                        });
                    }
                }
            }

            if (player["CurrencyAmounts"] is JObject currencies)
            {
                foreach (var currency in currencies.Properties())
                {
                    string path = $"Player.CurrencyAmounts.{currency.Name}";
                    if (!long.TryParse(currency.Name, out long id))
                    {
                        issues.Add(new() { Level = SaveIssueLevel.Caution, Path = path, Message = "A currency here has an ID that is not a number." });
                        continue;
                    }
                    CheckItem(root, id, PlacementKind.Currency, null, path, issues);
                }
            }

            InspectIdArray(root, player["Tools"] as JArray, "ToolItemID", PlacementKind.Tool, "Player.Tools", issues);
            InspectRecipeArray(root, player["Cookbook"]?["Recipes"] as JArray,
                PlacementKind.CookingRecipe, "Player.Cookbook.Recipes", issues);
            InspectRecipeArray(root, player["UnlockedCraftingRecipes"] as JArray,
                PlacementKind.CraftingRecipe, "Player.UnlockedCraftingRecipes", issues);

            InspectMounts(player, issues);

            return issues;
        }

        private static void CheckItem(JObject root, long id, PlacementKind expected, string? listKey,
                                      string path, List<SaveValidationIssue> issues)
        {
            var row = ItemDatabase.Find(id);
            if (row == null)
            {
                issues.Add(new() { Level = SaveIssueLevel.Caution, Path = path, Message = $"ID {id} is not in your item lists, so it can't be checked." });
                return;
            }

            var actual = ItemPlacement.Resolve(root, id.ToString());
            if (actual.Kind == PlacementKind.Unsupported)
            {
                issues.Add(new() { Level = SaveIssueLevel.Caution, Path = path, Message = $"ID {id}: {actual.Problem}" });
                return;
            }

            bool matches = actual.Kind == expected &&
                           (expected != PlacementKind.ListInventory || actual.ListKey == listKey);
            if (!matches)
                issues.Add(new() { Level = SaveIssueLevel.Error, Path = path, Message = $"ID {id} is in the wrong place. It belongs in {actual.Label}." });
            else if (!string.IsNullOrWhiteSpace(actual.Warning))
                issues.Add(new() { Level = SaveIssueLevel.Caution, Path = path, Message = $"ID {id}: {actual.Warning}" });
        }

        private static void AddComplexAreaNote(JObject player, string key, string path, string message,
                                               List<SaveValidationIssue> issues)
        {
            if (player[key] is not JToken value) return;
            int count = value switch
            {
                JObject obj => obj.Count,
                JArray arr => arr.Count,
                _ => 1
            };
            issues.Add(new()
            {
                Level = SaveIssueLevel.Info,
                Path = path,
                Message = $"{message} ({count:N0} found)"
            });
        }

        private static void InspectMounts(JObject player, List<SaveValidationIssue> issues)
        {
            if (player["Mounts"] is not JArray mounts) return;
            var seen = new HashSet<long>();
            for (int i = 0; i < mounts.Count; i++)
            {
                string path = $"Player.Mounts[{i}]";
                long id = ReadId(mounts[i]?["MountItemID"]);
                if (id <= 0)
                {
                    issues.Add(new() { Level = SaveIssueLevel.Caution, Path = path, Message = "A mount here has no valid ID. The game may not read it." });
                    continue;
                }

                var row = id > 0 ? ItemDatabase.Find(id) : null;
                string name = row == null
                    ? "unknown mount"
                    : ItemDatabase.Get(row, "LOCALIZED_DISPLAY_NAME", ItemDatabase.Get(row, "NAME", id.ToString()));
                string rawType = ItemDatabase.Get(row, "TYPE", "");
                string internalName = ItemDatabase.Get(row, "NAME", "");
                bool isMount = rawType.Equals("Companion", System.StringComparison.OrdinalIgnoreCase) &&
                               internalName.StartsWith("Mount/", System.StringComparison.OrdinalIgnoreCase);
                string progress = mounts[i]?["BondingLevel"] is JToken level
                    ? $"Bond level: {level}. "
                    : "";

                if (!seen.Add(id))
                {
                    issues.Add(new() { Level = SaveIssueLevel.Error, Path = path, Message = $"Mount {name} ({id}) is in the save twice." });
                    continue;
                }

                bool minimalRecord = mounts[i] is JObject record && record.Properties().All(p => p.Name == "MountItemID");
                issues.Add(new()
                {
                    Level = row == null || (isMount && minimalRecord) ? SaveIssueLevel.Caution : isMount ? SaveIssueLevel.Info : SaveIssueLevel.Error,
                    Path = path,
                    Message = row == null
                        ? $"Mount {id} is not in your item lists. {progress}"
                        : !isMount
                            ? $"{name} ({id}) is with your mounts, but the item lists say it is not a mount."
                            : minimalRecord
                                ? $"Mount {name} ({id}) was added by the editor and has no bond or look settings yet. Check it in the game; keep a backup."
                                : $"Mount {name} ({id}). {progress}"
                });
            }
        }

        private static void InspectIdArray(JObject root, JArray? entries, string idProperty,
            PlacementKind expected, string path, List<SaveValidationIssue> issues)
        {
            if (entries == null) return;
            for (int i = 0; i < entries.Count; i++)
            {
                long id = ReadId(entries[i] is JObject obj ? obj[idProperty] : entries[i]);
                string itemPath = $"{path}[{i}]";
                if (id <= 0)
                {
                    issues.Add(new() { Level = SaveIssueLevel.Caution, Path = itemPath, Message = "An entry here has no valid ID." });
                    continue;
                }
                CheckItem(root, id, expected, null, itemPath, issues);
            }
        }

        private static void InspectRecipeArray(JObject root, JArray? entries,
            PlacementKind expected, string path, List<SaveValidationIssue> issues)
        {
            if (entries == null) return;
            for (int i = 0; i < entries.Count; i++)
            {
                long id = ReadId(entries[i]);
                string itemPath = $"{path}[{i}]";
                if (id <= 0)
                {
                    issues.Add(new() { Level = SaveIssueLevel.Caution, Path = itemPath, Message = "A recipe here has no valid ID." });
                    continue;
                }
                CheckItem(root, id, expected, null, itemPath, issues);
            }
        }

        private static long ReadId(JToken? token)
        {
            if (token == null) return 0;
            return long.TryParse(token.ToString(), out long id) ? id : 0;
        }
    }
}
