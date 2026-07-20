using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoWCrucible.Core;

public sealed record DatabaseSyncColumnTranslation(string SourceColumn, string TargetColumn);
public sealed record DatabaseSyncTargetDefault(string TargetColumn, LegacyDatabaseAuditValue Value);
public sealed record DatabaseSyncTableTranslation(
    string SourceTable,
    string TargetTable,
    IReadOnlyList<DatabaseSyncColumnTranslation> ColumnMappings,
    IReadOnlyList<string> DroppedSourceColumns,
    IReadOnlyList<DatabaseSyncTargetDefault> TargetDefaults,
    IReadOnlyList<string>? ObservedSourceColumns = null,
    IReadOnlyList<string>? SourcePrimaryKeyColumns = null,
    bool RequiresInsertDefaults = false);
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
    int BlockedOperations);

/// <summary>
/// Exact one-row-to-one-row schema bridge used by legacy database recovery. The bridge deliberately supports
/// only explicit table/column renames, reviewed source-column drops, and typed target defaults. Structural
/// one-to-many conversions remain blocked so a compatibility profile can never silently invent content.
/// </summary>
public sealed class DatabaseSyncTranslationService
{
    public const string ProfileFormat = "wow-crucible-database-schema-bridge";
    public const int ProfileFormatVersion = 1;
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
        var evidenceCounts = new Dictionary<(string SourceTable, string TargetTable, string Action, string SourceColumn, string TargetColumn, string Description), int>();
        foreach (var operation in operations)
        {
            if (!rules.TryGetValue(operation.Table, out var rule))
            {
                translated.Add(Block(operation, $"Schema bridge has no rule for source table {operation.Table}."));
                continue;
            }
            var targetTable = string.IsNullOrWhiteSpace(rule.TargetTable) ? null : target.FindTable(rule.TargetTable);
            if (targetTable is null)
            {
                translated.Add(Block(operation, string.IsNullOrWhiteSpace(rule.TargetTable)
                    ? $"Schema bridge has no target table selected for {operation.Table}."
                    : $"Schema bridge target table {rule.TargetTable} does not exist in the bound target schema."));
                continue;
            }
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
                    Count("Key", part.Column, targetColumn.Name, $"Mapped primary-key column {operation.Table}.{part.Column} to {targetTable.Name}.{targetColumn.Name}.");
            }
            if (blocker is not null) { translated.Add(Block(operation, blocker)); continue; }
            foreach (var field in operation.Fields)
            {
                if (drops.Contains(field.Column))
                {
                    Count("Drop", field.Column, string.Empty, $"Explicitly omitted source field {operation.Table}.{field.Column}; its value is retained only in the verified audit.");
                    continue;
                }
                var targetColumn = ResolveColumn(field.Column, mappings, targetTable);
                if (targetColumn is null) { blocker = $"Schema bridge has no target column for {operation.Table}.{field.Column}."; break; }
                fields.Add(field with { Column = targetColumn.Name });
                if (mappings.ContainsKey(field.Column) || !field.Column.Equals(targetColumn.Name, StringComparison.OrdinalIgnoreCase))
                    Count("Column", field.Column, targetColumn.Name, $"Mapped {operation.Table}.{field.Column} to {targetTable.Name}.{targetColumn.Name}.");
            }
            if (blocker is not null) { translated.Add(Block(operation, blocker)); continue; }
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
                    Count("Default", string.Empty, targetColumn.Name, $"Added the explicit profile default for {targetTable.Name}.{targetColumn.Name}.");
                }
                if (blocker is null)
                {
                    var present = key.Select(part => part.Column).Concat(fields.Select(field => field.Column)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var required = targetTable.Columns.Where(column => !present.Contains(column.Name) && !column.Nullable && column.DefaultValue is null && !Generated(column)).Select(column => column.Name).ToArray();
                    if (required.Length > 0) blocker = $"Schema bridge leaves required target column(s) unresolved: {string.Join(", ", required)}.";
                }
            }
            if (blocker is null && operation.Kind == LegacyDatabaseRowChangeKind.Modified && fields.Count == 0) blocker = "Schema bridge removed every changed field from this UPDATE.";
            if (blocker is not null) { translated.Add(Block(operation, blocker)); continue; }
            var duplicateKey = key.GroupBy(part => part.Column, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
            var duplicateField = fields.GroupBy(field => field.Column, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
            if (duplicateKey is not null || duplicateField is not null)
            {
                translated.Add(Block(operation, $"Schema bridge maps multiple source columns onto target column {(duplicateKey?.Key ?? duplicateField!.Key)}."));
                continue;
            }
            if (!operation.Table.Equals(targetTable.Name, StringComparison.OrdinalIgnoreCase))
                Count("Table", string.Empty, string.Empty, $"Mapped source table {operation.Table} to target table {targetTable.Name}.");
            translated.Add(operation with { Table = targetTable.Name, Key = key, Fields = fields, Status = DatabaseSyncOperationStatus.Ready, Finding = "Schema bridge resolved; target comparison pending." });

            void Count(string action, string sourceColumn, string targetColumn, string description)
            {
                var evidenceKey = (operation.Table, targetTable.Name, action, sourceColumn, targetColumn, description);
                evidenceCounts[evidenceKey] = evidenceCounts.GetValueOrDefault(evidenceKey) + 1;
            }
        }
        var collapsed = translated.Select((operation, index) => (operation, index)).Where(value => value.operation.Status != DatabaseSyncOperationStatus.Blocked)
            .GroupBy(value => TargetIdentity(value.operation), StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).ToArray();
        foreach (var group in collapsed)
            foreach (var value in group)
                translated[value.index] = Block(value.operation, $"Multiple changed source rows map onto target identity {value.operation.Identity}.");
        var evidence = evidenceCounts.OrderBy(pair => pair.Key.SourceTable, StringComparer.OrdinalIgnoreCase).ThenBy(pair => pair.Key.Action, StringComparer.OrdinalIgnoreCase).ThenBy(pair => pair.Key.SourceColumn, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new DatabaseSyncTranslationEvidence(pair.Key.SourceTable, pair.Key.TargetTable, pair.Key.Action, pair.Key.SourceColumn, pair.Key.TargetColumn, pair.Value, pair.Key.Description)).ToArray();
        return new(translated, evidence, translated.Count(operation => operation.Status == DatabaseSyncOperationStatus.Blocked));
    }

    public async Task<DatabaseSyncTranslationProfile> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var profile = await JsonSerializer.DeserializeAsync<DatabaseSyncTranslationProfile>(stream, Json, cancellationToken) ?? throw new InvalidDataException("Schema bridge profile is empty.");
        ValidateStructure(profile); return profile;
    }

    public static async Task WriteAsync(string path, DatabaseSyncTranslationProfile profile, bool overwrite = false, CancellationToken cancellationToken = default)
    {
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
        if (profile.Format != ProfileFormat || profile.FormatVersion != ProfileFormatVersion) throw new InvalidDataException($"Unsupported schema bridge profile {profile.Format} v{profile.FormatVersion}.");
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
            var duplicateObserved = (rule.ObservedSourceColumns ?? []).GroupBy(column => column, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
            if (duplicateObserved is not null) throw new InvalidDataException($"Schema bridge {rule.SourceTable} observes {duplicateObserved.Key} more than once.");
            var unknownKey = (rule.SourcePrimaryKeyColumns ?? []).Except(rule.ObservedSourceColumns ?? [], StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (unknownKey is not null) throw new InvalidDataException($"Schema bridge {rule.SourceTable} marks unobserved column {unknownKey} as a source key.");
        }
    }
}
