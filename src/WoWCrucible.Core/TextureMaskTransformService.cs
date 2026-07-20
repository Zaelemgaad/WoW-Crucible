namespace WoWCrucible.Core;

public enum TextureMaskChannel { Alpha, Luminance, Red, Green, Blue }

public sealed record TextureChannelTransform(
    double RedScale = 1, double GreenScale = 1, double BlueScale = 1, double AlphaScale = 1,
    double RedOffset = 0, double GreenOffset = 0, double BlueOffset = 0, double AlphaOffset = 0);

public sealed record TextureMaskTransformSettings(
    TextureMaskChannel MaskChannel = TextureMaskChannel.Alpha,
    bool InvertMask = false,
    double Strength = 1,
    TextureChannelTransform? Transform = null);

public sealed record TextureMaskTransformResult(
    RgbaTexture Texture,
    int PixelsInfluenced,
    int PixelsChanged,
    byte MinimumMask,
    byte MaximumMask);

/// <summary>
/// Applies one affine transform per RGBA channel, blended through a selected
/// mask channel. Base and mask inputs remain immutable. A differently sized
/// mask is sampled with deterministic nearest-neighbor normalized mapping.
/// </summary>
public static class TextureMaskTransformService
{
    private const long MaximumPixels = 67_108_864;

    public static TextureMaskTransformResult Apply(RgbaTexture source, RgbaTexture mask,
        TextureMaskTransformSettings? settings = null, CancellationToken cancellationToken = default)
    {
        Validate(source, nameof(source)); Validate(mask, nameof(mask)); settings ??= new();
        if (!Enum.IsDefined(settings.MaskChannel)) throw new ArgumentOutOfRangeException(nameof(settings), "Mask channel is not recognized.");
        if (!double.IsFinite(settings.Strength) || settings.Strength < 0 || settings.Strength > 1)
            throw new ArgumentOutOfRangeException(nameof(settings), "Mask strength must be from 0 through 1.");
        var transform = settings.Transform ?? new(); Validate(transform);
        var output = source.Pixels.ToArray(); var influenced = 0; var changed = 0; byte minimum = byte.MaxValue; byte maximum = byte.MinValue;
        for (var y = 0; y < source.Height; y++)
        {
            if ((y & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
            var maskY = (int)Math.Min(mask.Height - 1L, (long)y * mask.Height / source.Height);
            for (var x = 0; x < source.Width; x++)
            {
                var maskX = (int)Math.Min(mask.Width - 1L, (long)x * mask.Width / source.Width);
                var maskOffset = checked((maskY * mask.Width + maskX) * 4); var maskValue = SampleMask(mask.Pixels, maskOffset, settings.MaskChannel);
                if (settings.InvertMask) maskValue = (byte)(255 - maskValue); minimum = Math.Min(minimum, maskValue); maximum = Math.Max(maximum, maskValue);
                var amount = maskValue / 255d * settings.Strength; if (amount <= 0) continue; influenced++;
                var offset = checked((y * source.Width + x) * 4); var beforeR = output[offset]; var beforeG = output[offset + 1]; var beforeB = output[offset + 2]; var beforeA = output[offset + 3];
                output[offset] = Transform(beforeR, transform.RedScale, transform.RedOffset, amount);
                output[offset + 1] = Transform(beforeG, transform.GreenScale, transform.GreenOffset, amount);
                output[offset + 2] = Transform(beforeB, transform.BlueScale, transform.BlueOffset, amount);
                output[offset + 3] = Transform(beforeA, transform.AlphaScale, transform.AlphaOffset, amount);
                if (beforeR != output[offset] || beforeG != output[offset + 1] || beforeB != output[offset + 2] || beforeA != output[offset + 3]) changed++;
            }
        }
        return new(new(source.Width, source.Height, output), influenced, changed, minimum, maximum);
    }

    public static string ChannelName(TextureMaskChannel channel) => channel switch
    {
        TextureMaskChannel.Alpha => "Alpha",
        TextureMaskChannel.Luminance => "RGB luminance",
        TextureMaskChannel.Red => "Red",
        TextureMaskChannel.Green => "Green",
        TextureMaskChannel.Blue => "Blue",
        _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };

    public static RgbaTexture CreateMaskPreview(RgbaTexture mask, TextureMaskChannel channel, bool invert = false,
        CancellationToken cancellationToken = default)
    {
        Validate(mask, nameof(mask)); if (!Enum.IsDefined(channel)) throw new ArgumentOutOfRangeException(nameof(channel)); var output = new byte[mask.Pixels.Length];
        for (var pixel = 0; pixel < checked(mask.Width * mask.Height); pixel++)
        {
            if ((pixel & 65_535) == 0) cancellationToken.ThrowIfCancellationRequested(); var offset = pixel * 4; var value = SampleMask(mask.Pixels, offset, channel); if (invert) value = (byte)(255 - value);
            output[offset] = output[offset + 1] = output[offset + 2] = value; output[offset + 3] = 255;
        }
        return new(mask.Width, mask.Height, output);
    }

    private static byte SampleMask(byte[] pixels, int offset, TextureMaskChannel channel) => channel switch
    {
        TextureMaskChannel.Alpha => pixels[offset + 3],
        TextureMaskChannel.Luminance => (byte)((54 * pixels[offset] + 183 * pixels[offset + 1] + 19 * pixels[offset + 2] + 128) >> 8),
        TextureMaskChannel.Red => pixels[offset],
        TextureMaskChannel.Green => pixels[offset + 1],
        TextureMaskChannel.Blue => pixels[offset + 2],
        _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };

    private static byte Transform(byte value, double scale, double offset, double amount)
    {
        var transformed = Math.Clamp(value * scale + offset, 0, 255); return (byte)Math.Clamp((int)Math.Round(value + (transformed - value) * amount), 0, 255);
    }

    private static void Validate(TextureChannelTransform transform)
    {
        var values = new[] { transform.RedScale, transform.GreenScale, transform.BlueScale, transform.AlphaScale, transform.RedOffset, transform.GreenOffset, transform.BlueOffset, transform.AlphaOffset };
        if (values.Any(value => !double.IsFinite(value))) throw new ArgumentException("Every RGBA scale and offset must be finite.", nameof(transform));
    }

    private static void Validate(RgbaTexture texture, string parameter)
    {
        ArgumentNullException.ThrowIfNull(texture); var pixels = checked((long)texture.Width * texture.Height);
        if (texture.Width <= 0 || texture.Height <= 0 || pixels > MaximumPixels || texture.Pixels is null || texture.Pixels.LongLength != checked(pixels * 4))
            throw new ArgumentException($"Texture must have positive matching RGBA dimensions of at most {MaximumPixels:N0} pixels.", parameter);
    }
}
