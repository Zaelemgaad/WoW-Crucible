using System.Globalization;
using System.Security.Cryptography;

namespace WoWCrucible.Core;

internal static class ClientTableMutationPrimitives
{
    public static void CompareEveryCell(WdbcFile expected, IReadOnlyList<DbcColumn> expectedColumns, WdbcFile actual, IReadOnlyList<DbcColumn> actualColumns)
    {
        if (expected.RowCount != actual.RowCount || expectedColumns.Count != actualColumns.Count)
            throw new InvalidDataException($"Mutated output shape differs: rows {expected.RowCount:N0}/{actual.RowCount:N0}, columns {expectedColumns.Count:N0}/{actualColumns.Count:N0}.");
        for (var columnIndex = 0; columnIndex < expectedColumns.Count; columnIndex++)
            if (!expectedColumns[columnIndex].Name.Equals(actualColumns[columnIndex].Name, StringComparison.Ordinal) ||
                expectedColumns[columnIndex].Type != actualColumns[columnIndex].Type || expectedColumns[columnIndex].Size != actualColumns[columnIndex].Size)
                throw new InvalidDataException($"Mutated output schema differs at logical column {columnIndex:N0}.");

        for (var row = 0; row < expected.RowCount; row++)
        {
            for (var columnIndex = 0; columnIndex < expectedColumns.Count; columnIndex++)
            {
                var leftColumn = expectedColumns[columnIndex];
                var rightColumn = actualColumns[columnIndex];
                if (leftColumn.Type == DbcValueType.StringOffset)
                {
                    var left = Convert.ToString(expected.GetDisplayValue(row, leftColumn), CultureInfo.InvariantCulture) ?? string.Empty;
                    var right = Convert.ToString(actual.GetDisplayValue(row, rightColumn), CultureInfo.InvariantCulture) ?? string.Empty;
                    if (!left.Equals(right, StringComparison.Ordinal))
                        throw new InvalidDataException($"String mismatch at row {row:N0}, column {leftColumn.Name}: '{left}' != '{right}'.");
                }
                else
                {
                    var left = expected.GetRaw64(row, leftColumn);
                    var right = actual.GetRaw64(row, rightColumn);
                    if (left != right)
                        throw new InvalidDataException($"Value mismatch at row {row:N0}, column {leftColumn.Name}: 0x{left:X} != 0x{right:X}.");
                }
            }
        }
    }

    public static void Mutate(WdbcFile file, int row, DbcColumn column, string marker)
    {
        if (column.Type == DbcValueType.StringOffset)
        {
            var current = Convert.ToString(file.GetDisplayValue(row, column), CultureInfo.InvariantCulture) ?? string.Empty;
            file.SetDisplayValue(row, column, current + marker);
            return;
        }
        if (column.Type == DbcValueType.Float32)
        {
            var current = file.GetRaw64(row, column);
            var first = (ulong)BitConverter.SingleToUInt32Bits(123.25f);
            file.SetRaw64(row, column, current == first ? BitConverter.SingleToUInt32Bits(-17.5f) : first);
            return;
        }
        if (column.Type is DbcValueType.Int32 or DbcValueType.Int64)
        {
            var current = Convert.ToInt64(file.GetDisplayValue(row, column), CultureInfo.InvariantCulture);
            file.SetDisplayValue(row, column, current == -1 ? "1" : "-1");
            return;
        }

        var bits = column.EffectiveBitWidth;
        var currentRaw = file.GetRaw64(row, column);
        ulong next = bits == 1 ? currentRaw ^ 1UL : currentRaw == 1 ? 2UL : 1UL;
        file.SetRaw64(row, column, next);
    }

    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
