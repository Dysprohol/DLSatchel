using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DDV.SaveEditor.Tests.TestData;
using DDV_SaveEditor_G.Modules;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DDV.SaveEditor.Tests
{
    /// <summary>Wild critters (Critter/..., 1201xxxxx) are not companions: game saves never hold them in Player.Pets.</summary>
    public class CritterTests
    {
        private static async Task LoadCatalogAsync()
        {
            string directory = XlsxBuilder.NewTempDirectory();
            string dump = XlsxBuilder.WriteCsv(Path.Combine(directory, "dump.csv"),
                "ID,NAME,LOCALIZED_DISPLAY_NAME,TYPE\n" +
                "120000002,Pet/Rabbit!Rabbit01,Classic Rabbit,Companion\n" +
                "120100000,Critter/Meadow!CritterMeadow01,Classic Rabbit,Companion\n" +
                "120100139,Critter/Swamp!Axolotl05,Onyx Axolotl,Companion\n");
            var (catalog, _) = await CatalogHarness.LoadAsync((dump, 10));
            ItemDatabase.SetCatalog(catalog);
        }

        private static JObject Save(params long[] pets) => new()
        {
            ["Player"] = new JObject
            {
                ["Pets"] = new JArray(pets.Select(id => PetModule.NewPetRecord(id, null)))
            }
        };

        [Fact]
        public async Task ACritter_CannotBeAddedAsACompanion_ButTheCompanionCan()
        {
            await LoadCatalogAsync();
            var save = Save();

            var critter = ItemPlacement.Resolve(save, "120100139");
            Assert.Equal(PlacementKind.Unsupported, critter.Kind);
            Assert.Contains("wild critter", critter.Problem);

            Assert.Equal(PlacementKind.Pet, ItemPlacement.Resolve(save, "120000002").Kind);
        }

        [Fact]
        public async Task CheckSave_ReportsCrittersInTheCompanionList()
        {
            await LoadCatalogAsync();
            var issues = SaveValidationModule.Analyze(Save(120000002, 120100139));

            var issue = Assert.Single(issues, i => i.Path == "Player.Pets[1]");
            Assert.Equal(SaveIssueLevel.Error, issue.Level);
            Assert.Contains("Onyx Axolotl", issue.Message);
            Assert.DoesNotContain(issues, i => i.Path == "Player.Pets[0]");
        }

        [Fact]
        public async Task RemovingACompanion_LeavesCollectionFlagsAlone()
        {
            await LoadCatalogAsync();
            var save = Save(120000002, 120100139);
            save["Player"]!["CollectionSets"] = JArray.Parse(
                """[ { "GroupData": [ { "GroupsCollectionItems": { "120000002": false, "120100139": true } } ] } ]""");
            var flagsBefore = save["Player"]!["CollectionSets"]!.DeepClone();

            var pets = PetModule.MapPets(save);
            pets.Rows[1].Delete();                                   // the critter
            PetModule.SavePets(save, pets, updateCollections: false); // as Save Changes does

            Assert.Single((JArray)save["Player"]!["Pets"]!);
            Assert.True(JToken.DeepEquals(flagsBefore, save["Player"]!["CollectionSets"]));
        }

        [Fact]
        public async Task CritterIds_AreRecognised_EvenWithoutTheirName()
        {
            await LoadCatalogAsync();
            Assert.True(ItemPlacement.IsWildCritter(120100000));
            Assert.True(ItemPlacement.IsWildCritter(120100999));   // not in the lists: the ID range decides
            Assert.False(ItemPlacement.IsWildCritter(120000002));
        }
    }
}
