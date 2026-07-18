using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class MapWorkspaceView : UserControl, IDisposable
{
    private readonly TextBox _path = new() { PlaceholderText = "Open or drop a WotLK ADT, WDT, or WDL file…" };
    private readonly TextBlock _summary = Info("No map file loaded.");
    private readonly TextBlock _selected = Info("Select a present grid cell for exact terrain metadata.");
    private readonly ListBox _chunks = new();
    private readonly ListBox _dependencies = new();
    private readonly TextBlock _status = Info("Read-only native inspection · no legacy map executable required");
    private readonly MapGridControl _grid = new();
    private CancellationTokenSource? _operation;

    public event EventHandler? BackRequested;

    public MapWorkspaceView()
    {
        var back = new Button { Content = "← Editor" }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var open = new Button { Content = "Open map file…" }; open.Click += async (_, _) => await PickAsync();
        var inspect = new Button { Content = "Inspect / reload" }; inspect.Click += async (_, _) => await OpenAsync(_path.Text);
        _grid.CellSelected += (_, cell) => _selected.Text = cell is null ? "No cell selected." : Describe(cell);

        var heading = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10, Margin = new Thickness(12, 8) };
        heading.Children.Add(back);
        var title = new StackPanel { Spacing = 2, Children = { new TextBlock { Text = "MAP & WORLD · WOTLK TERRAIN", FontSize = 18, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = "ADT terrain chunks · WDT world tiles · WDL horizon heights", Foreground = Brush.Parse("#8E99AD") } } };
        heading.Children.Add(title); Grid.SetColumn(title, 1);

        open.Margin = new Thickness(0, 0, 6, 0);
        var controls = new StackPanel { Margin = new Thickness(12, 8), Spacing = 8, Children = { _path, new WrapPanel { Children = { open, inspect } } } };
        var drop = new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(1), Background = Brush.Parse("#090D14"), Child = _grid };
        DragDrop.SetAllowDrop(drop, true);
        DragDrop.AddDragOverHandler(drop, (_, args) => { args.DragEffects = args.DataTransfer.TryGetFiles()?.Any(file => IsMap(file.TryGetLocalPath())) == true ? DragDropEffects.Copy : DragDropEffects.None; args.Handled = true; });
        DragDrop.AddDropHandler(drop, async (_, args) => { var path = args.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).FirstOrDefault(IsMap); if (path is not null) await OpenAsync(path); args.Handled = true; });

        var details = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Margin = new Thickness(10), Spacing = 8,
                Children = { Label("FILE SUMMARY"), Card(_summary), Label("SELECTED CELL"), Card(_selected), Label("CHUNK TABLE"), _chunks, Label("REFERENCED CLIENT ASSETS"), _dependencies }
            }
        };
        var body = new Grid { ColumnDefinitions = new("3*,Auto,2*") };
        body.Children.Add(drop);
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Brush.Parse("#2B3445") }; body.Children.Add(splitter); Grid.SetColumn(splitter, 1);
        body.Children.Add(details); Grid.SetColumn(details, 2);
        var root = new Grid { RowDefinitions = new("Auto,Auto,*,Auto") };
        root.Children.Add(new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 0, 0, 1), Child = heading });
        root.Children.Add(controls); Grid.SetRow(controls, 1);
        root.Children.Add(body); Grid.SetRow(body, 2);
        var footer = new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 7), Child = _status }; root.Children.Add(footer); Grid.SetRow(footer, 3);
        Content = root;
    }

    public async Task OpenAsync(string? path)
    {
        if (!IsMap(path)) { _status.Text = "Choose an existing .adt, .wdt, or .wdl file."; return; }
        _operation?.Cancel(); _operation?.Dispose(); _operation = new CancellationTokenSource(); var token = _operation.Token;
        try
        {
            _path.Text = Path.GetFullPath(path!); _status.Text = $"Inspecting {Path.GetFileName(path)}…";
            var inspection = await Task.Run(() => { token.ThrowIfCancellationRequested(); return MapAssetInspectionService.Inspect(path!); }, token);
            if (token.IsCancellationRequested) return;
            _grid.SetInspection(inspection); _summary.Text = Summary(inspection); _selected.Text = "Select a present grid cell for exact terrain metadata.";
            _chunks.ItemsSource = inspection.Chunks.Select(chunk => $"{chunk.Id} · {chunk.Occurrences:N0} chunk(s) · {chunk.PayloadBytes:N0} bytes").ToArray();
            _dependencies.ItemsSource = inspection.TexturePaths.Select(value => "Texture · " + value).Concat(inspection.ModelPaths.Select(value => "Model · " + value)).Concat(inspection.WmoPaths.Select(value => "WMO · " + value)).DefaultIfEmpty("No path-list dependencies in this file.").ToArray();
            _status.Text = $"Loaded {inspection.Kind} · {inspection.PresentCells:N0}/{inspection.Cells.Count:N0} present cells · click a cell for details · drop another map file anywhere on the grid";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _grid.SetInspection(null); _summary.Text = "Inspection failed."; _status.Text = exception.Message; DesktopCrashLogger.Log("Map inspection failed", exception); }
    }

    private async Task PickAsync()
    {
        var top = TopLevel.GetTopLevel(this); if (top is null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open a WotLK map asset", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("WoW map assets") { Patterns = ["*.adt", "*.wdt", "*.wdl"] }] });
        var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path is not null) await OpenAsync(path);
    }

    private static string Summary(MapAssetInspection value) => $"{Path.GetFileName(value.Path)}\n{value.Kind} · MVER {value.Version:N0}\nGrid {value.GridWidth:N0}×{value.GridHeight:N0} · {value.PresentCells:N0}/{value.Cells.Count:N0} present\nWorld tile {(value.TileX is null ? "not encoded by filename" : $"{value.TileX:N0},{value.TileY:N0}")}\nHeight {value.MinimumHeight?.ToString("0.###") ?? "-"} .. {value.MaximumHeight?.ToString("0.###") ?? "-"}\nHeader flags 0x{value.HeaderFlags:X}\n{value.TexturePaths.Count:N0} textures · {value.ModelPaths.Count:N0} models · {value.WmoPaths.Count:N0} WMOs" + (value.Findings.Count == 0 ? "\nValidation: clean" : "\n" + string.Join("\n", value.Findings.Select(finding => "Review: " + finding)));
    private static string Describe(MapTileCell cell) => $"Grid {cell.X:N0},{cell.Y:N0}\nPresent: {cell.Present}\nFlags: 0x{cell.Flags:X}\nAsync ID: {cell.AsyncId:N0}\nArea ID: {cell.AreaId?.ToString("N0") ?? "-"}\nHoles: 0x{cell.Holes?.ToString("X") ?? "-"}\nHeight: {cell.MinimumHeight?.ToString("0.###") ?? "-"} .. {cell.MaximumHeight?.ToString("0.###") ?? "-"}";
    private static bool IsMap(string? path) => path is not null && File.Exists(path) && Path.GetExtension(path).ToLowerInvariant() is ".adt" or ".wdt" or ".wdl";
    private static TextBlock Info(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#AAB5C7") };
    private static TextBlock Label(string text) => new() { Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#7F8A9F") };
    private static Border Card(Control child) => new() { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(1), Padding = new Thickness(9), Child = child };
    public void Dispose() { _operation?.Cancel(); _operation?.Dispose(); _operation = null; }
}

internal sealed class MapGridControl : Control
{
    private MapAssetInspection? _inspection; private MapTileCell? _selected;
    public event EventHandler<MapTileCell?>? CellSelected;
    public MapGridControl() { ClipToBounds = true; }
    public void SetInspection(MapAssetInspection? inspection) { _inspection = inspection; _selected = null; InvalidateVisual(); }

    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(Brush.Parse("#080B10"), Bounds); if (_inspection is null) { DrawText(context, "Drop an ADT, WDT, or WDL file here", new Point(18, 30), Brush.Parse("#8995A9")); return; }
        var size = Math.Max(1, Math.Min(Bounds.Width, Bounds.Height) - 28); var left = (Bounds.Width - size) / 2; var top = (Bounds.Height - size) / 2; var cellSize = size / Math.Max(_inspection.GridWidth, _inspection.GridHeight);
        var min = _inspection.MinimumHeight; var max = _inspection.MaximumHeight;
        foreach (var cell in _inspection.Cells)
        {
            var rect = new Rect(left + cell.X * cellSize, top + cell.Y * cellSize, Math.Max(0.5, cellSize - 0.35), Math.Max(0.5, cellSize - 0.35));
            IBrush brush = cell.Present ? HeightBrush(cell, min, max) : Brush.Parse("#111722"); context.FillRectangle(brush, rect);
            if (cell.Holes is > 0 && cellSize >= 4) context.DrawRectangle(null, new Pen(Brush.Parse("#FFB84A"), Math.Max(0.6, cellSize * 0.08)), rect);
            if (_selected == cell) context.DrawRectangle(null, new Pen(Brush.Parse("#FFFFFF"), Math.Max(1, cellSize * 0.13)), rect);
        }
        context.DrawRectangle(null, new Pen(Brush.Parse("#34415A"), 1), new Rect(left, top, size, size));
        DrawText(context, $"{_inspection.Kind} · {_inspection.PresentCells:N0} present · {_inspection.GridWidth}×{_inspection.GridHeight}", new Point(12, 18), Brush.Parse("#D8E2F1"));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); if (_inspection is null) return; var size = Math.Max(1, Math.Min(Bounds.Width, Bounds.Height) - 28); var left = (Bounds.Width - size) / 2; var top = (Bounds.Height - size) / 2; var point = e.GetPosition(this);
        var x = (int)((point.X - left) / size * _inspection.GridWidth); var y = (int)((point.Y - top) / size * _inspection.GridHeight);
        _selected = x >= 0 && x < _inspection.GridWidth && y >= 0 && y < _inspection.GridHeight ? _inspection.Cells.FirstOrDefault(cell => cell.X == x && cell.Y == y) : null;
        CellSelected?.Invoke(this, _selected); InvalidateVisual(); e.Handled = true;
    }

    private static IBrush HeightBrush(MapTileCell cell, float? minimum, float? maximum)
    {
        if (cell.MinimumHeight is null || cell.MaximumHeight is null || minimum is null || maximum is null || maximum <= minimum) return Brush.Parse("#2B7A78");
        var midpoint = (cell.MinimumHeight.Value + cell.MaximumHeight.Value) * 0.5f; var amount = Math.Clamp((midpoint - minimum.Value) / (maximum.Value - minimum.Value), 0, 1);
        var low = Color.Parse("#164E63"); var high = Color.Parse("#B6D369"); byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
        return new SolidColorBrush(Color.FromArgb(255, Mix(low.R, high.R), Mix(low.G, high.G), Mix(low.B, high.B)));
    }
    private static void DrawText(DrawingContext context, string text, Point point, IBrush brush) => context.DrawText(new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, brush), point);
}
