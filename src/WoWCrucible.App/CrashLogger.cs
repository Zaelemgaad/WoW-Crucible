using System.Diagnostics;
using System.Text;

namespace WoWCrucible.App;

internal static class CrashLogger
{
    private static readonly object Gate = new();
    public static string LogDirectory { get; } = ResolveLogDirectory();

    public static void Initialize()
    {
        Directory.CreateDirectory(LogDirectory);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log("Windows Forms unhandled exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("Fatal unhandled exception", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) => { Log("Unobserved task exception", e.Exception); e.SetObserved(); };
        Log($"Application started · logs: {LogDirectory}", null);
    }

    public static void Log(string context, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var path = Path.Combine(LogDirectory, $"WoWCrucible-{DateTime.Now:yyyy-MM-dd}.log");
            var entry = new StringBuilder()
                .AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {context}")
                .AppendLine($"Version: {Application.ProductVersion} | OS: {Environment.OSVersion} | .NET: {Environment.Version}");
            if (exception is not null) entry.AppendLine(exception.ToString());
            entry.AppendLine();
            lock (Gate) File.AppendAllText(path, entry.ToString());
        }
        catch { /* Logging must never cause another crash. */ }
    }

    public static void OpenDirectory()
    {
        Directory.CreateDirectory(LogDirectory);
        Process.Start(new ProcessStartInfo(LogDirectory) { UseShellExecute = true });
    }

    private static string ResolveLogDirectory()
    {
        var portable = Path.Combine(AppContext.BaseDirectory, "Logs");
        try
        {
            Directory.CreateDirectory(portable);
            var probe = Path.Combine(portable, $".write-test-{Environment.ProcessId}-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty); File.Delete(probe);
            return portable;
        }
        catch
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WoWCrucible", "Logs");
        }
    }
}
