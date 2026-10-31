using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DDV_SaveEditor_G.Data
{
    /// <summary>One input file. Ordering lives in <see cref="Priority"/>: lower wins.</summary>
    public sealed class CatalogSourceConfig
    {
        public string Path { get; set; } = "";

        public bool Enabled { get; set; } = true;

        /// <summary>Lower number = higher priority. Dump should beat Rubyelf.</summary>
        public int Priority { get; set; } = 100;

        /// <summary>BACKUP / "Before Update" sheets are only used to fill gaps.</summary>
        public bool TreatAsHistory { get; set; }

        /// <summary>Sheet name → manual column mapping (wins over auto-detection).</summary>
        public Dictionary<string, SheetMappingOverride> MappingOverrides { get; set; } = new();

        public CatalogSourceConfig Clone() => new()
        {
            Path = Path,
            Enabled = Enabled,
            Priority = Priority,
            TreatAsHistory = TreatAsHistory,
            MappingOverrides = MappingOverrides.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Clone(),
                StringComparer.OrdinalIgnoreCase)
        };
    }

    public sealed class SheetMappingOverride
    {
        /// <summary>Header text of the id column (not a letter), e.g. "Item ID".</summary>
        public string? IdColumn { get; set; }

        /// <summary>Logical field ("NAME", "TYPE", …) → header text.</summary>
        public Dictionary<string, string> Columns { get; set; } = new();

        /// <summary>Forces a role instead of auto-detection: Primary, Language, History.</summary>
        public string? Role { get; set; }

        /// <summary>Language code for a forced Language role.</summary>
        public string? Language { get; set; }

        public SheetMappingOverride Clone() => new()
        {
            IdColumn = IdColumn,
            Columns = new Dictionary<string, string>(Columns, StringComparer.OrdinalIgnoreCase),
            Role = Role,
            Language = Language
        };
    }

    /// <summary>Contents of sources.json.</summary>
    public sealed class SourcesConfig
    {
        public const int CurrentSchemaVersion = 1;

        [JsonPropertyName("_comment")]
        public string Comment { get; set; } =
            "Item list sources. priority: lower number wins. " +
            "mappingOverrides are written by the Sources panel and win over auto-detection.";

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public List<CatalogSourceConfig> Sources { get; set; } = new();

        public SourcesConfig Clone() => new()
        {
            Comment = Comment,
            SchemaVersion = SchemaVersion,
            Sources = Sources.Select(s => s.Clone()).ToList()
        };
    }

    /// <summary>Loads/creates sources.json and assigns default priorities.</summary>
    public static class SourcesConfigStore
    {
        public static readonly string[] SupportedExtensions =
            { ".xlsx", ".xlsm", ".csv", ".tsv" };

        /// <summary>Location: explicit path → existing portable config → %LocalAppData%.</summary>
        public static string ResolvePath(string? explicitPath, string? baseDirectory = null)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath!;

            string baseDir = baseDirectory ?? AppContext.BaseDirectory;
            string portable = System.IO.Path.Combine(baseDir, "sources.json");
            // Keep an intentionally supplied portable config, but don't create sidecars
            // beside a newly published single-file executable by default.
            if (File.Exists(portable)) return portable;

            return System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DDVSaveEditor", "sources.json");
        }

        public static SourcesConfig LoadOrCreate(
            string? path = null,
            string? sourcesDirectory = null,
            bool saveWhenCreated = true)
        {
            string resolved = ResolvePath(path);
            if (File.Exists(resolved))
            {
                try
                {
                    var loaded = JsonSerializer.Deserialize<SourcesConfig>(File.ReadAllText(resolved), JsonOptions);
                    if (loaded != null)
                    {
                        Normalize(loaded);
                        return loaded;
                    }
                }
                catch
                {
                    // corrupt config: fall through to a fresh discovery
                }
            }

            var config = new SourcesConfig
            {
                Sources = Discover(sourcesDirectory ?? AppContext.BaseDirectory)
            };
            if (saveWhenCreated)
            {
                try { Save(config, resolved); } catch { /* read-only install: keep in memory */ }
            }
            return config;
        }

        public static void Save(SourcesConfig config, string? path = null)
        {
            string resolved = ResolvePath(path);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(resolved)!);

            string tmp = resolved + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOptions));
            if (File.Exists(resolved)) File.Replace(tmp, resolved, null);
            else File.Move(tmp, resolved);
        }

        /// <summary>All supported files in a folder, with default priorities applied.</summary>
        public static List<CatalogSourceConfig> Discover(string directory)
        {
            var result = new List<CatalogSourceConfig>();
            if (!Directory.Exists(directory)) return result;

            foreach (string file in Directory.EnumerateFiles(directory)
                         .Where(f => SupportedExtensions.Contains(
                             System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                         .Where(f => !System.IO.Path.GetFileName(f).StartsWith("~$"))
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(new CatalogSourceConfig
                {
                    Path = file,
                    Enabled = true,
                    Priority = GuessPriority(System.IO.Path.GetFileName(file))
                });
            }
            return result;
        }

        /// <summary>
        /// Default order: dump (EN) → dump-companies → Rubyelf.
        /// Everything unknown lands between companies and Rubyelf.
        /// </summary>
        public static int GuessPriority(string fileName)
        {
            string name = fileName.ToLowerInvariant();
            if (name.Contains("rubyelf") || name.Contains("id list")) return 30;
            if (name.Contains("companies")) return 20;
            if (name.Contains("dump")) return 10;
            return 25;
        }

        /// <summary>Adds files that are not yet in the config; existing entries are untouched.</summary>
        public static void MergePaths(SourcesConfig config, IEnumerable<string> paths)
        {
            foreach (string path in paths)
            {
                string full = System.IO.Path.GetFullPath(path);
                var existing = config.Sources.FirstOrDefault(
                    s => string.Equals(s.Path, full, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    config.Sources.Add(new CatalogSourceConfig
                    {
                        Path = full,
                        Enabled = true,
                        Priority = GuessPriority(System.IO.Path.GetFileName(full))
                    });
                }
                else
                {
                    existing.Enabled = true;
                }
            }
            Normalize(config);
        }

        private static void Normalize(SourcesConfig config)
        {
            foreach (var source in config.Sources)
                source.MappingOverrides ??= new();

            config.Sources = config.Sources
                .OrderBy(s => s.Priority)
                .ThenBy(s => System.IO.Path.GetFileName(s.Path), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() }
        };
    }
}
