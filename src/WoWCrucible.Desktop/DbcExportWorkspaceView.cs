using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class DbcExportWorkspaceView : UserControl, IDisposable
{
    private sealed class ColumnChoice(DbcColumn column)
    {
        public DbcColumn Column { get; } = column;
        public bool Selected { get; set; } = true;
    }

    private readonly DbcDocumentSession _document;
    private readonly List<ColumnChoice> _choices;
    private readonly ListBox _columns = new();
    private readonly TextBox _columnSearch = new() { PlaceholderText = "Filter column names and types…" };
    private readonly TextBox _keys = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, PlaceholderText = "Optional record keys: 133, 116, 900-999" };
    private readonly ComboBox _format = new() { ItemsSource = new[] { "CSV", "JSON Lines", "JSON array" }, SelectedIndex = 0 };
    private readonly ToggleSwitch _rawStrings = new() { OnContent = "Raw string offsets", OffContent = "Decoded strings", IsChecked = false };
    private readonly ToggleSwitch _overwrite = new() { OnContent = "Overwrite allowed", OffContent = "Refuse existing output", IsChecked = false };
    private readonly TextBox _output = new() { PlaceholderText = "Output CSV / JSON / JSONL path" };
    private readonly TextBox _preview = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap };
    private readonly TextBlock _status = Status("Choose columns and preview the export. Nothing is written until Export is pressed.");
    private CancellationTokenSource? _operation;

    public event EventHandler? BackRequested;

    public DbcExportWorkspaceView(DbcDocumentSession document)
    {
        _document = document; _choices = document.Schema.Columns.Select(column => new ColumnChoice(column)).ToList();
        _columns.ItemTemplate = new FuncDataTemplate<ColumnChoice>((choice, _) =>
        {
            if (choice is null) return new TextBlock();
            var check = new CheckBox
            {
                IsChecked = choice.Selected,
                Content = new StackPanel
                {
                    Spacing = 1, Children =
                    {
                        new TextBlock { Text = choice.Column.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = $"field {choice.Column.Index:N0} · {choice.Column.Type} · {choice.Column.Size} byte(s){(choice.Column.IsIndex ? " · physical key" : string.Empty)}", Foreground = Brush.Parse("#8490A4"), FontSize = 10 }
                    }
                }
            };
            check.IsCheckedChanged += (_, _) => choice.Selected = check.IsChecked == true;
            return check;
        });
        _columnSearch.TextChanged += (_, _) => RefreshColumns(); RefreshColumns();

        var back = new Button { Content = "← DBC editor" }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var preview = Accent("Preview rows"); preview.Click += async (_, _) => await PreviewAsync();
        var export = Accent("Export atomically"); export.Click += async (_, _) => await ExportAsync();
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => _operation?.Cancel();
        var browse = new Button { Content = "Output…" }; browse.Click += async (_, _) => await PickOutputAsync();
        var all = new Button { Content = "Select all" }; all.Click += (_, _) => SetColumns(true);
        var none = new Button { Content = "Select none" }; none.Click += (_, _) => SetColumns(false);

        var header = new Border
        {
            BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 8),
            Child = new WrapPanel
            {
                Children =
                {
                    back,
                    new TextBlock { Text = "DBC ROW EXPORT", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) },
                    preview, export, cancel
                }
            }
        };
        var identity = new TextBlock
        {
            Text = $"{Path.GetFileName(document.File.SourcePath)} · {document.File.RowCount:N0} rows · {document.Schema.Columns.Count:N0} physical columns · key {document.Schema.KeyStrategy.DisplayName(document.Schema.Columns)} · {document.Schema.MatchKind}\n{document.SchemaSource}",
            TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse(document.Schema.UsedFallback ? "#E5B768" : "#9AA5B7"), Margin = new Thickness(12, 8, 12, 0)
        };

        var columnTools = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 6, Children = { _columnSearch, WithColumn(all, 1), WithColumn(none, 2) } };
        var left = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 7, Children = { columnTools, WithRow(_columns, 1) } };
        var path = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 6, Children = { _output, WithColumn(browse, 1) } };
        var options = new Grid { ColumnDefinitions = new("*,*,*"), ColumnSpacing = 8, Children = { _format, WithColumn(_rawStrings, 1), WithColumn(_overwrite, 2) } };
        var controls = new StackPanel
        {
            Spacing = 7, Children =
            {
                new TextBlock { Text = "RECORD FILTER", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#C58A2B") },
                _keys,
                new TextBlock { Text = "Leave blank for every row. Ranges are inclusive. Key filtering is blocked when the schema has no proven stable identity.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8490A4"), FontSize = 10 },
                new TextBlock { Text = "OUTPUT", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#C58A2B") },
                options, path
            }
        };
        var right = new Grid { RowDefinitions = new("Auto,Auto,*"), RowSpacing = 8, Children = { controls, WithRow(_status, 1), WithRow(_preview, 2) } };
        var body = new Grid
        {
            ColumnDefinitions = new("*,Auto,2*"), ColumnSpacing = 5, Margin = new Thickness(12, 9, 12, 12),
            Children = { left, WithColumn(new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Brush.Parse("#2B3445") }, 1), WithColumn(right, 2) }
        };
        Content = new Grid { RowDefinitions = new("Auto,Auto,*"), Children = { header, WithRow(identity, 1), WithRow(body, 2) } };
    }

    private async Task PreviewAsync()
    {
        var operation = Begin("Building a decoded preview…");
        try
        {
            var selected = SelectedColumns(); var keys = ParseKeys(_keys.Text); var raw = _rawStrings.IsChecked == true;
            var result = await Task.Run(() => DbcRowExportService.Preview(_document.File, _document.Schema, selected, keys, raw, 25), operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            _preview.Text = JsonSerializer.Serialize(result.Rows, new JsonSerializerOptions { WriteIndented = true });
            _status.Text = $"Previewing {result.Rows.Count:N0} of {result.MatchingRows:N0} matching row(s) · {result.Columns.Count:N0} output columns · {(raw ? "raw string offsets" : "decoded strings")}. $recordKey and $rowIndex are always included.";
        }
        catch (OperationCanceledException) { _status.Text = "Export preview cancelled."; }
        catch (Exception exception) { _status.Text = $"Preview failed: {exception.Message}"; DesktopCrashLogger.Log("DBC export preview failed", exception); }
        finally { End(operation); }
    }

    private async Task ExportAsync()
    {
        if (string.IsNullOrWhiteSpace(_output.Text)) { await PickOutputAsync(); if (string.IsNullOrWhiteSpace(_output.Text)) return; }
        var operation = Begin("Writing export to a verified temporary file…");
        try
        {
            var format = Format(); var selected = SelectedColumns(); var keys = ParseKeys(_keys.Text); var raw = _rawStrings.IsChecked == true; var output = Path.GetFullPath(_output.Text.Trim());
            var progress = new Progress<(int Done, int Total)>(value => _status.Text = $"Exporting {value.Done:N0}/{value.Total:N0} rows…");
            var result = await Task.Run(() => DbcRowExportService.Export(_document.File, _document.Schema, output, new(format, selected, keys, raw, _overwrite.IsChecked == true), progress, operation.Token), operation.Token);
            _status.Text = $"Exported {result.ExportedRows:N0}/{result.SourceRows:N0} row(s) atomically · {result.Columns.Count:N0} columns · {result.OutputPath}";
            DesktopCrashLogger.Debug("DBC", "row-export-success", ("source", _document.File.SourcePath), ("output", result.OutputPath), ("rows", result.ExportedRows), ("columns", result.Columns.Count), ("format", result.Format), ("raw_strings", raw));
        }
        catch (OperationCanceledException) { _status.Text = "DBC export cancelled; no partial output was published."; }
        catch (Exception exception) { _status.Text = $"Export failed: {exception.Message}"; DesktopCrashLogger.Log("DBC row export failed", exception); }
        finally { End(operation); }
    }

    private async Task PickOutputAsync()
    {
        var extension = Format() switch { DbcRowExportFormat.Csv => ".csv", DbcRowExportFormat.Json => ".json", _ => ".jsonl" };
        var file = await Storage().SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export decoded DBC rows", SuggestedFileName = Path.GetFileNameWithoutExtension(_document.File.SourcePath) + "-rows" + extension,
            FileTypeChoices = [new FilePickerFileType("DBC row export") { Patterns = ["*.csv", "*.json", "*.jsonl"] }]
        });
        var path = file?.TryGetLocalPath(); if (path is not null) _output.Text = path;
    }

    private IReadOnlyList<string> SelectedColumns()
    {
        var selected = _choices.Where(choice => choice.Selected).Select(choice => choice.Column.Name).ToArray();
        return selected.Length == 0 ? throw new InvalidOperationException("Select at least one physical DBC column.") : selected;
    }

    private static IReadOnlyList<uint>? ParseKeys(string? text)
    {
        var tokens = (text ?? string.Empty).Split([',', ';', '\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return null;
        var result = new List<uint>();
        foreach (var token in tokens)
        {
            var pair = token.Split('-', 2, StringSplitOptions.TrimEntries);
            if (!uint.TryParse(pair[0], out var start) || pair.Length == 2 && !uint.TryParse(pair[1], out _)) throw new FormatException($"Invalid record key or range: {token}");
            var end = pair.Length == 1 ? start : uint.Parse(pair[1]); if (end < start) throw new FormatException($"Record range runs backward: {token}");
            if ((ulong)result.Count + end - start + 1 > 1_000_000) throw new InvalidOperationException("The key filter expands beyond 1,000,000 entries. Use a smaller audit range or leave it blank for streaming all rows.");
            for (var value = start; ; value++) { result.Add(value); if (value == end) break; }
        }
        return result.Distinct().ToArray();
    }

    private DbcRowExportFormat Format() => _format.SelectedIndex switch { 0 => DbcRowExportFormat.Csv, 2 => DbcRowExportFormat.Json, _ => DbcRowExportFormat.JsonLines };
    private void SetColumns(bool selected) { foreach (var choice in _choices) choice.Selected = selected; RefreshColumns(); }
    private void RefreshColumns()
    {
        var query = _columnSearch.Text?.Trim() ?? string.Empty;
        _columns.ItemsSource = _choices.Where(choice => query.Length == 0 || choice.Column.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || choice.Column.Type.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    private CancellationTokenSource Begin(string text) { _operation?.Cancel(); var operation = new CancellationTokenSource(); _operation = operation; _status.Text = text; return operation; }
    private void End(CancellationTokenSource operation) { if (ReferenceEquals(_operation, operation)) _operation = null; operation.Dispose(); }
    private IStorageProvider Storage() => TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("The DBC export workspace is not attached to the main window.");
    private static Button Accent(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static TextBlock Status(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") };
    private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
    private static T WithColumn<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }

    public void Dispose() { _operation?.Cancel(); _operation?.Dispose(); }
}
