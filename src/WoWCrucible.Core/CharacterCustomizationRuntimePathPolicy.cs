using System.Text;

namespace WoWCrucible.Core;

public sealed record CharacterRuntimePathAssessment(string ClientPath, int Utf8Bytes, int MaximumUtf8Bytes)
{
    public bool Safe => Utf8Bytes <= MaximumUtf8Bytes;
}

public static class CharacterCustomizationRuntimePathPolicy
{
    // Confirmed in the Ascension Season 10 character compositor on 2026-08-01.
    // Keep this distinct from MPQ archive-path and Win32 source-path limits.
    public const int MaximumProvenUtf8Bytes = 124;

    public static CharacterRuntimePathAssessment Assess(string clientPath)
    {
        var normalized = PatchInputMapper.NormalizeArchivePath(clientPath);
        return new(normalized, Encoding.UTF8.GetByteCount(normalized), MaximumProvenUtf8Bytes);
    }

    public static void RequireSafe(string clientPath)
    {
        var result = Assess(clientPath);
        if (!result.Safe) throw new PathTooLongException(
            $"Character customization runtime path is {result.Utf8Bytes:N0} UTF-8 bytes; the proven Ascension ceiling is {result.MaximumUtf8Bytes:N0}: {result.ClientPath}. Use a compact project-owned runtime namespace.");
    }
}
