using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class DbcStagingWorkspaceView : UserControl
{
    private readonly DbcDocumentSession _document;
    private readonly DesktopWorkspaceSession _session;
    private readonly TextBox _project = new() { PlaceholderText = "Active Crucible project folder" };
    private readonly TextBox _workspace = new() { IsReadOnly = true, PlaceholderText = "Create or locate the table staging database" };
    private readonly TextBox _sql = new() { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono,Consolas") };
    private readonly TextBox _bindings = new() { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, PlaceholderText = "Optional, one name=value per line", FontFamily = new FontFamily("Cascadia Mono,Consolas") };
    private readonly TextBox _output = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono,Consolas") };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#98A5BA") };
    private readonly CheckBox _replace = new() { Content = "Replace the existing staging database" };
    private readonly CheckBox _confirmMutation = new() { Content = "I reviewed this exact mutation preview", IsVisible = false };
    private readonly CheckBox _confirmPublish = new() { Content = "I reviewed this exact DBC publication preview", IsVisible = false };
    private readonly Button _create = new() { Content = "Create / rebuild" };
    private readonly Button _query = new() { Content = "Run read-only query" };
    private readonly Button _previewMutation = new() { Content = "Preview mutation" };
    private readonly Button _applyMutation = new() { Content = "Apply reviewed mutation", IsEnabled = false };
    private readonly Button _refresh = new() { Content = "Refresh diff" };
    private readonly Button _previewPublish = new() { Content = "Preview into open DBC" };
    private readonly Button _applyPublish = new() { Content = "Apply reviewed changes to open DBC", IsEnabled = false };
    private string? _reviewedMutation;
    private (long Length, DateTime LastWriteUtc)? _reviewedPublication;

    public event EventHandler? BackRequested;
    public event EventHandler? ProjectWorkspaceRequested;
    public event EventHandler<DbcRowImportApplyResult>? AppliedToDocument;

    public DbcStagingWorkspaceView(DbcDocumentSession document, DesktopWorkspaceSession session)
    {
        _document = document; _session = session; _project.Text = session.Settings.ActiveProjectPath;
        ResolveWorkspacePath();
        _sql.Text = $"SELECT {Quote(document.Schema.Columns[0].Name)}\nFROM working\nORDER BY {Quote(document.Schema.Columns[0].Name)}\nLIMIT 100";
        var back = new Button { Content = "← Editor" }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var projects = new Button { Content = "Projects & shared IDs" }; projects.Click += (_, _) => ProjectWorkspaceRequested?.Invoke(this, EventArgs.Empty);
        _create.Click += async (_, _) => await CreateAsync(); _query.Click += async (_, _) => await QueryAsync(); _previewMutation.Click += async (_, _) => await MutateAsync(false);
        _applyMutation.Click += async (_, _) => await MutateAsync(true); _refresh.Click += async (_, _) => await RefreshDiffAsync(); _previewPublish.Click += async (_, _) => await PreviewPublishAsync(); _applyPublish.Click += (_, _) => ApplyPublish();
        _confirmMutation.IsCheckedChanged += (_, _) => _applyMutation.IsEnabled = _confirmMutation.IsChecked == true && _reviewedMutation is not null;
        _confirmPublish.IsCheckedChanged += (_, _) => _applyPublish.IsEnabled = _confirmPublish.IsChecked == true && _reviewedPublication is not null;
        _project.LostFocus += (_, _) => ResolveWorkspacePath();

        var header = new Grid
        {
            ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 12, Margin = new Thickness(14, 10),
            Children =
            {
                back,
                Column(new StackPanel { Spacing = 2, Children = { new TextBlock { Text = $"{Path.GetFileName(document.File.SourcePath)} · PROJECT STAGING DATABASE", FontSize = 19, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = "Bulk named-field editing in project-local SQLite; DBC remains authoritative and publication uses Crucible's stale-safe importer.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8794A9") } } }, 1),
                Column(projects, 2)
            }
        };
        var left = new Grid
        {
            RowDefinitions = new("Auto,Auto,*,Auto,Auto"), RowSpacing = 9, Margin = new Thickness(12),
            Children =
            {
                new StackPanel { Spacing = 5, Children = { Label("Project folder"), _project, Label("Workspace"), _workspace, new WrapPanel { Children = { _create, _replace } } } },
                Row(new TextBlock { Text = "SQL · query SELECT/WITH; mutation UPDATE/INSERT may target only working", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold }, 1),
                Row(_sql, 2),
                Row(new StackPanel { Spacing = 4, Children = { Label("Named bindings"), _bindings } }, 3),
                Row(new WrapPanel { Children = { _query, _previewMutation, _applyMutation, _confirmMutation } }, 4)
            }
        };
        var right = new Grid
        {
            RowDefinitions = new("Auto,*,Auto"), RowSpacing = 9, Margin = new Thickness(12),
            Children =
            {
                new StackPanel { Spacing = 5, Children = { new TextBlock { Text = "RESULT / EXACT DIFF", FontWeight = FontWeight.SemiBold }, new WrapPanel { Children = { _refresh, _previewPublish, _applyPublish, _confirmPublish } } } },
                Row(_output, 1),
                Row(_status, 2)
            }
        };
        var body = new ResponsiveSplitGrid(
            new Border { BorderBrush = Brush.Parse("#293246"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Margin = new Thickness(10, 4, 4, 10), Child = left },
            new Border { BorderBrush = Brush.Parse("#293246"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Margin = new Thickness(4, 4, 10, 10), Child = right },
            1, 1, compactFirstWeight: 1.2, compactSecondWeight: 1);
        Content = new Grid { RowDefinitions = new("Auto,*"), Children = { header, Row(body, 1) } };
        _status.Text = File.Exists(_workspace.Text) ? "Existing staging database found. Refresh its exact diff or run a bounded query." : "Create a Crucible project or select an active project, then create this table's staging database.";
    }

    private async Task CreateAsync()
    {
        var project = _project.Text?.Trim() ?? string.Empty; if (project.Length == 0) { Fail("Select or create a Crucible project first."); ProjectWorkspaceRequested?.Invoke(this, EventArgs.Empty); return; }
        SetBusy(true, "Creating the schema-bound staging database in one transaction…");
        try
        {
            var result = await Task.Run(() => DbcStagingWorkspaceService.Create(project, _document.File, _document.Schema, _replace.IsChecked == true));
            _session.Settings.ActiveProjectPath = Path.GetFullPath(project); _session.Settings.Save(); _workspace.Text = result.WorkspacePath; _replace.IsChecked = false;
            await RefreshDiffAsync(); Succeed($"Staged {result.SourceRows:N0} rows × {result.Fields:N0} named fields. Baseline and imported identities are immutable.");
        }
        catch (Exception exception) { Fail(exception.Message); DesktopCrashLogger.Log("DBC staging create failed", exception); }
        finally { SetBusy(false); }
    }

    private async Task QueryAsync()
    {
        if (!RequireWorkspace(out var path)) return; SetBusy(true, "Running bounded read-only staging query…");
        try { var result = await Task.Run(() => DbcStagingWorkspaceService.Query(path, _sql.Text ?? string.Empty, ParseBindings(), 1000)); _output.Text = RenderQuery(result); Succeed($"Returned {result.Rows.Count:N0} row(s){(result.Truncated ? "; result is truncated at 1,000" : string.Empty)}."); }
        catch (Exception exception) { Fail(exception.Message); DesktopCrashLogger.Log("DBC staging query failed", exception); }
        finally { SetBusy(false); }
    }

    private async Task MutateAsync(bool apply)
    {
        if (!RequireWorkspace(out var path)) return; var signature = MutationSignature();
        if (apply && (_reviewedMutation != signature || _confirmMutation.IsChecked != true)) { Fail("Preview this exact SQL and binding set, then acknowledge that preview before applying it."); return; }
        SetBusy(true, apply ? "Revalidating and applying the reviewed staging mutation…" : "Running the staging mutation inside a rollback-only preview transaction…");
        try
        {
            var result = await Task.Run(() => DbcStagingWorkspaceService.Mutate(path, _sql.Text ?? string.Empty, ParseBindings(), apply)); _output.Text = RenderDiff(result.Diff);
            if (apply) { ResetMutationReview(); Succeed($"Applied the reviewed mutation to working: {result.AffectedRows:N0} affected row(s). The source DBC is still untouched."); }
            else { _reviewedMutation = signature; _confirmMutation.IsVisible = true; _confirmMutation.IsChecked = false; Succeed($"Preview only: {result.AffectedRows:N0} affected row(s). SQLite rolled the statement back; check the exact diff before acknowledgement."); }
        }
        catch (Exception exception) { ResetMutationReview(); Fail(exception.Message); DesktopCrashLogger.Log("DBC staging mutation failed", exception); }
        finally { SetBusy(false); }
    }

    private async Task RefreshDiffAsync()
    {
        if (!RequireWorkspace(out var path)) return; SetBusy(true, "Refreshing touched-row diff…");
        try { var diff = await Task.Run(() => DbcStagingWorkspaceService.Diff(path, 1000)); _output.Text = RenderDiff(diff); ResetPublicationReview(); Succeed($"{diff.UpdatedRows:N0} updated · {diff.AppendedRows:N0} appended · {diff.ChangedCells:N0} changed cells · applyable {diff.CanApply}."); }
        catch (Exception exception) { Fail(exception.Message); DesktopCrashLogger.Log("DBC staging diff failed", exception); }
        finally { SetBusy(false); }
    }

    private async Task PreviewPublishAsync()
    {
        if (!RequireWorkspace(out var path)) return; SetBusy(true, "Building a stale-safe DBC import preview…");
        try
        {
            var plan = await Task.Run(() => DbcStagingWorkspaceService.PreviewApply(path, _document.File, _document.Schema)); var diff = DbcStagingWorkspaceService.Diff(path, 1000); _output.Text = RenderDiff(diff) + $"\n\nIMPORT PREVIEW\nUpdated rows: {plan.UpdatedRows:N0}\nAppended rows: {plan.AppendedRows:N0}\nChanged DBC cells: {plan.ChangedCells:N0}";
            var info = new FileInfo(path); _reviewedPublication = (info.Length, info.LastWriteTimeUtc); _confirmPublish.IsVisible = true; _confirmPublish.IsChecked = false; Succeed("Publication preview built against the current in-memory DBC and exact staging file. No DBC bytes changed.");
        }
        catch (Exception exception) { ResetPublicationReview(); Fail(exception.Message); DesktopCrashLogger.Log("DBC staging publication preview failed", exception); }
        finally { SetBusy(false); }
    }

    private void ApplyPublish()
    {
        if (!RequireWorkspace(out var path) || _reviewedPublication is not { } reviewed || _confirmPublish.IsChecked != true) { Fail("Build and acknowledge an exact publication preview first."); return; }
        var info = new FileInfo(path); if (reviewed != (info.Length, info.LastWriteTimeUtc)) { ResetPublicationReview(); Fail("The staging database changed after preview. Build and review a new publication preview."); return; }
        try
        {
            var plan = DbcStagingWorkspaceService.PreviewApply(path, _document.File, _document.Schema); var result = DbcRowImportService.Apply(_document.File, plan); ResetPublicationReview(); AppliedToDocument?.Invoke(this, result);
            Succeed($"Applied {result.UpdatedRows:N0} updated and {result.AppendedRows:N0} appended row(s) to the open DBC. Review it in the editor, then Save/Save As; the source file has not been written yet.");
        }
        catch (Exception exception) { ResetPublicationReview(); Fail(exception.Message); DesktopCrashLogger.Log("DBC staging publication failed", exception); }
    }

    private void ResolveWorkspacePath()
    {
        try
        {
            var project = _project.Text?.Trim(); if (string.IsNullOrWhiteSpace(project)) { _workspace.Text = string.Empty; return; }
            var projectPath = Directory.Exists(project) ? Path.Combine(Path.GetFullPath(project), "project.crucible.json") : Path.GetFullPath(project); var root = Path.GetDirectoryName(projectPath)!;
            _workspace.Text = Path.Combine(root, "Staging", Path.GetFileNameWithoutExtension(_document.File.SourcePath) + ".crucible.sqlite");
        }
        catch { _workspace.Text = string.Empty; }
    }

    private bool RequireWorkspace(out string path) { path = _workspace.Text?.Trim() ?? string.Empty; if (path.Length != 0 && File.Exists(path)) return true; Fail("Create or select an existing staging database first."); return false; }
    private IReadOnlyDictionary<string, object?> ParseBindings()
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase); var lines = (_bindings.Text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines) { var separator = line.IndexOf('='); if (separator <= 0) throw new FormatException($"Invalid binding '{line}'. Use one name=value per line."); result[line[..separator].Trim()] = line[(separator + 1)..]; } return result;
    }
    private string MutationSignature() => (_sql.Text ?? string.Empty) + "\n--bindings--\n" + string.Join('\n', ParseBindings().OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"));
    private void ResetMutationReview() { _reviewedMutation = null; _confirmMutation.IsChecked = false; _confirmMutation.IsVisible = false; _applyMutation.IsEnabled = false; }
    private void ResetPublicationReview() { _reviewedPublication = null; _confirmPublish.IsChecked = false; _confirmPublish.IsVisible = false; _applyPublish.IsEnabled = false; }
    private void SetBusy(bool busy, string? message = null) { foreach (var button in new[] { _create, _query, _previewMutation, _refresh, _previewPublish }) button.IsEnabled = !busy; if (busy) { _applyMutation.IsEnabled = false; _applyPublish.IsEnabled = false; if (message is not null) _status.Text = message; } else { _applyMutation.IsEnabled = _confirmMutation.IsChecked == true && _reviewedMutation is not null; _applyPublish.IsEnabled = _confirmPublish.IsChecked == true && _reviewedPublication is not null; } }
    private void Succeed(string message) { _status.Text = message; _status.Foreground = Brush.Parse("#76C89A"); }
    private void Fail(string message) { _status.Text = message; _status.Foreground = Brush.Parse("#E17A76"); }
    private static string RenderQuery(DbcStagingQueryResult result) { var text = new StringBuilder(); text.AppendLine(string.Join('\t', result.Columns)); foreach (var row in result.Rows) text.AppendLine(string.Join('\t', row.Select(Display))); if (result.Truncated) text.AppendLine("… truncated …"); return text.ToString(); }
    private static string RenderDiff(DbcStagingDiff diff) { var text = new StringBuilder($"UPDATED {diff.UpdatedRows:N0} · APPENDED {diff.AppendedRows:N0} · DELETED {diff.DeletedRows:N0} · CELLS {diff.ChangedCells:N0} · APPLYABLE {diff.CanApply}\n"); foreach (var finding in diff.Findings) text.AppendLine("BLOCK · " + finding); foreach (var change in diff.Changes) text.AppendLine($"{(change.Appended ? "APPEND" : "UPDATE")} · key {change.RecordKey?.ToString() ?? "-"} · {change.Column}\n  {change.Before}\n→ {change.After}"); if (diff.DetailsTruncated) text.AppendLine("… additional changed cells omitted from this view …"); return text.ToString(); }
    private static string Display(object? value) => value switch { null => "NULL", byte[] bytes => "0x" + Convert.ToHexString(bytes), IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty, _ => value.ToString() ?? string.Empty };
    private static string Quote(string name) => '"' + name.Replace("\"", "\"\"") + '"';
    private static TextBlock Label(string text) => new() { Text = text, Foreground = Brush.Parse("#8390A5"), FontSize = 11 };
    private static T Row<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
    private static T Column<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
}
