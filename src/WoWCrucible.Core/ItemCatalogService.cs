using MySqlConnector;

namespace WoWCrucible.Core;

public sealed record ItemCatalogEntry(uint Entry, string Name, int Quality, int ItemLevel, uint ItemSetId, IReadOnlyList<string> AcquisitionSources)
{
    public bool HasKnownAcquisitionPath => AcquisitionSources.Count > 0;
}
public sealed record ItemAcquisitionAudit(string Database, DateTimeOffset AuditedUtc, IReadOnlyList<string> CheckedSources, IReadOnlyList<string> MissingSources,
    int TotalItems, int ObtainableItems, IReadOnlyList<ItemCatalogEntry> NoKnownAcquisitionPath);
public sealed record ItemCloneResult(uint SourceEntry, uint NewEntry, string SourceName, string NewName, uint ItemSetId, int CopiedColumns, int CopiedLocaleRows);

public sealed class ItemCatalogService
{
    private sealed record AcquisitionSpec(string Table, params string[] Columns);
    private static readonly AcquisitionSpec[] AcquisitionSpecs =
    [
        new("npc_vendor", "item"),
        new("creature_loot_template", "Item", "item"), new("gameobject_loot_template", "Item", "item"),
        new("item_loot_template", "Item", "item"), new("mail_loot_template", "Item", "item"),
        new("pickpocketing_loot_template", "Item", "item"), new("skinning_loot_template", "Item", "item"),
        new("disenchant_loot_template", "Item", "item"), new("fishing_loot_template", "Item", "item"),
        new("spell_loot_template", "Item", "item"), new("reference_loot_template", "Item", "item"),
        new("prospecting_loot_template", "Item", "item"), new("milling_loot_template", "Item", "item"),
        new("playercreateinfo_item", "itemid", "item"),
        new("quest_template", "RewardItem1", "RewardItem2", "RewardItem3", "RewardItem4", "RewardChoiceItemID1", "RewardChoiceItemID2", "RewardChoiceItemID3", "RewardChoiceItemID4", "RewardChoiceItemID5", "RewardChoiceItemID6")
    ];

    public async Task<ItemAcquisitionAudit> AuditAsync(DatabaseConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile));
        await connection.OpenAsync(cancellationToken);
        var schema = await ReadSchemaAsync(connection, profile.Database, cancellationToken);
        var itemTable = ResolveTable(schema, "item_template") ?? throw new NotSupportedException("The selected database has no item_template table.");
        var acquired = new Dictionary<uint, HashSet<string>>(); var checkedSources = new List<string>(); var missingSources = new List<string>();
        foreach (var spec in AcquisitionSpecs)
        {
            var table = ResolveTable(schema, spec.Table);
            if (table is null) { missingSources.Add(spec.Table); continue; }
            var columns = spec.Columns.Select(candidate => ResolveColumn(schema[table], candidate)).Where(column => column is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (columns.Length == 0) { missingSources.Add(spec.Table); continue; }
            checkedSources.Add(table);
            foreach (var column in columns)
            {
                await using var command = new MySqlCommand($"SELECT DISTINCT {Quote(column!)} FROM {Quote(table)} WHERE {Quote(column!)} > 0", connection) { CommandTimeout = 120 };
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var entry = Convert.ToUInt32(reader.GetValue(0));
                    if (!acquired.TryGetValue(entry, out var sources)) acquired[entry] = sources = new(StringComparer.OrdinalIgnoreCase);
                    sources.Add(table);
                }
            }
        }

        var itemColumns = schema[itemTable];
        var entryColumn = RequireColumn(itemColumns, "entry"); var nameColumn = RequireColumn(itemColumns, "name");
        var qualityColumn = ResolveColumn(itemColumns, "Quality", "quality"); var levelColumn = ResolveColumn(itemColumns, "ItemLevel", "itemlevel"); var setColumn = ResolveColumn(itemColumns, "itemset", "ItemSet");
        var sql = $"SELECT {Quote(entryColumn)}, {Quote(nameColumn)}, {(qualityColumn is null ? "0" : Quote(qualityColumn))}, {(levelColumn is null ? "0" : Quote(levelColumn))}, {(setColumn is null ? "0" : Quote(setColumn))} FROM {Quote(itemTable)} ORDER BY {Quote(entryColumn)}";
        var items = new List<ItemCatalogEntry>();
        await using (var command = new MySqlCommand(sql, connection) { CommandTimeout = 120 })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                var entry = Convert.ToUInt32(reader.GetValue(0)); var sources = acquired.TryGetValue(entry, out var found) ? found.Order(StringComparer.OrdinalIgnoreCase).ToArray() : [];
                items.Add(new(entry, Convert.ToString(reader.GetValue(1)) ?? string.Empty, Convert.ToInt32(reader.GetValue(2)), Convert.ToInt32(reader.GetValue(3)), Convert.ToUInt32(reader.GetValue(4)), sources));
            }
        var unavailable = items.Where(item => !item.HasKnownAcquisitionPath).ToArray();
        return new(profile.Database, DateTimeOffset.UtcNow, checkedSources.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(), missingSources.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(), items.Count, items.Count - unavailable.Length, unavailable);
    }

    public async Task<IReadOnlyDictionary<uint, string>> GetItemNamesAsync(DatabaseConnectionProfile profile, IEnumerable<uint> itemIds, CancellationToken cancellationToken = default)
    {
        var ids = itemIds.Where(id => id != 0).Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<uint, string>();
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile));
        await connection.OpenAsync(cancellationToken);
        var schema = await ReadSchemaAsync(connection, profile.Database, cancellationToken);
        var table = ResolveTable(schema, "item_template") ?? throw new NotSupportedException("The selected database has no item_template table.");
        var entry = RequireColumn(schema[table], "entry"); var name = RequireColumn(schema[table], "name");
        var parameters = ids.Select((_, index) => $"@id{index}").ToArray();
        await using var command = new MySqlCommand($"SELECT {Quote(entry)},{Quote(name)} FROM {Quote(table)} WHERE {Quote(entry)} IN ({string.Join(',', parameters)})", connection);
        for (var index = 0; index < ids.Length; index++) command.Parameters.AddWithValue(parameters[index], ids[index]);
        var result = new Dictionary<uint, string>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result[Convert.ToUInt32(reader.GetValue(0))] = Convert.ToString(reader.GetValue(1)) ?? string.Empty;
        return result;
    }

    public async Task<ItemCloneResult> CloneAsync(DatabaseConnectionProfile profile, uint sourceEntry, uint newEntry, string nameSuffix = " Variant", uint? itemSetId = null, CancellationToken cancellationToken = default)
    {
        if (sourceEntry == 0 || newEntry == 0 || sourceEntry == newEntry) throw new ArgumentException("Source and destination item IDs must be distinct positive values.");
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        var columns = await ReadTableColumnsAsync(connection, profile.Database, "item_template", cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var entry = RequireColumn(columns, "entry"); var name = RequireColumn(columns, "name"); var set = ResolveColumn(columns, "itemset", "ItemSet");
        var writable = columns.Where(column => !column.Extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase)).ToArray();
        string sourceName;
        await using (var check = new MySqlCommand($"SELECT {Quote(name)} FROM `item_template` WHERE {Quote(entry)}=@source LIMIT 1", connection, transaction))
        {
            check.Parameters.AddWithValue("@source", sourceEntry); sourceName = Convert.ToString(await check.ExecuteScalarAsync(cancellationToken)) ?? throw new InvalidOperationException($"Source item {sourceEntry} does not exist.");
        }
        await using (var exists = new MySqlCommand($"SELECT 1 FROM `item_template` WHERE {Quote(entry)}=@destination LIMIT 1", connection, transaction))
        {
            exists.Parameters.AddWithValue("@destination", newEntry); if (await exists.ExecuteScalarAsync(cancellationToken) is not null) throw new InvalidOperationException($"Destination item {newEntry} already exists; nothing was replaced.");
        }
        var expressions = writable.Select(column => column.Name.Equals(entry, StringComparison.OrdinalIgnoreCase) ? "@destination" : column.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && nameSuffix.Length > 0 ? $"CONCAT({Quote(column.Name)}, @suffix)" : set is not null && itemSetId is not null && column.Name.Equals(set, StringComparison.OrdinalIgnoreCase) ? "@itemset" : Quote(column.Name)).ToArray();
        var insertSql = $"INSERT INTO `item_template` ({string.Join(",", writable.Select(column => Quote(column.Name)))}) SELECT {string.Join(",", expressions)} FROM `item_template` WHERE {Quote(entry)}=@source";
        await using (var insert = new MySqlCommand(insertSql, connection, transaction) { CommandTimeout = 30 })
        {
            insert.Parameters.AddWithValue("@source", sourceEntry); insert.Parameters.AddWithValue("@destination", newEntry); insert.Parameters.AddWithValue("@suffix", nameSuffix); insert.Parameters.AddWithValue("@itemset", itemSetId ?? 0);
            if (await insert.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("Item clone did not insert exactly one row.");
        }
        var localeRows = await CloneLocaleRowsAsync(connection, transaction, profile.Database, sourceEntry, newEntry, nameSuffix, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var effectiveSet = itemSetId ?? await ReadItemSetAsync(connection, newEntry, set, cancellationToken);
        return new(sourceEntry, newEntry, sourceName, sourceName + nameSuffix, effectiveSet, writable.Length, localeRows);
    }

    private static async Task<int> CloneLocaleRowsAsync(MySqlConnection connection, MySqlTransaction transaction, string database, uint source, uint destination, string suffix, CancellationToken cancellationToken)
    {
        var columns = await ReadTableColumnsAsync(connection, database, "item_template_locale", cancellationToken, required: false, transaction: transaction); if (columns.Count == 0) return 0;
        var key = ResolveColumn(columns, "ID", "entry"); if (key is null) return 0; var name = ResolveColumn(columns, "Name", "name");
        var writable = columns.Where(column => !column.Extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase)).ToArray();
        var expressions = writable.Select(column => column.Name.Equals(key, StringComparison.OrdinalIgnoreCase) ? "@destination" : name is not null && suffix.Length > 0 && column.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ? $"CONCAT({Quote(column.Name)}, @suffix)" : Quote(column.Name));
        var sql = $"INSERT INTO `item_template_locale` ({string.Join(",", writable.Select(column => Quote(column.Name)))}) SELECT {string.Join(",", expressions)} FROM `item_template_locale` WHERE {Quote(key)}=@source";
        await using var command = new MySqlCommand(sql, connection, transaction); command.Parameters.AddWithValue("@source", source); command.Parameters.AddWithValue("@destination", destination); command.Parameters.AddWithValue("@suffix", suffix);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<uint> ReadItemSetAsync(MySqlConnection connection, uint entry, string? setColumn, CancellationToken cancellationToken)
    {
        if (setColumn is null) return 0; await using var command = new MySqlCommand($"SELECT {Quote(setColumn)} FROM `item_template` WHERE `entry`=@entry", connection); command.Parameters.AddWithValue("@entry", entry); return Convert.ToUInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<Dictionary<string, List<string>>> ReadSchemaAsync(MySqlConnection connection, string database, CancellationToken cancellationToken)
    {
        const string sql = "SELECT TABLE_NAME,COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@database ORDER BY TABLE_NAME,ORDINAL_POSITION";
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase); await using var command = new MySqlCommand(sql, connection); command.Parameters.AddWithValue("@database", database);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); while (await reader.ReadAsync(cancellationToken)) { var table = reader.GetString(0); if (!result.TryGetValue(table, out var columns)) result[table] = columns = []; columns.Add(reader.GetString(1)); } return result;
    }
    private static async Task<IReadOnlyList<DatabaseColumnCapability>> ReadTableColumnsAsync(MySqlConnection connection, string database, string table, CancellationToken cancellationToken, bool required = true, MySqlTransaction? transaction = null)
    {
        const string sql = "SELECT COLUMN_NAME,DATA_TYPE,COLUMN_TYPE,IS_NULLABLE,COLUMN_DEFAULT,COLUMN_KEY,EXTRA,ORDINAL_POSITION FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@database AND TABLE_NAME=@table ORDER BY ORDINAL_POSITION";
        var result = new List<DatabaseColumnCapability>(); await using var command = new MySqlCommand(sql, connection, transaction); command.Parameters.AddWithValue("@database", database); command.Parameters.AddWithValue("@table", table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3)=="YES",reader.IsDBNull(4)?null:Convert.ToString(reader.GetValue(4)),reader.GetString(5),reader.GetString(6),reader.GetInt32(7)));
        if (required && result.Count == 0) throw new NotSupportedException($"The selected database has no {table} table."); return result;
    }
    private static string? ResolveTable(Dictionary<string, List<string>> schema, string requested) => schema.Keys.FirstOrDefault(table => table.Equals(requested, StringComparison.OrdinalIgnoreCase));
    private static string? ResolveColumn(IEnumerable<string> columns, params string[] requested) => requested.Select(name => columns.FirstOrDefault(column => column.Equals(name, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(column => column is not null);
    private static string? ResolveColumn(IEnumerable<DatabaseColumnCapability> columns, params string[] requested) => requested.Select(name => columns.FirstOrDefault(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Name).FirstOrDefault(column => column is not null);
    private static string RequireColumn(IEnumerable<string> columns, string requested) => ResolveColumn(columns, requested) ?? throw new NotSupportedException($"Required column '{requested}' is missing.");
    private static string RequireColumn(IEnumerable<DatabaseColumnCapability> columns, string requested) => ResolveColumn(columns, requested) ?? throw new NotSupportedException($"Required column '{requested}' is missing.");
    private static string Quote(string identifier) => $"`{identifier.Replace("`", "``")}`";
}
