namespace WoWCrucible.Core;

public enum TexturePaintMode { ColorAndAlpha, RgbOnly, AlphaOnly, EraseAlpha }
public enum TextureBrushFalloff { Hard, Linear, Smooth }

public readonly record struct TexturePoint(double X, double Y);
public sealed record TextureBrushSettings(double Radius, double Opacity, byte R, byte G, byte B, byte A,
    TexturePaintMode Mode = TexturePaintMode.ColorAndAlpha, TextureBrushFalloff Falloff = TextureBrushFalloff.Smooth);
public sealed record TextureEditResult(int ChangedPixels, int MinimumX, int MinimumY, int MaximumX, int MaximumY)
{
    public bool Changed => ChangedPixels > 0;
}
public sealed record TextureChannelStatistics(byte MinimumR, byte MaximumR, double AverageR,
    byte MinimumG, byte MaximumG, double AverageG, byte MinimumB, byte MaximumB, double AverageB,
    byte MinimumA, byte MaximumA, double AverageA, int TransparentPixels, int TranslucentPixels, int OpaquePixels);
public sealed record TextureChannelView(bool Red = true, bool Green = true, bool Blue = true, bool Alpha = true, bool AlphaAsGrayscale = false);

/// <summary>
/// Clean, format-independent RGBA editing primitives. The caller owns the mutable
/// pixel buffer and its undo history; this service never writes a source image.
/// </summary>
public static class TexturePixelEditService
{
    public static TextureEditResult ApplyStroke(RgbaTexture texture, IReadOnlyList<TexturePoint> points, TextureBrushSettings settings)
    {
        Validate(texture); ArgumentNullException.ThrowIfNull(points); Validate(settings);
        if (points.Count == 0) return Empty();
        if (points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y))) throw new ArgumentException("Texture brush points must be finite.", nameof(points));

        var influence = new Dictionary<int, double>();
        var previous = points[0]; Stamp(previous);
        for (var index = 1; index < points.Count; index++)
        {
            var next = points[index]; var dx = next.X - previous.X; var dy = next.Y - previous.Y; var distance = Math.Sqrt(dx * dx + dy * dy);
            var steps = Math.Max(1, checked((int)Math.Ceiling(distance / Math.Max(0.5, settings.Radius * 0.25))));
            for (var step = 1; step <= steps; step++) { var t = step / (double)steps; Stamp(new(previous.X + dx * t, previous.Y + dy * t)); }
            previous = next;
        }

        var changed = 0; var minX = texture.Width; var minY = texture.Height; var maxX = -1; var maxY = -1;
        foreach (var pair in influence)
        {
            var pixel = pair.Key; var offset = checked(pixel * 4); var factor = Math.Clamp(settings.Opacity * pair.Value, 0, 1); var any = false;
            if (settings.Mode is TexturePaintMode.ColorAndAlpha or TexturePaintMode.RgbOnly)
            {
                any |= Blend(offset, settings.R, factor); any |= Blend(offset + 1, settings.G, factor); any |= Blend(offset + 2, settings.B, factor);
            }
            if (settings.Mode is TexturePaintMode.ColorAndAlpha or TexturePaintMode.AlphaOnly or TexturePaintMode.EraseAlpha)
                any |= Blend(offset + 3, settings.Mode == TexturePaintMode.EraseAlpha ? (byte)0 : settings.A, factor);
            if (!any) continue;
            changed++; var x = pixel % texture.Width; var y = pixel / texture.Width; minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
        }
        return changed == 0 ? Empty() : new(changed, minX, minY, maxX, maxY);

        void Stamp(TexturePoint point)
        {
            if (point.X + settings.Radius < 0 || point.Y + settings.Radius < 0 || point.X - settings.Radius > texture.Width || point.Y - settings.Radius > texture.Height) return;
            var startX = Math.Max(0, checked((int)Math.Floor(point.X - settings.Radius))); var endX = Math.Min(texture.Width - 1, checked((int)Math.Ceiling(point.X + settings.Radius)));
            var startY = Math.Max(0, checked((int)Math.Floor(point.Y - settings.Radius))); var endY = Math.Min(texture.Height - 1, checked((int)Math.Ceiling(point.Y + settings.Radius)));
            for (var y = startY; y <= endY; y++) for (var x = startX; x <= endX; x++)
            {
                var dx = x + 0.5 - point.X; var dy = y + 0.5 - point.Y; var distance = Math.Sqrt(dx * dx + dy * dy); if (distance > settings.Radius) continue;
                var value = Falloff(distance / settings.Radius, settings.Falloff); var pixel = checked(y * texture.Width + x);
                if (!influence.TryGetValue(pixel, out var old) || value > old) influence[pixel] = value;
            }
        }
        bool Blend(int offset, byte target, double factor)
        {
            var before = texture.Pixels[offset]; var after = (byte)Math.Clamp((int)Math.Round(before + (target - before) * factor), 0, 255); if (after == before) return false; texture.Pixels[offset] = after; return true;
        }
    }

    public static TextureEditResult Fill(RgbaTexture texture, TextureBrushSettings settings)
    {
        Validate(texture); Validate(settings); var changed = 0;
        for (var offset = 0; offset < texture.Pixels.Length; offset += 4)
        {
            var any = false;
            if (settings.Mode is TexturePaintMode.ColorAndAlpha or TexturePaintMode.RgbOnly)
            {
                any |= Blend(offset, settings.R); any |= Blend(offset + 1, settings.G); any |= Blend(offset + 2, settings.B);
            }
            if (settings.Mode is TexturePaintMode.ColorAndAlpha or TexturePaintMode.AlphaOnly or TexturePaintMode.EraseAlpha)
                any |= Blend(offset + 3, settings.Mode == TexturePaintMode.EraseAlpha ? (byte)0 : settings.A);
            if (any) changed++;
        }
        return changed == 0 ? Empty() : new(changed, 0, 0, texture.Width - 1, texture.Height - 1);
        bool Blend(int offset, byte target) { var before = texture.Pixels[offset]; var after = (byte)Math.Clamp((int)Math.Round(before + (target - before) * settings.Opacity), 0, 255); if (before == after) return false; texture.Pixels[offset] = after; return true; }
    }

    public static TextureEditResult InvertAlpha(RgbaTexture texture)
    {
        Validate(texture); var changed = 0;
        for (var offset = 3; offset < texture.Pixels.Length; offset += 4) { var value = (byte)(255 - texture.Pixels[offset]); if (value != texture.Pixels[offset]) { texture.Pixels[offset] = value; changed++; } }
        return changed == 0 ? Empty() : new(changed, 0, 0, texture.Width - 1, texture.Height - 1);
    }

    public static RgbaTexture RenderChannels(RgbaTexture texture, TextureChannelView view)
    {
        Validate(texture); ArgumentNullException.ThrowIfNull(view); var pixels = new byte[texture.Pixels.Length];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            if (view.AlphaAsGrayscale)
            {
                var alpha = texture.Pixels[offset + 3]; pixels[offset] = alpha; pixels[offset + 1] = alpha; pixels[offset + 2] = alpha; pixels[offset + 3] = 255; continue;
            }
            pixels[offset] = view.Red ? texture.Pixels[offset] : (byte)0; pixels[offset + 1] = view.Green ? texture.Pixels[offset + 1] : (byte)0; pixels[offset + 2] = view.Blue ? texture.Pixels[offset + 2] : (byte)0; pixels[offset + 3] = view.Alpha ? texture.Pixels[offset + 3] : (byte)255;
        }
        return new(texture.Width, texture.Height, pixels);
    }

    public static TextureChannelStatistics Analyze(RgbaTexture texture)
    {
        Validate(texture); var minR = byte.MaxValue; var minG = byte.MaxValue; var minB = byte.MaxValue; var minA = byte.MaxValue; byte maxR = 0, maxG = 0, maxB = 0, maxA = 0; long sumR = 0, sumG = 0, sumB = 0, sumA = 0; var transparent = 0; var translucent = 0; var opaque = 0;
        for (var offset = 0; offset < texture.Pixels.Length; offset += 4)
        {
            var r = texture.Pixels[offset]; var g = texture.Pixels[offset + 1]; var b = texture.Pixels[offset + 2]; var a = texture.Pixels[offset + 3];
            minR = Math.Min(minR, r); minG = Math.Min(minG, g); minB = Math.Min(minB, b); minA = Math.Min(minA, a); maxR = Math.Max(maxR, r); maxG = Math.Max(maxG, g); maxB = Math.Max(maxB, b); maxA = Math.Max(maxA, a); sumR += r; sumG += g; sumB += b; sumA += a;
            if (a == 0) transparent++; else if (a == 255) opaque++; else translucent++;
        }
        var count = checked(texture.Width * texture.Height); return new(minR, maxR, sumR / (double)count, minG, maxG, sumG / (double)count, minB, maxB, sumB / (double)count, minA, maxA, sumA / (double)count, transparent, translucent, opaque);
    }

    private static double Falloff(double normalized, TextureBrushFalloff falloff)
    {
        var t = Math.Clamp(1 - normalized, 0, 1); return falloff switch { TextureBrushFalloff.Hard => 1, TextureBrushFalloff.Linear => t, _ => t * t * (3 - 2 * t) };
    }
    private static void Validate(TextureBrushSettings settings)
    {
        if (!double.IsFinite(settings.Radius) || settings.Radius <= 0 || settings.Radius > 65_536) throw new ArgumentOutOfRangeException(nameof(settings), "Texture brush radius must be finite and from 0 exclusive through 65,536 pixels.");
        if (!double.IsFinite(settings.Opacity) || settings.Opacity < 0 || settings.Opacity > 1) throw new ArgumentOutOfRangeException(nameof(settings), "Texture brush opacity must be from 0 through 1.");
        if (!Enum.IsDefined(settings.Mode) || !Enum.IsDefined(settings.Falloff)) throw new ArgumentOutOfRangeException(nameof(settings), "Texture brush mode and falloff must be recognized.");
    }
    private static void Validate(RgbaTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture); if (texture.Width <= 0 || texture.Height <= 0 || texture.Pixels is null || texture.Pixels.Length != checked(texture.Width * texture.Height * 4)) throw new ArgumentException("Texture pixels do not match its positive RGBA dimensions.", nameof(texture));
    }
    private static TextureEditResult Empty() => new(0, -1, -1, -1, -1);
}
