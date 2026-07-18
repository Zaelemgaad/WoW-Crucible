namespace WoWCrucible.Core;

public enum CharacterTextureRegion { FaceUpper, FaceLower, Torso, Pelvis }
public sealed record CharacterTextureLayer(RgbaTexture Texture, CharacterTextureRegion Region);

public static class CharacterTextureComposer
{
    public static RgbaTexture Compose(RgbaTexture baseTexture, IEnumerable<CharacterTextureLayer> layers)
    {
        if (baseTexture.Width <= 0 || baseTexture.Height <= 0 || baseTexture.Pixels.Length != checked(baseTexture.Width * baseTexture.Height * 4)) throw new InvalidDataException("The character base texture shape is invalid.");
        if (baseTexture.Width != baseTexture.Height || baseTexture.Width % 256 != 0) throw new InvalidDataException($"Wrath character composition requires a square 256-based atlas; received {baseTexture.Width}x{baseTexture.Height}.");
        var output = baseTexture.Pixels.ToArray(); var scale = baseTexture.Width / 256;
        foreach (var layer in layers)
        {
            var region = layer.Region switch
            {
                CharacterTextureRegion.FaceUpper => (X: 0, Y: 160, Width: 128, Height: 32),
                CharacterTextureRegion.FaceLower => (X: 0, Y: 192, Width: 128, Height: 64),
                CharacterTextureRegion.Torso => (X: 128, Y: 0, Width: 128, Height: 64),
                CharacterTextureRegion.Pelvis => (X: 128, Y: 96, Width: 128, Height: 64),
                _ => throw new ArgumentOutOfRangeException(nameof(layers))
            };
            Burn(output, baseTexture.Width, baseTexture.Height, layer.Texture, region.X * scale, region.Y * scale, region.Width * scale, region.Height * scale);
        }
        return new(baseTexture.Width, baseTexture.Height, output);
    }

    private static void Burn(byte[] destination, int destinationWidth, int destinationHeight, RgbaTexture source, int x, int y, int width, int height)
    {
        if (source.Width <= 0 || source.Height <= 0 || source.Pixels.Length != checked(source.Width * source.Height * 4)) throw new InvalidDataException("A character component texture shape is invalid.");
        if (x < 0 || y < 0 || x + width > destinationWidth || y + height > destinationHeight) throw new InvalidDataException("A character component escaped the atlas bounds.");
        for (var dy = 0; dy < height; dy++)
        {
            var sy = Math.Min(source.Height - 1, (int)((long)dy * source.Height / height));
            for (var dx = 0; dx < width; dx++)
            {
                var sx = Math.Min(source.Width - 1, (int)((long)dx * source.Width / width));
                var sourceOffset = (sy * source.Width + sx) * 4; var destinationOffset = ((y + dy) * destinationWidth + x + dx) * 4; var alpha = source.Pixels[sourceOffset + 3];
                if (alpha == 0) continue;
                if (alpha == 255) { Buffer.BlockCopy(source.Pixels, sourceOffset, destination, destinationOffset, 4); continue; }
                var inverse = 255 - alpha;
                destination[destinationOffset] = (byte)((source.Pixels[sourceOffset] * alpha + destination[destinationOffset] * inverse + 127) / 255);
                destination[destinationOffset + 1] = (byte)((source.Pixels[sourceOffset + 1] * alpha + destination[destinationOffset + 1] * inverse + 127) / 255);
                destination[destinationOffset + 2] = (byte)((source.Pixels[sourceOffset + 2] * alpha + destination[destinationOffset + 2] * inverse + 127) / 255);
                destination[destinationOffset + 3] = (byte)(alpha + (destination[destinationOffset + 3] * inverse + 127) / 255);
            }
        }
    }
}
