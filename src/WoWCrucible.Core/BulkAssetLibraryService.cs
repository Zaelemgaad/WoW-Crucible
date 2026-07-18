using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoWCrucible.Core;

public sealed record BulkAssetArchivePlan(string SourcePath, string RelativePath, string Identity, long ArchiveBytes, bool Eligible, int Entries, int BlpFiles, long LogicalBytes, string? Error);
public sealed record BulkAssetLibraryPlan(string SourceRoot, string LibraryRoot, long MaximumArchiveBytes, DateTimeOffset CreatedUtc, int LooseBlpFiles, IReadOnlyList<BulkAssetArchivePlan> Archives);
public sealed record BulkAssetLibraryCheckpoint(DateTimeOffset UpdatedUtc, IReadOnlyList<string> CompletedArchiveIds, IReadOnlyDictionary<string, string> Failures, int ConvertedPngFiles);
public sealed record BulkAssetLibraryRunResult(int CompletedArchives, int FailedArchives, int CopiedLooseBlps, int ConvertedPngs, int ConversionFailures, string CatalogPath, string CheckpointPath);
public sealed record BulkAssetConversionRepairResult(int NewlyConvertedPngs, int RemainingFailures, string CatalogPath, string CheckpointPath);
public sealed record BulkAssetLayoutResult(bool Applied, int SourceFolders, long Files, long Bytes, long MovedFiles, int Conflicts, string CatalogPath);
public sealed record BulkAssetExtractedImportResult(string Provenance, long SourceFiles, long SourceBytes, long ImportedFiles, int ConvertedPngs, int ConversionFailures, string CatalogPath);
[JsonConverter(typeof(JsonStringEnumConverter<LooseAssetDisposition>))]
public enum LooseAssetDisposition { Move, ExactDuplicate, Metadata }
public sealed record LooseAssetConsolidationEntry(string SourcePath, string DestinationPath, string LogicalPath, string Provenance, long Bytes, LooseAssetDisposition Disposition);
public sealed record LooseAssetConsolidationJournal(int FormatVersion, DateTimeOffset StartedUtc, DateTimeOffset? FilesCommittedUtc, DateTimeOffset? CatalogCompletedUtc,
    string? CatalogRebuildError, IReadOnlyList<LooseAssetConsolidationEntry> Entries);
public sealed record LooseAssetConsolidationResult(bool Applied, long Files, long Bytes, long MovedFiles, long ExactDuplicates, int Conflicts, string JournalPath, string CatalogPath,
    string? CatalogRebuildError = null);

public static class BulkAssetLibraryService
{
    private const string PlanFileName = "asset-library-plan.json";
    private const string CheckpointFileName = "asset-library-checkpoint.json";
    private static readonly HashSet<string> ContentAnchors = new(StringComparer.OrdinalIgnoreCase)
    {
        "character-fromscratch", "Character", "Creature", "Item", "Interface", "World", "Textures", "XTextures", "Tileset", "Sound", "Spell", "Spells", "Shaders", "_Shaders", "Interiors", "Dungeons", "Environments", "Buildings", "Particles", "Cameras", "Fonts", "DBFilesClient"
    };
    private static readonly HashSet<string> CharacterArchetypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "BloodElf", "Human", "NightElf", "Orc", "Scourge", "Undead", "Tauren", "Troll", "Draenei", "Dwarf", "Gnome", "Goblin", "Worgen", "Pandaren", "VoidElf", "LightforgedDraenei", "HighmountainTauren", "Nightborne", "ZandalariTroll", "KulTiran", "Kultiran", "DarkIronDwarf", "DarkironDwarf", "Vulpera", "Dracthyr", "Harronir", "Mechagnome", "MagharOrc", "Earthen", "FelOrc", "ForestTroll", "GoblinOld", "HighElf", "IceTroll", "Naga", "Naga_", "NorthrendSkeleton", "Skeleton", "Taunka", "Tuskarr", "Vrykul"
    };
    private static readonly Dictionary<string, string> CanonicalSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Undead"] = "Scourge", ["Kultiran"] = "KulTiran", ["DarkironDwarf"] = "DarkIronDwarf", ["Naga_"] = "Naga",
        ["Character"] = "Character", ["Creature"] = "Creature", ["Item"] = "Item", ["Interface"] = "Interface", ["World"] = "World", ["Textures"] = "Textures", ["XTextures"] = "XTextures", ["Tileset"] = "Tileset", ["Sound"] = "Sound", ["Spell"] = "Spell", ["Spells"] = "Spells", ["Shaders"] = "Shaders", ["_Shaders"] = "_Shaders", ["Interiors"] = "Interiors", ["Dungeons"] = "Dungeons", ["Environments"] = "Environments", ["Buildings"] = "Buildings", ["Particles"] = "Particles", ["Cameras"] = "Cameras", ["Fonts"] = "Fonts", ["DBFilesClient"] = "DBFilesClient",
        ["Male"] = "Male", ["Female"] = "Female", ["BloodElf"] = "BloodElf", ["Human"] = "Human", ["NightElf"] = "NightElf", ["Orc"] = "Orc", ["Scourge"] = "Scourge", ["Tauren"] = "Tauren", ["Troll"] = "Troll", ["Draenei"] = "Draenei", ["Dwarf"] = "Dwarf", ["Gnome"] = "Gnome", ["Goblin"] = "Goblin", ["Worgen"] = "Worgen", ["Pandaren"] = "Pandaren", ["VoidElf"] = "VoidElf", ["LightforgedDraenei"] = "LightforgedDraenei", ["HighmountainTauren"] = "HighmountainTauren", ["Nightborne"] = "Nightborne", ["ZandalariTroll"] = "ZandalariTroll", ["KulTiran"] = "KulTiran", ["DarkIronDwarf"] = "DarkIronDwarf", ["Vulpera"] = "Vulpera", ["Dracthyr"] = "Dracthyr", ["Harronir"] = "Harronir", ["Mechagnome"] = "Mechagnome", ["MagharOrc"] = "MagharOrc", ["Earthen"] = "Earthen", ["FelOrc"] = "FelOrc", ["ForestTroll"] = "ForestTroll", ["GoblinOld"] = "GoblinOld", ["HighElf"] = "HighElf", ["IceTroll"] = "IceTroll", ["Naga"] = "Naga", ["NorthrendSkeleton"] = "NorthrendSkeleton", ["Skeleton"] = "Skeleton", ["Taunka"] = "Taunka", ["Tuskarr"] = "Tuskarr", ["Vrykul"] = "Vrykul",
        ["Bakednpctextures"] = "BakedNpcTextures", ["Objectcomponents"] = "ObjectComponents", ["Texturecomponents"] = "TextureComponents", ["TorsoLowerTexture"] = "TorsoLowerTexture", ["TorsoUpperTexture"] = "TorsoUpperTexture", ["ArmLowerTexture"] = "ArmLowerTexture", ["ArmUpperTexture"] = "ArmUpperTexture", ["FootTexture"] = "FootTexture", ["HandTexture"] = "HandTexture", ["LegLowerTexture"] = "LegLowerTexture", ["LegUpperTexture"] = "LegUpperTexture"
    };

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
        using var operationLock = AcquireOperationLock(libraryRoot);
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
            var contentRoot = ArchiveStagingRoot(plan.LibraryRoot, archive); Directory.CreateDirectory(contentRoot);
            try
            {
                EnsureExtractionCapacity(plan.LibraryRoot, archive);
                progress?.Report(("Extract", done, eligible.Length, archive.RelativePath));
                var entries = archiveService.ListFiles(archive.SourcePath).Where(entry => !entry.IsMetadata).ToArray();
                var rejectedEntries = new List<string>();
                archiveService.Extract(archive.SourcePath, contentRoot, entries, null, cancellationToken, overwriteExisting: false, preserveLocaleVariants: true,
                    extractionFailure: (entry, exception) => rejectedEntries.Add($"{entry.ArchivePath}: {exception.Message}"));
                var conversion = await ConvertBlpsAsync(contentRoot, converterPath, conversionWorkers, cancellationToken);
                converted += conversion.Converted; conversionFailures += conversion.Failed;
                var relocation = RelocateContent(plan.LibraryRoot, ArchiveFolderName(archive), contentRoot, true, cancellationToken);
                if (relocation.Conflicts > 0) throw new IOException($"Content-first relocation found {relocation.Conflicts:N0} existing destination conflict(s); nothing was overwritten.");
                completed.Add(archive.Identity);
                if (rejectedEntries.Count == 0) failures.Remove(archive.Identity);
                else failures[archive.Identity] = $"{rejectedEntries.Count:N0} entry failure(s): {string.Join(" | ", rejectedEntries.Take(10))}";
            }
            catch (Exception exception) { failures[archive.Identity] = exception.Message; }
            WriteCheckpoint(checkpointPath, completed, failures, converted);
        }
        progress?.Report(("Convert non-archive sources", 0, plan.LooseBlpFiles, plan.SourceRoot));
        var looseConversion = await ConvertBlpsAsync(Path.Combine(plan.LibraryRoot, "Archives", "Content"), converterPath, conversionWorkers, cancellationToken);
        converted += looseConversion.Converted; conversionFailures += looseConversion.Failed;
        WriteCheckpoint(checkpointPath, completed, failures, converted);
        var catalog = WriteCatalog(plan, cancellationToken);
        return new(completed.Count, failures.Count, copiedLoose, converted, conversionFailures, catalog, checkpointPath);
    }

    public static async Task<BulkAssetConversionRepairResult> RepairConversionsAsync(string libraryRoot, string converterPath, int conversionWorkers = 6,
        IProgress<(string Stage, int Done, int Total, string Path)>? progress = null, CancellationToken cancellationToken = default)
    {
        var plan = LoadPlan(libraryRoot); converterPath = Path.GetFullPath(converterPath);
        if (!File.Exists(converterPath)) throw new FileNotFoundException("The BLP converter does not exist.", converterPath);
        conversionWorkers = Math.Clamp(conversionWorkers, 1, 16);
        var roots = ConversionRoots(plan.LibraryRoot).ToArray();
        var converted = 0; var failed = 0;
        for (var index = 0; index < roots.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested(); progress?.Report(("Repair PNGs", index + 1, roots.Length, roots[index].Label));
            var result = await ConvertBlpsAsync(roots[index].Root, converterPath, conversionWorkers, cancellationToken); converted += result.Converted; failed += result.Failed;
        }
        var checkpointPath = Path.Combine(plan.LibraryRoot, CheckpointFileName);
        var prior = File.Exists(checkpointPath) ? JsonSerializer.Deserialize<BulkAssetLibraryCheckpoint>(File.ReadAllText(checkpointPath)) : null;
        var matchingPngs = Directory.EnumerateFiles(plan.LibraryRoot, "*", SearchOption.AllDirectories)
            .Count(path => Path.GetExtension(path).Equals(".blp", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.ChangeExtension(path, ".png")));
        WriteCheckpoint(checkpointPath, (prior?.CompletedArchiveIds ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase), new Dictionary<string, string>(prior?.Failures ?? new Dictionary<string, string>()), matchingPngs);
        return new(converted, failed, WriteCatalog(plan, cancellationToken), checkpointPath);
    }

    public static async Task<BulkAssetExtractedImportResult> ImportExtractedArchiveAsync(string sourceRoot, string libraryRoot, string provenance,
        string converterPath, int conversionWorkers = 6, IProgress<(string Stage, long Done, long Total, string Path)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        sourceRoot = Path.GetFullPath(sourceRoot); libraryRoot = Path.GetFullPath(libraryRoot); converterPath = Path.GetFullPath(converterPath);
        using var operationLock = AcquireOperationLock(libraryRoot);
        ValidateRoots(sourceRoot, libraryRoot);
        var sourceFromLibrary = Path.GetRelativePath(libraryRoot, sourceRoot);
        if (sourceFromLibrary.Equals(".") || !sourceFromLibrary.StartsWith(".." + Path.DirectorySeparatorChar) && sourceFromLibrary != "..")
            throw new InvalidOperationException("The extracted source folder must be outside the asset library.");
        if (!File.Exists(converterPath)) throw new FileNotFoundException("The BLP converter does not exist.", converterPath);
        var cleanProvenance = SafeName(provenance);
        if (string.IsNullOrWhiteSpace(cleanProvenance) || cleanProvenance is "." or ".." || !cleanProvenance.Equals(provenance.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("Provenance must be a valid folder name without path separators or replacement characters.", nameof(provenance));
        conversionWorkers = Math.Clamp(conversionWorkers, 1, 16);

        var sources = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories).ToArray();
        var sourceBytes = sources.Sum(path => new FileInfo(path).Length);
        var driveRoot = Path.GetPathRoot(libraryRoot) ?? throw new InvalidOperationException("The asset library has no drive root.");
        const long ReserveBytes = 32L * 1024 * 1024 * 1024;
        if (new DriveInfo(driveRoot).AvailableFreeSpace - ReserveBytes < sourceBytes * 2L)
            throw new IOException($"Import requires an estimated {sourceBytes * 2d / (1024 * 1024 * 1024):0.##} GiB plus a 32 GiB safety reserve.");

        var stagingRoot = Path.Combine(libraryRoot, ".staging", "Imports", cleanProvenance, "Content");
        long imported = 0;
        for (var index = 0; index < sources.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = sources[index]; var relative = Path.GetRelativePath(sourceRoot, source);
            var final = ContentFirstDestination(libraryRoot, cleanProvenance, relative);
            if (File.Exists(final))
            {
                if (!FilesEqual(source, final)) throw new IOException($"Existing provenance file differs from the import source: {final}");
            }
            else
            {
                var staged = Path.GetFullPath(Path.Combine(stagingRoot, relative)); EnsureInside(stagingRoot, staged);
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                if (File.Exists(staged))
                {
                    if (!FilesEqual(source, staged)) throw new IOException($"Staged import file differs from the source: {staged}");
                }
                else { File.Copy(source, staged, false); File.SetLastWriteTimeUtc(staged, File.GetLastWriteTimeUtc(source)); imported++; }
            }
            if ((index & 511) == 0 || index + 1 == sources.Length) progress?.Report(("Copy extracted", index + 1, sources.Length, relative));
        }

        var conversion = await ConvertBlpsAsync(stagingRoot, converterPath, conversionWorkers, cancellationToken);
        long relocated = 0;
        var relocation = Directory.Exists(stagingRoot)
            ? RelocateContent(libraryRoot, cleanProvenance, stagingRoot, true, cancellationToken,
                path => { relocated++; if ((relocated & 511) == 0) progress?.Report(("Relocate", relocated, 0, path)); })
            : (MovedFiles: 0L, Conflicts: 0);
        if (relocation.Conflicts > 0) throw new IOException($"Extracted import found {relocation.Conflicts:N0} existing destination conflict(s); nothing was overwritten.");
        var plan = LoadPlan(libraryRoot);
        return new(cleanProvenance, sources.LongLength, sourceBytes, imported, conversion.Converted, conversion.Failed, WriteCatalog(plan, cancellationToken));
    }

    public static BulkAssetLayoutResult MigrateToContentFirstLayout(string libraryRoot, bool apply,
        IProgress<(long Done, long Total, string Path)>? progress = null, CancellationToken cancellationToken = default)
    {
        using var operationLock = AcquireOperationLock(libraryRoot); var plan = LoadPlan(libraryRoot); var archivesRoot = Path.Combine(plan.LibraryRoot, "Archives");
        if (!Directory.Exists(archivesRoot)) return new(apply, 0, 0, 0, 0, 0, Path.Combine(plan.LibraryRoot, "asset-catalog.csv"));
        var sources = LegacyArchiveRoots(archivesRoot).ToArray(); long files = 0; long bytes = 0;
        foreach (var source in sources)
            foreach (var file in Directory.EnumerateFiles(source.ContentRoot, "*", SearchOption.AllDirectories)) { files++; bytes += new FileInfo(file).Length; }
        long done = 0; long moved = 0; var conflicts = 0;
        foreach (var source in sources)
        {
            var relocation = RelocateContent(plan.LibraryRoot, source.ArchiveFolder, source.ContentRoot, false, cancellationToken,
                path => { done++; if ((done & 4095) == 0 || done == files) progress?.Report((done, files, path)); });
            conflicts += relocation.Conflicts;
        }
        if (!apply || conflicts > 0) return new(false, sources.Length, files, bytes, 0, conflicts, Path.Combine(plan.LibraryRoot, "asset-catalog.csv"));
        foreach (var source in sources) moved += RelocateContent(plan.LibraryRoot, source.ArchiveFolder, source.ContentRoot, true, cancellationToken).MovedFiles;
        return new(true, sources.Length, files, bytes, moved, 0, WriteCatalog(plan, cancellationToken));
    }

    public static LooseAssetConsolidationResult ConsolidateLooseLayout(string libraryRoot, bool apply,
        IProgress<(long Done, long Total, string Path)>? progress = null, CancellationToken cancellationToken = default)
    {
        libraryRoot = Path.GetFullPath(libraryRoot); using var operationLock = AcquireOperationLock(libraryRoot); var plan = LoadPlan(libraryRoot);
        var looseRoot = Path.Combine(libraryRoot, "Loose"); var contentRoot = Path.Combine(looseRoot, "Content"); var reportsRoot = Path.Combine(libraryRoot, "Reports");
        var journalPath = Path.Combine(reportsRoot, "loose-consolidation-journal.json"); var catalogPath = Path.Combine(libraryRoot, "asset-catalog.csv");
        if (!Directory.Exists(contentRoot)) return new(apply, 0, 0, 0, 0, 0, journalPath, catalogPath);
        var sources = Directory.EnumerateFiles(contentRoot, "*", SearchOption.AllDirectories).Where(path => !Path.GetFileName(path).Equals(".blp-conversion-failures.txt", StringComparison.OrdinalIgnoreCase)).ToArray();
        var planned = sources.Select(source =>
        {
            var relative = Path.GetRelativePath(contentRoot, source); var mapping = MapLooseRelative(relative); var destination = ContentFirstDestination(libraryRoot, mapping.Provenance, mapping.RelativePath);
            return new PlannedLooseFile(source, destination, Path.GetDirectoryName(mapping.RelativePath) ?? string.Empty, mapping.Provenance, new FileInfo(source).Length, LooseAssetDisposition.Move);
        }).ToList();
        var conflicts = 0;
        foreach (var group in planned.GroupBy(item => item.DestinationPath, StringComparer.OrdinalIgnoreCase))
        {
            var entries = group.ToArray();
            if (File.Exists(group.Key))
            {
                foreach (var entry in entries) { if (FilesEqual(entry.SourcePath, group.Key)) entry.Disposition = LooseAssetDisposition.ExactDuplicate; else conflicts++; }
                continue;
            }
            var primary = entries[0];
            foreach (var duplicate in entries.Skip(1)) { if (FilesEqual(primary.SourcePath, duplicate.SourcePath)) duplicate.Disposition = LooseAssetDisposition.ExactDuplicate; else conflicts++; }
        }
        var metadata = Path.Combine(contentRoot, ".blp-conversion-failures.txt");
        if (File.Exists(metadata))
        {
            var destination = Path.Combine(reportsRoot, "legacy-loose-blp-conversion-failures.txt");
            var disposition = File.Exists(destination) && FilesEqual(metadata, destination) ? LooseAssetDisposition.ExactDuplicate : LooseAssetDisposition.Metadata;
            if (File.Exists(destination) && disposition != LooseAssetDisposition.ExactDuplicate) conflicts++;
            planned.Add(new(metadata, destination, "Reports", "Legacy Loose metadata", new FileInfo(metadata).Length, disposition));
        }
        var files = planned.LongCount(); var bytes = planned.Sum(item => item.Bytes); var movedCount = planned.LongCount(item => item.Disposition is LooseAssetDisposition.Move or LooseAssetDisposition.Metadata); var exactCount = planned.LongCount(item => item.Disposition == LooseAssetDisposition.ExactDuplicate);
        if (!apply || conflicts > 0) return new(false, files, bytes, movedCount, exactCount, conflicts, journalPath, catalogPath);

        var startedUtc = DateTimeOffset.UtcNow; var journalEntries = planned.Select(ToJournal).ToArray();
        Directory.CreateDirectory(reportsRoot); WriteJsonAtomic(journalPath, new LooseAssetConsolidationJournal(2, startedUtc, null, null, null, journalEntries));
        var moved = new List<PlannedLooseFile>(); var removedDuplicates = new List<PlannedLooseFile>(); long done = 0;
        try
        {
            foreach (var item in planned.Where(item => item.Disposition is LooseAssetDisposition.Move or LooseAssetDisposition.Metadata))
            {
                cancellationToken.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.GetDirectoryName(item.DestinationPath)!); File.Move(item.SourcePath, item.DestinationPath, false); moved.Add(item); done++; if ((done & 511) == 0 || done == files) progress?.Report((done, files, item.SourcePath));
            }
            foreach (var item in planned.Where(item => item.Disposition == LooseAssetDisposition.ExactDuplicate))
            {
                cancellationToken.ThrowIfCancellationRequested(); if (!File.Exists(item.DestinationPath) || !FilesEqual(item.SourcePath, item.DestinationPath)) throw new IOException($"Exact-duplicate verification changed during consolidation: {item.SourcePath}"); File.Delete(item.SourcePath); removedDuplicates.Add(item); done++; if ((done & 511) == 0 || done == files) progress?.Report((done, files, item.SourcePath));
            }
        }
        catch
        {
            foreach (var item in removedDuplicates.AsEnumerable().Reverse()) if (!File.Exists(item.SourcePath) && File.Exists(item.DestinationPath)) { Directory.CreateDirectory(Path.GetDirectoryName(item.SourcePath)!); File.Copy(item.DestinationPath, item.SourcePath, false); }
            foreach (var item in moved.AsEnumerable().Reverse()) if (!File.Exists(item.SourcePath) && File.Exists(item.DestinationPath)) { Directory.CreateDirectory(Path.GetDirectoryName(item.SourcePath)!); File.Move(item.DestinationPath, item.SourcePath, false); }
            throw;
        }
        RemoveEmptyDirectories(contentRoot, libraryRoot); if (Directory.Exists(looseRoot) && !Directory.EnumerateFileSystemEntries(looseRoot).Any()) Directory.Delete(looseRoot, false);
        var filesCommittedUtc = DateTimeOffset.UtcNow;
        WriteJsonAtomic(journalPath, new LooseAssetConsolidationJournal(2, startedUtc, filesCommittedUtc, null, null, journalEntries));
        try
        {
            catalogPath = WriteCatalog(plan, cancellationToken);
            WriteJsonAtomic(journalPath, new LooseAssetConsolidationJournal(2, startedUtc, filesCommittedUtc, DateTimeOffset.UtcNow, null, journalEntries));
            return new(true, files, bytes, movedCount, exactCount, 0, journalPath, catalogPath);
        }
        catch (Exception exception)
        {
            WriteJsonAtomic(journalPath, new LooseAssetConsolidationJournal(2, startedUtc, filesCommittedUtc, null, exception.Message, journalEntries));
            return new(true, files, bytes, movedCount, exactCount, 0, journalPath, catalogPath, exception.Message);
        }

        static LooseAssetConsolidationEntry ToJournal(PlannedLooseFile item) => new(item.SourcePath, item.DestinationPath, item.LogicalPath, item.Provenance, item.Bytes, item.Disposition);
    }

    public static string RebuildCatalog(string libraryRoot, CancellationToken cancellationToken = default)
    {
        libraryRoot = Path.GetFullPath(libraryRoot); using var operationLock = AcquireOperationLock(libraryRoot); var plan = LoadPlan(libraryRoot);
        var catalogPath = WriteCatalog(plan, cancellationToken); var journalPath = Path.Combine(libraryRoot, "Reports", "loose-consolidation-journal.json");
        if (!File.Exists(journalPath)) return catalogPath;
        try
        {
            var journal = JsonSerializer.Deserialize<LooseAssetConsolidationJournal>(File.ReadAllText(journalPath));
            if (journal?.FilesCommittedUtc is not null && (journal.CatalogCompletedUtc is null || journal.CatalogRebuildError is not null))
                WriteJsonAtomic(journalPath, journal with { CatalogCompletedUtc = DateTimeOffset.UtcNow, CatalogRebuildError = null });
        }
        catch (JsonException) { }
        return catalogPath;
    }

    private static int CopyLooseBlps(BulkAssetLibraryPlan plan, IProgress<(string Stage, int Done, int Total, string Path)>? progress, CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(plan.SourceRoot, "*", SearchOption.AllDirectories).Where(path => Path.GetExtension(path).Equals(".blp", StringComparison.OrdinalIgnoreCase)).ToArray();
        var copied = 0;
        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var source = files[index]; var relative = Path.GetRelativePath(plan.SourceRoot, source); var mapping = MapLooseRelative(relative, Path.GetFileName(plan.SourceRoot));
            var destination = ContentFirstDestination(plan.LibraryRoot, mapping.Provenance, mapping.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination)) { File.Copy(source, destination, false); File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source)); copied++; }
            else if (!FilesEqual(source, destination))
            {
                var variant = ContentVariantPath(destination, source, false);
                if (File.Exists(variant) && !FilesEqual(source, variant)) variant = ContentVariantPath(destination, source, true);
                if (File.Exists(variant))
                {
                    if (!FilesEqual(source, variant)) throw new IOException($"A content-hash variant path exists with different bytes: {variant}");
                }
                else { File.Copy(source, variant, false); File.SetLastWriteTimeUtc(variant, File.GetLastWriteTimeUtc(source)); copied++; }
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
        var batches = BatchPaths(pending, 28_000, 128);
        await Parallel.ForEachAsync(batches, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken }, async (batch, token) =>
        {
            EnsureCompatibleConverter(await RunConverterAsync(converterPath, batch, token), converterPath);
        });
        // Older converters abort the rest of a batch at the first unsupported BLP. Retry
        // every missing output alone so one invalid modern texture cannot hide valid neighbors.
        var retry = pending.Where(source => !File.Exists(Path.ChangeExtension(source, ".png"))).ToArray();
        await Parallel.ForEachAsync(retry, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken },
            async (source, token) => EnsureCompatibleConverter(await RunConverterAsync(converterPath, [source], token), converterPath));
        var converted = pending.Count(source => File.Exists(Path.ChangeExtension(source, ".png")));
        var rejected = pending.Where(source => !File.Exists(Path.ChangeExtension(source, ".png"))).ToArray();
        var rejectionLog = Path.Combine(root, ".blp-conversion-failures.txt");
        if (rejected.Length > 0) File.WriteAllLines(rejectionLog, rejected.Select(source => Path.GetRelativePath(root, source)));
        else if (File.Exists(rejectionLog)) File.Delete(rejectionLog);
        return (converted, rejected.Length);
    }

    private static async Task<string> RunConverterAsync(string converterPath, IReadOnlyList<string> sources, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(converterPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/M"); foreach (var path in sources) start.ArgumentList.Add(path);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the BLP converter.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken); var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken); await Task.WhenAll(output, error);
        return (await output) + Environment.NewLine + (await error);
    }

    private static void EnsureCompatibleConverter(string output, string converterPath)
    {
        if (output.Contains("Invalid filename '/M'", StringComparison.OrdinalIgnoreCase) || output.Contains("Invalid filename \"/M\"", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The selected executable does not support BLPConverter's /M batch syntax: {converterPath}");
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

    private static string WriteCatalog(BulkAssetLibraryPlan plan, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(plan.LibraryRoot, "asset-catalog.csv"); var temp = path + ".tmp";
        try
        {
            using (var writer = new StreamWriter(temp, false, new UTF8Encoding(true), 1024 * 1024))
            {
                writer.WriteLine("category,format,source,relative_path,bytes");
                foreach (var root in CatalogRoots(plan.LibraryRoot))
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(IsCatalogAssetFile))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(plan.LibraryRoot, file); var extension = Path.GetExtension(file).TrimStart('.').ToUpperInvariant();
                    var source = CatalogSource(relative);
                    writer.WriteLine($"{Csv(Classify(relative))},{Csv(extension)},{Csv(source)},{Csv(relative)},{new FileInfo(file).Length}");
                }
            }
            File.Move(temp, path, true); return path;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    private static IEnumerable<string> CatalogRoots(string libraryRoot)
    {
        var archivesRoot = Path.Combine(libraryRoot, "Archives"); var contentFirst = Path.Combine(archivesRoot, "Content");
        if (Directory.Exists(contentFirst)) yield return contentFirst;
        if (Directory.Exists(archivesRoot)) foreach (var legacy in LegacyArchiveRoots(archivesRoot)) yield return legacy.ContentRoot;
        var loose = Path.Combine(libraryRoot, "Loose", "Content"); if (Directory.Exists(loose)) yield return loose;
    }

    private static bool IsCatalogAssetFile(string path) => !Path.GetFileName(path).Equals(".blp-conversion-failures.txt", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

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
    private static string CatalogSource(string relative)
    {
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length > 1 && parts[0].Equals("Loose", StringComparison.OrdinalIgnoreCase)) return "loose";
        if (parts.Length > 3 && parts[0].Equals("Archives", StringComparison.OrdinalIgnoreCase) && parts[1].Equals("Content", StringComparison.OrdinalIgnoreCase))
            return parts[^2];
        return parts.Length > 1 && parts[0].Equals("Archives", StringComparison.OrdinalIgnoreCase) ? parts[1] : "library";
    }

    private static string ArchiveFolderName(BulkAssetArchivePlan archive) => $"{SafeName(Path.GetFileNameWithoutExtension(archive.RelativePath))}-{archive.Identity}";
    private static string ArchiveStagingRoot(string libraryRoot, BulkAssetArchivePlan archive) => Path.Combine(libraryRoot, ".staging", "Archives", ArchiveFolderName(archive), "Content");

    private static IEnumerable<(string ArchiveFolder, string ContentRoot)> LegacyArchiveRoots(string archivesRoot) => Directory.EnumerateDirectories(archivesRoot)
        .Where(path => !Path.GetFileName(path).Equals("Content", StringComparison.OrdinalIgnoreCase))
        .Select(path => (Path.GetFileName(path), Path.Combine(path, "Content"))).Where(entry => Directory.Exists(entry.Item2));

    private static IEnumerable<(string Label, string Root)> ConversionRoots(string libraryRoot)
    {
        var archivesRoot = Path.Combine(libraryRoot, "Archives"); var contentFirst = Path.Combine(archivesRoot, "Content");
        if (Directory.Exists(contentFirst)) yield return ("Content-first archive library", contentFirst);
        if (Directory.Exists(archivesRoot)) foreach (var legacy in LegacyArchiveRoots(archivesRoot)) yield return (legacy.ArchiveFolder, legacy.ContentRoot);
    }

    private static (long MovedFiles, int Conflicts) RelocateContent(string libraryRoot, string archiveFolder, string sourceRoot, bool apply,
        CancellationToken cancellationToken, Action<string>? visited = null)
    {
        var destinationRoot = Path.Combine(libraryRoot, "Archives", "Content"); var planned = new List<PlannedRelocation>();
        foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested(); var relative = Path.GetRelativePath(sourceRoot, source); var relativeDirectory = Path.GetDirectoryName(relative);
            var destinationDirectory = string.IsNullOrEmpty(relativeDirectory) ? Path.Combine(destinationRoot, archiveFolder) : Path.Combine(destinationRoot, relativeDirectory, archiveFolder);
            var destination = Path.GetFullPath(Path.Combine(destinationDirectory, Path.GetFileName(relative))); EnsureInside(destinationRoot, destination); visited?.Invoke(relative);
            planned.Add(new(source, destination));
        }
        var conflicts = 0;
        foreach (var group in planned.GroupBy(item => item.DestinationPath, StringComparer.OrdinalIgnoreCase))
        {
            var entries = group.ToArray();
            if (File.Exists(group.Key))
            {
                foreach (var entry in entries) { if (FilesEqual(entry.SourcePath, group.Key)) entry.ExactDuplicate = true; else conflicts++; }
                continue;
            }
            var primary = entries[0];
            foreach (var entry in entries.Skip(1)) { if (FilesEqual(primary.SourcePath, entry.SourcePath)) entry.ExactDuplicate = true; else conflicts++; }
        }
        if (!apply || conflicts > 0) return (0, conflicts);

        var moved = new List<PlannedRelocation>(); var removedDuplicates = new List<PlannedRelocation>();
        try
        {
            foreach (var item in planned.Where(item => !item.ExactDuplicate))
            {
                cancellationToken.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.GetDirectoryName(item.DestinationPath)!); File.Move(item.SourcePath, item.DestinationPath, false); moved.Add(item);
            }
            foreach (var item in planned.Where(item => item.ExactDuplicate))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(item.DestinationPath) || !FilesEqual(item.SourcePath, item.DestinationPath)) throw new IOException($"Exact-duplicate verification changed during relocation: {item.SourcePath}");
                File.Delete(item.SourcePath); removedDuplicates.Add(item);
            }
        }
        catch
        {
            foreach (var item in removedDuplicates.AsEnumerable().Reverse()) if (!File.Exists(item.SourcePath) && File.Exists(item.DestinationPath)) { Directory.CreateDirectory(Path.GetDirectoryName(item.SourcePath)!); File.Copy(item.DestinationPath, item.SourcePath, false); }
            foreach (var item in moved.AsEnumerable().Reverse()) if (!File.Exists(item.SourcePath) && File.Exists(item.DestinationPath)) { Directory.CreateDirectory(Path.GetDirectoryName(item.SourcePath)!); File.Move(item.DestinationPath, item.SourcePath, false); }
            throw;
        }
        RemoveEmptyDirectories(sourceRoot, libraryRoot);
        return (moved.Count, 0);
    }

    private static string ContentFirstDestination(string libraryRoot, string provenance, string relative)
    {
        var relativeDirectory = Path.GetDirectoryName(relative);
        var directory = string.IsNullOrEmpty(relativeDirectory)
            ? Path.Combine(libraryRoot, "Archives", "Content", provenance)
            : Path.Combine(libraryRoot, "Archives", "Content", relativeDirectory, provenance);
        var destination = Path.GetFullPath(Path.Combine(directory, Path.GetFileName(relative)));
        EnsureInside(Path.Combine(libraryRoot, "Archives", "Content"), destination);
        return destination;
    }

    private static bool FilesEqual(string left, string right)
    {
        var leftInfo = new FileInfo(left); var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length) return false;
        const int bufferSize = 1024 * 1024;
        using var leftStream = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.SequentialScan);
        using var rightStream = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.SequentialScan);
        var leftBuffer = new byte[bufferSize]; var rightBuffer = new byte[bufferSize];
        while (true)
        {
            var leftRead = leftStream.Read(leftBuffer); var rightRead = rightStream.Read(rightBuffer);
            if (leftRead != rightRead) return false;
            if (leftRead == 0) return true;
            if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead))) return false;
        }
    }

    private static void RemoveEmptyDirectories(string root, string libraryRoot)
    {
        EnsureInside(libraryRoot, root);
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory, false);
        if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root, false);
        var parent = Path.GetDirectoryName(root);
        if (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent, false);
    }
    private static string SafeName(string value) { var invalid = Path.GetInvalidFileNameChars().ToHashSet(); var clean = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim(); return clean.Length > 60 ? clean[..60] : clean; }
    private static string ProvenanceName(string value)
    {
        var original = value.Trim(); var safe = SafeName(original);
        if (safe.Length > 0 && safe is not "." and not ".." && safe.Equals(original, StringComparison.Ordinal)) return safe;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original))).ToLowerInvariant()[..12];
        var prefix = safe is "" or "." or ".." ? "source" : safe; prefix = prefix[..Math.Min(prefix.Length, 47)];
        return $"{prefix}-{hash}";
    }
    private static (string Provenance, string RelativePath) MapLooseRelative(string relative, string? rootProvenance = null)
    {
        var parts = relative.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new InvalidDataException("A loose asset has no relative path.");
        if (parts.Length == 1) return (ProvenanceName(string.IsNullOrWhiteSpace(rootProvenance) ? "Loose root" : rootProvenance), parts[0]);
        var directContentRoot = !string.IsNullOrWhiteSpace(rootProvenance) && (IsContentAnchor(parts[0]) || parts.Length >= 2 && IsRace(parts[0]) && IsGender(parts[1]));
        var provenanceLabel = directContentRoot ? rootProvenance! : parts[0]; var content = directContentRoot ? parts : parts.Skip(1).ToArray();

        // Some collected folders wrap the real patch name around otherwise valid client paths.
        // Retain the most useful wrapper as provenance, but never let it split the logical path.
        var anchor = Array.FindIndex(content, IsContentAnchor);
        if (anchor > 0 && content[anchor - 1].StartsWith("Patch-", StringComparison.OrdinalIgnoreCase)) provenanceLabel = $"{provenanceLabel} - {content[anchor - 1]}";
        if (anchor >= 0) content = content[anchor..];
        else if (content.Length >= 2 && IsRace(content[0]) && IsGender(content[1])) content = ["Character", .. content];
        else if (content.Length == 2 && content[0].StartsWith("Patch-", StringComparison.OrdinalIgnoreCase))
        {
            provenanceLabel = $"{provenanceLabel} - {content[0]}";
            content = [content[1]];
        }
        else if (parts[0].Equals("WOW Mods", StringComparison.OrdinalIgnoreCase) && content.Length == 2 && IsLegUpperTexture(content[1]))
            content = ["Item", "TextureComponents", "LegUpperTexture", content[1]];
        else content = ["_Unmapped", "Package-Art", content[^1]];

        if (content[0].Equals("character-fromscratch", StringComparison.OrdinalIgnoreCase)) content[0] = "Character";
        if (content[0].Equals("Textures", StringComparison.OrdinalIgnoreCase) && content.Length >= 2 && content[1].Equals("Character", StringComparison.OrdinalIgnoreCase)) content = content[1..];
        else if (content[0].Equals("Textures", StringComparison.OrdinalIgnoreCase) && content.Length >= 2 && content[1].Equals("Creature", StringComparison.OrdinalIgnoreCase)) content = content[1..];
        else if (content[0].Equals("Textures", StringComparison.OrdinalIgnoreCase) && content.Length >= 2 && IsRace(content[1])) content = ["Character", .. content[1..]];

        // These are alternate looks for the same Murloc client paths. The variant belongs in
        // provenance so Asset Compare places every look together without filename collisions.
        if (content.Length >= 4 && content[0].Equals("Creature", StringComparison.OrdinalIgnoreCase) && content[1].Equals("Murloc", StringComparison.OrdinalIgnoreCase))
        {
            provenanceLabel = $"{provenanceLabel} - Murloc - {content[2]}";
            content = [content[0], content[1], .. content[3..]];
        }

        // Later clients sometimes omit the gender directory even though the filename states it.
        // Infer it only for a direct race file and only from an unambiguous token.
        if (content.Length == 3 && content[0].Equals("Character", StringComparison.OrdinalIgnoreCase) && IsRace(content[1]))
        {
            var gender = InferGender(Path.GetFileNameWithoutExtension(content[2]));
            if (gender is not null) content = [content[0], content[1], gender, content[2]];
        }

        for (var index = 0; index < content.Length; index++) content[index] = CanonicalSegment(content[index]);
        return (ProvenanceName(provenanceLabel), Path.Combine(content));
    }
    private static bool IsContentAnchor(string value) => ContentAnchors.Contains(value);
    private static bool IsGender(string value) => value.Equals("Male", StringComparison.OrdinalIgnoreCase) || value.Equals("Female", StringComparison.OrdinalIgnoreCase);
    private static bool IsLegUpperTexture(string fileName) => Path.GetFileNameWithoutExtension(fileName).EndsWith("_lu_u", StringComparison.OrdinalIgnoreCase);
    private static string? InferGender(string fileName)
    {
        // "female" contains the substring "male", so the longer token must be tested first.
        if (fileName.Contains("female", StringComparison.OrdinalIgnoreCase)) return "Female";
        return fileName.Contains("male", StringComparison.OrdinalIgnoreCase) ? "Male" : null;
    }
    private static bool IsRace(string value) => CharacterArchetypes.Contains(value.Replace(" ", string.Empty, StringComparison.Ordinal));
    private static string CanonicalSegment(string value)
    {
        var compact = value.Replace(" ", string.Empty, StringComparison.Ordinal);
        return CanonicalSegments.TryGetValue(compact, out var canonical) ? canonical : value;
    }
    private static string Identity(string relativePath, long bytes, long ticks) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{relativePath.ToUpperInvariant()}|{bytes}|{ticks}")))[..12].ToLowerInvariant();
    private static string ContentVariantPath(string destination, string source, bool fullHash)
    {
        using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); if (!fullHash) hash = hash[..24];
        var extension = Path.GetExtension(destination); return destination[..^extension.Length] + $".variant-{hash}{extension}";
    }
    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';

    private static void ValidateRoots(string sourceRoot, string libraryRoot)
    {
        if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException(sourceRoot);
        var relative = Path.GetRelativePath(sourceRoot, libraryRoot);
        if (relative.Equals(".") || !relative.StartsWith(".." + Path.DirectorySeparatorChar) && relative != "..") throw new InvalidOperationException("The asset library must be outside the source tree so it cannot ingest its own output.");
    }
    private static void EnsureInside(string root, string path) { var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path)); if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)) throw new InvalidOperationException($"Output escaped the asset library: {path}"); }
    private static void EnsureExtractionCapacity(string libraryRoot, BulkAssetArchivePlan archive)
    {
        const long ReserveBytes = 32L * 1024 * 1024 * 1024;
        var root = Path.GetPathRoot(Path.GetFullPath(libraryRoot)) ?? throw new InvalidOperationException("The asset library has no drive root.");
        var available = new DriveInfo(root).AvailableFreeSpace;
        var workingBytes = Math.Max(archive.ArchiveBytes * 3L, archive.LogicalBytes * 3L);
        if (available - ReserveBytes < workingBytes)
            throw new IOException($"Skipping {archive.RelativePath}: extraction and PNG conversion require an estimated {workingBytes / (1024d * 1024 * 1024):0.##} GiB plus a 32 GiB safety reserve, but only {available / (1024d * 1024 * 1024):0.##} GiB is free.");
    }
    private static FileStream AcquireOperationLock(string libraryRoot)
    {
        Directory.CreateDirectory(Path.GetFullPath(libraryRoot));
        var path = Path.Combine(Path.GetFullPath(libraryRoot), ".asset-library-operation.lock");
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (IOException exception) { throw new InvalidOperationException("Another Crucible asset-library operation is already running for this library.", exception); }
    }
    private static void WriteCheckpoint(string path, HashSet<string> completed, Dictionary<string, string> failures, int converted) => WriteJsonAtomic(path, new BulkAssetLibraryCheckpoint(DateTimeOffset.UtcNow, completed.Order().ToArray(), failures, converted));
    private static void WriteJsonAtomic<T>(string path, T value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp = path + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, path, true); }
    private sealed class PlannedLooseFile(string sourcePath, string destinationPath, string logicalPath, string provenance, long bytes, LooseAssetDisposition disposition)
    {
        public string SourcePath { get; } = sourcePath; public string DestinationPath { get; } = destinationPath; public string LogicalPath { get; } = logicalPath; public string Provenance { get; } = provenance; public long Bytes { get; } = bytes; public LooseAssetDisposition Disposition { get; set; } = disposition;
    }
    private sealed class PlannedRelocation(string sourcePath, string destinationPath)
    {
        public string SourcePath { get; } = sourcePath; public string DestinationPath { get; } = destinationPath; public bool ExactDuplicate { get; set; }
    }
}
