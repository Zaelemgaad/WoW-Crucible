using WoWCrucible.Core;

namespace WoWCrucible.App;

internal sealed class ClientServerPlanForm : Form
{
    private readonly AppSettings _settings;
    private readonly TextBox _clientDbcs = new() { Dock = DockStyle.Fill };
    private readonly TextBox _serverRoot = new() { Dock = DockStyle.Fill };
    private readonly TextBox _coreSource = new() { Dock = DockStyle.Fill };
    private readonly DataGridView _grid = new FastDataGridView { Dock = DockStyle.Fill, ReadOnly = true, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None };
    private readonly Label _summary = new() { Dock = DockStyle.Bottom, Height = 48, Padding = new(10), ForeColor = Color.FromArgb(55, 65, 81) };
    private readonly Button _analyze = new() { Text = "Analyze Client → Server", AutoSize = true };
    private ClientServerDeploymentPlan? _plan;

    public ClientServerPlanForm(AppSettings settings)
    {
        _settings = settings;
        Text = "Client DBC → Server Plan · WoW Crucible"; Width = 1400; Height = 820; StartPosition = FormStartPosition.CenterParent;
        _clientDbcs.Text = Directory.Exists(settings.OverrideDbcPath) ? settings.OverrideDbcPath : string.Empty;
        _serverRoot.Text = settings.ServerRootPath;
        _coreSource.Text = settings.CoreSourcePath;

        var paths = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new(10) };
        paths.ColumnStyles.Add(new(SizeType.AutoSize)); paths.ColumnStyles.Add(new(SizeType.Percent, 100)); paths.ColumnStyles.Add(new(SizeType.AutoSize));
        AddPath(paths, 0, "Extracted/effective DBCs", _clientDbcs, "Choose DBFilesClient or a folder containing extracted DBCs", () => PickFolder(_clientDbcs));
        AddPath(paths, 1, "Installed server", _serverRoot, "Choose the server folder containing the live worldserver.conf", () => PickFolder(_serverRoot));
        AddPath(paths, 2, "Core source (recommended)", _coreSource, "Choose current AzerothCore/TrinityCore source so every loaded DBC is discovered", () => PickFolder(_coreSource));

        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new(10, 0, 10, 8) };
        _analyze.Click += async (_, _) => await Analyze();
        actions.Controls.Add(_analyze); actions.Controls.Add(Button("Export Plan…", ExportPlan)); actions.Controls.Add(Button("Stage Patch + Server Files…", Stage));

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "DBC", Width = 230 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Rows", Width = 75 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Fields", Width = 65 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Client/server result", Width = 190 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Core consumer", Width = 145 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "SQL overlay", Width = 190 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Guidance", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 320 });
        _grid.SelectionChanged += (_, _) => ShowSelected();

        Controls.Add(_grid); Controls.Add(_summary); Controls.Add(actions); Controls.Add(paths);
        _summary.Text = "Select the effective extracted DBC layer and installed server. Different same-named DBCs under the source are treated as unresolved layer conflicts, never last-writer-wins.";
    }

    private static void AddPath(TableLayoutPanel panel, int row, string label, TextBox box, string tooltip, Action browse)
    {
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row); panel.Controls.Add(box, 1, row);
        var button = Button("Browse…", browse); new ToolTip().SetToolTip(button, tooltip); panel.Controls.Add(button, 2, row);
    }

    private static Button Button(string text, Action action) { var button = new Button { Text = text, AutoSize = true }; button.Click += (_, _) => action(); return button; }
    private void PickFolder(TextBox target)
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = Directory.Exists(target.Text) ? target.Text : string.Empty };
        if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.SelectedPath;
    }

    private async Task Analyze()
    {
        try
        {
            _analyze.Enabled = false; UseWaitCursor = true; _summary.Text = "Detecting server configuration and comparing DBC identities…";
            var workspace = await ServerWorkspaceDetector.DetectAsync(_serverRoot.Text);
            var source = Directory.Exists(_coreSource.Text) ? _coreSource.Text : null;
            _plan = await Task.Run(() => ClientServerDeploymentPlanner.Analyze(_clientDbcs.Text, workspace, source));
            _settings.OverrideDbcPath = Path.GetFullPath(_clientDbcs.Text); _settings.ServerRootPath = workspace.RootPath;
            if (source is not null) _settings.CoreSourcePath = Path.GetFullPath(source);
            _settings.CoreDbcPath = workspace.DbcPath; _settings.Save();
            FillGrid();
        }
        catch (Exception ex) { CrashLogger.Log("Client to server analysis failed", ex); MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); _summary.Text = "Analysis failed; no files were changed."; }
        finally { UseWaitCursor = false; _analyze.Enabled = true; }
    }

    private void FillGrid()
    {
        _grid.Rows.Clear();
        foreach (var entry in _plan!.Entries)
        {
            var row = _grid.Rows.Add(entry.DbcFileName, entry.ClientRows?.ToString("N0") ?? "—", entry.ClientFields?.ToString("N0") ?? "—", entry.Status, entry.Consumption, entry.SqlTableName ?? "—", entry.Guidance);
            _grid.Rows[row].Tag = entry;
            if (entry.Status is ClientServerPlanStatus.ConflictingClientLayers or ClientServerPlanStatus.InvalidDbc or ClientServerPlanStatus.UnknownConsumer or ClientServerPlanStatus.MissingServerDbc)
                _grid.Rows[row].DefaultCellStyle.BackColor = Color.FromArgb(254, 242, 242);
            else if (entry.Status == ClientServerPlanStatus.SqlOverlayRequiresAudit) _grid.Rows[row].DefaultCellStyle.BackColor = Color.FromArgb(255, 247, 237);
            else if (entry.Status == ClientServerPlanStatus.Identical) _grid.Rows[row].DefaultCellStyle.ForeColor = Color.Gray;
        }
        var changed = _plan.Entries.Count(entry => entry.Status != ClientServerPlanStatus.Identical);
        var blocked = _plan.Entries.Count(entry => entry.Status is ClientServerPlanStatus.ConflictingClientLayers or ClientServerPlanStatus.InvalidDbc or ClientServerPlanStatus.UnknownConsumer or ClientServerPlanStatus.MissingServerDbc);
        var sql = _plan.Entries.Count(entry => entry.Status == ClientServerPlanStatus.SqlOverlayRequiresAudit);
        _summary.Text = $"{_plan.Entries.Count:N0} DBCs analyzed · {changed:N0} differ/need review · {sql:N0} SQL-overlay audits · {blocked:N0} blocked or unresolved. Staging writes only to a new review folder, never to the live server.";
    }

    private void ShowSelected()
    {
        if (_grid.CurrentRow?.Tag is not ClientServerPlanEntry entry) return;
        _summary.Text = $"{entry.DbcFileName}: {entry.Guidance}  Profile: {entry.Profile} ({entry.SupportedRevision})." +
            (entry.ConflictingSources is { Count: > 1 } ? $" Conflicting sources: {string.Join(" | ", entry.ConflictingSources)}" : string.Empty);
    }

    private void ExportPlan()
    {
        if (_plan is null) { _summary.Text = "Analyze first, then export the reviewed result."; return; }
        using var dialog = new SaveFileDialog { Filter = "Crucible deployment plan (*.json)|*.json", FileName = "client-server-plan.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ClientServerDeploymentPlanner.Save(dialog.FileName, _plan); _summary.Text = $"Plan saved: {dialog.FileName}";
    }

    private void Stage()
    {
        if (_plan is null) { _summary.Text = "Analyze first, then stage its safe outputs."; return; }
        using var dialog = new FolderBrowserDialog { Description = "Choose an empty/new review folder for the patch manifest and server DBC candidates" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var result = ClientServerDeploymentPlanner.Stage(dialog.SelectedPath, _plan);
            _summary.Text = $"Staged {result.ClientFiles:N0} client patch files and {result.ServerFiles:N0} server DBC candidates; {result.BlockedFiles:N0} unresolved. Review {result.PlanPath} before applying anything live.";
        }
        catch (Exception ex) { CrashLogger.Log("Client to server staging failed", ex); MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
