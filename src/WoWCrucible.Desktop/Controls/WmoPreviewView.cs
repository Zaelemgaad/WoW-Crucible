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

public sealed class WmoPreviewView : UserControl, IDisposable
{
    private sealed record GroupChoice(int? Index, string Label) { public override string ToString() => Label; }
    private readonly WmoPreviewCanvas _canvas = new();
    private readonly ComboBox _groups = new() { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    private readonly CheckBox _wireframe = new() { Content = "Wireframe overlay" };

    public WmoPreviewView()
    {
        ClipToBounds = true;
        _wireframe.Margin = new Thickness(8, 0, 0, 0);
        var options = new WrapPanel { Children = { new TextBlock { Text = "Visible group", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }, _wireframe } };
        var controls = new StackPanel { Spacing = 5, Margin = new Thickness(7), Children = { options, _groups } };
        var root = new Grid { RowDefinitions = new("Auto,*") }; root.Children.Add(controls); root.Children.Add(_canvas); Grid.SetRow(_canvas, 1); Content = root;
        _groups.SelectionChanged += (_, _) => _canvas.SetGroup((_groups.SelectedItem as GroupChoice)?.Index);
        _wireframe.IsCheckedChanged += (_, _) => _canvas.SetWireframe(_wireframe.IsChecked == true);
    }

    public void SetGeometry(WmoPreviewGeometry geometry)
    {
        _canvas.SetGeometry(geometry);
        _groups.ItemsSource = new[] { new GroupChoice(null, $"All {geometry.Groups.Count:N0} loaded groups") }
            .Concat(geometry.Groups.Select(group => new GroupChoice(group.Index, $"{group.Index:000} · {Path.GetFileName(group.Path)} · {group.TriangleIndexCount / 3:N0} triangles"))).ToArray();
        _groups.SelectedIndex = 0;
    }

    public void SetDecodedTextures(IReadOnlyDictionary<int, RgbaTexture> textures) => _canvas.SetDecodedTextures(textures);
    public void SetPlacement(MapWmoPlacement? placement) => _canvas.SetPlacement(placement);
    public void ClearGeometry() { _groups.ItemsSource = null; _canvas.ClearGeometry(); }
    public void Dispose() => _canvas.Dispose();
}

internal sealed class WmoPreviewCanvas : Control, IDisposable
{
    private WmoPreviewGeometry? _geometry;
    private readonly Dictionary<int, SKBitmap> _textures = [];
    private float _yaw = -0.65f;
    private float _pitch = 0.35f;
    private float _zoom = 1;
    private Avalonia.Point? _dragStart;
    private int? _group;
    private bool _wireframe;
    private MapWmoPlacement? _placement;

    public WmoPreviewCanvas() => ClipToBounds = true;
    public void SetGeometry(WmoPreviewGeometry geometry) { _geometry = geometry; _yaw = -0.65f; _pitch = 0.35f; _zoom = 1; _group = null; _placement = null; ClearTextures(); InvalidateVisual(); }
    public void ClearGeometry() { _geometry = null; _group = null; _placement = null; ClearTextures(); InvalidateVisual(); }
    public void SetGroup(int? group) { _group = group; InvalidateVisual(); }
    public void SetWireframe(bool enabled) { _wireframe = enabled; InvalidateVisual(); }
    public void SetPlacement(MapWmoPlacement? placement) { _placement = placement; InvalidateVisual(); }
    public void SetDecodedTextures(IReadOnlyDictionary<int, RgbaTexture> textures)
    {
        ClearTextures(); foreach (var (material, texture) in textures) _textures[material] = CreateBitmap(texture); InvalidateVisual();
    }

    private static SKBitmap CreateBitmap(RgbaTexture texture)
    {
        var bitmap = new SKBitmap(new SKImageInfo(texture.Width, texture.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var bytes = checked(texture.Width * 4); var address = bitmap.GetPixels();
        for (var row = 0; row < texture.Height; row++) Marshal.Copy(texture.Pixels, row * bytes, IntPtr.Add(address, row * bitmap.RowBytes), bytes);
        return bitmap;
    }
    private void ClearTextures() { foreach (var texture in _textures.Values) texture.Dispose(); _textures.Clear(); }
    public void Dispose() { ClearTextures(); _geometry = null; }

    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(new SolidColorBrush(Color.Parse("#090D14")), Bounds);
        if (_geometry is not null) context.Custom(new WmoDrawOperation(Bounds, _geometry, _textures, _yaw, _pitch, _zoom, _group, _wireframe, _placement));
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e) { base.OnPointerPressed(e); if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return; _dragStart = e.GetPosition(this); e.Pointer.Capture(this); e.Handled = true; }
    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); if (_dragStart is not { } start || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return; var current = e.GetPosition(this); _yaw += (float)(current.X - start.X) * 0.012f; _pitch = Math.Clamp(_pitch + (float)(current.Y - start.Y) * 0.012f, -1.5f, 1.5f); _dragStart = current; InvalidateVisual(); }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { base.OnPointerReleased(e); _dragStart = null; e.Pointer.Capture(null); }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) { base.OnPointerWheelChanged(e); _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.12f : 0.89f), 0.08f, 12f); InvalidateVisual(); e.Handled = true; }

    private sealed class WmoDrawOperation(Rect bounds, WmoPreviewGeometry geometry, IReadOnlyDictionary<int, SKBitmap> textures, float yaw, float pitch, float zoom, int? selectedGroup, bool wireframe, MapWmoPlacement? placement) : ICustomDrawOperation
    {
        private readonly record struct Face(float Depth, int Material, int Ia, int Ib, int Ic, float Ax, float Ay, float Bx, float By, float Cx, float Cy, byte Shade, SKBitmap? Texture, uint BlendMode);
        private readonly record struct TextureGroup(int Material, SKBitmap Texture, uint BlendMode);
        public Rect Bounds => bounds;
        public bool HitTest(Avalonia.Point point) => Bounds.Contains(point);
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>(); if (feature is null) return; using var lease = feature.Lease(); var canvas = lease.SkCanvas;
            var width = (float)bounds.Width; var height = (float)bounds.Height; if (width <= 1 || height <= 1) return;
            var visibleGroups = selectedGroup is null ? geometry.Groups : geometry.Groups.Where(group => group.Index == selectedGroup).ToArray();
            if (visibleGroups.Count == 0) return;
            var minimum = new Vector3(float.PositiveInfinity); var maximum = new Vector3(float.NegativeInfinity);
            foreach (var group in visibleGroups) { minimum = Vector3.Min(minimum, group.Minimum); maximum = Vector3.Max(maximum, group.Maximum); }
            var center = (minimum + maximum) * 0.5f; var placementTransform = placement is null ? Matrix4x4.Identity :
                Matrix4x4.CreateScale(placement.Scale) * Matrix4x4.CreateRotationX(Degrees(placement.Orientation.X)) * Matrix4x4.CreateRotationY(Degrees(placement.Orientation.Y)) * Matrix4x4.CreateRotationZ(Degrees(placement.Orientation.Z));
            var rotation = placementTransform * Matrix4x4.CreateRotationZ(yaw) * Matrix4x4.CreateRotationX(pitch);
            var transformed = new Vector3[geometry.Vertices.Count]; for (var index = 0; index < transformed.Length; index++) transformed[index] = Vector3.Transform(geometry.Vertices[index] - center, rotation);
            var transformedMinimum = new Vector3(float.PositiveInfinity); var transformedMaximum = new Vector3(float.NegativeInfinity);
            foreach (var group in visibleGroups) for (var index = group.VertexStart; index < group.VertexStart + group.VertexCount; index++) { transformedMinimum = Vector3.Min(transformedMinimum, transformed[index]); transformedMaximum = Vector3.Max(transformedMaximum, transformed[index]); }
            var extent = transformedMaximum - transformedMinimum; var largest = Math.Max(extent.X, Math.Max(extent.Y, extent.Z)); if (!float.IsFinite(largest) || largest <= 0.00001f) return;
            var scale = Math.Min(width, height) * 0.42f / largest * zoom;
            var visibleSet = visibleGroups.Select(group => group.Index).ToHashSet(); var renderBatches = geometry.Batches.Where(batch => visibleSet.Contains(batch.GroupIndex)).ToArray();
            var triangleCount = renderBatches.Sum(batch => batch.TriangleIndexCount) / 3; var sampling = Math.Max(1, (int)Math.Ceiling(triangleCount / 50_000d)); var faces = new List<Face>(Math.Min(triangleCount, 50_000)); var light = Vector3.Normalize(new Vector3(-0.35f, -0.65f, 0.9f));
            foreach (var batch in renderBatches)
            {
                var end = Math.Min(geometry.TriangleIndices.Count, batch.TriangleStart + batch.TriangleIndexCount); textures.TryGetValue(batch.MaterialIndex, out var texture); var blend = (uint)(batch.MaterialIndex >= 0 && batch.MaterialIndex < geometry.Materials.Count ? geometry.Materials[batch.MaterialIndex].BlendMode : 0);
                for (var offset = batch.TriangleStart; offset + 2 < end; offset += 3 * sampling)
                {
                    var ia = geometry.TriangleIndices[offset]; var ib = geometry.TriangleIndices[offset + 1]; var ic = geometry.TriangleIndices[offset + 2]; var a = transformed[ia]; var b = transformed[ib]; var c = transformed[ic];
                    var ax = width * 0.5f + a.X * scale; var ay = height * 0.5f - a.Z * scale; var bx = width * 0.5f + b.X * scale; var by = height * 0.5f - b.Z * scale; var cx = width * 0.5f + c.X * scale; var cy = height * 0.5f - c.Z * scale;
                    if (Math.Abs((bx - ax) * (cy - ay) - (by - ay) * (cx - ax)) < 0.02f) continue;
                    var normal = Vector3.Cross(b - a, c - a); if (normal.LengthSquared() > 0.000001f) normal = Vector3.Normalize(normal); var brightness = Math.Clamp(0.24f + 0.76f * Math.Abs(Vector3.Dot(normal, light)), 0.2f, 1f);
                    faces.Add(new((a.Y + b.Y + c.Y) / 3f, batch.MaterialIndex, ia, ib, ic, ax, ay, bx, by, cx, cy, (byte)Math.Round(brightness * 15), texture, blend));
                }
            }
            faces.Sort(static (left, right) => right.Depth.CompareTo(left.Depth));
            using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill }; using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.7f, Color = new SKColor(5, 9, 15, 115) }; using var path = new SKPath();
            foreach (var face in faces.Where(face => face.Texture is null))
            {
                var color = MaterialColor(face.Material, face.Shade); fill.Color = color; path.Rewind(); path.MoveTo(face.Ax, face.Ay); path.LineTo(face.Bx, face.By); path.LineTo(face.Cx, face.Cy); path.Close(); canvas.DrawPath(path, fill); if (wireframe) canvas.DrawPath(path, edge);
            }
            foreach (var group in faces.Where(face => face.Texture is not null).GroupBy(face => new TextureGroup(face.Material, face.Texture!, face.BlendMode)))
            {
                var values = group.ToArray(); var positions = new SKPoint[values.Length * 3]; var coordinates = new SKPoint[values.Length * 3]; var colors = new SKColor[values.Length * 3];
                for (var index = 0; index < values.Length; index++)
                {
                    var face = values[index]; var offset = index * 3; positions[offset] = new(face.Ax, face.Ay); positions[offset + 1] = new(face.Bx, face.By); positions[offset + 2] = new(face.Cx, face.Cy); AddUv(offset, face.Ia); AddUv(offset + 1, face.Ib); AddUv(offset + 2, face.Ic); var shade = (byte)Math.Clamp(70 + face.Shade * 12, 0, 255); colors[offset] = colors[offset + 1] = colors[offset + 2] = new SKColor(shade, shade, shade, 255);
                }
                using var shader = SKShader.CreateBitmap(group.Key.Texture, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat); using var paint = new SKPaint { IsAntialias = true, Shader = shader, BlendMode = Blend(group.Key.BlendMode) }; using var mesh = SKVertices.CreateCopy(SKVertexMode.Triangles, positions, coordinates, colors); canvas.DrawVertices(mesh, SKBlendMode.Modulate, paint);
                if (wireframe) foreach (var face in values) { path.Rewind(); path.MoveTo(face.Ax, face.Ay); path.LineTo(face.Bx, face.By); path.LineTo(face.Cx, face.Cy); path.Close(); canvas.DrawPath(path, edge); }
                void AddUv(int destination, int vertex) { var uv = geometry.TextureCoordinates[vertex]; coordinates[destination] = new(uv.X * group.Key.Texture.Width, uv.Y * group.Key.Texture.Height); }
            }
            using var text = new SKPaint { IsAntialias = true, Color = new SKColor(225, 231, 240) }; using var titleFont = new SKFont(SKTypeface.Default, 13); using var hintFont = new SKFont(SKTypeface.Default, 12);
            var groupLabel = selectedGroup is null ? $"{visibleGroups.Count:N0} groups" : $"group {selectedGroup:000}"; var placementLabel = placement is null ? string.Empty : $" · UID {placement.UniqueId:N0} · MODF rot {placement.Orientation.X:0.#},{placement.Orientation.Y:0.#},{placement.Orientation.Z:0.#} · scale {placement.Scale:0.###}"; canvas.DrawText($"{Path.GetFileName(geometry.RootPath)} · {groupLabel} · {textures.Count:N0}/{geometry.Materials.Count:N0} textures · {faces.Count:N0} displayed faces{placementLabel}", 12, 23, SKTextAlign.Left, titleFont, text); text.Color = new SKColor(170, 182, 200); canvas.DrawText("Drag to rotate · wheel to zoom", 12, height - 12, SKTextAlign.Left, hintFont, text);
        }

        private static SKColor MaterialColor(int material, byte shade)
        {
            var seed = unchecked((uint)(material + 1) * 2654435761u); var brightness = 0.42f + shade / 15f * 0.58f; return new((byte)(((seed >> 16 & 0x7F) + 80) * brightness), (byte)(((seed >> 8 & 0x7F) + 80) * brightness), (byte)(((seed & 0x7F) + 80) * brightness), 255);
        }
        private static SKBlendMode Blend(uint mode) => mode switch { 3 or 4 => SKBlendMode.Plus, 5 or 6 => SKBlendMode.Modulate, _ => SKBlendMode.SrcOver };
        private static float Degrees(float value) => value * MathF.PI / 180f;
    }
}
