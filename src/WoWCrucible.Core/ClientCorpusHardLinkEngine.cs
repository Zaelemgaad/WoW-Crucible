using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace WoWCrucible.Core;

/// <summary>
/// Finds byte-identical files that are present in every complete client of the same
/// build below the reviewed discovery roots. Pairwise and partial matches remain independent.
/// </summary>
public sealed partial class ClientCorpusHardLinkService
{
    public const int RequestFormatVersion = 2;
    public const int PlanFormatVersion = 2;
    public const int ApplyReportFormatVersion = 1;
    private const int LegacyPlanFormatVersion = 1;
    private const uint MaximumHardLinks = 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed record CachedHash(long Length, long WriteTicks, string VolumeSerial, string FileId, string Sha256);

    private sealed class ScannedFile
    {
        public required string CohortId { get; init; }
        public required int ClientIndex { get; init; }
        public required string ClientRoot { get; init; }
        public required string RelativePath { get; init; }
        public required string FullPath { get; init; }
        public required long Length { get; init; }
        public required long CreationTicks { get; init; }
        public required long WriteTicks { get; init; }
        public required int Attributes { get; init; }
        public required NativeFileIdentity Identity { get; init; }
        public required bool HasZoneIdentifier { get; init; }
        public string? Sha256 { get; set; }
    }

    private sealed record ScanCounters(long ReparsePoints, long ZoneIdentifierFiles, long OtherAlternateStreamFiles);

    public static ClientCorpusHardLinkRequest LoadRequest(string path)
    {
        path = RequiredFile(path, "client corpus hard-link request");
        var request = JsonSerializer.Deserialize<ClientCorpusHardLinkRequest>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException($"Client corpus hard-link request is empty: {path}");
        ValidateRequest(request);
        return request;
    }

    public static void SaveRequest(string path, ClientCorpusHardLinkRequest request)
    {
        ValidateRequest(request);
        AtomicWrite(path, JsonSerializer.Serialize(request, JsonOptions));
    }

    public static ClientCorpusHardLinkPlan LoadPlan(string path)
    {
        path = RequiredFile(path, "client corpus hard-link plan");
        var plan = JsonSerializer.Deserialize<ClientCorpusHardLinkPlan>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException($"Client corpus hard-link plan is empty: {path}");
        if (plan.FormatVersion is not LegacyPlanFormatVersion and not PlanFormatVersion)
            throw new InvalidDataException($"Unsupported client corpus hard-link plan version {plan.FormatVersion}.");
        var expectedFingerprint = plan.FormatVersion == LegacyPlanFormatVersion
            ? LegacyFingerprint(plan.Roots, plan.ConsensusGroups, plan.Actions)
            : Fingerprint(plan);
        if (!string.Equals(plan.Fingerprint, expectedFingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("Client corpus hard-link plan fingerprint does not match its contents.");
        if (plan.FormatVersion == PlanFormatVersion && plan.Coverage is null)
            throw new InvalidDataException("Current client corpus hard-link plans require whole-library coverage evidence.");
        return plan;
    }

    public ClientCorpusHardLinkPlan Plan(
        ClientCorpusHardLinkRequest request,
        IProgress<ClientCorpusHardLinkProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var coverage = DiscoverAndValidateCoverage(request, progress, cancellationToken);
        var created = DateTimeOffset.UtcNow;
        var outputRoot = Path.GetFullPath(request.OutputRoot);
        Directory.CreateDirectory(outputRoot);
        var planRoot = Path.Combine(outputRoot, "plans", $"plan-{created:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(planRoot);
        var cachePath = Path.Combine(outputRoot, "client-content-hashes.sqlite");
        var excludedDirectories = NormalizeDirectoryNames(request.ExcludedDirectoryNames);
        var excludedPaths = NormalizeRelativePaths(request.ExcludedRelativePaths);
        var allFiles = new List<ScannedFile>();
        var roots = new List<ClientCorpusRootSnapshot>();
        var warnings = new List<string>();
        long skippedReparse = 0;
        long zoneIdentifierFiles = 0;
        long skippedOtherStreams = 0;
        var totalClients = request.Cohorts.Sum(value => value.ClientRoots.Count);

        foreach (var cohort in request.Cohorts)
        {
            for (var index = 0; index < cohort.ClientRoots.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = Path.GetFullPath(cohort.ClientRoots[index]);
                progress?.Report(new("Scanning client", roots.Count, totalClients, root));
                var start = allFiles.Count;
                var counters = EnumerateClient(root, cohort.Id, index, request.MinimumFileBytes, excludedDirectories,
                    excludedPaths, allFiles, progress, cancellationToken);
                var clientFiles = allFiles.Skip(start).ToArray();
                skippedReparse += counters.ReparsePoints;
                zoneIdentifierFiles += counters.ZoneIdentifierFiles;
                skippedOtherStreams += counters.OtherAlternateStreamFiles;
                roots.Add(new(cohort.Id, index, root, clientFiles.Length, clientFiles.Sum(file => file.Length),
                    clientFiles.Select(file => file.Identity.VolumeSerialHex).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));
            }
        }

        var candidates = new HashSet<ScannedFile>();
        foreach (var cohort in request.Cohorts)
        {
            var cohortFiles = allFiles.Where(file => file.CohortId.Equals(cohort.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var lengthGroup in cohortFiles.GroupBy(file => file.Length))
                if (lengthGroup.Select(file => file.ClientIndex).Distinct().Count() == cohort.ClientRoots.Count)
                    foreach (var file in lengthGroup) candidates.Add(file);
        }

        var (hashed, reused) = PopulateHashes(cachePath,
            candidates.OrderBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase).ToArray(),
            request.HashWorkers, progress, cancellationToken);
        var consensus = new List<ClientCorpusConsensusGroup>();
        var actions = new List<ClientCorpusHardLinkAction>();
        var near = new List<ClientCorpusNearConsensus>();

        foreach (var cohort in request.Cohorts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cohortFiles = candidates.Where(file =>
                file.CohortId.Equals(cohort.Id, StringComparison.OrdinalIgnoreCase) && file.Sha256 is not null).ToArray();
            foreach (var content in cohortFiles.GroupBy(file => (file.Length, Sha256: file.Sha256!), new ContentKeyComparer()))
            {
                var presentClients = content.Select(file => file.ClientIndex).Distinct().Count();
                if (presentClients != cohort.ClientRoots.Count)
                {
                    if (presentClients > 1)
                        near.Add(new(cohort.Id, content.Key.Sha256, content.Key.Length, presentClients,
                            cohort.ClientRoots.Count, content.Count(), content.OrderBy(file => file.FullPath,
                                StringComparer.OrdinalIgnoreCase).Take(5).Select(file => file.FullPath).ToArray()));
                    continue;
                }

                var ordered = content.OrderBy(file => file.ClientIndex)
                    .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
                var groupActions = BuildActions(cohort.Id, content.Key.Sha256, content.Key.Length, ordered, warnings);
                actions.AddRange(groupActions);
                var physicalBefore = ordered.Select(file => file.Identity.Key).Distinct(StringComparer.Ordinal).Count();
                var removedIdentities = groupActions.Where(action => action.EstimatedReclaimableBytes > 0)
                    .Select(action => $"{action.TargetVolumeSerial}:{action.TargetFileId}")
                    .Distinct(StringComparer.Ordinal).Count();
                consensus.Add(new(cohort.Id, content.Key.Sha256, content.Key.Length, cohort.ClientRoots.Count,
                    ordered.Length, physicalBefore, Math.Max(1, physicalBefore - removedIdentities),
                    groupActions.Sum(action => action.EstimatedReclaimableBytes), ordered.Select(ToOccurrence).ToArray()));
            }
        }

        var orderedGroups = consensus.OrderByDescending(group => group.EstimatedReclaimableBytes)
            .ThenBy(group => group.CohortId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Sha256, StringComparer.Ordinal).ToArray();
        var orderedActions = actions.OrderBy(action => action.CohortId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(action => action.TargetPath, StringComparer.OrdinalIgnoreCase).ToArray();
        var orderedRoots = roots.OrderBy(root => root.CohortId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(root => root.ClientIndex).ToArray();
        var nearSamples = near.OrderByDescending(value => value.Length * value.Occurrences).Take(250).ToArray();
        if (near.Count > nearSamples.Length)
            warnings.Add($"{near.Count - nearSamples.Length:N0} additional partial content matches were omitted from the near-consensus sample list.");
        if (zoneIdentifierFiles > 0)
            warnings.Add($"Ignored Windows Zone.Identifier metadata on {zoneIdentifierFiles:N0} file(s); linked targets inherit the canonical file's mark-of-the-web state.");
        warnings.Add($"Validated {coverage.Clients.Count:N0} complete client root(s) against every represented build below {coverage.DiscoveryRoots.Count:N0} discovery root(s).");

        var planPath = Path.Combine(planRoot, "client-hardlink-plan.json");
        var markdownPath = Path.Combine(planRoot, "client-hardlink-plan.md");
        var plan = new ClientCorpusHardLinkPlan(PlanFormatVersion, created, outputRoot, cachePath, planPath,
            markdownPath, string.Empty, orderedRoots,
            excludedDirectories.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            excludedPaths.Order(StringComparer.OrdinalIgnoreCase).ToArray(), request.MinimumFileBytes,
            allFiles.Count, allFiles.Sum(file => file.Length), skippedReparse, zoneIdentifierFiles,
            skippedOtherStreams, hashed, reused,
            orderedGroups, nearSamples, orderedActions,
            orderedActions.Sum(action => action.EstimatedReclaimableBytes),
            warnings.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), [], coverage);
        plan = plan with { Fingerprint = Fingerprint(plan) };
        AtomicWrite(planPath, JsonSerializer.Serialize(plan, JsonOptions));
        AtomicWrite(markdownPath, RenderPlan(plan));
        progress?.Report(new("Plan complete", orderedActions.Length, orderedActions.Length, planPath));
        return plan;
    }

    public ClientCorpusHardLinkApplyReport Apply(
        string planPath,
        bool materialize,
        IProgress<ClientCorpusHardLinkProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var plan = LoadPlan(planPath);
        if (!plan.Ready)
            throw new InvalidDataException("The client corpus hard-link plan contains blockers and cannot be applied.");
        if (!materialize)
        {
            if (plan.FormatVersion != PlanFormatVersion || plan.Coverage is null)
                throw new InvalidDataException("This legacy plan has no fingerprint-bound whole-library same-build coverage proof. It may be materialized, but a new plan is required before linking.");
            VerifyCurrentCoverage(plan, progress, cancellationToken);
        }
        var started = DateTimeOffset.UtcNow;
        var operation = materialize ? "materialize" : "apply";
        var reportRoot = Path.Combine(plan.OutputRoot, "applications",
            $"{operation}-{started:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(reportRoot);
        var journalPath = Path.Combine(reportRoot, "operation.journal.jsonl");
        var entries = new List<ClientCorpusHardLinkApplyEntry>();
        var errors = new List<string>();

        for (var index = 0; index < plan.Actions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var action = plan.Actions[index];
            progress?.Report(new(materialize ? "Materializing independent file" : "Linking unanimous file",
                index, plan.Actions.Count, action.TargetPath));
            try
            {
                var state = materialize
                    ? MaterializeAction(action, journalPath, cancellationToken)
                    : ApplyAction(action, journalPath, cancellationToken);
                entries.Add(new(action.Id, action.TargetPath, state,
                    state == ClientCorpusHardLinkApplyState.Linked ? action.EstimatedReclaimableBytes : 0, null));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var message = $"{action.TargetPath}: {exception.Message}";
                AppendJournal(journalPath, new
                {
                    Utc = DateTimeOffset.UtcNow,
                    action.Id,
                    action.TargetPath,
                    State = "Failed",
                    Error = exception.Message
                });
                entries.Add(new(action.Id, action.TargetPath, ClientCorpusHardLinkApplyState.Failed, 0, exception.Message));
                errors.Add(message);
                break;
            }
        }

        var jsonPath = Path.Combine(reportRoot, "client-hardlink-apply-report.json");
        var markdownPath = Path.Combine(reportRoot, "client-hardlink-apply-report.md");
        var report = new ClientCorpusHardLinkApplyReport(ApplyReportFormatVersion, started, DateTimeOffset.UtcNow,
            Path.GetFullPath(planPath), plan.Fingerprint, materialize, reportRoot, journalPath, jsonPath,
            markdownPath, entries, errors);
        AtomicWrite(jsonPath, JsonSerializer.Serialize(report, JsonOptions));
        AtomicWrite(markdownPath, RenderApply(report));
        progress?.Report(new(materialize ? "Materialization complete" : "Hard-link application complete",
            entries.Count, plan.Actions.Count, reportRoot));
        return report;
    }

    internal static NativeFileIdentity ReadIdentity(string path) => NativeHardLinks.ReadIdentity(path);

    private static ScanCounters EnumerateClient(
        string root,
        string cohortId,
        int clientIndex,
        long minimumBytes,
        IReadOnlySet<string> excludedDirectories,
        IReadOnlySet<string> excludedPaths,
        List<ScannedFile> output,
        IProgress<ClientCorpusHardLinkProgress>? progress,
        CancellationToken cancellationToken)
    {
        root = RequiredDirectory(root, $"{cohortId} client root");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"A client root cannot itself be a reparse point: {root}");
        long skippedReparse = 0;
        long zoneIdentifierFiles = 0;
        long skippedOtherStreams = 0;
        long visited = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    skippedReparse++;
                    continue;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!excludedDirectories.Contains(Path.GetFileName(entry))) pending.Push(entry);
                    continue;
                }

                var relative = Path.GetRelativePath(root, entry);
                if (excludedPaths.Contains(relative)) continue;
                var info = new FileInfo(entry);
                if (info.Length < minimumBytes) continue;
                var alternateStreams = NativeHardLinks.ReadAlternateStreamNames(entry);
                var hasZoneIdentifier = alternateStreams.Any(stream =>
                    stream.Equals(":Zone.Identifier:$DATA", StringComparison.OrdinalIgnoreCase));
                if (alternateStreams.Any(stream =>
                        !stream.Equals(":Zone.Identifier:$DATA", StringComparison.OrdinalIgnoreCase)))
                {
                    skippedOtherStreams++;
                    continue;
                }
                if (hasZoneIdentifier) zoneIdentifierFiles++;
                var identity = NativeHardLinks.ReadIdentity(entry);
                output.Add(new ScannedFile
                {
                    CohortId = cohortId,
                    ClientIndex = clientIndex,
                    ClientRoot = root,
                    RelativePath = relative,
                    FullPath = info.FullName,
                    Length = info.Length,
                    CreationTicks = info.CreationTimeUtc.Ticks,
                    WriteTicks = info.LastWriteTimeUtc.Ticks,
                    Attributes = (int)info.Attributes,
                    Identity = identity,
                    HasZoneIdentifier = hasZoneIdentifier
                });
                visited++;
                if (visited % 4096 == 0)
                    progress?.Report(new("Scanning client files", visited, 0, entry));
            }
        }
        return new(skippedReparse, zoneIdentifierFiles, skippedOtherStreams);
    }

    private static (long Hashed, long Reused) PopulateHashes(
        string cachePath,
        IReadOnlyList<ScannedFile> files,
        int requestedWorkers,
        IProgress<ClientCorpusHardLinkProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        using var connection = new SqliteConnection($"Data Source={cachePath};Mode=ReadWriteCreate;Cache=Shared;Pooling=False");
        connection.Open();
        InitializeCache(connection);
        var cache = ReadCache(connection);
        long reused = 0;
        var pending = new List<ScannedFile>();
        foreach (var file in files)
        {
            if (cache.TryGetValue(file.FullPath, out var value) && value.Length == file.Length &&
                value.WriteTicks == file.WriteTicks && value.VolumeSerial == file.Identity.VolumeSerialHex &&
                value.FileId == file.Identity.FileIdHex)
            {
                file.Sha256 = value.Sha256;
                reused++;
            }
            else pending.Add(file);
        }

        var workers = requestedWorkers <= 0
            ? Math.Clamp(Environment.ProcessorCount, 1, 16)
            : Math.Clamp(requestedWorkers, 1, 64);
        long hashed = 0;
        var processed = reused;
        progress?.Report(new("Hashing cohort candidates", processed, files.Count,
            files.FirstOrDefault()?.FullPath ?? cachePath));
        foreach (var batch in pending.Chunk(256))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Parallel.ForEach(batch, new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = workers
            }, file =>
            {
                var before = NativeHardLinks.ReadIdentity(file.FullPath);
                if (before.Key != file.Identity.Key)
                    throw new InvalidDataException($"File identity changed before hashing: {file.FullPath}");
                var hash = Hash(file.FullPath, cancellationToken);
                var info = new FileInfo(file.FullPath);
                var after = NativeHardLinks.ReadIdentity(file.FullPath);
                if (after.Key != before.Key || info.Length != file.Length || info.LastWriteTimeUtc.Ticks != file.WriteTicks)
                    throw new InvalidDataException($"File changed while it was being hashed: {file.FullPath}");
                file.Sha256 = hash;
                var done = Interlocked.Increment(ref processed);
                Interlocked.Increment(ref hashed);
                if (done % 128 == 0 || done == files.Count)
                    progress?.Report(new("Hashing cohort candidates", done, files.Count, file.FullPath));
            });
            WriteCache(connection, batch);
        }
        return (hashed, reused);
    }

    private static void InitializeCache(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS file_hashes(
              path TEXT PRIMARY KEY COLLATE NOCASE,
              length INTEGER NOT NULL,
              write_ticks INTEGER NOT NULL,
              volume_serial TEXT NOT NULL,
              file_id TEXT NOT NULL,
              sha256 TEXT NOT NULL,
              updated_utc TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static Dictionary<string, CachedHash> ReadCache(SqliteConnection connection)
    {
        var result = new Dictionary<string, CachedHash>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path,length,write_ticks,volume_serial,file_id,sha256 FROM file_hashes";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = new(reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5));
        return result;
    }

    private static void WriteCache(SqliteConnection connection, IReadOnlyList<ScannedFile> files)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO file_hashes(path,length,write_ticks,volume_serial,file_id,sha256,updated_utc)
            VALUES($path,$length,$ticks,$volume,$file,$sha,$utc)
            ON CONFLICT(path) DO UPDATE SET length=excluded.length,write_ticks=excluded.write_ticks,
              volume_serial=excluded.volume_serial,file_id=excluded.file_id,sha256=excluded.sha256,updated_utc=excluded.updated_utc
            """;
        foreach (var name in new[] { "$path", "$length", "$ticks", "$volume", "$file", "$sha", "$utc" })
            command.Parameters.Add(new(name, null));
        foreach (var file in files)
        {
            var values = new object[]
            {
                file.FullPath, file.Length, file.WriteTicks, file.Identity.VolumeSerialHex,
                file.Identity.FileIdHex, file.Sha256!, DateTimeOffset.UtcNow.ToString("O")
            };
            for (var index = 0; index < values.Length; index++) command.Parameters[index].Value = values[index];
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private sealed class ContentKeyComparer : IEqualityComparer<(long Length, string Sha256)>
    {
        public bool Equals((long Length, string Sha256) left, (long Length, string Sha256) right) =>
            left.Length == right.Length && left.Sha256.Equals(right.Sha256, StringComparison.Ordinal);

        public int GetHashCode((long Length, string Sha256) value) =>
            HashCode.Combine(value.Length, StringComparer.Ordinal.GetHashCode(value.Sha256));
    }
}
