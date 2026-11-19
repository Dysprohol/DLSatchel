using System.Collections.Generic;
using System.Text.Json.Serialization;
using MemoryPack;

namespace DDV_SaveEditor_G.Data
{
    /// <summary>
    /// One logical item: a single id, merged from every enabled source with a
    /// clear priority. Names in many languages are attributes of this record,
    /// not separate rows.
    /// </summary>
    [MemoryPackable]
    public sealed partial class ItemRecord
    {
        public long Id { get; set; }

        /// <summary>Id exactly as it appeared in the winning source (diagnostics only).</summary>
        public string RawId { get; set; } = "";

        /// <summary>Internal NAME from the game dump ("Furniture/…", "Meal/…"). Often empty.</summary>
        public string Name { get; set; } = "";

        /// <summary>Player-facing name (localized dump column or Rubyelf "Name").</summary>
        public string DisplayName { get; set; } = "";

        public string DisplayNameLanguage { get; set; } = "en";

        /// <summary>Language code → name. The primary language is also in <see cref="DisplayName"/>.</summary>
        public Dictionary<string, string> LocalizedNames { get; set; } = new();

        /// <summary>Raw game TYPE from the dump (kept for ItemPlacement compatibility).</summary>
        public string RawType { get; set; } = "";

        public ItemKind Kind { get; set; }

        public string Subtype { get; set; } = "";

        /// <summary>"NamePrefix", "Category", "Type" or "" – how Subtype was obtained.</summary>
        public string SubtypeSource { get; set; } = "";

        public string Category { get; set; } = "";
        public string Notes { get; set; } = "";
        public string Patch { get; set; } = "";
        public string Dlc { get; set; } = "";
        public string Image { get; set; } = "";
        public string CatalogueCategory { get; set; } = "";

        public SafetyState Safety { get; set; }

        public List<string> Warnings { get; set; } = new();

        public PlacementHint Placement { get; set; }

        public bool HasInternalName { get; set; }

        public bool TypeWasInferred { get; set; }

        /// <summary>Developer test asset per taxonomy testItemRules (Feet_Test, "TEST ITEM" notes).</summary>
        public bool IsTestItem { get; set; }

        /// <summary>"Dump", "Inferred", "Category" or "".</summary>
        public string TypeSource { get; set; } = "";

        /// <summary>Field name (NAME, TYPE, DISPLAY, …) → where the winning value came from.</summary>
        public Dictionary<string, SourceRef> Provenance { get; set; } = new();

        /// <summary>Pre-folded text (display, internal name, id, subtype) used by search.</summary>
        public string SearchText { get; set; } = "";

        /// <summary>Name to show in UI: display name, then internal name, then #id.</summary>
        [JsonIgnore, MemoryPackIgnore]
        public string BestName =>
            !string.IsNullOrEmpty(DisplayName) ? DisplayName :
            !string.IsNullOrEmpty(Name) ? Name :
            $"#{Id}";

        [JsonIgnore, MemoryPackIgnore]
        public string SafetyText => Safety switch
        {
            SafetyState.Unsafe => "UNSAFE",
            SafetyState.Caution => "CAUTION",
            SafetyState.Safe => "SAFE",
            _ => "UNKNOWN"
        };
    }
}