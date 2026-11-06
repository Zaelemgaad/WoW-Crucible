using System.Security.Cryptography;
using System.Text;

namespace WoWCrucible.Core;

/// <summary>Finds files the WoW client itself uses, so previews match the client without redistributing them.</summary>
public static class ClientAssetLocator
{
    public const string TooltipFont = @"Fonts\FRIZQT__.TTF";

    public static string? Find(string clientDataPath, string cacheDirectory, string internalPath = TooltipFont) =>
        FindAll(clientDataPath, cacheDirectory, [internalPath]).GetValueOrDefault(internalPath);

    /// <summary>
    /// Returns a loose copy of each found <paramref name="internalPaths"/> entry: a loose override beside Data, otherwise the
    /// highest-priority archive containing it (locale patches, then patches, then base archives), extracted once into
    /// <paramref name="cacheDirectory"/>. Archives are opened in a single pass. Missing files are absent from the result.
    /// </summary>
    public static IReadOnlyDictionary<string, string> FindAll(string clientDataPath, string cacheDirectory, IReadOnlyList<string> internalPaths)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(clientDataPath) || !Directory.Exists(clientDataPath)) return found;
        var clientRoot = Path.GetDirectoryName(Path.GetFullPath(clientDataPath).TrimEnd('\\', '/'))!;
        var archives = ArchivesByPriority(clientDataPath).ToArray();
        // Opening every large archive is slow, so remember each result; any archive change invalidates it.
        var stamp = Hash(string.Join("\n", archives.Select(archive => { var info = new FileInfo(archive); return $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"; })));
        var pending = new List<string>();
        foreach (var path in internalPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var loose = Path.Combine(clientRoot, path);
            if (File.Exists(loose)) found[path] = loose;
            else if (File.Exists(Remembered(path)) && File.ReadAllText(Remembered(path)) is var remembered && (remembered.Length == 0 || File.Exists(remembered)))
            {
                if (remembered.Length > 0) found[path] = remembered; // An empty record means a full scan found no copy.
            }
            else pending.Add(path);
        }
        if (pending.Count == 0) return found;
        Directory.CreateDirectory(cacheDirectory);
        var service = new PatchArchiveService();
        foreach (var archive in archives)
        {
            IReadOnlyList<string> present;
            try { present = service.ContainsAll(archive, pending); }
            catch (Exception exception) when (exception is not OperationCanceledException) { continue; } // An unreadable archive cannot supply files.
            foreach (var path in present)
            {
                try
                {
                    var destination = Path.Combine(cacheDirectory, stamp);
                    var extracted = Path.Combine(destination, path);
                    if (!File.Exists(extracted))
                    {
                        var entry = service.ListFiles(archive, "*" + Path.GetFileName(path))
                            .First(file => PatchInputMapper.NormalizeArchivePath(file.ArchivePath).Equals(PatchInputMapper.NormalizeArchivePath(path), StringComparison.OrdinalIgnoreCase));
                        service.Extract(archive, destination, [entry]);
                    }
                    if (!File.Exists(extracted)) continue;
                    File.WriteAllText(Remembered(path), extracted);
                    found[path] = extracted;
                    pending.Remove(path);
                }
                catch (Exception exception) when (exception is not OperationCanceledException) { } // An unreadable archive cannot supply the file.
            }
            if (pending.Count == 0) break;
        }
        foreach (var path in pending) File.WriteAllText(Remembered(path), string.Empty);
        return found;

        string Remembered(string path) => Path.Combine(cacheDirectory, Hash(stamp + "\n" + path) + ".resolved");
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    private static IEnumerable<string> ArchivesByPriority(string dataPath)
    {
        static IEnumerable<string> Mpqs(string folder, string pattern) => Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, pattern).OrderByDescending(path => Path.GetFileNameWithoutExtension(path).Length).ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            : [];
        var localeFolders = Directory.EnumerateDirectories(dataPath).Where(folder => Path.GetFileName(folder).Length == 4).ToArray();
        return localeFolders.SelectMany(folder => Mpqs(folder, "patch*.MPQ"))
            .Concat(Mpqs(dataPath, "patch*.MPQ"))
            .Concat(localeFolders.SelectMany(folder => Mpqs(folder, "*.MPQ").Where(path => !Path.GetFileName(path).StartsWith("patch", StringComparison.OrdinalIgnoreCase))))
            .Concat(Mpqs(dataPath, "*.MPQ").Where(path => !Path.GetFileName(path).StartsWith("patch", StringComparison.OrdinalIgnoreCase)));
    }
}
