using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
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
    private readonly DesktopWorkspaceSession _session;
    private readonly SqlWorkspaceService _service = new();
    private readonly TextBox _tableFilter = new() { PlaceholderText = "Filter tables…" };
    private readonly ListBox _tables = new();
    private readonly TextBox _rowSearch = new() { PlaceholderText = "Search this table by ID, name, or any of the first 24 fields…" };
    private readonly ListBox _rows = new();
    private readonly StackPanel _rowEditor = new() { Spacing = 6 };
    private readonly StackPanel _relationshipResults = new() { Spacing = 5 };
    private readonly SqlDependencyGraphView _dependencyGraph = new();
    private readonly StackPanel _dependencyList = new() { Spacing = 5 };
    private readonly TextBlock _dependencyStatus = Status("Select a SQL row, then analyze its recognized dependencies.");
    private readonly TextBox _dependencyLimit = new() { Text = "200", PlaceholderText = "Rows captured per dependency edge" };
    private readonly TextBlock _pageStatus = Status("Connect Server & SQL to browse the live database.");
    private readonly TextBlock _status = Status("SQL Studio is idle.");
    private readonly Border _confirmation = new() { IsVisible = false, BorderBrush = Brush.Parse("#6E5426"), BorderThickness = new Thickness(1), Padding = new Thickness(10) };
    private readonly TextBox _query = new() { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono,Consolas"), Text = "SELECT * FROM item_template WHERE entry IN (17, 17802);" };
    private readonly TextBox _queryOutput = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono,Consolas") };
    private readonly ListBox _favorites = new();
    private readonly TabControl _tabs;
    private readonly TextBox _favoriteNotes = new() { PlaceholderText = "Optional note: what you changed or why this row matters" };
    private readonly TextBox _favoriteDbc = new() { PlaceholderText = "Optional related DBC path" };
    private readonly TextBox _favoriteMpq = new() { PlaceholderText = "Optional related MPQ path" };
    private readonly Dictionary<string, (TextBox Text, CheckBox Null)> _editors = new(StringComparer.OrdinalIgnoreCase);
    private SqlTablePage? _page;
    private SqlRowRecord? _selectedRow;
    private bool _creatingRow;
    private bool _suppressTableSelection;
    private int _offset;
    private CancellationTokenSource? _operation;

    public event EventHandler? BackRequested;
    public event EventHandler<SqlGuidedEditRequest>? GuidedEditRequested;
    public event EventHandler<string>? OpenDbcRequested;
    public event EventHandler<string>? OpenMpqRequested;

    public SqlWorkspaceView(DesktopWorkspaceSession session)
    {
        _session = session; _session.Changed += SessionChanged;
        var back = new Button { Content = "← Editor" }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var heading = new Grid { ColumnDefinitions = new("Auto,*"), Margin = new Thickness(12, 8), Children = { back, WithColumn(new TextBlock { Text = "SQL STUDIO", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) }, 1) } };
        _tabs = new TabControl { Margin = new Thickness(10), Items = { new TabItem { Header = "Tables & rows", Content = BrowsePage() }, new TabItem { Header = "SQL query", Content = QueryPage() }, new TabItem { Header = "Favorites", Content = FavoritesPage() }, new TabItem { Header = "Dependency graph", Content = DependencyPage() } } };
        Content = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), Children = { new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 0, 0, 1), Child = heading }, WithRow(_tabs, 1), WithRow(_confirmation, 2), WithRow(new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 6), Child = _status }, 3) } };
        _tableFilter.TextChanged += (_, _) => PopulateTables(); _tables.SelectionChanged += async (_, _) => await SelectTableAsync();
        _rows.SelectionChanged += (_, _) => SelectRow();
        RefreshFavorites(); PopulateTables();
    }

    public void Activate() { PopulateTables(); if (_tables.SelectedItem is null && _tables.ItemCount > 0) _tables.SelectedIndex = 0; }

    public async Task OpenExactRowAsync(string tableName, IReadOnlyDictionary<string, object?> values)
    {
        if (_session.DatabaseCapabilities is null || _session.DatabaseProfile is null) throw new InvalidOperationException("Connect Server & SQL before opening a complete row.");
        var table = _session.DatabaseCapabilities.FindTable(tableName) ?? throw new NotSupportedException($"The connected database has no {tableName} table.");
        var primary = table.Columns.Where(column => column.Key.Equals("PRI", StringComparison.OrdinalIgnoreCase)).Select(column => column.Name).ToArray();
        if (primary.Length == 0) throw new InvalidOperationException($"{table.Name} has no primary key, so an exact row cannot be reopened safely.");
        var key = primary.ToDictionary(name => name, name => values.TryGetValue(name, out var value) ? value : throw new InvalidOperationException($"The supplied row is missing primary-key column {name}."), StringComparer.OrdinalIgnoreCase);
        var row = await _service.ReadRowAsync(_session.DatabaseProfile, table, key) ?? throw new InvalidOperationException($"The exact {table.Name} row no longer exists.");
        _suppressTableSelection = true;
        try { PopulateTables(); _tables.SelectedItem = (_tables.ItemsSource as IEnumerable<TableChoice>)?.FirstOrDefault(choice => choice.Table.Name.Equals(table.Name, StringComparison.OrdinalIgnoreCase)); }
        finally { _suppressTableSelection = false; }
        _rowSearch.Text = string.Empty; _offset = 0; _page = new(table.Name, table.Columns, primary, 1, 0, 1, "exact row", [row]);
        _rows.ItemsSource = _page.Rows; _rows.ItemTemplate = new FuncDataTemplate<SqlRowRecord>((value, _) => new TextBlock { Text = value is null ? string.Empty : RowSummary(value), TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(4) });
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
        var controls = new WrapPanel { Children = { refresh, _rowSearch, search, previous, next, create, exportCsv, exportJson, importCsv, _pageStatus } };
        var right = new Grid { RowDefinitions = new("Auto,2*,Auto,*"), Children = { controls, WithRow(new Border { BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), Child = _rows }, 1), WithRow(new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = Brush.Parse("#2B3445") }, 2), WithRow(new ScrollViewer { Content = _rowEditor }, 3) } };
        var left = new Grid { RowDefinitions = new("Auto,*"), Margin = new Thickness(0, 0, 8, 0), Children = { _tableFilter, WithRow(_tables, 1) } };
        return new Grid { ColumnDefinitions = new("*,Auto,3*"), Children = { left, WithColumn(new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Brush.Parse("#2B3445") }, 1), WithColumn(right, 2) } };
    }

    private Control QueryPage()
    {
        var run = AccentButton("Run read-only query"); run.Click += async (_, _) => await RunQueryAsync();
        var prepare = new Button { Content = "Prepare write statement" }; prepare.Click += (_, _) => PrepareStatement();
        return new Grid { RowDefinitions = new("*,Auto,2*"), Children = { _query, WithRow(new WrapPanel { Children = { run, prepare, new TextBlock { Text = "SELECT / SHOW / DESCRIBE / EXPLAIN run immediately. Writes require an inline confirmation and transaction.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7"), VerticalAlignment = VerticalAlignment.Center } } }, 1), WithRow(_queryOutput, 2) } };
    }

    private Control FavoritesPage()
    {
        _favorites.ItemTemplate = new FuncDataTemplate<SqlRowFavorite>((item, _) => item is null ? new TextBlock() : new StackPanel { Margin = new Thickness(4), Children = { new TextBlock { Text = $"{item.Label}  ·  {item.Database}.{item.Table}", FontWeight = FontWeight.SemiBold }, new TextBlock { Text = $"{string.Join(", ", item.Key.Select(pair => $"{pair.Key}={pair.Value}"))}{(string.IsNullOrWhiteSpace(item.Notes) ? string.Empty : $"  ·  {item.Notes}")}{(string.IsNullOrWhiteSpace(item.DbcPath) ? string.Empty : $"  ·  DBC {item.DbcPath}")}{(string.IsNullOrWhiteSpace(item.MpqPath) ? string.Empty : $"  ·  MPQ {item.MpqPath}")}", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") } } });
        var open = AccentButton("Open selected favorite"); open.Click += async (_, _) => await OpenFavoriteAsync();
        var remove = new Button { Content = "Remove favorite" }; remove.Click += (_, _) => { if (_favorites.SelectedItem is SqlRowFavorite favorite) { SqlFavoriteStore.Remove(favorite.Identity); RefreshFavorites(); } };
        var openDbc = new Button { Content = "Open linked DBC" }; openDbc.Click += (_, _) => { if (_favorites.SelectedItem is SqlRowFavorite { DbcPath: { Length: > 0 } path }) OpenDbcRequested?.Invoke(this, path); };
        var openMpq = new Button { Content = "Open linked MPQ" }; openMpq.Click += (_, _) => { if (_favorites.SelectedItem is SqlRowFavorite { MpqPath: { Length: > 0 } path }) OpenMpqRequested?.Invoke(this, path); };
        return new Grid { RowDefinitions = new("Auto,*"), Children = { new WrapPanel { Children = { open, remove, openDbc, openMpq } }, WithRow(_favorites, 1) } };
    }

    private Control DependencyPage()
    {
        var analyze = AccentButton("Analyze selected row"); analyze.Click += async (_, _) => await AnalyzeRelationshipsAsync(analyze);
        var export = new Button { Content = "Capture complete dependency snapshot…" }; export.Click += async (_, _) => await ExportDependencySnapshotAsync(export);
        var controls = new Grid { ColumnDefinitions = new("Auto,Auto,*,*"), ColumnSpacing = 8, Children = { analyze, WithColumn(export, 1), WithColumn(new TextBlock { Text = "Per-edge capture limit", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right }, 2), WithColumn(_dependencyLimit, 3) } };
        var details = new ScrollViewer { Content = _dependencyList };
        return new Grid { RowDefinitions = new("Auto,2*,Auto,*,Auto"), RowSpacing = 7, Children = { controls, WithRow(new Border { BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), Child = _dependencyGraph }, 1), WithRow(new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = Brush.Parse("#2B3445") }, 2), WithRow(details, 3), WithRow(_dependencyStatus, 4) } };
    }

    private void PopulateTables()
    {
        var selected = (_tables.SelectedItem as TableChoice)?.Table.Name; var query = _tableFilter.Text?.Trim() ?? string.Empty;
        var values = (_session.DatabaseCapabilities?.Tables.Values ?? []).Where(table => query.Length == 0 || table.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(table => table.Name).Select(table => new TableChoice(table)).ToArray();
        _tables.ItemsSource = values; if (selected is not null) _tables.SelectedItem = values.FirstOrDefault(item => item.Table.Name.Equals(selected, StringComparison.OrdinalIgnoreCase));
    }

    private async Task SelectTableAsync() { if (_suppressTableSelection) return; _offset = 0; await LoadPageAsync(false); }
    private async Task LoadPageAsync(bool selectFirst)
    {
        if (_session.DatabaseProfile is null || _tables.SelectedItem is not TableChoice selected) { _pageStatus.Text = "Connect Server & SQL and select a table."; return; }
        Begin($"Loading {selected.Table.Name}…");
        try
        {
            var profile = _session.DatabaseProfile; var search = _rowSearch.Text;
            _page = await _service.ReadPageAsync(profile, selected.Table, _offset, 200, search, _operation!.Token); _offset = _page.Offset;
            _rows.ItemsSource = _page.Rows; _rows.ItemTemplate = new FuncDataTemplate<SqlRowRecord>((row, _) => new TextBlock { Text = row is null ? string.Empty : RowSummary(row), TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(4) });
            _pageStatus.Text = _page.TotalRows == 0 ? "No matching rows." : $"{_page.Offset + 1:N0}–{Math.Min(_page.Offset + _page.Rows.Count, _page.TotalRows):N0} of {_page.TotalRows:N0}";
            if (selectFirst && _page.Rows.Count > 0) _rows.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { _status.Text = "Table load cancelled."; }
        catch (Exception exception) { Fail("Table load failed", exception); }
        finally { End(); }
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
        if (_page.Table is "item_template" or "creature_template" or "gameobject_template" or "quest_template" || BehaviorDomainCatalog.All.Any(domain => domain.TableName.Equals(_page.Table, StringComparison.OrdinalIgnoreCase))) { var guided = AccentButton("Open decoded editor"); guided.Click += (_, _) => GuidedEditRequested?.Invoke(this, new(_page.Table, _selectedRow.Values)); actions.Children.Add(guided); }
        var delete = new Button { Content = "Delete exactly this row" }; delete.Click += (_, _) => PrepareDelete(); actions.Children.Add(delete); heading.Children.Add(actions);
        _rowEditor.Children.Add(heading); _rowEditor.Children.Add(new Grid { ColumnDefinitions = new("*,*,*"), ColumnSpacing = 8, Children = { _favoriteNotes, WithColumn(_favoriteDbc, 1), WithColumn(_favoriteMpq, 2) } });
        foreach (var column in _page.Columns)
        {
            var value = _selectedRow.Values.GetValueOrDefault(column.Name); var text = new TextBox { Text = CellText(value), IsReadOnly = column.Extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase) || column.Extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase) };
            var nullValue = new CheckBox { Content = "NULL", IsChecked = value is null, IsEnabled = column.Nullable && !text.IsReadOnly };
            nullValue.IsCheckedChanged += (_, _) => text.IsEnabled = nullValue.IsChecked != true; text.IsEnabled = value is not null;
            var label = new TextBlock { Text = $"{column.Name}  ·  {column.ColumnType}{(column.Key == "PRI" ? "  ·  PRIMARY KEY" : string.Empty)}", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            _rowEditor.Children.Add(new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 8, Children = { label, WithColumn(text, 1), WithColumn(nullValue, 2) } }); _editors[column.Name] = (text, nullValue);
        }
        var save = AccentButton("Review complete row update"); save.Click += (_, _) => PrepareRowUpdate(); _rowEditor.Children.Add(save); AddRelationships();
    }

    private void BeginCreateRow()
    {
        if (_page is null || _tables.SelectedItem is not TableChoice) { _status.Text = "Select a table first."; return; }
        _rows.SelectedItem = null; _creatingRow = true; _selectedRow = null; _editors.Clear(); _rowEditor.Children.Clear(); _confirmation.IsVisible = false;
        _rowEditor.Children.Add(new TextBlock { Text = $"New {_page.Table} row · every schema column is available", FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        _rowEditor.Children.Add(new TextBlock { Text = "Auto-increment and generated fields may be omitted. Required fields are identified from the live schema. This is INSERT-only: existing keys are never replaced.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") });
        foreach (var column in _page.Columns)
        {
            var generated = column.Extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase); var automatic = column.Extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase);
            var text = new TextBox { Text = column.DefaultValue ?? string.Empty, IsReadOnly = generated };
            var omit = generated || automatic || column.Nullable || column.DefaultValue is not null;
            var nullValue = new CheckBox { Content = generated || automatic ? "OMIT" : "NULL / OMIT", IsChecked = omit, IsEnabled = !generated };
            nullValue.IsCheckedChanged += (_, _) => text.IsEnabled = nullValue.IsChecked != true; text.IsEnabled = !omit;
            var required = !column.Nullable && column.DefaultValue is null && !automatic && !generated;
            var label = new TextBlock { Text = $"{column.Name}  ·  {column.ColumnType}{(required ? "  ·  REQUIRED" : string.Empty)}{(automatic ? "  ·  AUTO" : string.Empty)}", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            _rowEditor.Children.Add(new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 8, Children = { label, WithColumn(text, 1), WithColumn(nullValue, 2) } }); _editors[column.Name] = (text, nullValue);
        }
        var insert = AccentButton("Review new row insert"); insert.Click += (_, _) => PrepareInsert(); _rowEditor.Children.Add(insert);
    }

    private void FavoriteSelected()
    {
        if (_selectedRow is null || _page is null || _selectedRow.Key.Count == 0) { _status.Text = "Rows without a primary key cannot be favorited safely."; return; }
        var key = _selectedRow.Key.ToDictionary(pair => pair.Key, pair => Convert.ToString(pair.Value, CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase);
        var name = _selectedRow.Values.FirstOrDefault(pair => pair.Key.Equals("name", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("LogTitle", StringComparison.OrdinalIgnoreCase)).Value;
        var label = string.IsNullOrWhiteSpace(Convert.ToString(name)) ? $"{_page.Table} · {_selectedRow.Display}" : $"{Convert.ToString(name)} · {_selectedRow.Display}";
        SqlFavoriteStore.Save(new(_session.DatabaseProfile?.Database ?? string.Empty, _page.Table, key, label, _favoriteNotes.Text ?? string.Empty, DateTimeOffset.UtcNow, EmptyNull(_favoriteDbc.Text), EmptyNull(_favoriteMpq.Text))); RefreshFavorites(); _status.Text = $"Favorited {label}.";
    }

    private void PrepareRowUpdate()
    {
        if (_selectedRow is null || _page is null || _session.DatabaseProfile is null) return;
        var values = _editors.ToDictionary(pair => pair.Key, pair => pair.Value.Null.IsChecked == true ? null : ParseCell(_page.Columns.Single(column => column.Name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)), pair.Value.Text.Text), StringComparer.OrdinalIgnoreCase);
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => _confirmation.IsVisible = false;
        var confirm = AccentButton("Commit exactly this row"); confirm.Click += async (_, _) => { try { confirm.IsEnabled = false; await _service.UpdateRowAsync(_session.DatabaseProfile, _page.Columns.Count > 0 ? (_tables.SelectedItem as TableChoice)!.Table : throw new InvalidOperationException(), _selectedRow.Key, values); _confirmation.IsVisible = false; _status.Text = "One row updated transactionally."; await LoadPageAsync(false); } catch (Exception exception) { Fail("Row update failed", exception); } finally { confirm.IsEnabled = true; } };
        _confirmation.Child = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = $"Update every writable field of {_page.Table} where {_selectedRow.Display}? The primary key identifies exactly one row; no INSERT/DELETE is implied.", TextWrapping = TextWrapping.Wrap }, WithColumn(cancel, 1), WithColumn(confirm, 2) } }; _confirmation.IsVisible = true;
    }

    private void PrepareInsert()
    {
        if (!_creatingRow || _page is null || _session.DatabaseProfile is null || _tables.SelectedItem is not TableChoice selected) return;
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _editors)
        {
            if (pair.Value.Null.IsChecked == true) continue;
            var column = _page.Columns.Single(value => value.Name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
            values[pair.Key] = ParseCell(column, pair.Value.Text.Text);
        }
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => _confirmation.IsVisible = false;
        var confirm = AccentButton("Insert one new row"); confirm.Click += async (_, _) =>
        {
            try { confirm.IsEnabled = false; var result = await _service.InsertRowAsync(_session.DatabaseProfile, selected.Table, values); _confirmation.IsVisible = false; _status.Text = result.InsertedId > 0 ? $"One row inserted transactionally · generated ID {result.InsertedId}." : "One row inserted transactionally."; _creatingRow = false; await LoadPageAsync(false); }
            catch (Exception exception) { Fail("Row insert failed", exception); } finally { confirm.IsEnabled = true; }
        };
        _confirmation.Child = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = $"Insert one new row into {_page.Table} using {values.Count:N0} supplied field(s)? Existing primary keys are blocked; Crucible will not replace or upsert.", TextWrapping = TextWrapping.Wrap }, WithColumn(cancel, 1), WithColumn(confirm, 2) } }; _confirmation.IsVisible = true;
    }

    private void PrepareDelete()
    {
        if (_selectedRow is null || _page is null || _session.DatabaseProfile is null || _tables.SelectedItem is not TableChoice selected) return;
        if (_selectedRow.Key.Count == 0) { _status.Text = "This table has no primary key; Crucible refuses an ambiguous delete."; return; }
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => _confirmation.IsVisible = false;
        var confirm = new Button { Content = $"Delete {_selectedRow.Display}" }; confirm.Click += async (_, _) =>
        {
            try { confirm.IsEnabled = false; await _service.DeleteRowAsync(_session.DatabaseProfile, selected.Table, _selectedRow.Key); _confirmation.IsVisible = false; _status.Text = "Exactly one row deleted transactionally."; await LoadPageAsync(false); }
            catch (Exception exception) { Fail("Row delete failed", exception); } finally { confirm.IsEnabled = true; }
        };
        _confirmation.Child = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = $"Permanently delete exactly one {_page.Table} row where {_selectedRow.Display}? Crucible preflights that the full primary key matches one row and rolls back if it does not.", TextWrapping = TextWrapping.Wrap }, WithColumn(cancel, 1), WithColumn(confirm, 2) } }; _confirmation.IsVisible = true;
    }

    private void AddRelationships()
    {
        if (_selectedRow is null || _page is null || _session.DatabaseCapabilities is null) return;
        var relations = _session.DatabaseCapabilities.Relationships.Where(relation => relation.Touches(_page.Table)).ToArray();
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
        if (_selectedRow is null || _page is null || _session.DatabaseCapabilities is null || _session.DatabaseProfile is null) return;
        try
        {
            button.IsEnabled = false; Begin($"Analyzing dependencies for {_selectedRow.Display}…");
            var matches = await _service.AnalyzeRelationshipsAsync(_session.DatabaseProfile, _session.DatabaseCapabilities, _page.Table, _selectedRow.Values, _operation!.Token);
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
        if (_selectedRow is null || _page is null || _session.DatabaseCapabilities is null || _session.DatabaseProfile is null) { _dependencyStatus.Text = "Select a primary-keyed SQL row first."; return; }
        if (!int.TryParse(_dependencyLimit.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) || limit is < 1 or > 500) { _dependencyStatus.Text = "Per-edge capture limit must be a whole number from 1 through 500."; return; }
        try
        {
            var file = await Storage().SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export read-only SQL dependency snapshot", SuggestedFileName = $"{_page.Table}-{SafeName(_selectedRow.Display)}.crucible-dependencies.json", FileTypeChoices = [new FilePickerFileType("Crucible dependency snapshot") { Patterns = ["*.crucible-dependencies.json", "*.json"] }] });
            var path = file?.TryGetLocalPath(); if (path is null) return;
            button.IsEnabled = false; Begin($"Capturing complete dependency rows for {_selectedRow.Display}…");
            var snapshot = await _service.CaptureDependencySnapshotAsync(_session.DatabaseProfile, _session.DatabaseCapabilities, _page.Table, _selectedRow, limit, _operation!.Token);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, _operation.Token);
            var summary = $"Captured the complete root row plus {snapshot.Edges.Sum(edge => edge.Rows.Count):N0} related row(s) across {snapshot.Edges.Count:N0} edge(s). {snapshot.Edges.Count(edge => edge.Truncated):N0} edge(s) were explicitly marked truncated."; _dependencyStatus.Text = summary; _status.Text = $"{summary} Snapshot: {path}";
        }
        catch (OperationCanceledException) { _dependencyStatus.Text = "Dependency snapshot capture cancelled."; }
        catch (Exception exception) { Fail("Dependency snapshot failed", exception); _dependencyStatus.Text = $"Dependency snapshot failed: {exception.Message}"; }
        finally { button.IsEnabled = true; End(); }
    }

    private async Task OpenRelatedAsync(string tableName, string columnName, object? value)
    {
        PopulateTables(); var choice = (_tables.ItemsSource as IEnumerable<TableChoice>)?.FirstOrDefault(item => item.Table.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase));
        if (choice is null) { _status.Text = $"Related table {tableName} is not available in the connected schema."; return; }
        try
        {
            _suppressTableSelection = true; _tables.SelectedItem = choice; _rowSearch.Text = string.Empty; _offset = 0;
            Begin($"Opening exact relationship {tableName}.{columnName}…"); _page = await _service.ReadColumnMatchesAsync(_session.DatabaseProfile!, choice.Table, columnName, value, 200, _operation!.Token);
            _rows.ItemsSource = _page.Rows; _rows.ItemTemplate = new FuncDataTemplate<SqlRowRecord>((row, _) => new TextBlock { Text = row is null ? string.Empty : RowSummary(row), TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(4) });
            _pageStatus.Text = _page.TotalRows == 0 ? $"No rows where {columnName} exactly equals {CellText(value)}." : $"Exact {columnName} match · showing {_page.Rows.Count:N0} of {_page.TotalRows:N0}";
            if (_page.Rows.Count > 0) _rows.SelectedIndex = 0; _status.Text = $"Opened exact dependency edge {tableName}.{columnName} = {CellText(value)}.";
        }
        catch (OperationCanceledException) { _status.Text = "Relationship navigation cancelled."; }
        catch (Exception exception) { Fail("Relationship navigation failed", exception); }
        finally { _suppressTableSelection = false; End(); }
    }

    private async Task ExportTableAsync(SqlExportFormat format)
    {
        if (_session.DatabaseProfile is null || _tables.SelectedItem is not TableChoice selected) { _status.Text = "Connect SQL and select a table first."; return; }
        try
        {
            var extension = format == SqlExportFormat.Csv ? "csv" : "jsonl";
            var file = await Storage().SaveFilePickerAsync(new FilePickerSaveOptions { Title = $"Export complete {selected.Table.Name} table", SuggestedFileName = $"{selected.Table.Name}.{extension}", FileTypeChoices = [new FilePickerFileType(format == SqlExportFormat.Csv ? "CSV" : "JSON Lines") { Patterns = [$"*.{extension}"] }] });
            var path = file?.TryGetLocalPath(); if (path is null) return;
            Begin($"Streaming complete {selected.Table.Name} export…"); var result = await new SqlTransferService().ExportTableAsync(_session.DatabaseProfile, selected.Table, path, format, overwrite: true, cancellationToken: _operation!.Token); _status.Text = $"Exported {result.Rows:N0} row(s) to {result.Path}.";
        }
        catch (OperationCanceledException) { _status.Text = "Table export cancelled."; }
        catch (Exception exception) { Fail("Table export failed", exception); }
        finally { End(); }
    }

    private async Task PrepareImportAsync()
    {
        if (_session.DatabaseProfile is null || _tables.SelectedItem is not TableChoice selected) { _status.Text = "Connect SQL and select a table first."; return; }
        try
        {
            var files = await Storage().OpenFilePickerAsync(new FilePickerOpenOptions { Title = $"Import CSV rows into {selected.Table.Name}", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }] });
            var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path is null) return;
            var plan = await Task.Run(() => new SqlTransferService().AnalyzeCsv(path, selected.Table));
            if (!plan.CanApply) { _status.Text = $"CSV import blocked: {string.Join(" ", plan.Findings)}"; return; }
            var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => _confirmation.IsVisible = false;
            var confirm = AccentButton($"Insert {plan.Rows:N0} row(s)"); confirm.Click += async (_, _) =>
            {
                try { confirm.IsEnabled = false; var inserted = await new SqlTransferService().ImportCsvAsync(_session.DatabaseProfile, selected.Table, plan.Path); _confirmation.IsVisible = false; _status.Text = $"Imported {inserted:N0} row(s) transactionally. Any duplicate/error would have rolled back the entire import."; await LoadPageAsync(false); }
                catch (Exception exception) { Fail("CSV import failed", exception); } finally { confirm.IsEnabled = true; }
            };
            _confirmation.Child = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = $"Dry-run passed: {plan.Rows:N0} row(s), {plan.Columns.Count:N0} mapped column(s), zero structural findings. Insert into {selected.Table.Name}? This is insert-only and one transaction; duplicate keys never overwrite existing data.", TextWrapping = TextWrapping.Wrap }, WithColumn(cancel, 1), WithColumn(confirm, 2) } }; _confirmation.IsVisible = true;
        }
        catch (Exception exception) { Fail("CSV analysis failed", exception); }
    }

    private async Task RunQueryAsync()
    {
        if (_session.DatabaseProfile is null) { _status.Text = "Connect Server & SQL first."; return; } var sql = _query.Text ?? string.Empty;
        if (!IsReadOnly(sql)) { _status.Text = "This is not recognized as read-only. Use Prepare write statement so it cannot execute accidentally."; return; }
        Begin("Running read-only query…"); try { var result = await _service.QueryAsync(_session.DatabaseProfile, sql, 2000, _operation!.Token); _queryOutput.Text = FormatResult(result); _status.Text = $"Returned {result.Rows.Count:N0} row(s) in {result.Duration.TotalMilliseconds:N0} ms."; } catch (Exception exception) { Fail("Query failed", exception); } finally { End(); }
    }

    private void PrepareStatement()
    {
        if (_session.DatabaseProfile is null) { _status.Text = "Connect Server & SQL first."; return; } var sql = _query.Text ?? string.Empty;
        if (IsReadOnly(sql)) { _status.Text = "Use Run read-only query for this statement."; return; }
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => _confirmation.IsVisible = false;
        var confirm = AccentButton("Execute write transaction"); confirm.Click += async (_, _) => { try { confirm.IsEnabled = false; var result = await _service.ExecuteAsync(_session.DatabaseProfile, sql); _queryOutput.Text = $"Affected rows: {result.AffectedRows:N0}\nDuration: {result.Duration.TotalMilliseconds:N0} ms"; _confirmation.IsVisible = false; _status.Text = "Statement committed transactionally."; } catch (Exception exception) { Fail("Statement failed", exception); } finally { confirm.IsEnabled = true; } };
        _confirmation.Child = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = $"Execute this non-read-only statement against {_session.DatabaseProfile.Database}? Review it carefully. Crucible begins a transaction, but MySQL schema/DDL statements can implicitly commit and may not be rollbackable.", TextWrapping = TextWrapping.Wrap }, WithColumn(cancel, 1), WithColumn(confirm, 2) } }; _confirmation.IsVisible = true;
    }

    private async Task OpenFavoriteAsync()
    {
        if (_favorites.SelectedItem is not SqlRowFavorite favorite || _session.DatabaseCapabilities is null || _session.DatabaseProfile is null) return;
        var table = _session.DatabaseCapabilities.FindTable(favorite.Table); if (table is null) { _status.Text = $"The connected database has no {favorite.Table} table."; return; }
        try
        {
            Begin($"Opening exact favorite {favorite.Label}…");
            var key = favorite.Key.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.OrdinalIgnoreCase);
            var row = await _service.ReadRowAsync(_session.DatabaseProfile, table, key, _operation!.Token);
            if (row is null) { _status.Text = $"The exact favorite no longer exists: {favorite.Database}.{favorite.Table} · {string.Join(", ", favorite.Key.Select(pair => $"{pair.Key}={pair.Value}"))}."; return; }
            _suppressTableSelection = true; try { PopulateTables(); _tables.SelectedItem = (_tables.ItemsSource as IEnumerable<TableChoice>)?.FirstOrDefault(choice => choice.Table.Name.Equals(table.Name, StringComparison.OrdinalIgnoreCase)); } finally { _suppressTableSelection = false; }
            _rowSearch.Text = string.Empty; _offset = 0; _page = new(table.Name, table.Columns, row.Key.Keys.ToArray(), 1, 0, 1, "exact favorite", [row]);
            _rows.ItemsSource = _page.Rows; _rows.ItemTemplate = new FuncDataTemplate<SqlRowRecord>((value, _) => new TextBlock { Text = value is null ? string.Empty : RowSummary(value), TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(4) });
            _pageStatus.Text = "Exact primary-key favorite · 1 row"; _tabs.SelectedIndex = 0; _rows.SelectedItem = row;
            _status.Text = $"Opened exact favorite {row.Display}. No broad text-search substitution was used.";
        }
        catch (OperationCanceledException) { _status.Text = "Favorite lookup cancelled."; }
        catch (Exception exception) { Fail("Favorite lookup failed", exception); }
        finally { End(); }
    }

    private void RefreshFavorites() => _favorites.ItemsSource = SqlFavoriteStore.Load();
    private void SessionChanged(object? sender, EventArgs e) { PopulateTables(); }
    private void Begin(string text) { _operation?.Cancel(); _operation?.Dispose(); _operation = new(); _status.Text = text; }
    private void End() { _operation?.Dispose(); _operation = null; }
    private void Fail(string context, Exception exception) { _status.Text = $"{context}: {exception.Message}"; DesktopCrashLogger.Log(context, exception); }
    public void Dispose() { _operation?.Cancel(); _operation?.Dispose(); _session.Changed -= SessionChanged; }

    private static string RowSummary(SqlRowRecord row) { var name = row.Values.FirstOrDefault(pair => pair.Key.Equals("name", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("LogTitle", StringComparison.OrdinalIgnoreCase)).Value; return $"{row.Display}{(name is null ? string.Empty : $"  ·  {name}")}"; }
    private static string RelationshipCount(SqlRelationshipMatch match) => match.MatchingRows < 0 ? "file DBC · SQL mirror empty" : $"{match.MatchingRows:N0} exact row(s)";
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
    private static object? ParseCell(DatabaseColumnCapability column, string? text)
    {
        text ??= string.Empty; var type = column.DataType.ToLowerInvariant();
        if (type.Contains("blob") || type.Contains("binary")) return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.FromHexString(text[2..]) : Encoding.UTF8.GetBytes(text);
        if (type is "tinyint" or "smallint" or "mediumint" or "int" or "bigint" or "decimal" or "float" or "double" or "bit") return text;
        return text;
    }
    private static bool IsReadOnly(string sql) => SqlWorkspaceService.IsReadOnlyStatement(sql);
    private static string FormatResult(SqlQueryResult result) { var builder = new StringBuilder(); builder.AppendLine(string.Join('\t', result.Columns)); foreach (var row in result.Rows) builder.AppendLine(string.Join('\t', row.Select(CellText))); return builder.ToString(); }
    private static string? EmptyNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string SafeName(string value) { var invalid = Path.GetInvalidFileNameChars(); var safe = new string(value.Select(character => invalid.Contains(character) || char.IsWhiteSpace(character) ? '-' : character).ToArray()).Trim('-'); return string.IsNullOrWhiteSpace(safe) ? "row" : safe.Length <= 80 ? safe : safe[..80]; }
    private IStorageProvider Storage() => TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("SQL Studio is not attached to the main window.");
    private static Button Button(string text, Func<Task> action) { var button = new Button { Content = text }; button.Click += async (_, _) => await action(); return button; }
    private static Button AccentButton(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static TextBlock Status(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#99A5B8"), VerticalAlignment = VerticalAlignment.Center };
    private static T WithColumn<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
    private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
}
