using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public enum CompatibilityLabClonePreparationState { Created, Resumed, VerifiedExisting, Failed }

public sealed record CompatibilityLabClonePreparationEntry(
    string Name,
    string SourceRoot,
    string CloneRoot,
    string PartialRoot,
    CompatibilityLabClonePreparationState State,
    int CopiedFiles,
    long CopiedBytes,
    int ReusedFiles,
    long ReusedBytes,
    int RemovedStaleFiles,
    CompatibilityLabCloneAudit? Audit,
    IReadOnlyList<string> Errors)
{
    public bool Passed => State != CompatibilityLabClonePreparationState.Failed && Errors.Count == 0 && Audit?.Passed == true;
}

public sealed record CompatibilityLabClonePreparationReport(
    int FormatVersion,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    string ReportRoot,
    IReadOnlyList<CompatibilityLabClonePreparationEntry> Entries,
    IReadOnlyList<string> Errors,
    string JsonReportPath,
    string MarkdownReportPath)
{
    public bool Passed => Errors.Count == 0 && Entries.Count > 0 && Entries.All(entry => entry.Passed);
}

public static partial class CompatibilityLabService
{
    private const int ClonePreparationFormatVersion = 1;
    private const int CopyBufferBytes = 4 * 1024 * 1024;

    private sealed record CompatibilityCloneResumeMarker(
        int FormatVersion,
        string SourceRoot,
        string CloneRoot,
        IReadOnlyList<string> ExcludedDirectoryNames,
        IReadOnlyList<string>? ExcludedFilePaths = null);

    public static CompatibilityLabClonePreparationReport PrepareClones(
        CompatibilityLabRequest request,
        IProgress<CompatibilityLabProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var started = DateTimeOffset.UtcNow;
        var outputRoot = Path.GetFullPath(request.OutputRoot);
        Directory.CreateDirectory(outputRoot);
        var reportRoot = Path.Combine(outputRoot, "clone-preparations", $"clone-{started:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(reportRoot);

        var pairs = request.Lanes.SelectMany(lane => lane.ClonePairs.Select(pair =>
            (Name: $"{lane.Name}: {pair.Name}", Pair: pair))).ToArray();
        ValidateCloneDestinations(pairs);

        var entries = new List<CompatibilityLabClonePreparationEntry>(pairs.Length);
        var errors = new List<string>();
        foreach (var item in pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                entries.Add(PrepareClone(item.Name, item.Pair, request.HashWorkers, progress, cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var source = SafeFullPath(item.Pair.SourceRoot);
                var clone = SafeFullPath(item.Pair.CloneRoot);
                var message = $"{item.Name}: {exception.Message}";
                errors.Add(message);
                entries.Add(new(item.Name, source, clone, PartialRoot(clone), CompatibilityLabClonePreparationState.Failed,
                    0, 0, 0, 0, 0, null, [exception.Message]));
            }
        }

        var jsonPath = Path.Combine(reportRoot, "clone-preparation-report.json");
        var markdownPath = Path.Combine(reportRoot, "clone-preparation-report.md");
        var report = new CompatibilityLabClonePreparationReport(ClonePreparationFormatVersion, started, DateTimeOffset.UtcNow,
            reportRoot, entries, errors, jsonPath, markdownPath);
        AtomicWrite(jsonPath, JsonSerializer.Serialize(report, JsonOptions));
        AtomicWrite(markdownPath, RenderClonePreparationMarkdown(report));
        progress?.Report(new("Clone preparation complete", 1, 1, reportRoot));
        return report;
    }

    private static CompatibilityLabClonePreparationEntry PrepareClone(
        string name,
        CompatibilityLabClonePair pair,
        int hashWorkers,
        IProgress<CompatibilityLabProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sourceRoot = RequiredDirectory(pair.SourceRoot, $"{name} source");
        var cloneRoot = Path.GetFullPath(pair.CloneRoot ?? string.Empty);
        var excludedDirectories = NormalizeExcludedDirectoryNames(pair.ExcludedDirectoryNames);
        var excludedFiles = NormalizeExcludedFilePaths(pair.ExcludedFilePaths);
        ValidateDisjointRoots(sourceRoot, cloneRoot, name);
        var partialRoot = PartialRoot(cloneRoot);
        ValidateDisjointRoots(sourceRoot, partialRoot, name);

        if (Directory.Exists(cloneRoot))
        {
            var existingAudit = AuditClone(name, pair with { SourceRoot = sourceRoot, CloneRoot = cloneRoot }, hashWorkers, progress, cancellationToken);
            return existingAudit.Passed
                ? new(name, sourceRoot, cloneRoot, partialRoot, CompatibilityLabClonePreparationState.VerifiedExisting,
                    0, 0, existingAudit.CloneFiles, existingAudit.CloneBytes, 0, existingAudit, [])
                : new(name, sourceRoot, cloneRoot, partialRoot, CompatibilityLabClonePreparationState.Failed,
                    0, 0, 0, 0, 0, existingAudit, ["The completed clone failed identity verification and was not modified. Select a new clone root or review the reported drift."]);
        }
        if (File.Exists(cloneRoot)) throw new IOException($"Clone destination is an existing file: {cloneRoot}");

        var sourceFiles = FileMap(sourceRoot, excludedDirectories, excludedFiles);
        var sourceBytes = sourceFiles.Values.Sum(file => file.Length);
        EnsureCloneCapacity(cloneRoot, sourceBytes);
        var markerPath = MarkerPath(partialRoot);
        var resumed = Directory.Exists(partialRoot) || File.Exists(markerPath);
        var expectedMarker = new CompatibilityCloneResumeMarker(
            ClonePreparationFormatVersion,
            sourceRoot,
            cloneRoot,
            excludedDirectories,
            excludedFiles);
        if (resumed)
        {
            if (!File.Exists(markerPath))
                throw new InvalidDataException($"Partial clone exists without a Crucible ownership marker and will not be modified: {partialRoot}");
            var marker = JsonSerializer.Deserialize<CompatibilityCloneResumeMarker>(File.ReadAllText(markerPath), JsonOptions)
                ?? throw new InvalidDataException($"Partial clone marker is empty: {markerPath}");
            if (marker.FormatVersion != expectedMarker.FormatVersion ||
                !PathEquals(marker.SourceRoot, expectedMarker.SourceRoot) ||
                !PathEquals(marker.CloneRoot, expectedMarker.CloneRoot) ||
                !NormalizeExcludedDirectoryNames(marker.ExcludedDirectoryNames).SequenceEqual(expectedMarker.ExcludedDirectoryNames, StringComparer.OrdinalIgnoreCase) ||
                !NormalizeExcludedFilePaths(marker.ExcludedFilePaths).SequenceEqual(expectedMarker.ExcludedFilePaths!, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException($"Partial clone marker does not match the current source, destination, or exclusions: {markerPath}");
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cloneRoot)!);
            AtomicWrite(markerPath, JsonSerializer.Serialize(expectedMarker, JsonOptions));
        }
        Directory.CreateDirectory(partialRoot);

        var partialFiles = FileMap(partialRoot, [], []);
        var stale = partialFiles.Keys.Except(sourceFiles.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var relative in stale)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stalePath = partialFiles[relative].FullName;
            EnsureInsideClone(partialRoot, stalePath);
            File.SetAttributes(stalePath, FileAttributes.Normal);
            File.Delete(stalePath);
        }
        RemoveEmptyCloneDirectories(partialRoot);

        var copiedFiles = 0;
        var copiedBytes = 0L;
        var reusedFiles = 0;
        var reusedBytes = 0L;
        var completedBytes = 0L;
        var timer = Stopwatch.StartNew();
        foreach (var relative in sourceFiles.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = sourceFiles[relative];
            var destination = Path.GetFullPath(Path.Combine(partialRoot, relative));
            EnsureInsideClone(partialRoot, destination);
            if (File.Exists(destination) && new FileInfo(destination).Length == source.Length && Hash(source.FullName) == Hash(destination))
            {
                reusedFiles++;
                reusedBytes += source.Length;
                completedBytes += source.Length;
                ReportCopyProgress(progress, name, completedBytes, sourceBytes, relative, timer, force: false);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            CopyCloneFile(source, destination, bytes =>
            {
                completedBytes += bytes;
                ReportCopyProgress(progress, name, completedBytes, sourceBytes, relative, timer, force: false);
            }, cancellationToken);
            copiedFiles++;
            copiedBytes += source.Length;
        }
        ReportCopyProgress(progress, name, completedBytes, sourceBytes, sourceRoot, timer, force: true);

        var partialPair = pair with
        {
            SourceRoot = sourceRoot,
            CloneRoot = partialRoot,
            ExcludedDirectoryNames = excludedDirectories,
            ExcludedFilePaths = excludedFiles
        };
        var audit = AuditClone(name, partialPair, hashWorkers, progress, cancellationToken);
        if (!audit.Passed)
            return new(name, sourceRoot, cloneRoot, partialRoot, CompatibilityLabClonePreparationState.Failed,
                copiedFiles, copiedBytes, reusedFiles, reusedBytes, stale.Length, audit,
                ["The prepared bytes failed source-to-clone identity verification. The marked partial tree was retained for diagnosis and resumption."]);

        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(cloneRoot) || File.Exists(cloneRoot))
            throw new IOException($"Clone destination appeared during preparation and was not overwritten: {cloneRoot}");
        Directory.Move(partialRoot, cloneRoot);
        File.Delete(markerPath);
        return new(name, sourceRoot, cloneRoot, partialRoot,
            resumed ? CompatibilityLabClonePreparationState.Resumed : CompatibilityLabClonePreparationState.Created,
            copiedFiles, copiedBytes, reusedFiles, reusedBytes, stale.Length, audit with { CloneRoot = cloneRoot }, []);
    }

    private static void CopyCloneFile(FileInfo source, string destination, Action<int> copied, CancellationToken cancellationToken)
    {
        var temporary = destination + $".{Environment.ProcessId}.{Guid.NewGuid():N}.copying";
        try
        {
            using (var input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes, FileOptions.SequentialScan))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferBytes, FileOptions.SequentialScan))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
                try
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var read = input.Read(buffer, 0, buffer.Length);
                        if (read == 0) break;
                        output.Write(buffer, 0, read);
                        copied(read);
                    }
                    output.Flush(true);
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
            File.Move(temporary, destination, true);
            File.SetLastWriteTimeUtc(destination, source.LastWriteTimeUtc);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string[] NormalizeExcludedDirectoryNames(IReadOnlyList<string>? values)
    {
        var result = (values ?? []).Select(value => value?.Trim() ?? string.Empty)
            .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (result.Any(value => value is "." or ".." || value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0))
            throw new InvalidDataException("Clone exclusions must be individual directory names, not relative or absolute paths.");
        return result;
    }

    private static string[] NormalizeExcludedFilePaths(IReadOnlyList<string>? values)
    {
        var normalized = new List<string>();
        foreach (var raw in values ?? [])
        {
            var value = raw?.Trim() ?? string.Empty;
            if (value.Length == 0) continue;
            if (Path.IsPathRooted(value) || value.StartsWith('\\') || value.StartsWith('/'))
                throw new InvalidDataException("Excluded clone files must be exact source-relative paths, not absolute paths.");

            var segments = value.Replace('\\', '/').Split('/');
            if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                throw new InvalidDataException($"Excluded clone file path is invalid or escapes its source root: {value}");
            normalized.Add(string.Join(Path.DirectorySeparatorChar, segments));
        }
        return normalized.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void ValidateCloneDestinations(IReadOnlyList<(string Name, CompatibilityLabClonePair Pair)> pairs)
    {
        var resolved = pairs.Select(item => (
            item.Name,
            SourceRoot: Path.GetFullPath(item.Pair.SourceRoot),
            CloneRoot: Path.GetFullPath(item.Pair.CloneRoot))).ToArray();
        var duplicate = resolved.GroupBy(item => item.CloneRoot, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"Compatibility clone destination is reused by multiple request pairs: {duplicate.Key}");
        for (var left = 0; left < resolved.Length; left++)
        for (var right = left + 1; right < resolved.Length; right++)
        {
            var leftRoot = resolved[left].CloneRoot;
            var rightRoot = resolved[right].CloneRoot;
            if (ContainsPath(leftRoot, rightRoot) || ContainsPath(rightRoot, leftRoot))
                throw new InvalidDataException($"Compatibility clone destinations overlap: {leftRoot} and {rightRoot}");
        }
        foreach (var destination in resolved)
        foreach (var source in resolved)
        {
            var partialRoot = PartialRoot(destination.CloneRoot);
            if (ContainsPath(source.SourceRoot, destination.CloneRoot) || ContainsPath(destination.CloneRoot, source.SourceRoot) ||
                ContainsPath(source.SourceRoot, partialRoot) || ContainsPath(partialRoot, source.SourceRoot))
                throw new InvalidDataException($"Compatibility source and clone trees overlap across request pairs: {source.SourceRoot} / {destination.CloneRoot}");
        }
    }

    private static void ValidateDisjointRoots(string sourceRoot, string cloneRoot, string name)
    {
        if (ContainsPath(sourceRoot, cloneRoot) || ContainsPath(cloneRoot, sourceRoot))
            throw new InvalidDataException($"{name} source and clone roots overlap: {sourceRoot} / {cloneRoot}");
    }

    private static bool ContainsPath(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullCandidate = Path.GetFullPath(candidate);
        if (!string.Equals(Path.GetPathRoot(fullRoot), Path.GetPathRoot(fullCandidate), StringComparison.OrdinalIgnoreCase))
            return false;
        var relative = Path.GetRelativePath(fullRoot, fullCandidate);
        return !Path.IsPathRooted(relative) &&
            (relative == "." || (relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)));
    }

    private static bool PathEquals(string left, string right) =>
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static void EnsureInsideClone(string root, string path)
    {
        if (!ContainsPath(root, path) || PathEquals(root, path))
            throw new InvalidDataException($"Resolved clone path escapes its owned partial root: {path}");
    }

    private static void RemoveEmptyCloneDirectories(string partialRoot)
    {
        foreach (var directory in Directory.EnumerateDirectories(partialRoot, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            EnsureInsideClone(partialRoot, directory);
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }

    private static void EnsureCloneCapacity(string cloneRoot, long sourceBytes)
    {
        var root = Path.GetPathRoot(cloneRoot);
        if (string.IsNullOrWhiteSpace(root) || root.StartsWith("\\\\", StringComparison.Ordinal)) return;
        var drive = new DriveInfo(root);
        if (!drive.IsReady) throw new IOException($"Clone destination drive is not ready: {root}");
        var reserve = Math.Max(1L << 30, sourceBytes / 20);
        if (drive.AvailableFreeSpace < sourceBytes + reserve)
            throw new IOException($"Clone requires up to {sourceBytes:N0} bytes plus {reserve:N0} bytes reserve, but {drive.AvailableFreeSpace:N0} bytes are free on {root}");
    }

    private static void ReportCopyProgress(IProgress<CompatibilityLabProgress>? progress, string name, long completed,
        long total, string currentPath, Stopwatch timer, bool force)
    {
        if (!force && completed != CopyBufferBytes && timer.ElapsedMilliseconds < 250) return;
        timer.Restart();
        progress?.Report(new($"{name}: clone copy bytes", completed, total, currentPath));
    }

    private static string PartialRoot(string cloneRoot) => cloneRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".crucible-partial";
    private static string MarkerPath(string partialRoot) => partialRoot + ".json";
    private static string SafeFullPath(string? path)
    {
        try { return Path.GetFullPath(path ?? string.Empty); }
        catch { return path ?? string.Empty; }
    }

    private static string RenderClonePreparationMarkdown(CompatibilityLabClonePreparationReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("# WoW Crucible Compatibility Clone Preparation").AppendLine();
        text.AppendLine($"- Result: **{(report.Passed ? "PASS" : "FAIL")}**");
        text.AppendLine($"- Started UTC: {report.StartedUtc:O}");
        text.AppendLine($"- Completed UTC: {report.CompletedUtc:O}");
        text.AppendLine($"- Report root: `{report.ReportRoot}`").AppendLine();
        text.AppendLine("| Pair | State | Copied files | Copied bytes | Reused files | Removed stale | Identity audit |");
        text.AppendLine("|---|---|---:|---:|---:|---:|---|");
        foreach (var entry in report.Entries)
            text.AppendLine($"| {entry.Name} | {entry.State} | {entry.CopiedFiles:N0} | {entry.CopiedBytes:N0} | {entry.ReusedFiles:N0} | {entry.RemovedStaleFiles:N0} | {(entry.Audit?.Passed == true ? "PASS" : "FAIL")} |");
        var errors = report.Errors.Concat(report.Entries.SelectMany(entry => entry.Errors.Select(error => $"{entry.Name}: {error}"))).Distinct(StringComparer.Ordinal).ToArray();
        if (errors.Length > 0)
        {
            text.AppendLine().AppendLine("## Errors").AppendLine();
            foreach (var error in errors) text.AppendLine($"- {error}");
        }
        return text.ToString();
    }
}
