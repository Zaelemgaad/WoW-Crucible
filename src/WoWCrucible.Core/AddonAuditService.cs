using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace WoWCrucible.Core;

public sealed record AddonIssue(string Severity, string Code, string Path, string Message);
public sealed record AddonFile(string Path, long Length, string Sha256);
public sealed record AddonPackage(string Name, string Directory, string Manifest,
    IReadOnlyDictionary<string, string> Metadata, IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> SavedVariables, IReadOnlyList<string> CharacterVariables,
    IReadOnlyList<string> LoadFiles, IReadOnlyList<AddonFile> Files, string ContentSha256,
    IReadOnlyList<AddonIssue> Issues)
{
    public string Interface => Metadata.GetValueOrDefault("Interface", "");
    public string Version => Metadata.GetValueOrDefault("Version", "");
    public int Errors => Issues.Count(issue => issue.Severity == "Error");
}
public sealed record AddonAuditReport(string Root, int TargetInterface, DateTimeOffset ScannedUtc,
    IReadOnlyList<AddonPackage> Packages, IReadOnlyList<AddonIssue> DiscoveryIssues);

public static class AddonAuditService
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private static readonly HashSet<string> IgnoredDirectories = new(Paths) { ".git", ".svn", "node_modules" };
    private static readonly HashSet<string> ListMetadata = new(Paths) { "SavedVariables", "SavedVariablesPerCharacter", "Dependencies", "RequiredDeps", "OptionalDeps" };

    public static AddonAuditReport Scan(string root, int targetInterface = 30300,
        IReadOnlyList<string>? exclusions = null, CancellationToken cancellationToken = default)
    {
        root = Path.GetFullPath(root);
        if (targetInterface < 1) throw new ArgumentOutOfRangeException(nameof(targetInterface));
        if (!System.IO.Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var excluded = (exclusions ?? []).Select(Path.GetFullPath).ToArray();
        var packages = new List<AddonPackage>();
        var issues = new List<AddonIssue>();
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (excluded.Any(path => Within(path, directory))) continue;
            try
            {
                if (IsLink(directory)) { issues.Add(new("Info", "LinkBoundary", directory, "Directory link was not traversed.")); continue; }
                var name = Path.GetFileName(directory);
                var manifests = System.IO.Directory.EnumerateFiles(directory).Where(path => Path.GetExtension(path).Equals(".toc", StringComparison.OrdinalIgnoreCase)).Order(Paths).ToArray();
                var manifest = manifests.FirstOrDefault(path => Paths.Equals(Path.GetFileNameWithoutExtension(path), name));
                manifest ??= manifests.FirstOrDefault(path => Parse(path).Metadata.GetValueOrDefault("Interface", "").Split(',', StringSplitOptions.TrimEntries).Contains(targetInterface.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                manifest ??= manifests.FirstOrDefault();
                if (manifest is not null)
                    packages.Add(Inspect(directory, manifest, targetInterface, excluded, cancellationToken));
                else
                    foreach (var child in System.IO.Directory.EnumerateDirectories(directory).OrderDescending(Paths))
                        if (!IgnoredDirectories.Contains(Path.GetFileName(child))) pending.Push(child);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException)
            { issues.Add(new("Error", "ReadFailure", directory, error.Message)); }
        }
        var installations = packages.GroupBy(package => Path.GetDirectoryName(package.Directory)!, Paths);
        foreach (var installation in installations)
        {
            var names = installation.Select(package => package.Name).ToHashSet(Paths);
            foreach (var package in installation)
                foreach (var dependency in package.Dependencies.Where(name => !names.Contains(name) && !name.StartsWith("Blizzard_", StringComparison.OrdinalIgnoreCase)))
                    ((List<AddonIssue>)package.Issues).Add(new("Warning", "MissingDependency", package.Manifest, $"Required addon {dependency} is not beside this package."));
        }
        return new(root, targetInterface, DateTimeOffset.UtcNow, packages.OrderBy(package => package.Directory, Paths).ToArray(), issues);
    }

    public static (Dictionary<string, string> Metadata, List<string> Entries) Parse(string manifest)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<string>();
        foreach (var line in File.ReadLines(manifest))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("##", StringComparison.Ordinal))
            {
                var separator = trimmed.IndexOf(':', 2);
                if (separator > 2)
                {
                    var key = trimmed[2..separator].Trim(); var value = trimmed[(separator + 1)..].Trim();
                    if (ListMetadata.Contains(key) && metadata.TryGetValue(key, out var previous))
                        value = previous + ", " + value;
                    metadata[key] = value;
                }
            }
            else if (trimmed.Length > 0 && !trimmed.StartsWith('#')) entries.Add(trimmed);
        }
        return (metadata, entries);
    }

    private static AddonPackage Inspect(string directory, string manifest, int target, string[] excluded, CancellationToken token)
    {
        var name = Path.GetFileName(directory);
        var (metadata, entries) = Parse(manifest);
        var issues = new List<AddonIssue>(); var files = new List<AddonFile>(); var loadFiles = new List<string>();
        var completeFingerprint = true;
        if (!Paths.Equals(Path.GetFileNameWithoutExtension(manifest), name))
            issues.Add(new("Error", "ManifestName", manifest, $"Legacy clients require {name}.toc at the addon root; flavor-only or differently named manifests need a real compatibility review."));
        var version = metadata.GetValueOrDefault("Interface", "");
        if (!int.TryParse(version, out var declared) || declared != target)
            issues.Add(new("Warning", "InterfaceTarget", manifest, $"Manifest interface '{version}' differs from target {target}. This is not proof of API compatibility or incompatibility."));
        var pending = new Stack<string>(); pending.Push(directory);
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (pending.TryPop(out var current))
        {
            token.ThrowIfCancellationRequested();
            foreach (var child in System.IO.Directory.EnumerateFileSystemEntries(current).Order(Paths))
            {
                if (excluded.Any(path => Within(path, child)))
                { completeFingerprint = false; issues.Add(new("Info", "ExcludedContent", child, "Excluded content was not read; this package cannot be compared as an exact duplicate.")); continue; }
                if (IsLink(child)) { completeFingerprint = false; issues.Add(new("Error", "LinkBoundary", child, "Package fingerprint cannot include a symbolic link or junction.")); continue; }
                if (System.IO.Directory.Exists(child)) { pending.Push(child); continue; }
                using var stream = File.OpenRead(child);
                files.Add(new(Path.GetRelativePath(directory, child).Replace('\\', '/'), stream.Length, Convert.ToHexString(SHA256.HashData(stream))));
            }
        }
        files = files.OrderBy(file => file.Path, Paths).ToList();
        foreach (var file in files)
        {
            fingerprint.AppendData(Encoding.UTF8.GetBytes(file.Path.ToLowerInvariant() + "\0"));
            fingerprint.AppendData(Convert.FromHexString(file.Sha256));
        }
        var visiting = new HashSet<string>(Paths);
        foreach (var entry in entries) Visit(entry, directory);
        return new(name, directory, manifest, metadata,
            Split(metadata.GetValueOrDefault("Dependencies", "") + "," + metadata.GetValueOrDefault("RequiredDeps", "")),
            Split(metadata.GetValueOrDefault("SavedVariables", "")), Split(metadata.GetValueOrDefault("SavedVariablesPerCharacter", "")),
            loadFiles, files, completeFingerprint ? Convert.ToHexString(fingerprint.GetHashAndReset()) : "", issues);

        void Visit(string relative, string parent)
        {
            token.ThrowIfCancellationRequested();
            var native = relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            string path;
            try { path = Path.GetFullPath(Path.Combine(parent, native)); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            { issues.Add(new("Error", "InvalidReference", parent, $"Invalid file reference {relative}: {error.Message}")); return; }
            if (excluded.Any(exclusion => Within(exclusion, path)))
            { issues.Add(new("Info", "ExcludedReference", parent, $"Excluded reference was not read: {relative}")); return; }
            if (!Within(directory, path))
            { issues.Add(new("Warning", "ExternalReference", parent, $"Reference outside this addon needs its client/other-addon context: {relative}")); return; }
            for (var ancestor = Path.GetDirectoryName(path); ancestor is not null && Within(directory, ancestor); ancestor = Path.GetDirectoryName(ancestor))
                if (System.IO.Directory.Exists(ancestor) && IsLink(ancestor))
                { issues.Add(new("Error", "LinkBoundary", path, "Referenced directory link was not followed.")); return; }
            if (!File.Exists(path)) { issues.Add(new("Error", "MissingFile", parent, $"Referenced file does not exist: {relative}")); return; }
            if (IsLink(path)) { issues.Add(new("Error", "LinkBoundary", path, "Referenced link was not followed.")); return; }
            loadFiles.Add(Path.GetRelativePath(directory, path).Replace('\\', '/'));
            if (!Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase)) return;
            if (!visiting.Add(path)) { issues.Add(new("Error", "IncludeCycle", path, "XML includes recursively load the same document.")); return; }
            try
            {
                using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                var document = XDocument.Load(reader);
                foreach (var element in document.Descendants().Where(element => element.Name.LocalName is "Script" or "Include"))
                    if (element.Attribute("file")?.Value is { Length: > 0 } file) Visit(file, Path.GetDirectoryName(path)!);
            }
            catch (XmlException error) { issues.Add(new("Error", "InvalidXml", path, error.Message)); }
            finally { visiting.Remove(path); }
        }
    }

    private static string[] Split(string value) => Regex.Split(value, @"[,\s]+").Where(part => part.Length > 0).Distinct(Paths).ToArray();
    private static bool Within(string root, string candidate) => Paths.Equals(root, candidate) || candidate.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
