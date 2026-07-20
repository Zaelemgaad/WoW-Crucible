using System.Security.Cryptography;
using System.Text.Json;

namespace WoWCrucible.Core;

public enum CreatureAppearancePortAction
{
    ReuseSameId,
    ReuseEquivalent,
    AddOriginalId,
    AddRemappedId
}

public sealed record CreatureAppearancePortRow(
    string Table,
    uint SourceId,
    uint TargetId,
    CreatureAppearancePortAction Action,
    IReadOnlyDictionary<string, uint> ReferenceRewrites)
{
    public bool AddsRow => Action is CreatureAppearancePortAction.AddOriginalId or CreatureAppearancePortAction.AddRemappedId;
}

public sealed record CreatureAppearanceRequiredAsset(string Kind, string ClientPath, string SourceTable, uint SourceId);

public sealed record CreatureAppearancePortPlan(
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    string SourceDbcRoot,
    string TargetDbcRoot,
    string SchemaPath,
    string SchemaSha256,
    uint SourceDisplayId,
    uint TargetDisplayId,
    IReadOnlyDictionary<string, string> SourceFileSha256,
    IReadOnlyDictionary<string, string> TargetFileSha256,
    IReadOnlyList<CreatureAppearancePortRow> Rows,
    IReadOnlyList<CreatureAppearanceRequiredAsset> RequiredAssets,
    IReadOnlyList<string> Findings)
{
    public int AddedRows => Rows.Count(row => row.AddsRow);
    public int ReusedRows => Rows.Count - AddedRows;
    public IReadOnlyList<string> ChangedTables => Rows.Where(row => row.AddsRow).Select(row => row.Table).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
}

public sealed record CreatureAppearancePortResult(
    string OutputDirectory,
    string ReceiptPath,
    uint TargetDisplayId,
    IReadOnlyDictionary<string, string> OutputFiles,
    IReadOnlyDictionary<string, string> OutputSha256,
    CreatureAppearancePortPlan Plan);

public sealed record CreatureAppearancePortRequest(string Role, uint SourceDisplayId);

public sealed record CreatureAppearancePortBinding(
    string Role,
    uint SourceDisplayId,
    uint TargetDisplayId,
    uint SourceModelId,
    uint TargetModelId,
    int AddedRows,
    int ReusedRows);

public sealed record CreatureAppearanceBatchPortPlan(
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    string SourceDbcRoot,
    string TargetDbcRoot,
    string SchemaPath,
    string SchemaSha256,
    IReadOnlyDictionary<string, string> SourceFileSha256,
    IReadOnlyDictionary<string, string> TargetFileSha256,
    IReadOnlyList<CreatureAppearancePortBinding> Bindings,
    IReadOnlyList<CreatureAppearancePortRow> Rows,
    IReadOnlyList<CreatureAppearanceRequiredAsset> RequiredAssets,
    IReadOnlyList<string> Findings)
{
    public int AddedRows => Rows.Count(row => row.AddsRow);
    public int ReusedRows => Rows.Count - AddedRows;
    public IReadOnlyList<string> ChangedTables => Rows.Where(row => row.AddsRow).Select(row => row.Table).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
}

public sealed record CreatureAppearanceBatchPortResult(
    string OutputDirectory,
    string ReceiptPath,
    IReadOnlyDictionary<string, string> OutputFiles,
    IReadOnlyDictionary<string, string> OutputSha256,
    CreatureAppearanceBatchPortPlan Plan);

public static class CreatureAppearancePortService
{
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static CreatureAppearancePortPlan CreatePlan(string sourceDbcRoot, string targetDbcRoot, string schemaPath, uint displayId, CancellationToken cancellationToken = default)
    {
        if (displayId == 0) throw new ArgumentOutOfRangeException(nameof(displayId), "A positive source creature display ID is required.");
        sourceDbcRoot = RequiredDirectory(sourceDbcRoot, "Source DBC root"); targetDbcRoot = RequiredDirectory(targetDbcRoot, "Target DBC root"); schemaPath = RequiredFile(schemaPath, "WotLK schema");
        var schema = DbcSchemaCatalog.Load(schemaPath); var sourceHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var targetHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var display = OpenContext("CreatureDisplayInfo", sourceDbcRoot, targetDbcRoot, schema, sourceHashes, targetHashes); cancellationToken.ThrowIfCancellationRequested();
        var sourceDisplayRow = display.SourceRows.TryGetValue(displayId, out var displayRow) ? displayRow : throw new KeyNotFoundException($"Source CreatureDisplayInfo.dbc has no ID {displayId:N0}.");
        var modelId = display.Source.GetRaw(sourceDisplayRow, display.Column("ModelID")); var extraId = display.Source.GetRaw(sourceDisplayRow, display.Column("ExtendedDisplayInfoID"));
        if (modelId == 0) throw new InvalidDataException($"CreatureDisplayInfo {displayId:N0} has no CreatureModelData reference.");

        var rows = new List<CreatureAppearancePortRow>(); var findings = new List<string>(); var requiredAssets = new List<CreatureAppearanceRequiredAsset>();
        var model = OpenContext("CreatureModelData", sourceDbcRoot, targetDbcRoot, schema, sourceHashes, targetHashes); var modelPlan = PlanRow(model, modelId, EmptyRewrites); rows.Add(modelPlan);

        var itemMap = new Dictionary<uint, uint>(); CreatureAppearancePortRow? extraPlan = null;
        if (extraId != 0)
        {
            var extra = OpenContext("CreatureDisplayInfoExtra", sourceDbcRoot, targetDbcRoot, schema, sourceHashes, targetHashes); var sourceExtraRow = extra.SourceRows.TryGetValue(extraId, out var extraRow) ? extraRow : throw new KeyNotFoundException($"Source CreatureDisplayInfoExtra.dbc has no ID {extraId:N0}, referenced by display {displayId:N0}.");
            var itemIds = Enumerable.Range(0, 11).Select(index => extra.Source.GetRaw(sourceExtraRow, extra.Column($"NPCItemDisplay[{index}]"))).Where(id => id != 0).Distinct().Order().ToArray();
            if (itemIds.Length > 0)
            {
                var items = OpenContext("ItemDisplayInfo", sourceDbcRoot, targetDbcRoot, schema, sourceHashes, targetHashes);
                foreach (var itemId in itemIds) { cancellationToken.ThrowIfCancellationRequested(); var itemPlan = PlanRow(items, itemId, EmptyRewrites); rows.Add(itemPlan); itemMap[itemId] = itemPlan.TargetId; AddItemAssets(requiredAssets, items.SourcePath, schemaPath, itemId); }
            }
            var extraRewrites = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < 11; index++) { var column = $"NPCItemDisplay[{index}]"; var id = extra.Source.GetRaw(sourceExtraRow, extra.Column(column)); if (id != 0 && itemMap.TryGetValue(id, out var replacement) && replacement != id) extraRewrites[column] = replacement; }
            extraPlan = PlanRow(extra, extraId, extraRewrites); rows.Add(extraPlan);
            var bakeName = Convert.ToString(extra.Source.GetDisplayValue(sourceExtraRow, extra.Column("BakeName"))) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(bakeName)) requiredAssets.Add(new("baked-creature-texture", $"Textures\\BakedNpcTextures\\{EnsureExtension(Path.GetFileName(bakeName), ".blp")}", "CreatureDisplayInfoExtra", extraId));
        }

        var displayRewrites = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        if (modelPlan.TargetId != modelId) displayRewrites["ModelID"] = modelPlan.TargetId;
        if (extraPlan is not null && extraPlan.TargetId != extraId) displayRewrites["ExtendedDisplayInfoID"] = extraPlan.TargetId;
        var parentPlan = PlanRow(display, displayId, displayRewrites); rows.Add(parentPlan);

        var preview = new CreatureDisplayPreviewService().ResolveDisplay(sourceDbcRoot, schemaPath, displayId);
        requiredAssets.Add(new("creature-model", preview.ModelClientPath, "CreatureModelData", modelId));
        foreach (var texture in preview.TextureVariations.Where(path => !string.IsNullOrWhiteSpace(path))) requiredAssets.Add(new("creature-texture", texture, "CreatureDisplayInfo", displayId));
        if (rows.All(row => !row.AddsRow)) findings.Add($"Target DBCs already contain a semantically equivalent complete appearance chain; use display ID {parentPlan.TargetId:N0} without writing duplicate rows.");
        else findings.Add($"Add {rows.Count(row => row.AddsRow):N0} row(s), reuse {rows.Count(row => !row.AddsRow):N0}, and assign target display ID {parentPlan.TargetId:N0}.");
        var assets = requiredAssets.Where(asset => !string.IsNullOrWhiteSpace(asset.ClientPath)).DistinctBy(asset => (asset.Kind.ToUpperInvariant(), PatchInputMapper.NormalizeArchivePath(asset.ClientPath).ToUpperInvariant())).Select(asset => asset with { ClientPath = PatchInputMapper.NormalizeArchivePath(asset.ClientPath) }).OrderBy(asset => asset.ClientPath, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(FormatVersion, DateTimeOffset.UtcNow, sourceDbcRoot, targetDbcRoot, schemaPath, Hash(schemaPath), displayId, parentPlan.TargetId, sourceHashes, targetHashes, rows, assets, findings);
    }

    /// <summary>
    /// Plans several complete appearance chains against one shared target state.
    /// This is deliberately not implemented as repeated single-display plans:
    /// model, item, extra, and display IDs allocated for an earlier role must be
    /// visible while a later role is deduplicated or remapped.
    /// </summary>
    public static CreatureAppearanceBatchPortPlan CreateBatchPlan(
        string sourceDbcRoot,
        string targetDbcRoot,
        string schemaPath,
        IReadOnlyList<CreatureAppearancePortRequest> requests,
        CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0) throw new ArgumentException("At least one creature appearance request is required.", nameof(requests));
        if (requests.Any(request => string.IsNullOrWhiteSpace(request.Role))) throw new ArgumentException("Every creature appearance request requires a role.", nameof(requests));
        if (requests.Any(request => request.SourceDisplayId == 0)) throw new ArgumentException("Every creature appearance request requires a positive source display ID.", nameof(requests));
        if (requests.Select(request => request.Role.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != requests.Count) throw new ArgumentException("Creature appearance request roles must be unique.", nameof(requests));

        sourceDbcRoot = RequiredDirectory(sourceDbcRoot, "Source DBC root"); targetDbcRoot = RequiredDirectory(targetDbcRoot, "Target DBC root"); schemaPath = RequiredFile(schemaPath, "WotLK schema");
        var schema = DbcSchemaCatalog.Load(schemaPath); var sourceHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var targetHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var contexts = new Dictionary<string, TableContext>(StringComparer.OrdinalIgnoreCase);
        TableContext Context(string table)
        {
            if (contexts.TryGetValue(table, out var existing)) return existing;
            var opened = OpenContext(table, sourceDbcRoot, targetDbcRoot, schema, sourceHashes, targetHashes); contexts[table] = opened; return opened;
        }

        var display = Context("CreatureDisplayInfo"); var model = Context("CreatureModelData");
        var rows = new List<CreatureAppearancePortRow>(); var bindings = new List<CreatureAppearancePortBinding>(requests.Count); var findings = new List<string>(); var requiredAssets = new List<CreatureAppearanceRequiredAsset>();
        foreach (var requestValue in requests)
        {
            cancellationToken.ThrowIfCancellationRequested(); var request = requestValue with { Role = requestValue.Role.Trim() }; var firstRow = rows.Count;
            var sourceDisplayRow = display.SourceRows.TryGetValue(request.SourceDisplayId, out var displayRow) ? displayRow : throw new KeyNotFoundException($"Source CreatureDisplayInfo.dbc has no ID {request.SourceDisplayId:N0} for {request.Role}.");
            var sourceModelId = display.Source.GetRaw(sourceDisplayRow, display.Column("ModelID")); var sourceExtraId = display.Source.GetRaw(sourceDisplayRow, display.Column("ExtendedDisplayInfoID"));
            if (sourceModelId == 0) throw new InvalidDataException($"Source CreatureDisplayInfo {request.SourceDisplayId:N0} for {request.Role} has no CreatureModelData reference.");
            var modelPlan = PlanRow(model, sourceModelId, EmptyRewrites); rows.Add(modelPlan);

            CreatureAppearancePortRow? extraPlan = null;
            if (sourceExtraId != 0)
            {
                var extra = Context("CreatureDisplayInfoExtra"); var sourceExtraRow = extra.SourceRows.TryGetValue(sourceExtraId, out var extraRow) ? extraRow : throw new KeyNotFoundException($"Source CreatureDisplayInfoExtra.dbc has no ID {sourceExtraId:N0}, referenced by {request.Role} display {request.SourceDisplayId:N0}.");
                var itemMap = new Dictionary<uint, uint>(); var itemIds = Enumerable.Range(0, 11).Select(index => extra.Source.GetRaw(sourceExtraRow, extra.Column($"NPCItemDisplay[{index}]"))).Where(id => id != 0).Distinct().Order().ToArray();
                if (itemIds.Length > 0)
                {
                    var items = Context("ItemDisplayInfo");
                    foreach (var itemId in itemIds)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); var itemPlan = PlanRow(items, itemId, EmptyRewrites); rows.Add(itemPlan); itemMap[itemId] = itemPlan.TargetId; AddItemAssets(requiredAssets, items.SourcePath, schemaPath, itemId);
                    }
                }
                var extraRewrites = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < 11; index++)
                {
                    var column = $"NPCItemDisplay[{index}]"; var id = extra.Source.GetRaw(sourceExtraRow, extra.Column(column)); if (id != 0 && itemMap.TryGetValue(id, out var replacement) && replacement != id) extraRewrites[column] = replacement;
                }
                extraPlan = PlanRow(extra, sourceExtraId, extraRewrites); rows.Add(extraPlan);
                var bakeName = Convert.ToString(extra.Source.GetDisplayValue(sourceExtraRow, extra.Column("BakeName"))) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(bakeName)) requiredAssets.Add(new("baked-creature-texture", $"Textures\\BakedNpcTextures\\{EnsureExtension(Path.GetFileName(bakeName), ".blp")}", "CreatureDisplayInfoExtra", sourceExtraId));
            }

            var displayRewrites = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            if (modelPlan.TargetId != sourceModelId) displayRewrites["ModelID"] = modelPlan.TargetId;
            if (extraPlan is not null && extraPlan.TargetId != sourceExtraId) displayRewrites["ExtendedDisplayInfoID"] = extraPlan.TargetId;
            var displayPlan = PlanRow(display, request.SourceDisplayId, displayRewrites); rows.Add(displayPlan);

            var preview = new CreatureDisplayPreviewService().ResolveDisplay(sourceDbcRoot, schemaPath, request.SourceDisplayId);
            requiredAssets.Add(new("creature-model", preview.ModelClientPath, "CreatureModelData", sourceModelId));
            foreach (var texture in preview.TextureVariations.Where(path => !string.IsNullOrWhiteSpace(path))) requiredAssets.Add(new("creature-texture", texture, "CreatureDisplayInfo", request.SourceDisplayId));
            var roleRows = rows.Skip(firstRow).ToArray(); bindings.Add(new(request.Role, request.SourceDisplayId, displayPlan.TargetId, sourceModelId, modelPlan.TargetId, roleRows.Count(row => row.AddsRow), roleRows.Count(row => !row.AddsRow)));
            findings.Add($"{request.Role}: source display {request.SourceDisplayId:N0} -> target display {displayPlan.TargetId:N0}; source model {sourceModelId:N0} -> target model {modelPlan.TargetId:N0}; add {roleRows.Count(row => row.AddsRow):N0}, reuse {roleRows.Count(row => !row.AddsRow):N0} row operation(s).");
        }

        var assets = requiredAssets.Where(asset => !string.IsNullOrWhiteSpace(asset.ClientPath)).DistinctBy(asset => (asset.Kind.ToUpperInvariant(), PatchInputMapper.NormalizeArchivePath(asset.ClientPath).ToUpperInvariant())).Select(asset => asset with { ClientPath = PatchInputMapper.NormalizeArchivePath(asset.ClientPath) }).OrderBy(asset => asset.ClientPath, StringComparer.OrdinalIgnoreCase).ToArray();
        findings.Add(rows.All(row => !row.AddsRow)
            ? "The target DBCs already contain semantically equivalent chains for every requested appearance; no appearance DBC row needs to be written."
            : $"The coordinated appearance promotion adds {rows.Count(row => row.AddsRow):N0} row(s) across {rows.Where(row => row.AddsRow).Select(row => row.Table).Distinct(StringComparer.OrdinalIgnoreCase).Count():N0} DBC table(s) and reuses {rows.Count(row => !row.AddsRow):N0} row operation(s).");
        return new(FormatVersion, DateTimeOffset.UtcNow, sourceDbcRoot, targetDbcRoot, schemaPath, Hash(schemaPath), sourceHashes, targetHashes, bindings, rows, assets, findings);
    }

    public static CreatureAppearanceBatchPortResult ApplyBatch(CreatureAppearanceBatchPortPlan plan, string outputDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan); if (plan.FormatVersion != FormatVersion) throw new InvalidDataException($"Unsupported creature appearance batch plan version {plan.FormatVersion}.");
        VerifyBatchInputs(plan); var requests = plan.Bindings.Select(binding => new CreatureAppearancePortRequest(binding.Role, binding.SourceDisplayId)).ToArray(); var recreated = CreateBatchPlan(plan.SourceDbcRoot, plan.TargetDbcRoot, plan.SchemaPath, requests, cancellationToken);
        if (!BatchIdentity(recreated).SequenceEqual(BatchIdentity(plan), StringComparer.Ordinal)) throw new InvalidDataException("The supplied creature appearance batch no longer matches a fresh deterministic plan over its bound inputs.");
        outputDirectory = Path.GetFullPath(outputDirectory); if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any()) throw new IOException($"Creature appearance batch output must be new or empty: {outputDirectory}");
        var parent = Path.GetDirectoryName(outputDirectory) ?? throw new InvalidOperationException("Output directory has no parent."); Directory.CreateDirectory(parent); var staging = Path.Combine(parent, $".{Path.GetFileName(outputDirectory)}.crucible-{Guid.NewGuid():N}"); Directory.CreateDirectory(staging);
        try
        {
            var schema = DbcSchemaCatalog.Load(plan.SchemaPath); var outputFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var outputHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tableRows in plan.Rows.Where(row => row.AddsRow).GroupBy(row => row.Table, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested(); var additions = tableRows.GroupBy(row => row.TargetId).Select(group => group.Count() == 1 ? group.Single() : throw new InvalidDataException($"Appearance batch planned more than one added {tableRows.Key} row for target ID {group.Key:N0}.")).OrderBy(row => row.TargetId).ToArray();
                var sourcePath = RequiredTablePath(plan.SourceDbcRoot, tableRows.Key); var targetPath = RequiredTablePath(plan.TargetDbcRoot, tableRows.Key); var source = WdbcFile.Load(sourcePath); var target = WdbcFile.Load(targetPath); var resolution = ResolveSchema(schema, tableRows.Key, target);
                if (source.FieldCount != target.FieldCount || source.RecordSize != target.RecordSize) throw new InvalidDataException($"{tableRows.Key} source/target layouts changed after planning.");
                var key = DbcRecordIdentity.PhysicalColumn(resolution.Columns, resolution.KeyStrategy) ?? throw new InvalidDataException($"{tableRows.Key} has no physical key."); var sourceRows = DbcRecordIdentity.IndexRows(source, resolution.Columns, resolution.KeyStrategy); var targetRows = DbcRecordIdentity.IndexRows(target, resolution.Columns, resolution.KeyStrategy);
                foreach (var rowPlan in additions)
                {
                    if (!sourceRows.TryGetValue(rowPlan.SourceId, out var sourceRow)) throw new InvalidDataException($"{tableRows.Key} source ID {rowPlan.SourceId:N0} disappeared after planning.");
                    if (targetRows.ContainsKey(rowPlan.TargetId)) throw new InvalidDataException($"{tableRows.Key} target ID {rowPlan.TargetId:N0} became occupied after planning.");
                    var destinationRow = target.AddBlankRow(key);
                    foreach (var column in resolution.Columns)
                    {
                        if (column.Index == key.Index) { target.SetRaw(destinationRow, column, rowPlan.TargetId); continue; }
                        if (rowPlan.ReferenceRewrites.TryGetValue(column.Name, out var replacement)) { target.SetRaw(destinationRow, column, replacement); continue; }
                        if (column.Type == DbcValueType.StringOffset) target.SetDisplayValue(destinationRow, column, source.GetString(source.GetRaw(sourceRow, column))); else target.SetRaw(destinationRow, column, source.GetRaw(sourceRow, column));
                    }
                    targetRows[rowPlan.TargetId] = destinationRow;
                }
                var outputPath = Path.Combine(staging, tableRows.Key + ".dbc"); target.Save(outputPath); var reloaded = WdbcFile.Load(outputPath); if (reloaded.RowCount != target.RowCount || reloaded.FieldCount != target.FieldCount) throw new InvalidDataException($"{tableRows.Key} batch output failed independent structural reload validation."); outputFiles[tableRows.Key] = outputPath; outputHashes[tableRows.Key] = Hash(outputPath);
            }
            var receiptPath = Path.Combine(staging, "creature-appearance-batch.crucible.json"); var finalFiles = outputFiles.ToDictionary(pair => pair.Key, pair => Path.Combine(outputDirectory, Path.GetFileName(pair.Value)), StringComparer.OrdinalIgnoreCase); var finalReceipt = Path.Combine(outputDirectory, Path.GetFileName(receiptPath)); var result = new CreatureAppearanceBatchPortResult(outputDirectory, finalReceipt, finalFiles, outputHashes, plan); File.WriteAllText(receiptPath, JsonSerializer.Serialize(result, JsonOptions));
            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory); Directory.Move(staging, outputDirectory); return result;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    public static void SavePlan(string path, CreatureAppearancePortPlan plan)
    {
        path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + ".tmp"; File.WriteAllText(temporary, JsonSerializer.Serialize(plan, JsonOptions)); File.Move(temporary, path, true);
    }

    public static CreatureAppearancePortPlan LoadPlan(string path)
    {
        var plan = JsonSerializer.Deserialize<CreatureAppearancePortPlan>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("Creature appearance port plan is empty.");
        if (plan.FormatVersion != FormatVersion) throw new InvalidDataException($"Unsupported creature appearance port plan version {plan.FormatVersion}."); return plan;
    }

    public static CreatureAppearancePortResult LoadResult(string receiptPath)
    {
        receiptPath = RequiredFile(receiptPath, "Creature appearance port receipt");
        var result = JsonSerializer.Deserialize<CreatureAppearancePortResult>(File.ReadAllText(receiptPath), JsonOptions) ?? throw new InvalidDataException("Creature appearance port receipt is empty.");
        if (result.Plan.FormatVersion != FormatVersion || result.TargetDisplayId != result.Plan.TargetDisplayId) throw new InvalidDataException("Creature appearance port receipt has an unsupported plan or mismatched target display ID.");
        var expected = result.Plan.ChangedTables.Order(StringComparer.OrdinalIgnoreCase).ToArray(); var actual = result.OutputFiles.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!expected.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase) || !actual.SequenceEqual(result.OutputSha256.Keys.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Creature appearance port receipt does not contain exactly the changed DBC outputs declared by its plan.");
        foreach (var table in actual)
        {
            var path = RequiredFile(result.OutputFiles[table], $"Receipt output {table}.dbc");
            if (!Hash(path).Equals(result.OutputSha256[table], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Receipt output {table}.dbc no longer matches its recorded SHA-256.");
        }
        return result with { ReceiptPath = receiptPath, OutputDirectory = Path.GetFullPath(result.OutputDirectory) };
    }

    public static void VerifyPlanInputs(CreatureAppearancePortPlan plan) => VerifyInputs(plan);
    public static void VerifyPlanSourceInputs(CreatureAppearancePortPlan plan)
    {
        if (!Hash(plan.SchemaPath).Equals(plan.SchemaSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The schema changed after the appearance port plan was created.");
        foreach (var pair in plan.SourceFileSha256) if (!Hash(RequiredTablePath(plan.SourceDbcRoot, pair.Key)).Equals(pair.Value, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Source {pair.Key}.dbc changed after planning.");
    }

    public static CreatureAppearancePortResult Apply(CreatureAppearancePortPlan plan, string outputDirectory, CancellationToken cancellationToken = default)
    {
        if (plan.FormatVersion != FormatVersion) throw new InvalidDataException($"Unsupported creature appearance port plan version {plan.FormatVersion}."); outputDirectory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any()) throw new IOException($"Creature appearance output must be new or empty: {outputDirectory}");
        VerifyInputs(plan); var recreated = CreatePlan(plan.SourceDbcRoot, plan.TargetDbcRoot, plan.SchemaPath, plan.SourceDisplayId, cancellationToken);
        if (recreated.TargetDisplayId != plan.TargetDisplayId || !recreated.Rows.Select(RowIdentity).SequenceEqual(plan.Rows.Select(RowIdentity))) throw new InvalidDataException("The supplied appearance plan does not match a fresh deterministic plan over its bound inputs.");
        var parent = Path.GetDirectoryName(outputDirectory) ?? throw new InvalidOperationException("Output directory has no parent."); Directory.CreateDirectory(parent); var staging = Path.Combine(parent, $".{Path.GetFileName(outputDirectory)}.crucible-{Guid.NewGuid():N}"); Directory.CreateDirectory(staging);
        try
        {
            var schema = DbcSchemaCatalog.Load(plan.SchemaPath); var outputFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var outputHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tableRows in plan.Rows.Where(row => row.AddsRow).GroupBy(row => row.Table, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested(); var sourcePath = RequiredTablePath(plan.SourceDbcRoot, tableRows.Key); var targetPath = RequiredTablePath(plan.TargetDbcRoot, tableRows.Key); var source = WdbcFile.Load(sourcePath); var target = WdbcFile.Load(targetPath); var resolution = ResolveSchema(schema, tableRows.Key, target);
                if (source.FieldCount != target.FieldCount || source.RecordSize != target.RecordSize) throw new InvalidDataException($"{tableRows.Key} source/target layouts changed after planning.");
                var key = DbcRecordIdentity.PhysicalColumn(resolution.Columns, resolution.KeyStrategy) ?? throw new InvalidDataException($"{tableRows.Key} has no physical key."); var sourceRows = DbcRecordIdentity.IndexRows(source, resolution.Columns, resolution.KeyStrategy); var targetRows = DbcRecordIdentity.IndexRows(target, resolution.Columns, resolution.KeyStrategy);
                foreach (var rowPlan in tableRows)
                {
                    if (!sourceRows.TryGetValue(rowPlan.SourceId, out var sourceRow)) throw new InvalidDataException($"{tableRows.Key} source ID {rowPlan.SourceId:N0} disappeared after planning.");
                    if (targetRows.ContainsKey(rowPlan.TargetId)) throw new InvalidDataException($"{tableRows.Key} target ID {rowPlan.TargetId:N0} became occupied after planning.");
                    var destinationRow = target.AddBlankRow(key); foreach (var column in resolution.Columns)
                    {
                        if (column.Index == key.Index) { target.SetRaw(destinationRow, column, rowPlan.TargetId); continue; }
                        if (rowPlan.ReferenceRewrites.TryGetValue(column.Name, out var replacement)) { target.SetRaw(destinationRow, column, replacement); continue; }
                        if (column.Type == DbcValueType.StringOffset) target.SetDisplayValue(destinationRow, column, source.GetString(source.GetRaw(sourceRow, column))); else target.SetRaw(destinationRow, column, source.GetRaw(sourceRow, column));
                    }
                    targetRows[rowPlan.TargetId] = destinationRow;
                }
                var outputPath = Path.Combine(staging, tableRows.Key + ".dbc"); target.Save(outputPath); var reloaded = WdbcFile.Load(outputPath); if (reloaded.RowCount != target.RowCount) throw new InvalidDataException($"{tableRows.Key} output failed independent row-count reload validation."); outputFiles[tableRows.Key] = outputPath; outputHashes[tableRows.Key] = Hash(outputPath);
            }
            var receiptPath = Path.Combine(staging, "creature-appearance-port.crucible.json"); var finalFiles = outputFiles.ToDictionary(pair => pair.Key, pair => Path.Combine(outputDirectory, Path.GetFileName(pair.Value)), StringComparer.OrdinalIgnoreCase); var finalReceipt = Path.Combine(outputDirectory, Path.GetFileName(receiptPath)); var result = new CreatureAppearancePortResult(outputDirectory, finalReceipt, plan.TargetDisplayId, finalFiles, outputHashes, plan); File.WriteAllText(receiptPath, JsonSerializer.Serialize(result, JsonOptions));
            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory); Directory.Move(staging, outputDirectory);
            return result;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private static readonly IReadOnlyDictionary<string, uint> EmptyRewrites = new Dictionary<string, uint>();

    private static CreatureAppearancePortRow PlanRow(TableContext context, uint sourceId, IReadOnlyDictionary<string, uint> rewrites)
    {
        if (!context.SourceRows.TryGetValue(sourceId, out var sourceRow)) throw new KeyNotFoundException($"Source {context.Table}.dbc has no ID {sourceId:N0}.");
        if (context.TargetRows.TryGetValue(sourceId, out var sameIdRow) && RowsEqual(context.Source, sourceRow, rewrites, context.Target, sameIdRow, context.Columns, context.Key)) return new(context.Table, sourceId, sourceId, CreatureAppearancePortAction.ReuseSameId, rewrites);
        foreach (var candidate in context.TargetRows.OrderBy(pair => pair.Key)) if (RowsEqual(context.Source, sourceRow, rewrites, context.Target, candidate.Value, context.Columns, context.Key)) return new(context.Table, sourceId, candidate.Key, CreatureAppearancePortAction.ReuseEquivalent, rewrites);
        foreach (var candidate in context.Planned) if (RowsEqual(context.Source, sourceRow, rewrites, context.Source, candidate.SourceRow, candidate.Rewrites, context.Columns, context.Key)) return new(context.Table, sourceId, candidate.TargetId, CreatureAppearancePortAction.ReuseEquivalent, rewrites);
        uint targetId; CreatureAppearancePortAction action;
        if (!context.Occupied.Contains(sourceId)) { targetId = sourceId; action = CreatureAppearancePortAction.AddOriginalId; }
        else { while (context.Occupied.Contains(context.NextId)) context.NextId = checked(context.NextId + 1); targetId = context.NextId; context.NextId = checked(context.NextId + 1); action = CreatureAppearancePortAction.AddRemappedId; }
        context.Occupied.Add(targetId); context.Planned.Add(new(sourceRow, targetId, rewrites)); return new(context.Table, sourceId, targetId, action, rewrites);
    }

    private static bool RowsEqual(WdbcFile left, int leftRow, IReadOnlyDictionary<string, uint> leftRewrites, WdbcFile right, int rightRow, IReadOnlyList<DbcColumn> columns, DbcColumn key)
        => RowsEqual(left, leftRow, leftRewrites, right, rightRow, EmptyRewrites, columns, key);

    private static bool RowsEqual(WdbcFile left, int leftRow, IReadOnlyDictionary<string, uint> leftRewrites, WdbcFile right, int rightRow, IReadOnlyDictionary<string, uint> rightRewrites, IReadOnlyList<DbcColumn> columns, DbcColumn key)
    {
        foreach (var column in columns.Where(column => column.Index != key.Index))
        {
            if (column.Type == DbcValueType.StringOffset)
            {
                if (!left.GetString(left.GetRaw(leftRow, column)).Equals(right.GetString(right.GetRaw(rightRow, column)), StringComparison.Ordinal)) return false;
            }
            else
            {
                var leftValue = leftRewrites.GetValueOrDefault(column.Name, left.GetRaw(leftRow, column)); var rightValue = rightRewrites.GetValueOrDefault(column.Name, right.GetRaw(rightRow, column)); if (leftValue != rightValue) return false;
            }
        }
        return true;
    }

    private static TableContext OpenContext(string table, string sourceRoot, string targetRoot, DbcSchemaCatalog schema, Dictionary<string, string> sourceHashes, Dictionary<string, string> targetHashes)
    {
        var sourcePath = RequiredTablePath(sourceRoot, table); var targetPath = RequiredTablePath(targetRoot, table); var source = WdbcFile.Load(sourcePath); var target = WdbcFile.Load(targetPath); if (source.FieldCount != target.FieldCount || source.RecordSize != target.RecordSize) throw new InvalidDataException($"{table}.dbc source and target layouts differ.");
        var resolution = ResolveSchema(schema, table, target); var key = DbcRecordIdentity.PhysicalColumn(resolution.Columns, resolution.KeyStrategy) ?? throw new InvalidDataException($"{table}.dbc requires a physical ID column."); var sourceRows = DbcRecordIdentity.IndexRows(source, resolution.Columns, resolution.KeyStrategy); var targetRows = DbcRecordIdentity.IndexRows(target, resolution.Columns, resolution.KeyStrategy);
        sourceHashes[table] = Hash(sourcePath); targetHashes[table] = Hash(targetPath); var next = checked(Math.Max(sourceRows.Keys.DefaultIfEmpty().Max(), targetRows.Keys.DefaultIfEmpty().Max()) + 1);
        return new(table, sourcePath, targetPath, source, target, resolution.Columns, key, sourceRows, targetRows, targetRows.Keys.ToHashSet(), next);
    }

    private static DbcSchemaResolution ResolveSchema(DbcSchemaCatalog schema, string table, WdbcFile file)
    {
        var resolution = schema.ResolveColumns(table, file.FieldCount); if (resolution.MatchKind != DbcSchemaMatchKind.NamedMatch || resolution.Columns.Count != file.FieldCount || resolution.KeyStrategy.Kind != DbcRecordKeyKind.PhysicalColumn) throw new InvalidDataException($"{table}.dbc requires an exact named schema with a physical ID; resolved {resolution.MatchKind}, {resolution.Columns.Count:N0}/{file.FieldCount:N0} fields, {resolution.KeyStrategy.Kind}."); return resolution;
    }

    private static void AddItemAssets(List<CreatureAppearanceRequiredAsset> assets, string itemDisplayPath, string schemaPath, uint itemDisplayId)
    {
        var display = ItemDisplayInfoService.Resolve(itemDisplayPath, schemaPath, itemDisplayId); foreach (var asset in display.Assets) foreach (var path in asset.ClientPaths) assets.Add(new($"item-{asset.Kind}", path, "ItemDisplayInfo", itemDisplayId));
    }

    private static void VerifyInputs(CreatureAppearancePortPlan plan)
    {
        if (!Hash(plan.SchemaPath).Equals(plan.SchemaSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The schema changed after the appearance port plan was created.");
        foreach (var pair in plan.SourceFileSha256) if (!Hash(RequiredTablePath(plan.SourceDbcRoot, pair.Key)).Equals(pair.Value, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Source {pair.Key}.dbc changed after planning.");
        foreach (var pair in plan.TargetFileSha256) if (!Hash(RequiredTablePath(plan.TargetDbcRoot, pair.Key)).Equals(pair.Value, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Target {pair.Key}.dbc changed after planning.");
    }

    private static void VerifyBatchInputs(CreatureAppearanceBatchPortPlan plan)
    {
        if (!Hash(plan.SchemaPath).Equals(plan.SchemaSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The schema changed after the appearance batch plan was created.");
        foreach (var pair in plan.SourceFileSha256) if (!Hash(RequiredTablePath(plan.SourceDbcRoot, pair.Key)).Equals(pair.Value, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Source {pair.Key}.dbc changed after batch planning.");
        foreach (var pair in plan.TargetFileSha256) if (!Hash(RequiredTablePath(plan.TargetDbcRoot, pair.Key)).Equals(pair.Value, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Target {pair.Key}.dbc changed after batch planning.");
    }

    private static IReadOnlyList<string> BatchIdentity(CreatureAppearanceBatchPortPlan plan) =>
    [
        .. plan.Bindings.Select(binding => $"B|{binding.Role}|{binding.SourceDisplayId}|{binding.TargetDisplayId}|{binding.SourceModelId}|{binding.TargetModelId}|{binding.AddedRows}|{binding.ReusedRows}"),
        .. plan.Rows.Select(RowIdentity),
        .. plan.RequiredAssets.Select(asset => $"A|{asset.Kind}|{asset.ClientPath}|{asset.SourceTable}|{asset.SourceId}"),
        .. plan.Findings.Select(finding => $"F|{finding}")
    ];

    private static string RowIdentity(CreatureAppearancePortRow row) => $"{row.Table}|{row.SourceId}|{row.TargetId}|{row.Action}|{string.Join(';', row.ReferenceRewrites.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"))}";
    private static string RequiredDirectory(string path, string label) { if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) throw new DirectoryNotFoundException($"{label} does not exist: {Path.GetFullPath(path ?? string.Empty)}"); return Path.GetFullPath(path); }
    private static string RequiredFile(string path, string label) { if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException($"{label} does not exist.", Path.GetFullPath(path ?? string.Empty)); return Path.GetFullPath(path); }
    private static string RequiredTablePath(string root, string table) => Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly).FirstOrDefault(path => Path.GetFileName(path).Equals(table + ".dbc", StringComparison.OrdinalIgnoreCase)) ?? throw new FileNotFoundException($"Required {table}.dbc is unavailable.", Path.Combine(root, table + ".dbc"));
    private static string EnsureExtension(string path, string extension) => string.IsNullOrEmpty(Path.GetExtension(path)) ? path + extension : path;
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }

    private sealed record PlannedSourceRow(int SourceRow, uint TargetId, IReadOnlyDictionary<string, uint> Rewrites);
    private sealed class TableContext(string table, string sourcePath, string targetPath, WdbcFile source, WdbcFile target, IReadOnlyList<DbcColumn> columns, DbcColumn key, IReadOnlyDictionary<uint, int> sourceRows, IReadOnlyDictionary<uint, int> targetRows, HashSet<uint> occupied, uint nextId)
    {
        public string Table { get; } = table; public string SourcePath { get; } = sourcePath; public string TargetPath { get; } = targetPath; public WdbcFile Source { get; } = source; public WdbcFile Target { get; } = target; public IReadOnlyList<DbcColumn> Columns { get; } = columns; public DbcColumn Key { get; } = key; public IReadOnlyDictionary<uint, int> SourceRows { get; } = sourceRows; public IReadOnlyDictionary<uint, int> TargetRows { get; } = targetRows; public HashSet<uint> Occupied { get; } = occupied; public uint NextId { get; set; } = nextId; public List<PlannedSourceRow> Planned { get; } = [];
        public DbcColumn Column(string name) => Columns.FirstOrDefault(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"{Table} schema is missing '{name}'.");
    }
}
