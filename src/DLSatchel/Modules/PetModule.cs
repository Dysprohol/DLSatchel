// PetModule.cs
using Newtonsoft.Json.Linq;
using System.Data;
using System.Diagnostics;
using System.Linq;

namespace DDV_SaveEditor_G.Modules
{
    public static class PetModule
    {
        private const string OriginalJsonColumn = "__OriginalJson";
        private const string EditableColumn = "__EditablePet";
        /// <summary>
        /// 1) JSON → DataTable (lista Companionów z Player.Pets: [{ "PetItemID": 120000002 }, ...])
        /// Kolumny: "Pet ID", "Name", "Localized Name"
        /// </summary>
        public static DataTable MapPets(JObject root)
        {
            var arr = root["Player"]?["Pets"] as JArray ?? new JArray();
            Debug.WriteLine($"[MapPets] JSON Pets array count = {arr.Count}");

            var tbl = new DataTable();
            tbl.Columns.Add("Pet ID", typeof(long));
            tbl.Columns.Add("Name", typeof(string));
            tbl.Columns.Add("Localized Name", typeof(string));
            tbl.Columns.Add(OriginalJsonColumn, typeof(string));
            tbl.Columns.Add(EditableColumn, typeof(bool));

            foreach (var tok in arr)
            {
                long id = ReadPetId(tok);
                bool editable = IsSupportedPet(root, id);
                var item = id > 0 ? ItemDatabase.Find(id) : null;
                string internalName = ItemDatabase.Get(item, "NAME", "Unrecognized companion entry");
                string displayName = ItemDatabase.Get(item, "LOCALIZED_DISPLAY_NAME", internalName);
                tbl.Rows.Add(id, internalName, displayName,
                    tok.ToString(Newtonsoft.Json.Formatting.None), editable);
            }

            tbl.AcceptChanges();
            Debug.WriteLine($"[MapPets] Returning table with {tbl.Rows.Count} rows.");
            return tbl;
        }

        public static void AddPetRow(DataTable petTable, long id, string? originalJson = null)
        {
            var dbRow = ItemDatabase.Find(id);
            string internalName = ItemDatabase.Get(dbRow, "NAME");
            petTable.Rows.Add(id, internalName, ItemDatabase.Get(dbRow, "LOCALIZED_DISPLAY_NAME", internalName),
                              (object?)originalJson ?? DBNull.Value, true);
        }

        public static bool IsSupportedPet(JObject root, long id)
            => id > 0 && ItemPlacement.Resolve(root, id.ToString()).Kind == PlacementKind.Pet;

        private static long ReadPetId(JToken token)
        {
            JToken? id = token.Type switch
            {
                JTokenType.Object => token["PetItemID"],
                JTokenType.Integer => token,
                _ => null
            };
            return long.TryParse(id?.ToString(), out long value) ? value : 0;
        }

        /// <summary>
        /// 2) DataTable → JSON (zapisywanie Companionów + kolekcji Critters).
        /// Kolekcje ruszamy tylko, gdy użytkownik faktycznie zmienił listę petów –
        /// inaczej błędne dopasowanie nazw w Excelu mogłoby wyzerować postęp kolekcji.
        /// </summary>
        public static void SavePets(JObject root, DataTable petTable, bool updateCollections)
        {
            Debug.WriteLine("[PetModule] SavePets START");

            if (root["Player"] is not JObject player || !player.ContainsKey("Pets"))
            {
                Debug.WriteLine("[PetModule] Player.Pets not found – skipped");
                return;
            }

            var savedPets = new JArray();
            foreach (DataRow row in petTable.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;

                long id = Convert.ToInt64(row["Pet ID"]);
                JToken? original = null;
                if (row[OriginalJsonColumn] is string json)
                {
                    try { original = JToken.Parse(json); }
                    catch (Newtonsoft.Json.JsonException) { /* malformed metadata is replaced by a minimal entry */ }
                }

                long originalId = original == null ? 0 : ReadPetId(original);
                bool editable = row[EditableColumn] is bool value && value;
                JToken saved = original != null && (!editable || originalId == id)
                    ? original.DeepClone()
                    : new JObject { ["PetItemID"] = id };

                savedPets.Add(saved);
                row[OriginalJsonColumn] = saved.ToString(Newtonsoft.Json.Formatting.None);
            }

            player["Pets"] = savedPets;
            Debug.WriteLine($"[PetModule] Wrote {petTable.Rows.Count} entries to Player.Pets");

            if (updateCollections && ItemDatabase.Table != null)
            {
                CollectionModule.SaveCollections(
                    root,
                    ItemDatabase.Table,
                    new[]
                    {
                        // starsze sejwy/dumpy: kolekcja Critters z ID 1201xxxxx, parowanie po nazwie
                        new CollectionDefinitionConfig {
                            DefinitionPrefix    = "Critters",
                            ItemPrefix          = "Critter/",
                            SelectionTable      = petTable,
                            LocalizedNameColumn = "Localized Name"
                        },
                        // sejw 535: CompanionsAnimals / CompanionsNotable z ID petów wprost
                        new CollectionDefinitionConfig {
                            DefinitionPrefix    = "Companions",
                            ItemPrefix          = "Pet/",
                            SelectionTable      = petTable,
                            LocalizedNameColumn = "Localized Name",
                            IdColumn            = "Pet ID"
                        },
                        // … inne konfiguracje
                    });
            }
            petTable.AcceptChanges();   // usunięte w tej sesji już zapisane – kolejny zapis ich nie powtarza
            Debug.WriteLine("[PetModule] SavePets END");
        }

        /// <summary>
        /// Player.CurrentPet istnieje tylko w części wersji sejwa – nie dopisujemy go.
        /// </summary>
        public static bool CanSetCurrentPet(JObject root)
            => root["Player"] is JObject p && p.ContainsKey("CurrentPet");

        public static long GetCurrentPet(JObject root)
        {
            var tok = root["Player"]?["CurrentPet"];
            return tok == null || tok.Type == JTokenType.Null ? 0 : tok.ToObject<long>();
        }

        public static void SetCurrentPet(JObject root, long id)
        {
            if (CanSetCurrentPet(root))
                root["Player"]!["CurrentPet"] = id;
        }
    }
}
