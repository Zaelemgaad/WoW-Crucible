using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class LayerStackIndexView : UserControl, IDisposable
{
    private readonly TextBox _source = new() { PlaceholderText = "Source Content tree…" };
    private readonly TextBox _index = new() { PlaceholderText = "Persistent layer-stack SQLite index…" };
    private readonly TextBox _layers = new() { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, PlaceholderText = "One layer per line: stack | order | name | extracted root" };
    private readonly TextBox _exclusions = new() { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, PlaceholderText = "Optional client-path exclusions, one glob per line" };
    private readonly TextBox _search = new() { PlaceholderText = "Filter normalized client paths…" };
    private readonly ComboBox _kind = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _status = new() { Text = "Choose one source tree and explicitly ordered reference layers.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#99A5B8") };
    private readonly ListBox _results = new();
    private CancellationTokenSource? _operation;

    public LayerStackIndexView(DesktopWorkspaceSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.Settings.ActiveProjectPath) && Directory.Exists(session.Settings.ActiveProjectPath))
            _index.Text = Path.Combine(session.Settings.ActiveProjectPath, "Cache", "layer-stack.sqlite");
        _kind.ItemsSource = new[] { "All classifications" }.Concat(Enum.GetNames<LooseLayerComparisonKind>()).ToArray(); _kind.SelectedIndex = 0;
        _results.ItemTemplate = new FuncDataTemplate<LooseLayerComparison>((row, _) => row is null ? new TextBlock() : new StackPanel
        {
            Spacing = 2, Margin = new Thickness(5, 4), Children =
            {
                new TextBlock { Text = $"{row.Kind} · {row.Stack}", FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse(KindColor(row.Kind)) },
                new TextBlock { Text = row.LogicalPath, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = $"{row.Suppliers.Count:N0} supplier(s) · family {row.FamilyKey}", Foreground = Brush.Parse("#8793A7"), FontSize = 10, TextWrapping = TextWrapping.Wrap }
            }
        });

        var browseSource = new Button { Content = "Browse source…" }; browseSource.Click += async (_, _) => await PickFolderAsync(_source, "Choose the source Content tree");
        var browseIndex = new Button { Content = "Choose index…" }; browseIndex.Click += async (_, _) => await PickIndexAsync();
        var addLayer = new Button { Content = "Add layer folder…" }; addLayer.Click += async (_, _) => await AddLayerAsync();
        var build = Accent("Build / resume index"); build.Click += async (_, _) => await BuildAsync();
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => _operation?.Cancel();
        var query = Accent("Query index"); query.Click += async (_, _) => await QueryAsync();
        _search.KeyDown += async (_, eventArgs) => { if (eventArgs.Key == Avalonia.Input.Key.Enter) await QueryAsync(); };
        _kind.SelectionChanged += async (_, _) => { if (File.Exists(_index.Text)) await QueryAsync(); };

        var configuration = new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 9, Margin = new Thickness(12), Children =
                {
                    new TextBlock { Text = "LOOSE TREE / PATCH STACK INDEX", FontSize = 17, FontWeight = FontWeight.SemiBold },
                    Note("Paths normalize from the first real client root. Layer order is never guessed; DBC/DB2 files are always marked for structured review. Unchanged hashes are reused on the next run."),
                    new TextBlock { Text = "Source content root", FontWeight = FontWeight.SemiBold }, Row(_source, browseSource),
                    new TextBlock { Text = "Persistent index", FontWeight = FontWeight.SemiBold }, Row(_index, browseIndex),
                    new TextBlock { Text = "Ordered layers", FontWeight = FontWeight.SemiBold }, _layers, addLayer,
                    new TextBlock { Text = "Protected/excluded paths", FontWeight = FontWeight.SemiBold }, _exclusions,
                    new WrapPanel { Children = { build, cancel } }, _status
                }
            }
        };
        var evidence = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 8, Margin = new Thickness(12) };
        evidence.Children.Add(new StackPanel { Spacing = 7, Children = { new TextBlock { Text = "QUERYABLE RESULTS", FontSize = 17, FontWeight = FontWeight.SemiBold }, Row(_search, _kind, query) } });
        Grid.SetRow(_results, 1); evidence.Children.Add(_results);
        Content = new ResponsiveSplitGrid(configuration, evidence, compactFirstWeight: 1, compactSecondWeight: 2);
    }

    private async Task BuildAsync()
    {
        _operation?.Cancel(); _operation?.Dispose(); _operation = new(); var token = _operation.Token;
        try
        {
            var source = ExistingDirectory(_source.Text, "Choose an existing source Content tree.");
            var index = RequiredPath(_index.Text, "Choose an index path.");
            var layers = ParseLayers(); var exclusions = Lines(_exclusions.Text);
            _status.Text = "Scanning normalized paths and hashing changed files…";
            var progress = new Progress<LooseLayerIndexProgress>(value => _status.Text = $"{value.ScannedFiles:N0} scanned · {value.HashedFiles:N0} hashed · {value.ReusedHashes:N0} reused\n{value.CurrentPath}");
            var summary = await Task.Run(() => new LooseLayerStackIndexService().Build(index, source, layers, exclusions, progress, token), token);
            _status.Text = $"Indexed {summary.SourceFiles:N0} source and {summary.LayerFiles:N0} layer files across {summary.Stacks:N0} stack(s). {summary.HashedFiles:N0} hashed, {summary.ReusedHashes:N0} reused. Exact {summary.ExactEffective:N0} · different {summary.DifferentEffective:N0} · absent {summary.Absent:N0} · structured {summary.StructuredTables:N0}.";
            await QueryAsync();
        }
        catch (OperationCanceledException) { _status.Text = "Layer-stack indexing cancelled; the last committed checkpoint remains usable."; }
        catch (Exception exception) { DesktopCrashLogger.Log("Layer-stack indexing failed", exception); _status.Text = $"ERROR · {exception.Message}"; }
    }

    private async Task QueryAsync()
    {
        try
        {
            var index = RequiredPath(_index.Text, "Choose an index path.");
            LooseLayerComparisonKind? kind = _kind.SelectedIndex <= 0 ? null : Enum.Parse<LooseLayerComparisonKind>((string)_kind.SelectedItem!);
            var rows = await Task.Run(() => new LooseLayerStackIndexService().Query(index, _search.Text, kind, 10_000));
            _results.ItemsSource = rows; _status.Text = $"Showing {rows.Count:N0} indexed comparison row(s). The SQLite checkpoint retains the complete result set.";
        }
        catch (Exception exception) { DesktopCrashLogger.Log("Layer-stack query failed", exception); _status.Text = $"ERROR · {exception.Message}"; }
    }

    private LooseLayerDefinition[] ParseLayers()
    {
        var result = new List<LooseLayerDefinition>();
        foreach (var line in Lines(_layers.Text))
        {
            var parts = line.Split('|', 4, StringSplitOptions.TrimEntries);
            if (parts.Length != 4 || !int.TryParse(parts[1], out var order)) throw new FormatException($"Invalid layer line: {line}. Use stack | order | name | root.");
            result.Add(new(parts[0], order, parts[2], ExistingDirectory(parts[3], $"Layer root does not exist: {parts[3]}")));
        }
        if (result.Count == 0) throw new InvalidOperationException("Add at least one explicitly ordered layer."); return result.ToArray();
    }

    private async Task AddLayerAsync()
    {
        var folders = await Storage().OpenFolderPickerAsync(new() { Title = "Choose one extracted patch layer", AllowMultiple = false }); if (folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath(); if (path is null) return; var order = Lines(_layers.Text).Length * 10 + 10; var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        _layers.Text = string.Join(Environment.NewLine, Lines(_layers.Text).Append($"Reference | {order} | {name} | {path}"));
    }
    private async Task PickFolderAsync(TextBox target, string title) { var folders = await Storage().OpenFolderPickerAsync(new() { Title = title, AllowMultiple = false }); if (folders.Count > 0) target.Text = folders[0].TryGetLocalPath(); }
    private async Task PickIndexAsync() { var files = await Storage().SaveFilePickerAsync(new() { Title = "Choose the persistent layer-stack index", SuggestedFileName = "layer-stack.sqlite", DefaultExtension = "sqlite", FileTypeChoices = [new("SQLite index") { Patterns = ["*.sqlite"] }] }); if (files?.TryGetLocalPath() is { } path) _index.Text = path; }
    private IStorageProvider Storage() => TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("The workspace is not attached to a desktop window.");
    private static string[] Lines(string? value) => (value ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static string ExistingDirectory(string? value, string error) { var path = RequiredPath(value, error); return Directory.Exists(path) ? path : throw new DirectoryNotFoundException(error); }
    private static string RequiredPath(string? value, string error) => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(error) : Path.GetFullPath(value);
    private static TextBlock Note(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8793A7"), FontSize = 11 };
    private static Button Accent(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static Grid Row(params Control[] controls) { var grid = new Grid { ColumnDefinitions = new(string.Join(',', controls.Select((_, index) => index == 0 ? "*" : "Auto"))), ColumnSpacing = 7 }; for (var index = 0; index < controls.Length; index++) { Grid.SetColumn(controls[index], index); grid.Children.Add(controls[index]); } return grid; }
    private static string KindColor(LooseLayerComparisonKind kind) => kind switch { LooseLayerComparisonKind.ExactEffective or LooseLayerComparisonKind.ExactEffectiveWithAlternateVersions => "#79B58A", LooseLayerComparisonKind.AbsentFromStack => "#D9A35F", LooseLayerComparisonKind.StructuredTableReview => "#7DA8D8", _ => "#D96C68" };
    public void Dispose() { _operation?.Cancel(); _operation?.Dispose(); }
}
