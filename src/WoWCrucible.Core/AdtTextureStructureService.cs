using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public enum AdtNewLayerEncoding { Auto, Packed4Bit, Big8Bit, Rle8Bit }

public sealed record AdtTextureStructureCell(int X, int Y, int OriginalLayers, int NewLayerSlot, string OriginalMcnkSha256);

public sealed record AdtTextureStructurePlan(
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    string InputPath,
    string InputSha256,
    string TexturePath,
    uint TextureId,
    AdtAlphaEncoding Encoding,
    byte InitialAlpha,
    IReadOnlyList<AdtTextureStructureCell> Cells);

public sealed record AdtTextureStructureResult(
    string OutputPath,
    string OutputSha256,
    string ReceiptPath,
    MapAssetInspection MapInspection,
    AdtTextureLayerInspection TextureInspection,
    AdtAlphaMapInspection AlphaInspection,
    uint TextureId,
    int EditedCells);

public static class AdtTextureStructureService
{
    private sealed record TopChunk(string Id, int OriginalOffset, byte[] Bytes);
    private const int FormatVersion = 1;
    private static readonly int[] McnkOffsetFields = [0x14, 0x18, 0x1C, 0x20, 0x24, 0x2C, 0x58, 0x60, 0x74];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public static AdtTextureStructurePlan Plan(string inputPath, string texturePath, IEnumerable<(int X, int Y)> cells,
        AdtNewLayerEncoding encoding = AdtNewLayerEncoding.Auto, byte initialAlpha = 0)
    {
        inputPath = Path.GetFullPath(inputPath); texturePath = NormalizeTexturePath(texturePath); var map = MapAssetInspectionService.Inspect(inputPath);
        if (map.Kind != MapAssetKind.Adt || map.Version != 18) throw new InvalidDataException("Structural texture insertion requires a validated WotLK MVER 18 ADT.");
        var textures = AdtTextureLayerService.Inspect(inputPath);
        if (textures.Textures.Any(texture => texture.Path.Equals(texturePath, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException($"MTEX already contains '{texturePath}'. Reassign that existing texture ID or paint its existing layer instead of creating a duplicate catalog entry.");
        var selected = cells.Select(cell => new AdtTextureStructureCell(cell.X, cell.Y, 0, 0, string.Empty)).DistinctBy(cell => (cell.X, cell.Y)).OrderBy(cell => cell.Y).ThenBy(cell => cell.X).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("Select at least one ADT cell for the new texture layer.");
        var chunks = ParseTopChunks(File.ReadAllBytes(inputPath)); ValidateStructuralScaffold(chunks); var byCoordinate = McnkByCoordinate(chunks); var planned = new List<AdtTextureStructureCell>();
        foreach (var cell in selected)
        {
            if (cell.X is < 0 or >= 16 || cell.Y is < 0 or >= 16) throw new ArgumentOutOfRangeException(nameof(cells), $"ADT cell {cell.X},{cell.Y} is outside the 16×16 grid.");
            if (!byCoordinate.TryGetValue((cell.X, cell.Y), out var chunk)) throw new InvalidDataException($"ADT has no MCNK for selected cell {cell.X},{cell.Y}.");
            var count = checked((int)ReadU32(chunk.Bytes, 8 + 0x0C)); if (count is < 1 or >= 4) throw new InvalidOperationException($"ADT cell {cell.X},{cell.Y} has {count:N0} texture layers. Structural insertion requires one through three existing layers; Wrath supports at most four.");
            ValidateCellChunks(chunk.Bytes, cell.X, cell.Y); planned.Add(new(cell.X, cell.Y, count, count, Hash(chunk.Bytes)));
        }
        var resolvedEncoding = ResolveEncoding(encoding, AdtAlphaMapService.Inspect(inputPath));
        return new(FormatVersion, DateTimeOffset.UtcNow, inputPath, Sha256(inputPath), texturePath, checked((uint)textures.Textures.Count), resolvedEncoding, initialAlpha, planned);
    }

    public static void SavePlan(AdtTextureStructurePlan plan, string path, bool overwrite = false)
    {
        _ = ValidatePlan(plan); path = Path.GetFullPath(path); if (File.Exists(path) && !overwrite) throw new IOException($"Structural texture plan already exists: {path}"); AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(plan, JsonOptions), overwrite);
    }

    public static AdtTextureStructurePlan LoadPlan(string path)
    {
        path = Path.GetFullPath(path); if (!File.Exists(path)) throw new FileNotFoundException("The structural ADT texture plan does not exist.", path); var plan = JsonSerializer.Deserialize<AdtTextureStructurePlan>(File.ReadAllBytes(path), JsonOptions) ?? throw new InvalidDataException("The structural ADT texture plan is empty."); _ = ValidatePlan(plan); return plan;
    }

    public static AdtTextureStructureResult Apply(AdtTextureStructurePlan plan, string outputPath, bool overwrite = false)
    {
        _ = ValidatePlan(plan); outputPath = Path.GetFullPath(outputPath); if (outputPath.Equals(Path.GetFullPath(plan.InputPath), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Crucible does not overwrite the source ADT; choose a separate structurally edited output path."); if (File.Exists(outputPath) && !overwrite) throw new IOException($"Output ADT already exists: {outputPath}");
        var chunks = ParseTopChunks(File.ReadAllBytes(plan.InputPath)); var selected = plan.Cells.ToDictionary(cell => (cell.X, cell.Y));
        for (var index = 0; index < chunks.Count; index++)
        {
            var chunk = chunks[index];
            if (chunk.Id == "MTEX") chunks[index] = chunk with { Bytes = AppendTexture(chunk.Bytes, plan.TexturePath) };
            else if (chunk.Id == "MCNK")
            {
                var coordinate = Coordinate(chunk.Bytes); if (selected.TryGetValue(coordinate, out var cell)) chunks[index] = chunk with { Bytes = AddLayer(chunk.Bytes, cell, plan.TextureId, plan.Encoding, plan.InitialAlpha) };
            }
        }
        RewriteTopLevelReferences(chunks); var outputBytes = Concatenate(chunks); AtomicWrite(outputPath, outputBytes, overwrite);
        var map = MapAssetInspectionService.Inspect(outputPath); var textures = AdtTextureLayerService.Inspect(outputPath); var alpha = AdtAlphaMapService.Inspect(outputPath);
        if (textures.Textures.Count != plan.TextureId + 1 || !textures.Textures[(int)plan.TextureId].Path.Equals(plan.TexturePath, StringComparison.Ordinal)) throw new InvalidDataException("Written ADT did not re-parse with the planned appended MTEX entry.");
        var expectedAlpha = StoredInitialAlpha(plan.Encoding, plan.InitialAlpha);
        foreach (var cell in plan.Cells)
        {
            var layer = textures.Layers.Single(value => value.CellX == cell.X && value.CellY == cell.Y && value.Slot == cell.NewLayerSlot); if (layer.TextureId != plan.TextureId || layer.TexturePath != plan.TexturePath) throw new InvalidDataException($"Written ADT cell {cell.X},{cell.Y} did not re-parse with the planned MCLY layer.");
            var alphaMap = alpha.Maps.Single(value => value.CellX == cell.X && value.CellY == cell.Y && value.Slot == cell.NewLayerSlot); if (alphaMap.Encoding != plan.Encoding || alphaMap.Minimum != expectedAlpha || alphaMap.Maximum != expectedAlpha) throw new InvalidDataException($"Written ADT cell {cell.X},{cell.Y} did not re-parse with the planned {plan.Encoding} alpha map.");
        }
        var outputHash = map.Path == outputPath ? Sha256(outputPath) : throw new InvalidDataException("Written map inspection resolved an unexpected output path."); var receiptPath = outputPath + ".crucible-map-structure.json"; var receipt = new { FormatVersion, AppliedUtc = DateTimeOffset.UtcNow, plan.InputPath, plan.InputSha256, OutputPath = outputPath, OutputSha256 = outputHash, plan.TexturePath, plan.TextureId, plan.Encoding, plan.InitialAlpha, plan.Cells }; AtomicWrite(receiptPath, JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions), overwrite: true);
        return new(outputPath, outputHash, receiptPath, map, textures, alpha, plan.TextureId, plan.Cells.Count);
    }

    private static AdtTextureStructurePlan ValidatePlan(AdtTextureStructurePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan); if (plan.FormatVersion != FormatVersion || plan.Cells.Count == 0 || plan.Cells.Select(cell => (cell.X, cell.Y)).Distinct().Count() != plan.Cells.Count || !Enum.IsDefined(plan.Encoding)) throw new InvalidDataException("Structural ADT texture plan has an unsupported format, encoding, or duplicate/empty cell list.");
        var rebuilt = Plan(plan.InputPath, plan.TexturePath, plan.Cells.Select(cell => (cell.X, cell.Y)), plan.Encoding switch { AdtAlphaEncoding.Packed4Bit => AdtNewLayerEncoding.Packed4Bit, AdtAlphaEncoding.Big8Bit => AdtNewLayerEncoding.Big8Bit, AdtAlphaEncoding.Rle8Bit => AdtNewLayerEncoding.Rle8Bit, _ => throw new InvalidDataException("Unsupported planned alpha encoding.") }, plan.InitialAlpha);
        if (!rebuilt.InputSha256.Equals(plan.InputSha256, StringComparison.OrdinalIgnoreCase) || rebuilt.TextureId != plan.TextureId || rebuilt.Encoding != plan.Encoding || rebuilt.Cells.Count != plan.Cells.Count) throw new InvalidDataException("Structural ADT texture plan no longer matches its source identity or catalog allocation.");
        for (var index = 0; index < rebuilt.Cells.Count; index++) if (rebuilt.Cells[index] != plan.Cells[index]) throw new InvalidDataException($"Structural ADT texture plan cell {plan.Cells[index].X},{plan.Cells[index].Y} has a changed layer count or MCNK preimage.");
        return rebuilt;
    }

    private static byte[] AddLayer(byte[] original, AdtTextureStructureCell cell, uint textureId, AdtAlphaEncoding encoding, byte initialAlpha)
    {
        if (!Hash(original).Equals(cell.OriginalMcnkSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"ADT cell {cell.X},{cell.Y} no longer matches its planned MCNK preimage."); var bytes = original.ToArray(); var header = 8; var count = checked((int)ReadU32(bytes, header + 0x0C)); if (count != cell.OriginalLayers || cell.NewLayerSlot != count) throw new InvalidDataException($"ADT cell {cell.X},{cell.Y} layer count changed after planning."); ValidateCellChunks(bytes, cell.X, cell.Y);
        var mcly = checked((int)ReadU32(bytes, header + 0x1C)); var mcal = checked((int)ReadU32(bytes, header + 0x24)); var mclySize = checked((int)ReadU32(bytes, mcly + 4)); var mcalSize = checked((int)ReadU32(bytes, mcal + 4)); var alpha = AlphaBytes(encoding, initialAlpha); var flags = encoding == AdtAlphaEncoding.Rle8Bit ? 0x300u : 0x100u;
        var layer = new byte[16]; BitConverter.GetBytes(textureId).CopyTo(layer, 0); BitConverter.GetBytes(flags).CopyTo(layer, 4); BitConverter.GetBytes((uint)mcalSize).CopyTo(layer, 8);
        var layerInsertion = mcly + 8 + mclySize; bytes = Insert(bytes, layerInsertion, layer); ShiftMcnkOffsets(bytes, layerInsertion, layer.Length); WriteU32(bytes, mcly + 4, checked((uint)(mclySize + layer.Length))); WriteU32(bytes, header + 0x0C, checked((uint)(count + 1))); WriteU32(bytes, 4, checked((uint)(bytes.Length - 8)));
        mcal = checked((int)ReadU32(bytes, header + 0x24)); var alphaInsertion = mcal + 8 + mcalSize; bytes = Insert(bytes, alphaInsertion, alpha); ShiftMcnkOffsets(bytes, alphaInsertion, alpha.Length); WriteU32(bytes, mcal + 4, checked((uint)(mcalSize + alpha.Length))); WriteU32(bytes, header + 0x28, checked((uint)(mcalSize + alpha.Length + 8))); WriteU32(bytes, 4, checked((uint)(bytes.Length - 8))); return bytes;
    }

    private static void RewriteTopLevelReferences(IReadOnlyList<TopChunk> chunks)
    {
        var offsets = new int[chunks.Count]; var position = 0; for (var index = 0; index < chunks.Count; index++) { offsets[index] = position; position = checked(position + chunks[index].Bytes.Length); }
        var mhdrIndex = chunks.Select((chunk, index) => (chunk, index)).Single(value => value.chunk.Id == "MHDR").index; var mhdr = chunks[mhdrIndex].Bytes; var mhdrBase = offsets[mhdrIndex] + 8; var names = new[] { "MCIN", "MTEX", "MMDX", "MMID", "MWMO", "MWID", "MDDF", "MODF", "MFBO", "MH2O", "MTFX" };
        for (var field = 0; field < names.Length; field++)
        {
            var target = chunks.Select((chunk, index) => (chunk, index)).FirstOrDefault(value => value.chunk.Id == names[field]); var fieldOffset = 8 + 4 + field * 4; if (target.chunk is null) { if (field >= 8) WriteU32(mhdr, fieldOffset, 0); continue; } WriteU32(mhdr, fieldOffset, checked((uint)(offsets[target.index] - mhdrBase)));
        }
        var mcinIndex = chunks.Select((chunk, index) => (chunk, index)).Single(value => value.chunk.Id == "MCIN").index; var mcin = chunks[mcinIndex].Bytes; var originalMcnk = chunks.Select((chunk, index) => (chunk, index)).Where(value => value.chunk.Id == "MCNK").ToDictionary(value => value.chunk.OriginalOffset, value => value.index);
        for (var entry = 0; entry < 256; entry++)
        {
            var offset = 8 + entry * 16; var old = ReadU32(mcin, offset); if (old == 0) continue; if (!originalMcnk.TryGetValue(checked((int)old), out var index)) throw new InvalidDataException($"MCIN entry {entry} points to absent original MCNK byte {old:N0}."); WriteU32(mcin, offset, checked((uint)offsets[index])); WriteU32(mcin, offset + 4, checked((uint)chunks[index].Bytes.Length));
        }
    }

    private static void ShiftMcnkOffsets(byte[] bytes, int insertion, int amount)
    {
        foreach (var field in McnkOffsetFields) { var offset = 8 + field; var value = ReadU32(bytes, offset); if (value != 0 && value >= insertion) WriteU32(bytes, offset, checked(value + (uint)amount)); }
    }

    private static byte[] AppendTexture(byte[] chunk, string path)
    {
        var payload = chunk.AsSpan(8).ToArray(); var text = Encoding.UTF8.GetBytes(path); var separator = payload.Length > 0 && payload[^1] != 0 ? 1 : 0; var result = new byte[8 + payload.Length + separator + text.Length + 1]; chunk.AsSpan(0, 8).CopyTo(result); payload.CopyTo(result, 8); text.CopyTo(result, 8 + payload.Length + separator); WriteU32(result, 4, checked((uint)(result.Length - 8))); return result;
    }

    private static byte[] AlphaBytes(AdtAlphaEncoding encoding, byte initialAlpha)
    {
        var pixels = Enumerable.Repeat(initialAlpha, AdtAlphaMapCodec.PixelCount).Select(value => (byte)value).ToArray(); return AdtAlphaMapCodec.Encode(encoding, pixels);
    }
    private static byte StoredInitialAlpha(AdtAlphaEncoding encoding, byte initialAlpha) => AdtAlphaMapCodec.Decode(encoding, AlphaBytes(encoding, initialAlpha))[0];
    private static AdtAlphaEncoding ResolveEncoding(AdtNewLayerEncoding requested, AdtAlphaMapInspection alpha)
    {
        if (requested != AdtNewLayerEncoding.Auto) return requested switch { AdtNewLayerEncoding.Packed4Bit => AdtAlphaEncoding.Packed4Bit, AdtNewLayerEncoding.Big8Bit => AdtAlphaEncoding.Big8Bit, AdtNewLayerEncoding.Rle8Bit => AdtAlphaEncoding.Rle8Bit, _ => throw new ArgumentOutOfRangeException(nameof(requested)) };
        var families = alpha.Maps.Select(map => map.Encoding == AdtAlphaEncoding.Packed4Bit ? "packed" : "8-bit").Distinct().ToArray(); if (families.Length == 0) throw new InvalidOperationException("This tile has no existing alpha maps from which to infer packed versus 8-bit storage. Select an encoding explicitly."); if (families.Length > 1) throw new InvalidOperationException("This tile mixes packed and 8-bit alpha families. Select the new layer encoding explicitly instead of relying on Auto.");
        return alpha.Maps.GroupBy(map => map.Encoding).OrderByDescending(group => group.Count()).ThenBy(group => group.Key).First().Key;
    }

    private static void ValidateStructuralScaffold(IReadOnlyList<TopChunk> chunks)
    {
        foreach (var required in new[] { "MHDR", "MCIN", "MTEX", "MCNK" }) if (!chunks.Any(chunk => chunk.Id == required)) throw new InvalidDataException($"Structural ADT editing requires a complete monolithic Wrath layout with {required}."); if (chunks.Count(chunk => chunk.Id == "MHDR") != 1 || chunks.Count(chunk => chunk.Id == "MCIN") != 1 || chunks.Count(chunk => chunk.Id == "MTEX") != 1) throw new InvalidDataException("Structural ADT editing requires exactly one MHDR, MCIN, and MTEX chunk."); var mcin = chunks.Single(chunk => chunk.Id == "MCIN"); if (mcin.Bytes.Length != 4104) throw new InvalidDataException("Structural ADT editing requires the complete 4,096-byte MCIN table.");
    }

    private static void ValidateCellChunks(byte[] bytes, int x, int y)
    {
        var header = 8; foreach (var pair in new[] { (Field: 0x1C, Id: "MCLY"), (Field: 0x24, Id: "MCAL") }) { var offset = checked((int)ReadU32(bytes, header + pair.Field)); if (offset < 136 || offset + 8 > bytes.Length || Decode(bytes.AsSpan(offset, 4)) != pair.Id || offset + 8L + ReadU32(bytes, offset + 4) > bytes.Length) throw new InvalidDataException($"ADT cell {x},{y} has no bounded nested {pair.Id} chunk."); }
    }

    private static Dictionary<(int X, int Y), TopChunk> McnkByCoordinate(IEnumerable<TopChunk> chunks)
    {
        var result = new Dictionary<(int, int), TopChunk>(); foreach (var chunk in chunks.Where(chunk => chunk.Id == "MCNK")) { var coordinate = Coordinate(chunk.Bytes); if (!result.TryAdd(coordinate, chunk)) throw new InvalidDataException($"ADT contains duplicate MCNK coordinate {coordinate.X},{coordinate.Y}."); } return result;
    }
    private static (int X, int Y) Coordinate(byte[] mcnk) => (checked((int)ReadU32(mcnk, 8 + 4)), checked((int)ReadU32(mcnk, 8 + 8)));

    private static List<TopChunk> ParseTopChunks(byte[] bytes)
    {
        var result = new List<TopChunk>(); var position = 0; while (position < bytes.Length) { Require(bytes, position, 8, "top-level ADT chunk"); var id = Decode(bytes.AsSpan(position, 4)); var size = ReadU32(bytes, position + 4); var length = checked((int)(size + 8)); Require(bytes, position, length, $"ADT chunk {id}"); result.Add(new(id, position, bytes.AsSpan(position, length).ToArray())); position += length; } return result;
    }
    private static byte[] Concatenate(IEnumerable<TopChunk> chunks) { using var stream = new MemoryStream(); foreach (var chunk in chunks) stream.Write(chunk.Bytes); return stream.ToArray(); }
    private static byte[] Insert(byte[] source, int offset, byte[] addition) { if (offset < 0 || offset > source.Length) throw new ArgumentOutOfRangeException(nameof(offset)); var result = new byte[checked(source.Length + addition.Length)]; source.AsSpan(0, offset).CopyTo(result); addition.CopyTo(result, offset); source.AsSpan(offset).CopyTo(result.AsSpan(offset + addition.Length)); return result; }
    private static string NormalizeTexturePath(string path) { path = (path ?? string.Empty).Trim().Replace('/', '\\').TrimStart('\\'); if (path.Length == 0 || path.IndexOf('\0') >= 0 || !Path.GetExtension(path).Equals(".blp", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("New MTEX path must be a non-empty client-relative .blp path.", nameof(path)); return path; }
    private static uint ReadU32(byte[] bytes, int offset) { Require(bytes, offset, 4, "32-bit value"); return BitConverter.ToUInt32(bytes, offset); }
    private static void WriteU32(byte[] bytes, int offset, uint value) { Require(bytes, offset, 4, "32-bit value"); BitConverter.GetBytes(value).CopyTo(bytes, offset); }
    private static string Decode(ReadOnlySpan<byte> raw) => new string(Encoding.ASCII.GetString(raw).Reverse().ToArray());
    private static string Sha256(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Require(byte[] bytes, long offset, long length, string label) { if (offset < 0 || length < 0 || offset + length > bytes.LongLength) throw new InvalidDataException($"{label} at byte {offset:N0} extends beyond the file."); }
    private static void AtomicWrite(string path, byte[] bytes, bool overwrite)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Output path has no parent directory."); Directory.CreateDirectory(directory); var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp"); try { using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); } File.Move(temporary, path, overwrite); } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
