using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace DDV_SaveEditor_G.Modules
{
    /// <param name="OnlyUnchecked">True when the only concern is that the community list does not have it
    /// yet - that is also true for new items a player really got in the game.</param>
    public sealed record RiskyItem(long Id, string ListKey, string Name, string Reason, bool OnlyUnchecked);

    /// <summary>
    /// Items in the collections that were not there in an earlier copy of the save and that the item
    /// lists flag. The game itself also keeps some quest objects in the collections, so only items
    /// that are new since that copy are listed, never everything of a kind.
    /// </summary>
    public static class RiskyAdditions
    {
        public static List<RiskyItem> Find(JObject current, JObject before)
        {
            var found = new List<RiskyItem>();
            if (current["Player"]?["ListInventories"] is not JObject lists) return found;
            var placed = SaveItemIds.PlacedInWorld(current);

            foreach (var list in lists.Properties())
            {
                if (list.Value["Inventory"] is not JObject items) continue;
                var earlier = before["Player"]?["ListInventories"]?[list.Name]?["Inventory"] as JObject;

                foreach (var item in items.Properties())
                {
                    if (!long.TryParse(item.Name, out long id) || earlier?[item.Name] != null || placed.Contains(id)) continue;
                    var row = ItemDatabase.Find(id);
                    if (row == null) continue;   // unknown to the lists: nothing to judge it by

                    string reason = Reason(row);
                    if (reason.Length > 0)
                        found.Add(new RiskyItem(id, list.Name,
                            ItemDatabase.Get(row, "LOCALIZED_DISPLAY_NAME", ItemDatabase.Get(row, "NAME", item.Name)), reason,
                            OnlyUnchecked: reason == NotCheckedReason));
                }
            }
            return found;
        }

        internal const string NotCheckedReason = "not checked by the community list";

        internal static string Reason(System.Data.DataRow row)
        {
            string name = ItemDatabase.Get(row, "NAME", "");
            string prefix = MissingItems.NamePrefix(name);
            // Roads and paths (GroundAlteration) are real items the game gives players; never added in
            // bulk, but not a reason to take them out again.
            if (!prefix.Equals("GroundAlteration", StringComparison.OrdinalIgnoreCase) &&
                MissingItems.TechnicalPrefixes.Contains(prefix, StringComparer.OrdinalIgnoreCase))
                return "game object, not a normal item";
            if (row["UNSAFE"] is true || ItemDatabase.Get(row, "SAFETY", "") == "UNSAFE")
                return "marked UNSAFE in the lists";
            if (row["IS_TEST"] is true)
                return "test item";
            if (!ItemDatabase.IsCommunityChecked(row))
                return NotCheckedReason;
            return "";
        }
    }
}
