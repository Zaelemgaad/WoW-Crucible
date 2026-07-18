using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Selection;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class LayeredDbcWorkspaceView : UserControl, IDisposable
{
    private readonly DesktopWorkspaceSession _session;
    private readonly TextBox _baseRoot = new();
    private readonly TextBox _overrideRoot = new();
    private readonly TextBox _schemaPath = new();
    private readonly ListBox _layers = new() { SelectionMode = SelectionMode.Single };
    private readonly ListBox _differences = new() { SelectionMode = SelectionMode.Multiple };
    private readonly TextBlock _summary = Status("Choose base and override DBC directories, then compare their effective layers.");
    private readonly TextBlock _detail = Status("Select a genuinely overridden DBC to inspect semantic row/field differences.");
    private readonly TextBlock _status = Status("Ready");
    private IReadOnlyList<DbcLayerEntry> _entries = [];
    private IReadOnlyList<DbcCellDifference> _cellDifferences = [];
    private DbcSchemaResolution? _selectedSchema;
    private CancellationTokenSource? _operation;
    private string? _lastOutput;

    public event EventHandler? BackRequested;
    public event EventHandler<string>? OpenDbcRequested;
    public event EventHandler<IReadOnlyList<string>>? StageOverridesRequested;

    public LayeredDbcWorkspaceView(DesktopWorkspaceSession session)
    {
        _session = session;
        _baseRoot.Text = session.Settings.BaseDbcPath;
        _overrideRoot.Text = session.Settings.OverrideDbcPath;
        _schemaPath.Text = session.Settings.SchemaDefinitionPath;
        ConfigureTemplates();

        var back = new Button { Content = "← Editor" }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var heading = new Grid { ColumnDefinitions = new("Auto,*"), Margin = new Thickness(12, 8), Children = { back, WithColumn(new TextBlock { Text = "DBC LAYERS & PROMOTION", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) }, 1) } };
        Content = new Grid
        {
            RowDefinitions = new("Auto,Auto,*,Auto"),
            Children =
            {
                new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 0, 0, 1), Child = heading },
                WithRow(PathAndActionArea(), 1),
                WithRow(ComparisonArea(), 2),
                WithRow(new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 7), Child = _status }, 3)
            }
        };
    }

    private void ConfigureTemplates()
    {
        _layers.ItemTemplate = new FuncDataTemplate<DbcLayerEntry>((entry, _) => entry is null ? new Grid() : new Grid
        {
            ColumnDefinitions = new("2*,Auto,Auto,3*"), ColumnSpacing = 10, Margin = new Thickness(3, 2), Children =
            {
                new TextBlock { Text = entry.Name },
                WithColumn(new TextBlock { Text = entry.Status.ToString(), Foreground = LayerBrush(entry.Status) }, 1),
                WithColumn(new TextBlock { Text = FormatBytes(entry.EffectiveSize) }, 2),
                WithColumn(new TextBlock { Text = entry.EffectivePath, TextTrimming = TextTrimming.CharacterEllipsis }, 3)
            }
        });
        _differences.ItemTemplate = new FuncDataTemplate<DbcCellDifference>((difference, _) => difference is null ? new Grid() : new Grid
        {
            ColumnDefinitions = new("Auto,2*,3*,3*"), ColumnSpacing = 10, Margin = new Thickness(3, 2), Children =
            {
                new TextBlock { Text = difference.Id.ToString() },
                WithColumn(new TextBlock { Text = difference.ColumnName }, 1),
                WithColumn(new TextBlock { Text = difference.BaseValue, TextTrimming = TextTrimming.CharacterEllipsis }, 2),
                WithColumn(new TextBlock { Text = difference.OverrideValue, TextTrimming = TextTrimming.CharacterEllipsis }, 3)
            }
        });
        _layers.SelectionChanged += async (_, _) => await LoadSelectedDifferencesAsync();
    }

    private Control PathAndActionArea()
    {
        var baseButton = new Button { Content = "Base…" }; baseButton.Click += async (_, _) => { var path = await PickFolderAsync("Choose the stock/effective base DBC directory"); if (path is not null) _baseRoot.Text = path; };
        var overrideButton = new Button { Content = "Override…" }; overrideButton.Click += async (_, _) => { var path = await PickFolderAsync("Choose the edited/override DBC directory"); if (path is not null) _overrideRoot.Text = path; };
        var schemaButton = new Button { Content = "Schema…" }; schemaButton.Click += async (_, _) => { var path = await PickFileAsync("Choose the WotLK build-12340 schema", ["*.xml"]); if (path is not null) _schemaPath.Text = path; };
        var compare = AccentButton("Compare layers"); compare.Click += async (_, _) => await CompareLayersAsync();
        var open = new Button { Content = "Open effective DBC" }; open.Click += (_, _) => OpenEffective();
        var stage = AccentButton("Stage changed overrides in MPQ"); stage.Click += (_, _) => StageChangedOverrides();
        var paths = new Grid { ColumnDefinitions = new("Auto,*,Auto"), RowDefinitions = new("Auto,Auto,Auto"), ColumnSpacing = 8, RowSpacing = 7, Margin = new Thickness(8, 8, 8, 4) };
        AddPath(paths, 0, "Base layer", _baseRoot, baseButton); AddPath(paths, 1, "Override layer", _overrideRoot, overrideButton); AddPath(paths, 2, "Definition", _schemaPath, schemaButton);
        return new StackPanel { Children = { paths, new WrapPanel { Margin = new Thickness(8, 0, 8, 8), Children = { compare, open, stage } } } };
    }

    private Control ComparisonArea()
    {
        var applyFields = AccentButton("Promote selected fields…"); applyFields.Click += async (_, _) => await ApplyOperationsAsync(CreateFieldOperations());
        var applyRows = new Button { Content = "Promote selected full rows…" }; applyRows.Click += async (_, _) => await ApplyOperationsAsync(CreateRowOperations());
        var additions = new Button { Content = "Promote additive rows only…" }; additions.Click += async (_, _) => await ApplyAdditionsAsync();
        var save = new Button { Content = "Save selected manifest…" }; save.Click += async (_, _) => await SaveManifestAsync(CreateFieldOperations());
        var applyManifest = new Button { Content = "Apply existing manifest…" }; applyManifest.Click += async (_, _) => await ApplyExistingManifestAsync();
        var openOutput = new Button { Content = "Open last output" }; openOutput.Click += (_, _) => { if (File.Exists(_lastOutput)) OpenDbcRequested?.Invoke(this, _lastOutput!); else _status.Text = "No promoted output has been created in this session."; };
        var left = new Grid { RowDefinitions = new("*,Auto"), Children = { _layers, WithRow(Card(_summary), 1) } };
        var right = new Grid { RowDefinitions = new("Auto,*,Auto"), Children = { new WrapPanel { Children = { applyFields, applyRows, additions, save, applyManifest, openOutput } }, WithRow(_differences, 1), WithRow(Card(_detail), 2) } };
        return new Grid
        {
            ColumnDefinitions = new("*,Auto,*"),
            Children = { left, WithColumn(new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Brush.Parse("#2B3445") }, 1), WithColumn(right, 2) }
        };
    }

    private async Task CompareLayersAsync()
    {
        Begin("Hash-comparing base and override layers…");
        try
        {
            var baseRoot = _baseRoot.Text ?? string.Empty;
            var overrideRoot = _overrideRoot.Text ?? string.Empty;
            _entries = await Task.Run(() => DbcLayerComparer.CompareDirectories(baseRoot, overrideRoot), _operation!.Token);
            _layers.ItemsSource = _entries;
            var changed = _entries.Count(entry => entry.Status is DbcLayerStatus.Overridden or DbcLayerStatus.OverrideOnly);
            _summary.Text = $"{_entries.Count:N0} effective DBCs · {changed:N0} supplied/changed by override · {_entries.Count(entry => entry.Status == DbcLayerStatus.Identical):N0} byte-identical overrides.";
            _session.Settings.BaseDbcPath = _baseRoot.Text ?? string.Empty; _session.Settings.OverrideDbcPath = _overrideRoot.Text ?? string.Empty; _session.Settings.SchemaDefinitionPath = _schemaPath.Text ?? string.Empty; _session.Settings.Save();
            _status.Text = "Layer comparison complete. Select an overridden table for semantic detail.";
        }
        catch (OperationCanceledException) { _status.Text = "Layer comparison cancelled."; }
        catch (Exception exception) { Fail("Layer comparison failed", exception); }
        finally { End(); }
    }

    private async Task LoadSelectedDifferencesAsync()
    {
        _operation?.Cancel();
        _cellDifferences = []; _differences.ItemsSource = null; _selectedSchema = null;
        if (_layers.SelectedItem is not DbcLayerEntry entry) return;
        if (entry is not { BasePath: not null, OverridePath: not null, Status: DbcLayerStatus.Overridden })
        {
            _detail.Text = entry.Status switch { DbcLayerStatus.OverrideOnly => "Override-only table: its complete file can be staged, but field promotion has no base row to compare.", DbcLayerStatus.Identical => "The override is byte-identical to base and does not need staging.", _ => "Base-only table: no override changes exist." };
            return;
        }
        Begin($"Finding semantic differences in {entry.Name}…");
        try
        {
            var catalog = File.Exists(_schemaPath.Text) ? DbcSchemaCatalog.Load(_schemaPath.Text!) : DbcSchemaCatalog.CreateBuiltIn12340();
            var sample = WdbcFile.Load(entry.BasePath); var resolution = catalog.ResolveColumns(Path.GetFileNameWithoutExtension(entry.Name), sample.FieldCount);
            if (resolution.UsedFallback) throw new InvalidDataException("Selective promotion requires a matching named schema; the fallback raw layout is not trusted for this operation.");
            _selectedSchema = resolution;
            var detail = await Task.Run(() => DbcLayerComparer.CompareFiles(entry.BasePath, entry.OverridePath, resolution.Columns, resolution.KeyStrategy, _operation!.Token), _operation!.Token);
            _cellDifferences = await Task.Run(() => DbcPromotionService.GetDifferences(entry.BasePath, entry.OverridePath, resolution.Columns, resolution.KeyStrategy, _operation!.Token), _operation!.Token);
            _differences.ItemsSource = _cellDifferences;
            _detail.Text = $"{entry.Name}: +{detail.AddedRows:N0} rows · -{detail.RemovedRows:N0} rows · {detail.ModifiedRows:N0} modified rows · {detail.ModifiedCells:N0} changed fields.\nSelect exact fields or promote selected IDs as complete rows. Base is never overwritten automatically.";
            _status.Text = $"Loaded {_cellDifferences.Count:N0} promotable differences.";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Fail("Detailed layer comparison failed", exception); }
        finally { End(); }
    }

    private DbcPromotionOperation[] CreateFieldOperations()
    {
        var selected = _differences.SelectedItems?.OfType<DbcCellDifference>().ToArray() ?? [];
        return selected.GroupBy(difference => difference.Id).Select(group => group.Any(difference => difference.ColumnIndex < 0)
            ? new DbcPromotionOperation(group.Key, ["*"])
            : new DbcPromotionOperation(group.Key, group.Select(difference => difference.ColumnName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())).ToArray();
    }

    private DbcPromotionOperation[] CreateRowOperations() => (_differences.SelectedItems?.OfType<DbcCellDifference>() ?? []).Select(difference => difference.Id).Distinct().Select(id => new DbcPromotionOperation(id, ["*"])).ToArray();

    private async Task SaveManifestAsync(DbcPromotionOperation[] operations)
    {
        if (!TryPromotionContext(out var entry, out var schema) || operations.Length == 0) { _status.Text = "Select at least one changed field first."; return; }
        var destination = await Storage().SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Save repeatable DBC promotion manifest", SuggestedFileName = $"{Path.GetFileNameWithoutExtension(entry.Name)}.dbc-promotion.json", FileTypeChoices = [new FilePickerFileType("DBC promotion manifest") { Patterns = ["*.dbc-promotion.json", "*.json"] }] });
        var path = destination?.TryGetLocalPath(); if (path is null) return;
        DbcPromotionService.SaveManifest(path, Path.GetFileNameWithoutExtension(entry.BasePath!)!, schema.KeyStrategy.DisplayName(schema.Columns), operations);
        _status.Text = $"Saved promotion manifest {path}";
    }

    private async Task ApplyOperationsAsync(DbcPromotionOperation[] operations)
    {
        if (!TryPromotionContext(out var entry, out var schema) || operations.Length == 0) { _status.Text = "Select at least one changed field or row first."; return; }
        await ApplyManifestAsync(entry, schema, new DbcPromotionManifest(1, Path.GetFileNameWithoutExtension(entry.BasePath!)!, schema.KeyStrategy.DisplayName(schema.Columns), operations));
    }

    private async Task ApplyAdditionsAsync()
    {
        if (!TryPromotionContext(out var entry, out var schema)) return;
        try { var manifest = await Task.Run(() => DbcPromotionService.CreateAdditionsManifest(entry.BasePath!, entry.OverridePath!, schema.Columns, schema.KeyStrategy)); if (manifest.Operations.Count == 0) { _status.Text = "No additive rows exist in the selected override."; return; } await ApplyManifestAsync(entry, schema, manifest); }
        catch (Exception exception) { Fail("Additive promotion failed", exception); }
    }

    private async Task ApplyExistingManifestAsync()
    {
        if (!TryPromotionContext(out var entry, out var schema)) return;
        var path = await PickFileAsync("Apply an existing DBC promotion manifest", ["*.dbc-promotion.json", "*.json"]); if (path is null) return;
        try { await ApplyManifestAsync(entry, schema, DbcPromotionService.LoadManifest(path)); }
        catch (Exception exception) { Fail("Manifest load/apply failed", exception); }
    }

    private async Task ApplyManifestAsync(DbcLayerEntry entry, DbcSchemaResolution schema, DbcPromotionManifest manifest)
    {
        var destination = await Storage().SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Create promoted DBC without overwriting either source", SuggestedFileName = entry.Name, FileTypeChoices = [new FilePickerFileType("WoW DBC") { Patterns = ["*.dbc"] }] });
        var output = destination?.TryGetLocalPath(); if (output is null) return;
        try { await Task.Run(() => DbcPromotionService.Apply(entry.BasePath!, entry.OverridePath!, output, schema.Columns, schema.KeyStrategy, manifest)); _lastOutput = output; _status.Text = $"Created promoted DBC: {output}. Neither source layer was overwritten."; }
        catch (Exception exception) { Fail("DBC promotion failed", exception); }
    }

    private bool TryPromotionContext(out DbcLayerEntry entry, out DbcSchemaResolution schema)
    {
        entry = _layers.SelectedItem as DbcLayerEntry ?? null!; schema = _selectedSchema!;
        if (entry is not { BasePath: not null, OverridePath: not null, Status: DbcLayerStatus.Overridden } || schema is null) { _status.Text = "Select a genuinely overridden DBC with a verified named schema first."; return false; }
        return true;
    }

    private void OpenEffective() { if (_layers.SelectedItem is DbcLayerEntry entry) OpenDbcRequested?.Invoke(this, entry.EffectivePath); else _status.Text = "Select a DBC layer first."; }
    private void StageChangedOverrides()
    {
        var paths = _entries.Where(entry => entry.OverridePath is not null && entry.Status is DbcLayerStatus.Overridden or DbcLayerStatus.OverrideOnly).Select(entry => entry.OverridePath!).ToArray();
        if (paths.Length == 0) { _status.Text = "No changed overrides are available to stage."; return; }
        StageOverridesRequested?.Invoke(this, paths);
    }

    private void Begin(string text) { _operation?.Cancel(); _operation?.Dispose(); _operation = new(); _status.Text = text; }
    private void End() { _operation?.Dispose(); _operation = null; }
    private void Fail(string title, Exception exception) { _status.Text = $"{title}: {exception.Message}"; DesktopCrashLogger.Log(title, exception); }
    private IStorageProvider Storage() => TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("DBC Layers is not attached to the main window.");
    private async Task<string?> PickFolderAsync(string title) { var values = await Storage().OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false }); return values.FirstOrDefault()?.TryGetLocalPath(); }
    private async Task<string?> PickFileAsync(string title, IReadOnlyList<string> patterns) { var values = await Storage().OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = [new FilePickerFileType(title) { Patterns = patterns }] }); return values.FirstOrDefault()?.TryGetLocalPath(); }
    public void Dispose() { _operation?.Cancel(); _operation?.Dispose(); }

    private static Border Card(Control content) => new() { Padding = new Thickness(10), BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), Child = content };
    private static TextBlock Status(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#99A5B8") };
    private static Button AccentButton(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static string FormatBytes(long bytes) => bytes < 1024 ? $"{bytes:N0} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.#} KiB" : $"{bytes / (1024d * 1024):0.#} MiB";
    private static IBrush LayerBrush(DbcLayerStatus status) => status switch { DbcLayerStatus.Identical => Brush.Parse("#78859A"), DbcLayerStatus.Overridden => Brush.Parse("#E5B75A"), DbcLayerStatus.OverrideOnly => Brush.Parse("#67C587"), _ => Brush.Parse("#B8C4D8") };
    private static void AddPath(Grid grid, int row, string label, Control input, Control button) { var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }; Grid.SetRow(text, row); grid.Children.Add(text); Grid.SetRow(input, row); Grid.SetColumn(input, 1); grid.Children.Add(input); Grid.SetRow(button, row); Grid.SetColumn(button, 2); grid.Children.Add(button); }
    private static T WithColumn<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
    private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
}
