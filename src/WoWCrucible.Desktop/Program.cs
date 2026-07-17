using Avalonia;

namespace WoWCrucible.Desktop;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        DesktopCrashLogger.Initialize();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
