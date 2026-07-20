using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WoWCrucible.Core;

public enum AdtPlacementLifecycleOperation { Add, Delete }

public sealed record AdtPlacementCellReference(int X, int Y);

public sealed record AdtPlacementLifecyclePlan(
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    string InputPath,
    string InputSha256,
    AdtPlacementLifecycleOperation Operation,
    AdtPlacementKind Kind,
    int Index,
    uint UniqueId,
    uint NameId,
    string? ClientPath,
    AdtPlacementVector Position,
    AdtPlacementVector Orientation,
    ushort ScaleRaw,
    ushort Flags,
    ushort DoodadSet,
    ushort NameSet,
    AdtPlacementVector? MinimumExtent,
    AdtPlacementVector? MaximumExtent,
    string? AssetPath,
    string? AssetSha256,
    AdtPlacementVector? AssetMinimum,
    AdtPlacementVector? AssetMaximum,
    uint AssetPointCount,
    string? OccupancyDirectory,
    string? OccupancyPrefix,
    string? OccupancyFingerprint,
    int OccupancyFilesScanned,
    IReadOnlyList<AdtPlacementCellReference> ReferencedCells,
    bool CoordinatedMultiTile = false);

public sealed record AdtPlacementLifecycleResult(
    string OutputPath,
    string OutputSha256,
    string ReceiptPath,
    MapAssetInspection Inspection,
    AdtPlacementLifecycleOperation Operation,
    AdtPlacementKind Kind,
    int Index,
    uint UniqueId,
    int ReferencedCells);

/// <summary>
/// Adds or removes complete Wrath ADT placements while rebuilding every
/// affected top-level pointer and per-cell MCRF reference. Plans retain exact
/// source/model hashes and are always written to a separate ADT.
/// </summary>
public static partial class AdtPlacementLifecycleService
{
    private sealed class TopChunk(string id, int originalOffset, byte[] bytes)
    {
        public string Id { get; } = id;
        public int OriginalOffset { get; } = originalOffset;
        public byte[] Bytes { get; set; } = bytes;
    }
    private sealed record CellReferences(int X, int Y, uint[] M2, uint[] Wmo);
    private sealed record OccupancyScope(string Directory, string Prefix, IReadOnlyList<string> Files);
    private const int FormatVersion = 1;
    private const int MaximumPlacementFiles = 4_096;
    private const int MaximumPlacementRecords = 500_000;
    private const double TileSize = 1600.0 / 3.0;
    private const double CellSize = 100.0 / 3.0;
    private const double BoundaryTolerance = 0.05;
    private static readonly int[] McnkOffsetFields = [0x14, 0x18, 0x1C, 0x20, 0x24, 0x2C, 0x58, 0x60, 0x74, 0x78];
    private static readonly string[] MhdrTargets = ["MCIN", "MTEX", "MMDX", "MMID", "MWMO", "MWID", "MDDF", "MODF", "MFBO", "MH2O", "MTXF"];
    private static readonly Dictionary<string, int> CanonicalRanks = new(StringComparer.Ordinal)
    {
        ["MVER"] = 0, ["MHDR"] = 1, ["MCIN"] = 2, ["MTEX"] = 3, ["MMDX"] = 4, ["MMID"] = 5,
        ["MWMO"] = 6, ["MWID"] = 7, ["MDDF"] = 8, ["MODF"] = 9, ["MFBO"] = 10, ["MH2O"] = 11, ["MTXF"] = 12, ["MCNK"] = 13
    };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static AdtPlacementLifecyclePlan PlanAdd(string inputPath, AdtPlacementKind kind, string clientPath, string assetPath,
        Vector3 position, Vector3 orientation, ushort? scaleRaw = null, uint? uniqueId = null, ushort flags = 0, ushort doodadSet = 0, ushort nameSet = 0)
        => PlanAddCore(inputPath, kind, clientPath, assetPath, position, orientation, scaleRaw, uniqueId, flags, doodadSet, nameSet, false, null);

    internal static AdtPlacementLifecyclePlan PlanCoordinatedAdd(string inputPath, AdtPlacementKind kind, string clientPath, string assetPath,
        Vector3 position, Vector3 orientation, ushort? scaleRaw, uint uniqueId, ushort flags, ushort doodadSet, ushort nameSet,
        string occupancyDirectory, string occupancyPrefix, IReadOnlyList<string> occupancyFiles)
        => PlanAddCore(inputPath, kind, clientPath, assetPath, position, orientation, scaleRaw, uniqueId, flags, doodadSet, nameSet, true,
            new(Path.GetFullPath(occupancyDirectory), occupancyPrefix, occupancyFiles.Select(Path.GetFullPath).ToArray()));

    private static AdtPlacementLifecyclePlan PlanAddCore(string inputPath, AdtPlacementKind kind, string clientPath, string assetPath,
        Vector3 position, Vector3 orientation, ushort? scaleRaw, uint? uniqueId, ushort flags, ushort doodadSet, ushort nameSet,
        bool coordinatedMultiTile, OccupancyScope? coordinatedScope)
    {
        inputPath = Path.GetFullPath(inputPath); clientPath = NormalizeClientPath(clientPath, kind); assetPath = Path.GetFullPath(assetPath);
        RequireFinite(position, "placement position"); RequireFinite(orientation, "placement orientation");
        var inspection = RequireEditableAdt(inputPath); var chunks = ParseTopChunks(File.ReadAllBytes(inputPath)); ValidateScaffold(chunks);
        EnsurePlacementChunks(chunks, kind); var nameId = ResolveNameId(chunks, kind, clientPath, mutate: false);
        var index = kind == AdtPlacementKind.M2 ? inspection.M2Placements.Count : inspection.WmoPlacements.Count;
        var rawScale = scaleRaw ?? 1024; if (kind == AdtPlacementKind.M2 && rawScale == 0) throw new InvalidDataException("M2 placement scale cannot be zero.");
        if (kind == AdtPlacementKind.M2 && (doodadSet != 0 || nameSet != 0)) throw new InvalidOperationException("Doodad-set and name-set values apply only to WMO MODF records.");
        var scope = coordinatedScope ?? DiscoverOccupancyScope(inputPath); var occupancy = ScanOccupancy(scope); var uid = uniqueId ?? NextUniqueId(occupancy.Maximum);
        if (uid == 0) throw new InvalidDataException("Placement unique ID zero is reserved; choose a positive ID.");
        if (occupancy.Ids.Contains(uid)) throw new InvalidOperationException($"Placement unique ID {uid:N0} is already present in {scope.Prefix}_<x>_<y>.adt within {scope.Directory}.");

        string assetHash; Vector3 assetMinimum; Vector3 assetMaximum; uint points; Vector3 minimum; Vector3 maximum;
        RequireMatchingAssetPath(clientPath, assetPath);
        if (kind == AdtPlacementKind.M2)
        {
            var evidence = M2PlacementBoundsService.InspectModel(assetPath); assetHash = evidence.Sha256; assetMinimum = evidence.ModelMinimum; assetMaximum = evidence.ModelMaximum; points = evidence.VertexCount;
            (minimum, maximum) = M2PlacementBoundsService.Calculate(evidence, position, orientation, rawScale / 1024f);
        }
        else
        {
            var evidence = WmoPlacementBoundsService.InspectRoot(assetPath); assetHash = evidence.Sha256; assetMinimum = evidence.DeclaredMinimum; assetMaximum = evidence.DeclaredMaximum; points = 8;
            var bounds = WmoPlacementBoundsService.Calculate(evidence, position, orientation, EffectiveScale(rawScale)); minimum = bounds.Minimum; maximum = bounds.Maximum;
        }
        var cells = IntersectedCells(inspection, minimum, maximum, coordinatedMultiTile);
        return new(FormatVersion, DateTimeOffset.UtcNow, inputPath, Sha256(inputPath), AdtPlacementLifecycleOperation.Add, kind, index, uid, nameId, clientPath,
            AdtPlacementVector.From(position), AdtPlacementVector.From(orientation), rawScale, flags, doodadSet, nameSet,
            AdtPlacementVector.From(minimum), AdtPlacementVector.From(maximum), assetPath, assetHash, AdtPlacementVector.From(assetMinimum), AdtPlacementVector.From(assetMaximum), points,
            scope.Directory, scope.Prefix, occupancy.Fingerprint, scope.Files.Count, cells, coordinatedMultiTile);
    }

    public static AdtPlacementLifecyclePlan PlanDelete(string inputPath, AdtPlacementKind kind, int index)
        => PlanDeleteCore(inputPath, kind, index, false);

    internal static AdtPlacementLifecyclePlan PlanCoordinatedDelete(string inputPath, AdtPlacementKind kind, int index)
        => PlanDeleteCore(inputPath, kind, index, true);

    private static AdtPlacementLifecyclePlan PlanDeleteCore(string inputPath, AdtPlacementKind kind, int index, bool coordinatedMultiTile)
    {
        inputPath = Path.GetFullPath(inputPath); if (index < 0) throw new ArgumentOutOfRangeException(nameof(index)); var inspection = RequireEditableAdt(inputPath); var chunks = ParseTopChunks(File.ReadAllBytes(inputPath)); ValidateScaffold(chunks);
        var references = ReadAllCellReferences(chunks); var cells = references.Where(cell => (kind == AdtPlacementKind.M2 ? cell.M2 : cell.Wmo).Contains(checked((uint)index))).Select(cell => new AdtPlacementCellReference(cell.X, cell.Y)).OrderBy(cell => cell.Y).ThenBy(cell => cell.X).ToArray();
        if (kind == AdtPlacementKind.M2)
        {
            var value = inspection.M2Placements.ElementAtOrDefault(index) ?? throw new ArgumentOutOfRangeException(nameof(index), $"M2 placement index {index:N0} does not exist.");
            return new(FormatVersion, DateTimeOffset.UtcNow, inputPath, Sha256(inputPath), AdtPlacementLifecycleOperation.Delete, kind, index, value.UniqueId, value.NameId, value.ClientPath,
                AdtPlacementVector.From(value.Position), AdtPlacementVector.From(value.Orientation), value.ScaleRaw, value.Flags, 0, 0, null, null, null, null, null, null, 0, null, null, null, 0, cells, coordinatedMultiTile);
        }
        var wmo = inspection.WmoPlacements.ElementAtOrDefault(index) ?? throw new ArgumentOutOfRangeException(nameof(index), $"WMO placement index {index:N0} does not exist.");
        return new(FormatVersion, DateTimeOffset.UtcNow, inputPath, Sha256(inputPath), AdtPlacementLifecycleOperation.Delete, kind, index, wmo.UniqueId, wmo.NameId, wmo.ClientPath,
            AdtPlacementVector.From(wmo.Position), AdtPlacementVector.From(wmo.Orientation), wmo.ScaleRaw, wmo.Flags, wmo.DoodadSet, wmo.NameSet,
            AdtPlacementVector.From(wmo.MinimumExtent), AdtPlacementVector.From(wmo.MaximumExtent), null, null, null, null, 0, null, null, null, 0, cells, coordinatedMultiTile);
    }

    public static void SavePlan(AdtPlacementLifecyclePlan plan, string path, bool overwrite = false)
    {
        Validate(plan, false, null); path = Path.GetFullPath(path); if (File.Exists(path) && !overwrite) throw new IOException($"Placement lifecycle plan already exists: {path}"); AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(plan, JsonOptions), overwrite);
    }

    public static AdtPlacementLifecyclePlan LoadPlan(string path)
    {
        path = Path.GetFullPath(path); if (!File.Exists(path)) throw new FileNotFoundException("The ADT placement lifecycle plan does not exist.", path);
        var plan = JsonSerializer.Deserialize<AdtPlacementLifecyclePlan>(File.ReadAllBytes(path), JsonOptions) ?? throw new InvalidDataException("The ADT placement lifecycle plan is empty."); Validate(plan, false, null); return plan;
    }

    public static AdtPlacementLifecycleResult Apply(AdtPlacementLifecyclePlan plan, string outputPath, bool overwrite = false)
        => ApplyCore(plan, outputPath, overwrite, false, null, writeReceipt: true);

    internal static AdtPlacementLifecycleResult ApplyCoordinated(AdtPlacementLifecyclePlan plan, string outputPath,
        string occupancyDirectory, string occupancyPrefix, IReadOnlyList<string> occupancyFiles)
        => ApplyCore(plan, outputPath, false, true, new(Path.GetFullPath(occupancyDirectory), occupancyPrefix, occupancyFiles.Select(Path.GetFullPath).ToArray()), writeReceipt: false);

    private static AdtPlacementLifecycleResult ApplyCore(AdtPlacementLifecyclePlan plan, string outputPath, bool overwrite,
        bool allowCoordinated, OccupancyScope? coordinatedScope, bool writeReceipt)
    {
        var before = Validate(plan, allowCoordinated, coordinatedScope); outputPath = Path.GetFullPath(outputPath);
        if (outputPath.Equals(Path.GetFullPath(plan.InputPath), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Crucible does not overwrite the source ADT; choose a separate placement-lifecycle output path.");
        if (File.Exists(outputPath) && !overwrite) throw new IOException($"Output ADT already exists: {outputPath}");
        var chunks = ParseTopChunks(File.ReadAllBytes(plan.InputPath)); ValidateScaffold(chunks); var originalReferences = ReadAllCellReferences(chunks);
        if (plan.Operation == AdtPlacementLifecycleOperation.Add) AddPlacement(chunks, plan); else DeletePlacement(chunks, plan);
        RewriteTopLevelReferences(chunks); var bytes = Concatenate(chunks); AtomicWrite(outputPath, bytes, overwrite);
        var after = MapAssetInspectionService.Inspect(outputPath); VerifyPlacements(before, after, plan); VerifyReferences(originalReferences, ReadAllCellReferences(ParseTopChunks(bytes)), plan);
        var hash = Sha256(outputPath); var receiptPath = writeReceipt ? outputPath + ".crucible-placement-lifecycle.json" : string.Empty;
        if (writeReceipt) AtomicWrite(receiptPath, JsonSerializer.SerializeToUtf8Bytes(new { FormatVersion, AppliedUtc = DateTimeOffset.UtcNow, Plan = plan, OutputPath = outputPath, OutputSha256 = hash }, JsonOptions), true);
        return new(outputPath, hash, receiptPath, after, plan.Operation, plan.Kind, plan.Index, plan.UniqueId, plan.ReferencedCells.Count);
    }

    internal static void ValidateCoordinated(AdtPlacementLifecyclePlan plan, string occupancyDirectory, string occupancyPrefix, IReadOnlyList<string> occupancyFiles)
        => _ = Validate(plan, true, new(Path.GetFullPath(occupancyDirectory), occupancyPrefix, occupancyFiles.Select(Path.GetFullPath).ToArray()));

    private static MapAssetInspection Validate(AdtPlacementLifecyclePlan plan, bool allowCoordinated, OccupancyScope? coordinatedScope)
    {
        ArgumentNullException.ThrowIfNull(plan); if (plan.FormatVersion != FormatVersion || !Enum.IsDefined(plan.Operation) || !Enum.IsDefined(plan.Kind) || plan.Index < 0 || plan.UniqueId == 0) throw new InvalidDataException("Placement lifecycle plan has an unsupported format, operation, kind, index, or unique ID.");
        if (plan.CoordinatedMultiTile && !allowCoordinated) throw new InvalidOperationException("This placement segment belongs to a coordinated multi-tile transaction and cannot be applied by itself.");
        if (!plan.CoordinatedMultiTile && coordinatedScope is not null) throw new InvalidDataException("A single-tile placement plan cannot be smuggled into a coordinated transaction.");
        RequireFinite(plan.Position.ToVector3(), "planned position"); RequireFinite(plan.Orientation.ToVector3(), "planned orientation");
        var source = RequireEditableAdt(plan.InputPath); if (!Sha256(plan.InputPath).Equals(plan.InputSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Source ADT hash no longer matches the placement lifecycle plan.");
        if (plan.Operation == AdtPlacementLifecycleOperation.Add && plan.ReferencedCells.Count == 0 || plan.ReferencedCells.Any(cell => cell.X is < 0 or >= 16 || cell.Y is < 0 or >= 16) || plan.ReferencedCells.Distinct().Count() != plan.ReferencedCells.Count) throw new InvalidDataException("Placement lifecycle plan has an empty add-cell set, duplicate cells, or an out-of-range MCRF cell.");
        if (plan.Operation == AdtPlacementLifecycleOperation.Delete)
        {
            if (plan.AssetPath is not null || plan.AssetSha256 is not null || plan.AssetMinimum is not null || plan.AssetMaximum is not null || plan.AssetPointCount != 0 || plan.OccupancyDirectory is not null || plan.OccupancyPrefix is not null || plan.OccupancyFingerprint is not null || plan.OccupancyFilesScanned != 0) throw new InvalidDataException("Delete plans cannot contain add-only geometry or occupancy evidence.");
            var rebuilt = PlanDeleteCore(plan.InputPath, plan.Kind, plan.Index, plan.CoordinatedMultiTile); RequireEquivalent(plan, rebuilt); return source;
        }
        if (plan.ClientPath is null || plan.AssetPath is null || plan.AssetSha256 is null || plan.AssetMinimum is null || plan.AssetMaximum is null || plan.AssetPointCount == 0 || plan.OccupancyDirectory is null || plan.OccupancyPrefix is null || plan.OccupancyFingerprint is null || plan.OccupancyFilesScanned <= 0 || plan.MinimumExtent is null || plan.MaximumExtent is null) throw new InvalidDataException("Add plan geometry, bounds, path, or occupancy evidence is incomplete.");
        if (plan.Kind == AdtPlacementKind.M2 && plan.ScaleRaw == 0) throw new InvalidDataException("M2 add plan scale cannot be zero.");
        var rebuiltAdd = PlanAddCore(plan.InputPath, plan.Kind, plan.ClientPath, plan.AssetPath, plan.Position.ToVector3(), plan.Orientation.ToVector3(), plan.ScaleRaw, plan.UniqueId, plan.Flags, plan.DoodadSet, plan.NameSet, plan.CoordinatedMultiTile, coordinatedScope); RequireEquivalent(plan, rebuiltAdd); return source;
    }

    private static void RequireEquivalent(AdtPlacementLifecyclePlan expected, AdtPlacementLifecyclePlan actual)
    {
        if (expected with { CreatedUtc = actual.CreatedUtc, ReferencedCells = actual.ReferencedCells } != actual || !expected.ReferencedCells.SequenceEqual(actual.ReferencedCells)) throw new InvalidDataException("Placement lifecycle plan no longer matches its exact source, asset geometry, UID occupancy, path catalog, or MCRF cell allocation.");
    }

    private static void AddPlacement(List<TopChunk> chunks, AdtPlacementLifecyclePlan plan)
    {
        EnsurePlacementChunks(chunks, plan.Kind); var nameId = ResolveNameId(chunks, plan.Kind, plan.ClientPath!, mutate: true); if (nameId != plan.NameId) throw new InvalidDataException("Placement path catalog allocation changed after planning.");
        var table = Single(chunks, plan.Kind == AdtPlacementKind.M2 ? "MDDF" : "MODF"); var record = BuildRecord(plan); table.Bytes = AppendPayload(table.Bytes, record);
        var selected = plan.ReferencedCells.Select(cell => (cell.X, cell.Y)).ToHashSet();
        foreach (var chunk in chunks.Where(chunk => chunk.Id == "MCNK")) { var refs = ReadCellReferences(chunk); chunk.Bytes = RewriteCellReferences(chunk.Bytes, refs, plan.Kind, plan.Index, add: selected.Contains((refs.X, refs.Y)), delete: false); }
    }

    private static void DeletePlacement(List<TopChunk> chunks, AdtPlacementLifecyclePlan plan)
    {
        var table = Single(chunks, plan.Kind == AdtPlacementKind.M2 ? "MDDF" : "MODF"); var size = plan.Kind == AdtPlacementKind.M2 ? 36 : 64; table.Bytes = RemovePayloadRecord(table.Bytes, plan.Index, size);
        foreach (var chunk in chunks.Where(chunk => chunk.Id == "MCNK")) { var refs = ReadCellReferences(chunk); chunk.Bytes = RewriteCellReferences(chunk.Bytes, refs, plan.Kind, plan.Index, add: false, delete: true); }
    }

    private static byte[] BuildRecord(AdtPlacementLifecyclePlan plan)
    {
        var bytes = new byte[plan.Kind == AdtPlacementKind.M2 ? 36 : 64]; WriteU32(bytes, 0, plan.NameId); WriteU32(bytes, 4, plan.UniqueId); WriteVector(bytes, 8, plan.Position); WriteVector(bytes, 20, plan.Orientation);
        if (plan.Kind == AdtPlacementKind.M2) { WriteU16(bytes, 32, plan.ScaleRaw); WriteU16(bytes, 34, plan.Flags); }
        else { WriteVector(bytes, 32, plan.MinimumExtent!); WriteVector(bytes, 44, plan.MaximumExtent!); WriteU16(bytes, 56, plan.Flags); WriteU16(bytes, 58, plan.DoodadSet); WriteU16(bytes, 60, plan.NameSet); WriteU16(bytes, 62, plan.ScaleRaw); }
        return bytes;
    }

    private static void VerifyPlacements(MapAssetInspection before, MapAssetInspection after, AdtPlacementLifecyclePlan plan)
    {
        var expectedM2 = before.M2Placements.ToList(); var expectedWmo = before.WmoPlacements.ToList();
        if (plan.Operation == AdtPlacementLifecycleOperation.Add)
        {
            if (plan.Kind == AdtPlacementKind.M2) expectedM2.Add(new(plan.Index, plan.NameId, plan.UniqueId, plan.ClientPath, plan.Position.ToVector3(), plan.Orientation.ToVector3(), plan.ScaleRaw, plan.Flags));
            else expectedWmo.Add(new(plan.Index, plan.NameId, plan.UniqueId, plan.ClientPath, plan.Position.ToVector3(), plan.Orientation.ToVector3(), plan.MinimumExtent!.ToVector3(), plan.MaximumExtent!.ToVector3(), plan.Flags, plan.DoodadSet, plan.NameSet, plan.ScaleRaw));
        }
        else if (plan.Kind == AdtPlacementKind.M2) expectedM2.RemoveAt(plan.Index); else expectedWmo.RemoveAt(plan.Index);
        expectedM2 = expectedM2.Select((value, index) => value with { Index = index }).ToList(); expectedWmo = expectedWmo.Select((value, index) => value with { Index = index }).ToList();
        if (!after.M2Placements.SequenceEqual(expectedM2) || !after.WmoPlacements.SequenceEqual(expectedWmo)) throw new InvalidDataException("Written ADT placement tables did not re-parse to the exact reviewed add/delete result.");
    }

    private static void VerifyReferences(IReadOnlyList<CellReferences> before, IReadOnlyList<CellReferences> after, AdtPlacementLifecyclePlan plan)
    {
        if (before.Count != after.Count) throw new InvalidDataException("Written ADT changed the MCNK cell count while editing placement references."); var selected = plan.ReferencedCells.Select(cell => (cell.X, cell.Y)).ToHashSet();
        for (var cell = 0; cell < before.Count; cell++)
        {
            var expectedM2 = RewriteValues(before[cell].M2, plan.Kind == AdtPlacementKind.M2, plan.Index, plan.Operation == AdtPlacementLifecycleOperation.Add && selected.Contains((before[cell].X, before[cell].Y)), plan.Operation == AdtPlacementLifecycleOperation.Delete);
            var expectedWmo = RewriteValues(before[cell].Wmo, plan.Kind == AdtPlacementKind.Wmo, plan.Index, plan.Operation == AdtPlacementLifecycleOperation.Add && selected.Contains((before[cell].X, before[cell].Y)), plan.Operation == AdtPlacementLifecycleOperation.Delete);
            if (after[cell].X != before[cell].X || after[cell].Y != before[cell].Y || !after[cell].M2.SequenceEqual(expectedM2) || !after[cell].Wmo.SequenceEqual(expectedWmo)) throw new InvalidDataException($"Written ADT MCRF references differ from the reviewed result in cell {before[cell].X},{before[cell].Y}.");
        }
    }

    private static uint[] RewriteValues(uint[] source, bool affectedKind, int index, bool add, bool delete)
    {
        if (!affectedKind) return source; var target = checked((uint)index); IEnumerable<uint> values = source;
        if (delete) values = values.Where(value => value != target).Select(value => value > target ? value - 1 : value);
        if (add && !values.Contains(target)) values = values.Append(target); return values.ToArray();
    }

    private static byte[] RewriteCellReferences(byte[] original, CellReferences cell, AdtPlacementKind kind, int index, bool add, bool delete)
    {
        var m2 = RewriteValues(cell.M2, kind == AdtPlacementKind.M2, index, add, delete); var wmo = RewriteValues(cell.Wmo, kind == AdtPlacementKind.Wmo, index, add, delete);
        if (m2.SequenceEqual(cell.M2) && wmo.SequenceEqual(cell.Wmo)) return original;
        var payload = new byte[checked((m2.Length + wmo.Length) * 4)]; var cursor = 0; foreach (var value in m2.Concat(wmo)) { WriteU32(payload, cursor, value); cursor += 4; }
        var mcrf = NewChunk("MCRF", payload); var oldOffset = checked((int)ReadU32(original, 8 + 0x20)); byte[] result;
        if (oldOffset == 0)
        {
            if (cell.M2.Length + cell.Wmo.Length != 0) throw new InvalidDataException($"ADT cell {cell.X},{cell.Y} has placement counts but no MCRF offset.");
            oldOffset = original.Length; result = Insert(original, oldOffset, mcrf); WriteU32(result, 8 + 0x20, checked((uint)oldOffset));
        }
        else
        {
            var oldLength = checked(8 + (int)ReadU32(original, oldOffset + 4)); foreach (var field in McnkOffsetFields.Where(field => field != 0x20)) { var value = ReadU32(original, 8 + field); if (value > oldOffset && value < oldOffset + oldLength) throw new InvalidDataException($"ADT cell {cell.X},{cell.Y} has nested offset field 0x{field:X} overlapping MCRF."); } result = Replace(original, oldOffset, oldLength, mcrf); ShiftMcnkOffsets(result, oldOffset + oldLength, mcrf.Length - oldLength);
        }
        WriteU32(result, 8 + 0x10, checked((uint)m2.Length)); WriteU32(result, 8 + 0x38, checked((uint)wmo.Length)); WriteU32(result, 4, checked((uint)(result.Length - 8))); return result;
    }

    private static IReadOnlyList<CellReferences> ReadAllCellReferences(IEnumerable<TopChunk> chunks) => chunks.Where(chunk => chunk.Id == "MCNK").Select(ReadCellReferences).OrderBy(cell => cell.Y).ThenBy(cell => cell.X).ToArray();

    private static CellReferences ReadCellReferences(TopChunk chunk)
    {
        var bytes = chunk.Bytes; Require(bytes, 0, 136, "MCNK header"); var x = checked((int)ReadU32(bytes, 8 + 4)); var y = checked((int)ReadU32(bytes, 8 + 8)); var m2Count = checked((int)ReadU32(bytes, 8 + 0x10)); var wmoCount = checked((int)ReadU32(bytes, 8 + 0x38)); var total = checked(m2Count + wmoCount);
        if (total > MaximumPlacementRecords) throw new InvalidDataException($"ADT cell {x},{y} exceeds the bounded MCRF reference count."); var offset = checked((int)ReadU32(bytes, 8 + 0x20));
        if (offset == 0)
        {
            if (total != 0) throw new InvalidDataException($"ADT cell {x},{y} declares {total:N0} references without MCRF."); return new(x, y, [], []);
        }
        Require(bytes, offset, 8, $"cell {x},{y} MCRF"); if (Decode(bytes.AsSpan(offset, 4)) != "MCRF") throw new InvalidDataException($"ADT cell {x},{y} MCRF offset does not identify MCRF."); var size = checked((int)ReadU32(bytes, offset + 4)); if (size != total * 4) throw new InvalidDataException($"ADT cell {x},{y} MCRF size {size:N0} does not equal its {total:N0} declared references."); Require(bytes, offset + 8, size, $"cell {x},{y} MCRF payload");
        var m2 = new uint[m2Count]; var wmo = new uint[wmoCount]; for (var index = 0; index < total; index++) { var value = ReadU32(bytes, offset + 8 + index * 4); if (index < m2Count) m2[index] = value; else wmo[index - m2Count] = value; } return new(x, y, m2, wmo);
    }

    private static IReadOnlyList<AdtPlacementCellReference> IntersectedCells(MapAssetInspection inspection, Vector3 minimum, Vector3 maximum, bool coordinatedMultiTile)
    {
        RequireFinite(minimum, "placement minimum extent"); RequireFinite(maximum, "placement maximum extent"); if (minimum.X > maximum.X || minimum.Y > maximum.Y || minimum.Z > maximum.Z) throw new InvalidDataException("Placement geometry produced reversed bounds.");
        if (inspection.TileX is not { } tileX || inspection.TileY is not { } tileY) throw new InvalidDataException("Placement creation requires an ADT named <map>_<tileX>_<tileY>.adt so cell ownership can be proven.");
        var tileMinimumX = tileX * TileSize; var tileMinimumZ = tileY * TileSize; var tileMaximumX = tileMinimumX + TileSize; var tileMaximumZ = tileMinimumZ + TileSize;
        if (!coordinatedMultiTile && (minimum.X < tileMinimumX - BoundaryTolerance || maximum.X > tileMaximumX + BoundaryTolerance || minimum.Z < tileMinimumZ - BoundaryTolerance || maximum.Z > tileMaximumZ + BoundaryTolerance)) throw new InvalidOperationException($"Placement bounds {minimum.X:0.###},{minimum.Z:0.###}..{maximum.X:0.###},{maximum.Z:0.###} cross outside ADT tile {tileX},{tileY} ({tileMinimumX:0.###},{tileMinimumZ:0.###}..{tileMaximumX:0.###},{tileMaximumZ:0.###}). Use the coordinated multi-tile placement workflow for this object.");
        var present = inspection.Cells.Where(cell => cell.Present).Select(cell => (cell.X, cell.Y)).ToHashSet(); var result = new List<AdtPlacementCellReference>();
        for (var y = 0; y < 16; y++) for (var x = 0; x < 16; x++)
        {
            var cellMinX = tileMinimumX + x * CellSize; var cellMaxX = cellMinX + CellSize; var cellMinZ = tileMinimumZ + y * CellSize; var cellMaxZ = cellMinZ + CellSize;
            if (maximum.X < cellMinX - BoundaryTolerance || minimum.X > cellMaxX + BoundaryTolerance || maximum.Z < cellMinZ - BoundaryTolerance || minimum.Z > cellMaxZ + BoundaryTolerance) continue;
            if (!present.Contains((x, y))) throw new InvalidDataException($"Placement bounds intersect absent ADT cell {x},{y}."); result.Add(new(x, y));
        }
        return result.Count == 0 ? throw new InvalidDataException("Placement bounds did not intersect any ADT cell.") : result;
    }

    private static void EnsurePlacementChunks(List<TopChunk> chunks, AdtPlacementKind kind)
    {
        foreach (var id in kind == AdtPlacementKind.M2 ? new[] { "MMDX", "MMID", "MDDF" } : new[] { "MWMO", "MWID", "MODF" }) EnsureChunk(chunks, id);
    }

    private static void EnsureChunk(List<TopChunk> chunks, string id)
    {
        if (chunks.Count(chunk => chunk.Id == id) > 1) throw new InvalidDataException($"ADT contains multiple {id} chunks."); if (chunks.Any(chunk => chunk.Id == id)) return;
        var rank = CanonicalRanks[id]; var insertion = chunks.FindIndex(chunk => CanonicalRanks.GetValueOrDefault(chunk.Id, int.MaxValue) > rank); if (insertion < 0) insertion = chunks.Count; chunks.Insert(insertion, new(id, -1, NewChunk(id, [])));
    }

    private static uint ResolveNameId(List<TopChunk> chunks, AdtPlacementKind kind, string clientPath, bool mutate)
    {
        var stringChunk = Single(chunks, kind == AdtPlacementKind.M2 ? "MMDX" : "MWMO"); var indexChunk = Single(chunks, kind == AdtPlacementKind.M2 ? "MMID" : "MWID"); var strings = ReadStrings(stringChunk.Bytes); var offsets = ReadIndex(indexChunk.Bytes);
        for (var nameId = 0; nameId < offsets.Length; nameId++) if (strings.TryGetValue(offsets[nameId], out var value) && value.Equals(clientPath, StringComparison.OrdinalIgnoreCase)) return checked((uint)nameId);
        var stringOffset = strings.FirstOrDefault(pair => pair.Value.Equals(clientPath, StringComparison.OrdinalIgnoreCase)).Key; var found = strings.Any(pair => pair.Value.Equals(clientPath, StringComparison.OrdinalIgnoreCase));
        if (!found) stringOffset = checked((uint)(stringChunk.Bytes.Length - 8 + (stringChunk.Bytes.Length > 8 && stringChunk.Bytes[^1] != 0 ? 1 : 0)));
        var result = checked((uint)offsets.Length); if (!mutate) return result;
        if (!found) stringChunk.Bytes = AppendString(stringChunk.Bytes, clientPath); indexChunk.Bytes = AppendPayload(indexChunk.Bytes, BitConverter.GetBytes(stringOffset)); return result;
    }

    private static Dictionary<uint, string> ReadStrings(byte[] chunk)
    {
        var result = new Dictionary<uint, string>(); var payload = chunk.AsSpan(8); var start = 0; for (var index = 0; index <= payload.Length; index++) if (index == payload.Length || payload[index] == 0) { if (index > start) result[checked((uint)start)] = Encoding.UTF8.GetString(payload[start..index]); start = index + 1; } return result;
    }

    private static uint[] ReadIndex(byte[] chunk)
    {
        if ((chunk.Length - 8) % 4 != 0) throw new InvalidDataException($"ADT {Decode(chunk.AsSpan(0, 4))} payload is not divisible by four bytes."); var result = new uint[(chunk.Length - 8) / 4]; for (var index = 0; index < result.Length; index++) result[index] = ReadU32(chunk, 8 + index * 4); return result;
    }

    private static void RewriteTopLevelReferences(List<TopChunk> chunks)
    {
        var offsets = new int[chunks.Count]; var position = 0; for (var index = 0; index < chunks.Count; index++) { offsets[index] = position; position = checked(position + chunks[index].Bytes.Length); }
        var mhdrIndex = SingleIndex(chunks, "MHDR"); var mhdr = chunks[mhdrIndex].Bytes; var mhdrBase = offsets[mhdrIndex] + 8;
        for (var field = 0; field < MhdrTargets.Length; field++) { var targets = chunks.Select((chunk, index) => (chunk, index)).Where(value => value.chunk.Id == MhdrTargets[field]).ToArray(); if (targets.Length > 1) throw new InvalidDataException($"ADT contains multiple {MhdrTargets[field]} chunks."); WriteU32(mhdr, 8 + 4 + field * 4, targets.Length == 0 ? 0u : checked((uint)(offsets[targets[0].index] - mhdrBase))); }
        var mcinIndex = SingleIndex(chunks, "MCIN"); var mcin = chunks[mcinIndex].Bytes; var original = chunks.Select((chunk, index) => (chunk, index)).Where(value => value.chunk.Id == "MCNK").ToDictionary(value => value.chunk.OriginalOffset, value => value.index);
        for (var entry = 0; entry < 256; entry++) { var at = 8 + entry * 16; var old = ReadU32(mcin, at); if (old == 0) continue; if (!original.TryGetValue(checked((int)old), out var index)) throw new InvalidDataException($"MCIN entry {entry} points to absent original MCNK byte {old:N0}."); WriteU32(mcin, at, checked((uint)offsets[index])); WriteU32(mcin, at + 4, checked((uint)chunks[index].Bytes.Length)); }
    }

    private static List<TopChunk> ParseTopChunks(byte[] bytes)
    {
        var result = new List<TopChunk>(); var position = 0; while (position < bytes.Length) { Require(bytes, position, 8, "top-level ADT chunk"); var id = Decode(bytes.AsSpan(position, 4)); var size = ReadU32(bytes, position + 4); var length = checked((int)size + 8); Require(bytes, position, length, $"ADT chunk {id}"); result.Add(new(id, position, bytes.AsSpan(position, length).ToArray())); position += length; } return result;
    }

    private static void ValidateScaffold(List<TopChunk> chunks)
    {
        foreach (var id in new[] { "MVER", "MHDR", "MCIN" }) if (chunks.Count(chunk => chunk.Id == id) != 1) throw new InvalidDataException($"Placement lifecycle editing requires exactly one {id} chunk.");
        if (Single(chunks, "MVER").Bytes.Length != 12 || ReadU32(Single(chunks, "MVER").Bytes, 8) != 18) throw new InvalidDataException("Placement lifecycle editing requires WotLK MVER 18.");
        if (Single(chunks, "MHDR").Bytes.Length != 72 || Single(chunks, "MCIN").Bytes.Length != 4104) throw new InvalidDataException("Placement lifecycle editing requires complete 64-byte MHDR and 4,096-byte MCIN payloads.");
        var cells = chunks.Where(chunk => chunk.Id == "MCNK").ToArray(); if (cells.Length != 256) throw new InvalidDataException($"Placement lifecycle editing requires a complete monolithic 256-cell ADT; this file has {cells.Length:N0} MCNK chunks.");
        var coordinates = cells.Select(chunk => (X: ReadU32(chunk.Bytes, 12), Y: ReadU32(chunk.Bytes, 16))).ToArray(); if (coordinates.Any(cell => cell.X >= 16 || cell.Y >= 16) || coordinates.Distinct().Count() != 256) throw new InvalidDataException("ADT MCNK coordinates are duplicate or outside the 16x16 grid.");
        var mcin = Single(chunks, "MCIN").Bytes; var byOffset = cells.ToDictionary(cell => checked((uint)cell.OriginalOffset)); for (var entry = 0; entry < 256; entry++) { var offset = ReadU32(mcin, 8 + entry * 16); var size = ReadU32(mcin, 12 + entry * 16); if (!byOffset.TryGetValue(offset, out var cell) || size != cell.Bytes.Length || ReadU32(cell.Bytes, 12) != entry % 16 || ReadU32(cell.Bytes, 16) != entry / 16) throw new InvalidDataException($"MCIN entry {entry:N0} does not identify the exact original MCNK coordinate and size."); }
        var mhdr = Single(chunks, "MHDR"); var mhdrBase = mhdr.OriginalOffset + 8; for (var field = 0; field < MhdrTargets.Length; field++) { var targets = chunks.Where(chunk => chunk.Id == MhdrTargets[field]).ToArray(); if (targets.Length > 1) throw new InvalidDataException($"ADT contains multiple {MhdrTargets[field]} chunks."); var stored = ReadU32(mhdr.Bytes, 8 + 4 + field * 4); var expected = targets.Length == 0 ? 0u : checked((uint)(targets[0].OriginalOffset - mhdrBase)); if (stored != expected) throw new InvalidDataException($"MHDR {MhdrTargets[field]} offset {stored:N0} does not identify its exact top-level chunk preimage {expected:N0}."); }
        var m2Count = PlacementCount(chunks, "MDDF", 36); var wmoCount = PlacementCount(chunks, "MODF", 64); var references = ReadAllCellReferences(chunks);
        var invalidM2 = references.SelectMany(cell => cell.M2.Select(index => (cell.X, cell.Y, Index: index))).FirstOrDefault(value => value.Index >= m2Count); if (invalidM2.Index >= m2Count && references.Any(cell => cell.M2.Length > 0)) throw new InvalidDataException($"ADT cell {invalidM2.X},{invalidM2.Y} references MDDF index {invalidM2.Index:N0}, outside {m2Count:N0} records.");
        var invalidWmo = references.SelectMany(cell => cell.Wmo.Select(index => (cell.X, cell.Y, Index: index))).FirstOrDefault(value => value.Index >= wmoCount); if (invalidWmo.Index >= wmoCount && references.Any(cell => cell.Wmo.Length > 0)) throw new InvalidDataException($"ADT cell {invalidWmo.X},{invalidWmo.Y} references MODF index {invalidWmo.Index:N0}, outside {wmoCount:N0} records.");
    }

    private static uint PlacementCount(List<TopChunk> chunks, string id, int stride)
    {
        var matches = chunks.Where(chunk => chunk.Id == id).ToArray(); if (matches.Length > 1) throw new InvalidDataException($"ADT contains multiple {id} chunks."); if (matches.Length == 0) return 0; var payload = matches[0].Bytes.Length - 8; if (payload % stride != 0 || payload / stride > MaximumPlacementRecords) throw new InvalidDataException($"ADT {id} placement table has an invalid or excessive size."); return checked((uint)(payload / stride));
    }

    private static MapAssetInspection RequireEditableAdt(string path)
    {
        var inspection = MapAssetInspectionService.Inspect(path); if (inspection.Kind != MapAssetKind.Adt || inspection.Version != 18) throw new InvalidDataException("Placement lifecycle editing requires a validated WotLK MVER 18 ADT."); return inspection;
    }

    private static OccupancyScope DiscoverOccupancyScope(string inputPath)
    {
        var inherited = ReadOccupancyLineage(inputPath); var directory = inherited?.Directory ?? Path.GetDirectoryName(inputPath)!; var match = AdtTileName().Match(Path.GetFileName(inputPath)); var prefix = inherited?.Prefix ?? (match.Success ? match.Groups[1].Value : Path.GetFileNameWithoutExtension(inputPath));
        var files = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, prefix + "_*_*.adt", SearchOption.TopDirectoryOnly).Where(path => { var candidate = AdtTileName().Match(Path.GetFileName(path)); return candidate.Success && candidate.Groups[1].Value.Equals(prefix, StringComparison.OrdinalIgnoreCase); }).Order(StringComparer.OrdinalIgnoreCase).ToList() : throw new DirectoryNotFoundException($"Inherited UID occupancy directory does not exist: {directory}");
        if (!files.Contains(inputPath, StringComparer.OrdinalIgnoreCase)) files.Add(inputPath); files = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count is 0 or > MaximumPlacementFiles) throw new InvalidDataException($"UID occupancy scan found {files.Count:N0} sibling ADTs; the safe bound is 1 through {MaximumPlacementFiles:N0}."); return new(directory, prefix, files);
    }

    private static (string Directory, string Prefix)? ReadOccupancyLineage(string inputPath)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var current = Path.GetFullPath(inputPath);
        for (var depth = 0; depth < 32 && visited.Add(current); depth++)
        {
            var receipt = current + ".crucible-placement-lifecycle.json"; if (!File.Exists(receipt))
            {
                if (depth == 0) return null; var match = AdtTileName().Match(Path.GetFileName(current)); return match.Success ? (Path.GetDirectoryName(current)!, match.Groups[1].Value) : null;
            }
            using var document = JsonDocument.Parse(File.ReadAllBytes(receipt)); if (!document.RootElement.TryGetProperty("Plan", out var plan)) throw new InvalidDataException($"Placement lineage receipt has no Plan object: {receipt}");
            if (plan.TryGetProperty("OccupancyDirectory", out var directory) && directory.ValueKind == JsonValueKind.String && plan.TryGetProperty("OccupancyPrefix", out var prefix) && prefix.ValueKind == JsonValueKind.String) return (Path.GetFullPath(directory.GetString()!), prefix.GetString()!);
            if (!plan.TryGetProperty("InputPath", out var parent) || parent.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(parent.GetString())) throw new InvalidDataException($"Placement lineage receipt cannot identify its parent ADT: {receipt}"); current = Path.GetFullPath(parent.GetString()!);
        }
        throw new InvalidDataException("Placement lifecycle receipt lineage is cyclic or exceeds 32 generations.");
    }

    private static (HashSet<uint> Ids, uint Maximum, string Fingerprint) ScanOccupancy(OccupancyScope scope)
    {
        var ids = new HashSet<uint>(); uint maximum = 0; using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); foreach (var path in scope.Files) { var values = ReadPlacementIds(path).ToArray(); var name = Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()); fingerprint.AppendData(BitConverter.GetBytes(name.Length)); fingerprint.AppendData(name); fingerprint.AppendData(BitConverter.GetBytes(values.Length)); foreach (var id in values) { ids.Add(id); maximum = Math.Max(maximum, id); fingerprint.AppendData(BitConverter.GetBytes(id)); } } return (ids, maximum, Convert.ToHexString(fingerprint.GetHashAndReset()));
    }

    private static IEnumerable<uint> ReadPlacementIds(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, FileOptions.SequentialScan); var header = new byte[8]; var records = 0;
        while (stream.Position < stream.Length)
        {
            stream.ReadExactly(header); var id = Decode(header.AsSpan(0, 4)); var size = BitConverter.ToUInt32(header, 4); var end = stream.Position + size; if (size > int.MaxValue || end > stream.Length) throw new InvalidDataException($"ADT occupancy scan found a truncated {id} chunk in {path}.");
            var stride = id == "MDDF" ? 36 : id == "MODF" ? 64 : 0; if (stride != 0) { if (size % stride != 0 || records + size / stride > MaximumPlacementRecords) throw new InvalidDataException($"ADT occupancy scan found an invalid or excessive {id} table in {path}."); var record = new byte[stride]; for (var index = 0; index < size / stride; index++) { stream.ReadExactly(record); yield return BitConverter.ToUInt32(record, 4); records++; } } stream.Position = end;
        }
    }

    private static uint NextUniqueId(uint maximum) => maximum == uint.MaxValue ? throw new InvalidOperationException("The sibling ADT set already uses uint.MaxValue; choose a reviewed unused UID explicitly.") : maximum + 1;
    private static float EffectiveScale(ushort raw) => raw == 0 ? 1f : raw / 1024f;
    private static string NormalizeClientPath(string path, AdtPlacementKind kind) { path = (path ?? string.Empty).Trim().Replace('/', '\\').TrimStart('\\'); var extension = Path.GetExtension(path).ToLowerInvariant(); var accepted = kind == AdtPlacementKind.M2 ? extension is ".m2" or ".mdx" : extension == ".wmo"; if (path.Length == 0 || path.IndexOf('\0') >= 0 || !accepted) throw new ArgumentException($"{kind} placement path must be a client-relative {(kind == AdtPlacementKind.M2 ? ".m2/.mdx" : ".wmo")} path.", nameof(path)); return path; }
    private static void RequireMatchingAssetPath(string clientPath, string assetPath) { if (!Path.GetFileName(clientPath).Equals(Path.GetFileName(assetPath), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Extracted geometry '{Path.GetFileName(assetPath)}' does not match client path '{clientPath}'. Select the exact provenance candidate for this object."); }
    private static void RequireFinite(Vector3 value, string label) { if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z)) throw new InvalidDataException($"{label} contains a non-finite value."); }
    private static TopChunk Single(List<TopChunk> chunks, string id) { var matches = chunks.Where(chunk => chunk.Id == id).ToArray(); return matches.Length == 1 ? matches[0] : throw new InvalidDataException($"ADT must contain exactly one {id} chunk; found {matches.Length:N0}."); }
    private static int SingleIndex(List<TopChunk> chunks, string id) { var matches = chunks.Select((chunk, index) => (chunk, index)).Where(value => value.chunk.Id == id).ToArray(); return matches.Length == 1 ? matches[0].index : throw new InvalidDataException($"ADT must contain exactly one {id} chunk; found {matches.Length:N0}."); }
    private static byte[] NewChunk(string id, byte[] payload) { var result = new byte[8 + payload.Length]; var raw = Encoding.ASCII.GetBytes(new string(id.Reverse().ToArray())); raw.CopyTo(result, 0); WriteU32(result, 4, checked((uint)payload.Length)); payload.CopyTo(result, 8); return result; }
    private static byte[] AppendPayload(byte[] chunk, byte[] payload) { var result = new byte[checked(chunk.Length + payload.Length)]; chunk.CopyTo(result, 0); payload.CopyTo(result, chunk.Length); WriteU32(result, 4, checked((uint)(result.Length - 8))); return result; }
    private static byte[] AppendString(byte[] chunk, string value) { var encoded = Encoding.UTF8.GetBytes(value); var separator = chunk.Length > 8 && chunk[^1] != 0 ? 1 : 0; var result = new byte[checked(chunk.Length + separator + encoded.Length + 1)]; chunk.CopyTo(result, 0); encoded.CopyTo(result, chunk.Length + separator); WriteU32(result, 4, checked((uint)(result.Length - 8))); return result; }
    private static byte[] RemovePayloadRecord(byte[] chunk, int index, int size) { var count = (chunk.Length - 8) / size; if ((chunk.Length - 8) % size != 0 || index < 0 || index >= count) throw new InvalidDataException("Placement record deletion index or table size is invalid."); var result = new byte[chunk.Length - size]; chunk.AsSpan(0, 8 + index * size).CopyTo(result); chunk.AsSpan(8 + (index + 1) * size).CopyTo(result.AsSpan(8 + index * size)); WriteU32(result, 4, checked((uint)(result.Length - 8))); return result; }
    private static byte[] Insert(byte[] source, int offset, byte[] addition) => Replace(source, offset, 0, addition);
    private static byte[] Replace(byte[] source, int offset, int removed, byte[] addition) { Require(source, offset, removed, "replacement range"); var result = new byte[checked(source.Length - removed + addition.Length)]; source.AsSpan(0, offset).CopyTo(result); addition.CopyTo(result, offset); source.AsSpan(offset + removed).CopyTo(result.AsSpan(offset + addition.Length)); return result; }
    private static void ShiftMcnkOffsets(byte[] bytes, int threshold, int delta) { if (delta == 0) return; foreach (var field in McnkOffsetFields) { var at = 8 + field; var value = ReadU32(bytes, at); if (value != 0 && value >= threshold) { var shifted = checked((long)value + delta); if (shifted < 136 || shifted > uint.MaxValue) throw new InvalidDataException("MCNK nested offset shift exceeded its valid range."); WriteU32(bytes, at, checked((uint)shifted)); } } }
    private static byte[] Concatenate(IEnumerable<TopChunk> chunks) { using var stream = new MemoryStream(); foreach (var chunk in chunks) stream.Write(chunk.Bytes); return stream.ToArray(); }
    private static uint ReadU32(byte[] bytes, int offset) { Require(bytes, offset, 4, "32-bit value"); return BitConverter.ToUInt32(bytes, offset); }
    private static void WriteU32(byte[] bytes, int offset, uint value) { Require(bytes, offset, 4, "32-bit value"); BitConverter.GetBytes(value).CopyTo(bytes, offset); }
    private static void WriteU16(byte[] bytes, int offset, ushort value) { Require(bytes, offset, 2, "16-bit value"); BitConverter.GetBytes(value).CopyTo(bytes, offset); }
    private static void WriteVector(byte[] bytes, int offset, AdtPlacementVector value) { BitConverter.GetBytes(value.X).CopyTo(bytes, offset); BitConverter.GetBytes(value.Y).CopyTo(bytes, offset + 4); BitConverter.GetBytes(value.Z).CopyTo(bytes, offset + 8); }
    private static string Decode(ReadOnlySpan<byte> raw) => new string(Encoding.ASCII.GetString(raw).Reverse().ToArray());
    private static string Sha256(string path) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void Require(byte[] bytes, long offset, long length, string label) { if (offset < 0 || length < 0 || offset + length > bytes.LongLength) throw new InvalidDataException($"{label} at byte {offset:N0} extends beyond the file."); }
    private static void AtomicWrite(string path, byte[] bytes, bool overwrite) { var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Output path has no parent directory."); Directory.CreateDirectory(directory); var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp"); try { using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); } File.Move(temporary, path, overwrite); } finally { if (File.Exists(temporary)) File.Delete(temporary); } }

    [GeneratedRegex(@"^(.+)_([0-9]+)_([0-9]+)(?:-[^\\/.]+)?\.adt$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AdtTileName();
}
