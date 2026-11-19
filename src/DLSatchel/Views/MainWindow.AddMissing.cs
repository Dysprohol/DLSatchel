using System.Data;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using DDV_SaveEditor_G.Modules;

namespace DDV_SaveEditor_G
{
    /// <summary>"Add all missing": one of every item of the open collection the player does not have.</summary>
    public partial class MainWindow
    {
        /// <summary>The list collection of the open tab, if it can be edited.</summary>
        private (string Key, string Label)? CurrentListTarget()
        {
            if (jsonData == null) return null;
            if (CurrentTab == "Clothes")
                return clothesKey != null ? (clothesKey, "Wardrobe") : null;
            if (IsCurrentStorageTab && TryGetCurrentStorageInfo(out var info) && ListInventoryModule.IsEditableType(info.ItemType))
                return (info.Key, DisplayInventoryType(info.ItemType));
            return null;
        }

        private void AddAllMissing_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentListTarget() is not (string key, string label) || jsonData == null) return;
            if (ItemDatabase.Table == null)
            {
                MessageBox.Show("Load the item lists first.", "Add All Missing", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (GetStorageTable(key) is not DataTable table) return;

            var owned = table.AsEnumerable()
                .Where(r => r.RowState != DataRowState.Deleted)
                .Select(r => r.Field<long>("Item ID"))
                .ToHashSet();
            owned.UnionWith(SaveItemIds.PlacedInWorld(jsonData));   // placed furniture is no longer in storage
            owned.UnionWith(SaveItemIds.Collect(jsonData).Where(kv => kv.Value == "Backpack").Select(kv => kv.Key));

            System.Collections.Generic.List<MissingItem> missing;
            Mouse.OverrideCursor = Cursors.Wait;
            try { missing = MissingItems.Find(jsonData, key, owned); }
            finally { Mouse.OverrideCursor = null; }

            if (missing.Count == 0)
            {
                MessageBox.Show($"{label}: you already have every item your lists know for this collection.",
                                "Add All Missing", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new AddMissingDialog(label, missing) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            var groups = dialog.SelectedGroups;
            var chosen = missing.Where(m => groups.Contains(m.Group)).ToList();

            Mouse.OverrideCursor = Cursors.Wait;
            int added = 0;
            try
            {
                table.BeginLoadData();
                foreach (var item in chosen)
                {
                    if (!AddToList(table, item.Id, 1)) continue;
                    ItemPlacement.MarkCollected(jsonData, item.Id);
                    added++;
                }
            }
            finally
            {
                table.EndLoadData();
                Mouse.OverrideCursor = null;
            }

            AddStatusText.Text = $"Added {added:N0} missing item(s) to {label}. Select Save Changes when you are ready.";
        }
    }
}
