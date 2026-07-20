using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop.Controls;

internal sealed record TexturePixelHover(int X, int Y, byte R, byte G, byte B, byte A);

internal sealed class TexturePixelCanvas : Control, IDisposable
{
    private RgbaTexture? _texture;
    private WriteableBitmap? _bitmap;
    private TextureChannelView _view = new();
    private readonly List<TexturePoint> _stroke = [];
    private bool _painting;
    private bool _panning;
    private Point _lastPointer;
    private Point? _cursor;
    private double _zoom = 1;
    private Vector _pan;

    public double BrushRadius { get; set; } = 8;
    public event EventHandler<IReadOnlyList<TexturePoint>>? StrokeCompleted;
    public event EventHandler<TexturePixelHover?>? HoverChanged;

    public TexturePixelCanvas()
    {
        ClipToBounds = true; Focusable = true;
    }

    public void SetTexture(RgbaTexture? texture, TextureChannelView? view = null)
    {
        _texture = texture; if (view is not null) _view = view; RebuildBitmap(); InvalidateVisual();
    }

    public void SetChannelView(TextureChannelView view)
    {
        _view = view; RebuildBitmap(); InvalidateVisual();
    }

    public void RefreshPixels() { RebuildBitmap(); InvalidateVisual(); }
    public void ResetView() { _zoom = 1; _pan = default; InvalidateVisual(); }

    public override void Render(DrawingContext context)
    {
        base.Render(context); DrawCheckerboard(context);
        if (_bitmap is null || _texture is null) { DrawText(context, "Open a BLP to edit its decoded RGBA pixels.", new(18, 18)); return; }
        var destination = Destination(); context.DrawImage(_bitmap, new Rect(0, 0, _texture.Width, _texture.Height), destination);
        context.DrawRectangle(null, new Pen(Brushes.White, 1), destination);
        if (_cursor is { } cursor && TryTexturePoint(cursor, out var texturePoint))
        {
            var center = ScreenPoint(texturePoint); var radius = Math.Max(1, BrushRadius * Scale());
            context.DrawEllipse(null, new Pen(Brushes.White, 1), center, radius, radius); context.DrawEllipse(null, new Pen(Brushes.Black, 1), center, Math.Max(0, radius - 1), Math.Max(0, radius - 1));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); Focus(); var current = e.GetCurrentPoint(this); _lastPointer = current.Position; _cursor = current.Position;
        if (current.Properties.IsMiddleButtonPressed || current.Properties.IsRightButtonPressed)
        {
            _panning = true; e.Pointer.Capture(this); e.Handled = true; return;
        }
        if (!current.Properties.IsLeftButtonPressed || !TryTexturePoint(current.Position, out var point)) return;
        _painting = true; _stroke.Clear(); _stroke.Add(point); e.Pointer.Capture(this); InvalidateVisual(); e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); var position = e.GetPosition(this); _cursor = position;
        if (_panning)
        {
            _pan += position - _lastPointer; _lastPointer = position; InvalidateVisual(); e.Handled = true; return;
        }
        if (_painting && TryTexturePoint(position, out var point) && (_stroke.Count == 0 || Distance(_stroke[^1], point) >= 0.25)) _stroke.Add(point);
        PublishHover(position); InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e); var wasPainting = _painting; _painting = false; _panning = false; e.Pointer.Capture(null);
        if (wasPainting && _stroke.Count > 0) StrokeCompleted?.Invoke(this, _stroke.ToArray()); _stroke.Clear(); PublishHover(e.GetPosition(this)); InvalidateVisual(); e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e); if (!_painting && !_panning) { _cursor = null; HoverChanged?.Invoke(this, null); InvalidateVisual(); }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e); if (_texture is null || e.Delta.Y == 0) return;
        _zoom = Math.Clamp(_zoom * Math.Pow(1.18, e.Delta.Y), 0.1, 64); InvalidateVisual(); e.Handled = true;
    }

    private void PublishHover(Point position)
    {
        if (_texture is null || !TryTexturePoint(position, out var point)) { HoverChanged?.Invoke(this, null); return; }
        var x = Math.Clamp((int)Math.Floor(point.X), 0, _texture.Width - 1); var y = Math.Clamp((int)Math.Floor(point.Y), 0, _texture.Height - 1); var offset = checked((y * _texture.Width + x) * 4);
        HoverChanged?.Invoke(this, new(x, y, _texture.Pixels[offset], _texture.Pixels[offset + 1], _texture.Pixels[offset + 2], _texture.Pixels[offset + 3]));
    }

    private bool TryTexturePoint(Point screen, out TexturePoint point)
    {
        point = default; if (_texture is null) return false; var destination = Destination(); if (!destination.Contains(screen)) return false;
        point = new((screen.X - destination.X) / destination.Width * _texture.Width, (screen.Y - destination.Y) / destination.Height * _texture.Height); return true;
    }
    private Point ScreenPoint(TexturePoint point) { var destination = Destination(); return new(destination.X + point.X / _texture!.Width * destination.Width, destination.Y + point.Y / _texture.Height * destination.Height); }
    private double Scale() { if (_texture is null) return 1; return Math.Min(Math.Max(1, Bounds.Width - 24) / _texture.Width, Math.Max(1, Bounds.Height - 24) / _texture.Height) * _zoom; }
    private Rect Destination()
    {
        if (_texture is null) return default; var scale = Scale(); var width = _texture.Width * scale; var height = _texture.Height * scale; return new((Bounds.Width - width) / 2 + _pan.X, (Bounds.Height - height) / 2 + _pan.Y, width, height);
    }

    private void DrawCheckerboard(DrawingContext context)
    {
        context.FillRectangle(Brush.Parse("#111722"), Bounds); const double size = 16;
        for (double y = 0; y < Bounds.Height; y += size) for (double x = 0; x < Bounds.Width; x += size)
            if ((((int)(x / size)) + (int)(y / size)) % 2 == 0) context.FillRectangle(Brush.Parse("#1C2432"), new Rect(x, y, Math.Min(size, Bounds.Width - x), Math.Min(size, Bounds.Height - y)));
    }

    private void RebuildBitmap()
    {
        var previous = _bitmap; _bitmap = null;
        if (_texture is not null)
        {
            var display = TexturePixelEditService.RenderChannels(_texture, _view); var bitmap = new WriteableBitmap(new PixelSize(display.Width, display.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
            using var frame = bitmap.Lock(); var rowBytes = checked(display.Width * 4);
            for (var row = 0; row < display.Height; row++) Marshal.Copy(display.Pixels, row * rowBytes, IntPtr.Add(frame.Address, row * frame.RowBytes), rowBytes);
            _bitmap = bitmap;
        }
        previous?.Dispose();
    }

    private static double Distance(TexturePoint left, TexturePoint right) { var x = left.X - right.X; var y = left.Y - right.Y; return Math.Sqrt(x * x + y * y); }
    private static void DrawText(DrawingContext context, string text, Point point) => context.DrawText(new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Inter"), 12, Brush.Parse("#98A2B4")), point);
    public void Dispose() { _bitmap?.Dispose(); }
}
