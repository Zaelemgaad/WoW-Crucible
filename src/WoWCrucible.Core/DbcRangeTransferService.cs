using System.Globalization;

namespace WoWCrucible.Core;

public sealed record DbcRangeTransferResult(
    int SourceRows,
    int SelectedColumns,
    int UpdatedRows,
    int AddedRows,
    int ChangedCells,
    int SkippedMissingRows,
    IReadOnlyList<string> UnmappedColumns,
    IReadOnlyDictionary<uint, uint> RemappedIds)
{
    public bool HasChanges => ChangedCells > 0 || AddedRows > 0;
}

/// <summary>
/// Applies a rectangular selection from one open client table to another. Stable
/// physical IDs are preferred over screen position, which makes a drop useful for
/// combining two complete replacements of the same DBC without duplicating rows.
/// </summary>
public static class DbcRangeTransferService
{
    public static DbcRangeTransferResult Transfer(
        WdbcFile source,
        IReadOnlyList<DbcColumn> sourceColumns,
        DbcRecordKeyStrategy sourceKeyStrategy,
        IReadOnlyList<int> sourceRows,
        IReadOnlyList<int> selectedColumnIndices,
        WdbcFile target,
        IReadOnlyList<DbcColumn> targetColumns,
        DbcRecordKeyStrategy targetKeyStrategy,
        int positionalTargetRow = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (!source.LogicalTableName.Equals(target.LogicalTableName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Selections can only be transferred between the same table. Source is {source.LogicalTableName}; target is {target.LogicalTableName}.");

        var rows = sourceRows.Distinct().ToArray();
        if (rows.Length == 0) throw new InvalidOperationException("The dragged selection contains no rows.");
        if (rows.Any(row => row < 0 || row >= source.RowCount)) throw new ArgumentOutOfRangeException(nameof(sourceRows));
        var indices = selectedColumnIndices.Distinct().Order().ToArray();
        if (indices.Length == 0) throw new InvalidOperationException("The dragged selection contains no columns.");
        if (indices.Any(index => index < 0 || index >= sourceColumns.Count)) throw new ArgumentOutOfRangeException(nameof(selectedColumnIndices));

        var targetByName = targetColumns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        var mapped = new List<(DbcColumn Source, DbcColumn Target)>();
        var unmapped = new List<string>();
        foreach (var index in indices)
        {
            var sourceColumn = sourceColumns[index];
            if (!targetByName.TryGetValue(sourceColumn.Name, out var targetColumn) ||
                sourceColumn.Size != targetColumn.Size || sourceColumn.Type != targetColumn.Type)
            {
                unmapped.Add(sourceColumn.Name);
                continue;
            }
            mapped.Add((sourceColumn, targetColumn));
        }
        if (mapped.Count == 0) throw new InvalidOperationException("None of the selected columns have a compatible destination column.");

        var sourceKey = DbcRecordIdentity.PhysicalColumn(sourceColumns, sourceKeyStrategy);
        var targetKey = DbcRecordIdentity.PhysicalColumn(targetColumns, targetKeyStrategy);
        var identityTransfer = sourceKey is not null && targetKey is not null;
        var completeRows = indices.Length == sourceColumns.Count;
        var working = target.CloneInMemory();
        var targetRows = identityTransfer
            ? Enumerable.Range(0, working.RowCount).ToDictionary(row => working.GetRaw(row, targetKey!), row => row)
            : null;
        var updatedRows = 0;
        var addedRows = 0;
        var changedCells = 0;
        var skippedMissingRows = 0;
        var remappedIds = new Dictionary<uint, uint>();

        for (var index = 0; index < rows.Length; index++)
        {
            var sourceRow = rows[index];
            int targetRow;
            if (identityTransfer)
            {
                var id = source.GetRaw(sourceRow, sourceKey!);
                if (!targetRows!.TryGetValue(id, out targetRow))
                {
                    if (!working.AllowsStructuralMutation) throw new InvalidOperationException($"Record {id:N0} is absent from the destination, but this DB2 has side tables that prevent safe row insertion.");
                    targetRow = working.AddBlankRow();
                    working.SetRaw(targetRow, targetKey!, id);
                    targetRows[id] = targetRow;
                    addedRows++;
                }
                else if (completeRows)
                {
                    var differs = mapped.Where(pair => pair.Source.Index != sourceKey!.Index).Any(pair =>
                        pair.Source.Type == DbcValueType.StringOffset
                            ? !source.GetString(source.GetRaw(sourceRow, pair.Source)).Equals(working.GetString(working.GetRaw(targetRow, pair.Target)), StringComparison.Ordinal)
                            : source.GetRaw(sourceRow, pair.Source) != working.GetRaw(targetRow, pair.Target));
                    if (!differs) continue;
                    var remappedId = working.NextId(targetKey!);
                    targetRow = working.AddBlankRow();
                    working.SetRaw(targetRow, targetKey!, remappedId);
                    targetRows[remappedId] = targetRow;
                    remappedIds[id] = remappedId;
                    addedRows++;
                }
                else updatedRows++;
            }
            else
            {
                targetRow = positionalTargetRow + index;
                if (targetRow < 0) targetRow = 0;
                if (targetRow >= working.RowCount)
                {
                    if (!working.AllowsStructuralMutation) throw new InvalidOperationException("The selection extends beyond the destination DB2 and its side tables prevent safe row insertion.");
                    while (working.RowCount <= targetRow) { working.AddBlankRow(); addedRows++; }
                }
                else updatedRows++;
            }

            foreach (var pair in mapped)
            {
                if (identityTransfer && remappedIds.ContainsKey(source.GetRaw(sourceRow, sourceKey!)) && pair.Target.Index == targetKey!.Index) continue;
                var before = working.GetRaw(targetRow, pair.Target);
                if (pair.Source.Type == DbcValueType.StringOffset)
                    working.SetDisplayValue(targetRow, pair.Target, source.GetString(source.GetRaw(sourceRow, pair.Source)));
                else
                    working.SetRaw(targetRow, pair.Target, source.GetRaw(sourceRow, pair.Source));
                if (working.GetRaw(targetRow, pair.Target) != before) changedCells++;
            }
        }

        if (changedCells > 0 || addedRows > 0) target.ReplaceContentFrom(working);
        return new(rows.Length, indices.Length, updatedRows, addedRows, changedCells, skippedMissingRows, unmapped, remappedIds);
    }

    public static int ReplaceText(
        WdbcFile file,
        IReadOnlyList<DbcColumn> columns,
        string find,
        string replacement,
        bool replaceAll,
        int startRow = 0,
        int startColumn = 0)
    {
        if (string.IsNullOrEmpty(find)) return 0;
        var working = file.CloneInMemory();
        var changed = 0;
        for (var rowOffset = 0; rowOffset < working.RowCount; rowOffset++)
        {
            var row = (startRow + rowOffset) % working.RowCount;
            for (var columnOffset = 0; columnOffset < columns.Count; columnOffset++)
            {
                var columnIndex = rowOffset == 0 ? (startColumn + columnOffset) % columns.Count : columnOffset;
                var column = columns[columnIndex];
                var text = Convert.ToString(working.GetDisplayValue(row, column), CultureInfo.InvariantCulture) ?? string.Empty;
                if (!text.Contains(find, StringComparison.OrdinalIgnoreCase)) continue;
                var replaced = ReplaceOrdinalIgnoreCase(text, find, replacement);
                working.SetDisplayValue(row, column, replaced);
                changed++;
                if (!replaceAll) { file.ReplaceContentFrom(working); return changed; }
            }
        }
        if (changed > 0) file.ReplaceContentFrom(working);
        return changed;
    }

    private static string ReplaceOrdinalIgnoreCase(string value, string find, string replacement)
    {
        var start = 0;
        var result = new System.Text.StringBuilder(value.Length);
        while (true)
        {
            var match = value.IndexOf(find, start, StringComparison.OrdinalIgnoreCase);
            if (match < 0) { result.Append(value, start, value.Length - start); return result.ToString(); }
            result.Append(value, start, match - start);
            result.Append(replacement);
            start = match + find.Length;
        }
    }
}
