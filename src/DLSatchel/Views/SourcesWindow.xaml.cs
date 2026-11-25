using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using DDV_SaveEditor_G.Data;
using DDV_SaveEditor_G.Modules;
using Microsoft.Win32;

namespace DDV_SaveEditor_G
{
    /// <summary>
    /// Interaction logic for SourcesWindow.xaml
    /// </summary>
    public partial class SourcesWindow : Window
    {
        private readonly ObservableCollection<SourceRowViewModel> _rows = new();

        public SourcesWindow()
        {
            InitializeComponent();
            SourcesGrid.ItemsSource = _rows;
            Loaded += SourcesWindow_Loaded;
            // Closing mid-download would leave the work without a window to report to.
            Closing += (_, e) => { if (_busy) e.Cancel = true; };
        }

        private async void SourcesWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            _rows.Clear();

            var catalog = ItemDatabase.DetailedCatalog;
            var config = catalog?.Sources;
            if (config == null || config.Sources.Count == 0)
                config = SourcesConfigStore.LoadOrCreate(SourcesConfigStore.ResolvePath(null), saveWhenCreated: false);
            var diag = catalog?.Diagnostics ?? ItemCatalog.Empty.Diagnostics;

            TxtSummary.Text = $"Items: {diag.TotalRecords:N0} | No internal name: {diag.TotalRecords - diag.RecordsWithInternalName:N0} | UNSAFE: {diag.UnsafeRecords:N0} | CAUTION: {diag.CautionRecords:N0}";

            foreach (var source in config.Sources)
            {
                var s = diag.Sources.FirstOrDefault(d => string.Equals(d.File, source.Path, StringComparison.OrdinalIgnoreCase));
                _rows.Add(new SourceRowViewModel
                {
                    Path = source.Path,
                    File = source.Path,
                    FileName = Path.GetFileName(source.Path),
                    Url = ListUpdates.LinkOf(source) ?? "",
                    OriginalUrl = ListUpdates.LinkOf(source) ?? "",
                    Updated = UpdatedText(source),
                    Enabled = source.Enabled,
                    Priority = source.Priority,
                    Format = s?.Format ?? Path.GetExtension(source.Path).TrimStart('.').ToUpperInvariant(),
                    TotalSheets = s?.TotalSheets ?? 0,
                    RowsRead = s?.RowsRead ?? 0,
                    RowsAccepted = s?.RowsAccepted ?? 0,
                    RowsRejected = s?.RowsRejected ?? 0,
                    EmptyRowsSkipped = s?.EmptyRowsSkipped ?? 0,
                    UniqueIds = s?.UniqueIds ?? 0,
                    Duplicates = s?.Duplicates ?? 0,
                    Conflicts = s?.Conflicts ?? 0,
                    NewRecords = s?.NewRecords ?? 0,
                    EnrichedRecords = s?.EnrichedRecords ?? 0,
                    ElapsedMs = s?.ElapsedMs ?? 0,
                    Sheets = (s?.Sheets ?? new List<SheetDiagnostics>()).Select(sh => new SheetRowViewModel
                    {
                        Sheet = sh.Sheet,
                        Role = sh.Role,
                        Language = sh.Language ?? "",
                        HeaderRow = sh.HeaderRow,
                        RowsRead = sh.RowsRead,
                        RowsAccepted = sh.RowsAccepted,
                        RowsRejected = sh.RowsRejected,
                        EmptyRowsSkipped = sh.EmptyRowsSkipped,
                        Duplicates = sh.Duplicates,
                        Conflicts = sh.Conflicts,
                        Mapping = sh.Mapping,
                        Notes = sh.Notes
                    }).ToList()
                });
            }
        }

        private async void BtnAddFile_Click(object sender, RoutedEventArgs e)
            => await PickAndLoadAsync("Add Item Lists", replaceCurrent: false);

        private async void BtnReplaceFile_Click(object sender, RoutedEventArgs e)
            => await PickAndLoadAsync("Use Only These Item Lists", replaceCurrent: true);

        private async Task PickAndLoadAsync(string title, bool replaceCurrent)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Excel/CSV files (*.xlsx;*.xlsm;*.csv;*.tsv)|*.xlsx;*.xlsm;*.csv;*.tsv|All files|*.*",
                Multiselect = true,
                Title = title
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                await ItemDatabase.LoadCatalogFilesAsync(dlg.FileNames, replaceCurrent);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"The item list could not be read: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            await RefreshAsync();
        }

        private SourcesConfig CurrentConfig()
        {
            var config = ItemDatabase.DetailedCatalog?.Sources;
            if (config == null || config.Sources.Count == 0)
                config = SourcesConfigStore.LoadOrCreate(SourcesConfigStore.ResolvePath(null), saveWhenCreated: false);
            return config;
        }

        private async void BtnRemove_Click(object sender, RoutedEventArgs e) => await GuardAsync(BtnRemoveAsync);

        private async Task BtnRemoveAsync()
        {
            if (SourcesGrid.SelectedItem is not SourceRowViewModel selected) return;

            var config = CurrentConfig();
            config.Sources.RemoveAll(s => string.Equals(s.Path, selected.Path, StringComparison.OrdinalIgnoreCase));
            await ItemDatabase.LoadFromConfigAsync(config);
            await RefreshAsync();
        }

        private async void BtnCleanup_Click(object sender, RoutedEventArgs e) => await GuardAsync(BtnCleanupAsync);

        private async Task BtnCleanupAsync()
        {
            var config = CurrentConfigWithEdits();
            var plan = SourceCleanup.Plan(config, SourceCleanup.EditorFolder, SourceDownloader.BackupDirectory);
            if (plan.IsEmpty)
            {
                MessageBox.Show("Nothing to clean up: every list is switched on and its file exists, and there are no old copies.",
                                "Clean Up", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var text = new System.Text.StringBuilder();
            if (plan.Remove.Count > 0)
            {
                text.AppendLine("Take off the list (switched off or file missing):");
                foreach (var source in plan.Remove)
                    text.AppendLine($"• {Path.GetFileName(source.Path)}  ({(File.Exists(source.Path) ? Path.GetDirectoryName(source.Path) : "file missing")})");
                text.AppendLine();
            }
            if (plan.DeleteFiles.Count > 0)
            {
                long bytes = plan.DeleteFiles.Sum(f => new FileInfo(f).Length);
                text.AppendLine($"Move to the Recycle Bin ({bytes / 1048576.0:F1} MB, old copies kept by the editor):");
                foreach (string file in plan.DeleteFiles)
                    text.AppendLine($"• {Path.GetFileName(file)}");
                text.AppendLine();
            }
            text.Append("Lists outside the editor's own folder stay on your disk. Continue?");

            if (MessageBox.Show(text.ToString(), "Clean Up", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var failed = new List<string>();
            foreach (string file in plan.DeleteFiles)
            {
                try
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    failed.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }

            config.Sources.RemoveAll(s => plan.Remove.Contains(s));
            try
            {
                SetBusy(true, "Reloading the item lists…");
                await ItemDatabase.LoadFromConfigAsync(config);
            }
            finally
            {
                SetBusy(false);
            }
            await RefreshAsync();

            if (failed.Count > 0)
                MessageBox.Show("Some files could not be moved to the Recycle Bin (close them if they are open in Excel):\n\n" +
                                string.Join("\n", failed), "Clean Up", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private async void BtnReload_Click(object sender, RoutedEventArgs e) => await GuardAsync(BtnReloadAsync);

        private async Task BtnReloadAsync()
        {
            var config = CurrentConfigWithEdits();
            await ItemDatabase.LoadFromConfigAsync(config);
            await RefreshAsync();
        }

        /// <summary>The current config with the checkboxes, priorities and links typed into the grid.</summary>
        private SourcesConfig CurrentConfigWithEdits()
        {
            SourcesGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
            SourcesGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
            var config = CurrentConfig();
            foreach (var row in _rows)
            {
                var source = config.Sources.FirstOrDefault(s => string.Equals(s.Path, row.Path, StringComparison.OrdinalIgnoreCase));
                if (source == null) continue;
                source.Enabled = row.Enabled;
                source.Priority = row.Priority;
                // Only a Link cell the player changed is written back. An emptied cell means
                // "I keep this list by hand", even for a known list.
                string edited = row.Url?.Trim() ?? "";
                if (edited != row.OriginalUrl)
                {
                    source.NoLink = edited.Length == 0;
                    source.Url = source.NoLink ? null : ListUpdates.StoredLink(edited, source.Path);
                }
            }
            return config;
        }

        private static string UpdatedText(CatalogSourceConfig source)
        {
            if (source.UpdatedUtc is DateTime updated)
                return updated.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            return File.Exists(source.Path) ? File.GetLastWriteTime(source.Path).ToString("yyyy-MM-dd") : "missing";
        }

        private static int CurrentItemCount(string path) =>
            ItemDatabase.DetailedCatalog?.Diagnostics.Sources
                .FirstOrDefault(d => string.Equals(d.File, path, StringComparison.OrdinalIgnoreCase))?.UniqueIds ?? 0;

        private bool _busy;

        /// <summary>Runs a button's work; an error is shown instead of closing the whole editor.</summary>
        private async Task GuardAsync(Func<Task> work)
        {
            if (_busy) return;
            try { await work(); }
            catch (Exception ex)
            {
                SetBusy(false);
                MessageBox.Show(this, $"Something went wrong: {ex.Message}\n\nYour save and your lists were not changed by this step.",
                                "Sources", MessageBoxButton.OK, MessageBoxImage.Warning);
                try { await RefreshAsync(); } catch (Exception) { /* keep the window usable */ }
            }
        }

        private void SetBusy(bool busy, string? status = null)
        {
            _busy = busy;
            foreach (var button in new[] { BtnAddFile, BtnReplaceFile, BtnAddLink, BtnUpdateLinks, BtnRemove, BtnCleanup, BtnReload })
                button.IsEnabled = !busy;
            Mouse.OverrideCursor = busy ? Cursors.Wait : null;
            if (status != null) TxtSummary.Text = status;
        }

        private async void BtnUpdateLinks_Click(object sender, RoutedEventArgs e) => await GuardAsync(BtnUpdateLinksAsync);

        private async Task BtnUpdateLinksAsync()
        {
            var config = CurrentConfigWithEdits();
            if (!config.Sources.Any(s => s.Enabled && ListUpdates.LinkOf(s) != null))
            {
                MessageBox.Show("None of the enabled lists has a link. Paste the sheet's link in the Link column, " +
                                "or use Add from link.", "Update from links", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            List<SourceUpdateResult> results;
            try
            {
                SetBusy(true);
                results = await ListUpdates.UpdateAllAsync(config, CurrentItemCount,
                    new Progress<string>(text => TxtSummary.Text = text));
                SetBusy(true, "Reloading the item lists…");
                await ItemDatabase.LoadFromConfigAsync(config);   // also keeps the links in sources.json
            }
            finally
            {
                SetBusy(false);
            }
            await RefreshAsync();

            if (results.All(r => r.Updated))
            {
                MessageBox.Show(ListUpdates.Describe(results), "Update from links", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (await ListProblems.ResolveAsync(this, config, results, CurrentItemCount))
            {
                try
                {
                    SetBusy(true, "Reloading the item lists…");
                    await ItemDatabase.LoadFromConfigAsync(config);
                }
                finally { SetBusy(false); }
                await RefreshAsync();
            }
        }

        private async void BtnAddLink_Click(object sender, RoutedEventArgs e) => await GuardAsync(BtnAddLinkAsync);

        private async Task BtnAddLinkAsync()
        {
            var dialog = new LinkDialog { Owner = this };
            if (dialog.ShowDialog() != true) return;
            string link = dialog.Link;

            var config = CurrentConfigWithEdits();
            var existing = config.Sources.FirstOrDefault(s => string.Equals(s.Url?.Trim(), link, StringComparison.OrdinalIgnoreCase));
            SourceUpdateResult result;
            try
            {
                SetBusy(true, "Downloading the list…");
                result = await SourceDownloader.UpdateAsync(link, existing?.Path, existing != null ? CurrentItemCount(existing.Path) : 0);
                if (result.Updated)
                {
                    existing ??= config.Sources.FirstOrDefault(s => string.Equals(s.Path, result.Path, StringComparison.OrdinalIgnoreCase));
                    if (existing == null)
                    {
                        existing = new CatalogSourceConfig
                        {
                            Path = result.Path!,
                            Priority = SourcesConfigStore.GuessPriority(result.FileName)
                        };
                        config.Sources.Add(existing);
                    }
                    existing.Path = result.Path!;   // a list from elsewhere now lives in the editor's folder
                    existing.Enabled = true;
                    existing.Url = link;
                    existing.NoLink = false;
                    existing.UpdatedUtc = DateTime.UtcNow;

                    SetBusy(true, "Reloading the item lists…");
                    await ItemDatabase.LoadFromConfigAsync(config);
                }
            }
            finally
            {
                SetBusy(false);
            }
            await RefreshAsync();

            if (result.Updated)
                MessageBox.Show($"{result.FileName}: {result.Message}\n\nThe list is now in use. " +
                                "Update from links gets a fresh copy later.",
                                "Add from link", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show($"The list was not added: {result.Message}", "Add from link",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void BtnExportJson_Click(object sender, RoutedEventArgs e)
        {
            var catalog = ItemDatabase.DetailedCatalog;
            if (catalog == null) return;

            var dlg = new SaveFileDialog
            {
                Filter = "JSON (*.json)|*.json",
                FileName = $"catalog-report-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json",
                Title = "Save JSON Report"
            };
            if (dlg.ShowDialog() == true)
            {
                CatalogReport.SaveJson(catalog.Diagnostics, dlg.FileName);
                MessageBox.Show($"Report saved:\n{dlg.FileName}", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnExportMd_Click(object sender, RoutedEventArgs e)
        {
            var catalog = ItemDatabase.DetailedCatalog;
            if (catalog == null) return;

            var dlg = new SaveFileDialog
            {
                Filter = "Markdown (*.md)|*.md",
                FileName = $"catalog-report-{DateTime.UtcNow:yyyyMMdd-HHmmss}.md",
                Title = "Save Markdown Report"
            };
            if (dlg.ShowDialog() == true)
            {
                CatalogReport.SaveMarkdown(catalog.Diagnostics, dlg.FileName);
                MessageBox.Show($"Report saved:\n{dlg.FileName}", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnOpenCache_Click(object sender, RoutedEventArgs e)
        {
            string cacheDir = CatalogCache.DefaultDirectory;
            if (Directory.Exists(cacheDir))
                System.Diagnostics.Process.Start("explorer.exe", cacheDir);
            else
                MessageBox.Show($"The cache folder does not exist:\n{cacheDir}", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
            => Close();

        private void Link_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            ListProblems.OpenInBrowser(e.Uri.AbsoluteUri);
            e.Handled = true;
        }
    }

    public class SourceRowViewModel : INotifyPropertyChanged
    {
        public string Path { get; set; } = "";
        public string File { get; set; } = "";
        public string FileName { get; set; } = "";
        public string? Url { get; set; }
        public string OriginalUrl { get; set; } = "";
        public string Updated { get; set; } = "";
        public bool Enabled { get; set; }
        public int Priority { get; set; }
        public string Format { get; set; } = "";
        public int TotalSheets { get; set; }
        public int RowsRead { get; set; }
        public int RowsAccepted { get; set; }
        public int RowsRejected { get; set; }
        public int EmptyRowsSkipped { get; set; }
        public int UniqueIds { get; set; }
        public int Duplicates { get; set; }
        public int Conflicts { get; set; }
        public int NewRecords { get; set; }
        public int EnrichedRecords { get; set; }
        public double ElapsedMs { get; set; }
        public List<SheetRowViewModel> Sheets { get; set; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class SheetRowViewModel
    {
        public string Sheet { get; set; } = "";
        public string Role { get; set; } = "";
        public string Language { get; set; } = "";
        public int HeaderRow { get; set; }
        public int RowsRead { get; set; }
        public int RowsAccepted { get; set; }
        public int RowsRejected { get; set; }
        public int EmptyRowsSkipped { get; set; }
        public int Duplicates { get; set; }
        public int Conflicts { get; set; }
        public Dictionary<string, string> Mapping { get; set; } = new();
        public List<string> Notes { get; set; } = new();

        public string MappingSummary => Mapping.Count > 0
            ? string.Join("; ", Mapping.Select(kv => $"{kv.Key}→{kv.Value}"))
            : "";

        public string NotesSummary => Notes.Count > 0 ? string.Join("; ", Notes) : "";
    }
}
