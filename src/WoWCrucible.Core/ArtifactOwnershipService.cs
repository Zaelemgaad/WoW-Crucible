using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoWCrucible.Core;

[JsonConverter(typeof(JsonStringEnumConverter<ArtifactLifecycleCategory>))]
public enum ArtifactLifecycleCategory { Deliverable, Cache, Scratch, Diagnostics, PreimageBackup, Receipt }
public sealed record OwnedArtifact(string RelativePath, string Sha256, long Bytes, string OperationId,
    ArtifactLifecycleCategory Category, bool RequiredByPublishedDeliverable, DateTimeOffset CreatedUtc, DateTimeOffset? ExpiresUtc);
public sealed record ArtifactOwnershipManifest(int FormatVersion, string ProjectId, DateTimeOffset UpdatedUtc, IReadOnlyList<OwnedArtifact> Artifacts);
public sealed record OwnedRunLayout(string OperationId, string RunId, string RootPath, string DeliverablePath, string CachePath,
    string ScratchPath, string DiagnosticsPath, string BackupPath, string ReceiptPath);
public sealed record ArtifactCleanupEntry(string RelativePath, string AbsolutePath, long Bytes, string Sha256, ArtifactLifecycleCategory Category, string OperationId);
public sealed record ArtifactCleanupPlan(string ProjectRoot, string ProjectId, DateTimeOffset PlannedUtc, long ReclaimableBytes, IReadOnlyList<ArtifactCleanupEntry> Entries);
public sealed record ArtifactCleanupResult(long RemovedFiles, long ReclaimedBytes, IReadOnlyList<string> RemovedRelativePaths);

public static class ArtifactOwnershipService
{
    private const int FormatVersion = 1;
    private const string ManifestName = "ownership.crucible.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static void Initialize(string projectRoot, string projectId)
    {
        projectRoot = RequireProjectRoot(projectRoot); if (string.IsNullOrWhiteSpace(projectId)) throw new ArgumentException("A stable project ID is required.", nameof(projectId));
        var path = Path.Combine(projectRoot, ManifestName); if (!File.Exists(path)) WriteAtomic(path, new ArtifactOwnershipManifest(FormatVersion, projectId, DateTimeOffset.UtcNow, []));
    }

    public static OwnedRunLayout CreateRun(string projectOrRoot, string operationId)
    {
        var root = RequireProjectRoot(projectOrRoot); var project = CrucibleContentProjectService.Load(root); Initialize(root, project.ProjectId);
        operationId = SafeToken(operationId, "operation"); var runId = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..32]; var runRoot = Path.Combine(root, "Runs", operationId, runId);
        string Make(string name) { var path = Path.Combine(runRoot, name); Directory.CreateDirectory(path); return path; }
        return new(operationId, runId, runRoot, Make("Deliverable"), Make("Cache"), Make("Scratch"), Make("Diagnostics"), Make("Backup"), Make("Receipt"));
    }

    public static ArtifactOwnershipManifest RegisterFiles(string projectOrRoot, string operationId, ArtifactLifecycleCategory category,
        IEnumerable<string> paths, bool requiredByPublishedDeliverable = false, DateTimeOffset? expiresUtc = null)
    {
        var root = RequireProjectRoot(projectOrRoot); var project = CrucibleContentProjectService.Load(root); Initialize(root, project.ProjectId); operationId = SafeToken(operationId, "operation");
        if ((category is ArtifactLifecycleCategory.Deliverable or ArtifactLifecycleCategory.PreimageBackup or ArtifactLifecycleCategory.Receipt) && expiresUtc is not null)
            throw new InvalidOperationException($"{category} artifacts cannot expire automatically.");
        var manifest = Load(root); var owned = manifest.Artifacts.ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase); var now = DateTimeOffset.UtcNow;
        foreach (var input in paths)
        {
            var absolute = Path.GetFullPath(input); EnsureInside(root, absolute); if (!File.Exists(absolute)) throw new FileNotFoundException("Only existing generated files can be registered as owned artifacts.", absolute);
            var relative = Path.GetRelativePath(root, absolute).Replace(Path.DirectorySeparatorChar, '/'); if (relative is "project.crucible.json" or "ids.crucible.json" or ManifestName) throw new InvalidOperationException("Project control files cannot be registered as operation artifacts.");
            if (!relative.StartsWith("Runs/", StringComparison.OrdinalIgnoreCase) && !CategoryRoot(category).Any(prefix => relative.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A {category} artifact must live in an owned Runs directory or its dedicated project category, not at '{relative}'.");
            var info = new FileInfo(absolute); owned[relative] = new(relative, Hash(absolute), info.Length, operationId, category, requiredByPublishedDeliverable, now, expiresUtc);
        }
        var updated = manifest with { UpdatedUtc = now, Artifacts = owned.Values.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray() }; WriteAtomic(Path.Combine(root, ManifestName), updated); return updated;
    }

    public static ArtifactCleanupPlan PlanCleanup(string projectOrRoot, DateTimeOffset? now = null)
    {
        var root = RequireProjectRoot(projectOrRoot); var project = CrucibleContentProjectService.Load(root); var manifest = Load(root); if (!manifest.ProjectId.Equals(project.ProjectId, StringComparison.Ordinal)) throw new InvalidDataException("The ownership manifest belongs to another project.");
        var instant = now ?? DateTimeOffset.UtcNow; var entries = new List<ArtifactCleanupEntry>();
        foreach (var artifact in manifest.Artifacts)
        {
            if (artifact.RequiredByPublishedDeliverable || artifact.Category is not (ArtifactLifecycleCategory.Cache or ArtifactLifecycleCategory.Scratch or ArtifactLifecycleCategory.Diagnostics)) continue;
            if (artifact.Category == ArtifactLifecycleCategory.Diagnostics && (artifact.ExpiresUtc is null || artifact.ExpiresUtc > instant)) continue;
            var absolute = ResolveOwned(root, artifact.RelativePath); if (!File.Exists(absolute)) continue; var info = new FileInfo(absolute);
            if (info.Length != artifact.Bytes || !Hash(absolute).Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
            entries.Add(new(artifact.RelativePath, absolute, info.Length, artifact.Sha256, artifact.Category, artifact.OperationId));
        }
        return new(root, project.ProjectId, instant, entries.Sum(entry => entry.Bytes), entries.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public static ArtifactCleanupResult ApplyCleanup(ArtifactCleanupPlan plan)
    {
        var root = RequireProjectRoot(plan.ProjectRoot); var project = CrucibleContentProjectService.Load(root); if (!project.ProjectId.Equals(plan.ProjectId, StringComparison.Ordinal)) throw new InvalidDataException("Cleanup plan project identity is stale.");
        var manifest = Load(root); var byPath = manifest.Artifacts.ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase); var removed = new List<string>(); long bytes = 0;
        var verified = new List<(ArtifactCleanupEntry Entry, string Absolute, long Bytes)>();
        foreach (var entry in plan.Entries)
        {
            if (!byPath.TryGetValue(entry.RelativePath, out var owned) || owned.RequiredByPublishedDeliverable ||
                owned.Category is not (ArtifactLifecycleCategory.Cache or ArtifactLifecycleCategory.Scratch or ArtifactLifecycleCategory.Diagnostics) ||
                owned.Category != entry.Category || !owned.OperationId.Equals(entry.OperationId, StringComparison.Ordinal) || owned.Bytes != entry.Bytes ||
                !owned.Sha256.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Cleanup ownership changed after preview: {entry.RelativePath}");
            var absolute = ResolveOwned(root, entry.RelativePath); if (!absolute.Equals(Path.GetFullPath(entry.AbsolutePath), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Cleanup path changed after preview: {entry.RelativePath}");
            if (!File.Exists(absolute)) throw new FileNotFoundException("Cleanup target disappeared after preview.", absolute); var info = new FileInfo(absolute);
            var hash = Hash(absolute); if (info.Length != entry.Bytes || !hash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase) || !hash.Equals(owned.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Cleanup target changed after preview: {entry.RelativePath}");
            verified.Add((entry, absolute, info.Length));
        }
        // Nothing is deleted until every exact target has passed the stale-plan check.
        foreach (var item in verified)
        {
            var entry = item.Entry; var absolute = item.Absolute;
            File.Delete(absolute); bytes += item.Bytes; removed.Add(entry.RelativePath); byPath.Remove(entry.RelativePath); RemoveEmptyOwnedParents(root, Path.GetDirectoryName(absolute)!);
        }
        WriteAtomic(Path.Combine(root, ManifestName), manifest with { UpdatedUtc = DateTimeOffset.UtcNow, Artifacts = byPath.Values.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray() });
        return new(removed.Count, bytes, removed);
    }

    public static ArtifactOwnershipManifest Load(string projectOrRoot)
    {
        var root = RequireProjectRoot(projectOrRoot); var path = Path.Combine(root, ManifestName); if (!File.Exists(path)) throw new FileNotFoundException("The project has no artifact ownership manifest.", path);
        var manifest = JsonSerializer.Deserialize<ArtifactOwnershipManifest>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("The ownership manifest is empty."); if (manifest.FormatVersion != FormatVersion) throw new InvalidDataException($"Unsupported ownership manifest format {manifest.FormatVersion}."); return manifest;
    }

    private static IEnumerable<string> CategoryRoot(ArtifactLifecycleCategory category) => category switch { ArtifactLifecycleCategory.Deliverable => ["Deliverables"], ArtifactLifecycleCategory.Cache => ["Cache"], ArtifactLifecycleCategory.Scratch => ["Staging"], ArtifactLifecycleCategory.Diagnostics => ["Diagnostics","Reports"], ArtifactLifecycleCategory.PreimageBackup => ["Backups"], ArtifactLifecycleCategory.Receipt => ["Receipts","Manifests"], _ => [] };
    private static string RequireProjectRoot(string path) { path = Path.GetFullPath(path); if (File.Exists(path)) path = Path.GetDirectoryName(path)!; if (!File.Exists(Path.Combine(path, "project.crucible.json"))) throw new DirectoryNotFoundException($"No Crucible project exists at {path}"); return path; }
    private static string ResolveOwned(string root, string relative) { if (Path.IsPathRooted(relative)) throw new InvalidDataException("Owned artifact paths must be portable project-relative paths."); var absolute = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))); EnsureInside(root, absolute); return absolute; }
    private static void EnsureInside(string root, string path) { var relative = Path.GetRelativePath(root, path); if (relative is "." or ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidOperationException($"Owned artifact path escapes the project: {path}"); }
    private static string SafeToken(string value, string name) { value = value?.Trim() ?? string.Empty; if (value.Length == 0 || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))) throw new ArgumentException($"The {name} must contain only letters, digits, '-' or '_'."); return value; }
    private static string Hash(string path) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void WriteAtomic<T>(string path, T value) { var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp"; try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions)); File.Move(temporary, path, true); } finally { if (File.Exists(temporary)) File.Delete(temporary); } }
    private static void RemoveEmptyOwnedParents(string root, string directory) { var runs = Path.Combine(root, "Runs"); while (directory.StartsWith(runs + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) { Directory.Delete(directory); directory = Path.GetDirectoryName(directory)!; } }
}
