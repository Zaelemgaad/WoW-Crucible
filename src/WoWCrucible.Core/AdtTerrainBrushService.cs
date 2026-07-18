using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public enum AdtTerrainBrushFalloff { Linear, Smooth, Constant }
public enum AdtTerrainBrushMode { RaiseLower, Flatten, Smooth, Noise }
public sealed record AdtTerrainBrushVertex(int CellX, int CellY, int VertexIndex, long HeightOffset, float OriginalRelativeHeight, float EditedRelativeHeight, float Weight, float TileX, float TileY);
public sealed record AdtTerrainBrushPlan(int FormatVersion, DateTimeOffset CreatedUtc, string InputPath, string InputSha256, float CenterX, float CenterY, float Radius, float Strength, AdtTerrainBrushFalloff Falloff, IReadOnlyList<AdtTerrainBrushVertex> Vertices, AdtTerrainBrushMode Mode = AdtTerrainBrushMode.RaiseLower, float? TargetHeight = null, int Seed = 0);
public sealed record AdtTerrainBrushResult(string OutputPath, string OutputSha256, string ReceiptPath, MapAssetInspection Inspection, int EditedVertices, int EditedCells);

public static class AdtTerrainBrushService
{
    private const int FormatVersion = 1;
    private const int VertexCount = 145;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AdtTerrainBrushPlan Plan(string inputPath, float centerX, float centerY, float radius, float strength, AdtTerrainBrushFalloff falloff, AdtTerrainBrushMode mode = AdtTerrainBrushMode.RaiseLower, float? targetHeight = null, int seed = 0)
    {
        inputPath = Path.GetFullPath(inputPath); if (!File.Exists(inputPath)) throw new FileNotFoundException("The source ADT does not exist.", inputPath);
        if (!Path.GetExtension(inputPath).Equals(".adt", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("Terrain brushing requires a WotLK ADT file.");
        if (!float.IsFinite(centerX) || !float.IsFinite(centerY) || centerX is < 0 or > 16 || centerY is < 0 or > 16) throw new ArgumentOutOfRangeException(nameof(centerX), "Brush center must be finite and inside the tile-local 0–16 terrain grid.");
        if (!float.IsFinite(radius) || radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius), "Brush radius must be finite and greater than zero.");
        if (!float.IsFinite(strength) || strength == 0) throw new ArgumentOutOfRangeException(nameof(strength), "Brush strength must be a finite nonzero amount.");
        if (!Enum.IsDefined(falloff)) throw new ArgumentOutOfRangeException(nameof(falloff)); if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == AdtTerrainBrushMode.Flatten && (targetHeight is null || !float.IsFinite(targetHeight.Value))) throw new ArgumentOutOfRangeException(nameof(targetHeight), "Flatten mode requires a finite absolute target height.");
        if (targetHeight is { } suppliedTarget && !float.IsFinite(suppliedTarget)) throw new ArgumentOutOfRangeException(nameof(targetHeight), "Target height must be finite when supplied.");
        var inspection = MapAssetInspectionService.Inspect(inputPath); if (inspection.Kind != MapAssetKind.Adt || inspection.Version != 18) throw new InvalidDataException("Terrain brushing requires a validated WotLK MVER 18 ADT.");
        var vertices = new List<AdtTerrainBrushVertex>(); var cells = LocateCells(inputPath); var heightGrid = HeightGrid(cells.Values);
        foreach (var cell in cells.Values.OrderBy(value => value.CellY).ThenBy(value => value.CellX))
        {
            for (var index = 0; index < cell.Heights.Length; index++)
            {
                var (localX, localY) = VertexPosition(index); var tileX = cell.CellX + localX; var tileY = cell.CellY + localY;
                var distance = MathF.Sqrt(MathF.Pow(tileX - centerX, 2) + MathF.Pow(tileY - centerY, 2)); if (distance > radius) continue;
                var weight = Weight(distance, radius, falloff);
                if (weight <= 0) continue; var edited = EditedRelativeHeight(cell, index, tileX, tileY, weight, strength, mode, targetHeight, seed, heightGrid); if (BitConverter.SingleToInt32Bits(edited) == BitConverter.SingleToInt32Bits(cell.Heights[index])) continue;
                if (!float.IsFinite(edited)) throw new InvalidDataException($"Brush produces a non-finite height at ADT cell {cell.CellX},{cell.CellY} vertex {index}.");
                vertices.Add(new(cell.CellX, cell.CellY, index, cell.HeightDataOffset + index * 4L, cell.Heights[index], edited, weight, tileX, tileY));
            }
        }
        if (vertices.Count == 0) throw new InvalidOperationException("The brush does not produce a terrain change. Move it onto present vertices, increase its radius/strength, or choose a different target/mode.");
        return new(FormatVersion, DateTimeOffset.UtcNow, inputPath, Sha256(inputPath), centerX, centerY, radius, strength, falloff, vertices, mode, targetHeight, seed);
    }

    public static MapAssetInspection Preview(AdtTerrainBrushPlan plan)
    {
        ValidatePlan(plan, verifySource: true); var inspection = MapAssetInspectionService.Inspect(plan.InputPath); var locations = LocateCells(plan.InputPath); var edits = plan.Vertices.ToDictionary(vertex => vertex.HeightOffset);
        var ranges = locations.Values.ToDictionary(cell => (cell.CellX, cell.CellY), cell =>
        {
            var minimum = float.PositiveInfinity; var maximum = float.NegativeInfinity;
            for (var index = 0; index < cell.Heights.Length; index++) { var offset = cell.HeightDataOffset + index * 4L; var relative = edits.TryGetValue(offset, out var edit) ? edit.EditedRelativeHeight : cell.Heights[index]; var absolute = cell.BaseHeight + relative; minimum = Math.Min(minimum, absolute); maximum = Math.Max(maximum, absolute); }
            return (minimum, maximum);
        });
        return inspection with { Cells = inspection.Cells.Select(cell => ranges.TryGetValue((cell.X, cell.Y), out var range) ? cell with { MinimumHeight = range.minimum, MaximumHeight = range.maximum } : cell).ToArray() };
    }

    public static void SavePlan(AdtTerrainBrushPlan plan, string outputPath, bool overwrite = false)
    {
        ValidatePlan(plan, verifySource: true); outputPath = Path.GetFullPath(outputPath); if (File.Exists(outputPath) && !overwrite) throw new IOException($"Terrain-brush plan already exists: {outputPath}"); AtomicWrite(outputPath, JsonSerializer.SerializeToUtf8Bytes(plan, JsonOptions), overwrite);
    }

    public static AdtTerrainBrushPlan LoadPlan(string path)
    {
        path = Path.GetFullPath(path); if (!File.Exists(path)) throw new FileNotFoundException("The ADT terrain-brush plan does not exist.", path);
        var plan = JsonSerializer.Deserialize<AdtTerrainBrushPlan>(File.ReadAllBytes(path), JsonOptions) ?? throw new InvalidDataException("The ADT terrain-brush plan is empty."); ValidatePlan(plan, verifySource: true); return plan;
    }

    public static AdtTerrainBrushResult Apply(AdtTerrainBrushPlan plan, string outputPath, bool overwrite = false)
    {
        ValidatePlan(plan, verifySource: true); outputPath = Path.GetFullPath(outputPath); if (outputPath.Equals(Path.GetFullPath(plan.InputPath), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Crucible does not overwrite the source ADT; choose a separate output path so the brush remains reversible.");
        if (File.Exists(outputPath) && !overwrite) throw new IOException($"Output ADT already exists: {outputPath}"); var source = File.ReadAllBytes(plan.InputPath);
        foreach (var vertex in plan.Vertices)
        {
            if (vertex.HeightOffset < 0 || vertex.HeightOffset + 4 > source.LongLength || BitConverter.SingleToInt32Bits(BitConverter.ToSingle(source, checked((int)vertex.HeightOffset))) != BitConverter.SingleToInt32Bits(vertex.OriginalRelativeHeight)) throw new InvalidDataException($"ADT cell {vertex.CellX},{vertex.CellY} vertex {vertex.VertexIndex} no longer matches its planned byte preimage.");
            BitConverter.GetBytes(vertex.EditedRelativeHeight).CopyTo(source, checked((int)vertex.HeightOffset));
        }
        AtomicWrite(outputPath, source, overwrite); var inspection = MapAssetInspectionService.Inspect(outputPath); var expected = Preview(plan);
        foreach (var coordinate in plan.Vertices.Select(vertex => (vertex.CellX, vertex.CellY)).Distinct())
        {
            var actual = inspection.Cells.Single(cell => cell.X == coordinate.CellX && cell.Y == coordinate.CellY && cell.Present); var planned = expected.Cells.Single(cell => cell.X == coordinate.CellX && cell.Y == coordinate.CellY && cell.Present);
            if (actual.MinimumHeight is not { } minimum || actual.MaximumHeight is not { } maximum || planned.MinimumHeight is not { } expectedMinimum || planned.MaximumHeight is not { } expectedMaximum || Math.Abs(minimum - expectedMinimum) > 0.001f || Math.Abs(maximum - expectedMaximum) > 0.001f) throw new InvalidDataException($"Written ADT cell {coordinate.CellX},{coordinate.CellY} did not re-parse to the brushed height range.");
        }
        var hash = Sha256(outputPath); var receiptPath = outputPath + ".crucible-map-brush.json"; var receipt = new { FormatVersion, AppliedUtc = DateTimeOffset.UtcNow, plan.InputPath, plan.InputSha256, OutputPath = outputPath, OutputSha256 = hash, plan.CenterX, plan.CenterY, plan.Radius, plan.Strength, plan.Falloff, plan.Mode, plan.TargetHeight, plan.Seed, plan.Vertices };
        AtomicWrite(receiptPath, JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions), overwrite: true); return new(outputPath, hash, receiptPath, inspection, plan.Vertices.Count, plan.Vertices.Select(vertex => (vertex.CellX, vertex.CellY)).Distinct().Count());
    }

    private sealed record CellVertices(int CellX, int CellY, float BaseHeight, long HeightDataOffset, float[] Heights);
    private static Dictionary<(int X, int Y), CellVertices> LocateCells(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, FileOptions.RandomAccess); var result = new Dictionary<(int, int), CellVertices>(); var header = new byte[8];
        while (stream.Position < stream.Length)
        {
            var chunkOffset = stream.Position; if (stream.Read(header) != 8) throw new InvalidDataException($"ADT chunk header is truncated at byte {chunkOffset:N0}."); var id = Decode(header.AsSpan(0, 4)); var size = BitConverter.ToUInt32(header, 4); var payload = stream.Position; var end = payload + size;
            if (size > int.MaxValue || end > stream.Length) throw new InvalidDataException($"ADT chunk {id} at byte {chunkOffset:N0} extends beyond the file.");
            if (id == "MCNK")
            {
                if (size < 128) throw new InvalidDataException($"ADT MCNK at byte {chunkOffset:N0} is shorter than its 128-byte header."); var data = new byte[128]; stream.ReadExactly(data); var x = BitConverter.ToUInt32(data, 4); var y = BitConverter.ToUInt32(data, 8); var relativeOffset = BitConverter.ToUInt32(data, 0x14); var baseHeight = BitConverter.ToSingle(data, 0x70);
                if (x >= 16 || y >= 16 || !float.IsFinite(baseHeight) || relativeOffset == 0) throw new InvalidDataException($"ADT MCNK at byte {chunkOffset:N0} has invalid coordinates, base height, or no MCVT grid."); var nested = chunkOffset + relativeOffset;
                if (nested < chunkOffset + 8 || nested + 8L + VertexCount * 4L > end) throw new InvalidDataException($"ADT MCNK {x},{y} points outside its chunk for MCVT."); stream.Position = nested; stream.ReadExactly(header); var nestedId = Decode(header.AsSpan(0, 4)); var nestedSize = BitConverter.ToUInt32(header, 4);
                if (nestedId != "MCVT" || nestedSize < VertexCount * 4 || stream.Position + nestedSize > end) throw new InvalidDataException($"ADT MCNK {x},{y} does not point to a complete MCVT height grid."); var heights = new float[VertexCount]; var bytes = new byte[VertexCount * 4]; stream.ReadExactly(bytes);
                for (var index = 0; index < heights.Length; index++) { heights[index] = BitConverter.ToSingle(bytes, index * 4); if (!float.IsFinite(heights[index])) throw new InvalidDataException($"ADT MCNK {x},{y} vertex {index} is non-finite."); }
                if (!result.TryAdd(((int)x, (int)y), new((int)x, (int)y, baseHeight, nested + 8, heights))) throw new InvalidDataException($"ADT contains duplicate MCNK coordinate {x},{y}.");
            }
            stream.Position = end;
        }
        return result;
    }

    public static (float X, float Y) VertexPosition(int index)
    {
        if (index is < 0 or >= VertexCount) throw new ArgumentOutOfRangeException(nameof(index)); var remaining = index;
        for (var row = 0; row < 17; row++) { var count = row % 2 == 0 ? 9 : 8; if (remaining < count) return ((remaining + (row % 2 == 0 ? 0f : 0.5f)) / 8f, row / 16f); remaining -= count; }
        throw new InvalidOperationException("MCVT vertex index could not be mapped.");
    }

    private static void ValidatePlan(AdtTerrainBrushPlan plan, bool verifySource)
    {
        ArgumentNullException.ThrowIfNull(plan); if (plan.FormatVersion != FormatVersion) throw new InvalidDataException($"Unsupported ADT terrain-brush plan format {plan.FormatVersion}.");
        if (plan.Vertices.Count == 0 || !float.IsFinite(plan.CenterX) || !float.IsFinite(plan.CenterY) || plan.CenterX is < 0 or > 16 || plan.CenterY is < 0 or > 16 || !float.IsFinite(plan.Radius) || plan.Radius <= 0 || !float.IsFinite(plan.Strength) || plan.Strength == 0 || !Enum.IsDefined(plan.Falloff) || !Enum.IsDefined(plan.Mode) || (plan.TargetHeight is { } target && !float.IsFinite(target)) || (plan.Mode == AdtTerrainBrushMode.Flatten && plan.TargetHeight is null)) throw new InvalidDataException("ADT terrain-brush plan has no valid brush, mode, or vertex edits.");
        if (plan.Vertices.Select(vertex => vertex.HeightOffset).Distinct().Count() != plan.Vertices.Count) throw new InvalidDataException("ADT terrain-brush plan contains duplicate byte offsets.");
        if (!verifySource) return; if (!Sha256(plan.InputPath).Equals(plan.InputSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Source ADT hash no longer matches the terrain-brush plan; rebuild the plan before applying it."); var cells = LocateCells(plan.InputPath); var heightGrid = HeightGrid(cells.Values);
        foreach (var vertex in plan.Vertices)
        {
            if (vertex.VertexIndex is < 0 or >= VertexCount || !cells.TryGetValue((vertex.CellX, vertex.CellY), out var cell)) throw new InvalidDataException($"Terrain-brush vertex {vertex.CellX},{vertex.CellY}:{vertex.VertexIndex} does not identify a source MCVT vertex."); var expectedOffset = cell.HeightDataOffset + vertex.VertexIndex * 4L;
            if (vertex.HeightOffset != expectedOffset || BitConverter.SingleToInt32Bits(vertex.OriginalRelativeHeight) != BitConverter.SingleToInt32Bits(cell.Heights[vertex.VertexIndex])) throw new InvalidDataException($"Terrain-brush vertex {vertex.CellX},{vertex.CellY}:{vertex.VertexIndex} does not match its source offset/preimage.");
            var (localX, localY) = VertexPosition(vertex.VertexIndex); var tileX = vertex.CellX + localX; var tileY = vertex.CellY + localY; var distance = MathF.Sqrt(MathF.Pow(tileX - plan.CenterX, 2) + MathF.Pow(tileY - plan.CenterY, 2)); var weight = Weight(distance, plan.Radius, plan.Falloff); var edited = EditedRelativeHeight(cell, vertex.VertexIndex, tileX, tileY, weight, plan.Strength, plan.Mode, plan.TargetHeight, plan.Seed, heightGrid);
            if (weight <= 0 || !float.IsFinite(vertex.Weight) || !float.IsFinite(vertex.EditedRelativeHeight) || BitConverter.SingleToInt32Bits(vertex.EditedRelativeHeight) == BitConverter.SingleToInt32Bits(vertex.OriginalRelativeHeight) || Math.Abs(vertex.TileX - tileX) > 0.000001f || Math.Abs(vertex.TileY - tileY) > 0.000001f || Math.Abs(vertex.Weight - weight) > 0.000001f || Math.Abs(vertex.EditedRelativeHeight - edited) > 0.00001f) throw new InvalidDataException($"Terrain-brush vertex {vertex.CellX},{vertex.CellY}:{vertex.VertexIndex} has a changed coordinate, weight, or postimage.");
        }
    }
    private static float Weight(float distance, float radius, AdtTerrainBrushFalloff falloff)
    {
        if (distance > radius) return 0; var normalized = Math.Clamp(distance / radius, 0, 1); return falloff switch { AdtTerrainBrushFalloff.Constant => 1f, AdtTerrainBrushFalloff.Smooth => 1f - normalized * normalized * (3f - 2f * normalized), _ => 1f - normalized };
    }
    private static Dictionary<(int X, int Y), float> HeightGrid(IEnumerable<CellVertices> cells)
    {
        var samples = new Dictionary<(int, int), (double Sum, int Count)>(); foreach (var cell in cells) for (var index = 0; index < cell.Heights.Length; index++) { var (localX, localY) = VertexPosition(index); var key = GridKey(cell.CellX + localX, cell.CellY + localY); samples.TryGetValue(key, out var value); samples[key] = (value.Sum + cell.BaseHeight + cell.Heights[index], value.Count + 1); }
        return samples.ToDictionary(pair => pair.Key, pair => (float)(pair.Value.Sum / pair.Value.Count));
    }
    private static float EditedRelativeHeight(CellVertices cell, int index, float tileX, float tileY, float weight, float strength, AdtTerrainBrushMode mode, float? targetHeight, int seed, IReadOnlyDictionary<(int X, int Y), float> heightGrid)
    {
        var originalRelative = cell.Heights[index]; var originalAbsolute = cell.BaseHeight + originalRelative; var amount = Math.Abs(strength) * weight; float editedAbsolute;
        switch (mode)
        {
            case AdtTerrainBrushMode.Flatten:
                var flattenTarget = targetHeight ?? throw new InvalidDataException("Flatten brush has no target height."); editedAbsolute = originalAbsolute + Math.Clamp(flattenTarget - originalAbsolute, -amount, amount); break;
            case AdtTerrainBrushMode.Smooth:
                var key = GridKey(tileX, tileY); double sum = 0; var count = 0; for (var y = -2; y <= 2; y++) for (var x = -2; x <= 2; x++) if ((x != 0 || y != 0) && x * x + y * y <= 4 && heightGrid.TryGetValue((key.X + x, key.Y + y), out var neighbor)) { sum += neighbor; count++; }
                var smoothTarget = count == 0 ? originalAbsolute : (float)(sum / count); editedAbsolute = originalAbsolute + Math.Clamp(smoothTarget - originalAbsolute, -amount, amount); break;
            case AdtTerrainBrushMode.Noise:
                var noiseKey = GridKey(tileX, tileY); editedAbsolute = originalAbsolute + Noise(noiseKey.X, noiseKey.Y, seed) * amount; break;
            default:
                editedAbsolute = originalAbsolute + strength * weight; break;
        }
        var result = editedAbsolute - cell.BaseHeight; if (!float.IsFinite(result)) throw new InvalidDataException($"Brush produces a non-finite height at ADT cell {cell.CellX},{cell.CellY} vertex {index}."); return result;
    }
    private static (int X, int Y) GridKey(float tileX, float tileY) => ((int)MathF.Round(tileX * 16f), (int)MathF.Round(tileY * 16f));
    private static float Noise(int x, int y, int seed)
    {
        var value = unchecked((uint)seed * 0x9E3779B9u ^ (uint)x * 0x85EBCA6Bu ^ (uint)y * 0xC2B2AE35u); value ^= value >> 16; value *= 0x7FEB352Du; value ^= value >> 15; value *= 0x846CA68Bu; value ^= value >> 16; return value / (float)uint.MaxValue * 2f - 1f;
    }
    private static string Decode(ReadOnlySpan<byte> raw) => new string(Encoding.ASCII.GetString(raw).Reverse().ToArray());
    private static string Sha256(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void AtomicWrite(string path, byte[] bytes, bool overwrite)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Output path has no parent directory."); Directory.CreateDirectory(directory); var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try { using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); } File.Move(temporary, path, overwrite); } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
