using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

public partial class MainWindow
{
    private sealed record OpenDestination(string Label, Func<Task> Open)
    {
        public override string ToString() => Label;
    }

    private async Task OpenDetectedFileAsync(string path)
    {
        try
        {
            path = Path.GetFullPath(path);
            var detected = await Task.Run(() => FileOpenCatalog.Detect(path));
            StatusText.Text = $"Opening {Path.GetFileName(path)} · {detected.Description}";
            DesktopCrashLogger.Debug("FILE", "identified", ("path", path), ("kind", detected.Kind));
            switch (detected.Kind)
            {
                case FileEditorKind.Table: await LoadDbcAsync(path); break;
                case FileEditorKind.Model: await LoadM2Async(path); break;
                case FileEditorKind.Texture: await OpenTextureWorkspaceAsync(path); break;
                case FileEditorKind.Image: OpenTextureWorkspace(); await _textureWorkspaceView!.OpenImageAsync(path); break;
                case FileEditorKind.Map: await OpenMapWorkspaceAsync(path); break;
                case FileEditorKind.Cache: await OpenCacheTableAsync(path); break;
                case FileEditorKind.Archive: await OpenIndexedArchiveAsync(path); break;
                case FileEditorKind.NativeAsset: OpenNativeConversionWorkspace(); await _nativeConversionWorkspaceView!.OpenAsync(path); break;
                case FileEditorKind.Sql:
                    var sql = await File.ReadAllTextAsync(path);
                    OpenSqlWorkspace(); _sqlWorkspaceView!.OpenScript(sql); break;
                case FileEditorKind.Project:
                    OpenProjectWorkspace(); await _projectWorkspaceView!.OpenProjectFileAsync(path); break;
                case FileEditorKind.Quest:
                    var draft = JsonSerializer.Deserialize<QuestPortableDraft>(await File.ReadAllTextAsync(path)) ?? throw new InvalidDataException("The quest draft is empty.");
                    OpenQuestWorkspace(); _questWorkspaceView!.OpenDraft(draft); break;
                case FileEditorKind.Talents:
                    OpenClassStudio(); await _classStudio!.ImportFileAsync(path); break;
                case FileEditorKind.StructuredRows: OpenRowDestination(path); break;
                default:
                    await ShowErrorAsync("Cannot identify this file", $"{Path.GetFileName(path)} has no recognized format. Crucible can open client tables, models, textures/images, maps, caches, MPQ archives, SQL, projects, quest drafts, talent layouts, and structured row data. This file was not changed."); break;
            }
        }
        catch (Exception exception)
        {
            DesktopCrashLogger.Log("Open file failed", exception);
            await ShowErrorAsync("Could not open file", exception.Message);
        }
    }

    private void OpenRowDestination(string path)
    {
        // CSV / plain JSON cannot identify which table owns the rows. Retain the
        // exact file and ask for a destination before invoking an existing importer.
        var choices = new List<OpenDestination>();
        foreach (var document in _documents)
        {
            var captured = document;
            choices.Add(new($"Client table: {Path.GetFileName(document.FullPath)}", () =>
            {
                var index = _documents.IndexOf(captured);
                if (index < 0) throw new InvalidOperationException("That table has been closed. Choose another destination.");
                ActivateDocument(index);
                OpenDbcImportClick(null, new RoutedEventArgs());
                _dbcImportViews[captured].SetInputPath(path);
                return Task.CompletedTask;
            }));
        }
        choices.Add(new("Choose a client table file…", async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            { Title = "Choose the destination table", FileTypeFilter = [new("Client table") { Patterns = ["*.dbc", "*.db2"] }] });
            if (files.FirstOrDefault()?.TryGetLocalPath() is not { } target) return;
            await LoadDbcAsync(target);
            if (Current is not { } document || !document.FullPath.Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return;
            OpenDbcImportClick(null, new RoutedEventArgs()); _dbcImportViews[document].SetInputPath(path);
        }));
        if (Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase) && _workspaceSession.DatabaseTested && _workspaceSession.DatabaseCapabilities is { } capabilities)
            foreach (var table in capabilities.Tables.Keys.Order(StringComparer.OrdinalIgnoreCase))
            {
                var target = table;
                choices.Add(new($"Database table: {target}", async () => { OpenSqlWorkspace(); await _sqlWorkspaceView!.OpenCsvAsync(path, target); }));
            }
        var picker = new ComboBox { ItemsSource = choices, SelectedIndex = 0, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var open = new Button { Content = "Open in selected editor" };
        open.Click += async (_, _) =>
        {
            if (picker.SelectedItem is not OpenDestination destination) return;
            try { open.IsEnabled = false; await destination.Open(); }
            catch (Exception error) { status.Text = error.Message; DesktopCrashLogger.Log("Open destination failed", error); }
            finally { open.IsEnabled = true; }
        };
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => CloseFeatureWorkspace();
        var view = new UserControl { Content = new StackPanel { Margin = new Thickness(20), Spacing = 12, Children =
        {
            new TextBlock { Text = "Where should this data open?", FontSize = 20 },
            new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = "These rows do not uniquely identify a destination. Choose a table, then review the import preview. For database CSV imports, connect Server & SQL first.", TextWrapping = TextWrapping.Wrap },
            picker, new WrapPanel { Children = { open, cancel } }, status
        } } };
        OpenFeatureWorkspace(view, $"Open {Path.GetFileName(path)}");
    }
}
