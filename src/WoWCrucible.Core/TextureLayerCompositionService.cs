namespace WoWCrucible.Core;

public enum TextureBlendMode { Normal, Multiply, Screen, Overlay, Add, Subtract, Darken, Lighten }

public sealed record TextureCompositionLayer(
    string Name,
    RgbaTexture Texture,
    bool Visible = true,
    double Opacity = 1,
    int OffsetX = 0,
    int OffsetY = 0,
    TextureBlendMode BlendMode = TextureBlendMode.Normal);

public sealed record TextureCompositionLayerResult(
    string Name,
    bool Visible,
    int PixelsInCanvas,
    int ContributingPixels,
    int ChangedPixels,
    int ClippedPixels);

public sealed record TextureCompositionResult(RgbaTexture Texture, IReadOnlyList<TextureCompositionLayerResult> Layers);

/// <summary>
/// Format-independent, ordered RGBA layer composition. Layers are supplied
/// bottom-to-top. Blending follows source-over alpha composition with the
/// selected blend function applied only where source and backdrop overlap.
/// </summary>
public static class TextureLayerCompositionService
{
    private const long MaximumPixels = 67_108_864;

    public static TextureCompositionResult Compose(int width, int height, IReadOnlyList<TextureCompositionLayer> layers,
        byte backgroundR = 0, byte backgroundG = 0, byte backgroundB = 0, byte backgroundA = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layers);
        var pixelCount = checked((long)width * height);
        if (width <= 0 || height <= 0 || pixelCount > MaximumPixels) throw new ArgumentOutOfRangeException(nameof(width), $"Texture canvas must have positive dimensions and at most {MaximumPixels:N0} pixels.");
        var output = new byte[checked((int)pixelCount * 4)];
        for (var offset = 0; offset < output.Length; offset += 4) { output[offset] = backgroundR; output[offset + 1] = backgroundG; output[offset + 2] = backgroundB; output[offset + 3] = backgroundA; }
        var results = new List<TextureCompositionLayerResult>(layers.Count);
        foreach (var layer in layers)
        {
            cancellationToken.ThrowIfCancellationRequested(); Validate(layer);
            if (!layer.Visible) { results.Add(new(layer.Name, false, 0, 0, 0, 0)); continue; }
            var inCanvas = 0; var contributing = 0; var changed = 0; var clipped = 0;
            for (var sourceY = 0; sourceY < layer.Texture.Height; sourceY++)
            {
                if ((sourceY & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
                var targetY = (long)sourceY + layer.OffsetY;
                for (var sourceX = 0; sourceX < layer.Texture.Width; sourceX++)
                {
                    var targetX = (long)sourceX + layer.OffsetX;
                    if (targetX < 0 || targetY < 0 || targetX >= width || targetY >= height) { clipped++; continue; }
                    inCanvas++; var sourceOffset = checked((sourceY * layer.Texture.Width + sourceX) * 4); var sourceAlpha = layer.Texture.Pixels[sourceOffset + 3] / 255d * layer.Opacity;
                    if (sourceAlpha <= 0) continue; contributing++;
                    var targetOffset = checked(((int)targetY * width + (int)targetX) * 4); var beforeR = output[targetOffset]; var beforeG = output[targetOffset + 1]; var beforeB = output[targetOffset + 2]; var beforeA = output[targetOffset + 3];
                    var backdropAlpha = beforeA / 255d; var resultAlpha = sourceAlpha + backdropAlpha * (1 - sourceAlpha);
                    output[targetOffset] = Composite(beforeR, layer.Texture.Pixels[sourceOffset], backdropAlpha, sourceAlpha, resultAlpha, layer.BlendMode);
                    output[targetOffset + 1] = Composite(beforeG, layer.Texture.Pixels[sourceOffset + 1], backdropAlpha, sourceAlpha, resultAlpha, layer.BlendMode);
                    output[targetOffset + 2] = Composite(beforeB, layer.Texture.Pixels[sourceOffset + 2], backdropAlpha, sourceAlpha, resultAlpha, layer.BlendMode);
                    output[targetOffset + 3] = ToByte(resultAlpha);
                    if (beforeR != output[targetOffset] || beforeG != output[targetOffset + 1] || beforeB != output[targetOffset + 2] || beforeA != output[targetOffset + 3]) changed++;
                }
            }
            results.Add(new(layer.Name, true, inCanvas, contributing, changed, clipped));
        }
        return new(new(width, height, output), results);
    }

    public static string BlendModeName(TextureBlendMode mode) => mode switch
    {
        TextureBlendMode.Normal => "Normal",
        TextureBlendMode.Multiply => "Multiply",
        TextureBlendMode.Screen => "Screen",
        TextureBlendMode.Overlay => "Overlay",
        TextureBlendMode.Add => "Add",
        TextureBlendMode.Subtract => "Subtract",
        TextureBlendMode.Darken => "Darken",
        TextureBlendMode.Lighten => "Lighten",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static byte Composite(byte backdropByte, byte sourceByte, double backdropAlpha, double sourceAlpha, double resultAlpha, TextureBlendMode mode)
    {
        if (resultAlpha <= 0) return 0;
        var backdrop = backdropByte / 255d; var source = sourceByte / 255d; var blend = Blend(backdrop, source, mode);
        var premultiplied = (1 - sourceAlpha) * backdropAlpha * backdrop + (1 - backdropAlpha) * sourceAlpha * source + sourceAlpha * backdropAlpha * blend;
        return ToByte(premultiplied / resultAlpha);
    }

    private static double Blend(double backdrop, double source, TextureBlendMode mode) => Math.Clamp(mode switch
    {
        TextureBlendMode.Normal => source,
        TextureBlendMode.Multiply => backdrop * source,
        TextureBlendMode.Screen => backdrop + source - backdrop * source,
        TextureBlendMode.Overlay => backdrop <= 0.5 ? 2 * backdrop * source : 1 - 2 * (1 - backdrop) * (1 - source),
        TextureBlendMode.Add => backdrop + source,
        TextureBlendMode.Subtract => backdrop - source,
        TextureBlendMode.Darken => Math.Min(backdrop, source),
        TextureBlendMode.Lighten => Math.Max(backdrop, source),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    }, 0, 1);

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value * 255), 0, 255);

    private static void Validate(TextureCompositionLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer); ArgumentNullException.ThrowIfNull(layer.Texture);
        if (string.IsNullOrWhiteSpace(layer.Name)) throw new ArgumentException("Texture composition layers require a readable name.", nameof(layer));
        if (!double.IsFinite(layer.Opacity) || layer.Opacity < 0 || layer.Opacity > 1) throw new ArgumentOutOfRangeException(nameof(layer), "Layer opacity must be from 0 through 1.");
        if (!Enum.IsDefined(layer.BlendMode)) throw new ArgumentOutOfRangeException(nameof(layer), "Layer blend mode is not recognized.");
        var pixelCount = checked((long)layer.Texture.Width * layer.Texture.Height);
        if (layer.Texture.Width <= 0 || layer.Texture.Height <= 0 || pixelCount > MaximumPixels || layer.Texture.Pixels is null || layer.Texture.Pixels.LongLength != checked(pixelCount * 4))
            throw new ArgumentException($"Layer {layer.Name} must have positive matching RGBA dimensions of at most {MaximumPixels:N0} pixels.", nameof(layer));
    }
}
