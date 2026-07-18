using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoWCrucible.Core;

public enum LegacyDatabaseAuditMode { BaselineCompared, Unattributed }
public enum LegacyDatabaseAuditAttribution { BaselineDelta, Unattributed }
public enum LegacyDatabaseAuditValueState { Unknown, Missing, Null, Scalar, Binary }
public enum LegacyDatabaseRowChangeKind { Added, Modified, Removed, UnattributedCandidate }
public enum LegacyDatabaseTableAuditStatus
{
    Unchanged,
    Changed,
    SchemaChanged,
    LegacyTableOnly,
    BaselineTableOnly,
    Unattributed,
    NotCaptured,
    BlockedNoPrimaryKey,
    BlockedIncompatibleSchema
}
public enum LegacyDatabaseContentDomain
{
    ItemsAndSets,
    ClassesAndRaces,
    Pets,
    Spells,
    Creatures,
    VendorsAndLoot,
    Quests,
    GameObjects,
    DbcOverlays,
    WorldContent,
    Unclassified
}
public enum LegacyDatabaseBaselineIdentity { NotProvided, MatchingCoreIdentity, DifferentCoreIdentity, Unknown }

public sealed record LegacyDatabaseAuditOptions(
    IReadOnlyList<string>? IncludePatterns = null,
    IReadOnlyList<string>? ExcludePatterns = null,
    bool IncludeSensitiveState = false,
    bool Overwrite = false);

public sealed record LegacyDatabaseAuditProgress(string Stage, string? Table, long Rows, int CompletedTables, int TotalTables);

public sealed record LegacyDatabaseAuditValue(LegacyDatabaseAuditValueState State, string? Value)
{
    public static LegacyDatabaseAuditValue Unknown { get; } = new(LegacyDatabaseAuditValueState.Unknown, null);
    public static LegacyDatabaseAuditValue Missing { get; } = new(LegacyDatabaseAuditValueState.Missing, null);
    public static LegacyDatabaseAuditValue Null { get; } = new(LegacyDatabaseAuditValueState.Null, null);
}

public sealed record LegacyDatabaseAuditKeyPart(string Column, LegacyDatabaseAuditValue Value);
public sealed record LegacyDatabaseFieldChange(string Column, LegacyDatabaseAuditValue Baseline, LegacyDatabaseAuditValue Legacy);

public sealed record LegacyDatabaseRowChange(
    string Table,
    LegacyDatabaseContentDomain Domain,
    LegacyDatabaseRowChangeKind Kind,
    LegacyDatabaseAuditAttribution Attribution,
    IReadOnlyList<LegacyDatabaseAuditKeyPart> Key,
    IReadOnlyList<LegacyDatabaseFieldChange> Fields,
    bool PromotionApproved = false);

public sealed record LegacyDatabaseSnapshotAuditReference(
    string ArtifactSha256,
    string SchemaSha256,
    string ContentSha256,
    DateTimeOffset CapturedUtc,
    LegacyDatabaseSnapshotIdentity Source,
    bool SensitiveStateIncluded);

public sealed record LegacyDatabaseAuditPolicy(
    IReadOnlyList<string> IncludePatterns,
    IReadOnlyList<string> ExcludePatterns,
    bool SensitiveStateIncluded,
    IReadOnlyList<string> ExcludedTables);

public sealed record LegacyDatabaseTableAudit(
    string Name,
    LegacyDatabaseContentDomain Domain,
    LegacyDatabaseTableAuditStatus Status,
    IReadOnlyList<string> PrimaryKey,
    long BaselineRows,
    long LegacyRows,
    long AddedRows,
    long ModifiedRows,
    long RemovedRows,
    long UnattributedRows,
    long ChangedFields,
    string? DataEntry,
    long UncompressedBytes,
    string? ChangesSha256,
    IReadOnlyList<string> Findings)
{
    public long ChangeRecords => checked(AddedRows + ModifiedRows + RemovedRows + UnattributedRows);
}

public sealed record LegacyDatabaseAuditManifest(
    string Format,
    int FormatVersion,
    string ToolVersion,
    DateTimeOffset CreatedUtc,
    LegacyDatabaseAuditMode Mode,
    LegacyDatabaseBaselineIdentity BaselineIdentity,
    LegacyDatabaseSnapshotAuditReference? Baseline,
    LegacyDatabaseSnapshotAuditReference Legacy,
    LegacyDatabaseAuditPolicy Policy,
    string DomainCatalogVersion,
    IReadOnlyList<LegacyDatabaseTableAudit> Tables,
    long TotalChangeRecords,
    long TotalChangedFields,
    string ChangesSha256,
    bool PromotionReady,
    IReadOnlyList<string> Warnings);

public sealed record LegacyDatabaseAuditResult(string Path, LegacyDatabaseAuditManifest Manifest, long ArtifactBytes);
public sealed record LegacyDatabaseAuditInspection(LegacyDatabaseAuditManifest? Manifest, bool Valid, IReadOnlyList<string> Findings);

/// <summary>
/// Offline phase-two recovery audit. It compares immutable snapshot artifacts and deliberately has no database
/// connection or database-writing API. Its output is evidence for review, never an executable promotion plan.
/// </summary>
public sealed class LegacyDatabaseAuditService
{
    public const string ArtifactFormat = "wow-crucible-legacy-world-audit";
    public const int ArtifactFormatVersion = 1;
    public const string DomainCatalogVersion = "wotlk-world-domains-1";

    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<LegacyDatabaseAuditResult> AuditAsync(
        string legacySnapshotPath,
        string outputPath,
        string? baselineSnapshotPath = null,
        LegacyDatabaseAuditOptions? options = null,
        IProgress<LegacyDatabaseAuditProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new();
        var includes = ValidatePatterns(options.IncludePatterns, "include");
        var excludes = ValidatePatterns(options.ExcludePatterns, "exclude");
        var fullOutput = Path.GetFullPath(outputPath);
        var legacyPath = Path.GetFullPath(legacySnapshotPath);
        var baselinePath = string.IsNullOrWhiteSpace(baselineSnapshotPath) ? null : Path.GetFullPath(baselineSnapshotPath);
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (fullOutput.Equals(legacyPath, pathComparison) || (baselinePath is not null && fullOutput.Equals(baselinePath, pathComparison)))
            throw new ArgumentException("The recovery-audit output cannot replace either input snapshot.", nameof(outputPath));
        if (baselinePath is not null && legacyPath.Equals(baselinePath, pathComparison))
            throw new ArgumentException("The stock baseline and legacy-edited snapshot must be different input paths.", nameof(baselineSnapshotPath));
        var outputDirectory = Path.GetDirectoryName(fullOutput) ?? throw new ArgumentException("Audit output must have a parent directory.", nameof(outputPath));
        Directory.CreateDirectory(outputDirectory);
        if (File.Exists(fullOutput) && !options.Overwrite)
            throw new IOException($"Audit artifact already exists: {fullOutput}. Use overwrite explicitly if replacement is intended.");

        await using var legacyReadLock = new FileStream(legacyPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous);
        await using var baselineReadLock = baselinePath is null ? null : new FileStream(baselinePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous);
        progress?.Report(new("Validating legacy snapshot", null, 0, 0, 0));
        var snapshotService = new LegacyDatabaseSnapshotService();
        var legacyInspection = await snapshotService.InspectAsync(legacyPath, verifyRows: true, cancellationToken);
        if (!legacyInspection.Valid || legacyInspection.Manifest is null)
            throw new InvalidDataException($"Legacy snapshot validation failed: {string.Join("; ", legacyInspection.Findings)}");

        LegacyDatabaseSnapshotManifest? baselineManifest = null;
        if (baselinePath is not null)
        {
            progress?.Report(new("Validating baseline snapshot", null, 0, 0, 0));
            var baselineInspection = await snapshotService.InspectAsync(baselinePath, verifyRows: true, cancellationToken);
            if (!baselineInspection.Valid || baselineInspection.Manifest is null)
                throw new InvalidDataException($"Baseline snapshot validation failed: {string.Join("; ", baselineInspection.Findings)}");
            baselineManifest = baselineInspection.Manifest;
        }
        await using var legacy = new SnapshotHandle(legacyPath, legacyInspection.Manifest);
        await using var baseline = baselineManifest is null ? null : new SnapshotHandle(baselinePath!, baselineManifest);
        var legacyReference = await CreateReferenceAsync(legacyPath, legacyInspection.Manifest, cancellationToken);
        var baselineReference = baselineManifest is null ? null : await CreateReferenceAsync(baselinePath!, baselineManifest, cancellationToken);

        var legacyTables = CaseInsensitiveTables(legacyInspection.Manifest, "legacy");
        var baselineTables = baselineManifest is null ? new Dictionary<string, LegacyDatabaseSnapshotTable>(StringComparer.OrdinalIgnoreCase) : CaseInsensitiveTables(baselineManifest, "baseline");
        var legacyExcluded = CaseInsensitiveNames(legacyInspection.Manifest.Policy.ExcludedTables, "legacy excluded-table list");
        var baselineExcluded = baselineManifest is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : CaseInsensitiveNames(baselineManifest.Policy.ExcludedTables, "baseline excluded-table list");
        var names = (baselineManifest is null ? legacyTables.Keys : legacyTables.Keys.Concat(baselineTables.Keys))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var policyExcluded = new List<string>();
        names = names.Where(name =>
        {
            var included = includes.Length == 0 || includes.Any(pattern => LegacyDatabaseSnapshotService.GlobMatches(name, pattern));
            var excluded = excludes.Any(pattern => LegacyDatabaseSnapshotService.GlobMatches(name, pattern));
            var sensitive = !options.IncludeSensitiveState && LegacyDatabaseSnapshotService.IsSensitiveStateTable(name);
            if (!included || excluded || sensitive) policyExcluded.Add(name);
            return included && !excluded && !sensitive;
        }).ToArray();
        if (names.Length == 0) throw new InvalidOperationException("The audit filters selected zero captured world-content tables.");

        var warnings = BuildWarnings(baselineManifest, legacyInspection.Manifest, options.IncludeSensitiveState).ToList();
        if (baselineReference is not null && baselineReference.ArtifactSha256.Equals(legacyReference.ArtifactSha256, StringComparison.OrdinalIgnoreCase))
            warnings.Add("The stock baseline and legacy snapshots are byte-identical. The zero-delta result is valid, but verify that two different intended captures were supplied.");
        var identity = DetermineBaselineIdentity(baselineManifest, legacyInspection.Manifest);
        var temporary = Path.Combine(outputDirectory, $".{Path.GetFileName(fullOutput)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var tableAudits = new List<LegacyDatabaseTableAudit>(names.Length);
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
                {
                    for (var index = 0; index < names.Length; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var name = names[index];
                        progress?.Report(new("Auditing", name, 0, index, names.Length));
                        legacyTables.TryGetValue(name, out var legacyTable);
                        baselineTables.TryGetValue(name, out var baselineTable);
                        var tableAudit = await AuditTableAsync(archive, legacy, baseline, legacyTable, baselineTable,
                            legacyExcluded.Contains(name), baselineExcluded.Contains(name), baselineManifest is not null, cancellationToken);
                        tableAudits.Add(tableAudit);
                        progress?.Report(new("Audited", name, tableAudit.ChangeRecords, index + 1, names.Length));
                    }

                    var orderedTables = tableAudits.OrderBy(table => table.Name, StringComparer.Ordinal).ToArray();
                    var dataTables = orderedTables.Where(table => table.DataEntry is not null && table.ChangesSha256 is not null).ToArray();
                    var manifest = new LegacyDatabaseAuditManifest(
                        ArtifactFormat,
                        ArtifactFormatVersion,
                        ToolVersion(),
                        DateTimeOffset.UtcNow,
                        baselineManifest is null ? LegacyDatabaseAuditMode.Unattributed : LegacyDatabaseAuditMode.BaselineCompared,
                        identity,
                        baselineReference,
                        legacyReference,
                        new(includes, excludes, options.IncludeSensitiveState, policyExcluded),
                        DomainCatalogVersion,
                        orderedTables,
                        orderedTables.Sum(table => table.ChangeRecords),
                        orderedTables.Sum(table => table.ChangedFields),
                        LegacyDatabaseSnapshotService.ComputeAggregateHash(dataTables.Select(table => (table.Name, table.ChangesSha256!, table.ChangeRecords))),
                        false,
                        warnings);

                    var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                    manifestEntry.LastWriteTime = manifest.CreatedUtc;
                    await using var manifestStream = manifestEntry.Open();
                    await JsonSerializer.SerializeAsync(manifestStream, manifest, AuditJson, cancellationToken);
                }
                await file.FlushAsync(cancellationToken);
            }

            var completed = await InspectAsync(temporary, verifyChanges: true, cancellationToken);
            if (!completed.Valid || completed.Manifest is null)
            {
                throw new InvalidDataException($"New audit artifact failed its pre-publication check: {string.Join("; ", completed.Findings)}");
            }
            File.Move(temporary, fullOutput, options.Overwrite);
            return new(fullOutput, completed.Manifest, new FileInfo(fullOutput).Length);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    public async Task<LegacyDatabaseAuditInspection> InspectAsync(string artifactPath, bool verifyChanges = true, CancellationToken cancellationToken = default)
    {
        var findings = new List<string>();
        LegacyDatabaseAuditManifest? manifest = null;
        try
        {
            await using var file = new FileStream(Path.GetFullPath(artifactPath), FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false, Encoding.UTF8);
            var manifests = archive.Entries.Where(entry => entry.FullName.Equals("manifest.json", StringComparison.Ordinal)).ToArray();
            if (manifests.Length != 1) return new(null, false, [$"Artifact must contain exactly one manifest.json; found {manifests.Length}."]);
            if (manifests[0].Length > 64L * 1024 * 1024) return new(null, false, ["manifest.json exceeds the 64 MiB safety limit."]);
            await using (var stream = manifests[0].Open())
                manifest = await JsonSerializer.DeserializeAsync<LegacyDatabaseAuditManifest>(stream, AuditJson, cancellationToken);
            if (manifest is null) return new(null, false, ["manifest.json is empty or invalid."]);
            if (!manifest.Format.Equals(ArtifactFormat, StringComparison.Ordinal)) findings.Add($"Unsupported audit format '{manifest.Format}'.");
            if (manifest.FormatVersion != ArtifactFormatVersion) findings.Add($"Unsupported audit version {manifest.FormatVersion}.");
            if (!Enum.IsDefined(manifest.Mode)) findings.Add($"Undefined audit mode value: {manifest.Mode}.");
            if (!Enum.IsDefined(manifest.BaselineIdentity)) findings.Add($"Undefined baseline-identity value: {manifest.BaselineIdentity}.");
            if (manifest.PromotionReady) findings.Add("A recovery audit cannot claim to be an executable promotion plan.");
            if (manifest.Legacy is null) findings.Add("Manifest has no legacy snapshot reference.");
            else ValidateSnapshotReference("Legacy", manifest.Legacy, findings);
            if (manifest.Baseline is not null) ValidateSnapshotReference("Baseline", manifest.Baseline, findings);
            if (!IsSha256(manifest.ChangesSha256)) findings.Add("Manifest contains an invalid aggregate changes SHA-256 value.");
            if (manifest.Mode == LegacyDatabaseAuditMode.Unattributed && manifest.Baseline is not null) findings.Add("Unattributed audit unexpectedly declares a baseline.");
            if (manifest.Mode == LegacyDatabaseAuditMode.BaselineCompared && manifest.Baseline is null) findings.Add("Baseline-compared audit has no baseline reference.");
            if (manifest.Mode == LegacyDatabaseAuditMode.Unattributed && manifest.BaselineIdentity != LegacyDatabaseBaselineIdentity.NotProvided) findings.Add("Unattributed audit has an invalid baseline-identity classification.");
            if (manifest.Mode == LegacyDatabaseAuditMode.BaselineCompared && manifest.BaselineIdentity == LegacyDatabaseBaselineIdentity.NotProvided) findings.Add("Baseline-compared audit claims no baseline identity.");
            if (manifest.Tables is null) return new(manifest, false, findings.Append("Manifest has no table collection.").ToArray());
            if (manifest.Warnings is null) findings.Add("Manifest has no warning collection.");
            if (manifest.Policy is null) findings.Add("Manifest has no audit policy.");
            if (!string.Equals(manifest.DomainCatalogVersion, DomainCatalogVersion, StringComparison.Ordinal)) findings.Add($"Unsupported domain catalog '{manifest.DomainCatalogVersion}'.");

            var nullTables = manifest.Tables.Count(table => table is null);
            if (nullTables > 0) findings.Add($"Manifest contains {nullTables} null table record(s).");
            var tables = manifest.Tables.Where(table => table is not null).ToArray();
            var duplicateTables = tables.GroupBy(table => table.Name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
            if (duplicateTables.Length > 0) findings.Add($"Duplicate table metadata: {string.Join(", ", duplicateTables)}.");
            var declared = tables.Where(table => table.DataEntry is not null).Select(table => table.DataEntry!).Append("manifest.json").ToHashSet(StringComparer.Ordinal);
            foreach (var duplicate in archive.Entries.GroupBy(entry => entry.FullName, StringComparer.Ordinal).Where(group => group.Count() > 1)) findings.Add($"Duplicate ZIP entry: {duplicate.Key}.");
            foreach (var unexpected in archive.Entries.Where(entry => !declared.Contains(entry.FullName))) findings.Add($"Unexpected ZIP entry: {unexpected.FullName}.");

            foreach (var table in tables)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(table.Name)) { findings.Add("Manifest contains a table with no name."); continue; }
                if (!Enum.IsDefined(table.Domain)) findings.Add($"{table.Name}: undefined content-domain value {table.Domain}.");
                else if (table.Domain != LegacyDatabaseDomainCatalog.Classify(table.Name)) findings.Add($"{table.Name}: content domain {table.Domain} does not match catalog classification {LegacyDatabaseDomainCatalog.Classify(table.Name)}.");
                if (!Enum.IsDefined(table.Status)) findings.Add($"{table.Name}: undefined table-status value {table.Status}.");
                if (table.PrimaryKey is null) { findings.Add($"{table.Name}: primary-key metadata is null."); continue; }
                if (table.PrimaryKey.Any(string.IsNullOrWhiteSpace) || table.PrimaryKey.Distinct(StringComparer.OrdinalIgnoreCase).Count() != table.PrimaryKey.Count)
                    findings.Add($"{table.Name}: primary-key metadata contains empty or duplicate columns.");
                if (table.Findings is null) findings.Add($"{table.Name}: finding collection is null.");
                if (table.BaselineRows < 0 || table.LegacyRows < 0 || table.AddedRows < 0 || table.ModifiedRows < 0 || table.RemovedRows < 0 ||
                    table.UnattributedRows < 0 || table.ChangeRecords < 0 || table.ChangedFields < 0 || table.UncompressedBytes < 0)
                {
                    findings.Add($"{table.Name}: negative counts are invalid.");
                    continue;
                }
                ValidateTableStatus(manifest.Mode, table, findings);
                if (table.DataEntry is null)
                {
                    if (table.ChangeRecords != 0 || table.ChangesSha256 is not null || table.UncompressedBytes != 0) findings.Add($"{table.Name}: change counts or hashes exist without a data entry.");
                    continue;
                }
                var expectedEntry = $"tables/{Uri.EscapeDataString(table.Name)}.changes.json";
                if (!table.DataEntry.Equals(expectedEntry, StringComparison.Ordinal)) { findings.Add($"{table.Name}: invalid changes entry '{table.DataEntry}'."); continue; }
                if (!IsSha256(table.ChangesSha256)) { findings.Add($"{table.Name}: invalid changes SHA-256."); continue; }
                var entries = archive.Entries.Where(entry => entry.FullName.Equals(table.DataEntry, StringComparison.Ordinal)).ToArray();
                if (entries.Length != 1) { findings.Add($"{table.Name}: expected one changes entry, found {entries.Length}."); continue; }
                if (entries[0].Length != table.UncompressedBytes) findings.Add($"{table.Name}: uncompressed byte count mismatch.");
                await using var entryStream = entries[0].Open();
                using var hashing = new HashingReadStream(entryStream);
                var accumulator = new ChangeAccumulator();
                if (verifyChanges)
                {
                    await foreach (var change in JsonSerializer.DeserializeAsyncEnumerable<LegacyDatabaseRowChange>(hashing, AuditJson, cancellationToken))
                    {
                        if (change is null) { findings.Add($"{table.Name}: null change record."); break; }
                        if (!change.Table.Equals(table.Name, StringComparison.OrdinalIgnoreCase)) findings.Add($"{table.Name}: change record targets table '{change.Table}'.");
                        if (change.PromotionApproved) findings.Add($"{table.Name}: audit record improperly claims promotion approval.");
                        if (ValidateChangeRecord(manifest.Mode, table, change, findings)) accumulator.Add(change);
                    }
                }
                else await hashing.CopyToAsync(Stream.Null, cancellationToken);
                var hash = hashing.GetHashAndReset();
                if (!hash.Equals(table.ChangesSha256, StringComparison.OrdinalIgnoreCase)) findings.Add($"{table.Name}: changes hash mismatch.");
                if (hashing.BytesRead != table.UncompressedBytes) findings.Add($"{table.Name}: uncompressed byte count mismatch.");
                if (verifyChanges && !accumulator.Matches(table)) findings.Add($"{table.Name}: manifest change counts do not match its records.");
            }

            var dataTables = tables.Where(table => table.DataEntry is not null && IsSha256(table.ChangesSha256)).ToArray();
            var aggregate = LegacyDatabaseSnapshotService.ComputeAggregateHash(dataTables.OrderBy(table => table.Name, StringComparer.Ordinal)
                .Select(table => (table.Name, table.ChangesSha256!, table.ChangeRecords)));
            if (!aggregate.Equals(manifest.ChangesSha256, StringComparison.OrdinalIgnoreCase)) findings.Add("Aggregate changes hash mismatch.");
            if (tables.Sum(table => table.ChangeRecords) != manifest.TotalChangeRecords) findings.Add("Total change-record count mismatch.");
            if (tables.Sum(table => table.ChangedFields) != manifest.TotalChangedFields) findings.Add("Total changed-field count mismatch.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException and not AccessViolationException)
        {
            findings.Add(exception.Message);
        }
        return new(manifest, findings.Count == 0, findings);
    }

    public async IAsyncEnumerable<LegacyDatabaseRowChange> ReadChangesAsync(
        string artifactPath,
        string tableName,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(artifactPath);
        await using var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var inspection = await InspectAsync(fullPath, verifyChanges: true, cancellationToken);
        if (!inspection.Valid || inspection.Manifest is null)
            throw new InvalidDataException($"Audit validation failed before reading changes: {string.Join("; ", inspection.Findings)}");
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false, Encoding.UTF8);
        var manifest = inspection.Manifest;
        var table = manifest.Tables.SingleOrDefault(value => value.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new KeyNotFoundException($"Audit has no table named '{tableName}'.");
        if (table.DataEntry is null) yield break;
        var entry = archive.GetEntry(table.DataEntry) ?? throw new InvalidDataException($"Audit is missing '{table.DataEntry}'.");
        await using var entryStream = entry.Open();
        await foreach (var change in JsonSerializer.DeserializeAsyncEnumerable<LegacyDatabaseRowChange>(entryStream, AuditJson, cancellationToken))
            if (change is not null) yield return change;
    }

    private static async Task<LegacyDatabaseTableAudit> AuditTableAsync(
        ZipArchive output,
        SnapshotHandle legacy,
        SnapshotHandle? baseline,
        LegacyDatabaseSnapshotTable? legacyTable,
        LegacyDatabaseSnapshotTable? baselineTable,
        bool excludedFromLegacy,
        bool excludedFromBaseline,
        bool hasBaseline,
        CancellationToken cancellationToken)
    {
        var tableName = legacyTable?.Name ?? baselineTable?.Name ?? throw new InvalidOperationException("Table audit has no table metadata.");
        var domain = LegacyDatabaseDomainCatalog.Classify(tableName);
        var findings = new List<string>();
        var keyTable = legacyTable ?? baselineTable!;
        if (hasBaseline && ((legacyTable is null && excludedFromLegacy) || (baselineTable is null && excludedFromBaseline)))
        {
            findings.Add("The table was excluded from one snapshot, so absence cannot be interpreted as an addition or removal.");
            return Summary(LegacyDatabaseTableAuditStatus.NotCaptured);
        }
        if (keyTable.PrimaryKey.Count == 0)
        {
            if (baselineTable is not null && legacyTable is not null && baselineTable.RowsSha256.Equals(legacyTable.RowsSha256, StringComparison.OrdinalIgnoreCase) &&
                baselineTable.SchemaSha256.Equals(legacyTable.SchemaSha256, StringComparison.OrdinalIgnoreCase))
                return Summary(LegacyDatabaseTableAuditStatus.Unchanged);
            findings.Add("The table has no declared primary key. Capture order is not a stable identity, so row-level differences are deliberately blocked.");
            return Summary(LegacyDatabaseTableAuditStatus.BlockedNoPrimaryKey);
        }
        IReadOnlyList<ColumnPair>? pairs = null;
        if (baselineTable is not null && legacyTable is not null)
        {
            if (!TryMapCompatibleSchemas(baselineTable, legacyTable, findings, out pairs))
                return Summary(LegacyDatabaseTableAuditStatus.BlockedIncompatibleSchema);
            if (baselineTable.RowsSha256.Equals(legacyTable.RowsSha256, StringComparison.OrdinalIgnoreCase))
                return Summary(baselineTable.SchemaSha256.Equals(legacyTable.SchemaSha256, StringComparison.OrdinalIgnoreCase)
                    ? LegacyDatabaseTableAuditStatus.Unchanged : LegacyDatabaseTableAuditStatus.SchemaChanged);
        }
        var nonPortableKeyColumns = keyTable.PrimaryKey
            .Select(key => keyTable.Columns.Single(column => column.Name.Equals(key, StringComparison.OrdinalIgnoreCase)))
            .Where(column => !HasPortableSnapshotOrder(column))
            .Select(column => $"{column.Name} ({column.ColumnType}{(string.IsNullOrWhiteSpace(column.Collation) ? string.Empty : $", {column.Collation}")})")
            .ToArray();
        if (nonPortableKeyColumns.Length > 0)
        {
            findings.Add($"Snapshot format v1 cannot prove a portable primary-key order for: {string.Join(", ", nonPortableKeyColumns)}. " +
                         "The server captured rows using its own collation/type ordering, so row inference is blocked instead of risking a false add/remove merge.");
            return Summary(LegacyDatabaseTableAuditStatus.BlockedIncompatibleSchema);
        }

        var entryName = $"tables/{Uri.EscapeDataString(tableName)}.changes.json";
        var entry = output.CreateEntry(entryName, CompressionLevel.Fastest);
        entry.LastWriteTime = DateTimeOffset.UtcNow;
        await using var entryStream = entry.Open();
        using var hashing = new HashingWriteStream(entryStream);
        var accumulator = new ChangeAccumulator();
        using (var writer = new Utf8JsonWriter(hashing, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartArray();
            if (!hasBaseline)
                await WriteOneSidedAsync(legacy, legacyTable!, writer, accumulator, LegacyDatabaseRowChangeKind.UnattributedCandidate, LegacyDatabaseAuditAttribution.Unattributed, domain, cancellationToken);
            else if (baselineTable is null)
                await WriteOneSidedAsync(legacy, legacyTable!, writer, accumulator, LegacyDatabaseRowChangeKind.Added, LegacyDatabaseAuditAttribution.BaselineDelta, domain, cancellationToken);
            else if (legacyTable is null)
                await WriteOneSidedAsync(baseline!, baselineTable, writer, accumulator, LegacyDatabaseRowChangeKind.Removed, LegacyDatabaseAuditAttribution.BaselineDelta, domain, cancellationToken);
            else
                await WriteComparedAsync(baseline!, baselineTable, legacy, legacyTable, pairs!, writer, accumulator, domain, cancellationToken);
            writer.WriteEndArray();
            writer.Flush();
        }
        var changesHash = hashing.GetHashAndReset();
        var status = !hasBaseline ? LegacyDatabaseTableAuditStatus.Unattributed
            : baselineTable is null ? LegacyDatabaseTableAuditStatus.LegacyTableOnly
            : legacyTable is null ? LegacyDatabaseTableAuditStatus.BaselineTableOnly
            : accumulator.Records == 0 ? LegacyDatabaseTableAuditStatus.SchemaChanged
            : LegacyDatabaseTableAuditStatus.Changed;
        return new(tableName, domain, status, keyTable.PrimaryKey, baselineTable?.Rows ?? 0, legacyTable?.Rows ?? 0,
            accumulator.Added, accumulator.Modified, accumulator.Removed, accumulator.Unattributed, accumulator.Fields,
            entryName, hashing.BytesWritten, changesHash, findings);

        LegacyDatabaseTableAudit Summary(LegacyDatabaseTableAuditStatus status) => new(
            tableName, domain, status, keyTable.PrimaryKey, baselineTable?.Rows ?? 0, legacyTable?.Rows ?? 0,
            0, 0, 0, 0, 0, null, 0, null, findings);
    }

    private static async Task WriteComparedAsync(
        SnapshotHandle baseline,
        LegacyDatabaseSnapshotTable baselineTable,
        SnapshotHandle legacy,
        LegacyDatabaseSnapshotTable legacyTable,
        IReadOnlyList<ColumnPair> columns,
        Utf8JsonWriter writer,
        ChangeAccumulator accumulator,
        LegacyDatabaseContentDomain domain,
        CancellationToken cancellationToken)
    {
        await using var baselineRows = baseline.ReadRowsAsync(baselineTable, cancellationToken).GetAsyncEnumerator(cancellationToken);
        await using var legacyRows = legacy.ReadRowsAsync(legacyTable, cancellationToken).GetAsyncEnumerator(cancellationToken);
        SnapshotRow? previousBaseline = null, previousLegacy = null;
        var hasBaseline = await baselineRows.MoveNextAsync();
        var hasLegacy = await legacyRows.MoveNextAsync();
        while (hasBaseline || hasLegacy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (hasBaseline) ValidateAscending(previousBaseline, baselineRows.Current, baselineTable);
            if (hasLegacy) ValidateAscending(previousLegacy, legacyRows.Current, legacyTable);
            var comparison = !hasBaseline ? 1 : !hasLegacy ? -1 : CompareKeys(baselineRows.Current.Key, legacyRows.Current.Key, baselineTable);
            if (comparison < 0)
            {
                var change = CreateWholeRowChange(baselineTable, baselineRows.Current, LegacyDatabaseRowChangeKind.Removed, LegacyDatabaseAuditAttribution.BaselineDelta, domain);
                Write(writer, accumulator, change); previousBaseline = baselineRows.Current; hasBaseline = await baselineRows.MoveNextAsync();
            }
            else if (comparison > 0)
            {
                var change = CreateWholeRowChange(legacyTable, legacyRows.Current, LegacyDatabaseRowChangeKind.Added, LegacyDatabaseAuditAttribution.BaselineDelta, domain);
                Write(writer, accumulator, change); previousLegacy = legacyRows.Current; hasLegacy = await legacyRows.MoveNextAsync();
            }
            else
            {
                var fields = new List<LegacyDatabaseFieldChange>();
                foreach (var pair in columns)
                {
                    var before = baselineRows.Current.Values[pair.Baseline.Ordinal - 1];
                    var after = legacyRows.Current.Values[pair.Legacy.Ordinal - 1];
                    if (before != after) fields.Add(new(pair.Legacy.Name, before, after));
                }
                if (fields.Count > 0)
                {
                    var key = KeyParts(legacyTable, legacyRows.Current);
                    Write(writer, accumulator, new(legacyTable.Name, domain, LegacyDatabaseRowChangeKind.Modified,
                        LegacyDatabaseAuditAttribution.BaselineDelta, key, fields));
                }
                previousBaseline = baselineRows.Current; previousLegacy = legacyRows.Current;
                hasBaseline = await baselineRows.MoveNextAsync(); hasLegacy = await legacyRows.MoveNextAsync();
            }
        }
    }

    private static async Task WriteOneSidedAsync(
        SnapshotHandle source,
        LegacyDatabaseSnapshotTable table,
        Utf8JsonWriter writer,
        ChangeAccumulator accumulator,
        LegacyDatabaseRowChangeKind kind,
        LegacyDatabaseAuditAttribution attribution,
        LegacyDatabaseContentDomain domain,
        CancellationToken cancellationToken)
    {
        SnapshotRow? previous = null;
        await foreach (var row in source.ReadRowsAsync(table, cancellationToken))
        {
            ValidateAscending(previous, row, table);
            Write(writer, accumulator, CreateWholeRowChange(table, row, kind, attribution, domain));
            previous = row;
        }
    }

    private static LegacyDatabaseRowChange CreateWholeRowChange(
        LegacyDatabaseSnapshotTable table,
        SnapshotRow row,
        LegacyDatabaseRowChangeKind kind,
        LegacyDatabaseAuditAttribution attribution,
        LegacyDatabaseContentDomain domain)
    {
        var fields = table.Columns.Select(column => kind switch
        {
            LegacyDatabaseRowChangeKind.Added => new LegacyDatabaseFieldChange(column.Name, LegacyDatabaseAuditValue.Missing, row.Values[column.Ordinal - 1]),
            LegacyDatabaseRowChangeKind.Removed => new LegacyDatabaseFieldChange(column.Name, row.Values[column.Ordinal - 1], LegacyDatabaseAuditValue.Missing),
            LegacyDatabaseRowChangeKind.UnattributedCandidate => new LegacyDatabaseFieldChange(column.Name, LegacyDatabaseAuditValue.Unknown, row.Values[column.Ordinal - 1]),
            _ => throw new InvalidOperationException("Whole-row changes cannot be Modified.")
        }).ToArray();
        return new(table.Name, domain, kind, attribution, KeyParts(table, row), fields);
    }

    private static void Write(Utf8JsonWriter writer, ChangeAccumulator accumulator, LegacyDatabaseRowChange change)
    {
        JsonSerializer.Serialize(writer, change, AuditJson);
        accumulator.Add(change);
    }

    private static IReadOnlyList<LegacyDatabaseAuditKeyPart> KeyParts(LegacyDatabaseSnapshotTable table, SnapshotRow row) =>
        table.PrimaryKey.Select((name, index) => new LegacyDatabaseAuditKeyPart(name, row.Key[index])).ToArray();

    private static bool TryMapCompatibleSchemas(
        LegacyDatabaseSnapshotTable baseline,
        LegacyDatabaseSnapshotTable legacy,
        List<string> findings,
        out IReadOnlyList<ColumnPair> pairs)
    {
        pairs = [];
        if (!baseline.PrimaryKey.SequenceEqual(legacy.PrimaryKey, StringComparer.OrdinalIgnoreCase))
        {
            findings.Add($"Primary keys differ (baseline: {string.Join(',', baseline.PrimaryKey)}; legacy: {string.Join(',', legacy.PrimaryKey)}).");
            return false;
        }
        var baselineColumns = ToColumnDictionary(baseline, "baseline");
        var legacyColumns = ToColumnDictionary(legacy, "legacy");
        var missingLegacy = baselineColumns.Keys.Except(legacyColumns.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
        var addedLegacy = legacyColumns.Keys.Except(baselineColumns.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
        if (missingLegacy.Length > 0 || addedLegacy.Length > 0)
        {
            if (missingLegacy.Length > 0) findings.Add($"Legacy schema is missing column(s): {string.Join(", ", missingLegacy)}.");
            if (addedLegacy.Length > 0) findings.Add($"Legacy schema added column(s): {string.Join(", ", addedLegacy)}.");
            findings.Add("A partial-column comparison could misattribute a core schema migration, so row-level differences are blocked.");
            return false;
        }
        var mapped = new List<ColumnPair>(legacy.Columns.Count);
        foreach (var legacyColumn in legacy.Columns)
        {
            var baselineColumn = baselineColumns[legacyColumn.Name];
            if (!baselineColumn.DataType.Equals(legacyColumn.DataType, StringComparison.OrdinalIgnoreCase) || IsUnsigned(baselineColumn) != IsUnsigned(legacyColumn))
            {
                findings.Add($"Column '{legacyColumn.Name}' changed type from {baselineColumn.ColumnType} to {legacyColumn.ColumnType}.");
                return false;
            }
            if (!baselineColumn.ColumnType.Equals(legacyColumn.ColumnType, StringComparison.OrdinalIgnoreCase) ||
                baselineColumn.Nullable != legacyColumn.Nullable || !string.Equals(baselineColumn.Collation, legacyColumn.Collation, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(baselineColumn.Extra, legacyColumn.Extra, StringComparison.OrdinalIgnoreCase))
                findings.Add($"Column '{legacyColumn.Name}' has compatible values but changed schema metadata; promotion will require a target adapter.");
            mapped.Add(new(baselineColumn, legacyColumn));
        }
        if (!baseline.SchemaSha256.Equals(legacy.SchemaSha256, StringComparison.OrdinalIgnoreCase) && findings.Count == 0)
            findings.Add("Table metadata changed while its keyed columns remained value-compatible.");
        pairs = mapped;
        return true;
    }

    private static Dictionary<string, LegacyDatabaseSnapshotColumn> ToColumnDictionary(LegacyDatabaseSnapshotTable table, string label)
    {
        var result = new Dictionary<string, LegacyDatabaseSnapshotColumn>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in table.Columns)
            if (!result.TryAdd(column.Name, column)) throw new InvalidDataException($"{label} table '{table.Name}' contains columns that differ only by case: {column.Name}.");
        return result;
    }

    private static bool IsUnsigned(LegacyDatabaseSnapshotColumn column) => column.ColumnType.Contains("unsigned", StringComparison.OrdinalIgnoreCase);

    private static bool HasPortableSnapshotOrder(LegacyDatabaseSnapshotColumn column) => column.DataType.ToLowerInvariant() switch
    {
        "tinyint" or "smallint" or "mediumint" or "int" or "integer" or "bigint" or "year" or
        "decimal" or "numeric" or "bit" or "date" or "datetime" or
        "binary" or "varbinary" or "tinyblob" or "blob" or "mediumblob" or "longblob" => true,
        _ => false
    };

    private static int CompareKeys(IReadOnlyList<LegacyDatabaseAuditValue> left, IReadOnlyList<LegacyDatabaseAuditValue> right, LegacyDatabaseSnapshotTable schema)
    {
        for (var index = 0; index < schema.PrimaryKey.Count; index++)
        {
            var column = schema.Columns.Single(value => value.Name.Equals(schema.PrimaryKey[index], StringComparison.OrdinalIgnoreCase));
            var comparison = CompareKeyValue(left[index], right[index], column);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    private static int CompareKeyValue(LegacyDatabaseAuditValue left, LegacyDatabaseAuditValue right, LegacyDatabaseSnapshotColumn column)
    {
        if (left.State is LegacyDatabaseAuditValueState.Null or LegacyDatabaseAuditValueState.Missing or LegacyDatabaseAuditValueState.Unknown ||
            right.State is LegacyDatabaseAuditValueState.Null or LegacyDatabaseAuditValueState.Missing or LegacyDatabaseAuditValueState.Unknown)
            throw new InvalidDataException($"Primary-key column '{column.Name}' contains a null or unavailable value.");
        if (left.State != right.State) return left.State.CompareTo(right.State);
        if (left.State == LegacyDatabaseAuditValueState.Binary)
            return CompareBytes(Convert.FromBase64String(left.Value!), Convert.FromBase64String(right.Value!));
        var leftText = left.Value ?? string.Empty; var rightText = right.Value ?? string.Empty;
        return column.DataType.ToLowerInvariant() switch
        {
            "tinyint" or "smallint" or "mediumint" or "int" or "integer" or "bigint" or "year" or "bit" =>
                BigInteger.Parse(leftText, CultureInfo.InvariantCulture).CompareTo(BigInteger.Parse(rightText, CultureInfo.InvariantCulture)),
            "decimal" or "numeric" => CompareDecimal(leftText, rightText),
            "float" or "double" or "real" => throw new InvalidDataException($"Floating-point primary key '{column.Name}' is not a safe stable identity."),
            _ => StringComparer.Ordinal.Compare(leftText, rightText)
        };
    }

    private static int CompareDecimal(string left, string right)
    {
        static (bool Negative, string Whole, string Fraction) Parts(string text)
        {
            text = text.Trim(); var negative = text.StartsWith("-", StringComparison.Ordinal); if (negative || text.StartsWith("+", StringComparison.Ordinal)) text = text[1..];
            var split = text.Split('.', 2); var whole = split[0].TrimStart('0'); if (whole.Length == 0) whole = "0";
            var fraction = split.Length == 2 ? split[1].TrimEnd('0') : string.Empty; if (whole == "0" && fraction.Length == 0) negative = false;
            return (negative, whole, fraction);
        }
        var a = Parts(left); var b = Parts(right); if (a.Negative != b.Negative) return a.Negative ? -1 : 1;
        var sign = a.Negative ? -1 : 1; var comparison = a.Whole.Length.CompareTo(b.Whole.Length);
        if (comparison == 0) comparison = StringComparer.Ordinal.Compare(a.Whole, b.Whole);
        if (comparison == 0)
        {
            var length = Math.Max(a.Fraction.Length, b.Fraction.Length);
            comparison = StringComparer.Ordinal.Compare(a.Fraction.PadRight(length, '0'), b.Fraction.PadRight(length, '0'));
        }
        return comparison * sign;
    }

    private static int CompareBytes(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var common = Math.Min(left.Length, right.Length);
        for (var index = 0; index < common; index++) { var comparison = left[index].CompareTo(right[index]); if (comparison != 0) return comparison; }
        return left.Length.CompareTo(right.Length);
    }

    private static void ValidateAscending(SnapshotRow? previous, SnapshotRow current, LegacyDatabaseSnapshotTable table)
    {
        if (previous is null) return;
        var comparison = CompareKeys(previous.Key, current.Key, table);
        if (comparison >= 0) throw new InvalidDataException($"{table.Name}: primary keys are duplicated or are not in a safely comparable order. Row-level audit was not published.");
    }

    public static LegacyDatabaseBaselineIdentity DetermineBaselineIdentity(LegacyDatabaseSnapshotManifest? baseline, LegacyDatabaseSnapshotManifest legacy)
    {
        if (baseline is null) return LegacyDatabaseBaselineIdentity.NotProvided;
        if (!TryNormalizeCoreIdentity(baseline.Source.CoreIdentity, out var baselineIdentity) ||
            !TryNormalizeCoreIdentity(legacy.Source.CoreIdentity, out var legacyIdentity)) return LegacyDatabaseBaselineIdentity.Unknown;
        var common = baselineIdentity.Keys.Intersect(legacyIdentity.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
        if (common.Length == 0) return LegacyDatabaseBaselineIdentity.Unknown;
        if (common.Any(key => !string.Equals(baselineIdentity[key], legacyIdentity[key], StringComparison.OrdinalIgnoreCase)))
            return LegacyDatabaseBaselineIdentity.DifferentCoreIdentity;
        return baselineIdentity.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(legacyIdentity.Keys)
            ? LegacyDatabaseBaselineIdentity.MatchingCoreIdentity : LegacyDatabaseBaselineIdentity.Unknown;
    }

    private static bool TryNormalizeCoreIdentity(IReadOnlyDictionary<string, string> source, out Dictionary<string, string> normalized)
    {
        normalized = new(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source)
            if (string.IsNullOrWhiteSpace(pair.Key) || !normalized.TryAdd(pair.Key, pair.Value)) return false;
        return true;
    }

    private static IReadOnlyList<string> BuildWarnings(LegacyDatabaseSnapshotManifest? baseline, LegacyDatabaseSnapshotManifest legacy, bool includeSensitive)
    {
        var warnings = new List<string>();
        if (baseline is null) warnings.Add("No baseline was supplied. Every emitted row is an unattributed candidate, not a detected customization.");
        else
        {
            var identity = DetermineBaselineIdentity(baseline, legacy);
            if (identity == LegacyDatabaseBaselineIdentity.DifferentCoreIdentity) warnings.Add("Baseline and legacy core identity fields differ. Deltas may include upstream schema/content drift.");
            if (identity == LegacyDatabaseBaselineIdentity.Unknown) warnings.Add("Core identity metadata is absent, incomplete, or not fully comparable. The baseline relationship is unverified.");
        }
        warnings.Add("This artifact is read-only audit evidence. It is not approved or executable SQL, and removals are never selected implicitly.");
        if (includeSensitive) warnings.Add("Sensitive runtime-state tables were explicitly allowed; protect this artifact as potentially secret-bearing data.");
        else if (legacy.Policy.SensitiveStateIncluded || baseline?.Policy.SensitiveStateIncluded == true)
            warnings.Add("An input snapshot contains sensitive state, but the audit safety filter excluded known runtime-state tables.");
        return warnings;
    }

    private static async Task<LegacyDatabaseSnapshotAuditReference> CreateReferenceAsync(string path, LegacyDatabaseSnapshotManifest manifest, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return new(Convert.ToHexString(hash).ToLowerInvariant(), manifest.SchemaSha256, manifest.ContentSha256,
            manifest.CapturedUtc, manifest.Source, manifest.Policy.SensitiveStateIncluded);
    }

    private static Dictionary<string, LegacyDatabaseSnapshotTable> CaseInsensitiveTables(LegacyDatabaseSnapshotManifest manifest, string label)
    {
        var tables = new Dictionary<string, LegacyDatabaseSnapshotTable>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in manifest.Tables)
            if (!tables.TryAdd(table.Name, table)) throw new InvalidDataException($"The {label} snapshot has table names that differ only by case: {table.Name}.");
        return tables;
    }

    private static HashSet<string> CaseInsensitiveNames(IEnumerable<string> names, string label)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names) if (!result.Add(name)) throw new InvalidDataException($"The {label} has duplicate names: {name}.");
        return result;
    }

    private static string[] ValidatePatterns(IReadOnlyList<string>? patterns, string kind)
    {
        if (patterns is null) return [];
        if (patterns.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException($"Audit {kind} patterns cannot be empty.");
        return patterns.Select(pattern => pattern.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void ValidateSnapshotReference(string label, LegacyDatabaseSnapshotAuditReference reference, List<string> findings)
    {
        if (!IsSha256(reference.ArtifactSha256)) findings.Add($"{label} snapshot reference has an invalid artifact SHA-256.");
        if (!IsSha256(reference.SchemaSha256)) findings.Add($"{label} snapshot reference has an invalid schema SHA-256.");
        if (!IsSha256(reference.ContentSha256)) findings.Add($"{label} snapshot reference has an invalid content SHA-256.");
        if (reference.Source is null) findings.Add($"{label} snapshot reference has no source identity.");
    }

    private static bool ValidateChangeRecord(
        LegacyDatabaseAuditMode mode,
        LegacyDatabaseTableAudit table,
        LegacyDatabaseRowChange change,
        List<string> findings)
    {
        var valid = true;
        void Invalid(string message) { findings.Add($"{table.Name}: {message}"); valid = false; }

        if (!Enum.IsDefined(change.Kind)) Invalid($"undefined change-kind value {change.Kind}.");
        if (!Enum.IsDefined(change.Attribution)) Invalid($"undefined attribution value {change.Attribution}.");
        if (!Enum.IsDefined(change.Domain)) Invalid($"undefined content-domain value {change.Domain}.");
        if (change.Domain != table.Domain) Invalid($"change domain {change.Domain} does not match table domain {table.Domain}.");
        if (change.PromotionApproved) Invalid("change record improperly claims promotion approval.");

        if (mode == LegacyDatabaseAuditMode.Unattributed)
        {
            if (change.Kind != LegacyDatabaseRowChangeKind.UnattributedCandidate) Invalid("unattributed audit contains a baseline-delta change kind.");
            if (change.Attribution != LegacyDatabaseAuditAttribution.Unattributed) Invalid("unattributed audit contains baseline-delta attribution.");
        }
        else if (mode == LegacyDatabaseAuditMode.BaselineCompared)
        {
            if (change.Kind == LegacyDatabaseRowChangeKind.UnattributedCandidate) Invalid("baseline-compared audit contains an unattributed candidate.");
            if (change.Attribution != LegacyDatabaseAuditAttribution.BaselineDelta) Invalid("baseline-compared audit contains unattributed attribution.");
        }

        if (change.Key is null) Invalid("change key is null.");
        else
        {
            if (change.Key.Count != table.PrimaryKey.Count) Invalid($"change key has {change.Key.Count} part(s); expected {table.PrimaryKey.Count}.");
            var keyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < change.Key.Count; index++)
            {
                var part = change.Key[index];
                if (part is null) { Invalid("change key contains a null part."); continue; }
                if (!keyNames.Add(part.Column ?? string.Empty)) Invalid($"change key repeats column '{part.Column}'.");
                if (index >= table.PrimaryKey.Count || !string.Equals(part.Column, table.PrimaryKey[index], StringComparison.OrdinalIgnoreCase))
                    Invalid($"change key part {index + 1} targets '{part.Column}' instead of '{(index < table.PrimaryKey.Count ? table.PrimaryKey[index] : "<none>")}'.");
                if (!ValidateAuditValue(part.Value, allowSpecialStates: false, $"key column '{part.Column}'", findings, table.Name)) valid = false;
            }
        }

        if (change.Fields is null) Invalid("field-change collection is null.");
        else
        {
            if (change.Fields.Count == 0) Invalid("change record has no changed fields.");
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in change.Fields)
            {
                if (field is null) { Invalid("field-change collection contains null."); continue; }
                if (string.IsNullOrWhiteSpace(field.Column) || !columns.Add(field.Column)) Invalid($"field column '{field.Column}' is empty or duplicated.");
                if (!ValidateAuditValue(field.Baseline, allowSpecialStates: true, $"field '{field.Column}' baseline", findings, table.Name) |
                    !ValidateAuditValue(field.Legacy, allowSpecialStates: true, $"field '{field.Column}' legacy", findings, table.Name)) valid = false;
                if (field.Baseline is null || field.Legacy is null) continue;
                var before = field.Baseline.State; var after = field.Legacy.State;
                switch (change.Kind)
                {
                    case LegacyDatabaseRowChangeKind.Added:
                        if (before != LegacyDatabaseAuditValueState.Missing || after is LegacyDatabaseAuditValueState.Missing or LegacyDatabaseAuditValueState.Unknown)
                            Invalid($"added field '{field.Column}' does not use Missing → value semantics.");
                        break;
                    case LegacyDatabaseRowChangeKind.Removed:
                        if (after != LegacyDatabaseAuditValueState.Missing || before is LegacyDatabaseAuditValueState.Missing or LegacyDatabaseAuditValueState.Unknown)
                            Invalid($"removed field '{field.Column}' does not use value → Missing semantics.");
                        break;
                    case LegacyDatabaseRowChangeKind.UnattributedCandidate:
                        if (before != LegacyDatabaseAuditValueState.Unknown || after is LegacyDatabaseAuditValueState.Missing or LegacyDatabaseAuditValueState.Unknown)
                            Invalid($"unattributed field '{field.Column}' does not use Unknown → value semantics.");
                        break;
                    case LegacyDatabaseRowChangeKind.Modified:
                        if (before is LegacyDatabaseAuditValueState.Missing or LegacyDatabaseAuditValueState.Unknown ||
                            after is LegacyDatabaseAuditValueState.Missing or LegacyDatabaseAuditValueState.Unknown || field.Baseline == field.Legacy)
                            Invalid($"modified field '{field.Column}' does not contain two distinct concrete values.");
                        break;
                }
            }
        }
        return valid;
    }

    private static bool ValidateAuditValue(
        LegacyDatabaseAuditValue? value,
        bool allowSpecialStates,
        string context,
        List<string> findings,
        string tableName)
    {
        if (value is null) { findings.Add($"{tableName}: {context} value is null."); return false; }
        if (!Enum.IsDefined(value.State)) { findings.Add($"{tableName}: {context} has undefined value-state {value.State}."); return false; }
        var valid = value.State switch
        {
            LegacyDatabaseAuditValueState.Unknown or LegacyDatabaseAuditValueState.Missing or LegacyDatabaseAuditValueState.Null => value.Value is null,
            LegacyDatabaseAuditValueState.Scalar or LegacyDatabaseAuditValueState.Binary => value.Value is not null,
            _ => false
        };
        if (!allowSpecialStates && value.State is not (LegacyDatabaseAuditValueState.Scalar or LegacyDatabaseAuditValueState.Binary)) valid = false;
        if (value.State == LegacyDatabaseAuditValueState.Binary && value.Value is not null)
        {
            try { _ = Convert.FromBase64String(value.Value); }
            catch (FormatException) { valid = false; }
        }
        if (!valid) findings.Add($"{tableName}: {context} has an invalid {value.State} representation.");
        return valid;
    }

    private static void ValidateTableStatus(LegacyDatabaseAuditMode mode, LegacyDatabaseTableAudit table, List<string> findings)
    {
        var records = table.ChangeRecords;
        var invalid = table.Status switch
        {
            LegacyDatabaseTableAuditStatus.Unchanged or LegacyDatabaseTableAuditStatus.BlockedNoPrimaryKey or
                LegacyDatabaseTableAuditStatus.BlockedIncompatibleSchema or LegacyDatabaseTableAuditStatus.NotCaptured => records != 0 || table.DataEntry is not null,
            LegacyDatabaseTableAuditStatus.SchemaChanged => records != 0 || table.BaselineRows != table.LegacyRows,
            LegacyDatabaseTableAuditStatus.LegacyTableOnly => table.BaselineRows != 0 || table.AddedRows != table.LegacyRows ||
                table.ModifiedRows != 0 || table.RemovedRows != 0 || table.UnattributedRows != 0 || table.DataEntry is null,
            LegacyDatabaseTableAuditStatus.BaselineTableOnly => table.LegacyRows != 0 || table.RemovedRows != table.BaselineRows ||
                table.AddedRows != 0 || table.ModifiedRows != 0 || table.UnattributedRows != 0 || table.DataEntry is null,
            LegacyDatabaseTableAuditStatus.Unattributed => table.BaselineRows != 0 || table.UnattributedRows != table.LegacyRows ||
                table.AddedRows != 0 || table.ModifiedRows != 0 || table.RemovedRows != 0 || table.DataEntry is null,
            LegacyDatabaseTableAuditStatus.Changed => records == 0 || table.UnattributedRows != 0 || table.DataEntry is null ||
                table.BaselineRows - table.RemovedRows + table.AddedRows != table.LegacyRows,
            _ => true
        };
        if (table.Status == LegacyDatabaseTableAuditStatus.Unchanged && table.BaselineRows != table.LegacyRows) invalid = true;
        if (mode == LegacyDatabaseAuditMode.Unattributed && table.Status is LegacyDatabaseTableAuditStatus.Unchanged or
            LegacyDatabaseTableAuditStatus.Changed or LegacyDatabaseTableAuditStatus.SchemaChanged or LegacyDatabaseTableAuditStatus.LegacyTableOnly or
            LegacyDatabaseTableAuditStatus.BaselineTableOnly or LegacyDatabaseTableAuditStatus.NotCaptured) invalid = true;
        if (mode == LegacyDatabaseAuditMode.BaselineCompared && table.Status == LegacyDatabaseTableAuditStatus.Unattributed) invalid = true;
        if (invalid) findings.Add($"{table.Name}: table status {table.Status} is inconsistent with its change counts or data entry.");
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
    private static string ToolVersion() => Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                                           ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    private sealed record ColumnPair(LegacyDatabaseSnapshotColumn Baseline, LegacyDatabaseSnapshotColumn Legacy);
    private sealed record SnapshotRow(IReadOnlyList<LegacyDatabaseAuditValue> Values, IReadOnlyList<LegacyDatabaseAuditValue> Key);

    private sealed class SnapshotHandle : IAsyncDisposable
    {
        private readonly FileStream _file;
        private readonly ZipArchive _archive;
        public LegacyDatabaseSnapshotManifest Manifest { get; }

        public SnapshotHandle(string path, LegacyDatabaseSnapshotManifest manifest)
        {
            Manifest = manifest;
            _file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
            _archive = new(_file, ZipArchiveMode.Read, leaveOpen: true, Encoding.UTF8);
        }

        public async IAsyncEnumerable<SnapshotRow> ReadRowsAsync(LegacyDatabaseSnapshotTable table, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var entries = _archive.Entries.Where(entry => entry.FullName.Equals(table.DataEntry, StringComparison.Ordinal)).ToArray();
            if (entries.Length != 1) throw new InvalidDataException($"{table.Name}: expected one snapshot row entry, found {entries.Length}.");
            var keyOrdinals = table.PrimaryKey.Select(name => table.Columns.Single(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Ordinal - 1).ToArray();
            await using var stream = entries[0].Open();
            await foreach (var element in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(stream, cancellationToken: cancellationToken))
            {
                if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != table.Columns.Count)
                    throw new InvalidDataException($"{table.Name}: snapshot row does not match its declared column count.");
                var values = new LegacyDatabaseAuditValue[table.Columns.Count]; var index = 0;
                foreach (var value in element.EnumerateArray()) values[index++] = ParseValue(value, table.Columns[index - 1]);
                yield return new(values, keyOrdinals.Select(ordinal => values[ordinal]).ToArray());
            }
        }

        public ValueTask DisposeAsync()
        {
            _archive.Dispose();
            return _file.DisposeAsync();
        }
    }

    private static LegacyDatabaseAuditValue ParseValue(JsonElement element, LegacyDatabaseSnapshotColumn column)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            if (!column.Nullable) throw new InvalidDataException($"Non-nullable column '{column.Name}' contains null in the snapshot.");
            return LegacyDatabaseAuditValue.Null;
        }
        if (element.ValueKind == JsonValueKind.String)
        {
            if (IsBinaryDataType(column.DataType)) throw new InvalidDataException($"Binary column '{column.Name}' contains scalar text instead of a $binary value.");
            var value = element.GetString() ?? string.Empty;
            ValidateScalarValue(column, value);
            return new(LegacyDatabaseAuditValueState.Scalar, value);
        }
        if (element.ValueKind == JsonValueKind.Object)
        {
            var properties = element.EnumerateObject().ToArray();
            if (properties.Length == 1 && properties[0].NameEquals("$binary") && properties[0].Value.ValueKind == JsonValueKind.String)
            {
                if (!IsBinaryDataType(column.DataType)) throw new InvalidDataException($"Non-binary column '{column.Name}' contains a $binary snapshot value.");
                var value = properties[0].Value.GetString() ?? string.Empty;
                _ = Convert.FromBase64String(value);
                return new(LegacyDatabaseAuditValueState.Binary, value);
            }
        }
        throw new InvalidDataException($"Column '{column.Name}' contains a snapshot value that is neither null, scalar text, nor a $binary value.");
    }

    private static bool IsBinaryDataType(string dataType) => dataType.ToLowerInvariant() is
        "binary" or "varbinary" or "tinyblob" or "blob" or "mediumblob" or "longblob" or
        "geometry" or "point" or "linestring" or "polygon" or "multipoint" or "multilinestring" or "multipolygon" or "geometrycollection";

    private static void ValidateScalarValue(LegacyDatabaseSnapshotColumn column, string value)
    {
        var dataType = column.DataType.ToLowerInvariant();
        if (dataType is "tinyint" or "smallint" or "mediumint" or "int" or "integer" or "bigint" or "year" or "bit")
        {
            if (!BigInteger.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) ||
                (IsUnsigned(column) && integer.Sign < 0))
                throw new InvalidDataException($"Integer column '{column.Name}' contains invalid invariant scalar text '{value}'.");
            return;
        }
        if (dataType is "decimal" or "numeric")
        {
            if (!IsDecimalText(value) || (IsUnsigned(column) && value.Length > 0 && value[0] == '-'))
                throw new InvalidDataException($"Decimal column '{column.Name}' contains invalid invariant scalar text '{value}'.");
            return;
        }
        if (dataType is "date" or "datetime" &&
            !DateTime.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _) &&
            !(dataType == "date" && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
            throw new InvalidDataException($"Date column '{column.Name}' contains invalid round-trip scalar text '{value}'.");
    }

    private static bool IsDecimalText(string value)
    {
        if (value.Length == 0) return false;
        var index = value[0] is '+' or '-' ? 1 : 0;
        if (index == value.Length) return false;
        var digits = 0; var decimalPoint = false;
        for (; index < value.Length; index++)
        {
            if (value[index] is >= '0' and <= '9') { digits++; continue; }
            if (value[index] == '.' && !decimalPoint) { decimalPoint = true; continue; }
            return false;
        }
        return digits > 0;
    }

    private sealed class ChangeAccumulator
    {
        public long Added { get; private set; }
        public long Modified { get; private set; }
        public long Removed { get; private set; }
        public long Unattributed { get; private set; }
        public long Fields { get; private set; }
        public long Records => Added + Modified + Removed + Unattributed;
        public void Add(LegacyDatabaseRowChange change)
        {
            switch (change.Kind)
            {
                case LegacyDatabaseRowChangeKind.Added: Added++; break;
                case LegacyDatabaseRowChangeKind.Modified: Modified++; break;
                case LegacyDatabaseRowChangeKind.Removed: Removed++; break;
                case LegacyDatabaseRowChangeKind.UnattributedCandidate: Unattributed++; break;
            }
            Fields = checked(Fields + change.Fields.Count);
        }
        public bool Matches(LegacyDatabaseTableAudit table) => Added == table.AddedRows && Modified == table.ModifiedRows && Removed == table.RemovedRows &&
                                                               Unattributed == table.UnattributedRows && Fields == table.ChangedFields;
    }

    private sealed class HashingWriteStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public long BytesWritten { get; private set; }
        public string GetHashAndReset() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
        public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); _hash.AppendData(buffer, offset, count); BytesWritten += count; }
        public override void Write(ReadOnlySpan<byte> buffer) { inner.Write(buffer); _hash.AppendData(buffer); BytesWritten += buffer.Length; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { await inner.WriteAsync(buffer, cancellationToken); _hash.AppendData(buffer.Span); BytesWritten += buffer.Length; }
        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true; public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush(); public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class HashingReadStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public long BytesRead { get; private set; }
        public string GetHashAndReset() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
        public override int Read(byte[] buffer, int offset, int count) { var read = inner.Read(buffer, offset, count); if (read > 0) { _hash.AppendData(buffer, offset, read); BytesRead += read; } return read; }
        public override int Read(Span<byte> buffer) { var read = inner.Read(buffer); if (read > 0) { _hash.AppendData(buffer[..read]); BytesRead += read; } return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { var read = await inner.ReadAsync(buffer, cancellationToken); if (read > 0) { _hash.AppendData(buffer.Span[..read]); BytesRead += read; } return read; }
        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override int ReadByte() { Span<byte> value = stackalloc byte[1]; return Read(value) == 0 ? -1 : value[0]; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public static class LegacyDatabaseDomainCatalog
{
    public static LegacyDatabaseContentDomain Classify(string tableName)
    {
        var name = tableName.ToLowerInvariant();
        if (name.EndsWith("_dbc", StringComparison.Ordinal)) return LegacyDatabaseContentDomain.DbcOverlays;
        if (name is "npc_vendor" or "game_event_npc_vendor" || name.Contains("loot_template", StringComparison.Ordinal)) return LegacyDatabaseContentDomain.VendorsAndLoot;
        if (name.StartsWith("playercreateinfo", StringComparison.Ordinal) || name.StartsWith("player_class", StringComparison.Ordinal) ||
            name.StartsWith("player_race", StringComparison.Ordinal) || name.Contains("racestats", StringComparison.Ordinal)) return LegacyDatabaseContentDomain.ClassesAndRaces;
        if (name.StartsWith("item_template", StringComparison.Ordinal) || name.StartsWith("item_set", StringComparison.Ordinal) || name.StartsWith("itemset", StringComparison.Ordinal) || name.StartsWith("item_enchantment", StringComparison.Ordinal)) return LegacyDatabaseContentDomain.ItemsAndSets;
        if (name.StartsWith("pet_", StringComparison.Ordinal)) return LegacyDatabaseContentDomain.Pets;
        if (name.StartsWith("spell_", StringComparison.Ordinal) || name.StartsWith("spell", StringComparison.Ordinal)) return LegacyDatabaseContentDomain.Spells;
        if (name.StartsWith("creature", StringComparison.Ordinal) || name.StartsWith("npc_", StringComparison.Ordinal)) return LegacyDatabaseContentDomain.Creatures;
        if (name.StartsWith("quest", StringComparison.Ordinal)) return LegacyDatabaseContentDomain.Quests;
        if (name.StartsWith("gameobject", StringComparison.Ordinal)) return LegacyDatabaseContentDomain.GameObjects;
        if (name.Contains("template", StringComparison.Ordinal) || name.StartsWith("world_", StringComparison.Ordinal)) return LegacyDatabaseContentDomain.WorldContent;
        return LegacyDatabaseContentDomain.Unclassified;
    }
}
