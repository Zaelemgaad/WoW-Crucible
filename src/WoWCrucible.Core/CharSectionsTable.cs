namespace WoWCrucible.Core;

internal static class CharSectionsTable
{
    internal static readonly DbcColumn[] Columns =
    [
        new(0, 0, 4, "ID", DbcValueType.UInt32, true),
        new(1, 4, 4, "RaceID", DbcValueType.UInt32),
        new(2, 8, 4, "SexID", DbcValueType.UInt32),
        new(3, 12, 4, "BaseSection", DbcValueType.UInt32),
        new(4, 16, 4, "TextureName[0]", DbcValueType.StringOffset),
        new(5, 20, 4, "TextureName[1]", DbcValueType.StringOffset),
        new(6, 24, 4, "TextureName[2]", DbcValueType.StringOffset),
        new(7, 28, 4, "Flags", DbcValueType.UInt32),
        new(8, 32, 4, "VariationIndex", DbcValueType.UInt32),
        new(9, 36, 4, "ColorIndex", DbcValueType.UInt32)
    ];

    internal static void Validate(WdbcFile file, string name)
    {
        if (file.ContainerKind != ClientTableContainerKind.Wdbc || file.FieldCount != Columns.Length || file.RecordSize != 40)
            throw new InvalidDataException(
                $"{name} is {file.ContainerKind} with {file.FieldCount:N0} fields and {file.RecordSize:N0}-byte records; " +
                "the Wrath CharSections layout requires WDBC, 10 fields, and 40-byte records.");
    }

    internal static void CopyRow(WdbcFile source, int sourceRow, WdbcFile target, int targetRow)
    {
        foreach (var column in Columns)
        {
            if (column.Type == DbcValueType.StringOffset)
                target.SetDisplayValue(targetRow, column, Convert.ToString(source.GetDisplayValue(sourceRow, column)) ?? string.Empty);
            else
                target.SetRaw(targetRow, column, source.GetRaw(sourceRow, column));
        }
    }
}
