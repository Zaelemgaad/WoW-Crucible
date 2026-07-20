using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public sealed record StaticM2BatchEntry(
    string RelativePath,
    StaticM2DownportScanStatus Status,
    string SourceSha256,
    StaticM2DownportPlan? ModelPlan,
    string? Error);

public sealed record StaticM2BatchPlan(
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    string SourceRoot,
    string? SourceListfilePath,
    string? SourceListfileSha256,
    string Fingerprint,
    IReadOnlyList<StaticM2BatchEntry> Entries)
{
    public int Ready => Entries.Count(entry => entry.Status == StaticM2DownportScanStatus.ConversionReady);
    public int AlreadyWotlk335 => Entries.Count(entry => entry.Status == StaticM2DownportScanStatus.AlreadyWotlk335);
    public int Blocked => Entries.Count(entry => entry.Status == StaticM2DownportScanStatus.Blocked);
    public int Failed => Entries.Count(entry => entry.Status == StaticM2DownportScanStatus.Failed);
}

public sealed record StaticM2BatchOutput(
    string SourceRelativePath,
    string ModelRelativePath,
    string ModelSha256,
    string SkinRelativePath,
    string SkinSha256,
    int Vertices,
    int Triangles,
    int Submeshes,
    int Materials);

public sealed record StaticM2BatchResult(
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    StaticM2BatchPlan Plan,
    bool ReadyOnly,
    int Workers,
    string OutputDirectory,
    string PayloadDirectory,
    string ReceiptPath,
    IReadOnlyList<StaticM2BatchOutput> Outputs);

/// <summary>
/// Publishes independently verified static M2 downports as one path-preserving,
/// MPQ-ready payload. Every model is converted in isolated temporary storage and
/// the complete batch is moved into place only after all requested outputs pass.
/// </summary>
public static class StaticM2BatchDownportService
{
    private const int PlanFormatVersion = 1;
    private const int ReceiptFormatVersion = 1;
    public const int MaximumWorkers = 32;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly EnumerationOptions RecursiveFiles = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    public static StaticM2BatchPlan Plan(string sourceRoot, string? listfilePath = null, CancellationToken cancellationToken = default) =>
        BuildPlan(sourceRoot, listfilePath, cancellationToken).Plan;

    public static StaticM2BatchResult Convert(StaticM2BatchPlan plan, string outputDirectory, bool readyOnly = false, int workers = 0, CancellationToken cancellationToken = default)
    {
        if (plan.FormatVersion != PlanFormatVersion) throw new InvalidDataException($"Unsupported static M2 batch plan version {plan.FormatVersion}.");
        workers = workers == 0 ? Math.Clamp(Environment.ProcessorCount, 1, 8) : workers;
        if (workers is < 1 or > MaximumWorkers) throw new ArgumentOutOfRangeException(nameof(workers), $"Batch conversion workers must be from 1 to {MaximumWorkers}.");
        cancellationToken.ThrowIfCancellationRequested();

        var rebuilt = BuildPlan(plan.SourceRoot, plan.SourceListfilePath, cancellationToken);
        if (!rebuilt.Plan.Fingerprint.Equals(plan.Fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The source M2/SKIN tree, selected listfile, or conversion findings changed after batch planning; create a fresh plan.");
        var current = rebuilt.Plan;
        if (!readyOnly && (current.Blocked > 0 || current.Failed > 0))
            throw new InvalidOperationException($"Batch contains {current.Blocked:N0} blocked and {current.Failed:N0} failed model(s). No output was written. Review the plan or explicitly choose ready-only publication.");
        var eligible = current.Entries.Where(entry => entry.Status == StaticM2DownportScanStatus.ConversionReady && entry.ModelPlan is not null).ToArray();
        if (eligible.Length == 0) throw new InvalidOperationException("The selected tree contains no models eligible for the verified static M2 downport profile.");

        outputDirectory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            throw new IOException($"Batch conversion output must be new or empty: {outputDirectory}");
        var parent = Path.GetDirectoryName(outputDirectory) ?? throw new InvalidOperationException("Batch conversion output has no parent folder.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".crucible-static-m2-batch-{Guid.NewGuid():N}");
        var payload = Path.Combine(staging, "Payload"); var working = Path.Combine(staging, ".working");
        Directory.CreateDirectory(payload); Directory.CreateDirectory(working);
        try
        {
            var outputs = new ConcurrentBag<StaticM2BatchOutput>();
            Parallel.ForEach(eligible, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = workers }, entry =>
            {
                var modelPlan = entry.ModelPlan!;
                var temporary = Path.Combine(working, modelPlan.SourceModelSha256[..12] + "-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var result = rebuilt.Listfile is null
                        ? StaticM2DownportService.Convert(modelPlan, temporary, cancellationToken)
                        : StaticM2DownportService.ConvertPrepared(modelPlan, temporary, rebuilt.Listfile, cancellationToken);
                    var modelRelative = NormalizeRelative(entry.RelativePath);
                    var skinRelative = NormalizeRelative(Path.Combine(Path.GetDirectoryName(modelRelative) ?? string.Empty, Path.GetFileName(result.OutputSkinPath)));
                    var modelDestination = SafeDestination(payload, modelRelative); var skinDestination = SafeDestination(payload, skinRelative);
                    Directory.CreateDirectory(Path.GetDirectoryName(modelDestination)!); Directory.CreateDirectory(Path.GetDirectoryName(skinDestination)!);
                    File.Copy(result.OutputModelPath, modelDestination, false); File.Copy(result.OutputSkinPath, skinDestination, false);
                    VerifyHash(modelDestination, result.OutputModelSha256); VerifyHash(skinDestination, result.OutputSkinSha256);
                    outputs.Add(new(entry.RelativePath, modelRelative, result.OutputModelSha256, skinRelative, result.OutputSkinSha256,
                        result.ValidatedVertices, result.ValidatedTriangles, result.ValidatedSubmeshes, result.ValidatedMaterials));
                }
                finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
            });
            cancellationToken.ThrowIfCancellationRequested();
            if (rebuilt.Listfile is not null) VerifyHash(rebuilt.Listfile.SourcePath, rebuilt.Listfile.SourceSha256);
            Directory.Delete(working, true);
            var ordered = outputs.OrderBy(output => output.ModelRelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
            var finalPayload = Path.Combine(outputDirectory, "Payload"); var finalReceipt = Path.Combine(outputDirectory, "batch-conversion-receipt.json");
            var result = new StaticM2BatchResult(ReceiptFormatVersion, DateTimeOffset.UtcNow, current, readyOnly, workers, outputDirectory, finalPayload, finalReceipt, ordered);
            File.WriteAllText(Path.Combine(staging, "batch-conversion-receipt.json"), JsonSerializer.Serialize(result, JsonOptions));
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(outputDirectory))
            {
                if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
                    throw new IOException($"Batch conversion output became non-empty before publication; no output was replaced: {outputDirectory}");
                Directory.Delete(outputDirectory);
            }
            Directory.Move(staging, outputDirectory);
            return result;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private static (StaticM2BatchPlan Plan, FileDataIdListfileSnapshot? Listfile) BuildPlan(string sourceRoot, string? listfilePath, CancellationToken cancellationToken)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);
        if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException($"Static M2 batch source folder not found: {sourceRoot}");
        var files = Directory.EnumerateFiles(sourceRoot, "*.m2", RecursiveFiles).Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) throw new InvalidOperationException("The selected source folder contains no M2 files.");
        FileDataIdListfileSnapshot? listfile = null;
        if (!string.IsNullOrWhiteSpace(listfilePath)) listfile = StaticM2DownportService.PrepareListfile(Path.GetFullPath(listfilePath), files, cancellationToken);
        var entries = new List<StaticM2BatchEntry>(files.Length); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = NormalizeRelative(Path.GetRelativePath(sourceRoot, file));
            if (!seen.Add(relative)) throw new InvalidDataException($"Two source models collapse to the same client-relative path: {relative}");
            var hash = HashFile(file);
            try
            {
                if (IsWotlkM2(file)) { entries.Add(new(relative, StaticM2DownportScanStatus.AlreadyWotlk335, hash, null, null)); continue; }
                var modelPlan = listfile is null ? StaticM2DownportService.Plan(file, cancellationToken: cancellationToken) : StaticM2DownportService.PlanWithListfileSnapshot(file, null, listfile, cancellationToken);
                entries.Add(new(relative, modelPlan.Ready ? StaticM2DownportScanStatus.ConversionReady : StaticM2DownportScanStatus.Blocked, hash, modelPlan, null));
            }
            catch (Exception exception) { entries.Add(new(relative, StaticM2DownportScanStatus.Failed, hash, null, exception.Message)); }
        }
        var listfilePathValue = listfile?.SourcePath; var listfileHash = listfile?.SourceSha256;
        var fingerprint = Fingerprint(sourceRoot, listfileHash, entries);
        return (new(PlanFormatVersion, DateTimeOffset.UtcNow, sourceRoot, listfilePathValue, listfileHash, fingerprint, entries), listfile);
    }

    private static string Fingerprint(string sourceRoot, string? listfileHash, IReadOnlyList<StaticM2BatchEntry> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string? value) { var bytes = Encoding.UTF8.GetBytes(value ?? "<null>"); hash.AppendData(bytes); hash.AppendData([0]); }
        Add(Path.GetFullPath(sourceRoot).ToUpperInvariant()); Add(listfileHash);
        foreach (var entry in entries.OrderBy(value => value.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            Add(entry.RelativePath.ToUpperInvariant()); Add(entry.Status.ToString()); Add(entry.SourceSha256); Add(entry.Error);
            Add(entry.ModelPlan?.SourceSkinSha256); Add(entry.ModelPlan?.SourceListfileSha256);
            foreach (var blocker in entry.ModelPlan?.Blockers ?? []) Add(blocker);
            foreach (var texture in entry.ModelPlan?.ResolvedTexturePaths ?? []) { Add(texture.TextureIndex.ToString()); Add(texture.FileDataId.ToString()); Add(texture.ClientPath.ToUpperInvariant()); }
        }
        return System.Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string NormalizeRelative(string path)
    {
        var normalized = PatchInputMapper.NormalizeArchivePath(path);
        if (Path.IsPathRooted(normalized) || normalized.Split('\\').Any(part => part is "" or "." or "..")) throw new InvalidDataException($"Unsafe client-relative model path: {path}");
        return normalized;
    }

    private static string SafeDestination(string root, string relative)
    {
        var destination = Path.GetFullPath(Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar)));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Batch output escapes its payload root: {relative}");
        return destination;
    }

    private static bool IsWotlkM2(string path)
    {
        Span<byte> header = stackalloc byte[8]; using var stream = File.OpenRead(path); if (stream.Length < 8) return false; stream.ReadExactly(header);
        return header[..4].SequenceEqual("MD20"u8) && BitConverter.ToUInt32(header[4..]) == 264;
    }

    private static string HashFile(string path) { using var stream = File.OpenRead(path); return System.Convert.ToHexString(SHA256.HashData(stream)); }
    private static void VerifyHash(string path, string expected) { var actual = HashFile(path); if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Batch output hash mismatch: {path}"); }
}
