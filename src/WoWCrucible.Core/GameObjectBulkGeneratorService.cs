using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public sealed record GameObjectBulkAsset(string SourcePath, string SourceSha256, string ClientPath, string Provenance, string Kind);
public sealed record GameObjectBulkRow(
    string SourcePath,
    string SourceSha256,
    string ClientPath,
    string Provenance,
    string Name,
    uint DisplayId,
    uint TemplateId,
    bool ReusesDisplay,
    float MinimumX,
    float MinimumY,
    float MinimumZ,
    float MaximumX,
    float MaximumY,
    float MaximumZ,
    IReadOnlyList<string> DependencyPaths);

public sealed record GameObjectBulkPlan(
    int FormatVersion,
    string ContentSha256,
    DateTimeOffset CreatedUtc,
    string DbcPath,
    string DbcSha256,
    string SchemaPath,
    string SchemaSha256,
    string? AssetLibrary,
    string? ClientRoot,
    string SqlProfile,
    IReadOnlyList<GameObjectBulkRow> Rows,
    IReadOnlyList<GameObjectBulkAsset> Assets,
    string Sql,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Findings)
{
    public bool Ready => Rows.Count > 0 && Blockers.Count == 0;
    public int AddedDisplays => Rows.Count(row => !row.ReusesDisplay);
}

public sealed record GameObjectBulkResult(
    string OutputRoot,
    string DbcPath,
    string SqlPath,
    string ManifestPath,
    string PatchPath,
    string ReceiptPath,
    string DbcSha256,
    int AddedDisplays,
    int Templates,
    int PatchEntries);

/// <summary>
/// Clean-room replacement for the useful part of legacy GobGenerator tools.
/// It turns extracted Wrath M2/WMO roots into collision-checked display records,
/// complete gameobject_template INSERTs, and a dependency-complete tiny patch.
/// </summary>
public static class GameObjectBulkGeneratorService
{
    public const int CurrentFormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly HashSet<string> ClientAnchors = new(StringComparer.OrdinalIgnoreCase)
    {
        "World", "Creature", "Doodads", "Doodad", "Buildings", "Item", "Spells", "Spell", "Environments", "Interiors", "Character"
    };

    public static GameObjectBulkPlan CreatePlan(
        string dbcPath,
        string schemaPath,
        IEnumerable<string> sourcePaths,
        uint firstDisplayId = 100_000,
        uint firstTemplateId = 100_000,
        string? assetLibrary = null,
        string? clientRoot = null,
        DatabaseCapabilities? capabilities = null,
        IEnumerable<uint>? occupiedTemplateIds = null,
        CancellationToken cancellationToken = default)
    {
        dbcPath = RequiredFile(dbcPath, "GameObjectDisplayInfo DBC");
        schemaPath = RequiredFile(schemaPath, "DBC schema");
        assetLibrary = OptionalDirectory(assetLibrary, "processed asset library");
        clientRoot = OptionalDirectory(clientRoot, "client-path root");
        var inputs = ExpandSources(sourcePaths, cancellationToken);
        if (inputs.Count == 0) throw new InvalidDataException("Select at least one .m2 or root .wmo file (or a folder containing them).");

        var dbc = WdbcFile.Load(dbcPath);
        var resolution = DbcSchemaCatalog.Load(schemaPath).ResolveColumns("GameObjectDisplayInfo", dbc.FieldCount);
        RequireSchema(dbc, resolution);
        var idColumn = Column(resolution.Columns, "ID");
        var modelColumn = Column(resolution.Columns, "ModelName");
        var existingIds = DbcRecordIdentity.IndexRows(dbc, resolution.Columns, resolution.KeyStrategy);
        var existingModels = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in existingIds)
        {
            var rawModel = dbc.GetString(dbc.GetRaw(pair.Value, modelColumn)); if (string.IsNullOrWhiteSpace(rawModel)) continue;
            string model; try { model = NormalizeClientPath(rawModel); } catch (ArgumentException) { continue; }
            if (!existingModels.TryGetValue(model, out var rows)) existingModels[model] = rows = [];
            rows.Add(pair.Value);
        }

        AssetComparisonIndex? libraryIndex = assetLibrary is null ? null : ClientAssetDependencyService.OpenLibraryLayout(assetLibrary);
        var blockers = new List<string>(); var findings = new List<string>();
        var candidates = new List<Candidate>();
        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var location = Locate(input, libraryIndex, clientRoot);
                var graph = libraryIndex is null
                    ? null
                    : ClientAssetDependencyService.Analyze(libraryIndex, location, cancellationToken);
                if (graph is not null && graph.Blocking.Count > 0)
                {
                    foreach (var issue in graph.Blocking.Take(20)) blockers.Add($"{location.ClientPath}: {issue.ClientPath} · {issue.Message}");
                    if (graph.Blocking.Count > 20) blockers.Add($"{location.ClientPath}: {graph.Blocking.Count - 20:N0} additional dependency blocker(s).");
                }
                Vector3 minimum; Vector3 maximum; IReadOnlyList<GameObjectBulkAsset> assets;
                if (graph is null)
                {
                    var standalone = InspectStandalone(input, location, clientRoot, cancellationToken); minimum = standalone.Minimum; maximum = standalone.Maximum; assets = standalone.Assets;
                }
                else
                {
                    var geometry = Bounds(input, cancellationToken); minimum = geometry.Minimum; maximum = geometry.Maximum;
                    assets = graph.PatchEntries.Select(entry => new GameObjectBulkAsset(Path.GetFullPath(entry.SourcePath), Hash(entry.SourcePath), NormalizeClientPath(entry.ArchivePath),
                        InferProvenance(libraryIndex, entry.SourcePath), DependencyKind(entry.ArchivePath))).ToArray();
                }
                candidates.Add(new(input, Hash(input), location, minimum, maximum, assets));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                blockers.Add($"{input}: {exception.Message}");
            }
        }

        var unique = new List<Candidate>();
        foreach (var group in candidates.GroupBy(candidate => candidate.Location.ClientPath, StringComparer.OrdinalIgnoreCase))
        {
            var hashes = group.Select(candidate => candidate.SourceSha256).Distinct(StringComparer.Ordinal).ToArray();
            if (hashes.Length > 1)
            {
                blockers.Add($"Client path '{group.Key}' is supplied by {hashes.Length:N0} different byte streams. Choose exactly one provenance; a patch cannot contain both.");
                continue;
            }
            unique.Add(group.OrderBy(candidate => candidate.Location.Provenance, StringComparer.OrdinalIgnoreCase).First());
            if (group.Count() > 1) findings.Add($"Collapsed {group.Count():N0} byte-identical selections for '{group.Key}'.");
        }

        var occupiedDisplays = existingIds.Keys.ToHashSet();
        var occupiedTemplates = occupiedTemplateIds?.Where(id => id != 0).ToHashSet() ?? [];
        var nextDisplay = Math.Max(1, firstDisplayId); var nextTemplate = Math.Max(1, firstTemplateId);
        var plannedRows = new List<GameObjectBulkRow>(); var patchAssets = new Dictionary<string, GameObjectBulkAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in unique.OrderBy(value => value.Location.ClientPath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint displayId; var reuse = false;
            if (existingModels.TryGetValue(candidate.Location.ClientPath, out var modelRows))
            {
                var ids = modelRows.Select(row => dbc.GetRaw(row, idColumn)).Distinct().Order().ToArray(); displayId = ids[0]; reuse = true;
                findings.Add(ids.Length == 1
                    ? $"Reused existing display {displayId:N0} for '{candidate.Location.ClientPath}'."
                    : $"'{candidate.Location.ClientPath}' already has {ids.Length:N0} display rows; reused lowest ID {displayId:N0} and created no duplicate.");
            }
            else displayId = Allocate(ref nextDisplay, occupiedDisplays, "GameObjectDisplayInfo");
            var templateId = Allocate(ref nextTemplate, occupiedTemplates, "gameobject_template");
            foreach (var asset in candidate.Assets)
            {
                if (patchAssets.TryGetValue(asset.ClientPath, out var prior) && !prior.SourceSha256.Equals(asset.SourceSha256, StringComparison.Ordinal))
                    blockers.Add($"Dependency path '{asset.ClientPath}' resolves to different bytes from '{prior.Provenance}' and '{asset.Provenance}'. Choose one provenance explicitly.");
                else patchAssets[asset.ClientPath] = asset;
            }
            plannedRows.Add(new(candidate.SourcePath, candidate.SourceSha256, candidate.Location.ClientPath, candidate.Location.Provenance,
                HumanName(candidate.Location.ClientPath), displayId, templateId, reuse,
                candidate.Minimum.X, candidate.Minimum.Y, candidate.Minimum.Z, candidate.Maximum.X, candidate.Maximum.Y, candidate.Maximum.Z,
                candidate.Assets.Select(asset => asset.ClientPath).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray()));
        }

        if (occupiedTemplateIds is null) findings.Add("Template IDs were allocated from the requested start and are protected by INSERT-only SQL, but no live occupied-ID set was supplied. Connected desktop planning verifies every current gameobject_template entry before allocation.");
        var targetCapabilities = capabilities ?? GameObjectTemplateAdapter.CreatePortableCapabilities();
        var sqlRows = plannedRows.SelectMany(row => GameObjectTemplateAdapter.CreatePlan(
            new(row.TemplateId, 5, row.DisplayId, row.Name, string.Empty, string.Empty, string.Empty, 1f, new long[24], string.Empty, string.Empty), targetCapabilities).Rows).ToArray();
        var sql = BuildSql(sqlRows, targetCapabilities.ServerVersion);
        var plan = new GameObjectBulkPlan(CurrentFormatVersion, string.Empty, DateTimeOffset.UtcNow, dbcPath, Hash(dbcPath), schemaPath, Hash(schemaPath), assetLibrary, clientRoot,
            targetCapabilities.ServerVersion, plannedRows, patchAssets.Values.OrderBy(asset => asset.ClientPath, StringComparer.OrdinalIgnoreCase).ToArray(), sql,
            blockers.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(), findings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        return plan with { ContentSha256 = PlanHash(plan) };
    }

    public static void SavePlan(string path, GameObjectBulkPlan plan, bool overwrite = false) { VerifyPlanContent(plan); AtomicJson(path, plan, overwrite); }

    public static GameObjectBulkPlan LoadPlan(string path)
    {
        path = RequiredFile(path, "gameobject bulk plan");
        var plan = JsonSerializer.Deserialize<GameObjectBulkPlan>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("The gameobject bulk plan is empty.");
        if (plan.FormatVersion != CurrentFormatVersion) throw new InvalidDataException($"Unsupported gameobject bulk plan version {plan.FormatVersion}.");
        VerifyPlanContent(plan);
        return plan;
    }

    public static GameObjectBulkResult Apply(GameObjectBulkPlan plan, string outputRoot, CancellationToken cancellationToken = default)
    {
        Verify(plan, cancellationToken);
        if (!plan.Ready) throw new InvalidOperationException($"The gameobject bulk plan has {plan.Blockers.Count:N0} blocker(s).");
        outputRoot = Path.GetFullPath(outputRoot);
        if (Directory.Exists(outputRoot) && Directory.EnumerateFileSystemEntries(outputRoot).Any()) throw new IOException($"Bulk output must be new or empty: {outputRoot}");
        var parent = Path.GetDirectoryName(outputRoot) ?? throw new InvalidOperationException("Bulk output has no parent folder.");
        Directory.CreateDirectory(parent); var stagingRoot = Path.Combine(parent, $".{Path.GetFileName(outputRoot)}.crucible-{Guid.NewGuid():N}"); Directory.CreateDirectory(stagingRoot);
        try
        {
            var dbc = WdbcFile.Load(plan.DbcPath); var resolution = DbcSchemaCatalog.Load(plan.SchemaPath).ResolveColumns("GameObjectDisplayInfo", dbc.FieldCount); RequireSchema(dbc, resolution);
            var id = Column(resolution.Columns, "ID"); var model = Column(resolution.Columns, "ModelName");
            var minX = Column(resolution.Columns, "GeoBoxMinX"); var minY = Column(resolution.Columns, "GeoBoxMinY"); var minZ = Column(resolution.Columns, "GeoBoxMinZ");
            var maxX = Column(resolution.Columns, "GeoBoxMaxX"); var maxY = Column(resolution.Columns, "GeoBoxMaxY"); var maxZ = Column(resolution.Columns, "GeoBoxMaxZ");
            var effect = Column(resolution.Columns, "ObjectEffectPackageID"); var occupied = DbcRecordIdentity.IndexRows(dbc, resolution.Columns, resolution.KeyStrategy);
            foreach (var row in plan.Rows.Where(row => !row.ReusesDisplay))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (occupied.ContainsKey(row.DisplayId)) throw new InvalidOperationException($"Display ID {row.DisplayId:N0} became occupied after planning.");
                var target = dbc.AddBlankRow(); dbc.SetRaw(target, id, row.DisplayId); dbc.SetDisplayValue(target, model, row.ClientPath);
                SetFloat(dbc, target, minX, row.MinimumX); SetFloat(dbc, target, minY, row.MinimumY); SetFloat(dbc, target, minZ, row.MinimumZ);
                SetFloat(dbc, target, maxX, row.MaximumX); SetFloat(dbc, target, maxY, row.MaximumY); SetFloat(dbc, target, maxZ, row.MaximumZ); dbc.SetRaw(target, effect, 0); occupied[row.DisplayId] = target;
            }

            var dbcOutput = Path.Combine(stagingRoot, "DBC", "GameObjectDisplayInfo.dbc"); Directory.CreateDirectory(Path.GetDirectoryName(dbcOutput)!); dbc.Save(dbcOutput, false);
            var sqlOutput = Path.Combine(stagingRoot, "SQL", "gameobject-template.sql"); WriteText(sqlOutput, plan.Sql);
            var patchRoot = Path.Combine(stagingRoot, "Staging"); var patchDbc = Path.Combine(patchRoot, "DBFilesClient", "GameObjectDisplayInfo.dbc"); CopyExact(dbcOutput, patchDbc);
            var entries = new List<PatchEntry> { new(patchDbc, @"DBFilesClient\GameObjectDisplayInfo.dbc") };
            foreach (var asset in plan.Assets)
            {
                cancellationToken.ThrowIfCancellationRequested(); var destination = Path.Combine(patchRoot, asset.ClientPath.Replace('\\', Path.DirectorySeparatorChar)); CopyExact(asset.SourcePath, destination); entries.Add(new(destination, asset.ClientPath));
            }
            var manifest = Path.Combine(stagingRoot, "Manifests", "gameobjects.patch.json");
            PatchManifestService.Save(manifest, "WoW Crucible bulk gameobjects", "patch-Crucible-GameObjects.MPQ", entries,
                policy: new(ExpectedEntryCount: entries.Count, RequiredGlobs: [@"DBFilesClient\GameObjectDisplayInfo.dbc"]));
            var patchDirectory = Path.Combine(stagingRoot, "Patch"); Directory.CreateDirectory(patchDirectory); PatchManifestService.Build(manifest, patchDirectory);
            var planCopy = Path.Combine(stagingRoot, "Reports", "gameobject-bulk-plan.json"); AtomicJson(planCopy, plan, false);
            var receiptPath = Path.Combine(stagingRoot, "Reports", "gameobject-bulk-receipt.json");
            var receipt = new { FormatVersion = 1, AppliedUtc = DateTimeOffset.UtcNow, PlanSha256 = Hash(planCopy), SourceDbcSha256 = plan.DbcSha256, OutputDbcSha256 = Hash(dbcOutput), AddedDisplays = plan.AddedDisplays, Templates = plan.Rows.Count, PatchEntries = entries.Count };
            AtomicJson(receiptPath, receipt, false);
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, false);
            Directory.Move(stagingRoot, outputRoot);
            return new(outputRoot, Path.Combine(outputRoot, "DBC", "GameObjectDisplayInfo.dbc"), Path.Combine(outputRoot, "SQL", "gameobject-template.sql"),
                Path.Combine(outputRoot, "Manifests", "gameobjects.patch.json"), Path.Combine(outputRoot, "Patch", "patch-Crucible-GameObjects.MPQ"), Path.Combine(outputRoot, "Reports", "gameobject-bulk-receipt.json"),
                receipt.OutputDbcSha256, plan.AddedDisplays, plan.Rows.Count, entries.Count);
        }
        catch { if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true); throw; }
    }

    private static IReadOnlyList<string> ExpandSources(IEnumerable<string> sources, CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in sources ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested(); if (string.IsNullOrWhiteSpace(raw)) continue; var path = Path.GetFullPath(raw);
            if (File.Exists(path)) { if (IsRootModel(path)) result.Add(path); continue; }
            if (!Directory.Exists(path)) throw new FileNotFoundException("A selected model source does not exist.", path);
            foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })) { cancellationToken.ThrowIfCancellationRequested(); if (IsRootModel(file)) result.Add(Path.GetFullPath(file)); }
        }
        return result.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsRootModel(string path)
    {
        var extension = Path.GetExtension(path); if (extension.Equals(".m2", StringComparison.OrdinalIgnoreCase)) return true;
        if (!extension.Equals(".wmo", StringComparison.OrdinalIgnoreCase)) return false;
        return !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(path), @"_\d{3}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    private static ClientAssetLocation Locate(string source, AssetComparisonIndex? library, string? clientRoot)
    {
        if (library is not null)
        {
            try { return ClientAssetDependencyService.InferLocation(library, source); }
            catch (InvalidOperationException) { }
        }
        if (clientRoot is not null && IsInside(clientRoot, source)) return new(NormalizeClientPath(Path.GetRelativePath(clientRoot, source)), "Selected client root", source);
        var parts = Path.GetFullPath(source).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var anchor = Array.FindLastIndex(parts, part => ClientAnchors.Contains(part));
        if (anchor >= 0) return new(NormalizeClientPath(string.Join('\\', parts[anchor..])), "Inferred extracted path", source);
        throw new InvalidDataException("The client path cannot be inferred. Select a processed asset library or a client-path root above World/Creature/Doodads.");
    }

    private static (Vector3 Minimum, Vector3 Maximum) Bounds(string source, CancellationToken cancellationToken)
    {
        if (Path.GetExtension(source).Equals(".m2", StringComparison.OrdinalIgnoreCase))
        {
            var geometry = M2PreviewGeometryService.Load(source, visibilityMode: M2PreviewVisibilityMode.AllGeosets); return ValidateBounds(geometry.Minimum, geometry.Maximum, source);
        }
        var wmo = WmoPreviewGeometryService.Load(source, cancellationToken: cancellationToken); return ValidateBounds(wmo.Minimum, wmo.Maximum, source);
    }

    private static (Vector3 Minimum, Vector3 Maximum, IReadOnlyList<GameObjectBulkAsset> Assets) InspectStandalone(string source, ClientAssetLocation location, string? clientRoot, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, GameObjectBulkAsset>(StringComparer.OrdinalIgnoreCase);
        void Add(string physical, string clientPath)
        {
            physical = RequiredFile(physical, $"dependency '{clientPath}'"); clientPath = NormalizeClientPath(clientPath);
            result[clientPath] = new(physical, Hash(physical), clientPath, location.Provenance, DependencyKind(clientPath));
        }
        Add(source, location.ClientPath); var directory = Path.GetDirectoryName(location.ClientPath) ?? string.Empty;
        Vector3 minimum; Vector3 maximum;
        if (Path.GetExtension(source).Equals(".m2", StringComparison.OrdinalIgnoreCase))
        {
            var geometry = M2PreviewGeometryService.Load(source, visibilityMode: M2PreviewVisibilityMode.AllGeosets); (minimum, maximum) = ValidateBounds(geometry.Minimum, geometry.Maximum, source); Add(geometry.SkinPath, CombineClient(directory, Path.GetFileName(geometry.SkinPath)));
            foreach (var slot in geometry.TextureSlots.Where(slot => slot.Type == 0 && !string.IsNullOrWhiteSpace(slot.EmbeddedPath)))
            {
                cancellationToken.ThrowIfCancellationRequested(); var clientPath = NormalizeClientPath(slot.EmbeddedPath!); var physical = ResolveStandalone(source, clientRoot, clientPath);
                if (physical is null) throw new FileNotFoundException($"Embedded M2 texture is missing below the selected client root: {clientPath}"); Add(physical, clientPath);
            }
        }
        else
        {
            var geometry = WmoPreviewGeometryService.Load(source, cancellationToken: cancellationToken);
            (minimum, maximum) = ValidateBounds(geometry.Minimum, geometry.Maximum, source);
            foreach (var group in geometry.Groups) Add(group.Path, CombineClient(directory, Path.GetFileName(group.Path)));
            foreach (var texture in geometry.Materials.SelectMany(material => new[] { material.Texture1, material.Texture2, material.Texture3 }).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested(); var clientPath = NormalizeClientPath(texture!); var physical = ResolveStandalone(source, clientRoot, clientPath);
                if (physical is null) throw new FileNotFoundException($"WMO texture is missing below the selected client root: {clientPath}"); Add(physical, clientPath);
            }
        }
        return (minimum, maximum, result.Values.OrderBy(asset => asset.ClientPath, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static string? ResolveStandalone(string source, string? clientRoot, string clientPath)
    {
        if (clientRoot is not null) { var candidate = Path.Combine(clientRoot, clientPath.Replace('\\', Path.DirectorySeparatorChar)); if (File.Exists(candidate)) return candidate; }
        var local = Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileName(clientPath)); return File.Exists(local) ? local : null;
    }

    private static string CombineClient(string directory, string file) => NormalizeClientPath(string.IsNullOrWhiteSpace(directory) ? file : Path.Combine(directory, file));

    private static (Vector3 Minimum, Vector3 Maximum) ValidateBounds(Vector3 minimum, Vector3 maximum, string source)
    {
        if (!Finite(minimum) || !Finite(maximum) || minimum.X > maximum.X || minimum.Y > maximum.Y || minimum.Z > maximum.Z) throw new InvalidDataException($"Model geometry has invalid bounds: {source}");
        return (minimum, maximum);
    }

    private static string BuildSql(IReadOnlyList<WorldSqlRowPlan> rows, string profile)
    {
        var plan = new WorldContentWritePlan("Bulk gameobject templates", rows, []); var builder = new StringBuilder();
        builder.AppendLine("-- WoW Crucible bulk model-to-gameobject plan"); builder.AppendLine($"-- Schema profile: {profile}");
        builder.AppendLine("-- INSERT-only by design: an occupied template ID aborts instead of replacing project data."); builder.AppendLine("START TRANSACTION;");
        builder.AppendLine(plan.PreviewSql()); builder.AppendLine("COMMIT;"); return builder.ToString();
    }

    private static void Verify(GameObjectBulkPlan plan, CancellationToken cancellationToken)
    {
        if (plan.FormatVersion != CurrentFormatVersion) throw new InvalidDataException($"Unsupported gameobject bulk plan version {plan.FormatVersion}.");
        VerifyPlanContent(plan);
        VerifyHash(plan.DbcPath, plan.DbcSha256, "source DBC"); VerifyHash(plan.SchemaPath, plan.SchemaSha256, "schema");
        foreach (var asset in plan.Assets) { cancellationToken.ThrowIfCancellationRequested(); VerifyHash(asset.SourcePath, asset.SourceSha256, asset.ClientPath); }
        foreach (var row in plan.Rows) { cancellationToken.ThrowIfCancellationRequested(); VerifyHash(row.SourcePath, row.SourceSha256, row.ClientPath); }
        var duplicateDisplays = plan.Rows.Where(row => !row.ReusesDisplay).GroupBy(row => row.DisplayId).FirstOrDefault(group => group.Count() > 1);
        var duplicateTemplates = plan.Rows.GroupBy(row => row.TemplateId).FirstOrDefault(group => group.Count() > 1);
        if (duplicateDisplays is not null || duplicateTemplates is not null) throw new InvalidDataException("The plan contains duplicate allocated IDs.");
        var validation = PatchManifestService.ValidateEntries(plan.Assets.Select(asset => new PatchEntry(asset.SourcePath, asset.ClientPath)));
        if (!validation.Passed) throw new InvalidDataException(string.Join(Environment.NewLine, validation.Errors.Select(error => error.Message)));
    }

    private static void RequireSchema(WdbcFile dbc, DbcSchemaResolution resolution)
    {
        if (dbc.ContainerKind != ClientTableContainerKind.Wdbc || !dbc.AllowsStructuralMutation) throw new NotSupportedException("Bulk display generation currently requires a structurally mutable WDBC table.");
        if (resolution.MatchKind is DbcSchemaMatchKind.FieldCountMismatchFallback or DbcSchemaMatchKind.MissingTableFallback || resolution.Columns.Count != dbc.FieldCount || resolution.KeyStrategy.Kind != DbcRecordKeyKind.PhysicalColumn)
            throw new InvalidDataException("GameObjectDisplayInfo.dbc did not resolve to the exact named WotLK schema with a physical ID.");
        foreach (var name in new[] { "ID", "ModelName", "GeoBoxMinX", "GeoBoxMinY", "GeoBoxMinZ", "GeoBoxMaxX", "GeoBoxMaxY", "GeoBoxMaxZ", "ObjectEffectPackageID" }) _ = Column(resolution.Columns, name);
    }

    private static DbcColumn Column(IReadOnlyList<DbcColumn> columns, string name) => columns.FirstOrDefault(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"GameObjectDisplayInfo schema has no '{name}' column.");
    private static void SetFloat(WdbcFile dbc, int row, DbcColumn column, float value) => dbc.SetDisplayValue(row, column, value.ToString("R", CultureInfo.InvariantCulture));
    private static uint Allocate(ref uint candidate, HashSet<uint> occupied, string domain)
    {
        if (candidate == 0) candidate = 1;
        while (occupied.Contains(candidate)) { if (candidate == uint.MaxValue) throw new OverflowException($"No {domain} ID remains at or after the requested start."); candidate++; }
        var result = candidate; occupied.Add(result); if (candidate < uint.MaxValue) candidate++; return result;
    }

    private static string HumanName(string clientPath)
    {
        var raw = Path.GetFileNameWithoutExtension(clientPath).Replace('_', ' ').Replace('-', ' '); var builder = new StringBuilder();
        for (var index = 0; index < raw.Length; index++) { if (index > 0 && char.IsUpper(raw[index]) && char.IsLower(raw[index - 1])) builder.Append(' '); builder.Append(raw[index]); }
        var value = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)); return value.Length == 0 ? "Crucible Gameobject" : value;
    }

    private static string DependencyKind(string path) => Path.GetExtension(path).ToLowerInvariant() switch { ".m2" => "M2", ".skin" => "SKIN", ".wmo" => "WMO", ".blp" => "BLP", _ => "Asset" };
    private static string InferProvenance(AssetComparisonIndex? index, string source) { if (index is null) return "Selected source"; try { return ClientAssetDependencyService.InferLocation(index, source).Provenance; } catch { return "Selected source"; } }
    private static string NormalizeClientPath(string path) => PatchInputMapper.NormalizeArchivePath(path);
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool IsInside(string root, string path) { var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path)); return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative); }
    private static string? OptionalDirectory(string? path, string label) { if (string.IsNullOrWhiteSpace(path)) return null; path = Path.GetFullPath(path); return Directory.Exists(path) ? path : throw new DirectoryNotFoundException($"The {label} does not exist: {path}"); }
    private static string RequiredFile(string path, string label) { path = Path.GetFullPath(path); return File.Exists(path) ? path : throw new FileNotFoundException($"The {label} does not exist.", path); }
    private static string Hash(string path) { using var stream = File.OpenRead(Path.GetFullPath(path)); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static string PlanHash(GameObjectBulkPlan plan) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan with { ContentSha256 = string.Empty }, JsonOptions)));
    private static void VerifyPlanContent(GameObjectBulkPlan plan) { var actual = PlanHash(plan); if (!actual.Equals(plan.ContentSha256, StringComparison.Ordinal)) throw new InvalidDataException($"Gameobject bulk plan content hash mismatch. Expected {plan.ContentSha256}, found {actual}."); }
    private static void VerifyHash(string path, string expected, string label) { path = RequiredFile(path, label); var actual = Hash(path); if (!actual.Equals(expected, StringComparison.Ordinal)) throw new InvalidOperationException($"{label} changed after planning. Expected {expected}, found {actual}."); }
    private static void CopyExact(string source, string destination) { Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination, false); }
    private static void WriteText(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text, new UTF8Encoding(false)); }
    private static void AtomicJson<T>(string path, T value, bool overwrite) { path = Path.GetFullPath(path); if (File.Exists(path) && !overwrite) throw new IOException($"Output already exists: {path}"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + $".{Guid.NewGuid():N}.tmp"; try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false)); File.Move(temporary, path, overwrite); } finally { if (File.Exists(temporary)) File.Delete(temporary); } }

    private sealed record Candidate(string SourcePath, string SourceSha256, ClientAssetLocation Location, Vector3 Minimum, Vector3 Maximum, IReadOnlyList<GameObjectBulkAsset> Assets);
}
