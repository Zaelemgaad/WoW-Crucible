using System.Globalization;
using MySqlConnector;

namespace WoWCrucible.Core;

public sealed record ItemDraft(
    uint Entry, string Name, int Class, int Subclass, uint DisplayId, int Quality, int InventoryType,
    uint ItemLevel, uint RequiredLevel, uint BuyPrice, uint SellPrice, uint Bonding, uint Flags,
    float Armor, float DamageMin, float DamageMax, uint Delay, uint MaxDurability, string Description,
    int StatType1 = 0, int StatValue1 = 0, int StatType2 = 0, int StatValue2 = 0);

public sealed record ItemWritePlan(string Table, IReadOnlyDictionary<string, object> Values, IReadOnlyList<string> OmittedFields)
{
    public string PreviewSql()
    {
        var names = string.Join(", ", Values.Keys.Select(QuoteIdentifier));
        var values = string.Join(", ", Values.Values.Select(SqlLiteral));
        return $"INSERT INTO {QuoteIdentifier(Table)} ({names})\nVALUES ({values});";
    }

    internal static string QuoteIdentifier(string value) => $"`{value.Replace("`", "``")}`";
    private static string SqlLiteral(object value) => value switch
    {
        string text => $"'{text.Replace("\\", "\\\\").Replace("'", "''")}'",
        float number => number.ToString("R", CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => $"'{Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace("'", "''")}'"
    };
}

public static class ItemTemplateAdapter
{
    public static DatabaseTableCapability CreatePortableTable()
    {
        string[] names = ["entry", "class", "subclass", "name", "displayid", "Quality", "InventoryType", "ItemLevel", "RequiredLevel", "BuyPrice", "SellPrice", "bonding", "Flags", "armor", "dmg_min1", "dmg_max1", "dmg_type1", "delay", "MaxDurability", "description", "stat_type1", "stat_value1", "stat_type2", "stat_value2"];
        return new("item_template", names.Select((name, index) => new DatabaseColumnCapability(name, name is "name" or "description" ? "varchar" : "int", name is "name" or "description" ? "varchar(255)" : "int", false, "0", name == "entry" ? "PRI" : string.Empty, string.Empty, index + 1)).ToArray());
    }

    public static ItemWritePlan CreatePlan(ItemDraft draft, DatabaseTableCapability table)
    {
        if (!table.Name.Equals("item_template", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The selected table is not item_template.");
        if (draft.Entry == 0 || string.IsNullOrWhiteSpace(draft.Name)) throw new InvalidDataException("Entry ID and name are required.");
        var semanticValues = new (string Name, object Value)[]
        {
            ("entry", draft.Entry), ("class", draft.Class), ("subclass", draft.Subclass), ("name", draft.Name.Trim()),
            ("displayid", draft.DisplayId), ("Quality", draft.Quality), ("InventoryType", draft.InventoryType),
            ("ItemLevel", draft.ItemLevel), ("RequiredLevel", draft.RequiredLevel), ("BuyPrice", draft.BuyPrice),
            ("SellPrice", draft.SellPrice), ("bonding", draft.Bonding), ("Flags", draft.Flags), ("armor", draft.Armor),
            ("dmg_min1", draft.DamageMin), ("dmg_max1", draft.DamageMax), ("dmg_type1", 0), ("delay", draft.Delay),
            ("MaxDurability", draft.MaxDurability), ("description", draft.Description ?? string.Empty),
            ("stat_type1", draft.StatType1), ("stat_value1", draft.StatValue1), ("stat_type2", draft.StatType2), ("stat_value2", draft.StatValue2)
        };
        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var omitted = new List<string>();
        foreach (var (name, value) in semanticValues)
        {
            var column = table.Find(name);
            if (column is null) omitted.Add(name); else values[column.Name] = value;
        }
        var statCount = table.Find("StatsCount");
        if (statCount is not null) values[statCount.Name] = (draft.StatType1 != 0 ? 1 : 0) + (draft.StatType2 != 0 ? 1 : 0);
        foreach (var required in new[] { "entry", "class", "subclass", "name", "displayid", "Quality", "InventoryType" })
            if (table.Find(required) is null) throw new NotSupportedException($"This item_template has no '{required}' column, so the item adapter cannot safely target it.");
        return new(table.Name, values, omitted);
    }
}

public sealed class ItemTemplateService
{
    public async Task InsertAsync(DatabaseConnectionProfile profile, ItemWritePlan plan, CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var entry = plan.Values.First(pair => pair.Key.Equals("entry", StringComparison.OrdinalIgnoreCase)).Value;
        await using (var exists = new MySqlCommand($"SELECT 1 FROM {ItemWritePlan.QuoteIdentifier(plan.Table)} WHERE `entry`=@entry LIMIT 1", connection, transaction))
        {
            exists.Parameters.AddWithValue("@entry", entry);
            if (await exists.ExecuteScalarAsync(cancellationToken) is not null)
                throw new InvalidOperationException($"Item entry {entry} already exists. Crucible will not silently replace it.");
        }
        var parameterNames = plan.Values.Select((_, index) => $"@p{index}").ToArray();
        var sql = $"INSERT INTO {ItemWritePlan.QuoteIdentifier(plan.Table)} ({string.Join(",", plan.Values.Keys.Select(ItemWritePlan.QuoteIdentifier))}) VALUES ({string.Join(",", parameterNames)})";
        await using var command = new MySqlCommand(sql, connection, transaction);
        var valueIndex = 0;
        foreach (var value in plan.Values.Values) command.Parameters.AddWithValue(parameterNames[valueIndex++], value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
