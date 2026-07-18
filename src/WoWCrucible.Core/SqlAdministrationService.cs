using System.Globalization;
using MySqlConnector;

namespace WoWCrucible.Core;

public sealed record SqlIndexInfo(string Name, bool Unique, string Type, IReadOnlyList<string> Columns, long? Cardinality, string Comment)
{
    public string Display => $"{Name} · {(Unique ? "UNIQUE" : "non-unique")} · {Type} · ({string.Join(", ", Columns)}){(Cardinality is null ? string.Empty : $" · ~{Cardinality:N0} values")}";
}
public sealed record SqlProcessInfo(ulong Id, string User, string Host, string? Database, string Command, long Seconds, string? State, string? Statement)
{
    public string Display => $"{Id} · {User}@{Host} · {Database ?? "(no schema)"} · {Command} · {Seconds:N0}s{(string.IsNullOrWhiteSpace(State) ? string.Empty : $" · {State}")}";
}
public sealed record SqlUserAccountInfo(string User, string Host, string? AccountLocked, string? PasswordExpired, string? AuthenticationPlugin)
{
    public string Display => $"'{User}'@'{Host}' · locked {AccountLocked ?? "unknown"} · expired {PasswordExpired ?? "unknown"} · {AuthenticationPlugin ?? "unknown plugin"}";
}

public sealed class SqlAdministrationService
{
    public async Task<string> ShowCreateTableAsync(DatabaseConnectionProfile profile, DatabaseTableCapability table, CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand($"SHOW CREATE TABLE {ItemWritePlan.QuoteIdentifier(table.Name)}", connection); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException($"SHOW CREATE TABLE returned no row for {table.Name}."); return reader.GetString(1);
    }

    public async Task<IReadOnlyList<SqlIndexInfo>> ReadIndexesAsync(DatabaseConnectionProfile profile, DatabaseTableCapability table, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT INDEX_NAME, NON_UNIQUE, SEQ_IN_INDEX, COLUMN_NAME, COLLATION, CARDINALITY, SUB_PART, INDEX_TYPE, INDEX_COMMENT
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA=@database AND TABLE_NAME=@table
            ORDER BY INDEX_NAME, SEQ_IN_INDEX
            """;
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection); command.Parameters.AddWithValue("@database", profile.Database); command.Parameters.AddWithValue("@table", table.Name);
        var rows = new List<(string Name, bool Unique, int Sequence, string Column, long? Cardinality, string Type, string Comment)>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add((reader.GetString(0), !reader.GetBoolean(1), reader.GetInt32(2), reader.IsDBNull(3) ? "(expression)" : reader.GetString(3), reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.GetString(7), reader.IsDBNull(8) ? string.Empty : reader.GetString(8)));
        return rows.GroupBy(row => row.Name, StringComparer.OrdinalIgnoreCase).Select(group => new SqlIndexInfo(group.Key, group.First().Unique, group.First().Type, group.OrderBy(row => row.Sequence).Select(row => row.Column).ToArray(), group.Max(row => row.Cardinality), group.First().Comment)).OrderBy(index => index.Name.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(index => index.Name).ToArray();
    }

    public async Task CreateIndexAsync(DatabaseConnectionProfile profile, DatabaseTableCapability table, string indexName, IReadOnlyList<string> columnNames, bool unique, CancellationToken cancellationToken = default)
    {
        indexName = ValidateIndexName(indexName); var existing = await ReadIndexesAsync(profile, table, cancellationToken);
        if (existing.Any(index => index.Name.Equals(indexName, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException($"Index {indexName} already exists on {table.Name}.");
        await ExecuteDdlAsync(profile, BuildCreateIndexSql(table, indexName, columnNames, unique), cancellationToken);
    }

    public async Task DropIndexAsync(DatabaseConnectionProfile profile, DatabaseTableCapability table, string indexName, CancellationToken cancellationToken = default)
    {
        indexName = ValidateIndexName(indexName); if (indexName.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Use an explicit reviewed ALTER TABLE plan to change a primary key; Crucible will not drop it as an ordinary index.");
        var existing = await ReadIndexesAsync(profile, table, cancellationToken); if (!existing.Any(index => index.Name.Equals(indexName, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException($"Index {indexName} no longer exists on {table.Name}.");
        await ExecuteDdlAsync(profile, BuildDropIndexSql(table, indexName), cancellationToken);
    }

    public async Task<IReadOnlyList<SqlProcessInfo>> ReadProcessesAsync(DatabaseConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("SHOW FULL PROCESSLIST", connection); await using var reader = await command.ExecuteReaderAsync(cancellationToken); var result = new List<SqlProcessInfo>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(Convert.ToUInt64(reader.GetValue(0), CultureInfo.InvariantCulture), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        return result.OrderByDescending(process => process.Seconds).ThenBy(process => process.Id).ToArray();
    }

    public async Task KillProcessAsync(DatabaseConnectionProfile profile, ulong processId, CancellationToken cancellationToken = default)
    {
        if (processId == 0) throw new ArgumentOutOfRangeException(nameof(processId)); await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand($"KILL CONNECTION {processId.ToString(CultureInfo.InvariantCulture)}", connection); await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SqlUserAccountInfo>> ReadUsersAsync(DatabaseConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT User, Host, account_locked, password_expired, plugin FROM mysql.user ORDER BY User, Host";
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken); await using var command = new MySqlCommand(sql, connection); await using var reader = await command.ExecuteReaderAsync(cancellationToken); var result = new List<SqlUserAccountInfo>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        return result;
    }

    public static string BuildJoinSql(DatabaseRelationCapability relation, DatabaseTableCapability sourceTable, DatabaseTableCapability targetTable, string joinType, int limit)
    {
        joinType = joinType.Trim().ToUpperInvariant(); if (joinType is not ("INNER" or "LEFT" or "RIGHT")) throw new ArgumentException("Join type must be INNER, LEFT, or RIGHT."); limit = Math.Clamp(limit, 1, 2000);
        if (!sourceTable.Name.Equals(relation.FromTable, StringComparison.OrdinalIgnoreCase) || !targetTable.Name.Equals(relation.ToTable, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Join table capabilities do not match the selected relationship.");
        var select = sourceTable.Columns.Select(column => $"source.{ItemWritePlan.QuoteIdentifier(column.Name)} AS {ItemWritePlan.QuoteIdentifier($"source__{column.Name}")}")
            .Concat(targetTable.Columns.Select(column => $"target.{ItemWritePlan.QuoteIdentifier(column.Name)} AS {ItemWritePlan.QuoteIdentifier($"target__{column.Name}")}"));
        return $"SELECT\n  {string.Join(",\n  ", select)}\nFROM {ItemWritePlan.QuoteIdentifier(relation.FromTable)} AS source\n{joinType} JOIN {ItemWritePlan.QuoteIdentifier(relation.ToTable)} AS target\n  ON source.{ItemWritePlan.QuoteIdentifier(relation.FromColumn)} = target.{ItemWritePlan.QuoteIdentifier(relation.ToColumn)}\nLIMIT {limit.ToString(CultureInfo.InvariantCulture)};";
    }

    public static string BuildCreateIndexSql(DatabaseTableCapability table, string indexName, IReadOnlyList<string> columnNames, bool unique)
    {
        indexName = ValidateIndexName(indexName); var columns = ValidateColumns(table, columnNames); return $"CREATE {(unique ? "UNIQUE " : string.Empty)}INDEX {ItemWritePlan.QuoteIdentifier(indexName)} ON {ItemWritePlan.QuoteIdentifier(table.Name)} ({string.Join(',', columns.Select(ItemWritePlan.QuoteIdentifier))});";
    }
    public static string BuildDropIndexSql(DatabaseTableCapability table, string indexName) { indexName = ValidateIndexName(indexName); if (indexName.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The primary key is not an ordinary removable index."); return $"DROP INDEX {ItemWritePlan.QuoteIdentifier(indexName)} ON {ItemWritePlan.QuoteIdentifier(table.Name)};"; }

    private static string ValidateIndexName(string value)
    {
        value = value.Trim(); if (value.Length is < 1 or > 64 || value.Any(character => !(char.IsLetterOrDigit(character) || character is '_' or '$'))) throw new ArgumentException("Index name must contain 1–64 letters, digits, underscores, or dollar signs."); return value;
    }
    private static IReadOnlyList<string> ValidateColumns(DatabaseTableCapability table, IReadOnlyList<string> columns)
    {
        var result = columns.Select(column => column.Trim()).Where(column => column.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); if (result.Length == 0) throw new ArgumentException("Select at least one index column.");
        var unknown = result.Where(column => table.Find(column) is null).ToArray(); if (unknown.Length > 0) throw new ArgumentException($"Unknown {table.Name} column(s): {string.Join(", ", unknown)}."); return result;
    }
    private static async Task ExecuteDdlAsync(DatabaseConnectionProfile profile, string sql, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken); await using var command = new MySqlCommand(sql, connection); await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
