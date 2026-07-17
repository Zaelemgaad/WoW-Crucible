using Avalonia;

namespace WoWCrucible.Desktop;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        DesktopCrashLogger.Initialize(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace(DesktopCrashLogger.IsDevbugEnabled ? Avalonia.Logging.LogEventLevel.Verbose : Avalonia.Logging.LogEventLevel.Warning);
}
