using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WoWCrucible.Core;

public sealed record ExtractedArchiveStack(string Name, string RootPath);

public enum ExtractedArchiveOverlapKind
{
    StructuredTableReview,
    Unique,
    WithinStackExact,
    WithinStackConflict,
    CrossStackExact,
    CrossStackConflict
}

public sealed record ExtractedArchiveOverlapProgress(long ScannedFiles, long HashedFiles, long ReusedHashes, string CurrentPath);

public sealed record ExtractedArchiveOverlapSummary(
    string IndexPath,
    DateTimeOffset UpdatedUtc,
    int Stacks,
    long Archives,
    long Files,
    long IgnoredFiles,
    long HashedFiles,
    long ReusedHashes,
    long LogicalPaths,
    long StructuredTables,
    long UniquePaths,
    long WithinStackExact,
    long WithinStackConflicts,
    long CrossStackExact,
    long CrossStackConflicts,
    double DurationMilliseconds);

public sealed record ExtractedArchiveSupplier(
    string Stack,
    string Archive,
    string PhysicalPath,
    long Length,
    string? Sha256);

public sealed record ExtractedArchiveOverlap(
    string LogicalPath,
    ExtractedArchiveOverlapKind Kind,
    int StackCount,
    int SupplierCount,
    int DistinctContentCount,
    IReadOnlyList<ExtractedArchiveSupplier> Suppliers);

/// <summary>
/// Indexes loose trees produced by extracting every MPQ into its own archive-named
/// folder. Archive ownership is retained, while content hashes are computed only for
/// logical paths supplied more than once.
/// </summary>
public sealed class ExtractedArchiveOverlapIndexService
{
    private const int FormatVersion = 1;

    private sealed record ExistingFile(long Length, long WriteTicks, string? Sha256);
    private sealed record PendingHash(long Id, string PhysicalPath, long Length, long WriteTicks, string? Sha256);

    public ExtractedArchiveOverlapSummary Build(
        string indexPath,
        IEnumerable<ExtractedArchiveStack> stackDefinitions,
        IEnumerable<string>? exclusions = null,
        IProgress<ExtractedArchiveOverlapProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        indexPath = Path.GetFullPath(indexPath);
        var stacks = stackDefinitions.Select(stack => stack with
        {
            Name = Required(stack.Name, "stack name"),
            RootPath = RequireDirectory(stack.RootPath, "extracted stack root")
        }).ToArray();
        if (stacks.Length == 0) throw new ArgumentException("At least one extracted stack is required.", nameof(stackDefinitions));
        if (stacks.GroupBy(stack => stack.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidDataException("Each extracted stack name must be unique.");
        if (stacks.GroupBy(stack => stack.RootPath, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidDataException("Each extracted stack root must be unique.");
        for (var left = 0; left < stacks.Length; left++)
        for (var right = left + 1; right < stacks.Length; right++)
            if (IsInside(stacks[left].RootPath, stacks[right].RootPath) || IsInside(stacks[right].RootPath, stacks[left].RootPath))
                throw new InvalidDataException("Extracted stack roots must not contain one another.");

        var indexDirectory = Path.GetDirectoryName(indexPath)!;
        Directory.CreateDirectory(indexDirectory);
        foreach (var stack in stacks)
            if (IsInside(stack.RootPath, indexPath))
                throw new InvalidOperationException("The extracted-overlap index must live outside every indexed stack root.");

        var excluded = (exclusions ?? [])
            .Select(PatchInputMapper.NormalizeArchivePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        using var connection = new SqliteConnection($"Data Source={indexPath};Mode=ReadWriteCreate;Cache=Shared;Pooling=False");
        connection.Open();
        Initialize(connection);
        SetComplete(connection, false);

        var existing = ReadExisting(connection);
        var generation = DateTimeOffset.UtcNow.UtcTicks;
        long scanned = 0;
        long ignored = 0;

        using (var transaction = connection.BeginTransaction())
        using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO extracted_files(stack_name,archive_path,physical_path,logical_path,length,write_ticks,sha256,extension,seen_generation)
                VALUES($stack,$archive,$physical,$logical,$length,$ticks,$sha,$extension,$generation)
                ON CONFLICT(stack_name,archive_path,physical_path) DO UPDATE SET
                  logical_path=excluded.logical_path,length=excluded.length,write_ticks=excluded.write_ticks,
                  sha256=excluded.sha256,extension=excluded.extension,seen_generation=excluded.seen_generation
                """;
            foreach (var name in new[] { "$stack", "$archive", "$physical", "$logical", "$length", "$ticks", "$sha", "$extension", "$generation" })
                upsert.Parameters.Add(new(name, null));

            foreach (var stack in stacks)
            foreach (var physicalPath in Directory.EnumerateFiles(stack.RootPath, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanned++;
                if (!TryResolveArchiveFile(stack.RootPath, physicalPath, out var archivePath, out var archiveRoot))
                {
                    ignored++;
                    continue;
                }

                var logicalPath = NormalizeExtractedClientPath(archiveRoot, physicalPath);
                if (logicalPath is null || excluded.Any(pattern => MpqPathFilter.Matches(logicalPath, pattern)))
                {
                    ignored++;
                    continue;
                }

                var info = new FileInfo(physicalPath);
                var fullPath = Path.GetFullPath(physicalPath);
                existing.TryGetValue(fullPath, out var old);
                var retainedHash = old is not null && old.Length == info.Length && old.WriteTicks == info.LastWriteTimeUtc.Ticks
                    ? old.Sha256
                    : null;
                var values = new object?[]
                {
                    stack.Name,
                    archivePath,
                    fullPath,
                    logicalPath,
                    info.Length,
                    info.LastWriteTimeUtc.Ticks,
                    retainedHash,
                    Path.GetExtension(logicalPath).ToLowerInvariant(),
                    generation
                };
                for (var index = 0; index < values.Length; index++)
                    upsert.Parameters[index].Value = values[index] ?? DBNull.Value;
                upsert.ExecuteNonQuery();

                if (scanned % 4096 == 0)
                    progress?.Report(new(scanned, 0, 0, physicalPath));
            }

            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM extracted_files WHERE seen_generation<>$generation";
            delete.Parameters.AddWithValue("$generation", generation);
            delete.ExecuteNonQuery();
            transaction.Commit();
        }

        long hashed = 0;
        long reused = 0;
        var pending = ReadOverlapHashCandidates(connection);
        using (var transaction = connection.BeginTransaction())
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE extracted_files SET sha256=$sha WHERE id=$id";
            update.Parameters.Add(new("$sha", null));
            update.Parameters.Add(new("$id", null));
            foreach (var file in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.Sha256 is not null)
                {
                    reused++;
                    continue;
                }

                var info = new FileInfo(file.PhysicalPath);
                if (!info.Exists || info.Length != file.Length || info.LastWriteTimeUtc.Ticks != file.WriteTicks)
                    throw new InvalidDataException($"Indexed source changed while overlap hashes were being computed: {file.PhysicalPath}");
                var sha = Hash(file.PhysicalPath, cancellationToken);
                update.Parameters[0].Value = sha;
                update.Parameters[1].Value = file.Id;
                update.ExecuteNonQuery();
                hashed++;
                if ((hashed + reused) % 1024 == 0)
                    progress?.Report(new(scanned, hashed, reused, file.PhysicalPath));
            }
            transaction.Commit();
        }

        RebuildOverlaps(connection, cancellationToken);
        WriteMetadata(connection, stacks, excluded, generation);
        SetComplete(connection, true);
        progress?.Report(new(scanned, hashed, reused, "Complete"));
        return ReadSummary(connection, indexPath, stacks.Length, ignored, hashed, reused, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    public IReadOnlyList<ExtractedArchiveOverlap> Query(
        string indexPath,
        string? search = null,
        ExtractedArchiveOverlapKind? kind = null,
        int limit = 1000)
    {
        indexPath = Path.GetFullPath(indexPath);
        if (!File.Exists(indexPath)) throw new FileNotFoundException("Extracted-overlap index not found.", indexPath);
        if (limit is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(limit));

        using var connection = new SqliteConnection($"Data Source={indexPath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        EnsureComplete(connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT logical_path,kind,stack_count,supplier_count,distinct_content_count
            FROM extracted_overlaps
            WHERE ($search='' OR logical_path LIKE '%'||$search||'%') AND ($kind='' OR kind=$kind)
            ORDER BY logical_path COLLATE NOCASE LIMIT $limit
            """;
        command.Parameters.AddWithValue("$search", search?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$kind", kind?.ToString() ?? string.Empty);
        command.Parameters.AddWithValue("$limit", limit);
        var rows = new List<(string LogicalPath, ExtractedArchiveOverlapKind Kind, int StackCount, int SupplierCount, int DistinctContentCount)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                rows.Add((reader.GetString(0), Enum.Parse<ExtractedArchiveOverlapKind>(reader.GetString(1)), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4)));

        return rows.Select(row => new ExtractedArchiveOverlap(
            row.LogicalPath,
            row.Kind,
            row.StackCount,
            row.SupplierCount,
            row.DistinctContentCount,
            ReadSuppliers(connection, row.LogicalPath))).ToArray();
    }

    private static void RebuildOverlaps(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM extracted_overlaps";
            clear.ExecuteNonQuery();
        }

        var groups = new List<(string LogicalPath, string Extension, int StackCount, int SupplierCount, int DistinctContentCount)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT logical_path,MIN(extension),COUNT(DISTINCT stack_name),COUNT(*),
                       COUNT(DISTINCT COALESCE(sha256,'UNHASHED:'||physical_path))
                FROM extracted_files
                GROUP BY logical_path
                ORDER BY logical_path COLLATE NOCASE
                """;
            using var reader = read.ExecuteReader();
            while (reader.Read())
                groups.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4)));
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO extracted_overlaps(logical_path,kind,stack_count,supplier_count,distinct_content_count)
            VALUES($logical,$kind,$stacks,$suppliers,$contents)
            """;
        foreach (var name in new[] { "$logical", "$kind", "$stacks", "$suppliers", "$contents" })
            insert.Parameters.Add(new(name, null));
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var kind = Classify(group.Extension, group.StackCount, group.SupplierCount, group.DistinctContentCount);
            var values = new object[] { group.LogicalPath, kind.ToString(), group.StackCount, group.SupplierCount, group.DistinctContentCount };
            for (var index = 0; index < values.Length; index++) insert.Parameters[index].Value = values[index];
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static ExtractedArchiveOverlapKind Classify(string extension, int stackCount, int supplierCount, int distinctContentCount)
    {
        if (extension is ".dbc" or ".db2") return ExtractedArchiveOverlapKind.StructuredTableReview;
        if (supplierCount == 1) return ExtractedArchiveOverlapKind.Unique;
        if (stackCount > 1)
            return distinctContentCount == 1 ? ExtractedArchiveOverlapKind.CrossStackExact : ExtractedArchiveOverlapKind.CrossStackConflict;
        return distinctContentCount == 1 ? ExtractedArchiveOverlapKind.WithinStackExact : ExtractedArchiveOverlapKind.WithinStackConflict;
    }

    private static IReadOnlyList<ExtractedArchiveSupplier> ReadSuppliers(SqliteConnection connection, string logicalPath)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT stack_name,archive_path,physical_path,length,sha256
            FROM extracted_files WHERE logical_path=$logical
            ORDER BY stack_name COLLATE NOCASE,archive_path COLLATE NOCASE,physical_path COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("$logical", logicalPath);
        var values = new List<ExtractedArchiveSupplier>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            values.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        return values;
    }

    private static IReadOnlyList<PendingHash> ReadOverlapHashCandidates(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,physical_path,length,write_ticks,sha256
            FROM extracted_files
            WHERE logical_path IN (SELECT logical_path FROM extracted_files GROUP BY logical_path HAVING COUNT(*)>1)
            ORDER BY logical_path COLLATE NOCASE,physical_path COLLATE NOCASE
            """;
        var values = new List<PendingHash>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            values.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        return values;
    }

    private static Dictionary<string, ExistingFile> ReadExisting(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT physical_path,length,write_ticks,sha256 FROM extracted_files";
        var values = new Dictionary<string, ExistingFile>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            values[reader.GetString(0)] = new(reader.GetInt64(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetString(3));
        return values;
    }

    private static ExtractedArchiveOverlapSummary ReadSummary(
        SqliteConnection connection,
        string indexPath,
        int stacks,
        long ignored,
        long hashed,
        long reused,
        double milliseconds)
    {
        long Scalar(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (long)(command.ExecuteScalar() ?? 0L);
        }

        return new(
            indexPath,
            DateTimeOffset.UtcNow,
            stacks,
            Scalar("SELECT COUNT(DISTINCT stack_name||char(31)||archive_path) FROM extracted_files"),
            Scalar("SELECT COUNT(*) FROM extracted_files"),
            ignored,
            hashed,
            reused,
            Scalar("SELECT COUNT(*) FROM extracted_overlaps"),
            Scalar($"SELECT COUNT(*) FROM extracted_overlaps WHERE kind='{ExtractedArchiveOverlapKind.StructuredTableReview}'"),
            Scalar($"SELECT COUNT(*) FROM extracted_overlaps WHERE kind='{ExtractedArchiveOverlapKind.Unique}'"),
            Scalar($"SELECT COUNT(*) FROM extracted_overlaps WHERE kind='{ExtractedArchiveOverlapKind.WithinStackExact}'"),
            Scalar($"SELECT COUNT(*) FROM extracted_overlaps WHERE kind='{ExtractedArchiveOverlapKind.WithinStackConflict}'"),
            Scalar($"SELECT COUNT(*) FROM extracted_overlaps WHERE kind='{ExtractedArchiveOverlapKind.CrossStackExact}'"),
            Scalar($"SELECT COUNT(*) FROM extracted_overlaps WHERE kind='{ExtractedArchiveOverlapKind.CrossStackConflict}'"),
            milliseconds);
    }

    private static void Initialize(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS extracted_overlap_metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS extracted_files(
              id INTEGER PRIMARY KEY,
              stack_name TEXT NOT NULL COLLATE NOCASE,
              archive_path TEXT NOT NULL COLLATE NOCASE,
              physical_path TEXT NOT NULL COLLATE NOCASE,
              logical_path TEXT NOT NULL COLLATE NOCASE,
              length INTEGER NOT NULL,
              write_ticks INTEGER NOT NULL,
              sha256 TEXT NULL,
              extension TEXT NOT NULL,
              seen_generation INTEGER NOT NULL,
              UNIQUE(stack_name,archive_path,physical_path));
            CREATE INDEX IF NOT EXISTS ix_extracted_files_logical ON extracted_files(logical_path);
            CREATE TABLE IF NOT EXISTS extracted_overlaps(
              logical_path TEXT PRIMARY KEY COLLATE NOCASE,
              kind TEXT NOT NULL,
              stack_count INTEGER NOT NULL,
              supplier_count INTEGER NOT NULL,
              distinct_content_count INTEGER NOT NULL);
            """;
        command.ExecuteNonQuery();
        using var version = connection.CreateCommand();
        version.CommandText = "SELECT value FROM extracted_overlap_metadata WHERE key='format_version'";
        var current = version.ExecuteScalar() as string;
        if (current is not null && current != FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new InvalidDataException($"Unsupported extracted-overlap index format {current}.");
    }

    private static void WriteMetadata(
        SqliteConnection connection,
        IReadOnlyList<ExtractedArchiveStack> stacks,
        IReadOnlyList<string> exclusions,
        long generation)
    {
        var values = new Dictionary<string, string>
        {
            ["format_version"] = FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["generation"] = generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["stacks"] = JsonSerializer.Serialize(stacks),
            ["exclusions"] = JsonSerializer.Serialize(exclusions)
        };
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO extracted_overlap_metadata(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        command.Parameters.Add(new("$key", null));
        command.Parameters.Add(new("$value", null));
        foreach (var pair in values)
        {
            command.Parameters[0].Value = pair.Key;
            command.Parameters[1].Value = pair.Value;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void SetComplete(SqliteConnection connection, bool complete)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO extracted_overlap_metadata(key,value) VALUES('complete',$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        command.Parameters.AddWithValue("$value", complete ? "1" : "0");
        command.ExecuteNonQuery();
    }

    private static void EnsureComplete(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM extracted_overlap_metadata WHERE key='complete'";
        if (!string.Equals(command.ExecuteScalar() as string, "1", StringComparison.Ordinal))
            throw new InvalidDataException("The extracted-overlap index is incomplete. Resume or rebuild it before querying.");
    }

    private static bool TryResolveArchiveFile(string stackRoot, string physicalPath, out string archivePath, out string archiveRoot)
    {
        var relative = Path.GetRelativePath(stackRoot, physicalPath);
        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var archiveIndex = Array.FindIndex(parts, part => part.EndsWith(".mpq", StringComparison.OrdinalIgnoreCase) || part.EndsWith(".mpq.disabled", StringComparison.OrdinalIgnoreCase));
        if (archiveIndex < 0 || archiveIndex == parts.Length - 1)
        {
            archivePath = string.Empty;
            archiveRoot = string.Empty;
            return false;
        }

        archivePath = PatchInputMapper.NormalizeArchivePath(Path.Combine(parts[..(archiveIndex + 1)]));
        archiveRoot = Path.Combine(stackRoot, Path.Combine(parts[..(archiveIndex + 1)]));
        return true;
    }

    private static string? NormalizeExtractedClientPath(string archiveRoot, string physicalPath)
    {
        var logicalPath = LooseLayerStackIndexService.NormalizeClientPath(archiveRoot, physicalPath);
        if (logicalPath is not null) return logicalPath;

        var relative = Path.GetRelativePath(archiveRoot, physicalPath);
        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 ||
            !parts[0].Equals("Custom", StringComparison.OrdinalIgnoreCase) &&
            !parts[0].Equals("Test", StringComparison.OrdinalIgnoreCase)) return null;
        return PatchInputMapper.NormalizeArchivePath(relative);
    }

    private static string Hash(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"A {name} is required.") : value.Trim();

    private static string RequireDirectory(string path, string name)
    {
        path = Path.GetFullPath(path);
        return Directory.Exists(path) ? path : throw new DirectoryNotFoundException($"The {name} does not exist: {path}");
    }

    private static bool IsInside(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
