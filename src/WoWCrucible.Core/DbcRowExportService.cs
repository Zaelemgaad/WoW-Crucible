using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public enum DbcRowExportFormat { Csv, JsonLines, Json }

public sealed record DbcRowExportOptions(
    DbcRowExportFormat Format,
    IReadOnlyList<string>? Columns = null,
    IReadOnlyList<uint>? RecordKeys = null,
    bool RawStringOffsets = false,
    bool Overwrite = false);

public sealed record DbcRowExportResult(
    string OutputPath,
    DbcRowExportFormat Format,
    int ExportedRows,
    int SourceRows,
    IReadOnlyList<string> Columns,
    DbcRecordKeyStrategy KeyStrategy,
    DbcSchemaMatchKind SchemaMatch);

public sealed record DbcRowExportPreview(
    int MatchingRows,
    int SourceRows,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    DbcRecordKeyStrategy KeyStrategy,
    DbcSchemaMatchKind SchemaMatch);

public static class DbcRowExportService
{
    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    public static DbcRowExportResult Export(string dbcPath, string schemaPath, string outputPath, DbcRowExportOptions options,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var file = WdbcFile.Load(dbcPath); var table = Path.GetFileNameWithoutExtension(dbcPath);
        var schema = DbcSchemaCatalog.Load(schemaPath).ResolveColumns(table, file.FieldCount);
        return Export(file, schema, outputPath, options, progress, cancellationToken);
    }

    public static DbcRowExportResult Export(WdbcFile file, DbcSchemaResolution schema, string outputPath, DbcRowExportOptions options,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file); ArgumentNullException.ThrowIfNull(schema); ArgumentNullException.ThrowIfNull(options);
        var columns = ResolveColumns(schema.Columns, options.Columns); var rows = ResolveRows(file, schema, options.RecordKeys);
        var names = ExportNames(columns); outputPath = Path.GetFullPath(outputPath);
        if (File.Exists(outputPath) && !options.Overwrite) throw new IOException($"Export already exists: {outputPath}. Enable overwrite explicitly to replace it.");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? throw new InvalidOperationException("The export path has no containing directory."));
        var temporary = outputPath + $".crucible-{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan))
            {
                switch (options.Format)
                {
                    case DbcRowExportFormat.Csv: WriteCsv(stream); break;
                    case DbcRowExportFormat.JsonLines: WriteJsonLines(stream); break;
                    case DbcRowExportFormat.Json: WriteJson(stream); break;
                    default: throw new ArgumentOutOfRangeException(nameof(options.Format));
                }
                stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested(); File.Move(temporary, outputPath, options.Overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new(outputPath, options.Format, rows.Count, file.RowCount, names, schema.KeyStrategy, schema.MatchKind);

        void WriteCsv(Stream stream)
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16, leaveOpen: true) { NewLine = "\r\n" };
            writer.WriteLine(string.Join(',', names.Select(Csv)));
            for (var index = 0; index < rows.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteLine(string.Join(',', Values(file, schema, rows[index], columns, options.RawStringOffsets).Select(value => Csv(Invariant(value)))));
                progress?.Report((index + 1, rows.Count));
            }
            writer.Flush();
        }

        void WriteJsonLines(Stream stream)
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16, leaveOpen: true) { NewLine = "\n" };
            for (var index = 0; index < rows.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteLine(JsonSerializer.Serialize(Row(file, schema, rows[index], columns, options.RawStringOffsets), CompactJson));
                progress?.Report((index + 1, rows.Count));
            }
            writer.Flush();
        }

        void WriteJson(Stream stream)
        {
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }); writer.WriteStartArray();
            for (var index = 0; index < rows.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested(); JsonSerializer.Serialize(writer, Row(file, schema, rows[index], columns, options.RawStringOffsets), CompactJson);
                progress?.Report((index + 1, rows.Count));
            }
            writer.WriteEndArray(); writer.Flush();
        }
    }

    public static DbcRowExportPreview Preview(WdbcFile file, DbcSchemaResolution schema, IReadOnlyList<string>? requestedColumns = null,
        IReadOnlyList<uint>? recordKeys = null, bool rawStringOffsets = false, int limit = 25)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var columns = ResolveColumns(schema.Columns, requestedColumns); var rows = ResolveRows(file, schema, recordKeys);
        var preview = rows.Take(limit).Select(row => (IReadOnlyDictionary<string, object?>)Row(file, schema, row, columns, rawStringOffsets)).ToArray();
        return new(rows.Count, file.RowCount, ExportNames(columns), preview, schema.KeyStrategy, schema.MatchKind);
    }

    private static IReadOnlyList<DbcColumn> ResolveColumns(IReadOnlyList<DbcColumn> available, IReadOnlyList<string>? requested)
    {
        if (requested is null || requested.Count == 0) return available;
        var result = new List<DbcColumn>();
        foreach (var name in requested.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
            result.Add(available.FirstOrDefault(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"DBC export column '{name}' does not exist. Available columns: {string.Join(", ", available.Select(column => column.Name))}"));
        if (result.Count == 0) throw new InvalidOperationException("Select at least one DBC export column.");
        return result;
    }

    private static IReadOnlyList<int> ResolveRows(WdbcFile file, DbcSchemaResolution schema, IReadOnlyList<uint>? requested)
    {
        if (requested is null || requested.Count == 0) return Enumerable.Range(0, file.RowCount).ToArray();
        if (schema.KeyStrategy.Kind == DbcRecordKeyKind.NoStableKey)
            throw new InvalidOperationException("This DBC has no proven stable record key, so keyed export is blocked. Export all rows and use $rowIndex as an unstable locator.");
        var index = DbcRecordIdentity.IndexRows(file, schema.Columns, schema.KeyStrategy); var rows = new List<int>(); var missing = new List<uint>();
        foreach (var key in requested.Distinct()) if (index.TryGetValue(key, out var row)) rows.Add(row); else missing.Add(key);
        if (missing.Count > 0) throw new KeyNotFoundException($"DBC export key(s) not found: {string.Join(", ", missing)}");
        return rows;
    }

    private static Dictionary<string, object?> Row(WdbcFile file, DbcSchemaResolution schema, int row, IReadOnlyList<DbcColumn> columns, bool rawStrings)
    {
        var names = ExportNames(columns); var values = Values(file, schema, row, columns, rawStrings); var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var index = 0; index < names.Count; index++) result.Add(names[index], values[index]);
        return result;
    }

    private static IReadOnlyList<string> ExportNames(IReadOnlyList<DbcColumn> columns)
    {
        var names = new[] { "$recordKey", "$rowIndex" }.Concat(columns.Select(column => column.Name)).ToArray();
        var duplicate = names.GroupBy(name => name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"DBC schema contains duplicate export column '{duplicate.Key}'.");
        return names;
    }

    private static IReadOnlyList<object?> Values(WdbcFile file, DbcSchemaResolution schema, int row, IReadOnlyList<DbcColumn> columns, bool rawStrings)
    {
        object? key = schema.KeyStrategy.Kind == DbcRecordKeyKind.NoStableKey ? null : DbcRecordIdentity.GetKey(file, row, schema.Columns, schema.KeyStrategy);
        return new object?[] { key, row }.Concat(columns.Select(column => rawStrings && column.Type == DbcValueType.StringOffset ? file.GetRaw(row, column) : file.GetDisplayValue(row, column))).ToArray();
    }

    private static string Invariant(object? value) => value switch
    {
        null => string.Empty,
        float number => number.ToString("R", CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static string Csv(string value) => value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value : $"\"{value.Replace("\"", "\"\"")}\"";
}
