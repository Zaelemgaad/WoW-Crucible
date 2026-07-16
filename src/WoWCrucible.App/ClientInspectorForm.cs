using System.Text.Json;
using WoWCrucible.Core;

namespace WoWCrucible.App;

internal sealed class ClientInspectorForm : Form
{
    private readonly AppSettings _settings;
    private readonly TextBox _client = new() { Dock = DockStyle.Fill };
    private readonly TextBox _index = new() { Dock = DockStyle.Fill };
    private readonly Label _summary = new() { Dock = DockStyle.Fill, AutoSize = true, Padding = new(12), ForeColor = Color.FromArgb(55, 65, 81) };
    private readonly Label _guidance = new() { Dock = DockStyle.Fill, AutoSize = true, Padding = new(12), MaximumSize = new(1250, 0), ForeColor = Color.FromArgb(55, 65, 81) };
    private readonly Label _progressText = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill };
    private readonly DataGridView _archives = new FastDataGridView { Dock = DockStyle.Fill, ReadOnly = true, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None };
    private readonly DataGridView _looseFiles = new FastDataGridView { Dock = DockStyle.Fill, ReadOnly = true, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None };
    private readonly Button _indexButton = new() { Text = "Index or Resume", AutoSize = true };
    private readonly Button _cancelButton = new() { Text = "Cancel", AutoSize = true, Enabled = false };
    private ClientArchiveIndex? _loaded;
    private CancellationTokenSource? _operation;

    public ClientInspectorForm(AppSettings settings)
    {
        _settings = settings;
        Text = "Client Inspector · WoW Crucible"; Width = 1320; Height = 820; StartPosition = FormStartPosition.CenterParent;
        _client.Text = ClientRootFromSettings(); _index.Text = settings.ClientIndexPath;

        var paths = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Padding = new(10) };
        paths.ColumnStyles.Add(new(SizeType.AutoSize)); paths.ColumnStyles.Add(new(SizeType.Percent, 100)); paths.ColumnStyles.Add(new(SizeType.AutoSize)); paths.ColumnStyles.Add(new(SizeType.AutoSize));
        paths.Controls.Add(new Label { Text = "Client folder", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0); paths.Controls.Add(_client, 1, 0);
        paths.Controls.Add(Button("Browse…", ChooseClient), 2, 0);
        paths.Controls.Add(new Label { Text = "Index folder", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1); paths.Controls.Add(_index, 1, 1);
        paths.Controls.Add(Button("Browse…", ChooseIndex), 2, 1); paths.Controls.Add(Button("Load Existing", LoadExisting), 3, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new(10, 0, 10, 8) };
        _indexButton.Click += async (_, _) => await BuildIndex(); _cancelButton.Click += (_, _) => _operation?.Cancel();
        actions.Controls.Add(_indexButton); actions.Controls.Add(_cancelButton); actions.Controls.Add(Button("Open Archive", OpenSelectedArchive)); actions.Controls.Add(Button("Extract Selected…", async () => await ExtractSelected()));
        actions.Controls.Add(Button("Plan Client → Server", () => { using var form = new ClientServerPlanForm(_settings); form.ShowDialog(this); }));
        actions.Controls.Add(Button("Plan Client Fusion", () => { using var form = new ClientFusionForm(_settings); form.ShowDialog(this); }));
        var progressRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 34, ColumnCount = 2, Padding = new(10, 2, 10, 2) };
        progressRow.ColumnStyles.Add(new(SizeType.Percent, 100)); progressRow.ColumnStyles.Add(new(SizeType.AutoSize)); progressRow.Controls.Add(_progress, 0, 0); progressRow.Controls.Add(_progressText, 1, 0);

        _archives.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Scope", Width = 125 });
        _archives.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Archive", Width = 390 });
        _archives.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Files", Width = 90 });
        _archives.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Unresolved", Width = 90 });
        _archives.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "MPQ size", Width = 95 });
        _archives.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Content size", Width = 100 });
        _archives.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 150 });
        _archives.SelectionChanged += async (_, _) => await ExplainSelection();

        _looseFiles.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Scope", Width = 125 });
        _looseFiles.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Loose file", Width = 560 });
        _looseFiles.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Size", Width = 100 });
        _looseFiles.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Identity", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 220 });
        _looseFiles.SelectionChanged += (_, _) => ExplainLooseSelection();

        var info = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 500, Panel1MinSize = 250, Panel2MinSize = 120 };
        var inventoryTabs = new TabControl { Dock = DockStyle.Fill };
        var archivesPage = new TabPage("MPQ archives"); archivesPage.Controls.Add(_archives);
        var loosePage = new TabPage("Loose files"); loosePage.Controls.Add(_looseFiles);
        inventoryTabs.TabPages.Add(archivesPage); inventoryTabs.TabPages.Add(loosePage); info.Panel1.Controls.Add(inventoryTabs);
        var lower = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, AutoScroll = true };
        lower.RowStyles.Add(new(SizeType.AutoSize)); lower.RowStyles.Add(new(SizeType.AutoSize));
        lower.Controls.Add(_summary, 0, 0); lower.Controls.Add(_guidance, 0, 1); info.Panel2.Controls.Add(lower);
        Controls.Add(info); Controls.Add(progressRow); Controls.Add(actions); Controls.Add(paths);
        Shown += (_, _) => { if (File.Exists(Path.Combine(_index.Text, "client-index.json"))) LoadIndex(_index.Text); };
    }

    private string ClientRootFromSettings()
    {
        if (Directory.Exists(_settings.ClientDataPath) && Path.GetFileName(Path.TrimEndingDirectorySeparator(_settings.ClientDataPath)).Equals("Data", StringComparison.OrdinalIgnoreCase))
            return Directory.GetParent(_settings.ClientDataPath)?.FullName ?? string.Empty;
        return string.Empty;
    }

    private void ChooseClient()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose the folder containing the WoW client executable and Data folder", SelectedPath = Directory.Exists(_client.Text) ? _client.Text : string.Empty };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _client.Text = dialog.SelectedPath;
        if (string.IsNullOrWhiteSpace(_index.Text)) _index.Text = Path.Combine(dialog.SelectedPath, ".crucible-index");
    }

    private void ChooseIndex()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose where the resumable client index will be stored", SelectedPath = Directory.Exists(_index.Text) ? _index.Text : string.Empty };
        if (dialog.ShowDialog(this) == DialogResult.OK) _index.Text = dialog.SelectedPath;
    }

    private void LoadExisting()
    {
        try { LoadIndex(_index.Text); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not load client index", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private async Task BuildIndex()
    {
        if (!Directory.Exists(Path.Combine(_client.Text, "Data"))) { MessageBox.Show(this, "Choose the client root containing its Data folder.", "Client folder required", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (string.IsNullOrWhiteSpace(_index.Text)) { MessageBox.Show(this, "Choose an index folder. It may be on another drive.", "Index folder required", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        SetBusy(true); _operation = new();
        try
        {
            var progress = new Progress<ClientIndexProgress>(p => { _progress.Maximum = Math.Max(1, p.TotalArchives); _progress.Value = Math.Min(p.CompletedArchives, _progress.Maximum); _progressText.Text = $"{p.CompletedArchives:N0}/{p.TotalArchives:N0} · {p.Stage} · {p.ArchivePath}"; });
            var executable = File.Exists(_settings.ClientExecutablePath) && string.Equals(Path.GetDirectoryName(Path.GetFullPath(_settings.ClientExecutablePath)), Path.GetFullPath(_client.Text), StringComparison.OrdinalIgnoreCase) ? _settings.ClientExecutablePath : null;
            var index = await Task.Run(() => new ClientArchiveIndexService().Build(_client.Text, _index.Text, true, progress, _operation.Token, executablePath: executable));
            _settings.ClientDataPath = Path.Combine(_client.Text, "Data"); _settings.ClientIndexPath = _index.Text;
            if (index.Executable is not null) _settings.ClientExecutablePath = Path.GetFullPath(Path.Combine(index.ClientRoot, index.Executable.Path));
            _settings.Save(); LoadIndex(_index.Text);
        }
        catch (OperationCanceledException) { _progressText.Text = "Cancelled safely. Choose Index or Resume to continue."; }
        catch (Exception ex) { CrashLogger.Log("Client indexing failed", ex); MessageBox.Show(this, ex.Message, "Client indexing failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { _operation?.Dispose(); _operation = null; SetBusy(false); }
    }

    private void LoadIndex(string directory)
    {
        _loaded = ClientArchiveIndexService.Load(directory); _index.Text = Path.GetFullPath(directory); _client.Text = _loaded.ClientRoot;
        _archives.Rows.Clear(); _looseFiles.Rows.Clear();
        foreach (var archive in _loaded.Archives.OrderBy(a => a.Scope).ThenBy(a => a.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var row = _archives.Rows.Add(archive.Scope, archive.RelativePath, archive.PayloadFiles.ToString("N0"), archive.AnonymousFiles.ToString("N0"), FormatSize(archive.Length), FormatSize(archive.UncompressedBytes), archive.Error ?? (archive.Sha256 is null ? "Indexed · hash skipped" : "Indexed · SHA-256 ready"));
            _archives.Rows[row].Tag = archive;
            if (archive.Scope is ClientArchiveScope.InactiveLocale or ClientArchiveScope.Backup) _archives.Rows[row].DefaultCellStyle.ForeColor = Color.Gray;
            else if (archive.Scope == ClientArchiveScope.CustomSubdirectory) _archives.Rows[row].DefaultCellStyle.BackColor = Color.FromArgb(255, 247, 237);
            else if (archive.AnonymousFiles > 0) _archives.Rows[row].DefaultCellStyle.BackColor = Color.FromArgb(254, 242, 242);
        }
        var loose = _loaded.LooseFiles ?? [];
        foreach (var file in loose.OrderBy(f => f.Scope).ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var row = _looseFiles.Rows.Add(file.Scope, file.RelativePath, FormatSize(file.Length), file.Sha256 ?? "Metadata only");
            _looseFiles.Rows[row].Tag = file;
            if (file.Scope == ClientLooseFileScope.Volatile) _looseFiles.Rows[row].DefaultCellStyle.ForeColor = Color.Gray;
            else if (file.Scope == ClientLooseFileScope.Runtime) _looseFiles.Rows[row].DefaultCellStyle.BackColor = Color.FromArgb(239, 246, 255);
        }
        var active = _loaded.Archives.Count(a => a.Scope is not ClientArchiveScope.InactiveLocale and not ClientArchiveScope.Backup);
        var custom = _loaded.Archives.Count(a => a.Scope == ClientArchiveScope.CustomSubdirectory);
        _summary.Text = $"{_loaded.Name} · {(_loaded.Complete ? "complete" : "resumable partial")} · {_loaded.Archives.Count:N0} MPQs ({active:N0} active, {custom:N0} loader-specific, {_loaded.Archives.Count - active:N0} excluded backup/inactive locale) · {_loaded.Archives.Sum(a => a.PayloadFiles):N0} indexed payloads · {loose.Count:N0} loose files ({loose.Count(f => f.Scope == ClientLooseFileScope.Runtime):N0} runtime, {loose.Count(f => f.Scope == ClientLooseFileScope.AddOn):N0} AddOn) · locale {_loaded.ActiveLocale ?? "not proven"} · executable {_loaded.Executable?.Path ?? "not detected"}.";
        _settings.ClientIndexPath = _index.Text; _settings.Save();
    }

    private async Task ExplainSelection()
    {
        if (_loaded is null || _archives.SelectedRows.Count == 0 || _archives.SelectedRows[0].Tag is not ClientArchiveSummary archive) return;
        _guidance.Text = ScopeGuidance(archive.Scope) + " Reading indexed content…";
        try
        {
            var contentPath = Path.Combine(_index.Text, archive.ContentIndexFile);
            var content = await Task.Run(() => JsonSerializer.Deserialize<ArchiveContentIndex>(File.ReadAllText(contentPath)) ?? throw new InvalidDataException("Content index is invalid."));
            var files = content.Files.Where(f => !f.IsMetadata).ToArray();
            var dbc = files.Count(f => f.ArchivePath.StartsWith("DBFilesClient\\", StringComparison.OrdinalIgnoreCase));
            var ui = files.Count(f => f.ArchivePath.StartsWith("Interface\\", StringComparison.OrdinalIgnoreCase));
            var protectedUi = files.Count(f => f.ArchivePath.StartsWith("Interface\\GlueXML\\", StringComparison.OrdinalIgnoreCase));
            var textures = files.Count(f => Path.GetExtension(f.ArchivePath).Equals(".blp", StringComparison.OrdinalIgnoreCase));
            var models = files.Count(f => new[] { ".m2", ".wmo", ".skin" }.Contains(Path.GetExtension(f.ArchivePath), StringComparer.OrdinalIgnoreCase));
            var maps = files.Count(f => new[] { ".adt", ".wdt", ".wdl" }.Contains(Path.GetExtension(f.ArchivePath), StringComparer.OrdinalIgnoreCase));
            _guidance.Text = $"{ScopeGuidance(archive.Scope)} Content: {dbc:N0} DBCs · {ui:N0} UI files ({protectedUi:N0} protected login UI) · {textures:N0} textures · {models:N0} models · {maps:N0} map files · {archive.AnonymousFiles:N0} unresolved paths. " +
                (protectedUi > 0 ? "Protected GlueXML may require a matching executable patch; bind and verify the client EXE before reuse. " : string.Empty) +
                (archive.AnonymousFiles > 0 ? "Unresolved files must remain quarantined by archive and hash until their names are recovered. " : string.Empty) +
                "Extraction preserves the internal path and resumes past matching files.";
        }
        catch (Exception ex) { _guidance.Text = $"{ScopeGuidance(archive.Scope)} Content details unavailable: {ex.Message}"; }
    }

    private void OpenSelectedArchive()
    {
        if (_loaded is null || _archives.SelectedRows.Count == 0 || _archives.SelectedRows[0].Tag is not ClientArchiveSummary archive) return;
        using var browser = new MpqBrowserForm(_settings, Path.Combine(_loaded.ClientRoot, archive.RelativePath)); browser.ShowDialog(this);
    }

    private void ExplainLooseSelection()
    {
        if (_looseFiles.SelectedRows.Count == 0 || _looseFiles.SelectedRows[0].Tag is not ClientLooseFileSummary file) return;
        _guidance.Text = file.Scope switch
        {
            ClientLooseFileScope.Runtime => $"Runtime dependency: {file.RelativePath}. Keep executable/DLL identities with the client profile; custom rendering, launcher, or extension behavior may depend on this exact file. {(file.Sha256 is null ? "No content hash was recorded." : $"SHA-256 {file.Sha256}.")}",
            ClientLooseFileScope.AddOn => $"Loose AddOn file: {file.RelativePath}. This is active outside the MPQ stack and can change UI or exchange data with server-side systems. Compare it separately from archived Interface files.",
            ClientLooseFileScope.Configuration => $"Configuration file: {file.RelativePath}. It helps explain the active locale/client behavior but is installation- or user-specific and should not be copied into a public patch by default.",
            ClientLooseFileScope.Volatile => $"Volatile/generated file: {file.RelativePath}. It is inventoried for completeness but excluded from reusable-content decisions by default.",
            _ => $"Loose client file: {file.RelativePath}. It is outside the MPQ stack; inspect its role before declaring the client reproducible or portable."
        };
    }

    private async Task ExtractSelected()
    {
        if (_loaded is null || _archives.SelectedRows.Count == 0 || _archives.SelectedRows[0].Tag is not ClientArchiveSummary archive) return;
        using var dialog = new FolderBrowserDialog { Description = "Choose an empty or resumable extraction folder" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        SetBusy(true); _operation = new();
        try
        {
            var progress = new Progress<(int Done, int Total, string Path)>(p => { _progress.Maximum = Math.Max(1, p.Total); _progress.Value = Math.Min(p.Done, _progress.Maximum); _progressText.Text = $"{p.Done:N0}/{p.Total:N0} · {p.Path}"; });
            var result = await Task.Run(() => ClientArchiveIndexService.ExtractIndexed(_index.Text, archive.RelativePath, dialog.SelectedPath, resolvedOnly: true, progress: progress, cancellationToken: _operation.Token));
            _progressText.Text = $"Extracted {result.ExtractedFiles:N0}; resumed past {result.SkippedExistingFiles:N0}; selected {result.SelectedFiles:N0}.";
        }
        catch (OperationCanceledException) { _progressText.Text = "Cancelled safely; extraction can be resumed."; }
        catch (Exception ex) { CrashLogger.Log("Indexed extraction failed", ex); MessageBox.Show(this, ex.Message, "Extraction failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { _operation?.Dispose(); _operation = null; SetBusy(false); }
    }

    private void SetBusy(bool busy) { _indexButton.Enabled = !busy; _cancelButton.Enabled = busy; }
    private static Button Button(string text, Action action) { var button = new Button { Text = text, AutoSize = true }; button.Click += (_, _) => action(); return button; }
    private static string FormatSize(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.##} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.##} MB" : $"{bytes / 1024d:0.##} KB";
    private static string ScopeGuidance(ClientArchiveScope scope) => scope switch
    {
        ClientArchiveScope.RootData => "Root Data layer. Usually active, but filename precedence still matters; compare duplicates before flattening.",
        ClientArchiveScope.ActiveLocale => "Active locale layer. Reuse only for the matching client locale unless the content is proven language-neutral.",
        ClientArchiveScope.InactiveLocale => "Inactive locale layer. Excluded from the effective client view by default.",
        ClientArchiveScope.Cache => "Cache layer. Treat as generated or launcher-managed content until proven otherwise.",
        ClientArchiveScope.CustomSubdirectory => "Custom loader subdirectory. It is not stock MPQ behavior; preserve it as a separate override and expect executable/launcher coupling.",
        ClientArchiveScope.Backup => "Backup archive. Excluded from the effective client view and never a patch input by default.",
        _ => "Unknown archive scope."
    };
}
