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
    private readonly TextBox _heightDelta = new() { Text = "0", PlaceholderText = "Signed terrain height delta" };
    private readonly TextBox _brushCenterX = new() { Text = "8", PlaceholderText = "Center X (0–16)" };
    private readonly TextBox _brushCenterY = new() { Text = "8", PlaceholderText = "Center Y (0–16)" };
    private readonly TextBox _brushRadius = new() { Text = "1", PlaceholderText = "Radius" };
    private readonly TextBox _brushStrength = new() { Text = "5", PlaceholderText = "Signed strength" };
    private readonly ComboBox _brushFalloff = new() { ItemsSource = Enum.GetValues<AdtTerrainBrushFalloff>(), SelectedItem = AdtTerrainBrushFalloff.Smooth };
    private CancellationTokenSource? _operation;
    private MapAssetInspection? _inspection;
    private AdtHeightEditPlan? _heightPlan;
    private AdtTerrainBrushPlan? _brushPlan;

    public event EventHandler? BackRequested;

    public MapWorkspaceView()
    {
        var back = new Button { Content = "← Editor" }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var open = new Button { Content = "Open map file…" }; open.Click += async (_, _) => await PickAsync();
        var inspect = new Button { Content = "Inspect / reload" }; inspect.Click += async (_, _) => await OpenAsync(_path.Text);
        _grid.CellsSelected += (_, cells) => { _heightPlan = null; _selected.Text = cells.Count == 0 ? "No cells selected." : cells.Count == 1 ? Describe(cells[0]) : $"{cells.Count:N0} present terrain cells selected.\nHold Ctrl while clicking to toggle individual cells."; };
        var selectAll = new Button { Content = "Select all present" }; selectAll.Click += (_, _) => _grid.SelectAllPresent();
        var clear = new Button { Content = "Clear selection" }; clear.Click += (_, _) => _grid.ClearSelection();
        var previewHeight = new Button { Content = "Preview height offset" }; previewHeight.Click += async (_, _) => await PreviewHeightAsync();
        var saveHeight = new Button { Content = "Write edited ADT…" }; saveHeight.Click += async (_, _) => await SaveHeightAsync();
        var previewBrush = new Button { Content = "Preview vertex brush" }; previewBrush.Click += async (_, _) => await PreviewBrushAsync();
        var saveBrush = new Button { Content = "Write brushed ADT…" }; saveBrush.Click += async (_, _) => await SaveBrushAsync();
        _grid.TerrainPointSelected += (_, point) => { _brushCenterX.Text = point.X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture); _brushCenterY.Text = point.Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture); UpdateBrushOverlay(); };
        _brushCenterX.TextChanged += (_, _) => BrushInputChanged(); _brushCenterY.TextChanged += (_, _) => BrushInputChanged(); _brushRadius.TextChanged += (_, _) => BrushInputChanged(); _brushStrength.TextChanged += (_, _) => _brushPlan = null; _brushFalloff.SelectionChanged += (_, _) => _brushPlan = null;
        _heightDelta.TextChanged += (_, _) => _heightPlan = null;

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
                Children = { Label("FILE SUMMARY"), Card(_summary), Label("SELECTED CELL(S)"), Card(_selected), Label("ADT TERRAIN HEIGHT OFFSET"), _heightDelta, new WrapPanel { Children = { selectAll, clear, previewHeight, saveHeight } }, Label("ADT VERTEX BRUSH"), BrushFields(), new WrapPanel { Children = { previewBrush, saveBrush } }, Info("Click the terrain grid to place the brush center. Strength may be positive or negative; the circle is the exact tile-local radius."), Label("CHUNK TABLE"), _chunks, Label("REFERENCED CLIENT ASSETS"), _dependencies }
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
            _inspection = inspection; _heightPlan = null; _brushPlan = null; _grid.SetInspection(inspection); _grid.SetBrush(null, null, null); _summary.Text = Summary(inspection); _selected.Text = "Select a present grid cell for exact terrain metadata.";
            _chunks.ItemsSource = inspection.Chunks.Select(chunk => $"{chunk.Id} · {chunk.Occurrences:N0} chunk(s) · {chunk.PayloadBytes:N0} bytes").ToArray();
            _dependencies.ItemsSource = inspection.TexturePaths.Select(value => "Texture · " + value).Concat(inspection.ModelPaths.Select(value => "Model · " + value)).Concat(inspection.WmoPaths.Select(value => "WMO · " + value)).DefaultIfEmpty("No path-list dependencies in this file.").ToArray();
            _status.Text = $"Loaded {inspection.Kind} · {inspection.PresentCells:N0}/{inspection.Cells.Count:N0} present cells · click a cell for details · drop another map file anywhere on the grid";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _grid.SetInspection(null); _summary.Text = "Inspection failed."; _status.Text = exception.Message; DesktopCrashLogger.Log("Map inspection failed", exception); }
    }

    private async Task PreviewHeightAsync()
    {
        try
        {
            if (_inspection?.Kind != MapAssetKind.Adt) throw new InvalidOperationException("Terrain-height editing requires an ADT file.");
            if (!float.TryParse(_heightDelta.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var delta) || !float.IsFinite(delta)) throw new InvalidOperationException("Enter a finite height delta using a period as the decimal separator.");
            var selected = _grid.SelectedCells.Where(cell => cell.Present).Select(cell => (cell.X, cell.Y)).ToArray();
            _status.Text = $"Planning {selected.Length:N0} terrain-cell height edit(s)…"; var plan = await Task.Run(() => AdtHeightEditService.Plan(_inspection.Path, selected, delta)); var preview = await Task.Run(() => AdtHeightEditService.Preview(plan));
            _brushPlan = null; _grid.SetInspection(preview, plan.Cells.Select(cell => (cell.X, cell.Y))); _heightPlan = plan; _summary.Text = Summary(preview); _status.Text = $"Preview only · {plan.Cells.Count:N0} cell(s) offset by {plan.Delta:R} · source bytes unchanged";
        }
        catch (Exception exception) { _status.Text = exception.Message; DesktopCrashLogger.Log("ADT height preview failed", exception); }
    }

    private async Task PreviewBrushAsync()
    {
        try
        {
            if (_inspection?.Kind != MapAssetKind.Adt) throw new InvalidOperationException("Terrain brushing requires an ADT file.");
            var centerX = Number(_brushCenterX, "brush center X"); var centerY = Number(_brushCenterY, "brush center Y"); var radius = Number(_brushRadius, "brush radius"); var strength = Number(_brushStrength, "brush strength");
            var falloff = _brushFalloff.SelectedItem is AdtTerrainBrushFalloff selected ? selected : AdtTerrainBrushFalloff.Smooth; _status.Text = "Planning exact MCVT vertex edits…";
            var plan = await Task.Run(() => AdtTerrainBrushService.Plan(_inspection.Path, centerX, centerY, radius, strength, falloff)); var preview = await Task.Run(() => AdtTerrainBrushService.Preview(plan));
            _brushPlan = plan; _heightPlan = null; _grid.SetInspection(preview, plan.Vertices.Select(vertex => (vertex.CellX, vertex.CellY)).Distinct()); _grid.SetBrush(plan.CenterX, plan.CenterY, plan.Radius); _summary.Text = Summary(preview);
            _status.Text = $"Preview only · {plan.Vertices.Count:N0} vertex edits across {plan.Vertices.Select(vertex => (vertex.CellX, vertex.CellY)).Distinct().Count():N0} cell(s) · {plan.Falloff} falloff · source bytes unchanged";
        }
        catch (Exception exception) { _status.Text = exception.Message; DesktopCrashLogger.Log("ADT terrain brush preview failed", exception); }
    }

    private async Task SaveBrushAsync()
    {
        try
        {
            if (_brushPlan is null) { await PreviewBrushAsync(); if (_brushPlan is null) return; }
            var top = TopLevel.GetTopLevel(this); if (top is null) return; var stem = Path.GetFileNameWithoutExtension(_brushPlan.InputPath);
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Write a separate vertex-brushed ADT", SuggestedFileName = stem + "-brush.adt", DefaultExtension = "adt", FileTypeChoices = [new FilePickerFileType("WoW ADT") { Patterns = ["*.adt"] }] });
            var output = file?.TryGetLocalPath(); if (output is null) return; _status.Text = "Writing and re-validating every affected terrain cell…"; var result = await Task.Run(() => AdtTerrainBrushService.Apply(_brushPlan, output, overwrite: false));
            _path.Text = result.OutputPath; _inspection = result.Inspection; _brushPlan = null; _grid.SetInspection(result.Inspection); _grid.SetBrush(null, null, null); _summary.Text = Summary(result.Inspection);
            _status.Text = $"Wrote {result.EditedVertices:N0} vertex edit(s) across {result.EditedCells:N0} cell(s) atomically · receipt {Path.GetFileName(result.ReceiptPath)} · original retained";
        }
        catch (Exception exception) { _status.Text = exception.Message; DesktopCrashLogger.Log("ADT terrain brush write failed", exception); }
    }

    private Control BrushFields()
    {
        var fields = new Grid { ColumnDefinitions = new("*,*,*,*"), ColumnSpacing = 6 };
        var values = new[] { Field("CENTER X", _brushCenterX), Field("CENTER Y", _brushCenterY), Field("RADIUS", _brushRadius), Field("STRENGTH", _brushStrength) };
        for (var index = 0; index < values.Length; index++) { fields.Children.Add(values[index]); Grid.SetColumn(values[index], index); }
        return new StackPanel { Spacing = 6, Children = { fields, Field("FALLOFF", _brushFalloff) } };
    }

    private void UpdateBrushOverlay()
    {
        if (TryNumber(_brushCenterX, out var x) && TryNumber(_brushCenterY, out var y) && TryNumber(_brushRadius, out var radius) && radius > 0) _grid.SetBrush(x, y, radius); else _grid.SetBrush(null, null, null);
    }
    private void BrushInputChanged() { _brushPlan = null; UpdateBrushOverlay(); }

    private static float Number(TextBox box, string label) => TryNumber(box, out var value) ? value : throw new InvalidOperationException($"Enter a finite {label} using a period as the decimal separator.");
    private static bool TryNumber(TextBox box, out float value) => float.TryParse(box.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

    private async Task SaveHeightAsync()
    {
        try
        {
            if (_heightPlan is null) { await PreviewHeightAsync(); if (_heightPlan is null) return; }
            var top = TopLevel.GetTopLevel(this); if (top is null) return; var stem = Path.GetFileNameWithoutExtension(_heightPlan.InputPath);
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Write a separate edited ADT", SuggestedFileName = stem + "-height-edit.adt", DefaultExtension = "adt", FileTypeChoices = [new FilePickerFileType("WoW ADT") { Patterns = ["*.adt"] }] });
            var output = file?.TryGetLocalPath(); if (output is null) return; _status.Text = "Writing and re-validating edited ADT…";
            var result = await Task.Run(() => AdtHeightEditService.Apply(_heightPlan, output, overwrite: false));
            _path.Text = result.OutputPath; _inspection = result.Inspection; _heightPlan = null; _grid.SetInspection(result.Inspection); _summary.Text = Summary(result.Inspection);
            _status.Text = $"Wrote {result.EditedCells:N0} edited terrain cell(s) atomically · receipt {Path.GetFileName(result.ReceiptPath)} · original retained";
        }
        catch (Exception exception) { _status.Text = exception.Message; DesktopCrashLogger.Log("ADT height write failed", exception); }
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
    private static StackPanel Field(string label, Control control) => new() { Spacing = 3, Children = { Label(label), control } };
    private static Border Card(Control child) => new() { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(1), Padding = new Thickness(9), Child = child };
    public void Dispose() { _operation?.Cancel(); _operation?.Dispose(); _operation = null; }
}

internal sealed class MapGridControl : Control
{
    private MapAssetInspection? _inspection; private readonly HashSet<(int X, int Y)> _selected = [];
    private double? _brushX; private double? _brushY; private double? _brushRadius;
    public event EventHandler<IReadOnlyList<MapTileCell>>? CellsSelected;
    public event EventHandler<MapTerrainPoint>? TerrainPointSelected;
    public IReadOnlyList<MapTileCell> SelectedCells => _inspection is null ? [] : _inspection.Cells.Where(cell => _selected.Contains((cell.X, cell.Y))).ToArray();
    public MapGridControl() { ClipToBounds = true; }
    public void SetInspection(MapAssetInspection? inspection, IEnumerable<(int X, int Y)>? selection = null) { _inspection = inspection; _selected.Clear(); if (selection is not null) foreach (var cell in selection) _selected.Add(cell); Notify(); }
    public void SelectAllPresent() { _selected.Clear(); if (_inspection is not null) foreach (var cell in _inspection.Cells.Where(cell => cell.Present)) _selected.Add((cell.X, cell.Y)); Notify(); }
    public void ClearSelection() { _selected.Clear(); Notify(); }
    public void SetBrush(double? x, double? y, double? radius) { _brushX = x; _brushY = y; _brushRadius = radius; InvalidateVisual(); }

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
            if (_selected.Contains((cell.X, cell.Y))) context.DrawRectangle(null, new Pen(Brush.Parse("#FFFFFF"), Math.Max(1, cellSize * 0.13)), rect);
        }
        if (_inspection.Kind == MapAssetKind.Adt && _brushX is { } brushX && _brushY is { } brushY && _brushRadius is { } radius && radius > 0)
        {
            var center = new Point(left + brushX / _inspection.GridWidth * size, top + brushY / _inspection.GridHeight * size); var pixelRadius = radius / _inspection.GridWidth * size;
            context.DrawEllipse(Brush.Parse("#1838D7FF"), new Pen(Brush.Parse("#7ADFFF"), Math.Max(1, cellSize * 0.06)), center, pixelRadius, pixelRadius);
        }
        context.DrawRectangle(null, new Pen(Brush.Parse("#34415A"), 1), new Rect(left, top, size, size));
        DrawText(context, $"{_inspection.Kind} · {_inspection.PresentCells:N0} present · {_inspection.GridWidth}×{_inspection.GridHeight}", new Point(12, 18), Brush.Parse("#D8E2F1"));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); if (_inspection is null) return; var size = Math.Max(1, Math.Min(Bounds.Width, Bounds.Height) - 28); var left = (Bounds.Width - size) / 2; var top = (Bounds.Height - size) / 2; var point = e.GetPosition(this);
        var localX = (point.X - left) / size * _inspection.GridWidth; var localY = (point.Y - top) / size * _inspection.GridHeight; var x = (int)localX; var y = (int)localY;
        if (localX >= 0 && localX <= _inspection.GridWidth && localY >= 0 && localY <= _inspection.GridHeight && _inspection.Kind == MapAssetKind.Adt) TerrainPointSelected?.Invoke(this, new(Math.Clamp(localX, 0, 16), Math.Clamp(localY, 0, 16)));
        var cell = x >= 0 && x < _inspection.GridWidth && y >= 0 && y < _inspection.GridHeight ? _inspection.Cells.FirstOrDefault(candidate => candidate.X == x && candidate.Y == y && candidate.Present) : null;
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) _selected.Clear();
        if (cell is not null && !_selected.Add((cell.X, cell.Y))) _selected.Remove((cell.X, cell.Y)); Notify(); e.Handled = true;
    }

    private static IBrush HeightBrush(MapTileCell cell, float? minimum, float? maximum)
    {
        if (cell.MinimumHeight is null || cell.MaximumHeight is null || minimum is null || maximum is null || maximum <= minimum) return Brush.Parse("#2B7A78");
        var midpoint = (cell.MinimumHeight.Value + cell.MaximumHeight.Value) * 0.5f; var amount = Math.Clamp((midpoint - minimum.Value) / (maximum.Value - minimum.Value), 0, 1);
        var low = Color.Parse("#164E63"); var high = Color.Parse("#B6D369"); byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
        return new SolidColorBrush(Color.FromArgb(255, Mix(low.R, high.R), Mix(low.G, high.G), Mix(low.B, high.B)));
    }
    private static void DrawText(DrawingContext context, string text, Point point, IBrush brush) => context.DrawText(new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, brush), point);
    private void Notify() { CellsSelected?.Invoke(this, SelectedCells); InvalidateVisual(); }
}

internal sealed record MapTerrainPoint(double X, double Y);
