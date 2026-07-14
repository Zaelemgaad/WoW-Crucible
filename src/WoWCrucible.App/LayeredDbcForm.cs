using WoWCrucible.Core;

namespace WoWCrucible.App;

internal sealed class LayeredDbcForm : Form
{
    private readonly AppSettings _settings;
    private readonly string? _schemaPath;
    private readonly Action<string> _openEffective;
    private readonly Action<IEnumerable<string>> _stageOverrides;
    private readonly DataGridView _grid = new FastDataGridView();
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 32, Padding = new(8) };
    private IReadOnlyList<DbcLayerEntry> _entries = [];
    private CancellationTokenSource? _detailCancellation;

    public LayeredDbcForm(AppSettings settings, string? schemaPath, Action<string> openEffective, Action<IEnumerable<string>> stageOverrides)
    {
        _settings = settings; _schemaPath = schemaPath; _openEffective = openEffective; _stageOverrides = stageOverrides;
        Text = "Layered DBC Comparison — base → override"; Width = 1100; Height = 720; StartPosition = FormStartPosition.CenterParent;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new(8), WrapContents = false };
        bar.Controls.Add(Button("Refresh Layers", async (_, _) => await RefreshLayers()));
        bar.Controls.Add(Button("Open Effective DBC", (_, _) => OpenSelected()));
        bar.Controls.Add(Button("Promote Fields/Rows", (_, _) => PromoteSelected()));
        bar.Controls.Add(Button("Stage Changed Overrides", (_, _) => StageOverrides()));
        _grid.Dock = DockStyle.Fill; _grid.ReadOnly = true; _grid.AllowUserToAddRows = false; _grid.AllowUserToDeleteRows = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.Columns.Add("Name", "DBC"); _grid.Columns.Add("Status", "Layer status"); _grid.Columns.Add("Size", "Effective size"); _grid.Columns.Add("Path", "Effective path");
        _grid.SelectionChanged += async (_, _) => await ShowSelectedDetails();
        Controls.Add(_grid); Controls.Add(_status); Controls.Add(bar);
        Shown += async (_, _) => await RefreshLayers();
        FormClosing += (_, _) => _detailCancellation?.Cancel();
    }

    private static Button Button(string text, EventHandler click) { var button = new Button { Text = text, AutoSize = true, Height = 28 }; button.Click += click; return button; }

    private async Task RefreshLayers()
    {
        if (!Directory.Exists(_settings.BaseDbcPath) || !Directory.Exists(_settings.OverrideDbcPath))
        {
            _status.Text = "Set Base DBC layer and Override DBC layer under Paths first."; return;
        }
        try
        {
            UseWaitCursor = true; _status.Text = "Hash-comparing DBC layers…";
            _entries = await Task.Run(() => DbcLayerComparer.CompareDirectories(_settings.BaseDbcPath, _settings.OverrideDbcPath));
            _grid.Rows.Clear();
            foreach (var entry in _entries) _grid.Rows.Add(entry.Name, entry.Status, $"{entry.EffectiveSize / 1024d:0.##} KB", entry.EffectivePath);
            var changed = _entries.Count(entry => entry.Status is DbcLayerStatus.Overridden or DbcLayerStatus.OverrideOnly);
            _status.Text = $"{_entries.Count:N0} effective DBCs · {changed:N0} supplied/changed by override · {_entries.Count(entry => entry.Status == DbcLayerStatus.Identical):N0} identical overrides";
        }
        catch (Exception ex) { CrashLogger.Log("Layer comparison failed", ex); MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { UseWaitCursor = false; }
    }

    private DbcLayerEntry? Selected => _grid.CurrentRow is { Index: >= 0 } row && row.Index < _entries.Count ? _entries[row.Index] : null;
    private void OpenSelected() { if (Selected is { } entry) { _openEffective(entry.EffectivePath); Close(); } }
    private void StageOverrides()
    {
        var paths = _entries.Where(entry => entry.OverridePath is not null && entry.Status is DbcLayerStatus.Overridden or DbcLayerStatus.OverrideOnly).Select(entry => entry.OverridePath!).ToArray();
        if (paths.Length == 0) { _status.Text = "No changed override DBCs to stage."; return; }
        _stageOverrides(paths);
    }

    private void PromoteSelected()
    {
        if (Selected is not { BasePath: not null, OverridePath: not null, Status: DbcLayerStatus.Overridden } entry) { _status.Text = "Select a genuinely overridden DBC first."; return; }
        try
        {
            var schema = _schemaPath is null ? DbcSchemaCatalog.CreateBuiltIn12340() : DbcSchemaCatalog.Load(_schemaPath);
            var sample = WdbcFile.Load(entry.BasePath); var resolution = schema.ResolveColumns(Path.GetFileNameWithoutExtension(entry.Name), sample.FieldCount);
            if (resolution.UsedFallback) { MessageBox.Show(this, "Selective promotion requires a matching named schema. Fix/select the schema definition before promoting fields.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            using var promotion = new DbcPromotionForm(entry.BasePath, entry.OverridePath, resolution.Columns, resolution.KeyStrategy, _openEffective);
            promotion.ShowDialog(this);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private async Task ShowSelectedDetails()
    {
        _detailCancellation?.Cancel(); _detailCancellation?.Dispose(); _detailCancellation = new();
        var token = _detailCancellation.Token;
        if (Selected is not { BasePath: not null, OverridePath: not null, Status: DbcLayerStatus.Overridden } entry) return;
        try
        {
            _status.Text = $"Comparing {entry.Name} rows…";
            var schema = _schemaPath is null ? DbcSchemaCatalog.CreateBuiltIn12340() : DbcSchemaCatalog.Load(_schemaPath);
            var sample = WdbcFile.Load(entry.BasePath); var resolution = schema.ResolveColumns(Path.GetFileNameWithoutExtension(entry.Name), sample.FieldCount);
            var detail = await Task.Run(() => DbcLayerComparer.CompareFiles(entry.BasePath, entry.OverridePath, resolution.Columns, resolution.KeyStrategy, token), token);
            if (token.IsCancellationRequested || Selected?.Name != entry.Name) return;
            _status.Text = $"{entry.Name}: +{detail.AddedRows:N0} rows · -{detail.RemovedRows:N0} rows · {detail.ModifiedRows:N0} modified rows · {detail.ModifiedCells:N0} changed fields";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = $"Detailed comparison unavailable: {ex.Message}"; }
    }
}
