using System.Text.RegularExpressions;

namespace WoWCrucible.Core;

public sealed record SpellTooltipRecord(uint Id, string Name, string Subtext, string Description, string AuraDescription)
{
    public string PreferredItemText(int trigger) => trigger == 1
        ? First(AuraDescription, Description)
        : First(Description, AuraDescription);
    private static string First(string preferred, string fallback) => string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
}

public sealed record SpellTooltipCatalog(string SourcePath, DateTime LastWriteUtc, IReadOnlyDictionary<uint, SpellTooltipRecord> Records)
{
    public bool Matches(string path) => SourcePath.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) && File.GetLastWriteTimeUtc(path) == LastWriteUtc;
}

public static partial class SpellTooltipService
{
    public static SpellTooltipCatalog Load(string spellDbcPath)
    {
        spellDbcPath = Path.GetFullPath(spellDbcPath); var file = WdbcFile.Load(spellDbcPath); var schema = DbcSchemaCatalog.CreateBuiltIn12340().ResolveColumns("Spell", file.FieldCount);
        if (schema.MatchKind != DbcSchemaMatchKind.NamedMatch || file.FieldCount != 234) throw new InvalidDataException($"Spell.dbc requires the WotLK build-12340 234-field layout; this file has {file.FieldCount:N0} fields.");
        var id = Column("ID"); var name = Column("Name[enUS]"); var subtext = Column("NameSubtext[enUS]"); var description = Column("Description[enUS]"); var aura = Column("AuraDescription[enUS]");
        var records = new Dictionary<uint, SpellTooltipRecord>(file.RowCount);
        for (var row = 0; row < file.RowCount; row++)
        {
            var spellId = file.GetRaw(row, id); records[spellId] = new(spellId, Text(row, name), Text(row, subtext), Text(row, description), Text(row, aura));
        }
        return new(spellDbcPath, File.GetLastWriteTimeUtc(spellDbcPath), records);

        DbcColumn Column(string value) => schema.Columns.First(column => column.Name.Equals(value, StringComparison.OrdinalIgnoreCase));
        string Text(int row, DbcColumn column) => Clean(Convert.ToString(file.GetDisplayValue(row, column)) ?? string.Empty);
    }

    public static string Clean(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        value = ColorStart().Replace(value, string.Empty).Replace("|r", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("\r", " ").Replace("\n", " ");
        return Whitespace().Replace(value, " ").Trim();
    }

    [GeneratedRegex(@"\|c[0-9a-fA-F]{8}", RegexOptions.CultureInvariant)] private static partial Regex ColorStart();
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)] private static partial Regex Whitespace();
}
