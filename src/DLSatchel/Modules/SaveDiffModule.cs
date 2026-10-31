using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace DDV_SaveEditor_G.Modules
{
    public sealed class SaveDiffSummary
    {
        public required IReadOnlyList<string> Paths { get; init; }
        public int OmittedPathCount { get; init; }
        public bool HasChanges => Paths.Count > 0 || OmittedPathCount > 0;
    }

    /// <summary>Reports changed JSON paths without exposing old or new save values.</summary>
    public static class SaveDiffModule
    {
        public static SaveDiffSummary Compare(JToken? before, JToken? after, int maxPaths = 20)
        {
            if (maxPaths < 1) throw new ArgumentOutOfRangeException(nameof(maxPaths));
            var all = new List<string>();
            Collect(before, after, "$", all);
            return new SaveDiffSummary
            {
                Paths = all.Take(maxPaths).ToArray(),
                OmittedPathCount = Math.Max(0, all.Count - maxPaths)
            };
        }

        private static void Collect(JToken? before, JToken? after, string path, List<string> output)
        {
            if (JToken.DeepEquals(before, after)) return;

            if (before is JObject left && after is JObject right)
            {
                foreach (string key in left.Properties().Select(p => p.Name)
                             .Union(right.Properties().Select(p => p.Name), StringComparer.Ordinal)
                             .OrderBy(k => k, StringComparer.Ordinal))
                {
                    Collect(left[key], right[key], $"{path}.{key}", output);
                }
                return;
            }

            // Arrays are treated as a unit: index-level diffs are noisy and unstable
            // for game-owned collections whose order can change during normal play.
            output.Add(path);
        }

        private static readonly Dictionary<string, string> CurrencyNames = new(StringComparer.Ordinal)
        {
            ["80300000"] = "Dreamlight",
            ["80000000"] = "Star Coins",
            ["80200002"] = "Pixel Dust",
            ["80000003"] = "Mist",
            ["80000009"] = "Daisy Coins",
            ["80000010"] = "Storybook Magic"
        };

        private static readonly Dictionary<string, string> PlayerFieldNames = new(StringComparer.Ordinal)
        {
            ["Name"] = "Player name",
            ["Level"] = "Level",
            ["Xp"] = "XP",
            ["Pets"] = "Companions",
            ["AssignedPets"] = "Assigned companions",
            ["Mounts"] = "Mounts",
            ["Tools"] = "Tools",
            ["Cookbook"] = "Cooking recipes",
            ["UnlockedCraftingRecipes"] = "Crafting recipes",
            ["CollectionSets"] = "Collection progress",
            ["MemoryShards"] = "Memories"
        };

        /// <summary>
        /// The changed areas as the editor names them ("Backpack", "Furniture", "Star Coins"),
        /// each listed once; <paramref name="save"/> tells which collection a storage list holds.
        /// </summary>
        public static IReadOnlyList<string> ReadableSections(JToken? save, IEnumerable<string> paths)
            => paths.Select(path => Describe(save, path)).Distinct(StringComparer.Ordinal).ToArray();

        /// <summary>One changed path in the editor's words; unknown areas fall back to "Player › Field".</summary>
        public static string Describe(JToken? save, string path)
        {
            string[] parts = path.StartsWith("$.", StringComparison.Ordinal)
                ? path[2..].Split('.')
                : path.Split('.');

            if (parts.Length >= 2 && parts[0] == "Player")
            {
                string field = parts[1];
                if (field == "ContainerInventories")
                    return parts.Length >= 3 && parts[2] == "0" ? "Backpack" : "Character inventories";

                if (field == "ListInventories" && parts.Length >= 3)
                {
                    string? type = save?["Player"]?["ListInventories"]?[parts[2]]?["CompatibleItemType"]?.ToString();
                    return type is { Length: > 0 }
                        ? ListInventoryModule.DisplayName(type.Replace(ListInventoryModule.ItemTypePrefix, ""))
                        : "Storage";
                }

                if (field == "CurrencyAmounts")
                    return parts.Length >= 3 && CurrencyNames.TryGetValue(parts[2], out var currency)
                        ? currency
                        : "Currencies";

                if (PlayerFieldNames.TryGetValue(field, out var name)) return name;
            }

            return string.Join(" › ", parts);
        }
    }
}
