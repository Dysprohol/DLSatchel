using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DDV_SaveEditor_G.Data
{
    /// <summary>
    /// Schema-driven taxonomy. All decisions about kind, subtype, placement and
    /// safety come from taxonomy.json, so adding a new list does not require code.
    /// </summary>
    public sealed class Taxonomy
    {
        public const string EmbeddedResourceName = "DDV_SaveEditor_G.Data.taxonomy.json";

        [JsonPropertyName("_comment")]
        public string Comment { get; set; } = "";

        public int SchemaVersion { get; set; } = 1;

        /// <summary>Character(s) that separate the subtype from the rest of the internal NAME.</summary>
        public List<string> SubtypeSeparators { get; set; } = new() { "/", "!" };

        public Dictionary<string, ItemKind> KindByType { get; set; } = new();

        /// <summary>Category (Rubyelf) → kind, used when TYPE is missing.</summary>
        public Dictionary<string, ItemKind> KindByCategory { get; set; } = new();

        /// <summary>Ordered overrides; first match wins.</summary>
        public List<KindRule> KindRules { get; set; } = new();

        public Dictionary<ItemKind, PlacementHint> PlacementByKind { get; set; } = new();

        /// <summary>Ordered; first matching state wins (Unsafe before Caution).</summary>
        public List<SafetyRule> SafetyRules { get; set; } = new();

        /// <summary>Game types that are not inventory items at all.</summary>
        public List<string> NonInventoryTypes { get; set; } = new();

        /// <summary>Developer test assets (e.g. CloFeet/Feet_Test, Rubyelf note "TEST ITEM"); any match flags the item.</summary>
        public List<TestItemRule> TestItemRules { get; set; } = new();

        [JsonIgnore]
        public string SourcePath { get; private set; } = "(embedded)";

        [JsonIgnore]
        public List<string> LoadNotes { get; } = new();

        /// <summary>SHA-256 of the JSON text this taxonomy was read from (file or embedded).</summary>
        [JsonIgnore]
        public string ContentHash { get; private set; } = "";

        /* ================================================================= */

        /// <summary>Loads taxonomy.json; falls back to the embedded copy on any error.</summary>
        public static Taxonomy Load(string? path = null)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path);
                    var parsed = JsonSerializer.Deserialize<Taxonomy>(json, JsonOptions);
                    if (parsed != null)
                    {
                        parsed.Normalize();
                        parsed.SourcePath = path!;
                        parsed.ContentHash = Hash(json);
                        return parsed;
                    }
                }
                catch (Exception ex)
                {
                    var fallback = LoadEmbedded();
                    fallback.LoadNotes.Add($"taxonomy.json unreadable ({ex.Message}); used embedded copy.");
                    return fallback;
                }
            }
            return LoadEmbedded();
        }

        private static Taxonomy LoadEmbedded()
        {
            var assembly = Assembly.GetExecutingAssembly();
            using Stream? stream = assembly.GetManifestResourceStream(EmbeddedResourceName);
            if (stream == null)
                throw new InvalidOperationException(
                    $"Embedded taxonomy resource '{EmbeddedResourceName}' is missing.");

            using var reader = new StreamReader(stream);
            string json = reader.ReadToEnd();
            var parsed = JsonSerializer.Deserialize<Taxonomy>(json, JsonOptions)
                         ?? throw new InvalidDataException("Embedded taxonomy is empty.");
            parsed.Normalize();
            parsed.ContentHash = Hash(json);
            return parsed;
        }

        private static string Hash(string json)
            => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));

        private void Normalize()
        {
            KindByType = new Dictionary<string, ItemKind>(KindByType, StringComparer.OrdinalIgnoreCase);
            KindByCategory = new Dictionary<string, ItemKind>(KindByCategory, StringComparer.OrdinalIgnoreCase);

            var placement = new Dictionary<ItemKind, PlacementHint>();
            foreach (var kv in PlacementByKind) placement[kv.Key] = kv.Value;
            PlacementByKind = placement;

            KindRules ??= new();
            SafetyRules ??= new();
            foreach (var rule in SafetyRules) rule.Prepare();
            TestItemRules ??= new();
            foreach (var rule in TestItemRules) rule.Prepare();
            NonInventoryTypes ??= new();
            SubtypeSeparators ??= new() { "/", "!" };
        }

        /* ================================================================= */

        public ItemKind ResolveKind(string? rawType, string? name, string? category)
        {
            string type = rawType ?? "";
            string internalName = name ?? "";
            string cat = category ?? "";
            string prefix = GetNamePrefix(internalName);

            foreach (var rule in KindRules)
            {
                if (!rule.Matches(type, prefix, cat)) continue;
                return rule.Kind;
            }

            if (type.Length > 0 && KindByType.TryGetValue(type, out var byType))
                return byType;

            if (cat.Length > 0 && KindByCategory.TryGetValue(cat, out var byCategory))
                return byCategory;

            return ItemKind.Unknown;
        }

        public PlacementHint ResolvePlacement(string? rawType, string? name, ItemKind kind)
        {
            string type = rawType ?? "";
            string prefix = GetNamePrefix(name ?? "");

            // Rule-level placement overrides (e.g. CookingRecipe → None).
            foreach (var rule in KindRules)
            {
                if (rule.Placement == null) continue;
                if (!rule.Matches(type, prefix, "")) continue;
                return rule.Placement.Value;
            }

            if (type.Length > 0 && NonInventoryTypes.Any(t =>
                    string.Equals(t, type, StringComparison.OrdinalIgnoreCase)))
            {
                return PlacementHint.None;
            }

            return PlacementByKind.TryGetValue(kind, out var hint) ? hint : PlacementHint.None;
        }

        public SafetyState ResolveSafety(
            string? name, string? displayName, string? category, string? notes, string? catalogue,
            string? type = null)
        {
            if (SafetyRules.Count == 0) return SafetyState.Safe;

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [SafetyField.Name] = FoldForSafety(name),
                [SafetyField.Display] = FoldForSafety(displayName),
                [SafetyField.Category] = FoldForSafety(category),
                [SafetyField.Notes] = FoldForSafety(notes),
                [SafetyField.Catalogue] = FoldForSafety(catalogue),
                [SafetyField.Type] = FoldForSafety(type)
            };

            foreach (var rule in SafetyRules)
            {
                if (rule.MatchAny.Count == 0) continue;

                // A rule only looks at the fields it names (all when none are named),
                // so e.g. "Corrupt" inside an internal quest path cannot make an item UNSAFE.
                var scope = rule.Fields.Count == 0 ? fields.Keys : (IEnumerable<string>)rule.Fields;
                string needle = string.Join(" | ", scope.Select(f => fields.TryGetValue(f, out var v) ? v : ""));
                foreach (string phrase in rule.FoldedIgnore)
                    needle = needle.Replace(phrase, " ", StringComparison.Ordinal);

                if (rule.FoldedMatch.Any(phrase => needle.Contains(phrase, StringComparison.Ordinal)))
                    return rule.State;
            }
            return SafetyState.Safe;
        }

        public bool IsTestItem(string? name, string? displayName, string? category, string? notes)
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [SafetyField.Name] = name ?? "",
                [SafetyField.Display] = displayName ?? "",
                [SafetyField.Category] = category ?? "",
                [SafetyField.Notes] = notes ?? ""
            };
            return TestItemRules.Any(rule => rule.Matches(fields));
        }

        /// <summary>Search folding plus apostrophes removed, so "DON'T" and "DONT" match alike.</summary>
        internal static string FoldForSafety(string? value)
            => TextNormalizer.FoldForSearch(value).Replace("'", "", StringComparison.Ordinal);

        public string GetNamePrefix(string? name)
        {
            if (string.IsNullOrEmpty(name)) return "";

            int best = -1;
            foreach (string separator in SubtypeSeparators)
            {
                if (string.IsNullOrEmpty(separator)) continue;
                int at = name.IndexOf(separator, StringComparison.Ordinal);
                if (at > 0 && (best < 0 || at < best)) best = at;
            }
            return best > 0 ? name[..best] : name;
        }

        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    public sealed class KindRule
    {
        /// <summary>Game TYPE; null or "*" matches any type.</summary>
        public string? Type { get; set; }

        /// <summary>Internal NAME prefix ("Meal", "CookingRecipe", …).</summary>
        public string? NamePrefix { get; set; }

        /// <summary>Rubyelf category (when TYPE is missing).</summary>
        public string? Category { get; set; }

        public ItemKind Kind { get; set; } = ItemKind.Unknown;

        /// <summary>Optional placement override.</summary>
        public PlacementHint? Placement { get; set; }

        [JsonPropertyName("_comment")]
        public string? Comment { get; set; }

        public bool Matches(string type, string namePrefix, string category)
        {
            if (Type is { Length: > 0 } && Type != "*" &&
                !string.Equals(Type, type, StringComparison.OrdinalIgnoreCase))
                return false;

            if (NamePrefix is { Length: > 0 } &&
                !namePrefix.StartsWith(NamePrefix, StringComparison.OrdinalIgnoreCase))
                return false;

            if (Category is { Length: > 0 } &&
                !string.Equals(Category, category, StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }
    }

    public sealed class SafetyRule
    {
        public SafetyState State { get; set; } = SafetyState.Safe;

        public List<string> MatchAny { get; set; } = new();

        /// <summary>Fields this rule reads (see <see cref="SafetyField"/>); empty means all.</summary>
        public List<string> Fields { get; set; } = new();

        /// <summary>Phrases cut out before matching, e.g. "Dont use if you dont have the DLC" for the UNSAFE rule.</summary>
        public List<string> IgnoreAny { get; set; } = new();

        [JsonPropertyName("_comment")]
        public string? Comment { get; set; }

        [JsonIgnore] internal string[] FoldedMatch { get; private set; } = Array.Empty<string>();
        [JsonIgnore] internal string[] FoldedIgnore { get; private set; } = Array.Empty<string>();

        internal void Prepare()
        {
            MatchAny ??= new();
            Fields ??= new();
            IgnoreAny ??= new();
            FoldedMatch = MatchAny.Select(Taxonomy.FoldForSafety).Where(p => p.Length > 0).Distinct().ToArray();
            // Longest first, so a long exception is cut before a shorter one inside it.
            FoldedIgnore = IgnoreAny.Select(Taxonomy.FoldForSafety).Where(p => p.Length > 0)
                .Distinct().OrderByDescending(p => p.Length).ToArray();
        }
    }

    public sealed class TestItemRule
    {
        /// <summary>Fields to test (<see cref="SafetyField"/> names: name, display, category, notes).</summary>
        public List<string> Fields { get; set; } = new();

        /// <summary>.NET regular expression; case-sensitive unless it starts with (?i).</summary>
        public string Pattern { get; set; } = "";

        [JsonPropertyName("_comment")]
        public string? Comment { get; set; }

        [JsonIgnore] private System.Text.RegularExpressions.Regex? _regex;

        internal void Prepare()
        {
            Fields ??= new();
            _regex = string.IsNullOrEmpty(Pattern)
                ? null
                : new System.Text.RegularExpressions.Regex(Pattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }

        internal bool Matches(IReadOnlyDictionary<string, string> fields)
            => _regex != null && Fields.Any(f => fields.TryGetValue(f, out var value) && _regex.IsMatch(value));
    }

    /// <summary>Field names usable in <see cref="SafetyRule.Fields"/>.</summary>
    public static class SafetyField
    {
        public const string Name = "name";
        public const string Display = "display";
        public const string Category = "category";
        public const string Notes = "notes";
        public const string Catalogue = "catalogue";
        public const string Type = "type";
    }
}
