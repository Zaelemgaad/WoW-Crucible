using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop.Controls;

internal sealed class PetCurvePlotView : Control
{
    private static readonly IBrush Background = Brush.Parse("#090D14");
    private static readonly IBrush Grid = Brush.Parse("#263044");
    private static readonly IBrush Text = Brush.Parse("#C9D3E1");
    private static readonly IBrush Muted = Brush.Parse("#8793A6");
    private static readonly IBrush LeftBrush = Brush.Parse("#5EB5FF");
    private static readonly IBrush RightBrush = Brush.Parse("#FFB45E");
    private static readonly Pen LeftPen = new(LeftBrush, 2);
    private static readonly Pen RightPen = new(RightBrush, 2);
    private static readonly Pen MissingPen = new(Brush.Parse("#536077"), 1);
    private PetLevelCurveMetricComparison? _metric;
    private uint _leftEntry;
    private uint _rightEntry;

    public void SetMetric(PetLevelCurveMetricComparison? metric, uint leftEntry, uint rightEntry)
    {
        _metric = metric; _leftEntry = leftEntry; _rightEntry = rightEntry; InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(Background, Bounds); if (Bounds.Width < 80 || Bounds.Height < 80) return;
        if (_metric is null || _metric.Points.Count == 0) { DrawText(context, "Run a family comparison to plot exact per-level values.", new Point(14, 24), Muted, 12); return; }
        var values = _metric.Points.SelectMany(point => new decimal?[] { point.Left, point.Right }).Where(value => value is not null).Select(value => (double)value!.Value).ToArray();
        if (values.Length == 0) { DrawText(context, "Neither curve contains this metric in the requested range.", new Point(14, 24), Muted, 12); return; }
        var left = Math.Max(52, Bounds.Width * 0.08); var right = Math.Max(18, Bounds.Width * 0.035); var top = Math.Max(38, Bounds.Height * 0.12); var bottom = Math.Max(34, Bounds.Height * 0.11); var plot = new Rect(left, top, Math.Max(1, Bounds.Width - left - right), Math.Max(1, Bounds.Height - top - bottom));
        var minimum = values.Min(); var maximum = values.Max(); if (Math.Abs(maximum - minimum) < 0.0000001) { minimum -= Math.Max(1, Math.Abs(minimum) * 0.05); maximum += Math.Max(1, Math.Abs(maximum) * 0.05); }
        for (var step = 0; step <= 4; step++)
        {
            var amount = step / 4d; var y = plot.Bottom - amount * plot.Height; context.DrawLine(new Pen(Grid, 1), new Point(plot.Left, y), new Point(plot.Right, y)); var value = minimum + amount * (maximum - minimum); DrawText(context, Compact(value), new Point(4, y - 7), Muted, 10);
        }
        context.DrawRectangle(null, new Pen(Grid, 1), plot); var firstLevel = _metric.Points[0].Level; var lastLevel = _metric.Points[^1].Level;
        DrawSeries(context, _metric.Points, point => point.Left, LeftPen, plot, minimum, maximum, firstLevel, lastLevel); DrawSeries(context, _metric.Points, point => point.Right, RightPen, plot, minimum, maximum, firstLevel, lastLevel);
        DrawText(context, $"{_metric.Display} · exact level values", new Point(plot.Left, 10), Text, 13); DrawText(context, $"■ {_leftEntry}", new Point(plot.Left, plot.Bottom + 10), LeftBrush, 11); DrawText(context, $"■ {_rightEntry}", new Point(plot.Left + Math.Max(92, plot.Width * 0.18), plot.Bottom + 10), RightBrush, 11);
        DrawText(context, firstLevel.ToString(CultureInfo.InvariantCulture), new Point(plot.Left, plot.Bottom + 25), Muted, 10); var last = lastLevel.ToString(CultureInfo.InvariantCulture); DrawText(context, last, new Point(plot.Right - last.Length * 6, plot.Bottom + 25), Muted, 10);
    }

    private static void DrawSeries(DrawingContext context, IReadOnlyList<PetLevelCurveValuePoint> points, Func<PetLevelCurveValuePoint, decimal?> selector, Pen pen, Rect plot, double minimum, double maximum, int firstLevel, int lastLevel)
    {
        Point? previous = null; foreach (var point in points)
        {
            var value = selector(point); if (value is null) { previous = null; continue; }
            var xAmount = lastLevel == firstLevel ? 0.5 : (point.Level - firstLevel) / (double)(lastLevel - firstLevel); var yAmount = ((double)value.Value - minimum) / (maximum - minimum); var current = new Point(plot.Left + xAmount * plot.Width, plot.Bottom - yAmount * plot.Height);
            if (previous is { } prior) context.DrawLine(pen, prior, current); else context.DrawEllipse(pen.Brush, MissingPen, current, 2.5, 2.5); previous = current;
        }
    }

    private static string Compact(double value) => Math.Abs(value) >= 1_000_000 ? $"{value / 1_000_000:0.##}m" : Math.Abs(value) >= 1_000 ? $"{value / 1_000:0.##}k" : value.ToString("0.##", CultureInfo.InvariantCulture);
    private static void DrawText(DrawingContext context, string text, Point point, IBrush brush, double size) => context.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush), point);
}
