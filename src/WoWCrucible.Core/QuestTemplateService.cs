using System.Globalization;

namespace WoWCrucible.Core;

public sealed record QuestTypeDefinition(int Value, string Name) { public override string ToString() => $"{Name} [{Value}]"; }
public sealed record QuestFlagDefinition(uint Value, string Name, string Meaning);
public sealed record QuestEndpointLinks(IReadOnlyList<uint>? CreatureStarters = null, IReadOnlyList<uint>? CreatureEnders = null,
    IReadOnlyList<uint>? GameObjectStarters = null, IReadOnlyList<uint>? GameObjectEnders = null);
public sealed record QuestPortableDraft(IReadOnlyDictionary<string, string?> Values, QuestEndpointLinks? Links = null);

public static class QuestSemanticCatalog
{
    public static IReadOnlyList<QuestTypeDefinition> Types { get; } =
    [
        new(0, "Enabled, auto-completes on acceptance"), new(1, "Disabled / unavailable"), new(2, "Enabled with normal objectives")
    ];

    public static IReadOnlyList<QuestFlagDefinition> Flags { get; } =
    [
        new(0x00000001, "Stay alive", "Legacy/unused by the stock core"), new(0x00000002, "Party accept", "Offers confirmation to eligible party members"),
        new(0x00000004, "Exploration", "Exploration objective behavior"), new(0x00000008, "Sharable", "Players may share this quest"),
        new(0x00000010, "Has condition", "Legacy condition marker"), new(0x00000020, "Hide reward POI", "Hides reward point-of-interest"),
        new(0x00000040, "Raid", "Raid quest marker"), new(0x00000080, "TBC", "Requires Burning Crusade content"),
        new(0x00000100, "No max-level XP money", "Do not convert experience to money at max level"), new(0x00000200, "Hidden rewards", "Hide item/money rewards until reward offer"),
        new(0x00000400, "Tracking", "Automatically rewarded and hidden from the quest log"), new(0x00000800, "Deprecated reputation", "Legacy reputation behavior"),
        new(0x00001000, "Daily", "Repeatable once per day"), new(0x00002000, "Forces PvP", "Carrying the quest forces PvP flag"),
        new(0x00004000, "Unavailable", "Not generically available"), new(0x00008000, "Weekly", "Repeatable once per week"),
        new(0x00010000, "Auto complete", "Quest may complete automatically"), new(0x00020000, "Display item in tracker", "Shows the usable quest item in the tracker"),
        new(0x00040000, "Objective text as complete text", "Uses objective text for completion"), new(0x00080000, "Auto accept", "Client-recognized auto-accept flag; unused by stock 3.3.5 quests")
    ];

    public static string DescribeField(string name) => name switch
    {
        "ID" => "Unique quest ID. Existing IDs are never replaced implicitly.",
        "QuestType" => "Quest category shown by the client (normal, elite, dungeon, raid, etc.).",
        "QuestLevel" => "Displayed quest level; -1 scales to the player in supported core paths.",
        "MinLevel" => "Minimum player level required to accept the quest.",
        "QuestSortID" => "Positive values are area IDs; negative values select a profession/class quest sort.",
        "QuestInfoID" => "QuestInfo.dbc category such as group, class, profession, or PvP.",
        "Flags" => "Bitmask decoded in the Quest flags panel.",
        "RequiredNpcOrGo1" or "RequiredNpcOrGo2" or "RequiredNpcOrGo3" or "RequiredNpcOrGo4" => "Positive ID means creature; negative ID means gameobject.",
        "RewardNextQuest" => "Quest offered after this one is rewarded.",
        "RewardSpell" => "Spell cast/learned by the server when rewarded.",
        "RewardDisplaySpell" => "Spell visual displayed in the reward UI.",
        "AllowableRaces" => "Race bitmask; 0 means no race restriction on current AzerothCore.",
        "VerifiedBuild" => "Source client build evidence; use 0 for custom content unless you have exact provenance.",
        _ when name.StartsWith("RewardChoiceItemID", StringComparison.OrdinalIgnoreCase) => "One of up to six player-selected reward items.",
        _ when name.StartsWith("RewardItem", StringComparison.OrdinalIgnoreCase) => "Guaranteed reward item.",
        _ when name.StartsWith("RequiredItemId", StringComparison.OrdinalIgnoreCase) => "Item required for an objective.",
        _ when name.StartsWith("ObjectiveText", StringComparison.OrdinalIgnoreCase) => "Client-visible text for the corresponding objective slot.",
        _ => string.Empty
    };
}

public static class QuestTemplateAdapter
{
    private static readonly string[] PortableColumns =
    [
        "ID","QuestType","QuestLevel","MinLevel","QuestSortID","QuestInfoID","SuggestedGroupNum","RequiredFactionId1","RequiredFactionId2","RequiredFactionValue1","RequiredFactionValue2","RewardNextQuest","RewardXPDifficulty","RewardMoney","RewardMoneyDifficulty","RewardDisplaySpell","RewardSpell","RewardHonor","RewardKillHonor","StartItem","Flags","RequiredPlayerKills",
        "RewardItem1","RewardAmount1","RewardItem2","RewardAmount2","RewardItem3","RewardAmount3","RewardItem4","RewardAmount4","ItemDrop1","ItemDropQuantity1","ItemDrop2","ItemDropQuantity2","ItemDrop3","ItemDropQuantity3","ItemDrop4","ItemDropQuantity4",
        "RewardChoiceItemID1","RewardChoiceItemQuantity1","RewardChoiceItemID2","RewardChoiceItemQuantity2","RewardChoiceItemID3","RewardChoiceItemQuantity3","RewardChoiceItemID4","RewardChoiceItemQuantity4","RewardChoiceItemID5","RewardChoiceItemQuantity5","RewardChoiceItemID6","RewardChoiceItemQuantity6",
        "POIContinent","POIx","POIy","POIPriority","RewardTitle","RewardTalents","RewardArenaPoints","RewardFactionID1","RewardFactionValue1","RewardFactionOverride1","RewardFactionID2","RewardFactionValue2","RewardFactionOverride2","RewardFactionID3","RewardFactionValue3","RewardFactionOverride3","RewardFactionID4","RewardFactionValue4","RewardFactionOverride4","RewardFactionID5","RewardFactionValue5","RewardFactionOverride5","TimeAllowed","AllowableRaces",
        "LogTitle","LogDescription","QuestDescription","AreaDescription","QuestCompletionLog","RequiredNpcOrGo1","RequiredNpcOrGo2","RequiredNpcOrGo3","RequiredNpcOrGo4","RequiredNpcOrGoCount1","RequiredNpcOrGoCount2","RequiredNpcOrGoCount3","RequiredNpcOrGoCount4",
        "RequiredItemId1","RequiredItemId2","RequiredItemId3","RequiredItemId4","RequiredItemId5","RequiredItemId6","RequiredItemCount1","RequiredItemCount2","RequiredItemCount3","RequiredItemCount4","RequiredItemCount5","RequiredItemCount6","Unknown0","ObjectiveText1","ObjectiveText2","ObjectiveText3","ObjectiveText4","VerifiedBuild"
    ];

    public static DatabaseTableCapability CreatePortableTable() => new("quest_template", PortableColumns.Select((name, index) =>
    {
        var text = name is "LogTitle" or "LogDescription" or "QuestDescription" or "AreaDescription" or "QuestCompletionLog" || name.StartsWith("ObjectiveText", StringComparison.Ordinal);
        var floating = name is "POIx" or "POIy" or "RewardKillHonor";
        return new DatabaseColumnCapability(name, text ? "text" : floating ? "float" : "int", text ? "text" : floating ? "float" : "int", text || name == "VerifiedBuild", text || name == "VerifiedBuild" ? null : "0", name == "ID" ? "PRI" : string.Empty, string.Empty, index + 1);
    }).ToArray());

    public static IReadOnlyDictionary<string, object?> CreateDefaultValues(DatabaseTableCapability table)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in table.Columns.Where(column => !column.Extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase)))
            values[column.Name] = column.DefaultValue ?? (column.Nullable ? null : IsText(column) ? string.Empty : "0");
        Set(values, table, "ID", 900000u); Set(values, table, "QuestType", 2); Set(values, table, "QuestLevel", 1); Set(values, table, "MinLevel", 1); Set(values, table, "LogTitle", "New Crucible Quest"); Set(values, table, "VerifiedBuild", 0);
        return values;
    }

    public static WorldContentWritePlan CreatePlan(DatabaseTableCapability questTable, IReadOnlyDictionary<string, object?> supplied,
        DatabaseCapabilities capabilities, QuestEndpointLinks? links = null)
    {
        var unknown = supplied.Keys.Where(name => questTable.Find(name) is null).ToArray(); if (unknown.Length > 0) throw new InvalidDataException($"Unknown quest column(s): {string.Join(", ", unknown)}.");
        var values = supplied.Where(pair => questTable.Find(pair.Key)?.Extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase) != true).ToDictionary(pair => questTable.Find(pair.Key)!.Name, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var id = Unsigned(values.GetValueOrDefault(questTable.Find("ID")?.Name ?? "ID"), "ID"); if (id == 0) throw new InvalidDataException("Quest ID must be nonzero.");
        var titleColumn = questTable.Find("LogTitle")?.Name; if (titleColumn is not null && string.IsNullOrWhiteSpace(Convert.ToString(values.GetValueOrDefault(titleColumn), CultureInfo.InvariantCulture))) throw new InvalidDataException("Quest LogTitle is required for a usable quest.");
        var minimum = Integer(values, questTable, "MinLevel"); var level = Integer(values, questTable, "QuestLevel"); if (minimum < 0 || minimum > 255 || level < -1) throw new InvalidDataException("MinLevel must be 0–255 and QuestLevel must be -1 or greater.");
        var rows = new List<WorldSqlRowPlan> { new(questTable.Name, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { [questTable.Find("ID")?.Name ?? throw new NotSupportedException("quest_template has no ID column.")] = id }, values) };
        links ??= new(); AddLinks(rows, capabilities, "creature_queststarter", id, links.CreatureStarters); AddLinks(rows, capabilities, "creature_questender", id, links.CreatureEnders); AddLinks(rows, capabilities, "gameobject_queststarter", id, links.GameObjectStarters); AddLinks(rows, capabilities, "gameobject_questender", id, links.GameObjectEnders);
        return new("Quest template", rows, []);
    }

    public static string Group(string name)
    {
        if (name is "ID" or "QuestType" or "QuestLevel" or "MinLevel" or "QuestSortID" or "QuestInfoID" or "SuggestedGroupNum" or "Flags" or "AllowableRaces" or "TimeAllowed" or "VerifiedBuild") return "Identity & behavior";
        if (name is "LogTitle" or "LogDescription" or "QuestDescription" or "AreaDescription" or "QuestCompletionLog" || name.StartsWith("ObjectiveText", StringComparison.OrdinalIgnoreCase)) return "Quest text";
        if (name.StartsWith("RequiredNpcOrGo", StringComparison.OrdinalIgnoreCase) || name.StartsWith("RequiredItem", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ItemDrop", StringComparison.OrdinalIgnoreCase) || name is "RequiredPlayerKills" or "StartItem") return "Objectives";
        if (name.StartsWith("Reward", StringComparison.OrdinalIgnoreCase)) return "Rewards";
        if (name.StartsWith("Required", StringComparison.OrdinalIgnoreCase)) return "Requirements";
        return "Advanced & POI";
    }

    private static void AddLinks(ICollection<WorldSqlRowPlan> rows, DatabaseCapabilities capabilities, string tableName, uint quest, IReadOnlyList<uint>? sources)
    {
        var ids = (sources ?? []).Where(id => id != 0).Distinct().ToArray(); if (ids.Length == 0) return; var table = capabilities.FindTable(tableName) ?? throw new NotSupportedException($"{tableName} is unavailable in the target schema."); var idColumn = table.Find("id")?.Name ?? throw new NotSupportedException($"{tableName} has no id column."); var questColumn = table.Find("quest")?.Name ?? throw new NotSupportedException($"{tableName} has no quest column.");
        foreach (var source in ids) { var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { [idColumn] = source, [questColumn] = quest }; rows.Add(new(table.Name, values, values)); }
    }

    private static bool IsText(DatabaseColumnCapability column) => column.DataType.Contains("char", StringComparison.OrdinalIgnoreCase) || column.DataType.Contains("text", StringComparison.OrdinalIgnoreCase) || column.DataType.Contains("blob", StringComparison.OrdinalIgnoreCase);
    private static void Set(IDictionary<string, object?> values, DatabaseTableCapability table, string name, object value) { if (table.Find(name) is { } column) values[column.Name] = value; }
    private static long Integer(IReadOnlyDictionary<string, object?> values, DatabaseTableCapability table, string name) { var column = table.Find(name); if (column is null || !values.TryGetValue(column.Name, out var value) || value is null) return 0; return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
    private static uint Unsigned(object? value, string name) { try { return Convert.ToUInt32(value, CultureInfo.InvariantCulture); } catch (Exception exception) { throw new InvalidDataException($"{name} must be a nonnegative integer.", exception); } }
}
