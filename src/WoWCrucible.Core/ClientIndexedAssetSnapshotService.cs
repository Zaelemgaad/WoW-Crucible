using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoWCrucible.Core;

public enum ClientIndexedAssetSnapshotState { Root, Resolved, ExternalBinding, Missing, Ambiguous, Invalid }

public sealed record ClientIndexedAssetSnapshotFile(
    int Depth,
    string? ParentClientPath,
    string Kind,
    string ClientPath,
    ClientIndexedAssetSnapshotState State,
    string? SourceRelativePath,
    string? SourceSha256,
    string? ArchiveRelativePath,
    ClientArchiveScope? ArchiveScope,
    long? ArchiveLength,
    long? ArchiveLastWriteUtcTicks,
    string? ArchiveSha256,
    long? EntrySize,
    uint? EntryLocale,
    uint? EntryBlockIndex,
    IReadOnlyList<string> Candidates,
    string Message);

public sealed record ClientIndexedAssetSnapshot(
    int FormatVersion,
    string ContentSha256,
    DateTimeOffset CreatedUtc,
    string IndexDirectory,
    string ClientName,
    string? ActiveLocale,
    string IndexFingerprint,
    string SourcesDirectory,
    IReadOnlyList<string> RootClientPaths,
    IReadOnlyList<ClientIndexedAssetSnapshotFile> Files)
{
    [JsonIgnore]
    public IReadOnlyList<ClientIndexedAssetSnapshotFile> Blocking => Files.Where(file => file.State is
        ClientIndexedAssetSnapshotState.Missing or ClientIndexedAssetSnapshotState.Ambiguous or ClientIndexedAssetSnapshotState.Invalid).ToArray();
    [JsonIgnore]
    public bool Ready => Blocking.Count == 0;
}

public sealed record ClientIndexedGameObjectPlanResult(ClientIndexedAssetSnapshot Snapshot, string SnapshotPath,
    GameObjectBulkPlan Plan, string PlanPath);

/// <summary>
/// Materializes selected virtual paths from the effective active MPQ layers into an
/// immutable, hash-bound source snapshot. The snapshot is intentionally separate
/// from GameObject planning so archive extraction and content authoring remain
/// independently reviewable.
/// </summary>
public static class ClientIndexedAssetSnapshotService
{
    public const int CurrentFormatVersion = 1;
    public const string ManifestFileName = "indexed-assets.snapshot.json";
    private const int MaximumFiles = 250_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };

    private sealed record Work(string ClientPath, string? ParentClientPath, string Kind, int Depth, bool Root);

    public static ClientIndexedAssetSnapshot Create(string indexDirectory, string outputRoot, IEnumerable<string> rootClientPaths,
        IReadOnlyDictionary<string, string>? archiveOverrides = null, CancellationToken cancellationToken = default)
    {
        indexDirectory = Path.GetFullPath(indexDirectory); outputRoot = Path.GetFullPath(outputRoot);
        var roots = (rootClientPaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Length == 0) throw new ArgumentException("Select at least one virtual client M2 or root WMO path.", nameof(rootClientPaths));
        foreach (var root in roots)
            if (!Path.GetExtension(root).Equals(".m2", StringComparison.OrdinalIgnoreCase) &&
                (!Path.GetExtension(root).Equals(".wmo", StringComparison.OrdinalIgnoreCase) || System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(root), @"_\d{3}$")))
                throw new InvalidDataException($"Indexed GameObject roots must be M2 files or root WMO files: {root}");
        if (Directory.Exists(outputRoot) && Directory.EnumerateFileSystemEntries(outputRoot).Any()) throw new IOException($"Indexed source workspace must be new or empty: {outputRoot}");

        var catalog = ClientEffectiveAssetCatalog.Load(indexDirectory, cancellationToken);
        var overrides = (archiveOverrides ?? new Dictionary<string, string>()).ToDictionary(pair => Normalize(pair.Key), pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var parent = Path.GetDirectoryName(outputRoot) ?? throw new InvalidOperationException("Indexed source workspace has no parent directory.");
        Directory.CreateDirectory(parent); var staging = Path.Combine(parent, $".{Path.GetFileName(outputRoot)}.crucible-{Guid.NewGuid():N}");
        var stagingSources = Path.Combine(staging, "Sources"); Directory.CreateDirectory(stagingSources);
        try
        {
            var queue = new Queue<Work>(roots.Select(path => new Work(path, null, "root", 0, true)));
            var files = new List<ClientIndexedAssetSnapshotFile>(); var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (queue.TryDequeue(out var work))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (files.Count >= MaximumFiles) throw new InvalidDataException($"Indexed dependency snapshot exceeded {MaximumFiles:N0} entries; the input is corrupt or unexpectedly broad.");
                if (!expanded.Add(work.ClientPath)) continue;
                overrides.TryGetValue(work.ClientPath, out var preferredArchive);
                ClientEffectiveAssetResolution resolution;
                try { resolution = catalog.Resolve(work.ClientPath, preferredArchive, cancellationToken); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    files.Add(Failure(work, ClientIndexedAssetSnapshotState.Invalid, exception.Message)); continue;
                }
                if (resolution.State != ClientEffectiveAssetState.Effective || resolution.Effective is null)
                {
                    var state = resolution.State == ClientEffectiveAssetState.Missing ? ClientIndexedAssetSnapshotState.Missing : ClientIndexedAssetSnapshotState.Ambiguous;
                    files.Add(Failure(work, state, resolution.Message, resolution.Candidates.Select(candidate => candidate.ArchiveRelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())); continue;
                }

                var candidate = resolution.Effective; string extracted;
                try
                {
                    extracted = catalog.ExtractEffective(candidate, stagingSources, false, cancellationToken);
                    var extractedHash = Hash(extracted, cancellationToken); var archiveHash = catalog.HashEffective(candidate, cancellationToken);
                    if (!extractedHash.Equals(archiveHash, StringComparison.Ordinal)) throw new IOException($"Effective extraction hash mismatch for {work.ClientPath}.");
                    var summary = catalog.Index.Archives.Single(archive => archive.RelativePath.Equals(candidate.ArchiveRelativePath, StringComparison.OrdinalIgnoreCase));
                    files.Add(new(work.Depth, work.ParentClientPath, work.Kind, work.ClientPath, work.Root ? ClientIndexedAssetSnapshotState.Root : ClientIndexedAssetSnapshotState.Resolved,
                        Path.Combine("Sources", work.ClientPath).Replace('/', Path.DirectorySeparatorChar), extractedHash, candidate.ArchiveRelativePath, candidate.Scope,
                        summary.Length, summary.LastWriteUtcTicks, summary.Sha256, candidate.Entry.Size, candidate.Entry.Locale, candidate.Entry.BlockIndex,
                        resolution.Candidates.Select(value => value.ArchiveRelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), resolution.Message));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    files.Add(Failure(work, ClientIndexedAssetSnapshotState.Invalid, exception.Message, resolution.Candidates.Select(value => value.ArchiveRelativePath).ToArray())); continue;
                }

                IReadOnlyList<ClientAssetReference> references;
                try { references = ClientAssetDependencyService.InspectReferences(extracted, work.ClientPath); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    files.Add(Failure(new(work.ClientPath, work.ClientPath, "parse", work.Depth + 1, false), ClientIndexedAssetSnapshotState.Invalid, exception.Message)); continue;
                }
                foreach (var reference in references)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (reference.External)
                    {
                        files.Add(new(work.Depth + 1, work.ClientPath, reference.Kind, reference.ClientPath, ClientIndexedAssetSnapshotState.ExternalBinding,
                            null, null, null, null, null, null, null, null, null, null, [], reference.Message ?? "Supplied through DBC/SQL appearance data."));
                    }
                    else if (reference.Missing)
                        files.Add(Failure(new(reference.ClientPath, work.ClientPath, reference.Kind, work.Depth + 1, false), ClientIndexedAssetSnapshotState.Missing, reference.Message ?? "Dependency has no usable client path."));
                    else queue.Enqueue(new(Normalize(reference.ClientPath), work.ClientPath, reference.Kind, work.Depth + 1, false));
                }
                if (Path.GetExtension(work.ClientPath).Equals(".m2", StringComparison.OrdinalIgnoreCase))
                {
                    var prefix = Path.Combine(Path.GetDirectoryName(work.ClientPath) ?? string.Empty, Path.GetFileNameWithoutExtension(work.ClientPath));
                    foreach (var animation in catalog.FindKnownPaths(prefix, ".anim")) queue.Enqueue(new(animation, work.ClientPath, "animation", work.Depth + 1, false));
                }
            }

            var ordered = files.OrderBy(file => file.Depth).ThenBy(file => file.ClientPath, StringComparer.OrdinalIgnoreCase).ThenBy(file => file.Kind, StringComparer.OrdinalIgnoreCase).ToArray();
            var snapshot = new ClientIndexedAssetSnapshot(CurrentFormatVersion, string.Empty, DateTimeOffset.UtcNow, indexDirectory, catalog.Index.Name,
                catalog.Index.ActiveLocale, catalog.Fingerprint, "Sources", roots, ordered);
            snapshot = snapshot with { ContentSha256 = ContentHash(snapshot) };
            WriteJson(Path.Combine(staging, ManifestFileName), snapshot);
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, false);
            Directory.Move(staging, outputRoot);
            Verify(snapshot, outputRoot, false, cancellationToken);
            return snapshot;
        }
        catch { if (Directory.Exists(staging)) Directory.Delete(staging, true); throw; }
    }

    public static ClientIndexedAssetSnapshot Load(string snapshotPath, bool verifyFiles = true, bool verifyArchives = false,
        CancellationToken cancellationToken = default)
    {
        snapshotPath = Path.GetFullPath(snapshotPath); var snapshot = JsonSerializer.Deserialize<ClientIndexedAssetSnapshot>(File.ReadAllText(snapshotPath), JsonOptions)
            ?? throw new InvalidDataException("The indexed asset snapshot is empty.");
        var workspace = Path.GetDirectoryName(snapshotPath) ?? throw new InvalidDataException("The indexed asset snapshot has no workspace directory.");
        Verify(snapshot, workspace, verifyArchives, cancellationToken, verifyFiles); return snapshot;
    }

    public static void Verify(ClientIndexedAssetSnapshot snapshot, string workspaceRoot, bool verifyArchives = false,
        CancellationToken cancellationToken = default, bool verifyFiles = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot); workspaceRoot = Path.GetFullPath(workspaceRoot);
        if (snapshot.FormatVersion != CurrentFormatVersion) throw new InvalidDataException($"Unsupported indexed asset snapshot version {snapshot.FormatVersion}.");
        var contentHash = ContentHash(snapshot); if (!contentHash.Equals(snapshot.ContentSha256, StringComparison.Ordinal)) throw new InvalidDataException("Indexed asset snapshot content hash mismatch.");
        if (!snapshot.SourcesDirectory.Equals("Sources", StringComparison.Ordinal)) throw new InvalidDataException("Indexed asset snapshot has an unsafe or unsupported source directory.");
        if (verifyFiles)
            foreach (var file in snapshot.Files.Where(file => file.SourceRelativePath is not null))
            {
                cancellationToken.ThrowIfCancellationRequested(); var path = SafeChild(workspaceRoot, file.SourceRelativePath!, "snapshot source");
                if (!File.Exists(path)) throw new FileNotFoundException($"Indexed snapshot source is missing for {file.ClientPath}.", path);
                if (!Hash(path, cancellationToken).Equals(file.SourceSha256, StringComparison.Ordinal)) throw new InvalidDataException($"Indexed snapshot source changed after capture: {file.ClientPath}");
            }
        if (verifyArchives)
        {
            var catalog = ClientEffectiveAssetCatalog.Load(snapshot.IndexDirectory, cancellationToken);
            if (!catalog.Fingerprint.Equals(snapshot.IndexFingerprint, StringComparison.Ordinal)) throw new InvalidDataException("Client archive index fingerprint changed after the snapshot was created.");
            foreach (var file in snapshot.Files.Where(file => file.ArchiveRelativePath is not null).GroupBy(file => file.ArchiveRelativePath!, StringComparer.OrdinalIgnoreCase).Select(group => group.First()))
            {
                var archive = catalog.Index.Archives.SingleOrDefault(value => value.RelativePath.Equals(file.ArchiveRelativePath, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"Snapshot archive disappeared: {file.ArchiveRelativePath}");
                if (archive.Length != file.ArchiveLength || archive.LastWriteUtcTicks != file.ArchiveLastWriteUtcTicks ||
                    !string.Equals(archive.Sha256, file.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Snapshot archive identity changed: {file.ArchiveRelativePath}");
                var archivePath = SafeChild(catalog.Index.ClientRoot, archive.RelativePath, "snapshot archive"); var info = new FileInfo(archivePath);
                if (!info.Exists || info.Length != file.ArchiveLength || info.LastWriteTimeUtc.Ticks != file.ArchiveLastWriteUtcTicks)
                    throw new InvalidDataException($"Snapshot archive changed on disk: {file.ArchiveRelativePath}");
                if (file.ArchiveSha256 is not null && !Hash(archivePath, cancellationToken).Equals(file.ArchiveSha256, StringComparison.Ordinal))
                    throw new InvalidDataException($"Snapshot archive content hash changed: {file.ArchiveRelativePath}");
            }
        }
    }

    public static IReadOnlyList<string> RootSourcePaths(ClientIndexedAssetSnapshot snapshot, string workspaceRoot)
    {
        Verify(snapshot, workspaceRoot, false); var roots = snapshot.Files.Where(file => file.State == ClientIndexedAssetSnapshotState.Root && file.SourceRelativePath is not null)
            .ToDictionary(file => file.ClientPath, file => SafeChild(workspaceRoot, file.SourceRelativePath!, "snapshot root"), StringComparer.OrdinalIgnoreCase);
        return snapshot.RootClientPaths.Select(path => roots.TryGetValue(path, out var source) ? source : throw new InvalidDataException($"Snapshot has no resolved source for root {path}.")).ToArray();
    }

    public static ClientIndexedGameObjectPlanResult CreateGameObjectPlan(string indexDirectory, string workspaceRoot, IEnumerable<string> rootClientPaths,
        string dbcPath, string schemaPath, uint displayStart, uint templateStart, DatabaseCapabilities? capabilities = null,
        IReadOnlyCollection<uint>? occupiedTemplateIds = null, IReadOnlyDictionary<string, string>? archiveOverrides = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = Create(indexDirectory, workspaceRoot, rootClientPaths, archiveOverrides, cancellationToken);
        var snapshotPath = Path.Combine(Path.GetFullPath(workspaceRoot), ManifestFileName);
        if (!snapshot.Ready) throw new InvalidOperationException($"Indexed source snapshot contains {snapshot.Blocking.Count:N0} blocker(s). Review {snapshotPath}.");
        var sources = RootSourcePaths(snapshot, workspaceRoot); var sourceRoot = Path.Combine(Path.GetFullPath(workspaceRoot), snapshot.SourcesDirectory);
        var plan = GameObjectBulkGeneratorService.CreatePlan(dbcPath, schemaPath, sources, displayStart, templateStart,
            clientRoot: sourceRoot, capabilities: capabilities, occupiedTemplateIds: occupiedTemplateIds, cancellationToken: cancellationToken);
        var planPath = Path.Combine(Path.GetFullPath(workspaceRoot), "gameobjects.plan.json"); GameObjectBulkGeneratorService.SavePlan(planPath, plan);
        return new(snapshot, snapshotPath, plan, planPath);
    }

    private static ClientIndexedAssetSnapshotFile Failure(Work work, ClientIndexedAssetSnapshotState state, string message, IReadOnlyList<string>? candidates = null)
        => new(work.Depth, work.ParentClientPath, work.Kind, work.ClientPath, state, null, null, null, null, null, null, null, null, null, null, candidates ?? [], message);
    private static string Normalize(string path) => PatchInputMapper.NormalizeArchivePath(path.Trim().Replace('/', '\\'));
    private static string ContentHash(ClientIndexedAssetSnapshot snapshot) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot with { ContentSha256 = string.Empty }, JsonOptions)));
    private static string Hash(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[1024 * 1024]; int read;
        while ((read = stream.Read(buffer)) > 0) { cancellationToken.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static void WriteJson(string path, object value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false)); }
    private static string SafeChild(string root, string relative, string label)
    {
        root = Path.GetFullPath(root); var path = Path.GetFullPath(Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar)));
        var check = Path.GetRelativePath(root, path); if (check == ".." || check.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(check)) throw new InvalidDataException($"The {label} escapes its workspace: {relative}");
        return path;
    }
}
