using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace WoWCrucible.Core;

public sealed record PatchEntry(string SourcePath, string ArchivePath);
public sealed record MpqFileEntry(string ArchivePath, long Size, long CompressedSize, uint Flags, uint Locale, uint BlockIndex = uint.MaxValue)
{
    public bool IsMetadata => MpqPathClassifier.IsMetadata(ArchivePath);
}
public sealed record PatchPathAssessment(bool HasWarning, string Message);

public static class MpqPathClassifier
{
    public static bool IsMetadata(string path) => path.Equals("(listfile)", StringComparison.OrdinalIgnoreCase) || path.Equals("(attributes)", StringComparison.OrdinalIgnoreCase) || path.Equals("(signature)", StringComparison.OrdinalIgnoreCase);
}

public static class MpqPathFilter
{
    public static bool Matches(string path, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        path = path.Replace('/', '\\'); filter = filter.Replace('/', '\\');
        if (!filter.Contains('*') && !filter.Contains('?')) return path.Contains(filter, StringComparison.OrdinalIgnoreCase);
        var pattern = "^" + Regex.Escape(filter).Replace("\\*\\*", ".*").Replace("\\*", "[^\\\\]*").Replace("\\?", "[^\\\\]") + "$";
        return Regex.IsMatch(path, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

public static class PatchInputMapper
{
    private static readonly HashSet<string> KnownClientRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "DBFilesClient", "Interface", "Character", "Creature", "World", "Textures", "Sound", "Fonts",
        "Cameras", "Dungeons", "Environments", "Item", "Particles", "Spells", "Tileset", "XTextures"
    };

    public static IReadOnlyList<PatchEntry> Map(IEnumerable<string> paths)
    {
        var result = new Dictionary<string, PatchEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in MapCandidates(paths)) result[entry.ArchivePath] = entry;
        return result.Values.OrderBy(entry => entry.ArchivePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<PatchEntry> MapCandidates(IEnumerable<string> paths)
    {
        var result = new List<PatchEntry>();
        foreach (var input in paths)
        {
            var fullPath = Path.GetFullPath(input);
            if (File.Exists(fullPath))
            {
                var archivePath = Path.GetExtension(fullPath).Equals(".dbc", StringComparison.OrdinalIgnoreCase)
                    ? $"DBFilesClient\\{Path.GetFileName(fullPath)}"
                    : Path.GetFileName(fullPath);
                result.Add(new(fullPath, archivePath));
                continue;
            }
            if (!Directory.Exists(fullPath)) continue;

            var selectedRootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath));
            foreach (var file in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(fullPath, file);
                var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var knownRoot = Array.FindIndex(parts, part => KnownClientRoots.Contains(part));
                if (knownRoot >= 0) relative = Path.Combine(parts[knownRoot..]);
                else if (KnownClientRoots.Contains(selectedRootName)) relative = Path.Combine(selectedRootName, relative);
                else if (Path.GetExtension(file).Equals(".dbc", StringComparison.OrdinalIgnoreCase)) relative = Path.Combine("DBFilesClient", Path.GetFileName(file));
                var archivePath = NormalizeArchivePath(relative);
                result.Add(new(Path.GetFullPath(file), archivePath));
            }
        }
        return result.OrderBy(entry => entry.ArchivePath, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.SourcePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string NormalizeArchivePath(string path)
    {
        var normalized = path.Replace('/', '\\').TrimStart('\\');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Split('\\').Any(part => part is "" or "." or ".."))
            throw new ArgumentException($"Invalid MPQ path: {path}", nameof(path));
        return normalized;
    }

    public static PatchPathAssessment AssessArchivePath(string path)
    {
        var normalized = NormalizeArchivePath(path);
        if (normalized.StartsWith("Interface\\GlueXML\\", StringComparison.OrdinalIgnoreCase) || normalized.Equals("Interface\\GlueXML", StringComparison.OrdinalIgnoreCase))
            return new(true, "Protected login GlueXML requires a compatible build-12340 executable; stock Wow.exe may reject it via GLUEXML.TOC.SIG");
        var root = normalized.Split('\\', 2)[0];
        if (KnownClientRoots.Contains(root)) return new(false, "Recognized client root");
        if (!normalized.Contains('\\')) return new(true, "Top-level file; verify that WoW expects it at the MPQ root");
        return new(true, $"Unrecognized client root '{root}'; verify the archive path");
    }
}

public sealed class PatchArchiveService
{
    public const long MaximumSafeUpdateBytes = 2L * 1024 * 1024 * 1024;
    public const int MaximumExtractionWorkers = 16;
    public static int RecommendedExtractionWorkers => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
    private const uint CreateListFile = 0x00100000;
    private const uint CreateArchiveV2 = 0x01000000;
    private const uint FileCompress = 0x00000200;
    private const uint FileReplaceExisting = 0x80000000;
    private const uint CompressionZlib = 0x02;
    private const uint OpenReadOnly = 0x00000100;
    private static readonly object LegacyLocaleExtractionLock = new();
    private sealed record ExtractionItem(MpqFileEntry Entry, string InternalPath, string OutputPath, string Destination, string LookupPath);

    public void Create(string outputPath, IEnumerable<PatchEntry> sourceEntries)
    {
        var entries = sourceEntries.Select(entry => entry with { SourcePath = Path.GetFullPath(entry.SourcePath), ArchivePath = PatchInputMapper.NormalizeArchivePath(entry.ArchivePath) }).ToArray();
        if (entries.Length == 0) throw new InvalidOperationException("Add at least one file to the patch.");
        if (entries.Any(entry => !File.Exists(entry.SourcePath))) throw new FileNotFoundException("One or more patch source files no longer exist.");
        var duplicate = entries.GroupBy(entry => entry.ArchivePath, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidOperationException($"Duplicate MPQ path: {duplicate.Key}");

        outputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var tempPath = outputPath + ".tmp";
        File.Delete(tempPath);
        IntPtr archive = IntPtr.Zero;
        try
        {
            if (!Native.SFileCreateArchive(tempPath, CreateListFile | CreateArchiveV2, (uint)Math.Max(16, entries.Length + 8), out archive))
                ThrowNative("create the MPQ archive");
            foreach (var entry in entries)
            {
                if (!Native.SFileAddFileEx(archive, entry.SourcePath, entry.ArchivePath, FileCompress | FileReplaceExisting, CompressionZlib, 0xFFFFFFFF))
                    ThrowNative($"add '{entry.ArchivePath}'");
            }
            if (!Native.SFileCloseArchive(archive)) ThrowNative("finalize the MPQ archive");
            archive = IntPtr.Zero;
            if (File.Exists(outputPath)) File.Copy(outputPath, outputPath + ".bak", true);
            File.Move(tempPath, outputPath, true);
        }
        finally
        {
            if (archive != IntPtr.Zero) Native.SFileCloseArchive(archive);
            File.Delete(tempPath);
        }
    }

    public bool Contains(string archivePath, string internalPath)
    {
        var archive = OpenArchiveWithRetry(Path.GetFullPath(archivePath), "open the MPQ archive");
        try { return Native.SFileHasFile(archive, PatchInputMapper.NormalizeArchivePath(internalPath)); }
        finally { Native.SFileCloseArchive(archive); }
    }

    public IReadOnlyList<MpqFileEntry> ListFiles(string archivePath, string mask = "*", string? externalListFile = null)
    {
        archivePath = Path.GetFullPath(archivePath);
        externalListFile = string.IsNullOrWhiteSpace(externalListFile) ? null : Path.GetFullPath(externalListFile);
        if (externalListFile is not null && !File.Exists(externalListFile)) throw new FileNotFoundException("The external MPQ listfile was not found.", externalListFile);
        var archive = OpenArchiveWithRetry(archivePath, "open the MPQ archive");
        string? embeddedListFile = null;
        try
        {
            var result = EnumerateFiles(archive, mask, externalListFile);
            if (externalListFile is null && mask == "*" && result.Any(entry => ClientArchiveIndexService.IsAnonymous(entry.ArchivePath)) && result.Any(entry => entry.ArchivePath.Equals("(listfile)", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    embeddedListFile = Path.Combine(Path.GetTempPath(), $"wow-crucible-embedded-listfile-{Environment.ProcessId}-{Guid.NewGuid():N}.txt");
                    bool extracted;
                    lock (LegacyLocaleExtractionLock) { Native.SFileSetLocale(0); extracted = Native.SFileExtractFile(archive, "(listfile)", embeddedListFile, 0); }
                    if (extracted && File.Exists(embeddedListFile) && new FileInfo(embeddedListFile).Length > 0)
                    {
                        var recovered = EnumerateFiles(archive, mask, embeddedListFile);
                        var anonymousBefore = result.Count(entry => ClientArchiveIndexService.IsAnonymous(entry.ArchivePath));
                        var anonymousAfter = recovered.Count(entry => ClientArchiveIndexService.IsAnonymous(entry.ArchivePath));
                        if (anonymousAfter < anonymousBefore && HasSamePhysicalEntries(result, recovered)) result = recovered;
                    }
                }
                catch (Exception) { /* Embedded metadata recovery is best-effort; retain the complete anonymous physical index. */ }
            }
            return result.OrderBy(entry => entry.ArchivePath, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally
        {
            Native.SFileCloseArchive(archive);
            if (embeddedListFile is not null) try { File.Delete(embeddedListFile); } catch { }
        }
    }

    private static List<MpqFileEntry> EnumerateFiles(IntPtr archive, string mask, string? listFile)
    {
        var result = new List<MpqFileEntry>();
        var data = new Native.SFileFindData();
        var find = Native.SFileFindFirstFile(archive, mask, ref data, listFile);
        if (find == IntPtr.Zero) return result;
        try
        {
            do
            {
                if (!string.IsNullOrWhiteSpace(data.FileName)) result.Add(new(data.FileName, data.FileSize, data.CompressedSize, data.FileFlags, data.Locale, data.BlockIndex));
                data = new Native.SFileFindData();
            } while (Native.SFileFindNextFile(find, ref data));
        }
        finally { Native.SFileFindClose(find); }
        return result;
    }

    private static bool HasSamePhysicalEntries(IReadOnlyList<MpqFileEntry> left, IReadOnlyList<MpqFileEntry> right)
    {
        if (left.Count != right.Count) return false;
        static Dictionary<(uint Block, uint Locale, long Size, long CompressedSize, uint Flags), int> Counts(IReadOnlyList<MpqFileEntry> entries)
        {
            var counts = new Dictionary<(uint, uint, long, long, uint), int>();
            foreach (var entry in entries)
            {
                var identity = (entry.BlockIndex, entry.Locale, entry.Size, entry.CompressedSize, entry.Flags);
                counts.TryGetValue(identity, out var count); counts[identity] = count + 1;
            }
            return counts;
        }
        var expected = Counts(left); var actual = Counts(right);
        return expected.Count == actual.Count && expected.All(pair => actual.TryGetValue(pair.Key, out var count) && count == pair.Value);
    }

    public void Extract(string archivePath, string destinationRoot, IEnumerable<MpqFileEntry> files, IProgress<(int Done, int Total, string Path)>? progress = null, CancellationToken cancellationToken = default,
        bool overwriteExisting = true, bool preserveLocaleVariants = false, Action<MpqFileEntry, Exception>? extractionFailure = null, int workers = 0)
    {
        if (workers is < 0 or > MaximumExtractionWorkers) throw new ArgumentOutOfRangeException(nameof(workers), $"Extraction workers must be auto (0) or from 1 to {MaximumExtractionWorkers}.");
        archivePath = Path.GetFullPath(archivePath);
        destinationRoot = Path.GetFullPath(destinationRoot);
        var entries = files.ToArray();
        var normalizedPaths = entries.Select(entry => PatchInputMapper.NormalizeArchivePath(entry.ArchivePath)).ToArray();
        var duplicatePaths = preserveLocaleVariants
            ? normalizedPaths.GroupBy(path => path, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var work = new List<ExtractionItem>(entries.Length);
        for (var index = 0; index < entries.Length; index++)
        {
            var internalPath = normalizedPaths[index];
            var outputPath = internalPath;
            if (duplicatePaths.Contains(internalPath))
            {
                occurrences.TryGetValue(internalPath, out var occurrence); occurrences[internalPath] = ++occurrence;
                var extension = Path.GetExtension(internalPath); var stem = internalPath[..^extension.Length];
                outputPath = $"{stem}.locale-{entries[index].Locale:X4}.variant-{occurrence:D2}{extension}";
            }
            var destination = Path.GetFullPath(Path.Combine(destinationRoot, outputPath.Replace('\\', Path.DirectorySeparatorChar)));
            var relativeDestination = Path.GetRelativePath(destinationRoot, destination);
            if (relativeDestination.Equals("..", StringComparison.Ordinal) || relativeDestination.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                throw new InvalidOperationException($"Unsafe archive path: {internalPath}");
            var extensionForLookup = Path.GetExtension(internalPath);
            if (string.IsNullOrEmpty(extensionForLookup)) extensionForLookup = ".bin";
            var lookupPath = entries[index].BlockIndex <= 99_999_999 ? $"File{entries[index].BlockIndex:D8}{extensionForLookup}" : internalPath;
            work.Add(new(entries[index], internalPath, outputPath, destination, lookupPath));
        }

        Directory.CreateDirectory(destinationRoot);
        var done = 0;
        var progressLock = new object();
        void Report(ExtractionItem item)
        {
            var completed = Interlocked.Increment(ref done);
            if (progress is not null) lock (progressLock) progress.Report((completed, entries.Length, item.OutputPath));
        }

        var pending = new List<ExtractionItem>(work.Count);
        foreach (var item in work)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!overwriteExisting && File.Exists(item.Destination)) Report(item);
            else pending.Add(item);
        }
        if (pending.Count == 0) return;

        // Entries sharing one output path intentionally remain ordered on one worker. This preserves
        // the long-standing "last selected locale wins" behavior when variants are not preserved.
        var groups = pending.GroupBy(item => item.Destination, StringComparer.OrdinalIgnoreCase).Select(group => group.ToArray()).ToArray();
        var workerCount = Math.Min(groups.Length, workers == 0 ? RecommendedExtractionWorkers : workers);
        var archives = new IntPtr[workerCount];
        try
        {
            for (var index = 0; index < archives.Length; index++) archives[index] = OpenArchiveWithRetry(archivePath, "open the MPQ archive for parallel extraction");
            var nextGroup = -1;
            ExceptionDispatchInfo? fatal = null;
            var failureLock = new object();
            Parallel.For(0, workerCount, new ParallelOptions { MaxDegreeOfParallelism = workerCount }, workerIndex =>
            {
                try
                {
                    while (!cancellationToken.IsCancellationRequested && Volatile.Read(ref fatal) is null)
                    {
                        var groupIndex = Interlocked.Increment(ref nextGroup);
                        if (groupIndex >= groups.Length) break;
                        foreach (var item in groups[groupIndex])
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                ExtractFileAtomically(item.Destination, overwriteExisting,
                                    temporary => ExtractNative(archives[workerIndex], item, temporary),
                                    () => ThrowNative($"extract '{item.InternalPath}'"));
                            }
                            catch (Exception exception) when (extractionFailure is not null)
                            {
                                lock (failureLock) extractionFailure(item.Entry, exception);
                            }
                            Report(item);
                        }
                    }
                }
                catch (Exception exception)
                {
                    lock (failureLock) fatal ??= ExceptionDispatchInfo.Capture(exception);
                }
            });
            cancellationToken.ThrowIfCancellationRequested();
            fatal?.Throw();
        }
        finally { foreach (var archive in archives) if (archive != IntPtr.Zero) Native.SFileCloseArchive(archive); }
    }

    private static bool ExtractNative(IntPtr archive, ExtractionItem item, string temporary)
    {
        if (!item.LookupPath.Equals(item.InternalPath, StringComparison.Ordinal))
            return Native.SFileExtractFile(archive, item.LookupPath, temporary, 0);
        lock (LegacyLocaleExtractionLock)
        {
            Native.SFileSetLocale(item.Entry.Locale);
            return Native.SFileExtractFile(archive, item.InternalPath, temporary, 0);
        }
    }

    internal IReadOnlyList<(MpqFileEntry Entry, string FilePath)> ExtractFlat(string archivePath, string destinationRoot, IEnumerable<MpqFileEntry> files, CancellationToken cancellationToken = default)
    {
        archivePath = Path.GetFullPath(archivePath); destinationRoot = Path.GetFullPath(destinationRoot); Directory.CreateDirectory(destinationRoot); var entries = files.ToArray(); var result = new List<(MpqFileEntry, string)>(entries.Length);
        var archive = OpenArchiveWithRetry(archivePath, "open the MPQ archive");
        try
        {
            for (var index = 0; index < entries.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested(); Native.SFileSetLocale(entries[index].Locale); var internalPath = PatchInputMapper.NormalizeArchivePath(entries[index].ArchivePath); var destination = Path.Combine(destinationRoot, $"{index:X8}.bin");
                ExtractFileAtomically(destination, overwriteExisting: false, temporary => Native.SFileExtractFile(archive, internalPath, temporary, 0), () => ThrowNative($"extract '{internalPath}'")); result.Add((entries[index], destination));
            }
        }
        finally { Native.SFileCloseArchive(archive); }
        return result;
    }

    internal static void ExtractFileAtomically(string destination, bool overwriteExisting, Func<string, bool> extractToTemporary, Action throwExtractionError)
    {
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + $".{Environment.ProcessId}.{Guid.NewGuid():N}.extracting";
        try
        {
            if (!extractToTemporary(temporary))
            {
                throwExtractionError();
                throw new IOException($"Archive extraction failed without reporting an operating-system error: {destination}");
            }
            if (!File.Exists(temporary)) throw new IOException($"Archive extraction reported success but created no file: {destination}");
            File.Move(temporary, destination, overwriteExisting);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw;
        }
    }

    public void Update(string archivePath, IEnumerable<PatchEntry> sourceEntries)
    {
        archivePath = Path.GetFullPath(archivePath);
        if (!File.Exists(archivePath)) throw new FileNotFoundException("The MPQ patch does not exist.", archivePath);
        if (new FileInfo(archivePath).Length > MaximumSafeUpdateBytes)
            throw new InvalidOperationException($"Refusing to copy-update this {new FileInfo(archivePath).Length / (1024d * 1024 * 1024):0.##} GB archive. Build a small manifest-driven patch MPQ instead; large source layers must remain immutable.");
        var entries = ValidateEntries(sourceEntries);
        var tempPath = archivePath + ".tmp";
        File.Copy(archivePath, tempPath, true);
        IntPtr archive = IntPtr.Zero;
        try
        {
            archive = OpenArchiveWithRetry(tempPath, "open the MPQ archive for updating", 0);
            foreach (var entry in entries)
                if (!Native.SFileAddFileEx(archive, entry.SourcePath, entry.ArchivePath, FileCompress | FileReplaceExisting, CompressionZlib, 0xFFFFFFFF))
                    ThrowNative($"add '{entry.ArchivePath}'");
            if (!Native.SFileCloseArchive(archive)) ThrowNative("finalize the updated MPQ archive");
            archive = IntPtr.Zero;
            File.Copy(archivePath, archivePath + ".bak", true);
            File.Move(tempPath, archivePath, true);
        }
        finally
        {
            if (archive != IntPtr.Zero) Native.SFileCloseArchive(archive);
            File.Delete(tempPath);
        }
    }

    private static PatchEntry[] ValidateEntries(IEnumerable<PatchEntry> sourceEntries)
    {
        var entries = sourceEntries.Select(entry => entry with { SourcePath = Path.GetFullPath(entry.SourcePath), ArchivePath = PatchInputMapper.NormalizeArchivePath(entry.ArchivePath) }).ToArray();
        if (entries.Length == 0) throw new InvalidOperationException("Add at least one file to the patch.");
        if (entries.Any(entry => !File.Exists(entry.SourcePath))) throw new FileNotFoundException("One or more patch source files no longer exist.");
        var duplicate = entries.GroupBy(entry => entry.ArchivePath, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidOperationException($"Duplicate MPQ path: {duplicate.Key}");
        return entries;
    }

    private static void ThrowNative(string operation) => throw new Win32Exception(Marshal.GetLastWin32Error(), $"StormLib could not {operation}");

    private static IntPtr OpenArchiveWithRetry(string archivePath, string operation, uint flags = OpenReadOnly)
    {
        const int attempts = 3;
        var error = 0;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (Native.SFileOpenArchive(archivePath, 0, flags, out var archive)) return archive;
            error = Marshal.GetLastWin32Error();
            if (attempt < attempts) Thread.Sleep(attempt * 150);
        }
        throw new Win32Exception(error, $"StormLib could not {operation} after {attempts} attempts: {archivePath}");
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        internal struct SFileFindData
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
            public IntPtr PlainName;
            public uint HashIndex;
            public uint BlockIndex;
            public uint FileSize;
            public uint FileFlags;
            public uint CompressedSize;
            public uint FileTimeLo;
            public uint FileTimeHi;
            public uint Locale;
        }

        [DllImport("StormLib.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool SFileCreateArchive(string archiveName, uint createFlags, uint maxFileCount, out IntPtr archive);

        [DllImport("StormLib.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool SFileOpenArchive(string archiveName, uint priority, uint flags, out IntPtr archive);

        [DllImport("StormLib.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool SFileCloseArchive(IntPtr archive);

        [DllImport("StormLib.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool SFileAddFileEx(IntPtr archive, string sourcePath, [MarshalAs(UnmanagedType.LPStr)] string archivePath, uint flags, uint compression, uint nextCompression);

        [DllImport("StormLib.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool SFileHasFile(IntPtr archive, string archivePath);

        [DllImport("StormLib.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        internal static extern IntPtr SFileFindFirstFile(IntPtr archive, string mask, ref SFileFindData findData, [MarshalAs(UnmanagedType.LPTStr)] string? listFile);

        [DllImport("StormLib.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool SFileFindNextFile(IntPtr find, ref SFileFindData findData);

        [DllImport("StormLib.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool SFileFindClose(IntPtr find);

        [DllImport("StormLib.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool SFileExtractFile(IntPtr archive, [MarshalAs(UnmanagedType.LPStr)] string internalPath, string destinationPath, uint searchScope);

        [DllImport("StormLib.dll", SetLastError = true)]
        internal static extern uint SFileSetLocale(uint locale);
    }
}
