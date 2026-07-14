using MySqlConnector;

namespace WoWCrucible.Core;

public sealed record DatabaseConnectionProfile(string Host, uint Port, string User, string Password, string Database, MySqlSslMode SslMode = MySqlSslMode.Preferred);
public sealed record DatabaseColumnCapability(string Name, string DataType, string ColumnType, bool Nullable, string? DefaultValue, string Key, string Extra, int Ordinal);
public sealed record DatabaseTableCapability(string Name, IReadOnlyList<DatabaseColumnCapability> Columns)
{
    public DatabaseColumnCapability? Find(string name) => Columns.FirstOrDefault(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
public sealed record DatabaseCapabilities(string ServerVersion, string Database, IReadOnlyDictionary<string, DatabaseTableCapability> Tables)
{
    public DatabaseTableCapability? FindTable(string name) => Tables.TryGetValue(name, out var table) ? table : null;
}

public sealed class DatabaseCapabilityService
{
    private static readonly string[] RelevantTables = ["item_template", "creature_template", "quest_template", "npc_vendor", "creature_loot_template", "gameobject_template", "spell_proc"];

    public async Task<DatabaseCapabilities> InspectAsync(DatabaseConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(BuildConnectionString(profile));
        await connection.OpenAsync(cancellationToken);
        string version;
        await using (var versionCommand = new MySqlCommand("SELECT VERSION()", connection))
            version = Convert.ToString(await versionCommand.ExecuteScalarAsync(cancellationToken)) ?? "Unknown";

        const string sql = """
            SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, COLUMN_TYPE, IS_NULLABLE,
                   COLUMN_DEFAULT, COLUMN_KEY, EXTRA, ORDINAL_POSITION
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = @database
              AND TABLE_NAME IN ('item_template','creature_template','quest_template','npc_vendor','creature_loot_template','gameobject_template','spell_proc')
            ORDER BY TABLE_NAME, ORDINAL_POSITION
            """;
        var columns = new Dictionary<string, List<DatabaseColumnCapability>>(StringComparer.OrdinalIgnoreCase);
        await using var command = new MySqlCommand(sql, connection) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("@database", profile.Database);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var table = reader.GetString(0);
            if (!columns.TryGetValue(table, out var list)) columns[table] = list = [];
            list.Add(new(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4) == "YES",
                reader.IsDBNull(5) ? null : Convert.ToString(reader.GetValue(5)), reader.GetString(6), reader.GetString(7), reader.GetInt32(8)));
        }
        var tables = columns.ToDictionary(pair => pair.Key, pair => new DatabaseTableCapability(pair.Key, pair.Value), StringComparer.OrdinalIgnoreCase);
        return new(version, profile.Database, tables);
    }

    public static string BuildConnectionString(DatabaseConnectionProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Host) || string.IsNullOrWhiteSpace(profile.User) || string.IsNullOrWhiteSpace(profile.Database))
            throw new ArgumentException("Host, user, and database are required.");
        return new MySqlConnectionStringBuilder
        {
            Server = profile.Host, Port = profile.Port, UserID = profile.User, Password = profile.Password,
            Database = profile.Database, SslMode = profile.SslMode, ConnectionTimeout = 5,
            DefaultCommandTimeout = 15, AllowUserVariables = false, Pooling = true
        }.ConnectionString;
    }

    public static IReadOnlyList<string> ExpectedTables => RelevantTables;
}
