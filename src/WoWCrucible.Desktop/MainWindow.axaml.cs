using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WoWCrucible.Core;
using WoWCrucible.Desktop.Controls;

namespace WoWCrucible.Desktop;

public partial class MainWindow : Window
{
    private sealed record DbcDragPayload(DbcDocumentSession Document, Controls.DbcRangeSelection Selection);
    private static readonly DataFormat<DbcDragPayload> DbcRangeFormat = DataFormat.CreateInProcessFormat<DbcDragPayload>("wowcrucible-dbc-range-v1");
    private readonly List<DbcDocumentSession> _documents = [];
    private int _primaryDocument = -1;
    private int _secondaryDocument = -1;
    private bool _secondaryPaneActive;
    private CancellationTokenSource? _searchCancellation;
    private long _lastRenderReport;
    private bool _closingApproved;
    private bool _closingPromptActive;
    private readonly object _schemaGate = new();
    private DbcSchemaCatalog? _schemaCatalog;
    private string _schemaSource = "Built-in 12340 definitions";
    private bool _syncingScrollbars;
    private Controls.DbcSelectionEventArgs? _dbcEditingSelection;
    private VirtualDbcView? _dbcEditingView;
    private TextBox? _dbcEditingEditor;
    private bool _dbcEditorClosing;
    private readonly DesktopWorkspaceSession _workspaceSession = new(DesktopSettings.Load());
    private AssetComparisonView? _assetComparisonView;
    private NativeConversionWorkspaceView? _nativeConversionWorkspaceView;
    private KnowledgeWorkspaceView? _knowledgeWorkspaceView;
    private ToolInventoryView? _toolInventoryView;
    private ItemWorkbenchView? _itemWorkbenchView;
    private MpqWorkspaceView? _mpqWorkspaceView;
    private ClientWorkspaceView? _clientWorkspaceView;
    private TextureWorkspaceView? _textureWorkspaceView;
    private MapWorkspaceView? _mapWorkspaceView;
    private LayeredDbcWorkspaceView? _layeredDbcWorkspaceView;
    private DbdSchemaAuditView? _dbdSchemaAuditView;
    private CacheTableWorkspaceView? _cacheTableWorkspaceView;
    private ProjectWorkspaceView? _projectWorkspaceView;
    private DbcExportWorkspaceView? _dbcExportWorkspaceView;
    private DbcImportWorkspaceView? _dbcImportWorkspaceView;
    private CreatureWorkspaceView? _creatureWorkspaceView;
    private GameObjectWorkspaceView? _gameObjectWorkspaceView;
    private QuestWorkspaceView? _questWorkspaceView;
    private BehaviorWorkspaceView? _behaviorWorkspaceView;
    private PetLevelCurveView? _petLevelCurveView;
    private PetAbilityGraphView? _petAbilityGraphView;
    private ServerSqlWorkspaceView? _serverSqlWorkspaceView;
    private SqlWorkspaceView? _sqlWorkspaceView;
    private WorkspaceSetupView? _workspaceSetupView;
    private readonly Stack<(Control Workspace, string Title)> _featureHistory = new();
    private string _featureTitle = string.Empty;
    private readonly Dictionary<string, (WdbcFile File, IReadOnlyList<DbcColumn> Columns)> _referenceDbcCache = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<CrucibleCommandMatch> _commandMatches = [];
    private string _knowledgeContext = string.Empty;
    private readonly IReadOnlyDictionary<string, Func<Task>> _commandRoutes;
    private IReadOnlyList<(Control Control, int CharacterIndex)> _uiFindMatches = [];
    private int _uiFindMatchIndex = -1;
    private bool _backupChoiceMadeThisSession;

    private int ActiveDocumentIndex => _secondaryPaneActive && SecondaryDbcPane.IsVisible ? _secondaryDocument : _primaryDocument;
    private VirtualDbcView ActiveDbcView => _secondaryPaneActive && SecondaryDbcPane.IsVisible ? SecondaryDbcView : DbcView;
    private TextBox ActiveDbcInlineEditor => _secondaryPaneActive && SecondaryDbcPane.IsVisible ? SecondaryDbcInlineEditor : DbcInlineEditor;
    private DbcDocumentSession? Current => ActiveDocumentIndex >= 0 && ActiveDocumentIndex < _documents.Count ? _documents[ActiveDocumentIndex] : null;
    private WdbcFile? CurrentFile => Current?.File;
    private IReadOnlyList<DbcColumn> CurrentColumns => Current?.Schema.Columns ?? [];

    public MainWindow()
    {
        InitializeComponent();
        var buildIdentity = ReadBuildIdentity();
        BuildIdentityText.Text = buildIdentity.Label;
        ToolTip.SetTip(BuildIdentityText, $"{buildIdentity.FullVersion}\nRunning from {AppContext.BaseDirectory}");
        Title = $"WoW Crucible · {buildIdentity.Label}";
        DevbugModeToggle.IsChecked = DesktopCrashLogger.IsDevbugEnabled;
        RuntimeStrip.Attach(_workspaceSession);
        RuntimeStrip.WorkspaceRequested += (_, _) => OpenWorkspaceSetup();
        RuntimeStrip.FeatureBackRequested += (_, _) => CloseFeatureWorkspace();
        RuntimeStrip.CommandRequested += async (_, command) => await ExecuteRuntimeCommandAsync(command);
        RefreshShellContext();
        _commandRoutes = BuildCommandRoutes();
        var unrouted = CrucibleCommandCatalog.All.Where(command => !_commandRoutes.ContainsKey(command.Id)).Select(command => command.Id).ToArray();
        if (unrouted.Length > 0 || _commandRoutes.Count != CrucibleCommandCatalog.All.Count) throw new InvalidOperationException($"Desktop command routes do not exactly match the shared catalog. Missing: {string.Join(", ", unrouted)}");
        CommandPaletteResults.ItemTemplate = new FuncDataTemplate<CrucibleCommandMatch>((match, _) => match is null ? new Grid() : BuildCommandPaletteRow(match.Command));
        CommandPaletteSearch.TextChanged += (_, _) => RefreshCommandPalette();
        CommandPaletteResults.DoubleTapped += async (_, _) => await ExecuteSelectedCommandAsync();
        DesktopCrashLogger.Debug("UI", "main-window-created", ("devbug", DesktopCrashLogger.IsDevbugEnabled), ("build", buildIdentity.FullVersion), ("base_directory", AppContext.BaseDirectory));
        AttachDbcPane(DbcView, DbcInlineEditor);
        AttachDbcPane(SecondaryDbcView, SecondaryDbcInlineEditor);
        DbcView.RenderMeasured += (_, measurement) =>
        {
            var now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(_lastRenderReport, now).TotalMilliseconds < 500) return;
            _lastRenderReport = now;
            Dispatcher.UIThread.Post(() =>
            {
                RenderText.Text = $"Render {measurement.Milliseconds:0.00} ms · {measurement.VisibleRows} × {measurement.VisibleColumns} visible";
                SyncScrollbars();
            }, DispatcherPriority.Background);
        };
        SecondaryDbcView.RenderMeasured += (_, _) => Dispatcher.UIThread.Post(SyncSecondaryScrollbars, DispatcherPriority.Background);
        Closing += WindowClosing;
        Closed += (_, _) => { RuntimeStrip.Dispose(); _assetComparisonView?.Dispose(); _nativeConversionWorkspaceView?.Dispose(); _dbcExportWorkspaceView?.Dispose(); _dbcImportWorkspaceView?.Dispose(); _projectWorkspaceView?.Dispose(); _itemWorkbenchView?.Dispose(); _mpqWorkspaceView?.Dispose(); _clientWorkspaceView?.Dispose(); _textureWorkspaceView?.Dispose(); _mapWorkspaceView?.Dispose(); _layeredDbcWorkspaceView?.Dispose(); _creatureWorkspaceView?.Dispose(); _gameObjectWorkspaceView?.Dispose(); _questWorkspaceView?.Dispose(); _behaviorWorkspaceView?.Dispose(); _petLevelCurveView?.Dispose(); _serverSqlWorkspaceView?.Dispose(); _sqlWorkspaceView?.Dispose(); _workspaceSession.Dispose(); };
        if (Directory.Exists(_workspaceSession.Settings.WorkspaceRootPath) || Directory.Exists(_workspaceSession.Settings.ServerRootPath)) Dispatcher.UIThread.Post(async () => await RestoreWorkspaceSessionAsync(), DispatcherPriority.Background);
    }

    private void AttachDbcPane(VirtualDbcView view, TextBox editor)
    {
        view.GotFocus += (_, _) => ActivateDbcPane(view);
        view.SelectionChanged += (_, selection) => { ActivateDbcPane(view); ShowSelection(selection); };
        view.CellEditRequested += async (_, request) => { ActivateDbcPane(view); if (await EnsureBackupChoiceAsync()) BeginInlineCellEdit(view, editor, request); };
        view.RangeDragRequested += async (_, request) => await StartDbcRangeDragAsync(view, request);
        editor.KeyDown += DbcInlineEditorKeyDown;
        editor.LostFocus += (_, _) =>
        {
            if (editor.IsVisible && !_dbcEditorClosing) _ = CommitInlineCellEdit(null);
        };
        DragDrop.SetAllowDrop(view, true);
        DragDrop.AddDragOverHandler(view, (_, e) =>
        {
            var payload = e.DataTransfer.TryGetValue(DbcRangeFormat);
            e.DragEffects = payload is not null && !ReferenceEquals(payload.Document, DocumentForView(view)) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        });
        DragDrop.AddDropHandler(view, async (_, e) =>
        {
            var payload = e.DataTransfer.TryGetValue(DbcRangeFormat);
            if (payload is null) return;
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
            await ApplyDbcRangeDropAsync(payload, view, view.SourceRowAt(e.GetPosition(view)));
        });
    }

    private void ActivateDbcPane(VirtualDbcView view)
    {
        _secondaryPaneActive = ReferenceEquals(view, SecondaryDbcView) && SecondaryDbcPane.IsVisible;
        var active = ActiveDocumentIndex;
        if (active < 0 || active >= _documents.Count) return;
        PrimaryDbcPane.BorderBrush = Brush.Parse(_secondaryPaneActive ? "#39455A" : "#C58A2B");
        SecondaryDbcPane.BorderBrush = Brush.Parse(_secondaryPaneActive ? "#C58A2B" : "#39455A");
        ShowDocumentSummary(_documents[active]);
        RefreshTabs();
    }

    private DbcDocumentSession? DocumentForView(VirtualDbcView view)
    {
        var index = ReferenceEquals(view, SecondaryDbcView) ? _secondaryDocument : _primaryDocument;
        return index >= 0 && index < _documents.Count ? _documents[index] : null;
    }

    private async Task StartDbcRangeDragAsync(VirtualDbcView sourceView, Controls.DbcRangeDragRequestEventArgs request)
    {
        var document = DocumentForView(sourceView);
        if (document is null || request.Selection.SourceRows.Count == 0 || request.Selection.ColumnIndices.Count == 0) return;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(DbcRangeFormat, new DbcDragPayload(document, request.Selection)));
        var rows = request.Selection.SourceRows.Count;
        var columns = request.Selection.ColumnIndices.Count;
        data.Add(DataTransferItem.CreateText($"{document.File.LogicalTableName}: {rows:N0} row(s) × {columns:N0} column(s)"));
        DesktopCrashLogger.Debug("DBC", "range-drag-start", ("path", document.FullPath), ("rows", rows), ("columns", columns));
        await DragDrop.DoDragDropAsync(request.Trigger, data, DragDropEffects.Copy);
    }

    private async Task ApplyDbcRangeDropAsync(DbcDragPayload payload, VirtualDbcView targetView, int targetRow)
    {
        var target = DocumentForView(targetView);
        if (target is null || ReferenceEquals(target, payload.Document)) return;
        if (!await EnsureBackupChoiceAsync()) return;
        try
        {
            var result = DbcRangeTransferService.Transfer(payload.Document.File, payload.Document.Schema.Columns, payload.Document.Schema.KeyStrategy,
                payload.Selection.SourceRows, payload.Selection.ColumnIndices, target.File, target.Schema.Columns, target.Schema.KeyStrategy, targetRow);
            target.History.Clear();
            targetView.RefreshDocument(Math.Min(Math.Max(0, targetRow), Math.Max(0, target.File.RowCount - 1)));
            RefreshTabs();
            var remaps = result.RemappedIds.Count == 0 ? string.Empty : $" · {result.RemappedIds.Count:N0} conflicting ID(s) allocated new IDs: {string.Join(", ", result.RemappedIds.Take(4).Select(pair => $"{pair.Key}→{pair.Value}"))}";
            var unmapped = result.UnmappedColumns.Count == 0 ? string.Empty : $" · {result.UnmappedColumns.Count:N0} incompatible column(s) skipped";
            StatusText.Text = $"Transferred {result.SourceRows:N0} row(s) × {result.SelectedColumns:N0} column(s): {result.AddedRows:N0} added, {result.UpdatedRows:N0} matched, {result.ChangedCells:N0} cells changed{remaps}{unmapped}";
            DesktopCrashLogger.Debug("DBC", "range-drop-applied", ("source", payload.Document.FullPath), ("target", target.FullPath), ("rows", result.SourceRows), ("columns", result.SelectedColumns), ("added", result.AddedRows), ("updated", result.UpdatedRows), ("changed_cells", result.ChangedCells), ("id_remaps", result.RemappedIds.Count));
        }
        catch (Exception exception)
        {
            DesktopCrashLogger.Log("DBC range transfer failed", exception);
            await ShowErrorAsync("Could not transfer DBC selection", exception.Message);
        }
    }

    private static (string Label, string FullVersion) ReadBuildIdentity()
    {
        var assembly = typeof(MainWindow).Assembly;
        var fullVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                          ?? assembly.GetName().Version?.ToString()
                          ?? "unknown";
        var separator = fullVersion.LastIndexOf('+');
        var revision = separator >= 0 && separator + 1 < fullVersion.Length ? fullVersion[(separator + 1)..] : string.Empty;
        var label = revision.Length >= 7 ? $"BUILD {revision[..7]}" : $"BUILD {fullVersion}";
        return (label, fullVersion);
    }

    private void DevbugModeChanged(object? sender, RoutedEventArgs e)
    {
        var enabled = DevbugModeToggle.IsChecked == true;
        DesktopCrashLogger.SetDevbugMode(enabled);
        StatusText.Text = enabled
            ? $"Devbug Mode enabled · live terminal + {DesktopCrashLogger.DebugLogPath}"
            : "Devbug Mode disabled · normal mode only records failures";
    }

    private void ToggleNavigationPaneClick(object? sender, RoutedEventArgs e)
    {
        _workspaceSession.Settings.NavigationPaneOpen = !_workspaceSession.Settings.NavigationPaneOpen;
        ApplyShellPaneState();
        _workspaceSession.Settings.Save();
        StatusText.Text = _workspaceSession.Settings.NavigationPaneOpen ? "Workspace pane restored · drag its divider to choose any width" : "Workspace pane hidden · the editor now owns that space";
    }

    private void ToggleInspectorPaneClick(object? sender, RoutedEventArgs e)
    {
        _workspaceSession.Settings.InspectorPaneOpen = !_workspaceSession.Settings.InspectorPaneOpen;
        ApplyShellPaneState();
        _workspaceSession.Settings.Save();
        StatusText.Text = _workspaceSession.Settings.InspectorPaneOpen ? "Inspector pane restored · drag its divider to choose any width" : "Inspector pane hidden · the editor now owns that space";
    }

    private void ApplyShellPaneState()
    {
        var shellVisible = !FeatureWorkspaceHost.IsVisible;
        var navigationVisible = shellVisible && _workspaceSession.Settings.NavigationPaneOpen;
        var inspectorVisible = shellVisible && _workspaceSession.Settings.InspectorPaneOpen && !WelcomePanel.IsVisible;
        NavigationPane.IsVisible = NavigationSplitter.IsVisible = navigationVisible;
        InspectorPane.IsVisible = InspectorSplitter.IsVisible = inspectorVisible;
        RootLayout.ColumnDefinitions[0].Width = navigationVisible ? new GridLength(1.1, GridUnitType.Star) : new GridLength(0);
        RootLayout.ColumnDefinitions[1].Width = navigationVisible ? GridLength.Auto : new GridLength(0);
        RootLayout.ColumnDefinitions[3].Width = inspectorVisible ? GridLength.Auto : new GridLength(0);
        RootLayout.ColumnDefinitions[4].Width = inspectorVisible ? new GridLength(1.5, GridUnitType.Star) : new GridLength(0);
        NavigationPaneButton.Content = _workspaceSession.Settings.NavigationPaneOpen ? "Hide workspace pane" : "Show workspace pane";
        InspectorPaneButton.Content = _workspaceSession.Settings.InspectorPaneOpen ? "Hide inspector pane" : "Show inspector pane";
    }

    private void RefreshShellContext()
    {
        var editingDbc = DbcHost.IsVisible && Current is not null;
        DbcQuickActions.IsVisible = editingDbc;
        DbcDocumentToolbar.IsVisible = editingDbc;
        ApplyShellPaneState();
    }

    private void ShowHome()
    {
        CloseAllFeatureWorkspaces();
        DbcHost.IsVisible = false;
        M2View.IsVisible = false;
        WelcomePanel.IsVisible = true;
        InspectorTitle.Text = "Nothing selected";
        InspectorSummary.Text = "Choose a job from the start page.";
        InspectorDetail.Text = "Specialized tools are grouped in the workspace pane, and staged DBC tabs remain open.";
        RefreshShellContext();
        StatusText.Text = _documents.Count == 0 ? "Ready" : $"Home · {_documents.Count:N0} staged DBC tab(s) remain open";
    }

    public Task LoadPathAsync(string path)
    {
        DesktopCrashLogger.Debug("FILE", "load-path-requested", ("path", path));
        var extension = Path.GetExtension(path);
        return extension.Equals(".dbc", StringComparison.OrdinalIgnoreCase) || extension.Equals(".db2", StringComparison.OrdinalIgnoreCase)
            ? LoadDbcAsync(path)
            : extension.Equals(".m2", StringComparison.OrdinalIgnoreCase)
                ? LoadM2Async(path)
                : extension.Equals(".blp", StringComparison.OrdinalIgnoreCase)
                    ? OpenTextureWorkspaceAsync(path)
                    : extension.Equals(".adt", StringComparison.OrdinalIgnoreCase) || extension.Equals(".wdt", StringComparison.OrdinalIgnoreCase) || extension.Equals(".wdl", StringComparison.OrdinalIgnoreCase)
                    ? OpenMapWorkspaceAsync(path)
                    : extension.Equals(".wdb", StringComparison.OrdinalIgnoreCase) || extension.Equals(".adb", StringComparison.OrdinalIgnoreCase)
                        ? OpenCacheTableAsync(path)
            : ShowErrorAsync("Unsupported file", "The desktop opens DBC, WDB2 DB2, WDB/ADB client caches, M2, BLP, ADT, WDT, and WDL files directly.");
    }

    private async void OpenDbcClick(object? sender, RoutedEventArgs e)
    {
        var start = Directory.Exists(_workspaceSession.Settings.CoreDbcPath)
            ? await StorageProvider.TryGetFolderFromPathAsync(_workspaceSession.Settings.CoreDbcPath)
            : null;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open one or more WDBC or WDB2 client tables",
            AllowMultiple = true,
            SuggestedStartLocation = start,
            FileTypeFilter = [new FilePickerFileType("WoW client tables") { Patterns = ["*.dbc", "*.db2"] }]
        });
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path is not null) await LoadDbcAsync(path);
        }
    }

    private async Task LoadDbcAsync(string path)
    {
        path = Path.GetFullPath(path);
        var existing = _documents.FindIndex(document => document.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) { DesktopCrashLogger.Debug("DBC", "open-reused-staged-document", ("path", path), ("tab", existing)); ActivateDocument(existing); return; }
        SetBusy($"Loading {Path.GetFileName(path)}…");
        var stopwatch = Stopwatch.StartNew();
        DesktopCrashLogger.Debug("DBC", "open-start", ("path", path), ("bytes", new FileInfo(path).Length));
        try
        {
            var session = await Task.Run(() =>
            {
                var file = WdbcFile.Load(path);
                var tableName = file.LogicalTableName;
                if (file.ContainerKind == ClientTableContainerKind.Wdb2)
                {
                    var definitions = FindDbdDefinitionsPath() ?? throw new DirectoryNotFoundException("Opening WDB2 requires the WoWDBDefs definitions folder. Configure it under DBD schemas & audit.");
                    var definition = Path.Combine(definitions, tableName + ".dbd");
                    if (!File.Exists(definition)) throw new FileNotFoundException($"No WoWDBDefs definition exists for {tableName}.db2.", definition);
                    var build = file.Db2Metadata?.Build ?? throw new InvalidDataException("WDB2 build metadata is missing.");
                    var db2Resolution = DbdSchemaService.ResolveFile(definition, build, file.FieldCount, file.RecordSize);
                    return new DbcDocumentSession(file, db2Resolution, $"{definition} · build {build}");
                }
                var catalog = ResolveSchemaCatalog(); var xmlResolution = catalog.ResolveColumns(tableName, file.FieldCount);
                return new DbcDocumentSession(file, xmlResolution, _schemaSource);
            });
            _documents.Add(session);
            ActivateDocument(_documents.Count - 1);
            StatusText.Text = $"Loaded {session.File.RowCount:N0} records in {stopwatch.Elapsed.TotalMilliseconds:0} ms · {_documents.Count:N0} staged file(s)";
            DesktopCrashLogger.Debug("DBC", "open-success", ("path", path), ("rows", session.File.RowCount), ("fields", session.File.FieldCount), ("schema", session.Schema.MatchKind), ("duration_ms", stopwatch.Elapsed.TotalMilliseconds));
        }
        catch (Exception exception)
        {
            DesktopCrashLogger.Log("DBC open failed", exception);
            StatusText.Text = "Open failed";
            await ShowErrorAsync("Could not open client table", exception.Message);
        }
    }

    private void ActivateDocument(int index)
    {
        if (index < 0 || index >= _documents.Count) return;
        CancelInlineCellEdit();
        if (SecondaryDbcPane.IsVisible && _secondaryPaneActive) _secondaryDocument = index;
        else { _primaryDocument = index; _secondaryPaneActive = false; }
        var document = _documents[index];
        DesktopCrashLogger.Debug("DBC", "document-activated", ("path", document.FullPath), ("tab", index), ("dirty", document.File.IsDirty));
        SearchBox.Text = string.Empty;
        ActiveDbcView.SetDocument(document.File, document.Schema.Columns, document.Schema.KeyStrategy, document.File.LogicalTableName, DecodedToggle.IsChecked == true);
        UpdateDbcPaneTitles();
        WelcomePanel.IsVisible = false;
        M2View.IsVisible = false;
        DbcHost.IsVisible = true;
        ShowDocumentSummary(document);
        RefreshTabs();
        RefreshShellContext();
    }

    private void RefreshTabs()
    {
        DocumentTabsPanel.Children.Clear();
        for (var index = 0; index < _documents.Count; index++)
        {
            var captured = index;
            var locations = (index == _primaryDocument ? "L" : string.Empty) + (SecondaryDbcPane.IsVisible && index == _secondaryDocument ? "R" : string.Empty);
            var active = index == ActiveDocumentIndex;
            var button = new Button
            {
                Content = locations.Length == 0 ? _documents[index].DisplayName : $"{locations} · {_documents[index].DisplayName}",
                Padding = new Thickness(14, 9),
                CornerRadius = new CornerRadius(0),
                Background = active ? new SolidColorBrush(Color.Parse("#202B3C")) : Brushes.Transparent,
                BorderBrush = active ? new SolidColorBrush(Color.Parse("#C58A2B")) : Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, active ? 2 : 0)
            };
            button.Click += (_, _) => ActivateDocument(captured);
            DocumentTabsPanel.Children.Add(button);
        }
    }

    private void UpdateDbcPaneTitles()
    {
        PrimaryDbcPaneTitle.Text = _primaryDocument >= 0 && _primaryDocument < _documents.Count ? $"LEFT · {_documents[_primaryDocument].DisplayName}" : "LEFT · choose an open tab";
        SecondaryDbcPaneTitle.Text = _secondaryDocument >= 0 && _secondaryDocument < _documents.Count ? $"RIGHT · {_documents[_secondaryDocument].DisplayName}" : "RIGHT · click here, then choose an open tab";
    }

    private void SplitDbcClick(object? sender, RoutedEventArgs e)
    {
        var enabled = SplitDbcToggle.IsChecked == true;
        SecondaryDbcPane.IsVisible = DbcSplitDivider.IsVisible = enabled;
        DbcHost.ColumnDefinitions[2].Width = enabled ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        if (enabled)
        {
            if (_secondaryDocument < 0 || _secondaryDocument >= _documents.Count || _secondaryDocument == _primaryDocument)
                _secondaryDocument = Enumerable.Range(0, _documents.Count).FirstOrDefault(index => index != _primaryDocument, _primaryDocument);
            if (_secondaryDocument >= 0 && _secondaryDocument < _documents.Count)
            {
                var document = _documents[_secondaryDocument];
                SecondaryDbcView.SetDocument(document.File, document.Schema.Columns, document.Schema.KeyStrategy, document.File.LogicalTableName, DecodedToggle.IsChecked == true);
            }
            StatusText.Text = "Split DBC view enabled · click either pane to make it active, then click a staged tab · drag selected rows/cells across panes";
        }
        else
        {
            _secondaryPaneActive = false;
            CancelInlineCellEdit();
            StatusText.Text = "Single DBC view restored · both staged documents remain open";
        }
        UpdateDbcPaneTitles(); RefreshTabs(); ActivateDbcPane(ActiveDbcView); SyncScrollbars(); SyncSecondaryScrollbars();
    }

    private void ShowDocumentSummary(DbcDocumentSession document)
    {
        InspectorTitle.Text = Path.GetFileName(document.File.SourcePath);
        InspectorSummary.Text = $"{document.File.RowCount:N0} records · {document.File.FieldCount:N0} fields";
        var db2 = document.File.Db2Metadata;
        InspectorDetail.Text = $"Container  {document.File.ContainerKind.ToString().ToUpperInvariant()}\nRecord     {document.File.RecordSize:N0} bytes\nStrings    {document.File.StringTableSize:N0} bytes{(db2 is null ? string.Empty : $"\nBuild      {db2.Build:N0}\nTable hash 0x{db2.TableHash:X8}\nID range   {db2.MinId:N0}..{db2.MaxId:N0}\nCopy rows  {db2.CopyRows:N0}\nStructural {(document.File.AllowsStructuralMutation ? "editable" : "locked by side tables")}")}\nSchema     {document.Schema.MatchKind}\nDefinition {document.SchemaSource}\nRow key    {document.Schema.KeyStrategy.DisplayName(document.Schema.Columns)}\nSource     {document.File.SourcePath}";
    }

    private async void SaveClick(object? sender, RoutedEventArgs e) => await SaveCurrentAsync(false);
    private async void SaveAsClick(object? sender, RoutedEventArgs e) => await SaveCurrentAsync(true);
    private void OpenDbcExportClick(object? sender, RoutedEventArgs e)
    {
        var document = Current;
        if (document is null) { StatusText.Text = "Open or select a DBC before exporting rows."; return; }
        _dbcExportWorkspaceView?.Dispose(); var view = _dbcExportWorkspaceView = new DbcExportWorkspaceView(document);
        view.BackRequested += (_, _) => { view.Dispose(); if (ReferenceEquals(_dbcExportWorkspaceView, view)) _dbcExportWorkspaceView = null; CloseFeatureWorkspace(); };
        OpenFeatureWorkspace(view, $"Export {Path.GetFileName(document.File.SourcePath)}");
    }

    private void OpenDbcImportClick(object? sender, RoutedEventArgs e)
    {
        var document = Current;
        if (document is null) { StatusText.Text = "Open or select a DBC before importing structured rows."; return; }
        _dbcImportWorkspaceView?.Dispose(); var view = _dbcImportWorkspaceView = new DbcImportWorkspaceView(document);
        view.BackRequested += (_, _) => { view.Dispose(); if (ReferenceEquals(_dbcImportWorkspaceView, view)) _dbcImportWorkspaceView = null; CloseFeatureWorkspace(); };
        view.Applied += (_, result) =>
        {
            ActiveDbcView.SetDocument(document.File, document.Schema.Columns, document.Schema.KeyStrategy, Path.GetFileNameWithoutExtension(document.File.SourcePath), DecodedToggle.IsChecked == true);
            ShowDocumentSummary(document); RefreshTabs();
            StatusText.Text = $"Structured import staged · {result.UpdatedRows:N0} updated row(s) · {result.AppendedRows:N0} appended · {result.ChangedCells:N0} cells · save still required";
        };
        OpenFeatureWorkspace(view, $"Import {Path.GetFileName(document.File.SourcePath)}");
    }

    private async Task<bool> SaveCurrentAsync(bool saveAs)
    {
        var document = Current;
        if (document is null) return false;
        if (!await EnsureBackupChoiceAsync()) return false;
        var path = document.File.SourcePath;
        if (saveAs)
        {
            var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save DBC as",
                SuggestedFileName = Path.GetFileName(path),
                FileTypeChoices = [new FilePickerFileType("WoW client table") { Patterns = [Path.GetExtension(path)] }]
            });
            path = destination?.TryGetLocalPath();
            if (path is null) return false;
            var fullDestination = Path.GetFullPath(path);
            if (_documents.Any(other => !ReferenceEquals(other, document) && other.FullPath.Equals(fullDestination, StringComparison.OrdinalIgnoreCase)))
            {
                await ShowErrorAsync("DBC already staged", "Another open document already uses that destination. Close it or choose a different path before Save As.");
                return false;
            }
        }
        SetBusy(CrucibleBackupService.Enabled ? "Saving table atomically with a bounded safety backup…" : "Saving table atomically · retained backups are disabled…");
        var stopwatch = Stopwatch.StartNew();
        DesktopCrashLogger.Debug("DBC", "save-start", ("source", document.FullPath), ("destination", path), ("save_as", saveAs), ("dirty", document.File.IsDirty));
        try
        {
            if (saveAs) await Task.Run(() => document.File.SaveAs(path, true));
            else await Task.Run(() => document.File.Save(path, true));
            RefreshTabs();
            var backup = document.File.LastBackupPath;
            StatusText.Text = backup is null ? $"Saved {path} · no retained backup · {CrucibleBackupService.LastDecision}" : $"Saved {path} · backup {backup}";
            DesktopCrashLogger.Debug("DBC", "save-success", ("path", path), ("rows", document.File.RowCount), ("duration_ms", stopwatch.Elapsed.TotalMilliseconds), ("backup", backup));
            return true;
        }
        catch (Exception exception)
        {
            DesktopCrashLogger.Log("DBC save failed", exception);
            await ShowErrorAsync("Could not save DBC", exception.Message);
            return false;
        }
    }

    private async void CloseDocumentClick(object? sender, RoutedEventArgs e) => await CloseCurrentDocumentAsync();

    private async Task CloseCurrentDocumentAsync()
    {
        var document = Current;
        if (document is null) return;
        if (document.File.IsDirty)
        {
            var choice = await PromptSaveAsync(document.DisplayName.TrimEnd(' ', '*'));
            if (choice == SaveChoice.Cancel) return;
            if (choice == SaveChoice.Save && !await SaveCurrentAsync(false)) return;
        }
        var closedIndex = ActiveDocumentIndex;
        _documents.RemoveAt(closedIndex);
        DesktopCrashLogger.Debug("DBC", "document-closed", ("path", document.FullPath), ("remaining", _documents.Count));
        _primaryDocument = ReindexDocumentAfterClose(_primaryDocument, closedIndex);
        _secondaryDocument = ReindexDocumentAfterClose(_secondaryDocument, closedIndex);
        if (_documents.Count > 0)
        {
            if (_primaryDocument < 0) _primaryDocument = Math.Min(closedIndex, _documents.Count - 1);
            if (_secondaryDocument < 0) _secondaryDocument = Enumerable.Range(0, _documents.Count).FirstOrDefault(index => index != _primaryDocument, _primaryDocument);
            ActivateDocument(_secondaryPaneActive ? _secondaryDocument : _primaryDocument);
        }
        else
        {
            _primaryDocument = _secondaryDocument = -1;
            SplitDbcToggle.IsChecked = false;
            SecondaryDbcPane.IsVisible = DbcSplitDivider.IsVisible = false;
            DbcHost.ColumnDefinitions[2].Width = new GridLength(0);
            DbcHost.IsVisible = false;
            M2View.IsVisible = false;
            WelcomePanel.IsVisible = true;
            SearchBox.Text = string.Empty;
            RefreshTabs();
            InspectorTitle.Text = "Nothing selected";
            InspectorSummary.Text = "Open a table or model to begin.";
            InspectorDetail.Text = "Table metadata and selection details appear here.";
            RefreshShellContext();
        }
    }

    private int ReindexDocumentAfterClose(int index, int closedIndex) => index == closedIndex ? -1 : index > closedIndex ? index - 1 : index;

    private void ShowSelection(Controls.DbcSelectionEventArgs selection)
    {
        var document = Current;
        if (document is null) return;
        var semantic = DbcSemanticCatalog.Get(Path.GetFileNameWithoutExtension(document.File.SourcePath), selection.Column.Index, document.File, selection.Row);
        string recordId;
        try { recordId = document.Schema.KeyStrategy.Kind == DbcRecordKeyKind.NoStableKey ? "Unavailable (schema has no stable key)" : DbcRecordIdentity.GetKey(document.File, selection.Row, document.Schema.Columns, document.Schema.KeyStrategy).ToString("N0", CultureInfo.InvariantCulture); }
        catch (Exception exception) { recordId = $"Invalid ({exception.Message})"; }
        InspectorTitle.Text = semantic?.Label ?? selection.Column.Name;
        InspectorSummary.Text = selection.Value.Length == 0 ? "(empty)" : selection.Value;
        var choices = semantic is null ? string.Empty : $"\nKnown     {semantic.Options.Count:N0} {semantic.Kind.ToString().ToLowerInvariant()} option(s)";
        InspectorDetail.Text = $"Row       {selection.Row + 1:N0}\nRecord ID {recordId}\nColumn    {selection.ColumnIndex:N0}\nField     {selection.Column.Name}\nType      {selection.Column.Type}\nOffset    {selection.Column.Offset:N0} bytes\nSize      {selection.Column.Size:N0} bytes{choices}\nEdit      Double-click or Enter/F2 · Tab/Shift+Tab moves across fields";
        _knowledgeContext = $"{Path.GetFileNameWithoutExtension(document.File.SourcePath)} {selection.Column.Name}";
    }

    private void CommitCellEdit(Controls.DbcCellEditCommitEventArgs edit)
    {
        var document = Current;
        if (document is null) { edit.Error = "No DBC document is active."; return; }
        var before = document.File.GetRaw(edit.Row, edit.Column);
        try
        {
            var semantic = DbcSemanticCatalog.Get(Path.GetFileNameWithoutExtension(document.File.SourcePath), edit.Column.Index, document.File, edit.Row);
            if (semantic is null) document.File.SetDisplayValue(edit.Row, edit.Column, edit.Value);
            else document.File.SetRaw(edit.Row, edit.Column, semantic.Parse(edit.Value));
            var after = document.File.GetRaw(edit.Row, edit.Column);
            document.History.Record(edit.Row, edit.Column, before, after);
            edit.Accepted = true;
            ActiveDbcView.RefreshDocument();
            RefreshTabs();
            var display = semantic?.Format(after) ?? Convert.ToString(document.File.GetDisplayValue(edit.Row, edit.Column), CultureInfo.InvariantCulture) ?? string.Empty;
            ShowSelection(new(edit.Row, edit.ColumnIndex, edit.Column, display));
            StatusText.Text = before == after ? "Value was unchanged · Tab continues across the row" : $"Modified {edit.Column.Name} · Ctrl+Z to undo · Tab continues across the row";
            DesktopCrashLogger.Debug("DBC", "inline-cell-edit", ("path", document.FullPath), ("row", edit.Row), ("column", edit.Column.Name), ("before_raw", before), ("after_raw", after), ("changed", before != after));
        }
        catch (Exception exception)
        {
            DesktopCrashLogger.Log("DBC cell edit rejected", exception);
            edit.Error = exception.Message;
            StatusText.Text = $"Invalid {edit.Column.Name}: {exception.Message}";
        }
    }

    private void BeginInlineCellEdit(VirtualDbcView view, TextBox editor, Controls.DbcCellEditRequestEventArgs request)
    {
        _dbcEditingSelection = request.Selection;
        _dbcEditingView = view;
        _dbcEditingEditor = editor;
        editor.Text = request.Selection.Value;
        editor.Margin = new Thickness(request.Bounds.X, request.Bounds.Y, 0, 0);
        editor.Width = request.Bounds.Width;
        editor.Height = request.Bounds.Height;
        editor.IsVisible = true;
        ToolTip.SetTip(editor, "Tab applies and moves right · Shift+Tab moves left · Enter moves down · Esc cancels");
        Dispatcher.UIThread.Post(() =>
        {
            if (!editor.IsVisible) return;
            editor.Focus();
            editor.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void DbcInlineEditorKeyDown(object? sender, KeyEventArgs e)
    {
        Controls.DbcCellMove? move = e.Key switch
        {
            Key.Tab when e.KeyModifiers.HasFlag(KeyModifiers.Shift) => Controls.DbcCellMove.PreviousColumn,
            Key.Tab => Controls.DbcCellMove.NextColumn,
            Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Shift) => Controls.DbcCellMove.PreviousRow,
            Key.Enter => Controls.DbcCellMove.NextRow,
            _ => null
        };
        if (e.Key == Key.Escape)
        {
            CancelInlineCellEdit();
            ActiveDbcView.Focus();
            e.Handled = true;
        }
        else if (move is not null)
        {
            _ = CommitInlineCellEdit(move);
            e.Handled = true;
        }
    }

    private bool CommitInlineCellEdit(Controls.DbcCellMove? move)
    {
        if (_dbcEditingEditor is null || !_dbcEditingEditor.IsVisible || _dbcEditingSelection is null) return true;
        var selection = _dbcEditingSelection;
        var editor = _dbcEditingEditor;
        var view = _dbcEditingView ?? ActiveDbcView;
        var edit = new Controls.DbcCellEditCommitEventArgs(selection.Row, selection.ColumnIndex, selection.Column, editor.Text ?? string.Empty);
        _dbcEditorClosing = true;
        try
        {
            CommitCellEdit(edit);
            if (!edit.Accepted)
            {
                ToolTip.SetTip(editor, edit.Error ?? "The value was rejected.");
                editor.Focus();
                editor.SelectAll();
                return false;
            }
            editor.IsVisible = false;
            _dbcEditingSelection = null;
            _dbcEditingEditor = null;
            _dbcEditingView = null;
            if (move is not null)
            {
                view.MoveSelection(move.Value);
                view.BeginSelectedEdit();
            }
            else view.Focus();
            return true;
        }
        finally { _dbcEditorClosing = false; }
    }

    private void CancelInlineCellEdit()
    {
        _dbcEditorClosing = true;
        DbcInlineEditor.IsVisible = false;
        SecondaryDbcInlineEditor.IsVisible = false;
        _dbcEditingSelection = null;
        _dbcEditingEditor = null;
        _dbcEditingView = null;
        _dbcEditorClosing = false;
    }

    private void UndoClick(object? sender, RoutedEventArgs e) => Undo();
    private void RedoClick(object? sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        var document = Current;
        if (document is null) return;
        var edit = document.History.Undo(document.File);
        if (edit is null) { StatusText.Text = "Nothing to undo in this DBC"; return; }
        ActiveDbcView.RefreshDocument(edit.Row); RefreshTabs(); StatusText.Text = $"Undid {edit.Description}";
        DesktopCrashLogger.Debug("DBC", "undo", ("path", document.FullPath), ("row", edit.Row), ("description", edit.Description));
    }

    private void Redo()
    {
        var document = Current;
        if (document is null) return;
        var edit = document.History.Redo(document.File);
        if (edit is null) { StatusText.Text = "Nothing to redo in this DBC"; return; }
        ActiveDbcView.RefreshDocument(edit.Row); RefreshTabs(); StatusText.Text = $"Redid {edit.Description}";
        DesktopCrashLogger.Debug("DBC", "redo", ("path", document.FullPath), ("row", edit.Row), ("description", edit.Description));
    }

    private async void AddRowClick(object? sender, RoutedEventArgs e) => await AddRowAsync();
    private async void CloneRowClick(object? sender, RoutedEventArgs e) => await CloneRowsAsync(1);
    private async void CloneMultipleClick(object? sender, RoutedEventArgs e)
    {
        var count = await PromptCloneCountAsync();
        if (count is not null) await CloneRowsAsync(count.Value);
    }

    private async Task AddRowAsync()
    {
        var document = Current;
        if (document is null) return;
        if (!await EnsureBackupChoiceAsync()) return;
        try
        {
            RequireStructuralKey(document);
            ClearFilter();
            var row = document.File.AddBlankRow(document.IdColumn);
            document.History.Clear();
            ActiveDbcView.RefreshDocument(row); RefreshTabs();
            StatusText.Text = $"Created row {row + 1:N0} with the next available identity";
            DesktopCrashLogger.Debug("DBC", "row-added", ("path", document.FullPath), ("row", row), ("new_row_count", document.File.RowCount));
        }
        catch (Exception exception) { DesktopCrashLogger.Log("DBC row add failed", exception); _ = ShowErrorAsync("Could not add row", exception.Message); }
    }

    private async Task CloneRowsAsync(int count)
    {
        var document = Current;
        var source = ActiveDbcView.SelectedSourceRow;
        if (document is null || source < 0) { StatusText.Text = "Select a source row first"; return; }
        if (!await EnsureBackupChoiceAsync()) return;
        try
        {
            RequireStructuralKey(document);
            ClearFilter();
            var first = document.File.CloneRows(source, count, document.IdColumn);
            document.History.Clear();
            ActiveDbcView.RefreshDocument(first); RefreshTabs();
            StatusText.Text = $"Created {count:N0} clone(s) in one batch, starting at row {first + 1:N0}";
            DesktopCrashLogger.Debug("DBC", "rows-cloned", ("path", document.FullPath), ("source_row", source), ("count", count), ("first_new_row", first), ("new_row_count", document.File.RowCount));
        }
        catch (Exception exception) { DesktopCrashLogger.Log("DBC row clone failed", exception); _ = ShowErrorAsync("Could not clone row", exception.Message); }
    }

    private async void DeleteRowClick(object? sender, RoutedEventArgs e)
    {
        var document = Current;
        var row = ActiveDbcView.SelectedSourceRow;
        if (document is null || row < 0) { StatusText.Text = "Select a row first"; return; }
        if (!await EnsureBackupChoiceAsync()) return;
        if (document.Schema.KeyStrategy.Kind == DbcRecordKeyKind.VirtualRowIndex && row != document.File.RowCount - 1 &&
            !await ConfirmAsync("Virtual row identities will change", "This table uses row positions as identities. Deleting a non-trailing row renumbers every following record and can break references. Continue anyway?")) return;
        if (!await ConfirmAsync("Delete selected row?", $"Delete row {row + 1:N0} from {Path.GetFileName(document.File.SourcePath)}? This structural operation clears cell undo history.")) return;
        document.File.DeleteRows([row]);
        document.History.Clear();
        ClearFilter();
        ActiveDbcView.RefreshDocument(Math.Min(row, Math.Max(0, document.File.RowCount - 1)));
        RefreshTabs();
        StatusText.Text = $"Deleted row {row + 1:N0}";
        DesktopCrashLogger.Debug("DBC", "row-deleted", ("path", document.FullPath), ("row", row), ("new_row_count", document.File.RowCount));
    }

    private void OpenSpellWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        var document = Current;
        var row = ActiveDbcView.SelectedSourceRow;
        if (document is null || !Path.GetFileNameWithoutExtension(document.File.SourcePath).Equals("Spell", StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "Open Spell.dbc and select a spell row before opening the guided spell workspace.";
            return;
        }
        if (row < 0) { StatusText.Text = "Select a spell row first."; return; }
        if (document.Schema.Columns.Count <= 233)
        {
            _ = ShowErrorAsync("Spell schema mismatch", "The guided WotLK spell workspace requires the 3.3.5a Spell.dbc layout. Select the matching build-12340 schema first.");
            return;
        }
        ShowSpellWorkspace(document, row);
    }

    private void OpenDbcStagingClick(object? sender, RoutedEventArgs e)
    {
        var document = Current;
        if (document is null) { StatusText.Text = "Open a schema-resolved DBC before opening its staging database."; return; }
        var view = new DbcStagingWorkspaceView(document, _workspaceSession);
        view.BackRequested += (_, _) => CloseFeatureWorkspace();
        view.ProjectWorkspaceRequested += (_, _) => OpenProjectWorkspace();
        view.AppliedToDocument += (_, result) =>
        {
            document.History.Clear(); ActiveDbcView.RefreshDocument(Math.Max(0, ActiveDbcView.SelectedSourceRow)); RefreshTabs();
            StatusText.Text = $"Applied staging database to the open {Path.GetFileName(document.File.SourcePath)}: {result.UpdatedRows:N0} updated, {result.AppendedRows:N0} appended, {result.ChangedCells:N0} cells. Save when reviewed.";
        };
        OpenFeatureWorkspace(view, $"{Path.GetFileNameWithoutExtension(document.File.SourcePath)} staging database");
    }

    private SpellWorkspaceView ShowSpellWorkspace(DbcDocumentSession document, int row)
    {
        var view = new SpellWorkspaceView(document.File, row, document.Schema.Columns, _workspaceSession, changes => ApplySpellChanges(document, row, changes));
        view.BackRequested += (_, _) => CloseFeatureWorkspace();
        view.FullSqlEditRequested += async (_, request) => await OpenCompleteSqlRowAsync(request);
        view.ReferenceLookupRequested += (_, request) => _ = OpenReferencePickerAsync(request);
        view.ProjectCloneRequested += async (_, request) => await CloneSpellIntoProjectAsync(document, row, view, request.Domain);
        OpenFeatureWorkspace(view, $"Spell {document.File.GetDisplayValue(row, document.Schema.Columns[0])}");
        return view;
    }

    private async Task CloneSpellIntoProjectAsync(DbcDocumentSession document, int sourceRow, SpellWorkspaceView sourceView, ContentIdDomain domain)
    {
        if (string.IsNullOrWhiteSpace(_workspaceSession.Settings.ActiveProjectPath))
        {
            sourceView.ReportProjectClone("Create or open a project first. Opening Projects & shared IDs…", false); OpenProjectWorkspace(); return;
        }
        try
        {
            RequireStructuralKey(document); var idColumn = document.IdColumn ?? throw new InvalidOperationException("Spell.dbc has no physical ID column.");
            if (!document.File.AllowsStructuralMutation) throw new InvalidOperationException("This client table has dependent DB2 side structures and cannot be cloned safely.");
            var stagedIds = DbcRecordIdentity.IndexRows(document.File, document.Schema.Columns, document.Schema.KeyStrategy).Keys.ToArray();
            var name = Convert.ToString(document.File.GetDisplayValue(sourceRow, document.Schema.Columns[136]), CultureInfo.InvariantCulture) ?? "unnamed spell";
            var sourceId = document.File.GetRaw(sourceRow, idColumn); var purpose = domain == ContentIdDomain.Mount ? $"Mount-spell draft cloned from {sourceId}: {name}" : $"Spell clone of {sourceId}: {name}";
            var reserved = await ProjectIdReservationBridge.ReserveNextAsync(_workspaceSession, domain, purpose, stagedDbcIds: new Dictionary<string, IReadOnlyCollection<uint>>(StringComparer.OrdinalIgnoreCase) { ["Spell"] = stagedIds });
            var newRow = document.File.CloneRowWithId(sourceRow, idColumn, reserved.SingleId); document.History.Clear(); ClearFilter(); ActiveDbcView.RefreshDocument(newRow); RefreshTabs();
            var clonedView = ShowSpellWorkspace(document, newRow); var kind = domain == ContentIdDomain.Mount ? "mount-spell draft" : "spell";
            clonedView.ReportProjectClone($"Reserved and cloned {kind} ID {reserved.SingleId:N0} in {reserved.ProjectName}. The new row is staged only; review effects/text/visuals, then save Spell.dbc and build the client patch. No SQL row was created.", true);
            StatusText.Text = $"Cloned Spell.dbc row {sourceId:N0} to project-reserved ID {reserved.SingleId:N0}";
            DesktopCrashLogger.Debug("SPELL", "project-clone", ("source_id", sourceId), ("target_id", reserved.SingleId), ("domain", domain), ("project", reserved.ProjectPath), ("new_row", newRow));
        }
        catch (Exception exception)
        {
            sourceView.ReportProjectClone($"Project spell clone failed: {exception.Message}", false); DesktopCrashLogger.Log("Project spell clone failed", exception);
        }
    }

    private async Task OpenReferencePickerAsync(ReferencePickerRequest request)
    {
        if (request.DbcSource is null)
        {
            var definition = ReferenceDbcDefinition(request.Domain);
            if (request.Domain == ReferenceDomain.Spell)
            {
                var openSpell = _documents.FirstOrDefault(document => Path.GetFileNameWithoutExtension(document.File.SourcePath).Equals("Spell", StringComparison.OrdinalIgnoreCase));
                if (openSpell is not null) request = request with { DbcSource = new(openSpell.File, openSpell.Schema.Columns, 0, 136, [39, 3]) };
            }
            if (request.DbcSource is null && definition is { } dbcDefinition)
            {
                var currentDirectory = Current is { } current ? Path.GetDirectoryName(current.File.SourcePath) : null;
                var path = currentDirectory is not null && File.Exists(Path.Combine(currentDirectory, dbcDefinition.FileName))
                    ? Path.Combine(currentDirectory, dbcDefinition.FileName)
                    : Path.Combine(_workspaceSession.Settings.CoreDbcPath, dbcDefinition.FileName);
                if (File.Exists(path))
                {
                    try
                    {
                        path = Path.GetFullPath(path);
                        if (!_referenceDbcCache.TryGetValue(path, out var cached))
                        {
                            var catalog = ResolveSchemaCatalog(); var loaded = await Task.Run(() => WdbcFile.Load(path)); var resolution = catalog.ResolveColumns(dbcDefinition.TableName, loaded.FieldCount);
                            if (resolution.MatchKind == DbcSchemaMatchKind.NamedMatch) _referenceDbcCache[path] = cached = (loaded, resolution.Columns);
                        }
                        if (cached.File is not null) request = request with { DbcSource = new(cached.File, cached.Columns, 0, dbcDefinition.NameColumn, dbcDefinition.DetailColumns) };
                    }
                    catch (Exception exception) { DesktopCrashLogger.Log($"Reference {dbcDefinition.FileName} load failed", exception); }
                }
            }
        }
        var view = new ReferencePickerView(_workspaceSession, request);
        view.BackRequested += (_, _) => { view.Dispose(); CloseFeatureWorkspace(); };
        view.SelectionApplied += (_, _) => { view.Dispose(); CloseFeatureWorkspace(); };
        OpenFeatureWorkspace(view, $"Select {request.Domain} for {request.FieldLabel}");
    }

    private sealed record ReferenceDbcDefinitionRow(string FileName, string TableName, int NameColumn, int[] DetailColumns);
    private static ReferenceDbcDefinitionRow? ReferenceDbcDefinition(ReferenceDomain domain) => domain switch
    {
        ReferenceDomain.Spell => new("Spell.dbc", "Spell", 136, [39, 3]),
        ReferenceDomain.SpellCastTime => new("SpellCastTimes.dbc", "SpellCastTimes", -1, [1, 2, 3]),
        ReferenceDomain.SpellDuration => new("SpellDuration.dbc", "SpellDuration", -1, [1, 2, 3]),
        ReferenceDomain.SpellRange => new("SpellRange.dbc", "SpellRange", 6, [1, 2, 3, 4, 5]),
        ReferenceDomain.SpellRuneCost => new("SpellRuneCost.dbc", "SpellRuneCost", -1, [1, 2, 3, 4]),
        ReferenceDomain.SpellVisual => new("SpellVisual.dbc", "SpellVisual", -1, [1, 2, 3, 4, 5, 6, 7, 8]),
        ReferenceDomain.SpellIcon => new("SpellIcon.dbc", "SpellIcon", 1, []),
        ReferenceDomain.SpellDifficulty => new("SpellDifficulty.dbc", "SpellDifficulty", -1, [1, 2, 3, 4]),
        _ => null
    };

    private void ApplySpellChanges(DbcDocumentSession document, int row, IReadOnlyList<SpellFieldChange> changes)
    {
        var applied = new List<(DbcColumn Column, uint Before)>();
        try
        {
            foreach (var change in changes)
            {
                var before = document.File.GetRaw(row, change.Column);
                var semantic = DbcSemanticCatalog.Get("Spell", change.Column.Index, document.File, row);
                if (semantic is null) document.File.SetDisplayValue(row, change.Column, change.Value);
                else document.File.SetRaw(row, change.Column, semantic.Parse(change.Value));
                applied.Add((change.Column, before));
            }
            foreach (var change in applied)
            {
                var after = document.File.GetRaw(row, change.Column);
                document.History.Record(row, change.Column, change.Before, after);
            }
            ActiveDbcView.RefreshDocument(row);
            RefreshTabs();
            StatusText.Text = $"Applied {changes.Count:N0} guided spell field change(s) · Ctrl+Z to undo";
            DesktopCrashLogger.Debug("SPELL", "guided-edit-applied", ("path", document.FullPath), ("row", row), ("fields", changes.Count));
        }
        catch
        {
            foreach (var change in applied.AsEnumerable().Reverse()) document.File.SetRaw(row, change.Column, change.Before);
            throw;
        }
    }

    private static void RequireStructuralKey(DbcDocumentSession document)
    {
        if (document.Schema.KeyStrategy.Kind == DbcRecordKeyKind.NoStableKey)
            throw new InvalidOperationException("This table has no verified row-key strategy. Select a matching schema before adding or cloning records.");
    }

    private void ClearFilter()
    {
        SearchBox.Text = string.Empty;
        ActiveDbcView.SetFilteredRows(null);
    }

    private void DecodedChanged(object? sender, RoutedEventArgs e)
    {
        DbcView.SetDecoded(DecodedToggle.IsChecked == true);
        SecondaryDbcView.SetDecoded(DecodedToggle.IsChecked == true);
        if (CurrentFile is not null) StatusText.Text = DecodedToggle.IsChecked == true ? "Decoded names enabled" : "Raw field values enabled";
    }

    private void OpenFindReplace(bool focusReplacement = false)
    {
        FindReplaceBar.IsVisible = true;
        Dispatcher.UIThread.Post(() =>
        {
            var target = focusReplacement ? ReplaceBox : SearchBox;
            target.Focus(); target.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void CloseFindReplaceClick(object? sender, RoutedEventArgs e) => CloseFindReplace();
    private void CloseFindReplace()
    {
        FindReplaceBar.IsVisible = false;
        _searchCancellation?.Cancel();
        ActiveDbcView.Focus();
    }

    private async void FindNextClick(object? sender, RoutedEventArgs e) => await NavigateFindAsync(1);
    private async void FindPreviousClick(object? sender, RoutedEventArgs e) => await NavigateFindAsync(-1);

    private async Task NavigateFindAsync(int direction)
    {
        var query = SearchBox.Text ?? string.Empty;
        if (query.Length == 0) { OpenFindReplace(); return; }
        var document = Current;
        if (DbcHost.IsVisible && document is not null)
        {
            if (!await EnsureBackupChoiceAsync()) return;
            SetBusy($"Finding “{query}”…");
            var selectedRow = Math.Max(0, ActiveDbcView.SelectedSourceRow);
            var selectedColumn = Math.Max(0, ActiveDbcView.SelectedColumn);
            var match = await Task.Run(() => FindNextDbcCell(document, query, selectedRow, selectedColumn, direction));
            if (match is null) { StatusText.Text = $"No DBC cell contains “{query}”"; return; }
            ActiveDbcView.SelectSourceRow(match.Value.Row, match.Value.Column);
            StatusText.Text = $"Found “{query}” at row {match.Value.Row + 1:N0}, {document.Schema.Columns[match.Value.Column].Name}";
            return;
        }
        RefreshVisibleUiMatches(query);
        if (_uiFindMatches.Count == 0) { StatusText.Text = $"No visible text contains “{query}”"; return; }
        _uiFindMatchIndex = (_uiFindMatchIndex + direction + _uiFindMatches.Count) % _uiFindMatches.Count;
        var uiMatch = _uiFindMatches[_uiFindMatchIndex];
        uiMatch.Control.BringIntoView(); uiMatch.Control.Focus();
        if (uiMatch.Control is TextBox box) { box.SelectionStart = uiMatch.CharacterIndex; box.SelectionEnd = uiMatch.CharacterIndex + query.Length; }
        StatusText.Text = $"Visible match {_uiFindMatchIndex + 1:N0} of {_uiFindMatches.Count:N0}";
    }

    private static (int Row, int Column)? FindNextDbcCell(DbcDocumentSession document, string query, int startRow, int startColumn, int direction)
    {
        var rows = document.File.RowCount;
        var columns = document.Schema.Columns.Count;
        if (rows == 0 || columns == 0) return null;
        var total = checked((long)rows * columns);
        var start = (long)Math.Clamp(startRow, 0, rows - 1) * columns + Math.Clamp(startColumn, 0, columns - 1);
        for (long step = 1; step <= total; step++)
        {
            var linear = (start + direction * step) % total;
            if (linear < 0) linear += total;
            var row = (int)(linear / columns);
            var columnIndex = (int)(linear % columns);
            var column = document.Schema.Columns[columnIndex];
            var rawDisplay = Convert.ToString(document.File.GetDisplayValue(row, column), CultureInfo.InvariantCulture) ?? string.Empty;
            var semantic = DbcSemanticCatalog.Get(document.File.LogicalTableName, column.Index, document.File, row)?.Format(document.File.GetRaw(row, column));
            if (rawDisplay.Contains(query, StringComparison.OrdinalIgnoreCase) || semantic?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                return (row, columnIndex);
        }
        return null;
    }

    private void RefreshVisibleUiMatches(string query)
    {
        var root = FeatureWorkspaceHost.IsVisible ? (Visual)FeatureWorkspaceHost : RootLayout;
        _uiFindMatches = root.GetVisualDescendants().OfType<Control>().Select(control => (Control: control, Text: control switch
        {
            TextBox box => box.Text ?? string.Empty,
            TextBlock block => block.Text ?? string.Empty,
            ContentControl content when content.Content is string text => text,
            _ => string.Empty
        })).Where(entry => entry.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
          .Select(entry => (entry.Control, entry.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        _uiFindMatchIndex = -1;
    }

    private async void ReplaceOneClick(object? sender, RoutedEventArgs e) => await ReplaceDbcAsync(false);
    private async void ReplaceAllClick(object? sender, RoutedEventArgs e) => await ReplaceDbcAsync(true);

    private async Task ReplaceDbcAsync(bool all)
    {
        var find = SearchBox.Text ?? string.Empty;
        if (find.Length == 0) { OpenFindReplace(); return; }
        var replacement = ReplaceBox.Text ?? string.Empty;
        var document = Current;
        if (DbcHost.IsVisible && document is not null)
        {
            if (all && !await ConfirmAsync("Replace every matching DBC cell?", $"Replace “{find}” with “{replacement}” throughout {Path.GetFileName(document.File.SourcePath)}? The operation is atomic, but bulk replacement clears per-cell undo history.")) return;
            try
            {
                var startRow = Math.Max(0, ActiveDbcView.SelectedSourceRow);
                var startColumn = Math.Max(0, ActiveDbcView.SelectedColumn);
                var changed = await Task.Run(() => DbcRangeTransferService.ReplaceText(document.File, document.Schema.Columns, find, replacement, all, startRow, startColumn));
                document.History.Clear(); ActiveDbcView.RefreshDocument(startRow); RefreshTabs();
                StatusText.Text = changed == 0 ? $"No writable DBC value contains “{find}”" : $"Replaced {changed:N0} DBC cell(s) · save is still required";
                if (!all && changed > 0) await NavigateFindAsync(1);
            }
            catch (Exception exception) { DesktopCrashLogger.Log("DBC find/replace failed", exception); await ShowErrorAsync("Could not replace DBC values", exception.Message); }
            return;
        }

        RefreshVisibleUiMatches(find);
        var writable = _uiFindMatches.Select(match => match.Control).OfType<TextBox>().Where(box => !box.IsReadOnly).Distinct().ToArray();
        if (writable.Length == 0) { StatusText.Text = "Find works in this view, but it has no directly writable text field matches."; return; }
        var targets = all ? writable : writable.Take(1);
        var changedFields = 0;
        foreach (var box in targets)
        {
            var before = box.Text ?? string.Empty;
            var after = ReplaceIgnoreCase(before, find, replacement);
            if (before == after) continue;
            box.Text = after; changedFields++;
        }
        StatusText.Text = $"Replaced text in {changedFields:N0} visible field(s)";
    }

    private static string ReplaceIgnoreCase(string text, string find, string replacement)
    {
        var result = new System.Text.StringBuilder(text.Length); var start = 0;
        while (true)
        {
            var match = text.IndexOf(find, start, StringComparison.OrdinalIgnoreCase);
            if (match < 0) { result.Append(text, start, text.Length - start); return result.ToString(); }
            result.Append(text, start, match - start); result.Append(replacement); start = match + find.Length;
        }
    }

    private async void SearchChanged(object? sender, TextChangedEventArgs e)
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var token = _searchCancellation.Token;
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        var document = Current;
        if (!DbcHost.IsVisible || document is null)
        {
            if (query.Length == 0) { _uiFindMatches = []; _uiFindMatchIndex = -1; return; }
            RefreshVisibleUiMatches(query);
            StatusText.Text = $"{_uiFindMatches.Count:N0} visible interface match(es) · Enter/Next moves through them";
            return;
        }
        if (query.Length == 0)
        {
            ActiveDbcView.SetFilteredRows(null);
            StatusText.Text = $"Showing all {document.File.RowCount:N0} records";
            return;
        }
        try
        {
            var stopwatch = Stopwatch.StartNew();
            DesktopCrashLogger.Debug("DBC", "search-start", ("path", document.FullPath), ("query", query), ("rows", document.File.RowCount));
            await Task.Delay(180, token);
            SetBusy($"Searching {document.File.RowCount:N0} records…");
            var decoded = DecodedToggle.IsChecked == true;
            var table = Path.GetFileNameWithoutExtension(document.File.SourcePath);
            var semanticColumns = decoded ? DbcSemanticCatalog.GetColumns(table).Where(index => index >= 0 && index < document.Schema.Columns.Count).ToArray() : [];
            var rows = await Task.Run(() => Enumerable.Range(0, document.File.RowCount).AsParallel().AsOrdered().WithCancellation(token)
                .Where(row => document.File.RowContains(row, query, document.Schema.Columns) || semanticColumns.Any(index =>
                    DbcSemanticCatalog.Get(table, index, document.File, row)?.Format(document.File.GetRaw(row, document.Schema.Columns[index])).Contains(query, StringComparison.OrdinalIgnoreCase) == true))
                .ToArray(), token);
            if (token.IsCancellationRequested || !ReferenceEquals(document, Current)) return;
            ActiveDbcView.SetFilteredRows(rows);
            StatusText.Text = $"{rows.Length:N0} of {document.File.RowCount:N0} records match “{query}”";
            DesktopCrashLogger.Debug("DBC", "search-success", ("path", document.FullPath), ("query", query), ("matches", rows.Length), ("duration_ms", stopwatch.Elapsed.TotalMilliseconds));
        }
        catch (OperationCanceledException) { DesktopCrashLogger.Debug("DBC", "search-cancelled", ("path", document.FullPath), ("query", query)); }
    }

    private async void OpenM2Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Inspect a WotLK M2 model", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("WotLK M2 models") { Patterns = ["*.m2"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) await LoadM2Async(path);
    }

    private async Task LoadM2Async(string path)
    {
        SetBusy($"Reading {Path.GetFileName(path)}…");
        var stopwatch = Stopwatch.StartNew();
        DesktopCrashLogger.Debug("MODEL", "preview-start", ("path", path), ("bytes", new FileInfo(path).Length));
        try
        {
            var geometry = await Task.Run(() => M2PreviewGeometryService.Load(path));
            WelcomePanel.IsVisible = false; DbcHost.IsVisible = false; M2View.IsVisible = true;
            M2View.SetGeometry(geometry);
            RefreshShellContext();
            InspectorTitle.Text = Path.GetFileName(path);
            InspectorSummary.Text = $"{geometry.Vertices.Count:N0} vertices · {geometry.TriangleIndices.Count / 3:N0} triangles";
            InspectorDetail.Text = $"Model     {geometry.ModelPath}\nSkin      {geometry.SkinPath}\nMinimum   {geometry.Minimum}\nMaximum   {geometry.Maximum}";
            StatusText.Text = "Native model ready · drag to rotate · wheel to zoom";
            DesktopCrashLogger.Debug("MODEL", "preview-success", ("path", path), ("skin", geometry.SkinPath), ("vertices", geometry.Vertices.Count), ("triangles", geometry.TriangleIndices.Count / 3), ("duration_ms", stopwatch.Elapsed.TotalMilliseconds));
        }
        catch (Exception exception)
        {
            DesktopCrashLogger.Log("M2 preview failed", exception);
            await ShowErrorAsync("Could not inspect model", exception.Message);
        }
    }

    private void OpenLogsClick(object? sender, RoutedEventArgs e) => DesktopCrashLogger.OpenDirectory();
    private void OpenItemWorkbenchClick(object? sender, RoutedEventArgs e) => OpenItemWorkbench();
    public void OpenItemWorkbench()
    {
        if (_itemWorkbenchView is null)
        {
            _itemWorkbenchView = new ItemWorkbenchView(_workspaceSession);
            _itemWorkbenchView.SqlStudioRequested += (_, _) => OpenSqlWorkspace();
            _itemWorkbenchView.SqlFavoritesRequested += (_, _) => OpenSqlFavorites();
            _itemWorkbenchView.MpqWorkspaceRequested += (_, _) => OpenMpqMergeWorkspace();
            _itemWorkbenchView.ProjectWorkspaceRequested += (_, _) => OpenProjectWorkspace();
            _itemWorkbenchView.FullSqlEditRequested += async (_, request) => await OpenCompleteSqlRowAsync(request);
            _itemWorkbenchView.ReferenceLookupRequested += (_, request) => _ = OpenReferencePickerAsync(request);
        }
        OpenFeatureWorkspace(_itemWorkbenchView, "Items & Sets");
    }
    public void OpenItemAcquisition(string? exactIds = null)
    {
        OpenItemWorkbench();
        _itemWorkbenchView!.ActivateAcquisition(exactIds);
    }
    private void OpenCreatureWorkspaceClick(object? sender, RoutedEventArgs e) => OpenCreatureWorkspace();
    public void OpenCreatureWorkspace()
    {
        if (_creatureWorkspaceView is null)
        {
            _creatureWorkspaceView = new CreatureWorkspaceView(_workspaceSession);
            _creatureWorkspaceView.BackRequested += (_, _) => CloseFeatureWorkspace();
            _creatureWorkspaceView.ProjectWorkspaceRequested += (_, _) => OpenProjectWorkspace();
            _creatureWorkspaceView.MpqWorkspaceRequested += (_, _) => OpenMpqWorkspace();
            _creatureWorkspaceView.PatchEntriesRequested += (_, entries) => OpenPatchBuilderWithEntries(entries);
            _creatureWorkspaceView.ReferenceLookupRequested += (_, request) => _ = OpenReferencePickerAsync(request);
        }
        OpenFeatureWorkspace(_creatureWorkspaceView, "Creatures & NPCs");
    }
    private void OpenGameObjectWorkspaceClick(object? sender, RoutedEventArgs e) => OpenGameObjectWorkspace();
    public void OpenGameObjectWorkspace()
    {
        if (_gameObjectWorkspaceView is null)
        {
            _gameObjectWorkspaceView = new GameObjectWorkspaceView(_workspaceSession);
            _gameObjectWorkspaceView.BackRequested += (_, _) => CloseFeatureWorkspace();
            _gameObjectWorkspaceView.ProjectWorkspaceRequested += (_, _) => OpenProjectWorkspace();
            _gameObjectWorkspaceView.ReferenceLookupRequested += (_, request) => _ = OpenReferencePickerAsync(request);
        }
        OpenFeatureWorkspace(_gameObjectWorkspaceView, "Gameobjects");
    }
    private void OpenQuestWorkspaceClick(object? sender, RoutedEventArgs e) => OpenQuestWorkspace();
    public void OpenQuestWorkspace()
    {
        if (_questWorkspaceView is null)
        {
            _questWorkspaceView = new QuestWorkspaceView(_workspaceSession);
            _questWorkspaceView.BackRequested += (_, _) => CloseFeatureWorkspace();
            _questWorkspaceView.ProjectWorkspaceRequested += (_, _) => OpenProjectWorkspace();
            _questWorkspaceView.ReferenceLookupRequested += (_, request) => _ = OpenReferencePickerAsync(request);
        }
        OpenFeatureWorkspace(_questWorkspaceView, "Quests");
    }
    private void OpenBehaviorWorkspaceClick(object? sender, RoutedEventArgs e) => OpenBehaviorWorkspace();
    public void OpenBehaviorWorkspace()
    {
        OpenFeatureWorkspace(EnsureWorldDataWorkspace(), "Behaviors & dialogue");
    }
    private void OpenPetWorkspaceClick(object? sender, RoutedEventArgs e) => OpenPetWorkspace();
    public void OpenPetWorkspace()
    {
        var view = EnsureWorldDataWorkspace(); view.SelectDomain("pet-level-stats"); OpenFeatureWorkspace(view, "Pets & companions");
    }
    private BehaviorWorkspaceView EnsureWorldDataWorkspace()
    {
        if (_behaviorWorkspaceView is not null) return _behaviorWorkspaceView;
        _behaviorWorkspaceView = new BehaviorWorkspaceView(_workspaceSession);
        _behaviorWorkspaceView.BackRequested += (_, _) => CloseFeatureWorkspace();
        _behaviorWorkspaceView.PetCurveRequested += (_, _) => OpenPetLevelCurveWorkspace();
        _behaviorWorkspaceView.PetAbilityGraphRequested += (_, _) => OpenPetAbilityGraphWorkspace();
        _behaviorWorkspaceView.ReferenceLookupRequested += (_, request) => _ = OpenReferencePickerAsync(request);
        return _behaviorWorkspaceView;
    }
    public void OpenPetLevelCurveWorkspace()
    {
        if (_petLevelCurveView is null)
        {
            _petLevelCurveView = new PetLevelCurveView(_workspaceSession);
            _petLevelCurveView.BackRequested += (_, _) => CloseFeatureWorkspace();
            _petLevelCurveView.ReferenceLookupRequested += (_, request) => _ = OpenReferencePickerAsync(request);
        }
        OpenFeatureWorkspace(_petLevelCurveView, "Pet level curve");
    }
    public void OpenPetAbilityGraphWorkspace()
    {
        if (_petAbilityGraphView is null)
        {
            _petAbilityGraphView = new PetAbilityGraphView(_workspaceSession);
            _petAbilityGraphView.BackRequested += (_, _) => CloseFeatureWorkspace();
            _petAbilityGraphView.ReferenceLookupRequested += (_, request) => _ = OpenReferencePickerAsync(request);
        }
        OpenFeatureWorkspace(_petAbilityGraphView, "Pet talents & abilities");
    }
    private void OpenAssetComparisonClick(object? sender, RoutedEventArgs e) => OpenAssetComparison();
    private void OpenNativeConversionClick(object? sender, RoutedEventArgs e) => OpenNativeConversionWorkspace();
    public void OpenNativeConversionWorkspace()
    {
        if (_nativeConversionWorkspaceView is null)
        {
            _nativeConversionWorkspaceView = new NativeConversionWorkspaceView(_workspaceSession.Settings);
            _nativeConversionWorkspaceView.BackRequested += (_, _) => CloseFeatureWorkspace();
        }
        OpenFeatureWorkspace(_nativeConversionWorkspaceView, "Modern Asset Conversion");
    }
    private async void OpenToolInventoryClick(object? sender, RoutedEventArgs e) => await OpenToolInventoryAsync();
    public async Task OpenToolInventoryAsync()
    {
        if (_toolInventoryView is null) { _toolInventoryView = new ToolInventoryView(); _toolInventoryView.BackRequested += (_, _) => CloseFeatureWorkspace(); }
        OpenFeatureWorkspace(_toolInventoryView, "Tool Inventory"); await _toolInventoryView.ActivateAsync();
    }
    private async void OpenKnowledgeClick(object? sender, RoutedEventArgs e) => await OpenKnowledgeAsync(_knowledgeContext);
    public async Task OpenKnowledgeAsync(string? query = null)
    {
        if (_knowledgeWorkspaceView is null) { _knowledgeWorkspaceView = new KnowledgeWorkspaceView(); _knowledgeWorkspaceView.BackRequested += (_, _) => CloseFeatureWorkspace(); }
        OpenFeatureWorkspace(_knowledgeWorkspaceView, "Offline Knowledge & Field Reference"); await _knowledgeWorkspaceView.ActivateAsync(query);
    }
    private void OpenHomeClick(object? sender, RoutedEventArgs e) => ShowHome();
    private void OpenEditorWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        CloseAllFeatureWorkspaces();
        if (Current is not null) ActivateDocument(ActiveDocumentIndex);
        else ShowHome();
    }
    private void OpenLayeredDbcsClick(object? sender, RoutedEventArgs e)
    {
        if (_layeredDbcWorkspaceView is null)
        {
            _layeredDbcWorkspaceView = new LayeredDbcWorkspaceView(_workspaceSession);
            _layeredDbcWorkspaceView.BackRequested += (_, _) => CloseFeatureWorkspace();
            _layeredDbcWorkspaceView.OpenDbcRequested += async (_, path) => { CloseAllFeatureWorkspaces(); await LoadDbcAsync(path); };
            _layeredDbcWorkspaceView.StageOverridesRequested += (_, paths) => OpenPatchBuilderWithPaths(paths);
        }
        OpenFeatureWorkspace(_layeredDbcWorkspaceView, "DBC Layers & Promotion");
    }
    private void OpenDbdSchemasClick(object? sender, RoutedEventArgs e) => OpenDbdSchemaAudit();
    public void OpenDbdSchemaAudit()
    {
        if (_dbdSchemaAuditView is null)
        {
            _dbdSchemaAuditView = new DbdSchemaAuditView(_workspaceSession);
            _dbdSchemaAuditView.BackRequested += (_, _) => CloseFeatureWorkspace();
        }
        OpenFeatureWorkspace(_dbdSchemaAuditView, "DBD Schemas & Audit");
    }
    private void OpenCacheTablesClick(object? sender, RoutedEventArgs e) => OpenCacheTableWorkspace();
    public void OpenCacheTableWorkspace()
    {
        if (_cacheTableWorkspaceView is null)
        {
            _cacheTableWorkspaceView = new CacheTableWorkspaceView(_workspaceSession);
            _cacheTableWorkspaceView.BackRequested += (_, _) => CloseFeatureWorkspace();
            _cacheTableWorkspaceView.SqlStudioRequested += (_, _) => OpenSqlWorkspace();
        }
        OpenFeatureWorkspace(_cacheTableWorkspaceView, "Client Cache Tables");
    }
    public async Task OpenCacheTableAsync(string path)
    {
        OpenCacheTableWorkspace();
        await _cacheTableWorkspaceView!.LoadAsync(path);
    }
    private void OpenProjectWorkspaceClick(object? sender, RoutedEventArgs e) => OpenProjectWorkspace();
    public void OpenProjectWorkspace()
    {
        if (_projectWorkspaceView is null)
        {
            _projectWorkspaceView = new ProjectWorkspaceView(_workspaceSession);
            _projectWorkspaceView.BackRequested += (_, _) => CloseFeatureWorkspace();
            _projectWorkspaceView.ServerSqlRequested += (_, _) => OpenServerSqlWorkspace();
        }
        OpenFeatureWorkspace(_projectWorkspaceView, "Projects & Shared IDs"); _projectWorkspaceView.Activate();
    }
    private void OpenMpqWorkspaceClick(object? sender, RoutedEventArgs e)
        => OpenMpqWorkspace();
    public void OpenMpqWorkspace()
    {
        if (_mpqWorkspaceView is null)
        {
            _mpqWorkspaceView = new MpqWorkspaceView(_workspaceSession);
            _mpqWorkspaceView.BackRequested += (_, _) => CloseFeatureWorkspace();
        }
        OpenFeatureWorkspace(_mpqWorkspaceView, "MPQ Patches & Archives");
    }
    public void OpenMpqMergeWorkspace()
    {
        OpenMpqWorkspace();
        _mpqWorkspaceView!.ActivateMerge();
    }
    private void OpenClientWorkspaceClick(object? sender, RoutedEventArgs e) => OpenClientWorkspace();
    public void OpenClientWorkspace()
    {
        if (_clientWorkspaceView is null)
        {
            _clientWorkspaceView = new ClientWorkspaceView(_workspaceSession);
            _clientWorkspaceView.BackRequested += (_, _) => CloseFeatureWorkspace();
            _clientWorkspaceView.OpenArchiveRequested += async (_, path) => await OpenIndexedArchiveAsync(path);
        }
        OpenFeatureWorkspace(_clientWorkspaceView, "Client Workshop");
    }

    private void OpenTextureWorkspaceClick(object? sender, RoutedEventArgs e) => OpenTextureWorkspace();
    private void OpenMapWorkspaceClick(object? sender, RoutedEventArgs e) => OpenMapWorkspace();
    public void OpenMapWorkspace(string? path = null)
    {
        if (_mapWorkspaceView is null) { _mapWorkspaceView = new MapWorkspaceView(_workspaceSession); _mapWorkspaceView.BackRequested += (_, _) => CloseFeatureWorkspace(); _mapWorkspaceView.OpenDbcRecordRequested += async (_, request) => await OpenDbcRecordAsync(request); }
        OpenFeatureWorkspace(_mapWorkspaceView, "Maps & World"); if (!string.IsNullOrWhiteSpace(path)) _ = _mapWorkspaceView.OpenAsync(path);
    }
    public void OpenLightingWorkspace(uint? lightId = null) { OpenMapWorkspace(); _mapWorkspaceView!.OpenLighting(lightId); }
    private async Task OpenDbcRecordAsync(DbcRecordNavigationRequest request)
    {
        CloseAllFeatureWorkspaces(); await LoadDbcAsync(request.Path); var document = Current;
        if (document is null || !document.FullPath.Equals(Path.GetFullPath(request.Path), StringComparison.OrdinalIgnoreCase)) return;
        var rows = DbcRecordIdentity.IndexRows(document.File, document.Schema.Columns, document.Schema.KeyStrategy);
        if (!rows.TryGetValue(request.Id, out var row)) { StatusText.Text = $"Opened {Path.GetFileName(request.Path)}, but record {request.Id:N0} is missing."; return; }
        ActiveDbcView.SelectSourceRow(row); StatusText.Text = $"Opened {Path.GetFileName(request.Path)} at exact record {request.Id:N0} · edit normally, then save or stage for MPQ.";
    }
    private async Task OpenMapWorkspaceAsync(string path) { OpenMapWorkspace(); await _mapWorkspaceView!.OpenAsync(path); }
    public void OpenTextureWorkspace(string? path = null)
    {
        if (_textureWorkspaceView is null)
        {
            _textureWorkspaceView = new TextureWorkspaceView(_workspaceSession);
            _textureWorkspaceView.BackRequested += (_, _) => CloseFeatureWorkspace();
            _textureWorkspaceView.ConsumerOpenRequested += async (_, consumerPath) => await OpenTextureConsumerAsync(consumerPath);
            _textureWorkspaceView.AppearanceDbcOpenRequested += async (_, request) => await OpenDbcRecordAsync(request);
            _textureWorkspaceView.AppearanceSqlOpenRequested += async (_, request) => await OpenCompleteSqlRowAsync(request);
        }
        OpenFeatureWorkspace(_textureWorkspaceView, "Texture Lab");
        if (!string.IsNullOrWhiteSpace(path)) _ = _textureWorkspaceView.OpenAsync(path);
    }

    private async Task OpenTextureWorkspaceAsync(string path)
    {
        OpenTextureWorkspace();
        await _textureWorkspaceView!.OpenAsync(path);
    }

    private async Task OpenTextureConsumerAsync(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".m2") { CloseAllFeatureWorkspaces(); await LoadM2Async(path); return; }
        if (extension is ".adt" or ".wdt") { await OpenMapWorkspaceAsync(path); return; }
        if (extension == ".wmo") { OpenNativeConversionWorkspace(); await _nativeConversionWorkspaceView!.OpenAsync(path); return; }
        await ShowErrorAsync("Unsupported texture consumer", $"Crucible indexed '{extension}', but no same-window viewer route exists for it.");
    }

    private async Task OpenIndexedArchiveAsync(string path)
    {
        if (_mpqWorkspaceView is null)
        {
            _mpqWorkspaceView = new MpqWorkspaceView(_workspaceSession);
            _mpqWorkspaceView.BackRequested += (_, _) => CloseFeatureWorkspace();
        }
        OpenFeatureWorkspace(_mpqWorkspaceView, "MPQ Patches & Archives");
        await _mpqWorkspaceView.OpenArchiveAsync(path);
    }

    private void OpenPatchBuilderWithPaths(IReadOnlyList<string> paths)
    {
        if (_mpqWorkspaceView is null)
        {
            _mpqWorkspaceView = new MpqWorkspaceView(_workspaceSession);
            _mpqWorkspaceView.BackRequested += (_, _) => CloseFeatureWorkspace();
        }
        _mpqWorkspaceView.StagePaths(paths);
        OpenFeatureWorkspace(_mpqWorkspaceView, "MPQ Patches & Archives");
    }
    private void OpenPatchBuilderWithEntries(IReadOnlyList<PatchEntry> entries)
    {
        if (_mpqWorkspaceView is null)
        {
            _mpqWorkspaceView = new MpqWorkspaceView(_workspaceSession);
            _mpqWorkspaceView.BackRequested += (_, _) => CloseFeatureWorkspace();
        }
        _mpqWorkspaceView.StageEntries(entries);
        OpenFeatureWorkspace(_mpqWorkspaceView, "MPQ Patches & Archives");
    }
    private void OpenServerSqlClick(object? sender, RoutedEventArgs e) => OpenServerSqlWorkspace();
    private void OpenWorkspaceSetupClick(object? sender, RoutedEventArgs e) => OpenWorkspaceSetup();
    private void OpenWorkspaceSetup()
    {
        if (_workspaceSetupView is null)
        {
            _workspaceSetupView = new WorkspaceSetupView(_workspaceSession);
            _workspaceSetupView.BackRequested += (_, _) => CloseFeatureWorkspace();
        }
        _workspaceSetupView.Activate();
        OpenFeatureWorkspace(_workspaceSetupView, "Workspace Setup");
    }
    internal void OpenServerSqlWorkspace()
    {
        if (_serverSqlWorkspaceView is null)
        {
            _serverSqlWorkspaceView = new ServerSqlWorkspaceView(_workspaceSession);
            _serverSqlWorkspaceView.BackRequested += (_, _) => CloseFeatureWorkspace();
        }
        OpenFeatureWorkspace(_serverSqlWorkspaceView, "Server & SQL");
        _serverSqlWorkspaceView.Activate();
    }
    private void OpenSqlWorkspaceClick(object? sender, RoutedEventArgs e) => OpenSqlWorkspace();
    public void OpenSqlWorkspace()
    {
        if (_sqlWorkspaceView is null)
        {
            _sqlWorkspaceView = new SqlWorkspaceView(_workspaceSession);
            _sqlWorkspaceView.BackRequested += (_, _) => CloseFeatureWorkspace();
            _sqlWorkspaceView.ConnectionRequested += (_, _) => OpenServerSqlWorkspace();
            _sqlWorkspaceView.GuidedEditRequested += (_, request) => OpenGuidedSqlRow(request);
            _sqlWorkspaceView.OpenDbcRequested += async (_, path) => { CloseAllFeatureWorkspaces(); await LoadDbcAsync(path); };
            _sqlWorkspaceView.OpenMpqRequested += async (_, path) => await OpenIndexedArchiveAsync(path);
            _sqlWorkspaceView.KnowledgeRequested += async (_, query) => await OpenKnowledgeAsync(query);
        }
        OpenFeatureWorkspace(_sqlWorkspaceView, "SQL Studio"); _sqlWorkspaceView.Activate();
    }

    public void OpenSqlFavorites()
    {
        OpenSqlWorkspace();
        _sqlWorkspaceView!.ActivateFavorites();
    }

    private async Task OpenCompleteSqlRowAsync(SqlGuidedEditRequest request)
    {
        try
        {
            OpenSqlWorkspace();
            await _sqlWorkspaceView!.OpenExactRowAsync(request.Table, request.Row);
        }
        catch (Exception exception)
        {
            DesktopCrashLogger.Log("Complete SQL row navigation failed", exception);
            await ShowErrorAsync("Could not open complete SQL row", exception.Message);
        }
    }

    private void OpenGuidedSqlRow(SqlGuidedEditRequest request)
    {
        if (request.Table.Equals("item_template", StringComparison.OrdinalIgnoreCase))
        {
            if (_itemWorkbenchView is null) { _itemWorkbenchView = new ItemWorkbenchView(_workspaceSession); _itemWorkbenchView.SqlStudioRequested += (_, _) => OpenSqlWorkspace(); _itemWorkbenchView.SqlFavoritesRequested += (_, _) => OpenSqlFavorites(); _itemWorkbenchView.MpqWorkspaceRequested += (_, _) => OpenMpqMergeWorkspace(); _itemWorkbenchView.ProjectWorkspaceRequested += (_, _) => OpenProjectWorkspace(); _itemWorkbenchView.FullSqlEditRequested += async (_, sqlRequest) => await OpenCompleteSqlRowAsync(sqlRequest); _itemWorkbenchView.ReferenceLookupRequested += (_, lookupRequest) => _ = OpenReferencePickerAsync(lookupRequest); }
            _itemWorkbenchView.OpenItemRow(request.Row); OpenFeatureWorkspace(_itemWorkbenchView, "Items & Sets");
        }
        else if (request.Table.Equals("creature_template", StringComparison.OrdinalIgnoreCase))
        {
            if (_creatureWorkspaceView is null) { _creatureWorkspaceView = new CreatureWorkspaceView(_workspaceSession); _creatureWorkspaceView.BackRequested += (_, _) => CloseFeatureWorkspace(); _creatureWorkspaceView.ProjectWorkspaceRequested += (_, _) => OpenProjectWorkspace(); _creatureWorkspaceView.MpqWorkspaceRequested += (_, _) => OpenMpqWorkspace(); _creatureWorkspaceView.PatchEntriesRequested += (_, entries) => OpenPatchBuilderWithEntries(entries); _creatureWorkspaceView.ReferenceLookupRequested += (_, lookupRequest) => _ = OpenReferencePickerAsync(lookupRequest); }
            _creatureWorkspaceView.OpenCreatureRow(request.Row); OpenFeatureWorkspace(_creatureWorkspaceView, "Creatures & NPCs");
        }
        else if (request.Table.Equals("gameobject_template", StringComparison.OrdinalIgnoreCase))
        {
            if (_gameObjectWorkspaceView is null) { _gameObjectWorkspaceView = new GameObjectWorkspaceView(_workspaceSession); _gameObjectWorkspaceView.BackRequested += (_, _) => CloseFeatureWorkspace(); _gameObjectWorkspaceView.ProjectWorkspaceRequested += (_, _) => OpenProjectWorkspace(); _gameObjectWorkspaceView.ReferenceLookupRequested += (_, lookupRequest) => _ = OpenReferencePickerAsync(lookupRequest); }
            _gameObjectWorkspaceView.OpenGameObjectRow(request.Row); OpenFeatureWorkspace(_gameObjectWorkspaceView, "Gameobjects");
        }
        else if (request.Table.Equals("quest_template", StringComparison.OrdinalIgnoreCase))
        {
            if (_questWorkspaceView is null) { _questWorkspaceView = new QuestWorkspaceView(_workspaceSession); _questWorkspaceView.BackRequested += (_, _) => CloseFeatureWorkspace(); _questWorkspaceView.ProjectWorkspaceRequested += (_, _) => OpenProjectWorkspace(); _questWorkspaceView.ReferenceLookupRequested += (_, lookupRequest) => _ = OpenReferencePickerAsync(lookupRequest); }
            _questWorkspaceView.OpenQuestRow(request.Row); OpenFeatureWorkspace(_questWorkspaceView, "Quests");
        }
        else if (BehaviorDomainCatalog.All.Any(domain => domain.TableName.Equals(request.Table, StringComparison.OrdinalIgnoreCase)))
        {
            var view = EnsureWorldDataWorkspace(); view.OpenRow(request.Table, request.Row);
            var title = request.Table.StartsWith("pet_", StringComparison.OrdinalIgnoreCase) || request.Table.Equals("spell_pet_auras", StringComparison.OrdinalIgnoreCase) ? "Pets & companions" : "Behaviors & dialogue";
            OpenFeatureWorkspace(view, title);
        }
    }

    private async Task RestoreWorkspaceSessionAsync()
    {
        try
        {
            StatusText.Text = "Restoring the saved Crucible workspace…";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var saved = (_workspaceSession.Settings.SavedWorkspaces ?? []).FirstOrDefault(profile =>
                profile.Name.Equals(_workspaceSession.Settings.WorkspaceName, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(profile.RootPath));
            saved ??= CrucibleWorkspaceLayoutService.LoadAllProfiles().FirstOrDefault(profile =>
                profile.Name.Equals(_workspaceSession.Settings.WorkspaceName, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(profile.RootPath));
            if (saved is not null)
            {
                await _workspaceSession.ConfigureWorkspaceAsync(saved, timeout.Token);
            }
            else await _workspaceSession.DetectServerAndConnectAsync(_workspaceSession.Settings.ServerRootPath, timeout.Token);
            StatusText.Text = $"Server ready · {_workspaceSession.Server?.CoreFamily} · {_workspaceSession.DatabaseCapabilities?.Database} · MySQL {_workspaceSession.DatabaseCapabilities?.ServerVersion} · {_workspaceSession.DatabaseTransportDescription}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Saved server workspace is currently unavailable: {exception.Message}";
        }
    }

    public void OpenAssetComparison(string? libraryRoot = null)
    {
        if (_assetComparisonView is null)
        {
            _assetComparisonView = new AssetComparisonView(_workspaceSession);
            _assetComparisonView.BackRequested += (_, _) => CloseFeatureWorkspace();
        }
        _assetComparisonView.Activate(libraryRoot);
        OpenFeatureWorkspace(_assetComparisonView, "Asset Comparison");
        Dispatcher.UIThread.Post(() => _assetComparisonView.Focus(), DispatcherPriority.Input);
        DesktopCrashLogger.Debug("UI", "asset-workspace-opened", ("library", libraryRoot));
    }

    private void OpenFeatureWorkspace(Control workspace, string title)
    {
        if (FeatureWorkspaceHost.IsVisible && FeatureWorkspaceHost.Child is Control current && !ReferenceEquals(current, workspace))
            _featureHistory.Push((current, _featureTitle));
        FeatureWorkspaceHost.Child = workspace;
        FeatureWorkspaceHost.IsVisible = true;
        MainHeader.IsVisible = EditorWorkspace.IsVisible = MainStatusBar.IsVisible = false;
        ApplyShellPaneState();
        _featureTitle = title;
        RuntimeStrip.ShowFeature(title, (workspace as IFeatureWorkspaceToolbar)?.FeatureToolbar);
        SuppressLegacyFeatureNavigation(workspace);
        Title = $"WoW Crucible — {title}";
        DesktopCrashLogger.Debug("UI", "feature-workspace-opened", ("title", title), ("view", workspace.GetType().Name), ("history_depth", _featureHistory.Count));
    }

    private void CloseFeatureWorkspace()
    {
        if (_featureHistory.TryPop(out var previous))
        {
            FeatureWorkspaceHost.Child = previous.Workspace;
            _featureTitle = previous.Title;
            RuntimeStrip.ShowFeature(previous.Title, (previous.Workspace as IFeatureWorkspaceToolbar)?.FeatureToolbar);
            SuppressLegacyFeatureNavigation(previous.Workspace);
            Title = $"WoW Crucible — {previous.Title}";
            DesktopCrashLogger.Debug("UI", "feature-workspace-back", ("title", previous.Title), ("view", previous.Workspace.GetType().Name), ("history_depth", _featureHistory.Count));
            return;
        }
        CloseAllFeatureWorkspaces();
    }

    private void CloseAllFeatureWorkspaces()
    {
        _featureHistory.Clear();
        _featureTitle = string.Empty;
        _assetComparisonView?.Suspend();
        FeatureWorkspaceHost.IsVisible = false;
        FeatureWorkspaceHost.Child = null;
        RuntimeStrip.HideFeature();
        MainHeader.IsVisible = EditorWorkspace.IsVisible = MainStatusBar.IsVisible = true;
        RefreshShellContext();
        Title = "WoW Crucible";
        DesktopCrashLogger.Debug("UI", "feature-workspace-closed");
    }

    private static void SuppressLegacyFeatureNavigation(Control workspace)
    {
        foreach (var (control, parent, wrapper) in WalkWorkspaceControls(workspace))
        {
            if (control is not Button { Content: string label } button || label is not ("← Editor" or "← DBC editor" or "← DBC table" or "← Pets" or "← Back")) continue;
            button.IsVisible = false;
            if (parent is null) continue;
            var title = WalkWorkspaceControls(parent)
                .Select(entry => entry.Control)
                .OfType<TextBlock>()
                .FirstOrDefault(text => text.FontSize >= 16);
            if (title is not null) title.IsVisible = false;
            if (WalkWorkspaceControls(parent).Skip(1).All(entry => !entry.Control.IsVisible))
            {
                parent.IsVisible = false;
                if (wrapper is Border or ContentControl) wrapper.IsVisible = false;
            }
        }
    }

    private static IEnumerable<(Control Control, Control? Parent, Control? Wrapper)> WalkWorkspaceControls(Control root)
    {
        var stack = new Stack<(Control Control, Control? Parent, Control? Wrapper)>();
        stack.Push((root, null, null));
        while (stack.TryPop(out var current))
        {
            yield return current;
            var children = current.Control switch
            {
                Panel panel => panel.Children.OfType<Control>(),
                Border { Child: Control child } => [child],
                ContentControl { Content: Control child } => [child],
                _ => []
            };
            foreach (var child in children.Reverse())
                stack.Push((child, current.Control, current.Parent));
        }
    }
    private async void OpenCliGuideClick(object? sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "docs", "CLI-REFERENCE.md");
        var text = File.Exists(path)
            ? await File.ReadAllTextAsync(path)
            : "The complete CLI reference was not found beside this build. Run wowcrucible --help or wowcrucible <group> --help for the built-in command map.";
        var back = new Button { Content = "← Editor", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        back.Click += (_, _) => CloseFeatureWorkspace();
        var view = new UserControl
        {
            Content = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,Auto,*"), Margin = new Thickness(16),
                Children =
                {
                    back,
                    WithGridRow(new TextBlock { Text = "CLI REFERENCE · searchable with Ctrl+F after opening the Markdown file in an editor", Foreground = new SolidColorBrush(Color.Parse("#C58A2B")), FontSize = 11, FontWeight = FontWeight.Bold, Margin = new Thickness(2,10) }, 1),
                    WithGridRow(new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono,Consolas"), FontSize = 12 }, 2)
                }
            }
        };
        OpenFeatureWorkspace(view, "CLI Guide");
    }

    private static T WithGridRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }

    private void DbcScrollChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingScrollbars) return;
        DbcView.SetScrollOffsets(HorizontalDbcScroll.Value, VerticalDbcScroll.Value);
    }

    private void SyncScrollbars()
    {
        _syncingScrollbars = true;
        VerticalDbcScroll.Maximum = DbcView.VerticalMaximum;
        HorizontalDbcScroll.Maximum = DbcView.HorizontalMaximum;
        VerticalDbcScroll.ViewportSize = Math.Max(1, DbcView.Bounds.Height - 32);
        HorizontalDbcScroll.ViewportSize = Math.Max(1, DbcView.Bounds.Width - 58);
        VerticalDbcScroll.Value = Math.Min(DbcView.VerticalOffset, VerticalDbcScroll.Maximum);
        HorizontalDbcScroll.Value = Math.Min(DbcView.HorizontalOffset, HorizontalDbcScroll.Maximum);
        _syncingScrollbars = false;
    }

    private void SecondaryDbcScrollChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingScrollbars) return;
        SecondaryDbcView.SetScrollOffsets(SecondaryHorizontalDbcScroll.Value, SecondaryVerticalDbcScroll.Value);
    }

    private void SyncSecondaryScrollbars()
    {
        _syncingScrollbars = true;
        SecondaryVerticalDbcScroll.Maximum = SecondaryDbcView.VerticalMaximum;
        SecondaryHorizontalDbcScroll.Maximum = SecondaryDbcView.HorizontalMaximum;
        SecondaryVerticalDbcScroll.ViewportSize = Math.Max(1, SecondaryDbcView.Bounds.Height - 32);
        SecondaryHorizontalDbcScroll.ViewportSize = Math.Max(1, SecondaryDbcView.Bounds.Width - 58);
        SecondaryVerticalDbcScroll.Value = Math.Min(SecondaryDbcView.VerticalOffset, SecondaryVerticalDbcScroll.Maximum);
        SecondaryHorizontalDbcScroll.Value = Math.Min(SecondaryDbcView.HorizontalOffset, SecondaryHorizontalDbcScroll.Maximum);
        _syncingScrollbars = false;
    }

    private void OpenCommandPaletteClick(object? sender, RoutedEventArgs e) => OpenCommandPalette();
    private void CloseCommandPaletteClick(object? sender, RoutedEventArgs e) => CloseCommandPalette();

    public void OpenCommandPalette(string? query = null)
    {
        if (DialogOverlayHost.IsVisible) return;
        if (query is not null) CommandPaletteSearch.Text = query;
        CommandPaletteHost.IsVisible = true;
        RefreshCommandPalette();
        Dispatcher.UIThread.Post(() => { CommandPaletteSearch.Focus(); CommandPaletteSearch.SelectAll(); }, DispatcherPriority.Input);
        DesktopCrashLogger.Debug("UI", "command-palette-opened", ("workspace", _featureTitle.Length == 0 ? "DBC tables" : _featureTitle), ("query", CommandPaletteSearch.Text), ("matches", _commandMatches.Count));
    }

    private async Task ExecuteRuntimeCommandAsync(string command)
    {
        try
        {
            if (command.Equals("ui.commands", StringComparison.Ordinal))
            {
                OpenCommandPalette();
                return;
            }
            if (!_commandRoutes.TryGetValue(command, out var route))
                throw new InvalidOperationException($"The application menu command '{command}' has no route.");
            await route();
        }
        catch (Exception exception)
        {
            DesktopCrashLogger.Log($"Application menu command failed: {command}", exception);
            await ShowErrorAsync("Could not open that Crucible workspace", exception.Message);
        }
    }

    private void CloseCommandPalette()
    {
        if (!CommandPaletteHost.IsVisible) return;
        CommandPaletteHost.IsVisible = false;
        DesktopCrashLogger.Debug("UI", "command-palette-closed", ("query", CommandPaletteSearch.Text), ("matches", _commandMatches.Count));
    }

    private void RefreshCommandPalette()
    {
        _commandMatches = CrucibleCommandCatalog.Search(CommandPaletteSearch.Text, 60);
        CommandPaletteResults.ItemsSource = _commandMatches;
        CommandPaletteResults.SelectedIndex = _commandMatches.Count > 0 ? 0 : -1;
        CommandPaletteEmptyText.IsVisible = _commandMatches.Count == 0;
        CommandPaletteStatus.Text = $"{_commandMatches.Count:N0} match(es) · {CrucibleCommandCatalog.All.Count:N0} native command(s)";
    }

    private static Control BuildCommandPaletteRow(CrucibleCommandDescriptor command)
    {
        var title = new TextBlock { Text = command.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        var category = new TextBlock { Text = command.Category.ToUpperInvariant(), Foreground = new SolidColorBrush(Color.Parse("#C58A2B")), FontSize = 10, FontWeight = FontWeight.Bold, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        var description = new TextBlock { Text = command.Description, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#8793A7")), FontSize = 11, Margin = new Thickness(0, 3, 8, 0) };
        var shortcut = new TextBlock { Text = command.Shortcut ?? string.Empty, Foreground = new SolidColorBrush(Color.Parse("#8793A7")), FontSize = 10, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        Grid.SetColumn(category, 1); Grid.SetRow(description, 1); Grid.SetColumn(shortcut, 1); Grid.SetRow(shortcut, 1);
        return new Grid { ColumnDefinitions = new("*,Auto"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 10, Margin = new Thickness(4, 5), Children = { title, category, description, shortcut } };
    }

    private IReadOnlyDictionary<string, Func<Task>> BuildCommandRoutes()
    {
        Func<Task> Done(Action action) => () => { action(); return Task.CompletedTask; };
        return new Dictionary<string, Func<Task>>(StringComparer.Ordinal)
        {
            ["workspace.setup"] = Done(OpenWorkspaceSetup),
            ["workspace.dbc"] = Done(CloseAllFeatureWorkspaces),
            ["workspace.dbc-layers"] = Done(() => OpenLayeredDbcsClick(null, new RoutedEventArgs())),
            ["workspace.dbd"] = Done(OpenDbdSchemaAudit),
            ["workspace.cache"] = Done(OpenCacheTableWorkspace),
            ["workspace.projects"] = Done(OpenProjectWorkspace),
            ["workspace.items"] = Done(OpenItemWorkbench),
            ["workspace.item-acquisition"] = Done(() => OpenItemAcquisition()),
            ["workspace.creatures"] = Done(OpenCreatureWorkspace),
            ["workspace.gameobjects"] = Done(OpenGameObjectWorkspace),
            ["workspace.quests"] = Done(OpenQuestWorkspace),
            ["workspace.pets"] = Done(OpenPetWorkspace),
            ["workspace.behaviors"] = Done(OpenBehaviorWorkspace),
            ["workspace.mpq"] = Done(OpenMpqWorkspace),
            ["workspace.mpq-merge"] = Done(OpenMpqMergeWorkspace),
            ["workspace.client"] = Done(() => OpenClientWorkspaceClick(null, new RoutedEventArgs())),
            ["workspace.maps"] = Done(() => OpenMapWorkspace()),
            ["workspace.lighting"] = Done(() => OpenLightingWorkspace()),
            ["workspace.textures"] = Done(() => OpenTextureWorkspace()),
            ["workspace.assets"] = Done(() => OpenAssetComparison()),
            ["workspace.conversion"] = Done(OpenNativeConversionWorkspace),
            ["workspace.knowledge"] = () => OpenKnowledgeAsync(_knowledgeContext),
            ["workspace.tools"] = OpenToolInventoryAsync,
            ["workspace.server"] = Done(OpenServerSqlWorkspace),
            ["workspace.sql"] = Done(OpenSqlWorkspace),
            ["workspace.sql-favorites"] = Done(OpenSqlFavorites),
            ["workspace.cli-guide"] = Done(() => OpenCliGuideClick(null, new RoutedEventArgs())),
            ["action.open-dbc"] = Done(() => OpenDbcClick(null, new RoutedEventArgs())),
            ["action.open-m2"] = Done(() => OpenM2Click(null, new RoutedEventArgs())),
            ["action.save"] = () => SaveCurrentAsync(false),
            ["action.save-as"] = () => SaveCurrentAsync(true),
            ["action.export-rows"] = Done(() => OpenDbcExportClick(null, new RoutedEventArgs())),
            ["action.import-rows"] = Done(() => OpenDbcImportClick(null, new RoutedEventArgs())),
            ["action.dbc-staging"] = Done(() => OpenDbcStagingClick(null, new RoutedEventArgs())),
            ["action.spell"] = Done(() => OpenSpellWorkspaceClick(null, new RoutedEventArgs())),
            ["action.logs"] = Done(DesktopCrashLogger.OpenDirectory),
            ["action.devbug"] = Done(() => DevbugModeToggle.IsChecked = DevbugModeToggle.IsChecked != true),
            ["action.back"] = Done(CloseFeatureWorkspace)
        };
    }

    private async Task ExecuteSelectedCommandAsync()
    {
        if (CommandPaletteResults.SelectedItem is not CrucibleCommandMatch match) return;
        var command = match.Command; CloseCommandPalette();
        try
        {
            if (!_commandRoutes.TryGetValue(command.Id, out var route)) throw new InvalidOperationException($"Command route is not implemented: {command.Id}");
            await route();
            DesktopCrashLogger.Debug("UI", "command-palette-executed", ("id", command.Id), ("title", command.Title));
        }
        catch (Exception exception)
        {
            DesktopCrashLogger.Log($"Command palette route failed: {command.Id}", exception);
            await ShowErrorAsync($"Could not open {command.Title}", exception.Message);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is Key.F or Key.H)
        {
            OpenFindReplace(e.Key == Key.H);
            e.Handled = true; return;
        }
        if (FindReplaceBar.IsVisible && e.Key == Key.Escape)
        {
            CloseFindReplace(); e.Handled = true; return;
        }
        if (FindReplaceBar.IsVisible && e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _ = NavigateFindAsync(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            e.Handled = true; return;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.K)
        {
            if (CommandPaletteHost.IsVisible) CloseCommandPalette(); else OpenCommandPalette();
            e.Handled = true; return;
        }
        if (e.Key == Key.F1) { _ = OpenKnowledgeAsync(_knowledgeContext); e.Handled = true; return; }
        if (CommandPaletteHost.IsVisible)
        {
            if (e.Key == Key.Escape) CloseCommandPalette();
            else if (e.Key == Key.Enter) _ = ExecuteSelectedCommandAsync();
            else if (e.Key == Key.Down && _commandMatches.Count > 0) CommandPaletteResults.SelectedIndex = Math.Min(_commandMatches.Count - 1, CommandPaletteResults.SelectedIndex + 1);
            else if (e.Key == Key.Up && _commandMatches.Count > 0) CommandPaletteResults.SelectedIndex = Math.Max(0, CommandPaletteResults.SelectedIndex - 1);
            else return;
            e.Handled = true; return;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key == Key.Left) { CloseFeatureWorkspace(); e.Handled = true; return; }
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (e.Key == Key.Z) Undo();
        else if (e.Key == Key.Y) Redo();
        else if (e.Key == Key.S) _ = SaveCurrentAsync(false);
        else if (e.Key == Key.O) OpenDbcClick(null, new RoutedEventArgs());
        else return;
        e.Handled = true;
    }

    private async void WindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingApproved) return;
        if (_closingPromptActive) { e.Cancel = true; return; }
        var dirty = _documents.Where(document => document.File.IsDirty).ToArray();
        if (dirty.Length == 0) return;
        e.Cancel = true;
        _closingPromptActive = true;
        try
        {
            var choice = await PromptSaveAsync($"{dirty.Length:N0} modified DBC file(s)");
            if (choice == SaveChoice.Cancel) return;
            if (choice == SaveChoice.Save)
            {
                foreach (var document in dirty)
                {
                    try { await Task.Run(() => document.File.Save(document.File.SourcePath, true)); }
                    catch (Exception exception) { DesktopCrashLogger.Log("Shutdown DBC save failed", exception); await ShowErrorAsync("Could not save all DBCs", exception.Message); return; }
                }
            }
            _closingApproved = true;
            Close();
        }
        finally { _closingPromptActive = false; }
    }

    private async Task<int?> PromptCloneCountAsync()
    {
        var input = new NumericUpDown { Minimum = 2, Maximum = 100_000, Value = 100, Increment = 1, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        var completion = new TaskCompletionSource<int?>();
        var cancel = new Button { Content = "Cancel" }; var create = new Button { Content = "Create clones", Classes = { "accent" } };
        cancel.Click += (_, _) => CompleteInlineDialog(completion, null); create.Click += (_, _) => CompleteInlineDialog(completion, (int?)input.Value);
        ShowInlineDialog(new StackPanel { Spacing = 14, Children = { new TextBlock { Text = "Number of copies", FontSize = 18, FontWeight = FontWeight.SemiBold }, input, new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { cancel, create } } } });
        return await completion.Task;
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var completion = new TaskCompletionSource<bool>();
        var no = new Button { Content = "Cancel" }; var yes = new Button { Content = "Continue", Classes = { "accent" } };
        no.Click += (_, _) => CompleteInlineDialog(completion, false); yes.Click += (_, _) => CompleteInlineDialog(completion, true);
        ShowInlineDialog(new StackPanel { Spacing = 15, Children = { new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { no, yes } } } });
        return await completion.Task;
    }

    private async Task<bool> EnsureBackupChoiceAsync()
    {
        if (_workspaceSession.Settings.BackupChoiceRemembered || _backupChoiceMadeThisSession) return true;
        var completion = new TaskCompletionSource<bool>();
        var remember = new CheckBox { Content = "Remember this choice" };
        var no = new Button { Content = "Edit without backups" };
        var yes = new Button { Content = "Keep backups", Classes = { "accent" } };
        void Complete(bool enabled)
        {
            _workspaceSession.Settings.BackupsEnabled = enabled;
            _backupChoiceMadeThisSession = true;
            if (remember.IsChecked == true)
            {
                _workspaceSession.Settings.BackupChoiceRemembered = true;
                _workspaceSession.Settings.Save();
            }
            CrucibleBackupService.Configure(_workspaceSession.Settings.BackupRootPath, enabled, Math.Clamp(_workspaceSession.Settings.BackupRetentionPerSource, 1, 100), (long)Math.Clamp(_workspaceSession.Settings.BackupStorageLimitGiB, 1, 1024) * 1024 * 1024 * 1024);
            CompleteInlineDialog(completion, true);
        }
        no.Click += (_, _) => Complete(false); yes.Click += (_, _) => Complete(true);
        ShowInlineDialog(new StackPanel
        {
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = "Safety backups", FontSize = 19, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Would you like Crucible to make a safety backup before it replaces an existing file? Backups are stored in the visible Backups folder beside WoWCrucible.exe, have per-file retention and a total storage ceiling, and can be disabled or moved later in Workspace settings.", TextWrapping = TextWrapping.Wrap },
                remember,
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { no, yes } }
            }
        });
        return await completion.Task;
    }

    private async Task<SaveChoice> PromptSaveAsync(string name)
    {
        var completion = new TaskCompletionSource<SaveChoice>();
        var cancel = new Button { Content = "Cancel" }; var discard = new Button { Content = "Discard" }; var save = new Button { Content = "Save", Classes = { "accent" } };
        cancel.Click += (_, _) => CompleteInlineDialog(completion, SaveChoice.Cancel); discard.Click += (_, _) => CompleteInlineDialog(completion, SaveChoice.Discard); save.Click += (_, _) => CompleteInlineDialog(completion, SaveChoice.Save);
        ShowInlineDialog(new StackPanel { Spacing = 15, Children = { new TextBlock { Text = "Unsaved changes", FontSize = 19, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = $"Save changes to {name} before continuing?", TextWrapping = TextWrapping.Wrap }, new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { cancel, discard, save } } } });
        return await completion.Task;
    }

    private void SetBusy(string message) => StatusText.Text = message;

    private DbcSchemaCatalog ResolveSchemaCatalog()
    {
        lock (_schemaGate)
        {
            if (_schemaCatalog is not null) return _schemaCatalog;
            var path = FindSchemaDefinitionPath();
            if (path is not null)
            {
                try
                {
                    _schemaCatalog = DbcSchemaCatalog.Load(path);
                    _schemaSource = path;
                    return _schemaCatalog;
                }
                catch (Exception exception)
                {
                    DesktopCrashLogger.Log($"Could not load schema {path}; using built-in definitions", exception);
                }
            }
            _schemaCatalog = DbcSchemaCatalog.CreateBuiltIn12340();
            return _schemaCatalog;
        }
    }

    private string? FindDbdDefinitionsPath()
    {
        if (!string.IsNullOrWhiteSpace(_workspaceSession.Settings.DbdDefinitionsPath) && Directory.Exists(_workspaceSession.Settings.DbdDefinitionsPath))
            return Path.GetFullPath(_workspaceSession.Settings.DbdDefinitionsPath);
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory }.Distinct(StringComparer.OrdinalIgnoreCase))
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                foreach (var relative in new[] { Path.Combine("Tools", "WoWDBDefs", "definitions"), Path.Combine("WoWDBDefs", "definitions"), "definitions" })
                {
                    var candidate = Path.Combine(directory.FullName, relative);
                    if (Directory.Exists(candidate)) return candidate;
                }
            }
        return null;
    }

    private string? FindSchemaDefinitionPath()
    {
        if (!string.IsNullOrWhiteSpace(_workspaceSession.Settings.SchemaDefinitionPath) && File.Exists(_workspaceSession.Settings.SchemaDefinitionPath))
            return Path.GetFullPath(_workspaceSession.Settings.SchemaDefinitionPath);
        try
        {
            var settingsPath = CruciblePaths.SettingsFileForRead;
            if (File.Exists(settingsPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (document.RootElement.TryGetProperty("SchemaDefinitionPath", out var configured))
                {
                    var path = configured.GetString();
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return Path.GetFullPath(path);
                }
            }
        }
        catch (Exception exception) { DesktopCrashLogger.Log("Could not read configured schema path", exception); }

        const string fileName = "WotLK 3.3.5 (12340).xml";
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            foreach (var relative in new[]
            {
                Path.Combine("Definitions", fileName),
                Path.Combine("WDBX.Editor", "Definitions", fileName),
                Path.Combine("WDBXEditor", "WDBXEditor", "Definitions", fileName),
                Path.Combine("WDBX (wow edit)", "Definitions", fileName)
            })
            {
                var candidate = Path.Combine(directory.FullName, relative);
                if (File.Exists(candidate)) return candidate;
            }
            directory = directory.Parent;
        }
        return null;
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        var completion = new TaskCompletionSource<bool>();
        var close = new Button { Content = "Close", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        close.Click += (_, _) => CompleteInlineDialog(completion, true);
        ShowInlineDialog(new StackPanel { Spacing = 14, Children = { new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, close } });
        await completion.Task;
    }

    private void ShowInlineDialog(Control content)
    {
        DialogContent.Content = content;
        DialogOverlayHost.IsVisible = true;
        Dispatcher.UIThread.Post(() => content.Focus(), DispatcherPriority.Input);
    }

    private void CompleteInlineDialog<T>(TaskCompletionSource<T> completion, T value)
    {
        if (!completion.TrySetResult(value)) return;
        DialogOverlayHost.IsVisible = false;
        DialogContent.Content = null;
    }

    private enum SaveChoice { Cancel, Discard, Save }
}
