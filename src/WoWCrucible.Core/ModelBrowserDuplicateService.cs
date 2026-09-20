using System.Security.Cryptography;
using System.Text;

namespace WoWCrucible.Core;

/// <summary>Groups byte-identical model bundles without deleting source paths or merging different animations.</summary>
internal static class ModelBrowserDuplicateService
{
    public static IReadOnlyList<ModelBrowserEntry> Group(ModelBrowserCatalog catalog, CancellationToken cancellation)
    {
        var groups = new Dictionary<string, List<ModelBrowserEntry>>(StringComparer.Ordinal);
        var unresolved = new List<ModelBrowserEntry>();
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in catalog.Models)
        {
            cancellation.ThrowIfCancellationRequested();
            if (entry.Error is not null) { unresolved.Add(entry); continue; }
            try
            {
                using var source = new ModelBrowserSource(entry, catalog);
                var model = new M2ModelSource(source);
                var companions = new HashSet<string>(model.CompanionFiles, StringComparer.OrdinalIgnoreCase);
                var missing = new HashSet<string>(StringComparer.Ordinal);
                foreach (var reference in model.ReferencedCompanions())
                {
                    if (reference.File is { } file) companions.Add(file);
                    else missing.Add($"unresolved:{reference.FileDataId}{reference.Extension}");
                }
                // Keep even unused LODs and animation variants distinct. Textures remain interchangeable in the viewer.
                foreach (var extension in new[] { ".skin", ".skel", ".anim", ".bone" })
                    companions.UnionWith(source.Nearby(extension));
                var skeletonFolders = model.CompanionFiles.Where(path => path.EndsWith(".skel", StringComparison.OrdinalIgnoreCase))
                    .Select(ModelBrowserSource.DirectoryName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var folder in skeletonFolders)
                {
                    companions.UnionWith(source.Nearby(".anim", folder));
                    companions.UnionWith(source.Nearby(".bone", folder));
                }
                var stem = Path.GetFileNameWithoutExtension(source.ModelName);
                var parts = new List<string> { "model:" + Hash(source.ModelName) };
                parts.AddRange(missing);
                foreach (var path in companions)
                {
                    var name = Path.GetFileName(path);
                    if (name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) name = "$model" + name[stem.Length..];
                    parts.Add(name.ToUpperInvariant() + ":" + Hash(path));
                }
                parts.Sort(StringComparer.Ordinal);
                var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts))));
                if (!groups.TryGetValue(fingerprint, out var copies)) groups.Add(fingerprint, copies = []);
                copies.Add(entry);

                string Hash(string path)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var identity = entry.ArchiveEntry is null ? Path.Combine(catalog.Root, path) : entry.FilePath + "|" + path;
                    if (!hashes.TryGetValue(identity, out var hash)) hashes[identity] = hash = Convert.ToHexString(SHA256.HashData(source.Read(path)));
                    return hash;
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
            {
                // An incomplete bundle cannot prove equivalence; leave it available for repair.
                unresolved.Add(entry);
            }
        }
        return groups.Values.Select(copies =>
        {
            var ordered = copies.OrderBy(entry => entry.ArchiveEntry is null ? 0 : 1)
                .ThenBy(entry => entry.RelativePath.Length).ThenBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
            return ordered[0] with { Copies = ordered.Skip(1).ToArray() };
        }).Concat(unresolved).OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
