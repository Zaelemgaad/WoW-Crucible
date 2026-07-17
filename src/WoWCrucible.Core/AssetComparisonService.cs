using System.Text;

namespace WoWCrucible.Core;

public sealed record AssetComparisonDirectory(string LogicalPath, int PngFiles, int ProvenanceSources);
public sealed record AssetComparisonEntry(string LogicalPath, string Provenance, string FileName, string FullPath, long Bytes);
public sealed record AssetComparisonIndex(string LibraryRoot, string ContentRoot, IReadOnlyList<AssetComparisonDirectory> Directories, int TotalPngFiles);

public static class AssetComparisonService
{
    private const string ArchivePrefix = "Archives\\Content\\";

    public static AssetComparisonIndex BuildIndex(string libraryRoot, CancellationToken cancellationToken = default)
    {
        libraryRoot = Path.GetFullPath(libraryRoot); var contentRoot = Path.Combine(libraryRoot, "Archives", "Content");
        if (!Directory.Exists(contentRoot)) throw new DirectoryNotFoundException($"The content-first archive folder does not exist: {contentRoot}");
        var catalog = Path.Combine(libraryRoot, "asset-catalog.csv");
        var counts = new Dictionary<string, (int Files, HashSet<string> Sources)>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(catalog)) ReadCatalog(catalog, counts, cancellationToken);
        else
        {
            foreach (var file in Directory.EnumerateFiles(contentRoot, "*.png", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested(); AddRelative(Path.GetRelativePath(libraryRoot, file), counts);
            }
        }
        var directories = counts.Select(pair => new AssetComparisonDirectory(pair.Key, pair.Value.Files, pair.Value.Sources.Count))
            .OrderBy(directory => directory.LogicalPath, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(libraryRoot, contentRoot, directories, directories.Sum(directory => directory.PngFiles));
    }

    public static IReadOnlyList<AssetComparisonEntry> GetDirectoryPngs(AssetComparisonIndex index, string logicalPath)
    {
        var directory = Path.GetFullPath(Path.Combine(index.ContentRoot, logicalPath)); EnsureInside(index.ContentRoot, directory);
        if (!Directory.Exists(directory)) return [];
        var result = new List<AssetComparisonEntry>();
        foreach (var provenanceDirectory in Directory.EnumerateDirectories(directory))
        {
            var provenance = Path.GetFileName(provenanceDirectory);
            foreach (var file in Directory.EnumerateFiles(provenanceDirectory, "*.png", SearchOption.TopDirectoryOnly))
            {
                var info = new FileInfo(file); result.Add(new(logicalPath, provenance, info.Name, info.FullName, info.Length));
            }
        }
        return result.OrderBy(entry => entry.Provenance, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void ReadCatalog(string catalog, Dictionary<string, (int Files, HashSet<string> Sources)> counts, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(catalog, Encoding.UTF8, true, 1024 * 1024); _ = reader.ReadLine(); string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested(); var fields = ParseCsv(line);
            if (fields.Count < 5 || !fields[1].Equals("PNG", StringComparison.OrdinalIgnoreCase)) continue;
            AddRelative(fields[3], counts);
        }
    }

    private static void AddRelative(string relative, Dictionary<string, (int Files, HashSet<string> Sources)> counts)
    {
        relative = relative.Replace('/', '\\'); if (!relative.StartsWith(ArchivePrefix, StringComparison.OrdinalIgnoreCase)) return;
        var parts = relative[ArchivePrefix.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return; var provenance = parts[^2]; var logical = parts.Length == 2 ? string.Empty : string.Join('\\', parts[..^2]);
        if (!counts.TryGetValue(logical, out var value)) value = (0, new(StringComparer.OrdinalIgnoreCase));
        value.Files++; value.Sources.Add(provenance); counts[logical] = value;
    }

    private static IReadOnlyList<string> ParseCsv(string line)
    {
        var fields = new List<string>(); var value = new StringBuilder(); var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"') { value.Append('"'); index++; }
                else quoted = !quoted;
            }
            else if (character == ',' && !quoted) { fields.Add(value.ToString()); value.Clear(); }
            else value.Append(character);
        }
        fields.Add(value.ToString()); return fields;
    }

    private static void EnsureInside(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)) throw new InvalidOperationException("The selected comparison directory escaped the asset library.");
    }
}
