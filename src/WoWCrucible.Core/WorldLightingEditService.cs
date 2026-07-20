using System.Security.Cryptography;
using System.Text.Json;

namespace WoWCrucible.Core;

public enum WorldLightingBandKind { Color, Float }

public sealed record WorldLightingBandKeyEdit(int Time, uint RawValue)
{
    public WorldLightColor Color => WorldLightColor.FromPacked(RawValue);
    public float FloatValue => BitConverter.UInt32BitsToSingle(RawValue);
}

public sealed record WorldLightingBandEditPlan(
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    string InputPath,
    string InputSha256,
    WorldLightingBandKind Kind,
    uint BandId,
    int RowIndex,
    IReadOnlyList<uint> OriginalFields,
    IReadOnlyList<WorldLightingBandKeyEdit> Keys);

public sealed record WorldLightingBandEditResult(
    string OutputPath,
    string OutputSha256,
    string ReceiptPath,
    WorldLightingBandKind Kind,
    uint BandId,
    int Keys,
    bool ReplacedSource,
    string? BackupPath);

public static class WorldLightingEditService
{
    private const int FormatVersion = 1;
    private const int Fields = 34;
    private const int RecordBytes = 136;
    private const int MaximumKeys = 16;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static WorldLightingBandEditPlan PlanColor(string inputPath, uint bandId, IEnumerable<(int Time, WorldLightColor Color)> keys) =>
        PlanRaw(inputPath, WorldLightingBandKind.Color, bandId, keys.Select(key => new WorldLightingBandKeyEdit(key.Time, Pack(key.Color))));

    public static WorldLightingBandEditPlan PlanFloat(string inputPath, uint bandId, IEnumerable<(int Time, float Value)> keys) =>
        PlanRaw(inputPath, WorldLightingBandKind.Float, bandId, keys.Select(key =>
        {
            if (!float.IsFinite(key.Value)) throw new ArgumentOutOfRangeException(nameof(keys), $"Float-band value at time {key.Time:N0} must be finite.");
            return new WorldLightingBandKeyEdit(key.Time, BitConverter.SingleToUInt32Bits(key.Value));
        }));

    public static WorldLightingBandEditPlan PlanRaw(string inputPath, WorldLightingBandKind kind, uint bandId, IEnumerable<WorldLightingBandKeyEdit> keys)
    {
        inputPath = Path.GetFullPath(inputPath); var file = LoadExact(inputPath, kind); var ordered = NormalizeKeys(kind, keys);
        var row = FindRow(file, bandId); var original = Enumerable.Range(0, Fields).Select(index => Raw(file, row, index)).ToArray();
        return new(FormatVersion, DateTimeOffset.UtcNow, inputPath, Sha256(inputPath), kind, bandId, row, original, ordered);
    }

    public static void SavePlan(WorldLightingBandEditPlan plan, string outputPath, bool overwrite = false)
    {
        ValidatePlan(plan, verifySource: true); outputPath = Path.GetFullPath(outputPath);
        if (File.Exists(outputPath) && !overwrite) throw new IOException($"Lighting edit plan already exists: {outputPath}");
        AtomicWrite(outputPath, JsonSerializer.SerializeToUtf8Bytes(plan, JsonOptions), overwrite);
    }

    public static WorldLightingBandEditPlan LoadPlan(string path)
    {
        path = Path.GetFullPath(path); if (!File.Exists(path)) throw new FileNotFoundException("The lighting edit plan does not exist.", path);
        var plan = JsonSerializer.Deserialize<WorldLightingBandEditPlan>(File.ReadAllBytes(path), JsonOptions) ?? throw new InvalidDataException("The lighting edit plan is empty.");
        ValidatePlan(plan, verifySource: true); return plan;
    }

    public static WorldLightingBandEditResult Apply(WorldLightingBandEditPlan plan, string outputPath, bool overwrite = false, bool allowSourceReplacement = false)
    {
        ValidatePlan(plan, verifySource: true); outputPath = Path.GetFullPath(outputPath); var replacesSource = outputPath.Equals(plan.InputPath, StringComparison.OrdinalIgnoreCase);
        if (replacesSource && !allowSourceReplacement) throw new InvalidOperationException("Crucible will not replace the loaded lighting DBC unless source replacement is explicitly enabled. Write a separate staged DBC or use the guarded in-place action that keeps a .bak.");
        if (replacesSource && !overwrite) throw new InvalidOperationException("Replacing the loaded lighting DBC requires explicit overwrite authority in addition to source-replacement authority.");
        if (!replacesSource && File.Exists(outputPath) && !overwrite) throw new IOException($"Lighting output already exists: {outputPath}");
        var file = LoadExact(plan.InputPath, plan.Kind); VerifyPreimage(file, plan); ApplyToFile(file, plan);
        var keepBackup = replacesSource || overwrite && File.Exists(outputPath); var backup = keepBackup ? outputPath + ".bak" : null;
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? throw new InvalidOperationException("Lighting output has no parent directory."));
        file.Save(outputPath, createBackup: keepBackup);
        try { VerifyOutput(outputPath, plan, file.RowCount); }
        catch
        {
            if (backup is not null && File.Exists(backup)) File.Copy(backup, outputPath, true);
            else if (!replacesSource && File.Exists(outputPath)) File.Delete(outputPath);
            throw;
        }
        var hash = Sha256(outputPath); var receiptPath = outputPath + ".crucible-light-band.json";
        var receipt = new { FormatVersion, AppliedUtc = DateTimeOffset.UtcNow, plan.InputPath, plan.InputSha256, OutputPath = outputPath, OutputSha256 = hash, plan.Kind, plan.BandId, plan.RowIndex, plan.OriginalFields, plan.Keys, ReplacedSource = replacesSource, BackupPath = backup };
        AtomicWrite(receiptPath, JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions), overwrite: true);
        return new(outputPath, hash, receiptPath, plan.Kind, plan.BandId, plan.Keys.Count, replacesSource, backup);
    }

    public static uint Pack(WorldLightColor color) => (uint)(color.R | color.G << 8 | color.B << 16);

    private static void ApplyToFile(WdbcFile file, WorldLightingBandEditPlan plan)
    {
        file.SetRaw(plan.RowIndex, Column(1), checked((uint)plan.Keys.Count));
        for (var index = 0; index < MaximumKeys; index++)
        {
            file.SetRaw(plan.RowIndex, Column(2 + index), index < plan.Keys.Count ? checked((uint)plan.Keys[index].Time) : 0);
            file.SetRaw(plan.RowIndex, Column(18 + index), index < plan.Keys.Count ? plan.Keys[index].RawValue : 0);
        }
    }

    private static void VerifyOutput(string outputPath, WorldLightingBandEditPlan plan, int expectedRows)
    {
        var output = LoadExact(outputPath, plan.Kind); if (output.RowCount != expectedRows) throw new InvalidDataException("Written lighting DBC changed its row count.");
        var row = FindRow(output, plan.BandId); if (row != plan.RowIndex || Raw(output, row, 1) != plan.Keys.Count) throw new InvalidDataException("Written lighting band identity/key count did not re-parse as planned.");
        for (var index = 0; index < MaximumKeys; index++)
        {
            var time = index < plan.Keys.Count ? checked((uint)plan.Keys[index].Time) : 0; var value = index < plan.Keys.Count ? plan.Keys[index].RawValue : 0;
            if (Raw(output, row, 2 + index) != time || Raw(output, row, 18 + index) != value) throw new InvalidDataException($"Written lighting key {index + 1:N0} did not re-parse exactly.");
        }
    }

    private static void ValidatePlan(WorldLightingBandEditPlan plan, bool verifySource)
    {
        ArgumentNullException.ThrowIfNull(plan); if (plan.FormatVersion != FormatVersion) throw new InvalidDataException($"Unsupported lighting edit plan format {plan.FormatVersion:N0}.");
        if (!Enum.IsDefined(plan.Kind)) throw new InvalidDataException($"Lighting edit plan has an unknown band kind value {(int)plan.Kind}.");
        if (plan.BandId == 0 || plan.RowIndex < 0 || plan.OriginalFields.Count != Fields) throw new InvalidDataException("Lighting edit plan has an invalid band identity or preimage.");
        var normalized = NormalizeKeys(plan.Kind, plan.Keys); if (!normalized.SequenceEqual(plan.Keys)) throw new InvalidDataException("Lighting edit plan keys are not stored in chronological order.");
        if (verifySource)
        {
            if (!File.Exists(plan.InputPath) || !Sha256(plan.InputPath).Equals(plan.InputSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Source lighting DBC changed after this edit was planned; reload the lighting graph and rebuild the edit.");
            VerifyPreimage(LoadExact(plan.InputPath, plan.Kind), plan);
        }
    }

    private static void VerifyPreimage(WdbcFile file, WorldLightingBandEditPlan plan)
    {
        if (plan.RowIndex >= file.RowCount || Raw(file, plan.RowIndex, 0) != plan.BandId) throw new InvalidDataException($"Lighting band {plan.BandId:N0} is no longer at its planned row.");
        for (var field = 0; field < Fields; field++) if (Raw(file, plan.RowIndex, field) != plan.OriginalFields[field]) throw new InvalidDataException($"Lighting band {plan.BandId:N0} field {field:N0} changed after planning.");
    }

    private static WorldLightingBandKeyEdit[] NormalizeKeys(WorldLightingBandKind kind, IEnumerable<WorldLightingBandKeyEdit> keys)
    {
        var result = keys.OrderBy(key => key.Time).ToArray(); if (result.Length is < 1 or > MaximumKeys) throw new InvalidDataException("A lighting band must contain between 1 and 16 keys.");
        if (result.Any(key => key.Time is < 0 or > WorldLightingService.DayUnits)) throw new InvalidDataException($"Lighting key times must stay within 0..{WorldLightingService.DayUnits:N0}.");
        if (result.GroupBy(key => key.Time).Any(group => group.Count() > 1)) throw new InvalidDataException("A lighting band cannot contain duplicate key times.");
        if (kind == WorldLightingBandKind.Color && result.Any(key => key.RawValue > 0x00FF_FFFF)) throw new InvalidDataException("Packed lighting colors may contain only RGB bytes.");
        if (kind == WorldLightingBandKind.Float && result.Any(key => !float.IsFinite(key.FloatValue))) throw new InvalidDataException("Float lighting keys must be finite.");
        return result;
    }

    private static WdbcFile LoadExact(string path, WorldLightingBandKind kind)
    {
        path = Path.GetFullPath(path); if (!File.Exists(path)) throw new FileNotFoundException("Lighting band DBC not found.", path);
        var expected = kind == WorldLightingBandKind.Color ? "LightIntBand.dbc" : "LightFloatBand.dbc";
        if (!Path.GetFileName(path).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{kind} band editing requires a file named {expected}.");
        var file = WdbcFile.Load(path); if (file.ContainerKind != ClientTableContainerKind.Wdbc || file.FieldCount != Fields || file.RecordSize != RecordBytes) throw new InvalidDataException($"{expected} is not the exact build-12340 WDBC layout ({Fields} fields / {RecordBytes} bytes).");
        return file;
    }

    private static int FindRow(WdbcFile file, uint id)
    {
        for (var row = 0; row < file.RowCount; row++) if (Raw(file, row, 0) == id) return row;
        throw new KeyNotFoundException($"Lighting band {id:N0} was not found in {Path.GetFileName(file.SourcePath)}.");
    }

    private static DbcColumn Column(int field) => new(field, field * 4, 4, $"Field{field}", DbcValueType.Raw32);
    private static uint Raw(WdbcFile file, int row, int field) => file.GetRaw(row, Column(field));
    private static string Sha256(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void AtomicWrite(string path, byte[] bytes, bool overwrite)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Output path has no parent directory."); Directory.CreateDirectory(directory); var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try { using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); } File.Move(temporary, path, overwrite); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
