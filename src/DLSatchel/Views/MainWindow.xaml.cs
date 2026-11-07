using Microsoft.Win32;
using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using DDV_SaveEditor_G.Modules;
using DDV_SaveEditor_G.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace DDV_SaveEditor_G
{
    public partial class MainWindow : Window
    {
        private string jsonFilePath;
        private string decodedCopyPath;
        private JObject jsonData;
        private JObject? loadedSaveSnapshot;
        private DataTable petTable;
        private bool petsDirty;

        /* Storage: klucz ListInventories → tabela (tylko te, które użytkownik otworzył) */
        private readonly Dictionary<string, DataTable> storageTables = new();
        private readonly Dictionary<string, DataGrid> storageGrids = new();
        private readonly Dictionary<string, ListInventoryInfo> storageInfos = new();
        private bool suppressSlotEvent;

        /* typ przedmiotu (kolumna TYPE), jakiego oczekuje dana zakładka */
        private const string BackpackItemType = "ActivityItem";
        private const string PetItemType = "Companion";
        private const string ClothingItemType = "Clothing";
        private static readonly string[] InventoryCategoryTabs =
        {
            "Furniture", "Makeup", "Trimming", "Building", "TouchOfMagic", "Skin",
            "BoardGame", "AvatarFeature", "PhotoMode", "MountGear", "Environment"
        };

        /* klucz ListInventories szafy (z CompatibleItemType, w 535 = "1") */
        private string? clothesKey;

        public MainWindow()
        {
            InitializeComponent();
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            PopulateStorage();
            InitializeUpdates();
            Loaded += MainWindow_Loaded;
        }

        /* =================================================================
         *  Baza przedmiotów (Excel)
         * ================================================================= */

        /// <summary>Loads the user's single configured catalog, if exactly one source is enabled.</summary>
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= MainWindow_Loaded;
            await TryAutoLoadExcelAsync();

            // A save passed on the command line ("Open with", or dropped on the .exe).
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && File.Exists(args[1]))
                LoadSave(args[1], quiet: true);
        }

        private async Task TryAutoLoadExcelAsync()
        {
            try
            {
                string configPath = SourcesConfigStore.ResolvePath(null);
                if (!File.Exists(configPath))
                {
                    DbStatusText.Text = "Choose an item catalog with Item Catalog or Sources… to begin.";
                    return;
                }

                var config = SourcesConfigStore.LoadOrCreate(configPath, saveWhenCreated: false);
                if (config.Sources.Any(source => source.Enabled))
                {
                    DbStatusText.Text = "Loading your selected item catalogs…";
                    await LoadConfiguredDatabaseAsync(config, showErrors: false);
                }
                else
                {
                    DbStatusText.Text = "Choose an item catalog with Item Catalog or Sources… to begin.";
                }
            }
            catch (Exception ex)
            {
                DbStatusText.Text = "Could not load your configured item catalog.";
                System.Diagnostics.Debug.WriteLine($"Catalog startup load failed: {ex}");
            }
        }

        private void LoadExcel_Click(object sender, RoutedEventArgs e)
            => PickCatalogFiles("Choose Item Catalogs (replaces the current ones)", replaceCurrent: true);

        private void AddCatalog_Click(object sender, RoutedEventArgs e)
            => PickCatalogFiles("Add Item Catalogs to the current ones", replaceCurrent: false);

        private void PickCatalogFiles(string title, bool replaceCurrent)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "Item catalogs (*.xlsx;*.xlsm;*.csv;*.tsv)|*.xlsx;*.xlsm;*.csv;*.tsv|All files (*.*)|*.*",
                Title = title,
                Multiselect = true
            };

            if (openFileDialog.ShowDialog() == true)
                _ = LoadCatalogFilesAsync(openFileDialog.FileNames, replaceCurrent);
        }

        private void Sources_Click(object sender, RoutedEventArgs e)
        {
            var window = new SourcesWindow();
            window.Owner = this;
            window.ShowDialog();
            if (ItemDatabase.Table is DataTable table)
                ApplyCatalogTable(table);
        }

        private async Task LoadConfiguredDatabaseAsync(SourcesConfig config, bool showErrors)
        {
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                var table = await ItemDatabase.LoadFromConfigAsync(config);
                ApplyCatalogTable(table);
            }
            catch (Exception ex)
            {
                if (showErrors) MessageBox.Show($"Error loading configured catalogs: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                else DbStatusText.Text = $"The item catalog could not be loaded. Check its file path in Sources… or choose a file with Item Catalog.";
            }
            finally { Mouse.OverrideCursor = null; }
        }

        private void ApplyCatalogTable(DataTable table)
        {
            ExcelDataGrid.ItemsSource = table.DefaultView;
            UpdateTypeComboBox(table);
            TypeComboBox.IsEnabled = true;
            UpdateCatalogOwnership();
            ApplyFilters();
            DbStatusText.Text = $"Item catalog: {table.Rows.Count} entries from " +
                                string.Join(", ", ItemDatabase.LoadedFiles.Select(Path.GetFileName));
            if (jsonData != null)
            {
                RefreshInventoryDataGrid();
                PopulatePets();
                PopulateMounts();
                PopulateTools();
                PopulatePresets();
                foreach (var tbl in storageTables.Values)
                    foreach (DataRow row in tbl.Rows)
                        if (row.RowState != DataRowState.Deleted) ListInventoryModule.FillNames(row);
            }
        }

        private async Task LoadCatalogFilesAsync(IEnumerable<string> paths, bool replaceCurrent)
        {
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                DbStatusText.Text = "Loading item catalogs…";
                var table = await ItemDatabase.LoadCatalogFilesAsync(paths, replaceCurrent);
                ApplyCatalogTable(table);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading item catalog: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        /* =================================================================
         *  Wczytywanie sejwa
         * ================================================================= */

        private void AutoDetectSave_Click(object sender, RoutedEventArgs e)
        {
            var saves = SaveAutoDetect.FindAllSaves();

            if (saves.Count == 0)
            {
                MessageBox.Show("No Steam save files detected.",
                                "No Saves Found",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 1) Tylko jeden save → ładujemy bez pytania
            if (saves.Count == 1)
            {
                LoadSave(saves[0].Profile, quiet: false);
                return;
            }

            // 2) Kilka save’ów → okno wyboru
            var wnd = new SaveSelectorWindow(saves) { Owner = this };

            if (wnd.ShowDialog() == true && wnd.SelectedSave != null)
                LoadSave(wnd.SelectedSave.Profile, quiet: false);
        }

        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Game save (profile.json)|profile.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
                Title = "Open Game Save"
            };
            if (dlg.ShowDialog() == true)
                LoadSave(dlg.FileName, quiet: false);
        }

        private bool LoadSave(string profilePath, bool quiet)
        {
            // spróbuj wczytać (plain‑JSON albo zaszyfrowany); tworzy .bak przy pierwszym odczycie
            if (!SaveDecoder.TryLoad(profilePath, out var decoded, out var json))
            {
                MessageBox.Show("File is neither valid JSON nor a recognised encrypted save.",
                                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            jsonFilePath = profilePath;
            decodedCopyPath = decoded;
            jsonData = json;
            loadedSaveSnapshot = (JObject)json.DeepClone();
            HighlightOwnershipCheckBox.IsEnabled = true;
            UpdateCatalogOwnership();

            PopulatePlayerFields();
            RefreshInventoryDataGrid();
            PopulatePets();
            PopulateMounts();
            PopulateTools();
            PopulatePresets();
            PopulateStorage();
            UpdateCollectionHelpText();
            UpdateInventorySlotsComboBox();
            SlotUpgradeComboBox.IsEnabled = true;
            CustomSlotSizeButton.IsEnabled = true;
            ValidateSaveButton.IsEnabled = true;

            if (!quiet)
            {
                string version = jsonData["GameInfo"]?["Version"]?.ToString() ?? "?";
                MessageBox.Show(
                    $"Save file loaded successfully! (save version {version})\n" +
                    (SaveDecoder.LastLoadWasEncrypted ? "Decoded copy and backup created." : "Backup created."),
                    "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return true;
        }

        private void DiscardChanges_Click(object sender, RoutedEventArgs e)
        {
            if (jsonFilePath == null) return;

            try
            {
                if (LoadSave(jsonFilePath, quiet: true))
                    MessageBox.Show("All unsaved changes have been discarded.",
                                    "Information", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error while discarding changes: {ex.Message}",
                                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /* =================================================================
         *  Zapis
         * ================================================================= */

        private void SaveChanges_Click(object sender, RoutedEventArgs e)
        {
            if (jsonData == null)
            {
                MessageBox.Show("Load a save first.", "No Save Loaded");
                return;
            }

            try
            {
                // zatwierdź komórkę, która jest w trakcie edycji
                bool playerGridCommitted = PlayerInventoryDataGrid.CommitEdit(DataGridEditingUnit.Row, true);
                bool clothesGridCommitted = ClothesDataGrid.CommitEdit(DataGridEditingUnit.Row, true);
                bool storageGridsCommitted = storageGrids.Values.All(grid =>
                    grid.CommitEdit(DataGridEditingUnit.Cell, true) && grid.CommitEdit(DataGridEditingUnit.Row, true));
                if (!playerGridCommitted || !storageGridsCommitted || !clothesGridCommitted)
                {
                    MessageBox.Show("Finish editing the current cell before saving.",
                                    "Invalid Value", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!ValidateBeforeSave(out string validationError))
                {
                    MessageBox.Show(validationError, "Cannot Save", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Zapisz dane gracza (poziom, waluty, nazwa)
                SavePlayerFields();

                var inventoryTable = (PlayerInventoryDataGrid.ItemsSource as DataView)?.Table;
                if (inventoryTable != null)
                    InventoryModule.UpdateJsonFromTable(jsonData, inventoryTable);

                if (petTable != null)
                    PetModule.SavePets(jsonData, petTable, updateCollections: petsDirty);

                foreach (var (key, tbl) in storageTables)
                    ListInventoryModule.Save(jsonData, key, tbl);

                var changes = SaveDiffModule.Compare(loadedSaveSnapshot, jsonData);
                if (!changes.HasChanges)
                {
                    MessageBox.Show("There are no changes to save.", "No Changes", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                string review = string.Join("\n", SaveDiffModule.ReadableSections(jsonData, changes.Paths)
                                                                 .Select(section => "• " + section));
                if (changes.OmittedPathCount > 0)
                    review += $"\n… and {changes.OmittedPathCount} more change(s)";

                if (MessageBox.Show(
                        "These parts of your save will change:\n\n" +
                        review +
                        "\n\nThe game must be closed or it may overwrite this file when it exits. Save these changes?",
                        "Review Changes", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;

                // Zapisz JSON do pliku – jeśli oryginał był zaszyfrowany,
                // SaveDecoder.Save zapisze go z powrotem w formacie gry
                // (ZIP → PKCS7 → AES-256-ECB), zweryfikowane round-tripem.
                string? backupPath = SaveDecoder.Save(jsonFilePath, jsonData);
                petsDirty = false;
                loadedSaveSnapshot = (JObject)jsonData.DeepClone();
                UpdateCatalogOwnership();

                RefreshInventoryDataGrid();
                MessageBox.Show("All changes were saved successfully.\n" +
                                (backupPath == null
                                    ? "A new save file was created; there was no previous file to back up."
                                    : $"Backup of the previous file: {backupPath}"),
                                "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not save changes: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ValidateSave_Click(object sender, RoutedEventArgs e)
        {
            if (jsonData == null)
            {
                MessageBox.Show("Load a save first.", "Save Check", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (ItemDatabase.Table == null)
            {
                MessageBox.Show("Load the item catalog to check item destinations in the save.",
                                "Catalog Not Loaded", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var issues = SaveValidationModule.Analyze(jsonData);
            if (issues.Count == 0)
            {
                MessageBox.Show("No known placement issues were found. This check cannot confirm every unknown game field.",
                                "Save Check", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            const int visibleLimit = 24;
            string details = string.Join("\n\n", issues.Take(visibleLimit).Select(issue =>
                $"[{issue.Level}] {issue.Path}\n{issue.Message}"));
            if (issues.Count > visibleLimit)
                details += $"\n\n… and {issues.Count - visibleLimit} more item(s).";

            MessageBox.Show($"Read-only check: {issues.Count} note(s). Nothing was changed.\n\n{details}",
                            "Save Check", MessageBoxButton.OK,
                            issues.Any(i => i.Level == SaveIssueLevel.Error) ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        private void CompareSaves_Click(object sender, RoutedEventArgs e)
        {
            var firstDialog = new OpenFileDialog { Filter = "Save files|profile.json;*.json;*.*|All files|*.*", Title = "Choose the First Save to Compare" };
            if (firstDialog.ShowDialog() != true) return;

            var secondDialog = new OpenFileDialog { Filter = "Save files|profile.json;*.json;*.*|All files|*.*", Title = "Choose the Second Save to Compare" };
            if (secondDialog.ShowDialog() != true) return;
            if (string.Equals(firstDialog.FileName, secondDialog.FileName, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Choose two different files.", "Compare Saves", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!SaveDecoder.TryDecode(firstDialog.FileName, out var first, out _) ||
                !SaveDecoder.TryDecode(secondDialog.FileName, out var second, out _))
            {
                MessageBox.Show("One of the profiles could not be read as JSON or an encrypted DDV save.",
                                "Read Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var diff = SaveDiffModule.Compare(first, second, maxPaths: 30);
            if (!diff.HasChanges)
            {
                MessageBox.Show("The JSON contents of these profiles are identical.", "Compare Saves",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string paths = string.Join("\n", SaveDiffModule.ReadableSections(second, diff.Paths).Select(section => "• " + section));
            if (diff.OmittedPathCount > 0) paths += $"\n… and {diff.OmittedPathCount} more change(s)";
            MessageBox.Show($"Read-only comparison:\n{Path.GetFileName(firstDialog.FileName)} → {Path.GetFileName(secondDialog.FileName)}\n" +
                            $"Parts that differ:\n\n{paths}",
                            "Compare Saves", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /* =================================================================
         *  Filtry listy przedmiotów
         * ================================================================= */

        private void SearchFilter_Changed(object sender, TextChangedEventArgs e) => ApplyFilters();
        private void TypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilters();
        private void HideUnsafe_Changed(object sender, RoutedEventArgs e) => ApplyFilters();
        private void HideCaution_Changed(object sender, RoutedEventArgs e) => ApplyFilters();
        private void HideTest_Changed(object sender, RoutedEventArgs e) => ApplyFilters();

        private void HighlightOwnership_Changed(object sender, RoutedEventArgs e)
        {
            if (OwnershipColumn != null)
                OwnershipColumn.Visibility = HighlightOwnershipCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            UpdateCatalogOwnership();
        }

        private void UpdateCatalogOwnership()
        {
            if (ItemDatabase.Table is not DataTable table || !table.Columns.Contains("OWNED_STATUS")) return;
            HighlightOwnershipCheckBox.IsEnabled = jsonData != null;
            bool show = HighlightOwnershipCheckBox.IsChecked == true && jsonData != null;
            var owned = show ? GetOwnedCatalogIds(jsonData) : new HashSet<long>();
            foreach (DataRow row in table.Rows)
            {
                if (!long.TryParse(row["ID"]?.ToString(), out long id)) continue;
                row["OWNED_STATUS"] = !show ? "" : owned.Contains(id) ? "Owned" : "Not owned";
            }
        }

        private static HashSet<long> GetOwnedCatalogIds(JObject? root)
        {
            var ids = new HashSet<long>();
            if (root?["Player"] is not JObject player) return ids;

            if (player["ListInventories"] is JObject inventories)
                foreach (var inventory in inventories.Properties())
                    if (inventory.Value["Inventory"] is JObject items)
                        foreach (var item in items.Properties()) AddOwnedId(ids, item.Name);

            // Only the player's backpack, never other ContainerInventories (character data).
            if (player["ContainerInventories"]?["0"]?["Inventory"] is JArray backpack)
                foreach (var item in backpack) AddOwnedId(ids, item?["ItemID"]?.ToString());
            AddOwnedArray(ids, player["Pets"] as JArray, "PetItemID");
            AddOwnedArray(ids, player["Mounts"] as JArray, "MountItemID");
            AddOwnedArray(ids, player["Tools"] as JArray, "ToolItemID");
            AddOwnedArray(ids, player["PremadeDecorationPresets"]?["DecorationPresets"] as JArray, "PresetID");
            AddOwnedArray(ids, player["Cookbook"]?["Recipes"] as JArray, "RecipeID");
            AddOwnedArray(ids, player["UnlockedCraftingRecipes"] as JArray, "RecipeID");
            if (player["CurrencyAmounts"] is JObject currencies)
                foreach (var currency in currencies.Properties()) AddOwnedId(ids, currency.Name);

            // CollectionSets are the game's explicit collected/unlocked flags; read only.
            if (player["CollectionSets"] is JArray sets)
                foreach (var items in sets.OfType<JObject>()
                             .SelectMany(set => set["GroupData"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                             .Select(group => group["GroupsCollectionItems"] as JObject))
                    if (items != null)
                        foreach (var item in items.Properties())
                            if (item.Value.Type == JTokenType.Boolean && item.Value.Value<bool>()) AddOwnedId(ids, item.Name);
            return ids;
        }

        private static void AddOwnedArray(HashSet<long> ids, JArray? values, string objectKey)
        {
            if (values == null) return;
            foreach (var value in values)
            {
                if (value is JObject obj)
                {
                    AddOwnedId(ids, obj[objectKey]?.ToString());
                    if (obj[objectKey] == null && obj["ItemID"] != null) AddOwnedId(ids, obj["ItemID"]!.ToString());
                }
                else AddOwnedId(ids, value.ToString());
            }
        }

        private static void AddOwnedId(HashSet<long> ids, string? value)
        {
            if (long.TryParse(value, out long id) && id > 0) ids.Add(id);
        }

        private void ApplyFilters()
        {
            // Guard‑clauses
            if (ExcelDataGrid?.ItemsSource is not DataView view)
                return;

            var clauses = new List<string>();

            /* 1 ▪ name  --------------------------------------------------- */
            string name = SearchNameBox.Text.Trim().Replace("'", "''");       // escape '
            if (!string.IsNullOrEmpty(name))
                clauses.Add(
                    $"([NAME] LIKE '%{name}%' OR [LOCALIZED_DISPLAY_NAME] LIKE '%{name}%')");

            /* 2 ▪ ID  ------------------------------------------------------ */
            string idPart = SearchIdBox.Text.Trim().Replace("'", "''");
            if (!string.IsNullOrEmpty(idPart))
                clauses.Add($"[ID] LIKE '%{idPart}%'");

            /* 3 ▪ scope to the selected inventory category ---------------- */
            // TYPE alone is not enough for Companion: the game keeps pets and
            // mounts in different save structures even though both use it.
            // KIND / SUBTYPE / PLACEMENT come from the shared catalog taxonomy.
            string? categoryRule = CurrentCatalogRule();
            if (!string.IsNullOrEmpty(categoryRule))
                clauses.Add(categoryRule);

            /* 4 ▪ type  ---------------------------------------------------- */
            if (TypeComboBox.SelectedItem is string type && type != "All" && !string.IsNullOrEmpty(type))
                clauses.Add($"[TYPE] = '{type.Replace("'", "''")}'");

            /* 5 ▪ unsafe --------------------------------------------------- */
            if (HideUnsafeCheckBox.IsChecked == true)
                clauses.Add("[UNSAFE] = false");

            /* 6 ▪ caution -------------------------------------------------- */
            if (HideCautionCheckBox.IsChecked == true)
                clauses.Add("[SAFETY] <> 'CAUTION'");

            /* 7 ▪ developer test assets ------------------------------------ */
            if (HideTestCheckBox.IsChecked == true)
                clauses.Add("[IS_TEST] = false");

            /* 8 ▪ apply / clear  ------------------------------------------ */
            view.RowFilter = clauses.Count > 0 ? string.Join(" AND ", clauses) : string.Empty;
        }

        private string? CurrentCatalogRule() => CurrentTab switch
        {
            // These groups share the game's TYPE but have distinct save locations.
            "Pets" => "[KIND] = 'Companion' AND [PLACEMENT] = 'Pet' AND [SUBTYPE] IN ('Pet', 'Critter')",
            "Mounts" => "[KIND] = 'Companion' AND [SUBTYPE] = 'Mount'",
            // Tools and held accessories share Player.Tools; upgrades are not tools.
            "Tools" => "[KIND] = 'Tool' AND [PLACEMENT] = 'Tool' AND ([TYPE] = 'Tool' OR [CATEGORY] = 'Accessory')",
            "Presets" => "[KIND] = 'DecorationPreset'",

            // Use the catalog's resolved category so rows inferred from a source
            // category are still shown with their CAUTION metadata.
            "Clothes" => "[KIND] = 'Clothing' AND [PLACEMENT] = 'Wardrobe'",
            "Backpack" => "[KIND] = 'ActivityItem' AND [PLACEMENT] = 'Backpack'",
            _ when TryGetCurrentStorageItemType(out var itemType) =>
                Enum.TryParse<DDV_SaveEditor_G.Data.ItemKind>(itemType, true, out var kind) &&
                kind != DDV_SaveEditor_G.Data.ItemKind.Unknown
                    ? $"[KIND] = '{kind}'"
                    : $"[TYPE] = '{itemType.Replace("'", "''")}' AND [PLACEMENT] <> 'None'",
            _ => null
        };

        /* ---- tylko cyfry w SearchIdBox ---- */
        private void DigitsOnly(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !e.Text.All(char.IsDigit);
        }

        private void UpdateTypeComboBox(DataTable dataTable)
        {
            var uniqueTypes = dataTable.AsEnumerable()
                .Select(row => row["TYPE"].ToString())
                .Where(type => !string.IsNullOrEmpty(type))
                .Distinct()
                .OrderBy(type => type)
                .ToList();

            uniqueTypes.Insert(0, "All");
            TypeComboBox.ItemsSource = uniqueTypes;
            TypeComboBox.SelectedIndex = 0;
        }

        /// <summary>Przełącza filtr typu na ten, którego oczekuje zakładka (jeśli istnieje w bazie).</summary>
        private void SuggestType(string? type)
        {
            if (TypeComboBox.ItemsSource is not List<string> types) return;
            TypeComboBox.SelectedItem = type != null && types.Contains(type) ? type : "All";
        }

        private string? CurrentTab
        {
            get
            {
                if (InventoryTabs.SelectedItem is not TabItem tab)
                    return null;

                if (tab.Tag is string key && !string.IsNullOrWhiteSpace(key))
                    return key;

                if (tab.Header is string text)
                    return text;

                return (tab.Header as StackPanel)?.Children
                    .OfType<TextBlock>()
                    .FirstOrDefault()?.Text;
            }
        }

        private void InventoryTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // SelectionChanged bąbelkuje z gridów/comboboxów w zakładkach – reagujemy tylko na same zakładki
            if (!ReferenceEquals(e.OriginalSource, InventoryTabs)) return;

            switch (CurrentTab)
            {
                case "Pets":
                case "Mounts":
                case "Tools":
                case "Presets":
                case "Clothes":
                case "Backpack":
                    // The tab's catalog scope is based on the resolved save kind;
                    // leaving TYPE at All also keeps category-inferred workbook rows visible.
                    SuggestType(null);
                    break;
            }
            if (IsCurrentStorageTab)
                SuggestType(null);

            // A tab may inherit the same raw TYPE filter from the previous tab;
            // reapply its subtype-specific scope even when SelectedItem is unchanged.
            ApplyFilters();

            bool readOnlyTab = CurrentTab == "Mounts" || (IsCurrentStorageTab &&
                (!TryGetCurrentStorageInfo(out var storageInfo) || !ListInventoryModule.IsEditableType(storageInfo.ItemType)));
            AddItemButton.IsEnabled = !readOnlyTab;
            RemoveSelectedButton.IsEnabled = !readOnlyTab && CurrentTab is not ("Tools" or "Presets");
            UpdateCollectionHelpText();
        }

        private void UpdateCollectionHelpText()
        {
            if (jsonData == null)
            {
                CollectionHelpText.Text = "Collections in your save, organized by category.";
                return;
            }

            CollectionHelpText.Text = CurrentTab switch
            {
                "Backpack" => "Backpack: items are stored in inventory slots. The game decides which items belong here.",
                "Pets" => "Companions: pets and critters are stored in the companion collection. New entries may show a save-format warning.",
                "Mounts" => "Mounts are progression records. This section is read-only until their save format is verified.",
                "Tools" => "Tools & accessories: tools and held items such as umbrellas and parasols. New ones are added unequipped; removing is not supported.",
                "Presets" => "Presets: Gameloft's ready-made decoration sets. An added preset shows up in the game's preset menu; removing is not supported.",
                "Clothes" => "Wardrobe: owned clothing and appearance items are listed here.",
                _ when TryGetCurrentStorageItemType(out var itemType) && !storageInfos.ContainsKey(itemType) =>
                    $"{DisplayInventoryType(itemType)} is not present in this save.",
                _ when TryGetCurrentStorageItemType(out var itemType) && !ListInventoryModule.IsEditableType(itemType) =>
                    $"{DisplayInventoryType(itemType)} is shown read-only because its save format is not verified.",
                _ when TryGetCurrentStorageItemType(out var itemType) =>
                    $"{DisplayInventoryType(itemType)} collection. Items are routed here automatically when the game stores them in this category.",
                _ => "Choose a category tab to view that part of your save."
            };
        }

        private void ExcelDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ExcelDataGrid.SelectedItem is DataRowView selectedRow)
            {
                ItemIDTextBox.Text = selectedRow["ID"]?.ToString() ?? string.Empty;
                ItemAmountTextBox.Text = "1";
            }
        }

        /* =================================================================
         *  Gracz
         * ================================================================= */

        private void PopulatePlayerFields()
        {
            var playerData = PlayerModule.MapPlayerData(jsonData);

            if (playerData == null)
            {
                MessageBox.Show("Player data not found in JSON.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            NameBox.Text = playerData["Name"];
            LevelBox.Text = playerData["Level"];
            XpBox.Text = playerData["Xp"];
            DreamlightBox.Text = playerData.GetValueOrDefault("Dreamlight", "0");
            StarCoinsBox.Text = playerData.GetValueOrDefault("StarCoins", "0");
            PixelDustBox.Text = playerData.GetValueOrDefault("PixelDust", "0");
            MistBox.Text = playerData.GetValueOrDefault("Mist", "0");
            DaisyCoinsBox.Text = playerData.GetValueOrDefault("DaisyCoins", "0");
            StorybookMagicBox.Text = playerData.GetValueOrDefault("StorybookMagic", "0");
        }

        private void SavePlayerFields()
        {
            var playerData = new Dictionary<string, string>
            {
                { "Name", NameBox.Text },
                { "Level", LevelBox.Text },
                { "Xp", XpBox.Text },
                { "Dreamlight", DreamlightBox.Text },
                { "StarCoins", StarCoinsBox.Text },
                { "PixelDust", PixelDustBox.Text },
                { "Mist", MistBox.Text },
                { "DaisyCoins", DaisyCoinsBox.Text },
                { "StorybookMagic", StorybookMagicBox.Text }
            };

            PlayerModule.SavePlayerData(jsonData, playerData);
        }

        private bool ValidateBeforeSave(out string error)
        {
            error = "";
            foreach (var (box, label) in new[]
                     {
                         (LevelBox, "Level"), (XpBox, "XP"), (DreamlightBox, "Dreamlight"),
                         (StarCoinsBox, "Star Coins"), (PixelDustBox, "Pixel Dust"),
                         (MistBox, "Mist"), (DaisyCoinsBox, "Daisy Coins"),
                         (StorybookMagicBox, "Storybook Magic")
                     })
            {
                if (!int.TryParse(box.Text, out int value) || value < 0)
                {
                    error = $"{label} must be a non-negative whole number.";
                    box.Focus();
                    box.SelectAll();
                    return false;
                }
            }

            var inventory = InventoryTable;
            if (inventory != null)
            {
                for (int i = 0; i < inventory.Rows.Count; i++)
                {
                    var row = inventory.Rows[i];
                    if (row.RowState == DataRowState.Deleted) continue;
                    if (!long.TryParse(row["Item ID"]?.ToString(), out long id) || id < 0)
                    {
                        error = $"Backpack, slot {i + 1}: item ID must be a non-negative whole number.";
                        return false;
                    }
                    if (!TryValidateAmount(row, "Backpack", i + 1, out error)) return false;
                }
            }

            foreach (var (key, table) in storageTables)
            {
                for (int i = 0; i < table.Rows.Count; i++)
                {
                    var row = table.Rows[i];
                    if (row.RowState == DataRowState.Deleted) continue;
                    if (!TryValidateAmount(row, $"Collection {key}", i + 1, out error)) return false;
                }
            }

            return true;
        }

        private static bool TryValidateAmount(DataRow row, string location, int rowNumber, out string error)
        {
            error = "";
            if (int.TryParse(row["Amount"]?.ToString(), out int amount) && amount >= 0)
                return true;

            error = $"{location}, row {rowNumber}: amount must be a non-negative whole number.";
            return false;
        }

        /* =================================================================
         *  Player backpack inventory (ContainerInventories["0"] only)
         * ================================================================= */

        private DataTable? InventoryTable => (PlayerInventoryDataGrid.ItemsSource as DataView)?.Table;

        private void RefreshInventoryDataGrid()
        {
            if (jsonData == null) return;
            var table = InventoryModule.MapInventory(jsonData);
            if (table == null)
            {
                PlayerInventoryDataGrid.ItemsSource = null;
                return;
            }
            PlayerInventoryDataGrid.ItemsSource = table.DefaultView;
        }

        private void SlotUpgradeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressSlotEvent || jsonData == null || SlotUpgradeComboBox.SelectedItem is not ComboBoxItem selected)
                return;

            if (!int.TryParse(selected.Tag?.ToString(), out int slots) || slots == InventoryModule.GetSlotSize(jsonData))
                return;

            var table = InventoryTable;
            if (table != null && HasOccupiedSlotsBeyond(table, slots))
            {
                MessageBox.Show("Move or remove items from the last backpack slots first. The smaller size would cut off an occupied slot.",
                    "Backpack Slots in Use", MessageBoxButton.OK, MessageBoxImage.Warning);
                UpdateInventorySlotsComboBox();
                return;
            }
            if (table != null) InventoryModule.SaveInventory(jsonData, table, slots);
            RefreshInventoryDataGrid();
        }

        private static bool HasOccupiedSlotsBeyond(DataTable table, int slotSize)
        {
            for (int i = Math.Max(0, slotSize); i < table.Rows.Count; i++)
            {
                var row = table.Rows[i];
                if (row.RowState == DataRowState.Deleted) continue;
                string id = row["Item ID"]?.ToString() ?? "0";
                int amount = int.TryParse(row["Amount"]?.ToString(), out int n) ? n : 0;
                if (long.TryParse(id, out long itemId) && itemId > 0 && amount > 0) return true;
            }
            return false;
        }

        private void UpdateInventorySlotsComboBox()
        {
            if (jsonData == null) return;
            int slotSize = InventoryModule.GetSlotSize(jsonData);
            suppressSlotEvent = true;
            try
            {
                foreach (var old in SlotUpgradeComboBox.Items.OfType<ComboBoxItem>()
                             .Where(i => i.Content?.ToString()?.StartsWith("Custom", StringComparison.Ordinal) == true).ToList())
                    SlotUpgradeComboBox.Items.Remove(old);

                var selected = SlotUpgradeComboBox.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => int.TryParse(i.Tag?.ToString(), out int n) && n == slotSize);
                if (selected == null)
                {
                    selected = new ComboBoxItem { Content = $"Custom ({slotSize} Slots)", Tag = slotSize };
                    SlotUpgradeComboBox.Items.Add(selected);
                }
                SlotUpgradeComboBox.SelectedItem = selected;
            }
            finally { suppressSlotEvent = false; }
        }

        private void CustomSlotSizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (jsonData == null) return;
            int currentSize = InventoryModule.GetSlotSize(jsonData);
            int? requestedSize = PromptForCustomSlotSize(currentSize);
            if (requestedSize is not int slots) return;

            var table = InventoryTable;
            if (table != null && HasOccupiedSlotsBeyond(table, slots))
            {
                MessageBox.Show("Move or remove items from the last backpack slots first. The selected size would cut off occupied slots.",
                    "Backpack Slots in Use", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (table != null) InventoryModule.SaveInventory(jsonData, table, slots);
            RefreshInventoryDataGrid();
            UpdateInventorySlotsComboBox();
        }

        private int? PromptForCustomSlotSize(int currentSize)
        {
            var dialog = new Window
            {
                Title = "Custom Backpack Size",
                Owner = this,
                Width = 360,
                Height = 170,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Background = (System.Windows.Media.Brush)FindResource("Panel"),
                Foreground = (System.Windows.Media.Brush)FindResource("Starlight"),
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                FontSize = 13
            };

            var layout = new Grid { Margin = new Thickness(18) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var prompt = new TextBlock
            {
                Text = "Enter the number of backpack slots:",
                Margin = new Thickness(0, 0, 0, 8),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(prompt, 0);
            layout.Children.Add(prompt);

            var sizeBox = new TextBox
            {
                Text = currentSize.ToString(),
                MinHeight = 32,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            sizeBox.PreviewTextInput += (_, args) => args.Handled = args.Text.Any(ch => !char.IsDigit(ch));
            DataObject.AddPastingHandler(sizeBox, (_, args) =>
            {
                if (args.DataObject.GetDataPresent(typeof(string)) &&
                    args.DataObject.GetData(typeof(string)) is string pasted && pasted.Any(ch => !char.IsDigit(ch)))
                    args.CancelCommand();
            });
            Grid.SetRow(sizeBox, 1);
            layout.Children.Add(sizeBox);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            var cancel = new Button { Content = "Cancel", MinWidth = 82, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var apply = new Button { Content = "Apply", MinWidth = 82, Style = (Style)FindResource("AccentButton"), IsDefault = true };
            cancel.Click += (_, _) => dialog.DialogResult = false;
            apply.Click += (_, _) =>
            {
                if (!int.TryParse(sizeBox.Text, out int value) || value <= 0)
                {
                    MessageBox.Show(dialog, "Enter a whole number greater than zero.", "Invalid Slot Size",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    sizeBox.Focus();
                    sizeBox.SelectAll();
                    return;
                }
                dialog.DialogResult = true;
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(apply);
            Grid.SetRow(buttons, 3);
            layout.Children.Add(buttons);
            dialog.Content = layout;
            dialog.Loaded += (_, _) =>
            {
                sizeBox.Focus();
                sizeBox.SelectAll();
            };

            return dialog.ShowDialog() == true && int.TryParse(sizeBox.Text, out int slots)
                ? slots
                : null;
        }

        private bool AddToBackpack(string id, int amount, Placement placement)
        {
            var table = InventoryTable;
            if (table == null) return false;
            var empty = table.AsEnumerable().Where(r => r.Field<string>("Item ID") == "0").ToList();
            if (placement.Stackable)
            {
                var existing = table.AsEnumerable().FirstOrDefault(r => r.Field<string>("Item ID") == id);
                if (existing != null) existing["Amount"] = existing.Field<int>("Amount") + amount;
                else if (empty.Count > 0) InventoryModule.PutInSlot(empty[0], id, amount, placement.DefaultState);
                else { MessageBox.Show("There are no empty backpack slots.", "Backpack Full"); return false; }
            }
            else
            {
                if (empty.Count < amount)
                {
                    MessageBox.Show($"The game stores this item one per slot. You need {amount} empty slots, but only {empty.Count} are available.", "Not Enough Slots");
                    return false;
                }
                foreach (var row in empty.Take(amount)) InventoryModule.PutInSlot(row, id, 1, placement.DefaultState);
            }
            InventoryModule.UpdateJsonFromTable(jsonData, table);
            RefreshInventoryDataGrid();
            return true;
        }

        /* =================================================================
         *  Pets
         * ================================================================= */

        private void PopulatePets()
        {
            if (jsonData == null)
                return;

            petTable = PetModule.MapPets(jsonData);
            petsDirty = false;
            PetDataGrid.ItemsSource = petTable.DefaultView;
            UpdateCurrentPetText();
        }

        /* =================================================================
         *  Mounts (experimental minimal additions to Player.Mounts)
         * ================================================================= */

        private void PopulateMounts()
        {
            if (jsonData == null)
            {
                MountsDataGrid.ItemsSource = null;
                MountsStatusText.Text = "Load a save to view unlocked mounts. Experimental additions are available through Find & Add Items.";
                return;
            }

            var table = MountModule.MapMounts(jsonData);
            MountsDataGrid.ItemsSource = table.DefaultView;
            MountsStatusText.Text = $"Unlocked mounts: {table.Rows.Count}. You can add a catalog mount with Find & Add Items. New entries are experimental and include only MountItemID; bond and customization are not initialized.";
        }

        private void PopulateTools()
        {
            if (jsonData == null)
            {
                ToolsDataGrid.ItemsSource = null;
                ToolsStatusText.Text = "Load a save to see your tools and accessories.";
                return;
            }

            var table = ToolModule.MapTools(jsonData);
            ToolsDataGrid.ItemsSource = table.DefaultView;
            int accessories = table.AsEnumerable().Count(r => r.Field<string>("Kind") == "Accessory");
            ToolsStatusText.Text = $"Tools and accessories: {table.Rows.Count} ({accessories} accessories). " +
                                   "Find an umbrella or parasol in the catalog and click Add item; it is added unequipped.";
        }

        private void PopulatePresets()
        {
            if (jsonData == null)
            {
                PresetsDataGrid.ItemsSource = null;
                PresetsStatusText.Text = "Load a save to see your decoration presets.";
                return;
            }

            var table = PresetModule.MapPresets(jsonData);
            PresetsDataGrid.ItemsSource = table.DefaultView;
            PresetsStatusText.Text = ItemPlacement.PresetList(jsonData) == null
                ? "This save has no preset list, so presets cannot be added."
                : $"Unlocked presets: {table.Rows.Count}. Find a preset in the catalog and click Add item. " +
                  "Most are shop items; unreleased ones are hidden unless you untick \"Hide risky or unfinished items\".";
        }

        private void UpdateCurrentPetText()
        {
            long curId = PetModule.GetCurrentPet(jsonData);
            string curName = ItemDatabase.Get(ItemDatabase.Find(curId), "LOCALIZED_DISPLAY_NAME");

            CurrentPetText.Text = curId == 0
                ? "Current companion: none"
                : $"Current companion: {curName} (ID {curId})";
            SetCurrentPetButton.IsEnabled = PetModule.CanSetCurrentPet(jsonData) &&
                PetDataGrid.SelectedItem is DataRowView selected &&
                PetModule.IsSupportedPet(jsonData, Convert.ToInt64(selected["Pet ID"]));
        }

        private bool AddPet(long id)
        {
            if (petTable == null) return false;

            if (petTable.AsEnumerable().Any(r => r.RowState != DataRowState.Deleted && r.Field<long>("Pet ID") == id))
            {
                MessageBox.Show("This companion is already unlocked.", "Already Unlocked");
                return false;
            }

            PetModule.AddPetRow(petTable, id);
            petsDirty = true;
            return true;
        }

        private void SetCurrentPet_Click(object sender, RoutedEventArgs e)
        {
            if (PetDataGrid.SelectedItem is not DataRowView rv ||
                !PetModule.IsSupportedPet(jsonData, Convert.ToInt64(rv["Pet ID"])))
            {
                MessageBox.Show("Select a recognized pet or critter from the game catalog.", "Choose a Companion");
                return;
            }

            PetModule.SetCurrentPet(jsonData, (long)rv["Pet ID"]);
            UpdateCurrentPetText();
        }

        private void PetDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SetCurrentPetButton.IsEnabled = PetModule.CanSetCurrentPet(jsonData) &&
                PetDataGrid.SelectedItem is DataRowView row &&
                PetModule.IsSupportedPet(jsonData, Convert.ToInt64(row["Pet ID"]));
        }

        /* =================================================================
         *  Storage (ListInventories)
         * ================================================================= */

        private void PopulateStorage()
        {
            storageTables.Clear();
            storageGrids.Clear();
            storageInfos.Clear();
            foreach (var oldTab in InventoryTabs.Items.OfType<TabItem>()
                         .Where(tab => tab.Tag is string tag && tag.StartsWith("InventoryCategory:", StringComparison.Ordinal)).ToList())
                InventoryTabs.Items.Remove(oldTab);

            var inventories = jsonData == null ? new List<ListInventoryInfo>() : ListInventoryModule.GetInventories(jsonData);

            // szafa ma własną zakładkę Clothes; ta sama tabela trafia do storageTables,
            // więc zapisuje się razem z resztą przy "Save Changes"
            var clothes = inventories.FirstOrDefault(i => i.ItemType == ClothingItemType);
            clothesKey = clothes?.Key;
            if (clothes != null && jsonData != null)
            {
                var tbl = ListInventoryModule.Map(jsonData, clothes.Key);
                storageTables[clothes.Key] = tbl;
                ClothesDataGrid.ItemsSource = tbl.DefaultView;
            }
            else
            {
                ClothesDataGrid.ItemsSource = null;
            }

            int insertIndex = InventoryTabs.Items.OfType<TabItem>().ToList()
                .FindIndex(tab => Equals(tab.Tag, "Clothes"));
            insertIndex = insertIndex < 0 ? InventoryTabs.Items.Count : insertIndex + 1;
            var categoryTypes = InventoryCategoryTabs.Concat(inventories
                    .Where(i => i != clothes && i.ItemType != "ActivityItem")
                    .Select(i => i.ItemType))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            foreach (var itemType in categoryTypes)
            {
                var info = inventories.FirstOrDefault(i => i.ItemType == itemType);
                var table = info != null && jsonData != null
                    ? ListInventoryModule.Map(jsonData, info.Key)
                    : ListInventoryModule.Map(new JObject(), string.Empty);
                if (info != null)
                {
                    storageTables[info.Key] = table;
                    storageInfos[itemType] = info;
                }
                var grid = CreateStorageGrid(table);
                grid.IsReadOnly = info == null || !ListInventoryModule.IsEditableType(itemType);
                storageGrids[itemType] = grid;

                var header = new StackPanel { Orientation = Orientation.Horizontal };
                header.Children.Add(new Border
                {
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(185, 199, 222)),
                    CornerRadius = new CornerRadius(4), Padding = new Thickness(2),
                    Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center,
                    Child = new Image
                    {
                        Source = new BitmapImage(new Uri($"pack://application:,,,/DLSatchel;component/Assets/Icons/{InventoryIcon(itemType)}", UriKind.Absolute)),
                        Width = 13, Height = 13
                    }
                });
                string title = DisplayInventoryType(itemType);
                header.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
                string tabHint = info == null
                    ? jsonData == null
                        ? "Load a save to check this category."
                        : "This category is not present in the loaded save."
                    : !ListInventoryModule.IsEditableType(itemType)
                        ? "Read-only category — this save format is not supported yet."
                        : $"Player.ListInventories[{info.Key}] — {itemType}";
                InventoryTabs.Items.Insert(insertIndex++, new TabItem
                {
                    Tag = $"InventoryCategory:{itemType}", Header = header,
                    Content = grid,
                    ToolTip = tabHint
                });
            }

            // Read-only state is shown in the selected category's help text and tab tooltip.
            // Keep the add area focused on routing and per-action feedback instead of a
            // global warning that can become stale after switching tabs or saves.
            AddStatusText.Text = "Select an item from the catalog to see where it will go. Items are routed using the game's save rules.";
        }

        private static DataGrid CreateStorageGrid(DataTable table)
        {
            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                Margin = new Thickness(5),
                ItemsSource = table.DefaultView
            };
            ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(grid, ScrollBarVisibility.Auto);
            grid.Columns.Add(new DataGridTextColumn { Header = "Item ID", Binding = new System.Windows.Data.Binding("[Item ID]"), IsReadOnly = true, Width = DataGridLength.Auto });
            grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new System.Windows.Data.Binding("[Name]"), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "In-game Name", Binding = new System.Windows.Data.Binding("[Localized Name]"), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "Amount", Binding = new System.Windows.Data.Binding("[Amount]"), Width = DataGridLength.Auto });
            grid.Columns.Add(new DataGridTextColumn { Header = "Category", Binding = new System.Windows.Data.Binding("[Category]"), IsReadOnly = true, Width = DataGridLength.Auto });
            return grid;
        }

        private static string DisplayInventoryType(string type) => ListInventoryModule.DisplayName(type);

        private static string InventoryIcon(string type) => type switch
        {
            "Furniture" or "Building" => "furniture.png",
            "Clothing" or "AvatarFeature" or "Skin" => "clothing.png",
            "Makeup" => "characters.png",
            "Trimming" => "wallpaper.png",
            "TouchOfMagic" => "motifs.png",
            "BoardGame" => "boardgame.png",
            "PhotoMode" => "camera.png",
            "MountGear" => "friendship.png",
            _ => "collections.png"
        };

        private DataTable? ClothesTable =>
            clothesKey != null && storageTables.TryGetValue(clothesKey, out var t) ? t : null;

        private bool IsCurrentStorageTab => TryGetCurrentStorageItemType(out _);

        private DataGrid? CurrentStorageGrid =>
            TryGetCurrentStorageItemType(out var itemType) && storageGrids.TryGetValue(itemType, out var grid) ? grid : null;

        private bool TryGetCurrentStorageItemType(out string itemType)
        {
            itemType = string.Empty;
            var tab = CurrentTab;
            const string prefix = "InventoryCategory:";
            if (tab == null || !tab.StartsWith(prefix, StringComparison.Ordinal)) return false;
            itemType = tab[prefix.Length..];
            return !string.IsNullOrWhiteSpace(itemType);
        }

        private bool TryGetCurrentStorageInfo(out ListInventoryInfo info)
        {
            info = null!;
            return TryGetCurrentStorageItemType(out var itemType) && storageInfos.TryGetValue(itemType, out info!);
        }

        private bool AddToList(DataTable? tbl, long id, int amount)
        {
            if (tbl == null)
            {
                MessageBox.Show("This collection is not present in the loaded save.", "Collection Not Found");
                return false;
            }

            var existing = tbl.AsEnumerable()
                .FirstOrDefault(r => r.RowState != DataRowState.Deleted && r.Field<long>("Item ID") == id);
            if (existing != null)
            {
                existing["Amount"] = existing.Field<int>("Amount") + amount;
            }
            else
            {
                tbl.Rows.Add(id, "?", "?", amount, "?");
                ListInventoryModule.FillNames(tbl.Rows[^1]);
            }
            return true;
        }

        /* =================================================================
         *  Dodawanie / usuwanie
         * ================================================================= */

        /// <summary>
        /// Ostrzega przed przedmiotami, które mogą uszkodzić sejw:
        /// brak w bazie, oznaczone w liście Rubyelf jako DEBUG/DON'T USE/Bugged/NOTHING,
        /// plus ewentualne ostrzeżenie z reguł rozmieszczenia.
        /// </summary>
        private bool ConfirmAdd(string id, string? extraWarning)
        {
            var row = ItemDatabase.Find(id);
            var warnings = new List<string>();

            string? progressionWarning = ItemPlacement.GetRiskWarning(row);
            if (progressionWarning != null)
                warnings.Add(progressionWarning);

            if (row == null)
            {
                warnings.Add(ItemDatabase.Table == null
                    ? "The item catalog is not loaded, so this ID cannot be checked."
                    : "This ID was not found in any loaded item catalog.");
            }
            else if (progressionWarning == null && ItemDatabase.IsUnsafe(row))
            {
                warnings.Add($"The catalog marks this item as unsafe: " +
                             $"{ItemDatabase.Get(row, "NOTES", ItemDatabase.Get(row, "CATEGORY"))}");
            }
            else if (progressionWarning == null && ItemDatabase.Get(row, "SAFETY", "") == "CAUTION")
            {
                warnings.Add("The catalog marks this item as CAUTION. Its type, name, or save behavior may need manual verification.");
            }
            else if (progressionWarning == null && ItemDatabase.Get(row, "SAFETY", "") == "UNKNOWN")
            {
                warnings.Add("The catalog does not recognize this as a supported inventory item type.");
            }
            if (extraWarning != null)
                warnings.Add(extraWarning);

            if (warnings.Count == 0) return true;

            string label = row == null ? id : $"{ItemDatabase.Get(row, "LOCALIZED_DISPLAY_NAME")} ({id})";
            return MessageBox.Show(
                       $"{label}\n\n• {string.Join("\n• ", warnings)}\n\n" +
                       "This item has warnings and may not work or may change the save unexpectedly. Add it anyway?",
                       "Item Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        // 1) Dodawanie nowego przedmiotu – trafia tam, gdzie trzyma go gra (ItemPlacement)
        private void AddItemButton_Click(object sender, RoutedEventArgs e)
        {
            if (jsonData == null)
            {
                MessageBox.Show("Load a save first.", "No Save Loaded");
                return;
            }

            if (!long.TryParse(ItemIDTextBox.Text.Trim(), out long id) || id <= 0)
            {
                MessageBox.Show("Enter a valid item ID.", "Invalid Item ID");
                return;
            }

            int amount = 1;
            if (!string.IsNullOrWhiteSpace(ItemAmountTextBox.Text) &&
                (!int.TryParse(ItemAmountTextBox.Text, out amount) || amount <= 0))
            {
                MessageBox.Show("Amount must be greater than zero.", "Invalid Amount");
                return;
            }

            AddById(id, amount);
        }

        /// <summary>
        /// Miejsce dla ID spoza bazy: nie wiemy, jak gra je rozumie, więc – po potwierdzeniu –
        /// bierzemy aktywną zakładkę, jak w starej wersji edytora.
        /// </summary>
        private Placement? ManualPlacement()
        {
            return CurrentTab switch
            {
                "Clothes" when clothesKey != null =>
                    new Placement { Kind = PlacementKind.ListInventory, ListKey = clothesKey, Label = "Wardrobe" },
                _ when TryGetCurrentStorageInfo(out var info) && ListInventoryModule.IsEditableType(info.ItemType) =>
                    new Placement { Kind = PlacementKind.ListInventory, ListKey = info.Key, Label = $"{DisplayInventoryType(info.ItemType)} collection" },
                "Backpack" => new Placement { Kind = PlacementKind.Backpack, Label = "Backpack" },
                _ => null
            };
        }

        private DataTable? GetStorageTable(string key)
        {
            if (!storageTables.TryGetValue(key, out var tbl))
            {
                tbl = ListInventoryModule.Map(jsonData, key);
                storageTables[key] = tbl;
            }
            return tbl;
        }

        private void AddById(long id, int amount)
        {
            string idTxt = id.ToString();
            var placement = ItemPlacement.Resolve(jsonData, idTxt);
            if (placement.Kind == PlacementKind.Mount && CurrentTab != "Mounts")
            {
                MessageBox.Show("Mounts can only be added from the Mounts tab.",
                    "Choose Mounts", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (placement.Kind == PlacementKind.Unsupported || placement.Problem != null)
            {
                DataRow? row = ItemDatabase.Find(idTxt);
                string? progressionWarning = ItemPlacement.GetRiskWarning(row);
                bool allowManualRiskOverride = progressionWarning != null;
                var manual = row == null || allowManualRiskOverride ? ManualPlacement() : null;
                if (manual == null)
                {
                    MessageBox.Show($"Cannot add {idTxt}:\n{placement.Problem ?? "No supported destination is available in the current section."}",
                                    "Unsupported Item", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string routeWarning = allowManualRiskOverride
                    ? $"{progressionWarning} The automatic save route is unavailable ({placement.Problem ?? "destination mismatch"}). You selected {manual.Label} manually; the game may reject this or damage the save."
                    : $"This will be added to the current section ({manual.Label}) without confirmation that the game accepts it there.";
                placement = new Placement
                {
                    Kind = manual.Kind, ListKey = manual.ListKey, Label = manual.Label,
                    Warning = routeWarning
                };
            }

            if (!ConfirmAdd(idTxt, placement.Warning))
                return;

            bool added;
            string extra = "";
            switch (placement.Kind)
            {
                case PlacementKind.Backpack:
                    added = AddToBackpack(idTxt, amount, placement);
                    break;

                case PlacementKind.ListInventory:
                    added = AddToList(GetStorageTable(placement.ListKey!), id, amount);
                    break;

                case PlacementKind.Pet:
                    added = AddPet(id);
                    break;

                case PlacementKind.Mount:
                    added = MountModule.AddMount(jsonData, id);
                    if (!added) extra = " — already present or mount data is unavailable";
                    break;

                case PlacementKind.Currency:
                    SavePlayerFields();   // pola w UI wygrywają przy zapisie – najpierw je wpisz do JSON
                    var now = ItemPlacement.AddCurrency(jsonData, id, amount);
                    added = now != null;
                    if (added) { PopulatePlayerFields(); extra = $" (stan: {now})"; }
                    break;

                case PlacementKind.Tool:
                    added = ItemPlacement.AddTool(jsonData, id);
                    if (!added) extra = " — already in the save";
                    break;

                case PlacementKind.CookingRecipe:
                case PlacementKind.CraftingRecipe:
                    added = ItemPlacement.AddRecipe(jsonData, id, placement.Kind == PlacementKind.CookingRecipe);
                    if (!added) extra = " — already in the save";
                    break;

                case PlacementKind.DecorationPreset:
                    added = ItemPlacement.AddPreset(jsonData, id);
                    if (!added) extra = " — already unlocked";
                    break;

                default:
                    added = false;
                    break;
            }

            string name = ItemDatabase.Get(ItemDatabase.Find(idTxt), "LOCALIZED_DISPLAY_NAME", idTxt);
            if (!added)
            {
                AddStatusText.Text = $"Not added: {name} → {placement.Label}{extra}";
                return;
            }

            // gra odnotowuje zdobycie flagą w kolekcjach (zostaje też po sprzedaży)
            int flags = ItemPlacement.MarkCollected(jsonData, id);
            if (placement.Kind == PlacementKind.Mount)
                PopulateMounts();
            if (placement.Kind == PlacementKind.Tool)
                PopulateTools();
            if (placement.Kind == PlacementKind.DecorationPreset)
                PopulatePresets();
            var itemRow = ItemDatabase.Find(idTxt);
            string safety = ItemDatabase.Get(itemRow, "SAFETY", "");
            string risk = placement.Warning ?? (safety == "CAUTION"
                ? "The catalog marks this item as CAUTION; its in-game result is uncertain."
                : safety == "UNSAFE" ? "Added despite the UNSAFE warning." : "");
            string riskSuffix = string.IsNullOrEmpty(risk) ? "" : $"\n⚠ {risk}";

            AddStatusText.Text = $"Added: {name} ×{amount} → {placement.Label}{extra}" +
                                 (flags > 0 ? $"; marked as collected in {flags} collection(s)" : "") +
                                 ". Select Save Changes when you are ready." + riskSuffix;
        }

        // 2) Usuwanie zaznaczonego (Backpack / Pets / Clothes / Storage)
        private void RemoveItemButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsCurrentStorageTab)
            {
                if (!TryGetCurrentStorageInfo(out var info) || !ListInventoryModule.IsEditableType(info.ItemType))
                {
                    MessageBox.Show("This category is read-only because its save format is not supported yet.",
                        "Read-Only Category", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                RemoveSelectedInventoryRows(CurrentStorageGrid);
                return;
            }

            switch (CurrentTab)
            {
                case "Mounts":
                    MessageBox.Show("Mounts are progression objects and can only be viewed in this version.", "Read-Only Section");
                    return;
                case "Tools":
                    MessageBox.Show("Tools and accessories can be added, but not removed: the game links tools to upgrades and to the one in use.", "Read-Only Section");
                    return;
                case "Presets":
                    MessageBox.Show("Presets can be added, but not removed in this version.", "Read-Only Section");
                    return;
                case "Pets":
                    var pets = PetDataGrid.SelectedItems.Cast<DataRowView>().ToList();
                    if (pets.Count == 0) { MessageBox.Show("Select a companion to remove.", "Nothing Selected"); return; }
                    var removablePets = pets.Where(rv =>
                        PetModule.IsSupportedPet(jsonData, Convert.ToInt64(rv["Pet ID"]))).ToList();
                    int protectedPets = pets.Count - removablePets.Count;
                    foreach (var rv in removablePets)
                        rv.Row.Delete();
                    if (removablePets.Count > 0)
                        petsDirty = true;   // zapis przy "Save Changes", jak w pozostałych zakładkach
                    if (protectedPets > 0)
                        MessageBox.Show($"Skipped {protectedPets} unrecognized companion record(s). They will be preserved unchanged.",
                            "Protected Records", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;

                case "Clothes":
                    RemoveSelectedInventoryRows(ClothesDataGrid);
                    return;

                case "Backpack":
                    var inventory = InventoryTable;
                    if (inventory == null || PlayerInventoryDataGrid.SelectedItems.Count == 0)
                    {
                        MessageBox.Show("Select a row to remove.", "Nothing Selected");
                        return;
                    }
                    foreach (var row in PlayerInventoryDataGrid.SelectedItems.OfType<DataRowView>().ToList())
                        InventoryModule.ClearSlot(row.Row);
                    InventoryModule.UpdateJsonFromTable(jsonData, inventory);
                    RefreshInventoryDataGrid();
                    return;

                default:
                    return;
            }
        }

        private static void RemoveSelectedInventoryRows(DataGrid? grid)
        {
            var items = grid?.SelectedItems.OfType<DataRowView>().ToList() ?? new List<DataRowView>();
            if (items.Count == 0) { MessageBox.Show("Select items to remove.", "Nothing Selected"); return; }
            var removable = items.Where(rv => int.TryParse(rv["Amount"]?.ToString(), out int amount) && amount > 0).ToList();
            int protectedRows = items.Count - removable.Count;
            foreach (var rv in removable) rv.Row.Delete();
            if (protectedRows > 0)
                MessageBox.Show($"Skipped {protectedRows} entries with Amount = 0. They may represent furniture or buildings placed in the world.",
                    "Protected Records", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // 3) Double‐click w ExcelDataGrid → dodaje +1 szt. do aktywnej zakładki
        private void ExcelDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (jsonData == null || ExcelDataGrid.SelectedItem is not DataRowView excelRow)
                return;

            if (long.TryParse(excelRow["ID"]?.ToString(), out long id))
                AddById(id, 1);
        }
    }
}
