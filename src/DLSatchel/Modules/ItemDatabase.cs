using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DDV_SaveEditor_G.Data;

namespace DDV_SaveEditor_G.Modules
{
    /// <summary>
    /// Backwards-compatible facade over <see cref="ItemCatalog"/>.
    ///
    /// The old loader parsed the sheets itself; all of that now lives in the
    /// schema-driven engine (Data/CatalogBuilder). This class only projects the
    /// catalog onto the <see cref="DataTable"/> shape the UI and the save writers
    /// already understand, so no consumer had to change.
    /// </summary>
    internal static class ItemDatabase
    {
        /// <summary>Kept for callers that still name the old preferred sheet.</summary>
        public const string PreferredSheet = "English";

        /* Legacy columns first – unchanged order and types. */
        private static readonly (string Name, Type Type)[] LegacyColumns =
        {
            ("ID", typeof(string)),
            ("NAME", typeof(string)),
            ("LOCALIZED_DISPLAY_NAME", typeof(string)),
            ("TYPE", typeof(string)),
            ("CATEGORY", typeof(string)),
            ("NOTES", typeof(string)),
            ("PATCH", typeof(string)),
            ("UNSAFE", typeof(bool))
        };

        /* New, additive columns – no existing consumer depends on them. */
        private static readonly (string Name, Type Type)[] ExtraColumns =
        {
            ("KIND", typeof(string)),
            ("SUBTYPE", typeof(string)),
            ("SAFETY", typeof(string)),
            ("PLACEMENT", typeof(string)),
            ("HAS_NAME", typeof(bool)),
            ("IS_TEST", typeof(bool)),
            ("OWNED_STATUS", typeof(string))
        };

        private static Dictionary<long, DataRow> _byId = new();

        public static DataTable? Table { get; private set; }

        /// <summary>The catalog behind <see cref="Table"/>; never null.</summary>
        public static IItemCatalog Catalog { get; private set; } = ItemCatalog.Empty;

        /// <summary>Extra API of the concrete catalog (diagnostics, reference gaps).</summary>
        public static ItemCatalog? DetailedCatalog => Catalog as ItemCatalog;

        /// <summary>Returns the current sources configuration (for Sources panel).</summary>
        public static SourcesConfig GetSourcesConfig()
            => DetailedCatalog?.Sources ?? new SourcesConfig();

        /// <summary>Adds a new source file to the config and reloads.</summary>
        public static void AddSource(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            var config = GetSourcesConfig();
            if (config.Sources.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))) return;
            config.Sources.Add(new CatalogSourceConfig { Path = Path.GetFullPath(path), Priority = 25, Enabled = true });
            // reload is triggered by the caller via LoadAsync
        }

        public static IReadOnlyList<string> LoadedFiles { get; private set; } = Array.Empty<string>();

        /// <summary>Files that could not be read ("name: reason"). Loading continues.</summary>
        public static IReadOnlyList<string> Errors { get; private set; } = Array.Empty<string>();

        /// <summary>Raised after a table has been rebuilt (Sources panel / startup).</summary>
        public static event EventHandler? Changed;

        /* =================================================================
         *  Loading
         * ================================================================= */

        /// <summary>
        /// Synchronous load, kept for the existing call sites. Prefer
        /// <see cref="LoadAsync"/> from the UI thread.
        /// </summary>
        public static DataTable Load(IEnumerable<string> paths)
            => LoadAsync(paths).GetAwaiter().GetResult();

        public static async Task<DataTable> LoadAsync(
            IEnumerable<string> paths,
            IProgress<CatalogProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var catalog = await ItemCatalog
                .LoadAsync(paths, new CatalogLoadOptions(), progress, cancellationToken)
                .ConfigureAwait(true);

            SetCatalog(catalog);
            return Table!;
        }

        public static Task<DataTable> LoadSingleAsync(string path)
            => LoadCatalogFilesAsync(new[] { path }, replaceCurrent: true);

        /// <summary>
        /// Enables the given catalog files and loads them merged with priorities
        /// (lower number wins a field; other sources fill the gaps, e.g. KOSDDV gives
        /// NAME/TYPE, Rubyelf adds Category/Notes and IDs the dump does not have).
        /// <paramref name="replaceCurrent"/> disables every other source first.
        /// </summary>
        public static async Task<DataTable> LoadCatalogFilesAsync(IEnumerable<string> paths, bool replaceCurrent)
        {
            var fullPaths = new List<string>();
            foreach (string path in paths)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    throw new FileNotFoundException("The selected item catalog does not exist.", path);
                fullPaths.Add(Path.GetFullPath(path));
            }

            string configPath = SourcesConfigStore.ResolvePath(null);
            var config = File.Exists(configPath)
                ? SourcesConfigStore.LoadOrCreate(configPath, saveWhenCreated: false)
                : new SourcesConfig();

            if (replaceCurrent)
                foreach (var source in config.Sources) source.Enabled = false;

            foreach (string fullPath in fullPaths)
            {
                var existing = config.Sources.FirstOrDefault(source =>
                    string.Equals(Path.GetFullPath(source.Path), fullPath, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.Enabled = true;
                    continue;
                }

                config.Sources.Add(new CatalogSourceConfig
                {
                    Path = fullPath,
                    Priority = SourcesConfigStore.GuessPriority(Path.GetFileName(fullPath)),
                    Enabled = true
                });
            }

            return await LoadFromConfigAsync(config).ConfigureAwait(true);
        }

        public static async Task<DataTable> LoadFromConfigAsync(SourcesConfig config)
        {
            var catalog = await ItemCatalog.LoadFromConfigAsync(config, new CatalogLoadOptions()).ConfigureAwait(true);
            SetCatalog(catalog);
            return Table!;
        }

        /// <summary>Replaces the current table with the given catalog snapshot.</summary>
        public static void SetCatalog(IItemCatalog catalog)
        {
            Catalog = catalog ?? ItemCatalog.Empty;
            LoadedFiles = Catalog is ItemCatalog detailed
                ? detailed.LoadedFiles.ToArray()
                : Array.Empty<string>();
            Errors = Catalog is ItemCatalog detailedErrors
                ? detailedErrors.Errors.ToArray()
                : Array.Empty<string>();

            Table = BuildTable(Catalog, out _byId);
            Changed?.Invoke(null, EventArgs.Empty);
        }

        public static void Clear()
        {
            Table = null;
            _byId = new Dictionary<long, DataRow>();
            Catalog = ItemCatalog.Empty;
            LoadedFiles = Array.Empty<string>();
            Errors = Array.Empty<string>();
            Changed?.Invoke(null, EventArgs.Empty);
        }

        /* =================================================================
         *  Legacy query API
         * ================================================================= */

        public static DataRow? Find(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            if (!IdNormalizer.TryNormalize(id, out long parsed, out _, out _)) return null;
            return _byId.TryGetValue(parsed, out var row) ? row : null;
        }

        public static DataRow? Find(long id) => _byId.TryGetValue(id, out var row) ? row : null;

        public static ItemRecord? FindRecord(long id) => Catalog.Get(id);

        public static string Get(DataRow? row, string column, string fallback = "?")
        {
            if (row == null || !row.Table.Columns.Contains(column)) return fallback;
            string? value = row[column] as string;
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        /// <summary>
        /// Category shown in the save grids: the game TYPE when the catalog has it
        /// (KOSDDV dump), otherwise the source's own Category (Rubyelf has no TYPE).
        /// </summary>
        public static string GetCategory(DataRow? row, string fallback = "?")
            => Get(row, "TYPE", Get(row, "CATEGORY", fallback));

        /// <summary>
        /// Internal name for the save grids. "—" means the item is in the catalog but
        /// the source has no internal NAME column (e.g. Rubyelf); "?" means not in the catalog.
        /// </summary>
        public static string GetInternalName(DataRow? row)
            => row == null ? "?" : Get(row, "NAME", "—");

        public static bool IsUnsafe(DataRow? row)
        {
            if (row == null) return false;
            if (row.Table.Columns.Contains("SAFETY") &&
                row["SAFETY"] is string safety &&
                safety == nameof(SafetyState.Unsafe))
                return true;
            return row["UNSAFE"] is true;
        }

        /* =================================================================
         *  Projection
         * ================================================================= */

        private static DataTable BuildTable(IItemCatalog catalog, out Dictionary<long, DataRow> index)
        {
            var table = new DataTable { CaseSensitive = false, Locale = System.Globalization.CultureInfo.InvariantCulture };
            foreach (var (name, type) in LegacyColumns) table.Columns.Add(name, type);
            foreach (var (name, type) in ExtraColumns) table.Columns.Add(name, type);

            var records = catalog.All;
            index = new Dictionary<long, DataRow>(records.Count);
            table.BeginLoadData();

            try
            {
                foreach (var record in records)
                {
                    var row = table.NewRow();

                    row["ID"] = record.Id.ToString();
                    row["NAME"] = record.Name;
                    row["LOCALIZED_DISPLAY_NAME"] = record.DisplayName;
                    row["TYPE"] = record.RawType;
                    row["CATEGORY"] = record.Category;
                    row["NOTES"] = record.Notes;
                    row["PATCH"] = record.Patch;
                    row["UNSAFE"] = record.Safety == SafetyState.Unsafe;

                    row["KIND"] = record.Kind.ToString();
                    row["SUBTYPE"] = record.Subtype;
                    row["SAFETY"] = record.SafetyText;
                    row["PLACEMENT"] = record.Placement.ToString();
                    row["HAS_NAME"] = record.HasInternalName;
                    row["IS_TEST"] = record.IsTestItem;
                    row["OWNED_STATUS"] = "";

                    table.Rows.Add(row);
                    index[record.Id] = row;
                }
            }
            finally
            {
                table.EndLoadData();
            }

            return table;
        }

        /* =================================================================
         *  Diagnostics helpers used by the Sources panel
         * ================================================================= */

        public static string DescribeLoad()
        {
            var diagnostics = Catalog.Diagnostics;
            string files = LoadedFiles.Count == 0
                ? "no files"
                : string.Join(", ", LoadedFiles.Select(Path.GetFileName));

            return diagnostics.FromCache
                ? $"{Catalog.Count} entries from {files} (cache, {diagnostics.LoadMs:F0} ms)"
                : $"{Catalog.Count} entries from {files} ({diagnostics.LoadMs:F0} ms)";
        }
    }
}
