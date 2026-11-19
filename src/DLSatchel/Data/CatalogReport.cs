using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DDV_SaveEditor_G.Data
{
    /// <summary>
    /// Exports a human-readable and machine-readable report from catalog diagnostics.
    /// </summary>
    public static class CatalogReport
    {
        public static string ToJson(CatalogDiagnostics d, bool indented = true)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = indented,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Converters = { new JsonStringEnumConverter() }
            };
            return JsonSerializer.Serialize(Export(d), options);
        }

        public static string ToMarkdown(CatalogDiagnostics d)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# DL Satchel – Catalog Report");
            sb.AppendLine($"**Generated:** {d.GeneratedUtc:u}");
            sb.AppendLine($"**Load time:** {d.LoadMs:F1} ms  |  **From cache:** {d.FromCache}");
            sb.AppendLine();

            sb.AppendLine("## Summary");
            sb.AppendLine($"| Metric | Value |");
            sb.AppendLine($"|---|---:|");
            sb.AppendLine($"| Total records | {d.TotalRecords:N0} |");
            sb.AppendLine($"| With internal NAME | {d.RecordsWithInternalName:N0} |");
            sb.AppendLine($"| With TYPE | {d.RecordsWithType:N0} |");
            sb.AppendLine($"| With display name | {d.RecordsWithDisplayName:N0} |");
            sb.AppendLine($"| With safety | {d.RecordsWithSafety:N0} |");
            sb.AppendLine($"| Unsafe | {d.UnsafeRecords:N0} |");
            sb.AppendLine($"| Caution | {d.CautionRecords:N0} |");
            sb.AppendLine($"| Unknown kind | {d.UnknownKindRecords:N0} |");
            sb.AppendLine($"| Inferred TYPE | {d.InferredTypeRecords:N0} |");
            sb.AppendLine($"| Duplicate IDs | {d.DuplicateIds:N0} |");
            sb.AppendLine($"| Cross-source conflicts | {d.Conflicts:N0} |");
            sb.AppendLine();

            sb.AppendLine("## Per-source");
            sb.AppendLine($"| File | Pri | Format | Sheets | Rows read | Accepted | Rejected | Empty | Unique IDs | Dupes | Conflicts | New | Enriched | Time (ms) | Error |");
            sb.AppendLine($"|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");
            foreach (var s in d.Sources)
            {
                string err = string.IsNullOrEmpty(s.Error) ? "" : s.Error.Replace("|", "\\|");
                sb.AppendLine($"| {s.FileName} | {s.Priority} | {s.Format} | {s.TotalSheets} | {s.RowsRead:N0} | {s.RowsAccepted:N0} | {s.RowsRejected:N0} | {s.EmptyRowsSkipped:N0} | {s.UniqueIds:N0} | {s.Duplicates:N0} | {s.Conflicts:N0} | {s.NewRecords:N0} | {s.EnrichedRecords:N0} | {s.ElapsedMs:F1} | {err} |");
            }
            sb.AppendLine();

            if (d.DuplicateSamples.Count > 0)
            {
                sb.AppendLine("## Duplicate samples (first 20)");
                sb.AppendLine($"| ID | File | Sheet | Kept row | Skipped row |");
                sb.AppendLine($"|---:|---|---|---:|---:|");
                foreach (var dup in d.DuplicateSamples.Take(20))
                    sb.AppendLine($"| {dup.Id} | {dup.File} | {dup.Sheet} | {dup.KeptRow} | {dup.SkippedRow} |");
                sb.AppendLine();
            }

            if (d.ConflictSamples.Count > 0)
            {
                sb.AppendLine("## Conflict samples (first 20)");
                sb.AppendLine($"| ID | Field | Kept | Kept source | Dropped | Dropped source |");
                sb.AppendLine($"|---:|---|---|---|---|---|");
                foreach (var c in d.ConflictSamples.Take(20))
                    sb.AppendLine($"| {c.Id} | {c.Field} | {Escape(c.KeptValue)} | {Escape(c.KeptSource)} | {Escape(c.DroppedValue)} | {Escape(c.DroppedSource)} |");
                sb.AppendLine();
            }

            if (d.Sources.Any(s => s.Rejections.Count > 0))
            {
                sb.AppendLine("## Rejection samples (first 50)");
                sb.AppendLine($"| File | Sheet | Row | Raw ID | Reason |");
                sb.AppendLine($"|---|---|---:|---|---|");
                int count = 0;
                foreach (var s in d.Sources)
                {
                    foreach (var r in s.Rejections)
                    {
                        if (count++ >= 50) break;
                        sb.AppendLine($"| {Escape(s.FileName)} | {Escape(r.Sheet)} | {r.Row} | {Escape(r.RawId)} | {Escape(r.Reason)} |");
                    }
                }
                sb.AppendLine();
            }

            if (d.MissingReferences.Count > 0)
            {
                sb.AppendLine("## Missing references (from save)");
                sb.AppendLine($"| Kind | ID | Reason |");
                sb.AppendLine($"|---|---:|---|");
                foreach (var m in d.MissingReferences)
                    sb.AppendLine($"| {m.Kind} | {m.Id} | {Escape(m.Reason)} |");
                sb.AppendLine();
            }

            if (d.Notes.Count > 0)
            {
                sb.AppendLine("## Notes");
                foreach (var n in d.Notes) sb.AppendLine($"- {n}");
                sb.AppendLine();
            }

            return sb.ToString();

            static string Escape(string s) => s?.Replace("|", "\\|").Replace("\n", " ").Replace("\r", "") ?? "";
        }

        public static void SaveJson(CatalogDiagnostics d, string path)
            => File.WriteAllText(path, ToJson(d), Encoding.UTF8);

        public static void SaveMarkdown(CatalogDiagnostics d, string path)
            => File.WriteAllText(path, ToMarkdown(d), Encoding.UTF8);

        private static object Export(CatalogDiagnostics d) => new
        {
            generatedUtc = d.GeneratedUtc,
            loadMs = d.LoadMs,
            fromCache = d.FromCache,
            totalRecords = d.TotalRecords,
            recordsWithInternalName = d.RecordsWithInternalName,
            recordsWithType = d.RecordsWithType,
            recordsWithDisplayName = d.RecordsWithDisplayName,
            recordsWithSafety = d.RecordsWithSafety,
            unsafeRecords = d.UnsafeRecords,
            cautionRecords = d.CautionRecords,
            unknownKindRecords = d.UnknownKindRecords,
            inferredTypeRecords = d.InferredTypeRecords,
            duplicateIds = d.DuplicateIds,
            conflicts = d.Conflicts,
            sources = d.Sources.Select(s => new
            {
                s.File,
                s.FileName,
                s.Enabled,
                s.Priority,
                s.Format,
                s.SizeBytes,
                s.ElapsedMs,
                s.TotalSheets,
                s.RowsRead,
                s.RowsAccepted,
                s.RowsRejected,
                s.EmptyRowsSkipped,
                s.UniqueIds,
                s.Duplicates,
                s.Conflicts,
                s.NewRecords,
                s.EnrichedRecords,
                s.Error,
                s.NeedsReview,
                sheets = s.Sheets.Select(sh => new
                {
                    sh.Sheet,
                    sh.Role,
                    sh.Language,
                    sh.HeaderRow,
                    sh.RowsRead,
                    sh.RowsAccepted,
                    sh.RowsRejected,
                    sh.EmptyRowsSkipped,
                    sh.Duplicates,
                    sh.Conflicts,
                    sh.Mapping,
                    sh.Notes
                }),
                rejections = s.Rejections.Select(r => new { r.File, r.Sheet, r.Row, r.RawId, r.Reason })
            }),
            duplicateSamples = d.DuplicateSamples.Select(x => new { x.Id, x.File, x.Sheet, x.KeptRow, x.SkippedRow }),
            conflictSamples = d.ConflictSamples.Select(x => new { x.Id, x.Field, x.KeptValue, x.KeptSource, x.DroppedValue, x.DroppedSource }),
            missingReferences = d.MissingReferences.Select(x => new { x.Kind, x.Id, x.Reason }),
            notes = d.Notes
        };
    }
}