using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public sealed record ProcessedAssetLayer(string Provenance, int Precedence);
public sealed record ProcessedAssetLayerFile(string LogicalPath, string Provenance, int Precedence, string SourcePath, long Bytes);
public sealed record ProcessedAssetLayerConflict(string LogicalPath, int Precedence, IReadOnlyList<ProcessedAssetLayerFile> Candidates, string? SelectedProvenance = null);
public sealed record ProcessedAssetLayerMergeProgress(long CompletedFiles, long TotalFiles, long CompletedBytes, long TotalBytes, string CurrentPath);
public sealed record ProcessedAssetLayerMergePlan(
    string LibraryRoot,
    string DestinationRoot,
    IReadOnlyList<ProcessedAssetLayer> Layers,
    long CatalogRows,
    IReadOnlyList<ProcessedAssetLayerFile> Winners,
    IReadOnlyList<ProcessedAssetLayerConflict> Conflicts,
    IReadOnlyList<string> MissingSources,
    long OutputBytes,
    long OverriddenCandidates,
    long EqualPrecedenceDuplicates)
{
    public bool Complete => Conflicts.All(conflict => conflict.SelectedProvenance is not null) && MissingSources.Count == 0;
}
public sealed record ProcessedAssetLayerAppliedFile(string LogicalPath, string Provenance, string DestinationPath, long Bytes, string Sha256);
public sealed record ProcessedAssetLayerMergeResult(
    ProcessedAssetLayerMergePlan Plan,
    string ContentRoot,
    string ConflictRoot,
    string ManifestPath,
    IReadOnlyList<ProcessedAssetLayerAppliedFile> Files,
    long ConflictFiles);

public sealed class ProcessedAssetLayerMergeService
{
    private const string CatalogName = "asset-catalog.csv";

    public ProcessedAssetLayerMergePlan Analyze(string libraryRoot, string destinationRoot, IReadOnlyList<ProcessedAssetLayer> layers,
        IReadOnlyDictionary<string, string>? conflictResolutions = null, CancellationToken cancellationToken = default)
    {
        libraryRoot = RequiredDirectory(libraryRoot, "Processed asset library");
        destinationRoot = Path.GetFullPath(destinationRoot);
        if (layers.Count == 0) throw new ArgumentException("At least one layer is required.", nameof(layers));
        var selected = layers.GroupBy(layer => CleanProvenance(layer.Provenance), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group =>
            {
                var values = group.Select(value => value.Precedence).Distinct().ToArray();
                if (values.Length != 1) throw new InvalidDataException($"Layer '{group.Key}' was assigned multiple precedence values.");
                return new ProcessedAssetLayer(group.Key, values[0]);
            }, StringComparer.OrdinalIgnoreCase);
        var catalogPath = Path.Combine(libraryRoot, CatalogName);
        if (!File.Exists(catalogPath)) throw new FileNotFoundException("The processed asset catalog does not exist.", catalogPath);

        var candidates = new Dictionary<string, List<ProcessedAssetLayerFile>>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        long catalogRows = 0;
        using (var reader = new StreamReader(catalogPath, Encoding.UTF8, true, 1024 * 1024))
        {
            var header = reader.ReadLine() ?? throw new InvalidDataException("The asset catalog is empty.");
            var columns = AssetComparisonAggregateCache.ParseCsv(header);
            if (columns.Count < 5 || !columns[2].Equals("source", StringComparison.OrdinalIgnoreCase) || !columns[3].Equals("relative_path", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The asset catalog header is not recognized.");
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                catalogRows++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                var fields = AssetComparisonAggregateCache.ParseCsv(line);
                if (fields.Count < 5 || !selected.TryGetValue(fields[2], out var layer)) continue;
                if (!long.TryParse(fields[4], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var bytes) || bytes < 0)
                    throw new InvalidDataException($"Catalog row {catalogRows + 1:N0} has an invalid byte length.");
                var sourcePath = SafeCombine(libraryRoot, fields[3]);
                var logicalPath = RemoveProvenance(fields[3], fields[2]);
                if (!File.Exists(sourcePath))
                {
                    missing.Add($"{fields[2]} :: {logicalPath} :: {sourcePath}");
                    continue;
                }
                var info = new FileInfo(sourcePath);
                if (info.Length != bytes)
                {
                    missing.Add($"{fields[2]} :: {logicalPath} :: catalog bytes {bytes}, current bytes {info.Length}");
                    continue;
                }
                if (!candidates.TryGetValue(logicalPath, out var values)) candidates[logicalPath] = values = [];
                if (values.Any(value => value.Provenance.Equals(layer.Provenance, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException($"Layer '{layer.Provenance}' contains duplicate logical path '{logicalPath}'.");
                values.Add(new(logicalPath, layer.Provenance, layer.Precedence, sourcePath, bytes));
            }
        }

        var winners = new List<ProcessedAssetLayerFile>(candidates.Count);
        var conflicts = new List<ProcessedAssetLayerConflict>();
        long overridden = 0;
        long equalDuplicates = 0;
        foreach (var pair in candidates.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var maximum = pair.Value.Max(value => value.Precedence);
            var effective = pair.Value.Where(value => value.Precedence == maximum).OrderBy(value => value.Provenance, StringComparer.OrdinalIgnoreCase).ToArray();
            overridden += pair.Value.Count - effective.Length;
            if (effective.Length == 1) { winners.Add(effective[0]); continue; }
            if (AllIdentical(effective, cancellationToken))
            {
                winners.Add(effective[0]);
                equalDuplicates += effective.Length - 1;
            }
            else
            {
                var requested = conflictResolutions?.FirstOrDefault(resolution => resolution.Key.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)).Value;
                var selectedCandidate = requested is null ? null : effective.FirstOrDefault(value => value.Provenance.Equals(requested, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"Conflict resolution for '{pair.Key}' selects unavailable provenance '{requested}'.");
                if (selectedCandidate is not null) winners.Add(selectedCandidate);
                conflicts.Add(new(pair.Key, maximum, effective, selectedCandidate?.Provenance));
            }
        }
        var outputBytes = winners.Sum(value => value.Bytes) + conflicts.Sum(conflict => conflict.Candidates.Sum(value => value.Bytes));
        return new(libraryRoot, destinationRoot, selected.Values.OrderBy(value => value.Precedence).ThenBy(value => value.Provenance, StringComparer.OrdinalIgnoreCase).ToArray(),
            catalogRows, winners, conflicts, missing, outputBytes, overridden, equalDuplicates);
    }

    public ProcessedAssetLayerMergeResult Apply(ProcessedAssetLayerMergePlan plan, IProgress<ProcessedAssetLayerMergeProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!plan.Complete)
            throw new InvalidOperationException($"The layer merge is incomplete: {plan.Conflicts.Count(conflict => conflict.SelectedProvenance is null):N0} unresolved conflict(s), {plan.MissingSources.Count:N0} missing or changed source(s). Review those findings before applying.");
        if (Directory.Exists(plan.DestinationRoot) || File.Exists(plan.DestinationRoot))
            throw new IOException($"The HD destination already exists: {plan.DestinationRoot}. Crucible will not merge into or replace an unverified directory.");
        var parent = Path.GetDirectoryName(plan.DestinationRoot) ?? throw new InvalidDataException("The HD destination has no parent directory.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileName(plan.DestinationRoot)}.staging-{Guid.NewGuid():N}");
        var contentRoot = Path.Combine(staging, "Content");
        var conflictRoot = Path.Combine(staging, "Conflicts", "Equal-Precedence");
        var metadataRoot = Path.Combine(staging, "_meta");
        Directory.CreateDirectory(contentRoot);
        Directory.CreateDirectory(metadataRoot);
        var applied = new List<ProcessedAssetLayerAppliedFile>(plan.Winners.Count);
        var totalFiles = plan.Winners.Count + plan.Conflicts.Sum(value => value.Candidates.Count);
        long completedFiles = 0, completedBytes = 0, conflictFiles = 0;
        try
        {
            foreach (var winner in plan.Winners)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = SafeCombine(contentRoot, winner.LogicalPath);
                var hash = CopyAndHash(winner.SourcePath, destination, winner.Bytes, cancellationToken);
                applied.Add(new(winner.LogicalPath, winner.Provenance, Path.Combine(plan.DestinationRoot, "Content", winner.LogicalPath), winner.Bytes, hash));
                completedFiles++; completedBytes += winner.Bytes;
                if (completedFiles % 250 == 0 || completedFiles == totalFiles) progress?.Report(new(completedFiles, totalFiles, completedBytes, plan.OutputBytes, winner.LogicalPath));
            }
            foreach (var conflict in plan.Conflicts)
            foreach (var candidate in conflict.Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = Path.GetDirectoryName(conflict.LogicalPath) ?? string.Empty;
                var relative = Path.Combine(directory, candidate.Provenance, Path.GetFileName(conflict.LogicalPath));
                var destination = SafeCombine(conflictRoot, relative);
                _ = CopyAndHash(candidate.SourcePath, destination, candidate.Bytes, cancellationToken);
                completedFiles++; conflictFiles++; completedBytes += candidate.Bytes;
                if (completedFiles % 250 == 0 || completedFiles == totalFiles) progress?.Report(new(completedFiles, totalFiles, completedBytes, plan.OutputBytes, $"CONFLICT :: {conflict.LogicalPath}"));
            }

            var manifestPath = Path.Combine(metadataRoot, "layer-merge.json");
            var result = new ProcessedAssetLayerMergeResult(plan, Path.Combine(plan.DestinationRoot, "Content"), Path.Combine(plan.DestinationRoot, "Conflicts", "Equal-Precedence"),
                Path.Combine(plan.DestinationRoot, "_meta", "layer-merge.json"), applied, conflictFiles);
            using (var stream = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
                JsonSerializer.Serialize(stream, result, new JsonSerializerOptions { WriteIndented = true });
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, plan.DestinationRoot);
            return result;
        }
        catch
        {
            // Preserve the uniquely named staging directory. It contains only newly copied data
            // and is safer forensic/recovery evidence than silently deleting a multi-gigabyte partial result.
            throw;
        }
    }

    private static bool AllIdentical(IReadOnlyList<ProcessedAssetLayerFile> candidates, CancellationToken cancellationToken)
    {
        var first = candidates[0];
        for (var index = 1; index < candidates.Count; index++)
        {
            if (candidates[index].Bytes != first.Bytes || !AssetComparisonService.FilesAreIdentical(first.SourcePath, candidates[index].SourcePath, cancellationToken))
                return false;
        }
        return true;
    }

    private static string CopyAndHash(string source, string destination, long expectedBytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        if (input.Length != expectedBytes) throw new IOException($"Source changed after review: {source}");
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long copied = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = input.Read(buffer);
            if (read == 0) break;
            output.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
            copied += read;
        }
        output.Flush(true);
        if (copied != expectedBytes) throw new EndOfStreamException($"Source changed while copying: {source}");
        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string RemoveProvenance(string catalogRelativePath, string provenance)
    {
        var parts = catalogRelativePath.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4 || !parts[0].Equals("Archives", StringComparison.OrdinalIgnoreCase) || !parts[1].Equals("Content", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Catalog path is not content-first: {catalogRelativePath}");
        var index = parts.Length - 2;
        if (index < 2 || !parts[index].Equals(provenance, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Catalog path does not end in provenance leaf '{provenance}' before its file: {catalogRelativePath}");
        var logical = parts.Skip(2).Where((_, partIndex) => partIndex != index - 2).ToArray();
        if (logical.Length == 0 || logical.Any(part => part is "." or "..")) throw new InvalidDataException($"Unsafe logical path in catalog: {catalogRelativePath}");
        return Path.Combine(logical);
    }

    private static string SafeCombine(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(fullRoot, relative));
        var check = Path.GetRelativePath(fullRoot, path);
        if (check == ".." || check.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(check))
            throw new InvalidDataException($"Asset path escapes its root: {relative}");
        return path;
    }

    private static string RequiredDirectory(string path, string label)
    {
        var full = Path.GetFullPath(path);
        return Directory.Exists(full) ? full : throw new DirectoryNotFoundException($"{label} does not exist: {full}");
    }

    private static string CleanProvenance(string value)
    {
        var clean = value.Trim();
        if (clean.Length == 0 || clean is "." or ".." || clean.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException($"Invalid provenance name: '{value}'.");
        return clean;
    }
}
