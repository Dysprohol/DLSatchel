using System;
using System.IO;
using System.Text.Json;

namespace DDV_SaveEditor_G.Updates
{
    /// <summary>Small per-user settings, kept next to the catalog settings in %LocalAppData%\DDVSaveEditor.</summary>
    internal sealed class AppSettings
    {
        /// <summary>Off by default: the editor contacts GitHub only when asked to.</summary>
        public bool CheckUpdatesAtStart { get; set; }

        /// <summary>The player said No to downloading the item lists at first start: don't ask at every start.</summary>
        public bool ListDownloadDeclined { get; set; }

        /// <summary>Ask before adding an item with warnings. Off: the warning only shows in the status line.</summary>
        public bool AskBeforeAddingWarnedItems { get; set; } = true;

        /// <summary>Off by default: once a day at start, download the item lists and say how many items are new.</summary>
        public bool CheckListsAtStart { get; set; }

        public DateTime? LastListCheckUtc { get; set; }

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DDVSaveEditor", "settings.json");

        public static AppSettings Load()
        {
            try
            {
                return File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                    : new AppSettings();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return new AppSettings();
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a read-only profile keeps the in-memory choice for this session
            }
        }
    }
}
