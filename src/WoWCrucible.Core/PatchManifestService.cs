using System.Text.Json;
using System.Security.Cryptography;

namespace WoWCrucible.Core;

public sealed record PatchManifest(int FormatVersion, string Name, string OutputFileName, IReadOnlyList<PatchEntry> Entries, string? RequiredClientExecutableSha256 = null);
public sealed record PatchCompatibilityIssue(string Code, string Message);

public static class PatchManifestService
{
    public static void Save(string manifestPath, string name, string outputFileName, IEnumerable<PatchEntry> entries, string? requiredClientExecutableSha256 = null)
    {
        manifestPath = Path.GetFullPath(manifestPath);
        var directory = Path.GetDirectoryName(manifestPath)!; Directory.CreateDirectory(directory);
        var portable = entries.Select(entry => new PatchEntry(Path.GetRelativePath(directory, Path.GetFullPath(entry.SourcePath)), PatchInputMapper.NormalizeArchivePath(entry.ArchivePath))).ToArray();
        var manifest = new PatchManifest(2, name, Path.GetFileName(outputFileName), portable, NormalizeSha256(requiredClientExecutableSha256));
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static PatchManifest Load(string manifestPath)
    {
        manifestPath = Path.GetFullPath(manifestPath);
        var manifest = JsonSerializer.Deserialize<PatchManifest>(File.ReadAllText(manifestPath)) ?? throw new InvalidDataException("The patch manifest is empty.");
        if (manifest.FormatVersion is not (1 or 2)) throw new InvalidDataException($"Unsupported patch manifest version: {manifest.FormatVersion}");
        var directory = Path.GetDirectoryName(manifestPath)!;
        var resolved = manifest.Entries.Select(entry => new PatchEntry(Path.GetFullPath(Path.Combine(directory, entry.SourcePath)), PatchInputMapper.NormalizeArchivePath(entry.ArchivePath))).ToArray();
        return manifest with { Entries = resolved, RequiredClientExecutableSha256 = NormalizeSha256(manifest.RequiredClientExecutableSha256) };
    }

    public static void Build(string manifestPath, string outputDirectory)
    {
        var manifest = Load(manifestPath);
        new PatchArchiveService().Create(Path.Combine(Path.GetFullPath(outputDirectory), manifest.OutputFileName), manifest.Entries);
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
}
