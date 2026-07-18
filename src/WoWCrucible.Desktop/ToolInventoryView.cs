using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class ToolInventoryView : UserControl
{
    private readonly TextBox _workspace = new() { Text = ToolConsolidationInventoryService.FindWorkspaceRoot(CruciblePaths.ApplicationDirectory), PlaceholderText = "Shared wow-edits workspace root" };
    private readonly TextBox _search = new() { PlaceholderText = "Search tool path, capability, or Crucible destination…" };
    private readonly ComboBox _statusFilter = new() { ItemsSource = new[] { "All states", "New unassigned", "Tracked", "Missing" }, SelectedIndex = 0 };
    private readonly ListBox _entries = new();
    private readonly TextBlock _summary = Status("Scan the local corpus to verify every tool root has an assigned Crucible replacement.");
    private readonly TextBlock _selection = Status("Select an entry to inspect its exact source path and replacement destination.");
    private ToolInventoryReport? _report;
    private int _loadRequest;

    public event EventHandler? BackRequested;

    public ToolInventoryView()
    {
        var back = new Button { Content = "← Editor" }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var browse = new Button { Content = "Workspace…" }; browse.Click += async (_, _) => await ChooseWorkspaceAsync();
        var refresh = new Button { Content = "Rescan" }; refresh.Click += async (_, _) => await LoadAsync();
        var reveal = new Button { Content = "Reveal selected" }; reveal.Click += (_, _) => RevealSelected();
        var heading = new WrapPanel { Children = { back, new TextBlock { Text = "TOOL CONSOLIDATION INVENTORY", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) } } };
        var path = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { _workspace, WithColumn(browse, 1), WithColumn(refresh, 2) } };
        var filters = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { _search, WithColumn(_statusFilter, 1), WithColumn(reveal, 2) } };
        _entries.ItemTemplate = new FuncDataTemplate<ToolInventoryEntry>((entry, _) => entry is null ? new Grid() : BuildEntry(entry));
        _entries.SelectionChanged += (_, _) => ShowSelection(); _search.TextChanged += (_, _) => Filter(); _statusFilter.SelectionChanged += (_, _) => Filter();
        Content = new Grid
        {
            RowDefinitions = new("Auto,Auto,Auto,Auto,*,Auto"), Margin = new Thickness(12), RowSpacing = 8, Children =
            {
                heading, WithRow(path, 1), WithRow(filters, 2), WithRow(_summary, 3), WithRow(new Border { BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), Child = _entries }, 4), WithRow(_selection, 5)
            }
        };
    }

    public Task ActivateAsync() => LoadAsync();

    private async Task ChooseWorkspaceAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider; if (storage is null) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select the shared WoW editing workspace", AllowMultiple = false }); var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return; _workspace.Text = path; await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var request = ++_loadRequest;
        try
        {
            var root = _workspace.Text ?? string.Empty; _summary.Text = "Scanning workspace and assigned tool groups…"; _entries.ItemsSource = null; DesktopCrashLogger.Debug("TOOLS", "inventory-start", ("workspace", root));
            var report = await Task.Run(() => ToolConsolidationInventoryService.Scan(root)); if (request != _loadRequest) return; _report = report; Filter();
            _summary.Text = $"{_report.Tracked:N0} tracked · {_report.Unassigned:N0} NEW UNASSIGNED · {_report.Missing:N0} expected roots absent. New unassigned entries are always sorted first and must be audited before corpus coverage is claimed.";
            DesktopCrashLogger.Debug("TOOLS", "inventory-success", ("workspace", report.WorkspaceRoot), ("tracked", report.Tracked), ("unassigned", report.Unassigned), ("missing", report.Missing));
        }
        catch (Exception exception) { if (request != _loadRequest) return; _report = null; _summary.Text = $"Inventory failed: {exception.Message}"; _selection.Text = "No inventory is loaded."; DesktopCrashLogger.Log("Tool inventory failed", exception); }
    }

    private void Filter()
    {
        if (_report is null) return; var query = (_search.Text ?? string.Empty).Trim(); var selectedStatus = _statusFilter.SelectedIndex switch { 1 => ToolInventoryStatus.Unassigned, 2 => ToolInventoryStatus.Tracked, 3 => ToolInventoryStatus.Missing, _ => (ToolInventoryStatus?)null };
        _entries.ItemsSource = _report.Entries.Where(entry => (selectedStatus is null || entry.Status == selectedStatus) && (query.Length == 0 || entry.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase) || entry.Capability.Contains(query, StringComparison.OrdinalIgnoreCase) || entry.CrucibleDestination.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    private void ShowSelection()
    {
        if (_entries.SelectedItem is not ToolInventoryEntry entry) { _selection.Text = "Select an entry to inspect its exact source path and replacement destination."; return; }
        _selection.Text = $"{entry.Status} · {entry.FullPath}\nCapability: {entry.Capability}\nCrucible destination: {entry.CrucibleDestination}";
    }

    private void RevealSelected()
    {
        if (_entries.SelectedItem is not ToolInventoryEntry { Exists: true } entry) { _selection.Text = "Choose an existing tracked or unassigned directory to reveal it."; return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{entry.FullPath}\"") { UseShellExecute = true }); }
        catch (Exception exception) { _selection.Text = $"Could not reveal {entry.FullPath}: {exception.Message}"; }
    }

    private static Control BuildEntry(ToolInventoryEntry entry)
    {
        var color = entry.Status switch { ToolInventoryStatus.Unassigned => "#E98472", ToolInventoryStatus.Missing => "#D2A95F", _ => "#79D7A8" };
        return new Border
        {
            BorderBrush = Brush.Parse("#273044"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(8, 6), Child = new StackPanel
            {
                Spacing = 3, Children =
                {
                    new WrapPanel { Children = { new TextBlock { Text = entry.Status == ToolInventoryStatus.Unassigned ? "NEW UNASSIGNED" : entry.Status.ToString().ToUpperInvariant(), Foreground = Brush.Parse(color), FontWeight = FontWeight.Bold, FontSize = 10, Margin = new Thickness(0,0,8,0) }, new TextBlock { Text = entry.RelativePath, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }, new TextBlock { Text = $" · {entry.Scope}", Foreground = Brush.Parse("#7F8A9F") } } },
                    new TextBlock { Text = entry.Capability, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#AAB3C2") },
                    new TextBlock { Text = $"→ {entry.CrucibleDestination}", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8793A7") }
                }
            }
        };
    }

    private static TextBlock Status(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#99A5B8") };
    private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
    private static T WithColumn<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
}
