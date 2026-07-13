using System.Globalization;

namespace WoWCrucible.Core;

public enum SemanticKind { Enum, Flags }

public sealed record SemanticOption(uint Value, string Name);
public sealed record SemanticField(string Label, SemanticKind Kind, IReadOnlyList<SemanticOption> Options)
{
    public string Format(uint raw)
    {
        if (Kind == SemanticKind.Enum)
            return Options.FirstOrDefault(option => option.Value == raw) is { } match ? $"{match.Name} [{raw}]" : $"Unknown [{raw}]";
        if (raw == 0) return "None [0x00000000]";
        var names = Options.Where(option => option.Value != 0 && (raw & option.Value) == option.Value).Select(option => option.Name).ToArray();
        var knownMask = Options.Aggregate(0u, (mask, option) => mask | option.Value);
        var unknown = raw & ~knownMask;
        var readable = names.ToList();
        if (unknown != 0) readable.Add($"Unknown 0x{unknown:X8}");
        return $"{string.Join(" | ", readable)} [0x{raw:X8}]";
    }

    public uint Parse(string text)
    {
        text = text.Trim();
        var bracket = text.LastIndexOf('[');
        if (bracket >= 0 && text.EndsWith(']')) text = text[(bracket + 1)..^1].Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return uint.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric)) return numeric;
        uint value = 0;
        foreach (var name in text.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var option = Options.FirstOrDefault(candidate => candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (option is null) throw new FormatException($"Unknown {Label} value: {name}");
            if (Kind == SemanticKind.Enum) return option.Value;
            value |= option.Value;
        }
        return value;
    }
}

public static class DbcSemanticCatalog
{
    private static readonly Dictionary<(string Table, int Column), SemanticField> Fields = new();
    private static readonly DbcColumn ItemClassColumn = new(1, 4, 4, "ClassID", DbcValueType.Int32);

    static DbcSemanticCatalog()
    {
        Enum("Item", 1, "Item class", "0:Consumable,1:Container,2:Weapon,3:Gem,4:Armor,5:Reagent,6:Projectile,7:Trade Goods,8:Generic,9:Recipe,10:Money,11:Quiver,12:Quest,13:Key,14:Permanent,15:Miscellaneous,16:Glyph");
        Enum("Item", 4, "Material", "4294967295:Consumable,0:None,1:Metal,2:Wood,3:Liquid,4:Jewelry,5:Chain,6:Plate,7:Cloth,8:Leather");
        Enum("Item", 6, "Inventory slot", "0:Non-equippable,1:Head,2:Neck,3:Shoulders,4:Shirt,5:Chest,6:Waist,7:Legs,8:Feet,9:Wrists,10:Hands,11:Finger,12:Trinket,13:One-hand weapon,14:Shield,15:Ranged,16:Back,17:Two-hand weapon,18:Bag,19:Tabard,20:Robe,21:Main hand,22:Off hand,23:Held in off hand,24:Ammo,25:Thrown,26:Ranged right,27:Quiver,28:Relic");
        Enum("Item", 7, "Sheathe type", "0:None,1:Two-hand weapon,2:Staff,3:One-hand weapon,4:Shield,5:Enchanter rod,6:Off hand,7:Ranged");

        Enum("Spell", 2, "Dispel type", "0:None,1:Magic,2:Curse,3:Disease,4:Poison,5:Stealth,6:Invisibility,7:All,8:Special NPC only,9:Frenzy,10:Zul'Gurub ticket");
        Enum("Spell", 3, "Mechanic", "0:None,1:Charm,2:Disorient,3:Disarm,4:Distract,5:Fear,6:Grip,7:Root,8:Slow attack,9:Silence,10:Sleep,11:Snare,12:Stun,13:Freeze,14:Knockout,15:Bleed,16:Bandage,17:Polymorph,18:Banish,19:Shield,20:Shackle,21:Mount,22:Infected,23:Turn,24:Horror,25:Invulnerability,26:Interrupt,27:Daze,28:Discovery,29:Immune shield,30:Sapped,31:Enraged");
        Enum("Spell", 31, "Prevention type", "0:None,1:Silence,2:Pacify");
        Enum("Spell", 41, "Power type", "0:Mana,1:Rage,2:Focus,3:Energy,4:Happiness,5:Rune,6:Runic power,4294967294:Health");
        for (var column = 71; column <= 73; column++) Enum("Spell", column, "Spell effect", SpellEffects);
        for (var column = 86; column <= 91; column++) Enum("Spell", column, "Implicit target", ImplicitTargets);
        for (var column = 95; column <= 97; column++) Enum("Spell", column, "Aura type", AuraTypes);
        Enum("Spell", 206, "Damage class", "0:None,1:Magic,2:Melee,3:Ranged");
        Enum("Spell", 207, "Spell family", "0:Generic,3:Mage,4:Warrior,5:Warlock,6:Priest,7:Druid,8:Rogue,9:Hunter,10:Paladin,11:Shaman,13:Potion,15:Death Knight,53:Pet");
        Flags("Spell", 225, "School mask", "1:Physical,2:Holy,4:Fire,8:Nature,16:Frost,32:Shadow,64:Arcane");
        Flags("Spell", 4, "Spell attributes", "1:Proc failure burns charge,2:Uses ranged slot,4:On next melee,16:Ability,32:Trade skill,64:Passive,128:Hidden in UI,256:Hidden in combat log,512:Held item only,1024:On next melee type 2,16384:Only indoors,32768:Only outdoors,65536:Not while shapeshifted,131072:Only while stealthed,1048576:Stop auto-attack,2097152:Cannot dodge/parry/block,8388608:Cast while dead,16777216:Cast while mounted,33554432:Cooldown starts on expiry,67108864:Negative aura,134217728:Cast while sitting,268435456:Cannot use in combat,536870912:Pierce invulnerability,2147483648:Aura cannot be cancelled");
        Flags("Spell", 5, "Extended attributes 1", "1:Dismiss pet first,2:Use all mana,4:Channeled,8:Ignore redirection,32:Does not break stealth,64:Self channeled,128:Cannot be reflected,256:Target out of combat,512:Starts auto-attack,1024:No threat,2048:Aura unique,8192:Farsight,16384:Track target while channeling,524288:Cannot self-cast,1048576:Finishing move damage,4194304:Finishing move duration,268435456:Hide aura icon,536870912:Show name in channel bar,2147483648:Cast when learned");
        Flags("Spell", 6, "Extended attributes 2", "1:Can target dead,4:Ignore line of sight,8:Ignore aura scaling,16:Show in stance bar,32:Auto-repeat,64:Cannot target tapped unit,4096:Chain from caster,8192:Enchant own item only,16384:Cast while invisible,65536:Target not in combat,131072:Auto attack,262144:Cannot target self,524288:Health funnel,1048576:Tame beast,8388608:Do not reset combat timers,16777216:Requires dead pet,33554432:Allow while not shapeshifted,67108864:Initiate combat post cast,1073741824:Food buff");
        Flags("Spell", 7, "Extended attributes 3", "1:Blockable spell,2:Ignore resurrection timer,4:Stack separately by caster,8:Only target players,16:Triggered can trigger proc,32:Main-hand weapon required,64:Battle resurrection,128:No initial aggro,256:Cannot miss,512:Retain item cast,1024:Death persistent,2048:Requires wand,4096:Requires offhand weapon,32768:Can proc from triggered,65536:Drain soul,262144:Only battlegrounds,1048576:Only target ghosts,2097152:Hide channel bar,4194304:Hide in raid filter,16777216:Always hit,67108864:No caster bonuses,134217728:No target bonuses,536870912:Offhand attack,1073741824:Do not display range");
        for (var column = 8; column <= 11; column++) Flags("Spell", column, $"Extended attributes {column - 4}", GenericBits);
    }

    public static SemanticField? Get(string table, int column, WdbcFile? file = null, int row = -1)
    {
        if (table.Equals("Item", StringComparison.OrdinalIgnoreCase) && column == 2 && file is not null && row >= 0)
            return ItemSubclass(file.GetRaw(row, ItemClassColumn));
        return Fields.GetValueOrDefault((table, column));
    }

    public static IReadOnlyList<int> GetColumns(string table) => Fields.Keys.Where(key => key.Table.Equals(table, StringComparison.OrdinalIgnoreCase)).Select(key => key.Column).Distinct().ToArray();

    private static SemanticField ItemSubclass(uint itemClass) => itemClass switch
    {
        2 => MakeEnum("Weapon subtype", "0:Axe (one-hand),1:Axe (two-hand),2:Bow,3:Gun,4:Mace (one-hand),5:Mace (two-hand),6:Polearm,7:Sword (one-hand),8:Sword (two-hand),10:Staff,13:Fist weapon,14:Miscellaneous,15:Dagger,16:Thrown,18:Crossbow,19:Wand,20:Fishing pole"),
        4 => MakeEnum("Armor subtype", "0:Miscellaneous,1:Cloth,2:Leather,3:Mail,4:Plate,6:Shield,7:Libram,8:Idol,9:Totem,10:Sigil"),
        15 => MakeEnum("Misc subtype", "0:Junk,1:Reagent,2:Companion pet,3:Holiday,4:Other,5:Mount"),
        _ => MakeEnum("Item subtype", "0:Subtype 0")
    };

    private static void Enum(string table, int column, string label, string values) => Fields[(table, column)] = MakeEnum(label, values);
    private static void Flags(string table, int column, string label, string values) => Fields[(table, column)] = new(label, SemanticKind.Flags, Parse(values));
    private static SemanticField MakeEnum(string label, string values) => new(label, SemanticKind.Enum, Parse(values));
    private static SemanticOption[] Parse(string values) => values.Split(',').Select(value => { var separator = value.IndexOf(':'); return new SemanticOption(uint.Parse(value[..separator], CultureInfo.InvariantCulture), value[(separator + 1)..]); }).ToArray();

    private const string GenericBits = "1:Bit 0,2:Bit 1,4:Bit 2,8:Bit 3,16:Bit 4,32:Bit 5,64:Bit 6,128:Bit 7,256:Bit 8,512:Bit 9,1024:Bit 10,2048:Bit 11,4096:Bit 12,8192:Bit 13,16384:Bit 14,32768:Bit 15,65536:Bit 16,131072:Bit 17,262144:Bit 18,524288:Bit 19,1048576:Bit 20,2097152:Bit 21,4194304:Bit 22,8388608:Bit 23,16777216:Bit 24,33554432:Bit 25,67108864:Bit 26,134217728:Bit 27,268435456:Bit 28,536870912:Bit 29,1073741824:Bit 30,2147483648:Bit 31";
    private const string SpellEffects = "0:None,1:Instakill,2:School damage,3:Dummy,5:Teleport units,6:Apply aura,8:Power drain,9:Health leech,10:Heal,16:Quest complete,17:Weapon damage,18:Resurrect,24:Create item,27:Persistent area aura,28:Summon,30:Energize,33:Open lock,35:Apply area aura party,36:Learn spell,38:Dispel,41:Jump,42:Jump destination,50:Trans door,53:Enchant item permanent,54:Enchant item temporary,56:Summon pet,58:Weapon damage percent,59:Open lock item,64:Trigger spell,68:Interrupt cast,69:Distract,70:Pull,71:Pickpocket,72:Add farsight,74:Apply glyph,76:Summon object,77:Script effect,80:Add combo points,83:Duel,85:Summon player,87:WMO damage,88:WMO repair,90:Kill credit,91:Threat,94:Self resurrect,95:Skinning,96:Charge,98:Knock back,101:Feed pet,103:Reputation,104:Summon object slot 1,105:Summon object slot 2,106:Summon object slot 3,107:Summon object slot 4,109:Dispel mechanic,113:Resurrect pet,115:Destroy all totems,116:Durability damage,119:Apply area aura pet,121:Normalized weapon damage,124:Pull toward,126:Steal beneficial buff,127:Prospecting,128:Apply area aura friend,129:Apply area aura enemy,130:Redirect threat,134:Kill credit 2,136:Heal percent,137:Energize percent,138:Leap back,140:Force cast,141:Force cast with value,142:Trigger spell with value,143:Apply area aura owner,145:Pull toward destination,146:Activate rune,147:Quest fail,148:Trigger missile,149:Charge destination,151:Trigger ritual,152:Summon RAF friend,153:Create tamed pet,154:Discover taxi,155:Titan grip,156:Enchant item prismatic,157:Create item 2,158:Milling,159:Rename pet,160:Force cast 2";
    private const string ImplicitTargets = "0:None,1:Self,2:Nearby enemy,3:Nearby party,4:Nearby ally,5:Pet,6:Target enemy,7:Scripted area destination,8:Home,15:All enemies around source,16:All enemies around destination,17:Destination coordinates,18:Target destination,20:All party around caster,21:Single friend,22:Caster source,24:Cone enemies,25:All enemies around caster,26:Gameobject target,27:Master,28:All enemies around target,30:All friendly around caster,31:All friendly around target,32:Minion,33:All party around target,35:Party class around target,37:Last selected party,38:Nearby entry,39:Destination front,40:Destination back,41:Destination right,42:Destination left,45:Chain heal,46:Script coordinates,47:Dynamic object front,48:Dynamic object back,49:Dynamic object right,50:Dynamic object left,52:Current enemy coordinates,53:Target front,54:Target back,55:Target right,56:Target left,57:Cone ally,58:Cone entry,59:Area entry source,60:Area entry destination,61:Target source,62:Target destination,63:Threat list,64:Tap list,65:Nearby destination,66:Nearby entry destination,67:Caster fishing,69:Target vehicle passenger 0,70:Target vehicle passenger 1,71:Target vehicle passenger 2,72:Target vehicle passenger 3,73:Target vehicle passenger 4,74:Target vehicle passenger 5,75:Target vehicle passenger 6,76:Target vehicle passenger 7,77:Caster vehicle passenger 0,78:Caster vehicle passenger 1,79:Caster vehicle passenger 2,80:Caster vehicle passenger 3,81:Caster vehicle passenger 4,82:Caster vehicle passenger 5,83:Caster vehicle passenger 6,84:Caster vehicle passenger 7,85:Cone enemy 2,86:Target minipet,87:Destination random,88:Destination radius,89:Vehicle,90:Target passenger,91:Passenger target,92:Destination target random,93:Noncombat pet,94:Cone enemy 3";
    private const string AuraTypes = "0:None,1:Bind sight,2:Possess,3:Periodic damage,4:Dummy,5:Confuse,6:Charm,7:Fear,8:Periodic heal,9:Mod attack speed,10:Mod threat,11:Taunt,12:Stun,13:Mod damage done,14:Mod damage taken,15:Damage shield,16:Stealth,17:Stealth detection,18:Invisibility,19:Invisibility detection,20:Periodic heal percent,21:Periodic power percent,22:Pacify,23:Root,24:Silence,25:Reflect spells,26:Mod stat,27:Mod skill,28:Mod increase speed,29:Mod decrease speed,30:Mod increase mounted speed,31:Mod decrease mounted speed,32:Mod increase health,33:Mod increase energy,34:Shapeshift,35:Effect immunity,36:State immunity,37:School immunity,38:Damage immunity,39:Dispel immunity,40:Proc trigger spell,41:Proc trigger damage,42:Track creatures,43:Track resources,44:Ignore shapeshift,45:Mod parry percent,46:Mod dodge percent,47:Mod block percent,48:Mod crit percent,49:Periodic leech,50:Mod hit chance,51:Mod spell hit chance,52:Transform,53:Mod spell crit chance,54:Mod increase swim speed,55:Mod damage done creature,56:Pacify silence,57:Mod scale,58:Periodic health funnel,59:Periodic mana funnel,60:Periodic mana leech,61:Mod casting speed,62:Feign death,63:Disarm,64:Stalked,65:School absorb,66:Extra attacks,67:Mod spell crit school,68:Mod power cost school percent,69:Mod power cost school flat,70:Reflect spells school,71:Mod language,72:Far sight,73:Mechanic immunity,74:Mounted,75:Mod damage percent done,76:Mod percent stat,77:Split damage percent,78:Water breathing,79:Mod base resistance,80:Mod health regen,81:Mod power regen,82:Channel death item,83:Mod damage percent taken,84:Mod health regen percent,85:Periodic damage percent,86:Mod resist chance,87:Mod detect range,88:Prevent fleeing,89:Mod unattackable,90:Interrupt regen,91:Ghost,92:Spell magnet,93:Mana shield,94:Mod skill talent,95:Mod attack power,96:Auras visible,97:Mod resistance percent,98:Mod melee attack power versus,99:Mod total threat,100:Water walk,101:Feather fall,102:Hover,103:Add flat modifier,104:Add percent modifier,105:Add target trigger,106:Mod power regen percent,107:Add caster hit trigger,108:Override class scripts,109:Mod ranged damage taken,110:Mod ranged damage taken percent,111:Mod healing,112:Mod regen during combat,113:Mod mechanic resistance,114:Mod healing percent,115:Share pet tracking,116:Untrackable,117:Empathy,118:Mod offhand damage percent,119:Mod target resistance,120:Mod ranged attack power,121:Mod melee damage taken,122:Mod melee damage taken percent,123:Ranged attack power attacker bonus,124:Mod possession pet,125:Mod speed always,126:Mod mounted speed always,127:Mod ranged attack power versus,128:Mod increase energy percent,129:Mod increase health percent,130:Mod mana regen interrupt,131:Mod healing done,132:Mod healing done percent,133:Mod total stat percent,134:Mod melee haste,135:Force reaction,136:Mod ranged haste,137:Mod ranged ammo haste,138:Mod base resistance percent,139:Mod resistance exclusive,140:Safe fall,141:Mod pet talent points,142:Allow tame pet type,143:Mechanic immunity mask,144:Retain combo points,145:Reduce pushback,146:Mod shield block value percent,147:Track stealthed,148:Mod detected range,149:Split damage flat,150:Mod stealth level,151:Mod water breathing,152:Mod reputation gain,153:Pet damage multi,154:Mod shield block value,155:No PVP credit,156:Mod AoE avoidance,157:Mod health regen in combat,158:Power burn,159:Mod crit damage bonus,160:Melee attack power attacker bonus,161:Mod attack power percent,162:Mod ranged attack power percent,163:Mod damage done versus,164:Mod crit percent versus,165:Detect amor,166:Mod speed not stack,167:Mod mounted speed not stack,168:Allow champion spells,169:Mod spell damage of stat percent,170:Mod spell healing of stat percent,171:Spirit of redemption,172:AoE charm,173:Mod debuff resistance,174:Mod attacker spell crit chance,175:Mod flat spell damage versus,176:Mod flat spell crit damage versus,177:Mod resistance of stat percent,178:Mod critical threat,179:Mod attacker melee hit chance,180:Mod attacker ranged hit chance,181:Mod attacker spell hit chance,182:Mod attacker melee crit chance,183:Mod attacker ranged crit chance,184:Mod rating,185:Mod faction reputation gain,186:Use normal movement speed,187:Mod melee ranged haste,188:Melee slow,189:Mod target absorb school,190:Mod target ability absorb school,191:Mod cooldown,192:Mod attacker crit chance,193:Mod all weapon skills,194:Mod increase spell hit chance,195:Mod XP percent,196:Fly,197:Ignore combat result,198:Mod attacker melee crit damage,199:Mod attacker ranged crit damage,200:Mod attacker spell crit damage,201:Mod flight speed,202:Mod flight speed mounted,203:Mod flight speed stack,204:Mod flight speed mounted stack,205:Mod flight speed not stack,206:Mod flight speed mounted not stack,207:Mod ranged attack power of stat percent,208:Mod rage from damage dealt,209:Tame pet passive,210:Arena preparation,211:Haste spells,212:Mod melee haste 2,213:Haste ranged,214:Mod mana regen from stat,215:Mod rating from stat,216:Feign death no threat,217:Mod disarm offhand,218:Mod disarm ranged,219:Mod damage done versus aura state,220:Mod fake inebriate,221:Mod minimum speed,222:Mod crit chance for caster,223:Mod resilience percent,224:Mod creature AoE damage avoidance,225:Mod increase health 2,226:Mod enemy dodge,227:Mod speed slow all,228:Mod block crit chance,229:Mod disarm,230:Mod mechanic damage taken percent,231:No reagent use,232:Mod target resistance by spell class,233:Override summon effects,234:Mod hot percent,235:Screen effect,236:Phase,237:Ability ignore aura state,238:Allow only ability,239:Mod immunity,240:Comprehend language,241:Mod duration of magic effects,242:Mod duration of effects by dispel,243:Mirror image,244:Mod combat result chance,245:Convert rune,246:Mod increase health percent 2,247:Mod enemy dodge 2,248:Mod speed slow all 2,249:Mod block crit chance 2,250:Mod disarm 2,251:Mod mechanic damage taken percent 2,252:No reagent use 2,253:Mod target resistance by spell class 2,254:Override summon effects 2,255:Mod hot percent 2";
}
