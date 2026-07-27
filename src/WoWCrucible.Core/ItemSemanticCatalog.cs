namespace WoWCrucible.Core;

/// <summary>
/// Readable build-12340 item identities shared by SQL, DBC, and guided item views.
/// Unknown values remain explicit instead of being silently mislabeled.
/// </summary>
public static class ItemSemanticCatalog
{
    public static string ClassName(int value) => value switch
    {
        0 => "Consumable", 1 => "Container", 2 => "Weapon", 3 => "Gem", 4 => "Armor", 5 => "Reagent",
        6 => "Projectile", 7 => "Trade Goods", 8 => "Generic", 9 => "Recipe", 10 => "Money",
        11 => "Quiver", 12 => "Quest", 13 => "Key", 14 => "Permanent", 15 => "Miscellaneous",
        16 => "Glyph", _ => $"Unknown class {value}"
    };

    public static string SubclassName(int itemClass, int value) => itemClass switch
    {
        0 => Name(value, "Consumable", "Potion", "Elixir", "Flask", "Scroll", "Food & Drink", "Item Enhancement", "Bandage", "Other"),
        1 => Name(value, "Bag", "Soul Bag", "Herb Bag", "Enchanting Bag", "Engineering Bag", "Gem Bag", "Mining Bag", "Leatherworking Bag", "Inscription Bag"),
        2 => value switch
        {
            0 => "One-Handed Axe", 1 => "Two-Handed Axe", 2 => "Bow", 3 => "Gun", 4 => "One-Handed Mace",
            5 => "Two-Handed Mace", 6 => "Polearm", 7 => "One-Handed Sword", 8 => "Two-Handed Sword",
            9 => "Obsolete Weapon", 10 => "Staff", 11 => "Exotic Weapon", 12 => "Exotic Weapon 2",
            13 => "Fist Weapon", 14 => "Miscellaneous Weapon", 15 => "Dagger", 16 => "Thrown",
            17 => "Spear", 18 => "Crossbow", 19 => "Wand", 20 => "Fishing Pole", _ => $"Unknown weapon subtype {value}"
        },
        3 => Name(value, "Red Gem", "Blue Gem", "Yellow Gem", "Purple Gem", "Green Gem", "Orange Gem", "Meta Gem", "Simple Gem", "Prismatic Gem"),
        4 => value switch
        {
            0 => "Miscellaneous Armor", 1 => "Cloth", 2 => "Leather", 3 => "Mail", 4 => "Plate",
            5 => "Buckler (obsolete)", 6 => "Shield", 7 => "Libram", 8 => "Idol", 9 => "Totem",
            10 => "Sigil", _ => $"Unknown armor subtype {value}"
        },
        5 => value == 0 ? "Reagent" : $"Unknown reagent subtype {value}",
        6 => value switch { 0 => "Wand Projectile (obsolete)", 1 => "Bolt (obsolete)", 2 => "Arrow", 3 => "Bullet", 4 => "Thrown Projectile (obsolete)", _ => $"Unknown projectile subtype {value}" },
        7 => Name(value, "Trade Goods", "Parts", "Explosives", "Devices", "Jewelcrafting", "Cloth", "Leather", "Metal & Stone", "Meat", "Herb", "Elemental", "Other", "Enchanting", "Materials", "Armor Enchantment", "Weapon Enchantment"),
        8 => value == 0 ? "Generic" : $"Unknown generic subtype {value}",
        9 => Name(value, "Book", "Leatherworking Recipe", "Tailoring Recipe", "Engineering Recipe", "Blacksmithing Recipe", "Cooking Recipe", "Alchemy Recipe", "First Aid Manual", "Enchanting Formula", "Fishing Manual", "Jewelcrafting Design", "Inscription Technique"),
        10 => value == 0 ? "Money" : $"Unknown money subtype {value}",
        11 => value switch { 0 => "Quiver (obsolete)", 1 => "Quiver 2 (obsolete)", 2 => "Quiver", 3 => "Ammo Pouch", _ => $"Unknown quiver subtype {value}" },
        12 => value == 0 ? "Quest Item" : $"Unknown quest subtype {value}",
        13 => value switch { 0 => "Key", 1 => "Lockpick", _ => $"Unknown key subtype {value}" },
        14 => value == 0 ? "Permanent" : $"Unknown permanent subtype {value}",
        15 => Name(value, "Junk", "Reagent", "Companion Pet", "Holiday", "Other", "Mount"),
        16 => value switch
        {
            1 => "Warrior Glyph", 2 => "Paladin Glyph", 3 => "Hunter Glyph", 4 => "Rogue Glyph",
            5 => "Priest Glyph", 6 => "Death Knight Glyph", 7 => "Shaman Glyph", 8 => "Mage Glyph",
            9 => "Warlock Glyph", 11 => "Druid Glyph", _ => $"Unknown glyph subtype {value}"
        },
        _ => $"Unknown subtype {value}"
    };

    public static string TypeName(int itemClass, int subclass)
    {
        var className = ClassName(itemClass);
        var subclassName = SubclassName(itemClass, subclass);
        return className.Equals(subclassName, StringComparison.OrdinalIgnoreCase) ? className : $"{className} · {subclassName}";
    }

    public static string InventoryTypeName(int value) => value switch
    {
        0 => "Not equippable", 1 => "Head", 2 => "Neck", 3 => "Shoulders", 4 => "Shirt", 5 => "Chest",
        6 => "Waist", 7 => "Legs", 8 => "Feet", 9 => "Wrists", 10 => "Hands", 11 => "Finger",
        12 => "Trinket", 13 => "One-Hand", 14 => "Off Hand / Shield", 15 => "Ranged", 16 => "Back",
        17 => "Two-Hand", 18 => "Bag", 19 => "Tabard", 20 => "Robe", 21 => "Main Hand",
        22 => "Off Hand Weapon", 23 => "Held In Off-Hand", 24 => "Ammo", 25 => "Thrown",
        26 => "Ranged Right", 27 => "Quiver", 28 => "Relic", _ => $"Unknown inventory type {value}"
    };

    public static string QualityName(int value) => value switch
    {
        0 => "Poor", 1 => "Common", 2 => "Uncommon", 3 => "Rare", 4 => "Epic", 5 => "Legendary",
        6 => "Artifact", 7 => "Heirloom", _ => $"Unknown quality {value}"
    };

    private static string Name(int value, params string[] names) =>
        value >= 0 && value < names.Length ? names[value] : $"Unknown subtype {value}";
}
