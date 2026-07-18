namespace WoWCrucible.Core;

public sealed record ItemDisplayAsset(
    string Kind,
    int Slot,
    string Name,
    IReadOnlyList<string> ClientPaths,
    IReadOnlyList<string> ExistingPaths);

public sealed record ItemDisplayInfoRecord(
    uint Id,
    IReadOnlyList<string> ModelNames,
    IReadOnlyList<string> ModelTextures,
    IReadOnlyList<string> InventoryIcons,
    IReadOnlyList<int> GeosetGroups,
    uint Flags,
    uint SpellVisualId,
    uint GroupSoundIndex,
    IReadOnlyList<uint> HelmetGeosetVisibility,
    IReadOnlyList<string> WearTextures,
    uint ItemVisualId,
    uint ParticleColorId,
    IReadOnlyList<ItemDisplayAsset> Assets)
{
    public IReadOnlyList<string> ExistingModels => Assets.Where(asset => asset.Kind == "model").SelectMany(asset => asset.ExistingPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

public static class ItemDisplayInfoService
{
    private static readonly string[] WearTextureDirectories =
    [
        "ArmUpperTexture", "ArmLowerTexture", "HandTexture", "TorsoUpperTexture",
        "TorsoLowerTexture", "LegUpperTexture", "LegLowerTexture", "FootTexture"
    ];

    public static ItemDisplayInfoRecord Resolve(string dbcPath, string? schemaPath, uint displayId, int itemClass = 0, int itemSubclass = 0,
        int inventoryType = 0, string? processedAssetLibrary = null)
    {
        if (displayId == 0) throw new ArgumentOutOfRangeException(nameof(displayId), "Display ID must be positive.");
        var file = WdbcFile.Load(dbcPath);
        var columns = ResolveColumns(file, schemaPath);
        var id = Column(columns, "ID");
        var row = Enumerable.Range(0, file.RowCount).FirstOrDefault(candidate => file.GetRaw(candidate, id) == displayId, -1);
        if (row < 0) throw new KeyNotFoundException($"ItemDisplayInfo.dbc has no display ID {displayId:N0}.");

        string Text(string name) => Convert.ToString(file.GetDisplayValue(row, Column(columns, name))) ?? string.Empty;
        uint Raw(string name) => file.GetRaw(row, Column(columns, name));
        var models = Values(2, index => Text($"ModelName[{index}]"));
        var modelTextures = Values(2, index => Text($"ModelTexture[{index}]"));
        var icons = Values(2, index => Text($"InventoryIcon[{index}]"));
        var geosets = Enumerable.Range(0, 3).Select(index => unchecked((int)Raw($"GeosetGroup[{index}]"))).ToArray();
        var helmet = Enumerable.Range(0, 2).Select(index => Raw($"HelmetGeosetVis[{index}]" )).ToArray();
        var wear = Values(8, index => Text($"Texture[{index}]"));
        var assets = new List<ItemDisplayAsset>();

        for (var index = 0; index < models.Count; index++)
        {
            var name = models[index]; if (string.IsNullOrWhiteSpace(name)) continue;
            var normalized = Path.ChangeExtension(Path.GetFileName(name), ".m2");
            var paths = ObjectDirectories(itemClass, itemSubclass, inventoryType).Select(directory => $"Item\\ObjectComponents\\{directory}\\{normalized}").ToArray();
            assets.Add(new("model", index, name, paths, FindExisting(processedAssetLibrary, paths)));
        }
        for (var index = 0; index < modelTextures.Count; index++)
        {
            var name = modelTextures[index]; if (string.IsNullOrWhiteSpace(name)) continue;
            var fileName = EnsureExtension(Path.GetFileName(name), ".blp");
            var paths = ObjectDirectories(itemClass, itemSubclass, inventoryType).Select(directory => $"Item\\ObjectComponents\\{directory}\\{fileName}").ToArray();
            assets.Add(new("model-texture", index, name, paths, FindExisting(processedAssetLibrary, paths)));
        }
        for (var index = 0; index < icons.Count; index++)
        {
            var name = icons[index]; if (string.IsNullOrWhiteSpace(name)) continue;
            var paths = new[] { $"Interface\\Icons\\{EnsureExtension(Path.GetFileName(name), ".blp")}" };
            assets.Add(new("icon", index, name, paths, FindExisting(processedAssetLibrary, paths)));
        }
        for (var index = 0; index < wear.Count; index++)
        {
            var name = wear[index]; if (string.IsNullOrWhiteSpace(name)) continue;
            var paths = new[] { $"Item\\TextureComponents\\{WearTextureDirectories[index]}\\{EnsureExtension(Path.GetFileName(name), ".blp")}" };
            assets.Add(new("wear-texture", index, name, paths, FindExisting(processedAssetLibrary, paths)));
        }

        return new(displayId, models, modelTextures, icons, geosets, Raw("Flags"), Raw("SpellVisualID"), Raw("GroupSoundIndex"), helmet,
            wear, Raw("ItemVisual"), Raw("ParticleColorID"), assets);
    }

    private static IReadOnlyList<DbcColumn> ResolveColumns(WdbcFile file, string? schemaPath)
    {
        var resolution = !string.IsNullOrWhiteSpace(schemaPath) && File.Exists(schemaPath)
            ? DbcSchemaCatalog.Load(schemaPath).ResolveColumns("ItemDisplayInfo", file.FieldCount)
            : new DbcSchemaResolution(BuiltInColumns(), file.FieldCount == 25 ? DbcSchemaMatchKind.NamedMatch : DbcSchemaMatchKind.FieldCountMismatchFallback, 25, DbcRecordKeyStrategy.Physical(0));
        if (resolution.MatchKind != DbcSchemaMatchKind.NamedMatch || resolution.Columns.Count != 25)
            throw new InvalidDataException($"ItemDisplayInfo.dbc requires the WotLK build-12340 25-field layout; the file has {file.FieldCount:N0} fields and the selected schema resolved {resolution.Columns.Count:N0}.");
        return resolution.Columns;
    }

    private static IReadOnlyList<DbcColumn> BuiltInColumns()
    {
        var names = new[]
        {
            "ID", "ModelName[0]", "ModelName[1]", "ModelTexture[0]", "ModelTexture[1]", "InventoryIcon[0]", "InventoryIcon[1]",
            "GeosetGroup[0]", "GeosetGroup[1]", "GeosetGroup[2]", "Flags", "SpellVisualID", "GroupSoundIndex", "HelmetGeosetVis[0]",
            "HelmetGeosetVis[1]", "Texture[0]", "Texture[1]", "Texture[2]", "Texture[3]", "Texture[4]", "Texture[5]", "Texture[6]",
            "Texture[7]", "ItemVisual", "ParticleColorID"
        };
        var strings = new HashSet<int> { 1, 2, 3, 4, 5, 6, 15, 16, 17, 18, 19, 20, 21, 22 };
        return names.Select((name, index) => new DbcColumn(index, index * 4, 4, name, strings.Contains(index) ? DbcValueType.StringOffset : DbcValueType.UInt32, index == 0)).ToArray();
    }

    private static IReadOnlyList<string> ObjectDirectories(int itemClass, int itemSubclass, int inventoryType)
    {
        var preferred = inventoryType switch
        {
            1 => "Head",
            3 => "Shoulder",
            14 when itemClass == 4 && itemSubclass == 6 => "Shield",
            27 => "Quiver",
            _ when itemClass == 2 || inventoryType is 13 or 14 or 15 or 17 or 21 or 22 or 23 or 25 or 26 => "Weapon",
            _ => "Misc"
        };
        return new[] { preferred, "Weapon", "Shield", "Head", "Shoulder", "Quiver", "Misc" }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> FindExisting(string? libraryRoot, IEnumerable<string> clientPaths)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot)) return [];
        var result = new List<string>();
        foreach (var clientPath in clientPaths)
        {
            var directory = Path.GetDirectoryName(clientPath) ?? string.Empty; var file = Path.GetFileName(clientPath);
            var archiveDirectory = Path.Combine(libraryRoot, "Archives", "Content", directory);
            if (Directory.Exists(archiveDirectory))
                foreach (var provenance in Directory.EnumerateDirectories(archiveDirectory))
                {
                    var candidate = FindFile(provenance, file); if (candidate is not null) result.Add(candidate);
                    if (Path.GetExtension(file).Equals(".blp", StringComparison.OrdinalIgnoreCase)) { var png = FindFile(provenance, Path.ChangeExtension(file, ".png")); if (png is not null) result.Add(png); }
                }
            var looseDirectory = Path.Combine(libraryRoot, "Loose", "Content", directory);
            if (Directory.Exists(looseDirectory))
            {
                var candidate = FindFile(looseDirectory, file); if (candidate is not null) result.Add(candidate);
                if (Path.GetExtension(file).Equals(".blp", StringComparison.OrdinalIgnoreCase)) { var png = FindFile(looseDirectory, Path.ChangeExtension(file, ".png")); if (png is not null) result.Add(png); }
            }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? FindFile(string directory, string fileName) => Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).FirstOrDefault(path => Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase));
    private static string EnsureExtension(string path, string extension) => string.IsNullOrEmpty(Path.GetExtension(path)) ? path + extension : path;
    private static IReadOnlyList<string> Values(int count, Func<int, string> read) => Enumerable.Range(0, count).Select(read).ToArray();
    private static DbcColumn Column(IReadOnlyList<DbcColumn> columns, string name) => columns.FirstOrDefault(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"ItemDisplayInfo schema is missing '{name}'.");
}
