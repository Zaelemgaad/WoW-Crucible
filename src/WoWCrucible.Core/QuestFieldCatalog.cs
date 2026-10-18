using System.Text.RegularExpressions;

namespace WoWCrucible.Core;

public sealed record QuestFieldDefinition(string Label, string Description, ReferenceDomain? Reference = null);

/// <summary>Build-12340 meanings checked against CoA QuestDef.cpp, Player.cpp and the AzerothCore quest_template reference.</summary>
public static class QuestFieldCatalog
{
    public const string Documentation = "https://www.azerothcore.org/wiki/quest_template";
    private static readonly IReadOnlyDictionary<string, QuestFieldDefinition> Fields = new Dictionary<string, QuestFieldDefinition>(StringComparer.OrdinalIgnoreCase)
    {
        ["ID"] = new("Quest ID", "Unique quest identity. Use a reserved project ID for a new quest."),
        ["QuestType"] = new("Completion behavior", "Controls whether the quest is active and whether objectives are required. This is not the group/dungeon category."),
        ["QuestLevel"] = new("Quest level", "Displayed level. -1 uses the player level in level-dependent reward calculations."),
        ["MinLevel"] = new("Minimum player level", "Lowest player level allowed to accept this quest."),
        ["QuestSortID"] = new("Quest log category", "Area ID for a location category; negative QuestSort.dbc ID for a class, profession or special category. 0 means none."),
        ["QuestInfoID"] = new("Quest category", "Normal, group, dungeon, raid and other labels from QuestInfo.dbc.", ReferenceDomain.QuestInfo),
        ["SuggestedGroupNum"] = new("Suggested group size", "Suggested player count shown in the quest log; 0 means no suggestion."),
        ["RewardNextQuest"] = new("Next quest", "Quest to offer after turn-in; 0 means none.", ReferenceDomain.Quest),
        ["RewardXPDifficulty"] = new("Experience reward tier", "Index 0-9 into QuestXP.dbc for the quest level. It is a tier, not an XP amount."),
        ["RewardMoney"] = new("Money reward or cost (copper)", "Positive pays the player; negative requires payment. 100 copper = 1 silver; 10,000 copper = 1 gold."),
        ["RewardMoneyDifficulty"] = new("Money scaling tier", "CoA looks up quest_money_reward for the player level and this Difficulty value. 0 uses the fixed money reward."),
        ["RewardDisplaySpell"] = new("Spell shown in rewards", "Spell advertised in the client reward panel; 0 means none.", ReferenceDomain.Spell),
        ["RewardSpell"] = new("Spell cast on completion", "Server casts this spell on reward. Learning depends on the spell effects; 0 means none.", ReferenceDomain.Spell),
        ["RewardHonor"] = new("Honor reward", "Fixed honor contribution on reward; 0 grants none."),
        ["RewardKillHonor"] = new("Honor scaling multiplier", "Multiplier for level-dependent honor, added to the fixed honor reward."),
        ["StartItem"] = new("Item given on acceptance", "Item handed to the player at quest start; 0 means none.", ReferenceDomain.Item),
        ["Flags"] = new("Quest behavior flags", "Independent on/off behavior switches. Unknown custom bits are preserved."),
        ["RequiredPlayerKills"] = new("Enemy players to defeat", "Number of enemy player kills needed; 0 means no PvP kill objective."),
        ["POIContinent"] = new("Objective map", "Map.dbc ID for the point-of-interest marker. Map 0 is Eastern Kingdoms.", ReferenceDomain.Map),
        ["POIx"] = new("Objective map X", "Horizontal coordinate of the point-of-interest marker."),
        ["POIy"] = new("Objective map Y", "Vertical coordinate of the point-of-interest marker."),
        ["POIPriority"] = new("Objective marker priority", "Legacy client marker priority. Unused by the stock 3.3.5 core."),
        ["RewardTitle"] = new("Title reward", "Character title from CharTitles.dbc; 0 means none.", ReferenceDomain.CharacterTitle),
        ["RewardTalents"] = new("Bonus talent points", "Additional talent points awarded on completion; 0 grants none."),
        ["RewardArenaPoints"] = new("Arena point reward", "Arena points awarded on completion; 0 grants none."),
        ["TimeAllowed"] = new("Time limit (seconds)", "0 means no timer. Otherwise the quest fails when this many seconds expire."),
        ["AllowableRaces"] = new("Allowed races", "0 allows every race. Selecting races builds the mask without discarding custom race bits."),
        ["LogTitle"] = new("Quest title", "Name displayed in the quest log and by the quest giver."),
        ["LogDescription"] = new("Objective summary", "Short objective instructions displayed in the quest log."),
        ["QuestDescription"] = new("Quest giver description", "Full description shown when accepting the quest."),
        ["AreaDescription"] = new("Area / event objective text", "Text describing an area or exploration objective."),
        ["QuestCompletionLog"] = new("Completion instructions", "Text displayed in the log after the objectives are complete."),
        ["Unknown0"] = new("Unused client field", "The CoA quest loader does not read this value. Keep 0 for new quests."),
        ["VerifiedBuild"] = new("Source client build", "Provenance of the original data. Use 0 for custom quests; this is not the quest level."),
    };
    private static readonly IReadOnlyDictionary<string, QuestFieldDefinition> Slots = new Dictionary<string, QuestFieldDefinition>(StringComparer.OrdinalIgnoreCase)
    {
        ["RequiredFactionId"] = new("Reputation objective faction", "Faction whose standing is checked for completion; 0 disables this objective.", ReferenceDomain.Faction),
        ["RequiredFactionValue"] = new("Required reputation points", "Raw standing required with the matching faction, not the Friendly/Honored rank number."),
        ["RewardItem"] = new("Guaranteed reward item", "Item given on turn-in, in addition to any chosen reward; 0 disables this reward.", ReferenceDomain.Item),
        ["RewardAmount"] = new("Guaranteed reward quantity", "How many copies of the matching guaranteed item to give."),
        ["ItemDrop"] = new("Quest supply item", "Quest-related drop that is not itself a required item objective; 0 means none.", ReferenceDomain.Item),
        ["ItemDropQuantity"] = new("Quest supply limit", "Maximum copies to drop while the player holds this quest."),
        ["RewardChoiceItemID"] = new("Choice reward item", "One of the rewards the player may choose on turn-in; 0 leaves the slot empty.", ReferenceDomain.Item),
        ["RewardChoiceItemQuantity"] = new("Choice reward quantity", "How many copies to give if this reward is chosen."),
        ["RewardFactionID"] = new("Reward faction", "Faction receiving reputation on completion; 0 disables this reward.", ReferenceDomain.Faction),
        ["RewardFactionValue"] = new("Reputation reward tier", "Signed index into QuestFactionReward.dbc. Negative selects the loss row. Used only when the explicit override is 0."),
        ["RewardFactionOverride"] = new("Explicit reputation reward (hundredths)", "Overrides the tier when nonzero. CoA divides by 100: 25000 grants 250 base reputation; negative values remove reputation."),
        ["RequiredNpcOrGo"] = new("Creature or object objective", "Positive creature ID or negative gameobject ID. Choose the target kind before searching; 0 means none."),
        ["RequiredNpcOrGoCount"] = new("Creature or object objective count", "How many kills or interactions the matching objective requires."),
        ["RequiredItemId"] = new("Required objective item", "Item the player must collect; 0 leaves this objective empty.", ReferenceDomain.Item),
        ["RequiredItemCount"] = new("Required item quantity", "How many copies of the matching objective item are required."),
        ["ObjectiveText"] = new("Objective text", "Optional client-visible description for this objective slot."),
    };

    public static QuestFieldDefinition? Get(string name)
    {
        if (Fields.TryGetValue(name, out var field)) return field;
        var match = Regex.Match(name, @"^(.*?)([1-6])$", RegexOptions.CultureInvariant);
        return match.Success && Slots.TryGetValue(match.Groups[1].Value, out field)
            ? field with { Label = field.Label + " " + match.Groups[2].Value } : null;
    }

    public static IReadOnlyList<QuestTypeDefinition> Choices(string name) => name.ToLowerInvariant() switch
    {
        "questtype" => QuestSemanticCatalog.Types,
        "questinfoid" => [new(0, "Normal"), new(1, "Group / elite"), new(21, "Life"), new(41, "PvP"), new(62, "Raid"), new(81, "Dungeon"), new(82, "World event"), new(83, "Legendary"), new(84, "Escort"), new(85, "Heroic"), new(88, "10-player raid"), new(89, "25-player raid")],
        "rewardxpdifficulty" => Enumerable.Range(0, 10).Select(value => new QuestTypeDefinition(value, $"XP tier {value} (QuestXP.dbc)")).ToArray(),
        _ => []
    };

    public static IReadOnlyList<QuestFlagDefinition> Races { get; } =
    [
        new(1, "Human", ""), new(2, "Orc", ""), new(4, "Dwarf", ""), new(8, "Night elf", ""),
        new(16, "Undead", ""), new(32, "Tauren", ""), new(64, "Gnome", ""), new(128, "Troll", ""),
        new(512, "Blood elf", ""), new(1024, "Draenei", "")
    ];
}
