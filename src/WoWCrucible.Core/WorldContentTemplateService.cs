using System.Globalization;
using MySqlConnector;

namespace WoWCrucible.Core;

public sealed record CreatureTemplateDraft(
    uint Entry,
    string Name,
    string Subname,
    IReadOnlyList<uint> DisplayIds,
    byte MinimumLevel,
    byte MaximumLevel,
    ushort Faction,
    uint NpcFlags,
    byte Rank,
    byte CreatureType,
    sbyte Family,
    byte UnitClass,
    float Scale,
    float WalkSpeed,
    float RunSpeed,
    float HealthModifier,
    float ManaModifier,
    float ArmorModifier,
    float DamageModifier,
    uint BaseAttackTime,
    uint RangeAttackTime,
    uint UnitFlags,
    uint UnitFlags2,
    uint DynamicFlags,
    uint TypeFlags,
    uint LootId,
    uint PickpocketLootId,
    uint SkinLootId,
    uint MinimumGold,
    uint MaximumGold,
    string AiName,
    string ScriptName,
    bool RegeneratesHealth = true,
    IReadOnlyList<VendorItemDraft>? VendorItems = null,
    IReadOnlyList<CreatureLootDraft>? LootItems = null);

public sealed record VendorItemDraft(int Slot, int Item, uint MaximumCount, uint RestockSeconds, uint ExtendedCost);
public sealed record CreatureLootDraft(uint Entry, int Item, int Reference, float Chance, bool QuestRequired, ushort LootMode, byte GroupId, byte MinimumCount, byte MaximumCount, string Comment);

public sealed record WorldSqlRowPlan(string Table, IReadOnlyDictionary<string, object?> Key, IReadOnlyDictionary<string, object?> Values);

public sealed record WorldContentWritePlan(string Domain, IReadOnlyList<WorldSqlRowPlan> Rows, IReadOnlyList<string> OmittedFields)
{
    public string PreviewSql() => string.Join($"{Environment.NewLine}{Environment.NewLine}", Rows.Select(row =>
        $"INSERT INTO {ItemWritePlan.QuoteIdentifier(row.Table)} ({string.Join(", ", row.Values.Keys.Select(ItemWritePlan.QuoteIdentifier))}){Environment.NewLine}" +
        $"VALUES ({string.Join(", ", row.Values.Values.Select(SqlLiteral))});"));

    private static string SqlLiteral(object? value) => value switch
    {
        null => "NULL",
        string text => $"'{text.Replace("\\", "\\\\").Replace("'", "''")}'",
        bool state => state ? "1" : "0",
        float number => number.ToString("R", CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => $"'{Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace("'", "''")}'"
    };
}

public static class CreatureTemplateAdapter
{
    public static DatabaseCapabilities CreatePortableCapabilities()
    {
        static DatabaseTableCapability Table(string name, params string[] columns) => new(name, columns.Select((column, index) => new DatabaseColumnCapability(column, column is "name" or "subname" or "AIName" or "ScriptName" ? "varchar" : "int", "portable", true, "0", index == 0 ? "PRI" : string.Empty, string.Empty, index + 1)).ToArray());
        var creature = Table("creature_template", "entry", "name", "subname", "minlevel", "maxlevel", "faction", "npcflag", "rank", "type", "family", "unit_class", "speed_walk", "speed_run", "HealthModifier", "ManaModifier", "ArmorModifier", "DamageModifier", "BaseAttackTime", "RangeAttackTime", "unit_flags", "unit_flags2", "dynamicflags", "type_flags", "lootid", "pickpocketloot", "skinloot", "mingold", "maxgold", "AIName", "ScriptName", "RegenHealth", "VerifiedBuild");
        var model = Table("creature_template_model", "CreatureID", "Idx", "CreatureDisplayID", "DisplayScale", "Probability", "VerifiedBuild");
        var vendor = Table("npc_vendor", "entry", "slot", "item", "maxcount", "incrtime", "ExtendedCost", "VerifiedBuild");
        var loot = Table("creature_loot_template", "Entry", "Item", "Reference", "Chance", "QuestRequired", "LootMode", "GroupId", "MinCount", "MaxCount", "Comment");
        return new("portable-current-core", "world", new Dictionary<string, DatabaseTableCapability>(StringComparer.OrdinalIgnoreCase) { [creature.Name] = creature, [model.Name] = model, [vendor.Name] = vendor, [loot.Name] = loot });
    }

    public static WorldContentWritePlan CreatePlan(CreatureTemplateDraft draft, DatabaseCapabilities capabilities)
    {
        if (draft.Entry == 0 || string.IsNullOrWhiteSpace(draft.Name)) throw new InvalidDataException("Creature entry and name are required.");
        if (draft.MinimumLevel == 0 || draft.MaximumLevel < draft.MinimumLevel) throw new InvalidDataException("Creature levels must be nonzero and maximum level cannot be below minimum level.");
        if (draft.Faction == 0) throw new InvalidDataException("Choose a faction template. Faction 0 commonly makes the NPC unusable or hostile in unintended ways.");
        if (!float.IsFinite(draft.Scale) || draft.Scale <= 0 || !float.IsFinite(draft.WalkSpeed) || draft.WalkSpeed <= 0 || !float.IsFinite(draft.RunSpeed) || draft.RunSpeed <= 0)
            throw new InvalidDataException("Scale and movement speeds must be finite positive numbers.");
        var table = capabilities.FindTable("creature_template") ?? throw new NotSupportedException("The connected world database has no creature_template table.");
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var omitted = new List<string>();
        Add(table, values, omitted, "entry", draft.Entry, required: true);
        Add(table, values, omitted, "name", draft.Name.Trim(), required: true);
        Add(table, values, omitted, "subname", draft.Subname.Trim());
        Add(table, values, omitted, "minlevel", draft.MinimumLevel, required: true);
        Add(table, values, omitted, "maxlevel", draft.MaximumLevel, required: true);
        Add(table, values, omitted, "faction", draft.Faction, required: true);
        Add(table, values, omitted, "npcflag", draft.NpcFlags);
        Add(table, values, omitted, "rank", draft.Rank);
        Add(table, values, omitted, "type", draft.CreatureType);
        Add(table, values, omitted, "family", draft.Family);
        Add(table, values, omitted, "unit_class", draft.UnitClass);
        Add(table, values, omitted, "scale", draft.Scale);
        Add(table, values, omitted, "speed_walk", draft.WalkSpeed);
        Add(table, values, omitted, "speed_run", draft.RunSpeed);
        Add(table, values, omitted, "HealthModifier", draft.HealthModifier);
        Add(table, values, omitted, "ManaModifier", draft.ManaModifier);
        Add(table, values, omitted, "ArmorModifier", draft.ArmorModifier);
        Add(table, values, omitted, "DamageModifier", draft.DamageModifier);
        Add(table, values, omitted, "BaseAttackTime", draft.BaseAttackTime);
        Add(table, values, omitted, "RangeAttackTime", draft.RangeAttackTime);
        Add(table, values, omitted, "unit_flags", draft.UnitFlags);
        Add(table, values, omitted, "unit_flags2", draft.UnitFlags2);
        Add(table, values, omitted, "dynamicflags", draft.DynamicFlags);
        Add(table, values, omitted, "type_flags", draft.TypeFlags);
        Add(table, values, omitted, "lootid", draft.LootId);
        Add(table, values, omitted, "pickpocketloot", draft.PickpocketLootId);
        Add(table, values, omitted, "skinloot", draft.SkinLootId);
        Add(table, values, omitted, "mingold", draft.MinimumGold);
        Add(table, values, omitted, "maxgold", draft.MaximumGold);
        Add(table, values, omitted, "AIName", draft.AiName.Trim());
        Add(table, values, omitted, "ScriptName", draft.ScriptName.Trim());
        Add(table, values, omitted, "RegenHealth", draft.RegeneratesHealth ? 1 : 0);
        Add(table, values, omitted, "VerifiedBuild", 0);

        var displayIds = (draft.DisplayIds ?? []).Where(id => id != 0).Distinct().Take(4).ToArray();
        var rows = new List<WorldSqlRowPlan> { new(table.Name, Key(table, ("entry", (object?)draft.Entry)), values) };
        var modelTable = capabilities.FindTable("creature_template_model");
        if (modelTable is not null)
        {
            for (var index = 0; index < displayIds.Length; index++)
            {
                var modelValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                Add(modelTable, modelValues, omitted, "CreatureID", draft.Entry, required: true);
                Add(modelTable, modelValues, omitted, "Idx", index, required: true);
                Add(modelTable, modelValues, omitted, "CreatureDisplayID", displayIds[index], required: true);
                Add(modelTable, modelValues, omitted, "DisplayScale", draft.Scale);
                Add(modelTable, modelValues, omitted, "Probability", 1f / displayIds.Length);
                Add(modelTable, modelValues, omitted, "VerifiedBuild", 0);
                rows.Add(new(modelTable.Name, Key(modelTable, ("CreatureID", (object?)draft.Entry), ("Idx", index)), modelValues));
            }
        }
        else
        {
            for (var index = 0; index < 4; index++) Add(table, values, omitted, $"modelid{index + 1}", index < displayIds.Length ? displayIds[index] : 0);
        }
        if (displayIds.Length == 0) omitted.Add("CreatureDisplayID (no model selected)");
        AddVendorRows(draft, capabilities, rows, omitted);
        AddLootRows(draft, capabilities, rows, omitted);
        return new("Creature template", rows, omitted.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static void AddVendorRows(CreatureTemplateDraft draft, DatabaseCapabilities capabilities, ICollection<WorldSqlRowPlan> rows, ICollection<string> omitted)
    {
        var items = (draft.VendorItems ?? []).Where(item => item.Item != 0).ToArray();
        if (items.Length == 0) return;
        var table = capabilities.FindTable("npc_vendor") ?? throw new NotSupportedException("Vendor items were supplied, but the connected schema has no npc_vendor table.");
        foreach (var item in items)
        {
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            Add(table, values, omitted, "entry", draft.Entry, required: true); Add(table, values, omitted, "slot", item.Slot);
            Add(table, values, omitted, "item", item.Item, required: true); Add(table, values, omitted, "maxcount", item.MaximumCount);
            Add(table, values, omitted, "incrtime", item.RestockSeconds); Add(table, values, omitted, "ExtendedCost", item.ExtendedCost); Add(table, values, omitted, "VerifiedBuild", 0);
            rows.Add(new(table.Name, AvailableKey(table, ("entry", (object?)draft.Entry), ("item", item.Item), ("ExtendedCost", item.ExtendedCost)), values));
        }
    }

    private static void AddLootRows(CreatureTemplateDraft draft, DatabaseCapabilities capabilities, ICollection<WorldSqlRowPlan> rows, ICollection<string> omitted)
    {
        var items = (draft.LootItems ?? []).Where(item => item.Item != 0 || item.Reference != 0).ToArray();
        if (items.Length == 0) return;
        var table = capabilities.FindTable("creature_loot_template") ?? throw new NotSupportedException("Loot items were supplied, but the connected schema has no creature_loot_template table.");
        foreach (var item in items)
        {
            if (item.Entry == 0) throw new InvalidDataException("Each creature loot row requires a nonzero loot entry (normally the creature's lootid).");
            if (!float.IsFinite(item.Chance) || item.Chance < 0 || item.Chance > 100) throw new InvalidDataException("Loot chance must be from 0 through 100.");
            if (item.MinimumCount == 0 || item.MaximumCount < item.MinimumCount) throw new InvalidDataException("Loot counts must be nonzero and maximum cannot be below minimum.");
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            Add(table, values, omitted, "Entry", item.Entry, required: true); Add(table, values, omitted, "Item", item.Item, required: true);
            Add(table, values, omitted, "Reference", item.Reference); Add(table, values, omitted, "Chance", item.Chance);
            Add(table, values, omitted, "QuestRequired", item.QuestRequired ? 1 : 0); Add(table, values, omitted, "LootMode", item.LootMode);
            Add(table, values, omitted, "GroupId", item.GroupId); Add(table, values, omitted, "MinCount", item.MinimumCount); Add(table, values, omitted, "MaxCount", item.MaximumCount);
            Add(table, values, omitted, "Comment", item.Comment.Trim());
            rows.Add(new(table.Name, AvailableKey(table, ("Entry", (object?)item.Entry), ("Item", item.Item), ("Reference", item.Reference), ("GroupId", item.GroupId)), values));
        }
    }

    private static IReadOnlyDictionary<string, object?> Key(DatabaseTableCapability table, params (string Name, object? Value)[] keys)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in keys)
        {
            var column = table.Find(name) ?? throw new NotSupportedException($"{table.Name} has no key column '{name}'.");
            result[column.Name] = value;
        }
        return result;
    }

    private static IReadOnlyDictionary<string, object?> AvailableKey(DatabaseTableCapability table, params (string Name, object? Value)[] candidates)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in candidates)
            if (table.Find(name) is { } column) result[column.Name] = value;
        if (result.Count < 2) throw new NotSupportedException($"{table.Name} does not expose enough identity columns for a safe additive insert.");
        return result;
    }

    private static void Add(DatabaseTableCapability table, IDictionary<string, object?> values, ICollection<string> omitted, string name, object? value, bool required = false)
    {
        var column = table.Find(name);
        if (column is null)
        {
            if (required) throw new NotSupportedException($"{table.Name} has no required '{name}' column.");
            omitted.Add($"{table.Name}.{name}"); return;
        }
        values[column.Name] = value;
    }
}

public sealed class WorldContentTemplateService
{
    public async Task InsertAsync(DatabaseConnectionProfile profile, WorldContentWritePlan plan, CancellationToken cancellationToken = default)
    {
        if (plan.Rows.Count == 0) throw new InvalidOperationException("The world-content plan has no rows.");
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var row in plan.Rows)
        {
            if (row.Key.Count == 0) throw new InvalidOperationException($"{row.Table} has no verified identity key; refusing an unsafe insert.");
            var predicates = row.Key.Keys.Select((key, index) => $"{ItemWritePlan.QuoteIdentifier(key)}=@k{index}").ToArray();
            await using var exists = new MySqlCommand($"SELECT 1 FROM {ItemWritePlan.QuoteIdentifier(row.Table)} WHERE {string.Join(" AND ", predicates)} LIMIT 1", connection, transaction);
            var keyIndex = 0; foreach (var value in row.Key.Values) exists.Parameters.AddWithValue($"@k{keyIndex++}", value ?? DBNull.Value);
            if (await exists.ExecuteScalarAsync(cancellationToken) is not null)
                throw new InvalidOperationException($"{row.Table} already contains {string.Join(", ", row.Key.Select(pair => $"{pair.Key}={pair.Value}"))}. Crucible will not silently replace it.");
        }
        foreach (var row in plan.Rows)
        {
            var parameters = row.Values.Select((_, index) => $"@p{index}").ToArray();
            var sql = $"INSERT INTO {ItemWritePlan.QuoteIdentifier(row.Table)} ({string.Join(",", row.Values.Keys.Select(ItemWritePlan.QuoteIdentifier))}) VALUES ({string.Join(",", parameters)})";
            await using var command = new MySqlCommand(sql, connection, transaction);
            var index = 0; foreach (var value in row.Values.Values) command.Parameters.AddWithValue(parameters[index++], value ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpdateFirstAndInsertChildrenAsync(DatabaseConnectionProfile profile, WorldContentWritePlan plan, CancellationToken cancellationToken = default)
    {
        if (plan.Rows.Count == 0) throw new InvalidOperationException("The world-content plan has no primary row.");
        var primary = plan.Rows[0]; if (primary.Key.Count == 0) throw new InvalidOperationException($"{primary.Table} has no verified identity key; refusing an ambiguous update.");
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var primaryPredicates = primary.Key.Keys.Select((key, index) => $"{ItemWritePlan.QuoteIdentifier(key)} <=> @pk{index}").ToArray();
        await using (var count = new MySqlCommand($"SELECT COUNT(*) FROM {ItemWritePlan.QuoteIdentifier(primary.Table)} WHERE {string.Join(" AND ", primaryPredicates)}", connection, transaction))
        {
            var index = 0; foreach (var value in primary.Key.Values) count.Parameters.AddWithValue($"@pk{index++}", value ?? DBNull.Value);
            if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 1) throw new InvalidOperationException($"The complete key for {primary.Table} does not identify exactly one existing row.");
        }
        foreach (var row in plan.Rows.Skip(1))
        {
            if (row.Key.Count == 0) throw new InvalidOperationException($"{row.Table} has no verified identity key; refusing an unsafe child insert.");
            var predicates = row.Key.Keys.Select((key, index) => $"{ItemWritePlan.QuoteIdentifier(key)} <=> @ck{index}").ToArray();
            await using var exists = new MySqlCommand($"SELECT 1 FROM {ItemWritePlan.QuoteIdentifier(row.Table)} WHERE {string.Join(" AND ", predicates)} LIMIT 1", connection, transaction);
            var index = 0; foreach (var value in row.Key.Values) exists.Parameters.AddWithValue($"@ck{index++}", value ?? DBNull.Value);
            if (await exists.ExecuteScalarAsync(cancellationToken) is not null) throw new InvalidOperationException($"{row.Table} already contains {string.Join(", ", row.Key.Select(pair => $"{pair.Key}={pair.Value}"))}. Existing child rows are never replaced implicitly.");
        }
        var writable = primary.Values.Where(pair => !primary.Key.ContainsKey(pair.Key)).ToArray(); if (writable.Length == 0) throw new InvalidOperationException("The primary plan has no writable non-key fields.");
        var assignments = writable.Select((pair, index) => $"{ItemWritePlan.QuoteIdentifier(pair.Key)}=@pv{index}").ToArray();
        await using (var update = new MySqlCommand($"UPDATE {ItemWritePlan.QuoteIdentifier(primary.Table)} SET {string.Join(',', assignments)} WHERE {string.Join(" AND ", primaryPredicates)} LIMIT 1", connection, transaction))
        {
            for (var index = 0; index < writable.Length; index++) update.Parameters.AddWithValue($"@pv{index}", writable[index].Value ?? DBNull.Value);
            var keyIndex = 0; foreach (var value in primary.Key.Values) update.Parameters.AddWithValue($"@pk{keyIndex++}", value ?? DBNull.Value);
            var affected = await update.ExecuteNonQueryAsync(cancellationToken); if (affected is < 0 or > 1) throw new InvalidOperationException($"Expected zero or one changed primary row, but MySQL reported {affected}.");
        }
        foreach (var row in plan.Rows.Skip(1))
        {
            var parameters = row.Values.Select((_, index) => $"@p{index}").ToArray(); await using var insert = new MySqlCommand($"INSERT INTO {ItemWritePlan.QuoteIdentifier(row.Table)} ({string.Join(',', row.Values.Keys.Select(ItemWritePlan.QuoteIdentifier))}) VALUES ({string.Join(',', parameters)})", connection, transaction);
            var index = 0; foreach (var value in row.Values.Values) insert.Parameters.AddWithValue(parameters[index++], value ?? DBNull.Value); if (await insert.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException($"A {row.Table} child insert did not affect exactly one row.");
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
