namespace WoWCrucible.Core;

public static class DbcRecordIdentity
{
    public static DbcColumn? PhysicalColumn(IReadOnlyList<DbcColumn> columns, DbcRecordKeyStrategy strategy)
        => strategy.Kind == DbcRecordKeyKind.PhysicalColumn && strategy.ColumnIndex is >= 0 && strategy.ColumnIndex < columns.Count
            ? columns[strategy.ColumnIndex.Value]
            : null;

    public static uint GetKey(WdbcFile file, int row, IReadOnlyList<DbcColumn> columns, DbcRecordKeyStrategy strategy) => strategy.Kind switch
    {
        DbcRecordKeyKind.PhysicalColumn => file.GetRaw(row, PhysicalColumn(columns, strategy) ?? throw new InvalidDataException("The schema's physical key column is invalid.")),
        DbcRecordKeyKind.VirtualRowIndex => checked((uint)row + strategy.VirtualStart),
        _ => throw new InvalidDataException("This table has no proven stable record key. Select a matching schema before comparing or promoting rows.")
    };

    public static Dictionary<uint, int> IndexRows(WdbcFile file, IReadOnlyList<DbcColumn> columns, DbcRecordKeyStrategy strategy)
    {
        if (strategy.Kind == DbcRecordKeyKind.NoStableKey)
            throw new InvalidDataException("This table has no proven stable record key. Select a matching schema before comparing or promoting rows.");
        var result = new Dictionary<uint, int>();
        for (var row = 0; row < file.RowCount; row++)
        {
            var key = GetKey(file, row, columns, strategy);
            if (!result.TryAdd(key, row)) throw new InvalidDataException($"Duplicate key {key} in {Path.GetFileName(file.SourcePath)}.");
        }
        return result;
    }
}
