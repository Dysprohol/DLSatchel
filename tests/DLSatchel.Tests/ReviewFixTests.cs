using System.Data;
using System.IO;
using DDV_SaveEditor_G;
using DDV_SaveEditor_G.Modules;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DDV.SaveEditor.Tests
{
    /// <summary>Fixes from the whole-program review.</summary>
    public class ReviewFixTests
    {
        [Fact]
        public void OpeningAFileThatIsNotASave_ChangesNothing()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ddv-review-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            string save = Path.Combine(dir, "profile.json");
            File.WriteAllBytes(save, SaveDecoder.Encode("{\"Player\":{\"Level\":7}}"));
            string junk = Path.Combine(dir, "notes.txt");
            File.WriteAllText(junk, "not a save");
            try
            {
                Assert.True(SaveDecoder.TryLoad(save, out _, out _));
                Assert.True(SaveDecoder.LastLoadWasEncrypted);

                Assert.False(SaveDecoder.TryLoad(junk, out _, out _));

                // The open save is still written back encrypted, and no .bak appears next to the junk.
                Assert.True(SaveDecoder.LastLoadWasEncrypted);
                Assert.False(File.Exists(junk + ".bak"));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Theory]
        [InlineData("[")]
        [InlineData("]")]
        [InlineData("a*b")]
        [InlineData("50%")]
        [InlineData("it's")]
        [InlineData("[*%']")]
        public void SearchText_IsTakenLiterally_AndNeverBreaksTheFilter(string typed)
        {
            var table = new DataTable();
            table.Columns.Add("NAME", typeof(string));
            table.Rows.Add("plain");
            table.Rows.Add($"x{typed}y");

            var view = table.DefaultView;
            view.RowFilter = $"[NAME] LIKE '%{MainWindow.LikeText(typed)}%'";

            Assert.Equal(1, view.Count);
            Assert.Equal($"x{typed}y", view[0]["NAME"]);
        }
    }
}
