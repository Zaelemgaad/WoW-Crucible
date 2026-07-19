namespace WoWCrucible.Core;

/// <summary>Reproduces the bounded fixed-function modulation used by build-264 multi-texture particles.</summary>
public static class M2ParticleTextureCompositionService
{
    private const int MaximumDimension = 8_192;
    private const long MaximumPixels = 16_777_216;

    public static RgbaTexture Compose(IReadOnlyList<RgbaTexture> textures)
    {
        ArgumentNullException.ThrowIfNull(textures);
        if (textures.Count is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(textures), "A WotLK particle composition requires one to three textures.");
        foreach (var texture in textures) Validate(texture);
        var width = textures[0].Width; var height = textures[0].Height;
        var pixels = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var output = (y * width + x) * 4;
            var first = Sample(textures[0], x, y, width, height);
            for (var channel = 0; channel < 4; channel++) pixels[output + channel] = (byte)Math.Min(255, first[channel] * 4);
            for (var layer = 1; layer < textures.Count; layer++)
            {
                var source = Sample(textures[layer], x, y, width, height);
                for (var channel = 0; channel < 4; channel++) pixels[output + channel] = (byte)((pixels[output + channel] * source[channel] + 127) / 255);
            }
        }
        return new(width, height, pixels);
    }

    private static ReadOnlySpan<byte> Sample(RgbaTexture texture, int x, int y, int outputWidth, int outputHeight)
    {
        var sourceX = Math.Min(texture.Width - 1, (int)((long)x * texture.Width / outputWidth));
        var sourceY = Math.Min(texture.Height - 1, (int)((long)y * texture.Height / outputHeight));
        return texture.Pixels.AsSpan((sourceY * texture.Width + sourceX) * 4, 4);
    }

    private static void Validate(RgbaTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (texture.Width <= 0 || texture.Height <= 0 || texture.Width > MaximumDimension || texture.Height > MaximumDimension || (long)texture.Width * texture.Height > MaximumPixels)
            throw new InvalidDataException($"Particle texture dimensions {texture.Width:N0} x {texture.Height:N0} exceed the safe composition bounds.");
        if (texture.Pixels.Length != (long)texture.Width * texture.Height * 4)
            throw new InvalidDataException($"Particle texture has {texture.Pixels.Length:N0} RGBA bytes; {texture.Width:N0} x {texture.Height:N0} requires {(long)texture.Width * texture.Height * 4:N0}.");
    }
}
