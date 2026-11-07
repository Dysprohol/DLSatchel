using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DDV_SaveEditor_G.Modules;
using Xunit;

namespace DDV.SaveEditor.Tests
{
    public class PresetLayoutTests
    {
        [Fact]
        public void BuiltInList_HasTheFurnitureOfEachPreset()
        {
            var layouts = PresetLayouts.ReadEmbedded();

            Assert.Equal(24, layouts.Count);
            var haunted = layouts[2050500029];
            Assert.Equal(10, haunted.Items.Count);
            Assert.Equal(44, haunted.Pieces);
            Assert.Equal(10, haunted.Items[40006281]);   // Haunted Mansion Pillar
        }

        [Fact]
        public void GameFiles_AreDecodedFromProtobuf()
        {
            string assets = Path.Combine(Path.GetTempPath(), "dls-presets-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(assets, "itemlist"));
            Directory.CreateDirectory(Path.Combine(assets, "PremadeDecorationPresets"));

            // ActivityData record: id, name, … path of the layout file.
            var activity = new List<byte> { 0x0A, 0x05, 1, 2, 3, 4, 5, 0x08 };
            activity.AddRange(Varint(2050500050));
            activity.AddRange(Encoding.ASCII.GetBytes("\x12\x0EPremadeTestKit\x1A\x02xx\x22\x24PremadeDecorationPresets/TestKit.json"));
            File.WriteAllBytes(Path.Combine(assets, "itemlist", "ActivityData.json"), activity.ToArray());

            // Layout: grid objects, each a nested message with the item ID and coordinates.
            var layout = new List<byte>();
            foreach (long item in new long[] { 40000001, 40000001, 40000002, 40000001 })
            {
                var obj = new List<byte> { 0x08, 0x01, 0x10 };
                obj.AddRange(Varint(item));
                obj.AddRange(new byte[] { 0x18, 0x30, 0x20, 0x14 });
                layout.Add(0x2A);
                layout.Add((byte)obj.Count);
                layout.AddRange(obj);
            }
            File.WriteAllBytes(Path.Combine(assets, "PremadeDecorationPresets", "TestKit.json"), layout.ToArray());

            var result = PresetLayouts.ReadFromGame(assets);

            var kit = Assert.Single(result).Value;
            Assert.Equal(2050500050, kit.PresetId);
            Assert.Equal("PremadeTestKit", kit.Name);
            Assert.Equal(3, kit.Items[40000001]);
            Assert.Equal(1, kit.Items[40000002]);
            Directory.Delete(assets, recursive: true);
        }

        [Fact]
        public void InstalledGame_MatchesTheBuiltInList()
        {
            const string assets = @"G:\SteamLibrary\steamapps\common\Disney Dreamlight Valley\ddv_Data\StreamingAssets";
            if (!Directory.Exists(assets)) return;   // only on a machine with the game installed

            var fromGame = PresetLayouts.ReadFromGame(assets);
            var builtIn = PresetLayouts.ReadEmbedded();

            Assert.True(fromGame.Count >= builtIn.Count);
            foreach (var (id, layout) in builtIn)
                Assert.Equal(layout.Items.OrderBy(kv => kv.Key), fromGame[id].Items.OrderBy(kv => kv.Key));
        }

        private static IEnumerable<byte> Varint(long value)
        {
            do
            {
                byte b = (byte)(value & 0x7F);
                value >>= 7;
                yield return value != 0 ? (byte)(b | 0x80) : b;
            } while (value != 0);
        }
    }
}
