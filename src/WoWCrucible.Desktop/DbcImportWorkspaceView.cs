using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class DbcImportWorkspaceView : UserControl, IDisposable
{
    private readonly DbcDocumentSession _document;
    private readonly TextBox _input = new() { PlaceholderText = "CSV, JSON Lines, or JSON array exported by Crucible…" };
    private readonly ComboBox _format = new() { ItemsSource = new[] { "Auto from extension", "CSV", "JSON Lines", "JSON array" }, SelectedIndex = 0 };
    private readonly CheckBox _append = new() { Content = "Allow missing keys to append" };
    private readonly CheckBox _rawStrings = new() { Content = "Values are raw string-table offsets" };
    private readonly TextBox _preview = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono,Consolas") };
    private readonly TextBlock _status = Status("Select a structured row file, then build a non-mutating preview.");
    private readonly Border _confirmation = new() { IsVisible = false, BorderBrush = Brush.Parse("#6E5426"), BorderThickness = new Thickness(1), Padding = new Thickness(10) };
    private CancellationTokenSource? _operation;
    private DbcRowImportPlan? _plan;

    public event EventHandler? BackRequested;
    public event EventHandler<DbcRowImportApplyResult>? Applied;

    public DbcImportWorkspaceView(DbcDocumentSession document)
    {
        _document = document;
        var back = new Button { Content = "← Editor" }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var browse = new Button { Content = "Browse…" }; browse.Click += async (_, _) => await PickInputAsync();
        var preview = Accent("Build import preview"); preview.Click += async (_, _) => await PreviewAsync();
        var review = new Button { Content = "Review apply to open DBC" }; review.Click += (_, _) => ReviewApply();
        var cancel = new Button { Content = "Cancel operation" }; cancel.Click += (_, _) => _operation?.Cancel();
        var header = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10, Margin = new Thickness(12, 8), Children = { back, WithColumn(new StackPanel { Children = { new TextBlock { Text = $"IMPORT ROWS · {Path.GetFileName(document.File.SourcePath)}", FontSize = 18, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = "Preview-first keyed updates; appends are explicit; no row is ever deleted.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") } } }, 1) } };
        var fileRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 7, Children = { _input, WithColumn(browse, 1) } };
        var controls = new WrapPanel { Children = { _format, _append, _rawStrings, preview, review, cancel } };
        var settings = new StackPanel { Spacing = 8, Margin = new Thickness(12, 8), Children = { fileRow, controls,
            new TextBlock { Text = "CSV blank numeric cells and JSON nulls mean ‘leave unchanged’; a blank decoded string intentionally clears that string. Unknown columns, duplicate targets, key changes, non-contiguous virtual appends, and stale previews are blocked. Applying is one structural batch, clears cell-level undo history, marks the open DBC dirty, and still requires the normal Save or Save As action.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8995A9") } } };
        var previewBorder = new Border { BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), Margin = new Thickness(12, 0), Child = _preview };
        Content = new Grid { RowDefinitions = new("Auto,Auto,*,Auto,Auto"), Children = { new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 0, 0, 1), Child = header }, WithRow(settings, 1), WithRow(previewBorder, 2), WithRow(_confirmation, 3), WithRow(new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 6), Child = _status }, 4) } };
        _input.TextChanged += (_, _) => InvalidatePlan(); _format.SelectionChanged += (_, _) => InvalidatePlan(); _append.IsCheckedChanged += (_, _) => InvalidatePlan(); _rawStrings.IsCheckedChanged += (_, _) => InvalidatePlan();
    }

    private async Task PickInputAsync()
    {
        var files = await Storage().OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Select structured DBC rows", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Structured DBC rows") { Patterns = ["*.csv", "*.jsonl", "*.ndjson", "*.json"] }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) _input.Text = path;
    }

    private async Task PreviewAsync()
    {
        if (string.IsNullOrWhiteSpace(_input.Text)) { await PickInputAsync(); if (string.IsNullOrWhiteSpace(_input.Text)) return; }
        var operation = Begin("Validating every input row on an isolated in-memory copy…");
        try
        {
            var path = Path.GetFullPath(_input.Text.Trim()); var options = new DbcRowImportOptions(Format(path), _append.IsChecked == true, _rawStrings.IsChecked == true);
            var progress = new Progress<(int Done, int Total)>(value => _status.Text = $"Previewing {value.Done:N0}/{value.Total:N0} input row(s)…");
            var plan = await Task.Run(() => DbcRowImportService.Preview(_document.File, _document.Schema, path, options, progress, operation.Token), operation.Token);
            operation.Token.ThrowIfCancellationRequested(); _plan = plan; _preview.Text = Render(plan);
            _status.Text = $"Preview ready · {plan.InputRows:N0} input row(s) · {plan.UpdatedRows:N0} existing row(s) changed · {plan.AppendedRows:N0} append(s) · {plan.ChangedCells:N0} cell change(s). The open DBC is still untouched.";
            DesktopCrashLogger.Debug("DBC", "row-import-preview", ("source", _document.File.SourcePath), ("input", plan.InputPath), ("rows", plan.InputRows), ("updated", plan.UpdatedRows), ("appended", plan.AppendedRows), ("cells", plan.ChangedCells));
        }
        catch (OperationCanceledException) { _status.Text = "Import preview cancelled; the open DBC was untouched."; }
        catch (Exception exception) { _status.Text = $"Import preview failed safely: {exception.Message}"; DesktopCrashLogger.Log("DBC import preview failed", exception); }
        finally { End(operation); }
    }

    private void ReviewApply()
    {
        if (_plan is null) { _status.Text = "Build a current preview before applying. Any path or option change invalidates the previous plan."; return; }
        var cancel = new Button { Content = "Keep preview only" }; cancel.Click += (_, _) => _confirmation.IsVisible = false;
        var apply = Accent("Apply exact preview to open DBC"); apply.Click += (_, _) => ApplyPlan();
        _confirmation.Child = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = $"Apply {_plan.ChangedCells:N0} cell change(s) and {_plan.AppendedRows:N0} append(s) to the staged {Path.GetFileName(_document.File.SourcePath)} document? No disk file changes until Save/Save As. This structural batch clears prior cell undo history.", TextWrapping = TextWrapping.Wrap }, WithColumn(cancel, 1), WithColumn(apply, 2) } };
        _confirmation.IsVisible = true;
    }

    private void ApplyPlan()
    {
        if (_plan is null) return;
        try
        {
            var result = DbcRowImportService.Apply(_document.File, _plan); _document.History.Clear(); _confirmation.IsVisible = false;
            Applied?.Invoke(this, result); _status.Text = result.ChangedCells == 0 && result.AppendedRows == 0
                ? "The preview was a no-op; the open DBC was not dirtied."
                : $"Applied {result.ChangedCells:N0} cell change(s) and {result.AppendedRows:N0} append(s) in memory. Use ← Editor, review the table, then Save/Save As when ready.";
            DesktopCrashLogger.Debug("DBC", "row-import-applied", ("source", _document.File.SourcePath), ("updated", result.UpdatedRows), ("appended", result.AppendedRows), ("cells", result.ChangedCells), ("rows", result.ResultRows));
            _plan = null;
        }
        catch (Exception exception) { _status.Text = $"Apply refused: {exception.Message}"; DesktopCrashLogger.Log("DBC import apply failed", exception); }
    }

    private static string Render(DbcRowImportPlan plan)
    {
        var text = new StringBuilder(); text.AppendLine($"Input       {plan.InputPath}"); text.AppendLine($"SHA-256     {plan.InputSha256}"); text.AppendLine($"Format      {plan.Format}");
        text.AppendLine($"Rows        {plan.InputRows:N0} input · {plan.UpdatedRows:N0} updated · {plan.AppendedRows:N0} appended"); text.AppendLine($"Cells       {plan.ChangedCells:N0}");
        foreach (var warning in plan.Warnings) text.AppendLine($"WARNING     {warning}"); text.AppendLine();
        foreach (var change in plan.Changes.Take(500)) text.AppendLine($"input {change.InputRow,6} · key {change.RecordKey?.ToString() ?? "-",10} · row {change.TargetRow,6} · {change.Column} · {change.Before}  =>  {change.After}");
        if (plan.Changes.Count > 500) text.AppendLine($"… {plan.Changes.Count - 500:N0} more cell change(s); the complete plan remains staged and will be applied, not discarded.");
        return text.ToString();
    }

    private DbcRowImportFormat Format(string path) => _format.SelectedIndex switch { 1 => DbcRowImportFormat.Csv, 2 => DbcRowImportFormat.JsonLines, 3 => DbcRowImportFormat.Json, _ => DbcRowImportService.InferFormat(path) };
    private void InvalidatePlan() { if (_plan is null) return; _plan = null; _confirmation.IsVisible = false; _status.Text = "Import path or options changed; build a new preview before applying."; }
    private CancellationTokenSource Begin(string text) { _operation?.Cancel(); _operation?.Dispose(); var operation = new CancellationTokenSource(); _operation = operation; _status.Text = text; return operation; }
    private void End(CancellationTokenSource operation) { if (ReferenceEquals(_operation, operation)) _operation = null; operation.Dispose(); }
    private IStorageProvider Storage() => TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("The DBC import workspace is not attached to the main window.");
    private static Button Accent(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static TextBlock Status(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") };
    private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
    private static T WithColumn<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
    public void Dispose() { _operation?.Cancel(); _operation?.Dispose(); }
}
