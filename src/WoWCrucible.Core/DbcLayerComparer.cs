using System.Security.Cryptography;

namespace WoWCrucible.Core;

public enum DbcLayerStatus { BaseOnly, OverrideOnly, Identical, Overridden }
public sealed record DbcLayerEntry(string Name, string? BasePath, string? OverridePath, string EffectivePath, DbcLayerStatus Status, long EffectiveSize);
public sealed record DbcRowComparison(int AddedRows, int RemovedRows, int ModifiedRows, long ModifiedCells);

public static class DbcLayerComparer
{
    public static IReadOnlyList<DbcLayerEntry> CompareDirectories(string baseDirectory, string overrideDirectory)
    {
        if (!Directory.Exists(baseDirectory)) throw new DirectoryNotFoundException($"Base DBC directory not found: {baseDirectory}");
        if (!Directory.Exists(overrideDirectory)) throw new DirectoryNotFoundException($"Override DBC directory not found: {overrideDirectory}");
        var baseFiles = Directory.EnumerateFiles(baseDirectory, "*.dbc", SearchOption.TopDirectoryOnly).ToDictionary(path => Path.GetFileName(path)!, StringComparer.OrdinalIgnoreCase);
        var overrideFiles = Directory.EnumerateFiles(overrideDirectory, "*.dbc", SearchOption.TopDirectoryOnly).ToDictionary(path => Path.GetFileName(path)!, StringComparer.OrdinalIgnoreCase);
        var names = baseFiles.Keys.Concat(overrideFiles.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        var result = new List<DbcLayerEntry>();
        foreach (var name in names)
        {
            baseFiles.TryGetValue(name, out var basePath); overrideFiles.TryGetValue(name, out var overridePath);
            var status = basePath is null ? DbcLayerStatus.OverrideOnly : overridePath is null ? DbcLayerStatus.BaseOnly : FilesEqual(basePath, overridePath) ? DbcLayerStatus.Identical : DbcLayerStatus.Overridden;
            var effective = overridePath ?? basePath!;
            result.Add(new(name, basePath, overridePath, effective, status, new FileInfo(effective).Length));
        }
        return result;
    }

    public static DbcRowComparison CompareFiles(string basePath, string overridePath, IReadOnlyList<DbcColumn> columns)
    {
        var baseFile = WdbcFile.Load(basePath); var overrideFile = WdbcFile.Load(overridePath);
        if (baseFile.FieldCount != overrideFile.FieldCount || baseFile.RecordSize != overrideFile.RecordSize)
            throw new InvalidDataException("The base and override DBC layouts differ.");
        if (columns.Count != baseFile.FieldCount) throw new InvalidDataException("The selected schema does not match this DBC layout.");
        var idColumn = columns.FirstOrDefault(column => column.IsIndex) ?? columns[0];
        var baseRows = Enumerable.Range(0, baseFile.RowCount).ToDictionary(row => baseFile.GetRaw(row, idColumn));
        var overrideRows = Enumerable.Range(0, overrideFile.RowCount).ToDictionary(row => overrideFile.GetRaw(row, idColumn));
        var added = overrideRows.Keys.Except(baseRows.Keys).Count();
        var removed = baseRows.Keys.Except(overrideRows.Keys).Count();
        var modifiedRows = 0; long modifiedCells = 0;
        foreach (var id in baseRows.Keys.Intersect(overrideRows.Keys))
        {
            var changed = 0;
            foreach (var column in columns)
                if (baseFile.GetRaw(baseRows[id], column) != overrideFile.GetRaw(overrideRows[id], column)) changed++;
            if (changed > 0) { modifiedRows++; modifiedCells += changed; }
        }
        return new(added, removed, modifiedRows, modifiedCells);
    }

    private static bool FilesEqual(string left, string right)
    {
        if (new FileInfo(left).Length != new FileInfo(right).Length) return false;
        using var leftStream = File.OpenRead(left); using var rightStream = File.OpenRead(right);
        return SHA256.HashData(leftStream).SequenceEqual(SHA256.HashData(rightStream));
    }
}
