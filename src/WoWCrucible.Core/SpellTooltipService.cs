using System.Text.RegularExpressions;

namespace WoWCrucible.Core;

public sealed record SpellTooltipRecord(uint Id, string Name, string Subtext, string Description, string AuraDescription)
{
    public string PreferredItemText(int trigger) => trigger == 1
        ? First(AuraDescription, Description)
        : First(Description, AuraDescription);
    private static string First(string preferred, string fallback) => string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
}

public sealed record SpellTooltipCatalog(string SourcePath, DateTime LastWriteUtc, IReadOnlyDictionary<uint, SpellTooltipRecord> Records, IReadOnlyDictionary<string,DateTime>? Dependencies = null)
{
    public bool Matches(string path) => SourcePath.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) && File.GetLastWriteTimeUtc(path) == LastWriteUtc && (Dependencies is null || Dependencies.All(p=>File.GetLastWriteTimeUtc(p.Key)==p.Value));
}

public static partial class SpellTooltipService
{
    public static SpellTooltipCatalog Load(string spellDbcPath)
    {
        spellDbcPath = Path.GetFullPath(spellDbcPath); var file = WdbcFile.Load(spellDbcPath); var schema = DbcSchemaCatalog.CreateBuiltIn12340().ResolveColumns("Spell", file.FieldCount);
        if (schema.MatchKind != DbcSchemaMatchKind.NamedMatch || file.FieldCount != 234) throw new InvalidDataException($"Spell.dbc requires the WotLK build-12340 234-field layout; this file has {file.FieldCount:N0} fields.");
        var id = Column("ID"); var name = Column("Name[enUS]"); var subtext = Column("NameSubtext[enUS]"); var description = Column("Description[enUS]"); var aura = Column("AuraDescription[enUS]");
        var related = new Dictionary<string, WdbcFile>();
        var dependencies = new Dictionary<string,DateTime>();
        foreach (var table in new[] { "SpellDuration", "SpellRadius" })
        {
            var path=Path.Combine(Path.GetDirectoryName(spellDbcPath)!, table+".dbc");
            dependencies[path]=File.GetLastWriteTimeUtc(path);
            if(File.Exists(path)) related[table]=WdbcFile.Load(path);
        }
        var renderer=CreateRenderer(file,related);
        var records = new Dictionary<uint, SpellTooltipRecord>(file.RowCount);
        for (var row = 0; row < file.RowCount; row++)
        {
            var spellId = file.GetRaw(row, id); records[spellId] = new(spellId, renderer.Render(Text(row, name),spellId).Text, renderer.Render(Text(row, subtext),spellId).Text, renderer.Render(Text(row, description),spellId).Text, renderer.Render(Text(row, aura),spellId).Text);
        }
        return new(spellDbcPath, File.GetLastWriteTimeUtc(spellDbcPath), records, dependencies);

        DbcColumn Column(string value) => schema.Columns.First(column => column.Name.Equals(value, StringComparison.OrdinalIgnoreCase));
        string Text(int row, DbcColumn column) => Convert.ToString(file.GetDisplayValue(row, column)) ?? string.Empty;
    }

    public static SpellDescriptionRenderer CreateRenderer(WdbcFile spell, IReadOnlyDictionary<string,WdbcFile>? related = null)
    {
        var files=new Dictionary<string,WdbcFile>(related ?? new Dictionary<string,WdbcFile>()) { ["Spell"]=spell };
        var schema=DbcSchemaCatalog.CreateBuiltIn12340();
        var tables=files.ToDictionary(p=>p.Key,p=>{
            var columns=schema.ResolveColumns(p.Key,p.Value.FieldCount);
            if(!columns.IsExactFor(p.Value))throw new InvalidDataException("Exact schema required for tooltip table "+p.Key);
            return (File:p.Value,Columns:columns.Columns.ToDictionary(c=>c.Name,StringComparer.OrdinalIgnoreCase),Index:DbcRecordIdentity.IndexRows(p.Value,columns.Columns,columns.KeyStrategy));
        });
        object? Read(string table,uint id,string column) => tables.TryGetValue(table,out var data) && data.Index.TryGetValue(id,out var row) && data.Columns.TryGetValue(column,out var definition)
            ? data.File.GetDisplayValue(row,definition) : null;
        return new((id,column)=>Read("Spell",id,column),Read);
    }

    public static string Clean(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        value = SpellDescriptionRenderer.PlainText(value).Replace("\r", " ").Replace("\n", " ");
        return Whitespace().Replace(value, " ").Trim();
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)] private static partial Regex Whitespace();
}
