using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class CompatibilityLabView : UserControl
{
    private sealed record ResultRow(string Kind, string Name, string Result, string Detail);

    private readonly DesktopSettings _settings;
    private readonly TextBox _request = new() { PlaceholderText = "Compatibility lab request JSON" };
    private readonly Button _prepare = Accent("Prepare / verify clones");
    private readonly Button _audit = new() { Content = "Run compatibility audit" };
    private readonly Button _cancel = new() { Content = "Cancel", IsEnabled = false };
    private readonly Button _reveal = new() { Content = "Reveal report", IsEnabled = false };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Value = 0, Height = 5 };
    private readonly TextBlock _status = Status("No compatibility request selected.");
    private readonly ListBox _results = new();
    private readonly TextBlock _detail = Status("Run clone preparation or the compatibility audit to populate results.");
    private CancellationTokenSource? _operation;
    private string? _lastReportRoot;

    public event EventHandler? BackRequested;

    public CompatibilityLabView(DesktopSettings settings)
    {
        _settings = settings;
        _request.Text = settings.CompatibilityLabRequestPath;
        _results.ItemTemplate = new FuncDataTemplate<ResultRow>((row, _) => row is null ? new Grid() : BuildRow(row));
        _results.SelectionChanged += (_, _) => ShowSelection();

        var back = new Button { Content = "Back" }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var browse = new Button { Content = "Browse..." }; browse.Click += async (_, _) => await PickRequestAsync();
        _prepare.Click += async (_, _) => await PrepareAsync();
        _audit.Click += async (_, _) => await AuditAsync();
        _cancel.Click += (_, _) => _operation?.Cancel();
        _reveal.Click += (_, _) => RevealReport();

        var heading = new Border
        {
            BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 8),
            Child = new WrapPanel
            {
                Children =
                {
                    back,
                    new TextBlock { Text = "COMPATIBILITY LAB", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) },
                    _prepare, _audit, _cancel, _reveal
                }
            }
        };
        var requestRow = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 8, Margin = new Thickness(12, 10, 12, 6) };
        var requestLabel = new TextBlock { Text = "Request", VerticalAlignment = VerticalAlignment.Center };
        requestRow.Children.Add(requestLabel); Grid.SetColumn(_request, 1); requestRow.Children.Add(_request); Grid.SetColumn(browse, 2); requestRow.Children.Add(browse);
        var operationState = new StackPanel { Spacing = 5, Margin = new Thickness(12, 0, 12, 8), Children = { _progress, _status } };
        var body = new Grid { RowDefinitions = new("*,Auto,*"), RowSpacing = 5, Margin = new Thickness(12, 0, 12, 10) };
        body.Children.Add(_results);
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = Brush.Parse("#2B3445"), Height = 4 };
        Grid.SetRow(splitter, 1); body.Children.Add(splitter);
        var detailScroll = new ScrollViewer { Content = _detail };
        Grid.SetRow(detailScroll, 2); body.Children.Add(detailScroll);
        Content = new Grid { RowDefinitions = new("Auto,Auto,Auto,*"), Children = { heading, WithRow(requestRow, 1), WithRow(operationState, 2), WithRow(body, 3) } };
    }

    private async Task PrepareAsync()
    {
        var operation = Begin("Preparing isolated clone trees...");
        try
        {
            var requestPath = PersistRequestPath();
            var progress = ProgressFor(operation);
            var report = await Task.Run(() => CompatibilityLabService.PrepareClones(CompatibilityLabService.LoadRequest(requestPath), progress, operation.Token), operation.Token);
            if (!ReferenceEquals(_operation, operation)) return;
            _lastReportRoot = report.ReportRoot; _reveal.IsEnabled = true;
            _results.ItemsSource = report.Entries.Select(entry => new ResultRow("CLONE", entry.Name,
                entry.Passed ? entry.State.ToString() : "FAILED",
                $"Source: {entry.SourceRoot}\nClone: {entry.CloneRoot}\nCopied: {entry.CopiedFiles:N0} files ({FormatBytes(entry.CopiedBytes)})\nReused: {entry.ReusedFiles:N0} files ({FormatBytes(entry.ReusedBytes)})\nRemoved stale partial files: {entry.RemovedStaleFiles:N0}\nIdentity audit: {(entry.Audit?.Passed == true ? "PASS" : "FAIL")}\n{string.Join(Environment.NewLine, entry.Errors)}")).ToArray();
            _status.Text = $"Clone preparation {(report.Passed ? "passed" : "failed")} - {report.Entries.Count:N0} pair(s) - {report.ReportRoot}";
            _progress.Value = report.Passed ? 1 : 0;
            DesktopCrashLogger.Debug("COMPAT", "clone-preparation-complete", ("passed", report.Passed), ("pairs", report.Entries.Count), ("report", report.ReportRoot));
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (ReferenceEquals(_operation, operation)) _status.Text = "Clone preparation cancelled. Its marked partial tree is retained and the same request resumes it.";
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_operation, operation)) { _status.Text = $"Clone preparation failed: {exception.Message}"; _progress.Value = 0; }
            DesktopCrashLogger.Log("Compatibility clone preparation failed", exception);
        }
        finally { End(operation); }
    }

    private async Task AuditAsync()
    {
        var operation = Begin("Running compatibility audit...");
        try
        {
            var requestPath = PersistRequestPath();
            var progress = ProgressFor(operation);
            var report = await Task.Run(() => CompatibilityLabService.Run(CompatibilityLabService.LoadRequest(requestPath), progress, operation.Token), operation.Token);
            if (!ReferenceEquals(_operation, operation)) return;
            _lastReportRoot = report.RunRoot; _reveal.IsEnabled = true;
            var rows = report.CloneAudits.Select(value => new ResultRow("CLONE", value.Name, value.Passed ? "PASS" : "FAIL",
                    $"Source: {value.SourceRoot}\nClone: {value.CloneRoot}\nHashed: {value.HashedPairs:N0}\nMissing: {value.MissingFiles:N0}; extra: {value.ExtraFiles:N0}; length mismatch: {value.LengthMismatches:N0}; SHA-256 mismatch: {value.HashMismatches:N0}"))
                .Concat(report.LaneAudits.Select(value => new ResultRow("LANE", value.Name, value.Passed ? "PASS" : "FAIL",
                    $"Profile: {value.ProfileId}; build {value.Build:N0}; core {value.CoreFamily}\nTables: {value.TableFiles:N0}; schema failures: {value.SchemaAudit.Failures:N0}\nNative deployment: {(value.NativeDeployment.Passed ? "PASS" : "FAIL")}\n{string.Join(Environment.NewLine, value.Findings.Concat(value.Errors))}")))
                .Concat(report.CrossTargetDeployments.Select(value => new ResultRow("CROSS", $"{value.SourceLane} -> {value.TargetLane}", value.Passed ? "PASS" : "FAIL",
                    $"Entries: {value.Entries:N0}; client staged: {value.StagedClientFiles:N0}; server staged: {value.StagedServerFiles:N0}; blocked: {value.BlockedFiles:N0}\n{string.Join(Environment.NewLine, value.Findings)}")))
                .ToArray();
            _results.ItemsSource = rows;
            _status.Text = $"Compatibility audit {(report.Passed ? "passed" : "found blockers")} - {report.RunRoot}";
            _progress.Value = report.Passed ? 1 : 0;
            DesktopCrashLogger.Debug("COMPAT", "lab-complete", ("passed", report.Passed), ("lanes", report.LaneAudits.Count), ("report", report.RunRoot));
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (ReferenceEquals(_operation, operation)) _status.Text = "Compatibility audit cancelled.";
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_operation, operation)) { _status.Text = $"Compatibility audit failed: {exception.Message}"; _progress.Value = 0; }
            DesktopCrashLogger.Log("Compatibility lab failed", exception);
        }
        finally { End(operation); }
    }

    private CancellationTokenSource Begin(string status)
    {
        if (_operation is not null) throw new InvalidOperationException("A compatibility operation is already running.");
        var operation = _operation = new CancellationTokenSource();
        _prepare.IsEnabled = _audit.IsEnabled = false; _cancel.IsEnabled = true; _progress.IsIndeterminate = true; _progress.Value = 0; _status.Text = status;
        return operation;
    }

    private void End(CancellationTokenSource operation)
    {
        if (!ReferenceEquals(_operation, operation)) return;
        _operation = null; _prepare.IsEnabled = _audit.IsEnabled = true; _cancel.IsEnabled = false; _progress.IsIndeterminate = false; operation.Dispose();
    }

    private IProgress<CompatibilityLabProgress> ProgressFor(CancellationTokenSource operation) => new Progress<CompatibilityLabProgress>(value =>
    {
        if (!ReferenceEquals(_operation, operation)) return;
        _progress.IsIndeterminate = value.Total <= 0;
        _progress.Value = value.Total <= 0 ? 0 : Math.Clamp((double)value.Completed / value.Total, 0, 1);
        _status.Text = $"{value.Phase} - {value.Completed:N0}/{value.Total:N0} - {value.CurrentPath}";
    });

    private string PersistRequestPath()
    {
        var path = Path.GetFullPath(_request.Text?.Trim() ?? string.Empty);
        if (!File.Exists(path)) throw new FileNotFoundException("Compatibility request does not exist.", path);
        _settings.CompatibilityLabRequestPath = path; _settings.Save();
        return path;
    }

    private async Task PickRequestAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("The compatibility lab is not attached to the main window.");
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select compatibility lab request", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Compatibility request") { Patterns = ["*.json"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        _request.Text = path; _settings.CompatibilityLabRequestPath = path; _settings.Save();
    }

    private void ShowSelection()
    {
        _detail.Text = _results.SelectedItem is ResultRow row
            ? $"{row.Kind} | {row.Name} | {row.Result}\n\n{row.Detail}"
            : "Select a result for exact paths and counts.";
    }

    private void RevealReport()
    {
        if (_lastReportRoot is null || !Directory.Exists(_lastReportRoot)) { _status.Text = "No report directory is available."; return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe", _lastReportRoot) { UseShellExecute = true }); }
        catch (Exception exception) { _status.Text = $"Could not reveal report: {exception.Message}"; }
    }

    private static Control BuildRow(ResultRow row)
    {
        var color = row.Result is "PASS" or "Created" or "Resumed" or "VerifiedExisting" ? "#79D7A8" : "#E98472";
        var grid = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 10, Margin = new Thickness(7, 5) };
        var kind = new TextBlock { Text = row.Kind, Foreground = Brush.Parse("#8895A9"), FontSize = 10, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = row.Name, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeight.SemiBold };
        var result = new TextBlock { Text = row.Result, Foreground = Brush.Parse(color), FontWeight = FontWeight.Bold };
        grid.Children.Add(kind); Grid.SetColumn(name, 1); grid.Children.Add(name); Grid.SetColumn(result, 2); grid.Children.Add(result); return grid;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"]; var value = (double)bytes; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }

    private static Button Accent(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static TextBlock Status(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") };
    private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
}
