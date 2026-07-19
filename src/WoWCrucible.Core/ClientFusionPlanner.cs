using System.Security.Cryptography;
using System.Text.Json;

namespace WoWCrucible.Core;

public enum ClientFusionStatus { IdenticalToBase, Added, Override, IdenticalCandidates, Conflict }
public sealed record ClientFusionSource(string Name, string RootPath);
public sealed record ClientFusionCandidate(string SourceName, string SourceRoot, string FilePath, long Size, string? Sha256);
public sealed record ClientFusionEntry(string ArchivePath, ClientFusionStatus Status, string? BaseFilePath, IReadOnlyList<ClientFusionCandidate> Candidates, string Guidance);
public sealed record ClientFusionPlan(int FormatVersion, DateTimeOffset GeneratedUtc, string BaseRoot, IReadOnlyList<ClientFusionSource> Sources, IReadOnlyList<ClientFusionEntry> Entries);
public sealed record ClientFusionStageResult(string RootPath, string ManifestPath, int StagedFiles, int SkippedBaseFiles, int UnresolvedConflicts);

public static class ClientFusionPlanner
{
    public static ClientFusionPlan Analyze(string baseRoot, IEnumerable<ClientFusionSource> sources, IProgress<(int Done, int Total, string Path)>? progress = null, CancellationToken cancellationToken = default)
    {
        baseRoot = Path.GetFullPath(baseRoot);
        if (!Directory.Exists(baseRoot)) throw new DirectoryNotFoundException($"Fusion base folder not found: {baseRoot}");
        var normalizedSources = sources.Select(source => source with { RootPath = Path.GetFullPath(source.RootPath) }).ToArray();
        if (normalizedSources.Length == 0) throw new InvalidOperationException("Add at least one extracted/effective override source.");
        if (normalizedSources.Any(source => !Directory.Exists(source.RootPath))) throw new DirectoryNotFoundException("One or more fusion source folders no longer exist.");
        var baseCandidates = PatchInputMapper.MapCandidates([baseRoot]);
        var ambiguousBase = baseCandidates.GroupBy(entry => entry.ArchivePath, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (ambiguousBase is not null)
            throw new InvalidDataException($"The selected base contains multiple files for '{ambiguousBase.Key}'. Choose/extract one effective base layer before fusion; archive load order will not be guessed.");
        var baseFiles = baseCandidates.ToDictionary(entry => entry.ArchivePath, entry => entry.SourcePath, StringComparer.OrdinalIgnoreCase);
        var mapped = normalizedSources.SelectMany(source => PatchInputMapper.MapCandidates([source.RootPath]).Select(entry => (Source: source, Entry: entry)))
            .GroupBy(item => item.Entry.ArchivePath, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        var result = new List<ClientFusionEntry>(mapped.Length); var done = 0;
        foreach (var group in mapped)
        {
            cancellationToken.ThrowIfCancellationRequested(); progress?.Report((done++, mapped.Length, group.Key));
            baseFiles.TryGetValue(group.Key, out var baseFile);
            var repeatedSources = group.GroupBy(item => item.Source.Name, StringComparer.OrdinalIgnoreCase).Where(sourceGroup => sourceGroup.Count() > 1).Select(sourceGroup => sourceGroup.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rawCandidates = group.Select(item => new ClientFusionCandidate(
                repeatedSources.Contains(item.Source.Name) ? $"{item.Source.Name} · {Path.GetRelativePath(item.Source.RootPath, item.Entry.SourcePath)}" : item.Source.Name,
                item.Source.RootPath, item.Entry.SourcePath, new FileInfo(item.Entry.SourcePath).Length, null)).ToArray();
            var candidates = rawCandidates.Length == 1 ? rawCandidates : rawCandidates
                .Select(candidate => candidate with { Sha256 = Hash(candidate.FilePath) })
                .GroupBy(candidate => candidate.Sha256!, StringComparer.OrdinalIgnoreCase).Select(hashGroup => hashGroup.First()).ToArray();
            var matchesBase = baseFile is not null && candidates.Length == 1 && FilesMatch(baseFile, candidates[0]);
            var status = matchesBase ? ClientFusionStatus.IdenticalToBase
                : candidates.Length > 1 ? ClientFusionStatus.Conflict
                : group.Count() > 1 ? ClientFusionStatus.IdenticalCandidates
                : baseFile is null ? ClientFusionStatus.Added : ClientFusionStatus.Override;
            result.Add(new(group.Key, status, baseFile, candidates, Guidance(group.Key, status)));
        }
        progress?.Report((mapped.Length, mapped.Length, "Complete"));
        return new(1, DateTimeOffset.UtcNow, baseRoot, normalizedSources, result);
    }

    public static void Save(string path, ClientFusionPlan plan)
    {
        path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }

    public static ClientFusionPlan Load(string path)
    {
        var plan = JsonSerializer.Deserialize<ClientFusionPlan>(File.ReadAllText(path)) ?? throw new InvalidDataException("Client fusion plan is empty.");
        if (plan.FormatVersion != 1) throw new InvalidDataException($"Unsupported client fusion plan version {plan.FormatVersion}."); return plan;
    }

    public static ClientFusionStageResult Stage(string rootPath, ClientFusionPlan plan, IReadOnlyDictionary<string, string>? conflictSelections = null, ClientFusionDbcResult? dbcResult = null)
    {
        if (dbcResult is not null)
        {
            ClientFusionDbcService.VerifyResult(dbcResult); if (JsonSerializer.Serialize(dbcResult.Plan.FusionPlan) != JsonSerializer.Serialize(plan)) throw new InvalidDataException("The merged DBC receipt belongs to a different client fusion plan.");
        }
        rootPath = Path.GetFullPath(rootPath); var filesRoot = Path.Combine(rootPath, "files"); Directory.CreateDirectory(filesRoot);
        var entries = new List<PatchEntry>(); var unresolved = 0; var skipped = 0;
        foreach (var entry in plan.Entries)
        {
            if (entry.Status == ClientFusionStatus.IdenticalToBase) { skipped++; continue; }
            if (dbcResult is not null && dbcResult.Plan.Tables.Any(table => table.ArchivePath.Equals(entry.ArchivePath, StringComparison.OrdinalIgnoreCase)))
            {
                if (dbcResult.BlockedArchivePaths.Contains(entry.ArchivePath, StringComparer.OrdinalIgnoreCase)) { unresolved++; continue; }
                if (dbcResult.OmittedArchivePaths.Contains(entry.ArchivePath, StringComparer.OrdinalIgnoreCase)) { skipped++; continue; }
                if (!dbcResult.OutputFiles.TryGetValue(entry.ArchivePath, out var merged)) throw new InvalidDataException($"DBC fusion receipt has no resolved output for {entry.ArchivePath}.");
                var mergedDestination = Path.Combine(filesRoot, entry.ArchivePath.Replace('\\', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(mergedDestination)!); File.Copy(merged, mergedDestination, true); entries.Add(new(mergedDestination, entry.ArchivePath)); continue;
            }
            ClientFusionCandidate? candidate;
            if (entry.Status == ClientFusionStatus.Conflict)
            {
                var selected = conflictSelections?.GetValueOrDefault(entry.ArchivePath);
                candidate = entry.Candidates.FirstOrDefault(value => value.FilePath.Equals(selected, StringComparison.OrdinalIgnoreCase));
                if (candidate is null) { unresolved++; continue; }
            }
            else candidate = entry.Candidates.Single();
            var destination = Path.Combine(filesRoot, entry.ArchivePath.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(candidate.FilePath, destination, true);
            entries.Add(new(destination, entry.ArchivePath));
        }
        if (entries.Count == 0) throw new InvalidOperationException("The fusion plan has no resolved changes to stage.");
        var manifest = Path.Combine(rootPath, "fusion.crucible-patch.json");
        PatchManifestService.Save(manifest, "Client fusion patch", "patch-Crucible-Fusion.MPQ", entries, policy: new(ExpectedEntryCount: entries.Count));
        return new(rootPath, manifest, entries.Count, skipped, unresolved);
    }

    private static string Guidance(string path, ClientFusionStatus status)
    {
        var dbc = path.StartsWith("DBFilesClient\\", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(path).Equals(".dbc", StringComparison.OrdinalIgnoreCase);
        return status switch
        {
            ClientFusionStatus.IdenticalToBase => "Same bytes as the selected base; omitted from the patch.",
            ClientFusionStatus.Added => "New path supplied by one source; safe to stage after compatibility review.",
            ClientFusionStatus.Override => dbc ? "DBC differs from base. Prefer adding new records or allocating/remapping IDs; replace existing records only by explicit review." : "Source uses a base path. Preserve both by renaming/repointing when possible; otherwise review the replacement explicitly.",
            ClientFusionStatus.IdenticalCandidates => "Multiple sources supply identical bytes; one deduplicated copy will be staged.",
            ClientFusionStatus.Conflict when dbc => "Different DBCs target the same path. Add/remap compatible records into one merged table; do not choose a whole-file winner or rely on load order.",
            _ => "Different files target the same client path. Prefer renaming the imported asset and repointing its references; selecting one source is an explicit replacement, not additive fusion."
        };
    }

    private static bool FilesMatch(string baseFile, ClientFusionCandidate candidate)
    {
        if (new FileInfo(baseFile).Length != candidate.Size) return false;
        using var left = new FileStream(baseFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using var right = new FileStream(candidate.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var leftBuffer = new byte[1 << 20]; var rightBuffer = new byte[1 << 20];
        while (true)
        {
            var leftRead = left.Read(leftBuffer); var rightRead = right.Read(rightBuffer);
            if (leftRead != rightRead) return false;
            if (leftRead == 0) return true;
            if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead))) return false;
        }
    }
    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
