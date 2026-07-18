namespace WoWCrucible.Core;

public sealed record ItemWearSourceSet(string Source, IReadOnlyDictionary<int, string> SlotFiles)
{
    public override string ToString() => $"{Source} · {SlotFiles.Count:N0} wear texture(s)";
}

public sealed record ItemEquipmentPreview(
    RgbaTexture Atlas,
    M2GeosetSelection Geosets,
    IReadOnlyList<int> AppliedSlots,
    IReadOnlyList<int> MissingSlots);

public static class ItemEquipmentPreviewService
{
    private static readonly CharacterTextureRegion[] SlotRegions =
    [
        CharacterTextureRegion.ArmUpper, CharacterTextureRegion.ArmLower, CharacterTextureRegion.Hand,
        CharacterTextureRegion.TorsoUpper, CharacterTextureRegion.TorsoLower, CharacterTextureRegion.LegUpper,
        CharacterTextureRegion.LegLower, CharacterTextureRegion.Foot
    ];

    public static IReadOnlyList<ItemWearSourceSet> FindWearSources(ItemDisplayInfoRecord display)
    {
        ArgumentNullException.ThrowIfNull(display);
        var sources = new Dictionary<string, Dictionary<int, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in display.Assets.Where(asset => asset.Kind.Equals("wear-texture", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var path in asset.ExistingPaths.Where(File.Exists))
            {
                var source = SourceName(path);
                if (!sources.TryGetValue(source, out var slots)) sources[source] = slots = new Dictionary<int, string>();
                if (!slots.TryGetValue(asset.Slot, out var existing) || Prefer(path, existing)) slots[asset.Slot] = path;
            }
        }
        return sources.OrderByDescending(pair => pair.Value.Count).ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new ItemWearSourceSet(pair.Key, pair.Value)).ToArray();
    }

    public static ItemEquipmentPreview Compose(string baseCharacterTexture, ItemDisplayInfoRecord display, int inventoryType, ItemWearSourceSet source)
    {
        if (string.IsNullOrWhiteSpace(baseCharacterTexture) || !File.Exists(baseCharacterTexture))
            throw new FileNotFoundException("Choose an extracted base character skin atlas (BLP or ordinary image).", baseCharacterTexture);
        return Compose(DecodeTexture(baseCharacterTexture), display, inventoryType, source);
    }

    public static ItemEquipmentPreview Compose(RgbaTexture baseCharacterAtlas, ItemDisplayInfoRecord display, int inventoryType, ItemWearSourceSet source)
    {
        ArgumentNullException.ThrowIfNull(baseCharacterAtlas); ArgumentNullException.ThrowIfNull(display); ArgumentNullException.ThrowIfNull(source);
        var layers = new List<CharacterTextureLayer>(); var applied = new List<int>(); var missing = new List<int>();
        for (var slot = 0; slot < display.WearTextures.Count && slot < SlotRegions.Length; slot++)
        {
            if (string.IsNullOrWhiteSpace(display.WearTextures[slot])) continue;
            if (!source.SlotFiles.TryGetValue(slot, out var path) || !File.Exists(path)) { missing.Add(slot); continue; }
            layers.Add(new(DecodeTexture(path), SlotRegions[slot])); applied.Add(slot);
        }
        var atlas = CharacterTextureComposer.Compose(baseCharacterAtlas, layers);
        return new(atlas, ResolveGeosets(inventoryType, display.GeosetGroups), applied, missing);
    }

    public static M2GeosetSelection ResolveGeosets(int inventoryType, IReadOnlyList<int> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int Value(int index) => index < values.Count ? Math.Max(0, values[index]) : 0;
        var groups = new Dictionary<int, int>();
        void Set(int group, int valueIndex) => groups[group] = Math.Min(99, Value(valueIndex) + 1);
        switch (inventoryType)
        {
            case 4: case 5: case 20: Set(8, 0); Set(10, 1); Set(13, 2); break; // shirt, chest, robe
            case 6: Set(18, 0); break;                                           // waist
            case 7: Set(11, 0); Set(9, 1); Set(13, 2); break;                    // legs
            case 8: Set(5, 0); Set(20, 1); break;                               // feet
            case 10: Set(4, 0); Set(23, 1); break;                              // hands
            case 16: Set(15, 0); break;                                         // cloak
            case 19: Set(12, 0); break;                                         // tabard
        }
        return new M2GeosetSelection(groups, $"ItemDisplayInfo inventory type {inventoryType}");
    }

    public static CharacterTextureRegion RegionForSlot(int slot) => slot >= 0 && slot < SlotRegions.Length
        ? SlotRegions[slot] : throw new ArgumentOutOfRangeException(nameof(slot), "Wear texture slots are numbered 0 through 7.");

    private static RgbaTexture DecodeTexture(string path) => Path.GetExtension(path).Equals(".blp", StringComparison.OrdinalIgnoreCase)
        ? BlpTextureService.Decode(path) : BlpTextureService.DecodeImage(path);
    private static bool Prefer(string candidate, string existing) => Path.GetExtension(candidate).Equals(".blp", StringComparison.OrdinalIgnoreCase) && !Path.GetExtension(existing).Equals(".blp", StringComparison.OrdinalIgnoreCase);
    private static string SourceName(string path)
    {
        var parent = Directory.GetParent(path)?.Name;
        parent = string.IsNullOrWhiteSpace(parent) ? "unidentified source" : parent;
        var stem = Path.GetFileNameWithoutExtension(path);
        var gender = stem.EndsWith("_F", StringComparison.OrdinalIgnoreCase) ? "female" : stem.EndsWith("_M", StringComparison.OrdinalIgnoreCase) ? "male" : "unisex";
        return $"{parent} · {gender}";
    }
}
