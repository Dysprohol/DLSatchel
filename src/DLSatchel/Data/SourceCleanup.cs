using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DDV_SaveEditor_G.Data
{
    /// <summary>What "Clean up" would remove from the Sources list and from disk.</summary>
    public sealed record CleanupPlan(IReadOnlyList<CatalogSourceConfig> Remove, IReadOnlyList<string> DeleteFiles)
    {
        public bool IsEmpty => Remove.Count == 0 && DeleteFiles.Count == 0;
    }

    /// <summary>
    /// Old item lists: entries that are switched off or whose file is gone, and the backups
    /// "Update from links" keeps. Files are only deleted inside the editor's own folder
    /// (%LocalAppData%\DDVSaveEditor); lists elsewhere are only taken off the list.
    /// </summary>
    public static class SourceCleanup
    {
        public static string EditorFolder => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDVSaveEditor");

        public static CleanupPlan Plan(SourcesConfig config, string editorFolder, string backupFolder)
        {
            var remove = config.Sources.Where(s => !s.Enabled || !File.Exists(s.Path)).ToList();
            var keep = config.Sources.Except(remove).Select(s => Full(s.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Only the folders that hold lists: Catalog (older versions) and lists. The cache,
            // sources.json and settings.json in the same editor folder are never candidates.
            string[] listFolders =
            {
                System.IO.Path.Combine(editorFolder, "Catalog"),
                System.IO.Path.Combine(editorFolder, "lists")
            };
            var delete = remove
                .Select(s => Full(s.Path))
                .Where(p => File.Exists(p) && listFolders.Any(f => IsInside(p, f)) && !keep.Contains(p))
                .ToList();
            if (Directory.Exists(backupFolder))
                delete.AddRange(Directory.EnumerateFiles(backupFolder).Select(Full).Where(p => !keep.Contains(p)));

            return new CleanupPlan(remove, delete.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        }

        internal static bool IsInside(string path, string folder)
        {
            string root = Full(folder).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            return Full(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private static string Full(string path)
        {
            try { return System.IO.Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
        }
    }
}
