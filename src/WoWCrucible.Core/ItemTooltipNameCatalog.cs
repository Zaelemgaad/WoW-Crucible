namespace WoWCrucible.Core;

/// <summary>enUS skill and enchantment names for item tooltips, read from WotLK 3.3.5a (12340) DBCs.</summary>
public sealed record ItemTooltipNameCatalog(IReadOnlyDictionary<uint, string> Skills, IReadOnlyDictionary<uint, string> Enchantments)
{
    public static ItemTooltipNameCatalog Load(string dbcFolder) => new(
        Names(Path.Combine(dbcFolder, "SkillLine.dbc"), fieldCount: 56, nameField: 3),
        Names(Path.Combine(dbcFolder, "SpellItemEnchantment.dbc"), fieldCount: 38, nameField: 14));

    /// <summary>Reads ID and the enUS string of a 12340 table; a missing file or different layout yields no names.</summary>
    private static IReadOnlyDictionary<uint, string> Names(string path, int fieldCount, int nameField)
    {
        if (!File.Exists(path)) return new Dictionary<uint, string>();
        var file = WdbcFile.Load(path);
        if (file.FieldCount != fieldCount || file.RecordSize != fieldCount * 4) return new Dictionary<uint, string>();
        var id = new DbcColumn(0, 0, 4, "ID", DbcValueType.UInt32);
        var name = new DbcColumn(nameField, nameField * 4, 4, "Name[enUS]", DbcValueType.StringOffset);
        var names = new Dictionary<uint, string>(file.RowCount);
        for (var row = 0; row < file.RowCount; row++)
            names[file.GetRaw(row, id)] = Convert.ToString(file.GetDisplayValue(row, name)) ?? string.Empty;
        return names;
    }
}
