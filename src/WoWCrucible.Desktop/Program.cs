using Avalonia;

using System.Text.Json;
using WoWCrucible.Desktop.WindowsIntegration;

namespace WoWCrucible.Desktop;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            // Explorer conversions never initialize Avalonia, open the editor, or allocate a debug console.
            if (args.Contains("--explorer-server") || args.Contains("--install-explorer-menu") || args.Contains("--remove-explorer-menu"))
            {
                try
                {
                    if (args.Contains("--install-explorer-menu")) ExplorerMenuRegistration.Install();
                    else if (args.Contains("--remove-explorer-menu")) ExplorerMenuRegistration.Remove();
                    else new ExplorerServer().Run();
                }
                catch (Exception exception)
                {
                    ExplorerReports.Error(exception, !args.Contains("--quiet"));
                    Environment.ExitCode = 1;
                }
                return;
            }
            if (args is ["--explorer-open-stdin"])
            {
                try
                {
                    args = JsonSerializer.Deserialize<string[]>(Console.OpenStandardInput()) ?? throw new InvalidDataException("The Explorer selection is empty.");
                    if (args.Length == 0 || args.Any(path => !Path.IsPathFullyQualified(path)))
                        throw new InvalidDataException("Explorer selections must contain absolute file paths.");
                }
                catch (Exception exception) { ExplorerReports.Error(exception, true); Environment.ExitCode = 1; return; }
            }
        }
        DesktopCrashLogger.Initialize(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace(DesktopCrashLogger.IsDevbugEnabled ? Avalonia.Logging.LogEventLevel.Verbose : Avalonia.Logging.LogEventLevel.Warning);
}
