using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoWCrucible.Core;

public enum PlayableRaceCustomizationAction
{
    ReuseEquivalent,
    AddOriginalId,
    AddRemappedId,
    AppendVirtualRow
}

public sealed record PlayableRaceCustomizationRowPlan(
    string Table,
    uint SourceKey,
    uint TargetKey,
    PlayableRaceCustomizationAction Action)
{
    public bool AddsRow => Action is not PlayableRaceCustomizationAction.ReuseEquivalent;
}

public sealed record PlayableRaceCustomizationTablePlan(
    string Table,
    string RaceColumn,
    DbcRecordKeyKind KeyKind,
    int SourceRows,
    IReadOnlyList<PlayableRaceCustomizationRowPlan> Rows)
{
    public int AddedRows => Rows.Count(row => row.AddsRow);
    public int ReusedRows => Rows.Count - AddedRows;
}

public sealed record PlayableRaceCustomizationPromotionPlan(
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    string SourceDbcRoot,
    string TargetDbcRoot,
    string SchemaPath,
    string SchemaSha256,
    uint SourceRaceId,
    uint TargetRaceId,
    string? ProcessedAssetLibraryRoot,
    string? RequestedAssetProvenance,
    string? MaleAssetProvenance,
    string? FemaleAssetProvenance,
    IReadOnlyDictionary<string, string> SourceFileSha256,
    IReadOnlyDictionary<string, string> TargetFileSha256,
    IReadOnlyList<PlayableRaceCustomizationTablePlan> Tables,
    IReadOnlyList<string> RequiredTexturePaths,
    IReadOnlyList<CreatureDisplayBoundAsset> Assets,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings,
    string ContentSha256)
{
    public bool Ready => Blockers.Count == 0;
    public int AddedRows => Tables.Sum(table => table.AddedRows);
    public int ReusedRows => Tables.Sum(table => table.ReusedRows);
}

public sealed record PlayableRaceCustomizationPromotionResult(
    string OutputDirectory,
    string ReceiptPath,
    IReadOnlyDictionary<string, string> OutputFiles,
    IReadOnlyDictionary<string, string> OutputSha256,
    PlayableRaceCustomizationPromotionPlan Plan);

/// <summary>
/// Promotes the five build-12340 character-customization tables as one reviewed
/// surface. Physical IDs are semantically reused or collision-remapped; the
/// generated-key facial-hair table is append-only and never receives a fake ID.
/// </summary>
public static class PlayableRaceCustomizationPromotionService
{
    public const int FormatVersion = 1;
    private static readonly (string Table, string RaceColumn)[] Surface =
    [
        ("BarberShopStyle", "Race"),
        ("CharacterFacialHairStyles", "RaceID"),
        ("CharHairGeosets", "RaceID"),
        ("CharHairTextures", "Race"),
        ("CharSections", "RaceID")
    ];
    public static IReadOnlySet<string> TableNames { get; } = Surface.Select(value => value.Table).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private static readonly JsonSerializerOptions HashJson = new() { WriteIndented = false, Converters = { new JsonStringEnumConverter() } };

    public static PlayableRaceCustomizationPromotionPlan CreatePlan(
        string sourceDbcRoot,
        string targetDbcRoot,
        string schemaPath,
        uint sourceRaceId,
        uint targetRaceId,
        string? processedAssetLibraryRoot = null,
        string? requestedAssetProvenance = null,
        string? maleAssetProvenance = null,
        string? femaleAssetProvenance = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRace(sourceRaceId, nameof(sourceRaceId)); ValidateRace(targetRaceId, nameof(targetRaceId));
        sourceDbcRoot = RequiredDirectory(sourceDbcRoot, "Customization source DBC folder"); targetDbcRoot = RequiredDirectory(targetDbcRoot, "Customization target DBC folder"); schemaPath = RequiredFile(schemaPath, "WotLK schema");
        processedAssetLibraryRoot = string.IsNullOrWhiteSpace(processedAssetLibraryRoot) ? null : RequiredDirectory(processedAssetLibraryRoot, "Processed asset library"); requestedAssetProvenance = Clean(requestedAssetProvenance); maleAssetProvenance = Clean(maleAssetProvenance); femaleAssetProvenance = Clean(femaleAssetProvenance);
        if (requestedAssetProvenance is not null && processedAssetLibraryRoot is null) throw new ArgumentException("An exact customization provenance requires a processed asset library.", nameof(requestedAssetProvenance));

        var schema = DbcSchemaCatalog.Load(schemaPath); var sourceHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var targetHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var tables = new List<PlayableRaceCustomizationTablePlan>(); var blockers = new List<string>(); var warnings = new List<string>(); var requiredTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (table, raceName) in Surface)
        {
            cancellationToken.ThrowIfCancellationRequested(); var sourcePath = RequiredTable(sourceDbcRoot, table); var targetPath = RequiredTable(targetDbcRoot, table); var source = WdbcFile.Load(sourcePath); var target = WdbcFile.Load(targetPath);
            if (source.FieldCount != target.FieldCount || source.RecordSize != target.RecordSize) throw new InvalidDataException($"{table}.dbc source/target layouts differ.");
            var resolution = Exact(schema, table, target); if (resolution.KeyStrategy.Kind == DbcRecordKeyKind.NoStableKey) throw new InvalidDataException($"{table}.dbc has no proven stable row identity."); var race = Column(resolution, raceName); sourceHashes[table] = Hash(sourcePath); targetHashes[table] = Hash(targetPath);
            var sourceIndex = DbcRecordIdentity.IndexRows(source, resolution.Columns, resolution.KeyStrategy); var targetIndex = DbcRecordIdentity.IndexRows(target, resolution.Columns, resolution.KeyStrategy); var selected = sourceIndex.Where(pair => source.GetRaw(pair.Value, race) == sourceRaceId).OrderBy(pair => pair.Key).ToArray();
            var occupiedTargetRace = targetIndex.Where(pair => target.GetRaw(pair.Value, race) == targetRaceId).Select(pair => pair.Key).ToArray();
            if (occupiedTargetRace.Length > 0)
            {
                blockers.Add($"{table}.dbc already contains {occupiedTargetRace.Length:N0} row(s) for target race {targetRaceId:N0}; Crucible will not mix an imported customization surface with unexplained existing rows.");
                tables.Add(new(table, raceName, resolution.KeyStrategy.Kind, selected.Length, [])); continue;
            }
            if (selected.Length == 0) warnings.Add($"Source {table}.dbc has no customization rows for race {sourceRaceId:N0}.");

            var physicalKey = DbcRecordIdentity.PhysicalColumn(resolution.Columns, resolution.KeyStrategy); var occupied = targetIndex.Keys.ToHashSet(); var planned = new List<(int SourceRow, uint TargetKey)>(); var rows = new List<PlayableRaceCustomizationRowPlan>(selected.Length); var appended = 0u;
            var maximum = sourceIndex.Keys.Concat(targetIndex.Keys).DefaultIfEmpty().Max(); var next = maximum == uint.MaxValue ? uint.MaxValue : maximum + 1;
            foreach (var pair in selected)
            {
                cancellationToken.ThrowIfCancellationRequested(); KeyValuePair<uint, int>? equivalentTarget = targetIndex.OrderBy(candidate => candidate.Key).Where(candidate => Equivalent(source, pair.Value, targetRaceId, target, candidate.Value, resolution.Columns, race, physicalKey, false)).Select(candidate => (KeyValuePair<uint, int>?)candidate).FirstOrDefault();
                if (equivalentTarget is { } targetMatch)
                {
                    rows.Add(new(table, pair.Key, targetMatch.Key, PlayableRaceCustomizationAction.ReuseEquivalent)); continue;
                }
                (int SourceRow, uint TargetKey)? equivalentPlanned = planned.Where(candidate => Equivalent(source, pair.Value, targetRaceId, source, candidate.SourceRow, resolution.Columns, race, physicalKey, true)).Select(candidate => ((int SourceRow, uint TargetKey)?)candidate).FirstOrDefault();
                if (equivalentPlanned is { } plannedMatch)
                {
                    rows.Add(new(table, pair.Key, plannedMatch.TargetKey, PlayableRaceCustomizationAction.ReuseEquivalent)); continue;
                }

                uint targetKey; PlayableRaceCustomizationAction action;
                if (physicalKey is null)
                {
                    targetKey = checked((uint)target.RowCount + resolution.KeyStrategy.VirtualStart + appended++); action = PlayableRaceCustomizationAction.AppendVirtualRow;
                }
                else if (occupied.Add(pair.Key)) { targetKey = pair.Key; action = PlayableRaceCustomizationAction.AddOriginalId; }
                else
                {
                    if (next == uint.MaxValue && occupied.Contains(next)) throw new InvalidDataException($"{table}.dbc has exhausted its 32-bit physical ID space."); while (!occupied.Add(next)) next = checked(next + 1); targetKey = next; if (next != uint.MaxValue) next++; action = PlayableRaceCustomizationAction.AddRemappedId;
                }
                planned.Add((pair.Value, targetKey)); rows.Add(new(table, pair.Key, targetKey, action));
                if (table.Equals("CharSections", StringComparison.OrdinalIgnoreCase)) foreach (var textureColumn in resolution.Columns.Where(column => column.Name.StartsWith("TextureName[", StringComparison.OrdinalIgnoreCase)))
                {
                    var path = Convert.ToString(source.GetDisplayValue(pair.Value, textureColumn))?.Trim(); if (!string.IsNullOrWhiteSpace(path)) requiredTextures.Add(PatchInputMapper.NormalizeArchivePath(path));
                }
            }
            tables.Add(new(table, raceName, resolution.KeyStrategy.Kind, selected.Length, rows));
        }

        var assets = new Dictionary<string, CreatureDisplayBoundAsset>(StringComparer.OrdinalIgnoreCase); var hashCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (requiredTextures.Count > 0 && processedAssetLibraryRoot is null) warnings.Add($"No processed asset library was supplied for {requiredTextures.Count:N0} CharSections texture path(s); the bundle assumes those exact files already exist in the target client.");
        else if (processedAssetLibraryRoot is not null)
        {
            var index = ClientAssetDependencyService.OpenLibraryLayout(processedAssetLibraryRoot);
            foreach (var texture in requiredTextures.Order(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested(); var preferred = requestedAssetProvenance ?? ProvenanceForTexture(texture, maleAssetProvenance, femaleAssetProvenance); var candidates = ClientAssetDependencyService.FindCandidates(index, texture, cancellationToken); var eligible = preferred is null ? candidates.ToArray() : candidates.Where(candidate => candidate.Provenance.Equals(preferred, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (eligible.Length == 0) { blockers.Add(candidates.Count == 0 ? $"Required CharSections texture is absent from the processed library: {texture}" : $"Required CharSections texture exists only outside bound provenance '{preferred}': {texture}"); continue; }
                var groups = eligible.GroupBy(candidate => HashCached(candidate.SourcePath), StringComparer.OrdinalIgnoreCase).ToArray(); if (groups.Length != 1) { blockers.Add($"Required CharSections texture has {eligible.Length:N0} different-byte candidates{(preferred is null ? string.Empty : $" within provenance '{preferred}'")}: {texture}"); continue; }
                var selected = eligible.OrderBy(candidate => candidate.SourcePath, StringComparer.OrdinalIgnoreCase).First(); if (eligible.Length > 1) warnings.Add($"{texture} has {eligible.Length:N0} byte-identical candidates; selected {selected.SourcePath} deterministically.");
                if (!Path.GetExtension(texture).Equals(".blp", StringComparison.OrdinalIgnoreCase)) { blockers.Add($"CharSections texture path is not a Wrath BLP: {texture}"); continue; }
                try { _ = BlpTextureService.Inspect(selected.SourcePath); }
                catch (Exception exception) when (exception is not OperationCanceledException) { blockers.Add($"CharSections texture is not a valid readable BLP ({texture}): {exception.Message}"); continue; }
                var hash = HashCached(selected.SourcePath);
                if (assets.TryGetValue(texture, out var existing) && !existing.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase)) blockers.Add($"CharSections texture {texture} resolves to different bytes from '{existing.SourcePath}' and '{selected.SourcePath}'."); else assets[texture] = new("char-sections-texture", texture, selected.SourcePath, selected.Provenance, hash);
            }
        }

        var plan = new PlayableRaceCustomizationPromotionPlan(FormatVersion, DateTimeOffset.UtcNow, sourceDbcRoot, targetDbcRoot, schemaPath, Hash(schemaPath), sourceRaceId, targetRaceId, processedAssetLibraryRoot, requestedAssetProvenance, maleAssetProvenance, femaleAssetProvenance, sourceHashes, targetHashes, tables, requiredTextures.Order(StringComparer.OrdinalIgnoreCase).ToArray(), assets.Values.OrderBy(asset => asset.ClientPath, StringComparer.OrdinalIgnoreCase).ToArray(), blockers.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(), warnings.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(), string.Empty);
        return plan with { ContentSha256 = ContentHash(plan) };

        string HashCached(string path) { path = Path.GetFullPath(path); if (hashCache.TryGetValue(path, out var value)) return value; value = Hash(path); hashCache[path] = value; return value; }
    }

    public static void VerifyAssets(PlayableRaceCustomizationPromotionPlan? plan)
    {
        if (plan is null) return;
        foreach (var asset in plan.Assets.DistinctBy(asset => asset.SourcePath, StringComparer.OrdinalIgnoreCase)) if (!Hash(asset.SourcePath).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Bound CharSections asset changed after review: {asset.SourcePath}");
    }

    public static PlayableRaceCustomizationPromotionResult Apply(PlayableRaceCustomizationPromotionPlan plan, string outputDirectory, CancellationToken cancellationToken = default)
    {
        ValidatePlan(plan); if (!plan.Ready) throw new InvalidOperationException($"Customization promotion has {plan.Blockers.Count:N0} blocker(s); nothing was written."); VerifyAssets(plan); VerifyInputs(plan);
        var fresh = CreatePlan(plan.SourceDbcRoot, plan.TargetDbcRoot, plan.SchemaPath, plan.SourceRaceId, plan.TargetRaceId, plan.ProcessedAssetLibraryRoot, plan.RequestedAssetProvenance, plan.MaleAssetProvenance, plan.FemaleAssetProvenance, cancellationToken); if (!fresh.ContentSha256.Equals(plan.ContentSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Customization source/target DBCs, schema, or selected asset bytes changed after review.");
        outputDirectory = Path.GetFullPath(outputDirectory); if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any()) throw new IOException($"Customization output must be new or empty: {outputDirectory}"); var parent = Path.GetDirectoryName(outputDirectory) ?? throw new InvalidOperationException("Customization output has no parent."); Directory.CreateDirectory(parent); var staging = Path.Combine(parent, $".{Path.GetFileName(outputDirectory)}.crucible-{Guid.NewGuid():N}"); Directory.CreateDirectory(staging);
        try
        {
            var schema = DbcSchemaCatalog.Load(plan.SchemaPath); var outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tablePlan in plan.Tables.Where(table => table.AddedRows > 0))
            {
                cancellationToken.ThrowIfCancellationRequested(); var source = WdbcFile.Load(RequiredTable(plan.SourceDbcRoot, tablePlan.Table)); var target = WdbcFile.Load(RequiredTable(plan.TargetDbcRoot, tablePlan.Table)); var resolution = Exact(schema, tablePlan.Table, target); var race = Column(resolution, tablePlan.RaceColumn); var physicalKey = DbcRecordIdentity.PhysicalColumn(resolution.Columns, resolution.KeyStrategy); var sourceRows = DbcRecordIdentity.IndexRows(source, resolution.Columns, resolution.KeyStrategy); var targetRows = DbcRecordIdentity.IndexRows(target, resolution.Columns, resolution.KeyStrategy);
                foreach (var rowPlan in tablePlan.Rows.Where(row => row.AddsRow))
                {
                    if (!sourceRows.TryGetValue(rowPlan.SourceKey, out var sourceRow)) throw new InvalidDataException($"{tablePlan.Table} source row {rowPlan.SourceKey:N0} disappeared after planning."); if (physicalKey is not null && targetRows.ContainsKey(rowPlan.TargetKey)) throw new InvalidDataException($"{tablePlan.Table} target ID {rowPlan.TargetKey:N0} became occupied after planning.");
                    var destination = physicalKey is null ? target.AddBlankRow() : target.AddBlankRow(physicalKey);
                    foreach (var column in resolution.Columns)
                    {
                        if (physicalKey is not null && column.Index == physicalKey.Index) { target.SetRaw(destination, column, rowPlan.TargetKey); continue; }
                        if (column.Index == race.Index) { target.SetRaw(destination, column, plan.TargetRaceId); continue; }
                        if (column.Type == DbcValueType.StringOffset) target.SetDisplayValue(destination, column, source.GetString(source.GetRaw(sourceRow, column))); else target.SetRaw(destination, column, source.GetRaw(sourceRow, column));
                    }
                    var actualKey = DbcRecordIdentity.GetKey(target, destination, resolution.Columns, resolution.KeyStrategy); if (actualKey != rowPlan.TargetKey) throw new InvalidDataException($"{tablePlan.Table} appended key {actualKey:N0}, expected {rowPlan.TargetKey:N0}."); targetRows[actualKey] = destination;
                }
                var output = Path.Combine(staging, tablePlan.Table + ".dbc"); target.Save(output); var reloaded = WdbcFile.Load(output); if (reloaded.RowCount != target.RowCount || reloaded.FieldCount != target.FieldCount) throw new InvalidDataException($"Written {tablePlan.Table}.dbc failed independent structural reload validation."); outputs[tablePlan.Table] = output; hashes[tablePlan.Table] = Hash(output);
            }
            var receipt = Path.Combine(staging, "race-customization-promotion.crucible.json"); var finalFiles = outputs.ToDictionary(pair => pair.Key, pair => Path.Combine(outputDirectory, Path.GetFileName(pair.Value)), StringComparer.OrdinalIgnoreCase); var finalReceipt = Path.Combine(outputDirectory, Path.GetFileName(receipt)); var result = new PlayableRaceCustomizationPromotionResult(outputDirectory, finalReceipt, finalFiles, hashes, plan); File.WriteAllText(receipt, JsonSerializer.Serialize(result, Json)); if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory); Directory.Move(staging, outputDirectory); return result;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private static bool Equivalent(WdbcFile left, int leftRow, uint targetRace, WdbcFile right, int rightRow, IReadOnlyList<DbcColumn> columns, DbcColumn race, DbcColumn? physicalKey, bool transformRightRace)
    {
        foreach (var column in columns)
        {
            if (physicalKey is not null && column.Index == physicalKey.Index) continue;
            if (column.Type == DbcValueType.StringOffset) { if (!left.GetString(left.GetRaw(leftRow, column)).Equals(right.GetString(right.GetRaw(rightRow, column)), StringComparison.Ordinal)) return false; continue; }
            var leftValue = column.Index == race.Index ? targetRace : left.GetRaw(leftRow, column); var rightValue = column.Index == race.Index && transformRightRace ? targetRace : right.GetRaw(rightRow, column); if (leftValue != rightValue) return false;
        }
        return true;
    }

    private static string? ProvenanceForTexture(string path, string? male, string? female)
    {
        var normalized = path.Replace('/', '\\'); if (normalized.Contains("\\Male\\", StringComparison.OrdinalIgnoreCase)) return male; if (normalized.Contains("\\Female\\", StringComparison.OrdinalIgnoreCase)) return female; return male is not null && male.Equals(female, StringComparison.OrdinalIgnoreCase) ? male : null;
    }
    private static string ContentHash(PlayableRaceCustomizationPromotionPlan plan)
    {
        var identity = new { plan.FormatVersion, plan.SourceDbcRoot, plan.TargetDbcRoot, plan.SchemaPath, plan.SchemaSha256, plan.SourceRaceId, plan.TargetRaceId, plan.ProcessedAssetLibraryRoot, plan.RequestedAssetProvenance, plan.MaleAssetProvenance, plan.FemaleAssetProvenance, plan.SourceFileSha256, plan.TargetFileSha256, plan.Tables, plan.RequiredTexturePaths, plan.Assets, plan.Blockers, plan.Warnings }; return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(identity, HashJson)));
    }
    private static void ValidatePlan(PlayableRaceCustomizationPromotionPlan plan) { if (plan.FormatVersion != FormatVersion || !ContentHash(plan).Equals(plan.ContentSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Playable-race customization plan format or content hash is invalid."); }
    private static void VerifyInputs(PlayableRaceCustomizationPromotionPlan plan) { if (!Hash(plan.SchemaPath).Equals(plan.SchemaSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Customization schema changed after planning."); foreach (var pair in plan.SourceFileSha256) if (!Hash(RequiredTable(plan.SourceDbcRoot, pair.Key)).Equals(pair.Value, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Source {pair.Key}.dbc changed after customization planning."); foreach (var pair in plan.TargetFileSha256) if (!Hash(RequiredTable(plan.TargetDbcRoot, pair.Key)).Equals(pair.Value, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Target {pair.Key}.dbc changed after customization planning."); }
    private static DbcSchemaResolution Exact(DbcSchemaCatalog schema, string table, WdbcFile file) { var resolution = schema.ResolveColumns(table, file.FieldCount); if (resolution.MatchKind != DbcSchemaMatchKind.NamedMatch || resolution.Columns.Count != file.FieldCount) throw new InvalidDataException($"{table}.dbc requires an exact named schema; resolved {resolution.MatchKind} and {resolution.Columns.Count:N0}/{file.FieldCount:N0} fields."); return resolution; }
    private static DbcColumn Column(DbcSchemaResolution resolution, string name) => resolution.Columns.FirstOrDefault(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"DBC schema is missing {name}.");
    private static void ValidateRace(uint value, string name) { if (value is < 1 or > 31) throw new ArgumentOutOfRangeException(name, "WotLK race IDs must be from 1 through 31."); }
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string RequiredDirectory(string path, string label) { path = Path.GetFullPath(path ?? string.Empty); if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"{label} does not exist: {path}"); return path; }
    private static string RequiredFile(string path, string label) { path = Path.GetFullPath(path ?? string.Empty); if (!File.Exists(path)) throw new FileNotFoundException($"{label} does not exist.", path); return path; }
    private static string RequiredTable(string root, string table) => Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly).FirstOrDefault(path => Path.GetFileName(path).Equals(table + ".dbc", StringComparison.OrdinalIgnoreCase)) ?? throw new FileNotFoundException($"Required {table}.dbc is unavailable.", Path.Combine(root, table + ".dbc"));
    private static string Hash(string path) { using var stream = File.OpenRead(Path.GetFullPath(path)); return Convert.ToHexString(SHA256.HashData(stream)); }
}
