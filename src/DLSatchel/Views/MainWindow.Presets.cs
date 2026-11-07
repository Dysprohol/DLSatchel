using System.Data;
using System.Text;
using System.Windows;
using DDV_SaveEditor_G.Modules;

namespace DDV_SaveEditor_G
{
    /// <summary>Unlocking a Gameloft preset together with the furniture its layout places.</summary>
    public partial class MainWindow
    {
        private sealed record MissingPiece(long Id, string Name, string Safety, int Missing, DataTable Table);

        /// <summary>
        /// Unlocks the preset and, if the player agrees, adds the furniture of its layout that is
        /// not yet in storage. Returns whether anything changed and the text for the status line.
        /// </summary>
        private (bool Added, string Extra) AddPresetWithFurniture(long presetId)
        {
            bool alreadyUnlocked = ItemPlacement.PresetList(jsonData)?
                .Any(t => t.Type == Newtonsoft.Json.Linq.JTokenType.Integer && (long)t == presetId) == true;
            var layout = PresetLayouts.Get(presetId);

            if (layout == null)
            {
                bool unlocked = ItemPlacement.AddPreset(jsonData, presetId);
                return (unlocked, unlocked ? " (its furniture list is not known, so only the preset was added)" : " — already unlocked");
            }

            var missing = FindMissingFurniture(layout, out int notRoutable);
            if (missing.Count == 0)
            {
                bool unlocked = ItemPlacement.AddPreset(jsonData, presetId);
                return (unlocked, unlocked ? " — you already have all its furniture" : " — already unlocked, and you have all its furniture");
            }

            string presetName = ItemDatabase.Get(ItemDatabase.Find(presetId), "LOCALIZED_DISPLAY_NAME", presetId.ToString());
            var text = new StringBuilder();
            text.AppendLine(alreadyUnlocked
                ? $"{presetName} is already unlocked, but your storage is missing furniture it needs:"
                : $"{presetName} is placed from {layout.Pieces} pieces of furniture. Your storage is missing:");
            text.AppendLine();
            foreach (var piece in missing.Take(15))
                text.AppendLine($"• {piece.Name} ×{piece.Missing}" + (piece.Safety is "UNSAFE" or "CAUTION" ? $"  [{piece.Safety}]" : ""));
            if (missing.Count > 15)
                text.AppendLine($"… and {missing.Count - 15} more");
            if (notRoutable > 0)
                text.AppendLine($"({notRoutable} item(s) cannot be added to storage and are skipped.)");
            text.AppendLine();
            text.Append(alreadyUnlocked
                ? "Add the missing furniture to your storage?"
                : "Yes – unlock the preset and add the missing furniture\nNo – unlock only the preset\nCancel – do nothing");

            var answer = MessageBox.Show(text.ToString(), "Preset Furniture",
                alreadyUnlocked ? MessageBoxButton.YesNo : MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel)
                return (false, " — cancelled");

            bool presetAdded = !alreadyUnlocked && ItemPlacement.AddPreset(jsonData, presetId);
            if (answer != MessageBoxResult.Yes)
                return (presetAdded, presetAdded ? " (without furniture)" : " — already unlocked");

            int pieces = 0;
            foreach (var piece in missing)
            {
                if (!AddToList(piece.Table, piece.Id, piece.Missing)) continue;
                ItemPlacement.MarkCollected(jsonData, piece.Id);
                pieces += piece.Missing;
            }
            return (presetAdded || pieces > 0,
                    (presetAdded ? "" : " (preset was already unlocked)") + $" with {pieces} piece(s) of furniture added to storage");
        }

        /// <summary>The layout's furniture that storage holds fewer of than the preset places.</summary>
        private List<MissingPiece> FindMissingFurniture(PresetLayout layout, out int notRoutable)
        {
            var missing = new List<MissingPiece>();
            notRoutable = 0;
            foreach (var (itemId, needed) in layout.Items.OrderBy(kv => kv.Key))
            {
                var placement = ItemPlacement.Resolve(jsonData, itemId.ToString());
                if (placement.Kind != PlacementKind.ListInventory || placement.ListKey == null ||
                    GetStorageTable(placement.ListKey) is not DataTable table)
                {
                    notRoutable++;
                    continue;
                }

                int have = table.AsEnumerable()
                    .Where(r => r.RowState != DataRowState.Deleted && r.Field<long>("Item ID") == itemId)
                    .Sum(r => r.Field<int>("Amount"));
                if (have >= needed) continue;

                var row = ItemDatabase.Find(itemId);
                missing.Add(new MissingPiece(itemId,
                    ItemDatabase.Get(row, "LOCALIZED_DISPLAY_NAME", itemId.ToString()),
                    ItemDatabase.Get(row, "SAFETY", ""),
                    needed - have,
                    table));
            }
            return missing;
        }
    }
}
