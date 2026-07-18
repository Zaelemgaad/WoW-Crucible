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
public sealed record SqlRowFavorite(string Database, string Table, IReadOnlyDictionary<string, string?> Key, string Label, string Notes, DateTimeOffset AddedUtc, string? DbcPath = null, string? MpqPath = null)
{
    public string Identity => $"{Database}\u001f{Table}\u001f{string.Join("\u001e", Key.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"{pair.Key}={pair.Value}"))}";
}

public sealed class SqlWorkspaceService
{
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
