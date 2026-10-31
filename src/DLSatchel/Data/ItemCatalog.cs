using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DDV_SaveEditor_G.Data
{
    /// <summary>
    /// Immutable-snapshot item catalog. A reload builds a new snapshot and swaps
    /// the reference atomically, so queries never see a half-built catalog.
    /// </summary>
    public sealed class ItemCatalog : IItemCatalog
    {
        private sealed class Snapshot
        {
            public required ItemRecord[] Ordered { get; init; }
            public required Dictionary<long, ItemRecord> ById { get; init; }
            public required Dictionary<ItemKind, ItemRecord[]> ByKind { get; init; }
            public required CatalogDiagnostics Diagnostics { get; init; }
            public required string[] LoadedFiles { get; init; }
            public required string[] Errors { get; init; }
        }

        private static readonly Snapshot EmptySnapshot = new()
        {
            Ordered = Array.Empty<ItemRecord>(),
            ById = new Dictionary<long, ItemRecord>(),
            ByKind = new Dictionary<ItemKind, ItemRecord[]>(),
            Diagnostics = new CatalogDiagnostics(),
            LoadedFiles = Array.Empty<string>(),
            Errors = Array.Empty<string>()
        };

        public static ItemCatalog Empty { get; } = new(
            new SourcesConfig(), Taxonomy.Load(), EmptySnapshot, new CatalogLoadOptions(),
            new List<ReferenceGap>());

        private volatile Snapshot _snapshot;
        private SourcesConfig _config;
        private CatalogLoadOptions _options;
        private Taxonomy _taxonomy;
        private List<ReferenceGap> _referenceGaps;

        private ItemCatalog(
            SourcesConfig config,
            Taxonomy taxonomy,
            Snapshot snapshot,
            CatalogLoadOptions options,
            List<ReferenceGap> referenceGaps)
        {
            _config = config;
            _taxonomy = taxonomy;
            _snapshot = snapshot;
            _options = options;
            _referenceGaps = referenceGaps;
        }

        /* ================================================================= */

        public int Count => _snapshot.Ordered.Length;

        public IReadOnlyList<ItemRecord> All => _snapshot.Ordered;

        public CatalogDiagnostics Diagnostics => _snapshot.Diagnostics;

        public IReadOnlyList<string> LoadedFiles => _snapshot.LoadedFiles;

        public IReadOnlyList<string> Errors => _snapshot.Errors;

        public SourcesConfig Sources => _config.Clone();

        public Taxonomy Taxonomy => _taxonomy;

        /// <summary>Ids the caller checked against this catalog (collection sets, pets, …).</summary>
        public IReadOnlyList<ReferenceGap> ReferenceGaps => _referenceGaps;

        public event EventHandler? Reloaded;

        public ItemRecord? Get(long id)
            => _snapshot.ById.TryGetValue(id, out var record) ? record : null;

        public bool TryGet(long id, out ItemRecord record)
        {
            if (_snapshot.ById.TryGetValue(id, out var found))
            {
                record = found;
                return true;
            }
            record = null!;
            return false;
        }

        /* ================================================================= */

        public IReadOnlyList<ItemRecord> Search(string text, int limit = 200)
        {
            string query = TextNormalizer.FoldForSearch(text);
            if (query.Length == 0 || limit <= 0) return Array.Empty<ItemRecord>();

            var snapshot = _snapshot;
            var exact = new List<ItemRecord>(Math.Min(limit, 32));
            var starts = new List<ItemRecord>(Math.Min(limit, 64));
            var contains = new List<ItemRecord>(Math.Min(limit, 128));

            foreach (var record in snapshot.Ordered)
            {
                int index = record.SearchText.IndexOf(query, StringComparison.Ordinal);
                if (index < 0) continue;

                if (record.SearchText == query ||
                    record.SearchText.StartsWith(query + " ", StringComparison.Ordinal))
                {
                    if (exact.Count < limit) exact.Add(record);
                    if (exact.Count >= limit) break;
                }
                else if (index == 0)
                {
                    if (starts.Count < limit) starts.Add(record);
                }
                else if (contains.Count < limit)
                {
                    contains.Add(record);
                }
            }

            var result = new List<ItemRecord>(Math.Min(limit, exact.Count + starts.Count + contains.Count));
            result.AddRange(exact);
            if (result.Count < limit) result.AddRange(starts.Take(limit - result.Count));
            if (result.Count < limit) result.AddRange(contains.Take(limit - result.Count));
            return result;
        }

        public IReadOnlyList<ItemRecord> Query(ItemQuery query)
        {
            var snapshot = _snapshot;
            int take = query.Take <= 0 ? 200 : query.Take;
            int skip = Math.Max(0, query.Skip);

            string? text = string.IsNullOrWhiteSpace(query.Text)
                ? null
                : TextNormalizer.FoldForSearch(query.Text);
            string? subtype = string.IsNullOrWhiteSpace(query.Subtype)
                ? null
                : TextNormalizer.FoldForSearch(query.Subtype);
            string? patch = string.IsNullOrWhiteSpace(query.Patch)
                ? null
                : TextNormalizer.FoldForSearch(query.Patch);

            HashSet<long>? idFilter = query.Ids == null ? null : new HashSet<long>(query.Ids);

            var result = new List<ItemRecord>(Math.Min(take, 4096));

            foreach (var record in snapshot.Ordered)
            {
                if (query.Kind is { } kind && record.Kind != kind) continue;

                if (!query.IncludeUnsafe && record.Safety == SafetyState.Unsafe) continue;
                if (query.SafeOnly && record.Safety != SafetyState.Safe) continue;

                if (subtype != null &&
                    !TextNormalizer.FoldForSearch(record.Subtype).Contains(subtype, StringComparison.Ordinal))
                    continue;

                if (patch != null &&
                    !TextNormalizer.FoldForSearch(record.Patch).Contains(patch, StringComparison.Ordinal))
                    continue;

                if (idFilter != null && !idFilter.Contains(record.Id)) continue;

                if (text != null && !record.SearchText.Contains(text, StringComparison.Ordinal)) continue;

                if (skip > 0) { skip--; continue; }

                result.Add(record);
                if (result.Count >= take) break;
            }

            return result;
        }

        public IReadOnlyList<ItemRecord> ByKind(ItemKind kind)
            => _snapshot.ByKind.TryGetValue(kind, out var records) ? records : Array.Empty<ItemRecord>();

        /* ================================================================= */

        /// <summary>Checks ids coming from the save (e.g. CollectionDefinitionID) against the catalog.</summary>
        public List<ReferenceGap> FindMissingReferences(string kind, IEnumerable<long> ids)
        {
            var snapshot = _snapshot;
            var gaps = new List<ReferenceGap>();

            foreach (long id in ids.Distinct())
            {
                if (!snapshot.ById.ContainsKey(id))
                    gaps.Add(new ReferenceGap { Kind = kind, Id = id, Reason = "not-in-sources" });
            }

            gaps.Sort((a, b) => a.Id.CompareTo(b.Id));
            return gaps;
        }

        public void SetReferenceGaps(IEnumerable<ReferenceGap> gaps)
        {
            _referenceGaps = gaps.ToList();
            _snapshot.Diagnostics.MissingReferences = _referenceGaps;
        }

        /* =================================================================
         *  Loading
         * ================================================================= */

        /// <summary>
        /// Loads the catalog. When <paramref name="paths"/> is given, those files are
        /// merged into sources.json (priorities preserved); otherwise the config is used.
        /// </summary>
        public static Task<ItemCatalog> LoadAsync(
            IEnumerable<string>? paths = null,
            CatalogLoadOptions? options = null,
            IProgress<CatalogProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            options ??= new CatalogLoadOptions();

            SourcesConfig config;
            if (paths != null)
            {
                var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
                config = SourcesConfigStore.LoadOrCreate(options.SourcesPath);
                SourcesConfigStore.MergePaths(config, list);
                try { SourcesConfigStore.Save(config, options.SourcesPath); }
                catch { /* read-only install: keep the in-memory config */ }
            }
            else
            {
                config = SourcesConfigStore.LoadOrCreate(options.SourcesPath);
            }

            return BuildAsync(config, options, progress, cancellationToken);
        }

        /// <summary>Loads an explicit config (used by the Sources panel after editing).</summary>
        public static Task<ItemCatalog> LoadFromConfigAsync(
            SourcesConfig config,
            CatalogLoadOptions? options = null,
            IProgress<CatalogProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            options ??= new CatalogLoadOptions();
            try { SourcesConfigStore.Save(config, options.SourcesPath); }
            catch { /* read-only install: keep the in-memory config */ }

            return BuildAsync(config, options, progress, cancellationToken);
        }

        private static async Task<ItemCatalog> BuildAsync(
            SourcesConfig config,
            CatalogLoadOptions options,
            IProgress<CatalogProgress>? progress,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();

            string taxonomyPath = options.TaxonomyPath
                                  ?? Path.Combine(AppContext.BaseDirectory, "taxonomy.json");
            var taxonomy = Taxonomy.Load(taxonomyPath);

            string cachePath = CatalogCache.ResolvePath(options.CacheDirectory);
            string key = CatalogCache.ComputeKey(config, taxonomy);

            if (options.UseCache && CatalogCache.TryLoad(cachePath, key, out var cached))
            {
                // Convert lean cached records back to full ItemRecord.
                var ordered = new ItemRecord[cached.Ordered.Length];
                for (int i = 0; i < cached.Ordered.Length; i++)
                    ordered[i] = FromCached(cached.Ordered[i]);

                var byId = new Dictionary<long, ItemRecord>(ordered.Length);
                foreach (var record in ordered)
                    byId[record.Id] = record;

                var byKind = new Dictionary<ItemKind, ItemRecord[]>(cached.ByKind.Length);
                for (int i = 0; i < cached.ByKind.Length; i++)
                {
                    var cachedKind = cached.ByKind[i] ?? Array.Empty<CachedItemRecord>();
                    var kindArray = new ItemRecord[cachedKind.Length];
                    for (int j = 0; j < cachedKind.Length; j++)
                        kindArray[j] = FromCached(cachedKind[j]);
                    byKind[(ItemKind)i] = kindArray;
                }

                var cachedSnapshot = new Snapshot
                {
                    Ordered = ordered,
                    ById = byId,
                    ByKind = byKind,
                    Diagnostics = cached.Diagnostics,
                    LoadedFiles = cached.LoadedFiles.ToArray(),
                    Errors = cached.Errors.ToArray()
                };
                cachedSnapshot.Diagnostics.FromCache = true;
                cachedSnapshot.Diagnostics.LoadMs = stopwatch.Elapsed.TotalMilliseconds;

                return new ItemCatalog(config, taxonomy, cachedSnapshot, options,
                    new List<ReferenceGap>());
            }

            var builder = new CatalogBuilder(taxonomy, options);
            CatalogBuildResult result = await Task.Run(
                () => builder.Build(config, progress, cancellationToken), cancellationToken)
                .ConfigureAwait(false);

            result.Diagnostics.LoadMs = stopwatch.Elapsed.TotalMilliseconds;
            result.Diagnostics.FromCache = false;

            var snapshot = CreateSnapshot(result.Records, result.Diagnostics,
                result.LoadedFiles, result.Errors);

            try
            {
                // Convert full records to lean cached records.
                var orderedCached = new CachedItemRecord[snapshot.Ordered.Length];
                for (int i = 0; i < snapshot.Ordered.Length; i++)
                    orderedCached[i] = ToCached(snapshot.Ordered[i]);

                var byKindArray = Enumerable.Range(0, Enum.GetValues<ItemKind>().Length)
                    .Select(_ => Array.Empty<CachedItemRecord>()).ToArray();
                foreach (var kv in snapshot.ByKind)
                {
                    var kindCached = new CachedItemRecord[kv.Value.Length];
                    for (int j = 0; j < kv.Value.Length; j++)
                        kindCached[j] = ToCached(kv.Value[j]);
                    byKindArray[(int)kv.Key] = kindCached;
                }

                CatalogCache.Save(cachePath, new CachedCatalog
                {
                    Key = key,
                    Ordered = orderedCached,
                    ByKind = byKindArray,
                    Diagnostics = ForCache(result.Diagnostics),
                    LoadedFiles = result.LoadedFiles,
                    Errors = result.Errors
                });
            }
            catch
            {
                // cache write is best-effort
            }

            return new ItemCatalog(config, taxonomy, snapshot, options, new List<ReferenceGap>());
        }

        /// <summary>
        /// Builds the object that goes into the cache: identical counters, but
        /// without the sample lists. Returns a copy, because the live diagnostics
        /// are still referenced by the snapshot and must keep their samples.
        /// </summary>
        private static CatalogDiagnostics ForCache(CatalogDiagnostics source)
        {
            var copy = new CatalogDiagnostics
            {
                GeneratedUtc = source.GeneratedUtc,
                LoadMs = source.LoadMs,
                FromCache = source.FromCache,
                TotalRecords = source.TotalRecords,
                RecordsWithInternalName = source.RecordsWithInternalName,
                RecordsWithType = source.RecordsWithType,
                RecordsWithDisplayName = source.RecordsWithDisplayName,
                RecordsWithSafety = source.RecordsWithSafety,
                UnsafeRecords = source.UnsafeRecords,
                CautionRecords = source.CautionRecords,
                UnknownKindRecords = source.UnknownKindRecords,
                InferredTypeRecords = source.InferredTypeRecords,
                DuplicateIds = source.DuplicateIds,
                Conflicts = source.Conflicts,
                Notes = new List<string>(source.Notes)
            };

            foreach (var file in source.Sources)
            {
                var fileCopy = new SourceDiagnostics
                {
                    File = file.File,
                    FileName = file.FileName,
                    Enabled = file.Enabled,
                    Priority = file.Priority,
                    Format = file.Format,
                    SizeBytes = file.SizeBytes,
                    ElapsedMs = file.ElapsedMs,
                    TotalSheets = file.TotalSheets,
                    RowsRead = file.RowsRead,
                    RowsAccepted = file.RowsAccepted,
                    RowsRejected = file.RowsRejected,
                    EmptyRowsSkipped = file.EmptyRowsSkipped,
                    UniqueIds = file.UniqueIds,
                    Duplicates = file.Duplicates,
                    Conflicts = file.Conflicts,
                    NewRecords = file.NewRecords,
                    EnrichedRecords = file.EnrichedRecords,
                    Error = file.Error,
                    NeedsReview = file.NeedsReview
                };

                foreach (var sheet in file.Sheets)
                {
                    fileCopy.Sheets.Add(new SheetDiagnostics
                    {
                        Sheet = sheet.Sheet,
                        Role = sheet.Role,
                        Language = sheet.Language,
                        HeaderRow = sheet.HeaderRow,
                        RowsRead = sheet.RowsRead,
                        RowsAccepted = sheet.RowsAccepted,
                        RowsRejected = sheet.RowsRejected,
                        EmptyRowsSkipped = sheet.EmptyRowsSkipped,
                        Duplicates = sheet.Duplicates,
                        Conflicts = sheet.Conflicts,
                        Mapping = new Dictionary<string, string>(sheet.Mapping, StringComparer.OrdinalIgnoreCase),
                        Notes = new List<string>(sheet.Notes)
                    });
                }

                copy.Sources.Add(fileCopy);
            }

            return copy;
        }

        private static Snapshot CreateSnapshot(
            List<ItemRecord> records,
            CatalogDiagnostics diagnostics,
            List<string> loadedFiles,
            List<string> errors)
        {
            var ordered = records.OrderBy(r => r.Id).ToArray();
            var byId = new Dictionary<long, ItemRecord>(ordered.Length);
            var kindBuckets = new Dictionary<ItemKind, List<ItemRecord>>();

            foreach (var record in ordered)
            {
                byId[record.Id] = record;
                if (!kindBuckets.TryGetValue(record.Kind, out var bucket))
                    kindBuckets[record.Kind] = bucket = new List<ItemRecord>();
                bucket.Add(record);
            }

            return new Snapshot
            {
                Ordered = ordered,
                ById = byId,
                ByKind = kindBuckets.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()),
                Diagnostics = diagnostics,
                LoadedFiles = loadedFiles.ToArray(),
                Errors = errors.ToArray()
            };
        }

        private static CachedItemRecord ToCached(ItemRecord r) => new()
        {
            Id = r.Id,
            RawId = r.RawId,
            Name = r.Name,
            DisplayName = r.DisplayName,
            DisplayNameLanguage = r.DisplayNameLanguage,
            RawType = r.RawType,
            Kind = r.Kind,
            Subtype = r.Subtype,
            Category = r.Category,
            Notes = r.Notes,
            Safety = r.Safety,
            Placement = r.Placement,
            HasInternalName = r.HasInternalName,
            TypeWasInferred = r.TypeWasInferred,
            SearchText = r.SearchText,
            IsTestItem = r.IsTestItem
        };

        private static ItemRecord FromCached(CachedItemRecord c) => new()
        {
            Id = c.Id,
            RawId = c.RawId,
            Name = c.Name,
            DisplayName = c.DisplayName,
            DisplayNameLanguage = c.DisplayNameLanguage,
            RawType = c.RawType,
            Kind = c.Kind,
            Subtype = c.Subtype,
            Category = c.Category,
            Notes = c.Notes,
            Safety = c.Safety,
            Placement = c.Placement,
            HasInternalName = c.HasInternalName,
            TypeWasInferred = c.TypeWasInferred,
            SearchText = c.SearchText,
            IsTestItem = c.IsTestItem,
            // Diagnostic fields not in cache – defaults:
            LocalizedNames = new Dictionary<string, string>(),
            SubtypeSource = "",
            Patch = "",
            Dlc = "",
            Image = "",
            CatalogueCategory = "",
            Warnings = new List<string>(),
            Provenance = new Dictionary<string, SourceRef>(),
            TypeSource = ""
        };

        /// <summary>Rebuilds from the current config and raises <see cref="Reloaded"/>.</summary>
        public async Task ReloadAsync(
            IProgress<CatalogProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var config = _config.Clone();
            var options = _options;

            string taxonomyPath = options.TaxonomyPath
                                  ?? Path.Combine(AppContext.BaseDirectory, "taxonomy.json");
            var taxonomy = Taxonomy.Load(taxonomyPath);

            string cachePath = CatalogCache.ResolvePath(options.CacheDirectory);
            string key = CatalogCache.ComputeKey(config, taxonomy);

            // An explicit reload must re-read the files, so the cache is only refreshed afterwards.
            var stopwatch = Stopwatch.StartNew();

            var builder = new CatalogBuilder(taxonomy, options);
            CatalogBuildResult result = await Task.Run(
                () => builder.Build(config, progress, cancellationToken), cancellationToken)
                .ConfigureAwait(true);

            result.Diagnostics.LoadMs = stopwatch.Elapsed.TotalMilliseconds;

            _taxonomy = taxonomy;
            _snapshot = CreateSnapshot(result.Records, result.Diagnostics,
                result.LoadedFiles, result.Errors);
            _referenceGaps = new List<ReferenceGap>();

            try
            {
                var newSnapshot = CreateSnapshot(result.Records, result.Diagnostics,
                    result.LoadedFiles, result.Errors);

                // Convert to lean cached records.
                var orderedCached = new CachedItemRecord[newSnapshot.Ordered.Length];
                for (int i = 0; i < newSnapshot.Ordered.Length; i++)
                    orderedCached[i] = ToCached(newSnapshot.Ordered[i]);

                var byKindArray = Enumerable.Range(0, Enum.GetValues<ItemKind>().Length)
                    .Select(_ => Array.Empty<CachedItemRecord>()).ToArray();
                foreach (var kv in newSnapshot.ByKind)
                {
                    var kindCached = new CachedItemRecord[kv.Value.Length];
                    for (int j = 0; j < kv.Value.Length; j++)
                        kindCached[j] = ToCached(kv.Value[j]);
                    byKindArray[(int)kv.Key] = kindCached;
                }

                CatalogCache.Save(cachePath, new CachedCatalog
                {
                    Key = key,
                    Ordered = orderedCached,
                    ByKind = byKindArray,
                    Diagnostics = ForCache(result.Diagnostics),
                    LoadedFiles = result.LoadedFiles,
                    Errors = result.Errors
                });
            }
            catch
            {
                // best-effort
            }

            Reloaded?.Invoke(this, EventArgs.Empty);
        }
    }
}
