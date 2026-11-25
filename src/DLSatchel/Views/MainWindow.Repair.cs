using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using DDV_SaveEditor_G.Data;
using DDV_SaveEditor_G.Modules;
using DDV_SaveEditor_G.Updates;
using Newtonsoft.Json.Linq;

namespace DDV_SaveEditor_G
{
    /// <summary>Going back after an edit went wrong, and a report people can paste when asking for help.</summary>
    public partial class MainWindow
    {
        private void RestoreBackup_Click(object sender, RoutedEventArgs e)
        {
            if (jsonFilePath == null)
            {
                MessageBox.Show("Load a save first.", "Restore Backup", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var backups = SaveBackups.Find(jsonFilePath);
            if (backups.Count == 0)
            {
                MessageBox.Show("There are no backups next to this save yet. The editor makes one every time you save.",
                                "Restore Backup", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new RestoreBackupDialog(backups) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Selected is not SaveBackup chosen) return;

            try
            {
                string copy = SaveBackups.Restore(jsonFilePath, chosen);
                LoadSave(jsonFilePath, quiet: true);
                MessageBox.Show($"Restored {chosen.Description}.\n\nThe save you had before is kept as {Path.GetFileName(copy)}.",
                                "Restore Backup", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Not restored: {ex.Message}", "Restore Backup", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>The save as it was before the editor first opened it, if that copy is there.</summary>
        private JObject? FirstBackup()
        {
            if (jsonFilePath == null) return null;
            string bak = jsonFilePath + ".bak";
            return File.Exists(bak) && SaveDecoder.TryDecode(bak, out var json, out _) ? json : null;
        }

        /// <summary>
        /// Part of Check Save: flagged items that were added to the collections since the first backup.
        /// Offers to take them out again (only from the editor's tables; Save Changes writes it).
        /// </summary>
        private void OfferToRemoveRiskyAdditions()
        {
            if (jsonData == null || FirstBackup() is not JObject before) return;
            var risky = RiskyAdditions.Find(jsonData, before);
            var strong = risky.Where(r => !r.OnlyUnchecked).ToList();
            var unchecked_ = risky.Where(r => r.OnlyUnchecked).ToList();
            int removed = 0;

            if (strong.Count > 0 && AskToRemove(strong,
                    $"{strong.Count} item(s) the lists warn about were added to your collections since your first backup:",
                    "Remove them now?"))
                removed += Remove(strong);

            if (unchecked_.Count > 0 && AskToRemove(unchecked_,
                    $"{unchecked_.Count} item(s) the community list hasn't checked yet were added since your first backup. " +
                    "Some may be new items you really got in the game; others may be unfinished or developer items:",
                    "Remove these too? If you're not sure, choose No - and if the game misbehaves, use Restore Backup…"))
                removed += Remove(unchecked_);

            if (removed > 0)
                AddStatusText.Text = $"Removed {removed} item(s) from your collections. Click Save Changes to keep this.";
        }

        private static bool AskToRemove(List<RiskyItem> items, string intro, string question)
        {
            var lines = items.Take(15).Select(r => $"• {r.Name} - {r.Reason}").ToList();
            if (items.Count > 15) lines.Add($"… and {items.Count - 15} more");
            string text = intro + "\n\n" + string.Join("\n", lines) + "\n\n" + question +
                          " Nothing is written until you click Save Changes.";
            return MessageBox.Show(text,
                                   "Check Save", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        private int Remove(List<RiskyItem> items)
        {
            int removed = 0;
            foreach (var item in items)
            {
                var row = GetStorageTable(item.ListKey)?.AsEnumerable()
                    .FirstOrDefault(r => r.RowState != DataRowState.Deleted && r.Field<long>("Item ID") == item.Id);
                if (row == null) continue;
                row.Delete();
                removed++;
            }
            return removed;
        }

        private void CopyReport_Click(object sender, RoutedEventArgs e)
        {
            var text = new StringBuilder();
            text.AppendLine($"DL Satchel {UpdateChecker.CurrentVersion.ToString(3)}, {Environment.OSVersion.VersionString}");

            if (jsonData != null)
            {
                text.AppendLine($"Save version: {jsonData["GameInfo"]?["Version"] ?? "?"}, " +
                                $"{(SaveDecoder.LastLoadWasEncrypted ? "encrypted" : "plain")} file");
                bool unsaved = petsDirty || storageTables.Values.Any(t => t.GetChanges() != null) ||
                               SaveDiffModule.Compare(loadedSaveSnapshot, jsonData, int.MaxValue).HasChanges;
                text.AppendLine($"Unsaved edits: {(unsaved ? "yes" : "no")}");
                text.AppendLine($"Backups next to the save: {(jsonFilePath == null ? 0 : SaveBackups.Find(jsonFilePath).Count)}");
                if (FirstBackup() is JObject before)
                {
                    var risky = RiskyAdditions.Find(jsonData, before);
                    text.AppendLine($"Flagged items added since the first backup: {risky.Count}" +
                                    (risky.Count > 0 ? $" ({string.Join(", ", risky.GroupBy(r => r.Reason).Select(g => $"{g.Count()} {g.Key}"))})" : ""));
                }
                if (ItemDatabase.Table != null)
                {
                    var issues = SaveValidationModule.Analyze(jsonData);
                    text.AppendLine($"Check Save: {issues.Count(i => i.Level == SaveIssueLevel.Error)} errors, " +
                                    $"{issues.Count(i => i.Level == SaveIssueLevel.Caution)} cautions, " +
                                    $"{SaveItemIds.NotInCatalog(jsonData).Count} items unknown to the lists");
                }
            }
            else text.AppendLine("No save open.");

            var game = PresetLayouts.GameUpdatedUtc();
            text.AppendLine($"Game files: {(game is DateTime g ? $"found, updated {g:yyyy-MM-dd}" : "not found")}");
            foreach (var source in CurrentSources().Sources.Where(s => s.Enabled))
            {
                DateTime? updated = source.UpdatedUtc ?? (File.Exists(source.Path) ? File.GetLastWriteTimeUtc(source.Path) : null);
                text.AppendLine($"List: {Path.GetFileName(source.Path)}, {CurrentItemCount(source.Path):N0} items, " +
                                $"updated {(updated is DateTime u ? u.ToString("yyyy-MM-dd") : "?")}");
            }
            text.AppendLine();
            text.AppendLine("What I did (tab, button, items):");
            text.AppendLine("What I expected:");
            text.AppendLine("What happened instead:");

            try
            {
                Clipboard.SetText(text.ToString());
                AddStatusText.Text = "Report copied (versions, counts and list names - nothing personal). " +
                                     "Paste it into your post and fill in the last three lines.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not copy: {ex.Message}", "Copy Report", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
