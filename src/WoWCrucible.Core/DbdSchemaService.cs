using System.Text.RegularExpressions;

namespace WoWCrucible.Core;

public enum DbdPrimitiveType { Int, Float, String, LocString, Unknown }
public sealed record DbdColumnDefinition(string Name, DbdPrimitiveType Type, string? Reference, string? Comment);
public sealed record DbdClientBuild(int Major, int Minor, int Patch, int Build) : IComparable<DbdClientBuild>
{
    public int CompareTo(DbdClientBuild? other) { if (other is null) return 1; var left = new[] { Major, Minor, Patch, Build }; var right = new[] { other.Major, other.Minor, other.Patch, other.Build }; for (var i = 0; i < left.Length; i++) { var comparison = left[i].CompareTo(right[i]); if (comparison != 0) return comparison; } return 0; }
    public override string ToString() => $"{Major}.{Minor}.{Patch}.{Build}";
    public static DbdClientBuild FromNumber(int build) => build switch
    {
        3368 => new(0,5,3,3368), 5875 => new(1,12,1,5875), 8606 => new(2,4,3,8606), 12340 => new(3,3,5,12340),
        15595 => new(4,3,4,15595), 18414 => new(5,4,8,18414), 21742 => new(6,2,4,21742), 26972 => new(7,3,5,26972),
        _ => new(0,0,0,build)
    };
}
public sealed record DbdBuildRange(DbdClientBuild Start, DbdClientBuild End, string Raw)
{
    public bool Contains(int build) => Contains(DbdClientBuild.FromNumber(build));
    public bool Contains(DbdClientBuild build) => build.CompareTo(Start) >= 0 && build.CompareTo(End) <= 0;
}
public sealed record DbdField(string Name, int ArraySize, int BitWidth, bool Unsigned, bool IsId, bool NonInline, string Raw);
public sealed record DbdLayout(IReadOnlyList<DbdBuildRange> Builds, IReadOnlyList<string> LayoutHashes, IReadOnlyList<string> Comments, IReadOnlyList<DbdField> Fields)
{
    public bool Supports(int build) => Builds.Any(range => range.Contains(build));
}
public sealed record DbdDefinition(string Path, string TableName, IReadOnlyDictionary<string, DbdColumnDefinition> Columns, IReadOnlyList<DbdLayout> Layouts)
{
    public DbdLayout? ForBuild(int build) => Layouts.FirstOrDefault(layout => layout.Supports(build));
}
public enum DbdAuditStatus { Match, EmptyPlaceholder, MissingDefinition, MissingBuild, FieldCountMismatch, InvalidDbc, InvalidDefinition }
public sealed record DbdSchemaAuditRow(string Table, DbdAuditStatus Status, int ActualFields, int? DbdFields, int? XmlFields, string Message);
public sealed record DbdSchemaAuditSummary(int Build, string DefinitionsRoot, string DbcRoot, IReadOnlyList<DbdSchemaAuditRow> Rows)
{
    public int Matches => Rows.Count(row => row.Status == DbdAuditStatus.Match);
    public int EmptyPlaceholders => Rows.Count(row => row.Status == DbdAuditStatus.EmptyPlaceholder);
    public int Failures => Rows.Count(row => row.Status is not DbdAuditStatus.Match and not DbdAuditStatus.EmptyPlaceholder);
}

public static partial class DbdSchemaService
{
    public static DbdDefinition Load(string path)
    {
        path = Path.GetFullPath(path); if (!File.Exists(path)) throw new FileNotFoundException("The DBD definition does not exist.", path);
        var lines = File.ReadAllLines(path); var columns = new Dictionary<string, DbdColumnDefinition>(StringComparer.OrdinalIgnoreCase); var layouts = new List<DbdLayout>();
        var inColumns = false; var builds = new List<DbdBuildRange>(); var hashes = new List<string>(); var comments = new List<string>(); var fields = new List<DbdField>();
        foreach (var original in lines)
        {
            var line = original.Trim(); if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.Equals("COLUMNS", StringComparison.OrdinalIgnoreCase)) { Finish(); inColumns = true; continue; }
            if (line.StartsWith("BUILD ", StringComparison.OrdinalIgnoreCase)) { if (fields.Count > 0) Finish(); inColumns = false; builds.AddRange(ParseBuilds(line[6..])); continue; }
            if (line.StartsWith("LAYOUT ", StringComparison.OrdinalIgnoreCase)) { if (fields.Count > 0) Finish(); inColumns = false; hashes.AddRange(line[7..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); continue; }
            if (line.StartsWith("COMMENT ", StringComparison.OrdinalIgnoreCase)) { comments.Add(line[8..].Trim()); continue; }
            if (inColumns) { var column = ParseColumn(line); columns[column.Name] = column; }
            else if (builds.Count > 0 || hashes.Count > 0) fields.Add(ParseField(line));
        }
        Finish(); if (columns.Count == 0) throw new InvalidDataException($"{path} has no COLUMNS section."); if (layouts.Count == 0) throw new InvalidDataException($"{path} has no build layouts.");
        return new(path, Path.GetFileNameWithoutExtension(path), columns, layouts);

        void Finish()
        {
            if (fields.Count > 0) layouts.Add(new(builds.ToArray(), hashes.ToArray(), comments.ToArray(), fields.ToArray()));
            builds.Clear(); hashes.Clear(); comments.Clear(); fields.Clear();
        }
    }

    public static IReadOnlyList<DbcColumn> ResolveColumns(DbdDefinition definition, int build)
    {
        var layout = definition.ForBuild(build) ?? throw new KeyNotFoundException($"{definition.TableName}.dbd has no layout covering client build {build:N0}.");
        var result = new List<DbcColumn>(); var offset = 0;
        foreach (var field in layout.Fields)
        {
            if (field.NonInline) continue;
            if (!definition.Columns.TryGetValue(field.Name, out var logical)) throw new InvalidDataException($"{definition.TableName}.dbd layout references undefined column '{field.Name}'.");
            if (logical.Type == DbdPrimitiveType.LocString)
            {
                if (field.ArraySize != 1) throw new InvalidDataException($"Localized field '{field.Name}' unexpectedly declares array size {field.ArraySize:N0}.");
                if (build <= 12340)
                {
                    for (var locale = 0; locale < 16; locale++) Add($"{field.Name}[{Locale(locale)}]", DbcValueType.StringOffset, 4, field.IsId);
                    Add($"{field.Name}[Flags]", DbcValueType.UInt32, 4, false);
                }
                else Add(field.Name, DbcValueType.StringOffset, Math.Max(1, field.BitWidth / 8), field.IsId);
                continue;
            }
            for (var element = 0; element < field.ArraySize; element++)
            {
                var name = field.ArraySize == 1 ? field.Name : $"{field.Name}[{element}]";
                var size = Math.Max(1, field.BitWidth / 8); var type = logical.Type switch
                {
                    DbdPrimitiveType.String => DbcValueType.StringOffset,
                    DbdPrimitiveType.Float => DbcValueType.Float32,
                    DbdPrimitiveType.Int when field.BitWidth == 8 && field.Unsigned => DbcValueType.Byte,
                    DbdPrimitiveType.Int when field.Unsigned => DbcValueType.UInt32,
                    DbdPrimitiveType.Int => DbcValueType.Int32,
                    _ => DbcValueType.Raw32
                };
                Add(name, type, size, field.IsId);
            }
        }
        return result;
        void Add(string name, DbcValueType type, int size, bool id) { result.Add(new(result.Count, offset, size, name, type, id)); offset += size; }
    }

    public static DbcSchemaResolution ResolveFile(string definitionPath, int build, int actualFieldCount, int recordSize)
    {
        var definition = Load(definitionPath); var columns = ResolveColumns(definition, build);
        if (columns.Count != actualFieldCount) throw new InvalidDataException($"{definition.TableName}.dbd build {build:N0} resolves {columns.Count:N0} physical fields, but the client table declares {actualFieldCount:N0}.");
        var resolvedSize = columns.Count == 0 ? 0 : columns.Max(column => column.Offset + column.Size);
        if (resolvedSize != recordSize) throw new InvalidDataException($"{definition.TableName}.dbd build {build:N0} resolves a {resolvedSize:N0}-byte record, but the client table declares {recordSize:N0} bytes.");
        if (columns.Any(column => column.Size is not (1 or 2 or 4))) throw new NotSupportedException($"{definition.TableName}.dbd contains scalar widths outside the currently editable 8/16/32-bit WDB2 provider.");
        var key = columns.FirstOrDefault(column => column.IsIndex);
        return new(columns, DbcSchemaMatchKind.NamedMatch, columns.Count, key is null ? DbcRecordKeyStrategy.None : DbcRecordKeyStrategy.Physical(key.Index));
    }

    public static DbdSchemaAuditSummary Audit(string definitionsRoot, string dbcRoot, int build, string? xmlSchemaPath = null)
    {
        definitionsRoot = Path.GetFullPath(definitionsRoot); dbcRoot = Path.GetFullPath(dbcRoot);
        if (!Directory.Exists(definitionsRoot)) throw new DirectoryNotFoundException($"DBD definitions folder not found: {definitionsRoot}");
        if (!Directory.Exists(dbcRoot)) throw new DirectoryNotFoundException($"Client-table folder not found: {dbcRoot}");
        var xml = !string.IsNullOrWhiteSpace(xmlSchemaPath) && File.Exists(xmlSchemaPath) ? DbcSchemaCatalog.Load(xmlSchemaPath) : null; var rows = new List<DbdSchemaAuditRow>();
        var tablePaths = Directory.EnumerateFiles(dbcRoot, "*.dbc", SearchOption.TopDirectoryOnly).Concat(Directory.EnumerateFiles(dbcRoot, "*.db2", SearchOption.TopDirectoryOnly)).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (tablePaths.Length == 0)
        {
            var nested = Directory.EnumerateDirectories(dbcRoot).Select(directory => new
            {
                Path = directory,
                Count = Directory.EnumerateFiles(directory, "*.dbc", SearchOption.AllDirectories).Concat(Directory.EnumerateFiles(directory, "*.db2", SearchOption.AllDirectories)).Take(2).Count()
            }).Where(candidate => candidate.Count > 0).OrderByDescending(candidate => candidate.Count).ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            var hint = nested is null ? "No .dbc or .db2 files were found at that level." : $"No top-level .dbc or .db2 files were found. A likely table folder is '{nested.Path}'.";
            throw new InvalidDataException($"Schema audit requires a folder containing client tables directly. {hint}");
        }
        foreach (var dbcPath in tablePaths)
        {
            var table = Path.GetFileNameWithoutExtension(dbcPath); var dbdPath = Path.Combine(definitionsRoot, table + ".dbd");
            if (new FileInfo(dbcPath).Length == 0)
            {
                int? emptyDbdFields = null;
                try { if (File.Exists(dbdPath)) { var definition = Load(dbdPath); if (definition.ForBuild(build) is not null) emptyDbdFields = ResolveColumns(definition, build).Count; } }
                catch { /* The placeholder itself remains valid; a separate non-empty file would surface a definition error. */ }
                rows.Add(new(table, DbdAuditStatus.EmptyPlaceholder, 0, emptyDbdFields, null, "Intentional zero-byte placeholder; no client-table records exist to validate."));
                continue;
            }
            int actual;
            try { actual = WdbcFile.Load(dbcPath).FieldCount; }
            catch (Exception exception) { rows.Add(new(table, DbdAuditStatus.InvalidDbc, 0, null, null, exception.Message)); continue; }
            var xmlFields = xml?.ResolveColumns(table, actual).DefinedFieldCount;
            if (!File.Exists(dbdPath)) { rows.Add(new(table, DbdAuditStatus.MissingDefinition, actual, null, xmlFields, "No matching DBD file.")); continue; }
            try
            {
                var definition = Load(dbdPath); var layout = definition.ForBuild(build);
                if (layout is null) { rows.Add(new(table, DbdAuditStatus.MissingBuild, actual, null, xmlFields, $"DBD has no layout covering build {build:N0}.")); continue; }
                var dbdFields = ResolveColumns(definition, build).Count; var status = dbdFields == actual ? DbdAuditStatus.Match : DbdAuditStatus.FieldCountMismatch;
                var xmlNote = xmlFields is null ? string.Empty : $" XML={xmlFields:N0}.";
                rows.Add(new(table, status, actual, dbdFields, xmlFields, status == DbdAuditStatus.Match ? $"DBD and client table both expose {actual:N0} physical fields.{xmlNote}" : $"Client={actual:N0}, DBD={dbdFields:N0}.{xmlNote}"));
            }
            catch (Exception exception) { rows.Add(new(table, DbdAuditStatus.InvalidDefinition, actual, null, xmlFields, exception.Message)); }
        }
        return new(build, definitionsRoot, dbcRoot, rows);
    }

    private static DbdColumnDefinition ParseColumn(string line)
    {
        var content = SplitComment(line, out var comment); var match = ColumnLine().Match(content); if (!match.Success) throw new InvalidDataException($"Invalid DBD column line: {line}");
        var type = match.Groups["type"].Value.ToLowerInvariant() switch { "int" => DbdPrimitiveType.Int, "float" => DbdPrimitiveType.Float, "string" => DbdPrimitiveType.String, "locstring" => DbdPrimitiveType.LocString, _ => DbdPrimitiveType.Unknown };
        return new(match.Groups["name"].Value.TrimEnd('?'), type, Empty(match.Groups["reference"].Value), comment);
    }

    private static DbdField ParseField(string line)
    {
        var content = SplitComment(line, out _); var annotations = string.Empty;
        if (content.StartsWith('$')) { var end = content.IndexOf('$', 1); if (end < 0) throw new InvalidDataException($"Invalid DBD field annotation: {line}"); annotations = content[1..end]; content = content[(end + 1)..]; }
        var match = FieldLine().Match(content); if (!match.Success) throw new InvalidDataException($"Invalid DBD layout field: {line}");
        var widthText = match.Groups["width"].Value; var unsigned = widthText.StartsWith('u'); if (unsigned) widthText = widthText[1..]; var width = widthText.Length == 0 ? 32 : int.Parse(widthText, System.Globalization.CultureInfo.InvariantCulture);
        var array = match.Groups["array"].Success ? int.Parse(match.Groups["array"].Value, System.Globalization.CultureInfo.InvariantCulture) : 1;
        var flags = annotations.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new(match.Groups["name"].Value.TrimEnd('?'), array, width, unsigned, flags.Contains("id", StringComparer.OrdinalIgnoreCase), flags.Contains("noninline", StringComparer.OrdinalIgnoreCase), line);
    }

    private static IReadOnlyList<DbdBuildRange> ParseBuilds(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(token =>
    {
        var range = token.Split('-', 2, StringSplitOptions.TrimEntries); var start = ParseBuild(range[0]); var end = range.Length == 1 ? start : ParseBuild(range[1]); return start.CompareTo(end) <= 0 ? new DbdBuildRange(start, end, token) : new DbdBuildRange(end, start, token);
    }).ToArray();
    private static DbdClientBuild ParseBuild(string value)
    {
        var parts=value.Split('.',StringSplitOptions.RemoveEmptyEntries);if(parts.Length==1&&int.TryParse(parts[0],out var number))return DbdClientBuild.FromNumber(number);if(parts.Length!=4||!parts.All(part=>int.TryParse(part,out _)))throw new InvalidDataException($"Invalid DBD build token '{value}'.");return new(int.Parse(parts[0]),int.Parse(parts[1]),int.Parse(parts[2]),int.Parse(parts[3]));
    }
    private static string SplitComment(string value, out string? comment) { var index = value.IndexOf("//", StringComparison.Ordinal); comment = index < 0 ? null : value[(index + 2)..].Trim(); return (index < 0 ? value : value[..index]).Trim(); }
    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static string Locale(int index) => index switch { 0 => "enUS", 1 => "koKR", 2 => "frFR", 3 => "deDE", 4 => "zhCN", 5 => "zhTW", 6 => "esES", 7 => "esMX", 8 => "ruRU", 9 => "ptBR", 10 => "itIT", _ => $"Locale{index}" };

    [GeneratedRegex(@"^(?<type>[A-Za-z0-9_]+)(?:<(?<reference>[^>]+)>)?\s+(?<name>\S+)$", RegexOptions.CultureInvariant)] private static partial Regex ColumnLine();
    [GeneratedRegex(@"^(?<name>[^<\[]+)(?:<(?<width>u?\d+)>)?(?:\[(?<array>\d+)\])?$", RegexOptions.CultureInvariant)] private static partial Regex FieldLine();
}
