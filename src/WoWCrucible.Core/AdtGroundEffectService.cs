using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoWCrucible.Core;

public sealed record GroundEffectDoodadRecord(uint Id, string ClientModelPath, uint Flags);
public sealed record GroundEffectTextureRecord(uint Id, IReadOnlyList<uint> DoodadIds, IReadOnlyList<uint> DoodadWeights,
    uint Density, uint Sound, IReadOnlyList<GroundEffectDoodadRecord?> Doodads, [property: JsonIgnore] string SearchText)
{
    public string Summary
    {
        get
        {
            var models = Doodads.Select((value, index) => (Doodad: value, Weight: index < DoodadWeights.Count ? DoodadWeights[index] : 0)).Where(value => value.Doodad is not null).GroupBy(value => value.Doodad!.ClientModelPath, StringComparer.OrdinalIgnoreCase).Select(group => $"{Path.GetFileName(group.Key)} weight {group.Sum(value => (long)value.Weight):N0}").Take(4).ToArray();
            return $"Effect {Id:N0} · density {Density:N0} · sound {Sound:N0}" + (models.Length == 0 ? " · no doodad models" : " · " + string.Join(", ", models));
        }
    }
}
public sealed record GroundEffectCatalog(string DbcDirectory, string TextureSha256, string DoodadSha256,
    IReadOnlyList<GroundEffectTextureRecord> Effects, IReadOnlyDictionary<uint, GroundEffectDoodadRecord> Doodads,
    IReadOnlyList<string> Findings);

public static class GroundEffectCatalogService
{
    public static GroundEffectCatalog Load(string dbcDirectory)
    {
        var root = Path.GetFullPath(dbcDirectory); if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"DBC directory not found: {root}");
        var doodadPath = Path.Combine(root, "GroundEffectDoodad.dbc"); var texturePath = Path.Combine(root, "GroundEffectTexture.dbc");
        var doodadFile = LoadExact(doodadPath, 3, 12); var textureFile = LoadExact(texturePath, 11, 44);
        var doodads = Enumerable.Range(0, doodadFile.RowCount).Select(row => new GroundEffectDoodadRecord(Raw(doodadFile, row, 0), doodadFile.GetString(Raw(doodadFile, row, 1)), Raw(doodadFile, row, 2))).ToArray();
        var duplicateDoodad = doodads.GroupBy(value => value.Id).FirstOrDefault(group => group.Count() > 1); if (duplicateDoodad is not null) throw new InvalidDataException($"GroundEffectDoodad.dbc contains duplicate ID {duplicateDoodad.Key:N0}.");
        var byDoodad = doodads.ToDictionary(value => value.Id); var findings = new List<string>(); var effects = new List<GroundEffectTextureRecord>(textureFile.RowCount); var effectIds = new HashSet<uint>();
        for (var row = 0; row < textureFile.RowCount; row++)
        {
            var id = Raw(textureFile, row, 0); if (!effectIds.Add(id)) throw new InvalidDataException($"GroundEffectTexture.dbc contains duplicate ID {id:N0}.");
            if (id > int.MaxValue) findings.Add($"GroundEffectTexture {id:N0} exceeds the signed MCLY effect-ID range and cannot be assigned.");
            var ids = Enumerable.Range(0, 4).Select(index => Raw(textureFile, row, 1 + index)).ToArray(); var weights = Enumerable.Range(0, 4).Select(index => Raw(textureFile, row, 5 + index)).ToArray();
            var resolved = ids.Select(doodadId => doodadId == 0 ? null : byDoodad.GetValueOrDefault(doodadId)).ToArray();
            foreach (var missing in ids.Where(value => value != 0 && !byDoodad.ContainsKey(value)).Distinct()) findings.Add($"GroundEffectTexture {id:N0} references missing GroundEffectDoodad {missing:N0}.");
            var density = Raw(textureFile, row, 9); var sound = Raw(textureFile, row, 10); var searchText = $"{id} {density} {sound} " + string.Join(' ', ids) + " " + string.Join(' ', resolved.Where(value => value is not null).Select(value => value!.ClientModelPath)); effects.Add(new(id, ids, weights, density, sound, resolved, searchText));
        }
        return new(root, Sha256(texturePath), Sha256(doodadPath), effects.OrderBy(value => value.Id).ToArray(), byDoodad, findings.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static IReadOnlyList<GroundEffectTextureRecord> Search(GroundEffectCatalog catalog, string? query, int limit = 256)
    {
        ArgumentNullException.ThrowIfNull(catalog); if (limit is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(limit), "Ground-effect result limit must be 1 through 10,000."); var text = query?.Trim() ?? string.Empty;
        if (text.Length == 0) return catalog.Effects.Take(limit).ToArray();
        var terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); return catalog.Effects.Where(effect =>
        {
            return terms.All(term => effect.SearchText.Contains(term, StringComparison.OrdinalIgnoreCase));
        }).Take(limit).ToArray();
    }

    public static GroundEffectTextureRecord Require(GroundEffectCatalog catalog, int effectId)
    {
        ArgumentNullException.ThrowIfNull(catalog); if (effectId < 0) throw new ArgumentOutOfRangeException(nameof(effectId), "A catalog ground-effect ID must be non-negative.");
        return catalog.Effects.FirstOrDefault(value => value.Id == (uint)effectId) ?? throw new InvalidDataException($"GroundEffectTexture.dbc does not contain effect ID {effectId:N0}.");
    }

    private static WdbcFile LoadExact(string path, int fields, int bytes)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Required build-12340 ground-effect table not found: {path}", path); var file = WdbcFile.Load(path);
        if (file.ContainerKind != ClientTableContainerKind.Wdbc || file.FieldCount != fields || file.RecordSize != bytes) throw new InvalidDataException($"{Path.GetFileName(path)} is not the expected build-12340 WDBC layout ({fields} fields / {bytes} bytes); found {file.ContainerKind}, {file.FieldCount} fields / {file.RecordSize} bytes."); return file;
    }
    private static uint Raw(WdbcFile file, int row, int field) => file.GetRaw(row, new(field, field * 4, 4, $"Field{field}", DbcValueType.Raw32));
    private static string Sha256(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}

public sealed record AdtGroundEffectEdit(int CellX, int CellY, int Slot, long EffectIdOffset, int OriginalEffectId, int EditedEffectId, uint TextureId);
public sealed record AdtGroundEffectPlan(int FormatVersion, DateTimeOffset CreatedUtc, string InputPath, string InputSha256,
    int LayerSlot, int EffectId, string? CatalogEvidence, string? CatalogDbcDirectory, string? CatalogTextureSha256,
    string? CatalogDoodadSha256, IReadOnlyList<AdtGroundEffectEdit> Edits);
public sealed record AdtGroundEffectResult(string OutputPath, string OutputSha256, string ReceiptPath,
    AdtTextureLayerInspection Inspection, int EditedLayers, int EditedCells);

public static class AdtGroundEffectService
{
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AdtGroundEffectPlan Plan(string inputPath, IEnumerable<(int X, int Y)> cells, int layerSlot, int effectId, GroundEffectCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(cells); if (layerSlot < 0) throw new ArgumentOutOfRangeException(nameof(layerSlot), "Layer slot must be non-negative."); if (effectId < -1) throw new ArgumentOutOfRangeException(nameof(effectId), "Ground-effect ID must be -1 (none) or a non-negative GroundEffectTexture ID.");
        var evidence = effectId == -1 ? "-1 · no ground effect" : catalog is null ? null : GroundEffectCatalogService.Require(catalog, effectId).Summary; var inspection = AdtTextureLayerService.Inspect(inputPath);
        var selected = cells.Distinct().OrderBy(cell => cell.Y).ThenBy(cell => cell.X).ToArray(); if (selected.Length == 0) throw new InvalidOperationException("Select at least one ADT terrain cell."); var edits = new List<AdtGroundEffectEdit>();
        foreach (var coordinate in selected)
        {
            if (coordinate.X is < 0 or >= 16 || coordinate.Y is < 0 or >= 16) throw new ArgumentOutOfRangeException(nameof(cells), $"ADT cell {coordinate.X},{coordinate.Y} is outside the 16×16 grid.");
            var layer = inspection.Layers.FirstOrDefault(value => value.CellX == coordinate.X && value.CellY == coordinate.Y && value.Slot == layerSlot) ?? throw new InvalidDataException($"ADT cell {coordinate.X},{coordinate.Y} has no texture layer slot {layerSlot}.");
            if (layer.EffectId != effectId) edits.Add(new(layer.CellX, layer.CellY, layer.Slot, layer.TextureIdOffset + 12, layer.EffectId, effectId, layer.TextureId));
        }
        if (edits.Count == 0) throw new InvalidOperationException("Every selected layer already uses that ground-effect ID; no edit is required.");
        var decodedCatalog = effectId >= 0 ? catalog : null;
        return new(FormatVersion, DateTimeOffset.UtcNow, inspection.Path, inspection.Sha256, layerSlot, effectId, evidence,
            decodedCatalog?.DbcDirectory, decodedCatalog?.TextureSha256, decodedCatalog?.DoodadSha256, edits);
    }

    public static AdtTextureLayerInspection Preview(AdtGroundEffectPlan plan)
    {
        var inspection = ValidatePlan(plan); var offsets = plan.Edits.Select(value => value.EffectIdOffset).ToHashSet();
        return inspection with { Layers = inspection.Layers.Select(layer => offsets.Contains(layer.TextureIdOffset + 12) ? layer with { EffectId = plan.EffectId } : layer).ToArray() };
    }

    public static void SavePlan(AdtGroundEffectPlan plan, string path, bool overwrite = false)
    {
        _ = ValidatePlan(plan); path = Path.GetFullPath(path); if (File.Exists(path) && !overwrite) throw new IOException($"Ground-effect plan already exists: {path}"); AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(plan, JsonOptions), overwrite);
    }

    public static AdtGroundEffectPlan LoadPlan(string path)
    {
        path = Path.GetFullPath(path); if (!File.Exists(path)) throw new FileNotFoundException("The ADT ground-effect plan does not exist.", path); var plan = JsonSerializer.Deserialize<AdtGroundEffectPlan>(File.ReadAllBytes(path), JsonOptions) ?? throw new InvalidDataException("The ADT ground-effect plan is empty."); _ = ValidatePlan(plan); return plan;
    }

    public static AdtGroundEffectResult Apply(AdtGroundEffectPlan plan, string outputPath, bool overwrite = false)
    {
        _ = ValidatePlan(plan); outputPath = Path.GetFullPath(outputPath); if (!Path.GetExtension(outputPath).Equals(".adt", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("Ground-effect output must use the .adt extension."); if (outputPath.Equals(Path.GetFullPath(plan.InputPath), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Crucible does not overwrite the source ADT; choose a separate output path so the ground-effect edit remains reversible."); if (File.Exists(outputPath) && !overwrite) throw new IOException($"Output ADT already exists: {outputPath}"); var bytes = File.ReadAllBytes(plan.InputPath);
        foreach (var edit in plan.Edits)
        {
            if (edit.EffectIdOffset < 0 || edit.EffectIdOffset + 4 > bytes.LongLength || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(checked((int)edit.EffectIdOffset), 4)) != edit.OriginalEffectId) throw new InvalidDataException($"ADT cell {edit.CellX},{edit.CellY} layer {edit.Slot} no longer matches its planned ground-effect preimage.");
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(checked((int)edit.EffectIdOffset), 4), edit.EditedEffectId);
        }
        var inspection = AtomicWriteValidated(outputPath, bytes, overwrite, AdtTextureLayerService.Inspect);
        foreach (var edit in plan.Edits) { var layer = inspection.Layers.Single(value => value.CellX == edit.CellX && value.CellY == edit.CellY && value.Slot == edit.Slot); if (layer.EffectId != plan.EffectId || layer.TextureId != edit.TextureId) throw new InvalidDataException($"Written ADT cell {edit.CellX},{edit.CellY} layer {edit.Slot} did not re-parse to ground-effect ID {plan.EffectId:N0} while retaining its texture."); }
        var receiptPath = outputPath + ".crucible-map-ground-effect.json"; var receipt = new { FormatVersion, AppliedUtc = DateTimeOffset.UtcNow, plan.InputPath, plan.InputSha256, OutputPath = outputPath, OutputSha256 = inspection.Sha256, plan.LayerSlot, plan.EffectId, plan.CatalogEvidence, plan.CatalogDbcDirectory, plan.CatalogTextureSha256, plan.CatalogDoodadSha256, plan.Edits }; AtomicWrite(receiptPath, JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions), overwrite: true);
        return new(outputPath, inspection.Sha256, receiptPath, inspection, plan.Edits.Count, plan.Edits.Select(value => (value.CellX, value.CellY)).Distinct().Count());
    }

    private static AdtTextureLayerInspection ValidatePlan(AdtGroundEffectPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan); if (plan.FormatVersion != FormatVersion || plan.LayerSlot < 0 || plan.EffectId < -1 || plan.Edits.Count == 0 || plan.Edits.Select(value => value.EffectIdOffset).Distinct().Count() != plan.Edits.Count) throw new InvalidDataException("ADT ground-effect plan has an unsupported format, invalid ID/slot, or duplicate/empty edits.");
        var catalogFields = new[] { plan.CatalogDbcDirectory, plan.CatalogTextureSha256, plan.CatalogDoodadSha256 }; var hasCatalog = catalogFields.Any(value => value is not null);
        var invalidClearEvidence = plan.EffectId == -1 && (hasCatalog || plan.CatalogEvidence != "-1 · no ground effect");
        var invalidCatalogEvidence = plan.EffectId >= 0 && (hasCatalog != !string.IsNullOrWhiteSpace(plan.CatalogEvidence) || hasCatalog && (catalogFields.Any(string.IsNullOrWhiteSpace) || !IsSha256(plan.CatalogTextureSha256!) || !IsSha256(plan.CatalogDoodadSha256!)));
        if (invalidClearEvidence || invalidCatalogEvidence) throw new InvalidDataException("ADT ground-effect plan has incomplete or inconsistent decoded-catalog provenance.");
        var inspection = AdtTextureLayerService.Inspect(plan.InputPath); if (!inspection.Sha256.Equals(plan.InputSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Source ADT hash no longer matches the ground-effect plan; rebuild the plan before applying it.");
        foreach (var edit in plan.Edits)
        {
            var layer = inspection.Layers.FirstOrDefault(value => value.CellX == edit.CellX && value.CellY == edit.CellY && value.Slot == edit.Slot) ?? throw new InvalidDataException($"Ground-effect plan references absent cell/layer {edit.CellX},{edit.CellY}:{edit.Slot}."); var expectedOffset = layer.TextureIdOffset + 12;
            if (edit.Slot != plan.LayerSlot || edit.EffectIdOffset != expectedOffset || edit.OriginalEffectId != layer.EffectId || edit.EditedEffectId != plan.EffectId || edit.TextureId != layer.TextureId || edit.OriginalEffectId == edit.EditedEffectId) throw new InvalidDataException($"Ground-effect plan edit {edit.CellX},{edit.CellY}:{edit.Slot} has a changed offset, texture identity, preimage, or postimage.");
        }
        return inspection;
    }

    private static void AtomicWrite(string path, byte[] bytes, bool overwrite)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Output path has no parent directory."); Directory.CreateDirectory(directory); var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try { using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); } File.Move(temporary, path, overwrite); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static AdtTextureLayerInspection AtomicWriteValidated(string path, byte[] bytes, bool overwrite, Func<string, AdtTextureLayerInspection> validate)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Output path has no parent directory."); Directory.CreateDirectory(directory); var temporary = Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(path)}.{Guid.NewGuid():N}.tmp.adt");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); }
            var result = validate(temporary); File.Move(temporary, path, overwrite); return result with { Path = path };
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f');
}
