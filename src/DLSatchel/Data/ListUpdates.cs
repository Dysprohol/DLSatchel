using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DDV_SaveEditor_G.Data
{
    /// <summary>
    /// Keeping the item lists fresh without the player having to know how: download the two
    /// known lists on first start, tell when they are old, update every list that has a link.
    /// Nothing here runs on its own; the window asks first.
    /// </summary>
    public static class ListUpdates
    {
        /// <summary>Lists older than this are worth refreshing (the game updates every few weeks).</summary>
        public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

        /// <summary>The two lists the editor is built around, with their usual priority.</summary>
        public static readonly IReadOnlyList<(string Link, int Priority)> KnownLists = new[]
        {
            (SourceDownloader.KosddvLink, 10),
            (SourceDownloader.RubyelfLink, 30)
        };

        /// <summary>The link of a list: the one the player set, or the usual one for a known file.</summary>
        public static string? LinkOf(CatalogSourceConfig source) =>
            source.NoLink ? null
            : !string.IsNullOrWhiteSpace(source.Url) ? source.Url!.Trim()
            : SourceDownloader.SuggestLink(Path.GetFileName(source.Path));

        public static bool HasEnabledLists(SourcesConfig config) =>
            config.Sources.Any(s => s.Enabled && File.Exists(s.Path));

        /// <summary>
        /// True when the game was updated after the oldest enabled list was downloaded: new items
        /// from that update may be missing from the lists, or not checked by anyone yet.
        /// </summary>
        public static bool GameNewerThanLists(SourcesConfig config, DateTime? gameUpdatedUtc)
        {
            if (gameUpdatedUtc is not DateTime game) return false;
            var listTimes = config.Sources
                .Where(s => s.Enabled && File.Exists(s.Path))
                .Select(s => s.UpdatedUtc ?? File.GetLastWriteTimeUtc(s.Path))
                .ToList();
            return listTimes.Count > 0 && listTimes.Min() < game;
        }

        /// <summary>How old the oldest enabled list with a link is; null when none has a link.</summary>
        public static TimeSpan? OldestAge(SourcesConfig config, DateTime nowUtc)
        {
            var ages = config.Sources
                .Where(s => s.Enabled && LinkOf(s) != null)
                .Select(s => s.UpdatedUtc ?? (File.Exists(s.Path) ? File.GetLastWriteTimeUtc(s.Path) : DateTime.MinValue))
                .Select(when => nowUtc - when)
                .ToList();
            return ages.Count == 0 ? null : ages.Max();
        }

        /// <summary>
        /// Downloads every enabled list that has a link. Updated entries point at the fresh copy in
        /// the editor's folder and remember the link; the rest stay as they were.
        /// </summary>
        public static async Task<List<SourceUpdateResult>> UpdateAllAsync(
            SourcesConfig config, Func<string, int> currentItems,
            IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            var results = new List<SourceUpdateResult>();
            foreach (var source in config.Sources.Where(s => s.Enabled).ToList())
            {
                string? link = LinkOf(source);
                if (link == null) continue;

                progress?.Report($"Downloading {Path.GetFileName(source.Path)}…");
                var result = await SourceDownloader.UpdateAsync(link, source.Path, currentItems(source.Path), cancellationToken)
                    .ConfigureAwait(true);
                if (result.Updated)
                {
                    source.Path = result.Path!;
                    source.Url = StoredLink(link, source.Path);
                    source.NoLink = false;
                    source.UpdatedUtc = DateTime.UtcNow;
                }
                results.Add(result);
            }
            RemoveDuplicates(config);
            return results;
        }

        /// <summary>Downloads the known lists the config does not have yet (first start).</summary>
        public static async Task<List<SourceUpdateResult>> AddKnownListsAsync(
            SourcesConfig config, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            var results = new List<SourceUpdateResult>();
            foreach (var (link, priority) in KnownLists)
            {
                if (config.Sources.Any(s => s.Enabled && File.Exists(s.Path) &&
                                            string.Equals(LinkOf(s), link, StringComparison.OrdinalIgnoreCase)))
                    continue;

                progress?.Report("Downloading the item lists…");
                var result = await SourceDownloader.UpdateAsync(link, null, 0, cancellationToken).ConfigureAwait(true);
                if (result.Updated)
                {
                    var entry = config.Sources.FirstOrDefault(s => string.Equals(s.Path, result.Path, StringComparison.OrdinalIgnoreCase));
                    if (entry == null)
                    {
                        entry = new CatalogSourceConfig { Path = result.Path!, Priority = priority };
                        config.Sources.Add(entry);
                    }
                    entry.Enabled = true;
                    entry.Url = StoredLink(link, entry.Path);
                    entry.NoLink = false;
                    entry.UpdatedUtc = DateTime.UtcNow;
                }
                results.Add(result);
            }
            RemoveDuplicates(config);
            return results;
        }

        /// <summary>Two entries for one file (an old entry moved onto an existing copy): keep one, switched on if any is.</summary>
        private static void RemoveDuplicates(SourcesConfig config)
        {
            var keep = config.Sources
                .GroupBy(s => FullPath(s.Path), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(s => s.Enabled).First())
                .ToHashSet();
            config.Sources = config.Sources.Where(keep.Contains).ToList();
        }

        private static string FullPath(string path)
        {
            try { return Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
        }

        /// <summary>
        /// The link to remember in sources.json: none when it is the usual link for the file's name,
        /// so a future version can change the built-in link and everyone follows.
        /// </summary>
        public static string? StoredLink(string link, string path) =>
            string.Equals(link, SourceDownloader.SuggestLink(Path.GetFileName(path)), StringComparison.OrdinalIgnoreCase)
                ? null : link;

        /// <summary>One line per list for a message box.</summary>
        public static string Describe(IEnumerable<SourceUpdateResult> results) =>
            string.Join("\n", results.Select(r => $"{(r.Updated ? "✓" : "✗")} {r.FileName}: {r.Message}"));
    }
}
