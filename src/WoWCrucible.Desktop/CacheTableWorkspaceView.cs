using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class CacheTableWorkspaceView : UserControl
{
    private readonly TextBox _cachePath = new() { PlaceholderText = "Client cache *.wdb or Cataclysm *.adb" };
    private readonly TextBox _definitionPath = new() { PlaceholderText = "Optional WDB.xml / wdb-definitions.xml / adb-definitions.xml" };
    private readonly TextBox _search = new() { PlaceholderText = "Search record ID, field name, or decoded value…" };
    private readonly ListBox _records = new();
    private readonly TextBlock _summary = Status("Open or drop a WDB client cache. Crucible never modifies the source cache.");
    private readonly TextBlock _detail = Status("Select a record to inspect every decoded field, byte offset, and unresolved remainder.");
    private WowCacheTable? _wdbTable;
    private WowAdbTable? _adbTable;
    private IReadOnlyList<CacheRow> _rows = [];
    private int _loadRequest;

    public event EventHandler? BackRequested;

    public CacheTableWorkspaceView()
    {
        _definitionPath.Text = DiscoverPreferredDefinition();
        var back = new Button { Content = "← Editor" }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var open = Accent("Open cache"); open.Click += async (_, _) => await OpenSelectedAsync();
        var pickCache = new Button { Content = "Browse…" }; pickCache.Click += async (_, _) => await PickCacheAsync();
        var pickDefinition = new Button { Content = "Schema…" }; pickDefinition.Click += async (_, _) => await PickDefinitionAsync();
        var csv = new Button { Content = "Export CSV…" }; csv.Click += async (_, _) => await ExportAsync("csv");
        var jsonl = new Button { Content = "Export JSONL…" }; jsonl.Click += async (_, _) => await ExportAsync("jsonl");
        var heading = new Border
        {
            BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 8),
            Child = new WrapPanel { Children = { back, new TextBlock { Text = "CLIENT CACHE TABLES", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) }, open, csv, jsonl } }
        };
        var paths = new Grid { ColumnDefinitions = new("Auto,*,Auto"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 8, RowSpacing = 6, Margin = new Thickness(12, 10, 12, 6) };
        AddPath(paths, 0, "Cache file", _cachePath, pickCache); AddPath(paths, 1, "Definition", _definitionPath, pickDefinition);
        _records.ItemTemplate = new FuncDataTemplate<CacheRow>((record, _) => record is null ? new Grid() : BuildRecord(record));
        _records.SelectionChanged += (_, _) => ShowRecord(); _search.TextChanged += (_, _) => Filter();
        var results = new Grid
        {
            RowDefinitions = new("Auto,Auto,*,Auto,*"), RowSpacing = 6, Margin = new Thickness(12, 0, 12, 8),
            Children = { _search, WithRow(_summary, 1), WithRow(new Border { BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), Child = _records }, 2), WithRow(new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = Brush.Parse("#2B3445") }, 3), WithRow(new ScrollViewer { Content = _detail }, 4) }
        };
        var drop = new Grid { RowDefinitions = new("Auto,Auto,*"), Children = { heading, WithRow(paths, 1), WithRow(results, 2) } };
        DragDrop.SetAllowDrop(drop, true);
        DragDrop.AddDragOverHandler(drop, (_, args) => { args.DragEffects = args.DataTransfer.TryGetFiles()?.Any(file => IsCache(file.TryGetLocalPath())) == true ? DragDropEffects.Copy : DragDropEffects.None; args.Handled = true; });
        DragDrop.AddDropHandler(drop, async (_, args) => { var path = args.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).FirstOrDefault(IsCache); if (path is not null) await LoadAsync(path); args.Handled = true; });
        Content = drop;
    }

    public async Task LoadAsync(string path)
    {
        var request = ++_loadRequest; path = Path.GetFullPath(path); _cachePath.Text = path; _summary.Text = $"Reading {Path.GetFileName(path)} on a background thread…"; _records.ItemsSource = null;
        try
        {
            var isAdb = Path.GetExtension(path).Equals(".adb", StringComparison.OrdinalIgnoreCase); var definitionPath = _definitionPath.Text?.Trim();
            if (isAdb && (string.IsNullOrWhiteSpace(definitionPath) || Path.GetFileName(definitionPath).Equals("WDB.xml", StringComparison.OrdinalIgnoreCase))) { definitionPath = DiscoverAdbDefinition(); _definitionPath.Text = definitionPath; }
            else if (!isAdb && !string.IsNullOrWhiteSpace(definitionPath) && Path.GetFileName(definitionPath).Equals("adb-definitions.xml", StringComparison.OrdinalIgnoreCase)) { definitionPath = DiscoverPreferredDefinition(); _definitionPath.Text = definitionPath; }
            var loaded = await Task.Run<object>(() =>
            {
                WowCacheTableDefinition? definition = null;
                if (!string.IsNullOrWhiteSpace(definitionPath))
                {
                    var catalog = WowCacheDefinitionCatalog.Load(definitionPath); definition = catalog.Resolve(path, isAdb ? WowCacheDefinitionKind.Adb : WowCacheDefinitionKind.Wdb);
                }
                return isAdb ? WowAdbTableService.LoadWch2(path, definition) : WowCacheTableService.LoadWdb(path, definition);
            });
            if (request != _loadRequest) return;
            if (loaded is WowCacheTable wdb)
            {
                _wdbTable = wdb; _adbTable = null; _rows = wdb.Records.Select(record => new CacheRow(record.Id.ToString("N0"), record.Id.ToString(), checked((int)record.PayloadSize), $"file 0x{record.FileOffset:X}", record.Values, record.UnconsumedBytes, record.DecodeError, record.Payload)).ToArray();
                var failures = wdb.Records.Count(record => record.DecodeError is not null); var remainder = wdb.Records.Count(record => record.UnconsumedBytes != 0); _summary.Text = $"{Path.GetFileName(path)} · {wdb.Header.Magic} · build {wdb.Header.Build:N0} · {wdb.Header.Locale} · {wdb.Records.Count:N0} record(s) · schema {wdb.Definition?.Name ?? "RAW"} · {failures:N0} decode failure(s) · {remainder:N0} remainder(s) · source SHA-256 {wdb.Sha256}";
                DesktopCrashLogger.Debug("CACHE", "wdb-open-success", ("path", path), ("build", wdb.Header.Build), ("records", wdb.Records.Count), ("definition", wdb.Definition?.SourcePath ?? "raw"), ("failures", failures));
            }
            else
            {
                var adb = (WowAdbTable)loaded; _adbTable = adb; _wdbTable = null; _rows = adb.Records.Select(record => new CacheRow(record.Id?.ToString("N0") ?? $"ROW {record.RowIndex:N0}", $"{record.RowIndex} {record.Id}", record.Payload.Length, $"row {record.RowIndex:N0}", record.Values, record.UnconsumedBytes, record.DecodeError, record.Payload)).ToArray();
                var failures = adb.Records.Count(record => record.DecodeError is not null); var remainder = adb.Records.Count(record => record.UnconsumedBytes != 0); _summary.Text = $"{Path.GetFileName(path)} · {adb.Header.Signature} · build {adb.Header.Build:N0} · {adb.Records.Count:N0} record(s) × {adb.Header.RecordSize:N0} bytes · {adb.Header.StringBlockSize:N0} string bytes · schema {adb.Definition?.Name ?? "RAW"} · {failures:N0} decode failure(s) · {remainder:N0} remainder(s) · source SHA-256 {adb.Sha256}";
                DesktopCrashLogger.Debug("CACHE", "adb-open-success", ("path", path), ("build", adb.Header.Build), ("records", adb.Records.Count), ("definition", adb.Definition?.SourcePath ?? "raw"), ("failures", failures));
            }
            Filter();
        }
        catch (Exception exception) { if (request != _loadRequest) return; _wdbTable = null; _adbTable = null; _rows = []; _summary.Text = $"Cache open failed: {exception.Message}"; _detail.Text = "The source file was not changed."; DesktopCrashLogger.Log("Client cache open failed", exception); }
    }

    private Task OpenSelectedAsync()
    {
        var path = _cachePath.Text?.Trim(); if (!IsCache(path)) { _summary.Text = "Choose an existing .wdb or .adb file first."; return Task.CompletedTask; }
        return LoadAsync(path!);
    }

    private void Filter()
    {
        var query = _search.Text?.Trim() ?? string.Empty; _records.ItemsSource = _rows.Where(record => query.Length == 0 || record.SearchIdentity.Contains(query, StringComparison.OrdinalIgnoreCase) || record.Values.Any(value => value.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || value.DisplayValue.Contains(query, StringComparison.OrdinalIgnoreCase)) || (record.DecodeError?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)).ToArray();
    }

    private void ShowRecord()
    {
        if (_records.SelectedItem is not CacheRow record) return;
        var builder = new StringBuilder(); builder.AppendLine($"RECORD {record.Identity} · payload {record.PayloadSize:N0} bytes · {record.Location}");
        builder.AppendLine(record.DecodeError is null ? $"Decoded fields: {record.Values.Count:N0} · unconsumed bytes: {record.UnconsumedBytes:N0}" : $"DECODE STOPPED: {record.DecodeError}"); builder.AppendLine();
        foreach (var value in record.Values) builder.AppendLine($"{value.Name}  =  {value.DisplayValue}    [{value.Type}, payload +0x{value.Offset:X}, {value.Length:N0} byte(s)]");
        if (record.UnconsumedBytes > 0 || record.Values.Count == 0)
        {
            var start = Math.Max(0, record.Payload.Length - record.UnconsumedBytes); var count = Math.Min(record.Payload.Length - start, 256);
            var hex = Convert.ToHexString(record.Payload.AsSpan(start, count));
            builder.AppendLine().AppendLine($"RAW {(count < record.Payload.Length - start ? "FIRST " : string.Empty)}{count:N0} BYTE(S) FROM +0x{start:X}").AppendLine(string.Join(' ', hex.Chunk(2).Select(pair => new string(pair))));
        }
        _detail.Text = builder.ToString();
    }

    private async Task PickCacheAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider; if (storage is null) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open WoW client cache", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("WoW client caches") { Patterns = ["*.wdb", "*.adb"] }] });
        var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path is not null) await LoadAsync(path);
    }

    private async Task PickDefinitionAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider; if (storage is null) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Select cache definition XML", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("Cache definitions") { Patterns = ["*.xml"] }] });
        var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path is null) return; _definitionPath.Text = path; if (IsCache(_cachePath.Text)) await LoadAsync(_cachePath.Text!);
    }

    private async Task ExportAsync(string format)
    {
        if (_wdbTable is null && _adbTable is null) { _summary.Text = "Open a cache before exporting it."; return; }
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider; if (storage is null) return;
        var sourcePath = _wdbTable?.SourcePath ?? _adbTable!.SourcePath; var target = await storage.SaveFilePickerAsync(new FilePickerSaveOptions { Title = $"Export decoded cache as {format.ToUpperInvariant()}", SuggestedFileName = Path.GetFileNameWithoutExtension(sourcePath) + "." + format, DefaultExtension = format, FileTypeChoices = [new FilePickerFileType(format.ToUpperInvariant()) { Patterns = [$"*.{format}"] }] });
        var path = target?.TryGetLocalPath(); if (path is null) return;
        try { if (_wdbTable is not null) await Task.Run(() => WowCacheTableService.Export(_wdbTable, path, format, overwrite: true)); else await Task.Run(() => WowAdbTableService.Export(_adbTable!, path, format, overwrite: true)); _summary.Text = $"Exported {_rows.Count:N0} record(s) atomically to {path}"; }
        catch (Exception exception) { _summary.Text = $"Export failed: {exception.Message}"; DesktopCrashLogger.Log("WDB cache export failed", exception); }
    }

    private static Control BuildRecord(CacheRow record)
    {
        var preview = record.DecodeError ?? (record.Values.Count == 0 ? "Raw record · select to inspect bytes" : string.Join(" · ", record.Values.Where(value => !value.Name.Equals("Entry", StringComparison.OrdinalIgnoreCase)).Take(4).Select(value => $"{value.Name}={value.DisplayValue}")));
        var color = record.DecodeError is null ? "#79D7A8" : "#E98472";
        var grid = new Grid { ColumnDefinitions = new("Auto,Auto,*"), ColumnSpacing = 10, Margin = new Thickness(6, 4) };
        Add(grid, record.Identity, 0, "#E5EAF2", FontWeight.SemiBold); Add(grid, $"{record.PayloadSize:N0} B", 1, color); Add(grid, preview, 2); return grid;
    }

    private static string DiscoverPreferredDefinition() => WowCacheDefinitionCatalog.Discover().FirstOrDefault(path => Path.GetFileName(path).Equals("WDB.xml", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
    private static string DiscoverAdbDefinition() => WowCacheDefinitionCatalog.Discover().FirstOrDefault(path => Path.GetFileName(path).Equals("adb-definitions.xml", StringComparison.OrdinalIgnoreCase) && path.Contains("4.3", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
    private static bool IsCache(string? path) => path is not null && File.Exists(path) && (Path.GetExtension(path).Equals(".wdb", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".adb", StringComparison.OrdinalIgnoreCase));
    private static Button Accent(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static TextBlock Status(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#99A5B8") };
    private static void AddPath(Grid grid, int row, string label, Control field, Control action) { var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }; Grid.SetRow(text, row); grid.Children.Add(text); Grid.SetRow(field, row); Grid.SetColumn(field, 1); grid.Children.Add(field); Grid.SetRow(action, row); Grid.SetColumn(action, 2); grid.Children.Add(action); }
    private static void Add(Grid grid, string text, int column, string color = "#9AA5B7", FontWeight? weight = null) { var block = new TextBlock { Text = text, Foreground = Brush.Parse(color), FontWeight = weight ?? FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis }; Grid.SetColumn(block, column); grid.Children.Add(block); }
    private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
    private sealed record CacheRow(string Identity, string SearchIdentity, int PayloadSize, string Location, IReadOnlyList<WowCacheValue> Values, int UnconsumedBytes, string? DecodeError, byte[] Payload);
}
