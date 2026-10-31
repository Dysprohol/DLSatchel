using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using DDV_SaveEditor_G.Modules;

namespace DDV_SaveEditor_G
{
    public class SaveEntry
    {
        public string Folder { get; set; }
        public string Profile { get; set; }
        public string Player { get; set; }
        public int Level { get; set; }
        public DateTime Modified { get; set; }
    }

    internal static class SaveAutoDetect
    {
        private static readonly Regex SteamFolderRegex =
            new(@"^steam_.*_r$", RegexOptions.IgnoreCase);

        /// <summary>
        /// Znajduje wszystkie save'y i zwraca listę SaveEntry dla UI.
        /// </summary>
        public static List<SaveEntry> FindAllSaves()
        {
            var list = new List<SaveEntry>();

            // Poprawiona ścieżka do LocalLow
            string basePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData", "LocalLow",
                "Gameloft", "Disney Dreamlight Valley");

            if (!Directory.Exists(basePath))
                return list;

            var dirs = Directory.GetDirectories(basePath)
                .Where(d => SteamFolderRegex.IsMatch(Path.GetFileName(d)));

            foreach (var dir in dirs)
            {
                string profilePath = Path.Combine(dir, "profile.json");
                if (!File.Exists(profilePath))
                    continue;

                string name = "<encrypted>";
                int level = 0;

                // Soft decode tylko Name + Level
                var json = TrySoftDecode(profilePath);
                if (json != null)
                {
                    name = json["Player"]?["Name"]?.ToString() ?? name;
                    level = json["Player"]?["Level"]?.ToObject<int>() ?? 0;
                }

                list.Add(new SaveEntry
                {
                    Folder = Path.GetFileName(dir),
                    Profile = profilePath,
                    Player = name,
                    Level = level,
                    Modified = File.GetLastWriteTime(profilePath)
                });
            }

            return list
                .OrderByDescending(s => s.Modified)
                .ToList();
        }


        /// <summary>
        /// Zwraca najnowszy save lub null.
        /// </summary>
        public static SaveEntry? FindSingleSave()
        {
            var all = FindAllSaves();
            if (all.Count == 0)
                return null;

            return all[0];
        }


        // =================================================================
        // Podgląd (tylko Name + Level) – ten sam dekoder co przy wczytywaniu,
        // ale bez tworzenia .bak / *_decoded.json w folderze gry.
        // =================================================================

        private static JObject? TrySoftDecode(string path)
        {
            try
            {
                return SaveDecoder.TryDecode(path, out var json, out _) ? json : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
