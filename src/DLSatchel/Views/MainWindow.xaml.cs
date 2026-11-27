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
            bool needsLists = await TryAutoLoadExcelAsync();

            // A save passed on the command line ("Open with", or dropped on the .exe).
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && File.Exists(args[1]))
                LoadSave(args[1], quiet: true);

            if (needsLists)
                await OfferFirstDownloadAsync();
            else
                await CheckListsAtStartAsync();
        }

        /// <summary>Loads the configured lists. Returns true when there are none yet.</summary>
        private async Task<bool> TryAutoLoadExcelAsync()
        {
            try
            {
                string configPath = SourcesConfigStore.ResolvePath(null);
                if (!File.Exists(configPath))
                {
                    DbStatusText.Text = "Add your item lists to begin (Sources…).";
                    return true;
                }

                var config = SourcesConfigStore.LoadOrCreate(configPath, saveWhenCreated: false);
                if (config.Sources.Any(source => source.Enabled))
                {
                    DbStatusText.Text = "Loading your item lists…";
                    await LoadConfiguredDatabaseAsync(config, showErrors: false);
                }
                else
                {
                    DbStatusText.Text = "Add your item lists to begin (Sources…).";
                    return true;
                }
            }
            catch (Exception ex)
            {
                DbStatusText.Text = "Your item lists could not be loaded.";
                System.Diagnostics.Debug.WriteLine($"Catalog startup load failed: {ex}");
            }
            return false;
        }

        private void LoadExcel_Click(object sender, RoutedEventArgs e)
            => PickCatalogFiles("Choose Item Lists (replaces the current ones)", replaceCurrent: true);

        private void AddCatalog_Click(object sender, RoutedEventArgs e)
            => PickCatalogFiles("Add Item Lists", replaceCurrent: false);

        private void PickCatalogFiles(string title, bool replaceCurrent)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "Item lists (*.xlsx;*.xlsm;*.csv;*.tsv)|*.xlsx;*.xlsm;*.csv;*.tsv|All files (*.*)|*.*",
                Title = title,
                Multiselect = true
            };

            if (openFileDialog.ShowDialog() == true)
                _ = LoadCatalogFilesAsync(openFileDialog.FileNames, replaceCurrent);
        }

        private void Sources_Click(object sender, RoutedEventArgs e)
        {
            if (ListsBusyNote()) return;
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
                if (showErrors) MessageBox.Show($"Your item lists could not be loaded: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                else DbStatusText.Text = "Your item lists could not be loaded. Check them in Sources…";
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
            DbStatusText.Text = $"Item lists: {table.Rows.Count} items from " +
                                string.Join(", ", ItemDatabase.LoadedFiles.Select(Path.GetFileName));
            UpdateListsNotice();
            if (jsonData != null)
            {
                // Refresh names only: rebuilding these tables from the save would drop edits not saved yet.
                if (InventoryTable is DataTable backpack)
                    foreach (DataRow row in backpack.Rows)
                        if (row.RowState != DataRowState.Deleted) InventoryModule.FillNames(row);
                if (petTable != null)
                    foreach (DataRow row in petTable.Rows)
                    {
                        if (row.RowState == DataRowState.Deleted) continue;
                        var item = ItemDatabase.Find(Convert.ToInt64(row["Pet ID"]));
                        string internalName = ItemDatabase.Get(item, "NAME", "Unknown companion");
                        row["Name"] = internalName;
                        row["Localized Name"] = ItemDatabase.Get(item, "LOCALIZED_DISPLAY_NAME", internalName);
                    }
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
                DbStatusText.Text = "Loading your item lists…";
                var table = await ItemDatabase.LoadCatalogFilesAsync(paths, replaceCurrent);
                ApplyCatalogTable(table);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"The item lists could not be loaded: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
            if (!ConfirmDiscardEdits("Open another save")) return;
            var saves = SaveAutoDetect.FindAllSaves();

            if (saves.Count == 0)
            {
                MessageBox.Show("No saves found. Use Open Save… to pick the file yourself.",
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

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!ConfirmDiscardEdits("Close the editor")) e.Cancel = true;
            base.OnClosing(e);
        }

        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmDiscardEdits("Open another save")) return;
            var dlg = new OpenFileDialog
            {
                Filter = "Game save (profile.json)|profile.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
                Title = "Open Game Save"
            };
            if (dlg.ShowDialog() == true)
                LoadSave(dlg.FileName, quiet: false);
        }

        /// <summary>Edits that Save Changes would write: tables not yet in the save, or the save itself.</summary>
        internal bool HasUnsavedEdits() =>
            jsonData != null && (petsDirty || storageTables.Values.Any(t => t.GetChanges() != null) ||
                                 InventoryTable?.GetChanges() != null ||
                                 SaveDiffModule.Compare(loadedSaveSnapshot, jsonData, 1).HasChanges);

        /// <summary>Asks before unsaved edits are thrown away. True when it is fine to go on.</summary>
        internal bool ConfirmDiscardEdits(string what) =>
            !HasUnsavedEdits() ||
            MessageBox.Show($"You have edits that are not saved yet. {what} anyway? They will be lost.",
                            "Unsaved Edits", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

        private bool LoadSave(string profilePath, bool quiet)
        {
            // spróbuj wczytać (plain‑JSON albo zaszyfrowany); tworzy .bak przy pierwszym odczycie
            JObject json;
            string decoded;
            bool loaded;
            try { loaded = SaveDecoder.TryLoad(profilePath, out decoded, out json); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show($"The save could not be opened: {ex.Message}", "Open Save",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (!loaded)
            {
                MessageBox.Show("This file is not a save the editor can read.",
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
            RestoreBackupButton.IsEnabled = true;

            // A line, not a window: nothing to decide here.
            AddStatusText.Text = $"Save loaded: {jsonData["Player"]?["Name"]}, level {jsonData["Player"]?["Level"]}. " +
                                 "Nothing changes until you click Save Changes.";
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
                    // Only the companions that changed: adding one already marks it collected (MarkCollected).
                    // Recounting every collection set flags the game itself had left unset (seen on a real
                    // save: removing one critter flipped 25 companion flags), so it is not done any more.
                    PetModule.SavePets(jsonData, petTable, updateCollections: false);

                foreach (var (key, tbl) in storageTables)
                    ListInventoryModule.Save(jsonData, key, tbl);

                var changes = SaveDiffModule.Compare(loadedSaveSnapshot, jsonData, maxPaths: int.MaxValue);
                if (!changes.HasChanges)
                {
                    MessageBox.Show("There are no changes to save.", "No Changes", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Every changed area is named, however many entries changed in it.
                string review = string.Join("\n", SaveDiffModule.ReadableSummary(jsonData, changes.Paths)
                                                                 .Select(section => "• " + section));

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
                AddStatusText.Text = backupPath == null
                    ? $"Saved at {DateTime.Now:HH:mm}. This is a new file, so there was nothing to back up."
                    : $"Saved at {DateTime.Now:HH:mm}. The save from before is kept: Restore Backup… goes back to it.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not save changes: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ValidateSave_Click(object sender, RoutedEventArgs e)
        {
            if (ListsBusyNote()) return;
            try { await ValidateSaveAsync(offerUpdate: true); }
            catch (Exception ex) { ShowListError(ex); }
        }

        private async Task ValidateSaveAsync(bool offerUpdate)
        {
            if (jsonData == null)
            {
                MessageBox.Show("Load a save first.", "Check Save", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (ItemDatabase.Table == null)
            {
                MessageBox.Show("Load your item lists first.",
                                "No Item Lists", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var unknown = SaveItemIds.NotInCatalog(jsonData);
            if (unknown.Count > 0 && offerUpdate &&
                CurrentSources().Sources.Any(s => s.Enabled && ListUpdates.LinkOf(s) != null) &&
                MessageBox.Show($"Your item lists do not know {unknown.Count} item(s) in this save. " +
                                "Usually that means the lists are older than your game.\n\nUpdate the item lists now?",
                                "Check Save", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                await UpdateListsAsync();
                await ValidateSaveAsync(offerUpdate: false);
                return;
            }

            // Problems first, then warnings, then notes - the one that matters must not hide under the rest.
            var issues = SaveValidationModule.Analyze(jsonData).OrderByDescending(i => i.Level).ToList();
            if (issues.Count == 0 && unknown.Count == 0)
            {
                MessageBox.Show("No problems found. Every item in your save is in your item lists. " +
                                "This check can't catch everything.",
                                "Check Save", MessageBoxButton.OK, MessageBoxImage.Information);
                OfferToRemoveRiskyAdditions();
                return;
            }

            var parts = new List<string>();
            if (unknown.Count > 0)
                parts.Add(UnknownItemsText(unknown));

            if (issues.Count > 0)
            {
                const int visibleLimit = 24;
                string details = string.Join("\n\n", issues.Take(visibleLimit).Select(IssueText));
                if (issues.Count > visibleLimit)
                    details += $"\n\n… and {issues.Count - visibleLimit} more.";
                parts.Add($"{issues.Count} note(s):\n\n{details}");
            }
            else
            {
                parts.Add("No problems found.");
            }

            MessageBox.Show($"Read-only check. Nothing was changed.\n\n{string.Join("\n\n────────\n\n", parts)}",
                            "Check Save", MessageBoxButton.OK,
                            issues.Any(i => i.Level == SaveIssueLevel.Error) ? MessageBoxImage.Warning : MessageBoxImage.Information);
            OfferToRemoveRiskyAdditions();
        }

        /// <summary>One Check Save note in the editor's words: "Warning – Furniture: …", no save paths.</summary>
        private string IssueText(SaveValidationIssue issue)
        {
            string level = issue.Level switch
            {
                SaveIssueLevel.Error => "Problem",
                SaveIssueLevel.Caution => "Warning",
                _ => "Note"
            };
            string path = System.Text.RegularExpressions.Regex.Replace(issue.Path, @"\[\d+\]", "");
            string section = SaveDiffModule.Describe(jsonData, path);
            return section.Contains('›') || section == "Player"
                ? $"{level}: {issue.Message}"
                : $"{level} – {section}: {issue.Message}";
        }

        /// <summary>Items a newer game update added that the item lists do not have yet.</summary>
        private static string UnknownItemsText(List<(long Id, string Section)> unknown)
        {
            const int visibleLimit = 12;
            var lines = unknown.GroupBy(u => u.Section)
                .SelectMany(g => g.Select(u => $"• {u.Id}  ({g.Key})"))
                .Take(visibleLimit)
                .ToList();
            if (unknown.Count > visibleLimit)
                lines.Add($"… and {unknown.Count - visibleLimit} more.");
            string sections = string.Join(", ", unknown.GroupBy(u => u.Section)
                .Select(g => $"{g.Key} {g.Count()}"));

            return $"Your item lists do not know {unknown.Count} item(s) in this save ({sections}). " +
                   "Usually the lists are older than your game. Open Sources… and click Update from links. " +
                   "Items still unknown after that can't be checked or added until the list makers add them.\n\n" +
                   string.Join("\n", lines);
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
                MessageBox.Show("One of the files is not a save the editor can read.",
                                "Read Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var diff = SaveDiffModule.Compare(first, second, maxPaths: int.MaxValue);
            if (!diff.HasChanges)
            {
                MessageBox.Show("These two saves are the same.", "Compare Saves",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            const int visibleAreas = 30;
            var areas = SaveDiffModule.ReadableSummary(second, diff.Paths);
            string paths = string.Join("\n", areas.Take(visibleAreas).Select(section => "• " + section));
            if (areas.Count > visibleAreas) paths += $"\n… and {areas.Count - visibleAreas} more area(s)";
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
            var owned = new HashSet<long>(SaveItemIds.Collect(root).Keys);
            owned.UnionWith(SaveItemIds.PlacedInWorld(root));
            return owned;
        }

        /// <summary>Text for a DataView LIKE pattern: quotes doubled, wildcards and brackets taken literally.</summary>
        internal static string LikeText(string text)
        {
            var sb = new System.Text.StringBuilder(text.Length);
            foreach (char c in text)
                sb.Append(c switch { '\'' => "''", '[' => "[[]", ']' => "[]]", '*' => "[*]", '%' => "[%]", _ => c.ToString() });
            return sb.ToString();
        }

        private void ApplyFilters()
        {
            // Guard‑clauses
            if (ExcelDataGrid?.ItemsSource is not DataView view)
                return;

            var clauses = new List<string>();

            /* 1 ▪ name  --------------------------------------------------- */
            string name = LikeText(SearchNameBox.Text.Trim());
            if (!string.IsNullOrEmpty(name))
                clauses.Add(
                    $"([NAME] LIKE '%{name}%' OR [LOCALIZED_DISPLAY_NAME] LIKE '%{name}%')");

            /* 2 ▪ ID  ------------------------------------------------------ */
            string idPart = LikeText(SearchIdBox.Text.Trim());
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

            // Mounts can be added (experimental) but not removed; tools and presets likewise.
            bool readOnlyTab = IsCurrentStorageTab &&
                (!TryGetCurrentStorageInfo(out var storageInfo) || !ListInventoryModule.IsEditableType(storageInfo.ItemType));
            AddItemButton.IsEnabled = !readOnlyTab;
            RemoveSelectedButton.IsEnabled = !readOnlyTab && CurrentTab is not ("Tools" or "Presets" or "Mounts");
            UpdateCollectionHelpText();
        }

        private void UpdateCollectionHelpText()
        {
            AddAllMissingButton.Visibility = jsonData != null && CurrentListTarget() != null
                ? Visibility.Visible : Visibility.Collapsed;
            if (jsonData == null)
            {
                CollectionHelpText.Text = "Collections in your save, organized by category.";
                return;
            }

            CollectionHelpText.Text = CurrentTab switch
            {
                "Backpack" => "Backpack: what you carry, slot by slot.",
                "Pets" => "Companions: your pets and critters.",
                "Mounts" => "A new mount gets its bond and favourite treats from the game the first time it loads (tested). Mounts can't be removed by the editor.",
                "Tools" => "Tools & Accessories: tools and held items such as umbrellas and parasols. New ones are added unequipped; removing is not supported.",
                "Presets" => "Presets: ready-made decoration sets. Adding one also offers the furniture it needs. Removing is not supported.",
                "Clothes" => "Wardrobe: owned clothing and appearance items are listed here.",
                _ when TryGetCurrentStorageItemType(out var itemType) && !storageInfos.ContainsKey(itemType) =>
                    $"{DisplayInventoryType(itemType)} is not present in this save.",
                _ when TryGetCurrentStorageItemType(out var itemType) && !ListInventoryModule.IsEditableType(itemType) =>
                    $"{DisplayInventoryType(itemType)} can only be viewed, not changed.",
                _ when TryGetCurrentStorageItemType(out var itemType) =>
                    $"{DisplayInventoryType(itemType)}: items you add go here by themselves.",
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
            int count = ExcelDataGrid.SelectedItems.Count;
            if (count > 1)
                AddStatusText.Text = $"{count} items selected. Add item adds all of them; you will see their warnings first.";
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
                Width = 380,
                SizeToContent = SizeToContent.Height,   // a fixed height cut the buttons off
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Background = (System.Windows.Media.Brush)FindResource("WindowBackground"),
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
                Text = $"Number of backpack slots (the game itself goes up to {SlotUpgradeComboBox.Items.OfType<ComboBoxItem>().Select(i => int.TryParse(i.Tag?.ToString(), out int n) ? n : 0).DefaultIfEmpty(42).Max()}):",
                TextWrapping = TextWrapping.Wrap,
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
                // The game's largest backpack (its last upgrade). Checked in the game: a bigger size is set
                // back to this on the next save; items beyond it are moved into free slots, or deleted if there are none.
                int max = SlotUpgradeComboBox.Items.OfType<ComboBoxItem>()
                    .Select(i => int.TryParse(i.Tag?.ToString(), out int n) ? n : 0).DefaultIfEmpty(42).Max();
                bool number = int.TryParse(sizeBox.Text, out int value) && value > 0;
                bool proceed = number && (value <= max || MessageBox.Show(dialog,
                    $"The game sets the backpack back to {max} on its next save. " +
                    $"Items beyond slot {max} are moved into free slots if there is room, otherwise deleted.\n\nUse {value} anyway?",
                    "Backpack Size", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);
                if (!proceed)
                {
                    if (!number)
                        MessageBox.Show(dialog, "Enter a whole number greater than zero.", "Backpack Size",
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
                MountsStatusText.Text = "Load a save to see your mounts.";
                return;
            }

            var table = MountModule.MapMounts(jsonData);
            MountsDataGrid.ItemsSource = table.DefaultView;
            MountsStatusText.Text = $"Unlocked mounts: {table.Rows.Count}. A new mount gets its bond and favourite treats from the game the first time it loads (tested). Mounts can't be removed by the editor.";
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
                                   "Find an umbrella or parasol in the item list and click Add item; it is added unequipped.";
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
                ? "This save has no preset list yet, so presets can't be added."
                : $"Unlocked presets: {table.Rows.Count}. Pick a preset in the item list and click Add item; " +
                  (PresetLayouts.All.Count > 0
                      ? "the editor also offers the furniture it needs. "
                      : "the game folder was not found, so the furniture a preset needs can't be offered. ") +
                  "Untick \"Hide risky or unfinished items\" to see unreleased ones.";
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
                MessageBox.Show("Pick a pet or critter that is in your item lists.", "Choose a Companion");
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
                        ? "View only. The editor can't change this yet."
                        : title;
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
            AddStatusText.Text = "Pick an item and click Add item. The editor puts it where the game keeps it.";
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
            var warnings = ItemWarnings(id, extraWarning);
            lastAddWarning = warnings.FirstOrDefault();
            if (warnings.Count == 0 || !AskBeforeWarnedAdds) return true;

            string label = row == null ? id : $"{ItemDatabase.Get(row, "LOCALIZED_DISPLAY_NAME")} ({id})";
            bool yes = DialogBox.Show(
                           $"{label}\n\n• {string.Join("\n• ", warnings)}\n\n" +
                           "Add it anyway? Back up your save first.",
                           "Item Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning, this,
                           "Don't ask again", out bool stopAsking) == MessageBoxResult.Yes;
            if (stopAsking) StopAskingAboutWarnings();
            return yes;
        }

        /// <summary>The first warning of the last item added, for the status line.</summary>
        private string? lastAddWarning;

        /// <summary>Everything that speaks against adding an item; empty when the lists have no warnings.</summary>
        private List<string> ItemWarnings(string id, string? extraWarning)
        {
            var row = ItemDatabase.Find(id);
            var warnings = new List<string>();

            // An UNSAFE item gets the lists' own words below; the general "quest / do not craft"
            // caution would only say the same thing again.
            string? progressionWarning = row != null && ItemDatabase.IsUnsafe(row) ? null : ItemPlacement.GetRiskWarning(row);
            if (progressionWarning != null)
                warnings.Add(progressionWarning);
            if (jsonData != null)
                warnings.AddRange(ItemPlacement.RequirementWarnings(jsonData, row));

            if (row == null)
            {
                warnings.Add(ItemDatabase.Table == null
                    ? "No item lists are loaded, so this item can't be checked."
                    : "This ID is not in your item lists.");
            }
            else if (progressionWarning == null && ItemDatabase.IsUnsafe(row))
            {
                var record = ItemDatabase.FindRecord(long.Parse(id));
                string said = record?.TestNote is { Length: > 0 } test
                    ? test
                    : ItemRecord.ListReason(ItemDatabase.Get(row, "LOCALIZED_DISPLAY_NAME", ""), ItemDatabase.Get(row, "NOTES", ""),
                                            record?.CatalogueCategory ?? "", ItemDatabase.Get(row, "CATEGORY", ""));
                warnings.Add($"The item lists mark it UNSAFE: {said}");
            }
            else if (progressionWarning == null && ItemDatabase.Get(row, "SAFETY", "") == "CAUTION")
            {
                // Only worth a question when the list talks about quests or breakage; a plain
                // CAUTION ("Event Reward") shows in the status line after adding instead.
                string notes = ItemDatabase.Get(row, "NOTES", "");
                if (System.Text.RegularExpressions.Regex.IsMatch(notes, "(?i)quest|mission|bug|break|don'?t|do not"))
                    warnings.Add($"The community list says: \"{notes.Trim()}\"");
            }
            else if (progressionWarning == null && ItemDatabase.Get(row, "SAFETY", "") == "UNKNOWN")
            {
                warnings.Add("The item lists say too little about it (UNKNOWN).");
            }
            // What a tester wrote ("Tested in the game (1.26): does not work: freezes when placed").
            if (row != null && !ItemDatabase.IsUnsafe(row) && ItemDatabase.FindRecord(long.Parse(id))?.TestNote is { Length: > 0 } note)
                warnings.Add(note);
            if (row != null && !ItemDatabase.IsCommunityChecked(row))
                warnings.Add("Not checked by the community: may be unfinished or a developer item (some broke games).");
            if (extraWarning != null)
                warnings.Add(extraWarning);
            return warnings;
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

            // Several catalog rows selected (Ctrl/Shift+click) and the ID box still shows one of them:
            // add them all in one go.
            var selected = SelectedCatalogIds();
            if (selected.Count > 1 && selected.Contains(id))
            {
                AddSeveral(selected, amount);
                return;
            }

            AddById(id, amount);
        }

        private List<long> SelectedCatalogIds() =>
            ExcelDataGrid.SelectedItems.OfType<DataRowView>()
                .Select(r => long.TryParse(r["ID"]?.ToString(), out long v) ? v : 0)
                .Where(v => v > 0)
                .ToList();

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
                MessageBox.Show("Open the Mounts tab to add a mount.",
                    "Add Mount", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (placement.Kind == PlacementKind.Unsupported || placement.Problem != null)
            {
                DataRow? row = ItemDatabase.Find(idTxt);
                string? progressionWarning = ItemPlacement.GetRiskWarning(row);
                bool allowManualRiskOverride = false;   // a known item the game won't take is not forced in anywhere
                var manual = row == null ? ManualPlacement() : null;
                if (manual == null)
                {
                    MessageBox.Show($"Can't add {idTxt}:\n{placement.Problem ?? "The editor doesn't know where this item goes."}",
                                    "Can't Add Item", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string routeWarning = allowManualRiskOverride
                    ? $"{progressionWarning} The editor can't tell where this item goes. It will go to {manual.Label}, the tab you have open. The game may not accept it, or it may damage the save."
                    : $"This item is not in your item lists. It will go to {manual.Label}, the tab you have open. The game may not accept it there.";
                placement = new Placement
                {
                    Kind = manual.Kind, ListKey = manual.ListKey, Label = manual.Label,
                    Warning = routeWarning
                };
            }

            if (!ConfirmAdd(idTxt, placement.Warning))
                return;

            var (added, extra) = ApplyAdd(id, amount, placement);

            string name = ItemDatabase.Get(ItemDatabase.Find(idTxt), "LOCALIZED_DISPLAY_NAME", idTxt);
            if (!added)
            {
                AddStatusText.Text = $"Not added: {name} → {placement.Label}{extra}";
                return;
            }

            // gra odnotowuje zdobycie flagą w kolekcjach (zostaje też po sprzedaży)
            int flags = ItemPlacement.MarkCollected(jsonData, id);
            RefreshAfterAdd(placement.Kind);
            var itemRow = ItemDatabase.Find(idTxt);
            string safety = ItemDatabase.Get(itemRow, "SAFETY", "");
            string risk = lastAddWarning ?? placement.Warning ?? (safety == "CAUTION"
                ? "Marked CAUTION in the item lists. Check it in the game."
                : safety == "UNSAFE" ? "Added despite the UNSAFE warning." : "");
            string riskSuffix = string.IsNullOrEmpty(risk) ? "" : $"\n⚠ {risk}";

            AddStatusText.Text = $"Added: {name} ×{amount} → {placement.Label}{extra}" +
                                 (flags > 0 ? $"; marked as collected in {flags} collection(s)" : "") +
                                 ". Select Save Changes when you are ready." + riskSuffix;
        }

        private void RefreshAfterAdd(PlacementKind kind)
        {
            if (kind == PlacementKind.Mount) PopulateMounts();
            if (kind == PlacementKind.Tool) PopulateTools();
            if (kind == PlacementKind.DecorationPreset) PopulatePresets();
        }

        /// <summary>Puts one item where the game keeps it. No questions; the caller confirmed.</summary>
        private (bool Added, string Extra) ApplyAdd(long id, int amount, Placement placement)
        {
            string idTxt = id.ToString();
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
                    if (!added) extra = " — already in the save, or this save has no mounts yet";
                    break;

                case PlacementKind.Currency:
                    SavePlayerFields();   // pola w UI wygrywają przy zapisie – najpierw je wpisz do JSON
                    var now = ItemPlacement.AddCurrency(jsonData, id, amount);
                    added = now != null;
                    if (added) { PopulatePlayerFields(); extra = $" (now {now})"; }
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
                    (added, extra) = AddPresetWithFurniture(id);
                    break;

                default:
                    added = false;
                    break;
            }
            return (added, extra);
        }

        /// <summary>
        /// Several rows selected in the catalog: one question for all of them. Items with warnings
        /// can be left out in the same question; mounts and presets are added one at a time.
        /// </summary>
        private void AddSeveral(IReadOnlyList<long> ids, int amount)
        {
            var plan = new List<(long Id, Placement Placement, string Name, string? Warning)>();
            var skipped = new List<string>();
            foreach (long id in ids.Distinct())
            {
                string idTxt = id.ToString();
                string name = ItemDatabase.Get(ItemDatabase.Find(idTxt), "LOCALIZED_DISPLAY_NAME", idTxt);
                var placement = ItemPlacement.Resolve(jsonData, idTxt);
                if (placement.Kind == PlacementKind.Unsupported || placement.Problem != null)
                {
                    skipped.Add($"{name}: {placement.Problem ?? "the editor doesn't know where it goes"}");
                    continue;
                }
                if (placement.Kind is PlacementKind.Mount or PlacementKind.DecorationPreset)
                {
                    skipped.Add($"{name}: add it on its own ({placement.Label} tab)");
                    continue;
                }
                var warnings = ItemWarnings(idTxt, placement.Warning);
                plan.Add((id, placement, name, warnings.Count > 0 ? warnings[0] : null));
            }

            if (plan.Count == 0)
            {
                MessageBox.Show("None of the selected items can be added:\n\n• " + string.Join("\n• ", skipped.Take(12)),
                                "Add Items", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var risky = plan.Where(p => p.Warning != null).ToList();
            var text = new System.Text.StringBuilder($"Add {plan.Count} selected items (amount {amount} where it applies)?");
            if (skipped.Count > 0)
                text.Append($"\n\nLeft out ({skipped.Count}):\n• " + string.Join("\n• ", skipped.Take(6)) +
                            (skipped.Count > 6 ? $"\n… and {skipped.Count - 6} more" : ""));
            MessageBoxResult answer;
            if (!AskBeforeWarnedAdds)
            {
                // Asking is off: add them all; the status line says how many had warnings.
            }
            else if (risky.Count > 0)
            {
                text.Append($"\n\n{risky.Count} of them have warnings:\n• " +
                            string.Join("\n• ", risky.Take(8).Select(r => $"{r.Name} - {r.Warning}")) +
                            (risky.Count > 8 ? $"\n… and {risky.Count - 8} more" : ""));
                text.Append($"\n\nYes - add all {plan.Count}\nNo - add only the {plan.Count - risky.Count} without warnings\nCancel - add nothing");
                answer = MessageBox.Show(text.ToString(), "Add Items", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Cancel) return;
                if (answer == MessageBoxResult.No) plan = plan.Where(p => p.Warning == null).ToList();
            }
            else if (MessageBox.Show(text.ToString(), "Add Items", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            int added = 0;
            var kinds = new HashSet<PlacementKind>();
            foreach (var item in plan)
            {
                if (!ApplyAdd(item.Id, amount, item.Placement).Added) continue;
                ItemPlacement.MarkCollected(jsonData, item.Id);
                kinds.Add(item.Placement.Kind);
                added++;
            }
            foreach (var kind in kinds) RefreshAfterAdd(kind);
            int selected = ids.Distinct().Count();
            int warned = plan.Count(p => p.Warning != null);
            AddStatusText.Text = $"Added {added} of {selected} selected items" +
                                 (selected > added ? $" ({selected - added} left out)" : "") +
                                 ". Select Save Changes when you are ready." +
                                 (warned > 0 && !AskBeforeWarnedAdds ? $"\n⚠ {warned} of them have warnings (for example: {plan.First(p => p.Warning != null).Warning})" : "");
        }

        // 2) Usuwanie zaznaczonego (Backpack / Pets / Clothes / Storage)
        private void RemoveItemButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsCurrentStorageTab)
            {
                if (!TryGetCurrentStorageInfo(out var info) || !ListInventoryModule.IsEditableType(info.ItemType))
                {
                    MessageBox.Show("The editor can't change this category yet.",
                        "View Only", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                RemoveSelectedInventoryRows(CurrentStorageGrid);
                return;
            }

            switch (CurrentTab)
            {
                case "Mounts":
                    MessageBox.Show("Mounts can't be removed by the editor.", "Can't Remove");
                    return;
                case "Tools":
                    MessageBox.Show("Tools and accessories can be added, but not removed: the game links tools to upgrades and to the one in use.", "Can't Remove");
                    return;
                case "Presets":
                    MessageBox.Show("Presets can be added, but not removed.", "Can't Remove");
                    return;
                case "Pets":
                    var pets = PetDataGrid.SelectedItems.Cast<DataRowView>().ToList();
                    if (pets.Count == 0) { MessageBox.Show("Select a companion to remove.", "Nothing Selected"); return; }
                    // The companion that is out with the player stays: what the game does without it is unknown.
                    long current = PetModule.GetCurrentPet(jsonData);
                    bool keptCurrent = pets.Any(rv => Convert.ToInt64(rv["Pet ID"]) == current && current != 0);
                    var removablePets = pets.Where(rv => Convert.ToInt64(rv["Pet ID"]) != current &&
                        (PetModule.IsSupportedPet(jsonData, Convert.ToInt64(rv["Pet ID"])) ||
                         ItemPlacement.IsWildCritter(Convert.ToInt64(rv["Pet ID"])))).ToList();
                    int protectedPets = pets.Count - removablePets.Count;
                    foreach (var rv in removablePets)
                        rv.Row.Delete();
                    if (removablePets.Count > 0)
                    {
                        petsDirty = true;   // zapis przy "Save Changes", jak w pozostałych zakładkach
                        ShowRemoved(removablePets.Count);
                    }
                    if (keptCurrent)
                        AddStatusText.Text += " The companion that is out with you was kept - choose another one in the game first.";
                    else if (protectedPets > 0)
                        AddStatusText.Text += $" {protectedPets} companion(s) not in your item lists were kept.";
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
                    var cleared = PlayerInventoryDataGrid.SelectedItems.OfType<DataRowView>().ToList();
                    foreach (var row in cleared)
                        InventoryModule.ClearSlot(row.Row);
                    InventoryModule.UpdateJsonFromTable(jsonData, inventory);
                    RefreshInventoryDataGrid();
                    ShowRemoved(cleared.Count);
                    return;

                default:
                    return;
            }
        }

        private void RemoveSelectedInventoryRows(DataGrid? grid)
        {
            var items = grid?.SelectedItems.OfType<DataRowView>().ToList() ?? new List<DataRowView>();
            if (items.Count == 0) { MessageBox.Show("Select items to remove.", "Nothing Selected"); return; }
            var removable = items.Where(rv => int.TryParse(rv["Amount"]?.ToString(), out int amount) && amount > 0).ToList();
            int protectedRows = items.Count - removable.Count;
            foreach (var rv in removable) rv.Row.Delete();
            if (removable.Count > 0) ShowRemoved(removable.Count);
            if (protectedRows > 0)
                MessageBox.Show($"{protectedRows} item(s) with amount 0 were kept: they are probably placed in your valley.",
                    "Kept", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ShowRemoved(int count) =>
            AddStatusText.Text = $"Removed {count} item(s). Click Save Changes to keep this, or Discard edits to undo.";

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
