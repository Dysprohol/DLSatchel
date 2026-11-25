using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace DDV_SaveEditor_G.Modules
{
    /// <summary>How an item the player does not have yet is offered by "Add all missing".</summary>
    public enum MissingGroup
    {
        Tested,
        Ordinary,
        Shop,
        Dlc,
        NotOwnedExpansion,
        Event,
        Quest,
        Unlock,
        Skins,
        NotChecked,
        Unclear,
        Other
    }

    public sealed record MissingItem(long Id, string Name, MissingGroup Group);

    /// <summary>
    /// Finds every catalog item that goes to one list collection (wardrobe, furniture …)
    /// and is not in it yet. Unsafe, test and unreleased items are never offered; the rest
    /// are grouped so quest rewards, shop items and the like can be left out.
    /// </summary>
    public static class MissingItems
    {
        public static string Label(MissingGroup group) => group switch
        {
            MissingGroup.Tested => "Tested in the game",
            MissingGroup.Ordinary => "Ordinary items",
            MissingGroup.Shop => "Shop and Premium items",
            MissingGroup.Dlc => "Expansion and DLC items",
            MissingGroup.NotOwnedExpansion => "From expansions not owned where this save was last played",
            MissingGroup.Event => "Event and Star Path items",
            MissingGroup.Quest => "Quest rewards",
            MissingGroup.Unlock => "Mermaid parts and ship skins",
            MissingGroup.Skins => "Styles for houses, buildings and characters",
            MissingGroup.NotChecked => "Not checked by the community yet",
            MissingGroup.Unclear => "Items the lists say little about",
            _ => "Items with other warnings"
        };

        public static string Description(MissingGroup group) => group switch
        {
            MissingGroup.Tested => "Players tried them in the game and they worked. A big game update can change that.",
            MissingGroup.Ordinary => "Items the lists have no warnings about.",
            MissingGroup.Shop => "Sold in the game's shop or in bundles, including premium Star Path rewards.",
            MissingGroup.Dlc => "From expansions or DLC. Tick them only if you own them.",
            MissingGroup.NotOwnedExpansion => "The save says the platform it was last played on doesn't own these expansions. A player reported losing an expansion's area after adding such items.",
            MissingGroup.Event => "From events and Star Paths.",
            MissingGroup.Quest => "Given by quests. Players report quests getting stuck when the reward is already there.",
            MissingGroup.Unlock => "Used by the mermaid form or the ship the game unlocks in its story. Adding them before that isn't tested.",
            MissingGroup.Skins => "A style only works for a house, building or character you already have. A style for something you don't have can break menus in the game.",
            MissingGroup.NotChecked => "Not in the community list: new, unfinished or developer items (some broke games).",
            MissingGroup.Unclear => "The lists give no type or notes, so they cannot be checked.",
            _ => "The lists mark them with a caution (for example \"can bug\" or \"careful\")."
        };

        /// <summary>Groups ticked when the dialog opens.</summary>
        public static bool CheckedByDefault(MissingGroup group) =>
            group is MissingGroup.Tested or MissingGroup.Ordinary or MissingGroup.Event;

        public static List<MissingItem> Find(JObject root, string listKey, ISet<long> owned)
        {
            var result = new List<MissingItem>();
            if (ItemDatabase.Table is not DataTable table) return result;

            foreach (DataRow row in table.Rows)
            {
                if (!long.TryParse(row["ID"]?.ToString(), out long id) || id <= 0 || owned.Contains(id)) continue;
                if (row["IS_TEST"] is true || row["UNSAFE"] is true) continue;
                if (ItemDatabase.Get(row, "SAFETY", "") == "UNSAFE") continue;

                var placement = ItemPlacement.Resolve(root, id.ToString());
                if (placement.Kind != PlacementKind.ListInventory || placement.ListKey != listKey || placement.Problem != null)
                    continue;

                result.Add(new MissingItem(id, ItemDatabase.Get(row, "LOCALIZED_DISPLAY_NAME", id.ToString()),
                                           Classify(root, row, placement)));
            }
            return result;
        }

        internal static MissingGroup Classify(JObject root, DataRow row, Placement placement)
        {
            string name = ItemDatabase.Get(row, "NAME", "");
            string type = ItemDatabase.Get(row, "TYPE", "");
            long.TryParse(row["ID"]?.ToString(), out long id);
            var record = ItemDatabase.FindRecord(id);
            string text = string.Join(" | ", ItemDatabase.Get(row, "NOTES", ""), ItemDatabase.Get(row, "CATEGORY", ""),
                                      record?.CatalogueCategory ?? "").ToUpperInvariant();

            if (placement.Warning != null)
                return ItemPlacement.UnlockWarning(root, type, NamePrefix(name)) != null ? MissingGroup.Unlock : MissingGroup.Other;

            // Technical objects the game places itself (world blockers, quest request items, terrain
            // changes): never something to hand out in bulk.
            if (TechnicalPrefixes.Contains(NamePrefix(name), StringComparer.OrdinalIgnoreCase))
                return MissingGroup.Other;

            // From an expansion the save itself says it doesn't own.
            if (ItemPlacement.MissingExpansion(root, row) != null)
                return MissingGroup.NotOwnedExpansion;

            // Someone tried it in the game and it worked: that beats every guess below.
            if (ItemDatabase.Get(row, "SAFETY", "") == "TESTED")
                return MissingGroup.Tested;

            // Warnings first: an item marked "can bug" or "do not craft" must never ride along in a
            // ticked group just because it also comes from an event or the shop. These are the same
            // items a single Add item asks about.
            if (ContainsAny(text.Replace("'", ""), HardCautions))
                return MissingGroup.Other;
            if (ItemPlacement.GetRiskWarning(row) != null)
                return name.Contains("Mission!", StringComparison.OrdinalIgnoreCase) ? MissingGroup.Quest : MissingGroup.Other;

            // The community list (Rubyelf) is the only source with category and notes. An item it does
            // not have is only known from the game files: new, unfinished or a developer item. This
            // follows every game update on its own - once the list adds an item, it leaves this group.
            if (!ItemDatabase.IsCommunityChecked(row))
                return MissingGroup.NotChecked;

            // Styles depend on what stands in the player's world (houses, shops, villagers, mounts).
            if (type.Equals("Skin", StringComparison.OrdinalIgnoreCase))
                return MissingGroup.Skins;

            // Paid content first, so a premium Star Path reward is never offered as a free event item.
            if (ContainsAny(text, "DLC", "EXPANSION", "RIFT IN TIME", "STORYBOOK VALE") ||
                ContainsAny(name.ToUpperInvariant(), "!EXPANSION0", "EXPANSION0", "!DLC"))
                return MissingGroup.Dlc;
            // Not "SHOP"/"STORE": Scrooge's Store sells ordinary items for Star Coins.
            if (ContainsAny(text, "PREMIUM", "BUNDLE", "MOONSTONE", "FOUNDER"))
                return MissingGroup.Shop;
            if (ContainsAny(text, "QUEST", "MISSION", "REQUEST") ||
                ContainsAny(name.ToUpperInvariant(), "VLG_", "MRQS_", "MISSION!"))
                return MissingGroup.Quest;
            if (ContainsAny(text, "EVENT", "SEASONAL", "STAR PATH", "STARPATH", "TIME LIMITED", "TIMELIMITED") ||
                name.Contains("Event!", StringComparison.OrdinalIgnoreCase))
                return MissingGroup.Event;

            return ItemDatabase.Get(row, "SAFETY", "") switch
            {
                "NO WARNINGS" => MissingGroup.Ordinary,
                "UNKNOWN" => MissingGroup.Unclear,
                _ => MissingGroup.Other
            };
        }

        /// <summary>Name prefixes of objects the game uses for its own world and quests.</summary>
        internal static readonly string[] TechnicalPrefixes =
        {
            "Blocker", "Request", "RequestItem", "GroundAlteration", "Mission", "Debug", "Dev", "Cheat"
        };

        /// <summary>Notes that say the item itself can cause trouble (apostrophes removed).</summary>
        private static readonly string[] HardCautions =
        {
            "CAN BUG", "COULD BUG", "CAREFUL", "DO NOT CRAFT", "DONT CRAFT", "BREAK QUEST", "BREAK GAME",
            "ONLY IF BUGGED", "UNLESS BROKEN QUEST", "IF YOU DONT HAVE THE DLC", "DONT USE", "DO NOT USE"
        };

        private static bool ContainsAny(string text, params string[] words) =>
            words.Any(w => text.Contains(w, StringComparison.Ordinal));

        internal static string NamePrefix(string name)
        {
            int slash = name.IndexOf('/');
            return slash > 0 ? name[..slash] : name;
        }
    }
}
