using System.Globalization;
using System.Text;
using System.Text.Json;
using MySqlConnector;

namespace WoWCrucible.Core;

public enum SqlExportFormat { Csv, JsonLines }
public sealed record SqlExportResult(string Path, string Table, long Rows, SqlExportFormat Format);
public sealed record SqlImportPlan(string Path, string Table, IReadOnlyList<string> Columns, long Rows, IReadOnlyList<string> Findings)
{
    public bool CanApply => Findings.Count == 0;
}

public static class SqlCsvCodec
{
    public const string NullToken = "\\N";

    public static string Encode(object? value)
    {
        var text = value switch
        {
            null => NullToken,
            byte[] bytes => "0x" + Convert.ToHexString(bytes),
            DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
        return text.IndexOfAny([',', '"', '\r', '\n']) < 0 ? text : $"\"{text.Replace("\"", "\"\"")}\"";
    }

    public static IEnumerable<IReadOnlyList<string?>> ReadRows(TextReader reader)
    {
        var row = new List<string?>(); var field = new StringBuilder(); var quoted = false; var any = false;
        while (reader.Read() is var raw && raw >= 0)
        {
            any = true; var character = (char)raw;
            if (quoted)
            {
                if (character == '"')
                {
                    if (reader.Peek() == '"') { reader.Read(); field.Append('"'); }
                    else quoted = false;
                }
                else field.Append(character);
                continue;
            }
            if (character == '"' && field.Length == 0) { quoted = true; continue; }
            if (character == ',') { row.Add(Decode(field)); field.Clear(); continue; }
            if (character is '\r' or '\n')
            {
                if (character == '\r' && reader.Peek() == '\n') reader.Read();
                row.Add(Decode(field)); field.Clear(); yield return row.ToArray(); row.Clear(); any = false; continue;
            }
            field.Append(character);
        }
        if (quoted) throw new InvalidDataException("CSV ended inside a quoted field.");
        if (any || field.Length > 0 || row.Count > 0) { row.Add(Decode(field)); yield return row.ToArray(); }
    }

    private static string? Decode(StringBuilder field) => field.ToString() == NullToken ? null : field.ToString();
}

public sealed class SqlTransferService
{
    public async Task<SqlExportResult> ExportTableAsync(DatabaseConnectionProfile profile, DatabaseTableCapability table, string outputPath,
        SqlExportFormat format, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(outputPath); Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        if (File.Exists(fullPath) && !overwrite) throw new IOException($"Output already exists: {fullPath}");
        var temporary = fullPath + $".{Guid.NewGuid():N}.tmp"; long rows = 0;
        try
        {
            await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
            await using var command = new MySqlCommand($"SELECT {string.Join(',', table.Columns.Select(column => ItemWritePlan.QuoteIdentifier(column.Name)))} FROM {ItemWritePlan.QuoteIdentifier(table.Name)}", connection) { CommandTimeout = 0 };
            await using var reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, cancellationToken);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 131072, leaveOpen: false))
            {
                if (format == SqlExportFormat.Csv) await writer.WriteLineAsync(string.Join(',', table.Columns.Select(column => SqlCsvCodec.Encode(column.Name))));
                while (await reader.ReadAsync(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (format == SqlExportFormat.Csv)
                        await writer.WriteLineAsync(string.Join(',', Enumerable.Range(0, reader.FieldCount).Select(index => SqlCsvCodec.Encode(reader.IsDBNull(index) ? null : reader.GetValue(index)))));
                    else
                    {
                        var value = Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, index => reader.IsDBNull(index) ? null : reader.GetValue(index), StringComparer.OrdinalIgnoreCase);
                        await writer.WriteLineAsync(JsonSerializer.Serialize(value));
                    }
                    rows++;
                }
                await writer.FlushAsync(cancellationToken);
            }
            File.Move(temporary, fullPath, overwrite); return new(fullPath, table.Name, rows, format);
        }
        catch { try { File.Delete(temporary); } catch { } throw; }
    }

    public SqlImportPlan AnalyzeCsv(string path, DatabaseTableCapability table)
    {
        var fullPath = Path.GetFullPath(path); var findings = new List<string>();
        using var reader = new StreamReader(fullPath, Encoding.UTF8, true); using var rows = SqlCsvCodec.ReadRows(reader).GetEnumerator();
        if (!rows.MoveNext()) return new(fullPath, table.Name, [], 0, ["CSV is empty and has no header row."]);
        var columns = rows.Current.Select(value => value?.Trim() ?? string.Empty).ToArray();
        if (columns.Any(string.IsNullOrWhiteSpace)) findings.Add("The header contains an empty column name.");
        var duplicates = columns.GroupBy(value => value, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        if (duplicates.Length > 0) findings.Add($"Duplicate header column(s): {string.Join(", ", duplicates)}.");
        var unknown = columns.Where(name => table.Find(name) is null).ToArray(); if (unknown.Length > 0) findings.Add($"Unknown table column(s): {string.Join(", ", unknown)}.");
        var generated = columns.Where(name => table.Find(name)?.Extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase) == true).ToArray(); if (generated.Length > 0) findings.Add($"Generated column(s) cannot be imported: {string.Join(", ", generated)}.");
        var required = table.Columns.Where(column => !column.Nullable && column.DefaultValue is null && !column.Extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase) && !column.Extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase) && !columns.Contains(column.Name, StringComparer.OrdinalIgnoreCase)).Select(column => column.Name).ToArray();
        if (required.Length > 0) findings.Add($"Required column(s) missing from header: {string.Join(", ", required)}.");
        long count = 0; while (rows.MoveNext()) { count++; if (rows.Current.Count != columns.Length && findings.Count < 20) findings.Add($"Row {count + 1:N0} has {rows.Current.Count} field(s); expected {columns.Length}."); }
        return new(fullPath, table.Name, columns, count, findings);
    }

    public async Task<long> ImportCsvAsync(DatabaseConnectionProfile profile, DatabaseTableCapability table, string path, CancellationToken cancellationToken = default)
    {
        var plan = AnalyzeCsv(path, table); if (!plan.CanApply) throw new InvalidOperationException(string.Join(" ", plan.Findings));
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var names = string.Join(',', plan.Columns.Select(ItemWritePlan.QuoteIdentifier)); var parameters = string.Join(',', plan.Columns.Select((_, index) => $"@v{index}"));
        await using var command = new MySqlCommand($"INSERT INTO {ItemWritePlan.QuoteIdentifier(table.Name)} ({names}) VALUES ({parameters})", connection, transaction) { CommandTimeout = 120 };
        for (var index = 0; index < plan.Columns.Count; index++) command.Parameters.Add(new MySqlParameter($"@v{index}", null));
        long inserted = 0;
        try
        {
            using var reader = new StreamReader(plan.Path, Encoding.UTF8, true); using var rows = SqlCsvCodec.ReadRows(reader).GetEnumerator(); rows.MoveNext();
            while (rows.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var index = 0; index < plan.Columns.Count; index++) command.Parameters[index].Value = (object?)rows.Current[index] ?? DBNull.Value;
                var affected = await command.ExecuteNonQueryAsync(cancellationToken); if (affected != 1) throw new InvalidOperationException($"Import row {inserted + 2:N0} affected {affected} rows instead of one."); inserted++;
            }
            await transaction.CommitAsync(cancellationToken); return inserted;
        }
        catch { try { await transaction.RollbackAsync(CancellationToken.None); } catch { } throw; }
    }
}
