using DDV_SaveEditor_G.Modules;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DDV.SaveEditor.Tests
{
    public class PetModuleTests
    {
        [Fact]
        public void SavingPetListPreservesExistingProgressAndUnknownFields()
        {
            var save = JObject.Parse("""
            {
              "Player": { "Pets": [
                { "PetItemID": 120000146, "FriendshipLevel": 4, "FriendshipXp": 23,
                  "LastPettedDate": "2026-10-30", "FutureGameField": { "Value": 7 } },
                { "PetItemID": 120000147, "FriendshipLevel": 2 }
              ] }
            }
            """);
            var table = PetModule.MapPets(save);
            table.Rows[1].Delete();

            PetModule.SavePets(save, table, updateCollections: false);

            var pet = (JObject)save["Player"]!["Pets"]![0]!;
            Assert.Single((JArray)save["Player"]!["Pets"]!);
            Assert.Equal(4, pet["FriendshipLevel"]?.Value<int>());
            Assert.Equal(23, pet["FriendshipXp"]?.Value<int>());
            Assert.Equal(7, pet["FutureGameField"]?["Value"]?.Value<int>());
            Assert.Equal("2026-10-30", pet["LastPettedDate"]?.Value<string>());
        }

        [Fact]
        public void AddingPetDoesNotAlterMetadataOfExistingPets()
        {
            var save = JObject.Parse("""
            { "Player": { "Pets": [ { "PetItemID": 120000146, "FriendshipLevel": 5, "Unknown": true } ] } }
            """);
            var table = PetModule.MapPets(save);
            PetModule.AddPetRow(table, 120000148);

            PetModule.SavePets(save, table, updateCollections: false);

            var pets = (JArray)save["Player"]!["Pets"]!;
            Assert.Equal(5, pets[0]!["FriendshipLevel"]?.Value<int>());
            Assert.True(pets[0]!["Unknown"]?.Value<bool>());
            Assert.Equal(120000148, pets[1]!["PetItemID"]?.Value<long>());
        }
    }
}
