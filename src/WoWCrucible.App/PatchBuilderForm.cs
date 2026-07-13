using WoWCrucible.Core;

namespace WoWCrucible.App;

public sealed class PatchBuilderForm : Form
{
    private readonly DataGridView _grid = new FastDataGridView();
    private readonly Label _hint = new();
    private readonly List<PatchEntry> _entries = [];
    private readonly AppSettings _settings;
    private string? _existingPatch;

    public PatchBuilderForm(AppSettings settings, IEnumerable<string>? initialPaths = null)
    {
        _settings = settings;
        Text = "WoW Crucible Patch MPQ Builder";
        Width = 1100;
        Height = 650;
        StartPosition = FormStartPosition.CenterParent;
        AllowDrop = true;

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new(8), WrapContents = false };
        bar.Controls.Add(MakeButton("Add Files", (_, _) => AddFiles()));
        bar.Controls.Add(MakeButton("Add Folder", (_, _) => AddFolder()));
        bar.Controls.Add(MakeButton("Open Existing MPQ", (_, _) => OpenExistingPatch()));
        bar.Controls.Add(MakeButton("Load Manifest", (_, _) => LoadManifest()));
        bar.Controls.Add(MakeButton("Save Manifest", (_, _) => SaveManifest()));
        bar.Controls.Add(MakeButton("Remove Selected", (_, _) => RemoveSelected()));
        bar.Controls.Add(MakeButton("Build MPQ", (_, _) => BuildPatch()));

        _hint.Dock = DockStyle.Bottom;
        _hint.Height = 34;
        _hint.Padding = new(8);
        _hint.Text = "Build a tiny patch containing only these files. Large base/mod archives are immutable sources, not update targets.";

        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Source", HeaderText = "Source file", ReadOnly = true, FillWeight = 58 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Archive", HeaderText = "Path inside MPQ", FillWeight = 42 });

        Controls.Add(_grid);
        Controls.Add(_hint);
        Controls.Add(bar);
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        _grid.DragEnter += OnDragEnter;
        _grid.DragDrop += OnDragDrop;
        _grid.AllowDrop = true;
        if (initialPaths is not null) Shown += (_, _) => AddPaths(initialPaths);
    }

    private static Button MakeButton(string text, EventHandler click)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 28, Margin = new(3, 0, 6, 0) };
        button.Click += click;
        return button;
    }

    private void AddFiles()
    {
        using var dialog = new OpenFileDialog { Multiselect = true, InitialDirectory = Directory.Exists(_settings.CoreDbcPath) ? _settings.CoreDbcPath : string.Empty, Filter = "Patch files (*.dbc;*.blp;*.m2;*.wmo;*.adt)|*.dbc;*.blp;*.m2;*.wmo;*.adt|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) == DialogResult.OK) AddPaths(dialog.FileNames);
    }

    private void OpenExistingPatch()
    {
        using var dialog = new OpenFileDialog { InitialDirectory = Directory.Exists(_settings.ClientDataPath) ? _settings.ClientDataPath : string.Empty, Filter = "MPQ patch (*.MPQ)|*.MPQ|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _existingPatch = dialog.FileName;
        _hint.Text = $"Updating {Path.GetFileName(_existingPatch)}. Added files replace matching internal paths; all other existing files remain intact.";
    }

    private void SaveManifest()
    {
        try
        {
            _grid.EndEdit();
            using var dialog = new SaveFileDialog { InitialDirectory = Directory.Exists(_settings.ClientDataPath) ? _settings.ClientDataPath : string.Empty, Filter = "WoW Crucible patch manifest (*.crucible-patch.json)|*.crucible-patch.json|JSON (*.json)|*.json", FileName = "classless.crucible-patch.json" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            PatchManifestService.Save(dialog.FileName, Path.GetFileNameWithoutExtension(dialog.FileName), "patch-W.mpq", EntriesFromGrid());
            _hint.Text = $"Saved manifest: {dialog.FileName}";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void LoadManifest()
    {
        try
        {
            using var dialog = new OpenFileDialog { InitialDirectory = Directory.Exists(_settings.ClientDataPath) ? _settings.ClientDataPath : string.Empty, Filter = "WoW Crucible patch manifest (*.crucible-patch.json)|*.crucible-patch.json|JSON (*.json)|*.json" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            var manifest = PatchManifestService.Load(dialog.FileName);
            _entries.Clear(); _entries.AddRange(manifest.Entries); _existingPatch = null; RefreshGrid();
            _hint.Text = $"Loaded {manifest.Name}: {manifest.Entries.Count:N0} changed file(s), output {manifest.OutputFileName}.";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void AddFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Select a folder tree to preserve inside the MPQ", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) AddPaths([dialog.SelectedPath]);
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        try
        {
            foreach (var entry in PatchInputMapper.Map(paths))
            {
                var existing = _entries.FindIndex(item => item.ArchivePath.Equals(entry.ArchivePath, StringComparison.OrdinalIgnoreCase));
                if (existing >= 0) _entries[existing] = entry; else _entries.Add(entry);
            }
            RefreshGrid();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void RefreshGrid()
    {
        _grid.Rows.Clear();
        foreach (var entry in _entries.OrderBy(entry => entry.ArchivePath, StringComparer.OrdinalIgnoreCase))
            _grid.Rows.Add(entry.SourcePath, entry.ArchivePath);
        _hint.Text = $"{_entries.Count:N0} file(s). Edit any internal path if needed, then build the patch MPQ.";
    }

    private void RemoveSelected()
    {
        var sources = _grid.SelectedRows.Cast<DataGridViewRow>().Select(row => Convert.ToString(row.Cells[0].Value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _entries.RemoveAll(entry => sources.Contains(entry.SourcePath));
        RefreshGrid();
    }

    private void BuildPatch()
    {
        try
        {
            _grid.EndEdit();
            var entries = EntriesFromGrid();
            var outputPath = _existingPatch;
            if (outputPath is null)
            {
                using var dialog = new SaveFileDialog { InitialDirectory = Directory.Exists(_settings.ClientDataPath) ? _settings.ClientDataPath : string.Empty, Filter = "MPQ patch (*.MPQ)|*.MPQ", FileName = "patch-W.mpq" };
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                outputPath = dialog.FileName;
            }
            Cursor = Cursors.WaitCursor;
            var service = new PatchArchiveService();
            if (_existingPatch is null) service.Create(outputPath, entries); else service.Update(outputPath, entries);
            _hint.Text = $"{(_existingPatch is null ? "Created" : "Updated")} {outputPath} with {entries.Length:N0} added/replaced file(s).";
            MessageBox.Show(this, $"Patch {(_existingPatch is null ? "created" : "updated")} successfully:\n{outputPath}\n\nA .bak copy preserves the previous archive.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { Cursor = Cursors.Default; }
    }

    private PatchEntry[] EntriesFromGrid() => _grid.Rows.Cast<DataGridViewRow>().Select(row => new PatchEntry(
        Convert.ToString(row.Cells[0].Value)!, Convert.ToString(row.Cells[1].Value)!)).ToArray();

    private void OnDragEnter(object? sender, DragEventArgs e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
    private void OnDragDrop(object? sender, DragEventArgs e) { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths); }
}
