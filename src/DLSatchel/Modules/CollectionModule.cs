// Modules/CollectionModule.cs
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;

namespace DDV_SaveEditor_G.Modules
{
    public class CollectionDefinitionConfig
    {
        /// <summary>Prefix NAME w Excelu dla definicji CollectionSet (np. "CrittersRabbit").</summary>
        public string DefinitionPrefix { get; set; }

        /// <summary>Prefix NAME w Excelu dla elementów tej kolekcji (np. "Critter/").</summary>
        public string ItemPrefix { get; set; }

        /// <summary>DataTable z zaznaczonymi przez użytkownika elementami (petTable, mountTable...).</summary>
        public DataTable SelectionTable { get; set; }

        /// <summary>Nazwa kolumny w SelectionTable, gdzie trzymasz lokalizowaną nazwę.</summary>
        public string LocalizedNameColumn { get; set; }

        /// <summary>
        /// Jeśli ustawione: elementy kolekcji to wprost ID z tej kolumny SelectionTable
        /// (sejw 535: CompanionsAnimals/CompanionsNotable trzymają ID petów 1200xxxxx),
        /// zamiast dopasowania po lokalizowanej nazwie.
        /// </summary>
        public string? IdColumn { get; set; }
    }

    public static class CollectionModule
    {
        /// <summary>
        /// Zapisuje w JSON:
        /// 1) ustawia true dla elementów z SelectionTable,
        /// 2) false TYLKO dla elementów usuniętych z SelectionTable (RowState Deleted) –
        ///    reszta flag zostaje, żeby rozjazd Excela z sejwem nie kasował postępu,
        /// 3) uaktualnia State (Completed/NotComplete).
        /// </summary>
        public static void SaveCollections(
            JObject root,
            DataTable excel,
            IEnumerable<CollectionDefinitionConfig> configs)
        {
            Debug.WriteLine("[CollectionModule] SAVE ALL COLLECTIONS START");

            // 1) Pobranie tablicy collectionSets
            var collSets = root["Player"]?["CollectionSets"] as JArray;
            if (collSets == null)
            {
                Debug.WriteLine("[CollectionModule] ERROR: Player.CollectionSets not found");
                return;
            }

            // 2) Zanim iterujemy — zbudujmy z Excela jednorazowo pełną listę wierszy
            var allDefs = excel.AsEnumerable()
                .Where(r => r.Field<string>("TYPE") == "CollectionSet")
                .ToList();
            var allItems = excel.AsEnumerable().ToList();

            foreach (var cfg in configs)
            {
                Debug.WriteLine($"[CollectionModule] → Config '{cfg.DefinitionPrefix}'");

                // 3) Definicje tej kolekcji
                var defs = allDefs
                    .Where(r => (r.Field<string>("NAME") ?? "").StartsWith(cfg.DefinitionPrefix))
                    .ToList();
                Debug.WriteLine($"    Definitions found: {defs.Count}");

                // 4) Mapa lokalizowana nazwa → lista item-ID
                //    bierzemy EXCEL tylko z wierszami, których NAME zaczyna się od cfg.ItemPrefix
                var map = allItems
                    .Where(r => (r.Field<string>("NAME") ?? "").StartsWith(cfg.ItemPrefix))
                    .GroupBy(r => r.Field<string>("LOCALIZED_DISPLAY_NAME") ?? "")
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(r => long.Parse(r.Field<string>("ID"))).ToList()
                    );
                Debug.WriteLine($"    Item‐map keys: {map.Count}");

                // 5) Zbiory ID które user "odhaczył" / usunął
                var chosen = new HashSet<long>();
                var removed = new HashSet<long>();
                foreach (DataRow sel in cfg.SelectionTable.Rows)
                {
                    bool deleted = sel.RowState == DataRowState.Deleted;
                    var version = deleted ? DataRowVersion.Original : DataRowVersion.Current;
                    var target = deleted ? removed : chosen;

                    if (cfg.IdColumn != null)
                    {
                        target.Add(Convert.ToInt64(sel[cfg.IdColumn, version]));
                        continue;
                    }
                    var loc = sel[cfg.LocalizedNameColumn, version] as string;
                    if (loc != null && map.TryGetValue(loc, out var ids))
                        foreach (var id in ids)
                            target.Add(id);
                }
                removed.ExceptWith(chosen);
                Debug.WriteLine($"    Chosen IDs count: {chosen.Count}, removed: {removed.Count}");

                // 6) Dla każdej definicji pod filtra
                foreach (var def in defs)
                {
                    long defId = long.Parse(def.Field<string>("ID"));
                    string defName = def.Field<string>("NAME");
                    Debug.WriteLine($"    → Processing definition {defName} (ID={defId})");

                    // a) Znajdź obiekt JSON
                    var collObj = collSets
                        .OfType<JObject>()
                        .FirstOrDefault(o => (long?)o["CollectionDefinitionID"] == defId);
                    if (collObj == null)
                    {
                        Debug.WriteLine($"       SKIP: JSON entry not found");
                        continue;
                    }

                    var groupDataArr = collObj["GroupData"] as JArray;
                    if (groupDataArr == null)
                    {
                        Debug.WriteLine($"       SKIP: no GroupData");
                        continue;
                    }

                    // b) Iteruj grupy, resetuj i ustawiaj
                    int total = 0, setTrue = 0;
                    foreach (var grp in groupDataArr.OfType<JObject>())
                    {
                        var items = grp["GroupsCollectionItems"] as JObject;
                        if (items == null) continue;

                        // ustaw / zdejmij
                        foreach (var prop in items.Properties().ToList())
                        {
                            total++;
                            if (!long.TryParse(prop.Name, out long cid)) continue;
                            bool was = prop.Value.Type == JTokenType.Boolean && prop.Value.Value<bool>();
                            bool should = chosen.Contains(cid) || (was && !removed.Contains(cid));

                            if (was != should)
                            {
                                items[prop.Name] = should;
                                Debug.WriteLine($"       [{defName}] cid={cid}: {was}→{should}");
                            }
                            if (should) setTrue++;
                        }
                    }

                    // c) State tylko 2 opcje
                    // UWAGA: w części wersji sejwa CollectionSets NIE mają pola "State"
                    // (zweryfikowane na realnym profile.json) – nie dopisujemy go wtedy,
                    // żeby nie tworzyć struktur nieznanych grze.
                    if (collObj.ContainsKey("State"))
                    {
                        string newState = (setTrue == total)
                            ? "SetState_Completed"
                            : "SetState_NotComplete";
                        string oldState = collObj["State"]?.ToString() ?? "<none>";
                        collObj["State"] = newState;
                        Debug.WriteLine(
                            $"       State: {oldState}→{newState} ({setTrue}/{total})");
                    }
                    else
                    {
                        Debug.WriteLine($"       State field absent in this save – skipped ({setTrue}/{total})");
                    }
                }
            }

            Debug.WriteLine("[CollectionModule] SAVE ALL COLLECTIONS END");
        }
    }
}
