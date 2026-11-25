using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using DDV_SaveEditor_G.Data;
using DDV_SaveEditor_G.Modules;

namespace DDV_SaveEditor_G
{
    /// <summary>
    /// Item lists without having to know about them: offer to download them on first start,
    /// say when they are old and update them with one click. Nothing is downloaded unasked.
    /// </summary>
    public partial class MainWindow
    {
        private bool _listsBusy;
        private bool _listsNoticeOffersDownload;

        private SourcesConfig CurrentSources()
        {
            var config = ItemDatabase.DetailedCatalog?.Sources;
            return config != null && config.Sources.Count > 0
                ? config
                : SourcesConfigStore.LoadOrCreate(SourcesConfigStore.ResolvePath(null), saveWhenCreated: false);
        }

        private static int CurrentItemCount(string path) =>
            ItemDatabase.DetailedCatalog?.Diagnostics.Sources
                .FirstOrDefault(d => string.Equals(d.File, path, StringComparison.OrdinalIgnoreCase))?.UniqueIds ?? 0;

        /// <summary>The gold line under the buttons: no lists yet, or lists that are getting old.</summary>
        private void UpdateListsNotice()
        {
            var config = CurrentSources();
            if (!ListUpdates.HasEnabledLists(config))
            {
                ShowListsNotice("No item lists yet.", "Download them", offersDownload: true);
                return;
            }

            var age = ListUpdates.OldestAge(config, DateTime.UtcNow);
            if (ListUpdates.GameNewerThanLists(config, PresetLayouts.GameUpdatedUtc()))
                ShowListsNotice("The game was updated after your item lists - new items are not checked yet. Back up before editing.",
                                "Update now", offersDownload: false);
            else if (age is TimeSpan old && old >= ListUpdates.StaleAfter)
                ShowListsNotice($"Your item lists are {(int)old.TotalDays} days old.", "Update now", offersDownload: false);
            else
                ListsNotice.Visibility = Visibility.Collapsed;
        }

        private void ShowListsNotice(string text, string link, bool offersDownload)
        {
            ListsNoticeText.Text = text + " ";
            ListsNoticeLinkText.Text = link;
            _listsNoticeOffersDownload = offersDownload;
            ListsNotice.Visibility = Visibility.Visible;
        }

        private async void ListsNotice_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_listsNoticeOffersDownload) await DownloadKnownListsAsync();
                else await UpdateListsAsync();
            }
            catch (Exception ex)
            {
                ShowListError(ex);
            }
        }

        /// <summary>An unexpected error while handling lists: say so, keep the editor and the save open.</summary>
        private void ShowListError(Exception ex)
        {
            _listsBusy = false;
            System.Windows.Input.Mouse.OverrideCursor = null;
            UpdateListsNotice();
            MessageBox.Show($"The item lists could not be updated: {ex.Message}\n\nYour save and your current lists were not changed.",
                            "Item Lists", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>Asked when the editor would wait for a running list download.</summary>
        private bool ListsBusyNote()
        {
            if (!_listsBusy) return false;
            MessageBox.Show("The item lists are being downloaded. Wait a moment until it finishes.",
                            "Item Lists", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }

        /// <summary>First start without lists: ask once, then fetch the two known lists.</summary>
        private async Task OfferFirstDownloadAsync()
        {
            UpdateListsNotice();
            if (appSettings.ListDownloadDeclined) return;   // the gold line keeps offering it
            var answer = MessageBox.Show(
                "DL Satchel needs two item lists that players on the FearLess Revolution forum keep up to date: " +
                "the KOSDDV item dump and Rubyelf's ID list. Without them it cannot tell what an item is or where it goes.\n\n" +
                "Download them now? (About 9 MB from Google Sheets.)",
                "Item Lists", MessageBoxButton.YesNo, MessageBoxImage.Question);
            appSettings.ListDownloadDeclined = answer != MessageBoxResult.Yes;
            appSettings.Save();
            if (answer == MessageBoxResult.Yes)
            {
                try { await DownloadKnownListsAsync(); }
                catch (Exception ex) { ShowListError(ex); }
            }
        }

        private async Task DownloadKnownListsAsync()
        {
            if (_listsBusy) return;
            _listsBusy = true;
            var config = CurrentSources();
            List<SourceUpdateResult> results;
            try
            {
                ListsNotice.Visibility = Visibility.Collapsed;
                results = await ListUpdates.AddKnownListsAsync(config, new Progress<string>(text => DbStatusText.Text = text));
                if (results.Any(r => r.Updated))
                    await LoadConfiguredDatabaseAsync(config, showErrors: true);
            }
            finally
            {
                _listsBusy = false;
                UpdateListsNotice();
            }

            if (await ListProblems.ResolveAsync(this, config, results, CurrentItemCount))
            {
                await LoadConfiguredDatabaseAsync(config, showErrors: true);
                UpdateListsNotice();
            }
        }

        /// <summary>Downloads every list that has a link and reloads the catalog. Returns whether any changed.</summary>
        private async Task<bool> UpdateListsAsync()
        {
            if (_listsBusy) return false;
            _listsBusy = true;
            var config = CurrentSources();
            List<SourceUpdateResult> results;
            try
            {
                ListsNotice.Visibility = Visibility.Collapsed;
                results = await ListUpdates.UpdateAllAsync(config, CurrentItemCount,
                    new Progress<string>(text => DbStatusText.Text = text));
                if (results.Count > 0)
                    await LoadConfiguredDatabaseAsync(config, showErrors: true);
            }
            finally
            {
                _listsBusy = false;
                UpdateListsNotice();
            }

            if (results.Count == 0)
            {
                MessageBox.Show("None of your lists has a link to update from. Open Sources… and use Add from link.",
                                "Item Lists", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            bool fixedByHand = await ListProblems.ResolveAsync(this, config, results, CurrentItemCount);
            if (fixedByHand)
            {
                await LoadConfiguredDatabaseAsync(config, showErrors: true);
                UpdateListsNotice();
            }
            return fixedByHand || results.Any(r => r.Updated);
        }
    }
}
