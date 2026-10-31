using System.Data;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DDV_SaveEditor_G.Modules
{
    /// <summary>
    /// Plecak gracza: Player.ContainerInventories["0"].
    ///  • starsze sejwy (≤ ~444) trzymają wszystkie sloty, puste jako {"ItemID":0,"Amount":0},
    ///  • nowsze (535+) trzymają TYLKO zajęte sloty (Size 21, Inventory ma np. 1 wpis).
    /// Slot ma pole "State" (MealData, ConsummableData…) – musi przetrwać zapis.
    /// ItemID zapisujemy jako liczbę (stara wersja edytora zapisywała string).
    /// </summary>
    public static class InventoryModule
    {
        /* ukryte kolumny – nie są bindowane w gridzie */
        private const string StateCol = "State";
        private const string OrigIdCol = "Orig ID";
        private const string OriginalJsonCol = "__OriginalJson";

        private static JObject? Container(JObject json)
            => json["Player"]?["ContainerInventories"]?["0"] as JObject;

        public static int GetSlotSize(JObject json)
            => Container(json)?["Size"]?.ToObject<int>() ?? 0;

        /// <summary>JSON → DataTable (wszystkie sloty, puste jako "0").</summary>
        public static DataTable? MapInventory(JObject jsonData)
        {
            var container = Container(jsonData);
            if (container?["Inventory"] is not JArray inventory)
                return null;

            int slotSize = container["Size"]?.ToObject<int>() ?? 42;

            var inventoryTable = new DataTable();
            inventoryTable.Columns.Add("Item ID", typeof(string));
            inventoryTable.Columns.Add("NAME", typeof(string));
            inventoryTable.Columns.Add("LOCALIZED_DISPLAY_NAME", typeof(string));
            inventoryTable.Columns.Add("Amount", typeof(int));
            inventoryTable.Columns.Add("Category", typeof(string));
            inventoryTable.Columns.Add(StateCol, typeof(string));
            inventoryTable.Columns.Add(OrigIdCol, typeof(string));
            inventoryTable.Columns.Add(OriginalJsonCol, typeof(string));

            for (int i = 0; i < System.Math.Max(slotSize, inventory.Count); i++)
            {
                var item = inventory.ElementAtOrDefault(i);
                string id = item?["ItemID"]?.ToString() ?? "0";
                int amt = item?["Amount"]?.ToObject<int>() ?? 0;
                var state = item?["State"];
                string? stateTxt = state == null || state.Type == JTokenType.Null
                    ? null
                    : state.ToString(Formatting.None);

                var row = inventoryTable.NewRow();
                row["Item ID"] = id;
                row["Amount"] = amt;
                row[StateCol] = (object?)stateTxt ?? System.DBNull.Value;
                row[OrigIdCol] = id;
                row[OriginalJsonCol] = item is JObject ? item.ToString(Formatting.None) : System.DBNull.Value;
                FillNames(row);
                inventoryTable.Rows.Add(row);
            }

            return inventoryTable;
        }

        /// <summary>Uzupełnia nazwy/kategorię slotu na podstawie bazy przedmiotów.</summary>
        public static void FillNames(DataRow row)
        {
            string id = row["Item ID"]?.ToString() ?? "0";
            if (id == "0")
            {
                row["NAME"] = "No Item";
                row["LOCALIZED_DISPLAY_NAME"] = "No Item";
                row["Category"] = "None";
                return;
            }

            var dbRow = ItemDatabase.Find(id);
            row["NAME"] = ItemDatabase.GetInternalName(dbRow);
            row["LOCALIZED_DISPLAY_NAME"] = dbRow == null ? "(not in catalog)" : ItemDatabase.Get(dbRow, "LOCALIZED_DISPLAY_NAME");
            row["Category"] = ItemDatabase.GetCategory(dbRow);
        }

        /// <summary>Wkłada przedmiot do slotu z podanym State (JSON albo null) – State trafi do sejwa.</summary>
        public static void PutInSlot(DataRow row, string id, int amount, string? stateJson)
        {
            row["Item ID"] = id;
            row["Amount"] = amount;
            row[StateCol] = (object?)stateJson ?? System.DBNull.Value;
            row[OrigIdCol] = id;
            row[OriginalJsonCol] = System.DBNull.Value;
            FillNames(row);
        }

        /// <summary>Czyści slot (ID 0, bez State).</summary>
        public static void ClearSlot(DataRow row)
        {
            row["Item ID"] = "0";
            row["Amount"] = 0;
            row[StateCol] = System.DBNull.Value;
            row[OrigIdCol] = "0";
            row[OriginalJsonCol] = System.DBNull.Value;
            FillNames(row);
        }

        /// <summary>Zmienia rozmiar plecaka i zapisuje tabelę.</summary>
        public static void SaveInventory(JObject jsonData, DataTable inventoryTable, int slotSize)
        {
            var container = Container(jsonData);
            if (container == null) return;

            container["Size"] = slotSize;
            UpdateJsonFromTable(jsonData, inventoryTable);
        }

        /// <summary>DataTable → JSON, z zachowaniem formatu (pełny / tylko zajęte sloty) i State.</summary>
        public static void UpdateJsonFromTable(JObject jsonData, DataTable inventoryTable)
        {
            var container = Container(jsonData);
            if (container?["Inventory"] is not JArray old)
                return;

            int size = container["Size"]?.ToObject<int>() ?? 0;
            // brak pustych wpisów w oryginale ⇒ nowy format gry (tylko zajęte sloty)
            bool sparse = !old.OfType<JObject>().Any(e => e["ItemID"]?.ToObject<long>() == 0);

            var inventory = new JArray();
            foreach (DataRow row in inventoryTable.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;

                long.TryParse(row["Item ID"]?.ToString(), out long id);
                int amount = int.TryParse(row["Amount"]?.ToString(), out int qty) ? qty : 0;

                if (id <= 0 || amount <= 0)
                {
                    if (!sparse) inventory.Add(EmptySlot());
                    continue;
                }

                bool sameItem = row[OrigIdCol]?.ToString() == id.ToString();
                JToken state = JValue.CreateNull();
                if (sameItem && row[StateCol] is string s)
                    state = JToken.Parse(s);

                JObject? savedSlot = null;
                if (sameItem && row[OriginalJsonCol] is string originalJson)
                {
                    try { savedSlot = JObject.Parse(originalJson); }
                    catch (JsonException) { /* rebuild a canonical slot if cached metadata is invalid */ }
                }
                savedSlot ??= new JObject();
                savedSlot["ItemID"] = id;
                savedSlot["Amount"] = amount;
                savedSlot["State"] = state;
                inventory.Add(savedSlot);
                row[OriginalJsonCol] = savedSlot.ToString(Formatting.None);
                row[OrigIdCol] = id.ToString();
            }

            if (!sparse)
                while (inventory.Count < size)
                    inventory.Add(EmptySlot());

            container["Inventory"] = inventory;
        }

        private static JObject EmptySlot() => new()
        {
            ["ItemID"] = 0,
            ["Amount"] = 0,
            ["State"] = null
        };
    }
}
