using System.Globalization;
using System.Text;
using MySqlConnector;

namespace WoWCrucible.Core;

public enum DbcSqlRowStatus { Same, SqlOverridesDbc, DbcOnly, MissingSqlRow, MissingDbcRow }
public sealed record DbcSqlAuditRow(uint Key, string Dimensions, DbcSqlRowStatus Status, IReadOnlyDictionary<string, object?> DbcValues, IReadOnlyDictionary<string, object?> SqlValues);
public sealed record DbcSqlAuditResult(ServerTableBinding Binding, string DbcPath, string KeyColumnName, IReadOnlyList<DbcSqlAuditRow> Rows, IReadOnlyDictionary<string, string>? SqlColumnNames = null)
{
    public int MismatchCount => Rows.Count(row => row.Status is DbcSqlRowStatus.SqlOverridesDbc or DbcSqlRowStatus.MissingDbcRow);
}

public sealed class DbcSqlAuditService
{
    public async Task<DbcSqlAuditResult> AuditAsync(DatabaseConnectionProfile profile, ServerTableBinding binding, string dbcPath, DbcSchemaResolution schema, DatabaseTableCapability table, CancellationToken cancellationToken = default)
    {
        if (binding.Consumption != ServerTableConsumption.SqlOverlayed || string.IsNullOrWhiteSpace(binding.SqlTableName))
            throw new InvalidOperationException($"{binding.DbcFileName} is {binding.Consumption}; it has no SQL overlay to audit for this core profile.");
        if (!table.Name.Equals(binding.SqlTableName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The inspected table is {table.Name}, but the binding requires {binding.SqlTableName}.");
        if (schema.KeyStrategy.Kind == DbcRecordKeyKind.NoStableKey)
            throw new InvalidDataException("The selected DBC schema has no proven stable key.");

        var file = WdbcFile.Load(dbcPath);
        if (file.FieldCount != schema.Columns.Count) throw new InvalidDataException("The selected schema does not match the DBC layout.");
        var sqlKeyName = schema.KeyStrategy.Kind == DbcRecordKeyKind.VirtualRowIndex ? "ID" : DbcRecordIdentity.PhysicalColumn(schema.Columns, schema.KeyStrategy)?.Name ?? throw new InvalidDataException("The physical key column is invalid.");
        var sqlKey = table.Find(sqlKeyName) ?? throw new InvalidDataException($"SQL overlay {table.Name} has no key column '{sqlKeyName}'.");
        var mappedColumns = schema.Columns.Select(column => (Column: column, Sql: FindSqlColumn(table, column.Name))).Where(pair => !pair.Column.Name.Equals(sqlKeyName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var missing = mappedColumns.Where(pair => pair.Sql is null).Select(pair => pair.Column.Name).ToArray();
        if (missing.Length > 0) throw new InvalidDataException($"SQL overlay {table.Name} is missing DBC field(s): {string.Join(", ", missing)}.");

        var selectedSqlColumns = new[] { sqlKey }.Concat(mappedColumns.Select(pair => pair.Sql!)).DistinctBy(column => column.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var sqlRows = new Dictionary<uint, Dictionary<string, object?>>(capacity: Math.Max(file.RowCount, 16));
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile));
        await connection.OpenAsync(cancellationToken);
        var sql = $"SELECT {string.Join(",", selectedSqlColumns.Select(column => Quote(column.Name)))} FROM {Quote(table.Name)} ORDER BY {Quote(sqlKey.Name)}";
        await using var command = new MySqlCommand(sql, connection) { CommandTimeout = 30 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = Convert.ToUInt32(reader[sqlKey.Name], CultureInfo.InvariantCulture);
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in mappedColumns) values[pair.Column.Name] = reader[pair.Sql!.Name] is DBNull ? null : reader[pair.Sql.Name];
            if (!sqlRows.TryAdd(key, values)) throw new InvalidDataException($"SQL overlay {table.Name} contains duplicate key {key}.");
        }

        var result = Compare(binding, dbcPath, file, schema, sqlRows.ToDictionary(pair => pair.Key, pair => (IReadOnlyDictionary<string, object?>)pair.Value), cancellationToken);
        return result with { SqlColumnNames = mappedColumns.ToDictionary(pair => pair.Column.Name, pair => pair.Sql!.Name, StringComparer.OrdinalIgnoreCase) };
    }

    public static DbcSqlAuditResult Compare(ServerTableBinding binding, string dbcPath, WdbcFile file, DbcSchemaResolution schema, IReadOnlyDictionary<uint, IReadOnlyDictionary<string, object?>> sqlRows, CancellationToken cancellationToken = default)
    {
        var sqlKeyName = schema.KeyStrategy.Kind == DbcRecordKeyKind.VirtualRowIndex ? "ID" : DbcRecordIdentity.PhysicalColumn(schema.Columns, schema.KeyStrategy)?.Name ?? throw new InvalidDataException("The physical key column is invalid.");
        var comparedColumns = schema.Columns.Where(column => !column.Name.Equals(sqlKeyName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var dbcRows = DbcRecordIdentity.IndexRows(file, schema.Columns, schema.KeyStrategy);
        var keys = dbcRows.Keys.Concat(sqlRows.Keys).Distinct().Order().ToArray();
        var result = new List<DbcSqlAuditRow>(keys.Length);
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hasDbc = dbcRows.TryGetValue(key, out var dbcRow); var hasSql = sqlRows.TryGetValue(key, out var sqlValues);
            var dbcValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (hasDbc)
                foreach (var column in comparedColumns) dbcValues[column.Name] = file.GetDisplayValue(dbcRow, column);
            var status = !hasDbc ? DbcSqlRowStatus.MissingDbcRow : !hasSql ? DbcSqlRowStatus.DbcOnly
                : comparedColumns.All(column => sqlValues!.TryGetValue(column.Name, out var sqlValue) && Equivalent(dbcValues[column.Name], sqlValue, column.Type)) ? DbcSqlRowStatus.Same : DbcSqlRowStatus.SqlOverridesDbc;
            result.Add(new(key, binding.DescribeRow(key), status, dbcValues, sqlValues ?? new Dictionary<string, object?>()));
        }
        return new(binding, Path.GetFullPath(dbcPath), sqlKeyName, result);
    }

    public static string CreateIdempotentMigration(DbcSqlAuditResult audit, IEnumerable<DbcSqlAuditRow>? selectedRows = null)
    {
        if (string.IsNullOrWhiteSpace(audit.Binding.SqlTableName)) throw new InvalidOperationException("This binding has no SQL table.");
        var rows = (selectedRows ?? audit.Rows.Where(row => row.Status == DbcSqlRowStatus.SqlOverridesDbc)).Where(row => row.DbcValues.Count > 0).ToArray();
        var columns = rows.SelectMany(row => row.DbcValues.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (rows.Length == 0 || columns.Length == 0) return "-- No DBC-to-SQL differences require migration.\n";
        var builder = new StringBuilder();
        builder.AppendLine($"-- WoW Crucible: synchronize {audit.Binding.DbcFileName} with {audit.Binding.SqlTableName}");
        builder.AppendLine($"-- Profile: {audit.Binding.Profile}; generated {DateTimeOffset.UtcNow:O}");
        string SqlName(string dbcName) => audit.SqlColumnNames?.GetValueOrDefault(dbcName) ?? dbcName;
        builder.Append($"INSERT INTO {Quote(audit.Binding.SqlTableName)} ({Quote(audit.KeyColumnName)},{string.Join(",", columns.Select(column => Quote(SqlName(column))))}) VALUES\n");
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            builder.Append($"({row.Key},{string.Join(",", columns.Select(column => Literal(row.DbcValues.GetValueOrDefault(column))))})");
            builder.AppendLine(index == rows.Length - 1 ? string.Empty : ",");
        }
        builder.Append("ON DUPLICATE KEY UPDATE ");
        builder.AppendLine(string.Join(",", columns.Select(column => $"{Quote(SqlName(column))}=VALUES({Quote(SqlName(column))})")) + ";");
        return builder.ToString();
    }

    private static bool Equivalent(object? dbc, object? sql, DbcValueType type)
    {
        if (dbc is null || sql is null) return dbc is null && sql is null;
        return type switch
        {
            DbcValueType.Float32 => Convert.ToSingle(dbc, CultureInfo.InvariantCulture).Equals(Convert.ToSingle(sql, CultureInfo.InvariantCulture)),
            DbcValueType.StringOffset => Convert.ToString(dbc, CultureInfo.InvariantCulture) == Convert.ToString(sql, CultureInfo.InvariantCulture),
            _ => Convert.ToDecimal(dbc, CultureInfo.InvariantCulture) == Convert.ToDecimal(sql, CultureInfo.InvariantCulture)
        };
    }

    private static DatabaseColumnCapability? FindSqlColumn(DatabaseTableCapability table, string dbcName)
    {
        var exact = table.Find(dbcName); if (exact is not null) return exact;
        var opening = dbcName.LastIndexOf('[');
        if (opening < 1 || !dbcName.EndsWith(']') || !int.TryParse(dbcName[(opening + 1)..^1], out var index)) return null;
        var prefix = dbcName[..opening];
        return table.Find($"{prefix}_{index + 1}") ?? table.Find($"{prefix}{index + 1}");
    }

    private static string Quote(string identifier) => $"`{identifier.Replace("`", "``")}`";
    private static string Literal(object? value) => value switch
    {
        null => "NULL",
        string text => $"'{text.Replace("\\", "\\\\").Replace("'", "''")}'",
        float number => number.ToString("R", CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => $"'{Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace("'", "''")}'"
    };
}
