using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace WoWCrucible.Core;

public sealed record LooseLayerDefinition(string Stack, int Order, string Name, string RootPath);
public enum LooseLayerComparisonKind
{
    StructuredTableReview,
    AbsentFromStack,
    ExactEffective,
    ExactEffectiveWithAlternateVersions,
    DifferentEffective,
    DifferentEffectiveWithExactAlternate
}
public sealed record LooseLayerIndexProgress(long ScannedFiles, long HashedFiles, long ReusedHashes, string CurrentPath);
public sealed record LooseLayerStackSummary(string IndexPath, string SourceRoot, DateTimeOffset UpdatedUtc,
    long SourceFiles, long LayerFiles, long HashedFiles, long ReusedHashes, long ExcludedFiles,
    int Stacks, long Comparisons, long StructuredTables, long ExactEffective, long DifferentEffective, long Absent,
    double DurationMilliseconds);
public sealed record LooseLayerSupplier(string Stack, int Order, string Layer, string PhysicalPath, long Length, string Sha256, bool Effective);
public sealed record LooseLayerComparison(string LogicalPath, string SourcePath, long SourceLength, string SourceSha256,
    string Stack, LooseLayerComparisonKind Kind, string FamilyKey, IReadOnlyList<LooseLayerSupplier> Suppliers);

/// <summary>
/// Persistent comparison of one loose client tree against explicitly ordered named
/// layer stacks. Identity is the normalized client path, never a global filename.
/// The SQLite index reuses hashes for unchanged physical files and is the checkpoint.
/// </summary>
public sealed class LooseLayerStackIndexService
{
    private const int FormatVersion = 1;
    private static readonly HashSet<string> ClientRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "Character", "Creature", "Item", "Interface", "World", "Textures", "XTextures", "Tileset",
        "Sound", "Spell", "Spells", "Shaders", "_Shaders", "Interiors", "Dungeons", "Environments",
        "Buildings", "Particles", "Cameras", "Fonts", "DBFilesClient"
    };

    private sealed record IndexedFile(string Role, string Stack, int LayerOrder, string LayerName, string PhysicalPath,
        string LogicalPath, long Length, long WriteTicks, string Sha256, string Extension, string FamilyKey);

    public LooseLayerStackSummary Build(string indexPath, string sourceRoot, IEnumerable<LooseLayerDefinition> layerDefinitions,
        IEnumerable<string>? exclusions = null, IProgress<LooseLayerIndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        indexPath = Path.GetFullPath(indexPath); sourceRoot = RequireDirectory(sourceRoot, "source root");
        var layers = layerDefinitions.Select(layer => layer with
        {
            Stack = Required(layer.Stack, "stack"), Name = Required(layer.Name, "layer name"), RootPath = RequireDirectory(layer.RootPath, "layer root")
        }).OrderBy(layer => layer.Stack, StringComparer.OrdinalIgnoreCase).ThenBy(layer => layer.Order).ToArray();
        if (layers.Length == 0) throw new ArgumentException("At least one named layer is required.", nameof(layerDefinitions));
        if (layers.GroupBy(layer => $"{layer.Stack}\u001f{layer.Order}", StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidDataException("Each stack must use every layer order exactly once.");
        var indexDirectory = Path.GetDirectoryName(indexPath)!; Directory.CreateDirectory(indexDirectory);
        foreach (var root in new[] { sourceRoot }.Concat(layers.Select(layer => layer.RootPath)))
            if (IsInside(root, indexPath)) throw new InvalidOperationException("The layer-stack index must live outside every indexed source/layer tree.");
        var excluded = (exclusions ?? []).Select(PatchInputMapper.NormalizeArchivePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        using var connection = new SqliteConnection($"Data Source={indexPath};Mode=ReadWriteCreate;Cache=Shared;Pooling=False"); connection.Open();
        Initialize(connection);
        var existing = ReadExisting(connection);
        var generation = DateTimeOffset.UtcNow.UtcTicks; long scanned = 0, hashed = 0, reused = 0, excludedFiles = 0;
        using var transaction = connection.BeginTransaction();
        using var upsert = connection.CreateCommand(); upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO files(role,stack_name,layer_order,layer_name,physical_path,logical_path,length,write_ticks,sha256,extension,family_key,seen_generation)
            VALUES($role,$stack,$order,$layer,$physical,$logical,$length,$ticks,$sha,$extension,$family,$generation)
            ON CONFLICT(role,stack_name,layer_order,layer_name,physical_path) DO UPDATE SET
              logical_path=excluded.logical_path,length=excluded.length,write_ticks=excluded.write_ticks,sha256=excluded.sha256,
              extension=excluded.extension,family_key=excluded.family_key,seen_generation=excluded.seen_generation
            """;
        foreach (var name in new[] { "$role", "$stack", "$order", "$layer", "$physical", "$logical", "$length", "$ticks", "$sha", "$extension", "$family", "$generation" }) upsert.Parameters.Add(new(name, null));

        void Scan(string role, string stack, int order, string name, string root)
        {
            foreach (var physical in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested(); scanned++;
                var logical = NormalizeClientPath(root, physical);
                if (logical is null || excluded.Any(pattern => MpqPathFilter.Matches(logical, pattern))) { excludedFiles++; continue; }
                var info = new FileInfo(physical); string sha;
                if (existing.TryGetValue(physical, out var old) && old.Length == info.Length && old.WriteTicks == info.LastWriteTimeUtc.Ticks) { sha = old.Sha256; reused++; }
                else { sha = Hash(physical, cancellationToken); hashed++; }
                var extension = Path.GetExtension(logical).ToLowerInvariant(); var family = Family(logical, extension);
                var values = new object?[] { role, stack, order, name, Path.GetFullPath(physical), logical, info.Length, info.LastWriteTimeUtc.Ticks, sha, extension, family, generation };
                for (var index = 0; index < values.Length; index++) upsert.Parameters[index].Value = values[index] ?? DBNull.Value;
                upsert.ExecuteNonQuery();
                if (scanned % 512 == 0) progress?.Report(new(scanned, hashed, reused, physical));
            }
        }

        Scan("source", string.Empty, 0, "source", sourceRoot);
        foreach (var layer in layers) Scan("layer", layer.Stack, layer.Order, layer.Name, layer.RootPath);
        using (var delete = connection.CreateCommand()) { delete.Transaction = transaction; delete.CommandText = "DELETE FROM files WHERE seen_generation<>$generation"; delete.Parameters.AddWithValue("$generation", generation); delete.ExecuteNonQuery(); }
        using (var clear = connection.CreateCommand()) { clear.Transaction = transaction; clear.CommandText = "DELETE FROM comparisons"; clear.ExecuteNonQuery(); }
        RebuildComparisons(connection, transaction, layers.Select(layer => layer.Stack).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), cancellationToken);
        WriteMetadata(connection, transaction, sourceRoot, layers, excluded, generation);
        transaction.Commit(); progress?.Report(new(scanned, hashed, reused, "Complete"));
        return ReadSummary(connection, indexPath, sourceRoot, hashed, reused, excludedFiles, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    public IReadOnlyList<LooseLayerComparison> Query(string indexPath, string? search = null, LooseLayerComparisonKind? kind = null, int limit = 1000)
    {
        indexPath = Path.GetFullPath(indexPath); if (!File.Exists(indexPath)) throw new FileNotFoundException("Layer-stack index not found.", indexPath);
        if (limit is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = new SqliteConnection($"Data Source={indexPath};Mode=ReadOnly;Pooling=False"); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT logical_path,source_path,source_length,source_sha256,stack_name,kind,family_key
            FROM comparisons
            WHERE ($search='' OR logical_path LIKE '%'||$search||'%') AND ($kind='' OR kind=$kind)
            ORDER BY logical_path COLLATE NOCASE,stack_name COLLATE NOCASE LIMIT $limit
            """;
        command.Parameters.AddWithValue("$search", search?.Trim() ?? string.Empty); command.Parameters.AddWithValue("$kind", kind?.ToString() ?? string.Empty); command.Parameters.AddWithValue("$limit", limit);
        var rows = new List<(string Logical, string Source, long Length, string Sha, string Stack, LooseLayerComparisonKind Kind, string Family)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4),
                Enum.Parse<LooseLayerComparisonKind>(reader.GetString(5)), reader.GetString(6)));
        }
        return rows.Select(row => new LooseLayerComparison(row.Logical, row.Source, row.Length, row.Sha, row.Stack, row.Kind, row.Family,
            ReadSuppliers(connection, row.Logical, row.Stack))).ToArray();
    }

    private static void RebuildComparisons(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<string> stacks, CancellationToken token)
    {
        var sourceGroups = ReadFiles(connection, "source", transaction).GroupBy(file => file.LogicalPath, StringComparer.OrdinalIgnoreCase).ToArray();
        var duplicateSource = sourceGroups.FirstOrDefault(group => group.Count() > 1);
        if (duplicateSource is not null) throw new InvalidDataException($"The source tree normalizes multiple physical files to '{duplicateSource.Key}'. Narrow the declared content root or remove the ambiguous extraction wrapper; Crucible will not choose one silently.");
        var sources = sourceGroups.Select(group => group.Single()).ToArray();
        var layerFiles = ReadFiles(connection, "layer", transaction);
        var duplicateLayer = layerFiles.GroupBy(file => $"{file.Stack}\u001f{file.LayerOrder}\u001f{file.LogicalPath}", StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicateLayer is not null) throw new InvalidDataException($"One declared layer normalizes multiple physical files to '{duplicateLayer.First().LogicalPath}'. Crucible will not guess which duplicate supplies the layer.");
        var layerLookup = layerFiles.GroupBy(file => $"{file.Stack}\u001f{file.LogicalPath}", StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.OrderBy(file => file.LayerOrder).ToArray(), StringComparer.OrdinalIgnoreCase);
        using var insert = connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO comparisons(logical_path,source_path,source_length,source_sha256,stack_name,kind,family_key) VALUES($logical,$source,$length,$sha,$stack,$kind,$family)";
        foreach (var name in new[] { "$logical", "$source", "$length", "$sha", "$stack", "$kind", "$family" }) insert.Parameters.Add(new(name, null));
        foreach (var source in sources)
        foreach (var stack in stacks)
        {
            token.ThrowIfCancellationRequested(); layerLookup.TryGetValue($"{stack}\u001f{source.LogicalPath}", out var supplied); supplied ??= [];
            var kind = Classify(source, supplied);
            var values = new object[] { source.LogicalPath, source.PhysicalPath, source.Length, source.Sha256, stack, kind.ToString(), source.FamilyKey };
            for (var index = 0; index < values.Length; index++) insert.Parameters[index].Value = values[index]; insert.ExecuteNonQuery();
        }
    }

    private static LooseLayerComparisonKind Classify(IndexedFile source, IReadOnlyList<IndexedFile> supplied)
    {
        if (source.Extension is ".dbc" or ".db2") return LooseLayerComparisonKind.StructuredTableReview;
        if (supplied.Count == 0) return LooseLayerComparisonKind.AbsentFromStack;
        var winner = supplied[^1]; var exactWinner = winner.Sha256.Equals(source.Sha256, StringComparison.OrdinalIgnoreCase);
        var alternateDifferent = supplied.Any(file => !file.Sha256.Equals(winner.Sha256, StringComparison.OrdinalIgnoreCase));
        var exactAlternate = supplied.Any(file => file.Sha256.Equals(source.Sha256, StringComparison.OrdinalIgnoreCase));
        if (exactWinner) return alternateDifferent ? LooseLayerComparisonKind.ExactEffectiveWithAlternateVersions : LooseLayerComparisonKind.ExactEffective;
        return exactAlternate ? LooseLayerComparisonKind.DifferentEffectiveWithExactAlternate : LooseLayerComparisonKind.DifferentEffective;
    }

    private static IReadOnlyList<LooseLayerSupplier> ReadSuppliers(SqliteConnection connection, string logical, string stack)
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT layer_order,layer_name,physical_path,length,sha256 FROM files WHERE role='layer' AND stack_name=$stack AND logical_path=$logical ORDER BY layer_order";
        command.Parameters.AddWithValue("$stack", stack); command.Parameters.AddWithValue("$logical", logical); var values = new List<LooseLayerSupplier>(); using var reader = command.ExecuteReader();
        while (reader.Read()) values.Add(new(stack, reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4), false));
        return values.Select((value, index) => value with { Effective = index == values.Count - 1 }).ToArray();
    }

    private static IReadOnlyList<IndexedFile> ReadFiles(SqliteConnection connection, string role, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT role,stack_name,layer_order,layer_name,physical_path,logical_path,length,write_ticks,sha256,extension,family_key FROM files WHERE role=$role"; command.Parameters.AddWithValue("$role", role);
        var values = new List<IndexedFile>(); using var reader = command.ExecuteReader(); while (reader.Read()) values.Add(new(reader.GetString(0),reader.GetString(1),reader.GetInt32(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetInt64(6),reader.GetInt64(7),reader.GetString(8),reader.GetString(9),reader.GetString(10))); return values;
    }

    private static Dictionary<string, (long Length, long WriteTicks, string Sha256)> ReadExisting(SqliteConnection connection)
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT physical_path,length,write_ticks,sha256 FROM files"; var values = new Dictionary<string,(long,long,string)>(StringComparer.OrdinalIgnoreCase); using var reader = command.ExecuteReader(); while(reader.Read()) values[reader.GetString(0)] = (reader.GetInt64(1),reader.GetInt64(2),reader.GetString(3)); return values;
    }

    private static LooseLayerStackSummary ReadSummary(SqliteConnection connection, string indexPath, string sourceRoot, long hashed, long reused, long excluded, double milliseconds)
    {
        long Scalar(string sql) { using var command=connection.CreateCommand();command.CommandText=sql;return (long)(command.ExecuteScalar()??0L); }
        return new(indexPath,sourceRoot,DateTimeOffset.UtcNow,Scalar("SELECT COUNT(*) FROM files WHERE role='source'"),Scalar("SELECT COUNT(*) FROM files WHERE role='layer'"),hashed,reused,excluded,
            (int)Scalar("SELECT COUNT(DISTINCT stack_name) FROM files WHERE role='layer'"),Scalar("SELECT COUNT(*) FROM comparisons"),Scalar($"SELECT COUNT(*) FROM comparisons WHERE kind='{LooseLayerComparisonKind.StructuredTableReview}'"),
            Scalar($"SELECT COUNT(*) FROM comparisons WHERE kind IN ('{LooseLayerComparisonKind.ExactEffective}','{LooseLayerComparisonKind.ExactEffectiveWithAlternateVersions}')"),
            Scalar($"SELECT COUNT(*) FROM comparisons WHERE kind IN ('{LooseLayerComparisonKind.DifferentEffective}','{LooseLayerComparisonKind.DifferentEffectiveWithExactAlternate}')"),Scalar($"SELECT COUNT(*) FROM comparisons WHERE kind='{LooseLayerComparisonKind.AbsentFromStack}'"),milliseconds);
    }

    private static void Initialize(SqliteConnection connection)
    {
        using var command=connection.CreateCommand();command.CommandText="""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS files(role TEXT NOT NULL,stack_name TEXT NOT NULL,layer_order INTEGER NOT NULL,layer_name TEXT NOT NULL,physical_path TEXT NOT NULL COLLATE NOCASE,logical_path TEXT NOT NULL COLLATE NOCASE,length INTEGER NOT NULL,write_ticks INTEGER NOT NULL,sha256 TEXT NOT NULL,extension TEXT NOT NULL,family_key TEXT NOT NULL,seen_generation INTEGER NOT NULL,PRIMARY KEY(role,stack_name,layer_order,layer_name,physical_path));
            CREATE INDEX IF NOT EXISTS ix_files_logical ON files(stack_name,logical_path,layer_order);
            CREATE TABLE IF NOT EXISTS comparisons(logical_path TEXT NOT NULL COLLATE NOCASE,source_path TEXT NOT NULL,source_length INTEGER NOT NULL,source_sha256 TEXT NOT NULL,stack_name TEXT NOT NULL COLLATE NOCASE,kind TEXT NOT NULL,family_key TEXT NOT NULL,PRIMARY KEY(logical_path,stack_name));
            """;command.ExecuteNonQuery();
        using var version=connection.CreateCommand();version.CommandText="SELECT value FROM metadata WHERE key='format_version'";var current=version.ExecuteScalar() as string;
        if(current is not null && current!=FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)) throw new InvalidDataException($"Unsupported loose-layer index format {current}.");
    }

    private static void WriteMetadata(SqliteConnection connection, SqliteTransaction transaction, string sourceRoot, IReadOnlyList<LooseLayerDefinition> layers, IReadOnlyList<string> exclusions, long generation)
    {
        var values=new Dictionary<string,string>{{"format_version",FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)},{"source_root",sourceRoot},{"generation",generation.ToString(System.Globalization.CultureInfo.InvariantCulture)},{"layers",System.Text.Json.JsonSerializer.Serialize(layers)},{"exclusions",System.Text.Json.JsonSerializer.Serialize(exclusions)}};
        using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="INSERT INTO metadata(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";command.Parameters.Add(new("$key",null));command.Parameters.Add(new("$value",null));foreach(var pair in values){command.Parameters[0].Value=pair.Key;command.Parameters[1].Value=pair.Value;command.ExecuteNonQuery();}
    }

    public static string? NormalizeClientPath(string root, string physicalPath)
    {
        root=Path.GetFullPath(root);physicalPath=Path.GetFullPath(physicalPath);var relative=Path.GetRelativePath(root,physicalPath);if(relative==".."||relative.StartsWith(".."+Path.DirectorySeparatorChar,StringComparison.Ordinal)) throw new InvalidOperationException("File is outside the declared content root.");
        var parts=relative.Split([Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar],StringSplitOptions.RemoveEmptyEntries);var rootName=Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        if(ClientRoots.Contains(rootName)) return PatchInputMapper.NormalizeArchivePath(Path.Combine([rootName,..parts]));
        var anchor=Array.FindIndex(parts,ClientRoots.Contains);return anchor<0?null:PatchInputMapper.NormalizeArchivePath(Path.Combine(parts[anchor..]));
    }

    private static string Family(string logical, string extension)
    {
        var directory=Path.GetDirectoryName(logical)?.Replace('/','\\')??string.Empty;var stem=Path.GetFileNameWithoutExtension(logical);
        if(extension==".skin"&&stem.Length>2&&char.IsDigit(stem[^1])&&char.IsDigit(stem[^2])) stem=stem[..^2];
        else if(extension==".anim"&&System.Text.RegularExpressions.Regex.Match(stem,@"^(.*)\d{4}-\d{2}$") is { Success:true } match) stem=match.Groups[1].Value;
        else if(extension is not (".m2" or ".bone")) return logical;
        return string.IsNullOrEmpty(directory)?stem:$"{directory}\\{stem}";
    }
    private static string Hash(string path,CancellationToken token){using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,1<<20,FileOptions.SequentialScan);using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[1<<20];int read;while((read=stream.Read(buffer))>0){token.ThrowIfCancellationRequested();hash.AppendData(buffer,0,read);}return Convert.ToHexString(hash.GetHashAndReset());}
    private static string RequireDirectory(string path,string name){path=Path.GetFullPath(path);return Directory.Exists(path)?path:throw new DirectoryNotFoundException($"The {name} does not exist: {path}");}
    private static string Required(string value,string name)=>string.IsNullOrWhiteSpace(value)?throw new ArgumentException($"A {name} is required."):value.Trim();
    private static bool IsInside(string root,string path){var relative=Path.GetRelativePath(root,path);return relative=="."||relative!=".."&&!relative.StartsWith(".."+Path.DirectorySeparatorChar,StringComparison.Ordinal);}
}
