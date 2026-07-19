using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public sealed record CacheServerFieldPlan(string SourceField, string TargetColumn, object? Value, string SourceType);

public sealed record CacheServerRecordPlan(
    uint RecordId,
    string TargetTable,
    string TargetKeyColumn,
    IReadOnlyList<CacheServerFieldPlan> Fields,
    IReadOnlyList<string> UnmappedSourceFields,
    IReadOnlyList<string> Warnings);

public sealed record CacheServerUpdatePlan(
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    string SourcePath,
    string SourceSha256,
    string SourceDefinition,
    uint SourceBuild,
    string TargetDatabase,
    string TargetServerVersion,
    string TargetSchemaSha256,
    IReadOnlyList<CacheServerRecordPlan> Records,
    IReadOnlyList<string> Warnings)
{
    public string PreviewSql()
    {
        var builder = new StringBuilder();
        builder.AppendLine("-- WoW Crucible cache-to-server REVIEW PLAN");
        builder.AppendLine($"-- Source SHA-256: {SourceSha256}");
        builder.AppendLine($"-- Target schema SHA-256: {TargetSchemaSha256}");
        builder.AppendLine("-- UPDATE EXISTING ROWS ONLY. This preview is not an automatic import.");
        foreach (var record in Records)
        {
            builder.AppendLine();
            if (record.Fields.Count == 0)
            {
                builder.AppendLine($"-- Record {record.RecordId}: no proven writable field mapping.");
                continue;
            }
            builder.Append($"UPDATE {Quote(record.TargetTable)} SET ");
            builder.Append(string.Join(", ", record.Fields.Select(field => $"{Quote(field.TargetColumn)}={Literal(field.Value)}")));
            builder.AppendLine($" WHERE {Quote(record.TargetKeyColumn)}={record.RecordId.ToString(CultureInfo.InvariantCulture)};");
        }
        return builder.ToString();
    }

    private static string Quote(string value) => $"`{value.Replace("`", "``", StringComparison.Ordinal)}`";
    private static string Literal(object? value) => value switch
    {
        null => "NULL",
        string text => $"'{text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal)}'",
        bool state => state ? "1" : "0",
        float number when float.IsFinite(number) => number.ToString("R", CultureInfo.InvariantCulture),
        double number when double.IsFinite(number) => number.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => $"'{Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace("'", "''", StringComparison.Ordinal)}'"
    };
}

/// <summary>
/// Produces an inspectable, schema-bound update plan from decoded client cache rows.
/// Cache files contain client-facing snapshots, not complete server templates, so the
/// service deliberately never invents INSERT/REPLACE statements or applies SQL.
/// </summary>
public static class CacheServerPlanService
{
    public const int FormatVersion = 1;
    public const int MaximumRecords = 10_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static CacheServerUpdatePlan Create(WowCacheTable cache, DatabaseCapabilities capabilities, IReadOnlyCollection<uint>? selectedIds = null)
    {
        ArgumentNullException.ThrowIfNull(cache); ArgumentNullException.ThrowIfNull(capabilities);
        var definitionName = cache.Definition?.Name ?? throw new InvalidDataException("A cache-to-server plan requires a decoded, explicitly resolved cache definition.");
        var domain = ResolveDomain(definitionName);
        var target = capabilities.FindTable(domain.Table) ?? throw new NotSupportedException($"The selected database has no modern {domain.Table} table. Crucible will not fall back to obsolete ArcEmu tables.");
        var key = target.Find(domain.Key) ?? throw new NotSupportedException($"{target.Name} has no required {domain.Key} identity column.");
        var requested = selectedIds is null || selectedIds.Count == 0 ? null : selectedIds.ToHashSet();
        var sourceRows = cache.Records.Where(record => requested is null || requested.Contains(record.Id)).ToArray();
        if (sourceRows.Length > MaximumRecords) throw new InvalidDataException($"A single cache plan is limited to {MaximumRecords:N0} selected records. Narrow the ID selection and review in batches.");
        if (requested is not null)
        {
            var missing = requested.Except(sourceRows.Select(record => record.Id)).Order().ToArray();
            if (missing.Length > 0) throw new KeyNotFoundException($"The cache has no selected record(s): {string.Join(", ", missing)}.");
        }

        var planRows = new List<CacheServerRecordPlan>(sourceRows.Length);
        foreach (var record in sourceRows)
        {
            var warnings = new List<string>();
            if (record.DecodeError is not null) warnings.Add($"Decode failed: {record.DecodeError}");
            if (record.UnconsumedBytes != 0) warnings.Add($"The decoder left {record.UnconsumedBytes:N0} payload byte(s); review the schema before trusting this row.");
            var values = record.Values.GroupBy(value => Canonical(value.Name), StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var fields = new List<CacheServerFieldPlan>(); var mappedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (record.DecodeError is null && record.UnconsumedBytes == 0)
            {
                foreach (var mapping in domain.Mappings)
                {
                    var column = target.Find(mapping.Target); if (column is null) continue;
                    var source = mapping.Sources.Select(Canonical).Select(name => values.GetValueOrDefault(name)).FirstOrDefault(value => value is not null);
                    if (source is null || source.Name.Equals("Entry", StringComparison.OrdinalIgnoreCase) || source.Value is null) continue;
                    if (source.Value is float single && !float.IsFinite(single) || source.Value is double real && !double.IsFinite(real)) { warnings.Add($"Skipped non-finite numeric field {source.Name}."); continue; }
                    if (fields.Any(field => field.TargetColumn.Equals(column.Name, StringComparison.OrdinalIgnoreCase))) continue;
                    fields.Add(new(source.Name, column.Name, source.Value, source.Type)); mappedSources.Add(Canonical(source.Name));
                }
            }
            var unmapped = record.Values.Where(value => !value.Name.Equals(key.Name, StringComparison.OrdinalIgnoreCase) && !mappedSources.Contains(Canonical(value.Name))).Select(value => value.Name).ToArray();
            planRows.Add(new(record.Id, target.Name, key.Name, fields, unmapped, warnings));
        }
        var planWarnings = new List<string>
        {
            "Cache data is client-facing and incomplete. This plan updates existing rows only and never creates server templates.",
            "Alternate localized names, client-only quest-item arrays, unknown fields, scripts, loot, spawns, factions, levels, and AI remain unchanged unless a mapping is proven explicitly.",
            "Review the generated SQL and the complete live row in SQL Studio before applying any update."
        };
        return new(FormatVersion, DateTimeOffset.UtcNow, cache.SourcePath, cache.Sha256, definitionName, cache.Header.Build,
            capabilities.Database, capabilities.ServerVersion, SchemaFingerprint(target), planRows, planWarnings);
    }

    public static void Save(CacheServerUpdatePlan plan, string path, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(plan); path = Path.GetFullPath(path);
        if (File.Exists(path) && !overwrite) throw new IOException($"Cache server plan already exists: {path}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(plan, JsonOptions), new UTF8Encoding(false)); File.Move(temporary, path, overwrite); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string SchemaFingerprint(DatabaseTableCapability table)
    {
        var canonical = string.Join('\n', table.Columns.OrderBy(column => column.Ordinal).Select(column =>
            $"{column.Ordinal}|{column.Name}|{column.DataType}|{column.ColumnType}|{column.Nullable}|{column.DefaultValue}|{column.Key}|{column.Extra}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{table.Name}\n{canonical}")));
    }

    private static string Canonical(string value) => new(value.Trim().Where(char.IsLetterOrDigit).Select(character => char.ToLowerInvariant(character)).ToArray());

    private static CacheDomain ResolveDomain(string definition) => definition.Trim().ToLowerInvariant() switch
    {
        "itemcache" => new("item_template", "entry", ItemMappings),
        "creaturecache" => new("creature_template", "entry", CreatureMappings),
        "gameobjectcache" => new("gameobject_template", "entry", GameObjectMappings),
        "questcache" => new("quest_template", "ID", QuestMappings),
        _ => throw new NotSupportedException($"Cache definition {definition} has no proven modern-core server mapping. It remains available for read/export review.")
    };

    private static FieldMap Map(string target, params string[] sources) => new(target, sources);

    private static readonly FieldMap[] CreatureMappings =
    [
        Map("name", "Name"), Map("subname", "SubName"), Map("IconName", "IconName"), Map("type_flags", "Flags", "type_flag"),
        Map("type", "Type"), Map("family", "Family"), Map("rank", "Rank"), Map("KillCredit1", "KillCredit1"), Map("KillCredit2", "KillCredit2"),
        Map("HealthModifier", "HealthModifier"), Map("ManaModifier", "PowerModifier"), Map("RacialLeader", "RacialLeader"), Map("movementId", "MovementId")
    ];

    private static readonly FieldMap[] GameObjectMappings =
    [
        Map("type", "Type"), Map("displayId", "DisplayId"), Map("name", "Name"), Map("IconName", "IconName"),
        Map("castBarCaption", "CastBarCaption"), Map("unk1", "wdb_Unk1"), Map("size", "Size"),
        .. Enumerable.Range(0, 24).Select(index => Map($"Data{index}", $"data{index}"))
    ];

    private static readonly FieldMap[] ItemMappings = BuildItemMappings();
    private static FieldMap[] BuildItemMappings()
    {
        var mappings = new List<FieldMap>
        {
            Map("class", "class"), Map("subclass", "subclass"), Map("name", "Name1", "Name"), Map("displayid", "displayid"),
            Map("Quality", "quality"), Map("Flags", "flags"), Map("BuyPrice", "buyprice"), Map("SellPrice", "sellprice"), Map("InventoryType", "inventoryType"),
            Map("AllowableClass", "allowableclass"), Map("AllowableRace", "allowablerace"), Map("ItemLevel", "itemlevel"), Map("RequiredLevel", "requiredlevel"),
            Map("RequiredSkill", "RequiredSkill"), Map("RequiredSkillRank", "RequiredSkillRank"), Map("requiredspell", "RequiredSpell"),
            Map("requiredhonorrank", "RequiredPlayerRank1"), Map("RequiredCityRank", "RequiredPlayerRank2"), Map("RequiredReputationFaction", "RequiredFaction"),
            Map("RequiredReputationRank", "RequiredFactionStanding"), Map("maxcount", "MaxCount", "Unique"), Map("stackable", "Stackable", "maxcount"),
            Map("ContainerSlots", "ContainerSlots"), Map("ScalingStatDistribution", "ScalingStatDistribution", "ScaledStatsDistributionId"),
            Map("ScalingStatValue", "ScalingStatValue", "ScaledStatsDistributionFlags"), Map("dmg_min1", "dmg_min1"), Map("dmg_max1", "dmg_max1"),
            Map("dmg_type1", "dmg_Type1"), Map("dmg_min2", "dmg_min2"), Map("dmg_max2", "dmg_max2"), Map("dmg_type2", "dmg_Type2"),
            Map("armor", "armor"), Map("holy_res", "holy_res"), Map("fire_res", "fire_res"), Map("nature_res", "nature_res"), Map("frost_res", "frost_res"),
            Map("shadow_res", "shadow_res"), Map("arcane_res", "arcane_res"), Map("delay", "delay"), Map("ammo_type", "ammo_Type"), Map("RangedModRange", "range"),
            Map("bonding", "bonding"), Map("description", "description"), Map("PageText", "page_id"), Map("LanguageID", "page_language"),
            Map("PageMaterial", "page_material"), Map("startquest", "quest_id"), Map("lockid", "lock_id"), Map("Material", "lock_material"), Map("sheath", "sheathID"),
            Map("RandomProperty", "randomprop"), Map("RandomSuffix", "randomsuffix"), Map("block", "block"), Map("itemset", "itemset"), Map("MaxDurability", "MaxDurability"),
            Map("area", "ZoneNameID"), Map("Map", "mapid"), Map("BagFamily", "bagfamily"), Map("TotemCategory", "TotemCategory"),
            Map("socketColor_1", "socket_color_1"), Map("socketContent_1", "unk201_3"), Map("socketColor_2", "socket_color_2"), Map("socketContent_2", "unk201_5"),
            Map("socketColor_3", "socket_color_3"), Map("socketContent_3", "unk201_7"), Map("socketBonus", "socket_bonus"), Map("GemProperties", "GemProperties"),
            Map("RequiredDisenchantSkill", "ReqDisenchantSkill"), Map("ArmorDamageModifier", "ArmorDamageModifier"), Map("duration", "existingduration"),
            Map("ItemLimitCategory", "ItemLimitCategoryId"), Map("HolidayId", "HolidayId")
        };
        for (var index = 1; index <= 10; index++) { mappings.Add(Map($"stat_type{index}", $"stat_type[{index}]", $"stat_type{index}")); mappings.Add(Map($"stat_value{index}", $"stat_value[{index}]", $"stat_value{index}")); }
        for (var index = 1; index <= 5; index++)
        {
            mappings.Add(Map($"spellid_{index}", $"spellid_{index}")); mappings.Add(Map($"spelltrigger_{index}", $"spelltrigger_{index}")); mappings.Add(Map($"spellcharges_{index}", $"spellcharges_{index}"));
            mappings.Add(Map($"spellcooldown_{index}", $"spellcooldown_{index}")); mappings.Add(Map($"spellcategory_{index}", $"spellcategory_{index}")); mappings.Add(Map($"spellcategorycooldown_{index}", $"spellcategorycooldown_{index}"));
        }
        return mappings.ToArray();
    }

    private static readonly FieldMap[] QuestMappings = BuildQuestMappings();
    private static FieldMap[] BuildQuestMappings()
    {
        var mappings = new List<FieldMap>
        {
            Map("QuestType", "Method"), Map("QuestLevel", "QuestLevel"), Map("MinLevel", "MinLevel"), Map("QuestSortID", "ZoneOrSort"), Map("QuestInfoID", "Type"),
            Map("SuggestedGroupNum", "SuggestedPlayers"), Map("RequiredFactionId1", "RepObjectiveFaction1"), Map("RequiredFactionValue1", "RepObjectiveValue1"),
            Map("RequiredFactionId2", "RepObjectiveFaction2"), Map("RequiredFactionValue2", "RepObjectiveValue2"), Map("RewardNextQuest", "NextQuestInChain"),
            Map("RewardXPDifficulty", "XPID"), Map("RewardMoney", "RewOrReqMoney"), Map("RewardMoneyDifficulty", "RewMoneyMaxLevel"),
            Map("RewardSpell", "RewSpell"), Map("RewardDisplaySpell", "RewSpellCast"), Map("StartItem", "SrcItemId"), Map("Flags", "QuestFlags"),
            Map("RequiredPlayerKills", "PlayersSlain"), Map("RewardTitle", "CharTitleId"), Map("RewardTalents", "BonusTalents"), Map("RewardArenaPoints", "BonusArenaPoints"),
            Map("POIContinent", "PointMapId"), Map("POIx", "PointX"), Map("POIy", "PointY"), Map("POIPriority", "PointOption"),
            Map("LogTitle", "Title"), Map("LogDescription", "Objectives"), Map("QuestDescription", "Details"), Map("AreaDescription", "EndText"), Map("QuestCompletionLog", "CompletionText")
        };
        for (var index = 1; index <= 4; index++)
        {
            mappings.Add(Map($"RewardItem{index}", $"RewItemId{index}")); mappings.Add(Map($"RewardAmount{index}", $"RewItemCount{index}"));
            mappings.Add(Map($"RequiredNpcOrGo{index}", $"ReqCreatureOrGOId{index}")); mappings.Add(Map($"RequiredNpcOrGoCount{index}", $"ReqCreatureOrGOCount{index}"));
            mappings.Add(Map($"ObjectiveText{index}", $"ObjectiveText{index}"));
        }
        for (var index = 1; index <= 6; index++)
        {
            mappings.Add(Map($"RewardChoiceItemID{index}", $"RewChoiceItemId{index}")); mappings.Add(Map($"RewardChoiceItemQuantity{index}", $"RewChoiceItemCount{index}"));
            mappings.Add(Map($"RequiredItemId{index}", $"ReqItemId{index}")); mappings.Add(Map($"RequiredItemCount{index}", $"ReqItemCount{index}"));
        }
        for (var index = 1; index <= 5; index++)
        {
            mappings.Add(Map($"RewardFactionID{index}", $"RewFactionId{index}")); mappings.Add(Map($"RewardFactionValue{index}", $"RewFactionVal{index}"));
            mappings.Add(Map($"RewardFactionOverride{index}", $"RewFactionValOverride{index}"));
        }
        return mappings.ToArray();
    }

    private sealed record FieldMap(string Target, IReadOnlyList<string> Sources);
    private sealed record CacheDomain(string Table, string Key, IReadOnlyList<FieldMap> Mappings);
}
