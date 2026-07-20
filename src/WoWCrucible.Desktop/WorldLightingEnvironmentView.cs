using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class WorldLightingEnvironmentView : UserControl
{
    private readonly WorldLightingEnvironmentControl _scene = new();
    private readonly ComboBox _water = new() { ItemsSource = new[] { "Ocean water", "Fresh water" }, SelectedIndex = 0 };
    private readonly TextBlock _details = new() { Text = "Select a resolved light profile.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") };

    public WorldLightingEnvironmentView()
    {
        _water.SelectionChanged += (_, _) => _scene.UseFreshWater = _water.SelectedIndex == 1;
        var heading = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(10, 7), Children = { new TextBlock { Text = "LIVE BAND COMPOSITION", FontWeight = FontWeight.SemiBold }, _water } }; Grid.SetColumn(_water, 1);
        Content = new Grid
        {
            RowDefinitions = new("Auto,*,Auto"),
            Children =
            {
                heading,
                WithRow(_scene, 1),
                WithRow(new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(10, 7), Child = _details }, 2)
            }
        };
    }

    public void SetScene(WorldLightingEnvironmentSample? scene)
    {
        _scene.SetScene(scene); _details.Text = scene is null ? "Select a resolved light profile." : $"{scene.Clock} · client time {scene.Time:N0}/{WorldLightingService.DayUnits:N0} · sky, fog, cloud, celestial, ambient, diffuse, and water colors sampled from the selected profile" + (scene.Findings.Count == 0 ? " · complete 18-color composition" : $" · {scene.Findings.Count:N0} visible fallback finding(s)");
    }

    private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
}

internal sealed class WorldLightingEnvironmentControl : Control
{
    private static readonly Typeface Typeface = new("Inter");
    private WorldLightingEnvironmentSample? _scene;
    private bool _freshWater;
    public bool UseFreshWater { get => _freshWater; set { if (_freshWater == value) return; _freshWater = value; InvalidateVisual(); } }
    public WorldLightingEnvironmentControl() => ClipToBounds = true;
    public void SetScene(WorldLightingEnvironmentSample? scene) { _scene = scene; InvalidateVisual(); }

    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(Brush.Parse("#090D14"), Bounds); if (_scene is null) { Draw(context, "Select a light profile to compose its environment", 14, 14, Brush.Parse("#8E99AD"), 12); return; }
        var width = Math.Max(1, Bounds.Width); var height = Math.Max(1, Bounds.Height); var horizon = height * 0.70;
        var sky = new LinearGradientBrush { StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative) };
        foreach (var stop in _scene.Sky) sky.GradientStops.Add(new GradientStop(ToColor(stop.Color), stop.Position)); context.FillRectangle(sky, new Rect(0, 0, width, horizon));
        var fog = new SolidColorBrush(ToColor(_scene.Fog), 0.66); context.FillRectangle(fog, new Rect(0, horizon * 0.80, width, horizon * 0.20));
        if (_scene.SunAboveHorizon)
        {
            var center = new Point(_scene.SunX * width, _scene.SunY * horizon); var radius = Math.Clamp(Math.Min(width, height) * 0.035, 3, Math.Min(width, height) * 0.10);
            context.DrawEllipse(new SolidColorBrush(ToColor(_scene.Sun), 0.18), null, center, radius * 2.8, radius * 2.8); context.DrawEllipse(new SolidColorBrush(ToColor(_scene.Sun), 0.45), null, center, radius * 1.7, radius * 1.7); context.DrawEllipse(new SolidColorBrush(ToColor(_scene.Sun)), null, center, radius, radius);
        }
        DrawClouds(context, width, horizon, _scene);
        var shallow = _freshWater ? _scene.FreshWaterShallow : _scene.OceanShallow; var deep = _freshWater ? _scene.FreshWaterDeep : _scene.OceanDeep;
        var water = new LinearGradientBrush { StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative), GradientStops = { new(ToColor(shallow), 0), new(ToColor(deep), 1) } }; context.FillRectangle(water, new Rect(0, horizon, width, height - horizon));
        for (var index = 1; index <= 4; index++) { var y = horizon + (height - horizon) * index / 5; context.DrawLine(new Pen(new SolidColorBrush(ToColor(_scene.GlobalDiffuse), 0.22), 1), new Point(0, y), new Point(width, y)); }
        context.FillRectangle(new SolidColorBrush(ToColor(_scene.GlobalAmbient), 0.22), new Rect(0, 0, width, height));
        Draw(context, $"{_scene.Clock} · {(_freshWater ? "fresh water" : "ocean")}", 10, 8, Brushes.White, 11);
        DrawSwatch(context, width, 10, "AMBIENT", _scene.GlobalAmbient); DrawSwatch(context, width, 34, "DIFFUSE", _scene.GlobalDiffuse); DrawSwatch(context, width, 58, "FOG", _scene.Fog);
        if (_scene.Findings.Count > 0) Draw(context, $"{_scene.Findings.Count:N0} fallback(s)", 10, height - 18, Brush.Parse("#FFD08A"), 10);
    }

    private static void DrawClouds(DrawingContext context, double width, double horizon, WorldLightingEnvironmentSample scene)
    {
        var shift = scene.Time / (double)WorldLightingService.DayUnits * width; var baseBrush = new SolidColorBrush(ToColor(scene.CloudBase), 0.48); var edge = new Pen(new SolidColorBrush(ToColor(scene.CloudEdge), 0.70), 1.5); var accent = new SolidColorBrush(ToColor(scene.CloudAccent), 0.25);
        for (var cloud = 0; cloud < 5; cloud++)
        {
            var x = (cloud * width * 0.24 + shift * (0.18 + cloud * 0.02)) % (width + 120) - 60; var y = horizon * (0.18 + cloud % 3 * 0.13); var rx = Math.Max(14, width * (0.07 + cloud % 2 * 0.025)); var ry = Math.Max(5, horizon * 0.035);
            context.DrawEllipse(accent, null, new Point(x + rx * 0.20, y + ry * 0.45), rx * 1.20, ry * 1.35); context.DrawEllipse(baseBrush, edge, new Point(x, y), rx, ry); context.DrawEllipse(baseBrush, edge, new Point(x + rx * 0.65, y + ry * 0.15), rx * 0.75, ry * 0.85);
        }
    }

    private static void DrawSwatch(DrawingContext context, double width, double y, string label, WorldLightColor color)
    {
        var x = Math.Max(4, width - 98); context.FillRectangle(new SolidColorBrush(ToColor(color)), new Rect(x, y, 18, 14)); context.DrawRectangle(null, new Pen(new SolidColorBrush(Colors.White, 0.45), 1), new Rect(x, y, 18, 14)); Draw(context, label, x + 24, y, Brushes.White, 9);
    }
    private static Color ToColor(WorldLightColor color) => Color.FromRgb(color.R, color.G, color.B);
    private static void Draw(DrawingContext context, string text, double x, double y, IBrush brush, double size) => context.DrawText(new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface, size, brush), new Point(x, y));
}
