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
    long TotalRows, int Offset, int Limit, string Search, IReadOnlyList<SqlRowRecord> Rows,
    string? FilterColumn = null, string? FilterValue = null, string? SortColumn = null, bool SortDescending = false);
public sealed record SqlQueryResult(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, int AffectedRows, TimeSpan Duration);
public sealed record SqlInsertResult(int AffectedRows, long InsertedId);
public sealed record SqlRelationshipMatch(DatabaseRelationCapability Relation, bool Outgoing, string TargetTable, string TargetColumn, object? Value, long MatchingRows);
public sealed record SqlDependencySnapshotEdge(string Relation, string Direction, string SourceTable, string SourceColumn, string TargetTable, string TargetColumn,
    object? Value, long TotalRows, bool Truncated, IReadOnlyList<SqlRowRecord> Rows, bool Declared, string Description);
public sealed record SqlDependencySnapshot(string Format, DateTimeOffset CapturedUtc, string Database, string RootTable, SqlRowRecord Root,
    int PerEdgeLimit, IReadOnlyList<SqlDependencySnapshotEdge> Edges, IReadOnlyList<string> Warnings);
public sealed record SqlRowFavorite(string Database, string Table, IReadOnlyDictionary<string, string?> Key, string Label, string Notes, DateTimeOffset AddedUtc, string? DbcPath = null, string? MpqPath = null)
{
    public string Identity => $"{Database}\u001f{Table}\u001f{string.Join("\u001e", Key.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"{pair.Key}={pair.Value}"))}";
}

public sealed class SqlWorkspaceService
{
    public async Task<IReadOnlyDictionary<string, long>> ReadTableRowCountsAsync(DatabaseConnectionProfile profile,
        IEnumerable<DatabaseTableCapability> tables, CancellationToken cancellationToken = default)
    {
        var requested = tables.OrderBy(table => table.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile));
        await connection.OpenAsync(cancellationToken);
        foreach (var table in requested)
        {
            await using var command = new MySqlCommand($"SELECT COUNT(*) FROM {ItemWritePlan.QuoteIdentifier(table.Name)}", connection) { CommandTimeout = 120 };
            result[table.Name] = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
        return result;
    }

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(DatabaseConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("SHOW DATABASES", connection); await using var reader = await command.ExecuteReaderAsync(cancellationToken); var databases = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) databases.Add(reader.GetString(0));
        return databases.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

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
        var populatedTables = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        async Task<long> CountMatchesAsync(DatabaseTableCapability targetTable, DatabaseColumnCapability targetColumn, object value)
        {
            await using var command = new MySqlCommand($"SELECT COUNT(*) FROM {ItemWritePlan.QuoteIdentifier(targetTable.Name)} WHERE {ItemWritePlan.QuoteIdentifier(targetColumn.Name)} <=> @value", connection);
            command.Parameters.AddWithValue("@value", value); var count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture); if (count > 0 || !targetTable.Name.EndsWith("_dbc", StringComparison.OrdinalIgnoreCase)) return count;
            if (!populatedTables.TryGetValue(targetTable.Name, out var populated))
            {
                await using var any = new MySqlCommand($"SELECT EXISTS(SELECT 1 FROM {ItemWritePlan.QuoteIdentifier(targetTable.Name)} LIMIT 1)", connection); populated = Convert.ToInt32(await any.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0; populatedTables[targetTable.Name] = populated;
            }
            return populated ? 0 : -1;
        }
        foreach (var relation in relations)
        {
            if (relation.FromTable.Equals(tableName, StringComparison.OrdinalIgnoreCase) && row.TryGetValue(relation.FromColumn, out var outgoingValue) && outgoingValue is not null && (relation.Declared || !IsEmptyReference(outgoingValue)))
            {
                var targetTable = capabilities.FindTable(relation.ToTable); var targetColumn = targetTable?.Find(relation.ToColumn);
                if (targetTable is not null && targetColumn is not null)
                {
                    var count = await CountMatchesAsync(targetTable, targetColumn, outgoingValue);
                    results.Add(new(relation, true, targetTable.Name, targetColumn.Name, outgoingValue, count));
                }
            }
            if (relation.ToTable.Equals(tableName, StringComparison.OrdinalIgnoreCase) && row.TryGetValue(relation.ToColumn, out var incomingValue) && incomingValue is not null && (relation.Declared || !IsEmptyReference(incomingValue)))
            {
                var sourceTable = capabilities.FindTable(relation.FromTable); var sourceColumn = sourceTable?.Find(relation.FromColumn);
                if (sourceTable is not null && sourceColumn is not null)
                {
                    var count = await CountMatchesAsync(sourceTable, sourceColumn, incomingValue);
                    if (count > 0) results.Add(new(relation, false, sourceTable.Name, sourceColumn.Name, incomingValue, count));
                }
            }
        }
        return results.OrderByDescending(result => result.MatchingRows).ThenBy(result => result.TargetTable).ThenBy(result => result.TargetColumn).ToArray();
    }

    public async Task<SqlDependencySnapshot> CaptureDependencySnapshotAsync(DatabaseConnectionProfile profile, DatabaseCapabilities capabilities,
        string tableName, SqlRowRecord root, int perEdgeLimit = 500, CancellationToken cancellationToken = default)
    {
        perEdgeLimit = Math.Clamp(perEdgeLimit, 1, 500); var matches = await AnalyzeRelationshipsAsync(profile, capabilities, tableName, root.Values, cancellationToken);
        var edges = new List<SqlDependencySnapshotEdge>(); var warnings = new List<string>();
        foreach (var match in matches)
        {
            var table = capabilities.FindTable(match.TargetTable); if (table is null) continue;
            if (match.MatchingRows < 0)
            {
                warnings.Add($"{match.TargetTable} is an empty SQL DBC mirror; resolve {match.TargetColumn}={Convert.ToString(match.Value, CultureInfo.InvariantCulture)} against the configured client/server DBC file.");
                edges.Add(new(match.Relation.Name, match.Outgoing ? "outgoing" : "incoming", match.Relation.FromTable, match.Relation.FromColumn, match.Relation.ToTable, match.Relation.ToColumn, match.Value, -1, false, [], match.Relation.Declared, match.Relation.Description));
                continue;
            }
            var page = await ReadColumnMatchesAsync(profile, table, match.TargetColumn, match.Value, perEdgeLimit, cancellationToken);
            var truncated = page.TotalRows > page.Rows.Count;
            if (truncated) warnings.Add($"{match.TargetTable}.{match.TargetColumn} has {page.TotalRows:N0} matching rows; only the first {page.Rows.Count:N0} are captured.");
            edges.Add(new(match.Relation.Name, match.Outgoing ? "outgoing" : "incoming", match.Relation.FromTable,
                match.Relation.FromColumn, match.Relation.ToTable, match.Relation.ToColumn,
                match.Value, page.TotalRows, truncated, page.Rows, match.Relation.Declared, match.Relation.Description));
        }
        warnings.Insert(0, "This is a read-only dependency snapshot, not executable SQL. Review identity allocation and target-core schema before converting any row into a deployment plan.");
        return new("wow-crucible-sql-dependency-snapshot-v1", DateTimeOffset.UtcNow, capabilities.Database, tableName, root, perEdgeLimit, edges, warnings);
    }

    private static bool IsEmptyReference(object value)
    {
        if (value is string text) return string.IsNullOrWhiteSpace(text) || text.Trim() == "0";
        if (value is byte[] bytes) return bytes.Length == 0;
        try { return Convert.ToDecimal(value, CultureInfo.InvariantCulture) == 0; } catch { return false; }
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

    public Task<SqlTablePage> ReadPageAsync(DatabaseConnectionProfile profile, DatabaseTableCapability table, int offset, int limit,
        string? search, CancellationToken cancellationToken)
        => ReadPageAsync(profile, table, offset, limit, search, cancellationToken: cancellationToken);

    public async Task<SqlTablePage> ReadPageAsync(DatabaseConnectionProfile profile, DatabaseTableCapability table, int offset, int limit,
        string? search = null, string? filterColumn = null, string? filterValue = null, string? sortColumn = null,
        bool sortDescending = false, CancellationToken cancellationToken = default)
    {
        offset = Math.Max(0, offset); limit = Math.Clamp(limit, 1, 500); search = search?.Trim() ?? string.Empty;
        filterColumn = string.IsNullOrWhiteSpace(filterColumn) ? null : table.Find(filterColumn)?.Name
            ?? throw new InvalidOperationException($"Unknown exact-filter column '{filterColumn}'.");
        filterValue = filterValue?.Trim(); if (filterColumn is null || string.IsNullOrEmpty(filterValue)) { filterColumn = null; filterValue = null; }
        sortColumn = string.IsNullOrWhiteSpace(sortColumn) ? null : table.Find(sortColumn)?.Name
            ?? throw new InvalidOperationException($"Unknown sort column '{sortColumn}'.");
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        var searchable = table.Columns.Where(column => !column.Extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase)).Take(24).ToArray();
        var predicates = new List<string>();
        var usesSearch = search.Length > 0 && searchable.Length > 0; var filterIsNull = filterValue?.Equals("<NULL>", StringComparison.OrdinalIgnoreCase) == true;
        if (usesSearch) predicates.Add($"CONCAT_WS(' ',{string.Join(',', searchable.Select(column => $"CAST({ItemWritePlan.QuoteIdentifier(column.Name)} AS CHAR)"))}) LIKE @search");
        if (filterColumn is not null) predicates.Add(filterIsNull ? $"{ItemWritePlan.QuoteIdentifier(filterColumn)} IS NULL" : $"CAST({ItemWritePlan.QuoteIdentifier(filterColumn)} AS CHAR) = @filter");
        var where = predicates.Count == 0 ? string.Empty : $" WHERE {string.Join(" AND ", predicates)}";
        await using var count = new MySqlCommand($"SELECT COUNT(*) FROM {ItemWritePlan.QuoteIdentifier(table.Name)}{where}", connection) { CommandTimeout = 120 };
        if (usesSearch) count.Parameters.AddWithValue("@search", $"%{search}%");
        if (filterColumn is not null && !filterIsNull) count.Parameters.AddWithValue("@filter", filterValue);
        var total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        var primary = table.Columns.Where(column => column.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase)).Select(column => column.Name).ToArray();
        var orderedColumns = sortColumn is not null ? new[] { sortColumn }.Concat(primary.Where(column => !column.Equals(sortColumn, StringComparison.OrdinalIgnoreCase))).ToArray() : primary;
        var order = orderedColumns.Length > 0 ? $" ORDER BY {string.Join(',', orderedColumns.Select(ItemWritePlan.QuoteIdentifier))}{(sortDescending ? " DESC" : string.Empty)}" : string.Empty;
        await using var command = new MySqlCommand($"SELECT {string.Join(',', table.Columns.Select(column => ItemWritePlan.QuoteIdentifier(column.Name)))} FROM {ItemWritePlan.QuoteIdentifier(table.Name)}{where}{order} LIMIT @limit OFFSET @offset", connection) { CommandTimeout = 120 };
        if (usesSearch) command.Parameters.AddWithValue("@search", $"%{search}%"); if (filterColumn is not null && !filterIsNull) command.Parameters.AddWithValue("@filter", filterValue); command.Parameters.AddWithValue("@limit", limit); command.Parameters.AddWithValue("@offset", offset);
        var rows = new List<SqlRowRecord>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < reader.FieldCount; index++) values[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            var key = primary.ToDictionary(name => name, name => values[name], StringComparer.OrdinalIgnoreCase);
            rows.Add(new(values, key));
        }
        return new(table.Name, table.Columns, primary, total, offset, limit, search, rows, filterColumn, filterValue, sortColumn, sortDescending);
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
        var statements = SqlReadBatchParser.Split(sql);
        if (statements.Count != 1) throw new InvalidOperationException("QueryAsync accepts exactly one read-only statement. Use QueryBatchAsync for an explicit read batch.");
        var batch = await QueryBatchAsync(profile, sql, maximumRows, cancellationToken);
        return batch.Results[0].Result;
    }

    public async Task<SqlQueryBatch> QueryBatchAsync(DatabaseConnectionProfile profile, string sql, int maximumRowsPerResult = 1000, CancellationToken cancellationToken = default)
    {
        var statements = SqlReadBatchParser.Split(sql);
        if (statements.Count == 0) throw new ArgumentException("Enter at least one query.");
        if (!statements.All(SqlReadBatchParser.IsReadOnlyStatement)) throw new InvalidOperationException("Read batches accept only SELECT, SHOW, DESCRIBE, DESC, or EXPLAIN, and reject SELECT file-output clauses. Use the separately confirmed write path for every mutation.");
        maximumRowsPerResult = Math.Clamp(maximumRowsPerResult, 1, 10000); var batchWatch = System.Diagnostics.Stopwatch.StartNew(); var results = new List<SqlQueryBatchResult>(statements.Count);
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        for (var statementIndex = 0; statementIndex < statements.Count; statementIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var statement = statements[statementIndex]; var watch = System.Diagnostics.Stopwatch.StartNew();
            await using var command = new MySqlCommand(statement, connection) { CommandTimeout = 120 };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray(); var rows = new List<IReadOnlyList<object?>>();
            while (rows.Count < maximumRowsPerResult && await reader.ReadAsync(cancellationToken)) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray());
            var truncated = rows.Count == maximumRowsPerResult && await reader.ReadAsync(cancellationToken); watch.Stop();
            results.Add(new(statementIndex + 1, statement, new(columns, rows, -1, watch.Elapsed), truncated));
        }
        batchWatch.Stop(); return new(results, batchWatch.Elapsed);
    }

    public static bool IsReadOnlyStatement(string sql) => SqlReadBatchParser.IsReadOnlyStatement(sql);
    public static bool IsReadOnlyBatch(string sql) => SqlReadBatchParser.IsReadOnlyBatch(sql);

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
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static IReadOnlyList<SqlRowFavorite> Load(string? path = null)
    {
        path = string.IsNullOrWhiteSpace(path) ? CruciblePaths.SqlFavoritesFile : Path.GetFullPath(path);
        if (!File.Exists(path)) return [];
        try
        {
            var favorites = JsonSerializer.Deserialize<List<SqlRowFavorite?>>(File.ReadAllText(path), JsonOptions) ?? [];
            if (favorites.Any(favorite => favorite is null || string.IsNullOrWhiteSpace(favorite.Database) || string.IsNullOrWhiteSpace(favorite.Table) || favorite.Key is null || favorite.Key.Count == 0 || string.IsNullOrWhiteSpace(favorite.Label)))
                throw new InvalidDataException("The favorites file contains a row without a database, table, complete key, or label.");
            return favorites.Cast<SqlRowFavorite>().ToArray();
        }
        catch
        {
            PreserveCorrupt(path);
            return [];
        }
    }
    public static IReadOnlyList<SqlRowFavorite> Save(SqlRowFavorite favorite, string? path = null)
    {
        path = string.IsNullOrWhiteSpace(path) ? CruciblePaths.SqlFavoritesFile : Path.GetFullPath(path);
        var favorites = Load(path).Where(item => !item.Identity.Equals(favorite.Identity, StringComparison.OrdinalIgnoreCase)).Append(favorite).OrderBy(item => item.Database).ThenBy(item => item.Table).ThenBy(item => item.Label).ToArray(); SaveAll(favorites, path); return favorites;
    }
    public static IReadOnlyList<SqlRowFavorite> Remove(string identity, string? path = null)
    {
        path = string.IsNullOrWhiteSpace(path) ? CruciblePaths.SqlFavoritesFile : Path.GetFullPath(path);
        var favorites = Load(path).Where(item => !item.Identity.Equals(identity, StringComparison.OrdinalIgnoreCase)).ToArray(); SaveAll(favorites, path); return favorites;
    }
    private static void SaveAll(IReadOnlyList<SqlRowFavorite> favorites, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("The favorites file must have a parent directory.")); var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(favorites, JsonOptions)); File.Move(temporary, path, true);
    }

    private static void PreserveCorrupt(string path)
    {
        try
        {
            var backup = $"{path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}.json";
            var suffix = 0;
            while (File.Exists(backup)) backup = $"{path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{++suffix}.json";
            File.Move(path, backup);
        }
        catch { }
    }
}
