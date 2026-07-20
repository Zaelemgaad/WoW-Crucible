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
    private sealed record GeosetChoice(int Variant, string Label)
    {
        public override string ToString() => Label;
    }
    private sealed record AppearancePlan(CharacterAppearanceIdentity? Identity, IReadOnlyList<CharacterBaseSkin> Skins, CharacterBaseSkin? SelectedSkin,
        IReadOnlyList<CharacterSection> Faces, CharacterSection? SelectedFace, IReadOnlyList<CharacterSection> FacialHair, CharacterSection? SelectedFacialHair,
        IReadOnlyList<CharacterSection> Hair, CharacterSection? SelectedHair, CharacterSection? Underwear,
        IReadOnlyList<AppearanceSourceChoice> Sources, AppearanceSourceChoice? SelectedSource, CharacterAppearanceGeosetPlan? Geosets, string Message, CharacterAppearancePreviewPlan? CorePlan = null);
    private sealed record CreatureAppearanceChoice(CreatureDisplayPreview Display, CreatureModelSource Source)
    {
        public override string ToString() => $"Display {Display.DisplayId:N0} · {Source.CreatureTextures.Count:N0}/3 texture(s) · scale {Display.DisplayScale * Display.ModelScale:0.###}";
    }
    private sealed record CreatureAppearancePlan(bool Applicable, IReadOnlyList<CreatureAppearanceChoice> Choices, CreatureAppearanceChoice? Selected, string Message);
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
    private readonly ComboBox _modelSkinPicker = new() { PlaceholderText = "No compatible SKIN views" };
    private readonly TextBox _modelSearch = new() { PlaceholderText = "Filter discovered M2 models…" };
    private readonly ComboBox _modelFilter = new() { ItemsSource = new[] { "Ready models", "All models" }, SelectedIndex = 0 };
    private readonly ComboBox _geosetMode = new() { ItemsSource = new[] { "Automatic: character DBC / complete generic model", "Naked character (no hair or facial hair)", "Manual: exactly one variant per group", "Everything stacked (diagnostic only)" }, SelectedIndex = 0 };
    private readonly ItemsControl _geosetGroups = new();
    private readonly TextBlock _geosetInspectorStatus = new() { Text = "Load a compatible character M2 to inspect its named geoset groups.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#99A5B8"), FontSize = 11 };
    private readonly CheckBox _showAttachmentPoints = new() { Content = "Show native attachment points" };
    private readonly ComboBox _attachmentPicker = new() { PlaceholderText = "No attachment points loaded", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _attachmentStatus = new() { Text = "Load a compatible M2 to inspect equipment and effect attachment points.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#99A5B8"), FontSize = 11 };
    private readonly ComboBox _skinPicker = new() { PlaceholderText = "No CharSections base skins loaded" };
    private readonly ComboBox _facePicker = new() { PlaceholderText = "No face layers" };
    private readonly ComboBox _facialHairPicker = new() { PlaceholderText = "No facial-hair layers" };
    private readonly ComboBox _hairPicker = new() { PlaceholderText = "No hair layers" };
    private readonly ComboBox _appearanceSourcePicker = new() { PlaceholderText = "Choose the texture provenance…" };
    private readonly TextBlock _appearanceStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#99A5B8"), FontSize = 11 };
    private readonly ComboBox _creatureAppearancePicker = new() { PlaceholderText = "Choose a CreatureDisplayInfo skin…", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _creatureAppearanceStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#99A5B8"), FontSize = 11 };
    private readonly StackPanel _creatureAppearancePanel = new() { Spacing = 6, IsVisible = false };
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
    private readonly Dictionary<int, int> _manualGeosetVariants = [];
    private IReadOnlyList<AssetComparisonModel> _allModels = []; private IReadOnlyList<AssetComparisonModel> _folderModels = []; private Control? _imageComparisonPane; private Control? _modelPreviewPane; private Control? _imageComparisonTools; private Control? _imageDirectoryTools; private Control? _imageCardScroller; private Control? _imagePager; private Control? _modelOnlyCatalogNotice; private AssetComparisonEntry? _selectedTexture; private M2PreviewGeometry? _loadedModelGeometry;
    private string _modelDiscoveryScope = string.Empty; private string? _projectPath; private DefinitiveAssetProject? _project; private AssetDependencyGraph? _modelDependencyGraph; private AssetComparisonDirectory? _selectedDirectory; private string? _resolvedModelTexturePath; private string? _geosetInspectorModel; private int _resolvedModelTextureCount; private bool _appearanceComposed; private bool _creatureAppearanceBound;
    private IReadOnlyDictionary<int, RgbaTexture> _loadedModelTextures = new Dictionary<int, RgbaTexture>();
    private Task? _modelDiscoveryTask;
    private CancellationTokenSource _workspaceCancellation = new(); private CancellationTokenSource? _directoryCancellation; private CancellationTokenSource? _thumbnailCancellation; private CancellationTokenSource? _imageSelectionCancellation; private CancellationTokenSource? _modelCancellation; private CancellationTokenSource? _duplicateScanCancellation; private int _page; private int _activeSlot; private double _zoom = 1; private bool _syncingScroll; private bool _settingSourceFilter; private bool _suppressPreviewModeChange; private bool _suppressModelSelection; private bool _suppressModelSkinSelection; private bool _suppressAppearanceSelection; private bool _suppressCreatureAppearanceSelection; private bool _modelOnlyDirectory; private bool _modelsDiscovered; private bool _directoryReady; private bool _initialIndexRequested; private bool _active; private bool _disposed; private long _activityVersion; private int _indexRequest; private int _directoryRequest; private int _thumbnailRequest; private int _imageSelectionRequest; private int _modelRequest; private int _duplicateScanRequest;

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
        _modelSkinPicker.ItemTemplate = new FuncDataTemplate<string>((item, _) => new TextBlock { Text = item is null ? string.Empty : Path.GetFileName(item), TextTrimming = TextTrimming.CharacterEllipsis });
        _modelPicker.SelectionChanged += async (_, _) => { if (_suppressModelSelection) return; RefreshModelSkinPicker(); await RunUiActionAsync("model-selection", LoadSelectedModelAsync); };
        _modelSkinPicker.SelectionChanged += async (_, _) => { if (!_suppressModelSkinSelection) await RunUiActionAsync("model-skin-selection", LoadSelectedModelAsync); };
        _geosetMode.SelectionChanged += async (_, _) => await RunUiActionAsync("geoset-mode-change", LoadSelectedModelAsync);
        _showAttachmentPoints.Click += (_, _) => ApplyAttachmentOverlay();
        _attachmentPicker.SelectionChanged += (_, _) => ApplyAttachmentOverlay();
        _skinPicker.SelectionChanged += async (_, _) => { if (!_suppressAppearanceSelection) await RunUiActionAsync("appearance-skin-change", LoadSelectedModelAsync); };
        _facePicker.SelectionChanged += async (_, _) => { if (!_suppressAppearanceSelection) await RunUiActionAsync("appearance-face-change", LoadSelectedModelAsync); };
        _facialHairPicker.SelectionChanged += async (_, _) => { if (!_suppressAppearanceSelection) await RunUiActionAsync("appearance-facial-hair-change", LoadSelectedModelAsync); };
        _hairPicker.SelectionChanged += async (_, _) => { if (!_suppressAppearanceSelection) await RunUiActionAsync("appearance-hair-change", LoadSelectedModelAsync); };
        _appearanceSourcePicker.SelectionChanged += async (_, _) => { if (!_suppressAppearanceSelection) await RunUiActionAsync("appearance-source-change", LoadSelectedModelAsync); };
        _creatureAppearancePicker.SelectionChanged += async (_, _) => { if (!_suppressCreatureAppearanceSelection) await RunUiActionAsync("creature-appearance-change", LoadSelectedModelAsync); };
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
        var libraryLabel = new TextBlock { Text = "ASSET LIBRARY", FontWeight = FontWeight.Bold, FontSize = 10, Foreground = Brush.Parse("#C58A2B"), VerticalAlignment = VerticalAlignment.Center };
        var top = new Grid { RowDefinitions = new("Auto,Auto"), Margin = new Thickness(14, 10), RowSpacing = 7 };
        top.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, Children = { back, libraryLabel, browse, load } });
        Grid.SetRow(_library, 1); top.Children.Add(_library);

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
        var decisionInputs = new StackPanel { Spacing = 6, Children = { _assetCategory, _assetNotes } };
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
        var directoryPane = new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0,0,1,0), Child = left };
        var reviewAndCompare = new ResponsiveSplitGrid(middle, compare, 1.35, 2, compactFirstWeight: 1.4, compactSecondWeight: 1);
        var body = new ResponsiveSplitGrid(directoryPane, reviewAndCompare, 0.85, 3.35, compactFirstWeight: 1, compactSecondWeight: 3);
        var root = new Grid { RowDefinitions = new("Auto,*,Auto,Auto") }; root.Children.Add(top); Grid.SetRow(body, 1); root.Children.Add(body); Grid.SetRow(projectTools, 2); root.Children.Add(projectTools);
        var status = new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0,1,0,0), Padding = new Thickness(14,7), Child = _status }; Grid.SetRow(status, 3); root.Children.Add(status); return root;
    }

    private Control BuildComparisonPane()
    {
        var zoom = new Slider { Minimum = 0.1, Maximum = 4, Value = 1, HorizontalAlignment = HorizontalAlignment.Stretch }; zoom.ValueChanged += (_, e) => { _zoom = e.NewValue; ApplyZoom(); };
        var openLeft = new Button { Content = "Reveal left" }; openLeft.Click += (_, _) => RevealSlot(0); var openRight = new Button { Content = "Reveal right" }; openRight.Click += (_, _) => RevealSlot(1);
        _imageComparisonTools = new StackPanel { Spacing = 5, Children = { new TextBlock { Text = "SYNCHRONIZED ZOOM & PAN", VerticalAlignment = VerticalAlignment.Center, FontSize = 10, FontWeight = FontWeight.Bold, Margin = new Thickness(4, 0) }, zoom, new WrapPanel { Children = { openLeft, openRight } } } };
        var toolbar = new StackPanel { Spacing = 6, Margin = new Thickness(12, 8), Children = { _previewMode, _imageComparisonTools } };
        var slots = new Control[2];
        for (var index = 0; index < 2; index++)
        {
            var header = new Grid { ColumnDefinitions = new("Auto,*"), Margin = new Thickness(2), ColumnSpacing = 8, Children = { _slotButtons[index], WithColumn(_comparisonTitles[index], 1) } };
            _comparisonScrolls[index].Content = _comparisonImages[index]; _comparisonScrolls[index].HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto; _comparisonScrolls[index].VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
            var scrollIndex = index; _comparisonScrolls[index].ScrollChanged += (_, _) => SyncScroll(scrollIndex);
            var imageBorder = new Border { Background = Brush.Parse("#090B0F"), BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(1), Margin = new Thickness(3), ClipToBounds = true, Child = _comparisonScrolls[index] };
            var slot = new Grid { RowDefinitions = new("Auto,*"), Children = { header } };
            Grid.SetRow(imageBorder, 1); slot.Children.Add(imageBorder); slots[index] = slot;
        }
        var comparisonSlots = new ResponsiveSplitGrid(slots[0], slots[1], wideAspect: 1);
        _imageComparisonPane = comparisonSlots;
        var previousModel = new Button { Content = "← Model" }; previousModel.Click += (_, _) => MoveModel(-1); var nextModel = new Button { Content = "Model →" }; nextModel.Click += (_, _) => MoveModel(1);
        _modelPicker.HorizontalAlignment = HorizontalAlignment.Stretch;
        _modelSkinPicker.HorizontalAlignment = HorizontalAlignment.Stretch;
        var modelFilters = new StackPanel { Spacing = 5, Children = { _modelSearch, new WrapPanel { Children = { _modelFilter, previousModel, nextModel } } } };
        var appearanceHeader = new TextBlock { Text = "CHARSECTIONS BASE APPEARANCE", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#C58A2B") };
        var appearancePickers = new StackPanel { Spacing = 6, Children = { _skinPicker, _appearanceSourcePicker, _facePicker, _facialHairPicker, _hairPicker } };
        _creatureAppearancePanel.Children.Add(new TextBlock { Text = "CREATURE DISPLAY APPEARANCE", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#C58A2B") });
        _creatureAppearancePanel.Children.Add(_creatureAppearancePicker); _creatureAppearancePanel.Children.Add(_creatureAppearanceStatus);
        var geosetInspector = new Expander
        {
            Header = "Exact geoset inspector",
            Content = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "Each group selector permits one visible variant or Hidden. Base-body sections with geoset ID 0 always remain visible. Only the Everything stacked mode intentionally combines mutually exclusive variants.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8793A7"), FontSize = 11 },
                    _geosetInspectorStatus,
                    _geosetGroups
                }
            }
        };
        var attachmentInspector = new Expander
        {
            Header = "Equipment & effect attachment points",
            Content = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "These are the model's native WotLK attachment records—not guessed screen positions. Select one to highlight where helmets, shoulders, weapons, sheaths, spell effects, or vehicle seats bind. Item-model mounting will reuse this exact data.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8793A7"), FontSize = 11 },
                    _showAttachmentPoints,
                    _attachmentPicker,
                    _attachmentStatus
                }
            }
        };
        var exportModel = new Button { Content = "Export visible/current-pose OBJ…" }; exportModel.Click += async (_, _) => await RunUiActionAsync("model-export", ExportModelAsync);
        var modelActions = new WrapPanel { Orientation = Orientation.Horizontal, Children = { exportModel } };
        var modelSkinRow = new StackPanel { Spacing = 5, Children = { new TextBlock { Text = "SKIN VIEW / LOD", VerticalAlignment = VerticalAlignment.Center, FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#C58A2B") }, _modelSkinPicker } };
        var modelHeader = new StackPanel { Spacing = 7, Margin = new Thickness(9), Children = { new TextBlock { Text = "AUTOMATIC M2 MODEL BROWSER", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#C58A2B") }, modelFilters, _modelPicker, modelSkinRow, _geosetMode, modelActions, geosetInspector, attachmentInspector, _creatureAppearancePanel, appearanceHeader, appearancePickers, _appearanceStatus, _modelStatus } };
        var modelHeaderScroll = new ScrollViewer { Content = modelHeader, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var modelPane = new Grid { RowDefinitions = new("2*,Auto,3*"), IsVisible = false, Children = { modelHeaderScroll } };
        var modelSplitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = Brush.Parse("#2B3445") }; Grid.SetRow(modelSplitter, 1); modelPane.Children.Add(modelSplitter);
        var modelBorder = new Border { Background = Brush.Parse("#090D14"), BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(1), Child = _modelView }; Grid.SetRow(modelBorder, 2); modelPane.Children.Add(modelBorder); _modelPreviewPane = modelPane;
        var previewHost = new Grid { Children = { comparisonSlots, modelPane } };
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
        _directoryReady = false; _modelsDiscovered = false; _folderEntries = []; _filteredEntries = []; _selectedTexture = null; _resolvedModelTexturePath = null; _resolvedModelTextureCount = 0; _appearanceComposed = false; _creatureAppearanceBound = false; ApplyCreatureAppearancePlan(new(false, [], null, string.Empty));
        _modelDiscoveryTask = null;
        _allModels = []; _folderModels = []; _modelDiscoveryScope = directory.LogicalPath; _loadedModelGeometry = null; _loadedModelTextures = new Dictionary<int, RgbaTexture>(); _modelDependencyGraph = null;
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
        _selectedTexture = entry; _resolvedModelTexturePath = null; _resolvedModelTextureCount = 0; _loadedModelTextures = new Dictionary<int, RgbaTexture>(); _modelView.SetTexture(entry.FullPath); UpdateModelStatus();
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
            _loadedModelGeometry = null; _loadedModelTextures = new Dictionary<int, RgbaTexture>(); _creatureAppearanceBound = false;
            ApplyCreatureAppearancePlan(new(false, [], null, string.Empty));
            ClearGeosetInspector("Choose a compatible M2 to inspect its geosets.");
            ClearAttachmentInspector("Choose a compatible M2 to inspect its attachment points.");
            _modelStatus.Text = _allModels.Count == 0
                ? "No M2 files were found in this path or its parent content paths."
                : $"{_allModels.Count:N0} M2 file(s) were discovered, but none match the current model filter. Choose All models to inspect compatibility details.";
            return;
        }
        var selectedSkinPath = _modelSkinPicker.SelectedItem as string ?? model.SkinPath;
        if (model.Compatibility != AssetModelCompatibility.Ready || selectedSkinPath is null) { _loadedModelGeometry = null; _loadedModelTextures = new Dictionary<int, RgbaTexture>(); _modelDependencyGraph = null; _creatureAppearanceBound = false; _modelView.ClearGeometry(); ApplyCreatureAppearancePlan(new(false, [], null, string.Empty)); ClearGeosetInspector("This model has no compatible M2/SKIN geometry to inspect."); ClearAttachmentInspector("This model has no compatible attachment data to inspect."); _modelStatus.Text = $"{model.Status}\nSource: {model.Provenance} · {model.LogicalPath}\nChoose a READY model to render it."; return; }
        _modelStatus.Text = $"Loading {model.FileName}…";
        var stopwatch = Stopwatch.StartNew();
        DesktopCrashLogger.Debug("MODEL", "comparison-preview-start", ("model", model.ModelPath), ("skin", selectedSkinPath));
        try
        {
            var requestedSkin = _skinPicker.SelectedItem as CharacterBaseSkin; var requestedFace = _facePicker.SelectedItem as CharacterSection; var requestedFacialHair = _facialHairPicker.SelectedItem as CharacterSection; var requestedHair = _hairPicker.SelectedItem as CharacterSection; var requestedSource = (_appearanceSourcePicker.SelectedItem as AppearanceSourceChoice)?.FullPath; var requestedCreatureDisplayId = (_creatureAppearancePicker.SelectedItem as CreatureAppearanceChoice)?.Display.DisplayId;
            var appearance = await Task.Run(() => BuildAppearancePlan(model, requestedSkin, requestedFace, requestedFacialHair, requestedHair, requestedSource, token), token); if (!IsCurrent(token, activity) || request != _modelRequest || directoryRequest != _directoryRequest) return;
            var geosetMode = _geosetMode.SelectedIndex;
            var visibilityMode = geosetMode switch
            {
                0 => M2PreviewVisibilityMode.Automatic,
                3 => M2PreviewVisibilityMode.AllGeosets,
                _ => M2PreviewVisibilityMode.BaseAppearance
            };
            M2GeosetSelection? geosetSelection = geosetMode switch
            {
                0 when appearance.Geosets is { GroupVariants.Count: > 0 } geosets => new(geosets.GroupVariants, "CharHairGeosets.dbc + CharacterFacialHairStyles.dbc"),
                1 => new(M2GeosetCatalog.NakedCharacterSelection, "naked character preset"),
                2 when _manualGeosetVariants.Count > 0 => new(new Dictionary<int, int>(_manualGeosetVariants), "manual exact geoset inspector"),
                _ => null
            };
            var geometry = await Task.Run(() => M2PreviewGeometryService.Load(model.ModelPath, selectedSkinPath, visibilityMode, geosetSelection), token); if (!IsCurrent(token, activity) || request != _modelRequest || directoryRequest != _directoryRequest) return;
            var creatureAppearance = await Task.Run(() => BuildCreatureAppearancePlan(model, geometry, requestedCreatureDisplayId, token), token); if (!IsCurrent(token, activity) || request != _modelRequest || directoryRequest != _directoryRequest) return;
            var selectedModel = model with { SkinPath = selectedSkinPath };
            var graph = _index is null ? null : await Task.Run(() => AssetDependencyGraphService.AnalyzeModel(_index, selectedModel), token); if (!IsCurrent(token, activity) || request != _modelRequest || directoryRequest != _directoryRequest) return;
            ApplyAppearancePlan(appearance);
            ApplyCreatureAppearancePlan(creatureAppearance);
            AppendGeosetSelectionCoverage(geometry);
            var embeddedTextures = new Dictionary<int, RgbaTexture>(); string? embeddedTexturePath = null;
            _appearanceComposed = false; _creatureAppearanceBound = false;
            if (_selectedTexture is null)
            {
                var usedTextureDefinitions = geometry.UsedTextureDefinitionIndices.ToHashSet();
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
                if (appearance.SelectedSource is not null)
                {
                    try
                    {
                        var decodedAppearance = await Task.Run(() => DecodeAppearance(appearance, token), token);
                        foreach (var slot in geometry.TextureSlots.Where(slot => usedTextureDefinitions.Contains(slot.Index) && slot.Type == 1)) embeddedTextures[slot.Index] = decodedAppearance.Body;
                        if (decodedAppearance.Hair is not null) foreach (var slot in geometry.TextureSlots.Where(slot => usedTextureDefinitions.Contains(slot.Index) && slot.Type == 6)) embeddedTextures[slot.Index] = decodedAppearance.Hair;
                        embeddedTexturePath ??= appearance.SelectedSource.FullPath;
                        _appearanceComposed = true;
                        if (decodedAppearance.Missing.Count > 0) _appearanceStatus.Text += $" Missing from '{appearance.SelectedSource.Provenance}': {string.Join(", ", decodedAppearance.Missing)}.";
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException) { DesktopCrashLogger.Log($"Character appearance composition failed: {appearance.SelectedSource.FullPath}", exception); }
                    if (!IsCurrent(token, activity) || request != _modelRequest || directoryRequest != _directoryRequest) return;
                }
                if (creatureAppearance.Selected is { } creatureChoice)
                {
                    var missing = new List<string>(); var decodedVariations = new Dictionary<int, RgbaTexture>();
                    foreach (var slot in geometry.TextureSlots.Where(slot => usedTextureDefinitions.Contains(slot.Index) && slot.Type is >= 11 and <= 13))
                    {
                        var variation = checked((int)slot.Type - 11);
                        if (!creatureChoice.Source.CreatureTextures.TryGetValue(variation, out var texturePath)) { missing.Add($"variation {variation + 1:N0}"); continue; }
                        try
                        {
                            if (!decodedVariations.TryGetValue(variation, out var decoded)) { decoded = await Task.Run(() => BlpTextureService.Decode(texturePath), token); decodedVariations[variation] = decoded; }
                            embeddedTextures[slot.Index] = decoded; embeddedTexturePath ??= texturePath;
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException) { missing.Add(Path.GetFileName(texturePath)); DesktopCrashLogger.Log($"Creature appearance texture decode failed: {texturePath}", exception); }
                        if (!IsCurrent(token, activity) || request != _modelRequest || directoryRequest != _directoryRequest) return;
                    }
                    _creatureAppearanceBound = decodedVariations.Count > 0;
                    if (missing.Count > 0) _creatureAppearanceStatus.Text += $" Missing: {string.Join(", ", missing.Distinct(StringComparer.OrdinalIgnoreCase))}.";
                }
            }
            _loadedModelGeometry = geometry; _loadedModelTextures = new Dictionary<int, RgbaTexture>(embeddedTextures); _modelDependencyGraph = graph; _resolvedModelTexturePath = embeddedTexturePath; _resolvedModelTextureCount = embeddedTextures.Count; RefreshGeosetInspector(geometry); RefreshAttachmentInspector(geometry); _modelView.SetGeometry(geometry); ApplyAttachmentOverlay();
            if (_selectedTexture is null) _modelView.SetDecodedTextures(embeddedTextures);
            UpdateModelStatus();
            DesktopCrashLogger.Debug("MODEL", "comparison-preview-success", ("model", model.ModelPath), ("vertices", geometry.Vertices.Count), ("triangles", geometry.TriangleIndices.Count / 3), ("texture_slots", geometry.TextureSlots.Count), ("duration_ms", stopwatch.Elapsed.TotalMilliseconds));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!IsCurrent(token, activity) || request != _modelRequest) return;
            _loadedModelGeometry = null; _loadedModelTextures = new Dictionary<int, RgbaTexture>();
            _creatureAppearanceBound = false; ApplyCreatureAppearancePlan(new(false, [], null, string.Empty));
            DesktopCrashLogger.Log($"Comparison model preview failed: {model.ModelPath}", exception); ClearGeosetInspector("Model loading failed before the geoset table could be inspected."); ClearAttachmentInspector("Model loading failed before attachment points could be inspected.");
            _modelStatus.Text = $"Could not load {model.FileName}: {exception.Message}";
        }
    }

    private AppearancePlan BuildAppearancePlan(AssetComparisonModel model, CharacterBaseSkin? requestedSkin, CharacterSection? requestedFace, CharacterSection? requestedFacialHair, CharacterSection? requestedHair, string? requestedSource, CancellationToken token)
    {
        var identity = CharacterAppearanceService.Infer(model.LogicalPath, model.FileName);
        if (identity is null) return EmptyAppearance(null, "This model path does not identify a playable race and sex; CharSections appearance binding does not apply.");
        if (!File.Exists(Path.Combine(_session.Settings.CoreDbcPath, "CharSections.dbc"))) return EmptyAppearance(identity, $"Detected {identity.RaceName} {identity.SexName}, but CharSections.dbc is unavailable. Point Server & SQL at a server DBC folder to enable real appearances.");
        if (_index is null) return EmptyAppearance(identity, "The asset index is unavailable, so CharSections textures cannot be resolved.");
        var core=CharacterAppearancePreviewService.Build(_index,_session.Settings.CoreDbcPath,identity,requestedSkin?.Id,requestedFace?.Id,requestedFacialHair?.Id,requestedHair?.Id,requestedSource,model.Provenance,token);
        var sources=core.Sources.Select(source=>new AppearanceSourceChoice(source.Provenance,source.FullPath,source.ByteEquivalent)).ToArray();var selectedSource=core.SelectedSource is null?null:sources.FirstOrDefault(source=>source.FullPath.Equals(core.SelectedSource.FullPath,StringComparison.OrdinalIgnoreCase));
        return new(identity,core.Skins,core.SelectedSkin,core.Faces,core.SelectedFace,core.FacialHair,core.SelectedFacialHair,core.Hair,core.SelectedHair,core.Underwear,sources,selectedSource,core.Geosets,core.Message,core);
    }

    private void ApplyAppearancePlan(AppearancePlan plan)
    {
        _suppressAppearanceSelection = true;
        try
        {
            _skinPicker.ItemsSource = plan.Skins; _skinPicker.SelectedItem = plan.SelectedSkin; _skinPicker.IsEnabled = plan.Skins.Count > 0;
            _facePicker.ItemsSource = plan.Faces; _facePicker.SelectedItem = plan.SelectedFace; _facePicker.IsEnabled = plan.Faces.Count > 0;
            _facialHairPicker.ItemsSource = plan.FacialHair; _facialHairPicker.SelectedItem = plan.SelectedFacialHair; _facialHairPicker.IsEnabled = plan.FacialHair.Count > 0;
            _hairPicker.ItemsSource = plan.Hair; _hairPicker.SelectedItem = plan.SelectedHair; _hairPicker.IsEnabled = plan.Hair.Count > 0;
            _appearanceSourcePicker.ItemsSource = plan.Sources; _appearanceSourcePicker.SelectedItem = plan.SelectedSource; _appearanceSourcePicker.IsEnabled = plan.Sources.Count > 0;
            _appearanceStatus.Text = plan.Message;
        }
        finally { _suppressAppearanceSelection = false; }
    }

    private void AppendGeosetSelectionCoverage(M2PreviewGeometry geometry)
    {
        if (geometry.GeosetSelectionFindings.Count == 0) return;
        var missing = geometry.GeosetSelectionFindings.Where(finding => finding.Missing).ToArray();
        _appearanceStatus.Text += missing.Length == 0
            ? $" All {geometry.GeosetSelectionFindings.Count(finding => finding.RequestedVariant > 0):N0} requested visible geoset variant(s) exist in the selected SKIN."
            : $" MISSING MODEL GEOSETS: {string.Join(" ", missing.Select(finding => finding.ToString()))} No substitute was guessed.";
    }

    private CreatureAppearancePlan BuildCreatureAppearancePlan(AssetComparisonModel model, M2PreviewGeometry geometry, uint? requestedDisplayId, CancellationToken token)
    {
        var replaceable = geometry.TextureSlots.Where(slot => slot.Type is >= 11 and <= 13).Select(slot => slot.Type).Distinct().Order().ToArray();
        if (replaceable.Length == 0) return new(false, [], null, string.Empty);
        var dbcRoot = _session.Settings.CoreDbcPath;
        if (_index is null) return new(true, [], null, "Creature texture slots are present, but the processed asset index is unavailable.");
        var clientPath = PatchInputMapper.NormalizeArchivePath(Path.Combine(model.LogicalPath, model.FileName));
        var schema = string.IsNullOrWhiteSpace(_session.Settings.SchemaDefinitionPath) ? null : _session.Settings.SchemaDefinitionPath;
        var lookup = new CreatureDisplayPreviewService().ResolveModelDisplaysForProvenance(dbcRoot, schema, clientPath, _index.LibraryRoot, model.Provenance, token);
        var displays = lookup.Displays;
        var choices = displays.SelectMany(display => display.Sources.Where(source => source.Provenance.Equals(model.Provenance, StringComparison.OrdinalIgnoreCase) && Path.GetFullPath(source.ModelPath).Equals(Path.GetFullPath(model.ModelPath), StringComparison.OrdinalIgnoreCase)).Select(source => new CreatureAppearanceChoice(display, source)))
            .OrderByDescending(choice => choice.Source.CreatureTextures.Count).ThenBy(choice => choice.Display.DisplayId).ToArray();
        if (choices.Length == 0) return new(true, [], null, displays.Count == 0
            ? lookup.Finding
            : $"Found {displays.Count:N0} creature display record(s), but none has an exact '{model.Provenance}' model source. Cross-provenance texture borrowing is blocked.");
        var selected = requestedDisplayId is { } requested ? choices.FirstOrDefault(choice => choice.Display.DisplayId == requested) : null;
        selected ??= choices[0];
        var missing = replaceable.Count(type => !selected.Source.CreatureTextures.ContainsKey(checked((int)type - 11)));
        var message = $"{lookup.Finding} Resolved {choices.Length:N0} same-provenance appearance(s). Display {selected.Display.DisplayId:N0} supplies {selected.Source.CreatureTextures.Count:N0}/3 texture variation(s){(missing == 0 ? "." : $"; {missing:N0} used slot(s) remain missing.")}";
        return new(true, choices, selected, message);
    }

    private void ApplyCreatureAppearancePlan(CreatureAppearancePlan plan)
    {
        _suppressCreatureAppearanceSelection = true;
        try
        {
            _creatureAppearancePanel.IsVisible = plan.Applicable; _creatureAppearancePicker.ItemsSource = plan.Choices; _creatureAppearancePicker.SelectedItem = plan.Selected; _creatureAppearancePicker.IsEnabled = plan.Choices.Count > 0; _creatureAppearanceStatus.Text = plan.Message;
        }
        finally { _suppressCreatureAppearanceSelection = false; }
    }

    private static AppearancePlan EmptyAppearance(CharacterAppearanceIdentity? identity, string message) => new(identity, [], null, [], null, [], null, [], null, null, [], null, null, message);

    private sealed record DecodedAppearance(RgbaTexture Body, RgbaTexture? Hair, IReadOnlyList<string> Missing);
    private DecodedAppearance DecodeAppearance(AppearancePlan plan, CancellationToken token)
    {
        if (_index is null||plan.CorePlan is null)throw new InvalidOperationException("A resolved core appearance plan is required.");var composed=CharacterAppearancePreviewService.Compose(_index,plan.CorePlan,token);return new(composed.Body,composed.Hair,composed.Missing);
    }

    private void RefreshGeosetInspector(M2PreviewGeometry geometry)
    {
        var groups = M2GeosetCatalog.Describe(geometry.Submeshes);
        if (!string.Equals(_geosetInspectorModel, geometry.ModelPath, StringComparison.OrdinalIgnoreCase))
        {
            _geosetInspectorModel = geometry.ModelPath; _manualGeosetVariants.Clear();
            foreach (var group in groups)
            {
                var visible = group.Variants.FirstOrDefault(variant => variant.Variant > 0 && variant.Visible);
                _manualGeosetVariants[group.Group] = visible?.Variant ?? 0;
            }
        }
        var editable = groups.Where(group => group.Variants.Any(variant => variant.Variant > 0)).ToArray();
        _geosetGroups.ItemsSource = editable.Select(BuildGeosetGroupRow).ToArray();
        var baseSections = groups.Where(group => group.Group == 0).SelectMany(group => group.Variants).FirstOrDefault(variant => variant.Variant == 0)?.SubmeshIndices.Count ?? 0;
        var missing = geometry.GeosetSelectionFindings.Where(finding => finding.Missing).ToArray();
        _geosetInspectorStatus.Text = $"{groups.Count:N0} decoded group(s) · {editable.Sum(group => group.Variants.Count(variant => variant.Variant > 0)):N0} selectable variant(s) · {baseSections:N0} always-on base section(s). " +
            (missing.Length == 0 ? string.Empty : $"{missing.Length:N0} requested DBC/manual variant(s) are absent from this SKIN: {string.Join("; ", missing.Select(finding => $"group {finding.Group:N0} variant {finding.RequestedVariant:N0}"))}. ") +
            (_geosetMode.SelectedIndex == 2 ? "Manual selections are live." : "Switch to Manual mode to change the selectors below without stacking variants.");
    }

    private void ClearGeosetInspector(string message)
    {
        _geosetInspectorModel = null; _manualGeosetVariants.Clear(); _geosetGroups.ItemsSource = null; _geosetInspectorStatus.Text = message;
    }

    private void RefreshAttachmentInspector(M2PreviewGeometry geometry)
    {
        var previous = (_attachmentPicker.SelectedItem as M2PreviewAttachment)?.Id;
        _attachmentPicker.ItemsSource = geometry.Attachments;
        _attachmentPicker.IsEnabled = geometry.Attachments.Count > 0;
        _attachmentPicker.SelectedItem = geometry.Attachments.FirstOrDefault(attachment => attachment.Id == previous)
            ?? geometry.Attachments.FirstOrDefault(attachment => attachment.Id == 11)
            ?? geometry.Attachments.FirstOrDefault();
        var lookupSlots = geometry.Attachments.SelectMany(attachment => attachment.LookupSlots).Distinct().Count();
        _attachmentStatus.Text = geometry.Attachments.Count == 0
            ? "This M2 contains no attachment records."
            : $"{geometry.Attachments.Count:N0} attachment record(s) · {lookupSlots:N0} populated lookup slot(s) · {geometry.Bones.Count:N0} validated bones.";
    }

    private void ClearAttachmentInspector(string message)
    {
        _attachmentPicker.ItemsSource = null; _attachmentPicker.SelectedItem = null; _attachmentPicker.IsEnabled = false; _attachmentStatus.Text = message;
        _modelView.SetAttachmentOverlay(false);
    }

    private void ApplyAttachmentOverlay()
    {
        var selected = _attachmentPicker.SelectedItem as M2PreviewAttachment;
        _modelView.SetAttachmentOverlay(_showAttachmentPoints.IsChecked == true, selected?.Index);
        if (selected is not null)
            _attachmentStatus.Text = $"ID {selected.Id:N0} · {selected.Name} · record {selected.Index:N0} · bone {selected.BoneIndex:N0} · bind position ({selected.Position.X:0.###}, {selected.Position.Y:0.###}, {selected.Position.Z:0.###}) · lookup slot(s) {(selected.LookupSlots.Count == 0 ? "none" : string.Join(", ", selected.LookupSlots))}.";
    }

    private Control BuildGeosetGroupRow(M2GeosetGroup group)
    {
        var choices = new List<GeosetChoice> { new(0, "Hidden") };
        choices.AddRange(group.Variants.Where(variant => variant.Variant > 0).Select(variant =>
        {
            var geosetId = group.Group == 0 ? variant.Variant : group.Group * 100 + variant.Variant;
            return new GeosetChoice(variant.Variant, $"Variant {variant.Variant:N0} · ID {geosetId:N0} · {variant.SubmeshIndices.Count:N0} section(s) · {variant.Triangles:N0} triangles");
        }));
        var selectedVariant = _manualGeosetVariants.GetValueOrDefault(group.Group);
        var selector = new ComboBox { ItemsSource = choices, SelectedItem = choices.FirstOrDefault(choice => choice.Variant == selectedVariant) ?? choices[0], HorizontalAlignment = HorizontalAlignment.Stretch };
        selector.SelectionChanged += async (_, _) =>
        {
            if (selector.SelectedItem is not GeosetChoice choice) return;
            _manualGeosetVariants[group.Group] = choice.Variant;
            if (_geosetMode.SelectedIndex == 2) await RunUiActionAsync("manual-geoset-change", LoadSelectedModelAsync);
        };
        return new Grid
        {
            ColumnDefinitions = new("*,*"), ColumnSpacing = 8, Margin = new Thickness(0, 2), Children =
            {
                new TextBlock { Text = $"{group.Group:N0} · {group.Name}", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap },
                WithColumn(selector, 1)
            }
        };
    }

    private async Task ExportModelAsync()
    {
        if (_loadedModelGeometry is null || _modelPicker.SelectedItem is not AssetComparisonModel model)
        {
            _modelStatus.Text = "Load a READY M2/SKIN model before exporting.";
            return;
        }
        var file = await GetStorageProvider().SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export the visible/current-pose model",
            SuggestedFileName = Path.GetFileNameWithoutExtension(model.FileName) + "-visible.obj",
            FileTypeChoices = [new FilePickerFileType("Wavefront model") { Patterns = ["*.obj"] }]
        });
        var path = file?.TryGetLocalPath(); if (path is null) return;
        var geometry = _loadedModelGeometry; var pose = _modelView.SnapshotPose(); var textures = new Dictionary<int, RgbaTexture>(_loadedModelTextures);
        _modelStatus.Text = $"Exporting {geometry.TriangleIndices.Count / 3:N0} visible triangle(s)…";
        var result = await Task.Run(() => M2ObjExportService.Export(geometry, path, pose, textures, overwrite: true));
        _modelStatus.Text = $"Exported {(result.Posed ? "the current animation pose" : "the static visible mesh")} as {result.Triangles:N0} triangle(s) with {result.TexturePaths.Count:N0} resolved texture(s).\nOBJ: {result.ObjPath}\nExact provenance and WoW material metadata: {result.ReceiptPath}";
        DesktopCrashLogger.Debug("MODEL", "obj-export-success", ("model", geometry.ModelPath), ("output", result.ObjPath), ("vertices", result.Vertices), ("triangles", result.Triangles), ("posed", result.Posed), ("textures", result.TexturePaths.Count));
    }

    private void UpdateModelStatus()
    {
        if (_modelPicker.SelectedItem is not AssetComparisonModel model) return;
        var texture = _selectedTexture is null
            ? _resolvedModelTexturePath is not null ? $"Resolved {_resolvedModelTextureCount:N0} material texture(s); first source: {Path.GetFileName(_resolvedModelTexturePath)}." : (_modelOnlyDirectory ? "Texture overlay: no embedded or appearance BLP resolved; geometry remains available for inspection." : "No texture card selected.")
            : $"Selected texture candidate: {_selectedTexture.Provenance} · {_selectedTexture.FileName}";
        var slots = _loadedModelGeometry is null ? "Texture slots: load pending." : _loadedModelGeometry.TextureSlots.Count == 0 ? "Texture slots: none declared." : "Texture slots: " + string.Join(", ", _loadedModelGeometry.TextureSlots.Select(slot => $"{slot.Index}:{TextureTypeName(slot.Type)}{(string.IsNullOrWhiteSpace(slot.EmbeddedPath) ? string.Empty : $"={slot.EmbeddedPath}")}"));
        var dependencies = _modelDependencyGraph is null ? "Dependencies: inspection pending." : $"Dependencies: {_modelDependencyGraph.Resolved.Count:N0} resolved · {_modelDependencyGraph.ExternalBindings.Count:N0} appearance/DBC binding(s) · {_modelDependencyGraph.Blocking.Count:N0} BLOCKING";
        var multiTextureUnits = _loadedModelGeometry?.MaterialUnits.Count(material => material.TextureStages.Count > 1) ?? 0;
        var multiTextureFallbacks = _loadedModelGeometry?.MaterialUnits.Count(material => material.TextureStages.Count > 1 && !material.Combiner.Supported) ?? 0;
        var multiTextureApproximations = _loadedModelGeometry?.MaterialUnits.Count(material => material.TextureStages.Count > 1 && material.Combiner.Supported && !material.Combiner.Exact) ?? 0;
        var materials = _loadedModelGeometry is null ? "Materials: load pending." : $"Materials: {_loadedModelGeometry.MaterialUnits.Count:N0} unit(s) · {multiTextureUnits:N0} multi-texture · {multiTextureApproximations:N0} labeled approximation(s) · {multiTextureFallbacks:N0} explicitly labeled first-stage fallback(s)";
        var geosets = _loadedModelGeometry is null || _loadedModelGeometry.Submeshes.Count == 0
            ? "Geosets: no SKIN submesh table; showing the complete mesh."
            : $"Geosets: {_loadedModelGeometry.Submeshes.Count(section => section.Visible):N0} visible of {_loadedModelGeometry.Submeshes.Count:N0} · {_loadedModelGeometry.VisibilityMode} · {_loadedModelGeometry.TriangleIndices.Count / 3:N0} of {_loadedModelGeometry.TotalTriangleIndices / 3:N0} triangles" +
              (_loadedModelGeometry.GeosetSelection is null ? string.Empty : $" · {_loadedModelGeometry.GeosetSelection.Source}: {string.Join(", ", _loadedModelGeometry.GeosetSelection.GroupVariants.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}"))}") +
              (_loadedModelGeometry.VisibilityMode == M2PreviewVisibilityMode.AllGeosets ? " · WARNING: mutually exclusive variants are intentionally stacked" : string.Empty);
        if (_loadedModelGeometry is not null)
        {
            var missing = _loadedModelGeometry.GeosetSelectionFindings.Where(finding => finding.Missing).ToArray();
            if (missing.Length > 0) geosets += $" · MISSING REQUESTED: {string.Join(", ", missing.Select(finding => $"{finding.Group}:{finding.RequestedVariant}"))} (no guessed substitute)";
        }
        var visibleGeosets = _loadedModelGeometry is null ? string.Empty : string.Join(" · ", M2GeosetCatalog.Describe(_loadedModelGeometry.Submeshes)
            .SelectMany(group => group.Variants.Where(variant => variant.Visible).Select(variant => $"{group.Name}={variant.Variant}")));
        var previewNote = _appearanceComposed
            ? "Geometry, per-material textures, supported legacy and explicit two/three-stage WotLK combiners with primary/secondary UV and view-space sphere-map routing, the selected CharSections appearance, and animation playback are live. Unhandled explicit/other three-plus-stage shaders and remaining particle variants stay visibly labeled; the visible/current pose can be exported above."
            : _creatureAppearanceBound
            ? "Geometry, same-provenance CreatureDisplayInfo texture variations, supported legacy and explicit WotLK combiners including view-angle edge fade, and animation playback are live. Missing variations and unsupported shader families remain visibly labeled rather than borrowed from another mod layer."
            : _resolvedModelTexturePath is not null
            ? "Geometry, resolved per-material textures, supported legacy and explicit two/three-stage WotLK combiners with primary/secondary UV and view-space sphere-map routing, and animation playback are live. Unresolved replaceable slots, unhandled explicit/other three-plus-stage shaders, and remaining particle variants stay visibly labeled."
            : _modelOnlyDirectory
            ? "Geometry and animation are live. Replacement texture resolution and Character layer/CharSections composition remain incomplete until the appearance plan supplies every layer."
            : "Geometry, animation, and the selected PNG diagnostic override are live. The override is not treated as an invented WoW material binding.";
        _modelStatus.Text = $"READY · {model.Provenance} · {model.FileName}\nContent path: {model.LogicalPath} · Skin: {Path.GetFileName(_loadedModelGeometry?.SkinPath ?? model.SkinPath)} · {model.SkinPaths.Count:N0} available view(s)\n{geosets}{(visibleGeosets.Length == 0 ? string.Empty : $"\nVisible groups: {visibleGeosets}")}\n{texture}\n{slots}\n{materials}\n{dependencies}\n{previewNote}";
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
        RefreshModelSkinPicker();
        if (requestModelLoad && _active && _previewMode.SelectedIndex == 1)
            _ = RunUiActionAsync("model-filter-selection", LoadSelectedModelAsync);
    }

    private void RefreshModelSkinPicker()
    {
        _suppressModelSkinSelection = true;
        try
        {
            if (_modelPicker.SelectedItem is not AssetComparisonModel model)
            {
                _modelSkinPicker.ItemsSource = Array.Empty<string>(); _modelSkinPicker.SelectedIndex = -1; _modelSkinPicker.IsEnabled = false; return;
            }
            _modelSkinPicker.ItemsSource = model.SkinPaths; _modelSkinPicker.SelectedItem = model.SkinPath; _modelSkinPicker.IsEnabled = model.SkinPaths.Count > 0;
            if (_modelSkinPicker.SelectedIndex < 0 && model.SkinPaths.Count > 0) _modelSkinPicker.SelectedIndex = 0;
        }
        finally { _suppressModelSkinSelection = false; }
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
                if (_modelSkinPicker.SelectedItem is string selectedSkin) model = model with { SkinPath = selectedSkin };
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
    private static T WithCell<T>(T control, int row, int column) where T : Control { Grid.SetRow(control, row); Grid.SetColumn(control, column); return control; }
}
