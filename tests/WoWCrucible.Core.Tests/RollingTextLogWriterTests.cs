using System.Text;
using WoWCrucible.Core;

internal static class RollingTextLogWriterTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), $"crucible-log-rotation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "session.log");
            var strictUtf8 = new UTF8Encoding(false, true);
            var text = "12345\u00e9\U0001F642abcdefghijk\u00e9lmnop";
            using (var writer = new RollingTextLogWriter(path, 16, 4, autoFlush: false))
                writer.Write(text);
            var segments = ReadSegments(path);
            if (string.Concat(segments.Select(file => strictUtf8.GetString(File.ReadAllBytes(file)))) != text ||
                segments.Any(file => new FileInfo(file).Length > 16))
                throw new InvalidOperationException("Rotation corrupted or lost UTF-8 text before the retention limit.");

            using (var writer = new RollingTextLogWriter(path, 16, 4))
            {
                for (var index = 0; index < 30; index++) writer.Write(index.ToString("D8"));
            }
            segments = ReadSegments(path);
            if (segments.Length != 4 || segments.Sum(file => new FileInfo(file).Length) > 64 ||
                !File.ReadAllText(path).EndsWith("00000029", StringComparison.Ordinal))
                throw new InvalidOperationException("Log retention did not bound the session and preserve the newest entries.");

            File.WriteAllText(path + ".unrelated", "keep");
            RollingTextLogWriter.DeleteSession(path, 4);
            if (ReadSegments(path).Length != 0 || !File.Exists(path + ".unrelated"))
                throw new InvalidOperationException("Session cleanup left rotation segments or deleted unrelated files.");

            using (var writer = new RollingTextLogWriter(path, 4, 1)) writer.Write("123456789");
            if (File.ReadAllText(path) != "9" || File.Exists(path + ".1"))
                throw new InvalidOperationException("Single-file retention failed.");
            var disposed = new RollingTextLogWriter(path, 8, 1);
            disposed.Dispose();
            try { disposed.Write("not written"); throw new InvalidOperationException("Disposed writer accepted data."); }
            catch (ObjectDisposedException) { }
            Console.WriteLine("Log rotation: UTF-8, restart, byte/count bounds, session cleanup and disposal passed.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static string[] ReadSegments(string path) => Enumerable.Range(0, 4).Reverse()
        .Select(index => index == 0 ? path : $"{path}.{index}").Where(File.Exists).ToArray();
}
