using System.Text;

namespace WoWCrucible.Core;

public sealed record AssetComparisonDirectory(string LogicalPath, int PngFiles, int ProvenanceSources);
public sealed record AssetComparisonEntry(string LogicalPath, string Provenance, string FileName, string FullPath, long Bytes);
public sealed record AssetComparisonIndex(string LibraryRoot, string ContentRoot, IReadOnlyList<AssetComparisonDirectory> Directories, int TotalPngFiles, string? LooseContentRoot = null);

public static class AssetComparisonService
{
    private const string ArchivePrefix = "Archives\\Content\\";
    private const string LoosePrefix = "Loose\\Content\\";

    public static AssetComparisonIndex BuildIndex(string libraryRoot, CancellationToken cancellationToken = default)
    {
        libraryRoot = Path.GetFullPath(libraryRoot); var contentRoot = Path.Combine(libraryRoot, "Archives", "Content"); var looseContentRoot = Path.Combine(libraryRoot, "Loose", "Content");
        if (!Directory.Exists(contentRoot) && !Directory.Exists(looseContentRoot)) throw new DirectoryNotFoundException($"No content-first archive or loose folder exists under: {libraryRoot}");
        var catalog = Path.Combine(libraryRoot, "asset-catalog.csv");
        var counts = new Dictionary<string, (int Files, HashSet<string> Sources)>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(catalog)) ReadCatalog(catalog, counts, cancellationToken);
        else
        {
            foreach (var root in new[] { contentRoot, looseContentRoot }.Where(Directory.Exists))
            foreach (var file in Directory.EnumerateFiles(root, "*.png", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested(); AddRelative(Path.GetRelativePath(libraryRoot, file), counts);
            }
        }
        var directories = counts.Select(pair => new AssetComparisonDirectory(pair.Key, pair.Value.Files, pair.Value.Sources.Count))
            .OrderBy(directory => directory.LogicalPath, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(libraryRoot, contentRoot, directories, directories.Sum(directory => directory.PngFiles), Directory.Exists(looseContentRoot) ? looseContentRoot : null);
    }

    public static IReadOnlyList<AssetComparisonEntry> GetDirectoryPngs(AssetComparisonIndex index, string logicalPath)
    {
        var result = new List<AssetComparisonEntry>();
        var directory = Path.GetFullPath(Path.Combine(index.ContentRoot, logicalPath)); EnsureInside(index.ContentRoot, directory);
        if (Directory.Exists(directory))
        {
            foreach (var provenanceDirectory in Directory.EnumerateDirectories(directory))
            {
                var provenance = Path.GetFileName(provenanceDirectory);
                foreach (var file in Directory.EnumerateFiles(provenanceDirectory, "*.png", SearchOption.TopDirectoryOnly)) AddEntry(result, logicalPath, provenance, file);
            }
        }
        if (index.LooseContentRoot is { } looseRoot)
        {
            var looseDirectory = Path.GetFullPath(Path.Combine(looseRoot, logicalPath)); EnsureInside(looseRoot, looseDirectory);
            if (Directory.Exists(looseDirectory)) foreach (var file in Directory.EnumerateFiles(looseDirectory, "*.png", SearchOption.TopDirectoryOnly)) AddEntry(result, logicalPath, "Loose", file);
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
        relative = relative.Replace('/', '\\'); string logical; string provenance;
        if (relative.StartsWith(ArchivePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var parts = relative[ArchivePrefix.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return; provenance = parts[^2]; logical = parts.Length == 2 ? string.Empty : string.Join('\\', parts[..^2]);
        }
        else if (relative.StartsWith(LoosePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var parts = relative[LoosePrefix.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1) return; provenance = "Loose"; logical = parts.Length == 1 ? string.Empty : string.Join('\\', parts[..^1]);
        }
        else return;
        if (!counts.TryGetValue(logical, out var value)) value = (0, new(StringComparer.OrdinalIgnoreCase));
        value.Files++; value.Sources.Add(provenance); counts[logical] = value;
    }

    private static void AddEntry(List<AssetComparisonEntry> result, string logicalPath, string provenance, string file)
    {
        var info = new FileInfo(file); result.Add(new(logicalPath, provenance, info.Name, info.FullName, info.Length));
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
