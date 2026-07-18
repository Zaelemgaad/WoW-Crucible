using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using WoWCrucible.Core;

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
    private readonly TextBlock _pageStatus = Status("Connect Server & SQL to browse the live database.");
    private readonly TextBlock _status = Status("SQL Studio is idle.");
    private readonly Border _confirmation = new() { IsVisible = false, BorderBrush = Brush.Parse("#6E5426"), BorderThickness = new Thickness(1), Padding = new Thickness(10) };
    private readonly TextBox _query = new() { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono,Consolas"), Text = "SELECT * FROM item_template WHERE entry IN (17, 17802);" };
    private readonly TextBox _queryOutput = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono,Consolas") };
    private readonly ListBox _favorites = new();
    private readonly TextBox _favoriteNotes = new() { PlaceholderText = "Optional note: what you changed or why this row matters" };
    private readonly TextBox _favoriteDbc = new() { PlaceholderText = "Optional related DBC path" };
    private readonly TextBox _favoriteMpq = new() { PlaceholderText = "Optional related MPQ path" };
    private readonly Dictionary<string, (TextBox Text, CheckBox Null)> _editors = new(StringComparer.OrdinalIgnoreCase);
    private SqlTablePage? _page;
    private SqlRowRecord? _selectedRow;
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
        var tabs = new TabControl { Margin = new Thickness(10), Items = { new TabItem { Header = "Tables & rows", Content = BrowsePage() }, new TabItem { Header = "SQL query", Content = QueryPage() }, new TabItem { Header = "Favorites", Content = FavoritesPage() } } };
        Content = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), Children = { new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 0, 0, 1), Child = heading }, WithRow(tabs, 1), WithRow(_confirmation, 2), WithRow(new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 6), Child = _status }, 3) } };
        _tableFilter.TextChanged += (_, _) => PopulateTables(); _tables.SelectionChanged += async (_, _) => await SelectTableAsync();
        _rows.SelectionChanged += (_, _) => SelectRow();
        RefreshFavorites(); PopulateTables();
    }

    public void Activate() { PopulateTables(); if (_tables.SelectedItem is null && _tables.ItemCount > 0) _tables.SelectedIndex = 0; }

    private Control BrowsePage()
    {
        var refresh = Button("Refresh", async () => await LoadPageAsync(false)); var search = Button("Search", async () => { _offset = 0; await LoadPageAsync(false); });
        var previous = Button("← Previous", async () => { _offset = Math.Max(0, _offset - (_page?.Limit ?? 200)); await LoadPageAsync(false); });
        var next = Button("Next →", async () => { if (_page is not null) _offset += _page.Limit; await LoadPageAsync(false); });
        var controls = new WrapPanel { Children = { refresh, _rowSearch, search, previous, next, _pageStatus } };
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

    private void PopulateTables()
    {
        var selected = (_tables.SelectedItem as TableChoice)?.Table.Name; var query = _tableFilter.Text?.Trim() ?? string.Empty;
        var values = (_session.DatabaseCapabilities?.Tables.Values ?? []).Where(table => query.Length == 0 || table.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(table => table.Name).Select(table => new TableChoice(table)).ToArray();
        _tables.ItemsSource = values; if (selected is not null) _tables.SelectedItem = values.FirstOrDefault(item => item.Table.Name.Equals(selected, StringComparison.OrdinalIgnoreCase));
    }

    private async Task SelectTableAsync() { _offset = 0; await LoadPageAsync(false); }
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
        _selectedRow = _rows.SelectedItem as SqlRowRecord; _editors.Clear(); _rowEditor.Children.Clear(); _confirmation.IsVisible = false;
        if (_selectedRow is null || _page is null) return;
        var heading = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8 };
        heading.Children.Add(new TextBlock { Text = $"Complete row editor · {_page.Table} · {_selectedRow.Display}", FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        var favorite = new Button { Content = "★ Favorite" }; favorite.Click += (_, _) => FavoriteSelected(); heading.Children.Add(WithColumn(favorite, 1));
        if (_page.Table is "item_template" or "creature_template") { var guided = AccentButton("Open decoded editor"); guided.Click += (_, _) => GuidedEditRequested?.Invoke(this, new(_page.Table, _selectedRow.Values)); heading.Children.Add(WithColumn(guided, 2)); }
        _rowEditor.Children.Add(heading); _rowEditor.Children.Add(new Grid { ColumnDefinitions = new("*,*,*"), ColumnSpacing = 8, Children = { _favoriteNotes, WithColumn(_favoriteDbc, 1), WithColumn(_favoriteMpq, 2) } });
        foreach (var column in _page.Columns)
        {
            var value = _selectedRow.Values.GetValueOrDefault(column.Name); var text = new TextBox { Text = CellText(value), IsReadOnly = column.Extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase) || column.Extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase) };
            var nullValue = new CheckBox { Content = "NULL", IsChecked = value is null, IsEnabled = column.Nullable && !text.IsReadOnly };
            nullValue.IsCheckedChanged += (_, _) => text.IsEnabled = nullValue.IsChecked != true; text.IsEnabled = value is not null;
            var label = new TextBlock { Text = $"{column.Name}  ·  {column.ColumnType}{(column.Key == "PRI" ? "  ·  PRIMARY KEY" : string.Empty)}", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            _rowEditor.Children.Add(new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 8, Children = { label, WithColumn(text, 1), WithColumn(nullValue, 2) } }); _editors[column.Name] = (text, nullValue);
        }
        var save = AccentButton("Review complete row update"); save.Click += (_, _) => PrepareRowUpdate(); _rowEditor.Children.Add(save);
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
        if (_favorites.SelectedItem is not SqlRowFavorite favorite || _session.DatabaseCapabilities is null) return;
        var table = _session.DatabaseCapabilities.FindTable(favorite.Table); if (table is null) { _status.Text = $"The connected database has no {favorite.Table} table."; return; }
        PopulateTables(); _tables.SelectedItem = (_tables.ItemsSource as IEnumerable<TableChoice>)?.FirstOrDefault(choice => choice.Table.Name.Equals(table.Name, StringComparison.OrdinalIgnoreCase));
        _rowSearch.Text = string.Join(' ', favorite.Key.Values.Where(value => !string.IsNullOrWhiteSpace(value))); _offset = 0; await LoadPageAsync(true);
    }

    private void RefreshFavorites() => _favorites.ItemsSource = SqlFavoriteStore.Load();
    private void SessionChanged(object? sender, EventArgs e) { PopulateTables(); }
    private void Begin(string text) { _operation?.Cancel(); _operation?.Dispose(); _operation = new(); _status.Text = text; }
    private void End() { _operation?.Dispose(); _operation = null; }
    private void Fail(string context, Exception exception) { _status.Text = $"{context}: {exception.Message}"; DesktopCrashLogger.Log(context, exception); }
    public void Dispose() { _operation?.Cancel(); _operation?.Dispose(); _session.Changed -= SessionChanged; }

    private static string RowSummary(SqlRowRecord row) { var name = row.Values.FirstOrDefault(pair => pair.Key.Equals("name", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("LogTitle", StringComparison.OrdinalIgnoreCase)).Value; return $"{row.Display}{(name is null ? string.Empty : $"  ·  {name}")}"; }
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
    private static Button Button(string text, Func<Task> action) { var button = new Button { Content = text }; button.Click += async (_, _) => await action(); return button; }
    private static Button AccentButton(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static TextBlock Status(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#99A5B8"), VerticalAlignment = VerticalAlignment.Center };
    private static T WithColumn<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
    private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
}
