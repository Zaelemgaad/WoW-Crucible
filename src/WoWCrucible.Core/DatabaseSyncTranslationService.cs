using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoWCrucible.Core;

public sealed record DatabaseSyncColumnTranslation(string SourceColumn, string TargetColumn);
public sealed record DatabaseSyncTargetDefault(string TargetColumn, LegacyDatabaseAuditValue Value);
public enum DatabaseSyncExpansionValueSource { SourceBefore, SourceAfter, Constant }
public sealed record DatabaseSyncExpansionValue(
    DatabaseSyncExpansionValueSource Source,
    string SourceColumn,
    LegacyDatabaseAuditValue? Constant = null);
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
    IReadOnlyList<DatabaseSyncRowExpansion>? Expansions = null);
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
    IReadOnlyList<int>? SourceOperationIndexes = null);

/// <summary>
/// Exact schema bridge used by legacy database recovery. Primary rows support explicit table/column renames,
/// reviewed source-column drops, and typed target defaults. Structural outputs are separately reviewed row
/// templates: every target key/preimage/postimage value names a source value or a typed constant. Nothing is
/// inferred from column similarity, and an incomplete expansion blocks instead of inventing content.
/// </summary>
public sealed class DatabaseSyncTranslationService
{
    public const string ProfileFormat = "wow-crucible-database-schema-bridge";
    public const int ProfileFormatVersion = 2;
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
        DatabaseCapabilities target)
    {
        ValidateProfile(profile, sourceAuditSha256, target);
        var rules = profile.Tables.ToDictionary(rule => rule.SourceTable, StringComparer.OrdinalIgnoreCase);
        var translated = new List<DatabaseSyncOperation>(operations.Count);
        var sourceIndexes = new List<int>(operations.Count);
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
                Add(TranslateExpansion(operation, expansion, target, (action, sourceColumn, targetColumn, description) =>
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
        return new(translated, evidence, translated.Count(operation => operation.Status == DatabaseSyncOperationStatus.Blocked), sourceIndexes);
    }

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
        DatabaseSyncRowExpansion expansion,
        DatabaseCapabilities target,
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
            if (!TryResolveExpansionValue(source, binding.Value, out var value, out var finding)) return Block(source, $"Structural expansion '{expansion.Name}' key {column.Name}: {finding}");
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
                if (!TryResolveExpansionValue(source, binding.After, out after, out var finding))
                    return Block(source, $"Structural expansion '{expansion.Name}' INSERT value {column.Name}: {finding}");
                CountBinding("ExpansionAfter", column.Name, binding.After);
            }
            else if (expansion.TargetKind == LegacyDatabaseRowChangeKind.Removed)
            {
                after = LegacyDatabaseAuditValue.Missing;
                if (binding.Before is null) return Block(source, $"Structural expansion '{expansion.Name}' DELETE preimage {column.Name}: a Before binding is required.");
                if (!TryResolveExpansionValue(source, binding.Before, out before, out var finding))
                    return Block(source, $"Structural expansion '{expansion.Name}' DELETE preimage {column.Name}: {finding}");
                CountBinding("ExpansionBefore", column.Name, binding.Before);
            }
            else
            {
                if (binding.Before is null) return Block(source, $"Structural expansion '{expansion.Name}' UPDATE preimage {column.Name}: a Before binding is required.");
                if (!TryResolveExpansionValue(source, binding.Before, out before, out var beforeFinding))
                    return Block(source, $"Structural expansion '{expansion.Name}' UPDATE preimage {column.Name}: {beforeFinding}");
                if (binding.After is null) return Block(source, $"Structural expansion '{expansion.Name}' UPDATE postimage {column.Name}: an After binding is required.");
                if (!TryResolveExpansionValue(source, binding.After, out after, out var afterFinding))
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
            var sourceColumn = value.Source == DatabaseSyncExpansionValueSource.Constant ? string.Empty : value.SourceColumn;
            var origin = value.Source == DatabaseSyncExpansionValueSource.Constant ? $"typed constant {DisplayValue(value.Constant!)}" : $"{value.Source} {source.Table}.{value.SourceColumn}";
            count(action, sourceColumn, targetColumn, $"Structural expansion '{expansion.Name}' binds {targetTable.Name}.{targetColumn} from {origin}.");
        }
    }

    private static bool TryResolveExpansionValue(DatabaseSyncOperation source, DatabaseSyncExpansionValue binding, out LegacyDatabaseAuditValue value, out string? finding)
    {
        if (binding.Source == DatabaseSyncExpansionValueSource.Constant)
        {
            value = binding.Constant ?? LegacyDatabaseAuditValue.Unknown;
            if (value.State is LegacyDatabaseAuditValueState.Unknown or LegacyDatabaseAuditValueState.Missing)
            { finding = "the typed constant is unresolved."; return false; }
            finding = null; return true;
        }
        if (string.IsNullOrWhiteSpace(binding.SourceColumn))
        { value = LegacyDatabaseAuditValue.Unknown; finding = "the source column is blank."; return false; }
        var key = source.Key.FirstOrDefault(part => part.Column.Equals(binding.SourceColumn, StringComparison.OrdinalIgnoreCase));
        if (key is not null) { value = key.Value; finding = null; return value.State is not LegacyDatabaseAuditValueState.Unknown and not LegacyDatabaseAuditValueState.Missing; }
        var field = source.Fields.FirstOrDefault(item => item.Column.Equals(binding.SourceColumn, StringComparison.OrdinalIgnoreCase));
        if (field is null)
        { value = LegacyDatabaseAuditValue.Unknown; finding = $"source column {source.Table}.{binding.SourceColumn} is not present in this audited operation."; return false; }
        value = binding.Source == DatabaseSyncExpansionValueSource.SourceBefore ? field.Before : field.After;
        if (value.State is LegacyDatabaseAuditValueState.Unknown or LegacyDatabaseAuditValueState.Missing)
        { finding = $"{binding.Source} {source.Table}.{binding.SourceColumn} is {value.State}."; return false; }
        finding = null; return true;
    }

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
            if (profile.FormatVersion == 1 && (rule.SuppressPrimaryOutput || expansions.Count > 0)) throw new InvalidDataException($"Schema bridge v1 cannot contain structural expansions for {rule.SourceTable}.");
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
    }

    private static void ValidateExpansionValue(DatabaseSyncTableTranslation rule, DatabaseSyncRowExpansion expansion, DatabaseSyncExpansionValue value, string location)
    {
        if (!Enum.IsDefined(value.Source)) throw new InvalidDataException($"Structural expansion {rule.SourceTable}/{expansion.Name} {location} uses an invalid value source.");
        if (value.Source == DatabaseSyncExpansionValueSource.Constant)
        {
            if (value.Constant is not null && !Enum.IsDefined(value.Constant.State)) throw new InvalidDataException($"Structural expansion {rule.SourceTable}/{expansion.Name} {location} uses an invalid constant value state.");
            if (value.Constant?.State == LegacyDatabaseAuditValueState.Binary)
                try { _ = Convert.FromBase64String(value.Constant.Value ?? string.Empty); }
                catch (FormatException exception) { throw new InvalidDataException($"Structural expansion {rule.SourceTable}/{expansion.Name} {location} contains invalid base64 bytes.", exception); }
            return;
        }
        if (string.IsNullOrWhiteSpace(value.SourceColumn)) return;
        if (!(rule.ObservedSourceColumns ?? []).Contains(value.SourceColumn, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"Structural expansion {rule.SourceTable}/{expansion.Name} {location} references unobserved source column {value.SourceColumn}.");
    }
}
