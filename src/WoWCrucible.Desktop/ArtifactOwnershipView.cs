using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class ArtifactOwnershipView : UserControl
{
    private readonly TextBox _project = new() { PlaceholderText = "Crucible project folder…" };
    private readonly TextBlock _summary = new() { Text = "Open a project to inspect only the artifacts Crucible explicitly owns.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#99A5B8") };
    private readonly ListBox _entries = new();
    private ArtifactCleanupPlan? _plan;

    public ArtifactOwnershipView(DesktopWorkspaceSession session)
    {
        _project.Text = session.Settings.ActiveProjectPath;
        _project.TextChanged += (_, _) => { _plan = null; _entries.ItemsSource = null; };
        _entries.ItemTemplate = new FuncDataTemplate<ArtifactCleanupEntry>((entry, _) => entry is null ? new TextBlock() : new StackPanel
        {
            Spacing = 2, Margin = new Thickness(5, 4), Children =
            {
                new TextBlock { Text = $"{entry.Category} · {FormatBytes(entry.Bytes)}", FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = entry.RelativePath, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = $"operation {entry.OperationId} · SHA-256 {entry.Sha256}", Foreground = Brush.Parse("#8793A7"), FontSize = 10, TextWrapping = TextWrapping.Wrap }
            }
        });
        var browse = new Button { Content = "Browse…" }; browse.Click += async (_, _) => await BrowseAsync();
        var inspect = new Button { Content = "Inspect ownership" }; inspect.Click += (_, _) => Inspect();
        var preview = Accent("Preview cleanup"); preview.Click += async (_, _) => await PreviewAsync();
        var apply = Accent("Apply exact preview"); apply.Click += async (_, _) => await ApplyAsync();
        Content = new Grid
        {
            RowDefinitions = new("Auto,Auto,*"), RowSpacing = 9, Margin = new Thickness(12), Children =
            {
                new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "ARTIFACT OWNERSHIP & SAFE CLEANUP", FontSize = 17, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = "Cleanup never infers ownership from names and never uses wildcards. Only manifest-owned cache, scratch, and expired diagnostics can appear here; deliverables, receipts, and preimage backups are protected.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8793A7"), FontSize = 11 }, Row(_project, browse), new WrapPanel { Children = { inspect, preview, apply } } } },
                AtRow(_summary, 1), AtRow(_entries, 2)
            }
        };
    }

    public void SetProject(string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return;
        var full = Path.GetFullPath(projectRoot);
        if (!_project.Text?.Equals(full, StringComparison.OrdinalIgnoreCase) ?? true) _project.Text = full;
    }

    private void Inspect()
    {
        try { var manifest = ArtifactOwnershipService.Load(Root()); _summary.Text = $"Project {manifest.ProjectId} · {manifest.Artifacts.Count:N0} owned artifact(s) · {FormatBytes(manifest.Artifacts.Sum(item => item.Bytes))}. Cleanup has not been previewed."; }
        catch (Exception exception) { Fail("Ownership inspection failed", exception); }
    }
    private async Task PreviewAsync()
    {
        try { _plan = await Task.Run(() => ArtifactOwnershipService.PlanCleanup(Root())); _entries.ItemsSource = _plan.Entries; _summary.Text = $"Preview only · {_plan.Entries.Count:N0} exact file(s) · {FormatBytes(_plan.ReclaimableBytes)} reclaimable. Nothing has been deleted."; }
        catch (Exception exception) { Fail("Cleanup preview failed", exception); }
    }
    private async Task ApplyAsync()
    {
        try
        {
            if (_plan is null) throw new InvalidOperationException("Preview cleanup first. Apply uses that exact immutable path/hash plan.");
            var result = await Task.Run(() => ArtifactOwnershipService.ApplyCleanup(_plan)); _plan = null; _entries.ItemsSource = null;
            _summary.Text = $"Removed {result.RemovedFiles:N0} exact manifest-owned file(s) and reclaimed {FormatBytes(result.ReclaimedBytes)}. Protected artifacts were untouched.";
        }
        catch (Exception exception) { Fail("Cleanup apply failed", exception); }
    }
    private async Task BrowseAsync() { var folders = await Storage().OpenFolderPickerAsync(new() { Title = "Choose a Crucible project", AllowMultiple = false }); if (folders.Count > 0) _project.Text = folders[0].TryGetLocalPath(); }
    private string Root() => string.IsNullOrWhiteSpace(_project.Text) ? throw new InvalidOperationException("Choose a Crucible project first.") : Path.GetFullPath(_project.Text);
    private void Fail(string action, Exception exception) { DesktopCrashLogger.Log(action, exception); _summary.Text = $"ERROR · {exception.Message}"; }
    private IStorageProvider Storage() => TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("The workspace is not attached to a desktop window.");
    private static Button Accent(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static Grid Row(params Control[] controls) { var grid = new Grid { ColumnDefinitions = new(string.Join(',', controls.Select((_, index) => index == 0 ? "*" : "Auto"))), ColumnSpacing = 7 }; for (var index = 0; index < controls.Length; index++) { Grid.SetColumn(controls[index], index); grid.Children.Add(controls[index]); } return grid; }
    private static T AtRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.##} GiB" : bytes >= 1024L * 1024 ? $"{bytes / (1024d * 1024):0.##} MiB" : bytes >= 1024 ? $"{bytes / 1024d:0.#} KiB" : $"{bytes:N0} B";
}
