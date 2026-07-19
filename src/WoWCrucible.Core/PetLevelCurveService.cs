using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MySqlConnector;

namespace WoWCrucible.Core;

public enum PetLevelCurveWriteMode
{
    InsertMissing,
    UpdateExactRange
}

public sealed record PetLevelCurveScale(
    decimal Health = 1m,
    decimal Mana = 1m,
    decimal Armor = 1m,
    decimal Attributes = 1m,
    decimal Damage = 1m);

public sealed record PetLevelCurveRequest(
    uint SourceCreatureEntry,
    uint TargetCreatureEntry,
    byte StartLevel,
    byte EndLevel,
    PetLevelCurveScale Scale);

public sealed record PetLevelCurvePreparedPlan(
    PetLevelCurveRequest Request,
    WorldContentWritePlan Content,
    string SchemaFingerprint,
    IReadOnlyDictionary<int, string?> ExpectedTargetRows,
    int SourceRows);

public sealed record PetLevelCurveApplyResult(int Inserted, int Updated, int Skipped);

public sealed class PetLevelCurveService
{
    private static readonly HashSet<string> AttributeColumns = new(StringComparer.OrdinalIgnoreCase) { "str", "agi", "sta", "inte", "spi" };
    private static readonly HashSet<string> DamageColumns = new(StringComparer.OrdinalIgnoreCase) { "min_dmg", "max_dmg" };

    public WorldContentWritePlan CreateScaledPlan(DatabaseTableCapability table, PetLevelCurveRequest request, IReadOnlyList<IReadOnlyDictionary<string, object?>> sourceRows)
    {
        ValidateTable(table); ValidateRequest(request);
        var byLevel = new Dictionary<int, IReadOnlyDictionary<string, object?>>();
        foreach (var row in sourceRows)
        {
            var creatureEntry = Convert.ToUInt32(Value(row, "creature_entry"), CultureInfo.InvariantCulture); if (creatureEntry != request.SourceCreatureEntry) throw new InvalidDataException($"Source row belongs to creature {creatureEntry}, not requested creature {request.SourceCreatureEntry}.");
            var level = ToInt(Value(row, "level"), "source level");
            if (!byLevel.TryAdd(level, row)) throw new InvalidDataException($"Source creature {request.SourceCreatureEntry} contains duplicate level {level} rows.");
        }

        var missing = Enumerable.Range(request.StartLevel, request.EndLevel - request.StartLevel + 1).Where(level => !byLevel.ContainsKey(level)).ToArray();
        if (missing.Length > 0) throw new InvalidDataException($"Source creature {request.SourceCreatureEntry} is missing requested level(s): {string.Join(", ", missing.Take(20))}{(missing.Length > 20 ? "…" : string.Empty)}. Crucible will not invent gaps in a reference curve.");

        var domain = BehaviorDomainCatalog.Find("pet-level-stats"); var rows = new List<WorldSqlRowPlan>(); var omitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var level in Enumerable.Range(request.StartLevel, request.EndLevel - request.StartLevel + 1))
        {
            var source = byLevel[level]; var supplied = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in table.Columns)
            {
                if (IsGenerated(column)) { omitted.Add($"{table.Name}.{column.Name} (generated)"); continue; }
                if (column.Name.Equals("creature_entry", StringComparison.OrdinalIgnoreCase)) { supplied[column.Name] = request.TargetCreatureEntry; continue; }
                if (column.Name.Equals("level", StringComparison.OrdinalIgnoreCase)) { supplied[column.Name] = level; continue; }
                if (!TryValue(source, column.Name, out var value)) throw new InvalidDataException($"Source level {level} does not contain live column {column.Name}.");
                supplied[column.Name] = ScaleValue(column, value, Factor(column.Name, request.Scale));
            }

            var validated = BehaviorAuthoringAdapter.CreatePlan(domain, table, supplied).Rows.Single();
            var values = validated.Values.Where(pair => table.Find(pair.Key) is { } column && !IsGenerated(column)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            rows.Add(new(table.Name, validated.Key, values));
        }
        return new($"Pet level curve {request.TargetCreatureEntry}", rows, omitted.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public async Task<PetLevelCurvePreparedPlan> PrepareAsync(DatabaseConnectionProfile profile, PetLevelCurveRequest request, CancellationToken cancellationToken = default)
    {
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken);
        var table = capabilities.FindTable("pet_levelstats") ?? throw new NotSupportedException("The connected world database has no pet_levelstats table.");
        ValidateTable(table); ValidateRequest(request);
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        var source = await ReadRowsAsync(connection, null, table, request.SourceCreatureEntry, request.StartLevel, request.EndLevel, false, cancellationToken);
        var content = CreateScaledPlan(table, request, source);
        var target = await ReadRowsAsync(connection, null, table, request.TargetCreatureEntry, request.StartLevel, request.EndLevel, false, cancellationToken);
        var targetByLevel = target.ToDictionary(row => ToInt(Value(row, "level"), "target level"));
        var expected = Enumerable.Range(request.StartLevel, request.EndLevel - request.StartLevel + 1).ToDictionary(level => level, level => targetByLevel.TryGetValue(level, out var row) ? FingerprintRow(table, row) : null);
        return new(request, content, FingerprintSchema(table), expected, source.Count);
    }

    public string PreviewSql(PetLevelCurvePreparedPlan plan, PetLevelCurveWriteMode mode)
    {
        var statements = plan.Content.Rows.Select(row => PreviewRow(row, mode));
        return $"START TRANSACTION;{Environment.NewLine}{Environment.NewLine}{string.Join($"{Environment.NewLine}{Environment.NewLine}", statements)}{Environment.NewLine}{Environment.NewLine}COMMIT;";
    }

    public async Task<PetLevelCurveApplyResult> ApplyAsync(DatabaseConnectionProfile profile, PetLevelCurvePreparedPlan prepared, PetLevelCurveWriteMode mode, CancellationToken cancellationToken = default)
    {
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken);
        var table = capabilities.FindTable("pet_levelstats") ?? throw new NotSupportedException("The connected world database has no pet_levelstats table."); ValidateTable(table);
        if (!FingerprintSchema(table).Equals(prepared.SchemaFingerprint, StringComparison.Ordinal)) throw new InvalidOperationException("pet_levelstats changed after this curve was previewed. Reload the curve against the current schema.");
        if (prepared.Content.Rows.Count == 0) throw new InvalidOperationException("The pet curve has no rows.");

        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var currentRows = await ReadRowsAsync(connection, transaction, table, prepared.Request.TargetCreatureEntry, prepared.Request.StartLevel, prepared.Request.EndLevel, true, cancellationToken);
        var current = currentRows.ToDictionary(row => ToInt(Value(row, "level"), "target level"));
        foreach (var (level, expected) in prepared.ExpectedTargetRows)
        {
            var actual = current.TryGetValue(level, out var row) ? FingerprintRow(table, row) : null;
            if (!string.Equals(expected, actual, StringComparison.Ordinal)) throw new InvalidOperationException($"Target pet level {level} changed after preview. Nothing was written; reload before applying.");
        }

        var inserted = 0; var updated = 0; var skipped = 0;
        foreach (var row in prepared.Content.Rows)
        {
            var level = ToInt(Value(row.Key, "level"), "planned level");
            if (current.ContainsKey(level))
            {
                if (mode == PetLevelCurveWriteMode.InsertMissing) { skipped++; continue; }
                var writable = row.Values.Where(pair => !row.Key.ContainsKey(pair.Key)).ToArray(); if (writable.Length == 0) throw new InvalidOperationException("The generated pet curve exposes no writable stat fields.");
                var assignments = writable.Select((pair, index) => $"{ItemWritePlan.QuoteIdentifier(pair.Key)}=@v{index}").ToArray(); var predicates = row.Key.Keys.Select((key, index) => $"{ItemWritePlan.QuoteIdentifier(key)} <=> @k{index}").ToArray();
                await using var command = new MySqlCommand($"UPDATE {ItemWritePlan.QuoteIdentifier(row.Table)} SET {string.Join(",", assignments)} WHERE {string.Join(" AND ", predicates)} LIMIT 1", connection, transaction);
                for (var index = 0; index < writable.Length; index++) command.Parameters.AddWithValue($"@v{index}", writable[index].Value ?? DBNull.Value);
                var keyIndex = 0; foreach (var value in row.Key.Values) command.Parameters.AddWithValue($"@k{keyIndex++}", value ?? DBNull.Value);
                var affected = await command.ExecuteNonQueryAsync(cancellationToken); if (affected is < 0 or > 1) throw new InvalidOperationException($"Expected at most one pet level {level} update, but MySQL reported {affected}."); updated++;
            }
            else
            {
                var parameters = row.Values.Select((_, index) => $"@v{index}").ToArray();
                await using var command = new MySqlCommand($"INSERT INTO {ItemWritePlan.QuoteIdentifier(row.Table)} ({string.Join(",", row.Values.Keys.Select(ItemWritePlan.QuoteIdentifier))}) VALUES ({string.Join(",", parameters)})", connection, transaction);
                var index = 0; foreach (var value in row.Values.Values) command.Parameters.AddWithValue(parameters[index++], value ?? DBNull.Value);
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException($"Pet level {level} insert did not affect exactly one row."); inserted++;
            }
        }
        await transaction.CommitAsync(cancellationToken); return new(inserted, updated, skipped);
    }

    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadRowsAsync(MySqlConnection connection, MySqlTransaction? transaction, DatabaseTableCapability table, uint creatureEntry, byte start, byte end, bool lockRows, CancellationToken cancellationToken)
    {
        var columns = string.Join(",", table.Columns.Select(column => ItemWritePlan.QuoteIdentifier(column.Name)));
        var sql = $"SELECT {columns} FROM {ItemWritePlan.QuoteIdentifier(table.Name)} WHERE {ItemWritePlan.QuoteIdentifier(table.Find("creature_entry")!.Name)}=@entry AND {ItemWritePlan.QuoteIdentifier(table.Find("level")!.Name)} BETWEEN @start AND @end ORDER BY {ItemWritePlan.QuoteIdentifier(table.Find("level")!.Name)}{(lockRows ? " FOR UPDATE" : string.Empty)}";
        await using var command = new MySqlCommand(sql, connection, transaction); command.Parameters.AddWithValue("@entry", creatureEntry); command.Parameters.AddWithValue("@start", start); command.Parameters.AddWithValue("@end", end);
        var rows = new List<IReadOnlyDictionary<string, object?>>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase); for (var index = 0; index < table.Columns.Count; index++) row[table.Columns[index].Name] = reader.IsDBNull(index) ? null : reader.GetValue(index); rows.Add(row);
        }
        return rows;
    }

    private static void ValidateTable(DatabaseTableCapability table)
    {
        var keys = table.Columns.Where(column => column.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (table.Find("creature_entry") is null || table.Find("level") is null || keys.Length != 2 || !keys.Any(column => column.Name.Equals("creature_entry", StringComparison.OrdinalIgnoreCase)) || !keys.Any(column => column.Name.Equals("level", StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException("Bulk pet curves require pet_levelstats to have the exact composite identity creature_entry + level.");
    }

    private static void ValidateRequest(PetLevelCurveRequest request)
    {
        if (request.SourceCreatureEntry == 0 || request.TargetCreatureEntry == 0) throw new InvalidDataException("Source and target creature entries must be positive.");
        if (request.StartLevel == 0 || request.EndLevel < request.StartLevel) throw new InvalidDataException("Pet curve levels must begin at 1 or higher and end at or above the starting level.");
        foreach (var (name, factor) in new[] { ("health", request.Scale.Health), ("mana", request.Scale.Mana), ("armor", request.Scale.Armor), ("attributes", request.Scale.Attributes), ("damage", request.Scale.Damage) })
            if (factor < 0m || factor > 1000m) throw new InvalidDataException($"Pet curve {name} scale must be between 0 and 1000.");
    }

    private static decimal Factor(string column, PetLevelCurveScale scale) => column.ToLowerInvariant() switch
    {
        "hp" => scale.Health,
        "mana" => scale.Mana,
        "armor" => scale.Armor,
        _ when AttributeColumns.Contains(column) => scale.Attributes,
        _ when DamageColumns.Contains(column) => scale.Damage,
        _ => 1m
    };

    private static object? ScaleValue(DatabaseColumnCapability column, object? value, decimal factor)
    {
        if (value is null || factor == 1m || !IsNumeric(column)) return value;
        var number = Convert.ToDecimal(value, CultureInfo.InvariantCulture) * factor;
        if (IsInteger(column)) number = decimal.Round(number, 0, MidpointRounding.AwayFromZero);
        ValidateRange(column, number);
        return number;
    }

    private static void ValidateRange(DatabaseColumnCapability column, decimal value)
    {
        if (column.ColumnType.Contains("unsigned", StringComparison.OrdinalIgnoreCase) && value < 0) throw new InvalidDataException($"Scaling {column.Name} produced a negative unsigned value.");
        var unsigned = column.ColumnType.Contains("unsigned", StringComparison.OrdinalIgnoreCase); var bounds = column.DataType.ToLowerInvariant() switch
        {
            "tinyint" => unsigned ? (0m, 255m) : (-128m, 127m),
            "smallint" => unsigned ? (0m, 65535m) : (-32768m, 32767m),
            "mediumint" => unsigned ? (0m, 16777215m) : (-8388608m, 8388607m),
            "int" or "integer" => unsigned ? (0m, 4294967295m) : (-2147483648m, 2147483647m),
            "bigint" => unsigned ? (0m, 18446744073709551615m) : (-9223372036854775808m, 9223372036854775807m),
            _ => (decimal.MinValue, decimal.MaxValue)
        };
        if (value < bounds.Item1 || value > bounds.Item2) throw new InvalidDataException($"Scaling {column.Name} produced {value.ToString(CultureInfo.InvariantCulture)}, outside {column.ColumnType}.");
    }

    private static string PreviewRow(WorldSqlRowPlan row, PetLevelCurveWriteMode mode)
    {
        var columns = string.Join(", ", row.Values.Keys.Select(ItemWritePlan.QuoteIdentifier)); var values = string.Join(", ", row.Values.Values.Select(SqlLiteral));
        if (mode == PetLevelCurveWriteMode.InsertMissing)
        {
            var predicates = string.Join(" AND ", row.Key.Select(pair => $"{ItemWritePlan.QuoteIdentifier(pair.Key)} <=> {SqlLiteral(pair.Value)}"));
            return $"INSERT INTO {ItemWritePlan.QuoteIdentifier(row.Table)} ({columns}){Environment.NewLine}SELECT {values}{Environment.NewLine}WHERE NOT EXISTS (SELECT 1 FROM {ItemWritePlan.QuoteIdentifier(row.Table)} WHERE {predicates});";
        }
        var keyPredicates = string.Join(" AND ", row.Key.Select(pair => $"{ItemWritePlan.QuoteIdentifier(pair.Key)} <=> {SqlLiteral(pair.Value)}"));
        var updates = string.Join(", ", row.Values.Where(pair => !row.Key.ContainsKey(pair.Key)).Select(pair => $"{ItemWritePlan.QuoteIdentifier(pair.Key)}={SqlLiteral(pair.Value)}"));
        return $"UPDATE {ItemWritePlan.QuoteIdentifier(row.Table)} SET {updates} WHERE {keyPredicates};{Environment.NewLine}" +
               $"INSERT INTO {ItemWritePlan.QuoteIdentifier(row.Table)} ({columns}){Environment.NewLine}SELECT {values}{Environment.NewLine}WHERE NOT EXISTS (SELECT 1 FROM {ItemWritePlan.QuoteIdentifier(row.Table)} WHERE {keyPredicates});";
    }

    private static string FingerprintSchema(DatabaseTableCapability table)
    {
        var text = string.Join("\n", table.Columns.Select(column => $"{column.Ordinal}|{column.Name}|{column.ColumnType}|{column.Nullable}|{column.Key}|{column.Extra}")); return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static string FingerprintRow(DatabaseTableCapability table, IReadOnlyDictionary<string, object?> row)
    {
        var builder = new StringBuilder(); foreach (var column in table.Columns) { var text = CellText(Value(row, column.Name)); builder.Append(column.Name.Length).Append(':').Append(column.Name).Append('=').Append(text.Length).Append(':').Append(text).Append('\n'); } return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string SqlLiteral(object? value) => value switch
    {
        null => "NULL", string text => $"'{text.Replace("\\", "\\\\").Replace("'", "''")}'", bool state => state ? "1" : "0", byte[] bytes => $"0x{Convert.ToHexString(bytes)}", float number => number.ToString("R", CultureInfo.InvariantCulture), double number => number.ToString("R", CultureInfo.InvariantCulture), IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture), _ => $"'{Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace("'", "''")}'"
    };

    private static bool IsGenerated(DatabaseColumnCapability column) => column.Extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase);
    private static bool IsNumeric(DatabaseColumnCapability column) => column.DataType.ToLowerInvariant() is "tinyint" or "smallint" or "mediumint" or "int" or "integer" or "bigint" or "decimal" or "numeric" or "float" or "double" or "real";
    private static bool IsInteger(DatabaseColumnCapability column) => column.DataType.ToLowerInvariant() is "tinyint" or "smallint" or "mediumint" or "int" or "integer" or "bigint";
    private static int ToInt(object? value, string label) { try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); } catch (Exception exception) { throw new InvalidDataException($"Invalid {label} value '{CellText(value)}'.", exception); } }
    private static object? Value(IReadOnlyDictionary<string, object?> row, string name) => TryValue(row, name, out var value) ? value : null;
    private static bool TryValue(IReadOnlyDictionary<string, object?> row, string name, out object? value) { foreach (var pair in row) if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = pair.Value; return true; } value = null; return false; }
    private static string CellText(object? value) => value switch { null => "<NULL>", byte[] bytes => "0x" + Convert.ToHexString(bytes), DateTime date => date.ToString("O", CultureInfo.InvariantCulture), IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture), _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty };
}
