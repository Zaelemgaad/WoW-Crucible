using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System.Numerics;
using WoWCrucible.Core;
using WoWCrucible.Desktop.Controls;

namespace WoWCrucible.Desktop;

internal sealed class MapWorkspaceView : UserControl, IDisposable
{
    private sealed record AssetCandidateChoice(ClientAssetLocation Location)
    {
        public override string ToString() => $"{Location.Provenance} · {Path.GetFileName(Location.SourcePath)}";
    }
    private sealed record SceneProvenanceChoice(string Name, int ResolvedPaths, int TotalPaths, ProcessedAssetEffectiveProfile? EffectiveProfile = null)
    {
        public override string ToString() => $"{Name} · {ResolvedPaths:N0}/{TotalPaths:N0} distinct scene paths";
    }
    private sealed record GroundEffectChoice(int Id, GroundEffectTextureRecord? Effect)
    {
        public override string ToString() => Effect?.Summary ?? "-1 · no ground-effect doodads";
    }
    private sealed record LiquidTypeChoice(ushort Id, LiquidTypeRecord Type) { public override string ToString() => Type.Summary; }
    private sealed record LiquidHeightModeChoice(string Name, AdtLiquidHeightMode Mode) { public override string ToString() => Name; }
    private sealed record MapSceneBuildResult(AssetComparisonIndex Index, IReadOnlyList<SceneProvenanceChoice> Choices, string? Provenance,
        AdtTerrainSceneGeometry? Terrain, AdtTerrainMaterialSet? Materials, IReadOnlyList<MapSceneM2Instance> M2, IReadOnlyList<MapSceneWmoInstance> Wmo,
        int RequestedPlacements, int OmittedPlacements, int UnresolvedPlacements, IReadOnlyList<string> Findings);
    private sealed record MapObjectReferenceChoice(string? ClientPath, MapWmoPlacement? WmoPlacement, MapM2Placement? M2Placement)
    {
        public bool IsWmo => WmoPlacement is not null || ClientPath is { } path && Path.GetExtension(path).Equals(".wmo", StringComparison.OrdinalIgnoreCase);
        public override string ToString()
        {
            if (WmoPlacement is { } wmo) return $"WMO · UID {wmo.UniqueId:N0} · {Path.GetFileName(ClientPath)} · pos {wmo.Position.X:0.##}, {wmo.Position.Y:0.##}, {wmo.Position.Z:0.##}";
            if (M2Placement is { } m2) return $"M2 · UID {m2.UniqueId:N0} · {Path.GetFileName(ClientPath)} · pos {m2.Position.X:0.##}, {m2.Position.Y:0.##}, {m2.Position.Z:0.##}";
            return $"{(IsWmo ? "WMO" : "M2")} · {ClientPath ?? "unresolved client path"}";
        }
    }
    private static readonly MapBrushModeChoice[] BrushModes = [new("Raise / lower", AdtTerrainBrushMode.RaiseLower), new("Flatten toward height", AdtTerrainBrushMode.Flatten), new("Smooth terrain", AdtTerrainBrushMode.Smooth), new("Seeded noise", AdtTerrainBrushMode.Noise)];
    private static readonly LiquidHeightModeChoice[] LiquidHeightModes = [new("Keep existing heights", AdtLiquidHeightMode.Keep), new("Shift every height", AdtLiquidHeightMode.Shift), new("Set one flat height", AdtLiquidHeightMode.SetFlat)];
    private readonly DesktopWorkspaceSession _session;
    private readonly WorldLightingView _lightingView;
    private readonly TabControl _workspaceTabs = new();
    private readonly TextBox _path = new() { PlaceholderText = "Open or drop a WotLK ADT, WDT, or WDL file…" };
    private readonly TextBlock _summary = Info("No map file loaded.");
    private readonly TextBlock _selected = Info("Select a present grid cell for exact terrain metadata.");
    private readonly ListBox _chunks = new();
    private readonly ListBox _dependencies = new();
    private readonly TextBlock _status = Info("Read-only native inspection · no legacy map executable required");
    private readonly MapGridControl _grid = new();
    private readonly StackPanel _wdtEditor = new() { Spacing = 8, IsVisible = false };
    private readonly StackPanel _adtEditor = new() { Spacing = 8, IsVisible = false };
    private readonly WmoPreviewView _wmoPreview = new();
    private readonly M2PreviewView _m2Preview = new() { IsVisible = false };
    private readonly MapSceneView _mapScene = new();
    private readonly ComboBox _sceneProvenance = new() { PlaceholderText = "Build once to discover coherent provenance layers…" };
    private readonly TextBox _scenePlacementLimit = new() { Text = "2000", PlaceholderText = "Placement load limit; 0 uses all" };
    private readonly TextBlock _sceneStatus = Info("Load an ADT, choose one coherent source provenance, and build a real terrain + placement scene.");
    private readonly TextBlock _wmoStatus = Info("Select a placed or referenced WMO/M2 object to inspect its native geometry.");
    private readonly TabControl _visualTabs = new();
    private readonly TextBox _wmoLibrary = new() { PlaceholderText = "Processed asset library root…" };
    private readonly ComboBox _wmoReferences = new() { PlaceholderText = "No placed or referenced objects loaded from this map" };
    private readonly ComboBox _wmoCandidates = new() { PlaceholderText = "Select an explicit provenance candidate" };
    private readonly TextBox _placementX = new(); private readonly TextBox _placementY = new(); private readonly TextBox _placementZ = new();
    private readonly TextBox _placementRotX = new(); private readonly TextBox _placementRotY = new(); private readonly TextBox _placementRotZ = new();
    private readonly TextBox _placementScale = new();
    private readonly TextBox _placementPositionSnap = new() { Text = "0", PlaceholderText = "Position grid step; 0 disables" }; private readonly TextBox _placementRotationSnap = new() { Text = "0", PlaceholderText = "Rotation degree step; 0 disables" }; private readonly TextBox _placementScaleSnap = new() { Text = "0", PlaceholderText = "Scale step; 0 disables" };
    private readonly TextBox _placementUid = new() { PlaceholderText = "Blank scans sibling ADTs and allocates the next UID" };
    private readonly TextBox _placementFlags = new() { Text = "0", PlaceholderText = "Flags (decimal or 0x...)" };
    private readonly TextBox _placementDoodadSet = new() { Text = "0", PlaceholderText = "WMO doodad set" };
    private readonly TextBox _placementNameSet = new() { Text = "0", PlaceholderText = "WMO name set" };
    private readonly TextBlock _placementStatus = Info("Select an existing MDDF or MODF placement to edit its transform safely.");
    private readonly TextBox _heightDelta = new() { Text = "0", PlaceholderText = "Signed terrain height delta" };
    private readonly TextBox _brushCenterX = new() { Text = "8", PlaceholderText = "Center X (0–16)" };
    private readonly TextBox _brushCenterY = new() { Text = "8", PlaceholderText = "Center Y (0–16)" };
    private readonly TextBox _brushRadius = new() { Text = "1", PlaceholderText = "Radius" };
    private readonly TextBox _brushStrength = new() { Text = "5", PlaceholderText = "Signed strength" };
    private readonly ComboBox _brushMode = new() { ItemsSource = BrushModes, SelectedIndex = 0 };
    private readonly ComboBox _brushFalloff = new() { ItemsSource = Enum.GetValues<AdtTerrainBrushFalloff>(), SelectedItem = AdtTerrainBrushFalloff.Smooth };
    private readonly TextBox _brushTarget = new() { Text = "0", PlaceholderText = "Flatten target height" };
    private readonly TextBox _brushSeed = new() { Text = "0", PlaceholderText = "Noise seed" };
    private readonly TextBox _textureLayerSlot = new() { Text = "0", PlaceholderText = "Layer slot" };
    private readonly ComboBox _textureChoice = new() { PlaceholderText = "Existing MTEX texture…" };
    private readonly TextBox _groundEffectLayerSlot = new() { Text = "0", PlaceholderText = "Layer slot" };
    private readonly TextBox _groundEffectId = new() { Text = "-1", PlaceholderText = "-1 clears; otherwise GroundEffectTexture ID" };
    private readonly TextBox _groundEffectSearch = new() { PlaceholderText = "Search effect ID, density, sound, or doodad path…" };
    private readonly ComboBox _groundEffectChoice = new() { PlaceholderText = "Decoded GroundEffectTexture result…" };
    private readonly TextBlock _groundEffectInfo = Info("Raw mode is available now; configure the server DBC folder for decoded GroundEffectTexture and GroundEffectDoodad choices.");
    private readonly TextBox _liquidLayerSlot = new() { Text = "0", PlaceholderText = "MH2O layer slot" };
    private readonly TextBox _liquidTypeId = new() { PlaceholderText = "Blank keeps each existing LiquidType ID" };
    private readonly TextBox _liquidTypeSearch = new() { PlaceholderText = "Search LiquidType ID, name, category, spell, or texture…" };
    private readonly ComboBox _liquidTypeChoice = new() { PlaceholderText = "Decoded LiquidType result…" };
    private readonly ComboBox _liquidHeightMode = new() { ItemsSource = LiquidHeightModes, SelectedIndex = 0 };
    private readonly TextBox _liquidHeightValue = new() { Text = "0", PlaceholderText = "Shift delta or absolute flat height" };
    private readonly TextBlock _liquidInfo = Info("Load an ADT to inspect all MH2O layers. Configure the server DBC folder for decoded LiquidType choices; blank type retains each current ID.");
    private readonly TextBox _newTexturePath = new() { PlaceholderText = @"Client-relative texture path, e.g. Tileset\Crucible\Ground.blp" };
    private readonly ComboBox _newTextureEncoding = new() { ItemsSource = Enum.GetValues<AdtNewLayerEncoding>(), SelectedItem = AdtNewLayerEncoding.Auto };
    private readonly TextBox _newTextureInitialAlpha = new() { Text = "0", PlaceholderText = "Initial alpha 0–255" };
    private readonly TextBox _alphaLayerSlot = new() { Text = "1", PlaceholderText = "Painted layer slot" };
    private readonly TextBox _alphaTarget = new() { Text = "255", PlaceholderText = "Target alpha 0–255" };
    private readonly TextBox _alphaOpacity = new() { Text = "1", PlaceholderText = "Opacity 0–1" };
    private readonly ComboBox _alphaFalloff = new() { ItemsSource = Enum.GetValues<AdtTerrainBrushFalloff>(), SelectedItem = AdtTerrainBrushFalloff.Smooth };
    private readonly CheckBox _alphaRestrict = new() { Content = "Restrict paint to selected cells" };
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _wmoOperation;
    private AssetComparisonIndex? _wmoLibraryIndex;
    private MapAssetInspection? _inspection;
    private AdtTextureLayerInspection? _textureSourceInspection;
    private AdtTextureLayerInspection? _textureInspection;
    private AdtAlphaMapInspection? _alphaSourceInspection;
    private AdtAlphaMapInspection? _alphaInspection;
    private AdtHeightEditPlan? _heightPlan;
    private AdtTerrainBrushPlan? _brushPlan;
    private AdtTextureLayerPlan? _texturePlan;
    private AdtGroundEffectPlan? _groundEffectPlan;
    private AdtLiquidInspection? _liquidSourceInspection;
    private AdtLiquidInspection? _liquidInspection;
    private AdtLiquidPlan? _liquidPlan;
    private AdtTextureStructurePlan? _textureStructurePlan;
    private AdtAlphaBrushPlan? _alphaPlan;
    private WdtTileEditPlan? _wdtPlan;
    private AdtMultiTilePlacementTransformPlan? _placementPlan;
    private AdtMultiTilePlacementPlan? _placementMultiTilePlan;
    private uint? _pendingLightingId;
    private bool _updatingScenePreviewFields;
    private bool _updatingGroundEffectChoices;
    private bool _updatingLiquidTypeChoices;
    private GroundEffectCatalog? _groundEffectCatalog;
    private LiquidTypeCatalog? _liquidTypeCatalog;

    public event EventHandler? BackRequested;
    public event EventHandler<DbcRecordNavigationRequest>? OpenDbcRecordRequested;

    public MapWorkspaceView(DesktopWorkspaceSession session)
    {
        _session = session; _lightingView = new WorldLightingView(session); _lightingView.OpenDbcRecordRequested += (_, request) => OpenDbcRecordRequested?.Invoke(this, request); _wmoLibrary.Text = !string.IsNullOrWhiteSpace(session.Settings.ProcessedAssetLibraryPath) ? session.Settings.ProcessedAssetLibraryPath : Directory.Exists(@"G:\Crucible-Extras-Processed") ? @"G:\Crucible-Extras-Processed" : string.Empty;
        AutomationProperties.SetName(_grid, "Map terrain and world tile grid"); AutomationProperties.SetName(_groundEffectLayerSlot, "Ground effect layer slot"); AutomationProperties.SetName(_groundEffectId, "Ground effect ID"); AutomationProperties.SetName(_groundEffectSearch, "Search decoded ground effects"); AutomationProperties.SetName(_groundEffectChoice, "Decoded ground effect choice"); AutomationProperties.SetName(_placementX, "Placement X"); AutomationProperties.SetName(_placementY, "Placement Y"); AutomationProperties.SetName(_placementZ, "Placement Z"); AutomationProperties.SetName(_placementRotX, "Placement rotation X"); AutomationProperties.SetName(_placementRotY, "Placement rotation Y"); AutomationProperties.SetName(_placementRotZ, "Placement rotation Z"); AutomationProperties.SetName(_placementScale, "Placement scale"); AutomationProperties.SetName(_placementPositionSnap, "Placement position snap step"); AutomationProperties.SetName(_placementRotationSnap, "Placement rotation snap step"); AutomationProperties.SetName(_placementScaleSnap, "Placement scale snap step"); AutomationProperties.SetName(_wmoReferences, "Map object reference"); AutomationProperties.SetName(_wmoCandidates, "Map object provenance"); AutomationProperties.SetName(_sceneProvenance, "Map scene provenance"); AutomationProperties.SetName(_scenePlacementLimit, "Map scene placement limit"); AutomationProperties.SetName(_sceneStatus, "Map scene status");
        AutomationProperties.SetName(_liquidLayerSlot, "Liquid layer slot"); AutomationProperties.SetName(_liquidTypeId, "Liquid type ID"); AutomationProperties.SetName(_liquidTypeSearch, "Search decoded liquid types"); AutomationProperties.SetName(_liquidTypeChoice, "Decoded liquid type choice"); AutomationProperties.SetName(_liquidHeightMode, "Liquid height mode"); AutomationProperties.SetName(_liquidHeightValue, "Liquid height value");
        _sceneStatus.TextWrapping = TextWrapping.NoWrap;
        var back = new Button { Content = "← Editor" }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var open = new Button { Content = "Open map file…" }; open.Click += async (_, _) => await PickAsync();
        var inspect = new Button { Content = "Inspect / reload" }; inspect.Click += async (_, _) => await OpenAsync(_path.Text);
        var createWdt = new Button { Content = "Create empty terrain WDT…" }; createWdt.Click += async (_, _) => await CreateWdtAsync();
        var openWmo = new Button { Content = "Preview extracted WMO…" }; openWmo.Click += async (_, _) => await PickWmoAsync();
        var chooseWmoLibrary = new Button { Content = "Choose processed library…" }; chooseWmoLibrary.Click += async (_, _) => await PickWmoLibraryAsync();
        var previewReference = new Button { Content = "Preview selected object" }; previewReference.Click += async (_, _) => await PreviewSelectedObjectAsync();
        var showPlacementInScene = new Button { Content = "Show live placement preview" }; showPlacementInScene.Click += async (_, _) => await ShowPlacementScenePreviewAsync();
        var previewPlacement = new Button { Content = "Preview coordinated transform" }; previewPlacement.Click += async (_, _) => await PreviewPlacementTransformAsync();
        var savePlacement = new Button { Content = "Build multi-tile transform payload…" }; savePlacement.Click += async (_, _) => await SavePlacementTransformAsync();
        var previewPlacementAdd = new Button { Content = "Preview add / clone" }; previewPlacementAdd.Click += async (_, _) => await PreviewPlacementAddAsync();
        var savePlacementAdd = new Button { Content = "Build multi-tile add payload…" }; savePlacementAdd.Click += async (_, _) => await SavePlacementLifecycleAsync(AdtPlacementLifecycleOperation.Add);
        var previewPlacementDelete = new Button { Content = "Review delete" }; previewPlacementDelete.Click += async (_, _) => await PreviewPlacementDeleteAsync();
        var savePlacementDelete = new Button { Content = "Build multi-tile delete payload…" }; savePlacementDelete.Click += async (_, _) => await SavePlacementLifecycleAsync(AdtPlacementLifecycleOperation.Delete);
        var buildScene = new Button { Content = "Build terrain + placement scene" }; buildScene.Click += async (_, _) => await BuildMapSceneAsync(buildScene);
        ToolTip.SetTip(buildScene, "One explicit provenance is used for the complete scene. Ambiguous or absent objects stay visibly unresolved; Crucible never mixes patches to make the view look complete.");
        var openPlacementControls = new Button { Content = "Open placement controls" }; openPlacementControls.Click += (_, _) => _visualTabs.SelectedIndex = 2;
        _wmoReferences.SelectionChanged += async (_, _) => { _mapScene.ClearPlacementPreview(); LoadPlacementFields(); await ResolveSelectedWmoAsync(); };
        _wmoCandidates.SelectionChanged += (_, _) => { _placementPlan = null; _placementMultiTilePlan = null; _mapScene.ClearPlacementPreview(); DescribeSelectedWmoCandidate(); };
        foreach (var input in PlacementInputs()) input.TextChanged += (_, _) => { _placementPlan = null; _placementMultiTilePlan = null; UpdateScenePlacementPreviewFromFields(); };
        foreach (var input in PlacementSnapInputs()) input.TextChanged += (_, _) => UpdateScenePlacementSnapSettings();
        _grid.CellsSelected += (_, cells) => { _heightPlan = null; _wdtPlan = null; _groundEffectPlan = null; InvalidateLiquidPreview(refreshSelection: false); InvalidateTexturePreview(refreshSelection: false); InvalidateTextureStructurePreview(refreshSelection: false); InvalidateAlphaPreview(refreshSelection: false); _selected.Text = DescribeSelection(cells); };
        var selectAll = new Button { Content = "Select all present" }; selectAll.Click += (_, _) => _grid.SelectAllPresent();
        var selectAllWdt = new Button { Content = "Select all present" }; selectAllWdt.Click += (_, _) => _grid.SelectAllPresent();
        var selectMissing = new Button { Content = "Select all missing WDT tiles" }; selectMissing.Click += (_, _) => _grid.SelectAllAbsent();
        var clear = new Button { Content = "Clear selection" }; clear.Click += (_, _) => _grid.ClearSelection();
        var clearWdt = new Button { Content = "Clear selection" }; clearWdt.Click += (_, _) => _grid.ClearSelection();
        var previewWdtPresent = new Button { Content = "Preview selected as present" }; previewWdtPresent.Click += async (_, _) => await PreviewWdtAsync(true);
        var previewWdtAbsent = new Button { Content = "Preview selected as absent" }; previewWdtAbsent.Click += async (_, _) => await PreviewWdtAsync(false);
        var saveWdt = new Button { Content = "Write reviewed WDT…" }; saveWdt.Click += async (_, _) => await SaveWdtAsync();
        AutomationProperties.SetName(previewWdtPresent, "Preview WDT tiles present"); AutomationProperties.SetName(previewWdtAbsent, "Preview WDT tiles absent"); AutomationProperties.SetName(saveWdt, "Write reviewed WDT tile table");
        var previewHeight = new Button { Content = "Preview height offset" }; previewHeight.Click += async (_, _) => await PreviewHeightAsync();
        var saveHeight = new Button { Content = "Write edited ADT…" }; saveHeight.Click += async (_, _) => await SaveHeightAsync();
        var previewBrush = new Button { Content = "Preview vertex brush" }; previewBrush.Click += async (_, _) => await PreviewBrushAsync();
        var saveBrush = new Button { Content = "Write brushed ADT…" }; saveBrush.Click += async (_, _) => await SaveBrushAsync();
        var previewTexture = new Button { Content = "Preview layer texture" }; previewTexture.Click += async (_, _) => await PreviewTextureAsync();
        var saveTexture = new Button { Content = "Write texture-edited ADT…" }; saveTexture.Click += async (_, _) => await SaveTextureAsync();
        var reloadGroundEffects = new Button { Content = "Reload decoded ground effects" }; reloadGroundEffects.Click += async (_, _) => await LoadGroundEffectCatalogAsync(force: true);
        var previewGroundEffect = new Button { Content = "Preview ground-effect assignment" }; previewGroundEffect.Click += async (_, _) => await PreviewGroundEffectAsync();
        var saveGroundEffect = new Button { Content = "Write ground-effect ADT…" }; saveGroundEffect.Click += async (_, _) => await SaveGroundEffectAsync();
        AutomationProperties.SetName(previewGroundEffect, "Preview ADT ground effect assignment"); AutomationProperties.SetName(saveGroundEffect, "Write ADT ground effect assignment");
        var reloadLiquidTypes = new Button { Content = "Reload decoded liquid types" }; reloadLiquidTypes.Click += async (_, _) => await LoadLiquidTypeCatalogAsync(force: true);
        var previewLiquid = new Button { Content = "Preview liquid edit" }; previewLiquid.Click += async (_, _) => await PreviewLiquidAsync();
        var saveLiquid = new Button { Content = "Write liquid-edited ADT…" }; saveLiquid.Click += async (_, _) => await SaveLiquidAsync();
        AutomationProperties.SetName(previewLiquid, "Preview ADT liquid edit"); AutomationProperties.SetName(saveLiquid, "Write ADT liquid edit");
        var previewNewTexture = new Button { Content = "Preview new texture layer" }; previewNewTexture.Click += async (_, _) => await PreviewTextureStructureAsync();
        var saveNewTexture = new Button { Content = "Write structurally edited ADT…" }; saveNewTexture.Click += async (_, _) => await SaveTextureStructureAsync();
        var previewAlpha = new Button { Content = "Preview alpha paint" }; previewAlpha.Click += async (_, _) => await PreviewAlphaAsync();
        var saveAlpha = new Button { Content = "Write alpha-painted ADT…" }; saveAlpha.Click += async (_, _) => await SaveAlphaAsync();
        _grid.TerrainPointSelected += (_, point) => { _brushCenterX.Text = point.X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture); _brushCenterY.Text = point.Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture); _alphaPlan = null; UpdateBrushOverlay(); };
        _mapScene.StatusChanged += (_, status) => _sceneStatus.Text = status;
        _mapScene.TerrainPicked += (_, pick) => UseScenePlacementPick(pick);
        _mapScene.PlacementPreviewChanged += (_, preview) => UseScenePlacementPreviewChange(preview);
        _brushCenterX.TextChanged += (_, _) => BrushInputChanged(); _brushCenterY.TextChanged += (_, _) => BrushInputChanged(); _brushRadius.TextChanged += (_, _) => BrushInputChanged(); _brushStrength.TextChanged += (_, _) => _brushPlan = null; _brushMode.SelectionChanged += (_, _) => _brushPlan = null; _brushFalloff.SelectionChanged += (_, _) => _brushPlan = null; _brushTarget.TextChanged += (_, _) => _brushPlan = null; _brushSeed.TextChanged += (_, _) => _brushPlan = null;
        _heightDelta.TextChanged += (_, _) => _heightPlan = null;
        _textureLayerSlot.TextChanged += (_, _) => InvalidateTexturePreview(); _textureChoice.SelectionChanged += (_, _) => InvalidateTexturePreview();
        _groundEffectLayerSlot.TextChanged += (_, _) => InvalidateGroundEffectPreview(); _groundEffectId.TextChanged += (_, _) => InvalidateGroundEffectPreview(); _groundEffectSearch.TextChanged += (_, _) => FilterGroundEffects();
        _groundEffectChoice.SelectionChanged += (_, _) => { if (_updatingGroundEffectChoices || _groundEffectChoice.SelectedItem is not GroundEffectChoice choice) return; _groundEffectId.Text = choice.Id.ToString(System.Globalization.CultureInfo.InvariantCulture); _groundEffectInfo.Text = choice.ToString(); };
        _liquidLayerSlot.TextChanged += (_, _) => InvalidateLiquidPreview(); _liquidTypeId.TextChanged += (_, _) => InvalidateLiquidPreview(); _liquidTypeSearch.TextChanged += (_, _) => FilterLiquidTypes(); _liquidHeightMode.SelectionChanged += (_, _) => InvalidateLiquidPreview(); _liquidHeightValue.TextChanged += (_, _) => InvalidateLiquidPreview();
        _liquidTypeChoice.SelectionChanged += (_, _) => { if (_updatingLiquidTypeChoices || _liquidTypeChoice.SelectedItem is not LiquidTypeChoice choice) return; _liquidTypeId.Text = choice.Id.ToString(System.Globalization.CultureInfo.InvariantCulture); _liquidInfo.Text = choice.ToString(); };
        _newTexturePath.TextChanged += (_, _) => InvalidateTextureStructurePreview(); _newTextureEncoding.SelectionChanged += (_, _) => InvalidateTextureStructurePreview(); _newTextureInitialAlpha.TextChanged += (_, _) => InvalidateTextureStructurePreview();
        _alphaLayerSlot.TextChanged += (_, _) => InvalidateAlphaPreview(); _alphaTarget.TextChanged += (_, _) => InvalidateAlphaPreview(); _alphaOpacity.TextChanged += (_, _) => InvalidateAlphaPreview(); _alphaFalloff.SelectionChanged += (_, _) => InvalidateAlphaPreview(); _alphaRestrict.IsCheckedChanged += (_, _) => InvalidateAlphaPreview();

        var heading = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10, Margin = new Thickness(12, 8) };
        heading.Children.Add(back);
        var title = new StackPanel { Spacing = 2, Children = { new TextBlock { Text = "MAP & WORLD · WOTLK TERRAIN", FontSize = 18, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = "ADT terrain chunks · WDT world tiles · WDL horizon heights", Foreground = Brush.Parse("#8E99AD") } } };
        heading.Children.Add(title); Grid.SetColumn(title, 1);

        open.Margin = new Thickness(0, 0, 6, 0);
        var controls = new StackPanel { Margin = new Thickness(12, 8), Spacing = 8, Children = { _path, new WrapPanel { Children = { open, inspect, createWdt } } } };
        var drop = new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(1), Background = Brush.Parse("#090D14"), Child = _grid };
        DragDrop.SetAllowDrop(drop, true);
        DragDrop.AddDragOverHandler(drop, (_, args) => { args.DragEffects = args.DataTransfer.TryGetFiles()?.Any(file => IsMap(file.TryGetLocalPath())) == true ? DragDropEffects.Copy : DragDropEffects.None; args.Handled = true; });
        DragDrop.AddDropHandler(drop, async (_, args) => { var path = args.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).FirstOrDefault(IsMap); if (path is not null) await OpenAsync(path); args.Handled = true; });

        _wdtEditor.Children.Add(Label("WDT 64×64 TERRAIN TILE TABLE")); _wdtEditor.Children.Add(new WrapPanel { Children = { selectAllWdt, selectMissing, clearWdt } }); _wdtEditor.Children.Add(new WrapPanel { Children = { previewWdtPresent, previewWdtAbsent, saveWdt } }); _wdtEditor.Children.Add(Info("WDT mode lets missing and present tiles be selected. Preview changes only MAIN presence bit 0 and preserves every other flag, async ID, chunk, and source byte. Global-WMO WDTs are refused because their MWMO/MODF lifecycle is not a terrain-grid edit."));
        _adtEditor.Children.Add(Label("ADT TERRAIN HEIGHT OFFSET")); _adtEditor.Children.Add(_heightDelta); _adtEditor.Children.Add(new WrapPanel { Children = { selectAll, clear, previewHeight, saveHeight } }); _adtEditor.Children.Add(Label("ADT VERTEX BRUSH")); _adtEditor.Children.Add(BrushFields()); _adtEditor.Children.Add(new WrapPanel { Children = { previewBrush, saveBrush } }); _adtEditor.Children.Add(Info("Click the terrain grid to place the brush center. Raise/lower uses signed strength. Flatten and smooth use its absolute value as the maximum movement per stroke. Noise uses it as amplitude; its seed makes the result exactly repeatable.")); _adtEditor.Children.Add(Label("ADT MH2O LIQUID")); _adtEditor.Children.Add(LiquidFields()); _adtEditor.Children.Add(new WrapPanel { Children = { reloadLiquidTypes, previewLiquid, saveLiquid } }); _adtEditor.Children.Add(_liquidInfo); _adtEditor.Children.Add(Info("Edits existing MH2O layers without rebuilding their visibility, fishable/deep, depth, or texture-coordinate data. Blank liquid type keeps each current ID. Keep, shift, or flatten exact height-bearing vertices; depth-only layers refuse height edits. Adding/removing water and painting masks remain a separate structural workflow.")); _adtEditor.Children.Add(Label("ADT TEXTURE LAYER")); _adtEditor.Children.Add(TextureFields()); _adtEditor.Children.Add(new WrapPanel { Children = { previewTexture, saveTexture } }); _adtEditor.Children.Add(Info("Reassigns an existing MCLY layer slot to one of this ADT's existing MTEX textures.")); _adtEditor.Children.Add(Label("ADT GROUND-EFFECT DOODADS")); _adtEditor.Children.Add(GroundEffectFields()); _adtEditor.Children.Add(new WrapPanel { Children = { reloadGroundEffects, previewGroundEffect, saveGroundEffect } }); _adtEditor.Children.Add(_groundEffectInfo); _adtEditor.Children.Add(Info("Assigns the exact signed MCLY effect ID for the selected layer without changing its terrain texture, flags, or alpha map. -1 removes ground doodads. Decoded choices come from the configured build-12340 GroundEffectTexture and GroundEffectDoodad tables; raw IDs remain available for advanced custom DBC workflows.")); _adtEditor.Children.Add(Label("ADD MTEX + MCLY LAYER")); _adtEditor.Children.Add(NewTextureFields()); _adtEditor.Children.Add(new WrapPanel { Children = { previewNewTexture, saveNewTexture } }); _adtEditor.Children.Add(Info("Appends one client-relative BLP to MTEX and adds a matching MCLY/MCAL layer to every selected cell. Wrath's four-layer limit is enforced. Auto preserves the tile's existing packed-versus-8-bit alpha family; mixed or evidence-free tiles require an explicit encoding.")); _adtEditor.Children.Add(Label("ADT ALPHA TEXTURE BRUSH")); _adtEditor.Children.Add(AlphaFields()); _adtEditor.Children.Add(_alphaRestrict); _adtEditor.Children.Add(new WrapPanel { Children = { previewAlpha, saveAlpha } }); _adtEditor.Children.Add(Info("Uses the click-positioned center and radius above to paint an existing additional layer toward alpha 0–255. Packed, big, and RLE maps preserve their current fixed-width encoding; an RLE result that cannot fit is refused instead of shifting MCNK offsets."));

        var details = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Margin = new Thickness(10), Spacing = 8,
                Children = { Label("FILE SUMMARY"), Card(_summary), Label("SELECTED CELL(S)"), Card(_selected), _wdtEditor, _adtEditor, Label("CHUNK TABLE"), _chunks, Label("REFERENCED CLIENT ASSETS"), _dependencies }
            }
        };
        var wmoResolver = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(7), Spacing = 6, Children = { _wmoLibrary, new WrapPanel { Children = { chooseWmoLibrary, previewReference, openWmo } }, _wmoReferences, _wmoCandidates, Label("PLACEMENT TRANSFORM & LIFECYCLE"), PlacementFields(), new WrapPanel { Children = { showPlacementInScene, previewPlacement, savePlacement, previewPlacementAdd, savePlacementAdd, previewPlacementDelete, savePlacementDelete } }, Info("Show live placement preview loads the exact selected provenance into the terrain scene and keeps position, rotation, and scale synchronized with these unsaved fields. Terrain movement snaps absolute world X/Y and recomputes exact height. Axis translation can follow world or verified local-model X/Y/Z; its drag distance comes from the visible projected axis and current zoom, with position snapping applied to signed movement increments. Rotation modes edit exact stored Euler X/Y/Z fields; Wrath supports one uniform placement scale, not invented per-axis scale. Zero disables each snap independently. One completed drag is one undo step; Ctrl+Z/Ctrl+Y work while the scene editor has focus. Transform/add/delete remain coordinated map transactions that retain every source and publish only changed ADTs beneath a tiny MPQ-ready Payload tree with manifest and receipt."), _placementStatus } } };
        var wmoFooter = new Border { Padding = new Thickness(8), Background = Brush.Parse("#101722"), Child = _wmoStatus }; Grid.SetRow(wmoFooter, 2);
        var objectPreviewHost = new Grid { Children = { _wmoPreview, _m2Preview } }; Grid.SetRow(objectPreviewHost, 1);
        var wmoPage = new Grid { RowDefinitions = new("Auto,*,Auto"), Children = { wmoResolver, objectPreviewHost, wmoFooter } };
        var sceneControls = new WrapPanel { Margin = new Thickness(7), Children = { buildScene, openPlacementControls, _sceneProvenance, _scenePlacementLimit } };
        var sceneStatusScroller = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = _sceneStatus };
        var scenePage = new Grid { RowDefinitions = new("Auto,*,Auto"), Children = { sceneControls, WithRow(_mapScene, 1), WithRow(new Border { Padding = new Thickness(8), Background = Brush.Parse("#101722"), Child = sceneStatusScroller }, 2) } };
        _visualTabs.Items.Add(new TabItem { Header = "Terrain / world grid", Content = drop }); _visualTabs.Items.Add(new TabItem { Header = "Terrain + placement scene", Content = scenePage }); _visualTabs.Items.Add(new TabItem { Header = "Selected object preview", Content = wmoPage }); _visualTabs.SelectedIndex = 0;
        var body = new ResponsiveSplitGrid(_visualTabs, details, 3, 2, compactFirstWeight: 4, compactSecondWeight: 1);
        var terrainPage = new Grid { RowDefinitions = new("Auto,*"), Children = { controls, body } }; Grid.SetRow(body, 1);
        _workspaceTabs.Items.Add(new TabItem { Header = "Terrain, objects & textures", Content = terrainPage }); _workspaceTabs.Items.Add(new TabItem { Header = "Lighting & skyboxes", Content = _lightingView }); _workspaceTabs.SelectedIndex = 0;
        var lightingLoadStarted = false;
        _workspaceTabs.SelectionChanged += async (_, _) =>
        {
            if (_workspaceTabs.SelectedIndex != 1 || lightingLoadStarted) return;
            lightingLoadStarted = true;
            await _lightingView.LoadAsync();
            if (_pendingLightingId is { } id) _lightingView.SelectLightId(id);
        };
        var root = new Grid { RowDefinitions = new("Auto,*,Auto") };
        root.Children.Add(new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 0, 0, 1), Child = heading });
        root.Children.Add(_workspaceTabs); Grid.SetRow(_workspaceTabs, 1);
        var footer = new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 7), Child = _status }; root.Children.Add(footer); Grid.SetRow(footer, 2);
        Content = root; UpdateScenePlacementSnapSettings();
    }

    public void OpenLighting(uint? lightId = null) { _pendingLightingId = lightId; _workspaceTabs.SelectedIndex = 1; if (lightId is { } id) _lightingView.SelectLightId(id); }

    public async Task OpenAsync(string? path)
    {
        if (!IsMap(path)) { _status.Text = "Choose an existing .adt, .wdt, or .wdl file."; return; }
        _operation?.Cancel(); _operation?.Dispose(); _wmoOperation?.Cancel(); _operation = new CancellationTokenSource(); var token = _operation.Token;
        try
        {
            _path.Text = Path.GetFullPath(path!); _status.Text = $"Inspecting {Path.GetFileName(path)}…";
            var loaded = await Task.Run(() => { token.ThrowIfCancellationRequested(); var inspection = MapAssetInspectionService.Inspect(path!); var textureResult = inspection.Kind == MapAssetKind.Adt ? TryInspectTextures(path!) : (Inspection: (AdtTextureLayerInspection?)null, Error: (string?)null); var alphaResult = inspection.Kind == MapAssetKind.Adt ? TryInspectAlpha(path!) : (Inspection: (AdtAlphaMapInspection?)null, Error: (string?)null); var liquidResult = inspection.Kind == MapAssetKind.Adt ? TryInspectLiquid(path!) : (Inspection: (AdtLiquidInspection?)null, Error: (string?)null); return (inspection, textureResult, alphaResult, liquidResult); }, token); var inspection = loaded.inspection;
            if (token.IsCancellationRequested) return;
            _inspection = inspection; _textureSourceInspection = _textureInspection = loaded.textureResult.Inspection; _alphaSourceInspection = _alphaInspection = loaded.alphaResult.Inspection; _liquidSourceInspection = _liquidInspection = loaded.liquidResult.Inspection; _heightPlan = null; _brushPlan = null; _texturePlan = null; _groundEffectPlan = null; _liquidPlan = null; _textureStructurePlan = null; _alphaPlan = null; _wdtPlan = null; _placementPlan = null; _placementMultiTilePlan = null; _mapScene.ClearScene(); _sceneProvenance.ItemsSource = null; _sceneStatus.Text = inspection.Kind == MapAssetKind.Adt ? "ADT loaded. Build the composed scene to discover effective-client and raw-provenance coverage." : "Terrain scene composition requires an ADT tile."; SetTextureChoices(_textureInspection); _grid.SetInspection(inspection); _grid.SetBrush(null, null, null); _summary.Text = Summary(inspection); _selected.Text = inspection.Kind == MapAssetKind.Wdt ? "Select present or missing WDT tiles. Hold Ctrl to build a sparse selection." : "Select a present grid cell for exact terrain, texture-layer, alpha-map, and liquid metadata."; _wdtEditor.IsVisible = inspection.Kind == MapAssetKind.Wdt; _adtEditor.IsVisible = inspection.Kind == MapAssetKind.Adt;
            _chunks.ItemsSource = inspection.Chunks.Select(chunk => $"{chunk.Id} · {chunk.Occurrences:N0} chunk(s) · {chunk.PayloadBytes:N0} bytes").ToArray();
            _dependencies.ItemsSource = inspection.TexturePaths.Select(value => "Texture · " + value).Concat(inspection.ModelPaths.Select(value => "Model · " + value)).Concat(inspection.WmoPaths.Select(value => "WMO · " + value)).DefaultIfEmpty("No path-list dependencies in this file.").ToArray();
            _wmoCandidates.ItemsSource = null;
            var placedWmoPaths = inspection.WmoPlacements.Where(value => value.ClientPath is not null).Select(value => value.ClientPath!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var placedM2Paths = inspection.M2Placements.Where(value => value.ClientPath is not null).Select(value => value.ClientPath!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var wmoReferences = inspection.WmoPlacements.Select(value => new MapObjectReferenceChoice(value.ClientPath, value, null))
                .Concat(inspection.M2Placements.Select(value => new MapObjectReferenceChoice(value.ClientPath, null, value)))
                .Concat(inspection.WmoPaths.Where(value => !placedWmoPaths.Contains(value)).Select(value => new MapObjectReferenceChoice(value, null, null)))
                .Concat(inspection.ModelPaths.Where(value => !placedM2Paths.Contains(value)).Select(value => new MapObjectReferenceChoice(value, null, null))).ToArray();
            _wmoReferences.ItemsSource = wmoReferences; _wmoReferences.SelectedIndex = wmoReferences.Length > 0 ? 0 : -1;
            if (wmoReferences.Length == 0) _wmoStatus.Text = "This map contains no path-listed or placed WMO/M2 objects.";
            _status.Text = $"Loaded {inspection.Kind} · {inspection.PresentCells:N0}/{inspection.Cells.Count:N0} present cells · click a cell for details · drop another map file anywhere on the grid" + (loaded.textureResult.Error is null ? string.Empty : $" · texture layers unavailable: {loaded.textureResult.Error}") + (loaded.alphaResult.Error is null ? string.Empty : $" · alpha maps unavailable: {loaded.alphaResult.Error}") + (loaded.liquidResult.Error is null ? string.Empty : $" · liquid unavailable: {loaded.liquidResult.Error}");
            if (inspection.Kind == MapAssetKind.Adt) { await LoadGroundEffectCatalogAsync(force: false); await LoadLiquidTypeCatalogAsync(force: false); UpdateLiquidInfo(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _grid.SetInspection(null); _wdtEditor.IsVisible = false; _adtEditor.IsVisible = false; _summary.Text = "Inspection failed."; _status.Text = exception.Message; DesktopCrashLogger.Log("Map inspection failed", exception); }
    }

    private async Task CreateWdtAsync()
    {
        try
        {
            var top = TopLevel.GetTopLevel(this); if (top is null) return;
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Create a new empty WotLK terrain WDT", SuggestedFileName = "NewMap.wdt", DefaultExtension = "wdt", FileTypeChoices = [new FilePickerFileType("WoW WDT") { Patterns = ["*.wdt"] }] });
            var output = file?.TryGetLocalPath(); if (output is null) return; _status.Text = "Creating and validating an empty 64×64 terrain WDT…";
            var result = await Task.Run(() => WdtTileTableService.Create(output, [], overwrite: false)); await OpenAsync(result.OutputPath);
            _status.Text = $"Created an empty MVER 18 terrain WDT atomically · receipt {Path.GetFileName(result.ReceiptPath)} · select missing tiles and preview them as present to author its MAIN table";
        }
        catch (Exception exception) { _status.Text = exception.Message; DesktopCrashLogger.Log("WDT creation failed", exception); }
    }

    private async Task PreviewWdtAsync(bool present)
    {
        try
        {
            if (_inspection?.Kind != MapAssetKind.Wdt) throw new InvalidOperationException("WDT tile-table editing requires a loaded WDT file.");
            var selected = _grid.SelectedCells.Select(cell => (cell.X, cell.Y)).ToArray(); _status.Text = $"Planning {selected.Length:N0} WDT tile state review(s)…";
            var plan = await Task.Run(() => WdtTileTableService.Plan(_inspection.Path, selected, present)); var preview = await Task.Run(() => WdtTileTableService.Preview(plan));
            _grid.SetInspection(preview, plan.Edits.Select(edit => (edit.X, edit.Y))); _wdtPlan = plan; _summary.Text = Summary(preview); _selected.Text = DescribeSelection(_grid.SelectedCells);
            _status.Text = $"Preview only · {plan.Edits.Count:N0} tile(s) become {(present ? "present" : "absent")} · {plan.AlreadyDesiredCells:N0} already in that state · every non-presence flag/async value and the source file remain unchanged";
        }
        catch (Exception exception) { _status.Text = exception.Message; DesktopCrashLogger.Log("WDT tile-table preview failed", exception); }
    }

    private async Task SaveWdtAsync()
    {
        try
        {
            if (_wdtPlan is null) throw new InvalidOperationException("Preview selected WDT tiles as present or absent before writing the reviewed change.");
            var top = TopLevel.GetTopLevel(this); if (top is null) return; var stem = Path.GetFileNameWithoutExtension(_wdtPlan.InputPath);
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Write a separate tile-edited WDT", SuggestedFileName = stem + "-tiles.wdt", DefaultExtension = "wdt", FileTypeChoices = [new FilePickerFileType("WoW WDT") { Patterns = ["*.wdt"] }] });
            var output = file?.TryGetLocalPath(); if (output is null) return; _status.Text = "Writing and re-validating the exact WDT MAIN tile slots…";
            var result = await Task.Run(() => WdtTileTableService.Apply(_wdtPlan, output, overwrite: false)); await OpenAsync(result.OutputPath);
            _status.Text = $"Wrote {result.EditedCells:N0} WDT tile state edit(s) atomically · receipt {Path.GetFileName(result.ReceiptPath)} · original WDT retained";
        }
        catch (Exception exception) { _status.Text = exception.Message; DesktopCrashLogger.Log("WDT tile-table write failed", exception); }
    }

    private async Task PreviewHeightAsync()
    {
        try
        {
            if (_inspection?.Kind != MapAssetKind.Adt) throw new InvalidOperationException("Terrain-height editing requires an ADT file.");
            if (!float.TryParse(_heightDelta.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var delta) || !float.IsFinite(delta)) throw new InvalidOperationException("Enter a finite height delta using a period as the decimal separator.");
            var selected = _grid.SelectedCells.Where(cell => cell.Present).Select(cell => (cell.X, cell.Y)).ToArray();
            _status.Text = $"Planning {selected.Length:N0} terrain-cell height edit(s)…"; var plan = await Task.Run(() => AdtHeightEditService.Plan(_inspection.Path, selected, delta)); var preview = await Task.Run(() => AdtHeightEditService.Preview(plan));
            _brushPlan = null; _groundEffectPlan = null; _liquidPlan = null; _liquidInspection = _liquidSourceInspection; _textureInspection = _textureSourceInspection; _textureStructurePlan = null; _grid.SetInspection(preview, plan.Cells.Select(cell => (cell.X, cell.Y))); _heightPlan = plan; _summary.Text = Summary(preview); _status.Text = $"Preview only · {plan.Cells.Count:N0} cell(s) offset by {plan.Delta:R} · source bytes unchanged";
        }
        catch (Exception exception) { _status.Text = exception.Message; DesktopCrashLogger.Log("ADT height preview failed", exception); }
    }

    private async Task PreviewBrushAsync()
    {
        try
        {
            if (_inspection?.Kind != MapAssetKind.Adt) throw new InvalidOperationException("Terrain brushing requires an ADT file.");
            var centerX = Number(_brushCenterX, "brush center X"); var centerY = Number(_brushCenterY, "brush center Y"); var radius = Number(_brushRadius, "brush radius"); var strength = Number(_brushStrength, "brush strength");
            var mode = _brushMode.SelectedItem is MapBrushModeChoice selectedMode ? selectedMode.Value : AdtTerrainBrushMode.RaiseLower; var falloff = _brushFalloff.SelectedItem is AdtTerrainBrushFalloff selectedFalloff ? selectedFalloff : AdtTerrainBrushFalloff.Smooth;
            float? target = mode == AdtTerrainBrushMode.Flatten ? Number(_brushTarget, "flatten target height") : null; var seed = 0; if (mode == AdtTerrainBrushMode.Noise && !int.TryParse(_brushSeed.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out seed)) throw new InvalidOperationException("Enter a signed 32-bit integer noise seed."); _status.Text = $"Planning exact {mode} MCVT vertex edits…";
            var plan = await Task.Run(() => AdtTerrainBrushService.Plan(_inspection.Path, centerX, centerY, radius, strength, falloff, mode, target, seed)); var preview = await Task.Run(() => AdtTerrainBrushService.Preview(plan));
            _brushPlan = plan; _heightPlan = null; _groundEffectPlan = null; _liquidPlan = null; _liquidInspection = _liquidSourceInspection; _textureInspection = _textureSourceInspection; _textureStructurePlan = null; _grid.SetInspection(preview, plan.Vertices.Select(vertex => (vertex.CellX, vertex.CellY)).Distinct()); _grid.SetBrush(plan.CenterX, plan.CenterY, plan.Radius); _summary.Text = Summary(preview);
            _status.Text = $"Preview only · {plan.Mode} · {plan.Vertices.Count:N0} vertex edits across {plan.Vertices.Select(vertex => (vertex.CellX, vertex.CellY)).Distinct().Count():N0} cell(s) · {plan.Falloff} falloff · source bytes unchanged";
        }
        catch (Exception exception) { _status.Text = exception.Message; DesktopCrashLogger.Log("ADT terrain brush preview failed", exception); }
    }

    private async Task SaveBrushAsync()
    {
        try
        {
            if (_brushPlan is null) { await PreviewBrushAsync(); if (_brushPlan is null) return; }
            var top = TopLevel.GetTopLevel(this); if (top is null) return; var stem = Path.GetFileNameWithoutExtension(_brushPlan.InputPath);
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Write a separate vertex-brushed ADT", SuggestedFileName = stem + "-brush.adt", DefaultExtension = "adt", FileTypeChoices = [new FilePickerFileType("WoW ADT") { Patterns = ["*.adt"] }] });
            var output = file?.TryGetLocalPath(); if (output is null) return; _status.Text = "Writing and re-validating every affected terrain cell…"; var saved = await Task.Run(() => { var result = AdtTerrainBrushService.Apply(_brushPlan, output, overwrite: false); return (result, textureResult: TryInspectTextures(result.OutputPath), alphaResult: TryInspectAlpha(result.OutputPath), liquidResult: TryInspectLiquid(result.OutputPath)); }); var result = saved.result;
            _path.Text = result.OutputPath; _inspection = result.Inspection; _textureSourceInspection = _textureInspection = saved.textureResult.Inspection; _alphaSourceInspection = _alphaInspection = saved.alphaResult.Inspection; _liquidSourceInspection = _liquidInspection = saved.liquidResult.Inspection; SetTextureChoices(_textureInspection); _brushPlan = null; _texturePlan = null; _groundEffectPlan = null; _liquidPlan = null; _textureStructurePlan = null; _alphaPlan = null; _grid.SetInspection(result.Inspection); _grid.SetBrush(null, null, null); _summary.Text = Summary(result.Inspection); UpdateLiquidInfo();
            _status.Text = $"Wrote {result.EditedVertices:N0} vertex edit(s) across {result.EditedCells:N0} cell(s) atomically · receipt {Path.GetFileName(result.ReceiptPath)} · original retained" + (saved.textureResult.Error is null ? string.Empty : $" · texture layers unavailable: {saved.textureResult.Error}") + (saved.alphaResult.Error is null ? string.Empty : $" · alpha maps unavailable: {saved.alphaResult.Error}") + (saved.liquidResult.Error is null ? string.Empty : $" · liquid unavailable: {saved.liquidResult.Error}");
        }
        catch (Exception exception) { _status.Text = exception.Message; DesktopCrashLogger.Log("ADT terrain brush write failed", exception); }
    }

    private Control BrushFields()
    {
        var position = new Grid { ColumnDefinitions = new("*,*,*,*"), ColumnSpacing = 6 }; var positionValues = new[] { Field("CENTER X", _brushCenterX), Field("CENTER Y", _brushCenterY), Field("RADIUS", _brushRadius), Field("STRENGTH", _brushStrength) };
        for (var index = 0; index < positionValues.Length; index++) { position.Children.Add(positionValues[index]); Grid.SetColumn(positionValues[index], index); }
        var behavior = new Grid { ColumnDefinitions = new("*,*,*,*"), ColumnSpacing = 6 }; var behaviorValues = new[] { Field("MODE", _brushMode), Field("FALLOFF", _brushFalloff), Field("FLATTEN TARGET", _brushTarget), Field("NOISE SEED", _brushSeed) };
        for (var index = 0; index < behaviorValues.Length; index++) { behavior.Children.Add(behaviorValues[index]); Grid.SetColumn(behaviorValues[index], index); }
        return new StackPanel { Spacing = 6, Children = { position, behavior } };
    }

    private Control TextureFields()
    {
        var fields = new Grid { ColumnDefinitions = new("*,3*"), ColumnSpacing = 6 }; var slot = Field("LAYER SLOT", _textureLayerSlot); var texture = Field("EXISTING MTEX TEXTURE", _textureChoice); fields.Children.Add(slot); fields.Children.Add(texture); Grid.SetColumn(texture, 1); return fields;
    }

    private Control GroundEffectFields() => new ResponsiveFieldGrid(("LAYER SLOT", _groundEffectLayerSlot), ("RAW EFFECT ID", _groundEffectId), ("SEARCH DECODED EFFECTS", _groundEffectSearch), ("DECODED RESULT", _groundEffectChoice));

    private Control LiquidFields() => new ResponsiveFieldGrid(("LAYER SLOT", _liquidLayerSlot), ("LIQUID TYPE ID · BLANK KEEPS", _liquidTypeId), ("SEARCH DECODED TYPES", _liquidTypeSearch), ("DECODED RESULT", _liquidTypeChoice), ("HEIGHT OPERATION", _liquidHeightMode), ("DELTA / FLAT HEIGHT", _liquidHeightValue));

    private async Task LoadLiquidTypeCatalogAsync(bool force)
    {
        var root = _session.Settings.CoreDbcPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) { _liquidTypeCatalog = null; FilterLiquidTypes(); UpdateLiquidInfo("Decoded liquid types are unavailable because no existing server DBC folder is configured. Raw unsigned IDs remain editable."); return; }
        try
        {
            root = Path.GetFullPath(root); _liquidInfo.Text = "Loading exact build-12340 LiquidType.dbc…"; if (force || _liquidTypeCatalog is null || !_liquidTypeCatalog.DbcDirectory.Equals(root, StringComparison.OrdinalIgnoreCase)) _liquidTypeCatalog = await Task.Run(() => LiquidTypeCatalogService.Load(root)); FilterLiquidTypes(); UpdateLiquidInfo();
        }
        catch (Exception exception) { _liquidTypeCatalog = null; FilterLiquidTypes(); UpdateLiquidInfo($"Decoded LiquidType catalog unavailable: {exception.Message} Raw unsigned IDs remain editable."); DesktopCrashLogger.Log("LiquidType DBC catalog load failed", exception); }
    }

    private void FilterLiquidTypes()
    {
        _updatingLiquidTypeChoices = true;
        try
        {
            var choices = new List<LiquidTypeChoice>(); var catalog = _liquidTypeCatalog; if (catalog is not null) { choices.AddRange(LiquidTypeCatalogService.Search(catalog, _liquidTypeSearch.Text, 256).Where(value => value.Id <= ushort.MaxValue).Select(value => new LiquidTypeChoice((ushort)value.Id, value))); if (ushort.TryParse(_liquidTypeId.Text, out var current) && !choices.Any(choice => choice.Id == current)) { var exact = catalog.Types.FirstOrDefault(value => value.Id == current); if (exact is not null) choices.Insert(0, new(current, exact)); } } ushort? selectedId = ushort.TryParse(_liquidTypeId.Text, out var id) ? id : null; _liquidTypeChoice.ItemsSource = choices; _liquidTypeChoice.SelectedItem = selectedId is null ? null : choices.FirstOrDefault(choice => choice.Id == selectedId.Value);
        }
        finally { _updatingLiquidTypeChoices = false; }
    }

    private void UpdateLiquidInfo(string? prefix = null)
    {
        var inspection = _liquidInspection; var catalog = _liquidTypeCatalog; var source = prefix is null ? string.Empty : prefix + " "; if (inspection is null) { _liquidInfo.Text = source + "No valid MH2O inspection is loaded."; return; } if (inspection.Layers.Count == 0) { _liquidInfo.Text = source + (inspection.Findings.FirstOrDefault() ?? "This ADT has no liquid layers.") + " Existing-layer editing is unavailable; structural water creation is not implemented yet."; return; }
        _liquidInfo.Text = source + $"Decoded {inspection.Layers.Count:N0} MH2O layer(s) across {inspection.Layers.Select(value => (value.CellX, value.CellY)).Distinct().Count():N0} cell(s): " + string.Join(", ", inspection.Layers.GroupBy(value => value.VertexFormat).OrderBy(value => value.Key).Select(group => $"{group.Key} {group.Count():N0}")) + (catalog is null ? " · raw LiquidType IDs" : $" · {catalog.Types.Count:N0} LiquidType records from {catalog.DbcDirectory}") + (inspection.Findings.Count == 0 ? string.Empty : $" · {inspection.Findings.Count:N0} visible finding(s)");
    }

    private async Task PreviewLiquidAsync()
    {
        try
        {
            if (_inspection?.Kind != MapAssetKind.Adt || _liquidSourceInspection is null || _liquidSourceInspection.Layers.Count == 0) throw new InvalidOperationException("Liquid editing requires an ADT containing a valid MH2O layer table."); if (!int.TryParse(_liquidLayerSlot.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var slot) || slot < 0) throw new InvalidOperationException("Liquid layer slot must be a non-negative integer."); ushort? targetType = null; if (!string.IsNullOrWhiteSpace(_liquidTypeId.Text)) { if (!ushort.TryParse(_liquidTypeId.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedType)) throw new InvalidOperationException("Liquid type must be an unsigned 16-bit LiquidType ID, or blank to retain each existing type."); targetType = parsedType; } var mode = _liquidHeightMode.SelectedItem is LiquidHeightModeChoice selectedMode ? selectedMode.Mode : AdtLiquidHeightMode.Keep; var value = Number(_liquidHeightValue, "liquid height value");
            var selected = _grid.SelectedCells.Select(cell => (cell.X, cell.Y)).ToArray(); var cells = selected.Where(cell => _liquidSourceInspection.Layers.Any(layer => layer.CellX == cell.X && layer.CellY == cell.Y && layer.Slot == slot)).ToArray(); var skipped = selected.Length - cells.Length; var verifiedCatalog = targetType is not null && _liquidTypeCatalog?.Types.Any(type => type.Id == targetType.Value) == true ? _liquidTypeCatalog : null; _status.Text = $"Planning MH2O layer {slot} edit across {cells.Length:N0} compatible selected cell(s)…";
            var plan = await Task.Run(() => AdtLiquidService.Plan(_inspection.Path, cells, slot, targetType, mode, value, verifiedCatalog)); var preview = await Task.Run(() => AdtLiquidService.Preview(plan)); _liquidPlan = plan; _liquidInspection = preview; _heightPlan = null; _brushPlan = null; _texturePlan = null; _groundEffectPlan = null; _textureStructurePlan = null; _alphaPlan = null; _textureInspection = _textureSourceInspection; _alphaInspection = _alphaSourceInspection; _grid.SetInspection(_inspection, plan.Edits.Select(edit => (edit.CellX, edit.CellY)).Distinct()); _selected.Text = DescribeSelection(_grid.SelectedCells); UpdateLiquidInfo();
            _status.Text = $"Preview only · {plan.Edits.Count:N0} liquid layer(s) · {(plan.TargetLiquidType is null ? "retain type" : $"type {plan.TargetLiquidType:N0}")} · {plan.HeightMode} {plan.HeightValue:R} · {plan.Edits.Sum(edit => edit.VertexHeights.Count):N0} vertex height(s) · source bytes unchanged" + (skipped == 0 ? string.Empty : $" · skipped {skipped:N0} selected cell(s) without slot {slot}");
        }
        catch (Exception exception) { _status.Text = exception.Message; DesktopCrashLogger.Log("ADT liquid preview failed", exception); }
    }

    private async Task SaveLiquidAsync()
    {
        try
        {
            if (_liquidPlan is null) { await PreviewLiquidAsync(); if (_liquidPlan is null) return; } var top = TopLevel.GetTopLevel(this); if (top is null) return; var stem = Path.GetFileNameWithoutExtension(_liquidPlan.InputPath); var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Write a separate liquid-edited ADT", SuggestedFileName = stem + "-liquid.adt", DefaultExtension = "adt", FileTypeChoices = [new FilePickerFileType("WoW ADT") { Patterns = ["*.adt"] }] }); var output = file?.TryGetLocalPath(); if (output is null) return; _status.Text = "Writing and re-validating exact MH2O fields…"; var result = await Task.Run(() => AdtLiquidService.Apply(_liquidPlan, output, overwrite: false)); await OpenAsync(result.OutputPath); _status.Text = $"Wrote {result.EditedLayers:N0} liquid layer edit(s) across {result.EditedCells:N0} cell(s) and {result.EditedVertexHeights:N0} vertex height(s) atomically · receipt {Path.GetFileName(result.ReceiptPath)} · original retained";
        }
        catch (Exception exception) { _status.Text = exception.Message; DesktopCrashLogger.Log("ADT liquid write failed", exception); }
    }

    private async Task LoadGroundEffectCatalogAsync(bool force)
    {
        var root = _session.Settings.CoreDbcPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            _groundEffectCatalog = null; FilterGroundEffects(); _groundEffectInfo.Text = "Decoded ground effects are unavailable because no existing server DBC folder is configured. Raw -1/non-negative IDs remain editable."; return;
        }
        try
        {
            root = Path.GetFullPath(root); _groundEffectInfo.Text = "Loading exact build-12340 GroundEffectTexture and GroundEffectDoodad tables…";
            if (force || _groundEffectCatalog is null || !_groundEffectCatalog.DbcDirectory.Equals(root, StringComparison.OrdinalIgnoreCase)) _groundEffectCatalog = await Task.Run(() => GroundEffectCatalogService.Load(root));
            FilterGroundEffects(); var catalog = _groundEffectCatalog;
            _groundEffectInfo.Text = $"Decoded {catalog.Effects.Count:N0} ground effects and {catalog.Doodads.Count:N0} doodad models from {catalog.DbcDirectory}. Showing up to 256 search results." + (catalog.Findings.Count == 0 ? string.Empty : $" {catalog.Findings.Count:N0} broken DBC reference(s) remain visible in CLI evidence.");
        }
        catch (Exception exception) { _groundEffectCatalog = null; FilterGroundEffects(); _groundEffectInfo.Text = $"Decoded ground-effect catalog unavailable: {exception.Message} Raw IDs remain editable."; DesktopCrashLogger.Log("Ground-effect DBC catalog load failed", exception); }
    }

    private void FilterGroundEffects()
    {
        _updatingGroundEffectChoices = true;
        try
        {
            var choices = new List<GroundEffectChoice> { new(-1, null) }; var catalog = _groundEffectCatalog;
            if (catalog is not null)
            {
                var matches = GroundEffectCatalogService.Search(catalog, _groundEffectSearch.Text, 256); choices.AddRange(matches.Where(effect => effect.Id <= int.MaxValue).Select(effect => new GroundEffectChoice((int)effect.Id, effect)));
                if (int.TryParse(_groundEffectId.Text, out var current) && current >= 0 && !choices.Any(choice => choice.Id == current)) { var exact = catalog.Effects.FirstOrDefault(effect => effect.Id == (uint)current); if (exact is not null) choices.Insert(1, new(current, exact)); }
            }
            var selectedId = int.TryParse(_groundEffectId.Text, out var value) ? value : int.MinValue; _groundEffectChoice.ItemsSource = choices; _groundEffectChoice.SelectedItem = choices.FirstOrDefault(choice => choice.Id == selectedId);
        }
        finally { _updatingGroundEffectChoices = false; }
    }

    private async Task PreviewGroundEffectAsync()
    {
        try
        {
            if (_inspection?.Kind != MapAssetKind.Adt || _textureSourceInspection is null) throw new InvalidOperationException("Ground-effect editing requires an ADT with a valid MCLY texture-layer table.");
            if (!int.TryParse(_groundEffectLayerSlot.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var slot) || slot < 0) throw new InvalidOperationException("Ground-effect layer slot must be a non-negative integer.");
            if (!int.TryParse(_groundEffectId.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var effectId) || effectId < -1) throw new InvalidOperationException("Ground-effect ID must be -1 (none) or a non-negative GroundEffectTexture ID.");
            var selected = _grid.SelectedCells.Select(cell => (cell.X, cell.Y)).ToArray(); var cells = selected.Where(cell => _textureSourceInspection.Layers.Any(layer => layer.CellX == cell.X && layer.CellY == cell.Y && layer.Slot == slot)).ToArray(); var skipped = selected.Length - cells.Length;
            var verifiedCatalog = effectId >= 0 && _groundEffectCatalog?.Effects.Any(effect => effect.Id == (uint)effectId) == true ? _groundEffectCatalog : null; _status.Text = $"Planning MCLY layer {slot} ground-effect assignment across {cells.Length:N0} compatible selected cell(s)…";
            var plan = await Task.Run(() => AdtGroundEffectService.Plan(_inspection.Path, cells, slot, effectId, verifiedCatalog)); var preview = await Task.Run(() => AdtGroundEffectService.Preview(plan));
            _groundEffectPlan = plan; _textureInspection = preview; _texturePlan = null; _liquidPlan = null; _liquidInspection = _liquidSourceInspection; _textureStructurePlan = null; _alphaPlan = null; _alphaInspection = _alphaSourceInspection; _heightPlan = null; _brushPlan = null; _grid.SetInspection(_inspection, plan.Edits.Select(edit => (edit.CellX, edit.CellY)).Distinct()); _selected.Text = DescribeSelection(_grid.SelectedCells);
            _status.Text = $"Preview only · {plan.Edits.Count:N0} layer(s) → ground effect {plan.EffectId:N0} · {(plan.CatalogEvidence ?? "raw custom ID; no matching configured DBC record")} · source bytes unchanged" + (skipped == 0 ? string.Empty : $" · skipped {skipped:N0} selected cell(s) without slot {slot}");
        }
        catch (Exception exception) { _status.Text = exception.Message; DesktopCrashLogger.Log("ADT ground-effect preview failed", exception); }
    }

    private async Task SaveGroundEffectAsync()
    {
        try
        {
            if (_groundEffectPlan is null) { await PreviewGroundEffectAsync(); if (_groundEffectPlan is null) return; } var top = TopLevel.GetTopLevel(this); if (top is null) return; var stem = Path.GetFileNameWithoutExtension(_groundEffectPlan.InputPath);
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Write a separate ground-effect-edited ADT", SuggestedFileName = stem + "-ground-effect.adt", DefaultExtension = "adt", FileTypeChoices = [new FilePickerFileType("WoW ADT") { Patterns = ["*.adt"] }] }); var output = file?.TryGetLocalPath(); if (output is null) return; _status.Text = "Writing and re-validating exact MCLY ground-effect fields…";
            var result = await Task.Run(() => AdtGroundEffectService.Apply(_groundEffectPlan, output, overwrite: false)); await OpenAsync(result.OutputPath); _status.Text = $"Wrote {result.EditedLayers:N0} ground-effect edit(s) across {result.EditedCells:N0} cell(s) atomically · receipt {Path.GetFileName(result.ReceiptPath)} · original retained";
        }
        catch (Exception exception) { _status.Text = exception.Message; DesktopCrashLogger.Log("ADT ground-effect write failed", exception); }
    }

    private Control NewTextureFields()
    {
        var fields = new Grid { ColumnDefinitions = new("3*,*,*"), ColumnSpacing = 6 }; var values = new[] { Field("NEW CLIENT BLP PATH", _newTexturePath), Field("ALPHA ENCODING", _newTextureEncoding), Field("INITIAL ALPHA", _newTextureInitialAlpha) };
        for (var index = 0; index < values.Length; index++) { fields.Children.Add(values[index]); Grid.SetColumn(values[index], index); }
        return fields;
    }

    private Control AlphaFields()
    {
        var fields = new Grid { ColumnDefinitions = new("*,*,*,*"), ColumnSpacing = 6 }; var values = new[] { Field("LAYER SLOT", _alphaLayerSlot), Field("TARGET ALPHA", _alphaTarget), Field("OPACITY", _alphaOpacity), Field("FALLOFF", _alphaFalloff) };
        for (var index = 0; index < values.Length; index++) { fields.Children.Add(values[index]); Grid.SetColumn(values[index], index); }
        return fields;
    }

    private async Task PreviewAlphaAsync()
    {
        try
        {
            if (_inspection?.Kind != MapAssetKind.Adt || _alphaSourceInspection is null) throw new InvalidOperationException("Alpha painting requires an ADT with a valid fixed-width MCAL layout.");
            if (!int.TryParse(_alphaLayerSlot.Text, out var slot) || slot <= 0) throw new InvalidOperationException("Enter an additional texture layer slot greater than zero; slot 0 is the opaque base layer.");
            if (!byte.TryParse(_alphaTarget.Text, out var target)) throw new InvalidOperationException("Target alpha must be an integer from 0 through 255.");
            var centerX = Number(_brushCenterX, "alpha-brush center X"); var centerY = Number(_brushCenterY, "alpha-brush center Y"); var radius = Number(_brushRadius, "alpha-brush radius"); var opacity = Number(_alphaOpacity, "alpha-brush opacity"); var falloff = _alphaFalloff.SelectedItem is AdtTerrainBrushFalloff selectedFalloff ? selectedFalloff : AdtTerrainBrushFalloff.Smooth;
            IReadOnlyList<(int X, int Y)>? cells = _alphaRestrict.IsChecked == true ? _grid.SelectedCells.Select(cell => (cell.X, cell.Y)).ToArray() : null;
            if (_alphaRestrict.IsChecked == true && cells is { Count: 0 }) throw new InvalidOperationException("Select at least one present cell or turn off the selected-cell restriction.");
            _status.Text = $"Planning fixed-width layer {slot} alpha paint…"; var plan = await Task.Run(() => AdtAlphaMapService.Plan(_inspection.Path, slot, centerX, centerY, radius, target, opacity, falloff, cells)); var preview = await Task.Run(() => AdtAlphaMapService.Preview(plan));
            _alphaPlan = plan; _alphaInspection = preview; _heightPlan = null; _brushPlan = null; _texturePlan = null; _groundEffectPlan = null; _liquidPlan = null; _liquidInspection = _liquidSourceInspection; _textureStructurePlan = null; _textureInspection = _textureSourceInspection; _grid.SetInspection(_inspection, plan.Edits.Select(edit => (edit.CellX, edit.CellY)).Distinct()); _grid.SetBrush(plan.CenterX, plan.CenterY, plan.Radius); _selected.Text = DescribeSelection(_grid.SelectedCells);
            _status.Text = $"Preview only · {plan.Edits.Sum(edit => edit.ChangedPixels):N0} stored alpha pixel(s) across {plan.Edits.Count:N0} map(s) · layer {plan.LayerSlot} toward {plan.TargetAlpha} at {plan.Opacity:0.###} opacity · source bytes unchanged";
        }
        catch (Exception exception) { _status.Text = exception.Message; DesktopCrashLogger.Log("ADT alpha-brush preview failed", exception); }
    }

    private async Task SaveAlphaAsync()
    {
        try
        {
            if (_alphaPlan is null) { await PreviewAlphaAsync(); if (_alphaPlan is null) return; } var top = TopLevel.GetTopLevel(this); if (top is null) return; var stem = Path.GetFileNameWithoutExtension(_alphaPlan.InputPath);
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Write a separate alpha-painted ADT", SuggestedFileName = stem + "-alpha.adt", DefaultExtension = "adt", FileTypeChoices = [new FilePickerFileType("WoW ADT") { Patterns = ["*.adt"] }] }); var output = file?.TryGetLocalPath(); if (output is null) return; _status.Text = "Writing and re-validating every painted MCAL map…";
            var saved = await Task.Run(() => { var result = AdtAlphaMapService.Apply(_alphaPlan, output, overwrite: false); return (result, map: MapAssetInspectionService.Inspect(result.OutputPath), textures: TryInspectTextures(result.OutputPath), liquidResult: TryInspectLiquid(result.OutputPath)); });
            _path.Text = saved.result.OutputPath; _inspection = saved.map; _textureSourceInspection = _textureInspection = saved.textures.Inspection; _alphaSourceInspection = _alphaInspection = saved.result.Inspection; _liquidSourceInspection = _liquidInspection = saved.liquidResult.Inspection; _alphaPlan = null; _heightPlan = null; _brushPlan = null; _texturePlan = null; _groundEffectPlan = null; _liquidPlan = null; _textureStructurePlan = null; SetTextureChoices(_textureInspection); _grid.SetInspection(_inspection); _grid.SetBrush(null, null, null); _summary.Text = Summary(_inspection); UpdateLiquidInfo();
            _status.Text = $"Wrote {saved.result.EditedPixels:N0} alpha pixel edit(s) across {saved.result.EditedMaps:N0} map(s) and {saved.result.EditedCells:N0} cell(s) atomically · receipt {Path.GetFileName(saved.result.ReceiptPath)} · original retained" + (saved.textures.Error is null ? string.Empty : $" · texture layers unavailable: {saved.textures.Error}") + (saved.liquidResult.Error is null ? string.Empty : $" · liquid unavailable: {saved.liquidResult.Error}");
        }
        catch (Exception exception) { _status.Text = exception.Message; DesktopCrashLogger.Log("ADT alpha-brush write failed", exception); }
    }

    private async Task PreviewTextureAsync()
    {
        try
        {
            if (_inspection?.Kind != MapAssetKind.Adt || _textureInspection is null) throw new InvalidOperationException("Texture-layer editing requires an ADT with a valid MTEX/MCLY layout."); if (!int.TryParse(_textureLayerSlot.Text, out var slot) || slot < 0) throw new InvalidOperationException("Enter a non-negative texture layer slot."); if (_textureChoice.SelectedItem is not MapTextureChoice choice) throw new InvalidOperationException("Choose an existing MTEX texture.");
            var selectedCells = _grid.SelectedCells.Select(cell => (cell.X, cell.Y)).ToArray(); var cells = selectedCells.Where(cell => _textureSourceInspection?.Layers.Any(layer => layer.CellX == cell.X && layer.CellY == cell.Y && layer.Slot == slot) == true).ToArray(); var skipped = selectedCells.Length - cells.Length; _status.Text = $"Planning layer {slot} reassignment across {cells.Length:N0} compatible selected cell(s)…"; var plan = await Task.Run(() => AdtTextureLayerService.Plan(_inspection.Path, cells, slot, choice.Id)); var preview = await Task.Run(() => AdtTextureLayerService.Preview(plan));
            _grid.SetInspection(_inspection, plan.Edits.Select(edit => (edit.CellX, edit.CellY)).Distinct()); _textureInspection = preview; _texturePlan = plan; _groundEffectPlan = null; _liquidPlan = null; _liquidInspection = _liquidSourceInspection; _textureStructurePlan = null; _alphaPlan = null; _alphaInspection = _alphaSourceInspection; _selected.Text = DescribeSelection(_grid.SelectedCells); _status.Text = $"Preview only · {plan.Edits.Count:N0} layer(s) → MTEX {plan.TextureId}: {plan.TexturePath} · source bytes unchanged" + (skipped == 0 ? string.Empty : $" · skipped {skipped:N0} selected cell(s) without slot {slot}");
        }
        catch (Exception exception) { _status.Text = exception.Message; DesktopCrashLogger.Log("ADT texture-layer preview failed", exception); }
    }

    private async Task SaveTextureAsync()
    {
        try
        {
            if (_texturePlan is null) { await PreviewTextureAsync(); if (_texturePlan is null) return; } var top = TopLevel.GetTopLevel(this); if (top is null) return; var stem = Path.GetFileNameWithoutExtension(_texturePlan.InputPath);
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Write a separate texture-edited ADT", SuggestedFileName = stem + "-texture.adt", DefaultExtension = "adt", FileTypeChoices = [new FilePickerFileType("WoW ADT") { Patterns = ["*.adt"] }] }); var output = file?.TryGetLocalPath(); if (output is null) return; _status.Text = "Writing and re-validating texture layers…";
            var saved = await Task.Run(() => { var result = AdtTextureLayerService.Apply(_texturePlan, output, overwrite: false); return (result, map: MapAssetInspectionService.Inspect(result.OutputPath), alphaResult: TryInspectAlpha(result.OutputPath), liquidResult: TryInspectLiquid(result.OutputPath)); }); _path.Text = saved.result.OutputPath; _inspection = saved.map; _textureSourceInspection = _textureInspection = saved.result.Inspection; _alphaSourceInspection = _alphaInspection = saved.alphaResult.Inspection; _liquidSourceInspection = _liquidInspection = saved.liquidResult.Inspection; _texturePlan = null; _groundEffectPlan = null; _liquidPlan = null; _textureStructurePlan = null; _alphaPlan = null; _heightPlan = null; _brushPlan = null; SetTextureChoices(_textureInspection); _grid.SetInspection(_inspection); _summary.Text = Summary(_inspection); UpdateLiquidInfo();
            _status.Text = $"Wrote {saved.result.EditedLayers:N0} texture-layer edit(s) across {saved.result.EditedCells:N0} cell(s) atomically · receipt {Path.GetFileName(saved.result.ReceiptPath)} · original retained";
        }
        catch (Exception exception) { _status.Text = exception.Message; DesktopCrashLogger.Log("ADT texture-layer write failed", exception); }
    }

    private async Task PreviewTextureStructureAsync()
    {
        try
        {
            if (_inspection?.Kind != MapAssetKind.Adt) throw new InvalidOperationException("Adding a texture catalog entry and layer requires a complete WotLK ADT.");
            var cells = _grid.SelectedCells.Where(cell => cell.Present).Select(cell => (cell.X, cell.Y)).ToArray();
            if (cells.Length == 0) throw new InvalidOperationException("Select at least one present ADT cell for the new texture layer.");
            if (!byte.TryParse(_newTextureInitialAlpha.Text, out var initialAlpha)) throw new InvalidOperationException("Initial alpha must be an integer from 0 through 255.");
            var encoding = _newTextureEncoding.SelectedItem is AdtNewLayerEncoding selected ? selected : AdtNewLayerEncoding.Auto;
            _status.Text = $"Planning structural MTEX/MCLY/MCAL insertion for {cells.Length:N0} selected cell(s)…";
            var plan = await Task.Run(() => AdtTextureStructureService.Plan(_inspection.Path, _newTexturePath.Text ?? string.Empty, cells, encoding, initialAlpha));
            _textureStructurePlan = plan; _texturePlan = null; _groundEffectPlan = null; _liquidPlan = null; _liquidInspection = _liquidSourceInspection; _alphaPlan = null; _heightPlan = null; _brushPlan = null;
            _grid.SetInspection(_inspection, plan.Cells.Select(cell => (cell.X, cell.Y)));
            _selected.Text = DescribeSelection(_grid.SelectedCells) + $"\n\nPlanned new layer: slot {string.Join(", ", plan.Cells.Select(cell => cell.NewLayerSlot).Distinct().Order())} · MTEX {plan.TextureId} · {plan.TexturePath} · {plan.Encoding} · initial alpha {plan.InitialAlpha}";
            _status.Text = $"Preview only · append MTEX {plan.TextureId} and one {plan.Encoding} layer in {plan.Cells.Count:N0} cell(s) · source bytes unchanged";
        }
        catch (Exception exception) { _status.Text = exception.Message; DesktopCrashLogger.Log("ADT structural texture-layer preview failed", exception); }
    }

    private async Task SaveTextureStructureAsync()
    {
        try
        {
            if (_textureStructurePlan is null) { await PreviewTextureStructureAsync(); if (_textureStructurePlan is null) return; }
            var top = TopLevel.GetTopLevel(this); if (top is null) return; var stem = Path.GetFileNameWithoutExtension(_textureStructurePlan.InputPath);
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Write a separate structurally edited ADT", SuggestedFileName = stem + "-new-layer.adt", DefaultExtension = "adt", FileTypeChoices = [new FilePickerFileType("WoW ADT") { Patterns = ["*.adt"] }] });
            var output = file?.TryGetLocalPath(); if (output is null) return; _status.Text = "Rebuilding ADT offsets and re-validating MTEX, MCLY, MCAL, MHDR, and MCIN…";
            var result = await Task.Run(() => AdtTextureStructureService.Apply(_textureStructurePlan, output, overwrite: false));
            var liquidResult = await Task.Run(() => TryInspectLiquid(result.OutputPath)); _path.Text = result.OutputPath; _inspection = result.MapInspection; _textureSourceInspection = _textureInspection = result.TextureInspection; _alphaSourceInspection = _alphaInspection = result.AlphaInspection; _liquidSourceInspection = _liquidInspection = liquidResult.Inspection;
            _textureStructurePlan = null; _texturePlan = null; _groundEffectPlan = null; _liquidPlan = null; _alphaPlan = null; _heightPlan = null; _brushPlan = null; SetTextureChoices(_textureInspection); _grid.SetInspection(_inspection); _grid.SetBrush(null, null, null); _summary.Text = Summary(_inspection); _selected.Text = "Select a present grid cell for exact terrain, texture-layer, alpha-map, and liquid metadata."; UpdateLiquidInfo();
            _chunks.ItemsSource = _inspection.Chunks.Select(chunk => $"{chunk.Id} · {chunk.Occurrences:N0} chunk(s) · {chunk.PayloadBytes:N0} bytes").ToArray();
            _dependencies.ItemsSource = _inspection.TexturePaths.Select(value => "Texture · " + value).Concat(_inspection.ModelPaths.Select(value => "Model · " + value)).Concat(_inspection.WmoPaths.Select(value => "WMO · " + value)).DefaultIfEmpty("No path-list dependencies in this file.").ToArray();
            _status.Text = $"Appended MTEX {result.TextureId} and added {result.EditedCells:N0} layer(s) atomically · receipt {Path.GetFileName(result.ReceiptPath)} · original retained";
        }
        catch (Exception exception) { _status.Text = exception.Message; DesktopCrashLogger.Log("ADT structural texture-layer write failed", exception); }
    }

    private void SetTextureChoices(AdtTextureLayerInspection? inspection)
    {
        var selected = _textureChoice.SelectedItem is MapTextureChoice choice ? choice.Id : 0; var choices = inspection?.Textures.Select(texture => new MapTextureChoice(texture.Id, texture.Path)).ToArray() ?? []; _textureChoice.ItemsSource = choices; _textureChoice.SelectedItem = choices.FirstOrDefault(choice => choice.Id == selected) ?? choices.FirstOrDefault();
    }

    private void InvalidateTexturePreview(bool refreshSelection = true)
    {
        _texturePlan = null; _textureInspection = _textureSourceInspection; if (refreshSelection) _selected.Text = DescribeSelection(_grid.SelectedCells);
    }

    private void InvalidateGroundEffectPreview(bool refreshSelection = true)
    {
        _groundEffectPlan = null; _textureInspection = _textureSourceInspection; if (refreshSelection) _selected.Text = DescribeSelection(_grid.SelectedCells);
    }

    private void InvalidateLiquidPreview(bool refreshSelection = true)
    {
        _liquidPlan = null; _liquidInspection = _liquidSourceInspection; if (refreshSelection) { _selected.Text = DescribeSelection(_grid.SelectedCells); UpdateLiquidInfo(); }
    }

    private void InvalidateTextureStructurePreview(bool refreshSelection = true)
    {
        _textureStructurePlan = null; if (refreshSelection) _selected.Text = DescribeSelection(_grid.SelectedCells);
    }

    private void InvalidateAlphaPreview(bool refreshSelection = true)
    {
        _alphaPlan = null; _alphaInspection = _alphaSourceInspection; if (refreshSelection) _selected.Text = DescribeSelection(_grid.SelectedCells);
    }

    private static (AdtTextureLayerInspection? Inspection, string? Error) TryInspectTextures(string path)
    {
        try { return (AdtTextureLayerService.Inspect(path), null); } catch (Exception exception) { return (null, exception.Message); }
    }

    private static (AdtAlphaMapInspection? Inspection, string? Error) TryInspectAlpha(string path)
    {
        try { return (AdtAlphaMapService.Inspect(path), null); } catch (Exception exception) { return (null, exception.Message); }
    }

    private static (AdtLiquidInspection? Inspection, string? Error) TryInspectLiquid(string path)
    {
        try { return (AdtLiquidService.Inspect(path), null); } catch (Exception exception) { return (null, exception.Message); }
    }

    private string DescribeSelection(IReadOnlyList<MapTileCell> cells)
    {
        if (cells.Count == 0) return "No cells selected."; if (cells.Count > 1) return _inspection?.Kind == MapAssetKind.Wdt ? $"{cells.Count:N0} WDT tiles selected · {cells.Count(cell => cell.Present):N0} present · {cells.Count(cell => !cell.Present):N0} missing.\nHold Ctrl while clicking to toggle individual tiles." : $"{cells.Count:N0} present terrain cells selected.\nHold Ctrl while clicking to toggle individual cells."; var cell = cells[0]; var layers = _textureInspection?.Layers.Where(layer => layer.CellX == cell.X && layer.CellY == cell.Y).OrderBy(layer => layer.Slot).ToArray() ?? []; var alpha = _alphaInspection?.Maps.Where(map => map.CellX == cell.X && map.CellY == cell.Y).OrderBy(map => map.Slot).ToArray() ?? []; var liquid = _liquidInspection?.Layers.Where(layer => layer.CellX == cell.X && layer.CellY == cell.Y).OrderBy(layer => layer.Slot).ToArray() ?? [];
        var layerText = layers.Length == 0 ? "\nTexture layers: none" : "\nTexture layers:\n" + string.Join("\n", layers.Select(layer => $"  {layer.Slot}: MTEX {layer.TextureId} · {layer.TexturePath ?? "MISSING"} · flags 0x{layer.Flags:X} · alpha {layer.AlphaOffset:N0} · effect {layer.EffectId}"));
        var alphaText = alpha.Length == 0 ? "\nPaintable alpha maps: none" : "\nPaintable alpha maps:\n" + string.Join("\n", alpha.Select(map => $"  {map.Slot}: {map.Encoding} · {map.Minimum}..{map.Maximum} · avg {map.Average:0.###} · {map.EncodedBytesUsed:N0}/{map.Capacity:N0} bytes"));
        var liquidText = liquid.Length == 0 ? "\nLiquid layers: none" : "\nLiquid layers:\n" + string.Join("\n", liquid.Select(value => { var decoded = _liquidTypeCatalog?.Types.FirstOrDefault(type => type.Id == value.LiquidType)?.Name; return $"  {value.Slot}: type {value.LiquidType}{(decoded is null ? string.Empty : $" · {decoded}")} · {value.VertexFormat} · {value.OffsetX}:{value.OffsetY}+{value.Width}×{value.Height} · {value.ActiveTiles:N0} active · heights {value.MinHeight:0.###}..{value.MaxHeight:0.###}"; }));
        return Describe(cell) + layerText + alphaText + liquidText;
    }

    private void UpdateBrushOverlay()
    {
        if (TryNumber(_brushCenterX, out var x) && TryNumber(_brushCenterY, out var y) && TryNumber(_brushRadius, out var radius) && radius > 0) _grid.SetBrush(x, y, radius); else _grid.SetBrush(null, null, null);
    }
    private void BrushInputChanged() { _brushPlan = null; _alphaPlan = null; UpdateBrushOverlay(); }

    private static float Number(TextBox box, string label) => TryNumber(box, out var value) ? value : throw new InvalidOperationException($"Enter a finite {label} using a period as the decimal separator.");
    private static bool TryNumber(TextBox box, out float value) => float.TryParse(box.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

    private async Task SaveHeightAsync()
    {
        try
        {
            if (_heightPlan is null) { await PreviewHeightAsync(); if (_heightPlan is null) return; }
            var top = TopLevel.GetTopLevel(this); if (top is null) return; var stem = Path.GetFileNameWithoutExtension(_heightPlan.InputPath);
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Write a separate edited ADT", SuggestedFileName = stem + "-height-edit.adt", DefaultExtension = "adt", FileTypeChoices = [new FilePickerFileType("WoW ADT") { Patterns = ["*.adt"] }] });
            var output = file?.TryGetLocalPath(); if (output is null) return; _status.Text = "Writing and re-validating edited ADT…";
            var saved = await Task.Run(() => { var result = AdtHeightEditService.Apply(_heightPlan, output, overwrite: false); return (result, textureResult: TryInspectTextures(result.OutputPath), alphaResult: TryInspectAlpha(result.OutputPath), liquidResult: TryInspectLiquid(result.OutputPath)); }); var result = saved.result;
            _path.Text = result.OutputPath; _inspection = result.Inspection; _textureSourceInspection = _textureInspection = saved.textureResult.Inspection; _alphaSourceInspection = _alphaInspection = saved.alphaResult.Inspection; _liquidSourceInspection = _liquidInspection = saved.liquidResult.Inspection; SetTextureChoices(_textureInspection); _heightPlan = null; _texturePlan = null; _groundEffectPlan = null; _liquidPlan = null; _textureStructurePlan = null; _alphaPlan = null; _grid.SetInspection(result.Inspection); _summary.Text = Summary(result.Inspection); UpdateLiquidInfo();
            _status.Text = $"Wrote {result.EditedCells:N0} edited terrain cell(s) atomically · receipt {Path.GetFileName(result.ReceiptPath)} · original retained" + (saved.textureResult.Error is null ? string.Empty : $" · texture layers unavailable: {saved.textureResult.Error}") + (saved.alphaResult.Error is null ? string.Empty : $" · alpha maps unavailable: {saved.alphaResult.Error}") + (saved.liquidResult.Error is null ? string.Empty : $" · liquid unavailable: {saved.liquidResult.Error}");
        }
        catch (Exception exception) { _status.Text = exception.Message; DesktopCrashLogger.Log("ADT height write failed", exception); }
    }

    private async Task PickAsync()
    {
        var top = TopLevel.GetTopLevel(this); if (top is null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open a WotLK map asset", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("WoW map assets") { Patterns = ["*.adt", "*.wdt", "*.wdl"] }] });
        var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path is not null) await OpenAsync(path);
    }

    private async Task PickWmoAsync()
    {
        var top = TopLevel.GetTopLevel(this); if (top is null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open an extracted WotLK root or group WMO", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("WoW WMO") { Patterns = ["*.wmo"] }] });
        var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path is not null) await LoadWmoAsync(path, null);
    }

    private async Task PickWmoLibraryAsync()
    {
        var top = TopLevel.GetTopLevel(this); if (top is null) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select Crucible processed asset library", AllowMultiple = false });
        var path = folders.FirstOrDefault()?.TryGetLocalPath(); if (path is null) return;
        _wmoLibrary.Text = Path.GetFullPath(path); _wmoLibraryIndex = null; _session.Settings.ProcessedAssetLibraryPath = _wmoLibrary.Text; _session.Settings.Save(); await ResolveSelectedWmoAsync();
    }

    private Control PlacementFields()
    {
        var grid = new Grid { ColumnDefinitions = new("Auto,*,*,*"), RowDefinitions = new("Auto,Auto,Auto,Auto,Auto,Auto"), ColumnSpacing = 6, RowSpacing = 5 };
        AddPlacementRow(grid, 0, "Position X / Y / Z", _placementX, _placementY, _placementZ);
        AddPlacementRow(grid, 1, "Rotation X / Y / Z", _placementRotX, _placementRotY, _placementRotZ);
        var scaleLabel = Label("Scale"); Grid.SetRow(scaleLabel, 2); grid.Children.Add(scaleLabel); Grid.SetRow(_placementScale, 2); Grid.SetColumn(_placementScale, 1); Grid.SetColumnSpan(_placementScale, 3); grid.Children.Add(_placementScale);
        AddPlacementRow(grid, 3, "New UID / flags / WMO doodad set", _placementUid, _placementFlags, _placementDoodadSet);
        var nameSetLabel = Label("WMO name set"); Grid.SetRow(nameSetLabel, 4); grid.Children.Add(nameSetLabel); Grid.SetRow(_placementNameSet, 4); Grid.SetColumn(_placementNameSet, 1); Grid.SetColumnSpan(_placementNameSet, 3); grid.Children.Add(_placementNameSet);
        AddPlacementRow(grid, 5, "Snap position / degrees / scale", _placementPositionSnap, _placementRotationSnap, _placementScaleSnap);
        return grid;
    }

    private static void AddPlacementRow(Grid grid, int row, string label, params Control[] controls)
    {
        var text = Label(label); Grid.SetRow(text, row); grid.Children.Add(text);
        for (var index = 0; index < controls.Length; index++) { Grid.SetRow(controls[index], row); Grid.SetColumn(controls[index], index + 1); grid.Children.Add(controls[index]); }
    }

    private IEnumerable<TextBox> PlacementInputs() => [_placementX, _placementY, _placementZ, _placementRotX, _placementRotY, _placementRotZ, _placementScale, _placementUid, _placementFlags, _placementDoodadSet, _placementNameSet];
    private IEnumerable<TextBox> PlacementSnapInputs() => [_placementPositionSnap, _placementRotationSnap, _placementScaleSnap];

    private void UseScenePlacementPick(MapSceneTerrainPick pick)
    {
        _updatingScenePreviewFields = true; try { _placementX.Text = Format(pick.WorldPosition.X); _placementY.Text = Format(pick.WorldPosition.Y); _placementZ.Text = Format(pick.WorldPosition.Z); } finally { _updatingScenePreviewFields = false; } UpdateScenePlacementPreviewFromFields(); _placementPlan = null; _placementMultiTilePlan = null;
        var reference = _wmoReferences.SelectedItem as MapObjectReferenceChoice; var action = reference?.M2Placement is not null || reference?.WmoPlacement is not null ? "coordinated transform" : "coordinated add";
        _placementStatus.Text = $"Scene-picked MCNK {pick.CellX},{pick.CellY} · X {pick.WorldPosition.X:0.###} · Y {pick.WorldPosition.Y:0.###} · Z {pick.WorldPosition.Z:0.###}. Open placement controls and preview the {action}; this is an unsaved field update only.";
        _sceneStatus.Text = $"Exact terrain pick copied into placement X / Y / Z · MCNK {pick.CellX},{pick.CellY} · triangle {pick.TriangleIndex:N0} · no ADT or project file changed.";
    }

    private async Task ShowPlacementScenePreviewAsync()
    {
        try
        {
            if (!_mapScene.HasScene) throw new InvalidOperationException("Build the terrain + placement scene first so the preview has exact terrain and coherent provenance context.");
            if (_wmoReferences.SelectedItem is not MapObjectReferenceChoice reference || string.IsNullOrWhiteSpace(reference.ClientPath)) throw new InvalidOperationException("Select a path-listed or placed M2/WMO first.");
            if (_wmoCandidates.SelectedItem is not AssetCandidateChoice candidate) throw new InvalidOperationException("Choose one exact extracted provenance candidate before loading a live placement preview.");
            if (!TryPlacementTransform(out var position, out var orientation, out var scale)) throw new InvalidOperationException("Enter finite position/rotation values and a WotLK-encodable positive scale before showing the live preview.");
            _wmoOperation?.Cancel(); _wmoOperation?.Dispose(); _wmoOperation = new CancellationTokenSource(); var token = _wmoOperation.Token; _placementStatus.Text = "Loading exact selected geometry into the terrain scene…"; MapScenePlacementPreview preview;
            if (reference.IsWmo)
            {
                var geometry = await Task.Run(() => WmoPreviewGeometryService.Load(candidate.Location.SourcePath, cancellationToken: token), token); token.ThrowIfCancellationRequested(); preview = new(geometry.Vertices, geometry.TriangleIndices, AdtPlacementKind.Wmo, reference.WmoPlacement?.UniqueId, reference.ClientPath!, position, orientation, scale);
            }
            else
            {
                var geometry = await Task.Run(() => M2PreviewGeometryService.Load(candidate.Location.SourcePath), token); token.ThrowIfCancellationRequested(); preview = new(geometry.Vertices, geometry.TriangleIndices, AdtPlacementKind.M2, reference.M2Placement?.UniqueId, reference.ClientPath!, position, orientation, scale);
            }
            _mapScene.SetPlacementSnapSettings(ReadPlacementSnapSettings()); _mapScene.SetPlacementPreview(preview); _visualTabs.SelectedIndex = 1; _sceneStatus.Text = $"LIVE UNSAVED {preview.Kind} PREVIEW · {Path.GetFileName(preview.Label)} · exact provenance {candidate.Location.Provenance} · edit across terrain, translate world/local X/Y/Z, rotate stored X/Y/Z, or uniformly scale. Configurable snapping and gesture undo/redo remain unsaved. No file changed."; _placementStatus.Text = "Gold scene preview is synchronized with these transform fields. Gizmo edits invalidate prior plans and remain unsaved until a coordinated review/build is explicitly completed.";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _placementStatus.Text = exception.Message; DesktopCrashLogger.Log("ADT live placement preview failed", exception); }
    }

    private void UseScenePlacementPreviewChange(MapScenePlacementPreviewChanged preview)
    {
        _updatingScenePreviewFields = true;
        try { _placementX.Text = Format(preview.Position.X); _placementY.Text = Format(preview.Position.Y); _placementZ.Text = Format(preview.Position.Z); _placementRotX.Text = Format(preview.Orientation.X); _placementRotY.Text = Format(preview.Orientation.Y); _placementRotZ.Text = Format(preview.Orientation.Z); _placementScale.Text = Format(preview.Scale); }
        finally { _updatingScenePreviewFields = false; }
        _placementPlan = null; _placementMultiTilePlan = null; var action = preview.Mode == MapScenePlacementEditMode.History ? preview.Action ?? "history" : preview.Mode.ToString(); _placementStatus.Text = $"Live scene {action} updated the unsaved transform fields. Preview the coordinated {(HasExistingPlacement() ? "transform" : "add")} before building; no ADT or project file changed.";
    }

    private void UpdateScenePlacementPreviewFromFields()
    {
        if (_updatingScenePreviewFields || !TryPlacementTransform(out var position, out var orientation, out var scale)) return; _mapScene.UpdatePlacementPreviewTransform(position, orientation, scale);
    }

    private void UpdateScenePlacementSnapSettings()
    {
        try { _mapScene.SetPlacementSnapSettings(ReadPlacementSnapSettings()); }
        catch when (PlacementSnapInputs().Any(input => string.IsNullOrWhiteSpace(input.Text))) { }
        catch (Exception exception) { _placementStatus.Text = exception.Message; }
    }

    private MapScenePlacementSnapSettings ReadPlacementSnapSettings() => MapScenePlacementGizmoService.ValidateSnapSettings(new(Number(_placementPositionSnap, "position snap step"), Number(_placementRotationSnap, "rotation snap step"), Number(_placementScaleSnap, "scale snap step")));

    private bool TryPlacementTransform(out Vector3 position, out Vector3 orientation, out float scale)
    {
        position = default; orientation = default; scale = 0; if (!TryNumber(_placementX, out var x) || !TryNumber(_placementY, out var y) || !TryNumber(_placementZ, out var z) || !TryNumber(_placementRotX, out var rx) || !TryNumber(_placementRotY, out var ry) || !TryNumber(_placementRotZ, out var rz) || !TryNumber(_placementScale, out scale) || !MapScenePlacementGizmoService.IsEncodableScale(scale)) return false; position = new(x, y, z); orientation = new(rx, ry, rz); return true;
    }

    private bool HasExistingPlacement() => _wmoReferences.SelectedItem is MapObjectReferenceChoice { M2Placement: not null } or MapObjectReferenceChoice { WmoPlacement: not null };

    private void LoadPlacementFields()
    {
        _placementPlan = null; _placementMultiTilePlan = null; var reference = _wmoReferences.SelectedItem as MapObjectReferenceChoice;
        Vector3? position = reference?.M2Placement?.Position ?? reference?.WmoPlacement?.Position;
        Vector3? orientation = reference?.M2Placement?.Orientation ?? reference?.WmoPlacement?.Orientation;
        var scale = reference?.M2Placement?.Scale ?? reference?.WmoPlacement?.Scale;
        if (position is null || orientation is null || scale is null)
        {
            if (reference?.ClientPath is null) { foreach (var input in PlacementInputs()) { input.Text = string.Empty; input.IsEnabled = false; } _placementStatus.Text = "Select a path-listed or placed M2/WMO first."; return; }
            var tileX = _inspection?.TileX ?? 0; var tileY = _inspection?.TileY ?? 0; _placementX.Text = Format((float)((tileX + 0.5) * (1600.0 / 3.0))); _placementY.Text = "0"; _placementZ.Text = Format((float)((tileY + 0.5) * (1600.0 / 3.0))); _placementRotX.Text = _placementRotY.Text = _placementRotZ.Text = "0"; _placementScale.Text = "1"; _placementUid.Text = string.Empty; _placementFlags.Text = _placementDoodadSet.Text = _placementNameSet.Text = "0";
            foreach (var input in PlacementInputs()) input.IsEnabled = true; _placementDoodadSet.IsEnabled = _placementNameSet.IsEnabled = reference.IsWmo;
            _placementStatus.Text = $"Unplaced {reference.ClientPath}. Enter its transform, choose one exact extracted provenance candidate, then preview Add. The default position is this tile's center; Crucible derives geometry bounds and every required MCRF cell before writing."; return;
        }
        _placementX.Text = Format(position.Value.X); _placementY.Text = Format(position.Value.Y); _placementZ.Text = Format(position.Value.Z);
        _placementRotX.Text = Format(orientation.Value.X); _placementRotY.Text = Format(orientation.Value.Y); _placementRotZ.Text = Format(orientation.Value.Z); _placementScale.Text = Format(scale.Value); _placementUid.Text = string.Empty;
        _placementFlags.Text = (reference?.M2Placement?.Flags ?? reference?.WmoPlacement?.Flags ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture); _placementDoodadSet.Text = (reference?.WmoPlacement?.DoodadSet ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture); _placementNameSet.Text = (reference?.WmoPlacement?.NameSet ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var input in PlacementInputs()) input.IsEnabled = true;
        var isM2 = reference?.M2Placement is not null;
        _placementDoodadSet.IsEnabled = _placementNameSet.IsEnabled = !isM2;
        _placementStatus.Text = isM2
            ? $"MDDF index {reference!.M2Placement!.Index:N0} · UID {reference.M2Placement.UniqueId:N0}. Edit its transform, clone it under an automatically safe UID, or review exact deletion."
            : $"MODF index {reference!.WmoPlacement!.Index:N0} · UID {reference.WmoPlacement.UniqueId:N0}. Edit/clone binds the exact version-17 WMO root and rebuilds world extents; deletion remaps every MCRF reference.";
    }

    private async Task PreviewPlacementTransformAsync()
    {
        try
        {
            if (_inspection?.Kind != MapAssetKind.Adt) throw new InvalidOperationException("Load a WotLK ADT before editing object placements.");
            if (_wmoReferences.SelectedItem is not MapObjectReferenceChoice reference || reference.M2Placement is null && reference.WmoPlacement is null) throw new InvalidOperationException("Select an existing MDDF or MODF placement row first.");
            if (_wmoCandidates.SelectedItem is not AssetCandidateChoice candidate) throw new InvalidOperationException("Choose one exact extracted provenance candidate. Coordinated transforms require real geometry to determine every old and edited ADT tile.");
            var position = new Vector3(Number(_placementX, "placement X"), Number(_placementY, "placement Y"), Number(_placementZ, "placement Z")); var orientation = new Vector3(Number(_placementRotX, "rotation X"), Number(_placementRotY, "rotation Y"), Number(_placementRotZ, "rotation Z"));
            AdtMultiTilePlacementTransformPlan plan;
            if (reference.M2Placement is { } m2)
            {
                var scale = Number(_placementScale, "placement scale"); var rawScale = AdtPlacementTransformService.EncodeScale(scale);
                plan = await Task.Run(() => AdtMultiTilePlacementTransformService.Plan(_inspection.Path, AdtPlacementKind.M2, m2.Index, candidate.Location.SourcePath, position, orientation, rawScale));
                await LoadM2Async(candidate.Location.SourcePath, m2 with { Position = plan.EditedPosition.ToVector3(), Orientation = plan.EditedOrientation.ToVector3(), ScaleRaw = plan.EditedScaleRaw });
            }
            else
            {
                var wmo = reference.WmoPlacement!;
                var scale = Number(_placementScale, "placement scale"); var rawScale = wmo.ScaleRaw == 0 && BitConverter.SingleToInt32Bits(scale) == BitConverter.SingleToInt32Bits(1f) ? (ushort)0 : AdtPlacementTransformService.EncodeScale(scale);
                plan = await Task.Run(() => AdtMultiTilePlacementTransformService.Plan(_inspection.Path, AdtPlacementKind.Wmo, wmo.Index, candidate.Location.SourcePath, position, orientation, rawScale));
                var edited = wmo with { Position = plan.EditedPosition.ToVector3(), Orientation = plan.EditedOrientation.ToVector3(), ScaleRaw = plan.EditedScaleRaw, MinimumExtent = plan.EditedMinimum.ToVector3(), MaximumExtent = plan.EditedMaximum.ToVector3() };
                await LoadWmoAsync(candidate.Location.SourcePath, edited);
            }
            _placementPlan = plan;
            _placementStatus.Text = $"Preview only · coordinated {plan.Kind} / UID {plan.UniqueId:N0} · remove {plan.DeleteSegments.Count:N0} old tile copy/copies → add {plan.TargetTiles.Count:N0} edited-footprint copy/copies · position {VectorText(plan.OriginalPosition)} → {VectorText(plan.EditedPosition)} · rotation {VectorText(plan.OriginalOrientation)} → {VectorText(plan.EditedOrientation)} · scale {ScaleText(plan.OriginalScaleRaw)} → {ScaleText(plan.EditedScaleRaw)} · exact asset SHA {plan.AssetSha256[..12]}… · every source unchanged";
            _status.Text = "Coordinated transform preview is ready. Build publishes one tiny map payload containing the exact union of old and edited tiles.";
        }
        catch (Exception exception) { _placementStatus.Text = exception.Message; DesktopCrashLogger.Log("ADT placement transform preview failed", exception); }
    }

    private async Task SavePlacementTransformAsync()
    {
        try
        {
            if (_placementPlan is null) { await PreviewPlacementTransformAsync(); if (_placementPlan is null) return; }
            var top = TopLevel.GetTopLevel(this); if (top is null) return; var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a parent for the new MPQ-ready coordinated transform bundle", AllowMultiple = false });
            var parent = folders.FirstOrDefault()?.TryGetLocalPath(); if (parent is null) return; var output = Path.Combine(parent, $"Crucible-{_placementPlan.MapPrefix}-Transform-UID-{_placementPlan.UniqueId}-{DateTime.Now:yyyyMMdd-HHmmss}");
            _placementStatus.Text = $"Removing {_placementPlan.DeleteSegments.Count:N0} old copy/copies, rebuilding {_placementPlan.TargetTiles.Count:N0} edited geometry copy/copies, and verifying every affected ADT…";
            var result = await Task.Run(() => AdtMultiTilePlacementTransformService.Apply(_placementPlan, output)); var targetCoordinates = _placementPlan.TargetTiles.Select(value => (value.TileX, value.TileY)).ToHashSet(); var reopened = result.Outputs.FirstOrDefault(value => targetCoordinates.Contains((value.TileX, value.TileY)) && value.TileX == _inspection?.TileX && value.TileY == _inspection?.TileY) ?? result.Outputs.First(value => targetCoordinates.Contains((value.TileX, value.TileY))); await OpenAsync(reopened.Path);
            _status.Text = $"Transformed {result.Kind} / UID {result.UniqueId:N0} atomically across {result.Outputs.Count:N0} changed tile(s) · tiny manifest {Path.GetFileName(result.ManifestPath)} · receipt {Path.GetFileName(result.ReceiptPath)} · every source retained";
        }
        catch (Exception exception) { _placementStatus.Text = exception.Message; DesktopCrashLogger.Log("ADT placement transform write failed", exception); }
    }

    private async Task PreviewPlacementAddAsync()
    {
        try
        {
            if (_inspection?.Kind != MapAssetKind.Adt) throw new InvalidOperationException("Load a WotLK ADT before adding object placements.");
            if (_wmoReferences.SelectedItem is not MapObjectReferenceChoice reference || string.IsNullOrWhiteSpace(reference.ClientPath)) throw new InvalidOperationException("Select a path-listed or placed M2/WMO with a resolvable client path first.");
            if (_wmoCandidates.SelectedItem is not AssetCandidateChoice candidate) throw new InvalidOperationException("Choose one exact extracted provenance candidate. Geometry evidence is mandatory for safe path, bounds, and MCRF creation.");
            var kind = reference.IsWmo ? AdtPlacementKind.Wmo : AdtPlacementKind.M2; var position = new Vector3(Number(_placementX, "placement X"), Number(_placementY, "placement Y"), Number(_placementZ, "placement Z")); var orientation = new Vector3(Number(_placementRotX, "rotation X"), Number(_placementRotY, "rotation Y"), Number(_placementRotZ, "rotation Z")); var scale = Number(_placementScale, "placement scale");
            var scaleRaw = kind == AdtPlacementKind.Wmo && reference.WmoPlacement?.ScaleRaw == 0 && BitConverter.SingleToInt32Bits(scale) == BitConverter.SingleToInt32Bits(1f) ? (ushort)0 : AdtPlacementTransformService.EncodeScale(scale);
            var uid = OptionalUnsigned(_placementUid, "new placement UID", uint.MaxValue); var flags = checked((ushort)RequiredUnsigned(_placementFlags, "placement flags", ushort.MaxValue)); var doodadSet = kind == AdtPlacementKind.Wmo ? checked((ushort)RequiredUnsigned(_placementDoodadSet, "WMO doodad set", ushort.MaxValue)) : (ushort)0; var nameSet = kind == AdtPlacementKind.Wmo ? checked((ushort)RequiredUnsigned(_placementNameSet, "WMO name set", ushort.MaxValue)) : (ushort)0;
            _placementStatus.Text = "Scanning the effective map, allocating one UID, and binding exact model bounds to every intersected ADT…";
            var transaction = await Task.Run(() => AdtMultiTilePlacementService.PlanAdd(_inspection.Path, kind, reference.ClientPath!, candidate.Location.SourcePath, position, orientation, scaleRaw, uid, flags, doodadSet, nameSet)); _placementMultiTilePlan = transaction;
            var plan = transaction.Segments.FirstOrDefault(segment => Path.GetFullPath(segment.InputPath).Equals(Path.GetFullPath(_inspection.Path), StringComparison.OrdinalIgnoreCase)) ?? transaction.Segments[0];
            if (kind == AdtPlacementKind.M2) await LoadM2Async(candidate.Location.SourcePath, new(plan.Index, plan.NameId, plan.UniqueId, plan.ClientPath, plan.Position.ToVector3(), plan.Orientation.ToVector3(), plan.ScaleRaw, plan.Flags));
            else await LoadWmoAsync(candidate.Location.SourcePath, new(plan.Index, plan.NameId, plan.UniqueId, plan.ClientPath, plan.Position.ToVector3(), plan.Orientation.ToVector3(), plan.MinimumExtent!.ToVector3(), plan.MaximumExtent!.ToVector3(), plan.Flags, plan.DoodadSet, plan.NameSet, plan.ScaleRaw));
            _placementStatus.Text = $"Preview only · ADD {transaction.Kind} / UID {transaction.UniqueId:N0} · {transaction.Segments.Count:N0} intersected ADT tile(s) · {transaction.Segments.Sum(segment => segment.ReferencedCells.Count):N0} tile-local MCRF cell reference(s) · UID proven absent across {transaction.SourceTiles.Count:N0} effective map tile(s) · asset SHA {plan.AssetSha256![..12]}… · every source unchanged.";
            _status.Text = "Coordinated placement-add preview is ready. Build publishes a new tiny Payload tree; it never overwrites map sources.";
        }
        catch (Exception exception) { _placementStatus.Text = exception.Message; DesktopCrashLogger.Log("ADT placement add preview failed", exception); }
    }

    private async Task PreviewPlacementDeleteAsync()
    {
        try
        {
            if (_inspection?.Kind != MapAssetKind.Adt) throw new InvalidOperationException("Load a WotLK ADT before deleting object placements."); if (_wmoReferences.SelectedItem is not MapObjectReferenceChoice reference || reference.M2Placement is null && reference.WmoPlacement is null) throw new InvalidOperationException("Select an existing MDDF or MODF placement row first.");
            var kind = reference.M2Placement is null ? AdtPlacementKind.Wmo : AdtPlacementKind.M2; var index = reference.M2Placement?.Index ?? reference.WmoPlacement!.Index; var transaction = await Task.Run(() => AdtMultiTilePlacementService.PlanDelete(_inspection.Path, kind, index)); _placementMultiTilePlan = transaction; var plan = transaction.Segments[0];
            _placementStatus.Text = $"REVIEW DELETE · {transaction.Kind} / UID {transaction.UniqueId:N0} · {plan.ClientPath ?? "unresolved path"} · removes {transaction.Segments.Count:N0} identical tile copy/copies across {transaction.Segments.Sum(segment => segment.ReferencedCells.Count):N0} MCRF cell(s), decrements every later local index, and retains path catalogs to avoid destabilizing unrelated name IDs · all sources unchanged.";
            _status.Text = "Coordinated deletion is reviewed and hash-bound. Build creates a new tiny Payload tree plus manifest and receipt.";
        }
        catch (Exception exception) { _placementStatus.Text = exception.Message; DesktopCrashLogger.Log("ADT placement delete preview failed", exception); }
    }

    private async Task SavePlacementLifecycleAsync(AdtPlacementLifecycleOperation operation)
    {
        try
        {
            if (_placementMultiTilePlan?.Operation != operation) { if (operation == AdtPlacementLifecycleOperation.Add) await PreviewPlacementAddAsync(); else await PreviewPlacementDeleteAsync(); if (_placementMultiTilePlan?.Operation != operation) return; }
            var plan = _placementMultiTilePlan; var top = TopLevel.GetTopLevel(this); if (top is null) return;
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a parent for the new MPQ-ready map placement bundle", AllowMultiple = false }); var parent = folders.FirstOrDefault()?.TryGetLocalPath(); if (parent is null) return;
            var output = Path.Combine(parent, $"Crucible-{plan.MapPrefix}-{plan.Operation}-UID-{plan.UniqueId}-{DateTime.Now:yyyyMMdd-HHmmss}");
            _placementStatus.Text = $"Rebuilding {plan.Segments.Count:N0} ADT tile(s), their local path/index tables, MHDR/MCIN pointers, and {plan.Segments.Sum(segment => segment.ReferencedCells.Count):N0} reviewed MCRF cell reference(s)…"; var result = await Task.Run(() => AdtMultiTilePlacementService.Apply(plan, output));
            var reopened = result.Outputs.FirstOrDefault(value => value.TileX == _inspection?.TileX && value.TileY == _inspection?.TileY) ?? result.Outputs[0]; await OpenAsync(reopened.Path);
            _status.Text = $"{result.Operation} {result.Kind} / UID {result.UniqueId:N0} published atomically across {result.Outputs.Count:N0} ADT tile(s) · tiny manifest {Path.GetFileName(result.ManifestPath)} · receipt {Path.GetFileName(result.ReceiptPath)} · every source retained";
        }
        catch (Exception exception) { _placementStatus.Text = exception.Message; DesktopCrashLogger.Log("ADT placement lifecycle write failed", exception); }
    }

    private static string Format(float value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    private static string VectorText(AdtPlacementVector value) => $"{value.X:0.###},{value.Y:0.###},{value.Z:0.###}";
    private static string ScaleText(ushort raw) => (raw == 0 ? 1f : raw / 1024f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    private static uint? OptionalUnsigned(TextBox box, string label, uint maximum) => string.IsNullOrWhiteSpace(box.Text) ? null : RequiredUnsigned(box, label, maximum);
    private static uint RequiredUnsigned(TextBox box, string label, uint maximum)
    {
        var text = box.Text?.Trim() ?? string.Empty; var style = System.Globalization.NumberStyles.Integer; if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { text = text[2..]; style = System.Globalization.NumberStyles.AllowHexSpecifier; }
        if (!uint.TryParse(text, style, System.Globalization.CultureInfo.InvariantCulture, out var value) || value > maximum) throw new InvalidOperationException($"Enter {label} as a decimal or 0x-prefixed integer from 0 through {maximum:N0}."); return value;
    }

    private async Task ResolveSelectedWmoAsync()
    {
        _wmoOperation?.Cancel(); _wmoOperation?.Dispose(); _wmoOperation = new CancellationTokenSource(); var token = _wmoOperation.Token; _wmoCandidates.ItemsSource = null;
        if (_wmoReferences.SelectedItem is not MapObjectReferenceChoice reference) return;
        if (string.IsNullOrWhiteSpace(reference.ClientPath))
        {
            var identity = reference.WmoPlacement?.UniqueId ?? reference.M2Placement?.UniqueId;
            _wmoStatus.Text = $"Placed object UID {identity?.ToString("N0") ?? "?"} has no resolvable path-list entry.";
            return;
        }
        var clientPath = reference.ClientPath;
        var library = _wmoLibrary.Text?.Trim(); if (string.IsNullOrWhiteSpace(library)) { _wmoStatus.Text = $"Referenced WMO: {clientPath} · choose the processed asset library to resolve it."; return; }
        _wmoStatus.Text = $"Resolving every provenance candidate for {clientPath}…";
        try
        {
            var resolved = await Task.Run(() =>
            {
                var root = Path.GetFullPath(library);
                var index = _wmoLibraryIndex is { } cached && cached.LibraryRoot.Equals(root, StringComparison.OrdinalIgnoreCase)
                    ? cached : ClientAssetDependencyService.OpenLibraryLayout(root);
                var candidates = ClientAssetDependencyService.FindCandidates(index, clientPath, token);
                string? provenance = null;
                if (_inspection is not null) try { provenance = ClientAssetDependencyService.InferLocation(index, _inspection.Path).Provenance; } catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException) { }
                return (index, candidates, provenance);
            }, token);
            token.ThrowIfCancellationRequested(); _wmoLibraryIndex = resolved.index;
            var choices = resolved.candidates.Select(value => new AssetCandidateChoice(value)).ToArray(); _wmoCandidates.ItemsSource = choices;
            var preferred = resolved.provenance is null ? null : choices.FirstOrDefault(value => value.Location.Provenance.Equals(resolved.provenance, StringComparison.OrdinalIgnoreCase));
            _wmoCandidates.SelectedItem = preferred ?? (choices.Length == 1 ? choices[0] : null);
            _wmoStatus.Text = choices.Length switch
            {
                0 => $"Referenced object is absent from the processed library: {clientPath}",
                1 => $"Resolved one source for {clientPath}: {choices[0].Location.Provenance}. Ready to preview.",
                _ when preferred is not null => $"Resolved {choices.Length:N0} sources; selected the map's exact provenance '{preferred.Location.Provenance}'.",
                _ => $"Resolved {choices.Length:N0} provenance candidates. Choose one explicitly; Crucible has not compared their bytes and will not guess a layer."
            };
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _wmoStatus.Text = $"Map object resolution failed: {exception.Message}"; DesktopCrashLogger.Log("Map object resolution failed", exception); }
    }

    private void DescribeSelectedWmoCandidate()
    {
        if (_wmoCandidates.SelectedItem is not AssetCandidateChoice choice) return;
        _wmoStatus.Text = $"Selected {choice.Location.Provenance} for {choice.Location.ClientPath}. Preview uses this exact physical file rather than guessing between layers.";
    }

    private async Task PreviewSelectedObjectAsync()
    {
        if (_wmoCandidates.SelectedItem is not AssetCandidateChoice choice) { _wmoStatus.Text = "Select an explicit object provenance candidate first."; return; }
        var reference = _wmoReferences.SelectedItem as MapObjectReferenceChoice;
        if (reference?.IsWmo == true || Path.GetExtension(choice.Location.SourcePath).Equals(".wmo", StringComparison.OrdinalIgnoreCase))
            await LoadWmoAsync(choice.Location.SourcePath, reference?.WmoPlacement);
        else
            await LoadM2Async(choice.Location.SourcePath, reference?.M2Placement);
    }

    private async Task BuildMapSceneAsync(Button button)
    {
        if (_inspection?.Kind != MapAssetKind.Adt) { _sceneStatus.Text = "Load a WotLK ADT before composing its terrain and placements."; return; }
        var library = _wmoLibrary.Text?.Trim(); if (string.IsNullOrWhiteSpace(library) || !Directory.Exists(library)) { _sceneStatus.Text = "Choose an existing processed asset library first."; return; }
        if (!int.TryParse(_scenePlacementLimit.Text, out var requestedLimit) || requestedLimit < 0 || requestedLimit > 50_000) { _sceneStatus.Text = "Placement load limit must be 0 through 50,000; zero means all only when the ADT stays within the 50,000-instance safety boundary."; return; }
        var totalPlacements = _inspection.M2Placements.Count + _inspection.WmoPlacements.Count; if (requestedLimit == 0 && totalPlacements > 50_000) { _sceneStatus.Text = $"This ADT has {totalPlacements:N0} placements. Enter an explicit load limit up to 50,000 so the diagnostic scene remains bounded."; return; }
        var selectedProvenance = (_sceneProvenance.SelectedItem as SceneProvenanceChoice)?.Name; _wmoOperation?.Cancel(); _wmoOperation?.Dispose(); _wmoOperation = new CancellationTokenSource(); var token = _wmoOperation.Token;
        try
        {
            button.IsEnabled = false; _sceneStatus.Text = "Building real MCVT terrain and resolving one coherent client source profile…"; var inspection = _inspection; var limit = requestedLimit == 0 ? totalPlacements : requestedLimit;
            var result = await Task.Run(() =>
            {
                var root = Path.GetFullPath(library); var index = _wmoLibraryIndex is { } cached && cached.LibraryRoot.Equals(root, StringComparison.OrdinalIgnoreCase) ? cached : ClientAssetDependencyService.OpenLibraryLayout(root);
                var requestedM2 = inspection.M2Placements.Select(value => (M2: (MapM2Placement?)value, Wmo: (MapWmoPlacement?)null, Path: value.ClientPath));
                var requestedWmo = inspection.WmoPlacements.Select(value => (M2: (MapM2Placement?)null, Wmo: (MapWmoPlacement?)value, Path: value.ClientPath));
                var requested = requestedM2.Concat(requestedWmo).Take(limit).ToArray(); var terrain = AdtTerrainSceneService.Load(inspection.Path); AdtTextureLayerInspection? textureInspection = null;
                try { textureInspection = AdtTextureLayerService.Inspect(inspection.Path); } catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException) { }
                var texturePaths = textureInspection?.Layers.Select(value => value.TexturePath).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
                var paths = requested.Where(value => !string.IsNullOrWhiteSpace(value.Path)).Select(value => value.Path!).Concat(texturePaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); var candidates = new Dictionary<string, IReadOnlyList<ClientAssetLocation>>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in paths) { token.ThrowIfCancellationRequested(); candidates[path] = ClientAssetDependencyService.FindCandidates(index, path, token); }
                var rawCoverage = candidates.Values.SelectMany(value => value.Select(candidate => candidate.Provenance)).Distinct(StringComparer.OrdinalIgnoreCase).Select(provenance => new SceneProvenanceChoice(provenance, candidates.Count(pair => pair.Value.Count(candidate => candidate.Provenance.Equals(provenance, StringComparison.OrdinalIgnoreCase)) == 1), paths.Length)).OrderByDescending(value => value.ResolvedPaths).ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase).ToList();
                try { var profile = ProcessedAssetEffectiveProfileService.Infer(index, inspection.Path); var resolvedCount = paths.Count(path => ProcessedAssetEffectiveProfileService.Resolve(index, profile, path, token).State == ClientEffectiveAssetState.Effective); rawCoverage.Insert(0, new(profile.DisplayName, resolvedCount, paths.Length, profile)); } catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or FileNotFoundException) { }
                var coverage = rawCoverage.ToArray(); var chosen = paths.Length == 0 ? "Geometry only" : selectedProvenance; if (chosen is not null && paths.Length > 0 && !coverage.Any(value => value.Name.Equals(chosen, StringComparison.OrdinalIgnoreCase))) chosen = null;
                if (chosen is null)
                {
                    chosen = coverage.FirstOrDefault(value => value.EffectiveProfile is not null)?.Name;
                    if (chosen is null) try { var mapProvenance = ClientAssetDependencyService.InferLocation(index, inspection.Path).Provenance; chosen = coverage.FirstOrDefault(value => value.Name.Equals(mapProvenance, StringComparison.OrdinalIgnoreCase))?.Name; } catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException) { }
                    if (chosen is null && coverage.Length == 1) chosen = coverage[0].Name;
                }
                if (chosen is null) return new MapSceneBuildResult(index, coverage, null, null, null, [], [], requested.Length, totalPlacements - requested.Length, requested.Length, [coverage.Length == 0 ? "No placed-model or MTEX path exists in the selected processed library." : "Multiple provenance layers are available. Choose one explicitly and build again; Crucible will not mix them."]);
                var chosenProfile = coverage.FirstOrDefault(value => value.Name.Equals(chosen, StringComparison.OrdinalIgnoreCase))?.EffectiveProfile; var m2 = new List<MapSceneM2Instance>(); var wmo = new List<MapSceneWmoInstance>(); var findings = new List<string>(); var unresolved = requested.Count(value => string.IsNullOrWhiteSpace(value.Path)); AdtTerrainMaterialSet? materials = null;
                if (texturePaths.Length > 0 && chosen != "Geometry only")
                {
                    var resolvedTextures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var path in texturePaths)
                    {
                        var exact = Resolve(path); if (exact.State == ClientEffectiveAssetState.Effective && exact.Effective is not null) resolvedTextures[path] = exact.Effective.SourcePath;
                        else findings.Add($"Terrain texture {path}: {exact.Message}");
                    }
                    try { materials = AdtTerrainMaterialService.Load(inspection.Path, chosen, resolvedTextures, cancellationToken: token); findings.AddRange(materials.Findings); }
                    catch (Exception exception) when (exception is not OperationCanceledException) { findings.Add($"Terrain material composition unavailable: {exception.Message}"); }
                }
                foreach (var group in requested.Where(value => !string.IsNullOrWhiteSpace(value.Path)).GroupBy(value => value.Path!, StringComparer.OrdinalIgnoreCase))
                {
                    token.ThrowIfCancellationRequested(); var resolution = Resolve(group.Key); if (resolution.State != ClientEffectiveAssetState.Effective || resolution.Effective is null) { unresolved += group.Count(); findings.Add($"{group.Key}: {resolution.Message}"); continue; } var exact = resolution.Effective;
                    try
                    {
                        if (group.Any(value => value.Wmo is not null)) { var geometry = WmoPreviewGeometryService.Load(exact.SourcePath, cancellationToken: token); foreach (var value in group) if (value.Wmo is { } placement) wmo.Add(new(geometry, placement, exact.SourcePath)); }
                        else { var geometry = M2PreviewGeometryService.Load(exact.SourcePath); foreach (var value in group) if (value.M2 is { } placement) m2.Add(new(geometry, placement, exact.SourcePath)); }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException) { unresolved += group.Count(); findings.Add($"{group.Key}: {exception.Message}"); }
                }
                return new(index, coverage, chosen, terrain, materials, m2, wmo, requested.Length, totalPlacements - requested.Length, unresolved, findings);

                ProcessedAssetEffectiveResolution Resolve(string path)
                {
                    if (chosenProfile is not null) return ProcessedAssetEffectiveProfileService.Resolve(index, chosenProfile, path, token);
                    var exact = candidates[path].Where(candidate => candidate.Provenance.Equals(chosen, StringComparison.OrdinalIgnoreCase)).ToArray();
                    return exact.Length switch { 1 => new(path, ClientEffectiveAssetState.Effective, exact[0], exact, $"Resolved exact archive provenance {chosen}."), 0 => new(path, ClientEffectiveAssetState.Missing, null, [], $"Absent from exact archive provenance {chosen}."), _ => new(path, ClientEffectiveAssetState.Ambiguous, null, exact, $"{exact.Length:N0} physical candidates exist inside archive provenance {chosen}.") };
                }
            }, token);
            token.ThrowIfCancellationRequested(); _wmoLibraryIndex = result.Index; _sceneProvenance.ItemsSource = result.Choices; _sceneProvenance.SelectedItem = result.Provenance is null ? null : result.Choices.FirstOrDefault(value => value.Name.Equals(result.Provenance, StringComparison.OrdinalIgnoreCase));
            if (result.Terrain is null) { _mapScene.ClearScene(); _sceneStatus.Text = string.Join(" ", result.Findings); return; }
            _mapScene.SetScene(result.Terrain, result.Materials, result.M2, result.Wmo, result.UnresolvedPlacements, result.Provenance!); _visualTabs.SelectedIndex = 1;
            var materialText = result.Materials is null ? "diagnostic terrain color" : $"{result.Materials.CompleteCells:N0}/{result.Materials.Cells.Count:N0} complete MCLY/MCAL material cells";
            _sceneStatus.Text = $"Composed {result.Terrain.Cells.Count:N0} terrain cell(s), {result.M2.Count:N0} M2 instance(s), and {result.Wmo.Count:N0} WMO instance(s) from source profile {result.Provenance} · {materialText} · {result.UnresolvedPlacements:N0} unresolved placements · {result.OmittedPlacements:N0} omitted by load limit" + (result.Findings.Count == 0 ? "" : $" · {result.Findings.Count:N0} finding(s): {string.Join(" | ", result.Findings.Take(3))}");
        }
        catch (OperationCanceledException) { _sceneStatus.Text = "Map scene build cancelled."; }
        catch (Exception exception) { _sceneStatus.Text = $"Map scene build failed: {exception.Message}"; DesktopCrashLogger.Log("Map scene build failed", exception); }
        finally { button.IsEnabled = true; }
    }

    private async Task LoadWmoAsync(string path, MapWmoPlacement? placement)
    {
        _wmoOperation?.Cancel(); _wmoOperation?.Dispose(); _wmoOperation = new CancellationTokenSource(); var token = _wmoOperation.Token; _wmoStatus.Text = "Loading WMO root, groups, and available BLP materials…";
        try
        {
            var loaded = await Task.Run(() => { var geometry = WmoPreviewGeometryService.Load(path, cancellationToken: token); var textures = WmoPreviewGeometryService.LoadTextures(geometry, cancellationToken: token); return (geometry, textures); }, token); token.ThrowIfCancellationRequested();
            _m2Preview.ClearGeometry(); _m2Preview.IsVisible = false; _wmoPreview.IsVisible = true; _wmoPreview.SetGeometry(loaded.geometry); _wmoPreview.SetPlacement(placement); _wmoPreview.SetDecodedTextures(loaded.textures.Textures); _visualTabs.SelectedIndex = 2;
            var placementText = placement is null ? string.Empty : $" · UID {placement.UniqueId:N0} · pos {placement.Position.X:0.##},{placement.Position.Y:0.##},{placement.Position.Z:0.##} · rot {placement.Orientation.X:0.##},{placement.Orientation.Y:0.##},{placement.Orientation.Z:0.##} · scale {placement.Scale:0.###}";
            _wmoStatus.Text = $"{Path.GetFileName(loaded.geometry.RootPath)} · {loaded.geometry.Groups.Count:N0} groups · {loaded.geometry.TriangleIndices.Count / 3:N0} triangles · {loaded.textures.Textures.Count:N0}/{loaded.geometry.Materials.Count:N0} textures{placementText}" + (loaded.geometry.Findings.Count + loaded.textures.Findings.Count == 0 ? string.Empty : $" · {loaded.geometry.Findings.Count + loaded.textures.Findings.Count:N0} finding(s)");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _wmoStatus.Text = $"WMO preview unavailable: {exception.Message}"; DesktopCrashLogger.Log("Map WMO preview failed", exception); }
    }

    private async Task LoadM2Async(string path, MapM2Placement? placement)
    {
        _wmoOperation?.Cancel(); _wmoOperation?.Dispose(); _wmoOperation = new CancellationTokenSource(); var token = _wmoOperation.Token; _wmoStatus.Text = "Loading M2 and its WotLK SKIN geometry…";
        try
        {
            var geometry = await Task.Run(() => { token.ThrowIfCancellationRequested(); return M2PreviewGeometryService.Load(path); }, token); token.ThrowIfCancellationRequested();
            _wmoPreview.ClearGeometry(); _wmoPreview.IsVisible = false; _m2Preview.IsVisible = true; _m2Preview.SetGeometry(geometry);
            if (placement is not null) _m2Preview.SetSceneTransform(M2PreviewSceneService.MapObjectTransform(placement.Orientation, placement.Scale), $"MDDF UID {placement.UniqueId:N0} · rot {placement.Orientation.X:0.#},{placement.Orientation.Y:0.#},{placement.Orientation.Z:0.#} · scale {placement.Scale:0.###}");
            _visualTabs.SelectedIndex = 2;
            var placementText = placement is null ? string.Empty : $" · UID {placement.UniqueId:N0} · pos {placement.Position.X:0.##},{placement.Position.Y:0.##},{placement.Position.Z:0.##} · rot {placement.Orientation.X:0.##},{placement.Orientation.Y:0.##},{placement.Orientation.Z:0.##} · scale {placement.Scale:0.###}";
            _wmoStatus.Text = $"{Path.GetFileName(geometry.ModelPath)} · {geometry.Vertices.Count:N0} vertices · {geometry.TriangleIndices.Count / 3:N0} visible triangles · {geometry.TextureSlots.Count:N0} texture slots{placementText}";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _wmoStatus.Text = $"M2 preview unavailable: {exception.Message}"; DesktopCrashLogger.Log("Map M2 preview failed", exception); }
    }

    private static string Summary(MapAssetInspection value) => $"{Path.GetFileName(value.Path)}\n{value.Kind} · MVER {value.Version:N0}\nGrid {value.GridWidth:N0}×{value.GridHeight:N0} · {value.PresentCells:N0}/{value.Cells.Count:N0} present\nWorld tile {(value.TileX is null ? "not encoded by filename" : $"{value.TileX:N0},{value.TileY:N0}")}\nHeight {value.MinimumHeight?.ToString("0.###") ?? "-"} .. {value.MaximumHeight?.ToString("0.###") ?? "-"}\nHeader flags 0x{value.HeaderFlags:X}\n{value.TexturePaths.Count:N0} textures · {value.ModelPaths.Count:N0} M2 paths · {value.WmoPaths.Count:N0} WMO paths · {value.M2Placements.Count:N0} placed M2 instances · {value.WmoPlacements.Count:N0} placed WMO instances" + (value.Findings.Count == 0 ? "\nValidation: clean" : "\n" + string.Join("\n", value.Findings.Select(finding => "Review: " + finding)));
    private static string Describe(MapTileCell cell) => $"Grid {cell.X:N0},{cell.Y:N0}\nPresent: {cell.Present}\nFlags: 0x{cell.Flags:X}\nAsync ID: {cell.AsyncId:N0}\nArea ID: {cell.AreaId?.ToString("N0") ?? "-"}\nHoles: 0x{cell.Holes?.ToString("X") ?? "-"}\nHeight: {cell.MinimumHeight?.ToString("0.###") ?? "-"} .. {cell.MaximumHeight?.ToString("0.###") ?? "-"}";
    private static bool IsMap(string? path) => path is not null && File.Exists(path) && Path.GetExtension(path).ToLowerInvariant() is ".adt" or ".wdt" or ".wdl";
    private static TextBlock Info(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#AAB5C7") };
    private static TextBlock Label(string text) => new() { Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#7F8A9F") };
    private static StackPanel Field(string label, Control control) => new() { Spacing = 3, Children = { Label(label), control } };
    private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
    private static Border Card(Control child) => new() { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(1), Padding = new Thickness(9), Child = child };
    public void Dispose() { _operation?.Cancel(); _operation?.Dispose(); _operation = null; _wmoOperation?.Cancel(); _wmoOperation?.Dispose(); _wmoOperation = null; _lightingView.Dispose(); _wmoPreview.Dispose(); _m2Preview.Dispose(); }
}

internal sealed class MapGridControl : Control
{
    private MapAssetInspection? _inspection; private readonly HashSet<(int X, int Y)> _selected = [];
    private double? _brushX; private double? _brushY; private double? _brushRadius;
    public event EventHandler<IReadOnlyList<MapTileCell>>? CellsSelected;
    public event EventHandler<MapTerrainPoint>? TerrainPointSelected;
    public IReadOnlyList<MapTileCell> SelectedCells => _inspection is null ? [] : _inspection.Cells.Where(cell => _selected.Contains((cell.X, cell.Y))).ToArray();
    public MapGridControl() { ClipToBounds = true; }
    public void SetInspection(MapAssetInspection? inspection, IEnumerable<(int X, int Y)>? selection = null) { _inspection = inspection; _selected.Clear(); if (selection is not null) foreach (var cell in selection) _selected.Add(cell); Notify(); }
    public void SelectAllPresent() { _selected.Clear(); if (_inspection is not null) foreach (var cell in _inspection.Cells.Where(cell => cell.Present)) _selected.Add((cell.X, cell.Y)); Notify(); }
    public void SelectAllAbsent() { _selected.Clear(); if (_inspection?.Kind == MapAssetKind.Wdt) foreach (var cell in _inspection.Cells.Where(cell => !cell.Present)) _selected.Add((cell.X, cell.Y)); Notify(); }
    public void ClearSelection() { _selected.Clear(); Notify(); }
    public void SetBrush(double? x, double? y, double? radius) { _brushX = x; _brushY = y; _brushRadius = radius; InvalidateVisual(); }

    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(Brush.Parse("#080B10"), Bounds); if (_inspection is null) { DrawText(context, "Drop an ADT, WDT, or WDL file here", new Point(18, 30), Brush.Parse("#8995A9")); return; }
        var size = Math.Max(1, Math.Min(Bounds.Width, Bounds.Height) - 28); var left = (Bounds.Width - size) / 2; var top = (Bounds.Height - size) / 2; var cellSize = size / Math.Max(_inspection.GridWidth, _inspection.GridHeight);
        var min = _inspection.MinimumHeight; var max = _inspection.MaximumHeight;
        foreach (var cell in _inspection.Cells)
        {
            var rect = new Rect(left + cell.X * cellSize, top + cell.Y * cellSize, Math.Max(0.5, cellSize - 0.35), Math.Max(0.5, cellSize - 0.35));
            IBrush brush = cell.Present ? HeightBrush(cell, min, max) : Brush.Parse("#111722"); context.FillRectangle(brush, rect);
            if (cell.Holes is > 0 && cellSize >= 4) context.DrawRectangle(null, new Pen(Brush.Parse("#FFB84A"), Math.Max(0.6, cellSize * 0.08)), rect);
            if (_selected.Contains((cell.X, cell.Y))) context.DrawRectangle(null, new Pen(Brush.Parse("#FFFFFF"), Math.Max(1, cellSize * 0.13)), rect);
        }
        if (_inspection.Kind == MapAssetKind.Adt && _brushX is { } brushX && _brushY is { } brushY && _brushRadius is { } radius && radius > 0)
        {
            var center = new Point(left + brushX / _inspection.GridWidth * size, top + brushY / _inspection.GridHeight * size); var pixelRadius = radius / _inspection.GridWidth * size;
            context.DrawEllipse(Brush.Parse("#1838D7FF"), new Pen(Brush.Parse("#7ADFFF"), Math.Max(1, cellSize * 0.06)), center, pixelRadius, pixelRadius);
        }
        context.DrawRectangle(null, new Pen(Brush.Parse("#34415A"), 1), new Rect(left, top, size, size));
        DrawText(context, $"{_inspection.Kind} · {_inspection.PresentCells:N0} present · {_inspection.GridWidth}×{_inspection.GridHeight}", new Point(12, 18), Brush.Parse("#D8E2F1"));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); if (_inspection is null) return; var size = Math.Max(1, Math.Min(Bounds.Width, Bounds.Height) - 28); var left = (Bounds.Width - size) / 2; var top = (Bounds.Height - size) / 2; var point = e.GetPosition(this);
        var localX = (point.X - left) / size * _inspection.GridWidth; var localY = (point.Y - top) / size * _inspection.GridHeight; var x = (int)localX; var y = (int)localY;
        if (localX >= 0 && localX <= _inspection.GridWidth && localY >= 0 && localY <= _inspection.GridHeight && _inspection.Kind == MapAssetKind.Adt) TerrainPointSelected?.Invoke(this, new(Math.Clamp(localX, 0, 16), Math.Clamp(localY, 0, 16)));
        var cell = x >= 0 && x < _inspection.GridWidth && y >= 0 && y < _inspection.GridHeight ? _inspection.Cells.FirstOrDefault(candidate => candidate.X == x && candidate.Y == y && (candidate.Present || _inspection.Kind == MapAssetKind.Wdt)) : null;
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) _selected.Clear();
        if (cell is not null && !_selected.Add((cell.X, cell.Y))) _selected.Remove((cell.X, cell.Y)); Notify(); e.Handled = true;
    }

    private static IBrush HeightBrush(MapTileCell cell, float? minimum, float? maximum)
    {
        if (cell.MinimumHeight is null || cell.MaximumHeight is null || minimum is null || maximum is null || maximum <= minimum) return Brush.Parse("#2B7A78");
        var midpoint = (cell.MinimumHeight.Value + cell.MaximumHeight.Value) * 0.5f; var amount = Math.Clamp((midpoint - minimum.Value) / (maximum.Value - minimum.Value), 0, 1);
        var low = Color.Parse("#164E63"); var high = Color.Parse("#B6D369"); byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
        return new SolidColorBrush(Color.FromArgb(255, Mix(low.R, high.R), Mix(low.G, high.G), Mix(low.B, high.B)));
    }
    private static void DrawText(DrawingContext context, string text, Point point, IBrush brush) => context.DrawText(new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, brush), point);
    private void Notify() { CellsSelected?.Invoke(this, SelectedCells); InvalidateVisual(); }
}

internal sealed record MapTerrainPoint(double X, double Y);
internal sealed record MapBrushModeChoice(string Label, AdtTerrainBrushMode Value) { public override string ToString() => Label; }
internal sealed record MapTextureChoice(uint Id, string Path) { public override string ToString() => $"{Id} · {Path}"; }
