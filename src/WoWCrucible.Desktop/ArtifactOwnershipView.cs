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
    private readonly Button _browse = new() { Content = "Browse…" };
    private readonly Button _inspect = new() { Content = "Inspect ownership" };
    private readonly Button _preview = Accent("Preview cleanup");
    private readonly Button _apply = Accent("Apply exact preview");
    private ArtifactCleanupPlan? _plan;
    private bool _busy;
    private int _revision;

    public ArtifactOwnershipView(DesktopWorkspaceSession session)
    {
        _project.Text = session.Settings.ActiveProjectPath;
        _project.TextChanged += (_, _) =>
        {
            InvalidatePreview();
            _summary.Text = "Project changed. No cleanup preview is active.";
        };
        _entries.ItemTemplate = new FuncDataTemplate<ArtifactCleanupEntry>((entry, _) => entry is null ? new TextBlock() : new StackPanel
        {
            Spacing = 2, Margin = new Thickness(5, 4), Children =
            {
                new TextBlock { Text = $"{entry.Category} · {FormatBytes(entry.Bytes)}", FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = entry.RelativePath, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = $"operation {entry.OperationId} · SHA-256 {entry.Sha256}", Foreground = Brush.Parse("#8793A7"), FontSize = 10, TextWrapping = TextWrapping.Wrap }
            }
        });
        _browse.Click += async (_, _) => await BrowseAsync();
        _inspect.Click += (_, _) => Inspect();
        _preview.Click += async (_, _) => await PreviewAsync();
        _apply.Click += async (_, _) => await ApplyAsync();
        SetBusy(false);
        Content = new Grid
        {
            RowDefinitions = new("Auto,Auto,*"), RowSpacing = 9, Margin = new Thickness(12), Children =
            {
                new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "ARTIFACT OWNERSHIP & SAFE CLEANUP", FontSize = 17, FontWeight = FontWeight.SemiBold }, Row(_project, _browse), new WrapPanel { Children = { _inspect, _preview, _apply } } } },
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
        if (_busy) return;
        InvalidatePreview();
        try { var manifest = ArtifactOwnershipService.Load(Root()); _summary.Text = $"Project {manifest.ProjectId} · {manifest.Artifacts.Count:N0} owned artifact(s) · {FormatBytes(manifest.Artifacts.Sum(item => item.Bytes))}. Cleanup has not been previewed."; }
        catch (Exception exception) { Fail("Ownership inspection failed", exception); }
    }
    private async Task PreviewAsync()
    {
        if (_busy) return;
        InvalidatePreview();
        var revision = _revision;
        SetBusy(true);
        try
        {
            var root = Root();
            _summary.Text = $"Checking ownership and file hashes: {root}";
            var plan = await Task.Run(() => ArtifactOwnershipService.PlanCleanup(root));
            if (revision != _revision) return;
            _plan = plan;
            _entries.ItemsSource = plan.Entries;
            _summary.Text = $"Preview only · {plan.Entries.Count:N0} exact file(s) · {FormatBytes(plan.ReclaimableBytes)} eligible file bytes.\nProject: {plan.ProjectRoot}";
        }
        catch (Exception exception) { Fail("Cleanup preview failed", exception, revision); }
        finally { SetBusy(false); }
    }
    private async Task ApplyAsync()
    {
        if (_busy || _plan is not { Entries.Count: > 0 } plan) return;
        InvalidatePreview();
        var revision = _revision;
        SetBusy(true);
        try
        {
            if (!Path.TrimEndingDirectorySeparator(Root()).Equals(Path.TrimEndingDirectorySeparator(plan.ProjectRoot), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The project changed. Preview cleanup again.");
            _summary.Text = $"Revalidating cleanup: {plan.ProjectRoot}";
            var result = await Task.Run(() => ArtifactOwnershipService.ApplyCleanup(plan));
            if (revision == _revision) _summary.Text = $"Removed {result.RemovedFiles:N0} exact manifest-owned file(s) · {FormatBytes(result.ReclaimedBytes)} file bytes. Protected artifacts were untouched.\nProject: {plan.ProjectRoot}";
        }
        catch (Exception exception) { Fail("Cleanup apply failed", exception, revision); }
        finally { SetBusy(false); }
    }
    private void InvalidatePreview()
    {
        _revision++;
        _plan = null;
        _entries.ItemsSource = null;
        _apply.IsEnabled = false;
    }
    private void SetBusy(bool busy)
    {
        _busy = busy;
        _project.IsEnabled = _browse.IsEnabled = _inspect.IsEnabled = _preview.IsEnabled = !busy;
        _apply.IsEnabled = !busy && _plan is { Entries.Count: > 0 };
    }
    private async Task BrowseAsync() { var folders = await Storage().OpenFolderPickerAsync(new() { Title = "Choose a Crucible project", AllowMultiple = false }); if (folders.Count > 0) _project.Text = folders[0].TryGetLocalPath(); }
    private string Root() => string.IsNullOrWhiteSpace(_project.Text) ? throw new InvalidOperationException("Choose a Crucible project first.") : Path.GetFullPath(_project.Text);
    private void Fail(string action, Exception exception, int? revision = null) { DesktopCrashLogger.Log(action, exception); if (revision is null || revision == _revision) _summary.Text = $"ERROR · {exception.Message}"; }
    private IStorageProvider Storage() => TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("The workspace is not attached to a desktop window.");
    private static Button Accent(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static Grid Row(params Control[] controls) { var grid = new Grid { ColumnDefinitions = new(string.Join(',', controls.Select((_, index) => index == 0 ? "*" : "Auto"))), ColumnSpacing = 7 }; for (var index = 0; index < controls.Length; index++) { Grid.SetColumn(controls[index], index); grid.Children.Add(controls[index]); } return grid; }
    private static T AtRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.##} GiB" : bytes >= 1024L * 1024 ? $"{bytes / (1024d * 1024):0.##} MiB" : bytes >= 1024 ? $"{bytes / 1024d:0.#} KiB" : $"{bytes:N0} B";
}
