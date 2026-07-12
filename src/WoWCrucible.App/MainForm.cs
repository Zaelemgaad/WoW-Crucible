using System.Diagnostics;
using WoWCrucible.Core;

namespace WoWCrucible.App;

public sealed class MainForm : Form
{
    private readonly DataGridView _grid = new FastDataGridView();
    private readonly ToolStripStatusLabel _status = new();
    private readonly ToolStripTextBox _search = new() { AutoSize = false, Width = 280 };
    private readonly System.Windows.Forms.Timer _searchTimer = new() { Interval = 250 };
    private WdbcFile? _file;
    private IReadOnlyList<DbcColumn> _columns = [];
    private int[]? _visibleRows;
    private string? _schemaPath;

    public MainForm(string? initialFile)
    {
        Text = "WoW Crucible — 3.3.5a Editor";
        Width = 1500;
        Height = 900;
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;

        var tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new(6, 4, 6, 4) };
        tools.Items.Add(Button("Open DBC", (_, _) => OpenFile()));
        tools.Items.Add(Button("Save", (_, _) => SaveFile(false)));
        tools.Items.Add(Button("Save As", (_, _) => SaveFile(true)));
        tools.Items.Add(new ToolStripSeparator());
        tools.Items.Add(Button("New Row", (_, _) => AddRow()));
        tools.Items.Add(Button("Clone Row", (_, _) => CloneRow()));
        tools.Items.Add(Button("Delete Rows", (_, _) => DeleteRows()));
        tools.Items.Add(new ToolStripSeparator());
        tools.Items.Add(Button("Build Patch MPQ", (_, _) => OpenPatchBuilder()));
        tools.Items.Add(Button("Sync to Core Data", (_, _) => SyncToCoreData()));
        tools.Items.Add(new ToolStripSeparator());
        tools.Items.Add(new ToolStripLabel("Search all fields:"));
        tools.Items.Add(_search);
        tools.Items.Add(Button("Clear", (_, _) => { _search.Text = string.Empty; ApplyFilter(); }));

        _grid.Dock = DockStyle.Fill;
        _grid.VirtualMode = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersWidth = 70;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.CellValueNeeded += GridCellValueNeeded;
        _grid.CellValuePushed += GridCellValuePushed;
        _grid.RowPostPaint += GridRowPostPaint;
        _grid.DataError += (_, e) => { e.ThrowException = false; ShowError(e.Exception); };

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_status);
        Controls.Add(_grid);
        Controls.Add(statusStrip);
        Controls.Add(tools);
        tools.Dock = DockStyle.Top;

        _search.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ApplyFilter(); };
        FormClosing += OnFormClosing;
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
            if (paths.Length == 1 && File.Exists(paths[0]) && Path.GetExtension(paths[0]).Equals(".dbc", StringComparison.OrdinalIgnoreCase))
                LoadFile(paths[0]);
            else
                OpenPatchBuilder(paths);
        };

        _schemaPath = FindSchemaPath();
        if (!string.IsNullOrWhiteSpace(initialFile) && File.Exists(initialFile))
            Shown += (_, _) => LoadFile(initialFile);
    }

    private static ToolStripButton Button(string text, EventHandler action)
    {
        var button = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text };
        button.Click += action;
        return button;
    }

    private void OpenFile()
    {
        using var dialog = new OpenFileDialog { Filter = "WoW database files (*.dbc)|*.dbc|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) == DialogResult.OK) LoadFile(dialog.FileName);
    }

    private void OpenPatchBuilder(IEnumerable<string>? droppedPaths = null)
    {
        var initial = droppedPaths ?? (_file is null ? null : new[] { _file.SourcePath });
        using var builder = new PatchBuilderForm(initial);
        builder.ShowDialog(this);
    }

    private void SyncToCoreData()
    {
        if (_file is null) return;
        if (_file.IsDirty)
        {
            var save = MessageBox.Show(this, "Save the current DBC before synchronizing it?", "Sync to core data", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (save == DialogResult.Cancel) return;
            if (save == DialogResult.Yes) SaveFile(false);
            if (_file.IsDirty) return;
        }
        using var dialog = new FolderBrowserDialog { Description = "Select the core's data\\dbc directory", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var destination = Path.Combine(dialog.SelectedPath, Path.GetFileName(_file.SourcePath));
            if (Path.GetFullPath(destination).Equals(Path.GetFullPath(_file.SourcePath), StringComparison.OrdinalIgnoreCase))
            {
                _status.Text = "The open DBC is already in that core data directory.";
                return;
            }
            if (File.Exists(destination)) File.Copy(destination, destination + ".bak", true);
            var temp = destination + ".tmp";
            File.Copy(_file.SourcePath, temp, true);
            File.Move(temp, destination, true);
            _status.Text = $"Synchronized to {destination}";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void LoadFile(string path)
    {
        try
        {
            Cursor = Cursors.WaitCursor;
            var timer = Stopwatch.StartNew();
            var loaded = WdbcFile.Load(path);
            DbcSchemaCatalog? schemas = _schemaPath is null ? null : DbcSchemaCatalog.Load(_schemaPath);
            var table = Path.GetFileNameWithoutExtension(path);
            var columns = schemas?.GetColumns(table, loaded.FieldCount) ?? Enumerable.Range(0, loaded.FieldCount)
                .Select(i => new DbcColumn(i, i * 4, 4, i == 0 ? "ID" : $"Field_{i}", DbcValueType.Raw32, i == 0)).ToArray();

            _file = loaded;
            _columns = columns;
            _visibleRows = null;
            _grid.Columns.Clear();
            foreach (var column in columns)
            {
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    Name = $"field{column.Index}", HeaderText = column.Name, Width = column.Type == DbcValueType.StringOffset ? 220 : 105,
                    ReadOnly = false, SortMode = DataGridViewColumnSortMode.NotSortable
                });
            }
            _grid.RowCount = loaded.RowCount;
            timer.Stop();
            Text = $"WoW Crucible — {Path.GetFileName(path)}";
            _status.Text = $"{loaded.RowCount:N0} rows · {loaded.FieldCount:N0} fields · {loaded.StringTableSize:N0} string bytes · loaded in {timer.ElapsedMilliseconds:N0} ms";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { Cursor = Cursors.Default; }
    }

    private async void ApplyFilter()
    {
        if (_file is null) return;
        var query = _search.Text.Trim();
        if (query.Length == 0)
        {
            _visibleRows = null;
            _grid.RowCount = _file.RowCount;
            _grid.Invalidate();
            _status.Text = $"{_file.RowCount:N0} rows";
            return;
        }

        try
        {
            var file = _file;
            var columns = _columns;
            _status.Text = "Searching…";
            var timer = Stopwatch.StartNew();
            var matches = await Task.Run(() => Enumerable.Range(0, file.RowCount).AsParallel().AsOrdered()
                .Where(row => file.RowContains(row, query, columns)).ToArray());
            if (!ReferenceEquals(file, _file) || query != _search.Text.Trim()) return;
            _visibleRows = matches;
            _grid.RowCount = matches.Length;
            _grid.Invalidate();
            _status.Text = $"{matches.Length:N0} of {file.RowCount:N0} rows · searched in {timer.ElapsedMilliseconds:N0} ms";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void SaveFile(bool saveAs)
    {
        if (_file is null) return;
        var path = _file.SourcePath;
        if (saveAs)
        {
            using var dialog = new SaveFileDialog { Filter = "WoW database files (*.dbc)|*.dbc", FileName = Path.GetFileName(path) };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            path = dialog.FileName;
        }
        try { _file.Save(path); _status.Text = $"Saved {path} (backup: {path}.bak)"; }
        catch (Exception ex) { ShowError(ex); }
    }

    private DbcColumn? IdColumn => _columns.FirstOrDefault(column => column.IsIndex) ?? _columns.FirstOrDefault();

    private void AddRow()
    {
        if (_file is null) return;
        try
        {
            ClearFilterForStructureChange();
            var row = _file.AddBlankRow(IdColumn);
            RefreshRows(row);
            _status.Text = $"Created row {row:N0} with the next available ID";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void CloneRow()
    {
        if (_file is null || _grid.CurrentCell is null) return;
        try
        {
            var source = SourceRow(_grid.CurrentCell.RowIndex);
            ClearFilterForStructureChange();
            var row = _file.CloneRow(source, IdColumn);
            RefreshRows(row);
            _status.Text = $"Cloned source row {source:N0} into row {row:N0} with a new ID";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void DeleteRows()
    {
        if (_file is null || _grid.SelectedCells.Count == 0) return;
        var rows = _grid.SelectedCells.Cast<DataGridViewCell>().Select(cell => SourceRow(cell.RowIndex)).Distinct().ToArray();
        if (MessageBox.Show(this, $"Delete {rows.Length:N0} selected row(s)?", "Delete rows", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try
        {
            _file.DeleteRows(rows);
            ClearFilterForStructureChange();
            RefreshRows(Math.Min(rows.Min(), Math.Max(0, _file.RowCount - 1)));
            _status.Text = $"Deleted {rows.Length:N0} row(s)";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ClearFilterForStructureChange()
    {
        _search.Text = string.Empty;
        _searchTimer.Stop();
        _visibleRows = null;
    }

    private void RefreshRows(int selectRow)
    {
        if (_file is null) return;
        _grid.RowCount = _file.RowCount;
        _grid.Invalidate();
        if (_file.RowCount > 0 && _grid.ColumnCount > 0)
        {
            selectRow = Math.Clamp(selectRow, 0, _file.RowCount - 1);
            _grid.CurrentCell = _grid[0, selectRow];
            _grid.FirstDisplayedScrollingRowIndex = selectRow;
        }
    }

    private void GridCellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (_file is null || e.RowIndex < 0 || e.ColumnIndex < 0) return;
        e.Value = _file.GetDisplayValue(SourceRow(e.RowIndex), _columns[e.ColumnIndex]);
    }

    private void GridCellValuePushed(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (_file is null) return;
        try { _file.SetDisplayValue(SourceRow(e.RowIndex), _columns[e.ColumnIndex], e.Value); _status.Text = "Modified — save to write changes"; }
        catch (Exception ex) { ShowError(ex); _grid.InvalidateCell(e.ColumnIndex, e.RowIndex); }
    }

    private void GridRowPostPaint(object? sender, DataGridViewRowPostPaintEventArgs e)
    {
        var text = SourceRow(e.RowIndex).ToString("N0");
        TextRenderer.DrawText(e.Graphics, text, _grid.RowHeadersDefaultCellStyle.Font, e.RowBounds,
            _grid.RowHeadersDefaultCellStyle.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
    }

    private int SourceRow(int visibleRow) => _visibleRows?[visibleRow] ?? visibleRow;

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_file?.IsDirty != true) return;
        var result = MessageBox.Show(this, "Save changes before closing?", "Unsaved changes", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        if (result == DialogResult.Cancel) e.Cancel = true;
        else if (result == DialogResult.Yes) SaveFile(false);
    }

    private static string? FindSchemaPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "WDBXEditor", "WDBXEditor", "Definitions", "WotLK 3.3.5 (12340).xml");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        return null;
    }

    private void ShowError(Exception? exception) => MessageBox.Show(this, exception?.Message ?? "Unknown error", "WoW Crucible", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
