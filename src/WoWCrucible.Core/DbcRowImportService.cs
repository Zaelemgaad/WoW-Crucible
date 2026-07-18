using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public enum DbcRowImportFormat { Csv, JsonLines, Json }

public sealed record DbcRowImportOptions(
    DbcRowImportFormat Format,
    bool AllowAppend = false,
    bool RawStringOffsets = false);

public sealed record DbcRowImportChange(
    int InputRow,
    uint? RecordKey,
    int TargetRow,
    bool Appended,
    string Column,
    string Before,
    string After);

public sealed class DbcRowImportPlan
{
    internal DbcRowImportPlan(string inputPath, string inputSha256, string sourceContentSha256, DbcRowImportFormat format,
        int inputRows, int updatedRows, int appendedRows, IReadOnlyList<DbcRowImportChange> changes,
        IReadOnlyList<string> warnings, WdbcFile preparedFile)
    {
        InputPath = inputPath; InputSha256 = inputSha256; SourceContentSha256 = sourceContentSha256; Format = format;
        InputRows = inputRows; UpdatedRows = updatedRows; AppendedRows = appendedRows; Changes = changes; Warnings = warnings; PreparedFile = preparedFile;
    }

    public string InputPath { get; }
    public string InputSha256 { get; }
    public string SourceContentSha256 { get; }
    public DbcRowImportFormat Format { get; }
    public int InputRows { get; }
    public int UpdatedRows { get; }
    public int AppendedRows { get; }
    public int ChangedCells => Changes.Count;
    public bool HasChanges => AppendedRows > 0 || Changes.Count > 0;
    public IReadOnlyList<DbcRowImportChange> Changes { get; }
    public IReadOnlyList<string> Warnings { get; }
    internal WdbcFile PreparedFile { get; }
}

public sealed record DbcRowImportApplyResult(int UpdatedRows, int AppendedRows, int ChangedCells, int ResultRows);

public static class DbcRowImportService
{
    private sealed record InputRow(int Number, IReadOnlyDictionary<string, string?> Values);

    public static DbcRowImportFormat InferFormat(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".csv" => DbcRowImportFormat.Csv,
        ".jsonl" or ".ndjson" => DbcRowImportFormat.JsonLines,
        ".json" => DbcRowImportFormat.Json,
        _ => throw new InvalidDataException("DBC row import must be .csv, .jsonl, .ndjson, or .json, or supply an explicit format.")
    };

    public static DbcRowImportPlan Preview(string dbcPath, string schemaPath, string inputPath, DbcRowImportOptions options,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var file = WdbcFile.Load(dbcPath);
        var schema = DbcSchemaCatalog.Load(schemaPath).ResolveColumns(Path.GetFileNameWithoutExtension(dbcPath), file.FieldCount);
        return Preview(file, schema, inputPath, options, progress, cancellationToken);
    }

    public static DbcRowImportPlan Preview(WdbcFile file, DbcSchemaResolution schema, string inputPath, DbcRowImportOptions options,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file); ArgumentNullException.ThrowIfNull(schema); ArgumentNullException.ThrowIfNull(options);
        inputPath = Path.GetFullPath(inputPath);
        if (!File.Exists(inputPath)) throw new FileNotFoundException("The structured DBC import file does not exist.", inputPath);
        if (schema.Columns.Count != file.FieldCount) throw new InvalidDataException("The selected schema does not match every physical field in this WDBC table.");
        var duplicateSchemaName = schema.Columns.GroupBy(column => column.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicateSchemaName is not null) throw new InvalidDataException($"The DBC schema contains duplicate column name '{duplicateSchemaName.Key}'.");

        string inputSha; using (var inputStream = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan)) inputSha = Convert.ToHexString(SHA256.HashData(inputStream));
        var sourceSha = file.ComputeContentSha256();
        var rows = ReadRows(inputPath, options.Format, cancellationToken);
        if (rows.Count == 0) throw new InvalidDataException("The structured import contains no data rows.");
        var prepared = file.CloneInMemory();
        var changes = new List<DbcRowImportChange>(); var warnings = new List<string>(); var changedTargets = new HashSet<int>(); var appendedTargets = new HashSet<int>();
        var columns = schema.Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        var physicalKey = DbcRecordIdentity.PhysicalColumn(schema.Columns, schema.KeyStrategy);
        var keyIndex = schema.KeyStrategy.Kind == DbcRecordKeyKind.NoStableKey ? null : DbcRecordIdentity.IndexRows(prepared, schema.Columns, schema.KeyStrategy);
        var claimedTargets = new HashSet<int>();

        for (var inputIndex = 0; inputIndex < rows.Count; inputIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var input = rows[inputIndex];
            ValidateNames(input, columns);
            var selector = ResolveSelector(input, physicalKey, schema.KeyStrategy);
            var (targetRow, appended, key) = ResolveTarget(prepared, schema, physicalKey, keyIndex, selector, input, options.AllowAppend);
            if (!claimedTargets.Add(targetRow)) throw new InvalidDataException($"Import row {input.Number} targets row {targetRow + 1:N0} more than once.");
            if (appended) appendedTargets.Add(targetRow);

            foreach (var pair in input.Values)
            {
                if (pair.Key is "$recordKey" or "$rowIndex" || pair.Value is null) continue;
                var column = columns[pair.Key];
                if (physicalKey is not null && column.Index == physicalKey.Index)
                {
                    var suppliedKey = ParseUInt(pair.Value, $"row {input.Number}, {column.Name}");
                    if (key != suppliedKey) throw new InvalidDataException($"Import row {input.Number} tries to change physical record key {key} to {suppliedKey}. Clone to a new key explicitly instead.");
                    continue;
                }
                if (options.Format == DbcRowImportFormat.Csv && pair.Value.Length == 0 && column.Type != DbcValueType.StringOffset) continue;
                var beforeRaw = prepared.GetRaw(targetRow, column); var before = Display(prepared, targetRow, column, options.RawStringOffsets);
                try
                {
                    if (options.RawStringOffsets && column.Type == DbcValueType.StringOffset)
                    {
                        var offset = ParseUInt(pair.Value, $"row {input.Number}, {column.Name}");
                        if (offset >= prepared.StringTableSize) throw new InvalidDataException($"String offset {offset} is outside the {prepared.StringTableSize:N0}-byte string table.");
                        prepared.SetRaw(targetRow, column, offset);
                    }
                    else prepared.SetDisplayValue(targetRow, column, pair.Value);
                }
                catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentOutOfRangeException)
                {
                    throw new InvalidDataException($"Import row {input.Number}, column '{column.Name}' has invalid value '{pair.Value}': {exception.Message}", exception);
                }
                var afterRaw = prepared.GetRaw(targetRow, column);
                if (beforeRaw == afterRaw) continue;
                changes.Add(new(input.Number, key, targetRow, appended, column.Name, before, Display(prepared, targetRow, column, options.RawStringOffsets)));
                if (!appended) changedTargets.Add(targetRow);
            }
            progress?.Report((inputIndex + 1, rows.Count));
        }

        if (schema.KeyStrategy.Kind == DbcRecordKeyKind.NoStableKey)
            warnings.Add("This table has no proven stable key. Updates use $rowIndex and are safe only while row order remains unchanged; appending is blocked.");
        if (!changes.Any() && appendedTargets.Count == 0) warnings.Add("Every supplied value already matches the open DBC; applying this plan would be a no-op.");
        return new(inputPath, inputSha, sourceSha, options.Format, rows.Count, changedTargets.Count, appendedTargets.Count, changes, warnings, prepared);
    }

    public static DbcRowImportApplyResult Apply(WdbcFile file, DbcRowImportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(file); ArgumentNullException.ThrowIfNull(plan);
        if (!file.ComputeContentSha256().Equals(plan.SourceContentSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("The open DBC changed after this import preview. Build a new preview before applying it.");
        if (plan.HasChanges) file.ReplaceContentFrom(plan.PreparedFile);
        return new(plan.UpdatedRows, plan.AppendedRows, plan.ChangedCells, plan.PreparedFile.RowCount);
    }

    private static (int Row, bool Appended, uint? Key) ResolveTarget(WdbcFile file, DbcSchemaResolution schema, DbcColumn? physicalKey,
        Dictionary<uint, int>? keyIndex, uint? selector, InputRow input, bool allowAppend)
    {
        if (schema.KeyStrategy.Kind == DbcRecordKeyKind.NoStableKey)
        {
            var row = ParseRowIndex(input, required: true)!.Value;
            if (row < 0 || row >= file.RowCount) throw new InvalidDataException($"Import row {input.Number} has $rowIndex {row}, outside the existing 0..{file.RowCount - 1} range. Tables without stable keys cannot append.");
            return (row, false, null);
        }
        if (selector is null) throw new InvalidDataException($"Import row {input.Number} requires $recordKey{(physicalKey is null ? string.Empty : $" or {physicalKey.Name}")}.");
        var key = selector.Value;
        if (keyIndex!.TryGetValue(key, out var existing)) return (existing, false, key);
        if (!allowAppend) throw new InvalidDataException($"Import row {input.Number} targets missing record key {key}. Enable append explicitly to create new records.");

        if (schema.KeyStrategy.Kind == DbcRecordKeyKind.VirtualRowIndex)
        {
            var expected = checked((uint)file.RowCount + schema.KeyStrategy.VirtualStart);
            if (key != expected) throw new InvalidDataException($"Virtual-key append must be contiguous. Import row {input.Number} has key {key}; the only safe next key is {expected}.");
            var virtualRow = file.AddBlankRow(); keyIndex[key] = virtualRow; return (virtualRow, true, key);
        }
        var appendedRow = file.AddBlankRow(); file.SetRaw(appendedRow, physicalKey!, key); keyIndex[key] = appendedRow; return (appendedRow, true, key);
    }

    private static uint? ResolveSelector(InputRow input, DbcColumn? physicalKey, DbcRecordKeyStrategy strategy)
    {
        var metadata = Value(input, "$recordKey");
        var keyValue = physicalKey is null ? null : Value(input, physicalKey.Name);
        uint? first = string.IsNullOrWhiteSpace(metadata) ? null : ParseUInt(metadata, $"row {input.Number}, $recordKey");
        uint? second = string.IsNullOrWhiteSpace(keyValue) ? null : ParseUInt(keyValue, $"row {input.Number}, {physicalKey!.Name}");
        if (first is not null && second is not null && first != second) throw new InvalidDataException($"Import row {input.Number} has conflicting $recordKey {first} and {physicalKey!.Name} {second}.");
        if (strategy.Kind == DbcRecordKeyKind.VirtualRowIndex && first is null && ParseRowIndex(input, false) is { } rowIndex)
            first = checked((uint)rowIndex + strategy.VirtualStart);
        return first ?? second;
    }

    private static int? ParseRowIndex(InputRow input, bool required)
    {
        var value = Value(input, "$rowIndex");
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required) throw new InvalidDataException($"Import row {input.Number} requires $rowIndex because this table has no stable key.");
            return null;
        }
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var row) || row < 0)
            throw new InvalidDataException($"Import row {input.Number} has invalid non-negative $rowIndex '{value}'.");
        return row;
    }

    private static void ValidateNames(InputRow input, IReadOnlyDictionary<string, DbcColumn> columns)
    {
        foreach (var name in input.Values.Keys)
            if (name is not "$recordKey" and not "$rowIndex" && !columns.ContainsKey(name))
                throw new InvalidDataException($"Import row {input.Number} contains unknown column '{name}'. Unknown fields are blocked so schema mistakes cannot be silently discarded.");
    }

    private static IReadOnlyList<InputRow> ReadRows(string path, DbcRowImportFormat format, CancellationToken cancellationToken) => format switch
    {
        DbcRowImportFormat.Csv => ReadCsv(path, cancellationToken),
        DbcRowImportFormat.JsonLines => ReadJsonLines(path, cancellationToken),
        DbcRowImportFormat.Json => ReadJson(path, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    private static IReadOnlyList<InputRow> ReadCsv(string path, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 1 << 16);
        var records = ParseCsv(reader);
        if (records.Count == 0) return [];
        var headers = records[0].Select(value => value.Trim()).ToArray();
        if (headers.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("CSV import has a blank column heading.");
        var duplicate = headers.GroupBy(value => value, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"CSV import has duplicate column heading '{duplicate.Key}'.");
        var result = new List<InputRow>();
        for (var index = 1; index < records.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var values = records[index];
            if (values.Count == 1 && values[0].Length == 0) continue;
            if (values.Count != headers.Length) throw new InvalidDataException($"CSV row {index + 1} has {values.Count} value(s), but the header has {headers.Length} column(s).");
            result.Add(new(index + 1, headers.Select((name, column) => (name, Value: (string?)values[column])).ToDictionary(pair => pair.name, pair => pair.Value, StringComparer.OrdinalIgnoreCase)));
        }
        return result;
    }

    private static IReadOnlyList<InputRow> ReadJsonLines(string path, CancellationToken cancellationToken)
    {
        var result = new List<InputRow>(); var number = 0;
        foreach (var line in File.ReadLines(path))
        {
            cancellationToken.ThrowIfCancellationRequested(); number++; if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line); result.Add(JsonRow(document.RootElement, number));
        }
        return result;
    }

    private static IReadOnlyList<InputRow> ReadJson(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("JSON DBC import must contain one array of row objects.");
        var result = new List<InputRow>(); var number = 0;
        foreach (var element in document.RootElement.EnumerateArray()) { cancellationToken.ThrowIfCancellationRequested(); result.Add(JsonRow(element, ++number)); }
        return result;
    }

    private static InputRow JsonRow(JsonElement element, int number)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"JSON import row {number} is not an object.");
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (values.ContainsKey(property.Name)) throw new InvalidDataException($"JSON import row {number} repeats property '{property.Name}'.");
            values[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.Null => null,
                _ => throw new InvalidDataException($"JSON import row {number}, property '{property.Name}' must be a string, number, or null.")
            };
        }
        return new(number, values);
    }

    private static List<List<string>> ParseCsv(TextReader reader)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var field = new StringBuilder(); var quoted = false;
        while (reader.Read() is var raw && raw >= 0)
        {
            var character = (char)raw;
            if (quoted)
            {
                if (character == '"' && reader.Peek() == '"') { field.Append('"'); reader.Read(); }
                else if (character == '"') quoted = false;
                else field.Append(character);
                continue;
            }
            if (character == '"' && field.Length == 0) { quoted = true; continue; }
            if (character == ',') { row.Add(field.ToString()); field.Clear(); continue; }
            if (character is '\r' or '\n')
            {
                if (character == '\r' && reader.Peek() == '\n') reader.Read();
                row.Add(field.ToString()); field.Clear(); rows.Add(row); row = []; continue;
            }
            field.Append(character);
        }
        if (quoted) throw new InvalidDataException("CSV import ends inside a quoted field.");
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }

    private static string? Value(InputRow row, string name) => row.Values.TryGetValue(name, out var value) ? value : null;
    private static uint ParseUInt(string value, string context) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? uint.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : throw new InvalidDataException($"{context} has invalid unsigned value '{value}'.")
        : uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : throw new InvalidDataException($"{context} has invalid unsigned value '{value}'.");
    private static string Display(WdbcFile file, int row, DbcColumn column, bool rawStrings)
    {
        if (rawStrings && column.Type == DbcValueType.StringOffset) return file.GetRaw(row, column).ToString(CultureInfo.InvariantCulture);
        return file.GetDisplayValue(row, column) switch
        {
            float value => value.ToString("R", CultureInfo.InvariantCulture),
            IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
            object value => value.ToString() ?? string.Empty,
            null => string.Empty
        };
    }
}
