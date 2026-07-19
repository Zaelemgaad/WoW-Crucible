using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop.Controls;

internal sealed class PetAbilityGraphOverview : Control
{
    private static readonly IBrush Background = Brush.Parse("#090D14");
    private static readonly IBrush Root = Brush.Parse("#5B3D12");
    private static readonly IBrush Neighbor = Brush.Parse("#19354B");
    private static readonly IBrush Border = Brush.Parse("#647187");
    private static readonly IBrush Text = Brush.Parse("#E7ECF4");
    private static readonly IBrush Muted = Brush.Parse("#98A5B8");
    private static readonly Pen EdgePen = new(Brush.Parse("#526A89"), 1.5);
    private PetAbilityGraph? _graph;
    private string? _selectedNode;

    public void SetGraph(PetAbilityGraph? graph, string? selectedNode = null)
    {
        _graph = graph;
        _selectedNode = selectedNode;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Background, Bounds);
        if (Bounds.Width < 40 || Bounds.Height < 40) return;
        if (_graph is null || _graph.Nodes.Count == 0)
        {
            DrawText(context, "Load a creature to map its evidenced spell and talent relationships.", new Point(12, 18), Muted, 11);
            return;
        }

        var center = _graph.Nodes.FirstOrDefault(node => node.Id.Equals(_selectedNode, StringComparison.OrdinalIgnoreCase))
            ?? _graph.Nodes.FirstOrDefault(node => node.Kind == PetAbilityNodeKind.Creature)
            ?? _graph.Nodes[0];
        var neighborIds = _graph.Edges
            .Where(edge => edge.From.Equals(center.Id, StringComparison.OrdinalIgnoreCase) || edge.To.Equals(center.Id, StringComparison.OrdinalIgnoreCase))
            .Select(edge => edge.From.Equals(center.Id, StringComparison.OrdinalIgnoreCase) ? edge.To : edge.From)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var neighbors = neighborIds.Select(id => _graph.Nodes.FirstOrDefault(node => node.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .Where(node => node is not null).Cast<PetAbilityGraphNode>().ToArray();

        if (Bounds.Width < 680) RenderVertical(context, center, neighbors);
        else RenderHorizontal(context, center, neighbors);
        DrawText(context, $"Selected neighborhood · {neighbors.Length:N0} direct connection(s) · full graph: {_graph.Nodes.Count:N0} nodes / {_graph.Edges.Count:N0} evidence edges", new Point(10, Bounds.Bottom - 18), Muted, 10);
    }

    private void RenderHorizontal(DrawingContext context, PetAbilityGraphNode center, IReadOnlyList<PetAbilityGraphNode> neighbors)
    {
        var padding = Math.Max(10, Bounds.Width * 0.018);
        var footer = 30d;
        var rootWidth = Math.Max(150, Bounds.Width * 0.28);
        var rootHeight = Math.Clamp(Bounds.Height * 0.22, 52, 100);
        var root = new Rect((Bounds.Width - rootWidth) / 2, (Bounds.Height - footer - rootHeight) / 2, rootWidth, rootHeight);
        DrawNode(context, root, Root, center, true);
        var visible = Visible(neighbors, Bounds.Height - footer - padding * 2, 54, 12);
        if (visible.Count == 0) return;
        var left = visible.Where((_, index) => index % 2 == 0).ToArray();
        var right = visible.Where((_, index) => index % 2 != 0).ToArray();
        DrawColumn(context, left, new Rect(padding, padding, Math.Max(100, root.Left - padding * 2), Bounds.Height - footer - padding * 2), root, true);
        DrawColumn(context, right, new Rect(root.Right + padding, padding, Math.Max(100, Bounds.Width - root.Right - padding * 2), Bounds.Height - footer - padding * 2), root, false);
    }

    private void RenderVertical(DrawingContext context, PetAbilityGraphNode center, IReadOnlyList<PetAbilityGraphNode> neighbors)
    {
        var padding = Math.Max(8, Bounds.Width * 0.02);
        var footer = 30d;
        var rootHeight = Math.Clamp((Bounds.Height - footer) * 0.22, 50, 90);
        var root = new Rect(padding, (Bounds.Height - footer - rootHeight) / 2, Bounds.Width - padding * 2, rootHeight);
        DrawNode(context, root, Root, center, true);
        var available = Math.Max(1, (Bounds.Height - footer - rootHeight) / 2 - padding * 2);
        var visible = Visible(neighbors, Bounds.Width - padding * 2, 125, 8);
        var before = visible.Take((visible.Count + 1) / 2).ToArray();
        var after = visible.Skip(before.Length).ToArray();
        DrawRow(context, before, new Rect(padding, padding, Bounds.Width - padding * 2, available), root, true);
        DrawRow(context, after, new Rect(padding, root.Bottom + padding, Bounds.Width - padding * 2, available), root, false);
    }

    private static void DrawColumn(DrawingContext context, IReadOnlyList<PetAbilityGraphNode> nodes, Rect area, Rect root, bool left)
    {
        if (nodes.Count == 0) return;
        var gap = Math.Max(4, area.Height * 0.012);
        var nodeHeight = (area.Height - gap * (nodes.Count - 1)) / nodes.Count;
        for (var index = 0; index < nodes.Count; index++)
        {
            var node = new Rect(area.X, area.Y + index * (nodeHeight + gap), area.Width, nodeHeight);
            context.DrawLine(EdgePen, left ? new Point(node.Right, node.Center.Y) : new Point(root.Right, root.Center.Y), left ? new Point(root.Left, root.Center.Y) : new Point(node.Left, node.Center.Y));
            DrawNode(context, node, Neighbor, nodes[index], false);
        }
    }

    private static void DrawRow(DrawingContext context, IReadOnlyList<PetAbilityGraphNode> nodes, Rect area, Rect root, bool above)
    {
        if (nodes.Count == 0) return;
        var gap = Math.Max(4, area.Width * 0.012);
        var nodeWidth = (area.Width - gap * (nodes.Count - 1)) / nodes.Count;
        for (var index = 0; index < nodes.Count; index++)
        {
            var node = new Rect(area.X + index * (nodeWidth + gap), area.Y, nodeWidth, area.Height);
            context.DrawLine(EdgePen, above ? new Point(node.Center.X, node.Bottom) : new Point(root.Center.X, root.Bottom), above ? new Point(root.Center.X, root.Top) : new Point(node.Center.X, node.Top));
            DrawNode(context, node, Neighbor, nodes[index], false);
        }
    }

    private static IReadOnlyList<PetAbilityGraphNode> Visible(IReadOnlyList<PetAbilityGraphNode> nodes, double extent, double desired, int maximum)
    {
        var count = Math.Min(nodes.Count, Math.Min(maximum, Math.Max(1, (int)(extent / desired))));
        return nodes.Take(count).ToArray();
    }

    private static void DrawNode(DrawingContext context, Rect rect, IBrush fill, PetAbilityGraphNode node, bool strong)
    {
        context.FillRectangle(fill, rect, 5);
        context.DrawRectangle(new Pen(Border, 1), rect, 5);
        var available = Math.Max(4, rect.Width - 16);
        DrawText(context, Trim(node.Label, available), new Point(rect.X + 8, rect.Y + Math.Max(5, rect.Height * 0.2)), Text, Math.Clamp(rect.Height * 0.16, 9, strong ? 13 : 12));
        if (rect.Height >= 34) DrawText(context, Trim(node.Kind.ToString(), available), new Point(rect.X + 8, rect.Y + Math.Max(20, rect.Height * 0.58)), Muted, Math.Clamp(rect.Height * 0.13, 8, 10));
    }

    private static string Trim(string value, double width)
    {
        var maximum = Math.Max(4, (int)(width / 6.5));
        return value.Length <= maximum ? value : string.Concat(value.AsSpan(0, maximum - 1), "…");
    }

    private static void DrawText(DrawingContext context, string text, Point point, IBrush brush, double size) =>
        context.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush), point);
}
