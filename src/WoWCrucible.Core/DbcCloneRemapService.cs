using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public sealed record DbcCloneRemapEntry(uint SourceId, uint TargetId, bool ReusesExisting = false);
public sealed record DbcCloneRemapManifest(int FormatVersion, string TableName, string KeyColumn, string BaseSha256, string SourceSha256, IReadOnlyList<DbcCloneRemapEntry> Entries);

public static class DbcCloneRemapService
{
    public static DbcCloneRemapManifest Load(string path)
    {
        var manifest = JsonSerializer.Deserialize<DbcCloneRemapManifest>(File.ReadAllText(path)) ?? throw new InvalidDataException("The clone/remap manifest is empty.");
        if (manifest.FormatVersion != 1) throw new InvalidDataException($"Unsupported clone/remap manifest version: {manifest.FormatVersion}");
        return manifest;
    }

    public static DbcCloneRemapManifest CreateManifest(string basePath, string sourcePath, IReadOnlyList<DbcColumn> columns, DbcRecordKeyStrategy keyStrategy, IEnumerable<uint> sourceIds, uint? startId = null)
    {
        if (keyStrategy.Kind != DbcRecordKeyKind.PhysicalColumn)
            throw new InvalidOperationException("Clone/remap currently requires a physical record ID. Virtual-row tables cannot safely be renumbered.");
        var baseFile = WdbcFile.Load(basePath); var sourceFile = WdbcFile.Load(sourcePath); Validate(baseFile, sourceFile, columns);
        var baseRows = DbcRecordIdentity.IndexRows(baseFile, columns, keyStrategy); var sourceRows = DbcRecordIdentity.IndexRows(sourceFile, columns, keyStrategy);
        var ids = sourceIds.Distinct().Order().ToArray();
        if (ids.Length == 0) throw new InvalidOperationException("Select at least one source record to clone.");
        var missing = ids.Where(id => !sourceRows.ContainsKey(id)).Cast<uint?>().FirstOrDefault();
        if (missing is { } missingId) throw new InvalidDataException($"Source table is missing ID {missingId}.");
        var keyColumn = DbcRecordIdentity.PhysicalColumn(columns, keyStrategy)!;
        var next = startId ?? checked(Math.Max(baseRows.Keys.DefaultIfEmpty().Max(), sourceRows.Keys.DefaultIfEmpty().Max()) + 1);
        var entries = new List<DbcCloneRemapEntry>(ids.Length); var occupied = baseRows.Keys.ToHashSet();
        var baseByContent = baseRows.GroupBy(pair => SemanticHash(baseFile, pair.Value, columns, keyColumn))
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var selectedByContent = new Dictionary<string, List<(int SourceRow, uint TargetId)>>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var sourceRow = sourceRows[id]; var contentHash = SemanticHash(sourceFile, sourceRow, columns, keyColumn);
            if (baseRows.TryGetValue(id, out var sameIdRow) && RowsEqualIgnoringKey(baseFile, sameIdRow, sourceFile, sourceRow, columns, keyColumn))
                continue;
            KeyValuePair<uint, int>? equivalentBase = null;
            foreach (var candidate in baseByContent.GetValueOrDefault(contentHash) ?? [])
                if (RowsEqualIgnoringKey(baseFile, candidate.Value, sourceFile, sourceRow, columns, keyColumn)) { equivalentBase = candidate; break; }
            if (equivalentBase is { } existing)
            {
                entries.Add(new(id, existing.Key, true));
                continue;
            }
            (int SourceRow, uint TargetId)? equivalentSelected = null;
            foreach (var candidate in selectedByContent.GetValueOrDefault(contentHash) ?? [])
                if (RowsEqualIgnoringKey(sourceFile, candidate.SourceRow, sourceFile, sourceRow, columns, keyColumn)) { equivalentSelected = candidate; break; }
            if (equivalentSelected is { } prior)
            {
                entries.Add(new(id, prior.TargetId, true));
                continue;
            }
            uint target;
            if (startId is null && !occupied.Contains(id)) target = id;
            else
            {
                while (occupied.Contains(next)) next = checked(next + 1);
                target = next; next = checked(next + 1);
            }
            entries.Add(new(id, target)); occupied.Add(target);
            if (!selectedByContent.TryGetValue(contentHash, out var selected)) selectedByContent[contentHash] = selected = [];
            selected.Add((sourceRow, target));
        }
        return new(1, Path.GetFileNameWithoutExtension(basePath), keyStrategy.DisplayName(columns), Hash(basePath), Hash(sourcePath), entries);
    }

    public static void Save(string path, DbcCloneRemapManifest manifest)
    {
        path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, path, true);
    }

    public static void Apply(string basePath, string sourcePath, string outputPath, IReadOnlyList<DbcColumn> columns, DbcRecordKeyStrategy keyStrategy, DbcCloneRemapManifest manifest)
    {
        if (manifest.FormatVersion != 1) throw new InvalidDataException($"Unsupported clone/remap manifest version: {manifest.FormatVersion}");
        var tableName = Path.GetFileNameWithoutExtension(basePath);
        if (!tableName.Equals(manifest.TableName, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Manifest targets {manifest.TableName}, not {tableName}.");
        if (!Hash(basePath).Equals(manifest.BaseSha256, StringComparison.OrdinalIgnoreCase) || !Hash(sourcePath).Equals(manifest.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Base or source DBC changed after this clone/remap plan was created. Recreate the plan before applying it.");
        var baseFile = WdbcFile.Load(basePath); var sourceFile = WdbcFile.Load(sourcePath); Validate(baseFile, sourceFile, columns);
        var keyColumn = DbcRecordIdentity.PhysicalColumn(columns, keyStrategy) ?? throw new InvalidOperationException("Clone/remap requires a physical ID column.");
        if (!keyColumn.Name.Equals(manifest.KeyColumn, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Manifest key is {manifest.KeyColumn}; schema key is {keyColumn.Name}.");
        var baseRows = DbcRecordIdentity.IndexRows(baseFile, columns, keyStrategy); var sourceRows = DbcRecordIdentity.IndexRows(sourceFile, columns, keyStrategy);
        foreach (var entry in manifest.Entries)
        {
            if (!sourceRows.TryGetValue(entry.SourceId, out var sourceRow)) throw new InvalidDataException($"Source is missing ID {entry.SourceId}.");
            if (entry.ReusesExisting)
            {
                if (!baseRows.TryGetValue(entry.TargetId, out var targetRow)) throw new InvalidDataException($"Reuse target ID {entry.TargetId} does not exist before source ID {entry.SourceId} is applied.");
                if (!RowsEqualIgnoringKey(baseFile, targetRow, sourceFile, sourceRow, columns, keyColumn)) throw new InvalidDataException($"Reuse target ID {entry.TargetId} is not semantically equivalent to source ID {entry.SourceId}.");
                continue;
            }
            if (baseRows.ContainsKey(entry.TargetId)) throw new InvalidDataException($"Target ID {entry.TargetId} already exists in the base.");
            var destinationRow = baseFile.AddBlankRow();
            foreach (var column in columns)
            {
                if (column.Type == DbcValueType.StringOffset) baseFile.SetDisplayValue(destinationRow, column, sourceFile.GetString(sourceFile.GetRaw(sourceRow, column)));
                else baseFile.SetRaw(destinationRow, column, sourceFile.GetRaw(sourceRow, column));
            }
            baseFile.SetRaw(destinationRow, keyColumn, entry.TargetId); baseRows[entry.TargetId] = destinationRow;
        }
        baseFile.Save(outputPath);
    }

    public static IReadOnlyList<uint> FindReferencedIds(string parentSourcePath, IReadOnlyList<DbcColumn> parentColumns, DbcRecordKeyStrategy parentKeyStrategy, IEnumerable<uint> parentIds, string foreignColumnName)
    {
        var parent = WdbcFile.Load(parentSourcePath); var rows = DbcRecordIdentity.IndexRows(parent, parentColumns, parentKeyStrategy);
        var foreign = parentColumns.FirstOrDefault(column => column.Name.Equals(foreignColumnName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"{Path.GetFileNameWithoutExtension(parentSourcePath)} has no named column '{foreignColumnName}'.");
        return parentIds.Distinct().Select(id => rows.TryGetValue(id, out var row) ? parent.GetRaw(row, foreign) : throw new InvalidDataException($"Parent source is missing ID {id}."))
            .Where(id => id != 0).Distinct().Order().ToArray();
    }

    public static int ApplyReferenceMap(string inputPath, string outputPath, IReadOnlyList<DbcColumn> columns, DbcRecordKeyStrategy keyStrategy, IEnumerable<uint> recordIds, string foreignColumnName, DbcCloneRemapManifest referencedManifest)
    {
        var file = WdbcFile.Load(inputPath); var rows = DbcRecordIdentity.IndexRows(file, columns, keyStrategy);
        var foreign = columns.FirstOrDefault(column => column.Name.Equals(foreignColumnName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"{Path.GetFileNameWithoutExtension(inputPath)} has no named column '{foreignColumnName}'.");
        var mapping = referencedManifest.Entries.ToDictionary(entry => entry.SourceId, entry => entry.TargetId); var changed = 0;
        foreach (var id in recordIds.Distinct())
        {
            if (!rows.TryGetValue(id, out var row)) throw new InvalidDataException($"Remap target is missing record ID {id}.");
            if (!mapping.TryGetValue(file.GetRaw(row, foreign), out var replacement)) continue;
            file.SetRaw(row, foreign, replacement); changed++;
        }
        file.Save(outputPath); return changed;
    }

    private static void Validate(WdbcFile baseFile, WdbcFile sourceFile, IReadOnlyList<DbcColumn> columns)
    {
        if (baseFile.FieldCount != sourceFile.FieldCount || baseFile.RecordSize != sourceFile.RecordSize) throw new InvalidDataException("The base and source DBC layouts differ.");
        if (columns.Count != baseFile.FieldCount) throw new InvalidDataException("The named schema does not match this DBC layout.");
    }
    private static bool RowsEqualIgnoringKey(WdbcFile left, int leftRow, WdbcFile right, int rightRow, IReadOnlyList<DbcColumn> columns, DbcColumn keyColumn)
    {
        foreach (var column in columns.Where(column => column.Index != keyColumn.Index))
        {
            if (column.Type == DbcValueType.StringOffset)
            {
                if (!left.GetString(left.GetRaw(leftRow, column)).Equals(right.GetString(right.GetRaw(rightRow, column)), StringComparison.Ordinal)) return false;
            }
            else if (left.GetRaw(leftRow, column) != right.GetRaw(rightRow, column)) return false;
        }
        return true;
    }
    private static string SemanticHash(WdbcFile file, int row, IReadOnlyList<DbcColumn> columns, DbcColumn keyColumn)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var column in columns.Where(column => column.Index != keyColumn.Index))
        {
            var value = column.Type == DbcValueType.StringOffset ? file.GetString(file.GetRaw(row, column)) : file.GetRaw(row, column).ToString("X8");
            hash.AppendData(Encoding.UTF8.GetBytes(value)); hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
