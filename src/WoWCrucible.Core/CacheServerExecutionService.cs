using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MySqlConnector;

namespace WoWCrucible.Core;

public enum CacheServerValueState { Null, Scalar, Binary }
public enum CacheServerRecordStatus { Ready, AlreadyApplied, MissingTarget, Blocked }

public sealed record CacheServerValue(CacheServerValueState State, string? Value)
{
    public static CacheServerValue Null { get; } = new(CacheServerValueState.Null, null);
}

public sealed record CacheServerTarget(string Host, uint Port, string User, string Database, string ServerVersion, string TableSchemaSha256);
public sealed record CacheServerLiveField(string SourceField, string TargetColumn, string SourceType, CacheServerValue Before, CacheServerValue After);
public sealed record CacheServerLiveRecord(
    uint RecordId,
    string TargetTable,
    string TargetKeyColumn,
    CacheServerRecordStatus Status,
    string Finding,
    IReadOnlyList<CacheServerLiveField> Fields,
    IReadOnlyList<string> UnmappedSourceFields);

public sealed record CacheServerExecutionPlan(
    string Format,
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    string SourcePath,
    string SourceSha256,
    string SourceDefinition,
    uint SourceBuild,
    CacheServerTarget Target,
    IReadOnlyList<CacheServerLiveRecord> Records,
    string ContentSha256,
    IReadOnlyList<string> Warnings)
{
    public int Ready => Records.Count(record => record.Status == CacheServerRecordStatus.Ready);
    public int AlreadyApplied => Records.Count(record => record.Status == CacheServerRecordStatus.AlreadyApplied);
    public int Missing => Records.Count(record => record.Status == CacheServerRecordStatus.MissingTarget);
    public int Blocked => Records.Count(record => record.Status == CacheServerRecordStatus.Blocked);

    public string PreviewSql()
    {
        var builder = new StringBuilder(); builder.AppendLine("-- WoW Crucible live cache-to-server preview");
        builder.AppendLine($"-- Target: {Target.User}@{Target.Host}:{Target.Port}/{Target.Database}");
        builder.AppendLine("-- Review only. Apply through Crucible for source/schema verification, row locks, transaction, receipt, and rollback.");
        foreach (var record in Records)
        {
            builder.AppendLine(); builder.AppendLine($"-- {record.Status}: {record.TargetTable} {record.RecordId} · {record.Finding}");
            if (record.Status != CacheServerRecordStatus.Ready || record.Fields.Count == 0) continue;
            builder.Append($"UPDATE {Quote(record.TargetTable)} SET {string.Join(",", record.Fields.Select(field => $"{Quote(field.TargetColumn)}={Literal(field.After)}"))} ");
            builder.Append($"WHERE {Quote(record.TargetKeyColumn)}={record.RecordId.ToString(CultureInfo.InvariantCulture)} AND ");
            builder.AppendLine(string.Join(" AND ", record.Fields.Select(field => $"{Quote(field.TargetColumn)} <=> {Literal(field.Before)}")) + ";");
        }
        return builder.ToString();
    }

    private static string Quote(string name) => ItemWritePlan.QuoteIdentifier(name);
    private static string Literal(CacheServerValue value) => value.State switch
    {
        CacheServerValueState.Null => "NULL",
        CacheServerValueState.Binary => $"X'{Convert.ToHexString(Convert.FromBase64String(value.Value ?? string.Empty))}'",
        CacheServerValueState.Scalar => $"CONVERT(X'{Convert.ToHexString(Encoding.UTF8.GetBytes(value.Value ?? string.Empty))}' USING utf8mb4)",
        _ => throw new InvalidDataException($"Unsupported cache server value state {value.State}.")
    };
}

public sealed record CacheServerReceipt(
    string Format,
    int FormatVersion,
    DateTimeOffset AppliedUtc,
    string PlanSha256,
    CacheServerTarget Target,
    IReadOnlyList<CacheServerLiveRecord> AppliedRecords,
    string ContentSha256,
    bool RolledBack = false,
    DateTimeOffset? RolledBackUtc = null);

public sealed record CacheServerApplyResult(string ReceiptPath, int UpdatedRecords, int UpdatedFields, int AlreadyAppliedRecords);
public sealed record CacheServerRollbackResult(string ReceiptPath, int RestoredRecords, int RestoredFields);

/// <summary>
/// Turns a schema-only cache mapping into an exact live-row diff, then applies or
/// rolls it back with row locks and typed preimage checks. Missing server templates
/// are never synthesized from incomplete client cache data.
/// </summary>
public sealed class CacheServerExecutionService
{
    public const string PlanFormat = "wow-crucible-cache-server-plan";
    public const string ReceiptFormat = "wow-crucible-cache-server-receipt";
    public const int PlanFormatVersion = 1;
    public const int ReceiptFormatVersion = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private static readonly JsonSerializerOptions HashJson = new(JsonSerializerDefaults.General) { WriteIndented = false, Converters = { new JsonStringEnumConverter() } };

    public async Task<CacheServerExecutionPlan> BuildAsync(WowCacheTable cache, DatabaseConnectionProfile profile, DatabaseCapabilities capabilities,
        IReadOnlyCollection<uint>? selectedIds = null, CancellationToken cancellationToken = default)
    {
        var mapped = CacheServerPlanService.Create(cache, capabilities, selectedIds);
        var targetTableName = mapped.Records.Select(record => record.TargetTable).Distinct(StringComparer.OrdinalIgnoreCase).Single();
        var targetTable = capabilities.FindTable(targetTableName) ?? throw new NotSupportedException($"The live schema no longer contains {targetTableName}.");
        var target = new CacheServerTarget(profile.Host, profile.Port, profile.User, profile.Database, capabilities.ServerVersion, CacheServerPlanService.SchemaFingerprint(targetTable));
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        var records = new List<CacheServerLiveRecord>(mapped.Records.Count);
        foreach (var record in mapped.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.Warnings.Count > 0 || record.Fields.Count == 0)
            {
                records.Add(new(record.RecordId, record.TargetTable, record.TargetKeyColumn, CacheServerRecordStatus.Blocked,
                    record.Warnings.Count > 0 ? string.Join("; ", record.Warnings) : "No proven writable fields exist for this cache record and live schema.", [], record.UnmappedSourceFields));
                continue;
            }
            var liveRows = await ReadRowsAsync(connection, null, profile.Database, record.TargetTable, record.TargetKeyColumn, record.RecordId,
                record.Fields.Select(field => field.TargetColumn).ToArray(), lockRow: false, cancellationToken);
            if (liveRows.Count == 0)
            {
                records.Add(new(record.RecordId, record.TargetTable, record.TargetKeyColumn, CacheServerRecordStatus.MissingTarget,
                    "No existing server template has this identity. Cache data is incomplete, so Crucible will not invent an INSERT.", [], record.UnmappedSourceFields));
                continue;
            }
            if (liveRows.Count != 1)
            {
                records.Add(new(record.RecordId, record.TargetTable, record.TargetKeyColumn, CacheServerRecordStatus.Blocked,
                    $"The declared identity unexpectedly matched {liveRows.Count:N0} rows.", [], record.UnmappedSourceFields));
                continue;
            }
            var live = liveRows[0]; var fields = record.Fields.Select(field => new CacheServerLiveField(field.SourceField, field.TargetColumn, field.SourceType,
                live[field.TargetColumn], Encode(field.Value))).Where(field => !Equal(field.Before, field.After)).ToArray();
            records.Add(new(record.RecordId, record.TargetTable, record.TargetKeyColumn,
                fields.Length == 0 ? CacheServerRecordStatus.AlreadyApplied : CacheServerRecordStatus.Ready,
                fields.Length == 0 ? "Every proven mapped field already equals the cache value." : $"{fields.Length:N0} proven field(s) differ from the live row; exact preimages were captured.", fields, record.UnmappedSourceFields));
        }
        var warnings = new[]
        {
            "Only existing server rows can be updated. Missing identities block the complete apply and are never inserted.",
            "Apply rechecks the source hash, target identity, table schema, and every changed field under row locks before committing.",
            "Unmapped client fields remain review evidence and are never silently projected into guessed server columns."
        };
        return CreatePlan(cache, target, records, warnings);
    }

    public async Task SavePlanAsync(CacheServerExecutionPlan plan, string path, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        ValidatePlan(plan); await WriteAtomicAsync(path, plan, overwrite, cancellationToken);
    }

    public async Task<CacheServerExecutionPlan> LoadPlanAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var plan = await JsonSerializer.DeserializeAsync<CacheServerExecutionPlan>(stream, Json, cancellationToken) ?? throw new InvalidDataException("Cache server plan is empty.");
        ValidatePlan(plan); return plan;
    }

    public async Task<CacheServerApplyResult> ApplyAsync(string planPath, DatabaseConnectionProfile profile, string receiptPath, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        var plan = await LoadPlanAsync(planPath, cancellationToken); ValidateTarget(plan.Target, profile);
        if (plan.Missing != 0 || plan.Blocked != 0) throw new InvalidOperationException($"Plan has {plan.Missing:N0} missing target(s) and {plan.Blocked:N0} blocked record(s). Nothing can be applied until the selection is clean.");
        if (!File.Exists(plan.SourcePath) || !string.Equals(await Sha256FileAsync(plan.SourcePath, cancellationToken), plan.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The source cache is missing or no longer matches the hash reviewed in this plan.");
        var receipt = Path.GetFullPath(receiptPath); if (File.Exists(receipt) && !overwrite) throw new IOException($"Receipt already exists: {receipt}");
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken); ValidateSchema(plan, capabilities);
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var applied = new List<CacheServerLiveRecord>(); var already = plan.AlreadyApplied; var fieldsChanged = 0; var committed = false; string? pendingReceipt = null;
        try
        {
            foreach (var record in plan.Records.Where(record => record.Status == CacheServerRecordStatus.Ready))
            {
                var liveRows = await ReadRowsAsync(connection, transaction, profile.Database, record.TargetTable, record.TargetKeyColumn, record.RecordId,
                    record.Fields.Select(field => field.TargetColumn).ToArray(), lockRow: true, cancellationToken);
                if (liveRows.Count != 1) throw new DBConcurrencyException($"Cache plan target {record.TargetTable} {record.RecordId:N0} is missing or non-unique.");
                var live = liveRows[0]; var divergent = record.Fields.Where(field => !Equal(live[field.TargetColumn], field.Before) && !Equal(live[field.TargetColumn], field.After)).Select(field => field.TargetColumn).ToArray();
                if (divergent.Length > 0) throw new DBConcurrencyException($"Cache plan is stale at {record.TargetTable} {record.RecordId:N0}: {string.Join(", ", divergent)} changed outside the reviewed preimage.");
                var pending = record.Fields.Where(field => Equal(live[field.TargetColumn], field.Before) && !Equal(field.Before, field.After)).ToArray();
                if (pending.Length == 0) { already++; continue; }
                await UpdateAsync(connection, transaction, profile.Database, record.TargetTable, record.TargetKeyColumn, record.RecordId, pending, useAfter: true, cancellationToken);
                applied.Add(record with { Fields = pending, Status = CacheServerRecordStatus.Ready, Finding = "Applied from the exact reviewed preimage." }); fieldsChanged += pending.Length;
            }
            var planSha = await Sha256FileAsync(planPath, cancellationToken); var receiptModel = CreateReceipt(planSha, plan.Target, applied);
            pendingReceipt = TemporaryFor(receipt); Directory.CreateDirectory(Path.GetDirectoryName(receipt)!);
            await File.WriteAllTextAsync(pendingReceipt, JsonSerializer.Serialize(receiptModel, Json) + Environment.NewLine, new UTF8Encoding(false), cancellationToken);
            await transaction.CommitAsync(cancellationToken); committed = true;
            try { File.Move(pendingReceipt, receipt, overwrite); pendingReceipt = null; }
            catch (Exception exception) { throw new IOException($"Cache updates committed, but the receipt rename failed. Recovery data remains at {pendingReceipt}; do not delete it.", exception); }
        }
        catch
        {
            if (!committed) { try { await transaction.RollbackAsync(CancellationToken.None); } catch { } if (pendingReceipt is not null && File.Exists(pendingReceipt)) File.Delete(pendingReceipt); }
            throw;
        }
        return new(receipt, applied.Count, fieldsChanged, already);
    }

    public async Task<CacheServerRollbackResult> RollbackAsync(string receiptPath, DatabaseConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(receiptPath); var receipt = await LoadReceiptAsync(path, cancellationToken); ValidateTarget(receipt.Target, profile);
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken);
        var table = receipt.AppliedRecords.FirstOrDefault()?.TargetTable;
        if (table is not null)
        {
            var current = capabilities.FindTable(table) ?? throw new DBConcurrencyException($"Rollback table {table} no longer exists.");
            if (!CacheServerPlanService.SchemaFingerprint(current).Equals(receipt.Target.TableSchemaSha256, StringComparison.OrdinalIgnoreCase)) throw new DBConcurrencyException("Target table schema changed after apply; rollback is refused.");
        }
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken); var restoredFields = 0;
        try
        {
            foreach (var record in receipt.AppliedRecords)
            {
                var liveRows = await ReadRowsAsync(connection, transaction, profile.Database, record.TargetTable, record.TargetKeyColumn, record.RecordId,
                    record.Fields.Select(field => field.TargetColumn).ToArray(), lockRow: true, cancellationToken);
                if (liveRows.Count != 1) throw new DBConcurrencyException($"Rollback target {record.TargetTable} {record.RecordId:N0} is missing or non-unique.");
                var live = liveRows[0]; var divergent = record.Fields.Where(field => !Equal(live[field.TargetColumn], field.After)).Select(field => field.TargetColumn).ToArray();
                if (divergent.Length > 0) throw new DBConcurrencyException($"Rollback refused because {record.TargetTable} {record.RecordId:N0} changed after apply: {string.Join(", ", divergent)}.");
                await UpdateAsync(connection, transaction, profile.Database, record.TargetTable, record.TargetKeyColumn, record.RecordId, record.Fields, useAfter: false, cancellationToken); restoredFields += record.Fields.Count;
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch { try { await transaction.RollbackAsync(CancellationToken.None); } catch { } throw; }
        var updated = receipt with { RolledBack = true, RolledBackUtc = DateTimeOffset.UtcNow }; updated = updated with { ContentSha256 = HashReceipt(updated.PlanSha256, updated.Target, updated.AppliedRecords, true, updated.RolledBackUtc) };
        await WriteAtomicAsync(path, updated, overwrite: true, cancellationToken);
        return new(path, receipt.AppliedRecords.Count, restoredFields);
    }

    public async Task<CacheServerReceipt> LoadReceiptAsync(string path, CancellationToken cancellationToken = default)
    {
        var receipt = JsonSerializer.Deserialize<CacheServerReceipt>(await File.ReadAllTextAsync(Path.GetFullPath(path), cancellationToken), Json) ?? throw new InvalidDataException("Cache server receipt is empty.");
        ValidateReceipt(receipt); return receipt;
    }

    internal static CacheServerExecutionPlan CreatePlan(WowCacheTable cache, CacheServerTarget target, IReadOnlyList<CacheServerLiveRecord> records, IReadOnlyList<string> warnings)
    {
        var hash = HashPlan(cache.SourcePath, cache.Sha256, cache.Definition?.Name ?? string.Empty, cache.Header.Build, target, records, warnings);
        return new(PlanFormat, PlanFormatVersion, DateTimeOffset.UtcNow, cache.SourcePath, cache.Sha256, cache.Definition?.Name ?? string.Empty, cache.Header.Build, target, records, hash, warnings);
    }

    internal static CacheServerReceipt CreateReceipt(string planSha256, CacheServerTarget target, IReadOnlyList<CacheServerLiveRecord> records)
    {
        var applied = DateTimeOffset.UtcNow; return new(ReceiptFormat, ReceiptFormatVersion, applied, planSha256, target, records, HashReceipt(planSha256, target, records, false, null));
    }

    internal static CacheServerValue Encode(object? value) => value switch
    {
        null or DBNull => CacheServerValue.Null,
        byte[] bytes => new(CacheServerValueState.Binary, Convert.ToBase64String(bytes)),
        DateTime date => new(CacheServerValueState.Scalar, date.ToString("O", CultureInfo.InvariantCulture)),
        DateTimeOffset date => new(CacheServerValueState.Scalar, date.ToString("O", CultureInfo.InvariantCulture)),
        TimeSpan span => new(CacheServerValueState.Scalar, span.ToString("c", CultureInfo.InvariantCulture)),
        IFormattable formatted => new(CacheServerValueState.Scalar, formatted.ToString(null, CultureInfo.InvariantCulture)),
        _ => new(CacheServerValueState.Scalar, Convert.ToString(value, CultureInfo.InvariantCulture))
    };
    internal static bool Equal(CacheServerValue left, CacheServerValue right) => left.State == right.State && (left.State == CacheServerValueState.Null || string.Equals(left.Value, right.Value, StringComparison.Ordinal));

    private static object? Decode(CacheServerValue value) => value.State switch
    {
        CacheServerValueState.Null => DBNull.Value,
        CacheServerValueState.Binary => Convert.FromBase64String(value.Value ?? string.Empty),
        CacheServerValueState.Scalar => value.Value,
        _ => throw new InvalidDataException($"Unsupported cache server value state {value.State}.")
    };

    private static async Task<IReadOnlyList<Dictionary<string, CacheServerValue>>> ReadRowsAsync(MySqlConnection connection, MySqlTransaction? transaction, string database,
        string table, string keyColumn, uint id, IReadOnlyList<string> fields, bool lockRow, CancellationToken cancellationToken)
    {
        var unique = fields.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); if (unique.Length == 0) return [];
        var sql = $"SELECT {string.Join(",", unique.Select(Quote))} FROM {Qualified(database, table)} WHERE {Quote(keyColumn)}=@id LIMIT 2{(lockRow ? " FOR UPDATE" : string.Empty)}";
        await using var command = new MySqlCommand(sql, connection, transaction); command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); var rows = new List<Dictionary<string, CacheServerValue>>(2);
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new Dictionary<string, CacheServerValue>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < unique.Length; index++) values[unique[index]] = Encode(reader.IsDBNull(index) ? null : reader.GetValue(index)); rows.Add(values);
        }
        return rows;
    }

    private static async Task UpdateAsync(MySqlConnection connection, MySqlTransaction transaction, string database, string table, string keyColumn, uint id,
        IReadOnlyList<CacheServerLiveField> fields, bool useAfter, CancellationToken cancellationToken)
    {
        if (fields.Count == 0) return;
        var sql = $"UPDATE {Qualified(database, table)} SET {string.Join(",", fields.Select((field, index) => $"{Quote(field.TargetColumn)}=@v{index}"))} WHERE {Quote(keyColumn)}=@id";
        await using var command = new MySqlCommand(sql, connection, transaction); for (var index = 0; index < fields.Count; index++) command.Parameters.AddWithValue($"@v{index}", Decode(useAfter ? fields[index].After : fields[index].Before)); command.Parameters.AddWithValue("@id", id);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken); if (affected != 1) throw new DBConcurrencyException($"Expected exactly one updated {table} row for {id:N0}; MySQL reported {affected}.");
    }

    private static void ValidatePlan(CacheServerExecutionPlan plan)
    {
        if (plan.Format != PlanFormat || plan.FormatVersion != PlanFormatVersion) throw new InvalidDataException($"Unsupported cache server plan {plan.Format} v{plan.FormatVersion}.");
        if (!HashPlan(plan.SourcePath, plan.SourceSha256, plan.SourceDefinition, plan.SourceBuild, plan.Target, plan.Records, plan.Warnings).Equals(plan.ContentSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Cache server plan content hash mismatch.");
    }
    private static void ValidateReceipt(CacheServerReceipt receipt)
    {
        if (receipt.Format != ReceiptFormat || receipt.FormatVersion != ReceiptFormatVersion) throw new InvalidDataException($"Unsupported cache server receipt {receipt.Format} v{receipt.FormatVersion}.");
        if (receipt.RolledBack) throw new InvalidDataException("This cache server receipt is already marked rolled back.");
        if (!HashReceipt(receipt.PlanSha256, receipt.Target, receipt.AppliedRecords, receipt.RolledBack, receipt.RolledBackUtc).Equals(receipt.ContentSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Cache server receipt content hash mismatch.");
    }
    private static void ValidateTarget(CacheServerTarget expected, DatabaseConnectionProfile actual)
    {
        if (!expected.Host.Equals(actual.Host, StringComparison.OrdinalIgnoreCase) || expected.Port != actual.Port || !expected.User.Equals(actual.User, StringComparison.OrdinalIgnoreCase) || !expected.Database.Equals(actual.Database, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Plan/receipt targets {expected.User}@{expected.Host}:{expected.Port}/{expected.Database}, not {actual.User}@{actual.Host}:{actual.Port}/{actual.Database}.");
    }
    private static void ValidateSchema(CacheServerExecutionPlan plan, DatabaseCapabilities capabilities)
    {
        var name = plan.Records.FirstOrDefault()?.TargetTable ?? throw new InvalidDataException("Cache server plan contains no records."); var table = capabilities.FindTable(name) ?? throw new DBConcurrencyException($"Target table {name} no longer exists.");
        if (!CacheServerPlanService.SchemaFingerprint(table).Equals(plan.Target.TableSchemaSha256, StringComparison.OrdinalIgnoreCase)) throw new DBConcurrencyException("Target table schema changed after review; rebuild the cache server plan.");
    }
    private static string HashPlan(string path, string sourceSha, string definition, uint build, CacheServerTarget target, IReadOnlyList<CacheServerLiveRecord> records, IReadOnlyList<string> warnings)
        => Hash(new { path, sourceSha, definition, build, target, records, warnings });
    private static string HashReceipt(string planSha, CacheServerTarget target, IReadOnlyList<CacheServerLiveRecord> records, bool rolledBack, DateTimeOffset? rolledBackUtc)
        => Hash(new { planSha, target, records, rolledBack, rolledBackUtc });
    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, HashJson)));
    private static string Quote(string name) => ItemWritePlan.QuoteIdentifier(name);
    private static string Qualified(string database, string table) => $"{Quote(database)}.{Quote(table)}";
    private static string TemporaryFor(string path) => Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
    private static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken) { await using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan); return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)); }
    private static async Task WriteAtomicAsync<T>(string path, T value, bool overwrite, CancellationToken cancellationToken)
    {
        path = Path.GetFullPath(path); if (File.Exists(path) && !overwrite) throw new IOException($"Output already exists: {path}"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = TemporaryFor(path);
        try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, Json) + Environment.NewLine, new UTF8Encoding(false), cancellationToken); File.Move(temporary, path, overwrite); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
