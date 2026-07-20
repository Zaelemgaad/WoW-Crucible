using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;
using WoWCrucible.Desktop.Controls;

namespace WoWCrucible.Desktop;

internal sealed record DbcRecordNavigationRequest(string Path, uint Id);

internal sealed class WorldLightingView : UserControl, IDisposable
{
    private sealed record ContinentChoice(uint? Id, int Lights) { public override string ToString() => Id is null ? $"Every map · {Lights:N0} lights" : $"Map {Id:N0} · {Lights:N0} lights"; }
    private sealed record ColorBandChoice(WorldLightColorBand Band, WorldLightColor Color)
    {
        public override string ToString() => $"{Band.Index + 1:00} · {Band.Name} · {Color.Hex} · {Band.Keys.Count:N0} key(s)";
    }
    private sealed record FloatBandChoice(WorldLightFloatBand Band, float Value)
    {
        public override string ToString() => $"{Band.Index + 1:00} · {Band.Name} · {Value:0.###} · {Band.Keys.Count:N0} key(s)";
    }

    private readonly DesktopWorkspaceSession _session;
    private readonly TextBox _dbcRoot = new() { PlaceholderText = "Build-12340 DBC folder…" };
    private readonly TextBox _search = new() { PlaceholderText = "Filter by light ID, map ID, parameter ID, or skybox path…" };
    private readonly ComboBox _continent = new();
    private readonly ComboBox _slot = new() { ItemsSource = Enumerable.Range(1, 8).Select(value => $"Parameter slot {value}").ToArray(), SelectedIndex = 0 };
    private readonly NumericUpDown _time = new() { Minimum = 0, Maximum = WorldLightingService.DayUnits, Increment = 10, Value = 1440 };
    private readonly TextBlock _timeLabel = Info("12:00 · client time 1,440 / 2,880");
    private readonly ListBox _lights = new();
    private readonly WorldLightPlotControl _plot = new();
    private readonly TextBlock _lightDetail = Info("Load the five lighting DBCs, then select a light.");
    private readonly TextBlock _paramsDetail = Info("No parameter profile selected.");
    private readonly TextBlock _skyboxDetail = Info("No skybox selected.");
    private readonly ListBox _colors = new();
    private readonly ListBox _floats = new();
    private readonly TextBlock _status = Info("Native Light.dbc inspection replaces legacy LightMapper and skybox viewers without copying their assets.");
    private WorldLightingCatalog? _catalog;
    private IReadOnlyList<WorldLightRecord> _visibleLights = [];
    private WorldLightProfile? _profile;
    private CancellationTokenSource? _operation;

    public event EventHandler<DbcRecordNavigationRequest>? OpenDbcRecordRequested;

    public WorldLightingView(DesktopWorkspaceSession session)
    {
        _session = session; _dbcRoot.Text = session.Settings.CoreDbcPath;
        _lights.ItemTemplate = new FuncDataTemplate<WorldLightRecord>((light, _) => light is null ? new Border() : new Border
        {
            BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(8, 6),
            Child = new StackPanel { Spacing = 2, Children = { new TextBlock { Text = $"Light {light.Id:N0} · map {light.ContinentId:N0}{(light.IsGlobal ? " · GLOBAL" : string.Empty)}", FontWeight = FontWeight.SemiBold }, new TextBlock { Text = $"world X/Z {light.WorldX:0.##}, {light.WorldZ:0.##} · radius {light.FalloffStart:0.##}–{light.FalloffEnd:0.##}", Foreground = Brush.Parse("#8E99AD") } } }
        });
        _colors.ItemTemplate = new FuncDataTemplate<ColorBandChoice>((choice, _) =>
        {
            if (choice is null) return new Border(); var swatch = new Border
            {
                Background = new SolidColorBrush(Avalonia.Media.Color.FromRgb(choice.Color.R, choice.Color.G, choice.Color.B)),
                BorderBrush = Brush.Parse("#596579"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
                Child = new TextBlock { Text = "   ", Padding = new Thickness(6, 2) }
            };
            var grid = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 8, Margin = new Thickness(3), Children = { swatch, new TextBlock { Text = choice.ToString(), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center } } }; Grid.SetColumn(swatch, 0); Grid.SetColumn(grid.Children[1], 1); return grid;
        });
        _floats.ItemTemplate = new FuncDataTemplate<FloatBandChoice>((choice, _) => choice is null ? new Border() : new TextBlock { Text = choice.ToString(), Margin = new Thickness(5), TextWrapping = TextWrapping.Wrap });
        _lights.SelectionChanged += (_, _) => SelectLight(_lights.SelectedItem as WorldLightRecord);
        _plot.LightSelected += (_, light) => { _lights.SelectedItem = light; _lights.ScrollIntoView(light); };
        _continent.SelectionChanged += (_, _) => ApplyFilter(); _search.TextChanged += (_, _) => ApplyFilter(); _slot.SelectionChanged += (_, _) => ResolveProfile();
        _time.ValueChanged += (_, _) => { RefreshTime(); RefreshBands(); };

        var load = Accent("Load / refresh lighting"); load.Click += async (_, _) => await LoadAsync();
        var browse = new Button { Content = "Choose DBC folder…" }; browse.Click += async (_, _) => await BrowseAsync();
        var openLight = new Button { Content = "Edit Light.dbc record" }; openLight.Click += (_, _) => Open("Light.dbc", (_lights.SelectedItem as WorldLightRecord)?.Id ?? 0);
        var openParams = new Button { Content = "Edit LightParams record" }; openParams.Click += (_, _) => Open("LightParams.dbc", _profile?.ParamsId ?? 0);
        var openSkybox = new Button { Content = "Edit LightSkybox record" }; openSkybox.Click += (_, _) => Open("LightSkybox.dbc", _profile?.Skybox?.Id ?? 0);
        var openColor = new Button { Content = "Edit selected color band" }; openColor.Click += (_, _) => Open("LightIntBand.dbc", (_colors.SelectedItem as ColorBandChoice)?.Band.Id ?? 0);
        var openFloat = new Button { Content = "Edit selected float band" }; openFloat.Click += (_, _) => Open("LightFloatBand.dbc", (_floats.SelectedItem as FloatBandChoice)?.Band.Id ?? 0);

        var source = new StackPanel { Spacing = 7, Margin = new Thickness(12, 8), Children = { new TextBlock { Text = "WOTLK WORLD LIGHTING GRAPH", FontSize = 16, FontWeight = FontWeight.SemiBold }, _dbcRoot, new WrapPanel { Children = { load, browse } }, _search, new WrapPanel { Children = { Field("MAP", _continent), Field("PROFILE", _slot), Field("TIME 0–2880", _time), _timeLabel } } } };
        var left = new Grid { RowDefinitions = new("Auto,*,Auto"), Children = { new TextBlock { Text = "LIGHT SOURCES", Margin = new Thickness(8), FontWeight = FontWeight.SemiBold }, WithRow(_lights, 1), WithRow(openLight, 2) } };
        var plotPage = new Grid { RowDefinitions = new("*,Auto"), Children = { _plot, WithRow(new TextBlock { Text = "Neutral coordinate plot · X/Z world coordinates · click a marker · circles show outer falloff", Margin = new Thickness(8), Foreground = Brush.Parse("#8E99AD"), TextWrapping = TextWrapping.Wrap }, 1) } };
        var profilePage = new ScrollViewer { Content = new StackPanel { Spacing = 8, Margin = new Thickness(10), Children = { Label("SELECTED LIGHT"), Card(_lightDetail), openLight, Label("PARAMETER PROFILE"), Card(_paramsDetail), openParams, Label("SKYBOX"), Card(_skyboxDetail), openSkybox, Label("18 TIME-SAMPLED COLOR BANDS"), _colors, openColor, Label("6 TIME-SAMPLED FLOAT BANDS"), _floats, openFloat } } };
        var rightTabs = new TabControl { Items = { new TabItem { Header = "Coordinate plot", Content = plotPage }, new TabItem { Header = "Colors, floats & skybox", Content = profilePage } } };
        var body = new ResponsiveSplitGrid(left, rightTabs, 2, 3);
        var root = new Grid { RowDefinitions = new("Auto,*,Auto"), Children = { source, WithRow(body, 1), WithRow(new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 7), Child = _status }, 2) } };
        Content = root;
    }

    public async Task LoadAsync()
    {
        _operation?.Cancel(); _operation?.Dispose(); _operation = new CancellationTokenSource(); var token = _operation.Token;
        try
        {
            var path = _dbcRoot.Text?.Trim() ?? string.Empty; _status.Text = "Loading and validating all five world-lighting tables…";
            var catalog = await Task.Run(() => { token.ThrowIfCancellationRequested(); return WorldLightingService.Load(path); }, token); if (token.IsCancellationRequested) return;
            _catalog = catalog; _session.Settings.CoreDbcPath = catalog.DbcDirectory; _session.Settings.Save();
            var choices = new[] { new ContinentChoice(null, catalog.Lights.Count) }.Concat(catalog.Lights.GroupBy(light => light.ContinentId).OrderBy(group => group.Key).Select(group => new ContinentChoice(group.Key, group.Count()))).ToArray();
            _continent.ItemsSource = choices; _continent.SelectedIndex = choices.Length > 1 ? 1 : 0; ApplyFilter();
            _status.Text = $"Validated {catalog.Lights.Count:N0} lights · {catalog.Parameters.Count:N0} profiles · {catalog.ColorBands.Count:N0} color bands · {catalog.FloatBands.Count:N0} float bands · {catalog.Skyboxes.Count:N0} skyboxes" + (catalog.Findings.Count == 0 ? " · no broken references" : $" · {catalog.Findings.Count:N0} finding(s)");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _catalog = null; _lights.ItemsSource = null; _plot.SetLights([]); _status.Text = exception.Message; DesktopCrashLogger.Log("World lighting load failed", exception); }
    }

    private void ApplyFilter()
    {
        if (_catalog is null) return; var map = (_continent.SelectedItem as ContinentChoice)?.Id; var query = _search.Text?.Trim() ?? string.Empty;
        IEnumerable<WorldLightRecord> rows = _catalog.Lights; if (map is not null) rows = rows.Where(light => light.ContinentId == map);
        if (query.Length > 0) rows = rows.Where(light => light.Id.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) || light.ContinentId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) || light.LightParamsIds.Any(id => id.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)) || light.LightParamsIds.Where(id => id != 0).Select(id => _catalog.Parameters.GetValueOrDefault(id)?.LightSkyboxId ?? 0).Select(id => _catalog.Skyboxes.GetValueOrDefault(id)?.ClientModelPath ?? string.Empty).Any(path => path.Contains(query, StringComparison.OrdinalIgnoreCase)));
        _visibleLights = rows.ToArray(); _lights.ItemsSource = _visibleLights; _plot.SetLights(_visibleLights); if (_visibleLights.Count > 0) _lights.SelectedItem = _visibleLights[0]; else SelectLight(null);
    }

    private void SelectLight(WorldLightRecord? light)
    {
        _plot.SetSelected(light); if (light is null) { _lightDetail.Text = "No matching light selected."; _profile = null; RefreshBands(); return; }
        _lightDetail.Text = $"ID {light.Id:N0} · map {light.ContinentId:N0}{(light.IsGlobal ? " · global/default light" : string.Empty)}\nWorld X/Y/Z  {light.WorldX:0.###}, {light.WorldY:0.###}, {light.WorldZ:0.###}\nStored X/Y/Z {light.StoredX:0.###}, {light.StoredY:0.###}, {light.StoredZ:0.###}\nFalloff      {light.FalloffStart:0.###} → {light.FalloffEnd:0.###} world yards\nParams       {string.Join(", ", light.LightParamsIds.Select((id, index) => $"{index + 1}:{(id == 0 ? "—" : id)}"))}";
        ResolveProfile();
    }

    private void ResolveProfile()
    {
        if (_catalog is null || _lights.SelectedItem is not WorldLightRecord light) return; _profile = WorldLightingService.Resolve(_catalog, light, Math.Max(0, _slot.SelectedIndex));
        var value = _profile.Parameters; _paramsDetail.Text = value is null ? string.Join("\n", _profile.Findings) : $"LightParams {value.Id:N0}\nHighlight sky {value.HighlightSky} · cloud type {value.CloudTypeId}\nGlow {value.Glow:0.###}\nWater alpha shallow/deep {value.WaterShallowAlpha:0.###} / {value.WaterDeepAlpha:0.###}\nOcean alpha shallow/deep {value.OceanShallowAlpha:0.###} / {value.OceanDeepAlpha:0.###}" + (_profile.Findings.Count == 0 ? string.Empty : "\n" + string.Join("\n", _profile.Findings));
        _skyboxDetail.Text = value?.LightSkyboxId == 0 ? "This profile uses no explicit LightSkybox record." : _profile.Skybox is null ? $"Missing LightSkybox {value?.LightSkyboxId}." : $"LightSkybox {_profile.Skybox.Id:N0}\n{_profile.Skybox.ClientModelPath}\nFlags 0x{_profile.Skybox.Flags:X8}";
        RefreshBands();
    }

    private void RefreshTime()
    {
        var time = (int)(_time.Value ?? 0); var minutes = (int)Math.Round(time / (double)WorldLightingService.DayUnits * 24 * 60) % (24 * 60); _timeLabel.Text = $"{minutes / 60:00}:{minutes % 60:00} · client time {time:N0} / {WorldLightingService.DayUnits:N0}";
    }
    private void RefreshBands()
    {
        if (_profile is null) { _colors.ItemsSource = null; _floats.ItemsSource = null; return; } var time = (int)(_time.Value ?? 0);
        var colors = _profile.ColorBands.Select(band => new ColorBandChoice(band, WorldLightingService.Sample(band, time))).ToArray(); var floats = _profile.FloatBands.Select(band => new FloatBandChoice(band, WorldLightingService.Sample(band, time))).ToArray();
        _colors.ItemsSource = colors; _floats.ItemsSource = floats; if (colors.Length > 0) _colors.SelectedIndex = 0; if (floats.Length > 0) _floats.SelectedIndex = 0;
    }

    private async Task BrowseAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider; if (storage is null) return; var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select the DBC folder containing all five Light tables", AllowMultiple = false }); var path = folders.FirstOrDefault()?.TryGetLocalPath(); if (path is not null) { _dbcRoot.Text = path; await LoadAsync(); }
    }
    private void Open(string fileName, uint id) { if (id == 0) { _status.Text = $"Select a non-empty {fileName} record first."; return; } var path = Path.Combine(_dbcRoot.Text?.Trim() ?? string.Empty, fileName); if (!File.Exists(path)) { _status.Text = $"File not found: {path}"; return; } OpenDbcRecordRequested?.Invoke(this, new(path, id)); }
    public void Dispose() { _operation?.Cancel(); _operation?.Dispose(); }
    private static Button Accent(string text) => new() { Content = text, Background = Brush.Parse("#C58A2B"), Foreground = Brushes.Black, FontWeight = FontWeight.SemiBold };
    private static TextBlock Info(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") };
    private static TextBlock Label(string text) => new() { Text = text, FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#C58A2B") };
    private static Border Card(Control child) => new() { BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), Padding = new Thickness(10), Child = child };
    private static StackPanel Field(string label, Control child) => new() { Margin = new Thickness(0, 0, 8, 0), Children = { Label(label), child } };
    private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
}

internal sealed class WorldLightPlotControl : Control
{
    private static readonly IBrush Background = Brush.Parse("#090D14"); private static readonly IBrush Grid = Brush.Parse("#202A39"); private static readonly IBrush PointBrush = Brush.Parse("#E0A55B"); private static readonly IBrush SelectedBrush = Brush.Parse("#67C6E3"); private static readonly Typeface Typeface = new("Inter");
    private IReadOnlyList<WorldLightRecord> _lights = []; private WorldLightRecord? _selected; private readonly Dictionary<uint, Point> _points = [];
    public event EventHandler<WorldLightRecord>? LightSelected;
    public WorldLightPlotControl() { ClipToBounds = true; Focusable = true; }
    public void SetLights(IReadOnlyList<WorldLightRecord> lights) { _lights = lights; if (_selected is not null && !_lights.Contains(_selected)) _selected = null; InvalidateVisual(); }
    public void SetSelected(WorldLightRecord? light) { _selected = light; InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(Background, Bounds); _points.Clear(); if (_lights.Count == 0 || Bounds.Width < 40 || Bounds.Height < 40) { Draw(context, "No matching lights", 12, 12, Brush.Parse("#8E99AD")); return; }
        var local = _lights.Where(light => !light.IsGlobal).ToArray(); var minX = local.Length == 0 ? 0 : local.Min(light => light.WorldX); var maxX = local.Length == 0 ? 1 : local.Max(light => light.WorldX); var minZ = local.Length == 0 ? 0 : local.Min(light => light.WorldZ); var maxZ = local.Length == 0 ? 1 : local.Max(light => light.WorldZ);
        if (maxX - minX < 1) maxX = minX + 1; if (maxZ - minZ < 1) maxZ = minZ + 1; var pad = 24d; var scaleX = (Bounds.Width - pad * 2) / (maxX - minX); var scaleZ = (Bounds.Height - pad * 2) / (maxZ - minZ); var scale = Math.Min(scaleX, scaleZ);
        for (var index = 1; index < 4; index++) { var x = Bounds.Width * index / 4; var y = Bounds.Height * index / 4; context.DrawLine(new Pen(Grid, 1), new Point(x, 0), new Point(x, Bounds.Height)); context.DrawLine(new Pen(Grid, 1), new Point(0, y), new Point(Bounds.Width, y)); }
        foreach (var light in _lights)
        {
            var point = light.IsGlobal ? new Point(16, 16) : new Point(pad + (light.WorldX - minX) * scale, Bounds.Height - pad - (light.WorldZ - minZ) * scale); _points[light.Id] = point;
            var selected = ReferenceEquals(light, _selected) || light.Id == _selected?.Id; var brush = selected ? SelectedBrush : PointBrush; var radius = Math.Clamp(light.FalloffEnd * scale, 2, Math.Max(Bounds.Width, Bounds.Height) * 2); if (!light.IsGlobal && radius > 2) context.DrawEllipse(null, new Pen(new SolidColorBrush(((SolidColorBrush)brush).Color, 0.28), selected ? 2 : 1), point, radius, radius);
            context.DrawEllipse(brush, null, point, selected ? 6 : 3.5, selected ? 6 : 3.5); if (selected) Draw(context, $"Light {light.Id:N0}", point.X + 8, point.Y - 8, SelectedBrush);
        }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); var point = e.GetPosition(this); var selected = _lights.Select(light => (Light: light, Distance: _points.TryGetValue(light.Id, out var target) ? Distance(point, target) : double.MaxValue)).OrderBy(value => value.Distance).FirstOrDefault(); if (selected.Light is not null && selected.Distance <= 18) { LightSelected?.Invoke(this, selected.Light); e.Handled = true; }
    }
    private static double Distance(Point left, Point right) { var x = left.X - right.X; var y = left.Y - right.Y; return Math.Sqrt(x * x + y * y); }
    private static void Draw(DrawingContext context, string text, double x, double y, IBrush brush) => context.DrawText(new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface, 11, brush), new Point(x, y));
}
