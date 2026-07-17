using System.Numerics;
using WoWCrucible.Core;

namespace WoWCrucible.App;

internal sealed class M2PreviewControl : Control
{
    private M2PreviewGeometry? _geometry;
    private string _message = "No model selected";
    private float _yaw = -0.65f, _pitch = 0.35f, _zoom = 1f;
    private Point? _dragStart;

    public M2PreviewControl()
    {
        DoubleBuffered = true; BackColor = Color.FromArgb(24, 27, 34); ForeColor = Color.Gainsboro; Dock = DockStyle.Fill;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    public void LoadModel(string path)
    {
        try
        {
            _geometry = M2PreviewGeometryService.Load(path); _message = $"{Path.GetFileName(path)} · {_geometry.Vertices.Count:N0} vertices · {_geometry.TriangleIndices.Count / 3:N0} triangles";
            _yaw = -0.65f; _pitch = 0.35f; _zoom = 1f; Invalidate();
        }
        catch (Exception ex) { _geometry = null; _message = ex.Message; Invalidate(); }
    }

    public void ClearPreview(string message = "No model selected") { _geometry = null; _message = message; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.Clear(BackColor);
        if (_geometry is null) { DrawMessage(e.Graphics, _message); return; }
        var width = ClientSize.Width; var height = ClientSize.Height;
        if (width < 10 || height < 10) return;
        var center = (_geometry.Minimum + _geometry.Maximum) * 0.5f; var extent = _geometry.Maximum - _geometry.Minimum;
        var largest = Math.Max(extent.X, Math.Max(extent.Y, extent.Z));
        if (!float.IsFinite(largest) || largest <= 0.00001f) { DrawMessage(e.Graphics, "Model bounds are empty."); return; }
        var scale = Math.Min(width, height) * 0.42f / largest * _zoom;
        var rotation = Matrix4x4.CreateRotationZ(_yaw) * Matrix4x4.CreateRotationX(_pitch);
        var transformed = new Vector3[_geometry.Vertices.Count];
        for (var index = 0; index < transformed.Length; index++) transformed[index] = Vector3.Transform(_geometry.Vertices[index] - center, rotation);
        var triangleCount = _geometry.TriangleIndices.Count / 3; var sampling = Math.Max(1, (int)Math.Ceiling(triangleCount / 35_000d));
        var faces = new List<Face>(Math.Min(triangleCount, 35_000)); var light = Vector3.Normalize(new Vector3(-0.35f, -0.65f, 0.9f));
        for (var triangle = 0; triangle < triangleCount; triangle += sampling)
        {
            var offset = triangle * 3; var a = transformed[_geometry.TriangleIndices[offset]]; var b = transformed[_geometry.TriangleIndices[offset + 1]]; var c = transformed[_geometry.TriangleIndices[offset + 2]];
            var pa = new PointF(width * 0.5f + a.X * scale, height * 0.5f - a.Z * scale); var pb = new PointF(width * 0.5f + b.X * scale, height * 0.5f - b.Z * scale); var pc = new PointF(width * 0.5f + c.X * scale, height * 0.5f - c.Z * scale);
            var area = (pb.X - pa.X) * (pc.Y - pa.Y) - (pb.Y - pa.Y) * (pc.X - pa.X); if (Math.Abs(area) < 0.02f) continue;
            var normal = Vector3.Cross(b - a, c - a); if (normal.LengthSquared() > 0.000001f) normal = Vector3.Normalize(normal);
            var brightness = Math.Clamp(0.25f + 0.75f * Math.Abs(Vector3.Dot(normal, light)), 0.2f, 1f);
            faces.Add(new((a.Y + b.Y + c.Y) / 3f, pa, pb, pc, (int)Math.Round(brightness * 15)));
        }
        faces.Sort((left, right) => right.Depth.CompareTo(left.Depth));
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var brushes = Enumerable.Range(0, 16).Select(index => { var value = 48 + index * 11; return new SolidBrush(Color.FromArgb(Math.Min(220, value / 2 + 45), Math.Min(230, value), Math.Min(255, value + 24))); }).ToArray();
        using var edge = new Pen(Color.FromArgb(45, 8, 12, 18), 0.7f);
        try { foreach (var face in faces) { PointF[] points = [face.A, face.B, face.C]; e.Graphics.FillPolygon(brushes[face.Shade], points); e.Graphics.DrawPolygon(edge, points); } }
        finally { foreach (var brush in brushes) brush.Dispose(); }
        using var overlay = new SolidBrush(Color.FromArgb(210, 235, 238, 244)); e.Graphics.DrawString(_message + $" · showing {faces.Count:N0} faces", Font, overlay, new PointF(8, 8));
        using var hint = new SolidBrush(Color.FromArgb(150, 210, 215, 225)); e.Graphics.DrawString("Drag to rotate · wheel to zoom", Font, hint, new PointF(8, height - Font.Height - 8));
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { _dragStart = e.Location; Capture = true; } }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); if (_dragStart is not Point start || e.Button != MouseButtons.Left) return;
        _yaw += (e.X - start.X) * 0.012f; _pitch = Math.Clamp(_pitch + (e.Y - start.Y) * 0.012f, -1.5f, 1.5f); _dragStart = e.Location; Invalidate();
    }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _dragStart = null; Capture = false; }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.12f : 0.89f), 0.15f, 8f); Invalidate(); }

    private void DrawMessage(Graphics graphics, string message)
    {
        using var brush = new SolidBrush(Color.FromArgb(205, ForeColor)); using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString(message, Font, brush, ClientRectangle, format);
    }

    private readonly record struct Face(float Depth, PointF A, PointF B, PointF C, int Shade);
}
