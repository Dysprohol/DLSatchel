using System;
using System.IO;
using DDV_SaveEditor_G.Data;
using Xunit;

namespace DDV.SaveEditor.Tests
{
    public class SourcesConfigPathTests
    {
        [Fact]
        public void DefaultConfigLivesOutsideSingleFilePublishDirectory()
        {
            string directory = Path.Combine(Path.GetTempPath(), "ddv-portable-test-" + Path.GetRandomFileName());
            Directory.CreateDirectory(directory);
            try
            {
                string resolved = SourcesConfigStore.ResolvePath(null, directory);
                string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                "DDVSaveEditor", "sources.json");
                Assert.Equal(expected, resolved);

                string portable = Path.Combine(directory, "sources.json");
                File.WriteAllText(portable, "{}");
                Assert.Equal(portable, SourcesConfigStore.ResolvePath(null, directory));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
