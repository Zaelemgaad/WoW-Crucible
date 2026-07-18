using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class DbdSchemaAuditView : UserControl
{
    private readonly DesktopWorkspaceSession _session;
    private readonly TextBox _definitions = new() { PlaceholderText = "WoWDBDefs definitions folder" };
    private readonly TextBox _dbc = new() { PlaceholderText = "Server or extracted-client DBC/DB2 folder" };
    private readonly TextBox _xml = new() { PlaceholderText = "Optional WDBX XML schema for a three-way comparison" };
    private readonly NumericUpDown _build = new() { Value = 12340, Minimum = 1, Maximum = int.MaxValue };
    private readonly TextBox _search = new() { PlaceholderText = "Filter table names, statuses, and messages…" };
    private readonly ComboBox _statusFilter = new() { ItemsSource = new[] { "All results", "Problems only", "Matches only", "Missing definitions/builds", "Field-count mismatches", "Empty placeholders" }, SelectedIndex = 1 };
    private readonly ListBox _results = new();
    private readonly TextBlock _summary = Status("Choose a DBD corpus and client-table folder, then run the audit.");
    private readonly TextBlock _detail = Status("Select a table to see its complete schema result.");
    private IReadOnlyList<DbdSchemaAuditRow> _rows = [];
    private CancellationTokenSource? _operation;

    public event EventHandler? BackRequested;

    public DbdSchemaAuditView(DesktopWorkspaceSession session)
    {
        _session = session;
        _definitions.Text = string.IsNullOrWhiteSpace(session.Settings.DbdDefinitionsPath) ? DiscoverDefinitions() : session.Settings.DbdDefinitionsPath;
        _dbc.Text = session.Settings.CoreDbcPath;
        _xml.Text = session.Settings.SchemaDefinitionPath;

        _results.ItemTemplate = new FuncDataTemplate<DbdSchemaAuditRow>((row, _) =>
        {
            if (row is null) return new TextBlock();
            var grid = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto,Auto"), ColumnSpacing = 10, Margin = new Thickness(5, 3) };
            Add(grid, row.Status.ToString(), 0, row.Status == DbdAuditStatus.Match ? "#76B78B" : row.Status == DbdAuditStatus.EmptyPlaceholder ? "#A9A7C8" : "#E3A35D");
            Add(grid, row.Table, 1, "#E5EAF2", FontWeight.SemiBold);
            Add(grid, $"CLIENT {row.ActualFields}", 2); Add(grid, $"DBD {row.DbdFields?.ToString() ?? "—"}", 3); Add(grid, $"XML {row.XmlFields?.ToString() ?? "—"}", 4);
            return grid;
        });
        _results.SelectionChanged += (_, _) => ShowDetail();
        _search.TextChanged += (_, _) => ApplyFilter();
        _statusFilter.SelectionChanged += (_, _) => ApplyFilter();

        var back = new Button { Content = "← Editor" }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var run = Accent("Audit build-aware schemas"); run.Click += async (_, _) => await AuditAsync();
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => _operation?.Cancel();
        var definitionsBrowse = new Button { Content = "Browse…" }; definitionsBrowse.Click += async (_, _) => await PickFolderAsync(_definitions, "Select WoWDBDefs definitions");
        var dbcBrowse = new Button { Content = "Browse…" }; dbcBrowse.Click += async (_, _) => await PickFolderAsync(_dbc, "Select DBC/DB2 folder");
        var xmlBrowse = new Button { Content = "Browse…" }; xmlBrowse.Click += async (_, _) => await PickXmlAsync();

        var heading = new Border
        {
            BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 8),
            Child = new WrapPanel { Children = { back, new TextBlock { Text = "DBD SCHEMA PROVIDER", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0), }, run, cancel } }
        };
        var paths = new Grid { ColumnDefinitions = new("Auto,*,Auto"), RowDefinitions = new("Auto,Auto,Auto,Auto"), ColumnSpacing = 8, RowSpacing = 6, Margin = new Thickness(12, 10) };
        AddPath(paths, 0, "Definitions", _definitions, definitionsBrowse); AddPath(paths, 1, "Tables folder", _dbc, dbcBrowse); AddPath(paths, 2, "XML cross-check", _xml, xmlBrowse); AddPath(paths, 3, "Client build", _build, new TextBlock { Text = "12340 = WotLK · 15595 = Cata", Foreground = Brush.Parse("#8995A8"), VerticalAlignment = VerticalAlignment.Center });
        var filters = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(12, 0, 12, 8), Children = { _search, WithColumn(_statusFilter, 1) } };
        var resultGrid = new Grid { RowDefinitions = new("Auto,*,Auto,*"), RowSpacing = 5, Margin = new Thickness(12, 0, 12, 8), Children = { _summary, WithRow(_results, 1), WithRow(new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = Brush.Parse("#2B3445") }, 2), WithRow(new ScrollViewer { Content = _detail }, 3) } };
        Content = new Grid { RowDefinitions = new("Auto,Auto,Auto,*"), Children = { heading, WithRow(paths, 1), WithRow(filters, 2), WithRow(resultGrid, 3) } };
    }

    private async Task AuditAsync()
    {
        _operation?.Cancel(); _operation?.Dispose(); _operation = new();
        try
        {
            var definitions = Path.GetFullPath(_definitions.Text?.Trim() ?? string.Empty); var dbc = Path.GetFullPath(_dbc.Text?.Trim() ?? string.Empty);
            var xml = string.IsNullOrWhiteSpace(_xml.Text) ? null : Path.GetFullPath(_xml.Text.Trim()); var build = decimal.ToInt32(_build.Value ?? 12340);
            _summary.Text = $"Auditing {Path.GetFileName(dbc)} against build {build:N0} definitions…";
            var summary = await Task.Run(() => DbdSchemaService.Audit(definitions, dbc, build, xml), _operation.Token);
            _rows = summary.Rows; ApplyFilter();
            _summary.Text = $"{summary.Rows.Count:N0} tables · {summary.Matches:N0} exact · {summary.EmptyPlaceholders:N0} empty placeholder · {summary.Failures:N0} corpus/layout problem(s)";
            _session.Settings.DbdDefinitionsPath = definitions; _session.Settings.CoreDbcPath = dbc; if (xml is not null) _session.Settings.SchemaDefinitionPath = xml; _session.Settings.Save();
            DesktopCrashLogger.Debug("DBD", "schema-audit-success", ("build", build), ("tables", summary.Rows.Count), ("matches", summary.Matches), ("empty", summary.EmptyPlaceholders), ("problems", summary.Failures));
        }
        catch (OperationCanceledException) { _summary.Text = "DBD schema audit cancelled."; }
        catch (Exception exception) { _summary.Text = $"Audit failed: {exception.Message}"; DesktopCrashLogger.Log("DBD schema audit failed", exception); }
    }

    private void ApplyFilter()
    {
        var query = _search.Text?.Trim() ?? string.Empty; var mode = _statusFilter.SelectedIndex;
        var filtered = _rows.Where(row => (query.Length == 0 || row.Table.Contains(query, StringComparison.OrdinalIgnoreCase) || row.Status.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) || row.Message.Contains(query, StringComparison.OrdinalIgnoreCase)) && mode switch
        {
            1 => row.Status is not DbdAuditStatus.Match and not DbdAuditStatus.EmptyPlaceholder,
            2 => row.Status == DbdAuditStatus.Match,
            3 => row.Status is DbdAuditStatus.MissingDefinition or DbdAuditStatus.MissingBuild,
            4 => row.Status == DbdAuditStatus.FieldCountMismatch,
            5 => row.Status == DbdAuditStatus.EmptyPlaceholder,
            _ => true
        }).ToArray();
        _results.ItemsSource = filtered;
    }

    private void ShowDetail()
    {
        if (_results.SelectedItem is not DbdSchemaAuditRow row) return;
        _detail.Text = $"{row.Table}\nStatus: {row.Status}\nClient-table physical fields: {row.ActualFields:N0}\nDBD physical fields: {row.DbdFields?.ToString("N0") ?? "not resolved"}\nXML fields: {row.XmlFields?.ToString("N0") ?? "not supplied/resolved"}\n\n{row.Message}";
    }

    private async Task PickFolderAsync(TextBox target, string title)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("The schema workspace is not attached to the main window.");
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false }); var path = folders.FirstOrDefault()?.TryGetLocalPath(); if (path is not null) target.Text = path;
    }
    private async Task PickXmlAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("The schema workspace is not attached to the main window.");
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Select optional WDBX XML schema", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("WDBX schema") { Patterns = ["*.xml"] }] }); var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path is not null) _xml.Text = path;
    }

    private static string DiscoverDefinitions()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory }.Distinct(StringComparer.OrdinalIgnoreCase))
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "Tools", "WoWDBDefs", "definitions"); if (Directory.Exists(candidate)) return candidate;
            }
        return string.Empty;
    }
    private static Button Accent(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static TextBlock Status(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") };
    private static void AddPath(Grid grid, int row, string label, Control field, Control action) { var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }; Grid.SetRow(text, row); grid.Children.Add(text); Grid.SetRow(field, row); Grid.SetColumn(field, 1); grid.Children.Add(field); Grid.SetRow(action, row); Grid.SetColumn(action, 2); grid.Children.Add(action); }
    private static void Add(Grid grid, string text, int column, string color = "#9AA5B7", FontWeight? weight = null) { var block = new TextBlock { Text = text, Foreground = Brush.Parse(color), FontWeight = weight ?? FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis }; Grid.SetColumn(block, column); grid.Children.Add(block); }
    private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
    private static T WithColumn<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
}
