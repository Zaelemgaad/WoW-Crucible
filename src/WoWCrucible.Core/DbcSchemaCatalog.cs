using System.Xml.Linq;

namespace WoWCrucible.Core;

public sealed class DbcSchemaCatalog
{
    private readonly Dictionary<string, IReadOnlyList<DbcColumn>> _tables;

    private DbcSchemaCatalog(Dictionary<string, IReadOnlyList<DbcColumn>> tables) => _tables = tables;

    public static DbcSchemaCatalog Load(string path)
    {
        using var stream = File.OpenRead(path);
        var document = XDocument.Load(stream, LoadOptions.None);
        var tables = new Dictionary<string, IReadOnlyList<DbcColumn>>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in document.Root?.Elements("Table") ?? [])
        {
            var tableName = (string?)table.Attribute("Name");
            if (string.IsNullOrWhiteSpace(tableName))
                continue;

            var columns = new List<DbcColumn>();
            var offset = 0;
            foreach (var field in table.Elements("Field"))
            {
                // Some packed tables use a synthetic editor-only key which is not present in the file record.
                if ((bool?)field.Attribute("AutoGenerate") == true)
                    continue;
                var name = (string?)field.Attribute("Name") ?? $"Field_{columns.Count}";
                var type = ((string?)field.Attribute("Type") ?? "int").ToLowerInvariant();
                var count = (int?)field.Attribute("ArraySize") ?? 1;
                var isIndex = (bool?)field.Attribute("IsIndex") ?? false;

                // WDBC stores localized strings as 16 locale offsets followed by locale flags.
                if (type == "loc")
                {
                    for (var i = 0; i < 16; i++)
                    {
                        columns.Add(new(columns.Count, offset, 4, $"{name}[{LocaleName(i)}]", DbcValueType.StringOffset, isIndex));
                        offset += 4;
                    }
                    columns.Add(new(columns.Count, offset, 4, $"{name}[Flags]", DbcValueType.UInt32));
                    offset += 4;
                    continue;
                }

                // This first editor operates on WDBC's 32-bit cells. A 64-bit schema value occupies two cells.
                if (type is "long" or "ulong")
                {
                    columns.Add(new(columns.Count, offset, 4, $"{name}[Low]", DbcValueType.UInt32, isIndex));
                    offset += 4;
                    columns.Add(new(columns.Count, offset, 4, $"{name}[High]", DbcValueType.UInt32));
                    offset += 4;
                    continue;
                }

                var valueType = type switch
                {
                    "int" => DbcValueType.Int32,
                    "uint" => DbcValueType.UInt32,
                    "float" => DbcValueType.Float32,
                    "string" => DbcValueType.StringOffset,
                    "byte" => DbcValueType.Byte,
                    _ => DbcValueType.Raw32
                };

                for (var i = 0; i < count; i++)
                {
                    var size = valueType == DbcValueType.Byte ? 1 : 4;
                    columns.Add(new(columns.Count, offset, size, count == 1 ? name : $"{name}[{i}]", valueType, isIndex));
                    offset += size;
                }
            }

            tables[tableName] = columns;
        }

        return new(tables);
    }

    public IReadOnlyList<DbcColumn> GetColumns(string tableName, int physicalFieldCount)
    {
        if (_tables.TryGetValue(tableName, out var defined) && defined.Count == physicalFieldCount)
            return defined;

        return Enumerable.Range(0, physicalFieldCount)
            .Select(i => new DbcColumn(i, i * 4, 4, i == 0 ? "ID" : $"Field_{i}", DbcValueType.Raw32, i == 0))
            .ToArray();
    }

    private static string LocaleName(int index) => index switch
    {
        0 => "enUS", 1 => "koKR", 2 => "frFR", 3 => "deDE", 4 => "zhCN", 5 => "zhTW",
        6 => "esES", 7 => "esMX", 8 => "ruRU", 9 => "ptBR", 10 => "itIT",
        _ => $"Locale{index}"
    };
}
