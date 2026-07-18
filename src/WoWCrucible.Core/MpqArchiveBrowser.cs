namespace WoWCrucible.Core;

public sealed record MpqBrowserNode(string Name, string ArchivePath, bool IsFolder, int FileCount, long Size, long CompressedSize, MpqFileEntry? Entry)
{
    public string Kind => IsFolder ? "folder" : Entry?.IsMetadata == true ? "metadata" : ClientArchiveIndexService.IsAnonymous(ArchivePath) ? "anonymous" : "file";
}

public sealed record MpqFolderPage(string CurrentFolder, IReadOnlyList<string> Breadcrumbs, IReadOnlyList<MpqBrowserNode> Nodes, int RecursiveFiles, long RecursiveBytes, int AnonymousFiles);

public static class MpqArchiveBrowser
{
    public static MpqFolderPage Browse(IReadOnlyList<MpqFileEntry> entries, string? folder)
    {
        var current = NormalizeFolder(folder); var prefix = current.Length == 0 ? string.Empty : current + "\\";
        var descendants = entries.Select(entry => (Entry: entry, Path: NormalizeEntry(entry.ArchivePath)))
            .Where(value => value.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && value.Path.Length > prefix.Length).ToArray();
        var directories = new Dictionary<string, List<(MpqFileEntry Entry, string Path)>>(StringComparer.OrdinalIgnoreCase); var files = new List<MpqBrowserNode>();
        foreach (var value in descendants)
        {
            var relative = value.Path[prefix.Length..]; var separator = relative.IndexOf('\\');
            if (separator >= 0)
            {
                var name = relative[..separator]; if (!directories.TryGetValue(name, out var children)) directories[name] = children = []; children.Add(value); continue;
            }
            files.Add(new(relative, value.Path, false, 1, value.Entry.Size, value.Entry.CompressedSize, value.Entry));
        }
        var nodes = directories.Select(pair => new MpqBrowserNode(pair.Key, prefix + pair.Key, true, pair.Value.Count, pair.Value.Sum(value => value.Entry.Size), pair.Value.Sum(value => value.Entry.CompressedSize), null))
            .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase).Concat(files.OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase).ThenBy(node => node.Entry?.Locale)).ToArray();
        var breadcrumbs = current.Length == 0 ? [] : current.Split('\\').Select((_, index) => string.Join('\\', current.Split('\\').Take(index + 1))).ToArray();
        return new(current, breadcrumbs, nodes, descendants.Length, descendants.Sum(value => value.Entry.Size), descendants.Count(value => ClientArchiveIndexService.IsAnonymous(value.Path)));
    }

    public static IReadOnlyList<MpqFileEntry> Select(IReadOnlyList<MpqFileEntry> entries, IEnumerable<MpqBrowserNode> nodes)
    {
        var selected = nodes.ToArray(); if (selected.Length == 0) return [];
        var exact = selected.Where(node => !node.IsFolder && node.Entry is not null).Select(node => node.Entry!).ToHashSet(); var folders = selected.Where(node => node.IsFolder).Select(node => node.ArchivePath + "\\").ToArray();
        return entries.Where(entry => exact.Contains(entry) || folders.Any(prefix => NormalizeEntry(entry.ArchivePath).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).Distinct().ToArray();
    }

    public static IReadOnlyList<MpqFileEntry> SelectFolder(IReadOnlyList<MpqFileEntry> entries, string? folder)
    {
        var current = NormalizeFolder(folder); if (current.Length == 0) return entries.ToArray(); var prefix = current + "\\";
        return entries.Where(entry => NormalizeEntry(entry.ArchivePath).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public static string Parent(string? folder)
    {
        var current = NormalizeFolder(folder); var separator = current.LastIndexOf('\\'); return separator < 0 ? string.Empty : current[..separator];
    }

    private static string NormalizeFolder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty; var normalized = value.Replace('/', '\\').Trim('\\');
        if (normalized.Split('\\').Any(part => part is "" or "." or "..")) throw new ArgumentException("MPQ folder navigation cannot contain empty, current, or parent path segments."); return normalized;
    }
    private static string NormalizeEntry(string value) => value.Replace('/', '\\').TrimStart('\\');
}
