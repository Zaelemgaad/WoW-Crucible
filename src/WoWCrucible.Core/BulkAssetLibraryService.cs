using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public sealed record BulkAssetArchivePlan(string SourcePath, string RelativePath, string Identity, long ArchiveBytes, bool Eligible, int Entries, int BlpFiles, long LogicalBytes, string? Error);
public sealed record BulkAssetLibraryPlan(string SourceRoot, string LibraryRoot, long MaximumArchiveBytes, DateTimeOffset CreatedUtc, int LooseBlpFiles, IReadOnlyList<BulkAssetArchivePlan> Archives);
public sealed record BulkAssetLibraryCheckpoint(DateTimeOffset UpdatedUtc, IReadOnlyList<string> CompletedArchiveIds, IReadOnlyDictionary<string, string> Failures, int ConvertedPngFiles);
public sealed record BulkAssetLibraryRunResult(int CompletedArchives, int FailedArchives, int CopiedLooseBlps, int ConvertedPngs, int ConversionFailures, string CatalogPath, string CheckpointPath);

public static class BulkAssetLibraryService
{
    private const string PlanFileName = "asset-library-plan.json";
    private const string CheckpointFileName = "asset-library-checkpoint.json";

    public static BulkAssetLibraryPlan CreatePlan(string sourceRoot, string libraryRoot, long maximumArchiveBytes, IProgress<(int Done, int Total, string Path)>? progress = null)
    {
        sourceRoot = Path.GetFullPath(sourceRoot); libraryRoot = Path.GetFullPath(libraryRoot);
        ValidateRoots(sourceRoot, libraryRoot);
        if (maximumArchiveBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumArchiveBytes));
        var allFiles = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories).ToArray();
        var looseBlps = allFiles.Count(path => Path.GetExtension(path).Equals(".blp", StringComparison.OrdinalIgnoreCase));
        var mpqs = allFiles.Where(path => Path.GetExtension(path).Equals(".mpq", StringComparison.OrdinalIgnoreCase)).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        var archiveService = new PatchArchiveService(); var archives = new List<BulkAssetArchivePlan>(mpqs.Length);
        for (var index = 0; index < mpqs.Length; index++)
        {
            var path = mpqs[index]; var info = new FileInfo(path); var relative = Path.GetRelativePath(sourceRoot, path);
            var identity = Identity(relative, info.Length, info.LastWriteTimeUtc.Ticks); var eligible = info.Length < maximumArchiveBytes;
            var entries = 0; var blps = 0; long logical = 0; string? error = null;
            if (eligible)
            {
                try
                {
                    var listed = archiveService.ListFiles(path).Where(entry => !entry.IsMetadata).ToArray();
                    entries = listed.Length; blps = listed.Count(entry => Path.GetExtension(entry.ArchivePath).Equals(".blp", StringComparison.OrdinalIgnoreCase)); logical = listed.Sum(entry => entry.Size);
                }
                catch (Exception exception) { error = exception.Message; }
            }
            archives.Add(new(path, relative, identity, info.Length, eligible, entries, blps, logical, error));
            progress?.Report((index + 1, mpqs.Length, relative));
        }
        var plan = new BulkAssetLibraryPlan(sourceRoot, libraryRoot, maximumArchiveBytes, DateTimeOffset.UtcNow, looseBlps, archives);
        Directory.CreateDirectory(libraryRoot); WriteJsonAtomic(Path.Combine(libraryRoot, PlanFileName), plan); return plan;
    }

    public static BulkAssetLibraryPlan LoadPlan(string libraryRoot)
    {
        var path = Path.Combine(Path.GetFullPath(libraryRoot), PlanFileName);
        return JsonSerializer.Deserialize<BulkAssetLibraryPlan>(File.ReadAllText(path)) ?? throw new InvalidDataException("The asset-library plan is empty or invalid.");
    }

    public static async Task<BulkAssetLibraryRunResult> RunAsync(string libraryRoot, string converterPath, int conversionWorkers = 6,
        IProgress<(string Stage, int Done, int Total, string Path)>? progress = null, CancellationToken cancellationToken = default)
    {
        var plan = LoadPlan(libraryRoot); ValidateRoots(plan.SourceRoot, plan.LibraryRoot);
        converterPath = Path.GetFullPath(converterPath); if (!File.Exists(converterPath)) throw new FileNotFoundException("The BLP converter does not exist.", converterPath);
        conversionWorkers = Math.Clamp(conversionWorkers, 1, 16);
        var checkpointPath = Path.Combine(plan.LibraryRoot, CheckpointFileName);
        var prior = File.Exists(checkpointPath) ? JsonSerializer.Deserialize<BulkAssetLibraryCheckpoint>(File.ReadAllText(checkpointPath)) : null;
        var completed = (prior?.CompletedArchiveIds ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var failures = new Dictionary<string, string>(prior?.Failures ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        var converted = prior?.ConvertedPngFiles ?? 0; var copiedLoose = CopyLooseBlps(plan, progress, cancellationToken);
        var conversionFailures = 0; var eligible = plan.Archives.Where(archive => archive.Eligible && archive.Error is null).OrderBy(archive => archive.ArchiveBytes).ToArray();
        var archiveService = new PatchArchiveService(); var done = 0;
        foreach (var archive in eligible)
        {
            cancellationToken.ThrowIfCancellationRequested(); done++;
            if (completed.Contains(archive.Identity)) continue;
            var contentRoot = ArchiveContentRoot(plan.LibraryRoot, archive); Directory.CreateDirectory(contentRoot);
            try
            {
                progress?.Report(("Extract", done, eligible.Length, archive.RelativePath));
                var entries = archiveService.ListFiles(archive.SourcePath).Where(entry => !entry.IsMetadata).ToArray();
                var rejectedEntries = new List<string>();
                archiveService.Extract(archive.SourcePath, contentRoot, entries, null, cancellationToken, overwriteExisting: false, preserveLocaleVariants: true,
                    extractionFailure: (entry, exception) => rejectedEntries.Add($"{entry.ArchivePath}: {exception.Message}"));
                var conversion = await ConvertBlpsAsync(contentRoot, converterPath, conversionWorkers, cancellationToken);
                converted += conversion.Converted; conversionFailures += conversion.Failed;
                completed.Add(archive.Identity);
                if (rejectedEntries.Count == 0) failures.Remove(archive.Identity);
                else failures[archive.Identity] = $"{rejectedEntries.Count:N0} entry failure(s): {string.Join(" | ", rejectedEntries.Take(10))}";
            }
            catch (Exception exception) { failures[archive.Identity] = exception.Message; }
            WriteCheckpoint(checkpointPath, completed, failures, converted);
        }
        progress?.Report(("Convert loose", 0, plan.LooseBlpFiles, plan.SourceRoot));
        var looseConversion = await ConvertBlpsAsync(Path.Combine(plan.LibraryRoot, "Loose", "Content"), converterPath, conversionWorkers, cancellationToken);
        converted += looseConversion.Converted; conversionFailures += looseConversion.Failed;
        WriteCheckpoint(checkpointPath, completed, failures, converted);
        var catalog = WriteCatalog(plan);
        return new(completed.Count, failures.Count, copiedLoose, converted, conversionFailures, catalog, checkpointPath);
    }

    private static int CopyLooseBlps(BulkAssetLibraryPlan plan, IProgress<(string Stage, int Done, int Total, string Path)>? progress, CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(plan.SourceRoot, "*", SearchOption.AllDirectories).Where(path => Path.GetExtension(path).Equals(".blp", StringComparison.OrdinalIgnoreCase)).ToArray();
        var copied = 0;
        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var source = files[index]; var relative = Path.GetRelativePath(plan.SourceRoot, source);
            var destination = Path.GetFullPath(Path.Combine(plan.LibraryRoot, "Loose", "Content", relative)); EnsureInside(plan.LibraryRoot, destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination)) { File.Copy(source, destination, false); File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source)); copied++; }
            else if (new FileInfo(destination).Length != new FileInfo(source).Length || File.GetLastWriteTimeUtc(destination) != File.GetLastWriteTimeUtc(source))
            {
                var extension = Path.GetExtension(destination); var variant = destination[..^extension.Length] + $".variant-{Identity(relative, new FileInfo(source).Length, File.GetLastWriteTimeUtc(source).Ticks)}{extension}";
                if (!File.Exists(variant)) { File.Copy(source, variant, false); File.SetLastWriteTimeUtc(variant, File.GetLastWriteTimeUtc(source)); copied++; }
            }
            if ((index & 127) == 0) progress?.Report(("Copy loose", index + 1, files.Length, relative));
        }
        return copied;
    }

    private static async Task<(int Converted, int Failed)> ConvertBlpsAsync(string root, string converterPath, int workers, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return (0, 0);
        var pending = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).Equals(".blp", StringComparison.OrdinalIgnoreCase) && !File.Exists(Path.ChangeExtension(path, ".png"))).ToArray();
        var batches = BatchPaths(pending, 28_000, 36); var converted = 0; var failed = 0;
        await Parallel.ForEachAsync(batches, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken }, async (batch, token) =>
        {
            var start = new ProcessStartInfo(converterPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("/M"); foreach (var path in batch) start.ArgumentList.Add(path);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the BLP converter.");
            var output = process.StandardOutput.ReadToEndAsync(token); var error = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token); await Task.WhenAll(output, error);
            foreach (var source in batch)
            {
                if (File.Exists(Path.ChangeExtension(source, ".png"))) Interlocked.Increment(ref converted);
                else Interlocked.Increment(ref failed);
            }
        });
        return (converted, failed);
    }

    private static IReadOnlyList<string[]> BatchPaths(IReadOnlyList<string> paths, int maximumCharacters, int maximumFiles)
    {
        var result = new List<string[]>(); var batch = new List<string>(); var characters = 0;
        foreach (var path in paths)
        {
            if (batch.Count > 0 && (batch.Count >= maximumFiles || characters + path.Length + 3 > maximumCharacters)) { result.Add(batch.ToArray()); batch.Clear(); characters = 0; }
            batch.Add(path); characters += path.Length + 3;
        }
        if (batch.Count > 0) result.Add(batch.ToArray()); return result;
    }

    private static string WriteCatalog(BulkAssetLibraryPlan plan)
    {
        var path = Path.Combine(plan.LibraryRoot, "asset-catalog.csv"); var temp = path + ".tmp";
        using (var writer = new StreamWriter(temp, false, new UTF8Encoding(true)))
        {
            writer.WriteLine("category,format,source,relative_path,bytes");
            foreach (var file in Directory.EnumerateFiles(plan.LibraryRoot, "*", SearchOption.AllDirectories).Where(file => !file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)))
            {
                var relative = Path.GetRelativePath(plan.LibraryRoot, file); var extension = Path.GetExtension(file).TrimStart('.').ToUpperInvariant();
                var source = relative.StartsWith($"Loose{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? "loose" : relative.Split(Path.DirectorySeparatorChar).Skip(1).FirstOrDefault() ?? "library";
                writer.WriteLine($"{Csv(Classify(relative))},{Csv(extension)},{Csv(source)},{Csv(relative)},{new FileInfo(file).Length}");
            }
        }
        File.Move(temp, path, true); return path;
    }

    private static string Classify(string path)
    {
        path = path.Replace('/', Path.DirectorySeparatorChar);
        if (Contains(path, "World\\Maps", "World\\Minimaps", "Minimap", "Map\\")) return "Maps";
        if (Contains(path, "Interface\\", "Glues\\")) return "UI";
        if (Contains(path, "Character\\")) return "Characters";
        if (Contains(path, "Creature\\")) return "Creatures";
        if (Contains(path, "Item\\")) return "Items";
        if (Contains(path, "Sound\\", "Music\\")) return "Audio";
        if (Contains(path, "Tileset\\", "Textures\\", ".blp", ".png", ".tga", ".dds")) return "Textures";
        if (Contains(path, ".m2", ".skin", ".wmo", ".adt", ".anim")) return "ModelsAndWorld";
        return "Other";
    }

    private static bool Contains(string value, params string[] fragments) => fragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    private static string ArchiveContentRoot(string libraryRoot, BulkAssetArchivePlan archive) => Path.Combine(libraryRoot, "Archives", $"{SafeName(Path.GetFileNameWithoutExtension(archive.RelativePath))}-{archive.Identity}", "Content");
    private static string SafeName(string value) { var invalid = Path.GetInvalidFileNameChars().ToHashSet(); var clean = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim(); return clean.Length > 60 ? clean[..60] : clean; }
    private static string Identity(string relativePath, long bytes, long ticks) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{relativePath.ToUpperInvariant()}|{bytes}|{ticks}")))[..12].ToLowerInvariant();
    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';

    private static void ValidateRoots(string sourceRoot, string libraryRoot)
    {
        if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException(sourceRoot);
        var relative = Path.GetRelativePath(sourceRoot, libraryRoot);
        if (relative.Equals(".") || !relative.StartsWith(".." + Path.DirectorySeparatorChar) && relative != "..") throw new InvalidOperationException("The asset library must be outside the source tree so it cannot ingest its own output.");
    }
    private static void EnsureInside(string root, string path) { var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path)); if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)) throw new InvalidOperationException($"Output escaped the asset library: {path}"); }
    private static void WriteCheckpoint(string path, HashSet<string> completed, Dictionary<string, string> failures, int converted) => WriteJsonAtomic(path, new BulkAssetLibraryCheckpoint(DateTimeOffset.UtcNow, completed.Order().ToArray(), failures, converted));
    private static void WriteJsonAtomic<T>(string path, T value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp = path + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, path, true); }
}
