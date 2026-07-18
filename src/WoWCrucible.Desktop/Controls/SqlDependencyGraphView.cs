using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop.Controls;

internal sealed class SqlDependencyGraphView : Control
{
    private static readonly IBrush BackgroundBrush = Brush.Parse("#090D14");
    private static readonly IBrush RootBrush = Brush.Parse("#5B3D12");
    private static readonly IBrush IncomingBrush = Brush.Parse("#19354B");
    private static readonly IBrush OutgoingBrush = Brush.Parse("#263D2D");
    private static readonly IBrush BorderBrush = Brush.Parse("#637087");
    private static readonly IBrush TextBrush = Brush.Parse("#E7ECF4");
    private static readonly IBrush MutedBrush = Brush.Parse("#9AA6B8");
    private static readonly Pen IncomingPen = new(Brush.Parse("#4B8BB5"), 1.5);
    private static readonly Pen OutgoingPen = new(Brush.Parse("#65A574"), 1.5);
    private static readonly Typeface Regular = new("Inter");
    private static readonly Typeface Strong = new("Inter", FontStyle.Normal, FontWeight.SemiBold);
    private string _root = "Select a primary-keyed SQL row";
    private IReadOnlyList<SqlRelationshipMatch> _matches = [];

    public void SetGraph(string root, IReadOnlyList<SqlRelationshipMatch> matches)
    {
        _root = root; _matches = matches; InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(BackgroundBrush, Bounds);
        if (Bounds.Width < 40 || Bounds.Height < 40) return;
        var incoming = _matches.Where(match => !match.Outgoing).ToArray(); var outgoing = _matches.Where(match => match.Outgoing).ToArray();
        if (Bounds.Width < 680) RenderVertical(context, incoming, outgoing); else RenderHorizontal(context, incoming, outgoing);
    }

    private void RenderHorizontal(DrawingContext context, IReadOnlyList<SqlRelationshipMatch> incoming, IReadOnlyList<SqlRelationshipMatch> outgoing)
    {
        var padding = Math.Max(8, Bounds.Width * 0.015); var gap = Math.Max(12, Bounds.Width * 0.025); var columnWidth = (Bounds.Width - padding * 2 - gap * 2) / 3;
        var rootHeight = Math.Clamp(Bounds.Height * 0.22, 48, 96); var root = new Rect(padding + columnWidth + gap, (Bounds.Height - rootHeight) / 2, columnWidth, rootHeight);
        DrawNode(context, root, RootBrush, _root, "selected SQL row", true);
        DrawColumn(context, incoming, new Rect(padding, padding, columnWidth, Bounds.Height - padding * 2), root, true);
        DrawColumn(context, outgoing, new Rect(padding + (columnWidth + gap) * 2, padding, columnWidth, Bounds.Height - padding * 2), root, false);
    }

    private void RenderVertical(DrawingContext context, IReadOnlyList<SqlRelationshipMatch> incoming, IReadOnlyList<SqlRelationshipMatch> outgoing)
    {
        var padding = Math.Max(7, Bounds.Width * 0.02); var bandGap = Math.Max(8, Bounds.Height * 0.02); var bandHeight = (Bounds.Height - padding * 2 - bandGap * 2) / 3;
        var root = new Rect(padding, padding + bandHeight + bandGap, Bounds.Width - padding * 2, bandHeight);
        DrawNode(context, root, RootBrush, _root, "selected SQL row", true);
        DrawRow(context, incoming, new Rect(padding, padding, Bounds.Width - padding * 2, bandHeight), root, true);
        DrawRow(context, outgoing, new Rect(padding, padding + (bandHeight + bandGap) * 2, Bounds.Width - padding * 2, bandHeight), root, false);
    }

    private void DrawColumn(DrawingContext context, IReadOnlyList<SqlRelationshipMatch> matches, Rect area, Rect root, bool incoming)
    {
        var visible = Visible(matches, area.Height, 42, 10); if (visible.Count == 0) { DrawEmpty(context, area, incoming ? "No populated incoming edges" : "No populated outgoing edges"); return; }
        var gap = Math.Max(5, area.Height * 0.012); var nodeHeight = (area.Height - gap * (visible.Count - 1)) / visible.Count;
        for (var index = 0; index < visible.Count; index++)
        {
            var node = new Rect(area.X, area.Y + index * (nodeHeight + gap), area.Width, nodeHeight); var start = incoming ? RightCenter(node) : RightCenter(root); var end = incoming ? LeftCenter(root) : LeftCenter(node);
            context.DrawLine(incoming ? IncomingPen : OutgoingPen, start, end); DrawMatchNode(context, node, visible[index], incoming);
        }
    }

    private void DrawRow(DrawingContext context, IReadOnlyList<SqlRelationshipMatch> matches, Rect area, Rect root, bool incoming)
    {
        var visible = Visible(matches, area.Width, 110, 5); if (visible.Count == 0) { DrawEmpty(context, area, incoming ? "No populated incoming edges" : "No populated outgoing edges"); return; }
        var gap = Math.Max(5, area.Width * 0.012); var nodeWidth = (area.Width - gap * (visible.Count - 1)) / visible.Count;
        for (var index = 0; index < visible.Count; index++)
        {
            var node = new Rect(area.X + index * (nodeWidth + gap), area.Y, nodeWidth, area.Height); var start = incoming ? BottomCenter(node) : BottomCenter(root); var end = incoming ? TopCenter(root) : TopCenter(node);
            context.DrawLine(incoming ? IncomingPen : OutgoingPen, start, end); DrawMatchNode(context, node, visible[index], incoming);
        }
    }

    private static IReadOnlyList<SqlRelationshipMatch> Visible(IReadOnlyList<SqlRelationshipMatch> values, double extent, double desired, int maximum)
    {
        var count = Math.Min(values.Count, Math.Min(maximum, Math.Max(1, (int)(extent / desired)))); return values.Take(count).ToArray();
    }

    private static void DrawMatchNode(DrawingContext context, Rect rect, SqlRelationshipMatch match, bool incoming)
    {
        var title = $"{match.TargetTable}.{match.TargetColumn}"; var count = match.MatchingRows < 0 ? "file DBC · mirror empty" : $"{match.MatchingRows:N0} exact row(s)"; var subtitle = $"{count} · {(match.Relation.Declared ? "FK" : "inferred")}";
        DrawNode(context, rect, incoming ? IncomingBrush : OutgoingBrush, title, subtitle, false);
    }

    private static void DrawNode(DrawingContext context, Rect rect, IBrush fill, string title, string subtitle, bool strong)
    {
        context.FillRectangle(fill, rect, 5); context.DrawRectangle(new Pen(BorderBrush, 1), rect, 5);
        var available = Math.Max(4, rect.Width - 16); var titleText = Trim(title.Replace('\n', ' '), available); var subtitleText = Trim(subtitle, available);
        DrawText(context, titleText, rect.X + 8, rect.Y + Math.Max(5, rect.Height * 0.2), TextBrush, strong ? Strong : Regular, Math.Clamp(rect.Height * 0.16, 9, 13));
        if (rect.Height >= 34) DrawText(context, subtitleText, rect.X + 8, rect.Y + Math.Max(20, rect.Height * 0.56), MutedBrush, Regular, Math.Clamp(rect.Height * 0.13, 8, 11));
    }

    private static void DrawEmpty(DrawingContext context, Rect area, string text) => DrawText(context, Trim(text, area.Width - 12), area.X + 6, area.Center.Y - 7, MutedBrush, Regular, 10);
    private static void DrawText(DrawingContext context, string text, double x, double y, IBrush brush, Typeface typeface, double size) => context.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush), new Point(x, y));
    private static string Trim(string value, double width) { var maximum = Math.Max(4, (int)(width / 6.5)); return value.Length <= maximum ? value : string.Concat(value.AsSpan(0, maximum - 1), "…"); }
    private static Point LeftCenter(Rect rect) => new(rect.Left, rect.Center.Y);
    private static Point RightCenter(Rect rect) => new(rect.Right, rect.Center.Y);
    private static Point TopCenter(Rect rect) => new(rect.Center.X, rect.Top);
    private static Point BottomCenter(Rect rect) => new(rect.Center.X, rect.Bottom);
}
