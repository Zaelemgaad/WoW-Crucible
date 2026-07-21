using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public sealed record WdtTileEdit(int X, int Y, long FlagsOffset, uint OriginalFlags, uint EditedFlags, uint AsyncId);
public sealed record WdtTileEditPlan(int FormatVersion, DateTimeOffset CreatedUtc, string InputPath, string InputSha256,
    bool Present, int RequestedCells, int AlreadyDesiredCells, long MainPayloadOffset, IReadOnlyList<WdtTileEdit> Edits);
public sealed record WdtTileEditResult(string OutputPath, string OutputSha256, string ReceiptPath, MapAssetInspection Inspection,
    int EditedCells, bool Present);
public sealed record WdtCreateResult(string OutputPath, string OutputSha256, string ReceiptPath, MapAssetInspection Inspection);

/// <summary>
/// Creates and edits the Wrath WDT MAIN 64x64 terrain-tile table while preserving every
/// unrelated byte in an existing WDT. Global-WMO WDTs are deliberately outside this
/// terrain-table workflow because their MWMO/MODF lifecycle has different invariants.
/// </summary>
public static class WdtTileTableService
{
    private const int FormatVersion = 1;
    private const int GridSize = 64;
    private const int MainBytes = GridSize * GridSize * 8;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static WdtTileEditPlan Plan(string inputPath, IEnumerable<(int X, int Y)> cells, bool present)
    {
        inputPath = Path.GetFullPath(inputPath);
        if (!File.Exists(inputPath)) throw new FileNotFoundException("The source WDT does not exist.", inputPath);
        if (!Path.GetExtension(inputPath).Equals(".wdt", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("World-tile table editing requires a WotLK WDT file.");
        var requested = NormalizeCells(cells); if (requested.Length == 0) throw new InvalidOperationException("Select at least one WDT tile.");
        var inspection = MapAssetInspectionService.Inspect(inputPath);
        if (inspection.Kind != MapAssetKind.Wdt || inspection.Version != 18) throw new InvalidDataException("World-tile editing requires a validated WotLK MVER 18 WDT.");
        if ((inspection.HeaderFlags & 1) != 0) throw new NotSupportedException("This WDT declares a global WMO. Terrain MAIN editing is blocked because global-WMO world authoring requires coordinated MWMO/MODF handling.");
        var main = LocateMain(inputPath); var byCoordinate = inspection.Cells.ToDictionary(cell => (cell.X, cell.Y)); var edits = new List<WdtTileEdit>(); var already = 0;
        foreach (var coordinate in requested)
        {
            var cell = byCoordinate[coordinate];
            if (cell.Present == present) { already++; continue; }
            var editedFlags = present ? cell.Flags | 1u : cell.Flags & ~1u;
            edits.Add(new(coordinate.X, coordinate.Y, main.PayloadOffset + (coordinate.Y * GridSize + coordinate.X) * 8L,
                cell.Flags, editedFlags, cell.AsyncId));
        }
        if (edits.Count == 0) throw new InvalidOperationException($"Every selected WDT tile is already {(present ? "present" : "absent")}; no byte change is required.");
        return new(FormatVersion, DateTimeOffset.UtcNow, inputPath, Sha256(inputPath), present, requested.Length, already, main.PayloadOffset, edits);
    }

    public static MapAssetInspection Preview(WdtTileEditPlan plan)
    {
        ValidatePlan(plan, verifySource: true); var inspection = MapAssetInspectionService.Inspect(plan.InputPath); var edits = plan.Edits.ToDictionary(edit => (edit.X, edit.Y));
        return inspection with
        {
            Cells = inspection.Cells.Select(cell => edits.TryGetValue((cell.X, cell.Y), out var edit)
                ? cell with { Present = plan.Present, Flags = edit.EditedFlags }
                : cell).ToArray()
        };
    }

    public static void SavePlan(WdtTileEditPlan plan, string outputPath, bool overwrite = false)
    {
        ValidatePlan(plan, verifySource: true); outputPath = Path.GetFullPath(outputPath);
        if (File.Exists(outputPath) && !overwrite) throw new IOException($"WDT tile-edit plan already exists: {outputPath}");
        AtomicWrite(outputPath, JsonSerializer.SerializeToUtf8Bytes(plan, JsonOptions), overwrite);
    }

    public static WdtTileEditPlan LoadPlan(string path)
    {
        path = Path.GetFullPath(path); if (!File.Exists(path)) throw new FileNotFoundException("The WDT tile-edit plan does not exist.", path);
        var plan = JsonSerializer.Deserialize<WdtTileEditPlan>(File.ReadAllBytes(path), JsonOptions) ?? throw new InvalidDataException("The WDT tile-edit plan is empty.");
        ValidatePlan(plan, verifySource: true); return plan;
    }

    public static WdtTileEditResult Apply(WdtTileEditPlan plan, string outputPath, bool overwrite = false)
    {
        ValidatePlan(plan, verifySource: true); outputPath = Path.GetFullPath(outputPath);
        if (outputPath.Equals(Path.GetFullPath(plan.InputPath), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Crucible does not overwrite the source WDT; choose a separate output path so the edit remains reversible.");
        if (File.Exists(outputPath) && !overwrite) throw new IOException($"Output WDT already exists: {outputPath}");
        var bytes = File.ReadAllBytes(plan.InputPath); var main = LocateMain(plan.InputPath);
        if (main.PayloadOffset != plan.MainPayloadOffset) throw new InvalidDataException("The WDT MAIN payload moved after review.");
        foreach (var edit in plan.Edits)
        {
            var expectedOffset = main.PayloadOffset + (edit.Y * GridSize + edit.X) * 8L;
            if (edit.FlagsOffset != expectedOffset || edit.FlagsOffset < 0 || edit.FlagsOffset + 8 > bytes.LongLength) throw new InvalidDataException($"WDT tile {edit.X},{edit.Y} points outside its reviewed MAIN slot.");
            var offset = checked((int)edit.FlagsOffset); var flags = BitConverter.ToUInt32(bytes, offset); var asyncId = BitConverter.ToUInt32(bytes, offset + 4);
            if (flags != edit.OriginalFlags || asyncId != edit.AsyncId) throw new InvalidDataException($"WDT tile {edit.X},{edit.Y} no longer matches its reviewed flags/async preimage.");
            BitConverter.GetBytes(edit.EditedFlags).CopyTo(bytes, offset);
        }
        AtomicWrite(outputPath, bytes, overwrite); var inspection = MapAssetInspectionService.Inspect(outputPath);
        foreach (var edit in plan.Edits)
        {
            var cell = inspection.Cells.Single(candidate => candidate.X == edit.X && candidate.Y == edit.Y);
            if (cell.Present != plan.Present || cell.Flags != edit.EditedFlags || cell.AsyncId != edit.AsyncId) throw new InvalidDataException($"Written WDT tile {edit.X},{edit.Y} did not re-parse to its reviewed state.");
        }
        var outputSha256 = Sha256(outputPath); var receiptPath = outputPath + ".crucible-wdt-edit.json";
        AtomicWrite(receiptPath, JsonSerializer.SerializeToUtf8Bytes(new
        {
            FormatVersion,
            AppliedUtc = DateTimeOffset.UtcNow,
            plan.InputPath,
            plan.InputSha256,
            OutputPath = outputPath,
            OutputSha256 = outputSha256,
            plan.Present,
            plan.RequestedCells,
            plan.AlreadyDesiredCells,
            plan.Edits
        }, JsonOptions), overwrite: true);
        return new(outputPath, outputSha256, receiptPath, inspection, plan.Edits.Count, plan.Present);
    }

    public static WdtCreateResult Create(string outputPath, IEnumerable<(int X, int Y)> presentCells, bool overwrite = false)
    {
        outputPath = Path.GetFullPath(outputPath); if (!Path.GetExtension(outputPath).Equals(".wdt", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("A new world definition must use the .wdt extension.");
        if (File.Exists(outputPath) && !overwrite) throw new IOException($"Output WDT already exists: {outputPath}");
        var selected = NormalizeCells(presentCells); var main = new byte[MainBytes];
        foreach (var (x, y) in selected) BitConverter.GetBytes(1u).CopyTo(main, (y * GridSize + x) * 8);
        byte[] bytes; using (var memory = new MemoryStream()) using (var writer = new BinaryWriter(memory, Encoding.ASCII, leaveOpen: true))
        {
            WriteChunk(writer, "MVER", BitConverter.GetBytes(18u)); WriteChunk(writer, "MPHD", new byte[32]); WriteChunk(writer, "MAIN", main); writer.Flush(); bytes = memory.ToArray();
        }
        AtomicWrite(outputPath, bytes, overwrite); var inspection = MapAssetInspectionService.Inspect(outputPath);
        if (inspection.Kind != MapAssetKind.Wdt || inspection.Version != 18 || inspection.PresentCells != selected.Length) throw new InvalidDataException("Created WDT did not re-parse to the requested terrain tile table.");
        var outputSha256 = Sha256(outputPath); var receiptPath = outputPath + ".crucible-wdt-create.json";
        AtomicWrite(receiptPath, JsonSerializer.SerializeToUtf8Bytes(new
        {
            FormatVersion,
            CreatedUtc = DateTimeOffset.UtcNow,
            OutputPath = outputPath,
            OutputSha256 = outputSha256,
            PresentCells = selected.Select(cell => new { cell.X, cell.Y }).ToArray()
        }, JsonOptions), overwrite: true);
        return new(outputPath, outputSha256, receiptPath, inspection);
    }

    private static (long PayloadOffset, int Size) LocateMain(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, FileOptions.SequentialScan); var header = new byte[8]; (long PayloadOffset, int Size)? main = null;
        while (stream.Position < stream.Length)
        {
            var headerOffset = stream.Position; if (stream.Read(header) != 8) throw new InvalidDataException($"WDT chunk header is truncated at byte {headerOffset:N0}.");
            var id = new string(Encoding.ASCII.GetString(header, 0, 4).Reverse().ToArray()); var size = BitConverter.ToUInt32(header, 4); var payload = stream.Position; var end = payload + size;
            if (size > int.MaxValue || end > stream.Length) throw new InvalidDataException($"WDT chunk {id} at byte {headerOffset:N0} extends beyond the file.");
            if (id == "MAIN")
            {
                if (main is not null) throw new InvalidDataException("WDT contains multiple MAIN tile tables.");
                if (size != MainBytes) throw new InvalidDataException($"WDT MAIN must contain exactly {MainBytes:N0} bytes.");
                main = (payload, checked((int)size));
            }
            stream.Position = end;
        }
        return main ?? throw new InvalidDataException("WDT has no MAIN tile table.");
    }

    private static (int X, int Y)[] NormalizeCells(IEnumerable<(int X, int Y)> cells)
    {
        ArgumentNullException.ThrowIfNull(cells); var result = cells.Distinct().OrderBy(cell => cell.Y).ThenBy(cell => cell.X).ToArray();
        foreach (var (x, y) in result) if (x is < 0 or >= GridSize || y is < 0 or >= GridSize) throw new ArgumentOutOfRangeException(nameof(cells), $"WDT tile {x},{y} is outside the 64x64 world grid.");
        return result;
    }

    private static void ValidatePlan(WdtTileEditPlan plan, bool verifySource)
    {
        ArgumentNullException.ThrowIfNull(plan); if (plan.FormatVersion != FormatVersion) throw new InvalidDataException($"Unsupported WDT tile-edit plan format {plan.FormatVersion:N0}.");
        if (plan.RequestedCells < plan.Edits.Count || plan.AlreadyDesiredCells != plan.RequestedCells - plan.Edits.Count || plan.Edits.Count == 0) throw new InvalidDataException("WDT tile-edit plan counts are inconsistent or empty.");
        if (plan.Edits.Select(edit => (edit.X, edit.Y)).Distinct().Count() != plan.Edits.Count) throw new InvalidDataException("WDT tile-edit plan contains duplicate tile coordinates.");
        foreach (var edit in plan.Edits)
        {
            if (edit.X is < 0 or >= GridSize || edit.Y is < 0 or >= GridSize || ((edit.EditedFlags & 1) != 0) != plan.Present || (edit.OriginalFlags & ~1u) != (edit.EditedFlags & ~1u)) throw new InvalidDataException($"WDT tile-edit plan has an invalid mutation for {edit.X},{edit.Y}.");
        }
        if (verifySource)
        {
            if (!File.Exists(plan.InputPath) || !Sha256(plan.InputPath).Equals(plan.InputSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Source WDT hash no longer matches the tile-edit plan; rebuild the plan before applying it.");
            var inspection = MapAssetInspectionService.Inspect(plan.InputPath); if ((inspection.HeaderFlags & 1) != 0) throw new NotSupportedException("Global-WMO WDT terrain-table editing remains blocked.");
            var main = LocateMain(plan.InputPath); if (main.PayloadOffset != plan.MainPayloadOffset) throw new InvalidDataException("The planned WDT MAIN payload offset does not match its source.");
            var sourceCells = inspection.Cells.ToDictionary(cell => (cell.X, cell.Y));
            foreach (var edit in plan.Edits)
            {
                var expectedOffset = main.PayloadOffset + (edit.Y * GridSize + edit.X) * 8L; var source = sourceCells[(edit.X, edit.Y)];
                if (edit.FlagsOffset != expectedOffset || source.Flags != edit.OriginalFlags || source.AsyncId != edit.AsyncId) throw new InvalidDataException($"WDT tile {edit.X},{edit.Y} does not match its exact reviewed MAIN preimage/offset.");
            }
        }
    }

    private static void WriteChunk(BinaryWriter writer, string id, byte[] payload)
    {
        writer.Write(Encoding.ASCII.GetBytes(new string(id.Reverse().ToArray()))); writer.Write((uint)payload.Length); writer.Write(payload);
    }
    private static string Sha256(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void AtomicWrite(string path, byte[] bytes, bool overwrite)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Output path has no parent directory."); Directory.CreateDirectory(directory); var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try { using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); } File.Move(temporary, path, overwrite); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
