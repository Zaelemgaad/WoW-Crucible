using System.Text.Json;

namespace WoWCrucible.Core;

public sealed record DbcCellDifference(uint Id, int ColumnIndex, string ColumnName, string BaseValue, string OverrideValue);
public sealed record DbcPromotionOperation(uint Id, IReadOnlyList<string> Columns);
public sealed record DbcPromotionManifest(int FormatVersion, string TableName, string KeyColumn, IReadOnlyList<DbcPromotionOperation> Operations);

public static class DbcPromotionService
{
    public static IReadOnlyList<DbcCellDifference> GetDifferences(string basePath, string overridePath, IReadOnlyList<DbcColumn> columns, CancellationToken cancellationToken = default)
    {
        var baseFile = WdbcFile.Load(basePath); var overrideFile = WdbcFile.Load(overridePath);
        ValidateLayouts(baseFile, overrideFile, columns);
        var key = columns.FirstOrDefault(column => column.IsIndex) ?? columns[0];
        var baseRows = IndexRows(baseFile, key); var overrideRows = IndexRows(overrideFile, key);
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
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static DbcPromotionManifest LoadManifest(string path)
    {
        var manifest = JsonSerializer.Deserialize<DbcPromotionManifest>(File.ReadAllText(path)) ?? throw new InvalidDataException("The promotion manifest is empty.");
        if (manifest.FormatVersion != 1) throw new InvalidDataException($"Unsupported promotion manifest version: {manifest.FormatVersion}");
        return manifest;
    }

    public static void Apply(string basePath, string overridePath, string outputPath, IReadOnlyList<DbcColumn> columns, DbcPromotionManifest manifest)
    {
        var tableName = Path.GetFileNameWithoutExtension(basePath);
        if (!tableName.Equals(manifest.TableName, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Manifest targets {manifest.TableName}, not {tableName}.");
        var baseFile = WdbcFile.Load(basePath); var overrideFile = WdbcFile.Load(overridePath);
        ValidateLayouts(baseFile, overrideFile, columns);
        var key = columns.FirstOrDefault(column => column.IsIndex) ?? columns[0];
        if (!key.Name.Equals(manifest.KeyColumn, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Manifest key is {manifest.KeyColumn}; current schema key is {key.Name}.");
        var baseRows = IndexRows(baseFile, key); var overrideRows = IndexRows(overrideFile, key);
        var byName = columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var operation in manifest.Operations)
        {
            if (!overrideRows.TryGetValue(operation.Id, out var sourceRow)) throw new InvalidDataException($"Override is missing ID {operation.Id}.");
            var entireRow = operation.Columns.Count == 1 && operation.Columns[0] == "*";
            if (!baseRows.TryGetValue(operation.Id, out var destinationRow))
            {
                if (!entireRow) throw new InvalidDataException($"Base is missing ID {operation.Id}; a partial-field promotion cannot create it.");
                destinationRow = baseFile.AddBlankRow(); baseRows[operation.Id] = destinationRow;
            }
            var selectedColumns = entireRow ? columns : operation.Columns.Select(name => byName.TryGetValue(name, out var column) ? column : throw new InvalidDataException($"Current schema has no column named '{name}'.")).ToArray();
            foreach (var column in selectedColumns) CopyValue(overrideFile, sourceRow, baseFile, destinationRow, column);
        }
        baseFile.Save(outputPath);
    }

    private static Dictionary<uint, int> IndexRows(WdbcFile file, DbcColumn key)
    {
        var result = new Dictionary<uint, int>();
        for (var row = 0; row < file.RowCount; row++)
            if (!result.TryAdd(file.GetRaw(row, key), row)) throw new InvalidDataException($"Duplicate key {file.GetRaw(row, key)} in {Path.GetFileName(file.SourcePath)}.");
        return result;
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
