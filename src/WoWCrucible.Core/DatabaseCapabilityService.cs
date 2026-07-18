using MySqlConnector;

namespace WoWCrucible.Core;

public sealed record DatabaseConnectionProfile(string Host, uint Port, string User, string Password, string Database, MySqlSslMode SslMode = MySqlSslMode.Preferred);
public sealed record DatabaseColumnCapability(string Name, string DataType, string ColumnType, bool Nullable, string? DefaultValue, string Key, string Extra, int Ordinal);
public sealed record DatabaseTableCapability(string Name, IReadOnlyList<DatabaseColumnCapability> Columns)
{
    public DatabaseColumnCapability? Find(string name) => Columns.FirstOrDefault(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
public sealed record DatabaseRelationCapability(string Name, string FromTable, string FromColumn, string ToTable, string ToColumn, bool Declared, string Description)
{
    public bool Touches(string table) => FromTable.Equals(table, StringComparison.OrdinalIgnoreCase) || ToTable.Equals(table, StringComparison.OrdinalIgnoreCase);
}
public sealed record DatabaseCapabilities(string ServerVersion, string Database, IReadOnlyDictionary<string, DatabaseTableCapability> Tables,
    IReadOnlyList<DatabaseRelationCapability>? Relations = null)
{
    public DatabaseTableCapability? FindTable(string name) => Tables.TryGetValue(name, out var table) ? table : null;
    public IReadOnlyList<DatabaseTableCapability> DbcOverlayTables => Tables.Values.Where(table => table.Name.EndsWith("_dbc", StringComparison.OrdinalIgnoreCase)).OrderBy(table => table.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    public IReadOnlyList<DatabaseRelationCapability> Relationships => Relations ?? [];
}

public sealed class DatabaseCapabilityService
{
    private static readonly string[] RelevantTables = ["item_template", "creature_template", "creature_template_model", "creature", "quest_template", "creature_queststarter", "creature_questender", "gameobject_queststarter", "gameobject_questender", "npc_vendor", "creature_loot_template", "gameobject_template", "gameobject", "spell_proc"];

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
            ORDER BY TABLE_NAME, ORDINAL_POSITION
            """;
        var columns = new Dictionary<string, List<DatabaseColumnCapability>>(StringComparer.OrdinalIgnoreCase);
        await using (var command = new MySqlCommand(sql, connection) { CommandTimeout = 15 })
        {
            command.Parameters.AddWithValue("@database", profile.Database);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var table = reader.GetString(0);
                if (!columns.TryGetValue(table, out var list)) columns[table] = list = [];
                list.Add(new(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4) == "YES",
                    reader.IsDBNull(5) ? null : Convert.ToString(reader.GetValue(5)), reader.GetString(6), reader.GetString(7), reader.GetInt32(8)));
            }
        }
        var tables = columns.ToDictionary(pair => pair.Key, pair => new DatabaseTableCapability(pair.Key, pair.Value), StringComparer.OrdinalIgnoreCase);
        var relations = await ReadRelationsAsync(connection, profile.Database, tables, cancellationToken);
        return new(version, profile.Database, tables, relations);
    }

    private static async Task<IReadOnlyList<DatabaseRelationCapability>> ReadRelationsAsync(MySqlConnection connection, string database,
        IReadOnlyDictionary<string, DatabaseTableCapability> tables, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT CONSTRAINT_NAME, TABLE_NAME, COLUMN_NAME, REFERENCED_TABLE_NAME, REFERENCED_COLUMN_NAME
            FROM information_schema.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = @database AND REFERENCED_TABLE_NAME IS NOT NULL
            ORDER BY TABLE_NAME, CONSTRAINT_NAME, ORDINAL_POSITION
            """;
        var result = new List<DatabaseRelationCapability>();
        await using (var command = new MySqlCommand(sql, connection) { CommandTimeout = 15 })
        {
            command.Parameters.AddWithValue("@database", database);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), true, "Declared by the database schema"));
        }
        foreach (var relation in InferredRelations)
        {
            if (!tables.TryGetValue(relation.FromTable, out var from) || from.Find(relation.FromColumn) is null ||
                !tables.TryGetValue(relation.ToTable, out var to) || to.Find(relation.ToColumn) is null) continue;
            if (result.Any(existing => existing.FromTable.Equals(relation.FromTable, StringComparison.OrdinalIgnoreCase) && existing.FromColumn.Equals(relation.FromColumn, StringComparison.OrdinalIgnoreCase) && existing.ToTable.Equals(relation.ToTable, StringComparison.OrdinalIgnoreCase) && existing.ToColumn.Equals(relation.ToColumn, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(relation);
        }
        return result;
    }

    private static readonly DatabaseRelationCapability[] InferredRelations =
    [
        new("crucible_item_vendor", "npc_vendor", "item", "item_template", "entry", false, "AzerothCore item sold by a vendor"),
        new("crucible_item_creature_loot", "creature_loot_template", "Item", "item_template", "entry", false, "AzerothCore creature loot item"),
        new("crucible_creature_spawn", "creature", "id1", "creature_template", "entry", false, "AzerothCore creature spawn template"),
        new("crucible_creature_model", "creature_template_model", "CreatureID", "creature_template", "entry", false, "AzerothCore creature display mapping"),
        new("crucible_creature_vendor", "npc_vendor", "entry", "creature_template", "entry", false, "AzerothCore creature vendor inventory"),
        new("crucible_creature_queststarter", "creature_queststarter", "id", "creature_template", "entry", false, "AzerothCore creature quest starter"),
        new("crucible_creature_questender", "creature_questender", "id", "creature_template", "entry", false, "AzerothCore creature quest ender"),
        new("crucible_creature_startquest", "creature_queststarter", "quest", "quest_template", "ID", false, "Quest started by a creature"),
        new("crucible_creature_endquest", "creature_questender", "quest", "quest_template", "ID", false, "Quest ended by a creature"),
        new("crucible_gameobject_spawn", "gameobject", "id", "gameobject_template", "entry", false, "AzerothCore gameobject spawn template"),
        new("crucible_gameobject_display", "gameobject_template", "displayId", "gameobjectdisplayinfo_dbc", "ID", false, "Client gameobject display definition mirrored by the server"),
        new("crucible_gameobject_queststarter", "gameobject_queststarter", "id", "gameobject_template", "entry", false, "AzerothCore gameobject quest starter"),
        new("crucible_gameobject_questender", "gameobject_questender", "id", "gameobject_template", "entry", false, "AzerothCore gameobject quest ender"),
        new("crucible_gameobject_startquest", "gameobject_queststarter", "quest", "quest_template", "ID", false, "Quest started by a gameobject"),
        new("crucible_gameobject_endquest", "gameobject_questender", "quest", "quest_template", "ID", false, "Quest ended by a gameobject"),
        new("crucible_creature_gossip", "creature_template", "gossip_menu_id", "gossip_menu", "MenuID", false, "Creature default gossip menu"),
        new("crucible_gossip_text", "gossip_menu", "TextID", "npc_text", "ID", false, "Text displayed by a gossip menu"),
        new("crucible_gossip_option_menu", "gossip_menu_option", "MenuID", "gossip_menu", "MenuID", false, "Option belonging to a gossip menu"),
        new("crucible_gossip_option_submenu", "gossip_menu_option", "ActionMenuID", "gossip_menu", "MenuID", false, "Submenu opened by a gossip option"),
        new("crucible_creature_trainer", "creature_default_trainer", "CreatureId", "creature_template", "entry", false, "Creature assigned to a normalized trainer"),
        new("crucible_default_trainer", "creature_default_trainer", "TrainerId", "trainer", "Id", false, "Normalized trainer assigned to a creature"),
        new("crucible_trainer_spell", "trainer_spell", "TrainerId", "trainer", "Id", false, "Spell taught by a normalized trainer"),
        new("crucible_legacy_trainer", "npc_trainer", "ID", "creature_template", "entry", false, "Legacy trainer spell attached directly to a creature")
    ];

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
