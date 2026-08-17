using System.Security.Cryptography;

namespace WoWCrucible.Core;

public sealed record CharSectionsHdCompatibilityResult(
    string PrimaryPath,
    string CompanionPath,
    string OutputPath,
    int PrimaryRows,
    int CompanionRows,
    int MatchedById,
    int MatchedBySelector,
    int AppendedRows,
    int ChangedRows,
    int ChangedCells,
    int ResultRows,
    string OutputSha256);

public static class CharSectionsHdCompatibilityService
{
    private static readonly DbcColumn[] Columns =
    [
        new(0, 0, 4, "ID", DbcValueType.UInt32, true),
        new(1, 4, 4, "RaceID", DbcValueType.UInt32),
        new(2, 8, 4, "SexID", DbcValueType.UInt32),
        new(3, 12, 4, "BaseSection", DbcValueType.UInt32),
        new(4, 16, 4, "TextureName[0]", DbcValueType.StringOffset),
        new(5, 20, 4, "TextureName[1]", DbcValueType.StringOffset),
        new(6, 24, 4, "TextureName[2]", DbcValueType.StringOffset),
        new(7, 28, 4, "Flags", DbcValueType.UInt32),
        new(8, 32, 4, "VariationIndex", DbcValueType.UInt32),
        new(9, 36, 4, "ColorIndex", DbcValueType.UInt32)
    ];

    private readonly record struct CoreIdentity(uint RaceId, uint SexId, uint Section, uint Variation, uint Color);
    private readonly record struct Selector(uint RaceId, uint SexId, uint Section, uint Flags, uint Variation, uint Color);

    public static CharSectionsHdCompatibilityResult Promote(
        string primaryPath,
        string companionPath,
        string outputPath,
        bool overwrite = false)
    {
        var primaryFullPath = Path.GetFullPath(primaryPath);
        var companionFullPath = Path.GetFullPath(companionPath);
        var outputFullPath = Path.GetFullPath(outputPath);
        if (outputFullPath.Equals(companionFullPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The compatibility output cannot replace HDCharSections.dbc.");
        if (File.Exists(outputFullPath) && !overwrite)
            throw new IOException($"Compatibility output already exists: {outputFullPath}. Use --overwrite explicitly.");

        var primary = WdbcFile.Load(primaryFullPath);
        var companion = WdbcFile.Load(companionFullPath);
        Validate(primary, "CharSections.dbc");
        Validate(companion, "HDCharSections.dbc");

        var target = primary.CloneInMemory();
        var targetById = IndexById(target, "CharSections.dbc");
        _ = IndexById(companion, "HDCharSections.dbc");
        var unresolved = new List<int>();
        var matchedById = 0;
        var matchedBySelector = 0;
        var appended = 0;
        var changedRows = new HashSet<int>();
        var changedCells = 0;

        for (var companionRow = 0; companionRow < companion.RowCount; companionRow++)
        {
            var id = companion.GetRaw(companionRow, Columns[0]);
            if (!targetById.TryGetValue(id, out var targetRow))
            {
                unresolved.Add(companionRow);
                continue;
            }

            var primaryIdentity = ReadCoreIdentity(target, targetRow);
            var companionIdentity = ReadCoreIdentity(companion, companionRow);
            if (primaryIdentity != companionIdentity)
                throw new InvalidDataException(
                    $"Physical CharSections ID {id:N0} identifies {Describe(primaryIdentity)} in the primary table " +
                    $"but {Describe(companionIdentity)} in HDCharSections. This is a real ID collision and cannot be promoted safely.");

            changedCells += CopyTextureBindings(companion, companionRow, target, targetRow, changedRows);
            matchedById++;
        }

        var targetBySelector = IndexBySelector(target);
        foreach (var companionRow in unresolved)
        {
            var selector = ReadSelector(companion, companionRow);
            if (targetBySelector.TryGetValue(selector, out var matches))
            {
                if (matches.Count != 1)
                {
                    var ids = string.Join(", ", matches.Select(row => target.GetRaw(row, Columns[0])));
                    throw new InvalidDataException(
                        $"HDCharSections selector {Describe(selector)} matches multiple primary rows ({ids}). " +
                        "Resolve the pre-existing ambiguity before promotion.");
                }

                changedCells += CopyTextureBindings(companion, companionRow, target, matches[0], changedRows);
                matchedBySelector++;
                continue;
            }

            var targetRow = target.AddBlankRow();
            changedCells += CopyEntireRow(companion, companionRow, target, targetRow, changedRows);
            appended++;
            targetById.Add(target.GetRaw(targetRow, Columns[0]), targetRow);
            targetBySelector[selector] = [targetRow];
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputFullPath)!);
        target.Save(outputFullPath, createBackup: overwrite);
        var reloaded = WdbcFile.Load(outputFullPath);
        Validate(reloaded, "promoted CharSections.dbc");
        if (reloaded.RowCount != primary.RowCount + appended)
            throw new InvalidDataException(
                $"Promoted CharSections row count is {reloaded.RowCount:N0}; expected {primary.RowCount + appended:N0}.");

        using var stream = File.OpenRead(outputFullPath);
        var sha256 = Convert.ToHexString(SHA256.HashData(stream));
        return new(
            primaryFullPath,
            companionFullPath,
            outputFullPath,
            primary.RowCount,
            companion.RowCount,
            matchedById,
            matchedBySelector,
            appended,
            changedRows.Count,
            changedCells,
            reloaded.RowCount,
            sha256);
    }

    private static void Validate(WdbcFile file, string name)
    {
        if (file.ContainerKind != ClientTableContainerKind.Wdbc || file.FieldCount != Columns.Length || file.RecordSize != 40)
            throw new InvalidDataException(
                $"{name} is {file.ContainerKind} with {file.FieldCount:N0} fields and {file.RecordSize:N0}-byte records; " +
                "the Wrath CharSections layout requires WDBC, 10 fields, and 40-byte records.");
    }

    private static Dictionary<uint, int> IndexById(WdbcFile file, string name)
    {
        var result = new Dictionary<uint, int>();
        for (var row = 0; row < file.RowCount; row++)
        {
            var id = file.GetRaw(row, Columns[0]);
            if (!result.TryAdd(id, row))
                throw new InvalidDataException($"{name} contains duplicate physical ID {id:N0}.");
        }
        return result;
    }

    private static Dictionary<Selector, List<int>> IndexBySelector(WdbcFile file)
    {
        var result = new Dictionary<Selector, List<int>>();
        for (var row = 0; row < file.RowCount; row++)
        {
            var selector = ReadSelector(file, row);
            if (!result.TryGetValue(selector, out var rows)) result[selector] = rows = [];
            rows.Add(row);
        }
        return result;
    }

    private static CoreIdentity ReadCoreIdentity(WdbcFile file, int row) => new(
        file.GetRaw(row, Columns[1]),
        file.GetRaw(row, Columns[2]),
        file.GetRaw(row, Columns[3]),
        file.GetRaw(row, Columns[8]),
        file.GetRaw(row, Columns[9]));

    private static Selector ReadSelector(WdbcFile file, int row) => new(
        file.GetRaw(row, Columns[1]),
        file.GetRaw(row, Columns[2]),
        file.GetRaw(row, Columns[3]),
        file.GetRaw(row, Columns[7]),
        file.GetRaw(row, Columns[8]),
        file.GetRaw(row, Columns[9]));

    private static int CopyTextureBindings(
        WdbcFile source,
        int sourceRow,
        WdbcFile target,
        int targetRow,
        ISet<int> changedRows) =>
        CopyColumns(source, sourceRow, target, targetRow, Columns.Skip(4).Take(3), changedRows);

    private static int CopyEntireRow(
        WdbcFile source,
        int sourceRow,
        WdbcFile target,
        int targetRow,
        ISet<int> changedRows) =>
        CopyColumns(source, sourceRow, target, targetRow, Columns, changedRows);

    private static int CopyColumns(
        WdbcFile source,
        int sourceRow,
        WdbcFile target,
        int targetRow,
        IEnumerable<DbcColumn> columns,
        ISet<int> changedRows)
    {
        var changed = 0;
        foreach (var column in columns)
        {
            if (column.Type == DbcValueType.StringOffset)
            {
                var sourceValue = Convert.ToString(source.GetDisplayValue(sourceRow, column)) ?? string.Empty;
                var targetValue = Convert.ToString(target.GetDisplayValue(targetRow, column)) ?? string.Empty;
                if (sourceValue.Equals(targetValue, StringComparison.Ordinal)) continue;
                target.SetDisplayValue(targetRow, column, sourceValue);
            }
            else
            {
                var sourceValue = source.GetRaw(sourceRow, column);
                if (sourceValue == target.GetRaw(targetRow, column)) continue;
                target.SetRaw(targetRow, column, sourceValue);
            }
            changed++;
        }
        if (changed > 0) changedRows.Add(targetRow);
        return changed;
    }

    private static string Describe(CoreIdentity value) =>
        $"race={value.RaceId}, sex={value.SexId}, section={value.Section}, variation={value.Variation}, color={value.Color}";

    private static string Describe(Selector value) =>
        $"race={value.RaceId}, sex={value.SexId}, section={value.Section}, flags={value.Flags}, variation={value.Variation}, color={value.Color}";
}
