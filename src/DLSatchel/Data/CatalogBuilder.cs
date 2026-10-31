using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace DDV_SaveEditor_G.Data
{
    public sealed class CatalogLoadOptions
    {
        public string? SourcesPath { get; set; }

        public string? TaxonomyPath { get; set; }

        public string? CacheDirectory { get; set; }

        public bool UseCache { get; set; } = true;

        /// <summary>When false, only the primary sheet of each file is read.</summary>
        public bool LoadLanguages { get; set; } = true;

        public int MaxSamplesPerSource { get; set; } = 150;
    }

    public sealed class CatalogProgress
    {
        public string Stage { get; init; } = "";

        public string File { get; init; } = "";

        public int FileIndex { get; init; }

        public int FileCount { get; init; }

        public string? Sheet { get; init; }

        public int SheetIndex { get; init; }

        public int SheetCount { get; init; }

        public override string ToString() =>
            Sheet == null
                ? $"{Stage}: {Path.GetFileName(File)} ({FileIndex}/{FileCount})"
                : $"{Stage}: {Path.GetFileName(File)} – {Sheet} ({SheetIndex}/{SheetCount})";
    }

    public sealed class CatalogBuildResult
    {
        public required List<ItemRecord> Records { get; init; }

        public required CatalogDiagnostics Diagnostics { get; init; }

        public required List<string> LoadedFiles { get; init; }

        public required List<string> Errors { get; init; }
    }

    /// <summary>
    /// Reads every enabled source, merges fields with an explicit priority and
    /// records provenance, duplicates, conflicts and rejected rows. No row is
    /// dropped silently: everything ends up in <see cref="CatalogDiagnostics"/>.
    /// </summary>
    public sealed class CatalogBuilder
    {
        private const int ConflictSampleLimit = 200;

        private readonly Taxonomy _taxonomy;
        private readonly CatalogLoadOptions _options;

        public CatalogBuilder(Taxonomy taxonomy, CatalogLoadOptions options)
        {
            _taxonomy = taxonomy;
            _options = options;
        }

        /* ================================================================= */

        public CatalogBuildResult Build(
            SourcesConfig config,
            IProgress<CatalogProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var diagnostics = new CatalogDiagnostics();
            var loadedFiles = new List<string>();
            var errors = new List<string>();

            var sources = config.Sources
                .Where(s => s.Enabled)
                .Where(s => WorkbookSourceFactory.IsSupported(s.Path))
                .OrderBy(s => s.TreatAsHistory ? 1 : 0)
                .ThenBy(s => s.Priority)
                .ThenBy(s => Path.GetFileName(s.Path), StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Disabled / unsupported sources still get a diagnostic entry so the panel can explain them.
            foreach (var disabled in config.Sources.Where(s => !s.Enabled))
            {
                diagnostics.Sources.Add(new SourceDiagnostics
                {
                    File = disabled.Path,
                    FileName = Path.GetFileName(disabled.Path),
                    Enabled = false,
                    Priority = disabled.Priority,
                    Format = Path.GetExtension(disabled.Path).TrimStart('.').ToLowerInvariant()
                });
            }

            var parsed = new List<SourceParseResult>(sources.Count);
            for (int i = 0; i < sources.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = sources[i];
                var result = ParseSource(source, i, sources.Count, progress, cancellationToken);
                parsed.Add(result);
                if (result.Diagnostics.Error == null) loadedFiles.Add(source.Path);
                else errors.Add($"{result.Diagnostics.FileName}: {result.Diagnostics.Error}");
                diagnostics.Sources.Add(result.Diagnostics);
            }

            var merged = Merge(parsed, diagnostics, progress, cancellationToken);

            InferMissingTypes(merged, diagnostics);
            var records = Materialize(merged, diagnostics, cancellationToken);

            diagnostics.TotalRecords = records.Count;
            diagnostics.RecordsWithInternalName = records.Count(r => r.HasInternalName);
            diagnostics.RecordsWithType = records.Count(r => !string.IsNullOrEmpty(r.RawType));
            diagnostics.RecordsWithDisplayName = records.Count(r => !string.IsNullOrEmpty(r.DisplayName));
            diagnostics.RecordsWithSafety = records.Count(r => r.Safety != SafetyState.Unknown);
            diagnostics.UnsafeRecords = records.Count(r => r.Safety == SafetyState.Unsafe);
            diagnostics.CautionRecords = records.Count(r => r.Safety == SafetyState.Caution);
            diagnostics.UnknownKindRecords = records.Count(r => r.Kind == ItemKind.Unknown);
            diagnostics.InferredTypeRecords = records.Count(r => r.TypeWasInferred);
            diagnostics.DuplicateIds = diagnostics.Sources.Sum(s => s.Duplicates);
            // Merge() already counted cross-source conflicts; add the in-file ones.
            diagnostics.Conflicts += diagnostics.Sources.Sum(s => s.Conflicts);

            progress?.Report(new CatalogProgress { Stage = "Done", FileCount = sources.Count });

            return new CatalogBuildResult
            {
                Records = records,
                Diagnostics = diagnostics,
                LoadedFiles = loadedFiles,
                Errors = errors
            };
        }

        /* =================================================================
         *  One file
         * ================================================================= */

        private sealed class SourceParseResult
        {
            public required CatalogSourceConfig Config { get; init; }
            public required SourceDiagnostics Diagnostics { get; init; }
            public Dictionary<long, SourceRecord> Records { get; } = new();
        }

        private sealed class SheetPlan
        {
            public required string Name { get; init; }
            public int Order { get; init; }
            public HeaderResult? Header { get; init; }
            public string Role { get; set; } = "Skipped";
            public string? Language { get; set; }
            public SheetMappingOverride? Override { get; init; }
            public List<string> Notes { get; } = new();
        }

        private sealed class SourceRecord
        {
            public long Id;
            public string RawId = "";
            public readonly Dictionary<string, string> Fields = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, SourceRef> Provenance = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> Localized = new(StringComparer.OrdinalIgnoreCase);
            public string DisplayLanguage = "";
            public readonly HashSet<string> Warnings = new(StringComparer.OrdinalIgnoreCase);
        }

        private SourceParseResult ParseSource(
            CatalogSourceConfig config,
            int index,
            int total,
            IProgress<CatalogProgress>? progress,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var diagnostics = new SourceDiagnostics
            {
                File = config.Path,
                FileName = Path.GetFileName(config.Path),
                Enabled = true,
                Priority = config.Priority,
                Format = Path.GetExtension(config.Path).TrimStart('.').ToLowerInvariant()
            };

            var result = new SourceParseResult { Config = config, Diagnostics = diagnostics };

            if (!File.Exists(config.Path))
            {
                diagnostics.Error = "file not found";
                diagnostics.ElapsedMs = stopwatch.Elapsed.TotalMilliseconds;
                return result;
            }

            try
            {
                diagnostics.SizeBytes = new FileInfo(config.Path).Length;

                using var workbook = WorkbookSourceFactory.Open(config.Path);
                diagnostics.TotalSheets = workbook.SheetNames.Count;

                // Phase 1: peek at the first rows of every sheet and classify it.
                var plans = new List<SheetPlan>();
                for (int sheetIndex = 0; sheetIndex < workbook.SheetNames.Count; sheetIndex++)
                {
                    string sheetName = workbook.SheetNames[sheetIndex];
                    config.MappingOverrides.TryGetValue(sheetName, out var sheetOverride);

                    RawSheet head = workbook.ReadSheet(sheetName, HeaderDetector.DefaultScanRows + 5);
                    var header = HeaderDetector.Detect(head, sheetOverride);

                    var plan = new SheetPlan
                    {
                        Name = sheetName,
                        Order = sheetIndex,
                        Header = header,
                        Override = sheetOverride
                    };

                    if (header == null)
                    {
                        plan.Role = "Skipped";
                        plan.Notes.Add("No id column found in the first 25 rows – not an item list.");
                    }
                    else
                    {
                        ClassifySheet(plan, header, workbook.SheetNames.Count);
                    }
                    plans.Add(plan);
                }

                var primary = ChoosePrimary(plans);
                if (primary == null)
                {
                    diagnostics.Error = "no sheet with an id column";
                    diagnostics.Sheets.AddRange(ToSheetDiagnostics(plans, config));
                    diagnostics.ElapsedMs = stopwatch.Elapsed.TotalMilliseconds;
                    return result;
                }

                // Phase 2: process primary first, then history (fill gaps), then languages.
                var ordered = plans
                    .Where(p => p.Header != null && p.Role != "Skipped")
                    .Where(p => _options.LoadLanguages || p.Role != "Language")
                    .OrderBy(p => ReferenceEquals(p, primary) ? 0 : p.Role == "History" ? 1 : 2)
                    .ThenBy(p => p.Order)
                    .ToList();

                var sheetDiagnostics = new List<SheetDiagnostics>();
                int sheetCounter = 0;

                foreach (var plan in ordered)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var diag = new SheetDiagnostics
                    {
                        Sheet = plan.Name,
                        Role = ReferenceEquals(plan, primary) ? "Primary" : plan.Role,
                        Language = plan.Language,
                        HeaderRow = plan.Header!.HeaderRow,
                        Mapping = plan.Header.Mapping
                    };
                    diag.Notes.AddRange(plan.Notes);
                    diag.Notes.AddRange(plan.Header.Notes);

                    progress?.Report(new CatalogProgress
                    {
                        Stage = "Reading",
                        File = config.Path,
                        FileIndex = index + 1,
                        FileCount = total,
                        Sheet = plan.Name,
                        SheetIndex = ++sheetCounter,
                        SheetCount = ordered.Count
                    });

                    RawSheet sheet = workbook.ReadSheet(plan.Name);
                    ProcessSheet(sheet, plan, plan.Header!, result, diag, primary, cancellationToken);
                    sheetDiagnostics.Add(diag);
                }

                diagnostics.Sheets = sheetDiagnostics;
                diagnostics.RowsRead = sheetDiagnostics.Sum(s => s.RowsRead);
                diagnostics.RowsAccepted = sheetDiagnostics.Sum(s => s.RowsAccepted);
                diagnostics.RowsRejected = sheetDiagnostics.Sum(s => s.RowsRejected);
                diagnostics.EmptyRowsSkipped = sheetDiagnostics.Sum(s => s.EmptyRowsSkipped);
                diagnostics.Duplicates = sheetDiagnostics.Sum(s => s.Duplicates);
                diagnostics.Conflicts = sheetDiagnostics.Sum(s => s.Conflicts);
                diagnostics.UniqueIds = result.Records.Count;
                diagnostics.NeedsReview = plans.Any(p => p.Header?.NeedsIdConfirmation == true);
                diagnostics.Rejections = sheetDiagnostics
                    .SelectMany(s => s.Rejections)
                    .Take(_options.MaxSamplesPerSource)
                    .ToList();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                diagnostics.Error = ex.Message;
            }
            finally
            {
                diagnostics.ElapsedMs = stopwatch.Elapsed.TotalMilliseconds;
            }

            return result;
        }

        private static void ClassifySheet(SheetPlan plan, HeaderResult header, int sheetCount)
        {
            if (plan.Override?.Role is { Length: > 0 } forced)
            {
                plan.Role = forced;
                plan.Language = plan.Override.Language;
                plan.Notes.Add($"Role forced by mapping override: {forced}.");
                return;
            }

            if (IsHistoryName(plan.Name))
            {
                plan.Role = "History";
                plan.Notes.Add("Backup / pre-update sheet – may only fill missing values.");
                return;
            }

            string? language = LanguageFromSheetName(plan.Name);
            if (language != null)
            {
                plan.Role = "Language";
                plan.Language = language;
                return;
            }

            // A sheet without a recognizable name but with a localized display
            // column is a language sheet. TYPE alone is not enough: a plain data
            // sheet named e.g. "Data" also has TYPE and must stay primary.
            if (header.Columns.ContainsKey(CatalogField.Display) && !header.NameIsDisplay)
            {
                plan.Role = "Language";
                plan.Language = NormalizeLanguageCode(plan.Name);
                plan.Notes.Add("Language sheet detected by LOCALIZED_DISPLAY_NAME column.");
                return;
            }

            plan.Role = "Primary";
        }

        private static SheetPlan? ChoosePrimary(List<SheetPlan> plans)
        {
            var usable = plans.Where(p => p.Header != null && p.Role != "Skipped").ToList();
            if (usable.Count == 0) return null;

            // 1) a real primary sheet
            var primary = usable
                .Where(p => p.Role == "Primary")
                .OrderBy(p => p.Order)
                .FirstOrDefault();
            if (primary != null) return primary;

            // 2) English, if the file consists of language sheets only
            var english = usable
                .Where(p => string.Equals(p.Language, "en", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.Order)
                .FirstOrDefault();
            if (english != null) return english;

            // 3) first usable sheet
            return usable.OrderBy(p => p.Order).FirstOrDefault();
        }

        /* ================================================================= */

        private void ProcessSheet(
            RawSheet sheet,
            SheetPlan plan,
            HeaderResult header,
            SourceParseResult source,
            SheetDiagnostics diag,
            SheetPlan primary,
            CancellationToken cancellationToken)
        {
            bool isPrimary = ReferenceEquals(plan, primary);
            bool isHistory = plan.Role == "History" && !isPrimary;
            bool isLanguage = plan.Role == "Language" && !isPrimary;
            bool primaryIsLanguage = primary.Role == "Language";
            string language = plan.Language ?? "en";

            var seenIds = new HashSet<long>();
            var duplicateIds = new HashSet<long>();
            var conflicts = new ConflictSink();
            int extraSheetRows = 0;

            int nameColumn = Column(header, CatalogField.Name);
            int displayColumn = Column(header, CatalogField.Display);
            int idColumn = Column(header, CatalogField.Id);
            int hexColumn = Column(header, CatalogField.Hex);
            int typeColumn = Column(header, CatalogField.Type);
            int categoryColumn = Column(header, CatalogField.Category);
            int subtypeColumn = Column(header, CatalogField.Subtype);
            int notesColumn = Column(header, CatalogField.Notes);
            int patchColumn = Column(header, CatalogField.Patch);
            int dlcColumn = Column(header, CatalogField.Dlc);
            int imageColumn = Column(header, CatalogField.Image);
            int catalogueColumn = Column(header, CatalogField.CatalogueCategory);

            bool nameIsDisplay = header.NameIsDisplay;

            for (int rowNumber = header.HeaderRow + 1; rowNumber <= sheet.RowCount; rowNumber++)
            {
                if ((rowNumber & 0x3FF) == 0) cancellationToken.ThrowIfCancellationRequested();

                var row = sheet.GetRow(rowNumber);
                if (RawSheet.IsEmpty(row))
                {
                    diag.EmptyRowsSkipped++;
                    continue;
                }

                // rows above the detected header that are not empty are counted as read but skipped
                diag.RowsRead++;

                string rawId = Cell(row, idColumn);
                long id;
                string? reason = "empty-id";

                if (rawId.Length == 0 && hexColumn >= 0)
                {
                    string rawHex = Cell(row, hexColumn);
                    if (rawHex.Length > 0 &&
                        IdNormalizer.TryNormalizeHex(rawHex, out id, out _, out reason))
                    {
                        rawId = rawHex;
                    }
                    else
                    {
                        Reject(diag, sheet, rowNumber, rawHex, reason ?? "empty-id");
                        continue;
                    }
                }
                else if (!IdNormalizer.TryNormalize(rawId, out id, out _, out reason))
                {
                    Reject(diag, sheet, rowNumber, rawId, reason ?? "invalid-id");
                    continue;
                }

                if (!seenIds.Add(id))
                {
                    duplicateIds.Add(id);
                    if (source.Records.TryGetValue(id, out var seen))
                        seen.Warnings.Add("Duplicate id inside the same sheet – first row kept.");
                    continue;
                }

                // Language and backup/history sheets never create records: they only
                // enrich ids the primary sheet already contributed. Otherwise an old
                // "Before Update" snapshot would resurrect items removed from the game.
                if ((isLanguage || isHistory) && !source.Records.ContainsKey(id))
                {
                    extraSheetRows++;
                    continue;
                }

                if (!source.Records.TryGetValue(id, out var record))
                {
                    record = new SourceRecord { Id = id, RawId = rawId };
                    source.Records[id] = record;
                }

                var sourceRef = new SourceRef
                {
                    File = sheet.FilePath,
                    Sheet = sheet.SheetName,
                    Row = rowNumber,
                    Column = idColumn + 1
                };

                /* ---- display name / localized name -------------------------- */
                string display = Cell(row, displayColumn);
                if (display.Length > 0) display = display.Replace('·', '-');

                if (isLanguage)
                {
                    if (display.Length > 0)
                    {
                        if (!record.Localized.TryGetValue(language, out string? existing))
                        {
                            record.Localized[language] = display;
                            record.Provenance[$"DISPLAY[{language}]"] = new SourceRef
                            {
                                File = sheet.FilePath,
                                Sheet = sheet.SheetName,
                                Row = rowNumber,
                                Column = displayColumn + 1
                            };
                        }
                        else if (!string.Equals(existing, display, StringComparison.Ordinal))
                        {
                            conflicts.Add(new ConflictEntry
                            {
                                Id = id,
                                Field = $"DISPLAY[{language}]",
                                KeptValue = existing,
                                KeptSource = record.Provenance.TryGetValue($"DISPLAY[{language}]", out var keptRef)
                                    ? keptRef.ToString()
                                    : "",
                                DroppedValue = display,
                                DroppedSource = sourceRef.ToString()
                            }, _options.MaxSamplesPerSource);
                        }
                    }
                    continue;   // language sheets only enrich names
                }

                string name = Cell(row, nameColumn);
                if (nameIsDisplay && name.Length > 0 && display.Length == 0)
                {
                    display = name;
                    name = "";
                }

                if (display.Length > 0)
                {
                    string languageForDisplay = isPrimary && primaryIsLanguage ? language : "en";
                    if (SetField(record, CatalogField.Display, display, sourceRef, isHistory,
                            id, conflicts, out bool added) && added)
                    {
                        record.DisplayLanguage = languageForDisplay;
                    }
                    if (!record.Localized.ContainsKey(languageForDisplay))
                        record.Localized[languageForDisplay] = display;
                }

                if (name.Length > 0)
                    SetField(record, CatalogField.Name, name, sourceRef, isHistory, id, conflicts, out _);

                MergeSimple(CatalogField.Type, typeColumn);
                MergeSimple(CatalogField.Category, categoryColumn);
                MergeSimple(CatalogField.Subtype, subtypeColumn);
                MergeSimple(CatalogField.Notes, notesColumn);
                MergeSimple(CatalogField.Patch, patchColumn);
                MergeSimple(CatalogField.Dlc, dlcColumn);
                MergeSimple(CatalogField.Image, imageColumn);
                MergeSimple(CatalogField.CatalogueCategory, catalogueColumn);

                void MergeSimple(string field, int column)
                {
                    if (column < 0) return;
                    string value = Cell(row, column);
                    if (value.Length == 0) return;
                    SetField(record, field, value, sourceRef, isHistory, id, conflicts, out _);
                }
            }

            diag.RowsAccepted = seenIds.Count;
            diag.Duplicates = duplicateIds.Count;
            diag.Conflicts = conflicts.Count;
            if (extraSheetRows > 0)
                diag.Notes.Add($"{extraSheetRows} rows in language/backup sheets have no row in the primary sheet and were ignored.");
            if (conflicts.Samples.Count > 0)
                diag.Notes.Add($"Conflicts recorded inside this sheet: {conflicts.Count}.");
        }

        /// <summary>Sets a field unless it already has a value; records conflicts instead of overwriting.</summary>
        private static bool SetField(
            SourceRecord record,
            string field,
            string value,
            SourceRef source,
            bool fillOnly,
            long id,
            ConflictSink conflicts,
            out bool added)
        {
            added = false;
            value = value.Trim();
            if (value.Length == 0) return false;

            if (!record.Fields.TryGetValue(field, out string? existing) || existing.Length == 0)
            {
                record.Fields[field] = value;
                record.Provenance[field] = source;
                added = true;
                return true;
            }

            if (string.Equals(existing, value, StringComparison.Ordinal)) return false;

            if (fillOnly) return false;

            conflicts.Add(new ConflictEntry
            {
                Id = id,
                Field = field,
                KeptValue = existing,
                KeptSource = record.Provenance.TryGetValue(field, out var keptSource) ? keptSource.ToString() : "",
                DroppedValue = value,
                DroppedSource = source.ToString()
            }, ConflictSampleLimit);
            return false;
        }

        private static void Reject(SheetDiagnostics diag, RawSheet sheet, int row, string rawId, string reason)
        {
            diag.RowsRejected++;
            if (diag.Rejections.Count < 60)
            {
                diag.Rejections.Add(new RejectionEntry
                {
                    File = sheet.FilePath,
                    Sheet = sheet.SheetName,
                    Row = row,
                    RawId = rawId.Length > 40 ? rawId[..40] : rawId,
                    Reason = reason
                });
            }
        }

        private static int Column(HeaderResult header, string field)
            => header.Columns.TryGetValue(field, out int index) ? index : -1;

        private static string Cell(string?[] row, int column)
            => column >= 0 && column < row.Length ? TextNormalizer.CleanCell(row[column]) : "";

        /* =================================================================
         *  Cross-source merge
         * ================================================================= */

        private List<SourceRecord> Merge(
            List<SourceParseResult> parsed,
            CatalogDiagnostics diagnostics,
            IProgress<CatalogProgress>? progress,
            CancellationToken cancellationToken)
        {
            var merged = new Dictionary<long, SourceRecord>();
            var conflicts = new ConflictSink();

            foreach (var source in parsed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new CatalogProgress
                {
                    Stage = "Merging",
                    File = source.Config.Path,
                    FileCount = parsed.Count
                });

                int added = 0, enriched = 0;

                foreach (var record in source.Records.Values)
                {
                    if (!merged.TryGetValue(record.Id, out var target))
                    {
                        target = new SourceRecord
                        {
                            Id = record.Id,
                            RawId = record.RawId,
                            DisplayLanguage = record.DisplayLanguage
                        };
                        CopyInto(record, target);
                        merged[record.Id] = target;
                        added++;
                        continue;
                    }

                    enriched++;
                    foreach (var kv in record.Fields)
                        MergeValue(target, kv.Key, kv.Value, record.Provenance, record.Id, conflicts);

                    foreach (var kv in record.Localized)
                    {
                        string field = $"DISPLAY[{kv.Key}]";
                        if (!target.Localized.ContainsKey(kv.Key))
                        {
                            target.Localized[kv.Key] = kv.Value;
                            if (record.Provenance.TryGetValue(field, out var reference))
                                target.Provenance[field] = reference;
                        }
                        else if (!string.Equals(target.Localized[kv.Key], kv.Value, StringComparison.Ordinal))
                        {
                            conflicts.Add(new ConflictEntry
                            {
                                Id = record.Id,
                                Field = field,
                                KeptValue = target.Localized[kv.Key],
                                KeptSource = target.Provenance.TryGetValue(field, out var kept) ? kept.ToString() : "",
                                DroppedValue = kv.Value,
                                DroppedSource = record.Provenance.TryGetValue(field, out var dropped) ? dropped.ToString() : ""
                            }, int.MaxValue);
                        }
                    }

                    foreach (string warning in record.Warnings) target.Warnings.Add(warning);
                }

                source.Diagnostics.NewRecords = added;
                source.Diagnostics.EnrichedRecords = enriched;
                source.Diagnostics.Conflicts += CountNewConflicts(conflicts);
            }

            diagnostics.Conflicts = conflicts.Count;
            diagnostics.ConflictSamples = conflicts.Samples.Take(200).ToList();
            return merged.Values.ToList();
        }

        private int _lastConflictCount;

        private int CountNewConflicts(ConflictSink sink)
        {
            int delta = sink.Count - _lastConflictCount;
            _lastConflictCount = sink.Count;
            return delta;
        }

        private static void CopyInto(SourceRecord from, SourceRecord to)
        {
            foreach (var kv in from.Fields) to.Fields[kv.Key] = kv.Value;
            foreach (var kv in from.Provenance) to.Provenance[kv.Key] = kv.Value;
            foreach (var kv in from.Localized) to.Localized[kv.Key] = kv.Value;
            foreach (string warning in from.Warnings) to.Warnings.Add(warning);
        }

        private static void MergeValue(
            SourceRecord target,
            string field,
            string value,
            Dictionary<string, SourceRef> provenance,
            long id,
            ConflictSink conflicts)
        {
            if (value.Length == 0) return;

            if (!target.Fields.TryGetValue(field, out string? existing) || existing.Length == 0)
            {
                target.Fields[field] = value;
                if (provenance.TryGetValue(field, out var reference))
                    target.Provenance[field] = reference;
                return;
            }

            if (string.Equals(existing, value, StringComparison.Ordinal)) return;

            conflicts.Add(new ConflictEntry
            {
                Id = id,
                Field = field,
                KeptValue = existing,
                KeptSource = target.Provenance.TryGetValue(field, out var kept) ? kept.ToString() : "",
                DroppedValue = value,
                DroppedSource = provenance.TryGetValue(field, out var dropped) ? dropped.ToString() : ""
            }, int.MaxValue);
        }

        /* =================================================================
         *  Type inference + taxonomy
         * ================================================================= */

        private void InferMissingTypes(List<SourceRecord> records, CatalogDiagnostics diagnostics)
        {
            var learned = records
                .Where(r => r.Fields.TryGetValue(CatalogField.Type, out string? type) && type.Length > 0)
                .Where(r => r.Fields.TryGetValue(CatalogField.Category, out string? category) && category.Length > 0)
                .GroupBy(r => r.Fields[CatalogField.Category])
                .Select(g => new
                {
                    Category = g.Key,
                    Best = g.GroupBy(r => r.Fields[CatalogField.Type])
                            .OrderByDescending(t => t.Count())
                            .First(),
                    Total = g.Count()
                })
                .Where(x => x.Best.Count() >= 0.8 * x.Total)
                .ToDictionary(x => x.Category, x => x.Best.Key, StringComparer.OrdinalIgnoreCase);

            int inferred = 0;
            foreach (var record in records)
            {
                if (record.Fields.TryGetValue(CatalogField.Type, out string? type) && type.Length > 0)
                    continue;

                if (record.Fields.TryGetValue(CatalogField.Category, out string? category) &&
                    category.Length > 0 &&
                    learned.TryGetValue(category, out string? learnedType))
                {
                    record.Fields[CatalogField.Type] = learnedType;
                    record.Provenance[CatalogField.Type] = new SourceRef
                    {
                        File = "(learned)",
                        Sheet = "Category→TYPE",
                        Row = 0,
                        Column = 0
                    };
                    inferred++;
                }
            }

            diagnostics.Notes.Add(
                $"TYPE inferred for {inferred} records from Category (majority rule, ≥80% agreement).");
        }

        private List<ItemRecord> Materialize(
            List<SourceRecord> merged,
            CatalogDiagnostics diagnostics,
            CancellationToken cancellationToken)
        {
            var records = new List<ItemRecord>(merged.Count);

            foreach (var source in merged)
            {
                if ((records.Count & 0x3FF) == 0) cancellationToken.ThrowIfCancellationRequested();

                string name = Get(source, CatalogField.Name);
                string display = Get(source, CatalogField.Display);
                string type = Get(source, CatalogField.Type);
                string category = Get(source, CatalogField.Category);
                string notes = Get(source, CatalogField.Notes);
                string catalogue = Get(source, CatalogField.CatalogueCategory);
                string patch = Get(source, CatalogField.Patch);
                string dlc = Get(source, CatalogField.Dlc);
                string image = Get(source, CatalogField.Image);

                bool typeInferred = source.Provenance.TryGetValue(CatalogField.Type, out var typeRef) &&
                                    typeRef.File == "(learned)";
                string prefix = _taxonomy.GetNamePrefix(name);

                ItemKind kind = _taxonomy.ResolveKind(type, name, category);
                PlacementHint placement = _taxonomy.ResolvePlacement(type, name, kind);
                // Safety markers come from the source's own metadata; the display
                // name is included because Rubyelf puts "Don't use" there too.
                SafetyState safety = _taxonomy.ResolveSafety(name, display, category, notes, catalogue, type);
                if (safety == SafetyState.Safe)
                {
                    // Missing/inferred game metadata lowers classification confidence,
                    // but does not make an otherwise ordinary item gameplay-risky.
                    // Keep that uncertainty separate from explicit CAUTION markers
                    // (quest/event/house/do-not-craft notes in taxonomy).
                    if (typeInferred || (type.Length == 0 && category.Length > 0) ||
                        (name.Length == 0 && type.Length > 0) || IsKnownUnsupportedType(type, prefix) ||
                        (kind == ItemKind.Unknown && type.Length > 0))
                        safety = SafetyState.Unknown;
                }

                string subtype;
                string subtypeSource;
                if (name.Length > 0 && prefix.Length > 0 && !string.Equals(prefix, name, StringComparison.Ordinal))
                {
                    subtype = prefix;
                    subtypeSource = "NamePrefix";
                }
                else if (category.Length > 0)
                {
                    subtype = category;
                    subtypeSource = "Category";
                }
                else
                {
                    subtype = "";
                    subtypeSource = "";
                }

                var warnings = new List<string>(source.Warnings);
                if (name.Length == 0)
                    warnings.Add("No internal NAME – only the display name is known, so the game's storage type cannot be checked.");
                if (typeInferred)
                    warnings.Add($"TYPE inferred from Category '{category}'.");
                if (type.Length > 0 && kind == ItemKind.Unknown)
                    warnings.Add($"TYPE '{type}' is not a known inventory item.");
                if (type.Length == 0 && category.Length == 0)
                    warnings.Add("Neither TYPE nor Category is known.");
                if (safety == SafetyState.Unsafe)
                    warnings.Add($"Marked UNSAFE by the source: {FirstNonEmpty(notes, category, catalogue)}");
                if (safety == SafetyState.Caution)
                    warnings.Add($"Marked CAUTION by the source: {FirstNonEmpty(notes, category, catalogue)}");

                string searchText = TextNormalizer.FoldForSearch(
                    $"{display} {name} {source.Id} {subtype} {category}");

                records.Add(new ItemRecord
                {
                    Id = source.Id,
                    RawId = source.RawId,
                    Name = name,
                    DisplayName = display,
                    DisplayNameLanguage = source.DisplayLanguage.Length == 0 ? "en" : source.DisplayLanguage,
                    LocalizedNames = new Dictionary<string, string>(source.Localized, StringComparer.OrdinalIgnoreCase),
                    RawType = type,
                    Kind = kind,
                    Subtype = subtype,
                    SubtypeSource = subtypeSource,
                    Category = category,
                    Notes = notes,
                    Patch = patch,
                    Dlc = dlc,
                    Image = image,
                    CatalogueCategory = catalogue,
                    Safety = safety,
                    Warnings = warnings,
                    Placement = placement,
                    HasInternalName = name.Length > 0,
                    TypeWasInferred = typeInferred,
                    IsTestItem = _taxonomy.IsTestItem(name, display, category, notes),
                    TypeSource = typeInferred ? "Inferred" : type.Length > 0 ? "Dump" : category.Length > 0 ? "Category" : "",
                    Provenance = new Dictionary<string, SourceRef>(source.Provenance, StringComparer.OrdinalIgnoreCase),
                    SearchText = searchText
                });
            }

            records.Sort((a, b) => a.Id.CompareTo(b.Id));
            return records;
        }

        private static string Get(SourceRecord record, string field)
            => record.Fields.TryGetValue(field, out string? value) ? value : "";

        private static string FirstNonEmpty(params string?[] values)
            => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

        /* =================================================================
         *  Sheet classification helpers
         * ================================================================= */

        private static bool IsHistoryName(string name)
        {
            string folded = TextNormalizer.FoldForSearch(name);
            return folded.Contains("backup") || folded.Contains("before update") || folded.Contains("historia");
        }

        private bool IsKnownUnsupportedType(string type, string namePrefix)
        {
            if (_taxonomy.NonInventoryTypes.Any(t =>
                    string.Equals(t, type, StringComparison.OrdinalIgnoreCase)))
                return true;

            if (type is "MemoryShard" or "Upgrade" or "Set" or "CollectionSet")
                return true;

            if (type == "ActivityData" && namePrefix is not "CookingRecipe" and not "CraftingRecipe")
                return true;

            if (type == "Companion" && namePrefix is not "Pet" and not "Critter")
                return true;

            return false;
        }

        private static readonly Dictionary<string, string> KnownLanguages = new(StringComparer.OrdinalIgnoreCase)
        {
            ["English"] = "en",
            ["French"] = "fr",
            ["German"] = "de",
            ["Spanish"] = "es",
            ["Italian"] = "it",
            ["Japanese"] = "ja",
            ["Chinese"] = "zh",
            ["PortugueseBR"] = "pt-BR",
            ["Portuguese"] = "pt",
            ["Polish"] = "pl",
            ["PL"] = "pl",
            ["Russian"] = "ru",
            ["Korean"] = "ko",
            ["Dutch"] = "nl",
            ["Turkish"] = "tr"
        };

        private static string? LanguageFromSheetName(string name)
        {
            string trimmed = name.Trim();
            if (KnownLanguages.TryGetValue(trimmed, out string? language)) return language;

            // Only longer names may be matched as substrings; short codes like "PL"
            // would otherwise match ordinary words ("Sample", "Display").
            foreach (var kv in KnownLanguages)
                if (kv.Key.Length >= 4 &&
                    name.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;

            return null;
        }

        private static string NormalizeLanguageCode(string name)
        {
            string folded = TextNormalizer.FoldForSearch(name);
            if (folded.Length == 0) return "und";
            int separator = folded.IndexOfAny(new[] { ' ', '_', '-' });
            string code = separator > 0 ? folded[..separator] : folded;
            return code.Length <= 8 ? code : code[..8];
        }

        private static List<SheetDiagnostics> ToSheetDiagnostics(
            List<SheetPlan> plans, CatalogSourceConfig config)
        {
            var result = new List<SheetDiagnostics>();
            foreach (var plan in plans)
            {
                var diag = new SheetDiagnostics
                {
                    Sheet = plan.Name,
                    Role = plan.Role,
                    Language = plan.Language,
                    HeaderRow = plan.Header?.HeaderRow ?? 0,
                    Mapping = plan.Header?.Mapping ?? new Dictionary<string, string>()
                };
                diag.Notes.AddRange(plan.Notes);
                if (plan.Header != null) diag.Notes.AddRange(plan.Header.Notes);
                result.Add(diag);
            }
            return result;
        }
    }
}
