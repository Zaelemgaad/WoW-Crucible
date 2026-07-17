using WoWCrucible.Core;

namespace WoWCrucible.App;

public sealed class PatchBuilderForm : Form
{
    private readonly DataGridView _grid = new FastDataGridView();
    private readonly Label _hint = new();
    private readonly List<PatchEntry> _entries = [];
    private readonly AppSettings _settings;
    private string? _existingPatch;
    private string? _requiredClientExecutableSha256;
    private PatchManifestPolicy? _policy;

    public PatchBuilderForm(AppSettings settings, IEnumerable<string>? initialPaths = null)
    {
        _settings = settings;
        Text = "WoW Crucible Patch MPQ Builder";
        Width = 1100;
        Height = 650;
        StartPosition = FormStartPosition.CenterParent;
        AllowDrop = true;

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new(8), WrapContents = false, AutoScroll = true };
        bar.Controls.Add(MakeButton("Add Files", (_, _) => AddFiles()));
        bar.Controls.Add(MakeButton("Add Folder", (_, _) => AddFolder()));
        bar.Controls.Add(MakeButton("Open Existing MPQ", (_, _) => OpenExistingPatch()));
        bar.Controls.Add(MakeButton("Bind Client EXE", (_, _) => BindClientExecutable()));
        bar.Controls.Add(MakeButton("Content Policy", (_, _) => EditPolicy()));
        bar.Controls.Add(MakeButton("Load Manifest", (_, _) => LoadManifest()));
        bar.Controls.Add(MakeButton("Save Manifest", (_, _) => SaveManifest()));
        bar.Controls.Add(MakeButton("Validate MPQ", (_, _) => ValidateArchive()));
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
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Archive", HeaderText = "Path inside MPQ (editable)", FillWeight = 34 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "PathStatus", HeaderText = "Path check", ReadOnly = true, FillWeight = 20 });
        _grid.CellEndEdit += (_, _) => RefreshPathAssessments();

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
            var entries = EntriesFromGrid();
            var unboundGlueXml = PatchManifestService.GetCompatibilityIssues(entries, _requiredClientExecutableSha256).FirstOrDefault(issue => issue.Code == "ProtectedGlueXmlUnbound");
            if (unboundGlueXml is not null && MessageBox.Show(this, unboundGlueXml.Message + "\n\nSave this unbound manifest anyway?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            using var dialog = new SaveFileDialog { InitialDirectory = Directory.Exists(_settings.ClientDataPath) ? _settings.ClientDataPath : string.Empty, Filter = "WoW Crucible patch manifest (*.crucible-patch.json)|*.crucible-patch.json|JSON (*.json)|*.json", FileName = "classless.crucible-patch.json" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            PatchManifestService.Save(dialog.FileName, Path.GetFileNameWithoutExtension(dialog.FileName), "patch-W.mpq", entries, _requiredClientExecutableSha256, _policy);
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
            _entries.Clear(); _entries.AddRange(manifest.Entries); _requiredClientExecutableSha256 = manifest.RequiredClientExecutableSha256; _policy = manifest.Policy; _existingPatch = null; RefreshGrid();
            _hint.Text = $"Loaded {manifest.Name}: {manifest.Entries.Count:N0} changed file(s), output {manifest.OutputFileName}." + (_requiredClientExecutableSha256 is null ? string.Empty : " Client executable hash is bound.");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void AddFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Select a folder tree to preserve inside the MPQ", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) AddPaths([dialog.SelectedPath]);
    }

    private void BindClientExecutable()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "WoW executable (Wow.exe)|Wow.exe|Executables (*.exe)|*.exe",
            FileName = File.Exists(_settings.ClientExecutablePath) ? Path.GetFileName(_settings.ClientExecutablePath) : "Wow.exe",
            InitialDirectory = File.Exists(_settings.ClientExecutablePath) ? Path.GetDirectoryName(_settings.ClientExecutablePath) : (Directory.Exists(_settings.ClientDataPath) ? Directory.GetParent(_settings.ClientDataPath)?.FullName : string.Empty)
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _requiredClientExecutableSha256 = PatchManifestService.ComputeExecutableSha256(dialog.FileName);
            _settings.ClientExecutablePath = dialog.FileName; _settings.Save();
            _hint.Text = $"Bound manifests to {Path.GetFileName(dialog.FileName)} SHA-256 {_requiredClientExecutableSha256}.";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void EditPolicy()
    {
        using var dialog = new ManifestPolicyForm(_policy);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _policy = dialog.Policy;
        _hint.Text = _policy is null ? "Manifest content policy cleared." : "Manifest content policy enabled; it will be enforced before save and build.";
    }

    private void ValidateArchive()
    {
        try
        {
            _grid.EndEdit();
            using var dialog = new OpenFileDialog { InitialDirectory = Directory.Exists(_settings.ClientDataPath) ? _settings.ClientDataPath : string.Empty, Filter = "MPQ archives (*.MPQ)|*.MPQ|All files (*.*)|*.*" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            var manifest = new PatchManifest(3, "Current Patch Builder", Path.GetFileName(dialog.FileName), EntriesFromGrid(), _requiredClientExecutableSha256, _policy);
            var validation = PatchManifestService.Validate(manifest, dialog.FileName);
            if (validation.Passed) MessageBox.Show(this, $"Validation passed. The archive exactly matches all {manifest.Entries.Count:N0} manifest paths, source sizes, and content policies.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            else MessageBox.Show(this, string.Join("\n", validation.Errors.Take(20).Select(error => $"• {error.Message}")), $"Validation failed — {validation.Errors.Count:N0} error(s)", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
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
            _grid.Rows.Add(entry.SourcePath, entry.ArchivePath, string.Empty);
        RefreshPathAssessments();
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
            var policyValidation = PatchManifestService.ValidateEntries(entries, _policy);
            if (!policyValidation.Passed) { MessageBox.Show(this, string.Join("\n", policyValidation.Errors.Take(12).Select(error => $"• {error.Message}")), "Manifest policy failed", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            var warnings = entries.Select(entry => (entry, check: PatchInputMapper.AssessArchivePath(entry.ArchivePath))).Where(item => item.check.HasWarning && !PatchInputMapper.NormalizeArchivePath(item.entry.ArchivePath).StartsWith("Interface\\GlueXML\\", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (warnings.Length > 0)
            {
                var examples = string.Join("\n", warnings.Take(6).Select(item => $"• {item.entry.ArchivePath}: {item.check.Message}"));
                if (warnings.Length > 6) examples += $"\n• …and {warnings.Length - 6:N0} more";
                if (MessageBox.Show(this, $"{warnings.Length:N0} archive path(s) need review:\n\n{examples}\n\nBuild anyway?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            }
            var compatibility = PatchManifestService.GetCompatibilityIssues(entries, _requiredClientExecutableSha256);
            var unboundGlueXml = compatibility.FirstOrDefault(issue => issue.Code == "ProtectedGlueXmlUnbound");
            if (unboundGlueXml is not null && MessageBox.Show(this, unboundGlueXml.Message + "\n\nBuild without an executable binding anyway?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
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
            ClientCacheInvalidationResult? cache = null;
            if (ClientPatchDeploymentService.IsInsideClientData(outputPath, _settings.ClientDataPath))
            {
                var clientRoot = Directory.GetParent(Path.GetFullPath(_settings.ClientDataPath))?.FullName
                    ?? throw new InvalidDataException("The configured client Data folder has no client root.");
                cache = ClientPatchDeploymentService.InvalidateCache(clientRoot);
            }
            var cacheText = cache is null ? "The output is outside the configured client Data folder; no client cache was changed."
                : cache.Existed ? $"Deleted the client Cache folder ({cache.DeletedFiles:N0} file(s), {cache.DeletedBytes:N0} bytes)."
                : "The client Cache folder was already absent.";
            _hint.Text = $"{(_existingPatch is null ? "Created" : "Updated")} {outputPath} with {entries.Length:N0} added/replaced file(s). {cacheText}";
            MessageBox.Show(this, $"Patch {(_existingPatch is null ? "created" : "updated")} successfully:\n{outputPath}\n\n{cacheText}\n\nA .bak copy preserves the previous archive.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { Cursor = Cursors.Default; }
    }

    private PatchEntry[] EntriesFromGrid() => _grid.Rows.Cast<DataGridViewRow>().Select(row => new PatchEntry(
        Convert.ToString(row.Cells[0].Value)!, Convert.ToString(row.Cells[1].Value)!)).ToArray();

    private void RefreshPathAssessments()
    {
        var warnings = 0;
        foreach (DataGridViewRow row in _grid.Rows)
        {
            try
            {
                var assessment = PatchInputMapper.AssessArchivePath(Convert.ToString(row.Cells[1].Value) ?? string.Empty);
                row.Cells[2].Value = assessment.Message;
                row.DefaultCellStyle.BackColor = assessment.HasWarning ? Color.MistyRose : _grid.DefaultCellStyle.BackColor;
                if (assessment.HasWarning) warnings++;
            }
            catch (Exception ex)
            {
                row.Cells[2].Value = ex.Message;
                row.DefaultCellStyle.BackColor = Color.MistyRose;
                warnings++;
            }
        }
        _hint.Text = $"{_grid.Rows.Count:N0} file(s), {warnings:N0} path warning(s). Review the complete source-to-MPQ mapping; internal paths are editable.";
    }

    private void OnDragEnter(object? sender, DragEventArgs e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
    private void OnDragDrop(object? sender, DragEventArgs e) { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths); }
}
