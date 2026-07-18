using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public sealed record AdtTextureCatalogEntry(uint Id, string Path);
public sealed record AdtTextureLayer(int CellX, int CellY, int Slot, long TextureIdOffset, uint TextureId, string? TexturePath, uint Flags, uint AlphaOffset, int EffectId);
public sealed record AdtTextureLayerInspection(string Path, string Sha256, IReadOnlyList<AdtTextureCatalogEntry> Textures, IReadOnlyList<AdtTextureLayer> Layers, IReadOnlyList<string> Findings);
public sealed record AdtTextureLayerEdit(int CellX, int CellY, int Slot, long TextureIdOffset, uint OriginalTextureId, uint EditedTextureId);
public sealed record AdtTextureLayerPlan(int FormatVersion, DateTimeOffset CreatedUtc, string InputPath, string InputSha256, int LayerSlot, uint TextureId, string TexturePath, IReadOnlyList<AdtTextureLayerEdit> Edits);
public sealed record AdtTextureLayerResult(string OutputPath, string OutputSha256, string ReceiptPath, AdtTextureLayerInspection Inspection, int EditedLayers, int EditedCells);

public static class AdtTextureLayerService
{
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AdtTextureLayerInspection Inspect(string path)
    {
        path = Path.GetFullPath(path); var map = MapAssetInspectionService.Inspect(path); if (map.Kind != MapAssetKind.Adt || map.Version != 18) throw new InvalidDataException("Texture-layer inspection requires a validated WotLK MVER 18 ADT.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, FileOptions.RandomAccess); var header = new byte[8]; byte[]? mtex = null; var cells = new List<(long ChunkOffset, long End, byte[] Header)>();
        while (stream.Position < stream.Length)
        {
            var chunkOffset = stream.Position; if (stream.Read(header) != 8) throw new InvalidDataException($"ADT chunk header is truncated at byte {chunkOffset:N0}."); var id = Decode(header.AsSpan(0, 4)); var size = BitConverter.ToUInt32(header, 4); var payload = stream.Position; var end = payload + size;
            if (size > int.MaxValue || end > stream.Length) throw new InvalidDataException($"ADT chunk {id} at byte {chunkOffset:N0} extends beyond the file.");
            if (id == "MTEX") { if (mtex is not null) throw new InvalidDataException("ADT contains more than one MTEX texture catalog."); mtex = new byte[checked((int)size)]; stream.ReadExactly(mtex); }
            else if (id == "MCNK") { if (size < 128) throw new InvalidDataException($"ADT MCNK at byte {chunkOffset:N0} is shorter than its 128-byte header."); var data = new byte[128]; stream.ReadExactly(data); cells.Add((chunkOffset, end, data)); }
            stream.Position = end;
        }
        if (mtex is null) throw new InvalidDataException("ADT has no MTEX terrain-texture catalog."); var textures = DecodeStrings(mtex).Select((value, index) => new AdtTextureCatalogEntry((uint)index, value)).ToArray(); if (textures.Length == 0) throw new InvalidDataException("ADT MTEX terrain-texture catalog is empty.");
        var layers = new List<AdtTextureLayer>(); var findings = textures.Where(texture => texture.Path.Length == 0).Select(texture => $"MTEX index {texture.Id} is empty.").ToList(); var coordinates = new HashSet<(int, int)>();
        foreach (var cell in cells)
        {
            var x = BitConverter.ToUInt32(cell.Header, 4); var y = BitConverter.ToUInt32(cell.Header, 8); var count = BitConverter.ToUInt32(cell.Header, 0x0C); var relativeOffset = BitConverter.ToUInt32(cell.Header, 0x1C);
            if (x >= 16 || y >= 16 || !coordinates.Add(((int)x, (int)y))) throw new InvalidDataException($"ADT MCNK at byte {cell.ChunkOffset:N0} has invalid or duplicate coordinate {x},{y}."); if (count == 0) continue;
            if (count > 64 || relativeOffset == 0) throw new InvalidDataException($"ADT MCNK {x},{y} declares {count:N0} layers without a bounded MCLY offset."); var nested = cell.ChunkOffset + relativeOffset;
            if (nested < cell.ChunkOffset + 8 || nested + 8L + count * 16L > cell.End) throw new InvalidDataException($"ADT MCNK {x},{y} points outside its chunk for MCLY."); stream.Position = nested; stream.ReadExactly(header); var nestedId = Decode(header.AsSpan(0, 4)); var nestedSize = BitConverter.ToUInt32(header, 4);
            if (nestedId != "MCLY" || nestedSize < count * 16 || stream.Position + nestedSize > cell.End) throw new InvalidDataException($"ADT MCNK {x},{y} does not point to a complete MCLY layer table."); var bytes = new byte[checked((int)(count * 16))]; stream.ReadExactly(bytes);
            for (var slot = 0; slot < count; slot++)
            {
                var offset = slot * 16; var textureId = BitConverter.ToUInt32(bytes, offset); var texture = textureId < textures.Length ? textures[textureId].Path : null; if (texture is null) findings.Add($"MCNK {x},{y} layer {slot} references missing MTEX index {textureId}.");
                layers.Add(new((int)x, (int)y, slot, nested + 8 + offset, textureId, texture, BitConverter.ToUInt32(bytes, offset + 4), BitConverter.ToUInt32(bytes, offset + 8), BitConverter.ToInt32(bytes, offset + 12)));
            }
        }
        return new(path, Sha256(path), textures, layers, findings.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static AdtTextureLayerPlan Plan(string inputPath, IEnumerable<(int X, int Y)> cells, int layerSlot, uint textureId)
    {
        if (layerSlot < 0) throw new ArgumentOutOfRangeException(nameof(layerSlot), "Layer slot must be non-negative."); var inspection = Inspect(inputPath); if (textureId >= inspection.Textures.Count) throw new ArgumentOutOfRangeException(nameof(textureId), $"Texture ID {textureId} is outside the MTEX catalog (0–{inspection.Textures.Count - 1}).");
        var selected = cells.Distinct().OrderBy(cell => cell.Y).ThenBy(cell => cell.X).ToArray(); if (selected.Length == 0) throw new InvalidOperationException("Select at least one ADT terrain cell."); var edits = new List<AdtTextureLayerEdit>();
        foreach (var coordinate in selected)
        {
            if (coordinate.X is < 0 or >= 16 || coordinate.Y is < 0 or >= 16) throw new ArgumentOutOfRangeException(nameof(cells), $"ADT cell {coordinate.X},{coordinate.Y} is outside the 16×16 grid."); var layer = inspection.Layers.FirstOrDefault(value => value.CellX == coordinate.X && value.CellY == coordinate.Y && value.Slot == layerSlot) ?? throw new InvalidDataException($"ADT cell {coordinate.X},{coordinate.Y} has no texture layer slot {layerSlot}.");
            if (layer.TextureId != textureId) edits.Add(new(layer.CellX, layer.CellY, layer.Slot, layer.TextureIdOffset, layer.TextureId, textureId));
        }
        if (edits.Count == 0) throw new InvalidOperationException("Every selected layer already uses that MTEX texture; no edit is required."); var texture = inspection.Textures[(int)textureId]; return new(FormatVersion, DateTimeOffset.UtcNow, inspection.Path, inspection.Sha256, layerSlot, textureId, texture.Path, edits);
    }

    public static AdtTextureLayerInspection Preview(AdtTextureLayerPlan plan)
    {
        var inspection = ValidatePlan(plan); var edits = plan.Edits.ToDictionary(edit => edit.TextureIdOffset); return inspection with { Layers = inspection.Layers.Select(layer => edits.ContainsKey(layer.TextureIdOffset) ? layer with { TextureId = plan.TextureId, TexturePath = plan.TexturePath } : layer).ToArray() };
    }

    public static void SavePlan(AdtTextureLayerPlan plan, string path, bool overwrite = false)
    {
        _ = ValidatePlan(plan); path = Path.GetFullPath(path); if (File.Exists(path) && !overwrite) throw new IOException($"Texture-layer plan already exists: {path}"); AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(plan, JsonOptions), overwrite);
    }

    public static AdtTextureLayerPlan LoadPlan(string path)
    {
        path = Path.GetFullPath(path); if (!File.Exists(path)) throw new FileNotFoundException("The ADT texture-layer plan does not exist.", path); var plan = JsonSerializer.Deserialize<AdtTextureLayerPlan>(File.ReadAllBytes(path), JsonOptions) ?? throw new InvalidDataException("The ADT texture-layer plan is empty."); _ = ValidatePlan(plan); return plan;
    }

    public static AdtTextureLayerResult Apply(AdtTextureLayerPlan plan, string outputPath, bool overwrite = false)
    {
        _ = ValidatePlan(plan); outputPath = Path.GetFullPath(outputPath); if (outputPath.Equals(Path.GetFullPath(plan.InputPath), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Crucible does not overwrite the source ADT; choose a separate output path so the texture edit remains reversible."); if (File.Exists(outputPath) && !overwrite) throw new IOException($"Output ADT already exists: {outputPath}"); var source = File.ReadAllBytes(plan.InputPath);
        foreach (var edit in plan.Edits) { if (edit.TextureIdOffset < 0 || edit.TextureIdOffset + 4 > source.LongLength || BitConverter.ToUInt32(source, checked((int)edit.TextureIdOffset)) != edit.OriginalTextureId) throw new InvalidDataException($"ADT cell {edit.CellX},{edit.CellY} layer {edit.Slot} no longer matches its planned byte preimage."); BitConverter.GetBytes(edit.EditedTextureId).CopyTo(source, checked((int)edit.TextureIdOffset)); }
        AtomicWrite(outputPath, source, overwrite); var inspection = Inspect(outputPath); foreach (var edit in plan.Edits) { var layer = inspection.Layers.Single(value => value.CellX == edit.CellX && value.CellY == edit.CellY && value.Slot == edit.Slot); if (layer.TextureId != plan.TextureId || !string.Equals(layer.TexturePath, plan.TexturePath, StringComparison.Ordinal)) throw new InvalidDataException($"Written ADT cell {edit.CellX},{edit.CellY} layer {edit.Slot} did not re-parse to MTEX {plan.TextureId}."); }
        var hash = inspection.Sha256; var receiptPath = outputPath + ".crucible-map-texture.json"; var receipt = new { FormatVersion, AppliedUtc = DateTimeOffset.UtcNow, plan.InputPath, plan.InputSha256, OutputPath = outputPath, OutputSha256 = hash, plan.LayerSlot, plan.TextureId, plan.TexturePath, plan.Edits }; AtomicWrite(receiptPath, JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions), overwrite: true);
        return new(outputPath, hash, receiptPath, inspection, plan.Edits.Count, plan.Edits.Select(edit => (edit.CellX, edit.CellY)).Distinct().Count());
    }

    private static AdtTextureLayerInspection ValidatePlan(AdtTextureLayerPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan); if (plan.FormatVersion != FormatVersion || plan.LayerSlot < 0 || plan.Edits.Count == 0 || plan.Edits.Select(edit => edit.TextureIdOffset).Distinct().Count() != plan.Edits.Count) throw new InvalidDataException("ADT texture-layer plan has an unsupported format, invalid slot, or duplicate/empty edits."); var inspection = Inspect(plan.InputPath); if (!inspection.Sha256.Equals(plan.InputSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Source ADT hash no longer matches the texture-layer plan; rebuild the plan before applying it.");
        if (plan.TextureId >= inspection.Textures.Count || !string.Equals(inspection.Textures[(int)plan.TextureId].Path, plan.TexturePath, StringComparison.Ordinal)) throw new InvalidDataException("Texture-layer plan MTEX identity no longer matches the source catalog.");
        foreach (var edit in plan.Edits) { var layer = inspection.Layers.FirstOrDefault(value => value.CellX == edit.CellX && value.CellY == edit.CellY && value.Slot == edit.Slot) ?? throw new InvalidDataException($"Texture-layer plan references absent cell/layer {edit.CellX},{edit.CellY}:{edit.Slot}."); if (edit.Slot != plan.LayerSlot || edit.TextureIdOffset != layer.TextureIdOffset || edit.OriginalTextureId != layer.TextureId || edit.EditedTextureId != plan.TextureId || edit.OriginalTextureId == edit.EditedTextureId) throw new InvalidDataException($"Texture-layer plan edit {edit.CellX},{edit.CellY}:{edit.Slot} has a changed offset, preimage, or postimage."); }
        return inspection;
    }

    private static IReadOnlyList<string> DecodeStrings(byte[] data)
    {
        var result = new List<string>(); var start = 0; for (var index = 0; index < data.Length; index++) if (data[index] == 0) { result.Add(Encoding.UTF8.GetString(data, start, index - start)); start = index + 1; } if (start < data.Length) result.Add(Encoding.UTF8.GetString(data, start, data.Length - start)); while (result.Count > 0 && result[^1].Length == 0) result.RemoveAt(result.Count - 1); return result;
    }
    private static string Decode(ReadOnlySpan<byte> raw) => new string(Encoding.ASCII.GetString(raw).Reverse().ToArray());
    private static string Sha256(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void AtomicWrite(string path, byte[] bytes, bool overwrite)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Output path has no parent directory."); Directory.CreateDirectory(directory); var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp"); try { using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); } File.Move(temporary, path, overwrite); } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
