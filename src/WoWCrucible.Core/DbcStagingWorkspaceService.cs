using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WoWCrucible.Core;

public sealed record DbcStagingWorkspaceInfo(
    string WorkspacePath,
    string Table,
    string SourcePath,
    string SourceContentSha256,
    string SchemaFingerprint,
    DbcSchemaMatchKind SchemaMatch,
    DbcRecordKeyStrategy KeyStrategy,
    int SourceRows,
    int Fields,
    DateTimeOffset CreatedUtc);

public sealed record DbcStagingCellChange(long StageId, int? SourceRow, uint? RecordKey, bool Appended, string Column, string Before, string After);

public sealed record DbcStagingDiff(
    int UpdatedRows,
    int AppendedRows,
    int DeletedRows,
    long ChangedCells,
    IReadOnlyList<DbcStagingCellChange> Changes,
    bool DetailsTruncated,
    IReadOnlyList<string> Findings)
{
    public bool HasChanges => UpdatedRows != 0 || AppendedRows != 0 || DeletedRows != 0 || ChangedCells != 0;
    public bool CanApply => DeletedRows == 0 && Findings.Count == 0;
}

public sealed record DbcStagingQueryResult(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, bool Truncated);
public sealed record DbcStagingMutationResult(int AffectedRows, bool Applied, DbcStagingDiff Diff);
public sealed record DbcStagingApplyResult(string OutputPath, DbcRowImportApplyResult Import, DbcStagingDiff Diff, string OutputSha256);

/// <summary>
/// A project-local, schema-bound SQLite workbench for bulk DBC edits. DBC remains the source artifact:
/// SQLite changes are converted back through DbcRowImportService's stale-safe preview before publication.
/// </summary>
public static class DbcStagingWorkspaceService
{
    public const string StageIdColumn = "__crucible_stage_id";
    public const string SourceRowColumn = "__crucible_source_row";
    public const string RecordKeyColumn = "__crucible_record_key";
    private const int FormatVersion = 1;

    public static DbcStagingWorkspaceInfo Create(string projectOrRoot, WdbcFile file, DbcSchemaResolution schema, bool replace = false,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file); ArgumentNullException.ThrowIfNull(schema);
        RequireExactSchema(file, schema);
        var projectPath = ResolveProjectPath(projectOrRoot); _ = CrucibleContentProjectService.Load(projectPath);
        var projectRoot = Path.GetDirectoryName(projectPath)!; var stagingRoot = Path.Combine(projectRoot, "Staging"); Directory.CreateDirectory(stagingRoot);
        var table = Path.GetFileNameWithoutExtension(file.SourcePath); var path = Path.Combine(stagingRoot, SafeFileName(table) + ".crucible.sqlite");
        if (File.Exists(path) && !replace) throw new IOException($"A staging workspace already exists for {table}: {path}. Reopen it or replace it explicitly.");
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            using var connection = Open(temporary, SqliteOpenMode.ReadWriteCreate);
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, "CREATE TABLE metadata (key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL)");
            Execute(connection, transaction, "CREATE TABLE columns (ordinal INTEGER PRIMARY KEY NOT NULL, physical_index INTEGER NOT NULL, name TEXT NOT NULL UNIQUE COLLATE NOCASE, value_type TEXT NOT NULL, storage_type TEXT NOT NULL)");
            var columnSql = string.Join(", ", schema.Columns.Select(column => $"{Q(column.Name)} {Storage(column.Type)}"));
            var internalSql = $"{Q(StageIdColumn)} INTEGER PRIMARY KEY, {Q(SourceRowColumn)} INTEGER UNIQUE, {Q(RecordKeyColumn)} INTEGER";
            Execute(connection, transaction, $"CREATE TABLE baseline ({internalSql}{(columnSql.Length == 0 ? string.Empty : ", " + columnSql)})");
            Execute(connection, transaction, $"CREATE TABLE working ({internalSql}{(columnSql.Length == 0 ? string.Empty : ", " + columnSql)})");
            Execute(connection, transaction, $"CREATE TABLE dirty_rows ({Q(StageIdColumn)} INTEGER PRIMARY KEY NOT NULL)");
            Execute(connection, transaction, $"CREATE INDEX working_record_key ON working ({Q(RecordKeyColumn)})");
            var fingerprint = SchemaFingerprint(schema); var created = DateTimeOffset.UtcNow;
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["format_version"] = FormatVersion.ToString(CultureInfo.InvariantCulture), ["table"] = table,
                ["source_path"] = Path.GetFullPath(file.SourcePath), ["source_content_sha256"] = file.ComputeContentSha256(),
                ["schema_fingerprint"] = fingerprint, ["schema_match"] = schema.MatchKind.ToString(),
                ["key_strategy"] = JsonSerializer.Serialize(schema.KeyStrategy), ["source_rows"] = file.RowCount.ToString(CultureInfo.InvariantCulture),
                ["fields"] = file.FieldCount.ToString(CultureInfo.InvariantCulture), ["created_utc"] = created.ToString("O", CultureInfo.InvariantCulture)
            };
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction; command.CommandText = "INSERT INTO metadata(key,value) VALUES($key,$value)";
                var key = command.Parameters.Add("$key", SqliteType.Text); var value = command.Parameters.Add("$value", SqliteType.Text);
                foreach (var pair in metadata) { key.Value = pair.Key; value.Value = pair.Value; command.ExecuteNonQuery(); }
            }
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction; command.CommandText = "INSERT INTO columns(ordinal,physical_index,name,value_type,storage_type) VALUES($ordinal,$physical,$name,$type,$storage)";
                var ordinal = command.Parameters.Add("$ordinal", SqliteType.Integer); var physical = command.Parameters.Add("$physical", SqliteType.Integer);
                var name = command.Parameters.Add("$name", SqliteType.Text); var type = command.Parameters.Add("$type", SqliteType.Text); var storage = command.Parameters.Add("$storage", SqliteType.Text);
                for (var index = 0; index < schema.Columns.Count; index++)
                {
                    var column = schema.Columns[index]; ordinal.Value = index; physical.Value = column.Index; name.Value = column.Name; type.Value = column.Type.ToString(); storage.Value = Storage(column.Type); command.ExecuteNonQuery();
                }
            }
            InsertSourceRows(connection, transaction, file, schema, progress, cancellationToken);
            Execute(connection, transaction, "CREATE TRIGGER baseline_no_insert BEFORE INSERT ON baseline BEGIN SELECT RAISE(ABORT, 'Crucible baseline is immutable'); END");
            Execute(connection, transaction, "CREATE TRIGGER baseline_no_update BEFORE UPDATE ON baseline BEGIN SELECT RAISE(ABORT, 'Crucible baseline is immutable'); END");
            Execute(connection, transaction, "CREATE TRIGGER baseline_no_delete BEFORE DELETE ON baseline BEGIN SELECT RAISE(ABORT, 'Crucible baseline is immutable'); END");
            Execute(connection, transaction, $"CREATE TRIGGER working_identity_no_update BEFORE UPDATE OF {Q(StageIdColumn)}, {Q(SourceRowColumn)}, {Q(RecordKeyColumn)} ON working WHEN OLD.{Q(SourceRowColumn)} IS NOT NULL BEGIN SELECT RAISE(ABORT, 'Imported Crucible row identity is immutable'); END");
            Execute(connection, transaction, "CREATE TRIGGER working_no_delete BEFORE DELETE ON working BEGIN SELECT RAISE(ABORT, 'Crucible staging does not publish DBC row deletion'); END");
            Execute(connection, transaction, $"CREATE TRIGGER working_track_insert AFTER INSERT ON working BEGIN INSERT OR IGNORE INTO dirty_rows({Q(StageIdColumn)}) VALUES(NEW.{Q(StageIdColumn)}); END");
            Execute(connection, transaction, $"CREATE TRIGGER working_track_update AFTER UPDATE ON working BEGIN INSERT OR IGNORE INTO dirty_rows({Q(StageIdColumn)}) VALUES(NEW.{Q(StageIdColumn)}); END");
            transaction.Commit(); connection.Close(); connection.Dispose();
            if (replace && File.Exists(path)) File.Copy(path, path + ".bak", true);
            File.Move(temporary, path, replace);
            return new(path, table, Path.GetFullPath(file.SourcePath), file.ComputeContentSha256(), fingerprint, schema.MatchKind, schema.KeyStrategy, file.RowCount, file.FieldCount, created);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static DbcStagingWorkspaceInfo Inspect(string workspacePath)
    {
        workspacePath = ExistingWorkspace(workspacePath);
        using var connection = Open(workspacePath, SqliteOpenMode.ReadOnly); return ReadInfo(connection, workspacePath);
    }

    public static DbcStagingDiff Diff(string workspacePath, int detailLimit = 500)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(detailLimit); workspacePath = ExistingWorkspace(workspacePath);
        using var connection = Open(workspacePath, SqliteOpenMode.ReadOnly); var info = ReadInfo(connection, workspacePath);
        return Analyze(connection, null, info, detailLimit);
    }

    public static DbcStagingQueryResult Query(string workspacePath, string sql, IReadOnlyDictionary<string, object?>? bindings = null, int limit = 500)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql); if (limit is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(limit), "Query limit must be 1–100000.");
        var verb = FirstToken(sql); if (verb is not ("SELECT" or "WITH" or "EXPLAIN")) throw new InvalidOperationException("Staging query is read-only. Use the explicit mutation preview/apply operation for UPDATE or INSERT.");
        workspacePath = ExistingWorkspace(workspacePath); using var connection = Open(workspacePath, SqliteOpenMode.ReadOnly); _ = ReadInfo(connection, workspacePath);
        using var command = connection.CreateCommand(); command.CommandText = sql; Bind(command, bindings);
        using var reader = command.ExecuteReader(); var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray(); var rows = new List<IReadOnlyList<object?>>(); var truncated = false;
        while (reader.Read())
        {
            if (rows.Count == limit) { truncated = true; break; }
            rows.Add(Enumerable.Range(0, reader.FieldCount).Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray());
        }
        return new(names, rows, truncated);
    }

    public static DbcStagingMutationResult Mutate(string workspacePath, string sql, IReadOnlyDictionary<string, object?>? bindings = null, bool apply = false, int detailLimit = 500)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql); var verb = FirstToken(sql);
        if (verb is not ("UPDATE" or "INSERT")) throw new InvalidOperationException("Staging mutation accepts one UPDATE or INSERT statement. Baseline rows and imported identities are immutable; DELETE is intentionally blocked.");
        if (ContainsMultipleStatements(sql)) throw new InvalidOperationException("Run one staging mutation statement at a time so every preview has an exact scope.");
        RequireWorkingMutation(sql, verb);
        workspacePath = ExistingWorkspace(workspacePath); using var connection = Open(workspacePath, SqliteOpenMode.ReadWrite); var info = ReadInfo(connection, workspacePath);
        using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; Bind(command, bindings);
        var affected = command.ExecuteNonQuery(); var diff = Analyze(connection, transaction, info, detailLimit); ValidateWorkingValues(connection, transaction, info);
        if (diff.DeletedRows != 0 || diff.Findings.Count != 0) throw new InvalidDataException(string.Join(" ", diff.Findings.Prepend($"The mutation would leave {diff.DeletedRows:N0} deleted source row(s).")));
        if (apply) transaction.Commit(); else transaction.Rollback();
        return new(affected, apply, diff);
    }

    public static DbcRowImportPlan PreviewApply(string workspacePath, WdbcFile file, DbcSchemaResolution schema, CancellationToken cancellationToken = default)
    {
        workspacePath = ExistingWorkspace(workspacePath); using var connection = Open(workspacePath, SqliteOpenMode.ReadOnly); var info = ReadInfo(connection, workspacePath);
        RequireBinding(info, file, schema); var diff = Analyze(connection, null, info, 0);
        if (!diff.CanApply) throw new InvalidDataException($"The staging workspace cannot be applied: {string.Join(" ", diff.Findings)} Deleted source rows: {diff.DeletedRows:N0}.");
        if (!diff.HasChanges) throw new InvalidOperationException("The staging workspace has no changes to apply.");
        var temporary = Path.Combine(Path.GetDirectoryName(workspacePath)!, $".{Path.GetFileName(workspacePath)}.{Guid.NewGuid():N}.json");
        try
        {
            WriteImport(connection, info, temporary, cancellationToken);
            return DbcRowImportService.Preview(file, schema, temporary, new(DbcRowImportFormat.Json, AllowAppend: true), cancellationToken: cancellationToken);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static DbcStagingApplyResult ApplyToOutput(string workspacePath, string sourceDbcPath, string schemaPath, string outputPath, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        sourceDbcPath = Path.GetFullPath(sourceDbcPath); var file = WdbcFile.Load(sourceDbcPath); var table = Path.GetFileNameWithoutExtension(sourceDbcPath); var schema = DbcSchemaCatalog.Load(schemaPath).ResolveColumns(table, file.FieldCount);
        return ApplyToOutput(workspacePath, file, schema, outputPath, overwrite, cancellationToken);
    }

    public static DbcStagingApplyResult ApplyToOutput(string workspacePath, WdbcFile source, DbcSchemaResolution schema, string outputPath, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(schema); outputPath = Path.GetFullPath(outputPath);
        if (File.Exists(outputPath) && !overwrite) throw new IOException($"Output DBC already exists: {outputPath}. Enable overwrite explicitly to replace it.");
        var file = source.CloneInMemory(); var diff = Diff(workspacePath, 500); var plan = PreviewApply(workspacePath, file, schema, cancellationToken); var result = DbcRowImportService.Apply(file, plan);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!); file.Save(outputPath, createBackup: overwrite);
        return new(outputPath, result, diff, HashFile(outputPath));
    }

    private static void InsertSourceRows(SqliteConnection connection, SqliteTransaction transaction, WdbcFile file, DbcSchemaResolution schema,
        IProgress<(int Done, int Total)>? progress, CancellationToken cancellationToken)
    {
        var names = new[] { StageIdColumn, SourceRowColumn, RecordKeyColumn }.Concat(schema.Columns.Select(column => column.Name)).ToArray();
        var parameters = Enumerable.Range(0, names.Length).Select(index => "$p" + index).ToArray();
        var insert = $"INSERT INTO baseline({string.Join(',', names.Select(Q))}) VALUES({string.Join(',', parameters)})";
        using var baseline = connection.CreateCommand(); baseline.Transaction = transaction; baseline.CommandText = insert;
        using var working = connection.CreateCommand(); working.Transaction = transaction; working.CommandText = insert.Replace("baseline", "working", StringComparison.Ordinal);
        for (var index = 0; index < names.Length; index++) { baseline.Parameters.Add(parameters[index], SqliteType.Text); working.Parameters.Add(parameters[index], SqliteType.Text); }
        for (var row = 0; row < file.RowCount; row++)
        {
            cancellationToken.ThrowIfCancellationRequested(); uint? key = schema.KeyStrategy.Kind == DbcRecordKeyKind.NoStableKey ? null : DbcRecordIdentity.GetKey(file, row, schema.Columns, schema.KeyStrategy);
            var values = new object?[names.Length]; values[0] = row + 1L; values[1] = row; values[2] = key is null ? null : (long)key.Value;
            for (var column = 0; column < schema.Columns.Count; column++) values[column + 3] = SqlValue(file.GetDisplayValue(row, schema.Columns[column]));
            Assign(baseline, values); baseline.ExecuteNonQuery(); Assign(working, values); working.ExecuteNonQuery(); progress?.Report((row + 1, file.RowCount));
        }
    }

    private static DbcStagingDiff Analyze(SqliteConnection connection, SqliteTransaction? transaction, DbcStagingWorkspaceInfo info, int detailLimit)
    {
        var columns = ReadColumns(connection, transaction); RequireStructure(connection, transaction, columns, info);
        var findings = new List<string>(); var changes = new List<DbcStagingCellChange>(); var updated = 0; var appended = 0; long changedCells = 0;
        var occupiedKeys = info.KeyStrategy.Kind == DbcRecordKeyKind.PhysicalColumn ? ReadBaselineKeys(connection, transaction) : []; var appendedKeys = new HashSet<uint>();
        var deleted = ScalarInt(connection, transaction, $"SELECT COUNT(*) FROM baseline b LEFT JOIN working w ON w.{Q(StageIdColumn)}=b.{Q(StageIdColumn)} WHERE w.{Q(StageIdColumn)} IS NULL");
        var select = new StringBuilder($"SELECT w.{Q(StageIdColumn)},w.{Q(SourceRowColumn)},w.{Q(RecordKeyColumn)},b.{Q(StageIdColumn)},b.{Q(SourceRowColumn)},b.{Q(RecordKeyColumn)}");
        foreach (var column in columns) select.Append($",w.{Q(column.Name)},b.{Q(column.Name)}");
        select.Append($" FROM dirty_rows d JOIN working w ON w.{Q(StageIdColumn)}=d.{Q(StageIdColumn)} LEFT JOIN baseline b ON b.{Q(StageIdColumn)}=w.{Q(StageIdColumn)} ORDER BY w.{Q(StageIdColumn)}");
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = select.ToString(); using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var stageId = reader.GetInt64(0); var sourceRow = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1); var workingKey = UInt(reader, 2); var existing = !reader.IsDBNull(3);
            if (!existing)
            {
                appended++; var resolvedKey = ResolveNewKey(reader, columns, info, workingKey, findings, stageId, 6);
                if (resolvedKey is { } key && (occupiedKeys.Contains(key) || !appendedKeys.Add(key))) findings.Add($"Stage row {stageId:N0} reuses existing or newly appended record key {key:N0}.");
                long rowChanges = 0;
                for (var index = 0; index < columns.Count; index++) if (!reader.IsDBNull(6 + index * 2)) { rowChanges++; AddDetail(stageId, null, resolvedKey, true, columns[index].Name, string.Empty, Display(reader.GetValue(6 + index * 2))); }
                changedCells += rowChanges; continue;
            }
            var baselineSource = reader.GetInt32(4); var baselineKey = UInt(reader, 5);
            if (sourceRow != baselineSource) findings.Add($"Imported stage row {stageId:N0} changed its source-row identity.");
            if (workingKey != baselineKey) findings.Add($"Imported stage row {stageId:N0} changed record key {baselineKey} to {workingKey}.");
            var rowChanged = false;
            for (var index = 0; index < columns.Count; index++)
            {
                var workingValue = reader.IsDBNull(6 + index * 2) ? null : reader.GetValue(6 + index * 2); var baselineValue = reader.IsDBNull(7 + index * 2) ? null : reader.GetValue(7 + index * 2);
                if (Equivalent(workingValue, baselineValue)) continue; rowChanged = true; changedCells++; AddDetail(stageId, baselineSource, baselineKey, false, columns[index].Name, Display(baselineValue), Display(workingValue));
            }
            if (rowChanged) updated++;
        }
        if (deleted != 0) findings.Add($"Working data is missing {deleted:N0} imported row(s); DBC row deletion is not published from staging.");
        return new(updated, appended, deleted, changedCells, changes, detailLimit >= 0 && changedCells > changes.Count, findings.Distinct().ToArray());

        void AddDetail(long stageId, int? sourceRow, uint? key, bool isAppend, string column, string before, string after)
        {
            if (changes.Count < detailLimit) changes.Add(new(stageId, sourceRow, key, isAppend, column, before, after));
        }
    }

    private static void WriteImport(SqliteConnection connection, DbcStagingWorkspaceInfo info, string path, CancellationToken cancellationToken)
    {
        var columns = ReadColumns(connection, null); var sql = new StringBuilder($"SELECT w.{Q(StageIdColumn)},w.{Q(SourceRowColumn)},w.{Q(RecordKeyColumn)},b.{Q(StageIdColumn)}");
        foreach (var column in columns) sql.Append($",w.{Q(column.Name)},b.{Q(column.Name)}");
        sql.Append($" FROM dirty_rows d JOIN working w ON w.{Q(StageIdColumn)}=d.{Q(StageIdColumn)} LEFT JOIN baseline b ON b.{Q(StageIdColumn)}=w.{Q(StageIdColumn)} ORDER BY w.{Q(StageIdColumn)}");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }); writer.WriteStartArray();
        using var command = connection.CreateCommand(); command.CommandText = sql.ToString(); using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested(); var existing = !reader.IsDBNull(3); var sourceRow = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1); var key = UInt(reader, 2);
            if (!existing) key = ResolveNewKey(reader, columns, info, key, [], reader.GetInt64(0), 4);
            var changed = !existing;
            if (existing) for (var index = 0; index < columns.Count; index++) if (!Equivalent(Value(reader, 4 + index * 2), Value(reader, 5 + index * 2))) { changed = true; break; }
            if (!changed) continue;
            writer.WriteStartObject(); if (info.KeyStrategy.Kind == DbcRecordKeyKind.NoStableKey) writer.WriteNumber("$rowIndex", sourceRow!.Value); else writer.WriteNumber("$recordKey", key!.Value);
            for (var index = 0; index < columns.Count; index++)
            {
                var working = Value(reader, 4 + index * 2); var baseline = Value(reader, 5 + index * 2); if (existing && Equivalent(working, baseline) || working is null) continue;
                WriteJsonValue(writer, columns[index].Name, working);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.Flush(); stream.Flush(true);
    }

    private static void ValidateWorkingValues(SqliteConnection connection, SqliteTransaction transaction, DbcStagingWorkspaceInfo info)
    {
        var columns = ReadColumns(connection, transaction); var physical = info.KeyStrategy.Kind == DbcRecordKeyKind.PhysicalColumn ? columns.FirstOrDefault(column => column.Ordinal == info.KeyStrategy.ColumnIndex) : null;
        var occupiedKeys = ReadBaselineKeys(connection, transaction); var newKeys = new HashSet<uint>();
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = $"SELECT w.{Q(StageIdColumn)},w.{Q(SourceRowColumn)},w.{Q(RecordKeyColumn)},{string.Join(',', columns.Select(column => "w." + Q(column.Name)))} FROM dirty_rows d JOIN working w ON w.{Q(StageIdColumn)}=d.{Q(StageIdColumn)}";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var stageId = reader.GetInt64(0); var existing = !reader.IsDBNull(1); var key = UInt(reader, 2);
            for (var index = 0; index < columns.Count; index++)
            {
                var value = Value(reader, index + 3); if (value is null) { if (existing) throw new InvalidDataException($"Stage row {stageId:N0}, {columns[index].Name} cannot be NULL."); continue; }
                ValidateValue(columns[index], value, stageId);
            }
            if (physical is not null)
            {
                var physicalValue = Value(reader, physical.Ordinal + 3); if (physicalValue is null) throw new InvalidDataException($"Stage row {stageId:N0} requires physical key {physical.Name}.");
                var physicalKey = ParseUInt(Display(physicalValue), $"stage row {stageId:N0}, {physical.Name}");
                if (key is not null && key != physicalKey) throw new InvalidDataException($"Stage row {stageId:N0} has {RecordKeyColumn}={key} but {physical.Name}={physicalKey}.");
                key = physicalKey;
            }
            if (!existing && key is { } newKey && (occupiedKeys.Contains(newKey) || !newKeys.Add(newKey))) throw new InvalidDataException($"New stage row {stageId:N0} collides with record key {newKey:N0}.");
        }
    }

    private static void RequireBinding(DbcStagingWorkspaceInfo info, WdbcFile file, DbcSchemaResolution schema)
    {
        RequireExactSchema(file, schema);
        if (!file.ComputeContentSha256().Equals(info.SourceContentSha256, StringComparison.Ordinal)) throw new InvalidOperationException("The source DBC no longer matches the staging baseline. Rebase or recreate the staging workspace before applying it.");
        if (!SchemaFingerprint(schema).Equals(info.SchemaFingerprint, StringComparison.Ordinal)) throw new InvalidOperationException("The selected DBC schema no longer matches the schema bound to this staging workspace.");
        if (!Path.GetFileNameWithoutExtension(file.SourcePath).Equals(info.Table, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"This workspace is bound to {info.Table}.dbc, not {Path.GetFileName(file.SourcePath)}.");
    }

    private static DbcStagingWorkspaceInfo ReadInfo(SqliteConnection connection, string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal); using var command = connection.CreateCommand(); command.CommandText = "SELECT key,value FROM metadata"; using var reader = command.ExecuteReader(); while (reader.Read()) values.Add(reader.GetString(0), reader.GetString(1));
        string Required(string key) => values.TryGetValue(key, out var value) ? value : throw new InvalidDataException($"Staging metadata '{key}' is missing.");
        if (int.Parse(Required("format_version"), CultureInfo.InvariantCulture) != FormatVersion) throw new InvalidDataException("Unsupported Crucible staging workspace format.");
        var keyStrategy = JsonSerializer.Deserialize<DbcRecordKeyStrategy>(Required("key_strategy")) ?? throw new InvalidDataException("Staging key strategy is invalid.");
        return new(path, Required("table"), Required("source_path"), Required("source_content_sha256"), Required("schema_fingerprint"), Enum.Parse<DbcSchemaMatchKind>(Required("schema_match")), keyStrategy,
            int.Parse(Required("source_rows"), CultureInfo.InvariantCulture), int.Parse(Required("fields"), CultureInfo.InvariantCulture), DateTimeOffset.Parse(Required("created_utc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    private sealed record StageColumn(int Ordinal, int PhysicalIndex, string Name, DbcValueType Type, string StorageType);
    private static IReadOnlyList<StageColumn> ReadColumns(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var result = new List<StageColumn>(); using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT ordinal,physical_index,name,value_type,storage_type FROM columns ORDER BY ordinal"; using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), Enum.Parse<DbcValueType>(reader.GetString(3)), reader.GetString(4))); return result;
    }

    private static void RequireStructure(SqliteConnection connection, SqliteTransaction? transaction, IReadOnlyList<StageColumn> columns, DbcStagingWorkspaceInfo info)
    {
        if (columns.Count != info.Fields || columns.Where((column, index) => column.Ordinal != index || column.StorageType != Storage(column.Type)).Any()) throw new InvalidDataException("Staging column metadata no longer matches the bound schema shape.");
        foreach (var table in new[] { "baseline", "working" })
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = $"PRAGMA table_info({Q(table)})"; using var reader = command.ExecuteReader(); var actual = new List<string>(); while (reader.Read()) actual.Add(reader.GetString(1));
            var expected = new[] { StageIdColumn, SourceRowColumn, RecordKeyColumn }.Concat(columns.Select(column => column.Name)).ToArray(); if (!actual.SequenceEqual(expected, StringComparer.Ordinal)) throw new InvalidDataException($"Staging {table} table shape was changed outside Crucible.");
        }
        using (var dirtyCommand = connection.CreateCommand())
        {
            dirtyCommand.Transaction = transaction; dirtyCommand.CommandText = $"PRAGMA table_info({Q("dirty_rows")})"; using var dirtyReader = dirtyCommand.ExecuteReader(); var dirtyColumns = new List<string>(); while (dirtyReader.Read()) dirtyColumns.Add(dirtyReader.GetString(1));
            if (!dirtyColumns.SequenceEqual([StageIdColumn], StringComparer.Ordinal)) throw new InvalidDataException("Staging dirty-row tracker shape was changed outside Crucible.");
        }
        if (ScalarInt(connection, transaction, $"SELECT COUNT(*) FROM dirty_rows d LEFT JOIN working w ON w.{Q(StageIdColumn)}=d.{Q(StageIdColumn)} WHERE w.{Q(StageIdColumn)} IS NULL") != 0) throw new InvalidDataException("Staging dirty-row tracker contains identities absent from working data.");
        using var triggerCommand = connection.CreateCommand(); triggerCommand.Transaction = transaction; triggerCommand.CommandText = "SELECT name FROM sqlite_master WHERE type='trigger'"; using var triggerReader = triggerCommand.ExecuteReader(); var triggers = new HashSet<string>(StringComparer.Ordinal); while (triggerReader.Read()) triggers.Add(triggerReader.GetString(0));
        var required = new[] { "baseline_no_insert", "baseline_no_update", "baseline_no_delete", "working_identity_no_update", "working_no_delete", "working_track_insert", "working_track_update" }; var missing = required.Where(name => !triggers.Contains(name)).ToArray(); if (missing.Length != 0) throw new InvalidDataException($"Crucible staging safety trigger(s) are missing: {string.Join(", ", missing)}.");
    }

    private static uint? ResolveNewKey(SqliteDataReader reader, IReadOnlyList<StageColumn> columns, DbcStagingWorkspaceInfo info, uint? stored, List<string> findings, long stageId, int pairStart)
    {
        if (info.KeyStrategy.Kind != DbcRecordKeyKind.PhysicalColumn) { findings.Add($"Stage row {stageId:N0} appends data, but {info.Table} does not have a proven physical key."); return stored; }
        var physical = columns.First(column => column.Ordinal == info.KeyStrategy.ColumnIndex); var value = Value(reader, pairStart + physical.Ordinal * 2);
        if (value is null) { findings.Add($"Stage row {stageId:N0} has no {physical.Name} value."); return stored; }
        var key = ParseUInt(Display(value), $"stage row {stageId:N0}, {physical.Name}"); if (stored is not null && stored != key) findings.Add($"Stage row {stageId:N0} has internal record key {stored} but {physical.Name}={key}."); return key;
    }

    private static void RequireExactSchema(WdbcFile file, DbcSchemaResolution schema)
    {
        if (schema.Columns.Count != file.FieldCount || schema.MatchKind is DbcSchemaMatchKind.FieldCountMismatchFallback or DbcSchemaMatchKind.MissingTableFallback) throw new InvalidDataException("Project staging requires an exact schema matching every physical DBC field.");
        var duplicate = schema.Columns.GroupBy(column => column.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1); if (duplicate is not null) throw new InvalidDataException($"DBC schema contains duplicate field '{duplicate.Key}'.");
        var reserved = schema.Columns.FirstOrDefault(column => column.Name is StageIdColumn or SourceRowColumn or RecordKeyColumn); if (reserved is not null) throw new InvalidDataException($"DBC schema field '{reserved.Name}' collides with a Crucible staging identity column.");
    }

    private static string SchemaFingerprint(DbcSchemaResolution schema)
    {
        var json = JsonSerializer.Serialize(new { schema.MatchKind, schema.DefinedFieldCount, schema.KeyStrategy, Columns = schema.Columns.Select(column => new { column.Index, column.Offset, column.Size, column.Name, column.Type, column.IsIndex }) });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static string ResolveProjectPath(string path) { path = Path.GetFullPath(path); return Directory.Exists(path) ? Path.Combine(path, "project.crucible.json") : path; }
    private static string ExistingWorkspace(string path) { path = Path.GetFullPath(path); return File.Exists(path) ? path : throw new FileNotFoundException("Crucible staging workspace does not exist.", path); }
    private static string SafeFileName(string value) { foreach (var invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_'); return value; }
    private static SqliteConnection Open(string path, SqliteOpenMode mode) { var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Cache = SqliteCacheMode.Private, Pooling = false }.ToString()); connection.Open(); using var pragma = connection.CreateCommand(); pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000"; pragma.ExecuteNonQuery(); return connection; }
    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql) { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; command.ExecuteNonQuery(); }
    private static int ScalarInt(SqliteConnection connection, SqliteTransaction? transaction, string sql) { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture); }
    private static string Q(string value) => '"' + value.Replace("\"", "\"\"") + '"';
    private static string Storage(DbcValueType type) => type switch { DbcValueType.StringOffset => "TEXT", DbcValueType.Float32 => "REAL", _ => "INTEGER" };
    private static object SqlValue(object value) => value is uint unsigned ? (long)unsigned : value;
    private static object? Value(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
    private static uint? UInt(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : checked((uint)reader.GetInt64(ordinal));
    private static string Display(object? value) => value switch { null => string.Empty, float number => number.ToString("R", CultureInfo.InvariantCulture), double number => number.ToString("R", CultureInfo.InvariantCulture), IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString() ?? string.Empty };
    private static bool Equivalent(object? left, object? right) => left is null || right is null ? left is null && right is null : left is double ld && right is double rd ? BitConverter.DoubleToInt64Bits(ld) == BitConverter.DoubleToInt64Bits(rd) : Equals(left, right);
    private static string FirstToken(string sql) => sql.TrimStart().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant();
    private static bool ContainsMultipleStatements(string sql) { var trimmed = sql.Trim(); var first = trimmed.IndexOf(';'); return first >= 0 && first != trimmed.Length - 1; }
    private static void RequireWorkingMutation(string sql, string verb)
    {
        var tokens = sql.Trim().TrimEnd(';').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries); var target = string.Empty;
        if (verb == "UPDATE")
        {
            if (tokens.Length > 1 && tokens[1].Equals("OR", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("UPDATE OR conflict modes are blocked because replacement semantics can erase a different staging row.");
            if (tokens.Length > 1) target = tokens[1];
        }
        else
        {
            if (tokens.Length < 3 || !tokens[1].Equals("INTO", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Use plain INSERT INTO working; conflict-replacement modes are blocked."); target = tokens[2];
        }
        target = target.Trim().Trim('"', '`', '[', ']'); if (!target.Equals("working", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Staging mutations may target only the working table. Metadata, schema, baseline, and dirty tracking are protected Crucible state.");
    }
    private static void Bind(SqliteCommand command, IReadOnlyDictionary<string, object?>? bindings) { if (bindings is null) return; foreach (var pair in bindings) command.Parameters.AddWithValue(pair.Key.StartsWith('$') || pair.Key.StartsWith('@') || pair.Key.StartsWith(':') ? pair.Key : "$" + pair.Key, pair.Value ?? DBNull.Value); }
    private static void Assign(SqliteCommand command, IReadOnlyList<object?> values) { for (var index = 0; index < values.Count; index++) command.Parameters[index].Value = values[index] ?? DBNull.Value; }
    private static HashSet<uint> ReadBaselineKeys(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var result = new HashSet<uint>(); using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = $"SELECT {Q(RecordKeyColumn)} FROM baseline WHERE {Q(RecordKeyColumn)} IS NOT NULL"; using var reader = command.ExecuteReader(); while (reader.Read()) result.Add(checked((uint)reader.GetInt64(0))); return result;
    }
    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static uint ParseUInt(string text, string context) => uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : throw new InvalidDataException($"{context} is not an unsigned 32-bit value: '{text}'.");

    private static void ValidateValue(StageColumn column, object value, long stageId)
    {
        var text = Display(value); var context = $"Stage row {stageId:N0}, {column.Name}";
        try
        {
            switch (column.Type)
            {
                case DbcValueType.Int32: _ = int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture); break;
                case DbcValueType.UInt32 or DbcValueType.Raw32: _ = uint.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture); break;
                case DbcValueType.Byte: _ = byte.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture); break;
                case DbcValueType.Float32: _ = float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture); break;
            }
        }
        catch (Exception exception) when (exception is FormatException or OverflowException) { throw new InvalidDataException($"{context} has invalid {column.Type} value '{text}'.", exception); }
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, string name, object value)
    {
        switch (value)
        {
            case long number: writer.WriteNumber(name, number); break;
            case int number: writer.WriteNumber(name, number); break;
            case double number: writer.WriteNumber(name, number); break;
            case float number: writer.WriteNumber(name, number); break;
            default: writer.WriteString(name, Display(value)); break;
        }
    }
}
