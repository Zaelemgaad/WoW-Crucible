using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace WoWCrucible.Core;

public enum TextureConsumerAssetState { Indexed, Unsupported, Invalid, Missing }

public sealed record TextureConsumerIndexProgress(long CatalogRows, int EligibleAssets, int UpdatedAssets, string CurrentPath);

public sealed record TextureConsumerIndexSummary(
    string IndexPath,
    string CatalogPath,
    DateTimeOffset GeneratedUtc,
    long CatalogRows,
    int EligibleAssets,
    int IndexedAssets,
    int UnsupportedAssets,
    int InvalidAssets,
    int MissingAssets,
    int TextureReferences,
    int CatalogIssues)
{
    public bool CoverageComplete => UnsupportedAssets == 0 && InvalidAssets == 0 && MissingAssets == 0 && CatalogIssues == 0;
}

public sealed record TextureConsumerIndexBuildResult(
    TextureConsumerIndexSummary Summary,
    int UpdatedAssets,
    int UnchangedAssets,
    int RemovedAssets,
    double DurationMilliseconds);

public sealed record TextureConsumerReference(
    string TextureClientPath,
    string ConsumerClientPath,
    string ConsumerProvenance,
    string ConsumerSourcePath,
    string ConsumerExtension,
    string ReferenceKind,
    bool SameProvenance);

public sealed record TextureConsumerQueryResult(
    string TextureClientPath,
    string? TextureProvenance,
    string? TextureSourcePath,
    IReadOnlyList<TextureConsumerReference> Consumers,
    TextureConsumerIndexSummary Summary);

/// <summary>
/// Persistent reverse lookup from exact client BLP paths to the M2/WMO/ADT/WDT
/// files which directly reference them. The processed asset catalog is the only
/// discovery input: a query never recursively scans the library.
/// </summary>
public sealed class TextureConsumerIndexService
{
    private const int FormatVersion = 2;
    private const int ParseBatchSize = 256;
    private const string CatalogFileName = "asset-catalog.csv";
    private static readonly HashSet<string> ConsumerExtensions = new(StringComparer.OrdinalIgnoreCase) { ".m2", ".wmo", ".adt", ".wdt" };
    private sealed record ExistingAsset(string ClientPath, string Provenance, long Length, long WriteTicks);
    private sealed record CatalogCandidate(long Row, string RelativePath, string SourcePath, string Extension);
    private sealed record ParsedAsset(CatalogCandidate Candidate, string ClientPath, string Provenance, long Length, long WriteTicks,
        TextureConsumerAssetState State, string? Error, IReadOnlyList<(string Path, string Kind)> References, bool Unchanged);

    public static string GetIndexPath(string libraryRoot)
        => Path.Combine(Path.GetFullPath(libraryRoot), "Cache", "texture-consumers.sqlite");

    public TextureConsumerIndexBuildResult Build(string libraryRoot,
        IProgress<TextureConsumerIndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        libraryRoot = Path.GetFullPath(libraryRoot);
        var catalogPath = Path.Combine(libraryRoot, CatalogFileName);
        if (!File.Exists(catalogPath))
            throw new FileNotFoundException("The processed asset library has no asset-catalog.csv. Rebuild the library catalog before indexing texture consumers.", catalogPath);
        _ = ClientAssetDependencyService.OpenLibraryLayout(libraryRoot);
        var indexPath = GetIndexPath(libraryRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);

        var useTemporary = !File.Exists(indexPath) || !IsCompatible(indexPath);
        var workingPath = useTemporary ? indexPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp" : indexPath;
        try
        {
            var result = BuildInto(libraryRoot, catalogPath, workingPath, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (useTemporary) File.Move(workingPath, indexPath, true);
            return result with { Summary = result.Summary with { IndexPath = indexPath } };
        }
        catch
        {
            if (useTemporary) TryDelete(workingPath);
            throw;
        }
    }

    public TextureConsumerQueryResult Query(string libraryRoot, string texturePathOrClientPath,
        CancellationToken cancellationToken = default)
    {
        libraryRoot = Path.GetFullPath(libraryRoot);
        var indexPath = GetIndexPath(libraryRoot);
        if (!File.Exists(indexPath)) throw new FileNotFoundException("No reverse texture-consumer index exists. Build it once before querying.", indexPath);
        var layout = ClientAssetDependencyService.OpenLibraryLayout(libraryRoot);
        string textureClientPath;
        string? textureProvenance = null;
        string? textureSourcePath = null;
        if (File.Exists(texturePathOrClientPath))
        {
            var location = ClientAssetDependencyService.InferLocation(layout, texturePathOrClientPath);
            textureClientPath = location.ClientPath;
            textureProvenance = location.Provenance;
            textureSourcePath = location.SourcePath;
        }
        else textureClientPath = PatchInputMapper.NormalizeArchivePath(texturePathOrClientPath);
        if (!Path.GetExtension(textureClientPath).Equals(".blp", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Texture consumer lookup requires an exact .blp client path or a processed-library BLP file.");

        using var connection = Open(indexPath, SqliteOpenMode.ReadOnly);
        RequireCompatible(connection);
        var summary = ReadSummary(connection, indexPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.texture_path, a.client_path, a.provenance, a.source_path, a.extension, r.reference_kind
            FROM texture_refs r
            JOIN assets a ON a.source_path = r.source_path
            WHERE r.texture_path = $texture COLLATE NOCASE AND a.scan_state = 'Indexed'
            ORDER BY CASE WHEN a.provenance = $provenance COLLATE NOCASE THEN 0 ELSE 1 END,
                     a.provenance COLLATE NOCASE, a.client_path COLLATE NOCASE, r.reference_kind COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("$texture", textureClientPath);
        command.Parameters.AddWithValue("$provenance", textureProvenance ?? string.Empty);
        var consumers = new List<TextureConsumerReference>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provenance = reader.GetString(2);
            consumers.Add(new(reader.GetString(0), reader.GetString(1), provenance, reader.GetString(3), reader.GetString(4), reader.GetString(5),
                textureProvenance is not null && provenance.Equals(textureProvenance, StringComparison.OrdinalIgnoreCase)));
        }
        return new(textureClientPath, textureProvenance, textureSourcePath, consumers, summary);
    }

    public TextureConsumerIndexSummary GetSummary(string libraryRoot)
    {
        var indexPath = GetIndexPath(libraryRoot);
        if (!File.Exists(indexPath)) throw new FileNotFoundException("No reverse texture-consumer index exists.", indexPath);
        using var connection = Open(indexPath, SqliteOpenMode.ReadOnly);
        RequireCompatible(connection);
        return ReadSummary(connection, indexPath);
    }

    private TextureConsumerIndexBuildResult BuildInto(string libraryRoot, string catalogPath, string indexPath,
        IProgress<TextureConsumerIndexProgress>? progress, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        progress?.Report(new(0, 0, 0, "Setup · fingerprinting asset catalog"));
        var beforeIdentity = AssetComparisonAggregateCache.CaptureIdentity(catalogPath, cancellationToken);
        progress?.Report(new(0, 0, 0, "Setup · validating content-first library layout"));
        var layout = ClientAssetDependencyService.OpenLibraryLayout(libraryRoot);
        progress?.Report(new(0, 0, 0, "Setup · opening transactional SQLite cache"));
        using var connection = Open(indexPath, SqliteOpenMode.ReadWriteCreate);
        EnsureSchema(connection);
        using var transaction = connection.BeginTransaction();
        var generation = Guid.NewGuid().ToString("N");
        Execute(connection, transaction, "DELETE FROM catalog_issues");
        var existingAssets = new Dictionary<string, ExistingAsset>(StringComparer.OrdinalIgnoreCase);
        using (var existingCommand = connection.CreateCommand())
        {
            existingCommand.Transaction = transaction; existingCommand.CommandText = "SELECT source_path,client_path,provenance,length,write_ticks FROM assets";
            using var existingReader = existingCommand.ExecuteReader();
            while (existingReader.Read()) existingAssets[existingReader.GetString(0)] = new(existingReader.GetString(1), existingReader.GetString(2), existingReader.GetInt64(3), existingReader.GetInt64(4));
        }
        progress?.Report(new(0, 0, 0, $"Setup · loaded {existingAssets.Count:N0} prior consumer identities · streaming catalog"));
        using var touch = Command(connection, transaction, "UPDATE assets SET seen_generation=$generation WHERE source_path=$source");
        var touchGeneration = touch.Parameters.Add("$generation", SqliteType.Text); var touchSource = touch.Parameters.Add("$source", SqliteType.Text);
        using var upsert = Command(connection, transaction, """
            INSERT INTO assets(source_path,client_path,provenance,extension,length,write_ticks,scan_state,error,seen_generation)
            VALUES($source,$client,$provenance,$extension,$length,$ticks,$state,$error,$generation)
            ON CONFLICT(source_path) DO UPDATE SET client_path=excluded.client_path,provenance=excluded.provenance,
              extension=excluded.extension,length=excluded.length,write_ticks=excluded.write_ticks,scan_state=excluded.scan_state,
              error=excluded.error,seen_generation=excluded.seen_generation
            """);
        var upsertParameters = new Dictionary<string, SqliteParameter>(StringComparer.Ordinal)
        {
            ["source"] = upsert.Parameters.Add("$source", SqliteType.Text), ["client"] = upsert.Parameters.Add("$client", SqliteType.Text),
            ["provenance"] = upsert.Parameters.Add("$provenance", SqliteType.Text), ["extension"] = upsert.Parameters.Add("$extension", SqliteType.Text),
            ["length"] = upsert.Parameters.Add("$length", SqliteType.Integer), ["ticks"] = upsert.Parameters.Add("$ticks", SqliteType.Integer),
            ["state"] = upsert.Parameters.Add("$state", SqliteType.Text), ["error"] = upsert.Parameters.Add("$error", SqliteType.Text),
            ["generation"] = upsert.Parameters.Add("$generation", SqliteType.Text)
        };
        using var deleteRefs = Command(connection, transaction, "DELETE FROM texture_refs WHERE source_path=$source");
        var deleteRefSource = deleteRefs.Parameters.Add("$source", SqliteType.Text);
        using var insertRef = Command(connection, transaction, "INSERT OR IGNORE INTO texture_refs(source_path,texture_path,reference_kind) VALUES($source,$texture,$kind)");
        var refSource = insertRef.Parameters.Add("$source", SqliteType.Text); var refTexture = insertRef.Parameters.Add("$texture", SqliteType.Text); var refKind = insertRef.Parameters.Add("$kind", SqliteType.Text);
        using var insertIssue = Command(connection, transaction, "INSERT OR REPLACE INTO catalog_issues(issue_key,row_number,relative_path,message) VALUES($key,$row,$path,$message)");
        var issueKey = insertIssue.Parameters.Add("$key", SqliteType.Text); var issueRow = insertIssue.Parameters.Add("$row", SqliteType.Integer); var issuePath = insertIssue.Parameters.Add("$path", SqliteType.Text); var issueMessage = insertIssue.Parameters.Add("$message", SqliteType.Text);

        long catalogRows = 0; var eligible = 0; var processedAssets = 0; var updated = 0; var unchanged = 0; var issueCount = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batch = new List<CatalogCandidate>(ParseBatchSize);
        using (var reader = new StreamReader(catalogPath, Encoding.UTF8, true, 1024 * 1024))
        {
            var header = reader.ReadLine() ?? throw new InvalidDataException("The asset catalog is empty.");
            var headerFields = AssetComparisonAggregateCache.ParseCsv(header);
            var required = new[] { "category", "format", "source", "relative_path", "bytes" };
            if (headerFields.Count < required.Length || !required.Select((field, index) => headerFields[index].Equals(field, StringComparison.OrdinalIgnoreCase)).All(value => value))
                throw new InvalidDataException("The asset catalog header is not recognized.");
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested(); catalogRows++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                // asset-catalog.csv is intentionally broad. Avoid allocating a
                // five-field CSV object graph for millions of BLP/PNG/audio/UI
                // rows when reverse texture lookup can consume only these four
                // binary container extensions. The later parsed extension check
                // remains authoritative, so this is only a conservative prefilter.
                if (!PotentialConsumerLine(line)) continue;
                IReadOnlyList<string> fields;
                try { fields = AssetComparisonAggregateCache.ParseCsv(line); }
                catch (Exception exception) when (exception is InvalidDataException or FormatException)
                {
                    AddIssue($"row:{catalogRows}", catalogRows, string.Empty, exception.Message); continue;
                }
                if (fields.Count < 5) { AddIssue($"row:{catalogRows}", catalogRows, string.Empty, "Catalog row is truncated."); continue; }
                var relative = fields[3]; var extension = Path.GetExtension(relative).ToLowerInvariant();
                if (!ConsumerExtensions.Contains(extension)) continue;
                string sourcePath;
                try { sourcePath = ResolveCatalogPath(libraryRoot, relative); }
                catch (Exception exception) when (exception is InvalidDataException or ArgumentException or NotSupportedException)
                { AddIssue($"row:{catalogRows}", catalogRows, relative, exception.Message); continue; }
                if (!seen.Add(sourcePath)) { AddIssue($"duplicate:{sourcePath}", catalogRows, relative, "Catalog contains the same consumer source path more than once."); continue; }
                eligible++;
                batch.Add(new(catalogRows, relative, sourcePath, extension));
                if (batch.Count >= ParseBatchSize) FlushBatch();
            }
            FlushBatch();
        }

        var removed = ScalarInt(connection, transaction, "SELECT COUNT(*) FROM assets WHERE seen_generation <> $generation", ("$generation", generation));
        Execute(connection, transaction, "DELETE FROM assets WHERE seen_generation <> $generation", ("$generation", generation));
        var afterIdentity = AssetComparisonAggregateCache.CaptureIdentity(catalogPath, cancellationToken);
        if (beforeIdentity != afterIdentity) throw new IOException("The asset catalog changed while the reverse texture-consumer index was being generated. The prior index was preserved.");
        var generated = DateTimeOffset.UtcNow;
        var indexed = ScalarInt(connection, transaction, "SELECT COUNT(*) FROM assets WHERE scan_state='Indexed'");
        var unsupported = ScalarInt(connection, transaction, "SELECT COUNT(*) FROM assets WHERE scan_state='Unsupported'");
        var invalid = ScalarInt(connection, transaction, "SELECT COUNT(*) FROM assets WHERE scan_state='Invalid'");
        var missing = ScalarInt(connection, transaction, "SELECT COUNT(*) FROM assets WHERE scan_state='Missing'");
        var referencesTotal = ScalarInt(connection, transaction, "SELECT COUNT(*) FROM texture_refs");
        issueCount = ScalarInt(connection, transaction, "SELECT COUNT(*) FROM catalog_issues");
        SetMetadata(connection, transaction, new Dictionary<string, string>
        {
            ["format_version"] = FormatVersion.ToString(CultureInfo.InvariantCulture), ["generated_utc"] = generated.ToString("O", CultureInfo.InvariantCulture),
            ["catalog_path"] = catalogPath, ["catalog_rows"] = catalogRows.ToString(CultureInfo.InvariantCulture), ["eligible_assets"] = eligible.ToString(CultureInfo.InvariantCulture),
            ["indexed_assets"] = indexed.ToString(CultureInfo.InvariantCulture), ["unsupported_assets"] = unsupported.ToString(CultureInfo.InvariantCulture), ["invalid_assets"] = invalid.ToString(CultureInfo.InvariantCulture),
            ["missing_assets"] = missing.ToString(CultureInfo.InvariantCulture), ["texture_references"] = referencesTotal.ToString(CultureInfo.InvariantCulture),
            ["catalog_issues"] = issueCount.ToString(CultureInfo.InvariantCulture), ["catalog_length"] = beforeIdentity.Length.ToString(CultureInfo.InvariantCulture),
            ["catalog_write_ticks"] = beforeIdentity.LastWriteTimeUtcTicks.ToString(CultureInfo.InvariantCulture), ["catalog_fingerprint"] = beforeIdentity.EdgeFingerprintSha256
        });
        cancellationToken.ThrowIfCancellationRequested(); transaction.Commit(); watch.Stop();
        var summary = new TextureConsumerIndexSummary(indexPath, catalogPath, generated, catalogRows, eligible, indexed, unsupported, invalid, missing, referencesTotal, issueCount);
        progress?.Report(new(catalogRows, eligible, updated, "Complete"));
        return new(summary, updated, unchanged, removed, watch.Elapsed.TotalMilliseconds);

        void AddIssue(string key, long row, string relativePath, string message)
        {
            issueKey.Value = key; issueRow.Value = row; issuePath.Value = relativePath; issueMessage.Value = message; insertIssue.ExecuteNonQuery(); issueCount++;
        }

        void FlushBatch()
        {
            if (batch.Count == 0) return;
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = batch.ToArray(); batch.Clear(); var parsed = new ParsedAsset[candidates.Length];
            var batchCompleted = 0;
            progress?.Report(new(catalogRows, processedAssets, updated, $"Batch start · {candidates.Length:N0} assets · {candidates[0].RelativePath}"));
            Parallel.For(0, candidates.Length, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount * 2, 1, 16) }, index =>
            {
                var candidate = candidates[index];
                var file = new FileInfo(candidate.SourcePath); file.Refresh();
                var length = file.Exists ? file.Length : -1L; var ticks = file.Exists ? file.LastWriteTimeUtc.Ticks : -1L;
                string clientPath = string.Empty, provenance = string.Empty;
                try
                {
                    if (file.Exists)
                    {
                        var location = ClientAssetDependencyService.InferExistingLocation(layout, candidate.SourcePath); clientPath = location.ClientPath; provenance = location.Provenance;
                    }
                    var same = existingAssets.TryGetValue(candidate.SourcePath, out var existing) && existing.ClientPath.Equals(clientPath, StringComparison.OrdinalIgnoreCase) &&
                        existing.Provenance.Equals(provenance, StringComparison.OrdinalIgnoreCase) && existing.Length == length && existing.WriteTicks == ticks;
                    if (same) { parsed[index] = new(candidate, clientPath, provenance, length, ticks, TextureConsumerAssetState.Indexed, null, [], true); ReportBatchProgress(); return; }
                    if (!file.Exists) { parsed[index] = new(candidate, clientPath, provenance, length, ticks, TextureConsumerAssetState.Missing, "The cataloged consumer file no longer exists.", [], false); ReportBatchProgress(); return; }
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException and not AccessViolationException)
                {
                    parsed[index] = new(candidate, clientPath, provenance, length, ticks, file.Exists ? TextureConsumerAssetState.Invalid : TextureConsumerAssetState.Missing, exception.Message, [], false); ReportBatchProgress(); return;
                }
                try
                {
                    var references = ClientAssetDependencyService.InspectTextureReferences(candidate.SourcePath, clientPath)
                        .Select(reference => (Path: PatchInputMapper.NormalizeArchivePath(reference.ClientPath), reference.Kind))
                        .DistinctBy(reference => (reference.Path.ToUpperInvariant(), reference.Kind.ToUpperInvariant())).ToArray();
                    parsed[index] = new(candidate, clientPath, provenance, length, ticks, TextureConsumerAssetState.Indexed, null, references, false);
                }
                catch (UnsupportedClientAssetFormatException exception)
                {
                    parsed[index] = new(candidate, clientPath, provenance, length, ticks, TextureConsumerAssetState.Unsupported, exception.Message, [], false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException and not AccessViolationException)
                {
                    parsed[index] = new(candidate, clientPath, provenance, length, ticks, TextureConsumerAssetState.Invalid, exception.Message, [], false);
                }
                ReportBatchProgress();

                void ReportBatchProgress()
                {
                    var completed = Interlocked.Increment(ref batchCompleted);
                    if (completed == 1 || completed == candidates.Length || completed % 64 == 0)
                        progress?.Report(new(catalogRows, processedAssets + completed, updated, $"Batch {completed:N0}/{candidates.Length:N0} · {candidate.RelativePath}"));
                }
            });
            foreach (var asset in parsed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (asset.Unchanged)
                {
                    touchGeneration.Value = generation; touchSource.Value = asset.Candidate.SourcePath; touch.ExecuteNonQuery(); unchanged++;
                }
                else
                {
                    deleteRefSource.Value = asset.Candidate.SourcePath; deleteRefs.ExecuteNonQuery();
                    upsertParameters["source"].Value = asset.Candidate.SourcePath; upsertParameters["client"].Value = asset.ClientPath; upsertParameters["provenance"].Value = asset.Provenance;
                    upsertParameters["extension"].Value = asset.Candidate.Extension; upsertParameters["length"].Value = asset.Length; upsertParameters["ticks"].Value = asset.WriteTicks;
                    upsertParameters["state"].Value = asset.State.ToString(); upsertParameters["error"].Value = (object?)asset.Error ?? DBNull.Value; upsertParameters["generation"].Value = generation; upsert.ExecuteNonQuery();
                    foreach (var reference in asset.References) { refSource.Value = asset.Candidate.SourcePath; refTexture.Value = reference.Path; refKind.Value = reference.Kind; insertRef.ExecuteNonQuery(); }
                    updated++;
                }
                processedAssets++;
                if (processedAssets % 100 == 0) progress?.Report(new(catalogRows, processedAssets, updated, asset.ClientPath.Length == 0 ? asset.Candidate.RelativePath : asset.ClientPath));
            }
        }
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=DELETE;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS assets(
              source_path TEXT PRIMARY KEY COLLATE NOCASE,
              client_path TEXT NOT NULL COLLATE NOCASE,
              provenance TEXT NOT NULL COLLATE NOCASE,
              extension TEXT NOT NULL COLLATE NOCASE,
              length INTEGER NOT NULL,
              write_ticks INTEGER NOT NULL,
              scan_state TEXT NOT NULL,
              error TEXT NULL,
              seen_generation TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS texture_refs(
              source_path TEXT NOT NULL COLLATE NOCASE REFERENCES assets(source_path) ON DELETE CASCADE,
              texture_path TEXT NOT NULL COLLATE NOCASE,
              reference_kind TEXT NOT NULL COLLATE NOCASE,
              PRIMARY KEY(source_path,texture_path,reference_kind));
            CREATE INDEX IF NOT EXISTS ix_texture_refs_texture ON texture_refs(texture_path COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS ix_assets_client ON assets(client_path COLLATE NOCASE,provenance COLLATE NOCASE);
            CREATE TABLE IF NOT EXISTS catalog_issues(issue_key TEXT PRIMARY KEY, row_number INTEGER NOT NULL, relative_path TEXT NOT NULL, message TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    private static TextureConsumerIndexSummary ReadSummary(SqliteConnection connection, string indexPath)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand(); command.CommandText = "SELECT key,value FROM metadata";
        using var reader = command.ExecuteReader(); while (reader.Read()) values[reader.GetString(0)] = reader.GetString(1);
        string Value(string key) => values.TryGetValue(key, out var value) ? value : throw new InvalidDataException($"Texture-consumer index metadata is missing '{key}'.");
        int Int(string key) => int.Parse(Value(key), CultureInfo.InvariantCulture); long Long(string key) => long.Parse(Value(key), CultureInfo.InvariantCulture);
        return new(indexPath, Value("catalog_path"), DateTimeOffset.Parse(Value("generated_utc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            Long("catalog_rows"), Int("eligible_assets"), Int("indexed_assets"), Int("unsupported_assets"), Int("invalid_assets"), Int("missing_assets"), Int("texture_references"), Int("catalog_issues"));
    }

    private static string ResolveCatalogPath(string libraryRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) throw new InvalidDataException("Catalog consumer path is empty or rooted.");
        var parts = relativePath.Split(['\\', '/'], StringSplitOptions.None);
        if (parts.Any(part => part.Length == 0 || part is "." or "..")) throw new InvalidDataException("Catalog consumer path contains an empty, current, or parent segment.");
        var full = Path.GetFullPath(Path.Combine(libraryRoot, Path.Combine(parts)));
        var relative = Path.GetRelativePath(libraryRoot, full);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Catalog consumer path escapes the processed library.");
        return full;
    }

    private static bool PotentialConsumerLine(string line)
        => line.Contains(".m2", StringComparison.OrdinalIgnoreCase) || line.Contains(".wmo", StringComparison.OrdinalIgnoreCase) ||
           line.Contains(".adt", StringComparison.OrdinalIgnoreCase) || line.Contains(".wdt", StringComparison.OrdinalIgnoreCase);

    private static bool IsCompatible(string path)
    {
        try { using var connection = Open(path, SqliteOpenMode.ReadOnly); RequireCompatible(connection); return true; }
        catch (Exception exception) when (exception is SqliteException or InvalidDataException or FormatException) { return false; }
    }

    private static void RequireCompatible(SqliteConnection connection)
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT value FROM metadata WHERE key='format_version'";
        var value = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) || version != FormatVersion)
            throw new InvalidDataException($"Texture-consumer index format is missing or unsupported; expected {FormatVersion}.");
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Cache = SqliteCacheMode.Private, Pooling = false }.ToString());
        connection.Open(); using var pragma = connection.CreateCommand(); pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000"; pragma.ExecuteNonQuery(); return connection;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return command; }
    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; foreach (var pair in parameters) command.Parameters.AddWithValue(pair.Name, pair.Value); command.ExecuteNonQuery(); }
    private static int ScalarInt(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; foreach (var pair in parameters) command.Parameters.AddWithValue(pair.Name, pair.Value); return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture); }
    private static void SetMetadata(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyDictionary<string, string> values)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "INSERT INTO metadata(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        var key = command.Parameters.Add("$key", SqliteType.Text); var value = command.Parameters.Add("$value", SqliteType.Text); command.Prepare();
        foreach (var pair in values) { key.Value = pair.Key; value.Value = pair.Value; command.ExecuteNonQuery(); }
    }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
