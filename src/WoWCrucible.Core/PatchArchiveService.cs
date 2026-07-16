using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WoWCrucible.Core;

public sealed record PatchEntry(string SourcePath, string ArchivePath);
public sealed record MpqFileEntry(string ArchivePath, long Size, long CompressedSize, uint Flags, uint Locale)
{
    public bool IsMetadata => MpqPathClassifier.IsMetadata(ArchivePath);
}
public sealed record PatchPathAssessment(bool HasWarning, string Message);

public static class MpqPathClassifier
{
    public static bool IsMetadata(string path) => path.Equals("(listfile)", StringComparison.OrdinalIgnoreCase) || path.Equals("(attributes)", StringComparison.OrdinalIgnoreCase) || path.Equals("(signature)", StringComparison.OrdinalIgnoreCase);
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
        foreach (var input in paths)
        {
            var fullPath = Path.GetFullPath(input);
            if (File.Exists(fullPath))
            {
                var archivePath = Path.GetExtension(fullPath).Equals(".dbc", StringComparison.OrdinalIgnoreCase)
                    ? $"DBFilesClient\\{Path.GetFileName(fullPath)}"
                    : Path.GetFileName(fullPath);
                result[archivePath] = new(fullPath, archivePath);
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
                var archivePath = NormalizeArchivePath(relative);
                result[archivePath] = new(Path.GetFullPath(file), archivePath);
            }
        }
        return result.Values.OrderBy(entry => entry.ArchivePath, StringComparer.OrdinalIgnoreCase).ToArray();
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
    private const uint CreateListFile = 0x00100000;
    private const uint CreateArchiveV2 = 0x01000000;
    private const uint FileCompress = 0x00000200;
    private const uint FileReplaceExisting = 0x80000000;
    private const uint CompressionZlib = 0x02;

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
        if (!Native.SFileOpenArchive(Path.GetFullPath(archivePath), 0, 0, out var archive)) ThrowNative("open the MPQ archive");
        try { return Native.SFileHasFile(archive, PatchInputMapper.NormalizeArchivePath(internalPath)); }
        finally { Native.SFileCloseArchive(archive); }
    }

    public IReadOnlyList<MpqFileEntry> ListFiles(string archivePath, string mask = "*", string? externalListFile = null)
    {
        archivePath = Path.GetFullPath(archivePath);
        externalListFile = string.IsNullOrWhiteSpace(externalListFile) ? null : Path.GetFullPath(externalListFile);
        if (externalListFile is not null && !File.Exists(externalListFile)) throw new FileNotFoundException("The external MPQ listfile was not found.", externalListFile);
        if (!Native.SFileOpenArchive(archivePath, 0, 0, out var archive)) ThrowNative("open the MPQ archive");
        var result = new List<MpqFileEntry>();
        try
        {
            var data = new Native.SFileFindData();
            var find = Native.SFileFindFirstFile(archive, mask, ref data, externalListFile);
            if (find == IntPtr.Zero) return result;
            try
            {
                do
                {
                    if (!string.IsNullOrWhiteSpace(data.FileName))
                        result.Add(new(data.FileName, data.FileSize, data.CompressedSize, data.FileFlags, data.Locale));
                    data = new Native.SFileFindData();
                } while (Native.SFileFindNextFile(find, ref data));
            }
            finally { Native.SFileFindClose(find); }
        }
        finally { Native.SFileCloseArchive(archive); }
        return result.OrderBy(entry => entry.ArchivePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void Extract(string archivePath, string destinationRoot, IEnumerable<MpqFileEntry> files, IProgress<(int Done, int Total, string Path)>? progress = null, CancellationToken cancellationToken = default)
    {
        archivePath = Path.GetFullPath(archivePath);
        destinationRoot = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(destinationRoot);
        var entries = files.ToArray();
        if (!Native.SFileOpenArchive(archivePath, 0, 0, out var archive)) ThrowNative("open the MPQ archive");
        try
        {
            for (var index = 0; index < entries.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var internalPath = PatchInputMapper.NormalizeArchivePath(entries[index].ArchivePath);
                var destination = Path.GetFullPath(Path.Combine(destinationRoot, internalPath.Replace('\\', Path.DirectorySeparatorChar)));
                var relativeDestination = Path.GetRelativePath(destinationRoot, destination);
                if (relativeDestination.Equals("..", StringComparison.Ordinal) || relativeDestination.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Unsafe archive path: {internalPath}");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (!Native.SFileExtractFile(archive, internalPath, destination, 0)) ThrowNative($"extract '{internalPath}'");
                progress?.Report((index + 1, entries.Length, internalPath));
            }
        }
        finally { Native.SFileCloseArchive(archive); }
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
            if (!Native.SFileOpenArchive(tempPath, 0, 0, out archive)) ThrowNative("open the MPQ archive for updating");
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
    }
}
