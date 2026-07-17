using System.Diagnostics;
using System.Text;

namespace WoWCrucible.Desktop;

internal static class DesktopCrashLogger
{
    private static readonly object Gate = new();
    public static string LogDirectory { get; } = ResolveLogDirectory();

    public static void Initialize()
    {
        Directory.CreateDirectory(LogDirectory);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log("Fatal desktop exception", args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, args) => { Log("Unobserved desktop task exception", args.Exception); args.SetObserved(); };
        Log("Avalonia desktop started", null);
    }

    public static void Log(string context, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var path = Path.Combine(LogDirectory, $"WoWCrucible-Desktop-{DateTime.Now:yyyy-MM-dd}.log");
            var entry = new StringBuilder()
                .AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {context}")
                .AppendLine($"OS: {Environment.OSVersion} | .NET: {Environment.Version}");
            if (exception is not null) entry.AppendLine(exception.ToString());
            entry.AppendLine();
            lock (Gate) File.AppendAllText(path, entry.ToString());
        }
        catch { }
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
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return portable;
        }
        catch
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WoWCrucible", "Logs");
        }
    }
}
