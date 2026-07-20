using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoWCrucible.Core;

public sealed record DatabaseSyncColumnTranslation(string SourceColumn, string TargetColumn);
public sealed record DatabaseSyncTargetDefault(string TargetColumn, LegacyDatabaseAuditValue Value);
public enum DatabaseSyncExpansionValueSource { SourceBefore, SourceAfter, Constant, Lookup }
public enum DatabaseSyncValueTransformKind { None, NumericAdd, NumericMultiply, StringPrefix, StringSuffix, ExactMap, NullFallback }
public sealed record DatabaseSyncValueMap(LegacyDatabaseAuditValue Match, LegacyDatabaseAuditValue Result);
public sealed record DatabaseSyncValueTransform(
    DatabaseSyncValueTransformKind Kind,
    string Operand = "",
    IReadOnlyList<DatabaseSyncValueMap>? Mappings = null,
    LegacyDatabaseAuditValue? Fallback = null);
public sealed record DatabaseSyncExpansionValue(
    DatabaseSyncExpansionValueSource Source,
    string SourceColumn,
    LegacyDatabaseAuditValue? Constant = null,
    string? LookupName = null,
    DatabaseSyncValueTransform? Transform = null);
public enum DatabaseSyncLookupSource { AuditChanges, TargetDatabase }
public sealed record DatabaseSyncLookupMatch(string LookupColumn, DatabaseSyncExpansionValue Input);
public sealed record DatabaseSyncRowLookup(
    string Name,
    DatabaseSyncLookupSource Source,
    string Table,
    IReadOnlyList<DatabaseSyncLookupMatch> Matches,
    string ResultColumn,
    DatabaseSyncExpansionValueSource ResultVersion = DatabaseSyncExpansionValueSource.SourceAfter);
public sealed record DatabaseSyncExpansionKeyBinding(string TargetColumn, DatabaseSyncExpansionValue Value);
public sealed record DatabaseSyncExpansionFieldBinding(
    string TargetColumn,
    DatabaseSyncExpansionValue? Before,
    DatabaseSyncExpansionValue? After);
public sealed record DatabaseSyncRowExpansion(
    string Name,
    string TargetTable,
    IReadOnlyList<LegacyDatabaseRowChangeKind> SourceKinds,
    LegacyDatabaseRowChangeKind TargetKind,
    IReadOnlyList<DatabaseSyncExpansionKeyBinding> KeyBindings,
    IReadOnlyList<DatabaseSyncExpansionFieldBinding> FieldBindings);
public sealed record DatabaseSyncTableTranslation(
    string SourceTable,
    string TargetTable,
    IReadOnlyList<DatabaseSyncColumnTranslation> ColumnMappings,
    IReadOnlyList<string> DroppedSourceColumns,
    IReadOnlyList<DatabaseSyncTargetDefault> TargetDefaults,
    IReadOnlyList<string>? ObservedSourceColumns = null,
    IReadOnlyList<string>? SourcePrimaryKeyColumns = null,
    bool RequiresInsertDefaults = false,
    bool SuppressPrimaryOutput = false,
    IReadOnlyList<DatabaseSyncRowExpansion>? Expansions = null,
    IReadOnlyList<DatabaseSyncRowLookup>? Lookups = null);
public sealed record DatabaseSyncTranslationProfile(
    string Format,
    int FormatVersion,
    string Name,
    DateTimeOffset CreatedUtc,
    string SourceAuditSha256,
    string TargetSchemaSha256,
    string TargetServerVersion,
    IReadOnlyList<DatabaseSyncTableTranslation> Tables);
public sealed record DatabaseSyncTranslationEvidence(
    string SourceTable,
    string TargetTable,
    string Action,
    string SourceColumn,
    string TargetColumn,
    int Operations,
    string Description);
public sealed record DatabaseSyncTranslationResult(
    IReadOnlyList<DatabaseSyncOperation> Operations,
    IReadOnlyList<DatabaseSyncTranslationEvidence> Evidence,
    int BlockedOperations,
    IReadOnlyList<int>? SourceOperationIndexes = null,
    IReadOnlyList<DatabaseSyncLookupEvidence>? LookupEvidence = null);
public sealed record DatabaseSyncTargetLookupRequest(
    string SourceOperationIdentity,
    string SourceDisplayIdentity,
    string SourceTable,
    string LookupName,
    string TargetTable,
    IReadOnlyList<LegacyDatabaseAuditKeyPart> Match,
    string ResultColumn,
    string? Finding = null);
public sealed record DatabaseSyncLookupEvidence(
    string SourceOperationIdentity,
    string SourceDisplayIdentity,
    string LookupName,
    DatabaseSyncLookupSource Source,
    string LookupTable,
    IReadOnlyList<LegacyDatabaseAuditKeyPart> Match,
    string ResultColumn,
    LegacyDatabaseAuditValue Result,
    string MatchedIdentity);
public sealed record DatabaseSyncResolvedLookup(
    string SourceOperationIdentity,
    string LookupName,
    LegacyDatabaseAuditValue? Value,
    string? Finding = null,
    DatabaseSyncLookupEvidence? Evidence = null);

/// <summary>
/// Exact schema bridge used by legacy database recovery. Primary rows support explicit table/column renames,
/// reviewed source-column drops, and typed target defaults. Structural outputs are separately reviewed row
/// templates: every target key/preimage/postimage value names a source value, typed constant, or explicitly
/// matched named row lookup. Bounded deterministic transforms are review data, not executable code. Nothing is
/// inferred from column similarity, and an incomplete or ambiguous expansion blocks instead of inventing content.
/// </summary>
public sealed class DatabaseSyncTranslationService
{
    public const string ProfileFormat = "wow-crucible-database-schema-bridge";
    public const int ProfileFormatVersion = 3;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public DatabaseSyncTranslationProfile CreateTemplate(
        string name,
        string sourceAuditSha256,
        DatabaseCapabilities target,
        IReadOnlyList<DatabaseSyncOperation> operations)
    {
        ValidateSha256(sourceAuditSha256, nameof(sourceAuditSha256));
        var rules = new List<DatabaseSyncTableTranslation>();
        foreach (var sourceGroup in operations.GroupBy(operation => operation.Table, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var sourceColumns = sourceGroup.SelectMany(operation => operation.Key.Select(part => part.Column).Concat(operation.Fields.Select(field => field.Column)))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var sourceKeys = sourceGroup.SelectMany(operation => operation.Key.Select(part => part.Column)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var targetTable = target.FindTable(sourceGroup.Key);
            var mappings = targetTable is null
                ? sourceColumns.Select(column => new DatabaseSyncColumnTranslation(column, string.Empty)).ToArray()
                : sourceColumns.Where(column => targetTable.Find(column) is null).Select(column => new DatabaseSyncColumnTranslation(column, string.Empty)).ToArray();
            var available = sourceColumns.Where(column => targetTable?.Find(column) is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var defaults = targetTable is null || !sourceGroup.Any(operation => operation.Kind == LegacyDatabaseRowChangeKind.Added)
                ? []
                : targetTable.Columns.Where(column => !available.Contains(column.Name) && !column.Nullable && column.DefaultValue is null && !Generated(column))
                    .Select(column => new DatabaseSyncTargetDefault(column.Name, LegacyDatabaseAuditValue.Unknown)).ToArray();
            rules.Add(new(sourceGroup.Key, targetTable?.Name ?? string.Empty, mappings, [], defaults, sourceColumns, sourceKeys, sourceGroup.Any(operation => operation.Kind == LegacyDatabaseRowChangeKind.Added)));
        }
        return new(ProfileFormat, ProfileFormatVersion, string.IsNullOrWhiteSpace(name) ? "Cross-core schema bridge" : name.Trim(), DateTimeOffset.UtcNow,
            sourceAuditSha256.ToLowerInvariant(), HashTargetSchema(target), target.ServerVersion, rules);
    }

    public DatabaseSyncTranslationResult Translate(
        IReadOnlyList<DatabaseSyncOperation> operations,
        DatabaseSyncTranslationProfile profile,
        string sourceAuditSha256,
        DatabaseCapabilities target,
        IReadOnlyList<DatabaseSyncOperation>? lookupCorpus = null,
        IReadOnlyList<DatabaseSyncResolvedLookup>? resolvedTargetLookups = null)
    {
        ValidateProfile(profile, sourceAuditSha256, target);
        var rules = profile.Tables.ToDictionary(rule => rule.SourceTable, StringComparer.OrdinalIgnoreCase);
        var translated = new List<DatabaseSyncOperation>(operations.Count);
        var sourceIndexes = new List<int>(operations.Count);
        lookupCorpus ??= operations;
        var resolvedLookups = (resolvedTargetLookups ?? []).ToDictionary(value => LookupIdentity(value.SourceOperationIdentity, value.LookupName), StringComparer.OrdinalIgnoreCase);
        var evidenceCounts = new Dictionary<(string SourceTable, string TargetTable, string Action, string SourceColumn, string TargetColumn, string Description), int>();
        for (var sourceIndex = 0; sourceIndex < operations.Count; sourceIndex++)
        {
            var operation = operations[sourceIndex];
            if (!rules.TryGetValue(operation.Table, out var rule))
            {
                Add(Block(operation, $"Schema bridge has no rule for source table {operation.Table}."));
                continue;
            }
            var matchingExpansions = (rule.Expansions ?? []).Where(expansion => expansion.SourceKinds.Contains(operation.Kind)).ToArray();
            if (!rule.SuppressPrimaryOutput)
            {
                Add(TranslatePrimary(operation, rule, target, (action, sourceColumn, targetColumn, description) =>
                    Count(operation.Table, rule.TargetTable, action, sourceColumn, targetColumn, description)));
            }
            else if (matchingExpansions.Length == 0)
            {
                Add(Block(operation, $"Primary output is suppressed but no structural expansion accepts source change kind {operation.Kind}."));
            }
            foreach (var expansion in matchingExpansions)
            {
                Add(TranslateExpansion(operation, rule, expansion, target, lookupCorpus, resolvedLookups, (action, sourceColumn, targetColumn, description) =>
                    Count(operation.Table, expansion.TargetTable, action, sourceColumn, targetColumn, description)));
            }

            void Add(DatabaseSyncOperation output)
            {
                translated.Add(output); sourceIndexes.Add(sourceIndex);
            }
            void Count(string sourceTable, string targetTable, string action, string sourceColumn, string targetColumn, string description)
            {
                var evidenceKey = (sourceTable, targetTable, action, sourceColumn, targetColumn, description);
                evidenceCounts[evidenceKey] = evidenceCounts.GetValueOrDefault(evidenceKey) + 1;
            }
        }
        var collapsed = translated.Select((operation, index) => (operation, index)).Where(value => value.operation.Status != DatabaseSyncOperationStatus.Blocked)
            .GroupBy(value => TargetIdentity(value.operation), StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).ToArray();
        foreach (var group in collapsed)
            foreach (var value in group)
                translated[value.index] = Block(value.operation, $"Multiple translated outputs map onto target identity {value.operation.Identity}; source rows and structural siblings may never collapse.");
        var evidence = evidenceCounts.OrderBy(pair => pair.Key.SourceTable, StringComparer.OrdinalIgnoreCase).ThenBy(pair => pair.Key.Action, StringComparer.OrdinalIgnoreCase).ThenBy(pair => pair.Key.SourceColumn, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new DatabaseSyncTranslationEvidence(pair.Key.SourceTable, pair.Key.TargetTable, pair.Key.Action, pair.Key.SourceColumn, pair.Key.TargetColumn, pair.Value, pair.Key.Description)).ToArray();
        var sourceIdentities = operations.Select(SourceOperationIdentity).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lookupEvidence = resolvedLookups.Values.Where(value => value.Evidence is not null && sourceIdentities.Contains(value.SourceOperationIdentity)).Select(value => value.Evidence!)
            .DistinctBy(value => LookupIdentity(value.SourceOperationIdentity, value.LookupName), StringComparer.OrdinalIgnoreCase).OrderBy(value => value.SourceDisplayIdentity, StringComparer.OrdinalIgnoreCase).ThenBy(value => value.LookupName, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(translated, evidence, translated.Count(operation => operation.Status == DatabaseSyncOperationStatus.Blocked), sourceIndexes, lookupEvidence);
    }

    public IReadOnlyList<DatabaseSyncTargetLookupRequest> BuildTargetLookupRequests(
        IReadOnlyList<DatabaseSyncOperation> operations,
        DatabaseSyncTranslationProfile profile,
        string sourceAuditSha256,
        DatabaseCapabilities target,
        IReadOnlyList<DatabaseSyncOperation>? lookupCorpus = null)
    {
        ValidateProfile(profile, sourceAuditSha256, target); lookupCorpus ??= operations;
        var rules = profile.Tables.ToDictionary(rule => rule.SourceTable, StringComparer.OrdinalIgnoreCase); var requests = new List<DatabaseSyncTargetLookupRequest>();
        foreach (var operation in operations)
        {
            if (!rules.TryGetValue(operation.Table, out var rule)) continue;
            var usedNames = (rule.Expansions ?? []).Where(expansion => expansion.SourceKinds.Contains(operation.Kind)).SelectMany(ExpansionValues)
                .Where(value => value.Source == DatabaseSyncExpansionValueSource.Lookup && !string.IsNullOrWhiteSpace(value.LookupName)).Select(value => value.LookupName!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var name in usedNames)
            {
                var lookup = (rule.Lookups ?? []).FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (lookup is null || lookup.Source != DatabaseSyncLookupSource.TargetDatabase) continue;
                var sourceIdentity = SourceOperationIdentity(operation); var match = new List<LegacyDatabaseAuditKeyPart>(); string? finding = null;
                var targetTable = string.IsNullOrWhiteSpace(lookup.Table) ? null : target.FindTable(lookup.Table);
                if (targetTable is null) finding = string.IsNullOrWhiteSpace(lookup.Table) ? $"Target lookup '{lookup.Name}' has no table selected." : $"Target lookup '{lookup.Name}' table {lookup.Table} does not exist in the bound schema.";
                else if (targetTable.Find(lookup.ResultColumn) is null) finding = $"Target lookup '{lookup.Name}' result column {targetTable.Name}.{lookup.ResultColumn} does not exist.";
                else if (lookup.Matches.Count == 0) finding = $"Target lookup '{lookup.Name}' has no exact match columns.";
                if (finding is null)
                {
                    foreach (var binding in lookup.Matches)
                    {
                        if (targetTable!.Find(binding.LookupColumn) is null) { finding = $"Target lookup '{lookup.Name}' match column {targetTable.Name}.{binding.LookupColumn} does not exist."; break; }
                        if (!TryResolveExpansionValue(operation, rule, binding.Input, lookupCorpus, new Dictionary<string, DatabaseSyncResolvedLookup>(), out var value, out var inputFinding))
                        { finding = $"Target lookup '{lookup.Name}' match {binding.LookupColumn}: {inputFinding}"; break; }
                        match.Add(new(binding.LookupColumn, value));
                    }
                }
                requests.Add(new(sourceIdentity, operation.Identity, operation.Table, lookup.Name, lookup.Table, match, lookup.ResultColumn, finding));
            }
        }
        return requests;
    }

    public IReadOnlyList<DatabaseSyncResolvedLookup> ResolveAuditLookups(
        IReadOnlyList<DatabaseSyncOperation> operations,
        DatabaseSyncTranslationProfile profile,
        string sourceAuditSha256,
        DatabaseCapabilities target,
        IReadOnlyList<DatabaseSyncOperation>? lookupCorpus = null)
    {
        ValidateProfile(profile, sourceAuditSha256, target); lookupCorpus ??= operations;
        var rules = profile.Tables.ToDictionary(rule => rule.SourceTable, StringComparer.OrdinalIgnoreCase); var results = new List<DatabaseSyncResolvedLookup>();
        foreach (var operation in operations)
        {
            if (!rules.TryGetValue(operation.Table, out var rule)) continue;
            foreach (var name in UsedLookupNames(rule, operation, DatabaseSyncLookupSource.AuditChanges))
            {
                var lookup = (rule.Lookups ?? []).Single(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (TryResolveAuditLookup(operation, rule, lookup, lookupCorpus, out var value, out var finding, out var evidence))
                    results.Add(new(SourceOperationIdentity(operation), lookup.Name, value, Evidence: evidence));
                else results.Add(new(SourceOperationIdentity(operation), lookup.Name, null, finding));
            }
        }
        return results;
    }

    private static IEnumerable<DatabaseSyncExpansionValue> ExpansionValues(DatabaseSyncRowExpansion expansion)
    {
        foreach (var binding in expansion.KeyBindings) yield return binding.Value;
        foreach (var binding in expansion.FieldBindings) { if (binding.Before is not null) yield return binding.Before; if (binding.After is not null) yield return binding.After; }
    }

    private static IEnumerable<string> UsedLookupNames(DatabaseSyncTableTranslation rule, DatabaseSyncOperation operation, DatabaseSyncLookupSource source) =>
        (rule.Expansions ?? []).Where(expansion => expansion.SourceKinds.Contains(operation.Kind)).SelectMany(ExpansionValues)
            .Where(value => value.Source == DatabaseSyncExpansionValueSource.Lookup && !string.IsNullOrWhiteSpace(value.LookupName))
            .Select(value => value.LookupName!).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => (rule.Lookups ?? []).Any(lookup => lookup.Source == source && lookup.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

    public async Task<DatabaseSyncTranslationProfile> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var profile = await JsonSerializer.DeserializeAsync<DatabaseSyncTranslationProfile>(stream, Json, cancellationToken) ?? throw new InvalidDataException("Schema bridge profile is empty.");
        ValidateStructure(profile); return profile;
    }

    public static async Task WriteAsync(string path, DatabaseSyncTranslationProfile profile, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        ValidateStructure(profile);
        path = Path.GetFullPath(path); if (File.Exists(path) && !overwrite) throw new IOException($"Schema bridge profile already exists: {path}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(profile, Json) + Environment.NewLine, new UTF8Encoding(false), cancellationToken); File.Move(temporary, path, overwrite); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string HashTargetSchema(DatabaseCapabilities capabilities)
    {
        var canonical = capabilities.Tables.Values.OrderBy(table => table.Name, StringComparer.OrdinalIgnoreCase).Select(table => new
        {
            table.Name,
            Columns = table.Columns.OrderBy(column => column.Ordinal).Select(column => new { column.Name, column.DataType, column.ColumnType, column.Nullable, column.DefaultValue, column.Key, column.Extra, column.Ordinal }).ToArray()
        }).ToArray();
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical))).ToLowerInvariant();
    }

    public static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static DatabaseSyncOperation TranslatePrimary(
        DatabaseSyncOperation operation,
        DatabaseSyncTableTranslation rule,
        DatabaseCapabilities target,
        Action<string, string, string, string> count)
    {
        var targetTable = string.IsNullOrWhiteSpace(rule.TargetTable) ? null : target.FindTable(rule.TargetTable);
        if (targetTable is null)
            return Block(operation, string.IsNullOrWhiteSpace(rule.TargetTable)
                ? $"Schema bridge has no target table selected for {operation.Table}."
                : $"Schema bridge target table {rule.TargetTable} does not exist in the bound target schema.");
        var mappings = rule.ColumnMappings.ToDictionary(mapping => mapping.SourceColumn, StringComparer.OrdinalIgnoreCase);
        var drops = rule.DroppedSourceColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var key = new List<LegacyDatabaseAuditKeyPart>(); var fields = new List<DatabaseSyncField>(); string? blocker = null;
        foreach (var part in operation.Key)
        {
            if (drops.Contains(part.Column)) { blocker = $"Primary-key column {operation.Table}.{part.Column} cannot be dropped by a schema bridge."; break; }
            var targetColumn = ResolveColumn(part.Column, mappings, targetTable);
            if (targetColumn is null) { blocker = $"Schema bridge has no target column for key {operation.Table}.{part.Column}."; break; }
            key.Add(part with { Column = targetColumn.Name });
            if (mappings.ContainsKey(part.Column) || !part.Column.Equals(targetColumn.Name, StringComparison.OrdinalIgnoreCase))
                count("Key", part.Column, targetColumn.Name, $"Mapped primary-key column {operation.Table}.{part.Column} to {targetTable.Name}.{targetColumn.Name}.");
        }
        if (blocker is not null) return Block(operation, blocker);
        foreach (var field in operation.Fields)
        {
            if (drops.Contains(field.Column))
            {
                count("Drop", field.Column, string.Empty, $"Explicitly omitted source field {operation.Table}.{field.Column}; its value is retained only in the verified audit.");
                continue;
            }
            var targetColumn = ResolveColumn(field.Column, mappings, targetTable);
            if (targetColumn is null) { blocker = $"Schema bridge has no target column for {operation.Table}.{field.Column}."; break; }
            fields.Add(field with { Column = targetColumn.Name });
            if (mappings.ContainsKey(field.Column) || !field.Column.Equals(targetColumn.Name, StringComparison.OrdinalIgnoreCase))
                count("Column", field.Column, targetColumn.Name, $"Mapped {operation.Table}.{field.Column} to {targetTable.Name}.{targetColumn.Name}.");
        }
        if (blocker is not null) return Block(operation, blocker);
        if (operation.Kind == LegacyDatabaseRowChangeKind.Added)
        {
            foreach (var targetDefault in rule.TargetDefaults)
            {
                var targetColumn = targetTable.Find(targetDefault.TargetColumn);
                if (targetColumn is null) { blocker = $"Schema bridge default targets missing column {targetTable.Name}.{targetDefault.TargetColumn}."; break; }
                if (targetDefault.Value.State is LegacyDatabaseAuditValueState.Unknown or LegacyDatabaseAuditValueState.Missing)
                { blocker = $"Schema bridge requires an explicit typed default for {targetTable.Name}.{targetColumn.Name}."; break; }
                if (fields.Any(field => field.Column.Equals(targetColumn.Name, StringComparison.OrdinalIgnoreCase)) || key.Any(part => part.Column.Equals(targetColumn.Name, StringComparison.OrdinalIgnoreCase)))
                { blocker = $"Schema bridge default duplicates mapped column {targetTable.Name}.{targetColumn.Name}."; break; }
                fields.Add(new(targetColumn.Name, LegacyDatabaseAuditValue.Missing, targetDefault.Value));
                count("Default", string.Empty, targetColumn.Name, $"Added the explicit profile default for {targetTable.Name}.{targetColumn.Name}.");
            }
            if (blocker is null)
            {
                var present = key.Select(part => part.Column).Concat(fields.Select(field => field.Column)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var required = targetTable.Columns.Where(column => !present.Contains(column.Name) && !column.Nullable && column.DefaultValue is null && !Generated(column)).Select(column => column.Name).ToArray();
                if (required.Length > 0) blocker = $"Schema bridge leaves required target column(s) unresolved: {string.Join(", ", required)}.";
            }
        }
        if (blocker is null && operation.Kind == LegacyDatabaseRowChangeKind.Modified && fields.Count == 0) blocker = "Schema bridge removed every changed field from this UPDATE.";
        if (blocker is not null) return Block(operation, blocker);
        var duplicateKey = key.GroupBy(part => part.Column, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        var duplicateField = fields.GroupBy(field => field.Column, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicateKey is not null || duplicateField is not null)
            return Block(operation, $"Schema bridge maps multiple source columns onto target column {(duplicateKey?.Key ?? duplicateField!.Key)}.");
        if (!operation.Table.Equals(targetTable.Name, StringComparison.OrdinalIgnoreCase))
            count("Table", string.Empty, string.Empty, $"Mapped source table {operation.Table} to target table {targetTable.Name}.");
        return operation with { Table = targetTable.Name, Key = key, Fields = fields, Status = DatabaseSyncOperationStatus.Ready, Finding = "Schema bridge resolved; target comparison pending." };
    }

    private static DatabaseSyncOperation TranslateExpansion(
        DatabaseSyncOperation source,
        DatabaseSyncTableTranslation rule,
        DatabaseSyncRowExpansion expansion,
        DatabaseCapabilities target,
        IReadOnlyList<DatabaseSyncOperation> lookupCorpus,
        IReadOnlyDictionary<string, DatabaseSyncResolvedLookup> resolvedTargetLookups,
        Action<string, string, string, string> count)
    {
        var targetTable = string.IsNullOrWhiteSpace(expansion.TargetTable) ? null : target.FindTable(expansion.TargetTable);
        if (targetTable is null)
            return Block(source, string.IsNullOrWhiteSpace(expansion.TargetTable)
                ? $"Structural expansion '{expansion.Name}' has no target table."
                : $"Structural expansion '{expansion.Name}' targets missing table {expansion.TargetTable}.");
        if (expansion.TargetKind is LegacyDatabaseRowChangeKind.UnattributedCandidate)
            return Block(source, $"Structural expansion '{expansion.Name}' has unsupported target kind {expansion.TargetKind}.");

        var primaryKey = targetTable.Columns.Where(column => column.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase)).Select(column => column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var declaredKey = expansion.KeyBindings.Select(binding => binding.TargetColumn).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (primaryKey.Count == 0) return Block(source, $"Structural expansion '{expansion.Name}' target {targetTable.Name} has no exact primary key.");
        if (!primaryKey.SetEquals(declaredKey))
            return Block(source, $"Structural expansion '{expansion.Name}' key must map the complete target primary key ({string.Join(", ", primaryKey.Order(StringComparer.OrdinalIgnoreCase))}).");

        var key = new List<LegacyDatabaseAuditKeyPart>();
        foreach (var binding in expansion.KeyBindings)
        {
            var column = targetTable.Find(binding.TargetColumn);
            if (column is null) return Block(source, $"Structural expansion '{expansion.Name}' key targets missing column {targetTable.Name}.{binding.TargetColumn}.");
            if (!TryResolveExpansionValue(source, rule, binding.Value, lookupCorpus, resolvedTargetLookups, out var value, out var finding)) return Block(source, $"Structural expansion '{expansion.Name}' key {column.Name}: {finding}");
            if (value.State is LegacyDatabaseAuditValueState.Null or LegacyDatabaseAuditValueState.Unknown or LegacyDatabaseAuditValueState.Missing)
                return Block(source, $"Structural expansion '{expansion.Name}' key {column.Name} resolved to invalid state {value.State}.");
            key.Add(new(column.Name, value));
            CountBinding("ExpansionKey", column.Name, binding.Value);
        }

        var fields = new List<DatabaseSyncField>();
        foreach (var binding in expansion.FieldBindings)
        {
            var column = targetTable.Find(binding.TargetColumn);
            if (column is null) return Block(source, $"Structural expansion '{expansion.Name}' field targets missing column {targetTable.Name}.{binding.TargetColumn}.");
            if (Generated(column)) return Block(source, $"Structural expansion '{expansion.Name}' cannot author generated column {targetTable.Name}.{column.Name}.");
            if (primaryKey.Contains(column.Name)) return Block(source, $"Structural expansion '{expansion.Name}' must define primary-key column {column.Name} only in KeyBindings.");
            LegacyDatabaseAuditValue before; LegacyDatabaseAuditValue after;
            if (expansion.TargetKind == LegacyDatabaseRowChangeKind.Added)
            {
                before = LegacyDatabaseAuditValue.Missing;
                if (binding.After is null) return Block(source, $"Structural expansion '{expansion.Name}' INSERT value {column.Name}: an After binding is required.");
                if (!TryResolveExpansionValue(source, rule, binding.After, lookupCorpus, resolvedTargetLookups, out after, out var finding))
                    return Block(source, $"Structural expansion '{expansion.Name}' INSERT value {column.Name}: {finding}");
                CountBinding("ExpansionAfter", column.Name, binding.After);
            }
            else if (expansion.TargetKind == LegacyDatabaseRowChangeKind.Removed)
            {
                after = LegacyDatabaseAuditValue.Missing;
                if (binding.Before is null) return Block(source, $"Structural expansion '{expansion.Name}' DELETE preimage {column.Name}: a Before binding is required.");
                if (!TryResolveExpansionValue(source, rule, binding.Before, lookupCorpus, resolvedTargetLookups, out before, out var finding))
                    return Block(source, $"Structural expansion '{expansion.Name}' DELETE preimage {column.Name}: {finding}");
                CountBinding("ExpansionBefore", column.Name, binding.Before);
            }
            else
            {
                if (binding.Before is null) return Block(source, $"Structural expansion '{expansion.Name}' UPDATE preimage {column.Name}: a Before binding is required.");
                if (!TryResolveExpansionValue(source, rule, binding.Before, lookupCorpus, resolvedTargetLookups, out before, out var beforeFinding))
                    return Block(source, $"Structural expansion '{expansion.Name}' UPDATE preimage {column.Name}: {beforeFinding}");
                if (binding.After is null) return Block(source, $"Structural expansion '{expansion.Name}' UPDATE postimage {column.Name}: an After binding is required.");
                if (!TryResolveExpansionValue(source, rule, binding.After, lookupCorpus, resolvedTargetLookups, out after, out var afterFinding))
                    return Block(source, $"Structural expansion '{expansion.Name}' UPDATE postimage {column.Name}: {afterFinding}");
                CountBinding("ExpansionBefore", column.Name, binding.Before); CountBinding("ExpansionAfter", column.Name, binding.After);
            }
            if (before.State == LegacyDatabaseAuditValueState.Unknown || after.State == LegacyDatabaseAuditValueState.Unknown)
                return Block(source, $"Structural expansion '{expansion.Name}' field {column.Name} resolved to Unknown.");
            fields.Add(new(column.Name, before, after));
        }

        if (expansion.TargetKind == LegacyDatabaseRowChangeKind.Modified && fields.Count == 0)
            return Block(source, $"Structural expansion '{expansion.Name}' UPDATE has no reviewed fields.");
        var present = key.Select(part => part.Column).Concat(fields.Select(field => field.Column)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (expansion.TargetKind == LegacyDatabaseRowChangeKind.Added)
        {
            var missing = targetTable.Columns.Where(column => !present.Contains(column.Name) && !column.Nullable && column.DefaultValue is null && !Generated(column)).Select(column => column.Name).ToArray();
            if (missing.Length > 0) return Block(source, $"Structural expansion '{expansion.Name}' INSERT leaves required target column(s) unresolved: {string.Join(", ", missing)}.");
            fields.InsertRange(0, key.Select(part => new DatabaseSyncField(part.Column, LegacyDatabaseAuditValue.Missing, part.Value)));
        }
        if (expansion.TargetKind == LegacyDatabaseRowChangeKind.Removed)
        {
            var writable = targetTable.Columns.Where(column => !Generated(column)).Select(column => column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!writable.SetEquals(present))
                return Block(source, $"Structural expansion '{expansion.Name}' DELETE must bind every writable target column for exact rollback; missing: {string.Join(", ", writable.Except(present, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))}.");
            fields.InsertRange(0, key.Select(part => new DatabaseSyncField(part.Column, part.Value, LegacyDatabaseAuditValue.Missing)));
        }
        count("Expansion", expansion.Name, string.Empty, $"Expanded one {source.Table} {source.Kind} row into reviewed {targetTable.Name} {expansion.TargetKind} output '{expansion.Name}'.");
        return new(targetTable.Name, source.Domain, expansion.TargetKind, key, fields, DatabaseSyncOperationStatus.Ready, $"Structural expansion '{expansion.Name}' resolved; target comparison pending.");

        void CountBinding(string action, string targetColumn, DatabaseSyncExpansionValue value)
        {
            var sourceColumn = value.Source is DatabaseSyncExpansionValueSource.Constant or DatabaseSyncExpansionValueSource.Lookup ? string.Empty : value.SourceColumn;
            var origin = value.Source switch
            {
                DatabaseSyncExpansionValueSource.Constant => $"typed constant {DisplayValue(value.Constant!)}",
                DatabaseSyncExpansionValueSource.Lookup => $"named lookup '{value.LookupName}'",
                _ => $"{value.Source} {source.Table}.{value.SourceColumn}"
            };
            var transform = value.Transform is null or { Kind: DatabaseSyncValueTransformKind.None } ? string.Empty : $" through {value.Transform.Kind}";
            count(action, sourceColumn, targetColumn, $"Structural expansion '{expansion.Name}' binds {targetTable.Name}.{targetColumn} from {origin}{transform}.");
        }
    }

    private static bool TryResolveExpansionValue(
        DatabaseSyncOperation source,
        DatabaseSyncTableTranslation rule,
        DatabaseSyncExpansionValue binding,
        IReadOnlyList<DatabaseSyncOperation> lookupCorpus,
        IReadOnlyDictionary<string, DatabaseSyncResolvedLookup> resolvedTargetLookups,
        out LegacyDatabaseAuditValue value,
        out string? finding)
    {
        if (binding.Source == DatabaseSyncExpansionValueSource.Constant)
        {
            value = binding.Constant ?? LegacyDatabaseAuditValue.Unknown;
            if (value.State is LegacyDatabaseAuditValueState.Unknown or LegacyDatabaseAuditValueState.Missing)
            { finding = "the typed constant is unresolved."; return false; }
            return TryApplyTransform(value, binding.Transform, out value, out finding);
        }
        if (binding.Source == DatabaseSyncExpansionValueSource.Lookup)
        {
            if (string.IsNullOrWhiteSpace(binding.LookupName)) { value = LegacyDatabaseAuditValue.Unknown; finding = "the lookup name is blank."; return false; }
            var lookup = (rule.Lookups ?? []).FirstOrDefault(item => item.Name.Equals(binding.LookupName, StringComparison.OrdinalIgnoreCase));
            if (lookup is null) { value = LegacyDatabaseAuditValue.Unknown; finding = $"lookup '{binding.LookupName}' is not declared for source table {rule.SourceTable}."; return false; }
            if (lookup.Source == DatabaseSyncLookupSource.AuditChanges)
            {
                var identity = LookupIdentity(SourceOperationIdentity(source), lookup.Name);
                if (resolvedTargetLookups.TryGetValue(identity, out var resolved))
                {
                    if (resolved.Finding is not null || resolved.Value is null) { value = LegacyDatabaseAuditValue.Unknown; finding = resolved.Finding ?? $"audit lookup '{lookup.Name}' returned no value."; return false; }
                    value = resolved.Value;
                }
                else if (!TryResolveAuditLookup(source, rule, lookup, lookupCorpus, out value, out finding, out _)) return false;
            }
            else
            {
                var identity = LookupIdentity(SourceOperationIdentity(source), lookup.Name);
                if (!resolvedTargetLookups.TryGetValue(identity, out var resolved)) { value = LegacyDatabaseAuditValue.Unknown; finding = $"target lookup '{lookup.Name}' was not resolved against the bound database."; return false; }
                if (resolved.Finding is not null || resolved.Value is null) { value = LegacyDatabaseAuditValue.Unknown; finding = resolved.Finding ?? $"target lookup '{lookup.Name}' returned no value."; return false; }
                value = resolved.Value;
            }
            return TryApplyTransform(value, binding.Transform, out value, out finding);
        }
        if (string.IsNullOrWhiteSpace(binding.SourceColumn))
        { value = LegacyDatabaseAuditValue.Unknown; finding = "the source column is blank."; return false; }
        if (!TryOperationValue(source, binding.SourceColumn, binding.Source, out value, out finding)) return false;
        return TryApplyTransform(value, binding.Transform, out value, out finding);
    }

    private static bool TryResolveAuditLookup(DatabaseSyncOperation source, DatabaseSyncTableTranslation sourceRule, DatabaseSyncRowLookup lookup,
        IReadOnlyList<DatabaseSyncOperation> corpus, out LegacyDatabaseAuditValue value, out string? finding, out DatabaseSyncLookupEvidence? evidence)
    {
        evidence = null;
        var matches = new List<(string Column, LegacyDatabaseAuditValue Value)>();
        foreach (var match in lookup.Matches)
        {
            if (!TryResolveExpansionValue(source, sourceRule, match.Input, corpus, new Dictionary<string, DatabaseSyncResolvedLookup>(), out var expected, out var inputFinding))
            { value = LegacyDatabaseAuditValue.Unknown; finding = $"audit lookup '{lookup.Name}' match {match.LookupColumn}: {inputFinding}"; return false; }
            matches.Add((match.LookupColumn, expected));
        }
        var candidates = new List<DatabaseSyncOperation>();
        foreach (var candidate in corpus.Where(operation => operation.Table.Equals(lookup.Table, StringComparison.OrdinalIgnoreCase)))
        {
            var matched = true;
            foreach (var match in matches)
            {
                if (!TryOperationValue(candidate, match.Column, lookup.ResultVersion, out var actual, out _) || !EqualValue(actual, match.Value)) { matched = false; break; }
            }
            if (matched) candidates.Add(candidate);
            if (candidates.Count > 1) break;
        }
        if (candidates.Count == 0) { value = LegacyDatabaseAuditValue.Unknown; finding = $"audit lookup '{lookup.Name}' found no changed {lookup.Table} row matching every reviewed value."; return false; }
        if (candidates.Count > 1) { value = LegacyDatabaseAuditValue.Unknown; finding = $"audit lookup '{lookup.Name}' matched more than one changed {lookup.Table} row; ambiguous joins are blocked."; return false; }
        if (!TryOperationValue(candidates[0], lookup.ResultColumn, lookup.ResultVersion, out value, out var resultFinding))
        { finding = $"audit lookup '{lookup.Name}' result {lookup.Table}.{lookup.ResultColumn}: {resultFinding}"; return false; }
        evidence = new(SourceOperationIdentity(source), source.Identity, lookup.Name, DatabaseSyncLookupSource.AuditChanges, lookup.Table,
            matches.Select(match => new LegacyDatabaseAuditKeyPart(match.Column, match.Value)).ToArray(), lookup.ResultColumn, value, candidates[0].Identity);
        finding = null; return true;
    }

    private static bool TryOperationValue(DatabaseSyncOperation operation, string column, DatabaseSyncExpansionValueSource version, out LegacyDatabaseAuditValue value, out string? finding)
    {
        var key = operation.Key.FirstOrDefault(part => part.Column.Equals(column, StringComparison.OrdinalIgnoreCase));
        if (key is not null) { value = key.Value; finding = null; return value.State is not LegacyDatabaseAuditValueState.Unknown and not LegacyDatabaseAuditValueState.Missing; }
        var field = operation.Fields.FirstOrDefault(item => item.Column.Equals(column, StringComparison.OrdinalIgnoreCase));
        if (field is null) { value = LegacyDatabaseAuditValue.Unknown; finding = $"column {operation.Table}.{column} is not present in this audited operation."; return false; }
        value = version == DatabaseSyncExpansionValueSource.SourceBefore ? field.Before : field.After;
        if (value.State is LegacyDatabaseAuditValueState.Unknown or LegacyDatabaseAuditValueState.Missing)
        { finding = $"{version} {operation.Table}.{column} is {value.State}."; return false; }
        finding = null; return true;
    }

    private static bool TryApplyTransform(LegacyDatabaseAuditValue input, DatabaseSyncValueTransform? transform, out LegacyDatabaseAuditValue value, out string? finding)
    {
        value = input; finding = null; if (transform is null || transform.Kind == DatabaseSyncValueTransformKind.None) return true;
        try
        {
            switch (transform.Kind)
            {
                case DatabaseSyncValueTransformKind.NumericAdd:
                case DatabaseSyncValueTransformKind.NumericMultiply:
                    if (input.State != LegacyDatabaseAuditValueState.Scalar || !decimal.TryParse(input.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !decimal.TryParse(transform.Operand, NumberStyles.Float, CultureInfo.InvariantCulture, out var operand))
                    { finding = $"{transform.Kind} requires finite invariant decimal scalar input and operand."; return false; }
                    var numeric = transform.Kind == DatabaseSyncValueTransformKind.NumericAdd ? checked(number + operand) : checked(number * operand);
                    value = new(LegacyDatabaseAuditValueState.Scalar, numeric.ToString("G29", CultureInfo.InvariantCulture)); return true;
                case DatabaseSyncValueTransformKind.StringPrefix:
                case DatabaseSyncValueTransformKind.StringSuffix:
                    if (input.State != LegacyDatabaseAuditValueState.Scalar) { finding = $"{transform.Kind} requires scalar text input."; return false; }
                    value = new(LegacyDatabaseAuditValueState.Scalar, transform.Kind == DatabaseSyncValueTransformKind.StringPrefix ? transform.Operand + input.Value : input.Value + transform.Operand); return true;
                case DatabaseSyncValueTransformKind.ExactMap:
                    var mappings = transform.Mappings ?? []; var mapped = mappings.FirstOrDefault(item => EqualValue(item.Match, input));
                    if (mapped is null) { finding = "ExactMap has no typed entry for the resolved input value."; return false; }
                    value = mapped.Result; if (value.State is LegacyDatabaseAuditValueState.Unknown or LegacyDatabaseAuditValueState.Missing) { finding = "ExactMap result is unresolved."; return false; } return true;
                case DatabaseSyncValueTransformKind.NullFallback:
                    if (input.State != LegacyDatabaseAuditValueState.Null) return true;
                    value = transform.Fallback ?? LegacyDatabaseAuditValue.Unknown; if (value.State is LegacyDatabaseAuditValueState.Unknown or LegacyDatabaseAuditValueState.Missing) { finding = "NullFallback has no typed fallback value."; return false; } return true;
                default: finding = $"Unsupported value transform {transform.Kind}."; return false;
            }
        }
        catch (OverflowException) { finding = $"{transform.Kind} overflowed the deterministic decimal range."; return false; }
    }

    private static bool EqualValue(LegacyDatabaseAuditValue left, LegacyDatabaseAuditValue right) => left.State == right.State &&
        (left.State is LegacyDatabaseAuditValueState.Null or LegacyDatabaseAuditValueState.Missing or LegacyDatabaseAuditValueState.Unknown || string.Equals(left.Value, right.Value, StringComparison.Ordinal));
    private static string LookupIdentity(string sourceOperationIdentity, string lookupName) => $"{sourceOperationIdentity}\u001c{lookupName}";
    public static string SourceOperationIdentity(DatabaseSyncOperation operation) => TargetIdentity(operation);

    private static string DisplayValue(LegacyDatabaseAuditValue value) => value.State switch
    {
        LegacyDatabaseAuditValueState.Null => "NULL",
        LegacyDatabaseAuditValueState.Binary => $"binary {Convert.FromBase64String(value.Value ?? string.Empty).Length:N0} bytes",
        _ => value.Value ?? value.State.ToString()
    };

    private static DatabaseColumnCapability? ResolveColumn(string sourceColumn, IReadOnlyDictionary<string, DatabaseSyncColumnTranslation> mappings, DatabaseTableCapability targetTable)
    {
        if (mappings.TryGetValue(sourceColumn, out var mapping)) return string.IsNullOrWhiteSpace(mapping.TargetColumn) ? null : targetTable.Find(mapping.TargetColumn);
        return targetTable.Find(sourceColumn);
    }

    private static DatabaseSyncOperation Block(DatabaseSyncOperation operation, string finding) => operation with { Status = DatabaseSyncOperationStatus.Blocked, Finding = $"Schema translation blocked: {finding}" };
    private static string TargetIdentity(DatabaseSyncOperation operation) => $"{operation.Table}\u001f{string.Join('\u001e', operation.Key.OrderBy(part => part.Column, StringComparer.OrdinalIgnoreCase).Select(part => $"{part.Column}\u001d{(int)part.Value.State}\u001d{part.Value.Value}"))}";
    private static bool Generated(DatabaseColumnCapability column) => column.Extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase) || column.Extra.Contains("generated", StringComparison.OrdinalIgnoreCase);
    private static void ValidateSha256(string value, string name) { if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character))) throw new InvalidDataException($"{name} is not a SHA-256 value."); }

    private static void ValidateProfile(DatabaseSyncTranslationProfile profile, string sourceAuditSha256, DatabaseCapabilities target)
    {
        ValidateStructure(profile);
        if (!profile.SourceAuditSha256.Equals(sourceAuditSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Schema bridge profile is bound to a different recovery audit.");
        var targetHash = HashTargetSchema(target); if (!profile.TargetSchemaSha256.Equals(targetHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Schema bridge profile is stale or belongs to a different target schema. Generate a fresh bridge before planning.");
    }

    private static void ValidateStructure(DatabaseSyncTranslationProfile profile)
    {
        if (profile.Format != ProfileFormat || profile.FormatVersion is < 1 or > ProfileFormatVersion) throw new InvalidDataException($"Unsupported schema bridge profile {profile.Format} v{profile.FormatVersion}.");
        ValidateSha256(profile.SourceAuditSha256, nameof(profile.SourceAuditSha256)); ValidateSha256(profile.TargetSchemaSha256, nameof(profile.TargetSchemaSha256));
        var duplicateTable = profile.Tables.GroupBy(rule => rule.SourceTable, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicateTable is not null) throw new InvalidDataException($"Schema bridge contains duplicate source table rule {duplicateTable.Key}.");
        foreach (var rule in profile.Tables)
        {
            if (string.IsNullOrWhiteSpace(rule.SourceTable)) throw new InvalidDataException("Schema bridge contains an empty source table name.");
            var duplicateMapping = rule.ColumnMappings.GroupBy(mapping => mapping.SourceColumn, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
            if (duplicateMapping is not null) throw new InvalidDataException($"Schema bridge {rule.SourceTable} contains duplicate source-column mapping {duplicateMapping.Key}.");
            var duplicateDrop = rule.DroppedSourceColumns.GroupBy(column => column, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
            if (duplicateDrop is not null) throw new InvalidDataException($"Schema bridge {rule.SourceTable} drops {duplicateDrop.Key} more than once.");
            var overlap = rule.ColumnMappings.Select(mapping => mapping.SourceColumn).Intersect(rule.DroppedSourceColumns, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (overlap is not null) throw new InvalidDataException($"Schema bridge {rule.SourceTable}.{overlap} is both mapped and dropped.");
            var duplicateDefault = rule.TargetDefaults.GroupBy(value => value.TargetColumn, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
            if (duplicateDefault is not null) throw new InvalidDataException($"Schema bridge {rule.SourceTable} contains duplicate target default {duplicateDefault.Key}.");
            foreach (var targetDefault in rule.TargetDefaults.Where(value => value.Value.State == LegacyDatabaseAuditValueState.Binary))
                try { _ = Convert.FromBase64String(targetDefault.Value.Value ?? string.Empty); }
                catch (FormatException exception) { throw new InvalidDataException($"Schema bridge {rule.SourceTable}.{targetDefault.TargetColumn} contains an invalid base64 binary default.", exception); }
            var invalidDefaultState = rule.TargetDefaults.FirstOrDefault(value => !Enum.IsDefined(value.Value.State));
            if (invalidDefaultState is not null) throw new InvalidDataException($"Schema bridge {rule.SourceTable}.{invalidDefaultState.TargetColumn} uses an invalid default value state.");
            var duplicateObserved = (rule.ObservedSourceColumns ?? []).GroupBy(column => column, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
            if (duplicateObserved is not null) throw new InvalidDataException($"Schema bridge {rule.SourceTable} observes {duplicateObserved.Key} more than once.");
            var unknownKey = (rule.SourcePrimaryKeyColumns ?? []).Except(rule.ObservedSourceColumns ?? [], StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (unknownKey is not null) throw new InvalidDataException($"Schema bridge {rule.SourceTable} marks unobserved column {unknownKey} as a source key.");
            var expansions = rule.Expansions ?? [];
            var lookups = rule.Lookups ?? [];
            if (profile.FormatVersion == 1 && (rule.SuppressPrimaryOutput || expansions.Count > 0)) throw new InvalidDataException($"Schema bridge v1 cannot contain structural expansions for {rule.SourceTable}.");
            if (profile.FormatVersion < 3 && (lookups.Count > 0 || expansions.SelectMany(ExpansionValues).Any(value => value.Source == DatabaseSyncExpansionValueSource.Lookup || value.Transform is not null and not { Kind: DatabaseSyncValueTransformKind.None })))
                throw new InvalidDataException($"Schema bridge v{profile.FormatVersion} cannot contain lookups or value transforms for {rule.SourceTable}.");
            var duplicateLookup = lookups.GroupBy(lookup => lookup.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
            if (duplicateLookup is not null) throw new InvalidDataException($"Schema bridge {rule.SourceTable} contains duplicate lookup name {duplicateLookup.Key}.");
            foreach (var lookup in lookups)
            {
                if (string.IsNullOrWhiteSpace(lookup.Name)) throw new InvalidDataException($"Schema bridge {rule.SourceTable} contains an unnamed row lookup.");
                if (!Enum.IsDefined(lookup.Source)) throw new InvalidDataException($"Schema bridge {rule.SourceTable} lookup {lookup.Name} has an invalid source.");
                if (lookup.Source == DatabaseSyncLookupSource.AuditChanges && lookup.ResultVersion is not (DatabaseSyncExpansionValueSource.SourceBefore or DatabaseSyncExpansionValueSource.SourceAfter))
                    throw new InvalidDataException($"Schema bridge {rule.SourceTable} audit lookup {lookup.Name} must select a Before or After result version.");
                var duplicateMatch = lookup.Matches.GroupBy(match => match.LookupColumn, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
                if (duplicateMatch is not null) throw new InvalidDataException($"Schema bridge {rule.SourceTable} lookup {lookup.Name} repeats match column {duplicateMatch.Key}.");
                foreach (var match in lookup.Matches)
                {
                    if (match.Input.Source == DatabaseSyncExpansionValueSource.Lookup) throw new InvalidDataException($"Schema bridge {rule.SourceTable} lookup {lookup.Name} cannot recursively depend on another lookup.");
                    ValidateValue(rule, match.Input, $"lookup {lookup.Name} match {match.LookupColumn}");
                }
            }
            var duplicateExpansion = expansions.GroupBy(expansion => expansion.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
            if (duplicateExpansion is not null) throw new InvalidDataException($"Schema bridge {rule.SourceTable} contains duplicate structural expansion name {duplicateExpansion.Key}.");
            foreach (var expansion in expansions)
            {
                if (string.IsNullOrWhiteSpace(expansion.Name)) throw new InvalidDataException($"Schema bridge {rule.SourceTable} contains an unnamed structural expansion.");
                if (expansion.SourceKinds.Count == 0) throw new InvalidDataException($"Structural expansion {rule.SourceTable}/{expansion.Name} selects no source change kinds.");
                if (expansion.SourceKinds.Any(kind => !Enum.IsDefined(kind)) || !Enum.IsDefined(expansion.TargetKind)) throw new InvalidDataException($"Structural expansion {rule.SourceTable}/{expansion.Name} contains an invalid source or target change kind.");
                if (expansion.SourceKinds.Distinct().Count() != expansion.SourceKinds.Count) throw new InvalidDataException($"Structural expansion {rule.SourceTable}/{expansion.Name} repeats a source change kind.");
                if (expansion.SourceKinds.Contains(LegacyDatabaseRowChangeKind.UnattributedCandidate) || expansion.TargetKind == LegacyDatabaseRowChangeKind.UnattributedCandidate)
                    throw new InvalidDataException($"Structural expansion {rule.SourceTable}/{expansion.Name} cannot use unattributed change kinds.");
                var duplicateExpansionKey = expansion.KeyBindings.GroupBy(binding => binding.TargetColumn, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
                if (duplicateExpansionKey is not null) throw new InvalidDataException($"Structural expansion {rule.SourceTable}/{expansion.Name} repeats key target {duplicateExpansionKey.Key}.");
                var duplicateExpansionField = expansion.FieldBindings.GroupBy(binding => binding.TargetColumn, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
                if (duplicateExpansionField is not null) throw new InvalidDataException($"Structural expansion {rule.SourceTable}/{expansion.Name} repeats field target {duplicateExpansionField.Key}.");
                var keyFieldOverlap = expansion.KeyBindings.Select(binding => binding.TargetColumn).Intersect(expansion.FieldBindings.Select(binding => binding.TargetColumn), StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (keyFieldOverlap is not null) throw new InvalidDataException($"Structural expansion {rule.SourceTable}/{expansion.Name} binds {keyFieldOverlap} as both key and field.");
                foreach (var binding in expansion.KeyBindings) ValidateExpansionValue(rule, expansion, binding.Value, $"key {binding.TargetColumn}");
                foreach (var binding in expansion.FieldBindings)
                {
                    if (binding.Before is not null) ValidateExpansionValue(rule, expansion, binding.Before, $"field {binding.TargetColumn} Before");
                    if (binding.After is not null) ValidateExpansionValue(rule, expansion, binding.After, $"field {binding.TargetColumn} After");
                }
            }
        }
        var sourceRules = profile.Tables.ToDictionary(rule => rule.SourceTable, StringComparer.OrdinalIgnoreCase);
        foreach (var rule in profile.Tables)
            foreach (var lookup in (rule.Lookups ?? []).Where(lookup => lookup.Source == DatabaseSyncLookupSource.AuditChanges && !string.IsNullOrWhiteSpace(lookup.Table)))
            {
                if (!sourceRules.TryGetValue(lookup.Table, out var lookupRule)) throw new InvalidDataException($"Schema bridge {rule.SourceTable} audit lookup {lookup.Name} references source table {lookup.Table}, which has no audited bridge rule/corpus description.");
                var observed = (lookupRule.ObservedSourceColumns ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(lookup.ResultColumn) && !observed.Contains(lookup.ResultColumn)) throw new InvalidDataException($"Schema bridge {rule.SourceTable} audit lookup {lookup.Name} result column {lookup.Table}.{lookup.ResultColumn} was not observed in the audit.");
                var unknownMatch = lookup.Matches.FirstOrDefault(match => !string.IsNullOrWhiteSpace(match.LookupColumn) && !observed.Contains(match.LookupColumn));
                if (unknownMatch is not null) throw new InvalidDataException($"Schema bridge {rule.SourceTable} audit lookup {lookup.Name} match column {lookup.Table}.{unknownMatch.LookupColumn} was not observed in the audit.");
            }
    }

    private static void ValidateExpansionValue(DatabaseSyncTableTranslation rule, DatabaseSyncRowExpansion expansion, DatabaseSyncExpansionValue value, string location)
    {
        ValidateValue(rule, value, $"structural expansion {expansion.Name} {location}");
        if (value.Source == DatabaseSyncExpansionValueSource.Lookup && !string.IsNullOrWhiteSpace(value.LookupName) && !(rule.Lookups ?? []).Any(lookup => lookup.Name.Equals(value.LookupName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Structural expansion {rule.SourceTable}/{expansion.Name} {location} references undeclared lookup {value.LookupName}.");
    }

    private static void ValidateValue(DatabaseSyncTableTranslation rule, DatabaseSyncExpansionValue value, string location)
    {
        if (!Enum.IsDefined(value.Source)) throw new InvalidDataException($"Schema bridge {rule.SourceTable} {location} uses an invalid value source.");
        if (value.Source == DatabaseSyncExpansionValueSource.Constant)
        {
            if (value.Constant is not null && !Enum.IsDefined(value.Constant.State)) throw new InvalidDataException($"Schema bridge {rule.SourceTable} {location} uses an invalid constant value state.");
            if (value.Constant?.State == LegacyDatabaseAuditValueState.Binary)
                try { _ = Convert.FromBase64String(value.Constant.Value ?? string.Empty); }
                catch (FormatException exception) { throw new InvalidDataException($"Schema bridge {rule.SourceTable} {location} contains invalid base64 bytes.", exception); }
        }
        else if (value.Source is DatabaseSyncExpansionValueSource.SourceBefore or DatabaseSyncExpansionValueSource.SourceAfter && !string.IsNullOrWhiteSpace(value.SourceColumn) && !(rule.ObservedSourceColumns ?? []).Contains(value.SourceColumn, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"Schema bridge {rule.SourceTable} {location} references unobserved source column {value.SourceColumn}.");
        if (value.Transform is null) return;
        if (!Enum.IsDefined(value.Transform.Kind)) throw new InvalidDataException($"Schema bridge {rule.SourceTable} {location} uses an invalid transform kind.");
        var duplicateMap = (value.Transform.Mappings ?? []).GroupBy(mapping => $"{(int)mapping.Match.State}\u001d{mapping.Match.Value}", StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicateMap is not null) throw new InvalidDataException($"Schema bridge {rule.SourceTable} {location} repeats an ExactMap input.");
        foreach (var mapping in value.Transform.Mappings ?? []) { ValidateTypedValue(mapping.Match, $"{rule.SourceTable} {location} map input"); ValidateTypedValue(mapping.Result, $"{rule.SourceTable} {location} map result"); }
        if (value.Transform.Fallback is not null) ValidateTypedValue(value.Transform.Fallback, $"{rule.SourceTable} {location} fallback");
    }

    private static void ValidateTypedValue(LegacyDatabaseAuditValue value, string location)
    {
        if (!Enum.IsDefined(value.State)) throw new InvalidDataException($"Schema bridge {location} uses an invalid typed value state.");
        if (value.State == LegacyDatabaseAuditValueState.Binary)
            try { _ = Convert.FromBase64String(value.Value ?? string.Empty); }
            catch (FormatException exception) { throw new InvalidDataException($"Schema bridge {location} contains invalid base64 bytes.", exception); }
    }
}
