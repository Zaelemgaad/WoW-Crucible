using System.Text;
using System.Security.Cryptography;

namespace WoWCrucible.Core;

public sealed record AssetComparisonDirectory(string LogicalPath, int PngFiles, int ProvenanceSources, int M2Files = 0, int SkinFiles = 0);
public sealed record AssetComparisonEntry(string LogicalPath, string Provenance, string FileName, string FullPath, long Bytes);
public enum AssetModelCompatibility { Ready, MissingSkin, RequiresConversion, Invalid }
public sealed record AssetComparisonModel(string LogicalPath, string Provenance, string FileName, string ModelPath, string? SkinPath, AssetModelCompatibility Compatibility, uint? Version, string Status)
{
    public IReadOnlyList<string> SkinPaths { get; init; } = SkinPath is null ? [] : [SkinPath];
    public override string ToString() => $"{(Compatibility == AssetModelCompatibility.Ready ? "READY" : Compatibility.ToString().ToUpperInvariant())} · {Provenance} · {FileName} · {SkinPaths.Count:N0} SKIN view(s){(string.IsNullOrEmpty(LogicalPath) ? string.Empty : $" · {LogicalPath}")}";
}
public sealed record AssetComparisonDuplicateGroup(string Sha256, long Bytes, IReadOnlyList<AssetComparisonEntry> Entries)
{
    public long RecoverableBytes => Bytes * (Entries.Count - 1L);
}
public enum AssetComparisonIndexSource { Sidecar, Catalog, FileSystem }
public sealed record AssetComparisonIndex(string LibraryRoot, string ContentRoot, IReadOnlyList<AssetComparisonDirectory> Directories, int TotalPngFiles,
    string? LooseContentRoot = null, AssetComparisonIndexSource Source = AssetComparisonIndexSource.FileSystem);

public static class AssetComparisonService
{
    public const string AggregateSidecarFileName = AssetComparisonAggregateCache.FileName;
    private const int MaximumModelAncestorScopes = 4;

    public static AssetComparisonIndex BuildIndex(string libraryRoot, CancellationToken cancellationToken = default)
    {
        libraryRoot = Path.GetFullPath(libraryRoot); var contentRoot = Path.Combine(libraryRoot, "Archives", "Content"); var looseContentRoot = Path.Combine(libraryRoot, "Loose", "Content");
        if (!Directory.Exists(contentRoot) && !Directory.Exists(looseContentRoot)) throw new DirectoryNotFoundException($"No content-first archive or loose folder exists under: {libraryRoot}");
        var catalog = Path.Combine(libraryRoot, "asset-catalog.csv");
        AssetComparisonAggregateDirectory[] aggregates;
        var source = AssetComparisonIndexSource.FileSystem;
        if (File.Exists(catalog) && AssetComparisonAggregateCache.TryLoad(libraryRoot, catalog, cancellationToken, out aggregates))
            source = AssetComparisonIndexSource.Sidecar;
        else if (File.Exists(catalog))
        {
            try
            {
                aggregates = AssetComparisonAggregateCache.ReadCatalog(catalog, cancellationToken);
                source = AssetComparisonIndexSource.Catalog;
                AssetComparisonAggregateCache.TryWrite(libraryRoot, catalog, aggregates, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException) { aggregates = AssetComparisonAggregateCache.ScanFileSystem(libraryRoot, new[] { contentRoot, looseContentRoot }.Where(Directory.Exists), cancellationToken); }
        }
        else aggregates = AssetComparisonAggregateCache.ScanFileSystem(libraryRoot, new[] { contentRoot, looseContentRoot }.Where(Directory.Exists), cancellationToken);
        var directories = aggregates.Select(aggregate => new AssetComparisonDirectory(aggregate.LogicalPath, aggregate.PngFiles, aggregate.PngProvenanceSources.Length, aggregate.M2Files, aggregate.SkinFiles)).ToArray();
        return new(libraryRoot, contentRoot, directories, directories.Sum(directory => directory.PngFiles), Directory.Exists(looseContentRoot) ? looseContentRoot : null, source);
    }

    public static IReadOnlyList<AssetComparisonEntry> GetDirectoryPngs(AssetComparisonIndex index, string logicalPath)
    {
        var result = new List<AssetComparisonEntry>();
        var directory = Path.GetFullPath(Path.Combine(index.ContentRoot, logicalPath)); EnsureInside(index.ContentRoot, directory);
        if (Directory.Exists(directory))
        {
            foreach (var provenanceDirectory in Directory.EnumerateDirectories(directory))
            {
                var provenance = Path.GetFileName(provenanceDirectory);
                foreach (var file in Directory.EnumerateFiles(provenanceDirectory, "*.png", SearchOption.TopDirectoryOnly)) AddEntry(result, logicalPath, provenance, file);
            }
        }
        if (index.LooseContentRoot is { } looseRoot)
        {
            var looseDirectory = Path.GetFullPath(Path.Combine(looseRoot, logicalPath)); EnsureInside(looseRoot, looseDirectory);
            if (Directory.Exists(looseDirectory)) foreach (var file in Directory.EnumerateFiles(looseDirectory, "*.png", SearchOption.TopDirectoryOnly)) AddEntry(result, logicalPath, "Loose", file);
        }
        return result.OrderBy(entry => entry.Provenance, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<AssetComparisonDuplicateGroup> FindExactDuplicates(IReadOnlyList<AssetComparisonEntry> entries, CancellationToken cancellationToken = default)
    {
        var duplicates = new List<AssetComparisonDuplicateGroup>();
        foreach (var sizeGroup in entries.GroupBy(entry => entry.Bytes).Where(group => group.Count() > 1))
        {
            var hashes = new Dictionary<string, List<AssetComparisonEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in sizeGroup)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = File.OpenRead(entry.FullPath);
                var hash = Convert.ToHexString(SHA256.HashData(stream));
                if (!hashes.TryGetValue(hash, out var matches)) hashes[hash] = matches = [];
                matches.Add(entry);
            }
            foreach (var hashGroup in hashes.Where(pair => pair.Value.Count > 1))
            {
                var verifiedGroups = new List<List<AssetComparisonEntry>>();
                foreach (var entry in hashGroup.Value)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var verified = verifiedGroups.FirstOrDefault(group => FilesAreIdentical(group[0].FullPath, entry.FullPath, cancellationToken));
                    if (verified is null) verifiedGroups.Add([entry]); else verified.Add(entry);
                }
                duplicates.AddRange(verifiedGroups.Where(group => group.Count > 1).Select(group => new AssetComparisonDuplicateGroup(hashGroup.Key, sizeGroup.Key, group)));
            }
        }
        return duplicates.OrderByDescending(group => group.RecoverableBytes).ThenBy(group => group.Sha256, StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<AssetComparisonModel> GetDirectoryModels(AssetComparisonIndex index, string logicalPath)
        => GetRelevantModels(index, logicalPath).Models;

    public static (string DiscoveryScope, IReadOnlyList<AssetComparisonModel> Models) GetRelevantModels(AssetComparisonIndex index, string logicalPath, CancellationToken cancellationToken = default)
    {
        logicalPath = logicalPath.Trim('\\', '/');
        foreach (var scope in LogicalAncestors(logicalPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new List<AssetComparisonModel>();
            var directory = Path.GetFullPath(Path.Combine(index.ContentRoot, scope)); EnsureInside(index.ContentRoot, directory);
            if (Directory.Exists(directory)) AddArchiveModels(result, scope, directory, cancellationToken);
            if (index.LooseContentRoot is { } looseRoot)
            {
                var looseDirectory = Path.GetFullPath(Path.Combine(looseRoot, scope)); EnsureInside(looseRoot, looseDirectory);
                if (Directory.Exists(looseDirectory)) AddLooseModels(result, looseRoot, looseDirectory, cancellationToken);
            }
            if (result.Count > 0) return (scope, result.OrderBy(model => model.Compatibility).ThenBy(model => model.Provenance, StringComparer.OrdinalIgnoreCase).ThenBy(model => model.LogicalPath, StringComparer.OrdinalIgnoreCase).ThenBy(model => model.FileName, StringComparer.OrdinalIgnoreCase).ToArray());
        }
        return (logicalPath, []);
    }

    private static void AddEntry(List<AssetComparisonEntry> result, string logicalPath, string provenance, string file)
    {
        var info = new FileInfo(file); result.Add(new(logicalPath, provenance, info.Name, info.FullName, info.Length));
    }

    private static void AddArchiveModels(List<AssetComparisonModel> result, string scope, string directory, CancellationToken cancellationToken)
    {
        // Content-first layout is <logical path>\<provenance>\<files>. Only
        // inspect the direct provenance layer for this scope. A recursive walk
        // here can accidentally turn a category fallback such as "Character"
        // into an 80k-model probe of the entire library.
        foreach (var provenanceDirectory in Directory.EnumerateDirectories(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provenance = Path.GetFileName(provenanceDirectory);
            foreach (var modelPath in Directory.EnumerateFiles(provenanceDirectory, "*.m2", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(ProbeModel(scope, provenance, modelPath));
            }
        }
    }

    private static void AddLooseModels(List<AssetComparisonModel> result, string looseRoot, string directory, CancellationToken cancellationToken)
    {
        foreach (var modelPath in Directory.EnumerateFiles(directory, "*.m2", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(ProbeModel(Path.GetDirectoryName(Path.GetRelativePath(looseRoot, modelPath)) ?? string.Empty, "Loose", modelPath));
        }
    }

    private static AssetComparisonModel ProbeModel(string logicalPath, string provenance, string modelPath)
    {
        try
        {
            using var stream = new FileStream(modelPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
            Span<byte> header = stackalloc byte[8]; if (stream.Read(header) != header.Length) return new(logicalPath, provenance, Path.GetFileName(modelPath), modelPath, null, AssetModelCompatibility.Invalid, null, "M2 header is truncated.");
            var magic = Encoding.ASCII.GetString(header[..4]); var version = BitConverter.ToUInt32(header[4..]);
            var directory = Path.GetDirectoryName(modelPath)!; var stem = Path.GetFileNameWithoutExtension(modelPath);
            var skins = Directory.EnumerateFiles(directory, stem + "*.skin", SearchOption.TopDirectoryOnly).Where(IsSkin).OrderBy(path => path.EndsWith("00.skin", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            var skin = skins.FirstOrDefault(); AssetComparisonModel result;
            if (magic == "MD21") result = new(logicalPath, provenance, Path.GetFileName(modelPath), modelPath, skin, AssetModelCompatibility.RequiresConversion, version, "Modern chunked MD21 model; convert it to Wrath before preview/deployment.");
            else if (magic != "MD20") result = new(logicalPath, provenance, Path.GetFileName(modelPath), modelPath, skin, AssetModelCompatibility.Invalid, version, $"Invalid M2 signature '{magic}'.");
            else if (version != 264) result = new(logicalPath, provenance, Path.GetFileName(modelPath), modelPath, skin, AssetModelCompatibility.RequiresConversion, version, $"M2 version {version}; Wrath preview requires version 264.");
            else result = skin is null
                ? new(logicalPath, provenance, Path.GetFileName(modelPath), modelPath, null, AssetModelCompatibility.MissingSkin, version, $"Wrath M2 is valid but no matching {stem}00.skin is available.")
                : new(logicalPath, provenance, Path.GetFileName(modelPath), modelPath, skin, AssetModelCompatibility.Ready, version, $"Wrath M2 version 264 with {Path.GetFileName(skin)}.");
            return result with { SkinPaths = skins };
        }
        catch (Exception exception) { return new(logicalPath, provenance, Path.GetFileName(modelPath), modelPath, null, AssetModelCompatibility.Invalid, null, exception.Message); }
    }

    private static bool IsSkin(string path)
    {
        try { using var stream = File.OpenRead(path); Span<byte> magic = stackalloc byte[4]; return stream.Read(magic) == 4 && Encoding.ASCII.GetString(magic) == "SKIN"; }
        catch { return false; }
    }

    private static IEnumerable<string> LogicalAncestors(string path)
    {
        var current = path; var yielded = 0;
        while (!string.IsNullOrEmpty(current) && yielded < MaximumModelAncestorScopes)
        {
            yield return current;
            yielded++;
            var parent = Path.GetDirectoryName(current); if (parent is null || parent.Equals(current, StringComparison.OrdinalIgnoreCase)) yield break; current = parent;
        }
    }

    public static bool FilesAreIdentical(string leftPath, string rightPath, CancellationToken cancellationToken = default)
    {
        const int BufferSize = 1024 * 1024;
        using var left = new FileStream(leftPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        using var right = new FileStream(rightPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        if (left.Length != right.Length) return false;
        var leftBuffer = new byte[BufferSize]; var rightBuffer = new byte[BufferSize];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leftRead = left.Read(leftBuffer); var rightRead = right.Read(rightBuffer);
            if (leftRead != rightRead) return false;
            if (leftRead == 0) return true;
            if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead))) return false;
        }
    }

    private static void EnsureInside(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)) throw new InvalidOperationException("The selected comparison directory escaped the asset library.");
    }
}
