namespace WoWCrucible.Core;

public enum DbcBatchExportStatus { Exported, Skipped, Failed, Cancelled }

public sealed record DbcBatchExportItem(string SourcePath, string OutputPath, DbcBatchExportStatus Status, int Rows, string? Message);
public sealed record DbcBatchExportProgress(int CompletedFiles, int TotalFiles, string SourcePath, int CompletedRows, int TotalRows);
public sealed record DbcBatchExportResult(IReadOnlyList<DbcBatchExportItem> Items)
{
    public int Exported => Items.Count(item => item.Status == DbcBatchExportStatus.Exported);
    public int Skipped => Items.Count(item => item.Status == DbcBatchExportStatus.Skipped);
    public int Failed => Items.Count(item => item.Status == DbcBatchExportStatus.Failed);
    public int Cancelled => Items.Count(item => item.Status == DbcBatchExportStatus.Cancelled);
}

public static class DbcBatchExportService
{
    public static DbcBatchExportResult Export(IEnumerable<string> sourcePaths, DbcRowExportFormat format,
        Func<WdbcFile, DbcSchemaResolution> resolveSchema, IProgress<DbcBatchExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        ArgumentNullException.ThrowIfNull(resolveSchema);
        var extension = format switch
        {
            DbcRowExportFormat.Csv => ".csv",
            DbcRowExportFormat.Json => ".json",
            DbcRowExportFormat.JsonLines => ".jsonl",
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var sources = sourcePaths.Select(Path.GetFullPath).Distinct(comparer).ToArray();
        if (sources.Length == 0) throw new ArgumentException("Select at least one DBC or DB2 file.", nameof(sourcePaths));
        var outputs = sources.Select(path => Path.ChangeExtension(path, extension)).ToArray();
        // A same-named DBC and DB2 must not race for a single output, even within one batch.
        var collisions = outputs.GroupBy(path => path, comparer).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(comparer);
        var items = new List<DbcBatchExportItem>(sources.Length);
        for (var index = 0; index < sources.Length; index++)
        {
            var source = sources[index];
            var output = outputs[index];
            progress?.Report(new(index, sources.Length, source, 0, 0));
            if (cancellationToken.IsCancellationRequested)
                items.Add(new(source, output, DbcBatchExportStatus.Cancelled, 0, "Cancelled before export."));
            else if (collisions.Contains(output))
                items.Add(new(source, output, DbcBatchExportStatus.Failed, 0, "Multiple selected files have the same output name."));
            else if (!Path.GetExtension(source).Equals(".dbc", StringComparison.OrdinalIgnoreCase) && !Path.GetExtension(source).Equals(".db2", StringComparison.OrdinalIgnoreCase))
                items.Add(new(source, output, DbcBatchExportStatus.Failed, 0, "Only DBC and DB2 files can be converted by this command."));
            else if (File.Exists(output))
                items.Add(new(source, output, DbcBatchExportStatus.Skipped, 0, "Output already exists; it was not replaced."));
            else
            {
                try
                {
                    var file = WdbcFile.Load(source);
                    cancellationToken.ThrowIfCancellationRequested();
                    var schema = resolveSchema(file);
                    if (!schema.IsExactFor(file))
                        throw new InvalidDataException($"No matching definition for {file.LogicalTableName} ({file.FieldCount} fields, {file.RecordSize} bytes per record). Configure the matching XML or WoWDBDefs definitions in Workspace setup.");
                    var rowProgress = progress is null ? null : new RowProgress(progress, index, sources.Length, source);
                    var result = DbcRowExportService.Export(file, schema, output, new(format), rowProgress, cancellationToken);
                    items.Add(new(source, output, DbcBatchExportStatus.Exported, result.ExportedRows, null));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    items.Add(new(source, output, DbcBatchExportStatus.Cancelled, 0, "Cancelled; no partial output was kept."));
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    items.Add(new(source, output, DbcBatchExportStatus.Failed, 0, exception.Message));
                }
            }
            progress?.Report(new(index + 1, sources.Length, source, 0, 0));
        }
        return new(items);
    }

    private sealed class RowProgress(IProgress<DbcBatchExportProgress> target, int completedFiles, int totalFiles, string source) : IProgress<(int Done, int Total)>
    {
        private long _lastReport;
        public void Report((int Done, int Total) value)
        {
            var now = Environment.TickCount64;
            if (value.Done != value.Total && now - _lastReport < 100) return;
            _lastReport = now;
            target.Report(new(completedFiles, totalFiles, source, value.Done, value.Total));
        }
    }
}
