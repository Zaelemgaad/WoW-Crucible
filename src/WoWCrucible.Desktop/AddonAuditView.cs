using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class AddonAuditView : UserControl
{
    private readonly TextBox _root = new() { PlaceholderText = "Addon collection or client folder" };
    private readonly TextBox _exclude = new() { PlaceholderText = "Excluded folder (optional)" };
    private readonly TextBox _search = new() { PlaceholderText = "Search addons, versions, paths or issues" };
    private readonly ComboBox _filter = new() { ItemsSource = new[] { "All addons", "Load errors", "Exact duplicates", "Interface differs" }, SelectedIndex = 0 };
    private readonly NumericUpDown _interface = new() { Minimum = 1, Maximum = 999999, Value = 30300, FormatString = "0", Width = 110 };
    private readonly ListBox _packages = new();
    private readonly TextBox _details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private AddonAuditReport? _report;
    private HashSet<string> _duplicates = [];
    private CancellationTokenSource? _scan;
    public event EventHandler? BackRequested;

    public AddonAuditView()
    {
        var back = ToolButton("\uE72B", "Back"); back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var browse = ToolButton("\uE8B7", "Open folder"); browse.Click += async (_, _) => await BrowseAsync();
        var scan = ToolButton("\uE721", "Scan addons"); scan.Click += async (_, _) => await ScanAsync();
        var cancel = ToolButton("\uE711", "Cancel scan"); cancel.Click += (_, _) => _scan?.Cancel();
        var export = ToolButton("\uE74E", "Export report"); export.Click += async (_, _) => await ExportAsync();
        var reveal = ToolButton("\uE8A7", "Open selected folder"); reveal.Click += (_, _) => Reveal();
        _packages.ItemTemplate = new FuncDataTemplate<AddonPackage>((package, _) => package is null ? new Grid() : new StackPanel
        {
            Margin = new Thickness(4), Spacing = 3, Children =
            {
                new TextBlock { Text = $"{package.Name}    {package.Version}    [{package.Interface}]", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = $"{package.Errors} load errors  |  {package.Files.Count} files  |  {package.Directory}", TextWrapping = TextWrapping.Wrap, FontSize = 12 }
            }
        });
        _packages.SelectionChanged += (_, _) => ShowSelection();
        _search.TextChanged += (_, _) => Filter(); _filter.SelectionChanged += (_, _) => Filter();
        var body = new Grid { ColumnDefinitions = new("3*,6,2*"), Children = { _packages, Cell(new GridSplitter { ResizeDirection = GridResizeDirection.Columns, HorizontalAlignment = HorizontalAlignment.Stretch }, 0, 1), Cell(_details, 0, 2) } };
        Content = new Grid
        {
            Margin = new Thickness(12), RowSpacing = 8, RowDefinitions = new("Auto,Auto,Auto,Auto,*,Auto"), Children =
            {
                new WrapPanel { Children = { back, new TextBlock { Text = "Addons", FontSize = 20, FontWeight = FontWeight.SemiBold, Margin = new Thickness(12,0), VerticalAlignment = VerticalAlignment.Center }, browse, scan, cancel, export, reveal } },
                Cell(new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, Children = { _root, Cell(_interface, 0, 1) } }, 1),
                Cell(_exclude, 2),
                Cell(new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, Children = { _search, Cell(_filter, 0, 1) } }, 3),
                Cell(body, 4), Cell(_status, 5)
            }
        };
        ToolTip.SetTip(_interface, "Target client interface number");
        DetachedFromVisualTree += (_, _) => _scan?.Cancel();
    }

    private async Task BrowseAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider; if (storage is null) return;
        var folder = (await storage.OpenFolderPickerAsync(new() { Title = "Open addon collection", AllowMultiple = false })).FirstOrDefault()?.TryGetLocalPath();
        if (folder is null) return; _root.Text = folder; await ScanAsync();
    }

    private async Task ScanAsync()
    {
        _scan?.Cancel(); var request = new CancellationTokenSource(); _scan = request;
        _report = null; _packages.ItemsSource = null; _details.Text = ""; _status.Text = "Scanning...";
        try
        {
            var root = _root.Text ?? ""; var target = (int)(_interface.Value ?? 30300);
            var excluded = string.IsNullOrWhiteSpace(_exclude.Text) ? Array.Empty<string>() : new[] { _exclude.Text! };
            var report = await Task.Run(() => AddonAuditService.Scan(root, target, excluded, request.Token), request.Token);
            if (_scan != request) return;
            _report = report; _duplicates = report.Packages.Where(package => package.ContentSha256.Length > 0).GroupBy(package => package.ContentSha256).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
            Filter();
            _status.Text = $"{report.Packages.Count} addons; {report.Packages.Sum(package => package.Errors)} load errors; {_duplicates.Count} duplicate groups; {report.DiscoveryIssues.Count} discovery notices. Static audit; in-game testing still required.";
            _details.Text = string.Join(Environment.NewLine, report.DiscoveryIssues.Select(issue => $"{issue.Severity}: {issue.Path}\n{issue.Message}"));
        }
        catch (OperationCanceledException) { if (_scan == request) _status.Text = "Scan canceled."; }
        catch (Exception error) { if (_scan == request) _status.Text = $"Scan failed: {error.Message}"; DesktopCrashLogger.Log("Addon audit failed", error); }
        finally { if (_scan == request) _scan = null; request.Dispose(); }
    }

    private void Filter()
    {
        if (_report is null) return; var query = _search.Text ?? "";
        var selected = _packages.SelectedItem as AddonPackage;
        var filtered = _report.Packages.Where(package => (_filter.SelectedIndex switch
        {
            1 => package.Errors > 0, 2 => _duplicates.Contains(package.ContentSha256),
            3 => package.Issues.Any(issue => issue.Code == "InterfaceTarget"), _ => true
        }) && $"{package.Name} {package.Version} {package.Directory} {string.Join(' ', package.Issues.Select(issue => issue.Message))}".Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        _packages.ItemsSource = filtered;
        _packages.SelectedItem = selected is not null && filtered.Contains(selected) ? selected : null;
        ShowSelection();
    }

    private void ShowSelection()
    {
        if (_packages.SelectedItem is not AddonPackage package) { _details.Text = ""; return; }
        _details.Text = $"{package.Name}\n{package.Directory}\n\nVersion: {package.Version}\nInterface: {package.Interface}\nSHA256: {package.ContentSha256}\n\nDependencies: {string.Join(", ", package.Dependencies)}\nAccount variables: {string.Join(", ", package.SavedVariables)}\nCharacter variables: {string.Join(", ", package.CharacterVariables)}\n\n"
            + string.Join("\n\n", package.Issues.Select(issue => $"{issue.Severity} - {issue.Code}\n{issue.Path}\n{issue.Message}"));
    }

    private async Task ExportAsync()
    {
        var report = _report; var storage = TopLevel.GetTopLevel(this)?.StorageProvider; if (report is null || storage is null) return;
        var file = await storage.SaveFilePickerAsync(new() { Title = "Export addon audit", SuggestedFileName = "addon-audit.json", DefaultExtension = "json" });
        if (file is null) return;
        try { await using var stream = await file.OpenWriteAsync(); stream.SetLength(0); await JsonSerializer.SerializeAsync(stream, report, new JsonSerializerOptions { WriteIndented = true }); }
        catch (Exception error) { _status.Text = $"Export failed: {error.Message}"; }
    }

    private void Reveal()
    {
        if (_packages.SelectedItem is not AddonPackage package) return;
        try { Process.Start(new ProcessStartInfo(package.Directory) { UseShellExecute = true }); }
        catch (Exception error) { _status.Text = $"Open folder failed: {error.Message}"; }
    }

    private static T Cell<T>(T control, int row, int column = 0) where T : Control { Grid.SetRow(control, row); Grid.SetColumn(control, column); return control; }

    private static Button ToolButton(string glyph, string label)
    {
        var button = new Button { Width = 34, Height = 32, Margin = new Thickness(2, 0), Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16 } };
        ToolTip.SetTip(button, label);
        Avalonia.Automation.AutomationProperties.SetName(button, label);
        return button;
    }
}
