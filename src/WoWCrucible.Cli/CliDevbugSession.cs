using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using WoWCrucible.Core;

sealed class CliDevbugSession : IDisposable
{
    private const int RetainedSessions = 3;
    private readonly object _gate = new();
    private readonly Stopwatch _timer = Stopwatch.StartNew();
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;
    private readonly StreamWriter _log;
    private long _sequence;
    private bool _completed;

    private CliDevbugSession(string[] arguments)
    {
        Directory.CreateDirectory(CruciblePaths.DebugLogDirectory);
        LogPath = UniquePath();
        RetainNewest(LogPath);
        _log = new StreamWriter(new FileStream(LogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false), 64 * 1024);
        _originalOut = Console.Out;
        _originalError = Console.Error;
        Console.SetOut(new DevbugTeeWriter(_originalOut, this, "OUT"));
        Console.SetError(new DevbugTeeWriter(_originalError, this, "ERR"));
        Record("INFO", "SESSION", "cli-start",
            ("version", Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown"),
            ("pid", Environment.ProcessId),
            ("cwd", Environment.CurrentDirectory),
            ("data_root", CruciblePaths.DataRoot),
            ("portable", CruciblePaths.IsPortable),
            ("arguments", string.Join(' ', Sanitize(arguments))),
            ("log", LogPath));
    }

    public string LogPath { get; }

    public static CliDevbugSession? TryStart(bool enabled, string[] arguments)
    {
        if (!enabled) return null;
        try { return new CliDevbugSession(arguments); }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"WARNING: Devbug logging could not start: {exception.Message}");
            return null;
        }
    }

    public void RecordException(Exception exception) => Record("ERROR", "COMMAND", "unhandled-exception",
        ("type", exception.GetType().FullName), ("message", RedactText(exception.Message)), ("stack", RedactText(exception.ToString())));

    public void Complete(int exitCode)
    {
        if (_completed) return;
        Record(exitCode == 0 ? "INFO" : "ERROR", "SESSION", "cli-complete",
            ("exit_code", exitCode), ("elapsed_ms", _timer.Elapsed.TotalMilliseconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
        lock (_gate) _log.Flush();
        _completed = true;
        _originalError.WriteLine($"Devbug log: {LogPath}");
    }

    public void Dispose()
    {
        if (!_completed) Complete(Environment.ExitCode);
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
        lock (_gate) _log.Dispose();
    }

    internal void RecordConsole(string stream, string? value)
    {
        if (value is not null) Record("TRACE", "CONSOLE", stream, ("text", RedactText(value)));
    }

    private void Record(string level, string category, string action, params (string Name, object? Value)[] fields)
    {
        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", System.Globalization.CultureInfo.InvariantCulture);
        var sequence = Interlocked.Increment(ref _sequence);
        var details = string.Join(' ', fields.Select(field => $"{field.Name}={Quote(field.Value)}"));
        lock (_gate) _log.WriteLine($"{timestamp} #{sequence:D8} [{level}] [t{Environment.CurrentManagedThreadId}] [{category}] {action}{(details.Length == 0 ? string.Empty : " " + details)}");
    }

    private static string Quote(object? value)
    {
        var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "<null>";
        return '"' + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + '"';
    }

    private static IEnumerable<string> Sanitize(IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
        {
            var separator = argument.IndexOf('=');
            var option = separator < 0 ? argument : argument[..separator];
            yield return separator >= 0 && IsSecretOption(option) ? option + "=<redacted>" : argument;
        }
    }

    private static bool IsSecretOption(string option) =>
        option.Contains("password", StringComparison.OrdinalIgnoreCase) && !option.Equals("--password-env", StringComparison.OrdinalIgnoreCase)
        || option.Contains("token", StringComparison.OrdinalIgnoreCase)
        || option.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || option.EndsWith("-key", StringComparison.OrdinalIgnoreCase);

    private static string RedactText(string text) => Regex.Replace(text,
        @"(?i)(--(?:password(?!-env)|token|secret|[^\s=]*-key)[^\s=]*=)(?:""[^""]*""|\S+)", "$1<redacted>", RegexOptions.CultureInvariant);

    private static string UniquePath()
    {
        var stem = $"WoWCrucible-CLI-Devbug-{DateTime.Now:yyyyMMdd-HHmmss-fff}-p{Environment.ProcessId}";
        var path = Path.Combine(CruciblePaths.DebugLogDirectory, stem + ".log");
        for (var suffix = 2; File.Exists(path); suffix++) path = Path.Combine(CruciblePaths.DebugLogDirectory, $"{stem}-{suffix}.log");
        return path;
    }

    private static void RetainNewest(string currentPath)
    {
        foreach (var old in Directory.EnumerateFiles(CruciblePaths.DebugLogDirectory, "WoWCrucible-CLI-Devbug-*.log")
                     .Where(path => !path.Equals(currentPath, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(File.GetLastWriteTimeUtc).Skip(RetainedSessions - 1))
        {
            try { File.Delete(old); } catch { }
        }
    }

    private sealed class DevbugTeeWriter(TextWriter original, CliDevbugSession session, string stream) : TextWriter
    {
        public override Encoding Encoding => original.Encoding;
        public override void Write(char value) => original.Write(value);
        public override void Write(string? value) => original.Write(value);
        public override void WriteLine() { original.WriteLine(); session.RecordConsole(stream, string.Empty); }
        public override void WriteLine(string? value) { original.WriteLine(value); session.RecordConsole(stream, value); }
        public override void Flush() => original.Flush();
    }
}
