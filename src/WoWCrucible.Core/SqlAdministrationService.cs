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
public sealed record SqlPrivilegeInfo(string Name, string Context, string Comment)
{
    public string Display => $"{Name} · {Context}{(string.IsNullOrWhiteSpace(Comment) ? string.Empty : $" · {Comment}")}";
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
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        const string columnsSql = "SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA='mysql' AND TABLE_NAME='user'"; var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var columnsCommand = new MySqlCommand(columnsSql, connection)) await using (var columnsReader = await columnsCommand.ExecuteReaderAsync(cancellationToken)) while (await columnsReader.ReadAsync(cancellationToken)) columns.Add(columnsReader.GetString(0));
        if (!columns.Contains("User") || !columns.Contains("Host")) throw new NotSupportedException("The server's mysql.user metadata does not expose User and Host account identity fields.");
        string Optional(string name) => columns.Contains(name) ? ItemWritePlan.QuoteIdentifier(name) : $"NULL AS {ItemWritePlan.QuoteIdentifier(name)}";
        var sql = $"SELECT `User`, `Host`, {Optional("account_locked")}, {Optional("password_expired")}, {Optional("plugin")} FROM mysql.user ORDER BY `User`, `Host`";
        await using var command = new MySqlCommand(sql, connection); await using var reader = await command.ExecuteReaderAsync(cancellationToken); var result = new List<SqlUserAccountInfo>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        return result;
    }

    public async Task<IReadOnlyList<string>> ReadGrantsAsync(DatabaseConnectionProfile profile, string user, string host, CancellationToken cancellationToken = default)
    {
        var account = QuoteAccount(user, host); await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand($"SHOW GRANTS FOR {account}", connection); await using var reader = await command.ExecuteReaderAsync(cancellationToken); var result = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0)); return result;
    }

    public async Task<IReadOnlyList<SqlPrivilegeInfo>> ReadPrivilegesAsync(DatabaseConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("SHOW PRIVILEGES", connection); await using var reader = await command.ExecuteReaderAsync(cancellationToken); var result = new List<SqlPrivilegeInfo>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2))); return result.OrderBy(privilege => privilege.Name).ToArray();
    }

    public async Task CreateUserAsync(DatabaseConnectionProfile profile, string user, string host, string password, bool locked, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("A non-empty password is required for guided account creation.");
        await ExecuteAccountSqlAsync(profile, BuildCreateUserSql(user, host, locked), password, cancellationToken);
    }

    public async Task ChangePasswordAsync(DatabaseConnectionProfile profile, string user, string host, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("A non-empty replacement password is required.");
        await ExecuteAccountSqlAsync(profile, BuildChangePasswordSql(user, host), password, cancellationToken);
    }

    public Task SetAccountLockAsync(DatabaseConnectionProfile profile, string user, string host, bool locked, CancellationToken cancellationToken = default) =>
        ExecuteDdlAsync(profile, BuildAccountLockSql(user, host, locked), cancellationToken);

    public Task DropUserAsync(DatabaseConnectionProfile profile, string user, string host, CancellationToken cancellationToken = default) =>
        ExecuteDdlAsync(profile, BuildDropUserSql(user, host), cancellationToken);

    public Task GrantAsync(DatabaseConnectionProfile profile, string user, string host, string database, string? table, bool global, IReadOnlyList<string> privileges, IReadOnlyList<SqlPrivilegeInfo> supportedPrivileges, bool withGrantOption, CancellationToken cancellationToken = default) =>
        ExecuteDdlAsync(profile, BuildGrantSql(user, host, database, table, global, privileges, supportedPrivileges, withGrantOption), cancellationToken);

    public Task RevokeAsync(DatabaseConnectionProfile profile, string user, string host, string database, string? table, bool global, IReadOnlyList<string> privileges, IReadOnlyList<SqlPrivilegeInfo> supportedPrivileges, CancellationToken cancellationToken = default) =>
        ExecuteDdlAsync(profile, BuildRevokeSql(user, host, database, table, global, privileges, supportedPrivileges), cancellationToken);

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

    public static string BuildCreateUserSql(string user, string host, bool locked) => $"CREATE USER {QuoteAccount(user, host)} IDENTIFIED BY @crucible_new_password ACCOUNT {(locked ? "LOCK" : "UNLOCK")};";
    public static string BuildChangePasswordSql(string user, string host) => $"ALTER USER {QuoteAccount(user, host)} IDENTIFIED BY @crucible_new_password;";
    public static string BuildAccountLockSql(string user, string host, bool locked) => $"ALTER USER {QuoteAccount(user, host)} ACCOUNT {(locked ? "LOCK" : "UNLOCK")};";
    public static string BuildDropUserSql(string user, string host) => $"DROP USER {QuoteAccount(user, host)};";
    public static string BuildGrantSql(string user, string host, string database, string? table, bool global, IReadOnlyList<string> privileges, IReadOnlyList<SqlPrivilegeInfo> supportedPrivileges, bool withGrantOption)
    {
        var normalized = ValidatePrivileges(privileges, supportedPrivileges); var target = BuildPrivilegeTarget(database, table, global);
        return $"GRANT {string.Join(", ", normalized)} ON {target} TO {QuoteAccount(user, host)}{(withGrantOption ? " WITH GRANT OPTION" : string.Empty)};";
    }
    public static string BuildRevokeSql(string user, string host, string database, string? table, bool global, IReadOnlyList<string> privileges, IReadOnlyList<SqlPrivilegeInfo> supportedPrivileges)
    {
        var normalized = ValidatePrivileges(privileges, supportedPrivileges); var target = BuildPrivilegeTarget(database, table, global);
        return $"REVOKE {string.Join(", ", normalized)} ON {target} FROM {QuoteAccount(user, host)};";
    }
    public static string RedactPasswordSql(string sql) => sql.Replace("@crucible_new_password", "<password supplied in memory>", StringComparison.Ordinal);

    private static string ValidateIndexName(string value)
    {
        value = value.Trim(); if (value.Length is < 1 or > 64 || value.Any(character => !(char.IsLetterOrDigit(character) || character is '_' or '$'))) throw new ArgumentException("Index name must contain 1–64 letters, digits, underscores, or dollar signs."); return value;
    }
    private static IReadOnlyList<string> ValidateColumns(DatabaseTableCapability table, IReadOnlyList<string> columns)
    {
        var result = columns.Select(column => column.Trim()).Where(column => column.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); if (result.Length == 0) throw new ArgumentException("Select at least one index column.");
        var unknown = result.Where(column => table.Find(column) is null).ToArray(); if (unknown.Length > 0) throw new ArgumentException($"Unknown {table.Name} column(s): {string.Join(", ", unknown)}."); return result;
    }
    private static string QuoteAccount(string user, string host)
    {
        user = ValidateAccountPart(user, "user", 32, allowWildcard: false); host = ValidateAccountPart(host, "host", 255, allowWildcard: true);
        return $"'{user.Replace("'", "''", StringComparison.Ordinal)}'@'{host.Replace("'", "''", StringComparison.Ordinal)}'";
    }
    private static string ValidateAccountPart(string value, string label, int maximumLength, bool allowWildcard)
    {
        value = value.Trim(); if (value.Length is < 1 || value.Length > maximumLength) throw new ArgumentException($"Account {label} must contain 1–{maximumLength} characters.");
        if (value.Any(character => char.IsControl(character) || character is '\0' or ';' or '\\')) throw new ArgumentException($"Account {label} contains a control character, backslash, or statement delimiter.");
        if (!allowWildcard && value.Contains('%')) throw new ArgumentException("Account user names cannot contain a host wildcard in Crucible's guided editor."); return value;
    }
    private static IReadOnlyList<string> ValidatePrivileges(IReadOnlyList<string> privileges, IReadOnlyList<SqlPrivilegeInfo> supportedPrivileges)
    {
        var requested = privileges.Select(NormalizePrivilege).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); if (requested.Length == 0) throw new ArgumentException("Select at least one privilege.");
        if (requested.Contains("ALL PRIVILEGES", StringComparer.OrdinalIgnoreCase) && requested.Length != 1) throw new ArgumentException("ALL PRIVILEGES must be selected by itself.");
        var supported = supportedPrivileges.Select(privilege => NormalizePrivilege(privilege.Name)).Append("ALL PRIVILEGES").ToHashSet(StringComparer.OrdinalIgnoreCase); var unknown = requested.Where(privilege => !supported.Contains(privilege)).ToArray();
        if (unknown.Length > 0) throw new ArgumentException($"Unsupported privilege(s) on this server: {string.Join(", ", unknown)}.");
        if (requested.Contains("GRANT OPTION", StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Use the separate WITH GRANT OPTION control instead of listing GRANT OPTION as an ordinary privilege."); return requested;
    }
    private static string NormalizePrivilege(string value) => string.Join(' ', value.Trim().Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    private static string BuildPrivilegeTarget(string database, string? table, bool global)
    {
        if (global)
        {
            if (!string.IsNullOrWhiteSpace(table)) throw new ArgumentException("A table cannot be combined with global *.* scope."); return "*.*";
        }
        database = database.Trim(); if (database.Length == 0) throw new ArgumentException("A database is required for non-global privileges.");
        return string.IsNullOrWhiteSpace(table) ? $"{ItemWritePlan.QuoteIdentifier(database)}.*" : $"{ItemWritePlan.QuoteIdentifier(database)}.{ItemWritePlan.QuoteIdentifier(table.Trim())}";
    }
    private static async Task ExecuteAccountSqlAsync(DatabaseConnectionProfile profile, string sql, string password, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken); await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@crucible_new_password", MySqlDbType.VarChar).Value = password; await command.ExecuteNonQueryAsync(cancellationToken);
    }
    private static async Task ExecuteDdlAsync(DatabaseConnectionProfile profile, string sql, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken); await using var command = new MySqlCommand(sql, connection); await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
