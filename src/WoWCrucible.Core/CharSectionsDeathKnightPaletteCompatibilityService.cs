using System.Security.Cryptography;

namespace WoWCrucible.Core;

public sealed record CharSectionsDeathKnightPaletteCompatibilityResult(
    string InputPath,
    string OutputPath,
    IReadOnlyList<uint> RaceIds,
    int InputRows,
    int DeathKnightPaletteSurfaces,
    int CandidateRows,
    int ExistingNormalRows,
    int AppendedRows,
    int ResultRows,
    string OutputSha256);

public static class CharSectionsDeathKnightPaletteCompatibilityService
{
    private const uint DeathKnightFlag = 0x04;
    private const uint PlayableFlag = 0x01;
    private const uint HdTextureFlag = 0x10;

    private readonly record struct Surface(uint RaceId, uint SexId, uint ColorIndex);
    private readonly record struct Selector(uint RaceId, uint SexId, uint Section, uint Flags, uint Variation, uint Color);

    public static CharSectionsDeathKnightPaletteCompatibilityResult Expose(
        string inputPath,
        string outputPath,
        IEnumerable<uint> raceIds,
        bool overwrite = false)
    {
        var inputFullPath = Path.GetFullPath(inputPath);
        var outputFullPath = Path.GetFullPath(outputPath);
        if (inputFullPath.Equals(outputFullPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Death Knight palette compatibility requires a separate output path.");
        if (File.Exists(outputFullPath) && !overwrite)
            throw new IOException($"Compatibility output already exists: {outputFullPath}. Use --overwrite explicitly.");

        var selectedRaces = raceIds.Distinct().Order().ToArray();
        if (selectedRaces.Length == 0)
            throw new ArgumentException("At least one race ID is required.", nameof(raceIds));
        if (selectedRaces.Any(raceId => raceId == 0))
            throw new ArgumentOutOfRangeException(nameof(raceIds), "Race IDs must be positive.");

        var source = WdbcFile.Load(inputFullPath);
        CharSectionsTable.Validate(source, "CharSections.dbc");
        var columns = CharSectionsTable.Columns;
        var selectedRaceSet = selectedRaces.ToHashSet();
        var paletteSurfaces = Enumerable.Range(0, source.RowCount)
            .Where(row =>
                selectedRaceSet.Contains(source.GetRaw(row, columns[1])) &&
                source.GetRaw(row, columns[3]) == 0 &&
                (source.GetRaw(row, columns[7]) & DeathKnightFlag) != 0)
            .Select(row => new Surface(
                source.GetRaw(row, columns[1]),
                source.GetRaw(row, columns[2]),
                source.GetRaw(row, columns[9])))
            .Distinct()
            .OrderBy(surface => surface.RaceId)
            .ThenBy(surface => surface.SexId)
            .ThenBy(surface => surface.ColorIndex)
            .ToArray();

        var surfaceSet = paletteSurfaces.ToHashSet();
        var candidates = Enumerable.Range(0, source.RowCount)
            .Where(row =>
                (source.GetRaw(row, columns[7]) & DeathKnightFlag) != 0 &&
                surfaceSet.Contains(new(
                    source.GetRaw(row, columns[1]),
                    source.GetRaw(row, columns[2]),
                    source.GetRaw(row, columns[9]))))
            .ToArray();

        var target = source.CloneInMemory();
        var selectors = Enumerable.Range(0, target.RowCount)
            .Select(row => ReadSelector(target, row))
            .ToHashSet();
        var nextId = target.NextId(columns[0]);
        var existing = 0;
        var appended = 0;

        foreach (var sourceRow in candidates)
        {
            var section = source.GetRaw(sourceRow, columns[3]);
            var normalFlags = section == 1 ? PlayableFlag : PlayableFlag | HdTextureFlag;
            var selector = new Selector(
                source.GetRaw(sourceRow, columns[1]),
                source.GetRaw(sourceRow, columns[2]),
                section,
                normalFlags,
                source.GetRaw(sourceRow, columns[8]),
                source.GetRaw(sourceRow, columns[9]));
            if (!selectors.Add(selector))
            {
                existing++;
                continue;
            }

            var targetRow = target.AddBlankRow();
            CharSectionsTable.CopyRow(source, sourceRow, target, targetRow);
            target.SetRaw(targetRow, columns[0], nextId++);
            target.SetRaw(targetRow, columns[7], normalFlags);
            appended++;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputFullPath)!);
        target.Save(outputFullPath, createBackup: overwrite);
        var written = WdbcFile.Load(outputFullPath);
        CharSectionsTable.Validate(written, "compatible CharSections.dbc");
        if (written.RowCount != source.RowCount + appended)
            throw new InvalidDataException(
                $"Compatible CharSections row count is {written.RowCount:N0}; expected {source.RowCount + appended:N0}.");

        var writtenSelectors = Enumerable.Range(0, written.RowCount).Select(row => ReadSelector(written, row)).ToHashSet();
        foreach (var sourceRow in candidates)
        {
            var section = source.GetRaw(sourceRow, columns[3]);
            var normalFlags = section == 1 ? PlayableFlag : PlayableFlag | HdTextureFlag;
            var expected = new Selector(
                source.GetRaw(sourceRow, columns[1]),
                source.GetRaw(sourceRow, columns[2]),
                section,
                normalFlags,
                source.GetRaw(sourceRow, columns[8]),
                source.GetRaw(sourceRow, columns[9]));
            if (!writtenSelectors.Contains(expected))
                throw new InvalidDataException($"Written CharSections is missing normal selector {Describe(expected)}.");
        }

        using var stream = File.OpenRead(outputFullPath);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        return new(
            inputFullPath,
            outputFullPath,
            selectedRaces,
            source.RowCount,
            paletteSurfaces.Length,
            candidates.Length,
            existing,
            appended,
            written.RowCount,
            hash);
    }

    private static Selector ReadSelector(WdbcFile file, int row)
    {
        var columns = CharSectionsTable.Columns;
        return new(
            file.GetRaw(row, columns[1]),
            file.GetRaw(row, columns[2]),
            file.GetRaw(row, columns[3]),
            file.GetRaw(row, columns[7]),
            file.GetRaw(row, columns[8]),
            file.GetRaw(row, columns[9]));
    }

    private static string Describe(Selector value) =>
        $"race={value.RaceId}, sex={value.SexId}, section={value.Section}, flags={value.Flags}, variation={value.Variation}, color={value.Color}";
}
