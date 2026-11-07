using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace DDV_SaveEditor_G.Modules
{
    /// <summary>The furniture one Gameloft decoration preset is made of: item ID → pieces in the layout.</summary>
    internal sealed record PresetLayout(long PresetId, string Name, IReadOnlyDictionary<long, int> Items)
    {
        public int Pieces => Items.Values.Sum();
    }

    /// <summary>
    /// A preset in the save is only an ID; the game places it from a layout file in
    /// StreamingAssets/PremadeDecorationPresets, which lists the furniture it needs. The game's
    /// itemlist/ActivityData.json links each preset ID to its file. Both are protobuf. They are read
    /// from the installed game when it is found (so new presets work at once), otherwise from the
    /// built-in Data/presets.json.
    /// </summary>
    internal static class PresetLayouts
    {
        public const string EmbeddedResourceName = "DDV_SaveEditor_G.Data.presets.json";
        private const long FirstPresetId = 2050500000, LastPresetId = 2050500999;
        private const long FirstFurnitureId = 40000000, LastFurnitureId = 40999999;

        private static IReadOnlyDictionary<long, PresetLayout>? _all;

        /// <summary>"game files" or "built-in list".</summary>
        public static string Source { get; private set; } = "";

        public static IReadOnlyDictionary<long, PresetLayout> All => _all ??= Load();

        public static PresetLayout? Get(long presetId) => All.TryGetValue(presetId, out var layout) ? layout : null;

        private static IReadOnlyDictionary<long, PresetLayout> Load()
        {
            foreach (string game in GameFolders())
            {
                string assets = Path.Combine(game, "ddv_Data", "StreamingAssets");
                try
                {
                    var fromGame = ReadFromGame(assets);
                    if (fromGame.Count > 0)
                    {
                        Source = "game files";
                        return fromGame;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // try the next library, then the built-in list
                }
            }

            Source = "built-in list";
            return ReadEmbedded();
        }

        /// <summary>Reads every preset layout the installed game knows.</summary>
        internal static Dictionary<long, PresetLayout> ReadFromGame(string streamingAssets)
        {
            var result = new Dictionary<long, PresetLayout>();
            string activityData = Path.Combine(streamingAssets, "itemlist", "ActivityData.json");
            string folder = Path.Combine(streamingAssets, "PremadeDecorationPresets");
            if (!File.Exists(activityData) || !Directory.Exists(folder)) return result;

            foreach (var (id, name, file) in FindPresetFiles(File.ReadAllBytes(activityData)))
            {
                string path = Path.Combine(folder, file);
                if (!File.Exists(path)) continue;
                var items = ReadLayout(File.ReadAllBytes(path));
                if (items.Count > 0) result[id] = new PresetLayout(id, name, items);
            }
            return result;
        }

        private static readonly Regex PresetFile = new(@"PremadeDecorationPresets/([A-Za-z0-9_]+\.json)", RegexOptions.CultureInvariant);
        private static readonly Regex PresetName = new(@"[A-Za-z][A-Za-z0-9_]{3,}", RegexOptions.CultureInvariant);

        /// <summary>
        /// In ActivityData each preset record holds its ID, then its name and, a little further on,
        /// the path of its layout file.
        /// </summary>
        internal static IEnumerable<(long Id, string Name, string File)> FindPresetFiles(byte[] activityData)
        {
            var text = Encoding.Latin1.GetString(activityData);
            for (long id = FirstPresetId; id <= LastPresetId; id++)
            {
                string encoded = Encoding.Latin1.GetString(Varint(id));
                int at = text.IndexOf(encoded, StringComparison.Ordinal);
                while (at >= 0)
                {
                    int start = at + encoded.Length;
                    string tail = text.Substring(start, Math.Min(400, text.Length - start));
                    var file = PresetFile.Match(tail);
                    if (file.Success && file.Index < 300)
                    {
                        var name = PresetName.Match(tail);
                        yield return (id, name.Success ? name.Value : file.Groups[1].Value, file.Groups[1].Value);
                        break;
                    }
                    at = text.IndexOf(encoded, at + 1, StringComparison.Ordinal);
                }
            }
        }

        /// <summary>Counts the furniture IDs placed in one layout file.</summary>
        internal static Dictionary<long, int> ReadLayout(byte[] data)
        {
            var counts = new Dictionary<long, int>();
            foreach (long value in Varints(data, 0, data.Length))
                if (value is >= FirstFurnitureId and <= LastFurnitureId)
                    counts[value] = counts.TryGetValue(value, out int n) ? n + 1 : 1;
            return counts;
        }

        private static IEnumerable<long> Varints(byte[] data, int from, int to)
        {
            int i = from;
            while (i < to)
            {
                int wireType = data[i++] & 7;
                switch (wireType)
                {
                    case 0:
                        if (!TryReadVarint(data, ref i, to, out long value)) yield break;
                        yield return value;
                        break;
                    case 2:
                        if (!TryReadVarint(data, ref i, to, out long length) || length < 0 || i + length > to) yield break;
                        foreach (long nested in Varints(data, i, i + (int)length)) yield return nested;
                        i += (int)length;
                        break;
                    case 1: i += 8; break;
                    case 5: i += 4; break;
                    default: yield break;
                }
            }
        }

        private static bool TryReadVarint(byte[] data, ref int i, int to, out long value)
        {
            value = 0;
            for (int shift = 0; i < to && shift < 64; shift += 7)
            {
                byte b = data[i++];
                value |= (long)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
            }
            return false;
        }

        private static byte[] Varint(long value)
        {
            var bytes = new List<byte>();
            do
            {
                byte b = (byte)(value & 0x7F);
                value >>= 7;
                bytes.Add(value != 0 ? (byte)(b | 0x80) : b);
            } while (value != 0);
            return bytes.ToArray();
        }

        internal static Dictionary<long, PresetLayout> ReadEmbedded()
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName);
            if (stream == null) return new();
            using var reader = new StreamReader(stream);
            var presets = JObject.Parse(reader.ReadToEnd())["presets"] as JObject;
            var result = new Dictionary<long, PresetLayout>();
            foreach (var preset in presets?.Properties() ?? Enumerable.Empty<JProperty>())
            {
                if (!long.TryParse(preset.Name, out long id) || preset.Value["items"] is not JObject items) continue;
                var counts = items.Properties()
                    .Where(p => long.TryParse(p.Name, out _))
                    .ToDictionary(p => long.Parse(p.Name), p => p.Value.Value<int>());
                result[id] = new PresetLayout(id, preset.Value.Value<string>("name") ?? preset.Name, counts);
            }
            return result;
        }

        /// <summary>Steam library folders holding the game, from the Steam registry key and libraryfolders.vdf.</summary>
        private static IEnumerable<string> GameFolders()
        {
            var libraries = new List<string>();
            try
            {
                if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string steam)
                {
                    libraries.Add(steam);
                    string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(vdf))
                        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                            libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }

            foreach (string library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string game = Path.Combine(library, "steamapps", "common", "Disney Dreamlight Valley");
                if (Directory.Exists(game)) yield return game;
            }
        }
    }
}
