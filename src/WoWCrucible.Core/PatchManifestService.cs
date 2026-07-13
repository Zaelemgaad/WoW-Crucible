using System.Text.Json;

namespace WoWCrucible.Core;

public sealed record PatchManifest(int FormatVersion, string Name, string OutputFileName, IReadOnlyList<PatchEntry> Entries);

public static class PatchManifestService
{
    public static void Save(string manifestPath, string name, string outputFileName, IEnumerable<PatchEntry> entries)
    {
        manifestPath = Path.GetFullPath(manifestPath);
        var directory = Path.GetDirectoryName(manifestPath)!; Directory.CreateDirectory(directory);
        var portable = entries.Select(entry => new PatchEntry(Path.GetRelativePath(directory, Path.GetFullPath(entry.SourcePath)), PatchInputMapper.NormalizeArchivePath(entry.ArchivePath))).ToArray();
        var manifest = new PatchManifest(1, name, Path.GetFileName(outputFileName), portable);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static PatchManifest Load(string manifestPath)
    {
        manifestPath = Path.GetFullPath(manifestPath);
        var manifest = JsonSerializer.Deserialize<PatchManifest>(File.ReadAllText(manifestPath)) ?? throw new InvalidDataException("The patch manifest is empty.");
        if (manifest.FormatVersion != 1) throw new InvalidDataException($"Unsupported patch manifest version: {manifest.FormatVersion}");
        var directory = Path.GetDirectoryName(manifestPath)!;
        var resolved = manifest.Entries.Select(entry => new PatchEntry(Path.GetFullPath(Path.Combine(directory, entry.SourcePath)), PatchInputMapper.NormalizeArchivePath(entry.ArchivePath))).ToArray();
        return manifest with { Entries = resolved };
    }

    public static void Build(string manifestPath, string outputDirectory)
    {
        var manifest = Load(manifestPath);
        new PatchArchiveService().Create(Path.Combine(Path.GetFullPath(outputDirectory), manifest.OutputFileName), manifest.Entries);
    }
}
