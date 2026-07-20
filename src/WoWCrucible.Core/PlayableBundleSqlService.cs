using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using MySqlConnector;

namespace WoWCrucible.Core;

public enum PlayableBundleIdentityKind { Class, Race }

public sealed record PlayableBundleDatabaseTarget(string Host, uint Port, string User, string Database, string ServerVersion,
    IReadOnlyDictionary<string, string> TableSchemaSha256);
public sealed record PlayableBundleSqlValue(string Column, LegacyDatabaseAuditValue Value);
public sealed record PlayableBundleSqlRow(IReadOnlyList<LegacyDatabaseAuditKeyPart> SourceKey,
    IReadOnlyList<LegacyDatabaseAuditKeyPart> TargetKey, IReadOnlyList<PlayableBundleSqlValue> Values,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UpdateColumn,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LegacyDatabaseAuditValue? UpdateBefore,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LegacyDatabaseAuditValue? UpdateAfter)
{
    public bool IsGuardedUpdate => UpdateColumn is not null;
}
public sealed record PlayableBundleSqlTablePlan(string Table, string Selector, int SourceRows, int AlreadyCovered,
    int Conflicts, IReadOnlyList<PlayableBundleSqlRow> Rows);
public sealed record PlayableBundleDbcTablePlan(string Table, string Action, int AffectedRows);
public sealed record PlayableBundleSqlInspection(PlayableBundleDatabaseTarget Target,
    IReadOnlyList<PlayableBundleSqlTablePlan> Tables, IReadOnlyList<string> Warnings);

public static class PlayableBundleSqlService
{
    private static readonly HashSet<string> ClassOverlayTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrclasses_dbc", "charbaseinfo_dbc", "charstartoutfit_dbc", "skilllineability_dbc", "skillraceclassinfo_dbc", "talenttab_dbc"
    };
    private static readonly HashSet<string> RaceOverlayTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrraces_dbc", "barbershopstyle_dbc", "characterfacialhairstyles_dbc", "charbaseinfo_dbc", "charhairgeosets_dbc", "charhairtextures_dbc", "charsections_dbc", "charstartoutfit_dbc", "dancemoves_dbc", "emotestextsound_dbc", "faction_dbc", "namegen_dbc", "skilllineability_dbc", "skillraceclassinfo_dbc", "talenttab_dbc", "vocaluisounds_dbc"
    };
    public static async Task<PlayableBundleSqlInspection> InspectAsync(PlayableBundleIdentityKind kind,
        uint sourceId, uint targetId, uint sourceMask, uint targetMask, DatabaseConnectionProfile profile,
        DatabaseCapabilities capabilities, ICollection<string> blockers, CancellationToken cancellationToken = default)
    {
        if (!profile.Database.Equals(capabilities.Database, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The database profile and inspected capabilities name different databases.");
        var candidates = capabilities.Tables.Values.Where(table => IsCandidate(table, kind)).OrderBy(table => table.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var warnings = new List<string>();
        if (!candidates.Any(table => table.Name.Equals("playercreateinfo", StringComparison.OrdinalIgnoreCase))) blockers.Add("The connected schema has no recognized playercreateinfo table.");
        if (!candidates.Any(table => table.Name.Contains("stat", StringComparison.OrdinalIgnoreCase))) warnings.Add($"No recognized player {kind.ToString().ToLowerInvariant()}-stat table was found; the new {kind.ToString().ToLowerInvariant()} may have no level/stat curve on this core.");
        var plans = new List<PlayableBundleSqlTablePlan>(); var fingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        foreach (var table in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested(); fingerprints[table.Name] = CacheServerPlanService.SchemaFingerprint(table);
            var direct = DirectColumn(table, kind); var mask = direct is null ? MaskColumn(table, kind) : null;
            plans.Add(direct is not null
                ? await PlanDirectAsync(connection, table, direct, sourceId, targetId, blockers, cancellationToken)
                : await PlanMaskAsync(connection, table, mask!, sourceMask, targetMask, blockers, cancellationToken));
        }
        return new(new(profile.Host, profile.Port, profile.User, profile.Database, capabilities.ServerVersion, fingerprints), plans, warnings);
    }

    public static string PreviewSql(string title, string sourceLabel, uint sourceId, string targetLabel, uint targetId,
        PlayableBundleDatabaseTarget target, IReadOnlyList<PlayableBundleSqlTablePlan> tables)
    {
        var builder = new StringBuilder(); builder.AppendLine($"-- WoW Crucible project-scoped playable {title} clone");
        builder.AppendLine($"-- {sourceLabel} ({sourceId}) -> {targetLabel} ({targetId})"); builder.AppendLine($"-- Target: {target.User}@{target.Host}:{target.Port}/{target.Database}");
        builder.AppendLine($"-- New identity rows use INSERT. Existing mask rows use preimage-guarded bit expansion without removing source access; no row is deleted."); builder.AppendLine("START TRANSACTION;");
        foreach (var table in tables) foreach (var row in table.Rows)
        {
            if (row is { IsGuardedUpdate: true, UpdateColumn: { } column, UpdateBefore: { } before, UpdateAfter: { } after })
            {
                builder.Append("UPDATE ").Append(Quote(table.Table)).Append(" SET ").Append(Quote(column)).Append('=').Append(Literal(after)).Append(" WHERE ")
                    .Append(string.Join(" AND ", row.SourceKey.Select(key => $"{Quote(key.Column)} <=> {Literal(key.Value)}"))).Append(" AND ").Append(Quote(column)).Append(" <=> ").Append(Literal(before)).AppendLine(";");
            }
            else
            {
                var values = row.Values; builder.Append("INSERT INTO ").Append(Quote(table.Table)).Append(" (")
                    .Append(string.Join(',', values.Select(value => Quote(value.Column)))).Append(") VALUES (")
                    .Append(string.Join(',', values.Select(value => Literal(value.Value)))).AppendLine(");");
            }
        }
        builder.AppendLine("COMMIT;"); return builder.ToString();
    }

    private static bool IsCandidate(DatabaseTableCapability table, PlayableBundleIdentityKind kind)
    {
        if (DirectColumn(table, kind) is null && MaskColumn(table, kind) is null) return false;
        return IsRecognizedIdentityTable(kind, table.Name);
    }

    public static bool IsRecognizedIdentityTable(PlayableBundleIdentityKind kind, string tableName) =>
        tableName.StartsWith("playercreateinfo", StringComparison.OrdinalIgnoreCase) ||
        (tableName.StartsWith("player_", StringComparison.OrdinalIgnoreCase) && tableName.Contains("stat", StringComparison.OrdinalIgnoreCase)) ||
        (kind == PlayableBundleIdentityKind.Class ? ClassOverlayTables : RaceOverlayTables).Contains(tableName);

    public static bool RequiresGuardedMaskExpansion(string selectorColumn, IEnumerable<string> primaryColumns)
    {
        var keys = primaryColumns.ToArray(); return keys.Length > 0 && keys.All(column => !column.Equals(selectorColumn, StringComparison.OrdinalIgnoreCase));
    }

    private static DatabaseColumnCapability? DirectColumn(DatabaseTableCapability table, PlayableBundleIdentityKind kind)
    {
        if (kind == PlayableBundleIdentityKind.Class && table.Name.Equals("chrclasses_dbc", StringComparison.OrdinalIgnoreCase)) return table.Find("ID") ?? table.Find("id");
        if (kind == PlayableBundleIdentityKind.Race && table.Name.Equals("chrraces_dbc", StringComparison.OrdinalIgnoreCase)) return table.Find("ID") ?? table.Find("id");
        return kind switch
        {
            PlayableBundleIdentityKind.Class => table.Find("class") ?? table.Find("Class") ?? table.Find("classID") ?? table.Find("ClassID"),
            PlayableBundleIdentityKind.Race => table.Find("race") ?? table.Find("Race") ?? table.Find("raceID") ?? table.Find("RaceID"),
            _ => null
        };
    }

    private static DatabaseColumnCapability? MaskColumn(DatabaseTableCapability table, PlayableBundleIdentityKind kind) => kind switch
    {
        PlayableBundleIdentityKind.Class => table.Find("classMask") ?? table.Find("classmask") ?? table.Find("ClassMask"),
        PlayableBundleIdentityKind.Race => table.Find("raceMask") ?? table.Find("racemask") ?? table.Find("RaceMask"),
        _ => null
    };

    private static async Task<PlayableBundleSqlTablePlan> PlanDirectAsync(MySqlConnection connection, DatabaseTableCapability table, DatabaseColumnCapability selector,
        uint sourceId, uint targetId, ICollection<string> blockers, CancellationToken cancellationToken)
    {
        var source = await ReadRowsAsync(connection, table, $"{Quote(selector.Name)} <=> @value", sourceId, cancellationToken); var target = await ReadRowsAsync(connection, table, $"{Quote(selector.Name)} <=> @value", targetId, cancellationToken);
        var targetByKey = target.GroupBy(row => Identity(table, row, null), StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal); var planned = new List<PlayableBundleSqlRow>(); var covered = 0; var conflicts = 0;
        foreach (var row in source)
        {
            var changed = new Dictionary<string, LegacyDatabaseAuditValue>(row, StringComparer.OrdinalIgnoreCase) { [selector.Name] = Scalar(targetId) }; var identity = Identity(table, changed, null);
            if (targetByKey.TryGetValue(identity, out var existing)) { if (existing.Any(candidate => RowsEqual(changed, candidate))) covered++; else { conflicts++; blockers.Add($"{table.Name} target identity {DisplayKey(table, changed)} already exists with different values."); } continue; }
            planned.Add(ToPlanRow(table, row, changed));
        }
        return new(table.Name, $"{selector.Name}={sourceId}", source.Count, covered, conflicts, planned);
    }

    private static async Task<PlayableBundleSqlTablePlan> PlanMaskAsync(MySqlConnection connection, DatabaseTableCapability table, DatabaseColumnCapability selector,
        uint sourceMask, uint targetMask, ICollection<string> blockers, CancellationToken cancellationToken)
    {
        var source = await ReadRowsAsync(connection, table, $"({Quote(selector.Name)} & @value) <> 0", sourceMask, cancellationToken);
        var primary = table.Columns.Where(column => column.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase)).OrderBy(column => column.Ordinal).ToArray();
        if (RequiresGuardedMaskExpansion(selector.Name, primary.Select(column => column.Name)))
        {
            var updates = new List<PlayableBundleSqlRow>(); var coveredUpdates = 0;
            foreach (var row in source)
            {
                if (!row.TryGetValue(selector.Name, out var before) || before.State != LegacyDatabaseAuditValueState.Scalar || !uint.TryParse(before.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var current)) { blockers.Add($"{table.Name}.{selector.Name} contains a non-unsigned mask that cannot be expanded safely."); continue; }
                if ((current & targetMask) != 0) { coveredUpdates++; continue; }
                var after = Scalar(current | targetMask); var changed = new Dictionary<string, LegacyDatabaseAuditValue>(row, StringComparer.OrdinalIgnoreCase) { [selector.Name] = after }; updates.Add(ToPlanRow(table, row, changed, selector.Name, before, after));
            }
            return new(table.Name, $"guarded {selector.Name} bit expansion from 0x{sourceMask:X8} to include 0x{targetMask:X8}", source.Count, coveredUpdates, 0, updates);
        }
        var target = await ReadRowsAsync(connection, table, $"({Quote(selector.Name)} & @value) <> 0", targetMask, cancellationToken);
        var targetBySemanticKey = target.GroupBy(row => Identity(table, row, selector.Name), StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal); var planned = new List<PlayableBundleSqlRow>(); var covered = 0; var conflicts = 0;
        foreach (var row in source)
        {
            var changed = new Dictionary<string, LegacyDatabaseAuditValue>(row, StringComparer.OrdinalIgnoreCase) { [selector.Name] = Scalar(targetMask) }; var identity = Identity(table, changed, selector.Name);
            if (targetBySemanticKey.TryGetValue(identity, out var candidates)) { if (candidates.Any(candidate => RowsEqualExcept(changed, candidate, selector.Name))) covered++; else { conflicts++; blockers.Add($"{table.Name} already has target-mask data for {DisplayKey(table, changed, selector.Name)} with different values."); } continue; }
            planned.Add(ToPlanRow(table, row, changed));
        }
        return new(table.Name, $"{selector.Name} includes 0x{sourceMask:X8}", source.Count, covered, conflicts, planned);
    }

    private static async Task<List<Dictionary<string, LegacyDatabaseAuditValue>>> ReadRowsAsync(MySqlConnection connection, DatabaseTableCapability table, string where, object value, CancellationToken cancellationToken)
    {
        var columns = table.Columns.Where(column => !column.Extra.Contains("generated", StringComparison.OrdinalIgnoreCase)).OrderBy(column => column.Ordinal).ToArray(); var primary = columns.Where(column => column.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase)).ToArray(); var order = primary.Length == 0 ? string.Empty : $" ORDER BY {string.Join(',', primary.Select(column => Quote(column.Name)))}";
        await using var command = new MySqlCommand($"SELECT {string.Join(',', columns.Select(column => Quote(column.Name)))} FROM {Quote(table.Name)} WHERE {where}{order} LIMIT 100001", connection) { CommandTimeout = 120 }; command.Parameters.AddWithValue("@value", value);
        var rows = new List<Dictionary<string, LegacyDatabaseAuditValue>>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (rows.Count == 100_000) throw new InvalidDataException($"{table.Name} matched more than 100,000 identity rows; refine the adapter before cloning.");
            var row = new Dictionary<string, LegacyDatabaseAuditValue>(StringComparer.OrdinalIgnoreCase); for (var index = 0; index < reader.FieldCount; index++) row[reader.GetName(index)] = Encode(reader.IsDBNull(index) ? null : reader.GetValue(index)); rows.Add(row);
        }
        rows.Sort((left, right) => string.CompareOrdinal(Identity(table, left, null), Identity(table, right, null))); return rows;
    }

    private static PlayableBundleSqlRow ToPlanRow(DatabaseTableCapability table, IReadOnlyDictionary<string, LegacyDatabaseAuditValue> source, IReadOnlyDictionary<string, LegacyDatabaseAuditValue> target,
        string? updateColumn = null, LegacyDatabaseAuditValue? updateBefore = null, LegacyDatabaseAuditValue? updateAfter = null)
    {
        var primary = table.Columns.Where(column => column.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase)).OrderBy(column => column.Ordinal).ToArray();
        IReadOnlyList<LegacyDatabaseAuditKeyPart> Key(IReadOnlyDictionary<string, LegacyDatabaseAuditValue> row) => (primary.Length > 0 ? primary.Select(column => column.Name) : row.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)).Select(name => new LegacyDatabaseAuditKeyPart(name, row[name])).ToArray();
        var values = table.Columns.Where(column => target.ContainsKey(column.Name) && !column.Extra.Contains("generated", StringComparison.OrdinalIgnoreCase)).OrderBy(column => column.Ordinal).Select(column => new PlayableBundleSqlValue(column.Name, target[column.Name])).ToArray(); return new(Key(source), Key(target), values, updateColumn, updateBefore, updateAfter);
    }

    private static string Identity(DatabaseTableCapability table, IReadOnlyDictionary<string, LegacyDatabaseAuditValue> row, string? omit)
    {
        var primary = table.Columns.Where(column => column.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase) && !column.Name.Equals(omit, StringComparison.OrdinalIgnoreCase)).OrderBy(column => column.Ordinal).Select(column => column.Name).ToArray(); var names = primary.Length > 0 ? primary : row.Keys.Where(name => !name.Equals(omit, StringComparison.OrdinalIgnoreCase)).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        return string.Join('\u001e', names.Select(name => $"{name}\u001d{(int)row[name].State}\u001d{row[name].Value}"));
    }

    private static string DisplayKey(DatabaseTableCapability table, IReadOnlyDictionary<string, LegacyDatabaseAuditValue> row, string? omit = null)
    {
        var primary = table.Columns.Where(column => column.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase) && !column.Name.Equals(omit, StringComparison.OrdinalIgnoreCase)).OrderBy(column => column.Ordinal).Select(column => column.Name).ToArray(); var names = primary.Length > 0 ? primary : row.Keys.Where(name => !name.Equals(omit, StringComparison.OrdinalIgnoreCase)).Take(4).ToArray(); return string.Join(", ", names.Select(name => $"{name}={row[name].Value ?? "NULL"}"));
    }

    private static bool RowsEqual(IReadOnlyDictionary<string, LegacyDatabaseAuditValue> left, IReadOnlyDictionary<string, LegacyDatabaseAuditValue> right) => RowsEqualExcept(left, right, null);
    private static bool RowsEqualExcept(IReadOnlyDictionary<string, LegacyDatabaseAuditValue> left, IReadOnlyDictionary<string, LegacyDatabaseAuditValue> right, string? omit) => left.Where(pair => !pair.Key.Equals(omit, StringComparison.OrdinalIgnoreCase)).All(pair => right.TryGetValue(pair.Key, out var value) && Equal(pair.Value, value)) && right.Keys.Where(name => !name.Equals(omit, StringComparison.OrdinalIgnoreCase)).All(left.ContainsKey);
    private static bool Equal(LegacyDatabaseAuditValue left, LegacyDatabaseAuditValue right) => left.State == right.State && string.Equals(left.Value, right.Value, StringComparison.Ordinal);
    private static LegacyDatabaseAuditValue Encode(object? value) => value switch { null or DBNull => LegacyDatabaseAuditValue.Null, byte[] bytes => new(LegacyDatabaseAuditValueState.Binary, Convert.ToBase64String(bytes)), DateTime date => new(LegacyDatabaseAuditValueState.Scalar, date.ToString("O", CultureInfo.InvariantCulture)), DateTimeOffset date => new(LegacyDatabaseAuditValueState.Scalar, date.ToString("O", CultureInfo.InvariantCulture)), TimeSpan span => new(LegacyDatabaseAuditValueState.Scalar, span.ToString("c", CultureInfo.InvariantCulture)), IFormattable formattable => new(LegacyDatabaseAuditValueState.Scalar, formattable.ToString(null, CultureInfo.InvariantCulture)), _ => new(LegacyDatabaseAuditValueState.Scalar, Convert.ToString(value, CultureInfo.InvariantCulture)) };
    private static LegacyDatabaseAuditValue Scalar(uint value) => new(LegacyDatabaseAuditValueState.Scalar, value.ToString(CultureInfo.InvariantCulture));
    private static string Quote(string value) => ItemWritePlan.QuoteIdentifier(value);
    private static string Literal(LegacyDatabaseAuditValue value) => value.State switch { LegacyDatabaseAuditValueState.Null => "NULL", LegacyDatabaseAuditValueState.Binary => $"X'{Convert.ToHexString(Convert.FromBase64String(value.Value ?? string.Empty))}'", LegacyDatabaseAuditValueState.Scalar => $"CONVERT(X'{Convert.ToHexString(Encoding.UTF8.GetBytes(value.Value ?? string.Empty))}' USING utf8mb4)", _ => throw new InvalidDataException("Playable bundle SQL contains an unresolved value.") };
}
