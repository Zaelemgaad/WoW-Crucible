namespace WoWCrucible.Core;

public readonly record struct WorldLightColor(byte R, byte G, byte B)
{
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";
    public static WorldLightColor FromPacked(uint value) => new((byte)value, (byte)(value >> 8), (byte)(value >> 16));
    public static WorldLightColor Lerp(WorldLightColor left, WorldLightColor right, float amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        static byte Channel(byte left, byte right, float amount) => (byte)Math.Clamp((int)MathF.Round(left + (right - left) * amount), 0, 255);
        return new(Channel(left.R, right.R, amount), Channel(left.G, right.G, amount), Channel(left.B, right.B, amount));
    }
}

public sealed record WorldLightRecord(uint Id, uint ContinentId, float StoredX, float StoredY, float StoredZ,
    float StoredFalloffStart, float StoredFalloffEnd, IReadOnlyList<uint> LightParamsIds)
{
    public const float CoordinateScale = 36f;
    public float WorldX => StoredX / CoordinateScale;
    public float WorldY => StoredY / CoordinateScale;
    public float WorldZ => StoredZ / CoordinateScale;
    public float FalloffStart => StoredFalloffStart / CoordinateScale;
    public float FalloffEnd => StoredFalloffEnd / CoordinateScale;
    public bool IsGlobal => StoredX == 0 && StoredY == 0 && StoredZ == 0;
}

public sealed record WorldLightParamsRecord(uint Id, uint HighlightSky, uint LightSkyboxId, uint CloudTypeId, float Glow,
    float WaterShallowAlpha, float WaterDeepAlpha, float OceanShallowAlpha, float OceanDeepAlpha);
public sealed record WorldLightSkyboxRecord(uint Id, string ClientModelPath, uint Flags);
public sealed record WorldLightColorKey(int Time, WorldLightColor Color, uint Packed);
public sealed record WorldLightFloatKey(int Time, float Value);
public sealed record WorldLightColorBand(uint Id, int Index, string Name, IReadOnlyList<WorldLightColorKey> Keys);
public sealed record WorldLightFloatBand(uint Id, int Index, string Name, IReadOnlyList<WorldLightFloatKey> Keys);
public sealed record WorldLightProfile(int Slot, uint ParamsId, WorldLightParamsRecord? Parameters, WorldLightSkyboxRecord? Skybox,
    IReadOnlyList<WorldLightColorBand> ColorBands, IReadOnlyList<WorldLightFloatBand> FloatBands, IReadOnlyList<string> Findings);
public sealed record WorldLightingCatalog(string DbcDirectory, IReadOnlyList<WorldLightRecord> Lights,
    IReadOnlyDictionary<uint, WorldLightParamsRecord> Parameters, IReadOnlyDictionary<uint, WorldLightSkyboxRecord> Skyboxes,
    IReadOnlyDictionary<uint, WorldLightColorBand> ColorBands, IReadOnlyDictionary<uint, WorldLightFloatBand> FloatBands,
    IReadOnlyList<string> Findings);

public static class WorldLightingService
{
    public const int DayUnits = 2880;
    public static readonly string[] ColorBandNames =
    [
        "Global diffusion", "Global ambient", "Sky A · zenith", "Sky B", "Sky C", "Sky D", "Sky E", "Fog",
        "Unknown color 9", "Sun / celestial", "Cloud base", "Cloud edge", "Cloud accent", "Unknown color 14",
        "Ocean shallow", "Ocean deep / fatigue", "Fresh water shallow", "Fresh water deep"
    ];
    public static readonly string[] FloatBandNames = ["Float band 1", "Float band 2", "Float band 3", "Float band 4", "Float band 5", "Float band 6"];

    public static WorldLightingCatalog Load(string dbcDirectory)
    {
        var root = Path.GetFullPath(dbcDirectory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"DBC directory not found: {root}");
        var light = LoadExact(root, "Light.dbc", 15, 60); var parameters = LoadExact(root, "LightParams.dbc", 9, 36);
        var colors = LoadExact(root, "LightIntBand.dbc", 34, 136); var floats = LoadExact(root, "LightFloatBand.dbc", 34, 136);
        var skyboxes = LoadExact(root, "LightSkybox.dbc", 3, 12); var findings = new List<string>();

        var parameterRows = Enumerable.Range(0, parameters.RowCount).Select(row => new WorldLightParamsRecord(
            Raw(parameters, row, 0), Raw(parameters, row, 1), Raw(parameters, row, 2), Raw(parameters, row, 3),
            Float(parameters, row, 4), Float(parameters, row, 5), Float(parameters, row, 6), Float(parameters, row, 7), Float(parameters, row, 8)))
            .ToDictionary(item => item.Id);
        var skyboxRows = Enumerable.Range(0, skyboxes.RowCount).Select(row => new WorldLightSkyboxRecord(
            Raw(skyboxes, row, 0), skyboxes.GetString(Raw(skyboxes, row, 1)), Raw(skyboxes, row, 2))).ToDictionary(item => item.Id);

        var colorRows = new Dictionary<uint, WorldLightColorBand>();
        for (var row = 0; row < colors.RowCount; row++)
        {
            var id = Raw(colors, row, 0); var count = Count(colors, row, id, "LightIntBand", findings); var keys = new List<WorldLightColorKey>(count);
            ValidateTimes(colors, row, count, id, "LightIntBand", findings);
            for (var index = 0; index < count; index++) { var packed = Raw(colors, row, 18 + index); keys.Add(new(unchecked((int)Raw(colors, row, 2 + index)), WorldLightColor.FromPacked(packed), packed)); }
            var bandIndex = id == 0 ? -1 : (int)((id - 1) % 18); colorRows[id] = new(id, bandIndex, bandIndex >= 0 ? ColorBandNames[bandIndex] : "Invalid color band", keys);
        }
        var floatRows = new Dictionary<uint, WorldLightFloatBand>();
        for (var row = 0; row < floats.RowCount; row++)
        {
            var id = Raw(floats, row, 0); var count = Count(floats, row, id, "LightFloatBand", findings); var keys = new List<WorldLightFloatKey>(count);
            ValidateTimes(floats, row, count, id, "LightFloatBand", findings);
            for (var index = 0; index < count; index++) keys.Add(new(unchecked((int)Raw(floats, row, 2 + index)), Float(floats, row, 18 + index)));
            var bandIndex = id == 0 ? -1 : (int)((id - 1) % 6); floatRows[id] = new(id, bandIndex, bandIndex >= 0 ? FloatBandNames[bandIndex] : "Invalid float band", keys);
        }
        var lights = Enumerable.Range(0, light.RowCount).Select(row => new WorldLightRecord(Raw(light, row, 0), Raw(light, row, 1),
            Float(light, row, 2), Float(light, row, 3), Float(light, row, 4), Float(light, row, 5), Float(light, row, 6),
            Enumerable.Range(0, 8).Select(index => Raw(light, row, 7 + index)).ToArray())).OrderBy(item => item.ContinentId).ThenBy(item => item.Id).ToArray();

        foreach (var item in lights)
            foreach (var id in item.LightParamsIds.Where(id => id != 0).Distinct())
                if (!parameterRows.ContainsKey(id)) findings.Add($"Light {item.Id} references missing LightParams {id}.");
        foreach (var item in parameterRows.Values)
        {
            if (item.LightSkyboxId != 0 && !skyboxRows.ContainsKey(item.LightSkyboxId)) findings.Add($"LightParams {item.Id} references missing LightSkybox {item.LightSkyboxId}.");
            for (var index = 0; index < 18; index++) if (!colorRows.ContainsKey(ColorBandId(item.Id, index))) findings.Add($"LightParams {item.Id} is missing color band {ColorBandId(item.Id, index)} ({ColorBandNames[index]}).");
            for (var index = 0; index < 6; index++) if (!floatRows.ContainsKey(FloatBandId(item.Id, index))) findings.Add($"LightParams {item.Id} is missing float band {FloatBandId(item.Id, index)}.");
        }
        return new(root, lights, parameterRows, skyboxRows, colorRows, floatRows, findings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public static WorldLightProfile Resolve(WorldLightingCatalog catalog, WorldLightRecord light, int slot)
    {
        if (slot is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(slot));
        var id = light.LightParamsIds[slot]; var findings = new List<string>();
        if (id == 0) return new(slot, 0, null, null, [], [], [$"Light {light.Id} parameter slot {slot + 1} is empty."]);
        if (!catalog.Parameters.TryGetValue(id, out var parameters)) return new(slot, id, null, null, [], [], [$"LightParams {id} does not exist."]);
        WorldLightSkyboxRecord? skybox = null;
        if (parameters.LightSkyboxId != 0 && !catalog.Skyboxes.TryGetValue(parameters.LightSkyboxId, out skybox)) findings.Add($"LightSkybox {parameters.LightSkyboxId} does not exist.");
        var colors = Enumerable.Range(0, 18).Select(index => catalog.ColorBands.GetValueOrDefault(ColorBandId(id, index))).Where(value => value is not null).Cast<WorldLightColorBand>().ToArray();
        var floats = Enumerable.Range(0, 6).Select(index => catalog.FloatBands.GetValueOrDefault(FloatBandId(id, index))).Where(value => value is not null).Cast<WorldLightFloatBand>().ToArray();
        if (colors.Length != 18) findings.Add($"Only {colors.Length}/18 color bands resolve."); if (floats.Length != 6) findings.Add($"Only {floats.Length}/6 float bands resolve.");
        return new(slot, id, parameters, skybox, colors, floats, findings);
    }

    public static WorldLightColor Sample(WorldLightColorBand band, int time) => Sample(band.Keys, time, key => key.Time, key => key.Color, WorldLightColor.Lerp);
    public static float Sample(WorldLightFloatBand band, int time) => Sample(band.Keys, time, key => key.Time, key => key.Value, (left, right, amount) => left + (right - left) * amount);
    public static uint ColorBandId(uint paramsId, int index) => checked((paramsId - 1) * 18 + (uint)index + 1);
    public static uint FloatBandId(uint paramsId, int index) => checked((paramsId - 1) * 6 + (uint)index + 1);

    private static T Sample<TKey, T>(IReadOnlyList<TKey> keys, int time, Func<TKey, int> getTime, Func<TKey, T> getValue, Func<T, T, float, T> lerp)
    {
        if (keys.Count == 0) throw new InvalidOperationException("The lighting band has no time keys.");
        var ordered = keys.GroupBy(key => Mod(getTime(key), DayUnits)).Select(group => (Time: group.Key, Key: group.Last())).OrderBy(pair => pair.Time).ToArray();
        if (ordered.Length == 1) return getValue(ordered[0].Key);
        var target = Mod(time, DayUnits); var rightIndex = Array.FindIndex(ordered, key => key.Time >= target);
        if (rightIndex < 0) rightIndex = 0; var leftIndex = rightIndex == 0 ? ordered.Length - 1 : rightIndex - 1;
        var leftTime = ordered[leftIndex].Time; var rightTime = ordered[rightIndex].Time; var adjustedTarget = target;
        if (rightIndex == 0) { rightTime += DayUnits; if (adjustedTarget < leftTime) adjustedTarget += DayUnits; }
        if (rightTime == leftTime) return getValue(ordered[rightIndex].Key);
        return lerp(getValue(ordered[leftIndex].Key), getValue(ordered[rightIndex].Key), (adjustedTarget - leftTime) / (float)(rightTime - leftTime));
    }

    private static int Mod(int value, int modulus) => (value % modulus + modulus) % modulus;
    private static WdbcFile LoadExact(string root, string name, int fields, int bytes)
    {
        var path = Path.Combine(root, name); if (!File.Exists(path)) throw new FileNotFoundException($"Required build-12340 lighting table not found: {path}", path);
        var file = WdbcFile.Load(path); if (file.ContainerKind != ClientTableContainerKind.Wdbc || file.FieldCount != fields || file.RecordSize != bytes)
            throw new InvalidDataException($"{name} is not the expected build-12340 WDBC layout ({fields} fields / {bytes} bytes); found {file.ContainerKind}, {file.FieldCount} fields / {file.RecordSize} bytes.");
        return file;
    }
    private static uint Raw(WdbcFile file, int row, int field) => file.GetRaw(row, new(field, field * 4, 4, $"Field{field}", DbcValueType.Raw32));
    private static float Float(WdbcFile file, int row, int field) => BitConverter.UInt32BitsToSingle(Raw(file, row, field));
    private static int Count(WdbcFile file, int row, uint id, string table, ICollection<string> findings)
    {
        var raw = Raw(file, row, 1); if (raw <= 16) return (int)raw; findings.Add($"{table} {id} declares {raw} keys; only the 16 physical keys are addressable."); return 16;
    }
    private static void ValidateTimes(WdbcFile file, int row, int count, uint id, string table, ICollection<string> findings)
    {
        var previous = int.MinValue; for (var index = 0; index < count; index++) { var time = unchecked((int)Raw(file, row, 2 + index)); if (time is < 0 or > DayUnits) findings.Add($"{table} {id} key {index + 1} uses out-of-day time {time}."); if (time < previous) findings.Add($"{table} {id} time keys are not monotonic at key {index + 1}."); previous = time; }
    }
}
