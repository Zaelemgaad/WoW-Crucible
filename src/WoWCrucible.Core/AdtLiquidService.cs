using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoWCrucible.Core;

public enum AdtLiquidVertexFormat : ushort { HeightDepth = 0, HeightTextureCoordinates = 1, DepthOnly = 2 }
public enum AdtLiquidHeightMode { Keep, Shift, SetFlat }

public sealed record LiquidTypeRecord(uint Id, string Name, uint Flags, uint Category, uint SoundId, uint SpellId,
    float MaxDarkenDepth, uint LightId, uint MaterialId, IReadOnlyList<string> Textures, [property: JsonIgnore] string SearchText)
{
    public string Summary => $"Liquid {Id:N0} · {Name} · {CategoryName(Category)} · sound {SoundId:N0} · spell {SpellId:N0}";
    private static string CategoryName(uint value) => value switch { 0 => "water", 1 => "ocean", 2 => "magma", 3 => "slime", _ => $"category {value:N0}" };
}

public sealed record LiquidTypeCatalog(string DbcDirectory, string Sha256, IReadOnlyList<LiquidTypeRecord> Types, IReadOnlyList<string> Findings);

public static class LiquidTypeCatalogService
{
    public static LiquidTypeCatalog Load(string dbcDirectory)
    {
        var root = Path.GetFullPath(dbcDirectory); if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"DBC directory not found: {root}");
        var path = Path.Combine(root, "LiquidType.dbc"); if (!File.Exists(path)) throw new FileNotFoundException("Required build-12340 LiquidType.dbc was not found.", path); var file = WdbcFile.Load(path);
        if (file.ContainerKind != ClientTableContainerKind.Wdbc || file.FieldCount != 45 || file.RecordSize != 180) throw new InvalidDataException($"LiquidType.dbc is not the expected build-12340 WDBC layout (45 fields / 180 bytes); found {file.ContainerKind}, {file.FieldCount} fields / {file.RecordSize} bytes.");
        var result = new List<LiquidTypeRecord>(file.RowCount); var ids = new HashSet<uint>(); var findings = new List<string>();
        for (var row = 0; row < file.RowCount; row++)
        {
            var id = Raw(file, row, 0); if (!ids.Add(id)) throw new InvalidDataException($"LiquidType.dbc contains duplicate ID {id:N0}."); if (id > ushort.MaxValue) findings.Add($"LiquidType {id:N0} exceeds the unsigned 16-bit MH2O liquid-type field and cannot be assigned.");
            var name = file.GetString(Raw(file, row, 1)); var textures = Enumerable.Range(0, 6).Select(index => file.GetString(Raw(file, row, 15 + index))).Where(value => value.Length > 0).ToArray();
            var flags = Raw(file, row, 2); var category = Raw(file, row, 3); var sound = Raw(file, row, 4); var spell = Raw(file, row, 5); var maxDarken = BitConverter.Int32BitsToSingle(unchecked((int)Raw(file, row, 6))); var light = Raw(file, row, 10); var material = Raw(file, row, 14);
            var search = $"{id} {name} {flags} {category} {sound} {spell} {light} {material} {string.Join(' ', textures)}"; result.Add(new(id, name, flags, category, sound, spell, maxDarken, light, material, textures, search));
        }
        return new(root, Hash(path), result.OrderBy(value => value.Id).ToArray(), findings);
    }

    public static IReadOnlyList<LiquidTypeRecord> Search(LiquidTypeCatalog catalog, string? query, int limit = 256)
    {
        ArgumentNullException.ThrowIfNull(catalog); if (limit is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(limit), "Liquid-type result limit must be 1 through 10,000."); var text = query?.Trim() ?? string.Empty;
        if (text.Length == 0) return catalog.Types.Take(limit).ToArray(); var terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return catalog.Types.Where(value => terms.All(term => value.SearchText.Contains(term, StringComparison.OrdinalIgnoreCase))).Take(limit).ToArray();
    }

    public static LiquidTypeRecord Require(LiquidTypeCatalog catalog, ushort id) => catalog.Types.FirstOrDefault(value => value.Id == id) ?? throw new InvalidDataException($"LiquidType.dbc does not contain liquid ID {id:N0}.");
    private static uint Raw(WdbcFile file, int row, int field) => file.GetRaw(row, new(field, field * 4, 4, $"Field{field}", DbcValueType.Raw32));
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}

public sealed record AdtLiquidLayer(int CellX, int CellY, int Slot, long InstanceOffset, long LiquidTypeOffset, ushort LiquidType,
    AdtLiquidVertexFormat VertexFormat, long MinHeightOffset, float MinHeight, long MaxHeightOffset, float MaxHeight,
    byte OffsetX, byte OffsetY, byte Width, byte Height, ulong ExistsMask, int ActiveTiles, ulong FishableMask, ulong DeepMask,
    long? VertexDataOffset, IReadOnlyList<long> HeightOffsets, IReadOnlyList<float> Heights, int ActiveHeightVertices, int DepthValues, int TextureCoordinatePairs);
public sealed record AdtLiquidInspection(string Path, string Sha256, long ChunkOffset, int ChunkPayloadBytes,
    IReadOnlyList<AdtLiquidLayer> Layers, IReadOnlyList<string> Findings);
public sealed record AdtLiquidVertexHeightEdit(long Offset, float OriginalHeight, float EditedHeight);
public sealed record AdtLiquidLayerEdit(int CellX, int CellY, int Slot, long InstanceOffset, long LiquidTypeOffset,
    ushort OriginalLiquidType, ushort EditedLiquidType, long MinHeightOffset, float OriginalMinHeight, float EditedMinHeight,
    long MaxHeightOffset, float OriginalMaxHeight, float EditedMaxHeight, IReadOnlyList<AdtLiquidVertexHeightEdit> VertexHeights);
public sealed record AdtLiquidPlan(int FormatVersion, DateTimeOffset CreatedUtc, string InputPath, string InputSha256, int LayerSlot,
    ushort? TargetLiquidType, AdtLiquidHeightMode HeightMode, float HeightValue, string? CatalogEvidence, string? CatalogDbcDirectory,
    string? CatalogSha256, IReadOnlyList<AdtLiquidLayerEdit> Edits);
public sealed record AdtLiquidResult(string OutputPath, string OutputSha256, string ReceiptPath, AdtLiquidInspection Inspection,
    int EditedLayers, int EditedCells, int EditedVertexHeights);

public static class AdtLiquidService
{
    private const int FormatVersion = 1;
    private const int CellHeadersBytes = 16 * 16 * 12;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AdtLiquidInspection Inspect(string path)
    {
        path = Path.GetFullPath(path); var map = MapAssetInspectionService.Inspect(path); if (map.Kind != MapAssetKind.Adt || map.Version != 18) throw new InvalidDataException("Liquid inspection requires a validated WotLK MVER 18 ADT."); var bytes = File.ReadAllBytes(path); var chunks = ParseTopChunks(bytes); var matches = chunks.Where(chunk => chunk.Id == "MH2O").ToArray();
        if (matches.Length == 0) return new(path, Hash(path), -1, 0, [], ["ADT has no MH2O liquid chunk."]); if (matches.Length != 1) throw new InvalidDataException($"ADT contains {matches.Length:N0} MH2O chunks; exactly one is required."); var mh2o = matches[0]; if (mh2o.PayloadLength < CellHeadersBytes) throw new InvalidDataException($"ADT MH2O payload is shorter than its 256-cell header table ({mh2o.PayloadLength:N0}/{CellHeadersBytes:N0} bytes).");
        var layers = new List<AdtLiquidLayer>(); var findings = new List<string>(); var instanceOffsets = new HashSet<long>();
        for (var index = 0; index < 256; index++)
        {
            var cellX = index % 16; var cellY = index / 16; var cellHeader = mh2o.PayloadOffset + index * 12; var relativeInstances = ReadU32(bytes, cellHeader); var count = ReadU32(bytes, cellHeader + 4); var relativeAttributes = ReadU32(bytes, cellHeader + 8);
            if (count == 0) { if (relativeInstances != 0 || relativeAttributes != 0) findings.Add($"MH2O cell {cellX},{cellY} has no layers but retains instance/attribute offsets."); continue; }
            if (count > 64 || relativeInstances == 0) throw new InvalidDataException($"MH2O cell {cellX},{cellY} declares {count:N0} liquid layers without a bounded instance table."); var instances = Relative(mh2o, relativeInstances, checked((int)count * 24), $"MH2O cell {cellX},{cellY} instance table");
            ulong fishable = ulong.MaxValue, deep = ulong.MaxValue; if (relativeAttributes != 0) { var attributes = Relative(mh2o, relativeAttributes, 16, $"MH2O cell {cellX},{cellY} attributes"); fishable = ReadU64(bytes, attributes); deep = ReadU64(bytes, attributes + 8); }
            for (var slot = 0; slot < count; slot++)
            {
                var instance = instances + slot * 24L; if (!instanceOffsets.Add(instance)) findings.Add($"MH2O cell {cellX},{cellY} layer {slot} reuses an earlier liquid-instance record."); var liquidType = ReadU16(bytes, instance); var formatRaw = ReadU16(bytes, instance + 2); if (formatRaw > 2) throw new InvalidDataException($"MH2O cell {cellX},{cellY} layer {slot} uses unsupported liquid vertex format {formatRaw:N0}."); var format = (AdtLiquidVertexFormat)formatRaw;
                var minimum = ReadF32(bytes, instance + 4); var maximum = ReadF32(bytes, instance + 8); if (!float.IsFinite(minimum) || !float.IsFinite(maximum)) throw new InvalidDataException($"MH2O cell {cellX},{cellY} layer {slot} contains a non-finite height bound."); var offsetX = bytes[instance + 12]; var offsetY = bytes[instance + 13]; var width = bytes[instance + 14]; var height = bytes[instance + 15]; if (width == 0 || height == 0 || offsetX > 7 || offsetY > 7 || offsetX + width > 8 || offsetY + height > 8) throw new InvalidDataException($"MH2O cell {cellX},{cellY} layer {slot} has an invalid {width}×{height} rectangle at {offsetX},{offsetY}.");
                var relativeBitmap = ReadU32(bytes, instance + 16); var relativeVertices = ReadU32(bytes, instance + 20); var tileBits = width * height; ulong existsMask; if (relativeBitmap == 0) existsMask = tileBits == 64 ? ulong.MaxValue : (1UL << tileBits) - 1; else existsMask = ReadU64(bytes, Relative(mh2o, relativeBitmap, 8, $"MH2O cell {cellX},{cellY} layer {slot} exists bitmap")); var relevantMask = tileBits == 64 ? ulong.MaxValue : (1UL << tileBits) - 1; var active = System.Numerics.BitOperations.PopCount(existsMask & relevantMask);
                var vertexCount = checked((width + 1) * (height + 1)); var heightOffsets = new List<long>(); var heights = new List<float>(); var depthValues = 0; var texturePairs = 0; long? vertexData = null;
                if (relativeVertices != 0)
                {
                    var vertexBytes = format switch { AdtLiquidVertexFormat.HeightDepth => vertexCount * 5, AdtLiquidVertexFormat.HeightTextureCoordinates => vertexCount * 6, AdtLiquidVertexFormat.DepthOnly => vertexCount, _ => throw new UnreachableException() }; vertexData = Relative(mh2o, relativeVertices, vertexBytes, $"MH2O cell {cellX},{cellY} layer {slot} vertex data");
                    if (format is AdtLiquidVertexFormat.HeightDepth or AdtLiquidVertexFormat.HeightTextureCoordinates) for (var vertex = 0; vertex < vertexCount; vertex++) { var offset = vertexData.Value + vertex * 4L; var value = ReadF32(bytes, offset); if (!float.IsFinite(value)) throw new InvalidDataException($"MH2O cell {cellX},{cellY} layer {slot} vertex {vertex:N0} has a non-finite height."); heightOffsets.Add(offset); heights.Add(value); }
                    depthValues = format is AdtLiquidVertexFormat.HeightDepth or AdtLiquidVertexFormat.DepthOnly ? vertexCount : 0; texturePairs = format == AdtLiquidVertexFormat.HeightTextureCoordinates ? vertexCount : 0;
                }
                else if (format is AdtLiquidVertexFormat.HeightDepth or AdtLiquidVertexFormat.HeightTextureCoordinates) findings.Add($"MH2O cell {cellX},{cellY} layer {slot} declares a height-bearing format without vertex data; height authoring is unavailable for that layer.");
                var activeHeightVertices = ActiveVertexIndices(width, height, existsMask).ToArray(); if (heights.Count > 0 && activeHeightVertices.Length > 0) { var activeHeights = activeHeightVertices.Select(index => heights[index]).ToArray(); var actualMinimum = activeHeights.Min(); var actualMaximum = activeHeights.Max(); if (minimum > actualMinimum || maximum < actualMaximum) findings.Add($"MH2O cell {cellX},{cellY} layer {slot} declared bounds {minimum:0.###}..{maximum:0.###} do not contain active-vertex range {actualMinimum:0.###}..{actualMaximum:0.###}."); }
                layers.Add(new(cellX, cellY, slot, instance, instance, liquidType, format, instance + 4, minimum, instance + 8, maximum, offsetX, offsetY, width, height, existsMask, active, fishable, deep, vertexData, heightOffsets, heights, activeHeightVertices.Length, depthValues, texturePairs));
            }
        }
        return new(path, Hash(path), mh2o.HeaderOffset, mh2o.PayloadLength, layers, findings.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static AdtLiquidPlan Plan(string inputPath, IEnumerable<(int X, int Y)> cells, int layerSlot, ushort? targetLiquidType,
        AdtLiquidHeightMode heightMode, float heightValue, LiquidTypeCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(cells); if (layerSlot < 0) throw new ArgumentOutOfRangeException(nameof(layerSlot), "Liquid layer slot must be non-negative."); if (!Enum.IsDefined(heightMode) || !float.IsFinite(heightValue)) throw new ArgumentOutOfRangeException(nameof(heightValue), "Liquid height mode/value must be recognized and finite."); if (targetLiquidType is null && heightMode == AdtLiquidHeightMode.Keep) throw new InvalidOperationException("Choose a target liquid type, a height operation, or both.");
        var inspection = Inspect(inputPath); var selected = cells.Distinct().OrderBy(cell => cell.Y).ThenBy(cell => cell.X).ToArray(); if (selected.Length == 0) throw new InvalidOperationException("Select at least one ADT terrain cell."); var edits = new List<AdtLiquidLayerEdit>();
        foreach (var coordinate in selected)
        {
            if (coordinate.X is < 0 or >= 16 || coordinate.Y is < 0 or >= 16) throw new ArgumentOutOfRangeException(nameof(cells), $"ADT cell {coordinate.X},{coordinate.Y} is outside the 16×16 grid."); var layer = inspection.Layers.FirstOrDefault(value => value.CellX == coordinate.X && value.CellY == coordinate.Y && value.Slot == layerSlot) ?? throw new InvalidDataException($"ADT cell {coordinate.X},{coordinate.Y} has no MH2O liquid layer slot {layerSlot}.");
            var editedType = targetLiquidType ?? layer.LiquidType; var editedMinimum = layer.MinHeight; var editedMaximum = layer.MaxHeight; var vertexEdits = new List<AdtLiquidVertexHeightEdit>();
            if (heightMode != AdtLiquidHeightMode.Keep)
            {
                if (layer.VertexFormat == AdtLiquidVertexFormat.DepthOnly || layer.HeightOffsets.Count == 0) throw new NotSupportedException($"ADT cell {coordinate.X},{coordinate.Y} layer {layerSlot} has no editable height vertices ({layer.VertexFormat}); only its liquid type can be changed safely.");
                editedMinimum = heightMode == AdtLiquidHeightMode.Shift ? AddFinite(layer.MinHeight, heightValue) : heightValue; editedMaximum = heightMode == AdtLiquidHeightMode.Shift ? AddFinite(layer.MaxHeight, heightValue) : heightValue;
                for (var index = 0; index < layer.Heights.Count; index++) { var edited = heightMode == AdtLiquidHeightMode.Shift ? AddFinite(layer.Heights[index], heightValue) : heightValue; if (!Same(layer.Heights[index], edited)) vertexEdits.Add(new(layer.HeightOffsets[index], layer.Heights[index], edited)); }
            }
            if (editedType != layer.LiquidType || !Same(editedMinimum, layer.MinHeight) || !Same(editedMaximum, layer.MaxHeight) || vertexEdits.Count > 0) edits.Add(new(layer.CellX, layer.CellY, layer.Slot, layer.InstanceOffset, layer.LiquidTypeOffset, layer.LiquidType, editedType, layer.MinHeightOffset, layer.MinHeight, editedMinimum, layer.MaxHeightOffset, layer.MaxHeight, editedMaximum, vertexEdits));
        }
        if (edits.Count == 0) throw new InvalidOperationException("Every selected liquid layer already has the requested type and height; no edit is required."); var decoded = targetLiquidType is not null && catalog is not null ? LiquidTypeCatalogService.Require(catalog, targetLiquidType.Value) : null;
        return new(FormatVersion, DateTimeOffset.UtcNow, inspection.Path, inspection.Sha256, layerSlot, targetLiquidType, heightMode, heightValue,
            decoded?.Summary, decoded is null ? null : catalog!.DbcDirectory, decoded is null ? null : catalog!.Sha256, edits);
    }

    public static AdtLiquidInspection Preview(AdtLiquidPlan plan)
    {
        var inspection = ValidatePlan(plan); var edits = plan.Edits.ToDictionary(value => value.InstanceOffset); return inspection with { Layers = inspection.Layers.Select(layer => edits.TryGetValue(layer.InstanceOffset, out var edit) ? layer with { LiquidType = edit.EditedLiquidType, MinHeight = edit.EditedMinHeight, MaxHeight = edit.EditedMaxHeight, Heights = ApplyHeightPreview(layer, edit) } : layer).ToArray() };
    }

    public static void SavePlan(AdtLiquidPlan plan, string path, bool overwrite = false) { _ = ValidatePlan(plan); path = Path.GetFullPath(path); if (File.Exists(path) && !overwrite) throw new IOException($"Liquid plan already exists: {path}"); AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(plan, JsonOptions), overwrite); }
    public static AdtLiquidPlan LoadPlan(string path) { path = Path.GetFullPath(path); if (!File.Exists(path)) throw new FileNotFoundException("The ADT liquid plan does not exist.", path); var plan = JsonSerializer.Deserialize<AdtLiquidPlan>(File.ReadAllBytes(path), JsonOptions) ?? throw new InvalidDataException("The ADT liquid plan is empty."); _ = ValidatePlan(plan); return plan; }

    public static AdtLiquidResult Apply(AdtLiquidPlan plan, string outputPath, bool overwrite = false)
    {
        _ = ValidatePlan(plan); outputPath = Path.GetFullPath(outputPath); if (!Path.GetExtension(outputPath).Equals(".adt", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("Liquid output must use the .adt extension."); if (outputPath.Equals(Path.GetFullPath(plan.InputPath), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Crucible does not overwrite the source ADT; choose a separate output path so the liquid edit remains reversible."); if (File.Exists(outputPath) && !overwrite) throw new IOException($"Output ADT already exists: {outputPath}"); var bytes = File.ReadAllBytes(plan.InputPath);
        foreach (var edit in plan.Edits)
        {
            RequirePreimage(bytes, edit.LiquidTypeOffset, edit.OriginalLiquidType); WriteU16(bytes, edit.LiquidTypeOffset, edit.EditedLiquidType); RequirePreimage(bytes, edit.MinHeightOffset, edit.OriginalMinHeight); WriteF32(bytes, edit.MinHeightOffset, edit.EditedMinHeight); RequirePreimage(bytes, edit.MaxHeightOffset, edit.OriginalMaxHeight); WriteF32(bytes, edit.MaxHeightOffset, edit.EditedMaxHeight);
            foreach (var vertex in edit.VertexHeights) { RequirePreimage(bytes, vertex.Offset, vertex.OriginalHeight); WriteF32(bytes, vertex.Offset, vertex.EditedHeight); }
        }
        var inspection = AtomicWriteValidated(outputPath, bytes, overwrite); foreach (var edit in plan.Edits) { var layer = inspection.Layers.Single(value => value.CellX == edit.CellX && value.CellY == edit.CellY && value.Slot == edit.Slot); var writtenHeights = layer.HeightOffsets.Select((offset, index) => (offset, value: layer.Heights[index])).ToDictionary(value => value.offset, value => value.value); if (layer.LiquidType != edit.EditedLiquidType || !Same(layer.MinHeight, edit.EditedMinHeight) || !Same(layer.MaxHeight, edit.EditedMaxHeight) || edit.VertexHeights.Any(vertex => !writtenHeights.TryGetValue(vertex.Offset, out var written) || !Same(written, vertex.EditedHeight))) throw new InvalidDataException($"Written ADT cell {edit.CellX},{edit.CellY} liquid layer {edit.Slot} did not re-parse to the reviewed type/height postimage."); }
        var receiptPath = outputPath + ".crucible-map-liquid.json"; var receipt = new { FormatVersion, AppliedUtc = DateTimeOffset.UtcNow, plan.InputPath, plan.InputSha256, OutputPath = outputPath, OutputSha256 = inspection.Sha256, plan.LayerSlot, plan.TargetLiquidType, plan.HeightMode, plan.HeightValue, plan.CatalogEvidence, plan.CatalogDbcDirectory, plan.CatalogSha256, plan.Edits }; AtomicWrite(receiptPath, JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions), overwrite: true);
        return new(outputPath, inspection.Sha256, receiptPath, inspection, plan.Edits.Count, plan.Edits.Select(value => (value.CellX, value.CellY)).Distinct().Count(), plan.Edits.Sum(value => value.VertexHeights.Count));
    }

    private static AdtLiquidInspection ValidatePlan(AdtLiquidPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan); if (plan.FormatVersion != FormatVersion || plan.LayerSlot < 0 || !Enum.IsDefined(plan.HeightMode) || !float.IsFinite(plan.HeightValue) || plan.Edits.Count == 0 || plan.Edits.Select(value => value.InstanceOffset).Distinct().Count() != plan.Edits.Count) throw new InvalidDataException("ADT liquid plan has an unsupported format, invalid operation, or duplicate/empty edits.");
        var hasCatalog = plan.CatalogDbcDirectory is not null || plan.CatalogSha256 is not null || plan.CatalogEvidence is not null; if (plan.TargetLiquidType is null && hasCatalog || plan.TargetLiquidType is not null && hasCatalog && (string.IsNullOrWhiteSpace(plan.CatalogDbcDirectory) || string.IsNullOrWhiteSpace(plan.CatalogEvidence) || plan.CatalogSha256 is null || !IsSha256(plan.CatalogSha256))) throw new InvalidDataException("ADT liquid plan has incomplete or inconsistent decoded LiquidType provenance.");
        if (hasCatalog) { var catalog = LiquidTypeCatalogService.Load(plan.CatalogDbcDirectory!); var decoded = LiquidTypeCatalogService.Require(catalog, plan.TargetLiquidType!.Value); if (!catalog.Sha256.Equals(plan.CatalogSha256, StringComparison.OrdinalIgnoreCase) || decoded.Summary != plan.CatalogEvidence) throw new InvalidDataException("LiquidType.dbc no longer matches the decoded catalog evidence bound into this plan."); }
        var inspection = Inspect(plan.InputPath); if (!inspection.Sha256.Equals(plan.InputSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Source ADT hash no longer matches the liquid plan; rebuild the plan before applying it."); var occupiedOffsets = new HashSet<long>();
        foreach (var edit in plan.Edits)
        {
            var layer = inspection.Layers.FirstOrDefault(value => value.CellX == edit.CellX && value.CellY == edit.CellY && value.Slot == edit.Slot) ?? throw new InvalidDataException($"Liquid plan references absent cell/layer {edit.CellX},{edit.CellY}:{edit.Slot}."); if (edit.Slot != plan.LayerSlot || edit.InstanceOffset != layer.InstanceOffset || edit.LiquidTypeOffset != layer.LiquidTypeOffset || edit.MinHeightOffset != layer.MinHeightOffset || edit.MaxHeightOffset != layer.MaxHeightOffset || edit.OriginalLiquidType != layer.LiquidType || !Same(edit.OriginalMinHeight, layer.MinHeight) || !Same(edit.OriginalMaxHeight, layer.MaxHeight) || edit.EditedLiquidType != (plan.TargetLiquidType ?? layer.LiquidType)) throw new InvalidDataException($"Liquid plan edit {edit.CellX},{edit.CellY}:{edit.Slot} has a changed identity, offset, type, or height preimage.");
            if (plan.HeightMode != AdtLiquidHeightMode.Keep && (layer.VertexFormat == AdtLiquidVertexFormat.DepthOnly || layer.HeightOffsets.Count == 0)) throw new InvalidDataException($"Liquid plan tries to edit heights for cell {edit.CellX},{edit.CellY}:{edit.Slot}, which has no editable height vertices.");
            var expectedMin = plan.HeightMode switch { AdtLiquidHeightMode.Keep => layer.MinHeight, AdtLiquidHeightMode.Shift => AddFinite(layer.MinHeight, plan.HeightValue), AdtLiquidHeightMode.SetFlat => plan.HeightValue, _ => throw new InvalidDataException("Unknown liquid height mode.") }; var expectedMax = plan.HeightMode switch { AdtLiquidHeightMode.Keep => layer.MaxHeight, AdtLiquidHeightMode.Shift => AddFinite(layer.MaxHeight, plan.HeightValue), AdtLiquidHeightMode.SetFlat => plan.HeightValue, _ => throw new InvalidDataException("Unknown liquid height mode.") };
            if (!Same(edit.EditedMinHeight, expectedMin) || !Same(edit.EditedMaxHeight, expectedMax)) throw new InvalidDataException($"Liquid plan edit {edit.CellX},{edit.CellY}:{edit.Slot} has a changed height postimage."); var expectedVertices = plan.HeightMode == AdtLiquidHeightMode.Keep ? [] : layer.HeightOffsets.Select((offset, index) => new AdtLiquidVertexHeightEdit(offset, layer.Heights[index], plan.HeightMode == AdtLiquidHeightMode.Shift ? AddFinite(layer.Heights[index], plan.HeightValue) : plan.HeightValue)).Where(value => !Same(value.OriginalHeight, value.EditedHeight)).ToArray();
            if (edit.VertexHeights.Count != expectedVertices.Length || !edit.VertexHeights.SequenceEqual(expectedVertices)) throw new InvalidDataException($"Liquid plan edit {edit.CellX},{edit.CellY}:{edit.Slot} has changed vertex-height offsets or values."); foreach (var offset in new[] { edit.LiquidTypeOffset, edit.MinHeightOffset, edit.MaxHeightOffset }.Concat(edit.VertexHeights.Select(value => value.Offset))) if (!occupiedOffsets.Add(offset)) throw new InvalidDataException("ADT liquid plan contains overlapping field edits.");
            if (edit.OriginalLiquidType == edit.EditedLiquidType && Same(edit.OriginalMinHeight, edit.EditedMinHeight) && Same(edit.OriginalMaxHeight, edit.EditedMaxHeight) && edit.VertexHeights.Count == 0) throw new InvalidDataException("ADT liquid plan contains a no-op layer edit.");
        }
        return inspection;
    }

    private static IReadOnlyList<float> ApplyHeightPreview(AdtLiquidLayer layer, AdtLiquidLayerEdit edit)
    {
        if (edit.VertexHeights.Count == 0) return layer.Heights; var byOffset = edit.VertexHeights.ToDictionary(value => value.Offset); return layer.HeightOffsets.Select((offset, index) => byOffset.TryGetValue(offset, out var value) ? value.EditedHeight : layer.Heights[index]).ToArray();
    }

    private static IEnumerable<int> ActiveVertexIndices(int width, int height, ulong existsMask)
    {
        var result = new HashSet<int>(); for (var y = 0; y < height; y++) for (var x = 0; x < width; x++) { var tile = y * width + x; if ((existsMask & (1UL << tile)) == 0) continue; var top = y * (width + 1) + x; result.Add(top); result.Add(top + 1); result.Add(top + width + 1); result.Add(top + width + 2); } return result.Order();
    }

    private sealed record Chunk(string Id, int HeaderOffset, int PayloadOffset, int PayloadLength);
    private static IReadOnlyList<Chunk> ParseTopChunks(byte[] bytes)
    {
        var result = new List<Chunk>(); var offset = 0; while (offset < bytes.Length) { Require(bytes, offset, 8, "ADT chunk header"); var id = Decode(bytes.AsSpan(offset, 4)); var size = ReadU32(bytes, offset + 4); if (size > int.MaxValue) throw new InvalidDataException($"ADT chunk {id} is too large."); Require(bytes, offset + 8L, size, $"ADT chunk {id}"); result.Add(new(id, offset, offset + 8, checked((int)size))); offset = checked(offset + 8 + (int)size); } return result;
    }
    private static long Relative(Chunk chunk, uint relative, int length, string label) { var value = chunk.PayloadOffset + (long)relative; if (relative < CellHeadersBytes || value < chunk.PayloadOffset || value + length > chunk.PayloadOffset + (long)chunk.PayloadLength) throw new InvalidDataException($"{label} points outside the MH2O payload."); return value; }
    private static string Decode(ReadOnlySpan<byte> raw) => new string(Encoding.ASCII.GetString(raw).Reverse().ToArray());
    private static void Require(byte[] bytes, long offset, long length, string label) { if (offset < 0 || length < 0 || offset + length > bytes.LongLength) throw new InvalidDataException($"{label} extends beyond the file."); }
    private static ushort ReadU16(byte[] bytes, long offset) { Require(bytes, offset, 2, "16-bit field"); return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(checked((int)offset), 2)); }
    private static uint ReadU32(byte[] bytes, long offset) { Require(bytes, offset, 4, "32-bit field"); return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(checked((int)offset), 4)); }
    private static ulong ReadU64(byte[] bytes, long offset) { Require(bytes, offset, 8, "64-bit field"); return BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(checked((int)offset), 8)); }
    private static float ReadF32(byte[] bytes, long offset) => BitConverter.Int32BitsToSingle(unchecked((int)ReadU32(bytes, offset)));
    private static void WriteU16(byte[] bytes, long offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(checked((int)offset), 2), value);
    private static void WriteF32(byte[] bytes, long offset, float value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(checked((int)offset), 4), BitConverter.SingleToUInt32Bits(value));
    private static void RequirePreimage(byte[] bytes, long offset, ushort value) { if (ReadU16(bytes, offset) != value) throw new InvalidDataException($"Liquid type at byte {offset:N0} no longer matches its planned preimage."); }
    private static void RequirePreimage(byte[] bytes, long offset, float value) { if (!Same(ReadF32(bytes, offset), value)) throw new InvalidDataException($"Liquid height at byte {offset:N0} no longer matches its planned preimage."); }
    private static float AddFinite(float left, float right) { var value = left + right; return float.IsFinite(value) ? value : throw new InvalidOperationException("The requested liquid height operation overflowed to a non-finite value."); }
    private static bool Same(float left, float right) => BitConverter.SingleToUInt32Bits(left) == BitConverter.SingleToUInt32Bits(right);
    private static bool IsSha256(string value) => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f');
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void AtomicWrite(string path, byte[] bytes, bool overwrite)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Output path has no parent directory."); Directory.CreateDirectory(directory); var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp"); try { using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); } File.Move(temporary, path, overwrite); } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static AdtLiquidInspection AtomicWriteValidated(string path, byte[] bytes, bool overwrite)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Output path has no parent directory."); Directory.CreateDirectory(directory); var temporary = Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(path)}.{Guid.NewGuid():N}.tmp.adt"); try { using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); } var inspection = Inspect(temporary); File.Move(temporary, path, overwrite); return inspection with { Path = path }; } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
