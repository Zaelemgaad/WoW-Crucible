using System.Security.Cryptography;
using System.Text.Json;

namespace WoWCrucible.Core;

public sealed record ProcessedAssetPreviewPrunePlan(
    string LayerRoot,
    string ContentRoot,
    string LayerManifestPath,
    string LayerManifestSha256,
    long PreviewFiles,
    long PreviewBytes,
    long RemainingFiles,
    long RemainingBytes);

public sealed record ProcessedAssetPreviewPruneEntry(string RelativePath, long Bytes);

public sealed record ProcessedAssetPreviewPruneReceipt(
    string Format,
    string State,
    DateTimeOffset PlannedUtc,
    DateTimeOffset? CompletedUtc,
    ProcessedAssetPreviewPrunePlan Plan,
    IReadOnlyList<ProcessedAssetPreviewPruneEntry> RemovedFiles);

public sealed record ProcessedAssetPreviewPruneProgress(long CompletedFiles, long TotalFiles, long FreedBytes, long TotalBytes, string CurrentPath);

public sealed class ProcessedAssetPreviewPruneService
{
    private static readonly EnumerationOptions RecursiveFiles = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    public ProcessedAssetPreviewPrunePlan Analyze(string layerRoot, CancellationToken cancellationToken = default)
    {
        layerRoot = Path.GetFullPath(layerRoot);
        if (!Directory.Exists(layerRoot)) throw new DirectoryNotFoundException($"Published layer directory does not exist: {layerRoot}");
        var contentRoot = Path.Combine(layerRoot, "Content");
        var manifestPath = Path.Combine(layerRoot, "_meta", "layer-merge.json");
        if (!Directory.Exists(contentRoot)) throw new DirectoryNotFoundException($"Published layer Content directory does not exist: {contentRoot}");
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("Refusing to prune a directory that is not a Crucible-published layer. Its layer merge manifest is missing.", manifestPath);

        long previewFiles = 0, previewBytes = 0, remainingFiles = 0, remainingBytes = 0;
        foreach (var path in Directory.EnumerateFiles(contentRoot, "*", RecursiveFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureInside(contentRoot, path);
            var bytes = new FileInfo(path).Length;
            if (Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                previewFiles++;
                previewBytes = checked(previewBytes + bytes);
            }
            else
            {
                remainingFiles++;
                remainingBytes = checked(remainingBytes + bytes);
            }
        }

        using var manifest = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        var manifestSha256 = Convert.ToHexString(SHA256.HashData(manifest)).ToLowerInvariant();
        return new(layerRoot, contentRoot, manifestPath, manifestSha256, previewFiles, previewBytes, remainingFiles, remainingBytes);
    }

    public ProcessedAssetPreviewPruneReceipt Apply(ProcessedAssetPreviewPrunePlan reviewedPlan,
        IProgress<ProcessedAssetPreviewPruneProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var current = Analyze(reviewedPlan.LayerRoot, cancellationToken);
        if (current != reviewedPlan)
            throw new IOException("The published layer changed after preview-prune review. Run the dry run again before applying.");

        var metadataRoot = Path.Combine(current.LayerRoot, "_meta");
        var pendingPath = Path.Combine(metadataRoot, "preview-prune.pending.json");
        var receiptPath = Path.Combine(metadataRoot, "preview-prune.json");
        if (File.Exists(pendingPath))
            throw new IOException($"An unfinished preview-prune journal already exists: {pendingPath}");
        if (File.Exists(receiptPath))
            throw new IOException($"This layer already has a completed preview-prune receipt: {receiptPath}");

        var entries = Directory.EnumerateFiles(current.ContentRoot, "*", RecursiveFiles)
            .Where(path => Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
            .Select(path =>
            {
                EnsureInside(current.ContentRoot, path);
                return new ProcessedAssetPreviewPruneEntry(Path.GetRelativePath(current.ContentRoot, path), new FileInfo(path).Length);
            })
            .OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (entries.LongLength != current.PreviewFiles || entries.Sum(entry => entry.Bytes) != current.PreviewBytes)
            throw new IOException("The preview inventory changed while preparing the deletion journal.");

        var plannedUtc = DateTimeOffset.UtcNow;
        WriteReceipt(pendingPath, new("wow-crucible-preview-prune-v1", "planned", plannedUtc, null, current, entries));

        long completed = 0, freed = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = SafeCombine(current.ContentRoot, entry.RelativePath);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != entry.Bytes || !info.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Preview changed after the deletion journal was written: {path}");
            File.Delete(path);
            completed++;
            freed = checked(freed + entry.Bytes);
            if (completed % 1000 == 0 || completed == entries.LongLength)
                progress?.Report(new(completed, entries.LongLength, freed, current.PreviewBytes, entry.RelativePath));
        }

        RemoveEmptyDirectories(current.ContentRoot);
        if (Directory.EnumerateFiles(current.ContentRoot, "*", RecursiveFiles)
            .Any(path => Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("One or more PNG previews remain after the prune operation.");

        var completedReceipt = new ProcessedAssetPreviewPruneReceipt(
            "wow-crucible-preview-prune-v1", "complete", plannedUtc, DateTimeOffset.UtcNow, current, entries);
        var temporaryReceipt = receiptPath + $".tmp-{Guid.NewGuid():N}";
        WriteReceipt(temporaryReceipt, completedReceipt);
        File.Move(temporaryReceipt, receiptPath);
        File.Delete(pendingPath);
        return completedReceipt;
    }

    private static void RemoveEmptyDirectories(string contentRoot)
    {
        foreach (var directory in Directory.EnumerateDirectories(contentRoot, "*", RecursiveFiles).OrderByDescending(path => path.Length))
        {
            EnsureInside(contentRoot, directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }

    private static void WriteReceipt(string path, ProcessedAssetPreviewPruneReceipt receipt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan);
        JsonSerializer.Serialize(stream, receipt, new JsonSerializerOptions { WriteIndented = true });
        stream.Flush(true);
    }

    private static string SafeCombine(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        EnsureInside(root, path);
        return path;
    }

    private static void EnsureInside(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(relative))
            throw new InvalidDataException($"Preview path escapes the published Content directory: {path}");
    }
}
