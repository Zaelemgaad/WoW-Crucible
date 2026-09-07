using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WoWCrucible.Core;

internal sealed record NativeFileIdentity(uint VolumeSerial, ulong FileId, uint LinkCount)
{
    public string VolumeSerialHex => VolumeSerial.ToString("X8");
    public string FileIdHex => FileId.ToString("X16");
    public string Key => $"{VolumeSerialHex}:{FileIdHex}";
}

internal static class NativeHardLinks
{
    private const int ErrorHandleEof = 38;
    private const int ErrorInvalidParameter = 87;
    private static readonly IntPtr InvalidHandle = new(-1);

    public static NativeFileIdentity ReadIdentity(string path)
    {
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, FileOptions.None);
        if (!GetFileInformationByHandle(handle, out var information))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not read NTFS file identity: {path}");
        var fileId = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        return new(information.VolumeSerialNumber, fileId, information.NumberOfLinks);
    }

    public static IReadOnlyList<string> ReadAlternateStreamNames(string path)
    {
        var handle = FindFirstStreamW(NativePath(path), StreamInfoLevels.FindStreamInfoStandard, out var data, 0);
        if (handle == InvalidHandle)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is ErrorHandleEof or ErrorInvalidParameter) return [];
            throw new Win32Exception(error, $"Could not inspect alternate data streams: {path}");
        }
        var result = new List<string>();
        try
        {
            do
            {
                if (!string.Equals(data.StreamName, "::$DATA", StringComparison.OrdinalIgnoreCase))
                    result.Add(data.StreamName);
            }
            while (FindNextStreamW(handle, out data));
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorHandleEof)
                throw new Win32Exception(error, $"Could not finish alternate data stream inspection: {path}");
            return result;
        }
        finally { FindClose(handle); }
    }

    public static void ReplaceWithHardLink(string targetPath, string canonicalPath)
    {
        var temporary = targetPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.linking";
        try
        {
            if (!CreateHardLinkW(NativePath(temporary), NativePath(canonicalPath), IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not create temporary hard link for {targetPath}");
            var expected = ReadIdentity(canonicalPath);
            var actual = ReadIdentity(temporary);
            if (expected.Key != actual.Key)
                throw new IOException("Temporary hard link did not share the canonical NTFS file identity.");
            File.Move(temporary, targetPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void Create(string newPath, string existingPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(newPath))!);
        if (!CreateHardLinkW(NativePath(newPath), NativePath(existingPath), IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not create hard link {newPath} -> {existingPath}");
        var expected = ReadIdentity(existingPath);
        var actual = ReadIdentity(newPath);
        if (expected.Key != actual.Key)
        {
            File.Delete(newPath);
            throw new IOException("Created path did not share the expected NTFS file identity.");
        }
    }

    private static string NativePath(string path)
    {
        path = Path.GetFullPath(path);
        if (path.StartsWith("\\\\?\\", StringComparison.Ordinal)) return path;
        return path.StartsWith("\\\\", StringComparison.Ordinal)
            ? "\\\\?\\UNC\\" + path[2..]
            : "\\\\?\\" + path;
    }

    private enum StreamInfoLevels { FindStreamInfoStandard }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindStreamData
    {
        public long StreamSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string StreamName;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstStreamW(string fileName, StreamInfoLevels informationLevel,
        out Win32FindStreamData findStreamData, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStreamW(IntPtr findStream, out Win32FindStreamData findStreamData);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr findFile);
}
