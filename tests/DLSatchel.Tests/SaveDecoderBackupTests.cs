using System.IO;
using DDV_SaveEditor_G.Modules;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DDV.SaveEditor.Tests
{
    public class SaveDecoderBackupTests
    {
        [Fact]
        public void SaveCreatesFreshSiblingBackupOfExactPreSaveBytes()
        {
            string directory = Path.Combine(Path.GetTempPath(), "ddv-backup-test-" + Path.GetRandomFileName());
            Directory.CreateDirectory(directory);
            string profile = Path.Combine(directory, "profile.json");
            const string original = "{\"Player\":{\"Level\":7}}";
            File.WriteAllText(profile, original);

            try
            {
                Assert.True(SaveDecoder.TryLoad(profile, out _, out var json));
                json["Player"]!["Level"] = 8;

                string? backup = SaveDecoder.Save(profile, json);

                Assert.NotNull(backup);
                Assert.True(File.Exists(backup));
                Assert.Equal(original, File.ReadAllText(backup));
                Assert.Equal(8, JObject.Parse(File.ReadAllText(profile))["Player"]?["Level"]?.Value<int>());
                Assert.Equal(original, File.ReadAllText(profile + ".bak"));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void EncryptedSaveIsRoundTripValidatedAndRemainsReadable()
        {
            string directory = Path.Combine(Path.GetTempPath(), "ddv-encrypted-save-test-" + Path.GetRandomFileName());
            Directory.CreateDirectory(directory);
            string profile = Path.Combine(directory, "profile.bin");
            var initial = JObject.Parse("{\"Player\":{\"Level\":7}}");
            File.WriteAllBytes(profile, SaveDecoder.Encode(initial.ToString(Newtonsoft.Json.Formatting.None)));

            try
            {
                Assert.True(SaveDecoder.TryLoad(profile, out _, out var json));
                Assert.True(SaveDecoder.LastLoadWasEncrypted);
                json["Player"]!["Level"] = 8;

                string? backup = SaveDecoder.Save(profile, json);

                Assert.NotNull(backup);
                Assert.True(File.Exists(backup));
                Assert.True(SaveDecoder.TryLoad(profile, out _, out var reread));
                Assert.True(SaveDecoder.LastLoadWasEncrypted);
                Assert.True(JToken.DeepEquals(json, reread));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
