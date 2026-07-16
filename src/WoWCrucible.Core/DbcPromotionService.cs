using System.Text.Json;

namespace WoWCrucible.Core;

public sealed record DbcCellDifference(uint Id, int ColumnIndex, string ColumnName, string BaseValue, string OverrideValue);
public sealed record DbcPromotionOperation(uint Id, IReadOnlyList<string> Columns);
public sealed record DbcPromotionManifest(int FormatVersion, string TableName, string KeyColumn, IReadOnlyList<DbcPromotionOperation> Operations);

public static class DbcPromotionService
{
    public static DbcPromotionManifest CreateAdditionsManifest(string basePath, string overridePath, IReadOnlyList<DbcColumn> columns, DbcRecordKeyStrategy keyStrategy, CancellationToken cancellationToken = default)
    {
        if (keyStrategy.Kind == DbcRecordKeyKind.NoStableKey)
            throw new InvalidOperationException("Additive promotion requires a physical ID or append-only virtual row key.");
        var additions = GetDifferences(basePath, overridePath, columns, keyStrategy, cancellationToken)
            .Where(difference => difference.ColumnIndex < 0).Select(difference => difference.Id).Distinct().Order().Select(id => new DbcPromotionOperation(id, ["*"])).ToArray();
        return new(1, Path.GetFileNameWithoutExtension(basePath), keyStrategy.DisplayName(columns), additions);
    }

    public static IReadOnlyList<DbcCellDifference> GetDifferences(string basePath, string overridePath, IReadOnlyList<DbcColumn> columns, DbcRecordKeyStrategy keyStrategy, CancellationToken cancellationToken = default)
    {
        var baseFile = WdbcFile.Load(basePath); var overrideFile = WdbcFile.Load(overridePath);
        ValidateLayouts(baseFile, overrideFile, columns);
        var baseRows = DbcRecordIdentity.IndexRows(baseFile, columns, keyStrategy); var overrideRows = DbcRecordIdentity.IndexRows(overrideFile, columns, keyStrategy);
        var differences = new List<DbcCellDifference>();
        foreach (var (id, overrideRow) in overrideRows.OrderBy(pair => pair.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!baseRows.TryGetValue(id, out var baseRow))
            {
                differences.Add(new(id, -1, "(entire new row)", "<missing>", "<new row>"));
                continue;
            }
            foreach (var column in columns)
            {
                if (ValuesEqual(baseFile, baseRow, overrideFile, overrideRow, column)) continue;
                differences.Add(new(id, column.Index, column.Name, Display(baseFile, baseRow, column), Display(overrideFile, overrideRow, column)));
            }
        }
        return differences;
    }

    public static void SaveManifest(string path, string tableName, string keyColumn, IEnumerable<DbcPromotionOperation> operations)
    {
        var manifest = new DbcPromotionManifest(1, tableName, keyColumn, operations.ToArray());
        SaveManifest(path, manifest);
    }

    public static void SaveManifest(string path, DbcPromotionManifest manifest)
    {
        path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }

    public static DbcPromotionManifest LoadManifest(string path)
    {
        var manifest = JsonSerializer.Deserialize<DbcPromotionManifest>(File.ReadAllText(path)) ?? throw new InvalidDataException("The promotion manifest is empty.");
        if (manifest.FormatVersion != 1) throw new InvalidDataException($"Unsupported promotion manifest version: {manifest.FormatVersion}");
        return manifest;
    }

    public static void Apply(string basePath, string overridePath, string outputPath, IReadOnlyList<DbcColumn> columns, DbcRecordKeyStrategy keyStrategy, DbcPromotionManifest manifest)
    {
        var tableName = Path.GetFileNameWithoutExtension(basePath);
        if (!tableName.Equals(manifest.TableName, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Manifest targets {manifest.TableName}, not {tableName}.");
        var baseFile = WdbcFile.Load(basePath); var overrideFile = WdbcFile.Load(overridePath);
        ValidateLayouts(baseFile, overrideFile, columns);
        var keyName = keyStrategy.DisplayName(columns);
        if (!keyName.Equals(manifest.KeyColumn, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Manifest key is {manifest.KeyColumn}; current schema key is {keyName}.");
        var baseRows = DbcRecordIdentity.IndexRows(baseFile, columns, keyStrategy); var overrideRows = DbcRecordIdentity.IndexRows(overrideFile, columns, keyStrategy);
        var byName = columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var operation in manifest.Operations)
        {
            if (!overrideRows.TryGetValue(operation.Id, out var sourceRow)) throw new InvalidDataException($"Override is missing ID {operation.Id}.");
            var entireRow = operation.Columns.Count == 1 && operation.Columns[0] == "*";
            if (!baseRows.TryGetValue(operation.Id, out var destinationRow))
            {
                if (!entireRow) throw new InvalidDataException($"Base is missing ID {operation.Id}; a partial-field promotion cannot create it.");
                if (keyStrategy.Kind == DbcRecordKeyKind.VirtualRowIndex && operation.Id != baseFile.RowCount + keyStrategy.VirtualStart)
                    throw new InvalidDataException($"Virtual row {operation.Id} cannot be inserted into the middle of the table. Only the next row ({baseFile.RowCount + keyStrategy.VirtualStart}) can be appended safely.");
                var physicalKey = DbcRecordIdentity.PhysicalColumn(columns, keyStrategy);
                destinationRow = baseFile.AddBlankRow(physicalKey); baseRows[operation.Id] = destinationRow;
            }
            var selectedColumns = entireRow ? columns : operation.Columns.Select(name => byName.TryGetValue(name, out var column) ? column : throw new InvalidDataException($"Current schema has no column named '{name}'.")).ToArray();
            foreach (var column in selectedColumns) CopyValue(overrideFile, sourceRow, baseFile, destinationRow, column);
        }
        baseFile.Save(outputPath);
    }

    private static void CopyValue(WdbcFile source, int sourceRow, WdbcFile destination, int destinationRow, DbcColumn column)
    {
        if (column.Type == DbcValueType.StringOffset) destination.SetDisplayValue(destinationRow, column, source.GetString(source.GetRaw(sourceRow, column)));
        else destination.SetRaw(destinationRow, column, source.GetRaw(sourceRow, column));
    }

    private static bool ValuesEqual(WdbcFile left, int leftRow, WdbcFile right, int rightRow, DbcColumn column) => column.Type == DbcValueType.StringOffset
        ? left.GetString(left.GetRaw(leftRow, column)).Equals(right.GetString(right.GetRaw(rightRow, column)), StringComparison.Ordinal)
        : left.GetRaw(leftRow, column) == right.GetRaw(rightRow, column);

    private static string Display(WdbcFile file, int row, DbcColumn column) => Convert.ToString(file.GetDisplayValue(row, column), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    private static void ValidateLayouts(WdbcFile baseFile, WdbcFile overrideFile, IReadOnlyList<DbcColumn> columns)
    {
        if (baseFile.FieldCount != overrideFile.FieldCount || baseFile.RecordSize != overrideFile.RecordSize) throw new InvalidDataException("The base and override DBC layouts differ.");
        if (columns.Count != baseFile.FieldCount) throw new InvalidDataException("The named schema does not match this DBC layout.");
    }
}
