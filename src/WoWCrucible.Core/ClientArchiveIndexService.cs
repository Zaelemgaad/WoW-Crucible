using System.Diagnostics;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public sealed record ClientExecutableIndex(string Path, long Length, long LastWriteUtcTicks, string? FileVersion, string? ProductVersion, string Sha256);
public enum ClientArchiveScope { RootData, ActiveLocale, InactiveLocale, Cache, CustomSubdirectory, Backup }
public enum ClientLooseFileScope { Runtime, AddOn, Configuration, Volatile, Other }
public sealed record ClientLooseFileSummary(string RelativePath, ClientLooseFileScope Scope, long Length, long LastWriteUtcTicks, string? Sha256);
public sealed record ClientArchiveSummary(string RelativePath, ClientArchiveScope Scope, long Length, long LastWriteUtcTicks, string? Sha256, string ContentIndexFile, int PayloadFiles, int MetadataFiles, int AnonymousFiles, long UncompressedBytes, string? Error);
public sealed record ClientArchiveIndex(int FormatVersion, string ClientRoot, string Name, DateTimeOffset UpdatedUtc, bool Complete, int CompletedArchives, string? ActiveLocale, ClientExecutableIndex? Executable, IReadOnlyList<ClientArchiveSummary> Archives, IReadOnlyList<ClientLooseFileSummary>? LooseFiles);
public sealed record ArchiveContentIndex(int FormatVersion, string RelativePath, long ArchiveLength, long ArchiveLastWriteUtcTicks, IReadOnlyList<MpqFileEntry> Files);
public sealed record ClientIndexProgress(int CompletedArchives, int TotalArchives, string ArchivePath, string Stage, bool Cached);
public sealed record IndexedExtractionResult(int SelectedFiles, int ExtractedFiles, int SkippedExistingFiles);

public sealed class ClientArchiveIndexService
{
    private const int IndexFormatVersion = 4;
    private const int ArchiveContentFormatVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ClientArchiveIndex Build(string clientRoot, string outputDirectory, bool hashArchives = true, IProgress<ClientIndexProgress>? progress = null, CancellationToken cancellationToken = default, string? externalListFile = null, string? executablePath = null)
    {
        clientRoot = Path.GetFullPath(clientRoot); outputDirectory = Path.GetFullPath(outputDirectory);
        if (!Directory.Exists(clientRoot)) throw new DirectoryNotFoundException($"Client root does not exist: {clientRoot}");
        var data = Path.Combine(clientRoot, "Data");
        if (!Directory.Exists(data)) throw new DirectoryNotFoundException($"Client has no Data directory: {clientRoot}");
        Directory.CreateDirectory(outputDirectory); Directory.CreateDirectory(Path.Combine(outputDirectory, "archives"));
        var summaryPath = Path.Combine(outputDirectory, "client-index.json");
        var partialPath = Path.Combine(outputDirectory, "client-index.partial.json");
        var previous = TryLoad<ClientArchiveIndex>(partialPath) ?? TryLoad<ClientArchiveIndex>(summaryPath);
        var cached = (previous?.Archives ?? []).ToDictionary(archive => archive.RelativePath, StringComparer.OrdinalIgnoreCase);
        var archives = Directory.EnumerateFiles(data, "*.mpq", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var summaries = new List<ClientArchiveSummary>(archives.Length);
        var activeLocale = DetectActiveLocale(clientRoot);
        var executable = IndexExecutable(clientRoot, executablePath);

        for (var index = 0; index < archives.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = archives[index];
            var info = new FileInfo(path);
            var relative = Path.GetRelativePath(clientRoot, path).Replace('/', Path.DirectorySeparatorChar);
            var scope = ClassifyArchive(relative, activeLocale);
            cached.TryGetValue(relative, out var old);
            var identityMatches = old is not null && old.Length == info.Length && old.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks;
            var contentFile = old?.ContentIndexFile ?? Path.Combine("archives", ContentFileName(relative)).Replace('/', Path.DirectorySeparatorChar);
            var contentPath = Path.Combine(outputDirectory, contentFile);
            var oldContent = identityMatches ? TryLoad<ArchiveContentIndex>(contentPath) : null;
            var contentMatches = oldContent is not null && oldContent.FormatVersion == ArchiveContentFormatVersion && oldContent.ArchiveLength == info.Length && oldContent.ArchiveLastWriteUtcTicks == info.LastWriteTimeUtc.Ticks;
            var hash = identityMatches ? old?.Sha256 : null;

            if (hashArchives && string.IsNullOrWhiteSpace(hash))
            {
                progress?.Report(new(index, archives.Length, relative, "hashing", false));
                hash = HashFile(path, cancellationToken);
            }
            if (contentMatches)
            {
                summaries.Add(ToSummary(relative, scope, info, hash, contentFile, oldContent!.Files, null));
                progress?.Report(new(index + 1, archives.Length, relative, "indexed", true));
                SaveSummary(partialPath, clientRoot, activeLocale, executable, summaries, archives.Length, false);
                continue;
            }

            try
            {
                progress?.Report(new(index, archives.Length, relative, "listing", false));
                var files = new PatchArchiveService().ListFiles(path);
                WriteAtomic(contentPath, new ArchiveContentIndex(ArchiveContentFormatVersion, relative, info.Length, info.LastWriteTimeUtc.Ticks, files));
                summaries.Add(ToSummary(relative, scope, info, hash, contentFile, files, null));
            }
            catch (Exception ex)
            {
                summaries.Add(new(relative, scope, info.Length, info.LastWriteTimeUtc.Ticks, hash, contentFile, 0, 0, 0, 0, ex.Message));
            }
            progress?.Report(new(index + 1, archives.Length, relative, "indexed", false));
            SaveSummary(partialPath, clientRoot, activeLocale, executable, summaries, archives.Length, false);
        }

        RecoverAnonymousNames(clientRoot, outputDirectory, archives, summaries, progress, cancellationToken, externalListFile);
        var looseFiles = IndexLooseFiles(clientRoot, outputDirectory, cancellationToken);
        var result = new ClientArchiveIndex(IndexFormatVersion, clientRoot, Path.GetFileName(Path.TrimEndingDirectorySeparator(clientRoot)), DateTimeOffset.UtcNow, true, summaries.Count, activeLocale, executable, summaries, looseFiles);
        WriteAtomic(summaryPath, result);
        if (File.Exists(partialPath)) File.Delete(partialPath);
        return result;
    }

    public static ClientArchiveIndex Load(string indexDirectory)
    {
        var directory = Path.GetFullPath(indexDirectory);
        return TryLoad<ClientArchiveIndex>(Path.Combine(directory, "client-index.json")) ?? TryLoad<ClientArchiveIndex>(Path.Combine(directory, "client-index.partial.json")) ?? throw new InvalidDataException("Client index is missing or invalid.");
    }

    private static ClientExecutableIndex? IndexExecutable(string root, string? requestedPath)
    {
        var path = string.IsNullOrWhiteSpace(requestedPath) ? Directory.EnumerateFiles(root, "Wow.exe", SearchOption.TopDirectoryOnly).FirstOrDefault() : Path.GetFullPath(Path.IsPathRooted(requestedPath) ? requestedPath : Path.Combine(root, requestedPath));
        path ??= Directory.EnumerateFiles(root, "*.exe", SearchOption.TopDirectoryOnly)
            .Select(candidate => (Path: candidate, Version: FileVersionInfo.GetVersionInfo(candidate)))
            .Where(candidate => candidate.Version.FileMajorPart == 3 && candidate.Version.FileMinorPart == 3 && candidate.Version.FileBuildPart == 5)
            .OrderByDescending(candidate => new FileInfo(candidate.Path).Length)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();
        if (path is null) return null;
        if (!File.Exists(path)) throw new FileNotFoundException("The selected client executable was not found.", path);
        var info = new FileInfo(path); var version = FileVersionInfo.GetVersionInfo(path);
        return new(Path.GetRelativePath(root, path), info.Length, info.LastWriteTimeUtc.Ticks, version.FileVersion, version.ProductVersion, HashFile(path, CancellationToken.None));
    }

    private static IReadOnlyList<ClientLooseFileSummary> IndexLooseFiles(string root, string outputDirectory, CancellationToken cancellationToken)
    {
        var data = Path.GetFullPath(Path.Combine(root, "Data"));
        var output = Path.GetFullPath(outputDirectory);
        var files = new List<ClientLooseFileSummary>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var path in Directory.EnumerateFiles(root, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(path);
            if (IsWithin(full, data) || IsWithin(full, output)) continue;
            var relative = Path.GetRelativePath(root, full).Replace('/', Path.DirectorySeparatorChar);
            var info = new FileInfo(full); var scope = ClassifyLooseFile(relative);
            var hash = scope == ClientLooseFileScope.Runtime && info.Length <= 256L * 1024 * 1024 ? HashFile(full, cancellationToken) : null;
            files.Add(new(relative, scope, info.Length, info.LastWriteTimeUtc.Ticks, hash));
        }
        return files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static ClientLooseFileScope ClassifyLooseFile(string relativePath)
    {
        var parts = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var first = parts.FirstOrDefault() ?? string.Empty;
        if (first.Equals("Interface", StringComparison.OrdinalIgnoreCase) && parts.Length > 1 && parts[1].Equals("AddOns", StringComparison.OrdinalIgnoreCase)) return ClientLooseFileScope.AddOn;
        if (first.Equals("WTF", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(relativePath).Equals(".wtf", StringComparison.OrdinalIgnoreCase)) return ClientLooseFileScope.Configuration;
        if (first.Equals("Cache", StringComparison.OrdinalIgnoreCase) || first.Equals("Errors", StringComparison.OrdinalIgnoreCase) || first.Equals("Logs", StringComparison.OrdinalIgnoreCase) || first.Equals("Screenshots", StringComparison.OrdinalIgnoreCase) || new[] { ".log", ".dmp" }.Contains(Path.GetExtension(relativePath), StringComparer.OrdinalIgnoreCase)) return ClientLooseFileScope.Volatile;
        if (parts.Length == 1 && new[] { ".exe", ".dll", ".asi" }.Contains(Path.GetExtension(relativePath), StringComparer.OrdinalIgnoreCase)) return ClientLooseFileScope.Runtime;
        return ClientLooseFileScope.Other;
    }

    private static bool IsWithin(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative.Equals(".", StringComparison.Ordinal) || !relative.Equals("..", StringComparison.Ordinal) && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static ClientArchiveSummary ToSummary(string relative, ClientArchiveScope scope, FileInfo info, string? hash, string contentFile, IReadOnlyList<MpqFileEntry> files, string? error)
        => new(relative, scope, info.Length, info.LastWriteTimeUtc.Ticks, hash, contentFile, files.Count(file => !file.IsMetadata), files.Count(file => file.IsMetadata), files.Count(file => !file.IsMetadata && IsAnonymous(file.ArchivePath)), files.Where(file => !file.IsMetadata).Sum(file => file.Size), error);

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
                var resolved = ToSummary(summary.RelativePath, summary.Scope, info, summary.Sha256, summary.ContentIndexFile, files, null);
                if (resolved.PayloadFiles == summary.PayloadFiles && resolved.AnonymousFiles < summary.AnonymousFiles)
                {
                    WriteAtomic(Path.Combine(outputDirectory, summary.ContentIndexFile), new ArchiveContentIndex(ArchiveContentFormatVersion, summary.RelativePath, info.Length, info.LastWriteTimeUtc.Ticks, files));
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

    public static IndexedExtractionResult ExtractIndexed(string indexDirectory, string archiveRelativePath, string destinationRoot, string? filter = null, bool resolvedOnly = false, bool anonymousOnly = false, bool overwrite = false, IProgress<(int Done, int Total, string Path)>? progress = null, CancellationToken cancellationToken = default, int workers = 0)
    {
        indexDirectory = Path.GetFullPath(indexDirectory);
        var index = Load(indexDirectory);
        var summary = index.Archives.SingleOrDefault(archive => archive.RelativePath.Equals(archiveRelativePath, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"The client index has no archive named '{archiveRelativePath}'.");
        var content = TryLoad<ArchiveContentIndex>(Path.Combine(indexDirectory, summary.ContentIndexFile))
            ?? throw new InvalidDataException($"The content index for '{summary.RelativePath}' is missing or invalid.");
        if (resolvedOnly && anonymousOnly) throw new ArgumentException("Resolved-only and anonymous-only extraction are mutually exclusive.");
        var entries = content.Files.Where(file => !file.IsMetadata && (!resolvedOnly || !IsAnonymous(file.ArchivePath)) && (!anonymousOnly || IsAnonymous(file.ArchivePath)) && MatchesFilter(file.ArchivePath, filter)).ToArray();
        var archivePath = Path.GetFullPath(Path.Combine(index.ClientRoot, summary.RelativePath));
        var relative = Path.GetRelativePath(index.ClientRoot, archivePath);
        if (relative.Equals("..", StringComparison.Ordinal) || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) throw new InvalidDataException("The indexed archive path escapes the client root.");
        destinationRoot = Path.GetFullPath(destinationRoot);
        var pending = overwrite ? entries : entries.Where(entry => !ExistingExtractionMatches(destinationRoot, entry)).ToArray();
        new PatchArchiveService().Extract(archivePath, destinationRoot, pending, progress, cancellationToken, workers: workers);
        return new(entries.Length, pending.Length, entries.Length - pending.Length);
    }

    private static bool ExistingExtractionMatches(string destinationRoot, MpqFileEntry entry)
    {
        var path = Path.GetFullPath(Path.Combine(destinationRoot, entry.ArchivePath.Replace('\\', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(destinationRoot, path);
        return !relative.Equals("..", StringComparison.Ordinal) && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && File.Exists(path) && new FileInfo(path).Length == entry.Size;
    }

    private static bool MatchesFilter(string archivePath, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        // FileSystemName treats backslashes as escape characters, so normalize archive paths to '/'.
        var normalizedPath = archivePath.Replace('\\', '/');
        var normalizedFilter = filter.Replace('\\', '/');
        return normalizedFilter.IndexOfAny(['*', '?']) >= 0
            ? FileSystemName.MatchesSimpleExpression(normalizedFilter, normalizedPath, ignoreCase: true)
            : normalizedPath.Contains(normalizedFilter, StringComparison.OrdinalIgnoreCase);
    }

    private static string? DetectActiveLocale(string root)
    {
        var config = Path.Combine(root, "WTF", "Config.wtf");
        if (File.Exists(config))
        {
            foreach (var line in File.ReadLines(config))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("SET locale", StringComparison.OrdinalIgnoreCase)) continue;
                var firstQuote = trimmed.IndexOf('"'); var lastQuote = trimmed.LastIndexOf('"');
                if (firstQuote >= 0 && lastQuote > firstQuote)
                {
                    var locale = trimmed[(firstQuote + 1)..lastQuote];
                    if (IsLocaleName(locale)) return locale;
                }
            }
        }
        var localeFolders = Directory.EnumerateDirectories(Path.Combine(root, "Data"), "*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).Where(IsLocaleName).ToArray();
        return localeFolders.Length == 1 ? localeFolders[0] : null;
    }

    private static ClientArchiveScope ClassifyArchive(string relativePath, string? activeLocale)
    {
        var parts = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 2) return ClientArchiveScope.RootData;
        var folder = parts[1];
        if (folder.StartsWith('_') || folder.Equals("backup", StringComparison.OrdinalIgnoreCase) || folder.Equals("backups", StringComparison.OrdinalIgnoreCase)) return ClientArchiveScope.Backup;
        if (folder.Equals("Cache", StringComparison.OrdinalIgnoreCase)) return ClientArchiveScope.Cache;
        if (IsLocaleName(folder)) return folder.Equals(activeLocale, StringComparison.OrdinalIgnoreCase) ? ClientArchiveScope.ActiveLocale : ClientArchiveScope.InactiveLocale;
        return ClientArchiveScope.CustomSubdirectory;
    }

    private static bool IsLocaleName(string? value) => value is { Length: 4 } && char.IsLower(value[0]) && char.IsLower(value[1]) && char.IsUpper(value[2]) && char.IsUpper(value[3]);

    private static void SaveSummary(string path, string root, string? activeLocale, ClientExecutableIndex? executable, IReadOnlyList<ClientArchiveSummary> archives, int totalArchives, bool complete)
        => WriteAtomic(path, new ClientArchiveIndex(IndexFormatVersion, root, Path.GetFileName(Path.TrimEndingDirectorySeparator(root)), DateTimeOffset.UtcNow, complete && archives.Count == totalArchives, archives.Count, activeLocale, executable, archives, null));

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
