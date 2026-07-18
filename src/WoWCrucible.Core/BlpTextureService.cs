using System.Buffers.Binary;
using System.IO.Compression;
using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using StbImageSharp;

namespace WoWCrucible.Core;

public enum BlpTextureVersion { Blp1, Blp2 }
public enum BlpOutputFormat { Auto, Dxt1, Dxt1Alpha, Dxt3, Dxt5 }
public enum BlpOutputQuality { Fast, Balanced, Best }

public sealed record RgbaTexture(int Width, int Height, byte[] Pixels)
{
    public int ByteLength => checked(Width * Height * 4);
}

public sealed record BlpMipLevel(int Index, int Width, int Height, long Offset, int Size);

public sealed record BlpTextureInfo(
    string Path,
    BlpTextureVersion Version,
    int Width,
    int Height,
    string Encoding,
    int AlphaDepth,
    int AlphaEncoding,
    bool DeclaresMipmaps,
    IReadOnlyList<BlpMipLevel> MipLevels,
    IReadOnlyList<string> Warnings);

public sealed record BlpEncodeOptions(
    BlpOutputFormat Format = BlpOutputFormat.Auto,
    bool GenerateMipmaps = true,
    BlpOutputQuality Quality = BlpOutputQuality.Best);

public sealed record BlpValidationResult(string Path, bool Valid, BlpTextureInfo? Info, string? Error);
public sealed record BlpValidationSummary(int Total, int Failures, int Warnings);

/// <summary>
/// Native, fully managed BLP1/BLP2 inspection, decode and BLP2 encoding.
/// It supports the formats used by Wrath clients: paletted BLP, JPEG BLP1,
/// and BLP2 BC1/DXT1, BC2/DXT3 and BC3/DXT5 textures.
/// </summary>
public static class BlpTextureService
{
    private const int Blp2HeaderSize = 148;
    private const int Blp1HeaderSize = 156;
    private const int PaletteBytes = 256 * 4;
    private const int MaximumDimension = 65_536;
    private const long MaximumPixels = 67_108_864;

    public static BlpTextureInfo Inspect(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("The BLP texture does not exist.", path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
        return Inspect(stream, path);
    }

    public static BlpTextureInfo Inspect(Stream stream, string sourceName = "<stream>")
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek) throw new ArgumentException("BLP inspection requires a readable, seekable stream.", nameof(stream));
        var start = stream.Position;
        var magic = ReadExactly(stream, 4);
        stream.Position = start;
        return magic.AsSpan().SequenceEqual("BLP2"u8) ? InspectBlp2(stream, sourceName, start)
            : magic.AsSpan().SequenceEqual("BLP1"u8) ? InspectBlp1(stream, sourceName, start)
            : throw new InvalidDataException($"{sourceName} is not a BLP1 or BLP2 texture.");
    }

    public static RgbaTexture Decode(string path, int mipLevel = 0)
    {
        path = Path.GetFullPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.SequentialScan);
        return Decode(stream, path, mipLevel);
    }

    public static RgbaTexture Decode(Stream stream, string sourceName = "<stream>", int mipLevel = 0)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek) throw new ArgumentException("BLP decoding requires a readable, seekable stream.", nameof(stream));
        var start = stream.Position;
        var info = Inspect(stream, sourceName);
        if ((uint)mipLevel >= (uint)info.MipLevels.Count) throw new ArgumentOutOfRangeException(nameof(mipLevel), $"Mip {mipLevel} does not exist; this texture has {info.MipLevels.Count} mip level(s).");
        var mip = info.MipLevels[mipLevel];
        stream.Position = start;
        return info.Version == BlpTextureVersion.Blp2
            ? DecodeBlp2(stream, info, mip, start)
            : DecodeBlp1(stream, info, mip, start);
    }

    public static void DecodeToPng(string sourcePath, string outputPath, int mipLevel = 0, bool overwrite = false)
    {
        var texture = Decode(sourcePath, mipLevel);
        WritePng(outputPath, texture, overwrite);
    }

    public static void EncodeFromImage(string sourcePath, string outputPath, BlpEncodeOptions? options = null, bool overwrite = false)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("The source image does not exist.", sourcePath);
        using var stream = File.OpenRead(sourcePath);
        var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        EncodeBlp2(new RgbaTexture(image.Width, image.Height, image.Data), outputPath, options, overwrite);
    }

    public static void EncodeBlp2(RgbaTexture texture, string outputPath, BlpEncodeOptions? options = null, bool overwrite = false)
    {
        ValidateTexture(texture);
        options ??= new BlpEncodeOptions();
        var selected = SelectFormat(texture.Pixels, options.Format);
        var format = selected switch
        {
            BlpOutputFormat.Dxt1 => CompressionFormat.Bc1,
            BlpOutputFormat.Dxt1Alpha => CompressionFormat.Bc1WithAlpha,
            BlpOutputFormat.Dxt3 => CompressionFormat.Bc2,
            BlpOutputFormat.Dxt5 => CompressionFormat.Bc3,
            _ => throw new InvalidOperationException($"Unsupported BLP output format: {selected}")
        };
        var encoder = new BcEncoder();
        encoder.OutputOptions.Format = format;
        encoder.OutputOptions.GenerateMipMaps = options.GenerateMipmaps;
        encoder.OutputOptions.Quality = options.Quality switch
        {
            BlpOutputQuality.Fast => CompressionQuality.Fast,
            BlpOutputQuality.Balanced => CompressionQuality.Balanced,
            _ => CompressionQuality.BestQuality
        };
        var mipData = encoder.EncodeToRawBytes(texture.Pixels, texture.Width, texture.Height, PixelFormat.Rgba32);
        if (mipData.Length == 0) throw new InvalidDataException("The texture compressor produced no mip data.");
        if (mipData.Length > 16) mipData = mipData[..16];

        var offsets = new uint[16];
        var sizes = new uint[16];
        long cursor = Blp2HeaderSize;
        for (var index = 0; index < mipData.Length; index++)
        {
            if (mipData[index].Length == 0) throw new InvalidDataException($"The texture compressor produced an empty mip {index}.");
            offsets[index] = checked((uint)cursor);
            sizes[index] = checked((uint)mipData[index].Length);
            cursor = checked(cursor + mipData[index].Length);
        }

        outputPath = PrepareOutputPath(outputPath, overwrite);
        var temporary = outputPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.SequentialScan))
            {
                Span<byte> header = stackalloc byte[Blp2HeaderSize];
                "BLP2"u8.CopyTo(header);
                BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], 1);
                header[8] = 2;
                (header[9], header[10]) = selected switch
                {
                    BlpOutputFormat.Dxt1 => ((byte)0, (byte)0),
                    BlpOutputFormat.Dxt1Alpha => ((byte)1, (byte)0),
                    BlpOutputFormat.Dxt3 => ((byte)8, (byte)1),
                    _ => ((byte)8, (byte)7)
                };
                header[11] = (byte)(mipData.Length > 1 ? 1 : 0);
                BinaryPrimitives.WriteUInt32LittleEndian(header[12..16], checked((uint)texture.Width));
                BinaryPrimitives.WriteUInt32LittleEndian(header[16..20], checked((uint)texture.Height));
                for (var index = 0; index < 16; index++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(20 + index * 4, 4), offsets[index]);
                    BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(84 + index * 4, 4), sizes[index]);
                }
                output.Write(header);
                foreach (var mip in mipData) output.Write(mip);
                output.Flush(true);
            }
            File.Move(temporary, outputPath, overwrite);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    public static void WritePng(string outputPath, RgbaTexture texture, bool overwrite = false)
    {
        ValidateTexture(texture);
        outputPath = PrepareOutputPath(outputPath, overwrite);
        var temporary = outputPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.SequentialScan))
            {
                output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
                Span<byte> ihdr = stackalloc byte[13];
                BinaryPrimitives.WriteUInt32BigEndian(ihdr[..4], checked((uint)texture.Width));
                BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..8], checked((uint)texture.Height));
                ihdr[8] = 8; ihdr[9] = 6;
                WritePngChunk(output, "IHDR"u8, ihdr);

                using var compressed = new MemoryStream();
                using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
                {
                    var stride = checked(texture.Width * 4);
                    for (var row = 0; row < texture.Height; row++)
                    {
                        zlib.WriteByte(0);
                        zlib.Write(texture.Pixels, row * stride, stride);
                    }
                }
                WritePngChunk(output, "IDAT"u8, compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length)));
                WritePngChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
                output.Flush(true);
            }
            File.Move(temporary, outputPath, overwrite);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    public static IReadOnlyList<BlpValidationResult> Validate(string path, bool recursive = false, CancellationToken cancellationToken = default)
    {
        var results = new List<BlpValidationResult>();
        ValidateEach(path, recursive, results.Add, cancellationToken);
        return results;
    }

    public static BlpValidationSummary ValidateEach(string path, bool recursive, Action<BlpValidationResult> resultSink, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resultSink);
        path = Path.GetFullPath(path);
        IEnumerable<string> files = File.Exists(path) ? [path]
            : Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(file => Path.GetExtension(file).Equals(".blp", StringComparison.OrdinalIgnoreCase))
            : throw new FileNotFoundException("The BLP file or directory does not exist.", path);
        var total = 0; var failures = 0; var warnings = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested(); BlpValidationResult result;
            try { var info = Inspect(file); result = new(file, true, info, null); if (info.Warnings.Count > 0) warnings++; }
            catch (Exception exception) { result = new(file, false, null, exception.Message); failures++; }
            total++; resultSink(result);
        }
        return new(total, failures, warnings);
    }

    private static BlpTextureInfo InspectBlp2(Stream stream, string sourceName, long start)
    {
        var header = ReadExactly(stream, Blp2HeaderSize);
        var type = U32(header, 4);
        var encoding = header[8];
        var rawAlphaDepth = header[9]; var alphaDepth = (int)rawAlphaDepth;
        var alphaEncoding = header[10];
        var hasMipmaps = header[11] != 0;
        var width = Dimension(U32(header, 12), "width");
        var height = Dimension(U32(header, 16), "height");
        ValidatePixelCount(width, height);
        if (type != 1) throw new InvalidDataException($"Unsupported BLP2 type {type}; Wrath textures use type 1.");
        if (encoding is not 1 and not 2 and not 3) throw new InvalidDataException($"Unsupported BLP2 encoding {encoding}; native support covers palette, DXT and raw BGRA textures.");
        var headerWarnings = new List<string>();
        if (alphaDepth is not 0 and not 1 and not 4 and not 8)
        {
            var lowNibble = alphaDepth & 0x0f;
            if (lowNibble is 0 or 1 or 4 or 8) { alphaDepth = lowNibble; headerWarnings.Add($"Normalized non-standard alpha-depth byte 0x{rawAlphaDepth:X2} to {alphaDepth} from its valid low nibble."); }
            else throw new InvalidDataException($"Unsupported BLP2 alpha depth {alphaDepth}.");
        }
        var label = encoding switch { 1 => "Palette", 2 => DxtLabel(alphaDepth, alphaEncoding), _ => "BGRA8888" };
        var structuralWarnings = new List<string>();
        var mips = ParseMipTable(header, 20, 84, width, height, start, stream.Length, encoding == 1 ? Blp2HeaderSize + PaletteBytes : Blp2HeaderSize,
            (w, h) => encoding switch { 1 => PalettePayloadSize(w, h, alphaDepth), 2 => DxtPayloadSize(w, h, alphaDepth, alphaEncoding), _ => checked(w * h * 4) }, structuralWarnings);
        var warnings = headerWarnings.Concat(MipWarnings(hasMipmaps, mips)).Concat(structuralWarnings).ToArray();
        return new(NormalizeSourceName(sourceName), BlpTextureVersion.Blp2, width, height, label, alphaDepth, alphaEncoding, hasMipmaps, mips, warnings);
    }

    private static BlpTextureInfo InspectBlp1(Stream stream, string sourceName, long start)
    {
        var header = ReadExactly(stream, Blp1HeaderSize);
        var compression = U32(header, 4);
        var alphaDepth = checked((int)U32(header, 8));
        var width = Dimension(U32(header, 12), "width");
        var height = Dimension(U32(header, 16), "height");
        var pictureType = U32(header, 20);
        var pictureSubType = U32(header, 24);
        ValidatePixelCount(width, height);
        if (compression is not 0 and not 1) throw new InvalidDataException($"Unsupported BLP1 compression {compression}; native support covers JPEG and palette textures.");
        if (alphaDepth is not 0 and not 1 and not 4 and not 8) throw new InvalidDataException($"Unsupported BLP1 alpha depth {alphaDepth}.");
        var minimumDataOffset = compression == 1 ? Blp1HeaderSize + PaletteBytes : Blp1HeaderSize + 4;
        if (compression == 0)
        {
            stream.Position = start + Blp1HeaderSize;
            var sharedHeaderBytes = checked((int)ReadUInt32(stream));
            minimumDataOffset = checked(minimumDataOffset + sharedHeaderBytes);
            if (start + minimumDataOffset > stream.Length) throw new InvalidDataException("The BLP1 shared JPEG header extends past the end of the file.");
        }
        var structuralWarnings = new List<string>();
        var mips = ParseMipTable(header, 28, 92, width, height, start, stream.Length, minimumDataOffset,
            (w, h) => compression == 1 ? PalettePayloadSize(w, h, alphaDepth) : 1, structuralWarnings);
        var warnings = MipWarnings(mips.Count > 1, mips).Concat(structuralWarnings).ToList();
        if (pictureType is not 3 and not 4 and not 5) warnings.Add($"Uncommon BLP1 picture type {pictureType}/{pictureSubType}; the pixel payload remains decodable.");
        if (compression == 0 && alphaDepth != 0) warnings.Add("BLP1 JPEG alpha planes are uncommon; RGB is decoded from JPEG and alpha compatibility should be checked in client.");
        return new(NormalizeSourceName(sourceName), BlpTextureVersion.Blp1, width, height, compression == 0 ? "JPEG" : "Palette", alphaDepth, checked((int)pictureSubType), mips.Count > 1, mips, warnings);
    }

    private static RgbaTexture DecodeBlp2(Stream stream, BlpTextureInfo info, BlpMipLevel mip, long start)
    {
        if (info.Encoding == "Palette") return DecodePalette(stream, start + Blp2HeaderSize, start + mip.Offset, mip, info.AlphaDepth);
        stream.Position = start + mip.Offset;
        var payload = ReadExactly(stream, mip.Size);
        if (info.Encoding == "BGRA8888")
        {
            var count = checked(mip.Width * mip.Height); var rawPixels = new byte[checked(count * 4)];
            for (var index = 0; index < count; index++)
            {
                rawPixels[index * 4] = payload[index * 4 + 2]; rawPixels[index * 4 + 1] = payload[index * 4 + 1]; rawPixels[index * 4 + 2] = payload[index * 4];
                rawPixels[index * 4 + 3] = info.AlphaDepth == 0 ? (byte)255 : payload[index * 4 + 3];
            }
            return new(mip.Width, mip.Height, rawPixels);
        }
        var format = info.Encoding switch
        {
            "DXT1" => CompressionFormat.Bc1,
            "DXT1A" => CompressionFormat.Bc1WithAlpha,
            "DXT3" => CompressionFormat.Bc2,
            "DXT5" => CompressionFormat.Bc3,
            _ => throw new InvalidDataException($"Unsupported compressed BLP encoding: {info.Encoding}")
        };
        var colors = new BcDecoder().DecodeRaw(payload, mip.Width, mip.Height, format);
        var pixels = new byte[checked(colors.Length * 4)];
        for (var index = 0; index < colors.Length; index++)
        {
            pixels[index * 4] = colors[index].r;
            pixels[index * 4 + 1] = colors[index].g;
            pixels[index * 4 + 2] = colors[index].b;
            pixels[index * 4 + 3] = colors[index].a;
        }
        return new(mip.Width, mip.Height, pixels);
    }

    private static RgbaTexture DecodeBlp1(Stream stream, BlpTextureInfo info, BlpMipLevel mip, long start)
    {
        if (info.Encoding == "Palette") return DecodePalette(stream, start + Blp1HeaderSize, start + mip.Offset, mip, info.AlphaDepth);
        stream.Position = start + Blp1HeaderSize;
        var sharedHeaderSize = checked((int)ReadUInt32(stream));
        var sharedHeader = ReadExactly(stream, sharedHeaderSize);
        stream.Position = start + mip.Offset;
        var payload = ReadExactly(stream, mip.Size);
        var jpeg = new byte[checked(sharedHeader.Length + payload.Length)];
        sharedHeader.CopyTo(jpeg, 0); payload.CopyTo(jpeg, sharedHeader.Length);
        var image = ImageResult.FromMemory(jpeg, ColorComponents.RedGreenBlueAlpha);
        if (image.Width != mip.Width || image.Height != mip.Height)
            throw new InvalidDataException($"BLP1 JPEG mip declares {mip.Width}x{mip.Height} but decodes as {image.Width}x{image.Height}.");
        return new(image.Width, image.Height, image.Data);
    }

    private static RgbaTexture DecodePalette(Stream stream, long paletteOffset, long payloadOffset, BlpMipLevel mip, int alphaDepth)
    {
        stream.Position = paletteOffset;
        var palette = ReadExactly(stream, PaletteBytes);
        stream.Position = payloadOffset;
        var count = checked(mip.Width * mip.Height);
        var indices = ReadExactly(stream, count);
        var alphaSize = AlphaBytes(count, alphaDepth);
        var alpha = alphaSize == 0 ? Array.Empty<byte>() : ReadExactly(stream, alphaSize);
        var pixels = new byte[checked(count * 4)];
        for (var index = 0; index < count; index++)
        {
            var paletteIndex = indices[index] * 4;
            pixels[index * 4] = palette[paletteIndex + 2];
            pixels[index * 4 + 1] = palette[paletteIndex + 1];
            pixels[index * 4 + 2] = palette[paletteIndex];
            pixels[index * 4 + 3] = Alpha(alpha, index, alphaDepth);
        }
        return new(mip.Width, mip.Height, pixels);
    }

    private static IReadOnlyList<BlpMipLevel> ParseMipTable(byte[] header, int offsetsStart, int sizesStart, int width, int height,
        long streamStart, long streamLength, int minimumDataOffset, Func<int, int, int> minimumPayloadSize, ICollection<string> warnings)
    {
        if (U32(header, offsetsStart) == uint.MaxValue && U32(header, sizesStart) is var firstEnd && firstEnd > minimumDataOffset && firstEnd <= streamLength - streamStart + 1)
            return ParseCumulativeEndMipTable(header, sizesStart, width, height, streamStart, streamLength, minimumDataOffset, minimumPayloadSize, warnings);
        var mips = new List<BlpMipLevel>();
        var ranges = new List<(long Start, long End, int Index)>();
        var sawEmpty = false; var maximumMipCount = 1 + (int)Math.Floor(Math.Log2(Math.Max(width, height)));
        for (var index = 0; index < 16; index++)
        {
            var offset = U32(header, offsetsStart + index * 4);
            var size = U32(header, sizesStart + index * 4);
            if (index >= maximumMipCount)
            {
                if (offset != 0 || size != 0) warnings.Add($"Ignored non-empty phantom mip slot {index} beyond the complete 1x1 chain (offset {offset:N0}, size {size:N0}).");
                continue;
            }
            if (offset == 0 && size == 0) { sawEmpty = true; continue; }
            if (offset == 0 || size == 0)
            {
                if (mips.Count > 0) { warnings.Add($"Ignored malformed trailing mip {index}: only one of offset/size is set."); break; }
                throw new InvalidDataException($"Mip {index} has only one of offset/size set.");
            }
            if (sawEmpty)
            {
                if (mips.Count > 0) { warnings.Add($"Ignored trailing mip {index} after an empty mip-table slot."); break; }
                throw new InvalidDataException($"Mip {index} appears after an empty mip-table slot.");
            }
            var mipWidth = Math.Max(1, width >> index);
            var mipHeight = Math.Max(1, height >> index);
            var minimum = minimumPayloadSize(mipWidth, mipHeight);
            if (size < minimum)
            {
                if (mips.Count > 0) { warnings.Add($"Stopped before undersized trailing mip {index}: {size:N0} bytes supplied, {minimum:N0} required for {mipWidth}x{mipHeight}."); break; }
                throw new InvalidDataException($"Mip {index} payload is {size:N0} bytes; at least {minimum:N0} are required for {mipWidth}x{mipHeight}.");
            }
            if (offset < minimumDataOffset)
            {
                if (mips.Count > 0) { warnings.Add($"Stopped before trailing mip {index}, whose payload begins inside the BLP header/palette area."); break; }
                throw new InvalidDataException($"Mip {index} begins inside the BLP header/palette area.");
            }
            var absoluteStart = checked(streamStart + offset);
            var absoluteEnd = checked(absoluteStart + size);
            if (absoluteEnd > streamLength)
            {
                if (mips.Count > 0) { warnings.Add($"Stopped before truncated trailing mip {index}, which extends {absoluteEnd - streamLength:N0} bytes past EOF."); break; }
                throw new InvalidDataException($"Mip {index} extends {absoluteEnd - streamLength:N0} bytes past the end of the file.");
            }
            foreach (var range in ranges)
                if (absoluteStart < range.End && absoluteEnd > range.Start)
                {
                    if (mips.Count > 0) { warnings.Add($"Stopped before trailing mip {index}, which overlaps mip {range.Index}."); return mips; }
                    throw new InvalidDataException($"Mip {index} overlaps mip {range.Index}.");
                }
            ranges.Add((absoluteStart, absoluteEnd, index));
            mips.Add(new(index, mipWidth, mipHeight, offset, checked((int)size)));
        }
        if (mips.Count == 0 || mips[0].Index != 0) throw new InvalidDataException("The BLP has no top-level mip payload.");
        return mips;
    }

    private static IReadOnlyList<BlpMipLevel> ParseCumulativeEndMipTable(byte[] header, int endsStart, int width, int height,
        long streamStart, long streamLength, int minimumDataOffset, Func<int, int, int> minimumPayloadSize, ICollection<string> warnings)
    {
        var result = new List<BlpMipLevel>(); var maximumMipCount = 1 + (int)Math.Floor(Math.Log2(Math.Max(width, height))); long previousEnd = 0;
        for (var index = 0; index < Math.Min(16, maximumMipCount); index++)
        {
            var declaredEnd = U32(header, endsStart + index * 4); if (declaredEnd == 0) break;
            var mipWidth = Math.Max(1, width >> index); var mipHeight = Math.Max(1, height >> index); var minimum = minimumPayloadSize(mipWidth, mipHeight);
            var offset = index == 0 ? (long)declaredEnd - minimum : previousEnd;
            var end = Math.Min((long)declaredEnd, streamLength - streamStart); var size = end - offset;
            if (offset < minimumDataOffset || size < minimum || end <= offset)
            {
                if (result.Count > 0) { warnings.Add($"Stopped before malformed recovered trailing mip {index} in the legacy cumulative-end table."); break; }
                throw new InvalidDataException("The legacy cumulative-end mip table could not produce a valid top-level payload.");
            }
            if (declaredEnd > streamLength - streamStart) warnings.Add($"Clamped recovered mip {index}'s cumulative end by {declaredEnd - (streamLength - streamStart):N0} byte(s) at EOF.");
            result.Add(new(index, mipWidth, mipHeight, checked((int)offset), checked((int)size))); previousEnd = declaredEnd;
        }
        if (result.Count == 0) throw new InvalidDataException("The legacy cumulative-end mip table contains no usable mip levels.");
        warnings.Add("Recovered a legacy exporter mip table whose offset entries are 0xFFFFFFFF and whose size entries contain cumulative payload ends.");
        return result;
    }

    private static IReadOnlyList<string> MipWarnings(bool declaresMipmaps, IReadOnlyList<BlpMipLevel> mips)
    {
        var warnings = new List<string>();
        if (declaresMipmaps && mips.Count == 1) warnings.Add("The header declares mipmaps but only the top level is present.");
        if (!declaresMipmaps && mips.Count > 1) warnings.Add("Mip levels are present even though the header does not declare them.");
        var last = mips[^1];
        if (declaresMipmaps && (last.Width != 1 || last.Height != 1)) warnings.Add("The mip chain stops before 1x1.");
        return warnings;
    }

    private static string DxtLabel(int alphaDepth, int alphaEncoding)
    {
        if (alphaDepth == 0) return "DXT1";
        if (alphaDepth == 1) return "DXT1A";
        return alphaEncoding switch
        {
            1 => "DXT3",
            7 => "DXT5",
            _ => throw new InvalidDataException($"Unsupported BLP2 DXT alpha combination depth={alphaDepth}, encoding={alphaEncoding}.")
        };
    }

    private static int DxtPayloadSize(int width, int height, int alphaDepth, int alphaEncoding)
    {
        var blockBytes = DxtLabel(alphaDepth, alphaEncoding) is "DXT1" or "DXT1A" ? 8 : 16;
        return checked(Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * blockBytes);
    }

    private static int PalettePayloadSize(int width, int height, int alphaDepth)
    {
        var pixels = checked(width * height);
        return checked(pixels + AlphaBytes(pixels, alphaDepth));
    }

    private static int AlphaBytes(int pixels, int alphaDepth) => alphaDepth switch
    {
        0 => 0,
        1 => checked((pixels + 7) / 8),
        4 => checked((pixels + 1) / 2),
        8 => pixels,
        _ => throw new InvalidDataException($"Unsupported alpha depth {alphaDepth}.")
    };

    private static byte Alpha(byte[] alpha, int index, int depth) => depth switch
    {
        0 => 255,
        1 => (byte)(((alpha[index >> 3] >> (index & 7)) & 1) * 255),
        4 => (byte)((index % 2 == 0 ? alpha[index >> 1] & 0x0f : alpha[index >> 1] >> 4) * 17),
        8 => alpha[index],
        _ => throw new InvalidDataException($"Unsupported alpha depth {depth}.")
    };

    private static BlpOutputFormat SelectFormat(byte[] pixels, BlpOutputFormat requested)
    {
        if (requested != BlpOutputFormat.Auto) return requested;
        var binary = true; var opaque = true;
        for (var index = 3; index < pixels.Length; index += 4)
        {
            var alpha = pixels[index];
            if (alpha != 255) opaque = false;
            if (alpha is not 0 and not 255) binary = false;
            if (!binary && !opaque) return BlpOutputFormat.Dxt5;
        }
        return opaque ? BlpOutputFormat.Dxt1 : binary ? BlpOutputFormat.Dxt1Alpha : BlpOutputFormat.Dxt5;
    }

    private static void ValidateTexture(RgbaTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (texture.Width <= 0 || texture.Width > MaximumDimension || texture.Height <= 0 || texture.Height > MaximumDimension)
            throw new ArgumentOutOfRangeException(nameof(texture), $"Texture dimensions must be between 1 and {MaximumDimension:N0}.");
        ValidatePixelCount(texture.Width, texture.Height);
        if (texture.Pixels is null || texture.Pixels.Length != texture.ByteLength)
            throw new ArgumentException($"RGBA data must contain exactly {texture.ByteLength:N0} bytes.", nameof(texture));
    }

    private static int Dimension(uint value, string name)
    {
        if (value == 0 || value > MaximumDimension) throw new InvalidDataException($"BLP {name} {value:N0} is outside the supported 1..{MaximumDimension:N0} range.");
        return checked((int)value);
    }

    private static void ValidatePixelCount(int width, int height)
    {
        if ((long)width * height > MaximumPixels) throw new InvalidDataException($"Texture dimensions {width:N0}x{height:N0} exceed the safe pixel limit.");
    }

    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));

    private static uint ReadUInt32(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[4];
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static byte[] ReadExactly(Stream stream, int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        var bytes = new byte[count];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static string PrepareOutputPath(string outputPath, bool overwrite)
    {
        outputPath = Path.GetFullPath(outputPath);
        if (File.Exists(outputPath) && !overwrite) throw new IOException($"Output already exists: {outputPath}. Use overwrite explicitly to replace it.");
        return outputPath;
    }

    private static string NormalizeSourceName(string sourceName) => sourceName.StartsWith('<') && sourceName.EndsWith('>') ? sourceName : Path.GetFullPath(sourceName);

    private static void WritePngChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        if (type.Length != 4) throw new ArgumentException("PNG chunk type must be four bytes.", nameof(type));
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)payload.Length));
        output.Write(length); output.Write(type); output.Write(payload);
        var crc = 0xffffffffu;
        foreach (var value in type) crc = UpdateCrc(crc, value);
        foreach (var value in payload) crc = UpdateCrc(crc, value);
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, ~crc);
        output.Write(checksum);
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
        return crc;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
