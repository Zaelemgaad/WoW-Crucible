using System.Diagnostics;
using WoWCrucible.Core;

namespace WoWCrucible.App;

public sealed class MpqBrowserForm : Form
{
    private readonly AppSettings _settings;
    private readonly DataGridView _grid = new FastDataGridView();
    private readonly TextBox _search = new() { Width = 280, PlaceholderText = "Filter paths…" };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 30, Padding = new(8) };
    private IReadOnlyList<MpqFileEntry> _all = [];
    private IReadOnlyList<MpqFileEntry> _visible = [];
    private string? _archivePath;
    private CancellationTokenSource? _operation;

    public MpqBrowserForm(AppSettings settings, string? initialArchive = null)
    {
        _settings = settings; Text = "WoW Crucible MPQ Browser"; Width = 1200; Height = 760; StartPosition = FormStartPosition.CenterParent;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 45, Padding = new(8), WrapContents = false };
        bar.Controls.Add(Button("Open MPQ", async (_, _) => await ChooseArchive()));
        bar.Controls.Add(Button("Extract Selected", async (_, _) => await ExtractSelected()));
        bar.Controls.Add(Button("Extract All", async (_, _) => await ExtractFiles(_all)));
        bar.Controls.Add(Button("Cancel", (_, _) => _operation?.Cancel()));
        bar.Controls.Add(new Label { Text = " Search:", AutoSize = true, Padding = new(4, 6, 0, 0) }); bar.Controls.Add(_search);
        _grid.Dock = DockStyle.Fill; _grid.ReadOnly = true; _grid.VirtualMode = true; _grid.AllowUserToAddRows = false; _grid.AllowUserToDeleteRows = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _grid.MultiSelect = true; _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Path", HeaderText = "Path inside MPQ", FillWeight = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Size", HeaderText = "Size", FillWeight = 12 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Packed", HeaderText = "Packed", FillWeight = 12 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Ratio", HeaderText = "Ratio", FillWeight = 8 });
        _grid.CellValueNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _visible.Count) return;
            var entry = _visible[e.RowIndex];
            e.Value = e.ColumnIndex switch { 0 => entry.ArchivePath, 1 => FormatSize(entry.Size), 2 => FormatSize(entry.CompressedSize), 3 => entry.Size == 0 ? "—" : $"{100d * entry.CompressedSize / entry.Size:0.#}%", _ => null };
        };
        _search.TextChanged += (_, _) => ApplyFilter();
        Controls.Add(_grid); Controls.Add(_status); Controls.Add(bar);
        FormClosing += (_, _) => _operation?.Cancel();
        if (!string.IsNullOrWhiteSpace(initialArchive)) Shown += async (_, _) => await LoadArchive(initialArchive);
    }

    private static Button Button(string text, EventHandler click) { var button = new Button { Text = text, AutoSize = true, Height = 28, Margin = new(3, 0, 5, 0) }; button.Click += click; return button; }

    private async Task ChooseArchive()
    {
        using var dialog = new OpenFileDialog { InitialDirectory = Directory.Exists(_settings.ClientDataPath) ? _settings.ClientDataPath : string.Empty, Filter = "MPQ archives (*.MPQ)|*.MPQ|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) == DialogResult.OK) await LoadArchive(dialog.FileName);
    }

    private async Task LoadArchive(string path)
    {
        try
        {
            UseWaitCursor = true; _status.Text = $"Indexing {Path.GetFileName(path)}…";
            var timer = Stopwatch.StartNew();
            var files = await Task.Run(() => new PatchArchiveService().ListFiles(path));
            timer.Stop(); _archivePath = path; _all = files; ApplyFilter();
            Text = $"WoW Crucible MPQ Browser — {Path.GetFileName(path)}";
            _status.Text = $"{files.Count:N0} named files indexed in {timer.ElapsedMilliseconds:N0} ms · {path}";
        }
        catch (Exception ex) { CrashLogger.Log("MPQ browser open failed", ex); MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { UseWaitCursor = false; }
    }

    private void ApplyFilter()
    {
        var query = _search.Text.Trim();
        _visible = query.Length == 0 ? _all : _all.Where(entry => entry.ArchivePath.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        _grid.RowCount = _visible.Count; _grid.Invalidate();
        if (_archivePath is not null) _status.Text = $"{_visible.Count:N0} of {_all.Count:N0} named files · {_archivePath}";
    }

    private async Task ExtractSelected()
    {
        var selected = _grid.SelectedRows.Cast<DataGridViewRow>().Select(row => row.Index).Where(index => index >= 0 && index < _visible.Count)
            .Select(index => _visible[index]).ToArray();
        await ExtractFiles(selected);
    }

    private async Task ExtractFiles(IEnumerable<MpqFileEntry> files)
    {
        if (_archivePath is null) return;
        var entries = files.ToArray(); if (entries.Length == 0) return;
        using var dialog = new FolderBrowserDialog { Description = $"Extract {entries.Length:N0} file(s) to…", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _operation?.Dispose(); _operation = new CancellationTokenSource();
        var progress = new Progress<(int Done, int Total, string Path)>(value => _status.Text = $"Extracting {value.Done:N0}/{value.Total:N0}: {value.Path}");
        try
        {
            UseWaitCursor = true;
            await Task.Run(() => new PatchArchiveService().Extract(_archivePath, dialog.SelectedPath, entries, progress, _operation.Token));
            _status.Text = $"Extracted {entries.Length:N0} file(s) to {dialog.SelectedPath}";
        }
        catch (OperationCanceledException) { _status.Text = "Extraction canceled."; }
        catch (Exception ex) { CrashLogger.Log("MPQ extraction failed", ex); MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { UseWaitCursor = false; }
    }

    private static string FormatSize(long bytes) => bytes switch { >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB", >= 1L << 20 => $"{bytes / (double)(1L << 20):0.##} MB", >= 1L << 10 => $"{bytes / (double)(1L << 10):0.##} KB", _ => $"{bytes:N0} B" };
}
