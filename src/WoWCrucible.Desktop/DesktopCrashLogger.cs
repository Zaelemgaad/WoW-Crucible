using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal static class DesktopCrashLogger
{
    private const int RetainedSessions = 3;
    private static readonly object CrashGate = new();
    private static readonly BlockingCollection<string> DebugQueue = new(new ConcurrentQueue<string>());
    private static readonly DesktopSettings Settings = DesktopSettings.Load();
    private static Thread? _writerThread;
    private static string? _debugLogPath;
    private static string? _crashLogPath;
    private static bool _ownsConsole;
    private static bool _traceAttached;
    private static int _initialized;
    private static int _sequence;

    public static bool IsDevbugEnabled { get; private set; } = Settings.DevbugMode;
    public static string LogDirectory => CruciblePaths.LogDirectory;
    public static string? DebugLogPath => _debugLogPath;

    public static void Initialize(string[] args)
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
        if (args.Any(argument => argument.Equals("--devbug", StringComparison.OrdinalIgnoreCase) || argument.Equals("--debug", StringComparison.OrdinalIgnoreCase)))
            IsDevbugEnabled = true;
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            Fatal("PROCESS", "fatal-unhandled-exception", eventArgs.ExceptionObject as Exception ?? new Exception(eventArgs.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, eventArgs) => { Failure("TASK", "unobserved-task-exception", eventArgs.Exception); eventArgs.SetObserved(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
        if (IsDevbugEnabled) StartDevbugSession("startup");
    }

    public static void SetDevbugMode(bool enabled, string reason = "user-toggle")
    {
        if (enabled == IsDevbugEnabled) return;
        if (!enabled) Debug("SESSION", "devbug-disabled", ("reason", reason));
        IsDevbugEnabled = enabled;
        Settings.DevbugMode = enabled;
        try { Settings.Save(); }
        catch (Exception exception) { Failure("SETTINGS", "devbug-setting-save-failed", exception); }
        if (enabled) StartDevbugSession(reason);
        else
        {
            FlushDebugQueue(TimeSpan.FromSeconds(2));
            DetachConsole();
        }
    }

    public static void Debug(string category, string action, params (string Key, object? Value)[] fields)
    {
        if (!IsDevbugEnabled) return;
        Enqueue(Format("TRACE", category, action, null, fields));
    }

    public static void Failure(string category, string action, Exception exception, params (string Key, object? Value)[] fields)
    {
        var entry = Format("ERROR", category, action, exception, fields);
        if (IsDevbugEnabled) Enqueue(entry);
        else WriteCrash(entry);
    }

    public static void Fatal(string category, string action, Exception exception, params (string Key, object? Value)[] fields)
    {
        var entry = Format("FATAL", category, action, exception, fields);
        if (IsDevbugEnabled) { Enqueue(entry); FlushDebugQueue(TimeSpan.FromSeconds(2)); }
        else WriteCrash(entry);
    }

    // Compatibility entry point for handled UI activity. Both status messages
    // and handled exceptions are Devbug-only; fatal hooks write crash records.
    public static void Log(string context, Exception? exception)
    {
        if (exception is null) Debug("APP", "status", ("context", context));
        else if (IsDevbugEnabled) Enqueue(Format("ERROR", "APP", context, exception));
    }

    public static void OpenDirectory()
    {
        Directory.CreateDirectory(LogDirectory);
        Process.Start(new ProcessStartInfo(LogDirectory) { UseShellExecute = true });
    }

    private static void StartDevbugSession(string reason)
    {
        Directory.CreateDirectory(CruciblePaths.DebugLogDirectory);
        _debugLogPath = UniquePath(CruciblePaths.DebugLogDirectory, "WoWCrucible-Devbug", ".log");
        RetainNewest(CruciblePaths.DebugLogDirectory, "WoWCrucible-Devbug-*.log", RetainedSessions, _debugLogPath);
        AttachConsole();
        AttachTraceListener();
        EnsureWriter();
        Enqueue(Format("INFO", "SESSION", "devbug-started", null,
            ("reason", reason), ("pid", Environment.ProcessId), ("version", typeof(DesktopCrashLogger).Assembly.GetName().Version),
            ("os", Environment.OSVersion), ("dotnet", Environment.Version), ("base_directory", AppContext.BaseDirectory),
            ("data_root", CruciblePaths.DataRoot), ("portable", CruciblePaths.IsPortable), ("log", _debugLogPath)));
    }

    private static void EnsureWriter()
    {
        if (_writerThread is { IsAlive: true }) return;
        _writerThread = new Thread(DebugWriterLoop) { IsBackground = true, Name = "Crucible Devbug Log Writer", Priority = ThreadPriority.BelowNormal };
        _writerThread.Start();
    }

    private static void DebugWriterLoop()
    {
        StreamWriter? writer = null; string? writerPath = null;
        try
        {
            foreach (var entry in DebugQueue.GetConsumingEnumerable())
            {
                try
                {
                    var path = _debugLogPath;
                    if (path is not null && !path.Equals(writerPath, StringComparison.OrdinalIgnoreCase))
                    {
                        writer?.Dispose();
                        writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan), new UTF8Encoding(false)) { AutoFlush = true };
                        writerPath = path;
                    }
                    writer?.Write(entry);
                    if (_ownsConsole || GetConsoleWindow() != IntPtr.Zero) Console.Write(entry);
                }
                catch { }
            }
        }
        finally { writer?.Dispose(); }
    }

    private static void Enqueue(string entry)
    {
        try { DebugQueue.Add(entry); }
        catch { }
    }

    private static string Format(string level, string category, string action, Exception? exception, params (string Key, object? Value)[] fields)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var line = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
            .Append(" | ").Append(sequence.ToString("D8"))
            .Append(" | ").Append(level.PadRight(5))
            .Append(" | T").Append(Environment.CurrentManagedThreadId.ToString("D2"))
            .Append(" | ").Append(category.ToUpperInvariant())
            .Append(" | ").Append(action);
        foreach (var field in fields) line.Append(" | ").Append(field.Key).Append('=').Append(Quote(field.Value));
        line.AppendLine();
        if (exception is not null)
        {
            line.Append("    exception_type: ").AppendLine(exception.GetType().FullName)
                .Append("    message: ").AppendLine(exception.Message.ReplaceLineEndings(" "))
                .AppendLine("    details:");
            foreach (var detail in exception.ToString().ReplaceLineEndings("\n").Split('\n')) line.Append("      ").AppendLine(detail);
        }
        return line.ToString();
    }

    private static string Quote(object? value)
    {
        if (value is null) return "null";
        var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        return '"' + text.Replace("\\", "\\\\").Replace("\"", "\\\"").ReplaceLineEndings("\\n") + '"';
    }

    private static void WriteCrash(string entry)
    {
        try
        {
            lock (CrashGate)
            {
                Directory.CreateDirectory(CruciblePaths.CrashLogDirectory);
                _crashLogPath ??= UniquePath(CruciblePaths.CrashLogDirectory, "WoWCrucible-Crash", ".log");
                File.AppendAllText(_crashLogPath, entry, Encoding.UTF8);
                RetainNewest(CruciblePaths.CrashLogDirectory, "WoWCrucible-Crash-*.log", RetainedSessions, _crashLogPath);
            }
        }
        catch { }
    }

    private static string UniquePath(string directory, string prefix, string extension)
        => Path.Combine(directory, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss-fff}-p{Environment.ProcessId}{extension}");

    private static void RetainNewest(string directory, string pattern, int retain, string current)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, pattern).Where(path => !path.Equals(current, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(File.GetLastWriteTimeUtc).Skip(Math.Max(0, retain - 1))) File.Delete(file);
        }
        catch { }
    }

    private static void AttachConsole()
    {
        if (!OperatingSystem.IsWindows() || GetConsoleWindow() != IntPtr.Zero) return;
        if (!AllocConsole()) return;
        _ownsConsole = true;
        SetConsoleTitle("WoW Crucible — Devbug Mode");
        var systemMenu = GetSystemMenu(GetConsoleWindow(), false);
        if (systemMenu != IntPtr.Zero) DeleteMenu(systemMenu, 0xF060, 0);
        try
        {
            var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            var error = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetOut(output); Console.SetError(error);
        }
        catch { }
    }

    private static void AttachTraceListener()
    {
        if (_traceAttached) return;
        Trace.Listeners.Add(new DevbugTraceListener());
        _traceAttached = true;
    }

    private static void DetachConsole()
    {
        if (!_ownsConsole) return;
        try { FreeConsole(); } catch { }
        _ownsConsole = false;
    }

    private static void FlushDebugQueue(TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (DebugQueue.Count > 0 && stopwatch.Elapsed < timeout) Thread.Sleep(10);
    }

    private static void Shutdown()
    {
        if (IsDevbugEnabled) Debug("SESSION", "process-exit");
        FlushDebugQueue(TimeSpan.FromSeconds(2));
        try { DebugQueue.CompleteAdding(); } catch { }
        try { _writerThread?.Join(TimeSpan.FromSeconds(2)); } catch { }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AllocConsole();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool SetConsoleTitle(string title);
    [DllImport("user32.dll")] private static extern IntPtr GetSystemMenu(IntPtr window, bool revert);
    [DllImport("user32.dll")] private static extern bool DeleteMenu(IntPtr menu, uint position, uint flags);

    private sealed class DevbugTraceListener : TraceListener
    {
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Debug("FRAMEWORK", "trace", ("message", message)); }
        public override void WriteLine(string? message) { if (!string.IsNullOrWhiteSpace(message)) Debug("FRAMEWORK", "trace", ("message", message)); }
    }
}
