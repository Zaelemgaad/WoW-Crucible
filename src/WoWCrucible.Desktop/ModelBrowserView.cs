using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;
using WoWCrucible.Desktop.Controls;

namespace WoWCrucible.Desktop;

internal sealed class ModelBrowserView : UserControl, IDisposable
{
    private readonly DesktopSettings _settings;
    private readonly ListBox _models = new();
    private readonly TextBox _search = new() { PlaceholderText = "Search models and folders" };
    private readonly TextBox _root = new() { IsReadOnly = true };
    private readonly ComboBox _filter = new() { ItemsSource = new[] { "All models", "Loose files", "Inside ZIPs", "Unreviewed", "Keep", "Marked for deletion" }, SelectedIndex = 0 };
    private readonly TextBlock _summary = Label("No folder opened.");
    private readonly TextBlock _status = Label("No model selected.");
    private readonly TextBlock _modelTitle = Label(string.Empty);
    private readonly TextBox _details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _geosets = new() { Spacing = 4 };
    private readonly ModelBrowserTextures _textures = new();
    private readonly M2PreviewView _preview = new();
    private readonly Dictionary<int, RgbaTexture> _decoded = [];
    private readonly Dictionary<int, string> _bindings = [];
    private readonly Dictionary<int, int> _textureRequests = [];
    private readonly HashSet<int> _selectedGeosets = [];
    private readonly List<CheckBox> _geosetChecks = [];
    private sealed record FaceChoice(ushort GeosetId, string Name) { public override string ToString() => Name; }
    private readonly ComboBox _faces = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _review = new() { ItemsSource = new[] { "Unreviewed", "Keep", "Mark for deletion" }, SelectedIndex = 0 };
    private ModelBrowserCatalog? _catalog;
    private ModelBrowserSource? _source;
    private ModelBrowserEntry? _current;
    private M2PreviewGeometry? _fullGeometry;
    private M2PreviewGeometry? _visibleGeometry;
    private CancellationTokenSource? _scan;
    private int _generation;
    private bool _updating;
    private bool _disposed;

    public ModelBrowserView(DesktopSettings settings)
    {
        _settings = settings;
        _textures.BindingChanged = SetTextureAsync;
        _faces.SelectionChanged += (_, _) => { if (!_updating) { ApplyFace(); ShowGeometry(); } };
        var folder = new Button { Content = "Open model folder" }; folder.Click += async (_, _) => await ChooseFolderAsync();
        var refresh = new Button { Content = "Rescan" }; refresh.Click += async (_, _) => { if (_catalog is not null) await OpenAsync(_catalog.Root); };
        var cancel = new Button { Content = "Cancel scan" }; cancel.Click += (_, _) => _scan?.Cancel();
        var reveal = new Button { Content = "Show source file" }; reveal.Click += (_, _) => RevealSource();
        var previous = new Button { Content = "<", Width = 34 }; ToolTip.SetTip(previous, "Previous model"); previous.Click += (_, _) => Move(-1);
        var next = new Button { Content = ">", Width = 34 }; ToolTip.SetTip(next, "Next model"); next.Click += (_, _) => Move(1);
        _review.SelectionChanged += (_, _) => SaveReview();
        _search.TextChanged += (_, _) => Filter(); _filter.SelectionChanged += (_, _) => Filter();
        _models.ItemTemplate = new FuncDataTemplate<ModelBrowserEntry>((entry, _) => entry is null ? new TextBlock() : new StackPanel
        {
            Margin = new Thickness(5), Spacing = 3,
            Children =
            {
                new TextBlock { Text = entry.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = entry.FormatLabel + " | " + entry.Container, FontSize = 11, Foreground = Brush.Parse("#A8B7C5") },
                new TextBlock { Text = entry.RelativePath, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#A8B7C5") }
            }
        });
        _models.SelectionChanged += async (_, _) => { if (!_updating && _models.SelectedItem is ModelBrowserEntry entry) await SelectAsync(entry); };
        var filePane = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), RowSpacing = 7 };
        filePane.Children.Add(_search); Put(filePane, _filter, 1); Put(filePane, _models, 2); Put(filePane, _summary, 3);
        var modelPane = new Grid { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 6 };
        modelPane.Children.Add(new DockPanel { LastChildFill = true, Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { previous, next, _review, reveal } }, _modelTitle } });
        Put(modelPane, _preview, 1); Put(modelPane, _status, 2);
        var tabs = new TabControl { ItemsSource = new[]
        {
            new TabItem { Header = "Textures", Foreground = Brush.Parse("#E8EBF2"), FontSize = 13, Padding = new Thickness(8), Content = _textures },
            new TabItem { Header = "Geosets", Foreground = Brush.Parse("#E8EBF2"), FontSize = 13, Padding = new Thickness(8), Content = GeosetPane() },
            new TabItem { Header = "Details", Foreground = Brush.Parse("#E8EBF2"), FontSize = 13, Padding = new Thickness(8), Content = _details }
        }};
        var saveDefaults = new Button { Content = "Save defaults" }; saveDefaults.Click += (_, _) => SaveDefaults();
        var resetDefaults = new Button { Content = "Reset defaults" }; resetDefaults.Click += async (_, _) => await ResetDefaultsAsync();
        var settingsPane = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 5 };
        settingsPane.Children.Add(new WrapPanel { Children = { saveDefaults, resetDefaults } }); Put(settingsPane, tabs, 1);
        var previewAndDetails = new ResponsiveSplitGrid(modelPane, settingsPane, 2.6, 1, wideAspect: 1.05, compactFirstWeight: 3, compactSecondWeight: 1);
        var body = new ResponsiveSplitGrid(filePane, previewAndDetails, 0.8, 3.2, wideAspect: 1.3, compactFirstWeight: 1, compactSecondWeight: 3) { Margin = new Thickness(10) };
        var toolbar = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto"), ColumnSpacing = 7, Margin = new Thickness(10, 7) };
        toolbar.Children.Add(folder); PutColumn(toolbar, _root, 1); PutColumn(toolbar, refresh, 2); PutColumn(toolbar, cancel, 3);
        var content = new Grid { RowDefinitions = new("Auto,*") }; content.Children.Add(toolbar); Put(content, body, 1); Content = content;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, args) => { args.DragEffects = args.DataTransfer.TryGetFiles()?.Any(file => CanOpen(file.TryGetLocalPath())) == true ? DragDropEffects.Copy : DragDropEffects.None; args.Handled = true; }, RoutingStrategies.Bubble, true);
        AddHandler(DragDrop.DropEvent, async (_, args) =>
        {
            args.Handled = true;
            var path = args.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).FirstOrDefault(CanOpen);
            if (path is not null)
                await OpenAsync(Directory.Exists(path) ? path : Path.GetDirectoryName(path), File.Exists(path) ? path : null);
        }, RoutingStrategies.Bubble, true);
    }

    private Control GeosetPane()
    {
        var all = new Button { Content = "All" }; all.Click += (_, _) => SetGeosets(_ => true);
        var body = new Button { Content = "Body only" }; body.Click += (_, _) => SetGeosets(section => section.GeosetId == 0);
        var defaults = new Button { Content = "Defaults" }; defaults.Click += (_, _) => RestoreGeosets();
        return new DockPanel { LastChildFill = true, Children =
        {
            DockTop(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { defaults, body, all } }),
            new ScrollViewer { Content = _geosets, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }
        }};
    }

    private async Task ChooseFolderAsync()
    {
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null) return;
        var folder = (await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Open model folder", AllowMultiple = false })).FirstOrDefault()?.TryGetLocalPath();
        if (folder is not null) await OpenAsync(folder);
    }

    public async Task OpenAsync(string? path = null, string? selectedPath = null)
    {
        path = string.IsNullOrWhiteSpace(path) ? _settings.ModelBrowserRootPath : path;
        if (string.IsNullOrWhiteSpace(path)) return;
        _scan?.Cancel(); var scan = new CancellationTokenSource(); _scan = scan;
        _summary.Text = "Scanning models and ZIP contents...";
        try
        {
            var catalog = await Task.Run(() => ModelBrowserCatalogService.Scan(path, scan.Token), scan.Token);
            if (_disposed || !ReferenceEquals(_scan, scan)) return;
            _catalog = catalog; _root.Text = catalog.Root; _settings.ModelBrowserRootPath = catalog.Root; _settings.Save();
            if (selectedPath is not null) { _search.Text = string.Empty; _filter.SelectedIndex = 0; }
            Filter();
            _details.Text = string.Join('\n', catalog.Errors.Concat(catalog.UnopenedArchives.Select(archive => "Unopened archive: " + archive)));
            var selected = catalog.Models.FirstOrDefault(entry => entry.FilePath.Equals(selectedPath, StringComparison.OrdinalIgnoreCase)) ?? catalog.Models.FirstOrDefault();
            if (selected is not null)
            {
                _updating = true; _models.SelectedItem = selected; _models.ScrollIntoView(selected); _updating = false;
                await SelectAsync(selected);
            }
            else { _generation++; ClearModel(); _status.Text = "No M2 models found in this folder or its ZIPs."; }
        }
        catch (OperationCanceledException) { if (ReferenceEquals(_scan, scan)) _summary.Text = "Scan cancelled."; }
        catch (Exception exception)
        {
            if (!_disposed && ReferenceEquals(_scan, scan)) _summary.Text = "Scan failed: " + exception.Message;
            DesktopCrashLogger.Log("Model browser scan failed", exception);
        }
        finally { if (ReferenceEquals(_scan, scan)) _scan = null; scan.Dispose(); }
    }

    private void Filter()
    {
        if (_catalog is null) return;
        var query = (_search.Text ?? string.Empty).Trim(); var mode = _filter.SelectedIndex;
        var entries = _catalog.Models.Where(entry => entry.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase) && (mode switch
        {
            1 => entry.ArchiveEntry is null, 2 => entry.ArchiveEntry is not null,
            3 => Review(entry) == "Unreviewed", 4 => Review(entry) == "Keep", 5 => Review(entry) == "Mark for deletion", _ => true
        })).ToArray();
        var selected = _models.SelectedItem;
        _updating = true; _models.ItemsSource = entries; _models.SelectedItem = entries.Contains(selected) ? selected : null; _updating = false;
        _summary.Text = $"{entries.Length:N0} / {_catalog.Models.Count:N0} models | {_catalog.UnopenedArchives.Count:N0} unopened archives | {_catalog.Errors.Count:N0} scan errors";
    }

    private async Task SelectAsync(ModelBrowserEntry entry)
    {
        var generation = ++_generation;
        ClearModel(); _current = entry;
        _modelTitle.Text = entry.Name; _status.Text = "Loading model, skeleton and textures...";
        _updating = true; _review.SelectedItem = Review(entry); _updating = false;
        ModelBrowserSource? source = null;
        var preset = _settings.ModelBrowserPresets.GetValueOrDefault(entry.Identity);
        try
        {
            var loaded = await Task.Run(() =>
            {
                source = new ModelBrowserSource(entry, _catalog);
                var geometry = M2PreviewGeometryService.LoadForViewing(source, visibilityMode: M2PreviewVisibilityMode.AllGeosets);
                var bindings = preset is null ? ModelBrowserTextureService.SuggestBindings(source, geometry)
                    : preset.Textures.Where(pair => geometry.TextureSlots.Any(slot => slot.Index == pair.Key)).ToDictionary();
                var textures = new Dictionary<int, RgbaTexture>(); var errors = new List<string>();
                foreach (var pair in bindings)
                {
                    try { textures[pair.Key] = Path.IsPathFullyQualified(pair.Value) ? BlpTextureService.Decode(pair.Value) : ModelBrowserTextureService.Decode(source, pair.Value); }
                    catch (Exception exception) { errors.Add($"Texture {pair.Key}: {exception.Message}"); }
                }
                return (Geometry: geometry, Bindings: bindings, Textures: textures, Errors: errors);
            });
            if (_disposed || generation != _generation) { source?.Dispose(); return; }
            _source = source; source = null; _fullGeometry = loaded.Geometry;
            foreach (var pair in loaded.Bindings) _bindings[pair.Key] = pair.Value;
            foreach (var pair in loaded.Textures) _decoded[pair.Key] = pair.Value;
            _selectedGeosets.Clear();
            _selectedGeosets.UnionWith(preset is null ? M2GeosetCatalog.BrowserDefaults(loaded.Geometry.Submeshes)
                : loaded.Geometry.Submeshes.Where(section => preset.GeosetIds.Contains(section.GeosetId)).Select(section => section.Index));
            BuildGeosets(); BuildTextures(); ShowGeometry(); _preview.SetDecodedTextures(_decoded);
            _details.Text = $"{entry.RelativePath}\n\n{entry.FormatLabel}\nVertices: {loaded.Geometry.Vertices.Count:N0}\nBones: {loaded.Geometry.Bones.Count:N0}\nAnimations: {loaded.Geometry.Sequences.Count:N0}\n\n"
                + string.Join('\n', loaded.Geometry.PreviewWarnings.Concat(loaded.Errors))
                + "\n\nClient rendering has not been tested. This viewer does not convert or modify the source model.";
            DesktopCrashLogger.Debug("MODEL", "browser-model-loaded", ("path", entry.RelativePath), ("vertices", loaded.Geometry.Vertices.Count), ("bones", loaded.Geometry.Bones.Count), ("animations", loaded.Geometry.Sequences.Count));
        }
        catch (Exception exception)
        {
            source?.Dispose();
            if (_disposed || generation != _generation) return;
            _status.Text = "Could not load model: " + exception.Message; _details.Text = exception.ToString();
            DesktopCrashLogger.Log("Model browser preview failed: " + entry.RelativePath, exception);
        }
    }

    private void ClearModel()
    {
        _preview.ClearGeometry(); _source?.Dispose(); _source = null; _current = null; _fullGeometry = null; _visibleGeometry = null; _modelTitle.Text = string.Empty;
        _textures.Clear(); _geosets.Children.Clear(); _geosetChecks.Clear(); _decoded.Clear(); _bindings.Clear(); _textureRequests.Clear(); _selectedGeosets.Clear();
    }

    private static bool CanOpen(string? path) => Directory.Exists(path) || File.Exists(path) && Path.GetExtension(path).ToLowerInvariant() is ".m2" or ".zip";

    private void BuildGeosets()
    {
        _geosets.Children.Clear(); _geosetChecks.Clear();
        _updating = true; _faces.ItemsSource = null; _updating = false;
        var choices = _fullGeometry!.Submeshes.Where(section => section.GeosetGroup == 32 && section.GeosetVariant > 1)
            .GroupBy(section => section.GeosetId).OrderBy(group => group.Key)
            .Select((group, index) => new FaceChoice(group.Key, $"Face {index + 1} ({group.Sum(section => section.TriangleIndexCount) / 3:N0} triangles)")).ToArray();
        if (choices.Length > 0)
        {
            _updating = true; _faces.ItemsSource = choices;
            _faces.SelectedItem = choices.FirstOrDefault(choice => _fullGeometry.Submeshes.Any(section => section.GeosetId == choice.GeosetId && _selectedGeosets.Contains(section.Index))) ?? choices[0];
            _updating = false;
            _geosets.Children.Add(Label("Face")); _geosets.Children.Add(_faces);
        }
        foreach (var sections in _fullGeometry!.Submeshes.GroupBy(section => section.GeosetId))
        {
            var section = sections.First();
            if (section.GeosetGroup == 32 && section.GeosetVariant > 1) continue;
            var check = new CheckBox { Content = Label($"{section.GeosetId}  {section.GeosetGroupName} ({sections.Sum(part => part.TriangleIndexCount) / 3:N0})"), IsChecked = sections.Any(part => _selectedGeosets.Contains(part.Index)), Tag = sections.Select(part => part.Index).ToArray() };
            check.IsCheckedChanged += (_, _) =>
            {
                if (_updating) return;
                foreach (var part in sections) if (check.IsChecked == true) _selectedGeosets.Add(part.Index); else _selectedGeosets.Remove(part.Index);
                ShowGeometry();
            };
            _geosets.Children.Add(check); _geosetChecks.Add(check);
        }
    }

    private void SetGeosets(Func<M2PreviewSubmesh, bool> predicate)
    {
        if (_fullGeometry is null) return;
        _selectedGeosets.Clear(); foreach (var section in _fullGeometry.Submeshes.Where(predicate)) _selectedGeosets.Add(section.Index);
        ApplyFace();
        _updating = true; foreach (var check in _geosetChecks) check.IsChecked = ((int[])check.Tag!).Any(_selectedGeosets.Contains); _updating = false;
        ShowGeometry();
    }

    private void ShowGeometry()
    {
        if (_fullGeometry is null) return;
        var geometry = _visibleGeometry = M2PreviewGeometryService.SelectGeosets(_fullGeometry, _selectedGeosets);
        _preview.SetGeometry(geometry);
        _textures.UpdateVisibleGeometry(geometry);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_visibleGeometry is not { } geometry) return;
        _status.Text = $"{geometry.TriangleIndices.Count / 3:N0} triangles | {geometry.Sequences.Count:N0} animations | {geometry.UsedTextureDefinitionIndices.Count(_decoded.ContainsKey)}/{geometry.UsedTextureDefinitionIndices.Count} visible textures";
        if (geometry.PreviewWarnings.Count > 0) _status.Text += $" | {geometry.PreviewWarnings.Count} rendering limitation(s), see Details";
    }

    private void BuildTextures()
    {
        var paths = _source!.Files.Where(path => path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase));
        if (_current?.ArchiveEntry is not null && _catalog is not null)
            paths = paths.Concat(_catalog.Files.Where(path => path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)));
        _textures.Load(_fullGeometry!, _bindings, _decoded.Keys, paths);
    }

    private async Task SetTextureAsync(int slot, string? path)
    {
        if (_source is null) return;
        var source = _source; var generation = _generation;
        var request = _textureRequests.GetValueOrDefault(slot) + 1; _textureRequests[slot] = request;
        try
        {
            if (path is null or "(Unassigned)") { _bindings.Remove(slot); _decoded.Remove(slot); }
            else
            {
                var texture = await Task.Run(() => Path.IsPathFullyQualified(path) ? BlpTextureService.Decode(path) : ModelBrowserTextureService.Decode(source, path));
                if (_disposed || generation != _generation || _textureRequests.GetValueOrDefault(slot) != request) return;
                _bindings[slot] = path; _decoded[slot] = texture;
            }
            _preview.SetDecodedTextures(_decoded);
            _textures.UpdateBindings(_bindings, _decoded.Keys); UpdateStatus();
        }
        catch (Exception exception)
        {
            if (_disposed || generation != _generation || _textureRequests.GetValueOrDefault(slot) != request) return;
            _textures.UpdateBindings(_bindings, _decoded.Keys);
            _status.Text = "Texture failed: " + exception.Message; DesktopCrashLogger.Log("Model browser texture failed", exception);
        }
    }

    private string Review(ModelBrowserEntry entry) => _settings.ModelBrowserReviews.GetValueOrDefault(entry.Identity) ?? "Unreviewed";
    private void RestoreGeosets()
    {
        if (_fullGeometry is null) return;
        var preset = _current is null ? null : _settings.ModelBrowserPresets.GetValueOrDefault(_current.Identity);
        _selectedGeosets.Clear();
        _selectedGeosets.UnionWith(preset is null ? M2GeosetCatalog.BrowserDefaults(_fullGeometry.Submeshes)
            : _fullGeometry.Submeshes.Where(section => preset.GeosetIds.Contains(section.GeosetId)).Select(section => section.Index));
        BuildGeosets(); ShowGeometry();
    }

    private void SaveDefaults()
    {
        if (_current is null || _fullGeometry is null) return;
        try
        {
            _settings.ModelBrowserPresets[_current.Identity] = new(_fullGeometry.Submeshes.Where(section => _selectedGeosets.Contains(section.Index)).Select(section => (int)section.GeosetId).Distinct().ToArray(), new(_bindings));
            _settings.Save(); _status.Text = "Defaults saved for " + _current.Name;
        }
        catch (Exception exception) { _status.Text = "Could not save defaults: " + exception.Message; DesktopCrashLogger.Log("Model defaults save failed", exception); }
    }

    private async Task ResetDefaultsAsync()
    {
        if (_current is null || _fullGeometry is null || _source is null) return;
        try
        {
            _settings.ModelBrowserPresets.Remove(_current.Identity); _settings.Save(); RestoreGeosets();
            var suggested = ModelBrowserTextureService.SuggestBindings(_source, _fullGeometry); var generation = _generation;
            foreach (var slot in _fullGeometry.TextureSlots)
            {
                await SetTextureAsync(slot.Index, suggested.GetValueOrDefault(slot.Index));
                if (generation != _generation || _disposed) return;
            }
            _status.Text = "Saved defaults cleared for " + _current.Name;
        }
        catch (Exception exception) { _status.Text = "Could not reset defaults: " + exception.Message; DesktopCrashLogger.Log("Model defaults reset failed", exception); }
    }
    private void ApplyFace()
    {
        if (_fullGeometry is null || _faces.SelectedItem is not FaceChoice face) return;
        foreach (var section in _fullGeometry.Submeshes.Where(section => section.GeosetGroup == 32 && section.GeosetVariant > 1))
            if (section.GeosetId == face.GeosetId) _selectedGeosets.Add(section.Index); else _selectedGeosets.Remove(section.Index);
    }
    private void SaveReview()
    {
        if (_updating || _current is null || _review.SelectedItem is not string review) return;
        try { if (review == "Unreviewed") _settings.ModelBrowserReviews.Remove(_current.Identity); else _settings.ModelBrowserReviews[_current.Identity] = review; _settings.Save(); Filter(); }
        catch (Exception exception) { _status.Text = "Could not save review: " + exception.Message; DesktopCrashLogger.Log("Model review save failed", exception); }
    }

    private void Move(int direction)
    {
        if (_models.ItemCount == 0) return;
        _models.SelectedIndex = Math.Clamp(_models.SelectedIndex + direction, 0, _models.ItemCount - 1);
        _models.ScrollIntoView(_models.SelectedItem!);
    }

    private void RevealSource()
    {
        if (_current is null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{_current.FilePath}\"", UseShellExecute = true }); }
        catch (Exception exception) { _status.Text = "Could not open source folder: " + exception.Message; }
    }

    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2), VerticalAlignment = VerticalAlignment.Center };
    private static Control DockTop(Control control) { DockPanel.SetDock(control, Dock.Top); return control; }
    private static void Put(Grid grid, Control control, int row) { Grid.SetRow(control, row); grid.Children.Add(control); }
    private static void PutColumn(Grid grid, Control control, int column) { Grid.SetColumn(control, column); grid.Children.Add(control); }
    public void Dispose() { _disposed = true; _generation++; _scan?.Cancel(); _preview.Dispose(); _source?.Dispose(); _source = null; }
}
