using System.Text;

namespace WoWCrucible.Core;

public sealed class RollingTextLogWriter : IDisposable
{
    public const long DefaultMaximumFileBytes = 32 * 1024 * 1024;
    public const int DefaultRetainedFiles = 4;
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private readonly string _path;
    private readonly long _maximumFileBytes;
    private readonly int _retainedFiles;
    private readonly bool _autoFlush;
    private FileStream? _stream;
    private long _writtenBytes;
    private bool _disposed;

    public RollingTextLogWriter(string path, long maximumFileBytes = DefaultMaximumFileBytes,
        int retainedFiles = DefaultRetainedFiles, bool autoFlush = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFileBytes, 4);
        ArgumentOutOfRangeException.ThrowIfLessThan(retainedFiles, 1);
        _path = Path.GetFullPath(path);
        _maximumFileBytes = maximumFileBytes;
        _retainedFiles = retainedFiles;
        _autoFlush = autoFlush;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public void Write(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var bytes = Utf8.GetBytes(text);
        var offset = 0;
        while (offset < bytes.Length)
        {
            if (_stream is null)
            {
                _stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read,
                    64 * 1024, FileOptions.SequentialScan);
                _writtenBytes = _stream.Length;
            }
            var available = Math.Max(0, _maximumFileBytes - _writtenBytes);
            var count = (int)Math.Min(available, bytes.Length - offset);
            // Keep each segment valid UTF-8 when a long entry spans rotation.
            while (count > 0 && offset + count < bytes.Length && (bytes[offset + count] & 0xc0) == 0x80)
                count--;
            if (count == 0)
            {
                Rotate();
                continue;
            }
            _stream.Write(bytes, offset, count);
            _writtenBytes += count;
            offset += count;
        }
        if (_autoFlush) Flush();
    }

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stream?.Flush();
    }

    private void Rotate()
    {
        _stream?.Dispose();
        _stream = null;
        if (_retainedFiles == 1)
        {
            File.Delete(_path);
            return;
        }
        for (var index = _retainedFiles - 1; index >= 1; index--)
        {
            var source = SegmentPath(_path, index - 1);
            if (File.Exists(source)) File.Move(source, SegmentPath(_path, index), true);
        }
    }

    public static void DeleteSession(string path, int retainedFiles)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retainedFiles, 1);
        for (var index = 0; index < retainedFiles; index++) File.Delete(SegmentPath(path, index));
    }

    private static string SegmentPath(string path, int index) => index == 0 ? path : $"{path}.{index}";

    public void Dispose()
    {
        if (_disposed) return;
        _stream?.Dispose();
        _stream = null;
        _disposed = true;
    }
}
