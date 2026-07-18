using System.Globalization;
using System.Text;
using MySqlConnector;

namespace WoWCrucible.Core;

public enum SqlDatabaseObjectType { View, Trigger, Procedure, Function, Event }
public sealed record SqlDatabaseObjectInfo(SqlDatabaseObjectType Type, string Database, string Name, string Definer,
    string Details, DateTime? Created = null, DateTime? Modified = null, string? State = null)
{
    public string Identity => $"{Database}\u001f{Type}\u001f{Name}";
    public string Display => $"{Type} · {Name} · {Details}{(string.IsNullOrWhiteSpace(State) ? string.Empty : $" · {State}")}";
}
public sealed record SqlDatabaseObjectDefinition(SqlDatabaseObjectInfo Object, string CreateSql);
public sealed record SqlDatabaseObjectExportResult(string Path, int Objects, long Bytes);

public sealed class SqlDatabaseObjectService
{
    public async Task<IReadOnlyList<SqlDatabaseObjectInfo>> ListAsync(DatabaseConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        var result = new List<SqlDatabaseObjectInfo>();
        await ReadAsync(connection, """
            SELECT TABLE_NAME, COALESCE(DEFINER,''), SECURITY_TYPE, CHECK_OPTION, IS_UPDATABLE
            FROM information_schema.VIEWS WHERE TABLE_SCHEMA=@database ORDER BY TABLE_NAME
            """, profile.Database, reader => new(SqlDatabaseObjectType.View, profile.Database, reader.GetString(0), reader.GetString(1),
                $"security {reader.GetString(2)} · check {reader.GetString(3)} · updatable {reader.GetString(4)}"), result, cancellationToken);
        await ReadAsync(connection, """
            SELECT TRIGGER_NAME, COALESCE(DEFINER,''), ACTION_TIMING, EVENT_MANIPULATION, EVENT_OBJECT_TABLE, CREATED
            FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA=@database ORDER BY TRIGGER_NAME
            """, profile.Database, reader => new(SqlDatabaseObjectType.Trigger, profile.Database, reader.GetString(0), reader.GetString(1),
                $"{reader.GetString(2)} {reader.GetString(3)} on {reader.GetString(4)}", NullableDateTime(reader, 5)), result, cancellationToken);
        await ReadAsync(connection, """
            SELECT ROUTINE_NAME, ROUTINE_TYPE, COALESCE(DEFINER,''), SECURITY_TYPE, SQL_DATA_ACCESS, DTD_IDENTIFIER, CREATED, LAST_ALTERED
            FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA=@database ORDER BY ROUTINE_TYPE, ROUTINE_NAME
            """, profile.Database, reader =>
            {
                var type = reader.GetString(1).Equals("FUNCTION", StringComparison.OrdinalIgnoreCase) ? SqlDatabaseObjectType.Function : SqlDatabaseObjectType.Procedure;
                return new(type, profile.Database, reader.GetString(0), reader.GetString(2),
                    $"security {reader.GetString(3)} · {reader.GetString(4)}{(reader.IsDBNull(5) ? string.Empty : $" · returns {reader.GetString(5)}")}", NullableDateTime(reader, 6), NullableDateTime(reader, 7));
            }, result, cancellationToken);
        await ReadAsync(connection, """
            SELECT EVENT_NAME, COALESCE(DEFINER,''), EVENT_TYPE, STATUS, ON_COMPLETION, CREATED, LAST_ALTERED,
                   EXECUTE_AT, INTERVAL_VALUE, INTERVAL_FIELD
            FROM information_schema.EVENTS WHERE EVENT_SCHEMA=@database ORDER BY EVENT_NAME
            """, profile.Database, reader => new(SqlDatabaseObjectType.Event, profile.Database, reader.GetString(0), reader.GetString(1),
                EventDetails(reader), NullableDateTime(reader, 5), NullableDateTime(reader, 6), reader.GetString(3)), result, cancellationToken);
        return result.OrderBy(item => item.Type).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<SqlDatabaseObjectDefinition> ShowCreateAsync(DatabaseConnectionProfile profile, SqlDatabaseObjectInfo item, CancellationToken cancellationToken = default)
    {
        ValidateTarget(profile, item); await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand($"SHOW CREATE {Keyword(item.Type)} {Qualified(item.Database, item.Name)}", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException($"SHOW CREATE returned no row for {item.Display}.");
        var index = Enumerable.Range(0, reader.FieldCount).FirstOrDefault(field => reader.GetName(field).StartsWith("Create ", StringComparison.OrdinalIgnoreCase), -1);
        if (index < 0 || reader.IsDBNull(index)) throw new InvalidDataException($"SHOW CREATE did not expose a definition column for {item.Display}.");
        return new(item, reader.GetString(index));
    }

    public async Task<SqlDatabaseObjectExportResult> ExportAsync(DatabaseConnectionProfile profile, string outputPath, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        outputPath = Path.GetFullPath(outputPath); if (File.Exists(outputPath) && !overwrite) throw new IOException($"Output already exists: {outputPath}");
        var objects = await ListAsync(profile, cancellationToken); var builder = new StringBuilder();
        builder.AppendLine($"-- WoW Crucible exact database-object export"); builder.AppendLine($"-- Database: {profile.Database}"); builder.AppendLine($"-- Generated: {DateTimeOffset.UtcNow:O}"); builder.AppendLine("-- Review DEFINER clauses before importing on another server."); builder.AppendLine();
        foreach (var item in objects)
        {
            cancellationToken.ThrowIfCancellationRequested(); var definition = await ShowCreateAsync(profile, item, cancellationToken);
            builder.AppendLine($"-- {item.Type} {item.Database}.{item.Name}"); builder.AppendLine("DELIMITER $$"); builder.Append(definition.CreateSql.Trim().TrimEnd(';')).AppendLine("$$"); builder.AppendLine("DELIMITER ;"); builder.AppendLine();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!); var temporary = outputPath + $".{Guid.NewGuid():N}.tmp";
        try { await File.WriteAllTextAsync(temporary, builder.ToString(), new UTF8Encoding(false), cancellationToken); File.Move(temporary, outputPath, overwrite); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new(outputPath, objects.Count, new FileInfo(outputPath).Length);
    }

    public static string BuildDropSql(SqlDatabaseObjectInfo item) => $"DROP {Keyword(item.Type)} {Qualified(item.Database, ValidateName(item.Name))};";

    public async Task DropAsync(DatabaseConnectionProfile profile, SqlDatabaseObjectInfo item, CancellationToken cancellationToken = default)
    {
        ValidateTarget(profile, item); var current = await ListAsync(profile, cancellationToken);
        if (!current.Any(candidate => candidate.Identity.Equals(item.Identity, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException($"{item.Display} no longer exists.");
        await ExecuteDdlAsync(profile, BuildDropSql(item), cancellationToken);
    }

    public static string BuildCreateOrReplaceViewSql(string database, string name, string selectSql)
    {
        database = ValidateName(database); name = ValidateName(name); var statements = SqlReadBatchParser.Split(selectSql);
        if (statements.Count != 1 || !SqlReadBatchParser.IsReadOnlyStatement(statements[0]) || !FirstToken(statements[0]).Equals("SELECT", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A guided view must contain exactly one SELECT statement. SHOW/DESCRIBE/EXPLAIN, writes, batches, and SELECT file output are blocked.");
        return $"CREATE OR REPLACE VIEW {Qualified(database, name)} AS\n{statements[0].Trim().TrimEnd(';')};";
    }

    public async Task CreateOrReplaceViewAsync(DatabaseConnectionProfile profile, string name, string selectSql, CancellationToken cancellationToken = default)
        => await ExecuteDdlAsync(profile, BuildCreateOrReplaceViewSql(profile.Database, name, selectSql), cancellationToken);

    public static string BuildEventStateSql(SqlDatabaseObjectInfo item, bool enabled)
    {
        if (item.Type != SqlDatabaseObjectType.Event) throw new ArgumentException("Only scheduled events have an ENABLE/DISABLE state.");
        return $"ALTER EVENT {Qualified(item.Database, ValidateName(item.Name))} {(enabled ? "ENABLE" : "DISABLE")};";
    }

    public async Task SetEventEnabledAsync(DatabaseConnectionProfile profile, SqlDatabaseObjectInfo item, bool enabled, CancellationToken cancellationToken = default)
    {
        ValidateTarget(profile, item); await ExecuteDdlAsync(profile, BuildEventStateSql(item, enabled), cancellationToken);
    }

    private static async Task ReadAsync(MySqlConnection connection, string sql, string database, Func<MySqlDataReader, SqlDatabaseObjectInfo> map,
        ICollection<SqlDatabaseObjectInfo> output, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(sql, connection); command.Parameters.AddWithValue("@database", database); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) output.Add(map(reader));
    }
    private static DateTime? NullableDateTime(MySqlDataReader reader, int index) => reader.IsDBNull(index) ? null : Convert.ToDateTime(reader.GetValue(index), CultureInfo.InvariantCulture);
    private static string EventDetails(MySqlDataReader reader)
    {
        if (reader.GetString(2).Equals("ONE TIME", StringComparison.OrdinalIgnoreCase)) return reader.IsDBNull(7) ? "one time" : $"one time at {reader.GetValue(7)}";
        var value = reader.IsDBNull(8) ? "?" : reader.GetString(8); var field = reader.IsDBNull(9) ? "?" : reader.GetString(9); return $"recurring every {value} {field}";
    }
    private static void ValidateTarget(DatabaseConnectionProfile profile, SqlDatabaseObjectInfo item)
    {
        if (!profile.Database.Equals(item.Database, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"The selected object belongs to {item.Database}, not the connected schema {profile.Database}."); ValidateName(item.Name);
    }
    private static string Keyword(SqlDatabaseObjectType type) => type switch { SqlDatabaseObjectType.View => "VIEW", SqlDatabaseObjectType.Trigger => "TRIGGER", SqlDatabaseObjectType.Procedure => "PROCEDURE", SqlDatabaseObjectType.Function => "FUNCTION", SqlDatabaseObjectType.Event => "EVENT", _ => throw new ArgumentOutOfRangeException(nameof(type)) };
    private static string Qualified(string database, string name) => $"{ItemWritePlan.QuoteIdentifier(ValidateName(database))}.{ItemWritePlan.QuoteIdentifier(ValidateName(name))}";
    private static string ValidateName(string value) { value = value.Trim(); if (value.Length is < 1 or > 64 || value.Any(character => char.IsControl(character) || character is '\0' or ';')) throw new ArgumentException("Database object names must contain 1–64 non-control characters without statement delimiters."); return value; }
    private static string FirstToken(string sql) { var text = sql.TrimStart('\uFEFF', ' ', '\t', '\r', '\n'); while (text.StartsWith("--", StringComparison.Ordinal) || text.StartsWith('#') || text.StartsWith("/*", StringComparison.Ordinal)) { if (text.StartsWith("/*", StringComparison.Ordinal)) { var end = text.IndexOf("*/", StringComparison.Ordinal); if (end < 0) return string.Empty; text = text[(end + 2)..].TrimStart(); } else { var end = text.IndexOf('\n'); if (end < 0) return string.Empty; text = text[(end + 1)..].TrimStart(); } } return new string(text.TakeWhile(char.IsLetter).ToArray()); }
    private static async Task ExecuteDdlAsync(DatabaseConnectionProfile profile, string sql, CancellationToken cancellationToken) { await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken); await using var command = new MySqlCommand(sql, connection); await command.ExecuteNonQueryAsync(cancellationToken); }
}
