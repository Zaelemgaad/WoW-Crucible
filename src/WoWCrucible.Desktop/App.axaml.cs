using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace WoWCrucible.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
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
