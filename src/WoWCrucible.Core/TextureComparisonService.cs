using System.Text.Json.Serialization;

namespace WoWCrucible.Core;

public sealed record TextureChannelError(
    long ChangedSamples,
    double MeanAbsoluteError,
    double RootMeanSquareError,
    int MaximumAbsoluteError,
    double? PeakSignalToNoiseDb);

public sealed record TextureComparisonReport(
    int Width,
    int Height,
    long PixelCount,
    long ExactPixels,
    long ChangedPixels,
    TextureChannelError Red,
    TextureChannelError Green,
    TextureChannelError Blue,
    TextureChannelError Alpha,
    TextureChannelError RgbCombined,
    TextureChannelError RgbaCombined,
    long TransparentBoundaryChanges,
    long OpaqueBoundaryChanges,
    long AlphaThresholdCrossings,
    long BinaryAlphaBecameTranslucent);

public sealed record TextureEncodingProof(
    BlpOutputFormat RequestedFormat,
    BlpOutputQuality Quality,
    bool GeneratedMipmaps,
    string ActualEncoding,
    int MipLevels,
    long EncodedBytes,
    TextureComparisonReport Comparison,
    [property: JsonIgnore] RgbaTexture DecodedPreview,
    [property: JsonIgnore] RgbaTexture DifferenceMap);

/// <summary>
/// Compares exact decoded RGBA bytes and proves the loss introduced by the
/// actual Crucible BLP encoder/decoder round trip. Temporary BLP data is never
/// published and is removed whether analysis succeeds or fails.
/// </summary>
public static class TextureComparisonService
{
    public static TextureComparisonReport Compare(RgbaTexture expected, RgbaTexture actual, CancellationToken cancellationToken = default)
    {
        ValidatePair(expected, actual);
        var channels = new[] { new Accumulator(), new Accumulator(), new Accumulator(), new Accumulator() };
        var rgb = new Accumulator(); var rgba = new Accumulator();
        long changedPixels = 0, transparentBoundaryChanges = 0, opaqueBoundaryChanges = 0, thresholdCrossings = 0, binaryToTranslucent = 0;
        var pixels = checked((long)expected.Width * expected.Height);
        for (var offset = 0; offset < expected.Pixels.Length; offset += 4)
        {
            if ((offset & 0x3FFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            var pixelChanged = false;
            for (var channel = 0; channel < 4; channel++)
            {
                var difference = Math.Abs(expected.Pixels[offset + channel] - actual.Pixels[offset + channel]);
                channels[channel].Add(difference); rgba.Add(difference); if (channel < 3) rgb.Add(difference); if (difference != 0) pixelChanged = true;
            }
            if (pixelChanged) changedPixels++;
            var expectedAlpha = expected.Pixels[offset + 3]; var actualAlpha = actual.Pixels[offset + 3];
            if ((expectedAlpha == 0) != (actualAlpha == 0)) transparentBoundaryChanges++;
            if ((expectedAlpha == 255) != (actualAlpha == 255)) opaqueBoundaryChanges++;
            if ((expectedAlpha >= 128) != (actualAlpha >= 128)) thresholdCrossings++;
            if (expectedAlpha is 0 or 255 && actualAlpha is > 0 and < 255) binaryToTranslucent++;
        }
        return new(expected.Width, expected.Height, pixels, pixels - changedPixels, changedPixels,
            channels[0].Finish(pixels), channels[1].Finish(pixels), channels[2].Finish(pixels), channels[3].Finish(pixels),
            rgb.Finish(checked(pixels * 3)), rgba.Finish(checked(pixels * 4)), transparentBoundaryChanges, opaqueBoundaryChanges, thresholdCrossings, binaryToTranslucent);
    }

    public static RgbaTexture CreateDifferenceMap(RgbaTexture expected, RgbaTexture actual, double amplification = 4, CancellationToken cancellationToken = default)
    {
        ValidatePair(expected, actual);
        if (!double.IsFinite(amplification) || amplification <= 0 || amplification > 255) throw new ArgumentOutOfRangeException(nameof(amplification), "Difference amplification must be finite and from 0 exclusive through 255.");
        var pixels = new byte[expected.Pixels.Length];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            if ((offset & 0x3FFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            var red = Scale(Math.Abs(expected.Pixels[offset] - actual.Pixels[offset]));
            var green = Scale(Math.Abs(expected.Pixels[offset + 1] - actual.Pixels[offset + 1]));
            var blue = Scale(Math.Abs(expected.Pixels[offset + 2] - actual.Pixels[offset + 2]));
            var alpha = Scale(Math.Abs(expected.Pixels[offset + 3] - actual.Pixels[offset + 3]));
            pixels[offset] = Math.Max(red, alpha); pixels[offset + 1] = green; pixels[offset + 2] = Math.Max(blue, alpha); pixels[offset + 3] = 255;
        }
        return new(expected.Width, expected.Height, pixels);
        byte Scale(int difference) => (byte)Math.Clamp((int)Math.Round(difference * amplification), 0, 255);
    }

    public static TextureEncodingProof AnalyzeEncoding(RgbaTexture source, BlpEncodeOptions options, double differenceAmplification = 4,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(options); cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(Path.GetTempPath(), "WoWCrucible", "TextureProof");
        var temporary = Path.Combine(directory, $"{Environment.ProcessId}-{Guid.NewGuid():N}.blp");
        try
        {
            Directory.CreateDirectory(directory);
            BlpTextureService.EncodeBlp2(source, temporary, options, overwrite: false);
            cancellationToken.ThrowIfCancellationRequested();
            var info = BlpTextureService.Inspect(temporary); var bytes = new FileInfo(temporary).Length; var decoded = BlpTextureService.Decode(temporary);
            var comparison = Compare(source, decoded, cancellationToken); var difference = CreateDifferenceMap(source, decoded, differenceAmplification, cancellationToken);
            return new(options.Format, options.Quality, options.GenerateMipmaps, info.Encoding, info.MipLevels.Count, bytes, comparison, decoded, difference);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            try { if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); } catch { }
        }
    }

    private static void ValidatePair(RgbaTexture expected, RgbaTexture actual)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(actual);
        if (expected.Width <= 0 || expected.Height <= 0 || expected.Pixels is null || expected.Pixels.Length != checked(expected.Width * expected.Height * 4)) throw new ArgumentException("Expected texture pixels do not match its positive RGBA dimensions.", nameof(expected));
        if (actual.Width <= 0 || actual.Height <= 0 || actual.Pixels is null || actual.Pixels.Length != checked(actual.Width * actual.Height * 4)) throw new ArgumentException("Actual texture pixels do not match its positive RGBA dimensions.", nameof(actual));
        if (expected.Width != actual.Width || expected.Height != actual.Height) throw new ArgumentException($"Texture dimensions differ: expected {expected.Width}x{expected.Height}, actual {actual.Width}x{actual.Height}.", nameof(actual));
    }

    private sealed class Accumulator
    {
        private long _absolute; private long _squared; private long _changed; private int _maximum;
        public void Add(int difference) { _absolute = checked(_absolute + difference); _squared = checked(_squared + (long)difference * difference); if (difference != 0) _changed++; _maximum = Math.Max(_maximum, difference); }
        public TextureChannelError Finish(long samples)
        {
            if (samples <= 0) throw new ArgumentOutOfRangeException(nameof(samples));
            var meanAbsolute = _absolute / (double)samples; var meanSquared = _squared / (double)samples; var rmse = Math.Sqrt(meanSquared);
            return new(_changed, meanAbsolute, rmse, _maximum, rmse == 0 ? null : 20 * Math.Log10(255 / rmse));
        }
    }
}
