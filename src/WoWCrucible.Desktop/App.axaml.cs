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
            var arguments = desktop.Args ?? [];
            var assetComparisonIndex = Array.FindIndex(arguments, argument =>
                IsOption(argument, "--asset-compare") || IsOption(argument, "--asset-library"));
            if (assetComparisonIndex >= 0)
            {
                var assetComparison = arguments[assetComparisonIndex];
                var separator = assetComparison.IndexOf('='); var library = separator < 0 ? null : assetComparison[(separator + 1)..].Trim('"');
                if (string.IsNullOrWhiteSpace(library) && assetComparisonIndex + 1 < arguments.Length && !arguments[assetComparisonIndex + 1].StartsWith("--", StringComparison.Ordinal))
                    library = arguments[assetComparisonIndex + 1].Trim('"');
                window.Opened += (_, _) => window.OpenAssetComparison(library);
            }
            if (arguments.Any(argument => IsOption(argument, "--sql-studio"))) window.Opened += (_, _) => window.OpenSqlWorkspace();
            else if (arguments.Any(argument => IsOption(argument, "--projects"))) window.Opened += (_, _) => window.OpenProjectWorkspace();
            else if (arguments.Any(argument => IsOption(argument, "--maps"))) window.Opened += (_, _) => window.OpenMapWorkspace();
            else if (arguments.Any(argument => IsOption(argument, "--items"))) window.Opened += (_, _) => window.OpenItemWorkbench();
            else if (arguments.Any(argument => IsOption(argument, "--creatures"))) window.Opened += (_, _) => window.OpenCreatureWorkspace();
            else if (arguments.Any(argument => IsOption(argument, "--textures"))) window.Opened += (_, _) => window.OpenTextureWorkspace();
            else if (arguments.Any(argument => IsOption(argument, "--gameobjects"))) window.Opened += (_, _) => window.OpenGameObjectWorkspace();
            else if (arguments.Any(argument => IsOption(argument, "--quests"))) window.Opened += (_, _) => window.OpenQuestWorkspace();
            else if (arguments.Any(argument => IsOption(argument, "--pet-curve"))) window.Opened += (_, _) => window.OpenPetLevelCurveWorkspace();
            else if (arguments.Any(argument => IsOption(argument, "--pet-graph"))) window.Opened += (_, _) => window.OpenPetAbilityGraphWorkspace();
            else if (arguments.Any(argument => IsOption(argument, "--pets"))) window.Opened += (_, _) => window.OpenPetWorkspace();
            else if (arguments.Any(argument => IsOption(argument, "--behaviors"))) window.Opened += (_, _) => window.OpenBehaviorWorkspace();
            else if (arguments.Any(argument => IsOption(argument, "--tool-inventory"))) window.Opened += async (_, _) => await window.OpenToolInventoryAsync();
            else if (arguments.Any(argument => IsOption(argument, "--dbd-schemas"))) window.Opened += (_, _) => window.OpenDbdSchemaAudit();
            else if (arguments.Any(argument => IsOption(argument, "--mpq"))) window.Opened += (_, _) => window.OpenMpqWorkspace();
            var paletteIndex = Array.FindIndex(arguments, argument => IsOption(argument, "--command-palette"));
            if (paletteIndex >= 0)
            {
                var paletteArgument = arguments[paletteIndex]; var separator = paletteArgument.IndexOf('='); var query = separator < 0 ? null : paletteArgument[(separator + 1)..].Trim('"');
                if (string.IsNullOrWhiteSpace(query) && paletteIndex + 1 < arguments.Length && !arguments[paletteIndex + 1].StartsWith("--", StringComparison.Ordinal)) query = arguments[paletteIndex + 1].Trim('"');
                window.Opened += (_, _) => window.OpenCommandPalette(query);
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

    private static bool IsOption(string argument, string name) => argument.Equals(name, StringComparison.OrdinalIgnoreCase) || argument.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase);
}
