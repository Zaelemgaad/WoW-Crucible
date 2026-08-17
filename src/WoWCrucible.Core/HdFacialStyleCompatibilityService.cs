using System.Security.Cryptography;

namespace WoWCrucible.Core;

public sealed record HdFacialStyleCompatibilityResult(
    string OrdinaryPath,
    string HdPath,
    string OutputPath,
    int OrdinaryRows,
    int HdRows,
    int CompatibleSurfaces,
    int IncompatibleSurfaces,
    int AppendedRows,
    int SkippedMissingRows,
    int ResultRows,
    string OutputSha256);

public static class HdFacialStyleCompatibilityService
{
    private static readonly DbcColumn[] OrdinaryColumns =
    [
        new(0, 0, 4, "RaceID", DbcValueType.UInt32),
        new(1, 4, 4, "SexID", DbcValueType.UInt32),
        new(2, 8, 4, "VariationID", DbcValueType.UInt32),
        new(3, 12, 4, "Geoset[0]", DbcValueType.UInt32),
        new(4, 16, 4, "Geoset[1]", DbcValueType.UInt32),
        new(5, 20, 4, "Geoset[2]", DbcValueType.UInt32),
        new(6, 24, 4, "Geoset[3]", DbcValueType.UInt32),
        new(7, 28, 4, "Geoset[4]", DbcValueType.UInt32)
    ];

    private static readonly DbcColumn[] HdColumns =
    [
        new(0, 0, 4, "ID", DbcValueType.UInt32, true),
        new(1, 4, 4, "RaceID", DbcValueType.UInt32),
        new(2, 8, 4, "SexID", DbcValueType.UInt32),
        new(3, 12, 4, "VariationID", DbcValueType.UInt32),
        new(4, 16, 4, "Geoset[0]", DbcValueType.UInt32),
        new(5, 20, 4, "Geoset[1]", DbcValueType.UInt32),
        new(6, 24, 4, "Geoset[2]", DbcValueType.UInt32),
        new(7, 28, 4, "Geoset[3]", DbcValueType.UInt32),
        new(8, 32, 4, "Geoset[4]", DbcValueType.UInt32)
    ];

    private readonly record struct Surface(uint RaceId, uint SexId);
    private readonly record struct Selector(uint RaceId, uint SexId, uint VariationId);

    public static HdFacialStyleCompatibilityResult Promote(
        string ordinaryPath,
        string hdPath,
        string outputPath,
        bool overwrite = false)
    {
        var ordinaryFullPath = Path.GetFullPath(ordinaryPath);
        var hdFullPath = Path.GetFullPath(hdPath);
        var outputFullPath = Path.GetFullPath(outputPath);
        if (outputFullPath.Equals(ordinaryFullPath, StringComparison.OrdinalIgnoreCase) ||
            outputFullPath.Equals(hdFullPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The compatibility output must be separate from both facial-style inputs.");
        if (File.Exists(outputFullPath) && !overwrite)
            throw new IOException($"Compatibility output already exists: {outputFullPath}. Use --overwrite explicitly.");

        var ordinary = WdbcFile.Load(ordinaryFullPath);
        var hd = WdbcFile.Load(hdFullPath);
        Validate(ordinary, "CharacterFacialHairStyles.dbc", 8, 32);
        Validate(hd, "HDCharacterFacialHairStyles.dbc", 9, 36);

        var ordinaryBySurface = new Dictionary<Surface, List<int>>();
        var ordinaryBySelector = new Dictionary<Selector, List<int>>();
        for (var row = 0; row < ordinary.RowCount; row++)
        {
            var surface = ReadOrdinarySurface(ordinary, row);
            var selector = ReadOrdinarySelector(ordinary, row);
            if (!ordinaryBySurface.TryGetValue(surface, out var surfaceRows)) ordinaryBySurface[surface] = surfaceRows = [];
            if (!ordinaryBySelector.TryGetValue(selector, out var selectorRows)) ordinaryBySelector[selector] = selectorRows = [];
            surfaceRows.Add(row);
            selectorRows.Add(row);
        }

        var target = hd.CloneInMemory();
        var hdBySurface = new Dictionary<Surface, List<int>>();
        var hdBySelector = new Dictionary<Selector, int>();
        var usedIds = new HashSet<uint>();
        for (var row = 0; row < hd.RowCount; row++)
        {
            var id = hd.GetRaw(row, HdColumns[0]);
            if (!usedIds.Add(id)) throw new InvalidDataException($"HDCharacterFacialHairStyles.dbc contains duplicate physical ID {id:N0}.");
            var surface = ReadHdSurface(hd, row);
            var selector = ReadHdSelector(hd, row);
            if (!hdBySurface.TryGetValue(surface, out var surfaceRows)) hdBySurface[surface] = surfaceRows = [];
            if (!hdBySelector.TryAdd(selector, row))
                throw new InvalidDataException($"HDCharacterFacialHairStyles.dbc contains duplicate selector {Describe(selector)}.");
            surfaceRows.Add(row);
        }

        var compatibleSurfaces = 0;
        var incompatibleSurfaces = 0;
        var appendedRows = 0;
        var skippedMissingRows = 0;
        var nextId = usedIds.Count == 0 ? 1u : checked(usedIds.Max() + 1u);

        foreach (var (surface, hdRows) in hdBySurface.OrderBy(pair => pair.Key.RaceId).ThenBy(pair => pair.Key.SexId))
        {
            if (!ordinaryBySurface.TryGetValue(surface, out var ordinaryRows)) continue;
            var missingRows = ordinaryRows.Where(row => !hdBySelector.ContainsKey(ReadOrdinarySelector(ordinary, row))).ToArray();
            if (missingRows.Length == 0) continue;

            var duplicateOrdinarySelector = ordinaryRows.Any(row => ordinaryBySelector[ReadOrdinarySelector(ordinary, row)].Count != 1);
            var commonRows = 0;
            var mismatchedRows = 0;
            foreach (var hdRow in hdRows)
            {
                var selector = ReadHdSelector(hd, hdRow);
                if (!ordinaryBySelector.TryGetValue(selector, out var matches) || matches.Count != 1) continue;
                commonRows++;
                if (!PayloadMatches(ordinary, matches[0], hd, hdRow)) mismatchedRows++;
            }

            if (duplicateOrdinarySelector || commonRows == 0 || mismatchedRows != 0)
            {
                incompatibleSurfaces++;
                skippedMissingRows += missingRows.Length;
                continue;
            }

            compatibleSurfaces++;
            foreach (var ordinaryRow in missingRows.OrderBy(row => ordinary.GetRaw(row, OrdinaryColumns[2])))
            {
                while (!usedIds.Add(nextId)) nextId = checked(nextId + 1u);
                var targetRow = target.AddBlankRow();
                target.SetRaw(targetRow, HdColumns[0], nextId);
                for (var payload = 0; payload < OrdinaryColumns.Length; payload++)
                    target.SetRaw(targetRow, HdColumns[payload + 1], ordinary.GetRaw(ordinaryRow, OrdinaryColumns[payload]));
                hdBySelector.Add(ReadOrdinarySelector(ordinary, ordinaryRow), targetRow);
                appendedRows++;
                nextId = checked(nextId + 1u);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputFullPath)!);
        target.Save(outputFullPath, createBackup: overwrite);
        var reloaded = WdbcFile.Load(outputFullPath);
        Validate(reloaded, "promoted HDCharacterFacialHairStyles.dbc", 9, 36);
        if (reloaded.RowCount != hd.RowCount + appendedRows)
            throw new InvalidDataException(
                $"Promoted HD facial-style row count is {reloaded.RowCount:N0}; expected {hd.RowCount + appendedRows:N0}.");

        using var stream = File.OpenRead(outputFullPath);
        return new(
            ordinaryFullPath,
            hdFullPath,
            outputFullPath,
            ordinary.RowCount,
            hd.RowCount,
            compatibleSurfaces,
            incompatibleSurfaces,
            appendedRows,
            skippedMissingRows,
            reloaded.RowCount,
            Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static void Validate(WdbcFile file, string name, int fields, int recordSize)
    {
        if (file.ContainerKind != ClientTableContainerKind.Wdbc || file.FieldCount != fields || file.RecordSize != recordSize)
            throw new InvalidDataException(
                $"{name} is {file.ContainerKind} with {file.FieldCount:N0} fields and {file.RecordSize:N0}-byte records; " +
                $"the required Wrath layout is WDBC, {fields:N0} fields, and {recordSize:N0}-byte records.");
    }

    private static Surface ReadOrdinarySurface(WdbcFile file, int row) =>
        new(file.GetRaw(row, OrdinaryColumns[0]), file.GetRaw(row, OrdinaryColumns[1]));

    private static Selector ReadOrdinarySelector(WdbcFile file, int row) =>
        new(file.GetRaw(row, OrdinaryColumns[0]), file.GetRaw(row, OrdinaryColumns[1]), file.GetRaw(row, OrdinaryColumns[2]));

    private static Surface ReadHdSurface(WdbcFile file, int row) =>
        new(file.GetRaw(row, HdColumns[1]), file.GetRaw(row, HdColumns[2]));

    private static Selector ReadHdSelector(WdbcFile file, int row) =>
        new(file.GetRaw(row, HdColumns[1]), file.GetRaw(row, HdColumns[2]), file.GetRaw(row, HdColumns[3]));

    private static bool PayloadMatches(WdbcFile ordinary, int ordinaryRow, WdbcFile hd, int hdRow)
    {
        for (var payload = 0; payload < OrdinaryColumns.Length; payload++)
            if (ordinary.GetRaw(ordinaryRow, OrdinaryColumns[payload]) != hd.GetRaw(hdRow, HdColumns[payload + 1])) return false;
        return true;
    }

    private static string Describe(Selector selector) =>
        $"race={selector.RaceId}, sex={selector.SexId}, variation={selector.VariationId}";
}
