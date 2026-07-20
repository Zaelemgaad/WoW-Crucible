using System.Text.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace WoWCrucible.Core;

public sealed record PatchManifestPolicy(IReadOnlyList<string>? AllowedGlobs = null, IReadOnlyList<string>? ForbiddenGlobs = null, int? ExpectedEntryCount = null, IReadOnlyList<string>? RequiredGlobs = null);
public sealed record PatchInheritedAssetRequirement(string ClientPath, string EffectiveArchive, string Sha256);
public sealed record PatchTargetClientRequirement(string ClientName, string? ActiveLocale, string IndexFingerprint, IReadOnlyList<PatchInheritedAssetRequirement> InheritedAssets);
public sealed record PatchManifest(int FormatVersion, string Name, string OutputFileName, IReadOnlyList<PatchEntry> Entries, string? RequiredClientExecutableSha256 = null, PatchManifestPolicy? Policy = null, PatchTargetClientRequirement? TargetClient = null);
public sealed record PatchCompatibilityIssue(string Code, string Message);
public sealed record PatchManifestValidationIssue(string Code, string Message, string? ArchivePath = null);
public sealed record PatchManifestValidationResult(IReadOnlyList<PatchManifestValidationIssue> Errors, IReadOnlyList<PatchManifestValidationIssue> Warnings)
{
    public bool Passed => Errors.Count == 0;
}

public static class PatchManifestService
{
    public static void Save(string manifestPath, string name, string outputFileName, IEnumerable<PatchEntry> entries, string? requiredClientExecutableSha256 = null, PatchManifestPolicy? policy = null, PatchTargetClientRequirement? targetClient = null)
    {
        manifestPath = Path.GetFullPath(manifestPath);
        var directory = Path.GetDirectoryName(manifestPath)!; Directory.CreateDirectory(directory);
        var materialized = entries.ToArray();
        var validation = ValidateEntries(materialized, policy);
        var targetErrors = ValidateTarget(targetClient); if (!validation.Passed || targetErrors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, validation.Errors.Select(error => error.Message).Concat(targetErrors.Select(error => error.Message))));
        var portable = materialized.Select(entry => new PatchEntry(Path.GetRelativePath(directory, Path.GetFullPath(entry.SourcePath)), PatchInputMapper.NormalizeArchivePath(entry.ArchivePath), entry.Locale)).ToArray();
        var manifest = new PatchManifest(portable.Any(entry => entry.Locale != 0) ? 5 : targetClient is null ? 3 : 4, name, Path.GetFileName(outputFileName), portable, NormalizeSha256(requiredClientExecutableSha256), NormalizePolicy(policy), NormalizeTarget(targetClient));
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static PatchManifest Load(string manifestPath)
    {
        manifestPath = Path.GetFullPath(manifestPath);
        var manifest = JsonSerializer.Deserialize<PatchManifest>(File.ReadAllText(manifestPath)) ?? throw new InvalidDataException("The patch manifest is empty.");
        if (manifest.FormatVersion is < 1 or > 5) throw new InvalidDataException($"Unsupported patch manifest version: {manifest.FormatVersion}");
        var directory = Path.GetDirectoryName(manifestPath)!;
        var resolved = manifest.Entries.Select(entry => { MpqLocale.Validate(entry.Locale); return new PatchEntry(Path.GetFullPath(Path.Combine(directory, entry.SourcePath)), PatchInputMapper.NormalizeArchivePath(entry.ArchivePath), entry.Locale); }).ToArray();
        return manifest with { Entries = resolved, RequiredClientExecutableSha256 = NormalizeSha256(manifest.RequiredClientExecutableSha256), Policy = NormalizePolicy(manifest.Policy), TargetClient = NormalizeTarget(manifest.TargetClient) };
    }

    public static void Build(string manifestPath, string outputDirectory)
    {
        var manifest = Load(manifestPath);
        var validation = Validate(manifest);
        if (!validation.Passed) throw new InvalidDataException(string.Join(Environment.NewLine, validation.Errors.Select(error => error.Message)));
        new PatchArchiveService().Create(Path.Combine(Path.GetFullPath(outputDirectory), manifest.OutputFileName), manifest.Entries);
    }

    public static PatchManifestValidationResult Validate(PatchManifest manifest, string? archivePath = null)
    {
        var entryValidation = ValidateEntries(manifest.Entries, manifest.Policy);
        var errors = entryValidation.Errors.ToList(); var warnings = entryValidation.Warnings.ToList();
        errors.AddRange(ValidateTarget(manifest.TargetClient));
        if (manifest.TargetClient is not null && manifest.FormatVersion < 4) errors.Add(new("TargetRequirementVersion", "Target-client inheritance requires patch manifest format version 4."));
        if (manifest.Entries.Any(entry => entry.Locale != 0) && manifest.FormatVersion < 5) errors.Add(new("LocaleRequirementVersion", "Locale-aware patch entries require patch manifest format version 5."));
        if (archivePath is not null)
        {
            if (errors.Any(error => error.Code is "InvalidArchivePath" or "InvalidLocale" or "DuplicateArchivePath")) return new(errors, warnings);
            var expected = manifest.Entries.GroupBy(entry => MpqLocale.Identity(entry.ArchivePath, entry.Locale), StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var actual = new PatchArchiveService().ListFiles(archivePath)
                .Where(entry => !entry.IsMetadata).GroupBy(entry => MpqLocale.Identity(entry.ArchivePath, entry.Locale), StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            foreach (var identity in expected.Keys.Except(actual.Keys, StringComparer.Ordinal)) { var entry = expected[identity]; errors.Add(new("MissingArchiveEntry", $"Archive is missing manifest identity '{entry.ArchivePath}' · {MpqLocale.Format(entry.Locale)}.", entry.ArchivePath)); }
            foreach (var identity in actual.Keys.Except(expected.Keys, StringComparer.Ordinal)) { var entry = actual[identity]; errors.Add(new("UnexpectedArchiveEntry", $"Archive contains unexpected identity '{entry.ArchivePath}' · {MpqLocale.Format(entry.Locale)}.", entry.ArchivePath)); }
            foreach (var identity in expected.Keys.Intersect(actual.Keys, StringComparer.Ordinal))
            {
                if (!File.Exists(expected[identity].SourcePath)) continue;
                var sourceSize = new FileInfo(expected[identity].SourcePath).Length;
                if (sourceSize != actual[identity].Size) errors.Add(new("SizeMismatch", $"Archive identity '{expected[identity].ArchivePath}' · {MpqLocale.Format(expected[identity].Locale)} is {actual[identity].Size:N0} bytes; manifest source is {sourceSize:N0} bytes.", expected[identity].ArchivePath));
            }
        }
        return new(errors, warnings);
    }

    public static PatchManifestValidationResult ValidateEntries(IEnumerable<PatchEntry> sourceEntries, PatchManifestPolicy? policy = null)
    {
        var entries = sourceEntries.ToArray(); var errors = new List<PatchManifestValidationIssue>(); var warnings = new List<PatchManifestValidationIssue>();
        if (entries.Length == 0) errors.Add(new("EmptyManifest", "Manifest contains no entries."));
        var normalized = new List<(PatchEntry Entry, string Path)>();
        foreach (var entry in entries)
        {
            string? path = null; var validLocale = true;
            try { path = PatchInputMapper.NormalizeArchivePath(entry.ArchivePath); }
            catch (Exception ex) { errors.Add(new("InvalidArchivePath", ex.Message, entry.ArchivePath)); }
            try { MpqLocale.Validate(entry.Locale); }
            catch (Exception ex) { validLocale = false; errors.Add(new("InvalidLocale", ex.Message, entry.ArchivePath)); }
            if (path is not null && validLocale) normalized.Add((entry, path));
            if (!File.Exists(entry.SourcePath)) errors.Add(new("MissingSource", $"Source file does not exist: {entry.SourcePath}", entry.ArchivePath));
        }
        foreach (var duplicate in normalized.GroupBy(item => MpqLocale.Identity(item.Path, item.Entry.Locale), StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            var item = duplicate.First(); errors.Add(new("DuplicateArchivePath", $"Manifest contains duplicate archive identity '{item.Path}' · {MpqLocale.Format(item.Entry.Locale)}.", item.Path));
        }
        var normalizedPolicy = NormalizePolicy(policy);
        if (normalizedPolicy?.ExpectedEntryCount is { } count && count != entries.Length) errors.Add(new("EntryCountMismatch", $"Manifest policy expects {count:N0} entries but contains {entries.Length:N0}."));
        foreach (var item in normalized)
        {
            if (normalizedPolicy?.AllowedGlobs is { Count: > 0 } allowed && !allowed.Any(glob => GlobMatches(glob, item.Path)))
                errors.Add(new("PathNotAllowed", $"Archive path '{item.Path}' does not match an allowed glob.", item.Path));
            if (normalizedPolicy?.ForbiddenGlobs is { Count: > 0 } forbidden && forbidden.Any(glob => GlobMatches(glob, item.Path)))
                errors.Add(new("PathForbidden", $"Archive path '{item.Path}' matches a forbidden glob.", item.Path));
        }
        foreach (var required in normalizedPolicy?.RequiredGlobs ?? [])
            if (!normalized.Any(item => GlobMatches(required, item.Path))) errors.Add(new("RequiredPathMissing", $"Manifest contains no archive path matching required glob '{required}'.", required));
        return new(errors, warnings);
    }

    public static IReadOnlyList<PatchCompatibilityIssue> GetCompatibilityIssues(IEnumerable<PatchEntry> entries, string? requiredClientExecutableSha256 = null)
    {
        var protectedGlueXml = entries.Where(entry =>
            PatchInputMapper.NormalizeArchivePath(entry.ArchivePath).StartsWith("Interface\\GlueXML\\", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (protectedGlueXml.Length == 0) return [];
        var hash = NormalizeSha256(requiredClientExecutableSha256);
        return hash is null
            ? [new("ProtectedGlueXmlUnbound", $"{protectedGlueXml.Length:N0} protected Interface\\GlueXML file(s) require a GlueXML-compatible build-12340 executable. Stock Wow.exe may report corrupt login interface files. Bind the known-compatible executable SHA-256 to this manifest.")]
            : [new("ProtectedGlueXmlBound", $"{protectedGlueXml.Length:N0} protected Interface\\GlueXML file(s) are bound to required client executable SHA-256 {hash}.")];
    }

    public static string ComputeExecutableSha256(string executablePath)
    {
        using var stream = File.OpenRead(Path.GetFullPath(executablePath));
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string? NormalizeSha256(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) return null;
        var normalized = hash.Replace("-", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character))) throw new InvalidDataException("Client executable SHA-256 must contain exactly 64 hexadecimal characters.");
        return normalized;
    }

    private static PatchTargetClientRequirement? NormalizeTarget(PatchTargetClientRequirement? target)
    {
        if (target is null) return null;
        return target with
        {
            ClientName = target.ClientName.Trim(), ActiveLocale = string.IsNullOrWhiteSpace(target.ActiveLocale) ? null : target.ActiveLocale.Trim(),
            IndexFingerprint = target.IndexFingerprint.Trim().ToUpperInvariant(),
            InheritedAssets = target.InheritedAssets.Select(asset => asset with { ClientPath = PatchInputMapper.NormalizeArchivePath(asset.ClientPath), EffectiveArchive = PatchInputMapper.NormalizeArchivePath(asset.EffectiveArchive), Sha256 = asset.Sha256.Trim().ToUpperInvariant() }).OrderBy(asset => asset.ClientPath, StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    private static IReadOnlyList<PatchManifestValidationIssue> ValidateTarget(PatchTargetClientRequirement? target)
    {
        if (target is null) return [];
        var errors = new List<PatchManifestValidationIssue>();
        if (string.IsNullOrWhiteSpace(target.ClientName)) errors.Add(new("InvalidTargetClient", "Target-client requirement has no client name."));
        if (!IsSha256(target.IndexFingerprint)) errors.Add(new("InvalidTargetFingerprint", "Target-client index fingerprint is not a SHA-256 value."));
        if (target.InheritedAssets.Count == 0) errors.Add(new("EmptyTargetInheritance", "Target-client requirement contains no inherited assets."));
        foreach (var asset in target.InheritedAssets)
        {
            try { _ = PatchInputMapper.NormalizeArchivePath(asset.ClientPath); _ = PatchInputMapper.NormalizeArchivePath(asset.EffectiveArchive); }
            catch (Exception exception) { errors.Add(new("InvalidInheritedPath", exception.Message, asset.ClientPath)); }
            if (!IsSha256(asset.Sha256)) errors.Add(new("InvalidInheritedHash", $"Inherited target path '{asset.ClientPath}' has an invalid SHA-256 value.", asset.ClientPath));
        }
        foreach (var duplicate in target.InheritedAssets.GroupBy(asset => asset.ClientPath, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1)) errors.Add(new("DuplicateInheritedPath", $"Target requirement repeats inherited path '{duplicate.Key}'.", duplicate.Key));
        return errors;
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static PatchManifestPolicy? NormalizePolicy(PatchManifestPolicy? policy)
    {
        if (policy is null) return null;
        if (policy.ExpectedEntryCount < 0) throw new InvalidDataException("Expected manifest entry count cannot be negative.");
        static string[] NormalizeGlobs(IReadOnlyList<string>? globs) => (globs ?? []).Select(glob => glob.Replace('/', '\\').Trim().TrimStart('\\')).Where(glob => glob.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new(NormalizeGlobs(policy.AllowedGlobs), NormalizeGlobs(policy.ForbiddenGlobs), policy.ExpectedEntryCount, NormalizeGlobs(policy.RequiredGlobs));
    }

    private static bool GlobMatches(string glob, string path)
    {
        glob = glob.Replace('/', '\\');
        var pattern = new System.Text.StringBuilder("^");
        for (var index = 0; index < glob.Length; index++)
        {
            if (glob[index] == '*' && index + 1 < glob.Length && glob[index + 1] == '*')
            {
                index++;
                if (index + 1 < glob.Length && glob[index + 1] == '\\') { index++; pattern.Append("(?:.*\\\\)?"); }
                else pattern.Append(".*");
            }
            else if (glob[index] == '*') pattern.Append("[^\\\\]*");
            else if (glob[index] == '?') pattern.Append("[^\\\\]");
            else pattern.Append(Regex.Escape(glob[index].ToString()));
        }
        pattern.Append('$');
        return Regex.IsMatch(path, pattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

}
