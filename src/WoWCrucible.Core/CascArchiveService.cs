using System.ComponentModel;
using System.Runtime.InteropServices;

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
        var storage = OpenStorage(storagePath);
        var result = new List<CascFileEntry>();
        try
        {
            var data = new Native.CascFindData();
            var find = Native.CascFindFirstFile(storage, string.IsNullOrWhiteSpace(mask) ? "*" : mask, ref data, externalListFile);
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

    public void Extract(string storagePath, string destinationRoot, IEnumerable<CascFileEntry> files,
        IProgress<(int Done, int Total, string Path)>? progress = null, CancellationToken cancellationToken = default,
        bool overwriteExisting = true)
    {
        storagePath = ValidateStoragePath(storagePath);
        destinationRoot = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(destinationRoot);
        var entries = files.ToArray();
        var storage = OpenStorage(storagePath);
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

    private static void EnsureDescendant(string root, string destination, string internalPath)
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
