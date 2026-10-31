using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DDV_SaveEditor_G.Data;

namespace DDV.SaveEditor.Tests.TestData
{
    /// <summary>
    /// Builds a catalog from explicit files only, with its own sources.json and
    /// cache folder, so tests never touch the user's real configuration.
    /// </summary>
    internal static class CatalogHarness
    {
        public static async Task<(ItemCatalog Catalog, string Directory)> LoadAsync(
            IEnumerable<(string Path, int Priority)> sources,
            bool useCache = false)
        {
            string directory = XlsxBuilder.NewTempDirectory();

            var config = new SourcesConfig();
            foreach (var (path, priority) in sources)
            {
                config.Sources.Add(new CatalogSourceConfig
                {
                    Path = Path.GetFullPath(path),
                    Priority = priority,
                    Enabled = true
                });
            }

            var options = new CatalogLoadOptions
            {
                SourcesPath = Path.Combine(directory, "sources.json"),
                CacheDirectory = directory,
                UseCache = useCache
            };

            var catalog = await ItemCatalog.LoadFromConfigAsync(config, options);
            return (catalog, directory);
        }

        public static async Task<(ItemCatalog Catalog, string Directory)> LoadAsync(
            params (string Path, int Priority)[] sources)
            => await LoadAsync(sources, useCache: false);
    }
}
