using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public enum AdtAlphaEncoding { Packed4Bit, Big8Bit, Rle8Bit }

public sealed record AdtAlphaMapSummary(
    int CellX,
    int CellY,
    int Slot,
    uint TextureId,
    string? TexturePath,
    uint Flags,
    uint RelativeOffset,
    long DataOffset,
    int Capacity,
    int EncodedBytesUsed,
    AdtAlphaEncoding Encoding,
    byte Minimum,
    byte Maximum,
    double Average);

public sealed record AdtAlphaMapInspection(
    string Path,
    string Sha256,
    IReadOnlyList<AdtAlphaMapSummary> Maps,
    IReadOnlyList<string> Findings);

public sealed record AdtAlphaMapEdit(
    int CellX,
    int CellY,
    int Slot,
    long DataOffset,
    int Capacity,
    AdtAlphaEncoding Encoding,
    string OriginalEncodedSha256,
    string EditedPixelsSha256,
    byte[] EditedEncoded,
    int ChangedPixels);

public sealed record AdtAlphaCell(int X, int Y);

public sealed record AdtAlphaBrushPlan(
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    string InputPath,
    string InputSha256,
    int LayerSlot,
    float CenterX,
    float CenterY,
    float Radius,
    byte TargetAlpha,
    float Opacity,
    AdtTerrainBrushFalloff Falloff,
    IReadOnlyList<AdtAlphaCell> Cells,
    IReadOnlyList<AdtAlphaMapEdit> Edits);

public sealed record AdtAlphaBrushResult(
    string OutputPath,
    string OutputSha256,
    string ReceiptPath,
    AdtAlphaMapInspection Inspection,
    int EditedMaps,
    int EditedCells,
    int EditedPixels);

public static class AdtAlphaMapCodec
{
    public const int PixelCount = 64 * 64;

    public static byte[] Decode(AdtAlphaEncoding encoding, ReadOnlySpan<byte> encoded)
        => encoding switch
        {
            AdtAlphaEncoding.Packed4Bit => DecodePacked(encoded),
            AdtAlphaEncoding.Big8Bit => DecodeBig(encoded),
            AdtAlphaEncoding.Rle8Bit => DecodeRle(encoded, out _),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding))
        };

    public static byte[] Encode(AdtAlphaEncoding encoding, ReadOnlySpan<byte> pixels)
    {
        if (pixels.Length != PixelCount) throw new ArgumentException($"An alpha map must contain exactly {PixelCount:N0} pixels.", nameof(pixels));
        return encoding switch
        {
            AdtAlphaEncoding.Packed4Bit => EncodePacked(pixels),
            AdtAlphaEncoding.Big8Bit => pixels.ToArray(),
            AdtAlphaEncoding.Rle8Bit => EncodeRle(pixels),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding))
        };
    }

    internal static byte[] DecodeRle(ReadOnlySpan<byte> encoded, out int consumed)
    {
        var pixels = new byte[PixelCount]; var input = 0; var output = 0;
        while (output < pixels.Length)
        {
            if (input >= encoded.Length) throw new InvalidDataException($"RLE alpha map ended after {output:N0} of {PixelCount:N0} pixels.");
            var control = encoded[input++]; var count = control & 0x7F;
            if (count == 0) throw new InvalidDataException("RLE alpha map contains a zero-length run.");
            if (output + count > pixels.Length) throw new InvalidDataException("RLE alpha map expands beyond 4,096 pixels.");
            if ((control & 0x80) != 0)
            {
                if (input >= encoded.Length) throw new InvalidDataException("RLE alpha fill run has no value byte.");
                pixels.AsSpan(output, count).Fill(encoded[input++]);
            }
            else
            {
                if (input + count > encoded.Length) throw new InvalidDataException("RLE alpha literal run extends beyond its MCAL slice.");
                encoded.Slice(input, count).CopyTo(pixels.AsSpan(output, count)); input += count;
            }
            output += count;
        }
        consumed = input; return pixels;
    }

    private static byte[] DecodePacked(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length != 2048) throw new InvalidDataException($"Packed alpha map must be 2,048 bytes, not {encoded.Length:N0}.");
        var pixels = new byte[PixelCount];
        for (var y = 0; y < 63; y++)
            for (var pair = 0; pair < 32; pair++)
            {
                var value = encoded[y * 32 + pair]; var x = pair * 2;
                pixels[y * 64 + x] = ExpandNibble(value & 0x0F);
                pixels[y * 64 + x + 1] = pair == 31 ? pixels[y * 64 + x] : ExpandNibble(value >> 4);
            }
        pixels.AsSpan(62 * 64, 64).CopyTo(pixels.AsSpan(63 * 64, 64));
        return pixels;
    }

    private static byte[] DecodeBig(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length != PixelCount) throw new InvalidDataException($"Big alpha map must be 4,096 bytes, not {encoded.Length:N0}.");
        return encoded.ToArray();
    }

    private static byte[] EncodePacked(ReadOnlySpan<byte> pixels)
    {
        var encoded = new byte[2048];
        for (var y = 0; y < 63; y++)
            for (var pair = 0; pair < 32; pair++)
            {
                var x = pair * 2; var low = QuantizeNibble(pixels[y * 64 + x]);
                var high = QuantizeNibble(pixels[y * 64 + (pair == 31 ? x : x + 1)]);
                encoded[y * 32 + pair] = (byte)(low | high << 4);
            }
        encoded.AsSpan(62 * 32, 32).CopyTo(encoded.AsSpan(63 * 32, 32));
        return encoded;
    }

    private static byte[] EncodeRle(ReadOnlySpan<byte> pixels)
    {
        var output = new List<byte>(); var index = 0;
        while (index < pixels.Length)
        {
            var repeated = RepeatLength(pixels, index);
            if (repeated >= 3)
            {
                var count = Math.Min(127, repeated); output.Add((byte)(0x80 | count)); output.Add(pixels[index]); index += count; continue;
            }
            var literalStart = index; index += repeated;
            while (index < pixels.Length && index - literalStart < 127)
            {
                repeated = RepeatLength(pixels, index);
                if (repeated >= 3) break;
                index += Math.Min(repeated, 127 - (index - literalStart));
            }
            var literalCount = index - literalStart; output.Add((byte)literalCount);
            for (var offset = 0; offset < literalCount; offset++) output.Add(pixels[literalStart + offset]);
        }
        return output.ToArray();
    }

    private static int RepeatLength(ReadOnlySpan<byte> pixels, int start)
    {
        var count = 1; while (start + count < pixels.Length && count < 127 && pixels[start + count] == pixels[start]) count++; return count;
    }
    private static byte ExpandNibble(int value) => (byte)(value * 17);
    private static int QuantizeNibble(byte value) => Math.Clamp((int)Math.Round(value / 17d, MidpointRounding.AwayFromZero), 0, 15);
}

public static class AdtAlphaMapService
{
    private const int FormatVersion = 1;
    private const uint UseAlphaMap = 0x100;
    private const uint AlphaCompressed = 0x200;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public static AdtAlphaMapInspection Inspect(string path)
    {
        path = Path.GetFullPath(path); var map = MapAssetInspectionService.Inspect(path);
        if (map.Kind != MapAssetKind.Adt || map.Version != 18) throw new InvalidDataException("Alpha-map inspection requires a validated WotLK MVER 18 ADT.");
        var textures = AdtTextureLayerService.Inspect(path); var texturePaths = textures.Layers.ToDictionary(layer => (layer.CellX, layer.CellY, layer.Slot), layer => (layer.TextureId, layer.TexturePath));
        var bytes = File.ReadAllBytes(path); var maps = new List<AdtAlphaMapSummary>(); var findings = new List<string>(); var position = 0;
        while (position < bytes.Length)
        {
            Require(bytes, position, 8, "top-level ADT chunk header"); var id = Decode(bytes.AsSpan(position, 4)); var size = ReadU32(bytes, position + 4); var end = checked(position + 8L + size);
            if (end > bytes.LongLength) throw new InvalidDataException($"ADT chunk {id} at byte {position:N0} extends beyond the file.");
            if (id == "MCNK") InspectCell(bytes, position, checked((int)end), texturePaths, maps, findings);
            position = checked((int)end);
        }
        return new(path, Sha256(path), maps.OrderBy(value => value.CellY).ThenBy(value => value.CellX).ThenBy(value => value.Slot).ToArray(), findings.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static byte[] ReadPixels(string path, AdtAlphaMapSummary map)
    {
        path = Path.GetFullPath(path); return ReadPixels(File.ReadAllBytes(path), map);
    }

    public static AdtAlphaBrushPlan Plan(string inputPath, int layerSlot, float centerX, float centerY, float radius, byte targetAlpha, float opacity,
        AdtTerrainBrushFalloff falloff = AdtTerrainBrushFalloff.Smooth, IEnumerable<(int X, int Y)>? cells = null)
    {
        ValidateBrush(layerSlot, centerX, centerY, radius, opacity, falloff); var inspection = Inspect(inputPath); var selected = NormalizeCells(cells);
        var edits = BuildEdits(inspection, layerSlot, centerX, centerY, radius, targetAlpha, opacity, falloff, selected);
        if (edits.Count == 0) throw new InvalidOperationException("The alpha brush produced no stored pixel changes. Check the layer slot, brush position/radius, target alpha, and optional cell restriction.");
        return new(FormatVersion, DateTimeOffset.UtcNow, inspection.Path, inspection.Sha256, layerSlot, centerX, centerY, radius, targetAlpha, opacity, falloff, selected, edits);
    }

    public static AdtAlphaMapInspection Preview(AdtAlphaBrushPlan plan)
    {
        var inspection = ValidatePlan(plan); var edits = plan.Edits.ToDictionary(edit => (edit.CellX, edit.CellY, edit.Slot));
        return inspection with
        {
            Maps = inspection.Maps.Select(map => edits.TryGetValue((map.CellX, map.CellY, map.Slot), out var edit)
                ? WithStatistics(map, AdtAlphaMapCodec.Decode(map.Encoding, edit.EditedEncoded))
                : map).ToArray()
        };
    }

    public static void SavePlan(AdtAlphaBrushPlan plan, string path, bool overwrite = false)
    {
        _ = ValidatePlan(plan); path = Path.GetFullPath(path); if (File.Exists(path) && !overwrite) throw new IOException($"Alpha-brush plan already exists: {path}");
        AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(plan, JsonOptions), overwrite);
    }

    public static AdtAlphaBrushPlan LoadPlan(string path)
    {
        path = Path.GetFullPath(path); if (!File.Exists(path)) throw new FileNotFoundException("The ADT alpha-brush plan does not exist.", path);
        var plan = JsonSerializer.Deserialize<AdtAlphaBrushPlan>(File.ReadAllBytes(path), JsonOptions) ?? throw new InvalidDataException("The ADT alpha-brush plan is empty.");
        _ = ValidatePlan(plan); return plan;
    }

    public static AdtAlphaBrushResult Apply(AdtAlphaBrushPlan plan, string outputPath, bool overwrite = false)
    {
        _ = ValidatePlan(plan); outputPath = Path.GetFullPath(outputPath);
        if (outputPath.Equals(Path.GetFullPath(plan.InputPath), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Crucible does not overwrite the source ADT; choose a separate alpha-painted output path.");
        if (File.Exists(outputPath) && !overwrite) throw new IOException($"Output ADT already exists: {outputPath}");
        var bytes = File.ReadAllBytes(plan.InputPath);
        foreach (var edit in plan.Edits)
        {
            Require(bytes, edit.DataOffset, edit.Capacity, $"planned MCAL map {edit.CellX},{edit.CellY}:{edit.Slot}");
            var current = bytes.AsSpan(checked((int)edit.DataOffset), edit.Capacity); if (!Hash(current).Equals(edit.OriginalEncodedSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"MCAL map {edit.CellX},{edit.CellY}:{edit.Slot} no longer matches its planned byte preimage.");
            edit.EditedEncoded.CopyTo(current);
        }
        AtomicWrite(outputPath, bytes, overwrite); var inspection = Inspect(outputPath); var writtenBytes = File.ReadAllBytes(outputPath);
        foreach (var edit in plan.Edits)
        {
            var written = inspection.Maps.Single(map => map.CellX == edit.CellX && map.CellY == edit.CellY && map.Slot == edit.Slot);
            if (!Hash(ReadPixels(writtenBytes, written)).Equals(edit.EditedPixelsSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Written MCAL map {edit.CellX},{edit.CellY}:{edit.Slot} did not re-parse to its planned pixels.");
        }
        var receiptPath = outputPath + ".crucible-map-alpha.json"; var outputHash = inspection.Sha256;
        var receipt = new { FormatVersion, AppliedUtc = DateTimeOffset.UtcNow, plan.InputPath, plan.InputSha256, OutputPath = outputPath, OutputSha256 = outputHash, plan.LayerSlot, plan.CenterX, plan.CenterY, plan.Radius, plan.TargetAlpha, plan.Opacity, plan.Falloff, plan.Cells, plan.Edits };
        AtomicWrite(receiptPath, JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions), overwrite: true);
        return new(outputPath, outputHash, receiptPath, inspection, plan.Edits.Count, plan.Edits.Select(edit => (edit.CellX, edit.CellY)).Distinct().Count(), plan.Edits.Sum(edit => edit.ChangedPixels));
    }

    private static void InspectCell(byte[] bytes, int chunkOffset, int chunkEnd,
        IReadOnlyDictionary<(int X, int Y, int Slot), (uint TextureId, string? TexturePath)> texturePaths,
        ICollection<AdtAlphaMapSummary> maps, ICollection<string> findings)
    {
        if (chunkEnd - chunkOffset < 136) throw new InvalidDataException($"ADT MCNK at byte {chunkOffset:N0} is shorter than its 128-byte header.");
        var header = chunkOffset + 8; var x = ReadU32(bytes, header + 4); var y = ReadU32(bytes, header + 8); var count = ReadU32(bytes, header + 0x0C);
        if (x >= 16 || y >= 16 || count > 64) throw new InvalidDataException($"ADT MCNK at byte {chunkOffset:N0} has invalid coordinate/layer count {x},{y}/{count}.");
        if (count <= 1) return;
        var mcly = checked(chunkOffset + (int)ReadU32(bytes, header + 0x1C)); var mcal = checked(chunkOffset + (int)ReadU32(bytes, header + 0x24)); var declaredMcal = ReadU32(bytes, header + 0x28);
        ValidateNested(bytes, mcly, chunkEnd, "MCLY"); ValidateNested(bytes, mcal, chunkEnd, "MCAL");
        var mclySize = ReadU32(bytes, mcly + 4); var mcalSize = ReadU32(bytes, mcal + 4);
        if (mclySize < count * 16 || mcly + 8L + mclySize > chunkEnd || mcal + 8L + mcalSize > chunkEnd) throw new InvalidDataException($"ADT MCNK {x},{y} has a truncated MCLY or MCAL chunk.");
        if (declaredMcal != 0 && declaredMcal != mcalSize + 8) findings.Add($"MCNK {x},{y} declares sizeMCAL {declaredMcal:N0}, while its nested MCAL occupies {mcalSize + 8:N0} bytes.");
        var layers = new List<(int Slot, uint Flags, uint AlphaOffset)>();
        for (var slot = 1; slot < count; slot++)
        {
            var layer = mcly + 8 + slot * 16; var flags = ReadU32(bytes, layer + 4); var alphaOffset = ReadU32(bytes, layer + 8);
            if ((flags & UseAlphaMap) != 0) layers.Add((slot, flags, alphaOffset)); else findings.Add($"MCNK {x},{y} layer {slot} has no UseAlphaMap flag and cannot be painted safely.");
        }
        for (var index = 0; index < layers.Count; index++)
        {
            var layer = layers[index]; var end = index + 1 < layers.Count ? layers[index + 1].AlphaOffset : mcalSize;
            if (layer.AlphaOffset >= end || end > mcalSize) throw new InvalidDataException($"ADT MCNK {x},{y} layer {layer.Slot} has an invalid MCAL slice {layer.AlphaOffset:N0}..{end:N0} of {mcalSize:N0}.");
            var capacity = checked((int)(end - layer.AlphaOffset)); var encoding = (layer.Flags & AlphaCompressed) != 0 ? AdtAlphaEncoding.Rle8Bit : capacity switch { 2048 => AdtAlphaEncoding.Packed4Bit, 4096 => AdtAlphaEncoding.Big8Bit, _ => throw new InvalidDataException($"ADT MCNK {x},{y} layer {layer.Slot} uses an unsupported uncompressed alpha size of {capacity:N0} bytes.") };
            var dataOffset = checked(mcal + 8L + layer.AlphaOffset); var encoded = bytes.AsSpan(checked((int)dataOffset), capacity); byte[] pixels; int used;
            if (encoding == AdtAlphaEncoding.Rle8Bit) pixels = AdtAlphaMapCodec.DecodeRle(encoded, out used); else { pixels = AdtAlphaMapCodec.Decode(encoding, encoded); used = capacity; }
            var texture = texturePaths.GetValueOrDefault(((int)x, (int)y, layer.Slot)); maps.Add(WithStatistics(new((int)x, (int)y, layer.Slot, texture.TextureId, texture.TexturePath, layer.Flags, layer.AlphaOffset, dataOffset, capacity, used, encoding, 0, 0, 0), pixels));
        }
    }

    private static List<AdtAlphaMapEdit> BuildEdits(AdtAlphaMapInspection inspection, int layerSlot, float centerX, float centerY, float radius, byte targetAlpha, float opacity, AdtTerrainBrushFalloff falloff, IReadOnlyList<AdtAlphaCell> cells)
    {
        var selected = cells.Count == 0 ? null : cells.ToHashSet(); var source = File.ReadAllBytes(inspection.Path); var edits = new List<AdtAlphaMapEdit>();
        foreach (var map in inspection.Maps.Where(map => map.Slot == layerSlot && (selected is null || selected.Contains(new(map.CellX, map.CellY)))))
        {
            var originalEncoded = source.AsSpan(checked((int)map.DataOffset), map.Capacity).ToArray(); var originalPixels = AdtAlphaMapCodec.Decode(map.Encoding, originalEncoded); var editedPixels = originalPixels.ToArray();
            for (var y = 0; y < 64; y++) for (var x = 0; x < 64; x++)
            {
                var worldX = map.CellX + (x + 0.5f) / 64f; var worldY = map.CellY + (y + 0.5f) / 64f; var distance = MathF.Sqrt((worldX - centerX) * (worldX - centerX) + (worldY - centerY) * (worldY - centerY)); if (distance > radius) continue;
                var amount = opacity * Falloff(distance / radius, falloff); var index = y * 64 + x; editedPixels[index] = (byte)Math.Clamp((int)MathF.Round(originalPixels[index] + (targetAlpha - originalPixels[index]) * amount), 0, 255);
            }
            var encoded = EncodeFixed(map.Encoding, editedPixels, originalEncoded);
            if (encoded.AsSpan().SequenceEqual(originalEncoded)) continue;
            var storedPixels = AdtAlphaMapCodec.Decode(map.Encoding, encoded); var changed = originalPixels.Zip(storedPixels).Count(pair => pair.First != pair.Second);
            if (changed == 0) continue;
            edits.Add(new(map.CellX, map.CellY, map.Slot, map.DataOffset, map.Capacity, map.Encoding, Hash(originalEncoded), Hash(storedPixels), encoded, changed));
        }
        return edits;
    }

    private static byte[] EncodeFixed(AdtAlphaEncoding encoding, byte[] pixels, byte[] original)
    {
        var minimal = AdtAlphaMapCodec.Encode(encoding, pixels);
        if (minimal.Length > original.Length) throw new InvalidOperationException($"The painted {encoding} alpha map needs {minimal.Length:N0} encoded bytes but its fixed MCAL slice has only {original.Length:N0}. Reduce the stroke or first convert/resize the layer in a structural map project.");
        if (encoding != AdtAlphaEncoding.Packed4Bit)
        {
            var result = original.ToArray(); minimal.CopyTo(result, 0); return result;
        }
        // Packed Wrath alpha maps do not store an independent final column or row.
        // Preserve those ignored nibbles/bytes so a brush never normalizes unrelated source data.
        var packed = original.ToArray();
        for (var y = 0; y < 63; y++)
        {
            minimal.AsSpan(y * 32, 31).CopyTo(packed.AsSpan(y * 32, 31));
            packed[y * 32 + 31] = (byte)((packed[y * 32 + 31] & 0xF0) | (minimal[y * 32 + 31] & 0x0F));
        }
        return packed;
    }

    private static AdtAlphaMapInspection ValidatePlan(AdtAlphaBrushPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan); if (plan.FormatVersion != FormatVersion || plan.Edits.Count == 0) throw new InvalidDataException("ADT alpha-brush plan has an unsupported format or no edits.");
        ValidateBrush(plan.LayerSlot, plan.CenterX, plan.CenterY, plan.Radius, plan.Opacity, plan.Falloff); var inspection = Inspect(plan.InputPath);
        if (!inspection.Sha256.Equals(plan.InputSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Source ADT hash no longer matches the alpha-brush plan; rebuild the plan before applying it.");
        var cells = NormalizeCells(plan.Cells); var expected = BuildEdits(inspection, plan.LayerSlot, plan.CenterX, plan.CenterY, plan.Radius, plan.TargetAlpha, plan.Opacity, plan.Falloff, cells);
        if (expected.Count != plan.Edits.Count) throw new InvalidDataException("ADT alpha-brush plan edit count no longer matches its brush inputs.");
        for (var index = 0; index < expected.Count; index++)
        {
            var left = expected[index]; var right = plan.Edits[index];
            if (left.CellX != right.CellX || left.CellY != right.CellY || left.Slot != right.Slot || left.DataOffset != right.DataOffset || left.Capacity != right.Capacity || left.Encoding != right.Encoding || left.ChangedPixels != right.ChangedPixels || !left.OriginalEncodedSha256.Equals(right.OriginalEncodedSha256, StringComparison.OrdinalIgnoreCase) || !left.EditedPixelsSha256.Equals(right.EditedPixelsSha256, StringComparison.OrdinalIgnoreCase) || !left.EditedEncoded.AsSpan().SequenceEqual(right.EditedEncoded)) throw new InvalidDataException($"ADT alpha-brush edit {right.CellX},{right.CellY}:{right.Slot} has a changed offset, encoding, preimage, or postimage.");
        }
        return inspection;
    }

    private static void ValidateBrush(int layerSlot, float centerX, float centerY, float radius, float opacity, AdtTerrainBrushFalloff falloff)
    {
        if (layerSlot <= 0) throw new ArgumentOutOfRangeException(nameof(layerSlot), "Alpha painting requires an additional texture layer slot greater than zero; slot 0 is the opaque base layer.");
        if (!float.IsFinite(centerX) || !float.IsFinite(centerY) || centerX < 0 || centerX > 16 || centerY < 0 || centerY > 16) throw new ArgumentOutOfRangeException(nameof(centerX), "Alpha-brush center must be finite and inside the tile-local 0..16 grid.");
        if (!float.IsFinite(radius) || radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius), "Alpha-brush radius must be finite and positive.");
        if (!float.IsFinite(opacity) || opacity <= 0 || opacity > 1) throw new ArgumentOutOfRangeException(nameof(opacity), "Alpha-brush opacity must be greater than zero and at most one.");
        if (!Enum.IsDefined(falloff)) throw new ArgumentOutOfRangeException(nameof(falloff));
    }

    private static IReadOnlyList<AdtAlphaCell> NormalizeCells(IEnumerable<(int X, int Y)>? cells)
    {
        if (cells is null) return [];
        var result = cells.Select(cell => new AdtAlphaCell(cell.X, cell.Y)).Distinct().OrderBy(cell => cell.Y).ThenBy(cell => cell.X).ToArray();
        foreach (var cell in result) if (cell.X is < 0 or >= 16 || cell.Y is < 0 or >= 16) throw new ArgumentOutOfRangeException(nameof(cells), $"ADT cell {cell.X},{cell.Y} is outside the 16×16 grid.");
        return result;
    }

    private static IReadOnlyList<AdtAlphaCell> NormalizeCells(IEnumerable<AdtAlphaCell>? cells)
    {
        if (cells is null) return [];
        var result = cells.Distinct().OrderBy(cell => cell.Y).ThenBy(cell => cell.X).ToArray();
        foreach (var cell in result) if (cell.X is < 0 or >= 16 || cell.Y is < 0 or >= 16) throw new InvalidDataException($"ADT alpha-brush cell {cell.X},{cell.Y} is outside the 16×16 grid.");
        return result;
    }

    private static float Falloff(float normalizedDistance, AdtTerrainBrushFalloff falloff)
    {
        var remaining = Math.Clamp(1 - normalizedDistance, 0, 1); return falloff switch { AdtTerrainBrushFalloff.Constant => 1, AdtTerrainBrushFalloff.Linear => remaining, AdtTerrainBrushFalloff.Smooth => remaining * remaining * (3 - 2 * remaining), _ => throw new ArgumentOutOfRangeException(nameof(falloff)) };
    }

    private static AdtAlphaMapSummary WithStatistics(AdtAlphaMapSummary map, byte[] pixels)
        => map with { Minimum = pixels.Min(), Maximum = pixels.Max(), Average = pixels.Average(value => (double)value) };
    private static byte[] ReadPixels(byte[] bytes, AdtAlphaMapSummary map)
    {
        Require(bytes, map.DataOffset, map.Capacity, $"MCAL map {map.CellX},{map.CellY}:{map.Slot}"); return AdtAlphaMapCodec.Decode(map.Encoding, bytes.AsSpan(checked((int)map.DataOffset), map.Capacity));
    }
    private static void ValidateNested(byte[] bytes, int offset, int chunkEnd, string expected)
    {
        Require(bytes, offset, 8, $"nested {expected} header"); var actual = Decode(bytes.AsSpan(offset, 4)); var size = ReadU32(bytes, offset + 4);
        if (actual != expected || offset + 8L + size > chunkEnd) throw new InvalidDataException($"Expected bounded nested {expected} at byte {offset:N0}, found {actual}.");
    }
    private static uint ReadU32(byte[] bytes, int offset) { Require(bytes, offset, 4, "32-bit value"); return BitConverter.ToUInt32(bytes, offset); }
    private static string Decode(ReadOnlySpan<byte> raw) => new string(Encoding.ASCII.GetString(raw).Reverse().ToArray());
    private static string Sha256(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Require(byte[] bytes, long offset, long length, string label) { if (offset < 0 || length < 0 || offset + length > bytes.LongLength) throw new InvalidDataException($"{label} at byte {offset:N0} extends beyond the file."); }
    private static void AtomicWrite(string path, byte[] bytes, bool overwrite)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Output path has no parent directory."); Directory.CreateDirectory(directory); var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try { using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); } File.Move(temporary, path, overwrite); } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
