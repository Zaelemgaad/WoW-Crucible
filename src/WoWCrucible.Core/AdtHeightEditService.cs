using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public sealed record AdtHeightEditCell(int X, int Y, long BaseHeightOffset, float OriginalBaseHeight, float OriginalMinimumHeight, float OriginalMaximumHeight, float EditedMinimumHeight, float EditedMaximumHeight);
public sealed record AdtHeightEditPlan(int FormatVersion, DateTimeOffset CreatedUtc, string InputPath, string InputSha256, float Delta, IReadOnlyList<AdtHeightEditCell> Cells);
public sealed record AdtHeightEditResult(string OutputPath, string OutputSha256, string ReceiptPath, MapAssetInspection Inspection, int EditedCells, float Delta);

public static class AdtHeightEditService
{
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AdtHeightEditPlan Plan(string inputPath, IEnumerable<(int X, int Y)> cells, float delta)
    {
        inputPath = Path.GetFullPath(inputPath); if (!File.Exists(inputPath)) throw new FileNotFoundException("The source ADT does not exist.", inputPath);
        if (!Path.GetExtension(inputPath).Equals(".adt", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("Terrain-height editing requires a WotLK ADT file.");
        if (!float.IsFinite(delta)) throw new ArgumentOutOfRangeException(nameof(delta), "The height delta must be finite.");
        var requested = cells.Distinct().ToArray(); if (requested.Length == 0) throw new InvalidOperationException("Select at least one present ADT terrain cell.");
        foreach (var (x, y) in requested) if (x is < 0 or >= 16 || y is < 0 or >= 16) throw new ArgumentOutOfRangeException(nameof(cells), $"ADT cell {x},{y} is outside the 16×16 terrain grid.");
        var inspection = MapAssetInspectionService.Inspect(inputPath); if (inspection.Kind != MapAssetKind.Adt || inspection.Version != 18) throw new InvalidDataException("Terrain-height editing requires a validated WotLK MVER 18 ADT.");
        var locations = LocateCells(inputPath); var result = new List<AdtHeightEditCell>(requested.Length);
        foreach (var coordinate in requested.OrderBy(cell => cell.Y).ThenBy(cell => cell.X))
        {
            if (!locations.TryGetValue(coordinate, out var location)) throw new InvalidDataException($"ADT cell {coordinate.X},{coordinate.Y} is absent or invalid.");
            var cell = inspection.Cells.Single(candidate => candidate.X == coordinate.X && candidate.Y == coordinate.Y && candidate.Present);
            var minimum = cell.MinimumHeight ?? location.BaseHeight; var maximum = cell.MaximumHeight ?? location.BaseHeight;
            var editedMinimum = minimum + delta; var editedMaximum = maximum + delta; var editedBase = location.BaseHeight + delta;
            if (!float.IsFinite(editedMinimum) || !float.IsFinite(editedMaximum) || !float.IsFinite(editedBase)) throw new InvalidDataException($"Height delta produces a non-finite value for ADT cell {coordinate.X},{coordinate.Y}.");
            result.Add(new(coordinate.X, coordinate.Y, location.Offset, location.BaseHeight, minimum, maximum, editedMinimum, editedMaximum));
        }
        return new(FormatVersion, DateTimeOffset.UtcNow, inputPath, Sha256(inputPath), delta, result);
    }

    public static MapAssetInspection Preview(AdtHeightEditPlan plan)
    {
        ValidatePlan(plan, verifySource: true); var inspection = MapAssetInspectionService.Inspect(plan.InputPath); var edits = plan.Cells.ToDictionary(cell => (cell.X, cell.Y));
        return inspection with { Cells = inspection.Cells.Select(cell => edits.TryGetValue((cell.X, cell.Y), out var edit) ? cell with { MinimumHeight = edit.EditedMinimumHeight, MaximumHeight = edit.EditedMaximumHeight } : cell).ToArray() };
    }

    public static void SavePlan(AdtHeightEditPlan plan, string outputPath, bool overwrite = false)
    {
        ValidatePlan(plan, verifySource: true); outputPath = Path.GetFullPath(outputPath); if (File.Exists(outputPath) && !overwrite) throw new IOException($"Height-edit plan already exists: {outputPath}");
        AtomicWrite(outputPath, JsonSerializer.SerializeToUtf8Bytes(plan, JsonOptions), overwrite);
    }

    public static AdtHeightEditPlan LoadPlan(string path)
    {
        path = Path.GetFullPath(path); if (!File.Exists(path)) throw new FileNotFoundException("The ADT height-edit plan does not exist.", path);
        var plan = JsonSerializer.Deserialize<AdtHeightEditPlan>(File.ReadAllBytes(path), JsonOptions) ?? throw new InvalidDataException("The ADT height-edit plan is empty."); ValidatePlan(plan, verifySource: true); return plan;
    }

    public static AdtHeightEditResult Apply(AdtHeightEditPlan plan, string outputPath, bool overwrite = false)
    {
        ValidatePlan(plan, verifySource: true); outputPath = Path.GetFullPath(outputPath);
        if (outputPath.Equals(Path.GetFullPath(plan.InputPath), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Crucible does not overwrite the source ADT; choose a separate output path so the edit remains reversible.");
        if (File.Exists(outputPath) && !overwrite) throw new IOException($"Output ADT already exists: {outputPath}");
        var source = File.ReadAllBytes(plan.InputPath); var locations = LocateCells(plan.InputPath);
        foreach (var edit in plan.Cells)
        {
            if (!locations.TryGetValue((edit.X, edit.Y), out var current) || current.Offset != edit.BaseHeightOffset || BitConverter.SingleToInt32Bits(current.BaseHeight) != BitConverter.SingleToInt32Bits(edit.OriginalBaseHeight))
                throw new InvalidDataException($"ADT cell {edit.X},{edit.Y} no longer matches the planned byte offset/base height.");
            BitConverter.GetBytes(edit.OriginalBaseHeight + plan.Delta).CopyTo(source, checked((int)edit.BaseHeightOffset));
        }
        AtomicWrite(outputPath, source, overwrite); var inspection = MapAssetInspectionService.Inspect(outputPath);
        foreach (var edit in plan.Cells)
        {
            var cell = inspection.Cells.Single(candidate => candidate.X == edit.X && candidate.Y == edit.Y && candidate.Present);
            if (cell.MinimumHeight is not { } minimum || cell.MaximumHeight is not { } maximum || Math.Abs(minimum - edit.EditedMinimumHeight) > 0.001f || Math.Abs(maximum - edit.EditedMaximumHeight) > 0.001f)
                throw new InvalidDataException($"Written ADT cell {edit.X},{edit.Y} did not re-parse to the planned height range.");
        }
        var hash = Sha256(outputPath); var receiptPath = outputPath + ".crucible-map-edit.json";
        var receipt = new { FormatVersion, AppliedUtc = DateTimeOffset.UtcNow, plan.InputPath, plan.InputSha256, OutputPath = outputPath, OutputSha256 = hash, plan.Delta, plan.Cells };
        AtomicWrite(receiptPath, JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions), overwrite: true);
        return new(outputPath, hash, receiptPath, inspection, plan.Cells.Count, plan.Delta);
    }

    private static Dictionary<(int X, int Y), (long Offset, float BaseHeight)> LocateCells(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, FileOptions.SequentialScan); var result = new Dictionary<(int, int), (long, float)>(); var header = new byte[8];
        while (stream.Position < stream.Length)
        {
            var chunkOffset = stream.Position; if (stream.Read(header) != 8) throw new InvalidDataException($"ADT chunk header is truncated at byte {chunkOffset:N0}.");
            var id = new string(Encoding.ASCII.GetString(header, 0, 4).Reverse().ToArray()); var size = BitConverter.ToUInt32(header, 4); var payload = stream.Position; var end = payload + size;
            if (size > int.MaxValue || end > stream.Length) throw new InvalidDataException($"ADT chunk {id} at byte {chunkOffset:N0} extends beyond the file.");
            if (id == "MCNK")
            {
                if (size < 128) throw new InvalidDataException($"ADT MCNK at byte {chunkOffset:N0} is shorter than its 128-byte header.");
                var data = new byte[128]; stream.ReadExactly(data); var x = BitConverter.ToUInt32(data, 4); var y = BitConverter.ToUInt32(data, 8);
                if (x >= 16 || y >= 16) throw new InvalidDataException($"ADT MCNK at byte {chunkOffset:N0} uses invalid coordinate {x:N0},{y:N0}.");
                var baseHeight = BitConverter.ToSingle(data, 0x70); if (!float.IsFinite(baseHeight)) throw new InvalidDataException($"ADT MCNK {x:N0},{y:N0} has a non-finite base height.");
                if (!result.TryAdd(((int)x, (int)y), (payload + 0x70, baseHeight))) throw new InvalidDataException($"ADT contains duplicate MCNK coordinate {x:N0},{y:N0}.");
            }
            stream.Position = end;
        }
        return result;
    }

    private static void ValidatePlan(AdtHeightEditPlan plan, bool verifySource)
    {
        ArgumentNullException.ThrowIfNull(plan); if (plan.FormatVersion != FormatVersion) throw new InvalidDataException($"Unsupported ADT height-edit plan format {plan.FormatVersion:N0}.");
        if (!float.IsFinite(plan.Delta) || plan.Cells.Count == 0) throw new InvalidDataException("ADT height-edit plan has no finite edit selection.");
        if (plan.Cells.Select(cell => (cell.X, cell.Y)).Distinct().Count() != plan.Cells.Count) throw new InvalidDataException("ADT height-edit plan contains duplicate cells.");
        if (verifySource && !Sha256(plan.InputPath).Equals(plan.InputSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Source ADT hash no longer matches the height-edit plan; rebuild the plan before applying it.");
    }
    private static string Sha256(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void AtomicWrite(string path, byte[] bytes, bool overwrite)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Output path has no parent directory."); Directory.CreateDirectory(directory); var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try { using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); } File.Move(temporary, path, overwrite); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
