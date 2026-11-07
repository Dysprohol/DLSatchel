namespace DDV_SaveEditor_G.Data
{
    /// <summary>
    /// Source-independent item kind. Derived from the game TYPE and/or the
    /// display category by rules stored in taxonomy.json (never by scattered ifs).
    /// </summary>
    public enum ItemKind
    {
        Unknown = 0,
        Furniture,
        Clothing,
        ActivityItem,
        Companion,
        Building,
        Tool,
        Currency,
        CollectionSet,
        Recipe,
        Memory,
        Makeup,
        Skin,
        Trimming,
        TouchOfMagic,
        MountGear,
        BoardGame,
        PhotoMode,
        AvatarFeature,
        DecorationPreset
    }

    /// <summary>How risky it is to hand this item to the game.</summary>
    public enum SafetyState
    {
        Unknown = 0,
        Safe,
        Caution,
        Unsafe
    }

    /// <summary>Where the game stores the item. Advisory only – save writing stays in ItemPlacement.</summary>
    public enum PlacementHint
    {
        None = 0,
        Backpack,
        Wardrobe,
        Storage,
        Collection,
        Pet,
        Tool,
        Currency,
        DecorationPreset
    }
}
