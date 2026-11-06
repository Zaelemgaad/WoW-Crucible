using System.Globalization;

namespace WoWCrucible.Core;

/// <summary>One rendered tooltip row. Colors are client font colors as hex; <see cref="SocketColor"/> marks an empty-socket row.</summary>
public sealed record ItemTooltipLine(string Left, string? Right = null, string Color = ItemTooltipBuilder.White, bool Header = false, int SocketColor = 0);

/// <summary>Name lookups the tooltip needs from client data. Any of them may return null when the data is not loaded.</summary>
public sealed record ItemTooltipContext(
    Func<uint, SpellTooltipRecord?>? Spell = null,
    Func<uint, string?>? SkillName = null,
    Func<uint, string?>? EnchantName = null);

/// <summary>
/// Builds the WotLK 3.3.5a item tooltip for an item_template draft. Format strings are the client's own
/// (Interface\FrameXML\GlobalStrings.lua, enUS); the line order follows in-game 3.3.5 tooltips.
/// Instance-only lines (Soulbound, enchants, gems, sell price at a vendor) are not part of a template preview.
/// </summary>
public static class ItemTooltipBuilder
{
    // FrameXML font colors: HIGHLIGHT, GREEN, NORMAL, GRAY and RED_FONT_COLOR.
    public const string White = "#FFFFFF", Green = "#1AFF1A", Gold = "#FFD100", Gray = "#808080", Red = "#FF1A1A";
    public const uint FlagHeroic = 0x8, FlagUniqueEquipped = 0x80000, FlagBindToAccount = 0x8000000;
    private const int PlayableClassMask = 0x5FF, PlayableRaceMask = 0x6FF;
    private static readonly string[] Schools = ["Physical", "Holy", "Fire", "Nature", "Frost", "Shadow", "Arcane"];
    private static readonly string[] ResistanceNames = ["Holy Resistance", "Fire Resistance", "Nature Resistance", "Frost Resistance", "Shadow Resistance", "Arcane Resistance"];
    public static readonly (int Id, string Name)[] Classes = [(1, "Warrior"), (2, "Paladin"), (3, "Hunter"), (4, "Rogue"), (5, "Priest"), (6, "Death Knight"), (7, "Shaman"), (8, "Mage"), (9, "Warlock"), (11, "Druid")];
    public static readonly (int Id, string Name)[] Races = [(1, "Human"), (2, "Orc"), (3, "Dwarf"), (4, "Night Elf"), (5, "Undead"), (6, "Tauren"), (7, "Gnome"), (8, "Troll"), (10, "Blood Elf"), (11, "Draenei")];

    public static IReadOnlyList<ItemTooltipLine> Build(ItemDraft item, ItemTooltipContext? context = null)
    {
        context ??= new();
        var lines = new List<ItemTooltipLine>();
        void Add(string text, string color = White) => lines.Add(new(text, null, color));

        lines.Add(new(string.IsNullOrWhiteSpace(item.Name) ? "Unnamed Item" : item.Name.Trim(), null, ItemSemanticCatalog.QualityColor(item.Quality).ToUpperInvariant(), true));
        if ((item.Flags & FlagHeroic) != 0) Add("Heroic", Green); // ITEM_HEROIC
        var bind = (item.Flags & FlagBindToAccount) != 0 ? "Binds to account" : item.Bonding switch // ITEM_BIND_*
        {
            1 => "Binds when picked up", 2 => "Binds when equipped", 3 => "Binds when used", 4 or 5 => "Quest Item", _ => null
        };
        if (bind is not null) Add(bind);
        if (item.MaxCount == 1) Add("Unique"); // ITEM_UNIQUE
        else if (item.MaxCount > 1) Add($"Unique ({item.MaxCount})"); // ITEM_UNIQUE_MULTIPLE
        else if ((item.Flags & FlagUniqueEquipped) != 0) Add("Unique-Equipped"); // ITEM_UNIQUE_EQUIPPABLE
        if (item.StartQuest != 0) Add("This Item Begins a Quest"); // ITEM_STARTS_QUEST

        // Containers replace the slot line with their capacity, e.g. "16 Slot Bag" (CONTAINER_SLOTS).
        if (item.Class is 1 or 11 && item.ContainerSlots > 0) Add($"{item.ContainerSlots} Slot {ItemSemanticCatalog.SubclassName(item.Class, item.Subclass)}");
        else if (item.InventoryType != 18 && SlotName(item.InventoryType) is { } slot) lines.Add(new(slot, SubclassName(item.Class, item.Subclass)));

        if (item.Class == 6 && (item.DamageMin > 0 || item.DamageMax > 0))
        {
            var school = item.DamageType > 0 && item.DamageType < Schools.Length ? Schools[item.DamageType] + " " : string.Empty;
            Add($"Adds {((item.DamageMin + item.DamageMax) / 2.0).ToString("G6", CultureInfo.InvariantCulture)} {school}damage per second"); // AMMO_(SCHOOL_)DAMAGE_TEMPLATE
        }
        else if (item.DamageMin > 0 || item.DamageMax > 0)
            lines.Add(new(DamageText(item.DamageMin, item.DamageMax, item.DamageType, plus: false), item.Delay > 0 ? $"Speed {Fixed(item.Delay / 1000.0, 2)}" : null));
        if (item.DamageMin2 > 0 || item.DamageMax2 > 0) Add(DamageText(item.DamageMin2, item.DamageMax2, item.DamageType2, plus: true));
        if (item.Delay > 0 && (item.DamageMax > 0 || item.DamageMax2 > 0))
        {
            var average = (item.DamageMin + item.DamageMax) / 2.0 + (item.DamageMin2 + item.DamageMax2) / 2.0;
            Add($"({Fixed(average / (item.Delay / 1000.0), 1)} damage per second)"); // DPS_TEMPLATE
        }
        if (item.Armor > 0) Add($"{item.Armor} Armor"); // ARMOR_TEMPLATE
        if (item.Block > 0) Add($"{item.Block} Block"); // SHIELD_BLOCK_TEMPLATE

        var stats = (item.Stats ?? []).Take(10).Where(stat => stat.Type >= 0 && stat.Value != 0).ToArray();
        foreach (var stat in stats.Where(stat => PrimaryStatName(stat.Type) is not null))
            Add($"{Signed(stat.Value)} {PrimaryStatName(stat.Type)}"); // ITEM_MOD_<STAT> "%c%d <Stat>"

        var resistances = Enumerable.Range(0, 6).Select(item.Resistance).ToArray();
        // Equal schools collapse into one ITEM_RESIST_ALL line; in game, equal holy resistance adds no separate line either.
        var allFive = resistances[1] != 0 && resistances.Skip(1).All(value => value == resistances[1]);
        if (resistances[0] != 0 && !(allFive && resistances[0] == resistances[1])) Add($"{Signed(resistances[0])} {ResistanceNames[0]}");
        if (allFive) Add($"{Signed(resistances[1])} to All Resistances"); // ITEM_RESIST_ALL
        else for (var school = 1; school < 6; school++) if (resistances[school] != 0) Add($"{Signed(resistances[school])} {ResistanceNames[school]}"); // ITEM_RESIST_SINGLE

        var sockets = Enumerable.Range(0, 3).Select(item.SocketColor).Where(color => color != 0).ToArray();
        foreach (var color in sockets) lines.Add(new(SocketName(color), null, Gray, SocketColor: color));
        if (sockets.Length > 0 && item.SocketBonus != 0)
            Add($"Socket Bonus: {context.EnchantName?.Invoke(item.SocketBonus) ?? $"Enchantment #{item.SocketBonus}"}", Gray); // ITEM_SOCKET_BONUS

        if (item.MaxDurability > 0) Add($"Durability {item.MaxDurability} / {item.MaxDurability}"); // DURABILITY_TEMPLATE
        var classes = AllowedNames(item.AllowableClass, PlayableClassMask, Classes);
        var races = AllowedNames(item.AllowableRace, PlayableRaceMask, Races);
        if (classes is [var onlyClass] && races is [var onlyRace]) Add($"{onlyRace} {onlyClass} Only"); // RACE_CLASS_ONLY
        else
        {
            if (classes is not null) Add($"Classes: {string.Join(", ", classes)}"); // ITEM_CLASSES_ALLOWED
            if (races is not null) Add($"Races: {string.Join(", ", races)}"); // ITEM_RACES_ALLOWED
        }
        if (item.RequiredLevel > 0) Add($"Requires Level {item.RequiredLevel}"); // ITEM_MIN_LEVEL
        if (item.ItemLevel > 0 && item.Class is 2 or 4) Add($"Item Level {item.ItemLevel}"); // ITEM_LEVEL, shown for weapons and armor only
        if (item.RequiredSkill > 0)
        {
            var skill = context.SkillName?.Invoke(item.RequiredSkill) ?? $"Skill #{item.RequiredSkill}";
            Add(item.RequiredSkillRank > 0 ? $"Requires {skill} ({item.RequiredSkillRank})" : $"Requires {skill}"); // ITEM_MIN_SKILL / ITEM_REQ_SKILL
        }
        if (item.RequiredSpell > 0) Add($"Requires {Name(context.Spell?.Invoke(item.RequiredSpell)) ?? $"Spell #{item.RequiredSpell}"}"); // ITEM_REQ_SKILL

        foreach (var stat in stats.Where(stat => PrimaryStatName(stat.Type) is null))
            Add("Equip: " + SecondaryStatText(stat.Type, stat.Value), Green); // ITEM_SPELL_TRIGGER_ONEQUIP + ITEM_MOD_*
        foreach (var spell in (item.Spells ?? []).Take(5).Where(spell => spell.SpellId > 0 && spell.Trigger != 6))
        {
            var prefix = spell.Trigger switch { 1 => "Equip:", 2 => "Chance on hit:", _ => "Use:" }; // ITEM_SPELL_TRIGGER_*
            string text;
            if (context.Spell?.Invoke((uint)spell.SpellId) is { } record)
            {
                text = SpellDescriptionRenderer.PlainText(record.PreferredItemText(spell.Trigger)).Trim();
                if (text.Length == 0) continue; // The client prints nothing for a spell without tooltip text.
            }
            else text = $"Spell #{spell.SpellId}";
            var cooldown = Math.Max(spell.CooldownMs, spell.CategoryCooldownMs);
            if (spell.Trigger is 0 or 5 && cooldown > 0) text += " " + CooldownText(cooldown);
            Add($"{prefix} {text}", Green);
            if (spell.Charges != 0) Add(Plural(Math.Abs(spell.Charges), "Charge", "Charges")); // ITEM_SPELL_CHARGES
        }
        if (!string.IsNullOrWhiteSpace(item.Description)) Add($"\"{item.Description.Trim()}\"", Gold);
        return lines;
    }

    private static string DamageText(float min, float max, int school, bool plus)
    {
        // DAMAGE_TEMPLATE(_WITH_SCHOOL), SINGLE_DAMAGE_TEMPLATE(_WITH_SCHOOL), PLUS_(SINGLE_)DAMAGE_TEMPLATE(_WITH_SCHOOL). %d truncates.
        var low = (int)min; var high = (int)max;
        var range = low == high ? $"{high}" : $"{low} - {high}";
        var name = school > 0 && school < Schools.Length ? Schools[school] + " " : string.Empty;
        return plus ? (name.Length == 0 ? $"+ {range} Damage" : $"+{range} {name}Damage") : $"{range} {name}Damage";
    }

    private static string CooldownText(int milliseconds)
    {
        var seconds = milliseconds / 1000;
        return seconds >= 86400 ? $"({Plural(seconds / 86400, "Day", "Days")} Cooldown)" // ITEM_COOLDOWN_TOTAL_*
            : seconds >= 3600 ? $"({Plural(seconds / 3600, "Hour", "Hours")} Cooldown)"
            : seconds >= 60 ? $"({seconds / 60} Min Cooldown)"
            : $"({seconds} Sec Cooldown)";
    }

    private static string[]? AllowedNames(int mask, int playable, (int Id, string Name)[] entries)
    {
        if (mask == -1 || (mask & playable) == playable || (mask & playable) == 0) return null;
        return entries.Where(entry => (mask & (1 << (entry.Id - 1))) != 0).Select(entry => entry.Name).ToArray();
    }

    public static string? SlotName(int inventoryType) => inventoryType switch // INVTYPE_*
    {
        1 => "Head", 2 => "Neck", 3 => "Shoulder", 4 => "Shirt", 5 => "Chest", 6 => "Waist", 7 => "Legs", 8 => "Feet", 9 => "Wrist",
        10 => "Hands", 11 => "Finger", 12 => "Trinket", 13 => "One-Hand", 14 => "Off Hand", 15 => "Ranged", 16 => "Back", 17 => "Two-Hand",
        18 => "Bag", 19 => "Tabard", 20 => "Chest", 21 => "Main Hand", 22 => "Off Hand", 23 => "Held In Off-hand", 24 => "Ammo",
        25 => "Thrown", 26 => "Ranged", 27 => "Quiver", 28 => "Relic", _ => null
    };

    /// <summary>ItemSubClass.dbc short display names, as shown on the right of the slot line.</summary>
    public static string? SubclassName(int itemClass, int subclass) => itemClass switch
    {
        2 => subclass switch
        {
            0 or 1 => "Axe", 2 => "Bow", 3 => "Gun", 4 or 5 => "Mace", 6 => "Polearm", 7 or 8 => "Sword", 10 => "Staff", 13 => "Fist Weapon",
            14 => "Miscellaneous", 15 => "Dagger", 16 => "Thrown", 17 => "Spear", 18 => "Crossbow", 19 => "Wand", 20 => "Fishing Pole", _ => null
        },
        4 => subclass switch { 1 => "Cloth", 2 => "Leather", 3 => "Mail", 4 => "Plate", 6 => "Shield", 7 => "Libram", 8 => "Idol", 9 => "Totem", 10 => "Sigil", _ => null },
        6 => subclass switch { 2 => "Arrow", 3 => "Bullet", _ => null },
        _ => null
    };

    private static string SocketName(int color) => color switch // EMPTY_SOCKET_*
    {
        1 => "Meta Socket", 2 => "Red Socket", 4 => "Yellow Socket", 8 => "Blue Socket", _ => "Prismatic Socket"
    };

    private static string? PrimaryStatName(int type) => type switch
    {
        0 => "Mana", 1 => "Health", 3 => "Agility", 4 => "Strength", 5 => "Intellect", 6 => "Spirit", 7 => "Stamina", _ => null
    };

    private static string SecondaryStatText(int type, int value) => type switch // ITEM_MOD_*
    {
        12 => $"Increases defense rating by {value}.",
        13 => $"Increases your dodge rating by {value}.",
        14 => $"Increases your parry rating by {value}.",
        15 => $"Increases your shield block rating by {value}.",
        16 => $"Improves melee hit rating by {value}.",
        17 => $"Improves ranged hit rating by {value}.",
        18 => $"Improves spell hit rating by {value}.",
        19 => $"Improves melee critical strike rating by {value}.",
        20 => $"Improves ranged critical strike rating by {value}.",
        21 => $"Improves spell critical strike rating by {value}.",
        22 => $"Improves melee hit avoidance rating by {value}.",
        23 => $"Improves ranged hit avoidance rating by {value}.",
        24 => $"Improves spell hit avoidance rating by {value}.",
        25 => $"Improves melee critical avoidance rating by {value}.",
        26 => $"Improves ranged critical avoidance rating by {value}.",
        27 => $"Improves spell critical avoidance rating by {value}.",
        28 => $"Improves melee haste rating by {value}.",
        29 => $"Improves ranged haste rating by {value}.",
        30 => $"Improves spell haste rating by {value}.",
        31 => $"Improves hit rating by {value}.",
        32 => $"Improves critical strike rating by {value}.",
        33 => $"Improves hit avoidance rating by {value}.",
        34 => $"Improves critical avoidance rating by {value}.",
        35 => $"Improves your resilience rating by {value}.",
        36 => $"Improves haste rating by {value}.",
        37 => $"Increases your expertise rating by {value}.",
        38 => $"Increases attack power by {value}.",
        39 => $"Increases ranged attack power by {value}.",
        40 => $"Increases attack power by {value} in Cat, Bear, Dire Bear, and Moonkin forms only.",
        41 => $"Increases healing done by magical spells and effects by up to {value}.",
        42 => $"Increases damage done by magical spells and effects by up to {value}.",
        43 => $"Restores {value} mana per 5 sec.",
        44 => $"Increases your armor penetration rating by {value}.",
        45 => $"Increases spell power by {value}.",
        46 => $"Restores {value} health per 5 sec.",
        47 => $"Increases spell penetration by {value}.",
        48 => $"Increases the block value of your shield by {value}.",
        _ => $"Unknown stat {type} by {value}."
    };

    private static string? Name(SpellTooltipRecord? spell) => spell is null ? null : SpellDescriptionRenderer.PlainText(spell.Name).Trim();
    private static string Signed(int value) => value < 0 ? value.ToString(CultureInfo.InvariantCulture) : "+" + value.ToString(CultureInfo.InvariantCulture);
    private static string Fixed(double value, int decimals) => value.ToString("F" + decimals, CultureInfo.InvariantCulture);
    private static string Plural(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";
}
