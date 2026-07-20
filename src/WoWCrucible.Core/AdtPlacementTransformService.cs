using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public enum AdtPlacementKind { M2, Wmo }

public sealed record AdtPlacementVector(float X, float Y, float Z)
{
    public Vector3 ToVector3() => new(X, Y, Z);
    public static AdtPlacementVector From(Vector3 value) => new(value.X, value.Y, value.Z);
}

public sealed record AdtPlacementTransformPlan(
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    string InputPath,
    string InputSha256,
    AdtPlacementKind Kind,
    int Index,
    uint UniqueId,
    uint NameId,
    string? ClientPath,
    AdtPlacementVector OriginalPosition,
    AdtPlacementVector OriginalOrientation,
    ushort OriginalScaleRaw,
    AdtPlacementVector EditedPosition,
    AdtPlacementVector EditedOrientation,
    ushort EditedScaleRaw,
    AdtPlacementVector? OriginalMinimumExtent,
    AdtPlacementVector? OriginalMaximumExtent,
    AdtPlacementVector? EditedMinimumExtent,
    AdtPlacementVector? EditedMaximumExtent);

public sealed record AdtPlacementTransformResult(
    string OutputPath,
    string OutputSha256,
    string ReceiptPath,
    MapAssetInspection Inspection,
    AdtPlacementKind Kind,
    int Index,
    uint UniqueId);

/// <summary>
/// Safely edits an existing Wrath ADT placement without rebuilding unrelated
/// chunks. M2 placements may move, rotate, and scale. WMO placements may move;
/// their already-world-space MODF extents are translated by the exact delta.
/// WMO rotation/scale requires source-geometry bound extent reconstruction and
/// is deliberately refused here instead of leaving stale collision bounds.
/// </summary>
public static class AdtPlacementTransformService
{
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static ushort EncodeScale(float scale)
    {
        if (!float.IsFinite(scale) || scale <= 0 || scale > ushort.MaxValue / 1024f)
            throw new ArgumentOutOfRangeException(nameof(scale), $"Placement scale must be finite and from {1f / 1024f:R} through {ushort.MaxValue / 1024f:R}.");
        var raw = checked((int)MathF.Round(scale * 1024f, MidpointRounding.AwayFromZero));
        if (raw is < 1 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(scale), "Placement scale cannot be represented by the Wrath 16-bit fixed-point field.");
        return (ushort)raw;
    }

    public static AdtPlacementTransformPlan Plan(string inputPath, AdtPlacementKind kind, int index,
        Vector3? position = null, Vector3? orientation = null, ushort? scaleRaw = null)
    {
        inputPath = Path.GetFullPath(inputPath);
        var inspection = MapAssetInspectionService.Inspect(inputPath);
        if (inspection.Kind != MapAssetKind.Adt || inspection.Version != 18)
            throw new InvalidDataException("Placement editing requires a validated WotLK MVER 18 ADT.");
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index), "Placement index must be non-negative.");

        if (kind == AdtPlacementKind.M2)
        {
            var source = inspection.M2Placements.ElementAtOrDefault(index) ?? throw new ArgumentOutOfRangeException(nameof(index), $"M2 placement index {index:N0} does not exist; this ADT has {inspection.M2Placements.Count:N0} M2 placements.");
            var editedPosition = position ?? source.Position; var editedOrientation = orientation ?? source.Orientation; var editedScale = scaleRaw ?? source.ScaleRaw;
            RequireFinite(editedPosition, "edited M2 position"); RequireFinite(editedOrientation, "edited M2 orientation");
            if (editedScale == 0) throw new InvalidDataException("M2 placement scale cannot be zero; that would make the object disappear.");
            if (Same(source.Position, editedPosition) && Same(source.Orientation, editedOrientation) && source.ScaleRaw == editedScale)
                throw new InvalidOperationException("The edited M2 transform is byte-identical to the current placement; no plan is required.");
            return new(FormatVersion, DateTimeOffset.UtcNow, inspection.Path, Sha256(inspection.Path), kind, index, source.UniqueId, source.NameId, source.ClientPath,
                AdtPlacementVector.From(source.Position), AdtPlacementVector.From(source.Orientation), source.ScaleRaw,
                AdtPlacementVector.From(editedPosition), AdtPlacementVector.From(editedOrientation), editedScale,
                null, null, null, null);
        }

        var wmo = inspection.WmoPlacements.ElementAtOrDefault(index) ?? throw new ArgumentOutOfRangeException(nameof(index), $"WMO placement index {index:N0} does not exist; this ADT has {inspection.WmoPlacements.Count:N0} WMO placements.");
        var wmoPosition = position ?? wmo.Position; var wmoOrientation = orientation ?? wmo.Orientation; var wmoScale = scaleRaw ?? wmo.ScaleRaw;
        RequireFinite(wmoPosition, "edited WMO position"); RequireFinite(wmoOrientation, "edited WMO orientation");
        if (!Same(wmoOrientation, wmo.Orientation) || wmoScale != wmo.ScaleRaw)
            throw new NotSupportedException("This guarded path moves WMO placements only. Rotation or scale would require recomputing MODF world extents from the exact WMO geometry, so Crucible refused to leave stale collision/visibility bounds.");
        if (Same(wmoPosition, wmo.Position)) throw new InvalidOperationException("The edited WMO position is byte-identical to the current placement; no plan is required.");
        var delta = wmoPosition - wmo.Position; var editedMinimum = wmo.MinimumExtent + delta; var editedMaximum = wmo.MaximumExtent + delta;
        RequireFinite(editedMinimum, "edited WMO minimum extent"); RequireFinite(editedMaximum, "edited WMO maximum extent");
        return new(FormatVersion, DateTimeOffset.UtcNow, inspection.Path, Sha256(inspection.Path), kind, index, wmo.UniqueId, wmo.NameId, wmo.ClientPath,
            AdtPlacementVector.From(wmo.Position), AdtPlacementVector.From(wmo.Orientation), wmo.ScaleRaw,
            AdtPlacementVector.From(wmoPosition), AdtPlacementVector.From(wmo.Orientation), wmo.ScaleRaw,
            AdtPlacementVector.From(wmo.MinimumExtent), AdtPlacementVector.From(wmo.MaximumExtent), AdtPlacementVector.From(editedMinimum), AdtPlacementVector.From(editedMaximum));
    }

    public static void SavePlan(AdtPlacementTransformPlan plan, string path, bool overwrite = false)
    {
        _ = Validate(plan); path = Path.GetFullPath(path);
        if (File.Exists(path) && !overwrite) throw new IOException($"Placement transform plan already exists: {path}");
        AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(plan, JsonOptions), overwrite);
    }

    public static AdtPlacementTransformPlan LoadPlan(string path)
    {
        path = Path.GetFullPath(path); if (!File.Exists(path)) throw new FileNotFoundException("The ADT placement transform plan does not exist.", path);
        var plan = JsonSerializer.Deserialize<AdtPlacementTransformPlan>(File.ReadAllBytes(path), JsonOptions) ?? throw new InvalidDataException("The ADT placement transform plan is empty.");
        _ = Validate(plan); return plan;
    }

    public static AdtPlacementTransformResult Apply(AdtPlacementTransformPlan plan, string outputPath, bool overwrite = false)
    {
        var before = Validate(plan); outputPath = Path.GetFullPath(outputPath);
        if (outputPath.Equals(Path.GetFullPath(plan.InputPath), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Crucible does not overwrite the source ADT; choose a separate output path so the placement edit remains reversible.");
        if (File.Exists(outputPath) && !overwrite) throw new IOException($"Output ADT already exists: {outputPath}");
        var source = File.ReadAllBytes(plan.InputPath); var location = LocatePlacement(source, plan.Kind, plan.Index);
        VerifyVector(source, location + 8, plan.OriginalPosition, "position"); VerifyVector(source, location + 20, plan.OriginalOrientation, "orientation");
        var scaleOffset = location + (plan.Kind == AdtPlacementKind.M2 ? 32 : 62);
        if (BitConverter.ToUInt16(source, scaleOffset) != plan.OriginalScaleRaw) throw new InvalidDataException("Placement scale no longer matches the reviewed byte preimage.");
        if (plan.Kind == AdtPlacementKind.Wmo)
        {
            VerifyVector(source, location + 32, plan.OriginalMinimumExtent!, "minimum extent"); VerifyVector(source, location + 44, plan.OriginalMaximumExtent!, "maximum extent");
        }
        WriteVector(source, location + 8, plan.EditedPosition); WriteVector(source, location + 20, plan.EditedOrientation); BitConverter.GetBytes(plan.EditedScaleRaw).CopyTo(source, scaleOffset);
        if (plan.Kind == AdtPlacementKind.Wmo)
        {
            WriteVector(source, location + 32, plan.EditedMinimumExtent!); WriteVector(source, location + 44, plan.EditedMaximumExtent!);
        }
        AtomicWrite(outputPath, source, overwrite); var after = MapAssetInspectionService.Inspect(outputPath); VerifyOutput(plan, before, after);
        var hash = Sha256(outputPath); var receiptPath = outputPath + ".crucible-placement-edit.json";
        AtomicWrite(receiptPath, JsonSerializer.SerializeToUtf8Bytes(new { FormatVersion, AppliedUtc = DateTimeOffset.UtcNow, Plan = plan, OutputPath = outputPath, OutputSha256 = hash }, JsonOptions), true);
        return new(outputPath, hash, receiptPath, after, plan.Kind, plan.Index, plan.UniqueId);
    }

    private static MapAssetInspection Validate(AdtPlacementTransformPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan); if (plan.FormatVersion != FormatVersion) throw new InvalidDataException($"Unsupported ADT placement transform format {plan.FormatVersion:N0}.");
        if (plan.Index < 0) throw new InvalidDataException("Placement plan index must be non-negative.");
        RequireFinite(plan.OriginalPosition.ToVector3(), "original position"); RequireFinite(plan.OriginalOrientation.ToVector3(), "original orientation"); RequireFinite(plan.EditedPosition.ToVector3(), "edited position"); RequireFinite(plan.EditedOrientation.ToVector3(), "edited orientation");
        var inspection = MapAssetInspectionService.Inspect(plan.InputPath); if (inspection.Kind != MapAssetKind.Adt || inspection.Version != 18) throw new InvalidDataException("Placement plan source is not a validated WotLK MVER 18 ADT.");
        if (!Sha256(plan.InputPath).Equals(plan.InputSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Source ADT hash no longer matches the placement plan; rebuild the plan before applying it.");
        if (plan.Kind == AdtPlacementKind.M2)
        {
            var source = inspection.M2Placements.ElementAtOrDefault(plan.Index) ?? throw new InvalidDataException("The planned M2 placement index no longer exists.");
            if (source.UniqueId != plan.UniqueId || source.NameId != plan.NameId || !string.Equals(source.ClientPath, plan.ClientPath, StringComparison.OrdinalIgnoreCase) || !Same(source.Position, plan.OriginalPosition.ToVector3()) || !Same(source.Orientation, plan.OriginalOrientation.ToVector3()) || source.ScaleRaw != plan.OriginalScaleRaw || plan.EditedScaleRaw == 0)
                throw new InvalidDataException("The planned M2 placement identity or transform preimage no longer matches the source ADT.");
            if (plan.OriginalMinimumExtent is not null || plan.OriginalMaximumExtent is not null || plan.EditedMinimumExtent is not null || plan.EditedMaximumExtent is not null) throw new InvalidDataException("M2 placement plans cannot contain MODF extent edits.");
            if (Same(source.Position, plan.EditedPosition.ToVector3()) && Same(source.Orientation, plan.EditedOrientation.ToVector3()) && source.ScaleRaw == plan.EditedScaleRaw) throw new InvalidDataException("M2 placement plan contains no transform change.");
        }
        else
        {
            var source = inspection.WmoPlacements.ElementAtOrDefault(plan.Index) ?? throw new InvalidDataException("The planned WMO placement index no longer exists.");
            if (source.UniqueId != plan.UniqueId || source.NameId != plan.NameId || !string.Equals(source.ClientPath, plan.ClientPath, StringComparison.OrdinalIgnoreCase) || !Same(source.Position, plan.OriginalPosition.ToVector3()) || !Same(source.Orientation, plan.OriginalOrientation.ToVector3()) || source.ScaleRaw != plan.OriginalScaleRaw)
                throw new InvalidDataException("The planned WMO placement identity or transform preimage no longer matches the source ADT.");
            if (!Same(plan.EditedOrientation.ToVector3(), source.Orientation) || plan.EditedScaleRaw != source.ScaleRaw) throw new InvalidDataException("WMO placement plans may translate only; rotation/scale requires geometry-bound extent reconstruction.");
            if (Same(plan.EditedPosition.ToVector3(), source.Position)) throw new InvalidDataException("WMO placement plan contains no position change.");
            if (plan.OriginalMinimumExtent is null || plan.OriginalMaximumExtent is null || plan.EditedMinimumExtent is null || plan.EditedMaximumExtent is null ||
                !Same(source.MinimumExtent, plan.OriginalMinimumExtent.ToVector3()) || !Same(source.MaximumExtent, plan.OriginalMaximumExtent.ToVector3())) throw new InvalidDataException("WMO placement plan extents do not match the source MODF record.");
            var delta = plan.EditedPosition.ToVector3() - source.Position;
            if (!Same(source.MinimumExtent + delta, plan.EditedMinimumExtent.ToVector3()) || !Same(source.MaximumExtent + delta, plan.EditedMaximumExtent.ToVector3())) throw new InvalidDataException("WMO placement plan did not translate both world extents by the exact position delta.");
        }
        return inspection;
    }

    private static void VerifyOutput(AdtPlacementTransformPlan plan, MapAssetInspection before, MapAssetInspection after)
    {
        if (before.M2Placements.Count != after.M2Placements.Count || before.WmoPlacements.Count != after.WmoPlacements.Count) throw new InvalidDataException("Written ADT changed the placement table sizes.");
        for (var index = 0; index < before.M2Placements.Count; index++)
        {
            var expected = before.M2Placements[index]; if (plan.Kind == AdtPlacementKind.M2 && index == plan.Index) expected = expected with { Position = plan.EditedPosition.ToVector3(), Orientation = plan.EditedOrientation.ToVector3(), ScaleRaw = plan.EditedScaleRaw };
            if (after.M2Placements[index] != expected) throw new InvalidDataException($"Written ADT M2 placement {index:N0} did not re-parse to the exact reviewed record.");
        }
        for (var index = 0; index < before.WmoPlacements.Count; index++)
        {
            var expected = before.WmoPlacements[index]; if (plan.Kind == AdtPlacementKind.Wmo && index == plan.Index) expected = expected with { Position = plan.EditedPosition.ToVector3(), MinimumExtent = plan.EditedMinimumExtent!.ToVector3(), MaximumExtent = plan.EditedMaximumExtent!.ToVector3() };
            if (after.WmoPlacements[index] != expected) throw new InvalidDataException($"Written ADT WMO placement {index:N0} did not re-parse to the exact reviewed record.");
        }
    }

    private static int LocatePlacement(byte[] bytes, AdtPlacementKind kind, int index)
    {
        var wanted = kind == AdtPlacementKind.M2 ? "MDDF" : "MODF"; var recordSize = kind == AdtPlacementKind.M2 ? 36 : 64; int? payload = null; var offset = 0;
        while (offset < bytes.Length)
        {
            if (offset + 8 > bytes.Length) throw new InvalidDataException($"ADT chunk header is truncated at byte {offset:N0}.");
            var id = Decode(bytes.AsSpan(offset, 4)); var size = BitConverter.ToUInt32(bytes, offset + 4); var end = offset + 8L + size; if (size > int.MaxValue || end > bytes.LongLength) throw new InvalidDataException($"ADT chunk {id} at byte {offset:N0} extends beyond the file.");
            if (id == wanted) { if (payload is not null) throw new InvalidDataException($"ADT contains more than one {wanted} placement table."); if (size % recordSize != 0) throw new InvalidDataException($"{wanted} size {size:N0} is not divisible by {recordSize:N0}."); if (index >= size / recordSize) throw new InvalidDataException($"{wanted} placement index {index:N0} is outside the table."); payload = checked(offset + 8 + index * recordSize); }
            offset = checked((int)end);
        }
        return payload ?? throw new InvalidDataException($"ADT has no {wanted} placement table.");
    }

    private static void VerifyVector(byte[] bytes, int offset, AdtPlacementVector expected, string label)
    {
        if (BitConverter.SingleToInt32Bits(BitConverter.ToSingle(bytes, offset)) != BitConverter.SingleToInt32Bits(expected.X) || BitConverter.SingleToInt32Bits(BitConverter.ToSingle(bytes, offset + 4)) != BitConverter.SingleToInt32Bits(expected.Y) || BitConverter.SingleToInt32Bits(BitConverter.ToSingle(bytes, offset + 8)) != BitConverter.SingleToInt32Bits(expected.Z))
            throw new InvalidDataException($"Placement {label} no longer matches the reviewed byte preimage.");
    }
    private static void WriteVector(byte[] bytes, int offset, AdtPlacementVector value) { BitConverter.GetBytes(value.X).CopyTo(bytes, offset); BitConverter.GetBytes(value.Y).CopyTo(bytes, offset + 4); BitConverter.GetBytes(value.Z).CopyTo(bytes, offset + 8); }
    private static bool Same(Vector3 left, Vector3 right) => BitConverter.SingleToInt32Bits(left.X) == BitConverter.SingleToInt32Bits(right.X) && BitConverter.SingleToInt32Bits(left.Y) == BitConverter.SingleToInt32Bits(right.Y) && BitConverter.SingleToInt32Bits(left.Z) == BitConverter.SingleToInt32Bits(right.Z);
    private static void RequireFinite(Vector3 value, string label) { if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z)) throw new InvalidDataException($"{label} must contain three finite values."); }
    private static string Decode(ReadOnlySpan<byte> raw) => new string(Encoding.ASCII.GetString(raw).Reverse().ToArray());
    private static string Sha256(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void AtomicWrite(string path, byte[] bytes, bool overwrite)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Output path has no parent directory."); Directory.CreateDirectory(directory); var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try { using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); } File.Move(temporary, path, overwrite); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
