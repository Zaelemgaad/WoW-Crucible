using WoWCrucible.Core;

/// <summary>
/// Item tooltips transcribed from in-game WotLK 3.3.5a screenshots (unowned item, no vendor open).
/// Each expected line is the screenshot text; a right-hand column is written as "left | right".
/// </summary>
internal static class ItemTooltipTestSuite
{
    public static void Run(string? clientDataPath = null, string? dbcFolder = null)
    {
        if (dbcFolder is not null)
        {
            var names = ItemTooltipNameCatalog.Load(dbcFolder);
            Require(names.Skills.GetValueOrDefault(356u) == "Fishing" && names.Enchantments.Count > 1000,
                $"Real DBC names did not resolve: skill 356 = '{names.Skills.GetValueOrDefault(356u)}', {names.Enchantments.Count} enchantments.");
            Console.WriteLine($"DBC names: {names.Skills.Count} skills, {names.Enchantments.Count} enchantments.");
        }
        if (clientDataPath is not null)
        {
            var cache = Path.Combine(Path.GetTempPath(), "wow-crucible-font-test");
            var font = ClientAssetLocator.Find(clientDataPath, cache) ?? throw new InvalidOperationException($"No {ClientAssetLocator.TooltipFont} found in {clientDataPath}.");
            Require(new FileInfo(font).Length > 10_000 && ClientAssetLocator.Find(clientDataPath, cache) == font, "The client tooltip font must extract once and be reused.");
            Console.WriteLine($"Client tooltip font: {font}");
            var socketPaths = new[] { "-Meta", "-Red", "-Yellow", "-Blue", "" }.Select(color => $@"Interface\ItemSocketingFrame\UI-EmptySocket{color}.blp").ToArray();
            var sockets = ClientAssetLocator.FindAll(clientDataPath, cache, socketPaths);
            Require(socketPaths.All(sockets.ContainsKey), "Missing socket textures: " + string.Join(", ", socketPaths.Where(path => !sockets.ContainsKey(path))));
            foreach (var path in socketPaths) Require(BlpTextureService.Decode(sockets[path]) is { Width: > 0, Height: > 0 }, $"{path} did not decode.");
            Console.WriteLine($"Client socket textures: {string.Join(", ", socketPaths.Select(path => Path.GetFileName(sockets[path])))}");
        }
        var spells = new Dictionary<uint, SpellTooltipRecord>();
        uint Spell(string description, string name = "")
        {
            var id = (uint)spells.Count + 1;
            spells[id] = new(id, name, string.Empty, description, string.Empty);
            return id;
        }
        var stealMagic = Spell(string.Empty, "Steal Magic");
        var experienced = Spell(string.Empty, "Experienced");
        var context = new ItemTooltipContext(
            id => spells.GetValueOrDefault(id),
            skill => skill == 356 ? "Fishing" : null,
            enchant => enchant switch { 3312 => "+4 Agility", 3313 => "+10 All Stats", _ => null });
        ItemSpellDraft Effect(int trigger, string description, int cooldown = -1) => new((int)Spell(description), trigger, 0, 0, cooldown, 0, -1);
        ItemDraft Item(string name, int quality, int itemClass, int subclass, int inventory, uint bonding) =>
            new(900000, name, itemClass, subclass, 1, quality, inventory, 0, 0, 0, 0, bonding, 0, 0, 0, 0, 0, 0, string.Empty);

        Check("Signet of Twilight", Item("Signet of Twilight", 4, 4, 0, 11, 1) with
        {
            Flags = ItemTooltipBuilder.FlagHeroic | ItemTooltipBuilder.FlagUniqueEquipped, RequiredLevel = 80, ItemLevel = 284,
            Stats = [new(3, 109), new(7, 109), new(38, 145), new(32, 73), new(31, 57)], SocketColors = [4], SocketBonus = 3312
        },
            "Signet of Twilight", "Heroic", "Binds when picked up", "Unique-Equipped", "Finger", "+109 Agility", "+109 Stamina",
            "Yellow Socket", "Socket Bonus: +4 Agility", "Requires Level 80", "Item Level 284",
            "Equip: Increases attack power by 145.", "Equip: Improves critical strike rating by 73.", "Equip: Improves hit rating by 57.");

        Check("Ashbringer", Item("Ashbringer", 5, 2, 8, 17, 1) with
        {
            MaxCount = 1, DamageMin = 201, DamageMax = 247, Delay = 3000, DamageMin2 = 30, DamageMax2 = 50, DamageType2 = 1, MaxDurability = 145,
            RequiredLevel = 60, ItemLevel = 76, Spells = [Effect(2, "Blasts a target for 700 Fire damage.")], Description = "Blade of the Scarlet Highlord"
        },
            "Ashbringer", "Binds when picked up", "Unique", "Two-Hand | Sword", "201 - 247 Damage | Speed 3.00", "+30 - 50 Holy Damage",
            "(88.0 damage per second)", "Durability 145 / 145", "Requires Level 60", "Item Level 76",
            "Chance on hit: Blasts a target for 700 Fire damage.", "\"Blade of the Scarlet Highlord\"");

        Check("Thunderfury", Item("Thunderfury, Blessed Blade of the Windseeker", 5, 2, 7, 13, 1) with
        {
            MaxCount = 2, DamageMin = 185, DamageMax = 285, Delay = 1800, DamageMin2 = 406, DamageMax2 = 488, DamageType2 = 3,
            Stats = [new(4, 252), new(3, 236), new(7, 247), new(5, 222), new(6, 270)], Resistances = [0, 0, 150, 0, 0, 0], RequiredLevel = 60, ItemLevel = 300,
            Spells = [Effect(1, "Increases spell power by 324."), Effect(2, "Blasts your enemy with lightning.")], Description = "Thunderstruck!"
        },
            "Thunderfury, Blessed Blade of the Windseeker", "Binds when picked up", "Unique (2)", "One-Hand | Sword", "185 - 285 Damage | Speed 1.80",
            "+406 - 488 Nature Damage", "(378.9 damage per second)", "+252 Strength", "+236 Agility", "+247 Stamina", "+222 Intellect", "+270 Spirit",
            "+150 Nature Resistance", "Requires Level 60", "Item Level 300", "Equip: Increases spell power by 324.",
            "Chance on hit: Blasts your enemy with lightning.", "\"Thunderstruck!\"");

        Check("Zael's Ring of Power", Item("Zael's Ring of Power", 7, 4, 0, 11, 0) with
        {
            Flags = ItemTooltipBuilder.FlagBindToAccount, MaxCount = 1, RequiredLevel = 80, RequiredSpell = experienced,
            Stats = [new(4, 32767), new(3, 12000), new(7, 32767), new(5, 12000), new(6, 32767), new(38, 32767), new(45, 32767), new(41, 32767), new(42, 32767), new(36, 1200)],
            SocketColors = [2, 2, 2], Spells = [Effect(1, "Decreases the cooldown of your Judgement spells by 2 sec.")], Description = "The only ring the one can't rule."
        },
            "Zael's Ring of Power", "Binds to account", "Unique", "Finger", "+32767 Strength", "+12000 Agility", "+32767 Stamina", "+12000 Intellect", "+32767 Spirit",
            "Red Socket", "Red Socket", "Red Socket", "Requires Level 80", "Requires Experienced",
            "Equip: Increases attack power by 32767.", "Equip: Increases spell power by 32767.",
            "Equip: Increases healing done by magical spells and effects by up to 32767.", "Equip: Increases damage done by magical spells and effects by up to 32767.",
            "Equip: Improves haste rating by 1200.", "Equip: Decreases the cooldown of your Judgement spells by 2 sec.", "\"The only ring the one can't rule.\"");

        Check("Zael's Ring of Suppression", Item("Zael's Ring of Suppression", 7, 4, 0, 11, 0) with
        {
            Flags = ItemTooltipBuilder.FlagBindToAccount, MaxCount = 1, AllowableClass = 1 << 1, AllowableRace = 1 << 9, RequiredLevel = 80, RequiredSpell = stealMagic,
            Stats = [new(4, -255), new(3, -255), new(7, -316), new(5, -255), new(6, -200), new(1, -3000), new(32, -1000), new(31, 1000), new(46, 9001), new(43, 9001)],
            Spells = [Effect(0, "Teleports the caster to and from the spires atop the Violet Citadel.")], Description = "A ring created by Zael to limit his abilities"
        },
            "Zael's Ring of Suppression", "Binds to account", "Unique", "Finger", "-255 Strength", "-255 Agility", "-316 Stamina", "-255 Intellect", "-200 Spirit", "-3000 Health",
            "Blood Elf Paladin Only", "Requires Level 80", "Requires Steal Magic",
            "Equip: Improves critical strike rating by -1000.", "Equip: Improves hit rating by 1000.", "Equip: Restores 9001 health per 5 sec.", "Equip: Restores 9001 mana per 5 sec.",
            "Use: Teleports the caster to and from the spires atop the Violet Citadel.", "\"A ring created by Zael to limit his abilities\"");

        Check("Zael's Pole", Item("Zael's Pole", 5, 2, 20, 17, 1) with
        {
            MaxCount = 1, DamageMin = 500, DamageMax = 700, Delay = 3000, RequiredSkill = 356, RequiredSkillRank = 1,
            Spells = [Effect(1, "Fishing skill increased by 1000."), Effect(1, "Allows underwater breathing."), Effect(0, "Right Click to set up a comfy fishing chair.", 60000), Effect(2, "Hurls a watery bolt at an enemy.")],
            Description = "Just holding this fishing pole makes you shiver with excitement."
        },
            "Zael's Pole", "Binds when picked up", "Unique", "Two-Hand | Fishing Pole", "500 - 700 Damage | Speed 3.00", "(200.0 damage per second)", "Requires Fishing (1)",
            "Equip: Fishing skill increased by 1000.", "Equip: Allows underwater breathing.", "Use: Right Click to set up a comfy fishing chair. (1 Min Cooldown)",
            "Chance on hit: Hurls a watery bolt at an enemy.", "\"Just holding this fishing pole makes you shiver with excitement.\"");

        Check("Zael's \"Replica\" Ashbringer", Item("Zael's \"Replica\" Ashbringer", 5, 2, 8, 17, 1) with
        {
            MaxCount = 1, DamageMin = 333, DamageMax = 444, Delay = 3333, DamageMin2 = 777, DamageMax2 = 777, DamageType2 = 1, RequiredLevel = 60,
            Stats = [new(4, 384), new(7, 384), new(5, 384), new(32, 383), new(36, 256), new(45, 572), new(31, 648)],
            Resistances = [0, 200, 100, 200, 255, 100], SocketColors = [2, 2, 2], SocketBonus = 3313,
            Spells = [Effect(2, "Blasts a target for 700 Fire damage."), Effect(1, "Chance on melee and ranged critical strike to increase your attack power by 1284 for 10 secs.")],
            Description = "Zael's replica, it turned out more powerful than the original."
        },
            "Zael's \"Replica\" Ashbringer", "Binds when picked up", "Unique", "Two-Hand | Sword", "333 - 444 Damage | Speed 3.33", "+777 Holy Damage", "(349.7 damage per second)",
            "+384 Strength", "+384 Stamina", "+384 Intellect", "+200 Fire Resistance", "+100 Nature Resistance", "+200 Frost Resistance", "+255 Shadow Resistance", "+100 Arcane Resistance",
            "Red Socket", "Red Socket", "Red Socket", "Socket Bonus: +10 All Stats", "Requires Level 60",
            "Equip: Improves critical strike rating by 383.", "Equip: Improves haste rating by 256.", "Equip: Increases spell power by 572.", "Equip: Improves hit rating by 648.",
            "Chance on hit: Blasts a target for 700 Fire damage.", "Equip: Chance on melee and ranged critical strike to increase your attack power by 1284 for 10 secs.",
            "\"Zael's replica, it turned out more powerful than the original.\"");

        Check("Zael's Sleeper Shield", Item("Zael's Sleeper Shield", 1, 4, 6, 14, 0) with
        {
            Armor = 32767, Block = 32767, Resistances = [255, 255, 255, 255, 255, 255], ItemLevel = 5, Stats = [new(37, 15000), new(35, 15000)]
        },
            "Zael's Sleeper Shield", "Off Hand | Shield", "32767 Armor", "32767 Block", "+255 to All Resistances", "Item Level 5",
            "Equip: Increases your expertise rating by 15000.", "Equip: Improves your resilience rating by 15000.");

        Check("Traveler's Backpack", Item("Traveler's Backpack", 2, 1, 0, 18, 0) with
        {
            ContainerSlots = 16, Description = "This item is bound to your soul, and will not be lost on death so long as it is equipped."
        },
            "Traveler's Backpack", "16 Slot Bag", "\"This item is bound to your soul, and will not be lost on death so long as it is equipped.\"");

        Check("Netherweave Bag", Item("Netherweave Bag", 2, 1, 0, 18, 2) with { ContainerSlots = 16, ItemLevel = 63 },
            "Netherweave Bag", "Binds when equipped", "16 Slot Bag");

        var colors = ItemTooltipBuilder.Build(Item("Colors", 4, 4, 0, 11, 1) with { Flags = ItemTooltipBuilder.FlagHeroic, Stats = [new(32, 1)], SocketColors = [8], SocketBonus = 1, Description = "x" }, context);
        Require(colors[0].Color == "#A335EE" && colors[0].Header, "Item name must use the quality color and header font.");
        Require(colors.Single(line => line.Left == "Heroic").Color == ItemTooltipBuilder.Green, "Heroic must be green.");
        Require(colors.Single(line => line.Left == "Blue Socket") is { Color: ItemTooltipBuilder.Gray, SocketColor: 8 }, "An empty socket must be a gray socket row.");
        Require(colors.Single(line => line.Left.StartsWith("Socket Bonus")).Color == ItemTooltipBuilder.Gray, "An inactive socket bonus must be gray.");
        Require(colors.Single(line => line.Left.StartsWith("Equip:")).Color == ItemTooltipBuilder.Green, "Equip lines must be green.");
        Require(colors[^1].Color == ItemTooltipBuilder.Gold, "The flavor description must be gold.");

        var unresolved = ItemTooltipBuilder.Build(Item("Unresolved", 1, 2, 7, 13, 0) with { Spells = [new(54321, 1, 0, 0, -1, 0, -1)] });
        Require(unresolved.Any(line => line.Left == "Equip: Spell #54321"), "A spell without loaded Spell.dbc text must still show its ID.");

        var ammo = ItemTooltipBuilder.Build(Item("Zael's BFG Rounds", 5, 6, 3, 24, 0) with { DamageMin = 500000, DamageMax = 500000 });
        Require(ammo.Any(line => line.Left == "Adds 500000 damage per second") && !ammo.Any(line => line.Left.EndsWith(" Damage")), "Ammo must use AMMO_DAMAGE_TEMPLATE.");

        var allResistances = ItemTooltipBuilder.Build(Item("Resists", 3, 4, 0, 2, 0) with { Resistances = [0, 10, 10, 10, 10, 10] });
        Require(allResistances.Any(line => line.Left == "+10 to All Resistances"), "Equal resistances must use ITEM_RESIST_ALL.");

        // Spell text as the 3.3.5 client prints it (Zael's Pole and Thunderfury screenshots).
        var effects = new Dictionary<(uint, string), object>
        {
            [(1, "EffectBasePoints[0]")] = 8749, [(1, "EffectDieSides[0]")] = 2501,
            [(2, "EffectBasePoints[0]")] = -26, [(2, "EffectDieSides[0]")] = 1, [(2, "EffectBasePoints[1]")] = -21, [(2, "EffectDieSides[1]")] = 1
        };
        var renderer = new SpellDescriptionRenderer((id, column) => effects.GetValueOrDefault((id, column)), (_, _, _) => null);
        Require(renderer.Render("inflicting $s1 Frost damage.", 1).Text == "inflicting 8750 to 11250 Frost damage.", "A rolled spell effect must show its full range.");
        Require(renderer.Render("Nature resistance by $s1. Attack speed by $s2%.", 2).Text == "Nature resistance by 25. Attack speed by 20%.", "Spell descriptions must show effect magnitudes without a sign.");

        CheckAdapterRoundTrip();
        Console.WriteLine("Item tooltip checks passed.");

        void Check(string label, ItemDraft item, params string[] expected)
        {
            var actual = ItemTooltipBuilder.Build(item, context).Select(line => line.Right is null ? line.Left : $"{line.Left} | {line.Right}").ToArray();
            for (var index = 0; index < Math.Max(actual.Length, expected.Length); index++)
            {
                var want = index < expected.Length ? expected[index] : "<no line>";
                var got = index < actual.Length ? actual[index] : "<no line>";
                if (want != got)
                    throw new InvalidOperationException($"{label} tooltip line {index + 1}: expected \"{want}\", got \"{got}\".\nFull tooltip:\n  {string.Join("\n  ", actual)}");
            }
        }
    }

    private static void CheckAdapterRoundTrip()
    {
        var draft = new ItemDraft(900002, "Round trip", 2, 8, 1, 4, 17, 200, 80, 0, 0, 1, 0, 0, 10, 20, 3000, 100, string.Empty,
            DamageMin2: 5, DamageMax2: 9, DamageType2: 2, Block: 7, Resistances: [1, 2, 3, 4, 5, 6], SocketColors: [2, 4, 8], SocketBonus: 3312,
            MaxCount: 1, AllowableClass: 2, AllowableRace: 512, RequiredSkill: 356, RequiredSkillRank: 1, RequiredSpell: 30449, StartQuest: 123, ContainerSlots: 16);
        var plan = ItemTemplateAdapter.CreatePlan(draft, ItemTemplateAdapter.CreatePortableTable());
        var expected = new Dictionary<string, object>
        {
            ["dmg_min2"] = 5f, ["dmg_max2"] = 9f, ["dmg_type2"] = 2, ["block"] = 7u, ["holy_res"] = 1, ["fire_res"] = 2, ["nature_res"] = 3, ["frost_res"] = 4,
            ["shadow_res"] = 5, ["arcane_res"] = 6, ["socketColor_1"] = 2, ["socketColor_2"] = 4, ["socketColor_3"] = 8, ["socketBonus"] = 3312u, ["maxcount"] = 1,
            ["AllowableClass"] = 2, ["AllowableRace"] = 512, ["RequiredSkill"] = 356u, ["RequiredSkillRank"] = 1u, ["requiredspell"] = 30449u, ["startquest"] = 123u, ["ContainerSlots"] = 16u
        };
        foreach (var (column, value) in expected)
            Require(plan.Values.TryGetValue(column, out var actual) && actual.Equals(value), $"Item plan wrote {column} = {(plan.Values.TryGetValue(column, out var found) ? found : "<missing>")}, expected {value}.");
        Require(plan.OmittedFields.Count == 0, "The portable item_template must represent every guided field.");

        var defaults = ItemTemplateAdapter.CreatePlan(draft with { AllowableClass = -1, AllowableRace = -1, Resistances = null, SocketColors = null }, ItemTemplateAdapter.CreatePortableTable());
        Require(defaults.Values["AllowableClass"] is -1 && defaults.Values["AllowableRace"] is -1 && defaults.Values["fire_res"] is 0 && defaults.Values["socketColor_1"] is 0,
            "Unrestricted class/race masks must stay -1 and absent resistances/sockets must write 0.");

        var narrow = new DatabaseTableCapability("item_template", ItemTemplateAdapter.CreatePortableTable().Columns.Where(column => !column.Name.StartsWith("socket", StringComparison.OrdinalIgnoreCase)).ToArray());
        var narrowPlan = ItemTemplateAdapter.CreatePlan(draft, narrow);
        Require(narrowPlan.SelectedOmittedFields.Contains("socketColor_1") && narrowPlan.SelectedOmittedFields.Contains("socketBonus"),
            "Configured sockets must block a write to a schema without socket columns instead of being dropped silently.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
