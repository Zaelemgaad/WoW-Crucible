namespace WoWCrucible.Core;

public static class DbcRowMutationService
{
    public static void CopyRow(string basePath, string sourcePath, string outputPath, IReadOnlyList<DbcColumn> columns, DbcRecordKeyStrategy keyStrategy, uint sourceId, uint targetId, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var destination = WdbcFile.Load(basePath); var source = WdbcFile.Load(sourcePath);
        Validate(destination, source, columns); var key = PhysicalKey(columns, keyStrategy);
        var destinationRows = DbcRecordIdentity.IndexRows(destination, columns, keyStrategy); var sourceRows = DbcRecordIdentity.IndexRows(source, columns, keyStrategy);
        if (destinationRows.ContainsKey(targetId)) throw new InvalidDataException($"Target ID {targetId} already exists.");
        if (!sourceRows.TryGetValue(sourceId, out var sourceRow)) throw new InvalidDataException($"Source ID {sourceId} does not exist.");
        var row = destination.AddBlankRow();
        foreach (var column in columns)
        {
            if (column.Type == DbcValueType.StringOffset) destination.SetDisplayValue(row, column, source.GetString(source.GetRaw(sourceRow, column)));
            else destination.SetRaw(row, column, source.GetRaw(sourceRow, column));
        }
        destination.SetRaw(row, key, targetId); ApplyOverrides(destination, row, columns, overrides); destination.Save(outputPath);
    }

    public static void SetRow(string inputPath, string outputPath, IReadOnlyList<DbcColumn> columns, DbcRecordKeyStrategy keyStrategy, uint id, IReadOnlyDictionary<string, string> values)
    {
        if (values.Count == 0) throw new InvalidOperationException("Set at least one field value.");
        var file = WdbcFile.Load(inputPath); var rows = DbcRecordIdentity.IndexRows(file, columns, keyStrategy);
        if (!rows.TryGetValue(id, out var row)) throw new InvalidDataException($"Record ID {id} does not exist.");
        ApplyOverrides(file, row, columns, values); file.Save(outputPath);
    }

    private static void ApplyOverrides(WdbcFile file, int row, IReadOnlyList<DbcColumn> columns, IReadOnlyDictionary<string, string>? values)
    {
        foreach (var pair in values ?? new Dictionary<string, string>())
        {
            var column = columns.FirstOrDefault(column => column.Name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"The schema has no named column '{pair.Key}'.");
            file.SetDisplayValue(row, column, pair.Value);
        }
    }

    private static DbcColumn PhysicalKey(IReadOnlyList<DbcColumn> columns, DbcRecordKeyStrategy keyStrategy) =>
        DbcRecordIdentity.PhysicalColumn(columns, keyStrategy) ?? throw new InvalidOperationException("Row copying requires a physical record ID.");

    private static void Validate(WdbcFile destination, WdbcFile source, IReadOnlyList<DbcColumn> columns)
    {
        if (destination.FieldCount != source.FieldCount || destination.RecordSize != source.RecordSize) throw new InvalidDataException("The base and source DBC layouts differ.");
        if (columns.Count != destination.FieldCount) throw new InvalidDataException("The named schema does not match this DBC layout.");
    }
}
