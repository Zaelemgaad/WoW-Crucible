using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WoWCrucible.Core;

public enum CascEntryNameType
{
    FullPath,
    FileDataId,
    ContentKey,
    EncodedKey
}

public sealed record CascFileEntry(
    string ArchivePath,
    long Size,
    uint FileDataId,
    uint Locale,
    uint ContentFlags,
    bool IsAvailableLocally,
    CascEntryNameType NameType,
    string ContentKey,
    string EncodedKey);

public sealed record CascPathProbe(
    uint FileDataId,
    string ArchivePath,
    bool IsAvailableLocally,
    long Size,
    int? NativeError);

/// <summary>
/// Read-only CASC storage access backed by the pinned, MIT-licensed CascLib native provider.
/// The service deliberately exposes no online-download behavior and never mutates a client.
/// </summary>
public sealed class CascArchiveService
{
    private const uint AllLocales = 0xFFFFFFFF;
    private const int BufferSize = 1024 * 1024;
    private static readonly IntPtr InvalidHandle = new(-1);

    public static bool IsNativeProviderAvailable()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) return false;
        try { _ = Native.GetCascError(); return true; }
        catch (DllNotFoundException) { return false; }
        catch (BadImageFormatException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    public IReadOnlyList<CascFileEntry> ListFiles(string storagePath, string mask = "*", string? externalListFile = null, CancellationToken cancellationToken = default)
    {
        storagePath = ValidateStoragePath(storagePath);
        externalListFile = ValidateListFile(externalListFile);
        IntPtr storage;
        try { storage = OpenStorage(storagePath); }
        catch (Exception nativeException) when (externalListFile is not null && IsNativeStorageOpenFailure(nativeException))
        {
            try { return TactSharpCascStorage.Open(storagePath).ListFiles(externalListFile!, mask, cancellationToken); }
            catch (Exception fallbackException) when (fallbackException is not OperationCanceledException)
            {
                throw new AggregateException("CascLib could not open this installation, and Crucible's local root/index fallback also failed.", nativeException, fallbackException);
            }
        }
        var result = new List<CascFileEntry>();
        try
        {
            var data = new Native.CascFindData();
            var nativeListFile = externalListFile is null ? null : PrepareListFileForCascLib(externalListFile, CruciblePaths.CascListfileCacheDirectory, cancellationToken);
            var find = Native.CascFindFirstFile(storage, string.IsNullOrWhiteSpace(mask) ? "*" : mask, ref data, nativeListFile);
            if (find == InvalidHandle) return result;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!string.IsNullOrWhiteSpace(data.FileName)) result.Add(ToEntry(data));
                    data = new Native.CascFindData();
                    if (!Native.CascFindNextFile(find, ref data)) break;
                }
            }
            finally { Native.CascFindClose(find); }
        }
        finally { Native.CascCloseStorage(storage); }
        return result.OrderBy(entry => entry.ArchivePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Probes a bounded set of exact CASC paths without enumerating the entire root.
    /// This is the correct path for FileDataID bridges that already resolved IDs to
    /// names through a listfile and only need to know which payloads exist locally.
    /// </summary>
    public IReadOnlyList<CascPathProbe> ProbePaths(
        string storagePath,
        IEnumerable<FileDataIdPath> paths,
        CancellationToken cancellationToken = default)
    {
        storagePath = ValidateStoragePath(storagePath);
        ArgumentNullException.ThrowIfNull(paths);
        var requested = paths
            .Select(value => new FileDataIdPath(value.FileDataId, PatchInputMapper.NormalizeArchivePath(value.ClientPath)))
            .DistinctBy(value => (value.FileDataId, value.ClientPath.ToUpperInvariant()))
            .OrderBy(value => value.FileDataId)
            .ThenBy(value => value.ClientPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var result = new List<CascPathProbe>(requested.Length);
        var storage = OpenStorage(storagePath);
        try
        {
            foreach (var requestedPath in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IntPtr file = IntPtr.Zero;
                try
                {
                    if (!Native.CascOpenFile(storage, requestedPath.ClientPath, AllLocales, 0, out file))
                    {
                        result.Add(new(requestedPath.FileDataId, requestedPath.ClientPath, false, 0, unchecked((int)Native.GetCascError())));
                        continue;
                    }
                    if (!Native.CascGetFileSize64(file, out var nativeSize))
                    {
                        result.Add(new(requestedPath.FileDataId, requestedPath.ClientPath, false, 0, unchecked((int)Native.GetCascError())));
                        continue;
                    }
                    if (nativeSize > long.MaxValue) throw new IOException($"CASC file is too large for this process: {requestedPath.ClientPath}");
                    result.Add(new(requestedPath.FileDataId, requestedPath.ClientPath, true, checked((long)nativeSize), null));
                }
                finally
                {
                    if (file != IntPtr.Zero) Native.CascCloseFile(file);
                }
            }
        }
        finally { Native.CascCloseStorage(storage); }
        return result;
    }

    public void Extract(string storagePath, string destinationRoot, IEnumerable<CascFileEntry> files,
        IProgress<(int Done, int Total, string Path)>? progress = null, CancellationToken cancellationToken = default,
        bool overwriteExisting = true)
    {
        storagePath = ValidateStoragePath(storagePath);
        destinationRoot = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(destinationRoot);
        var entries = files.ToArray();
        IntPtr storage;
        try { storage = OpenStorage(storagePath); }
        catch (Exception nativeException) when (IsNativeStorageOpenFailure(nativeException))
        {
            try
            {
                TactSharpCascStorage.Open(storagePath).Extract(destinationRoot, entries, progress, cancellationToken, overwriteExisting);
                return;
            }
            catch (Exception fallbackException) when (fallbackException is not OperationCanceledException)
            {
                throw new AggregateException("CascLib could not open this installation, and Crucible's local root/index fallback also failed.", nativeException, fallbackException);
            }
        }
        try
        {
            for (var index = 0; index < entries.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var internalPath = PatchInputMapper.NormalizeArchivePath(entries[index].ArchivePath);
                var destination = Path.GetFullPath(Path.Combine(destinationRoot, internalPath.Replace('\\', Path.DirectorySeparatorChar)));
                EnsureDescendant(destinationRoot, destination, internalPath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (!overwriteExisting && File.Exists(destination)) { progress?.Report((index + 1, entries.Length, internalPath)); continue; }
                ExtractFileAtomically(storage, internalPath, destination, entries[index].Locale, overwriteExisting, cancellationToken);
                progress?.Report((index + 1, entries.Length, internalPath));
            }
        }
        finally { Native.CascCloseStorage(storage); }
    }

    internal static int NativeFindDataSize => Marshal.SizeOf<Native.CascFindData>();

    private static void ExtractFileAtomically(IntPtr storage, string internalPath, string destination, uint locale, bool overwriteExisting, CancellationToken cancellationToken)
    {
        var temporary = destination + $".{Environment.ProcessId}.{Guid.NewGuid():N}.extracting";
        IntPtr file = IntPtr.Zero;
        try
        {
            if (!Native.CascOpenFile(storage, internalPath, locale == 0 ? AllLocales : locale, 0, out file)) ThrowNative($"open '{internalPath}'");
            if (!Native.CascGetFileSize64(file, out var nativeSize)) ThrowNative($"read the size of '{internalPath}'");
            if (nativeSize > long.MaxValue) throw new IOException($"CASC file is too large for this process: {internalPath}");
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan))
            {
                var buffer = new byte[BufferSize];
                ulong remaining = nativeSize;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var requested = (uint)Math.Min((ulong)buffer.Length, remaining);
                    if (!Native.CascReadFile(file, buffer, requested, out var read)) ThrowNative($"read '{internalPath}'");
                    if (read == 0) throw new EndOfStreamException($"CASC returned no data before the declared end of '{internalPath}'.");
                    output.Write(buffer, 0, checked((int)read));
                    remaining -= read;
                }
                output.Flush(true);
            }
            File.Move(temporary, destination, overwriteExisting);
        }
        finally
        {
            if (file != IntPtr.Zero) Native.CascCloseFile(file);
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static CascFileEntry ToEntry(Native.CascFindData data) => new(
        data.FileName.Replace('/', '\\'), checked((long)data.FileSize), data.FileDataId, data.LocaleFlags, data.ContentFlags,
        (data.AvailabilityBits & 1) != 0, (CascEntryNameType)data.NameType,
        Convert.ToHexString(data.ContentKey ?? []), Convert.ToHexString(data.EncodedKey ?? []));

    private static IntPtr OpenStorage(string storagePath)
    {
        EnsureProvider();
        if (!Native.CascOpenStorage(storagePath, AllLocales, out var storage)) ThrowNative($"open CASC storage '{storagePath}'");
        return storage;
    }

    private static string ValidateStoragePath(string storagePath)
    {
        if (string.IsNullOrWhiteSpace(storagePath)) throw new ArgumentException("Choose a CASC client/storage folder.", nameof(storagePath));
        var fullPath = Path.GetFullPath(storagePath);
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException($"CASC storage folder not found: {fullPath}");
        return fullPath;
    }

    private static string? ValidateListFile(string? listFile)
    {
        if (string.IsNullOrWhiteSpace(listFile)) return null;
        var fullPath = Path.GetFullPath(listFile);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The external CASC listfile was not found.", fullPath);
        return fullPath;
    }

    /// <summary>
    /// CascLib's native CascFindFirstFile expects a plain listfile: one WoW-internal
    /// path per line, no leading identifier. The modern community-standard listfile
    /// (wow.tools style, e.g. "132492;interface\icons\inv_belt_03.blp") instead prefixes
    /// every line with "FileDataId;". Fed directly to CascLib, every line is hashed as one
    /// literal (and invalid) path, so nothing resolves and the storage silently behaves as
    /// if no listfile were supplied at all. Normalize either format into a plain listfile
    /// CascLib can actually match. Keep the original mapping intact for the TACTSharp
    /// fallback, and cache the native-only copy in Crucible's own portable cache.
    /// </summary>
    internal static string PrepareListFileForCascLib(string sourcePath, string cacheDirectory, CancellationToken cancellationToken = default)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("The external CASC listfile was not found.", sourcePath);
        var info = new FileInfo(sourcePath);
        var sourceKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath.ToUpperInvariant()))).ToLowerInvariant();
        cacheDirectory = Path.GetFullPath(cacheDirectory);
        Directory.CreateDirectory(cacheDirectory);
        var cachePath = Path.Combine(cacheDirectory, $"{sourceKey}-{info.Length}-{info.LastWriteTimeUtc.Ticks}.txt");
        if (File.Exists(cachePath)) return cachePath;

        var temporary = cachePath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var reader = new StreamReader(sourcePath))
            using (var writer = new StreamWriter(new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None), new UTF8Encoding(false)))
            {
                while (reader.ReadLine() is { } line)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    writer.WriteLine(FileDataIdListfileService.TryParseMapping(line, out var mapping) ? mapping.ClientPath : line.Trim());
                }
            }
            try { File.Move(temporary, cachePath, overwrite: false); }
            catch (IOException) when (File.Exists(cachePath)) { }
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }

        // Best-effort cleanup of stale caches from earlier versions of the same source file.
        foreach (var stale in Directory.EnumerateFiles(cacheDirectory, $"{sourceKey}-*.txt"))
            if (!string.Equals(stale, cachePath, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(stale); } catch { }

        return cachePath;
    }

    internal static void EnsureDescendant(string root, string destination, string internalPath)
    {
        var relative = Path.GetRelativePath(root, destination);
        if (relative.Equals("..", StringComparison.Ordinal) || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsafe CASC path: {internalPath}");
    }

    private static void EnsureProvider()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new PlatformNotSupportedException("Crucible's CascLib provider currently requires 64-bit Windows.");
        if (!IsNativeProviderAvailable()) throw new DllNotFoundException("CascLib.dll is missing. Reinstall the complete Crucible package; the native provider must remain beside the executable.");
    }

    private static bool IsNativeStorageOpenFailure(Exception exception) =>
        exception is Win32Exception or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or PlatformNotSupportedException;

    private static void ThrowNative(string operation)
    {
        var error = unchecked((int)Native.GetCascError());
        throw new Win32Exception(error, $"CascLib could not {operation}");
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        internal struct CascFindData
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] ContentKey;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] EncodedKey;
            public ulong TagBitMask;
            public ulong FileSize;
            public IntPtr PlainName;
            public uint FileDataId;
            public uint LocaleFlags;
            public uint ContentFlags;
            public uint SpanCount;
            public uint AvailabilityBits;
            public int NameType;
        }

        [DllImport("CascLib.dll", CharSet = CharSet.Ansi)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool CascOpenStorage(string storagePath, uint localeMask, out IntPtr storage);

        [DllImport("CascLib.dll")]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool CascCloseStorage(IntPtr storage);

        [DllImport("CascLib.dll", CharSet = CharSet.Ansi)]
        internal static extern IntPtr CascFindFirstFile(IntPtr storage, string mask, ref CascFindData findData, string? listFile);

        [DllImport("CascLib.dll")]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool CascFindNextFile(IntPtr find, ref CascFindData findData);

        [DllImport("CascLib.dll")]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool CascFindClose(IntPtr find);

        [DllImport("CascLib.dll", CharSet = CharSet.Ansi)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool CascOpenFile(IntPtr storage, string fileName, uint localeFlags, uint openFlags, out IntPtr file);

        [DllImport("CascLib.dll")]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool CascGetFileSize64(IntPtr file, out ulong size);

        [DllImport("CascLib.dll")]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool CascReadFile(IntPtr file, [Out] byte[] buffer, uint bytesToRead, out uint bytesRead);

        [DllImport("CascLib.dll")]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool CascCloseFile(IntPtr file);

        [DllImport("CascLib.dll")]
        internal static extern uint GetCascError();
    }
}
