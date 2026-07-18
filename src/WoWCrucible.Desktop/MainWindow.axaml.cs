using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

public partial class MainWindow : Window
{
    private readonly List<DbcDocumentSession> _documents = [];
    private int _activeDocument = -1;
    private CancellationTokenSource? _searchCancellation;
    private long _lastRenderReport;
    private bool _closingApproved;
    private bool _closingPromptActive;
    private readonly object _schemaGate = new();
    private DbcSchemaCatalog? _schemaCatalog;
    private string _schemaSource = "Built-in 12340 definitions";
    private bool _syncingScrollbars;
    private readonly DesktopWorkspaceSession _workspaceSession = new(DesktopSettings.Load());
    private AssetComparisonView? _assetComparisonView;
    private ItemWorkbenchView? _itemWorkbenchView;
    private MpqWorkspaceView? _mpqWorkspaceView;
    private ClientWorkspaceView? _clientWorkspaceView;
    private TextureWorkspaceView? _textureWorkspaceView;
    private LayeredDbcWorkspaceView? _layeredDbcWorkspaceView;
    private CreatureWorkspaceView? _creatureWorkspaceView;
    private GameObjectWorkspaceView? _gameObjectWorkspaceView;
    private QuestWorkspaceView? _questWorkspaceView;
    private BehaviorWorkspaceView? _behaviorWorkspaceView;
    private ServerSqlWorkspaceView? _serverSqlWorkspaceView;
    private SqlWorkspaceView? _sqlWorkspaceView;
    private readonly Stack<(Control Workspace, string Title)> _featureHistory = new();
    private string _featureTitle = string.Empty;
    private readonly Dictionary<string, (WdbcFile File, IReadOnlyList<DbcColumn> Columns)> _referenceDbcCache = new(StringComparer.OrdinalIgnoreCase);

    private DbcDocumentSession? Current => _activeDocument >= 0 && _activeDocument < _documents.Count ? _documents[_activeDocument] : null;
    private WdbcFile? CurrentFile => Current?.File;
    private IReadOnlyList<DbcColumn> CurrentColumns => Current?.Schema.Columns ?? [];

    public MainWindow()
    {
        InitializeComponent();
        DevbugModeToggle.IsChecked = DesktopCrashLogger.IsDevbugEnabled;
        DesktopCrashLogger.Debug("UI", "main-window-created", ("devbug", DesktopCrashLogger.IsDevbugEnabled));
        DbcView.SelectionChanged += (_, selection) => ShowSelection(selection);
        DbcView.CellEditRequested += async (_, selection) => await EditCellAsync(selection);
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
        Closing += WindowClosing;
        Closed += (_, _) => { _assetComparisonView?.Dispose(); _itemWorkbenchView?.Dispose(); _mpqWorkspaceView?.Dispose(); _clientWorkspaceView?.Dispose(); _textureWorkspaceView?.Dispose(); _layeredDbcWorkspaceView?.Dispose(); _creatureWorkspaceView?.Dispose(); _gameObjectWorkspaceView?.Dispose(); _questWorkspaceView?.Dispose(); _behaviorWorkspaceView?.Dispose(); _serverSqlWorkspaceView?.Dispose(); _sqlWorkspaceView?.Dispose(); };
        if (Directory.Exists(_workspaceSession.Settings.ServerRootPath)) Dispatcher.UIThread.Post(async () => await RestoreWorkspaceSessionAsync(), DispatcherPriority.Background);
    }

    private void DevbugModeChanged(object? sender, RoutedEventArgs e)
    {
        var enabled = DevbugModeToggle.IsChecked == true;
        DesktopCrashLogger.SetDevbugMode(enabled);
        StatusText.Text = enabled
            ? $"Devbug Mode enabled · live terminal + {DesktopCrashLogger.DebugLogPath}"
            : "Devbug Mode disabled · normal mode only records failures";
    }

    public Task LoadPathAsync(string path)
    {
        DesktopCrashLogger.Debug("FILE", "load-path-requested", ("path", path));
        var extension = Path.GetExtension(path);
        return extension.Equals(".dbc", StringComparison.OrdinalIgnoreCase)
            ? LoadDbcAsync(path)
            : extension.Equals(".m2", StringComparison.OrdinalIgnoreCase)
                ? LoadM2Async(path)
                : extension.Equals(".blp", StringComparison.OrdinalIgnoreCase)
                    ? OpenTextureWorkspaceAsync(path)
                    : ShowErrorAsync("Unsupported file", "The desktop opens DBC, M2 and BLP files directly.");
    }

    private async void OpenDbcClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open one or more WotLK WDBC tables",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("WoW DBC tables") { Patterns = ["*.dbc"] }]
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
                var tableName = Path.GetFileNameWithoutExtension(path);
                var catalog = ResolveSchemaCatalog();
                var resolution = catalog.ResolveColumns(tableName, file.FieldCount);
                return new DbcDocumentSession(file, resolution, _schemaSource);
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
            await ShowErrorAsync("Could not open DBC", exception.Message);
        }
    }

    private void ActivateDocument(int index)
    {
        if (index < 0 || index >= _documents.Count) return;
        _activeDocument = index;
        var document = _documents[index];
        DesktopCrashLogger.Debug("DBC", "document-activated", ("path", document.FullPath), ("tab", index), ("dirty", document.File.IsDirty));
        SearchBox.Text = string.Empty;
        DbcView.SetDocument(document.File, document.Schema.Columns, Path.GetFileNameWithoutExtension(document.File.SourcePath), DecodedToggle.IsChecked == true);
        WelcomePanel.IsVisible = false;
        M2View.IsVisible = false;
        DbcHost.IsVisible = true;
        ShowDocumentSummary(document);
        RefreshTabs();
    }

    private void RefreshTabs()
    {
        DocumentTabsPanel.Children.Clear();
        for (var index = 0; index < _documents.Count; index++)
        {
            var captured = index;
            var button = new Button
            {
                Content = _documents[index].DisplayName,
                Padding = new Thickness(14, 9),
                CornerRadius = new CornerRadius(0),
                Background = index == _activeDocument ? new SolidColorBrush(Color.Parse("#202B3C")) : Brushes.Transparent,
                BorderBrush = index == _activeDocument ? new SolidColorBrush(Color.Parse("#C58A2B")) : Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, index == _activeDocument ? 2 : 0)
            };
            button.Click += (_, _) => ActivateDocument(captured);
            DocumentTabsPanel.Children.Add(button);
        }
    }

    private void ShowDocumentSummary(DbcDocumentSession document)
    {
        InspectorTitle.Text = Path.GetFileName(document.File.SourcePath);
        InspectorSummary.Text = $"{document.File.RowCount:N0} records · {document.File.FieldCount:N0} fields";
        InspectorDetail.Text = $"Container  WDBC\nRecord     {document.File.RecordSize:N0} bytes\nStrings    {document.File.StringTableSize:N0} bytes\nSchema     {document.Schema.MatchKind}\nDefinition {document.SchemaSource}\nRow key    {document.Schema.KeyStrategy.DisplayName(document.Schema.Columns)}\nSource     {document.File.SourcePath}";
    }

    private async void SaveClick(object? sender, RoutedEventArgs e) => await SaveCurrentAsync(false);
    private async void SaveAsClick(object? sender, RoutedEventArgs e) => await SaveCurrentAsync(true);

    private async Task<bool> SaveCurrentAsync(bool saveAs)
    {
        var document = Current;
        if (document is null) return false;
        var path = document.File.SourcePath;
        if (saveAs)
        {
            var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save DBC as",
                SuggestedFileName = Path.GetFileName(path),
                FileTypeChoices = [new FilePickerFileType("WoW DBC table") { Patterns = ["*.dbc"] }]
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
        SetBusy("Saving table atomically with backup…");
        var stopwatch = Stopwatch.StartNew();
        DesktopCrashLogger.Debug("DBC", "save-start", ("source", document.FullPath), ("destination", path), ("save_as", saveAs), ("dirty", document.File.IsDirty));
        try
        {
            if (saveAs) await Task.Run(() => document.File.SaveAs(path, true));
            else await Task.Run(() => document.File.Save(path, true));
            RefreshTabs();
            StatusText.Text = $"Saved {path} · previous file retained as .bak";
            DesktopCrashLogger.Debug("DBC", "save-success", ("path", path), ("rows", document.File.RowCount), ("duration_ms", stopwatch.Elapsed.TotalMilliseconds), ("backup", path + ".bak"));
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
        _documents.RemoveAt(_activeDocument);
        DesktopCrashLogger.Debug("DBC", "document-closed", ("path", document.FullPath), ("remaining", _documents.Count));
        if (_documents.Count > 0) ActivateDocument(Math.Min(_activeDocument, _documents.Count - 1));
        else
        {
            _activeDocument = -1;
            DbcHost.IsVisible = false;
            M2View.IsVisible = false;
            WelcomePanel.IsVisible = true;
            SearchBox.Text = string.Empty;
            RefreshTabs();
            InspectorTitle.Text = "Nothing selected";
            InspectorSummary.Text = "Open a table or model to begin.";
            InspectorDetail.Text = "Table metadata and selection details appear here.";
        }
    }

    private void ShowSelection(Controls.DbcSelectionEventArgs selection)
    {
        var document = Current;
        if (document is null) return;
        var semantic = DbcSemanticCatalog.Get(Path.GetFileNameWithoutExtension(document.File.SourcePath), selection.Column.Index, document.File, selection.Row);
        InspectorTitle.Text = semantic?.Label ?? selection.Column.Name;
        InspectorSummary.Text = selection.Value.Length == 0 ? "(empty)" : selection.Value;
        var choices = semantic is null ? string.Empty : $"\nKnown     {semantic.Options.Count:N0} {semantic.Kind.ToString().ToLowerInvariant()} option(s)\nEdit      Double-click the cell";
        InspectorDetail.Text = $"Row       {selection.Row + 1:N0}\nColumn    {selection.ColumnIndex:N0}\nField     {selection.Column.Name}\nType      {selection.Column.Type}\nOffset    {selection.Column.Offset:N0} bytes\nSize      {selection.Column.Size:N0} bytes{choices}";
    }

    private async Task EditCellAsync(Controls.DbcSelectionEventArgs selection)
    {
        var document = Current;
        if (document is null) return;
        var before = document.File.GetRaw(selection.Row, selection.Column);
        var editor = new CellEditorView(document.File, selection.Row, selection.Column);
        var completion = new TaskCompletionSource<string?>();
        editor.Completed += (_, value) => CompleteInlineDialog(completion, value);
        ShowInlineDialog(editor);
        var value = await completion.Task;
        if (value is null) return;
        try
        {
            var semantic = DbcSemanticCatalog.Get(Path.GetFileNameWithoutExtension(document.File.SourcePath), selection.Column.Index, document.File, selection.Row);
            if (semantic is null) document.File.SetDisplayValue(selection.Row, selection.Column, value);
            else document.File.SetRaw(selection.Row, selection.Column, semantic.Parse(value));
            var after = document.File.GetRaw(selection.Row, selection.Column);
            document.History.Record(selection.Row, selection.Column, before, after);
            DbcView.RefreshDocument(selection.Row);
            RefreshTabs();
            ShowSelection(selection with { Value = Convert.ToString(document.File.GetDisplayValue(selection.Row, selection.Column), CultureInfo.InvariantCulture) ?? string.Empty });
            StatusText.Text = before == after ? "Value was unchanged" : $"Modified {selection.Column.Name} · Ctrl+Z to undo";
            DesktopCrashLogger.Debug("DBC", "cell-edit", ("path", document.FullPath), ("row", selection.Row), ("column", selection.Column.Name), ("before_raw", before), ("after_raw", after), ("changed", before != after));
        }
        catch (Exception exception)
        {
            DesktopCrashLogger.Log("DBC cell edit rejected", exception);
            await ShowErrorAsync("Invalid cell value", exception.Message);
        }
    }

    private void UndoClick(object? sender, RoutedEventArgs e) => Undo();
    private void RedoClick(object? sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        var document = Current;
        if (document is null) return;
        var edit = document.History.Undo(document.File);
        if (edit is null) { StatusText.Text = "Nothing to undo in this DBC"; return; }
        DbcView.RefreshDocument(edit.Row); RefreshTabs(); StatusText.Text = $"Undid {edit.Description}";
        DesktopCrashLogger.Debug("DBC", "undo", ("path", document.FullPath), ("row", edit.Row), ("description", edit.Description));
    }

    private void Redo()
    {
        var document = Current;
        if (document is null) return;
        var edit = document.History.Redo(document.File);
        if (edit is null) { StatusText.Text = "Nothing to redo in this DBC"; return; }
        DbcView.RefreshDocument(edit.Row); RefreshTabs(); StatusText.Text = $"Redid {edit.Description}";
        DesktopCrashLogger.Debug("DBC", "redo", ("path", document.FullPath), ("row", edit.Row), ("description", edit.Description));
    }

    private void AddRowClick(object? sender, RoutedEventArgs e) => AddRow();
    private void CloneRowClick(object? sender, RoutedEventArgs e) => CloneRows(1);
    private async void CloneMultipleClick(object? sender, RoutedEventArgs e)
    {
        var count = await PromptCloneCountAsync();
        if (count is not null) CloneRows(count.Value);
    }

    private void AddRow()
    {
        var document = Current;
        if (document is null) return;
        try
        {
            RequireStructuralKey(document);
            ClearFilter();
            var row = document.File.AddBlankRow(document.IdColumn);
            document.History.Clear();
            DbcView.RefreshDocument(row); RefreshTabs();
            StatusText.Text = $"Created row {row + 1:N0} with the next available identity";
            DesktopCrashLogger.Debug("DBC", "row-added", ("path", document.FullPath), ("row", row), ("new_row_count", document.File.RowCount));
        }
        catch (Exception exception) { DesktopCrashLogger.Log("DBC row add failed", exception); _ = ShowErrorAsync("Could not add row", exception.Message); }
    }

    private void CloneRows(int count)
    {
        var document = Current;
        var source = DbcView.SelectedSourceRow;
        if (document is null || source < 0) { StatusText.Text = "Select a source row first"; return; }
        try
        {
            RequireStructuralKey(document);
            ClearFilter();
            var first = document.File.CloneRows(source, count, document.IdColumn);
            document.History.Clear();
            DbcView.RefreshDocument(first); RefreshTabs();
            StatusText.Text = $"Created {count:N0} clone(s) in one batch, starting at row {first + 1:N0}";
            DesktopCrashLogger.Debug("DBC", "rows-cloned", ("path", document.FullPath), ("source_row", source), ("count", count), ("first_new_row", first), ("new_row_count", document.File.RowCount));
        }
        catch (Exception exception) { DesktopCrashLogger.Log("DBC row clone failed", exception); _ = ShowErrorAsync("Could not clone row", exception.Message); }
    }

    private async void DeleteRowClick(object? sender, RoutedEventArgs e)
    {
        var document = Current;
        var row = DbcView.SelectedSourceRow;
        if (document is null || row < 0) { StatusText.Text = "Select a row first"; return; }
        if (document.Schema.KeyStrategy.Kind == DbcRecordKeyKind.VirtualRowIndex && row != document.File.RowCount - 1 &&
            !await ConfirmAsync("Virtual row identities will change", "This table uses row positions as identities. Deleting a non-trailing row renumbers every following record and can break references. Continue anyway?")) return;
        if (!await ConfirmAsync("Delete selected row?", $"Delete row {row + 1:N0} from {Path.GetFileName(document.File.SourcePath)}? This structural operation clears cell undo history.")) return;
        document.File.DeleteRows([row]);
        document.History.Clear();
        ClearFilter();
        DbcView.RefreshDocument(Math.Min(row, Math.Max(0, document.File.RowCount - 1)));
        RefreshTabs();
        StatusText.Text = $"Deleted row {row + 1:N0}";
        DesktopCrashLogger.Debug("DBC", "row-deleted", ("path", document.FullPath), ("row", row), ("new_row_count", document.File.RowCount));
    }

    private void OpenSpellWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        var document = Current;
        var row = DbcView.SelectedSourceRow;
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
        var view = new SpellWorkspaceView(document.File, row, document.Schema.Columns, _workspaceSession, changes => ApplySpellChanges(document, row, changes));
        view.BackRequested += (_, _) => CloseFeatureWorkspace();
        view.FullSqlEditRequested += async (_, request) => await OpenCompleteSqlRowAsync(request);
        view.ReferenceLookupRequested += (_, request) => _ = OpenReferencePickerAsync(request);
        OpenFeatureWorkspace(view, $"Spell {document.File.GetDisplayValue(row, document.Schema.Columns[0])}");
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
            DbcView.RefreshDocument(row);
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
        DbcView.SetFilteredRows(null);
    }

    private void DecodedChanged(object? sender, RoutedEventArgs e)
    {
        DbcView.SetDecoded(DecodedToggle.IsChecked == true);
        if (CurrentFile is not null) StatusText.Text = DecodedToggle.IsChecked == true ? "Decoded names enabled" : "Raw field values enabled";
    }

    private async void SearchChanged(object? sender, TextChangedEventArgs e)
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var token = _searchCancellation.Token;
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        var document = Current;
        if (document is null) return;
        if (query.Length == 0)
        {
            DbcView.SetFilteredRows(null);
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
            DbcView.SetFilteredRows(rows);
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
    private void OpenItemWorkbenchClick(object? sender, RoutedEventArgs e)
    {
        if (_itemWorkbenchView is null)
        {
            _itemWorkbenchView = new ItemWorkbenchView(_workspaceSession);
            _itemWorkbenchView.BackRequested += (_, _) => CloseFeatureWorkspace();
            _itemWorkbenchView.FullSqlEditRequested += async (_, request) => await OpenCompleteSqlRowAsync(request);
            _itemWorkbenchView.ReferenceLookupRequested += (_, request) => _ = OpenReferencePickerAsync(request);
        }
        OpenFeatureWorkspace(_itemWorkbenchView, "Items & Sets");
    }
    private void OpenCreatureWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        if (_creatureWorkspaceView is null)
        {
            _creatureWorkspaceView = new CreatureWorkspaceView(_workspaceSession);
            _creatureWorkspaceView.BackRequested += (_, _) => CloseFeatureWorkspace();
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
            _questWorkspaceView.ReferenceLookupRequested += (_, request) => _ = OpenReferencePickerAsync(request);
        }
        OpenFeatureWorkspace(_questWorkspaceView, "Quests");
    }
    private void OpenBehaviorWorkspaceClick(object? sender, RoutedEventArgs e) => OpenBehaviorWorkspace();
    public void OpenBehaviorWorkspace()
    {
        if (_behaviorWorkspaceView is null)
        {
            _behaviorWorkspaceView = new BehaviorWorkspaceView(_workspaceSession);
            _behaviorWorkspaceView.BackRequested += (_, _) => CloseFeatureWorkspace();
        }
        OpenFeatureWorkspace(_behaviorWorkspaceView, "Behaviors & dialogue");
    }
    private void OpenAssetComparisonClick(object? sender, RoutedEventArgs e) => OpenAssetComparison();
    private void OpenEditorWorkspaceClick(object? sender, RoutedEventArgs e) => CloseFeatureWorkspace();
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
    private void OpenMpqWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        if (_mpqWorkspaceView is null)
        {
            _mpqWorkspaceView = new MpqWorkspaceView(_workspaceSession);
            _mpqWorkspaceView.BackRequested += (_, _) => CloseFeatureWorkspace();
        }
        OpenFeatureWorkspace(_mpqWorkspaceView, "MPQ Patches & Archives");
    }
    private void OpenClientWorkspaceClick(object? sender, RoutedEventArgs e)
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
    public void OpenTextureWorkspace(string? path = null)
    {
        if (_textureWorkspaceView is null)
        {
            _textureWorkspaceView = new TextureWorkspaceView();
            _textureWorkspaceView.BackRequested += (_, _) => CloseFeatureWorkspace();
        }
        OpenFeatureWorkspace(_textureWorkspaceView, "Texture Lab");
        if (!string.IsNullOrWhiteSpace(path)) _ = _textureWorkspaceView.OpenAsync(path);
    }

    private async Task OpenTextureWorkspaceAsync(string path)
    {
        OpenTextureWorkspace();
        await _textureWorkspaceView!.OpenAsync(path);
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
    private void OpenServerSqlClick(object? sender, RoutedEventArgs e)
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
            _sqlWorkspaceView.GuidedEditRequested += (_, request) => OpenGuidedSqlRow(request);
            _sqlWorkspaceView.OpenDbcRequested += async (_, path) => { CloseAllFeatureWorkspaces(); await LoadDbcAsync(path); };
            _sqlWorkspaceView.OpenMpqRequested += async (_, path) => await OpenIndexedArchiveAsync(path);
        }
        OpenFeatureWorkspace(_sqlWorkspaceView, "SQL Studio"); _sqlWorkspaceView.Activate();
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
            if (_itemWorkbenchView is null) { _itemWorkbenchView = new ItemWorkbenchView(_workspaceSession); _itemWorkbenchView.BackRequested += (_, _) => CloseFeatureWorkspace(); _itemWorkbenchView.FullSqlEditRequested += async (_, sqlRequest) => await OpenCompleteSqlRowAsync(sqlRequest); _itemWorkbenchView.ReferenceLookupRequested += (_, lookupRequest) => _ = OpenReferencePickerAsync(lookupRequest); }
            _itemWorkbenchView.OpenItemRow(request.Row); OpenFeatureWorkspace(_itemWorkbenchView, "Items & Sets");
        }
        else if (request.Table.Equals("creature_template", StringComparison.OrdinalIgnoreCase))
        {
            if (_creatureWorkspaceView is null) { _creatureWorkspaceView = new CreatureWorkspaceView(_workspaceSession); _creatureWorkspaceView.BackRequested += (_, _) => CloseFeatureWorkspace(); _creatureWorkspaceView.ReferenceLookupRequested += (_, lookupRequest) => _ = OpenReferencePickerAsync(lookupRequest); }
            _creatureWorkspaceView.OpenCreatureRow(request.Row); OpenFeatureWorkspace(_creatureWorkspaceView, "Creatures & NPCs");
        }
        else if (request.Table.Equals("gameobject_template", StringComparison.OrdinalIgnoreCase))
        {
            if (_gameObjectWorkspaceView is null) { _gameObjectWorkspaceView = new GameObjectWorkspaceView(_workspaceSession); _gameObjectWorkspaceView.BackRequested += (_, _) => CloseFeatureWorkspace(); _gameObjectWorkspaceView.ReferenceLookupRequested += (_, lookupRequest) => _ = OpenReferencePickerAsync(lookupRequest); }
            _gameObjectWorkspaceView.OpenGameObjectRow(request.Row); OpenFeatureWorkspace(_gameObjectWorkspaceView, "Gameobjects");
        }
        else if (request.Table.Equals("quest_template", StringComparison.OrdinalIgnoreCase))
        {
            if (_questWorkspaceView is null) { _questWorkspaceView = new QuestWorkspaceView(_workspaceSession); _questWorkspaceView.BackRequested += (_, _) => CloseFeatureWorkspace(); _questWorkspaceView.ReferenceLookupRequested += (_, lookupRequest) => _ = OpenReferencePickerAsync(lookupRequest); }
            _questWorkspaceView.OpenQuestRow(request.Row); OpenFeatureWorkspace(_questWorkspaceView, "Quests");
        }
        else if (BehaviorDomainCatalog.All.Any(domain => domain.TableName.Equals(request.Table, StringComparison.OrdinalIgnoreCase)))
        {
            if (_behaviorWorkspaceView is null) { _behaviorWorkspaceView = new BehaviorWorkspaceView(_workspaceSession); _behaviorWorkspaceView.BackRequested += (_, _) => CloseFeatureWorkspace(); }
            _behaviorWorkspaceView.OpenRow(request.Table, request.Row); OpenFeatureWorkspace(_behaviorWorkspaceView, "Behaviors & dialogue");
        }
    }

    private async Task RestoreWorkspaceSessionAsync()
    {
        try
        {
            StatusText.Text = "Restoring the saved server workspace…";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _workspaceSession.DetectServerAndConnectAsync(_workspaceSession.Settings.ServerRootPath, timeout.Token);
            StatusText.Text = $"Server ready · {_workspaceSession.Server?.CoreFamily} · {_workspaceSession.DatabaseCapabilities?.Database} · MySQL {_workspaceSession.DatabaseCapabilities?.ServerVersion}";
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
        MainHeader.IsVisible = NavigationPane.IsVisible = NavigationSplitter.IsVisible = EditorWorkspace.IsVisible = InspectorSplitter.IsVisible = InspectorPane.IsVisible = MainStatusBar.IsVisible = false;
        _featureTitle = title;
        Title = $"WoW Crucible — {title}";
        DesktopCrashLogger.Debug("UI", "feature-workspace-opened", ("title", title), ("view", workspace.GetType().Name), ("history_depth", _featureHistory.Count));
    }

    private void CloseFeatureWorkspace()
    {
        if (_featureHistory.TryPop(out var previous))
        {
            FeatureWorkspaceHost.Child = previous.Workspace;
            _featureTitle = previous.Title;
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
        MainHeader.IsVisible = NavigationPane.IsVisible = NavigationSplitter.IsVisible = EditorWorkspace.IsVisible = InspectorSplitter.IsVisible = InspectorPane.IsVisible = MainStatusBar.IsVisible = true;
        Title = "WoW Crucible";
        DesktopCrashLogger.Debug("UI", "feature-workspace-closed");
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

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control)) return;
        if (e.Key == Avalonia.Input.Key.Z) Undo();
        else if (e.Key == Avalonia.Input.Key.Y) Redo();
        else if (e.Key == Avalonia.Input.Key.S) _ = SaveCurrentAsync(false);
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

    private static string? FindSchemaDefinitionPath()
    {
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
