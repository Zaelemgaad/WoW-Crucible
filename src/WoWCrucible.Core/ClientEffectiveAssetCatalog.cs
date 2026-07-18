using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WoWCrucible.Core;

public enum ClientEffectiveAssetState { Missing, Effective, Ambiguous }
public sealed record ClientEffectiveAssetCandidate(string ArchiveRelativePath, ClientArchiveScope Scope, MpqFileEntry Entry,
    long? Precedence, string PrecedenceDescription);
public sealed record ClientEffectiveAssetResolution(string ClientPath, ClientEffectiveAssetState State,
    ClientEffectiveAssetCandidate? Effective, IReadOnlyList<ClientEffectiveAssetCandidate> Candidates, string Message);

/// <summary>
/// Resolves a named client path through the active Wrath MPQ layers recorded by a
/// ClientArchiveIndex. Named content indexes are used first; archives with anonymous
/// entries are additionally probed with the exact requested path so absence is never
/// inferred from an incomplete listfile.
/// </summary>
public sealed class ClientEffectiveAssetCatalog
{
    private sealed record IndexedArchive(ClientArchiveSummary Summary, string ArchivePath, IReadOnlyList<MpqFileEntry> Files, long? Precedence, string PrecedenceDescription, bool RequiresExactProbe);
    private readonly IReadOnlyList<IndexedArchive> _archives;
    private readonly Dictionary<string, IReadOnlyList<ClientEffectiveAssetCandidate>> _known;
    private readonly Dictionary<string, ClientEffectiveAssetResolution> _resolutions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _hashes = new(StringComparer.OrdinalIgnoreCase);

    public string IndexDirectory { get; }
    public ClientArchiveIndex Index { get; }
    public int ActiveArchives => _archives.Count;
    public string Fingerprint { get; }

    private ClientEffectiveAssetCatalog(string indexDirectory, ClientArchiveIndex index, IReadOnlyList<IndexedArchive> archives,
        Dictionary<string, IReadOnlyList<ClientEffectiveAssetCandidate>> known)
    {
        IndexDirectory = indexDirectory; Index = index; _archives = archives; _known = known;
        var identity = string.Join('\n', archives.OrderBy(archive => archive.Summary.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(archive => $"{archive.Summary.RelativePath.ToUpperInvariant()}\t{archive.Summary.Length}\t{archive.Summary.LastWriteUtcTicks}\t{archive.Summary.Sha256 ?? "NO_ARCHIVE_HASH"}"));
        Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{index.ActiveLocale ?? "-"}\n{identity}")));
    }

    public static ClientEffectiveAssetCatalog Load(string indexDirectory, CancellationToken cancellationToken = default)
    {
        indexDirectory = Path.GetFullPath(indexDirectory);
        var index = ClientArchiveIndexService.Load(indexDirectory);
        if (!index.Complete) throw new InvalidDataException("Target-client satisfaction requires a complete client index; finish or resume indexing first.");
        var clientRoot = Path.GetFullPath(index.ClientRoot); var archives = new List<IndexedArchive>();
        foreach (var summary in index.Archives.Where(archive => archive.Scope is ClientArchiveScope.RootData or ClientArchiveScope.ActiveLocale))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var archivePath = SafeChild(clientRoot, summary.RelativePath, "indexed archive");
            if (!File.Exists(archivePath)) throw new FileNotFoundException("An indexed target-client archive no longer exists.", archivePath);
            var contentPath = SafeChild(indexDirectory, summary.ContentIndexFile, "archive content index");
            var content = File.Exists(contentPath) ? JsonSerializer.Deserialize<ArchiveContentIndex>(File.ReadAllText(contentPath)) : null;
            var files = content?.Files.Where(file => !file.IsMetadata && !ClientArchiveIndexService.IsAnonymous(file.ArchivePath)).ToArray() ?? [];
            var (precedence, description) = Precedence(summary.RelativePath, summary.Scope, index.ActiveLocale);
            archives.Add(new(summary, archivePath, files, precedence, description, summary.AnonymousFiles > 0 || summary.Error is not null || content is null));
        }
        if (archives.Count == 0) throw new InvalidDataException("The target client index contains no active root-data or active-locale MPQ archives.");
        var accumulating = new Dictionary<string, List<ClientEffectiveAssetCandidate>>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in archives)
            foreach (var file in archive.Files)
            {
                cancellationToken.ThrowIfCancellationRequested(); var path = Normalize(file.ArchivePath);
                if (!accumulating.TryGetValue(path, out var candidates)) accumulating[path] = candidates = [];
                candidates.Add(new(archive.Summary.RelativePath, archive.Summary.Scope, file, archive.Precedence, archive.PrecedenceDescription));
            }
        var known = accumulating.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<ClientEffectiveAssetCandidate>)pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        return new(indexDirectory, index, archives, known);
    }

    public ClientEffectiveAssetResolution Resolve(string clientPath, string? preferredArchive = null, CancellationToken cancellationToken = default)
    {
        clientPath = Normalize(clientPath); preferredArchive = string.IsNullOrWhiteSpace(preferredArchive) ? null : PatchInputMapper.NormalizeArchivePath(preferredArchive);
        var cacheKey = preferredArchive is null ? clientPath : clientPath + "\u001f" + preferredArchive; if (_resolutions.TryGetValue(cacheKey, out var cached)) return cached;
        var candidates = _known.TryGetValue(clientPath, out var known) ? known.ToList() : [];
        var failures = new List<string>();
        foreach (var archive in _archives.Where(archive => archive.RequiresExactProbe && !candidates.Any(candidate => candidate.ArchiveRelativePath.Equals(archive.Summary.RelativePath, StringComparison.OrdinalIgnoreCase))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in new PatchArchiveService().ListFiles(archive.ArchivePath, clientPath).Where(entry => !entry.IsMetadata && Normalize(entry.ArchivePath).Equals(clientPath, StringComparison.OrdinalIgnoreCase)))
                    candidates.Add(new(archive.Summary.RelativePath, archive.Summary.Scope, entry, archive.Precedence, archive.PrecedenceDescription));
            }
            catch (Exception exception) { failures.Add($"{archive.Summary.RelativePath}: {exception.Message}"); }
        }
        candidates = candidates.DistinctBy(candidate => $"{candidate.ArchiveRelativePath}\u001f{candidate.Entry.ArchivePath}\u001f{candidate.Entry.Locale}", StringComparer.OrdinalIgnoreCase).ToList();
        ClientEffectiveAssetResolution result;
        if (preferredArchive is not null)
        {
            var selected = candidates.Where(candidate => PatchInputMapper.NormalizeArchivePath(candidate.ArchiveRelativePath).Equals(preferredArchive, StringComparison.OrdinalIgnoreCase)).ToArray();
            result = selected.Length switch
            {
                1 => new(clientPath, ClientEffectiveAssetState.Effective, selected[0], candidates, $"Target archive {selected[0].ArchiveRelativePath} was selected explicitly from {candidates.Count:N0} candidate layer(s)."),
                0 => new(clientPath, ClientEffectiveAssetState.Ambiguous, null, candidates, $"The explicit target archive choice '{preferredArchive}' does not contain this path in the current index."),
                _ => new(clientPath, ClientEffectiveAssetState.Ambiguous, null, candidates, $"The explicit target archive '{preferredArchive}' contains multiple locale variants of this path; select a locale-aware extraction before inheritance.")
            };
        }
        else if (failures.Count > 0)
            result = new(clientPath, ClientEffectiveAssetState.Ambiguous, null, candidates, $"Could not prove effective target content because {failures.Count:N0} active archive probe(s) failed: {string.Join(" | ", failures.Take(3))}");
        else if (candidates.Count == 0)
            result = new(clientPath, ClientEffectiveAssetState.Missing, null, [], "The target client does not contain this exact named path in any active indexed archive.");
        else if (candidates.Count == 1)
            result = new(clientPath, ClientEffectiveAssetState.Effective, candidates[0], candidates, $"Target supplies this path from {candidates[0].ArchiveRelativePath}.");
        else if (candidates.Any(candidate => candidate.Precedence is null) || candidates.GroupBy(candidate => candidate.Precedence).Any(group => group.Count() > 1))
            result = new(clientPath, ClientEffectiveAssetState.Ambiguous, null, candidates, $"The target contains this path in {candidates.Count:N0} archives, but their effective order is not provable from standard Wrath archive naming.");
        else
        {
            var effective = candidates.MaxBy(candidate => candidate.Precedence) ?? throw new InvalidOperationException();
            result = new(clientPath, ClientEffectiveAssetState.Effective, effective, candidates, $"Effective target path resolves to {effective.ArchiveRelativePath} ({effective.PrecedenceDescription}) over {candidates.Count - 1:N0} lower layer(s).");
        }
        _resolutions[cacheKey] = result; return result;
    }

    public bool ContentEquals(string sourcePath, ClientEffectiveAssetResolution resolution, CancellationToken cancellationToken = default)
    {
        sourcePath = Path.GetFullPath(sourcePath); var effective = resolution.Effective ?? throw new InvalidOperationException("An effective target candidate is required for content comparison.");
        if (new FileInfo(sourcePath).Length != effective.Entry.Size) return false;
        var sourceHash = HashFile(sourcePath, cancellationToken); var targetHash = HashEffective(effective, cancellationToken);
        return sourceHash.Equals(targetHash, StringComparison.OrdinalIgnoreCase);
    }

    public string HashEffective(ClientEffectiveAssetCandidate candidate, CancellationToken cancellationToken = default)
    {
        var key = $"{candidate.ArchiveRelativePath}\u001f{candidate.Entry.ArchivePath}\u001f{candidate.Entry.Locale}";
        if (_hashes.TryGetValue(key, out var cached)) return cached;
        var archive = _archives.Single(value => value.Summary.RelativePath.Equals(candidate.ArchiveRelativePath, StringComparison.OrdinalIgnoreCase));
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"wow-crucible-effective-{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            new PatchArchiveService().Extract(archive.ArchivePath, temporaryRoot, [candidate.Entry], cancellationToken: cancellationToken);
            var extracted = SafeChild(temporaryRoot, Normalize(candidate.Entry.ArchivePath), "extracted target asset");
            if (!File.Exists(extracted)) throw new IOException($"The effective target asset was not extracted: {candidate.Entry.ArchivePath}");
            var hash = HashFile(extracted, cancellationToken); _hashes[key] = hash; return hash;
        }
        finally { try { if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, true); } catch { } }
    }

    private static (long? Rank, string Description) Precedence(string relativePath, ClientArchiveScope scope, string? activeLocale)
    {
        var name = Path.GetFileNameWithoutExtension(relativePath); var scopeRank = scope == ClientArchiveScope.RootData ? 2_000_000L : 1_000_000L;
        var locale = activeLocale is null ? "[a-z]{2}[A-Z]{2}" : Regex.Escape(activeLocale);
        var patch = scope == ClientArchiveScope.RootData ? Regex.Match(name, "^patch(?:-([0-9A-Za-z]))?$", RegexOptions.IgnoreCase) : Regex.Match(name, $"^patch-{locale}(?:-([0-9A-Za-z]))?$", RegexOptions.IgnoreCase);
        if (patch.Success)
        {
            if (!patch.Groups[1].Success) return (scopeRank + 800_000, "base patch layer");
            var suffix = char.ToUpperInvariant(patch.Groups[1].Value[0]); var suffixRank = char.IsDigit(suffix) ? suffix - '0' : 100 + suffix - 'A';
            return (scopeRank + 900_000 + suffixRank, $"patch suffix {suffix}");
        }
        var lower = name.ToLowerInvariant();
        if (scope == ClientArchiveScope.RootData)
        {
            if (lower == "lichking") return (scopeRank + 400_000, "lichking base layer");
            if (lower == "expansion") return (scopeRank + 300_000, "expansion base layer");
            if (lower == "common-2") return (scopeRank + 200_000, "common-2 base layer");
            if (lower == "common") return (scopeRank + 100_000, "common base layer");
        }
        else
        {
            if (lower.StartsWith("lichking-locale-")) return (scopeRank + 400_000, "lichking locale layer");
            if (lower.StartsWith("expansion-locale-")) return (scopeRank + 300_000, "expansion locale layer");
            if (lower.StartsWith("locale-")) return (scopeRank + 200_000, "locale base layer");
            if (lower.StartsWith("base-")) return (scopeRank + 100_000, "locale base archive");
        }
        return (null, "nonstandard archive name");
    }

    private static string HashFile(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[1024 * 1024]; int read;
        while ((read = stream.Read(buffer)) > 0) { cancellationToken.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string Normalize(string path) => PatchInputMapper.NormalizeArchivePath(path);
    private static string SafeChild(string root, string relative, string label)
    {
        root = Path.GetFullPath(root); var path = Path.GetFullPath(Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar)));
        var check = Path.GetRelativePath(root, path); if (check == ".." || check.StartsWith(".." + Path.DirectorySeparatorChar)) throw new InvalidDataException($"The {label} escapes its declared root: {relative}");
        return path;
    }
}
