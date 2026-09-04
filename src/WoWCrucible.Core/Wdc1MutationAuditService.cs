namespace WoWCrucible.Core;

public sealed record Wdc1MutationAuditCase(
    string Table,
    string SourcePath,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> Mutations,
    int SourceRows,
    int OutputRows,
    bool Passed,
    string Message,
    string? OutputPath = null,
    string? OutputSha256 = null);

public sealed record Wdc1MutationAuditSummary(
    int Build,
    string DefinitionsRoot,
    string TableRoot,
    int ScannedTables,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<Wdc1MutationAuditCase> Cases,
    IReadOnlyList<string> Errors,
    string? ArtifactRoot)
{
    public IReadOnlyList<string> CoveredCapabilities => Cases.Where(result => result.Passed).SelectMany(result => result.Capabilities)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    public IReadOnlyList<string> MissingCapabilities => RequiredCapabilities.Except(CoveredCapabilities, StringComparer.OrdinalIgnoreCase).ToArray();
    public bool Passed => Errors.Count == 0 && Cases.Count > 0 && Cases.All(result => result.Passed) && MissingCapabilities.Count == 0;
}

public static class Wdc1MutationAuditService
{
    private static readonly string[] RequiredCapabilities =
    [
        "storage:none",
        "storage:immediate",
        "storage:common",
        "storage:pallet",
        "storage:palletarray",
        "offset-map",
        "copy-table",
        "relationship",
        "external-id",
        "inline-id",
        "empty-table"
    ];

    private sealed record Candidate(string Path, string Table, int Rows, IReadOnlyList<string> Capabilities);

    public static Wdc1MutationAuditSummary Audit(
        string definitionsRoot,
        string tableRoot,
        int build,
        string? artifactParent = null,
        CancellationToken cancellationToken = default)
    {
        definitionsRoot = RequiredDirectory(definitionsRoot, "WoWDBDefs definitions folder");
        tableRoot = RequiredDirectory(tableRoot, "WDC1 table folder");
        if (build <= 0) throw new ArgumentOutOfRangeException(nameof(build), "A positive client build is required for a corpus mutation audit.");

        var candidates = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var paths = Directory.EnumerateFiles(tableRoot, "*.db2", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var scanned = 0;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var file = WdbcFile.Load(path);
                if (file.ContainerKind != ClientTableContainerKind.Wdc1) continue;
                var definition = RequiredDefinition(definitionsRoot, file.LogicalTableName);
                var schema = DbdSchemaService.ResolveFile(definition, build, file);
                if (schema.MatchKind != DbcSchemaMatchKind.NamedMatch) throw new InvalidDataException("The schema did not resolve exactly.");
                scanned++;
                var metadata = file.Wdc1Metadata ?? throw new InvalidDataException("WDC1 metadata is unavailable.");
                var capabilities = Capabilities(metadata, file.RowCount);
                var candidate = new Candidate(path, file.LogicalTableName, file.RowCount, capabilities);
                foreach (var capability in capabilities)
                {
                    if (!candidates.TryGetValue(capability, out var current) || CandidateScore(candidate) < CandidateScore(current))
                        candidates[capability] = candidate;
                }
            }
            catch (Exception exception)
            {
                errors.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }

        var parent = artifactParent is null
            ? Path.Combine(Path.GetTempPath(), "wow-crucible-wdc1-mutation-audits")
            : Path.GetFullPath(artifactParent);
        Directory.CreateDirectory(parent);
        var artifactRoot = Path.Combine(parent, $"wdc1-{build}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifactRoot);
        var keepArtifacts = artifactParent is not null;
        var results = new List<Wdc1MutationAuditCase>();
        try
        {
            var selected = candidates.Values.DistinctBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
                .OrderBy(candidate => candidate.Rows).ThenBy(candidate => candidate.Table, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var candidate in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var covered = candidates.Where(pair => pair.Value.Path.Equals(candidate.Path, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key)
                    .Order(StringComparer.OrdinalIgnoreCase).ToArray();
                results.Add(RunCase(definitionsRoot, build, candidate, covered, artifactRoot));
            }
        }
        finally
        {
            if (!keepArtifacts && Directory.Exists(artifactRoot)) Directory.Delete(artifactRoot, true);
        }

        return new(build, definitionsRoot, tableRoot, scanned, RequiredCapabilities, results, errors, keepArtifacts ? artifactRoot : null);
    }

    private static Wdc1MutationAuditCase RunCase(string definitionsRoot, int build, Candidate candidate, IReadOnlyList<string> capabilities, string artifactRoot)
    {
        var mutations = new List<string>();
        var output = Path.Combine(artifactRoot, candidate.Table + ".mutated.db2");
        try
        {
            var source = WdbcFile.Load(candidate.Path);
            var definition = RequiredDefinition(definitionsRoot, source.LogicalTableName);
            var schema = DbdSchemaService.ResolveFile(definition, build, source);
            var sourceMetadata = source.Wdc1Metadata ?? throw new InvalidDataException("WDC1 metadata is unavailable.");
            var sourceRows = source.RowCount;
            var key = DbcRecordIdentity.PhysicalColumn(schema.Columns, schema.KeyStrategy);

            if (source.RowCount == 0)
            {
                var row = source.AddBlankRow(key);
                mutations.Add($"add blank row {row:N0}");
                var valueColumn = schema.Columns.FirstOrDefault(column => !column.IsIndex && column.StorageKind == DbcColumnStorageKind.Record);
                if (valueColumn is not null)
                {
                    ClientTableMutationPrimitives.Mutate(source, row, valueColumn, " [Crucible WDC1 mutation]");
                    mutations.Add($"set {valueColumn.Name}");
                }
            }
            else
            {
                var changedColumns = new HashSet<int>();
                foreach (var mode in sourceMetadata.FieldStorage.Select(field => field.Mode).Distinct())
                {
                    var storageIndexes = sourceMetadata.FieldStorage.Where(field => field.Mode == mode).Select(field => field.Index).ToHashSet();
                    var column = schema.Columns.FirstOrDefault(value =>
                        !value.IsIndex && value.StorageKind == DbcColumnStorageKind.Record && storageIndexes.Contains(value.StorageIndex) && changedColumns.Add(value.Index));
                    if (column is null) continue;
                    ClientTableMutationPrimitives.Mutate(source, 0, column, " [Crucible WDC1 mutation]");
                    mutations.Add($"set {column.Name} ({mode})");
                }
                if (sourceMetadata.HasRelationshipData)
                {
                    var relationship = schema.Columns.FirstOrDefault(column => column.StorageKind == DbcColumnStorageKind.Relationship);
                    if (relationship is not null)
                    {
                        ClientTableMutationPrimitives.Mutate(source, 0, relationship, " [Crucible WDC1 mutation]");
                        mutations.Add($"set {relationship.Name} (relationship)");
                    }
                }
                if (key is not null)
                {
                    var row = source.CloneRow(0, key);
                    mutations.Add($"clone row 0 as {source.GetRaw64(row, key):N0}");
                }
            }

            source.Save(output, createBackup: false);
            var reloaded = WdbcFile.Load(output);
            var reloadedSchema = DbdSchemaService.ResolveFile(definition, build, reloaded);
            ClientTableMutationPrimitives.CompareEveryCell(source, schema.Columns, reloaded, reloadedSchema.Columns);

            var canonical = reloaded.Wdc1Metadata ?? throw new InvalidDataException("The mutated output is not WDC1.");
            if (canonical.HasOffsetMap || canonical.CopyRecordCount != 0 || canonical.FieldStorage.Any(field => field.Mode != Wdc1StorageMode.None))
                throw new InvalidDataException("The dirty writer did not produce its declared canonical fixed-record WDC1 form.");
            if (sourceMetadata.HasExternalIds != canonical.HasExternalIds)
                throw new InvalidDataException("Canonical output changed the table's external-ID contract.");
            if (sourceMetadata.HasRelationshipData != canonical.HasRelationshipData)
                throw new InvalidDataException("Canonical output changed the table's relationship-data contract.");

            var stableOutput = Path.Combine(artifactRoot, candidate.Table + ".mutated-stable.db2");
            reloaded.Save(stableOutput, createBackup: false);
            var outputHash = ClientTableMutationPrimitives.Hash(output);
            if (!outputHash.Equals(ClientTableMutationPrimitives.Hash(stableOutput), StringComparison.Ordinal))
                throw new InvalidDataException("A second unchanged save of the canonical output was not byte-identical.");

            return new(candidate.Table, candidate.Path, capabilities, mutations, sourceRows, reloaded.RowCount, true,
                $"Mutated, canonicalized, reloaded, compared {checked((long)reloaded.RowCount * schema.Columns.Count):N0} logical cells, and verified stable unchanged persistence.", output, outputHash);
        }
        catch (Exception exception)
        {
            return new(candidate.Table, candidate.Path, capabilities, mutations, candidate.Rows, 0, false, exception.Message,
                File.Exists(output) ? output : null, File.Exists(output) ? ClientTableMutationPrimitives.Hash(output) : null);
        }
    }

    private static IReadOnlyList<string> Capabilities(Wdc1Metadata metadata, int rows)
    {
        var result = metadata.FieldStorage.Select(field => $"storage:{field.Mode.ToString().ToLowerInvariant()}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (metadata.HasOffsetMap) result.Add("offset-map");
        if (metadata.HasCopyTable) result.Add("copy-table");
        if (metadata.HasRelationshipData) result.Add("relationship");
        result.Add(metadata.HasExternalIds ? "external-id" : "inline-id");
        if (rows == 0) result.Add("empty-table");
        return result.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static long CandidateScore(Candidate candidate)
    {
        var emptyPenalty = candidate.Rows == 0 && !candidate.Capabilities.Contains("empty-table", StringComparer.OrdinalIgnoreCase) ? long.MaxValue / 2 : 0;
        return checked(emptyPenalty + Math.Max(1, candidate.Rows));
    }

    private static string RequiredDefinition(string definitionsRoot, string table)
    {
        var path = Path.Combine(definitionsRoot, table + ".dbd");
        return File.Exists(path) ? path : throw new FileNotFoundException($"No DBD definition exists for {table}.", path);
    }

    private static string RequiredDirectory(string path, string label)
    {
        path = Path.GetFullPath(path ?? string.Empty);
        return Directory.Exists(path) ? path : throw new DirectoryNotFoundException($"{label} does not exist: {path}");
    }

}
