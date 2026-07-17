using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace WoWCrucible.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += (_, eventArgs) => DesktopCrashLogger.Fatal("UI", "dispatcher-unhandled-exception", eventArgs.Exception);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            var assetComparison = desktop.Args?.FirstOrDefault(argument => argument.StartsWith("--asset-compare", StringComparison.OrdinalIgnoreCase));
            if (assetComparison is not null)
            {
                var separator = assetComparison.IndexOf('='); var library = separator < 0 ? null : assetComparison[(separator + 1)..].Trim('"');
                window.Opened += async (_, _) => await new AssetComparisonWindow(library).ShowDialog(window);
            }
            var initialPaths = desktop.Args?.Where(File.Exists).ToArray() ?? [];
            if (initialPaths.Length > 0)
                window.Opened += async (_, _) =>
                {
                    foreach (var path in initialPaths) await window.LoadPathAsync(path);
                };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
