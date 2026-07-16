using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public sealed record ClientExecutableIndex(string Path, long Length, long LastWriteUtcTicks, string? FileVersion, string? ProductVersion, string Sha256);
public sealed record ClientArchiveSummary(string RelativePath, long Length, long LastWriteUtcTicks, string? Sha256, string ContentIndexFile, int PayloadFiles, int MetadataFiles, int AnonymousFiles, long UncompressedBytes, string? Error);
public sealed record ClientArchiveIndex(int FormatVersion, string ClientRoot, string Name, DateTimeOffset UpdatedUtc, bool Complete, int CompletedArchives, ClientExecutableIndex? Executable, IReadOnlyList<ClientArchiveSummary> Archives);
public sealed record ArchiveContentIndex(int FormatVersion, string RelativePath, long ArchiveLength, long ArchiveLastWriteUtcTicks, IReadOnlyList<MpqFileEntry> Files);
public sealed record ClientIndexProgress(int CompletedArchives, int TotalArchives, string ArchivePath, string Stage, bool Cached);
public sealed record IndexedExtractionResult(int SelectedFiles, int ExtractedFiles, int SkippedExistingFiles);

public sealed class ClientArchiveIndexService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ClientArchiveIndex Build(string clientRoot, string outputDirectory, bool hashArchives = true, IProgress<ClientIndexProgress>? progress = null, CancellationToken cancellationToken = default, string? externalListFile = null)
    {
        clientRoot = Path.GetFullPath(clientRoot); outputDirectory = Path.GetFullPath(outputDirectory);
        if (!Directory.Exists(clientRoot)) throw new DirectoryNotFoundException($"Client root does not exist: {clientRoot}");
        var data = Path.Combine(clientRoot, "Data");
        if (!Directory.Exists(data)) throw new DirectoryNotFoundException($"Client has no Data directory: {clientRoot}");
        Directory.CreateDirectory(outputDirectory); Directory.CreateDirectory(Path.Combine(outputDirectory, "archives"));
        var summaryPath = Path.Combine(outputDirectory, "client-index.json");
        var previous = TryLoad<ClientArchiveIndex>(summaryPath);
        var cached = (previous?.Archives ?? []).ToDictionary(archive => archive.RelativePath, StringComparer.OrdinalIgnoreCase);
        var archives = Directory.EnumerateFiles(data, "*.mpq", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var summaries = new List<ClientArchiveSummary>(archives.Length);
        var executable = IndexExecutable(clientRoot);

        for (var index = 0; index < archives.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = archives[index];
            var info = new FileInfo(path);
            var relative = Path.GetRelativePath(clientRoot, path).Replace('/', Path.DirectorySeparatorChar);
            cached.TryGetValue(relative, out var old);
            var identityMatches = old is not null && old.Length == info.Length && old.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks;
            var contentFile = old?.ContentIndexFile ?? Path.Combine("archives", ContentFileName(relative)).Replace('/', Path.DirectorySeparatorChar);
            var contentPath = Path.Combine(outputDirectory, contentFile);
            var oldContent = identityMatches ? TryLoad<ArchiveContentIndex>(contentPath) : null;
            var contentMatches = oldContent is not null && oldContent.ArchiveLength == info.Length && oldContent.ArchiveLastWriteUtcTicks == info.LastWriteTimeUtc.Ticks;
            var hash = identityMatches ? old?.Sha256 : null;

            if (hashArchives && string.IsNullOrWhiteSpace(hash))
            {
                progress?.Report(new(index, archives.Length, relative, "hashing", false));
                hash = HashFile(path, cancellationToken);
            }
            if (contentMatches)
            {
                summaries.Add(ToSummary(relative, info, hash, contentFile, oldContent!.Files, null));
                progress?.Report(new(index + 1, archives.Length, relative, "indexed", true));
                SaveSummary(summaryPath, clientRoot, executable, summaries, archives.Length, false);
                continue;
            }

            try
            {
                progress?.Report(new(index, archives.Length, relative, "listing", false));
                var files = new PatchArchiveService().ListFiles(path);
                WriteAtomic(contentPath, new ArchiveContentIndex(1, relative, info.Length, info.LastWriteTimeUtc.Ticks, files));
                summaries.Add(ToSummary(relative, info, hash, contentFile, files, null));
            }
            catch (Exception ex)
            {
                summaries.Add(new(relative, info.Length, info.LastWriteTimeUtc.Ticks, hash, contentFile, 0, 0, 0, 0, ex.Message));
            }
            progress?.Report(new(index + 1, archives.Length, relative, "indexed", false));
            SaveSummary(summaryPath, clientRoot, executable, summaries, archives.Length, false);
        }

        RecoverAnonymousNames(clientRoot, outputDirectory, archives, summaries, progress, cancellationToken, externalListFile);
        var result = new ClientArchiveIndex(1, clientRoot, Path.GetFileName(Path.TrimEndingDirectorySeparator(clientRoot)), DateTimeOffset.UtcNow, true, summaries.Count, executable, summaries);
        WriteAtomic(summaryPath, result);
        return result;
    }

    public static ClientArchiveIndex Load(string indexDirectory) => TryLoad<ClientArchiveIndex>(Path.Combine(Path.GetFullPath(indexDirectory), "client-index.json")) ?? throw new InvalidDataException("Client index is missing or invalid.");

    private static ClientExecutableIndex? IndexExecutable(string root)
    {
        var path = Directory.EnumerateFiles(root, "Wow.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (path is null) return null;
        var info = new FileInfo(path); var version = FileVersionInfo.GetVersionInfo(path);
        return new(Path.GetRelativePath(root, path), info.Length, info.LastWriteTimeUtc.Ticks, version.FileVersion, version.ProductVersion, HashFile(path, CancellationToken.None));
    }

    private static ClientArchiveSummary ToSummary(string relative, FileInfo info, string? hash, string contentFile, IReadOnlyList<MpqFileEntry> files, string? error)
        => new(relative, info.Length, info.LastWriteTimeUtc.Ticks, hash, contentFile, files.Count(file => !file.IsMetadata), files.Count(file => file.IsMetadata), files.Count(file => !file.IsMetadata && IsAnonymous(file.ArchivePath)), files.Where(file => !file.IsMetadata).Sum(file => file.Size), error);

    private static void RecoverAnonymousNames(string clientRoot, string outputDirectory, string[] archivePaths, List<ClientArchiveSummary> summaries, IProgress<ClientIndexProgress>? progress, CancellationToken cancellationToken, string? externalListFile)
    {
        var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var summary in summaries)
        {
            var content = TryLoad<ArchiveContentIndex>(Path.Combine(outputDirectory, summary.ContentIndexFile));
            if (content is null) continue;
            foreach (var file in content.Files.Where(file => !file.IsMetadata && !IsAnonymous(file.ArchivePath))) knownPaths.Add(file.ArchivePath);
        }
        if (!string.IsNullOrWhiteSpace(externalListFile))
        {
            externalListFile = Path.GetFullPath(externalListFile);
            if (!File.Exists(externalListFile)) throw new FileNotFoundException("The external MPQ listfile was not found.", externalListFile);
            foreach (var path in File.ReadLines(externalListFile).Where(path => !string.IsNullOrWhiteSpace(path))) knownPaths.Add(path.Trim());
        }
        if (knownPaths.Count == 0 || summaries.All(summary => summary.AnonymousFiles == 0)) return;

        var corpusPath = Path.Combine(outputDirectory, "known-paths.txt");
        File.WriteAllLines(corpusPath, knownPaths.Order(StringComparer.OrdinalIgnoreCase), new UTF8Encoding(false));
        var archiveByRelative = archivePaths.ToDictionary(path => Path.GetRelativePath(clientRoot, path).Replace('/', Path.DirectorySeparatorChar), StringComparer.OrdinalIgnoreCase);
        var service = new PatchArchiveService();
        for (var index = 0; index < summaries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var summary = summaries[index];
            if (summary.AnonymousFiles == 0 || !archiveByRelative.TryGetValue(summary.RelativePath, out var archivePath)) continue;
            progress?.Report(new(index, summaries.Count, summary.RelativePath, "resolving names", false));
            try
            {
                var info = new FileInfo(archivePath);
                var files = service.ListFiles(archivePath, "*", corpusPath);
                var resolved = ToSummary(summary.RelativePath, info, summary.Sha256, summary.ContentIndexFile, files, null);
                if (resolved.PayloadFiles == summary.PayloadFiles && resolved.AnonymousFiles < summary.AnonymousFiles)
                {
                    WriteAtomic(Path.Combine(outputDirectory, summary.ContentIndexFile), new ArchiveContentIndex(1, summary.RelativePath, info.Length, info.LastWriteTimeUtc.Ticks, files));
                    summaries[index] = resolved;
                }
            }
            catch (Exception ex)
            {
                summaries[index] = summary with { Error = $"Name recovery failed: {ex.Message}" };
            }
        }
    }

    public static bool IsAnonymous(string archivePath)
    {
        if (archivePath.IndexOfAny(['\\', '/']) >= 0) return false;
        var stem = Path.GetFileNameWithoutExtension(archivePath);
        return stem.Length > 4 && stem.StartsWith("File", StringComparison.OrdinalIgnoreCase) && stem[4..].All(char.IsDigit);
    }

    public static int CreatePathCorpus(IEnumerable<string> indexDirectories, string outputFile)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in indexDirectories.Select(Path.GetFullPath))
        {
            var index = Load(directory);
            foreach (var archive in index.Archives)
            {
                var content = TryLoad<ArchiveContentIndex>(Path.Combine(directory, archive.ContentIndexFile));
                if (content is null) continue;
                foreach (var file in content.Files.Where(file => !file.IsMetadata && !IsAnonymous(file.ArchivePath))) paths.Add(file.ArchivePath);
            }
        }
        outputFile = Path.GetFullPath(outputFile);
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
        File.WriteAllLines(outputFile, paths.Order(StringComparer.OrdinalIgnoreCase), new UTF8Encoding(false));
        return paths.Count;
    }

    public static IndexedExtractionResult ExtractIndexed(string indexDirectory, string archiveRelativePath, string destinationRoot, string? filter = null, bool resolvedOnly = false, bool overwrite = false, IProgress<(int Done, int Total, string Path)>? progress = null, CancellationToken cancellationToken = default)
    {
        indexDirectory = Path.GetFullPath(indexDirectory);
        var index = Load(indexDirectory);
        var summary = index.Archives.SingleOrDefault(archive => archive.RelativePath.Equals(archiveRelativePath, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"The client index has no archive named '{archiveRelativePath}'.");
        var content = TryLoad<ArchiveContentIndex>(Path.Combine(indexDirectory, summary.ContentIndexFile))
            ?? throw new InvalidDataException($"The content index for '{summary.RelativePath}' is missing or invalid.");
        var entries = content.Files.Where(file => !file.IsMetadata && (!resolvedOnly || !IsAnonymous(file.ArchivePath)) && (string.IsNullOrEmpty(filter) || file.ArchivePath.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray();
        var archivePath = Path.GetFullPath(Path.Combine(index.ClientRoot, summary.RelativePath));
        var relative = Path.GetRelativePath(index.ClientRoot, archivePath);
        if (relative.Equals("..", StringComparison.Ordinal) || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) throw new InvalidDataException("The indexed archive path escapes the client root.");
        destinationRoot = Path.GetFullPath(destinationRoot);
        var pending = overwrite ? entries : entries.Where(entry => !ExistingExtractionMatches(destinationRoot, entry)).ToArray();
        new PatchArchiveService().Extract(archivePath, destinationRoot, pending, progress, cancellationToken);
        return new(entries.Length, pending.Length, entries.Length - pending.Length);
    }

    private static bool ExistingExtractionMatches(string destinationRoot, MpqFileEntry entry)
    {
        var path = Path.GetFullPath(Path.Combine(destinationRoot, entry.ArchivePath.Replace('\\', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(destinationRoot, path);
        return !relative.Equals("..", StringComparison.Ordinal) && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && File.Exists(path) && new FileInfo(path).Length == entry.Size;
    }

    private static void SaveSummary(string path, string root, ClientExecutableIndex? executable, IReadOnlyList<ClientArchiveSummary> archives, int totalArchives, bool complete)
        => WriteAtomic(path, new ClientArchiveIndex(1, root, Path.GetFileName(Path.TrimEndingDirectorySeparator(root)), DateTimeOffset.UtcNow, complete && archives.Count == totalArchives, archives.Count, executable, archives));

    private static string HashFile(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[4 * 1024 * 1024];
        int read; while ((read = stream.Read(buffer)) > 0) { cancellationToken.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string ContentFileName(string relative)
    {
        var name = string.Concat(Path.GetFileName(relative).Select(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_'));
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relative)))[..12].ToLowerInvariant();
        return $"{name}.{suffix}.json";
    }

    private static T? TryLoad<T>(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : default; }
        catch { return default; }
    }

    private static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions)); File.Move(temp, path, true);
    }
}
