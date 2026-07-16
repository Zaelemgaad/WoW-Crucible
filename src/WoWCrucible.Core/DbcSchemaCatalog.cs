using System.Xml.Linq;

namespace WoWCrucible.Core;

public enum DbcSchemaMatchKind { NamedMatch, MissingTableFallback, FieldCountMismatchFallback }
public enum DbcRecordKeyKind { PhysicalColumn, VirtualRowIndex, NoStableKey }
public sealed record DbcRecordKeyStrategy(DbcRecordKeyKind Kind, int? ColumnIndex = null, uint VirtualStart = 0)
{
    public static DbcRecordKeyStrategy Physical(int columnIndex) => new(DbcRecordKeyKind.PhysicalColumn, columnIndex);
    public static DbcRecordKeyStrategy Virtual(uint start = 0) => new(DbcRecordKeyKind.VirtualRowIndex, null, start);
    public static DbcRecordKeyStrategy None { get; } = new(DbcRecordKeyKind.NoStableKey);
    public string DisplayName(IReadOnlyList<DbcColumn> columns) => Kind switch
    {
        DbcRecordKeyKind.PhysicalColumn when ColumnIndex is >= 0 && ColumnIndex < columns.Count => columns[ColumnIndex.Value].Name,
        DbcRecordKeyKind.VirtualRowIndex => "$row",
        _ => "$none"
    };
}
public sealed record DbcSchemaResolution(IReadOnlyList<DbcColumn> Columns, DbcSchemaMatchKind MatchKind, int? DefinedFieldCount, DbcRecordKeyStrategy KeyStrategy)
{
    public bool UsedFallback => MatchKind != DbcSchemaMatchKind.NamedMatch;
}

public sealed class DbcSchemaCatalog
{
    private sealed record TableDefinition(IReadOnlyList<DbcColumn> Columns, DbcRecordKeyStrategy KeyStrategy);
    private readonly Dictionary<string, TableDefinition> _tables;

    private DbcSchemaCatalog(Dictionary<string, TableDefinition> tables) => _tables = tables;

    public static DbcSchemaCatalog CreateBuiltIn12340()
    {
        var tables = new Dictionary<string, TableDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["Spell"] = PhysicalDefinition(BuildSpellColumns())
        };
        AddSimple(tables, "SpellCastTimes", "ID:int,Base:int,PerLevel:int,Minimum:int");
        AddSimple(tables, "SpellDuration", "ID:int,Duration:int,DurationPerLevel:int,MaxDuration:int");
        AddSimple(tables, "SpellRadius", "ID:int,Radius:float,RadiusPerLevel:float,RadiusMax:float");
        AddSimple(tables, "SpellRuneCost", "ID:int,Blood:uint,Unholy:uint,Frost:uint,RunicPower:uint");
        AddSimple(tables, "SpellDifficulty", "ID:int,DifficultySpellID[0]:int,DifficultySpellID[1]:int,DifficultySpellID[2]:int,DifficultySpellID[3]:int");
        return new(tables);
    }

    public static DbcSchemaCatalog Load(string path)
    {
        using var stream = File.OpenRead(path);
        var document = XDocument.Load(stream, LoadOptions.None);
        var tables = new Dictionary<string, TableDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in document.Root?.Elements("Table") ?? [])
        {
            var tableName = (string?)table.Attribute("Name");
            if (string.IsNullOrWhiteSpace(tableName))
                continue;

            var columns = new List<DbcColumn>();
            var hasGeneratedKey = false;
            var offset = 0;
            foreach (var field in table.Elements("Field"))
            {
                // Some packed tables use a synthetic editor-only key which is not present in the file record.
                if ((bool?)field.Attribute("AutoGenerate") == true)
                {
                    hasGeneratedKey = true;
                    continue;
                }
                var name = (string?)field.Attribute("Name") ?? $"Field_{columns.Count}";
                var type = ((string?)field.Attribute("Type") ?? "int").ToLowerInvariant();
                var count = (int?)field.Attribute("ArraySize") ?? 1;
                var isIndex = (bool?)field.Attribute("IsIndex") ?? false;

                // WDBC stores localized strings as 16 locale offsets followed by locale flags.
                if (type == "loc")
                {
                    for (var i = 0; i < 16; i++)
                    {
                        columns.Add(new(columns.Count, offset, 4, $"{name}[{LocaleName(i)}]", DbcValueType.StringOffset, isIndex));
                        offset += 4;
                    }
                    columns.Add(new(columns.Count, offset, 4, $"{name}[Flags]", DbcValueType.UInt32));
                    offset += 4;
                    continue;
                }

                // This first editor operates on WDBC's 32-bit cells. A 64-bit schema value occupies two cells.
                if (type is "long" or "ulong")
                {
                    columns.Add(new(columns.Count, offset, 4, $"{name}[Low]", DbcValueType.UInt32, isIndex));
                    offset += 4;
                    columns.Add(new(columns.Count, offset, 4, $"{name}[High]", DbcValueType.UInt32));
                    offset += 4;
                    continue;
                }

                var valueType = type switch
                {
                    "int" => DbcValueType.Int32,
                    "uint" => DbcValueType.UInt32,
                    "float" => DbcValueType.Float32,
                    "string" => DbcValueType.StringOffset,
                    "byte" => DbcValueType.Byte,
                    _ => DbcValueType.Raw32
                };

                for (var i = 0; i < count; i++)
                {
                    var size = valueType == DbcValueType.Byte ? 1 : 4;
                    columns.Add(new(columns.Count, offset, size, count == 1 ? name : $"{name}[{i}]", valueType, isIndex));
                    offset += size;
                }
            }

            var physicalKey = columns.FirstOrDefault(column => column.IsIndex);
            var keyStrategy = hasGeneratedKey ? DbcRecordKeyStrategy.Virtual() : physicalKey is not null ? DbcRecordKeyStrategy.Physical(physicalKey.Index) : DbcRecordKeyStrategy.None;
            tables[tableName] = new(columns, keyStrategy);
        }

        ApplyKnown12340Corrections(tables, document);

        return new(tables);
    }

    public IReadOnlyList<DbcColumn> GetColumns(string tableName, int physicalFieldCount)
        => ResolveColumns(tableName, physicalFieldCount).Columns;

    public DbcSchemaResolution ResolveColumns(string tableName, int physicalFieldCount)
    {
        if (_tables.TryGetValue(tableName, out var defined) && defined.Columns.Count == physicalFieldCount)
            return new(defined.Columns, DbcSchemaMatchKind.NamedMatch, defined.Columns.Count, defined.KeyStrategy);

        var fallback = Enumerable.Range(0, physicalFieldCount)
            .Select(i => new DbcColumn(i, i * 4, 4, i == 0 ? "ID" : $"Field_{i}", DbcValueType.Raw32, i == 0))
            .ToArray();
        return new(fallback, defined is null ? DbcSchemaMatchKind.MissingTableFallback : DbcSchemaMatchKind.FieldCountMismatchFallback, defined?.Columns.Count, DbcRecordKeyStrategy.None);
    }

    private static string LocaleName(int index) => index switch
    {
        0 => "enUS",
        1 => "koKR",
        2 => "frFR",
        3 => "deDE",
        4 => "zhCN",
        5 => "zhTW",
        6 => "esES",
        7 => "esMX",
        8 => "ruRU",
        9 => "ptBR",
        10 => "itIT",
        _ => $"Locale{index}"
    };

    private static IReadOnlyList<DbcColumn> BuildSpellColumns()
    {
        var columns = Enumerable.Range(0, 234)
            .Select(index => new DbcColumn(index, index * 4, 4, index == 0 ? "ID" : $"Field_{index}", DbcValueType.Raw32, index == 0))
            .ToArray();

        void Set(int index, string name, DbcValueType type = DbcValueType.UInt32) =>
            columns[index] = new(index, index * 4, 4, name, type, index == 0);
        void Array(int start, string name, int count, DbcValueType type = DbcValueType.UInt32)
        {
            for (var i = 0; i < count; i++) Set(start + i, $"{name}[{i}]", type);
        }
        void Localized(int start, string name)
        {
            for (var i = 0; i < 16; i++) Set(start + i, $"{name}[{LocaleName(i)}]", DbcValueType.StringOffset);
            Set(start + 16, $"{name}[Flags]");
        }

        Set(0, "ID", DbcValueType.Int32); Set(1, "Category"); Set(2, "DispelType"); Set(3, "Mechanic");
        for (var i = 0; i < 8; i++) Set(4 + i, i == 0 ? "Attributes" : $"AttributesEx{i}");
        Set(12, "ShapeshiftMask[Low]"); Set(13, "ShapeshiftMask[High]"); Set(14, "ShapeshiftExclude[Low]"); Set(15, "ShapeshiftExclude[High]");
        string[] names16 = ["Targets", "TargetCreatureType", "RequiresSpellFocus", "FacingCasterFlags", "CasterAuraState", "TargetAuraState", "ExcludeCasterAuraState", "ExcludeTargetAuraState", "CasterAuraSpell", "TargetAuraSpell", "ExcludeCasterAuraSpell", "ExcludeTargetAuraSpell", "CastingTimeIndex", "RecoveryTime", "CategoryRecoveryTime", "InterruptFlags", "AuraInterruptFlags", "ChannelInterruptFlags", "ProcTypeMask", "ProcChance", "ProcCharges", "MaxLevel", "BaseLevel", "SpellLevel", "DurationIndex"];
        for (var i = 0; i < names16.Length; i++) Set(16 + i, names16[i]);
        Set(41, "PowerType", DbcValueType.Int32); Set(42, "ManaCost"); Set(43, "ManaCostPerLevel"); Set(44, "ManaPerSecond"); Set(45, "ManaPerSecondPerLevel"); Set(46, "RangeIndex"); Set(47, "Speed", DbcValueType.Float32); Set(48, "ModalNextSpell"); Set(49, "CumulativeAura");
        Array(50, "Totem", 2); Array(52, "Reagent", 8, DbcValueType.Int32); Array(60, "ReagentCount", 8, DbcValueType.Int32);
        Set(68, "EquippedItemClass", DbcValueType.Int32); Set(69, "EquippedItemSubclass", DbcValueType.Int32); Set(70, "EquippedItemInvTypes", DbcValueType.Int32);
        Array(71, "Effect", 3); Array(74, "EffectDieSides", 3, DbcValueType.Int32); Array(77, "EffectRealPointsPerLevel", 3, DbcValueType.Float32); Array(80, "EffectBasePoints", 3, DbcValueType.Int32); Array(83, "EffectMechanic", 3); Array(86, "ImplicitTargetA", 3); Array(89, "ImplicitTargetB", 3); Array(92, "EffectRadiusIndex", 3); Array(95, "EffectAura", 3); Array(98, "EffectAuraPeriod", 3); Array(101, "EffectMultipleValue", 3, DbcValueType.Float32); Array(104, "EffectChainTargets", 3); Array(107, "EffectItemType", 3); Array(110, "EffectMiscValue", 3, DbcValueType.Int32); Array(113, "EffectMiscValueB", 3, DbcValueType.Int32); Array(116, "EffectTriggerSpell", 3); Array(119, "EffectPointsPerCombo", 3, DbcValueType.Float32); Array(122, "EffectSpellClassMaskA", 3); Array(125, "EffectSpellClassMaskB", 3); Array(128, "EffectSpellClassMaskC", 3);
        Array(131, "SpellVisualID", 2); Set(133, "SpellIconID"); Set(134, "ActiveIconID"); Set(135, "SpellPriority");
        Localized(136, "Name"); Localized(153, "NameSubtext"); Localized(170, "Description"); Localized(187, "AuraDescription");
        string[] tail = ["ManaCostPct", "StartRecoveryCategory", "StartRecoveryTime", "MaxTargetLevel", "SpellClassSet"];
        for (var i = 0; i < tail.Length; i++) Set(204 + i, tail[i]);
        Array(209, "SpellClassMask", 3); Set(212, "MaxTargets"); Set(213, "DefenseType"); Set(214, "PreventionType"); Set(215, "StanceBarOrder"); Array(216, "EffectChainAmplitude", 3, DbcValueType.Float32); Set(219, "MinFactionID"); Set(220, "MinReputation"); Set(221, "RequiredAuraVision"); Array(222, "RequiredTotemCategoryID", 2); Set(224, "RequiredAreasID", DbcValueType.Int32); Set(225, "SchoolMask"); Set(226, "RuneCostID"); Set(227, "SpellMissileID"); Set(228, "PowerDisplayID", DbcValueType.Int32); Array(229, "UnknownFloat", 3, DbcValueType.Float32); Set(232, "SpellDescriptionVariableID"); Set(233, "SpellDifficultyID");
        return columns;
    }

    private static TableDefinition PhysicalDefinition(IReadOnlyList<DbcColumn> columns) => new(columns, DbcRecordKeyStrategy.Physical(columns.First(column => column.IsIndex).Index));

    private static void ApplyKnown12340Corrections(Dictionary<string, TableDefinition> tables, XDocument document)
    {
        var isBuild12340 = document.Root?.Elements("Table").Any(table => (string?)table.Attribute("Build") == "12340") == true;
        if (!isBuild12340) return;

        // The commonly distributed WDBX 12340 XML omits the two exclusion masks and
        // labels a non-existent trailing trade-skill field. TrinityCore's physical
        // format and real stock files both contain these 14 cells.
        AddSimple(tables, "SkillLineAbility", "ID:int,SkillLine:int,Spell:int,RaceMask:uint,ClassMask:uint,ExcludeRace:uint,ExcludeClass:uint,MinSkillLineRank:int,SupercededBySpell:int,AcquireMethod:int,TrivialSkillLineRankHigh:int,TrivialSkillLineRankLow:int,CharacterPoints[0]:int,CharacterPoints[1]:int");

        // Stock build-12340 SpellVisualKit has the 37 defined cells plus a final
        // flags cell. Preserve the imported names/types and append that missing cell.
        if (tables.TryGetValue("SpellVisualKit", out var visualKit) && visualKit.Columns.Count == 37)
        {
            var columns = visualKit.Columns.Concat([new DbcColumn(37, 37 * 4, 4, "Flags", DbcValueType.UInt32)]).ToArray();
            tables["SpellVisualKit"] = PhysicalDefinition(columns);
        }
    }

    private static void AddSimple(Dictionary<string, TableDefinition> tables, string tableName, string specification)
    {
        var fields = specification.Split(',');
        var columns = fields.Select((field, index) =>
        {
            var parts = field.Split(':');
            var type = parts[1] switch { "int" => DbcValueType.Int32, "float" => DbcValueType.Float32, "string" => DbcValueType.StringOffset, _ => DbcValueType.UInt32 };
            return new DbcColumn(index, index * 4, 4, parts[0], type, index == 0);
        }).ToArray();
        tables[tableName] = PhysicalDefinition(columns);
    }
}
