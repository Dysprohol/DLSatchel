using System;
using System.Collections.Generic;
using MemoryPack;

namespace DDV_SaveEditor_G.Data
{
    /// <summary>
    /// Everything the Sources panel needs to explain what happened. Every number
    /// here is computed during loading – nothing is hard-coded.
    /// </summary>
    [MemoryPackable]
    public sealed partial class CatalogDiagnostics
    {
        public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;
        public double LoadMs { get; set; }
        public bool FromCache { get; set; }
        public int TotalRecords { get; set; }
        public int RecordsWithInternalName { get; set; }
        public int RecordsWithType { get; set; }
        public int RecordsWithDisplayName { get; set; }
        public int RecordsWithSafety { get; set; }
        public int UnsafeRecords { get; set; }
        public int CautionRecords { get; set; }
        public int UnknownKindRecords { get; set; }
        public int InferredTypeRecords { get; set; }
        public int DuplicateIds { get; set; }
        public int Conflicts { get; set; }
        public List<SourceDiagnostics> Sources { get; set; } = new();
        /// <summary>First N duplicates (full count is in <see cref="DuplicateIds"/>).</summary>
        public List<DuplicateEntry> DuplicateSamples { get; set; } = new();
        /// <summary>First N conflicts (full count is in <see cref="Conflicts"/>).</summary>
        public List<ConflictEntry> ConflictSamples { get; set; } = new();
        /// <summary>Ids the save refers to but no source defines (e.g. collection sets).</summary>
        public List<ReferenceGap> MissingReferences { get; set; } = new();
        public List<string> Notes { get; set; } = new();
    }

    [MemoryPackable]
    public sealed partial class SourceDiagnostics
    {
        public string File { get; set; } = "";
        public string FileName { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public int Priority { get; set; }
        public string Format { get; set; } = "";
        public long SizeBytes { get; set; }
        public double ElapsedMs { get; set; }
        public int TotalSheets { get; set; }
        public int RowsRead { get; set; }
        public int RowsAccepted { get; set; }
        public int RowsRejected { get; set; }
        public int EmptyRowsSkipped { get; set; }
        public int UniqueIds { get; set; }
        public int Duplicates { get; set; }
        public int Conflicts { get; set; }
        public int NewRecords { get; set; }
        public int EnrichedRecords { get; set; }
        /// <summary>Set when the whole file could not be read; load continues (fail-soft).</summary>
        public string? Error { get; set; }
        public bool NeedsReview { get; set; }
        public List<SheetDiagnostics> Sheets { get; set; } = new();
        public List<RejectionEntry> Rejections { get; set; } = new();
    }

    [MemoryPackable]
    public sealed partial class SheetDiagnostics
    {
        public string Sheet { get; set; } = "";
        /// <summary>"Primary", "Language" or "History".</summary>
        public string Role { get; set; } = "";
        public string? Language { get; set; }
        public int HeaderRow { get; set; }
        public int RowsRead { get; set; }
        public int RowsAccepted { get; set; }
        public int RowsRejected { get; set; }
        public int EmptyRowsSkipped { get; set; }
        public int Duplicates { get; set; }
        public int Conflicts { get; set; }
        /// <summary>Logical field → column letter ("NAME" → "B").</summary>
        public Dictionary<string, string> Mapping { get; set; } = new();
        public List<RejectionEntry> Rejections { get; set; } = new();
        public List<string> Notes { get; set; } = new();
    }

    [MemoryPackable]
    public sealed partial class RejectionEntry
    {
        public string File { get; set; } = "";
        public string Sheet { get; set; } = "";
        public int Row { get; set; }
        public string RawId { get; set; } = "";
        public string Reason { get; set; } = "";
    }

    [MemoryPackable]
    public sealed partial class DuplicateEntry
    {
        public long Id { get; set; }
        public string File { get; set; } = "";
        public string Sheet { get; set; } = "";
        public int KeptRow { get; set; }
        public int SkippedRow { get; set; }
    }

    [MemoryPackable]
    public sealed partial class ConflictEntry
    {
        public long Id { get; set; }
        public string Field { get; set; } = "";
        public string KeptValue { get; set; } = "";
        public string KeptSource { get; set; } = "";
        public string DroppedValue { get; set; } = "";
        public string DroppedSource { get; set; } = "";
    }

    [MemoryPackable]
    public sealed partial class ReferenceGap
    {
        /// <summary>What kind of reference is missing, e.g. "CollectionSet".</summary>
        public string Kind { get; set; } = "";
        public long Id { get; set; }
        public string Reason { get; set; } = "not-in-sources";
    }

    /// <summary>Per-file and per-sheet counters collected while merging.</summary>
    public sealed class ConflictSink
    {
        public int Count { get; private set; }
        public List<ConflictEntry> Samples { get; } = new();
        public void Add(ConflictEntry entry, int sampleLimit)
        {
            Count++;
            if (Samples.Count < sampleLimit) Samples.Add(entry);
        }
    }
}