namespace WoWCrucible.Core;

public sealed record WorldLightingSkyStop(double Position, string Role, WorldLightColor Color);

public sealed record WorldLightingEnvironmentSample(
    int Time,
    string Clock,
    IReadOnlyList<WorldLightingSkyStop> Sky,
    WorldLightColor GlobalDiffuse,
    WorldLightColor GlobalAmbient,
    WorldLightColor Fog,
    WorldLightColor Sun,
    WorldLightColor CloudBase,
    WorldLightColor CloudEdge,
    WorldLightColor CloudAccent,
    WorldLightColor OceanShallow,
    WorldLightColor OceanDeep,
    WorldLightColor FreshWaterShallow,
    WorldLightColor FreshWaterDeep,
    double SunX,
    double SunY,
    bool SunAboveHorizon,
    IReadOnlyList<float?> FloatBands,
    IReadOnlyList<string> Findings);

public static class WorldLightingEnvironmentService
{
    public static WorldLightingEnvironmentSample Compose(WorldLightProfile profile, int time)
    {
        ArgumentNullException.ThrowIfNull(profile); time = Math.Clamp(time, 0, WorldLightingService.DayUnits); var findings = new List<string>();
        var bands = profile.ColorBands.ToDictionary(band => band.Index); var values = new WorldLightColor[18];
        for (var index = 0; index < values.Length; index++)
        {
            if (!bands.TryGetValue(index, out var band)) { findings.Add($"Color band {index + 1} ({WorldLightingService.ColorBandNames[index]}) is missing."); values[index] = default; }
            else if (band.Keys.Count == 0) { findings.Add($"Color band {band.Id} ({band.Name}) contains no time keys."); values[index] = default; }
            else values[index] = WorldLightingService.Sample(band, time);
        }
        var floatBands = Enumerable.Range(0, 6).Select(index => profile.FloatBands.FirstOrDefault(band => band.Index == index)).Select(band => band is null || band.Keys.Count == 0 ? (float?)null : WorldLightingService.Sample(band, time)).ToArray();
        foreach (var band in profile.FloatBands.Where(band => band.Keys.Count == 0)) findings.Add($"Float band {band.Id} ({band.Name}) contains no time keys.");
        var normalizedDay = time / (double)WorldLightingService.DayUnits; var sunAngle = normalizedDay * Math.PI * 2 - Math.PI / 2; var altitude = Math.Sin(sunAngle);
        var minutes = (int)Math.Round(normalizedDay * 24 * 60) % (24 * 60);
        return new(time, $"{minutes / 60:00}:{minutes % 60:00}",
        [
            new(0.00, WorldLightingService.ColorBandNames[2], values[2]),
            new(0.25, WorldLightingService.ColorBandNames[3], values[3]),
            new(0.50, WorldLightingService.ColorBandNames[4], values[4]),
            new(0.75, WorldLightingService.ColorBandNames[5], values[5]),
            new(1.00, WorldLightingService.ColorBandNames[6], values[6])
        ], values[0], values[1], values[7], values[9], values[10], values[11], values[12], values[14], values[15], values[16], values[17],
            0.5 + Math.Cos(sunAngle) * 0.38, 0.78 - Math.Max(0, altitude) * 0.60, altitude >= 0, floatBands, findings.Concat(profile.Findings).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }
}
