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

public sealed class M2PreviewView : Control
{
    private M2PreviewGeometry? _geometry;
    private float _yaw = -0.65f;
    private float _pitch = 0.35f;
    private float _zoom = 1;
    private Avalonia.Point? _dragStart;

    public M2PreviewView() => ClipToBounds = true;

    public void SetGeometry(M2PreviewGeometry geometry)
    {
        _geometry = geometry;
        _yaw = -0.65f;
        _pitch = 0.35f;
        _zoom = 1;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#090D14")), Bounds);
        if (_geometry is null) return;
        context.Custom(new M2DrawOperation(Bounds, _geometry, _yaw, _pitch, _zoom));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragStart = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragStart is not { } start || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var current = e.GetPosition(this);
        _yaw += (float)(current.X - start.X) * 0.012f;
        _pitch = Math.Clamp(_pitch + (float)(current.Y - start.Y) * 0.012f, -1.5f, 1.5f);
        _dragStart = current;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragStart = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.12f : 0.89f), 0.15f, 8f);
        InvalidateVisual();
        e.Handled = true;
    }

    private sealed class M2DrawOperation(Rect bounds, M2PreviewGeometry geometry, float yaw, float pitch, float zoom) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Avalonia.Point point) => Bounds.Contains(point);
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature is null) return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            var width = (float)bounds.Width;
            var height = (float)bounds.Height;
            var center = (geometry.Minimum + geometry.Maximum) * 0.5f;
            var extent = geometry.Maximum - geometry.Minimum;
            var largest = Math.Max(extent.X, Math.Max(extent.Y, extent.Z));
            if (!float.IsFinite(largest) || largest <= 0.00001f) return;

            var scale = Math.Min(width, height) * 0.42f / largest * zoom;
            var rotation = Matrix4x4.CreateRotationZ(yaw) * Matrix4x4.CreateRotationX(pitch);
            var transformed = new Vector3[geometry.Vertices.Count];
            for (var index = 0; index < transformed.Length; index++)
                transformed[index] = Vector3.Transform(geometry.Vertices[index] - center, rotation);

            var triangleCount = geometry.TriangleIndices.Count / 3;
            var sampling = Math.Max(1, (int)Math.Ceiling(triangleCount / 30_000d));
            var faces = new List<Face>(Math.Min(triangleCount, 30_000));
            var light = Vector3.Normalize(new Vector3(-0.35f, -0.65f, 0.9f));
            for (var triangle = 0; triangle < triangleCount; triangle += sampling)
            {
                var offset = triangle * 3;
                var a = transformed[geometry.TriangleIndices[offset]];
                var b = transformed[geometry.TriangleIndices[offset + 1]];
                var c = transformed[geometry.TriangleIndices[offset + 2]];
                var ax = width * 0.5f + a.X * scale; var ay = height * 0.5f - a.Z * scale;
                var bx = width * 0.5f + b.X * scale; var by = height * 0.5f - b.Z * scale;
                var cx = width * 0.5f + c.X * scale; var cy = height * 0.5f - c.Z * scale;
                var area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
                if (Math.Abs(area) < 0.02f) continue;
                var normal = Vector3.Cross(b - a, c - a);
                if (normal.LengthSquared() > 0.000001f) normal = Vector3.Normalize(normal);
                var brightness = Math.Clamp(0.25f + 0.75f * Math.Abs(Vector3.Dot(normal, light)), 0.2f, 1f);
                faces.Add(new((a.Y + b.Y + c.Y) / 3f, ax, ay, bx, by, cx, cy, (byte)Math.Round(brightness * 15)));
            }
            faces.Sort(static (left, right) => right.Depth.CompareTo(left.Depth));

            using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.65f, Color = new SKColor(5, 9, 15, 58) };
            using var path = new SKPath();
            foreach (var face in faces)
            {
                var value = 48 + face.Shade * 11;
                fill.Color = new SKColor((byte)Math.Min(220, value / 2 + 45), (byte)Math.Min(230, value), (byte)Math.Min(255, value + 24));
                path.Rewind(); path.MoveTo(face.Ax, face.Ay); path.LineTo(face.Bx, face.By); path.LineTo(face.Cx, face.Cy); path.Close();
                canvas.DrawPath(path, fill); canvas.DrawPath(path, edge);
            }

            using var text = new SKPaint { IsAntialias = true, Color = new SKColor(225, 231, 240) };
            using var titleFont = new SKFont(SKTypeface.Default, 13);
            using var hintFont = new SKFont(SKTypeface.Default, 12);
            canvas.DrawText($"{Path.GetFileName(geometry.ModelPath)} · {geometry.Vertices.Count:N0} vertices · {faces.Count:N0} displayed faces", 12, 23, SKTextAlign.Left, titleFont, text);
            text.Color = new SKColor(170, 182, 200);
            canvas.DrawText("Drag to rotate · wheel to zoom", 12, height - 12, SKTextAlign.Left, hintFont, text);
        }

        private readonly record struct Face(float Depth, float Ax, float Ay, float Bx, float By, float Cx, float Cy, byte Shade);
    }
}
