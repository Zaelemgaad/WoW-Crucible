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
            var initialPath = desktop.Args?.FirstOrDefault(File.Exists);
            if (initialPath is not null)
                window.Opened += async (_, _) => await window.LoadPathAsync(initialPath);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
