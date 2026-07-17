using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;
using WoWCrucible.Desktop.Controls;

namespace WoWCrucible.Desktop;

internal sealed class AssetComparisonWindow : Window
{
    private const int PageSize = 96;
    private readonly TextBox _library = new() { Text = Directory.Exists(@"G:\Crucible-Extras-Processed") ? @"G:\Crucible-Extras-Processed" : string.Empty };
    private readonly TextBox _directorySearch = new() { PlaceholderText = "Filter content paths…" };
    private readonly ListBox _directories = new();
    private readonly TextBox _fileSearch = new() { PlaceholderText = "Optional filename filter…" };
    private readonly ComboBox _sourceFilter = new() { MinWidth = 180 };
    private readonly ComboBox _sort = new() { MinWidth = 170, ItemsSource = new[] { "Source then name", "Name A–Z", "Size: largest first", "Size: smallest first" }, SelectedIndex = 0 };
    private readonly Button _scanDuplicates = new() { Content = "Scan exact copies" };
    private readonly CheckBox _collapseDuplicates = new() { Content = "Collapse exact copies", IsEnabled = false };
    private readonly CheckBox _undecidedOnly = new() { Content = "Undecided only", IsChecked = true };
    private readonly CheckBox _autoAdvance = new() { Content = "Auto-advance", IsChecked = true };
    private readonly ComboBox _previewMode = new() { MinWidth = 165, ItemsSource = new[] { "Image comparison", "Live model preview" }, SelectedIndex = 0 };
    private readonly ComboBox _modelPicker = new() { MinWidth = 300 };
    private readonly TextBox _modelSearch = new() { PlaceholderText = "Filter discovered M2 models…" };
    private readonly ComboBox _modelFilter = new() { MinWidth = 150, ItemsSource = new[] { "Ready models", "All models" }, SelectedIndex = 0 };
    private readonly TextBox _assetCategory = new() { Text = "Unsorted", MinWidth = 115, PlaceholderText = "Category" };
    private readonly TextBox _assetNotes = new() { MinWidth = 180, PlaceholderText = "Optional decision notes" };
    private readonly TextBlock _projectStatus = new() { Foreground = Brush.Parse("#99A5B8"), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _modelStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#99A5B8") };
    private readonly M2PreviewView _modelView = new();
    private readonly WrapPanel _cards = new() { Orientation = Orientation.Horizontal, ItemWidth = 176, ItemHeight = 206 };
    private readonly TextBlock _folderTitle = new() { FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _pageStatus = new() { Foreground = Brush.Parse("#8F9AAE") };
    private readonly TextBlock _status = new() { Text = "Choose a content-first asset library.", Foreground = Brush.Parse("#99A5B8") };
    private readonly Image[] _comparisonImages = [new() { Stretch = Stretch.Uniform }, new() { Stretch = Stretch.Uniform }];
    private readonly ScrollViewer[] _comparisonScrolls = [new(), new()];
    private readonly TextBlock[] _comparisonTitles = [new() { TextWrapping = TextWrapping.Wrap }, new() { TextWrapping = TextWrapping.Wrap }];
    private readonly Button[] _slotButtons = [new() { Content = "LEFT SLOT" }, new() { Content = "RIGHT SLOT" }];
    private readonly Bitmap?[] _comparisonBitmaps = new Bitmap?[2];
    private readonly List<Bitmap> _thumbnailBitmaps = [];
    private AssetComparisonIndex? _index; private IReadOnlyList<AssetComparisonEntry> _folderEntries = []; private IReadOnlyList<AssetComparisonEntry> _filteredEntries = [];
    private IReadOnlyDictionary<string, AssetComparisonDuplicateGroup> _duplicateByPath = new Dictionary<string, AssetComparisonDuplicateGroup>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<AssetComparisonModel> _allModels = []; private IReadOnlyList<AssetComparisonModel> _folderModels = []; private Control? _imageComparisonPane; private Control? _modelPreviewPane; private Control? _imageComparisonTools; private AssetComparisonEntry? _selectedTexture; private M2PreviewGeometry? _loadedModelGeometry;
    private string _modelDiscoveryScope = string.Empty; private string? _projectPath; private DefinitiveAssetProject? _project; private AssetDependencyGraph? _modelDependencyGraph;
    private CancellationTokenSource? _thumbnailCancellation; private CancellationTokenSource? _duplicateScanCancellation; private int _page; private int _activeSlot; private double _zoom = 1; private bool _syncingScroll; private bool _settingSourceFilter;

    public AssetComparisonWindow(string? libraryRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(libraryRoot)) _library.Text = libraryRoot;
        Opened += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_library.Text) && Directory.Exists(_library.Text)) await LoadIndexAsync();
        };
        Title = "WoW Crucible — Visual Asset Comparison"; Width = 1580; Height = 940; MinWidth = 1120; MinHeight = 700; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _directories.ItemTemplate = new FuncDataTemplate<AssetComparisonDirectory>((item, _) => item is null ? new Grid() : new Grid
        {
            ColumnDefinitions = new("*,Auto"), Margin = new Thickness(3, 2), Children =
            {
                new TextBlock { Text = string.IsNullOrEmpty(item.LogicalPath) ? "(archive root)" : item.LogicalPath, TextTrimming = TextTrimming.CharacterEllipsis },
                WithColumn(new TextBlock { Text = $"{item.PngFiles:N0} · {item.ProvenanceSources:N0} src", Foreground = Brush.Parse("#7F8A9F"), FontSize = 10, Margin = new Thickness(8,0,0,0) }, 1)
            }
        });
        _directories.SelectionChanged += async (_, _) => await SelectDirectoryAsync(); _directorySearch.TextChanged += (_, _) => FilterDirectories();
        _fileSearch.TextChanged += async (_, _) => await FilterFilesAsync(); _sourceFilter.SelectionChanged += async (_, _) => { if (!_settingSourceFilter) await FilterFilesAsync(); };
        _sort.SelectionChanged += async (_, _) => await FilterFilesAsync(); _collapseDuplicates.Click += async (_, _) => await FilterFilesAsync(); _undecidedOnly.Click += async (_, _) => await FilterFilesAsync(); _scanDuplicates.Click += async (_, _) => await ScanDuplicatesAsync();
        _previewMode.SelectionChanged += async (_, _) => await ChangePreviewModeAsync(); _modelPicker.SelectionChanged += async (_, _) => await LoadSelectedModelAsync();
        _modelSearch.TextChanged += (_, _) => FilterModels(); _modelFilter.SelectionChanged += (_, _) => FilterModels();
        for (var index = 0; index < 2; index++) { var slot = index; _slotButtons[index].Click += (_, _) => SetActiveSlot(slot); }
        KeyDown += async (_, e) => await HandleDecisionKeyAsync(e); SetActiveSlot(0); Content = BuildLayout(); Closed += (_, _) => DisposeImages();
    }

    private Control BuildLayout()
    {
        var browse = new Button { Content = "Browse…" }; browse.Click += async (_, _) => await BrowseLibraryAsync();
        var load = AccentButton("Index library"); load.Click += async (_, _) => await LoadIndexAsync();
        var top = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto"), Margin = new Thickness(14, 12), ColumnSpacing = 8 };
        top.Children.Add(new TextBlock { Text = "ASSET LIBRARY", FontWeight = FontWeight.Bold, FontSize = 10, Foreground = Brush.Parse("#C58A2B"), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_library, 1); top.Children.Add(_library); Grid.SetColumn(browse, 2); top.Children.Add(browse); Grid.SetColumn(load, 3); top.Children.Add(load);

        var left = new Grid { RowDefinitions = new("Auto,*"), Margin = new Thickness(10) };
        left.Children.Add(_directorySearch); Grid.SetRow(_directories, 1); _directories.Margin = new Thickness(0, 8, 0, 0); left.Children.Add(_directories);

        var previous = new Button { Content = "← Previous" }; previous.Click += async (_, _) => { if (_page > 0) { _page--; await RenderPageAsync(); } };
        var next = new Button { Content = "Next →" }; next.Click += async (_, _) => { if ((_page + 1) * PageSize < _filteredEntries.Count) { _page++; await RenderPageAsync(); } };
        var duplicateTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _scanDuplicates, _collapseDuplicates, _undecidedOnly, _autoAdvance, new TextBlock { Text = "Keys: K keep · A alternative · R review · X reject", VerticalAlignment = VerticalAlignment.Center, Foreground = Brush.Parse("#8793A7"), FontSize = 10 } } };
        var filters = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { _fileSearch, WithColumn(_sourceFilter, 1), WithColumn(_sort, 2) } };
        var keeper = AccentButton("KEEP"); keeper.Click += async (_, _) => await RecordCurrentAsync(AssetDecision.Keeper);
        var alternative = new Button { Content = "Alternative" }; alternative.Click += async (_, _) => await RecordCurrentAsync(AssetDecision.Alternative);
        var reject = new Button { Content = "Reject" }; reject.Click += async (_, _) => await RecordCurrentAsync(AssetDecision.Rejected);
        var review = new Button { Content = "Review later" }; review.Click += async (_, _) => await RecordCurrentAsync(AssetDecision.Review);
        var stage = new Button { Content = "Stage keepers…" }; stage.Click += async (_, _) => await StageKeepersAsync();
        var revealProject = new Button { Content = "Reveal project" }; revealProject.Click += (_, _) => RevealProject();
        var projectTools = new WrapPanel { Orientation = Orientation.Horizontal, ItemHeight = 34, Children = { keeper, alternative, reject, review, _assetCategory, _assetNotes, stage, revealProject, _projectStatus } };
        var middleHeader = new StackPanel { Spacing = 8, Margin = new Thickness(10, 10, 10, 4), Children = { _folderTitle, new TextBlock { Text = "Every PNG directly in this content directory is shown from archive patches and Loose content. Choose a card, then record it in the persistent Definitive Set below.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8793A7"), FontSize = 11 }, filters, duplicateTools, projectTools } };
        var pager = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(10, 5), Children = { previous, next, _pageStatus } };
        var middle = new Grid { RowDefinitions = new("Auto,*,Auto") }; middle.Children.Add(middleHeader);
        var cardScroll = new ScrollViewer { Content = _cards, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Margin = new Thickness(8) };
        Grid.SetRow(cardScroll, 1); middle.Children.Add(cardScroll); Grid.SetRow(pager, 2); middle.Children.Add(pager);

        var compare = BuildComparisonPane();
        var body = new Grid { ColumnDefinitions = new("330,520,*") };
        body.Children.Add(new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0,0,1,0), Child = left });
        Grid.SetColumn(middle, 1); body.Children.Add(middle); Grid.SetColumn(compare, 2); body.Children.Add(compare);
        var root = new Grid { RowDefinitions = new("Auto,*,Auto") }; root.Children.Add(top); Grid.SetRow(body, 1); root.Children.Add(body);
        var status = new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0,1,0,0), Padding = new Thickness(14,7), Child = _status }; Grid.SetRow(status, 2); root.Children.Add(status); return root;
    }

    private Control BuildComparisonPane()
    {
        var zoom = new Slider { Minimum = 0.1, Maximum = 4, Value = 1, Width = 180 }; zoom.ValueChanged += (_, e) => { _zoom = e.NewValue; ApplyZoom(); };
        var openLeft = new Button { Content = "Reveal left" }; openLeft.Click += (_, _) => RevealSlot(0); var openRight = new Button { Content = "Reveal right" }; openRight.Click += (_, _) => RevealSlot(1);
        _imageComparisonTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "SYNCHRONIZED ZOOM & PAN", VerticalAlignment = VerticalAlignment.Center, FontSize = 10, FontWeight = FontWeight.Bold }, zoom, openLeft, openRight } };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12), Children = { _previewMode, _imageComparisonTools } };
        var grid = new Grid { ColumnDefinitions = new("*,*"), RowDefinitions = new("Auto,*") };
        for (var index = 0; index < 2; index++)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _slotButtons[index], _comparisonTitles[index] } }; Grid.SetColumn(header, index); grid.Children.Add(header);
            _comparisonScrolls[index].Content = _comparisonImages[index]; _comparisonScrolls[index].HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto; _comparisonScrolls[index].VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
            var scrollIndex = index; _comparisonScrolls[index].ScrollChanged += (_, _) => SyncScroll(scrollIndex);
            var imageBorder = new Border { Background = Brush.Parse("#090B0F"), BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(1), Margin = new Thickness(3), ClipToBounds = true, Child = _comparisonScrolls[index] };
            Grid.SetRow(imageBorder, 1); Grid.SetColumn(imageBorder, index); grid.Children.Add(imageBorder);
        }
        _imageComparisonPane = grid;
        var previousModel = new Button { Content = "← Model" }; previousModel.Click += (_, _) => MoveModel(-1); var nextModel = new Button { Content = "Model →" }; nextModel.Click += (_, _) => MoveModel(1);
        var modelFilters = new Grid { ColumnDefinitions = new("*,Auto,Auto,Auto"), ColumnSpacing = 7, Children = { _modelSearch, WithColumn(_modelFilter, 1), WithColumn(previousModel, 2), WithColumn(nextModel, 3) } };
        var modelHeader = new StackPanel { Spacing = 7, Margin = new Thickness(9), Children = { new TextBlock { Text = "AUTOMATIC M2 MODEL BROWSER", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#C58A2B") }, modelFilters, _modelPicker, _modelStatus } };
        var modelPane = new Grid { RowDefinitions = new("Auto,*"), IsVisible = false, Children = { modelHeader } };
        var modelBorder = new Border { Background = Brush.Parse("#090D14"), BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(1), Child = _modelView }; Grid.SetRow(modelBorder, 1); modelPane.Children.Add(modelBorder); _modelPreviewPane = modelPane;
        var previewHost = new Grid { Children = { grid, modelPane } };
        var root = new Grid { RowDefinitions = new("Auto,*"), Margin = new Thickness(6) }; root.Children.Add(toolbar); Grid.SetRow(previewHost, 1); root.Children.Add(previewHost); return root;
    }

    private async Task LoadIndexAsync()
    {
        _status.Text = "Reading the catalog and grouping PNGs by content directory…";
        var libraryRoot = _library.Text ?? string.Empty;
        var stopwatch = Stopwatch.StartNew();
        DesktopCrashLogger.Debug("ASSET", "index-start", ("library", libraryRoot));
        try
        {
            _index = await Task.Run(() => AssetComparisonService.BuildIndex(libraryRoot));
            _projectPath = DefinitiveAssetProjectService.DefaultPath(libraryRoot); _project = DefinitiveAssetProjectService.LoadOrCreate(_projectPath, libraryRoot); UpdateProjectStatus();
            FilterDirectories();
            _status.Text = $"Indexed {_index.TotalPngFiles:N0} PNGs across {_index.Directories.Count:N0} content directories.";
            DesktopCrashLogger.Log($"Asset comparison indexed {_index.TotalPngFiles:N0} PNGs across {_index.Directories.Count:N0} content directories from {libraryRoot}", null);
            DesktopCrashLogger.Debug("ASSET", "index-success", ("library", libraryRoot), ("pngs", _index.TotalPngFiles), ("directories", _index.Directories.Count), ("duration_ms", stopwatch.Elapsed.TotalMilliseconds));
        }
        catch (Exception exception) { DesktopCrashLogger.Log("Asset comparison indexing failed", exception); _status.Text = exception.Message; }
    }

    private void FilterDirectories()
    {
        if (_index is null) return; var query = _directorySearch.Text?.Trim() ?? string.Empty;
        _directories.ItemsSource = _index.Directories.Where(directory => query.Length == 0 || directory.LogicalPath.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private async Task SelectDirectoryAsync()
    {
        if (_index is null || _directories.SelectedItem is not AssetComparisonDirectory directory) return;
        _duplicateScanCancellation?.Cancel();
        _folderTitle.Text = string.IsNullOrEmpty(directory.LogicalPath) ? "(archive root)" : directory.LogicalPath; _status.Text = "Reading direct PNG variants…";
        var stopwatch = Stopwatch.StartNew();
        _folderEntries = await Task.Run(() => AssetComparisonService.GetDirectoryPngs(_index, directory.LogicalPath));
        _loadedModelGeometry = null; _modelDependencyGraph = null; _modelView.ClearGeometry(); _modelView.SetTexture(null); var discovered = await Task.Run(() => AssetComparisonService.GetRelevantModels(_index, directory.LogicalPath)); _allModels = discovered.Models; _modelDiscoveryScope = discovered.DiscoveryScope; FilterModels();
        _modelStatus.Text = _allModels.Count == 0 ? "No M2 files were found in this path or its parent content paths." : $"Discovered {_allModels.Count:N0} M2 file(s) under '{_modelDiscoveryScope}', including {_allModels.Count(model => model.Compatibility == AssetModelCompatibility.Ready):N0} ready for live preview.";
        _duplicateByPath = new Dictionary<string, AssetComparisonDuplicateGroup>(StringComparer.OrdinalIgnoreCase); _collapseDuplicates.IsChecked = false; _collapseDuplicates.IsEnabled = false;
        var sources = new[] { "All patch sources" }.Concat(_folderEntries.Select(entry => entry.Provenance).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)).ToArray();
        _settingSourceFilter = true; _sourceFilter.ItemsSource = sources; _sourceFilter.SelectedIndex = 0; _settingSourceFilter = false; await FilterFilesAsync();
        DesktopCrashLogger.Debug("ASSET", "directory-selected", ("path", directory.LogicalPath), ("pngs", _folderEntries.Count), ("sources", sources.Length - 1), ("models", _folderModels.Count), ("duration_ms", stopwatch.Elapsed.TotalMilliseconds));
    }

    private async Task FilterFilesAsync()
    {
        var query = _fileSearch.Text?.Trim() ?? string.Empty; var source = _sourceFilter.SelectedItem as string;
        IEnumerable<AssetComparisonEntry> filtered = _folderEntries.Where(entry => (query.Length == 0 || entry.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)) && (string.IsNullOrEmpty(source) || source == "All patch sources" || entry.Provenance.Equals(source, StringComparison.OrdinalIgnoreCase)) && (_undecidedOnly.IsChecked != true || DecisionFor(entry.FullPath) is null));
        if (_collapseDuplicates.IsChecked == true) filtered = filtered.GroupBy(entry => _duplicateByPath.TryGetValue(entry.FullPath, out var group) ? group.Sha256 : entry.FullPath, StringComparer.OrdinalIgnoreCase).Select(group => group.First());
        filtered = (_sort.SelectedItem as string) switch
        {
            "Name A–Z" => filtered.OrderBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.Provenance, StringComparer.OrdinalIgnoreCase),
            "Size: largest first" => filtered.OrderByDescending(entry => entry.Bytes).ThenBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase),
            "Size: smallest first" => filtered.OrderBy(entry => entry.Bytes).ThenBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderBy(entry => entry.Provenance, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
        };
        _filteredEntries = filtered.ToArray();
        DesktopCrashLogger.Debug("ASSET", "filter-applied", ("query", query), ("source", source), ("sort", _sort.SelectedItem), ("collapse_exact", _collapseDuplicates.IsChecked), ("matches", _filteredEntries.Count));
        _page = 0; await RenderPageAsync();
    }

    private async Task RenderPageAsync()
    {
        _thumbnailCancellation?.Cancel(); _thumbnailCancellation?.Dispose(); _thumbnailCancellation = new(); var token = _thumbnailCancellation.Token;
        foreach (var bitmap in _thumbnailBitmaps) bitmap.Dispose(); _thumbnailBitmaps.Clear(); _cards.Children.Clear();
        var page = _filteredEntries.Skip(_page * PageSize).Take(PageSize).ToArray(); var images = new List<(Image Image, AssetComparisonEntry Entry)>();
        foreach (var entry in page)
        {
            var image = new Image { Width = 148, Height = 148, Stretch = Stretch.Uniform };
            var duplicateText = _duplicateByPath.TryGetValue(entry.FullPath, out var duplicate) ? $" · {duplicate.Entries.Count:N0} exact copies" : string.Empty; var decision = DecisionFor(entry.FullPath); var decisionText = decision is null ? string.Empty : $" · {decision}";
            var card = new Button { Width = 170, Height = 200, Margin = new Thickness(3), Padding = new Thickness(7), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
                Content = new StackPanel { Spacing = 4, Children = { image, new TextBlock { Text = entry.FileName, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 }, new TextBlock { Text = $"{entry.Provenance} · {FormatBytes(entry.Bytes)}{duplicateText}{decisionText}", TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brush.Parse("#C58A2B"), FontSize = 10 } } } };
            card.Click += async (_, _) => await SelectComparisonAsync(entry); _cards.Children.Add(card); images.Add((image, entry));
        }
        _pageStatus.Text = _filteredEntries.Count == 0 ? "No PNGs match." : $"{_page * PageSize + 1:N0}–{_page * PageSize + page.Length:N0} of {_filteredEntries.Count:N0}";
        _status.Text = $"Showing all direct PNGs in this path · {_folderEntries.Count:N0} total · {_folderEntries.Select(entry => entry.Provenance).Distinct(StringComparer.OrdinalIgnoreCase).Count():N0} patch sources.";
        foreach (var item in images)
        {
            try { var bitmap = await Task.Run(() => { token.ThrowIfCancellationRequested(); using var stream = File.OpenRead(item.Entry.FullPath); return Bitmap.DecodeToWidth(stream, 148, BitmapInterpolationMode.MediumQuality); }, token); if (!token.IsCancellationRequested) { item.Image.Source = bitmap; _thumbnailBitmaps.Add(bitmap); } else bitmap.Dispose(); }
            catch (OperationCanceledException) { break; }
            catch (Exception exception) { DesktopCrashLogger.Log($"Asset thumbnail decode failed: {item.Entry.FullPath}", exception); }
        }
    }

    private async Task SelectComparisonAsync(AssetComparisonEntry entry)
    {
        _selectedTexture = entry; _modelView.SetTexture(entry.FullPath); UpdateModelStatus();
        var slot = _activeSlot; try
        {
            var bitmap = await Task.Run(() => new Bitmap(entry.FullPath)); _comparisonBitmaps[slot]?.Dispose(); _comparisonBitmaps[slot] = bitmap; _comparisonImages[slot].Source = bitmap;
            ApplyZoom();
            _comparisonTitles[slot].Text = $"{entry.Provenance}\n{entry.FileName}\n{bitmap.PixelSize.Width:N0}×{bitmap.PixelSize.Height:N0} · {entry.Bytes / 1024d:0.#} KiB";
            _comparisonImages[slot].Tag = entry; SetActiveSlot(slot == 0 ? 1 : 0);
        }
        catch (Exception exception) { DesktopCrashLogger.Log("Asset comparison image load failed", exception); _status.Text = exception.Message; }
    }

    private async Task ChangePreviewModeAsync()
    {
        var modelMode = _previewMode.SelectedIndex == 1;
        if (_imageComparisonPane is not null) _imageComparisonPane.IsVisible = !modelMode;
        if (_modelPreviewPane is not null) _modelPreviewPane.IsVisible = modelMode;
        if (_imageComparisonTools is not null) _imageComparisonTools.IsVisible = !modelMode;
        if (modelMode) await LoadSelectedModelAsync();
    }

    private async Task LoadSelectedModelAsync()
    {
        if (_previewMode.SelectedIndex != 1) return;
        if (_modelPicker.SelectedItem is not AssetComparisonModel model) { _modelStatus.Text = "No model matches the current model filter."; return; }
        if (model.Compatibility != AssetModelCompatibility.Ready || model.SkinPath is null) { _modelDependencyGraph = null; _modelView.ClearGeometry(); _modelStatus.Text = $"{model.Status}\nSource: {model.Provenance} · {model.LogicalPath}\nChoose a READY model to render it."; return; }
        _modelStatus.Text = $"Loading {model.FileName}…";
        var stopwatch = Stopwatch.StartNew();
        DesktopCrashLogger.Debug("MODEL", "comparison-preview-start", ("model", model.ModelPath), ("skin", model.SkinPath));
        try
        {
            var geometry = await Task.Run(() => M2PreviewGeometryService.Load(model.ModelPath, model.SkinPath)); _loadedModelGeometry = geometry; _modelDependencyGraph = _index is null ? null : await Task.Run(() => AssetDependencyGraphService.AnalyzeModel(_index, model)); _modelView.SetGeometry(geometry); UpdateModelStatus();
            DesktopCrashLogger.Debug("MODEL", "comparison-preview-success", ("model", model.ModelPath), ("vertices", geometry.Vertices.Count), ("triangles", geometry.TriangleIndices.Count / 3), ("texture_slots", geometry.TextureSlots.Count), ("duration_ms", stopwatch.Elapsed.TotalMilliseconds));
        }
        catch (Exception exception) { DesktopCrashLogger.Log("Comparison model preview failed", exception); _modelStatus.Text = exception.Message; }
    }

    private void UpdateModelStatus()
    {
        if (_modelPicker.SelectedItem is not AssetComparisonModel model) return;
        var texture = _selectedTexture is null ? "No texture card selected." : $"Selected texture candidate: {_selectedTexture.Provenance} · {_selectedTexture.FileName}";
        var slots = _loadedModelGeometry is null ? "Texture slots: load pending." : _loadedModelGeometry.TextureSlots.Count == 0 ? "Texture slots: none declared." : "Texture slots: " + string.Join(", ", _loadedModelGeometry.TextureSlots.Select(slot => $"{slot.Index}:{TextureTypeName(slot.Type)}{(string.IsNullOrWhiteSpace(slot.EmbeddedPath) ? string.Empty : $"={slot.EmbeddedPath}")}"));
        var dependencies = _modelDependencyGraph is null ? "Dependencies: inspection pending." : $"Dependencies: {_modelDependencyGraph.Resolved.Count:N0} resolved · {_modelDependencyGraph.ExternalBindings.Count:N0} appearance/DBC binding(s) · {_modelDependencyGraph.Blocking.Count:N0} BLOCKING";
        _modelStatus.Text = $"READY · {model.Provenance} · {model.FileName}\nContent path: {model.LogicalPath} · Skin: {Path.GetFileName(model.SkinPath)}\n{texture}\n{slots}\n{dependencies}\nGeometry and the selected PNG texture are live. Character layer/CharSections composition is still an approximation until the full appearance plan supplies every layer.";
    }

    private async Task ScanDuplicatesAsync()
    {
        var entries = _folderEntries; if (entries.Count < 2) { _status.Text = "This path has fewer than two PNGs to compare."; return; }
        _duplicateScanCancellation?.Cancel(); _duplicateScanCancellation?.Dispose(); _duplicateScanCancellation = new(); var token = _duplicateScanCancellation.Token;
        _scanDuplicates.IsEnabled = false; _status.Text = $"Hashing same-size candidates across {entries.Count:N0} PNGs…";
        var stopwatch = Stopwatch.StartNew();
        DesktopCrashLogger.Debug("ASSET", "exact-copy-scan-start", ("path", _folderTitle.Text), ("entries", entries.Count));
        try
        {
            var groups = await Task.Run(() => AssetComparisonService.FindExactDuplicates(entries, token), token);
            if (!ReferenceEquals(entries, _folderEntries)) return;
            _duplicateByPath = groups.SelectMany(group => group.Entries.Select(entry => (entry.FullPath, Group: group))).ToDictionary(pair => pair.FullPath, pair => pair.Group, StringComparer.OrdinalIgnoreCase);
            _collapseDuplicates.IsEnabled = groups.Count > 0; await FilterFilesAsync();
            _status.Text = groups.Count == 0 ? "No byte-identical PNGs exist in this content path." : $"Found {groups.Count:N0} exact-content group(s), {groups.Sum(group => group.Entries.Count - 1):N0} redundant copy/copies, and {FormatBytes(groups.Sum(group => group.RecoverableBytes))} potentially recoverable.";
            DesktopCrashLogger.Debug("ASSET", "exact-copy-scan-success", ("groups", groups.Count), ("redundant_copies", groups.Sum(group => group.Entries.Count - 1)), ("recoverable_bytes", groups.Sum(group => group.RecoverableBytes)), ("duration_ms", stopwatch.Elapsed.TotalMilliseconds));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { DesktopCrashLogger.Log("Exact asset duplicate scan failed", exception); _status.Text = exception.Message; }
        finally { _scanDuplicates.IsEnabled = true; }
    }

    private void SetActiveSlot(int slot) { _activeSlot = slot; for (var index = 0; index < 2; index++) { _slotButtons[index].Classes.Set("accent", index == slot); } }
    private void ApplyZoom() { for (var index = 0; index < 2; index++) if (_comparisonBitmaps[index] is { } bitmap) { _comparisonImages[index].Width = bitmap.PixelSize.Width * _zoom; _comparisonImages[index].Height = bitmap.PixelSize.Height * _zoom; } }
    private void SyncScroll(int source) { if (_syncingScroll) return; _syncingScroll = true; _comparisonScrolls[source == 0 ? 1 : 0].Offset = _comparisonScrolls[source].Offset; _syncingScroll = false; }
    private void RevealSlot(int slot) { if (_comparisonImages[slot].Tag is not AssetComparisonEntry entry) return; Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { "/select,", entry.FullPath } }); }
    private void FilterModels()
    {
        var query = _modelSearch.Text?.Trim() ?? string.Empty; IEnumerable<AssetComparisonModel> models = _allModels;
        if (_modelFilter.SelectedIndex == 0) models = models.Where(model => model.Compatibility == AssetModelCompatibility.Ready);
        if (query.Length > 0) models = models.Where(model => model.FileName.Contains(query, StringComparison.OrdinalIgnoreCase) || model.Provenance.Contains(query, StringComparison.OrdinalIgnoreCase) || model.LogicalPath.Contains(query, StringComparison.OrdinalIgnoreCase));
        _folderModels = models.ToArray(); _modelPicker.ItemsSource = _folderModels; _modelPicker.SelectedIndex = _folderModels.Count > 0 ? 0 : -1;
    }

    private void MoveModel(int offset)
    {
        if (_folderModels.Count == 0) return; _modelPicker.SelectedIndex = Math.Clamp(_modelPicker.SelectedIndex + offset, 0, _folderModels.Count - 1);
    }

    private async Task RecordCurrentAsync(AssetDecision decision)
    {
        if (_project is null || _projectPath is null) { _status.Text = "Index an asset library before recording decisions."; return; }
        var category = _assetCategory.Text ?? "Unsorted"; var notes = _assetNotes.Text ?? string.Empty;
        try
        {
            _status.Text = $"Recording {decision} decision and hashing its deployable source file(s)…";
            if (_previewMode.SelectedIndex == 1)
            {
                if (_modelPicker.SelectedItem is not AssetComparisonModel model) { _status.Text = "Select a model first."; return; }
                if (_index is null) return; var index = _index;
                _project = await Task.Run(() => DefinitiveAssetProjectService.RecordModel(_projectPath, _project, index, model, decision, category, notes));
                _status.Text = $"Recorded {decision}: {model.Provenance} · {model.FileName} plus its SKIN/animation dependencies.";
            }
            else
            {
                if (_selectedTexture is null) { _status.Text = "Select a texture card first."; return; }
                var selected = _selectedTexture; _project = await Task.Run(() => DefinitiveAssetProjectService.RecordTexture(_projectPath, _project, selected, decision, category, notes));
                _status.Text = $"Recorded {decision}: {selected.Provenance} · {selected.FileName}. The deployable BLP is tracked instead of its PNG preview.";
            }
            _assetNotes.Text = string.Empty; UpdateProjectStatus(); await FilterFilesAsync();
            if (_autoAdvance.IsChecked == true && _previewMode.SelectedIndex == 0 && _filteredEntries.Count > 0) await SelectComparisonAsync(_filteredEntries[0]);
            DesktopCrashLogger.Debug("ASSET-PROJECT", "decision-recorded", ("decision", decision), ("entries", _project.Entries.Count), ("project", _projectPath));
        }
        catch (Exception exception) { DesktopCrashLogger.Log("Definitive Set decision failed", exception); _status.Text = exception.Message; }
    }

    private async Task StageKeepersAsync()
    {
        if (_project is null || _projectPath is null) { _status.Text = "No Definitive Set project is loaded."; return; }
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a parent folder for the staged Definitive Set", AllowMultiple = false });
        var selected = folders.FirstOrDefault()?.TryGetLocalPath(); if (selected is null) return;
        var output = Path.Combine(selected, "Crucible-Definitive-Stage");
        try
        {
            _status.Text = "Verifying keeper hashes and staging the exact client paths…";
            var project = _project; var projectPath = _projectPath; var result = await Task.Run(() => DefinitiveAssetProjectService.StageKeepers(projectPath, project, output));
            _status.Text = $"Staged {result.Files:N0} keeper file(s), {FormatBytes(result.Bytes)}. Manifest: {result.ManifestPath}";
            Process.Start(new ProcessStartInfo("explorer.exe", result.RootPath) { UseShellExecute = true });
        }
        catch (Exception exception) { DesktopCrashLogger.Log("Definitive Set staging failed", exception); _status.Text = exception.Message; }
    }

    private void UpdateProjectStatus()
    {
        if (_project is null) { _projectStatus.Text = "No project"; return; }
        var groups = _project.Entries.GroupBy(entry => entry.GroupId).ToArray();
        _projectStatus.Text = $"{groups.Count(group => group.Any(entry => entry.Decision == AssetDecision.Keeper)):N0} keep · {groups.Count(group => group.Any(entry => entry.Decision == AssetDecision.Alternative)):N0} alt · {groups.Count(group => group.All(entry => entry.Decision == AssetDecision.Rejected)):N0} reject";
    }

    private AssetDecision? DecisionFor(string previewPath) => _project?.Entries.Where(entry => entry.PreviewPath.Equals(previewPath, StringComparison.OrdinalIgnoreCase)).OrderBy(entry => entry.Kind).Select(entry => (AssetDecision?)entry.Decision).FirstOrDefault();
    private async Task HandleDecisionKeyAsync(KeyEventArgs e)
    {
        if (e.Source is TextBox || e.KeyModifiers != KeyModifiers.None) return;
        var decision = e.Key switch { Key.K => AssetDecision.Keeper, Key.A => AssetDecision.Alternative, Key.R => AssetDecision.Review, Key.X => AssetDecision.Rejected, _ => (AssetDecision?)null };
        if (decision is null) return; e.Handled = true; await RecordCurrentAsync(decision.Value);
    }
    private void RevealProject()
    {
        if (_projectPath is null) return; var directory = Path.GetDirectoryName(_projectPath)!; Directory.CreateDirectory(directory);
        if (File.Exists(_projectPath)) Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { "/select,", _projectPath } });
        else Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
    }
    private async Task BrowseLibraryAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select Crucible asset library", AllowMultiple = false });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        _library.Text = path;
        await LoadIndexAsync();
    }
    private void DisposeImages() { _thumbnailCancellation?.Cancel(); _duplicateScanCancellation?.Cancel(); foreach (var bitmap in _thumbnailBitmaps) bitmap.Dispose(); foreach (var bitmap in _comparisonBitmaps) bitmap?.Dispose(); }
    private static Button AccentButton(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.##} GiB" : bytes >= 1024L * 1024 ? $"{bytes / (1024d * 1024):0.##} MiB" : bytes >= 1024 ? $"{bytes / 1024d:0.#} KiB" : $"{bytes:N0} B";
    private static string TextureTypeName(uint type) => type switch { 0 => "embedded", 1 => "body+clothes", 2 => "cape", 6 => "hair/beard", 8 => "fur", 11 => "creature-skin-1", 12 => "creature-skin-2", 13 => "creature-skin-3", _ => $"replaceable-{type}" };
    private static T WithColumn<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
}
