using System.Linq;
using DDV_SaveEditor_G.Data;
using DDV_SaveEditor_G.Modules;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DDV.SaveEditor.Tests
{
    public class BackpackLimitTests
    {
        private const string BackpackPath = "Player.ContainerInventories.0";

        /// <summary>A backpack with the given Size/ExtraSize and items in the listed slot indexes.</summary>
        private static JObject Save(int size, int extraSize, params int[] occupied)
        {
            var inventory = new JArray();
            int count = occupied.Length == 0 ? 0 : occupied.Max() + 1;
            for (int i = 0; i < count; i++)
            {
                bool used = occupied.Contains(i);
                inventory.Add(new JObject
                {
                    ["ItemID"] = used ? 30000001 + i : 0,
                    ["Amount"] = used ? 1 : 0,
                    ["State"] = null
                });
            }
            return new JObject
            {
                ["Player"] = new JObject
                {
                    ["ContainerInventories"] = new JObject
                    {
                        ["0"] = new JObject { ["Size"] = size, ["ExtraSize"] = extraSize, ["Inventory"] = inventory }
                    }
                }
            };
        }

        private static SaveValidationIssue[] BackpackIssues(JObject save)
        {
            ItemDatabase.SetCatalog(ItemCatalog.Empty);
            return SaveValidationModule.Analyze(save).Where(issue => issue.Path == BackpackPath).ToArray();
        }

        [Fact]
        public void OverLimitBackpackIsReportedWithItemsBeyondSlot42()
        {
            var save = Save(49, 0, 0, 41, 42, 45, 48);
            var before = save.DeepClone();

            var issue = Assert.Single(BackpackIssues(save));

            Assert.Equal(SaveIssueLevel.Caution, issue.Level);
            Assert.Contains("set to 49 slots", issue.Message);
            Assert.Contains("3 item(s) are beyond slot 42", issue.Message);
            Assert.Equal("Backpack", SaveDiffModule.Describe(save, issue.Path));
            Assert.True(JToken.DeepEquals(before, save));
        }

        [Fact]
        public void OverLimitBackpackWithoutItemsBeyondSlot42StillWarns()
        {
            var issue = Assert.Single(BackpackIssues(Save(45, 0, 0, 10)));

            Assert.DoesNotContain("beyond slot 42:", issue.Message);
            Assert.Contains("Set the backpack to 42.", issue.Message);
        }

        [Theory]
        [InlineData(21)]
        [InlineData(35)]
        [InlineData(42)]
        public void BackpackAtOrBelowLimitIsNotReported(int size)
            => Assert.Empty(BackpackIssues(Save(size, 0, 0, size - 1)));

        [Fact]
        public void ExtraSizeIsCountedAsPartOfSize()
        {
            // The game treats ExtraSize as part of Size, so the check compares Size minus ExtraSize with 42.
            Assert.Empty(BackpackIssues(Save(42, 0, 0)));
            Assert.Empty(BackpackIssues(Save(42, 7, 0)));
            Assert.Empty(BackpackIssues(Save(49, 7, 0)));
            Assert.Single(BackpackIssues(Save(50, 7, 0)));
        }
    }
}
