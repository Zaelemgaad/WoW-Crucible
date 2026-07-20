using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MySqlConnector;

namespace WoWCrucible.Core;

public enum DatabaseSyncOperationStatus { Ready, AlreadyApplied, Conflict, Blocked }

public sealed record DatabaseSyncTarget(string Host, uint Port, string User, string Database, string ServerVersion);
public sealed record DatabaseSyncField(string Column, LegacyDatabaseAuditValue Before, LegacyDatabaseAuditValue After);
public sealed record DatabaseSyncIdRemap(string Table, string Column, uint SourceId, uint TargetId, int RewrittenReferences);
public sealed record DatabaseSyncDependencyInclusion(
    string Relation,
    bool Declared,
    string SelectedIdentity,
    string IncludedIdentity,
    string SelectedEndpoint,
    string IncludedEndpoint,
    string MatchedValue,
    string Description);
public sealed record DatabaseSyncOperation(
    string Table,
    LegacyDatabaseContentDomain Domain,
    LegacyDatabaseRowChangeKind Kind,
    IReadOnlyList<LegacyDatabaseAuditKeyPart> Key,
    IReadOnlyList<DatabaseSyncField> Fields,
    DatabaseSyncOperationStatus Status,
    string Finding)
{
    public string Identity => $"{Table} · {string.Join(", ", Key.Select(part => $"{part.Column}={Display(part.Value)}"))}";
    private static string Display(LegacyDatabaseAuditValue value) => value.State switch
    {
        LegacyDatabaseAuditValueState.Null => "NULL",
        LegacyDatabaseAuditValueState.Binary => $"<binary {Convert.FromBase64String(value.Value ?? string.Empty).Length:N0} bytes>",
        _ => value.Value ?? value.State.ToString()
    };
}

public sealed record DatabaseSyncPlan(
    string Format,
    int FormatVersion,
    string ToolVersion,
    DateTimeOffset CreatedUtc,
    string SourceAuditSha256,
    DatabaseSyncTarget Target,
    bool RemovalsIncluded,
    IReadOnlyList<string> IncludePatterns,
    IReadOnlyList<DatabaseSyncIdRemap> IdRemaps,
    IReadOnlyList<DatabaseSyncOperation> Operations,
    string ContentSha256,
    IReadOnlyList<string> Warnings,
    bool DependencyClosureIncluded = false,
    IReadOnlyList<DatabaseSyncDependencyInclusion>? DependencyInclusions = null)
{
    public int Ready => Operations.Count(operation => operation.Status == DatabaseSyncOperationStatus.Ready);
    public int AlreadyApplied => Operations.Count(operation => operation.Status == DatabaseSyncOperationStatus.AlreadyApplied);
    public int Conflicts => Operations.Count(operation => operation.Status == DatabaseSyncOperationStatus.Conflict);
    public int Blocked => Operations.Count(operation => operation.Status == DatabaseSyncOperationStatus.Blocked);
}

public sealed record DatabaseSyncBuildOptions(
    IReadOnlyList<string>? IncludePatterns = null,
    bool IncludeRemovals = false,
    int MaximumOperations = 100_000,
    bool Overwrite = false,
    bool AutoRemapCollisions = false,
    uint? RemapStart = null,
    bool IncludeDependencyClosure = false);

public sealed record DatabaseSyncPlanResult(string Path, DatabaseSyncPlan Plan, long Bytes);
public sealed record DatabaseSyncApplyResult(string ReceiptPath, int Applied, int AlreadyApplied);
public sealed record DatabaseSyncReceipt(
    string Format,
    int FormatVersion,
    DateTimeOffset AppliedUtc,
    string PlanSha256,
    DatabaseSyncTarget Target,
    IReadOnlyList<DatabaseSyncOperation> AppliedOperations,
    string ApplyContentSha256,
    bool RolledBack = false,
    DateTimeOffset? RolledBackUtc = null);

/// <summary>
/// Builds and executes target-bound synchronization plans from verified baseline-to-edited database audits.
/// Plans never contain credentials. Every write is preceded by an exact primary-key lookup and typed preimage
/// comparison inside the same transaction; conflicts abort the complete apply before any commit.
/// </summary>
public sealed class DatabaseSynchronizationService
{
    public const string PlanFormat = "wow-crucible-database-sync-plan";
    public const string ReceiptFormat = "wow-crucible-database-sync-receipt";
    public const int FormatVersion = 3;
    public const int ReceiptFormatVersion = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly JsonSerializerOptions HashJson = new(JsonSerializerDefaults.General)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<DatabaseSyncPlanResult> BuildPlanAsync(
        string auditPath,
        DatabaseConnectionProfile target,
        string outputPath,
        DatabaseSyncBuildOptions? options = null,
        IProgress<(string Stage, string? Table, int Completed, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new();
        if (options.MaximumOperations is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(options), "Maximum operations must be from 1 through 1,000,000.");
        var output = Path.GetFullPath(outputPath); if (File.Exists(output) && !options.Overwrite) throw new IOException($"Synchronization plan already exists: {output}");
        var audit = Path.GetFullPath(auditPath); var auditService = new LegacyDatabaseAuditService();
        progress?.Report(("Verifying recovery audit", null, 0, 0));
        var inspection = await auditService.InspectAsync(audit, verifyChanges: true, cancellationToken);
        var manifest = inspection.Valid && inspection.Manifest is not null ? inspection.Manifest : throw new InvalidDataException($"Recovery audit validation failed: {string.Join("; ", inspection.Findings)}");
        if (manifest.Mode != LegacyDatabaseAuditMode.BaselineCompared || manifest.Baseline is null)
            throw new InvalidDataException("Synchronization requires a baseline-compared audit. Unattributed rows cannot prove a safe target preimage.");
        if (manifest.BaselineIdentity != LegacyDatabaseBaselineIdentity.MatchingCoreIdentity)
            throw new InvalidDataException($"Synchronization requires matching baseline/edited core identity; this audit reports {manifest.BaselineIdentity}.");
        var patterns = (options.IncludePatterns ?? []).Select(pattern => pattern.Trim()).Where(pattern => pattern.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var eligibleTables = manifest.Tables.Where(table => table.DataEntry is not null && table.Status is LegacyDatabaseTableAuditStatus.Changed or LegacyDatabaseTableAuditStatus.LegacyTableOnly or LegacyDatabaseTableAuditStatus.BaselineTableOnly).ToArray();
        var selectedTables = eligibleTables.Where(table => patterns.Length == 0 || patterns.Any(pattern => LegacyDatabaseSnapshotService.GlobMatches(table.Name, pattern))).ToArray();
        if (selectedTables.Length == 0) throw new InvalidOperationException("The synchronization selection contains no row-level comparable changed tables.");

        var capabilities = await new DatabaseCapabilityService().InspectAsync(target, cancellationToken);
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(target)); await connection.OpenAsync(cancellationToken);
        var scannedTables = options.IncludeDependencyClosure && patterns.Length > 0 ? eligibleTables : selectedTables;
        var candidateOperations = new List<DatabaseSyncOperation>();
        for (var tableIndex = 0; tableIndex < scannedTables.Length; tableIndex++)
        {
            var table = scannedTables[tableIndex]; progress?.Report((options.IncludeDependencyClosure ? "Reading dependency candidates" : "Comparing target rows", table.Name, tableIndex, scannedTables.Length));
            await foreach (var change in auditService.ReadChangesAsync(audit, table.Name, cancellationToken))
            {
                if (change.Kind == LegacyDatabaseRowChangeKind.Removed && !options.IncludeRemovals) continue;
                if (change.Kind == LegacyDatabaseRowChangeKind.UnattributedCandidate) continue;
                if (candidateOperations.Count >= options.MaximumOperations) throw new InvalidOperationException($"The {(options.IncludeDependencyClosure ? "dependency candidate scan" : "plan")} exceeded the explicit {options.MaximumOperations:N0}-operation safety limit. Narrow the table selection or raise the limit deliberately.");
                candidateOperations.Add(FromAuditChange(change));
            }
            progress?.Report((options.IncludeDependencyClosure ? "Read dependency candidates" : "Compared target rows", table.Name, tableIndex + 1, scannedTables.Length));
        }
        var seedTableNames = selectedTables.Select(table => table.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seeds = candidateOperations.Where(operation => seedTableNames.Contains(operation.Table)).ToArray();
        IReadOnlyList<DatabaseSyncOperation> sourceOperations = seeds; IReadOnlyList<DatabaseSyncDependencyInclusion> dependencyInclusions = [];
        if (options.IncludeDependencyClosure && patterns.Length > 0)
        {
            var expanded = ExpandDependencyClosure(seeds, candidateOperations, capabilities.Relationships, options.MaximumOperations, cancellationToken);
            sourceOperations = expanded.Operations; dependencyInclusions = expanded.Inclusions;
        }
        var preliminary = new List<DatabaseSyncOperation>(sourceOperations.Count);
        foreach (var operation in sourceOperations) preliminary.Add(await AnalyzeAsync(connection, null, target, capabilities, operation, lockRow: false, cancellationToken));
        IReadOnlyList<DatabaseSyncIdRemap> remaps = [];
        IReadOnlyList<DatabaseSyncOperation> rewritten = sourceOperations;
        if (options.AutoRemapCollisions)
        {
            var allocated = await AllocateRemapsAsync(connection, target, capabilities, sourceOperations, preliminary, options.RemapStart, cancellationToken);
            rewritten = RewriteRemappedReferences(sourceOperations, capabilities, allocated, out remaps);
        }
        var analyzed = new List<DatabaseSyncOperation>(rewritten.Count);
        if (remaps.Count == 0) analyzed.AddRange(preliminary); else foreach (var operation in rewritten) analyzed.Add(await AnalyzeAsync(connection, null, target, capabilities, operation, lockRow: false, cancellationToken));
        var ordered = OrderOperations(analyzed, capabilities).ToArray();
        var warnings = new List<string>
        {
            "Rows marked Conflict or Blocked are never written. Apply refuses the complete plan while either status remains.",
            options.IncludeRemovals ? "Explicit removals are included and require exact full-row preimages before DELETE." : "Source removals are excluded. Crucible never carries deletions into a target implicitly."
        };
        if (remaps.Count > 0) warnings.Add($"Automatically allocated {remaps.Count:N0} collision-free ID remap(s) above live target maxima and rewrote {remaps.Sum(remap => remap.RewrittenReferences):N0} recognized selected reference(s). Review every mapping before apply.");
        if (options.IncludeDependencyClosure)
            warnings.Add(patterns.Length == 0
                ? "Dependency closure was enabled, but no table filter was supplied; every eligible changed row was already selected."
                : $"Dependency closure added {dependencyInclusions.Count:N0} exactly related changed row(s) through declared or named core relationships. Review every recorded causal edge before apply.");
        if (manifest.Warnings.Count > 0) warnings.AddRange(manifest.Warnings.Select(warning => $"Source audit: {warning}"));
        var sourceAuditSha256 = await Sha256FileAsync(audit, cancellationToken); var binding = new DatabaseSyncTarget(target.Host, target.Port, target.User, target.Database, capabilities.ServerVersion);
        var plan = new DatabaseSyncPlan(PlanFormat, FormatVersion, ToolVersion(), DateTimeOffset.UtcNow, sourceAuditSha256,
            binding, options.IncludeRemovals, patterns, remaps, ordered,
            HashPlanContent(sourceAuditSha256, binding, options.IncludeRemovals, patterns, remaps, ordered, options.IncludeDependencyClosure, dependencyInclusions), warnings,
            options.IncludeDependencyClosure, dependencyInclusions);
        await WriteAtomicAsync(output, plan, options.Overwrite, cancellationToken);
        return new(output, plan, new FileInfo(output).Length);
    }

    public async Task<DatabaseSyncPlan> LoadPlanAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var plan = await JsonSerializer.DeserializeAsync<DatabaseSyncPlan>(stream, Json, cancellationToken) ?? throw new InvalidDataException("Synchronization plan is empty.");
        if (plan.Format != PlanFormat || plan.FormatVersion is not 2 and not FormatVersion) throw new InvalidDataException($"Unsupported synchronization plan format {plan.Format} v{plan.FormatVersion}.");
        var contentHash = plan.FormatVersion == 2
            ? HashPlanContentV2(plan.SourceAuditSha256, plan.Target, plan.RemovalsIncluded, plan.IncludePatterns, plan.IdRemaps, plan.Operations)
            : HashPlanContent(plan.SourceAuditSha256, plan.Target, plan.RemovalsIncluded, plan.IncludePatterns, plan.IdRemaps, plan.Operations, plan.DependencyClosureIncluded, plan.DependencyInclusions ?? []);
        if (!contentHash.Equals(plan.ContentSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Synchronization plan content hash mismatch.");
        return plan;
    }

    public static (IReadOnlyList<DatabaseSyncOperation> Operations, IReadOnlyList<DatabaseSyncDependencyInclusion> Inclusions) ExpandDependencyClosure(
        IReadOnlyList<DatabaseSyncOperation> seeds,
        IReadOnlyList<DatabaseSyncOperation> candidates,
        IReadOnlyList<DatabaseRelationCapability> relationships,
        int maximumOperations = 100_000,
        CancellationToken cancellationToken = default)
    {
        if (maximumOperations is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maximumOperations));
        if (seeds.Count > maximumOperations) throw new InvalidOperationException($"The seed selection exceeds the {maximumOperations:N0}-operation safety limit.");
        var uniqueCandidates = candidates.GroupBy(OperationIdentity, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
        var byIdentity = uniqueCandidates.Select((operation, index) => (Operation: operation, Index: index)).ToDictionary(value => OperationIdentity(value.Operation), value => value.Index, StringComparer.OrdinalIgnoreCase);
        var candidateIndicesByTable = uniqueCandidates.Select((operation, index) => (operation.Table, Index: index)).GroupBy(value => value.Table, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(value => value.Index).ToArray(), StringComparer.OrdinalIgnoreCase);
        var endpointColumns = relationships.SelectMany(relation => new[] { (relation.FromTable, relation.FromColumn), (relation.ToTable, relation.ToColumn) })
            .Distinct(new TableColumnComparer()).ToArray();
        var endpointIndex = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var endpoint in endpointColumns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!candidateIndicesByTable.TryGetValue(endpoint.Item1, out var tableIndices)) continue;
            foreach (var index in tableIndices)
            {
                var operation = uniqueCandidates[index];
                foreach (var value in RelationValues(operation, endpoint.Item2))
                {
                    var key = EndpointKey(endpoint.Item1, endpoint.Item2, value); if (!endpointIndex.TryGetValue(key, out var matches)) endpointIndex[key] = matches = []; matches.Add(index);
                }
            }
        }
        var relationshipsByTable = relationships.SelectMany(relation => new[] { (Table: relation.FromTable, Relation: relation), (Table: relation.ToTable, Relation: relation) })
            .GroupBy(value => value.Table, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Select(value => value.Relation).Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
        var selected = new HashSet<int>(); var queue = new Queue<int>();
        foreach (var seed in seeds)
        {
            if (!byIdentity.TryGetValue(OperationIdentity(seed), out var index)) continue;
            if (selected.Add(index)) queue.Enqueue(index);
        }
        var inclusions = new List<DatabaseSyncDependencyInclusion>();
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested(); var selectedIndex = queue.Dequeue(); var selectedOperation = uniqueCandidates[selectedIndex];
            if (!relationshipsByTable.TryGetValue(selectedOperation.Table, out var touchingRelationships)) continue;
            foreach (var relation in touchingRelationships)
            {
                var selectedIsFrom = relation.FromTable.Equals(selectedOperation.Table, StringComparison.OrdinalIgnoreCase);
                var selectedTable = selectedIsFrom ? relation.FromTable : relation.ToTable; var selectedColumn = selectedIsFrom ? relation.FromColumn : relation.ToColumn;
                var includedTable = selectedIsFrom ? relation.ToTable : relation.FromTable; var includedColumn = selectedIsFrom ? relation.ToColumn : relation.FromColumn;
                foreach (var value in RelationValues(selectedOperation, selectedColumn))
                {
                    if (!endpointIndex.TryGetValue(EndpointKey(includedTable, includedColumn, value), out var matches)) continue;
                    foreach (var match in matches)
                    {
                        if (match == selectedIndex || !selected.Add(match)) continue;
                        if (selected.Count > maximumOperations) throw new InvalidOperationException($"Dependency closure exceeded the explicit {maximumOperations:N0}-operation safety limit.");
                        var included = uniqueCandidates[match]; queue.Enqueue(match);
                        inclusions.Add(new(relation.Name, relation.Declared, selectedOperation.Identity, included.Identity,
                            $"{selectedTable}.{selectedColumn}", $"{includedTable}.{includedColumn}", DisplayRelationValue(value), relation.Description));
                    }
                }
            }
        }
        var ordered = selected.Select(index => uniqueCandidates[index]).OrderBy(operation => operation.Table, StringComparer.OrdinalIgnoreCase).ThenBy(operation => operation.Identity, StringComparer.Ordinal).ToArray();
        return (ordered, inclusions);
    }

    public async Task<DatabaseSyncApplyResult> ApplyAsync(string planPath, DatabaseConnectionProfile target, string receiptPath, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        var plan = await LoadPlanAsync(planPath, cancellationToken); ValidateTarget(plan.Target, target);
        if (plan.Conflicts != 0 || plan.Blocked != 0) throw new InvalidOperationException($"Plan has {plan.Conflicts:N0} conflict(s) and {plan.Blocked:N0} blocked operation(s). Narrow or repair the plan before applying anything.");
        var receipt = Path.GetFullPath(receiptPath); if (File.Exists(receipt) && !overwrite) throw new IOException($"Receipt already exists: {receipt}");
        var capabilities = await new DatabaseCapabilityService().InspectAsync(target, cancellationToken);
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(target)); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var applied = new List<DatabaseSyncOperation>(); var already = 0; var committed = false; string? pendingReceipt = null;
        try
        {
            foreach (var planned in OrderOperations(plan.Operations.Where(operation => operation.Status is DatabaseSyncOperationStatus.Ready or DatabaseSyncOperationStatus.AlreadyApplied), capabilities))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var live = await AnalyzeAsync(connection, transaction, target, capabilities, planned with { Status = DatabaseSyncOperationStatus.Ready, Finding = string.Empty }, lockRow: true, cancellationToken);
                if (live.Status == DatabaseSyncOperationStatus.AlreadyApplied) { already++; continue; }
                if (live.Status != DatabaseSyncOperationStatus.Ready) throw new DBConcurrencyException($"Stale synchronization plan at {planned.Identity}: {live.Finding}");
                await ExecuteAsync(connection, transaction, target, capabilities, live, cancellationToken); applied.Add(live);
            }
            var planSha256 = await Sha256FileAsync(planPath, cancellationToken); var receiptModel = new DatabaseSyncReceipt(ReceiptFormat, ReceiptFormatVersion, DateTimeOffset.UtcNow, planSha256, plan.Target, applied, HashReceiptContent(planSha256, plan.Target, applied));
            pendingReceipt = TemporaryFor(receipt); Directory.CreateDirectory(Path.GetDirectoryName(receipt)!);
            await File.WriteAllTextAsync(pendingReceipt, JsonSerializer.Serialize(receiptModel, Json) + Environment.NewLine, new UTF8Encoding(false), cancellationToken);
            await transaction.CommitAsync(cancellationToken); committed = true;
            try { File.Move(pendingReceipt, receipt, overwrite); pendingReceipt = null; }
            catch (Exception exception) { throw new IOException($"Database synchronization committed, but the final receipt rename failed. The complete recovery receipt is preserved at {pendingReceipt}; do not delete it.", exception); }
        }
        catch
        {
            if (!committed)
            {
                try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
                if (pendingReceipt is not null && File.Exists(pendingReceipt)) File.Delete(pendingReceipt);
            }
            throw;
        }
        return new(receipt, applied.Count, already);
    }

    public async Task<DatabaseSyncApplyResult> RollbackAsync(string receiptPath, DatabaseConnectionProfile target, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(receiptPath); var model = JsonSerializer.Deserialize<DatabaseSyncReceipt>(await File.ReadAllTextAsync(path, cancellationToken), Json) ?? throw new InvalidDataException("Synchronization receipt is empty.");
        if (model.Format != ReceiptFormat || model.FormatVersion != ReceiptFormatVersion || model.RolledBack) throw new InvalidDataException(model.RolledBack ? "This synchronization receipt is already marked rolled back." : "Unsupported synchronization receipt.");
        if (!HashReceiptContent(model.PlanSha256, model.Target, model.AppliedOperations).Equals(model.ApplyContentSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Synchronization receipt content hash mismatch.");
        ValidateTarget(model.Target, target); var capabilities = await new DatabaseCapabilityService().InspectAsync(target, cancellationToken);
        var reverse = model.AppliedOperations.Reverse().Select(Reverse).ToArray();
        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(target)); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken); var applied = new List<DatabaseSyncOperation>(); var already = 0;
        try
        {
            foreach (var planned in reverse)
            {
                var live = await AnalyzeAsync(connection, transaction, target, capabilities, planned, lockRow: true, cancellationToken);
                if (live.Status == DatabaseSyncOperationStatus.AlreadyApplied) { already++; continue; }
                if (live.Status != DatabaseSyncOperationStatus.Ready) throw new DBConcurrencyException($"Rollback is stale at {planned.Identity}: {live.Finding}");
                await ExecuteAsync(connection, transaction, target, capabilities, live, cancellationToken); applied.Add(live);
            }
            await transaction.CommitAsync(cancellationToken);
            var completed = model with { RolledBack = true, RolledBackUtc = DateTimeOffset.UtcNow };
            await WriteAtomicAsync(path, completed, overwrite: true, cancellationToken);
        }
        catch { try { await transaction.RollbackAsync(CancellationToken.None); } catch { } throw; }
        return new(path, applied.Count, already);
    }

    public string PreviewSql(DatabaseSyncPlan plan)
    {
        var builder = new StringBuilder(); builder.AppendLine("-- WoW Crucible target-bound database synchronization preview"); builder.AppendLine($"-- Target: {plan.Target.User}@{plan.Target.Host}:{plan.Target.Port}/{plan.Target.Database}"); builder.AppendLine("-- Apply through Crucible for transactional row locking, exact preimage verification, and a rollback receipt.");
        foreach (var inclusion in plan.DependencyInclusions ?? []) builder.AppendLine($"-- DEPENDENCY: {inclusion.IncludedIdentity} via {inclusion.Relation} ({inclusion.SelectedEndpoint} = {inclusion.IncludedEndpoint} = {inclusion.MatchedValue}); triggered by {inclusion.SelectedIdentity}.");
        foreach (var remap in plan.IdRemaps) builder.AppendLine($"-- ID REMAP: {remap.Table}.{remap.Column} {remap.SourceId} -> {remap.TargetId}; {remap.RewrittenReferences:N0} recognized selected reference(s) rewritten.");
        builder.AppendLine("START TRANSACTION;");
        foreach (var operation in plan.Operations.Where(operation => operation.Status == DatabaseSyncOperationStatus.Ready)) builder.AppendLine(Preview(operation));
        builder.AppendLine("-- Preview intentionally does not COMMIT. Use Crucible apply after every conflict is resolved."); builder.AppendLine("ROLLBACK;"); return builder.ToString();
    }

    private static DatabaseSyncOperation FromAuditChange(LegacyDatabaseRowChange change) => new(change.Table, change.Domain, change.Kind, change.Key,
        change.Fields.Select(field => new DatabaseSyncField(field.Column, field.Baseline, field.Legacy)).ToArray(), DatabaseSyncOperationStatus.Ready, "Not analyzed");

    private static string OperationIdentity(DatabaseSyncOperation operation) => $"{operation.Table}\u001f{string.Join('\u001e', operation.Key.OrderBy(part => part.Column, StringComparer.OrdinalIgnoreCase).Select(part => $"{part.Column}\u001d{(int)part.Value.State}\u001d{part.Value.Value}"))}";
    private static IReadOnlyList<LegacyDatabaseAuditValue> RelationValues(DatabaseSyncOperation operation, string column)
    {
        var values = new List<LegacyDatabaseAuditValue>();
        values.AddRange(operation.Key.Where(part => part.Column.Equals(column, StringComparison.OrdinalIgnoreCase)).Select(part => part.Value));
        values.AddRange(operation.Fields.Where(field => field.Column.Equals(column, StringComparison.OrdinalIgnoreCase)).Select(field => operation.Kind == LegacyDatabaseRowChangeKind.Removed ? field.Before : field.After));
        return values.Where(value => value.State is LegacyDatabaseAuditValueState.Scalar or LegacyDatabaseAuditValueState.Binary)
            .DistinctBy(value => $"{(int)value.State}\u001f{value.Value}", StringComparer.Ordinal).ToArray();
    }
    private static string EndpointKey(string table, string column, LegacyDatabaseAuditValue value) => $"{table.ToUpperInvariant()}\u001f{column.ToUpperInvariant()}\u001f{(int)value.State}\u001f{value.Value}";
    private static string DisplayRelationValue(LegacyDatabaseAuditValue value) => value.State == LegacyDatabaseAuditValueState.Binary
        ? $"<binary {Convert.FromBase64String(value.Value ?? string.Empty).Length:N0} bytes>"
        : value.Value ?? string.Empty;

    private static async Task<IReadOnlyList<DatabaseSyncIdRemap>> AllocateRemapsAsync(MySqlConnection connection, DatabaseConnectionProfile target,
        DatabaseCapabilities capabilities, IReadOnlyList<DatabaseSyncOperation> source, IReadOnlyList<DatabaseSyncOperation> preliminary, uint? requestedStart, CancellationToken cancellationToken)
    {
        var candidates = source.Select((operation, index) => (Operation: operation, Analysis: preliminary[index]))
            .Where(value => value.Operation.Kind == LegacyDatabaseRowChangeKind.Added && value.Analysis.Status == DatabaseSyncOperationStatus.Conflict && value.Analysis.Finding.Contains("key is already occupied", StringComparison.OrdinalIgnoreCase))
            .Select(value =>
            {
                if (value.Operation.Key.Count != 1 || value.Operation.Key[0].Value.State != LegacyDatabaseAuditValueState.Scalar || !uint.TryParse(value.Operation.Key[0].Value.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return (Valid: false, value.Operation, Column: string.Empty, Id: 0u);
                return (Valid: true, value.Operation, value.Operation.Key[0].Column, Id: id);
            }).Where(value => value.Valid).ToArray();
        var result = new List<DatabaseSyncIdRemap>();
        foreach (var group in candidates.GroupBy(value => (value.Operation.Table, value.Column), new TableColumnComparer()))
        {
            var table = capabilities.FindTable(group.Key.Table); var column = table?.Find(group.Key.Column); if (table is null || column is null || !IsInteger(column.DataType)) continue;
            var capacity = IntegerCapacity(column.ColumnType); var reserved = source.Where(operation => operation.Kind == LegacyDatabaseRowChangeKind.Added && operation.Table.Equals(group.Key.Table, StringComparison.OrdinalIgnoreCase) && operation.Key.Count == 1 && operation.Key[0].Column.Equals(group.Key.Column, StringComparison.OrdinalIgnoreCase) && operation.Key[0].Value.State == LegacyDatabaseAuditValueState.Scalar)
                .Select(operation => ulong.TryParse(operation.Key[0].Value.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0).Where(value => value > 0).ToHashSet();
            await using var maximumCommand = new MySqlCommand($"SELECT COALESCE(MAX({Quote(group.Key.Column)}),0) FROM {Qualified(target.Database, group.Key.Table)} WHERE {Quote(group.Key.Column)} >= 0", connection);
            var maximum = Convert.ToUInt64(await maximumCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture); var candidate = Math.Max(maximum + 1, requestedStart ?? 1u);
            foreach (var collision in group.OrderBy(value => value.Id))
            {
                while (reserved.Contains(candidate)) candidate++;
                if (candidate == 0 || candidate > capacity || candidate > uint.MaxValue) throw new OverflowException($"No collision-free ID remains in {group.Key.Table}.{group.Key.Column} above {maximum:N0} within {column.ColumnType}.");
                result.Add(new(group.Key.Table, group.Key.Column, collision.Id, (uint)candidate, 0)); reserved.Add(candidate); candidate++;
            }
        }
        return result;
    }

    private static IReadOnlyList<DatabaseSyncOperation> RewriteRemappedReferences(IReadOnlyList<DatabaseSyncOperation> source, DatabaseCapabilities capabilities,
        IReadOnlyList<DatabaseSyncIdRemap> allocated, out IReadOnlyList<DatabaseSyncIdRemap> completed)
    {
        var operations = source.ToArray(); var remaps = new List<DatabaseSyncIdRemap>(allocated.Count);
        foreach (var remap in allocated)
        {
            var references = capabilities.Relationships.Where(relation => relation.ToTable.Equals(remap.Table, StringComparison.OrdinalIgnoreCase) && relation.ToColumn.Equals(remap.Column, StringComparison.OrdinalIgnoreCase)).ToArray(); var rewrittenReferences = 0;
            for (var index = 0; index < operations.Length; index++)
            {
                var operation = operations[index]; var key = operation.Key.ToArray(); var fields = operation.Fields.ToArray();
                if (operation.Kind == LegacyDatabaseRowChangeKind.Added && operation.Table.Equals(remap.Table, StringComparison.OrdinalIgnoreCase))
                {
                    key = key.Select(part => part.Column.Equals(remap.Column, StringComparison.OrdinalIgnoreCase) && IsScalar(part.Value, remap.SourceId) ? part with { Value = Scalar(remap.TargetId) } : part).ToArray();
                    fields = fields.Select(field => field.Column.Equals(remap.Column, StringComparison.OrdinalIgnoreCase) && IsScalar(field.After, remap.SourceId) ? field with { After = Scalar(remap.TargetId) } : field).ToArray();
                }
                foreach (var relation in references.Where(relation => relation.FromTable.Equals(operation.Table, StringComparison.OrdinalIgnoreCase)))
                {
                    var referenceChanged = false;
                    fields = fields.Select(field =>
                    {
                        if (!field.Column.Equals(relation.FromColumn, StringComparison.OrdinalIgnoreCase) || !IsScalar(field.After, remap.SourceId) || operation.Kind is LegacyDatabaseRowChangeKind.Removed) return field;
                        referenceChanged = true; return field with { After = Scalar(remap.TargetId) };
                    }).ToArray();
                    if (operation.Kind == LegacyDatabaseRowChangeKind.Added)
                        key = key.Select(part =>
                        {
                            if (!part.Column.Equals(relation.FromColumn, StringComparison.OrdinalIgnoreCase) || !IsScalar(part.Value, remap.SourceId)) return part;
                            referenceChanged = true; return part with { Value = Scalar(remap.TargetId) };
                        }).ToArray();
                    if (referenceChanged) rewrittenReferences++;
                }
                operations[index] = operation with { Key = key, Fields = fields };
            }
            remaps.Add(remap with { RewrittenReferences = rewrittenReferences });
        }
        completed = remaps; return operations;
    }

    private static bool IsInteger(string dataType) => dataType.ToLowerInvariant() is "tinyint" or "smallint" or "mediumint" or "int" or "integer" or "bigint";
    private static ulong IntegerCapacity(string columnType)
    {
        var unsigned = columnType.Contains("unsigned", StringComparison.OrdinalIgnoreCase); var dataType = columnType.Split(['(', ' '], StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
        return dataType switch { "tinyint" => unsigned ? byte.MaxValue : (ulong)sbyte.MaxValue, "smallint" => unsigned ? ushort.MaxValue : (ulong)short.MaxValue, "mediumint" => unsigned ? 16_777_215u : 8_388_607u, "int" or "integer" => unsigned ? uint.MaxValue : int.MaxValue, "bigint" => uint.MaxValue, _ => 0 };
    }
    private static bool IsScalar(LegacyDatabaseAuditValue value, uint expected) => value.State == LegacyDatabaseAuditValueState.Scalar && uint.TryParse(value.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed == expected;
    private static LegacyDatabaseAuditValue Scalar(uint value) => new(LegacyDatabaseAuditValueState.Scalar, value.ToString(CultureInfo.InvariantCulture));

    private sealed class TableColumnComparer : IEqualityComparer<(string Table, string Column)>
    {
        public bool Equals((string Table, string Column) x, (string Table, string Column) y) => x.Table.Equals(y.Table, StringComparison.OrdinalIgnoreCase) && x.Column.Equals(y.Column, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Table, string Column) value) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Table), StringComparer.OrdinalIgnoreCase.GetHashCode(value.Column));
    }

    private static async Task<DatabaseSyncOperation> AnalyzeAsync(MySqlConnection connection, MySqlTransaction? transaction, DatabaseConnectionProfile target,
        DatabaseCapabilities capabilities, DatabaseSyncOperation operation, bool lockRow, CancellationToken cancellationToken)
    {
        var fields = operation.Fields.ToArray();
        var table = capabilities.FindTable(operation.Table); if (table is null) return Blocked("Target schema has no matching table.");
        if (operation.Key.Count == 0) return Blocked("Operation has no complete primary key.");
        foreach (var part in operation.Key) if (table.Find(part.Column) is null) return Blocked($"Target table is missing key column {part.Column}.");
        fields = operation.Fields.Where(field => table.Find(field.Column)?.Extra.Contains("generated", StringComparison.OrdinalIgnoreCase) != true).ToArray();
        var missing = fields.Where(field => table.Find(field.Column) is null).Select(field => field.Column).ToArray(); if (missing.Length > 0) return Blocked($"Target table is missing source column(s): {string.Join(", ", missing)}.");
        if (operation.Kind == LegacyDatabaseRowChangeKind.Added)
        {
            var source = fields.Select(field => field.Column).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var required = table.Columns.Where(column => !source.Contains(column.Name) && !column.Nullable && column.DefaultValue is null && !column.Extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase) && !column.Extra.Contains("generated", StringComparison.OrdinalIgnoreCase)).Select(column => column.Name).ToArray();
            if (required.Length > 0) return Blocked($"Target INSERT requires unmapped column(s): {string.Join(", ", required)}.");
        }
        var selected = fields.Select(field => table.Find(field.Column)!).Concat(operation.Key.Select(part => table.Find(part.Column)!)).DistinctBy(column => column.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var where = string.Join(" AND ", operation.Key.Select((part, index) => $"{Quote(part.Column)} <=> @key{index}"));
        var sql = $"SELECT {string.Join(",", selected.Select(column => Quote(column.Name)))} FROM {Qualified(target.Database, table.Name)} WHERE {where} LIMIT 2{(lockRow ? " FOR UPDATE" : string.Empty)}";
        await using var command = new MySqlCommand(sql, connection, transaction); for (var index = 0; index < operation.Key.Count; index++) command.Parameters.AddWithValue($"@key{index}", Decode(operation.Key[index].Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); var rows = new List<Dictionary<string, LegacyDatabaseAuditValue>>(2);
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new Dictionary<string, LegacyDatabaseAuditValue>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < selected.Length; index++) values[selected[index].Name] = Encode(reader.IsDBNull(index) ? null : reader.GetValue(index)); rows.Add(values);
        }
        if (rows.Count > 1) return Blocked("The declared key matched more than one target row.");
        if (rows.Count == 0) return operation.Kind switch
        {
            LegacyDatabaseRowChangeKind.Added => Ready("Target key is unused; complete INSERT preflight passed."),
            LegacyDatabaseRowChangeKind.Removed => Applied("Target row is already absent."),
            _ => Conflict("Target row is missing; a baseline-attributed UPDATE cannot be applied.")
        };
        var row = rows[0];
        if (operation.Kind == LegacyDatabaseRowChangeKind.Added)
            return fields.All(field => Equal(row[field.Column], field.After)) ? Applied("Target row already equals the edited source row.") : Conflict("Target key is already occupied by different row content.");
        if (operation.Kind == LegacyDatabaseRowChangeKind.Removed)
            return fields.All(field => Equal(row[field.Column], field.Before)) ? Ready("Target row exactly matches the baseline removal preimage.") : Conflict("Target row differs from the baseline removal preimage.");
        var divergent = fields.Where(field => !Equal(row[field.Column], field.Before) && !Equal(row[field.Column], field.After)).Select(field => field.Column).ToArray();
        if (divergent.Length > 0) return Conflict($"Target value differs from both baseline and edited value in: {string.Join(", ", divergent)}.");
        return fields.All(field => Equal(row[field.Column], field.After)) ? Applied("Target row already contains every edited value.") : Ready("Every changed target field still equals baseline or is already applied.");

        DatabaseSyncOperation Ready(string finding) => operation with { Fields = fields, Status = DatabaseSyncOperationStatus.Ready, Finding = finding };
        DatabaseSyncOperation Applied(string finding) => operation with { Fields = fields, Status = DatabaseSyncOperationStatus.AlreadyApplied, Finding = finding };
        DatabaseSyncOperation Conflict(string finding) => operation with { Fields = fields, Status = DatabaseSyncOperationStatus.Conflict, Finding = finding };
        DatabaseSyncOperation Blocked(string finding) => operation with { Fields = fields, Status = DatabaseSyncOperationStatus.Blocked, Finding = finding };
    }

    private static async Task ExecuteAsync(MySqlConnection connection, MySqlTransaction transaction, DatabaseConnectionProfile target,
        DatabaseCapabilities capabilities, DatabaseSyncOperation operation, CancellationToken cancellationToken)
    {
        var table = capabilities.FindTable(operation.Table) ?? throw new InvalidOperationException($"Target table disappeared: {operation.Table}");
        string sql; var parameters = new List<(string Name, object? Value)>();
        if (operation.Kind == LegacyDatabaseRowChangeKind.Added)
        {
            var fields = operation.Fields.Where(field => field.After.State != LegacyDatabaseAuditValueState.Missing && table.Find(field.Column)?.Extra.Contains("generated", StringComparison.OrdinalIgnoreCase) != true).ToArray();
            sql = $"INSERT INTO {Qualified(target.Database, operation.Table)} ({string.Join(",", fields.Select(field => Quote(field.Column)))}) VALUES ({string.Join(",", fields.Select((_, index) => $"@value{index}"))})";
            parameters.AddRange(fields.Select((field, index) => ($"@value{index}", Decode(field.After))));
        }
        else if (operation.Kind == LegacyDatabaseRowChangeKind.Modified)
        {
            sql = $"UPDATE {Qualified(target.Database, operation.Table)} SET {string.Join(",", operation.Fields.Select((field, index) => $"{Quote(field.Column)}=@value{index}"))} WHERE {string.Join(" AND ", operation.Key.Select((part, index) => $"{Quote(part.Column)} <=> @key{index}"))}";
            parameters.AddRange(operation.Fields.Select((field, index) => ($"@value{index}", Decode(field.After)))); parameters.AddRange(operation.Key.Select((part, index) => ($"@key{index}", Decode(part.Value))));
        }
        else if (operation.Kind == LegacyDatabaseRowChangeKind.Removed)
        {
            sql = $"DELETE FROM {Qualified(target.Database, operation.Table)} WHERE {string.Join(" AND ", operation.Key.Select((part, index) => $"{Quote(part.Column)} <=> @key{index}"))}"; parameters.AddRange(operation.Key.Select((part, index) => ($"@key{index}", Decode(part.Value))));
        }
        else throw new InvalidOperationException($"Unsupported synchronization change kind {operation.Kind}.");
        await using var command = new MySqlCommand(sql, connection, transaction); foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken); if (affected != 1) throw new DBConcurrencyException($"Expected exactly one affected row for {operation.Identity}; MySQL reported {affected}.");
    }

    private static DatabaseSyncOperation Reverse(DatabaseSyncOperation operation)
    {
        var fields = operation.Fields.Select(field => new DatabaseSyncField(field.Column, field.After, field.Before)).ToArray();
        var kind = operation.Kind switch { LegacyDatabaseRowChangeKind.Added => LegacyDatabaseRowChangeKind.Removed, LegacyDatabaseRowChangeKind.Removed => LegacyDatabaseRowChangeKind.Added, _ => LegacyDatabaseRowChangeKind.Modified };
        return operation with { Kind = kind, Fields = fields, Status = DatabaseSyncOperationStatus.Ready, Finding = "Rollback preflight pending." };
    }

    private static IEnumerable<DatabaseSyncOperation> OrderOperations(IEnumerable<DatabaseSyncOperation> operations, DatabaseCapabilities capabilities)
    {
        var list = operations.ToArray(); var tables = list.Select(operation => operation.Table).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var dependencies = tables.ToDictionary(table => table, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        foreach (var relation in capabilities.Relationships.Where(relation => relation.Declared && dependencies.ContainsKey(relation.FromTable) && dependencies.ContainsKey(relation.ToTable))) dependencies[relation.FromTable].Add(relation.ToTable);
        var ordered = new List<string>(); var remaining = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(table => dependencies[table].All(parent => !remaining.Contains(parent))).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (ready.Length == 0) ready = [remaining.Order(StringComparer.OrdinalIgnoreCase).First()];
            foreach (var table in ready) { remaining.Remove(table); ordered.Add(table); }
        }
        var rank = ordered.Select((table, index) => (table, index)).ToDictionary(pair => pair.table, pair => pair.index, StringComparer.OrdinalIgnoreCase);
        return list.OrderBy(operation => operation.Kind == LegacyDatabaseRowChangeKind.Removed ? 2 : operation.Kind == LegacyDatabaseRowChangeKind.Modified ? 1 : 0)
            .ThenBy(operation => operation.Kind == LegacyDatabaseRowChangeKind.Removed ? -rank[operation.Table] : rank[operation.Table]).ThenBy(operation => operation.Identity, StringComparer.Ordinal);
    }

    private static object? Decode(LegacyDatabaseAuditValue value) => value.State switch
    {
        LegacyDatabaseAuditValueState.Null => DBNull.Value,
        LegacyDatabaseAuditValueState.Binary => Convert.FromBase64String(value.Value ?? string.Empty),
        LegacyDatabaseAuditValueState.Scalar => value.Value,
        _ => throw new InvalidDataException($"Cannot write database value state {value.State}.")
    };
    private static LegacyDatabaseAuditValue Encode(object? value) => value switch
    {
        null or DBNull => LegacyDatabaseAuditValue.Null,
        byte[] bytes => new(LegacyDatabaseAuditValueState.Binary, Convert.ToBase64String(bytes)),
        DateTime date => new(LegacyDatabaseAuditValueState.Scalar, date.ToString("O", CultureInfo.InvariantCulture)),
        DateTimeOffset date => new(LegacyDatabaseAuditValueState.Scalar, date.ToString("O", CultureInfo.InvariantCulture)),
        TimeSpan span => new(LegacyDatabaseAuditValueState.Scalar, span.ToString("c", CultureInfo.InvariantCulture)),
        IFormattable formatted => new(LegacyDatabaseAuditValueState.Scalar, formatted.ToString(null, CultureInfo.InvariantCulture)),
        _ => new(LegacyDatabaseAuditValueState.Scalar, Convert.ToString(value, CultureInfo.InvariantCulture))
    };
    private static bool Equal(LegacyDatabaseAuditValue left, LegacyDatabaseAuditValue right) => left.State == right.State && (left.State is LegacyDatabaseAuditValueState.Null or LegacyDatabaseAuditValueState.Missing or LegacyDatabaseAuditValueState.Unknown || string.Equals(left.Value, right.Value, StringComparison.Ordinal));
    private static string Preview(DatabaseSyncOperation operation)
    {
        var key = string.Join(" AND ", operation.Key.Select(part => $"{Quote(part.Column)} <=> {Literal(part.Value)}"));
        return operation.Kind switch
        {
            LegacyDatabaseRowChangeKind.Added => $"INSERT INTO {Quote(operation.Table)} ({string.Join(",", operation.Fields.Select(field => Quote(field.Column)))}) VALUES ({string.Join(",", operation.Fields.Select(field => Literal(field.After)))});",
            LegacyDatabaseRowChangeKind.Modified => $"UPDATE {Quote(operation.Table)} SET {string.Join(",", operation.Fields.Select(field => $"{Quote(field.Column)}={Literal(field.After)}"))} WHERE {key} AND {string.Join(" AND ", operation.Fields.Select(field => $"{Quote(field.Column)} <=> {Literal(field.Before)}"))};",
            LegacyDatabaseRowChangeKind.Removed => $"DELETE FROM {Quote(operation.Table)} WHERE {key} AND {string.Join(" AND ", operation.Fields.Select(field => $"{Quote(field.Column)} <=> {Literal(field.Before)}"))};",
            _ => $"-- Unsupported {operation.Kind}: {operation.Identity}"
        };
    }
    private static string Literal(LegacyDatabaseAuditValue value) => value.State switch
    {
        LegacyDatabaseAuditValueState.Null => "NULL",
        LegacyDatabaseAuditValueState.Binary => $"X'{Convert.ToHexString(Convert.FromBase64String(value.Value ?? string.Empty))}'",
        LegacyDatabaseAuditValueState.Scalar => $"CONVERT(X'{Convert.ToHexString(Encoding.UTF8.GetBytes(value.Value ?? string.Empty))}' USING utf8mb4)",
        _ => throw new InvalidDataException($"Cannot preview database value state {value.State}.")
    };
    private static void ValidateTarget(DatabaseSyncTarget expected, DatabaseConnectionProfile actual)
    {
        if (!expected.Host.Equals(actual.Host, StringComparison.OrdinalIgnoreCase) || expected.Port != actual.Port || !expected.User.Equals(actual.User, StringComparison.OrdinalIgnoreCase) || !expected.Database.Equals(actual.Database, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Plan/receipt is bound to {expected.User}@{expected.Host}:{expected.Port}/{expected.Database}, not {actual.User}@{actual.Host}:{actual.Port}/{actual.Database}.");
    }
    private static string Quote(string name) => ItemWritePlan.QuoteIdentifier(name);
    private static string Qualified(string database, string table) => $"{Quote(database)}.{Quote(table)}";
    private static string HashPlanContentV2(string sourceAuditSha256, DatabaseSyncTarget target, bool removalsIncluded, IReadOnlyList<string> patterns, IReadOnlyList<DatabaseSyncIdRemap> remaps, IReadOnlyList<DatabaseSyncOperation> operations)
        => Hash(new { sourceAuditSha256, target, removalsIncluded, patterns, remaps, operations });
    private static string HashPlanContent(string sourceAuditSha256, DatabaseSyncTarget target, bool removalsIncluded, IReadOnlyList<string> patterns, IReadOnlyList<DatabaseSyncIdRemap> remaps, IReadOnlyList<DatabaseSyncOperation> operations, bool dependencyClosureIncluded, IReadOnlyList<DatabaseSyncDependencyInclusion> dependencyInclusions)
        => Hash(new { sourceAuditSha256, target, removalsIncluded, patterns, remaps, operations, dependencyClosureIncluded, dependencyInclusions });
    private static string HashReceiptContent(string planSha256, DatabaseSyncTarget target, IReadOnlyList<DatabaseSyncOperation> operations)
        => Hash(new { planSha256, target, operations });
    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, HashJson))).ToLowerInvariant();
    private static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken) { await using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan); return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant(); }
    private static async Task WriteAtomicAsync<T>(string path, T value, bool overwrite, CancellationToken cancellationToken)
    {
        path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); if (File.Exists(path) && !overwrite) throw new IOException($"Output already exists: {path}"); var temporary = TemporaryFor(path);
        try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, Json) + Environment.NewLine, new UTF8Encoding(false), cancellationToken); File.Move(temporary, path, overwrite); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string TemporaryFor(string path) => Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
    private static string ToolVersion() => typeof(DatabaseSynchronizationService).Assembly.GetName().Version?.ToString() ?? "unknown";
}
