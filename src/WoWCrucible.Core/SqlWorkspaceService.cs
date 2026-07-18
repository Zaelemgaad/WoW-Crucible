using System.Globalization;
using System.Text.Json;
using MySqlConnector;

namespace WoWCrucible.Core;

public sealed record SqlRowRecord(IReadOnlyDictionary<string, object?> Values, IReadOnlyDictionary<string, object?> Key)
{
    public string Display => Key.Count > 0
        ? string.Join(" · ", Key.Select(pair => $"{pair.Key}={Convert.ToString(pair.Value, CultureInfo.InvariantCulture)}"))
        : string.Join(" · ", Values.Take(3).Select(pair => $"{pair.Key}={Convert.ToString(pair.Value, CultureInfo.InvariantCulture)}"));
}

public sealed record SqlTablePage(string Table, IReadOnlyList<DatabaseColumnCapability> Columns, IReadOnlyList<string> PrimaryKey,
    long TotalRows, int Offset, int Limit, string Search, IReadOnlyList<SqlRowRecord> Rows);
public sealed record SqlQueryResult(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, int AffectedRows, TimeSpan Duration);
public sealed record SqlInsertResult(int AffectedRows, long InsertedId);
public sealed record SqlRelationshipMatch(DatabaseRelationCapability Relation, bool Outgoing, string TargetTable, string TargetColumn, object? Value, long MatchingRows);
public sealed record SqlRowFavorite(string Database, string Table, IReadOnlyDictionary<string, string?> Key, string Label, string Notes, DateTimeOffset AddedUtc, string? DbcPath = null, string? MpqPath = null)
{
    public string Identity => $"{Database}\u001f{Table}\u001f{string.Join("\u001e", Key.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"{pair.Key}={pair.Value}"))}";
}

public sealed class SqlWorkspaceService
{
    public async Task<SqlTablePage> ReadColumnMatchesAsync(DatabaseConnectionProfile profile, DatabaseTableCapability table,
        string columnName, object? value, int limit = 200, CancellationToken cancellationToken = default)
    {
        var column = table.Find(columnName) ?? throw new InvalidOperationException($"{table.Name} has no {columnName} column.");
        limit = Math.Clamp(limit, 1, 500); var quotedColumn = ItemWritePlan.QuoteIdentifier(column.Name);
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        await using var count = new MySqlCommand($"SELECT COUNT(*) FROM {ItemWritePlan.QuoteIdentifier(table.Name)} WHERE {quotedColumn} <=> @value", connection);
        count.Parameters.AddWithValue("@value", value ?? DBNull.Value); var total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        var primary = table.Columns.Where(candidate => candidate.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase)).Select(candidate => candidate.Name).ToArray();
        var order = primary.Length > 0 ? $" ORDER BY {string.Join(',', primary.Select(ItemWritePlan.QuoteIdentifier))}" : string.Empty;
        await using var command = new MySqlCommand($"SELECT {string.Join(',', table.Columns.Select(candidate => ItemWritePlan.QuoteIdentifier(candidate.Name)))} FROM {ItemWritePlan.QuoteIdentifier(table.Name)} WHERE {quotedColumn} <=> @value{order} LIMIT @limit", connection);
        command.Parameters.AddWithValue("@value", value ?? DBNull.Value); command.Parameters.AddWithValue("@limit", limit);
        var rows = new List<SqlRowRecord>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < reader.FieldCount; index++) values[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            rows.Add(new(values, primary.ToDictionary(name => name, name => values[name], StringComparer.OrdinalIgnoreCase)));
        }
        return new(table.Name, table.Columns, primary, total, 0, limit, $"{column.Name} = {Convert.ToString(value, CultureInfo.InvariantCulture)}", rows);
    }

    public async Task<IReadOnlyList<SqlRelationshipMatch>> AnalyzeRelationshipsAsync(DatabaseConnectionProfile profile, DatabaseCapabilities capabilities,
        string tableName, IReadOnlyDictionary<string, object?> row, CancellationToken cancellationToken = default)
    {
        var relations = capabilities.Relationships.Where(relation => relation.Touches(tableName)).ToArray(); var results = new List<SqlRelationshipMatch>();
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        foreach (var relation in relations)
        {
            var outgoing = relation.FromTable.Equals(tableName, StringComparison.OrdinalIgnoreCase); var sourceColumn = outgoing ? relation.FromColumn : relation.ToColumn;
            if (!row.TryGetValue(sourceColumn, out var value) || value is null) continue;
            var targetTableName = outgoing ? relation.ToTable : relation.FromTable; var targetColumnName = outgoing ? relation.ToColumn : relation.FromColumn;
            var targetTable = capabilities.FindTable(targetTableName); var targetColumn = targetTable?.Find(targetColumnName); if (targetTable is null || targetColumn is null) continue;
            await using var command = new MySqlCommand($"SELECT COUNT(*) FROM {ItemWritePlan.QuoteIdentifier(targetTable.Name)} WHERE {ItemWritePlan.QuoteIdentifier(targetColumn.Name)} <=> @value", connection);
            command.Parameters.AddWithValue("@value", value); var count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            results.Add(new(relation, outgoing, targetTable.Name, targetColumn.Name, value, count));
        }
        return results.OrderByDescending(result => result.MatchingRows).ThenBy(result => result.TargetTable).ThenBy(result => result.TargetColumn).ToArray();
    }

    public async Task<SqlRowRecord?> ReadRowAsync(DatabaseConnectionProfile profile, DatabaseTableCapability table,
        IReadOnlyDictionary<string, object?> key, CancellationToken cancellationToken = default)
    {
        if (key.Count == 0) throw new InvalidOperationException("An exact row lookup requires the complete primary key.");
        var primary = table.Columns.Where(column => column.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase)).Select(column => column.Name).ToArray();
        if (primary.Length == 0 || primary.Any(name => !key.ContainsKey(name)) || key.Keys.Any(name => !primary.Contains(name, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"The supplied key does not exactly match {table.Name}'s primary key ({string.Join(", ", primary)}).");
        var predicates = primary.Select((name, index) => $"{ItemWritePlan.QuoteIdentifier(name)} <=> @k{index}").ToArray();
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand($"SELECT {string.Join(',', table.Columns.Select(column => ItemWritePlan.QuoteIdentifier(column.Name)))} FROM {ItemWritePlan.QuoteIdentifier(table.Name)} WHERE {string.Join(" AND ", predicates)} LIMIT 2", connection);
        for (var index = 0; index < primary.Length; index++) command.Parameters.AddWithValue($"@k{index}", key[primary[index]] ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); if (!await reader.ReadAsync(cancellationToken)) return null;
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < reader.FieldCount; index++) values[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
        var rowKey = primary.ToDictionary(name => name, name => values[name], StringComparer.OrdinalIgnoreCase);
        if (await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("A supposedly unique primary key returned more than one row.");
        return new(values, rowKey);
    }

    public async Task<SqlTablePage> ReadPageAsync(DatabaseConnectionProfile profile, DatabaseTableCapability table, int offset, int limit, string? search = null, CancellationToken cancellationToken = default)
    {
        offset = Math.Max(0, offset); limit = Math.Clamp(limit, 1, 500); search = search?.Trim() ?? string.Empty;
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        var searchable = table.Columns.Where(column => !column.Extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase)).Take(24).ToArray();
        var where = search.Length == 0 || searchable.Length == 0 ? string.Empty : $" WHERE CONCAT_WS(' ',{string.Join(',', searchable.Select(column => $"CAST({ItemWritePlan.QuoteIdentifier(column.Name)} AS CHAR)"))}) LIKE @search";
        await using var count = new MySqlCommand($"SELECT COUNT(*) FROM {ItemWritePlan.QuoteIdentifier(table.Name)}{where}", connection) { CommandTimeout = 120 };
        if (search.Length > 0) count.Parameters.AddWithValue("@search", $"%{search}%");
        var total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        var primary = table.Columns.Where(column => column.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase)).Select(column => column.Name).ToArray();
        var order = primary.Length > 0 ? $" ORDER BY {string.Join(',', primary.Select(ItemWritePlan.QuoteIdentifier))}" : string.Empty;
        await using var command = new MySqlCommand($"SELECT {string.Join(',', table.Columns.Select(column => ItemWritePlan.QuoteIdentifier(column.Name)))} FROM {ItemWritePlan.QuoteIdentifier(table.Name)}{where}{order} LIMIT @limit OFFSET @offset", connection) { CommandTimeout = 120 };
        if (search.Length > 0) command.Parameters.AddWithValue("@search", $"%{search}%"); command.Parameters.AddWithValue("@limit", limit); command.Parameters.AddWithValue("@offset", offset);
        var rows = new List<SqlRowRecord>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < reader.FieldCount; index++) values[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            var key = primary.ToDictionary(name => name, name => values[name], StringComparer.OrdinalIgnoreCase);
            rows.Add(new(values, key));
        }
        return new(table.Name, table.Columns, primary, total, offset, limit, search, rows);
    }

    public async Task<int> UpdateRowAsync(DatabaseConnectionProfile profile, DatabaseTableCapability table, IReadOnlyDictionary<string, object?> key,
        IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
    {
        if (key.Count == 0) throw new InvalidOperationException("This table has no primary key; Crucible refuses an ambiguous row update.");
        var writable = table.Columns.Where(column => values.ContainsKey(column.Name) && !column.Extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase) && !column.Extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (writable.Length == 0) throw new InvalidOperationException("No writable fields were supplied.");
        foreach (var name in key.Keys) if (table.Find(name) is null) throw new InvalidOperationException($"Unknown key column {name}.");
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var assignments = writable.Select((column, index) => $"{ItemWritePlan.QuoteIdentifier(column.Name)}=@v{index}");
        var predicates = key.Keys.Select((name, index) => $"{ItemWritePlan.QuoteIdentifier(name)} <=> @k{index}");
        await using var command = new MySqlCommand($"UPDATE {ItemWritePlan.QuoteIdentifier(table.Name)} SET {string.Join(',', assignments)} WHERE {string.Join(" AND ", predicates)} LIMIT 1", connection, transaction);
        for (var index = 0; index < writable.Length; index++) command.Parameters.AddWithValue($"@v{index}", values[writable[index].Name] ?? DBNull.Value);
        var keyIndex = 0; foreach (var value in key.Values) command.Parameters.AddWithValue($"@k{keyIndex++}", value ?? DBNull.Value);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1) throw new InvalidOperationException($"Expected exactly one changed row but MySQL reported {affected}. The transaction was not committed.");
        await transaction.CommitAsync(cancellationToken); return affected;
    }

    public async Task<SqlInsertResult> InsertRowAsync(DatabaseConnectionProfile profile, DatabaseTableCapability table,
        IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
    {
        var unknown = values.Keys.Where(name => table.Find(name) is null).ToArray();
        if (unknown.Length > 0) throw new InvalidOperationException($"Unknown column(s): {string.Join(", ", unknown)}.");
        var writable = table.Columns.Where(column => values.ContainsKey(column.Name) && !column.Extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (writable.Length == 0) throw new InvalidOperationException("No writable fields were supplied.");
        var requiredMissing = table.Columns.Where(column => !column.Nullable && column.DefaultValue is null &&
            !column.Extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase) && !column.Extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase) && !values.ContainsKey(column.Name)).Select(column => column.Name).ToArray();
        if (requiredMissing.Length > 0) throw new InvalidOperationException($"Required column(s) are missing: {string.Join(", ", requiredMissing)}.");
        var key = table.Columns.Where(column => column.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase) && values.ContainsKey(column.Name)).ToArray();
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (key.Length > 0)
        {
            var predicates = key.Select((column, index) => $"{ItemWritePlan.QuoteIdentifier(column.Name)} <=> @pk{index}");
            await using var exists = new MySqlCommand($"SELECT COUNT(*) FROM {ItemWritePlan.QuoteIdentifier(table.Name)} WHERE {string.Join(" AND ", predicates)}", connection, transaction);
            for (var index = 0; index < key.Length; index++) exists.Parameters.AddWithValue($"@pk{index}", values[key[index].Name] ?? DBNull.Value);
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0)
                throw new InvalidOperationException("A row with the supplied primary key already exists. Crucible does not silently replace or upsert rows.");
        }
        var names = string.Join(',', writable.Select(column => ItemWritePlan.QuoteIdentifier(column.Name)));
        var parameters = string.Join(',', writable.Select((_, index) => $"@v{index}"));
        await using var command = new MySqlCommand($"INSERT INTO {ItemWritePlan.QuoteIdentifier(table.Name)} ({names}) VALUES ({parameters})", connection, transaction);
        for (var index = 0; index < writable.Length; index++) command.Parameters.AddWithValue($"@v{index}", values[writable[index].Name] ?? DBNull.Value);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1) throw new InvalidOperationException($"Expected exactly one inserted row but MySQL reported {affected}. The transaction was not committed.");
        var insertedId = command.LastInsertedId; await transaction.CommitAsync(cancellationToken); return new(affected, insertedId);
    }

    public async Task<int> DeleteRowAsync(DatabaseConnectionProfile profile, DatabaseTableCapability table,
        IReadOnlyDictionary<string, object?> key, CancellationToken cancellationToken = default)
    {
        if (key.Count == 0) throw new InvalidOperationException("This table has no primary key; Crucible refuses an ambiguous delete.");
        foreach (var name in key.Keys)
            if (table.Find(name) is not { Key: var kind } || !kind.Equals("PRI", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{name} is not a primary-key column of {table.Name}.");
        var predicates = key.Keys.Select((name, index) => $"{ItemWritePlan.QuoteIdentifier(name)} <=> @k{index}").ToArray();
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var count = new MySqlCommand($"SELECT COUNT(*) FROM {ItemWritePlan.QuoteIdentifier(table.Name)} WHERE {string.Join(" AND ", predicates)}", connection, transaction))
        {
            var index = 0; foreach (var value in key.Values) count.Parameters.AddWithValue($"@k{index++}", value ?? DBNull.Value);
            var matches = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            if (matches != 1) throw new InvalidOperationException($"Expected the primary key to identify exactly one row, but it matched {matches}. Nothing was deleted.");
        }
        await using var command = new MySqlCommand($"DELETE FROM {ItemWritePlan.QuoteIdentifier(table.Name)} WHERE {string.Join(" AND ", predicates)} LIMIT 1", connection, transaction);
        var keyIndex = 0; foreach (var value in key.Values) command.Parameters.AddWithValue($"@k{keyIndex++}", value ?? DBNull.Value);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1) throw new InvalidOperationException($"Expected exactly one deleted row but MySQL reported {affected}. The transaction was not committed.");
        await transaction.CommitAsync(cancellationToken); return affected;
    }

    public async Task<SqlQueryResult> QueryAsync(DatabaseConnectionProfile profile, string sql, int maximumRows = 1000, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("Enter a query.");
        if (!IsReadOnlyStatement(sql)) throw new InvalidOperationException("QueryAsync accepts only SELECT, SHOW, DESCRIBE, DESC, or EXPLAIN. Use the explicitly confirmed write path for every other statement.");
        maximumRows = Math.Clamp(maximumRows, 1, 10000); var started = System.Diagnostics.Stopwatch.StartNew();
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection) { CommandTimeout = 120 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray(); var rows = new List<IReadOnlyList<object?>>();
        while (rows.Count < maximumRows && await reader.ReadAsync(cancellationToken)) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray());
        return new(columns, rows, -1, started.Elapsed);
    }

    public static bool IsReadOnlyStatement(string sql)
    {
        var text = (sql ?? string.Empty).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        while (text.Length > 0)
        {
            if (text.StartsWith("--", StringComparison.Ordinal) || text.StartsWith('#')) { var end = text.IndexOf('\n'); if (end < 0) return false; text = text[(end + 1)..].TrimStart(); continue; }
            if (text.StartsWith("/*", StringComparison.Ordinal)) { var end = text.IndexOf("*/", StringComparison.Ordinal); if (end < 0) return false; text = text[(end + 2)..].TrimStart(); continue; }
            break;
        }
        var token = new string(text.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
        return token is "SELECT" or "SHOW" or "DESCRIBE" or "DESC" or "EXPLAIN";
    }

    public async Task<SqlQueryResult> ExecuteAsync(DatabaseConnectionProfile profile, string sql, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("Enter a statement."); var started = System.Diagnostics.Stopwatch.StartNew();
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken); await using var command = new MySqlCommand(sql, connection, transaction) { CommandTimeout = 120 };
        var affected = await command.ExecuteNonQueryAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return new([], [], affected, started.Elapsed);
    }
}

public static class SqlFavoriteStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static IReadOnlyList<SqlRowFavorite> Load()
    {
        try { return File.Exists(CruciblePaths.SqlFavoritesFile) ? JsonSerializer.Deserialize<List<SqlRowFavorite>>(File.ReadAllText(CruciblePaths.SqlFavoritesFile)) ?? [] : []; }
        catch { return []; }
    }
    public static IReadOnlyList<SqlRowFavorite> Save(SqlRowFavorite favorite)
    {
        var favorites = Load().Where(item => !item.Identity.Equals(favorite.Identity, StringComparison.OrdinalIgnoreCase)).Append(favorite).OrderBy(item => item.Database).ThenBy(item => item.Table).ThenBy(item => item.Label).ToArray(); SaveAll(favorites); return favorites;
    }
    public static IReadOnlyList<SqlRowFavorite> Remove(string identity)
    {
        var favorites = Load().Where(item => !item.Identity.Equals(identity, StringComparison.OrdinalIgnoreCase)).ToArray(); SaveAll(favorites); return favorites;
    }
    private static void SaveAll(IReadOnlyList<SqlRowFavorite> favorites)
    {
        Directory.CreateDirectory(CruciblePaths.SettingsDirectory); var temporary = CruciblePaths.SqlFavoritesFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(favorites, JsonOptions)); File.Move(temporary, CruciblePaths.SqlFavoritesFile, true);
    }
}
