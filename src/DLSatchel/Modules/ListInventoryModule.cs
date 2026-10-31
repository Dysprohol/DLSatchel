using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

namespace DDV_SaveEditor_G.Modules
{
    /// <summary>Jeden magazyn z Player.ListInventories (np. szafa, meble, domy).</summary>
    public sealed class ListInventoryInfo
    {
        public required string Key { get; init; }
        /// <summary>Typ z sejwa bez prefixu, np. "Clothing" – zgodny z kolumną TYPE dumpu KOSDDV.</summary>
        public required string ItemType { get; init; }
        public required int Count { get; init; }

        public override string ToString() => $"{ItemType} ({Count})";
    }

    /// <summary>
    /// Magazyny w sejwie DDV żyją w Player.ListInventories[klucz].Inventory jako SŁOWNIK:
    ///   { "50000320": { "Amount": 3, "Marker": "ItemMarker_None" }, ... }
    /// Klucz → typ wg pola CompatibleItemType (zweryfikowane na sejwie 535):
    ///   0 Furniture, 1 Clothing, 2 ActivityItem, 3 Makeup, 4 Trimming, 5 Building,
    ///   6 TouchOfMagic, 7 Skin, 8 BoardGame, 9 AvatarFeature, 10 PhotoMode, 11 MountGear.
    /// Dlatego numer klucza bierzemy z sejwa, a nie na sztywno.
    /// </summary>
    internal static class ListInventoryModule
    {
        public const string ItemTypePrefix = "ItemType_";
        private const string ContainerTypeProperty = "ContainerItemType";
        private static readonly HashSet<string> EditableTypes = new(StringComparer.Ordinal)
        {
            "Furniture", "Clothing", "Makeup", "Trimming", "Building", "TouchOfMagic",
            "Skin", "BoardGame", "AvatarFeature", "PhotoMode", "MountGear"
        };

        /// <summary>True only for save destinations whose item routing is implemented and reviewed.</summary>
        public static bool IsEditableType(string itemType) => EditableTypes.Contains(itemType);

        /// <summary>The collection name shown in the editor for a save's CompatibleItemType.</summary>
        public static string DisplayName(string itemType) => itemType switch
        {
            "Clothing" => "Wardrobe",
            "Trimming" => "Trimmings",
            "Building" => "Buildings",
            "TouchOfMagic" => "Touch of Magic",
            "Skin" => "Character Styles",
            "AvatarFeature" => "Avatar Features",
            "PhotoMode" => "Photo Mode",
            "MountGear" => "Mount Gear",
            "BoardGame" => "Board Games",
            _ => itemType
        };

        public static List<ListInventoryInfo> GetInventories(JObject root)
        {
            var li = root["Player"]?["ListInventories"] as JObject;
            if (li == null) return new();

            return li.Properties()
                .Where(p => p.Value is JObject)
                .Select(p => new ListInventoryInfo
                {
                    Key = p.Name,
                    ItemType = (p.Value["CompatibleItemType"]?.ToString() ?? $"List {p.Name}")
                                   .Replace(ItemTypePrefix, ""),
                    Count = (p.Value["Inventory"] as JObject)?.Count ?? 0
                })
                .ToList();
        }

        private static JObject? GetInventory(JObject root, string key)
            => root["Player"]?["ListInventories"]?[key]?["Inventory"] as JObject;

        /// <summary>JSON → DataTable dla jednego magazynu.</summary>
        internal static DataTable Map(JObject root, string key)
        {
            var tbl = new DataTable();
            tbl.Columns.Add("Item ID", typeof(long));
            tbl.Columns.Add("Name", typeof(string));
            tbl.Columns.Add("Localized Name", typeof(string));
            tbl.Columns.Add("Amount", typeof(int));
            tbl.Columns.Add("Category", typeof(string));

            var inv = GetInventory(root, key);
            if (inv == null) return tbl;

            // The save itself says what this list stores; used when the catalog has no category.
            string? itemType = root["Player"]?["ListInventories"]?[key]?["CompatibleItemType"]?.ToString();
            if (!string.IsNullOrEmpty(itemType))
                tbl.ExtendedProperties[ContainerTypeProperty] = itemType.Replace(ItemTypePrefix, "");

            foreach (var prop in inv.Properties())
            {
                if (!long.TryParse(prop.Name, out long id))
                    continue;

                int amount = prop.Value?["Amount"]?.ToObject<int>() ?? 1;
                tbl.Rows.Add(id, "?", "?", amount, "?");
                FillNames(tbl.Rows[^1]);
            }

            tbl.AcceptChanges();
            return tbl;
        }

        internal static void FillNames(DataRow row)
        {
            var dbRow = ItemDatabase.Find(Convert.ToInt64(row["Item ID"]));
            string containerType = row.Table.ExtendedProperties[ContainerTypeProperty] as string ?? "?";
            row["Name"] = ItemDatabase.GetInternalName(dbRow);
            row["Localized Name"] = dbRow == null ? "(not in catalog)" : ItemDatabase.Get(dbRow, "LOCALIZED_DISPLAY_NAME");
            row["Category"] = ItemDatabase.GetCategory(dbRow, containerType);
        }

        /// <summary>
        /// DataTable → JSON. Nanosi tylko zmiany z tabeli (RowState), resztę sejwa zostawia 1:1 –
        /// gra trzyma tu też wpisy z Amount 0 (np. postawione budynki w magazynie "Building").
        ///  • usunięty wiersz → wpis znika,
        ///  • zmieniony → nowy Amount (Marker i inne pola zostają),
        ///  • dodany → nowy wpis { Amount, Marker: ItemMarker_None }.
        /// Po zapisie tabela ma AcceptChanges(), więc kolejny zapis nie powtarza zmian.
        /// </summary>
        internal static void Save(JObject root, string key, DataTable tbl)
        {
            var inv = GetInventory(root, key);
            if (inv == null) return;   // brak takiego magazynu – nie tworzymy struktur

            foreach (DataRow r in tbl.Rows)
            {
                if (r.RowState == DataRowState.Deleted)
                {
                    inv.Remove(Convert.ToInt64(r["Item ID", DataRowVersion.Original]).ToString());
                    continue;
                }
                if (r.RowState == DataRowState.Unchanged) continue;

                string id = Convert.ToInt64(r["Item ID"]).ToString();
                int amount = int.TryParse(r["Amount"]?.ToString(), out int a) ? a : 0;

                if (inv[id] is JObject existing)
                    existing["Amount"] = amount;
                else if (amount > 0)
                    inv[id] = new JObject { ["Amount"] = amount, ["Marker"] = "ItemMarker_None" };
            }

            tbl.AcceptChanges();
        }
    }
}
