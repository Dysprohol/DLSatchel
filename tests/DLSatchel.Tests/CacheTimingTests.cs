using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using DDV_SaveEditor_G.Data;
using Xunit;
using Xunit.Abstractions;

namespace DDV.SaveEditor.Tests
{
    public class CacheTimingTests
    {
        private readonly ITestOutputHelper _output;
        public CacheTimingTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task BreakDownWarmLoad()
        {
            string dir = XlsxBuilder.NewTempDirectory();
            string dump = Path.Combine(AppContext.BaseDirectory, "KOSDDV - DDV ITEM DUMP.xlsx");
            if (!File.Exists(dump))
                dump = Path.Combine(AppContext.BaseDirectory, "pliki_testowe", "KOSDDV - DDV ITEM DUMP.xlsx");

            var config = new SourcesConfig();
            config.Sources.Add(new CatalogSourceConfig { Path = dump, Priority = 10 });

            var options = new CatalogLoadOptions
            {
                SourcesPath = Path.Combine(dir, "sources.json"),
                CacheDirectory = dir,
                UseCache = true
            };

            var tax = Taxonomy.Load(Path.Combine(AppContext.BaseDirectory, "taxonomy.json"));
            string key = CatalogCache.ComputeKey(config, tax);

            var cold = await ItemCatalog.LoadFromConfigAsync(config, options);
            _output.WriteLine($"cold: {cold.Diagnostics.LoadMs:F0} ms, records {cold.Count}");

            var sw = Stopwatch.StartNew();
            bool ok = CatalogCache.TryLoad(CatalogCache.ResolvePath(dir), key, out var cached);
            sw.Stop();
            _output.WriteLine($"TryLoad: {sw.Elapsed.TotalMilliseconds:F0} ms ok={ok} records={cached.Ordered.Length}");
        }
    }
}
