using System.Globalization;
using MySqlConnector;

namespace WoWCrucible.Core;

public sealed record SpellSqlFieldDifference(int FieldIndex, string DbcField, string SqlColumn, char LoaderType, object? DbcValue, object? SqlValue);

public sealed record SpellSqlRelatedRow(string Table, string Relationship, IReadOnlyList<string> MatchedColumns,
    IReadOnlyDictionary<string, object?> Values, IReadOnlyDictionary<string, object?> Key)
{
    public string Display => Key.Count > 0
        ? string.Join(" · ", Key.Select(pair => $"{pair.Key}={Convert.ToString(pair.Value, CultureInfo.InvariantCulture)}"))
        : string.Join(" · ", Values.Take(3).Select(pair => $"{pair.Key}={Convert.ToString(pair.Value, CultureInfo.InvariantCulture)}"));
}

public sealed record SpellSqlAuditResult(uint SpellId, bool DbcRecordFound, SpellSqlRelatedRow? FullOverride,
    IReadOnlyList<SpellSqlFieldDifference> OverrideDifferences, string? OverrideComparisonWarning,
    IReadOnlyList<SpellSqlRelatedRow> RelatedRows, IReadOnlyList<string> CheckedTables, IReadOnlyList<string> MissingTables)
{
    public bool HasFullOverride => FullOverride is not null;
    public string EffectiveSource => HasFullOverride ? "SQL spell_dbc full-record replacement" : DbcRecordFound ? "file Spell.dbc record" : "no Spell.dbc record found";
}

/// <summary>
/// Explains the effective AzerothCore spell record and every recognized live SQL relationship for one spell ID.
/// AzerothCore loads Spell.dbc first and then replaces matching records from spell_dbc using SpellEntryfmt.
/// </summary>
public sealed class SpellSqlAuditService
{
    // AzerothCore src/server/shared/DataStores/DBCfmt.h. One character is consumed for every SELECT * column.
    // 'x' consumes the SQL/DBC cell but the server ignores it; 's' becomes a pooled string.
    public const string AzerothCoreSpellEntryFormat = "niiiiiiiiiiiixixiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiifxiiiiiiiiiiiiiiiiiiiiiiiiiiiifffiiiiiiiiiiiiiiiiiiiiifffiiiiiiiiiiiiiiifffiiiiiiiiiiiiiissssssssssssssssxssssssssssssssssxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxiiiiiiiiiiixfffxxxiiiiixxfffxx";

    private sealed record ReferenceColumn(string Name, bool Absolute = false);
    private sealed record ReferenceSpec(string Table, string Relationship, ReferenceColumn[] Columns, string? AdditionalPredicate = null, string[]? RequiredColumns = null);

    private static readonly ReferenceSpec[] References =
    [
        new("spell_area", "area/autocast or aura requirement", [new("spell"), new("aura_spell", true)]),
        new("spell_bonus_data", "damage/healing coefficient override", [new("entry", true)]),
        new("spell_cone", "cone-angle override", [new("ID")]),
        new("spell_cooldown_overrides", "cooldown override", [new("Id")]),
        new("spell_custom_attr", "custom server attributes", [new("spell_id", true)]),
        new("spell_group", "spell-group membership", [new("spell_id")]),
        new("spell_jump_distance", "jump-distance override", [new("ID")]),
        new("spell_linked_spell", "linked trigger/effect", [new("spell_trigger", true), new("spell_effect", true)]),
        new("spell_loot_template", "spell-created loot", [new("Entry")]),
        new("spell_mixology", "mixology modifier", [new("entry")]),
        new("spell_pet_auras", "pet aura mapping", [new("spell", true), new("aura", true)]),
        new("spell_proc", "proc definition", [new("SpellId", true)]),
        new("spell_proc_event", "legacy proc-event definition", [new("entry", true)]),
        new("spell_ranks", "rank chain", [new("first_spell_id"), new("spell_id")]),
        new("spell_required", "spell prerequisite", [new("spell_id"), new("req_spell")]),
        new("spell_script_names", "compiled spell script binding", [new("spell_id", true)]),
        new("spell_scripts", "legacy spell script command", [new("id")]),
        new("spell_target_position", "scripted target position", [new("ID")]),
        new("spell_threat", "threat override", [new("entry")]),
        new("trainer_spell", "trainer-taught or prerequisite spell", [new("SpellId"), new("ReqAbility1"), new("ReqAbility2"), new("ReqAbility3")]),
        new("npc_trainer", "legacy trainer-taught or prerequisite spell", [new("SpellID"), new("ReqSpell")]),
        new("item_template", "item requirement or item spell slot", [new("requiredspell"), new("spellid_1"), new("spellid_2"), new("spellid_3"), new("spellid_4"), new("spellid_5")]),
        new("quest_template", "quest reward spell", [new("RewardDisplaySpell"), new("RewardSpell")]),
        new("creature_template_spell", "creature spell slot", [new("Spell")]),
        new("npc_spellclick_spells", "spell-click cast", [new("spell_id")]),
        new("player_factionchange_spells", "faction-change spell mapping", [new("alliance_id"), new("horde_id")]),
        new("playercreateinfo_cast_spell", "character-start cast", [new("spell")]),
        new("playercreateinfo_spell_custom", "custom character-start spell", [new("Spell")]),
        new("playercreateinfo_action", "character-start action spell", [new("action")], "`type` = 0", ["type"]),
        new("smart_scripts", "SmartAI cast/add-aura action", [new("action_param1")], "`action_type` IN (11,75,86,134)", ["action_type"]),
        new("conditions", "aura/learned-spell condition", [new("ConditionValue1")], "`ConditionTypeOrReference` IN (1,25)", ["ConditionTypeOrReference"]),
        new("disables", "disabled spell", [new("entry", true)], "`sourceType` = 0", ["sourceType"])
    ];

    public async Task<SpellSqlAuditResult> AuditAsync(DatabaseConnectionProfile profile, DatabaseCapabilities capabilities, uint spellId,
        WdbcFile? dbc = null, IReadOnlyList<DbcColumn>? dbcColumns = null, int? knownDbcRow = null, CancellationToken cancellationToken = default)
    {
        if (spellId == 0) throw new ArgumentOutOfRangeException(nameof(spellId), "Spell ID must be positive.");
        int? dbcRow = dbc is null || dbcColumns is null ? null : knownDbcRow ?? FindDbcRow(dbc, dbcColumns, spellId);
        var checkedTables = new List<string>(); var missingTables = new List<string>();
        SpellSqlRelatedRow? fullOverride = null; IReadOnlyList<SpellSqlFieldDifference> differences = [];
        string? comparisonWarning = null;

        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile));
        await connection.OpenAsync(cancellationToken);

        var overrideTable = capabilities.FindTable("spell_dbc");
        if (overrideTable?.Find("ID") is { } overrideId)
        {
            checkedTables.Add(overrideTable.Name);
            var overrideRows = await ReadRowsAsync(connection, overrideTable, $"{Quote(overrideId.Name)} = @spell", spellId, "full Spell.dbc record replacement", [overrideId.Name], cancellationToken);
            if (overrideRows.Count > 1) throw new InvalidDataException($"spell_dbc contains more than one ID {spellId} row.");
            fullOverride = overrideRows.FirstOrDefault();
            if (fullOverride is not null && dbc is not null && dbcColumns is not null && dbcRow is not null)
            {
                try { differences = CompareOverride(dbc, dbcRow.Value, dbcColumns, overrideTable, fullOverride.Values); }
                catch (Exception exception) { comparisonWarning = exception.Message; }
            }
            else if (fullOverride is not null && dbcRow is null) comparisonWarning = "The SQL override exists, but no matching file Spell.dbc row was available for field comparison.";
            else if (fullOverride is not null && (dbc is null || dbcColumns is null)) comparisonWarning = "The SQL override exists; provide Spell.dbc to compare its effective fields.";
        }
        else missingTables.Add("spell_dbc.ID");

        var related = new List<SpellSqlRelatedRow>();
        foreach (var spec in References)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var table = capabilities.FindTable(spec.Table);
            if (table is null) { missingTables.Add(spec.Table); continue; }
            var columns = spec.Columns.Select(candidate => (Spec: candidate, Column: table.Find(candidate.Name))).Where(pair => pair.Column is not null).ToArray();
            var prerequisites = spec.RequiredColumns ?? [];
            if (columns.Length == 0 || prerequisites.Any(name => table.Find(name) is null)) { missingTables.Add($"{spec.Table} (recognized columns unavailable)"); continue; }
            checkedTables.Add(table.Name);
            var predicates = columns.Select(pair => pair.Spec.Absolute
                ? $"ABS(CAST({Quote(pair.Column!.Name)} AS SIGNED)) = @spell"
                : $"{Quote(pair.Column!.Name)} = @spell").ToArray();
            var where = $"({string.Join(" OR ", predicates)})" + (string.IsNullOrWhiteSpace(spec.AdditionalPredicate) ? string.Empty : $" AND ({spec.AdditionalPredicate})");
            var rows = await ReadRowsAsync(connection, table, where, spellId, spec.Relationship, columns.Select(pair => pair.Column!.Name).ToArray(), cancellationToken);
            related.AddRange(rows.Select(row => row with { MatchedColumns = MatchedColumns(row.Values, columns, spellId) }));
        }

        return new(spellId, dbcRow is not null, fullOverride, differences, comparisonWarning,
            related.OrderBy(row => row.Table, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Display, StringComparer.OrdinalIgnoreCase).ToArray(),
            checkedTables.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            missingTables.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public static int? FindDbcRow(WdbcFile dbc, IReadOnlyList<DbcColumn> columns, uint spellId)
    {
        if (columns.Count == 0 || dbc.FieldCount != columns.Count) return null;
        var id = columns[0];
        for (var row = 0; row < dbc.RowCount; row++) if (dbc.GetRaw(row, id) == spellId) return row;
        return null;
    }

    public static IReadOnlyList<SpellSqlFieldDifference> CompareOverride(WdbcFile dbc, int dbcRow, IReadOnlyList<DbcColumn> dbcColumns,
        DatabaseTableCapability sqlTable, IReadOnlyDictionary<string, object?> sqlValues)
    {
        if (AzerothCoreSpellEntryFormat.Length != 234) throw new InvalidDataException("The embedded AzerothCore SpellEntryfmt is not 234 fields.");
        var sqlColumns = sqlTable.Columns.OrderBy(column => column.Ordinal).ToArray();
        if (dbc.FieldCount != AzerothCoreSpellEntryFormat.Length || dbcColumns.Count != dbc.FieldCount)
            throw new InvalidDataException($"Spell.dbc has {dbc.FieldCount} fields; AzerothCore SpellEntryfmt requires {AzerothCoreSpellEntryFormat.Length}.");
        if (sqlColumns.Length != AzerothCoreSpellEntryFormat.Length)
            throw new InvalidDataException($"{sqlTable.Name} has {sqlColumns.Length} columns; AzerothCore SpellEntryfmt requires {AzerothCoreSpellEntryFormat.Length}. The full SQL row still replaces the server record, but an ordinal comparison would be unsafe.");
        if (dbcRow < 0 || dbcRow >= dbc.RowCount) throw new ArgumentOutOfRangeException(nameof(dbcRow));

        var differences = new List<SpellSqlFieldDifference>();
        for (var index = 0; index < AzerothCoreSpellEntryFormat.Length; index++)
        {
            var format = AzerothCoreSpellEntryFormat[index];
            if (format == 'x') continue;
            var dbcColumn = dbcColumns[index]; var sqlColumn = sqlColumns[index];
            if (!sqlValues.TryGetValue(sqlColumn.Name, out var sqlValue)) throw new InvalidDataException($"SQL override row did not expose {sqlColumn.Name}.");
            object? dbcValue; object? normalizedSql; bool same;
            switch (format)
            {
                case 's':
                    dbcValue = Convert.ToString(dbc.GetDisplayValue(dbcRow, dbcColumn), CultureInfo.InvariantCulture) ?? string.Empty;
                    normalizedSql = Convert.ToString(sqlValue, CultureInfo.InvariantCulture) ?? string.Empty;
                    same = string.Equals((string)dbcValue, (string)normalizedSql, StringComparison.Ordinal);
                    break;
                case 'f':
                    dbcValue = BitConverter.UInt32BitsToSingle(dbc.GetRaw(dbcRow, dbcColumn));
                    normalizedSql = sqlValue is null ? 0f : Convert.ToSingle(sqlValue, CultureInfo.InvariantCulture);
                    same = ((float)dbcValue).Equals((float)normalizedSql);
                    break;
                case 'n':
                case 'i':
                    dbcValue = dbc.GetRaw(dbcRow, dbcColumn);
                    normalizedSql = ToUInt32(sqlValue);
                    same = (uint)dbcValue == (uint)normalizedSql;
                    break;
                default: throw new InvalidDataException($"Unsupported SpellEntryfmt character '{format}' at field {index}.");
            }
            if (!same) differences.Add(new(index, dbcColumn.Name, sqlColumn.Name, format, dbcValue, normalizedSql));
        }
        return differences;
    }

    private static async Task<IReadOnlyList<SpellSqlRelatedRow>> ReadRowsAsync(MySqlConnection connection, DatabaseTableCapability table,
        string where, uint spellId, string relationship, IReadOnlyList<string> candidateColumns, CancellationToken cancellationToken)
    {
        var selected = table.Columns.OrderBy(column => column.Ordinal).ToArray();
        var primary = selected.Where(column => column.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase)).Select(column => column.Name).ToArray();
        var order = primary.Length == 0 ? string.Empty : $" ORDER BY {string.Join(',', primary.Select(Quote))}";
        await using var command = new MySqlCommand($"SELECT {string.Join(',', selected.Select(column => Quote(column.Name)))} FROM {Quote(table.Name)} WHERE {where}{order}", connection) { CommandTimeout = 120 };
        command.Parameters.AddWithValue("@spell", spellId);
        var result = new List<SpellSqlRelatedRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < reader.FieldCount; index++) values[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            var key = primary.ToDictionary(name => name, name => values[name], StringComparer.OrdinalIgnoreCase);
            result.Add(new(table.Name, relationship, candidateColumns, values, key));
        }
        return result;
    }

    private static IReadOnlyList<string> MatchedColumns(IReadOnlyDictionary<string, object?> values,
        IEnumerable<(ReferenceColumn Spec, DatabaseColumnCapability? Column)> columns, uint spellId) => columns
        .Where(pair => pair.Column is not null && IsMatch(values.GetValueOrDefault(pair.Column.Name), spellId, pair.Spec.Absolute))
        .Select(pair => pair.Column!.Name).ToArray();

    private static bool IsMatch(object? value, uint spellId, bool absolute)
    {
        if (value is null) return false;
        try { var number = Convert.ToInt64(value, CultureInfo.InvariantCulture); return absolute ? Math.Abs(number) == spellId : number == spellId; }
        catch { return false; }
    }

    private static uint ToUInt32(object? value)
    {
        if (value is null) return 0;
        return value switch
        {
            uint number => number,
            int number => unchecked((uint)number),
            ulong number => unchecked((uint)number),
            long number => unchecked((uint)number),
            ushort number => number,
            short number => unchecked((uint)number),
            byte number => number,
            sbyte number => unchecked((uint)number),
            _ => unchecked((uint)Convert.ToInt64(value, CultureInfo.InvariantCulture))
        };
    }

    private static string Quote(string identifier) => $"`{identifier.Replace("`", "``")}`";
}
