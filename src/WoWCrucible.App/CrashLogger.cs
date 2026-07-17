using System.Diagnostics;
using System.Text;
using WoWCrucible.Core;

namespace WoWCrucible.App;

internal static class CrashLogger
{
    private static readonly object Gate = new();
    private static readonly string SessionPath = Path.Combine(CruciblePaths.CrashLogDirectory, $"WoWCrucible-Legacy-Crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}-p{Environment.ProcessId}.log");
    public static string LogDirectory => CruciblePaths.LogDirectory;

    public static void Initialize()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log("Windows Forms unhandled exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("Fatal unhandled exception", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) => { Log("Unobserved task exception", e.Exception); e.SetObserved(); };
    }

    public static void Log(string context, Exception? exception)
    {
        try
        {
            if (exception is null) return;
            Directory.CreateDirectory(CruciblePaths.CrashLogDirectory);
            var entry = new StringBuilder()
                .AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {context}")
                .AppendLine($"Version: {Application.ProductVersion} | OS: {Environment.OSVersion} | .NET: {Environment.Version}");
            if (exception is not null) entry.AppendLine(exception.ToString());
            entry.AppendLine();
            lock (Gate)
            {
                File.AppendAllText(SessionPath, entry.ToString());
                foreach (var old in Directory.EnumerateFiles(CruciblePaths.CrashLogDirectory, "WoWCrucible-Legacy-Crash-*.log").Where(path => !path.Equals(SessionPath, StringComparison.OrdinalIgnoreCase)).OrderByDescending(File.GetLastWriteTimeUtc).Skip(2)) File.Delete(old);
            }
        }
        catch { /* Logging must never cause another crash. */ }
    }

    public static void OpenDirectory()
    {
        Directory.CreateDirectory(LogDirectory);
        Process.Start(new ProcessStartInfo(LogDirectory) { UseShellExecute = true });
    }

}
