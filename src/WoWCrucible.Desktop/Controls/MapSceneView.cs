using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop.Controls;

public sealed record MapSceneM2Instance(M2PreviewGeometry Geometry, MapM2Placement Placement, string SourcePath);
public sealed record MapSceneWmoInstance(WmoPreviewGeometry Geometry, MapWmoPlacement Placement, string SourcePath);

public sealed class MapSceneView : UserControl
{
    private readonly MapSceneCanvas _canvas = new(); private readonly CheckBox _terrain = new() { Content = "Terrain", IsChecked = true }; private readonly CheckBox _objects = new() { Content = "Placed objects", IsChecked = true }; private readonly CheckBox _wireframe = new() { Content = "Wireframe overlay" };
    public MapSceneView()
    {
        var reset = new Button { Content = "Reset view" }; reset.Click += (_, _) => _canvas.ResetView();
        _terrain.IsCheckedChanged += (_, _) => _canvas.SetVisibility(_terrain.IsChecked == true, _objects.IsChecked == true, _wireframe.IsChecked == true); _objects.IsCheckedChanged += (_, _) => _canvas.SetVisibility(_terrain.IsChecked == true, _objects.IsChecked == true, _wireframe.IsChecked == true); _wireframe.IsCheckedChanged += (_, _) => _canvas.SetVisibility(_terrain.IsChecked == true, _objects.IsChecked == true, _wireframe.IsChecked == true);
        Content = new Grid { RowDefinitions = new("Auto,*"), Children = { new WrapPanel { Margin = new Thickness(7), Children = { _terrain, _objects, _wireframe, reset } }, WithRow(_canvas, 1) } };
    }
    public void SetScene(AdtTerrainSceneGeometry terrain, AdtTerrainMaterialSet? materials, IReadOnlyList<MapSceneM2Instance> m2, IReadOnlyList<MapSceneWmoInstance> wmo, int unresolved, string provenance) => _canvas.SetScene(terrain, materials, m2, wmo, unresolved, provenance);
    public void ClearScene() => _canvas.ClearScene();
    private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
}

internal sealed class MapSceneCanvas : Control
{
    private sealed record Mesh(IReadOnlyList<Vector3> Vertices, IReadOnlyList<int> Indices, IReadOnlyList<SKColor>? VertexColors, Matrix4x4 Transform, SKColor Color, string Kind);
    private sealed record Scene(IReadOnlyList<Mesh> Meshes, Vector3 Minimum, Vector3 Maximum, int M2, int Wmo, int Unresolved, string Provenance, int SourceTriangles, int MaterialCells, int CompleteMaterialCells);
    private Scene? _scene; private float _yaw = -0.72f; private float _pitch = 0.72f; private float _zoom = 1f; private Point? _dragStart; private bool _terrain = true; private bool _objects = true; private bool _wireframe;
    public MapSceneCanvas() => ClipToBounds = true;
    public void SetScene(AdtTerrainSceneGeometry terrain, AdtTerrainMaterialSet? materials, IReadOnlyList<MapSceneM2Instance> m2, IReadOnlyList<MapSceneWmoInstance> wmo, int unresolved, string provenance)
    {
        var materialByCell = materials?.Cells.ToDictionary(value => (value.CellX, value.CellY)) ?? new Dictionary<(int, int), AdtTerrainMaterialCell>(); var meshes = new List<Mesh>();
        foreach (var cell in terrain.Cells)
        {
            var vertices = terrain.Vertices.Skip(cell.VertexStart).Take(cell.VertexCount).ToArray(); var indices = terrain.TriangleIndices.Skip(cell.TriangleStart).Take(cell.TriangleIndexCount).Select(value => value - cell.VertexStart).ToArray(); IReadOnlyList<SKColor>? colors = null;
            if (materialByCell.TryGetValue((cell.CellX, cell.CellY), out var material)) colors = Enumerable.Range(0, cell.VertexCount).Select(index => MaterialColor(material.Composite, index)).ToArray();
            meshes.Add(new(vertices, indices, colors, Matrix4x4.Identity, new SKColor(72, 121, 74), "terrain"));
        }
        meshes.AddRange(m2.Select(value => new Mesh(value.Geometry.Vertices, value.Geometry.TriangleIndices, null, M2PreviewSceneService.MapObjectTransform(value.Placement.Orientation, value.Placement.Scale, value.Placement.Position), new SKColor(91, 155, 213), "M2")));
        meshes.AddRange(wmo.Select(value => new Mesh(value.Geometry.Vertices, value.Geometry.TriangleIndices, null, M2PreviewSceneService.MapObjectTransform(value.Placement.Orientation, value.Placement.Scale, value.Placement.Position), new SKColor(178, 142, 91), "WMO")));
        var minimum = new Vector3(float.PositiveInfinity); var maximum = new Vector3(float.NegativeInfinity); var triangles = 0;
        foreach (var mesh in meshes)
        {
            triangles += mesh.Indices.Count / 3; foreach (var vertex in mesh.Vertices) { var world = Vector3.Transform(vertex, mesh.Transform); minimum = Vector3.Min(minimum, world); maximum = Vector3.Max(maximum, world); }
        }
        _scene = new(meshes, minimum, maximum, m2.Count, wmo.Count, unresolved, provenance, triangles, materials?.Cells.Count ?? 0, materials?.CompleteCells ?? 0); ResetView();

        static SKColor MaterialColor(RgbaTexture texture, int vertexIndex)
        {
            var local = AdtTerrainBrushService.VertexPosition(vertexIndex); var x = Math.Clamp((int)Math.Round(local.X * (texture.Width - 1)), 0, texture.Width - 1); var y = Math.Clamp((int)Math.Round(local.Y * (texture.Height - 1)), 0, texture.Height - 1); var offset = (y * texture.Width + x) * 4; return new(texture.Pixels[offset], texture.Pixels[offset + 1], texture.Pixels[offset + 2], texture.Pixels[offset + 3]);
        }
    }
    public void ClearScene() { _scene = null; InvalidateVisual(); }
    public void ResetView() { _yaw = -0.72f; _pitch = 0.72f; _zoom = 1; InvalidateVisual(); }
    public void SetVisibility(bool terrain, bool objects, bool wireframe) { _terrain = terrain; _objects = objects; _wireframe = wireframe; InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(Brush.Parse("#080B10"), Bounds); if (_scene is null) { context.DrawText(new FormattedText("Build a terrain + placement scene from a loaded ADT", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 13, Brush.Parse("#8995A9")), new Point(16, 28)); return; }
        context.Custom(new DrawOperation(Bounds, _scene, _yaw, _pitch, _zoom, _terrain, _objects, _wireframe));
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e) { base.OnPointerPressed(e); if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return; _dragStart = e.GetPosition(this); e.Pointer.Capture(this); e.Handled = true; }
    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); if (_dragStart is not { } start || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return; var point = e.GetPosition(this); _yaw += (float)(point.X - start.X) * 0.01f; _pitch = Math.Clamp(_pitch + (float)(point.Y - start.Y) * 0.01f, -1.5f, 1.5f); _dragStart = point; InvalidateVisual(); }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { base.OnPointerReleased(e); _dragStart = null; e.Pointer.Capture(null); }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) { base.OnPointerWheelChanged(e); _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.12f : 0.89f), 0.08f, 20f); InvalidateVisual(); e.Handled = true; }

    private sealed class DrawOperation(Rect bounds, Scene scene, float yaw, float pitch, float zoom, bool terrain, bool objects, bool wireframe) : ICustomDrawOperation
    {
        private readonly record struct Face(float Depth, float Ax, float Ay, float Bx, float By, float Cx, float Cy, SKColor AColor, SKColor BColor, SKColor CColor);
        public Rect Bounds => bounds; public bool HitTest(Point point) => bounds.Contains(point); public bool Equals(ICustomDrawOperation? other) => false; public void Dispose() { }
        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>(); if (feature is null) return; using var lease = feature.Lease(); var canvas = lease.SkCanvas; var width = (float)bounds.Width; var height = (float)bounds.Height; if (width <= 1 || height <= 1) return;
            var visible = scene.Meshes.Where(mesh => mesh.Kind == "terrain" ? terrain : objects).ToArray(); if (visible.Length == 0) return; var center = (scene.Minimum + scene.Maximum) * 0.5f; var extent = scene.Maximum - scene.Minimum; var largest = Math.Max(extent.X, Math.Max(extent.Y, extent.Z)); if (!float.IsFinite(largest) || largest <= 0.0001f) return;
            var view = Matrix4x4.CreateRotationZ(yaw) * Matrix4x4.CreateRotationX(pitch); var scale = Math.Min(width, height) * 0.43f / largest * zoom; const int faceBudget = 100_000; var totalTriangles = visible.Sum(mesh => mesh.Indices.Count / 3); var sample = Math.Max(1, (int)Math.Ceiling(totalTriangles / (double)faceBudget)); var faces = new List<Face>(Math.Min(totalTriangles, faceBudget)); var light = Vector3.Normalize(new Vector3(-0.4f, -0.55f, 0.85f));
            foreach (var mesh in visible)
            {
                var transformed = new Vector3[mesh.Vertices.Count]; for (var index = 0; index < transformed.Length; index++) transformed[index] = Vector3.Transform(Vector3.Transform(mesh.Vertices[index], mesh.Transform) - center, view);
                for (var index = 0; index + 2 < mesh.Indices.Count; index += 3 * sample)
                {
                    var ia = mesh.Indices[index]; var ib = mesh.Indices[index + 1]; var ic = mesh.Indices[index + 2]; if ((uint)ia >= transformed.Length || (uint)ib >= transformed.Length || (uint)ic >= transformed.Length) continue; var a = transformed[ia]; var b = transformed[ib]; var c = transformed[ic];
                    var ax = width * .5f + a.X * scale; var ay = height * .5f - a.Z * scale; var bx = width * .5f + b.X * scale; var by = height * .5f - b.Z * scale; var cx = width * .5f + c.X * scale; var cy = height * .5f - c.Z * scale; if (Math.Abs((bx - ax) * (cy - ay) - (by - ay) * (cx - ax)) < .015f) continue;
                    var normal = Vector3.Cross(b - a, c - a); if (normal.LengthSquared() > .000001f) normal = Vector3.Normalize(normal); var shade = Math.Clamp(.28f + .72f * Math.Abs(Vector3.Dot(normal, light)), .22f, 1f); var baseA = mesh.VertexColors is null ? mesh.Color : mesh.VertexColors[ia]; var baseB = mesh.VertexColors is null ? mesh.Color : mesh.VertexColors[ib]; var baseC = mesh.VertexColors is null ? mesh.Color : mesh.VertexColors[ic]; faces.Add(new((a.Y + b.Y + c.Y) / 3f, ax, ay, bx, by, cx, cy, Shade(baseA, shade), Shade(baseB, shade), Shade(baseC, shade)));
                }
            }
            faces.Sort(static (left, right) => right.Depth.CompareTo(left.Depth)); var positions = new SKPoint[faces.Count * 3]; var colors = new SKColor[positions.Length];
            for (var index = 0; index < faces.Count; index++) { var face = faces[index]; var offset = index * 3; positions[offset] = new(face.Ax, face.Ay); positions[offset + 1] = new(face.Bx, face.By); positions[offset + 2] = new(face.Cx, face.Cy); colors[offset] = face.AColor; colors[offset + 1] = face.BColor; colors[offset + 2] = face.CColor; }
            using (var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill }) using (var vertices = SKVertices.CreateCopy(SKVertexMode.Triangles, positions, null, colors)) canvas.DrawVertices(vertices, SKBlendMode.SrcOver, fill);
            if (wireframe) { using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = .65f, Color = new SKColor(7, 11, 17, 110) }; using var path = new SKPath(); foreach (var face in faces) { path.Rewind(); path.MoveTo(face.Ax, face.Ay); path.LineTo(face.Bx, face.By); path.LineTo(face.Cx, face.Cy); path.Close(); canvas.DrawPath(path, edge); } }
            using var text = new SKPaint { IsAntialias = true, Color = new SKColor(223, 230, 240) }; using var font = new SKFont(SKTypeface.Default, 13); using var hint = new SKFont(SKTypeface.Default, 12); canvas.DrawText($"ADT terrain + {scene.M2:N0} M2 + {scene.Wmo:N0} WMO · {faces.Count:N0}/{scene.SourceTriangles:N0} rendered triangles · provenance {scene.Provenance}", 12, 22, SKTextAlign.Left, font, text); text.Color = scene.Unresolved == 0 ? new SKColor(155, 194, 163) : new SKColor(255, 186, 91); canvas.DrawText(scene.Unresolved == 0 ? "All selected placements resolved" : $"{scene.Unresolved:N0} placement(s) unresolved or outside the selected provenance", 12, 40, SKTextAlign.Left, hint, text); text.Color = scene.MaterialCells == 0 || scene.CompleteMaterialCells < scene.MaterialCells ? new SKColor(255, 186, 91) : new SKColor(155, 194, 163); canvas.DrawText(scene.MaterialCells == 0 ? "Diagnostic terrain color · no material set loaded" : $"MCLY/MCAL terrain materials · {scene.CompleteMaterialCells:N0}/{scene.MaterialCells:N0} cells complete", 12, 57, SKTextAlign.Left, hint, text); text.Color = new SKColor(160, 172, 190); canvas.DrawText("Drag to rotate · wheel to zoom", 12, height - 12, SKTextAlign.Left, hint, text);

            static SKColor Shade(SKColor color, float amount) => new((byte)(color.Red * amount), (byte)(color.Green * amount), (byte)(color.Blue * amount), color.Alpha);
        }
    }
}
