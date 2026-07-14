using System.Diagnostics;
using WoWCrucible.Core;

namespace WoWCrucible.App;

public sealed class MainForm : Form
{
    private readonly DataGridView _grid = new FastDataGridView();
    private readonly ToolStripStatusLabel _status = new();
    private readonly ToolStripTextBox _search = new() { AutoSize = false, Width = 280 };
    private readonly System.Windows.Forms.Timer _searchTimer = new() { Interval = 250 };
    private readonly EditHistory _history = new();
    private readonly ToolStripComboBox _openFiles = new() { AutoSize = false, Width = 210, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ToolStripButton _decoded = new("Decoded: On") { CheckOnClick = true, Checked = true, DisplayStyle = ToolStripItemDisplayStyle.Text };
    private readonly List<(WdbcFile File, IReadOnlyList<DbcColumn> Columns)> _documents = [];
    private bool _activatingDocument;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly StartCenterControl _startCenter;
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
        KeyPreview = true;

        _startCenter = new StartCenterControl(_settings, OpenGuidedSpellWorkspace, OpenFile, () => OpenPatchBuilder(), () => { using var browser = new MpqBrowserForm(_settings); browser.ShowDialog(this); }, OpenLayeredDbcs, ConfigurePaths);

        var tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new(6, 4, 6, 4) };
        tools.Items.Add(Button("Home", (_, _) => ShowStartCenter()));
        tools.Items.Add(new ToolStripSeparator());
        tools.Items.Add(Button("Open DBC(s)", (_, _) => OpenFile()));
        tools.Items.Add(_openFiles);
        tools.Items.Add(Button("Close DBC", (_, _) => CloseCurrentFile()));
        tools.Items.Add(Button("Save", (_, _) => SaveFile(false)));
        tools.Items.Add(Button("Save As", (_, _) => SaveFile(true)));
        tools.Items.Add(new ToolStripSeparator());
        tools.Items.Add(Button("Undo", (_, _) => UndoEdit()));
        tools.Items.Add(Button("Redo", (_, _) => RedoEdit()));
        tools.Items.Add(_decoded);
        tools.Items.Add(new ToolStripSeparator());
        tools.Items.Add(Button("Spell Workspace", (_, _) => OpenSpellWorkspace()));
        tools.Items.Add(new ToolStripSeparator());
        tools.Items.Add(Button("New Row", (_, _) => AddRow()));
        tools.Items.Add(Button("Clone Row", (_, _) => CloneRow()));
        tools.Items.Add(Button("Clone Multiple", (_, _) => CloneMultipleRows()));
        tools.Items.Add(Button("Delete Rows", (_, _) => DeleteRows()));
        tools.Items.Add(new ToolStripSeparator());
        tools.Items.Add(Button("Build Patch MPQ", (_, _) => OpenPatchBuilder()));
        tools.Items.Add(Button("Browse/Extract MPQ", (_, _) => { using var browser = new MpqBrowserForm(_settings); browser.ShowDialog(this); }));
        tools.Items.Add(Button("Layered DBCs", (_, _) => OpenLayeredDbcs()));
        tools.Items.Add(Button("Sync to Core Data", (_, _) => SyncToCoreData()));
        tools.Items.Add(Button("Open Logs", (_, _) => CrashLogger.OpenDirectory()));
        tools.Items.Add(Button("Paths", (_, _) => ConfigurePaths()));
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
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (_decoded.Checked && EditDecodedValue(e.RowIndex, e.ColumnIndex)) return;
            if (IsSpellFile) OpenSpellWorkspace();
        };

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_status);
        Controls.Add(_grid);
        Controls.Add(_startCenter);
        Controls.Add(statusStrip);
        Controls.Add(tools);
        tools.Dock = DockStyle.Top;
        _grid.Visible = false;

        _search.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        _decoded.CheckedChanged += (_, _) => { _decoded.Text = _decoded.Checked ? "Decoded: On" : "Decoded: Off"; _grid.Invalidate(); };
        _openFiles.SelectedIndexChanged += (_, _) =>
        {
            if (!_activatingDocument && _openFiles.SelectedIndex >= 0) ActivateDocument(_openFiles.SelectedIndex);
        };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ApplyFilter(); };
        FormClosing += OnFormClosing;
        KeyDown += OnShortcutKeyDown;
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
            if (paths.Length == 1 && Path.GetExtension(paths[0]).Equals(".mpq", StringComparison.OrdinalIgnoreCase))
            {
                using var browser = new MpqBrowserForm(_settings, paths[0]); browser.ShowDialog(this);
            }
            else if (paths.All(path => File.Exists(path) && Path.GetExtension(path).Equals(".dbc", StringComparison.OrdinalIgnoreCase)))
                foreach (var path in paths) LoadFile(path);
            else
                OpenPatchBuilder(paths);
        };

        _schemaPath = FindSchemaPath(_settings.SchemaDefinitionPath);
        if (!string.IsNullOrWhiteSpace(initialFile) && File.Exists(initialFile))
            Shown += (_, _) =>
            {
                if (Path.GetExtension(initialFile).Equals(".mpq", StringComparison.OrdinalIgnoreCase))
                { using var browser = new MpqBrowserForm(_settings, initialFile); browser.ShowDialog(this); }
                else LoadFile(initialFile);
            };
    }

    private static ToolStripButton Button(string text, EventHandler action)
    {
        var button = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text };
        button.Click += action;
        return button;
    }

    private void OpenFile()
    {
        using var dialog = new OpenFileDialog { Multiselect = true, InitialDirectory = Directory.Exists(_settings.CoreDbcPath) ? _settings.CoreDbcPath : string.Empty, Filter = "WoW database files (*.dbc)|*.dbc|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            foreach (var path in dialog.FileNames) LoadFile(path);
    }

    private void ConfigurePaths()
    {
        using var form = new SettingsForm(_settings);
        if (form.ShowDialog(this) != DialogResult.OK) return;
        _schemaPath = FindSchemaPath(_settings.SchemaDefinitionPath); _startCenter.RefreshReadiness();
    }

    private void ShowStartCenter()
    {
        _grid.Visible = false; _startCenter.Visible = true; _startCenter.BringToFront(); _startCenter.RefreshReadiness();
        Text = _file is null ? "WoW Crucible — Start Center" : $"WoW Crucible — Home · {Path.GetFileName(_file.SourcePath)} remains open";
        _status.Text = _documents.Count == 0 ? "Choose a workflow to begin" : $"{_documents.Count:N0} DBC file(s) remain staged";
    }

    private void OpenGuidedSpellWorkspace()
    {
        var path = Directory.Exists(_settings.CoreDbcPath) ? Path.Combine(_settings.CoreDbcPath, "Spell.dbc") : string.Empty;
        if (!File.Exists(path))
        {
            using var dialog = new OpenFileDialog { InitialDirectory = Directory.Exists(_settings.CoreDbcPath) ? _settings.CoreDbcPath : string.Empty, Filter = "Spell.dbc|Spell.dbc|WoW database files (*.dbc)|*.dbc", FileName = "Spell.dbc", Title = "Select the build-12340 Spell.dbc" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            path = dialog.FileName;
        }
        LoadFile(path);
        if (!IsSpellFile) { MessageBox.Show(this, "The selected file is not a build-12340 Spell.dbc with 234 fields.", "WoW Crucible", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (_grid.RowCount > 0 && _grid.ColumnCount > 0) _grid.CurrentCell = _grid[0, 0];
        OpenSpellWorkspace();
    }

    private void OpenPatchBuilder(IEnumerable<string>? droppedPaths = null)
    {
        var initial = droppedPaths ?? (_file is null ? null : new[] { _file.SourcePath });
        using var builder = new PatchBuilderForm(_settings, initial);
        builder.ShowDialog(this);
    }

    private void OpenLayeredDbcs()
    {
        using var layered = new LayeredDbcForm(_settings, _schemaPath, LoadFile, paths =>
        {
            using var builder = new PatchBuilderForm(_settings, paths);
            builder.ShowDialog(this);
        });
        layered.ShowDialog(this);
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
        using var dialog = new FolderBrowserDialog { Description = "Select the core's data\\dbc directory", UseDescriptionForTitle = true, SelectedPath = Directory.Exists(_settings.CoreDbcPath) ? _settings.CoreDbcPath : string.Empty };
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
            path = Path.GetFullPath(path);
            var existing = _documents.FindIndex(document => Path.GetFullPath(document.File.SourcePath).Equals(path, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) { ActivateDocument(existing); return; }
            Cursor = Cursors.WaitCursor;
            var timer = Stopwatch.StartNew();
            var loaded = WdbcFile.Load(path);
            var schemas = _schemaPath is null ? DbcSchemaCatalog.CreateBuiltIn12340() : DbcSchemaCatalog.Load(_schemaPath);
            var table = Path.GetFileNameWithoutExtension(path);
            var columns = schemas.GetColumns(table, loaded.FieldCount);

            _documents.Add((loaded, columns));
            _openFiles.Items.Add(Path.GetFileName(path));
            ActivateDocument(_documents.Count - 1, timer);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { Cursor = Cursors.Default; }
    }

    private void ActivateDocument(int index, Stopwatch? loadTimer = null)
    {
        if (index < 0 || index >= _documents.Count) return;
        var document = _documents[index];
        _file = document.File;
        _columns = document.Columns;
        _history.Clear();
        _visibleRows = null;
        _search.Text = string.Empty;
        _activatingDocument = true;
        _openFiles.SelectedIndex = index;
        _activatingDocument = false;
        _grid.Columns.Clear();
        foreach (var column in _columns)
        {
            var table = Path.GetFileNameWithoutExtension(_file.SourcePath);
            var semantic = _file.RowCount > 0 ? DbcSemanticCatalog.Get(table, column.Index, _file, 0) : DbcSemanticCatalog.Get(table, column.Index);
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = $"field{column.Index}",
                HeaderText = semantic is null ? column.Name : $"{column.Name} — {semantic.Label}",
                Width = semantic?.Kind == SemanticKind.Flags ? 380 : semantic is not null ? 240 : column.Type == DbcValueType.StringOffset ? 220 : 105,
                ReadOnly = false,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        }
        _grid.RowCount = _file.RowCount;
        _startCenter.Visible = false; _grid.Visible = true; _grid.BringToFront();
        loadTimer?.Stop();
        Text = $"WoW Crucible — {Path.GetFileName(_file.SourcePath)} · {_documents.Count:N0} open";
        _status.Text = loadTimer is null
            ? $"{_file.RowCount:N0} rows · {_file.FieldCount:N0} fields"
            : $"{_file.RowCount:N0} rows · {_file.FieldCount:N0} fields · {_file.StringTableSize:N0} string bytes · loaded in {loadTimer.ElapsedMilliseconds:N0} ms";
    }

    private void CloseCurrentFile()
    {
        var index = _openFiles.SelectedIndex;
        if (index < 0 || _file is null) return;
        if (_file.IsDirty)
        {
            var result = MessageBox.Show(this, $"Save changes to {Path.GetFileName(_file.SourcePath)} before closing it?", "Unsaved changes", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (result == DialogResult.Cancel) return;
            if (result == DialogResult.Yes) SaveFile(false);
            if (_file.IsDirty) return;
        }
        _documents.RemoveAt(index);
        _openFiles.Items.RemoveAt(index);
        if (_documents.Count > 0) ActivateDocument(Math.Min(index, _documents.Count - 1));
        else
        {
            _file = null; _columns = []; _visibleRows = null;
            _grid.Columns.Clear(); _grid.RowCount = 0;
            ShowStartCenter();
        }
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
            var table = Path.GetFileNameWithoutExtension(file.SourcePath);
            var decoded = _decoded.Checked;
            var semanticColumns = decoded ? DbcSemanticCatalog.GetColumns(table).Where(index => index >= 0 && index < columns.Count).Select(index => columns[index]).ToArray() : [];
            var matches = await Task.Run(() => Enumerable.Range(0, file.RowCount).AsParallel().AsOrdered()
                .Where(row => file.RowContains(row, query, columns) || decoded && semanticColumns.Any(column =>
                    DbcSemanticCatalog.Get(table, column.Index, file, row)?.Format(file.GetRaw(row, column)).Contains(query, StringComparison.OrdinalIgnoreCase) == true)).ToArray());
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
        try
        {
            if (saveAs)
            {
                _file.SaveAs(path);
                var index = _openFiles.SelectedIndex;
                if (index >= 0) _openFiles.Items[index] = Path.GetFileName(path);
                Text = $"WoW Crucible — {Path.GetFileName(path)} · {_documents.Count:N0} open";
            }
            else _file.Save(path);
            _status.Text = $"Saved {path} (backup: {path}.bak)";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private DbcColumn? IdColumn => _columns.FirstOrDefault(column => column.IsIndex) ?? _columns.FirstOrDefault();
    private bool IsSpellFile => _file is not null && Path.GetFileNameWithoutExtension(_file.SourcePath).Equals("Spell", StringComparison.OrdinalIgnoreCase) && _file.FieldCount == 234;

    private void OpenSpellWorkspace()
    {
        if (_file is null || _grid.CurrentCell is null) return;
        if (!IsSpellFile)
        {
            MessageBox.Show(this, "The Spell Workspace is available when Spell.dbc is open.", "WoW Crucible", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var editor = new SpellEditorForm(_file, SourceRow(_grid.CurrentCell.RowIndex), _columns, ApplyEditorValue);
        editor.ShowDialog(this);
        _grid.Invalidate();
    }

    private void ApplyEditorValue(int row, DbcColumn column, object? value)
    {
        if (_file is null) return;
        var before = _file.GetRaw(row, column);
        _file.SetDisplayValue(row, column, value);
        _history.Record(row, column, before, _file.GetRaw(row, column));
        _status.Text = $"Modified {column.Name} — Ctrl+Z to undo";
    }

    private void AddRow()
    {
        if (_file is null) return;
        try
        {
            ClearFilterForStructureChange();
            var row = _file.AddBlankRow(IdColumn);
            _history.Clear();
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
            _history.Clear();
            RefreshRows(row);
            _status.Text = $"Cloned source row {source:N0} into row {row:N0} with a new ID";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void CloneMultipleRows()
    {
        if (_file is null || _grid.CurrentCell is null) return;
        var count = PromptForCloneCount();
        if (count is null) return;
        try
        {
            var source = SourceRow(_grid.CurrentCell.RowIndex);
            ClearFilterForStructureChange();
            var firstRow = _file.CloneRows(source, count.Value, IdColumn);
            _history.Clear();
            RefreshRows(firstRow);
            _status.Text = $"Created {count.Value:N0} clones in one batch, starting at row {firstRow:N0}";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private int? PromptForCloneCount()
    {
        using var dialog = new Form { Text = "Clone multiple rows", Width = 390, Height = 170, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var label = new Label { Text = "Number of copies to create:", AutoSize = true, Location = new(16, 18) };
        var count = new NumericUpDown { Minimum = 2, Maximum = 100_000, Value = 100, ThousandsSeparator = true, Width = 180, Location = new(18, 48) };
        var create = new Button { Text = "Create clones", DialogResult = DialogResult.OK, AutoSize = true, Location = new(218, 88) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Location = new(140, 88) };
        dialog.Controls.AddRange([label, count, create, cancel]);
        dialog.AcceptButton = create;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog(this) == DialogResult.OK ? decimal.ToInt32(count.Value) : null;
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
            _history.Clear();
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
        var row = SourceRow(e.RowIndex);
        var column = _columns[e.ColumnIndex];
        var semantic = _decoded.Checked ? DbcSemanticCatalog.Get(Path.GetFileNameWithoutExtension(_file.SourcePath), column.Index, _file, row) : null;
        e.Value = semantic?.Format(_file.GetRaw(row, column)) ?? _file.GetDisplayValue(row, column);
    }

    private void GridCellValuePushed(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (_file is null) return;
        try
        {
            var row = SourceRow(e.RowIndex);
            var column = _columns[e.ColumnIndex];
            var before = _file.GetRaw(row, column);
            var semantic = _decoded.Checked ? DbcSemanticCatalog.Get(Path.GetFileNameWithoutExtension(_file.SourcePath), column.Index, _file, row) : null;
            if (semantic is null) _file.SetDisplayValue(row, column, e.Value);
            else _file.SetRaw(row, column, semantic.Parse(Convert.ToString(e.Value) ?? string.Empty));
            _history.Record(row, column, before, _file.GetRaw(row, column));
            _status.Text = "Modified — Ctrl+Z to undo, save to write changes";
        }
        catch (Exception ex) { ShowError(ex); _grid.InvalidateCell(e.ColumnIndex, e.RowIndex); }
    }

    private bool EditDecodedValue(int visibleRow, int columnIndex)
    {
        if (_file is null) return false;
        var row = SourceRow(visibleRow); var column = _columns[columnIndex];
        var semantic = DbcSemanticCatalog.Get(Path.GetFileNameWithoutExtension(_file.SourcePath), column.Index, _file, row);
        if (semantic is null) return false;
        var before = _file.GetRaw(row, column);
        using var dialog = new DecodedValueForm(semantic, before);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Value == before) return true;
        _file.SetRaw(row, column, dialog.Value); _history.Record(row, column, before, dialog.Value);
        _grid.InvalidateCell(columnIndex, visibleRow); _status.Text = $"Modified {semantic.Label} — Ctrl+Z to undo";
        return true;
    }

    private void GridRowPostPaint(object? sender, DataGridViewRowPostPaintEventArgs e)
    {
        var text = SourceRow(e.RowIndex).ToString("N0");
        TextRenderer.DrawText(e.Graphics, text, _grid.RowHeadersDefaultCellStyle.Font, e.RowBounds,
            _grid.RowHeadersDefaultCellStyle.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
    }

    private int SourceRow(int visibleRow) => _visibleRows?[visibleRow] ?? visibleRow;

    private void UndoEdit()
    {
        if (_file is null) return;
        var edit = _history.Undo(_file);
        if (edit is null) { _status.Text = "Nothing to undo"; return; }
        _grid.Invalidate();
        _status.Text = $"Undid {edit.Description}";
    }

    private void RedoEdit()
    {
        if (_file is null) return;
        var edit = _history.Redo(_file);
        if (edit is null) { _status.Text = "Nothing to redo"; return; }
        _grid.Invalidate();
        _status.Text = $"Redid {edit.Description}";
    }

    private void OnShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.Control) return;
        if (e.KeyCode == Keys.Z) UndoEdit();
        else if (e.KeyCode == Keys.Y) RedoEdit();
        else return;
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        var dirty = _documents.Where(document => document.File.IsDirty).ToArray();
        if (dirty.Length == 0) return;
        var result = MessageBox.Show(this, $"Save all {dirty.Length:N0} modified DBC file(s) before closing?", "Unsaved changes", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        if (result == DialogResult.Cancel) e.Cancel = true;
        else if (result == DialogResult.Yes)
        {
            foreach (var document in dirty)
            {
                try { document.File.Save(document.File.SourcePath); }
                catch (Exception ex) { ShowError(ex); e.Cancel = true; return; }
            }
        }
    }

    private static string? FindSchemaPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath)) return Path.GetFullPath(configuredPath);
        const string fileName = "WotLK 3.3.5 (12340).xml";
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            foreach (var relative in new[] { Path.Combine("Definitions", fileName), Path.Combine("WDBXEditor", "WDBXEditor", "Definitions", fileName), Path.Combine("WDBX (wow edit)", "Definitions", fileName) })
            {
                var candidate = Path.Combine(directory.FullName, relative);
                if (File.Exists(candidate)) return candidate;
            }
            directory = directory.Parent;
        }
        return null;
    }

    private void ShowError(Exception? exception)
    {
        CrashLogger.Log("Handled UI error", exception);
        MessageBox.Show(this, $"{exception?.Message ?? "Unknown error"}\n\nDetails were written to:\n{CrashLogger.LogDirectory}", "WoW Crucible", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
