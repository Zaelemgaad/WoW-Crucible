using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;
using WoWCrucible.Desktop.Controls;

namespace WoWCrucible.Desktop;

internal sealed class AssetComparisonView : UserControl, IDisposable
{
    private const int PageSize = 96;
    private sealed record AppearanceSourceChoice(string Provenance, string FullPath, bool ExactEquivalent)
    {
        public override string ToString() => $"{Provenance} · {Path.GetFileName(FullPath)}{(ExactEquivalent ? " · exact-equivalent" : string.Empty)}";
    }
    private sealed record AppearancePlan(CharacterAppearanceIdentity? Identity, IReadOnlyList<CharacterBaseSkin> Skins, CharacterBaseSkin? SelectedSkin, IReadOnlyList<AppearanceSourceChoice> Sources, string? SelectedSourcePath, string Message);
    private readonly TextBox _library = new() { Text = Directory.Exists(@"G:\Crucible-Extras-Processed") ? @"G:\Crucible-Extras-Processed" : string.Empty };
    private readonly TextBox _directorySearch = new() { PlaceholderText = "Filter content paths…" };
    private readonly ListBox _directories = new();
    private readonly TextBox _fileSearch = new() { PlaceholderText = "Optional filename filter…" };
    private readonly ComboBox _sourceFilter = new();
    private readonly ComboBox _sort = new() { ItemsSource = new[] { "Source then name", "Name A–Z", "Size: largest first", "Size: smallest first" }, SelectedIndex = 0 };
    private readonly ComboBox _cardDensity = new() { ItemsSource = new[] { "1 column", "2 columns", "3 columns", "4 columns", "5 columns", "6 columns", "7 columns", "8 columns" }, SelectedIndex = 3 };
    private readonly Button _scanDuplicates = new() { Content = "Scan exact copies" };
    private readonly CheckBox _collapseDuplicates = new() { Content = "Collapse exact copies", IsEnabled = false };
    private readonly CheckBox _undecidedOnly = new() { Content = "Undecided only", IsChecked = true };
    private readonly CheckBox _autoAdvance = new() { Content = "Auto-advance", IsChecked = true };
    private readonly ComboBox _previewMode = new() { ItemsSource = new[] { "Image comparison", "Live model preview" }, SelectedIndex = 0 };
    private readonly ComboBox _modelPicker = new();
    private readonly TextBox _modelSearch = new() { PlaceholderText = "Filter discovered M2 models…" };
    private readonly ComboBox _modelFilter = new() { ItemsSource = new[] { "Ready models", "All models" }, SelectedIndex = 0 };
    private readonly ComboBox _geosetMode = new() { ItemsSource = new[] { "Base appearance", "All geosets (diagnostic)" }, SelectedIndex = 0 };
    private readonly ComboBox _skinPicker = new() { PlaceholderText = "No CharSections base skins loaded" };
    private readonly ComboBox _appearanceSourcePicker = new() { PlaceholderText = "Choose the texture provenance…" };
    private readonly TextBlock _appearanceStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#99A5B8"), FontSize = 11 };
    private readonly TextBox _assetCategory = new() { Text = "Unsorted", PlaceholderText = "Category" };
    private readonly TextBox _assetNotes = new() { PlaceholderText = "Optional decision notes" };
    private readonly TextBlock _projectStatus = new() { Foreground = Brush.Parse("#99A5B8"), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _modelStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#99A5B8") };
    private readonly M2PreviewView _modelView = new();
    private readonly UniformGrid _cards = new() { Columns = 4 };
    private readonly TextBlock _folderTitle = new() { FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _folderHelp = new() { Text = "Every PNG directly in this content directory is shown from every provenance source. Choose a card, then record it in the persistent Definitive Set below.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8793A7"), FontSize = 11 };
    private readonly TextBlock _pageStatus = new() { Foreground = Brush.Parse("#8F9AAE") };
    private readonly TextBlock _status = new() { Text = "Choose a content-first asset library.", Foreground = Brush.Parse("#99A5B8") };
    private readonly Image[] _comparisonImages = [new() { Stretch = Stretch.Uniform }, new() { Stretch = Stretch.Uniform }];
    private readonly ScrollViewer[] _comparisonScrolls = [new(), new()];
    private readonly TextBlock[] _comparisonTitles = [new() { TextWrapping = TextWrapping.Wrap }, new() { TextWrapping = TextWrapping.Wrap }];
    private readonly Button[] _slotButtons = [new() { Content = "LEFT SLOT" }, new() { Content = "RIGHT SLOT" }];
    private readonly Bitmap?[] _comparisonBitmaps = new Bitmap?[2];
    private readonly List<Bitmap> _thumbnailBitmaps = [];
    private readonly DesktopWorkspaceSession _session;
    private AssetComparisonIndex? _index; private IReadOnlyList<AssetComparisonEntry> _folderEntries = []; private IReadOnlyList<AssetComparisonEntry> _filteredEntries = [];
    private IReadOnlyDictionary<string, AssetComparisonDuplicateGroup> _duplicateByPath = new Dictionary<string, AssetComparisonDuplicateGroup>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<AssetComparisonModel> _allModels = []; private IReadOnlyList<AssetComparisonModel> _folderModels = []; private Control? _imageComparisonPane; private Control? _modelPreviewPane; private Control? _imageComparisonTools; private Control? _imageDirectoryTools; private Control? _imageCardScroller; private Control? _imagePager; private Control? _modelOnlyCatalogNotice; private AssetComparisonEntry? _selectedTexture; private M2PreviewGeometry? _loadedModelGeometry;
    private string _modelDiscoveryScope = string.Empty; private string? _projectPath; private DefinitiveAssetProject? _project; private AssetDependencyGraph? _modelDependencyGraph; private AssetComparisonDirectory? _selectedDirectory; private string? _resolvedModelTexturePath; private int _resolvedModelTextureCount;
    private Task? _modelDiscoveryTask;
    private CancellationTokenSource _workspaceCancellation = new(); private CancellationTokenSource? _directoryCancellation; private CancellationTokenSource? _thumbnailCancellation; private CancellationTokenSource? _imageSelectionCancellation; private CancellationTokenSource? _modelCancellation; private CancellationTokenSource? _duplicateScanCancellation; private int _page; private int _activeSlot; private double _zoom = 1; private bool _syncingScroll; private bool _settingSourceFilter; private bool _suppressPreviewModeChange; private bool _suppressModelSelection; private bool _suppressAppearanceSelection; private bool _modelOnlyDirectory; private bool _modelsDiscovered; private bool _directoryReady; private bool _initialIndexRequested; private bool _active; private bool _disposed; private long _activityVersion; private int _indexRequest; private int _directoryRequest; private int _thumbnailRequest; private int _imageSelectionRequest; private int _modelRequest; private int _duplicateScanRequest;

    public event EventHandler? BackRequested;

    public AssetComparisonView(DesktopWorkspaceSession session, string? libraryRoot = null)
    {
        _session = session;
        if (!string.IsNullOrWhiteSpace(libraryRoot)) _library.Text = libraryRoot;
        Focusable = true;
        _directories.ItemTemplate = new FuncDataTemplate<AssetComparisonDirectory>((item, _) => item is null ? new Grid() : new Grid
        {
            ColumnDefinitions = new("*,Auto"), Margin = new Thickness(3, 2), Children =
            {
                new TextBlock { Text = string.IsNullOrEmpty(item.LogicalPath) ? "(archive root)" : item.LogicalPath, TextTrimming = TextTrimming.CharacterEllipsis },
                WithColumn(new TextBlock { Text = DirectoryAssetSummary(item), TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brush.Parse("#7F8A9F"), FontSize = 10, Margin = new Thickness(8,0,0,0) }, 1)
            }
        });
        _directories.SelectionChanged += async (_, _) => await RunUiActionAsync("directory-selection", SelectDirectoryAsync); _directorySearch.TextChanged += (_, _) => FilterDirectories();
        _fileSearch.TextChanged += async (_, _) => await RunUiActionAsync("file-filter", FilterFilesAsync); _sourceFilter.SelectionChanged += async (_, _) => { if (!_settingSourceFilter) await RunUiActionAsync("source-filter", FilterFilesAsync); };
        _sort.SelectionChanged += async (_, _) => await RunUiActionAsync("sort-change", FilterFilesAsync); _collapseDuplicates.Click += async (_, _) => await RunUiActionAsync("duplicate-collapse", FilterFilesAsync); _undecidedOnly.Click += async (_, _) => await RunUiActionAsync("decision-filter", FilterFilesAsync); _scanDuplicates.Click += async (_, _) => await RunUiActionAsync("exact-copy-scan", ScanDuplicatesAsync);
        _cardDensity.SelectionChanged += (_, _) => _cards.Columns = Math.Max(1, _cardDensity.SelectedIndex + 1);
        _previewMode.SelectionChanged += async (_, _) => { if (!_suppressPreviewModeChange) await RunUiActionAsync("preview-mode-change", ChangePreviewModeAsync); };
        _modelPicker.SelectionChanged += async (_, _) => { if (!_suppressModelSelection) await RunUiActionAsync("model-selection", LoadSelectedModelAsync); };
        _geosetMode.SelectionChanged += async (_, _) => await RunUiActionAsync("geoset-mode-change", LoadSelectedModelAsync);
        _skinPicker.SelectionChanged += async (_, _) => { if (!_suppressAppearanceSelection) await RunUiActionAsync("appearance-skin-change", LoadSelectedModelAsync); };
        _appearanceSourcePicker.SelectionChanged += async (_, _) => { if (!_suppressAppearanceSelection) await RunUiActionAsync("appearance-source-change", LoadSelectedModelAsync); };
        _modelSearch.TextChanged += (_, _) => FilterModels(); _modelFilter.SelectionChanged += (_, _) => FilterModels();
        for (var index = 0; index < 2; index++) { var slot = index; _slotButtons[index].Click += (_, _) => SetActiveSlot(slot); }
        KeyDown += async (_, e) => await RunUiActionAsync("decision-shortcut", () => HandleDecisionKeyAsync(e)); SetActiveSlot(0); Content = BuildLayout();
    }

    public void Activate(string? libraryRoot = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var requestedRoot = string.IsNullOrWhiteSpace(libraryRoot) ? null : Path.GetFullPath(libraryRoot);
        var changedRoot = requestedRoot is not null && !requestedRoot.Equals(_library.Text, StringComparison.OrdinalIgnoreCase);
        if (changedRoot) _library.Text = requestedRoot;
        _workspaceCancellation.Cancel(); _workspaceCancellation.Dispose(); _workspaceCancellation = new();
        _directoryCancellation?.Dispose(); _directoryCancellation = CancellationTokenSource.CreateLinkedTokenSource(_workspaceCancellation.Token);
        if (!_modelsDiscovered) _modelDiscoveryTask = null;
        _active = true; _activityVersion++; _scanDuplicates.IsEnabled = true;
        if ((!_initialIndexRequested || changedRoot) && !string.IsNullOrWhiteSpace(_library.Text) && Directory.Exists(_library.Text))
        {
            _initialIndexRequested = true;
            _ = LoadIndexAsync();
        }
    }

    public void Suspend()
    {
        if (!_active) return;
        _active = false; _activityVersion++; _workspaceCancellation.Cancel(); _directoryCancellation?.Cancel(); _thumbnailCancellation?.Cancel(); _imageSelectionCancellation?.Cancel(); _modelCancellation?.Cancel(); _duplicateScanCancellation?.Cancel();
    }

    private Control BuildLayout()
    {
        var back = new Button { Content = "← Editor" }; ToolTip.SetTip(back, "Return to the main Crucible workspace"); back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var browse = new Button { Content = "Browse…" }; browse.Click += async (_, _) => await RunUiActionAsync("library-picker", BrowseLibraryAsync);
        var load = AccentButton("Index library"); load.Click += async (_, _) => await RunUiActionAsync("index-request", LoadIndexAsync);
        var top = new Grid { ColumnDefinitions = new("Auto,Auto,*,Auto,Auto"), Margin = new Thickness(14, 10), ColumnSpacing = 8 };
        top.Children.Add(back);
        var libraryLabel = new TextBlock { Text = "ASSET LIBRARY", FontWeight = FontWeight.Bold, FontSize = 10, Foreground = Brush.Parse("#C58A2B"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(libraryLabel, 1); top.Children.Add(libraryLabel);
        Grid.SetColumn(_library, 2); top.Children.Add(_library); Grid.SetColumn(browse, 3); top.Children.Add(browse); Grid.SetColumn(load, 4); top.Children.Add(load);

        var left = new Grid { RowDefinitions = new("Auto,*"), Margin = new Thickness(10) };
        left.Children.Add(_directorySearch); Grid.SetRow(_directories, 1); _directories.Margin = new Thickness(0, 8, 0, 0); left.Children.Add(_directories);

        var previous = new Button { Content = "← Previous" }; previous.Click += async (_, _) => await RunUiActionAsync("previous-page", async () => { if (_page > 0) { _page--; await RenderPageAsync(); } });
        var next = new Button { Content = "Next →" }; next.Click += async (_, _) => await RunUiActionAsync("next-page", async () => { if ((_page + 1) * PageSize < _filteredEntries.Count) { _page++; await RenderPageAsync(); } });
        var duplicateTools = new WrapPanel { Orientation = Orientation.Horizontal, Children = { _scanDuplicates, _collapseDuplicates, _undecidedOnly, _autoAdvance, new TextBlock { Text = "Keys: K keep · A alternative · R review · X reject", VerticalAlignment = VerticalAlignment.Center, Foreground = Brush.Parse("#8793A7"), FontSize = 10, Margin = new Thickness(4, 0) } } };
        var filters = new WrapPanel { Orientation = Orientation.Horizontal, Children = { _fileSearch, _sourceFilter, _sort, _cardDensity } };
        _imageDirectoryTools = new StackPanel { Spacing = 8, Children = { filters, duplicateTools } };
        var keeper = AccentButton("KEEP"); keeper.Click += async (_, _) => await RunUiActionAsync("record-keeper", () => RecordCurrentAsync(AssetDecision.Keeper));
        var alternative = new Button { Content = "Alternative" }; alternative.Click += async (_, _) => await RunUiActionAsync("record-alternative", () => RecordCurrentAsync(AssetDecision.Alternative));
        var reject = new Button { Content = "Reject" }; reject.Click += async (_, _) => await RunUiActionAsync("record-reject", () => RecordCurrentAsync(AssetDecision.Rejected));
        var review = new Button { Content = "Review later" }; review.Click += async (_, _) => await RunUiActionAsync("record-review", () => RecordCurrentAsync(AssetDecision.Review));
        var stage = new Button { Content = "Stage keepers…" }; stage.Click += async (_, _) => await RunUiActionAsync("stage-keepers", StageKeepersAsync);
        var revealProject = new Button { Content = "Reveal project" }; revealProject.Click += (_, _) => RevealProject();
        var decisionButtons = new WrapPanel { Orientation = Orientation.Horizontal, Children = { keeper, alternative, reject, review } };
        var decisionInputs = new Grid { ColumnDefinitions = new("*,2*"), ColumnSpacing = 8, Children = { _assetCategory, WithColumn(_assetNotes, 1) } };
        var decisionActions = new WrapPanel { Orientation = Orientation.Horizontal, Children = { stage, revealProject, _projectStatus } };
        var projectTools = new StackPanel { Spacing = 7, Margin = new Thickness(10, 7), Children = { decisionButtons, decisionInputs, decisionActions } };
        var middleHeader = new StackPanel { Spacing = 8, Margin = new Thickness(10, 10, 10, 4), Children = { _folderTitle, _folderHelp, _imageDirectoryTools } };
        var headerScroll = new ScrollViewer { Content = middleHeader, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var pager = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 5), Children = { previous, next, _pageStatus } };
        _imagePager = pager;
        var middle = new Grid { RowDefinitions = new("Auto,*,Auto") }; middle.Children.Add(headerScroll);
        var cardScroll = new ScrollViewer { Content = _cards, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Margin = new Thickness(8) };
        _imageCardScroller = cardScroll;
        _modelOnlyCatalogNotice = new Border
        {
            IsVisible = false,
            Margin = new Thickness(12),
            Padding = new Thickness(18),
            BorderBrush = Brush.Parse("#2B3445"),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "This is a model-asset path. Use the Live model preview, model search, and model selector on the right to inspect its M2/SKIN variants.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush.Parse("#99A5B8"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            }
        };
        Grid.SetRow(cardScroll, 1); middle.Children.Add(cardScroll); Grid.SetRow(_modelOnlyCatalogNotice, 1); middle.Children.Add(_modelOnlyCatalogNotice); Grid.SetRow(pager, 2); middle.Children.Add(pager);

        var compare = BuildComparisonPane();
        var body = new Grid { ColumnDefinitions = new("0.85*,Auto,1.35*,Auto,2*") };
        body.Children.Add(new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0,0,1,0), Child = left });
        var firstSplitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Brush.Parse("#2B3445") }; Grid.SetColumn(firstSplitter, 1); body.Children.Add(firstSplitter);
        Grid.SetColumn(middle, 2); body.Children.Add(middle);
        var secondSplitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Brush.Parse("#2B3445") }; Grid.SetColumn(secondSplitter, 3); body.Children.Add(secondSplitter);
        Grid.SetColumn(compare, 4); body.Children.Add(compare);
        var root = new Grid { RowDefinitions = new("Auto,*,Auto,Auto") }; root.Children.Add(top); Grid.SetRow(body, 1); root.Children.Add(body); Grid.SetRow(projectTools, 2); root.Children.Add(projectTools);
        var status = new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0,1,0,0), Padding = new Thickness(14,7), Child = _status }; Grid.SetRow(status, 3); root.Children.Add(status); return root;
    }

    private Control BuildComparisonPane()
    {
        var zoom = new Slider { Minimum = 0.1, Maximum = 4, Value = 1, HorizontalAlignment = HorizontalAlignment.Stretch }; zoom.ValueChanged += (_, e) => { _zoom = e.NewValue; ApplyZoom(); };
        var openLeft = new Button { Content = "Reveal left" }; openLeft.Click += (_, _) => RevealSlot(0); var openRight = new Button { Content = "Reveal right" }; openRight.Click += (_, _) => RevealSlot(1);
        _imageComparisonTools = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = "SYNCHRONIZED ZOOM & PAN", VerticalAlignment = VerticalAlignment.Center, FontSize = 10, FontWeight = FontWeight.Bold, Margin = new Thickness(4, 0) }, WithColumn(zoom, 1), WithColumn(openLeft, 2), WithColumn(openRight, 3) } };
        var toolbar = new StackPanel { Spacing = 6, Margin = new Thickness(12, 8), Children = { _previewMode, _imageComparisonTools } };
        var grid = new Grid { ColumnDefinitions = new("*,*"), RowDefinitions = new("Auto,*") };
        for (var index = 0; index < 2; index++)
        {
            var header = new Grid { ColumnDefinitions = new("Auto,*"), Margin = new Thickness(2), ColumnSpacing = 8, Children = { _slotButtons[index], WithColumn(_comparisonTitles[index], 1) } }; Grid.SetColumn(header, index); grid.Children.Add(header);
            _comparisonScrolls[index].Content = _comparisonImages[index]; _comparisonScrolls[index].HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto; _comparisonScrolls[index].VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
            var scrollIndex = index; _comparisonScrolls[index].ScrollChanged += (_, _) => SyncScroll(scrollIndex);
            var imageBorder = new Border { Background = Brush.Parse("#090B0F"), BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(1), Margin = new Thickness(3), ClipToBounds = true, Child = _comparisonScrolls[index] };
            Grid.SetRow(imageBorder, 1); Grid.SetColumn(imageBorder, index); grid.Children.Add(imageBorder);
        }
        _imageComparisonPane = grid;
        var previousModel = new Button { Content = "← Model" }; previousModel.Click += (_, _) => MoveModel(-1); var nextModel = new Button { Content = "Model →" }; nextModel.Click += (_, _) => MoveModel(1);
        _modelPicker.HorizontalAlignment = HorizontalAlignment.Stretch;
        var modelFilters = new Grid { ColumnDefinitions = new("2*,*,Auto,Auto"), ColumnSpacing = 8, Children = { _modelSearch, WithColumn(_modelFilter, 1), WithColumn(previousModel, 2), WithColumn(nextModel, 3) } };
        var appearanceHeader = new TextBlock { Text = "CHARSECTIONS BASE APPEARANCE", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#C58A2B") };
        var appearancePickers = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 8, Children = { _skinPicker, WithColumn(_appearanceSourcePicker, 1) } };
        var modelHeader = new StackPanel { Spacing = 7, Margin = new Thickness(9), Children = { new TextBlock { Text = "AUTOMATIC M2 MODEL BROWSER", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#C58A2B") }, modelFilters, _modelPicker, _geosetMode, appearanceHeader, appearancePickers, _appearanceStatus, _modelStatus } };
        var modelHeaderScroll = new ScrollViewer { Content = modelHeader, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var modelPane = new Grid { RowDefinitions = new("Auto,*"), IsVisible = false, Children = { modelHeaderScroll } };
        var modelBorder = new Border { Background = Brush.Parse("#090D14"), BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(1), Child = _modelView }; Grid.SetRow(modelBorder, 1); modelPane.Children.Add(modelBorder); _modelPreviewPane = modelPane;
        var previewHost = new Grid { Children = { grid, modelPane } };
        var root = new Grid { RowDefinitions = new("Auto,*"), Margin = new Thickness(6) }; root.Children.Add(toolbar); Grid.SetRow(previewHost, 1); root.Children.Add(previewHost); return root;
    }

    private async Task LoadIndexAsync()
    {
        if (!_active) return;
        var token = _workspaceCancellation.Token; var activity = _activityVersion; var request = ++_indexRequest;
        _status.Text = "Reading the asset catalog and grouping content directories…";
        var libraryRoot = _library.Text ?? string.Empty;
        var stopwatch = Stopwatch.StartNew();
        DesktopCrashLogger.Debug("ASSET", "index-start", ("library", libraryRoot));
        try
        {
            var index = await Task.Run(() => AssetComparisonService.BuildIndex(libraryRoot, token), token);
            if (!IsCurrent(token, activity) || request != _indexRequest) return;
            _index = index;
            _projectPath = DefinitiveAssetProjectService.DefaultPath(libraryRoot); _project = DefinitiveAssetProjectService.LoadOrCreate(_projectPath, libraryRoot); UpdateProjectStatus();
            FilterDirectories();
            var modelDirectories = _index.Directories.Count(directory => directory.M2Files > 0 || directory.SkinFiles > 0);
            _status.Text = $"Indexed {_index.TotalPngFiles:N0} PNG previews across {_index.Directories.Count:N0} content directories, including {modelDirectories:N0} with model assets, via {IndexSourceLabel(_index.Source)}.";
            DesktopCrashLogger.Log($"Asset comparison indexed {_index.TotalPngFiles:N0} PNG previews across {_index.Directories.Count:N0} content directories ({modelDirectories:N0} with model assets) from {libraryRoot}", null);
            DesktopCrashLogger.Debug("ASSET", "index-success", ("library", libraryRoot), ("pngs", _index.TotalPngFiles), ("directories", _index.Directories.Count), ("model_directories", modelDirectories), ("index_source", _index.Source), ("duration_ms", stopwatch.Elapsed.TotalMilliseconds));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!IsCurrent(token, activity) || request != _indexRequest) return;
            ReportFailure("index-failed", "Could not index that asset library", exception, ("library", libraryRoot));
        }
    }

    private void FilterDirectories()
    {
        if (_index is null) return; var query = _directorySearch.Text?.Trim() ?? string.Empty;
        _directories.ItemsSource = _index.Directories.Where(directory => query.Length == 0 || directory.LogicalPath.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private static string DirectoryAssetSummary(AssetComparisonDirectory directory)
    {
        if (directory.M2Files == 0 && directory.SkinFiles == 0)
            return $"{directory.PngFiles:N0} PNG · {directory.ProvenanceSources:N0} src";
        if (directory.PngFiles == 0)
            return $"{directory.M2Files:N0} M2 · {directory.SkinFiles:N0} SKIN";
        return $"{directory.PngFiles:N0} PNG · {directory.M2Files:N0} M2";
    }

    private static string IndexSourceLabel(AssetComparisonIndexSource source) => source switch
    {
        AssetComparisonIndexSource.Sidecar => "the compact cache",
        AssetComparisonIndexSource.Catalog => "the CSV catalog",
        _ => "a filesystem scan"
    };

    private static bool IsModelOnlyDirectory(AssetComparisonDirectory directory) =>
        directory.PngFiles == 0 && (directory.M2Files > 0 || directory.SkinFiles > 0);

    private void ApplyDirectoryPresentation(AssetComparisonDirectory directory, bool modelOnly)
    {
        _selectedDirectory = directory;
        _modelOnlyDirectory = modelOnly;
        _imageDirectoryTools?.IsVisible = !modelOnly;
        _imageCardScroller?.IsVisible = !modelOnly;
        _imagePager?.IsVisible = !modelOnly;
        _modelOnlyCatalogNotice?.IsVisible = modelOnly;
        _folderHelp.Text = modelOnly
            ? $"This logical path contains model assets ({directory.M2Files:N0} M2 · {directory.SkinFiles:N0} SKIN) rather than PNG previews. Crucible opens Live model preview automatically and resolves nearby models plus their dependencies."
            : "Every PNG directly in this content directory is shown from every provenance source. Choose a card, then record it in the persistent Definitive Set below.";
    }

    private async Task PresentModelOnlyDirectoryAsync()
    {
        if (_previewMode.SelectedIndex != 1)
        {
            _suppressPreviewModeChange = true;
            try { _previewMode.SelectedIndex = 1; }
            finally { _suppressPreviewModeChange = false; }
        }
        await ChangePreviewModeAsync();
    }

    private async Task SelectDirectoryAsync()
    {
        if (!_active) return;
        var activity = _activityVersion; var request = ++_directoryRequest;
        _directoryCancellation?.Cancel(); _directoryCancellation?.Dispose(); _directoryCancellation = CancellationTokenSource.CreateLinkedTokenSource(_workspaceCancellation.Token); var token = _directoryCancellation.Token;
        _thumbnailCancellation?.Cancel(); _imageSelectionCancellation?.Cancel(); _modelCancellation?.Cancel(); _duplicateScanCancellation?.Cancel();
        _thumbnailRequest++; _imageSelectionRequest++; _modelRequest++; _scanDuplicates.IsEnabled = true;
        if (_index is null || _directories.SelectedItem is not AssetComparisonDirectory directory) { _directoryReady = false; _selectedDirectory = null; return; }
        var index = _index;
        var modelOnly = IsModelOnlyDirectory(directory);
        _directoryReady = false; _modelsDiscovered = false; _folderEntries = []; _filteredEntries = []; _selectedTexture = null; _resolvedModelTexturePath = null; _resolvedModelTextureCount = 0;
        _modelDiscoveryTask = null;
        _allModels = []; _folderModels = []; _modelDiscoveryScope = directory.LogicalPath; _loadedModelGeometry = null; _modelDependencyGraph = null;
        _modelView.ClearGeometry(); _modelView.SetTexture(null); ApplyDirectoryPresentation(directory, modelOnly); FilterModels(requestModelLoad: false);
        foreach (var bitmap in _thumbnailBitmaps) bitmap.Dispose(); _thumbnailBitmaps.Clear(); _cards.Children.Clear(); _pageStatus.Text = "Loading directory…";
        _folderTitle.Text = string.IsNullOrEmpty(directory.LogicalPath) ? "(archive root)" : directory.LogicalPath;
        _status.Text = modelOnly ? "Reading model assets and resolving preview dependencies…" : "Reading direct PNG variants…";
        _modelStatus.Text = modelOnly
            ? "Waiting for this model-only content path to finish loading…"
            : "Model discovery is idle. Switch to Live model preview when you want Crucible to inspect nearby M2 files.";
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var entries = await Task.Run(() => AssetComparisonService.GetDirectoryPngs(index, directory.LogicalPath), token);
            if (!IsCurrent(token, activity) || request != _directoryRequest) return;
            // The catalog is an acceleration index, not the source of truth for
            // the files currently on disk. Reconcile presentation with the live
            // directory reads so a stale cache can never hide newly added PNGs.
            modelOnly = entries.Count == 0 && (directory.M2Files > 0 || directory.SkinFiles > 0);
            _folderEntries = entries; _directoryReady = true;
            ApplyDirectoryPresentation(directory, modelOnly);
            _modelStatus.Text = modelOnly
                ? "Resolving nearby M2 models for this model-only content path…"
                : "Model discovery is idle. Switch to Live model preview when you want Crucible to inspect nearby M2 files.";
            _duplicateByPath = new Dictionary<string, AssetComparisonDuplicateGroup>(StringComparer.OrdinalIgnoreCase); _collapseDuplicates.IsChecked = false; _collapseDuplicates.IsEnabled = false;
            var sources = new[] { "All patch sources" }.Concat(_folderEntries.Select(entry => entry.Provenance).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)).ToArray();
            _settingSourceFilter = true; _sourceFilter.ItemsSource = sources; _sourceFilter.SelectedIndex = 0; _settingSourceFilter = false; await FilterFilesAsync();
            if (!IsCurrent(token, activity) || request != _directoryRequest) return;
            if (modelOnly || _previewMode.SelectedIndex == 1)
            {
                await EnsureModelsDiscoveredAsync();
                if (!IsCurrent(token, activity) || request != _directoryRequest) return;
                if (modelOnly) await PresentModelOnlyDirectoryAsync();
                else await ChangePreviewModeAsync();
            }
            DesktopCrashLogger.Debug("ASSET", "directory-selected", ("path", directory.LogicalPath), ("pngs", _folderEntries.Count), ("sources", sources.Length - 1), ("models", _folderModels.Count), ("duration_ms", stopwatch.Elapsed.TotalMilliseconds));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!IsCurrent(token, activity) || request != _directoryRequest) return;
            ReportFailure("directory-read-failed", "Could not read that asset directory", exception, ("path", directory.LogicalPath));
        }
    }

    private async Task FilterFilesAsync()
    {
        if (!_active) return;
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
        if (!_active) return;
        var activity = _activityVersion; var request = ++_thumbnailRequest;
        _thumbnailCancellation?.Cancel(); _thumbnailCancellation?.Dispose(); _thumbnailCancellation = CancellationTokenSource.CreateLinkedTokenSource(_workspaceCancellation.Token); var token = _thumbnailCancellation.Token;
        foreach (var bitmap in _thumbnailBitmaps) bitmap.Dispose(); _thumbnailBitmaps.Clear(); _cards.Children.Clear();
        if (_modelOnlyDirectory)
        {
            var readyModels = _allModels.Count(model => model.Compatibility == AssetModelCompatibility.Ready);
            _pageStatus.Text = _allModels.Count == 0 ? "No M2 models discovered." : $"{_allModels.Count:N0} M2 discovered · {readyModels:N0} ready";
            _status.Text = _selectedDirectory is null
                ? $"Model-only content path · {_allModels.Count:N0} M2 discovered · {readyModels:N0} ready for live preview."
                : $"Model-only content path · {_selectedDirectory.M2Files:N0} M2 · {_selectedDirectory.SkinFiles:N0} SKIN · {readyModels:N0} ready for live preview.";
            return;
        }
        var page = _filteredEntries.Skip(_page * PageSize).Take(PageSize).ToArray(); var images = new List<(Image Image, AssetComparisonEntry Entry)>();
        foreach (var entry in page)
        {
            var image = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
            var duplicateText = _duplicateByPath.TryGetValue(entry.FullPath, out var duplicate) ? $" · {duplicate.Entries.Count:N0} exact copies" : string.Empty; var decision = DecisionFor(entry.FullPath); var decisionText = decision is null ? string.Empty : $" · {decision}";
            var card = new Button { Margin = new Thickness(3), Padding = new Thickness(7), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
                Content = new StackPanel { Spacing = 4, Children = { image, new TextBlock { Text = entry.FileName, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 }, new TextBlock { Text = $"{entry.Provenance} · {FormatBytes(entry.Bytes)}{duplicateText}{decisionText}", TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brush.Parse("#C58A2B"), FontSize = 10 } } } };
            card.Click += async (_, _) => await RunUiActionAsync("image-selection", () => SelectComparisonAsync(entry)); _cards.Children.Add(card); images.Add((image, entry));
        }
        _pageStatus.Text = _filteredEntries.Count == 0 ? "No PNGs match." : $"{_page * PageSize + 1:N0}–{_page * PageSize + page.Length:N0} of {_filteredEntries.Count:N0}";
        _status.Text = $"Showing all direct PNGs in this path · {_folderEntries.Count:N0} total · {_folderEntries.Select(entry => entry.Provenance).Distinct(StringComparer.OrdinalIgnoreCase).Count():N0} patch sources.";
        var failedThumbnails = 0; Exception? firstThumbnailFailure = null; string? firstFailedPath = null;
        foreach (var item in images)
        {
            try
            {
                var bitmap = await Task.Run(() => { token.ThrowIfCancellationRequested(); using var stream = File.OpenRead(item.Entry.FullPath); return Bitmap.DecodeToWidth(stream, 148, BitmapInterpolationMode.MediumQuality); }, token);
                if (IsCurrent(token, activity) && request == _thumbnailRequest) { item.Image.Source = bitmap; _thumbnailBitmaps.Add(bitmap); }
                else bitmap.Dispose();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception exception)
            {
                if (!IsCurrent(token, activity) || request != _thumbnailRequest) break;
                failedThumbnails++; firstThumbnailFailure ??= exception; firstFailedPath ??= item.Entry.FullPath;
                DesktopCrashLogger.Debug("ASSET", "thumbnail-decode-failed", ("path", item.Entry.FullPath), ("error", exception.Message));
            }
        }
        if (failedThumbnails > 0 && firstThumbnailFailure is not null && IsCurrent(token, activity) && request == _thumbnailRequest)
            ReportFailure("thumbnail-page-failed", $"Could not preview {failedThumbnails:N0} image(s) on this page", firstThumbnailFailure, ("first_path", firstFailedPath), ("failed", failedThumbnails));
    }

    private async Task SelectComparisonAsync(AssetComparisonEntry entry)
    {
        if (!_active) return;
        var activity = _activityVersion; var request = ++_imageSelectionRequest;
        _imageSelectionCancellation?.Cancel(); _imageSelectionCancellation?.Dispose(); _imageSelectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_workspaceCancellation.Token); var token = _imageSelectionCancellation.Token;
        _selectedTexture = entry; _resolvedModelTexturePath = null; _resolvedModelTextureCount = 0; _modelView.SetTexture(entry.FullPath); UpdateModelStatus();
        var slot = _activeSlot; try
        {
            var bitmap = await Task.Run(() => new Bitmap(entry.FullPath), token); if (!IsCurrent(token, activity) || request != _imageSelectionRequest) { bitmap.Dispose(); return; } _comparisonBitmaps[slot]?.Dispose(); _comparisonBitmaps[slot] = bitmap; _comparisonImages[slot].Source = bitmap;
            ApplyZoom();
            _comparisonTitles[slot].Text = $"{entry.Provenance}\n{entry.FileName}\n{bitmap.PixelSize.Width:N0}×{bitmap.PixelSize.Height:N0} · {entry.Bytes / 1024d:0.#} KiB";
            _comparisonImages[slot].Tag = entry; SetActiveSlot(slot == 0 ? 1 : 0);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!IsCurrent(token, activity) || request != _imageSelectionRequest) return;
            ReportFailure("comparison-image-load-failed", "Could not load that comparison image", exception, ("path", entry.FullPath), ("slot", slot));
        }
    }

    private async Task ChangePreviewModeAsync()
    {
        var modelMode = _previewMode.SelectedIndex == 1;
        if (_imageComparisonPane is not null) _imageComparisonPane.IsVisible = !modelMode;
        if (_modelPreviewPane is not null) _modelPreviewPane.IsVisible = modelMode;
        if (_imageComparisonTools is not null) _imageComparisonTools.IsVisible = !modelMode;
        if (modelMode)
        {
            await EnsureModelsDiscoveredAsync();
            await LoadSelectedModelAsync();
        }
        else { _modelRequest++; _modelCancellation?.Cancel(); }
    }

    private Task EnsureModelsDiscoveredAsync()
    {
        if (!_active || !_directoryReady || _modelsDiscovered || _index is null || _selectedDirectory is null) return Task.CompletedTask;
        return _modelDiscoveryTask ??= DiscoverModelsAsync(_index, _selectedDirectory);
    }

    private async Task DiscoverModelsAsync(AssetComparisonIndex index, AssetComparisonDirectory directory)
    {
        var activity = _activityVersion; var request = _directoryRequest; var token = _directoryCancellation?.Token ?? _workspaceCancellation.Token;
        _modelStatus.Text = "Discovering nearby M2 models…";
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var discovered = await Task.Run(() => AssetComparisonService.GetRelevantModels(index, directory.LogicalPath, token), token);
            if (!IsCurrent(token, activity) || request != _directoryRequest || !ReferenceEquals(directory, _selectedDirectory)) return;
            _allModels = discovered.Models; _modelDiscoveryScope = discovered.DiscoveryScope; _modelsDiscovered = true;
            FilterModels(requestModelLoad: false, preferReady: _modelOnlyDirectory);
            _modelStatus.Text = _allModels.Count == 0
                ? "No M2 files were found in this path or its bounded parent scopes."
                : $"Discovered {_allModels.Count:N0} M2 file(s) under '{_modelDiscoveryScope}', including {_allModels.Count(model => model.Compatibility == AssetModelCompatibility.Ready):N0} ready for live preview.";
            DesktopCrashLogger.Debug("MODEL", "comparison-discovery-success", ("path", directory.LogicalPath), ("scope", _modelDiscoveryScope), ("models", _allModels.Count), ("duration_ms", stopwatch.Elapsed.TotalMilliseconds));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!IsCurrent(token, activity) || request != _directoryRequest) return;
            _modelDiscoveryTask = null;
            ReportFailure("model-discovery-failed", "Could not discover nearby models", exception, ("path", directory.LogicalPath));
        }
    }

    private async Task LoadSelectedModelAsync()
    {
        if (!_active || _previewMode.SelectedIndex != 1) return;
        var activity = _activityVersion; var directoryRequest = _directoryRequest; var request = ++_modelRequest;
        _modelCancellation?.Cancel(); _modelCancellation?.Dispose();
        var directoryToken = _directoryCancellation?.Token ?? _workspaceCancellation.Token;
        _modelCancellation = CancellationTokenSource.CreateLinkedTokenSource(_workspaceCancellation.Token, directoryToken); var token = _modelCancellation.Token;
        if (_modelPicker.SelectedItem is not AssetComparisonModel model)
        {
            _modelStatus.Text = _allModels.Count == 0
                ? "No M2 files were found in this path or its parent content paths."
                : $"{_allModels.Count:N0} M2 file(s) were discovered, but none match the current model filter. Choose All models to inspect compatibility details.";
            return;
        }
        if (model.Compatibility != AssetModelCompatibility.Ready || model.SkinPath is null) { _modelDependencyGraph = null; _modelView.ClearGeometry(); _modelStatus.Text = $"{model.Status}\nSource: {model.Provenance} · {model.LogicalPath}\nChoose a READY model to render it."; return; }
        _modelStatus.Text = $"Loading {model.FileName}…";
        var stopwatch = Stopwatch.StartNew();
        DesktopCrashLogger.Debug("MODEL", "comparison-preview-start", ("model", model.ModelPath), ("skin", model.SkinPath));
        try
        {
            var visibilityMode = _geosetMode.SelectedIndex == 1 ? M2PreviewVisibilityMode.AllGeosets : M2PreviewVisibilityMode.BaseAppearance;
            var geometry = await Task.Run(() => M2PreviewGeometryService.Load(model.ModelPath, model.SkinPath, visibilityMode), token); if (!IsCurrent(token, activity) || request != _modelRequest || directoryRequest != _directoryRequest) return;
            var graph = _index is null ? null : await Task.Run(() => AssetDependencyGraphService.AnalyzeModel(_index, model), token); if (!IsCurrent(token, activity) || request != _modelRequest || directoryRequest != _directoryRequest) return;
            var requestedSkin = _skinPicker.SelectedItem as CharacterBaseSkin; var requestedSource = (_appearanceSourcePicker.SelectedItem as AppearanceSourceChoice)?.FullPath;
            var appearance = await Task.Run(() => BuildAppearancePlan(model, requestedSkin, requestedSource, token), token); if (!IsCurrent(token, activity) || request != _modelRequest || directoryRequest != _directoryRequest) return;
            ApplyAppearancePlan(appearance);
            var embeddedTextures = new Dictionary<int, RgbaTexture>(); string? embeddedTexturePath = null;
            if (_selectedTexture is null)
            {
                var usedTextureDefinitions = geometry.Batches.Where(batch => batch.TextureDefinitionIndex is not null).Select(batch => batch.TextureDefinitionIndex!.Value).Distinct().ToHashSet();
                foreach (var slot in geometry.TextureSlots.Where(slot => usedTextureDefinitions.Contains(slot.Index) && slot.Type == 0 && !string.IsNullOrWhiteSpace(slot.EmbeddedPath)))
                {
                    var clientPath = PatchInputMapper.NormalizeArchivePath(slot.EmbeddedPath!);
                    var resolved = graph?.Resolved.FirstOrDefault(dependency => dependency.Kind == "embedded-texture" && dependency.ClientPath.Equals(clientPath, StringComparison.OrdinalIgnoreCase) && dependency.SourcePath is not null && Path.GetExtension(dependency.SourcePath).Equals(".blp", StringComparison.OrdinalIgnoreCase))?.SourcePath;
                    if (resolved is null) continue;
                    try
                    {
                        embeddedTextures[slot.Index] = await Task.Run(() => BlpTextureService.Decode(resolved), token);
                        embeddedTexturePath ??= resolved;
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        DesktopCrashLogger.Log($"Embedded model texture decode failed: {resolved}", exception);
                    }
                    if (!IsCurrent(token, activity) || request != _modelRequest || directoryRequest != _directoryRequest) return;
                }
                if (appearance.SelectedSourcePath is { } appearancePath)
                {
                    var bodySlots = geometry.TextureSlots.Where(slot => usedTextureDefinitions.Contains(slot.Index) && slot.Type == 1).Select(slot => slot.Index).ToArray();
                    if (bodySlots.Length > 0)
                    {
                        try
                        {
                            var bodyTexture = await Task.Run(() => BlpTextureService.Decode(appearancePath), token);
                            foreach (var slot in bodySlots) embeddedTextures[slot] = bodyTexture;
                            embeddedTexturePath ??= appearancePath;
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException) { DesktopCrashLogger.Log($"Character base-skin decode failed: {appearancePath}", exception); }
                        if (!IsCurrent(token, activity) || request != _modelRequest || directoryRequest != _directoryRequest) return;
                    }
                }
            }
            _loadedModelGeometry = geometry; _modelDependencyGraph = graph; _resolvedModelTexturePath = embeddedTexturePath; _resolvedModelTextureCount = embeddedTextures.Count; _modelView.SetGeometry(geometry);
            if (_selectedTexture is null) _modelView.SetDecodedTextures(embeddedTextures);
            UpdateModelStatus();
            DesktopCrashLogger.Debug("MODEL", "comparison-preview-success", ("model", model.ModelPath), ("vertices", geometry.Vertices.Count), ("triangles", geometry.TriangleIndices.Count / 3), ("texture_slots", geometry.TextureSlots.Count), ("duration_ms", stopwatch.Elapsed.TotalMilliseconds));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!IsCurrent(token, activity) || request != _modelRequest) return;
            DesktopCrashLogger.Log($"Comparison model preview failed: {model.ModelPath}", exception);
            _modelStatus.Text = $"Could not load {model.FileName}: {exception.Message}";
        }
    }

    private AppearancePlan BuildAppearancePlan(AssetComparisonModel model, CharacterBaseSkin? requestedSkin, string? requestedSource, CancellationToken token)
    {
        var identity = CharacterAppearanceService.Infer(model.LogicalPath, model.FileName);
        if (identity is null) return new(null, [], null, [], null, "This model path does not identify a playable race and sex; CharSections appearance binding does not apply.");
        var dbcPath = Path.Combine(_session.Settings.CoreDbcPath, "CharSections.dbc");
        if (!File.Exists(dbcPath)) return new(identity, [], null, [], null, $"Detected {identity.RaceName} {identity.SexName}, but CharSections.dbc is unavailable. Point Server & SQL at a server DBC folder to enable real base skins.");
        var skins = CharacterAppearanceService.LoadBaseSkins(dbcPath, identity); token.ThrowIfCancellationRequested();
        if (skins.Count == 0) return new(identity, [], null, [], null, $"CharSections.dbc has no base-skin records for {identity.RaceName} {identity.SexName}.");
        var selectedSkin = requestedSkin is not null && requestedSkin.RaceId == identity.RaceId && requestedSkin.SexId == identity.SexId
            ? skins.FirstOrDefault(skin => skin.Id == requestedSkin.Id) ?? skins[0] : skins[0];
        if (_index is null) return new(identity, skins, selectedSkin, [], null, "The asset index is unavailable, so the selected CharSections texture cannot be resolved.");
        var resolution = AssetDependencyGraphService.ResolveClientAsset(_index, model.Provenance, selectedSkin.TexturePath);
        var paths = (resolution.SourcePath is not null ? new[] { resolution.SourcePath } : resolution.Candidates).ToArray();
        var exactEquivalent = paths.Length > 1 && paths.Skip(1).All(path => AssetComparisonService.FilesAreIdentical(paths[0], path, token));
        var sources = paths.Select(path => new AppearanceSourceChoice(SourceName(_index, path), path, exactEquivalent)).OrderBy(source => source.Provenance, StringComparer.OrdinalIgnoreCase).ToArray();
        var selectedPath = sources.FirstOrDefault(source => source.FullPath.Equals(requestedSource, StringComparison.OrdinalIgnoreCase))?.FullPath;
        selectedPath ??= sources.Length == 1 || exactEquivalent ? sources.FirstOrDefault()?.FullPath : null;
        var message = sources.Length == 0
            ? $"{identity.RaceName} {identity.SexName} · skin {selectedSkin.ColorIndex}: required texture is missing from the processed library."
            : selectedPath is null
                ? $"{identity.RaceName} {identity.SexName} · skin {selectedSkin.ColorIndex}: {sources.Length:N0} non-identical provenance variants exist. Choose the source that belongs to this model/client; Crucible will not guess."
                : $"{identity.RaceName} {identity.SexName} · skin {selectedSkin.ColorIndex}: applying {SourceName(_index, selectedPath)} to the model's body texture slot{(exactEquivalent ? " (all listed candidates are byte-for-byte identical)" : string.Empty)}.";
        return new(identity, skins, selectedSkin, sources, selectedPath, message);
    }

    private void ApplyAppearancePlan(AppearancePlan plan)
    {
        _suppressAppearanceSelection = true;
        try
        {
            _skinPicker.ItemsSource = plan.Skins; _skinPicker.SelectedItem = plan.SelectedSkin; _skinPicker.IsEnabled = plan.Skins.Count > 0;
            _appearanceSourcePicker.ItemsSource = plan.Sources; _appearanceSourcePicker.SelectedItem = plan.SelectedSourcePath is null ? null : plan.Sources.FirstOrDefault(source => source.FullPath.Equals(plan.SelectedSourcePath, StringComparison.OrdinalIgnoreCase)); _appearanceSourcePicker.IsEnabled = plan.Sources.Count > 0;
            _appearanceStatus.Text = plan.Message;
        }
        finally { _suppressAppearanceSelection = false; }
    }

    private static string SourceName(AssetComparisonIndex index, string path)
        => index.LooseContentRoot is { } loose && Path.GetRelativePath(loose, path) is var relative && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar)
            ? "Loose" : Directory.GetParent(path)?.Name ?? "Unknown";

    private void UpdateModelStatus()
    {
        if (_modelPicker.SelectedItem is not AssetComparisonModel model) return;
        var texture = _selectedTexture is null
            ? _resolvedModelTexturePath is not null ? $"Resolved {_resolvedModelTextureCount:N0} embedded material BLP(s); first: {Path.GetFileName(_resolvedModelTexturePath)}." : (_modelOnlyDirectory ? "Texture overlay: no embedded BLP resolved; geometry remains available for inspection." : "No texture card selected.")
            : $"Selected texture candidate: {_selectedTexture.Provenance} · {_selectedTexture.FileName}";
        var slots = _loadedModelGeometry is null ? "Texture slots: load pending." : _loadedModelGeometry.TextureSlots.Count == 0 ? "Texture slots: none declared." : "Texture slots: " + string.Join(", ", _loadedModelGeometry.TextureSlots.Select(slot => $"{slot.Index}:{TextureTypeName(slot.Type)}{(string.IsNullOrWhiteSpace(slot.EmbeddedPath) ? string.Empty : $"={slot.EmbeddedPath}")}"));
        var dependencies = _modelDependencyGraph is null ? "Dependencies: inspection pending." : $"Dependencies: {_modelDependencyGraph.Resolved.Count:N0} resolved · {_modelDependencyGraph.ExternalBindings.Count:N0} appearance/DBC binding(s) · {_modelDependencyGraph.Blocking.Count:N0} BLOCKING";
        var geosets = _loadedModelGeometry is null || _loadedModelGeometry.Submeshes.Count == 0
            ? "Geosets: no SKIN submesh table; showing the complete mesh."
            : $"Geosets: {_loadedModelGeometry.Submeshes.Count(section => section.Visible):N0} visible of {_loadedModelGeometry.Submeshes.Count:N0} · {_loadedModelGeometry.VisibilityMode} · {_loadedModelGeometry.TriangleIndices.Count / 3:N0} of {_loadedModelGeometry.TotalTriangleIndices / 3:N0} triangles";
        var previewNote = _resolvedModelTexturePath is not null
            ? "Geometry and first-pass per-submesh texture-unit assignment are live. Multi-pass shader blending and Character layer/CharSections composition remain fidelity stages."
            : _modelOnlyDirectory
            ? "Geometry is live. Replacement texture resolution and Character layer/CharSections composition remain approximate until the full appearance plan supplies every layer."
            : "Geometry and the selected PNG texture are live. Character layer/CharSections composition is still an approximation until the full appearance plan supplies every layer.";
        _modelStatus.Text = $"READY · {model.Provenance} · {model.FileName}\nContent path: {model.LogicalPath} · Skin: {Path.GetFileName(model.SkinPath)}\n{geosets}\n{texture}\n{slots}\n{dependencies}\n{previewNote}";
    }

    private async Task ScanDuplicatesAsync()
    {
        if (!_active) return;
        var entries = _folderEntries; if (entries.Count < 2) { _status.Text = "This path has fewer than two PNGs to compare."; return; }
        var activity = _activityVersion; var request = ++_duplicateScanRequest;
        _duplicateScanCancellation?.Cancel(); _duplicateScanCancellation?.Dispose(); _duplicateScanCancellation = CancellationTokenSource.CreateLinkedTokenSource(_workspaceCancellation.Token); var token = _duplicateScanCancellation.Token;
        _scanDuplicates.IsEnabled = false; _status.Text = $"Hashing same-size candidates across {entries.Count:N0} PNGs…";
        var stopwatch = Stopwatch.StartNew();
        DesktopCrashLogger.Debug("ASSET", "exact-copy-scan-start", ("path", _folderTitle.Text), ("entries", entries.Count));
        try
        {
            var groups = await Task.Run(() => AssetComparisonService.FindExactDuplicates(entries, token), token);
            if (!IsCurrent(token, activity) || request != _duplicateScanRequest || !ReferenceEquals(entries, _folderEntries)) return;
            _duplicateByPath = groups.SelectMany(group => group.Entries.Select(entry => (entry.FullPath, Group: group))).ToDictionary(pair => pair.FullPath, pair => pair.Group, StringComparer.OrdinalIgnoreCase);
            _collapseDuplicates.IsEnabled = groups.Count > 0; await FilterFilesAsync();
            _status.Text = groups.Count == 0 ? "No byte-identical PNGs exist in this content path." : $"Found {groups.Count:N0} exact-content group(s), {groups.Sum(group => group.Entries.Count - 1):N0} redundant copy/copies, and {FormatBytes(groups.Sum(group => group.RecoverableBytes))} potentially recoverable.";
            DesktopCrashLogger.Debug("ASSET", "exact-copy-scan-success", ("groups", groups.Count), ("redundant_copies", groups.Sum(group => group.Entries.Count - 1)), ("recoverable_bytes", groups.Sum(group => group.RecoverableBytes)), ("duration_ms", stopwatch.Elapsed.TotalMilliseconds));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (IsCurrent(token, activity) && request == _duplicateScanRequest)
                ReportFailure("exact-copy-scan-failed", "Could not scan this directory for exact copies", exception, ("path", _folderTitle.Text));
        }
        finally { if (IsCurrent(token, activity) && request == _duplicateScanRequest) _scanDuplicates.IsEnabled = true; }
    }

    private void SetActiveSlot(int slot) { _activeSlot = slot; for (var index = 0; index < 2; index++) { _slotButtons[index].Classes.Set("accent", index == slot); } }
    private void ApplyZoom() { for (var index = 0; index < 2; index++) if (_comparisonBitmaps[index] is { } bitmap) { _comparisonImages[index].Width = bitmap.PixelSize.Width * _zoom; _comparisonImages[index].Height = bitmap.PixelSize.Height * _zoom; } }
    private void SyncScroll(int source) { if (_syncingScroll) return; _syncingScroll = true; _comparisonScrolls[source == 0 ? 1 : 0].Offset = _comparisonScrolls[source].Offset; _syncingScroll = false; }
    private void RevealSlot(int slot) { if (_comparisonImages[slot].Tag is not AssetComparisonEntry entry) return; Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { "/select,", entry.FullPath } }); }
    private void FilterModels(bool requestModelLoad = true, bool preferReady = false)
    {
        var query = _modelSearch.Text?.Trim() ?? string.Empty; IEnumerable<AssetComparisonModel> models = _allModels;
        if (_modelFilter.SelectedIndex == 0) models = models.Where(model => model.Compatibility == AssetModelCompatibility.Ready);
        if (query.Length > 0) models = models.Where(model => model.FileName.Contains(query, StringComparison.OrdinalIgnoreCase) || model.Provenance.Contains(query, StringComparison.OrdinalIgnoreCase) || model.LogicalPath.Contains(query, StringComparison.OrdinalIgnoreCase));
        _folderModels = models.ToArray();
        var selectedIndex = _folderModels.Count == 0 ? -1 : 0;
        if (preferReady)
        {
            var firstReady = Array.FindIndex(_folderModels.ToArray(), model => model.Compatibility == AssetModelCompatibility.Ready);
            if (firstReady >= 0) selectedIndex = firstReady;
        }
        _suppressModelSelection = true;
        try { _modelPicker.ItemsSource = _folderModels; _modelPicker.SelectedIndex = selectedIndex; }
        finally { _suppressModelSelection = false; }
        if (requestModelLoad && _active && _previewMode.SelectedIndex == 1)
            _ = RunUiActionAsync("model-filter-selection", LoadSelectedModelAsync);
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
        var folders = await GetStorageProvider().OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a parent folder for the staged Definitive Set", AllowMultiple = false });
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
        var folders = await GetStorageProvider().OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select Crucible asset library", AllowMultiple = false });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        _library.Text = path;
        await LoadIndexAsync();
    }
    public void Dispose()
    {
        if (_disposed) return;
        Suspend(); _disposed = true;
        _workspaceCancellation.Dispose();
        _directoryCancellation?.Cancel(); _directoryCancellation?.Dispose();
        _thumbnailCancellation?.Cancel(); _thumbnailCancellation?.Dispose();
        _imageSelectionCancellation?.Cancel(); _imageSelectionCancellation?.Dispose();
        _modelCancellation?.Cancel(); _modelCancellation?.Dispose();
        _duplicateScanCancellation?.Cancel(); _duplicateScanCancellation?.Dispose();
        foreach (var bitmap in _thumbnailBitmaps) bitmap.Dispose();
        foreach (var bitmap in _comparisonBitmaps) bitmap?.Dispose();
        _thumbnailBitmaps.Clear();
        _modelView.Dispose();
    }
    private async Task RunUiActionAsync(string action, Func<Task> work)
    {
        try { await work(); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ReportFailure($"ui-{action}-failed", "Asset Compare could not complete that action", exception); }
    }
    private void ReportFailure(string action, string message, Exception exception, params (string Key, object? Value)[] fields)
    {
        DesktopCrashLogger.Debug("ASSET", action, fields);
        DesktopCrashLogger.Log($"Asset Compare {action}", exception);
        if (_active) _status.Text = $"ERROR · {message}: {exception.Message}";
    }
    private bool IsCurrent(CancellationToken token, long activity) => _active && !token.IsCancellationRequested && activity == _activityVersion;
    private IStorageProvider GetStorageProvider() => TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("The asset workspace is not attached to a desktop window.");
    private static Button AccentButton(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.##} GiB" : bytes >= 1024L * 1024 ? $"{bytes / (1024d * 1024):0.##} MiB" : bytes >= 1024 ? $"{bytes / 1024d:0.#} KiB" : $"{bytes:N0} B";
    private static string TextureTypeName(uint type) => type switch { 0 => "embedded", 1 => "body+clothes", 2 => "cape", 6 => "hair/beard", 8 => "fur", 11 => "creature-skin-1", 12 => "creature-skin-2", 13 => "creature-skin-3", _ => $"replaceable-{type}" };
    private static T WithColumn<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
}
