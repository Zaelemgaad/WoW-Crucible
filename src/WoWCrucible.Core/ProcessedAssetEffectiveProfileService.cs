using System.Text.Json;
using System.Text.RegularExpressions;

namespace WoWCrucible.Core;

public sealed record ProcessedAssetEffectiveLayer(string Identity, string SourceArchivePath, string ArchiveName,
    string Provenance, long? Precedence, string PrecedenceDescription);

public sealed record ProcessedAssetEffectiveProfile(string LibraryRoot, string ClientDataRoot, string SourceProvenance,
    IReadOnlyList<ProcessedAssetEffectiveLayer> Layers, IReadOnlyList<string> Findings)
{
    public string DisplayName => $"Effective client · {ClientDataRoot}";
}

public sealed record ProcessedAssetEffectiveResolution(string ClientPath, ClientEffectiveAssetState State,
    ClientAssetLocation? Effective, IReadOnlyList<ClientAssetLocation> Candidates, string Message);

/// <summary>
/// Reconstructs a coherent root-Data archive stack from the processed library's
/// immutable source registry. This prevents unrelated mods/clients from being
/// mixed while still allowing normal common -> common-2 -> expansion ->
/// lichking -> patch inheritance inside one actual Wrath client.
/// </summary>
public static class ProcessedAssetEffectiveProfileService
{
    private const string RegistryName = "asset-library-sources.json";

    public static ProcessedAssetEffectiveProfile Infer(AssetComparisonIndex index, string sourcePath)
    {
        var source = ClientAssetDependencyService.InferLocation(index, sourcePath); return InferFromProvenance(index, source.Provenance);
    }

    public static ProcessedAssetEffectiveProfile InferFromProvenance(AssetComparisonIndex index, string sourceProvenance)
    {
        var registryPath = Path.Combine(index.LibraryRoot, RegistryName); if (!File.Exists(registryPath)) throw new FileNotFoundException("The processed library has no source-archive registry for effective-profile reconstruction.", registryPath);
        var registry = JsonSerializer.Deserialize<BulkAssetArchiveSourceRegistry>(File.ReadAllText(registryPath)) ?? throw new InvalidDataException("The processed source-archive registry is empty.");
        var identity = ProvenanceIdentity(sourceProvenance) ?? throw new InvalidDataException($"Provenance '{sourceProvenance}' has no registered 12-character archive identity.");
        var source = registry.Archives.LastOrDefault(value => value.Identity.Equals(identity, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"No registered source archive matches provenance '{sourceProvenance}'.");
        var dataRoot = Path.GetFullPath(Path.GetDirectoryName(source.SourcePath) ?? throw new InvalidDataException("Registered source archive has no parent directory."));
        var findings = new List<string>(); var layers = new List<ProcessedAssetEffectiveLayer>();
        foreach (var archive in registry.Archives.Where(value => SameDirectory(value.SourcePath, dataRoot)).GroupBy(value => value.Identity, StringComparer.OrdinalIgnoreCase).Select(group => group.Last()))
        {
            var (rank, description) = Precedence(Path.GetFileNameWithoutExtension(archive.SourcePath)); var provenance = $"{Path.GetFileNameWithoutExtension(archive.SourcePath)}-{archive.Identity}";
            if (rank is null) findings.Add($"Archive {archive.SourcePath} uses a nonstandard root-Data name and cannot win automatic effective precedence.");
            layers.Add(new(archive.Identity, Path.GetFullPath(archive.SourcePath), Path.GetFileName(archive.SourcePath), provenance, rank, description));
        }
        if (layers.Count == 0 || !layers.Any(value => value.Identity.Equals(identity, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("The source archive is not part of a reconstructable root-Data profile.");
        return new(index.LibraryRoot, dataRoot, sourceProvenance, layers.OrderBy(value => value.Precedence ?? long.MinValue).ThenBy(value => value.ArchiveName, StringComparer.OrdinalIgnoreCase).ToArray(), findings);
    }

    public static ProcessedAssetEffectiveResolution Resolve(AssetComparisonIndex index, ProcessedAssetEffectiveProfile profile,
        string rawClientPath, CancellationToken cancellationToken = default)
    {
        var path = PatchInputMapper.NormalizeArchivePath(rawClientPath); var allowed = profile.Layers.ToDictionary(value => value.Identity, StringComparer.OrdinalIgnoreCase);
        var candidates = ClientAssetDependencyService.FindCandidates(index, path, cancellationToken).Where(candidate => ProvenanceIdentity(candidate.Provenance) is { } identity && allowed.ContainsKey(identity)).ToArray();
        if (candidates.Length == 0) return new(path, ClientEffectiveAssetState.Missing, null, [], $"The effective client rooted at {profile.ClientDataRoot} does not supply this processed path.");
        if (candidates.Length == 1) return new(path, ClientEffectiveAssetState.Effective, candidates[0], candidates, $"Resolved from {allowed[ProvenanceIdentity(candidates[0].Provenance)!].ArchiveName}.");
        var ranked = candidates.Select(candidate => (Candidate: candidate, Layer: allowed[ProvenanceIdentity(candidate.Provenance)!])).ToArray();
        if (ranked.Any(value => value.Layer.Precedence is null) || ranked.GroupBy(value => value.Layer.Precedence).Any(group => group.Count() > 1))
            return new(path, ClientEffectiveAssetState.Ambiguous, null, candidates, $"The coherent client contains {candidates.Length:N0} candidates, but standard Wrath precedence is not provable: {string.Join(", ", ranked.Select(value => value.Layer.ArchiveName))}.");
        var effective = ranked.MaxBy(value => value.Layer.Precedence)!.Candidate; var winner = allowed[ProvenanceIdentity(effective.Provenance)!];
        return new(path, ClientEffectiveAssetState.Effective, effective, candidates, $"Resolved to {winner.ArchiveName} ({winner.PrecedenceDescription}) over {candidates.Length - 1:N0} lower client layer(s).");
    }

    private static string? ProvenanceIdentity(string provenance)
    {
        var match = Regex.Match(provenance, "(?:^|-)([0-9a-fA-F]{12})$"); return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    private static bool SameDirectory(string sourcePath, string directory)
    {
        try { return Path.GetFullPath(Path.GetDirectoryName(sourcePath) ?? string.Empty).Equals(directory, StringComparison.OrdinalIgnoreCase); } catch { return false; }
    }

    private static (long? Rank, string Description) Precedence(string name)
    {
        var patch = Regex.Match(name, "^patch(?:-([0-9A-Za-z]))?$", RegexOptions.IgnoreCase);
        if (patch.Success)
        {
            if (!patch.Groups[1].Success) return (800_000, "base patch layer"); var suffix = char.ToUpperInvariant(patch.Groups[1].Value[0]); var suffixRank = char.IsDigit(suffix) ? suffix - '0' : 100 + suffix - 'A'; return (900_000 + suffixRank, $"patch suffix {suffix}");
        }
        return name.ToLowerInvariant() switch { "common" => (100_000, "common base layer"), "common-2" => (200_000, "common-2 base layer"), "expansion" => (300_000, "expansion base layer"), "lichking" => (400_000, "lichking base layer"), _ => (null, "nonstandard archive name") };
    }
}
