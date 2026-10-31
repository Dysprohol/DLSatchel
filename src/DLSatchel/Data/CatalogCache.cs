using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MemoryPack;

namespace DDV_SaveEditor_G.Data
{
    /// <summary>
    /// Lean record for cache: only fields needed at runtime. Diagnostic fields
    /// (LocalizedNames, Provenance, Warnings, SubtypeSource, TypeSource, Dlc, Image,
    /// CatalogueCategory, Patch) are omitted to keep the cache small and fast.
    /// </summary>
    [MemoryPackable]
    public sealed partial class CachedItemRecord
    {
        public long Id { get; set; }
        public string RawId { get; set; } = "";
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string DisplayNameLanguage { get; set; } = "en";
        public string RawType { get; set; } = "";
        public ItemKind Kind { get; set; }
        public string Subtype { get; set; } = "";
        public string Category { get; set; } = "";
        public string Notes { get; set; } = "";
        public SafetyState Safety { get; set; }
        public PlacementHint Placement { get; set; }
        public bool HasInternalName { get; set; }
        public bool TypeWasInferred { get; set; }
        public string SearchText { get; set; } = "";
        public bool IsTestItem { get; set; }
    }

    /// <summary>Serialized form of a built catalog snapshot, used by the on-disk cache.</summary>
    [MemoryPackable]
    public sealed partial class CachedCatalog
    {
        public string SchemaVersion { get; set; } = CatalogCache.SchemaVersion;
        public string Key { get; set; } = "";
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public CachedItemRecord[] Ordered { get; set; } = Array.Empty<CachedItemRecord>();
        public CachedItemRecord[][] ByKind { get; set; } = Array.Empty<CachedItemRecord[]>();
        public CatalogDiagnostics Diagnostics { get; set; } = new();
        public List<string> LoadedFiles { get; set; } = new();
        public List<string> Errors { get; set; } = new();
    }

    /// <summary>
    /// %LocalAppData%\DDVSaveEditor\cache\catalog-v5.bin – MemoryPack binary.
    /// The key covers every file's path, size, mtime, priority, mapping overrides
    /// and the taxonomy, so any edit invalidates the cache automatically.
    /// </summary>
    public static class CatalogCache
    {
        public const string SchemaVersion = "8"; // CachedItemRecord gained IsTestItem
        public const string FileName = "catalog-v6.bin";

        public static string DefaultDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DDVSaveEditor", "cache");

        public static string ResolvePath(string? cacheDirectory = null, string? fileName = null)
            => Path.Combine(cacheDirectory ?? DefaultDirectory, fileName ?? FileName);

        /// <summary>Computes a stable key for the current sources + taxonomy.</summary>
        public static string ComputeKey(SourcesConfig config, Taxonomy taxonomy)
        {
            var sb = new StringBuilder(4096);
            sb.Append("schema=").Append(SchemaVersion).Append('\n');
            sb.Append("taxonomy=").Append(TaxonomyFingerprint(taxonomy)).Append('\n');

            foreach (var source in config.Sources
                         .Where(s => s.Enabled)
                         .OrderBy(s => s.TreatAsHistory ? 1 : 0)
                         .ThenBy(s => s.Priority)
                         .ThenBy(s => s.Path, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append(source.Path.ToLowerInvariant()).Append('|')
                  .Append(source.Priority).Append('|')
                  .Append(source.TreatAsHistory ? '1' : '0');

                try
                {
                    var info = new FileInfo(source.Path);
                    if (info.Exists)
                        sb.Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks);
                    else
                        sb.Append("|missing");
                }
                catch
                {
                    sb.Append("|unknown");
                }

                foreach (var mapping in source.MappingOverrides.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase))
                {
                    sb.Append('|').Append(mapping.Key).Append('=')
                      .Append(mapping.Value.IdColumn).Append(',')
                      .Append(mapping.Value.Role).Append(',')
                      .Append(mapping.Value.Language);
                    foreach (var column in mapping.Value.Columns.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
                        sb.Append(';').Append(column.Key).Append(':').Append(column.Value);
                }

                sb.Append('\n');
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        }

        private static string TaxonomyFingerprint(Taxonomy taxonomy)
        {
            // Hash of the rules actually in use, so editing a phrase in the embedded
            // taxonomy also invalidates the cache (rule counts alone did not change).
            if (taxonomy.ContentHash.Length > 0) return taxonomy.ContentHash;

            try
            {
                if (File.Exists(taxonomy.SourcePath))
                    return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(taxonomy.SourcePath)));
            }
            catch
            {
                // fall through to a structural fingerprint
            }

            return "embedded-" + taxonomy.SchemaVersion + "-" + taxonomy.KindRules.Count + "-" +
                   taxonomy.SafetyRules.Count + "-" + taxonomy.KindByType.Count;
        }

        public static bool TryLoad(string cachePath, string expectedKey, out CachedCatalog cached)
        {
            cached = new CachedCatalog();
            try
            {
                if (!File.Exists(cachePath)) return false;

                byte[] bytes = File.ReadAllBytes(cachePath);
                var loaded = MemoryPackSerializer.Deserialize<CachedCatalog>(bytes);

                if (loaded == null ||
                    loaded.SchemaVersion != SchemaVersion ||
                    !string.Equals(loaded.Key, expectedKey, StringComparison.Ordinal) ||
                    loaded.Ordered == null ||
                    loaded.ByKind == null ||
                    loaded.ByKind.Length != Enum.GetValues<ItemKind>().Length ||
                    loaded.Diagnostics == null ||
                    loaded.LoadedFiles == null ||
                    loaded.Errors == null ||
                    loaded.Ordered.Any(record => record == null) ||
                    loaded.ByKind.Any(records => records != null && records.Any(record => record == null)))
                {
                    return false;
                }

                cached = loaded;
                return true;
            }
            catch
            {
                // corrupt cache must never break startup
                return false;
            }
        }

        public static void Save(string cachePath, CachedCatalog catalog)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

            string temp = cachePath + ".tmp";
            byte[] bytes = MemoryPackSerializer.Serialize(catalog);
            File.WriteAllBytes(temp, bytes);

            if (File.Exists(cachePath)) File.Replace(temp, cachePath, null);
            else File.Move(temp, cachePath);
        }

        public static void Clear(string? cacheDirectory = null)
        {
            try
            {
                string path = ResolvePath(cacheDirectory);
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // clearing the cache is best-effort
            }
        }
    }
}
