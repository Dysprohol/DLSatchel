using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using DDV.SaveEditor.Tests.TestData;
using DDV_SaveEditor_G.Modules;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace DDV.SaveEditor.Tests
{
    /// <summary>
    /// Runs the editor on real saves and real item lists. Nothing here is hard-coded to one game
    /// version: drop newer saves into pliki_testowe\saves and newer lists into
    /// pliki_testowe\lists-current (or point DLSATCHEL_TEST_SAVES / DLSATCHEL_TEST_LISTS at other
    /// folders) and run the tests again. Without saves the tests pass with a note.
    /// </summary>
    public class SaveIntegrityTests
    {
        private readonly ITestOutputHelper _output;
        public SaveIntegrityTests(ITestOutputHelper output) => _output = output;

        /* ---------------- where the local test data is ---------------- */

        private static string RepoRoot([CallerFilePath] string here = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

        private static string Folder(string envVar, string fallback) =>
            Environment.GetEnvironmentVariable(envVar) is { Length: > 0 } custom
                ? custom
                : Path.Combine(RepoRoot(), "pliki_testowe", fallback);

        private static List<string> SaveFiles()
        {
            string dir = Folder("DLSATCHEL_TEST_SAVES", "saves");
            return Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories).OrderBy(f => f).ToList()
                : new List<string>();
        }

        /// <summary>Newest lists if present, else the old lists the other integration tests pin.</summary>
        private static List<string> ListFiles()
        {
            string current = Folder("DLSATCHEL_TEST_LISTS", "lists-current");
            if (Directory.Exists(current))
            {
                var files = Directory.GetFiles(current, "*.xlsx").OrderBy(f => f).ToList();
                if (files.Count > 0) return files;
            }
            string old = Path.Combine(RepoRoot(), "pliki_testowe");
            return Directory.Exists(old) ? Directory.GetFiles(old, "*.xlsx").OrderBy(f => f).ToList() : new List<string>();
        }

        private static async Task<bool> LoadListsAsync()
        {
            var lists = ListFiles();
            if (lists.Count == 0) return false;
            // The game-file dump first, the community list on top (same order the editor uses).
            var sources = lists.Select(f => (f, f.Contains("Rubyelf", StringComparison.OrdinalIgnoreCase) ? 30 : 10));
            var (catalog, _) = await CatalogHarness.LoadAsync(sources, useCache: false);
            ItemDatabase.SetCatalog(catalog);
            return true;
        }

        private static (JObject Json, string Text, bool Encrypted) Read(string path)
        {
            Assert.True(SaveDecoder.TryDecode(path, out var json, out bool encrypted, out string text), $"Cannot read {path}");
            return (json, Compact(text.TrimStart('﻿')), encrypted);
        }

        /// <summary>
        /// The game's JSON without the spaces and line breaks between values. Everything else -
        /// how each number, date and text is written - stays exactly as the game wrote it.
        /// </summary>
        private static string Compact(string json)
        {
            var sb = new StringBuilder(json.Length);
            bool inString = false, escaped = false;
            foreach (char c in json)
            {
                if (inString)
                {
                    sb.Append(c);
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                }
                else if (c == '"') { inString = true; sb.Append(c); }
                else if (!char.IsWhiteSpace(c)) sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Writes the save the way Save Changes does and reads it back from the bytes.</summary>
        private static JObject WriteAndReadBack(JObject json, bool encrypted)
        {
            string text = json.ToString(Newtonsoft.Json.Formatting.None);
            string file = Path.Combine(XlsxBuilder.NewTempDirectory(), "profile.json");
            File.WriteAllBytes(file, encrypted ? SaveDecoder.Encode(text) : new UTF8Encoding(false).GetBytes(text));
            Assert.True(SaveDecoder.TryDecode(file, out var reread, out bool rereadEncrypted, out _));
            Assert.Equal(encrypted, rereadEncrypted);
            return reread;
        }

        private static string FirstDifference(string a, string b)
        {
            int i = 0;
            while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
            string Around(string s) => s.Substring(Math.Max(0, i - 60), Math.Min(120, s.Length - Math.Max(0, i - 60)));
            return $"first difference at character {i}:\n  game:   …{Around(a)}…\n  editor: …{Around(b)}…";
        }

        private bool NoSaves(List<string> saves)
        {
            if (saves.Count > 0) return false;
            _output.WriteLine("No test saves found - put game-written saves into pliki_testowe\\saves.");
            return true;
        }

        /* ---------------- 1. reading and writing changes nothing ---------------- */

        [Fact]
        public void EverySave_IsWrittenBackExactlyAsTheGameWroteIt()
        {
            var saves = SaveFiles();
            if (NoSaves(saves)) return;

            foreach (string path in saves)
            {
                var (json, text, encrypted) = Read(path);
                string written = json.ToString(Newtonsoft.Json.Formatting.None);
                Assert.True(written == text, $"{Path.GetFileName(path)}: {FirstDifference(text, written)}");
                Assert.True(JToken.DeepEquals(json, WriteAndReadBack(json, encrypted)), $"{Path.GetFileName(path)}: changed after encrypting");
                _output.WriteLine($"{Path.GetFileName(path)}: {text.Length:N0} characters, identical");
            }
        }

        [Fact]
        public async Task OpeningEverySectionAndSavingWithoutChanges_ChangesNothing()
        {
            var saves = SaveFiles();
            if (NoSaves(saves) || !await LoadListsAsync()) return;

            foreach (string path in saves)
            {
                var (json, text, _) = Read(path);

                // What Save Changes writes back from the editor's tables.
                if (InventoryModule.MapInventory(json) is DataTable backpack)
                    InventoryModule.UpdateJsonFromTable(json, backpack);
                PetModule.SavePets(json, PetModule.MapPets(json), updateCollections: false);
                foreach (var list in ListInventoryModule.GetInventories(json))
                    ListInventoryModule.Save(json, list.Key, ListInventoryModule.Map(json, list.Key));

                string written = json.ToString(Newtonsoft.Json.Formatting.None);
                Assert.True(written == text, $"{Path.GetFileName(path)}: {FirstDifference(text, written)}");
                _output.WriteLine($"{Path.GetFileName(path)}: unchanged");
            }
        }

        /* ---------------- 2. every kind of item ---------------- */

        private static string Prefix(string name)
        {
            int slash = name.IndexOf('/');
            if (slash > 0) return name[..slash];
            int bang = name.IndexOf('!');
            return bang > 0 ? name[..bang] : name;
        }

        /// <summary>Areas of the save an added item may change. Anything else changing is a bug.</summary>
        private static bool AllowedChange(string path) =>
            new[]
            {
                "Player.ListInventories", "Player.ContainerInventories.0.Inventory", "Player.Pets", "Player.Mounts",
                "Player.CurrencyAmounts", "Player.Tools", "Player.Cookbook.Recipes", "Player.UnlockedCraftingRecipes",
                "Player.PremadeDecorationPresets", "Player.CollectionSets"
            }.Any(p => path.TrimStart('$', '.').StartsWith(p, StringComparison.Ordinal));

        private static IEnumerable<string> Keys(JToken? record) =>
            record is JObject o ? o.Properties().Select(p => p.Name).OrderBy(n => n) : Enumerable.Empty<string>();

        [Fact]
        public async Task EveryKindOfItem_IsAddedInTheRightPlace_WithAFullRecord_AndNothingElseChanges()
        {
            var saves = SaveFiles();
            if (NoSaves(saves) || !await LoadListsAsync()) return;
            var problems = new List<string>();

            foreach (string path in saves)
            {
                string file = Path.GetFileName(path);
                var (original, _, encrypted) = Read(path);
                var json = (JObject)original.DeepClone();
                var owned = SaveItemIds.Collect(original);

                // One item of every kind (type + name prefix + destination) the save does not have yet.
                var picks = new Dictionary<string, (long Id, Placement Placement)>();
                foreach (DataRow row in ItemDatabase.Table!.Rows)
                {
                    if (!long.TryParse(ItemDatabase.Get(row, "ID", ""), out long id) || owned.ContainsKey(id)) continue;
                    var placement = ItemPlacement.Resolve(json, id.ToString());
                    if (placement.Kind == PlacementKind.Unsupported || placement.Problem != null) continue;
                    string kind = $"{ItemDatabase.Get(row, "TYPE", "")}/{Prefix(ItemDatabase.Get(row, "NAME", ""))} → {placement.Kind} {placement.ListKey}";
                    if (!picks.ContainsKey(kind) || (ItemDatabase.IsCommunityChecked(row) && !ItemDatabase.IsCommunityChecked(ItemDatabase.Find(picks[kind].Id))))
                        picks[kind] = (id, placement);
                }

                // Add them the way the editor does.
                var lists = new Dictionary<string, DataTable>();
                var backpack = InventoryModule.MapInventory(json);
                var pets = PetModule.MapPets(json);
                var currencyBefore = new Dictionary<long, long>();
                var added = new List<(string Kind, long Id, Placement Placement)>();
                foreach (var (kind, (id, placement)) in picks)
                {
                    bool ok;
                    switch (placement.Kind)
                    {
                        case PlacementKind.ListInventory:
                            if (!lists.TryGetValue(placement.ListKey!, out var table))
                                lists[placement.ListKey!] = table = ListInventoryModule.Map(json, placement.ListKey!);
                            table.Rows.Add(id, "?", "?", 1, "?");
                            ok = true;
                            break;
                        case PlacementKind.Backpack:
                            var slot = backpack?.AsEnumerable().FirstOrDefault(r => r.Field<string>("Item ID") == "0");
                            ok = slot != null;
                            if (ok) InventoryModule.PutInSlot(slot!, id.ToString(), 1, placement.DefaultState);
                            break;
                        case PlacementKind.Pet:
                            PetModule.AddPetRow(pets, id);
                            ok = true;
                            break;
                        case PlacementKind.Mount: ok = MountModule.AddMount(json, id); break;
                        case PlacementKind.Currency:
                            currencyBefore[id] = json["Player"]?["CurrencyAmounts"]?[id.ToString()]?.Value<long>() ?? 0;
                            ok = ItemPlacement.AddCurrency(json, id, 1) != null;
                            break;
                        case PlacementKind.Tool: ok = ItemPlacement.AddTool(json, id); break;
                        case PlacementKind.CookingRecipe: ok = ItemPlacement.AddRecipe(json, id, cooking: true); break;
                        case PlacementKind.CraftingRecipe: ok = ItemPlacement.AddRecipe(json, id, cooking: false); break;
                        case PlacementKind.DecorationPreset: ok = ItemPlacement.AddPreset(json, id); break;
                        default: ok = false; break;
                    }
                    if (!ok) { _output.WriteLine($"{file}: skipped {kind} ({id}) - no room or no such list in this save"); continue; }
                    ItemPlacement.MarkCollected(json, id);
                    added.Add((kind, id, placement));
                }
                if (backpack != null) InventoryModule.UpdateJsonFromTable(json, backpack);
                PetModule.SavePets(json, pets, updateCollections: false);   // as Save Changes does
                foreach (var (key, table) in lists) ListInventoryModule.Save(json, key, table);

                // Write the encrypted file and read it back, as the game will.
                var reread = WriteAndReadBack(json, encrypted);
                if (!JToken.DeepEquals(json, reread)) problems.Add($"{file}: the written file reads back differently");

                var nowOwned = SaveItemIds.Collect(reread);
                foreach (var (kind, id, placement) in added)
                {
                    string where = placement.Kind switch
                    {
                        PlacementKind.Currency => "currency",
                        _ => nowOwned.TryGetValue(id, out var section) ? section : "MISSING"
                    };
                    if (where == "MISSING")
                        problems.Add($"{file}: {kind} {id} is not in the save after writing");
                    if (placement.Kind == PlacementKind.Currency &&
                        reread["Player"]?["CurrencyAmounts"]?[id.ToString()]?.Value<long>() != currencyBefore[id] + 1)
                        problems.Add($"{file}: currency {id} did not go up by 1");

                    // The new record must look like the records the game wrote next to it.
                    (JToken? added1, JToken? sibling) = placement.Kind switch
                    {
                        PlacementKind.ListInventory => (
                            reread["Player"]?["ListInventories"]?[placement.ListKey!]?["Inventory"]?[id.ToString()],
                            (original["Player"]?["ListInventories"]?[placement.ListKey!]?["Inventory"] as JObject)?.Properties().FirstOrDefault()?.Value),
                        PlacementKind.Backpack => (
                            (reread["Player"]?["ContainerInventories"]?["0"]?["Inventory"] as JArray)?.FirstOrDefault(s => s["ItemID"]?.Value<long>() == id),
                            (original["Player"]?["ContainerInventories"]?["0"]?["Inventory"] as JArray)?.FirstOrDefault(s => s["ItemID"]?.Value<long>() > 0)),
                        PlacementKind.Pet => (
                            (reread["Player"]?["Pets"] as JArray)?.FirstOrDefault(p => p["PetItemID"]?.Value<long>() == id),
                            (original["Player"]?["Pets"] as JArray)?.FirstOrDefault()),
                        PlacementKind.Tool => (
                            (reread["Player"]?["Tools"] as JArray)?.FirstOrDefault(t => t["ToolItemID"]?.Value<long>() == id),
                            (original["Player"]?["Tools"] as JArray)?.FirstOrDefault()),
                        _ => ((JToken?)null, (JToken?)null)
                    };
                    if (added1 is JObject && sibling is JObject && !Keys(added1).SequenceEqual(Keys(sibling)))
                        problems.Add($"{file}: {kind} {id} record has fields [{string.Join(",", Keys(added1))}], the game's own records have [{string.Join(",", Keys(sibling))}]");
                }

                var changes = SaveDiffModule.Compare(original, reread, maxPaths: int.MaxValue).Paths;
                foreach (string changed in changes.Where(p => !AllowedChange(p)).Take(20))
                    problems.Add($"{file}: unexpected change at {changed}");

                _output.WriteLine($"{file}: added {added.Count} kinds of items, {changes.Count} changed values");
                foreach (var group in added.GroupBy(a => a.Placement.Kind).OrderBy(g => g.Key))
                    _output.WriteLine($"   {group.Key}: {group.Count()} kinds");
            }

            Assert.True(problems.Count == 0, string.Join("\n", problems.Take(60)));
        }

        [Fact]
        public async Task AddAllMissing_WithTheGroupsTickedAtFirst_AddsNothingFlagged_InAnyCollection()
        {
            var saves = SaveFiles();
            if (NoSaves(saves) || !await LoadListsAsync()) return;
            var problems = new List<string>();

            foreach (string path in saves)
            {
                string file = Path.GetFileName(path);
                var (original, _, encrypted) = Read(path);
                var json = (JObject)original.DeepClone();
                int added = 0;

                foreach (var list in ListInventoryModule.GetInventories(json).Where(l => ListInventoryModule.IsEditableType(l.ItemType)))
                {
                    // Owned the way the Add all missing button counts it.
                    var table = ListInventoryModule.Map(json, list.Key);
                    var owned = table.AsEnumerable().Select(r => r.Field<long>("Item ID")).ToHashSet();
                    owned.UnionWith(SaveItemIds.PlacedInWorld(json));
                    owned.UnionWith(SaveItemIds.Collect(json).Where(kv => kv.Value == "Backpack").Select(kv => kv.Key));

                    foreach (var item in MissingItems.Find(json, list.Key, owned).Where(m => MissingItems.CheckedByDefault(m.Group)))
                    {
                        var row = ItemDatabase.Find(item.Id)!;
                        string reason = RiskyAdditions.Reason(row);
                        string safety = ItemDatabase.Get(row, "SAFETY", "");
                        // Event items carry CAUTION ("from an event") and are ticked on purpose.
                        bool caution = safety == "CAUTION" && item.Group != MissingGroup.Event;
                        if (reason.Length > 0 || safety == "UNSAFE" || caution || ItemPlacement.GetRiskWarning(row) != null)
                            problems.Add($"{file}: {list.ItemType} {item.Id} {item.Name} ({item.Group}) - {(reason.Length > 0 ? reason : safety)}");
                        table.Rows.Add(item.Id, "?", "?", 1, "?");
                        added++;
                    }
                    ListInventoryModule.Save(json, list.Key, table);
                }

                var reread = WriteAndReadBack(json, encrypted);
                Assert.True(JToken.DeepEquals(json, reread), $"{file}: reads back differently");
                foreach (string changed in SaveDiffModule.Compare(original, reread, int.MaxValue).Paths
                             .Where(p => !p.TrimStart('$', '.').StartsWith("Player.ListInventories", StringComparison.Ordinal)).Take(10))
                    problems.Add($"{file}: unexpected change at {changed}");
                _output.WriteLine($"{file}: Add all missing with default groups adds {added:N0} items");
            }

            Assert.True(problems.Count == 0, string.Join("\n", problems.Take(40)));
        }

        /* ---------------- 3. what a new game update brings ---------------- */

        [Fact]
        public async Task EveryItemTypeInTheLists_HasAKnownPlaceOrIsRefused()
        {
            if (!await LoadListsAsync()) return;
            var save = SaveFiles().Select(f => Read(f).Json).FirstOrDefault() ?? new JObject { ["Player"] = new JObject() };

            var report = ItemDatabase.Table!.AsEnumerable()
                .GroupBy(r => ItemDatabase.Get(r, "TYPE", "(no type)"))
                .Select(g =>
                {
                    var kinds = g.Select(r => ItemPlacement.Resolve(save, ItemDatabase.Get(r, "ID", "0")).Kind)
                                 .GroupBy(k => k).OrderByDescending(k => k.Count())
                                 .Select(k => $"{k.Key} {k.Count()}");
                    return $"{g.Key,-22} {g.Count(),6}  {string.Join(", ", kinds)}";
                })
                .OrderBy(l => l)
                .ToList();
            foreach (string line in report) _output.WriteLine(line);

            // Resolve must never throw and every row must get an answer (a place or a reason).
            foreach (DataRow row in ItemDatabase.Table!.Rows)
            {
                var placement = ItemPlacement.Resolve(save, ItemDatabase.Get(row, "ID", "0"));
                Assert.True(placement.Kind != PlacementKind.Unsupported || !string.IsNullOrWhiteSpace(placement.Problem));
            }
        }
    }
}
