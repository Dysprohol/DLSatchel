using DDV_SaveEditor_G.Data;
using Xunit;

namespace DDV.SaveEditor.Tests
{
    public class TaxonomySafetyTests
    {
        private static readonly Taxonomy Embedded = Taxonomy.Load();

        private static SafetyState Resolve(string name = "", string display = "", string category = "",
                                           string notes = "", string type = "")
            => Embedded.ResolveSafety(name, display, category, notes, "", type);

        [Fact]
        public void UnsafeWordsInsideInternalQuestPath_GiveCautionNotUnsafe()
        {
            Assert.Equal(SafetyState.Caution, Resolve(
                name: "Ingredient/Mission!Village!Timon!VLG_Timo_040_CorruptedMushrooms",
                display: "Night Mushroom", type: "ActivityItem"));
        }

        [Theory]
        [InlineData("Bugged [Don't Use]", SafetyState.Unsafe)]
        [InlineData("DONT USE", SafetyState.Unsafe)]
        [InlineData("DO NOT CRAFT WILL BREAK SAVE", SafetyState.Unsafe)]
        [InlineData("Dont use if you dont have the DLC", SafetyState.Caution)]
        [InlineData("CRAFTED QUEST ITEM, DON'T USE UNLESS BROKEN QUEST", SafetyState.Caution)]
        [InlineData("Can bug if crafted", SafetyState.Caution)]
        [InlineData("I think this is a renamed enchanted stage, i wouldnt use it", SafetyState.Caution)]
        [InlineData("No longer listed as \"Debug\"", SafetyState.Safe)]
        public void RubyelfNotes_AreClassifiedByMeaning(string notes, SafetyState expected)
            => Assert.Equal(expected, Resolve(display: "Some Item", notes: notes));

        [Theory]
        [InlineData("Corrupted Purple Rock")]
        [InlineData("The De Vil Crash")]
        public void WarningWordsInRealItemNames_DoNotMakeThemUnsafe(string display)
            => Assert.Equal(SafetyState.Safe, Resolve(display: display));

        [Fact]
        public void CrashInNotes_IsUnsafe()
            => Assert.Equal(SafetyState.Unsafe, Resolve(display: "Some Item", notes: "Do not Craft can crash game"));

        [Fact]
        public void EventOnlyInType_IsNotCaution()
            => Assert.Equal(SafetyState.Safe, Resolve(name: "Recurring/Thing", type: "ConditionalEvent"));

        [Theory]
        [InlineData("CloFeet/Feet_Test", "Feet_Test", "", "", true)]
        [InlineData("CloTopInner/TestAsset!Shirt100", "Bare Test Torso", "", "", true)]
        [InlineData("Blocker/MountPushable!LargeBoulder", "TestBoulder", "", "", true)]
        [InlineData("CloFeet/BareFeet", "Bare Feet", "Clothes Shoe", "TEST ITEM", true)]
        [InlineData("", "Fullbody_Test", "Clothes Outfit", "TEST_ITEM", true)]
        [InlineData("Consummable/Mission!Village!Mushu!VLG_Mush_020_Rorschach", "Ink Blot Test", "", "", false)]
        [InlineData("Default/44_Mulan!Update11!RorschachTest01", "The Beast's Test", "", "", false)]
        [InlineData("Default/20_Incredibles!Update14!EdnaOutfitTestingMachine", "Edna Mode Testing Machine", "", "", false)]
        [InlineData("Default/10_ToyStory!Set03!StrongTest01", "\"Show Your Strength\" Test", "Furniture", "", false)]
        [InlineData("Default/Mission!Village!Merida!VLG_Meri_020_FlynnWanted", "Archery Contest Sign", "", "", false)]
        [InlineData("Fish/WhiteSturgeon", "White Sturgeon", "Fish", "", false)]
        public void TestItems_AreDeveloperAssetsOnly(string name, string display, string category, string notes, bool expected)
            => Assert.Equal(expected, Embedded.IsTestItem(name, display, category, notes));

        [Fact]
        public void EmbeddedTaxonomy_HasContentHashForCacheKey()
        {
            Assert.NotEmpty(Embedded.ContentHash);
            Assert.Equal(Embedded.ContentHash, Taxonomy.Load().ContentHash);
        }
    }
}
