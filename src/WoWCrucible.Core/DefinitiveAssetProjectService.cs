using System.Security.Cryptography;
using System.Text.Json;

namespace WoWCrucible.Core;

public enum AssetDecision { Review, Keeper, Alternative, Rejected }
public enum DefinitiveAssetKind { Texture, Model, Skin, Animation, Wmo, Other }
public sealed record DefinitiveAssetEntry(string Id, string GroupId, DefinitiveAssetKind Kind, AssetDecision Decision, string Category, string LogicalPath, string ArchivePath, string Provenance, string SourcePath, string PreviewPath, string Sha256, long Bytes, string Notes, DateTimeOffset UpdatedUtc);
public sealed record DefinitiveAssetProject(int FormatVersion, string Name, string LibraryRoot, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, IReadOnlyList<DefinitiveAssetEntry> Entries);
public sealed record DefinitiveAssetStageResult(string RootPath, string ManifestPath, int Files, long Bytes);

public static class DefinitiveAssetProjectService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DefaultPath(string libraryRoot) => Path.Combine(Path.GetFullPath(libraryRoot), "Projects", "Definitive-Set.crucible-assets.json");

    public static DefinitiveAssetProject LoadOrCreate(string projectPath, string libraryRoot)
    {
        projectPath = Path.GetFullPath(projectPath);
        if (!File.Exists(projectPath)) return new(1, "Definitive Asset Set", Path.GetFullPath(libraryRoot), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        var project = JsonSerializer.Deserialize<DefinitiveAssetProject>(File.ReadAllText(projectPath)) ?? throw new InvalidDataException("The Definitive Set project is empty.");
        if (project.FormatVersion != 1) throw new InvalidDataException($"Unsupported Definitive Set format: {project.FormatVersion}");
        return project;
    }

    public static DefinitiveAssetProject RecordTexture(string projectPath, DefinitiveAssetProject project, AssetComparisonEntry entry, AssetDecision decision, string category, string notes)
    {
        var source = Path.ChangeExtension(entry.FullPath, ".blp"); if (!File.Exists(source)) source = entry.FullPath;
        var archiveName = Path.GetFileName(source); var archivePath = CombineArchive(entry.LogicalPath, archiveName);
        return Save(projectPath, Upsert(project, [Create(entry.FullPath, source, entry.LogicalPath, archivePath, entry.Provenance, DefinitiveAssetKind.Texture, decision, category, notes, Group(entry.FullPath))]));
    }

    public static DefinitiveAssetProject RecordModel(string projectPath, DefinitiveAssetProject project, AssetComparisonModel model, AssetDecision decision, string category, string notes)
    {
        if (decision == AssetDecision.Keeper && model.Compatibility != AssetModelCompatibility.Ready)
            throw new InvalidOperationException($"This model cannot become a deployable keeper yet: {model.Status} Record it as Review/Alternative or convert and validate it first.");
        var group = Group(model.ModelPath); var directory = Path.GetDirectoryName(model.ModelPath)!; var stem = Path.GetFileNameWithoutExtension(model.ModelPath);
        var files = new[] { model.ModelPath }.Concat(Directory.EnumerateFiles(directory, stem + "*", SearchOption.TopDirectoryOnly).Where(path => Path.GetExtension(path).Equals(".skin", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".anim", StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.OrdinalIgnoreCase);
        var entries = files.Select(path => Create(model.ModelPath, path, model.LogicalPath, CombineArchive(model.LogicalPath, Path.GetFileName(path)), model.Provenance,
            path.Equals(model.ModelPath, StringComparison.OrdinalIgnoreCase) ? DefinitiveAssetKind.Model : Path.GetExtension(path).Equals(".skin", StringComparison.OrdinalIgnoreCase) ? DefinitiveAssetKind.Skin : DefinitiveAssetKind.Animation,
            decision, category, notes, group)).ToArray();
        return Save(projectPath, Upsert(project, entries));
    }

    public static DefinitiveAssetProject Save(string projectPath, DefinitiveAssetProject project)
    {
        projectPath = Path.GetFullPath(projectPath); Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        var updated = project with { UpdatedUtc = DateTimeOffset.UtcNow };
        var temporary = projectPath + ".tmp"; File.WriteAllText(temporary, JsonSerializer.Serialize(updated, JsonOptions)); File.Move(temporary, projectPath, true); return updated;
    }

    public static DefinitiveAssetStageResult StageKeepers(string projectPath, DefinitiveAssetProject project, string outputRoot)
    {
        outputRoot = Path.GetFullPath(outputRoot); var filesRoot = Path.Combine(outputRoot, "Files"); Directory.CreateDirectory(filesRoot);
        var keepers = project.Entries.Where(entry => entry.Decision == AssetDecision.Keeper).GroupBy(entry => entry.ArchivePath, StringComparer.OrdinalIgnoreCase).Select(group => group.OrderByDescending(entry => entry.UpdatedUtc).First()).ToArray();
        if (keepers.Length == 0) throw new InvalidOperationException("The Definitive Set has no keeper files to stage.");
        var patchEntries = new List<PatchEntry>(); long bytes = 0;
        foreach (var keeper in keepers)
        {
            if (!File.Exists(keeper.SourcePath)) throw new FileNotFoundException("A selected keeper source is missing.", keeper.SourcePath);
            using var stream = File.OpenRead(keeper.SourcePath); var hash = Convert.ToHexString(SHA256.HashData(stream)); if (!hash.Equals(keeper.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Keeper changed after selection: {keeper.SourcePath}");
            var destination = Path.Combine(filesRoot, keeper.ArchivePath); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination) && !FilesEqual(destination, keeper.SourcePath)) throw new IOException($"Staging collision at {keeper.ArchivePath}");
            File.Copy(keeper.SourcePath, destination, true); bytes += keeper.Bytes; patchEntries.Add(new(destination, keeper.ArchivePath));
        }
        var manifest = Path.Combine(outputRoot, "definitive-set.crucible-patch.json"); PatchManifestService.Save(manifest, project.Name, "patch-Crucible-Definitive.MPQ", patchEntries, policy: new(ExpectedEntryCount: patchEntries.Count));
        File.Copy(projectPath, Path.Combine(outputRoot, Path.GetFileName(projectPath)), true); return new(outputRoot, manifest, patchEntries.Count, bytes);
    }

    private static DefinitiveAssetProject Upsert(DefinitiveAssetProject project, IReadOnlyList<DefinitiveAssetEntry> updates)
    {
        var entries = project.Entries.ToDictionary(entry => entry.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var update in updates)
        {
            if (update.Decision == AssetDecision.Keeper)
                foreach (var conflict in entries.Values.Where(entry => entry.Decision == AssetDecision.Keeper && entry.ArchivePath.Equals(update.ArchivePath, StringComparison.OrdinalIgnoreCase) && !entry.Id.Equals(update.Id, StringComparison.OrdinalIgnoreCase)).ToArray()) entries[conflict.Id] = conflict with { Decision = AssetDecision.Alternative, UpdatedUtc = DateTimeOffset.UtcNow };
            entries[update.Id] = update;
        }
        return project with { Entries = entries.Values.OrderBy(entry => entry.ArchivePath, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.Provenance, StringComparer.OrdinalIgnoreCase).ToArray() };
    }

    private static DefinitiveAssetEntry Create(string preview, string source, string logical, string archive, string provenance, DefinitiveAssetKind kind, AssetDecision decision, string category, string notes, string group)
    {
        source = Path.GetFullPath(source); using var stream = File.OpenRead(source); var hash = Convert.ToHexString(SHA256.HashData(stream)); var info = new FileInfo(source);
        var id = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source.ToUpperInvariant())))[..24];
        return new(id, group, kind, decision, string.IsNullOrWhiteSpace(category) ? "Unsorted" : category.Trim(), logical, PatchInputMapper.NormalizeArchivePath(archive), provenance, source, Path.GetFullPath(preview), hash, info.Length, notes.Trim(), DateTimeOffset.UtcNow);
    }

    private static string Group(string path) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())))[..16];
    private static string CombineArchive(string logical, string file) => string.IsNullOrEmpty(logical) ? file : logical.Trim('\\', '/') + "\\" + file;
    private static bool FilesEqual(string left, string right) { var a = new FileInfo(left); var b = new FileInfo(right); if (a.Length != b.Length) return false; using var x = File.OpenRead(left); using var y = File.OpenRead(right); return SHA256.HashData(x).AsSpan().SequenceEqual(SHA256.HashData(y)); }
}
