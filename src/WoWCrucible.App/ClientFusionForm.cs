using WoWCrucible.Core;

namespace WoWCrucible.App;

internal sealed class ClientFusionForm : Form
{
    private readonly TextBox _base = new() { Dock = DockStyle.Fill };
    private readonly ListBox _sources = new() { Dock = DockStyle.Fill, Height = 80 };
    private readonly DataGridView _grid = new FastDataGridView { Dock = DockStyle.Fill, ReadOnly = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 44, Padding = new(10), ForeColor = Color.FromArgb(55, 65, 81) };
    private readonly Button _analyze = new() { Text = "Analyze Fusion", AutoSize = true };
    private readonly List<ClientFusionSource> _sourceValues = [];
    private ClientFusionPlan? _plan;

    public ClientFusionForm(AppSettings settings)
    {
        Text = "Client Fusion Planner · WoW Crucible"; Width = 1450; Height = 850; StartPosition = FormStartPosition.CenterParent;
        _base.Text = Directory.Exists(settings.BaseDbcPath) ? settings.BaseDbcPath : string.Empty;
        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new(10) };
        top.ColumnStyles.Add(new(SizeType.AutoSize)); top.ColumnStyles.Add(new(SizeType.Percent, 100)); top.ColumnStyles.Add(new(SizeType.AutoSize));
        top.Controls.Add(new Label { Text = "Stock/effective base", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0); top.Controls.Add(_base, 1, 0); top.Controls.Add(Button("Browse…", () => PickBase()), 2, 0);
        top.Controls.Add(new Label { Text = "Override sources", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1); top.Controls.Add(_sources, 1, 1);
        var sourceButtons = new FlowLayoutPanel { AutoSize = true }; sourceButtons.Controls.Add(Button("Add…", AddSource)); sourceButtons.Controls.Add(Button("Remove", RemoveSource)); top.Controls.Add(sourceButtons, 2, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new(10, 0, 10, 8) };
        _analyze.Click += async (_, _) => await Analyze(); actions.Controls.Add(_analyze); actions.Controls.Add(Button("Stage Resolved Patch…", Stage));

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Client path", Width = 430, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Result", Width = 150, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Candidates", Width = 90, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "Chosen source", Width = 220, FlatStyle = FlatStyle.Flat });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Guidance", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 360, ReadOnly = true });
        _grid.DataError += (_, e) => e.ThrowException = false;
        _grid.SelectionChanged += (_, _) => { if (_grid.CurrentRow?.Tag is ClientFusionEntry entry) _status.Text = $"{entry.ArchivePath}: {entry.Guidance}"; };
        Controls.Add(_grid); Controls.Add(_status); Controls.Add(actions); Controls.Add(top);
        _status.Text = "Use an extracted stock/effective client root as the base, then add extracted mod roots. Only paths differing from base become patch inputs; collisions require a choice.";
    }

    private static Button Button(string text, Action action) { var button = new Button { Text = text, AutoSize = true }; button.Click += (_, _) => action(); return button; }
    private void PickBase() { using var dialog = new FolderBrowserDialog { SelectedPath = Directory.Exists(_base.Text) ? _base.Text : string.Empty }; if (dialog.ShowDialog(this) == DialogResult.OK) _base.Text = dialog.SelectedPath; }
    private void AddSource()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose one extracted/effective client override root" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var root = Path.GetFullPath(dialog.SelectedPath); if (_sourceValues.Any(value => value.RootPath.Equals(root, StringComparison.OrdinalIgnoreCase))) return;
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root)); var suffix = 2; var unique = name;
        while (_sourceValues.Any(value => value.Name.Equals(unique, StringComparison.OrdinalIgnoreCase))) unique = $"{name} {suffix++}";
        _sourceValues.Add(new(unique, root)); RefreshSources();
    }
    private void RemoveSource() { if (_sources.SelectedIndex < 0) return; _sourceValues.RemoveAt(_sources.SelectedIndex); RefreshSources(); }
    private void RefreshSources() { _sources.Items.Clear(); foreach (var source in _sourceValues) _sources.Items.Add($"{source.Name}  —  {source.RootPath}"); }

    private async Task Analyze()
    {
        try
        {
            _analyze.Enabled = false; UseWaitCursor = true; _status.Text = "Mapping client paths and hashing collisions…";
            var progress = new Progress<(int Done, int Total, string Path)>(value => _status.Text = $"{value.Done:N0}/{value.Total:N0} · {value.Path}");
            _plan = await Task.Run(() => ClientFusionPlanner.Analyze(_base.Text, _sourceValues, progress)); Fill();
        }
        catch (Exception ex) { CrashLogger.Log("Client fusion analysis failed", ex); MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { UseWaitCursor = false; _analyze.Enabled = true; }
    }

    private void Fill()
    {
        _grid.Rows.Clear();
        foreach (var entry in _plan!.Entries)
        {
            var rowIndex = _grid.Rows.Add(entry.ArchivePath, entry.Status, entry.Candidates.Count, string.Empty, entry.Guidance); var row = _grid.Rows[rowIndex]; row.Tag = entry;
            var choice = (DataGridViewComboBoxCell)row.Cells[3];
            if (entry.Status == ClientFusionStatus.Conflict) { choice.Items.Add("— unresolved —"); foreach (var candidate in entry.Candidates) choice.Items.Add(candidate.SourceName); choice.Value = "— unresolved —"; }
            else { choice.Items.Add(entry.Candidates[0].SourceName); choice.Value = entry.Candidates[0].SourceName; choice.ReadOnly = true; }
            if (entry.Status == ClientFusionStatus.Conflict) row.DefaultCellStyle.BackColor = Color.FromArgb(254, 242, 242);
            else if (entry.Status == ClientFusionStatus.IdenticalToBase) row.DefaultCellStyle.ForeColor = Color.Gray;
        }
        var changes = _plan.Entries.Count(entry => entry.Status != ClientFusionStatus.IdenticalToBase); var conflicts = _plan.Entries.Count(entry => entry.Status == ClientFusionStatus.Conflict);
        _status.Text = $"{_plan.Entries.Count:N0} override paths examined · {changes:N0} patch candidates · {conflicts:N0} explicit conflicts. Whole-file DBC conflicts should be merged with Layered DBCs before staging.";
    }

    private void Stage()
    {
        if (_plan is null) { _status.Text = "Analyze the fusion sources first."; return; }
        var selections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is not ClientFusionEntry { Status: ClientFusionStatus.Conflict } entry) continue;
            var sourceName = row.Cells[3].Value?.ToString(); var candidate = entry.Candidates.FirstOrDefault(value => value.SourceName.Equals(sourceName, StringComparison.OrdinalIgnoreCase));
            if (candidate is not null) selections[entry.ArchivePath] = candidate.FilePath;
        }
        using var dialog = new FolderBrowserDialog { Description = "Choose a new review folder for the small fusion patch staging tree" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { var result = ClientFusionPlanner.Stage(dialog.SelectedPath, _plan, selections); _status.Text = $"Staged {result.StagedFiles:N0} changed files; omitted {result.SkippedBaseFiles:N0} base-identical files; {result.UnresolvedConflicts:N0} conflicts remain excluded. Manifest: {result.ManifestPath}"; }
        catch (Exception ex) { CrashLogger.Log("Client fusion staging failed", ex); MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
