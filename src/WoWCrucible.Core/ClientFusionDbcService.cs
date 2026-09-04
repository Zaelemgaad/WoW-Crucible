using System.Security.Cryptography;
using System.Text.Json;

namespace WoWCrucible.Core;

public sealed record ClientFusionDbcAddition(uint Id, string SourceName, string SourcePath);
public sealed record ClientFusionDbcConflict(uint Id, string ExistingSource, string IncomingSource, IReadOnlyList<string> DifferingColumns);
public sealed record ClientFusionDbcTablePlan(
    string Table,
    string ArchivePath,
    string BasePath,
    string BaseSha256,
    IReadOnlyDictionary<string, string> CandidateSha256,
    string SchemaSourcePath,
    string SchemaSourceSha256,
    string KeyColumn,
    IReadOnlyList<ClientFusionDbcAddition> Additions,
    int ReusedRows,
    IReadOnlyList<ClientFusionDbcConflict> Conflicts,
    IReadOnlyList<string> Blockers)
{
    public bool Ready => Blockers.Count == 0 && Conflicts.Count == 0;
    public bool RequiresOutput => Ready && Additions.Count > 0;
}

public sealed record ClientFusionDbcPlan(
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    int Build,
    string? XmlSchemaPath,
    string? DefinitionsRoot,
    ClientFusionPlan FusionPlan,
    IReadOnlyList<ClientFusionDbcTablePlan> Tables,
    IReadOnlyList<string> Findings)
{
    public int ResolvableTables => Tables.Count(table => table.Ready);
    public int BlockedTables => Tables.Count - ResolvableTables;
}

public sealed record ClientFusionDbcResult(
    string OutputDirectory,
    string ReceiptPath,
    IReadOnlyDictionary<string, string> OutputFiles,
    IReadOnlyDictionary<string, string> OutputSha256,
    IReadOnlyList<string> OmittedArchivePaths,
    IReadOnlyList<string> BlockedArchivePaths,
    ClientFusionDbcPlan Plan);

/// <summary>
/// Resolves whole-file client-table collisions only when record semantics prove that the
/// sources are additive or equal. Different content at the same physical ID is
/// retained as a blocker until dependency-aware ID remapping can rewrite every
/// reference; it is never discarded by source order.
/// </summary>
public static class ClientFusionDbcService
{
    private const int FormatVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static ClientFusionDbcPlan CreatePlan(ClientFusionPlan fusionPlan, string? xmlSchemaPath,
        string? definitionsRoot = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fusionPlan);
        var schema = new ClientTableSchemaProvider(fusionPlan.TargetBuild, xmlSchemaPath, definitionsRoot);
        var tables = new List<ClientFusionDbcTablePlan>();
        var candidates = fusionPlan.Entries.Where(entry => entry.BaseFilePath is not null && ClientTableCompatibilityPolicy.IsClientTablePath(entry.ArchivePath) &&
            entry.Status is ClientFusionStatus.Override or ClientFusionStatus.Conflict or ClientFusionStatus.IdenticalCandidates)
            .OrderBy(entry => entry.ArchivePath, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var entry in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested(); var table = Path.GetFileNameWithoutExtension(entry.ArchivePath); var basePath = Path.GetFullPath(entry.BaseFilePath!); var hashes = entry.Candidates.OrderBy(candidate => candidate.SourceName, StringComparer.OrdinalIgnoreCase).ThenBy(candidate => candidate.FilePath, StringComparer.OrdinalIgnoreCase).ToDictionary(candidate => Path.GetFullPath(candidate.FilePath), candidate => Hash(candidate.FilePath), StringComparer.OrdinalIgnoreCase);
            try { tables.Add(AnalyzeTable(table, entry.ArchivePath, basePath, entry.Candidates, hashes, schema, cancellationToken)); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                tables.Add(new(table, entry.ArchivePath, basePath, Hash(basePath), hashes, string.Empty, string.Empty, string.Empty, [], 0, [], [exception.Message]));
            }
        }
        var findings = new List<string>
        {
            $"Inspected {tables.Count:N0} byte-different client-table path(s) against the selected effective base for build {fusionPlan.TargetBuild:N0}.",
            $"{tables.Count(table => table.Ready):N0} table(s) are semantically additive/equal; {tables.Count(table => !table.Ready):N0} retain genuine same-ID/layout/schema blockers.",
            "A different record at an occupied ID is not renumbered here because every cross-table reference must be rewritten as one dependency-bound operation."
        };
        return new(FormatVersion, DateTimeOffset.UtcNow, fusionPlan.TargetBuild, schema.XmlSchemaPath,
            schema.DefinitionsRoot, fusionPlan, tables, findings);
    }

    public static void SavePlan(string path, ClientFusionDbcPlan plan) => AtomicJson(path, plan);
    public static ClientFusionDbcPlan LoadPlan(string path)
    {
        var plan = JsonSerializer.Deserialize<ClientFusionDbcPlan>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("Client fusion DBC plan is empty.");
        if (plan.FormatVersion != FormatVersion) throw new InvalidDataException($"Unsupported client fusion DBC plan version {plan.FormatVersion}."); return plan;
    }

    public static ClientFusionDbcResult LoadResult(string receiptPath)
    {
        receiptPath = RequiredFile(receiptPath, "Client fusion DBC receipt"); var result = JsonSerializer.Deserialize<ClientFusionDbcResult>(File.ReadAllText(receiptPath), JsonOptions) ?? throw new InvalidDataException("Client fusion DBC receipt is empty."); VerifyResult(result);
        return result with { ReceiptPath = receiptPath, OutputDirectory = Path.GetFullPath(result.OutputDirectory) };
    }

    public static ClientFusionDbcResult Apply(ClientFusionDbcPlan plan, string outputDirectory, CancellationToken cancellationToken = default)
    {
        Verify(plan, cancellationToken); if (plan.Tables.Count == 0) throw new InvalidOperationException("The fusion plan has no byte-different base-backed client-table path to review.");
        outputDirectory = Path.GetFullPath(outputDirectory); if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any()) throw new IOException($"Client-table fusion output must be new or empty: {outputDirectory}");
        var parent = Path.GetDirectoryName(outputDirectory) ?? throw new InvalidOperationException("Client-table fusion output has no parent."); Directory.CreateDirectory(parent); var staging = Path.Combine(parent, $".{Path.GetFileName(outputDirectory)}.crucible-{Guid.NewGuid():N}"); Directory.CreateDirectory(staging);
        try
        {
            var schema = new ClientTableSchemaProvider(plan.Build, plan.XmlSchemaPath, plan.DefinitionsRoot); var stagedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var omitted = new List<string>();
            foreach (var table in plan.Tables.Where(table => table.Ready))
            {
                cancellationToken.ThrowIfCancellationRequested(); if (!table.RequiresOutput) { omitted.Add(table.ArchivePath); continue; }
                var file = WdbcFile.Load(table.BasePath); var resolution = schema.Resolve(file); RequirePhysicalSchema(table.Table, file, resolution); var key = DbcRecordIdentity.PhysicalColumn(resolution.Columns, resolution.KeyStrategy)!; var rows = DbcRecordIdentity.IndexRows(file, resolution.Columns, resolution.KeyStrategy); var sources = new Dictionary<string, (WdbcFile File, Dictionary<uint, int> Rows)>(StringComparer.OrdinalIgnoreCase);
                foreach (var addition in table.Additions)
                {
                    if (!sources.TryGetValue(addition.SourcePath, out var source)) { var sourceFile = WdbcFile.Load(addition.SourcePath); source = (sourceFile, DbcRecordIdentity.IndexRows(sourceFile, resolution.Columns, resolution.KeyStrategy)); sources[addition.SourcePath] = source; }
                    if (!source.Rows.TryGetValue(addition.Id, out var sourceRow)) throw new InvalidDataException($"{table.Table} source {addition.SourceName} no longer contains planned ID {addition.Id:N0}.");
                    if (rows.ContainsKey(addition.Id)) throw new InvalidDataException($"{table.Table} target ID {addition.Id:N0} became occupied while applying its additive plan.");
                    var destination = file.AddBlankRow(); CopyRow(source.File, sourceRow, file, destination, resolution.Columns); file.SetRaw(destination, key, addition.Id); rows[addition.Id] = destination;
                }
                var staged = Path.Combine(staging, Path.GetFileName(table.ArchivePath)); file.Save(staged, createBackup: false); stagedFiles[table.ArchivePath] = staged; hashes[table.ArchivePath] = Hash(staged);
            }
            var finalFiles = stagedFiles.ToDictionary(pair => pair.Key, pair => Path.Combine(outputDirectory, Path.GetFileName(pair.Value)), StringComparer.OrdinalIgnoreCase); var finalReceipt = Path.Combine(outputDirectory, "client-fusion-dbc.crucible.json"); var result = new ClientFusionDbcResult(outputDirectory, finalReceipt, finalFiles, hashes, omitted.Order(StringComparer.OrdinalIgnoreCase).ToArray(), plan.Tables.Where(table => !table.Ready).Select(table => table.ArchivePath).Order(StringComparer.OrdinalIgnoreCase).ToArray(), plan);
            File.WriteAllText(Path.Combine(staging, Path.GetFileName(finalReceipt)), JsonSerializer.Serialize(result, JsonOptions)); Directory.Move(staging, outputDirectory); return result;
        }
        catch { if (Directory.Exists(staging)) Directory.Delete(staging, true); throw; }
    }

    public static void Verify(ClientFusionDbcPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan); if (plan.FormatVersion != FormatVersion) throw new InvalidDataException($"Unsupported client-table fusion plan version {plan.FormatVersion}.");
        foreach (var table in plan.Tables)
        {
            if (!string.IsNullOrWhiteSpace(table.SchemaSourcePath) && !Hash(table.SchemaSourcePath).Equals(table.SchemaSourceSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Fusion schema for {table.Table} changed after planning.");
            if (!Hash(table.BasePath).Equals(table.BaseSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Fusion base {table.Table} changed after planning.");
            foreach (var pair in table.CandidateSha256) if (!Hash(pair.Key).Equals(pair.Value, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Fusion source {pair.Key} changed after planning.");
        }
        var recreated = CreatePlan(plan.FusionPlan, plan.XmlSchemaPath, plan.DefinitionsRoot, cancellationToken); if (JsonSerializer.Serialize(recreated.Tables) != JsonSerializer.Serialize(plan.Tables)) throw new InvalidDataException("The supplied client-table fusion operations do not match a fresh semantic analysis of their bound inputs.");
    }

    public static void VerifyResult(ClientFusionDbcResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result); Verify(result.Plan, cancellationToken); var expectedOutputs = result.Plan.Tables.Where(table => table.RequiresOutput).Select(table => table.ArchivePath).Order(StringComparer.OrdinalIgnoreCase).ToArray(); var actualOutputs = result.OutputFiles.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!expectedOutputs.SequenceEqual(actualOutputs, StringComparer.OrdinalIgnoreCase) || !actualOutputs.SequenceEqual(result.OutputSha256.Keys.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("DBC fusion receipt outputs do not match the resolvable additive table plan.");
        foreach (var path in actualOutputs) if (!Hash(result.OutputFiles[path]).Equals(result.OutputSha256[path], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Merged DBC output changed after creation: {result.OutputFiles[path]}");
        var omissions = result.Plan.Tables.Where(table => table.Ready && !table.RequiresOutput).Select(table => table.ArchivePath).Order(StringComparer.OrdinalIgnoreCase); if (!omissions.SequenceEqual(result.OmittedArchivePaths.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("DBC fusion receipt omissions do not match semantically base-equivalent tables.");
        var blocked = result.Plan.Tables.Where(table => !table.Ready).Select(table => table.ArchivePath).Order(StringComparer.OrdinalIgnoreCase); if (!blocked.SequenceEqual(result.BlockedArchivePaths.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("DBC fusion receipt blockers do not match the reviewed plan.");
    }

    private static ClientFusionDbcTablePlan AnalyzeTable(string table, string archivePath, string basePath, IReadOnlyList<ClientFusionCandidate> candidates, IReadOnlyDictionary<string, string> hashes, ClientTableSchemaProvider schema, CancellationToken cancellationToken)
    {
        var baseFile = WdbcFile.Load(basePath); var resolved = schema.ResolveWithSource(baseFile); var resolution = resolved.Resolution; RequirePhysicalSchema(table, baseFile, resolution); var key = DbcRecordIdentity.PhysicalColumn(resolution.Columns, resolution.KeyStrategy)!; var baseRows = DbcRecordIdentity.IndexRows(baseFile, resolution.Columns, resolution.KeyStrategy);
        var accumulated = baseRows.ToDictionary(pair => pair.Key, pair => new RowSource("effective base", basePath, baseFile, pair.Value)); var additions = new List<ClientFusionDbcAddition>(); var conflicts = new List<ClientFusionDbcConflict>(); var blockers = new List<string>(); var reused = 0;
        foreach (var candidate in candidates.OrderBy(candidate => candidate.SourceName, StringComparer.OrdinalIgnoreCase).ThenBy(candidate => candidate.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested(); WdbcFile source;
            try { source = WdbcFile.Load(candidate.FilePath); }
            catch (Exception exception) { blockers.Add($"{candidate.SourceName}: {exception.Message}"); continue; }
            if (!ClientTableCompatibilityPolicy.HasSameLayout(source, baseFile)) { blockers.Add($"{candidate.SourceName}: {source.ContainerKind} layout {source.FieldCount} fields/{source.RecordSize} bytes differs from base {baseFile.ContainerKind} {baseFile.FieldCount}/{baseFile.RecordSize}."); continue; }
            var rows = DbcRecordIdentity.IndexRows(source, resolution.Columns, resolution.KeyStrategy);
            foreach (var pair in rows.OrderBy(pair => pair.Key))
            {
                cancellationToken.ThrowIfCancellationRequested(); if (!accumulated.TryGetValue(pair.Key, out var existing)) { additions.Add(new(pair.Key, candidate.SourceName, Path.GetFullPath(candidate.FilePath))); accumulated[pair.Key] = new(candidate.SourceName, candidate.FilePath, source, pair.Value); continue; }
                var differences = Differences(existing.File, existing.Row, source, pair.Value, resolution.Columns, key); if (differences.Count == 0) { reused++; continue; }
                conflicts.Add(new(pair.Key, existing.Name, candidate.SourceName, differences));
            }
        }
        if (additions.Count > 0 && !baseFile.AllowsStructuralMutation)
            blockers.Add($"{baseFile.ContainerKind} side tables prevent safe row insertion for this table.");
        return new(table, PatchInputMapper.NormalizeArchivePath(archivePath), basePath, Hash(basePath), hashes,
            resolved.SourcePath, Hash(resolved.SourcePath), key.Name, additions, reused, conflicts, blockers);
    }

    private static IReadOnlyList<string> Differences(WdbcFile left, int leftRow, WdbcFile right, int rightRow, IReadOnlyList<DbcColumn> columns, DbcColumn key)
    {
        var result = new List<string>(); foreach (var column in columns.Where(column => column.Index != key.Index))
        {
            var equal = column.Type == DbcValueType.StringOffset ? left.GetString(left.GetRaw(leftRow, column)).Equals(right.GetString(right.GetRaw(rightRow, column)), StringComparison.Ordinal) : left.GetRaw64(leftRow, column) == right.GetRaw64(rightRow, column); if (!equal) result.Add(column.Name);
        }
        return result;
    }

    private static void CopyRow(WdbcFile source, int sourceRow, WdbcFile destination, int destinationRow, IReadOnlyList<DbcColumn> columns)
    {
        foreach (var column in columns) if (column.Type == DbcValueType.StringOffset) destination.SetDisplayValue(destinationRow, column, source.GetString(source.GetRaw(sourceRow, column))); else destination.SetRaw64(destinationRow, column, source.GetRaw64(sourceRow, column));
    }

    private static void RequirePhysicalSchema(string table, WdbcFile file, DbcSchemaResolution resolution)
    {
        if (!resolution.IsExactFor(file) || resolution.KeyStrategy.Kind != DbcRecordKeyKind.PhysicalColumn) throw new InvalidDataException($"{table} requires an exact named schema with a physical ID; resolved {resolution.MatchKind}, {resolution.DefinedFieldCount?.ToString("N0") ?? "-"}/{file.FieldCount:N0} declared fields, {resolution.ResolvedRecordSize:N0}/{file.RecordSize:N0} bytes, {resolution.KeyStrategy.Kind}.");
    }
    private static void AtomicJson<T>(string path, T value) { path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + ".tmp"; File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions)); File.Move(temporary, path, true); }
    private static string RequiredFile(string path, string label) { path = Path.GetFullPath(path); if (!File.Exists(path)) throw new FileNotFoundException($"{label} does not exist.", path); return path; }
    private static string Hash(string path) { using var stream = File.OpenRead(Path.GetFullPath(path)); return Convert.ToHexString(SHA256.HashData(stream)); }
    private sealed record RowSource(string Name, string Path, WdbcFile File, int Row);
}
