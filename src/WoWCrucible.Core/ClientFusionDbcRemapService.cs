using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public sealed record ClientFusionDbcRemapOperation(
    string SourceName,
    string SourceRoot,
    string SourcePath,
    uint SourceId,
    uint TargetId,
    bool AddsRow,
    string Resolution,
    IReadOnlyDictionary<string, uint> ReferenceRewrites);

public sealed record ClientFusionDbcRemapTablePlan(
    string Table,
    string ArchivePath,
    string BasePath,
    string BaseSha256,
    IReadOnlyDictionary<string, string> SourceSha256,
    string DbdPath,
    string DbdSha256,
    string KeyColumn,
    IReadOnlyList<ClientFusionDbcRemapOperation> Operations)
{
    public int AddedRows => Operations.Count(operation => operation.AddsRow);
    public int ReusedMappings => Operations.Count - AddedRows;
}

public sealed record ClientFusionDbcRemapPlan(
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    string SchemaPath,
    string SchemaSha256,
    string DefinitionsRoot,
    ClientFusionPlan FusionPlan,
    IReadOnlyList<ClientFusionDbcRemapTablePlan> Tables,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Findings)
{
    public bool Ready => Blockers.Count == 0 && Tables.Count > 0;
    public int AddedRows => Tables.Sum(table => table.AddedRows);
    public int ReusedMappings => Tables.Sum(table => table.ReusedMappings);
}

public sealed record ClientFusionDbcRemapResult(
    string OutputDirectory,
    string ReceiptPath,
    IReadOnlyDictionary<string, string> OutputFiles,
    IReadOnlyDictionary<string, string> OutputSha256,
    IReadOnlyList<string> OmittedArchivePaths,
    ClientFusionDbcRemapPlan Plan);

/// <summary>
/// Allocates new IDs for genuinely different occupied DBC records and propagates
/// those mappings through every source-layer DBC field whose WoWDBDefs column
/// declares a reference. Planning is global and fixed-point: a row that was byte
/// identical to the base is cloned when one of its referenced identities moves.
/// </summary>
public static class ClientFusionDbcRemapService
{
    private const int FormatVersion = 1;
    private const int Build = 12340;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static ClientFusionDbcRemapPlan CreatePlan(ClientFusionPlan fusionPlan, string schemaPath, string definitionsRoot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fusionPlan); schemaPath = RequiredFile(schemaPath, "DBC schema"); definitionsRoot = RequiredDirectory(definitionsRoot, "WoWDBDefs definitions root"); var schema = DbcSchemaCatalog.Load(schemaPath); var blockers = new List<string>(); var contexts = new Dictionary<string, TableContext>(StringComparer.OrdinalIgnoreCase);
        var entries = fusionPlan.Entries.Where(entry => entry.BaseFilePath is not null && IsDbc(entry.ArchivePath)).OrderBy(entry => entry.ArchivePath, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested(); var table = Path.GetFileNameWithoutExtension(entry.ArchivePath); try { contexts[table] = OpenTable(fusionPlan, entry, table, schema, definitionsRoot); }
            catch (Exception exception) when (exception is not OperationCanceledException) { blockers.Add($"{table}: {exception.Message}"); }
        }
        if (entries.Length == 0) blockers.Add("The fusion plan contains no base-backed DBC supplied by an override layer.");

        var states = new Dictionary<StateKey, PlannedRow>();
        foreach (var context in contexts.Values.OrderBy(context => context.Table, StringComparer.OrdinalIgnoreCase))
        foreach (var layer in context.Layers.Values.OrderBy(layer => layer.Source.Name, StringComparer.OrdinalIgnoreCase))
        foreach (var pair in layer.Rows.OrderBy(pair => pair.Key))
        {
            cancellationToken.ThrowIfCancellationRequested(); if (!context.BaseRows.TryGetValue(pair.Key, out var baseRow) || !RowsEqual(new(context.BaseFile, baseRow, EmptyRewrites), new(layer.File, pair.Value, EmptyRewrites), context.Columns, context.Key)) AddState(context, layer, pair.Key, pair.Value, states);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal); var stable = false; var iterations = Math.Max(8, states.Count + contexts.Values.Sum(context => context.Layers.Values.Sum(layer => layer.Rows.Count)) + 2);
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var added = false;
            foreach (var context in contexts.Values.OrderBy(context => context.Table, StringComparer.OrdinalIgnoreCase))
            foreach (var layer in context.Layers.Values.OrderBy(layer => layer.Source.Name, StringComparer.OrdinalIgnoreCase))
            foreach (var pair in layer.Rows.OrderBy(pair => pair.Key))
            {
                var key = StateKeyFor(layer.Source.RootPath, context.Table, pair.Key); var rewrites = ResolveReferences(context, layer.Source.RootPath, layer.File, pair.Value, states);
                if (states.TryGetValue(key, out var state)) state.ReferenceRewrites = rewrites;
                else if (rewrites.Count > 0 && (!context.BaseRows.TryGetValue(pair.Key, out var baseRow) || !RowsEqual(new(context.BaseFile, baseRow, EmptyRewrites), new(layer.File, pair.Value, rewrites), context.Columns, context.Key))) { state = AddState(context, layer, pair.Key, pair.Value, states); state.ReferenceRewrites = rewrites; added = true; }
            }

            var changed = false;
            foreach (var context in contexts.Values.OrderBy(context => context.Table, StringComparer.OrdinalIgnoreCase))
            {
                var catalog = new Dictionary<string, List<CatalogRow>>(StringComparer.Ordinal);
                foreach (var pair in context.BaseRows.OrderBy(pair => pair.Key)) AddCatalog(catalog, context, new(context.BaseFile, pair.Value, EmptyRewrites), pair.Key);
                foreach (var state in states.Values.Where(state => state.Context == context).OrderBy(state => state.Layer.Source.Name, StringComparer.OrdinalIgnoreCase).ThenBy(state => state.SourceId))
                {
                    var view = new RowView(state.Layer.File, state.SourceRow, state.ReferenceRewrites); var equivalent = FindEquivalent(catalog, context, view); var target = equivalent?.TargetId ?? state.AllocatedId; var adds = equivalent is null;
                    if (state.TargetId != target || state.AddsRow != adds) changed = true; state.TargetId = target; state.AddsRow = adds; state.Resolution = equivalent is null ? $"Clone different source ID {state.SourceId:N0} as new target ID {target:N0}." : $"Reuse semantically equivalent target ID {target:N0}.";
                    if (adds) AddCatalog(catalog, context, view, target);
                }
            }
            var signature = Signature(states); if (!seen.Add(signature)) { if (!changed && !added) { stable = true; break; } blockers.Add("Reference remapping entered a non-converging dependency cycle; no output can be trusted."); break; }
            if (!changed && !added) { stable = true; break; }
        }
        if (!stable && blockers.All(blocker => !blocker.Contains("non-converging", StringComparison.OrdinalIgnoreCase))) blockers.Add("Reference remapping did not reach a stable fixed point within its bounded iteration count.");

        var tables = contexts.Values.OrderBy(context => context.Table, StringComparer.OrdinalIgnoreCase).Select(context => new ClientFusionDbcRemapTablePlan(
            context.Table, context.ArchivePath, context.BasePath, Hash(context.BasePath), context.Layers.Values.ToDictionary(layer => layer.Path, layer => Hash(layer.Path), StringComparer.OrdinalIgnoreCase), context.DbdPath, Hash(context.DbdPath), context.Key.Name,
            states.Values.Where(state => state.Context == context).OrderBy(state => state.Layer.Source.Name, StringComparer.OrdinalIgnoreCase).ThenBy(state => state.SourceId).Select(state => new ClientFusionDbcRemapOperation(state.Layer.Source.Name, state.Layer.Source.RootPath, state.Layer.Path, state.SourceId, state.TargetId, state.AddsRow, state.Resolution, new Dictionary<string, uint>(state.ReferenceRewrites, StringComparer.OrdinalIgnoreCase))).ToArray())).ToArray();
        var findings = new[]
        {
            $"Dependency plan covers {tables.Length:N0} base-backed DBC table(s), {tables.Sum(table => table.Operations.Count):N0} changed/propagated source row(s), and {tables.Sum(table => table.AddedRows):N0} additive target row(s).",
            $"Reused {tables.Sum(table => table.ReusedMappings):N0} semantically equivalent mapping(s); no duplicate row is emitted for those source identities.",
            "Only DBD-declared DBC-to-DBC references are rewritten. SQL, scripts, executable code, and unnamed binary references remain outside this client-table plan and require their own coordinated change plan."
        };
        return new(FormatVersion, DateTimeOffset.UtcNow, schemaPath, Hash(schemaPath), definitionsRoot, fusionPlan, tables, blockers.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(), findings);
    }

    public static void SavePlan(string path, ClientFusionDbcRemapPlan plan) => AtomicJson(path, plan);
    public static ClientFusionDbcRemapPlan LoadPlan(string path)
    {
        var plan = JsonSerializer.Deserialize<ClientFusionDbcRemapPlan>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("DBC dependency-remap plan is empty."); if (plan.FormatVersion != FormatVersion) throw new InvalidDataException($"Unsupported DBC dependency-remap plan version {plan.FormatVersion}."); return plan;
    }

    public static ClientFusionDbcRemapResult Apply(ClientFusionDbcRemapPlan plan, string outputDirectory, CancellationToken cancellationToken = default)
    {
        Verify(plan, cancellationToken); if (!plan.Ready) throw new InvalidOperationException($"DBC dependency-remap plan has {plan.Blockers.Count:N0} blocker(s)."); outputDirectory = Path.GetFullPath(outputDirectory); if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any()) throw new IOException($"DBC remap output must be new or empty: {outputDirectory}");
        var parent = Path.GetDirectoryName(outputDirectory) ?? throw new InvalidOperationException("DBC remap output has no parent."); Directory.CreateDirectory(parent); var staging = Path.Combine(parent, $".{Path.GetFileName(outputDirectory)}.crucible-{Guid.NewGuid():N}"); Directory.CreateDirectory(staging);
        try
        {
            var schema = DbcSchemaCatalog.Load(plan.SchemaPath); var outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var omitted = new List<string>();
            foreach (var table in plan.Tables)
            {
                cancellationToken.ThrowIfCancellationRequested(); var additions = table.Operations.Where(operation => operation.AddsRow).ToArray(); if (additions.Length == 0) { omitted.Add(table.ArchivePath); continue; }
                var target = WdbcFile.Load(table.BasePath); var resolution = schema.ResolveColumns(table.Table, target.FieldCount); RequirePhysicalSchema(table.Table, target, resolution); var key = DbcRecordIdentity.PhysicalColumn(resolution.Columns, resolution.KeyStrategy)!; var targetRows = DbcRecordIdentity.IndexRows(target, resolution.Columns, resolution.KeyStrategy); var sources = new Dictionary<string, (WdbcFile File, Dictionary<uint, int> Rows)>(StringComparer.OrdinalIgnoreCase);
                foreach (var operation in additions.OrderBy(operation => operation.TargetId))
                {
                    if (!sources.TryGetValue(operation.SourcePath, out var source)) { var file = WdbcFile.Load(operation.SourcePath); source = (file, DbcRecordIdentity.IndexRows(file, resolution.Columns, resolution.KeyStrategy)); sources[operation.SourcePath] = source; }
                    if (!source.Rows.TryGetValue(operation.SourceId, out var sourceRow)) throw new InvalidDataException($"{table.Table} source ID {operation.SourceId:N0} disappeared after planning."); if (targetRows.ContainsKey(operation.TargetId)) throw new InvalidDataException($"{table.Table} target ID {operation.TargetId:N0} became occupied after planning.");
                    var row = target.AddBlankRow(); foreach (var column in resolution.Columns) { if (column.Type == DbcValueType.StringOffset) target.SetDisplayValue(row, column, source.File.GetString(source.File.GetRaw(sourceRow, column))); else target.SetRaw(row, column, operation.ReferenceRewrites.GetValueOrDefault(column.Name, source.File.GetRaw(sourceRow, column))); } target.SetRaw(row, key, operation.TargetId); targetRows[operation.TargetId] = row;
                }
                var staged = Path.Combine(staging, table.Table + ".dbc"); target.Save(staged, createBackup: false); outputs[table.ArchivePath] = staged; hashes[table.ArchivePath] = Hash(staged);
            }
            var finalFiles = outputs.ToDictionary(pair => pair.Key, pair => Path.Combine(outputDirectory, Path.GetFileName(pair.Value)), StringComparer.OrdinalIgnoreCase); var receipt = Path.Combine(outputDirectory, "client-fusion-dbc-remap.crucible.json"); var result = new ClientFusionDbcRemapResult(outputDirectory, receipt, finalFiles, hashes, omitted.Order(StringComparer.OrdinalIgnoreCase).ToArray(), plan); File.WriteAllText(Path.Combine(staging, Path.GetFileName(receipt)), JsonSerializer.Serialize(result, JsonOptions)); Directory.Move(staging, outputDirectory); return result;
        }
        catch { if (Directory.Exists(staging)) Directory.Delete(staging, true); throw; }
    }

    public static ClientFusionDbcRemapResult LoadResult(string receiptPath)
    {
        receiptPath = RequiredFile(receiptPath, "DBC dependency-remap receipt"); var result = JsonSerializer.Deserialize<ClientFusionDbcRemapResult>(File.ReadAllText(receiptPath), JsonOptions) ?? throw new InvalidDataException("DBC dependency-remap receipt is empty."); VerifyResult(result); return result with { ReceiptPath = receiptPath, OutputDirectory = Path.GetFullPath(result.OutputDirectory) };
    }

    public static void Verify(ClientFusionDbcRemapPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan); if (plan.FormatVersion != FormatVersion) throw new InvalidDataException($"Unsupported DBC dependency-remap plan version {plan.FormatVersion}."); if (!Hash(plan.SchemaPath).Equals(plan.SchemaSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("DBC remap schema changed after planning.");
        foreach (var table in plan.Tables) { if (!Hash(table.BasePath).Equals(table.BaseSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"DBC remap base {table.Table} changed after planning."); if (!Hash(table.DbdPath).Equals(table.DbdSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"DBC definition {table.DbdPath} changed after planning."); foreach (var pair in table.SourceSha256) if (!Hash(pair.Key).Equals(pair.Value, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"DBC remap source {pair.Key} changed after planning."); }
        var recreated = CreatePlan(plan.FusionPlan, plan.SchemaPath, plan.DefinitionsRoot, cancellationToken); if (JsonSerializer.Serialize(recreated.Tables) != JsonSerializer.Serialize(plan.Tables) || !recreated.Blockers.SequenceEqual(plan.Blockers, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("DBC dependency-remap operations do not match a fresh fixed-point analysis of their bound inputs.");
    }

    public static void VerifyResult(ClientFusionDbcRemapResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result); Verify(result.Plan, cancellationToken); var expected = result.Plan.Tables.Where(table => table.AddedRows > 0).Select(table => table.ArchivePath).Order(StringComparer.OrdinalIgnoreCase).ToArray(); var actual = result.OutputFiles.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray(); if (!expected.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase) || !actual.SequenceEqual(result.OutputSha256.Keys.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("DBC dependency-remap receipt outputs do not match its plan."); foreach (var path in actual) if (!Hash(result.OutputFiles[path]).Equals(result.OutputSha256[path], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"DBC dependency-remap output changed after creation: {result.OutputFiles[path]}"); var omissions = result.Plan.Tables.Where(table => table.AddedRows == 0).Select(table => table.ArchivePath).Order(StringComparer.OrdinalIgnoreCase); if (!omissions.SequenceEqual(result.OmittedArchivePaths.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("DBC dependency-remap receipt omissions do not match its plan.");
    }

    private static TableContext OpenTable(ClientFusionPlan fusionPlan, ClientFusionEntry entry, string table, DbcSchemaCatalog schema, string definitionsRoot)
    {
        var basePath = Path.GetFullPath(entry.BaseFilePath!); var baseFile = WdbcFile.Load(basePath); var resolution = schema.ResolveColumns(table, baseFile.FieldCount); RequirePhysicalSchema(table, baseFile, resolution); var key = DbcRecordIdentity.PhysicalColumn(resolution.Columns, resolution.KeyStrategy)!; var dbdPath = RequiredFile(Path.Combine(definitionsRoot, table + ".dbd"), $"{table} WoWDBDefs definition"); var dbd = DbdSchemaService.Load(dbdPath); if (dbd.ForBuild(Build) is null) throw new InvalidDataException($"{table}.dbd has no layout covering build {Build:N0}.");
        var references = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); foreach (var column in resolution.Columns) { var logicalName = column.Name.Split('[', 2)[0]; if (!dbd.Columns.TryGetValue(logicalName, out var definition) || string.IsNullOrWhiteSpace(definition.Reference)) continue; var separator = definition.Reference.IndexOf("::", StringComparison.Ordinal); var target = (separator < 0 ? definition.Reference : definition.Reference[..separator]).Trim(); if (target.Length > 0) references[column.Name] = target; }
        var layers = new Dictionary<string, LayerTable>(StringComparer.OrdinalIgnoreCase); foreach (var source in fusionPlan.Sources.OrderBy(source => source.Name, StringComparer.OrdinalIgnoreCase))
        {
            var matches = entry.Candidates.Where(candidate => SamePath(candidate.SourceRoot, source.RootPath)).ToArray(); if (matches.Length == 0) continue; if (matches.Length > 1) throw new InvalidDataException($"Source '{source.Name}' contains {matches.Length:N0} physical candidates for {entry.ArchivePath}; choose one effective layer before ID remapping."); var candidate = matches[0]; var file = WdbcFile.Load(candidate.FilePath); if (file.FieldCount != baseFile.FieldCount || file.RecordSize != baseFile.RecordSize) throw new InvalidDataException($"Source '{source.Name}' layout {file.FieldCount}/{file.RecordSize} differs from base {baseFile.FieldCount}/{baseFile.RecordSize}."); layers[source.RootPath] = new(source with { RootPath = Path.GetFullPath(source.RootPath) }, Path.GetFullPath(candidate.FilePath), file, DbcRecordIdentity.IndexRows(file, resolution.Columns, resolution.KeyStrategy));
        }
        var baseRows = DbcRecordIdentity.IndexRows(baseFile, resolution.Columns, resolution.KeyStrategy); var occupied = baseRows.Keys.ToHashSet(); var maximum = occupied.Concat(layers.Values.SelectMany(layer => layer.Rows.Keys)).DefaultIfEmpty().Max(); if (maximum == uint.MaxValue) throw new InvalidDataException($"{table}.dbc already uses ID {uint.MaxValue:N0}; no collision-free automatic ID remains in its 32-bit namespace."); return new(table, PatchInputMapper.NormalizeArchivePath(entry.ArchivePath), basePath, baseFile, baseRows, resolution.Columns, key, references, dbdPath, layers, occupied, maximum + 1);
    }

    private static PlannedRow AddState(TableContext context, LayerTable layer, uint sourceId, int sourceRow, IDictionary<StateKey, PlannedRow> states)
    {
        var key = StateKeyFor(layer.Source.RootPath, context.Table, sourceId); if (states.TryGetValue(key, out var existing)) return existing; uint allocated; if (!context.BaseRows.ContainsKey(sourceId) && context.Reserved.Add(sourceId)) allocated = sourceId; else { while (!context.Reserved.Add(context.NextId)) context.NextId = checked(context.NextId + 1); allocated = context.NextId; context.NextId = checked(context.NextId + 1); } var state = new PlannedRow(context, layer, sourceId, sourceRow, allocated); states[key] = state; return state;
    }

    private static Dictionary<string, uint> ResolveReferences(TableContext context, string sourceRoot, WdbcFile file, int row, IReadOnlyDictionary<StateKey, PlannedRow> states)
    {
        var rewrites = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase); foreach (var pair in context.References) { var column = context.Columns.First(column => column.Name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)); var sourceId = file.GetRaw(row, column); if (sourceId == 0) continue; if (states.TryGetValue(StateKeyFor(sourceRoot, pair.Value, sourceId), out var target) && target.TargetId != sourceId) rewrites[column.Name] = target.TargetId; } return rewrites;
    }

    private static CatalogRow? FindEquivalent(IReadOnlyDictionary<string, List<CatalogRow>> catalog, TableContext context, RowView view)
    {
        var hash = SemanticHash(context, view); return catalog.TryGetValue(hash, out var rows) ? rows.FirstOrDefault(row => RowsEqual(row.View, view, context.Columns, context.Key)) : null;
    }
    private static void AddCatalog(IDictionary<string, List<CatalogRow>> catalog, TableContext context, RowView view, uint targetId) { var hash = SemanticHash(context, view); if (!catalog.TryGetValue(hash, out var rows)) catalog[hash] = rows = []; rows.Add(new(targetId, view)); }
    private static string SemanticHash(TableContext context, RowView view) { using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); foreach (var column in context.Columns.Where(column => column.Index != context.Key.Index)) { var value = column.Type == DbcValueType.StringOffset ? view.File.GetString(view.File.GetRaw(view.Row, column)) : view.Rewrites.GetValueOrDefault(column.Name, view.File.GetRaw(view.Row, column)).ToString("X8"); hash.AppendData(Encoding.UTF8.GetBytes(value)); hash.AppendData([0]); } return Convert.ToHexString(hash.GetHashAndReset()); }
    private static bool RowsEqual(RowView left, RowView right, IReadOnlyList<DbcColumn> columns, DbcColumn key) { foreach (var column in columns.Where(column => column.Index != key.Index)) { if (column.Type == DbcValueType.StringOffset) { if (!left.File.GetString(left.File.GetRaw(left.Row, column)).Equals(right.File.GetString(right.File.GetRaw(right.Row, column)), StringComparison.Ordinal)) return false; } else if (left.Rewrites.GetValueOrDefault(column.Name, left.File.GetRaw(left.Row, column)) != right.Rewrites.GetValueOrDefault(column.Name, right.File.GetRaw(right.Row, column))) return false; } return true; }
    private static string Signature(IReadOnlyDictionary<StateKey, PlannedRow> states) => string.Join('|', states.OrderBy(pair => pair.Key.SourceRoot, StringComparer.OrdinalIgnoreCase).ThenBy(pair => pair.Key.Table, StringComparer.OrdinalIgnoreCase).ThenBy(pair => pair.Key.SourceId).Select(pair => $"{pair.Key.SourceRoot}:{pair.Key.Table}:{pair.Key.SourceId}>{pair.Value.TargetId}:{pair.Value.AddsRow}:{string.Join(',', pair.Value.ReferenceRewrites.OrderBy(value => value.Key).Select(value => $"{value.Key}={value.Value}"))}"));
    private static void RequirePhysicalSchema(string table, WdbcFile file, DbcSchemaResolution resolution) { if (resolution.MatchKind != DbcSchemaMatchKind.NamedMatch || resolution.Columns.Count != file.FieldCount || resolution.KeyStrategy.Kind != DbcRecordKeyKind.PhysicalColumn) throw new InvalidDataException($"{table}.dbc requires an exact named schema with a physical ID; resolved {resolution.MatchKind}, {resolution.Columns.Count:N0}/{file.FieldCount:N0} fields, {resolution.KeyStrategy.Kind}."); }
    private static bool IsDbc(string path) => path.StartsWith("DBFilesClient\\", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(path).Equals(".dbc", StringComparison.OrdinalIgnoreCase);
    private static StateKey StateKeyFor(string sourceRoot, string table, uint sourceId) => new(Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot)).ToUpperInvariant(), table.Trim().ToUpperInvariant(), sourceId);
    private static bool SamePath(string left, string right) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
    private static void AtomicJson<T>(string path, T value) { path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + ".tmp"; File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions)); File.Move(temporary, path, true); }
    private static string RequiredFile(string path, string label) { path = Path.GetFullPath(path); if (!File.Exists(path)) throw new FileNotFoundException($"{label} does not exist.", path); return path; }
    private static string RequiredDirectory(string path, string label) { path = Path.GetFullPath(path); if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"{label} does not exist: {path}"); return path; }
    private static string Hash(string path) { using var stream = File.OpenRead(Path.GetFullPath(path)); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static readonly IReadOnlyDictionary<string, uint> EmptyRewrites = new Dictionary<string, uint>();
    private readonly record struct StateKey(string SourceRoot, string Table, uint SourceId);
    private sealed record LayerTable(ClientFusionSource Source, string Path, WdbcFile File, Dictionary<uint, int> Rows);
    private sealed class TableContext(string table, string archivePath, string basePath, WdbcFile baseFile, Dictionary<uint, int> baseRows, IReadOnlyList<DbcColumn> columns, DbcColumn key, IReadOnlyDictionary<string, string> references, string dbdPath, Dictionary<string, LayerTable> layers, HashSet<uint> reserved, uint nextId)
    {
        public string Table { get; } = table; public string ArchivePath { get; } = archivePath; public string BasePath { get; } = basePath; public WdbcFile BaseFile { get; } = baseFile; public Dictionary<uint, int> BaseRows { get; } = baseRows; public IReadOnlyList<DbcColumn> Columns { get; } = columns; public DbcColumn Key { get; } = key; public IReadOnlyDictionary<string, string> References { get; } = references; public string DbdPath { get; } = dbdPath; public Dictionary<string, LayerTable> Layers { get; } = layers; public HashSet<uint> Reserved { get; } = reserved; public uint NextId { get; set; } = nextId;
    }
    private sealed class PlannedRow(TableContext context, LayerTable layer, uint sourceId, int sourceRow, uint allocatedId)
    {
        public TableContext Context { get; } = context; public LayerTable Layer { get; } = layer; public uint SourceId { get; } = sourceId; public int SourceRow { get; } = sourceRow; public uint AllocatedId { get; } = allocatedId; public uint TargetId { get; set; } = allocatedId; public bool AddsRow { get; set; } = true; public string Resolution { get; set; } = string.Empty; public Dictionary<string, uint> ReferenceRewrites { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
    private sealed record RowView(WdbcFile File, int Row, IReadOnlyDictionary<string, uint> Rewrites);
    private sealed record CatalogRow(uint TargetId, RowView View);
}
