using System.IO;
using MemoryPack;

namespace DDV_SaveEditor_G.Data
{
    /// <summary>
    /// Where a single fact came from. Row and Column are 1-based, Column is the
    /// numeric index (1 = "A"). Provenance is stored per merged field.
    /// </summary>
    [MemoryPackable]
    public sealed partial class SourceRef
    {
        public string File { get; set; } = "";
        public string Sheet { get; set; } = "";
        public int Row { get; set; }
        public int Column { get; set; }

        /// <summary>"A", "B", … for humans; not persisted separately.</summary>
        [MemoryPackIgnore]
        public string ColumnName => TextNormalizer.ColumnName(Column);

        public override string ToString()
        {
            string file = string.IsNullOrEmpty(File) ? "?" : Path.GetFileName(File);
            return $"{file} › {Sheet} › {ColumnName}{Row}";
        }
    }
}