using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop.Controls;

public sealed class M2PreviewView : Control, IDisposable
{
    private M2PreviewGeometry? _geometry;
    private SKBitmap? _texture;
    private readonly Dictionary<int, SKBitmap> _materialTextures = [];
    private float _yaw = -0.65f;
    private float _pitch = 0.35f;
    private float _zoom = 1;
    private Avalonia.Point? _dragStart;
    private bool _showAttachments;
    private int? _highlightedAttachmentIndex;

    public M2PreviewView() => ClipToBounds = true;

    public void SetGeometry(M2PreviewGeometry geometry)
    {
        _geometry = geometry;
        ClearMaterialTextures();
        _yaw = -0.65f;
        _pitch = 0.35f;
        _zoom = 1;
        InvalidateVisual();
    }

    public void ClearGeometry()
    {
        _geometry = null;
        InvalidateVisual();
    }

    public void SetTexture(string? previewPath)
    {
        _texture?.Dispose(); _texture = null;
        if (!string.IsNullOrWhiteSpace(previewPath) && File.Exists(previewPath)) _texture = SKBitmap.Decode(previewPath);
        InvalidateVisual();
    }

    public void SetDecodedTexture(RgbaTexture? texture)
    {
        ClearMaterialTextures();
        _texture?.Dispose(); _texture = null;
        if (texture is not null)
        {
            var bitmap = new SKBitmap(new SKImageInfo(texture.Width, texture.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            var rowBytes = checked(texture.Width * 4); var address = bitmap.GetPixels();
            for (var row = 0; row < texture.Height; row++) Marshal.Copy(texture.Pixels, row * rowBytes, IntPtr.Add(address, row * bitmap.RowBytes), rowBytes);
            _texture = bitmap;
        }
        InvalidateVisual();
    }

    public void SetDecodedTextures(IReadOnlyDictionary<int, RgbaTexture> textures)
    {
        _texture?.Dispose(); _texture = null;
        ClearMaterialTextures();
        foreach (var (textureDefinitionIndex, texture) in textures) _materialTextures[textureDefinitionIndex] = CreateBitmap(texture);
        InvalidateVisual();
    }

    public void SetAttachmentOverlay(bool visible, int? highlightedAttachmentIndex = null)
    {
        _showAttachments = visible;
        _highlightedAttachmentIndex = highlightedAttachmentIndex;
        InvalidateVisual();
    }

    private static SKBitmap CreateBitmap(RgbaTexture texture)
    {
        var bitmap = new SKBitmap(new SKImageInfo(texture.Width, texture.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var rowBytes = checked(texture.Width * 4); var address = bitmap.GetPixels();
        for (var row = 0; row < texture.Height; row++) Marshal.Copy(texture.Pixels, row * rowBytes, IntPtr.Add(address, row * bitmap.RowBytes), rowBytes);
        return bitmap;
    }

    private void ClearMaterialTextures()
    {
        foreach (var texture in _materialTextures.Values) texture.Dispose();
        _materialTextures.Clear();
    }

    public void Dispose()
    {
        _texture?.Dispose(); _texture = null;
        ClearMaterialTextures();
        _geometry = null;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#090D14")), Bounds);
        if (_geometry is null) return;
        context.Custom(new M2DrawOperation(Bounds, _geometry, _texture, _materialTextures, _yaw, _pitch, _zoom, _showAttachments, _highlightedAttachmentIndex));
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

    private sealed class M2DrawOperation(Rect bounds, M2PreviewGeometry geometry, SKBitmap? texture, IReadOnlyDictionary<int, SKBitmap> materialTextures, float yaw, float pitch, float zoom, bool showAttachments, int? highlightedAttachmentIndex) : ICustomDrawOperation
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
            IReadOnlyList<M2PreviewBatch> batches = geometry.Batches.Count == 0 ? [new M2PreviewBatch(0, 0, 0, geometry.TriangleIndices.Count, null, null)] : geometry.Batches;
            foreach (var batch in batches)
            {
                var end = Math.Min(geometry.TriangleIndices.Count, batch.TriangleStart + batch.TriangleIndexCount);
                for (var offset = batch.TriangleStart; offset + 2 < end; offset += 3 * sampling)
                {
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
                faces.Add(new((a.Y + b.Y + c.Y) / 3f, geometry.TriangleIndices[offset], geometry.TriangleIndices[offset + 1], geometry.TriangleIndices[offset + 2], ax, ay, bx, by, cx, cy, (byte)Math.Round(brightness * 15), batch.TextureDefinitionIndex));
                }
            }
            faces.Sort(static (left, right) => right.Depth.CompareTo(left.Depth));

            using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.65f, Color = new SKColor(5, 9, 15, 58) };
            using var path = new SKPath();
            foreach (var face in faces.Where(face => texture is null && (face.TextureDefinitionIndex is not { } index || !materialTextures.ContainsKey(index))))
            {
                var value = 48 + face.Shade * 11;
                fill.Color = new SKColor((byte)Math.Min(220, value / 2 + 45), (byte)Math.Min(230, value), (byte)Math.Min(255, value + 24));
                path.Rewind(); path.MoveTo(face.Ax, face.Ay); path.LineTo(face.Bx, face.By); path.LineTo(face.Cx, face.Cy); path.Close();
                canvas.DrawPath(path, fill); canvas.DrawPath(path, edge);
            }

            var texturedFaces = faces.Where(face => texture is not null || face.TextureDefinitionIndex is { } index && materialTextures.ContainsKey(index))
                .GroupBy(face => texture is not null ? -1 : face.TextureDefinitionIndex!.Value);
            foreach (var group in texturedFaces)
            {
                var activeTexture = texture ?? materialTextures[group.Key]; var groupFaces = group.ToArray();
                var positions = new SKPoint[groupFaces.Length * 3]; var coordinates = new SKPoint[groupFaces.Length * 3]; var colors = new SKColor[groupFaces.Length * 3];
                for (var index = 0; index < groupFaces.Length; index++)
                {
                    var face = groupFaces[index]; var offset = index * 3;
                    positions[offset] = new(face.Ax, face.Ay); positions[offset + 1] = new(face.Bx, face.By); positions[offset + 2] = new(face.Cx, face.Cy);
                    AddUv(offset, face.Ia); AddUv(offset + 1, face.Ib); AddUv(offset + 2, face.Ic);
                    var shade = (byte)Math.Clamp(70 + face.Shade * 12, 0, 255); colors[offset] = colors[offset + 1] = colors[offset + 2] = new SKColor(shade, shade, shade, 255);
                }
                using var shader = SKShader.CreateBitmap(activeTexture, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
                using var paint = new SKPaint { IsAntialias = true, Shader = shader };
                using var mesh = SKVertices.CreateCopy(SKVertexMode.Triangles, positions, coordinates, colors);
                canvas.DrawVertices(mesh, SKBlendMode.Modulate, paint);
                void AddUv(int destination, int vertex)
                {
                    var uv = geometry.TextureCoordinates[vertex]; coordinates[destination] = new(uv.X * activeTexture.Width, uv.Y * activeTexture.Height);
                }
            }

            if (showAttachments && geometry.Attachments.Count > 0)
            {
                using var marker = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(69, 211, 255, 210) };
                using var markerEdge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = new SKColor(6, 14, 24, 230) };
                using var labelBack = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(5, 10, 18, 220) };
                using var labelText = new SKPaint { IsAntialias = true, Color = new SKColor(232, 247, 255) };
                using var labelFont = new SKFont(SKTypeface.Default, 12);
                foreach (var attachment in geometry.Attachments)
                {
                    var point = Vector3.Transform(attachment.Position - center, rotation);
                    var x = width * 0.5f + point.X * scale; var y = height * 0.5f - point.Z * scale;
                    var selected = highlightedAttachmentIndex == attachment.Index;
                    var radius = selected ? 6f : 3.2f;
                    marker.Color = selected ? new SKColor(255, 189, 72, 245) : new SKColor(69, 211, 255, 190);
                    canvas.DrawCircle(x, y, radius, marker); canvas.DrawCircle(x, y, radius, markerEdge);
                    if (!selected) continue;
                    var label = $"{attachment.Id:N0} · {attachment.Name} · bone {attachment.BoneIndex:N0}";
                    var widthText = labelFont.MeasureText(label, labelText); var left = Math.Clamp(x + 9, 4, Math.Max(4, width - widthText - 12)); var top = Math.Clamp(y - 21, 4, Math.Max(4, height - 25));
                    canvas.DrawRoundRect(new SKRect(left - 4, top - 2, left + widthText + 4, top + 17), 4, 4, labelBack);
                    canvas.DrawText(label, left, top + 12, SKTextAlign.Left, labelFont, labelText);
                }
            }

            using var text = new SKPaint { IsAntialias = true, Color = new SKColor(225, 231, 240) };
            using var titleFont = new SKFont(SKTypeface.Default, 13);
            using var hintFont = new SKFont(SKTypeface.Default, 12);
            var geosets = geometry.Submeshes.Count == 0 ? "complete mesh" : $"{geometry.Submeshes.Count(section => section.Visible):N0}/{geometry.Submeshes.Count:N0} geosets";
            var textureCount = texture is not null ? "manual texture" : $"{materialTextures.Count:N0} material texture(s)";
            var attachments = showAttachments ? $" · {geometry.Attachments.Count:N0} attachment point(s)" : string.Empty;
            canvas.DrawText($"{Path.GetFileName(geometry.ModelPath)} · {geosets} · {textureCount} · {faces.Count:N0} displayed faces{attachments}", 12, 23, SKTextAlign.Left, titleFont, text);
            text.Color = new SKColor(170, 182, 200);
            canvas.DrawText("Drag to rotate · wheel to zoom", 12, height - 12, SKTextAlign.Left, hintFont, text);
        }

        private readonly record struct Face(float Depth, int Ia, int Ib, int Ic, float Ax, float Ay, float Bx, float By, float Cx, float Cy, byte Shade, int? TextureDefinitionIndex);
    }
}
