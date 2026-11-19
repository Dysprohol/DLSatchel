using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DDV_SaveEditor_G.Data;
using DDV.SaveEditor.Tests.TestData;
using Xunit;
using Xunit.Abstractions;

namespace DDV.SaveEditor.Tests
{
    /// <summary>
    /// Runs the engine on the real community workbooks (KOSDDV dump and Rubyelf's
    /// list, placed in pliki_testowe) and asserts the numbers measured on them.
    /// </summary>
    public class IntegrationTests
    {
        private const string DumpFile = "KOSDDV - DDV ITEM DUMP.xlsx";
        private const string RubyelfFile = "Disney Dream Light ID List - Mainted by Rubyelf.xlsx";

        private readonly ITestOutputHelper _output;

        public IntegrationTests(ITestOutputHelper output) => _output = output;

        /* ================================================================= */

        private static string Locate(string fileName)
        {
            string direct = Path.Combine(AppContext.BaseDirectory, fileName);
            if (File.Exists(direct)) return direct;

            string nested = Path.Combine(AppContext.BaseDirectory, "pliki_testowe", fileName);
            if (File.Exists(nested)) return nested;

            throw new FileNotFoundException(
                $"Test fixture '{fileName}' was not copied to the test output. " +
                $"Looked in '{AppContext.BaseDirectory}'.");
        }

        private static async Task<ItemCatalog> LoadAsync(params (string File, int Priority)[] files)
            => (await CatalogHarness.LoadAsync(files)).Catalog;

        /* ================================================================= */

        [Fact]
        public async Task Dump_Alone_HasExpectedUniqueIds()
        {
            var catalog = await LoadAsync((Locate(DumpFile), 10));

            _output.WriteLine($"dump: {catalog.Count} unique ids");
            Assert.Equal(28987, catalog.Count);
        }

        [Fact]
        public async Task Rubyelf_Alone_HasExpectedIdsAndThreeDuplicateIds()
        {
            var catalog = await LoadAsync((Locate(RubyelfFile), 30));

            _output.WriteLine($"rubyelf: {catalog.Count} unique ids, " +
                              $"{catalog.Diagnostics.DuplicateIds} duplicate ids");

            Assert.Equal(11946, catalog.Count);
            Assert.Equal(3, catalog.Diagnostics.DuplicateIds);

            foreach (long id in new[] { 50200119L, 100000652L, 110800007L })
                Assert.NotNull(catalog.Get(id));
        }

        [Fact]
        public async Task DumpAndRubyelf_MergeToExpectedUnion()
        {
            var catalog = await LoadAsync(
                (Locate(DumpFile), 10),
                (Locate(RubyelfFile), 30));

            _output.WriteLine($"dump+rubyelf: {catalog.Count} unique ids");
            Assert.Equal(32406, catalog.Count);
        }

        [Fact]
        public async Task RecordsWithoutInternalName_AreAboutThreeThousandFourHundred()
        {
            // Measured on dump + Rubyelf: every record that only Rubyelf knows has no
            // game-internal NAME (Rubyelf's "Name" column is a display name).
            var catalog = await LoadAsync(
                (Locate(DumpFile), 10),
                (Locate(RubyelfFile), 30));

            int withoutName = catalog.All.Count(r => !r.HasInternalName);
            _output.WriteLine($"records without NAME: {withoutName}");
            Assert.InRange(withoutName, 3417, 3423);   // ~3 420 (±3)
        }

        [Fact]
        public async Task PetsWithoutInternalName_AreReported()
        {
            var catalog = await LoadAsync(
                (Locate(DumpFile), 10),
                (Locate(RubyelfFile), 30));

            var pets = catalog.All
                .Where(r => string.Equals(r.Category, "Pet", StringComparison.OrdinalIgnoreCase))
                .ToList();

            int withoutName = pets.Count(r => !r.HasInternalName);
            _output.WriteLine($"pets: {pets.Count}, without NAME: {withoutName}");

            // 226 pets in Rubyelf, 51 of them are not in the dump (no internal NAME).
            Assert.Equal(226, pets.Count);
            Assert.Equal(51, withoutName);
        }

        [Fact]
        public async Task ColdLoad_UnderThreeSeconds_WarmLoad_UnderThreeHundredMilliseconds()
        {
            string directory = XlsxBuilder.NewTempDirectory();
            var sources = new[]
            {
                (Locate(DumpFile), 10),
                (Locate(RubyelfFile), 30)
            };

            var config = new SourcesConfig();
            foreach (var (file, priority) in sources)
                config.Sources.Add(new CatalogSourceConfig { Path = file, Priority = priority });

            var options = new CatalogLoadOptions
            {
                SourcesPath = Path.Combine(directory, "sources.json"),
                CacheDirectory = directory,
                UseCache = true
            };

            var cold = await ItemCatalog.LoadFromConfigAsync(config, options);
            _output.WriteLine($"cold: {cold.Diagnostics.LoadMs:F0} ms (from cache: {cold.Diagnostics.FromCache})");
            Assert.False(cold.Diagnostics.FromCache);
            Assert.True(cold.Diagnostics.LoadMs < 3000, $"cold load took {cold.Diagnostics.LoadMs:F0} ms");

            var warm = await ItemCatalog.LoadFromConfigAsync(config, options);
            _output.WriteLine($"warm: {warm.Diagnostics.LoadMs:F0} ms (from cache: {warm.Diagnostics.FromCache})");
            Assert.True(warm.Diagnostics.FromCache);
            Assert.True(warm.Diagnostics.LoadMs < 300, $"warm load took {warm.Diagnostics.LoadMs:F0} ms");
        }

        [Fact]
        public async Task FilteringOverThirtyThousandRecords_IsFast()
        {
            var catalog = await LoadAsync(
                (Locate(DumpFile), 10),
                (Locate(RubyelfFile), 30));

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = catalog.Query(new ItemQuery { Kind = ItemKind.Furniture, Take = 200 });
            stopwatch.Stop();

            _output.WriteLine($"filter: {stopwatch.Elapsed.TotalMilliseconds:F2} ms, {result.Count} hits");
            Assert.NotEmpty(result);
            Assert.True(stopwatch.Elapsed.TotalMilliseconds < 50,
                $"filter took {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
        }
    }
}
