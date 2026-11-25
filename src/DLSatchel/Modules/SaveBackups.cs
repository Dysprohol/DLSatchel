using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DDV_SaveEditor_G.Modules
{
    public sealed record SaveBackup(string Path, DateTime Time, bool BeforeFirstEdit)
    {
        public long Size => File.Exists(Path) ? new FileInfo(Path).Length : 0;

        public string Label => (BeforeFirstEdit
            ? $"Before your first edit (the game saved it {Format(Time, "d MMM yyyy, HH:mm")})"
            : $"Before saving on {Format(Time, "d MMM yyyy, HH:mm:ss")}") + $"   ·   {Size / 1024.0:N0} KB";

        /// <summary>English month names, like the rest of the window.</summary>
        /// <summary>What the copy is, without its size: for sentences like "Restored: …".</summary>
        public string Description => BeforeFirstEdit
            ? $"the copy from before your first edit ({Format(Time, "d MMM yyyy, HH:mm")})"
            : $"the copy from {Format(Time, "d MMM yyyy, HH:mm:ss")}";

        public override string ToString() => Label;

        public static string Format(DateTime time, string pattern) => time.ToString(pattern, CultureInfo.GetCultureInfo("en-GB"));
    }

    /// <summary>
    /// The copies the editor keeps next to a save: profile.json.bak (the save as it was before the
    /// editor first opened it) and profile.json.backup-&lt;date&gt; (made before every Save Changes).
    /// </summary>
    public static class SaveBackups
    {
        private const string Stamp = "yyyyMMdd-HHmmss-fff";

        /// <summary>Newest first; the copy from before the first edit last.</summary>
        public static List<SaveBackup> Find(string savePath)
        {
            var list = new List<SaveBackup>();
            string dir = Path.GetDirectoryName(Path.GetFullPath(savePath))!;
            string name = Path.GetFileName(savePath);
            if (!Directory.Exists(dir)) return list;

            foreach (string file in Directory.GetFiles(dir, name + ".backup-*"))
            {
                string stamp = Path.GetFileName(file)[(name.Length + ".backup-".Length)..];
                if (stamp.Length > Stamp.Length) stamp = stamp[..Stamp.Length];   // "-1" when two saves met in one millisecond
                var time = DateTime.TryParseExact(stamp, Stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                    ? t
                    : File.GetLastWriteTime(file);
                list.Add(new SaveBackup(file, time, BeforeFirstEdit: false));
            }
            list.Sort((a, b) => b.Time.CompareTo(a.Time));

            string first = Path.Combine(dir, name + ".bak");
            if (File.Exists(first))
                list.Add(new SaveBackup(first, File.GetLastWriteTime(first), BeforeFirstEdit: true));
            return list;
        }

        /// <summary>
        /// Puts a backup back in place of the save. The current save is copied first, so this can be
        /// undone with the same list. Returns that copy's path.
        /// </summary>
        public static string Restore(string savePath, SaveBackup backup)
        {
            if (!SaveDecoder.TryDecode(backup.Path, out _, out _))
                throw new InvalidDataException("This backup cannot be read, so it was not restored.");

            string safety = SaveDecoder.CreateTimestampedBackup(savePath);
            string temp = savePath + ".restore.tmp";
            File.Copy(backup.Path, temp, overwrite: true);
            File.Move(temp, savePath, overwrite: true);
            return safety;
        }
    }
}
