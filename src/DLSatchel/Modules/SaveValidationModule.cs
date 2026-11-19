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
                issues.Add(new() { Level = SaveIssueLevel.Error, Path = "Player", Message = "Player object is missing." });
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
                            issues.Add(new() { Level = SaveIssueLevel.Caution, Path = $"Player.ListInventories.{list.Name}.Inventory.{item.Name}", Message = "Item key is not a numeric ID." });
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
                        issues.Add(new() { Level = SaveIssueLevel.Caution, Path = path, Message = "PetItemID is not recognized. The record will be preserved unchanged." });
                        continue;
                    }

                    if (!seenPets.Add(id))
                        issues.Add(new() { Level = SaveIssueLevel.Error, Path = path, Message = $"Duplicate PetItemID record: {id}." });

                    CheckItem(root, id, PlacementKind.Pet, null, path, issues);

                    if (profile626 && pets[i] is JObject pet &&
                        new[] { "FriendshipLevel", "FriendshipXp", "GrantedInventorySlots", "CritterInteractions", "PendingHangoutRewards" }
                            .Any(field => pet[field] == null))
                    {
                        issues.Add(new()
                        {
                            Level = SaveIssueLevel.Caution,
                            Path = path,
                            Message = "This companion record has fewer fields than observed records in a version 626 profile. Verify its defaults and completeness in-game."
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
                        issues.Add(new() { Level = SaveIssueLevel.Caution, Path = path, Message = "Currency key is not a numeric ID." });
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
            AddComplexAreaNote(player, "MemoryShards", "Player.MemoryShards", "Values are memory progress masks; the meaning of individual bits must be confirmed before editing.", issues);
            AddComplexAreaNote(player, "ToolUpgrades", "Player.ToolUpgrades", "Upgrades are grouped by tool and may require a matching EvolveProgressState.", issues);
            AddComplexAreaNote(player, "EvolveStatesProfile", "Player.EvolveStatesProfile", "Progress for evolving tools is not editable yet.", issues);
            AddComplexAreaNote(player, "AssignedPets", "Player.AssignedPets", "Assigned companion slots were found, but their meaning needs further verification.", issues);

            return issues;
        }

        private static void CheckItem(JObject root, long id, PlacementKind expected, string? listKey,
                                      string path, List<SaveValidationIssue> issues)
        {
            var row = ItemDatabase.Find(id);
            if (row == null)
            {
                issues.Add(new() { Level = SaveIssueLevel.Caution, Path = path, Message = $"ID {id} is not in the current catalog; its in-game location is unknown." });
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
                issues.Add(new() { Level = SaveIssueLevel.Error, Path = path, Message = $"ID {id} appears to be in the wrong save location. The catalog points to: {actual.Label}." });
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
                Message = $"Found {count} record(s). {message}"
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
                    issues.Add(new() { Level = SaveIssueLevel.Caution, Path = path, Message = "MountItemID is missing or invalid; review this record manually." });
                    continue;
                }

                var row = id > 0 ? ItemDatabase.Find(id) : null;
                string name = row == null
                    ? "wpis nieznany w katalogu"
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
                    issues.Add(new() { Level = SaveIssueLevel.Error, Path = path, Message = $"Duplicate mount record: {name} (ID {id})." });
                    continue;
                }

                bool minimalRecord = mounts[i] is JObject record && record.Properties().All(p => p.Name == "MountItemID");
                issues.Add(new()
                {
                    Level = row == null || (isMount && minimalRecord) ? SaveIssueLevel.Caution : isMount ? SaveIssueLevel.Info : SaveIssueLevel.Error,
                    Path = path,
                    Message = row == null
                        ? $"Mount ID {id} is not in the catalog. {progress}Bond, upgrade, and customization editing are not supported yet."
                        : !isMount
                            ? $"ID {id} ({name}) is not classified as Companion/Mount in the catalog. Review this record before saving."
                            : minimalRecord
                                ? $"Mount {name} (ID {id}) has only MountItemID. This experimental record has no verified bond, upgrade, or customization defaults; test it in game only on a backup copy."
                                : $"Mount {name} (ID {id}). {progress}Bond, upgrade, and customization editing are not supported yet."
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
                    issues.Add(new() { Level = SaveIssueLevel.Caution, Path = itemPath, Message = $"Missing or invalid ID ({idProperty})." });
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
                    issues.Add(new() { Level = SaveIssueLevel.Caution, Path = itemPath, Message = "Missing or invalid recipe ID." });
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
