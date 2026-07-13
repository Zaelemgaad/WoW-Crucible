using WoWCrucible.Core;

namespace WoWCrucible.App;

internal sealed class DbcPromotionForm : Form
{
    private readonly string _basePath;
    private readonly string _overridePath;
    private readonly IReadOnlyList<DbcColumn> _columns;
    private readonly Action<string> _openOutput;
    private readonly DataGridView _grid = new FastDataGridView();
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 32, Padding = new(8) };
    private IReadOnlyList<DbcCellDifference> _differences = [];
    private CancellationTokenSource? _cancellation;

    public DbcPromotionForm(string basePath, string overridePath, IReadOnlyList<DbcColumn> columns, Action<string> openOutput)
    {
        _basePath = basePath; _overridePath = overridePath; _columns = columns; _openOutput = openOutput;
        Text = $"Promote override changes — {Path.GetFileName(basePath)}"; Width = 1250; Height = 760; StartPosition = FormStartPosition.CenterParent;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new(8), WrapContents = false };
        bar.Controls.Add(Button("Apply Selected Fields", (_, _) => Apply(CreateFieldOperations())));
        bar.Controls.Add(Button("Apply Selected Rows", (_, _) => Apply(CreateRowOperations())));
        bar.Controls.Add(Button("Save Field Manifest", (_, _) => SaveManifest(CreateFieldOperations())));
        bar.Controls.Add(Button("Apply Existing Manifest", (_, _) => ApplyExistingManifest()));
        _grid.Dock = DockStyle.Fill; _grid.ReadOnly = true; _grid.VirtualMode = true; _grid.AllowUserToAddRows = false; _grid.AllowUserToDeleteRows = false;
        _grid.MultiSelect = true; _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ID", HeaderText = "ID", FillWeight = 10 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Column", HeaderText = "Changed field", FillWeight = 24 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Base", HeaderText = "Base value", FillWeight = 33 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Override", HeaderText = "Override value", FillWeight = 33 });
        _grid.CellValueNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _differences.Count) return;
            var difference = _differences[e.RowIndex];
            e.Value = e.ColumnIndex switch { 0 => difference.Id, 1 => difference.ColumnName, 2 => difference.BaseValue, 3 => difference.OverrideValue, _ => null };
        };
        Controls.Add(_grid); Controls.Add(_status); Controls.Add(bar);
        Shown += async (_, _) => await LoadDifferences(); FormClosing += (_, _) => _cancellation?.Cancel();
    }

    private static Button Button(string text, EventHandler click) { var button = new Button { Text = text, AutoSize = true, Height = 28 }; button.Click += click; return button; }
    private async Task LoadDifferences()
    {
        _cancellation = new();
        try
        {
            UseWaitCursor = true; _status.Text = "Finding semantic cell differences…";
            _differences = await Task.Run(() => DbcPromotionService.GetDifferences(_basePath, _overridePath, _columns, _cancellation.Token), _cancellation.Token);
            _grid.RowCount = _differences.Count; _grid.Invalidate();
            _status.Text = $"{_differences.Count:N0} changed cells/new rows. Select entries to promote; the base file is never overwritten automatically.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { CrashLogger.Log("DBC promotion diff failed", ex); MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { UseWaitCursor = false; }
    }

    private DbcPromotionOperation[] CreateFieldOperations()
    {
        var selected = SelectedDifferences();
        return selected.GroupBy(difference => difference.Id).Select(group => group.Any(difference => difference.ColumnIndex < 0)
            ? new DbcPromotionOperation(group.Key, ["*"])
            : new DbcPromotionOperation(group.Key, group.Select(difference => difference.ColumnName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())).ToArray();
    }

    private DbcPromotionOperation[] CreateRowOperations() => SelectedDifferences().Select(difference => difference.Id).Distinct().Select(id => new DbcPromotionOperation(id, ["*"])).ToArray();
    private DbcCellDifference[] SelectedDifferences() => _grid.SelectedRows.Cast<DataGridViewRow>().Select(row => row.Index).Where(index => index >= 0 && index < _differences.Count).Select(index => _differences[index]).ToArray();

    private void SaveManifest(DbcPromotionOperation[] operations)
    {
        if (operations.Length == 0) { _status.Text = "Select at least one changed field."; return; }
        using var dialog = new SaveFileDialog { Filter = "DBC promotion manifest (*.dbc-promotion.json)|*.dbc-promotion.json|JSON (*.json)|*.json", FileName = $"{Path.GetFileNameWithoutExtension(_basePath)}.dbc-promotion.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var key = _columns.FirstOrDefault(column => column.IsIndex) ?? _columns[0];
        DbcPromotionService.SaveManifest(dialog.FileName, Path.GetFileNameWithoutExtension(_basePath), key.Name, operations);
        _status.Text = $"Saved repeatable promotion manifest: {dialog.FileName}";
    }

    private void Apply(DbcPromotionOperation[] operations)
    {
        if (operations.Length == 0) { _status.Text = "Select at least one changed field or row."; return; }
        var key = _columns.FirstOrDefault(column => column.IsIndex) ?? _columns[0];
        ApplyManifest(new(1, Path.GetFileNameWithoutExtension(_basePath), key.Name, operations));
    }

    private void ApplyExistingManifest()
    {
        using var dialog = new OpenFileDialog { Filter = "DBC promotion manifest (*.dbc-promotion.json)|*.dbc-promotion.json|JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) == DialogResult.OK) ApplyManifest(DbcPromotionService.LoadManifest(dialog.FileName));
    }

    private void ApplyManifest(DbcPromotionManifest manifest)
    {
        using var dialog = new SaveFileDialog { Filter = "WoW database files (*.dbc)|*.dbc", FileName = Path.GetFileName(_basePath) };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            DbcPromotionService.Apply(_basePath, _overridePath, dialog.FileName, _columns, manifest);
            _status.Text = $"Created promoted DBC: {dialog.FileName}";
            if (MessageBox.Show(this, "Promotion applied successfully. Open the generated DBC in Crucible?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes) { _openOutput(dialog.FileName); Close(); }
        }
        catch (Exception ex) { CrashLogger.Log("DBC promotion apply failed", ex); MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
