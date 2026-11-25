using System.Collections.Generic;
using System.Linq;
using System.Windows;
using DDV_SaveEditor_G.Modules;

namespace DDV_SaveEditor_G
{
    /// <summary>Lists the copies the editor kept next to the save and lets the player pick one.</summary>
    public partial class RestoreBackupDialog : Window
    {
        public RestoreBackupDialog(IReadOnlyList<SaveBackup> backups)
        {
            InitializeComponent();
            BackupList.ItemsSource = backups;
            var times = backups.Select(b => b.Time).ToList();
            SummaryText.Text = backups.Count == 1
                ? "1 copy next to your save."
                : $"{backups.Count} copies next to your save, from {SaveBackup.Format(times.Min(), "d MMM yyyy")} to {SaveBackup.Format(times.Max(), "d MMM yyyy")}. Newest first.";
            _folder = System.IO.Path.GetDirectoryName(backups[0].Path) ?? "";
            _first = backups[0].Path;
        }

        private readonly string _folder;
        private readonly string _first;

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            string target = (Selected?.Path ?? _first);
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{target}\""); }
            catch { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_folder) { UseShellExecute = true }); }
        }

        public SaveBackup? Selected => BackupList.SelectedItem as SaveBackup;

        private void BackupList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
            => OkButton.IsEnabled = Selected != null;

        private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
