namespace WoWCrucible.Core;

public sealed record FixedTableMutationAuditCase(
    string Table,
    string SourcePath,
    ClientTableContainerKind Container,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> Mutations,
    int SourceRows,
    int OutputRows,
    bool Passed,
    string Message,
    string? OutputPath = null,
    string? OutputSha256 = null,
    string? Diagnostic = null);

public sealed record FixedTableMutationAuditSummary(
    int Build,
    string DefinitionsRoot,
    string? XmlSchemaPath,
    string TableRoot,
    int ScannedTables,
    int EmptyPlaceholders,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<FixedTableMutationAuditCase> Cases,
    IReadOnlyList<string> Errors,
    string? ArtifactRoot)
{
    public IReadOnlyList<string> CoveredCapabilities => Cases.Where(result => result.Passed).SelectMany(result => result.Capabilities)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    public IReadOnlyList<string> MissingCapabilities => RequiredCapabilities.Except(CoveredCapabilities, StringComparer.OrdinalIgnoreCase).ToArray();
    public bool Passed => Errors.Count == 0 && Cases.Count == ScannedTables && Cases.Count > 0 && Cases.All(result => result.Passed) && MissingCapabilities.Count == 0;
}

public static class FixedTableMutationAuditService
{
    private static readonly string[] RequiredCapabilities =
    [
        "container:wdbc",
        "container:wdb2",
        "value:string",
        "value:float32",
        "width:8",
        "width:32",
        "array",
        "structural:allowed",
        "structural:blocked-side-tables"
    ];

    public static FixedTableMutationAuditSummary Audit(
        string definitionsRoot,
        string tableRoot,
        int build,
        string? artifactParent = null,
        string? xmlSchemaPath = null,
        CancellationToken cancellationToken = default,
        bool recursiveTableDiscovery = true)
    {
        definitionsRoot = RequiredDirectory(definitionsRoot, "WoWDBDefs definitions folder");
        tableRoot = RequiredDirectory(tableRoot, "fixed-layout client-table folder");
        xmlSchemaPath = string.IsNullOrWhiteSpace(xmlSchemaPath) ? null : Path.GetFullPath(xmlSchemaPath);
        if (xmlSchemaPath is not null && !File.Exists(xmlSchemaPath)) throw new FileNotFoundException("The WDBX XML schema does not exist.", xmlSchemaPath);
        var xmlSchema = xmlSchemaPath is null ? null : DbcSchemaCatalog.Load(xmlSchemaPath);
        if (build <= 0) throw new ArgumentOutOfRangeException(nameof(build), "A positive client build is required for a corpus mutation audit.");

        var parent = artifactParent is null
            ? Path.Combine(Path.GetTempPath(), "wow-crucible-fixed-table-mutation-audits")
            : Path.GetFullPath(artifactParent);
        Directory.CreateDirectory(parent);
        var artifactRoot = Path.Combine(parent, $"fixed-{build}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifactRoot);
        var keepArtifacts = artifactParent is not null;
        var results = new List<FixedTableMutationAuditCase>();
        var errors = new List<string>();
        var emptyPlaceholders = 0;
        var scanned = 0;
        try
        {
            var paths = Directory.EnumerateFiles(tableRoot, "*", recursiveTableDiscovery ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetExtension(path) is ".dbc" or ".db2" || Path.GetExtension(path).Equals(".dbc", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".db2", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (new FileInfo(path).Length == 0) { emptyPlaceholders++; continue; }
                try
                {
                    var file = WdbcFile.Load(path);
                    if (file.ContainerKind == ClientTableContainerKind.Wdc1) continue;
                    var schema = ResolveSchema(definitionsRoot, xmlSchema, build, file);
                    if (!schema.IsExactFor(file)) throw new InvalidDataException("The schema did not resolve exactly to the fixed-layout table.");
                    scanned++;
                    results.Add(RunCase(tableRoot, artifactRoot, definitionsRoot, xmlSchema, build, path, file, schema));
                }
                catch (Exception exception)
                {
                    errors.Add($"{Path.GetRelativePath(tableRoot, path)}: {exception.Message}");
                }
            }
        }
        finally
        {
            if (!keepArtifacts && Directory.Exists(artifactRoot)) Directory.Delete(artifactRoot, true);
        }

        return new(build, definitionsRoot, xmlSchemaPath, tableRoot, scanned, emptyPlaceholders, RequiredCapabilities, results, errors,
            keepArtifacts ? artifactRoot : null);
    }

    private static FixedTableMutationAuditCase RunCase(
        string tableRoot,
        string artifactRoot,
        string definitionsRoot,
        DbcSchemaCatalog? xmlSchema,
        int build,
        string sourcePath,
        WdbcFile source,
        DbcSchemaResolution schema)
    {
        var relative = Path.GetRelativePath(tableRoot, sourcePath);
        var output = Path.Combine(artifactRoot, "mutated", relative);
        var stableOutput = Path.Combine(artifactRoot, "stable", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        Directory.CreateDirectory(Path.GetDirectoryName(stableOutput)!);
        var mutations = new List<string>();
        var capabilities = Capabilities(source, schema.Columns);
        var sourceRows = source.RowCount;
        try
        {
            var key = DbcRecordIdentity.PhysicalColumn(schema.Columns, schema.KeyStrategy);
            if (source.RowCount == 0)
            {
                var row = source.AddBlankRow(key);
                mutations.Add($"add blank row {row:N0}");
                var valueColumn = MutableColumns(schema.Columns).FirstOrDefault();
                if (valueColumn is not null)
                {
                    ClientTableMutationPrimitives.Mutate(source, row, valueColumn, " [Crucible fixed mutation]");
                    mutations.Add($"set {valueColumn.Name}");
                }
            }
            else
            {
                foreach (var column in RepresentativeColumns(schema.Columns))
                {
                    ClientTableMutationPrimitives.Mutate(source, 0, column, " [Crucible fixed mutation]");
                    mutations.Add($"set {column.Name}");
                }

                if (source.AllowsStructuralMutation)
                {
                    var first = source.CloneRows(0, 2, key);
                    source.DeleteRows([first + 1]);
                    mutations.Add($"clone row 0 twice; delete row {first + 1:N0}; retain row {first:N0}");
                }
                else
                {
                    try
                    {
                        _ = source.CloneRow(0, key);
                        throw new InvalidDataException("A WDB2 with dependent side tables unexpectedly allowed structural mutation.");
                    }
                    catch (InvalidOperationException exception) when (exception.Message.Contains("side table", StringComparison.OrdinalIgnoreCase))
                    {
                        mutations.Add("verified structural mutation blocker for dependent WDB2 side tables");
                    }
                }
            }

            if (mutations.Count == 0) throw new InvalidDataException("No safe mutation surface was found for this table.");
            source.Save(output, createBackup: false);
            var reloaded = WdbcFile.Load(output);
            var reloadedSchema = ResolveSchema(definitionsRoot, xmlSchema, build, reloaded);
            if (source.ContainerKind != reloaded.ContainerKind) throw new InvalidDataException("The fixed-layout container changed during persistence.");
            ValidateWdb2Metadata(source.Db2Metadata, reloaded.Db2Metadata);
            ClientTableMutationPrimitives.CompareEveryCell(source, schema.Columns, reloaded, reloadedSchema.Columns);

            reloaded.Save(stableOutput, createBackup: false);
            var outputHash = ClientTableMutationPrimitives.Hash(output);
            if (!outputHash.Equals(ClientTableMutationPrimitives.Hash(stableOutput), StringComparison.Ordinal))
                throw new InvalidDataException("A second unchanged save of the mutated fixed-layout table was not byte-identical.");

            return new(source.LogicalTableName, sourcePath, source.ContainerKind, capabilities, mutations, sourceRows, reloaded.RowCount, true,
                $"Mutated, reloaded, compared {checked((long)reloaded.RowCount * schema.Columns.Count):N0} logical cells, and verified stable unchanged persistence.", output, outputHash);
        }
        catch (Exception exception)
        {
            return new(source.LogicalTableName, sourcePath, source.ContainerKind, capabilities, mutations, sourceRows, 0, false,
                exception.Message, File.Exists(output) ? output : null, File.Exists(output) ? ClientTableMutationPrimitives.Hash(output) : null,
                exception.ToString());
        }
    }

    private static IReadOnlyList<DbcColumn> RepresentativeColumns(IReadOnlyList<DbcColumn> columns)
    {
        var candidates = MutableColumns(columns).ToArray();
        if (candidates.Length == 0) return [];
        var selected = new Dictionary<string, DbcColumn>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in candidates)
        {
            selected.TryAdd($"type:{column.Type}", column);
            selected.TryAdd($"width:{column.Size}", column);
            if (column.Name.Contains('[', StringComparison.Ordinal)) selected.TryAdd("array", column);
        }
        return selected.Values.DistinctBy(column => column.Index).OrderBy(column => column.Index).ToArray();
    }

    private static IEnumerable<DbcColumn> MutableColumns(IReadOnlyList<DbcColumn> columns) => columns.Where(column =>
        !IsPadding(column) && !column.IsIndex);

    private static IReadOnlyList<string> Capabilities(WdbcFile file, IReadOnlyList<DbcColumn> columns)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"container:{file.ContainerKind.ToString().ToLowerInvariant()}",
            file.AllowsStructuralMutation ? "structural:allowed" : "structural:blocked-side-tables"
        };
        foreach (var column in columns)
        {
            result.Add($"width:{column.Size * 8}");
            if (column.Type == DbcValueType.StringOffset) result.Add("value:string");
            if (column.Type == DbcValueType.Float32) result.Add("value:float32");
            if (column.Name.Contains('[', StringComparison.Ordinal)) result.Add("array");
            if (IsPadding(column)) result.Add("padding");
        }
        return result.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void ValidateWdb2Metadata(Wdb2Metadata? expected, Wdb2Metadata? actual)
    {
        if (expected is null && actual is null) return;
        if (expected is null || actual is null || expected.TableHash != actual.TableHash || expected.Build != actual.Build ||
            expected.Timestamp != actual.Timestamp || expected.MinId != actual.MinId || expected.MaxId != actual.MaxId ||
            expected.Locale != actual.Locale || expected.CopyTableSize != actual.CopyTableSize ||
            !expected.IndexMap.SequenceEqual(actual.IndexMap) || !expected.StringLengths.SequenceEqual(actual.StringLengths))
            throw new InvalidDataException("WDB2 metadata or dependent side-table shape changed during persistence.");
    }

    private static bool IsPadding(DbcColumn column) => column.Name.Equals("Padding", StringComparison.OrdinalIgnoreCase) ||
        column.Name.StartsWith("Padding_", StringComparison.OrdinalIgnoreCase) || column.Name.StartsWith("Padding[", StringComparison.OrdinalIgnoreCase);

    private static DbcSchemaResolution ResolveSchema(string definitionsRoot, DbcSchemaCatalog? xmlSchema, int build, WdbcFile file)
    {
        var definition = Path.Combine(definitionsRoot, file.LogicalTableName + ".dbd");
        if (File.Exists(definition))
        {
            try { return DbdSchemaService.ResolveFile(definition, build, file); }
            catch (KeyNotFoundException) when (xmlSchema is not null) { }
        }
        if (xmlSchema is not null)
        {
            var resolution = xmlSchema.ResolveColumns(file.LogicalTableName, file.FieldCount);
            if (resolution.IsExactFor(file)) return resolution;
            throw new InvalidDataException($"The WDBX XML schema does not exactly match {file.LogicalTableName} ({file.FieldCount:N0} declared fields, {file.RecordSize:N0} record bytes).");
        }
        if (!File.Exists(definition)) throw new FileNotFoundException($"No DBD definition exists for {file.LogicalTableName}.", definition);
        throw new KeyNotFoundException($"{file.LogicalTableName}.dbd has no layout covering client build {build:N0}, and no exact WDBX XML fallback was supplied.");
    }

    private static string RequiredDirectory(string path, string label)
    {
        path = Path.GetFullPath(path ?? string.Empty);
        return Directory.Exists(path) ? path : throw new DirectoryNotFoundException($"{label} does not exist: {path}");
    }
}
