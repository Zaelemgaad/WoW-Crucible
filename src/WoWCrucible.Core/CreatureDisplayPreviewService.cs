namespace WoWCrucible.Core;

public sealed record CreatureModelSource(
    string Provenance,
    string ModelPath,
    string? SkinPath,
    IReadOnlyDictionary<int, string> CreatureTextures)
{
    public bool Ready => SkinPath is not null;
    public override string ToString() => $"{(Ready ? "READY" : "MISSING SKIN")} · {Provenance} · {Path.GetFileName(ModelPath)}";
}

public sealed record CreatureDisplayPreview(
    uint DisplayId,
    uint ModelId,
    string ModelClientPath,
    float DisplayScale,
    float ModelScale,
    IReadOnlyList<string> TextureVariations,
    IReadOnlyList<CreatureModelSource> Sources,
    string Finding);

public sealed record CreatureTemplatePreview(
    uint CreatureEntry,
    string Name,
    IReadOnlyList<CreatureDisplayPreview> Displays,
    string Finding);

public sealed record CreatureModelDisplayLookup(
    IReadOnlyList<CreatureDisplayPreview> Displays,
    string? DbcRoot,
    string Finding)
{
    public bool FromProcessedProvenance { get; init; }
}

public sealed record CreatureDisplayCatalogEntry(
    uint DisplayId,
    uint ModelId,
    string ModelClientPath,
    float DisplayScale,
    float ModelScale,
    IReadOnlyList<string> TextureVariations,
    string Finding)
{
    public bool Usable => DisplayId != 0 && ModelId != 0 && !string.IsNullOrWhiteSpace(ModelClientPath) && string.IsNullOrWhiteSpace(Finding);

    public bool Matches(string? query)
    {
        var terms = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return true;
        var searchable = $"{DisplayId} {ModelId} {ModelClientPath} {string.Join(' ', TextureVariations)} {Finding}";
        return terms.All(term => searchable.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    public override string ToString() => $"Display {DisplayId:N0} · Model {ModelId:N0} · {(string.IsNullOrWhiteSpace(ModelClientPath) ? "<missing model path>" : ModelClientPath)}";
}

public sealed record CreatureDisplayCatalog(
    string DbcRoot,
    IReadOnlyList<CreatureDisplayCatalogEntry> Entries,
    int UsableEntries,
    int MissingModelEntries,
    int InvalidEntries);

public sealed class CreatureDisplayPreviewService
{
    public CreatureDisplayCatalog LoadCatalog(string dbcRoot, string? schemaPath = null, CancellationToken cancellationToken = default)
    {
        dbcRoot = Path.GetFullPath(dbcRoot);
        var display = LoadTable(RequiredTablePath(dbcRoot, "CreatureDisplayInfo.dbc"), "CreatureDisplayInfo", schemaPath, CreatureDisplayColumns(), 16);
        var model = LoadTable(RequiredTablePath(dbcRoot, "CreatureModelData.dbc"), "CreatureModelData", schemaPath, CreatureModelColumns(), 28);
        var modelIdColumn = Column(model.Columns, "ID"); var modelNameColumn = Column(model.Columns, "ModelName"); var modelScaleColumn = Column(model.Columns, "ModelScale");
        var models = new Dictionary<uint, (string Path, float Scale)>();
        for (var row = 0; row < model.File.RowCount; row++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var id = model.File.GetRaw(row, modelIdColumn); if (models.ContainsKey(id)) continue;
            var rawPath = Convert.ToString(model.File.GetDisplayValue(row, modelNameColumn)) ?? string.Empty;
            var path = string.IsNullOrWhiteSpace(rawPath) ? string.Empty : NormalizeModel(rawPath); var scale = ReadFloat(model.File, row, modelScaleColumn);
            models[id] = (path, float.IsFinite(scale) && scale > 0 ? scale : 1f);
        }

        var displayIdColumn = Column(display.Columns, "ID"); var displayModelColumn = Column(display.Columns, "ModelID"); var displayScaleColumn = Column(display.Columns, "CreatureModelScale");
        var textureColumns = Enumerable.Range(0, 3).Select(index => Column(display.Columns, $"TextureVariation[{index}]")).ToArray();
        var entries = new List<CreatureDisplayCatalogEntry>(display.File.RowCount); var usable = 0; var missingModel = 0; var invalid = 0;
        for (var row = 0; row < display.File.RowCount; row++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var displayId = display.File.GetRaw(row, displayIdColumn); var modelId = display.File.GetRaw(row, displayModelColumn);
            var displayScale = ReadFloat(display.File, row, displayScaleColumn); if (!float.IsFinite(displayScale) || displayScale <= 0) displayScale = 1f;
            string modelPath; float modelScale; string finding;
            if (!models.TryGetValue(modelId, out var modelValue)) { modelPath = string.Empty; modelScale = 1f; finding = $"CreatureModelData.dbc has no model ID {modelId:N0}."; missingModel++; }
            else if (string.IsNullOrWhiteSpace(modelValue.Path)) { modelPath = string.Empty; modelScale = modelValue.Scale; finding = $"CreatureModelData {modelId:N0} has no model path."; invalid++; }
            else { modelPath = modelValue.Path; modelScale = modelValue.Scale; finding = displayId == 0 ? "Display ID 0 cannot be assigned to a creature template." : string.Empty; if (displayId == 0) invalid++; else usable++; }
            var textures = textureColumns.Select(column => CreatureTexturePath(modelPath, Convert.ToString(display.File.GetDisplayValue(row, column)) ?? string.Empty)).ToArray();
            entries.Add(new(displayId, modelId, modelPath, displayScale, modelScale, textures, finding));
        }
        return new(dbcRoot, entries.OrderBy(entry => entry.DisplayId).ToArray(), usable, missingModel, invalid);
    }

    public async Task<IReadOnlyList<CreatureTemplatePreview>> ResolveCreaturesAsync(DatabaseConnectionProfile profile, string dbcRoot, string? schemaPath,
        string? processedAssetLibrary, IEnumerable<uint> creatureEntries, CancellationToken cancellationToken = default)
    {
        dbcRoot = Path.GetFullPath(dbcRoot); var displayPath = RequiredTablePath(dbcRoot, "CreatureDisplayInfo.dbc"); var modelPath = RequiredTablePath(dbcRoot, "CreatureModelData.dbc");
        if (!File.Exists(displayPath) || !File.Exists(modelPath)) throw new FileNotFoundException("Creature preview requires CreatureDisplayInfo.dbc and CreatureModelData.dbc in the configured server DBC folder.");
        var displays = LoadTable(displayPath, "CreatureDisplayInfo", schemaPath, CreatureDisplayColumns(), 16); var models = LoadTable(modelPath, "CreatureModelData", schemaPath, CreatureModelColumns(), 28);
        var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken); var template = capabilities.FindTable("creature_template") ?? throw new NotSupportedException("The connected world database has no creature_template table."); var entryColumn = template.Find("entry") ?? template.Find("Entry") ?? throw new NotSupportedException("creature_template has no entry identity column.");
        var mapping = capabilities.FindTable("creature_template_model"); var result = new List<CreatureTemplatePreview>(); var workspace = new SqlWorkspaceService();
        foreach (var entry in creatureEntries.Where(value => value != 0).Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested(); var row = await workspace.ReadRowAsync(profile, template, new Dictionary<string, object?> { [entryColumn.Name] = entry }, cancellationToken);
            if (row is null) { result.Add(new(entry, string.Empty, [], $"creature_template has no entry {entry}.")); continue; }
            var name = Convert.ToString(Value(row.Values, "name")) ?? $"Creature {entry}"; var links = new List<(uint DisplayId, float Scale)>();
            if (mapping is not null)
            {
                var creatureColumn = mapping.Find("CreatureID") ?? mapping.Find("creatureid") ?? mapping.Find("entry"); var displayColumn = mapping.Find("CreatureDisplayID") ?? mapping.Find("displayid");
                if (creatureColumn is null || displayColumn is null) throw new NotSupportedException("creature_template_model does not expose recognizable creature and display columns.");
                var page = await workspace.ReadPageAsync(profile, mapping, 0, 500, filterColumn: creatureColumn.Name, filterValue: entry.ToString(System.Globalization.CultureInfo.InvariantCulture), sortColumn: mapping.Find("Idx")?.Name, cancellationToken: cancellationToken);
                foreach (var modelRow in page.Rows) { var displayId = UInt(Value(modelRow.Values, displayColumn.Name)); if (displayId != 0) links.Add((displayId, Float(Value(modelRow.Values, "DisplayScale"), 1f))); }
            }
            else
            {
                for (var slot = 1; slot <= 4; slot++) { var displayId = UInt(Value(row.Values, $"modelid{slot}")); if (displayId != 0) links.Add((displayId, 1f)); }
            }
            var resolved = new List<CreatureDisplayPreview>();
            foreach (var link in links.DistinctBy(link => link.DisplayId))
            {
                try { resolved.Add(ResolveDisplay(displays.File, displays.Columns, models.File, models.Columns, link.DisplayId, processedAssetLibrary, link.Scale, cancellationToken)); }
                catch (Exception exception) when (exception is not OperationCanceledException) { resolved.Add(new(link.DisplayId, 0, string.Empty, link.Scale, 1f, [], [], exception.Message)); }
            }
            result.Add(new(entry, name, resolved, links.Count == 0 ? "No nonzero creature display mapping is present." : resolved.Any(display => display.Sources.Any(source => source.Ready)) ? string.Empty : "Display mappings resolved, but no same-provenance WotLK M2/SKIN pair is ready in the processed library."));
        }
        return result;
    }

    public CreatureDisplayPreview ResolveDisplay(string dbcRoot, string? schemaPath, uint displayId, string? processedAssetLibrary = null, CancellationToken cancellationToken = default)
    {
        dbcRoot = Path.GetFullPath(dbcRoot); var display = LoadTable(RequiredTablePath(dbcRoot, "CreatureDisplayInfo.dbc"), "CreatureDisplayInfo", schemaPath, CreatureDisplayColumns(), 16); var model = LoadTable(RequiredTablePath(dbcRoot, "CreatureModelData.dbc"), "CreatureModelData", schemaPath, CreatureModelColumns(), 28);
        return ResolveDisplay(display.File, display.Columns, model.File, model.Columns, displayId, processedAssetLibrary, 1f, cancellationToken);
    }

    public IReadOnlyList<CreatureDisplayPreview> ResolveModelDisplays(string dbcRoot, string? schemaPath, string modelClientPath, string? processedAssetLibrary = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelClientPath)) throw new ArgumentException("A creature model client path is required.", nameof(modelClientPath));
        dbcRoot = Path.GetFullPath(dbcRoot); var display = LoadTable(RequiredTablePath(dbcRoot, "CreatureDisplayInfo.dbc"), "CreatureDisplayInfo", schemaPath, CreatureDisplayColumns(), 16); var model = LoadTable(RequiredTablePath(dbcRoot, "CreatureModelData.dbc"), "CreatureModelData", schemaPath, CreatureModelColumns(), 28);
        var normalized = NormalizeModel(modelClientPath); var modelIds = new HashSet<uint>(); var modelIdColumn = Column(model.Columns, "ID"); var modelNameColumn = Column(model.Columns, "ModelName");
        for (var row = 0; row < model.File.RowCount; row++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var raw = Convert.ToString(model.File.GetDisplayValue(row, modelNameColumn)) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(raw) && NormalizeModel(raw).Equals(normalized, StringComparison.OrdinalIgnoreCase)) modelIds.Add(model.File.GetRaw(row, modelIdColumn));
        }
        if (modelIds.Count == 0) return [];
        var displayIdColumn = Column(display.Columns, "ID"); var displayModelColumn = Column(display.Columns, "ModelID"); var ids = new List<uint>();
        for (var row = 0; row < display.File.RowCount; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (modelIds.Contains(display.File.GetRaw(row, displayModelColumn))) ids.Add(display.File.GetRaw(row, displayIdColumn));
        }
        return ids.Where(id => id != 0).Distinct().Order().Select(id => ResolveDisplay(display.File, display.Columns, model.File, model.Columns, id, processedAssetLibrary, 1f, cancellationToken)).ToArray();
    }

    public CreatureModelDisplayLookup ResolveModelDisplaysForProvenance(string? targetDbcRoot, string? schemaPath, string modelClientPath,
        string? processedAssetLibrary, string provenance, CancellationToken cancellationToken = default)
    {
        string? targetFinding = null;
        if (!string.IsNullOrWhiteSpace(targetDbcRoot) && HasCreatureTables(targetDbcRoot))
        {
            try
            {
                var targetDisplays = ResolveModelDisplays(targetDbcRoot, schemaPath, modelClientPath, processedAssetLibrary, cancellationToken);
                if (targetDisplays.Count > 0)
                    return new(targetDisplays, Path.GetFullPath(targetDbcRoot), "Resolved from the configured target/server DBCs.");
                targetFinding = $"The configured target/server DBCs contain no record for {NormalizeModel(modelClientPath)}.";
            }
            catch (InvalidDataException exception)
            {
                targetFinding = $"The configured target/server creature DBCs are not compatible with the WotLK build-12340 layout: {exception.Message}";
            }
        }
        else targetFinding = "The configured target/server folder does not contain a complete CreatureDisplayInfo/CreatureModelData pair.";

        var sourceRoot = ProcessedProvenanceDbcRoot(processedAssetLibrary, provenance);
        if (sourceRoot is null)
            return new([], null, $"{targetFinding} Processed provenance '{provenance}' also has no complete creature DBC pair.");
        try
        {
            var sourceDisplays = ResolveModelDisplays(sourceRoot, schemaPath, modelClientPath, processedAssetLibrary, cancellationToken);
            return sourceDisplays.Count == 0
                ? new([], sourceRoot, $"{targetFinding} The exact processed provenance DBC pair also contains no matching record.") { FromProcessedProvenance = true }
                : new(sourceDisplays, sourceRoot, $"{targetFinding} Resolved from processed provenance '{provenance}'.") { FromProcessedProvenance = true };
        }
        catch (InvalidDataException exception)
        {
            return new([], sourceRoot, $"Processed provenance '{provenance}' has creature DBCs, but they are not compatible with the WotLK build-12340 layout: {exception.Message}") { FromProcessedProvenance = true };
        }
    }

    public static string? ResolveSameProvenanceAsset(string? libraryRoot, string provenance, string rawClientPath)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot) || string.IsNullOrWhiteSpace(rawClientPath)) return null; var clientPath = PatchInputMapper.NormalizeArchivePath(rawClientPath); var directory = Path.GetDirectoryName(clientPath) ?? string.Empty; var name = Path.GetFileName(clientPath);
        if (provenance.Equals("Loose", StringComparison.OrdinalIgnoreCase)) return FindFile(Path.Combine(libraryRoot, "Loose", "Content", directory), name);
        return FindFile(Path.Combine(libraryRoot, "Archives", "Content", directory, provenance), name);
    }

    private static CreatureDisplayPreview ResolveDisplay(WdbcFile displayFile, IReadOnlyList<DbcColumn> displayColumns, WdbcFile modelFile, IReadOnlyList<DbcColumn> modelColumns,
        uint displayId, string? libraryRoot, float sqlScale, CancellationToken cancellationToken)
    {
        if (displayId == 0) throw new ArgumentOutOfRangeException(nameof(displayId), "Creature display ID must be positive."); var displayRow = Row(displayFile, displayColumns, displayId, "CreatureDisplayInfo"); var modelId = displayFile.GetRaw(displayRow, Column(displayColumns, "ModelID")); var modelRow = Row(modelFile, modelColumns, modelId, "CreatureModelData");
        var modelClientPath = NormalizeModel(Convert.ToString(modelFile.GetDisplayValue(modelRow, Column(modelColumns, "ModelName"))) ?? string.Empty); if (string.IsNullOrWhiteSpace(modelClientPath)) throw new InvalidDataException($"CreatureModelData {modelId} has no model client path.");
        var textureNames = Enumerable.Range(0, 3).Select(index => Convert.ToString(displayFile.GetDisplayValue(displayRow, Column(displayColumns, $"TextureVariation[{index}]"))) ?? string.Empty).ToArray(); var texturePaths = textureNames.Select(name => CreatureTexturePath(modelClientPath, name)).ToArray();
        var sources = FindModelSources(libraryRoot, modelClientPath, texturePaths, cancellationToken);
        var displayScale = ReadFloat(displayFile, displayRow, Column(displayColumns, "CreatureModelScale")) * sqlScale;
        var modelScale = ReadFloat(modelFile, modelRow, Column(modelColumns, "ModelScale"));
        var finding = sources.Count == 0 ? $"No extracted source matches {modelClientPath}." : sources.Any(source => source.Ready) ? string.Empty : "M2 sources exist, but their same-provenance view-0 SKIN is missing.";
        return new(displayId, modelId, modelClientPath, displayScale, modelScale, texturePaths, sources, finding);
    }

    private static IReadOnlyList<CreatureModelSource> FindModelSources(string? libraryRoot, string modelClientPath, IReadOnlyList<string> texturePaths, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot)) return []; var directory = Path.GetDirectoryName(modelClientPath) ?? string.Empty; var fileName = Path.GetFileName(modelClientPath); var result = new List<CreatureModelSource>(); var archiveDirectory = Path.Combine(libraryRoot, "Archives", "Content", directory);
        if (Directory.Exists(archiveDirectory)) foreach (var provenanceDirectory in Directory.EnumerateDirectories(archiveDirectory)) { cancellationToken.ThrowIfCancellationRequested(); var model = FindFile(provenanceDirectory, fileName); if (model is not null) result.Add(Source(Path.GetFileName(provenanceDirectory), model, texturePaths, libraryRoot)); }
        var looseDirectory = Path.Combine(libraryRoot, "Loose", "Content", directory); var looseModel = FindFile(looseDirectory, fileName); if (looseModel is not null) result.Add(Source("Loose", looseModel, texturePaths, libraryRoot));
        return result.OrderByDescending(source => source.Ready).ThenByDescending(source => source.CreatureTextures.Count).ThenBy(source => source.Provenance, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static CreatureModelSource Source(string provenance, string modelPath, IReadOnlyList<string> texturePaths, string libraryRoot)
    {
        var skin = FindFile(Path.GetDirectoryName(modelPath)!, Path.GetFileNameWithoutExtension(modelPath) + "00.skin"); var textures = new Dictionary<int, string>(); for (var index = 0; index < texturePaths.Count; index++) { var path = ResolveSameProvenanceAsset(libraryRoot, provenance, texturePaths[index]); if (path is not null) textures[index] = path; } return new(provenance, modelPath, skin, textures);
    }

    private static (WdbcFile File, IReadOnlyList<DbcColumn> Columns) LoadTable(string path, string table, string? schemaPath, IReadOnlyList<DbcColumn> builtIn, int fields)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Required {table}.dbc is unavailable.", path); var file = WdbcFile.Load(path); var resolution = !string.IsNullOrWhiteSpace(schemaPath) && File.Exists(schemaPath) ? DbcSchemaCatalog.Load(schemaPath).ResolveColumns(table, file.FieldCount) : new DbcSchemaResolution(builtIn, file.FieldCount == fields ? DbcSchemaMatchKind.NamedMatch : DbcSchemaMatchKind.FieldCountMismatchFallback, fields, DbcRecordKeyStrategy.Physical(0));
        if (resolution.MatchKind != DbcSchemaMatchKind.NamedMatch || resolution.Columns.Count != fields) throw new InvalidDataException($"{table}.dbc requires the WotLK build-12340 {fields}-field layout; file/schema resolved {file.FieldCount}/{resolution.Columns.Count}."); return (file, resolution.Columns);
    }

    private static bool HasCreatureTables(string root) => FindTableFile(root, "CreatureDisplayInfo.dbc") is not null && FindTableFile(root, "CreatureModelData.dbc") is not null;

    private static string? ProcessedProvenanceDbcRoot(string? libraryRoot, string provenance)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot) || string.IsNullOrWhiteSpace(provenance)) return null;
        var root = Path.Combine(Path.GetFullPath(libraryRoot), "Archives", "Content", "DBFilesClient", provenance);
        return HasCreatureTables(root) ? root : null;
    }

    private static string? FindTableFile(string root, string fileName)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
        return Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase));
    }

    private static string RequiredTablePath(string root, string fileName)
        => FindTableFile(root, fileName) ?? throw new FileNotFoundException($"Required {fileName} is unavailable in the configured DBC folder.", Path.Combine(root, fileName));

    private static IReadOnlyList<DbcColumn> CreatureDisplayColumns() => Columns(["ID", "ModelID", "SoundID", "ExtendedDisplayInfoID", "CreatureModelScale", "CreatureModelAlpha", "TextureVariation[0]", "TextureVariation[1]", "TextureVariation[2]", "PortraitTextureName", "BloodLevel", "BloodID", "NPCSoundID", "ParticleColorID", "CreatureGeosetData", "ObjectEffectPackageID"], [4, 6, 7, 8, 9]);
    private static IReadOnlyList<DbcColumn> CreatureModelColumns() => Columns(["ID", "Flags", "ModelName", "SizeClass", "ModelScale", "BloodID", "FootprintTextureID", "FootprintTextureLength", "FootprintTextureWidth", "FootprintParticleScale", "FoleyMaterialID", "FootstepShakeSize", "DeathThudShakeSize", "SoundID", "CollisionWidth", "CollisionHeight", "MountHeight", "GeoBoxMinX", "GeoBoxMinY", "GeoBoxMinZ", "GeoBoxMaxX", "GeoBoxMaxY", "GeoBoxMaxZ", "WorldEffectScale", "AttachedEffectScale", "MissileCollisionRadius", "MissileCollisionPush", "MissileCollisionRaise"], [2, 4, 7, 8, 9, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27]);
    private static IReadOnlyList<DbcColumn> Columns(IReadOnlyList<string> names, IReadOnlyList<int> special) { var strings = special.Where(index => names[index].Contains("Name", StringComparison.OrdinalIgnoreCase) || names[index].Contains("TextureVariation", StringComparison.OrdinalIgnoreCase)).ToHashSet(); var floats = special.Except(strings).ToHashSet(); return names.Select((name, index) => new DbcColumn(index, index * 4, 4, name, strings.Contains(index) ? DbcValueType.StringOffset : floats.Contains(index) ? DbcValueType.Float32 : DbcValueType.UInt32, index == 0)).ToArray(); }
    private static int Row(WdbcFile file, IReadOnlyList<DbcColumn> columns, uint id, string table) { var idColumn = Column(columns, "ID"); var row = Enumerable.Range(0, file.RowCount).FirstOrDefault(index => file.GetRaw(index, idColumn) == id, -1); return row >= 0 ? row : throw new KeyNotFoundException($"{table}.dbc has no ID {id}."); }
    private static DbcColumn Column(IReadOnlyList<DbcColumn> columns, string name) => columns.FirstOrDefault(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"Creature preview schema is missing {name}.");
    private static float ReadFloat(WdbcFile file, int row, DbcColumn column) => BitConverter.Int32BitsToSingle(unchecked((int)file.GetRaw(row, column)));
    private static string NormalizeModel(string path)
    {
        path = path.Replace('/', '\\').TrimStart('\\');
        if (Path.GetExtension(path).Equals(".mdx", StringComparison.OrdinalIgnoreCase))
            path = Path.ChangeExtension(path, ".m2");
        else if (string.IsNullOrEmpty(Path.GetExtension(path)))
            path += ".m2";
        return PatchInputMapper.NormalizeArchivePath(path);
    }

    private static string CreatureTexturePath(string modelPath, string texture)
    {
        if (string.IsNullOrWhiteSpace(texture)) return string.Empty;
        texture = texture.Replace('/', '\\').TrimStart('\\');
        if (string.IsNullOrEmpty(Path.GetExtension(texture))) texture += ".blp";
        return texture.Contains('\\')
            ? PatchInputMapper.NormalizeArchivePath(texture)
            : PatchInputMapper.NormalizeArchivePath(Path.Combine(Path.GetDirectoryName(modelPath) ?? string.Empty, texture));
    }
    private static string? FindFile(string directory, string fileName) => string.IsNullOrWhiteSpace(fileName) || !Directory.Exists(directory) ? null : Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).FirstOrDefault(path => Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase));
    private static object? Value(IReadOnlyDictionary<string, object?> values, string name) => values.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    private static uint UInt(object? value) { try { return Convert.ToUInt32(value, System.Globalization.CultureInfo.InvariantCulture); } catch { return 0; } }
    private static float Float(object? value, float fallback) { try { return Convert.ToSingle(value ?? fallback, System.Globalization.CultureInfo.InvariantCulture); } catch { return fallback; } }
}
