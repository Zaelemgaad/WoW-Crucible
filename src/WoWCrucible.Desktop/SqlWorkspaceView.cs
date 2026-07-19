using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;
using WoWCrucible.Desktop.Controls;

namespace WoWCrucible.Desktop;

internal sealed record SqlGuidedEditRequest(string Table, IReadOnlyDictionary<string, object?> Row);

internal sealed class SqlWorkspaceView : UserControl, IDisposable
{
    private sealed record TableChoice(DatabaseTableCapability Table) { public override string ToString() => $"{Table.Name}  ·  {Table.Columns.Count} columns"; }
    private sealed record RelationChoice(DatabaseRelationCapability Relation) { public override string ToString() => $"{Relation.FromTable}.{Relation.FromColumn} → {Relation.ToTable}.{Relation.ToColumn} · {(Relation.Declared ? "FK" : "inferred")}"; }
    private sealed record FieldEditor(TextBox Text, CheckBox? Null, ComboBox? InsertMode);
    private sealed record QueryDisplayRow(int Number, IReadOnlyList<string> Columns, IReadOnlyList<object?> Values);
    private sealed record FavoriteDisplayRow(SqlRowFavorite Favorite, SqlFavoriteVerification Verification)
    {
        public string Identity => Favorite.Identity;
    }
    private readonly DesktopWorkspaceSession _session;
    private readonly SqlWorkspaceService _service = new();
    private readonly SqlAdministrationService _administration = new();
    private readonly TextBox _tableFilter = new() { PlaceholderText = "Filter tables…" };
    private readonly ComboBox _schemas = new() { PlaceholderText = "Database schema" };
    private readonly ListBox _tables = new();
    private readonly TextBox _rowSearch = new() { PlaceholderText = "Search this table by ID, name, or any of the first 24 fields…" };
    private readonly ComboBox _filterColumn = new() { PlaceholderText = "Exact column filter" };
    private readonly TextBox _filterValue = new() { PlaceholderText = "Exact value or <NULL>" };
    private readonly ComboBox _sortColumn = new() { PlaceholderText = "Default primary-key order" };
    private readonly ComboBox _sortDirection = new() { ItemsSource = new[] { "Ascending", "Descending" }, SelectedIndex = 0 };
    private readonly ComboBox _pageSize = new() { ItemsSource = new[] { 50, 100, 200, 500 }, SelectedItem = 200 };
    private readonly ComboBox _rowDisplay = new() { ItemsSource = new[] { "Compact rows", "Complete row cards" }, SelectedIndex = 0 };
    private readonly ListBox _rows = new();
    private readonly StackPanel _rowEditor = new() { Spacing = 6 };
    private readonly StackPanel _relationshipResults = new() { Spacing = 5 };
    private readonly SqlDependencyGraphView _dependencyGraph = new();
    private readonly StackPanel _dependencyList = new() { Spacing = 5 };
    private readonly TextBlock _dependencyStatus = Status("Select a SQL row, then analyze its recognized dependencies.");
    private readonly TextBox _dependencyLimit = new() { Text = "200", PlaceholderText = "Rows captured per dependency edge" };
    private readonly ListBox _indexes = new();
    private readonly TextBox _tableDdl = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono,Consolas") };
    private readonly TextBox _indexName = new() { PlaceholderText = "Index name" };
    private readonly TextBox _indexColumns = new() { PlaceholderText = "column1, column2" };
    private readonly CheckBox _indexUnique = new() { Content = "UNIQUE" };
    private readonly TextBlock _administrationStatus = Status("Select a table or refresh a server-administration view.");
    private readonly ListBox _structureColumns = new();
    private readonly TextBox _structureColumnName = new() { PlaceholderText = "Column name" };
    private readonly TextBox _structureDefinition = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, PlaceholderText = "Complete definition after the name, for example: int unsigned NOT NULL DEFAULT '0'" };
    private readonly ComboBox _structurePlacement = new() { ItemsSource = new[] { "Keep current / append new", "First column", "After column" }, SelectedIndex = 0 };
    private readonly ComboBox _structureAfter = new() { PlaceholderText = "Existing column" };
    private readonly TextBox _structureTableName = new() { PlaceholderText = "New table name" };
    private readonly TextBlock _structureStatus = Status("Load the selected table to edit its exact server-normalized column definitions.");
    private SqlTableDesignSnapshot? _structureSnapshot;
    private readonly ListBox _foreignKeys = new();
    private readonly ListBox _checkConstraints = new();
    private readonly TextBox _constraintName = new() { PlaceholderText = "Constraint name" };
    private readonly TextBox _constraintColumns = new() { PlaceholderText = "Source columns in order: column1, column2" };
    private readonly ComboBox _constraintReferenceTable = new() { PlaceholderText = "Referenced table" };
    private readonly TextBox _constraintReferenceColumns = new() { PlaceholderText = "Referenced columns in matching order" };
    private readonly ComboBox _constraintDeleteRule = new() { ItemsSource = new[] { "RESTRICT", "CASCADE", "SET NULL", "NO ACTION" }, SelectedIndex = 0 };
    private readonly ComboBox _constraintUpdateRule = new() { ItemsSource = new[] { "RESTRICT", "CASCADE", "SET NULL", "NO ACTION" }, SelectedIndex = 0 };
    private readonly TextBox _checkExpression = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, PlaceholderText = "Boolean expression, for example: `minlevel` <= `maxlevel`" };
    private readonly TextBlock _constraintStatus = Status("Load the selected table to inspect and design exact foreign-key and CHECK constraints.");
    private readonly ListBox _processes = new();
    private readonly TextBox _processDetail = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _databaseUsers = new();
    private readonly ListBox _databaseObjects = new();
    private readonly ComboBox _databaseObjectType = new() { ItemsSource = new[] { "All objects", "Views", "Triggers", "Procedures", "Functions", "Events" }, SelectedIndex = 0 };
    private readonly TextBox _databaseObjectSearch = new() { PlaceholderText = "Filter object names or details…" };
    private readonly TextBox _databaseObjectDefinition = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono,Consolas") };
    private readonly TextBox _viewName = new() { PlaceholderText = "View name" };
    private readonly TextBox _viewSelect = new() { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono,Consolas"), Text = "SELECT entry, name FROM item_template;" };
    private readonly TextBox _accountUser = new() { PlaceholderText = "Database account user" };
    private readonly TextBox _accountHost = new() { Text = "localhost", PlaceholderText = "Account host" };
    private readonly TextBox _accountPassword = new() { PasswordChar = '●', PlaceholderText = "New password (memory only)" };
    private readonly TextBox _accountTable = new() { PlaceholderText = "Optional table in active database" };
    private readonly CheckBox _accountGlobal = new() { Content = "Global *.* scope" };
    private readonly CheckBox _accountGrantOption = new() { Content = "WITH GRANT OPTION" };
    private readonly CheckBox _accountCreateLocked = new() { Content = "Create locked" };
    private readonly ListBox _accountPrivileges = new() { SelectionMode = SelectionMode.Multiple };
    private readonly TextBox _accountGrants = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono,Consolas") };
    private IReadOnlyList<SqlPrivilegeInfo> _supportedPrivileges = [];
    private readonly ComboBox _joinRelation = new() { PlaceholderText = "Recognized relationship" };
    private readonly ComboBox _joinType = new() { ItemsSource = new[] { "INNER", "LEFT", "RIGHT" }, SelectedIndex = 1 };
    private readonly TextBox _joinLimit = new() { Text = "200", PlaceholderText = "Row limit" };
    private readonly TextBox _joinSql = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono,Consolas") };
    private readonly TextBox _joinOutput = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono,Consolas") };
    private readonly TextBlock _pageStatus = Status("Connect Server & SQL to browse the live database.");
    private readonly TextBlock _status = Status("SQL Studio is idle.");
    private readonly TextBlock _connectionStatus = Status("Not connected");
    private readonly Border _confirmation = new() { IsVisible = false, BorderBrush = Brush.Parse("#6E5426"), BorderThickness = new Thickness(1), Padding = new Thickness(10) };
    private readonly TextBox _query = new() { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono,Consolas"), Text = "SELECT * FROM item_template WHERE entry IN (17, 17802);" };
    private readonly ListBox _queryResults = new();
    private readonly ComboBox _queryResultSets = new() { PlaceholderText = "Result set" };
    private readonly ComboBox _queryDisplay = new() { ItemsSource = new[] { "Complete field cards", "Compact rows" }, SelectedIndex = 0 };
    private readonly TextBlock _querySummary = Status("Run a read-only query to inspect structured results.");
    private readonly SqlQueryHistoryStore _queryHistoryStore = new();
    private readonly ListBox _queryHistory = new();
    private readonly TextBox _queryBookmarkLabel = new() { PlaceholderText = "Optional bookmark label" };
    private readonly ListBox _favorites = new();
    private readonly TextBox _favoriteSearch = new() { PlaceholderText = "Search labels, notes, tables, keys, DBCs, or MPQs…" };
    private readonly ComboBox _favoriteState = new() { ItemsSource = new[] { "All states", "Unchecked", "Live", "Missing", "Schema changed", "Check failed" }, SelectedIndex = 0 };
    private readonly TextBox _savedFavoriteLabel = new() { PlaceholderText = "Favorite label" };
    private readonly TextBox _savedFavoriteNotes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, PlaceholderText = "Notes about the row or planned edit" };
    private readonly TextBox _savedFavoriteDbc = new() { PlaceholderText = "Optional related DBC / DB2 path" };
    private readonly TextBox _savedFavoriteMpq = new() { PlaceholderText = "Optional related MPQ path" };
    private readonly TextBlock _favoriteStatus = Status("Favorites are portable and have not been checked against the live server yet.");
    private readonly SqlFavoriteWorkspaceService _favoriteService = new();
    private IReadOnlyList<SqlRowFavorite> _favoriteCache = [];
    private readonly Dictionary<string, SqlFavoriteVerification> _favoriteChecks = new(StringComparer.OrdinalIgnoreCase);
    private readonly TabControl _tabs;
    private readonly TextBox _favoriteNotes = new() { PlaceholderText = "Optional note: what you changed or why this row matters" };
    private readonly TextBox _favoriteDbc = new() { PlaceholderText = "Optional related DBC path" };
    private readonly TextBox _favoriteMpq = new() { PlaceholderText = "Optional related MPQ path" };
    private readonly Dictionary<string, FieldEditor> _editors = new(StringComparer.OrdinalIgnoreCase);
    private SqlTablePage? _page;
    private SqlRowRecord? _selectedRow;
    private bool _creatingRow;
    private bool _suppressTableSelection;
    private int _offset;
    private CancellationTokenSource? _operation;
    private DatabaseConnectionProfile? _profile;
    private DatabaseCapabilities? _capabilities;
    private bool _suppressSchemaSelection;
    private string? _browseTable;
    private SqlQueryResult? _queryResult;
    private SqlQueryBatch? _queryBatch;
    private IReadOnlyList<SqlDatabaseObjectInfo> _databaseObjectCache = [];

    public event EventHandler? BackRequested;
    public event EventHandler? ConnectionRequested;
    public event EventHandler<SqlGuidedEditRequest>? GuidedEditRequested;
    public event EventHandler<string>? OpenDbcRequested;
    public event EventHandler<string>? OpenMpqRequested;
    public event EventHandler<string>? KnowledgeRequested;

    public SqlWorkspaceView(DesktopWorkspaceSession session)
    {
        _session = session; _profile = session.DatabaseProfile; _capabilities = session.DatabaseCapabilities; _session.Changed += SessionChanged;
        var back = new Button { Content = "← Editor" }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var connection = new Button { Content = "Connect / change server SQL" }; connection.Click += (_, _) => ConnectionRequested?.Invoke(this, EventArgs.Empty);
        var refreshSchemas = new Button { Content = "↻ Schemas" }; refreshSchemas.Click += async (_, _) => await LoadSchemasAsync();
        var fieldHelp = new Button { Content = "? Table / field help" }; fieldHelp.Click += (_, _) => KnowledgeRequested?.Invoke(this, KnowledgeContext());
        var heading = new Grid { ColumnDefinitions = new("Auto,*"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 8, RowSpacing = 6, Margin = new Thickness(12, 8), Children = { back, WithColumn(new TextBlock { Text = "SQL STUDIO", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) }, 1), WithRow(new WrapPanel { Children = { _connectionStatus, connection, _schemas, refreshSchemas, fieldHelp } }, 1) } };
        Grid.SetColumnSpan(heading.Children[^1], 2);
        _tabs = new TabControl { Margin = new Thickness(10), Items = { new TabItem { Header = "Tables & rows", Content = BrowsePage() }, new TabItem { Header = "SQL query", Content = QueryPage() }, new TabItem { Header = "Favorites", Content = FavoritesPage() }, new TabItem { Header = "Dependency graph", Content = DependencyPage() }, new TabItem { Header = "Schema & server", Content = AdministrationPage() } } };
        Content = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), Children = { new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 0, 0, 1), Child = heading }, WithRow(_tabs, 1), WithRow(_confirmation, 2), WithRow(new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 6), Child = _status }, 3) } };
        _tableFilter.TextChanged += (_, _) => PopulateTables(); _tables.SelectionChanged += async (_, _) => await SelectTableAsync();
        _schemas.SelectionChanged += async (_, _) => { if (!_suppressSchemaSelection && _schemas.SelectedItem is string database) await SwitchSchemaAsync(database); };
        _rows.SelectionChanged += (_, _) => SelectRow();
        _rows.DoubleTapped += (_, _) => OpenSelectedDecodedEditor();
        _rowDisplay.SelectionChanged += (_, _) => ApplyRowTemplate();
        _queryDisplay.SelectionChanged += (_, _) => ApplyQueryResultTemplate();
        _queryResultSets.SelectionChanged += (_, _) => SelectQueryResultSet();
        _processes.SelectionChanged += (_, _) => ShowSelectedProcess();
        _databaseUsers.SelectionChanged += (_, _) => SelectDatabaseUser();
        _databaseObjectSearch.TextChanged += (_, _) => FilterDatabaseObjects();
        _databaseObjectType.SelectionChanged += (_, _) => FilterDatabaseObjects();
        _databaseObjects.SelectionChanged += async (_, _) => await LoadSelectedDatabaseObjectAsync();
        _structureColumns.SelectionChanged += (_, _) => SelectStructureColumn();
        _foreignKeys.SelectionChanged += (_, _) => SelectForeignKey();
        _checkConstraints.SelectionChanged += (_, _) => SelectCheckConstraint();
        _constraintReferenceTable.SelectionChanged += (_, _) => UpdateConstraintReferenceColumns();
        _favoriteSearch.TextChanged += (_, _) => ApplyFavoriteFilter();
        _favoriteState.SelectionChanged += (_, _) => ApplyFavoriteFilter();
        _favorites.SelectionChanged += (_, _) => SelectFavorite();
        _favorites.DoubleTapped += async (_, _) => await OpenFavoriteAsync(CanOpenSelectedFavoriteInGuidedEditor());
        RefreshConnectionStatus(); RefreshFavorites(); RefreshQueryHistory(); PopulateTables(); PopulateRelations();
    }

    public void Activate() { PopulateTables(); if (_tables.SelectedItem is null && _tables.ItemCount > 0) _tables.SelectedIndex = 0; _ = LoadSchemasAsync(); }

    private string KnowledgeContext()
    {
        var table = (_tables.SelectedItem as TableChoice)?.Table.Name ?? _page?.Table ?? string.Empty;
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        var field = _editors.FirstOrDefault(pair => ReferenceEquals(pair.Value.Text, focused) || ReferenceEquals(pair.Value.Null, focused) || ReferenceEquals(pair.Value.InsertMode, focused)).Key;
        field ??= (_structureColumns.SelectedItem as SqlTableColumnDefinition)?.Name ?? _filterColumn.SelectedItem as string ?? _sortColumn.SelectedItem as string;
        return string.Join(' ', new[] { table, field }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    public async Task OpenExactRowAsync(string tableName, IReadOnlyDictionary<string, object?> values)
    {
        if (_capabilities is null || _profile is null) throw new InvalidOperationException("Connect Server & SQL before opening a complete row.");
        var table = _capabilities.FindTable(tableName) ?? throw new NotSupportedException($"The connected database has no {tableName} table.");
        var primary = table.Columns.Where(column => column.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase)).Select(column => column.Name).ToArray();
        if (primary.Length == 0) throw new InvalidOperationException($"{table.Name} has no primary key, so an exact row cannot be reopened safely.");
        var key = primary.ToDictionary(name => name, name => values.TryGetValue(name, out var value) ? value : throw new InvalidOperationException($"The supplied row is missing primary-key column {name}."), StringComparer.OrdinalIgnoreCase);
        var row = await _service.ReadRowAsync(_profile, table, key) ?? throw new InvalidOperationException($"The exact {table.Name} row no longer exists.");
        ResetBrowseOptions(table.Name);
        _suppressTableSelection = true;
        try { PopulateTables(); _tables.SelectedItem = (_tables.ItemsSource as IEnumerable<TableChoice>)?.FirstOrDefault(choice => choice.Table.Name.Equals(table.Name, StringComparison.OrdinalIgnoreCase)); }
        finally { _suppressTableSelection = false; }
        _rowSearch.Text = string.Empty; _offset = 0; _page = new(table.Name, table.Columns, primary, 1, 0, 1, "exact row", [row]);
        _rows.ItemsSource = _page.Rows; ApplyRowTemplate();
        _pageStatus.Text = "Exact primary-key row · 1 row"; _tabs.SelectedIndex = 0; _rows.SelectedItem = row;
        _status.Text = $"Opened complete row {row.Display}. Every live-schema column remains editable.";
    }

    private Control BrowsePage()
    {
        var refresh = Button("Refresh", async () => await LoadPageAsync(false)); var search = Button("Search", async () => { _offset = 0; await LoadPageAsync(false); });
        var previous = Button("← Previous", async () => { _offset = Math.Max(0, _offset - (_page?.Limit ?? 200)); await LoadPageAsync(false); });
        var next = Button("Next →", async () => { if (_page is not null) _offset += _page.Limit; await LoadPageAsync(false); });
        var create = AccentButton("New row"); create.Click += (_, _) => BeginCreateRow();
        var exportCsv = new Button { Content = "Export table CSV" }; exportCsv.Click += async (_, _) => await ExportTableAsync(SqlExportFormat.Csv);
        var exportJson = new Button { Content = "Export table JSONL" }; exportJson.Click += async (_, _) => await ExportTableAsync(SqlExportFormat.JsonLines);
        var importCsv = new Button { Content = "Import CSV…" }; importCsv.Click += async (_, _) => await PrepareImportAsync();
        var controls = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                _rowSearch,
                new WrapPanel { Children = { refresh, search, _filterColumn, _filterValue } },
                new WrapPanel { Children = { new TextBlock { Text = "Sort", VerticalAlignment = VerticalAlignment.Center }, _sortColumn, _sortDirection, new TextBlock { Text = "Rows/page", VerticalAlignment = VerticalAlignment.Center }, _pageSize, _rowDisplay } },
                new WrapPanel { Children = { previous, next, create, exportCsv, exportJson, importCsv, _pageStatus } }
            }
        };
        var right = new Grid { RowDefinitions = new("Auto,2*,Auto,*"), Children = { controls, WithRow(new Border { BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), Child = _rows }, 1), WithRow(new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = Brush.Parse("#2B3445") }, 2), WithRow(new ScrollViewer { Content = _rowEditor }, 3) } };
        var left = new Grid { RowDefinitions = new("Auto,*"), Margin = new Thickness(0, 0, 8, 0), Children = { _tableFilter, WithRow(_tables, 1) } };
        return new ResponsiveSplitGrid(left, right, 1, 3);
    }

    private Control QueryPage()
    {
        var run = AccentButton("Run read-only batch"); run.Click += async (_, _) => await RunQueryAsync();
        var prepare = new Button { Content = "Prepare write statement" }; prepare.Click += (_, _) => PrepareStatement();
        var bookmark = new Button { Content = "★ Bookmark query" }; bookmark.Click += (_, _) => BookmarkCurrentQuery();
        var exportCsv = new Button { Content = "Export result CSV" }; exportCsv.Click += async (_, _) => await ExportQueryResultAsync(SqlExportFormat.Csv);
        var exportJson = new Button { Content = "Export result JSONL" }; exportJson.Click += async (_, _) => await ExportQueryResultAsync(SqlExportFormat.JsonLines);
        var copy = new Button { Content = "Copy selected row" }; copy.Click += async (_, _) => await CopySelectedQueryRowAsync();
        var controls = new WrapPanel { Children = { run, prepare, bookmark, _queryResultSets, _queryDisplay, copy, exportCsv, exportJson, _querySummary, new TextBlock { Text = "Up to 32 semicolon-separated read statements run sequentially. Every statement is validated independently; writes still require inline confirmation.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7"), VerticalAlignment = VerticalAlignment.Center } } };
        var results = new Border { BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), Child = _queryResults };
        ApplyQueryResultTemplate();
        var lower = new TabControl { Items = { new TabItem { Header = "Current results", Content = results }, new TabItem { Header = "History & bookmarks", Content = QueryHistoryPage() } } };
        return new Grid { RowDefinitions = new("2*,Auto,Auto,3*"), Children = { _query, WithRow(controls, 1), WithRow(new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = Brush.Parse("#2B3445") }, 2), WithRow(lower, 3) } };
    }

    private Control QueryHistoryPage()
    {
        _queryHistory.ItemTemplate = new FuncDataTemplate<SqlQueryHistoryEntry>((entry, _) => entry is null ? new TextBlock() : new StackPanel
        {
            Margin = new Thickness(4, 3),
            Children =
            {
                new TextBlock { Text = entry.Display, FontWeight = entry.Bookmarked ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = $"{entry.ResultSets:N0} result set(s) · {entry.Rows:N0} row(s) · {entry.DurationMs:N0} ms\n{entry.Sql}", TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono,Consolas"), FontSize = 10, Foreground = Brush.Parse("#8995A9") }
            }
        });
        var load = AccentButton("Load selected into editor"); load.Click += async (_, _) => await LoadQueryHistoryAsync();
        var remove = new Button { Content = "Remove selected" }; remove.Click += (_, _) => { if (_queryHistory.SelectedItem is SqlQueryHistoryEntry entry) { _queryHistoryStore.Remove(entry.Id); RefreshQueryHistory(); } };
        var clear = new Button { Content = "Clear non-bookmarked history" }; clear.Click += (_, _) => { _queryHistoryStore.ClearUnbookmarked(); RefreshQueryHistory(); };
        return new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 6, Children = { new WrapPanel { Children = { _queryBookmarkLabel, load, remove, clear, new TextBlock { Text = $"Stored locally at {CruciblePaths.SqlQueryHistoryFile}", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8995A9"), VerticalAlignment = VerticalAlignment.Center } } }, WithRow(_queryHistory, 1) } };
    }

    private Control FavoritesPage()
    {
        _favorites.ItemTemplate = new FuncDataTemplate<FavoriteDisplayRow>((item, _) => item is null ? new TextBlock() : new StackPanel
        {
            Margin = new Thickness(5, 4), Spacing = 2,
            Children =
            {
                new WrapPanel { Children = { new TextBlock { Text = item.Verification.Display, FontSize = 10, FontWeight = FontWeight.Bold, Foreground = FavoriteStateBrush(item.Verification.State), Margin = new Thickness(0, 0, 8, 0) }, new TextBlock { Text = $"{item.Favorite.Label}  ·  {item.Favorite.Database}.{item.Favorite.Table}", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap } } },
                new TextBlock { Text = $"{string.Join(", ", item.Favorite.Key.Select(pair => $"{pair.Key}={pair.Value}"))}{(string.IsNullOrWhiteSpace(item.Favorite.Notes) ? string.Empty : $"  ·  {item.Favorite.Notes}")}", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") },
                new TextBlock { Text = item.Verification.Detail, TextWrapping = TextWrapping.Wrap, FontSize = 10, Foreground = Brush.Parse("#7F8BA0") }
            }
        });
        var open = AccentButton("Open selected favorite"); open.Click += async (_, _) => await OpenFavoriteAsync();
        var decoded = new Button { Content = "Open favorite in decoded editor" }; decoded.Click += async (_, _) => await OpenFavoriteAsync(true);
        var verify = new Button { Content = "Verify selected" }; verify.Click += async (_, _) => await VerifyFavoritesAsync(verify, selectedOnly: true);
        var verifyVisible = new Button { Content = "Verify visible" }; verifyVisible.Click += async (_, _) => await VerifyFavoritesAsync(verifyVisible, selectedOnly: false);
        var save = AccentButton("Save favorite details"); save.Click += (_, _) => SaveFavoriteDetails();
        var remove = new Button { Content = "Remove favorite" }; remove.Click += (_, _) => RemoveFavorite();
        var openDbc = new Button { Content = "Open linked DBC" }; openDbc.Click += (_, _) => { if (SelectedFavorite() is { DbcPath: { Length: > 0 } path }) OpenDbcRequested?.Invoke(this, path); };
        var openMpq = new Button { Content = "Open linked MPQ" }; openMpq.Click += (_, _) => { if (SelectedFavorite() is { MpqPath: { Length: > 0 } path }) OpenMpqRequested?.Invoke(this, path); };
        var pickDbc = new Button { Content = "DBC / DB2…" }; pickDbc.Click += async (_, _) => await PickFavoritePathAsync(_savedFavoriteDbc, "Select a related client table", "DBC or DB2", "*.dbc", "*.db2");
        var pickMpq = new Button { Content = "MPQ…" }; pickMpq.Click += async (_, _) => await PickFavoritePathAsync(_savedFavoriteMpq, "Select a related MPQ patch", "MPQ", "*.mpq");
        var filter = new StackPanel { Spacing = 5, Children = { _favoriteSearch, new WrapPanel { Children = { _favoriteState, verify, verifyVisible } } } };
        var dbcPath = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 5, Children = { _savedFavoriteDbc, WithColumn(pickDbc, 1) } };
        var mpqPath = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 5, Children = { _savedFavoriteMpq, WithColumn(pickMpq, 1) } };
        var paths = new StackPanel { Spacing = 5, Children = { dbcPath, mpqPath } };
        var editor = new Grid { RowDefinitions = new("Auto,*,Auto,Auto,Auto"), RowSpacing = 6, Children = { _savedFavoriteLabel, WithRow(_savedFavoriteNotes, 1), WithRow(paths, 2), WithRow(new WrapPanel { Children = { save, remove, open, decoded, openDbc, openMpq } }, 3), WithRow(_favoriteStatus, 4) } };
        var body = new ResponsiveSplitGrid(_favorites, editor, 2, 3);
        return new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 7, Children = { filter, WithRow(body, 1) } };
    }

    private Control DependencyPage()
    {
        var analyze = AccentButton("Analyze selected row"); analyze.Click += async (_, _) => await AnalyzeRelationshipsAsync(analyze);
        var export = new Button { Content = "Capture complete dependency snapshot…" }; export.Click += async (_, _) => await ExportDependencySnapshotAsync(export);
        var controls = new Grid { ColumnDefinitions = new("Auto,Auto,*,*"), ColumnSpacing = 8, Children = { analyze, WithColumn(export, 1), WithColumn(new TextBlock { Text = "Per-edge capture limit", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right }, 2), WithColumn(_dependencyLimit, 3) } };
        var details = new ScrollViewer { Content = _dependencyList };
        return new Grid { RowDefinitions = new("Auto,2*,Auto,*,Auto"), RowSpacing = 7, Children = { controls, WithRow(new Border { BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), Child = _dependencyGraph }, 1), WithRow(new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = Brush.Parse("#2B3445") }, 2), WithRow(details, 3), WithRow(_dependencyStatus, 4) } };
    }

    private Control AdministrationPage()
    {
        _indexes.ItemTemplate = new FuncDataTemplate<SqlIndexInfo>((index, _) => new TextBlock { Text = index?.Display ?? string.Empty, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) });
        _processes.ItemTemplate = new FuncDataTemplate<SqlProcessInfo>((process, _) => new TextBlock { Text = process?.Display ?? string.Empty, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) });
        _databaseUsers.ItemTemplate = new FuncDataTemplate<SqlUserAccountInfo>((account, _) => new TextBlock { Text = account?.Display ?? string.Empty, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) });
        _databaseObjects.ItemTemplate = new FuncDataTemplate<SqlDatabaseObjectInfo>((item, _) => item is null ? new TextBlock() : new StackPanel { Margin = new Thickness(4, 3), Children = { new TextBlock { Text = item.Display, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }, new TextBlock { Text = $"Definer {item.Definer}{(item.Modified is null ? string.Empty : $" · modified {item.Modified:yyyy-MM-dd HH:mm:ss}")}", Foreground = Brush.Parse("#8995A9"), TextWrapping = TextWrapping.Wrap } } });
        _structureColumns.ItemTemplate = new FuncDataTemplate<SqlTableColumnDefinition>((column, _) => new TextBlock { Text = column?.Display ?? string.Empty, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) });
        var pages = new TabControl { Items = { new TabItem { Header = "Table DDL & indexes", Content = TableAdministrationPage() }, new TabItem { Header = "Columns & table changes", Content = TableDesignerPage() }, new TabItem { Header = "Foreign keys & checks", Content = ConstraintDesignerPage() }, new TabItem { Header = "Database objects", Content = DatabaseObjectsPage() }, new TabItem { Header = "Processes", Content = ProcessAdministrationPage() }, new TabItem { Header = "Database users", Content = UserAdministrationPage() }, new TabItem { Header = "Visual joins", Content = JoinDesignerPage() } } };
        return new Grid { RowDefinitions = new("*,Auto"), RowSpacing = 7, Children = { pages, WithRow(_administrationStatus, 1) } };
    }

    private Control TableAdministrationPage()
    {
        var refresh = AccentButton("Load selected table definition & indexes"); refresh.Click += async (_, _) => await LoadTableAdministrationAsync(refresh);
        var create = new Button { Content = "Review CREATE INDEX" }; create.Click += (_, _) => PrepareCreateIndex();
        var drop = new Button { Content = "Review DROP selected index" }; drop.Click += (_, _) => PrepareDropIndex();
        var controls = new WrapPanel { Children = { refresh, new StackPanel { Children = { new TextBlock { Text = "Index name", Foreground = Brush.Parse("#9AA5B7") }, _indexName } }, new StackPanel { Children = { new TextBlock { Text = "Columns, in index order", Foreground = Brush.Parse("#9AA5B7") }, _indexColumns } }, new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Children = { _indexUnique } }, create, drop } };
        var ddl = new Grid { RowDefinitions = new("Auto,*"), Children = { new TextBlock { Text = "Exact SHOW CREATE TABLE", FontWeight = FontWeight.SemiBold }, WithRow(_tableDdl, 1) } };
        var indexes = new Grid { RowDefinitions = new("Auto,*"), Children = { new TextBlock { Text = "Live indexes", FontWeight = FontWeight.SemiBold }, WithRow(_indexes, 1) } };
        return new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 7, Children = { controls, WithRow(new ResponsiveSplitGrid(ddl, indexes, 2, 1), 1) } };
    }

    private Control TableDesignerPage()
    {
        var load = AccentButton("Load selected table structure"); load.Click += async (_, _) => await LoadTableDesignerAsync(load);
        var fresh = new Button { Content = "New column draft" }; fresh.Click += (_, _) => ResetStructureColumnDraft();
        var add = new Button { Content = "Review ADD column" }; add.Click += (_, _) => PrepareTableDesign(SqlTableDesignOperation.AddColumn);
        var modify = new Button { Content = "Review MODIFY selected" }; modify.Click += (_, _) => PrepareTableDesign(SqlTableDesignOperation.ModifyColumn);
        var rename = new Button { Content = "Review RENAME selected" }; rename.Click += (_, _) => PrepareTableDesign(SqlTableDesignOperation.RenameColumn);
        var drop = new Button { Content = "Review DROP selected" }; drop.Click += (_, _) => PrepareTableDesign(SqlTableDesignOperation.DropColumn);
        var cloneTable = new Button { Content = "Review CLONE structure" }; cloneTable.Click += (_, _) => PrepareTableDesign(SqlTableDesignOperation.CloneStructure);
        var renameTable = new Button { Content = "Review RENAME table" }; renameTable.Click += (_, _) => PrepareTableDesign(SqlTableDesignOperation.RenameTable);
        var top = new WrapPanel { Children = { load, fresh, add, modify, rename, drop } };
        var placement = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 7, Children = { new StackPanel { Children = { new TextBlock { Text = "Column placement", Foreground = Brush.Parse("#9AA5B7") }, _structurePlacement } }, WithColumn(new StackPanel { Children = { new TextBlock { Text = "After which column", Foreground = Brush.Parse("#9AA5B7") }, _structureAfter } }, 1) } };
        var editor = new StackPanel { Spacing = 7, Children =
        {
            new TextBlock { Text = "Exact column editor", FontWeight = FontWeight.SemiBold },
            _structureColumnName,
            _structureDefinition,
            placement,
            new TextBlock { Text = "The definition is the complete clause after the quoted column name. Crucible preserves server-normalized definitions when loading a column and blocks statement injection, but MySQL decides whether existing values can be converted.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8995A9") },
            new StackPanel { Spacing = 5, Children = { new TextBlock { Text = "Table-level changes", FontWeight = FontWeight.SemiBold }, _structureTableName, new WrapPanel { Children = { cloneTable, renameTable } } } }
        } };
        var body = new ResponsiveSplitGrid(_structureColumns, new ScrollViewer { Content = editor }, 1, 2);
        return new Grid { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 7, Children = { top, WithRow(body, 1), WithRow(_structureStatus, 2) } };
    }

    private Control ConstraintDesignerPage()
    {
        _foreignKeys.ItemTemplate = new FuncDataTemplate<SqlForeignKeyDefinition>((constraint, _) => new TextBlock { Text = constraint?.Display ?? string.Empty, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) });
        _checkConstraints.ItemTemplate = new FuncDataTemplate<SqlCheckConstraintDefinition>((constraint, _) => new TextBlock { Text = constraint?.Display ?? string.Empty, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) });
        var load = AccentButton("Load selected table constraints"); load.Click += async (_, _) => await LoadConstraintsAsync(load);
        var addForeign = AccentButton("Review ADD foreign key"); addForeign.Click += (_, _) => PrepareConstraintDesign(SqlTableDesignOperation.AddForeignKey);
        var dropForeign = new Button { Content = "Review DROP selected foreign key" }; dropForeign.Click += (_, _) => PrepareConstraintDesign(SqlTableDesignOperation.DropForeignKey);
        var addCheck = AccentButton("Review ADD check"); addCheck.Click += (_, _) => PrepareConstraintDesign(SqlTableDesignOperation.AddCheckConstraint);
        var dropCheck = new Button { Content = "Review DROP selected check" }; dropCheck.Click += (_, _) => PrepareConstraintDesign(SqlTableDesignOperation.DropCheckConstraint);
        var inventories = new TabControl
        {
            Items =
            {
                new TabItem { Header = "Foreign keys", Content = _foreignKeys },
                new TabItem { Header = "CHECK constraints", Content = _checkConstraints }
            }
        };
        var foreignEditor = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Foreign-key draft", FontWeight = FontWeight.SemiBold },
                _constraintName,
                _constraintColumns,
                _constraintReferenceTable,
                _constraintReferenceColumns,
                new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "ON DELETE", Foreground = Brush.Parse("#9AA5B7") }, _constraintDeleteRule } },
                new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "ON UPDATE", Foreground = Brush.Parse("#9AA5B7") }, _constraintUpdateRule } },
                new WrapPanel { Children = { addForeign, dropForeign } },
                new TextBlock { Text = "Composite keys preserve the typed column order. SET NULL is blocked unless every source column is nullable. MySQL validates existing rows and may create a supporting source index.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8995A9") }
            }
        };
        var checkEditor = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "CHECK constraint draft", FontWeight = FontWeight.SemiBold },
                _checkExpression,
                new WrapPanel { Children = { addCheck, dropCheck } },
                new TextBlock { Text = "The expression is preserved exactly after delimiter/comment/balance validation. The server remains authoritative for supported functions and validates every existing row.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8995A9") }
            }
        };
        var editors = new TabControl { Items = { new TabItem { Header = "Foreign-key editor", Content = new ScrollViewer { Content = foreignEditor } }, new TabItem { Header = "CHECK editor", Content = new ScrollViewer { Content = checkEditor } } } };
        var body = new ResponsiveSplitGrid(inventories, editors, 1, 2);
        return new Grid { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 7, Children = { new WrapPanel { Children = { load, new TextBlock { Text = "Every plan is bound to the exact current SHOW CREATE TABLE hash and produces a before/after schema receipt.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8995A9"), VerticalAlignment = VerticalAlignment.Center } } }, WithRow(body, 1), WithRow(_constraintStatus, 2) } };
    }

    private Control DatabaseObjectsPage()
    {
        var refresh = AccentButton("Refresh objects"); refresh.Click += async (_, _) => await LoadDatabaseObjectsAsync(refresh);
        var export = new Button { Content = "Export all exact definitions…" }; export.Click += async (_, _) => await ExportDatabaseObjectsAsync(export);
        var drop = new Button { Content = "Review DROP selected" }; drop.Click += (_, _) => PrepareDropDatabaseObject();
        var enable = new Button { Content = "Review ENABLE event" }; enable.Click += (_, _) => PrepareEventState(true);
        var disable = new Button { Content = "Review DISABLE event" }; disable.Click += (_, _) => PrepareEventState(false);
        var reviewView = AccentButton("Review CREATE / REPLACE view"); reviewView.Click += (_, _) => PrepareCreateOrReplaceView();
        var controls = new StackPanel { Spacing = 4, Children = { _databaseObjectSearch, new WrapPanel { Children = { refresh, _databaseObjectType, export, drop, enable, disable } } } };
        var selected = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 5, Children = { new TextBlock { Text = "Exact SHOW CREATE definition", FontWeight = FontWeight.SemiBold }, WithRow(_databaseObjectDefinition, 1) } };
        var viewEditor = new Grid
        {
            RowDefinitions = new("Auto,Auto,*,Auto"), RowSpacing = 5,
            Children =
            {
                new TextBlock { Text = "Guided view editor · exactly one read-only SELECT", FontWeight = FontWeight.SemiBold },
                WithRow(_viewName, 1), WithRow(_viewSelect, 2), WithRow(new WrapPanel { Children = { reviewView, new TextBlock { Text = "DDL may implicitly commit. The exact SQL is always shown before execution.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8995A9"), VerticalAlignment = VerticalAlignment.Center } } }, 3)
            }
        };
        var details = new Grid { RowDefinitions = new("2*,Auto,*"), RowSpacing = 5, Children = { selected, WithRow(new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = Brush.Parse("#2B3445") }, 1), WithRow(viewEditor, 2) } };
        var body = new ResponsiveSplitGrid(_databaseObjects, details, 1, 2);
        return new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 7, Children = { controls, WithRow(body, 1) } };
    }

    private Control ProcessAdministrationPage()
    {
        var refresh = AccentButton("Refresh process list"); refresh.Click += async (_, _) => await LoadProcessesAsync(refresh);
        var kill = new Button { Content = "Review KILL selected connection" }; kill.Click += (_, _) => PrepareKillProcess();
        return new Grid { RowDefinitions = new("Auto,2*,Auto,*"), RowSpacing = 7, Children = { new WrapPanel { Children = { refresh, kill, new TextBlock { Text = "KILL is never immediate: Crucible shows the exact connection and requires a second confirmation.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7"), VerticalAlignment = VerticalAlignment.Center } } }, WithRow(_processes, 1), WithRow(new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = Brush.Parse("#2B3445") }, 2), WithRow(_processDetail, 3) } };
    }

    private Control UserAdministrationPage()
    {
        _accountPrivileges.ItemTemplate = new FuncDataTemplate<SqlPrivilegeInfo>((privilege, _) => new TextBlock { Text = privilege?.Display ?? string.Empty, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) });
        var refresh = AccentButton("Read accounts & supported privileges"); refresh.Click += async (_, _) => await LoadUsersAsync(refresh); var grants = new Button { Content = "Read exact grants" }; grants.Click += async (_, _) => await LoadAccountGrantsAsync(grants);
        var create = new Button { Content = "Review CREATE USER" }; create.Click += (_, _) => PrepareCreateUser(); var password = new Button { Content = "Review password change" }; password.Click += (_, _) => PrepareAccountPassword();
        var lockAccount = new Button { Content = "Review lock" }; lockAccount.Click += (_, _) => PrepareAccountLock(true); var unlock = new Button { Content = "Review unlock" }; unlock.Click += (_, _) => PrepareAccountLock(false); var drop = new Button { Content = "Review DROP USER" }; drop.Click += (_, _) => PrepareDropUser();
        var grant = new Button { Content = "Review GRANT" }; grant.Click += (_, _) => PreparePrivilegeChange(false); var revoke = new Button { Content = "Review REVOKE" }; revoke.Click += (_, _) => PreparePrivilegeChange(true);
        var readOnly = new Button { Content = "Select read-only preset" }; readOnly.Click += (_, _) => SelectPrivilegePreset("SELECT", "SHOW VIEW"); var content = new Button { Content = "Select content-editor preset" }; content.Click += (_, _) => SelectPrivilegePreset("SELECT", "INSERT", "UPDATE", "DELETE", "EXECUTE", "SHOW VIEW"); var clear = new Button { Content = "Clear privilege selection" }; clear.Click += (_, _) => _accountPrivileges.SelectedItems?.Clear();
        var accountFields = new StackPanel { Spacing = 5, Children = { new StackPanel { Children = { new TextBlock { Text = "Account user", Foreground = Brush.Parse("#9AA5B7") }, _accountUser } }, new StackPanel { Children = { new TextBlock { Text = "Account host", Foreground = Brush.Parse("#9AA5B7") }, _accountHost } } } };
        var scopeFields = new StackPanel { Spacing = 5, Children = { _accountTable, new WrapPanel { Children = { _accountGlobal, _accountGrantOption } } } };
        var editor = new Grid { RowDefinitions = new("Auto,Auto,Auto,Auto,Auto,*,Auto,Auto"), RowSpacing = 7, Children = { accountFields, WithRow(_accountPassword, 1), WithRow(new WrapPanel { Children = { _accountCreateLocked, create, password, lockAccount, unlock, drop } }, 2), WithRow(new TextBlock { Text = "New passwords stay in memory, are parameterized at execution, and appear only as <password supplied in memory> in review SQL. Every mutation requires the inline second confirmation below SQL Studio.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") }, 3), WithRow(scopeFields, 4), WithRow(_accountPrivileges, 5), WithRow(new WrapPanel { Children = { readOnly, content, clear, grant, revoke } }, 6), WithRow(_accountGrants, 7) } };
        var top = new WrapPanel { Children = { refresh, grants, new TextBlock { Text = "Account metadata and SHOW GRANTS are permission-aware. Password hashes are never queried.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7"), VerticalAlignment = VerticalAlignment.Center } } };
        var body = new ResponsiveSplitGrid(_databaseUsers, new ScrollViewer { Content = editor }, 1, 2);
        return new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 7, Children = { top, WithRow(body, 1) } };
    }

    private Control JoinDesignerPage()
    {
        var build = new Button { Content = "Build exact join SQL" }; build.Click += (_, _) => BuildJoinSql(); var run = AccentButton("Run read-only join"); run.Click += async (_, _) => await RunJoinAsync(run);
        var controls = new StackPanel { Spacing = 5, Children = { _joinRelation, new WrapPanel { Children = { _joinType, _joinLimit, build, run } } } };
        return new Grid { RowDefinitions = new("Auto,*,Auto,*"), RowSpacing = 7, Children = { controls, WithRow(_joinSql, 1), WithRow(new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = Brush.Parse("#2B3445") }, 2), WithRow(_joinOutput, 3) } };
    }

    private void PopulateTables()
    {
        var selected = (_tables.SelectedItem as TableChoice)?.Table.Name; var query = _tableFilter.Text?.Trim() ?? string.Empty;
        var values = (_capabilities?.Tables.Values ?? []).Where(table => query.Length == 0 || table.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(table => table.Name).Select(table => new TableChoice(table)).ToArray();
        _tables.ItemsSource = values; if (selected is not null) _tables.SelectedItem = values.FirstOrDefault(item => item.Table.Name.Equals(selected, StringComparison.OrdinalIgnoreCase));
    }

    private async Task SelectTableAsync()
    {
        if (_suppressTableSelection) return;
        if (_tables.SelectedItem is TableChoice selected && !string.Equals(_browseTable, selected.Table.Name, StringComparison.OrdinalIgnoreCase))
            ResetBrowseOptions(selected.Table.Name);
        _offset = 0; await LoadPageAsync(false);
    }

    private void ResetBrowseOptions(string tableName)
    {
        _browseTable = tableName; _rowSearch.Text = string.Empty; _filterColumn.SelectedItem = null; _filterValue.Text = string.Empty; _sortColumn.SelectedItem = null; _sortDirection.SelectedIndex = 0;
    }
    private async Task LoadPageAsync(bool selectFirst)
    {
        if (_profile is null || _tables.SelectedItem is not TableChoice selected) { _pageStatus.Text = "Connect Server & SQL and select a table."; return; }
        Begin($"Loading {selected.Table.Name}…");
        try
        {
            var profile = _profile; var search = _rowSearch.Text; PopulateBrowseColumns(selected.Table);
            var filterColumn = _filterColumn.SelectedItem as string; var sortColumn = _sortColumn.SelectedItem as string;
            var limit = _pageSize.SelectedItem is int requestedLimit ? requestedLimit : 200;
            _page = await _service.ReadPageAsync(profile, selected.Table, _offset, limit, search, filterColumn, _filterValue.Text, sortColumn, _sortDirection.SelectedIndex == 1, _operation!.Token); _offset = _page.Offset;
            _rows.ItemsSource = _page.Rows; ApplyRowTemplate();
            _pageStatus.Text = _page.TotalRows == 0 ? "No matching rows." : $"{_page.Offset + 1:N0}–{Math.Min(_page.Offset + _page.Rows.Count, _page.TotalRows):N0} of {_page.TotalRows:N0} · {_page.Columns.Count:N0} complete column(s)";
            if (selectFirst && _page.Rows.Count > 0) _rows.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { _status.Text = "Table load cancelled."; }
        catch (Exception exception) { Fail("Table load failed", exception); }
        finally { End(); }
    }

    private void PopulateBrowseColumns(DatabaseTableCapability table)
    {
        static string? Selected(ComboBox box) => box.SelectedItem as string;
        var previousFilter = Selected(_filterColumn); var previousSort = Selected(_sortColumn); var columns = table.Columns.Select(column => column.Name).ToArray();
        _filterColumn.ItemsSource = columns; _sortColumn.ItemsSource = columns;
        _filterColumn.SelectedItem = previousFilter is not null && columns.Contains(previousFilter, StringComparer.OrdinalIgnoreCase) ? columns.First(value => value.Equals(previousFilter, StringComparison.OrdinalIgnoreCase)) : null;
        _sortColumn.SelectedItem = previousSort is not null && columns.Contains(previousSort, StringComparer.OrdinalIgnoreCase) ? columns.First(value => value.Equals(previousSort, StringComparison.OrdinalIgnoreCase)) : null;
    }

    private void ApplyRowTemplate()
    {
        var complete = _rowDisplay.SelectedIndex == 1;
        _rows.ItemTemplate = new FuncDataTemplate<SqlRowRecord>((row, _) =>
        {
            if (row is null) return new TextBlock();
            var heading = new TextBlock { Text = RowSummary(row), FontWeight = FontWeight.SemiBold, TextWrapping = complete ? TextWrapping.Wrap : TextWrapping.NoWrap };
            if (!complete) return new Border { Padding = new Thickness(4), Child = heading };
            var fields = new TextBlock { Text = string.Join("  ·  ", row.Values.Select(pair => $"{pair.Key}={CellText(pair.Value)}")), TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") };
            return new Border { Padding = new Thickness(6), Child = new StackPanel { Spacing = 3, Children = { heading, fields } } };
        });
    }

    private void SelectRow()
    {
        _creatingRow = false; _selectedRow = _rows.SelectedItem as SqlRowRecord; _editors.Clear(); _rowEditor.Children.Clear(); _confirmation.IsVisible = false;
        _dependencyList.Children.Clear(); _dependencyGraph.SetGraph(_selectedRow is null || _page is null ? "Select a primary-keyed SQL row" : $"{_page.Table} · {_selectedRow.Display}", []);
        _dependencyStatus.Text = _selectedRow is null ? "Select a SQL row, then analyze its recognized dependencies." : "Ready to analyze exact incoming and outgoing dependency edges.";
        if (_selectedRow is null || _page is null) return;
        var heading = new StackPanel { Spacing = 6 };
        heading.Children.Add(new TextBlock { Text = $"Complete row editor · {_page.Table} · {_selectedRow.Display}", FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        var actions = new WrapPanel(); var favorite = new Button { Content = "★ Favorite" }; favorite.Click += (_, _) => FavoriteSelected(); actions.Children.Add(favorite);
        var clone = new Button { Content = "Clone complete row as new identity" }; clone.Click += (_, _) => BeginCreateRow(_selectedRow); actions.Children.Add(clone);
        if (CanOpenGuidedEditor(_page.Table)) { var guided = AccentButton("Open decoded editor"); guided.Click += (_, _) => OpenSelectedDecodedEditor(); actions.Children.Add(guided); }
        var delete = new Button { Content = "Delete exactly this row" }; delete.Click += (_, _) => PrepareDelete(); actions.Children.Add(delete); heading.Children.Add(actions);
        var browseDbc = new Button { Content = "DBC/DB2…" }; browseDbc.Click += async (_, _) => await PickFavoritePathAsync(_favoriteDbc, "Select a related client table", "DBC or DB2", "*.dbc", "*.db2");
        var browseMpq = new Button { Content = "MPQ…" }; browseMpq.Click += async (_, _) => await PickFavoritePathAsync(_favoriteMpq, "Select a related MPQ patch", "MPQ", "*.mpq");
        var dbcPath = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 5, Children = { _favoriteDbc, WithColumn(browseDbc, 1) } };
        var mpqPath = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 5, Children = { _favoriteMpq, WithColumn(browseMpq, 1) } };
        _rowEditor.Children.Add(heading); _rowEditor.Children.Add(new Grid { ColumnDefinitions = new("*,*,*"), ColumnSpacing = 8, Children = { _favoriteNotes, WithColumn(dbcPath, 1), WithColumn(mpqPath, 2) } });
        foreach (var column in _page.Columns)
        {
            var value = _selectedRow.Values.GetValueOrDefault(column.Name); var text = new TextBox { Text = CellText(value), IsReadOnly = column.Extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase) || column.Extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase) };
            var nullValue = new CheckBox { Content = "NULL", IsChecked = value is null, IsEnabled = column.Nullable && !text.IsReadOnly };
            nullValue.IsCheckedChanged += (_, _) => text.IsEnabled = nullValue.IsChecked != true; text.IsEnabled = value is not null;
            var label = new TextBlock { Text = $"{column.Name}  ·  {column.ColumnType}{(column.Key == "PRI" ? "  ·  PRIMARY KEY" : string.Empty)}", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            _rowEditor.Children.Add(new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 8, Children = { label, WithColumn(text, 1), WithColumn(nullValue, 2) } }); _editors[column.Name] = new(text, nullValue, null);
        }
        var save = AccentButton("Review complete row update"); save.Click += (_, _) => PrepareRowUpdate(); _rowEditor.Children.Add(save); AddRelationships();
    }

    private void BeginCreateRow(SqlRowRecord? source = null)
    {
        if (_page is null || _tables.SelectedItem is not TableChoice) { _status.Text = "Select a table first."; return; }
        _rows.SelectedItem = null; _creatingRow = true; _selectedRow = null; _editors.Clear(); _rowEditor.Children.Clear(); _confirmation.IsVisible = false;
        _rowEditor.Children.Add(new TextBlock { Text = source is null ? $"New {_page.Table} row · every schema column is available" : $"Clone complete {_page.Table} row · change the identity before insert", FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        _rowEditor.Children.Add(new TextBlock { Text = source is null ? "Choose VALUE, NULL, or OMIT independently for every live-schema field. This is INSERT-only: existing keys are never replaced." : $"Every non-generated value was copied from {source.Display}. Change the primary identity; duplicate keys are refused transactionally and never overwrite the source row.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") });
        foreach (var column in _page.Columns)
        {
            var generated = column.Extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase); var automatic = column.Extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase);
            var sourceValue = source?.Values.GetValueOrDefault(column.Name); var text = new TextBox { Text = source is null ? column.DefaultValue ?? string.Empty : CellText(sourceValue), IsReadOnly = generated };
            var required = !column.Nullable && column.DefaultValue is null && !automatic && !generated;
            var initialMode = generated || automatic ? "OMIT" : source is not null ? sourceValue is null ? "NULL" : "VALUE" : required ? "VALUE" : column.Nullable && column.DefaultValue is null ? "NULL" : "OMIT";
            var mode = new ComboBox { ItemsSource = new[] { "VALUE", "NULL", "OMIT" }, SelectedItem = initialMode, IsEnabled = !generated };
            mode.SelectionChanged += (_, _) => text.IsEnabled = (mode.SelectedItem as string) == "VALUE"; text.IsEnabled = initialMode == "VALUE";
            var label = new TextBlock { Text = $"{column.Name}  ·  {column.ColumnType}{(required ? "  ·  REQUIRED" : string.Empty)}{(automatic ? "  ·  AUTO" : string.Empty)}", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            _rowEditor.Children.Add(new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 8, Children = { label, WithColumn(text, 1), WithColumn(mode, 2) } }); _editors[column.Name] = new(text, null, mode);
        }
        var insert = AccentButton("Review new row insert"); insert.Click += (_, _) => PrepareInsert(); _rowEditor.Children.Add(insert);
    }

    private void FavoriteSelected()
    {
        if (_selectedRow is null || _page is null || _selectedRow.Key.Count == 0) { _status.Text = "Rows without a primary key cannot be favorited safely."; return; }
        var key = _selectedRow.Key.ToDictionary(pair => pair.Key, pair => (string?)CellText(pair.Value), StringComparer.OrdinalIgnoreCase);
        var name = _selectedRow.Values.FirstOrDefault(pair => pair.Key.Equals("name", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("LogTitle", StringComparison.OrdinalIgnoreCase)).Value;
        var label = string.IsNullOrWhiteSpace(Convert.ToString(name)) ? $"{_page.Table} · {_selectedRow.Display}" : $"{Convert.ToString(name)} · {_selectedRow.Display}";
        SqlFavoriteStore.Save(new(_profile?.Database ?? string.Empty, _page.Table, key, label, _favoriteNotes.Text ?? string.Empty, DateTimeOffset.UtcNow, EmptyNull(_favoriteDbc.Text), EmptyNull(_favoriteMpq.Text))); RefreshFavorites(); _status.Text = $"Favorited {label}.";
    }

    private void PrepareRowUpdate()
    {
        if (_selectedRow is null || _page is null || _profile is null) return;
        var values = _editors.ToDictionary(pair => pair.Key, pair => pair.Value.Null?.IsChecked == true ? null : ParseCell(_page.Columns.Single(column => column.Name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)), pair.Value.Text.Text), StringComparer.OrdinalIgnoreCase);
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => _confirmation.IsVisible = false;
        var confirm = AccentButton("Commit exactly this row"); confirm.Click += async (_, _) => { try { confirm.IsEnabled = false; await _service.UpdateRowAsync(_profile, _page.Columns.Count > 0 ? (_tables.SelectedItem as TableChoice)!.Table : throw new InvalidOperationException(), _selectedRow.Key, values); _confirmation.IsVisible = false; _status.Text = "One row updated transactionally."; await LoadPageAsync(false); } catch (Exception exception) { Fail("Row update failed", exception); } finally { confirm.IsEnabled = true; } };
        _confirmation.Child = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = $"Update every writable field of {_page.Table} where {_selectedRow.Display}? The primary key identifies exactly one row; no INSERT/DELETE is implied.", TextWrapping = TextWrapping.Wrap }, WithColumn(cancel, 1), WithColumn(confirm, 2) } }; _confirmation.IsVisible = true;
    }

    private void PrepareInsert()
    {
        if (!_creatingRow || _page is null || _profile is null || _tables.SelectedItem is not TableChoice selected) return;
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _editors)
        {
            var mode = pair.Value.InsertMode?.SelectedItem as string ?? "VALUE";
            if (mode == "OMIT") continue;
            var column = _page.Columns.Single(value => value.Name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
            values[pair.Key] = mode == "NULL" ? null : ParseCell(column, pair.Value.Text.Text);
        }
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => _confirmation.IsVisible = false;
        var confirm = AccentButton("Insert one new row"); confirm.Click += async (_, _) =>
        {
            try { confirm.IsEnabled = false; var result = await _service.InsertRowAsync(_profile, selected.Table, values); _confirmation.IsVisible = false; _status.Text = result.InsertedId > 0 ? $"One row inserted transactionally · generated ID {result.InsertedId}." : "One row inserted transactionally."; _creatingRow = false; await LoadPageAsync(false); }
            catch (Exception exception) { Fail("Row insert failed", exception); } finally { confirm.IsEnabled = true; }
        };
        _confirmation.Child = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = $"Insert one new row into {_page.Table} using {values.Count:N0} supplied field(s)? Existing primary keys are blocked; Crucible will not replace or upsert.", TextWrapping = TextWrapping.Wrap }, WithColumn(cancel, 1), WithColumn(confirm, 2) } }; _confirmation.IsVisible = true;
    }

    private void PrepareDelete()
    {
        if (_selectedRow is null || _page is null || _profile is null || _tables.SelectedItem is not TableChoice selected) return;
        if (_selectedRow.Key.Count == 0) { _status.Text = "This table has no primary key; Crucible refuses an ambiguous delete."; return; }
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => _confirmation.IsVisible = false;
        var confirm = new Button { Content = $"Delete {_selectedRow.Display}" }; confirm.Click += async (_, _) =>
        {
            try { confirm.IsEnabled = false; await _service.DeleteRowAsync(_profile, selected.Table, _selectedRow.Key); _confirmation.IsVisible = false; _status.Text = "Exactly one row deleted transactionally."; await LoadPageAsync(false); }
            catch (Exception exception) { Fail("Row delete failed", exception); } finally { confirm.IsEnabled = true; }
        };
        _confirmation.Child = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = $"Permanently delete exactly one {_page.Table} row where {_selectedRow.Display}? Crucible preflights that the full primary key matches one row and rolls back if it does not.", TextWrapping = TextWrapping.Wrap }, WithColumn(cancel, 1), WithColumn(confirm, 2) } }; _confirmation.IsVisible = true;
    }

    private void AddRelationships()
    {
        if (_selectedRow is null || _page is null || _capabilities is null) return;
        var relations = _capabilities.Relationships.Where(relation => relation.Touches(_page.Table)).ToArray();
        _rowEditor.Children.Add(new TextBlock { Text = relations.Length == 0 ? "Dependencies · none declared or recognized for this table" : $"Dependencies · {relations.Length:N0} recognized edge(s)", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        if (relations.Length == 0) return;
        _relationshipResults.Children.Clear();
        var analyze = AccentButton("Analyze exact dependency counts"); analyze.Click += async (_, _) => await AnalyzeRelationshipsAsync(analyze);
        _rowEditor.Children.Add(new TextBlock { Text = "Navigation uses the specific relationship column, never a broad all-field text match. Analyze before deleting, cloning, or moving an identity to see the exact number of matching rows on every recognized edge.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") });
        _rowEditor.Children.Add(analyze);
        var buttons = new WrapPanel();
        foreach (var relation in relations)
        {
            var outgoing = relation.FromTable.Equals(_page.Table, StringComparison.OrdinalIgnoreCase); var sourceColumn = outgoing ? relation.FromColumn : relation.ToColumn;
            var targetTable = outgoing ? relation.ToTable : relation.FromTable; var targetColumn = outgoing ? relation.ToColumn : relation.FromColumn;
            if (!_selectedRow.Values.TryGetValue(sourceColumn, out var value) || value is null) continue;
            var direction = outgoing ? "→" : "←"; var button = new Button { Content = $"{direction} {targetTable}.{targetColumn} = {CellText(value)}  ·  {(relation.Declared ? "FK" : "inferred")}" };
            button.Click += async (_, _) => await OpenRelatedAsync(targetTable, targetColumn, value); ToolTip.SetTip(button, relation.Description); buttons.Children.Add(button);
        }
        _rowEditor.Children.Add(buttons); _rowEditor.Children.Add(_relationshipResults);
    }

    private async Task AnalyzeRelationshipsAsync(Button button)
    {
        if (_selectedRow is null || _page is null || _capabilities is null || _profile is null) return;
        try
        {
            button.IsEnabled = false; Begin($"Analyzing dependencies for {_selectedRow.Display}…");
            var matches = await _service.AnalyzeRelationshipsAsync(_profile, _capabilities, _page.Table, _selectedRow.Values, _operation!.Token);
            _relationshipResults.Children.Clear();
            _dependencyList.Children.Clear(); _dependencyGraph.SetGraph($"{_page.Table} · {_selectedRow.Display}", matches);
            if (matches.Count == 0) _relationshipResults.Children.Add(Status("No relationship values are populated on this row."));
            foreach (var match in matches)
            {
                var direction = match.Outgoing ? "depends on" : "referenced by"; var open = new Button { HorizontalContentAlignment = HorizontalAlignment.Left, Content = $"{RelationshipCount(match)} · {direction} {match.TargetTable}.{match.TargetColumn} = {CellText(match.Value)}" };
                var graphOpen = new Button { HorizontalContentAlignment = HorizontalAlignment.Left, Content = open.Content };
                if (match.MatchingRows < 0)
                {
                    var dbcPath = ResolveDbcMirrorPath(match.TargetTable); open.IsEnabled = graphOpen.IsEnabled = dbcPath is not null;
                    if (dbcPath is not null) { open.Click += (_, _) => OpenDbcRequested?.Invoke(this, dbcPath); graphOpen.Click += (_, _) => OpenDbcRequested?.Invoke(this, dbcPath); }
                    ToolTip.SetTip(open, dbcPath is null ? $"{match.Relation.Description}. Configure the server DBC folder to open this file." : $"{match.Relation.Description}. Open {dbcPath}."); ToolTip.SetTip(graphOpen, ToolTip.GetTip(open));
                }
                else
                {
                    open.Click += async (_, _) => await OpenRelatedAsync(match.TargetTable, match.TargetColumn, match.Value); graphOpen.Click += async (_, _) => await OpenRelatedAsync(match.TargetTable, match.TargetColumn, match.Value); ToolTip.SetTip(open, match.Relation.Description); ToolTip.SetTip(graphOpen, match.Relation.Description);
                }
                _relationshipResults.Children.Add(open); _dependencyList.Children.Add(graphOpen);
            }
            if (matches.Count == 0) _dependencyList.Children.Add(Status("No populated recognized dependency edges exist for this row."));
            var summary = $"Analyzed {matches.Count:N0} populated dependency edge(s); {matches.Where(match => match.MatchingRows >= 0).Sum(match => match.MatchingRows):N0} exact SQL row reference(s); {matches.Count(match => match.MatchingRows < 0):N0} edge(s) require file-DBC resolution."; _status.Text = summary; _dependencyStatus.Text = summary; _tabs.SelectedIndex = 3;
        }
        catch (OperationCanceledException) { _status.Text = "Dependency analysis cancelled."; }
        catch (Exception exception) { Fail("Dependency analysis failed", exception); }
        finally { button.IsEnabled = true; End(); }
    }

    private async Task ExportDependencySnapshotAsync(Button button)
    {
        if (_selectedRow is null || _page is null || _capabilities is null || _profile is null) { _dependencyStatus.Text = "Select a primary-keyed SQL row first."; return; }
        if (!int.TryParse(_dependencyLimit.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) || limit is < 1 or > 500) { _dependencyStatus.Text = "Per-edge capture limit must be a whole number from 1 through 500."; return; }
        try
        {
            var file = await Storage().SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export read-only SQL dependency snapshot", SuggestedFileName = $"{_page.Table}-{SafeName(_selectedRow.Display)}.crucible-dependencies.json", FileTypeChoices = [new FilePickerFileType("Crucible dependency snapshot") { Patterns = ["*.crucible-dependencies.json", "*.json"] }] });
            var path = file?.TryGetLocalPath(); if (path is null) return;
            button.IsEnabled = false; Begin($"Capturing complete dependency rows for {_selectedRow.Display}…");
            var snapshot = await _service.CaptureDependencySnapshotAsync(_profile, _capabilities, _page.Table, _selectedRow, limit, _operation!.Token);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, _operation.Token);
            var summary = $"Captured the complete root row plus {snapshot.Edges.Sum(edge => edge.Rows.Count):N0} related row(s) across {snapshot.Edges.Count:N0} edge(s). {snapshot.Edges.Count(edge => edge.Truncated):N0} edge(s) were explicitly marked truncated."; _dependencyStatus.Text = summary; _status.Text = $"{summary} Snapshot: {path}";
        }
        catch (OperationCanceledException) { _dependencyStatus.Text = "Dependency snapshot capture cancelled."; }
        catch (Exception exception) { Fail("Dependency snapshot failed", exception); _dependencyStatus.Text = $"Dependency snapshot failed: {exception.Message}"; }
        finally { button.IsEnabled = true; End(); }
    }

    private async Task LoadTableAdministrationAsync(Button button)
    {
        if (_profile is null || _tables.SelectedItem is not TableChoice selected) { _administrationStatus.Text = "Select a table in Tables & rows first."; return; }
        try
        {
            button.IsEnabled = false; Begin($"Reading DDL and indexes for {selected.Table.Name}…"); _tableDdl.Text = await _administration.ShowCreateTableAsync(_profile, selected.Table, _operation!.Token); var indexes = await _administration.ReadIndexesAsync(_profile, selected.Table, _operation.Token); _indexes.ItemsSource = indexes; _indexColumns.PlaceholderText = $"Available: {string.Join(", ", selected.Table.Columns.Select(column => column.Name))}"; _administrationStatus.Text = $"{_profile.Database}.{selected.Table.Name} · {selected.Table.Columns.Count:N0} column(s) · {indexes.Count:N0} index(es).";
        }
        catch (OperationCanceledException) { _administrationStatus.Text = "Table administration load cancelled."; }
        catch (Exception exception) { Fail("Table administration load failed", exception); _administrationStatus.Text = exception.Message; }
        finally { button.IsEnabled = true; End(); }
    }

    private async Task LoadTableDesignerAsync(Button button, string? tableName = null)
    {
        if (_profile is null) { _structureStatus.Text = "Connect Server & SQL first."; return; }
        tableName ??= (_tables.SelectedItem as TableChoice)?.Table.Name;
        if (string.IsNullOrWhiteSpace(tableName)) { _structureStatus.Text = "Select a table in Tables & rows first."; return; }
        try
        {
            button.IsEnabled = false; Begin($"Reading exact structure for {tableName}…");
            _structureSnapshot = await new SqlTableDesignerService().InspectAsync(_profile, tableName, _operation!.Token);
            _structureColumns.ItemsSource = _structureSnapshot.Columns;
            _structureAfter.ItemsSource = _structureSnapshot.Columns.Select(column => column.Name).ToArray();
            _structureColumns.SelectedIndex = _structureSnapshot.Columns.Count > 0 ? 0 : -1;
            _structureTableName.Text = $"{_structureSnapshot.Table}_copy";
            _structureStatus.Text = $"{_structureSnapshot.Database}.{_structureSnapshot.Table} · {_structureSnapshot.Columns.Count:N0} exact column definition(s) · {_structureSnapshot.Indexes.Count:N0} index(es) · {_structureSnapshot.Relations.Count:N0} declared relationship(s).";
        }
        catch (OperationCanceledException) { _structureStatus.Text = "Table structure load cancelled."; }
        catch (Exception exception) { Fail("Table structure load failed", exception); _structureStatus.Text = exception.Message; }
        finally { button.IsEnabled = true; End(); }
    }

    private async Task LoadConstraintsAsync(Button button)
    {
        if (_profile is null || _tables.SelectedItem is not TableChoice selected) { _constraintStatus.Text = "Select a table in Tables & rows first."; return; }
        try
        {
            button.IsEnabled = false; Begin($"Reading exact constraints for {selected.Table.Name}…");
            _structureSnapshot = await new SqlTableDesignerService().InspectAsync(_profile, selected.Table.Name, _operation!.Token);
            RefreshConstraintControls();
            _constraintStatus.Text = $"{_structureSnapshot.Database}.{_structureSnapshot.Table} · {(_structureSnapshot.ForeignKeys ?? []).Count:N0} foreign key(s) · {(_structureSnapshot.CheckConstraints ?? []).Count:N0} CHECK constraint(s) · server {_structureSnapshot.ServerVersion}.";
        }
        catch (OperationCanceledException) { _constraintStatus.Text = "Constraint inspection cancelled."; }
        catch (Exception exception) { Fail("Constraint inspection failed", exception); _constraintStatus.Text = exception.Message; }
        finally { button.IsEnabled = true; End(); }
    }

    private void RefreshConstraintControls()
    {
        _foreignKeys.ItemsSource = _structureSnapshot?.ForeignKeys ?? [];
        _checkConstraints.ItemsSource = _structureSnapshot?.CheckConstraints ?? [];
        _constraintColumns.PlaceholderText = _structureSnapshot is null ? "Source columns in order" : $"Available source columns: {string.Join(", ", _structureSnapshot.Columns.Select(column => column.Name))}";
        var tables = (_structureSnapshot?.Tables?.Values ?? []).OrderBy(table => table.Name, StringComparer.OrdinalIgnoreCase).Select(table => table.Name).ToArray();
        var selectedTarget = _constraintReferenceTable.SelectedItem as string;
        _constraintReferenceTable.ItemsSource = tables;
        _constraintReferenceTable.SelectedItem = selectedTarget is not null ? tables.FirstOrDefault(table => table.Equals(selectedTarget, StringComparison.OrdinalIgnoreCase)) : null;
        UpdateConstraintReferenceColumns();
    }

    private void SelectForeignKey()
    {
        if (_foreignKeys.SelectedItem is not SqlForeignKeyDefinition constraint) return;
        _constraintName.Text = constraint.Name; _constraintColumns.Text = string.Join(", ", constraint.Columns);
        _constraintReferenceTable.SelectedItem = (_constraintReferenceTable.ItemsSource as IEnumerable<string>)?.FirstOrDefault(table => table.Equals(constraint.ReferencedTable, StringComparison.OrdinalIgnoreCase));
        _constraintReferenceColumns.Text = string.Join(", ", constraint.ReferencedColumns);
        _constraintDeleteRule.SelectedItem = constraint.DeleteRule; _constraintUpdateRule.SelectedItem = constraint.UpdateRule;
        _constraintStatus.Text = $"Loaded exact foreign key {constraint.Name}. Change the name before creating a variant, or drop this selected identity explicitly.";
    }

    private void SelectCheckConstraint()
    {
        if (_checkConstraints.SelectedItem is not SqlCheckConstraintDefinition constraint) return;
        _constraintName.Text = constraint.Name; _checkExpression.Text = constraint.Expression;
        _constraintStatus.Text = $"Loaded exact CHECK constraint {constraint.Name} · {(constraint.Enforced ? "enforced" : "not enforced")}. Change the name before creating a variant, or drop this selected identity explicitly.";
    }

    private void UpdateConstraintReferenceColumns()
    {
        var tableName = _constraintReferenceTable.SelectedItem as string;
        var table = tableName is null ? null : _structureSnapshot?.Tables?.Values.FirstOrDefault(candidate => candidate.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase));
        _constraintReferenceColumns.PlaceholderText = table is null ? "Referenced columns in matching order" : $"Available on {table.Name}: {string.Join(", ", table.Columns.Select(column => column.Name))}";
    }

    private void SelectStructureColumn()
    {
        if (_structureColumns.SelectedItem is not SqlTableColumnDefinition column) return;
        _structureColumnName.Text = column.Name;
        _structureDefinition.Text = column.Definition;
        _structureStatus.Text = $"Loaded exact server definition for {column.Name}. MODIFY keeps the name; RENAME uses the edited name and this complete definition.";
    }

    private void ResetStructureColumnDraft()
    {
        _structureColumns.SelectedItem = null;
        _structureColumnName.Text = string.Empty;
        _structureDefinition.Text = "int unsigned NOT NULL DEFAULT '0'";
        _structurePlacement.SelectedIndex = 0;
        _structureAfter.SelectedItem = null;
        _structureStatus.Text = "New column draft. Choose a name, complete definition, and placement, then review ADD column.";
    }

    private void PrepareTableDesign(SqlTableDesignOperation operation)
    {
        if (_profile is null || _structureSnapshot is null) { _structureStatus.Text = "Load the selected table structure first."; return; }
        try
        {
            var selected = _structureColumns.SelectedItem as SqlTableColumnDefinition;
            var placement = _structurePlacement.SelectedIndex switch { 1 => SqlColumnPlacement.First, 2 => SqlColumnPlacement.After, _ => SqlColumnPlacement.End };
            SqlTableDesignRequest request = operation switch
            {
                SqlTableDesignOperation.AddColumn => new(operation, NewName: _structureColumnName.Text, Definition: _structureDefinition.Text, Placement: placement, AfterColumn: _structureAfter.SelectedItem as string),
                SqlTableDesignOperation.ModifyColumn => new(operation, ColumnName: selected?.Name, Definition: _structureDefinition.Text, Placement: placement, AfterColumn: _structureAfter.SelectedItem as string),
                SqlTableDesignOperation.RenameColumn => new(operation, ColumnName: selected?.Name, NewName: _structureColumnName.Text, Definition: _structureDefinition.Text, Placement: placement, AfterColumn: _structureAfter.SelectedItem as string),
                SqlTableDesignOperation.DropColumn => new(operation, ColumnName: selected?.Name),
                SqlTableDesignOperation.CloneStructure => new(operation, NewName: _structureTableName.Text),
                SqlTableDesignOperation.RenameTable => new(operation, NewName: _structureTableName.Text),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
            var plan = new SqlTableDesignerService().Prepare(_profile, _structureSnapshot, request);
            var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => _confirmation.IsVisible = false;
            var confirm = operation is SqlTableDesignOperation.DropColumn or SqlTableDesignOperation.RenameTable ? new Button { Content = $"Execute {operation}" } : AccentButton($"Execute {operation}");
            confirm.Click += async (_, _) => await ApplyTableDesignAsync(confirm, plan);
            var warning = string.Join("\n", plan.Warnings.Select(value => $"• {value}"));
            _confirmation.Child = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = $"{(plan.Destructive ? "DESTRUCTIVE OR DATA-TRANSFORMING DDL" : "SCHEMA-CHANGING DDL")}\n{plan.Sql}\n\n{warning}\n\nThe plan is bound to the current SHOW CREATE TABLE hash. A changed table is refused. Successful apply writes an exact before/after receipt under {CruciblePaths.SqlSchemaBackupDirectory}.", TextWrapping = TextWrapping.Wrap }, WithColumn(cancel, 1), WithColumn(confirm, 2) } };
            _confirmation.IsVisible = true;
            _structureStatus.Text = $"Prepared {operation}; review the exact DDL and every warning below SQL Studio.";
        }
        catch (Exception exception) { _structureStatus.Text = $"Cannot prepare table change: {exception.Message}"; }
    }

    private void PrepareConstraintDesign(SqlTableDesignOperation operation)
    {
        if (_profile is null || _structureSnapshot is null) { _constraintStatus.Text = "Load the selected table constraints first."; return; }
        try
        {
            SqlTableDesignRequest request = operation switch
            {
                SqlTableDesignOperation.AddForeignKey => new(operation, NewName: _constraintName.Text, Columns: ParseColumnList(_constraintColumns.Text), ReferencedTable: _constraintReferenceTable.SelectedItem as string, ReferencedColumns: ParseColumnList(_constraintReferenceColumns.Text), DeleteRule: _constraintDeleteRule.SelectedItem as string, UpdateRule: _constraintUpdateRule.SelectedItem as string),
                SqlTableDesignOperation.DropForeignKey => new(operation, ColumnName: (_foreignKeys.SelectedItem as SqlForeignKeyDefinition)?.Name),
                SqlTableDesignOperation.AddCheckConstraint => new(operation, NewName: _constraintName.Text, CheckExpression: _checkExpression.Text),
                SqlTableDesignOperation.DropCheckConstraint => new(operation, ColumnName: (_checkConstraints.SelectedItem as SqlCheckConstraintDefinition)?.Name),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
            var plan = new SqlTableDesignerService().Prepare(_profile, _structureSnapshot, request);
            var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => _confirmation.IsVisible = false;
            var confirm = plan.Destructive ? new Button { Content = $"Execute {operation}" } : AccentButton($"Execute {operation}");
            confirm.Click += async (_, _) => await ApplyTableDesignAsync(confirm, plan);
            var warning = string.Join("\n", plan.Warnings.Select(value => $"• {value}"));
            _confirmation.Child = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = $"{(plan.Destructive ? "ENFORCEMENT-REMOVING DDL" : "SCHEMA-CHANGING DDL")}\n{plan.Sql}\n\n{warning}\n\nThe plan is bound to the current SHOW CREATE TABLE hash. A changed table is refused. Successful apply saves the exact before/after DDL receipt under {CruciblePaths.SqlSchemaBackupDirectory}.", TextWrapping = TextWrapping.Wrap }, WithColumn(cancel, 1), WithColumn(confirm, 2) } };
            _confirmation.IsVisible = true; _constraintStatus.Text = $"Prepared {operation}; review the exact DDL and every warning below SQL Studio.";
        }
        catch (Exception exception) { _constraintStatus.Text = $"Cannot prepare constraint change: {exception.Message}"; }
    }

    private async Task ApplyTableDesignAsync(Button button, SqlTableDesignPlan plan)
    {
        if (_profile is null) return;
        try
        {
            button.IsEnabled = false; Begin($"Applying stale-bound {plan.Request.Operation}…");
            var result = await new SqlTableDesignerService().ApplyAsync(_profile, plan, _operation!.Token);
            _confirmation.IsVisible = false;
            await _session.RefreshDatabaseAsync(CancellationToken.None);
            _profile = _session.DatabaseProfile; _capabilities = _session.DatabaseCapabilities;
            PopulateTables();
            var resultText = $"Applied {plan.Request.Operation}. Exact before/after receipt: {result.ReceiptPath}";
            _structureStatus.Text = resultText; _constraintStatus.Text = resultText; _status.Text = resultText;
            if (_profile is not null) _structureSnapshot = await new SqlTableDesignerService().InspectAsync(_profile, plan.ResultTable, CancellationToken.None);
            _structureColumns.ItemsSource = _structureSnapshot?.Columns;
            _structureAfter.ItemsSource = _structureSnapshot?.Columns.Select(column => column.Name).ToArray();
            _structureColumns.SelectedIndex = _structureSnapshot?.Columns.Count > 0 ? 0 : -1;
            RefreshConstraintControls();
            _tables.SelectedItem = (_tables.ItemsSource as IEnumerable<TableChoice>)?.FirstOrDefault(choice => choice.Table.Name.Equals(plan.ResultTable, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) { _structureStatus.Text = _constraintStatus.Text = "Table change cancelled before completion or refresh."; }
        catch (Exception exception) { Fail("Table design apply failed", exception); _structureStatus.Text = _constraintStatus.Text = exception.Message; }
        finally { button.IsEnabled = true; End(); }
    }

    private void PrepareCreateIndex()
    {
        if (_profile is null || _tables.SelectedItem is not TableChoice selected) { _administrationStatus.Text = "Select a table first."; return; }
        try
        {
            var columns = ParseColumnList(_indexColumns.Text); var sql = SqlAdministrationService.BuildCreateIndexSql(selected.Table, _indexName.Text ?? string.Empty, columns, _indexUnique.IsChecked == true);
            var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => _confirmation.IsVisible = false; var confirm = AccentButton("Execute CREATE INDEX");
            confirm.Click += async (_, _) => { try { confirm.IsEnabled = false; await _administration.CreateIndexAsync(_profile, selected.Table, _indexName.Text ?? string.Empty, columns, _indexUnique.IsChecked == true); _confirmation.IsVisible = false; _administrationStatus.Text = $"Created index {_indexName.Text} on {_profile.Database}.{selected.Table.Name}."; await LoadTableAdministrationAsync(confirm); } catch (Exception exception) { Fail("CREATE INDEX failed", exception); _administrationStatus.Text = exception.Message; } finally { confirm.IsEnabled = true; } };
            _confirmation.Child = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = $"Execute this schema-changing DDL? MySQL may implicitly commit it; transaction rollback is not promised.\n{sql}", TextWrapping = TextWrapping.Wrap }, WithColumn(cancel, 1), WithColumn(confirm, 2) } }; _confirmation.IsVisible = true;
        }
        catch (Exception exception) { _administrationStatus.Text = $"Cannot create index: {exception.Message}"; }
    }

    private void PrepareDropIndex()
    {
        if (_profile is null || _tables.SelectedItem is not TableChoice selected || _indexes.SelectedItem is not SqlIndexInfo index) { _administrationStatus.Text = "Select a table and a live index first."; return; }
        try
        {
            var sql = SqlAdministrationService.BuildDropIndexSql(selected.Table, index.Name); var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => _confirmation.IsVisible = false; var confirm = new Button { Content = $"Drop {index.Name}" };
            confirm.Click += async (_, _) => { try { confirm.IsEnabled = false; await _administration.DropIndexAsync(_profile, selected.Table, index.Name); _confirmation.IsVisible = false; _administrationStatus.Text = $"Dropped index {index.Name} from {_profile.Database}.{selected.Table.Name}."; await LoadTableAdministrationAsync(confirm); } catch (Exception exception) { Fail("DROP INDEX failed", exception); _administrationStatus.Text = exception.Message; } finally { confirm.IsEnabled = true; } };
            _confirmation.Child = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = $"Permanently remove this index? Query performance and uniqueness enforcement may change immediately. MySQL may implicitly commit DDL.\n{sql}", TextWrapping = TextWrapping.Wrap }, WithColumn(cancel, 1), WithColumn(confirm, 2) } }; _confirmation.IsVisible = true;
        }
        catch (Exception exception) { _administrationStatus.Text = $"Cannot drop index: {exception.Message}"; }
    }

    private async Task LoadDatabaseObjectsAsync(Button button)
    {
        if (_profile is null) { _administrationStatus.Text = "Connect Server & SQL first."; return; }
        try
        {
            button.IsEnabled = false; Begin("Reading views, triggers, routines, and scheduled events…");
            _databaseObjectCache = await new SqlDatabaseObjectService().ListAsync(_profile, _operation!.Token); FilterDatabaseObjects();
            _administrationStatus.Text = $"{_profile.Database} · {_databaseObjectCache.Count:N0} database object(s). Select one to read its exact SHOW CREATE definition.";
        }
        catch (OperationCanceledException) { _administrationStatus.Text = "Database-object refresh cancelled."; }
        catch (Exception exception) { Fail("Database-object refresh failed", exception); _administrationStatus.Text = exception.Message; }
        finally { button.IsEnabled = true; End(); }
    }

    private void FilterDatabaseObjects()
    {
        var identity = (_databaseObjects.SelectedItem as SqlDatabaseObjectInfo)?.Identity;
        var type = _databaseObjectType.SelectedIndex switch { 1 => SqlDatabaseObjectType.View, 2 => SqlDatabaseObjectType.Trigger, 3 => SqlDatabaseObjectType.Procedure, 4 => SqlDatabaseObjectType.Function, 5 => SqlDatabaseObjectType.Event, _ => (SqlDatabaseObjectType?)null };
        var query = _databaseObjectSearch.Text?.Trim() ?? string.Empty;
        var filtered = _databaseObjectCache.Where(item => (type is null || item.Type == type) && (query.Length == 0 || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Details.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Definer.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        _databaseObjects.ItemsSource = filtered;
        if (identity is not null) _databaseObjects.SelectedItem = filtered.FirstOrDefault(item => item.Identity.Equals(identity, StringComparison.OrdinalIgnoreCase));
    }

    private async Task LoadSelectedDatabaseObjectAsync()
    {
        if (_profile is null || _databaseObjects.SelectedItem is not SqlDatabaseObjectInfo item) { _databaseObjectDefinition.Text = string.Empty; return; }
        try
        {
            Begin($"Reading exact definition for {item.Type} {item.Name}…");
            var definition = await new SqlDatabaseObjectService().ShowCreateAsync(_profile, item, _operation!.Token); _databaseObjectDefinition.Text = definition.CreateSql;
            if (item.Type == SqlDatabaseObjectType.View) _viewName.Text = item.Name;
            _administrationStatus.Text = $"{item.Display} · definer {item.Definer}. Exact definition loaded.";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Fail("SHOW CREATE failed", exception); _administrationStatus.Text = exception.Message; }
        finally { End(); }
    }

    private async Task ExportDatabaseObjectsAsync(Button button)
    {
        if (_profile is null) { _administrationStatus.Text = "Connect Server & SQL first."; return; }
        try
        {
            var file = await Storage().SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export exact database-object definitions", SuggestedFileName = $"{_profile.Database}-objects.sql", FileTypeChoices = [new FilePickerFileType("SQL script") { Patterns = ["*.sql"] }] });
            var path = file?.TryGetLocalPath(); if (path is null) return;
            if (File.Exists(path)) { _administrationStatus.Text = "That export target already exists. Choose a new file so Crucible never silently replaces a database-object backup."; return; }
            button.IsEnabled = false; Begin("Exporting exact database-object definitions atomically…");
            var result = await new SqlDatabaseObjectService().ExportAsync(_profile, path, cancellationToken: _operation!.Token);
            _administrationStatus.Text = $"Exported {result.Objects:N0} exact definition(s), {result.Bytes:N0} bytes, to {result.Path}. Review DEFINER clauses before importing elsewhere.";
        }
        catch (OperationCanceledException) { _administrationStatus.Text = "Database-object export cancelled."; }
        catch (Exception exception) { Fail("Database-object export failed", exception); _administrationStatus.Text = exception.Message; }
        finally { button.IsEnabled = true; End(); }
    }

    private void PrepareCreateOrReplaceView()
    {
        if (_profile is null) { _administrationStatus.Text = "Connect Server & SQL first."; return; }
        try
        {
            var name = _viewName.Text ?? string.Empty; var selectSql = _viewSelect.Text ?? string.Empty; var sql = SqlDatabaseObjectService.BuildCreateOrReplaceViewSql(_profile.Database, name, selectSql);
            var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => _confirmation.IsVisible = false; var confirm = AccentButton("Execute CREATE / REPLACE VIEW");
            confirm.Click += async (_, _) => { try { confirm.IsEnabled = false; Begin($"Creating or replacing view {name}…"); await new SqlDatabaseObjectService().CreateOrReplaceViewAsync(_profile, name, selectSql, _operation!.Token); _confirmation.IsVisible = false; _administrationStatus.Text = $"Created or replaced {_profile.Database}.{name}. Refresh objects to read the server-normalized definition."; } catch (Exception exception) { Fail("CREATE OR REPLACE VIEW failed", exception); _administrationStatus.Text = exception.Message; } finally { confirm.IsEnabled = true; End(); } };
            _confirmation.Child = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = $"Execute this schema-changing DDL? It may implicitly commit. The guided editor permits exactly one read-only SELECT and blocks batches and file output.\n{sql}", TextWrapping = TextWrapping.Wrap }, WithColumn(cancel, 1), WithColumn(confirm, 2) } }; _confirmation.IsVisible = true;
        }
        catch (Exception exception) { _administrationStatus.Text = $"Cannot prepare view: {exception.Message}"; }
    }

    private void PrepareDropDatabaseObject()
    {
        if (_profile is null || _databaseObjects.SelectedItem is not SqlDatabaseObjectInfo item) { _administrationStatus.Text = "Select a live database object first."; return; }
        try
        {
            var sql = SqlDatabaseObjectService.BuildDropSql(item); var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => _confirmation.IsVisible = false; var confirm = new Button { Content = $"Drop {item.Type} {item.Name}" };
            confirm.Click += async (_, _) => { try { confirm.IsEnabled = false; Begin($"Dropping exact {item.Type} {item.Name}…"); await new SqlDatabaseObjectService().DropAsync(_profile, item, _operation!.Token); _confirmation.IsVisible = false; _databaseObjectCache = _databaseObjectCache.Where(candidate => !candidate.Identity.Equals(item.Identity, StringComparison.OrdinalIgnoreCase)).ToArray(); FilterDatabaseObjects(); _databaseObjectDefinition.Text = string.Empty; _administrationStatus.Text = $"Dropped exact {item.Type} {_profile.Database}.{item.Name}."; } catch (Exception exception) { Fail($"DROP {item.Type} failed", exception); _administrationStatus.Text = exception.Message; } finally { confirm.IsEnabled = true; End(); } };
            _confirmation.Child = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = $"DESTRUCTIVE SCHEMA CHANGE. Export definitions first if this object matters. MySQL DDL may implicitly commit and cannot be promised rollback.\n{sql}", TextWrapping = TextWrapping.Wrap }, WithColumn(cancel, 1), WithColumn(confirm, 2) } }; _confirmation.IsVisible = true;
        }
        catch (Exception exception) { _administrationStatus.Text = $"Cannot prepare DROP: {exception.Message}"; }
    }

    private void PrepareEventState(bool enabled)
    {
        if (_profile is null || _databaseObjects.SelectedItem is not SqlDatabaseObjectInfo { Type: SqlDatabaseObjectType.Event } item) { _administrationStatus.Text = "Select a scheduled event first."; return; }
        try
        {
            var sql = SqlDatabaseObjectService.BuildEventStateSql(item, enabled); var action = enabled ? "ENABLE" : "DISABLE"; var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => _confirmation.IsVisible = false; var confirm = AccentButton($"Execute {action}");
            confirm.Click += async (_, _) => { try { confirm.IsEnabled = false; Begin($"Setting event {item.Name} {action}…"); await new SqlDatabaseObjectService().SetEventEnabledAsync(_profile, item, enabled, _operation!.Token); _confirmation.IsVisible = false; _administrationStatus.Text = $"Event {_profile.Database}.{item.Name} is now {(enabled ? "enabled" : "disabled")}. Refresh objects to verify server state."; } catch (Exception exception) { Fail($"ALTER EVENT {action} failed", exception); _administrationStatus.Text = exception.Message; } finally { confirm.IsEnabled = true; End(); } };
            _confirmation.Child = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = $"Change this scheduled event's live execution state? MySQL DDL may implicitly commit.\n{sql}", TextWrapping = TextWrapping.Wrap }, WithColumn(cancel, 1), WithColumn(confirm, 2) } }; _confirmation.IsVisible = true;
        }
        catch (Exception exception) { _administrationStatus.Text = $"Cannot prepare event change: {exception.Message}"; }
    }

    private async Task LoadProcessesAsync(Button button)
    {
        if (_profile is null) return; try { button.IsEnabled = false; Begin("Reading MySQL process list…"); var processes = await _administration.ReadProcessesAsync(_profile, _operation!.Token); _processes.ItemsSource = processes; _administrationStatus.Text = $"{processes.Count:N0} visible MySQL connection/process(es)."; }
        catch (Exception exception) { Fail("Process-list refresh failed", exception); _administrationStatus.Text = exception.Message; } finally { button.IsEnabled = true; End(); }
    }

    private void ShowSelectedProcess()
    {
        _processDetail.Text = _processes.SelectedItem is not SqlProcessInfo process ? string.Empty : $"Connection ID: {process.Id}\nUser: {process.User}\nHost: {process.Host}\nDatabase: {process.Database ?? "(none)"}\nCommand: {process.Command}\nElapsed: {process.Seconds:N0} seconds\nState: {process.State ?? "(none)"}\n\n{process.Statement ?? "(no active statement)"}";
    }

    private void PrepareKillProcess()
    {
        if (_profile is null || _processes.SelectedItem is not SqlProcessInfo process) { _administrationStatus.Text = "Select a process first."; return; }
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => _confirmation.IsVisible = false; var confirm = new Button { Content = $"Kill connection {process.Id}" };
        confirm.Click += async (_, _) => { try { confirm.IsEnabled = false; await _administration.KillProcessAsync(_profile, process.Id); _confirmation.IsVisible = false; _administrationStatus.Text = $"Sent KILL CONNECTION {process.Id}."; await LoadProcessesAsync(confirm); } catch (Exception exception) { Fail("KILL CONNECTION failed", exception); _administrationStatus.Text = exception.Message; } finally { confirm.IsEnabled = true; } };
        _confirmation.Child = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = $"Terminate MySQL connection {process.Id} owned by {process.User}@{process.Host}? Active work can roll back and a server component may reconnect.\n\n{process.Statement ?? "No active statement reported."}", TextWrapping = TextWrapping.Wrap }, WithColumn(cancel, 1), WithColumn(confirm, 2) } }; _confirmation.IsVisible = true;
    }

    private async Task LoadUsersAsync(Button button)
    {
        if (_profile is null) return; button.IsEnabled = false; Begin("Reading account metadata and server-supported privileges…"); var findings = new List<string>();
        try { _supportedPrivileges = await _administration.ReadPrivilegesAsync(_profile, _operation!.Token); _accountPrivileges.ItemsSource = _supportedPrivileges; findings.Add($"{_supportedPrivileges.Count:N0} supported privilege(s)"); }
        catch (Exception exception) { Fail("Supported-privilege read failed", exception); findings.Add($"privilege list unavailable: {exception.Message}"); }
        try { var users = await _administration.ReadUsersAsync(_profile, _operation!.Token); _databaseUsers.ItemsSource = users; findings.Add($"{users.Count:N0} visible account(s)"); }
        catch (Exception exception) { Fail("Database-account read failed", exception); findings.Add($"account list unavailable to this login: {exception.Message}"); }
        finally { button.IsEnabled = true; End(); _administrationStatus.Text = $"{string.Join(" · ", findings)}. Password hashes were not queried."; }
    }

    private void SelectDatabaseUser()
    {
        if (_databaseUsers.SelectedItem is not SqlUserAccountInfo account) return; _accountUser.Text = account.User; _accountHost.Text = account.Host; _accountPassword.Text = string.Empty; _accountGrants.Text = string.Empty;
    }

    private async Task LoadAccountGrantsAsync(Button button)
    {
        if (_profile is null) return; try { button.IsEnabled = false; Begin("Reading exact SHOW GRANTS output…"); var grants = await _administration.ReadGrantsAsync(_profile, _accountUser.Text ?? string.Empty, _accountHost.Text ?? string.Empty, _operation!.Token); _accountGrants.Text = string.Join(Environment.NewLine, grants); _administrationStatus.Text = $"{grants.Count:N0} exact grant statement(s) visible for {AccountLabel()}."; }
        catch (Exception exception) { Fail("SHOW GRANTS failed", exception); _administrationStatus.Text = $"Grants are unavailable to this login: {exception.Message}"; } finally { button.IsEnabled = true; End(); }
    }

    private void PrepareCreateUser()
    {
        if (_profile is not { } profile) return; var user = _accountUser.Text ?? string.Empty; var host = _accountHost.Text ?? string.Empty; var password = _accountPassword.Text ?? string.Empty; var locked = _accountCreateLocked.IsChecked == true;
        try { if (password.Length == 0) throw new ArgumentException("Enter a non-empty new password."); var sql = SqlAdministrationService.BuildCreateUserSql(user, host, locked); PrepareAccountConfirmation("CREATE USER", SqlAdministrationService.RedactPasswordSql(sql), AccountLabel(user, host), async token => await _administration.CreateUserAsync(profile, user, host, password, locked, token), passwordBearing: true); }
        catch (Exception exception) { _administrationStatus.Text = $"Cannot prepare CREATE USER: {exception.Message}"; }
    }

    private void PrepareAccountPassword()
    {
        if (_profile is not { } profile) return; var user = _accountUser.Text ?? string.Empty; var host = _accountHost.Text ?? string.Empty; var password = _accountPassword.Text ?? string.Empty;
        try { if (password.Length == 0) throw new ArgumentException("Enter a non-empty replacement password."); var sql = SqlAdministrationService.BuildChangePasswordSql(user, host); PrepareAccountConfirmation("ALTER USER password", SqlAdministrationService.RedactPasswordSql(sql), AccountLabel(user, host), async token => await _administration.ChangePasswordAsync(profile, user, host, password, token), passwordBearing: true); }
        catch (Exception exception) { _administrationStatus.Text = $"Cannot prepare password change: {exception.Message}"; }
    }

    private void PrepareAccountLock(bool locked)
    {
        if (_profile is not { } profile) return; var user = _accountUser.Text ?? string.Empty; var host = _accountHost.Text ?? string.Empty;
        try { var sql = SqlAdministrationService.BuildAccountLockSql(user, host, locked); PrepareAccountConfirmation(locked ? "ACCOUNT LOCK" : "ACCOUNT UNLOCK", sql, AccountLabel(user, host), async token => await _administration.SetAccountLockAsync(profile, user, host, locked, token)); }
        catch (Exception exception) { _administrationStatus.Text = $"Cannot prepare account {(locked ? "lock" : "unlock")}: {exception.Message}"; }
    }

    private void PrepareDropUser()
    {
        if (_profile is not { } profile) return; var user = _accountUser.Text ?? string.Empty; var host = _accountHost.Text ?? string.Empty;
        try { var sql = SqlAdministrationService.BuildDropUserSql(user, host); PrepareAccountConfirmation("DROP USER", sql, AccountLabel(user, host), async token => await _administration.DropUserAsync(profile, user, host, token), destructive: true); }
        catch (Exception exception) { _administrationStatus.Text = $"Cannot prepare DROP USER: {exception.Message}"; }
    }

    private void PreparePrivilegeChange(bool revoke)
    {
        if (_profile is not { } profile) return; var user = _accountUser.Text ?? string.Empty; var host = _accountHost.Text ?? string.Empty; var table = EmptyNull(_accountTable.Text); var global = _accountGlobal.IsChecked == true; var withGrantOption = _accountGrantOption.IsChecked == true; var supported = _supportedPrivileges;
        var privileges = _accountPrivileges.SelectedItems?.OfType<SqlPrivilegeInfo>().Select(privilege => privilege.Name).ToArray() ?? [];
        try
        {
            var sql = revoke ? SqlAdministrationService.BuildRevokeSql(user, host, profile.Database, table, global, privileges, supported) : SqlAdministrationService.BuildGrantSql(user, host, profile.Database, table, global, privileges, supported, withGrantOption);
            PrepareAccountConfirmation(revoke ? "REVOKE" : "GRANT", sql, AccountLabel(user, host), revoke ? async token => await _administration.RevokeAsync(profile, user, host, profile.Database, table, global, privileges, supported, token) : async token => await _administration.GrantAsync(profile, user, host, profile.Database, table, global, privileges, supported, withGrantOption, token));
        }
        catch (Exception exception) { _administrationStatus.Text = $"Cannot prepare {(revoke ? "REVOKE" : "GRANT")}: {exception.Message}"; }
    }

    private void PrepareAccountConfirmation(string action, string previewSql, string targetAccount, Func<CancellationToken, Task> apply, bool passwordBearing = false, bool destructive = false)
    {
        if (_profile is null) { _administrationStatus.Text = "Connect Server & SQL first."; return; }
        var cancel = new Button { Content = "Cancel" }; var confirm = new Button { Content = $"Execute {action}" }; cancel.Click += (_, _) => { _confirmation.IsVisible = false; _confirmation.Child = null; if (passwordBearing) _accountPassword.Text = string.Empty; };
        confirm.Click += async (_, _) =>
        {
            try { confirm.IsEnabled = false; Begin($"Executing reviewed {action} for {targetAccount}…"); await apply(_operation!.Token); _confirmation.IsVisible = false; _confirmation.Child = null; _accountPassword.Text = string.Empty; _administrationStatus.Text = $"Applied {action} for {targetAccount}. Read exact grants/account metadata to verify the resulting state."; }
            catch (Exception exception) { Fail($"Account {action} failed", exception); _administrationStatus.Text = exception.Message; }
            finally { confirm.IsEnabled = true; End(); }
        };
        _confirmation.Child = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = $"{(destructive ? "DESTRUCTIVE ACCOUNT CHANGE. " : string.Empty)}Review the exact account, host, privilege scope, and statement. MySQL account changes can affect active or future server access immediately.\n{previewSql}", TextWrapping = TextWrapping.Wrap }, WithColumn(cancel, 1), WithColumn(confirm, 2) } }; _confirmation.IsVisible = true;
    }

    private void SelectPrivilegePreset(params string[] names)
    {
        if (_accountPrivileges.SelectedItems is null) return; _accountPrivileges.SelectedItems.Clear(); var requested = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var privilege in _supportedPrivileges.Where(privilege => requested.Contains(privilege.Name.Replace('_', ' ')))) _accountPrivileges.SelectedItems.Add(privilege);
        _administrationStatus.Text = $"Selected {_accountPrivileges.SelectedItems.Count:N0} privilege(s). Review the exact scope before GRANT or REVOKE.";
    }

    private string AccountLabel() => AccountLabel(_accountUser.Text ?? string.Empty, _accountHost.Text ?? string.Empty);
    private static string AccountLabel(string user, string host) => $"'{user}'@'{host}'";

    private void PopulateRelations()
    {
        var selected = (_joinRelation.SelectedItem as RelationChoice)?.Relation.Name; var choices = (_capabilities?.Relationships ?? []).Where(relation => _capabilities?.FindTable(relation.FromTable) is not null && _capabilities.FindTable(relation.ToTable) is not null).OrderBy(relation => relation.FromTable).ThenBy(relation => relation.ToTable).ThenBy(relation => relation.Name).Select(relation => new RelationChoice(relation)).ToArray(); _joinRelation.ItemsSource = choices; _joinRelation.SelectedItem = choices.FirstOrDefault(choice => choice.Relation.Name.Equals(selected, StringComparison.OrdinalIgnoreCase)); if (_joinRelation.SelectedItem is null && choices.Length > 0) _joinRelation.SelectedIndex = 0;
    }

    private bool BuildJoinSql()
    {
        try { if (_joinRelation.SelectedItem is not RelationChoice choice || _capabilities is null) throw new InvalidOperationException("Select a recognized relationship."); if (!int.TryParse(_joinLimit.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var limit)) throw new FormatException("Join limit must be numeric."); var source = _capabilities.FindTable(choice.Relation.FromTable) ?? throw new InvalidOperationException("Source table is unavailable."); var target = _capabilities.FindTable(choice.Relation.ToTable) ?? throw new InvalidOperationException("Target table is unavailable."); _joinSql.Text = SqlAdministrationService.BuildJoinSql(choice.Relation, source, target, _joinType.SelectedItem as string ?? "LEFT", limit); _administrationStatus.Text = "Built read-only exact-column join SQL with source__/target__ aliases. Review it before running."; return true; }
        catch (Exception exception) { _joinSql.Text = string.Empty; _administrationStatus.Text = $"Cannot build join: {exception.Message}"; return false; }
    }

    private async Task RunJoinAsync(Button button)
    {
        if (_profile is null) return; try { if (!BuildJoinSql()) return; button.IsEnabled = false; Begin("Running read-only visual join…"); var result = await _service.QueryAsync(_profile, _joinSql.Text ?? string.Empty, 2000, _operation!.Token); _joinOutput.Text = FormatResult(result); _administrationStatus.Text = $"Visual join returned {result.Rows.Count:N0} row(s) in {result.Duration.TotalMilliseconds:N0} ms."; }
        catch (Exception exception) { Fail("Visual join failed", exception); _administrationStatus.Text = exception.Message; } finally { button.IsEnabled = true; End(); }
    }

    private async Task OpenRelatedAsync(string tableName, string columnName, object? value)
    {
        PopulateTables(); var choice = (_tables.ItemsSource as IEnumerable<TableChoice>)?.FirstOrDefault(item => item.Table.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase));
        if (choice is null) { _status.Text = $"Related table {tableName} is not available in the connected schema."; return; }
        try
        {
            ResetBrowseOptions(tableName); _suppressTableSelection = true; _tables.SelectedItem = choice; _offset = 0;
            Begin($"Opening exact relationship {tableName}.{columnName}…"); _page = await _service.ReadColumnMatchesAsync(_profile!, choice.Table, columnName, value, 200, _operation!.Token);
            _rows.ItemsSource = _page.Rows; ApplyRowTemplate();
            _pageStatus.Text = _page.TotalRows == 0 ? $"No rows where {columnName} exactly equals {CellText(value)}." : $"Exact {columnName} match · showing {_page.Rows.Count:N0} of {_page.TotalRows:N0}";
            if (_page.Rows.Count > 0) _rows.SelectedIndex = 0; _status.Text = $"Opened exact dependency edge {tableName}.{columnName} = {CellText(value)}.";
        }
        catch (OperationCanceledException) { _status.Text = "Relationship navigation cancelled."; }
        catch (Exception exception) { Fail("Relationship navigation failed", exception); }
        finally { _suppressTableSelection = false; End(); }
    }

    private async Task ExportTableAsync(SqlExportFormat format)
    {
        if (_profile is null || _tables.SelectedItem is not TableChoice selected) { _status.Text = "Connect SQL and select a table first."; return; }
        try
        {
            var extension = format == SqlExportFormat.Csv ? "csv" : "jsonl";
            var file = await Storage().SaveFilePickerAsync(new FilePickerSaveOptions { Title = $"Export complete {selected.Table.Name} table", SuggestedFileName = $"{selected.Table.Name}.{extension}", FileTypeChoices = [new FilePickerFileType(format == SqlExportFormat.Csv ? "CSV" : "JSON Lines") { Patterns = [$"*.{extension}"] }] });
            var path = file?.TryGetLocalPath(); if (path is null) return;
            Begin($"Streaming complete {selected.Table.Name} export…"); var result = await new SqlTransferService().ExportTableAsync(_profile, selected.Table, path, format, overwrite: true, cancellationToken: _operation!.Token); _status.Text = $"Exported {result.Rows:N0} row(s) to {result.Path}.";
        }
        catch (OperationCanceledException) { _status.Text = "Table export cancelled."; }
        catch (Exception exception) { Fail("Table export failed", exception); }
        finally { End(); }
    }

    private async Task PrepareImportAsync()
    {
        if (_profile is null || _tables.SelectedItem is not TableChoice selected) { _status.Text = "Connect SQL and select a table first."; return; }
        try
        {
            var files = await Storage().OpenFilePickerAsync(new FilePickerOpenOptions { Title = $"Import CSV rows into {selected.Table.Name}", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }] });
            var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path is null) return;
            var plan = await Task.Run(() => new SqlTransferService().AnalyzeCsv(path, selected.Table));
            if (!plan.CanApply) { _status.Text = $"CSV import blocked: {string.Join(" ", plan.Findings)}"; return; }
            var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => _confirmation.IsVisible = false;
            var confirm = AccentButton($"Insert {plan.Rows:N0} row(s)"); confirm.Click += async (_, _) =>
            {
                try { confirm.IsEnabled = false; var inserted = await new SqlTransferService().ImportCsvAsync(_profile, selected.Table, plan.Path); _confirmation.IsVisible = false; _status.Text = $"Imported {inserted:N0} row(s) transactionally. Any duplicate/error would have rolled back the entire import."; await LoadPageAsync(false); }
                catch (Exception exception) { Fail("CSV import failed", exception); } finally { confirm.IsEnabled = true; }
            };
            _confirmation.Child = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = $"Dry-run passed: {plan.Rows:N0} row(s), {plan.Columns.Count:N0} mapped column(s), zero structural findings. Insert into {selected.Table.Name}? This is insert-only and one transaction; duplicate keys never overwrite existing data.", TextWrapping = TextWrapping.Wrap }, WithColumn(cancel, 1), WithColumn(confirm, 2) } }; _confirmation.IsVisible = true;
        }
        catch (Exception exception) { Fail("CSV analysis failed", exception); }
    }

    private async Task RunQueryAsync()
    {
        if (_profile is null) { _status.Text = "Connect Server & SQL first."; return; } var sql = _query.Text ?? string.Empty;
        if (!SqlWorkspaceService.IsReadOnlyBatch(sql)) { _status.Text = "One or more statements are not recognized as read-only, are malformed, or use SELECT file output. Use Prepare write statement for an intentional mutation."; return; }
        Begin("Running validated read-only batch…");
        try
        {
            _queryBatch = await _service.QueryBatchAsync(_profile, sql, 2000, _operation!.Token);
            _queryResultSets.ItemsSource = _queryBatch.Results; _queryResultSets.SelectedItem = _queryBatch.Results.FirstOrDefault(); SelectQueryResultSet();
            var truncated = _queryBatch.Results.Count(result => result.Truncated);
            _status.Text = $"Returned {_queryBatch.TotalRows:N0} structured row(s) across {_queryBatch.Results.Count:N0} result set(s) in {_queryBatch.Duration.TotalMilliseconds:N0} ms.{(truncated == 0 ? string.Empty : $" {truncated:N0} result set(s) reached the 2,000-row display cap.")} Select a result set to copy or export it.";
            try { _queryHistoryStore.Record(_profile.Database, sql, _queryBatch); RefreshQueryHistory(); }
            catch (Exception historyException)
            {
                DesktopCrashLogger.Log("SQL query succeeded but portable history could not be updated", historyException);
                _status.Text += " The result is valid, but portable query history could not be updated; see the log for details.";
            }
        }
        catch (Exception exception) { Fail("Query failed", exception); }
        finally { End(); }
    }

    private void PrepareStatement()
    {
        if (_profile is null) { _status.Text = "Connect Server & SQL first."; return; } var sql = _query.Text ?? string.Empty;
        if (SqlWorkspaceService.IsReadOnlyBatch(sql)) { _status.Text = "Use Run read-only batch for this statement set."; return; }
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => _confirmation.IsVisible = false;
        var confirm = AccentButton("Execute write transaction"); confirm.Click += async (_, _) => { try { confirm.IsEnabled = false; var result = await _service.ExecuteAsync(_profile, sql); _queryBatch = null; _queryResult = null; _queryResultSets.ItemsSource = null; _queryResults.ItemsSource = null; _querySummary.Text = $"Write result · {result.AffectedRows:N0} affected row(s) · {result.Duration.TotalMilliseconds:N0} ms"; _confirmation.IsVisible = false; _status.Text = "Statement committed transactionally."; } catch (Exception exception) { Fail("Statement failed", exception); } finally { confirm.IsEnabled = true; } };
        _confirmation.Child = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = $"Execute this non-read-only statement against {_profile.Database}? Review it carefully. Crucible begins a transaction, but MySQL schema/DDL statements can implicitly commit and may not be rollbackable.", TextWrapping = TextWrapping.Wrap }, WithColumn(cancel, 1), WithColumn(confirm, 2) } }; _confirmation.IsVisible = true;
    }

    private void SelectQueryResultSet()
    {
        if (_queryResultSets.SelectedItem is not SqlQueryBatchResult selected)
        {
            _queryResult = null; _queryResults.ItemsSource = null; _querySummary.Text = "No result set selected."; return;
        }
        _queryResult = selected.Result;
        _queryResults.ItemsSource = _queryResult.Rows.Select((row, index) => new QueryDisplayRow(index + 1, _queryResult.Columns, row)).ToArray();
        ApplyQueryResultTemplate();
        _querySummary.Text = $"Result {selected.Index:N0}/{_queryBatch?.Results.Count ?? 1:N0} · {_queryResult.Rows.Count:N0} row(s) · {_queryResult.Columns.Count:N0} column(s) · {_queryResult.Duration.TotalMilliseconds:N0} ms{(selected.Truncated ? " · truncated at display cap" : string.Empty)}";
    }

    private void BookmarkCurrentQuery()
    {
        if (_profile is null) { _status.Text = "Connect Server & SQL before bookmarking a query."; return; }
        var sql = _query.Text ?? string.Empty; if (!SqlWorkspaceService.IsReadOnlyBatch(sql)) { _status.Text = "Only a validated read-only query or batch can be bookmarked here."; return; }
        _queryHistoryStore.Bookmark(_profile.Database, sql, _queryBookmarkLabel.Text); RefreshQueryHistory(); _status.Text = "Bookmarked the current read batch in the portable SQL history.";
    }

    private async Task LoadQueryHistoryAsync()
    {
        if (_queryHistory.SelectedItem is not SqlQueryHistoryEntry entry) { _status.Text = "Select a history or bookmark entry first."; return; }
        if (_profile is null) { _status.Text = "Connect Server & SQL before loading a database-specific history entry."; return; }
        if (!_profile.Database.Equals(entry.Database, StringComparison.OrdinalIgnoreCase))
        {
            await SwitchSchemaAsync(entry.Database);
            if (_profile is null || !_profile.Database.Equals(entry.Database, StringComparison.OrdinalIgnoreCase))
            {
                _status.Text = $"Could not switch to {entry.Database}; the history entry was not loaded against a different schema.";
                return;
            }
        }
        _query.Text = entry.Sql; _queryBookmarkLabel.Text = entry.Label; _tabs.SelectedIndex = 1;
        _status.Text = $"Loaded {(entry.Bookmarked ? "bookmark" : "history entry")} from {entry.Database}. Review it, then run the read batch explicitly.";
    }

    private void RefreshQueryHistory()
    {
        var selected = (_queryHistory.SelectedItem as SqlQueryHistoryEntry)?.Id; var entries = _queryHistoryStore.Load(); _queryHistory.ItemsSource = entries;
        _queryHistory.SelectedItem = selected is null ? entries.FirstOrDefault() : entries.FirstOrDefault(entry => entry.Id.Equals(selected, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyQueryResultTemplate()
    {
        var compact = _queryDisplay.SelectedIndex == 1;
        _queryResults.ItemTemplate = new FuncDataTemplate<QueryDisplayRow>((row, _) =>
        {
            if (row is null) return new TextBlock();
            if (compact)
                return new TextBlock { Text = $"{row.Number:N0}\t{string.Join('\t', row.Values.Select(QueryCellText))}", FontFamily = new FontFamily("Cascadia Mono,Consolas"), TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(5, 3) };
            var values = new WrapPanel { Margin = new Thickness(3, 2) };
            for (var index = 0; index < row.Columns.Count; index++)
            {
                values.Children.Add(new Border
                {
                    BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 4), Margin = new Thickness(2),
                    Child = new StackPanel { Children = { new TextBlock { Text = row.Columns[index], FontSize = 10, Foreground = Brush.Parse("#8995A9") }, new TextBlock { Text = QueryCellText(row.Values[index]), TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono,Consolas") } } }
                });
            }
            return new StackPanel { Margin = new Thickness(3, 2), Children = { new TextBlock { Text = $"ROW {row.Number:N0}", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#C58A2B") }, values } };
        });
    }

    private async Task CopySelectedQueryRowAsync()
    {
        if (_queryResults.SelectedItem is not QueryDisplayRow row) { _status.Text = "Select a query-result row first."; return; }
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard; if (clipboard is null) { _status.Text = "The system clipboard is unavailable."; return; }
        await clipboard.SetTextAsync(string.Join('\t', row.Columns) + Environment.NewLine + string.Join('\t', row.Values.Select(QueryCellText)));
        _status.Text = $"Copied query-result row {row.Number:N0} with its {_queryResult?.Columns.Count ?? row.Columns.Count:N0} column names.";
    }

    private async Task ExportQueryResultAsync(SqlExportFormat format)
    {
        if (_queryResult is null || _queryResult.Columns.Count == 0) { _status.Text = "Run a read-only query before exporting its result."; return; }
        try
        {
            var extension = format == SqlExportFormat.Csv ? "csv" : "jsonl";
            var file = await Storage().SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export current in-memory query result", SuggestedFileName = $"query-result.{extension}", FileTypeChoices = [new FilePickerFileType(format == SqlExportFormat.Csv ? "CSV" : "JSON Lines") { Patterns = [$"*.{extension}"] }] });
            var path = file?.TryGetLocalPath(); if (path is null) return;
            Begin("Exporting current query result atomically…"); var result = await new SqlTransferService().ExportQueryResultAsync(_queryResult, path, format, overwrite: true, cancellationToken: _operation!.Token);
            _status.Text = $"Exported {result.Rows:N0} row(s) × {result.Columns:N0} column(s) to {result.Path}.";
        }
        catch (OperationCanceledException) { _status.Text = "Query-result export cancelled; no partial output was published."; }
        catch (Exception exception) { Fail("Query-result export failed", exception); }
        finally { End(); }
    }

    private async Task OpenFavoriteAsync(bool openDecoded = false)
    {
        if (SelectedFavorite() is not { } favorite || _profile is null) return;
        try
        {
            if (!_profile.Database.Equals(favorite.Database, StringComparison.OrdinalIgnoreCase)) await SwitchSchemaAsync(favorite.Database);
            if (_capabilities is null || _profile is null) return; var table = _capabilities.FindTable(favorite.Table); if (table is null) { _status.Text = $"{favorite.Database} has no {favorite.Table} table."; return; }
            Begin($"Opening exact favorite {favorite.Label}…");
            var key = favorite.Key.ToDictionary(pair => pair.Key, pair => SqlFavoriteWorkspaceService.ParseStoredKey(table.Find(pair.Key) ?? throw new InvalidOperationException($"Favorite key column {pair.Key} no longer exists."), pair.Value), StringComparer.OrdinalIgnoreCase);
            var row = await _service.ReadRowAsync(_profile, table, key, _operation!.Token);
            if (row is null) { _status.Text = $"The exact favorite no longer exists: {favorite.Database}.{favorite.Table} · {string.Join(", ", favorite.Key.Select(pair => $"{pair.Key}={pair.Value}"))}."; return; }
            ResetBrowseOptions(table.Name);
            _suppressTableSelection = true; try { PopulateTables(); _tables.SelectedItem = (_tables.ItemsSource as IEnumerable<TableChoice>)?.FirstOrDefault(choice => choice.Table.Name.Equals(table.Name, StringComparison.OrdinalIgnoreCase)); } finally { _suppressTableSelection = false; }
            _rowSearch.Text = string.Empty; _offset = 0; _page = new(table.Name, table.Columns, row.Key.Keys.ToArray(), 1, 0, 1, "exact favorite", [row]);
            _rows.ItemsSource = _page.Rows; ApplyRowTemplate();
            _pageStatus.Text = "Exact primary-key favorite · 1 row"; _tabs.SelectedIndex = 0; _rows.SelectedItem = row;
            _status.Text = $"Opened exact favorite {row.Display}. No broad text-search substitution was used.";
            if (openDecoded)
            {
                if (!CanOpenGuidedEditor(table.Name)) _status.Text = $"Opened exact favorite {row.Display}, but {table.Name} has no guided editor yet; every field remains available here.";
                else GuidedEditRequested?.Invoke(this, new(table.Name, row.Values));
            }
        }
        catch (OperationCanceledException) { _status.Text = "Favorite lookup cancelled."; }
        catch (Exception exception) { Fail("Favorite lookup failed", exception); }
        finally { End(); }
    }

    private SqlRowFavorite? SelectedFavorite() => (_favorites.SelectedItem as FavoriteDisplayRow)?.Favorite;

    private bool CanOpenSelectedFavoriteInGuidedEditor() => SelectedFavorite() is { Table: { } table } && CanOpenGuidedEditor(table);

    private void OpenSelectedDecodedEditor()
    {
        if (_selectedRow is null || _page is null) { _status.Text = "Select a row first."; return; }
        if (!CanOpenGuidedEditor(_page.Table)) { _status.Text = $"{_page.Table} has no decoded editor yet; its complete live-schema row remains editable here."; return; }
        GuidedEditRequested?.Invoke(this, new(_page.Table, _selectedRow.Values));
    }

    private void RefreshFavorites(string? preferredIdentity = null)
    {
        preferredIdentity ??= (_favorites.SelectedItem as FavoriteDisplayRow)?.Identity;
        _favoriteCache = SqlFavoriteStore.Load();
        var identities = _favoriteCache.Select(favorite => favorite.Identity).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in _favoriteChecks.Keys.Where(identity => !identities.Contains(identity)).ToArray()) _favoriteChecks.Remove(stale);
        ApplyFavoriteFilter(preferredIdentity);
    }

    private void ApplyFavoriteFilter(string? preferredIdentity = null)
    {
        preferredIdentity ??= (_favorites.SelectedItem as FavoriteDisplayRow)?.Identity;
        var requestedState = _favoriteState.SelectedIndex switch
        {
            1 => SqlFavoriteVerificationState.Unchecked,
            2 => SqlFavoriteVerificationState.Live,
            3 => SqlFavoriteVerificationState.Missing,
            4 => SqlFavoriteVerificationState.SchemaMismatch,
            5 => SqlFavoriteVerificationState.Error,
            _ => (SqlFavoriteVerificationState?)null
        };
        var rows = _favoriteCache
            .Where(favorite => SqlFavoriteWorkspaceService.Matches(favorite, _favoriteSearch.Text))
            .Select(favorite => new FavoriteDisplayRow(favorite, _favoriteChecks.GetValueOrDefault(favorite.Identity) ?? Unchecked(favorite)))
            .Where(row => requestedState is null || row.Verification.State == requestedState)
            .OrderBy(row => row.Favorite.Database, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Favorite.Table, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Favorite.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _favorites.ItemsSource = rows;
        _favorites.SelectedItem = preferredIdentity is null ? rows.FirstOrDefault() : rows.FirstOrDefault(row => row.Identity.Equals(preferredIdentity, StringComparison.OrdinalIgnoreCase));
        _favoriteStatus.Text = $"Showing {rows.Length:N0} of {_favoriteCache.Count:N0} favorite(s) · {rows.Count(row => row.Verification.State == SqlFavoriteVerificationState.Live):N0} verified live · {rows.Count(row => row.Verification.State == SqlFavoriteVerificationState.Missing):N0} missing.";
    }

    private void SelectFavorite()
    {
        if (_favorites.SelectedItem is not FavoriteDisplayRow row)
        {
            _savedFavoriteLabel.Text = _savedFavoriteNotes.Text = _savedFavoriteDbc.Text = _savedFavoriteMpq.Text = string.Empty;
            return;
        }
        _savedFavoriteLabel.Text = row.Favorite.Label;
        _savedFavoriteNotes.Text = row.Favorite.Notes;
        _savedFavoriteDbc.Text = row.Favorite.DbcPath ?? string.Empty;
        _savedFavoriteMpq.Text = row.Favorite.MpqPath ?? string.Empty;
        _favoriteStatus.Text = $"{row.Verification.Display} · {row.Verification.Detail}";
    }

    private void SaveFavoriteDetails()
    {
        if (SelectedFavorite() is not { } favorite) { _favoriteStatus.Text = "Select a favorite first."; return; }
        var label = (_savedFavoriteLabel.Text ?? string.Empty).Trim();
        if (label.Length == 0) { _favoriteStatus.Text = "A favorite label cannot be empty."; return; }
        var updated = favorite with
        {
            Label = label,
            Notes = _savedFavoriteNotes.Text ?? string.Empty,
            DbcPath = EmptyNull(_savedFavoriteDbc.Text),
            MpqPath = EmptyNull(_savedFavoriteMpq.Text)
        };
        SqlFavoriteStore.Save(updated); RefreshFavorites(updated.Identity); _status.Text = $"Updated favorite details for {updated.Label}.";
    }

    private void RemoveFavorite()
    {
        if (SelectedFavorite() is not { } favorite) { _favoriteStatus.Text = "Select a favorite first."; return; }
        SqlFavoriteStore.Remove(favorite.Identity); _favoriteChecks.Remove(favorite.Identity); RefreshFavorites(); _status.Text = $"Removed favorite {favorite.Label}. The live SQL row was not changed.";
    }

    private async Task VerifyFavoritesAsync(Button button, bool selectedOnly)
    {
        if (_profile is null) { _favoriteStatus.Text = "Connect Server & SQL before checking favorites."; return; }
        var favorites = selectedOnly
            ? SelectedFavorite() is { } selected ? new[] { selected } : []
            : (_favorites.ItemsSource as IEnumerable<FavoriteDisplayRow>)?.Select(row => row.Favorite).ToArray() ?? [];
        if (favorites.Length == 0) { _favoriteStatus.Text = selectedOnly ? "Select a favorite first." : "No visible favorites match the current filters."; return; }
        try
        {
            button.IsEnabled = false; Begin($"Verifying {favorites.Length:N0} exact SQL favorite(s)…");
            var results = await _favoriteService.VerifyAsync(_profile, favorites, _operation!.Token);
            foreach (var result in results) _favoriteChecks[result.Identity] = result;
            var selectedIdentity = (_favorites.SelectedItem as FavoriteDisplayRow)?.Identity; ApplyFavoriteFilter(selectedIdentity);
            var live = results.Count(result => result.State == SqlFavoriteVerificationState.Live); var missing = results.Count(result => result.State == SqlFavoriteVerificationState.Missing); var changed = results.Count(result => result.State == SqlFavoriteVerificationState.SchemaMismatch); var failed = results.Count(result => result.State == SqlFavoriteVerificationState.Error);
            _favoriteStatus.Text = $"Checked {results.Count:N0} exact favorite(s) · {live:N0} live · {missing:N0} missing · {changed:N0} schema changed · {failed:N0} failed.";
            _status.Text = _favoriteStatus.Text;
        }
        catch (OperationCanceledException) { _favoriteStatus.Text = "Favorite verification cancelled."; }
        catch (Exception exception) { Fail("Favorite verification failed", exception); _favoriteStatus.Text = exception.Message; }
        finally { button.IsEnabled = true; End(); }
    }

    private static SqlFavoriteVerification Unchecked(SqlRowFavorite favorite) => new(favorite.Identity, SqlFavoriteVerificationState.Unchecked, "Not checked against the current live server in this session.", DateTimeOffset.MinValue);

    private static IBrush FavoriteStateBrush(SqlFavoriteVerificationState state) => Brush.Parse(state switch
    {
        SqlFavoriteVerificationState.Live => "#79B58A",
        SqlFavoriteVerificationState.Missing => "#D96C68",
        SqlFavoriteVerificationState.SchemaMismatch => "#D4A45F",
        SqlFavoriteVerificationState.Error => "#D96C68",
        _ => "#8995A9"
    });

    private async Task PickFavoritePathAsync(TextBox target, string title, string label, params string[] patterns)
    {
        var files = await Storage().OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = [new FilePickerFileType(label) { Patterns = patterns }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) target.Text = path;
    }
    private async Task LoadSchemasAsync()
    {
        if (_profile is null) return;
        try
        {
            _schemas.IsEnabled = false; var databases = await _service.ListDatabasesAsync(_profile); _suppressSchemaSelection = true;
            try { _schemas.ItemsSource = databases; _schemas.SelectedItem = databases.FirstOrDefault(database => database.Equals(_profile.Database, StringComparison.OrdinalIgnoreCase)); }
            finally { _suppressSchemaSelection = false; }
            _status.Text = $"{databases.Count:N0} accessible database schema(s) · active {_profile.Database}. Schema switching stays local to SQL Studio.";
        }
        catch (Exception exception) { Fail("Schema discovery failed", exception); }
        finally { _schemas.IsEnabled = true; }
    }

    private async Task SwitchSchemaAsync(string database)
    {
        if (_profile is null || string.IsNullOrWhiteSpace(database) || _profile.Database.Equals(database, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            Begin($"Inspecting database schema {database}…"); var profile = _profile with { Database = database }; var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, _operation!.Token);
            _profile = profile; _capabilities = capabilities; RefreshConnectionStatus(); _page = null; _selectedRow = null; _browseTable = null; _offset = 0; _rows.ItemsSource = null; _rowSearch.Text = string.Empty; _filterColumn.SelectedItem = null; _filterValue.Text = string.Empty; _sortColumn.SelectedItem = null; _rowEditor.Children.Clear(); _relationshipResults.Children.Clear(); _dependencyList.Children.Clear(); _dependencyGraph.SetGraph($"{database} · select a primary-keyed row", []); _confirmation.IsVisible = false;
            _suppressSchemaSelection = true; try { _schemas.SelectedItem = (_schemas.ItemsSource as IEnumerable<string>)?.FirstOrDefault(value => value.Equals(database, StringComparison.OrdinalIgnoreCase)) ?? database; } finally { _suppressSchemaSelection = false; }
            _suppressTableSelection = true; try { PopulateTables(); _tables.SelectedItem = null; } finally { _suppressTableSelection = false; } PopulateRelations();
            _pageStatus.Text = $"{capabilities.Tables.Count:N0} table(s) in {database} · select a table."; _dependencyStatus.Text = $"Active schema: {database}. Select a row to analyze dependencies."; _status.Text = $"SQL Studio switched locally to {database}; the saved world-server profile remains {_session.DatabaseProfile?.Database ?? "unchanged"}.";
        }
        catch (OperationCanceledException) { _status.Text = "Schema switch cancelled."; }
        catch (Exception exception) { Fail($"Could not open schema {database}", exception); }
        finally { End(); }
    }

    private void SessionChanged(object? sender, EventArgs e)
    {
        _profile = _session.DatabaseProfile; _capabilities = _session.DatabaseCapabilities; RefreshConnectionStatus(); PopulateTables(); PopulateRelations(); _ = LoadSchemasAsync();
    }
    private void RefreshConnectionStatus() => _connectionStatus.Text = _profile is null || _capabilities is null ? "Not connected · browsing is disabled" : $"Connected · {_profile.User}@{_profile.Host}:{_profile.Port}/{_profile.Database} · MySQL {_capabilities.ServerVersion} · {_capabilities.Tables.Count:N0} table(s)";
    private void Begin(string text) { _operation?.Cancel(); _operation?.Dispose(); _operation = new(); _status.Text = text; }
    private void End() { _operation?.Dispose(); _operation = null; }
    private void Fail(string context, Exception exception) { _status.Text = $"{context}: {exception.Message}"; DesktopCrashLogger.Log(context, exception); }
    public void Dispose() { _operation?.Cancel(); _operation?.Dispose(); _session.Changed -= SessionChanged; }

    private static string RowSummary(SqlRowRecord row) { var name = row.Values.FirstOrDefault(pair => pair.Key.Equals("name", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("LogTitle", StringComparison.OrdinalIgnoreCase)).Value; return $"{row.Display}{(name is null ? string.Empty : $"  ·  {name}")}"; }
    private static string RelationshipCount(SqlRelationshipMatch match) => match.MatchingRows < 0 ? "file DBC · SQL mirror empty" : $"{match.MatchingRows:N0} exact row(s)";
    private bool IsSharedWorldSchema() => _profile is not null && _session.DatabaseProfile is not null && _profile.Host.Equals(_session.DatabaseProfile.Host, StringComparison.OrdinalIgnoreCase) && _profile.Port == _session.DatabaseProfile.Port && _profile.Database.Equals(_session.DatabaseProfile.Database, StringComparison.OrdinalIgnoreCase);
    private bool CanOpenGuidedEditor(string table) => IsSharedWorldSchema() &&
        (new[] { "item_template", "creature_template", "gameobject_template", "quest_template" }.Contains(table, StringComparer.OrdinalIgnoreCase) ||
         BehaviorDomainCatalog.All.Any(domain => domain.TableName.Equals(table, StringComparison.OrdinalIgnoreCase)));
    private string? ResolveDbcMirrorPath(string tableName)
    {
        if (!tableName.EndsWith("_dbc", StringComparison.OrdinalIgnoreCase)) return null; var stem = tableName[..^4];
        foreach (var root in new[] { _session.Settings.OverrideDbcPath, _session.Settings.CoreDbcPath, _session.Settings.BaseDbcPath }.Where(Directory.Exists))
        {
            var match = Directory.EnumerateFiles(root, "*.dbc", SearchOption.TopDirectoryOnly).FirstOrDefault(path => Path.GetFileNameWithoutExtension(path).Equals(stem, StringComparison.OrdinalIgnoreCase)); if (match is not null) return match;
        }
        return null;
    }
    private static string CellText(object? value) => value switch { null => string.Empty, byte[] bytes => "0x" + Convert.ToHexString(bytes), DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture), IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture), _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty };
    private static string QueryCellText(object? value) => value is null ? "<NULL>" : CellText(value);
    private static object? ParseCell(DatabaseColumnCapability column, string? text)
    {
        text ??= string.Empty; var type = column.DataType.ToLowerInvariant();
        if (type.Contains("blob") || type.Contains("binary")) return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.FromHexString(text[2..]) : Encoding.UTF8.GetBytes(text);
        if (type is "tinyint" or "smallint" or "mediumint" or "int" or "bigint" or "decimal" or "float" or "double" or "bit") return text;
        return text;
    }
    private static string FormatResult(SqlQueryResult result) { var builder = new StringBuilder(); builder.AppendLine(string.Join('\t', result.Columns)); foreach (var row in result.Rows) builder.AppendLine(string.Join('\t', row.Select(CellText))); return builder.ToString(); }
    private static string? EmptyNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static IReadOnlyList<string> ParseColumnList(string? value) => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static string SafeName(string value) { var invalid = Path.GetInvalidFileNameChars(); var safe = new string(value.Select(character => invalid.Contains(character) || char.IsWhiteSpace(character) ? '-' : character).ToArray()).Trim('-'); return string.IsNullOrWhiteSpace(safe) ? "row" : safe.Length <= 80 ? safe : safe[..80]; }
    private IStorageProvider Storage() => TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("SQL Studio is not attached to the main window.");
    private static Button Button(string text, Func<Task> action) { var button = new Button { Content = text }; button.Click += async (_, _) => await action(); return button; }
    private static Button AccentButton(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static TextBlock Status(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#99A5B8"), VerticalAlignment = VerticalAlignment.Center };
    private static T WithColumn<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
    private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
}
