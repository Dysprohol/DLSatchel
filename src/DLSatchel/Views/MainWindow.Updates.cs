using System.IO;
using System.Net.Http;
using System.Windows;
using DDV_SaveEditor_G.Updates;

namespace DDV_SaveEditor_G
{
    /// <summary>Top-right corner: version, the GitHub link and "Check for updates".</summary>
    public partial class MainWindow
    {
        private AppSettings appSettings = new();
        private ReleaseInfo? availableRelease;
        private bool loadingUpdateSettings;

        private void InitializeUpdates()
        {
            UpdateChecker.DeleteLeftoverFromUpdate();
            VersionText.Text = "v" + UpdateChecker.CurrentVersion.ToString(3);

            appSettings = AppSettings.Load();
            loadingUpdateSettings = true;
            CheckUpdatesAtStartCheckBox.IsChecked = appSettings.CheckUpdatesAtStart;
            loadingUpdateSettings = false;

            if (appSettings.CheckUpdatesAtStart)
                _ = CheckForUpdatesAsync();
        }

        private void GitHubLink_Click(object sender, RoutedEventArgs e)
            => UpdateChecker.OpenInBrowser(UpdateChecker.ReleasesUrl);

        private void CheckUpdatesAtStart_Changed(object sender, RoutedEventArgs e)
        {
            if (loadingUpdateSettings) return;
            appSettings.CheckUpdatesAtStart = CheckUpdatesAtStartCheckBox.IsChecked == true;
            appSettings.Save();
        }

        private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync();

        private async Task CheckForUpdatesAsync()
        {
            CheckUpdatesButton.IsEnabled = false;
            ShowUpdateStatus("Checking GitHub…");
            try
            {
                var latest = await UpdateChecker.GetLatestAsync();
                if (latest == null)
                {
                    ShowUpdateStatus("Could not read the latest release.");
                }
                else if (latest.Version > UpdateChecker.CurrentVersion)
                {
                    availableRelease = latest;
                    bool oneClick = UpdateChecker.CanInstallInPlace && latest.ExeUrl != null;
                    ShowUpdateStatus($"{latest.Tag} is available. ", oneClick ? "Update now" : "Open download page");
                }
                else
                {
                    ShowUpdateStatus("You have the latest version.");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or Newtonsoft.Json.JsonException)
            {
                ShowUpdateStatus("Cannot reach GitHub right now.");
            }
            finally
            {
                CheckUpdatesButton.IsEnabled = true;
            }
        }

        private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (availableRelease is not ReleaseInfo release) return;

            if (!UpdateChecker.CanInstallInPlace || release.ExeUrl == null)
            {
                UpdateChecker.OpenInBrowser(release.PageUrl);
                return;
            }

            if (MessageBox.Show($"Download {release.Tag} and restart the editor?\n\nChanges you have not saved will be lost.",
                    "Update", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            CheckUpdatesButton.IsEnabled = false;
            try
            {
                var progress = new Progress<double>(share => ShowUpdateStatus($"Downloading {release.Tag}… {share:P0}"));
                string downloaded = await UpdateChecker.DownloadAsync(release, progress);

                if (UpdateChecker.TryInstallAndRestart(downloaded))
                {
                    Application.Current.Shutdown();
                    return;
                }

                // The editor's folder is read-only (e.g. Program Files): leave the new file in Downloads.
                string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                Directory.CreateDirectory(downloads);
                string saved = Path.Combine(downloads, Path.GetFileName(downloaded));
                File.Copy(downloaded, saved, overwrite: true);
                ShowUpdateStatus($"{release.Tag} saved to Downloads.");
                MessageBox.Show($"The editor's folder cannot be written to, so the new version was saved here:\n{saved}\n\nClose this window and start that file.",
                                "Update", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                           or InvalidDataException or UnauthorizedAccessException)
            {
                ShowUpdateStatus("The update could not be downloaded.");
                MessageBox.Show($"The update could not be downloaded:\n{ex.Message}\n\nYou can get it from the releases page.",
                                "Update", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                CheckUpdatesButton.IsEnabled = true;
            }
        }

        private void ShowUpdateStatus(string text, string action = "")
        {
            UpdateStatusRun.Text = text;
            InstallUpdateRun.Text = action;
            InstallUpdateLink.IsEnabled = action.Length > 0;
        }
    }
}
