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
    private readonly ComboBox _filter = new() { ItemsSource = new[] { "All models", "Loose files", "Inside ZIPs", "Unreviewed", "Keep", "Skip" }, SelectedIndex = 0 };
    private readonly TextBlock _summary = Label("No folder opened.");
    private readonly TextBlock _status = Label("No model selected.");
    private readonly TextBlock _modelTitle = Label(string.Empty);
    private readonly TextBox _details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _geosets = new() { Spacing = 4 };
    private readonly StackPanel _textures = new() { Spacing = 9 };
    private readonly M2PreviewView _preview = new();
    private readonly Dictionary<int, RgbaTexture> _decoded = [];
    private readonly Dictionary<int, string> _bindings = [];
    private readonly Dictionary<int, int> _textureRequests = [];
    private readonly HashSet<int> _selectedGeosets = [];
    private readonly List<CheckBox> _geosetChecks = [];
    private readonly ComboBox _review = new() { ItemsSource = new[] { "Unreviewed", "Keep", "Skip" }, SelectedIndex = 0 };
    private ModelBrowserCatalog? _catalog;
    private ModelBrowserSource? _source;
    private ModelBrowserEntry? _current;
    private M2PreviewGeometry? _fullGeometry;
    private CancellationTokenSource? _scan;
    private int _generation;
    private bool _updating;
    private bool _disposed;

    public ModelBrowserView(DesktopSettings settings)
    {
        _settings = settings;
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
            new TabItem { Header = "Textures", Foreground = Brush.Parse("#E8EBF2"), FontSize = 13, Padding = new Thickness(8), Content = new ScrollViewer { Content = _textures, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } },
            new TabItem { Header = "Geosets", Foreground = Brush.Parse("#E8EBF2"), FontSize = 13, Padding = new Thickness(8), Content = GeosetPane() },
            new TabItem { Header = "Details", Foreground = Brush.Parse("#E8EBF2"), FontSize = 13, Padding = new Thickness(8), Content = _details }
        }};
        var previewAndDetails = new ResponsiveSplitGrid(modelPane, tabs, 2.6, 1, wideAspect: 1.05, compactFirstWeight: 3, compactSecondWeight: 1);
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
        var defaults = new Button { Content = "Default" }; defaults.Click += (_, _) => SetGeosets(DefaultGeoset);
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
            3 => Review(entry) == "Unreviewed", 4 => Review(entry) == "Keep", 5 => Review(entry) == "Skip", _ => true
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
        try
        {
            var loaded = await Task.Run(() =>
            {
                source = new ModelBrowserSource(entry, _catalog);
                var geometry = M2PreviewGeometryService.LoadForViewing(source, visibilityMode: M2PreviewVisibilityMode.AllGeosets);
                var bindings = ModelBrowserTextureService.SuggestBindings(source, geometry);
                var textures = new Dictionary<int, RgbaTexture>(); var errors = new List<string>();
                foreach (var pair in bindings)
                {
                    try { textures[pair.Key] = ModelBrowserTextureService.Decode(source, pair.Value); }
                    catch (Exception exception) { errors.Add($"Texture {pair.Key}: {exception.Message}"); }
                }
                return (Geometry: geometry, Bindings: bindings, Textures: textures, Errors: errors);
            });
            if (_disposed || generation != _generation) { source?.Dispose(); return; }
            _source = source; source = null; _fullGeometry = loaded.Geometry;
            foreach (var pair in loaded.Bindings) _bindings[pair.Key] = pair.Value;
            foreach (var pair in loaded.Textures) _decoded[pair.Key] = pair.Value;
            _selectedGeosets.Clear();
            foreach (var section in loaded.Geometry.Submeshes.Where(DefaultGeoset)) _selectedGeosets.Add(section.Index);
            BuildGeosets(); BuildTextures(); ShowGeometry();
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

    private static bool DefaultGeoset(M2PreviewSubmesh section) => section.GeosetId is 0 or 1 || section.GeosetId % 100 == 1 && section.GeosetId >= 100;

    private void ClearModel()
    {
        _preview.ClearGeometry(); _source?.Dispose(); _source = null; _current = null; _fullGeometry = null; _modelTitle.Text = string.Empty;
        _textures.Children.Clear(); _geosets.Children.Clear(); _geosetChecks.Clear(); _decoded.Clear(); _bindings.Clear(); _textureRequests.Clear(); _selectedGeosets.Clear();
    }

    private static bool CanOpen(string? path) => Directory.Exists(path) || File.Exists(path) && Path.GetExtension(path).ToLowerInvariant() is ".m2" or ".zip";

    private void BuildGeosets()
    {
        foreach (var section in _fullGeometry!.Submeshes)
        {
            var check = new CheckBox { Content = $"{section.GeosetId}  {section.GeosetGroupName} ({section.TriangleIndexCount / 3:N0})", IsChecked = _selectedGeosets.Contains(section.Index), Tag = section.Index };
            check.IsCheckedChanged += (_, _) =>
            {
                if (_updating) return;
                if (check.IsChecked == true) _selectedGeosets.Add(section.Index); else _selectedGeosets.Remove(section.Index);
                ShowGeometry();
            };
            _geosets.Children.Add(check); _geosetChecks.Add(check);
        }
    }

    private void SetGeosets(Func<M2PreviewSubmesh, bool> predicate)
    {
        if (_fullGeometry is null) return;
        _selectedGeosets.Clear(); foreach (var section in _fullGeometry.Submeshes.Where(predicate)) _selectedGeosets.Add(section.Index);
        _updating = true; foreach (var check in _geosetChecks) check.IsChecked = _selectedGeosets.Contains((int)check.Tag!); _updating = false;
        ShowGeometry();
    }

    private void ShowGeometry()
    {
        if (_fullGeometry is null) return;
        var geometry = M2PreviewGeometryService.SelectGeosets(_fullGeometry, _selectedGeosets);
        _preview.SetGeometry(geometry); _preview.SetDecodedTextures(_decoded);
        _status.Text = $"{geometry.TriangleIndices.Count / 3:N0} triangles | {geometry.Sequences.Count:N0} animations | {geometry.UsedTextureDefinitionIndices.Count(_decoded.ContainsKey)}/{geometry.UsedTextureDefinitionIndices.Count} visible textures";
        if (geometry.PreviewWarnings.Count > 0) _status.Text += $" | {geometry.PreviewWarnings.Count} rendering limitation(s), see Details";
    }

    private void BuildTextures()
    {
        var choices = new[] { "(Unassigned)" }.Concat(_source!.Nearby(".blp")).ToArray();
        foreach (var slot in _fullGeometry!.TextureSlots)
        {
            _textures.Children.Add(Label($"{slot.Index}: {ModelBrowserTextureService.SlotName(slot.Type)}" + (slot.FileDataId == 0 ? "" : $" | ID {slot.FileDataId}")));
            var selected = _bindings.GetValueOrDefault(slot.Index);
            var values = selected is not null && !choices.Contains(selected) ? choices.Append(selected).ToArray() : choices;
            var picker = new ComboBox { ItemsSource = values, MaxDropDownHeight = 330, HorizontalAlignment = HorizontalAlignment.Stretch, SelectedItem = selected ?? "(Unassigned)" };
            picker.ItemTemplate = new FuncDataTemplate<string>((path, _) => new TextBlock { Text = path is null ? string.Empty : Path.GetFileName(path), TextTrimming = TextTrimming.CharacterEllipsis });
            picker.SelectionChanged += async (_, _) => await SetTextureAsync(slot.Index, picker.SelectedItem as string);
            var browse = new Button { Content = "...", Width = 32, Padding = new Thickness(3) }; ToolTip.SetTip(browse, "Choose a BLP texture");
            browse.Click += async (_, _) =>
            {
                var provider = TopLevel.GetTopLevel(this)?.StorageProvider; if (provider is null) return;
                var generation = _generation;
                var path = (await provider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose BLP texture", AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("BLP textures") { Patterns = ["*.blp"] }] })).FirstOrDefault()?.TryGetLocalPath();
                if (path is null || _disposed || generation != _generation) return;
                var updated = ((IEnumerable<string>)picker.ItemsSource!).Append(path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                picker.ItemsSource = updated; picker.SelectedItem = path;
            };
            var row = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 4 }; row.Children.Add(picker); PutColumn(row, browse, 1);
            _textures.Children.Add(row);
        }
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
        }
        catch (Exception exception)
        {
            if (_disposed || generation != _generation || _textureRequests.GetValueOrDefault(slot) != request) return;
            _status.Text = "Texture failed: " + exception.Message; DesktopCrashLogger.Log("Model browser texture failed", exception);
        }
    }

    private string Review(ModelBrowserEntry entry) => _settings.ModelBrowserReviews.GetValueOrDefault(entry.Identity) ?? "Unreviewed";
    private void SaveReview()
    {
        if (_updating || _current is null || _review.SelectedItem is not string review) return;
        try { if (review == "Unreviewed") _settings.ModelBrowserReviews.Remove(_current.Identity); else _settings.ModelBrowserReviews[_current.Identity] = review; _settings.Save(); }
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
