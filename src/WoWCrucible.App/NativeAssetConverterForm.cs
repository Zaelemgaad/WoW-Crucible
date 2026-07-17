using WoWCrucible.Core;

namespace WoWCrucible.App;

public sealed class NativeAssetConverterForm : Form
{
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    private readonly TextBox _details = new() { Dock = DockStyle.Bottom, Height = 150, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly M2PreviewControl _modelPreview = new();
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 32, Padding = new(8), Text = "Add modern M2/WMO files or a folder. Analysis never modifies source assets." };
    private readonly List<AssetInspection> _assets = [];

    public NativeAssetConverterForm()
    {
        Text = "Native Modern → WoW 3.3.5 Asset Converter"; Width = 1150; Height = 720; StartPosition = FormStartPosition.CenterParent; AllowDrop = true;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new(8), WrapContents = false };
        bar.Controls.Add(Button("Add Files", (_, _) => AddFiles()));
        bar.Controls.Add(Button("Add Folder", (_, _) => AddFolder()));
        bar.Controls.Add(Button("Remove Selected", (_, _) => RemoveSelected()));
        bar.Controls.Add(Button("Create Conversion Workspace", (_, _) => CreateWorkspace()));
        bar.Controls.Add(new Label { Text = "Native conversion is enabled section-by-section only after loss and structure validation.", AutoSize = true, ForeColor = Color.DimGray, Margin = new(14, 7, 0, 0) });
        _grid.Columns.Add("File", "Source asset"); _grid.Columns.Add("Format", "Format"); _grid.Columns.Add("Compatibility", "3.3.5 status"); _grid.Columns.Add("Version", "Version"); _grid.Columns.Add("Dependencies", "Dependencies");
        _grid.SelectionChanged += (_, _) => ShowDetails();
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2, SplitterDistance = 720, Panel2MinSize = 300 };
        split.Panel1.Controls.Add(_grid); split.Panel2.Controls.Add(_modelPreview);
        Controls.Add(split); Controls.Add(_details); Controls.Add(_status); Controls.Add(bar);
        DragEnter += OnDragEnter; DragDrop += OnDragDrop; _grid.AllowDrop = true; _grid.DragEnter += OnDragEnter; _grid.DragDrop += OnDragDrop;
    }

    private static Button Button(string text, EventHandler click) { var button = new Button { Text = text, AutoSize = true, Height = 28, Margin = new(3, 0, 6, 0) }; button.Click += click; return button; }

    private void AddFiles()
    {
        using var dialog = new OpenFileDialog { Multiselect = true, Filter = "WoW models (*.m2;*.wmo)|*.m2;*.wmo|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) == DialogResult.OK) _ = Analyze(dialog.FileNames);
    }

    private void AddFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose a folder containing extracted M2/WMO assets", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) _ = Analyze(Directory.EnumerateFiles(dialog.SelectedPath, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).Equals(".m2", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".wmo", StringComparison.OrdinalIgnoreCase)));
    }

    private async Task Analyze(IEnumerable<string> paths)
    {
        try
        {
            var additions = paths.Select(Path.GetFullPath).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(path => _assets.All(asset => !asset.Path.Equals(path, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (additions.Length == 0) return;
            UseWaitCursor = true; _status.Text = $"Inspecting {additions.Length:N0} asset(s)…";
            var inspected = await Task.Run(() => additions.Select(NativeAssetConversionService.Inspect).ToArray());
            _assets.AddRange(inspected); RefreshGrid();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { UseWaitCursor = false; }
    }

    private void RefreshGrid()
    {
        _grid.Rows.Clear();
        foreach (var asset in _assets) _grid.Rows.Add(asset.Path, asset.Format, asset.Compatibility, asset.Version?.ToString() ?? "—", asset.Dependencies.Count);
        _status.Text = $"{_assets.Count:N0} asset(s) · {_assets.Count(asset => asset.Compatibility == AssetCompatibility.AlreadyWotlk335):N0} already compatible · {_assets.Count(asset => asset.Compatibility == AssetCompatibility.RequiresNativeConversion):N0} require conversion · {_assets.Count(asset => asset.Compatibility is AssetCompatibility.Invalid or AssetCompatibility.Unsupported):N0} blocked.";
        ShowDetails();
    }

    private void ShowDetails()
    {
        if (_grid.CurrentRow is null || _grid.CurrentRow.Index < 0 || _grid.CurrentRow.Index >= _assets.Count) { _details.Clear(); _modelPreview.ClearPreview(); return; }
        var asset = _assets[_grid.CurrentRow.Index];
        _details.Text = $"SHA-256: {asset.Sha256}\r\nMagic/version: {asset.Magic} / {asset.Version?.ToString() ?? "unknown"}\r\nChunks: {string.Join(", ", asset.Chunks.Select(chunk => $"{chunk.Id} ({chunk.Size:N0})"))}\r\n\r\n{string.Join("\r\n", asset.Findings.Select(finding => "• " + finding))}";
        if (asset.Format == AssetFormat.M2 && asset.Compatibility == AssetCompatibility.AlreadyWotlk335) _modelPreview.LoadModel(asset.Path);
        else _modelPreview.ClearPreview(asset.Format == AssetFormat.M2 ? "Conversion is required before this model can be rendered by the 3.3.5 preview." : "WMO rendering will follow the native M2 preview pipeline.");
    }

    private void RemoveSelected()
    {
        foreach (var index in _grid.SelectedRows.Cast<DataGridViewRow>().Select(row => row.Index).OrderDescending()) _assets.RemoveAt(index);
        RefreshGrid();
    }

    private void CreateWorkspace()
    {
        if (_assets.Count == 0) { MessageBox.Show(this, "Add at least one M2 or WMO asset first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        using var dialog = new FolderBrowserDialog { Description = "Choose a new or empty native conversion workspace folder", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var result = NativeAssetConversionService.CreateWorkspace(_assets.Select(asset => asset.Path), dialog.SelectedPath);
            MessageBox.Show(this, $"Conversion workspace created:\n{result.RootPath}\n\nCompatible: {result.CompatibleAssets:N0}\nRequire conversion: {result.ConversionRequired:N0}\nBlocked: {result.BlockedAssets:N0}\n\nSources were copied into hash-named immutable snapshots. See conversion-report.json for the complete downgrade plan.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void OnDragEnter(object? sender, DragEventArgs e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths) return;
        var files = paths.SelectMany(path => Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories) : [path])
            .Where(path => Path.GetExtension(path).Equals(".m2", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".wmo", StringComparison.OrdinalIgnoreCase));
        _ = Analyze(files);
    }
}
