using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using DDV_SaveEditor_G.Data;
using Microsoft.Win32;

namespace DDV_SaveEditor_G
{
    /// <summary>
    /// What happens after a list fails to download: no internet gets one short note; a link that
    /// stopped working gets <see cref="ListProblemDialog"/>, where the player pastes a new link or
    /// picks a file. Used by the main window and the Sources window.
    /// </summary>
    internal static class ListProblems
    {
        public const string ForumLink = "https://fearlessrevolution.com/viewtopic.php?f=2&t=21330";

        public static void OpenInBrowser(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                MessageBox.Show($"The browser could not be opened. The address is:\n{url}", "Open Link",
                                MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>Lets the player fix every failed list. Returns whether the config changed.</summary>
        public static async Task<bool> ResolveAsync(Window owner, SourcesConfig config,
                                                    IReadOnlyCollection<SourceUpdateResult> results, Func<string, int> currentItems)
        {
            var failed = results.Where(r => !r.Updated).ToList();
            if (failed.Count == 0) return false;

            // A window that closed in the meantime cannot own a dialog; fall back to the main window.
            if (!owner.IsLoaded) owner = Application.Current.MainWindow;

            bool changed = false;
            var locked = failed.Where(r => r.Problem == ListProblem.FileLocked).ToList();
            if (locked.Count > 0)
                MessageBox.Show(owner,
                    "These lists could not be replaced because the file is in use (probably open in Excel):\n\n" +
                    string.Join("\n", locked.Select(r => "• " + r.FileName)) +
                    "\n\nClose the file and click Update again. Your current lists are still used.",
                    "Item Lists", MessageBoxButton.OK, MessageBoxImage.Warning);

            foreach (var result in failed.Where(r => r.Problem is not (ListProblem.Offline or ListProblem.FileLocked)))
            {
                var entry = config.Sources.FirstOrDefault(s =>
                    s.Enabled && result.Link != null &&
                    string.Equals(ListUpdates.LinkOf(s), result.Link, StringComparison.OrdinalIgnoreCase));
                changed |= await ResolveOneAsync(owner, config, entry, result, currentItems);
            }

            if (failed.Any(r => r.Problem == ListProblem.Offline))
                MessageBox.Show(owner,
                    "The item lists could not be downloaded: the internet connection is not working, or Google Sheets " +
                    "could not be reached or is busy right now.\n\nYour current lists are still used. Try again later.",
                    "Item Lists", MessageBoxButton.OK, MessageBoxImage.Warning);
            return changed;
        }

        private static async Task<bool> ResolveOneAsync(Window owner, SourcesConfig config, CatalogSourceConfig? entry,
                                                        SourceUpdateResult result, Func<string, int> currentItems)
        {
            string? brokenLink = result.Link;
            while (true)
            {
                bool hasList = entry != null && File.Exists(entry.Path);
                var dialog = new ListProblemDialog(result, hasList) { Owner = owner };
                if (dialog.ShowDialog() != true) return false;

                if (dialog.Choice == ListProblemDialog.Fix.File)
                {
                    var pick = new OpenFileDialog
                    {
                        Title = "Choose the Excel File of the List",
                        Filter = "Item lists (*.xlsx;*.xlsm;*.csv;*.tsv)|*.xlsx;*.xlsm;*.csv;*.tsv|All files (*.*)|*.*"
                    };
                    if (pick.ShowDialog(owner) != true) continue;

                    entry ??= AddEntry(config, pick.FileName, brokenLink);
                    entry.Path = pick.FileName;
                    entry.Enabled = true;
                    entry.Url = null;
                    entry.NoLink = true;      // kept by hand: don't try the broken link again
                    entry.UpdatedUtc = null;
                    return true;
                }

                var linkDialog = new LinkDialog { Owner = owner };
                if (linkDialog.ShowDialog() != true) continue;
                string link = linkDialog.Link;

                Mouse(true);
                SourceUpdateResult attempt;
                try
                {
                    attempt = await SourceDownloader.UpdateAsync(link, hasList ? entry!.Path : null,
                                                                  hasList ? currentItems(entry!.Path) : 0);
                }
                finally { Mouse(false); }

                if (attempt.Updated)
                {
                    entry ??= AddEntry(config, attempt.Path!, brokenLink);
                    entry.Path = attempt.Path!;
                    entry.Enabled = true;
                    entry.Url = link;
                    entry.NoLink = false;
                    entry.UpdatedUtc = DateTime.UtcNow;
                    MessageBox.Show(owner, $"{attempt.FileName}: {attempt.Message}", "Item Lists",
                                    MessageBoxButton.OK, MessageBoxImage.Information);
                    return true;
                }
                result = attempt;   // show what is wrong with the new link
            }
        }

        private static CatalogSourceConfig AddEntry(SourcesConfig config, string path, string? brokenLink)
        {
            var known = ListUpdates.KnownLists.FirstOrDefault(k => string.Equals(k.Link, brokenLink, StringComparison.OrdinalIgnoreCase));
            var entry = new CatalogSourceConfig
            {
                Path = path,
                Priority = known.Link != null ? known.Priority : SourcesConfigStore.GuessPriority(Path.GetFileName(path))
            };
            config.Sources.Add(entry);
            return entry;
        }

        private static void Mouse(bool busy) =>
            System.Windows.Input.Mouse.OverrideCursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }
}
